using System.Diagnostics;
using System.Text.Json;
using Glint.Core;

namespace Glint.Tuning;

/// <summary>Offline checks for <see cref="CpuTuning"/>: CoreCycler config and log parsing, final values, BIOS sheets, the kill script,
/// CPU family detection, the thermal watchdog and the refusal rules. A fake process runner, sensor and clock: no CoreCycler,
/// y-cruncher, PowerShell or real hardware is ever touched.</summary>
public static class CpuTuningTests
{
    sealed class FakeRunner : ICpuProcessRunner
    {
        public List<string> Launched = new(), Scripts = new(), Tasks = new();
        public bool Running;
        public void Launch(string dir) { Launched.Add(dir); Running = true; }
        public void RunPowerShell(string script) { Scripts.Add(script); Running = false; }
        public bool IsCoreCyclerRunning() => Running;
        public void DeleteTask(string name) => Tasks.Add(name);
    }

    public static void Run(Action<string, bool, string> check)
    {
        void C(string n, bool p, string d = "") => check("tuning.cpu: " + n, p, d);
        string Err(Action a) { try { a(); return ""; } catch (Exception e) { return e.Message; } }

        // ---- CoreCycler config (search mode)
        var ini = CpuSearchTools.SearchConfig(-25).Replace("\r\n", "\n");
        var kv = ini.Split('\n').Where(l => l.Contains('=')).Select(l => l.Split('=', 2)).ToDictionary(x => x[0].Trim(), x => x[1].Trim());
        C("config.ini: y-cruncher, one thread, auto runtime", kv["stressTestProgram"] == "YCRUNCHER" && kv["runtimePerCore"] == "auto" && kv["numberOfThreads"] == "1" && kv["mode"] == "auto" && kv["tests"] == "SFTv4, FFTv4, N63");
        C("config.ini: automatic adjustment from the start value, +1, never above 0", kv["enableAutomaticAdjustment"] == "1" && kv["startValues"] == "-25" && kv["maxValue"] == "0" && kv["incrementBy"] == "1" && kv["setVoltageOnlyForTestedCore"] == "1");
        C("config.ini: resumes after a freeze, WHEA counts as an error, no update check", kv["enableResumeAfterUnexpectedExit"] == "1" && kv["waitBeforeAutomaticResume"] == "120" && kv["lookForWheaErrors"] == "1" && kv["treatWheaWarningAsError"] == "1" && kv["enableUpdateCheck"] == "0",
            "resume/WHEA/update keys");
        C("config.ini: start value is the only thing that varies", CpuSearchTools.SearchConfig(-10).Replace("-10", "-25").Equals(CpuSearchTools.SearchConfig(-25)) && CpuSearchTools.SearchConfig(-10).Contains("startValues = -10"));

        // ---- log parsing
        string[] log =
        {
            "12:00:01 - Curve Optimizer starting values",
            "12:10:00 - Increasing Curve Optimizer value for core 2 from -25 to -24",
            "ERROR: At Core 2 (CPU 4)",
            "12:20:00 - Increasing Curve Optimizer value for core 2 from -24 to -23",
            "ERROR: At Core 2 (CPU 4)",
            "13:00:00 - Increasing Curve Optimizer value for core 5 from -25 to -24",
            "ERROR: At Core 5 (CPU 10)",
            "random noise 123 | 456",
        };
        var p1 = CpuSearchTools.Parse(log, -25, 8);
        C("log: increase lines give the latest value per core", p1.Values.SequenceEqual(new[] { -25, -25, -23, -25, -25, -24, -25, -25 }) && p1.Adjustments == 3 && !p1.SummaryFound, string.Join(",", p1.Values));
        C("log: ERROR lines count failed cores", p1.FailedCores.GetValueOrDefault(2) == 2 && p1.FailedCores.GetValueOrDefault(5) == 1 && p1.FailedCores.Count == 2);
        var summaryLog = log.Concat(new[] { "Core      C0 | C1 | C2 | C3", "Starting values  -25 | -25 | -25 | -25", "Current values   -25 |  -22 | -23 | -25 " });
        var p2 = CpuSearchTools.Parse(summaryLog, -25, 8);
        C("log: the final 'Current values' table wins over the tally", p2.SummaryFound && p2.Values.SequenceEqual(new[] { -25, -22, -23, -25 }), string.Join(",", p2.Values));
        var p3 = CpuSearchTools.Parse(new[] { "No adjustments to the Curve Optimizer values were necessary", "CO values  -30 | -30 | -30 | -30 | -30 | -30 | -30 | -30" }, -30, 8);
        C("log: 'CO values' line (no adjustments) is read too", p3.SummaryFound && p3.Values.Length == 8 && p3.Values.All(v => v == -30));
        C("log: out-of-range core numbers are ignored", CpuSearchTools.Parse(new[] { "Increasing Curve Optimizer value for core 99 from -25 to -24" }, -25, 8).Values.All(v => v == -25));
        var hostile = "Current values " + string.Concat(Enumerable.Repeat("-12 | ", 6000)) + "x";
        var hostile2 = "CO values " + new string('9', 60000) + " " + new string(' ', 30000) + "!";
        var sw = Stopwatch.StartNew();
        var ph = CpuSearchTools.Parse(new[] { hostile, hostile2, "Increasing Curve Optimizer value for core 1 " + new string('x', 80000) }, -25, 8);
        sw.Stop();
        C("log: the table regex finishes fast on long hostile lines (no ReDoS)", sw.ElapsedMilliseconds < 500 && !ph.SummaryFound, $"{sw.ElapsedMilliseconds} ms");

        // ---- log files on disk (read shared, only logs since the search began)
        var dir = Path.Combine(Path.GetTempPath(), "GlintCpuTest_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var cc = Path.Combine(dir, "cc");
            Directory.CreateDirectory(Path.Combine(cc, "logs"));
            File.WriteAllText(Path.Combine(cc, "script-corecycler.ps1"), "# fake");
            File.WriteAllText(Path.Combine(cc, "config.ini"), "original");
            var oldLog = Path.Combine(cc, "logs", "CoreCycler_old.log");
            File.WriteAllText(oldLog, "Increasing Curve Optimizer value for core 0 from -25 to -1\n");
            File.SetLastWriteTime(oldLog, new DateTime(2025, 6, 1));
            File.WriteAllText(Path.Combine(cc, "logs", "CoreCycler_new.log"), string.Join("\n", log) + "\n");
            var fromDisk = CpuSearchTools.Parse(CpuSearchTools.ReadLogs(cc, DateTime.Now.AddHours(-1)), -25, 8);
            C("log files: only logs written since the search started are read", fromDisk.Values[0] == -25 && fromDisk.Values[2] == -23, string.Join(",", fromDisk.Values));
            using (var held = new FileStream(Path.Combine(cc, "logs", "CoreCycler_new.log"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                C("log files: readable while CoreCycler has the log open", CpuSearchTools.ReadLogs(cc, DateTime.Now.AddHours(-1)).Any());

            // ---- final values and BIOS sheet
            var fin = CpuSearchTools.FinalValues(new[] { -25, -2, -1, 0, 4, -30 });
            C("final values: found + 3 margin, never positive", fin.SequenceEqual(new[] { -22, 0, 0, 0, 0, -27 }) && CpuSearchTools.Margin == 3, string.Join(",", fin));
            var fam5 = CpuFamilies.For("AMD Ryzen 9 9950X3D 16-Core Processor");
            var co = Enumerable.Range(0, 12).Select(i => -i - 10).ToArray();
            foreach (var v in CpuSearchTools.BoardVendors)
            {
                var sheet = CpuSearchTools.BiosSheet("Test Board", v, CpuFamilies.For("AMD Ryzen 9 9950X 16-Core Processor"), co);
                bool all = Enumerable.Range(0, 12).All(i => sheet.Contains($"Core {i} Curve Optimizer") && sheet.Contains($"Negative {Math.Abs(co[i])}"));
                C($"BIOS sheet ({v}): lists every core, the key and the PBO path", all && sheet.Contains("Precision Boost Overdrive") && sheet.Contains("Max CPU Boost Clock Override(+) .. 200") && !sheet.Contains("Negative -"), sheet.Split('\n')[0]);
            }
            C("BIOS sheets differ per board maker", CpuSearchTools.BiosSheet("x", "asus", fam5, co).Contains("F7") && CpuSearchTools.BiosSheet("x", "gigabyte", fam5, co).Contains("F2") && CpuSearchTools.BiosSheet("x", "asrock", fam5, co).Contains("F6")
                && CpuSearchTools.BiosSheet("x", "msi", fam5, co).Contains("OC > Advanced CPU Configuration"));
            C("BIOS sheet: no boost override row on X3D (not allowed there)", !CpuSearchTools.BiosSheet("x", "asus", fam5, co).Contains("Boost Clock Override") && CpuSearchTools.BiosSheet("x", "asus", fam5, co).Contains("Negative 10"));
            C("board vendor from the maker string", CpuSearchTools.BoardVendor("ASUSTeK COMPUTER INC.", "ROG STRIX X870E") == "asus" && CpuSearchTools.BoardVendor("Micro-Star International Co., Ltd.", "MAG") == "msi"
                && CpuSearchTools.BoardVendor("Gigabyte Technology", "X670 AORUS") == "gigabyte" && CpuSearchTools.BoardVendor("ASRock", "B650") == "asrock" && CpuSearchTools.BoardVendor("Dell", "") == "other" && CpuSearchTools.BoardVendor(null, null) == "other");

            // ---- kill script safety
            string Kill(string? d) => CpuSearchTools.KillScript(d);
            var everything = "Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and ($_.CommandLine -match 'script-corecycler') }";
            bool narrow = new[] { null, "", " ", "tools\\CoreCycler", "..\\x", "C:\\", "C:", "/", "\\", "relative/path" }.All(d => Kill(d).StartsWith(everything) && !Kill(d).Contains("StartsWith"));
            C("kill script: empty, relative or drive-root folder only ever matches CoreCycler's own command line", narrow, Kill("C:\\"));
            var good = Kill(@"C:\Program Files\Glint\tools\CoreCycler\");
            C("kill script: exact folder prefix (StartsWith, not -like), trailing slash normalised, ordinal ignore-case",
                good.Contains(@"StartsWith('C:\Program Files\Glint\tools\CoreCycler\', [StringComparison]::OrdinalIgnoreCase)") && !good.Contains("-like") && !good.Contains(@"CoreCycler\\'"), good);
            var quoted = Kill(@"C:\Users\O'Brien\[x]\cc");
            C("kill script: quotes in the path are escaped, '[' stays literal", quoted.Contains(@"C:\Users\O''Brien\[x]\cc\") && !quoted.Contains("-like"), quoted);
            C("kill script: UNC paths count as absolute, never kills itself", CpuSearchTools.IsAbsoluteWindowsPath(@"\\srv\share\cc") && Kill(@"C:\a\b").Contains("$_.ProcessId -ne $PID"));

            // ---- CPU families
            string F(string n) => CpuFamilies.Id(n);
            C("family: Ryzen 9000 / X3D", F("AMD Ryzen 9 9950X 16-Core Processor") == "amd-zen5" && F("AMD Ryzen 7 9800X3D 8-Core Processor") == "amd-zen5-x3d" && F("AMD Ryzen 5 9600X 6-Core Processor") == "amd-zen5");
            C("family: Ryzen 7000 / X3D, laptop and APU parts excluded", F("AMD Ryzen 9 7950X3D 16-Core Processor") == "amd-zen4-x3d" && F("AMD Ryzen 7 7700X 8-Core Processor") == "amd-zen4" && F("AMD Ryzen 9 7945HX") == "amd-other" && F("AMD Ryzen 7 8700G") == "amd-zen4-apu");
            C("family: Ryzen 5000 / X3D / 3000", F("AMD Ryzen 7 5800X3D 8-Core Processor") == "amd-zen3-x3d" && F("AMD Ryzen 9 5900X 12-Core Processor") == "amd-zen3" && F("AMD Ryzen 5 3600 6-Core Processor") == "amd-zen2" && F("AMD Ryzen 7 PRO 5750G with Radeon Graphics") == "amd-zen3");
            C("family: Intel and unknown", F("13th Gen Intel(R) Core(TM) i9-13900K") == "intel-raptorlake" && F("Intel(R) Core(TM) i7-14700K") == "intel-raptorlake" && F("12th Gen Intel(R) Core(TM) i5-12600K") == "intel-alderlake"
                && F("Intel(R) Core(TM) Ultra 9 285K") == "intel-arrowlake" && F("Intel(R) Core(TM) i7-9700K") == "intel-other" && F("") == "unknown" && F(null) == "unknown" && F("Apple M3") == "unknown");
            C("family: only Ryzen 5000/7000/9000 are supported", new[] { "amd-zen5", "amd-zen5-x3d", "amd-zen4", "amd-zen4-x3d", "amd-zen3", "amd-zen3-x3d" }.All(i => CpuFamilies.Describe(i).Supported)
                && new[] { "amd-zen2", "amd-zen4-apu", "amd-other", "intel-raptorlake", "intel-arrowlake", "intel-alderlake", "intel-other", "unknown" }.All(i => !CpuFamilies.Describe(i).Supported));

            // ---- microcode
            byte[] raw = { 0, 0, 0, 0, 0x2B, 0x01, 0, 0 };
            C("microcode: upper 32 bits of the 8-byte value", CpuFamilies.ParseMicrocode(raw) == 0x12B && CpuFamilies.ParseMicrocode(new byte[] { 1, 2, 3 }) == null && CpuFamilies.ParseMicrocode(null) == null && CpuFamilies.ParseMicrocode(new byte[8]) == null);
            C("microcode: warns below 0x12B on 13th/14th gen only", CpuFamilies.MicrocodeAdvisory("intel-raptorlake", 0x129)!.Contains("update the motherboard BIOS") && CpuFamilies.MicrocodeAdvisory("intel-raptorlake", 0x12B)!.Contains("current enough")
                && CpuFamilies.MicrocodeAdvisory("intel-raptorlake", 0x12A)!.Contains("older") && CpuFamilies.MicrocodeAdvisory("intel-raptorlake", null)!.Contains("0x12B") && CpuFamilies.MicrocodeAdvisory("amd-zen5", 0x100) == null);

            // ---- the service on fakes
            var cfg = GlintConfig.Defaults();
            var runner = new FakeRunner();
            var temp = (double?)55;
            var now = new DateTime(2026, 1, 1, 12, 0, 0);
            string name = "AMD Ryzen 9 9950X 16-Core Processor";
            byte[]? mc = null;
            CpuTuning Make(string? ccDir = null, string state = "cpu-state.json") =>
                new(() => cfg, () => temp, ccDir ?? cc, Path.Combine(dir, state), runner, () => name, () => mc, () => ("ASUSTeK COMPUTER INC.", "ROG STRIX X870E-E"), () => 8, _ => 3, () => now);
            string P(object o) => JsonSerializer.Serialize(o);

            var t0 = Make();
            C("start refused while tuning is off", Err(() => t0.StartSearch(-20)).Contains("tuning is off") && runner.Launched.Count == 0 && File.ReadAllText(Path.Combine(cc, "config.ini")) == "original");
            cfg.Tuning.Enabled = true;
            C("start refused when CoreCycler isn't bundled", Err(() => Make(Path.Combine(dir, "nope")).StartSearch(-20)).Contains("isn't bundled") && runner.Launched.Count == 0);
            name = "13th Gen Intel(R) Core(TM) i9-13900K"; mc = new byte[] { 0, 0, 0, 0, 0x29, 0x01, 0, 0 };
            C("start refused on Intel (nothing launched)", Err(() => t0.StartSearch(-20)).Contains("Intel") && runner.Launched.Count == 0);
            var intelSt = P(t0.Status());
            C("Intel status: not tunable, microcode advisory shown", intelSt.Contains("\"tunable\":false") && intelSt.Contains("0x129") && intelSt.Contains("update the motherboard BIOS"), intelSt[..Math.Min(400, intelSt.Length)]);
            name = "AMD Ryzen 5 3600 6-Core Processor";
            C("start refused on Zen 2 and unknown CPUs", Err(() => t0.StartSearch(-20)).Contains("Zen 2") && runner.Launched.Count == 0);
            name = "AMD Ryzen 9 9950X 16-Core Processor"; mc = null;
            C("start refused for start values outside -50..0", Err(() => t0.StartSearch(5)).Contains("-50..0") && Err(() => t0.StartSearch(-51)).Contains("-50..0") && runner.Launched.Count == 0);
            temp = null;
            C("start refused when the CPU temperature is unreadable", Err(() => t0.StartSearch(-20)).Contains("isn't readable") && runner.Launched.Count == 0);
            temp = 0;
            C("start refused when the sensor reads 0 (no admin)", Err(() => t0.StartSearch(-20)).Contains("isn't readable"));
            temp = 90;
            C("start refused when the CPU is already hot", Err(() => t0.StartSearch(-20)).Contains("cool down") && runner.Launched.Count == 0);
            temp = 50;
            var stOk = P(t0.Status());
            C("status: tunable Ryzen with start hint, bundled path and BIOS vendor", stOk.Contains("\"tunable\":true") && stOk.Contains("\"startHint\":-30") && stOk.Contains("\"bundled\":true") && stOk.Contains("\"vendor\":\"asus\"") && stOk.Contains("\"phase\":\"idle\""), stOk[..Math.Min(500, stOk.Length)]);

            var started = P(t0.StartSearch(-20));
            C("start: config.ini written, original kept, CoreCycler launched in its folder", File.ReadAllText(Path.Combine(cc, "config.ini")).Contains("startValues = -20") && File.ReadAllText(Path.Combine(cc, "config.ini.orig")) == "original"
                && runner.Launched.SequenceEqual(new[] { cc }) && started.Contains("\"started\":true"), started);
            C("start: state saved to disk before/with launch, watchdog timer exists only now", Make(state: "cpu-state.json").State is { Phase: "searching", Search.StartValue: -20, Search.Cores: 8 } && t0.WatchdogRunning);
            C("start refused while a search is running", Err(() => t0.StartSearch(-20)).Contains("already running") && runner.Launched.Count == 1);
            var stRun = P(t0.Status());
            C("status while searching: running, per-core values, WHEA count", stRun.Contains("\"phase\":\"searching\"") && stRun.Contains("\"running\":true") && stRun.Contains("\"wheaSinceStart\":3") && stRun.Contains("\"values\":[-20,"), stRun[..Math.Min(500, stRun.Length)]);

            // ---- watchdog (fake clock + sensor)
            now = now.AddSeconds(1); temp = 70;
            C("watchdog: cool CPU, no trip", !t0.Tick() && t0.State.Phase == "searching" && runner.Scripts.Count == 0);
            temp = 89.9; now = now.AddSeconds(1);
            C("watchdog: just under the limit (90 C on Zen 5), no trip", !t0.Tick());
            temp = 90; now = now.AddSeconds(1);
            bool tripped = t0.Tick();
            C("watchdog: trips at the temperature limit, kills the stress test and the resume task, records why",
                tripped && t0.State.Phase == "stopped" && t0.State.StoppedReason!.Contains("90") && runner.Scripts.Count == 1 && runner.Scripts[0].Contains(cc) && runner.Tasks.Contains(CpuTuning.StartupTask) && !t0.WatchdogRunning,
                $"{t0.State.Phase} / {t0.State.StoppedReason}");
            C("watchdog: nothing more to do after a trip", !t0.Tick());
            C("stopped search keeps its progress and Finish still works", Err(() => t0.Finish()) == "" && t0.State.Phase == "done");

            // sensor failure
            runner.Scripts.Clear(); runner.Tasks.Clear(); runner.Running = false; temp = 50;
            var t1 = Make(state: "s1.json"); t1.StartSearch(-20);
            temp = null; now = now.AddSeconds(1);
            bool a1 = t1.Tick(); now = now.AddSeconds(CpuTuning.UnreadableTripSeconds - 2);
            bool a2 = t1.Tick();
            C("watchdog: sensor unreadable for less than 10 s does not trip", !a1 && !a2 && t1.State.Phase == "searching");
            temp = 50; now = now.AddSeconds(1); t1.Tick();
            temp = 0; now = now.AddSeconds(1); t1.Tick(); now = now.AddSeconds(CpuTuning.UnreadableTripSeconds - 1);
            C("watchdog: a good reading resets the unreadable timer", !t1.Tick() && t1.State.Phase == "searching");
            temp = 200; now = now.AddSeconds(2);
            bool a3 = t1.Tick();
            C("watchdog: trips after 10 s without a usable reading (0 / >=150 count as unreadable)", a3 && t1.State.Phase == "stopped" && t1.State.StoppedReason!.Contains("unreadable") && runner.Scripts.Count == 1, t1.State.StoppedReason ?? "");

            // finish
            runner.Running = false; temp = 50;
            var t2 = Make(state: "s2.json"); t2.StartSearch(-25);
            File.WriteAllText(Path.Combine(cc, "logs", "CoreCycler_new.log"), "Increasing Curve Optimizer value for core 1 from -25 to -24\n");
            File.SetLastWriteTime(Path.Combine(cc, "logs", "CoreCycler_new.log"), now.AddSeconds(5));
            runner.Running = true;
            C("finish refused while the search is still working (cores untested)", Err(() => t2.Finish()).Contains("hasn't finished") && t2.State.Phase == "searching" && t2.WatchdogRunning);
            var forced = P(t2.Finish(force: true));
            C("finish(force): kills CoreCycler, final = found + 3, never positive, BIOS sheet for the board, warns it is incomplete",
                t2.State.Phase == "done" && t2.State.Final!.SequenceEqual(new[] { -22, -21, -22, -22, -22, -22, -22, -22 }) && forced.Contains("\"complete\":false") && forced.Contains("F7 for Advanced Mode") && forced.Contains("Core 7 Curve Optimizer") && !t2.WatchdogRunning && runner.Scripts.Count == 2, forced[..Math.Min(300, forced.Length)]);
            C("finish twice returns the same result without killing again", P(t2.Finish()).Contains("F7 for Advanced Mode") && runner.Scripts.Count == 2);

            // finish after a complete table
            runner.Running = false; temp = 50;
            var t3 = Make(state: "s3.json"); t3.StartSearch(-30);
            File.WriteAllText(Path.Combine(cc, "logs", "CoreCycler_new.log"), "Current values  -30 | -12 | -3 | -1 | 0 | -30 | -30 | -30\n");
            File.SetLastWriteTime(Path.Combine(cc, "logs", "CoreCycler_new.log"), now.AddSeconds(10));
            runner.Running = true;
            var done = P(t3.Finish());
            C("finish after the final table needs no force: [-30,-12,-3,-1,0,...] -> [-27,-9,0,0,0,...]", t3.State.Final!.SequenceEqual(new[] { -27, -9, 0, 0, 0, -27, -27, -27 }) && done.Contains("\"complete\":true"), string.Join(",", t3.State.Final!));
            C("finish without a search is refused", Err(() => Make(state: "none.json").Finish()).Contains("no CPU search"));

            // Ryzen 5000 floor, explicit stop, startup resume
            name = "AMD Ryzen 9 5900X 12-Core Processor"; runner.Running = false;
            var t4 = Make(state: "s4.json");
            var r4 = P(t4.StartSearch(-45));
            C("Ryzen 5000: start value raised to the -30 floor, with a note", r4.Contains("-30") && r4.Contains("allows -30") && File.ReadAllText(Path.Combine(cc, "config.ini")).Contains("startValues = -30"), r4);
            int tasksBefore = runner.Tasks.Count;
            var stopped = P(t4.Stop());
            C("stop: kills CoreCycler, removes the resume task, keeps progress, timer gone", stopped.Contains("\"stopped\":true") && t4.State.Phase == "stopped" && !t4.WatchdogRunning && runner.Tasks.Count == tasksBefore + 1);
            runner.Running = false; name = "AMD Ryzen 9 9950X 16-Core Processor";
            var t5 = Make(state: "s5.json"); t5.StartSearch(-20); t5.Dispose();
            var scriptsBefore = runner.Scripts.Count;
            var back = Make(state: "s5.json");
            C("restart: a search that was running is still 'searching' (CoreCycler resumes it), the watchdog comes back with OnStartup",
                back.State.Phase == "searching" && !back.WatchdogRunning && Err(() => back.OnStartup()) == "" && back.WatchdogRunning);
            back.Dispose();
            cfg.Tuning.Enabled = false;
            var back2 = Make(state: "s5.json"); back2.OnStartup();
            C("OnStartup with tuning turned off stops the stress test instead of watching it", back2.State.Phase == "stopped" && !back2.WatchdogRunning && runner.Scripts.Count > scriptsBefore);
            cfg.Tuning.Enabled = true;
            C("OnStartup does nothing when no search was running", Err(() => Make(state: "fresh.json").OnStartup()) == "");

            // AI tool names exist as control methods and MCP tools
            bool toolsExist = AiTuner.Tools.Where(t => t.Contains("_cpu_")).All(t => Cli.HasTool(t) && ControlServer.Methods.Any(m => "glint_" + m.Method.Replace('.', '_') == t));
            C("AI: every CPU tool the tuner may call exists as an MCP tool and control method", toolsExist && AiTuner.Tools.Count(t => t.Contains("_cpu_")) == 4 && !AiTuner.Tools.Contains("glint_tuning_cpu_test"),
                string.Join(",", AiTuner.Tools.Where(t => t.Contains("_cpu_"))));
            C("AI: CPU tools pass the tuning-only MCP filter", new[] { "tuning.cpu.status", "tuning.cpu.start", "tuning.cpu.finish", "tuning.cpu.stop" }.All(Cli.IsTuningTool));
            var prompt = AiTuner.SystemPrompt;
            C("AI: prompt tells it the search takes hours, resumes itself, and to finish on the second call", prompt.Contains("HOURS") && prompt.Contains("resumes by itself") && prompt.Contains("glint_tuning_cpu_finish") && prompt.Contains("BIOS sheet"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
