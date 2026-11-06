using System.Diagnostics;
using Glint.Devices;

namespace Glint.Core;

/// <summary>
/// Renders effects into every LED and pushes frames to devices. Frames are only sent when a
/// device's colours actually changed (plus a slow keep-alive), so static scenes cost ~nothing.
/// </summary>
public sealed class LightingEngine : IDisposable
{
    readonly Rig _rig;
    readonly Func<IReadOnlyDictionary<string, double>> _sensors;
    GlintConfig _cfg;
    Thread? _thread;
    volatile bool _stop;
    volatile bool _dirty = true;
    AudioAnalyzer? _audio;

    // layout
    Led[] _leds = Array.Empty<Led>();
    readonly List<(ILightDevice dev, int start, int count)> _devSlices = new();
    readonly List<(string zoneId, string devId, int start, int count, ZoneKind kind, int cols)> _zones = new();
    readonly List<LayoutZone> _layoutZones = new();
    string _layoutKey = "";
    /// <summary>Effective placement of every zone (saved layout, auto-arranged where missing).</summary>
    public Dictionary<string, ZonePlacement> Placements { get; private set; } = new();
    public IReadOnlyList<LayoutZone> LayoutZones => _layoutZones;
    (HashSet<string> targets, double until) _identify = (new(), 0);
    List<(Effect fx, int[] idx)> _groups = new();
    Rgb[] _frame = Array.Empty<Rgb>();
    readonly Dictionary<ILightDevice, (Rgb[] last, long sentMs)> _sent = new();

    public double LastFrameMs { get; private set; }
    public Dictionary<string, double> DeviceMs => _writers.Values.ToDictionary(w => w.Dev.Id, w => w.LastMs);
    public long FramesSent => _writers.Values.Sum(w => w.Sent);
    public Dictionary<string, double> DeviceFps => _writers.Values.ToDictionary(w => w.Dev.Id, w => w.Fps);
    readonly Dictionary<ILightDevice, DeviceWriter> _writers = new();
    public double ActualFps { get; private set; }
    public string Status { get; private set; } = "";
    public Rgb[] Preview => _frame;
    internal Led[] LedsForTest => _leds;
    public IReadOnlyList<(string zoneId, string devId, int start, int count, ZoneKind kind, int cols)> Zones => _zones;

    public LightingEngine(Rig rig, GlintConfig cfg, Func<IReadOnlyDictionary<string, double>> sensors)
    {
        _rig = rig; _cfg = cfg; _sensors = sensors;
        BuildLayout();
    }

    void BuildLayout()
    {
        var leds = new List<Led>();
        int zoneIdx = 0;
        // Default spatial order: AIO pump, radiator fans, case fans, then strips. Override with config order later.
        foreach (var dev in _rig.Lights)
        {
            int devStart = leds.Count;
            foreach (var z in dev.Zones)
            {
                bool ring = z.Kind == ZoneKind.Ring;
                _zones.Add((z.Id, dev.Id, leds.Count, z.Leds, z.Kind, z.Cols));
                _layoutZones.Add(new LayoutZone(z.Id, dev.Id, z.Name, z.Leds, z.Kind, z.Cols, z.Category));
                for (int i = 0; i < z.Leds; i++)
                {
                    // U = position within the zone: around a ring, along a strip, or down a matrix (row).
                    double u = z.Kind switch
                    {
                        ZoneKind.Ring => (double)i / z.Leds,
                        ZoneKind.Matrix when z.Cols > 0 => (double)(i / z.Cols) / Math.Max(1, (z.Leds + z.Cols - 1) / z.Cols - 1),
                        _ => z.Leds <= 1 ? 0 : (double)i / (z.Leds - 1),
                    };
                    // Matrix X spreads columns across the zone's slice so waves sweep across keyboards.
                    double xin = z.Kind == ZoneKind.Matrix && z.Cols > 0 ? (double)(i % z.Cols) / Math.Max(1, z.Cols - 1) : ring ? 0.5 : u;
                    leds.Add(new Led(leds.Count, zoneIdx, xin, u, ring));
                }
                zoneIdx++;
            }
            _devSlices.Add((dev, devStart, leds.Count - devStart));
            _writers[dev] = new DeviceWriter(dev, e => Status = e);
        }
        _leds = leds.ToArray();
        _frame = new Rgb[_leds.Length];
        ApplyLayout();
    }

