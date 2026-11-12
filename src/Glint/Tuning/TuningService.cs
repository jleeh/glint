using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Glint.Core;

namespace Glint.Tuning;

/// <summary>A GPU stress test at one setting, as kept in the history the AI (and the dashboard) reads.</summary>
/// <remarks>Rate = compute throughput (G operations/s, comparable between tests on one card). ThermalLimited / PowerLimited:
/// the card throttled on temperature or its power limit during the test: a performance limit, not instability.</remarks>
public sealed record GpuTestRecord(DateTime Time, int Core, int MemAb, double Seconds, bool Light, bool Ok, string? Why,
    int? LoadMhz, double? PeakCoreC, double? PeakMemC, double? PowerW, double? GBps, string Driver,
    double? Rate = null, bool ThermalLimited = false, bool PowerLimited = false, double? PowerLimitW = null,
    double? AvgFanPct = null, double? PeakFanPct = null, double? AvgFanRpm = null, string? Goal = null, int? PowerPct = null);

/// <summary>A profile's GPU result and stock, tested back to back; Gain = the weighted speed-up (null = nothing beat stock).</summary>
public sealed class GpuBenchPair
{
    public GpuTestRecord? Stock { get; set; }
    public GpuTestRecord? Tuned { get; set; }
    public double? Gain { get; set; }
    public string? Note { get; set; }
}

/// <summary>Everything Glint must remember across restarts and crashes (tuning-state.json next to config.json).</summary>
public sealed class TuningState
{
    /// <summary>The setting being tested, written to disk BEFORE it is applied: if the PC dies, the next start knows what killed it.</summary>
    public GpuTestRecord? Testing { get; set; }
    /// <summary>Offsets the user (or the AI) saved, before results became per profile (migrated into the active profile).</summary>
    public GpuSaved? Saved { get; set; }
    /// <summary>The overclock that belongs to each Glint profile (by profile name): switching profile applies it; a profile
    /// without one runs at stock. Startup apply and crash back-off work on the active profile's entry.</summary>
    public Dictionary<string, GpuSaved> Profiles { get; set; } = new();
    /// <summary>NVML memory offset → reported clock delta (Afterburner units = NVML x ratio). Measured once per card.</summary>
    public double? MemRatio { get; set; }
    public string? RatioFor { get; set; }
    public List<GpuTestRecord> History { get; set; } = new();
    /// <summary>Set when crash watch put the GPU back to stock after repeated crashes; blocks apply-at-startup until cleared.</summary>
    public string? RevertedReason { get; set; }
    /// <summary>Glint profiles the AI owns (name → goal kind). The AI's fan tools only ever touch these, never the user's own profiles.</summary>
    public Dictionary<string, string> AiProfiles { get; set; } = new();
    /// <summary>The profile that was active before the AI switched to its own (restoring it is the user's choice).</summary>
    public string? AiPreviousProfile { get; set; }
    /// <summary>Per-core Curve Optimizer values (a finished, complete CPU search) that belong to each profile: switching to the
    /// profile applies them through the SMU; a profile without any puts back what the BIOS had set.</summary>
    public Dictionary<string, int[]> CpuCo { get; set; } = new();
    /// <summary>The user picked Stock: no profile's overclock or undervolt is applied (also at startup) until they pick a profile or tune.</summary>
    public bool StockMode { get; set; }
    /// <summary>PBO / boost-override advice per profile, from the load test of the tune that made it.</summary>
    public Dictionary<string, PboAdvice> Pbo { get; set; } = new();
    /// <summary>CPU benchmark before (stock) and after (tuned) the undervolt, per profile.</summary>
    public Dictionary<string, CpuBenchPair> CpuBench { get; set; } = new();
    /// <summary>Per profile: its GPU result measured back to back with stock (<see cref="TuningService.StartVerify"/>).</summary>
    public Dictionary<string, GpuBenchPair> GpuBench { get; set; } = new();
    /// <summary>The profile whose CPU undervolt Glint applied in this session (null = none): a crash while it's applied counts against it.</summary>
    public string? CpuAppliedProfile { get; set; }
    public int CpuCrashes { get; set; }
    /// <summary>This session started without the CPU undervolt after a crash with it applied (its shutdown is not counted either way).</summary>
    public bool CpuSkippedSession { get; set; }
    /// <summary>Per profile: what its last tune changed and why (the AI's summary, plus Glint's own CPU result when the search ends later).</summary>
    public Dictionary<string, TuneNotes> Notes { get; set; } = new();
}

/// <summary>The record of one tune, kept with the profile it made.</summary>
public sealed class TuneNotes
{
    public DateTime Time { get; set; }
    public string? Goal { get; set; }
    public string? Target { get; set; }
    /// <summary>The AI's summary of what it changed and why (Markdown).</summary>
    public string? Ai { get; set; }
    /// <summary>Glint's CPU result, written when the search finishes or stops (it outlasts the AI's run).</summary>
    public string? Cpu { get; set; }
}

/// <param name="PowerPct">Power limit as % of the card's default (efficiency mode); null = the default behaviour (unlocked when overclocked).</param>
public sealed record GpuSaved(int Core, int MemAb, DateTime When, string Driver, string Vbios, int TestedCore, int TestedMemAb, int? PowerPct = null);

/// <summary>
/// Overclocking service: owns the GPU (through <see cref="IGpuControl"/>), the stress test and the saved state, and
/// enforces every safety rail itself, so the dashboard, the CLI/MCP and the built-in AI tuner can only ask, never
/// bypass: hard offset caps, temperature cut-offs, refusing to run next to other overclocking tools, driver-reset
/// and ECC detection, back to stock after every test unless a result is explicitly applied.
/// Long work runs as one background job at a time; <see cref="Wait"/> long-polls it (the control API has a 15 s call timeout).
/// Nothing is opened, read or changed until tuning is enabled and something asks.
/// </summary>
public sealed class TuningService : IDisposable
{
    public static string StatePath => Path.Combine(GlintConfig.Dir, "tuning-state.json");
    public const int MaxTestSeconds = 300, MaxHistory = 200;
    static readonly string[] Conflicting = { "MSIAfterburner", "EVGAPrecisionX1", "ASUSGPUTweakIII", "GPUTweakIII", "NVIDIAInspector" };

    readonly Func<GlintConfig> _cfg;
    readonly Func<IGpuControl?> _openGpu;
    readonly Func<IGpuControl, IGpuStress> _openStress;
    readonly Func<double?> _memJunction;
    readonly Action<int?> _fanBoost;
    readonly Func<DateTime, IReadOnlyList<string>> _driverResets;
    readonly Func<IEnumerable<string>> _processes;
    readonly string _statePath;
    readonly object _gate = new();
    IGpuControl? _gpu;
    IGpuStress? _stress;
    bool _golden;
    Thread? _job;
    volatile bool _stop;
    public TuningState State { get; private set; }
    public string Phase { get; private set; } = "idle";
    public string Progress { get; private set; } = "";
    public object? LastResult { get; private set; }
    public string? OpenError { get; private set; }
    /// <summary>Offsets currently applied by Glint (null = stock): what crash watch backs off from.</summary>
    public (int Core, int MemNvml, double MemRatio)? AppliedNow { get; private set; }
    /// <summary>The GPU if it has been opened (crash watch never opens it on its own).</summary>
    public IGpuControl? OpenedGpu => _gpu;
    /// <summary>Raised on every phase / progress change (the dashboard and the AI transcript listen).</summary>
    public event Action<string>? Log;
    /// <summary>The active Glint profile's name (set by the owner; tests set it directly).</summary>
    public Func<string> ActiveProfile { get; set; } = () => "Default";

    /// <summary>The goal of the running AI tuning run (null = none: tests then use the old fixed fan boost). Set by the owner.</summary>
    public TuningGoal? CurrentGoal { get; set; }
    /// <summary>Live duty / rpm of the GPU fans while a goal-based test runs (set by the owner; falls back to the card's own fan reading).</summary>
    public Func<(double? Duty, double? Rpm)?>? FanProbe { get; set; }

