// GlowFX Installer - one-click installer for GlowFX Photoshop plugins.
//
// Reads catalog.json (from GitHub, or a local folder for testing), compares it with the
// plugins Photoshop has installed, and installs / updates / uninstalls them through Adobe's
// own Creative Cloud installer (UnifiedPluginInstallerAgent). It can also update itself.
//
// Built with the C# compiler that ships with Windows (.NET Framework 4.8), so it needs
// nothing extra installed. Language level is C# 5: no string interpolation, no ?. etc.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("GlowFX Installer")]
[assembly: AssemblyProduct("GlowFX Installer")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace GlowFX
{
    static class Config
    {
        // Bump this when publishing a new installer build (the publish script reads it).
        public const string AppVersion = "1.0.0";
        public const string DefaultCatalog = "https://raw.githubusercontent.com/Mouadgrna/GlowFX-Installer/main/catalog.json";
    }

    // ------------------------------------------------------------------ data
    class PluginItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void Notify(string name) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name)); }

        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string PackageUrl { get; set; }
        public string Sha256 { get; set; }
        public string IconUrl { get; set; }
        public string MinPhotoshop { get; set; }
        public string InstalledName { get; set; }

        string installedVersion;
        public string InstalledVersion { get { return installedVersion; } set { installedVersion = value; Refresh(); } }

        bool isChecked;
        public bool IsChecked { get { return isChecked; } set { isChecked = value; Notify("IsChecked"); } }

        ImageSource icon;
        public ImageSource Icon { get { return icon; } set { icon = value; Notify("Icon"); } }

        string progressText;
        public string ProgressText
        {
            get { return progressText; }
            set { progressText = value; Notify("ProgressText"); Notify("ProgressVisibility"); }
        }
        public Visibility ProgressVisibility { get { return string.IsNullOrEmpty(progressText) ? Visibility.Collapsed : Visibility.Visible; } }

        public bool IsInstalled { get { return !string.IsNullOrEmpty(installedVersion); } }
        public bool HasUpdate { get { return IsInstalled && Versions.Compare(Version, installedVersion) > 0; } }
        public bool UpToDate { get { return IsInstalled && !HasUpdate; } }

        public string StatusKind { get { return UpToDate ? "installed" : HasUpdate ? "update" : "new"; } }
        public string StatusText { get { return UpToDate ? "Installed" : HasUpdate ? "Update available" : "Not installed"; } }
        public bool CanSelect { get { return !UpToDate; } }
        public Visibility UninstallVisibility { get { return IsInstalled ? Visibility.Visible : Visibility.Collapsed; } }
        public string VersionLine
        {
            get
            {
                var s = "Latest v" + Version;
                if (IsInstalled) s += "   ·   installed v" + installedVersion;
                if (!string.IsNullOrEmpty(MinPhotoshop)) s += "   ·   Photoshop " + MinPhotoshop + "+";
                return s;
            }
        }

        public void Refresh()
        {
            foreach (var p in new[] { "InstalledVersion", "StatusKind", "StatusText", "CanSelect", "UninstallVisibility", "VersionLine" }) Notify(p);
            if (UpToDate && isChecked) IsChecked = false;
        }
    }

    static class Versions
    {
        public static int Compare(string a, string b)
        {
            Version va, vb;
            if (!System.Version.TryParse(Pad(a), out va)) va = new Version(0, 0);
            if (!System.Version.TryParse(Pad(b), out vb)) vb = new Version(0, 0);
            return va.CompareTo(vb);
        }
        static string Pad(string v)
        {
            if (string.IsNullOrEmpty(v)) return "0.0";
            var parts = v.Split('-')[0].Split('.');
            return parts.Length == 1 ? parts[0] + ".0" : string.Join(".", parts.Take(4));
        }
    }

    static class Log
    {
        static readonly string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlowFX", "installer.log");
        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
        public static string FilePath { get { return file; } }
    }

    // ------------------------------------------------------------------ Adobe / Photoshop
    static class Adobe
    {
        static readonly string[] UpiCandidates = {
            @"%CommonProgramFiles%\Adobe\Adobe Desktop Common\RemoteComponents\UPI\UnifiedPluginInstallerAgent\UnifiedPluginInstallerAgent.exe",
            @"%CommonProgramFiles(x86)%\Adobe\Adobe Desktop Common\RemoteComponents\UPI\UnifiedPluginInstallerAgent\UnifiedPluginInstallerAgent.exe",
            @"C:\Program Files\Common Files\Adobe\Adobe Desktop Common\RemoteComponents\UPI\UnifiedPluginInstallerAgent\UnifiedPluginInstallerAgent.exe",
        };

        public static string FindInstaller()
        {
            foreach (var c in UpiCandidates)
            {
                var p = Environment.ExpandEnvironmentVariables(c);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static bool PhotoshopRunning() { return Process.GetProcessesByName("Photoshop").Length > 0; }

        /// <summary>pluginId -> (name, version) for plugins Photoshop has enabled/disabled (ignores ones being uninstalled).</summary>
        public static Dictionary<string, KeyValuePair<string, string>> InstalledPlugins()
        {
            var result = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Adobe\UXP\PluginsInfo\v1\PS.json");
                if (!File.Exists(path)) return result;
                var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                var list = root != null && root.ContainsKey("plugins") ? root["plugins"] as object[] : null;
                if (list == null) return result;
                foreach (var o in list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var id = Json.Str(d, "pluginId");
                    var status = Json.Str(d, "status");
                    if (string.IsNullOrEmpty(id) || status == "uninstalling") continue;
                    var ver = Json.Str(d, "versionString");
                    KeyValuePair<string, string> prev;
                    if (!result.TryGetValue(id, out prev) || Versions.Compare(ver, prev.Value) > 0)
                        result[id] = new KeyValuePair<string, string>(Json.Str(d, "name"), ver);
                }
            }
            catch (Exception e) { Log.Write("Reading PS.json failed: " + e.Message); }
            return result;
        }

        /// <summary>Runs Adobe's installer agent; returns (success, output).</summary>
        public static Task<KeyValuePair<bool, string>> RunAgent(string exe, string args)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(5 * 60 * 1000)) { try { p.Kill(); } catch { } return new KeyValuePair<bool, string>(false, "Timed out"); }
                    var output = outTask.Result + errTask.Result;
                    Log.Write("UPI " + args + " -> exit " + p.ExitCode + ": " + output.Trim().Replace(Environment.NewLine, " | "));
                    var ok = p.ExitCode == 0 && output.IndexOf("Successful", StringComparison.OrdinalIgnoreCase) >= 0;
                    return new KeyValuePair<bool, string>(ok, output.Trim());
                }
            });
        }
    }

    static class Json
    {
        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }
    }

    // ------------------------------------------------------------------ catalog + downloads
    class Catalog
    {
        public string Location;          // URL or local path of catalog.json
        public List<PluginItem> Plugins = new List<PluginItem>();
        public string InstallerVersion, InstallerUrl, InstallerSha;

        static readonly HttpClient http = CreateHttp();
        static HttpClient CreateHttp()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("GlowFX-Installer/" + Config.AppVersion);
            return c;
        }

        public static bool IsWeb(string loc) { return loc.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || loc.StartsWith("https://", StringComparison.OrdinalIgnoreCase); }

        /// <summary>Resolves a path from the catalog relative to where the catalog lives.</summary>
        public string Resolve(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            if (IsWeb(rel) || Path.IsPathRooted(rel)) return rel;
            if (IsWeb(Location)) return new Uri(new Uri(Location), rel).ToString();
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Location)), rel.Replace('/', '\\')));
        }

        public static async Task<Catalog> Load(string location)
        {
            string text;
            if (IsWeb(location))
            {
                // cache-buster so a freshly pushed update shows up straight away
                var url = location + (location.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
                text = await http.GetStringAsync(url);
            }
            else text = File.ReadAllText(location);

            var root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            if (root == null) throw new Exception("The catalog file is not valid.");
            var cat = new Catalog { Location = location };
            var inst = Json.Obj(root, "installer");
            if (inst != null)
            {
                cat.InstallerVersion = Json.Str(inst, "version");
                cat.InstallerUrl = cat.Resolve(Json.Str(inst, "url"));
                cat.InstallerSha = Json.Str(inst, "sha256");
            }
            var list = root.ContainsKey("plugins") ? root["plugins"] as object[] : null;
            if (list != null)
            {
                foreach (var o in list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || string.IsNullOrEmpty(Json.Str(d, "id"))) continue;
                    cat.Plugins.Add(new PluginItem
                    {
                        Id = Json.Str(d, "id"),
                        Name = Json.Str(d, "name") ?? Json.Str(d, "id"),
                        Description = Json.Str(d, "description") ?? "",
                        Version = Json.Str(d, "version") ?? "0.0.0",
                        PackageUrl = cat.Resolve(Json.Str(d, "package")),
                        Sha256 = (Json.Str(d, "sha256") ?? "").ToLowerInvariant(),
                        IconUrl = cat.Resolve(Json.Str(d, "icon")),
                        MinPhotoshop = Json.Str(d, "minPhotoshop"),
                    });
                }
            }
            return cat;
        }

        /// <summary>Downloads (or copies) a file to the temp folder, reporting 0..100, and checks its SHA-256.</summary>
        public static async Task<string> Fetch(string source, string fileName, string expectedSha, Action<int> progress)
        {
            var dir = Path.Combine(Path.GetTempPath(), "GlowFX");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, fileName);
            if (IsWeb(source))
            {
                using (var resp = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? -1L;
                    using (var input = await resp.Content.ReadAsStreamAsync())
                    using (var output = File.Create(dest))
                    {
                        var buf = new byte[81920];
                        long done = 0; int n;
                        while ((n = await input.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await output.WriteAsync(buf, 0, n);
                            done += n;
                            if (total > 0) progress((int)(done * 100 / total));
                        }
                    }
                }
            }
            else
            {
                File.Copy(source, dest, true);
                progress(100);
            }
            if (!string.IsNullOrEmpty(expectedSha))
            {
                var actual = Sha256Of(dest);
                if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(dest); } catch { }
                    throw new Exception("The download was corrupted (checksum mismatch). Please try again.");
                }
            }
            return dest;
        }

        public static string Sha256Of(string file)
        {
            using (var s = File.OpenRead(file))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        public static ImageSource LoadIcon(string src)
        {
            if (string.IsNullOrEmpty(src)) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(src, UriKind.Absolute);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                img.EndInit();
                return img;
            }
            catch (Exception e) { Log.Write("Icon failed " + src + ": " + e.Message); return null; }
        }
    }

    // ------------------------------------------------------------------ window
    class MainController
    {
        readonly Window window;
        readonly ItemsControl list;
        readonly Button installButton, refreshButton, updateAppButton;
        readonly TextBlock statusText, emptyText, bannerText;
        readonly ProgressBar progress;
        readonly Border banner;
        readonly ObservableCollection<PluginItem> items = new ObservableCollection<PluginItem>();
        readonly string catalogLocation;
        Catalog catalog;
        bool busy;

        // developer test hooks: --screenshot <png> [--auto <pluginId>] (see Program.Main)
        public string ScreenshotPath;
        public string AutoInstallId;

        public MainController(Window w, string catalogLocation)
        {
            window = w;
            this.catalogLocation = catalogLocation;
            list = (ItemsControl)w.FindName("PluginList");
            installButton = (Button)w.FindName("InstallButton");
            refreshButton = (Button)w.FindName("RefreshButton");
            updateAppButton = (Button)w.FindName("UpdateAppButton");
            statusText = (TextBlock)w.FindName("StatusText");
            emptyText = (TextBlock)w.FindName("EmptyText");
            bannerText = (TextBlock)w.FindName("UpdateBannerText");
            progress = (ProgressBar)w.FindName("Progress");
            banner = (Border)w.FindName("UpdateBanner");

            list.ItemsSource = items;
            installButton.Click += async (s, e) => await InstallSelected();
            refreshButton.Click += async (s, e) => await Reload();
            updateAppButton.Click += async (s, e) => await UpdateSelf();
            // the Uninstall buttons live inside the item template, so catch their clicks here
            list.AddHandler(Button.ClickEvent, new RoutedEventHandler(async (s, e) =>
            {
                var b = e.OriginalSource as Button;
                if (b == null || (b.Tag as string) != "uninstall") return;
                var item = b.DataContext as PluginItem;
                if (item != null) await Uninstall(item);
            }));
            window.Loaded += async (s, e) =>
            {
                await Reload();
                if (ScreenshotPath == null) return;
                if (AutoInstallId == "__installer__")
                {
                    await Task.Delay(1500);           // let the list and banner render
                    SaveScreenshot(ScreenshotPath);   // shows the update banner
                    await UpdateSelf();               // swaps the .exe and relaunches
                    return;
                }
                if (AutoInstallId != null)
                {
                    foreach (var i in items) i.IsChecked = string.Equals(i.Id, AutoInstallId, StringComparison.OrdinalIgnoreCase);
                    await InstallSelected();
                }
                await Task.Delay(1500); // let icons load
                SaveScreenshot(ScreenshotPath);
                Application.Current.Shutdown();
            };
        }

        void SaveScreenshot(string file)
        {
            var content = (FrameworkElement)window.Content;
            int w = (int)window.ActualWidth, h = (int)window.ActualHeight;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
                var p = content.TranslatePoint(new Point(0, 0), window);
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(p, new Size(content.ActualWidth, content.ActualHeight)));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        void SetBusy(bool on)
        {
            busy = on;
            refreshButton.IsEnabled = !on;
            updateAppButton.IsEnabled = !on;
            list.IsEnabled = !on;
            progress.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            if (!on) progress.Value = 0;
            UpdateInstallButton();
        }

        void UpdateInstallButton()
        {
            var n = items.Count(i => i.IsChecked);
            var updates = items.Count(i => i.IsChecked && i.HasUpdate);
            installButton.Content = n == 0 ? "Install selected" : (updates == n ? "Update " : "Install ") + n + (n == 1 ? " plugin" : " plugins");
            installButton.IsEnabled = !busy && n > 0;
        }

        void Status(string text) { statusText.Text = text; }

        async Task Reload()
        {
            if (busy) return;
            SetBusy(true);
            Status("Checking for plugins…");
            try
            {
                catalog = await Catalog.Load(catalogLocation);
                var installed = Adobe.InstalledPlugins();
                foreach (var old in items) old.PropertyChanged -= OnItemChanged;
                items.Clear();
                foreach (var p in catalog.Plugins)
                {
                    KeyValuePair<string, string> inst;
                    if (installed.TryGetValue(p.Id, out inst)) { p.InstalledName = inst.Key; p.InstalledVersion = inst.Value; }
                    p.Icon = Catalog.LoadIcon(p.IconUrl);
                    p.IsChecked = p.HasUpdate;          // updates come pre-selected
                    p.PropertyChanged += OnItemChanged;
                    items.Add(p);
                }
                emptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                emptyText.Text = "No plugins in the catalog yet.";
                var updates = items.Count(i => i.HasUpdate);
                Status(updates > 0 ? updates + (updates == 1 ? " update available." : " updates available.") : "Tick the plugins you want, then click Install.");

                if (!string.IsNullOrEmpty(catalog.InstallerVersion) && Versions.Compare(catalog.InstallerVersion, Config.AppVersion) > 0)
                {
                    bannerText.Text = "A new version of GlowFX Installer is available (v" + catalog.InstallerVersion + ").";
                    banner.Visibility = Visibility.Visible;
                }
                else banner.Visibility = Visibility.Collapsed;

                if (Adobe.FindInstaller() == null)
                    Status("Creative Cloud desktop app not found. Install it from adobe.com, then click Refresh.");
            }
            catch (Exception e)
            {
                Log.Write("Catalog load failed: " + e);
                items.Clear();
                emptyText.Visibility = Visibility.Visible;
                emptyText.Text = "Couldn't load the plugin list.\nCheck your internet connection, then click Refresh.\n\n" + e.Message;
                Status("");
            }
            finally { SetBusy(false); }
        }

        void OnItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsChecked") UpdateInstallButton();
        }

        /// <summary>Asks the user to close Photoshop. Returns false if they cancel.</summary>
        bool HandlePhotoshopOpen(string action)
        {
            if (ScreenshotPath != null) return true; // unattended test run: continue anyway
            while (Adobe.PhotoshopRunning())
            {
                var r = MessageBox.Show(window,
                    "Photoshop is open.\n\nSave your work and close Photoshop so the " + action + " finishes cleanly, then click OK.\n\n" +
                    "Click No to continue anyway (the old version is cleaned up when you restart Photoshop).",
                    "GlowFX Installer", MessageBoxButton.YesNoCancel, MessageBoxImage.Information, MessageBoxResult.Yes);
                if (r == MessageBoxResult.Cancel) return false;
                if (r == MessageBoxResult.No) return true;
                // Yes = "I closed it": loop and check again
            }
            return true;
        }

        async Task InstallSelected()
        {
            if (busy) return;
            var todo = items.Where(i => i.IsChecked).ToList();
            if (todo.Count == 0) return;
            var agent = Adobe.FindInstaller();
            if (agent == null)
            {
                MessageBox.Show(window, "The Creative Cloud desktop app is needed to install Photoshop plugins.\nInstall it from adobe.com and try again.",
                    "GlowFX Installer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!HandlePhotoshopOpen(todo.Any(t => t.IsInstalled) ? "update" : "install")) return;

            SetBusy(true);
            int ok = 0;
            var failures = new List<string>();
            for (int k = 0; k < todo.Count; k++)
            {
                var item = todo[k];
                var baseProgress = k * 100.0 / todo.Count;
                var span = 100.0 / todo.Count;
                try
                {
                    Status("Downloading " + item.Name + "…");
                    item.ProgressText = "Downloading…";
                    var file = await Catalog.Fetch(item.PackageUrl, Safe(item.Id) + "-" + item.Version + ".ccx", item.Sha256,
                        pct => { progress.Value = baseProgress + span * 0.6 * pct / 100; item.ProgressText = "Downloading… " + pct + "%"; });

                    if (item.IsInstalled)
                    {
                        // remove the old version first, otherwise Creative Cloud can keep both
                        item.ProgressText = "Removing v" + item.InstalledVersion + "…";
                        progress.Value = baseProgress + span * 0.7;
                        await Adobe.RunAgent(agent, "/remove \"" + (item.InstalledName ?? item.Name) + "\"");
                    }

                    item.ProgressText = "Installing into Photoshop…";
                    Status("Installing " + item.Name + "…");
                    progress.Value = baseProgress + span * 0.8;
                    var res = await Adobe.RunAgent(agent, "/install \"" + file + "\"");
                    if (!res.Key) throw new Exception(FirstLine(res.Value));

                    item.InstalledName = item.Name;
                    item.InstalledVersion = item.Version;
                    item.IsChecked = false;
                    item.ProgressText = null;
                    progress.Value = baseProgress + span;
                    ok++;
                    try { File.Delete(file); } catch { }
                }
                catch (Exception e)
                {
                    Log.Write("Install failed for " + item.Id + ": " + e);
                    item.ProgressText = "Failed: " + e.Message;
                    failures.Add(item.Name + ": " + e.Message);
                }
            }
            SetBusy(false);

            // re-read what Photoshop actually has, so the list is the truth
            var installed = Adobe.InstalledPlugins();
            foreach (var i in items)
            {
                KeyValuePair<string, string> inst;
                if (installed.TryGetValue(i.Id, out inst)) { i.InstalledName = inst.Key; i.InstalledVersion = inst.Value; }
            }

            if (failures.Count == 0)
            {
                Status("Done! " + ok + (ok == 1 ? " plugin" : " plugins") + " installed. Open Photoshop > Plugins menu.");
                if (Adobe.PhotoshopRunning() && ScreenshotPath == null)
                    MessageBox.Show(window, "Installed. Restart Photoshop to load the new version.", "GlowFX Installer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Status(ok + " installed, " + failures.Count + " failed.");
                MessageBox.Show(window, "Some plugins could not be installed:\n\n" + string.Join("\n", failures) + "\n\nDetails are in:\n" + Log.FilePath,
                    "GlowFX Installer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        async Task Uninstall(PluginItem item)
        {
            if (busy) return;
            var agent = Adobe.FindInstaller();
            if (agent == null) return;
            if (MessageBox.Show(window, "Uninstall " + item.Name + " from Photoshop?", "GlowFX Installer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (!HandlePhotoshopOpen("uninstall")) return;
            SetBusy(true);
            item.ProgressText = "Uninstalling…";
            var res = await Adobe.RunAgent(agent, "/remove \"" + (item.InstalledName ?? item.Name) + "\"");
            item.ProgressText = null;
            SetBusy(false);
            if (res.Key) { item.InstalledVersion = null; Status(item.Name + " was uninstalled."); }
            else
            {
                Status("Uninstall failed.");
                MessageBox.Show(window, "Could not uninstall " + item.Name + ":\n" + FirstLine(res.Value), "GlowFX Installer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>Downloads the new installer, then a tiny script swaps the .exe once this one has closed and relaunches it.</summary>
        async Task UpdateSelf()
        {
            if (busy || catalog == null || string.IsNullOrEmpty(catalog.InstallerUrl)) return;
            SetBusy(true);
            try
            {
                Status("Downloading the new installer…");
                var file = await Catalog.Fetch(catalog.InstallerUrl, "GlowFXInstaller-" + catalog.InstallerVersion + ".exe", catalog.InstallerSha,
                    pct => progress.Value = pct);
                var current = Assembly.GetEntryAssembly().Location;
                var script = Path.Combine(Path.GetTempPath(), "GlowFX", "update-installer.cmd");
                var pid = Process.GetCurrentProcess().Id;
                File.WriteAllText(script,
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    "tasklist /FI \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                    "copy /y \"" + file + "\" \"" + current + "\" >nul\r\n" +
                    "start \"\" \"" + current + "\"\r\n" +
                    "del \"%~f0\"\r\n", Encoding.ASCII);
                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { CreateNoWindow = true, UseShellExecute = false });
                Log.Write("Self-update to " + catalog.InstallerVersion + " scheduled");
                Application.Current.Shutdown();
            }
            catch (Exception e)
            {
                Log.Write("Self-update failed: " + e);
                SetBusy(false);
                Status("Installer update failed: " + e.Message);
            }
        }

        static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }
        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Unknown error";
            var lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[lines.Length - 1] : s;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // --catalog <path or url> lets you test against a local folder before pushing
            // --screenshot / --auto are developer test hooks: open off-screen, optionally install, save a PNG, exit
            var catalog = Config.DefaultCatalog;
            string shot = null, auto = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--catalog") catalog = args[i + 1];
                if (args[i] == "--screenshot") shot = Path.GetFullPath(args[i + 1]);
                if (args[i] == "--auto") auto = args[i + 1];
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("Unhandled: " + e.Exception);
                MessageBox.Show("Something went wrong:\n" + e.Exception.Message + "\n\nDetails: " + Log.FilePath, "GlowFX Installer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };

            Window window;
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("MainWindow.xaml"))
                window = (Window)XamlReader.Load(s);
            using (var ico = asm.GetManifestResourceStream("app.ico"))
                if (ico != null) window.Icon = BitmapFrame.Create(ico, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            window.Title = "GlowFX Installer  v" + Config.AppVersion;

            var controller = new MainController(window, catalog);
            if (shot != null)
            {
                controller.ScreenshotPath = shot;
                controller.AutoInstallId = auto;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -4000; window.Top = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
            }
            Log.Write("Started v" + Config.AppVersion + " catalog=" + catalog);
            app.Run(window);
        }
    }
}
