using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Glint.Devices;

namespace Glint.Core;

/// <summary>Offline checks for the v0.2 features: LED-count test pattern, setup check, update check, OpenRGB bridge.
/// No hardware, no network beyond an in-process fake server on 127.0.0.1.</summary>
static class SelfTestV02
{
    public static void Run(Action<string, bool, string> C)
    {
        Run("test pattern", C, TestPattern);
        Run("setup check", C, Setup);
        Run("update check", C, Updates);
        Run("OpenRGB bridge", C, OpenRgb);
        Run("config v0.2", C, Config);
        Run("window size", C, WindowSize);
    }

    static void WindowSize(Action<string, bool, string> C)
    {
        var big = new System.Drawing.Rectangle(0, 0, 3840, 2100);
        var a = Glint.UI.WindowFit.Fit(1560, 960, 144, big);      // 4K at 150 %
        var b = Glint.UI.WindowFit.Fit(1560, 960, 96, new System.Drawing.Rectangle(0, 0, 1366, 728));   // small laptop
        C("window size: default is in 96-dpi units, scaled by the monitor's DPI (1560 x 960 -> 2340 x 1440 at 150 %)", a.Width == 2340 && a.Height == 1440, $"{a}");
        C("window size: never bigger than 94 % of the screen's working area", b.Width == 1284 && b.Height == 684, $"{b}");
    }

    static void Run(string what, Action<string, bool, string> C, Action<Action<string, bool, string>> f)
    {
        try { f(C); } catch (Exception e) { C($"{what}: checks ran", false, e.ToString()); }
    }

    // ------------------------------------------------------------------ LED-count test pattern

    static void TestPattern(Action<string, bool, string> C)
    {
        var buf = new Rgb[30];
        LightingEngine.PaintTestPattern(buf, 20);
        var white = new Rgb(255, 255, 255); var red = new Rgb(255, 0, 0);
        bool blocks = Enumerable.Range(1, 18).All(i => buf[i] == LightingEngine.TestPatternBlocks[i / 8 % LightingEngine.TestPatternBlocks.Length])
                      && buf[7] != buf[8] && buf[15] != buf[16] && buf[1] == buf[7];
        C("test pattern: LED 1 white, LED <count> red, a new colour every 8 LEDs, dark past the count",
            buf[0] == white && buf[19] == red && blocks && buf.Skip(20).All(c => c == Rgb.Black) && !buf.Skip(1).Take(18).Any(c => c == white || c == red), string.Join(" ", buf.Select(c => c.ToString())));
        var over = new Rgb[10];
        LightingEngine.PaintTestPattern(over, 16);
        C("test pattern: count longer than the zone lights it all, no red end (restart needed for more LEDs)", over[0] == white && over.Skip(1).All(c => c != red && c != Rgb.Black), string.Join(" ", over.Select(c => c.ToString())));

        // through the real lighting engine: only the chosen zone changes, and it switches itself off
        var cfg = GlintConfig.Defaults();
        cfg.Lighting.Effect = new EffectConfig { Type = EffectType.Static, Colors = new() { "#102030" } };
        cfg.Lighting.Overrides.Clear();
        var rig = new Rig(cfg, useLhm: false, hardware: false);
        rig.Lights.Add(new SelfTest.FakeLight("tp", new LightZone("tp:fan1", "Fan", 8, ZoneKind.Ring), new LightZone("tp:argb1", "ARGB 1", 30)));
        using var eng = new LightingEngine(rig, cfg, () => new Dictionary<string, double>());
        eng.Start();
        bool known = eng.TestPattern("tp:argb1", 20);
        Thread.Sleep(800);
        var f = eng.Preview.ToArray();
        var expect = new Rgb[30]; LightingEngine.PaintTestPattern(expect, 20);
        var bg = Rgb.Parse("#102030");
        C("test pattern in the engine: the zone shows the pattern, other zones keep their effect",
            known && f.Skip(8).Take(30).SequenceEqual(expect) && f.Take(8).All(c => c == bg) && eng.TestPatternZone == "tp:argb1", string.Join(" ", f.Skip(8).Take(4)) + " | " + f[0]);
        C("test pattern: unknown zone refused", !eng.TestPattern("nope:zone", 10), "");
        eng.TestPattern("tp:argb1", 25);
        Thread.Sleep(800);
        f = eng.Preview.ToArray();
        C("test pattern: new count applies live", f[8 + 24] == red && f[8 + 19] != red && f[8 + 25] == Rgb.Black, $"{f[8 + 19]} {f[8 + 24]} {f[8 + 25]}");
        eng.TestPattern("tp:argb1", 20, seconds: 0.4);
        Thread.Sleep(1300);
        f = eng.Preview.ToArray();
        C("test pattern: times out by itself", eng.TestPatternZone == null && f.Skip(8).All(c => c == bg), $"{eng.TestPatternZone} {f[8]}");
        eng.TestPattern("tp:argb1", 20); eng.StopTestPattern();
        Thread.Sleep(800);
        C("test pattern: stop restores the effect", eng.Preview.Skip(8).All(c => c == bg), eng.Preview[8].ToString());
        C("test pattern never runs longer than 60 s", LightingEngine.TestPatternSeconds == 60, "");
    }

