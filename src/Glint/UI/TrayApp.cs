using System.Drawing.Drawing2D;
using Glint.Core;

namespace Glint.UI;

public sealed class TrayApp : ApplicationContext
{
    readonly GlintService _svc;
    readonly NotifyIcon _icon;
    readonly System.Windows.Forms.Timer _tick;
    WebDashboard? _dash;
    TuningMonitor? _monitor;
    readonly HotkeyWindow _hotkey;
    readonly GameOverlay.OverlayController _overlay;
    readonly Thread _uiThread = Thread.CurrentThread;
    readonly SynchronizationContext _sync;
    public static readonly Icon AppIcon = MakeIcon();

    public TrayApp(GlintService svc, bool quiet = false)
    {
        _svc = svc;
        _icon = new NotifyIcon { Icon = AppIcon, Visible = true, Text = "Glint" };
        _icon.DoubleClick += (_, _) => ShowDashboard();
        _icon.ContextMenuStrip = new ContextMenuStrip();
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _tick = new System.Windows.Forms.Timer { Interval = 2000 };
        _tick.Tick += (_, _) => UpdateTooltip();
        _tick.Start();
        // In-game overlay: hotkey on a message-only window; nothing else runs until it is switched on.
        _hotkey = new HotkeyWindow(ToggleOverlay);
        _sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        GameOverlay.EtwPresentSource.StopSession();   // a session left behind by a crash keeps running otherwise
        _overlay = new GameOverlay.OverlayController(_svc, new GameOverlay.EtwPresentSource(), () => new GameOverlay.LayeredOverlayWindow(), Ui);
        _svc.Overlay = _overlay;
        // any hardware sensor from the Sensors page catalog can be an overlay item (clocks, loads, power, VRAM ...)
        _overlay.ExtraResolver = src => _svc.Rig.CatalogValue(src.StartsWith("lhm:") ? src[4..] : src);
        RegisterHotkey(_svc.Config.Overlay?.Hotkey);
        _svc.Updater.Changed += OnUpdater;
        // the tuning monitor pops up when a GPU test or an AI run starts (a test inside an AI run doesn't re-open a window the user closed)
        _svc.Tuning.JobStarted += _ => { if (!_svc.Ai.Running) _sync.Post(_ => ShowMonitor(false), null); };
        _svc.Ai.Started += () => _sync.Post(_ => ShowMonitor(false), null);
        _svc.ConfigApplied += c => Ui(() => RegisterHotkey(c.Overlay?.Hotkey));
        // Judge sensors on real readings, not at construction (LHM's first sample can be empty).
        if (!quiet)
        {
            var check = new System.Windows.Forms.Timer { Interval = 15000 };
            check.Tick += (_, _) =>
            {
                check.Dispose();
                if (!_svc.Sensors.ContainsKey("cpu"))
                    _icon.ShowBalloonTip(8000, "Glint", "CPU temperature unavailable — Glint needs admin + PawnIO (run install.ps1). CPU-driven curves are at fail-safe speed.", ToolTipIcon.Warning);
            };
            check.Start();
        }
    }

    /// <summary>Run on the UI thread (inline if already on it).</summary>
    string _hotkeyWarned = "";
    /// <summary>Register the overlay hotkey; if another app owns it, say so once (log + tray notification).</summary>
    void RegisterHotkey(string? hotkey)
    {
        _hotkey.Register(hotkey);
        if (_hotkey.Error.Length == 0) { _hotkeyWarned = ""; return; }
        _svc.Rig.Log.Add("Overlay: " + _hotkey.Error + " (change it in the Game overlay page)");
        if (_hotkeyWarned == hotkey) return;
        _hotkeyWarned = hotkey ?? "";
        _icon.ShowBalloonTip(8000, "Glint", $"The overlay hotkey {hotkey} is already used by another app. Pick a different one in the Game overlay page.", ToolTipIcon.Warning);
    }

    void Ui(Action a)
    {
        if (Thread.CurrentThread == _uiThread) a();
        else _sync.Post(_ => a(), null);
    }

    void ToggleOverlay()
    {
        var c = System.Text.Json.JsonSerializer.Deserialize<GlintConfig>(System.Text.Json.JsonSerializer.Serialize(_svc.Config, GlintConfig.Json), GlintConfig.Json)!;
        c.Overlay ??= new();
        c.Overlay.Enabled = !c.Overlay.Enabled;
        _svc.Apply(c);
    }

