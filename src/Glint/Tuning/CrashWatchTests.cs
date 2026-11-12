using System.Diagnostics.Eventing.Reader;
using Glint.Core;

namespace Glint.Tuning;

/// <summary>
/// Offline checks for crash classification and the back-off decisions, on event XML built like Windows' own
/// (<c>EventRecord.ToXml()</c>) and a fake GPU. The only real access is READING the event log to prove the XPath
/// filters are accepted by Windows; no hardware, no config of the user's.
/// </summary>
public static class CrashWatchTests
{
    const string Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
    static long _rec = 100;

    static string Xml(string provider, int id, (string? Name, string Value)[]? data = null, string channel = "System", string time = "2026-11-08T10:00:00.123456789Z", long? rec = null)
    {
        var d = string.Concat((data ?? []).Select(x => x.Name == null ? $"<Data>{x.Value}</Data>" : $"<Data Name=\"{x.Name}\">{x.Value}</Data>"));
        return $"<Event xmlns=\"{Ns}\"><System><Provider Name=\"{provider}\" Guid=\"{{00000000-0000-0000-0000-000000000000}}\"/><EventID>{id}</EventID><Version>0</Version><Level>2</Level>" +
               $"<TimeCreated SystemTime=\"{time}\"/><EventRecordID>{rec ?? ++_rec}</EventRecordID><Channel>{channel}</Channel><Computer>PC</Computer></System><EventData>{d}</EventData></Event>";
    }

    static CrashEvent Ev(string provider, int id, (string? Name, string Value)[]? data = null, string channel = "System", string time = "2026-11-08T10:00:00.123456789Z", long? rec = null)
        => CrashEvents.Parse(Xml(provider, id, data, channel, time, rec))!;

