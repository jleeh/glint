using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using Glint.Core;

namespace Glint.Tuning;

/// <summary>A GPU stress test at one setting, as kept in the history the AI (and the dashboard) reads.</summary>
/// <remarks>Rate = compute throughput (G operations/s, comparable between tests on one card). ThermalLimited / PowerLimited:
/// the card throttled on temperature or its power limit during the test: a performance limit, not instability.</remarks>
public sealed record GpuTestRecord(DateTime Time, int Core, int MemAb, double Seconds, bool Light, bool Ok, string? Why,
    int? LoadMhz, double? PeakCoreC, double? PeakMemC, double? PowerW, double? GBps, string Driver,
    double? Rate = null, bool ThermalLimited = false, bool PowerLimited = false, double? PowerLimitW = null);

/// <summary>Everything Glint must remember across restarts and crashes (tuning-state.json next to config.json).</summary>
public sealed class TuningState
{
    /// <summary>The setting being tested, written to disk BEFORE it is applied: if the PC dies, the next start knows what killed it.</summary>
    public GpuTestRecord? Testing { get; set; }
    /// <summary>Offsets the user (or the AI) saved as the result: what "apply at startup" and crash back-off work on.</summary>
    public GpuSaved? Saved { get; set; }
    /// <summary>NVML memory offset → reported clock delta (Afterburner units = NVML x ratio). Measured once per card.</summary>
    public double? MemRatio { get; set; }
    public string? RatioFor { get; set; }
    public List<GpuTestRecord> History { get; set; } = new();
    /// <summary>Set when crash watch put the GPU back to stock after repeated crashes; blocks apply-at-startup until cleared.</summary>
    public string? RevertedReason { get; set; }
}