    // ------------------------------------------------------------------ setup check

    sealed class FakeEnv : SetupEnv
    {
        public string[] Procs = Array.Empty<string>();
        public (string, string)[] Runs = Array.Empty<(string, string)>();
        public bool Pawn = true; public string? Wv = "142.0.0.0"; public bool? Task = true;
        public override IEnumerable<string> ProcessNames() => Procs;
        public override IEnumerable<(string Name, string Command)> RunEntries() => Runs;
        public override bool PawnIoInstalled() => Pawn;
        public override string? WebView2Version() => Wv;
        public override bool? StartupTask() => Task;
    }

    static void Setup(Action<string, bool, string> C)
    {
        var bad = new SetupCheck.SetupInput
        {
            Elevated = false, Ring0 = false, SmbusOk = false, SmbusError = "Glint is not running as administrator", Cpu = null,
            Log = new() { "[Plugin: Demo hub] init declined (device not recognised)", "LHM: 1 Cpu; 0 temps",
                          "[Some DRAM] init declined (device not recognised)",                      // SMBus probe found none: fine
                          "[Keyboard] init declined (device not recognised)", "Plugin: Keyboard [experimental]: kb 5 LEDs" }, // spare interface: fine
            Plugins = new()
            {
                new("ok.js", "effect", "Fine", true, "ok"),
                new("ram.js", "smbus", "Some DRAM", false, "Needs your approval", NeedsApproval: true),
                new("broken.js", "device", "Broken", false, "SyntaxError (line 3)"),
                new("off.js", "device", "Off", false, "disabled"),
            },
            Update = new UpdateInfo("0.2.0", "0.3.0", UpdateChecker.RepoUrl + "/releases/tag/v0.3.0", true, DateTime.UtcNow, null), UpdatesOn = true,
        };
        var env = new FakeEnv
        {
            Procs = new[] { "explorer", "iCUE", "OpenRGB" }, Runs = new[] { ("SignalRGB", "\"C:\\Program Files\\WhirlwindFX\\SignalRgbLauncher.exe\" --silent") },
            Pawn = false, Wv = null, Task = false,
        };
        var r = SetupCheck.Run(bad, env);
        SetupItem? I(string id) => r.Items.FirstOrDefault(i => i.Id == id);
        string Dump() => string.Join(" | ", r.Items.Select(i => $"{i.Id}={i.Status}"));
        C("setup: not admin → fail with a restart-as-admin fix", I("elevated") is { Status: "fail", Fix: "restartAdmin", Important: true }, Dump());
        C("setup: PawnIO missing → fail, fix opens pawnio.eu", I("pawnio") is { Status: "fail", Fix: "openUrl" } p && p.FixArg == SetupCheck.PawnIoUrl, Dump());
        C("setup: WebView2 missing → fail", I("webview2")?.Status == "fail", Dump());
        C("setup: CPU temperature missing → fail (important)", I("cpuTemp") is { Status: "fail", Important: true }, Dump());
        C("setup: running iCUE → fail with startup-apps fix", I("conflict:corsair-icue") is { Status: "fail", Fix: "startupApps", Important: true }, Dump());
        C("setup: SignalRGB only in the Run key → warn naming the entry", I("conflict:signalrgb") is { Status: "warn" } s && s.Detail.Contains("\"SignalRGB\""), Dump());
        C("setup: OpenRGB running without the bridge is a conflict", I("conflict:openrgb")?.Status == "fail", Dump());
        C("setup: plugin needing approval → warn, fix goes to the Plugins page", I("plugins:approval") is { Status: "warn", Fix: "page", FixArg: "plugins" }, Dump());
        C("setup: broken plugin → fail; switched-off plugins ignored", I("plugins:failed") is { Status: "fail" } pf && pf.Detail.Contains("broken.js") && !pf.Detail.Contains("off.js") && pf.Title.StartsWith("1 "), I("plugins:failed")?.Detail ?? Dump());
        C("setup: a declined device → warn quoting the log", I("devices") is { Status: "warn" } d && d.Detail.Contains("init declined"), Dump());
        C("setup: RAM probes finding nothing and a working device's spare interfaces are not flagged", I("devices") is { } dd && dd.Title.StartsWith("1 ") && !dd.Detail.Contains("Some DRAM") && !dd.Detail.Contains("[Keyboard]"), I("devices")?.Title ?? Dump());
        C("setup: no startup task → warn", I("startupTask")?.Status == "warn", Dump());
        C("setup: update available → warn opening the release page", I("version") is { Status: "warn", Fix: "openUrl" } v && v.FixArg!.StartsWith(UpdateChecker.RepoUrl) && v.Title.Contains("0.3.0"), Dump());
        C("setup: summary counts + important flag", r.ImportantFail && r.Fail == r.Items.Count(i => i.Status == "fail") && r.Fail >= 7 && r.Ok + r.Warn + r.Fail == r.Items.Count, $"{r.Ok}/{r.Warn}/{r.Fail}");
        C("setup: every item explains itself in plain language", r.Items.All(i => i.Detail.Length > 25 && i.Title.Length > 3 && i.Status is "ok" or "warn" or "fail"), "");
        C("setup: fix URLs are only Glint's own / PawnIO / WebView2", r.Items.Where(i => i.Fix == "openUrl").All(i => SetupCheck.AllowedUrl(i.FixArg)) && !SetupCheck.AllowedUrl("https://example.com/x"), "");

        var good = new SetupCheck.SetupInput
        {
            Elevated = true, Ring0 = true, SmbusOk = true, SmbusError = null, Cpu = 48.5, BridgeOn = true,
            Bridge = new OpenRgbStatus(true, true, "127.0.0.1:6742", 4, new() { "orgb-123456 Strip" }, new(), null),
            Plugins = new() { new("ok.js", "effect", "Fine", true, "ok") }, Log = new() { "Plugin: Corsair Commander Core: cc 40 LEDs" },
            Update = new UpdateInfo("0.2.0", "0.2.0", UpdateChecker.ReleasesUrl, false, DateTime.UtcNow, null), UpdatesOn = true,
        };
        var r2 = SetupCheck.Run(good, new FakeEnv { Procs = new[] { "explorer", "OpenRGB" } });
        C("setup: a healthy PC is all ok (OpenRGB is fine while the bridge uses it)", r2.Fail == 0 && r2.Warn == 0 && !r2.ImportantFail && r2.Items.Any(i => i.Id == "conflicts" || i.Id == "conflict:openrgb") && r2.Items.Any(i => i.Id == "openrgb" && i.Status == "ok"),
            string.Join(" | ", r2.Items.Where(i => i.Status != "ok").Select(i => i.Id)));
        var r3 = SetupCheck.Run(new SetupCheck.SetupInput { Elevated = true, Ring0 = true, SmbusOk = true, Cpu = 40, BridgeOn = true, Bridge = new OpenRgbStatus(true, false, "127.0.0.1:6742", 0, new(), new(), "connection refused") }, new FakeEnv());
        C("setup: bridge on but OpenRGB unreachable → fail (important)", r3.Items.FirstOrDefault(i => i.Id == "openrgb") is { Status: "fail", Important: true }, "");
    }