    static string At(int min) => new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc).AddMinutes(min).ToString("yyyy-MM-ddTHH:mm:ss.0000000Z", System.Globalization.CultureInfo.InvariantCulture);

    static string AtSec(double sec) => new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc).AddSeconds(sec).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);

    sealed class FakeGpu : IGpuControl
    {
        public List<string> Calls = [];
        public string Name => "Fake"; public string Driver => "1"; public string Vbios => "1"; public string? SubsystemVendor => null;
        public double VramGiB => 16; public (double Default, double Max) PowerLimitW => (300, 350); public bool IsWorkstation => false;
 public double? PowerLimitNowW => null;
 public void SetPowerLimitW(double watts) { }
 public double? SlowdownC => null;
        public OffsetRange? Range(ClockKind kind) => null;
        public int GetOffset(ClockKind kind) => 0;
        public void SetOffset(ClockKind kind, int mhz) => Calls.Add($"{kind}={mhz}");
        public void LockCoreClocks(int minMhz, int maxMhz) { }
        public void UnlockCoreClocks() { }
        public IReadOnlyList<string> ResetAll() { Calls.Add("reset"); return []; }
        public GpuSample Sample() => new(null, null, 0, 0, 0, 0, 0, []);
        public long? EccCorrected() => null;
        public void Dispose() { }
    }

    /// <summary>A watch with injectable state; the applied offsets follow what the back-off saved, like the real service.</summary>
    sealed class Rig : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "glint-crash-" + Guid.NewGuid().ToString("N"));
        public readonly FakeGpu Gpu = new();
        public readonly TuningConfig Cfg = new();
        public (int Core, int Mem, double Ratio)? Applied = (150, 400, 0.5);
        public List<(int, int)> Saved = []; public int Reverts;
        public CrashWatch Watch;
        public Rig(string? dumps = null)
        {
            Directory.CreateDirectory(Dir);
            Watch = Make(dumps);
        }
        public CrashWatch Make(string? dumps = null) => new(() => Cfg, () => Gpu, () => Applied,
            (c, m) => { Saved.Add((c, m)); Applied = (c, m, Applied?.Ratio ?? 0.5); },
            () => { Reverts++; Applied = null; }, Path.Combine(Dir, "crashes.json"), 2, dumps ?? Path.Combine(Dir, "nodumps"));
        public CrashRecord? Tdr(int min = 0) => Watch.Process(Ev("nvlddmkm", 13, null, "System", At(min)));
        public void Dispose() { Watch.Dispose(); try { Directory.Delete(Dir, true); } catch (IOException) { } }
    }

    static string? XPathError(string channel, string xpath)
    {
        try { using var r = new EventLogReader(new EventLogQuery(channel, PathType.LogName, xpath)); using var e = r.ReadEvent(); return null; }
        catch (EventLogException ex) { return ex.Message; }
        catch (UnauthorizedAccessException ex) { return ex.Message; }
    }

    static bool Balanced(string s)
    {
        int sq = 0, q = 0; bool inStr = false;
        foreach (var ch in s)
        {
            if (ch == '\'') inStr = !inStr;
            if (inStr) continue;
            if (ch == '[') sq++; else if (ch == ']' && --sq < 0) return false;
            if (ch == '(') q++; else if (ch == ')' && --q < 0) return false;
        }
        return sq == 0 && q == 0 && !inStr;
    }

    public static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "") => check("tuning.crash: " + name, ok, detail);

        // ---- parsing and classification
        var p = CrashEvents.Parse(Xml("nvlddmkm", 13, null, "System", "2026-11-08T10:00:00.123456789Z", 5));
        C("event XML parses (provider, id, record, channel, 9-digit time)",
            p is { Provider: "nvlddmkm", Id: 13, RecordId: 5, Channel: "System" } && p.Time == new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567), p?.Time.ToString("o") ?? "null");
        C("garbage XML parses to null", CrashEvents.Parse("garbage") == null);

        var tdr = CrashEvents.Classify(Ev("nvlddmkm", 13));
        var tdr2 = CrashEvents.Classify(Ev("Display", 4101));
        C("gpu_tdr: nvlddmkm and Display 4101", tdr?.Kind == "gpu_tdr" && tdr.Suspect == "gpu" && tdr2?.Kind == "gpu_tdr", $"{tdr?.Kind}/{tdr2?.Kind}");
        C("Display 4102 is ignored", CrashEvents.Classify(Ev("Display", 4102)) == null);

        var app = CrashEvents.Classify(Ev("Application Error", 1000, [(null, "game.exe"), (null, "1.0"), (null, "nvwgf2umx.dll")], "Application"));
        C("gpu_app_crash needs an NVIDIA module and is soft", app is { Kind: "gpu_app_crash", Suspect: "gpu", Soft: true } && app.Detail.Contains("nvwgf2umx.dll"), app?.Detail ?? "null");
        C("app crash in another module is ignored", CrashEvents.Classify(Ev("Application Error", 1000, [(null, "x.exe"), (null, "KERNELBASE.dll")], "Application")) == null);

        var wc = CrashEvents.Classify(Ev("Microsoft-Windows-WHEA-Logger", 19, [("ErrorSource", "0"), ("ApicId", "5")]));
        var wf = CrashEvents.Classify(Ev("Microsoft-Windows-WHEA-Logger", 18, [(null, "Processor APIC ID: 7")]));
        var wn = CrashEvents.Classify(Ev("Microsoft-Windows-WHEA-Logger", 19));
        C("whea_corrected / whea_fatal are cpu suspects with a core", wc is { Kind: "whea_corrected", Suspect: "cpu", Core: 2 } && wf is { Kind: "whea_fatal", Core: 3 } && wn?.Core == null, $"{wc?.Core}/{wf?.Core}/{wn?.Core}");
        C("APIC to core honours threads per core", CrashEvents.ApicToCore(5) == 2 && CrashEvents.ApicToCore(5, 1) == 5 && CrashEvents.ApicToCore(7, 2) == 3 && CrashEvents.ApicToCore(3, 0) == 3);
        C("WHEA core uses the threadsPerCore parameter", CrashEvents.Classify(Ev("Microsoft-Windows-WHEA-Logger", 19, [("ApicId", "5")]), 1)?.Core == 5);

        var bc = CrashEvents.Classify(Ev("Microsoft-Windows-WER-SystemErrorReporting", 1001, [("param1", "The bugcheck was: 0x00000116 (0xffffd, 0xfffff, 0x0, 0x4). A dump was saved in: C:\\Windows\\MEMORY.DMP.")]));
        C("bugcheck 1001 text: 0x116 is gpu", bc is { Kind: "bugcheck", Suspect: "gpu", Bugcheck: 0x116 }, $"{bc?.Bugcheck}");
        bool all = true; string bad = "";
        foreach (var (code, sus) in new[] { ("00000124", "cpu"), ("00000101", "cpu"), ("0000010e", "gpu"), ("00000117", "gpu"), ("00000119", "gpu"), ("0000001a", "unknown") })
        {
            var r = CrashEvents.Classify(Ev("Microsoft-Windows-WER-SystemErrorReporting", 1001, [("param1", $"0x{code} (0x1, 0x2)")]));
            if (r?.Suspect != sus) { all = false; bad += $" {code}->{r?.Suspect}"; }
        }
        C("bugcheck suspect mapping (gpu 116/117/119/10E, cpu 124/101, else unknown)", all, bad);

        var kp0 = CrashEvents.Classify(Ev("Microsoft-Windows-Kernel-Power", 41, [("BugcheckCode", "0"), ("BugcheckParameter1", "0x00000000")]));
        var kp = CrashEvents.Classify(Ev("Microsoft-Windows-Kernel-Power", 41, [("BugcheckCode", "278")]));
        C("Kernel-Power 41: decimal 278 = 0x116 gpu; 0 = no code, unknown", kp is { Kind: "unexpected_shutdown", Suspect: "gpu", Bugcheck: 0x116 } && kp0 is { Suspect: "unknown", Bugcheck: null }, $"{kp?.Bugcheck}/{kp0?.Bugcheck}");

        // ---- minidump header
        var hdr = new byte[0x60];
        "PAGEDU64"u8.CopyTo(hdr); BitConverter.GetBytes(0x116u).CopyTo(hdr, 0x38);
        for (int i = 0; i < 4; i++) BitConverter.GetBytes((ulong)(i + 1)).CopyTo(hdr, 0x40 + 8 * i);
        var md = CrashEvents.ParseMinidumpHeader(hdr);
        C("minidump header: code at 0x38 and 4 parameters", md is { Code: 0x116 } && md.Value.Params.SequenceEqual(new ulong[] { 1, 2, 3, 4 }));
        var badHdr = (byte[])hdr.Clone(); badHdr[0] = (byte)'X';
        C("minidump header: bad signature or short input is rejected", CrashEvents.ParseMinidumpHeader(badHdr) == null && CrashEvents.ParseMinidumpHeader(hdr.AsSpan(0, 0x30)) == null);

        // minidump fallback through the watch: newest file in a temp dir fills a code-less Kernel-Power 41
        var dumpDir = Path.Combine(Path.GetTempPath(), "glint-dumps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dumpDir);
        try
        {
            File.WriteAllBytes(Path.Combine(dumpDir, "old.dmp"), hdr); File.SetLastWriteTimeUtc(Path.Combine(dumpDir, "old.dmp"), new DateTime(2020, 1, 1));
            var newer = (byte[])hdr.Clone(); BitConverter.GetBytes(0x124u).CopyTo(newer, 0x38);
            var np = Path.Combine(dumpDir, "new.dmp"); File.WriteAllBytes(np, newer);
            File.SetLastWriteTimeUtc(np, new DateTime(2026, 11, 8, 10, 1, 0, DateTimeKind.Utc));
            using var rg = new Rig(dumpDir);
            var rec = rg.Watch.Process(Ev("Microsoft-Windows-Kernel-Power", 41, [("BugcheckCode", "0")], "System", At(0)));
            C("minidump fallback: code-less Kernel-Power 41 takes the newest dump's code", rec is { Bugcheck: "0x00000124", Suspect: "cpu" }, $"{rec?.Bugcheck}/{rec?.Suspect}");
        }
        finally { try { Directory.Delete(dumpDir, true); } catch (IOException) { } }

        // ---- decisions
        using (var rg = new Rig())
        {
            rg.Cfg.MaxCrashes = 10;
            rg.Tdr(0);
            var a1 = rg.Saved.ElementAtOrDefault(0);
            rg.Tdr(10); var a2 = rg.Saved.ElementAtOrDefault(1);
            rg.Tdr(20); var a3 = rg.Saved.ElementAtOrDefault(2);
            C("back-off sequence: core -15, then memory -250 AB (-125 NVML), then core again",
                a1 == (135, 400) && a2 == (135, 275) && a3 == (120, 275), $"{a1} {a2} {a3}");
            C("back-off sets the GPU offsets through IGpuControl", rg.Gpu.Calls.Take(2).SequenceEqual(["Core=135", "Memory=400"]), string.Join(",", rg.Gpu.Calls));
        }
        using (var rg = new Rig())
        {
            rg.Cfg.MaxCrashes = 99; rg.Applied = (10, 200, 0.5);
            for (int i = 0; i < 5; i++) rg.Tdr(i);
            C("never below 0: offsets bottom out at 0/0 (stock) and later crashes are not counted",
                rg.Saved.All(s => s.Item1 >= 0 && s.Item2 >= 0) && rg.Saved[^1] == (0, 0) && rg.Saved.Count == 3 && rg.Watch.Records[^1].Counts == false, string.Join(";", rg.Saved));
        }
        using (var rg = new Rig())
        {
            rg.Cfg.MaxCrashes = 3;
            rg.Tdr(0); rg.Tdr(10); var third = rg.Tdr(20);
            C("MaxCrashes GPU crashes in the window revert to stock", rg.Reverts == 1 && rg.Gpu.Calls.Contains("reset") && third?.ActionText.StartsWith("Reverted") == true, third?.ActionText ?? "null");
            rg.Applied = (100, 100, 0.5);
            rg.Tdr(30);
            C("after a revert the counter starts fresh (next crash backs off, not reverts)", rg.Reverts == 1 && rg.Saved.Count == 3, $"reverts={rg.Reverts} saved={rg.Saved.Count}");
        }
        // one driver hang logs several events within seconds (seen on a real card: nvlddmkm 153, 14, 153 in 2.3 s):
        // that is one crash, so one back-off step and one strike, not MaxCrashes strikes and a revert to stock
        using (var rg = new Rig())
        {
            rg.Cfg.MaxCrashes = 3;
            var b1 = rg.Watch.Process(Ev("nvlddmkm", 153, null, "System", AtSec(0)));
            var b2 = rg.Watch.Process(Ev("nvlddmkm", 14, null, "System", AtSec(0.5)));
            var b3 = rg.Watch.Process(Ev("Display", 4101, null, "System", AtSec(2.3)));
            C("a burst of driver-reset events is one crash: one back-off, no revert",
                rg.Reverts == 0 && rg.Saved.Count == 1 && rg.Saved[0] == (135, 400) && rg.Applied == (135, 400, 0.5)
                && b1 is { Counts: true, Step: "core" } && b2 is { Counts: false, Step: null } && b3 is { Counts: false, Step: null }
                && b2.ActionText.StartsWith("Same incident") && rg.Watch.Records.Count == 3,
                $"reverts={rg.Reverts} saved={string.Join(";", rg.Saved)} | {b2?.ActionText} | {b3?.ActionText}");
            var late = rg.Watch.Process(Ev("nvlddmkm", 153, null, "System", AtSec(CrashDecision.IncidentSeconds - 1)));
            var next = rg.Watch.Process(Ev("nvlddmkm", 153, null, "System", AtSec(CrashDecision.IncidentSeconds + 1)));
            C("the incident window is measured from the crash that counted: an event after it is a new crash",
                late is { Counts: false } && next is { Counts: true, Step: "mem" } && rg.Saved.Count == 2 && rg.Reverts == 0, $"{late?.ActionText} | {next?.ActionText}");
        }
        using (var rg = new Rig())
        {
            rg.Cfg.MaxCrashes = 3;
            // every crash is told to the user: wired the way GlintService wires crash watch to the notifier
            var told = new List<Notice>();
            var notify = new Notifications(() => new NotificationOptions());
            notify.Attach(told.Add);
            rg.Watch.Acted += (rec, act) => notify.Post(NoticeText.Crash(rec, act));
            rg.Watch.Process(Ev("nvlddmkm", 153, null, "System", AtSec(-7200))); rg.Watch.Process(Ev("nvlddmkm", 14, null, "System", AtSec(-7199.5))); rg.Watch.Process(Ev("nvlddmkm", 153, null, "System", AtSec(-7197.7)));
            C("one driver reset logged as three events is ONE Windows notification, saying the overclock was lowered",
                told.Count == 1 && told[0] is { Category: "crashes", Title: "GPU crash: overclock lowered", Shown: true } && told[0].Text.Contains("NVIDIA driver reset (nvlddmkm 153)") && told[0].Text.Contains("+150 → +135 MHz"),
                $"{told.Count}: {string.Join(" | ", told.Select(n => n.Title + ": " + n.Text))}");
            told.Clear();
            using var fresh = new Rig(); fresh.Cfg.MaxCrashes = 3;
            fresh.Watch.Acted += (rec, act) => notify.Post(NoticeText.Crash(rec, act));
            CrashRecord? third = null;
            for (int i = 0; i < 3; i++) { third = fresh.Tdr(i * 10); fresh.Watch.Process(Ev("nvlddmkm", 14, null, "System", AtSec(i * 600 + 1))); }
            C("three separate incidents, each logged twice, revert on the third incident (not the third event)",
                fresh.Reverts == 1 && fresh.Saved.Count == 2 && third is { Step: "revert" }, $"reverts={fresh.Reverts} saved={fresh.Saved.Count} third={third?.ActionText}");
            C("three separate incidents are three notifications: lowered, lowered, then removed with the reason",
                string.Join(" / ", told.Select(n => n.Title)) == "GPU crash: overclock lowered / GPU crash: overclock lowered / GPU crash: overclock removed" && told[^1].Text.Contains("3 GPU-suspect crashes in 7 days"),
                string.Join(" / ", told.Select(n => n.Title)));
        }
        using (var rg = new Rig())
        {
            Func<double, CrashRecord?> soft = s => rg.Watch.Process(Ev("Application Error", 1000, [(null, "g.exe"), (null, "1"), (null, "nvwgf2umx.dll")], "Application", AtSec(s)));
            var s1 = soft(0); var s2 = soft(1); int afterPair = rg.Saved.Count;
            var s3 = soft(5 * 3600);
            C("one game crash logged twice is still the 1st soft event; a later one acts",
                s1 is { Counts: false } && s2 is { Counts: false } && afterPair == 0 && s3 is { Counts: true } && rg.Saved.Count == 1, $"{s2?.ActionText} | {s3?.ActionText}");
            var tdrThenApp = rg.Watch.Process(Ev("nvlddmkm", 153, null, "System", AtSec(9 * 3600)));
            var appAfter = soft(9 * 3600 + 3);
            C("a game crash seconds after a counted driver reset is the same incident",
                tdrThenApp is { Counts: true } && appAfter is { Counts: false } && appAfter.ActionText.StartsWith("Same incident") && rg.Saved.Count == 2 && rg.Reverts == 0, appAfter?.ActionText ?? "null");
        }
        using (var rg = new Rig())
        {
            rg.Cfg.MaxCrashes = 3;
            rg.Tdr(0); rg.Tdr(60 * 24 * 8);
            C("crashes older than CrashWindowDays are not counted", rg.Reverts == 0 && rg.Saved.Count == 2);
        }
        using (var rg = new Rig())
        {
            rg.Cfg.AutoBackoff = false; rg.Cfg.MaxCrashes = 3;
            var r = rg.Tdr(0);
            C("AutoBackoff off: recorded and counted, nothing changed", r is { Counts: true } && rg.Saved.Count == 0 && rg.Gpu.Calls.Count == 0);
        }
        using (var rg = new Rig())
        {
            Func<int, CrashRecord?> soft = m => rg.Watch.Process(Ev("Application Error", 1000, [(null, "g.exe"), (null, "1"), (null, "nvwgf2umx.dll")], "Application", At(m)));
            var s1 = soft(0); int afterFirst = rg.Saved.Count;
            var s2 = soft(60 * 5); int afterSecond = rg.Saved.Count;
            C("soft app crash: 1st only recorded, 2nd within 24 h backs off", s1 is { Counts: false } && afterFirst == 0 && s2 is { Counts: true } && afterSecond == 1, $"{s1?.ActionText} | {s2?.ActionText}");
        }
        using (var rg = new Rig())
        {
            rg.Watch.Process(Ev("Application Error", 1000, [(null, "g.exe"), (null, "1"), (null, "nvwgf2umx.dll")], "Application", At(0)));
            rg.Watch.Process(Ev("Application Error", 1000, [(null, "g.exe"), (null, "1"), (null, "nvwgf2umx.dll")], "Application", At(60 * 30)));
            C("soft app crashes more than 24 h apart never act", rg.Saved.Count == 0 && rg.Gpu.Calls.Count == 0);
        }
        using (var rg = new Rig())
        {
            rg.Applied = null;
            var a = rg.Tdr(0); var b = rg.Tdr(5); var c = rg.Tdr(10);
            rg.Applied = (150, 400, 0.5); rg.Cfg.MaxCrashes = 2;
            rg.Tdr(20);
            C("crashes at stock are recorded but never counted (next overclocked crash is the 1st)",
                rg.Watch.Records.Count == 4 && a is { Counts: false, AppliedCore: null } && c is { Counts: false } && rg.Reverts == 0 && rg.Saved.Count == 1, $"reverts={rg.Reverts} saved={rg.Saved.Count}");
        }
        using (var rg = new Rig())
        {
            var w = rg.Watch.Process(Ev("Microsoft-Windows-WHEA-Logger", 19, [("ApicId", "9")]));
            var k = rg.Watch.Process(Ev("Microsoft-Windows-Kernel-Power", 41, [("BugcheckCode", "292")]));
            C("CPU-suspect events never change anything; core is recorded", w is { Suspect: "cpu", Core: 4 } && k is { Suspect: "cpu", Bugcheck: "0x00000124" } && rg.Gpu.Calls.Count == 0 && rg.Saved.Count == 0 && rg.Reverts == 0, $"{w?.Core}");
        }
        using (var rg = new Rig())
        {
            rg.Cfg.MaxCrashes = 10;
            var e = Ev("Display", 4101, null, "System", At(0), 777);
            var first = rg.Watch.Process(e); var again = rg.Watch.Process(e);
            var other = rg.Watch.Process(Ev("Display", 4101, null, "System", At(1), 778));
            using var reload = rg.Make();
            var afterRestart = reload.Process(e);
            C("dedupe by record id (in memory and across a restart via crashes.json)",
                first != null && again == null && other != null && afterRestart == null && reload.Records.Count == 2 && rg.Saved.Count == 2, $"{first != null}/{again == null}/{afterRestart == null}/{reload.Records.Count}");
        }
        using (var rg = new Rig())
        {
            rg.Cfg.MaxCrashes = 99; rg.Applied = (150, 400, 0.5);
            var bug = rg.Watch.Process(Ev("Microsoft-Windows-WER-SystemErrorReporting", 1001, [("param1", "0x00000116 (0x1, 0x2, 0x3, 0x4)")], "System", At(1)));
            var kpd = rg.Watch.Process(Ev("Microsoft-Windows-Kernel-Power", 41, [("BugcheckCode", "278")], "System", At(0)));
            C("Kernel-Power 41 and its bugcheck 1001 are one crash", bug != null && kpd == null && rg.Watch.Records.Count == 1);
        }
        using (var rg = new Rig())
        {
            for (int i = 0; i < CrashWatch.MaxEvents + 20; i++) rg.Watch.Process(Ev("Microsoft-Windows-WHEA-Logger", 19, [("ApicId", "1")], "System", At(i), 1000 + i));
            using var reload = rg.Make();
            C("history is capped at MaxEvents on disk", rg.Watch.Records.Count == CrashWatch.MaxEvents && reload.Records.Count == CrashWatch.MaxEvents, $"{rg.Watch.Records.Count}/{reload.Records.Count}");
        }
        using (var rg = new Rig())
        {
            CrashRecord? seen = null; CrashAction? act = null;
            rg.Watch.Acted += (r, a) => { seen = r; act = a; };
            rg.Tdr(0);
            C("Acted event carries the record and the action", seen is { Kind: "gpu_tdr", AppliedCore: 150, AppliedMem: 400 } && act is { Kind: "backoff", NewCore: 135 });
        }

        // ---- XPath
        var xps = new (string Ch, string Xp, string Name)[] { ("System", CrashEvents.SystemXPath, "System"), ("Application", CrashEvents.AppXPath, "Application"), ("System", CrashEvents.ScanXPath(3_600_000), "since-boot scan") };
        C("XPath strings are balanced", xps.All(x => Balanced(x.Xp)) && !Balanced("*[System[Provider[@Name='x']]") && !Balanced("*[System[(EventID=1]]"));
        C("XPath filters name exactly the providers/IDs we classify",
            new[] { "nvlddmkm", "4101", "Microsoft-Windows-WHEA-Logger", "EventID=18", "EventID=19", "Kernel-Power", "EventID=41", "SystemErrorReporting", "EventID=1001" }.All(CrashEvents.SystemXPath.Contains)
            && CrashEvents.NvModules.All(m => CrashEvents.AppXPath.Contains($"Data='{m}'")));
        C("XPath validator rejects a malformed query (checker can fail)", XPathError("System", "*[System[Provider[@Name='x']") != null);
        foreach (var (ch, xp, name) in xps)
        {
            var err = XPathError(ch, xp);
            C($"XPath accepted by the event log: {name}", err == null, err ?? "");
        }
    }
}
