// Ported from Headroom (MIT, Headroom contributors)
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Glint.Tuning;

/// <summary>One Windows event reduced to what crash classification needs (parsed from <c>EventRecord.ToXml()</c>).</summary>
public sealed record CrashEvent(string Provider, int Id, long RecordId, string Channel, DateTime Time,
    IReadOnlyDictionary<string, string> Data, IReadOnlyList<string> Texts);

/// <summary>What an event means. Suspect: gpu | cpu | unknown. Soft = a game crashed in the NVIDIA user-mode driver and the driver recovered.</summary>
public sealed record CrashClass(string Kind, string Suspect, string Detail, uint? Bugcheck, int? Core, bool Soft);

/// <summary>
/// Pure crash-event logic: XPath filters, XML parsing, classification, bugcheck and minidump parsing.
/// Everything Windows-specific (the event log itself) lives in <see cref="CrashWatch"/>, so this is testable with fixtures.
/// </summary>
public static class CrashEvents
{
    public const string GpuTdr = "gpu_tdr", GpuAppCrash = "gpu_app_crash", WheaCorrected = "whea_corrected",
        WheaFatal = "whea_fatal", UnexpectedShutdown = "unexpected_shutdown", Bugcheck = "bugcheck";
    public const string ProvKernelPower = "Microsoft-Windows-Kernel-Power", ProvWer = "Microsoft-Windows-WER-SystemErrorReporting",
        ProvWhea = "Microsoft-Windows-WHEA-Logger";

    public static readonly string[] NvModules = ["nvwgf2umx.dll", "nvwgf2um.dll", "nvd3dumx.dll", "nvoglv64.dll", "nvlddmkm.sys"];
    static readonly HashSet<uint> GpuBugchecks = [0x116, 0x117, 0x119, 0x10E], CpuBugchecks = [0x124, 0x101];

    /// <summary>System channel: exactly the providers/IDs we classify (the OS filters, we never poll).</summary>
    public const string SystemXPath =
        "*[System[Provider[@Name='nvlddmkm'] or (Provider[@Name='Display'] and EventID=4101) or " +
        "(Provider[@Name='" + ProvWhea + "'] and (EventID=18 or EventID=19)) or " +
        "(Provider[@Name='" + ProvKernelPower + "'] and EventID=41) or " +
        "(Provider[@Name='" + ProvWer + "'] and EventID=1001)]]";

    /// <summary>Application channel: Application Error 1000 whose data names an NVIDIA driver module.</summary>
    public static readonly string AppXPath =
        "*[System[Provider[@Name='Application Error'] and EventID=1000] and EventData[" +
        string.Join(" or ", NvModules.Select(m => $"Data='{m}'")) + "]]";

    /// <summary>Only the crashes that are logged on the next boot (Kernel-Power 41, bugcheck 1001), newer than <paramref name="ageMs"/>.</summary>
    public static string ScanXPath(long ageMs) =>
        "*[System[((Provider[@Name='" + ProvKernelPower + "'] and EventID=41) or (Provider[@Name='" + ProvWer +
        "'] and EventID=1001)) and TimeCreated[timediff(@SystemTime) <= " + ageMs.ToString(CultureInfo.InvariantCulture) + "]]]";

