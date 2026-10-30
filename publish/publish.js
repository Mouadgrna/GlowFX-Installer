// Publishes plugins to the GlowFX catalog.
//
//   node publish/publish.js                      build packages + catalog.json (no push)
//   node publish/publish.js --push -m "Notes"    ...and commit + push to GitHub
//   node publish/publish.js --installer          also rebuild the installer .exe
//
// For every entry in publish/plugins.json it packages the plugin folder as a .ccx,
// checks the version, and writes catalog.json, which the installer app reads.
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { execFileSync } = require('child_process');
const { sha256, readJSON } = require('./lib.js');

const repo = path.join(__dirname, '..');
const args = process.argv.slice(2);
const flag = (f) => args.includes(f);
const opt = (f) => { const i = args.indexOf(f); return i >= 0 ? args[i + 1] : null; };

const TEXT = /\.(js|json|html|css|txt|md|svg)$/i;
const SKIP = /(^|[\\/])(\.|node_modules|Thumbs\.db)/;

function listFiles(dir, base = dir) {
  let out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, e.name);
    const rel = path.relative(base, full);
    if (SKIP.test(rel)) continue;
    if (e.isDirectory()) out = out.concat(listFiles(full, base));
    else out.push(rel);
  }
  return out;
}

/** Copies the plugin into a temp folder (stripping UTF-8 BOMs, which Photoshop can choke on) and zips it as .ccx */
function buildCcx(sourceDir, outFile) {
  const stage = fs.mkdtempSync(path.join(os.tmpdir(), 'glowfx-'));
  const files = listFiles(sourceDir);
  for (const rel of files) {
    const dest = path.join(stage, rel);
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    const srcFile = path.join(sourceDir, rel);
    let buf = fs.readFileSync(srcFile);
    if (TEXT.test(rel) && buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf) buf = buf.slice(3);
    fs.writeFileSync(dest, buf);
    const st = fs.statSync(srcFile);
    fs.utimesSync(dest, st.atime, st.mtime); // keep timestamps so unchanged plugins give identical packages
  }
  const zip = outFile + '.zip';
  if (fs.existsSync(zip)) fs.unlinkSync(zip);
  // Windows' built-in tar writes zips with forward-slash paths (Compress-Archive does not)
  execFileSync('tar', ['-a', '-cf', zip, '-C', stage, ...files.map((f) => f.replace(/\\/g, '/'))]);
  fs.renameSync(zip, outFile);
  fs.rmSync(stage, { recursive: true, force: true });
  return files.length;
}

/** Hash of a folder's file names + contents (ignores timestamps), used to spot "changed but version not bumped". */
function contentHash(dir, filter) {
  const h = crypto.createHash('sha256');
  for (const rel of listFiles(dir).filter(filter || (() => true)).sort()) {
    h.update(rel.replace(/\\/g, '/') + '\0');
    h.update(fs.readFileSync(path.join(dir, rel)));
  }
  return h.digest('hex');
}

/** Reads the plugin's changelog.js (newest first) up to the published version; keeps the last 10 versions. */
function readChangelog(src, version) {
  const file = path.join(src, 'changelog.js');
  if (!fs.existsSync(file)) return [];
  delete require.cache[require.resolve(file)];
  const list = require(file);
  if (!Array.isArray(list)) return [];
  const num = (v) => String(v).split('.').map((n) => parseInt(n, 10) || 0);
  const le = (a, b) => { const x = num(a), y = num(b); for (let i = 0; i < 4; i++) { if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) < (y[i] || 0); } return true; };
  return list.filter((e) => e && e.version && Array.isArray(e.notes) && le(e.version, version))
    .slice(0, 10).map((e) => ({ version: String(e.version), notes: e.notes.map(String) }));
}