    /// <summary>Place every LED on the canvas from the saved layout (auto-arranging zones that have no placement).</summary>
    internal void ApplyLayout()
    {
        var saved = _cfg.Lighting.Layout;
        var auto = Layout.AutoArrange(_layoutZones);
        Placements = _layoutZones.ToDictionary(z => z.Id, z => saved.TryGetValue(z.Id, out var p) ? p : auto[z.Id]);
        var leds = _leds;
        foreach (var (zoneId, _, start, count, kind, cols) in _zones)
        {
            var pts = Layout.Positions(Placements[zoneId], kind, count, cols);
            for (int i = 0; i < count; i++) leds[start + i] = leds[start + i] with { X = Math.Clamp(pts[i].x, 0, 1), Y = Math.Clamp(pts[i].y, 0, 1) };
        }
        _layoutKey = System.Text.Json.JsonSerializer.Serialize(saved);
    }

    /// <summary>Flash a device or zone white for a few seconds (everything else dims) so you can find it.</summary>
    public void Identify(string target, double seconds = 4)
    {
        var t = _zones.Where(z => z.zoneId == target || z.devId == target).Select(z => z.zoneId).ToHashSet();
        _identify = (t, _clock.Elapsed.TotalSeconds + seconds);
    }
    readonly Stopwatch _clock = Stopwatch.StartNew();

    // ---- ARGB LED-count test pattern (Devices page): one zone, time-limited, like Identify
    volatile TestPatternState? _test;
    sealed record TestPatternState(string ZoneId, int Count, double Until);
    public const double TestPatternSeconds = 60;
    /// <summary>Colour blocks every 8 LEDs (one fan's worth); never white or red, which mark the ends.</summary>
    public static readonly Rgb[] TestPatternBlocks = { new(0, 255, 0), new(0, 80, 255), new(255, 200, 0), new(200, 0, 255), new(0, 255, 255), new(255, 100, 0) };
    public const int TestPatternBlock = 8;

    /// <summary>Paint a zone's LED-count test pattern for <paramref name="count"/> LEDs (the count the user typed):
    /// LED 1 white, LED <paramref name="count"/> red, blocks of 8 in between, anything past the count dark. When the count
    /// matches the real fans/strip, the red LED is the very last one that lights.</summary>
    public static void PaintTestPattern(Span<Rgb> zone, int count)
    {
        for (int i = 0; i < zone.Length; i++)
            zone[i] = i >= count ? Rgb.Black : i == 0 ? new Rgb(255, 255, 255) : i == count - 1 ? new Rgb(255, 0, 0) : TestPatternBlocks[i / TestPatternBlock % TestPatternBlocks.Length];
    }

    /// <summary>Show the test pattern on one zone for up to <paramref name="seconds"/> (count = what the user typed; LEDs
    /// beyond the zone's current length need a restart to exist). Returns false for an unknown zone.</summary>
    public bool TestPattern(string zoneId, int count, double seconds = TestPatternSeconds)
    {
        if (!_zones.Any(z => z.zoneId == zoneId)) return false;
        _test = new TestPatternState(zoneId, Math.Max(0, count), _clock.Elapsed.TotalSeconds + Math.Clamp(seconds, 0, TestPatternSeconds));
        return true;
    }
    public void StopTestPattern() => _test = null;
    /// <summary>The zone showing the test pattern right now (null when off or expired).</summary>
    public string? TestPatternZone => _test is { } t && t.Until > _clock.Elapsed.TotalSeconds ? t.ZoneId : null;

    // ---- effect field preview (what the global effect paints across the whole canvas, like SignalRGB's canvas)
    public const int FieldW = 64, FieldH = 40;
    Effect? _fieldFx;
    readonly Led[] _fieldLeds = Enumerable.Range(0, FieldW * FieldH).Select(i => new Led(i, 0, (i % FieldW + 0.5) / FieldW, (i % FieldW + 0.5) / FieldW, false, (i / FieldW + 0.5) / FieldH)).ToArray();
    readonly Rgb[] _field = new Rgb[FieldW * FieldH];
    double _fieldWantedUntil;
    /// <summary>Latest field frame (RGB bytes, FieldW×FieldH) or null if nobody asked recently.</summary>
    public byte[]? Field { get; private set; }
    /// <summary>The dashboard calls this while the Lighting page is open; the field is only rendered then.</summary>
    public void WantField() => _fieldWantedUntil = _clock.Elapsed.TotalSeconds + 2;

    public void Apply(GlintConfig cfg)
    {
        _cfg = cfg;
        _dirty = true;
    }