public sealed record GpuSaved(int Core, int MemAb, DateTime When, string Driver, string Vbios, int TestedCore, int TestedMemAb);

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
    }

    bool Busy => _job is { IsAlive: true };
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
        var c = T;
        if (!c.Enabled || !c.ApplyAtStartup || State.Saved is not { } s) return note;
        if (lastShutdownWasCrash) return Say("Saved overclock not applied: the last shutdown was a crash.");
        if (State.RevertedReason != null) return Say("Saved overclock not applied: " + State.RevertedReason);
        var gpu = Gpu();
        if (gpu == null) return note;
        if (gpu.Driver != s.Driver || gpu.Vbios != s.Vbios)
            return Say($"Saved overclock not applied: the GPU driver/VBIOS changed ({s.Driver} → {gpu.Driver}). Re-test first.");
        ApplyNow(s.Core, s.MemAb, "startup");
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
            memRatio = State.MemRatio,
            // the test in progress (for the monitor window's progress bar) and a live sample while busy
            testing = State.Testing == null ? null : new { core = State.Testing.Core, memAb = State.Testing.MemAb, seconds = State.Testing.Seconds,
                                                           light = State.Testing.Light, startedAt = State.Testing.Time },
            live = Busy && gpu != null ? Live(gpu) : null,
            saved = State.Saved, reverted = State.RevertedReason,
            history = State.History.TakeLast(30).Reverse(),
            last = LastResult,
        };
    }

    // ------------------------------------------------------------------ GPU tests

    /// <summary>Start a stress test at these offsets (memory in MSI Afterburner units). Returns at once; poll with <see cref="Wait"/>.
    /// The card goes back to stock when the test ends, pass or fail.</summary>
    public object StartGpuTest(int core, int memAb, double seconds, bool light)
    {
        var gpu = Ready();
        var (ceilCore, ceilMem) = TuningLimits.Ceilings(gpu.Range(ClockKind.Core), gpu.Range(ClockKind.Memory));
        if (core < 0 || core > ceilCore) throw new InvalidOperationException($"core must be 0..{ceilCore} MHz on this card");
        if (memAb < 0 || memAb > ceilMem) throw new InvalidOperationException($"memAb must be 0..{ceilMem} (Afterburner units) on this card");
        seconds = Math.Clamp(seconds, 10, MaxTestSeconds);
        Run("gpu test", () => GpuTest(gpu, core, memAb, seconds, light));
        return new { started = true, core, memAb, seconds, light, note = "poll tuning.wait until busy=false" };
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

    GpuTestRecord GpuTest(IGpuControl gpu, int core, int memAb, double seconds, bool light)
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
        void Tick()
        {
            if (_stop) throw new TuneAbort("stopped");
            var s = gpu.Sample();
            var tc = TuningLimits.ValidTemp(s.CoreC);
            var tm = TuningLimits.ValidTemp(_memJunction());
            if (tc is double a) { peakCore = Math.Max(peakCore, a); if (a >= hotAt) thermal = true; if (a >= abortCore) throw new TuneAbort($"emergency stop: GPU core {a:0} °C reached {abortCore:0} °C"); }
            if (tm is double m) { peakMem = Math.Max(peakMem, m); if (m >= abortMem) throw new TuneAbort($"emergency stop: memory junction {m:0} °C reached {abortMem:0} °C"); }
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
            _fanBoost(85);
            powerLimitUsed = UnlockPower(gpu);
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
                         PowerLimitW = powerLimitUsed };
        lock (_gate)
        {
            State.Testing = null;
            State.History.Add(rec);
            if (State.History.Count > MaxHistory) State.History.RemoveRange(0, State.History.Count - MaxHistory);
            Save();
        }
        var limits = string.Join(", ", new[] { rec.ThermalLimited ? "thermal-limited" : null, rec.PowerLimited ? "power-limited" : null }.Where(x => x != null));
        Say(rec.Ok ? $"PASS core {core:+0;-0} / memory {memAb:+0;-0}: {rec.LoadMhz} MHz under load, {rec.PeakCoreC:0} °C{(limits != "" ? " (" + limits + ")" : "")}" : $"FAIL core {core:+0;-0} / memory {memAb:+0;-0}: {why}");
        return rec;
    }

    // ------------------------------------------------------------------ apply / save / stock

    /// <summary>Apply offsets now (until reboot or stock). Never above what a passing test proved on this driver.</summary>
    public object Apply(int core, int memAb)
    {
        var gpu = Ready();
        if (!Proven(core, memAb, gpu.Driver)) throw new InvalidOperationException($"core {core:+0;-0} / memory {memAb:+0;-0} hasn't passed a full test on this driver: test it first");
        return ApplyNow(core, memAb, "user");
    }

    object ApplyNow(int core, int memAb, string by)
    {
        var gpu = Ready(allowWhileBusy: false);
        double ratio = State.MemRatio ?? 0.5;
        int c = TuningLimits.ClampCore(core), m = TuningLimits.ClampMemNvml((int)Math.Round(memAb / ratio), ratio);
        if (c != 0 || m != 0) UnlockPower(gpu);
        gpu.SetOffset(ClockKind.Core, c);
        gpu.SetOffset(ClockKind.Memory, m);
        AppliedNow = c == 0 && m == 0 ? null : (c, m, ratio);
        Say($"Applied core {core:+0;-0} / memory {memAb:+0;-0} ({by})");
        Applied?.Invoke(core, memAb);
        return new { applied = true, core, memAb };
    }

    /// <summary>Raised when offsets are applied (crash watch starts watching) or the card goes back to stock (core=memAb=0).</summary>
    public event Action<int, int>? Applied;

    /// <summary>Save offsets as the result (what startup and crash back-off use). Must be proven by a passing full test.</summary>
    public object SaveResult(int core, int memAb)
    {
        var gpu = Ready();
        if (!Proven(core, memAb, gpu.Driver)) throw new InvalidOperationException("only a setting at or below one that passed a full test on this driver can be saved");
        var best = State.History.Where(h => h.Ok && !h.Light && h.Driver == gpu.Driver && h.Core >= core && h.MemAb >= memAb).OrderBy(h => h.Core + h.MemAb).First();
        lock (_gate)
        {
            State.Saved = new GpuSaved(core, memAb, DateTime.Now, gpu.Driver, gpu.Vbios, best.Core, best.MemAb);
            State.RevertedReason = null;
            Save();
        }
        Say($"Saved result: core {core:+0;-0} / memory {memAb:+0;-0}");
        return new { saved = State.Saved };
    }

    /// <summary>Lower the saved result after a crash (called by crash watch) and apply it if an overclock is active.</summary>
    public void BackOff(int core, int memAb, string reason)
    {
        lock (_gate)
        {
            if (State.Saved is not { } s) return;
            State.Saved = s with { Core = Math.Max(0, core), MemAb = Math.Max(0, memAb), When = DateTime.Now };
            Save();
        }
        Say($"Backed off to core {core:+0;-0} / memory {memAb:+0;-0}: {reason}");
        if (_gpu != null && !Busy) ApplyNow(Math.Max(0, core), Math.Max(0, memAb), "crash back-off");
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
        var s = State.Saved;
        var tuned = s != null && s.Driver == driver
            ? ok.Where(h => h.Core >= s.Core && h.MemAb >= s.MemAb).OrderBy(h => h.Core + h.MemAb).FirstOrDefault()
            : ok.Where(h => h.Core > 0 || h.MemAb > 0).OrderByDescending(h => h.Rate ?? h.LoadMhz ?? 0).FirstOrDefault();
        return stock == null && tuned == null ? null : new { stock, tuned, savedCore = s?.Core, savedMemAb = s?.MemAb };
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
        lock (_gate)
        {
            if (Busy) throw new InvalidOperationException("busy: " + Phase);
            _stop = false;
            Phase = name; LastResult = null;
            _job = new Thread(() =>
            {
                try { LastResult = work(); }
                catch (Exception e) { LastResult = new { ok = false, error = e.Message }; Say("Failed: " + e.Message); }
                finally { ReleaseStress(); Phase = "idle"; }
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
