using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glint.Core;

namespace Glint.GameOverlay;

/// <summary>Fake present source: records Start/Stop, lets a test push presents. Never touches ETW.</summary>
public sealed class FakePresentSource : IPresentSource
{
    Action<int, long, int>? _cb;
    public int Starts, Stops;
    public bool Running { get; private set; }
    public string Status => "";
    public double Frequency => 10_000_000;
    public long Clock;                                    // test-controlled time (ticks)
    public long Now() => Clock;
    public bool Start(Action<int, long, int> cb) { _cb = cb; Running = true; Starts++; return true; }
    public void Stop() { if (!Running) return; Running = false; Stops++; _cb = null; }
    public void Push(int pid, long ts, int stream) => _cb?.Invoke(pid, ts, stream);
    public void Dispose() => Stop();
}

/// <summary>Overlay checks for selftest / controltest, the README screenshot, a cost bench and an ETW smoke test.
/// Nothing here creates a window.</summary>
public static class OverlayTests
{
    const double F = 10_000_000;
    static long T(double sec) => (long)(sec * F);

    public static void Checks(Action<string, bool, string> C)
    {
        // ---------------- frame statistics
        {
            var s = new FrameStats(F);
            for (int i = 0; i <= 600; i++) s.Add(T(i / 60.0));
            var r = s.Compute(T(10));
            C("overlay frames: steady 60 fps → 60 fps, 16.67 ms, 1% low 60", Math.Abs(r.Fps - 60) < 0.2 && Math.Abs(r.FrameMs - 16.667) < 0.05 && Math.Abs(r.Low1 - 60) < 0.5,
              $"fps {r.Fps:0.00}, {r.FrameMs:0.000} ms, low {r.Low1:0.00}, {r.Samples} samples");
            var stale = s.Compute(T(13));
            C("overlay frames: no presents for 3 s → no reading", stale.Fps == 0 && stale.Low1 == 0, $"{stale.Fps}");

            // stutter: 60 fps with a 100 ms hitch every 50th frame (2% of frames) → 1% low ≈ 10 fps
            var st = new FrameStats(F); double t = 0;
            for (int i = 1; i <= 600; i++) { t += i % 50 == 0 ? 0.1 : 1 / 60.0; st.Add(T(t)); }
            var rs = st.Compute(T(t));
            C("overlay frames: stutter (100 ms every 50th frame) → 1% low ≈ 10 fps, average still > 40", Math.Abs(rs.Low1 - 10) < 0.5 && rs.Fps > 40 && rs.Fps < 60,
              $"fps {rs.Fps:0.0}, low {rs.Low1:0.0}");
            // a single hitch in 600 frames (0.17%) must not drag the 1% low down
            var one = new FrameStats(F); t = 0;
            for (int i = 1; i <= 600; i++) { t += i == 300 ? 0.1 : 1 / 60.0; one.Add(T(t)); }
            var ro = one.Compute(T(t));
            C("overlay frames: one hitch in 10 s → 1% low stays ≈ 60", Math.Abs(ro.Low1 - 60) < 1, $"low {ro.Low1:0.0}");
            var dup = new FrameStats(F); dup.Add(T(1)); dup.Add(T(1)); dup.Add(T(0.5)); dup.Add(T(1 + 1 / 60.0));
            C("overlay frames: duplicate / out-of-order timestamps are dropped", dup.Count == 2, $"{dup.Count}");
        }
        // ---------------- tracker: stream choice, target process
        {
            var tr = new PresentTracker(F) { TargetPid = 42 };
            for (int i = 0; i <= 120; i++)
            {
                tr.OnPresent(42, T(i / 60.0), PresentStream.DxgKrnl(184));
                tr.OnPresent(42, T(i / 60.0 + 0.001), PresentStream.DxgKrnl(215));   // same frame seen twice in the kernel
                tr.OnPresent(7, T(i / 120.0), PresentStream.Dxgi);                    // another process
            }
            var (k, ks) = tr.Read(T(2));
            C("overlay tracker: kernel-only app counted once per frame (not 120), other processes ignored", Math.Abs(k.Fps - 60) < 0.5 && ks >= 1000, $"{k.Fps:0.0} fps via {PresentStream.Name(ks)}");
            for (int i = 0; i <= 120; i++) tr.OnPresent(42, T(i / 60.0 + 0.002) , PresentStream.Dxgi);
            var (_, ds) = tr.Read(T(2));
            C("overlay tracker: DXGI presents win over kernel events", ds == PresentStream.Dxgi, PresentStream.Name(ds));
            // a pop-up / notification takes focus for a moment and draws nothing: the game's numbers stay
            tr.TargetPid = 43;
            var (pop, ps) = tr.Read(T(2));
            C("overlay tracker: a non-presenting window in front keeps showing the game (no blip)", ps != 0 && Math.Abs(pop.Fps - 60) < 0.5 && tr.ShownPid == 42, $"{pop.Fps:0.0} via pid {tr.ShownPid}");
            // the new foreground app presents: it takes over
            for (int i = 0; i <= 60; i++) tr.OnPresent(43, T(1 + i / 30.0), PresentStream.Dxgi);
            var (nw, _) = tr.Read(T(3));
            C("overlay tracker: a presenting foreground app takes over", Math.Abs(nw.Fps - 30) < 0.5 && tr.ShownPid == 43, $"{nw.Fps:0.0} via pid {tr.ShownPid}");
            // back to the game: its history survived the switch
            tr.TargetPid = 42;
            for (int i = 0; i <= 60; i++) tr.OnPresent(42, T(3 + i / 60.0), PresentStream.Dxgi);
            var (back, _) = tr.Read(T(4));
            C("overlay tracker: switching back keeps the game's history (1% low over 10 s)", Math.Abs(back.Fps - 60) < 0.5 && back.Samples > 100 && tr.ShownPid == 42, $"{back.Fps:0.0}, {back.Samples} samples");
            // nothing presents any more (desktop): after the stale window the overlay shows no FPS
            tr.TargetPid = 99;
            C("overlay tracker: nothing presenting for 2.5 s → no FPS", tr.Read(T(8)).stream == 0, "");
            // processes that are never foreground are ignored
            var t2 = new PresentTracker(F) { TargetPid = 5 };
            t2.OnPresent(6, T(0.1), PresentStream.Dxgi); t2.OnPresent(6, T(0.2), PresentStream.Dxgi);
            C("overlay tracker: background processes are not tracked", t2.Read(T(0.3)).stream == 0 && t2.Events == 0, $"{t2.Events}");
        }
        // ---------------- items: resolution + formatting
        {
            var d = new OverlayData
            {
                Frames = new FrameStats.Result(143.6, 6.944, 97.2, 1000),
                Sensors = new Dictionary<string, double> { ["cpu"] = 62.4, ["gpu"] = 71, ["xt:probe1"] = 31.04 },
                Rpm = new Dictionary<string, int> { ["xt:fan1"] = 1240 }, Duty = new Dictionary<string, int> { ["xt:fan1"] = 45 },
                FanNames = new Dictionary<string, string> { ["xt:fan1"] = "Front top" },
                Extra = s => s == "lhm:/gpu-nvidia/0/clock/0" ? (1850.4, "MHz") : null,
            };
            var rows = OverlayItems.Rows(OverlayConfig.DefaultItems(), d);
            C("overlay items: defaults show FPS, 1% low, CPU, GPU; missing hotspot / liquid are left out",
              string.Join(",", rows.Select(r => r.Label)) == "FPS,1% low,CPU,GPU" && rows[0].Value == "144" && rows[0].Hero && rows[1].Value == "97" && rows[2].Value == "62" && rows[2].Unit == "°C",
              string.Join(" | ", rows.Select(r => $"{r.Label}={r.Value}{r.Unit}")));
            var custom = OverlayItems.Rows(new List<OverlayItem>
            {
                new() { Source = "frametime" }, new() { Source = "fan:xt:fan1" }, new() { Source = "duty:xt:fan1", Label = "Front %", Color = "#00e5ff" },
                new() { Source = "lhm:/gpu-nvidia/0/clock/0", Label = "GPU clock" }, new() { Source = "xt:probe1" }, new() { Source = "nope" }, new() { Source = "time" },
            }, d);
            string J = string.Join(" | ", custom.Select(r => $"{r.Label}={r.Value}{r.Unit}"));
            C("overlay items: frametime 6.9 ms, fan rpm with its friendly name, duty %, catalog sensor via the pluggable resolver, unknown hidden",
              custom.Count == 6 && J.StartsWith("Frame=6.9ms | Front top=1240rpm | Front %=45% | GPU clock=1850MHz | xt:probe1=31°C | Time=") && custom[2].Color == "#00e5ff", J);
            var none = OverlayItems.Rows(new List<OverlayItem> { new() { Source = "fps" }, new() { Source = "low1" } }, new OverlayData());
            C("overlay items: no frame data → '–' (not hidden, not 0)", none.Count == 2 && none.All(r => r.Value == "–"), string.Join(",", none.Select(r => r.Value)));
        }
        // ---------------- hotkey
        {
            var a = Hotkey.Parse("Ctrl+Shift+O"); var b = Hotkey.Parse("alt + f10"); var c = Hotkey.Parse("Win+Shift+5");
            C("overlay hotkey: Ctrl+Shift+O, Alt+F10, Win+Shift+5 parse; bad ones are rejected",
              a == (Hotkey.Ctrl | Hotkey.Shift, 'O') && b == (Hotkey.Alt, 0x79u) && c == (Hotkey.Win | Hotkey.Shift, '5')
              && Hotkey.Parse("Ctrl+Shift") == null && Hotkey.Parse("Ctrl+Nope") == null && Hotkey.Error("O") != null && Hotkey.Error("F9") == null && Hotkey.Error("") == null,
              $"{a} {b} {c}");
        }
        // ---------------- validation
        {
            var good = GlintConfig.Defaults();
            C("overlay validation: defaults are valid", ConfigValidator.Validate(good).Count == 0, string.Join("; ", ConfigValidator.Validate(good)));
            var bad = GlintConfig.Defaults();
            bad.Overlay = new OverlayConfig
            {
                Position = "middle", Scale = 9, Opacity = 0, BackgroundOpacity = 2, Accent = "red", Layout = "big", Hotkey = "Ctrl+Nope", OffsetX = -5000,
                Items = new() { new() { Source = "" }, new() { Source = "cpu", Label = new string('x', 40) }, new() { Source = "gpu", Color = "blue" }, new() { Source = "max(cpu,gpu)" } },
            };
            var e = ConfigValidator.Validate(bad);
            string[] expect = { "overlay.position", "overlay.scale", "overlay.opacity", "overlay.backgroundOpacity", "overlay.accent", "overlay.layout", "overlay.hotkey", "overlay.offsetX",
                                "items[0].source", "items[1].label", "items[2].color", "items[3].source" };
            C("overlay validation: catches position, scale, opacity, background, accent, layout, hotkey, offset, empty source, long label, bad colour, expressions",
              expect.All(x => e.Any(y => y.Contains(x))), "missing: " + string.Join(", ", expect.Where(x => !e.Any(y => y.Contains(x)))));
            var unk = GlintConfig.Defaults(); unk.Overlay.Items.Add(new() { Source = "lhm:/gpu-nvidia/0/clock/0" });
            var v = ConfigValidator.Check(unk, new ValidationContext { Sensors = new[] { "cpu", "gpu" } });
            C("overlay validation: a sensor that isn't reading is a warning, not an error (catalog ids plug in later)", v.Ok && v.Warnings.Any(w => w.Contains("lhm:/gpu-nvidia")), string.Join("; ", v.Errors));
            var nul = JsonSerializer.Deserialize<GlintConfig>("""{"profiles":{"a":[]},"activeProfile":"a"}""", GlintConfig.Json)!;
            C("overlay validation: an old config without 'overlay' gets the defaults", nul.Overlay.Items.Count == 6 && !nul.Overlay.Enabled && ConfigValidator.Validate(nul).Count == 0, string.Join("; ", ConfigValidator.Validate(nul)));
        }
        // ---------------- controller: ETW only while shown, never a window in tests
        {
            var cfg = GlintConfig.Defaults();
            using var svc = new GlintService(cfg, new Rig(cfg, useLhm: false, hardware: false)) { NoSave = true };
            var fake = new FakePresentSource();
            OffscreenSurface? surf = null;
            var fg = new ForegroundInfo((IntPtr)1, 4242, new Rectangle(100, 100, 800, 600), new Rectangle(0, 0, 2560, 1440), new Rectangle(0, 0, 2560, 1400), 1.5, "GameWnd");
            using var ov = new OverlayController(svc, fake, () => surf = new OffscreenSurface(), a => a(), () => fg, _ => "game", useTimer: false);
            ov.Tick();
            C("overlay: hidden → ETW session never started, no surface", fake.Starts == 0 && !fake.Running && surf == null && !ov.Visible, $"starts {fake.Starts}");
            var on = Clone(svc.Config); on.Overlay.Enabled = true; svc.Apply(on);
            ov.Tick();
            C("overlay: toggled on (via config) → ETW started once, drawn off-screen", fake.Starts == 1 && fake.Running && ov.Visible && surf?.Presents == 1, $"starts {fake.Starts}, presents {surf?.Presents}");
            for (int i = 0; i <= 120; i++) fake.Push(4242, T(i / 60.0), PresentStream.Dxgi);
            fake.Clock = T(2);
            ov.Tick();
            var stats = JsonSerializer.SerializeToNode(ov.Stats())!;
            C("overlay: foreground presents → FPS on the overlay and in stats", ov.LastRows.FirstOrDefault().Value == "60" && (double?)stats["fps"] == 60 && (string?)stats["presentSource"] == "dxgi" && (string?)stats["process"] == "game",
              stats.ToJsonString());
            int presents = surf!.Presents;
            ov.Tick();
            C("overlay: unchanged text → no redraw", surf.Presents == presents, $"{presents} → {surf.Presents}");
            C("overlay: anchored to the foreground window's monitor work area, DPI-scaled offset", surf.Bounds.X == 24 && surf.Bounds.Y == 24, surf.Bounds.ToString());
            var og = Clone(svc.Config); og.Overlay.OnlyWhenGame = true; og.Overlay.Position = "bottom-right"; svc.Apply(og);
            ov.Tick();
            C("overlay: onlyWhenGame + windowed app → hidden and ETW stopped", !ov.Visible && !fake.Running && !surf.Shown && fake.Stops == 1, $"visible {ov.Visible}, running {fake.Running}");
            fg = fg with { Window = new Rectangle(0, 0, 2560, 1440) };
            ov.Tick();
            C("overlay: onlyWhenGame + borderless fullscreen → shown bottom-right of the monitor, ETW running", ov.Visible && fake.Running && surf.Shown && surf.Bounds.Right == 2560 - 24 && surf.Bounds.Bottom == 1440 - 24, surf.Bounds.ToString());
            var desk = fg with { Pid = 99, ClassName = "Progman" };
            fg = desk; ov.Tick();
            C("overlay: desktop in front → not a game", !ov.Visible && !fake.Running, "");
            var noFps = Clone(svc.Config); noFps.Overlay.OnlyWhenGame = false; noFps.Overlay.Items = new() { new() { Source = "cpu" }, new() { Source = "time" } }; svc.Apply(noFps);
            ov.Tick();
            C("overlay: no FPS items → shown without an ETW session", ov.Visible && !fake.Running, $"running {fake.Running}");
            var off = Clone(svc.Config); off.Overlay.Enabled = false; svc.Apply(off);
            ov.Tick();
            C("overlay: toggled off → ETW stopped, surface released", !fake.Running && !ov.Visible && surf.Last == null, $"starts {fake.Starts}, stops {fake.Stops}");
            svc.Overlay = null;
        }
        // ---------------- rendering (off-screen)
        {
            var rows = SampleRows("144");
            using var bmp = OverlayRenderer.Render(rows, new OverlayStyle(1, 0.72, null, false));
            var corner = bmp.GetPixel(0, 0); var mid = bmp.GetPixel(bmp.Width - 6, bmp.Height / 2);
            bool grad = false;
            for (int x = 0; x < bmp.Width && !grad; x++) for (int y = 0; y < 45 && !grad; y++) { var p = bmp.GetPixel(x, y); grad = p.A > 200 && p.R > 200 && p.G < 140; }
            C("overlay render: transparent rounded corners, translucent dark panel, gradient FPS", corner.A == 0 && mid.A is > 150 and < 200 && mid.R < 30 && grad && bmp.Width is > 100 and < 400,
              $"{bmp.Width}x{bmp.Height}, corner a{corner.A}, panel {mid}");
            var w1 = OverlayRenderer.Measure(SampleRows("143"), new OverlayStyle(1, 0.72, null, true));
            var w2 = OverlayRenderer.Measure(SampleRows("111"), new OverlayStyle(1, 0.72, null, true));
            var big = OverlayRenderer.Measure(rows, new OverlayStyle(2, 0.72, null, false));
            C("overlay render: width doesn't jitter as digits change; scale 2 doubles the size", w1 == w2 && Math.Abs(big.Width - 2 * bmp.Width) <= 3, $"{w1} vs {w2}; {bmp.Size} → {big}");
            C("overlay render: nothing to show → empty", OverlayRenderer.Measure(Array.Empty<OverlayRow>(), new OverlayStyle(1, 1, null, false)).IsEmpty, "");
        }
    }

