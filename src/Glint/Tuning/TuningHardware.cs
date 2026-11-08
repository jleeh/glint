namespace Glint.Tuning;

/// <summary>Where the real hardware layer comes from. The service never constructs NVML / CUDA itself, so tests (and
/// machines without an NVIDIA driver) never load them; nothing here runs until tuning is enabled and something asks.</summary>
public static class TuningHardware
{
    public static Func<IGpuControl?> OpenGpu = () => Nvml.TryOpen(out var e) ?? (e != null ? throw new InvalidOperationException(e) : null);
    public static Func<IGpuControl, IGpuStress> OpenStress = _ => new CudaStress();
}