    void UpdateTooltip()
    {
        var s = _svc.Sensors;
        string T(string k) => s.TryGetValue(k, out var v) ? $"{v:0}°" : "–";
        var text = $"Glint · {_svc.Config.ActiveProfile}\nCPU {T("cpu")}  GPU {T("gpu")}  Liquid {T("liquid")}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>One radio-style item per fan profile (the active one ticked). '&amp;' is escaped so a name never becomes a keyboard accelerator.</summary>
    internal static List<ToolStripMenuItem> ProfileItems(GlintConfig cfg, Action<string> pick)
    {
        var items = new List<ToolStripMenuItem>();
        foreach (var p in cfg.Profiles.Keys)
        {
            var name = p;
            var it = new ToolStripMenuItem(name.Replace("&", "&&")) { Checked = name == cfg.ActiveProfile, CheckOnClick = false };
            it.Click += (_, _) => { if (cfg.Profiles.ContainsKey(name)) pick(name); };
            items.Add(it);
        }
        return items;
    }

    void BuildMenu()
    {
        var m = _icon.ContextMenuStrip!;
        m.Items.Clear();
        var cfg = _svc.Config;
        var s = _svc.Sensors;
        string T(string k) => s.TryGetValue(k, out var v) ? $"{v:0.0}°C" : "n/a";
        m.Items.Add(new ToolStripMenuItem($"CPU {T("cpu")}   GPU {T("gpu")}   Liquid {T("liquid")}") { Enabled = false });
        m.Items.Add(new ToolStripSeparator());

        var prof = new ToolStripMenuItem("Fan profile");
        foreach (var it in ProfileItems(cfg, p => { try { _svc.UseProfile(p); } catch (Exception e) { _svc.Rig.Log.Add("profile: " + e.Message); } })) prof.DropDownItems.Add(it);
        var fanOff = new ToolStripMenuItem("Fan control off") { Checked = !cfg.FanControl, ToolTipText = "Hand fans back to the BIOS, GPU driver and device firmware" };
        fanOff.Click += (_, _) => { cfg.FanControl = !cfg.FanControl; _svc.Apply(cfg); };
        prof.DropDownItems.Add(new ToolStripSeparator());
        prof.DropDownItems.Add(fanOff);
        m.Items.Add(prof);

        var light = new ToolStripMenuItem("Lighting");
        foreach (var (name, fx) in cfg.Lighting.Presets)
        {
            var it = new ToolStripMenuItem(name);
            it.Click += (_, _) => { cfg.Lighting.Effect = Clone(fx); cfg.Lighting.Overrides.Clear(); cfg.Lighting.Enabled = true; _svc.Apply(cfg); };
            light.DropDownItems.Add(it);
        }
        light.DropDownItems.Add(new ToolStripSeparator());
        foreach (var b in new[] { 100, 75, 50, 25, 10 })
        {
            var it = new ToolStripMenuItem($"Brightness {b}%") { Checked = cfg.Lighting.Brightness == b };
            it.Click += (_, _) => { cfg.Lighting.Brightness = b; _svc.Apply(cfg); };
            light.DropDownItems.Add(it);
        }
        var off = new ToolStripMenuItem("Lights off") { Checked = !cfg.Lighting.Enabled };
        off.Click += (_, _) => { cfg.Lighting.Enabled = !cfg.Lighting.Enabled; _svc.Apply(cfg); };
        light.DropDownItems.Add(off);
        m.Items.Add(light);

        var ov = new ToolStripMenuItem("Game overlay") { Checked = cfg.Overlay?.Enabled == true, ShortcutKeyDisplayString = cfg.Overlay?.Hotkey is { Length: > 0 } hk ? hk : null };
        ov.Click += (_, _) => ToggleOverlay();
        m.Items.Add(ov);

        m.Items.Add(new ToolStripSeparator());
        if (_svc.Updates.Info is { Available: true } up)
        {
            var st = _svc.Updater.Status;
            if (_svc.Updater.Busy)
                m.Items.Add(new ToolStripMenuItem($"Updating to v{st.Version ?? up.Latest}: {st.State}…") { Enabled = false });
            else if (_svc.Updater.CannotInstall() == null)
                m.Items.Add($"Install update v{up.Latest}", null, (_, _) => InstallUpdate(up.Latest!));
            m.Items.Add($"Update available: v{up.Latest} (release page)…", null, (_, _) => Program.OpenUrl(up.Url ?? UpdateChecker.ReleasesUrl));
        }
        m.Items.Add("Dashboard…", null, (_, _) => ShowDashboard());
        m.Items.Add("Edit config file", null, (_, _) => WebDashboard.OpenInNotepad(GlintConfig.FilePath));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Exit (hand fans back to hardware)", null, (_, _) => ExitThread());
    }

    void InstallUpdate(string version)
    {
        if (MessageBox.Show($"Install Glint v{version} now?\n\nGlint downloads the setup from its GitHub releases, checks its SHA-256 and GitHub build attestation, then closes and restarts with the new version.",
                "Glint update", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _svc.Updater.Start();
    }

    /// <summary>Install progress / errors in the tray (the dashboard may be closed).</summary>
    void OnUpdater(UpdateStatus s)
    {
        if (s.State == "failed" && s.Message != null) Ui(() => _icon.ShowBalloonTip(10000, "Glint update", s.Message, ToolTipIcon.Warning));
        else if (s.State == "installing" && s.Message != null) Ui(() => _icon.ShowBalloonTip(5000, "Glint update", s.Message, ToolTipIcon.Info));
    }

    public static EffectConfig Clone(EffectConfig e) =>
        System.Text.Json.JsonSerializer.Deserialize<EffectConfig>(System.Text.Json.JsonSerializer.Serialize(e, GlintConfig.Json), GlintConfig.Json)!;

    /// <summary>Open the tuning monitor (single instance). Explicit opens bring it to the front; automatic ones don't steal focus from an open one.</summary>
    void ShowMonitor(bool explicitOpen)
    {
        if (_monitor is { IsDisposed: false })
        {
            if (_monitor.WindowState == FormWindowState.Minimized) _monitor.WindowState = FormWindowState.Normal;
            if (explicitOpen) { _monitor.Activate(); _monitor.BringToFront(); }
            return;
        }
        _monitor = new TuningMonitor(_svc);
        _monitor.FormClosed += (_, _) => { _monitor = null; GC.Collect(); };
        _monitor.Show();
    }

    void ShowDashboard()
    {
        if (_dash is { IsDisposed: false }) { _dash.Activate(); return; }
        _dash = new WebDashboard(_svc, () => ShowMonitor(true));
        _dash.FormClosed += (_, _) => { _dash = null; GC.Collect(); };
        _dash.Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _overlay.Dispose(); _hotkey.Dispose(); _tick.Dispose(); _icon.Visible = false; _icon.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void ExitThreadCore()
    {
        _tick.Stop();
        _icon.Visible = false;
        _overlay.Dispose();
        _hotkey.Dispose();
        _dash?.Close();
        base.ExitThreadCore();
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var br = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(255, 90, 31), Color.FromArgb(160, 60, 255), 45f);
            g.FillEllipse(br, 2, 2, 28, 28);
            using var p = new Pen(Color.White, 3);
            for (int i = 0; i < 3; i++) g.DrawArc(p, 9, 9, 14, 14, i * 120, 70);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>Message-only window that receives WM_HOTKEY for the overlay toggle.</summary>
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    readonly Action _onHotkey;
    bool _registered;
    string? _current;
    public string Error { get; private set; } = "";

    public HotkeyWindow(Action onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams { Parent = (IntPtr)(-3) /* HWND_MESSAGE */ });
    }

    public void Register(string? hotkey)
    {
        if (hotkey == _current && (_registered || string.IsNullOrWhiteSpace(hotkey))) return;
        Unregister();
        _current = hotkey;
        if (GameOverlay.Hotkey.Parse(hotkey) is not { } k) return;
        _registered = RegisterHotKey(Handle, 1, k.mods | 0x4000 /* MOD_NOREPEAT */, k.vk);
        Error = _registered ? "" : $"hotkey {hotkey} is taken by another app";
    }

    void Unregister() { if (_registered) UnregisterHotKey(Handle, 1); _registered = false; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 /* WM_HOTKEY */) { try { _onHotkey(); } catch { } return; }
        base.WndProc(ref m);
    }

    public void Dispose() { Unregister(); if (Handle != IntPtr.Zero) DestroyHandle(); }

    [System.Runtime.InteropServices.DllImport("user32")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.DllImport("user32")] static extern bool UnregisterHotKey(IntPtr h, int id);
}
