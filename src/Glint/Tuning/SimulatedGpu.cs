namespace Glint.Tuning;

/// <summary>How a <see cref="SimulatedGpu"/> behaves. Offsets are NVML units. Null thresholds mean "never".</summary>
public sealed record SimConfig
{
    public string Name { get; init; } = "NVIDIA GeForce RTX 5090";
    public OffsetRange? CoreRange { get; init; } = new(-1000, 1000);
    public OffsetRange? MemRange { get; init; } = new(-2000, 6000);
    /// <summary>Core offset above this fails the compute test; above FaultCore the "driver" faults (like a TDR).</summary>
    public int? UnstableCore { get; init; }
    public int? FaultCore { get; init; }
    /// <summary>Memory offset above this fails the VRAM test. EccAboveMem makes the ECC-corrected counter grow instead (silent errors).</summary>
    public int? UnstableMem { get; init; }
    public int? EccAboveMem { get; init; }
    public bool EccSupported { get; init; }
    public double AmbientC { get; init; } = 35;
    public double? SlowdownC { get; init; } = 88;
    /// <summary>Power-limited card: compute stops scaling above this core offset and every memory offset costs compute.</summary>
    public int? PowerCapCore { get; init; }
}

/// <summary>
/// A deterministic fake card for every offline test: ranges, "unstable above" thresholds, temperatures that rise with
/// load and the offset, optional ECC errors and an optional driver fault. No randomness, no clocks, no hardware.
/// </summary>
public sealed class SimulatedGpu(SimConfig? config = null) : IGpuControl
{
    readonly object _l = new();
    readonly Dictionary<ClockKind, int> _off = new() { [ClockKind.Core] = 0, [ClockKind.Memory] = 0 };
    long _ecc;
    (int Min, int Max)? _lock;

    public SimConfig Config { get; } = config ?? new SimConfig();
    /// <summary>0..1 load the stress is applying; temperature and power follow it.</summary>
    public double Load { get; set; }
    public int SetCalls { get; private set; }
    public int ResetCalls { get; private set; }
    public bool Disposed { get; private set; }
    public (int Min, int Max)? Locked { get { lock (_l) return _lock; } }

    public string Name => Config.Name;
    public string Driver => "999.99";
    public string Vbios => "SIM.00.00";
    public string? SubsystemVendor => "1043";
    public double VramGiB => 32;
    public (double Default, double Max) PowerLimitW => (575, 600);
    double _power = 575;
    public double? PowerLimitNowW { get { lock (_l) return _power; } }
    public void SetPowerLimitW(double watts)
    {
        if (!(watts >= 300 && watts <= PowerLimitW.Max)) throw new ArgumentOutOfRangeException(nameof(watts), $"power limit {watts:0} W is outside 300..600 W");
        lock (_l) _power = watts;
    }
    public double? SlowdownC => Config.SlowdownC;
    public bool IsWorkstation => Nvml.IsWorkstationName(Name);

    public OffsetRange? Range(ClockKind kind) => kind == ClockKind.Core ? Config.CoreRange : Config.MemRange;
    public int GetOffset(ClockKind kind) { lock (_l) return _off[kind]; }

    public void SetOffset(ClockKind kind, int mhz)
    {
        Nvml.CheckOffset(kind, mhz);   // the same hard caps as the real card
        var r = Range(kind);
        if (r != null && (mhz < r.Min || mhz > r.Max)) throw new NvmlException("nvmlDeviceSetClockOffsets", 2);
        lock (_l) { _off[kind] = mhz; SetCalls++; }
    }

    public void LockCoreClocks(int minMhz, int maxMhz) { lock (_l) _lock = (minMhz, maxMhz); }
    public void UnlockCoreClocks() { lock (_l) _lock = null; }

    public IReadOnlyList<string> ResetAll()
    {
        lock (_l) { _off[ClockKind.Core] = 0; _off[ClockKind.Memory] = 0; _lock = null; _power = PowerLimitW.Default; ResetCalls++; }
        return [];
    }

