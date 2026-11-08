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
        "amd-zen5-x3d" => new(id, "AMD Ryzen 9000X3D (Zen 5 + 3D V-Cache)", "amd", true, null, -50, -30, 90, 0, 85,
            "Don't raise PPT/TDC/EDC: the cache makes games barely clock-sensitive. Ryzen 9000 often freezes instead of rebooting on a bad value: press reset, the search resumes by itself."),
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

    /// <summary>The settings to type into the BIOS for this board maker and CPU family; <paramref name="co"/> null = still searching (zeros).</summary>
    public static string BiosSheet(string? boardName, string vendor, CpuFamily fam, IReadOnlyList<int>? co, int coresIfUnknown = 8)
    {
        var b = Boards.GetValueOrDefault(vendor) ?? Boards["other"];
        var rows = new List<string>
        {
            $"{(string.IsNullOrWhiteSpace(boardName) ? "Your motherboard" : boardName)} - enter these in the BIOS ({b.BiosKey}):",
            "  " + b.PboPath, "",
            "  Precision Boost Overdrive ........ Advanced",
            "  PBO Limits ....................... Auto   (do NOT raise)",
        };
        if (fam.BoostOverrideMhz > 0)
        {
            rows.Add("  CPU Boost Clock Override ......... Enabled (Positive)");
            rows.Add($"  Max CPU Boost Clock Override(+) .. {fam.BoostOverrideMhz}");
        }
        if (fam.PlatformLimitC > 0) rows.Add($"  {b.ThermalName} {new string('.', Math.Max(2, 34 - b.ThermalName.Length))} {fam.PlatformLimitC} C");
        rows.Add("  Curve Optimizer .................. Per Core");
        if (co == null) rows.Add("  Curve Optimizer, every core ...... Negative 0  (the search finds these)");
        else
            for (int i = 0; i < co.Count; i++)
                rows.Add($"  Core {i} Curve Optimizer {new string('.', i < 10 ? 11 : 10)} Negative {Math.Abs(co[i])}");
        rows.Add("");
        rows.Add("  Save and exit (usually F10). Then check it is stable under a long all-core load before relying on it;");
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
}

public sealed class CpuSearchInfo
{
    public int StartValue { get; set; }
    public DateTime Since { get; set; }
    public string Dir { get; set; } = "";
    public int Cores { get; set; }
    public string Family { get; set; } = "";
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
}