    public bool IsAiProfile(string? name) { lock (_gate) return name != null && State.AiProfiles.ContainsKey(name); }
    public string? AiGoalOf(string name) { lock (_gate) return State.AiProfiles.GetValueOrDefault(name); }
    public void MarkAiProfile(string name, string goalKind, string? previous = null)
    {
        lock (_gate) { State.AiProfiles[name] = goalKind; if (previous != null) State.AiPreviousProfile = previous; Save(); }
    }
    /// <summary>A profile that no longer exists can't stay AI-owned (a user profile created later under that name must not inherit it).</summary>
    public void PruneAiProfiles(IEnumerable<string> existing)
    {
        var have = existing.ToHashSet();
        lock (_gate)
        {
            var gone = State.AiProfiles.Keys.Where(k => !have.Contains(k)).ToList();
            if (gone.Count == 0) return;
            foreach (var k in gone) State.AiProfiles.Remove(k);
            Save();
        }
    }
    public string? PreviousProfile { get { lock (_gate) return State.AiPreviousProfile; } }
    public void ClearPreviousProfile() { lock (_gate) { State.AiPreviousProfile = null; Save(); } }

    /// <summary>The active profile's overclock (null = stock).</summary>
    public GpuSaved? ActiveSaved { get { lock (_gate) return State.Profiles.GetValueOrDefault(ActiveProfile()); } }
    /// <summary>Raised (on the job's caller thread) when a background job such as a GPU test starts; the tuning monitor window opens on it.</summary>
    public event Action<string>? JobStarted;
    /// <summary>Raised on the job's own thread when a background job has ended (its name; <see cref="LastResult"/> is set).</summary>
    public event Action<string>? JobEnded;
    /// <summary>Raised when a GPU overclock has been measured against stock: the profile and the one-line outcome.</summary>
    public event Action<string, string>? Verified;

    /// <param name="openGpu">null result = no NVIDIA GPU. Tests pass a <see cref="SimulatedGpu"/>.</param>
    /// <param name="memJunction">Memory-junction temperature from Glint's sensors (LibreHardwareMonitor), validated by the caller or here.</param>
    /// <param name="fanBoost">Hold the GPU fans at least at this duty while testing (null = release).</param>
    public TuningService(Func<GlintConfig> cfg, Func<IGpuControl?> openGpu, Func<IGpuControl, IGpuStress> openStress,
        Func<double?> memJunction, Action<int?> fanBoost, Func<DateTime, IReadOnlyList<string>>? driverResets = null,
        Func<IEnumerable<string>>? processes = null, string? statePath = null)
    {
        _cfg = cfg; _openGpu = openGpu; _openStress = openStress; _memJunction = memJunction; _fanBoost = fanBoost;
        _driverResets = driverResets ?? DriverResetsSince;
        _processes = processes ?? (() => Process.GetProcesses().Select(p => { try { return p.ProcessName; } finally { p.Dispose(); } }));
        _statePath = statePath ?? StatePath;
        State = LoadState(_statePath);
        State.Profiles ??= new();
        State.AiProfiles ??= new();
        State.CpuCo ??= new();
        State.Pbo ??= new();
        State.CpuBench ??= new();
    }

    bool Busy => _job is { IsAlive: true };
    public bool IsBusy => Busy;
    TuningConfig T => _cfg().Tuning ?? new TuningConfig();

    // ------------------------------------------------------------------ startup

    /// <summary>Called once at startup. A test that was running when the PC died is recorded as a failure (the AI and the
    /// search treat that setting as unstable), then the saved result is re-applied if the user asked for that and it's safe.</summary>
    public string? OnStartup(bool lastShutdownWasCrash)
    {
        string? note = null;
        lock (_gate)
        {
            if (State.Testing is { } t)
            {
                State.History.Add(t with { Ok = false, Why = "the PC crashed or froze during this test" });
                State.Testing = null;
                Save();
                note = $"A test at core {t.Core:+0;-0} / memory {t.MemAb:+0;-0} didn't finish (crash or freeze): recorded as unstable.";
            }
        }
        lock (_gate)
            if (State.Saved is { } legacy && State.Profiles.Count == 0) { State.Profiles[ActiveProfile()] = legacy; Save(); }
        var c = T;
        if (!c.Enabled || State.StockMode || ActiveSaved is not { } s) return note;   // the active profile always re-applies (unless the user chose Stock)
        if (lastShutdownWasCrash) return Say("Saved overclock not applied: the last shutdown was a crash.");
        if (State.RevertedReason != null) return Say("Saved overclock not applied: " + State.RevertedReason);
        var gpu = Gpu();
        if (gpu == null) return note;
        if (gpu.Driver != s.Driver || gpu.Vbios != s.Vbios)
            return Say($"Saved overclock not applied: the GPU driver/VBIOS changed ({s.Driver} → {gpu.Driver}). Re-test first.");
        ApplyNow(s.Core, s.MemAb, "startup", s.PowerPct);
        return note;
    }

    // ------------------------------------------------------------------ status (what the AI reads first)

    public object Status()
    {
        var c = T;
        var gpu = c.Enabled ? Gpu() : null;
        var (ceilCore, ceilMem) = gpu == null ? (0, 0) : TuningLimits.Ceilings(gpu.Range(ClockKind.Core), gpu.Range(ClockKind.Memory));
        return new
        {
            enabled = c.Enabled,
            busy = Busy, phase = Phase, progress = Progress,
            gpu = gpu == null ? null : new
            {
                name = gpu.Name, driver = gpu.Driver, vbios = gpu.Vbios, vramGiB = Math.Round(gpu.VramGiB, 1),
                powerLimitW = new { @default = gpu.PowerLimitW.Default, max = gpu.PowerLimitW.Max, now = gpu.PowerLimitNowW },
                slowdownC = gpu.SlowdownC,
                workstation = gpu.IsWorkstation, tunable = Blocked(gpu) == null, blocked = Blocked(gpu),
                offsets = new { core = gpu.GetOffset(ClockKind.Core), memAb = ToAb(gpu.GetOffset(ClockKind.Memory)) },
                eccCorrected = gpu.EccCorrected(),
            },
            gpuError = c.Enabled ? OpenError : null,
            limits = new { maxCore = ceilCore, maxMemAb = ceilMem, emergencyCoreC = TuningLimits.EmergencyCore(c.EmergencyCoreC, gpu?.SlowdownC),
                           emergencyMemC = TuningLimits.EmergencyMem(c.EmergencyMemC), unlockPower = c.UnlockPower, maxTestSeconds = MaxTestSeconds },
            compare = Compare(gpu?.Driver),
            profileStats = ProfileStats(gpu?.Driver),
            memRatio = State.MemRatio,
            // the test in progress (for the monitor window's progress bar) and a live sample while busy
            testing = State.Testing == null ? null : new { core = State.Testing.Core, memAb = State.Testing.MemAb, seconds = State.Testing.Seconds,
                                                           light = State.Testing.Light, startedAt = State.Testing.Time },
            live = Busy && gpu != null ? Live(gpu) : null,
            saved = ActiveSaved, activeProfile = ActiveProfile(), profiles = Snap(() => new Dictionary<string, GpuSaved>(State.Profiles)),
            goal = CurrentGoal == null ? null : new { kind = CurrentGoal.Kind, text = CurrentGoal.Text, target = CurrentGoal.Target, profile = CurrentGoal.ProfileName, objective = CurrentGoal.Objective },
            aiProfiles = Snap(() => new Dictionary<string, string>(State.AiProfiles)), cpuProfiles = Snap(() => new Dictionary<string, int[]>(State.CpuCo)), pboProfiles = Snap(() => new Dictionary<string, PboAdvice>(State.Pbo)), cpuBench = Snap(() => new Dictionary<string, CpuBenchPair>(State.CpuBench)), gpuBench = Snap(() => new Dictionary<string, GpuBenchPair>(State.GpuBench)), notes = Snap(() => State.Notes.Keys.Concat(State.Profiles.Keys).Concat(State.CpuCo.Keys).Distinct().Select(k => (k, text: NotesFor(k), n: State.Notes.GetValueOrDefault(k)))
                .Where(x => x.text != null).ToDictionary(x => x.k, x => new { time = x.n?.Time ?? State.Profiles.GetValueOrDefault(x.k)?.When, goal = x.n?.Goal, target = x.n?.Target, text = x.text })), stockMode = State.StockMode, previousProfile = State.AiPreviousProfile, activeIsAi = State.AiProfiles.ContainsKey(ActiveProfile()), reverted = State.RevertedReason,
            history = Snap(() => State.History.TakeLast(30).Reverse().ToArray()),
            last = LastResult,
        };
    }

