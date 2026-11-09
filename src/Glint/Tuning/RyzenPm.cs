using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace Glint.Tuning;

/// <summary>The CPU's power limits in effect (from its SMU power table) and how close it is to each right now.</summary>
public sealed record PmLimits(uint Version, double? PptLimitW, double? PptW, double? TdcLimitA, double? TdcA, double? EdcLimitA, double? EdcA,
    double? ThmLimitC, double? ThmC, bool Valid, string? Why, float[]? Head)
{
    static double? Pct(double? v, double? l) => v is double a && l is double b && b > 0 ? Math.Round(a / b * 100, 1) : null;
    public double? PptPct => Pct(PptW, PptLimitW);
    public double? TdcPct => Pct(TdcA, TdcLimitA);
    public double? EdcPct => Pct(EdcA, EdcLimitA);
    /// <summary>PPT far above any Ryzen's stock limit (230 W at most): the BIOS has PBO limits raised ("Motherboard" / manual).</summary>
    public bool Raised => PptLimitW is > 300;
}

/// <summary>
/// Reads the Ryzen SMU power table through the reader LibreHardwareMonitor already holds for the CPU (PawnIO, admin; read-only: the
/// SMU is only asked to refresh its table). LHM only sizes the table for versions it knows, so for Zen 4 / Zen 5 the size is filled
/// in from the code name. The limit fields (PPT, TDC, thermal, EDC at the start of the table) are only trusted after sanity checks.
/// </summary>
public static class RyzenPm
{
    static readonly object Gate = new();
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static PmLimits? Read(IEnumerable<IHardware>? hardware)
    {
        var cpu = hardware?.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu == null) return null;
        lock (Gate)
        {
            try
            {
                var smu = cpu.GetType().GetField("_smu", Any)?.GetValue(cpu);
                if (smu == null) return new PmLimits(0, null, null, null, null, null, null, null, null, false, "no SMU reader for this CPU", null);
                var t = smu.GetType();
                if (t.GetField("_supportedCPU", Any)?.GetValue(smu) is not true) return new PmLimits(0, null, null, null, null, null, null, null, null, false, "the SMU power table isn't available (PawnIO / admin)", null);
                uint version = t.GetField("_pmTableVersion", Any)?.GetValue(smu) is uint v ? v : 0;
                var sizeF = t.GetField("_pmTableSize", Any);
                if (sizeF?.GetValue(smu) is uint size && size == 0)
                {
                    var code = t.GetField("_cpuCodeName", Any)?.GetValue(smu)?.ToString();
                    uint guess = code switch { "GraniteRidge" => 0x994, "Raphael" => 0x950, _ => 0 };
                    if (guess == 0) return new PmLimits(version, null, null, null, null, null, null, null, null, false, $"power table 0x{version:X} on {code} isn't known", null);
                    sizeF.SetValue(smu, guess);
                }
                if (t.GetMethod("GetPmTable", Any)?.Invoke(smu, null) is not float[] table || table.Length < 10)
                    return new PmLimits(version, null, null, null, null, null, null, null, null, false, "the power table came back empty", null);
                return Parse(version, table);
            }
            catch (Exception e) { return new PmLimits(0, null, null, null, null, null, null, null, null, false, "couldn't read the power table: " + (e.InnerException ?? e).Message, null); }
        }
    }

    /// <summary>
    /// Two layouts are known. Zen 2 / Zen 3: [0] PPT limit, [1] PPT, [2] TDC limit, [3] TDC, [4] thermal limit, [5] temperature, [8] EDC limit,
    /// [9] EDC. Zen 4 / Zen 5 desktop (measured on a 9950X3D, table 0x620205, against LHM's package power and Tctl): [2] PPT limit, [3] PPT,
    /// [8] TDC limit, [9] TDC, [10] thermal limit, [11] temperature; EDC is not in that block, so it is left unknown. Each layout is
    /// sanity-checked and the first that passes is used; if neither does, nothing is used.
    /// </summary>
    public static PmLimits Parse(uint version, float[] t)
    {
        double? F(int i) => i < t.Length && float.IsFinite(t[i]) ? Math.Round(t[i], 1) : null;
        var head = t.Take(16).ToArray();
        string? Check(double? pptL, double? ppt, double? tdcL, double? edcL, double? thmL, double? thm, bool edcKnown) =>
            thmL is not (>= 60 and <= 115) ? "thermal limit field out of range" :
            pptL is not (>= 20 and <= 5000) ? "PPT limit field out of range" :
            ppt is not (>= 0) || ppt > pptL * 1.25 + 5 ? "PPT field out of range" :
            tdcL is not (>= 5 and <= 2000) || (edcKnown && edcL is not (>= 5 and <= 2000)) ? "TDC / EDC limit fields out of range" :
            thm is not (>= 0 and <= 125) ? "temperature field out of range" : null;
        // Zen 4 / Zen 5 desktop tables (0x54xxxx Raphael, 0x62xxxx Granite Ridge) are tried first; the older layout otherwise.
        bool newer = (version >> 16) is 0x54 or 0x62;
        var layouts = newer ? new[] { true, false } : new[] { false, true };
        string? firstWhy = null;
        foreach (var z45 in layouts)
        {
            var (pptL, ppt, tdcL, tdc, thmL, thm, edcL, edc) = z45
                ? (F(2), F(3), F(8), F(9), F(10), F(11), (double?)null, (double?)null)
                : (F(0), F(1), F(2), F(3), F(4), F(5), F(8), F(9));
            var why = Check(pptL, ppt, tdcL, edcL, thmL, thm, !z45);
            if (why == null) return new PmLimits(version, pptL, ppt, tdcL, tdc, edcL, edc, thmL, thm, true, null, head);
            firstWhy ??= why;
        }
        return new PmLimits(version, null, null, null, null, null, null, null, null, false, $"power table 0x{version:X}: {firstWhy} (layout not recognised)", head);
    }
}
