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
        public List<string> ToolCalls = new();
        public (int, string) ToolResult = (0, "Current PBO offsets:\n-3,-1,0,-5,-2,-4,0,-1\n");
        public bool? Hvci = true; public bool Pawn = true;
        public (int Exit, string Output) RunTool(string exe, IReadOnlyList<string> args, int timeoutMs)
        {
            ToolCalls.Add(Path.GetFileName(exe) + " " + string.Join(" ", args));
            if (args.Count == 2 && args[0] == "--offset") LastOffsets = args[1].Split(',').Select(int.Parse).ToArray();
            return args.Count == 2 && args[0] == "--offset" ? (0, "ok") : ToolResult;
        }
        public int[]? LastOffsets;
        /// <summary>core → the most negative value it is stable at.</summary>
        public Dictionary<int, int> Floor = new();
        /// <summary>core → floor that only shows when the core is tested alone (single-core boost is where Curve Optimizer fails first).</summary>
        public Dictionary<int, int> CoreFloor = new();
        /// <summary>y-cruncher output for a running test, from the Curve Optimizer vector it runs at (benchmark scores).</summary>
        public Func<int[], string>? OutputFor;
        public List<(string Exe, string Cfg, int[] Offsets)> Stress = new();
        public bool StressExitsAtOnce;
        /// <summary>Simulate y-cruncher ending cleanly (exit 0) with this output, instead of running.</summary>
        public string? CleanExitText;
        /// <summary>What a running (healthy) y-cruncher has printed so far.</summary>
        public string StressOutput = "";
        public IStressRun StartStress(string exe, IReadOnlyList<string> args, string workDir)
        {
            var cfgText = File.ReadAllText(args[^1]);
            Stress.Add((Path.GetFileName(exe), cfgText, LastOffsets?.ToArray() ?? Array.Empty<int>()));
            bool fail = StressExitsAtOnce || Unstable();
            return CleanExitText != null && !fail ? new FakeRun(false, 0, CleanExitText) : new FakeRun(fail, null, OutputFor?.Invoke(LastOffsets ?? Array.Empty<int>()) ?? StressOutput, this);
        }
        /// <summary>Whether the Curve Optimizer values set right now are past a floor (checked live: the sweep changes them mid-run).</summary>
        public bool Unstable()
        {
            var offs = LastOffsets ?? Array.Empty<int>();
            bool alone = offs.Count(v => v != 0) == 1;
            return Enumerable.Range(0, offs.Length).Any(i => (Floor.TryGetValue(i, out var f) && offs[i] < f) || (alone && offs[i] != 0 && CoreFloor.TryGetValue(i, out var g) && offs[i] < g));
        }
        /// <summary>The speed a running test reports at the values set right now (from OutputFor, else a constant).</summary>
        public double SpeedNow() => OutputFor?.Invoke(LastOffsets ?? Array.Empty<int>()) is { } t && CpuTuning.SpeedList(t) is { Count: > 0 } l ? l[0] : 1e9;
        public bool? MemoryIntegrity() => Hvci;
        public bool PawnIoInstalled() => Pawn;
    }

    sealed class FakeRun(bool fail, int? cleanExitCode = null, string text = "", FakeRunner? live = null) : IStressRun
    {
        public bool Killed;
        bool Bad => fail || (live != null && live.Unstable());
        public bool Exited => Bad || cleanExitCode != null || Killed;
        public int? ExitCode => Bad ? 1 : cleanExitCode;
        public string Output => Bad ? "Error detected" : text;
        readonly List<double> _speeds = new();
        /// <summary>A new reading each time it is read (a running test printing results), at the values set right now.</summary>
        public IReadOnlyList<double> Speeds { get { if (!Exited && live != null) _speeds.Add(live.SpeedNow()); return _speeds.ToArray(); } }
        public void Kill() => Killed = true;
        public void Dispose() { }
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
            var ycBin = Path.Combine(cc, "test_programs", "y-cruncher", "Binaries");
            Directory.CreateDirectory(ycBin);
            foreach (var b in new[] { "24-ZN5 ~ Komari.exe", "22-ZN4 ~ Kizuna.exe", "19-ZN2 ~ Kagari.exe" }) File.WriteAllText(Path.Combine(ycBin, b), "fake");
            Directory.CreateDirectory(Path.Combine(cc, "tools", "ryzen-smu-cli")); File.WriteAllText(Path.Combine(cc, "tools", "ryzen-smu-cli", "ryzen-smu-cli.exe"), "fake");
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
            var fam4x = CpuFamilies.For("AMD Ryzen 9 7950X3D 16-Core Processor");
            C("BIOS sheet: no boost override row on 7000X3D (not allowed there)", !CpuSearchTools.BiosSheet("x", "asus", fam4x, co).Contains("Boost Clock Override") && CpuSearchTools.BiosSheet("x", "asus", fam4x, co).Contains("Negative 10"));
            C("9000X3D is fully unlocked: boost override up to +200 MHz", fam5.BoostOverrideMhz == 200);
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

            // ---- the service on fakes (native search: y-cruncher per core, Curve Optimizer set through the SMU tool)
            var cfg = GlintConfig.Defaults();
            var runner = new FakeRunner();
            var temp = (double?)55;
            var now = new DateTime(2026, 1, 1, 12, 0, 0);
            string name = "AMD Ryzen 9 9950X 16-Core Processor";
            byte[]? mc = null;
            int whea = 0;
            CpuTuning Make(string? ccDir = null, string state = "cpu-state.json")
            {
                var t = new CpuTuning(() => cfg, () => temp, ccDir ?? cc, Path.Combine(dir, state), runner, () => name, () => mc, () => ("ASUSTeK COMPUTER INC.", "ROG STRIX X870E-E"), () => 8, _ => whea, () => now);
                t.AutoRun = false; t.SecondsPerPass = 0.02; t.CoreCheckSeconds = 0.02; t.SoakSeconds = 0.02; t.PollMs = 2; t.SampleMs = 2; t.LoadTimeScale = 0.004; t.BaselinePath = Path.Combine(dir, state + ".co.json");
                return t;
            }
            string P(object o) => JsonSerializer.Serialize(o);
            int Drive(CpuTuning t) { int n = 0; while (n < 500 && t.StepOnce()) n++; return n; }

            var t0 = Make();
            C("start refused while tuning is off", Err(() => t0.StartSearch(-20)).Contains("tuning is off") && runner.Stress.Count == 0);
            cfg.Tuning.Enabled = true;
            C("start refused when the stress test isn't bundled", Err(() => Make(Path.Combine(dir, "nope")).StartSearch(-20)).Contains("isn't bundled") && runner.Stress.Count == 0);
            name = "13th Gen Intel(R) Core(TM) i9-13900K"; mc = new byte[] { 0, 0, 0, 0, 0x29, 0x01, 0, 0 };
            C("start refused on Intel (nothing run)", Err(() => t0.StartSearch(-20)).Contains("Intel") && runner.Stress.Count == 0);
            var intelSt = P(t0.Status());
            C("Intel status: not tunable, microcode advisory shown", intelSt.Contains("\"tunable\":false") && intelSt.Contains("0x129") && intelSt.Contains("update the motherboard BIOS"), intelSt[..Math.Min(400, intelSt.Length)]);
            name = "AMD Ryzen 5 3600 6-Core Processor";
            C("start refused on Zen 2 and unknown CPUs", Err(() => t0.StartSearch(-20)).Contains("Zen 2") && runner.Stress.Count == 0);
            name = "AMD Ryzen 9 9950X 16-Core Processor"; mc = null;
            C("start refused for start values outside -50..0", Err(() => t0.StartSearch(5)).Contains("-50..0") && Err(() => t0.StartSearch(-51)).Contains("-50..0"));
            temp = null;
            C("start refused when the CPU temperature is unreadable", Err(() => t0.StartSearch(-20)).Contains("isn't readable") && runner.Stress.Count == 0);
            temp = 0;
            C("start refused when the sensor reads 0 (no admin)", Err(() => t0.StartSearch(-20)).Contains("isn't readable"));
            temp = 90;
            C("start refused when the CPU is already hot", Err(() => t0.StartSearch(-20)).Contains("cool down"));
            temp = 50;
            var stOk = P(t0.Status());
            C("status: tunable Ryzen with start hint, bundled, BIOS vendor", stOk.Contains("\"tunable\":true") && stOk.Contains("\"startHint\":-30") && stOk.Contains("\"bundled\":true") && stOk.Contains("\"vendor\":\"asus\"") && stOk.Contains("\"phase\":\"idle\""), stOk[..Math.Min(500, stOk.Length)]);

            // the tune: a sweep picks the value, each core is checked alone, then all cores are soaked
            runner.Floor = new() { [2] = -22 }; runner.CoreFloor = new() { [5] = -16 };
            string? finProf = null; int[]? finVals = null;
            var ts = Make(state: "s-engine.json"); ts.TargetProfile = () => "Balanced (tuned)"; ts.Finished += (p, v) => { finProf = p; finVals = v; };
            var started = P(ts.StartSearch(-30));
            C("start: state on disk before anything runs, the sweep first, watchdog on", Make(state: "s-engine.json").State is { Phase: "searching", Stage: "sweep" } && ts.WatchdogRunning && started.Contains("\"started\":true") && started.Contains("min"), started);
            C("start refused while a search is running", Err(() => ts.StartSearch(-30)).Contains("already running"));
            ts.StepOnce();
            var first = runner.Stress[0];
            C("sweep: one all-core run (Zen 5 build), no undervolt first (the reference)",
                first.Exe == "24-ZN5 ~ Komari.exe" && first.Cfg.Contains("FFTv4") && runner.Stress.Count == 1 && ts.State.Sweep is [{ Value: 0, Ok: true, AllCore: > 0 }]
                && runner.LastOffsets!.All(v => v == 0) && runner.ToolCalls.First().Contains("--get-offsets-terse"), first.Cfg);
            ts.StepOnce();
            C("sweep: the next value is set under the SAME run (separate runs differ by up to 20%), not a new launch", runner.Stress.Count == 1 && runner.LastOffsets!.All(v => v == -5) && ts.State.Sweep!.Count == 2);
            var liveSt = P(ts.Status());
            C("status while sweeping: phase and sweep points in the live numbers", liveSt.Contains("\"phase\":\"sweep\"") && liveSt.Contains("\"sweep\":[{\"value\":0,"), liveSt[..Math.Min(500, liveSt.Length)]);
            int steps = Drive(ts);
            C("sweep: all cores together, 5 deeper per step until an error (core 2 can't do -25); unscored = the deepest stable value",
                string.Join(",", ts.State.Sweep!.Select(x => x.Value)).StartsWith("0,-5,-10,-15,-20,-25,0,") && ts.State.Sweep.First(x => x.Value == -25) is { Ok: false } && ts.State.Picked == -20, string.Join(",", ts.State.Sweep!.Select(x => $"{x.Value}{(x.Ok ? "" : "x")}")) + $" picked {ts.State.Picked}");
            var single = runner.Stress.Where(x => x.Offsets.Count(v => v != 0) == 1).ToList();
            int tpc = Math.Max(1, Environment.ProcessorCount / 8);
            C("each core alone: pinned to ONE logical CPU of it, only that core gets the value, stop on error",
                single.Count >= 8 && single[0].Cfg.Contains("LogicalCores : [0]") && single[0].Offsets.SequenceEqual(new[] { -20, 0, 0, 0, 0, 0, 0, 0 }) && single[0].Cfg.Contains("StopOnError : \"true\"")
                && single.Any(x => x.Cfg.Contains($"LogicalCores : [{5 * tpc}]")), single.Count > 0 ? single[0].Cfg : "none");
            var coreOf = single.Select(x => Array.FindIndex(x.Offsets, v => v != 0)).ToList();
            C("each core alone: one core at a time, in order (cores done goes up steadily)", coreOf.Zip(coreOf.Skip(1)).All(z => z.Second >= z.First), string.Join(",", coreOf));
            C("each core alone: an error loosens that core by 3 and retests it (core 5: -20, -17 fail, -14 passes)",
                ts.State.Found!.SequenceEqual(new[] { -20, -20, -20, -20, -20, -14, -20, -20 }), string.Join(",", ts.State.Found ?? Array.Empty<int>()) + $" after {steps} steps");
            var soak = runner.Stress.LastOrDefault(x => !x.Cfg.Contains("N63") && x.Offsets.Count(v => v != 0) > 1 && x.Offsets.Distinct().Count() > 1);
            C("soak: all-core FFTv4 only (the in-cache tests pin the CPU at its thermal limit), per-core checks keep the full mix", soak.Cfg?.Contains("FFTv4") == true && !soak.Cfg.Contains("SFTv4") && single.All(x => x.Cfg.Contains("SFTv4") && x.Cfg.Contains("N63")));
            C("soak: every core loaded together at the final per-core values, the full test mix in the per-core checks", soak.Offsets?.SequenceEqual(new[] { -20, -20, -20, -20, -20, -14, -20, -20 }) == true, soak.Offsets == null ? "no soak" : string.Join(",", soak.Offsets));
            C("complete: it finishes by itself: final = found + 3 margin, never positive", ts.State.Phase == "done" && ts.State.Final!.SequenceEqual(new[] { -17, -17, -17, -17, -17, -11, -17, -17 }), string.Join(",", ts.State.Final ?? Array.Empty<int>()));
            C("complete: the result goes to the profile the tune made", finProf == "Balanced (tuned)" && finVals!.SequenceEqual(ts.State.Final!), finProf ?? "none");
            C("complete: the BIOS values are put back (the profile then applies its own)", runner.LastOffsets!.SequenceEqual(new[] { -3, -1, 0, -5, -2, -4, 0, -1 }), string.Join(",", runner.LastOffsets!));
            // the CPU power table: limit fields trusted only after sanity checks
            var pmOk = RyzenPm.Parse(0x621202, new float[] { 1000, 160, 1000, 110, 95, 78, 0, 0, 1000, 150, 0, 0 });
            var pmBad = RyzenPm.Parse(0x621202, new float[] { 0.5f, 3, 7, 1e6f, 2, 0, 0, 0, 1, 1 });
            C("power table: PPT / TDC / thermal / EDC fields parsed and checked; nonsense is refused, never used", pmOk is { Valid: true, PptLimitW: 1000, PptW: 160, ThmLimitC: 95, EdcLimitA: 1000, Raised: true } && pmOk.PptPct == 16 && pmBad is { Valid: false, PptLimitW: null } && pmBad.Why!.Contains("not recognised"));
            var notLimited = PboAdvisor.Advise(now, 78, 90, 162, 4987, null, 200, "custom", pmOk);
            var pmReal = RyzenPm.Parse(0x620205, new float[] { 0, 0, 1000.00006f, 64.98147f, 0, 0, 0, 0, 1000.00006f, 23.141119f, 95, 51.963516f, 0, 0, 0, 0 });
            var pmRealZ3 = RyzenPm.Parse(0x380805, new float[] { 0, 0, 1000.00006f, 64.98147f, 0, 0, 0, 0, 1000.00006f, 23.141119f, 95, 51.963516f, 0, 0, 0, 0 });
            C("power table: the real 9950X3D table (0x620205) parses with the Zen 4/5 layout: PPT 65 of 1000 W, TDC 23 of 1000 A, 95 C limit, EDC unknown", pmReal is { Valid: true, PptLimitW: 1000, PptW: 65, TdcA: 23.1, ThmLimitC: 95, ThmC: 52, EdcLimitA: null, Raised: true } && pmReal.EdcPct == null && pmRealZ3 is { Valid: true, PptW: 65 });
            var stockHit = PboAdvisor.Advise(now, 75, 90, 200, 4900, null, 200, "custom", RyzenPm.Parse(1, new float[] { 200, 199, 160, 120, 95, 75, 0, 0, 225, 180 }));
            C("PBO advice from real limits: not power-limited = no change (says the BIOS has them raised) + boost override; at the stock limit with headroom = Motherboard",
                notLimited.Short == "PBO: no change, +200 MHz" && notLimited.Detail.Contains("already has the PBO limits raised") && stockHit.Short.StartsWith("PBO: Motherboard"), notLimited.Short + " | " + stockHit.Short);
            // thermal history: real-use peaks and sustained-load periods for the AI's fan tuning
            var th = new ThermalHistory(null); var t0h = new DateTime(2026, 1, 1, 18, 0, 0);
            for (int k = 0; k < 40; k++) th.Record(t0h.AddSeconds(30 * k), new Dictionary<string, double> { ["gpu"] = 35, ["liquid"] = 33, ["cpu"] = 60 }, new() { ["Case & radiator fans"] = 25 });
            for (int k = 40; k < 100; k++) th.Record(t0h.AddSeconds(30 * k), new Dictionary<string, double> { ["gpu"] = 75, ["liquid"] = 33 + Math.Min(11, (k - 40) / 3.0), ["cpu"] = 70 }, new() { ["Case & radiator fans"] = 60 });
            th.Record(t0h.AddSeconds(30 * 99 + 5), new Dictionary<string, double> { ["gpu"] = 99 }, new());   // within 30 s of the last: skipped
            var ths = P(th.Summary(t0h.AddSeconds(30 * 100), 24));
            C("thermal history: one sample per 30 s; the sustained-load period found with its peaks and the fans' duty", th.Count == 100 && ths.Contains("\"sustainedLoad\":[{") && ths.Contains("\"liquid\":44") && ths.Contains("\"avgDuty\":{\"Case \\u0026 radiator fans\":60}") && ths.Contains("\"minutes\":30"), ths[..Math.Min(600, ths.Length)]);
            C("finish without a search is refused", Err(() => Make(state: "none.json").Finish()).Contains("no CPU search"));

            // a soak error loosens every core and soaks again
            runner.Floor = new(); runner.CoreFloor = new();
            var tLs = Make(state: "s-soak.json"); tLs.StartSearch(-30); tLs.State.Stage = "soak"; tLs.State.Values = Enumerable.Repeat(-20, 8).ToArray(); runner.Floor = new() { [3] = -18 };
            tLs.StepOnce();
            C("soak: an error loosens every core by 2 and soaks again", tLs.State.Values!.All(v => v == -18) && tLs.State.SoakTries == 1 && tLs.State.Phase == "searching");
            tLs.StepOnce();
            C("soak: passing finishes the tune", tLs.State.Phase == "done");
            runner.Floor = new(); var tkh = Make(state: "s-soakhot.json"); tkh.StartSearch(-30); tkh.State.Stage = "soak"; tkh.State.Values = Enumerable.Repeat(-20, 8).ToArray(); tkh.SoakSeconds = 5; temp = 88;
            bool more = tkh.StepOnce(); temp = 50;
            C("soak: reaching the temperature limit early is not an error: cool down and soak again; twice = stop with the reason", more && tkh.State.HotSoaks == 1 && tkh.State.Values!.All(v => v == -20) && tkh.State.Phase == "searching", $"more={more} hot={tkh.State.HotSoaks} phase={tkh.State.Phase} stage={tkh.State.Stage} why={tkh.State.StoppedReason} tries={tkh.State.SoakTries} vals={string.Join(",", tkh.State.Values!)}");
            temp = 88; tkh.StepOnce(); temp = 50;
            C("soak: too hot twice = stopped, says cooling limits it", tkh.State.Phase == "stopped" && tkh.State.StoppedReason!.Contains("cooling limits it"), tkh.State.StoppedReason ?? "");
            bool reapplied = false; var tst = Make(state: "s-stopev.json"); tst.Stopped += () => reapplied = true; tst.StartSearch(-30); tst.Stop();
            C("stop: raises Stopped (the owner re-applies the profile's undervolt)", reapplied);
            runner.Floor = new();

            // scores decide: performance = fastest, efficiency = speed per watt
            runner.OutputFor = offs => { double d = offs.Length == 0 ? 0 : -offs.Average(); double sc = 100 + Math.Min(d, 30) - Math.Max(0, d - 30) * 3; return FormattableString.Invariant($"Running FFTv4: Passed  Test Speed:  {sc / 25:0.000} * 10^09  bits / sec\n"); };
            double Curve() { double d = runner.LastOffsets is { Length: > 0 } lo ? -lo.Average() : 0; return 100 + Math.Min(d, 30) - Math.Max(0, d - 30) * 3; }
            (double?, double?, double?, double?) Power() => (200 + 3 * (runner.LastOffsets is { Length: > 0 } lo ? lo.Average() : 0), 48 * Curve(), 54 * Curve(), 100);   // effective clock follows the same curve
            var tpf = Make(state: "s-perf.json"); tpf.LoadProbe = Power; tpf.StartSearch(-30);
            for (int k = 0; k < 40 && tpf.State.Stage == "sweep"; k++) tpf.StepOnce();
            C("performance: the sweep stops once scores fall two steps in a row (clock stretching), re-measures the best three with stock in the same run, then each twice on one core, and keeps the fastest",
                string.Join(",", tpf.State.Sweep!.Select(x => x.Value)) == "0,-5,-10,-15,-20,-25,-30,-35,-40,0,-30,-25,-20,0,-30,-25,-20,0,-30,-25,-20" && tpf.State.Picked == -30, string.Join(",", tpf.State.Sweep!.Select(x => x.Value)) + $" picked {tpf.State.Picked}");
            tpf.Stop();
            var tef = Make(state: "s-eff.json"); tef.LoadProbe = Power; tef.Mode = "efficiency"; tef.StartSearch(-30);
            for (int k = 0; k < 40 && tef.State.Stage == "sweep"; k++) tef.StepOnce();
            C("efficiency: the best speed per watt within 5% of stock (deeper, lower power)", tef.State.Picked == -40, $"picked {tef.State.Picked}");
            tef.Stop();
            var tcmp = Make(state: "s-cmp.json"); int before = runner.Stress.Count;
            var pair = tcmp.CompareCo(new int[8], Enumerable.Repeat(-30, 8).ToArray());
            C("compare: stock vs tuned alternated inside ONE all-core and ONE single-core run, BIOS values back after",
                pair is { Stock.AllCore: > 0, Tuned.AllCore: > 0, Stock.Single: > 0 } && pair.Tuned!.AllCore > pair.Stock!.AllCore * 1.2 && runner.Stress.Count - before == 2
                && runner.LastOffsets!.SequenceEqual(new[] { -3, -1, 0, -5, -2, -4, 0, -1 }), $"{pair?.Stock?.AllCore} {pair?.Tuned?.AllCore} runs {runner.Stress.Count - before}");
            runner.OutputFor = null;
            var noisy = new List<SweepPoint> { new() { Value = 0, Ok = true, AllCore = 100, Single = 10 }, new() { Value = -10, Ok = true, AllCore = 106, Single = 10.6 }, new() { Value = -15, Ok = true, AllCore = 112, Single = 11.2 },
                new() { Value = -15, Ok = true, AllCore = 96, Single = 9.6 }, new() { Value = -10, Ok = true, AllCore = 106, Single = 10.6 } };
            // real 9950X3D readings (2026-11-09): y-cruncher's score jumped around within one run, the effective clock didn't
            var real = new List<SweepPoint> {
                new() { Value = 0, Ok = true, AllCore = 23.10e9, ClockMhz = 4636 }, new() { Value = -5, Ok = true, AllCore = 25.05e9, ClockMhz = 4690 },
                new() { Value = -10, Ok = true, AllCore = 24.35e9, ClockMhz = 4765 }, new() { Value = -15, Ok = true, AllCore = 23.35e9, ClockMhz = 4829 },
                new() { Value = -20, Ok = true, AllCore = 25.20e9, ClockMhz = 4877 }, new() { Value = -25, Ok = true, AllCore = 26.80e9, ClockMhz = 4929 },
                new() { Value = -30, Ok = true, AllCore = 24.15e9, ClockMhz = 4983 }, new() { Value = -35, Ok = true, AllCore = 24.70e9, ClockMhz = 5027 } };
            C("sweep (real readings): scored by effective clock, so a noisy score dip at -30 / -35 doesn't end the descent early (it did, at -25)",
                CpuTuning.NextSweep(real, -50, 5) == -40 && CpuTuning.Composite(real[7], real[0]) > CpuTuning.Composite(real[5], real[0]), $"next {CpuTuning.NextSweep(real, -50, 5)}");
            var mixed = new List<SweepPoint>(real.Take(2)) { new() { Value = 0, Ok = true, Single = 3.81e9, SingleMhz = 5450 }, new() { Value = -5, Ok = true, Single = 3.875e9, SingleMhz = 5520 } };
            var chips = System.Text.Json.JsonSerializer.Serialize(CpuTuning.SweepChips(mixed));
            C("monitor chips: single-core readings are scored against single-core stock (they all showed 0%)", chips.Contains("\"kind\":\"one\",\"score\":1.3") && chips.Contains("\"kind\":\"all\",\"score\":1.2"), chips);
            C("not slower: judged by effective clock when both have it (score noise ignored)",
                CpuTuning.NotSlower(new CpuBench(now, 26.8e9, null, 4636, 80, 160), new CpuBench(now, 24.1e9, null, 4929, 80, 160))
                && !CpuTuning.NotSlower(new CpuBench(now, 24e9, null, 4900, 80, 160), new CpuBench(now, 26e9, null, 4800, 80, 160)));
            // the profile's record of a finished search, from the real 9950X3D run (2026-11-09)
            var doneSt = new CpuState { Phase = "done", Picked = -40, Found = Enumerable.Repeat(-40, 16).ToArray(), Search = new CpuSearchInfo { Mode = "performance" },
                Sweep = new() { new SweepPoint { Value = 0, Ok = true }, new SweepPoint { Value = -40, Ok = true }, new SweepPoint { Value = -45, Ok = false, Why = "y-cruncher: Exception Encountered: AlgorithmFailedException" } } };
            var biosReal = Enumerable.Repeat(-15, 8).Concat(Enumerable.Repeat(-20, 8)).ToArray();
            var benchReal = new CpuBenchPair { Stock = new CpuBench(now, 24.47e9, 4.205e9, 4858, 82, 159.7), Tuned = new CpuBench(now, 25.27e9, 4.373e9, 5041, 82, 158.6) };
            var note = CpuTuning.ResultNote(biosReal, Enumerable.Repeat(-37, 16).ToArray(), doneSt, benchReal, false);
            C("CPU result note: before → after, why (sweep edge, per-core, soak, margin) and the measured change, facts only",
                note.Contains("cores 0–7 −15, cores 8–15 −20 (BIOS) → **−37 on all 16 cores**") && note.Contains("the sweep picked −40 (−45 failed") && note.Contains("every core passed alone at −40")
                && note.Contains("soak passed") && note.Contains("3 steps of margin") && note.Contains("4858 → 5041 MHz (+3.8%)") && note.Contains("single-core score +4.0%") && note.Split('\n').Length <= 4, note);
            C("pick: repeated measurements are averaged (one lucky run doesn't win)", CpuTuning.PickSweep(noisy, null) == -10 && string.Join(",", CpuTuning.RecheckList(noisy.Take(3).ToList())) == "0,-15,-10");
            C("pick: nothing usable when the no-undervolt step fails; 0 when no undervolt beats stock",
                CpuTuning.PickSweep(new[] { new SweepPoint { Value = 0, Ok = false } }, null) == null
                && CpuTuning.PickSweep(new[] { new SweepPoint { Value = 0, Ok = true, AllCore = 100, Single = 10 }, new SweepPoint { Value = -5, Ok = true, AllCore = 90, Single = 9 } }, null) == 0);

            // a freeze during the sweep ends the sweep at that value
            var fz = new CpuState { Phase = "searching", Stage = "sweep", Search = new CpuSearchInfo { StartValue = -30, Since = now, Dir = cc, Cores = 8, Family = "amd-zen5" },
                Values = Enumerable.Repeat(-30, 8).ToArray(), Passes = new int[8], Fails = new int[8], Sweep = new() { new SweepPoint { Value = 0, Ok = true }, new SweepPoint { Value = -5, Ok = true } },
                Testing = new CoreTest { Core = -2, Value = -10, Since = now, Seconds = 35 } };
            File.WriteAllText(Path.Combine(dir, "s-fz.json"), JsonSerializer.Serialize(fz, GlintConfig.Json));
            var tfz = Make(state: "s-fz.json"); tfz.OnStartup();
            C("after a freeze mid-sweep: that value counts as failed and the sweep picks from what it measured", tfz.State.Sweep![^1] is { Value: -10, Ok: false } && tfz.State.Testing == null && tfz.State.Phase == "searching");
            tfz.Stop(); tfz.Dispose();

            // the step logic on its own: deeper by 5, then halve the gap to the first error, then confirm the edge
            var sa = new CpuState { Values = new[] { -30 } }; var seq = new List<int>();
            bool Stable(int v) => v >= -37;
            for (int k = 0; k < 20 && !(sa.Confirm?[0] == true && sa.Passes![0] >= 2); k++) { seq.Add(sa.Values[0]); CpuTuning.Advance(sa, 0, sa.Values[0], Stable(sa.Values[0]), -50, 2); }
            C("advance: -30 pass, -35 pass, -40 fail, -37 pass, -38 fail, then -37 confirmed", string.Join(",", seq) == "-30,-35,-40,-37,-38,-37" && sa.Values[0] == -37, string.Join(",", seq));
            var sb2 = new CpuState { Values = new[] { -30 } };
            CpuTuning.Advance(sb2, 0, -30, true, -50, 2); CpuTuning.Advance(sb2, 0, -35, false, -50, 2);
            CpuTuning.Advance(sb2, 0, -33, false, -50, 2); CpuTuning.Advance(sb2, 0, -32, false, -50, 2); CpuTuning.Advance(sb2, 0, -31, false, -50, 2);
            bool confirming = sb2.Confirm![0] && sb2.Values[0] == -30;
            CpuTuning.Advance(sb2, 0, -30, false, -50, 2);
            C("advance: an error while confirming drops that value and carries on above it", confirming && sb2.Values[0] == -29 && !sb2.Confirm[0] && sb2.Good![0] == null, string.Join(",", sb2.Values));
            var sz = new CpuState { Values = new[] { -1 } }; CpuTuning.Advance(sz, 0, -1, false, -50, 2);
            C("advance: a core that fails at -1 ends at 0 (no undervolt)", sz.Values[0] == 0);
            C("performance mode: within 2% of stock is fine, a bigger all-core or single-core drop is not",
                CpuTuning.NotSlower(new CpuBench(now, 100, 50, null, null, null), new CpuBench(now, 98.5, 51, null, null, null)) && !CpuTuning.NotSlower(new CpuBench(now, 100, 50, null, null, null), new CpuBench(now, 84, 53, null, null, null))
                && !CpuTuning.NotSlower(new CpuBench(now, 100, 50, null, null, null), new CpuBench(now, 101, 45, null, null, null)) && CpuTuning.NotSlower(new CpuBench(now, null, null, null, null, null), new CpuBench(now, 80, 40, null, null, null)));
            // y-cruncher ending cleanly: error text in its output = a failure; no error = restarted, never a false failure
            const string realYc = "  4/5 Time Limit:         5 seconds per test / Run Forever\n  6   Stop on Error:      Enabled\n\nIteration: 0  Total Elapsed Time: 0.003 seconds\nRunning SFTv4: Passed  Test Speed:  7.55 * 10^09  bits / sec\nRunning FFTv4: Passed  Test Speed:  4.45 * 10^09  bits / sec\n";
            C("stress output: y-cruncher's real header and pass lines (captured on hardware) are not errors", CpuTuning.StressErrorText(realYc) == null);
            C("stress output: error lines found, '0 errors' counters ignored", CpuTuning.StressErrorText("Iteration 3\nErrors: 0\n0 errors so far\n") == null && CpuTuning.StressErrorText("Test FFTv4\nERROR: Numerical error detected at core 4\n")!.Contains("Numerical"));
            runner.Floor = new(); runner.CleanExitText = "Coefficient is too large. Errors detected.";
            var tce = Make(state: "s-clean.json"); tce.StartSearch(-10); tce.State.Stage = "cores"; tce.StepOnce();
            C("a clean exit that printed an error fails the core (loosened by 3)", tce.State.Values![0] == -7 && tce.State.Fails![0] == 1);
            tce.Stop(); runner.CleanExitText = "";
            var tq = Make(state: "s-quick.json"); tq.StartSearch(-10); tq.State.Stage = "cores"; tq.StepOnce();
            C("y-cruncher exiting at once with no error stops the search (no core is marked unstable)", tq.State.Phase == "stopped" && tq.State.StoppedReason!.Contains("exiting at once") && tq.State.Fails!.All(f => f == 0), tq.State.StoppedReason ?? "");
            runner.CleanExitText = null;

            // a WHEA hardware error counts as a failure even when y-cruncher kept going
            runner.Floor = new(); whea = 2;
            var tw = Make(state: "s-whea.json"); tw.StartSearch(-10); tw.State.Stage = "cores"; tw.StepOnce();
            C("WHEA errors during a core test fail it (core loosened by 3)", tw.State.Values![0] == -7 && tw.State.Fails![0] == 1);
            whea = 0; tw.Stop(); tw.Dispose();

            // a freeze mid-test: the next start loosens that core and carries on
            var frozen = new CpuState { Phase = "searching", Search = new CpuSearchInfo { StartValue = -30, Since = now, Dir = cc, Cores = 8, Family = "amd-zen5" },
                Values = Enumerable.Repeat(-30, 8).ToArray(), Passes = new int[8], Fails = new int[8], Testing = new CoreTest { Core = 3, Value = -30, Since = now, Seconds = 180 } };
            File.WriteAllText(Path.Combine(dir, "s-frozen.json"), JsonSerializer.Serialize(frozen, GlintConfig.Json));
            var tf = Make(state: "s-frozen.json"); tf.OnStartup();
            C("after a freeze/reboot mid-test: that core failed at that value (loosened), the search resumes with the watchdog",
                tf.State.Values![3] == -29 && tf.State.Fails![3] == 1 && tf.State.Testing == null && tf.State.Phase == "searching" && tf.WatchdogRunning);
            tf.Stop(); tf.Dispose();

            // watchdog (fake clock + sensor)
            var t1 = Make(state: "s1.json"); t1.StartSearch(-20);
            now = now.AddSeconds(1); temp = 70;
            C("watchdog: cool CPU, no trip", !t1.Tick() && t1.State.Phase == "searching");
            temp = 89.9; now = now.AddSeconds(1);
            C("watchdog: just under the limit (90 C on Zen 5), no trip", !t1.Tick());
            temp = 90; now = now.AddSeconds(1);
            C("watchdog: trips at the temperature limit, stops the search, records why", t1.Tick() && t1.State.Phase == "stopped" && t1.State.StoppedReason!.Contains("90") && !t1.WatchdogRunning, t1.State.StoppedReason ?? "");
            C("watchdog: nothing more to do after a trip", !t1.Tick() && !t1.StepOnce());
            temp = 50;
            var t2 = Make(state: "s2.json"); t2.StartSearch(-20);
            temp = null; now = now.AddSeconds(1);
            bool a1 = t2.Tick(); now = now.AddSeconds(CpuTuning.UnreadableTripSeconds - 2);
            C("watchdog: sensor unreadable for less than 10 s does not trip", !a1 && !t2.Tick() && t2.State.Phase == "searching");
            temp = 200; now = now.AddSeconds(3);
            C("watchdog: trips after 10 s without a usable reading (0 / >=150 count as unreadable)", t2.Tick() && t2.State.StoppedReason!.Contains("unreadable"), t2.State.StoppedReason ?? "");
            temp = 50;

            // stop / force finish
            bool t3Fin = false;
            var t3 = Make(state: "s3.json"); t3.Finished += (_, _) => t3Fin = true; t3.StartSearch(-25); t3.StepOnce();
            C("finish refused while the search is still working", Err(() => t3.Finish()).Contains("hasn't finished") && t3.State.Phase == "searching");
            var forced = P(t3.Finish(force: true));
            C("finish(force): incomplete result kept for the BIOS sheet but NOT saved to a profile", t3.State.Phase == "done" && !t3Fin && forced.Contains("\"complete\":false"));
            var t4 = Make(state: "s4.json"); t4.StartSearch(-25); t4.StepOnce();
            var stopped = P(t4.Stop());
            C("stop: ends the search, keeps progress, BIOS values back, nothing more runs", stopped.Contains("\"stopped\":true") && t4.State.Phase == "stopped" && !t4.WatchdogRunning && !t4.StepOnce()
                && runner.LastOffsets!.SequenceEqual(new[] { -3, -1, 0, -5, -2, -4, 0, -1 }));
            name = "AMD Ryzen 9 5900X 12-Core Processor";
            var t5 = Make(state: "s5.json"); var r5 = P(t5.StartSearch(-45));
            C("Ryzen 5000: start value raised to the -30 floor, with a note, Zen 2/3 y-cruncher build", r5.Contains("allows -30") && t5.State.Values![0] == -30 && t5.StepOnce() && runner.Stress[^1].Exe == "19-ZN2 ~ Kagari.exe", r5);
            t5.Stop(); name = "AMD Ryzen 9 9950X 16-Core Processor";
            cfg.Tuning.Enabled = false;
            var t6 = Make(state: "s6.json"); File.WriteAllText(Path.Combine(dir, "s6.json"), JsonSerializer.Serialize(frozen, GlintConfig.Json)); t6 = Make(state: "s6.json"); t6.OnStartup();
            C("OnStartup with tuning turned off stops the search instead of resuming it", t6.State.Phase == "stopped" && !t6.WatchdogRunning);
            cfg.Tuning.Enabled = true;
            C("OnStartup does nothing when no search was running", Err(() => Make(state: "fresh.json").OnStartup()) == "");

            // all-core load test for the PBO / boost-override recommendation
            var tl = Make(state: "load.json");
            tl.LoadProbe = () => (180, 5100, 5450, 100);
            var lr = P(tl.LoadTest(45));
            C("load test: y-cruncher on every logical CPU, temperature / power / clocks sampled, nothing changed",
                runner.Stress[^2].Cfg.Contains("LogicalCores : [0 1") && lr.Contains("\"packagePowerW\":180") && lr.Contains("\"avgClockMhz\":5100") && lr.Contains("\"limitC\":90") && lr.Contains("\"headroomC\":40"), lr);
            temp = 92;
            C("load test: refused when the CPU is already warm", Err(() => tl.LoadTest(45)).Contains("cool down"));
            temp = 50;
            var cpuTemps = new Queue<double>(new[] { 70.0, 85, 91 });
            var tl3 = new CpuTuning(() => cfg, () => cpuTemps.Count > 0 ? cpuTemps.Dequeue() : 91, cc, Path.Combine(dir, "load3.json"), runner, () => name, () => mc, () => ("ASUSTeK COMPUTER INC.", "X"), () => 8, _ => 0, () => now) { AutoRun = false, SampleMs = 2, LoadTimeScale = 0.01, BaselinePath = Path.Combine(dir, "load3.co.json") };
            var hr = P(tl3.LoadTest(45));
            C("load test: stops at the temperature limit and says so (thermal-limited)", hr.Contains("reached the 90") && hr.Contains("\"peakC\":91"), hr);
            var tj = Make(state: "loadjob.json"); tj.LoadProbe = () => (150, 5200, 5400, 100);
            var sj = P(tj.StartLoadTest(45));
            C("load test job: returns at once, a second start and a search are refused while it runs", sj.Contains("\"started\":true") && (Err(() => tj.StartLoadTest(45)).Contains("already running") || !tj.LoadBusy));
            var wj = P(tj.WaitLoad(25));
            C("load test job: tuning.wait shape, result once done, also in the CPU status", wj.Contains("\"busy\":false") && wj.Contains("\"packagePowerW\":150") && P(tj.Status()).Contains("\"loadTest\":{\"running\":false,\"live\":null,\"last\":{"), wj[..Math.Min(200, wj.Length)]);
            var tLstop = Make(state: "loadstop.json"); tLstop.LoadProbe = () => (150, 5200, 5400, 100); tLstop.LoadTimeScale = 1;
            tLstop.StartLoadTest(45);
            string? liveSeen = null; for (int i = 0; i < 200 && liveSeen == null; i++) { Thread.Sleep(5); var st = P(tLstop.Status()); if (st.Contains("\"live\":{\"step\":\"all\"")) liveSeen = st; }
            tLstop.StopLoadTest(); P(tLstop.WaitLoad(25));
            var stStoptop = P(tLstop.Status());
            C("load test: live temperature / power / clock in the status while it runs (for the Monitor), cleared after", liveSeen != null && liveSeen.Contains("\"powerW\":150") && stStoptop.Contains("\"live\":null"), liveSeen?[..Math.Min(300, liveSeen.Length)] ?? "never live");
            C("load test: Stop is not reported as a y-cruncher error and gives no PBO advice", !stStoptop.Contains("PBO: off") && !stStoptop.Contains("stopped with an error"), stStoptop[..Math.Min(400, stStoptop.Length)]);
            var ts2 = Make(state: "load4.json"); ts2.StartSearch(-20);
            C("load test: refused while the per-core search runs", Err(() => ts2.LoadTest(45)).Contains("search is running"));
            ts2.Stop();

            // ---- PBO / boost-override advice from the load test numbers
            var tA = new DateTime(2026, 1, 1);
            C("pbo: near the limit = thermal-limited, leave at Auto", PboAdvisor.Advise(tA, 87, 90, 190, 5100, null, 200, "balanced").Short == "PBO: Auto");
            C("pbo: stopped at the limit = thermal-limited", PboAdvisor.Advise(tA, 91, 90, 200, 5000, "reached the 90 °C limit after 20 s", 200, "performance").Detail.Contains("Thermal-limited"));
            C("pbo: 10 °C+ headroom = Motherboard limits + the family's boost override", PboAdvisor.Advise(tA, 72, 90, 142, 5300, null, 200, "performance").Short == "PBO: Motherboard, +200 MHz");
            C("pbo: no boost override where the family doesn't allow one (X3D)", PboAdvisor.Advise(tA, 72, 90, 142, 5300, null, 0, "performance").Short == "PBO: Motherboard");
            C("pbo: a little headroom = little to gain", PboAdvisor.Advise(tA, 82, 90, 180, 5200, null, 200, "balanced").Detail.Contains("little to gain"));
            C("pbo: quiet goal = reduced limits; an error at stock = no PBO; always says it's a BIOS setting",
                PboAdvisor.Advise(tA, 70, 90, 150, 5000, null, 200, "quiet").Short == "PBO: reduced" && PboAdvisor.Advise(tA, 70, 90, 150, 5000, "y-cruncher stopped with an error (exit 1)", 200, null).Short == "PBO: off"
                && PboAdvisor.Advise(tA, 70, 90, 150, 5000, null, 200, null).Detail.Contains("Glint doesn't change it"));
            C("bench: y-cruncher speed lines averaged (bits/s), none = null", CpuTuning.StressScore("Running FFTv4: Passed  Test Speed:  4.45 * 10^09  bits / sec\nRunning FFTv4: Passed  Test Speed:  4.55 * 10^09  bits / sec") is double sc && Math.Abs(sc - 4.5e9) < 1 && CpuTuning.StressScore("no speed") == null);
            string? pboProf = null; PboAdvice? pboAdv = null; CpuBench? bench = null; bool? verified = null;
            runner.StressOutput = "Running FFTv4: Passed  Test Speed:  9.10 * 10^10  bits / sec\n";
            var tlp = Make(state: "load-pbo.json"); tlp.TargetProfile = () => "Performance (tuned)"; tlp.LoadTested += (pp, a, b, v) => { pboProf = pp; pboAdv = a; bench = b; verified = v; }; tlp.LoadProbe = () => (140, 5300, 5450, 100);
            int stressBefore = runner.Stress.Count;
            var lpr = P(tlp.LoadTest(45));
            C("pbo: a load test hands its advice and its benchmark to the profile the tune made", pboProf == "Performance (tuned)" && pboAdv != null && verified == false && lpr.Contains("\"advice\":{"), lpr[..Math.Min(200, lpr.Length)]);
            C("bench: all-core and single-core scores from y-cruncher's own speed, single run pinned to one CPU",
                bench is { AllCore: > 9e10, Single: > 9e10 } && runner.Stress.Count == stressBefore + 2 && runner.Stress[^1].Cfg.Contains("LogicalCores : [0]") && runner.Stress[^2].Cfg.Contains("LogicalCores : [0 1"), P(bench!));
            tlp.LoadTest(45, verify: true);
            C("bench: the post-search measurement is flagged as the tuned one", verified == true);
            runner.StressOutput = "";

            // ---- read-only Curve Optimizer access check (SMU)
            var now0 = new DateTime(2026, 1, 1);
            C("smu: offsets parsed from the last comma line", SmuProbeTools.ParseOffsets("ZenStates-Core 1.7\r\nCurrent PBO offsets:\r\n-3,-1,0,-5,-2,-4,0,-1\r\n", 8)?.SequenceEqual(new[] { -3, -1, 0, -5, -2, -4, 0, -1 }) == true);
            C("smu: wrong core count / out-of-range / junk is not a reading", SmuProbeTools.ParseOffsets("-3,-1,0", 8) == null && SmuProbeTools.ParseOffsets("-3,-1,0,-5,-2,-4,0,-99", 8) == null && SmuProbeTools.ParseOffsets("error 1,2", 8) == null && SmuProbeTools.ParseOffsets(null, 8) == null);
            var hostileSmu = string.Concat(Enumerable.Repeat("-1,", 40000)) + "x";
            sw.Restart(); var hs = SmuProbeTools.ParseOffsets(hostileSmu + "\n" + new string('9', 90000), 8); sw.Stop();
            C("smu: long hostile output parses fast and isn't a reading", hs == null && sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds} ms");
            var jOk = SmuProbeTools.Judge(now0, 0, "-3,-1,0,-5,-2,-4,0,-1", 8, true, true);
            C("smu: success = ok, offsets, no fix", jOk.Ok && jOk.Offsets!.Length == 8 && jOk.Fix == null && jOk.Output == null);
            var jNoPawn = SmuProbeTools.Judge(now0, 1, "Error: WinIo driver could not be loaded", 8, true, false);
            C("smu: no PawnIO + Memory Integrity: says the fallback drivers are blocked, fix = install PawnIO, keep virtualization on",
                !jNoPawn.Ok && jNoPawn.Summary.Contains("Memory Integrity blocks") && jNoPawn.Fix!.Contains("Install PawnIO") && jNoPawn.Fix.Contains("Leave virtualization"), jNoPawn.Summary);
            var jFall = SmuProbeTools.Judge(now0, 1, "Unhandled exception: inpoutx64 driver failed", 8, true, true);
            C("smu: PawnIO present but the tool fell back to a blocked driver: fix = update PawnIO", !jFall.Ok && jFall.Fix!.Contains("Update PawnIO"), jFall.Summary);
            var jNet = SmuProbeTools.Judge(now0, -2147450730, "You must install or update .NET to run this application. Framework: 'Microsoft.NETCore.App', version '8.0.0'", 8, false, true);
            C("smu: missing .NET runtime is named as such", !jNet.Ok && jNet.Fix!.Contains(".NET 8"), jNet.Summary);
            var jTime = SmuProbeTools.Judge(now0, -1, "", 8, null, true);
            C("smu: a hang is reported as a timeout", !jTime.Ok && jTime.Summary.Contains("in time"));
            var jBig = SmuProbeTools.Judge(now0, 3, new string('e', 50000), 8, false, true);
            C("smu: failure output kept for the user but capped", !jBig.Ok && jBig.Output!.Length <= SmuProbeTools.MaxOutput && jBig.Summary.Contains("exit code 3"));

            var smuDir = Path.Combine(cc, "tools", "ryzen-smu-cli");
            name = "AMD Ryzen 9 9950X3D 16-Core Processor";
            var noSmu = Path.Combine(dir, "cc-nosmu"); Directory.CreateDirectory(Path.Combine(noSmu, "test_programs", "y-cruncher", "Binaries"));
            runner.ToolCalls.Clear();
            var tSmu0 = Make(noSmu, state: "smu0.json");
            C("smu: not bundled = no check, no process", tSmu0.ProbeSmu() == null && runner.ToolCalls.Count == 0);
            Directory.CreateDirectory(smuDir); File.WriteAllText(Path.Combine(smuDir, "ryzen-smu-cli.exe"), "fake");
            var tSmu = Make(state: "smu1.json");
            var pr = tSmu.ProbeSmu();
            C("smu: runs only read commands (offsets, the info / power table, the PBO scalar), never --offset, --set-* or anything that writes",
                pr is { Ok: true } && runner.ToolCalls.SequenceEqual(new[] { "ryzen-smu-cli.exe --get-offsets-terse", "ryzen-smu-cli.exe --get-pbo-scalar" }) && pr.Offsets![3] == -5, string.Join(" | ", runner.ToolCalls));
            C("smu: status carries the result", P(tSmu.Status()).Contains("\"smu\":{") && P(tSmu.Status()).Contains("\"ok\":true"));
            runner.ToolResult = (1, "Error: WinIo driver could not be loaded"); runner.Pawn = false;
            var pr2 = tSmu.ProbeSmu();
            C("smu: failure surfaces why + fix in status", pr2 is { Ok: false } && P(tSmu.Status()).Contains("Install PawnIO"));
            runner.ToolCalls.Clear();
            name = "Intel(R) Core(TM) i9-14900K";
            C("smu: never runs on Intel", Make(state: "smu2.json").ProbeSmu() == null && runner.ToolCalls.Count == 0);
            name = "AMD Ryzen 9 9950X3D 16-Core Processor";
            cfg.Tuning.Enabled = false;
            C("smu: never runs with tuning off", Make(state: "smu3.json").ProbeSmu() == null && runner.ToolCalls.Count == 0);
            cfg.Tuning.Enabled = true;
            var tSearch = Make(state: "smu4.json"); runner.Running = false; tSearch.StartSearch(-20);
            runner.ToolCalls.Clear();
            tSearch.ProbeSmu(); tSearch.Status();
            C("smu: never runs while a search is going (CoreCycler owns the SMU)", runner.ToolCalls.Count == 0);
            tSearch.Stop(); tSearch.Dispose();
            runner.ToolResult = (0, "-1,-1,-1,-1,-1,-1,-1,-1"); runner.Pawn = true; runner.ToolCalls.Clear();
            var tAuto = Make(state: "smu5.json");
            tAuto.Status();
            for (int i = 0; i < 40 && tAuto.Smu == null; i++) Thread.Sleep(25);
            tAuto.Status(); tAuto.Status(); Thread.Sleep(100);
            C("smu: the first status runs the check once in the background, later statuses don't repeat it", tAuto.Smu is { Ok: true } && runner.ToolCalls.Count == 2, runner.ToolCalls.Count.ToString());
            // ---- per-profile CPU undervolt applied through the SMU
            runner.ToolCalls.Clear(); runner.ToolResult = (0, "-5,-5,-5,-5,-5,-5,-5,-5");
            var tCo = Make(state: "co.json");
            C("co: nothing of Glint's applied yet: putting the BIOS values back is a no-op", tCo.ApplyCo(null, "t") == null && runner.ToolCalls.Count == 0);
            var co1 = tCo.ApplyCo(new[] { -20, -20, -18, -25, -20, -22, -19, -21 }, "profile X");
            C("co: reads the BIOS values first, then writes exactly the profile's values",
                co1 == null && runner.ToolCalls.SequenceEqual(new[] { "ryzen-smu-cli.exe --get-offsets-terse", "ryzen-smu-cli.exe --offset -20,-20,-18,-25,-20,-22,-19,-21" }) && tCo.CoApplied![3] == -25, string.Join(" | ", runner.ToolCalls));
            runner.ToolCalls.Clear();
            C("co: positive, past the family floor, or the wrong core count: refused, nothing written",
                tCo.ApplyCo(new[] { 5, 0, 0, 0, 0, 0, 0, 0 }, "t") != null && tCo.ApplyCo(new[] { -60, 0, 0, 0, 0, 0, 0, 0 }, "t") != null && tCo.ApplyCo(new[] { -1, -1 }, "t") != null && runner.ToolCalls.Count == 0);
            tCo.ApplyCo(null, "profile Y");
            C("co: a profile without an undervolt puts the BIOS values back", runner.ToolCalls.SequenceEqual(new[] { "ryzen-smu-cli.exe --offset -5,-5,-5,-5,-5,-5,-5,-5" }) && tCo.CoApplied == null, string.Join(" | ", runner.ToolCalls));
            runner.ToolCalls.Clear(); runner.ToolResult = (1, "error");
            var tCo2 = Make(state: "co2.json");
            C("co: if the current values can't be read first, nothing is written", tCo2.ApplyCo(new[] { -1, -1, -1, -1, -1, -1, -1, -1 }, "t")!.Contains("couldn't read") && runner.ToolCalls.Count == 1);
            runner.ToolResult = (0, "-5,-5,-5,-5,-5,-5,-5,-5"); runner.ToolCalls.Clear();
            var tCo3 = Make(state: "co3.json"); runner.Running = false; tCo3.StartSearch(-20); runner.ToolCalls.Clear();
            C("co: never applied while a search runs (CoreCycler owns the SMU)", tCo3.ApplyCo(new[] { -1, -1, -1, -1, -1, -1, -1, -1 }, "t")!.Contains("search is running") && runner.ToolCalls.Count == 0);
            tCo3.Stop(); tCo3.Dispose();
            cfg.Tuning.Enabled = false;
            C("co: never applied with tuning off", Make(state: "co4.json").ApplyCo(new[] { -1, -1, -1, -1, -1, -1, -1, -1 }, "t") == null && runner.ToolCalls.Count == 0);
            cfg.Tuning.Enabled = true;

            // ---- security review fixes
            runner.ToolResult = (0, "-5,-5,-5,-5,-5,-5,-5,-5"); runner.ToolCalls.Clear();
            var tb1 = Make(state: "base.json"); tb1.ApplyCo(new[] { -20, -20, -20, -20, -20, -20, -20, -20 }, "profile X");
            runner.ToolResult = (0, "-20,-20,-20,-20,-20,-20,-20,-20"); runner.ToolCalls.Clear();   // the chip now holds Glint's values
            var tb2 = Make(state: "base.json"); tb2.ApplyCo(new[] { -10, -10, -10, -10, -10, -10, -10, -10 }, "profile Y"); tb2.ApplyCo(null, "stock");
            C("baseline: a Glint restart in the same boot restores the BIOS values read before its first change, not its own",
                runner.LastOffsets!.SequenceEqual(new[] { -5, -5, -5, -5, -5, -5, -5, -5 }) && !runner.ToolCalls.Any(x => x.Contains("--get-offsets-terse")), string.Join(" | ", runner.ToolCalls));
            File.WriteAllText(Path.Combine(dir, "plant.json.co.json"), JsonSerializer.Serialize(new { boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64), values = new[] { 40, 40, 40, 40, 40, 40, 40, 40 } }, GlintConfig.Json));
            runner.ToolResult = (0, "-5,-5,-5,-5,-5,-5,-5,-5"); runner.ToolCalls.Clear();
            var tb3 = Make(state: "plant.json"); tb3.ApplyCo(new[] { -1, -1, -1, -1, -1, -1, -1, -1 }, "p"); tb3.ApplyCo(null, "stock");
            C("baseline: a planted out-of-range baseline file is ignored (read from the chip instead)", runner.LastOffsets!.SequenceEqual(new[] { -5, -5, -5, -5, -5, -5, -5, -5 }) && runner.ToolCalls.Any(x => x.Contains("--get-offsets-terse")));
            var mism = new CpuState { Phase = "searching", Search = new CpuSearchInfo { StartValue = -30, Since = now, Dir = cc, Cores = 8, Family = "amd-zen3" }, Values = Enumerable.Repeat(-30, 8).ToArray(), Passes = new int[8], Fails = new int[8] };
            File.WriteAllText(Path.Combine(dir, "s-mism.json"), JsonSerializer.Serialize(mism, GlintConfig.Json));
            var tm = Make(state: "s-mism.json"); tm.OnStartup();
            C("resume: a saved search for another CPU family (or core count) is not resumed", tm.State.Phase == "stopped" && !tm.WatchdogRunning && tm.State.StoppedReason!.Contains("doesn't match"));
            C("stream: an error line is caught while y-cruncher streams (not only on exit)", ((IStressRun)new FakeRun(false)).ErrorLine == null && CpuTuning.StressErrorText("FFTv4 ... error detected on core 3") != null);

            C("smu: tuning.cpu.check is a control method, an MCP tool and passes the tuning-only filter",
                ControlServer.Methods.Any(m => m.Method == "tuning.cpu.check") && Cli.HasTool("glint_tuning_cpu_check") && Cli.IsTuningTool("tuning.cpu.check"));

            // AI tool names exist as control methods and MCP tools
            bool toolsExist = AiTuner.Tools.Where(t => t.Contains("_cpu_")).All(t => Cli.HasTool(t) && ControlServer.Methods.Any(m => "glint_" + m.Method.Replace('.', '_') == t));
            C("AI: every CPU tool the tuner may call exists as an MCP tool and control method", toolsExist && AiTuner.Tools.Count(t => t.Contains("_cpu_")) == 6 && !AiTuner.Tools.Contains("glint_tuning_cpu_test"),
                string.Join(",", AiTuner.Tools.Where(t => t.Contains("_cpu_"))));
            C("AI: CPU tools pass the tuning-only MCP filter", new[] { "tuning.cpu.status", "tuning.cpu.start", "tuning.cpu.finish", "tuning.cpu.stop", "tuning.cpu.loadtest" }.All(Cli.IsTuningTool));
            var prompt = AiTuner.SystemPrompt;
            C("AI: prompt tells it the tune takes 30-40 minutes, resumes itself, and to finish on the second call", prompt.Contains("30-40 minutes") && !prompt.Contains("HOURS") && prompt.Contains("resumes by itself") && prompt.Contains("glint_tuning_cpu_finish"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
