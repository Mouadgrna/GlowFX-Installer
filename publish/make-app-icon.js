// Draws the installer's app icon: a ✨ (big + two small four-point stars) with a
// holographic violet -> blue -> cyan gradient and a soft glow, on a dark rounded tile.
'use strict';
const fs = require('fs');
const path = require('path');
const { encodePNG, encodeICO } = require('./lib.js');

const STARS = [{ x: 0.43, y: 0.57, r: 0.30 }, { x: 0.74, y: 0.27, r: 0.15 }, { x: 0.27, y: 0.23, r: 0.09 }];
const P = 0.62;
const GRAD = [[0.62, 0.36, 1.0], [0.30, 0.55, 1.0], [0.30, 0.95, 1.0]];

function starField(px, py) {         // >= 1 inside a star, smooth falloff outside (used for the glow)
  let best = 0;
  for (const s of STARS) {
    const v = Math.pow(Math.abs(px - s.x) / s.r, P) + Math.pow(Math.abs(py - s.y) / s.r, P);
    best = Math.max(best, 1 / Math.max(v, 1e-6));
  }
  return best;
}
function grad(t) {
  t = Math.min(1, Math.max(0, t)) * (GRAD.length - 1);
  const i = Math.min(GRAD.length - 2, Math.floor(t)), f = t - i;
  return GRAD[i].map((c, k) => c + (GRAD[i + 1][k] - c) * f);
}
function render(size) {
  const ss = 4, out = new Uint8Array(size * size * 4);
  const rad = 0.22; // tile corner radius
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    let r = 0, g = 0, b = 0, a = 0;
    for (let j = 0; j < ss; j++) for (let i = 0; i < ss; i++) {
      const u = (x + (i + 0.5) / ss) / size, v = (y + (j + 0.5) / ss) / size;
      // rounded tile
      const qx = Math.max(Math.abs(u - 0.5) - (0.5 - rad), 0), qy = Math.max(Math.abs(v - 0.5) - (0.5 - rad), 0);
      if (Math.hypot(qx, qy) > rad) continue;
      let cr = 0.09, cg = 0.09, cb = 0.12;
      const f = starField(u, v);
      const col = grad((u + (1 - v)) / 2);
      if (f >= 1) { cr = col[0]; cg = col[1]; cb = col[2]; const w = 0.35 * Math.min(1, (f - 1) * 1.5); cr += (1 - cr) * w; cg += (1 - cg) * w; cb += (1 - cb) * w; }
      else { const gl = Math.pow(f, 3) * 0.55; cr += col[0] * gl; cg += col[1] * gl; cb += col[2] * gl; }
      r += cr; g += cg; b += cb; a += 1;
    }
    const n = ss * ss, o = (y * size + x) * 4;
    if (a) { out[o] = Math.min(255, r / a * 255); out[o + 1] = Math.min(255, g / a * 255); out[o + 2] = Math.min(255, b / a * 255); }
    out[o + 3] = Math.round(a / n * 255);
  }
  return out;
}

const appDir = path.join(__dirname, '..', 'app');
const images = [16, 24, 32, 48, 64, 128, 256].map((s) => ({ size: s, png: encodePNG(s, s, render(s)) }));
fs.writeFileSync(path.join(appDir, 'app.ico'), encodeICO(images));
fs.writeFileSync(path.join(appDir, 'app-icon-256.png'), images[images.length - 1].png);
console.log('wrote app/app.ico');