    void Rebuild()
    {
        if (System.Text.Json.JsonSerializer.Serialize(_cfg.Lighting.Layout) != _layoutKey) ApplyLayout();
        var L = _cfg.Lighting;
        var map = new Dictionary<EffectConfig, List<int>>(ReferenceEqualityComparer.Instance);
        foreach (var (zoneId, devId, start, count, _, _) in _zones)
        {
            var ec = L.Overrides.GetValueOrDefault(zoneId) ?? L.Overrides.GetValueOrDefault(devId) ?? L.Effect;
            if (!L.Enabled) ec = new EffectConfig { Type = EffectType.Off };
            if (!map.TryGetValue(ec, out var list)) map[ec] = list = new();
            list.AddRange(Enumerable.Range(start, count));
        }
        foreach (var (old, _) in _groups) (old as IDisposable)?.Dispose();
        (_fieldFx as IDisposable)?.Dispose(); _fieldFx = null;
        _groups = map.Select(kv => (Effect.Create(kv.Key), kv.Value.ToArray())).ToList();
        bool needAudio = _groups.Any(g => g.fx.Cfg.Type is EffectType.Audio or EffectType.AudioPulse);
        if (needAudio && _audio == null) { try { _audio = new AudioAnalyzer(); } catch (Exception e) { Status = "audio: " + e.Message; } }
        if (!needAudio && _audio != null) { _audio.Dispose(); _audio = null; }
        foreach (var k in _sent.Keys.ToList()) _sent[k] = (Array.Empty<Rgb>(), 0); // force resend
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Glint lighting", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        using var timer = new PreciseSleep();
        double next = 0;
        var ctx = new FxContext();
        bool animatedOverride = false;
        double last = 0, fpsWindow = 0; int fpsCount = 0;
        Led[] sub = Array.Empty<Led>();
        Rgb[] tmp = Array.Empty<Rgb>();
        while (!_stop)
        {
            double cpu0 = CpuMeter.ThreadMs();
            if (_dirty) { _dirty = false; Rebuild(); }
            double now = sw.Elapsed.TotalSeconds;
            ctx.Dt = Math.Clamp(now - last, 0, 0.25); ctx.Time = now; last = now;
            ctx.Sensors = _sensors();
            if (_audio != null) (ctx.Spectrum, ctx.Level) = _audio.Analyze();

            var t0 = Stopwatch.GetTimestamp();
            foreach (var (fx, idx) in _groups)
            {
                if (sub.Length != idx.Length) { sub = new Led[idx.Length]; tmp = new Rgb[idx.Length]; }
                for (int i = 0; i < idx.Length; i++) sub[i] = _leds[idx[i]];
                fx.Render(ctx, sub, tmp);
                for (int i = 0; i < idx.Length; i++) _frame[idx[i]] = tmp[i];
            }
            double bri = Math.Clamp(_cfg.Lighting.Brightness, 0, 100) / 100.0;
            if (bri < 0.999) for (int i = 0; i < _frame.Length; i++) _frame[i] = _frame[i].Scale(bri);
            if (_identify.until > _clock.Elapsed.TotalSeconds)
            {
                bool on = (_clock.Elapsed.TotalSeconds * 3) % 1 < 0.6;           // 3 Hz white blink on the target
                foreach (var (zoneId, _, start, count, _, _) in _zones)
                {
                    bool hit = _identify.targets.Contains(zoneId);
                    for (int i = start; i < start + count; i++) _frame[i] = hit ? (on ? new Rgb(255, 255, 255) : Rgb.Black) : _frame[i].Scale(0.08);
                }
                animatedOverride = true;
            }
            else animatedOverride = false;
            if (_test is { } tp)
            {
                if (tp.Until <= _clock.Elapsed.TotalSeconds) _test = null;   // auto-off
                else foreach (var (zoneId, _, start, count, _, _) in _zones)
                    if (zoneId == tp.ZoneId) PaintTestPattern(_frame.AsSpan(start, count), tp.Count);
            }

            long ms = sw.ElapsedMilliseconds;
            foreach (var (dev, start, count) in _devSlices)
            {
                var span = _frame.AsSpan(start, count);
                var prev = _sent.GetValueOrDefault(dev);
                bool changed = prev.last == null || prev.last.Length != count || !span.SequenceEqual(prev.last);
                if (!changed && ms - prev.sentMs < 4000) continue; // keep-alive every 4 s (Commander/LNP time out)
                _writers[dev].Submit(span);
                if (prev.last == null || prev.last.Length != count) prev.last = new Rgb[count];
                span.CopyTo(prev.last);
                _sent[dev] = (prev.last, ms);
            }
            LastFrameMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            fpsCount++;
            if (now - fpsWindow >= 2) { ActualFps = fpsCount / (now - fpsWindow); fpsCount = 0; fpsWindow = now; }

            if (_fieldWantedUntil > _clock.Elapsed.TotalSeconds)
            {
                var L = _cfg.Lighting;
                _fieldFx ??= Effect.Create(L.Enabled ? L.Effect : new EffectConfig { Type = EffectType.Off });
                _fieldFx.Render(ctx, _fieldLeds, _field);
                var fb = new byte[_field.Length * 3];
                for (int i = 0; i < _field.Length; i++) { var c = _field[i].Scale(bri); fb[i * 3] = c.R; fb[i * 3 + 1] = c.G; fb[i * 3 + 2] = c.B; }
                Field = fb;
            }
            CpuMeter.Add("lighting render", CpuMeter.ThreadMs() - cpu0);
            bool animated = animatedOverride || _groups.Any(g => g.fx.Animated);
            double period = animated ? 1000.0 / Math.Clamp(_cfg.Lighting.Fps, 1, 60) : 500;
            // Fixed cadence on absolute deadlines (no drift); resync if we fell behind.
            next = Math.Max(next + period, sw.Elapsed.TotalMilliseconds - period);
            timer.Sleep(next - sw.Elapsed.TotalMilliseconds);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
        foreach (var w in _writers.Values) w.Dispose();
        _audio?.Dispose();
    }
}

/// <summary>
/// One thread per device: always sends the newest submitted frame, dropping stale ones, so a slow
/// controller (Core XT needs ~60 ms per frame) never holds back the others.
/// </summary>
sealed class DeviceWriter : IDisposable
{
    public readonly ILightDevice Dev;
    readonly Action<string> _err;
    readonly Thread _t;
    readonly AutoResetEvent _ev = new(false);
    readonly PreciseSleep _sleep = new();
    readonly object _lock = new();
    Rgb[] _pending = Array.Empty<Rgb>(), _sending = Array.Empty<Rgb>();
    bool _has;
    volatile bool _stop;
    public double LastMs, Fps;
    int _failures;
    public bool Paused { get; private set; }
    public long Sent;

