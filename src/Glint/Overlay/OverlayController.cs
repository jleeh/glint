using System.Diagnostics;
using System.Runtime.InteropServices;
using Glint.Core;

namespace Glint.GameOverlay;

/// <summary>
/// Runs the in-game overlay. Costs nothing while off: no timer, no ETW session, no window, no bitmap.
/// While on, a one-shot timer ticks at 4 Hz (1 Hz if no FPS items are shown): it follows the foreground window,
/// starts the ETW session only while the overlay is actually on screen, and redraws only when the text, the
/// position or the style changed.
/// </summary>
public sealed class OverlayController : IDisposable
{
    readonly GlintService _svc;
    readonly IPresentSource _source;
    readonly Func<IOverlaySurface> _surfaceFactory;
    readonly Action<Action> _ui;
    readonly Func<ForegroundInfo> _probe;
    readonly Func<int, string> _processName;
    readonly PresentTracker _tracker;
    readonly object _lock = new();
    readonly bool _useTimer;
    System.Threading.Timer? _timer;
    IOverlaySurface? _surface;
    OverlayConfig _cfg;
    bool _disposed;
    long _retryAt;
    string _lastKey = "";
    int _pid; string _procName = ""; readonly Dictionary<int, string> _names = new();
    (FrameStats.Result stats, int stream) _frames;
    double _cpuWall, _cpuProc, _glintCpu = double.NaN;

    /// <summary>Extra item sources (the hardware sensor catalog plugs in here).</summary>
    public SensorResolver? ExtraResolver { get; set; }
    /// <summary>The overlay is on (config) — it may still be hidden by onlyWhenGame.</summary>
    public bool Active { get; private set; }
    /// <summary>The overlay is on screen right now.</summary>
    public bool Visible { get; private set; }
    public IReadOnlyList<OverlayRow> LastRows { get; private set; } = Array.Empty<OverlayRow>();
    public int Redraws { get; private set; }
    /// <summary>Wall time spent in ticks + redraws (ms). Finer than thread CPU time (15.6 ms granularity) for tiny costs.</summary>
    public double BusyMs { get; private set; }
    public IPresentSource Source => _source;

    public OverlayController(GlintService svc, IPresentSource source, Func<IOverlaySurface> surfaceFactory, Action<Action> ui,
        Func<ForegroundInfo>? probe = null, Func<int, string>? processName = null, bool useTimer = true)
    {
        _svc = svc; _source = source; _surfaceFactory = surfaceFactory; _ui = ui;
        _probe = probe ?? Foreground.Probe;
        _processName = processName ?? NameOf;
        _useTimer = useTimer;
        _tracker = new PresentTracker(source.Frequency);
        _cfg = svc.Config.Overlay ?? new();
        svc.ConfigApplied += OnConfig;
        Apply(_cfg);
    }

    void OnConfig(GlintConfig c) => Apply(c.Overlay ?? new());

    /// <summary>New overlay settings (any thread).</summary>
    public void Apply(OverlayConfig c)
    {
        lock (_lock)
        {
            if (_disposed) return;
            c.Items ??= new(); c.Position ??= "top-left"; c.Layout ??= "detailed";
            _cfg = c;
            _lastKey = "";                          // style may have changed: redraw on the next tick
            if (c.Enabled && !Active)
            {
                Active = true;
                if (_useTimer) _timer = new System.Threading.Timer(_ => TimerTick(), null, 0, Timeout.Infinite);
            }
            else if (!c.Enabled && Active)
            {
                Active = false;
                _timer?.Dispose(); _timer = null;
                HideAndStop();
            }
        }
    }

    void TimerTick()
    {
        int next;
        try { next = Tick(); } catch { next = 1000; }
        lock (_lock) _timer?.Change(next, Timeout.Infinite);
    }

    /// <summary>One update. Returns ms until the next one. Public so tests can drive it without a timer.</summary>
    public int Tick()
    {
        double cpu0 = CpuMeter.ThreadMs();
        long w0 = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_disposed || !Active) return 1000;
            var cfg = _cfg;
            bool wantFrames = cfg.Items.Any(i => OverlayItems.IsFrameSource(i.Source));
            var fg = _probe();
            if (fg.Pid != _pid) { _pid = fg.Pid; _procName = Name(fg.Pid); }
            bool game = Foreground.IsGame(fg, _procName);
            bool show = !cfg.OnlyWhenGame || game;
            if (!show) { HideAndStop(); return 500; }

            // frames: only the foreground process, and only while the overlay is on screen
            if (fg.Pid != Environment.ProcessId) _tracker.TargetPid = fg.Pid;
            if (wantFrames && !_source.Running && Environment.TickCount64 >= _retryAt)
                if (!_source.Start(_tracker.OnPresent)) _retryAt = Environment.TickCount64 + 10_000;   // e.g. not elevated: don't hammer StartTrace
            else if (!wantFrames && _source.Running) _source.Stop();
            _frames = _source.Running ? _tracker.Read(_source.Now()) : default;
            UpdateGlintCpu();

