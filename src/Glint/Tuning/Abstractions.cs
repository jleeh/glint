namespace Glint.Tuning;

/// <summary>Which clock an offset applies to.</summary>
public enum ClockKind { Core, Memory }

/// <summary>One sample of the GPU while a test runs (from NVML; memory junction from LibreHardwareMonitor).</summary>
public sealed record GpuSample(double? CoreC, double? MemJunctionC, double PowerW, int CoreMhz, int MemMhz, int UtilPct, int FanPct, IReadOnlyList<string> Throttle);

/// <summary>Driver-reported offset range for one clock (NVML units: memory is double-data-rate, i.e. 2x Afterburner).</summary>
public sealed record OffsetRange(int Min, int Max);

/// <summary>
/// The GPU controls the tuner needs. The real implementation is <see cref="Nvml"/> (NVIDIA's management library,
/// shipped with the driver); tests use a simulated GPU so no test ever touches hardware.
/// Every setter must enforce <see cref="TuningLimits"/> itself, whoever calls it.
/// </summary>
public interface IGpuControl : IDisposable
{
    string Name { get; }
    string Driver { get; }
    string Vbios { get; }
    /// <summary>PCI subsystem vendor (board partner), e.g. "1043" for ASUS.</summary>
    string? SubsystemVendor { get; }
    double VramGiB { get; }
    /// <summary>Default / max power limit in watts (read only: the tuner never changes power limits).</summary>
    (double Default, double Max) PowerLimitW { get; }
    /// <summary>Range the driver accepts for this card, or null when it doesn't report one.</summary>
    OffsetRange? Range(ClockKind kind);
    int GetOffset(ClockKind kind);
    void SetOffset(ClockKind kind, int mhz);
    /// <summary>Lock the core clock range (efficiency mode) / undo it.</summary>
    void LockCoreClocks(int minMhz, int maxMhz);
    void UnlockCoreClocks();
    /// <summary>Offsets 0, clock locks removed. Returns the errors it hit (it always tries everything).</summary>
    IReadOnlyList<string> ResetAll();
    GpuSample Sample();
    /// <summary>Total corrected (volatile) ECC errors in VRAM, or null when ECC isn't available or is off.</summary>
    long? EccCorrected();
    /// <summary>Workstation / data-centre card (RTX PRO, RTX A, Quadro, Tesla...): tuning only after the user opts in.</summary>
    bool IsWorkstation { get; }
}

/// <summary>Result of one stress run.</summary>
public sealed record StressResult(bool Ok, long Launches, long Errors, double Rate, double? GBps, string? Why, bool Fault);

/// <summary>
/// Compute and VRAM stress with bit-for-bit verification against results recorded at stock clocks
/// (<see cref="CudaStress"/> on real hardware, a simulation in tests).
/// <paramref name="tick"/> is called about every 100 ms and may throw <see cref="TuneAbort"/> (temperature limit, stop).
/// </summary>
public interface IGpuStress : IDisposable
{
    void Calibrate();
    /// <summary>Record the reference results at stock clocks.</summary>
    void Golden();
    StressResult RunCore(double seconds, bool light, Action tick);
    StressResult RunMemory(double seconds, Action tick);
    /// <summary>After a driver reset: reopen the CUDA context, keep the golden results.</summary>
    void Reopen();
    double MemoryGiB { get; }
}

/// <summary>Thrown inside a test to stop it now (temperature limit, user pressed Stop, fan failure).</summary>
public sealed class TuneAbort(string reason) : Exception(reason);