    public GpuSample Sample()
    {
        lock (_l)
        {
            double core = Config.AmbientC + 40 * Load + _off[ClockKind.Core] * 0.02;
            int mhz = 2400 + _off[ClockKind.Core] - (_lock is { } k && 2400 > k.Max ? 2400 - k.Max : 0);
            double draw = 60 + 500 * Load; if (_power is double lim && draw > lim) draw = lim;   // the power limit caps the draw
            return new GpuSample(core, null, draw, mhz, 14000 + _off[ClockKind.Memory], (int)(100 * Load), (int)(30 + 60 * Load), Load > 0.95 ? ["power cap"] : []);
        }
    }

    public long? EccCorrected() { lock (_l) return Config.EccSupported ? _ecc : null; }

    /// <summary>Test hook for the simulated stress: ECC errors the card "corrected" during a memory run.</summary>
    internal void AddEcc(long n) { lock (_l) _ecc += n; }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// Simulated stress against a <see cref="SimulatedGpu"/>: compute fails above its core threshold, the VRAM test above its
/// memory threshold, ECC-corrected errors grow above EccAboveMem, and the driver can fault. Calls <paramref name="tick"/>
/// a few times per run (never sleeps) so TuneAbort propagation can be tested.
/// </summary>
public sealed class SimulatedStress(SimulatedGpu gpu) : IGpuStress
{
    bool _calibrated, _golden;
    /// <summary>Set when the simulated driver reset: every run faults until <see cref="Reopen"/>.</summary>
    public bool Dead { get; set; }
    public int ReopenCalls { get; private set; }
    public double MemoryGiB => 24;

    public void Calibrate() => _calibrated = true;
    public void Golden()
    {
        if (gpu.GetOffset(ClockKind.Core) != 0 || gpu.GetOffset(ClockKind.Memory) != 0) throw new InvalidOperationException("golden results must be taken at stock clocks");
        _golden = true;
    }
    public void Reopen() { ReopenCalls++; Dead = false; }

    static void Ticks(double seconds, Action tick) { for (int i = 0; i < Math.Clamp((int)(seconds * 10), 1, 20); i++) tick(); }

    public StressResult RunCore(double seconds, bool light, Action tick)
    {
        if (!_calibrated || !_golden) throw new InvalidOperationException("Calibrate() and Golden() first");
        int off = gpu.GetOffset(ClockKind.Core);
        gpu.Load = light ? 0.5 : 1;
        try
        {
            Ticks(seconds, tick);
            if (Dead || gpu.Config.FaultCore is { } f && off > f) { Dead = true; return new(false, 3, 0, 0, null, "simulated driver fault (CUDA error 700)", true); }
            long launches = Math.Max(1, (long)(seconds * 8));
            bool bad = gpu.Config.UnstableCore is { } u && off > u + (light ? 30 : 0);  // the light load survives slightly more
            long errors = bad ? launches / 2 + 1 : 0;
            double eff = gpu.Config.PowerCapCore is int cap ? Math.Min(off, cap) - gpu.GetOffset(ClockKind.Memory) / 100.0 : off;
            double pf = gpu.PowerLimitNowW is double lim && lim < 560 ? Math.Pow(lim / 560, 0.4) : 1;   // less power: slower, but less than in proportion
            return new(errors == 0, launches, errors, 1e12 * (1 + eff / 3000.0) * pf, null, bad ? $"{errors} of {launches} compute results differ from stock" : null, false);
        }
        finally { gpu.Load = 0; }
    }

    public StressResult RunMemory(double seconds, Action tick)
    {
        if (!_calibrated || !_golden) throw new InvalidOperationException("Calibrate() and Golden() first");
        int off = gpu.GetOffset(ClockKind.Memory);
        gpu.Load = 0.7;
        try
        {
            Ticks(seconds, tick);
            if (Dead) return new(false, 0, 0, 0, null, "simulated driver fault (CUDA error 700)", true);
            if (gpu.Config.EccAboveMem is { } e && off > e) gpu.AddEcc(1 + (off - e) / 50);
            long passes = Math.Max(1, (long)(seconds / 2));
            bool bad = gpu.Config.UnstableMem is { } u && off > u;
            long errors = bad ? 5 : 0;
            // GDDR7 retries on errors, so bandwidth falls once the clock is past stable
            double gbps = 1500.0 * (1 + off / 28000.0) * (bad ? 0.7 : 1);
            return new(errors == 0, passes, errors, passes / Math.Max(seconds, 0.001), gbps, bad ? $"{errors} VRAM mismatches" : null, false);
        }
        finally { gpu.Load = 0; }
    }

    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}