            var data = new OverlayData
            {
                Frames = _frames.stats, Sensors = _svc.Sensors, Rpm = _svc.Rpm, Duty = _svc.Duty,
                FanNames = _svc.Config.FanNames, SensorNames = _svc.Config.SensorNames, GlintCpu = _glintCpu, Now = DateTime.Now, Extra = ExtraResolver,
            };
            var rows = OverlayItems.Rows(cfg.Items, data);
            LastRows = rows;
            double scale = Math.Clamp(cfg.Scale, Limits.OverlayScaleMin, Limits.OverlayScaleMax) * (fg.DpiScale > 0 ? fg.DpiScale : 1);
            var style = new OverlayStyle(Math.Round(scale, 3), cfg.BackgroundOpacity,
                cfg.Accent is { } a && Limits.Color.IsMatch(a) ? ColorTranslator.FromHtml(a) : null, cfg.Layout == "compact");
            var area = fg.Fullscreen || fg.Work.IsEmpty ? fg.Monitor : fg.Work;
            if (area.IsEmpty) area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            bool right = cfg.Position.EndsWith("right"), bottom = cfg.Position.StartsWith("bottom");
            int ox = (int)Math.Round(cfg.OffsetX * fg.DpiScale), oy = (int)Math.Round(cfg.OffsetY * fg.DpiScale);
            var anchor = new Point(right ? area.Right - ox : area.Left + ox, bottom ? area.Bottom - oy : area.Top + oy);
            string key = string.Join("|", rows.Select(r => r.Label + "=" + r.Value + r.Unit + r.Color)) + $"|{style}|{anchor}|{right}{bottom}|{cfg.Opacity}";
            if (key != _lastKey || !Visible)
            {
                _lastKey = key;
                double opacity = Math.Clamp(cfg.Opacity, Limits.OverlayOpacityMin, 1);
                Redraws++;
                Visible = true;
                _ui(() =>
                {
                    if (!Active) return;
                    long r0 = Stopwatch.GetTimestamp();
                    try { (_surface ??= _surfaceFactory()).Present(rows, style, anchor, right, bottom, opacity); } catch { }
                    BusyMs += Stopwatch.GetElapsedTime(r0).TotalMilliseconds;
                });
            }
            CpuMeter.Add("overlay", CpuMeter.ThreadMs() - cpu0);
            BusyMs += Stopwatch.GetElapsedTime(w0).TotalMilliseconds;
            return wantFrames ? 250 : 1000;
        }
    }

    void HideAndStop()
    {
        if (_source.Running) _source.Stop();
        _frames = default;
        bool was = Visible;
        Visible = false;
        if (was || !Active) _ui(() => { if (!Active) { _surface?.Dispose(); _surface = null; OverlayRenderer.ReleaseCache(); } else _surface?.Hide(); });
    }

    string Name(int pid)
    {
        if (pid == 0) return "";
        if (_names.TryGetValue(pid, out var n)) return n;
        if (_names.Count > 64) _names.Clear();
        return _names[pid] = _processName(pid);
    }

    static string NameOf(int pid) { try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return ""; } }

    void UpdateGlintCpu()
    {
        double wall = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (!GetProcessTimes(GetCurrentProcess(), out _, out _, out long k, out long u)) return;
        double proc = (k + u) / 1e7;
        if (_cpuWall > 0 && wall - _cpuWall >= 0.9)
        {
            _glintCpu = (proc - _cpuProc) / (wall - _cpuWall) / Environment.ProcessorCount * 100;
            _cpuWall = wall; _cpuProc = proc;
        }
        else if (_cpuWall == 0) { _cpuWall = wall; _cpuProc = proc; }
    }

    /// <summary>For overlay.stats.</summary>
    public object Stats()
    {
        lock (_lock)
        {
            var (s, stream) = _frames;
            double R(double v, int d) => Math.Round(v, d);
            return new
            {
                enabled = Active, visible = Visible,
                process = _procName, pid = _pid,
                fps = s.Fps > 0 ? R(s.Fps, 1) : (double?)null,
                frameTimeMs = s.FrameMs > 0 ? R(s.FrameMs, 2) : (double?)null,
                low1 = s.Low1 > 0 ? R(s.Low1, 1) : (double?)null,
                samples = s.Samples,
                presentSource = PresentStream.Name(stream),
                etw = _source.Running ? "running" : "stopped",
                etwStatus = _source.Status,
                text = string.Join("  ", LastRows.Select(r => $"{r.Label} {r.Value}{r.Unit}")),
            };
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _svc.ConfigApplied -= OnConfig;
            Active = false;
            _timer?.Dispose(); _timer = null;
            HideAndStop();
            _disposed = true;
        }
        _source.Dispose();
    }

    [DllImport("kernel32")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
}
