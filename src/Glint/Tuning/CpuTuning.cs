using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glint.Core;

namespace Glint.Tuning;

// Ported from Headroom (MIT, Headroom contributors): cpu_tune.py (CoreCycler config, log parsing, final values, BIOS sheet,
// kill script, watchdog), scan.py (cpu_family, microcode) and profiles/families.json (per-family and per-board data).
// CoreCycler itself (github.com/sp00n/CoreCycler, CC BY-NC-SA 4.0) is bundled unmodified under tools\CoreCycler.

/// <summary>What Glint knows about a CPU family for Curve Optimizer tuning.</summary>
public sealed record CpuFamily(string Id, string Label, string Vendor, bool Supported, string? WhyNot,
    int MinStart, int StartHint, double AbortC, int BoostOverrideMhz, int PlatformLimitC, string Notes);

/// <summary>Detection of the CPU family from its marketing name, plus the Intel microcode advisory.</summary>
public static class CpuFamilies
{
    // 4-digit model number and suffix: "Ryzen 9 9950X3D", "Ryzen 7 5800X3D", "Ryzen Threadripper PRO 5995WX"
    static readonly Regex Ryzen = new(@"Ryzen\s+(?:Threadripper\s+)?\d\s+(?:PRO\s+)?(\d{4})(\w*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex UltraA = new(@"Core\(TM\)\s+Ultra\s+\d\s+2\d\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex UltraB = new(@"Ultra\s+\d\s+2\d\dK", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex IntelGen = new(@"i\d-(\d{2})\d{3}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The 13th/14th gen microcode that fixes the voltage degradation issue.</summary>
    public const uint RaptorLakeMinMicrocode = 0x12B;

    /// <summary>amd-zen5[-x3d] / amd-zen4[-x3d] / amd-zen3[-x3d] / amd-zen2 / amd-zen4-apu / amd-other / intel-arrowlake /
    /// intel-raptorlake / intel-alderlake / intel-other / unknown.</summary>
    public static string Id(string? name)
    {
        var n = name ?? "";
        try
        {
            var m = Ryzen.Match(n);
            if (m.Success)
            {
                int num = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var suf = m.Groups[2].Value.ToUpperInvariant();
                var x3d = suf.Contains("X3D") ? "-x3d" : "";
                bool apu = suf.Contains('G');
                if (num is >= 9000 and < 10000) return "amd-zen5" + x3d;
                if (num is >= 7000 and < 8000 && num % 100 is 0 or 50 && !apu) return "amd-zen4" + x3d;
                if (num is >= 8000 and < 9000) return "amd-zen4-apu";
                if (num is >= 5000 and < 6000) return "amd-zen3" + x3d;
                if (num is >= 3000 and < 4000) return "amd-zen2";
                return "amd-other";
            }
            if (n.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return "amd-other";
            if (UltraA.IsMatch(n) || UltraB.IsMatch(n)) return "intel-arrowlake";
            var g = IntelGen.Match(n);
            if (g.Success)
            {
                int gen = int.Parse(g.Groups[1].Value, CultureInfo.InvariantCulture);
                return gen is 13 or 14 ? "intel-raptorlake" : gen == 12 ? "intel-alderlake" : "intel-other";
            }
            if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "intel-other";
        }
        catch (RegexMatchTimeoutException) { }
        return "unknown";
    }

    public static CpuFamily For(string? name) => Describe(Id(name));

    public static CpuFamily Describe(string id) => id switch
    {
        "amd-zen5-x3d" => new(id, "AMD Ryzen 9000X3D (Zen 5 + 3D V-Cache)", "amd", true, null, -50, -30, 90, 200, 85,
            "Fully unlocked (unlike 7000X3D): PBO limits and a boost override up to +200 MHz are allowed; set them in the BIOS before the CPU tune so it tests at those clocks. Ryzen 9000 often freezes instead of rebooting on a bad value: press reset, the search resumes by itself."),
        "amd-zen5" => new(id, "AMD Ryzen 9000 (Zen 5)", "amd", true, null, -50, -30, 90, 200, 85,
            "Ryzen 9000 often freezes instead of rebooting on a bad value: press reset, the search resumes by itself."),
        "amd-zen4-x3d" => new(id, "AMD Ryzen 7000X3D (Zen 4 + 3D V-Cache)", "amd", true, null, -50, -30, 85, 0, 80,
            "No boost clock override on these chips. Keep SoC voltage at or below 1.30 V and the BIOS up to date."),
        "amd-zen4" => new(id, "AMD Ryzen 7000 (Zen 4)", "amd", true, null, -50, -30, 90, 200, 85,
            "Zen 4 is designed to run hot under heavy load; 85 C platform limit is a quieter target."),
        "amd-zen3-x3d" => new(id, "AMD Ryzen 5000X3D (Zen 3 + 3D V-Cache)", "amd", true, null, -30, -20, 85, 0, 80,
            "Many boards lock Curve Optimizer on the 5800X3D; if the BIOS has no per-core option there is nothing to enter."),
        "amd-zen3" => new(id, "AMD Ryzen 5000 (Zen 3)", "amd", true, null, -30, -20, 85, 100, 85,
            "Ryzen 5000's lowest allowed Curve Optimizer value is -30."),
        "amd-zen2" => new(id, "AMD Ryzen 3000 (Zen 2)", "amd", false, "Curve Optimizer doesn't exist on Zen 2 (Ryzen 3000)", 0, 0, 85, 0, 0, ""),
        "amd-zen4-apu" => new(id, "AMD Ryzen 8000 (Zen 4 APU)", "amd", false, "Ryzen 8000 APUs aren't supported by the automatic search", 0, 0, 85, 0, 0, ""),
        "intel-raptorlake" => new(id, "Intel Core 13th/14th gen (Raptor Lake)", "intel", false,
            "no per-core Curve Optimizer on Intel. The useful fix is a BIOS with microcode 0x12B or newer and Intel's default power profile", 0, 0, 95, 0, 0,
            "Do not raise voltage or power limits on 13th/14th gen."),
        "intel-arrowlake" => new(id, "Intel Core Ultra 200S (Arrow Lake)", "intel", false, "no automatic tuning for Intel CPUs (use the '200S Boost' BIOS profile where offered)", 0, 0, 95, 0, 0, ""),
        "intel-alderlake" => new(id, "Intel Core 12th gen (Alder Lake)", "intel", false, "no automatic tuning for Intel CPUs", 0, 0, 95, 0, 0, ""),
        "intel-other" => new(id, "Intel CPU", "intel", false, "no automatic tuning for Intel CPUs", 0, 0, 95, 0, 0, ""),
        "amd-other" => new(id, "Other AMD CPU", "amd", false, "only Ryzen 5000, 7000 and 9000 desktop CPUs are supported", 0, 0, 85, 0, 0, ""),
        _ => new("unknown", "Unknown CPU", "other", false, "CPU not recognised", 0, 0, 85, 0, 0, ""),
    };

    /// <summary>The microcode revision Windows loaded: the upper 32 bits of the little-endian 8-byte "Update Revision" value. null = unknown.</summary>
    public static uint? ParseMicrocode(byte[]? raw)
    {
        if (raw == null || raw.Length < 8) return null;
        var rev = BitConverter.ToUInt32(raw, 4);
        return rev == 0 ? null : rev;
    }

    /// <summary>Warning text for 13th/14th gen CPUs below the fixed microcode (null = nothing to say).</summary>
    public static string? MicrocodeAdvisory(string familyId, uint? microcode)
    {
        if (familyId != "intel-raptorlake") return null;
        if (microcode is not uint m) return "Couldn't read the microcode revision. 13th/14th gen CPUs need microcode 0x12B or newer (update the motherboard BIOS) to stop voltage degradation.";
        return m < RaptorLakeMinMicrocode
            ? $"Microcode 0x{m:X} is older than 0x{RaptorLakeMinMicrocode:X}: update the motherboard BIOS now. Older microcode can make 13th/14th gen CPUs degrade over time (unstable, crashing)."
            : $"Microcode 0x{m:X} is current enough (0x{RaptorLakeMinMicrocode:X} or newer). Keep Intel's default power profile.";
    }
}

/// <summary>CoreCycler log parsing, final values and the BIOS sheet.</summary>
public static class CpuSearchTools
{
    /// <summary>Safety margin added to the values the search found (less negative = more stable).</summary>
    public const int Margin = 3;

    static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
    static readonly Regex Inc = new(@"Increasing Curve Optimizer value for core (\d+)\D.*?from (-?\d+) to (-?\d+)", RegexOptions.CultureInvariant, Limit);
    static readonly Regex Err = new(@"ERROR: At Core (\d+)", RegexOptions.CultureInvariant, Limit);
    // one number, then separator+number repeats: every step must consume a separator, so a hostile line can't cause backtracking blow-up
    static readonly Regex Cur = new(@"^\s*(?:Current values|CO values)\s+(-?\d+(?:[\s|]+-?\d+)*)[\s|]*$", RegexOptions.CultureInvariant, Limit);
    static readonly Regex Num = new(@"-?\d+", RegexOptions.CultureInvariant, Limit);

    public sealed record Result(int[] Values, int Adjustments, IReadOnlyDictionary<int, int> FailedCores, bool SummaryFound);

    /// <summary>Per-core Curve Optimizer values from CoreCycler log lines (oldest first). The final "CO values" / "Current values"
    /// table wins over the running tally of "Increasing ..." lines.</summary>
    public static Result Parse(IEnumerable<string> lines, int startValue, int cores)
    {
        var vals = Enumerable.Repeat(startValue, Math.Max(1, cores)).ToArray();
        int adjustments = 0;
        var failed = new Dictionary<int, int>();
        int[]? summary = null;
        foreach (var ln in lines)
        {
            try
            {
                var m = Inc.Match(ln);
                if (m.Success)
                {
                    int c = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (c >= 0 && c < vals.Length && int.TryParse(m.Groups[3].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var to)) vals[c] = to;
                    adjustments++;
                }
                m = Err.Match(ln);
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ec)) failed[ec] = failed.GetValueOrDefault(ec) + 1;
                m = Cur.Match(ln);
                if (m.Success)
                {
                    var nums = Num.Matches(m.Groups[1].Value).Select(x => int.Parse(x.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)).ToArray();
                    if (nums.Length >= 2 && nums.Length <= 64) summary = nums;
                }
            }
            catch (RegexMatchTimeoutException) { }   // one pathological line is skipped, never a hang
            catch (OverflowException) { }
        }
        return new Result(summary ?? vals, adjustments, failed, summary != null);
    }

    /// <summary>Lines of CoreCycler's logs written since <paramref name="since"/> (a minute of slack), oldest log first. Read shared: CoreCycler has them open.</summary>
    public static IEnumerable<string> ReadLogs(string dir, DateTime since)
    {
        var logs = Path.Combine(dir, "logs");
        if (!Directory.Exists(logs)) yield break;
        foreach (var f in Directory.EnumerateFiles(logs, "CoreCycler_*.log").OrderBy(x => x, StringComparer.Ordinal))
        {
            if (File.GetLastWriteTime(f) < since.AddSeconds(-60)) continue;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            for (string? ln; (ln = sr.ReadLine()) != null;) yield return ln;
        }
    }

    /// <summary>Found value + margin, never above 0 (Glint never suggests a positive Curve Optimizer).</summary>
    public static int[] FinalValues(IEnumerable<int> found) => found.Select(v => Math.Min(0, v + Margin)).ToArray();

    // ------------------------------------------------------------------ BIOS sheet

    public sealed record Board(string Label, string BiosKey, string PboPath, string ThermalName);

    static readonly Dictionary<string, Board> Boards = new()
    {
        ["asus"] = new("ASUS", "F7 for Advanced Mode", "Advanced > AMD Overclocking > (Accept) > Precision Boost Overdrive", "Platform Thermal Throttle Limit"),
        ["msi"] = new("MSI", "F7 for Advanced mode", "OC > Advanced CPU Configuration > AMD Overclocking > Precision Boost Overdrive", "Platform Thermal Throttle Limit (PBO page)"),
        ["gigabyte"] = new("Gigabyte", "F2 for Advanced Mode", "Tweaker > Advanced CPU Settings > Precision Boost Overdrive (or Settings > AMD Overclocking)", "Platform Thermal Throttle Limit / Thermal Limit"),
        ["asrock"] = new("ASRock", "F6 for Advanced Mode", "Advanced > AMD Overclocking > Precision Boost Overdrive (or OC Tweaker)", "Platform Thermal Throttle Limit"),
        ["other"] = new("your motherboard", "the BIOS advanced mode", "look for 'AMD Overclocking' > 'Precision Boost Overdrive'", "Platform Thermal Throttle Limit"),
    };

    public static IReadOnlyCollection<string> BoardVendors => Boards.Keys;

    /// <summary>"asus" / "msi" / "gigabyte" / "asrock" / "other" from the board maker and model.</summary>
    public static string BoardVendor(string? maker, string? model)
    {
        var s = ((maker ?? "") + " " + (model ?? "")).ToLowerInvariant();
        foreach (var (key, v) in new[] { ("asus", "asus"), ("micro-star", "msi"), ("msi", "msi"), ("gigabyte", "gigabyte"), ("aorus", "gigabyte"), ("asrock", "asrock") })
            if (s.Contains(key)) return v;
        return "other";
    }

    /// <summary>What each goal means for the CPU: the BIOS values besides the Curve Optimizer undervolt (null/balanced = stock power behaviour).</summary>
    public static object GoalPlan(string? goal, CpuFamily fam, PboAdvice? measured = null)
    {
        if (measured != null && fam.Supported)
        {
            var plan = GoalPlan(goal, fam);
            var pboM = $"Recommended from Glint's load test: {measured.Short.Replace("PBO: ", "PBO Limits = ")} ({measured.Detail}) Glint can't read or change the BIOS setting.";
            return plan.GetType().GetProperties().ToDictionary(p => p.Name, p => p.Name == "pbo" ? pboM : p.GetValue(plan));
        }
        var g = goal is "quiet" or "performance" ? goal : "balanced";
        string boost = fam.BoostOverrideMhz > 0 ? $"CPU Boost Clock Override Enabled (Positive), +{fam.BoostOverrideMhz} MHz" : "no boost override on this CPU";
        if (!fam.Supported)
            return new { goal = goal ?? "balanced", advisoryOnly = true, note = "Undervolting is locked on Intel CPUs and unsupported on this one: advisory only, Glint changes nothing." };
        return g switch
        {
            "quiet" => new { goal = "quiet", undervolt = "deepest stable Curve Optimizer undervolt per core", pbo = $"Manual: PPT, TDC and EDC about {QuietLimitPct}% of the CPU's default (all three together)", boostOverride = "not used (Auto)",
                             platformLimitC = fam.PlatformLimitC, fans = "CPU / radiator / case fan curves tuned for the lowest duty under the temperature target; the pump is never touched" },
            "performance" => new { goal = "performance", undervolt = "deepest stable Curve Optimizer undervolt per core (more headroom for boost)", pbo = "PBO Limits = Motherboard (the board's own PPT/TDC/EDC limits)", boostOverride = boost,
                                   platformLimitC = fam.PlatformLimitC, fans = "CPU / radiator / case fans ramp harder to hold boost; the pump is never touched" },
            _ => new { goal = "balanced", undervolt = "deepest stable Curve Optimizer undervolt per core", pbo = "PBO Limits = Auto (stock power behaviour, do not raise)", boostOverride = boost,
                       platformLimitC = fam.PlatformLimitC, fans = "tuned for the best performance per unit of fan noise; the pump is never touched" },
        };
    }

    public const int QuietLimitPct = 75;

    /// <summary>The settings to type into the BIOS for this board maker and CPU family; <paramref name="co"/> null = still searching (zeros).
    /// <paramref name="goal"/>: quiet | balanced | performance (custom and null = balanced).</summary>
    public static string BiosSheet(string? boardName, string vendor, CpuFamily fam, IReadOnlyList<int>? co, int coresIfUnknown = 8, string? goal = null)
    {
        var b = Boards.GetValueOrDefault(vendor) ?? Boards["other"];
        bool quiet = goal == "quiet", perf = goal == "performance";
        var rows = new List<string>
        {
            $"{(string.IsNullOrWhiteSpace(boardName) ? "Your motherboard" : boardName)} - enter these in the BIOS ({b.BiosKey}){(goal != null ? ", goal: " + (quiet || perf ? goal : "balanced") : "")}:",
            "  " + b.PboPath, "",
            "  Precision Boost Overdrive ........ Advanced",
        };
        if (quiet)
        {
            rows.Add("  PBO Limits ....................... Manual");
            rows.Add($"  PPT / TDC / EDC Limit ............ about {QuietLimitPct}% of the CPU's default each (lower heat and fan noise)");
        }
        else if (perf) rows.Add("  PBO Limits ....................... Motherboard   (the board's own PPT / TDC / EDC limits)");
        else rows.Add("  PBO Limits ....................... Auto   (do NOT raise)");
        if (fam.BoostOverrideMhz > 0 && !quiet)
        {
            rows.Add("  CPU Boost Clock Override ......... Enabled (Positive)");
            rows.Add($"  Max CPU Boost Clock Override(+) .. {fam.BoostOverrideMhz}");
        }
        else if (perf) rows.Add("  CPU Boost Clock Override ......... not available on this CPU (leave Auto)");
        if (fam.PlatformLimitC > 0) rows.Add($"  {b.ThermalName} {new string('.', Math.Max(2, 34 - b.ThermalName.Length))} {fam.PlatformLimitC} C");
        rows.Add("  Curve Optimizer .................. Per Core   (each Negative value is a per-core UNDERVOLT)");
        if (co == null) rows.Add("  Curve Optimizer, every core ...... Negative 0  (the search finds these)");
        else
            for (int i = 0; i < co.Count; i++)
                rows.Add($"  Core {i} Curve Optimizer {new string('.', i < 10 ? 11 : 10)} Negative {Math.Abs(co[i])}");
        rows.Add("");
        rows.Add("  Never raise any voltage (no positive Curve Optimizer, no Vcore offset). Save and exit (usually F10). Then check it is stable under a long all-core load before relying on it;");
        rows.Add("  if a core crashes later, lower its Negative value by 2-3.");
        return string.Join("\n", rows);
    }

    // ------------------------------------------------------------------ CoreCycler config + kill script

    /// <summary>config.ini for the automatic per-core search: each core alone with y-cruncher, starting at <paramref name="startValue"/>,
    /// +1 on every error, resuming by itself after a crash or freeze (written exactly as Headroom does).</summary>
    public static string SearchConfig(int startValue) => string.Join("\r\n", (
"""
# Written by Glint - per-core Curve Optimizer search
[General]
stressTestProgram = YCRUNCHER
runtimePerCore = auto
coreTestOrder = Default
numberOfThreads = 1
maxIterations = 3
lookForWheaErrors = 1
treatWheaWarningAsError = 1
beepOnError = 0
flashOnError = 0

[yCruncher]
mode = auto
tests = SFTv4, FFTv4, N63
testDuration = 20

[AutomaticTestMode]
enableAutomaticAdjustment = 1
startValues = {SV}
maxValue = 0
incrementBy = 1
setVoltageOnlyForTestedCore = 1
repeatCoreOnError = 1
enableResumeAfterUnexpectedExit = 1
waitBeforeAutomaticResume = 120
createSystemRestorePoint = 1
askForSystemRestorePointCreation = 0

[Update]
enableUpdateCheck = 0
""").Replace("{SV}", startValue.ToString(CultureInfo.InvariantCulture)).Split('\n').Select(l => l.TrimEnd('\r'))) + "\r\n";

    /// <summary>An absolute Windows path: "C:\..." or "\\server\share\...". Anything else must never become a process filter.</summary>
    public static bool IsAbsoluteWindowsPath(string? p) =>
        !string.IsNullOrEmpty(p) && (Regex.IsMatch(p, @"^[A-Za-z]:[\\/]") || Regex.IsMatch(p, @"^\\\\[^\\/]+[\\/][^\\/]+"));

    /// <summary>PowerShell that stops CoreCycler and the stress programs it started. Matched by exact folder prefix (StartsWith, not -like:
    /// '[' in a path is a wildcard there). An empty, relative or drive-root folder must never become "kill everything".</summary>
    public static string KillScript(string? dir)
    {
        var parts = new List<string> { "$_.CommandLine -match 'script-corecycler'" };
        if (IsAbsoluteWindowsPath(dir) && dir!.TrimEnd('\\', '/').Length > 3)
        {
            var prefix = dir.TrimEnd('\\', '/') + "\\";
            parts.Insert(0, "($_.ExecutablePath -and $_.ExecutablePath.StartsWith('" + prefix.Replace("'", "''") + "', [StringComparison]::OrdinalIgnoreCase))");
        }
        return "Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and (" + string.Join(" -or ", parts) +
               ") } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }";
    }
}

/// <summary>
/// Result of the read-only Curve Optimizer check: can Windows reach the CPU's SMU here (what CoreCycler's search and any
/// runtime undervolt need)? It only reads the current per-core offsets (ryzen-smu-cli --get-offsets-terse), never writes.
/// Virtualization (SVM) in the BIOS doesn't matter by itself; Memory Integrity (HVCI) blocks the tool's older fallback drivers,
/// so with it on the tool must reach the SMU through PawnIO.
/// </summary>
public sealed record SmuProbe(DateTime Time, bool Ok, int[]? Offsets, string Summary, string? Fix, bool? MemoryIntegrity, bool PawnIo, string? Output);

/// <summary>A CPU benchmark from the load test: y-cruncher's own speed (bits/s) all-core and on one core, plus clock / temperature / power.</summary>
public sealed record CpuBench(DateTime Time, double? AllCore, double? Single, double? ClockMhz, double? PeakC, double? PowerW, double? SingleMhz = null);

/// <summary>Before / after the undervolt, per profile.</summary>
public sealed class CpuBenchPair
{
    public CpuBench? Stock { get; set; }
    public CpuBench? Tuned { get; set; }
}

/// <summary>PBO / boost-override advice from an all-core load test (BIOS settings Glint can't set). Short = what the profile list shows.</summary>
public sealed record PboAdvice(DateTime Time, string Short, string Detail, double? PeakC, double? LimitC, double? PowerW, double? ClockMhz);

public static class PboAdvisor
{
    /// <summary>
    /// Thermal-limited (within 5 °C of the limit, or stopped there) = leave PBO at Auto; 10 °C+ headroom = the power limit is what
    /// holds it back, so "Motherboard" limits (plus the family's boost override where allowed) can help; in between = little to gain.
    /// A quiet goal always gets the reduced-limits advice. An error at stock = no PBO at all.
    /// </summary>
    public static PboAdvice Advise(DateTime time, double? peakC, double limitC, double? powerW, double? clockMhz, string? stopped, int boostOverrideMhz, string? goal, PmLimits? pm = null)
    {
        string at = (powerW is double w ? $"{w:0} W" : "") + (clockMhz is double c ? (powerW != null ? ", " : "") + $"{c:0} MHz" : "");
        string facts = (peakC is double p ? $"peak {p:0} °C of {limitC:0} °C" : "temperature unknown") + (at.Length > 0 ? ", " + at : "");
        PboAdvice A(string s, string d) => new(time, s, d + $" (all-core load test: {facts}). A BIOS setting: Glint doesn't change it.", peakC, limitC, powerW, clockMhz);
        if (stopped != null && stopped.Contains("error", StringComparison.OrdinalIgnoreCase))
            return A("PBO: off", "y-cruncher hit an error at the current settings under all-core load: don't add PBO until that's fixed (BIOS update, memory settings)");
        if (goal == "quiet")
            return A("PBO: reduced", "Quiet goal: PPT, TDC and EDC at about 75% of the CPU's default (all three together) for less heat and noise");
        double? headroom = peakC is double pk ? limitC - pk : null;
        bool thermal = (stopped != null && stopped.Contains("limit", StringComparison.OrdinalIgnoreCase)) || headroom is <= 5;
        if (thermal) return A("PBO: Auto", "Thermal-limited: more power would only add heat, leave PBO limits at Auto (gains come from the undervolt and cooling)");
        if (pm is { Valid: true })
        {
            // the real limits from the CPU's power table: is any of them what holds it back?
            double use = new[] { pm.PptPct, pm.TdcPct, pm.EdcPct }.Max(x => x ?? 0);
            string lim = $"PPT {pm.PptW:0} of {pm.PptLimitW:0} W, TDC {pm.TdcA:0} of {pm.TdcLimitA:0} A" + (pm.EdcLimitA is null ? "" : $", EDC {pm.EdcA:0} of {pm.EdcLimitA:0} A");
            string bios = pm.Raised ? " Your BIOS already has the PBO limits raised." : "";
            string bo = boostOverrideMhz > 0 && headroom is >= 10 ? $" A +{boostOverrideMhz} MHz boost override can still raise single-core clocks." : "";
            if (use < 92)
                return A(bo.Length > 0 ? $"PBO: no change, +{boostOverrideMhz} MHz" : "PBO: no change",
                    $"Not power-limited ({lim}; at most {use:0}% of a limit): raising PBO limits won't help.{bios}{bo}");
            if (headroom is >= 10)
                return pm.Raised
                    ? A("PBO: at its limits", $"It reaches the raised limits ({lim}) with {headroom:0} °C to spare: going higher adds heat and noise for little gain.{bo}")
                    : A(boostOverrideMhz > 0 ? $"PBO: Motherboard, +{boostOverrideMhz} MHz" : "PBO: Motherboard", $"It runs into the stock power limits ({lim}) with {headroom:0} °C to spare: PBO limits 'Motherboard' can help.{bo}");
            return A("PBO: no change", $"It reaches its power limits ({lim}) and is near its temperature limit: more power wouldn't help.");
        }
        if (headroom is >= 10)
            return A(boostOverrideMhz > 0 ? $"PBO: Motherboard, +{boostOverrideMhz} MHz" : "PBO: Motherboard",
                $"{headroom:0} °C of headroom: the power limit is what holds it back, so PBO limits 'Motherboard'{(boostOverrideMhz > 0 ? $" and a +{boostOverrideMhz} MHz boost override" : "")} can help");
        return A("PBO: Auto", headroom is double h ? $"Only {h:0} °C of headroom: little to gain from raising PBO limits" : "Temperature wasn't readable: no recommendation, leave PBO at Auto");
    }
}

public static class SmuProbeTools
{
    public const int MaxOutput = 2000;
    static readonly Regex Line = new(@"^\s*-?\d{1,3}(?:\s*,\s*-?\d{1,3})+\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The offsets from --get-offsets-terse: the last line that is only comma-separated numbers (CoreCycler reads it the
    /// same way). null = no such line, a count that doesn't match the cores, or a value outside -60..60.</summary>
    public static int[]? ParseOffsets(string? output, int cores)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var lines = output.Split('\n').Select(l => l.Trim().Trim('"', '\'')).Where(l => l.Length is > 0 and < 800).ToList();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (!Line.IsMatch(lines[i])) continue;
            var v = lines[i].Split(',').Select(x => int.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
            if (v.Any(x => x is < -60 or > 60)) return null;
            if (cores > 0 && v.Length != cores && v.Length != cores * 2) return null;
            return v;
        }
        return null;
    }

    /// <summary>Turn the tool's exit code and output into a verdict plus, when it failed, the most likely fix.</summary>
    public static SmuProbe Judge(DateTime time, int exit, string? output, int cores, bool? hvci, bool pawn)
    {
        var o = output ?? "";
        if (o.Length > MaxOutput) o = o[..MaxOutput];
        var offsets = exit == 0 ? ParseOffsets(o, cores) : null;
        if (offsets != null)
            return new(time, true, offsets, "Curve Optimizer can be read and set from Windows (through the SMU)", null, hvci, pawn, null);
        string low = o.ToLowerInvariant();
        string? fix;
        string why;
        if (exit == -1) { why = "the Curve Optimizer tool didn't answer in time"; fix = "Restart Glint and re-check; if it keeps happening another tuning app may be holding the SMU (close Ryzen Master, ZenTimings, HWiNFO's SMU access)."; }
        else if (low.Contains("framework") || low.Contains(".net") || low.Contains("hostfxr"))
        { why = "the Curve Optimizer tool couldn't start (.NET runtime)"; fix = "Install the .NET 8 Desktop Runtime (x64) from Microsoft, then re-check."; }
        else if (!pawn)
        { why = "PawnIO isn't installed, so the tool has only its older drivers" + (hvci == true ? ", which Memory Integrity blocks" : ""); fix = "Install PawnIO (Setup → Get PawnIO), restart Glint, re-check. Leave virtualization and Memory Integrity on."; }
        else if (hvci == true && (low.Contains("winio") || low.Contains("inpout") || low.Contains("driver")))
        { why = "the tool fell back to an older driver that Memory Integrity blocks"; fix = "Update PawnIO (pawnio.eu) and restart Glint so the tool can use it. Leave virtualization and Memory Integrity on."; }
        else if (low.Contains("unsupported") || low.Contains("not supported"))
        { why = "the SMU library doesn't recognise this CPU / firmware yet"; fix = "Use the BIOS sheet: enter the values in the BIOS by hand."; }
        else
        { why = "the Curve Optimizer tool failed" + (exit != 0 ? $" (exit code {exit})" : " (no offsets in its output)"); fix = "Re-check after a restart; if it keeps failing, enter the values in the BIOS by hand (BIOS sheet)."; }
        return new(time, false, null, "Curve Optimizer can't be reached from Windows: " + why, fix, hvci, pawn, o.Trim());
    }
}

/// <summary>Everything CpuTuning does to the machine, so tests never start a process.</summary>
public interface ICpuProcessRunner
{
    /// <summary>Start "Run CoreCycler.bat" in <paramref name="dir"/> without a visible window of our own.</summary>
    void Launch(string dir);
    /// <summary>Run a PowerShell command (the kill script) and wait for it.</summary>
    void RunPowerShell(string script);
    bool IsCoreCyclerRunning();
    /// <summary>Delete CoreCycler's own resume-after-reboot scheduled task.</summary>
    void DeleteTask(string name);
    /// <summary>Run a bundled tool hidden and wait at most <paramref name="timeoutMs"/>: (exit code, stdout + stderr). -1 = timed out.</summary>
    (int Exit, string Output) RunTool(string exe, IReadOnlyList<string> args, int timeoutMs) => (-2, "not available");
    /// <summary>Memory Integrity (HVCI) turned on in Windows Security? null = unknown.</summary>
    bool? MemoryIntegrity() => null;
    bool PawnIoInstalled() => false;
    /// <summary>Start the stress program hidden (no window), output captured.</summary>
    IStressRun StartStress(string exe, IReadOnlyList<string> args, string workDir) => throw new NotSupportedException("no stress runner");
}

public sealed class CpuSearchInfo
{
    public int StartValue { get; set; }
    public DateTime Since { get; set; }
    public string Dir { get; set; } = "";
    public int Cores { get; set; }
    public string Family { get; set; } = "";
    /// <summary>The goal kind the search was started for (quiet | balanced | performance | custom); null = none.</summary>
    public string? Goal { get; set; }
    /// <summary>The Glint profile the result is saved into when the search finishes (the AI run's profile, else the active one).</summary>
    public string? Profile { get; set; }
    /// <summary>performance (an undervolt that costs all-core or single-core speed vs stock is backed off) | efficiency (lowest power / temperature wins).</summary>
    public string? Mode { get; set; }
}

/// <summary>What survives restarts and freezes (cpu-state.json next to config.json).</summary>
public sealed class CpuState
{
    /// <summary>idle | searching | stopped | done</summary>
    public string Phase { get; set; } = "idle";
    public CpuSearchInfo? Search { get; set; }
    public int[]? Found { get; set; }
    public int[]? Final { get; set; }
    public string? StoppedReason { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>Native search: the value each core is being tested at, its passes in a row and its errors so far.</summary>
    public int[]? Values { get; set; }
    public int[]? Passes { get; set; }
    public int[]? Fails { get; set; }
    public int Cursor { get; set; }
    /// <summary>The core test in progress, written to disk BEFORE the value is set: after a freeze the next start knows what did it.</summary>
    public CoreTest? Testing { get; set; }
    /// <summary>Per core: the deepest value that passed, the shallowest that failed, and whether the settled value is being confirmed.</summary>
    /// <summary>sweep (all cores together, short benchmarks, find the best value) | cores (each core alone at it) | soak (all cores,
    /// full load) | complete. null = a search started by an older Glint (per-core stepping only).</summary>
    public string? Stage { get; set; }
    public List<SweepPoint>? Sweep { get; set; }
    public int? Picked { get; set; }
    /// <summary>After the descent: the values measured once more (no undervolt + the best few) before picking; null = not started.</summary>
    public List<int>? Recheck { get; set; }
    /// <summary>all (one all-core run over the values) | one (one single-core run over the candidates); null = all.</summary>
    public string? SweepPass { get; set; }
    public List<int>? Candidates { get; set; }
    public List<int>? OneQueue { get; set; }
    public int SoakTries { get; set; }
    public int HotSoaks { get; set; }
    public int?[]? Good { get; set; }
    public int?[]? Bad { get; set; }
    public bool[]? Confirm { get; set; }
}

/// <summary>One step of the sweep: every core at <see cref="Value"/>, a short all-core + single-core benchmark.</summary>
public sealed class SweepPoint
{
    public int Value { get; set; }
    public double? AllCore { get; set; }
    public double? Single { get; set; }
    public double? PowerW { get; set; }
    /// <summary>All-core: the average effective clock of every core under load.</summary>
    public double? ClockMhz { get; set; }
    /// <summary>Single-core: the effective clock of the loaded core (the fastest one).</summary>
    public double? SingleMhz { get; set; }
    public double? PeakC { get; set; }
    public bool Ok { get; set; }
    public string? Why { get; set; }
}

public sealed class CoreTest
{
    public int Core { get; set; }
    public int Value { get; set; }
    public DateTime Since { get; set; }
    public double Seconds { get; set; }
}

/// <summary>One running stress process (y-cruncher pinned to one logical CPU).</summary>
public interface IStressRun : IDisposable
{
    bool Exited { get; }
    int? ExitCode { get; }
    string Output { get; }
    /// <summary>The first error line y-cruncher printed (checked line by line as it streams, so nothing is missed), or null.</summary>
    string? ErrorLine => null;
    /// <summary>Every "Test Speed" reading so far, in bits/s, in order (streamed, so nothing is lost to the output's tail limit).</summary>
    IReadOnlyList<double> Speeds => CpuTuning.SpeedList(Output);
    void Kill();
}

/// <summary>
/// AI-driven CPU tuning for Ryzen 5000/7000/9000: the per-core Curve Optimizer search runs inside CoreCycler (bundled, y-cruncher
/// as the stress test); Glint writes its config, starts it, watches CPU temperature while it runs, and at the end turns what it
/// found into the profile's undervolt (applied through the SMU while that profile is active, never a positive value) and a BIOS sheet
/// for the permanent route. Glint never raises a clock limit or a voltage.
/// CoreCycler runs elevated, so it lives in Glint's own (admin-only) install folder, never in a user-writable one.
/// The thermal watchdog is a timer that exists only while a search runs.
/// </summary>
public sealed class CpuTuning : IDisposable
{
    public static string StatePath => Path.Combine(GlintConfig.Dir, "cpu-state.json");
    public const string StartupTask = @"\CoreCycler\CoreCycler AutoMode Startup Task";
    public const int UnreadableTripSeconds = 10;

    readonly Func<GlintConfig> _cfg;
    readonly Func<string?> _cpuName;
    readonly Func<byte[]?> _microcode;
    readonly Func<(string? Maker, string? Model)> _board;
    readonly Func<double?> _cpuTemp;
    readonly Func<int> _cores;
    readonly Func<DateTime, int> _wheaSince;
    readonly ICpuProcessRunner _proc;
    readonly Func<DateTime> _now;
    readonly string? _ccDir;
    readonly string _statePath;
    readonly object _gate = new();
    System.Threading.Timer? _timer;
    DateTime? _unreadableSince;
    double _peakC;
    SmuProbe? _smu;
    int _smuBusy, _smuAuto;
    string? _smuInfo;
    PmLimits? _pm;
    /// <summary>Reads the CPU's power table (set by the owner: LibreHardwareMonitor's SMU reader).</summary>
    public Func<PmLimits?>? PmProbe { get; set; }
    PmLimits? SafePm() { try { return PmProbe?.Invoke(); } catch { return null; } }
    /// <summary>The power limits in effect, from the last check.</summary>
    public PmLimits? Pm { get { lock (_gate) return _pm; } }
    /// <summary>Raw read-only output of the SMU tool (power table, PBO scalar) from the last check.</summary>
    public string? SmuInfo { get { lock (_gate) return _smuInfo; } }
    int[]? _coBaseline, _coApplied;
    Thread? _engine;
    volatile bool _engineStop;
    IStressRun? _run;
    /// <summary>Passes in a row a core needs at its value to count as done.</summary>
    public int PassesNeeded { get; set; } = 2;
    /// <summary>Seconds per core per pass (3 y-cruncher tests x 60 s). Tests shorten it.</summary>
    public double SecondsPerPass { get; set; } = 180;
    /// <summary>How often a running test is checked (stop, early exit).</summary>
    public int PollMs { get; set; } = 250;
    /// <summary>Delay before a search resumes after Glint starts (sensors up, a chance to stop it).</summary>
    public int ResumeDelaySec { get; set; } = 90;
    /// <summary>false = the owner drives <see cref="StepOnce"/> itself (tests).</summary>
    public bool AutoRun { get; set; } = true;
    /// <summary>CPU package power (W), average / highest core clock (MHz) and total load (%) right now (from LibreHardwareMonitor).</summary>
    public Func<(double? PowerW, double? AvgMhz, double? MaxMhz, double? LoadPct)>? LoadProbe { get; set; }
    /// <summary>Seconds between load-test samples (tests shorten it).</summary>
    public int SampleMs { get; set; } = 1000;
    /// <summary>Load-test duration multiplier (tests shorten it).</summary>
    public double LoadTimeScale { get; set; } = 1;
    volatile bool _loadStop;
    public bool LoadTesting { get; private set; }
    /// <summary>What the load test is doing right now, for the Monitor (null when none is running).</summary>
    volatile LoadLive? _loadLive;
    sealed record LoadLive(string Step, double Elapsed, double Seconds, double? TempC, double? PowerW, double? AvgMhz, double? MaxMhz, double AbortC);
    /// <summary>A load test finished: (profile it belongs to, the advice, the benchmark, true = it measured the applied undervolt).</summary>
    public event Action<string?, PboAdvice, CpuBench, bool>? LoadTested;

    static readonly Regex SpeedRx = new(@"Test Speed:\s+([0-9.]+)\s*\*\s*10\^(\d{1,2})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    /// <summary>Mean of y-cruncher's "Test Speed: 4.45 * 10^09 bits / sec" lines, in bits/s (null = none printed).</summary>
    /// <summary>Performance mode: the tuned run is within 2% of stock on all-core and single-core (missing scores don't count against it).</summary>
    /// <summary>Curve Optimizer values in a few words: "-37 on all 16 cores", or runs like "cores 0-7 -15, cores 8-15 -20".</summary>
    public static string CoText(int[] v)
    {
        static string N(int x) => x == 0 ? "0" : x.ToString(CultureInfo.InvariantCulture).Replace("-", "−");
        if (v.Length == 0) return "none";
        if (v.All(x => x == v[0])) return $"{N(v[0])} on all {v.Length} cores";
        var parts = new List<string>();
        for (int i = 0; i < v.Length;)
        {
            int j = i; while (j + 1 < v.Length && v[j + 1] == v[i]) j++;
            parts.Add(i == j ? $"core {i} {N(v[i])}" : $"cores {i}–{j} {N(v[i])}"); i = j + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Glint's own record of a finished CPU search for the profile's notes: what was set (from what), why (the sweep's edge, the
    /// checks it passed, the margin) and what it measured against the BIOS values. Facts only.
    /// </summary>
    public static string ResultNote(int[]? bios, int[] saved, CpuState st, CpuBenchPair? bench, bool backedOff)
    {
        static string N(int x) => x.ToString(CultureInfo.InvariantCulture).Replace("-", "−");
        static string Pct(double? a, double? b) => a is > 0 && b is > 0 ? $"{(a.Value / b.Value - 1) * 100:+0.0;−0.0}%" : "";
        var sb = new StringBuilder();
        sb.Append($"**CPU undervolt:** {(bios != null ? CoText(bios) + " (BIOS) → " : "")}**{CoText(saved)}**");
        if (st.Search?.Mode is { } m) sb.Append($" ({m} mode)");
        sb.Append('\n');
        var fail = st.Sweep?.Where(p => !p.Ok).OrderByDescending(p => p.Value).FirstOrDefault();
        var why = new List<string>();
        if (st.Picked is int pk) why.Add($"the sweep picked {N(pk)}{(fail != null ? $" ({N(fail.Value)} failed: {fail.Why})" : "")}");
        if (st.Found is { } f) why.Add(f.Distinct().Count() == 1 ? $"every core passed alone at {N(f[0])}" : $"each core alone settled at {CoText(f)}");
        if (st.SoakTries == 0) why.Add("the 5-minute all-core soak passed first time"); else why.Add($"the soak needed {st.SoakTries} loosening{(st.SoakTries == 1 ? "" : "s")}");
        why.Add($"saved with {CpuSearchTools.Margin} steps of margin");
        if (backedOff) why.Add("backed off 5 at a time because the deeper values measured slower than the BIOS values");
        sb.Append("- Why: ").Append(string.Join("; ", why)).Append(".\n");
        if (bench is { Stock: { } s, Tuned: { } t })
        {
            var parts = new List<string>();
            if (s.ClockMhz is > 0 && t.ClockMhz is > 0) parts.Add($"all-core clock {s.ClockMhz:0} → {t.ClockMhz:0} MHz ({Pct(t.ClockMhz, s.ClockMhz)})");
            if (s.AllCore is > 0 && t.AllCore is > 0) parts.Add($"all-core score {Pct(t.AllCore, s.AllCore)}");
            if (s.Single is > 0 && t.Single is > 0) parts.Add($"single-core score {Pct(t.Single, s.Single)}");
            if (s.PowerW is > 0 && t.PowerW is > 0) parts.Add($"power {s.PowerW:0} → {t.PowerW:0} W");
            if (s.PeakC is > 0 && t.PeakC is > 0) parts.Add($"peak {s.PeakC:0} → {t.PeakC:0} °C");
            if (parts.Count > 0) sb.Append("- Measured against the BIOS values, back to back: ").Append(string.Join(", ", parts)).Append(".\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tuned not slower than stock: by effective clock when both have it (repeatable to ~0.3% on a 9950X3D, and it includes clock
    /// stretching), else by y-cruncher's score (which moves by up to ~10% between readings of the same setting).
    /// </summary>
    public static bool NotSlower(CpuBench stock, CpuBench tuned)
    {
        bool allSlow = stock.ClockMhz is > 0 && tuned.ClockMhz is > 0 ? tuned.ClockMhz < stock.ClockMhz * 0.99 : stock.AllCore is double a && tuned.AllCore is double b && b < a * 0.98;
        bool oneSlow = stock.SingleMhz is > 0 && tuned.SingleMhz is > 0 ? tuned.SingleMhz < stock.SingleMhz * 0.99 : stock.Single is double s && tuned.Single is double u && u < s * 0.98;
        return !allSlow && !oneSlow;
    }

    public static List<double> SpeedList(string? output) => string.IsNullOrEmpty(output) ? new() :
        SpeedRx.Matches(output).Select(m => double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a * Math.Pow(10, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : double.NaN).Where(double.IsFinite).ToList();

    public static double? StressScore(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var v = SpeedRx.Matches(output).Select(m => double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a * Math.Pow(10, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : double.NaN)
            .Where(double.IsFinite).ToList();
        return v.Count == 0 ? null : v.Average();
    }
    /// <summary>Glint applied a profile's undervolt (the note says which), or put the BIOS values back (null).</summary>
    public event Action<string?>? CoAppliedChanged;
    /// <summary>Which profile a new search saves into (set by the owner).</summary>
    public Func<string?>? TargetProfile { get; set; }
    /// <summary>The load test's PBO advice for the profile (it replaces the goal's generic PBO line).</summary>
    public Func<PboAdvice?>? PboAdvice { get; set; }

    /// <summary>A new Tune press: forget a finished / stopped search (its result is already in its profile) so the tune starts fresh.</summary>
    public void ResetForTune()
    {
        lock (_gate)
        {
            if (State.Phase == "searching" || _engine is { IsAlive: true }) return;
            if (State.Phase == "idle" && State.Search == null) return;
            State = new CpuState(); Save();
        }
        Say("CPU: earlier search cleared for the new tune");
    }
    /// <summary>performance | efficiency for the next search (see <see cref="CpuSearchInfo.Mode"/>).</summary>
    public string? Mode { get; set; }
    /// <summary>The benchmark of the last finished load test.</summary>
    public CpuBench? LastBench { get; private set; }
    PboAdvice? SafePbo() { try { return PboAdvice?.Invoke(); } catch { return null; } }
    /// <summary>A complete search finished: (profile it was started for, final per-core values).</summary>
    public event Action<string?, int[]>? Finished;
    /// <summary>A running search was stopped (the BIOS values are back): the owner re-applies the active profile's undervolt.</summary>
    public event Action? Stopped;
    public CpuState State { get; private set; }
    public event Action<string>? Log;
    /// <summary>The AI run's goal (set by the owner for the run): the BIOS sheet and status list its values.</summary>
    public TuningGoal? Goal { get; set; }

    /// <param name="coreCyclerDir">Where CoreCycler is bundled (null / missing = not in this build).</param>
    /// <param name="cpuTemp">Glint's "cpu" sensor. 0 / 150+ count as unreadable.</param>
    public CpuTuning(Func<GlintConfig> cfg, Func<double?> cpuTemp, string? coreCyclerDir = null, string? statePath = null, ICpuProcessRunner? runner = null,
        Func<string?>? cpuName = null, Func<byte[]?>? microcode = null, Func<(string? Maker, string? Model)>? board = null,
        Func<int>? physicalCores = null, Func<DateTime, int>? wheaSince = null, Func<DateTime>? now = null)
    {
        _cfg = cfg; _cpuTemp = cpuTemp;
        _ccDir = coreCyclerDir ?? Path.Combine(AppContext.BaseDirectory, "tools", "CoreCycler");
        _statePath = statePath ?? StatePath;
        _proc = runner ?? new WindowsCpuProcessRunner();
        _cpuName = cpuName ?? CpuHardware.CpuName;
        _microcode = microcode ?? CpuHardware.MicrocodeRaw;
        _board = board ?? CpuHardware.Board;
        _cores = physicalCores ?? CpuHardware.PhysicalCores;
        _wheaSince = wheaSince ?? CpuHardware.WheaSince;
        _now = now ?? (() => DateTime.Now);
        State = Load(_statePath);
    }

    TuningConfig T => _cfg().Tuning ?? new TuningConfig();
    string? CcPath => !string.IsNullOrEmpty(_ccDir) && Directory.Exists(Path.Combine(_ccDir, "test_programs", "y-cruncher", "Binaries")) ? _ccDir : null;
    /// <summary>The y-cruncher build for the family (what y-cruncher itself picks: Zen 5 / Zen 4 AVX-512, Zen 2-3 AVX2).</summary>
    string? YcExe(CpuFamily fam)
    {
        if (CcPath is not { } d) return null;
        var bin = fam.Id.StartsWith("amd-zen5") ? "24-ZN5 ~ Komari.exe" : fam.Id.StartsWith("amd-zen4") ? "22-ZN4 ~ Kizuna.exe" : "19-ZN2 ~ Kagari.exe";
        var p = Path.Combine(d, "test_programs", "y-cruncher", "Binaries", bin);
        return File.Exists(p) ? p : null;
    }
    public bool WatchdogRunning { get { lock (_gate) return _timer != null; } }

    // ------------------------------------------------------------------ status

    /// <summary>Why a search can't start here (null = it can).</summary>
    public string? WhyNot(CpuFamily fam)
    {
        if (!T.Enabled) return "tuning is off (Settings → Tuning)";
        if (!fam.Supported) return fam.WhyNot ?? "this CPU isn't supported";
        if (CcPath == null || SmuCliPath == null) return "the CPU stress test (y-cruncher + SMU tool) isn't bundled in this build";
        if (YcExe(fam) == null) return "the y-cruncher binary for this CPU isn't bundled";
        return null;
    }

    string? SheetGoal(CpuSearchInfo? s) => Goal?.Kind ?? s?.Goal;

    public object Status()
    {
        var name = SafeName();
        var fam = CpuFamilies.For(name);
        uint? mc = null;
        try { mc = CpuFamilies.ParseMicrocode(_microcode()); } catch { }
        var vendor = CpuSearchTools.BoardVendor(_board().Maker, _board().Model);
        var why = WhyNot(fam);
        CpuState st; lock (_gate) st = State;
        // first status with tuning on: run the read-only SMU check once in the background (the dashboard shows it next refresh)
        if (T.Enabled && fam.Supported && fam.Vendor == "amd" && st.Phase != "searching" && Smu == null && SmuCliPath != null && Interlocked.Exchange(ref _smuAuto, 1) == 0)
            Task.Run(() => { try { ProbeSmu(); } catch { } });
        object? search = null;
        if (st.Search is { } s)
        {
            var res = Progress(st);
            bool running = st.Phase == "searching" && EngineRunning;
            int? whea = (_now() - s.Since).TotalDays < 7 ? Safe(() => _wheaSince(s.Since)) : 0;
            var final = st.Final ?? (res.SummaryFound ? CpuSearchTools.FinalValues(res.Values) : null);
            search = new
            {
                startValue = s.StartValue, since = s.Since, running, summaryFound = res.SummaryFound, cores = s.Cores,
                values = res.Values, adjustments = res.Adjustments, failedCores = res.FailedCores.ToDictionary(k => k.Key.ToString(), k => k.Value),
                wheaSinceStart = whea, peakC = _peakC > 0 ? _peakC : (double?)null,
                found = st.Found, final, margin = CpuSearchTools.Margin, passes = st.Passes, passesNeeded = PassesNeeded, live = Live(),
                biosSheet = final == null ? null : CpuSearchTools.BiosSheet(BoardName(), vendor, fam, final, 8, SheetGoal(st.Search)),
            };
        }
        return new
        {
            enabled = T.Enabled,
            phase = st.Phase, stoppedReason = st.StoppedReason,
            cpu = new
            {
                name, family = fam.Id, label = fam.Label, vendor = fam.Vendor, cores = Safe(_cores),
                microcode = mc is uint m ? "0x" + m.ToString("X") : null, advisory = CpuFamilies.MicrocodeAdvisory(fam.Id, mc),
                tempC = TuningLimits.ValidTemp(_cpuTemp()),
            },
            curveOptimizer = new
            {
                supported = fam.Supported, tunable = why == null, why, minStart = fam.Supported ? fam.MinStart : (int?)null, startHint = fam.Supported ? fam.StartHint : (int?)null,
                abortC = fam.AbortC, boostOverrideMhz = fam.BoostOverrideMhz, notes = fam.Notes,
                expect = fam.Supported ? "about 30-40 minutes (a quick sweep picks the best value, each core is checked alone, then a 5-minute all-core soak); it resumes by itself after a freeze or reboot" : null,
            },
            coreCycler = new { bundled = CcPath != null, path = CcPath },
            loadTest = new { running = LoadBusy, live = _loadLive is { } ll ? new { step = ll.Step, elapsed = Math.Round(ll.Elapsed), seconds = Math.Round(ll.Seconds), tempC = ll.TempC, powerW = ll.PowerW, avgMhz = ll.AvgMhz, maxMhz = ll.MaxMhz, abortC = ll.AbortC } : null, last = LoadInfo() },
            power = Pm is { } pmx ? new { pmx.Valid, pmx.Why, pptLimitW = pmx.PptLimitW, pptW = pmx.PptW, tdcLimitA = pmx.TdcLimitA, tdcA = pmx.TdcA, edcLimitA = pmx.EdcLimitA, edcA = pmx.EdcA, thermalLimitC = pmx.ThmLimitC, raised = pmx.Raised, version = $"0x{pmx.Version:X}", pboScalar = SmuInfo } : null,
            undervolt = new { applied = CoApplied, bios = _coBaseline },
            smu = Smu is { } sm ? new { time = sm.Time, ok = sm.Ok, summary = sm.Summary, fix = sm.Fix, offsets = sm.Offsets, memoryIntegrity = sm.MemoryIntegrity, pawnIo = sm.PawnIo, output = sm.Output } : null,
            goal = SheetGoal(st.Search) is { } gk ? new { kind = gk, text = Goal?.Text, target = Goal?.Target } : null,
            goalPlan = CpuSearchTools.GoalPlan(SheetGoal(st.Search), fam, SafePbo()),
            curveOptimizerNote = "Curve Optimizer negative values ARE per-core undervolts (less voltage than stock). Voltages are never raised.",
            board = new { name = BoardName(), vendor },
            search,
        };
    }

    // ------------------------------------------------------------------ SMU check (read-only)

    public string? SmuCliPath => CcPath is { } d && File.Exists(Path.Combine(d, "tools", "ryzen-smu-cli", "ryzen-smu-cli.exe"))
        ? Path.Combine(d, "tools", "ryzen-smu-cli", "ryzen-smu-cli.exe") : null;
    /// <summary>The last check's result (null = not run yet or not applicable).</summary>
    public SmuProbe? Smu { get { lock (_gate) return _smu; } }

    /// <summary>Read the current per-core Curve Optimizer offsets once (never writes), to tell whether runtime Curve Optimizer works on
    /// this PC. Only for supported Ryzen CPUs, never while a search runs (CoreCycler owns the SMU then). Synchronous; ~1 s.</summary>
    public SmuProbe? ProbeSmu()
    {
        var fam = CpuFamilies.For(SafeName());
        if (!T.Enabled || !fam.Supported || fam.Vendor != "amd" || SmuCliPath is not { } exe) return null;
        lock (_gate) if (State.Phase == "searching") return _smu;
        if (Interlocked.Exchange(ref _smuBusy, 1) == 1) return Smu;
        try
        {
            bool? hvci = null; bool pawn = false;
            try { hvci = _proc.MemoryIntegrity(); } catch { }
            try { pawn = _proc.PawnIoInstalled(); } catch { }
            (int Exit, string Output) r;
            try { r = _proc.RunTool(exe, new[] { "--get-offsets-terse" }, 10000); }
            catch (Exception e) { r = (-3, e.Message); }
            var res = SmuProbeTools.Judge(_now(), r.Exit, r.Output, Safe(_cores) ?? 0, hvci, pawn);
            lock (_gate) _smu = res;
            if (res.Ok)
            {
                // read-only: the tool's own info (power table with the PPT / TDC / EDC limits in effect) and the PBO scalar
                string? scalar = null;
                try { var x = _proc.RunTool(exe, new[] { "--get-pbo-scalar" }, 10000); if (x.Exit == 0) scalar = x.Output.Trim(); } catch { }
                var pm = SafePm();
                lock (_gate) { _smuInfo = scalar; _pm = pm; }
            }
            Say(res.Ok ? "CPU: " + res.Summary : "CPU: " + res.Summary + (res.Fix != null ? " Fix: " + res.Fix : ""));
            return res;
        }
        finally { Volatile.Write(ref _smuBusy, 0); }
    }

    string? SafeTarget() { try { return TargetProfile?.Invoke(); } catch { return null; } }

    /// <summary>Applied now by Glint (null = Glint hasn't changed the CPU's values since it started).</summary>
    public int[]? CoApplied { get { lock (_gate) return _coApplied?.ToArray(); } }

    /// <summary>
    /// Apply a profile's per-core Curve Optimizer values through the SMU (lost at reboot, like any runtime setting), or with null put
    /// back the values the BIOS had set (read before Glint's first change). Refused while a search runs (CoreCycler owns the SMU),
    /// for anything but a supported Ryzen, and for values outside the family's range or never-positive rule. Returns a note or null.
    /// </summary>
    public string? ApplyCo(int[]? values, string why)
    {
        var fam = CpuFamilies.For(SafeName());
        if (!T.Enabled || !fam.Supported || fam.Vendor != "amd" || SmuCliPath == null) return null;
        lock (_gate) if (State.Phase == "searching") return Note("CPU undervolt not applied: a search is running");
        int cores = Safe(_cores) ?? 0;
        if (values == null)
        {
            lock (_gate) if (_coApplied == null) return null;   // nothing of Glint's to undo
            values = _coBaseline;
            if (values == null) return null;
        }
        else if (values.Length != cores || values.Any(v => v < fam.MinStart || v > 0))
            return Note($"CPU undervolt not applied ({why}): values don't fit this CPU ({cores} cores, {fam.MinStart}..0)");
        return WriteCo(values, why) is { } e ? Note(e) : null;

        string Note(string n) { Say(n); return n; }
    }

    /// <summary>Where the per-boot BIOS values are kept: next to Glint.exe (Program Files, writable only by administrators), so a
    /// non-elevated process can't plant "BIOS values" for the elevated Glint to write back. Tests point it elsewhere.</summary>
    public string BaselinePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "co-baseline.json");
    sealed record Baseline(DateTime Boot, int[] Values);
    static DateTime BootTime() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>The values read before Glint's first change in THIS boot (a restart of Glint without a reboot must not take its
    /// own earlier values for the BIOS ones). Ignored when from another boot, another core count or out of range.</summary>
    int[]? LoadBaseline(int cores)
    {
        try
        {
            if (!File.Exists(BaselinePath)) return null;
            var b = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(BaselinePath), GlintConfig.Json);
            if (b == null || Math.Abs((b.Boot - BootTime()).TotalMinutes) > 2 || b.Values.Length != cores || b.Values.Any(v => v is < -60 or > 30)) return null;
            return b.Values;
        }
        catch { return null; }
    }
    void SaveBaseline(int[] values)
    {
        try { File.WriteAllText(BaselinePath, JsonSerializer.Serialize(new Baseline(BootTime(), values), GlintConfig.Json)); } catch { }
    }

    /// <summary>Put the BIOS values back if Glint changed anything (end of a search, stop).</summary>
    void RestoreCo() { int[]? b; lock (_gate) b = _coApplied != null ? _coBaseline : null; if (b != null) WriteCo(b, "search over"); }

    /// <summary>Write per-core values through the SMU, reading the BIOS values first (once) so they can be put back.
    /// Checks again: the right core count, never positive unless it is the BIOS's own values being restored.</summary>
    string? WriteCo(int[] values, string why)
    {
        var fam = CpuFamilies.For(SafeName());
        if (SmuCliPath is not { } exe) return "the SMU tool isn't bundled";
        int cores = Safe(_cores) ?? 0;
        bool restore; lock (_gate) restore = _coBaseline != null && ReferenceEquals(values, _coBaseline);
        if (values.Length != cores || (!restore && values.Any(v => v < Math.Min(-1, fam.MinStart) || v > 0))) return $"values don't fit this CPU ({cores} cores)";
        if (_coBaseline == null && LoadBaseline(cores) is { } kept) lock (_gate) _coBaseline = kept;
        if (_coBaseline == null)
        {
            var r = SafeTool(exe, new[] { "--get-offsets-terse" });
            var b = r.Exit == 0 ? SmuProbeTools.ParseOffsets(r.Output, cores) : null;
            if (b == null || b.Length != cores || b.Any(v => v is < -60 or > 30)) return "CPU undervolt not applied: couldn't read the current values first, so nothing was changed";
            lock (_gate) _coBaseline = b;
            SaveBaseline(b);
        }
        var w = SafeTool(exe, new[] { "--offset", string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture))) });
        if (w.Exit != 0) return $"CPU undervolt not applied ({why}): the SMU tool failed (exit {w.Exit})";
        lock (_gate) _coApplied = restore ? null : values.ToArray();
        try { CoAppliedChanged?.Invoke(restore || why == "search" ? null : why); } catch { }
        if (why != "search") Say(restore ? $"CPU: BIOS Curve Optimizer values back ({why})" : $"CPU: applied undervolt {string.Join(" ", values)} ({why})");
        return null;
    }

    (int Exit, string Output) SafeTool(string exe, string[] args)
    {
        try { return _proc.RunTool(exe, args, 10000); } catch (Exception e) { return (-3, e.Message); }
    }

    // ------------------------------------------------------------------ start / finish / stop

    /// <summary>Write CoreCycler's config, start it and start watching temperatures. Returns at once; the search takes hours.</summary>
    public object StartSearch(int startValue)
    {
        var fam = CpuFamilies.For(SafeName());
        if (WhyNot(fam) is { } why) throw new InvalidOperationException(why);
        if (startValue is < -50 or > 0) throw new ArgumentOutOfRangeException(nameof(startValue), "startValue must be -50..0");
        string? note = null;
        if (startValue < fam.MinStart) { note = $"{fam.Label} allows {fam.MinStart} at most: start value raised from {startValue}"; startValue = fam.MinStart; }
        lock (_gate)
        {
            if (State.Phase == "searching" || WatchdogRunning) throw new InvalidOperationException("a CPU search is already running (tuning.cpu.status shows its progress; tuning.cpu.stop ends it)");
            if (LoadTesting) throw new InvalidOperationException("a CPU load test is running: wait for it to finish");
        }
        // the watchdog is the safety net: don't start a multi-hour stress test it couldn't watch
        var t = TuningLimits.ValidTemp(_cpuTemp());
        if (t is not double now0) throw new InvalidOperationException("CPU temperature isn't readable (is Glint running as administrator?): refusing to stress-test the CPU unwatched");
        if (t >= fam.AbortC - 10) throw new InvalidOperationException($"the CPU is already at {t:0} °C: let it cool down below {fam.AbortC - 10:0} °C first");
        int cores = Math.Max(1, Safe(_cores) ?? 8);
        lock (_gate)
        {
            // on disk before anything is set: a freeze must find the search recorded
            State = new CpuState
            {
                Phase = "searching", Search = new CpuSearchInfo { StartValue = startValue, Since = _now(), Dir = CcPath!, Cores = cores, Family = fam.Id, Goal = Goal?.Kind, Profile = SafeTarget(), Mode = Mode },
                Values = Enumerable.Repeat(startValue, cores).ToArray(), Passes = new int[cores], Fails = new int[cores],
                Stage = "sweep", Sweep = new(),
            };
            _peakC = now0;
            Save();
        }
        Say($"CPU tune started on {cores} cores: sweeping Curve Optimizer values with short benchmarks ({Mode ?? "performance"})");
        StartWatchdog(0);
        StartEngine(0);
        return new { started = true, startValue, cores, abortC = fam.AbortC, note, takes = $"about {Math.Round((15 * 3 * SweepLineSec + 8 * 12 + cores * CoreCheckSeconds + SoakSeconds + 180) / 60.0, 0)} min: a sweep (all cores at 0, -{SweepStepSize}, -{2 * SweepStepSize}... down to {fam.MinStart}, measured inside one benchmark run so the values compare fairly, then the best few on one core) picks the best value for the mode, each core is then checked alone at it ({CoreCheckSeconds:0} s, loosened by {CoreBackoff} on an error) and the result is soaked on all cores for {SoakSeconds / 60:0} min. It resumes by itself after a freeze or reboot. tuning.cpu.status follows it; it finishes and saves by itself." };
    }

    /// <summary>Stop CoreCycler, compute the final values (found + 3 margin, never positive) and the BIOS sheet.</summary>
    public object Finish(bool force = false)
    {
        CpuState st; lock (_gate) st = State;
        if (st.Search is not { } s) throw new InvalidOperationException("no CPU search has been started");
        var fam = CpuFamilies.Describe(s.Family);
        var vendor = CpuSearchTools.BoardVendor(_board().Maker, _board().Model);
        if (st.Phase == "done" && st.Final != null)
            return new { final = st.Final, found = st.Found, margin = CpuSearchTools.Margin, complete = true, biosSheet = CpuSearchTools.BiosSheet(BoardName(), vendor, fam, st.Final, 8, SheetGoal(s)) };
        var res = Progress(st);
        if (!res.SummaryFound && !force && st.Phase == "searching")
            throw new InvalidOperationException("the search hasn't finished yet (summaryFound is false and it is still working). Wait for it, or call again with force=true to use the values found so far: cores it hasn't finished keep their current value and are untested");
        StopEngine();
        StopWatchdog();
        RestoreCo();
        var final = CpuSearchTools.FinalValues(res.Values);
        lock (_gate)
        {
            State.Found = res.Values; State.Final = final; State.Phase = "done"; State.StoppedReason = null; State.FinishedAt = _now(); State.Testing = null;
            Save();
        }
        Say("CPU search finished: " + string.Join(" ", final));
        if (res.SummaryFound) try { Finished?.Invoke(s.Profile, final); } catch (Exception e) { Say("CPU: couldn't save the result to the profile: " + e.Message); }
        return new
        {
            final, found = res.Values, margin = CpuSearchTools.Margin, complete = res.SummaryFound, failedCores = res.FailedCores.ToDictionary(k => k.Key.ToString(), k => k.Value),
            warning = res.SummaryFound ? null : "the search hadn't printed its final table: cores not reached keep the start value, untested",
            biosSheet = CpuSearchTools.BiosSheet(BoardName(), vendor, fam, final, 8, SheetGoal(st.Search)),
        };
    }

    /// <summary>Stop the search and its stress test now (also removes CoreCycler's resume-after-reboot task). Progress is kept: tuning.cpu.finish still works.</summary>
    public object Stop(string? reason = null)
    {
        bool had;
        lock (_gate) had = State.Phase == "searching";
        StopEngine();
        StopWatchdog();
        if (had) RestoreCo();
        lock (_gate)
        {
            if (State.Phase == "searching") { State.Phase = "stopped"; State.StoppedReason = reason ?? "stopped by request"; State.Testing = null; Save(); }
        }
        if (had) Say("CPU search stopped" + (reason != null ? ": " + reason : ""));
        if (had) try { Stopped?.Invoke(); } catch { }
        return new { stopped = had };
    }

    /// <summary>Called once at startup: a search that was running when Glint (or the PC) went away is still CoreCycler's to resume,
    /// so the watchdog must be back before it does. Delayed a minute so sensors are up (an unreadable sensor trips it).</summary>
    public void OnStartup()
    {
        lock (_gate) if (State.Phase != "searching") return;
        if (!T.Enabled) { Stop("tuning was turned off"); return; }
        var live = CpuFamilies.For(SafeName()); int liveCores = Safe(_cores) ?? 0;
        bool mismatch; lock (_gate) mismatch = State.Search?.Family != live.Id || State.Values?.Length != liveCores || !live.Supported;
        if (mismatch) { Stop("the saved search doesn't match this CPU"); return; }
        lock (_gate)
        {
            // the core under test when the PC froze / rebooted failed at that value: loosen it by one and carry on
            if (State.Testing is { Core: -2 } ts)
            {
                (State.Sweep ??= new()).Add(new SweepPoint { Value = ts.Value, Ok = false, Why = "froze or crashed the PC" });
                State.Testing = null; Save();
                Say($"CPU: the sweep at {ts.Value} froze or crashed the PC: the sweep ends there");
            }
            else if (State.Testing is { Core: -1 } tk && State.Values is { } sv)
            {
                State.SoakTries++;
                for (int i = 0; i < sv.Length; i++) sv[i] = Math.Min(0, sv[i] + SoakBackoff);
                State.Testing = null; Save();
                Say($"CPU: the all-core soak froze or crashed the PC: every core loosened by {SoakBackoff}");
            }
            else if (State.Stage == "cores" && State.Testing is { } tc && State.Values is { } cv && tc.Core >= 0 && tc.Core < cv.Length)
            {
                cv[tc.Core] = Math.Min(0, tc.Value + CoreBackoff); State.Passes ??= new int[cv.Length]; State.Passes[tc.Core] = 0; State.Fails ??= new int[cv.Length]; State.Fails[tc.Core]++;
                State.Testing = null; Save();
                Say($"CPU: core {tc.Core} at {tc.Value} froze or crashed the PC: now {cv[tc.Core]}");
            }
            else if (State.Testing is { } t && State.Values is { } v && t.Core >= 0 && t.Core < v.Length)
            {
                State.Passes ??= new int[v.Length]; State.Fails ??= new int[v.Length];
                Advance(State, t.Core, t.Value, false, live.MinStart, PassesNeeded);
                State.Testing = null;
                Save();
                Say($"CPU: core {t.Core} at {t.Value} froze or crashed the PC: now testing {v[t.Core]}");
            }
        }
        StartWatchdog(60);
        StartEngine(ResumeDelaySec);
    }

    // ------------------------------------------------------------------ native search engine

    public bool EngineRunning { get { lock (_gate) return _engine is { IsAlive: true }; } }

    void StartEngine(int delaySec)
    {
        if (!AutoRun) return;
        lock (_gate)
        {
            if (_engine is { IsAlive: true }) return;
            _engineStop = false;
            _engine = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < delaySec * 4 && !_engineStop; i++) Thread.Sleep(250);
                    while (!_engineStop && StepOnce()) { }
                }
                catch (Exception e) { Say("CPU search error: " + e.Message); try { Stop("error: " + e.Message); } catch { } }
            }) { IsBackground = true, Name = "Glint CPU search", Priority = ThreadPriority.AboveNormal };
            _engine.Start();
        }
    }

    void StopEngine()
    {
        KillSweepRunSoon();
        Thread? t;
        lock (_gate) { _engineStop = true; t = _engine; }
        try { _run?.Kill(); } catch { }
        if (t != null && t != Thread.CurrentThread) t.Join(15000);
        lock (_gate) if (_engine == t) _engine = null;
    }

    /// <summary>How far a core steps deeper after a pass while it hasn't failed yet.</summary>
    public const int DeepStep = 5;

    /// <summary>
    /// Record one test of core <paramref name="c"/> at <paramref name="v"/> and pick its next value. A pass steps it
    /// <see cref="DeepStep"/> deeper (down to <paramref name="floor"/>); an error halves the gap between the deepest pass and the
    /// shallowest error (no pass yet: 1 shallower). When the two meet, the deepest pass is confirmed with <paramref name="need"/>
    /// passes in a row (an error there drops it and the search carries on above it). Returns what happens next, for the log.
    /// </summary>
    public static string Advance(CpuState st, int c, int v, bool passed, int floor, int need)
    {
        int n = st.Values!.Length;
        st.Passes ??= new int[n]; st.Fails ??= new int[n];
        st.Good ??= new int?[n]; st.Bad ??= new int?[n]; st.Confirm ??= new bool[n];
        if (passed)
        {
            st.Passes[c]++;
            if (st.Confirm[c]) return st.Passes[c] >= need ? $"settled at {v}" : $"confirming {v} ({st.Passes[c]}/{need})";
            st.Good[c] = v;
        }
        else
        {
            st.Fails[c]++;
            if (st.Confirm[c]) { st.Confirm[c] = false; st.Good[c] = null; }
            st.Bad[c] = v;
        }
        st.Passes[c] = 0;
        int? g = st.Good[c], b = st.Bad[c];
        int? next = null;
        if (g is null) next = Math.Min(0, b!.Value + 1);
        else if (b is null) { if (g.Value > floor) next = Math.Max(floor, g.Value - DeepStep); }
        else { int mid = (g.Value + b.Value) / 2; if (mid != g.Value && mid != b.Value) next = mid; }
        if (next is int x) { st.Values[c] = x; return x >= 0 ? "no undervolt for this core" : $"next {x}"; }
        // the deepest pass is the edge: confirm it (its exploring pass counts as the first)
        st.Values[c] = g!.Value; st.Confirm[c] = true; st.Passes[c] = 1;
        return st.Passes[c] >= need ? $"settled at {g}" : $"confirming {g} (1/{need})";
    }

    static bool Done(CpuState st, int i, int need) => st.Passes![i] >= need || st.Values![i] >= 0;

    /// <summary>The search so far in the shape the status, finish and tests use.</summary>
    CpuSearchTools.Result Progress(CpuState st)
    {
        var s = st.Search!;
        var vals = st.Values ?? Enumerable.Repeat(s.StartValue, s.Cores).ToArray();
        bool done = st.Stage == "complete" || (st.Stage == null && st.Values != null && st.Passes != null && Enumerable.Range(0, vals.Length).All(i => Done(st, i, PassesNeeded)));
        var fails = new Dictionary<int, int>();
        if (st.Fails != null) for (int i = 0; i < st.Fails.Length; i++) if (st.Fails[i] > 0) fails[i] = st.Fails[i];
        return new CpuSearchTools.Result(vals.ToArray(), fails.Values.Sum(), fails, done);
    }

    /// <summary>
    /// One core test: set that core's Curve Optimizer value (every other core at 0) through the SMU, run y-cruncher pinned to that
    /// core for <see cref="SecondsPerPass"/>, judge it (an early exit = y-cruncher found an error, or any WHEA hardware error), and
    /// record it: a pass counts toward <see cref="PassesNeeded"/>, an error loosens the core by 1 and resets its passes. Returns false
    /// when the search is complete (it then finishes and saves by itself) or stopped. Public so tests can drive it with no thread.
    /// </summary>
    public bool StepOnce()
    {
        string? stage; lock (_gate) { if (State.Phase != "searching") return false; stage = State.Stage; }
        if (stage == "sweep") return SweepStep();
        if (stage == "soak") return SoakStep();
        if (stage == "complete") goto complete;
        int c, v, cores; CpuFamily fam;
        lock (_gate)
        {
            if (State.Phase != "searching" || State.Values is not { } vals || State.Passes == null || State.Search == null) return false;
            fam = CpuFamilies.For(SafeName());   // the live CPU, never the family stored in the (user-writable) state file
            cores = vals.Length;
            c = -1;
            for (int k = 0; k < cores; k++) { int i = (State.Cursor + k) % cores; if (!Done(State, i, Need(State))) { c = i; break; } }
            if (c < 0 && State.Stage == "cores") { State.Stage = "soak"; State.Testing = null; Save(); }
            if (c < 0) { if (State.Stage == "soak") { Say("CPU: every core passed alone: soaking all cores together"); return true; } goto complete; }
            v = vals[c];
            State.Cursor = c;   // one core to the end before the next: steady progress, a clear 'cores done'
        }
        var exe = YcExe(fam);
        if (exe == null) { Stop("y-cruncher isn't bundled"); return false; }
        var vec = new int[cores]; vec[c] = v;
        if (WriteCo(vec, "search") is { } err) { Stop("couldn't set Curve Optimizer: " + err); return false; }
        var since = _now();
        bool quick = stage == "cores";
        double secs = quick ? CoreCheckSeconds : SecondsPerPass;
        lock (_gate) { State.Testing = new CoreTest { Core = c, Value = v, Since = since, Seconds = secs }; Save(); }
        int threadsPerCore = Math.Max(1, Environment.ProcessorCount / Math.Max(1, cores));
        var cfgFile = Path.Combine(CcPath!, "glint-core.cfg");
        File.WriteAllText(cfgFile, YcConfig(c * threadsPerCore, quick ? Math.Max(5, (int)Math.Round(CoreCheckSeconds / 3)) : 60), new UTF8Encoding(false));
        var (why, broken) = RunStress(exe, cfgFile, secs, since);
        if (broken != null) { Stop(broken); return false; }
        if (_engineStop) { lock (_gate) { State.Testing = null; Save(); } return false; }
        string step;
        lock (_gate)
        {
            if (quick)
            {
                if (why == null) { State.Passes![c] = 1; step = "done"; }
                else { State.Values![c] = Math.Min(0, v + CoreBackoff); State.Passes![c] = 0; State.Fails![c]++; step = $"now {State.Values[c]}"; }
            }
            else step = Advance(State, c, v, why == null, fam.MinStart, PassesNeeded);
            State.Testing = null;
            Save();
        }
        Say(why != null ? $"CPU core {c} failed at {v} ({why}): {step}" : $"CPU core {c} passed at {v}: {step}");
        return true;

    complete:
        Say("CPU search complete: finishing");
        try { Finish(); } catch (Exception e) { Say("CPU: couldn't finish: " + e.Message); }
        return false;
    }

    /// <summary>Run y-cruncher with <paramref name="cfgFile"/> for <paramref name="seconds"/>: (error, or "can't judge" reason).</summary>
    (string? Why, string? Broken) RunStress(string exe, string cfgFile, double seconds, DateTime since) => RunStress(exe, cfgFile, seconds, since, null, out _, out _);

    /// <param name="tempStop">Stop the run (not a failure) when the CPU reaches this temperature; <paramref name="hot"/> says it did.</param>
    (string? Why, string? Broken) RunStress(string exe, string cfgFile, double seconds, DateTime since, double? tempStop, out bool hot, out double ran)
    {
        hot = false;
        string? why = null, broken = null;
        var sw = Stopwatch.StartNew();
        IStressRun? run = null;
        int quickExits = 0;
        var SecondsPerPass = seconds;
        try
        {
            while (sw.Elapsed.TotalSeconds < SecondsPerPass && !_engineStop && why == null && broken == null && !hot)
            {
                run = _proc.StartStress(exe, new[] { "skip-warnings", "pause:-2", "config", cfgFile }, Path.GetDirectoryName(exe)!);
                lock (_gate) _run = run;
                var started = sw.Elapsed.TotalSeconds;
                while (sw.Elapsed.TotalSeconds < SecondsPerPass && !_engineStop && !run.Exited)
                {
                    Thread.Sleep(PollMs);
                    if (tempStop is double ts && TuningLimits.ValidTemp(_cpuTemp()) is double tc && tc >= ts) { hot = true; break; }
                }
                if (!hot && run.Exited && !_engineStop && sw.Elapsed.TotalSeconds < SecondsPerPass)
                {
                    // an error = a non-zero exit, or error text in its output; a clean exit with no error just restarts it
                    if (run.ExitCode is int code && code != 0) why = $"y-cruncher stopped with an error (exit {code})";
                    else if ((run.ErrorLine ?? StressErrorText(run.Output)) is { } line) why = "y-cruncher reported: " + line;
                    else if (sw.Elapsed.TotalSeconds - started < 5 && ++quickExits >= 3) broken = "y-cruncher keeps exiting at once without an error: can't judge stability";
                }
                if (why == null && !_engineStop && run.ErrorLine is { } streamed) why = "y-cruncher reported: " + streamed;
                try { run.Kill(); } catch { }
                try { run.Dispose(); } catch { }
                lock (_gate) _run = null; run = null;
            }
        }
        finally { try { run?.Kill(); } catch { } try { run?.Dispose(); } catch { } lock (_gate) _run = null; }
        ran = sw.Elapsed.TotalSeconds;
        if (why == null && broken == null && !_engineStop && (Safe(() => _wheaSince(since)) ?? 0) is int whea and > 0) why = $"{whea} hardware error{(whea == 1 ? "" : "s")} (WHEA)";
        return (why, broken);
    }

    void KillSweepRunSoon() { try { _sweepRun?.Kill(); } catch { } }

    // ------------------------------------------------------------------ sweep and soak

    public int SweepStepSize { get; set; } = 5;
    public double CoreCheckSeconds { get; set; } = 60;
    public double SoakSeconds { get; set; } = 300;
    public const int CoreBackoff = 3, SoakBackoff = 2, MaxSoakTries = 3;

    int Need(CpuState st) => st.Stage == "cores" ? 1 : PassesNeeded;

    /// <summary>
    /// The Monitor's sweep chips: each reading vs the no-undervolt readings of the same kind (all-core against all-core, single-core
    /// against single-core, averaged), so single-core readings no longer all show 0%.
    /// </summary>
    public static object[]? SweepChips(IReadOnlyList<SweepPoint>? pts)
    {
        if (pts == null) return null;
        var stock = Averaged(pts).FirstOrDefault(p => p.Value == 0);
        return pts.Select(p => (object)new
        {
            value = p.Value, ok = p.Ok, kind = p.AllCore != null || p.ClockMhz != null ? "all" : "one",
            score = stock == null || !p.Ok ? (double?)null : Math.Round((Composite(p, stock) - 1) * 100, 1),
            mhz = p.ClockMhz ?? p.SingleMhz, powerW = p.PowerW, why = p.Why,
        }).ToArray();
    }

    /// <summary>
    /// The sweep's score for a point: 60% all-core, 40% single-core, relative to the no-undervolt point; 1 when unmeasured. Each side
    /// uses the effective clock when both points have it: on a 9950X3D the same setting read 4636 / 4645 MHz but y-cruncher's score
    /// moved by up to ~10% within one run; the effective clock also drops when the core stretches its clock (too deep an undervolt).
    /// </summary>
    public static double Composite(SweepPoint p, SweepPoint stock)
    {
        double? a = p.ClockMhz is > 0 && stock.ClockMhz is > 0 ? p.ClockMhz / stock.ClockMhz : p.AllCore is > 0 && stock.AllCore is > 0 ? p.AllCore / stock.AllCore : null;
        double? s = p.SingleMhz is > 0 && stock.SingleMhz is > 0 ? p.SingleMhz / stock.SingleMhz : p.Single is > 0 && stock.Single is > 0 ? p.Single / stock.Single : null;
        return a is double x && s is double y ? 0.6 * x + 0.4 * y : a ?? s ?? 1;
    }

    /// <summary>The next sweep value, or null when the sweep is over (an error, the floor, or scores falling two steps in a row: clock stretching).</summary>
    public static int? NextSweep(IReadOnlyList<SweepPoint> pts, int floor, int step)
    {
        if (pts.Count == 0) return 0;
        var last = pts[^1];
        if (!last.Ok || last.Value - step < floor) return null;
        var ok = pts.Where(p => p.Ok).ToList();
        if (ok.Count >= 4)
        {
            // single short runs are noisy (a few %): stop only when the last two are both well below the best
            double best = ok.Max(p => Composite(p, pts[0]));
            if (Composite(ok[^1], pts[0]) < best * 0.96 && Composite(ok[^2], pts[0]) < best * 0.96) return null;
        }
        return last.Value - step;
    }

    /// <summary>Every measurement of each value averaged (a value that failed once is out); the no-undervolt value is the reference.</summary>
    public static List<SweepPoint> Averaged(IReadOnlyList<SweepPoint> pts)
    {
        static double? Avg(IEnumerable<double?> v) { var l = v.Where(x => x is > 0).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Average(); }
        return pts.GroupBy(p => p.Value).Select(g => new SweepPoint
        {
            Value = g.Key, Ok = g.All(p => p.Ok), Why = g.FirstOrDefault(p => !p.Ok)?.Why,
            AllCore = Avg(g.Where(p => p.Ok).Select(p => p.AllCore)), Single = Avg(g.Where(p => p.Ok).Select(p => p.Single)),
            PowerW = Avg(g.Where(p => p.Ok).Select(p => p.PowerW)), ClockMhz = Avg(g.Where(p => p.Ok).Select(p => p.ClockMhz)), SingleMhz = Avg(g.Where(p => p.Ok).Select(p => p.SingleMhz)), PeakC = g.Max(p => p.PeakC),
        }).OrderByDescending(p => p.Value).ToList();
    }

    /// <summary>The values to measure again before picking: no undervolt and the best three (by their single run so far).</summary>
    public static List<int> RecheckList(IReadOnlyList<SweepPoint> pts, string? mode = null)
    {
        var av = Averaged(pts);
        if (av.Count == 0 || av[0].Value != 0 || !av[0].Ok) return new();
        var ok = av.Where(p => p.Ok && p.Value < 0).ToList();
        var best = mode == "efficiency" && ok.All(p => p.PowerW is > 0)
            ? ok.Where(p => Composite(p, av[0]) >= 0.95).OrderByDescending(p => Composite(p, av[0]) / p.PowerW!.Value).ThenBy(p => p.Value)
            : ok.OrderByDescending(p => Math.Round(Composite(p, av[0]), 3)).ThenBy(p => p.Value);   // a tie: the deeper value
        return new[] { 0 }.Concat(best.Take(3).Select(p => p.Value)).ToList();
    }

    /// <summary>
    /// The value to keep: performance = the best score (the deepest within 0.5% of it); efficiency = the best score per watt among
    /// points within 5% of stock. Null = nothing usable (the no-undervolt point failed); 0 = no undervolt beat stock.
    /// </summary>
    public static int? PickSweep(IReadOnlyList<SweepPoint> raw, string? mode)
    {
        var pts = Averaged(raw);
        var ok = pts.Where(p => p.Ok).ToList();
        if (ok.Any(p => p.Value != 0 && p.Single != null)) ok = ok.Where(p => p.Single != null).ToList();   // like for like: the values measured in both passes
        if (ok.Count == 0 || !pts[0].Ok) return null;
        var stock = pts[0];
        if (mode == "efficiency")
        {
            var el = ok.Where(p => Composite(p, stock) >= 0.95).ToList();
            if (el.All(p => p.PowerW is > 0)) return el.OrderByDescending(p => Composite(p, stock) / p.PowerW!.Value).ThenBy(p => p.Value).First().Value;
            return el.Min(p => p.Value);
        }
        double best = ok.Max(p => Composite(p, stock));
        return ok.Where(p => Composite(p, stock) >= best * 0.995).Min(p => p.Value);
    }

    // One y-cruncher process stays up for a whole pass and the Curve Optimizer is changed under it: separate launches of the same
    // test differ by up to ~20% on a 9950X3D (thread placement / V-cache scheduling), while readings within one run agree to ~1%.
    IStressRun? _sweepRun;
    string? _sweepKind;

    void KillSweepRun()
    {
        IStressRun? r; lock (_gate) { r = _sweepRun; _sweepRun = null; _sweepKind = null; if (ReferenceEquals(_run, r)) _run = null; }
        try { r?.Kill(); } catch { } try { r?.Dispose(); } catch { }
    }

    /// <summary>Seconds per y-cruncher speed line for the sweep's all-core and single-core runs.</summary>
    public int SweepLineSec { get; set; } = 5;

    IStressRun? SweepRun(string exe, string kind)
    {
        lock (_gate) if (_sweepRun is { Exited: false } live && _sweepKind == kind) return live;
        KillSweepRun();
        var cfg = Path.Combine(CcPath!, kind == "all" ? "glint-sweep.cfg" : "glint-sweep1.cfg");
        File.WriteAllText(cfg, kind == "all" ? YcConfigAll(Environment.ProcessorCount, 0, SweepLineSec) : YcConfigOne(0), new UTF8Encoding(false));
        var run = _proc.StartStress(exe, new[] { "skip-warnings", "pause:-2", "config", cfg }, Path.GetDirectoryName(exe)!);
        lock (_gate) { _sweepRun = run; _sweepKind = kind; _run = run; }
        // warm-up: the first reading comes from a cold start; wait for it and ignore it
        var sw = Stopwatch.StartNew();
        while (run.Speeds.Count < 1 && !run.Exited && !_engineStop && sw.Elapsed.TotalSeconds < (SweepLineSec * 4 + 10) * LoadTimeScale) Thread.Sleep(SampleMs);
        return run;
    }

    /// <summary>
    /// Set every core to <paramref name="vec"/> under the running process and read it: the reading that spans the change is dropped,
    /// the next two are kept (power, clock and temperature sampled meanwhile). An error, an exit or the temperature limit = failed.
    /// </summary>
    SweepPoint MeasureLive(CpuFamily fam, IStressRun run, int value, int[] vec, bool allCore, DateTime since)
    {
        var pt = new SweepPoint { Value = value, Ok = true };
        if (WriteCo(vec, "search") is { } err) { pt.Ok = false; pt.Why = "couldn't set Curve Optimizer: " + err; return pt; }
        var temps = new List<double>(); var power = new List<double>(); var clk = new List<double>(); var top = new List<double>();
        int start = run.Speeds.Count;
        var sw = Stopwatch.StartNew();
        double limit = (SweepLineSec * 5 + 15) * LoadTimeScale;
        string? why = null;
        while (run.Speeds.Count < start + 3 && !_engineStop)
        {
            Thread.Sleep(SampleMs);
            if (run.ErrorLine is { } el) { why = "y-cruncher: " + el; break; }
            if (run.Exited) { why = run.ExitCode is int ec && ec != 0 ? $"y-cruncher error (exit {ec})" : (StressErrorText(run.Output) is { } l ? "y-cruncher: " + l : "y-cruncher stopped"); break; }
            if (TuningLimits.ValidTemp(_cpuTemp()) is double tc) { temps.Add(tc); if (tc >= fam.AbortC) { why = $"reached the {fam.AbortC:0} °C limit"; break; } }
            if (run.Speeds.Count > start) try { if (LoadProbe?.Invoke() is var (pw, a, m, _)) { if (pw is > 0) power.Add(pw.Value); if (a is > 0) clk.Add(a.Value); if (m is > 0) top.Add(m.Value); } } catch { }
            if (sw.Elapsed.TotalSeconds > limit) { why = "y-cruncher stopped printing results"; break; }
        }
        if (why == null && !_engineStop && (Safe(() => _wheaSince(since)) ?? 0) is int whea and > 0) why = $"{whea} hardware error{(whea == 1 ? "" : "s")} (WHEA)";
        if (why != null) { pt.Ok = false; pt.Why = why; return pt; }
        var sp = run.Speeds; double score = (sp[start + 1] + sp[start + 2]) / 2;
        if (allCore) pt.AllCore = score; else pt.Single = score;
        pt.PowerW = allCore && power.Count > 0 ? Math.Round(power.Average(), 1) : null; pt.PeakC = temps.Count > 0 ? temps.Max() : null;   // power: all-core only (per-watt picks)
        if (allCore) pt.ClockMhz = clk.Count > 0 ? Math.Round(clk.Average()) : null;
        else pt.SingleMhz = top.Count > 0 ? Math.Round(top.Average()) : null;
        return pt;
    }

    bool SweepStep()
    {
        var fam = CpuFamilies.For(SafeName());
        var exe = YcExe(fam);
        if (exe == null) { Stop("y-cruncher isn't bundled"); return false; }
        List<SweepPoint> pts; int cores; string? mode; string pass;
        lock (_gate) { pts = (State.Sweep ??= new()).ToList(); cores = State.Values!.Length; mode = State.Search?.Mode; pass = State.SweepPass ?? "all"; }
        int? next = null;
        if (pass == "all")
        {
            bool rechecking; lock (_gate) rechecking = State.Recheck != null;
            next = rechecking ? null : NextSweep(pts, fam.MinStart, SweepStepSize);
            if (next == null && pts.Count > 0 && pts[0].Ok)
            {
                // the descent is over: no-undervolt and the best few once more in the same run, then pick from the averages
                lock (_gate)
                {
                    if (State.Recheck == null) { State.Recheck = RecheckList(pts, mode); State.Candidates = State.Recheck.ToList(); }
                    if (State.Recheck.Count > 0) { next = State.Recheck[0]; State.Recheck.RemoveAt(0); Save(); }
                }
            }
            if (next == null)
            {
                KillSweepRun();
                List<int> cand; lock (_gate) cand = State.Candidates ?? new();
                if (cand.Count == 0 || !pts.Any(p => p.Value == 0 && p.Ok)) { Stop("the CPU wasn't stable even without an undervolt during the sweep: check the BIOS settings"); return false; }
                // single-core pass: each candidate twice, alternating, in one run
                lock (_gate) { State.SweepPass = "one"; State.OneQueue = cand.Concat(cand).ToList(); Save(); }
                Say($"CPU sweep: all-core pass done; single-core pass over {string.Join(", ", cand)}");
                return true;
            }
        }
        else
        {
            lock (_gate) { if (State.OneQueue is { Count: > 0 } q) { next = q[0]; q.RemoveAt(0); Save(); } }
            if (next == null)
            {
                KillSweepRun();
                var pick = PickSweep(pts, mode);
                if (pick is null) { Stop("the CPU wasn't stable even without an undervolt during the sweep: check the BIOS settings"); return false; }
                if (pick >= 0) { Stop(mode == "efficiency" ? "no undervolt improved efficiency" : "no undervolt was faster than stock: nothing to save (Efficiency mode keeps the lower-power values)"); return false; }
                lock (_gate)
                {
                    State.Picked = pick; State.Values = Enumerable.Repeat(pick.Value, cores).ToArray(); State.Passes = new int[cores]; State.Fails = new int[cores];
                    State.Cursor = 0; State.Stage = "cores"; Save();
                }
                var avp = Averaged(pts); var sp = avp.First(p => p.Value == pick); var st0 = avp.First(p => p.Value == 0);
                Say($"CPU sweep: picked {pick} ({mode ?? "performance"}: {(Composite(sp, st0) - 1) * 100:+0.0;-0.0}% speed{(sp.PowerW is double pw && st0.PowerW is double p0 ? $", {pw - p0:+0;-0} W" : "")}); checking each core alone");
                return true;
            }
        }
        int nv = next.Value;
        var run = SweepRun(exe, pass);
        if (_engineStop) { KillSweepRun(); return false; }
        var since = _now();
        lock (_gate) { State.Testing = new CoreTest { Core = -2, Value = nv, Since = since, Seconds = SweepLineSec * 3 }; Save(); }
        var pt = run == null ? new SweepPoint { Value = nv, Ok = false, Why = "y-cruncher didn't start" } : MeasureLive(fam, run, nv, Enumerable.Repeat(nv, cores).ToArray(), pass == "all", since);
        if (_engineStop) { lock (_gate) { State.Testing = null; Save(); } KillSweepRun(); return false; }
        if (!pt.Ok) { KillSweepRun(); RestoreCo(); }   // a failed run: BIOS values back before the next run starts
        lock (_gate) { (State.Sweep ??= new()).Add(pt); State.Testing = null; Save(); }
        Say(pt.Ok ? $"CPU sweep {nv} ({(pass == "all" ? "all cores" : "one core")}): {((pass == "all" ? pt.AllCore : pt.Single) ?? 0) / 1e9:0.000}{(pt.PowerW is double w ? $", {w:0} W" : "")}" : $"CPU sweep {nv}: {pt.Why}");
        return true;
    }

    /// <summary>
    /// Stock (<paramref name="a"/>) vs tuned (<paramref name="b"/>) measured in the same runs, alternating <paramref name="rounds"/>
    /// times: one all-core run, then one single-core run. Leaves the BIOS values set; the caller applies what it wants.
    /// </summary>
    public CpuBenchPair? CompareCo(int[] a, int[] b, int rounds = 3)
    {
        var fam = CpuFamilies.For(SafeName());
        if (YcExe(fam) is not { } exe || WhyNot(fam) != null) return null;
        lock (_gate) if (State.Phase == "searching" || LoadTesting) return null;
        var A = new List<SweepPoint>(); var B = new List<SweepPoint>();
        _engineStop = false;
        try
        {
            foreach (var kind in new[] { "all", "one" })
            {
                var run = SweepRun(exe, kind);
                if (run == null) return null;
                for (int r = 0; r < rounds; r++)
                {
                    var pa = MeasureLive(fam, run, 0, a, kind == "all", _now()); var pb = MeasureLive(fam, run, 1, b, kind == "all", _now());
                    if (!pa.Ok || !pb.Ok) { Say("CPU compare: " + (pa.Why ?? pb.Why)); return null; }
                    A.Add(pa); B.Add(pb);
                }
                KillSweepRun();
            }
        }
        finally { KillSweepRun(); RestoreCo(); }
        static double? Avg(IEnumerable<double?> v) { var l = v.Where(x => x is > 0).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Average(); }
        CpuBench Bench(List<SweepPoint> l) => new(_now(), Avg(l.Select(p => p.AllCore)), Avg(l.Select(p => p.Single)), Avg(l.Select(p => p.ClockMhz)), l.Max(p => p.PeakC), Avg(l.Where(p => p.AllCore != null).Select(p => p.PowerW)), Avg(l.Select(p => p.SingleMhz)));
        return new CpuBenchPair { Stock = Bench(A), Tuned = Bench(B) };
    }

    /// <summary>The BIOS values read before Glint's first change this boot (reads them now if needed), or null.</summary>
    public int[]? BiosCo()
    {
        lock (_gate) if (_coBaseline != null) return _coBaseline.ToArray();
        int cores = Safe(_cores) ?? 0;
        if (LoadBaseline(cores) is { } kept) { lock (_gate) _coBaseline = kept; return kept.ToArray(); }
        if (SmuCliPath is not { } exe) return null;
        var r = SafeTool(exe, new[] { "--get-offsets-terse" });
        var b = r.Exit == 0 ? SmuProbeTools.ParseOffsets(r.Output, cores) : null;
        if (b == null || b.Length != cores || b.Any(v => v is < -60 or > 30)) return null;
        lock (_gate) _coBaseline = b; SaveBaseline(b);
        return b.ToArray();
    }

    bool SoakStep()
    {
        var fam = CpuFamilies.For(SafeName());
        var exe = YcExe(fam);
        if (exe == null) { Stop("y-cruncher isn't bundled"); return false; }
        int[] vals; lock (_gate) vals = State.Values!.ToArray();
        if (WriteCo(vals, "search") is { } err) { Stop("couldn't set Curve Optimizer: " + err); return false; }
        var since = _now();
        lock (_gate) { State.Testing = new CoreTest { Core = -1, Value = vals.Min(), Since = since, Seconds = SoakSeconds }; Save(); }
        var cfg = Path.Combine(CcPath!, "glint-soak.cfg");
        // FFTv4 on every thread: a realistic all-core load (measured ~79 C steady on a 9950X3D with PBO Motherboard); the small
        // in-cache tests (SFTv4) pin a whole CPU at its thermal limit within seconds, so they only run in the per-core checks
        File.WriteAllText(cfg, YcConfigAll(Environment.ProcessorCount, 0, 20), new UTF8Encoding(false));
        var (why, broken) = RunStress(exe, cfg, SoakSeconds, since, fam.AbortC - 3, out bool hot, out double ran);
        if (broken != null) { Stop(broken); return false; }
        if (_engineStop) { lock (_gate) { State.Testing = null; Save(); } return false; }
        if (why == null && hot && ran < SoakSeconds * 0.6)
        {
            int hotTries; lock (_gate) { State.HotSoaks++; hotTries = State.HotSoaks; State.Testing = null; Save(); }
            if (hotTries >= 2) { Stop($"the all-core soak reaches {fam.AbortC - 3:0} °C after {ran:0} s: cooling limits it (no errors so far). Better cooling or lower PBO limits, then tune again"); return false; }
            Say($"CPU soak: {fam.AbortC - 3:0} °C after {ran:0} s with no errors: cooling down, then soaking again");
            for (int i = 0; i < 120 && !_engineStop && TuningLimits.ValidTemp(_cpuTemp()) is double t0 && t0 > fam.AbortC - 25; i++) Thread.Sleep(Math.Max(1, PollMs * 4));
            return !_engineStop;
        }
        if (why == null && hot) Say($"CPU soak: reached {fam.AbortC - 3:0} °C after {ran:0} s with no errors: counted as passed");
        if (why != null)
        {
            int tries;
            lock (_gate)
            {
                State.SoakTries++; tries = State.SoakTries;
                for (int i = 0; i < State.Values!.Length; i++) State.Values[i] = Math.Min(0, State.Values[i] + SoakBackoff);
                State.Testing = null; Save();
            }
            if (tries >= MaxSoakTries) { Stop($"still unstable with all cores loaded after {MaxSoakTries} back-offs ({why})"); return false; }
            Say($"CPU soak failed ({why}): every core loosened by {SoakBackoff}, soaking again");
            return true;
        }
        lock (_gate) { State.Stage = "complete"; State.Testing = null; Save(); }
        Say("CPU soak passed: finishing");
        try { Finish(); } catch (Exception e) { Say("CPU: couldn't finish: " + e.Message); }
        return false;
    }

    /// <summary>
    /// All-core load test for the PBO / boost-override recommendation (nothing is changed): y-cruncher on every logical CPU for
    /// <paramref name="seconds"/> (20-90), sampling temperature, package power and clocks once a second. Stops early at the family's
    /// temperature limit (that itself is the answer: thermal-limited). Refused while a search runs or the CPU is already warm.
    /// </summary>
    public object LoadTest(int seconds) => LoadTest(seconds, false);

    /// <param name="verify">true = measuring the applied undervolt after a search (no new PBO advice is raised from it).</param>
    /// <param name="profile">The profile the result belongs to (default: the tune's / active one).</param>
    public object LoadTest(int seconds, bool verify, string? profile = null)
    {
        var fam = CpuFamilies.For(SafeName());
        if (WhyNot(fam) is { } why) throw new InvalidOperationException(why);
        seconds = Math.Clamp(seconds, 20, 90);
        double runFor = seconds * LoadTimeScale;
        lock (_gate)
        {
            if (State.Phase == "searching" || _engine is { IsAlive: true }) throw new InvalidOperationException("the per-core search is running: the load test would disturb it");
            if (LoadTesting) throw new InvalidOperationException("a load test is already running");
            LoadTesting = true; _loadStop = false;
        }
        try
        {
            LastBench = null;
            var t0 = TuningLimits.ValidTemp(_cpuTemp());
            if (t0 is not double idle) throw new InvalidOperationException("CPU temperature isn't readable: refusing to load the CPU unwatched");
            if (idle >= fam.AbortC - 15) throw new InvalidOperationException($"the CPU is already at {idle:0} °C: let it cool down below {fam.AbortC - 15:0} °C first");
            var exe = YcExe(fam)!;
            int logical = Environment.ProcessorCount;
            var cfgFile = Path.Combine(CcPath!, "glint-load.cfg");
            File.WriteAllText(cfgFile, YcConfigAll(logical, seconds), new UTF8Encoding(false));
            var temps = new List<double>(); var power = new List<double>(); var avg = new List<double>(); var max = new List<double>();
            string? stopped = null, allOut = "";
            PmLimits? pmPeak = null;   // the highest share of each limit seen under load
            var sw = Stopwatch.StartNew();
            IStressRun? run = null;
            try
            {
                run = _proc.StartStress(exe, new[] { "skip-warnings", "pause:-2", "config", cfgFile }, Path.GetDirectoryName(exe)!);
                lock (_gate) _run = run;
                while (sw.Elapsed.TotalSeconds < runFor && !_loadStop)
                {
                    Thread.Sleep(SampleMs);
                    if (_loadStop) break;
                    if (run.Exited) { stopped = $"y-cruncher stopped with an error (exit {run.ExitCode?.ToString() ?? "?"}): the CPU isn't stable at its current settings under all-core load"; break; }
                    var t = TuningLimits.ValidTemp(_cpuTemp());
                    if (t is double tc) { temps.Add(tc); if (tc >= fam.AbortC) { stopped = $"reached the {fam.AbortC:0} °C limit after {sw.Elapsed.TotalSeconds:0} s"; break; } }
                    double? lpw = null, la = null, lm = null;
                    try { if (LoadProbe?.Invoke() is var (pw, a, m, _)) { lpw = pw; la = a; lm = m; if (pw is > 0) power.Add(pw.Value); if (a is > 0) avg.Add(a.Value); if (m is > 0) max.Add(m.Value); } } catch { }
                    _loadLive = new LoadLive("all", sw.Elapsed.TotalSeconds, runFor, t, lpw, la, lm, fam.AbortC);
                    if (SafePm() is { Valid: true } pmS && sw.Elapsed.TotalSeconds > runFor / 3)
                        pmPeak = pmPeak == null ? pmS : pmPeak with { PptW = Math.Max(pmPeak.PptW ?? 0, pmS.PptW ?? 0), TdcA = Math.Max(pmPeak.TdcA ?? 0, pmS.TdcA ?? 0), EdcA = pmS.EdcA is double e ? Math.Max(pmPeak.EdcA ?? 0, e) : pmPeak.EdcA };
                }
                allOut = run.Output;
            }
            finally { try { run?.Kill(); } catch { } try { run?.Dispose(); } catch { } lock (_gate) _run = null; }
            // a short single-core run: the light-load / gaming side (boost clock on one core)
            double? single = null;
            if (stopped == null && !_loadStop)
            {
                var one = Path.Combine(CcPath!, "glint-load1.cfg");
                File.WriteAllText(one, YcConfigOne(0), new UTF8Encoding(false));
                IStressRun? r1 = null; var sw1 = Stopwatch.StartNew();
                try
                {
                    r1 = _proc.StartStress(exe, new[] { "skip-warnings", "pause:-2", "config", one }, Path.GetDirectoryName(exe)!);
                    lock (_gate) _run = r1;
                    while (sw1.Elapsed.TotalSeconds < 16 * LoadTimeScale && !_loadStop && !r1.Exited)
                    {
                        Thread.Sleep(SampleMs);
                        double? lpw = null, la = null, lm = null;
                        try { if (LoadProbe?.Invoke() is var (pw, a, m, _)) { lpw = pw; la = a; lm = m; } } catch { }
                        _loadLive = new LoadLive("one", sw1.Elapsed.TotalSeconds, 16 * LoadTimeScale, TuningLimits.ValidTemp(_cpuTemp()), lpw, la, lm, fam.AbortC);
                    }
                    // a kill from Stop exits with -1 too: only an exit on its own is an error
                    if (!_loadStop && r1.Exited && r1.ExitCode is int ec && ec != 0) stopped = $"y-cruncher stopped with an error on one core (exit {ec})";
                    single = StressScore(r1.Output);
                }
                finally { try { r1?.Kill(); } catch { } try { r1?.Dispose(); } catch { } lock (_gate) _run = null; }
            }
            double? allCore = StressScore(allOut);
            if (_loadStop) stopped = "stopped by request";
            static double? Tail(List<double> l) => l.Count == 0 ? null : Math.Round(l.Skip(l.Count / 2).Average(), 1);
            double? peak = temps.Count > 0 ? temps.Max() : null;
            var res = new
            {
                seconds = Math.Round(sw.Elapsed.TotalSeconds), threads = logical, idleC = idle, peakC = peak, sustainedC = Tail(temps), limitC = fam.AbortC,
                headroomC = peak is double p ? Math.Round(fam.AbortC - p, 1) : (double?)null,
                packagePowerW = Tail(power), peakPowerW = power.Count > 0 ? Math.Round(power.Max(), 1) : (double?)null,
                avgClockMhz = Tail(avg), maxClockMhz = max.Count > 0 ? Math.Round(max.Max()) : (double?)null, allCoreScore = allCore, singleScore = single,
                stopped, family = fam.Id, boostOverrideAllowedMhz = fam.BoostOverrideMhz,
                limits = pmPeak == null ? null : new { pptW = pmPeak.PptW, pptLimitW = pmPeak.PptLimitW, pptPct = pmPeak.PptPct, tdcA = pmPeak.TdcA, tdcLimitA = pmPeak.TdcLimitA, tdcPct = pmPeak.TdcPct, edcA = pmPeak.EdcA, edcLimitA = pmPeak.EdcLimitA, edcPct = pmPeak.EdcPct, raisedInBios = pmPeak.Raised },
                read = "Temperature near the limit = thermal-limited: raising PBO limits won't help (better cooling or a deeper undervolt will). Plenty of headroom (10 °C+) with power flat at the stock limit = power-limited: PBO limits at the motherboard's ('Motherboard') and a boost override can help. Glint can't set these: they are BIOS settings to recommend.",
            };
            Say($"CPU load test: peak {peak:0.#} °C, {Tail(power):0} W, {Tail(avg):0} MHz avg{(stopped != null ? " (" + stopped + ")" : "")}");
            if (_loadStop && stopped == "stopped by request") return res;
            var advice = PboAdvisor.Advise(_now(), peak, fam.AbortC, Tail(power), Tail(avg), stopped, fam.BoostOverrideMhz, Goal?.Kind, pmPeak);
            var bench = new CpuBench(_now(), allCore, single, Tail(avg), peak, Tail(power));
            LastBench = stopped == null ? bench : null;
            try { LoadTested?.Invoke(profile ?? SafeTarget(), advice, bench, verify); } catch { }
            return new { result = res, advice, bench };
        }
        finally { _loadLive = null; lock (_gate) LoadTesting = false; }
    }

    public void StopLoadTest() { _loadStop = true; try { _run?.Kill(); } catch { } }

    Thread? _loadThread;
    object? _loadResult; string? _loadError; DateTime? _loadAt;

    /// <summary>Start the load test as a job (it takes about a minute; tool calls time out long before that). Refusals come back at
    /// once; poll <see cref="WaitLoad"/> (tuning.wait) or read tuning.cpu.status for the result.</summary>
    public object StartLoadTest(int seconds)
    {
        var fam = CpuFamilies.For(SafeName());
        if (WhyNot(fam) is { } why) throw new InvalidOperationException(why);
        lock (_gate)
        {
            if (State.Phase == "searching" || _engine is { IsAlive: true }) throw new InvalidOperationException("the per-core search is running: the load test would disturb it");
            if (LoadTesting || _loadThread is { IsAlive: true }) throw new InvalidOperationException("a load test is already running: call tuning.wait until busy is false");
            var t = TuningLimits.ValidTemp(_cpuTemp());
            if (t is not double idle) throw new InvalidOperationException("CPU temperature isn't readable: refusing to load the CPU unwatched");
            if (idle >= fam.AbortC - 15) throw new InvalidOperationException($"the CPU is already at {idle:0} °C: let it cool down below {fam.AbortC - 15:0} °C first");
            _loadResult = null; _loadError = null; _loadAt = null;
            _loadThread = new Thread(() =>
            {
                object? r = null; string? err = null;
                try { r = LoadTest(seconds); } catch (Exception e) { err = e.Message; }
                lock (_gate) { _loadResult = r; _loadError = err; _loadAt = _now(); }
            }) { IsBackground = true, Name = "Glint CPU load test" };
            _loadThread.Start();
        }
        return new { started = true, seconds = Math.Clamp(seconds, 20, 90), takes = $"about {Math.Clamp(seconds, 20, 90) + 16} s (all cores, then one core). Call tuning.wait until busy is false, then read the result (also in tuning.cpu.status loadTest)." };
    }

    public bool LoadBusy { get { lock (_gate) return LoadTesting || _loadThread is { IsAlive: true }; } }

    /// <summary>Wait up to <paramref name="seconds"/> (max 25) for a running load test; the tuning.wait shape.</summary>
    public object WaitLoad(double seconds)
    {
        Thread? t; lock (_gate) t = _loadThread;
        t?.Join(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 25)));
        return new { busy = LoadBusy, phase = LoadBusy ? "cpu load test" : "idle", progress = LoadBusy ? "CPU load test running (all cores, then one core)" : null, result = LoadBusy ? null : LoadInfo() };
    }

    object? LoadInfo() { lock (_gate) return _loadAt == null ? null : new { finishedAt = _loadAt, result = _loadResult, error = _loadError }; }

    public static string YcConfigAll(int logical, int seconds, int perTest = 10, bool mix = false) => string.Join("\r\n", new[]
    {
        "{", "    Action : \"StressTest\"", "    StressTest : {", "        AllocateLocally : \"true\"",
        $"        LogicalCores : [{string.Join(" ", Enumerable.Range(0, Math.Max(1, logical)))}]", "        TotalMemory : 268435456",
        $"        SecondsPerTest : {perTest}", "        SecondsTotal : 0", "        StopOnError : \"true\"", "        Tests : [" }
        .Concat(mix ? new[] { "            \"SFTv4\"", "            \"FFTv4\"", "            \"N63\"" } : new[] { "            \"FFTv4\"" })
        .Concat(new[] { "        ]", "    }", "}" }));

    /// <summary>One logical CPU, the same test, short runs (several speed readings in ~16 s).</summary>
    public static string YcConfigOne(int logicalCpu) => string.Join("\r\n", new[]
    {
        "{", "    Action : \"StressTest\"", "    StressTest : {", "        AllocateLocally : \"true\"", $"        LogicalCores : [{logicalCpu}]", "        TotalMemory : 13418572",
        "        SecondsPerTest : 4", "        SecondsTotal : 0", "        StopOnError : \"true\"", "        Tests : [", "            \"FFTv4\"", "        ]", "    }", "}",
    });

    static readonly Regex StressErr = new(@"\b(error|errors detected|failed|failure|mismatch|exception|too large)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    /// <summary>The first line of y-cruncher output that reports an error (ignoring "0 errors"-style counters), or null.</summary>
    public static string? StressErrorText(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        foreach (var raw in output.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.Length > 400) continue;
            if (Regex.IsMatch(l, @"\b0\s+errors?\b|errors?\s*:\s*0\b|stop on error|passed", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) continue;   // y-cruncher's own header / pass lines
            if (StressErr.IsMatch(l)) return l.Length > 160 ? l[..160] : l;
        }
        return null;
    }

    /// <summary>y-cruncher's stress config: the small in-cache tests on ONE logical CPU, stop at the first error.</summary>
    public static string YcConfig(int logicalCpu, int perTest = 60) => string.Join("\r\n", new[]
    {
        "{", "    Action : \"StressTest\"", "    StressTest : {", "        AllocateLocally : \"true\"",
        $"        LogicalCores : [{logicalCpu}]", "        TotalMemory : 13418572", $"        SecondsPerTest : {perTest}", "        SecondsTotal : 0",
        "        StopOnError : \"true\"", "        Tests : [", "            \"SFTv4\"", "            \"FFTv4\"", "            \"N63\"", "        ]", "    }", "}",
    });

    /// <summary>Live numbers for the dashboard and the monitor while a core test runs.</summary>
    public object? Live()
    {
        lock (_gate)
        {
            if (State.Phase != "searching" || State.Values == null || State.Passes == null) return null;
            var t = State.Testing;
            int need = Need(State);
            bool sweeping = State.Stage == "sweep", soaking = State.Stage == "soak" || State.Stage == "complete";
            int done = sweeping ? 0 : soaking ? State.Values.Length : Enumerable.Range(0, State.Values.Length).Count(i => Done(State, i, need));
            bool onCore = t != null && t.Core >= 0 && t.Core < State.Values.Length;
            return new
            {
                core = onCore ? t!.Core : (int?)null, value = t?.Value, elapsed = t == null ? (double?)null : Math.Round((_now() - t.Since).TotalSeconds, 1), seconds = t?.Seconds,
                pass = onCore ? State.Passes[t!.Core] + 1 : (int?)null, passesNeeded = need, coresDone = done, coresTotal = State.Values.Length,
                phase = State.Stage, picked = State.Picked, soakTries = State.SoakTries, sweepPass = State.SweepPass ?? "all",
                sweep = SweepChips(State.Sweep),
                running = _engine is { IsAlive: true }, tempC = TuningLimits.ValidTemp(_cpuTemp()),
                powerW = Probe()?.PowerW, maxMhz = Probe()?.MaxMhz, abortC = CpuFamilies.Describe(State.Search?.Family ?? "").AbortC,
                values = State.Values.ToArray(), passes = State.Passes.ToArray(),
                stage = t == null ? null : sweeping ? "sweep" : soaking ? "soak" : State.Stage == "cores" ? "core check" : !onCore ? null : State.Confirm?[t.Core] == true ? "confirming" : State.Good?[t.Core] == null && State.Bad?[t.Core] != null ? "loosening" : State.Bad?[t.Core] == null ? "stepping deeper" : "narrowing",
                deepest = !onCore ? null : State.Good?[t!.Core], failedAt = !onCore ? null : State.Bad?[t!.Core],
                done = Enumerable.Range(0, State.Values.Length).Select(i => !sweeping && (soaking || Done(State, i, need))).ToArray(),
            };
        }
    }

    (double? PowerW, double? AvgMhz, double? MaxMhz, double? LoadPct)? _probe; DateTime _probeAt;
    (double? PowerW, double? AvgMhz, double? MaxMhz, double? LoadPct)? Probe()
    {
        if (LoadProbe == null) return null;
        if (_probe != null && (DateTime.UtcNow - _probeAt).TotalMilliseconds < 800) return _probe;
        try { _probe = LoadProbe(); _probeAt = DateTime.UtcNow; } catch { _probe = null; }
        return _probe;
    }

    // ------------------------------------------------------------------ watchdog

    void StartWatchdog(int delaySec)
    {
        lock (_gate)
        {
            if (_timer != null) return;
            _unreadableSince = null;
            _timer = new System.Threading.Timer(_ => { try { Tick(); } catch { } }, null, TimeSpan.FromSeconds(delaySec + 1), TimeSpan.FromSeconds(1));
        }
    }

    void StopWatchdog()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; _unreadableSince = null; }
    }