    // ------------------------------------------------------------------ GPU tests

    /// <summary>Start a stress test at these offsets (memory in MSI Afterburner units). Returns at once; poll with <see cref="Wait"/>.
    /// The card goes back to stock when the test ends, pass or fail.</summary>
    /// <summary>performance (fastest) | efficiency (most work per watt: a lower power limit) for this tune; set by the owner.</summary>
    public string GpuMode { get; set; } = "performance";
    public const int MinPowerPct = 50;

    public object StartGpuTest(int core, int memAb, double seconds, bool light, int? powerPct = null)
    {
        if (powerPct is int pp0 && (pp0 < MinPowerPct || pp0 > 100)) throw new InvalidOperationException($"powerPct must be {MinPowerPct}..100 (% of the card's default power limit)");
        var gpu = Ready();
        var (ceilCore, ceilMem) = TuningLimits.Ceilings(gpu.Range(ClockKind.Core), gpu.Range(ClockKind.Memory));
        if (core < 0 || core > ceilCore) throw new InvalidOperationException($"core must be 0..{ceilCore} MHz on this card");
        if (memAb < 0 || memAb > ceilMem) throw new InvalidOperationException($"memAb must be 0..{ceilMem} (Afterburner units) on this card");
        seconds = Math.Clamp(seconds, 10, MaxTestSeconds);
        if (CurrentGoal is { } g0 && !IsAiProfile(ActiveProfile()))
            throw new InvalidOperationException($"a {g0.Kind} goal run needs Glint on the AI's own profile (\"{g0.ProfileName}\") so the fans follow its curve: call tuning.profile.prepare first");
        Run("gpu test", () => GpuTest(gpu, core, memAb, seconds, light, powerPct));
        return new { started = true, core, memAb, seconds, light, powerPct, note = "poll tuning.wait until busy=false" };
    }

