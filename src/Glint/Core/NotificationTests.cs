using System.Text.Json;
using Glint.Tuning;

namespace Glint.Core;

/// <summary>
/// Offline checks for Windows notifications: the notifier (switches, repeats, late sink, limits), the wording of every
/// notice against Windows' length limits, the cooling alerts fed by the fan loop, the config section, and the service
/// wiring on a no-hardware rig. Nothing is ever shown: no test attaches the tray icon.
/// </summary>
public static class NotificationTests
{
    public static void Run(Action<string, bool, string> check)
    {
        void C(string n, bool p, string d = "") => check("notify: " + n, p, d);

        // ---- the notifier
        {
            var opt = new NotificationOptions();
            var now = new DateTime(2026, 1, 1, 12, 0, 0);
            var shown = new List<Notice>();
            var nt = new Notifications(() => opt, () => now);
            nt.Attach(shown.Add);
            var a = nt.Post(new NoticeDraft(Notifications.Crashes, "warning", "GPU crash", "Driver reset.", "tuning"));
            C("a posted notice reaches the sink once and is recorded as shown",
                shown.Count == 1 && a is { Shown: true, Muted: null, Category: "crashes", Level: "warning", Title: "GPU crash", Text: "Driver reset.", Page: "tuning" } && nt.Recent.Count == 1 && a.Time == now);

            opt.Crashes = false;
            var b = nt.Post(new NoticeDraft(Notifications.Crashes, "warning", "x", "y"));
            var c = nt.Post(new NoticeDraft(Notifications.Cooling, "warning", "x", "y"));
            C("a kind's switch mutes only that kind (recorded with the reason, never shown)",
                b is { Shown: false, Muted: "crashes notifications are off" } && c is { Shown: true } && shown.Count == 2 && nt.Recent.Count == 3, $"{b?.Muted} shown={shown.Count}");

            opt.Enabled = false;
            var d = nt.Post(new NoticeDraft(Notifications.Cooling, "warning", "x", "y"));
            var t = nt.Post(NoticeText.TestNotice());
            C("the master switch mutes everything, the test notice included", d is { Shown: false, Muted: "notifications are off" } && t is { Shown: false } && shown.Count == 2, $"{d?.Muted}");

            opt.Enabled = true; opt.Crashes = opt.Tuning = opt.Cooling = opt.Devices = opt.Updates = false;
            var t2 = nt.Post(NoticeText.TestNotice());
            C("the test notice needs only the master switch", t2 is { Shown: true, Category: "test" } && shown.Count == 3);
            C("every kind has its own switch", Notifications.Categories.All(k => !opt.Allows(k)) && new NotificationOptions() is var all && Notifications.Categories.All(all.Allows) && Notifications.Categories.Length == 5);
        }
        {
            var opt = new NotificationOptions(); var now = new DateTime(2026, 1, 1, 12, 0, 0);
            var shown = new List<Notice>(); var nt = new Notifications(() => opt, () => now); nt.Attach(shown.Add);
            NoticeDraft K(string key, double quiet) => new(Notifications.Cooling, "warning", "t", "x", null, key, quiet);
            var p1 = nt.Post(K("sensor:a", 600)); now = now.AddSeconds(599); var p2 = nt.Post(K("sensor:a", 600));
            var other = nt.Post(K("sensor:b", 600));
            now = now.AddSeconds(2); var p3 = nt.Post(K("sensor:a", 600));
            C("the same key is not repeated within its quiet time, and is again after it; other keys are unaffected",
                p1 != null && p2 == null && other != null && p3 != null && shown.Count == 3 && nt.Recent.Count == 3, $"shown={shown.Count} recent={nt.Recent.Count}");
            var o1 = nt.Post(K("once", Notifications.Once)); now = now.AddDays(30); var o2 = nt.Post(K("once", Notifications.Once));
            C("a once-per-run key is never repeated", o1 != null && o2 == null);
            int before = shown.Count; nt.Post(new NoticeDraft("cooling", "info", "t", "x")); nt.Post(new NoticeDraft("cooling", "info", "t", "x"));
            C("a notice without a key is always posted", shown.Count == before + 2);
            C("a null draft posts nothing", nt.Post((NoticeDraft?)null) == null && shown.Count == before + 2);
        }
        {
            var opt = new NotificationOptions(); var nt = new Notifications(() => opt);
            for (int i = 0; i < 7; i++) nt.Post(new NoticeDraft("cooling", "info", "n" + i, "x"));
            opt.Cooling = false; nt.Post(new NoticeDraft("cooling", "info", "muted", "x")); opt.Cooling = true;
            var late = new List<Notice>(); nt.Attach(late.Add);
            C("notices posted before the tray icon exists are shown when it attaches (the newest 5, in order, never a muted one)",
                string.Join(",", late.Select(n => n.Title)) == "n2,n3,n4,n5,n6", string.Join(",", late.Select(n => n.Title)));
            nt.Post(new NoticeDraft("cooling", "info", "after", "x"));
            C("after attaching, notices go straight to the sink and are not replayed", late.Count == 6 && late[^1].Title == "after");
            for (int i = 0; i < Notifications.Keep + 10; i++) nt.Post(new NoticeDraft("cooling", "info", "k" + i, "x"));
            C("the recent list is capped", nt.Recent.Count == Notifications.Keep && nt.Recent[^1].Title == "k" + (Notifications.Keep + 9), $"{nt.Recent.Count}");
            var bad = new Notifications(() => null); bad.Attach(_ => throw new InvalidOperationException("sink"));
            C("a failing sink (or missing options) never breaks the poster", bad.Post(new NoticeDraft("cooling", "info", "t", "x")) is { Shown: true });
        }
        {
            string long1 = string.Join(' ', Enumerable.Repeat("overclock", 60));
            var f = Notifications.Fit(long1, Notifications.TextMax);
            C("text over Windows' limit is cut at a word and ends with an ellipsis", f.Length <= Notifications.TextMax && f.EndsWith("overclock…") && f.Length > 240, $"{f.Length}");
            C("text within the limit is untouched; line breaks and runs of spaces become single spaces",
                Notifications.Fit("Short text.", 255) == "Short text." && Notifications.Fit(" a\r\n  b\tc ", 255) == "a b c" && Notifications.Fit(null, 10) == "");
            var one = Notifications.Fit(new string('x', 400), Notifications.TitleMax);
            C("a single over-long word is still cut to the limit", one.Length == Notifications.TitleMax && one.EndsWith('…'), $"{one.Length}");
            var nt = new Notifications(() => new NotificationOptions());
            var n = nt.Post(new NoticeDraft("cooling", "info", new string('t', 100), long1));
            C("a posted notice always fits Windows' title and text limits", n!.Title.Length <= Notifications.TitleMax && n.Text.Length <= Notifications.TextMax, $"{n.Title.Length}/{n.Text.Length}");
        }

        // ---- wording: what a crash turns into
        {
            CrashRecord Rec(string kind, string suspect, string detail, int? core = null, int? appliedCore = 300, int? appliedMem = 5000) =>
                new(1, "System", new DateTime(2026, 1, 1), kind, suspect, detail, null, core, appliedCore, appliedMem, "", false, null);
            var tdr = Rec(CrashEvents.GpuTdr, "gpu", "NVIDIA driver reset (nvlddmkm 153)");
            var back = NoticeText.Crash(tdr, new CrashAction(CrashDecision.Backoff, "Auto back-off after a GPU crash: core offset +300 -> +285 MHz", true, "core", 285, 5000));
            C("GPU crash with a back-off: says what crashed and the new core offset",
                back is { Category: "crashes", Level: "warning", Title: "GPU crash: overclock lowered", Page: "tuning" } && back.Value.Text.Contains("NVIDIA driver reset (nvlddmkm 153)") && back.Value.Text.Contains("+300 → +285 MHz"), back?.Text ?? "null");
            var mem = NoticeText.Crash(tdr, new CrashAction(CrashDecision.Backoff, "x", true, "mem", 300, 4875));
            C("GPU crash with a memory back-off says so", mem?.Text.Contains("Memory offset lowered by 250") == true, mem?.Text ?? "null");
            var rev = NoticeText.Crash(tdr, new CrashAction(CrashDecision.Revert, "Reverted to stock: 3 GPU-suspect crashes in 7 days. Re-run the tuner to try again.", true, "revert"));
            C("GPU crash that reverts to stock: says the overclock is gone and why",
                rev is { Title: "GPU crash: overclock removed" } && rev.Value.Text.Contains("3 GPU-suspect crashes in 7 days") && rev.Value.Text.Contains("Re-run the tuner"), rev?.Text ?? "null");
            C("an event of an incident already reported, or at stock, raises nothing",
                NoticeText.Crash(tdr, new CrashAction(CrashDecision.None, "Same incident", false, Quiet: true)) == null
                && NoticeText.Crash(Rec(CrashEvents.GpuTdr, "gpu", "NVIDIA driver reset", appliedCore: null, appliedMem: null), CrashDecision.Decide(new TuningConfig(), tdr, [], null)) == null);
            var whea = NoticeText.Crash(Rec(CrashEvents.WheaCorrected, "cpu", "WHEA corrected hardware error on core 4 (APIC 9)", core: 4), new CrashAction(CrashDecision.None, "CPU-suspect", false));
            C("CPU hardware error: names the core, changes nothing, at most once an hour per core",
                whea is { Title: "CPU hardware error detected", Key: "crash-cpu-4", QuietSec: 3600 } && whea.Value.Text.Contains("core 4's Curve Optimizer"), whea?.Text ?? "null");
            var unk = NoticeText.Crash(Rec(CrashEvents.UnexpectedShutdown, "unknown", "Unexpected shutdown (power loss, freeze or crash)"), new CrashAction(CrashDecision.None, "Cause unknown: nothing changed", false));
            C("a crash of unknown cause is still told", unk is { Title: "Crash detected" } && unk.Value.Text.Contains("Unexpected shutdown") && unk.Value.Text.Contains("changed nothing"), unk?.Text ?? "null");
            var soft = NoticeText.Crash(Rec(CrashEvents.GpuAppCrash, "gpu", "game.exe crashed in nvwgf2umx.dll"), new CrashAction(CrashDecision.None, "Soft event", false));
            C("a first game crash in the driver is told as watched, not acted on", soft is { Title: "Game crash in the GPU driver" } && soft.Value.Text.Contains("game.exe crashed in nvwgf2umx.dll") && soft.Value.Text.Contains("watching"), soft?.Text ?? "null");
            var off = NoticeText.Crash(tdr, new CrashAction(CrashDecision.None, "Auto back-off is off: nothing changed", true));
            C("a GPU crash with auto back-off switched off says nothing changed", off is { Title: "GPU crash detected" } && off.Value.Text.Contains("Auto back-off is off"), off?.Text ?? "null");
        }

        // ---- wording: start-up
        {
            const string crashNote = "Saved overclock not applied: the last shutdown was a crash.";
            var both = NoticeText.TuningStartup(true, crashNote, null, null, true, null);
            C("start after a crash with a saved overclock and undervolt: one notice naming both",
                both is { Category: "crashes", Title: "Started after a crash", Page: "tuning" } && both.Value.Text.Contains("GPU overclock and CPU undervolt were not applied") && both.Value.Text.Contains("Pick the profile again"), both?.Text ?? "null");
            var gpuOnly = NoticeText.TuningStartup(true, crashNote, null, null, false, null);
            var cpuOnly = NoticeText.TuningStartup(true, null, null, null, true, null);
            C("start after a crash names only what was really held back",
                gpuOnly!.Value.Text.Contains("so the GPU overclock was not applied") && !gpuOnly.Value.Text.Contains("undervolt") && cpuOnly!.Value.Text.Contains("so the CPU undervolt was not applied") && !cpuOnly.Value.Text.Contains("GPU"),
                $"{gpuOnly.Value.Text} | {cpuOnly.Value.Text}");
            var bare = NoticeText.TuningStartup(true, null, null, null, false, null);
            C("start after a crash with nothing saved is still told", bare is { Title: "Started after a crash" } && bare.Value.Text.Contains("no overclock or undervolt to hold back"), bare?.Text ?? "null");
            var removed = NoticeText.TuningStartup(true, crashNote, null, "2 crashes in a row with the CPU undervolt of Quiet (tuned) applied: it was removed from the profile (tune again to find a stable one).", false, null);
            C("start after a crash carries the CPU crash note", removed!.Value.Text.Contains("it was removed from the profile"), removed.Value.Text);

            var reverted = NoticeText.TuningStartup(false, "Saved overclock not applied: 3 GPU crashes within 7 days: back to stock until you re-tune", null, null, false, null);
            C("a start that leaves the overclock off after crash watch reverted it says why",
                reverted is { Category: "tuning", Level: "warning", Title: "Overclock not applied" } && reverted.Value.Text == "Saved overclock not applied: 3 GPU crashes within 7 days: back to stock until you re-tune.", reverted?.Text ?? "null");
            var driver = NoticeText.TuningStartup(false, "Saved overclock not applied: the GPU driver/VBIOS changed (621.14 → 630.02). Re-test first.", null, null, false, null);
            C("a driver change is told with both versions", driver is { Title: "Overclock not applied" } && driver.Value.Text.Contains("621.14 → 630.02"), driver?.Text ?? "null");
            var busy = NoticeText.TuningStartup(false, null, "MSI Afterburner is running", null, false, null);
            C("an overclock that could not be applied (another tool holds the card) is told", busy is { Title: "Overclock not applied" } && busy.Value.Text == "GPU overclock not re-applied: MSI Afterburner is running.", busy?.Text ?? "null");
            var co = NoticeText.TuningStartup(false, null, null, null, false, "CPU undervolt not applied (startup): values don't fit this CPU (16 cores, -50..0)");
            C("a CPU undervolt that could not be applied has its own title", co is { Title: "CPU undervolt not applied" }, co?.Title ?? "null");
            var died = NoticeText.TuningStartup(false, "A test at core +350 / memory +2500 didn't finish (crash or freeze): recorded as unstable.", null, null, false, null);
            C("a test that died with the PC is told without claiming the overclock is off", died is { Title: "Tuning" } && died.Value.Text.Contains("recorded as unstable"), died?.Title ?? "null");
            C("a normal start (everything applied) raises nothing", NoticeText.TuningStartup(false, null, null, null, false, null) == null);
        }

        // ---- wording: tune results and the rest; everything fits Windows' limits as written
        {
            string prof = new string('P', 40);   // the longest profile name the validator allows
            var done = NoticeText.TuneDone("Custom (tuned)", null, (250, 2000, null), 9.9, "−37 on all 16 cores");
            C("tune finished: profile, GPU offsets with the measured gain, CPU undervolt",
                done is { Category: "tuning", Level: "info", Title: "Tune finished" } && done.Text == "Custom (tuned) is ready. GPU core +250 / memory +2000, 9.9% better than stock. CPU undervolt −37 on all 16 cores.", done.Text);
            var eff = NoticeText.TuneDone("Quiet (tuned)", null, (0, 0, 70), null, null);
            C("tune finished: an efficiency tune names its power limit", eff.Text == "Quiet (tuned) is ready. GPU core 0 / memory 0 at 70% power.", eff.Text);
            C("tune finished with nothing to add is just the profile", NoticeText.TuneDone("Stocky", null, null, null, null).Text == "Stocky is ready.");
            var early = NoticeText.TuneDone("Custom (tuned)", "Claude Code isn't signed in: run 'claude' once and sign in, then try again.", null, null, null);
            C("tune that ended early: a warning with the reason", early is { Level: "warning", Title: "Tune ended early" } && early.Text.StartsWith("Custom (tuned): Claude Code isn't signed in"), early.Text);
            var lostN = NoticeText.SensorLost("max(xt:probe1,xt:probe2)", ["Radiator + case fans", "GPU fans"], 100);
            C("sensor lost: names the sensor, the rules and the fail-safe speed; the same sensor at most every 10 min",
                lostN is { Category: "cooling", Key: "sensor:max(xt:probe1,xt:probe2)", QuietSec: 600 } && lostN.Text == "\"max(xt:probe1,xt:probe2)\" is not reporting, so the rules Radiator + case fans, GPU fans run at 100% until it is back.", lostN.Text);
            C("sensor lost: one rule reads as singular", NoticeText.SensorLost("liquid", ["Pump"], 90).Text == "\"liquid\" is not reporting, so the rule Pump runs at 90% until it is back.");
            C("update notices: a new version once per version; only the hand-over and a failure of an install",
                NoticeText.UpdateAvailable("0.6.0") is { Title: "Glint 0.6.0 is available", Key: "update:0.6.0", QuietSec: Notifications.Once }
                && NoticeText.Updater(new UpdateStatus("downloading", "0.6.0", "Downloading…", 0, 0, DateTime.Now)) == null
                && NoticeText.Updater(new UpdateStatus("failed", "0.6.0", "Update not installed: checksum mismatch.", 0, 0, DateTime.Now)) is { Level: "warning", Category: "updates" }
                && NoticeText.Updater(new UpdateStatus("installing", "0.6.0", "Starting the Glint 0.6.0 setup.", 0, 0, DateTime.Now)) is { Level: "info" });

            var drafts = new List<NoticeDraft>
            {
                NoticeText.TuneDone(prof, null, (500, 3000, 100), 12.3, "−30 on cores 0–7, −25 on cores 8–15"), NoticeText.TuneDone(prof, null, null, null, null),
                NoticeText.Verified($"{prof}: core +500 / memory +3000 at 100% power is 12.3% more efficient (-10.0% speed) than stock, tested back to back (changed from +500 / +3000 at 90%)"),
                NoticeText.CpuSearchDone(prof, "−30 on cores 0–7, −25 on cores 8–15"), NoticeText.CpuSearchStopped("watchdog: CPU reached 95 °C (limit 95 °C)"),
                NoticeText.ProfileOc($"Profile {prof}: overclock not applied (3 GPU crashes within 7 days: back to stock until you re-tune)"),
                NoticeText.SensorLost("max(xt:probe1,xt:probe2)", ["Radiator + case fans", "GPU fans", "Rear exhaust", "Pump"], 100),
                NoticeText.FanContested("GPU fan 1", "another program set this fan to 100% (Glint sent 45%), e.g. MSI Afterburner's fan curve or FanControl. Glint handed it back and tries again in 10 minutes."),
                NoticeText.LoopError("The device is not connected.", 100), NoticeText.NoCpuTemp(), NoticeText.DeviceStopped("Corsair Lighting Node Pro", "The device is not connected"),
                NoticeText.PluginApproval("Kingston Fury DRAM"), NoticeText.HotkeyTaken("Ctrl+Shift+F12"), NoticeText.ConfigInvalid("'}' is invalid after a property name. LineNumber: 12"),
                NoticeText.UpdateAvailable("10.20.30"), NoticeText.TestNotice(),
                NoticeText.TuningStartup(true, "Saved overclock not applied: the last shutdown was a crash.", null, $"Crash with the CPU undervolt of {prof} applied (1/2): not applied this start.", true, null)!.Value,
                NoticeText.TuningStartup(true, "Saved overclock not applied: the last shutdown was a crash.", null, $"2 crashes in a row with the CPU undervolt of {prof} applied: it was removed from the profile (tune again to find a stable one).", false, null)!.Value,
                NoticeText.TuningStartup(false, "Saved overclock not applied: the GPU driver/VBIOS changed (621.14 → 630.02). Re-test first.", null, null, false, $"CPU undervolt not applied (startup): values don't fit this CPU (16 cores, -50..0)")!.Value,
                NoticeText.Crash(new CrashRecord(1, "System", DateTime.Now, CrashEvents.GpuTdr, "gpu", "NVIDIA driver reset (nvlddmkm 153)", null, null, 500, 6000, "", false, null),
                    new CrashAction(CrashDecision.Revert, "Reverted to stock: 20 GPU-suspect crashes in 90 days. Re-run the tuner to try again.", true, "revert"))!.Value,
            };
            var tooLong = drafts.Where(d => d.Title.Length > Notifications.TitleMax || Notifications.Fit(d.Text, int.MaxValue).Length > Notifications.TextMax).Select(d => $"{d.Title} ({d.Title.Length}/{d.Text.Length})").ToList();
            C("every built-in notice fits Windows' title and text limits without being cut, even with the longest names", tooLong.Count == 0, string.Join("; ", tooLong));
            C("every built-in notice has a known category, a level and a page that exists",
                drafts.All(d => (Notifications.Categories.Contains(d.Category) || d.Category == Notifications.Test) && d.Level is "info" or "warning" or "error"
                    && d.Page is "tuning" or "cooling" or "devices" or "plugins" or "overlay" or "settings" or "setup"), string.Join(",", drafts.Select(d => d.Page).Distinct()));
        }

        // ---- cooling alerts (what the fan loop feeds)
        {
            var posted = new List<NoticeDraft>();
            var al = new CoolingAlerts();
            (string, string)[] none = [];
            (string, string)[] lost = [("Case fans", "xt:probe1"), ("Radiator", "xt:probe1"), ("GPU fans", "gpu")];
            string Name(string id) => id == "gpu:Fan 1" ? "GPU fan 1" : id;
            void Tick(double now, (string, string)[] l, Dictionary<string, string>? contested = null, string err = "") => al.Check(now, l, contested, err, 100, Name, posted.Add);
            Tick(0, none); Tick(1, none);
            C("nothing is posted while every sensor reports", posted.Count == 0);
            Tick(2, lost); Tick(2 + CoolingAlerts.AfterSec - 1, lost);
            C("a sensor that just went missing is given time (start-up readings are often empty)", posted.Count == 0);
            Tick(2 + CoolingAlerts.AfterSec, lost);
            C("a sensor missing for the whole wait is told once per sensor, naming the rules it drives",
                posted.Count == 2 && posted.Any(p => p.Key == "sensor:xt:probe1" && p.Text.Contains("the rules Case fans, Radiator run at 100%")) && posted.Any(p => p.Key == "sensor:gpu" && p.Text.Contains("the rule GPU fans runs at 100%")),
                string.Join(" | ", posted.Select(p => p.Text)));
            Tick(60, lost); Tick(120, lost);
            C("it is not repeated while the sensor stays missing", posted.Count == 2);
            Tick(121, none); Tick(122, lost); Tick(122 + CoolingAlerts.AfterSec + 1, lost);
            C("a sensor that came back and went missing again is told again", posted.Count == 4, $"{posted.Count}");
            posted.Clear();
            var gap = new CoolingAlerts();
            gap.Check(0, lost, null, "", 100, Name, posted.Add); gap.Check(3, lost, null, "", 100, Name, posted.Add); gap.Check(4, none, null, "", 100, Name, posted.Add);
            gap.Check(4 + CoolingAlerts.AfterSec + 5, none, null, "", 100, Name, posted.Add);
            C("a sensor that was only missing for a moment at start-up raises nothing", posted.Count == 0);

            var con = new Dictionary<string, string> { ["gpu:Fan 1"] = "another program set this fan to 100% (Glint sent 45%)" };
            Tick(200, none, con); Tick(201, none, con);
            C("a fan another program took over is told once, by its name", posted.Count == 1 && posted[0] is { Title: "Another program is controlling a fan", Category: "cooling" } && posted[0].Text.StartsWith("GPU fan 1: another program set this fan to 100%"), posted.Count > 0 ? posted[0].Text : "none");
            Tick(202, none, new Dictionary<string, string>()); Tick(203, none, con);
            C("a fan that was released and taken again is told again", posted.Count == 2);
            posted.Clear();
            Tick(300, none, null, "The device is not connected."); Tick(300 + CoolingAlerts.AfterSec - 1, none, null, "The device is not connected.");
            bool early = posted.Count == 0;
            Tick(300 + CoolingAlerts.AfterSec, none, null, "The device is not connected."); Tick(400, none, null, "The device is not connected.");
            C("a fan-loop error that lasts is told once, as an error, with the fail-safe speed",
                early && posted.Count == 1 && posted[0] is { Level: "error", Title: "Fan control problem" } && posted[0].Text.Contains("fail-safe speed (100%)"), posted.Count > 0 ? posted[0].Text : "none");
            Tick(401, none); Tick(402, none, null, "again"); Tick(402 + CoolingAlerts.AfterSec, none, null, "again");
            C("after the loop recovered, a new error is told again", posted.Count == 2);
        }
        {
            var cfg = GlintConfig.Defaults();
            cfg.ActiveProfile = "T";
            cfg.Profiles["T"] = [new FanRule { Name = "Case", Fans = ["f1"], Inputs = [new() { Sensor = "probe", Points = [[20, 20], [60, 100]] }, new() { Sensor = "max(cpu,gone)", Points = [[20, 20], [60, 100]] }] },
                                 new FanRule { Name = "Pump", Fans = ["p"], Fixed = 100 }];
            var e = new FanEngine();
            var duty = e.Compute(cfg, new Dictionary<string, double> { ["cpu"] = 40 }, 1);
            C("the fan engine lists the sensors a rule could not read (and runs that rule at the fail-safe duty)",
                e.Lost.Count == 2 && e.Lost.Contains(("Case", "probe")) && e.Lost.Contains(("Case", "max(cpu,gone)")) && duty["f1"] == cfg.FailsafeDuty, string.Join(";", e.Lost));
            e.Compute(cfg, new Dictionary<string, double> { ["cpu"] = 40, ["probe"] = 30, ["gone"] = 35 }, 1);
            C("the list is empty again once every sensor reports", e.Lost.Count == 0);
            var real = GlintService.LostOnRealFans([("Case", "probe"), ("Ghost", "probe")], [cfg.Profiles["T"][0], new FanRule { Name = "Ghost", Fans = ["not-here"] }], new HashSet<string> { "f1", "p" });
            C("a rule that drives no fan this PC has raises no sensor alert", real.Count == 1 && real[0] == ("Case", "probe"), string.Join(";", real));
        }

        // ---- config
        {
            var def = GlintConfig.Defaults();
            C("notifications are on by default, every kind", def.Notifications is { Enabled: true, Crashes: true, Tuning: true, Cooling: true, Devices: true, Updates: true });
            var old = JsonSerializer.Deserialize<GlintConfig>("{ \"pollMs\": 1000 }", GlintConfig.Json)!;
            C("a config.json from before notifications existed loads with them on", old.Notifications is { Enabled: true, Crashes: true });
            var json = JsonSerializer.Serialize(def, GlintConfig.Json);
            var back = JsonSerializer.Deserialize<GlintConfig>(json.Replace("\"crashes\": true", "\"crashes\": false"), GlintConfig.Json)!;
            C("the section round-trips through config.json", json.Contains("\"notifications\"") && back.Notifications is { Enabled: true, Crashes: false, Cooling: true });
            var ctx = new ValidationContext();
            bool okBefore = ConfigValidator.Check(def, ctx).Ok;
            def.Notifications = null!;
            var v = ConfigValidator.Check(def, ctx);
            C("the validator refuses a config without the section", okBefore && !v.Ok && v.Errors.Any(e => e.StartsWith("notifications is missing")), string.Join("; ", v.Errors.Take(2)));
            var schema = ConfigValidator.Schema().ToJsonString();
            C("the schema documents the section and every switch", new[] { "\"notifications\"", "\"crashes\"", "\"cooling\"", "\"devices\"" }.All(schema.Contains) && schema.Contains("Master switch"));
        }

        // ---- the service: crash watch is wired to the notifier (no-hardware rig, temp config folder, no tray icon)
        {
            var cfg = GlintConfig.Defaults();
            using var svc = new GlintService(cfg, new Rig(cfg, useLhm: false, hardware: false)) { NoSave = true };
            var shown = new List<Notice>(); svc.Notify.Attach(shown.Add);
            const string ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            string Xml(long rec, string time) => $"<Event xmlns=\"{ns}\"><System><Provider Name=\"Microsoft-Windows-WHEA-Logger\"/><EventID>19</EventID><TimeCreated SystemTime=\"{time}\"/><EventRecordID>{rec}</EventRecordID><Channel>System</Channel></System><EventData><Data Name=\"ApicId\">9</Data></EventData></Event>";
            long id = 900_000 + Environment.ProcessId;
            svc.Crashes.Process(CrashEvents.Parse(Xml(id, "2026-01-01T10:00:00.0000000Z"))!);
            svc.Crashes.Process(CrashEvents.Parse(Xml(id + 1, "2026-01-01T10:00:01.0000000Z"))!);
            C("a crash seen by crash watch becomes one Windows notification (a burst on the same core is not repeated)",
                shown.Count == 1 && shown[0] is { Category: "crashes", Title: "CPU hardware error detected", Shown: true } && shown[0].Text.Contains("core 4"), $"{shown.Count}: {shown.FirstOrDefault()?.Text}");
            cfg.Notifications.Crashes = false;
            svc.Notify.Post(NoticeText.Crash(new CrashRecord(1, "System", DateTime.Now, CrashEvents.UnexpectedShutdown, "unknown", "Unexpected shutdown", null, null, null, null, "", false, null), new CrashAction(CrashDecision.None, "x", false)));
            C("the service's notifier follows the live config (crashes switched off)", shown.Count == 1 && svc.Notify.Recent[^1] is { Shown: false, Muted: "crashes notifications are off" });
            var draft = svc.Notify.Post(NoticeText.TestNotice());
            C("the test notice goes out through the service's notifier", draft is { Shown: true } && shown.Count == 2 && shown[^1].Title == "Glint notifications work");
        }
    }
}