    // ------------------------------------------------------------------ update check

    sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Requests.Add(r); return Task.FromResult(f(r)); }
    }

    static void Updates(Action<string, bool, string> C)
    {
        var cfg = GlintConfig.Defaults();
        HttpResponseMessage Json(string tag, string url) => new(HttpStatusCode.OK) { Content = new StringContent($$"""{"tag_name":"{{tag}}","html_url":"{{url}}","assets":[]}""") };
        var h = new FakeHttp(_ => Json("v0.3.0", UpdateChecker.RepoUrl + "/releases/tag/v0.3.0"));
        using (var u = new UpdateChecker(() => cfg, h, "0.2.0"))
        {
            u.OnConfig(cfg);
            C("update check: nothing is requested until the tray app starts it", h.Requests.Count == 0, $"{h.Requests.Count}");
            var i = u.CheckNow().Result;
            var req = h.Requests.Single();
            C("update check: one GET to the repo's releases/latest API with a User-Agent", req.Method == HttpMethod.Get && req.RequestUri!.ToString() == UpdateChecker.ApiUrl && req.Headers.UserAgent.ToString().StartsWith("Glint/"), req.RequestUri?.ToString() ?? "");
            C("update check: newer release → available, release page url", i.Available && i.Latest == "0.3.0" && i.Url == UpdateChecker.RepoUrl + "/releases/tag/v0.3.0" && u.Info == i, $"{i}");
        }
        using (var u = new UpdateChecker(() => cfg, new FakeHttp(_ => Json("v0.2.0", UpdateChecker.ReleasesUrl)), "0.2.0"))
            C("update check: same version → not available", u.CheckNow().Result is { Available: false, Latest: "0.2.0" }, $"{u.Info}");
        using (var u = new UpdateChecker(() => cfg, new FakeHttp(_ => Json("v9.0.0", "https://evil.example/x")), "0.2.0"))
            C("update check: a link outside the repo is never used", u.CheckNow().Result.Url == UpdateChecker.ReleasesUrl, $"{u.Info.Url}");
        using (var u = new UpdateChecker(() => cfg, new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), "0.2.0"))
        {
            var i = u.CheckNow().Result;
            C("update check: private / missing repo (404) handled silently", !i.Available && i.Latest == null && i.Note != null, $"{i}");
        }
        using (var u = new UpdateChecker(() => cfg, new FakeHttp(_ => throw new HttpRequestException("No such host is known")), "0.2.0"))
        {
            var sw = Stopwatch.StartNew();
            var i = u.CheckNow().Result;
            C("update check: offline handled silently and fast", !i.Available && i.Note!.Contains("offline") && sw.ElapsedMilliseconds < 2000, $"{i} {sw.ElapsedMilliseconds} ms");
        }
        using (var u = new UpdateChecker(() => cfg, new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not json") }), "0.2.0"))
            C("update check: garbage response handled", u.CheckNow().Result is { Available: false, Latest: null }, $"{u.Info}");
        {
            // the reported bug: checked at 20:11, release published at 20:22 → "latest" stayed stale for a whole day
            var tag = "v0.3.0";
            var hs = new FakeHttp(_ => Json(tag, UpdateChecker.ReleasesUrl));
            using var u = new UpdateChecker(() => cfg, hs, "0.3.0");
            C("update check: no answer yet counts as stale", u.IsStale(UpdateChecker.Interval), "");
            var first = u.CheckIfStale(UpdateChecker.Interval).Result;
            C("update check: CheckIfStale checks when there's no answer", hs.Requests.Count == 1 && first is { Available: false, Latest: "0.3.0" }, $"{first}");
            tag = "v0.4.0";
            u.CheckIfStale(UpdateChecker.StaleOnOpen).Wait();
            C("update check: a fresh answer is reused (no request)", hs.Requests.Count == 1 && !u.Info.Available, $"{hs.Requests.Count}");
            C("update check: stale by the wall clock after the interval (sleep can't stretch it)", u.IsStale(UpdateChecker.Interval, u.Info.CheckedAt!.Value + UpdateChecker.Interval)
                && !u.IsStale(UpdateChecker.Interval, u.Info.CheckedAt!.Value + UpdateChecker.Interval - TimeSpan.FromMinutes(1)), "");
            C("update check: background interval is at most 6 h and the loop looks at the clock often", UpdateChecker.Interval <= TimeSpan.FromHours(6) && UpdateChecker.Tick <= TimeSpan.FromMinutes(15)
                && UpdateChecker.StaleOnOpen <= TimeSpan.FromHours(1), "");
            var rapid = u.CheckRequested().Result;
            C("update check: \"Check now\" right after a check doesn't spam GitHub", hs.Requests.Count == 1 && rapid == u.Info, $"{hs.Requests.Count}");
            var off = GlintConfig.Defaults(); off.Updates.Check = false;
            var h2 = new FakeHttp(_ => Json("v0.4.0", UpdateChecker.ReleasesUrl));
            using var u2 = new UpdateChecker(() => off, h2, "0.3.0");
            u2.CheckIfStale(TimeSpan.Zero).Wait();
            C("update check: background/open checks send nothing while the check is off", h2.Requests.Count == 0, $"{h2.Requests.Count}");
            var asked = u2.CheckRequested().Result;
            C("update check: \"Check now\" finds a release published after the last check", h2.Requests.Count == 1 && asked is { Available: true, Latest: "0.4.0" }, $"{asked}");
        }
        C("update check: version parsing (v-prefix, pre-release, two parts)", UpdateChecker.Parse("v1.2.3-beta.1") == new Version(1, 2, 3) && UpdateChecker.Parse("0.3") == new Version(0, 3, 0) && UpdateChecker.Parse("nightly") == null, "");
        C("update check: this build has a real version (not 1.0.0 / 0.0.0)", UpdateChecker.Parse(UpdateChecker.CurrentVersion) is { } v && v < new Version(1, 0, 0) && v > new Version(0, 0, 0), UpdateChecker.CurrentVersion);
    }

    // ------------------------------------------------------------------ OpenRGB bridge vs an in-process fake SDK server

    /// <summary>[name, type, location, zones: (name, type, leds, matrix w, h, map)]</summary>
    sealed record FakeCtl(string Name, int Type, string Location, (string Name, int Type, int Leds, int W, int H, uint[]? Map)[] Zones);

    /// <summary>A fake OpenRGB SDK server on 127.0.0.1 (random port), built from OpenRGB's RGBController::GetDeviceDescriptionData.</summary>
    sealed class FakeOpenRgb : IDisposable
    {
        readonly TcpListener _l = new(IPAddress.Loopback, 0);
        readonly FakeCtl[] _ctls;
        readonly uint _version; readonly bool _answerVersion;
        public int Port { get; }
        public readonly List<(uint Dev, uint Id, byte[] Data, byte[] Raw)> Got = new();
        volatile bool _stop;
        public FakeOpenRgb(FakeCtl[] ctls, uint version, bool answerVersion = true)
        {
            _ctls = ctls; _version = version; _answerVersion = answerVersion;
            _l.Start(); Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            new Thread(Serve) { IsBackground = true, Name = "fake OpenRGB" }.Start();
        }
        public List<(uint Dev, uint Id, byte[] Data, byte[] Raw)> Packets() { lock (Got) return Got.ToList(); }

        void Serve()
        {
            try
            {
                while (!_stop)
                {
                    var cl = _l.AcceptTcpClient();
                    new Thread(() => { try { Handle(cl); } catch { } finally { cl.Dispose(); } }) { IsBackground = true }.Start();
                }
            }
            catch { }
        }

        void Handle(TcpClient c)
        {
            {
                {
                    var s = c.GetStream();
                    uint proto = 0;
                    var h = new byte[16];
                    while (!_stop)
                    {
                        if (!Read(s, h)) break;
                        uint dev = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)), id = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8)), n = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12));
                        var d = new byte[n];
                        if (n > 0 && !Read(s, d)) break;
                        lock (Got) Got.Add((dev, id, d, h.Concat(d).ToArray()));
                        switch (id)
                        {
                            case 40:
                                if (!_answerVersion) break;
                                proto = Math.Min(_version, BinaryPrimitives.ReadUInt32LittleEndian(d));
                                Reply(s, 0, 40, BitConverter.GetBytes(_version));
                                break;
                            case 0: Reply(s, 0, 0, BitConverter.GetBytes((uint)_ctls.Length)); break;
                            case 1:
                                uint pv = d.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(d) : 0;
                                Reply(s, dev, 1, Describe(_ctls[dev], Math.Min(pv, proto)));
                                break;
                        }
                    }
                }
            }
        }
        static bool Read(NetworkStream s, byte[] b) { int got = 0; while (got < b.Length) { int k = s.Read(b, got, b.Length - got); if (k == 0) return false; got += k; } return true; }
        static void Reply(NetworkStream s, uint dev, uint id, byte[] data)
        {
            var h = new byte[16]; "ORGB"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), dev); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), id); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)data.Length);
            s.Write(h); s.Write(data);
        }

        /// <summary>Field order as in OpenRGB's GetDeviceDescriptionData / GetModeDescriptionData / GetZoneDescriptionData.</summary>
        static byte[] Describe(FakeCtl c, uint proto)
        {
            var w = new BinaryWriter(new MemoryStream());
            void Str(string x) { var b = Encoding.UTF8.GetBytes(x + "\0"); w.Write((ushort)b.Length); w.Write(b); }
            w.Write(0u); // size, patched below
            w.Write(c.Type); Str(c.Name);
            if (proto >= 1) Str("Demo vendor");
            Str("Fake device for Glint's selftest"); Str("1.0"); Str(""); Str(c.Location);
            w.Write((ushort)2); w.Write(0); // two modes, active 0
            foreach (var m in new[] { "Direct", "Rainbow" })
            {
                Str(m); w.Write(m == "Direct" ? 0 : 1); w.Write(0x20u); w.Write(0u); w.Write(100u);
                if (proto >= 3) { w.Write(0u); w.Write(100u); }
                w.Write(1u); w.Write(2u); w.Write(50u);
                if (proto >= 3) w.Write(100u);
                w.Write(0u); w.Write(1u);
                w.Write((ushort)2); w.Write(0x00FF0000u); w.Write(0x0000FF00u);
            }
            w.Write((ushort)c.Zones.Length);
            foreach (var z in c.Zones)
            {
                Str(z.Name); w.Write(z.Type); w.Write(0u); w.Write(300u); w.Write((uint)z.Leds);
                if (z.Map == null) w.Write((ushort)0);
                else { w.Write((ushort)(8 + 4 * z.Map.Length)); w.Write((uint)z.H); w.Write((uint)z.W); foreach (var x in z.Map) w.Write(x); }
                if (proto >= 4) { w.Write((ushort)1); Str("Segment 1"); w.Write(1); w.Write(0u); w.Write((uint)z.Leds); }
            }
            int leds = c.Zones.Sum(z => z.Leds);
            w.Write((ushort)leds);
            for (int i = 0; i < leds; i++) { Str("LED " + (i + 1)); w.Write((uint)i); }
            w.Write((ushort)leds);
            for (int i = 0; i < leds; i++) w.Write(0u);
            var b = ((MemoryStream)w.BaseStream).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)b.Length);
            return b;
        }
        public void Dispose() { _stop = true; try { _l.Stop(); } catch { } }
    }

    static readonly FakeCtl[] Rig1 =
    {
        new("Corsair Commander Core", 3, @"HID: \\?\hid#vid_1b1c&pid_0c1c&mi_00#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", new[] { ("Pump", 1, 16, 0, 0, (uint[]?)null) }),
        new("Demo LED Strip Controller", 4, @"HID: \\?\hid#vid_16c0&pid_05dc#7&2b3c4d5e&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", new[] { ("Strip A", 1, 10, 0, 0, (uint[]?)null), ("Logo", 0, 1, 0, 0, null), ("Empty header", 1, 0, 0, 0, null) }),
        new("Demo Keyboard", 5, @"HID: \\?\hid#vid_16c0&pid_05df&mi_01#8&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", new[] { ("Keys", 2, 5, 3, 2, (uint[]?)new uint[] { 0, 1, 2, 3, 0xFFFFFFFF, 4 }) }),
        new("Excluded Mouse", 6, @"HID: \\?\hid#vid_16c0&pid_05e0#8&2&0&0000#{x}", new[] { ("Logo", 0, 1, 0, 0, (uint[]?)null) }),
        new("Demo DRAM", 1, "I2C: PIIX4 #0, address 0x58", new[] { ("DRAM", 1, 8, 0, 0, (uint[]?)null) }),
    };

    static void OpenRgb(Action<string, bool, string> C)
    {
        var native = new List<ILightDevice>
        {
            new SelfTest.FakeLight("cc", new LightZone("cc:pump", "Pump", 16, ZoneKind.Ring)),
            new SelfTest.FakeLight("ram", new LightZone("ram:dimm1", "RAM 1", 8, ZoneKind.Stick)),
        };
        var vidpids = new HashSet<(int, int)> { (0x1b1c, 0x0c1c) };
        var ocfg = new OpenRgbConfig { Enabled = true, Host = "127.0.0.1", Exclude = new() { "Excluded" } };

        using (var srv = new FakeOpenRgb(Rig1, 4))
        {
            ocfg.Port = srv.Port;
            var ids = new HashSet<string>();
            var log = new List<string>();
            using var bridge = OpenRgbBridge.Connect(ocfg, native, vidpids, id => ids.Add(id) ? id : id + "b", log.Add);
            var pk = srv.Packets();
            C("OpenRGB: handshake asks for protocol version 4, then names the client 'Glint'",
                pk.Count >= 2 && pk[0].Id == 40 && BinaryPrimitives.ReadUInt32LittleEndian(pk[0].Data) == 4 && pk[1].Id == 50 && Encoding.ASCII.GetString(pk[1].Data) == "Glint\0" && bridge.Status is { Connected: true, Protocol: 4 },
                string.Join(",", pk.Select(p => p.Id)) + " " + bridge.Status);
            C("OpenRGB: controller data requested with the negotiated version", pk.Where(p => p.Id == 1).Select(p => p.Dev).SequenceEqual(new uint[] { 0, 1, 2, 3, 4 }) && pk.Where(p => p.Id == 1).All(p => p.Data.Length == 4 && p.Data[0] == 4), "");
            var names = bridge.Devices.Select(d => d.Name).ToList();
            C("OpenRGB: devices listed as Glint devices (orgb-<hash>)", names.SequenceEqual(new[] { "Demo LED Strip Controller", "Demo Keyboard" }) && bridge.Devices.All(d => System.Text.RegularExpressions.Regex.IsMatch(d.Id, "^orgb-[0-9a-f]{6}$")), string.Join(", ", bridge.Devices.Select(d => $"{d.Id} {d.Name}")));
            C("OpenRGB: natively driven devices are skipped (USB VID/PID, RAM), so two drivers never fight",
                bridge.Status.Skipped.Count == 3 && bridge.Status.Skipped.Any(s => s.StartsWith("Corsair Commander Core") && s.Contains("1b1c:0c1c")) && bridge.Status.Skipped.Any(s => s.StartsWith("Demo DRAM")) && bridge.Status.Skipped.Any(s => s.StartsWith("Excluded Mouse") && s.Contains("excluded")),
                string.Join("; ", bridge.Status.Skipped));
            var strip = bridge.Devices[0]; var kb = bridge.Devices[1];
            C("OpenRGB: zones mapped (linear → strip, single → single, empty zones dropped)",
                string.Join(",", strip.Zones.Select(z => $"{z.Id[(strip.Id.Length + 1)..]}={z.Leds}/{z.Kind}")) == "strip-a=10/Strip,logo=1/Single", string.Join(",", strip.Zones.Select(z => $"{z.Id}={z.Leds}/{z.Kind}")));
            C("OpenRGB: matrix zone → matrix with its column count (one cell per map position)", kb.Zones.Single() is { Kind: ZoneKind.Matrix, Cols: 3, Leds: 6 } && kb.Zones[0].Category == "keyboard", $"{kb.Zones[0]}");
            C("OpenRGB: frame gap 33 ms", strip.MinGapMs == 33, "");

            // UPDATELEDS bytes for the strip controller (index 1): 10 + 1 LEDs; the empty zone has none
            var frame = Enumerable.Range(0, 11).Select(i => new Rgb((byte)(i * 20), (byte)(i + 1), (byte)(200 - i))).ToArray();
            int before = srv.Packets().Count;
            strip.Write(frame);
            Thread.Sleep(300);
            var after = srv.Packets().Skip(before).ToList();
            var expect = new List<byte>();
            expect.AddRange("ORGB"u8.ToArray()); expect.AddRange(new byte[] { 1, 0, 0, 0, 0x1A, 0x04, 0, 0, 50, 0, 0, 0 });   // dev 1, id 1050, 50 bytes
            expect.AddRange(new byte[] { 50, 0, 0, 0, 11, 0 });                                                        // data size, 11 colours
            foreach (var c in frame) expect.AddRange(new byte[] { c.R, c.G, c.B, 0 });
            C("OpenRGB: SETCUSTOMMODE once before the first frame", after.Count >= 1 && after[0].Id == 1100 && after[0].Dev == 1 && after[0].Data.Length == 0, string.Join(",", after.Select(p => p.Id)));
            C("OpenRGB: UPDATELEDS packet byte-exact", after.Count == 2 && after[1].Raw.SequenceEqual(expect), after.Count > 1 ? Convert.ToHexString(after[1].Raw)[..Math.Min(80, after[1].Raw.Length * 2)] : "missing");
            strip.Write(frame); Thread.Sleep(200);
            C("OpenRGB: later frames are just UPDATELEDS", srv.Packets().Skip(before + 2).Select(p => p.Id).SequenceEqual(new uint[] { 1050 }), string.Join(",", srv.Packets().Skip(before + 2).Select(p => p.Id)));
            // matrix: cells map to LEDs through OpenRGB's matrix map; the hole (0xFFFFFFFF) is dropped
            var cells = Enumerable.Range(0, 6).Select(i => new Rgb((byte)(10 * (i + 1)), 0, 0)).ToArray();
            before = srv.Packets().Count;
            kb.Write(cells); Thread.Sleep(300);
            var up = srv.Packets().Skip(before).LastOrDefault(p => p.Id == 1050);
            var reds = up.Data == null ? "" : string.Join(",", Enumerable.Range(0, BinaryPrimitives.ReadUInt16LittleEndian(up.Data.AsSpan(4))).Select(i => up.Data[6 + i * 4]));
            C("OpenRGB: matrix cells land on the right LEDs (hole skipped)", up.Dev == 2 && reds == "10,20,30,40,60", reds);
            ids.Clear();
            using var again = OpenRgbBridge.Connect(ocfg, native, vidpids, id => id, _ => { });
            C("OpenRGB: ids stable across connections", again.Devices.Select(d => d.Id).SequenceEqual(bridge.Devices.Select(d => d.Id)), "");
            C("OpenRGB: only 127.0.0.1 used in tests", ocfg.Host == "127.0.0.1", "");
        }

        // a protocol-0 server (never answers the version request): no vendor string in the data
        using (var srv0 = new FakeOpenRgb(Rig1.Skip(1).Take(1).ToArray(), 0, answerVersion: false))
        {
            var sw = Stopwatch.StartNew();
            using var b0 = OpenRgbBridge.Connect(new OpenRgbConfig { Enabled = true, Host = "127.0.0.1", Port = srv0.Port }, native, new HashSet<(int, int)>(), id => id, _ => { });
            C("OpenRGB: protocol-0 server (no version reply) still lists devices", b0.Status.Protocol == 0 && b0.Devices.Count == 1 && b0.Devices[0].Zones.Count == 2 && srv0.Packets().Where(p => p.Id == 1).All(p => p.Data.Length == 0) && sw.ElapsedMilliseconds < 3000,
                $"{b0.Status} {sw.ElapsedMilliseconds} ms");
        }

        // nothing listening: no exception, no devices, quick, status says why
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int dead = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var sw2 = Stopwatch.StartNew();
        using var none = OpenRgbBridge.Connect(new OpenRgbConfig { Enabled = true, Host = "127.0.0.1", Port = dead }, native, new HashSet<(int, int)>(), id => id, _ => { });
        C("OpenRGB: server not running → no devices, status explains, startup not held up", !none.Status.Connected && none.Devices.Count == 0 && none.Status.Error != null && sw2.ElapsedMilliseconds < 2500, $"{none.Status.Error} {sw2.ElapsedMilliseconds} ms");

        // and when it's off, Rig never connects
        var cfg = GlintConfig.Defaults();
        C("OpenRGB: bridge off by default", cfg.OpenRgb.Enabled == false && cfg.OpenRgb.Host == "127.0.0.1" && cfg.OpenRgb.Port == 6742, "");
    }

    // ------------------------------------------------------------------ config

    static void Config(Action<string, bool, string> C)
    {
        var c = GlintConfig.Defaults();
        C("config: updates.check defaults to on", c.Updates.Check, "");
        c.OpenRgb.Port = 0; c.OpenRgb.Host = "bad host/x"; c.OpenRgb.Exclude.Add(" ");
        var e = ConfigValidator.Validate(c);
        C("validator: OpenRGB port, host and exclude entries checked", e.Any(x => x.Contains("openRgb.port")) && e.Any(x => x.Contains("openRgb.host")) && e.Any(x => x.Contains("openRgb.exclude")), string.Join("; ", e));
        var n = GlintConfig.Defaults(); n.Updates = null!; n.OpenRgb = null!;
        var e2 = ConfigValidator.Validate(n);
        C("validator: missing updates / openRgb sections rejected", e2.Any(x => x.StartsWith("updates")) && e2.Any(x => x.StartsWith("openRgb")), string.Join("; ", e2));
        var old = System.Text.Json.JsonSerializer.Deserialize<GlintConfig>("""{"profiles":{"A":[]},"activeProfile":"A"}""", GlintConfig.Json)!;
        C("config: an older config.json without the new sections gets the defaults", old.Updates.Check && !old.OpenRgb.Enabled && old.OpenRgb.Port == 6742, "");
        var schema = ConfigValidator.Schema()["fields"]!;
        C("schema: documents updates.check and openRgb.*", schema["updates"]?["fields"]?["check"] != null && schema["openRgb"]?["fields"]?["port"]?["max"] != null && schema["openRgb"]?["fields"]?["exclude"] != null, "");
    }
}