    /// <summary>One watchdog check (the timer calls it once a second; public for tests). True = it just stopped the search.
    /// Trips at the family's temperature limit, or when the sensor has been unreadable for <see cref="UnreadableTripSeconds"/> s.</summary>
    public bool Tick()
    {
        CpuState st; lock (_gate) st = State;
        if (st.Phase != "searching") return false;
        var fam = CpuFamilies.For(SafeName());
        double abort = fam.Supported ? fam.AbortC : 85;
        var t = TuningLimits.ValidTemp(_cpuTemp());
        var now = _now();
        string? trip = null;
        if (t is double c)
        {
            _unreadableSince = null;
            _peakC = Math.Max(_peakC, c);
            if (c >= abort) trip = $"CPU {c:0.#} °C reached the {abort:0} °C limit";
        }
        else
        {
            _unreadableSince ??= now;
            if ((now - _unreadableSince.Value).TotalSeconds >= UnreadableTripSeconds) trip = $"CPU temperature unreadable for {UnreadableTripSeconds} s (is Glint's sensor working?)";
        }
        if (trip == null) return false;
        Say("CPU EMERGENCY STOP: " + trip);
        Stop("watchdog: " + trip);
        return true;
    }


    // ------------------------------------------------------------------ helpers

    string? SafeName() { try { return _cpuName(); } catch { return null; } }
    static T? Safe<T>(Func<T> f) where T : struct { try { return f(); } catch { return null; } }
    string? BoardName()
    {
        var (m, mo) = _board();
        var s = $"{m} {mo}".Trim();
        return s.Length == 0 ? null : s;
    }

