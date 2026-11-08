namespace Glint.Tuning;

/// <summary>
/// Hard limits nothing may cross, whatever asked for it: the dashboard, the CLI/MCP, saved state, a crash back-off.
/// The search ceilings for a card come from the driver's own offset range, clamped to these.
/// No voltage control exists in Glint at all. The power limit can be raised only up to the card's own maximum (its
/// firmware ceiling); offsets alone can crash a game but not damage a card, because the firmware still enforces it.
/// </summary>
public static class TuningLimits
{
    public const int MaxCoreOffset = 500;            // MHz
    public const int MaxMemOffsetAfterburner = 3000; // MSI Afterburner units (NVML = AB / ratio, ratio ~0.5 on GDDR6/7)
    public const int MaxMemOffsetNvml = 6000;
    /// <summary>Emergency stops, not test results: hitting the card's own thermal limit just means it throttles (the
    /// tuner then looks for the best sustained performance under it). Only a temperature well past normal throttling stops a test.</summary>
    public const double DefaultEmergencyCoreC = 90, DefaultEmergencyMemC = 100;
    public const double MaxEmergencyCoreC = 95, MaxEmergencyMemC = 105, MinEmergencyC = 75;

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

    /// <summary>Core emergency stop: the user's setting, never more than 3 °C past the card's own slowdown point, never past the cap.</summary>
    public static double EmergencyCore(double configured, double? slowdownC)
    {
        double c = double.IsFinite(configured) ? Math.Clamp(configured, MinEmergencyC, MaxEmergencyCoreC) : DefaultEmergencyCoreC;
        return slowdownC is double s ? Math.Min(c, s + 3) : c;
    }
    public static double EmergencyMem(double configured) => double.IsFinite(configured) ? Math.Clamp(configured, MinEmergencyC + 10, MaxEmergencyMemC) : DefaultEmergencyMemC;

    /// <summary>A temperature the safety logic may act on. 0 = "not read" (no admin), 255 = NVAPI's "no data":
    /// both count as unreadable, never as cool or as hot.</summary>
    public static double? ValidTemp(double? t) => t is > 0 and < 150 ? t : null;
}