    public DeviceWriter(ILightDevice dev, Action<string> err)
    {
        Dev = dev; _err = err;
        _t = new Thread(Run) { IsBackground = true, Name = "Glint " + dev.Id, Priority = ThreadPriority.AboveNormal };
        _t.Start();
    }

    public void Submit(ReadOnlySpan<Rgb> frame)
    {
        lock (_lock)
        {
            if (_pending.Length != frame.Length) _pending = new Rgb[frame.Length];
            frame.CopyTo(_pending);
            _has = true;
        }
        _ev.Set();
    }

    void Run()
    {
        var win = Stopwatch.StartNew(); int n = 0;
        while (!_stop)
        {
            _ev.WaitOne(1000);
            if (_stop) break;
            lock (_lock)
            {
                if (!_has) continue;
                (_pending, _sending) = (_sending, _pending);
                if (_sending.Length != _pending.Length) _pending = new Rgb[_sending.Length];
                _has = false;
            }
            long t0 = Stopwatch.GetTimestamp();
            double c0 = CpuMeter.ThreadMs();
            try { Dev.Write(_sending); Sent++; n++; _failures = 0; }
            catch (Exception e)
            {
                // Back off exponentially and give up after repeated failures: a device that's choking must not be hammered.
                _failures++;
                if (_failures >= 5) { _err($"{Dev.Name}: stopped after {_failures} failed writes ({e.Message}); restart Glint to retry"); Paused = true; return; }
                _err($"{Dev.Name}: {e.Message} (retrying)");
                Thread.Sleep(Math.Min(30_000, 500 * (1 << (_failures - 1))));
            }
            LastMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            CpuMeter.Add("device " + Dev.Id, CpuMeter.ThreadMs() - c0);
            int gap = Dev.MinGapMs;
            if (gap > 0) _sleep.Sleep(gap);
            if (win.ElapsedMilliseconds >= 2000) { Fps = n * 1000.0 / win.ElapsedMilliseconds; n = 0; win.Restart(); }
        }
    }

    public void Dispose() { _stop = true; _ev.Set(); _t.Join(1000); _ev.Dispose(); }
}