/// <summary>
/// AI-driven CPU tuning for Ryzen 5000/7000/9000: the per-core Curve Optimizer search runs inside CoreCycler (bundled, y-cruncher
/// as the stress test); Glint writes its config, starts it, watches CPU temperature while it runs, and at the end turns what it
/// found into a BIOS sheet (Curve Optimizer can't be set permanently from Windows). Glint never changes a clock or voltage itself.
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
    public CpuState State { get; private set; }
    public event Action<string>? Log;

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
    string? CcPath => !string.IsNullOrEmpty(_ccDir) && File.Exists(Path.Combine(_ccDir, "script-corecycler.ps1")) ? _ccDir : null;
    public bool WatchdogRunning { get { lock (_gate) return _timer != null; } }

    // ------------------------------------------------------------------ status

    /// <summary>Why a search can't start here (null = it can).</summary>
    public string? WhyNot(CpuFamily fam)
    {
        if (!T.Enabled) return "tuning is off (Settings → Tuning)";
        if (!fam.Supported) return fam.WhyNot ?? "this CPU isn't supported";
        if (CcPath == null) return "CoreCycler isn't bundled in this build";
        return null;
    }

    public object Status()
    {
        var name = SafeName();
        var fam = CpuFamilies.For(name);
        uint? mc = null;
        try { mc = CpuFamilies.ParseMicrocode(_microcode()); } catch { }
        var vendor = CpuSearchTools.BoardVendor(_board().Maker, _board().Model);
        var why = WhyNot(fam);
        CpuState st; lock (_gate) st = State;
        object? search = null;
        if (st.Search is { } s)
        {
            var res = CpuSearchTools.Parse(SafeLines(s), s.StartValue, s.Cores);
            bool running = st.Phase == "searching" && SafeRunning();
            int? whea = (_now() - s.Since).TotalDays < 7 ? Safe(() => _wheaSince(s.Since)) : 0;
            var final = st.Final ?? (res.SummaryFound ? CpuSearchTools.FinalValues(res.Values) : null);
            search = new
            {
                startValue = s.StartValue, since = s.Since, running, summaryFound = res.SummaryFound, cores = s.Cores,
                values = res.Values, adjustments = res.Adjustments, failedCores = res.FailedCores.ToDictionary(k => k.Key.ToString(), k => k.Value),
                wheaSinceStart = whea, peakC = _peakC > 0 ? _peakC : (double?)null,
                found = st.Found, final, margin = CpuSearchTools.Margin,
                biosSheet = final == null ? null : CpuSearchTools.BiosSheet(BoardName(), vendor, fam, final),
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
                expect = fam.Supported ? "the search takes several hours (each core is stress-tested alone, errors raise that core's value by 1); it resumes by itself after a freeze or reboot" : null,
            },
            coreCycler = new { bundled = CcPath != null, path = CcPath },
            board = new { name = BoardName(), vendor },
            search,
        };
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
        }
        if (SafeRunning()) throw new InvalidOperationException("CoreCycler is already running on this PC: close it first");
        // the watchdog is the safety net: don't start a multi-hour stress test it couldn't watch
        var t = TuningLimits.ValidTemp(_cpuTemp());
        if (t is not double now0) throw new InvalidOperationException("CPU temperature isn't readable (is Glint running as administrator?): refusing to stress-test the CPU unwatched");
        if (t >= fam.AbortC - 10) throw new InvalidOperationException($"the CPU is already at {t:0} °C: let it cool down below {fam.AbortC - 10:0} °C first");
        var dir = CcPath!;
        var cfgPath = Path.Combine(dir, "config.ini");
        if (File.Exists(cfgPath) && !File.Exists(cfgPath + ".orig")) File.Copy(cfgPath, cfgPath + ".orig");
        File.WriteAllText(cfgPath, CpuSearchTools.SearchConfig(startValue), new UTF8Encoding(false));
        int cores = Math.Max(1, Safe(_cores) ?? 8);
        lock (_gate)
        {
            // on disk before CoreCycler runs: a freeze must find the search recorded
            State = new CpuState { Phase = "searching", Search = new CpuSearchInfo { StartValue = startValue, Since = _now(), Dir = dir, Cores = cores, Family = fam.Id } };
            _peakC = now0;
            Save();
        }
        Say($"CPU search started at Curve Optimizer {startValue} on {cores} cores");
        _proc.Launch(dir);
        StartWatchdog(0);
        return new { started = true, startValue, cores, abortC = fam.AbortC, note, takes = "several hours; it resumes by itself after a freeze (press reset) or reboot. Call tuning.cpu.status to follow it, tuning.cpu.finish when summaryFound is true." };
    }

    /// <summary>Stop CoreCycler, compute the final values (found + 3 margin, never positive) and the BIOS sheet.</summary>
    public object Finish(bool force = false)
    {
        CpuState st; lock (_gate) st = State;
        if (st.Search is not { } s) throw new InvalidOperationException("no CPU search has been started");
        var fam = CpuFamilies.Describe(s.Family);
        var vendor = CpuSearchTools.BoardVendor(_board().Maker, _board().Model);
        if (st.Phase == "done" && st.Final != null)
            return new { final = st.Final, found = st.Found, margin = CpuSearchTools.Margin, complete = true, biosSheet = CpuSearchTools.BiosSheet(BoardName(), vendor, fam, st.Final) };
        var res = CpuSearchTools.Parse(SafeLines(s), s.StartValue, s.Cores);
        if (!res.SummaryFound && !force && st.Phase == "searching" && SafeRunning())
            throw new InvalidOperationException("the search hasn't finished yet (summaryFound is false and CoreCycler is still working). Wait for it, or call again with force=true to use the values found so far: cores it hasn't reached keep the start value and are untested");
        StopStress(deleteTask: true, "Finish");
        StopWatchdog();
        var final = CpuSearchTools.FinalValues(res.Values);
        lock (_gate)
        {
            State.Found = res.Values; State.Final = final; State.Phase = "done"; State.StoppedReason = null; State.FinishedAt = _now();
            Save();
        }
        Say("CPU search finished: " + string.Join(" ", final));
        return new
        {
            final, found = res.Values, margin = CpuSearchTools.Margin, complete = res.SummaryFound, failedCores = res.FailedCores.ToDictionary(k => k.Key.ToString(), k => k.Value),
            warning = res.SummaryFound ? null : "the search hadn't printed its final table: cores not reached keep the start value, untested",
            biosSheet = CpuSearchTools.BiosSheet(BoardName(), vendor, fam, final),
        };
    }

    /// <summary>Stop the search and its stress test now (also removes CoreCycler's resume-after-reboot task). Progress is kept: tuning.cpu.finish still works.</summary>
    public object Stop(string? reason = null)
    {
        bool had;
        lock (_gate) had = State.Phase == "searching";
        StopStress(deleteTask: true, reason ?? "Stop");
        StopWatchdog();
        lock (_gate)
        {
            if (State.Phase == "searching") { State.Phase = "stopped"; State.StoppedReason = reason ?? "stopped by request"; Save(); }
        }
        if (had) Say("CPU search stopped" + (reason != null ? ": " + reason : ""));
        return new { stopped = had };
    }

    /// <summary>Called once at startup: a search that was running when Glint (or the PC) went away is still CoreCycler's to resume,
    /// so the watchdog must be back before it does. Delayed a minute so sensors are up (an unreadable sensor trips it).</summary>
    public void OnStartup()
    {
        lock (_gate) if (State.Phase != "searching") return;
        if (!T.Enabled) { Stop("tuning was turned off"); return; }
        StartWatchdog(60);
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
        var fam = CpuFamilies.Describe(st.Search?.Family ?? "");
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

    void StopStress(bool deleteTask, string why)
    {
        try { _proc.RunPowerShell(CpuSearchTools.KillScript(State.Search?.Dir ?? CcPath)); } catch (Exception e) { Say("couldn't stop CoreCycler (" + why + "): " + e.Message); }
        if (deleteTask) try { _proc.DeleteTask(StartupTask); } catch { }
    }

    // ------------------------------------------------------------------ helpers

    string? SafeName() { try { return _cpuName(); } catch { return null; } }
    bool SafeRunning() { try { return _proc.IsCoreCyclerRunning(); } catch { return false; } }
    static T? Safe<T>(Func<T> f) where T : struct { try { return f(); } catch { return null; } }
    IEnumerable<string> SafeLines(CpuSearchInfo s)
    {
        try { return CpuSearchTools.ReadLogs(s.Dir, s.Since).ToList(); } catch { return Array.Empty<string>(); }
    }
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
        if (searching) StopStress(deleteTask: false, "Glint closing");
    }
}

/// <summary>The real machine: registry, event log, processes. Never reached from tests.</summary>
public sealed class WindowsCpuProcessRunner : ICpuProcessRunner
{
    public void Launch(string dir)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
        psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(Path.Combine(dir, "Run CoreCycler.bat"));
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