    public static CrashEvent? Parse(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root == null) return null;
            string prov = "", chan = ""; int id = 0; long rec = 0; var time = DateTime.MinValue;
            var data = new Dictionary<string, string>(); var texts = new List<string>();
            foreach (var el in root.Descendants())
                switch (el.Name.LocalName)
                {
                    case "Provider": prov = (string?)el.Attribute("Name") ?? ""; break;
                    case "EventID": int.TryParse(el.Value.Trim(), out id); break;
                    case "EventRecordID": long.TryParse(el.Value.Trim(), out rec); break;
                    case "Channel": chan = el.Value.Trim(); break;
                    case "TimeCreated": time = ParseTime((string?)el.Attribute("SystemTime")); break;
                    case "Data":
                        var v = el.Value.Trim(); texts.Add(v);
                        if ((string?)el.Attribute("Name") is { Length: > 0 } n) data[n] = v;
                        break;
                }
            return new CrashEvent(prov, id, rec, chan, time, data, texts);
        }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>SystemTime has 9 fractional digits; DateTime takes 7.</summary>
    static DateTime ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
        s = Regex.Replace(s.Trim(), @"(\.\d{7})\d+", "$1");
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MinValue;
    }

    /// <summary>Bugcheck 1001 text is "The bugcheck was: 0x00000116 (...)"; Kernel-Power 41 has a decimal BugcheckCode (0 = none).</summary>
    public static uint? ParseBugcheck(CrashEvent ev)
    {
        foreach (var k in new[] { "BugcheckCode", "BugCheckCode" })
            if (ev.Data.TryGetValue(k, out var bc) && uint.TryParse(bc, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n != 0) return n;
        if (ev.Provider == ProvKernelPower) return null; // its other parameters are not codes
        foreach (var t in ev.Texts)
        {
            var m = Regex.Match(t, @"0x([0-9a-fA-F]{8})\b");
            if (!m.Success) m = Regex.Match(t, @"bugcheck was:?\s*0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
            if (m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) return code;
        }
        return null;
    }

    public static string BugcheckSuspect(uint? code) => code is { } c ? GpuBugchecks.Contains(c) ? "gpu" : CpuBugchecks.Contains(c) ? "cpu" : "unknown" : "unknown";
    public static string Hex(uint code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Logical-processor (APIC) id to physical core, for the WHEA "loosen this core" hint.</summary>
    public static int ApicToCore(int apic, int threadsPerCore = 2) => apic / Math.Max(1, threadsPerCore);

    static int? ApicId(CrashEvent ev)
    {
        foreach (var (k, v) in ev.Data)
            if (k.Contains("apic", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var a)) return a;
        foreach (var t in ev.Texts)
            if (Regex.Match(t, @"APIC ID:?\s*(\d+)", RegexOptions.IgnoreCase) is { Success: true } m) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>Null = not an event we care about.</summary>
    public static CrashClass? Classify(CrashEvent ev, int threadsPerCore = 2)
    {
        string p = ev.Provider; int id = ev.Id;
        if (p == "nvlddmkm" || (p == "Display" && id == 4101))
            return new(GpuTdr, "gpu", $"NVIDIA driver reset ({p} {id})", null, null, false);
        if (p == "Application Error" && id == 1000)
        {
            var mod = NvModules.FirstOrDefault(m => ev.Texts.Any(t => t.Contains(m, StringComparison.OrdinalIgnoreCase)));
            if (mod == null) return null;
            return new(GpuAppCrash, "gpu", $"{(ev.Texts.Count > 0 ? ev.Texts[0] : "?")} crashed in {mod}", null, null, true);
        }
        if (p == ProvWhea && id is 18 or 19)
        {
            var apic = ApicId(ev); int? core = apic is { } a ? ApicToCore(a, threadsPerCore) : null;
            string what = id == 19 ? "corrected" : "fatal";
            return new(id == 19 ? WheaCorrected : WheaFatal, "cpu", $"WHEA {what} hardware error" + (core is { } c ? $" on core {c} (APIC {apic})" : ""), null, core, false);
        }
        if (p == ProvKernelPower && id == 41)
        {
            var code = ParseBugcheck(ev);
            return new(UnexpectedShutdown, BugcheckSuspect(code), "Unexpected shutdown (power loss, freeze or crash)", code, null, false);
        }
        if (p == ProvWer && id == 1001)
        {
            var code = ParseBugcheck(ev);
            return new(Bugcheck, BugcheckSuspect(code), "Blue screen " + (code is { } c ? Hex(c) : "(unknown code)"), code, null, false);
        }
        return null;
    }

    /// <summary>Kernel dump header: "PAGEDU64" at 0, BugCheckCode (u32) at 0x38, four u64 parameters at 0x40.</summary>
    public static (uint Code, ulong[] Params)? ParseMinidumpHeader(ReadOnlySpan<byte> h)
    {
        if (h.Length < 0x60 || !h[..8].SequenceEqual("PAGEDU64"u8)) return null;
        var ps = new ulong[4];
        for (int i = 0; i < 4; i++) ps[i] = BitConverter.ToUInt64(h.Slice(0x40 + 8 * i, 8));
        return (BitConverter.ToUInt32(h.Slice(0x38, 4)), ps);
    }

    public static string? NewestMinidump(string dir)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static (uint Code, ulong[] Params)? ReadMinidump(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var h = new byte[0x60]; int n = f.Read(h, 0, h.Length);
            return ParseMinidumpHeader(h.AsSpan(0, n));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