    static GlintConfig Clone(GlintConfig c) => JsonSerializer.Deserialize<GlintConfig>(JsonSerializer.Serialize(c, GlintConfig.Json), GlintConfig.Json)!;

    static List<OverlayRow> SampleRows(string fps) => OverlayItems.Rows(
        new List<OverlayItem> { new() { Source = "fps" }, new() { Source = "frametime" }, new() { Source = "low1" }, new() { Source = "cpu" }, new() { Source = "gpu" }, new() { Source = "gpu.hotspot" }, new() { Source = "liquid" } },
        new OverlayData
        {
            Frames = new FrameStats.Result(double.Parse(fps), 1000 / double.Parse(fps), 118, 1000),
            Sensors = new Dictionary<string, double> { ["cpu"] = 62, ["gpu"] = 71, ["gpu.hotspot"] = 84, ["liquid"] = 33.4 },
        });

    /// <summary>README screenshot: both layouts over a game-like backdrop. Off-screen only.</summary>
    public static void Screenshot(string path)
    {
        const int W = 1200, H = 440;
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var sky = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(0x1b, 0x2a, 0x4a), Color.FromArgb(0xe0, 0x7a, 0x4f), 90f))
            {
                sky.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0x14, 0x1c, 0x38), Color.FromArgb(0x5a, 0x3a, 0x6e), Color.FromArgb(0xd8, 0x76, 0x52), Color.FromArgb(0xf2, 0xb0, 0x6a) }, Positions = new[] { 0f, 0.45f, 0.78f, 1f } };
                g.FillRectangle(sky, 0, 0, W, H);
            }
            using (var sun = new GraphicsPath())
            {
                sun.AddEllipse(760, 210, 260, 260);
                using var pb = new PathGradientBrush(sun) { CenterColor = Color.FromArgb(230, 255, 214, 150), SurroundColors = new[] { Color.FromArgb(0, 255, 160, 90) } };
                g.FillPath(pb, sun);
            }
            void Ridge(Color c, int baseY, double amp, double freq, double phase)
            {
                var pts = new List<PointF> { new(0, H) };
                for (int x = 0; x <= W; x += 8) pts.Add(new PointF(x, (float)(baseY - amp * (Math.Sin(x * freq + phase) * 0.6 + Math.Sin(x * freq * 2.7 + phase * 1.3) * 0.4))));
                pts.Add(new PointF(W, H));
                using var b = new SolidBrush(c);
                g.FillPolygon(b, pts.ToArray());
            }
            Ridge(Color.FromArgb(0x3b, 0x26, 0x4a), 330, 40, 0.006, 1.0);
            Ridge(Color.FromArgb(0x24, 0x18, 0x33), 370, 34, 0.009, 2.2);
            Ridge(Color.FromArgb(0x12, 0x0d, 0x1c), 410, 26, 0.013, 0.4);

            using var detailed = OverlayRenderer.Render(SampleRows("144"), new OverlayStyle(1.5, 0.72, null, false));
            g.DrawImage(detailed, 28, 28, detailed.Width, detailed.Height);
            using var compact = OverlayRenderer.Render(OverlayItems.Rows(new List<OverlayItem> { new() { Source = "fps" }, new() { Source = "low1" }, new() { Source = "cpu" }, new() { Source = "gpu" }, new() { Source = "liquid" } },
                new OverlayData { Frames = new FrameStats.Result(144, 6.9, 118, 1000), Sensors = new Dictionary<string, double> { ["cpu"] = 62, ["gpu"] = 71, ["liquid"] = 33.4 } }), new OverlayStyle(1.5, 0.72, null, true));
            g.DrawImage(compact, W - 28 - compact.Width, 28, compact.Width, compact.Height);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// Cost of the overlay with a fake 144 fps source: CPU of the overlay timer (tick + text + off-screen render)
    /// and of feeding presents, and private memory while off / on / off again. No window, no ETW.
    /// </summary>
    public static int Bench(string outPath, int seconds = 10)
    {
        Environment.SetEnvironmentVariable("GLINT_CONFIG_DIR", Path.Combine(Path.GetTempPath(), "GlintOverlayBench_" + Environment.ProcessId));
        var proc = Process.GetCurrentProcess();
        long Kb() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); proc.Refresh(); return proc.PrivateMemorySize64 / 1024; }
        double CpuMs() { proc.Refresh(); return proc.TotalProcessorTime.TotalMilliseconds; }
        // the tray app has GDI+ up already (tray icon): warm it here too, so the numbers are the overlay's own
        using (var wb = new Bitmap(32, 32)) using (var wg = Graphics.FromImage(wb)) { wg.SmoothingMode = SmoothingMode.AntiAlias; wg.FillEllipse(Brushes.White, 2, 2, 28, 28); }
        var cfg = GlintConfig.Defaults();
        cfg.Lighting.Enabled = false;
        using var svc = new GlintService(cfg, new Rig(cfg, useLhm: false, hardware: false)) { NoSave = true };
        var fake = new FakePresentSource();
        var fg = new ForegroundInfo((IntPtr)1, 4242, new Rectangle(0, 0, 2560, 1440), new Rectangle(0, 0, 2560, 1440), new Rectangle(0, 0, 2560, 1400), 1.5, "GameWnd");
        using var ov = new OverlayController(svc, fake, () => new OffscreenSurface(), a => a(), () => fg, _ => "game");
        svc.Overlay = ov;
        Thread.Sleep(1500);
        long kbOff = Kb(); long mgOff = GC.GetTotalMemory(true) / 1024;
        double busy0 = ov.BusyMs, c0 = CpuMs(); var sw = Stopwatch.StartNew();
        Thread.Sleep(seconds * 1000 / 2);
        double offBusy = ov.BusyMs - busy0, offProc = (CpuMs() - c0) / sw.Elapsed.TotalMilliseconds * 100;

        var on = Clone(svc.Config); on.Overlay.Enabled = true; svc.Apply(on);
        bool stop = false; double feedMs = 0; long presents = 0;
        var feeder = new Thread(() =>
        {
            var clock = Stopwatch.StartNew(); var rnd = new Random(1); double next = 0;
            while (!Volatile.Read(ref stop))
            {
                next += (rnd.NextDouble() < 0.97 ? 6.94 : 25) / 1000;  // ~140 fps with the odd hitch, so the text keeps changing
                while (clock.Elapsed.TotalSeconds < next) Thread.Sleep(0);
                long p0 = Stopwatch.GetTimestamp();
                fake.Clock = (long)(clock.Elapsed.TotalSeconds * F);
                fake.Push(4242, fake.Clock, PresentStream.Dxgi); presents++;
                feedMs += Stopwatch.GetElapsedTime(p0).TotalMilliseconds;
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        feeder.Start();
        Thread.Sleep(1000);
        double busy1 = ov.BusyMs, feed1 = feedMs; long pr1 = presents; int rd1 = ov.Redraws; sw.Restart();
        Thread.Sleep(seconds * 1000);
        double secs = sw.Elapsed.TotalSeconds;
        double onBusy = ov.BusyMs - busy1, onFeed = feedMs - feed1; long onPresents = presents - pr1; int redraws = ov.Redraws - rd1;
        long kbOn = Kb();
        var stats = JsonSerializer.SerializeToNode(ov.Stats());
        Volatile.Write(ref stop, true); feeder.Join();
        var off = Clone(svc.Config); off.Overlay.Enabled = false; svc.Apply(off);
        Thread.Sleep(500);
        long kbAfter = Kb();
        // one redraw in isolation (detailed layout, 7 rows, 150% scaling)
        var rows = SampleRows("144"); var style = new OverlayStyle(1.5, 0.72, null, false);
        for (int i = 0; i < 20; i++) OverlayRenderer.Render(rows, style).Dispose();
        var rs = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) OverlayRenderer.Render(rows, style).Dispose();
        double renderMs = rs.Elapsed.TotalMilliseconds / 200;
        var o = new
        {
            hiddenOverlayBusyMs = Math.Round(offBusy, 3),
            hiddenProcessCpuPercentOfOneCore = Math.Round(offProc, 3),
            visibleOverlayPercentOfOneCore = Math.Round(onBusy / (secs * 1000) * 100, 3),
            presentHandlingPercentOfOneCore = Math.Round(onFeed / (secs * 1000) * 100, 3),
            presentsPerSec = Math.Round(onPresents / secs, 1),
            redrawsPerSec = Math.Round(redraws / secs, 2),
            renderMsPerRedraw = Math.Round(renderMs, 3),
            managedKBHidden = mgOff, managedKBAfterHide = GC.GetTotalMemory(true) / 1024,
            privateKBHidden = kbOff, privateKBVisible = kbOn, privateKBAfterHide = kbAfter, visibleDeltaKB = kbOn - kbOff,
            stats,
            note = "Fake ~140 fps source (wall-time of the overlay timer + redraws, and of the per-present tracker call that the ETW callback makes). " +
                   "Off-screen surface allocates a bitmap per redraw; the real window reuses one DIB. ETW kernel/session cost not included.",
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        svc.Overlay = null;
        return 0;
    }

    /// <summary>
    /// Read-only ETW check (needs admin): start a separate session for a few seconds, count present events from
    /// any process, stop it, and confirm it is gone. Writes a JSON report; exit code 0 = events seen.
    /// </summary>
    public static int EtwSmoke(string outPath, int seconds = 3)
    {
        const string name = "Glint-Overlay-Smoke";
        var src = new EtwPresentSource(name);
        var perStream = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        var pids = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        long first = 0, last = 0;
        bool ok = src.Start((pid, ts, stream) =>
        {
            perStream.AddOrUpdate(stream, 1, (_, n) => n + 1);
            pids.AddOrUpdate(pid, 1, (_, n) => n + 1);
            if (first == 0) first = ts; last = ts;
        });
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        if (ok) Thread.Sleep(seconds * 1000);
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
        src.Stop();
        bool gone = !EtwPresentSource.StopSession(name);   // stopping again must fail: the session no longer exists
        var top = pids.OrderByDescending(p => p.Value).Take(5).Select(p =>
        {
            string n; try { using var pr = Process.GetProcessById(p.Key); n = pr.ProcessName; } catch { n = "?"; }
            return new { pid = p.Key, process = n, events = p.Value };
        }).ToList();
        var o = new
        {
            started = ok, status = src.Status, seconds, events = src.Events,
            streams = perStream.ToDictionary(k => PresentStream.Name(k.Key), k => k.Value),
            topProcesses = top,
            timestampsAreQpc = first > 0 && Math.Abs((last - first) / (double)Stopwatch.Frequency) <= seconds + 2,
            sessionGoneAfterStop = gone,
            cpuMsWhileRunning = Math.Round(cpu, 1),
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        return ok && src.Events > 0 && gone ? 0 : 1;
    }

    /// <summary>Control API checks (controltest): overlay.get / set / toggle / stats, validation, MCP tool list.</summary>
    public static void ApiChecks(GlintService svc, Func<string, object?, JsonNode?> Call, Action<string, bool, string> C, JsonArray? tools)
    {
        string J(JsonNode? n) => n?.ToJsonString() ?? "null";
        bool Ok(JsonNode? n) => (bool?)n?["result"]?["ok"] == true;
        bool Rejected(JsonNode? n) => (bool?)n?["result"]?["ok"] == false && n?["result"]?["errors"]?.AsArray().Count > 0;
        string Saved() => JsonSerializer.Serialize(svc.Config, GlintConfig.Json);
        var fake = new FakePresentSource();
        var fg = new ForegroundInfo((IntPtr)1, 4242, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 1, "GameWnd");
        var ov = new OverlayController(svc, fake, () => new OffscreenSurface(), a => a(), () => fg, _ => "game");
        svc.Overlay = ov;
        try
        {
            var names = new[] { "overlay.get", "overlay.set", "overlay.toggle", "overlay.stats" };
            C("MCP exposes overlay.get/set/toggle/stats with schemas", names.All(m => tools?.Any(t => (string?)t!["name"] == "glint_" + m.Replace('.', '_') && t["inputSchema"]?["type"] != null && ((string?)t["description"])?.Length > 20) == true), "");
            var g = Call("overlay.get", null)?["result"];
            C("overlay.get returns the defaults", (string?)g?["hotkey"] == "Ctrl+Shift+O" && g?["items"]?.AsArray().Count == 6 && (bool?)g?["enabled"] == false, J(g));
            C("overlay hidden → no ETW session", fake.Starts == 0, $"{fake.Starts}");
            var t = Call("overlay.toggle", null);
            bool started = SpinWait.SpinUntil(() => fake.Running && ov.Visible, 3000);
            C("overlay.toggle shows it and starts the (fake) present source", Ok(t) && (bool?)t?["result"]?["enabled"] == true && started && svc.Config.Overlay.Enabled, J(t));
            for (int i = 0; i <= 120; i++) fake.Push(4242, T(i / 90.0), PresentStream.Dxgi);
            fake.Clock = T(120 / 90.0);
            SpinWait.SpinUntil(() => (double?)JsonSerializer.SerializeToNode(ov.Stats())?["fps"] > 0, 3000);
            var st = Call("overlay.stats", null)?["result"];
            C("overlay.stats reports fps / frame time / 1% low and the target process", Math.Abs(((double?)st?["fps"] ?? 0) - 90) < 0.5 && (double?)st?["frameTimeMs"] > 11 && (double?)st?["low1"] > 0 && (string?)st?["process"] == "game" && (string?)st?["etw"] == "running", J(st));
            var s = Call("overlay.set", new { position = "top-right", scale = 1.25, layout = "compact", items = new object[] { new { source = "fps" }, new { source = "cpu", label = "CPU temp", color = "#ff6a3d" } } });
            C("overlay.set partial update (other fields kept)", Ok(s) && svc.Config.Overlay.Position == "top-right" && svc.Config.Overlay.Scale == 1.25 && svc.Config.Overlay.Items.Count == 2 && svc.Config.Overlay.Hotkey == "Ctrl+Shift+O" && svc.Config.Overlay.Enabled, J(s));
            var before = Saved();
            var bad = Call("overlay.set", new { position = "middle", scale = 10, accent = "orange" });
            C("overlay.set rejects bad values without saving", Rejected(bad) && Saved() == before && J(bad).Contains("position") && J(bad).Contains("scale") && J(bad).Contains("accent"), J(bad));
            var unk = Call("overlay.set", new { sizee = 2 });
            C("overlay.set rejects unknown fields", Rejected(unk) && Saved() == before, J(unk));
            var hk = Call("overlay.set", new { hotkey = "Ctrl+" });
            C("overlay.set rejects a bad hotkey", Rejected(hk) && Saved() == before, J(hk));
            var patch = Call("config.patch", new { path = "overlay.backgroundOpacity", value = 0.4 });
            C("config.patch reaches overlay fields (validated)", Ok(patch) && svc.Config.Overlay.BackgroundOpacity == 0.4 && Rejected(Call("config.patch", new { path = "overlay.opacity", value = 3 })), J(patch));
            var off = Call("overlay.toggle", new { enabled = false });
            bool stopped = SpinWait.SpinUntil(() => !fake.Running && !ov.Visible, 3000);
            C("overlay.toggle enabled=false hides it and stops the present source", Ok(off) && stopped && !svc.Config.Overlay.Enabled, J(off));
            var schema = Call("config.schema", null)?["result"]?["fields"]?["overlay"]?["fields"];
            C("config.schema documents the overlay", schema?["position"]?["enum"]?.AsArray().Count == 4 && schema?["scale"]?["max"] != null && schema?["items"]?["fields"]?["source"] != null, J(schema).Length.ToString());
        }
        finally { svc.Overlay = null; ov.Dispose(); }
    }
}
