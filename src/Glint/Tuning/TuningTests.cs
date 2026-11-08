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

            // ---- proof rule
            C("apply refused above a proven setting", Err(() => s0.Apply(200, 0)).Contains("test it first"));
            C("apply allowed at or below a proven setting", Err(() => s0.Apply(120, 0)) == "" && g0.GetOffset(ClockKind.Core) == 120);
            C("save refused for an unproven setting", Err(() => s0.SaveResult(300, 0)) != "");
            C("save allowed for a proven setting", Err(() => s0.SaveResult(150, 0)) == "" && s0.State.Saved?.Core == 150);
            s0.Stock();
            C("stock resets the card", g0.GetOffset(ClockKind.Core) == 0);

            // ---- a crash mid-test is recorded as a failure on the next start
            var st = JsonSerializer.Deserialize<TuningState>(File.ReadAllText(Path.Combine(dir, "state.json")), GlintConfig.Json)!;
            st.Testing = new GpuTestRecord(DateTime.Now, 180, 0, 60, false, false, null, null, null, null, null, null, "999.99");
            File.WriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(st, GlintConfig.Json));
            var (s1, g1) = Make(new SimConfig { UnstableCore = 200 });
            var note = s1.OnStartup(lastShutdownWasCrash: true);
            C("unfinished test recorded as unstable after a crash", s1.State.History.Any(h => h.Core == 180 && !h.Ok && h.Why!.Contains("crashed")) && s1.State.Testing == null, note ?? "");

            // ---- apply at startup: skipped after a crash, on a driver change, after a revert; applied otherwise
            cfg.Tuning.ApplyAtStartup = true;
            C("startup apply skipped after a crash", s1.OnStartup(true)?.Contains("crash") == true && g1.GetOffset(ClockKind.Core) == 0);
            C("startup apply when safe", s1.OnStartup(false) == null && g1.GetOffset(ClockKind.Core) == 150);
            s1.Stock("3 GPU crashes in 7 days");
            C("startup apply skipped after crash watch reverted it", s1.OnStartup(false)?.Contains("crashes") == true && g1.GetOffset(ClockKind.Core) == 0);
            var st2 = s1.State; st2.RevertedReason = null; st2.Saved = st2.Saved! with { Driver = "111.11" };
            C("startup apply skipped after a driver change", s1.OnStartup(false)?.Contains("driver") == true && g1.GetOffset(ClockKind.Core) == 0);
            cfg.Tuning.ApplyAtStartup = false;

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
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