    void Say(string msg) => Log?.Invoke(msg);

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var tmp = _statePath + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(f, State, GlintConfig.Json);
            f.Flush(true);
        }
        File.Move(tmp, _statePath, true);
    }

    static CpuState Load(string path)
    {
        try { return JsonSerializer.Deserialize<CpuState>(File.ReadAllText(path), GlintConfig.Json) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Glint closing: stop the timer and stop the stress test (nobody would be watching it). The search stays "searching":
    /// CoreCycler's own resume task restarts it at the next logon and <see cref="OnStartup"/> brings the watchdog back first.</summary>
    public void Dispose()
    {
        bool searching;
        lock (_gate) searching = State.Phase == "searching";
        StopWatchdog();
        StopLoadTest();
        if (searching) StopEngine();   // the search stays "searching": the next start resumes it
    }
}

/// <summary>The real machine: registry, event log, processes. Never reached from tests.</summary>
public sealed class WindowsCpuProcessRunner : ICpuProcessRunner
{
    public void Launch(string dir)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
        psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(Path.Combine(dir, "Run CoreCycler.bat"));
        psi.Environment["DOTNET_ROLL_FORWARD"] = "Major"; // CoreCycler's ryzen-smu-cli targets .NET 8: run on a newer runtime if 8 is missing
        using var p = Process.Start(psi);
    }

    static string Ps(string script, int timeoutMs)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException("PowerShell didn't finish"); }
        return o.Result;
    }

    public void RunPowerShell(string script) => Ps(script, 30000);

    public bool IsCoreCyclerRunning()
    {
        var o = Ps("(Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -match 'script-corecycler' } | Measure-Object).Count", 20000).Trim();
        return o != "" && o != "0";
    }

    public void DeleteTask(string name)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "/delete", "/tn", name, "/f" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15000);
    }

    public (int Exit, string Output) RunTool(string exe, IReadOnlyList<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_ROLL_FORWARD"] = "Major"; // the tool targets .NET 8; let it run on a newer runtime when 8 isn't installed
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, ""); }
        return (p.ExitCode, (o.Result + "\n" + e.Result).Trim());
    }

    public bool? MemoryIntegrity()
    {
        var v = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", null);
        return v is int i ? i != 0 : false;
    }

    public IStressRun StartStress(string exe, IReadOnlyList<string> args, string workDir) => new ProcessStressRun(exe, args, workDir);

    // Every stress process goes into one job object that kills its processes when Glint's handle closes (exit, crash, restart):
    // an all-core y-cruncher can never be left running unwatched.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimit info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [StructLayout(LayoutKind.Sequential)] struct JobBasicLimit { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)] struct JobExtendedLimit { public JobBasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    static readonly Lazy<IntPtr> KillJob = new(() =>
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JobExtendedLimit { Basic = new JobBasicLimit { LimitFlags = 0x2000 } };   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        SetInformationJobObject(job, 9, ref info, Marshal.SizeOf<JobExtendedLimit>());          // JobObjectExtendedLimitInformation
        return job;
    });

    sealed class ProcessStressRun : IStressRun
    {
        readonly Process _p;
        readonly StringBuilder _out = new();
        readonly List<double> _speeds = new();
        string? _err;
        public ProcessStressRun(string exe, IReadOnlyList<string> args, string workDir)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = workDir };
            foreach (var a in args) psi.ArgumentList.Add(a);
            _p = Process.Start(psi)!;
            try { if (KillJob.Value != IntPtr.Zero) AssignProcessToJobObject(KillJob.Value, _p.Handle); } catch { }
            void Add(string? l)
            {
                if (l == null) return;
                lock (_out)
                {
                    _err ??= CpuTuning.StressErrorText(l);
                    if (l.Contains("Test Speed", StringComparison.Ordinal)) _speeds.AddRange(CpuTuning.SpeedList(l));
                    _out.AppendLine(l);
                    if (_out.Length > 64_000) _out.Remove(0, _out.Length - 48_000);   // keep the tail
                }
            }
            _p.OutputDataReceived += (_, e) => Add(e.Data); _p.ErrorDataReceived += (_, e) => Add(e.Data);
            _p.BeginOutputReadLine(); _p.BeginErrorReadLine();
        }
        public bool Exited { get { try { return _p.HasExited; } catch { return true; } } }
        public int? ExitCode { get { try { return _p.HasExited ? _p.ExitCode : null; } catch { return null; } } }
        public string Output { get { lock (_out) return _out.ToString(); } }
        public string? ErrorLine { get { lock (_out) return _err; } }
        public IReadOnlyList<double> Speeds { get { lock (_out) return _speeds.ToArray(); } }
        public void Kill() { try { if (!_p.HasExited) { _p.Kill(true); _p.WaitForExit(5000); } } catch { } }
        public void Dispose() { Kill(); _p.Dispose(); }
    }

    public bool PawnIoInstalled()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return k != null;
    }
}