    /// <summary>Wait up to <paramref name="seconds"/> for the running job; returns status either way.</summary>
    public object Wait(double seconds)
    {
        var j = _job;
        j?.Join(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 25)));
        return new { busy = Busy, phase = Phase, progress = Progress, result = Busy ? null : LastResult };
    }

    public object Stop()
    {
        _stop = true;
        return new { stopping = Busy };
    }

    GpuTestRecord GpuTest(IGpuControl gpu, int core, int memAb, double seconds, bool light, int? powerPct = null)
    {
        var c = T;
        double? slowdown = gpu.SlowdownC;
        double abortCore = TuningLimits.EmergencyCore(c.EmergencyCoreC, slowdown), abortMem = TuningLimits.EmergencyMem(c.EmergencyMemC);
        double hotAt = (slowdown ?? abortCore - 5) - 2;   // at the card's own throttle point: performance-limited, not unstable
        var stress = Stress(gpu);
        double ratio = Ratio(gpu, stress);
        int memNvml = TuningLimits.ClampMemNvml((int)Math.Round(memAb / ratio), ratio);
        var rec = new GpuTestRecord(DateTime.Now, core, memAb, seconds, light, false, null, null, null, null, null, null, gpu.Driver);
        lock (_gate) { State.Testing = rec; Save(); }     // on disk before the card sees the setting
        if (AppliedNow != null) { AppliedNow = null; Applied?.Invoke(0, 0); }   // the test ends at stock
        double peakCore = 0, peakMem = 0, power = 0; int loadMhz = 0, samples = 0; bool thermal = false, powerCap = false;
        var goal = CurrentGoal;   // goal run: the fans follow the AI profile's curve and their duty is the noise proxy
        double fanSum = 0, fanPeak = 0, rpmSum = 0; int fanN = 0, rpmN = 0;
        void Tick()
        {
            if (_stop) throw new TuneAbort("stopped");
            var s = gpu.Sample();
            var tc = TuningLimits.ValidTemp(s.CoreC);
            var tm = TuningLimits.ValidTemp(_memJunction());
            if (tc is double a) { peakCore = Math.Max(peakCore, a); if (a >= hotAt) thermal = true; if (a >= abortCore) throw new TuneAbort($"emergency stop: GPU core {a:0} °C reached {abortCore:0} °C"); }
            if (tm is double m) { peakMem = Math.Max(peakMem, m); if (m >= abortMem) throw new TuneAbort($"emergency stop: memory junction {m:0} °C reached {abortMem:0} °C"); }
            if (goal != null)
            {
                var fp = SafeProbe();
                double? duty = fp?.Duty ?? (s.FanPct > 0 ? s.FanPct : null);
                if (duty is double fd) { fanSum += fd; fanPeak = Math.Max(fanPeak, fd); fanN++; }
                if (fp?.Rpm is double fr and > 0) { rpmSum += fr; rpmN++; }
            }
            if (s.UtilPct >= 30)
            {
                loadMhz = Math.Max(loadMhz, s.CoreMhz); power += s.PowerW; samples++;
                foreach (var why in s.Throttle)
                {
                    if (why.Contains("thermal", StringComparison.OrdinalIgnoreCase)) thermal = true;
                    if (why.Contains("power", StringComparison.OrdinalIgnoreCase)) powerCap = true;
                }
            }
        }
        var t0 = DateTime.Now;
        long? ecc0 = gpu.EccCorrected();
        double? powerLimitUsed = null;
        StressResult? r1 = null, r2 = null;
        string? why = null;
        try
        {
            if (goal == null) _fanBoost(85);   // no goal: the old fixed boost. A goal run lets the AI profile's curve drive the fans
            powerLimitUsed = powerPct is int pp ? LimitPower(gpu, pp) : UnlockPower(gpu);
            gpu.SetOffset(ClockKind.Core, TuningLimits.ClampCore(core));
            gpu.SetOffset(ClockKind.Memory, memNvml);
            Thread.Sleep(500);
            Say($"Testing core {core:+0;-0} / memory {memAb:+0;-0} for {seconds:0} s{(light ? " (light load, top boost clocks)" : "")}");
            double half = light ? seconds : seconds / 2;
            r1 = stress.RunCore(half, light, Tick);
            if (r1.Ok && !light) { Say("Compute OK, now VRAM"); r2 = stress.RunMemory(seconds - half, Tick); }
            why = !r1.Ok ? r1.Why ?? "wrong results" : r2 is { Ok: false } ? r2.Why ?? "VRAM errors" : null;
            if (why == null && gpu.EccCorrected() is long e1 && ecc0 is long e0 && e1 > e0) why = $"{e1 - e0} ECC-corrected VRAM errors (the memory offset is too high)";
            var resets = _driverResets(t0);
            if (resets.Count > 0) why = "driver reset: " + resets[0];
        }
        catch (TuneAbort a) { why = a.Message; }
        catch (Exception e) { why = "GPU fault: " + e.Message; }
        finally
        {
            foreach (var err in gpu.ResetAll()) Say("reset: " + err);
            _fanBoost(null);
            if (r1?.Fault == true || r2?.Fault == true || (why?.StartsWith("driver reset") ?? false)) { try { stress.Reopen(); } catch { _golden = false; } }
        }
        rec = rec with { Ok = why == null, Why = why, LoadMhz = loadMhz > 0 ? loadMhz : null, PeakCoreC = peakCore > 0 ? peakCore : null,
                         PeakMemC = peakMem > 0 ? peakMem : null, PowerW = samples > 0 ? Math.Round(power / samples, 1) : null, GBps = r2?.GBps,
                         Rate = r1 is { Ok: true, Rate: > 0 } ? Math.Round(r1.Rate / 1e9, 1) : null, ThermalLimited = thermal, PowerLimited = powerCap,
                         PowerLimitW = powerLimitUsed,
                         AvgFanPct = fanN > 0 ? Math.Round(fanSum / fanN, 1) : null, PeakFanPct = fanN > 0 ? Math.Round(fanPeak, 1) : null,
                         AvgFanRpm = rpmN > 0 ? Math.Round(rpmSum / rpmN) : null, Goal = goal?.Kind, PowerPct = powerPct };
        lock (_gate)
        {
            State.Testing = null;
            State.History.Add(rec);
            if (State.History.Count > MaxHistory) State.History.RemoveRange(0, State.History.Count - MaxHistory);
            Save();
        }
        var limits = string.Join(", ", new[] { rec.ThermalLimited ? "thermal-limited" : null, rec.PowerLimited ? "power-limited" : null }.Where(x => x != null));
        Say(rec.Ok ? $"PASS core {core:+0;-0} / memory {memAb:+0;-0}: {rec.LoadMhz} MHz under load, {rec.PeakCoreC:0} °C{(rec.AvgFanPct is double af ? $", fans {af:0}% avg" : "")}{(limits != "" ? " (" + limits + ")" : "")}" : $"FAIL core {core:+0;-0} / memory {memAb:+0;-0}: {why}");
        return rec;
    }

    /// <summary>Weighted speed-up of <paramref name="t"/> over <paramref name="s"/>: 75% compute rate, 25% VRAM bandwidth (whichever exist).</summary>
    public bool HasGpuResult(string profile) { lock (_gate) return State.Profiles.TryGetValue(profile, out var s) && (s.Core > 0 || s.MemAb > 0); }
    public bool VerifiedSince(string profile, DateTime since) { lock (_gate) return State.GpuBench.TryGetValue(profile, out var b) && (b.Tuned ?? b.Stock)?.Time >= since; }
    /// <summary>A profile's saved GPU overclock (null = stock) and its measured gain over stock in %, for the "tune finished" notification.</summary>
    public ((int Core, int MemAb, int? PowerPct)? Gpu, double? GainPct) ResultOf(string profile)
    {
        lock (_gate)
        {
            if (!State.Profiles.TryGetValue(profile, out var s) || (s.Core == 0 && s.MemAb == 0 && s.PowerPct == null)) return (null, null);
            return ((s.Core, s.MemAb, s.PowerPct), State.GpuBench.GetValueOrDefault(profile)?.Gain);
        }
    }

    public static double? Gain(GpuTestRecord t, GpuTestRecord s)
    {
        double? c = t.Rate is > 0 && s.Rate is > 0 ? t.Rate / s.Rate - 1 : null;
        double? v = t.GBps is > 0 && s.GBps is > 0 ? t.GBps / s.GBps - 1 : null;
        if (c is double a && v is double b) return 0.75 * a + 0.25 * b;
        return c ?? v ?? (t.LoadMhz is > 0 && s.LoadMhz is > 0 ? (double)t.LoadMhz.Value / s.LoadMhz.Value - 1 : null);
    }

    /// <summary>The smallest speed-up over stock worth keeping an overclock for.</summary>
    public double MinGain { get; set; } = 0.015;
    public double VerifySeconds { get; set; } = 120;
    /// <summary>Pause between the back-to-back tests so both start from a similar temperature.</summary>
    public int VerifyCoolSec { get; set; } = 45;

    /// <summary>
    /// Check a profile's saved GPU result against stock, back to back (same length, same fans, minutes apart instead of hours).
    /// Keeps it if it is at least <see cref="MinGain"/> faster; otherwise lowers memory (1000 at a time), then core (50), and tries
    /// again; if nothing beats stock the profile's GPU overclock is removed. A job: returns at once, poll <see cref="Wait"/>.
    /// </summary>
    /// <summary>
    /// Apply <paramref name="profile"/> once the running job (whose own thread counts as busy) has ended: a job like verify that
    /// changes the active profile's result must not leave the card at the stock its last test ended on.
    /// </summary>
    void ReapplyAfterJob(string profile) => _ = Task.Run(() =>
    {
        for (int i = 0; i < 200 && Busy; i++) Thread.Sleep(100);
        try { if (profile == ActiveProfile()) OnProfileChanged(profile); } catch (Exception e) { Say("profile not re-applied: " + e.Message); }
    });

    /// <summary>The active profile's saved result, applied as a profile switch would (its power limit too; leaves stock mode).</summary>
    public string? ApplySaved()
    {
        var p = ActiveProfile();
        if (ActiveSaved == null) throw new InvalidOperationException("no saved result for this profile yet");
        SetStockMode(false);
        return OnProfileChanged(p);
    }

    public object StartVerify(string? profile = null)
    {
        var gpu = Ready();
        profile ??= CurrentGoal?.ProfileName ?? ActiveProfile();
        GpuSaved s;
        lock (_gate) { if (!State.Profiles.TryGetValue(profile, out var have) || (have.Core == 0 && have.MemAb == 0 && have.PowerPct == null)) throw new InvalidOperationException($"\"{profile}\" has no GPU overclock saved to check"); s = have; }
        var p = profile;
        Run("gpu verify", () => Verify(gpu, p, s.Core, s.MemAb, s.PowerPct));
        return new { started = true, profile, core = s.Core, memAb = s.MemAb, takes = $"about {Math.Round((VerifySeconds * 2 + VerifyCoolSec) / 60.0, 0)}-{Math.Round((VerifySeconds * 5 + VerifyCoolSec * 4) / 60.0, 0)} min", note = "poll tuning.wait until busy=false" };
    }

    /// <summary>Efficiency: work (75% compute, 25% bandwidth) per watt vs stock.</summary>
    public static double? EfficiencyGain(GpuTestRecord t, GpuTestRecord s) =>
        Gain(t, s) is double g && t.PowerW is > 0 && s.PowerW is > 0 ? (1 + g) / (t.PowerW.Value / s.PowerW.Value) - 1 : null;

    object Verify(IGpuControl gpu, string profile, int core, int memAb, int? powerPct = null)
    {
        bool eff = GpuMode == "efficiency" || powerPct != null;
        void Cool()
        {
            var until = DateTime.Now.AddSeconds(VerifyCoolSec);
            while (DateTime.Now < until) { if (_stop) throw new TuneAbort("stopped"); try { if (gpu.Sample().CoreC is double c && c < 50) break; } catch { } Thread.Sleep(1000); }
        }
        Say($"Checking {profile} against stock, back to back");
        var stock = GpuTest(gpu, 0, 0, VerifySeconds, false);
        if (!stock.Ok) return new { ok = false, profile, why = "stock itself failed: " + stock.Why };
        int c0 = core, m0 = memAb; int? p0 = powerPct;
        for (int round = 0; round < 4 && (core > 0 || memAb > 0 || powerPct != null); round++)
        {
            Cool();
            var t = GpuTest(gpu, core, memAb, VerifySeconds, false, powerPct);
            double? speed = t.Ok ? Gain(t, stock) : null;
            // performance: faster and compute never below stock; efficiency: 5%+ more work per watt and no more than 10% slower
            var g = eff ? (t.Ok ? EfficiencyGain(t, stock) : null) : speed;
            bool computeKept = eff ? speed is >= -0.10 : !(t.Rate is > 0 && stock.Rate is > 0 && t.Rate < stock.Rate);
            if (g is double gain && gain >= (eff ? 0.05 : MinGain) && computeKept)
            {
                if (core != c0 || memAb != m0 || powerPct != p0)
                    lock (_gate) { State.Profiles[profile] = new GpuSaved(core, memAb, DateTime.Now, gpu.Driver, gpu.Vbios, core, memAb, powerPct); Save(); }   // lower than proven, and just retested
                lock (_gate) { State.GpuBench[profile] = new GpuBenchPair { Stock = stock, Tuned = t, Gain = Math.Round(gain * 100, 1) }; Save(); }
                if (profile == ActiveProfile()) ReapplyAfterJob(profile);
                var msg = $"{profile}: core {core:+0;-0} / memory {memAb:+0;-0}{(powerPct is int pv ? $" at {pv}% power" : "")} is {gain * 100:0.0}% {(eff ? $"more efficient ({(speed ?? 0) * 100:+0.0;-0.0}% speed)" : "faster")} than stock, tested back to back{(core != c0 || memAb != m0 || powerPct != p0 ? $" (changed from {c0:+0;-0} / {m0:+0;-0}{(p0 is int pp1 ? $" at {pp1}%" : "")})" : "")}";
                Say(msg);
                try { Verified?.Invoke(profile, msg); } catch { }
                return new { ok = true, profile, core, memAb, gainPct = Math.Round(gain * 100, 1), stock, tuned = t, note = msg };
            }
            Say($"core {core:+0;-0} / memory {memAb:+0;-0}: {(!t.Ok ? "failed: " + t.Why : !computeKept ? (eff ? "too much slower than stock" : "compute slower than stock") : $"{(g ?? 0) * 100:0.0}% vs stock, not enough")}; {(eff && t.Ok && !computeKept && powerPct is int pq && pq < 100 ? "raising the power limit" : "lowering")}");
            if (eff && t.Ok && !computeKept && powerPct is int pr && pr < 100) powerPct = Math.Min(100, pr + 10);   // too slow: give it more power
            else if (memAb > 0) memAb = Math.Max(0, memAb - 1000); else if (core > 0) core = Math.Max(0, core - 50); else powerPct = powerPct is int pz ? Math.Min(100, pz + 10) : null;
        }
        lock (_gate) { State.Profiles.Remove(profile); State.GpuBench[profile] = new GpuBenchPair { Stock = stock, Note = $"No overclock was faster than stock by {MinGain * 100:0.#}% or more when tested back to back, so this profile runs the GPU at stock." }; Save(); }
        if (profile == ActiveProfile()) ReapplyAfterJob(profile);
        Say($"{profile}: no overclock beat stock back to back: the GPU runs at stock in this profile");
        try { Verified?.Invoke(profile, $"{profile}: no overclock beat stock when tested back to back, so the GPU runs at stock in this profile"); } catch { }
        return new { ok = true, profile, core = 0, memAb = 0, gainPct = (double?)null, stock, note = "no overclock beat stock: the profile's GPU overclock was removed" };
    }

    // ------------------------------------------------------------------ apply / save / stock

    /// <summary>Apply offsets now (until reboot or stock). Never above what a passing test proved on this driver.</summary>
    /// <param name="force">Manual overclocking from the dashboard only (the user confirmed applying an untested setting):
    /// the hard caps still apply, and crash watch still backs it off. Never set by the AI or the MCP tools.</param>
    public object Apply(int core, int memAb, bool force = false)
    {
        var gpu = Ready();
        var (ceilCore, ceilMem) = TuningLimits.Ceilings(gpu.Range(ClockKind.Core), gpu.Range(ClockKind.Memory));
        if (core < 0 || core > ceilCore || memAb < 0 || memAb > ceilMem) throw new InvalidOperationException($"outside this card's limits (core 0..{ceilCore}, memory 0..{ceilMem})");
        bool proven = Proven(core, memAb, gpu.Driver);
        if (!proven && !force) throw new InvalidOperationException($"core {core:+0;-0} / memory {memAb:+0;-0} hasn't passed a full test on this driver: test it first");
        return ApplyNow(core, memAb, proven ? "user" : "manual, untested");
    }

    /// <summary>The active Glint profile changed: apply that profile's overclock, or go to stock if it has none (only if
    /// Glint applied one). Same safety as startup: not after a revert, not on another driver, not while a test runs.</summary>
    public bool StockMode { get { lock (_gate) return State.StockMode; } }
    public void SetStockMode(bool on) { lock (_gate) { if (State.StockMode == on) return; State.StockMode = on; Save(); } }

    public string? OnProfileChanged(string profile)
    {
        if (!T.Enabled || Busy) return null;
        if (StockMode) { if (AppliedNow != null) Stock(); return null; }
        GpuSaved? s; lock (_gate) s = State.Profiles.GetValueOrDefault(profile);
        if (s == null) { if (AppliedNow != null) Stock(); return null; }
        if (State.RevertedReason != null) return Say($"Profile {profile}: overclock not applied ({State.RevertedReason})");
        var gpu = Gpu();
        if (gpu == null) return null;
        if (gpu.Driver != s.Driver) return Say($"Profile {profile}: overclock not applied, the GPU driver changed ({s.Driver} → {gpu.Driver}). Re-test first.");
        ApplyNow(s.Core, s.MemAb, "profile " + profile, s.PowerPct);
        return null;
    }

    object ApplyNow(int core, int memAb, string by, int? powerPct = null)
    {
        var gpu = Ready(allowWhileBusy: false);
        double ratio = State.MemRatio ?? 0.5;
        int c = TuningLimits.ClampCore(core), m = TuningLimits.ClampMemNvml((int)Math.Round(memAb / ratio), ratio);
        // a profile without a power limit of its own never inherits an efficiency profile's lowered one: back to the default first
        if (powerPct is int pp) LimitPower(gpu, pp);
        else { if (_powerLowered) LimitPower(gpu, 100); if (c != 0 || m != 0) UnlockPower(gpu); }
        _powerLowered = powerPct is < 100;
        gpu.SetOffset(ClockKind.Core, c);
        gpu.SetOffset(ClockKind.Memory, m);
        AppliedNow = c == 0 && m == 0 && powerPct == null ? null : (c, m, ratio);
        Say($"Applied core {core:+0;-0} / memory {memAb:+0;-0}{(powerPct is int p2 ? $", power limit {p2}%" : "")} ({by})");
        Applied?.Invoke(core, memAb);
        return new { applied = true, core, memAb };
    }

    /// <summary>Raised when offsets are applied (crash watch starts watching) or the card goes back to stock (core=memAb=0).</summary>
    public event Action<int, int>? Applied;

    /// <summary>Save offsets as the result (what startup and crash back-off use). Must be proven by a passing full test.</summary>
    /// <param name="profile">Which Glint profile the result belongs to (default: the active one).</param>
    /// <param name="force">Manual save of an untested setting (dashboard only, after the user confirmed).</param>
    public object SaveResult(int core, int memAb, string? profile = null, bool force = false, int? powerPct = null)
    {
        if (powerPct is int pp0 && (pp0 < MinPowerPct || pp0 > 100)) throw new InvalidOperationException($"powerPct must be {MinPowerPct}..100");
        var gpu = Ready();
        profile ??= ActiveProfile();
        if (CurrentGoal != null && !force && !IsAiProfile(profile))
            throw new InvalidOperationException($"the AI only saves into its own profile (\"{CurrentGoal.ProfileName}\"), never into \"{profile}\": call tuning.profile.prepare first");
        bool proven = Proven(core, memAb, gpu.Driver);
        if (!proven && !force) throw new InvalidOperationException("only a setting at or below one that passed a full test on this driver can be saved");
        var best = State.History.Where(h => h.Ok && !h.Light && h.Driver == gpu.Driver && h.Core >= core && h.MemAb >= memAb).OrderBy(h => h.Core + h.MemAb).FirstOrDefault();
        var saved = new GpuSaved(core, memAb, DateTime.Now, gpu.Driver, gpu.Vbios, best?.Core ?? 0, best?.MemAb ?? 0, powerPct);
        lock (_gate)
        {
            State.Profiles[profile] = saved;
            State.RevertedReason = null;
            Save();
        }
        Say($"Saved for profile {profile}: core {core:+0;-0} / memory {memAb:+0;-0}{(powerPct is int p3 ? $", power limit {p3}%" : "")}{(proven ? "" : " (untested)")}");
        return new { saved, profile };
    }

    /// <summary>A Glint profile was renamed: its overclock (GPU and CPU) follows it.</summary>
    public void RenameProfile(string from, string to)
    {
        lock (_gate)
        {
            if (from == to) return;
            bool changed = false;
            if (State.Profiles.Remove(from, out var s)) { State.Profiles[to] = s; changed = true; }
            if (State.CpuCo.Remove(from, out var co)) { State.CpuCo[to] = co; changed = true; }
            if (State.Pbo.Remove(from, out var pb)) { State.Pbo[to] = pb; changed = true; }
            if (State.CpuBench.Remove(from, out var cb)) { State.CpuBench[to] = cb; changed = true; }
            if (State.GpuBench.Remove(from, out var gb)) { State.GpuBench[to] = gb; changed = true; }
            if (State.Notes.Remove(from, out var nb)) { State.Notes[to] = nb; changed = true; }
            if (State.AiProfiles.Remove(from, out var ak)) { State.AiProfiles[to] = ak; changed = true; }
            if (State.AiPreviousProfile == from) { State.AiPreviousProfile = to; changed = true; }
            if (State.CpuAppliedProfile == from) { State.CpuAppliedProfile = to; changed = true; }
            if (changed) Save();
        }
    }

    /// <summary>The CPU undervolt (per-core Curve Optimizer values) saved for a profile, or null.</summary>
    public int[]? CpuFor(string? profile) { lock (_gate) return profile != null && State.CpuCo.TryGetValue(profile, out var v) ? v.ToArray() : null; }

    public void MarkCpuApplied(string? profile) { lock (_gate) { if (State.CpuAppliedProfile == profile) return; State.CpuAppliedProfile = profile; if (profile == null) State.CpuCrashes = 0; Save(); } }

    /// <summary>Startup after a crash: if the crash happened with a CPU undervolt applied, count it; at <paramref name="max"/> in a row the
    /// profile's undervolt is removed (it re-applies at every start, so a bad one must not crash-loop the PC). Returns a note or null.</summary>
    public string? OnCpuCrash(bool crashed, int max = 2)
    {
        lock (_gate)
        {
            var p = State.CpuAppliedProfile;
            if (p == null) return null;
            // the last session ran without the undervolt (skipped after a crash): its shutdown says nothing about the undervolt,
            // so the count neither resets nor grows; a crash in it keeps this session skipped too
            if (State.CpuSkippedSession) { State.CpuSkippedSession = crashed; Save(); return crashed ? $"Crash with the CPU undervolt of {p} not applied: it isn't counted, and stays off this start." : null; }
            if (!crashed) { if (State.CpuCrashes != 0) { State.CpuCrashes = 0; Save(); } return null; }
            State.CpuCrashes++;
            if (State.CpuCrashes < max) { State.CpuSkippedSession = true; Save(); return $"Crash with the CPU undervolt of {p} applied ({State.CpuCrashes}/{max}): not applied this start."; }
            State.CpuCo.Remove(p); State.CpuBench.Remove(p); State.CpuAppliedProfile = null; State.CpuCrashes = 0; State.CpuSkippedSession = false; Save();
            return $"{max} crashes in a row with the CPU undervolt of {p} applied: it was removed from the profile (tune again to find a stable one).";
        }
    }

    /// <summary>A new tune of <paramref name="profile"/>: its notes start over.</summary>
    public void StartNotes(string profile, string? goal, string? target, DateTime now)
    { lock (_gate) { State.Notes[profile] = new TuneNotes { Time = now, Goal = goal, Target = target }; Save(); } }
    /// <summary>Change a profile's notes (created if the profile has none).</summary>
    public void EditNotes(string profile, Action<TuneNotes> f)
    {
        lock (_gate)
        {
            if (!State.Notes.TryGetValue(profile, out var n)) State.Notes[profile] = n = new TuneNotes { Time = DateTime.Now };
            f(n); Save();
        }
    }
    /// <summary>The whole record as Markdown: the AI's part, then the CPU result.</summary>
    public static string? NotesText(TuneNotes? n) => n == null ? null : string.Join("\n\n", new[] { n.Ai, n.Cpu }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } t ? t : null;

    /// <summary>
    /// A profile's record for display: its notes, and where a part is missing (a profile tuned before notes existed, or an AI run
    /// that ended without a summary) the facts Glint holds for it: the saved GPU overclock and its back-to-back result.
    /// </summary>
    public string? NotesFor(string profile)
    {
        lock (_gate)
        {
            State.Notes.TryGetValue(profile, out var n);
            string? ai = n?.Ai is { Length: > 0 } a ? a : GpuFact(profile);
            return string.Join("\n\n", new[] { ai, n?.Cpu }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } t ? t : null;
        }
    }

    /// <summary>The saved GPU result in one or two lines (null when the profile has none).</summary>
    string? GpuFact(string profile)
    {
        if (!State.Profiles.TryGetValue(profile, out var g)) return null;
        static string S(int v) => v >= 0 ? $"+{v}" : v.ToString(CultureInfo.InvariantCulture).Replace("-", "−");
        var sb = new StringBuilder($"- **GPU overclock:** stock → core {S(g.Core)} MHz, memory {S(g.MemAb)}{(g.PowerPct is int pp ? $", power limit {pp}%" : "")}{(g.TestedCore == g.Core && g.TestedMemAb == g.MemAb ? " (passed a full stress test)" : " (not fully tested)")}.");
        if (State.GpuBench.TryGetValue(profile, out var b) && b is { Stock: { } st, Tuned: { } tu } && tu.Core == g.Core && tu.MemAb == g.MemAb)
        {
            var parts = new List<string>();
            if (st.LoadMhz is > 0 && tu.LoadMhz is > 0) parts.Add($"clock under load {st.LoadMhz:0} → {tu.LoadMhz:0} MHz");
            if (st.Rate is > 0 && tu.Rate is > 0) parts.Add($"compute {(tu.Rate / st.Rate - 1) * 100:+0.0;−0.0}%");
            if (st.GBps is > 0 && tu.GBps is > 0) parts.Add($"VRAM {st.GBps:0} → {tu.GBps:0} GB/s");
            if (st.PowerW is > 0 && tu.PowerW is > 0) parts.Add($"power {st.PowerW:0} → {tu.PowerW:0} W");
            if (st.PeakCoreC is > 0 && tu.PeakCoreC is > 0) parts.Add($"peak {st.PeakCoreC:0} → {tu.PeakCoreC:0} °C");
            if (parts.Count > 0) sb.Append("\n- **Result** (back to back with stock): ").Append(string.Join(", ", parts)).Append('.');
        }
        return sb.ToString();
    }

    public void SavePbo(string profile, PboAdvice advice) { lock (_gate) { State.Pbo[profile] = advice; Save(); } }
    /// <summary>Store a CPU benchmark for a profile: stock (before the search) or tuned (the applied undervolt).</summary>
    public void SaveCpuBench(string profile, CpuBench bench, bool tuned)
    {
        lock (_gate)
        {
            var pair = State.CpuBench.TryGetValue(profile, out var p) ? p : State.CpuBench[profile] = new CpuBenchPair();
            if (tuned) pair.Tuned = bench; else { pair.Stock = bench; pair.Tuned = null; }
            Save();
        }
    }

    public void ClearCpu(string profile) { lock (_gate) { if (State.CpuCo.Remove(profile)) Save(); } }

    /// <summary>Store a finished CPU search's values for a profile (they are checked again when applied).</summary>
    public void SaveCpu(string profile, int[] values)
    {
        if (values.Length is 0 or > 64 || values.Any(v => v is < -60 or > 0)) throw new ArgumentException("Curve Optimizer values must be -60..0 per core");
        lock (_gate) { State.CpuCo[profile] = values.ToArray(); Save(); }
        Say($"Saved CPU undervolt for profile {profile}: {string.Join(" ", values)}");
    }

    /// <summary>
    /// The end of an AI run: if the AI didn't save a GPU result into the run's profile itself (stopped, out of time, Claude Code
    /// quit), save the best setting from this run that passed a full test on this driver: the highest clock under load, then the
    /// lowest offsets within 1% of it. Only proven settings; nothing untested is ever saved here. Returns what it did, or null.
    /// </summary>
    public string? SaveBestFromRun(string profile, DateTime since)
    {
        var gpu = Gpu();
        if (gpu == null) return null;
        GpuTestRecord? pick;
        lock (_gate)
        {
            if (State.Profiles.TryGetValue(profile, out var have) && have.When >= since) return null;   // the AI saved it
            var ok = State.History.Where(h => h.Time >= since && h.Ok && !h.Light && h.Driver == gpu.Driver && (h.Core > 0 || h.MemAb > 0)).ToList();
            if (ok.Count == 0) return null;
            // by work done (compute rate, then VRAM bandwidth), never by clock: at the power limit the clock rises while the work doesn't
            double maxRate = ok.Max(h => h.Rate ?? 0), maxBw = ok.Max(h => h.GBps ?? 0), maxMhz = ok.Max(h => (double)(h.LoadMhz ?? 0));
            double Score(GpuTestRecord h) => maxRate > 0
                ? (h.Rate ?? 0) / maxRate * (maxBw > 0 ? 0.75 : 1) + (maxBw > 0 ? (h.GBps ?? 0) / maxBw * 0.25 : 0)
                : maxMhz > 0 ? (h.LoadMhz ?? 0) / maxMhz : 0;
            double best = ok.Max(Score);
            pick = ok.Where(h => Score(h) >= best * 0.99).OrderBy(h => h.Core + h.MemAb / 4).First();
        }
        SaveResult(pick.Core, pick.MemAb, profile);
        return $"Glint saved the best tested result of this run to {profile}: core {pick.Core:+0;-0} / memory {pick.MemAb:+0;-0}";
    }

    /// <summary>A Glint profile was deleted, or the user cleared its overclock: it runs at stock from now on.</summary>
    public object ClearProfile(string profile)
    {
        bool had; lock (_gate) { had = State.Profiles.Remove(profile); if (State.CpuCo.Remove(profile)) had = true; if (State.Pbo.Remove(profile)) had = true; if (State.CpuBench.Remove(profile)) had = true; if (State.GpuBench.Remove(profile)) had = true; if (State.Notes.Remove(profile)) had = true; if (had) Save(); }
        if (had && profile == ActiveProfile() && AppliedNow != null) Stock();
        return new { cleared = had, profile };
    }

    /// <summary>Lower the saved result after a crash (called by crash watch) and apply it if an overclock is active.</summary>
    public void BackOff(int core, int memAb, string reason)
    {
        lock (_gate)
        {
            var p = ActiveProfile();
            if (!State.Profiles.TryGetValue(p, out var s)) return;
            State.Profiles[p] = s with { Core = Math.Max(0, core), MemAb = Math.Max(0, memAb), When = DateTime.Now };
            Save();
        }
        Say($"Backed off to core {core:+0;-0} / memory {memAb:+0;-0}: {reason}");
        if (_gpu != null && !Busy) ApplyNow(Math.Max(0, core), Math.Max(0, memAb), "crash back-off", State.Profiles.GetValueOrDefault(ActiveProfile())?.PowerPct);
    }

    /// <summary>The user re-enables apply-at-startup after crash watch reverted the overclock.</summary>
    public object ClearRevert()
    {
        lock (_gate) { State.RevertedReason = null; Save(); }
        return new { cleared = true };
    }

    public object Stock(string? reason = null)
    {
        if (Busy) { _stop = true; _job?.Join(15000); }
        var gpu = _gpu;
        var errs = gpu?.ResetAll() ?? Array.Empty<string>();
        AppliedNow = null;
        if (reason != null) lock (_gate) { State.RevertedReason = reason; Save(); }
        Say("GPU back to stock" + (reason != null ? ": " + reason : ""));
        Applied?.Invoke(0, 0);
        return new { stock = true, errors = errs };
    }

    (double? Duty, double? Rpm)? SafeProbe() { try { return FanProbe?.Invoke(); } catch { return null; } }

    object? Live(IGpuControl gpu)
    {
        try
        {
            var s = gpu.Sample();
            return new { coreC = TuningLimits.ValidTemp(s.CoreC), memC = TuningLimits.ValidTemp(_memJunction()), mhz = s.CoreMhz, memMhz = s.MemMhz,
                         powerW = Math.Round(s.PowerW, 1), utilPct = s.UtilPct, fanPct = s.FanPct, throttle = s.Throttle };
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Power limit to the card's own maximum (its firmware ceiling) when the user wants it unlocked. Returns the limit in force.</summary>
    /// <summary>Power limit to <paramref name="pct"/>% of the card's default (never above its maximum); returns the limit in force.</summary>
    bool _powerLowered;

    /// <summary>A copy taken under the lock: Status is serialized after it returns, while the job thread may be changing the state.</summary>
    T2 Snap<T2>(Func<T2> f) { lock (_gate) return f(); }

    double? LimitPower(IGpuControl gpu, int pct)
    {
        var (def, max) = gpu.PowerLimitW;
        if (def > 0)
            try { gpu.SetPowerLimitW(Math.Min(max > 0 ? max : def, Math.Round(def * Math.Clamp(pct, MinPowerPct, 100) / 100.0))); }
            catch (Exception e) { Say($"power limit {pct}% not set: " + e.Message); }
        return gpu.PowerLimitNowW;
    }

    double? UnlockPower(IGpuControl gpu)
    {
        var (def, max) = gpu.PowerLimitW;
        if (T.UnlockPower && max > def && max > 0)
            try { gpu.SetPowerLimitW(max); }
            catch (Exception e) { Say("power limit not raised: " + e.Message); }
        return gpu.PowerLimitNowW;
    }

    /// <summary>Stock vs tuned on this driver, for the dashboard's comparison: the newest passing full test at stock, and the
    /// passing full test behind the saved result (or the best-performing passing test).</summary>
    object? Compare(string? driver)
    {
        if (driver == null) return null;
        var ok = State.History.Where(h => h.Ok && !h.Light && h.Driver == driver).ToList();
        var stock = ok.LastOrDefault(h => h.Core == 0 && h.MemAb == 0);
        var s = ActiveSaved;
        var tuned = s != null && s.Driver == driver
            ? ok.Where(h => h.Core >= s.Core && h.MemAb >= s.MemAb).OrderBy(h => h.Core + h.MemAb).FirstOrDefault()
            : ok.Where(h => h.Core > 0 || h.MemAb > 0).OrderByDescending(h => h.Rate ?? h.LoadMhz ?? 0).FirstOrDefault();
        return stock == null && tuned == null ? null : new { stock, tuned, savedCore = s?.Core, savedMemAb = s?.MemAb };
    }

    /// <summary>Measured numbers per profile: the stock baseline once and, for each profile's saved overclock, the passing full test
    /// that proves it (the exact setting if it was tested, else the closest one above it) on the current driver.</summary>
    object? ProfileStats(string? driver)
    {
        if (driver == null) return null;
        lock (_gate)
        {
            var ok = State.History.Where(h => h.Ok && !h.Light && h.Driver == driver).ToList();
            // every run at the same offsets averaged (single runs swing several % on a power-limited card)
            static double? Avg(IEnumerable<double?> v) { var l = v.Where(x => x is double d && double.IsFinite(d)).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : Math.Round(l.Average(), 1); }
            object? Pick(List<GpuTestRecord> runs) => runs.Count == 0 ? null : new
            {
                runs[0].Core, runs[0].MemAb, LoadMhz = Avg(runs.Select(h => (double?)h.LoadMhz)), Rate = Avg(runs.Select(h => h.Rate)), GBps = Avg(runs.Select(h => h.GBps)),
                PowerW = Avg(runs.Select(h => h.PowerW)), PeakCoreC = Avg(runs.Select(h => h.PeakCoreC)), AvgFanPct = Avg(runs.Select(h => h.AvgFanPct)),
                ThermalLimited = runs.Any(h => h.ThermalLimited), PowerLimited = runs.Any(h => h.PowerLimited), runs[^1].Time, Runs = runs.Count,
            };
            List<GpuTestRecord> At(int core, int mem) => ok.Where(h => h.Core == core && h.MemAb == mem).ToList();
            var per = new Dictionary<string, object?>();
            foreach (var (name, s) in State.Profiles)
            {
                if (s.Driver != driver) { per[name] = null; continue; }
                var exact = At(s.Core, s.MemAb);
                if (exact.Count == 0 && ok.Where(h => h.Core >= s.Core && h.MemAb >= s.MemAb).OrderBy(h => h.Core + h.MemAb).FirstOrDefault() is { } near) exact = At(near.Core, near.MemAb);
                per[name] = Pick(exact);
            }
            return new { stock = Pick(At(0, 0)), profiles = per };
        }
    }

    /// <summary>A page of the test history, newest first (the dashboard paginates through all of it).</summary>
    public object History(int offset, int limit)
    {
        limit = Math.Clamp(limit, 1, 100); offset = Math.Max(0, offset);
        lock (_gate)
        {
            var all = Enumerable.Reverse(State.History).ToList();
            return new { total = all.Count, offset, limit, items = all.Skip(offset).Take(limit).ToList() };
        }
    }

    bool Proven(int core, int memAb, string driver) =>
        core >= 0 && memAb >= 0 && State.History.Any(h => h.Ok && !h.Light && h.Driver == driver && h.Core >= core && h.MemAb >= memAb);

    string? Blocked(IGpuControl gpu)
    {
        if (!T.Enabled) return "tuning is off (Settings → Tuning)";
        if (gpu.IsWorkstation && !T.AllowWorkstation) return "workstation GPU: turn on 'Allow tuning a workstation GPU' first (they're validated at stock clocks)";
        if (gpu.Range(ClockKind.Core) is { Max: <= 0 } && gpu.Range(ClockKind.Memory) is { Max: <= 0 }) return "the driver doesn't allow clock offsets on this card";
        return null;
    }

    IGpuControl Ready(bool allowWhileBusy = false)
    {
        if (!allowWhileBusy && Busy) throw new InvalidOperationException("busy: " + Phase);
        var gpu = Gpu() ?? throw new InvalidOperationException(OpenError ?? "no NVIDIA GPU found");
        if (Blocked(gpu) is { } why) throw new InvalidOperationException(why);
        var clash = _processes().Where(p => Conflicting.Any(c => p.Equals(c, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
        if (clash.Count > 0) throw new InvalidOperationException("close " + string.Join(", ", clash) + " first: it re-applies its own clocks and would fight these tests");
        return gpu;
    }

    IGpuControl? Gpu()
    {
        lock (_gate)
        {
            if (_gpu != null) return _gpu;
            try { _gpu = _openGpu(); OpenError = _gpu == null ? "no NVIDIA GPU found" : null; }
            catch (Exception e) { OpenError = "NVIDIA driver not usable: " + e.Message; }
            return _gpu;
        }
    }

    /// <summary>The stress test holds a CUDA context and most of the VRAM: never keep it between jobs (a game needs that memory).
    /// The next test records its stock reference again, which costs a few seconds.</summary>
    void ReleaseStress()
    {
        var s = _stress;
        _stress = null; _golden = false;
        try { s?.Dispose(); } catch { }
    }

    IGpuStress Stress(IGpuControl gpu)
    {
        _stress ??= _openStress(gpu);
        if (!_golden)
        {
            Say("Recording reference results at stock clocks");
            foreach (var e in gpu.ResetAll()) Say("reset: " + e);
            _stress.Calibrate();
            _stress.Golden();
            _golden = true;
        }
        return _stress;
    }

    /// <summary>NVML memory offset → reported clock: set +500 NVML for a few seconds under VRAM load and read the clock.</summary>
    double Ratio(IGpuControl gpu, IGpuStress stress)
    {
        if (State.MemRatio is double r && State.RatioFor == gpu.Name + "|" + gpu.Vbios) return r;
        int Clock(int off)
        {
            gpu.SetOffset(ClockKind.Memory, off);
            int mhz = 0;
            stress.RunMemory(4, () => { if (_stop) throw new TuneAbort("stopped"); mhz = Math.Max(mhz, gpu.Sample().MemMhz); });
            return mhz;
        }
        Say("Measuring how the memory offset maps to the memory clock");
        int c0 = Clock(0), c1 = Clock(500);
        gpu.SetOffset(ClockKind.Memory, 0);
        double ratio = c1 > c0 ? Math.Round((c1 - c0) / 500.0, 3) : 0.5;
        lock (_gate) { State.MemRatio = ratio; State.RatioFor = gpu.Name + "|" + gpu.Vbios; Save(); }
        return ratio;
    }

    int ToAb(int nvml) => (int)Math.Round(nvml * (State.MemRatio ?? 0.5));

    void Run(string name, Func<object> work)
    {
        StartJob(name, work);
        try { JobStarted?.Invoke(name); } catch { }
    }

    void StartJob(string name, Func<object> work)
    {
        lock (_gate)
        {
            if (Busy) throw new InvalidOperationException("busy: " + Phase);
            _stop = false;
            Phase = name; LastResult = null;
            _job = new Thread(() =>
            {
                try { LastResult = work(); }
                catch (Exception e) { LastResult = new { ok = false, error = e.Message }; Say("Failed: " + e.Message); }
                finally { ReleaseStress(); Phase = "idle"; try { JobEnded?.Invoke(name); } catch { } }
            }) { IsBackground = true, Name = "Glint tuning" };
            _job.Start();
        }
    }

    string Say(string msg)
    {
        Progress = msg;
        Log?.Invoke(msg);
        return msg;
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var tmp = _statePath + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(f, State, GlintConfig.Json);
            f.Flush(true);      // through to disk: the point of this file is surviving a freeze
        }
        File.Move(tmp, _statePath, true);
    }

    static TuningState LoadState(string path)
    {
        try { return JsonSerializer.Deserialize<TuningState>(File.ReadAllText(path), GlintConfig.Json) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Was the last shutdown unexpected (Kernel-Power 41, a bugcheck, or the "previous shutdown was unexpected"
    /// record) since this boot? Then a saved overclock must not be re-applied at startup.</summary>
    public static bool LastShutdownWasCrash()
    {
        var upMs = Environment.TickCount64 + 120000;
        var q = "*[System[((Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or " +
                "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or " +
                $"(Provider[@Name='EventLog'] and EventID=6008)) and TimeCreated[timediff(@SystemTime) <= {upMs}]]]";
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, q));
            using var e = reader.ReadEvent();
            return e != null;
        }
        catch (EventLogException) { return false; }
    }

    /// <summary>NVIDIA driver resets (TDR / Xid) logged since <paramref name="since"/>: any of these during a test = unstable.</summary>
    public static IReadOnlyList<string> DriverResetsSince(DateTime since)
    {
        var ms = Math.Max(1000, (long)(DateTime.Now - since).TotalMilliseconds + 5000);
        var q = $"*[System[(Provider[@Name='nvlddmkm'] or (Provider[@Name='Display'] and EventID=4101)) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        var found = new List<string>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, q) { ReverseDirection = true });
            for (EventRecord? e; found.Count < 5 && (e = reader.ReadEvent()) != null;)
                using (e) found.Add($"event {e.Id} {e.ProviderName}");
        }
        catch (EventLogException) { }
        return found;
    }

    public void Dispose()
    {
        _stop = true;
        _job?.Join(15000);
        try { _stress?.Dispose(); } catch { }
        try { _gpu?.Dispose(); } catch { }
    }
}
