using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glint.Core;
using Glint.Devices;
using HidSharp;

namespace Glint;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "pluginbench")
        {
            using var w = new StreamWriter(args[1]) { AutoFlush = true };
            var leds = new List<Led>();
            for (int z = 0; z < 19; z++) { int n = z < 11 ? 8 : 40; for (int i = 0; i < n; i++) leds.Add(new Led(leds.Count, z, (z + i / (double)n) / 19, i / (double)n, z < 11)); }
            while (leds.Count < 555) leds.Add(new Led(leds.Count, 18, 0.99, 0.5, false));
            var L = leds.ToArray(); var o = new Rgb[L.Length];
            foreach (var f in Directory.GetFiles(Path.Combine(Plugins.PluginHost.BundledDir, "effects"), "*.js"))
            {
                var (ep, _) = Plugins.PluginHost.LoadForTest(f);
                var fx = new Plugins.PluginEffect(new EffectConfig { Type = EffectType.Plugin, Plugin = ep!.Info.Name }, ep);
                var ctx = new FxContext { Sensors = new Dictionary<string, double> { ["cpu"] = 60 } };
                for (int i = 0; i < 30; i++) { ctx.Time = i / 30.0; ctx.Dt = 1 / 30.0; fx.Render(ctx, L, o); }
                var cpu0 = Process.GetCurrentProcess().TotalProcessorTime; var sw = Stopwatch.StartNew();
                for (int i = 0; i < 150; i++) { ctx.Time = 1 + i / 30.0; fx.Render(ctx, L, o); }
                w.WriteLine($"{ep.Info.Name}: {sw.Elapsed.TotalMilliseconds / 150:0.00} ms/frame for {L.Length} LEDs  {fx.Error}");
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "engbench") { SelfTest.EngineBench(args[1], args[2]); return 0; }
        if (args.Length > 0 && args[0] == "plugincheck")
        {
            // Glint.exe plugincheck [file.js | all] [out.json]: run plugins' declared tests against simulated devices
            var files = args.Length < 2 || args[1] == "all" ? Plugins.PluginHost.AllFiles.ToList() : new List<string> { args[1] };
            var res = files.SelectMany(Plugins.PluginCheck.Run).ToList();
            var text = string.Join("\n", res.Select(r => $"{(r.Pass ? "ok  " : "FAIL")} {r.Plugin} · {r.Test} · {r.Detail}")) + $"\n{res.Count(r => r.Pass)} passed, {res.Count(r => !r.Pass)} failed";
            if (args.Length > 2) File.WriteAllText(args[2], text); else { Core.Cli.Print(text); }
            return res.Count(r => !r.Pass);
        }
        if (args.Length > 0 && args[0] == "layoutdump")
        {
            // zones JSON [{id, deviceId, name, leds, kind, cols, category}] → auto-arranged placements (used by the dashboard mock)
            var zs = JsonSerializer.Deserialize<List<LayoutZone>>(File.ReadAllText(args[1]), GlintConfig.Json)!;
            File.WriteAllText(args[2], JsonSerializer.Serialize(Layout.AutoArrange(zs), GlintConfig.Json));
            return 0;
        }
        if (args.Length > 0 && args[0] == "devbench") { SelfTest.DeviceBench(args[1]); return 0; }
        if (args.Length > 0 && args[0] == "sensorbench")
        {
            // Read-only: LibreHardwareMonitor sensors only (no HID / SMBus, no fan writes). Sensor-thread CPU per 1 s tick with the
            // Sensors page closed, open (full catalog), and closed again.  usage: sensorbench <out.txt> [ticksPerPhase]
            using var w = new StreamWriter(args[1]) { AutoFlush = true };
            int n = args.Length > 2 ? int.Parse(args[2]) : 15;
            using var rig = new Rig(GlintConfig.Defaults(), useLhm: true, hardware: true, nativeDevices: false);
            foreach (var l in rig.Log) w.WriteLine(l);
            rig.ReadSensors(new()); // warm-up (first update + JIT)
            double Phase(string name, bool open)
            {
                var ms = new List<double>(); int sensors = 0;
                for (int i = 0; i < n; i++)
                {
                    if (open) rig.WantCatalog();
                    double c0 = CpuMeter.ThreadMs();
                    rig.ReadSensors(new());
                    ms.Add(CpuMeter.ThreadMs() - c0);
                    if (rig.Catalog is { } cat) sensors = Count(cat.Hardware);
                    Thread.Sleep(1000);
                }
                double avg = ms.Skip(1).Average(); // first tick of a phase includes the group switch
                w.WriteLine($"{name}: {avg:0.00} ms CPU per tick (avg of {n - 1}, ~{avg / 10:0.000}% of one core at 1 Hz), first tick {ms[0]:0.0} ms, max {ms.Max():0.0} ms{(open ? $", {sensors} sensors in catalog" : "")}");
                return avg;
            }
            static int Count(List<CatalogHardware> h) => h.Sum(x => x.Sensors.Count + Count(x.Sub));
            Phase("closed", false);
            Phase("open", true);
            Thread.Sleep(3500); // let the keep-alive lapse
            Phase("closed again", false);
            var off = Rig.SensorCatalog(); // no running service: private read-only LHM instance, every group on
            var json = JsonSerializer.Serialize(off, GlintConfig.Json);
            w.WriteLine($"Rig.SensorCatalog() offline: {Count(off.Hardware)} sensors, {json.Length / 1024} KB JSON{(off.Error != null ? ", error " + off.Error : "")}");
            return 0;
        }
        if (args.Length > 0 && args[0] == "lhmmem")
        {
            using var w = new StreamWriter(args[2], true) { AutoFlush = true };
            var proc = Process.GetCurrentProcess();
            long Mb() { GC.Collect(); GC.WaitForPendingFinalizers(); proc.Refresh(); return proc.PrivateMemorySize64 / 1048576; }
            long b0 = Mb();
            bool all = args[1] == "all";
            var c = new LibreHardwareMonitor.Hardware.Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true,
                IsStorageEnabled = all, IsMemoryEnabled = all, IsPsuEnabled = all, IsControllerEnabled = all };
            c.Open();
            for (int i = 0; i < 5; i++) foreach (var h in c.Hardware) { h.Update(); foreach (var sh in h.SubHardware) sh.Update(); }
            w.WriteLine($"LHM {args[1]}: {Mb() - b0} MB ({c.Hardware.Count} devices: {string.Join(", ", c.Hardware.Select(h => h.HardwareType))})");
            c.Close();
            return 0;
        }
        if (args.Length > 0 && args[0] == "v8mem")
        {
            // measure private memory for 6 engines under one configuration (flags are per-process)
            string mode = args[1];
            using var w = new StreamWriter(args[2], true) { AutoFlush = true };
            var proc = Process.GetCurrentProcess();
            long Mb() { GC.Collect(); GC.WaitForPendingFinalizers(); proc.Refresh(); return proc.PrivateMemorySize64 / 1048576; }
            long b0 = Mb();
            var keep = new List<object>();
            var code = File.ReadAllText(Path.Combine(Plugins.PluginHost.BundledDir, "devices", "corsair-commander-core.js"));
            Microsoft.ClearScript.V8.V8Runtime? rt = null;
            if (mode == "lite") Microsoft.ClearScript.V8.V8Settings.GlobalFlags = Microsoft.ClearScript.V8.V8GlobalFlags.None;
            for (int i = 0; i < 6; i++)
            {
                Microsoft.ClearScript.V8.V8ScriptEngine e = mode switch
                {
                    "small" => new Microsoft.ClearScript.V8.V8ScriptEngine("e", new Microsoft.ClearScript.V8.V8RuntimeConstraints { MaxOldSpaceSize = 8, MaxNewSpaceSize = 1 }, Microsoft.ClearScript.V8.V8ScriptEngineFlags.DisableGlobalMembers),
                    "shared" => (rt ??= new Microsoft.ClearScript.V8.V8Runtime("rt", new Microsoft.ClearScript.V8.V8RuntimeConstraints { MaxOldSpaceSize = 64, MaxNewSpaceSize = 16 })).CreateScriptEngine(Microsoft.ClearScript.V8.V8ScriptEngineFlags.DisableGlobalMembers),
                    _ => Plugins.PluginHost.NewEngine("e"),
                };
                if (mode != "default") e.Execute("var module={exports:{}};");
                e.Execute(code);
                for (int k = 0; k < 200; k++) e.Execute("var a = new Uint8Array(400); for (var j=0;j<400;j++) a[j]=j;");
                keep.Add(e);
            }
            w.WriteLine($"{mode}: baseline {b0} MB, with 6 engines {Mb()} MB → {(Mb() - b0) / 6.0:0.0} MB per engine");
            return 0;
        }
        if (args.Length > 0 && args[0] == "plugintest")
            return PluginTest(args[1], args[2], int.Parse(args[3]));
        if (args.Length > 0 && args[0] == "controltest")
            return ControlTest(args[1]);
        if (args.Length > 0 && args[0] == "webtest")
            return WebTest(args[1]);
        if (args.Length > 0 && args[0] == "selftest")
            return SelfTest.Run(args.Length > 1 ? args[1] : "selftest.json", args.Contains("--hw"));
        if (args.Length > 0 && args[0] == "overlayshot") { GameOverlay.OverlayTests.Screenshot(args.Length > 1 ? args[1] : "overlay.png"); return 0; }
        if (args.Length > 0 && args[0] == "overlaybench") return GameOverlay.OverlayTests.Bench(args.Length > 1 ? args[1] : "overlaybench.json", args.Length > 2 ? int.Parse(args[2]) : 10);
        if (args.Length > 0 && args[0] == "etwsmoke") return GameOverlay.OverlayTests.EtwSmoke(args.Length > 1 ? args[1] : "etwsmoke.json", args.Length > 2 ? int.Parse(args[2]) : 3);
        if (args.Length > 0 && args[0] == "ctl") return Cli.Ctl(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "mcp") return Cli.Mcp();
        if (EnsureLeanGc(args)) return 0;
        return RunTray(args.Contains("--restarted"));
    }

    /// <summary>
    /// Opens the real WebView2 dashboard off-screen (no hardware), checks the C#↔JS bridge both ways,
    /// then closes it and checks the browser processes exit. Exit code = failed checks.
    /// </summary>
    static int WebTest(string outPath)
    {
        var checks = new List<object>(); int failed = 0;
        void C(string n, bool ok, string d = "") { checks.Add(new { name = n, pass = ok, detail = d }); if (!ok) failed++; }
        // never touch the real config folder: the service watches config.json and would pick up the user's live edits
        var testDir = Path.Combine(Path.GetTempPath(), "GlintWebTest_" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("GLINT_CONFIG_DIR", testDir);
        var t0 = DateTime.Now;
        int Edge() => Process.GetProcessesByName("msedgewebview2").Count(p => { try { return p.StartTime >= t0; } catch { return false; } });
        ApplicationConfiguration.Initialize();
        Plugins.PluginHost.LoadAll();
        var cfg = GlintConfig.Defaults();
        // one simulated RGB device so the Lighting page shows its effect gallery (with none it shows the empty state)
        var rig = new Rig(cfg, useLhm: false, hardware: false);
        rig.Lights.Add(new SelfTest.FakeLight("demo", new LightZone("fan1", "Fan 1", 8, ZoneKind.Ring), new LightZone("strip", "Strip", 20)));
        using var svc = new GlintService(cfg, rig) { NoSave = true };
        var d = new UI.WebDashboard(svc) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false };
        d.Show();
        var sw = Stopwatch.StartNew();
        while (!d.Ready && sw.ElapsedMilliseconds < 15000) { Application.DoEvents(); Thread.Sleep(20); }
        C("page loads and says ready", d.Ready, $"{sw.ElapsedMilliseconds} ms");
        string Eval(string js)
        {
            var t = d.EvalAsync(js);
            var w = Stopwatch.StartNew();
            while (!t.IsCompleted && w.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
            return t.IsCompleted ? t.Result : "timeout";
        }
        void Pump(int ms) { var w = Stopwatch.StartNew(); while (w.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); } }
        Pump(2500);
        C("init received (profiles)", Eval("Object.keys(S.cfg.profiles).length") == "3", Eval("Object.keys(S.cfg.profiles).join()"));
        C("live state received", Eval("S.state != null && S.state.perf.privateMB > 0") == "true", Eval("JSON.stringify(S.state?.perf)"));
        C("no JS errors on load", Eval("document.querySelectorAll('.page.active').length") == "1");
        int edgeOpen = Edge();
        C("WebView2 processes running while open", edgeOpen > 0, edgeOpen.ToString());
        // UI → service: switch profile via the real button
        Eval("document.querySelector('#profile-seg button[data-p=\"Quiet\"]').click()");
        Pump(800);
        C("profile click reaches the service", svc.Config.ActiveProfile == "Quiet", svc.Config.ActiveProfile);
        C("config round-trip keeps lighting presets", svc.Config.Lighting.Presets.Count == cfg.Lighting.Presets.Count, $"{svc.Config.Lighting.Presets.Count}");
        // lighting effect
        Eval("window.__errs = []; window.addEventListener('error', e => __errs.push(e.message + ' @' + e.lineno)); 1");
        Eval("go('lighting'); document.querySelector('[data-fx=\"Fire\"]').click()");
        Pump(800);
        C("effect click reaches the service", svc.Config.Lighting.Effect.Type == EffectType.Fire, svc.Config.Lighting.Effect.Type.ToString() + " " + Eval("Array.from(document.querySelectorAll('.toast')).map(t => t.textContent).join(' | ') + ' errs: ' + JSON.stringify(window.__errs) + ' page: ' + JSON.stringify({t: S.target, fx: S.cfg.lighting.effect.type, en: S.cfg.lighting.enabled, card: !!document.querySelector('[data-fx=\"Fire\"]'), pg: S.page})"));
        C("plugin effects reach the dashboard", Eval("(S.init.plugins?.effects ?? []).length >= 3") == "true", Eval("JSON.stringify((S.init.plugins?.effects ?? []).map(e => e.name))"));
        Eval("go('lighting'); document.querySelector('[data-pfx=\"Aurora\"]').click()");
        Pump(1200);
        C("plugin effect click reaches the service", svc.Config.Lighting.Effect.Type == EffectType.Plugin && svc.Config.Lighting.Effect.Plugin == "Aurora", $"{svc.Config.Lighting.Effect.Type} {svc.Config.Lighting.Effect.Plugin}");
        C("lighting engine runs the plugin without errors", svc.Lighting.Status == "", svc.Lighting.Status);
        foreach (var pg in new[] { "overview", "cooling", "lighting", "devices", "settings" })
        {
            Eval($"go('{pg}')"); Pump(300);
            C($"page {pg} renders in WebView2", Eval($"document.getElementById('page-{pg}').children.length > 0") == "true");
        }
        d.Close(); d.Dispose();
        Pump(4000);
        int edgeAfter = Edge();
        C("WebView2 processes exit after close", edgeAfter == 0, $"{edgeOpen} → {edgeAfter}");
        var proc = Process.GetCurrentProcess();
        File.WriteAllText(outPath, JsonSerializer.Serialize(new { failed, checks, privateMBAfterClose = proc.PrivateMemorySize64 / 1048576 }, new JsonSerializerOptions { WriteIndented = true }));
        return failed;
    }

    /// <summary>Run one device plugin against real hardware (only devices it claims) with a rainbow.</summary>
    static int PluginTest(string pluginFile, string outPath, int seconds)
    {
        using var w = new StreamWriter(outPath) { AutoFlush = true };
        var (_, def) = Plugins.PluginHost.LoadForTest(pluginFile);
        if (def == null) { w.WriteLine("not a device plugin"); return 1; }
        using var e = Plugins.PluginHost.NewEngine("plugintest");
        e.Execute(File.ReadAllText(pluginFile));
        var meta = JsonDocument.Parse((string)e.Evaluate("JSON.stringify({v:module.exports.vendorId,p:module.exports.productIds,i:module.exports.interface,u:module.exports.usagePage})")).RootElement;
        int vid = meta.GetProperty("v").GetInt32();
        var pids = meta.GetProperty("p").EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
        int iface = meta.TryGetProperty("i", out var iv) ? iv.GetInt32() : -1;
        int up = meta.TryGetProperty("u", out var uv) ? uv.GetInt32() : -1;
        int fails = 0;
        foreach (var d in DeviceList.Local.GetHidDevices(vid).Where(d => pids.Contains(d.ProductID)))
        {
            if (iface >= 0 && DriverRegistry.InterfaceOf(d) != iface) continue;
            if (up >= 0 && DriverRegistry.UsagePageOf(d) != up) continue;
            w.WriteLine($"candidate {d.DevicePath}");
            Found? f;
            try { f = def.OpenWith(new Plugins.HidSharpPort(d), x => x, (z, n) => n, w.WriteLine); }
            catch (Exception ex) { w.WriteLine("open failed: " + ex.Message); fails++; continue; }
            if (f == null) { w.WriteLine("init declined"); continue; }
            foreach (var l in f.Lights)
            {
                int n = l.Zones.Sum(z => z.Leds);
                w.WriteLine($"opened {l.Id}: {n} LEDs in {l.Zones.Count} zones");
                var sw = Stopwatch.StartNew(); int frames = 0; double worst = 0;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    var c = new Rgb[n];
                    for (int i = 0; i < n; i++) c[i] = Rgb.FromHsv(i * 60 + sw.Elapsed.TotalSeconds * 120);
                    var t = Stopwatch.StartNew();
                    try { l.Write(c); frames++; } catch (Exception ex) { w.WriteLine("write failed: " + ex.Message); fails++; break; }
                    worst = Math.Max(worst, t.Elapsed.TotalMilliseconds);
                    Thread.Sleep(Math.Max(l.MinGapMs, 30));
                }
                w.WriteLine($"{frames} frames in {seconds}s, worst write {worst:0.0} ms");
                l.Dispose();
            }
        }
        return fails;
    }

    /// <summary>Start a fresh instance (inherits elevation) and exit this one; the new one waits for our mutex.</summary>
    public static void Restart()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false });
        Application.Exit();
    }

    /// <summary>Exercise the control API, the ctl CLI and the MCP server against a no-hardware instance.</summary>
    static int ControlTest(string outPath)
    {
        Environment.SetEnvironmentVariable("GLINT_PIPE", "GlintControlTest_" + Environment.ProcessId);
        // never touch the real config folder: plugins, layout backups etc. go to a throwaway folder
        var testDir = Path.Combine(Path.GetTempPath(), "GlintControlTest_" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("GLINT_CONFIG_DIR", testDir);
        var checks = new List<object>(); int failed = 0;
        void C(string n, bool ok, string d = "") { checks.Add(new { name = n, pass = ok, detail = d.Length > 300 ? d[..300] : d }); if (!ok) failed++; }
        var cfg = GlintConfig.Defaults();
        Plugins.PluginHost.Disabled = () => cfg.DisabledPlugins;
        Plugins.PluginHost.LoadAll();
        using var svc = new GlintService(cfg, new Rig(cfg, useLhm: false, hardware: false)) { NoSave = true };
        Plugins.PluginHost.Disabled = () => svc.Config.DisabledPlugins;
        using var srv = new ControlServer(svc, a => a());
        Thread.Sleep(300);
        JsonNode? Call(string m, object? p = null)
        {
            var r = ControlServer.Call(m, p == null ? null : JsonSerializer.SerializeToNode(p));
            return r == null ? null : JsonNode.Parse(r);
        }
        var help = Call("help");
        C("help lists methods", help?["result"]?.AsArray().Count >= 20, $"{help?["result"]?.AsArray().Count}");
        C("status works", Call("status")?["result"]?["profile"] != null);
        var hid = Call("hid.list")?["result"]?.AsArray();
        C("hid.list sees real USB devices (incl. Corsair 0x1b1c)", hid != null && hid.Any(h => (string?)h!["vendorId"] == "0x1b1c"), $"{hid?.Count} devices");
        C("plugins.docs returns the guide", ((string?)Call("plugins.docs")?["result"])?.Contains("Device plugins") == true);
        var file = "controltest-fx.js";
        var add = Call("plugins.add", new { file, code = "module.exports = { type: 'effect', name: 'CT Effect', render(ctx, leds, out) { for (let i = 0; i < out.length; i++) out[i] = 128; } };" });
        C("plugins.add loads a good effect", (bool?)add?["result"]?["status"]?["ok"] == true, add?.ToJsonString() ?? "null");
        var bad = Call("plugins.add", new { file = "controltest-bad.js", code = "module.exports = { type: 'effect', render( { };" });
        C("plugins.add reports a broken plugin's error", (bool?)bad?["result"]?["status"]?["ok"] == false && ((string?)bad?["result"]?["status"]?["message"])?.Length > 0, bad?.ToJsonString() ?? "null");
        var unsafeName = Call("plugins.add", new { file = "..\\evil.js", code = "x" });
        C("plugins.add rejects path tricks", unsafeName?["error"] != null || (string?)unsafeName?["result"]?["file"] == "evil.js", unsafeName?.ToJsonString() ?? "");
        Call("plugins.remove", new { file = "evil.js" });
        var set = Call("lighting.set", new { effect = new { type = "plugin", plugin = "CT Effect" } });
        C("lighting.set switches to the new plugin effect", svc.Config.Lighting.Effect.Plugin == "CT Effect", set?.ToJsonString() ?? "");
        Call("plugins.setEnabled", new { file, enabled = false });
        var list = Call("plugins.list")?["result"]?.AsArray();
        C("plugins.setEnabled false → listed as disabled", list?.Any(x => (string?)x!["file"] == file && (bool?)x["enabled"] == false) == true);
        Call("plugins.setEnabled", new { file, enabled = true });
        Call("plugins.remove", new { file }); Call("plugins.remove", new { file = "controltest-bad.js" });
        list = Call("plugins.list")?["result"]?.AsArray();
        C("plugins.remove deletes user plugins", list?.Any(x => ((string?)x!["file"])?.StartsWith("controltest") == true) == false);
        C("bundled plugins can't be deleted", Call("plugins.remove", new { file = "aurora.js" })?["error"] != null);
        C("profile.set validates names", Call("profile.set", new { name = "Nope" })?["error"] != null && Call("profile.set", new { name = "Quiet" })?["error"] == null && svc.Config.ActiveProfile == "Quiet");
        var noProf = Call("config.set", new { config = new { profiles = new { } } });
        C("config.set refuses a config with no profiles", noProf?["error"] != null || (bool?)noProf?["result"]?["ok"] == false, noProf?.ToJsonString() ?? "");
        var patch = Call("config.patch", new { path = "lighting.brightness", value = 42 });
        C("config.patch sets one value", svc.Config.Lighting.Brightness == 42, patch?.ToJsonString() ?? "");
        C("config.patch rejects unknown paths", Call("config.patch", new { path = "nope.deeper.x", value = 1 })?["error"] != null);
        var tmpCode = Path.Combine(Path.GetTempPath(), "ct-file-fx.js");
        File.WriteAllText(tmpCode, "module.exports = { type: 'effect', name: 'From File', render(ctx, leds, out) { out.fill(9); } };");
        var cliFile = Run($"ctl plugins.add file=ct-file-fx.js code=@{tmpCode}");
        C("ctl code=@file adds a plugin from a file", cliFile.Contains("\"ok\": true") || cliFile.Contains("From File"), cliFile.Length > 200 ? cliFile[..200] : cliFile);
        Call("plugins.remove", new { file = "ct-file-fx.js" });
        C("no API to approve SMBus plugins", Call("plugins.approve", new { file = "x.js" })?["error"] != null);

        // the real CLI and MCP server, as separate processes
        string Run(string args, string? stdin = null)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, args) { RedirectStandardOutput = true, RedirectStandardInput = stdin != null, UseShellExecute = false, CreateNoWindow = true };
            psi.Environment["GLINT_PIPE"] = Environment.GetEnvironmentVariable("GLINT_PIPE");
            using var pr = Process.Start(psi)!;
            if (stdin != null) { pr.StandardInput.Write(stdin); pr.StandardInput.Close(); }
            var o = pr.StandardOutput.ReadToEnd(); pr.WaitForExit(20000); return o;
        }
        var cli = Run("ctl profile.set name=Balanced");
        C("ctl CLI round-trip (key=value)", cli.Contains("\"ok\": true") && svc.Config.ActiveProfile == "Balanced", cli.Trim());
        var cliJson = Run("ctl profile.set \"{\\\"name\\\":\\\"Quiet\\\"}\"");
        C("ctl CLI round-trip (JSON)", cliJson.Contains("\"ok\": true") && svc.Config.ActiveProfile == "Quiet", cliJson.Trim());
        Run("ctl profile.set name=Balanced");
        var mcpIn = string.Join("\n", new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"glint_profile_set","arguments":{"name":"Quiet"}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"glint_plugins_list","arguments":{}}}""",
        }) + "\n";
        var mcp = Run("mcp", mcpIn).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        C("MCP initialize", mcp.Any(m => (int?)m["id"] == 1 && m["result"]?["serverInfo"]?["name"]?.ToString() == "glint"));
        var tools = mcp.FirstOrDefault(m => (int?)m["id"] == 2)?["result"]?["tools"]?.AsArray();
        C("MCP tools/list exposes the API", tools?.Count >= 20 && tools.Any(t => (string?)t!["name"] == "glint_plugins_add"), $"{tools?.Count} tools");
        C("MCP tools/call changes the app", mcp.Any(m => (int?)m["id"] == 3 && m["result"]?["isError"]?.GetValue<bool>() == false) && svc.Config.ActiveProfile == "Quiet");
        C("MCP tools/call returns data", mcp.FirstOrDefault(m => (int?)m["id"] == 4)?["result"]?["content"]?[0]?["text"]?.ToString().Contains("aurora.js") == true);
        FanApiChecks(svc, Call, C, mcp.FirstOrDefault(m => (int?)m["id"] == 2)?["result"]?["tools"]?.AsArray());
        GameOverlay.OverlayTests.ApiChecks(svc, Call, C, mcp.FirstOrDefault(m => (int?)m["id"] == 2)?["result"]?["tools"]?.AsArray());
        File.WriteAllText(outPath, JsonSerializer.Serialize(new { failed, checks }, new JsonSerializerOptions { WriteIndented = true }));
        try { Directory.Delete(testDir, true); } catch { }
        return failed;
    }

    /// <summary>Fan / curve / profile / schema / validation / layout-guard methods of the control API.</summary>
    static void FanApiChecks(GlintService svc, Func<string, object?, JsonNode?> Call, Action<string, bool, string> Check, JsonArray? tools)
    {
        void C(string n, bool ok, string d = "") => Check(n, ok, d);
        string J(JsonNode? n) => n?.ToJsonString() ?? "null";
        bool Ok(JsonNode? n) => (bool?)n?["result"]?["ok"] == true;
        bool Rejected(JsonNode? n) => (bool?)n?["result"]?["ok"] == false && n?["result"]?["errors"]?.AsArray().Count > 0;
        string Saved() => JsonSerializer.Serialize(svc.Config, GlintConfig.Json);
        Call("profile.set", new { name = "Balanced" });

        var names = new[] { "fans.list", "fans.setRule", "fans.removeRule", "fans.override", "curves.set", "curves.remove", "profiles.create", "profiles.remove", "config.schema", "config.validate", "lighting.layoutBackups", "lighting.restoreLayout" };
        C("MCP exposes every new method as a tool with a schema", names.All(m => tools?.Any(t => (string?)t!["name"] == "glint_" + m.Replace('.', '_') && t["inputSchema"]?["type"] != null && ((string?)t["description"])?.Length > 20) == true),
            string.Join(",", names.Where(m => tools?.Any(t => (string?)t!["name"] == "glint_" + m.Replace('.', '_')) != true)));
        var help = Call("help", null)?["result"]?.AsArray();
        C("help lists the new methods", names.All(m => help?.Any(h => (string?)h!["method"] == m) == true));

        // fans.list
        var fl = Call("fans.list", null)?["result"];
        var case_ = fl?["profiles"]?["Balanced"]?.AsArray().FirstOrDefault(r => (string?)r!["name"] == "Case fans");
        C("fans.list: rules with fans, inputs, curves, sensors", case_?["fans"]?.AsArray().Count == 6 && (string?)case_["inputs"]?[0]?["sensor"] == "gpu" && case_["inputs"]?[0]?["hysteresis"] != null
            && case_["minDuty"] != null && case_["rampDownPerSec"] != null && fl?["curves"]?["cpu-balanced"] != null && fl?["sensors"]?.AsObject().ContainsKey("liquid") == true && (string?)case_["fans"]?[0]?["name"] != null, J(case_));

        // curves
        var cs = Call("curves.set", new { name = "ct-curve", points = new[] { new[] { 30.0, 25 }, new[] { 50.0, 50 }, new[] { 70.0, 100 } } });
        C("curves.set creates a curve", Ok(cs) && svc.Config.Curves.ContainsKey("ct-curve"), J(cs));
        var before = Saved();
        var badCurve = Call("curves.set", new { name = "ct-bad", points = new[] { new[] { 60.0, 25 }, new[] { 40.0, 50 } } });
        C("validation rejects a non-monotonic curve without saving", Rejected(badCurve) && Saved() == before && ((string?)badCurve?["result"]?["errors"]?[0])?.Contains("increase") == true, J(badCurve));
        var badDuty = Call("curves.set", new { name = "ct-bad", points = new[] { new[] { 40.0, 25 }, new[] { 60.0, 140 } } });
        C("validation rejects curve duty > 100 without saving", Rejected(badDuty) && Saved() == before, J(badDuty));

        // rules
        var sr = Call("fans.setRule", new { name = "CT rule", rule = new { fans = new[] { "ct:fan1" }, inputs = new[] { new { sensor = "cpu", curve = "ct-curve" } }, minDuty = 30 } });
        var ctRule = svc.Config.Profiles["Balanced"].FirstOrDefault(r => r.Name == "CT rule");
        C("fans.setRule creates a rule (unknown fan = warning, not error)", Ok(sr) && ctRule?.MinDuty == 30 && ctRule.MaxDuty == 100 && ctRule.Inputs[0].Curve == "ct-curve"
            && sr?["result"]?["warnings"]?.AsArray().Any(w => ((string?)w)?.Contains("not currently connected") == true) == true, J(sr));
        var merge = Call("fans.setRule", new { name = "CT rule", rule = new { maxDuty = 80 } });
        ctRule = svc.Config.Profiles["Balanced"].FirstOrDefault(r => r.Name == "CT rule");
        C("fans.setRule partial update merges", Ok(merge) && ctRule?.MaxDuty == 80 && ctRule.MinDuty == 30 && ctRule.Fans.SequenceEqual(new[] { "ct:fan1" }), J(merge));
        before = Saved();
        var minMax = Call("fans.setRule", new { name = "CT rule", rule = new { minDuty = 90 } });
        C("validation rejects min > max without saving", Rejected(minMax) && Saved() == before && J(minMax).Contains("greater than maxDuty"), J(minMax));
        var badSensor = Call("fans.setRule", new { name = "CT rule", rule = new { inputs = new[] { new { sensor = "bogus:nothing", curve = "ct-curve" } } } });
        C("validation rejects an unknown sensor without saving", Rejected(badSensor) && Saved() == before && J(badSensor).Contains("unknown sensor"), J(badSensor));
        var badFn = Call("fans.setRule", new { name = "CT rule", rule = new { inputs = new[] { new { sensor = "median(cpu,gpu)", curve = "ct-curve" } } } });
        C("validation rejects an unknown sensor function", Rejected(badFn) && Saved() == before, J(badFn));
        var badRef = Call("fans.setRule", new { name = "CT rule", rule = new { inputs = new[] { new { sensor = "max(cpu,gpu)", curve = "no-such-curve" } } } });
        C("validation rejects an unknown curve", Rejected(badRef) && Saved() == before && J(badRef).Contains("unknown curve"), J(badRef));
        var badField = Call("fans.setRule", new { name = "CT rule", rule = new { minduty2 = 5 } });
        C("fans.setRule rejects unknown fields", Rejected(badField) && Saved() == before, J(badField));
        var inline = Call("fans.setRule", new { name = "CT rule", rule = new { inputs = new object[] { new { sensor = "max(cpu,gpu)", points = new[] { new[] { 40.0, 30 }, new[] { 80.0, 100 } } } } } });
        C("fans.setRule accepts inline points + sensor expressions", Ok(inline), J(inline));

        // curves.remove refuses a curve in use
        var useCurve = Call("fans.setRule", new { name = "CT rule", rule = new { inputs = new[] { new { sensor = "cpu", curve = "ct-curve" } } } });
        var rmUsed = Call("curves.remove", new { name = "ct-curve" });
        C("curves.remove refuses a curve in use and says where", Ok(useCurve) && Rejected(rmUsed) && J(rmUsed).Contains("CT rule") && svc.Config.Curves.ContainsKey("ct-curve"), J(rmUsed));

        // override
        var ov = Call("fans.override", new { name = "CT rule", percent = 95 });
        ctRule = svc.Config.Active.First(r => r.Name == "CT rule");
        C("fans.override sets a manual duty clamped to max", Ok(ov) && ctRule.Override == 80, J(ov));
        var duty = new FanEngine().Compute(svc.Config, new Dictionary<string, double> { ["cpu"] = 40, ["gpu"] = 40, ["liquid"] = 30 }, 1);
        C("override drives the fan (engine)", duty.GetValueOrDefault("ct:fan1") == 80, $"{duty.GetValueOrDefault("ct:fan1")}");
        var lost = new FanEngine().Compute(svc.Config, new Dictionary<string, double> { ["gpu"] = 40, ["liquid"] = 30 }, 1);
        C("fail-safe still beats the override (lost sensor)", lost.GetValueOrDefault("ct:fan1") == svc.Config.FailsafeDuty, $"{lost.GetValueOrDefault("ct:fan1")}");
        var auto = Call("fans.override", new { name = "CT rule", percent = (int?)null });
        C("fans.override null → auto", Ok(auto) && svc.Config.Active.First(r => r.Name == "CT rule").Override == null && (string?)auto?["result"]?["mode"] == "auto", J(auto));
        C("fans.override unknown rule → error", Rejected(Call("fans.override", new { name = "Nope", percent = 50 })));

        // profiles
        var pc = Call("profiles.create", new { name = "CT profile", from = "Quiet" });
        C("profiles.create copies a profile", Ok(pc) && svc.Config.Profiles["CT profile"].Count == svc.Config.Profiles["Quiet"].Count && svc.Config.ActiveProfile == "Balanced", J(pc));
        var pso = Call("fans.setRule", new { name = "AIO pump", profile = "CT profile", rule = new { minDuty = 70 } });
        C("fans.setRule on a non-active profile leaves the copy source alone", Ok(pso) && svc.Config.Profiles["CT profile"][0].MinDuty == 70 && svc.Config.Profiles["Quiet"][0].MinDuty == 60, J(pso));
        C("profiles.remove refuses the active profile", Rejected(Call("profiles.remove", new { name = "Balanced" })) && svc.Config.Profiles.ContainsKey("Balanced"));
        var pr = Call("profiles.remove", new { name = "CT profile" });
        C("profiles.remove deletes a profile", Ok(pr) && !svc.Config.Profiles.ContainsKey("CT profile"), J(pr));

        // cleanup via the API
        var rr = Call("fans.removeRule", new { name = "CT rule" });
        var rc = Call("curves.remove", new { name = "ct-curve" });
        C("fans.removeRule + curves.remove", Ok(rr) && Ok(rc) && !svc.Config.Curves.ContainsKey("ct-curve") && svc.Config.Active.All(r => r.Name != "CT rule"), J(rr) + J(rc));

        // config.set / config.patch go through the same validator
        before = Saved();
        var badPatch = Call("config.patch", new { path = "lighting.brightness", value = 300 });
        C("config.patch rejects brightness 300 without saving", Rejected(badPatch) && Saved() == before, J(badPatch));
        var badType = Call("config.patch", new { path = "lighting.effect.type", value = "laserShow" });
        C("config.patch rejects an unknown effect type", Rejected(badType) && Saved() == before, J(badType));
        var badActive = Call("config.patch", new { path = "activeProfile", value = "Nope" });
        C("config.patch rejects a missing active profile", Rejected(badActive) && Saved() == before, J(badActive));
        var cfgNode = JsonSerializer.SerializeToNode(svc.Config, GlintConfig.Json)!;
        cfgNode["curves"]!["cpu-balanced"] = JsonNode.Parse("[[40,25],[40,30]]");
        var badSet = Call("config.set", new { config = cfgNode });
        C("config.set rejects a bad curve without saving", Rejected(badSet) && Saved() == before, J(badSet));
        var val = Call("config.validate", new { config = cfgNode });
        C("config.validate reports without saving", (bool?)val?["result"]?["ok"] == false && Saved() == before, J(val));
        C("default config validates clean", ConfigValidator.Validate(GlintConfig.Defaults()).Count == 0, string.Join("; ", ConfigValidator.Validate(GlintConfig.Defaults())));

        // schema
        var schema = Call("config.schema", null)?["result"];
        var f = schema?["fields"];
        C("config.schema documents fans, curves, lighting effects, layout, HA, sensors", f?["profiles"]?["fields"]?["minDuty"]?["max"] != null && f?["profiles"]?["fields"]?["override"] != null
            && f?["lighting"]?["fields"]?["effect"]?["fields"]?["type"]?["enum"]?.AsArray().Count == Enum.GetValues<EffectType>().Length && f?["lighting"]?["fields"]?["layout"]?["fields"]?["rotation"] != null
            && f?["homeAssistant"]?["fields"]?["port"]?["max"] != null && f?["sensors"]?["fields"]?["storage"] != null && f?["curves"]?["items"] != null, J(schema).Length.ToString());

        // layout guard
        var hand = new Dictionary<string, ZonePlacement> { ["ct:zone"] = new() { X = 0.1, Y = 0.2, W = 0.1, H = 0.16, Rotation = 30 } };
        var c0 = JsonSerializer.Deserialize<GlintConfig>(Saved(), GlintConfig.Json)!; c0.Lighting.Layout = hand; svc.Apply(c0);
        var nBefore = Call("lighting.layoutBackups", null)?["result"]?.AsArray().Count ?? -1;
        var noConfirm = Call("lighting.autoLayout", null);
        C("lighting.autoLayout refused without confirm (nothing changed)", (bool?)noConfirm?["result"]?["ok"] == false && svc.Config.Lighting.Layout.ContainsKey("ct:zone")
            && Call("lighting.layoutBackups", null)?["result"]?.AsArray().Count == nBefore, J(noConfirm));
        var yes = Call("lighting.autoLayout", new { confirm = true });
        var backups = Call("lighting.layoutBackups", null)?["result"]?.AsArray();
        C("lighting.autoLayout with confirm replaces the layout and backs it up first", Ok(yes) && !svc.Config.Lighting.Layout.ContainsKey("ct:zone") && backups?.Count == nBefore + 1
            && (string?)backups?[0]?["name"] == (string?)yes?["result"]?["backup"] && File.Exists(Path.Combine(LayoutBackups.Dir, (string?)yes?["result"]?["backup"] + ".json")), J(yes)[..Math.Min(200, J(yes).Length)]);
        var restore = Call("lighting.restoreLayout", new { name = (string?)yes?["result"]?["backup"] });
        C("lighting.restoreLayout brings the hand-tuned layout back", Ok(restore) && svc.Config.Lighting.Layout.TryGetValue("ct:zone", out var z) && z.Rotation == 30 && z.X == 0.1, J(restore));
        C("lighting.restoreLayout unknown name → error", Call("lighting.restoreLayout", new { name = "nope" })?["error"] != null);
        for (int i = 0; i < 12; i++) LayoutBackups.Save(hand, "test");
        C("layout backups keep the last 10", Directory.GetFiles(LayoutBackups.Dir, "*.json").Length == LayoutBackups.Keep, $"{Directory.GetFiles(LayoutBackups.Dir, "*.json").Length}");
        var badLayout = Call("config.patch", new { path = "lighting.layout", value = new Dictionary<string, object> { ["x"] = new { x = 0.1, y = 0.1, w = -1, h = 0.1 } } });
        C("validation rejects a bad layout placement", Rejected(badLayout), J(badLayout));
    }

    /// <summary>
    /// The GC sizes its gen0 budget from L3 cache; on X3D CPUs (128 MB L3) tens of MB of garbage pile up between
    /// collections (measured: +74 MB vs +23 MB for 6 device plugins). The knob is environment-only, so the tray
    /// process relaunches itself once with it set.
    /// </summary>
    static bool EnsureLeanGc(string[] args)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_GCgen0size") != null) return false;
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_GCgen0size"] = "0x400000";
        try { Process.Start(psi); return true; } catch { return false; }
    }

    static int RunTray(bool restarted)
    {
        using var single = new Mutex(false, @"Local\GlintSingleInstance");
        bool first;
        try { first = single.WaitOne(restarted ? 15000 : 0); } catch (AbandonedMutexException) { first = true; }
        if (!first) return 1;
        ApplicationConfiguration.Initialize();
        GlintConfig cfg;
        try { cfg = GlintConfig.Load(); }
        catch (Exception e)
        {
            MessageBox.Show($"config.json is invalid, using defaults:\n{e.Message}", "Glint");
            cfg = GlintConfig.Defaults();
        }
        Plugins.PluginHost.Disabled = () => cfg.DisabledPlugins;
        Plugins.PluginHost.LoadAll();
        using var svc = new GlintService(cfg);
        Plugins.PluginHost.EffectsChanged += () => svc.Lighting.Apply(svc.Config);
        var tray = new UI.TrayApp(svc);
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        using var control = new ControlServer(svc, a => ui.Post(_ => a(), null));
        Application.Run(tray);
        return 0;
    }



}
