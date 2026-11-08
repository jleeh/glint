namespace Glint.Tuning;

/// <summary>
/// Hard limits nothing may cross, whatever asked for it: the dashboard, the CLI/MCP, saved state, a crash back-off.
/// The search ceilings for a card come from the driver's own offset range, clamped to these.
/// No voltage or power-limit control exists in Glint at all: offsets alone can crash a game but not damage a card,
/// because the card's firmware still enforces its power and voltage limits.
/// </summary>
public static class TuningLimits
{
    public const int MaxCoreOffset = 500;            // MHz
    public const int MaxMemOffsetAfterburner = 3000; // MSI Afterburner units (NVML = AB / ratio, ratio ~0.5 on GDDR6/7)
    public const int MaxMemOffsetNvml = 6000;
    public const double DefaultAbortCoreC = 83, DefaultAbortMemC = 96;
    public const double MaxAbortCoreC = 85, MaxAbortMemC = 100, MinAbortC = 70;

    public static int ClampCore(int mhz) => Math.Clamp(mhz, 0, MaxCoreOffset);

    /// <summary>NVML memory offset limited to the Afterburner cap converted with the measured ratio.</summary>
    public static int ClampMemNvml(int nvml, double ratio)
    {
        if (!(ratio > 0)) ratio = 0.5;
        return Math.Clamp(nvml, 0, Math.Min(MaxMemOffsetNvml, (int)(MaxMemOffsetAfterburner / ratio)));
    }

    /// <summary>Search ceiling: the driver's range when it reports one, never past the hard caps.</summary>
    public static (int Core, int MemAfterburner) Ceilings(OffsetRange? core, OffsetRange? mem)
    {
        int c = core is { Max: > 0 } r ? Math.Min(r.Max, MaxCoreOffset) : 150;
        int m = mem is { Max: > 0 } rm ? Math.Min(rm.Max / 2, MaxMemOffsetAfterburner) : 500;
        return (c, m);
    }

    public static double ClampAbortCore(double c) => double.IsFinite(c) ? Math.Clamp(c, MinAbortC, MaxAbortCoreC) : DefaultAbortCoreC;
    public static double ClampAbortMem(double c) => double.IsFinite(c) ? Math.Clamp(c, MinAbortC + 10, MaxAbortMemC) : DefaultAbortMemC;

    /// <summary>A temperature the safety logic may act on. 0 = "not read" (no admin), 255 = NVAPI's "no data":
    /// both count as unreadable, never as cool or as hot.</summary>
    public static double? ValidTemp(double? t) => t is > 0 and < 150 ? t : null;
}
