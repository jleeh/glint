using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glint.Core;
using Glint.Devices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Glint.UI;

/// <summary>
/// The dashboard: a WebView2 page (ui/index.html) talking to the service over postMessage.
/// It only exists while open; closing disposes the WebView2 browser processes entirely.
/// </summary>
public sealed class WebDashboard : Form
{
    readonly GlintService _svc;
    readonly Action? _openMonitor;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(12, 13, 18) };
    readonly System.Windows.Forms.Timer _state = new() { Interval = 1000 }, _preview = new() { Interval = 50 };
    bool _ready;
    DateTime _lastCatalog;
    public bool Ready => _ready;
    public Task<string> EvalAsync(string js) => _web.CoreWebView2.ExecuteScriptAsync(js);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public WebDashboard(GlintService svc, Action? openMonitor = null)
    {
        _svc = svc;
        _openMonitor = openMonitor;
        Text = "Glint";
        Size = new Size(1320, 860);
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 13, 18);
        Icon = TrayApp.AppIcon;
        Controls.Add(_web);
        _state.Tick += (_, _) =>
        {
            Push(StateMessage());
            // tuning status only while the Tuning page is open (it asks every few seconds; nothing is read otherwise)
            if (Environment.TickCount64 < _tuningUntil) Push(TuningMessage());
            if (_svc.Rig.CatalogWanted && _svc.Rig.Catalog is { } cat && cat.Time != _lastCatalog) { _lastCatalog = cat.Time; Push(new { type = "sensors", catalog = cat }); }
        };
        _preview.Tick += (_, _) => Push(PreviewMessage());
        Plugins.PluginHost.EffectsChanged += OnPluginsChanged;
        FormClosed += (_, _) => { _svc.Lighting.StopTestPattern(); Plugins.PluginHost.EffectsChanged -= OnPluginsChanged; _state.Dispose(); _preview.Dispose(); _web.Dispose(); };
        _ = InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1; // dark title bar + Mica backdrop (Win11)
        DwmSetWindowAttribute(Handle, 20, ref on, 4);
        int mica = 2; DwmSetWindowAttribute(Handle, 38, ref mica, 4);
    }

    /// <summary>The only origin the dashboard is served from; messages and navigation from anywhere else are refused.</summary>
    public const string Origin = "https://glint.local/";
    public static bool IsLocal(string? uri) => uri != null && uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// WebView2 browser data, in the usual per-user place. Even when Glint runs elevated, WebView2 starts its browser
    /// processes de-elevated (as the normal user), so they can't use an admin-only folder, and anything planted in this
    /// folder only reaches a normal-privilege process: no elevation to gain. The dashboard's real boundary is that it
    /// only loads Glint's own pages and only accepts messages from that origin (see <see cref="Origin"/>).
    /// </summary>
    public static string DataFolder(bool elevated) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glint", "WebView2");

    /// <summary>WEBVIEW2_* variables (browser folder, extra arguments, data folder...) override what we pass and come from
    /// the user's environment, which a normal-privilege program can set: drop them before starting the browser.</summary>
    public static void ClearWebView2Overrides()
    {
        foreach (var k in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("COREWEBVIEW2_", StringComparison.OrdinalIgnoreCase)).ToList())
            Environment.SetEnvironmentVariable(k, null);
    }

    async Task InitAsync()
    {
        ClearWebView2Overrides();
        var data = DataFolder(_svc.Rig.Elevated);
        var env = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection"));
        await _web.EnsureCoreWebView2Async(env);
        var cw = _web.CoreWebView2;
        cw.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.AreDefaultContextMenusEnabled = false;
        cw.Settings.IsZoomControlEnabled = false;
        cw.Settings.AreHostObjectsAllowed = false;
        cw.Settings.IsGeneralAutofillEnabled = false;
        cw.Settings.IsPasswordAutosaveEnabled = false;
        var ui = Path.Combine(AppContext.BaseDirectory, "web");
        cw.SetVirtualHostNameToFolderMapping("glint.local", ui, CoreWebView2HostResourceAccessKind.DenyCors);
        // the page is the elevated process's remote control: it may only ever be our own local page
        cw.NavigationStarting += (_, e) => { if (!IsLocal(e.Uri)) e.Cancel = true; };
        cw.FrameNavigationStarting += (_, e) => { if (!IsLocal(e.Uri)) e.Cancel = true; };
        cw.NewWindowRequested += (_, e) => { e.Handled = true; Program.OpenUrl(e.Uri); }; // only Glint's own pages open (in the real browser)
        cw.DownloadStarting += (_, e) => e.Cancel = true;
        cw.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        cw.WebMessageReceived += (_, e) => { if (IsLocal(e.Source)) OnMessage(e.WebMessageAsJson); };
        cw.Navigate(Origin + "index.html");
    }

    static string Sys(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);
    static string Sys32(string exe) => Path.Combine(Environment.SystemDirectory, exe);
    /// <summary>Show a folder: absolute explorer path (never a planted explorer.exe from the search path), quoted argument.</summary>
    public static void OpenFolder(string dir) => Process.Start(new ProcessStartInfo(Sys("explorer.exe"), $"\"{dir}\"") { UseShellExecute = false });
    /// <summary>Notepad by absolute path, for a plugin file / the config.</summary>
    public static void OpenInNotepad(string file) => Process.Start(new ProcessStartInfo(Sys32("notepad.exe"), $"\"{file}\"") { UseShellExecute = false });

    /// <summary>After an update check: push the fresh answer (the dashboard's "Check now" waits for this).</summary>
    async Task RefreshUpdate(Task<UpdateInfo> check)
    {
        UpdateInfo info;
        try { info = await check; } catch { info = _svc.Updates.Info; }
        try { if (!IsDisposed) BeginInvoke(() => Push(new { type = "updateInfo", update = info })); } catch { }
    }

    void Push(object msg)
    {
        if (!_ready || _web.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg, GlintConfig.Json)); } catch { }
    }

    long _tuningUntil;
    object TuningMessage()
    {
        try
        {
            return new { type = "tuning", status = _svc.Tuning.Status(), ai = _svc.Ai.Status(), cpu = _svc.Cpu.Status(),
                         crashes = new { watching = _svc.Crashes.Running, events = _svc.Crashes.Records.TakeLast(20).Reverse(), cpuCoresToLoosen = _svc.Crashes.CoresToLoosen() } };
        }
        catch (Exception e) { return new { type = "tuning", error = e.Message }; }
    }

    static object? Open(Action a) { a(); return null; }

    /// <summary>One page of the saved test history for the Tuning page's pagination.</summary>
    object HistoryMessage(int offset, int limit)
    {
        var h = _svc.Tuning.History(offset, limit);
        return new { type = "tuningHistory", history = h };
    }

    void OnMessage(string json)
    {
        JsonNode? m;
        try { m = JsonNode.Parse(json); } catch { return; }
        switch ((string?)m?["type"])
        {
            case "ready":
                _ready = true;
                Push(InitMessage());
                Push(StateMessage());
                _state.Start();
                RunSetupCheck(); // once per dashboard open, for the sidebar badge and the Overview banner
                _ = RefreshUpdate(_svc.Updates.CheckIfStale(UpdateChecker.StaleOnOpen));
                break;
            case "checkUpdate":
                _ = RefreshUpdate(_svc.Updates.CheckRequested());
                break;
            case "setupCheck":
                RunSetupCheck();
                break;
            case "setupFix":
                {
                    var arg = (string?)m!["arg"];
                    switch ((string?)m["fix"])
                    {
                        case "openUrl": Program.OpenUrl(arg ?? ""); break;
                        case "startupApps": Program.OpenStartupApps(); break;
                        case "restartAdmin": Program.RestartElevated(); break;
                        case "restart": Program.Restart(); break;
                        case "installUpdate": _svc.Updater.Start(); break;
                    }
                    break;
                }
            case "openUrl":
                Program.OpenUrl((string?)m!["url"] ?? "");
                break;
            case "installUpdate":
                // downloads, verifies (SHA256SUMS + build attestation) and runs only a newer release of Glint's own repository
                _svc.Updater.Start();
                Push(new { type = "updater", install = _svc.Updater.Status });
                break;
            case "testPattern":
                {
                    var zone = (string?)m!["zone"] ?? "";
                    if ((bool?)m["on"] == true) _svc.Lighting.TestPattern(zone, (int?)m["count"] ?? 0);
                    else if (zone == "" || _svc.Lighting.TestPatternZone == zone) _svc.Lighting.StopTestPattern();
                    break;
                }
            case "config":
                try
                {
                    var cfg = m!["config"].Deserialize<GlintConfig>(GlintConfig.Json);
                    if (cfg == null) break;
                    // same validator as the control API; problems the saved config already had don't block the user
                    var v = ConfigValidator.Check(cfg, new ValidationContext { Sensors = _svc.Sensors.Keys.ToList(), Previous = _svc.Config });
                    if (!v.Ok)
                    {
                        Push(new { type = "toast", kind = "error", text = "Not saved: " + string.Join("; ", v.Errors.Take(3)) });
                        Push(InitMessage()); // put the dashboard back in sync with what is really applied
                        break;
                    }
                    _svc.Apply(cfg);
                }
                catch (Exception e) { Push(new { type = "toast", kind = "error", text = "Couldn't apply: " + e.Message }); }
                break;
            case "sensorsWanted":
                _svc.Rig.WantCatalog();
                if ((bool?)m!["reset"] == true) _svc.Rig.ResetCatalogMinMax();
                if (_svc.Rig.Catalog is { } now) { _lastCatalog = now.Time; Push(new { type = "sensors", catalog = now }); }
                break;
            case "tuning":
                {
                    _tuningUntil = Environment.TickCount64 + 6000;
                    var op = (string?)m!["op"] ?? "status";
                    var a = m["args"] as JsonObject ?? new JsonObject();
                    int I(string k) => a[k] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (int)Math.Round(d) : throw new ArgumentException(k + " must be a number");
                    try
                    {
                        _ = op switch
                        {
                            "status" => null,
                            "aiStart" => _svc.Config.Tuning?.Enabled == true ? _svc.Ai.Start((string?)a["goal"] ?? "gpu", (int?)(double?)a["budgetMinutes"] ?? 45) : throw new InvalidOperationException("turn tuning on first"),
                            "aiStop" => _svc.Ai.Stop(),
                            "history" => Open(() => Push(HistoryMessage(Math.Max(0, (int?)(double?)a["offset"] ?? 0), Math.Clamp((int?)(double?)a["limit"] ?? 15, 1, 100)))),
                            "monitor" => Open(() => _openMonitor?.Invoke()),
                            "stop" => _svc.Tuning.Stop(),
                            "stock" => _svc.Tuning.Stock(),
                            "applySaved" => _svc.Tuning.State.Saved is { } sv ? _svc.Tuning.Apply(sv.Core, sv.MemAb) : throw new InvalidOperationException("no saved result yet"),
                            "test" => _svc.Tuning.StartGpuTest(I("core"), I("memAb"), (double?)a["seconds"] ?? 60, (bool?)a["light"] ?? false),
                            "clearRevert" => _svc.Tuning.ClearRevert(),
                            "cpuFinish" => _svc.Cpu.Finish((bool?)a["force"] ?? false),
                            "cpuStop" => _svc.Cpu.Stop(),
                            _ => throw new InvalidOperationException("unknown tuning action " + op),
                        };
                    }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Push(new { type = "toast", kind = "error", text = e.Message }); }
                    Push(TuningMessage());
                    break;
                }
            case "sensorsReset":
                _svc.Rig.ResetCatalogMinMax();
                break;
            case "preview":
                _preview.Enabled = (bool?)m!["on"] == true;
                break;
            case "restart":
                // LED counts / devices changed: relaunch (inherits elevation), this instance exits.
                Program.Restart();
                break;
            case "openConfig":
                OpenFolder(GlintConfig.Dir);
                break;
            case "openPlugins":
                UserFs.CreateDirectory(Plugins.PluginHost.UserDir);
                OpenFolder(Plugins.PluginHost.UserDir);
                break;
            case "reloadPlugins":
                Plugins.PluginHost.ReloadSoon();
                break;
            case "identify":
                _svc.Lighting.Identify((string?)m!["target"] ?? "", 4);
                break;
            case "autoLayout":
                {
                    var c = JsonSerializer.Deserialize<GlintConfig>(JsonSerializer.Serialize(_svc.Config, GlintConfig.Json), GlintConfig.Json)!;
                    c.Lighting.Layout = Core.Layout.AutoArrange(_svc.Lighting.LayoutZones);
                    _svc.Apply(c);
                    Push(new { type = "layout", placements = c.Lighting.Layout, config = PageConfig(c) });
                    break;
                }
            case "setPluginEnabled":
                {
                    var file = Path.GetFileName((string?)m!["file"] ?? "");
                    bool on = (bool?)m["enabled"] ?? true;
                    var c = JsonSerializer.Deserialize<GlintConfig>(JsonSerializer.Serialize(_svc.Config, GlintConfig.Json), GlintConfig.Json)!;
                    c.DisabledPlugins.RemoveAll(x => x.Equals(file, StringComparison.OrdinalIgnoreCase));
                    if (!on) c.DisabledPlugins.Add(file);
                    _svc.Apply(c);
                    Task.Run(Plugins.PluginHost.ReloadNow);
                    break;
                }
            case "removePlugin":
                try
                {
                    var f = Plugins.PluginHost.FindFile((string?)m!["file"] ?? "") ?? throw new FileNotFoundException("plugin not found");
                    if (Plugins.PluginHost.IsBundled(f)) throw new InvalidOperationException("bundled plugins can only be switched off");
                    UserFs.Delete(f);
                    Task.Run(Plugins.PluginHost.ReloadNow);
                }
                catch (Exception e) { Push(new { type = "toast", kind = "error", text = e.Message }); }
                break;
            case "openPluginFile":
                {
                    var f = Plugins.PluginHost.FindFile((string?)m!["file"] ?? "");
                    if (f != null) OpenInNotepad(f);
                    break;
                }
            case "newPlugin":
                {
                    bool device = (string?)m!["kind"] == "device";
                    var tpl = Path.Combine(AppContext.BaseDirectory, "plugin-templates", device ? "device-template.js" : "effect-template.js");
                    string baseName = device ? "my-device" : "my-effect", file;
                    int n = 1;
                    do file = Path.Combine(Plugins.PluginHost.UserDir, $"{baseName}{(n > 1 ? "-" + n : "")}.js"); while (UserFs.Exists(file) && ++n < 100);
                    var code = File.ReadAllText(tpl);
                    if (!device) code = code.Replace("name: 'My effect'", $"name: '{Path.GetFileNameWithoutExtension(file)}'");
                    UserFs.Run(() => { Directory.CreateDirectory(Plugins.PluginHost.UserDir); File.WriteAllText(file, code); });
                    OpenInNotepad(file);
                    Task.Run(Plugins.PluginHost.ReloadNow);
                    break;
                }
            case "pluginDocs":
                Push(new { type = "docs", text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plugin-templates", "PLUGINS.md")) });
                break;
            case "approvePlugin":
                try
                {
                    var f = Plugins.PluginHost.FindFile((string?)m!["file"] ?? "") ?? throw new FileNotFoundException("plugin not found");
                    Plugins.PluginTrust.Approve(f);
                    Push(new { type = "toast", text = $"Approved {Path.GetFileName(f)}. Restart Glint to load it." });
                    Plugins.PluginHost.ReloadSoon();
                }
                catch (Exception e) { Push(new { type = "toast", kind = "error", text = "Couldn't approve: " + e.Message }); }
                break;
        }
    }

    int _setupRunning;
    /// <summary>Setup checks read the process list, registry and Task Scheduler: off the UI thread, one at a time.</summary>
    void RunSetupCheck()
    {
        if (Interlocked.Exchange(ref _setupRunning, 1) == 1) return;
        Task.Run(() =>
        {
            object msg;
            try { msg = new { type = "setup", report = SetupCheck.Run(_svc) }; }
            catch (Exception e) { msg = new { type = "toast", kind = "error", text = "Setup check failed: " + e.Message }; }
            finally { _setupRunning = 0; }
            try { if (!IsDisposed) BeginInvoke(() => Push(msg)); } catch { }
        });
    }

    object InitMessage() => new
    {
        type = "init",
        config = PageConfig(_svc.Config),
        haHasPassword = !string.IsNullOrEmpty(_svc.Config.HomeAssistant?.PasswordProtected),
        fans = _svc.Rig.Fans.Select(f => new { f.Id, f.Name, f.Device }),
        zones = _svc.Lighting.Zones.Select(z => new
        {
            id = z.zoneId, dev = z.devId, z.start, z.count,
            ring = z.kind == ZoneKind.Ring, kind = z.kind.ToString().ToLowerInvariant(), z.cols,
            name = _svc.Lighting.LayoutZones.FirstOrDefault(l => l.Id == z.zoneId)?.Name ?? z.zoneId,
            category = _svc.Lighting.LayoutZones.FirstOrDefault(l => l.Id == z.zoneId)?.Category ?? "",
        }),
        devices = _svc.Rig.Lights.Select(l => new { l.Id, l.Name, zones = l.Zones.Select(z => new { z.Id, z.Name, z.Leds }) }),
        hubs = _svc.Rig.Controllers.Select(h => new { h.Id, h.Name, firmware = "", channels = h.Channels.Select(c => new { c.Id, c.Name, c.IsPump }) }),
        log = _svc.Rig.Log,
        effects = Effect.All.Select(e => e.ToString()),
        placements = _svc.Lighting.Placements,
        aspect = Core.Layout.Aspect,
        deviceNames = _svc.Rig.Lights.ToDictionary(l => l.Id, l => l.Name),
        elevated = _svc.Rig.Elevated,
        plugins = PluginsInfo(),
        version = UpdateChecker.CurrentVersion,
        releasesUrl = UpdateChecker.ReleasesUrl,
        cannotInstall = _svc.Updater.CannotInstall(),
    };

    /// <summary>The config as the page sees it: without the stored (encrypted) MQTT password.</summary>
    static JsonNode PageConfig(GlintConfig c)
    {
        var n = JsonSerializer.SerializeToNode(c, GlintConfig.Json)!;
        (n["homeAssistant"] as JsonObject)?.Remove("passwordProtected");
        (n["homeAssistant"] as JsonObject)?.Remove("password");
        return n;
    }

    object PluginsInfo() => new
    {
        dir = Plugins.PluginHost.UserDir,
        list = ControlServer.PluginList(_svc),
        exe = Environment.ProcessPath,
        effects = Plugins.PluginHost.Effects.Select(e => new { e.Name, e.Description, @params = e.Params, e.Preview }),
        status = Plugins.PluginHost.Status,
    };

    void OnPluginsChanged()
    {
        if (IsDisposed) return;
        try { BeginInvoke(() => Push(new { type = "plugins", plugins = PluginsInfo() })); } catch { }
    }

    readonly Process _proc = Process.GetCurrentProcess();
    object StateMessage()
    {
        _proc.Refresh();
        return new
        {
            type = "state",
            sensors = _svc.Sensors.Where(k => !k.Key.StartsWith('/')).ToDictionary(k => k.Key, k => Math.Round(k.Value, 1)),
            rpm = _svc.Rpm,
            duty = _svc.Duty,
            explain = _svc.Explain,
            profile = _svc.Config.ActiveProfile,
            error = _svc.LastError,
            ha = _svc.HomeAssistant.Status(),
            update = _svc.Updates.Info,
            install = _svc.Updater.Status,
            openRgb = _svc.Rig.OpenRgb?.Status,
            testPattern = _svc.Lighting.TestPatternZone,
            // manual fan overrides change outside the page (Home Assistant / API): the page syncs them from here
            overrides = _svc.Config.Profiles.ToDictionary(p => p.Key, p => p.Value.Select(r => r.Override)),
            perf = new
            {
                cpuSeconds = _proc.TotalProcessorTime.TotalSeconds,
                privateMB = _proc.PrivateMemorySize64 / 1048576,
                fps = Math.Round(_svc.Lighting.ActualFps, 1),
                devices = _svc.Lighting.DeviceFps.ToDictionary(k => k.Key, k => Math.Round(k.Value, 1)),
                cores = Environment.ProcessorCount,
            },
        };
    }

    object PreviewMessage()
    {
        var f = _svc.Lighting.Preview;
        var bytes = MemoryMarshal.AsBytes(f.AsSpan()).ToArray();
        _svc.Lighting.WantField();
        var field = _svc.Lighting.Field;
        return new { type = "preview", rgb = Convert.ToBase64String(bytes), field = field == null ? null : Convert.ToBase64String(field), fw = LightingEngine.FieldW, fh = LightingEngine.FieldH };
    }
}
