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
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(12, 13, 18) };
    readonly System.Windows.Forms.Timer _state = new() { Interval = 1000 }, _preview = new() { Interval = 50 };
    bool _ready;
    DateTime _lastCatalog;
    public bool Ready => _ready;
    public Task<string> EvalAsync(string js) => _web.CoreWebView2.ExecuteScriptAsync(js);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public WebDashboard(GlintService svc)
    {
        _svc = svc;
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
            if (_svc.Rig.CatalogWanted && _svc.Rig.Catalog is { } cat && cat.Time != _lastCatalog) { _lastCatalog = cat.Time; Push(new { type = "sensors", catalog = cat }); }
        };
        _preview.Tick += (_, _) => Push(PreviewMessage());
        Plugins.PluginHost.EffectsChanged += OnPluginsChanged;
        FormClosed += (_, _) => { Plugins.PluginHost.EffectsChanged -= OnPluginsChanged; _state.Dispose(); _preview.Dispose(); _web.Dispose(); };
        _ = InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1; // dark title bar + Mica backdrop (Win11)
        DwmSetWindowAttribute(Handle, 20, ref on, 4);
        int mica = 2; DwmSetWindowAttribute(Handle, 38, ref mica, 4);
    }

    async Task InitAsync()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glint", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection"));
        await _web.EnsureCoreWebView2Async(env);
        var cw = _web.CoreWebView2;
        cw.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.AreDefaultContextMenusEnabled = false;
        cw.Settings.IsZoomControlEnabled = false;
        var ui = Path.Combine(AppContext.BaseDirectory, "web");
        cw.SetVirtualHostNameToFolderMapping("glint.local", ui, CoreWebView2HostResourceAccessKind.DenyCors);
        cw.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
        cw.Navigate("https://glint.local/index.html");
    }

    void Push(object msg)
    {
        if (!_ready || _web.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg, GlintConfig.Json)); } catch { }
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
                break;
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
                Process.Start(new ProcessStartInfo("explorer.exe", GlintConfig.Dir));
                break;
            case "openPlugins":
                Directory.CreateDirectory(Plugins.PluginHost.UserDir);
                Process.Start(new ProcessStartInfo("explorer.exe", Plugins.PluginHost.UserDir));
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
                    File.Delete(f);
                    Task.Run(Plugins.PluginHost.ReloadNow);
                }
                catch (Exception e) { Push(new { type = "toast", kind = "error", text = e.Message }); }
                break;
            case "openPluginFile":
                {
                    var f = Plugins.PluginHost.FindFile((string?)m!["file"] ?? "");
                    if (f != null) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{f}\"") { UseShellExecute = false });
                    break;
                }
            case "newPlugin":
                {
                    bool device = (string?)m!["kind"] == "device";
                    var tpl = Path.Combine(AppContext.BaseDirectory, "plugin-templates", device ? "device-template.js" : "effect-template.js");
                    string baseName = device ? "my-device" : "my-effect", file;
                    int n = 1;
                    do file = Path.Combine(Plugins.PluginHost.UserDir, $"{baseName}{(n > 1 ? "-" + n : "")}.js"); while (File.Exists(file) && ++n < 100);
                    var code = File.ReadAllText(tpl);
                    if (!device) code = code.Replace("name: 'My effect'", $"name: '{Path.GetFileNameWithoutExtension(file)}'");
                    File.WriteAllText(file, code);
                    Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = false });
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
        version = typeof(WebDashboard).Assembly.GetName().Version?.ToString(3),
    };

    /// <summary>The config as the page sees it: without the stored (encrypted) MQTT password.</summary>
    static JsonNode PageConfig(GlintConfig c)
    {
        var n = JsonSerializer.SerializeToNode(c, GlintConfig.Json)!;
        (n["homeAssistant"] as JsonObject)?.Remove("passwordProtected");
        (n["homeAssistant"] as JsonObject)?.Remove("password");
        return n;
    }

    static object PluginsInfo() => new
    {
        dir = Plugins.PluginHost.UserDir,
        list = ControlServer.PluginList(),
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
