using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glint.Core;
using Glint.Tuning;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Glint.UI;

/// <summary>
/// The small "Tuning monitor" window: opens when a GPU test or an AI run starts and shows the progress, live gauges and the
/// AI conversation (web/monitor.html). Like the dashboard it only exists while open: one 1 s timer pushing status, and
/// closing disposes the WebView2 browser processes. Single instance, owned by the tray.
/// </summary>
public sealed class TuningMonitor : Form
{
    readonly GlintService _svc;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(12, 13, 18) };
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    bool _ready;
    public bool Ready => _ready;
    public Task<string> EvalAsync(string js) => _web.CoreWebView2.ExecuteScriptAsync(js);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public TuningMonitor(GlintService svc)
    {
        _svc = svc;
        Text = "Glint · Tuning monitor";
        Size = new Size(520, 720);
        MinimumSize = new Size(400, 460);
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(Math.Max(wa.Left, wa.Right - Width - 24), Math.Max(wa.Top, wa.Bottom - Height - 24));
        BackColor = Color.FromArgb(12, 13, 18);
        Icon = TrayApp.AppIcon;
        Controls.Add(_web);
        _tick.Tick += (_, _) => Push(Message(_svc.Tuning, _svc.Ai, _svc.Cpu));
        FormClosed += (_, _) => { _tick.Dispose(); _web.Dispose(); };
        _ = InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4);
    }

    /// <summary>What the page gets every second: the same three status objects the dashboard's Tuning page reads.</summary>
    public static object Message(TuningService tuning, AiTuner ai, CpuTuning cpu)
    {
        try { return new { type = "tuning", status = tuning.Status(), ai = ai.Status(), cpu = cpu.Status(), at = DateTime.Now }; }
        catch (Exception e) { return new { type = "tuning", error = e.Message }; }
    }

    /// <summary>The page's actions. "stop" ends the AI run and the running test; anything else is refused.</summary>
    public static bool Act(string? op, TuningService tuning, AiTuner ai)
    {
        if (op != "stop") return false;
        ai.Stop();
        tuning.Stop();
        return true;
    }

    async Task InitAsync()
    {
        WebDashboard.ClearWebView2Overrides();
        var env = await CoreWebView2Environment.CreateAsync(null, WebDashboard.DataFolder(_svc.Rig.Elevated), new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection"));
        await _web.EnsureCoreWebView2Async(env);
        var cw = _web.CoreWebView2;
        cw.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.AreDefaultContextMenusEnabled = false;
        cw.Settings.IsZoomControlEnabled = false;
        cw.Settings.AreHostObjectsAllowed = false;
        cw.Settings.IsGeneralAutofillEnabled = false;
        cw.Settings.IsPasswordAutosaveEnabled = false;
        cw.SetVirtualHostNameToFolderMapping("glint.local", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.DenyCors);
        cw.NavigationStarting += (_, e) => { if (!WebDashboard.IsLocal(e.Uri)) e.Cancel = true; };
        cw.FrameNavigationStarting += (_, e) => { if (!WebDashboard.IsLocal(e.Uri)) e.Cancel = true; };
        cw.NewWindowRequested += (_, e) => e.Handled = true;
        cw.DownloadStarting += (_, e) => e.Cancel = true;
        cw.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        cw.WebMessageReceived += (_, e) => { if (WebDashboard.IsLocal(e.Source)) OnMessage(e.WebMessageAsJson); };
        cw.Navigate(WebDashboard.Origin + "monitor.html");
    }

    void OnMessage(string json)
    {
        JsonNode? m;
        try { m = JsonNode.Parse(json); } catch { return; }
        switch ((string?)m?["type"])
        {
            case "ready": _ready = true; Push(Message(_svc.Tuning, _svc.Ai, _svc.Cpu)); _tick.Start(); break;
            case "top": TopMost = (bool?)m!["on"] == true; break;
            case "action":
                try { Act((string?)m!["op"], _svc.Tuning, _svc.Ai); } catch { }
                Push(Message(_svc.Tuning, _svc.Ai, _svc.Cpu));
                break;
        }
    }

    void Push(object msg)
    {
        if (!_ready || IsDisposed || _web.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg, GlintConfig.Json)); } catch { }
    }
}
