using System.Text.Json;
using Glint.Core;

namespace Glint.Tuning;

/// <summary>Offline checks for <see cref="TuningService"/> on a <see cref="SimulatedGpu"/>: the safety rails, the crash
/// record, the proof rule and startup behaviour. No hardware, no real config folder.</summary>
public static class TuningTests
{
    public static void Run(Action<string, bool, string> check)
    {
        void C(string n, bool p, string d = "") => check("tuning.service: " + n, p, d);
        var dir = Path.Combine(Path.GetTempPath(), "GlintTuningTest_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = GlintConfig.Defaults();
            var procs = new List<string>();
            var resets = new List<string>();
            int? boost = -1; var boosts = new List<int?>();
            var stresses = new List<SimulatedStress>();
            (TuningService svc, SimulatedGpu gpu) Make(SimConfig sim, string state = "state.json")
            {
                var g = new SimulatedGpu(sim);
                var s = new TuningService(() => cfg, () => g, x => { var st = new SimulatedStress((SimulatedGpu)x); stresses.Add(st); return st; }, () => null,
                    b => { boost = b; boosts.Add(b); }, _ => resets.ToList(), () => procs, Path.Combine(dir, state));
                return (s, g);
            }
            object? Result(TuningService s) { s.Wait(25); return s.LastResult; }
            GpuTestRecord? Rec(TuningService s) => Result(s) as GpuTestRecord;
            string Err(Action a) { try { a(); return ""; } catch (Exception e) { return e.Message; } }

            // ---- off by default: nothing may run
            var (s0, g0) = Make(new SimConfig { UnstableCore = 200 });
            C("tuning off by default", !cfg.Tuning.Enabled);
            C("tests refused while tuning is off", Err(() => s0.StartGpuTest(50, 0, 10, false)).Contains("tuning is off") && g0.SetCalls == 0);
            cfg.Tuning.Enabled = true;

            // ---- workstation gate
            var (sw, _) = Make(new SimConfig { Name = "NVIDIA RTX PRO 6000 Blackwell Workstation Edition" }, "ws.json");
            C("workstation GPU blocked without opt-in", Err(() => sw.StartGpuTest(50, 0, 10, false)).Contains("workstation"));
            cfg.Tuning.AllowWorkstation = true;
            C("workstation GPU allowed after opt-in", Err(() => sw.StartGpuTest(50, 0, 10, false)) == "" && Rec(sw)?.Ok == true);
            cfg.Tuning.AllowWorkstation = false;

            // ---- conflicting overclocking tools
            procs.Add("MSIAfterburner");
            C("refused while MSI Afterburner runs", Err(() => s0.StartGpuTest(50, 0, 10, false)).Contains("MSIAfterburner"));
            procs.Clear();

            // ---- limits from the driver range, never past the hard caps
            var (sr, _) = Make(new SimConfig { CoreRange = new(-100, 200), MemRange = new(-100, 1000) }, "range.json");
            C("core above the card's range refused", Err(() => sr.StartGpuTest(250, 0, 10, false)).Contains("0..200"));
            C("memory above the card's range refused", Err(() => sr.StartGpuTest(0, 600, 10, false)).Contains("0..500"));

            // ---- pass / fail, back to stock, history, fan boost
            boosts.Clear();
            s0.StartGpuTest(150, 0, 20, false);
            var pass = Rec(s0);
            C("stable setting passes", pass is { Ok: true }, pass?.Why ?? "no result");
            C("card back at stock after a test", g0.GetOffset(ClockKind.Core) == 0 && g0.GetOffset(ClockKind.Memory) == 0);
            C("fans boosted during the test, released after", boosts.Contains(85) && boost == null, string.Join(",", boosts));
            C("stress test (CUDA context + VRAM) released after the job", stresses.Count > 0 && stresses.All(x => x.Disposed), $"{stresses.Count(x => x.Disposed)}/{stresses.Count} released");
            s0.StartGpuTest(250, 0, 20, false);
            var fail = Rec(s0);
            C("unstable setting fails", fail is { Ok: false }, fail?.Why ?? "no result");
            C("both tests in the history", s0.State.History.Count(h => h.Core is 150 or 250) == 2);
            C("history survives a restart", JsonSerializer.Deserialize<TuningState>(File.ReadAllText(Path.Combine(dir, "state.json")), GlintConfig.Json)!.History.Count >= 2);

            // ---- every AI run ends with a saved result: the best full test of the run, lowest offsets within 1%
            {
                var (sb, gb) = Make(new SimConfig { UnstableCore = 400 }, "best.json");
                var t0 = DateTime.Now.AddMinutes(-1);
                sb.State.History.Add(new GpuTestRecord(t0.AddMinutes(-10), 300, 900, 60, false, true, null, 3200, 70, 80, 300, 900, gb.Driver));   // before the run
                sb.State.History.Add(new GpuTestRecord(DateTime.Now, 150, 500, 60, false, true, null, 2990, 70, 80, 300, 900, gb.Driver));
                sb.State.History.Add(new GpuTestRecord(DateTime.Now, 200, 750, 60, false, true, null, 3000, 70, 80, 300, 900, gb.Driver));
                sb.State.History.Add(new GpuTestRecord(DateTime.Now, 250, 750, 60, false, true, null, 3010, 70, 80, 300, 900, gb.Driver));
                sb.State.History.Add(new GpuTestRecord(DateTime.Now, 300, 1000, 30, true, true, null, 3100, 70, 80, 300, 900, gb.Driver));   // light: not proof
                sb.State.History.Add(new GpuTestRecord(DateTime.Now, 350, 1000, 60, false, false, "wrong results", 3150, 70, 80, 300, 900, gb.Driver));
                var bestNote = sb.SaveBestFromRun("Balanced (tuned)", t0);
                var saved = sb.State.Profiles.GetValueOrDefault("Balanced (tuned)");
                C("run end: Glint saves the best full test of the run into its profile (within 1% of the best clock, lowest offsets)",
                    bestNote != null && saved is { Core: 150, MemAb: 500 }, bestNote ?? "nothing saved");
                C("run end: a result the AI already saved during the run is left alone", sb.SaveBestFromRun("Balanced (tuned)", t0) == null);
                C("run end: nothing to save when the run had no passing full test", sb.SaveBestFromRun("Quiet (tuned)", DateTime.Now.AddSeconds(5)) == null && !sb.State.Profiles.ContainsKey("Quiet (tuned)"));
                // ranking by work done: a higher clock with less work loses
                var (rK, rKg) = Make(new SimConfig(), "rank.json");
                rK.State.History.Add(new GpuTestRecord(DateTime.Now, 250, 0, 60, false, true, null, 3080, 70, 80, 500, 1400, rKg.Driver, Rate: 27000));
                rK.State.History.Add(new GpuTestRecord(DateTime.Now, 350, 2500, 60, false, true, null, 3210, 70, 80, 510, 1450, rKg.Driver, Rate: 24800));
                rK.SaveBestFromRun("Balanced (tuned)", t0);
                C("run end: the pick is by work done (compute, then bandwidth), not by the clock reading", rK.State.Profiles.GetValueOrDefault("Balanced (tuned)") is { Core: 250, MemAb: 0 });

                // back-to-back check against stock
                cfg.Tuning.AllowWorkstation = true;
                var (sv, gv) = Make(new SimConfig(), "verify.json"); sv.VerifySeconds = 10; sv.VerifyCoolSec = 0;
                sv.State.History.Add(new GpuTestRecord(DateTime.Now, 300, 1000, 60, false, true, null, 3100, 70, 80, 500, 1500, gv.Driver));
                sv.SaveResult(300, 1000, "Balanced (tuned)", force: true);
                sv.StartVerify("Balanced (tuned)"); sv.Wait(25);
                var vb = sv.State.GpuBench.GetValueOrDefault("Balanced (tuned)");
                C("verify: a real gain keeps the result, stock and tuned stored with the gain", vb is { Stock: not null, Tuned: { Core: 300 }, Gain: > 1.5 } && sv.State.Profiles["Balanced (tuned)"] is { Core: 300 }, $"{vb?.Gain}");
                var gf = sv.NotesFor("Balanced (tuned)") ?? "";
                C("profile notes: a profile tuned without a record still shows what it sets (the saved GPU overclock and its back-to-back result)",
                    gf.Contains("**GPU overclock:** stock → core +300 MHz") && gf.Contains("**Result** (back to back with stock)") && gf.Contains("compute +") && sv.NotesFor("no such profile") == null, gf);
                var (vP, vPg) = Make(new SimConfig { PowerCapCore = 0 }, "verify2.json"); vP.VerifySeconds = 10; vP.VerifyCoolSec = 0;
                vP.State.History.Add(new GpuTestRecord(DateTime.Now, 300, 3000, 60, false, true, null, 3100, 70, 80, 500, 1500, vPg.Driver));
                vP.SaveResult(300, 3000, "Balanced (tuned)", force: true);
                vP.StartVerify("Balanced (tuned)"); vP.Wait(25);
                var pb2 = vP.State.GpuBench.GetValueOrDefault("Balanced (tuned)");
                C("verify: a power-capped card that gains nothing loses the overclock (lowered first, then removed, with a reason)",
                    !vP.State.Profiles.ContainsKey("Balanced (tuned)") && pb2 is { Tuned: null, Note: not null } && vP.State.History.Count(h => h.Time >= t0) >= 4, $"{vP.State.History.Count} {System.Text.Json.JsonSerializer.Serialize(vP.LastResult)} {vP.Progress}");
                var (vM, vMg) = Make(new SimConfig { PowerCapCore = 10 }, "verify3.json"); vM.VerifySeconds = 10; vM.VerifyCoolSec = 0; vM.MinGain = 0.005;
                vM.State.History.Add(new GpuTestRecord(DateTime.Now, 300, 3000, 60, false, true, null, 3100, 70, 80, 500, 1500, vMg.Driver));
                vM.SaveResult(300, 3000, "Balanced (tuned)", force: true);
                vM.StartVerify("Balanced (tuned)"); vM.Wait(25);
                C("verify: memory that costs compute on a power-limited card is lowered until it pays off", vM.State.Profiles.GetValueOrDefault("Balanced (tuned)") is { Core: 300, MemAb: > 0 and < 3000 } && vM.State.GpuBench["Balanced (tuned)"].Gain > 0.5,
                    $"{vM.State.Profiles.GetValueOrDefault("Balanced (tuned)")} {System.Text.Json.JsonSerializer.Serialize(vM.LastResult)[..Math.Min(900, System.Text.Json.JsonSerializer.Serialize(vM.LastResult).Length)]}");
                // efficiency mode: judged on work per watt; too slow = more power
                var (vE, vEg) = Make(new SimConfig(), "verify4.json"); vE.VerifySeconds = 10; vE.VerifyCoolSec = 0; vE.GpuMode = "efficiency";
                vE.State.History.Add(new GpuTestRecord(DateTime.Now, 300, 0, 60, false, true, null, 3100, 70, 80, 500, 1500, vEg.Driver));
                vE.SaveResult(300, 0, "Balanced (tuned)", force: true, powerPct: 70);
                vE.StartVerify("Balanced (tuned)"); vE.Wait(25);
                var eb = vE.State.GpuBench.GetValueOrDefault("Balanced (tuned)");
                C("efficiency verify: a lower power limit kept for much more work per watt at near-stock speed, saved with the profile",
                    eb is { Tuned.PowerPct: 70, Gain: > 10 } && vE.State.Profiles["Balanced (tuned)"].PowerPct == 70 && eb.Tuned.PowerW < eb.Stock!.PowerW, $"{eb?.Gain} {eb?.Tuned?.PowerW} {eb?.Stock?.PowerW}");
                var (vS, vSg) = Make(new SimConfig(), "verify5.json"); vS.VerifySeconds = 10; vS.VerifyCoolSec = 0; vS.GpuMode = "efficiency";
                vS.SaveResult(0, 0, "Balanced (tuned)", force: true, powerPct: 55);
                vS.StartVerify("Balanced (tuned)"); vS.Wait(25);
                C("efficiency verify: more than 10% slower than stock = the power limit is raised until it isn't", vS.State.Profiles.GetValueOrDefault("Balanced (tuned)") is { PowerPct: > 55 and <= 85 } && vS.State.GpuBench["Balanced (tuned)"].Gain > 5,
                    $"{vS.State.Profiles.GetValueOrDefault("Balanced (tuned)")} {System.Text.Json.JsonSerializer.Serialize(vS.LastResult)[..Math.Min(300, System.Text.Json.JsonSerializer.Serialize(vS.LastResult).Length)]}");
                C("efficiency: a power limit outside 50-100% is refused", Err(() => vS.StartGpuTest(0, 0, 10, false, 30)).Contains("50..100") && Err(() => vS.SaveResult(0, 0, "x", true, 120)).Contains("50..100"));
                C("verify: refused when the profile has no overclock", Err(() => vM.StartVerify("Nope")).Contains("no GPU overclock"));
                cfg.Tuning.AllowWorkstation = false;
                sb.SaveCpu("Balanced (tuned)", new[] { -20, -18 });
                sb.StartNotes("Balanced (tuned)", "balanced", "all", DateTime.Now); sb.EditNotes("Balanced (tuned)", n => n.Ai = "- **GPU core offset:** +0 → +120.");
                sb.EditNotes("Balanced (tuned)", n => n.Cpu = "**CPU undervolt:** BIOS → **−20**");
                var ntxt = TuningService.NotesText(sb.State.Notes["Balanced (tuned)"]);
                sb.RenameProfile("Balanced (tuned)", "Gaming");
                C("profile notes: the AI's summary and Glint's later CPU result are kept together with the profile, and follow a rename",
                    ntxt == "- **GPU core offset:** +0 → +120.\n\n**CPU undervolt:** BIOS → **−20**" && sb.State.Notes.ContainsKey("Gaming") && !sb.State.Notes.ContainsKey("Balanced (tuned)"), ntxt ?? "");
                sb.StartNotes("Gaming", "balanced", "gpu", DateTime.Now);
                C("profile notes: a new tune of the profile starts a fresh record", TuningService.NotesText(sb.State.Notes["Gaming"]) == null);
                sb.EditNotes("Gaming", n => n.Ai = "- **GPU core offset:** +0 → +120.");
                C("profiles: GPU and CPU results follow a rename", sb.State.Profiles.ContainsKey("Gaming") && sb.CpuFor("Gaming")!.SequenceEqual(new[] { -20, -18 }) && sb.CpuFor("Balanced (tuned)") == null);
                C("profiles: a positive CPU value is never stored", Err(() => sb.SaveCpu("Gaming", new[] { 3, -1 })) != "" && sb.CpuFor("Gaming")![0] == -20);
                sb.SaveCpuBench("Gaming", new CpuBench(DateTime.Now, 9e10, 7e9, 5100, 88, 180), tuned: false);
                sb.SaveCpuBench("Gaming", new CpuBench(DateTime.Now, 9.3e10, 7.2e9, 5250, 83, 175), tuned: true);
                C("profiles: CPU benchmark keeps stock and tuned side by side; a new stock run clears the old tuned one",
                    sb.State.CpuBench["Gaming"] is { Stock.AllCore: 9e10, Tuned.AllCore: 9.3e10 } && Err(() => { sb.SaveCpuBench("Gaming", new CpuBench(DateTime.Now, 9.1e10, 7e9, 5100, 88, 180), tuned: false); }) == "" && sb.State.CpuBench["Gaming"].Tuned == null);
                sb.SaveCpu("Gaming", new[] { -25, -25 }); sb.MarkCpuApplied("Gaming");
                var n1 = sb.OnCpuCrash(true); var skipClean = sb.OnCpuCrash(false); var n2c = sb.CpuFor("Gaming"); var n2 = sb.OnCpuCrash(true);
                C("cpu crash counter: a crash with the undervolt applied skips it one session (not counted either way); a second crash with it applied removes it",
                    n1!.Contains("1/2") && skipClean == null && n2c != null && n2!.Contains("removed") && sb.CpuFor("Gaming") == null, (n1 ?? "") + " | " + (n2 ?? ""));
                sb.SaveCpu("Gaming", new[] { -25, -25 }); sb.MarkCpuApplied("Gaming");
                var c1 = sb.OnCpuCrash(true); var c2 = sb.OnCpuCrash(true); var c3 = sb.OnCpuCrash(false);
                C("cpu crash counter: a crash while the undervolt was skipped (another cause) isn't counted against it", c1!.Contains("1/2") && c2!.Contains("isn't counted") && c3 == null && sb.CpuFor("Gaming") != null && sb.State.CpuCrashes == 1, $"{c1} | {c2} | {sb.State.CpuCrashes}");
                sb.OnCpuCrash(false);
                C("cpu crash counter: a clean start with it applied resets it", sb.State.CpuCrashes == 0 && sb.OnCpuCrash(true)!.Contains("1/2") && sb.CpuFor("Gaming") != null);
                sb.OnCpuCrash(false);
                sb.State.AiProfiles["Gaming"] = "balanced"; sb.State.AiPreviousProfile = "Gaming"; sb.MarkCpuApplied("Gaming");
                sb.RenameProfile("Gaming", "Night");
                C("profiles: a rename moves AI ownership, the pre-tune profile and the applied-undervolt marker too (crash watch still finds it)",
                    sb.State.AiProfiles.ContainsKey("Night") && !sb.State.AiProfiles.ContainsKey("Gaming") && sb.State.AiPreviousProfile == "Night" && sb.State.CpuAppliedProfile == "Night");
                sb.RenameProfile("Night", "Gaming");
                sb.ClearProfile("Gaming");
                C("profiles: clearing a profile removes its GPU and CPU results", !sb.State.Profiles.ContainsKey("Gaming") && sb.CpuFor("Gaming") == null);
            }

            // ---- proof rule
            C("apply refused above a proven setting", Err(() => s0.Apply(200, 0)).Contains("test it first"));
            C("apply allowed at or below a proven setting", Err(() => s0.Apply(120, 0)) == "" && g0.GetOffset(ClockKind.Core) == 120);
            C("save refused for an unproven setting", Err(() => s0.SaveResult(300, 0)) != "");
            C("save allowed for a proven setting", Err(() => s0.SaveResult(150, 0)) == "" && s0.ActiveSaved?.Core == 150);
            s0.Stock();
            C("stock resets the card", g0.GetOffset(ClockKind.Core) == 0);

            // ---- a crash mid-test is recorded as a failure on the next start
            var st = JsonSerializer.Deserialize<TuningState>(File.ReadAllText(Path.Combine(dir, "state.json")), GlintConfig.Json)!;
            st.Testing = new GpuTestRecord(DateTime.Now, 180, 0, 60, false, false, null, null, null, null, null, null, "999.99");
            File.WriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(st, GlintConfig.Json));
            var (s1, g1) = Make(new SimConfig { UnstableCore = 200 });
            var note = s1.OnStartup(lastShutdownWasCrash: true);
            C("unfinished test recorded as unstable after a crash", s1.State.History.Any(h => h.Core == 180 && !h.Ok && h.Why!.Contains("crashed")) && s1.State.Testing == null, note ?? "");

            // ---- the active profile re-applies at startup (no setting): skipped after a crash, on a driver change, after a revert
            cfg.Tuning.ApplyAtStartup = false;   // the old setting no longer matters
            C("startup apply skipped after a crash", s1.OnStartup(true)?.Contains("crash") == true && g1.GetOffset(ClockKind.Core) == 0);
            C("startup apply when safe, without any setting", s1.OnStartup(false) == null && g1.GetOffset(ClockKind.Core) == 150);
            s1.Stock("3 GPU crashes in 7 days");
            C("startup apply skipped after crash watch reverted it", s1.OnStartup(false)?.Contains("crashes") == true && g1.GetOffset(ClockKind.Core) == 0);
            var st2 = s1.State; st2.RevertedReason = null; st2.Profiles["Default"] = st2.Profiles["Default"] with { Driver = "111.11" };
            C("startup apply skipped after a driver change", s1.OnStartup(false)?.Contains("driver") == true && g1.GetOffset(ClockKind.Core) == 0);

            // ---- temperatures: the card's own throttle point is a performance limit, only an emergency stops a test
            cfg.Tuning.EmergencyCoreC = 75;
            var (sh, gh) = Make(new SimConfig { AmbientC = 50, SlowdownC = null }, "hot.json");
            sh.StartGpuTest(50, 0, 20, false);
            var hot = Rec(sh);
            C("emergency temperature stops the test (back to stock)", hot is { Ok: false } && hot.Why!.Contains("emergency") && gh.GetOffset(ClockKind.Core) == 0, hot?.Why ?? "");
            cfg.Tuning.EmergencyCoreC = 90;
            var (sth, _) = Make(new SimConfig { AmbientC = 45, SlowdownC = null }, "thermal.json");
            sth.StartGpuTest(50, 0, 20, false);
            var th = Rec(sth);
            C("reaching the throttle point passes and is flagged thermal-limited", th is { Ok: true, ThermalLimited: true }, th?.Why ?? $"thermal={th?.ThermalLimited}");
            var (ssd, _) = Make(new SimConfig { AmbientC = 45, SlowdownC = 80 }, "slowdown.json");
            ssd.StartGpuTest(50, 0, 20, false);
            var sd = Rec(ssd);
            C("emergency stop never more than 3 °C past the card's slowdown point", sd is { Ok: false } && sd.Why!.Contains("83"), sd?.Why ?? "");
            C("compute throughput recorded", th?.Rate is > 0, $"{th?.Rate}");

            // ---- power: unlocked to the card's own maximum during tests and while applied; default at stock
            var (sp, gp) = Make(new SimConfig { UnstableCore = 300 }, "power.json");
            sp.StartGpuTest(0, 0, 20, false);
            var stockRec = Rec(sp);
            sp.StartGpuTest(150, 0, 20, false);
            var pr = Rec(sp);
            C("power limit raised to the card's maximum during the test", pr?.PowerLimitW == 600, $"{pr?.PowerLimitW}");
            C("power limit back to default after the test", gp.PowerLimitNowW == 575, $"{gp.PowerLimitNowW}");
            sp.Apply(150, 0);
            C("power limit stays raised while an overclock is applied", gp.PowerLimitNowW == 600, $"{gp.PowerLimitNowW}");
            sp.Stock();
            C("stock restores the default power limit", gp.PowerLimitNowW == 575, $"{gp.PowerLimitNowW}");
            cfg.Tuning.UnlockPower = false;
            sp.StartGpuTest(100, 0, 20, false);
            C("power limit untouched when unlocking is off", Rec(sp)?.PowerLimitW == 575);
            cfg.Tuning.UnlockPower = true;
            var compact = new JsonSerializerOptions(GlintConfig.Json) { WriteIndented = false };
            var cmpJson = JsonSerializer.Serialize(sp.Status(), compact);
            C("status compares stock with the best tuned test", stockRec is { Ok: true } && cmpJson.Contains("\"compare\":{\"stock\":{") && cmpJson.Contains("\"tuned\":{"), cmpJson.Length > 0 ? cmpJson[..Math.Min(200, cmpJson.Length)] : "");
            var page = JsonSerializer.Serialize(sp.History(0, 2), compact);
            C("history pages newest first", page.Contains("\"total\":3") && page.Contains("\"limit\":2") && page.IndexOf("\"core\":100") < page.IndexOf("\"core\":150"), page[..Math.Min(200, page.Length)]);
            sth.Dispose(); ssd.Dispose(); sp.Dispose();

            // ---- per-profile overclocks, manual overclocking
            string active = "Quiet";
            var (sq, gq) = Make(new SimConfig { UnstableCore = 300 }, "profiles.json");
            sq.ActiveProfile = () => active;
            sq.StartGpuTest(200, 0, 20, false); Rec(sq);
            sq.SaveResult(120, 0);                                    // Quiet
            sq.SaveResult(200, 0, "Performance");
            active = "Performance"; sq.OnProfileChanged("Performance");
            C("switching profile applies that profile's overclock", gq.GetOffset(ClockKind.Core) == 200, $"{gq.GetOffset(ClockKind.Core)}");
            active = "Quiet"; sq.OnProfileChanged("Quiet");
            C("...and each profile keeps its own", gq.GetOffset(ClockKind.Core) == 120, $"{gq.GetOffset(ClockKind.Core)}");
            active = "Balanced"; sq.OnProfileChanged("Balanced");
            C("a profile without an overclock runs at stock", gq.GetOffset(ClockKind.Core) == 0);
            sq.SaveResult(120, 0, "Eff", powerPct: 60);
            bool unl = cfg.Tuning!.UnlockPower; cfg.Tuning.UnlockPower = false;   // the case that leaked: nothing raises the limit again
            active = "Eff"; sq.OnProfileChanged("Eff"); var effW = gq.PowerLimitNowW;
            active = "Quiet"; sq.OnProfileChanged("Quiet"); var afterW = gq.PowerLimitNowW;
            cfg.Tuning.UnlockPower = unl;
            var (pdef, _) = gq.PowerLimitW;
            C("an efficiency profile's lowered power limit doesn't carry over to a profile without one", effW is double e1 && Math.Abs(e1 - pdef * 0.6) < 1 && afterW is double a1 && Math.Abs(a1 - pdef) < 1 && gq.GetOffset(ClockKind.Core) == 120, $"eff {effW} after {afterW} default {pdef}");
            active = "Eff"; sq.SetStockMode(true); var asn = Err(() => sq.ApplySaved());
            C("apply saved works like a profile switch: the saved power limit too, and it leaves stock mode", asn == "" && !sq.StockMode && gq.PowerLimitNowW is double e2 && Math.Abs(e2 - pdef * 0.6) < 1, asn);
            active = "Quiet"; sq.OnProfileChanged("Quiet");
            C("untested manual apply refused without the user's confirmation", Err(() => sq.Apply(260, 0)).Contains("test it first"));
            C("confirmed manual apply works (hard caps still apply)", Err(() => sq.Apply(260, 0, force: true)) == "" && gq.GetOffset(ClockKind.Core) == 260);
            C("manual apply past the card's limits refused even when confirmed", Err(() => sq.Apply(9999, 0, force: true)).Contains("limits"));
            sq.Stock(); sq.Dispose();
            var legacy = new TuningState { Saved = new GpuSaved(90, 250, DateTime.Now, "999.99", "SIM.00.00", 90, 250) };
            File.WriteAllText(Path.Combine(dir, "legacy.json"), JsonSerializer.Serialize(legacy, GlintConfig.Json));
            var (sl, _) = Make(new SimConfig(), "legacy.json");
            sl.OnStartup(false);
            C("an old single saved result moves into the active profile", sl.ActiveSaved?.Core == 90 && sl.State.Profiles.ContainsKey("Default"));
            sl.Dispose();
            var (se, _) = Make(new SimConfig { EccSupported = true, EccAboveMem = 400 }, "ecc.json");
            se.StartGpuTest(0, 600, 20, false);
            var ecc = Rec(se);
            C("ECC-corrected VRAM errors fail the test", ecc is { Ok: false } && ecc.Why!.Contains("ECC"), ecc?.Why ?? "");
            resets.Add("event 153 nvlddmkm");
            s0.StartGpuTest(100, 0, 20, false);
            var tdr = Rec(s0);
            C("a driver reset during the test fails it", tdr is { Ok: false } && tdr.Why!.Contains("driver reset"), tdr?.Why ?? "");
            resets.Clear();
            s0.Dispose(); s1.Dispose(); sw.Dispose(); sr.Dispose(); sh.Dispose(); se.Dispose();

            // ---- AI tuner: what Claude Code is allowed to do
            var ai = new AiTuner(@"C:\Program Files\Glint\Glint.exe", () => new ClaudeCli("windows", @"C:\Users\x\.local\bin\claude.exe"));
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-should-not-leak");
            var psi = ai.BuildStart(new ClaudeCli("windows", @"C:\claude.exe"), "gpu", 30);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            var a = psi.ArgumentList.ToList();
            int ti = a.IndexOf("--tools");
            C("AI: no built-in tools (--tools \"\")", ti >= 0 && ti + 1 < a.Count && a[ti + 1] == "", string.Join(" ", a.Take(12)));
            C("AI: only Glint's MCP server", a.Contains("--strict-mcp-config") && a.Contains("--no-session-persistence") && a[a.IndexOf("--setting-sources") + 1] == "");
            var mcpJson = a[a.IndexOf("--mcp-config") + 1];
            C("AI: Glint MCP server restricted to tuning tools", mcpJson.Contains("\"--only\"") && mcpJson.Contains("\"tuning\""), mcpJson);
            var allowed = a[a.IndexOf("--allowedTools") + 1].Split(',');
            C("AI: allowed tools are tuning tools only", allowed.Length > 0 && allowed.All(t => t.StartsWith("mcp__glint__glint_tuning_")) && !allowed.Any(t => t.Contains("_ai_")), string.Join(",", allowed));
            C("AI: the user's own login (no API key passed on)", !psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
            C("MCP tuning filter excludes everything else", Cli.IsTuningTool("tuning.gpu.test") && !Cli.IsTuningTool("tuning.ai.start") && !Cli.IsTuningTool("config.set") && !Cli.IsTuningTool("app.restart"));
            var wslPsi = ai.BuildStart(new ClaudeCli("wsl", "/home/x/.local/bin/claude"), "gpu", 30);
            C("AI: WSL install runs through wsl.exe", wslPsi.FileName.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase) && wslPsi.ArgumentList[0] == "-e" && wslPsi.ArgumentList[1] == "/home/x/.local/bin/claude");
            ai.OnStream("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Testing core +100\"},{\"type\":\"tool_use\",\"name\":\"mcp__glint__glint_tuning_gpu_test\",\"input\":{\"core\":100,\"memAb\":0}}]}}");
            ai.OnStream("{\"type\":\"result\",\"is_error\":false,\"result\":\"Saved core +135 / memory +750\"}");
            ai.OnStream("not json");
            var st3 = JsonSerializer.Serialize(ai.Status());
            C("AI: transcript shows text and tool calls; summary from the result", st3.Contains("Testing core") && st3.Contains("gpu.test") && st3.Contains("Saved core"), st3[..Math.Min(300, st3.Length)]);

            // ---- tuning monitor window: events, the message it pushes, its one action
            var (sm, _) = Make(new SimConfig(), "monitor.json");
            var started = new List<string>();
            sm.JobStarted += n => started.Add(n);
            var cpuT = new CpuTuning(() => cfg, () => null);
            var aiM = new AiTuner(@"C:\x\Glint.exe", () => null);
            string Flat(object o) => System.Text.RegularExpressions.Regex.Replace(JsonSerializer.Serialize(o, GlintConfig.Json), @"\s+", "");
            var idleMsg = Flat(UI.TuningMonitor.Message(sm, aiM, cpuT));
            C("monitor: message carries status, ai and cpu", idleMsg.Contains("\"type\":\"tuning\"") && idleMsg.Contains("\"status\":{") && idleMsg.Contains("\"ai\":{") && idleMsg.Contains("\"cpu\":"), idleMsg[..Math.Min(160, idleMsg.Length)]);
            C("monitor: idle message has no test and no live sample", !idleMsg.Contains("\"testing\":{") && !idleMsg.Contains("\"live\":{"));
            sm.StartGpuTest(100, 0, 10, false);
            C("monitor: starting a GPU test raises JobStarted once", started.Count == 1 && started[0] == "gpu test", string.Join(",", started));
            var busyMsg = Flat(UI.TuningMonitor.Message(sm, aiM, cpuT));
            Result(sm);
            C("monitor: message survives a running or just-finished job", busyMsg.Contains("\"type\":\"tuning\"") && !busyMsg.Contains("\"error\""), busyMsg[..Math.Min(120, busyMsg.Length)]);
            int stops = 0; object StopAll() { stops++; aiM.Stop(); sm.Stop(); return new { }; }
            C("monitor: unknown actions are refused", !UI.TuningMonitor.Act("format", StopAll) && !UI.TuningMonitor.Act(null, StopAll) && stops == 0);
            C("monitor: stop stops everything and a later test still runs", UI.TuningMonitor.Act("stop", StopAll) && stops == 1 && Err(() => sm.StartGpuTest(100, 0, 10, false)) == "" && Rec(sm) != null);
            var hist = Flat(sm.History(0, 15));
            C("monitor: history page reports total and items", hist.Contains("\"total\":") && hist.Contains("\"items\":["), hist[..Math.Min(100, hist.Length)]);
            sm.Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