function main() {
  const config = readJSON(path.join(__dirname, 'plugins.json'));
  const catalogFile = path.join(repo, 'catalog.json');
  const previous = fs.existsSync(catalogFile) ? readJSON(catalogFile) : { plugins: [] };
  const warnings = [];
  const plugins = [];

  for (const entry of config) {
    const src = path.resolve(__dirname, entry.source);
    const manifest = readJSON(path.join(src, 'manifest.json'));
    const host = Array.isArray(manifest.host) ? manifest.host.find((h) => h.app === 'PS') || manifest.host[0] : manifest.host;
    const outDir = path.join(repo, 'plugins', entry.folder);
    fs.mkdirSync(outDir, { recursive: true });
    for (const f of fs.readdirSync(outDir)) if (f.endsWith('.ccx')) fs.unlinkSync(path.join(outDir, f)); // keep only the latest

    const pkgName = manifest.name.replace(/[^A-Za-z0-9]+/g, '') + '-' + manifest.version + '.ccx';
    const n = buildCcx(src, path.join(outDir, pkgName));
    let icon = null;
    if (entry.icon) {
      fs.copyFileSync(path.resolve(__dirname, entry.icon), path.join(outDir, 'icon.png'));
      icon = `plugins/${entry.folder}/icon.png`;
    }
    const pkg = `plugins/${entry.folder}/${pkgName}`;
    const item = {
      id: manifest.id,
      name: manifest.name,
      version: manifest.version,
      description: entry.description || '',
      minPhotoshop: host && host.minVersion,
      package: pkg,
      sha256: sha256(path.join(repo, pkg)),
      size: fs.statSync(path.join(repo, pkg)).size,
      icon,
      sourceHash: contentHash(src),
      changes: readChangelog(src, manifest.version),
    };
    if (!item.changes.some((c) => c.version === item.version)) {
      warnings.push(`${item.name}: no notes for ${item.version}. Add them to changelog.js so users can see what changed.`);
    }
    const old = (previous.plugins || []).find((p) => p.id === item.id);
    if (old && old.version === item.version && old.sourceHash && old.sourceHash !== item.sourceHash) {
      warnings.push(`${item.name}: files changed but the version is still ${item.version}. ` +
        `Bump "version" in its manifest.json, or users won't be offered the update.`);
    }
    plugins.push(item);
    console.log(`  ${item.name} ${item.version}  (${n} files, ${(item.size / 1024).toFixed(0)} KB)` +
      (old ? (old.version !== item.version ? `  update from ${old.version}` : '') : '  new'));
  }

  // installer app
  const exe = path.join(repo, 'installer', 'GlowFX.exe');
  const cs = fs.readFileSync(path.join(repo, 'app', 'GlowFXInstaller.cs'), 'utf8');
  const appVersion = (cs.match(/AppVersion\s*=\s*"([^"]+)"/) || [])[1];
  if (flag('--installer') || !fs.existsSync(exe)) {
    console.log('  building installer…');
    execFileSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'app', 'build.ps1')], { stdio: 'inherit' });
  }
  const appSource = contentHash(path.join(repo, 'app'), (f) => /\.(cs|xaml|ico)$/i.test(f));
  const installer = { version: appVersion, url: 'installer/GlowFX.exe', sha256: sha256(exe), sourceHash: appSource };
  const prevInst = previous.installer;
  if (prevInst && prevInst.version === appVersion && prevInst.sourceHash && prevInst.sourceHash !== appSource) {
    warnings.push(`Installer: the app source changed but AppVersion is still ${appVersion}. Bump AppVersion in app/GlowFXInstaller.cs ` +
      `and publish with --installer so users get the update.`);
  }
  if (prevInst && prevInst.sourceHash && prevInst.sourceHash !== appSource && !flag('--installer')) {
    warnings.push('Installer: the app source changed. Add --installer to rebuild the .exe.');
  }

  const catalog = { schema: 1, name: 'GlowFX', updated: new Date().toISOString(), installer, plugins };
  fs.writeFileSync(catalogFile, JSON.stringify(catalog, null, 2) + '\n');
  console.log('  wrote catalog.json');
  warnings.forEach((w) => console.warn('WARNING: ' + w));

  if (flag('--push')) {
    const msg = opt('-m') || opt('--message') || ('Publish ' + plugins.map((p) => `${p.name} ${p.version}`).join(', '));
    const git = (...a) => execFileSync('git', a, { cwd: repo, stdio: 'inherit' });
    git('add', '-A');
    try { git('commit', '-m', msg); } catch (e) { console.log('  nothing new to commit'); }
    git('push', 'origin', 'HEAD:main');
    console.log('Pushed. Users get it the next time they open GlowFX (GitHub can take a few minutes to refresh).');
  }
}

main();