/// <summary>Reads about the CPU and board from the registry, the event log and the Win32 API (no WMI, nothing loaded until asked).</summary>
public static class CpuHardware
{
    const string Cpu0 = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public static string? CpuName() => Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + Cpu0, "ProcessorNameString", null) as string;
    public static byte[]? MicrocodeRaw() => Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + Cpu0, "Update Revision", null) as byte[];

    public static (string? Maker, string? Model) Board()
    {
        const string k = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
        return (Microsoft.Win32.Registry.GetValue(k, "BaseBoardManufacturer", null) as string, Microsoft.Win32.Registry.GetValue(k, "BaseBoardProduct", null) as string);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

    /// <summary>Physical core count (RelationProcessorCore records), falling back to half the logical processors.</summary>
    public static int PhysicalCores()
    {
        try
        {
            int len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len > 0)
            {
                var buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (GetLogicalProcessorInformationEx(0, buf, ref len))
                    {
                        int n = 0;
                        for (int off = 0; off + 8 <= len;)
                        {
                            int size = Marshal.ReadInt32(buf, off + 4);
                            if (size <= 0) break;
                            if (Marshal.ReadInt32(buf, off) == 0) n++;
                            off += size;
                        }
                        if (n > 0) return n;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch { }
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    /// <summary>WHEA hardware error events logged since <paramref name="since"/> (a failing core shows up here before it crashes).</summary>
    public static int WheaSince(DateTime since)
    {
        var ms = Math.Max(1000, (long)(DateTime.Now - since).TotalMilliseconds + 5000);
        var q = $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        int n = 0;
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, q));
            for (EventRecord? e; n < 1000 && (e = reader.ReadEvent()) != null;) { using (e) n++; }
        }
        catch (EventLogException) { }
        return n;
    }
}
