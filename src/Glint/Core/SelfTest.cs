using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glint.Devices;

namespace Glint.Core;

/// <summary>
/// Verification by measurement. Logic checks always run; --hw also drives the real hardware and
/// measures fan RPM response, lighting frame rate / cost, and the process's CPU + memory.
/// Writes a JSON report; exit code = number of failed checks.
/// </summary>
public static class SelfTest
{
    sealed record Check(string Name, bool Pass, string Detail);

    public static int Run(string outPath, bool hw)
    {
        var checks = new List<Check>();
        void C(string n, bool p, string d) => checks.Add(new Check(n, p, d));
        var metrics = new Dictionary<string, object>();
        // offline runs never touch the real config folder (plugins, backups, status go to a throwaway folder)
        if (!hw && Environment.GetEnvironmentVariable("GLINT_CONFIG_DIR") == null)
            Environment.SetEnvironmentVariable("GLINT_CONFIG_DIR", Path.Combine(Path.GetTempPath(), "GlintSelfTest_" + Environment.ProcessId));

        // ---------------- config validator
        {
            var good = GlintConfig.Defaults();
            var v0 = ConfigValidator.Validate(good);
            C("validator: default config is valid", v0.Count == 0, string.Join("; ", v0));
            var bad = GlintConfig.Defaults();
            bad.Curves["cpu-balanced"] = new() { new[] { 60.0, 30 }, new[] { 50.0, 40 } };
            bad.Profiles["Balanced"][0].MinDuty = 90; bad.Profiles["Balanced"][0].MaxDuty = 80;
            bad.Profiles["Balanced"][1].Inputs[0].Curve = "nope";
            bad.Lighting.Brightness = 101; bad.Lighting.Effect.Type = (EffectType)99; bad.Lighting.Effect.Colors = new() { "red" };
            bad.ActiveProfile = "Missing";
            var v1 = ConfigValidator.Validate(bad);
            string[] expect = { "strictly increase", "greater than maxDuty", "unknown curve \"nope\"", "lighting.brightness", "unknown effect type", "#rrggbb", "activeProfile" };
            C("validator: catches curve order, min>max, unknown curve, brightness, effect type, colour, active profile", expect.All(e => v1.Any(x => x.Contains(e))), string.Join("; ", expect.Where(e => !v1.Any(x => x.Contains(e)))));
            var withSensor = GlintConfig.Defaults(); withSensor.Profiles["Balanced"][0].Inputs[0].Sensor = "max(cpu,xt:ghost)";
            var ctx = new ValidationContext { Sensors = new[] { "cpu", "gpu" }, Fans = new[] { "st:pump" } };
            var v2 = ConfigValidator.Check(withSensor, ctx);
            C("validator: unknown sensor is an error, disconnected fan only a warning", v2.Errors.Any(e => e.Contains("xt:ghost")) && v2.Warnings.Any(w => w.Contains("not currently connected")) && !v2.Errors.Any(e => e.Contains("st:fan1")), string.Join("; ", v2.Errors));
            var v3 = ConfigValidator.Check(withSensor, new ValidationContext { Sensors = ctx.Sensors, Previous = withSensor });
            C("validator: a sensor the saved config already uses is not blocked", v3.Ok && v3.Warnings.Any(w => w.Contains("xt:ghost")), string.Join("; ", v3.Errors));
            var v4 = ConfigValidator.Check(bad, new ValidationContext { Previous = bad });
            C("validator: problems already in the saved config only warn", v4.Ok && v4.Warnings.Any(w => w.Contains("already in the current config")), string.Join("; ", v4.Errors));
        }

        // ---------------- in-game overlay (frame stats, items, validation, ETW gating, off-screen render)
        try { GameOverlay.OverlayTests.Checks(C); }
        catch (Exception e) { C("overlay: checks ran", false, e.ToString()); }

        // ---------------- fan engine
        var curve = new List<double[]> { new[] { 30.0, 20 }, new[] { 60.0, 50 }, new[] { 80.0, 100 } };
        C("curve below first point", FanEngine.Interpolate(curve, 10) == 20, $"{FanEngine.Interpolate(curve, 10)}");
        C("curve midpoint", Math.Abs(FanEngine.Interpolate(curve, 45) - 35) < 1e-9, $"{FanEngine.Interpolate(curve, 45)}");
        C("curve above last point", FanEngine.Interpolate(curve, 95) == 100, $"{FanEngine.Interpolate(curve, 95)}");
        var s = new Dictionary<string, double> { ["cpu"] = 50, ["gpu"] = 70 };
        C("resolve max()", FanEngine.Resolve("max(cpu,gpu)", s) == 70, "");
        C("resolve missing → NaN", double.IsNaN(FanEngine.Resolve("max(cpu,nope)", s)), "");

        GlintConfig Cfg(Combine comb, params (string sensor, double w)[] inputs)
        {
            var c = new GlintConfig { ActiveProfile = "t" };
            c.Curves["c"] = curve;
            c.Profiles["t"] = new() { new FanRule { Fans = new() { "f" }, Combine = comb, MinDuty = 0, RampUpPerSec = 1000, RampDownPerSec = 1000,
                Inputs = inputs.Select(i => new FanInput { Sensor = i.sensor, Curve = "c", Weight = i.w, Hysteresis = 0 }).ToList() } };
            return c;
        }
        // cpu 50 → 40%, gpu 70 → 75%
        int D(GlintConfig c, Dictionary<string, double> sens) => new FanEngine().Compute(c, sens, 1)["f"];
        C("multi-input max", D(Cfg(Combine.Max, ("cpu", 1), ("gpu", 1)), s) == 75, $"{D(Cfg(Combine.Max, ("cpu", 1), ("gpu", 1)), s)}");
        C("multi-input average", D(Cfg(Combine.Average, ("cpu", 1), ("gpu", 1)), s) == 58, $"{D(Cfg(Combine.Average, ("cpu", 1), ("gpu", 1)), s)}"); // 57.5 → 58
        C("multi-input weighted 3:1", D(Cfg(Combine.Weighted, ("cpu", 3), ("gpu", 1)), s) == 49, $"{D(Cfg(Combine.Weighted, ("cpu", 3), ("gpu", 1)), s)}"); // 48.75
        C("missing sensor → failsafe", D(Cfg(Combine.Max, ("cpu", 1), ("liquid", 1)), s) == 100, "");

        // LHM-driven fans (board headers, GPU fans): clamp to the control's range, keep-alive, hand back when unassigned / on exit
        {
            var log = new List<string>();
            var sf = new SoftwareFans(log.Add);
            var g1 = new FakeControl { Min = 30 }; var g2 = new FakeControl(); var bad = new FakeControl { Throw = true };
            sf.Add("gpu:Fan 1", g1); sf.Add("gpu:Fan 2", g2); sf.Add("mb:Fan #1", bad);
            sf.Apply(new Dictionary<string, int> { ["gpu:Fan 1"] = 10, ["gpu:Fan 2"] = 55, ["mb:Fan #1"] = 40, ["cc:fan1"] = 50 }, full: true);
            C("GPU fan duty clamped to the control's floor (10% → 30%), others sent as asked",
                g1.Value == 30 && g2.Value == 55 && g1.Mode == "software" && g2.Mode == "software", $"fan1 {g1.Value} {g1.Mode}, fan2 {g2.Value} {g2.Mode}");
            C("a failing control doesn't stop the others and isn't marked as held", !sf.Touched("mb:Fan #1") && sf.Touched("gpu:Fan 2") && log.Any(l => l.Contains("mb:Fan #1 set duty failed")),
                string.Join(" | ", log));
            int sets = g2.Sets;
            sf.Apply(new Dictionary<string, int> { ["gpu:Fan 1"] = 10, ["gpu:Fan 2"] = 55 }, full: false);
            int unchanged = g2.Sets - sets;
            sf.Apply(new Dictionary<string, int> { ["gpu:Fan 1"] = 10, ["gpu:Fan 2"] = 55 }, full: true);
            C("unchanged duty not re-sent until the keep-alive", unchanged == 0 && g2.Sets == sets + 1, $"re-sends without keep-alive {unchanged}, with {g2.Sets - sets}");
            sf.Apply(new Dictionary<string, int> { ["gpu:Fan 2"] = 60 }, full: false);
            C("fan dropped from every rule goes back to automatic (driver) control", g1.Mode == "default" && !sf.Touched("gpu:Fan 1") && g2.Mode == "software" && g2.Value == 60,
                $"fan1 {g1.Mode}, fan2 {g2.Mode} {g2.Value}");
            sf.Apply(new Dictionary<string, int> { ["gpu:Fan 1"] = 45, ["gpu:Fan 2"] = 60 }, full: false);
            C("re-assigned fan is taken back even when its duty matches the last one sent", g1.Mode == "software" && g1.Value == 45, $"fan1 {g1.Mode} {g1.Value}");
            sf.ReleaseAll();
            C("exit / crash hands every held fan back", g1.Mode == "default" && g2.Mode == "default" && !sf.Touched("gpu:Fan 2"), $"fan1 {g1.Mode}, fan2 {g2.Mode}");
            C("GPU fan names: \"GPU Fan 1\" → \"Fan 1\"", Rig.GpuFanName("GPU Fan 1") == "Fan 1" && Rig.GpuFanName("Fan") == "Fan" && Rig.GpuFanName("GPU") == "GPU",
                $"{Rig.GpuFanName("GPU Fan 1")}, {Rig.GpuFanName("Fan")}, {Rig.GpuFanName("GPU")}");
        }

        // Another program writing the same fan (Afterburner's curve...): detected from the read-back, Glint backs off
        {
            double now = 0;
            var log = new List<string>();
            var sf = new SoftwareFans(log.Add, c => ((FakeControl)c).Reported, () => now);
            var f = new FakeControl();
            sf.Add("gpu:Fan 1", f);
            var d40 = new Dictionary<string, int> { ["gpu:Fan 1"] = 40 };
            void Tick(double t, float? reported, Dictionary<string, int> duty, bool full = false) { now = t; f.Reported = reported; sf.Apply(duty, full, 100); }
            Tick(0, null, d40, true); Tick(6, 40, d40); Tick(7, 41, d40, true);
            C("fan conflict: read-back matching what Glint sent is fine", !sf.Contested.ContainsKey("gpu:Fan 1") && sf.Touched("gpu:Fan 1"), string.Join(" | ", log));
            Tick(8, 25, d40); bool afterOne = sf.Contested.ContainsKey("gpu:Fan 1");
            Tick(9, 41, d40); Tick(10, 25, d40); bool afterStale = sf.Contested.ContainsKey("gpu:Fan 1");
            C("fan conflict: one stray low reading is not a conflict", !afterOne && !afterStale, $"after one {afterOne}, after low-match-low {afterStale}");
            int sets = f.Sets;
            Tick(11, 25, d40, true);
            var why = sf.Contested.GetValueOrDefault("gpu:Fan 1") ?? "";
            C("fan conflict: another program holding the fan lower → handed back to automatic (never parked), no more writes or keep-alive",
                why.Contains("25%") && why.Contains("40%") && f.Mode == "default" && f.Sets == sets && !sf.Touched("gpu:Fan 1"), $"{why} | mode {f.Mode}, writes after {f.Sets - sets}");
            Tick(12, 25, d40, true);
            int quiet = f.Sets - sets;
            Tick(12.5, 25, new Dictionary<string, int> { ["gpu:Fan 1"] = 100 });
            bool fsOn = f.Mode == "software" && f.Value == 100;
            Tick(13, 25, d40);
            C("fan conflict: the fail-safe still wins on a contested fan, then it goes back to automatic", quiet == 0 && fsOn && f.Mode == "default" && sf.Contested.ContainsKey("gpu:Fan 1"),
                $"writes while contested {quiet}, fail-safe {fsOn}, after {f.Mode}");
            sets = f.Sets;
            Tick(13.5, 25, new Dictionary<string, int>()); Tick(14, 25, d40);
            C("fan conflict: out of every rule clears it; back in a rule, Glint takes it again", !sf.Contested.ContainsKey("gpu:Fan 1") && f.Sets == sets + 1 && f.Value == 40,
                $"contested {sf.Contested.ContainsKey("gpu:Fan 1")}, writes {f.Sets - sets}, value {f.Value}");

            // a card minimum (asks 10 %, runs 30 %) is steady → not a conflict; a value moving while Glint holds still is
            var d10 = new Dictionary<string, int> { ["gpu:Fan 1"] = 10 };
            Tick(20, 40, d10); for (int t = 26; t < 40; t++) Tick(t, 30, d10);
            C("fan conflict: hardware minimum (asked 10 %, steady 30 %) is not a conflict, it's noted once",
                !sf.Contested.ContainsKey("gpu:Fan 1") && log.Count(l => l.Contains("hardware's minimum")) == 1, string.Join(" | ", log.TakeLast(3)));
            Tick(40, 55, d10); Tick(41, 55, d10);
            C("fan conflict: level moving above what Glint sent → another program", sf.Contested.GetValueOrDefault("gpu:Fan 1")?.Contains("30% → 55%") == true,
                sf.Contested.GetValueOrDefault("gpu:Fan 1") ?? "(not contested)");

            // a duty jump: the fan's read-back lags behind for a few seconds; that's not a conflict
            var g = new FakeControl(); now = 100;
            var sf2 = new SoftwareFans(null, c => ((FakeControl)c).Reported, () => now);
            sf2.Add("gpu:Fan 1", g);
            var d100 = new Dictionary<string, int> { ["gpu:Fan 1"] = 100 };
            sf2.Apply(new Dictionary<string, int> { ["gpu:Fan 1"] = 30 }, true);
            now = 110; g.Reported = 30; sf2.Apply(d100, false);
            for (int t = 111; t < 115; t++) { now = t; g.Reported = 30 + (t - 110) * 15; sf2.Apply(d100, false); }
            now = 116; g.Reported = 100; sf2.Apply(d100, false);
            C("fan conflict: read-back lagging a duty jump (settle time) is not a conflict", sf2.Contested.Count == 0 && sf2.Touched("gpu:Fan 1"), string.Join(",", sf2.Contested.Values));
            // contested → retried after RetrySec
            g.Reported = 20; now = 122; sf2.Apply(d100, false); now = 123; sf2.Apply(d100, false);
            bool contested2 = sf2.Contested.Count == 1; int gs = g.Sets;
            now = 123 + SoftwareFans.RetrySec - 1; sf2.Apply(new Dictionary<string, int> { ["gpu:Fan 1"] = 70 }, false); bool stillOff = g.Sets == gs;
            now = 123 + SoftwareFans.RetrySec; sf2.Apply(new Dictionary<string, int> { ["gpu:Fan 1"] = 70 }, false);
            C("fan conflict: a contested fan is tried again after the retry time", contested2 && stillOff && sf2.Contested.Count == 0 && g.Value == 70 && g.Mode == "software",
                $"contested {contested2}, quiet before retry {stillOff}, after: {g.Mode} {g.Value}");
            // exit: release, and a fan-loop tick racing the exit can't take the fan again; a failed release stays held
            g.FailDefault = 1;
            sf2.ReleaseAll(); bool keptOnFail = sf2.Touched("gpu:Fan 1");
            sf2.ReleaseAll(); gs = g.Sets;
            sf2.Apply(new Dictionary<string, int> { ["gpu:Fan 1"] = 80 }, true);
            C("exit: a failed release stays held and is retried; after release no tick can take the fan again", keptOnFail && g.Mode == "default" && !sf2.Touched("gpu:Fan 1") && g.Sets == gs,
                $"kept on failure {keptOnFail}, mode {g.Mode}, writes after exit {g.Sets - gs}");
            var dc = new DriverContext { Config = GlintConfig.Defaults(), Log = _ => { } };
            C("device ids never take LHM fan prefixes (mb, psu, gpu, gpu2...)", dc.Unique("gpu") == "gpudev" && dc.Unique("gpu2") == "gpu2dev" && dc.Unique("mb") == "mbdev" && dc.Unique("gpuhub") == "gpuhub",
                $"{dc.Unique("psu")}");

            // global fan-control switch: releasable hubs + every LHM fan go back to their own control, the rest keep their curve
            {
                var rig = new Rig(GlintConfig.Defaults(), useLhm: false, hardware: false);
                var free = new FakeFanHub("free", canRelease: true); var tied = new FakeFanHub("tied", canRelease: false);
                rig.Controllers.Add(free); rig.Controllers.Add(tied);
                var gfan = new FakeControl();
                rig.Software.Add("gpu:Fan 1", gfan);
                foreach (var id in new[] { "free:fan1", "tied:fan1", "gpu:Fan 1" }) rig.Fans.Add(new FanOutput(id, id, "test"));
                Dictionary<string, int> All() => new() { ["free:fan1"] = 50, ["tied:fan1"] = 50, ["gpu:Fan 1"] = 50 };
                rig.ApplyDuty(All());
                bool heldBefore = rig.Software.Touched("gpu:Fan 1") && free.Duties == 1 && tied.Duties == 1;
                rig.SetFanControl(false); rig.SetFanControl(false);
                var duty = All(); duty["tied:fan1"] = 55; // a changed duty, so the hub that keeps its curve is written again
                var why2 = new Dictionary<string, string> { ["tied:fan1"] = "50% ← max[cpu]" };
                GlintService.FanControlOff(duty, why2, rig.Fans, rig.HandedBack);
                rig.ApplyDuty(duty);
                C("fan control off: releasable hub + GPU fan handed back (once), the hub that can't keeps its curve",
                    heldBefore && free.Releases == 1 && tied.Releases == 1 && free.Duties == 1 && tied.Duties == 2 && gfan.Mode == "default" && !rig.Software.Touched("gpu:Fan 1")
                    && !duty.ContainsKey("free:fan1") && !duty.ContainsKey("gpu:Fan 1") && duty.ContainsKey("tied:fan1"),
                    $"held {heldBefore}, releases {free.Releases}/{tied.Releases}, duties {free.Duties}/{tied.Duties}, gpu {gfan.Mode}, duty [{string.Join(",", duty.Keys)}]");
                C("fan control off: every fan says why", why2.GetValueOrDefault("free:fan1", "").StartsWith("fan control off: on its own") && why2.GetValueOrDefault("gpu:Fan 1", "").StartsWith("fan control off: on its own")
                    && why2.GetValueOrDefault("tied:fan1", "").Contains("can't hand its fans back") && why2["tied:fan1"].EndsWith("50% ← max[cpu]"), string.Join(" | ", why2.Select(k => $"{k.Key}: {k.Value}")));
                rig.ApplyDuty(new Dictionary<string, int> { ["free:fan1"] = 100, ["tied:fan1"] = 100 }); // the fan loop's error fail-safe names every hub fan
                C("fan control off: the error fail-safe doesn't take back a hub that runs its own fans", free.Duties == 1 && tied.Duties == 3, $"duties {free.Duties}/{tied.Duties}");
                rig.SetFanControl(true);
                rig.ApplyDuty(All());
                C("fan control on again: everything taken back at once", !rig.HandedBack("free:fan1") && free.Duties == 2 && tied.Duties == 4 && gfan.Mode == "software",
                    $"duties {free.Duties}/{tied.Duties}, gpu {gfan.Mode}");
                var fc = GlintConfig.Defaults(); fc.FanControl = false;
                var json = JsonSerializer.Serialize(fc, GlintConfig.Json);
                var back = JsonSerializer.Deserialize<GlintConfig>(json, GlintConfig.Json)!;
                C("fan control: on by default, saved as fanControl, valid, documented in the schema",
                    GlintConfig.Defaults().FanControl && json.Contains("\"fanControl\": false") && !back.FanControl && ConfigValidator.Validate(fc).Count == 0 && ConfigValidator.Schema().ToJsonString().Contains("fanControl"),
                    $"errors: {string.Join("; ", ConfigValidator.Validate(fc))}");
            }

            // built-in "Graphics card fans" entry: on by default, switched off through DisabledPlugins (like a plugin)
            {
                var off = GlintConfig.Defaults(); off.DisabledPlugins.Add(Rig.GpuFansBuiltin);
                bool onByDefault = new Rig(GlintConfig.Defaults(), useLhm: false, hardware: false).GpuFansOn;
                bool offWhenDisabled = !new Rig(off, useLhm: false, hardware: false).GpuFansOn;
                C("graphics-card fans: built in, on by default, off when switched off on the Plugins page", onByDefault && offWhenDisabled && Rig.GpuFansBuiltin == Path.GetFileName(Rig.GpuFansBuiltin),
                    $"default {onByDefault}, disabled→off {offWhenDisabled}");
            }

            // setup report: GPU tools only warn when Glint drives GPU fans; a contested fan is a failure with the reason
            var env = new ProcEnv { Procs = new[] { "MSIAfterburner" } };
            SetupItem? Item(SetupCheck.SetupInput i, string id) => SetupCheck.Run(i, env).Items.FirstOrDefault(x => x.Id == id);
            var idle = Item(new SetupCheck.SetupInput { Elevated = true, Ring0 = true, Cpu = 50 }, "gpufan:msi-afterburner");
            var busy = Item(new SetupCheck.SetupInput { Elevated = true, Ring0 = true, Cpu = 50, GpuFansInRules = new() { "gpu:Fan 1" } }, "gpufan:msi-afterburner");
            var taken = Item(new SetupCheck.SetupInput { Elevated = true, Ring0 = true, Cpu = 50, ContestedFans = new() { ["gpu:Fan 1"] = "another program set this fan to 25%" } }, "fanconflict:gpu:Fan 1");
            C("setup: Afterburner running → ok while Glint leaves GPU fans alone, warn once a rule drives them, fail for a taken-over fan",
                idle?.Status == "ok" && busy?.Status == "warn" && busy.Detail.Contains("gpu:Fan 1") && taken?.Status == "fail" && taken.Detail.StartsWith("Another program set"),
                $"idle {idle?.Status}, busy {busy?.Status}, taken {taken?.Status}: {taken?.Detail}");
        }

        // manual override (Home Assistant fan entity): holds its duty, "auto" returns to the curve, fail-safe beats it
        {
            var c = Cfg(Combine.Max, ("cpu", 1));
            var r = c.Profiles["t"][0];
            r.RampDownPerSec = 4; r.RampUpPerSec = 10; // overrides must not be ramp-limited, the curve still is
            var e = new FanEngine();
            int curveDuty = e.Compute(c, s, 1)["f"];                    // cpu 50 → 40%
            r.Override = 80;
            int ov = e.Compute(c, s, 1)["f"];
            r.Override = null;
            int back = e.Compute(c, new Dictionary<string, double> { ["cpu"] = 50 }, 1)["f"];
            C("fan override holds its duty (no ramp), auto returns to the curve", curveDuty == 40 && ov == 80 && back < 80 && back >= 40, $"curve {curveDuty}, override {ov}, after auto {back} (ramping down)");
            r.Override = 30;
            int lost = new FanEngine().Compute(c, new Dictionary<string, double> { ["gpu"] = 50 }, 1)["f"];
            int hot = new FanEngine().Compute(c, new Dictionary<string, double> { ["cpu"] = 85 }, 1)["f"];
            C("fail-safe beats a manual override (sensor lost → 100%, curve at 100% → 100%)", lost == 100 && hot == 100, $"lost {lost}, hot {hot}");
            r.Override = 10; r.MinDuty = 60;
            int floor = new FanEngine().Compute(c, s, 1)["f"];
            C("manual override is clamped to the rule's minimum (pumps)", floor == 60, $"{floor}");
        }

        // hysteresis: 70 → 68 holds, 65 drops
        {
            var c = Cfg(Combine.Max, ("cpu", 1));
            c.Profiles["t"][0].Inputs[0].Hysteresis = 3;
            var e = new FanEngine();
            int a = e.Compute(c, new Dictionary<string, double> { ["cpu"] = 70 }, 1)["f"], b = e.Compute(c, new Dictionary<string, double> { ["cpu"] = 68 }, 1)["f"], d = e.Compute(c, new Dictionary<string, double> { ["cpu"] = 64 }, 1)["f"];
            C("hysteresis holds within band", a == 75 && b == 75, $"{a},{b}");
            C("hysteresis releases below band", d < 75 && d > 60, $"{d}");
        }
        // ramp: 20 → 100 target with 10%/s up
        {
            var c = Cfg(Combine.Max, ("cpu", 1));
            c.Profiles["t"][0].RampUpPerSec = 10;
            var e = new FanEngine();
            e.Compute(c, new Dictionary<string, double> { ["cpu"] = 30 }, 1);
            int r1 = e.Compute(c, new Dictionary<string, double> { ["cpu"] = 79 }, 1)["f"];
            C("ramp limits rise", r1 == 30, $"{r1}");
            int r2 = e.Compute(c, new Dictionary<string, double> { ["cpu"] = 79 }, 1)["f"];
            C("ramp continues", r2 == 40, $"{r2}");
        }

        // ---------------- effects: every type renders, animated ones move, static ones don't, no allocation blow-up
        {
            var leds = new List<Led>();
            for (int z = 0; z < 6; z++) for (int i = 0; i < 8; i++) leds.Add(new Led(leds.Count, z, (z + 0.5) / 7, i / 8.0, true));
            for (int i = 0; i < 60; i++) leds.Add(new Led(leds.Count, 6, (6 + i / 59.0) / 7, i / 59.0, false));
            var L = leds.ToArray();
            var fx = new FxContext { Sensors = new Dictionary<string, double> { ["cpu"] = 60 }, Spectrum = Enumerable.Range(0, 16).Select(i => (float)Math.Abs(Math.Sin(i))).ToArray() };
            foreach (var t in Effect.All)
            {
                var e = Effect.Create(new EffectConfig { Type = t, Colors = new() { "#ff0000", "#00ff00", "#0000ff" }, Sensor = "cpu", Density = 1, Speed = 0.5 });
                var a = new Rgb[L.Length]; var b = new Rgb[L.Length];
                var sw = Stopwatch.StartNew();
                bool moved = false; int lit = 0;
                for (int f = 0; f < 120; f++)
                {
                    fx.Time = f / 30.0; fx.Dt = 1 / 30.0;
                    if (t is EffectType.Audio or EffectType.AudioPulse) fx.Spectrum = Enumerable.Range(0, 16).Select(i => (float)Math.Abs(Math.Sin(i + f * 0.7))).ToArray();
                    e.Render(fx, L, b);
                    if (f > 0 && !a.AsSpan().SequenceEqual(b)) moved = true;
                    lit = Math.Max(lit, b.Count(c => c.R + c.G + c.B > 30));
                    (a, b) = (b, a);
                }
                double usPerFrame = sw.Elapsed.TotalMilliseconds * 1000 / 120;
                bool expectStill = t is EffectType.Off or EffectType.Static or EffectType.Gradient;
                bool expectDark = t == EffectType.Off;
                C($"effect {t}", (moved != expectStill || t == EffectType.Temperature) && (expectDark ? lit == 0 : lit > 0) && usPerFrame < 2000,
                  $"moved={moved} maxLit={lit}/{L.Length} {usPerFrame:0}µs/frame");
            }
            var layered = Effect.Create(new EffectConfig { Type = EffectType.Static, Colors = new() { "#200000" }, Layers = new() { new() { Type = EffectType.Twinkle, Colors = new() { "#ffffff" }, Density = 1 } } });
            C("layered effect animates over static base", layered.Animated, "");
        }

        // ---------------- layout canvas: geometry, auto-arrange, and effects that follow it
        {
            var ring = Layout.Positions(new ZonePlacement { X = 0.4, Y = 0.4, W = 0.1, H = 0.16 }, Devices.ZoneKind.Ring, 8, 0);
            double cxr = 0.45 * Layout.Aspect, cyr = 0.48;
            var radii = ring.Select(p => Math.Sqrt(Math.Pow(p.x * Layout.Aspect - cxr, 2) + Math.Pow(p.y - cyr, 2))).ToArray();
            C("layout: ring LEDs on a true circle", radii.Max() - radii.Min() < 1e-9, $"r {radii.Min():0.0000}..{radii.Max():0.0000}");
            var strip = Layout.Positions(new ZonePlacement { X = 0.1, Y = 0.5, W = 0.4, H = 0.01 }, Devices.ZoneKind.Strip, 10, 0);
            var rot = Layout.Positions(new ZonePlacement { X = 0.1, Y = 0.5, W = 0.4, H = 0.01, Rotation = 90 }, Devices.ZoneKind.Strip, 10, 0);
            C("layout: strip is horizontal, rotated 90° it is vertical", strip.All(p => Math.Abs(p.y - strip[0].y) < 1e-9) && rot.All(p => Math.Abs(p.x - rot[0].x) < 1e-9) && strip[^1].x > strip[0].x,
              $"{strip[0]}→{strip[^1]} | {rot[0]}→{rot[^1]}");

            var zones = new List<LayoutZone>
            {
                new("aio:pump", "aio", "Pump", 24, Devices.ZoneKind.Ring, 0, ""),
            };
            for (int i = 1; i <= 3; i++) zones.Add(new($"aio:fan{i}", "aio", $"Fan {i}", 16, Devices.ZoneKind.Ring, 0, ""));
            for (int i = 1; i <= 4; i++) zones.Add(new($"hub:fan{i}", "hub", $"Fan {i}", 16, Devices.ZoneKind.Ring, 0, ""));
            zones.Add(new("dram:dimm1", "dram", "RAM 1", 8, Devices.ZoneKind.Stick, 0, "")); zones.Add(new("dram:dimm2", "dram", "RAM 2", 8, Devices.ZoneKind.Stick, 0, ""));
            zones.Add(new("board:onboard", "board", "Onboard", 4, Devices.ZoneKind.Single, 0, ""));
            for (int i = 1; i <= 2; i++) zones.Add(new($"board:argb{i}", "board", $"ARGB {i}", 30, Devices.ZoneKind.Strip, 0, ""));
            zones.Add(new("node:ch1", "node", "Ch 1", 40, Devices.ZoneKind.Strip, 0, ""));
            zones.Add(new("kbd:keys", "kbd", "Keyboard", 132, Devices.ZoneKind.Matrix, 22, "keyboard")); zones.Add(new("mouse:logo", "mouse", "Logo", 1, Devices.ZoneKind.Single, 0, "mouse"));
            var auto = Layout.AutoArrange(zones);
            C("auto-arrange places every zone inside the canvas", zones.All(z => auto.ContainsKey(z.Id) && auto[z.Id].X >= 0 && auto[z.Id].Y >= 0 && auto[z.Id].X + auto[z.Id].W <= 1 && auto[z.Id].Y + auto[z.Id].H <= 1), "");
            var bodies = zones.Where(z => z.Kind is Devices.ZoneKind.Ring or Devices.ZoneKind.Stick).Select(z => auto[z.Id]).ToList();
            int overlaps = 0;
            for (int i = 0; i < bodies.Count; i++) for (int j = i + 1; j < bodies.Count; j++)
                { var a = bodies[i]; var b = bodies[j]; if (a.X < b.X + b.W - 1e-6 && b.X < a.X + a.W - 1e-6 && a.Y < b.Y + b.H - 1e-6 && b.Y < a.Y + a.H - 1e-6) overlaps++; }
            C("auto-arrange: no fans / RAM overlap", overlaps == 0, $"{overlaps} overlaps");
            C("auto-arrange: pump on the CPU, radiator above it, peripherals on the desk",
              auto["aio:fan1"].Y < auto["aio:pump"].Y && auto["kbd:keys"].Y > 0.84 && auto["mouse:logo"].Y > 0.84 && auto["aio:pump"].X > 0.3 && auto["aio:pump"].X < 0.6,
              $"pump {auto["aio:pump"].X:0.00},{auto["aio:pump"].Y:0.00} keyboard y {auto["kbd:keys"].Y:0.00}");

            // effects read the canvas: same column ⇒ same colour for a 0° wave; same row ⇒ same colour at 90°
            var grid = new List<Led>();
            for (int yy = 0; yy < 5; yy++) for (int xx = 0; xx < 8; xx++) grid.Add(new Led(grid.Count, 0, (xx + 0.5) / 8, 0, false, (yy + 0.5) / 5));
            var G = grid.ToArray();
            Rgb[] Run(EffectConfig cfgFx) { var e = Effect.Create(cfgFx); var o = new Rgb[G.Length]; e.Render(new FxContext { Time = 1.3, Dt = 1 / 30.0 }, G, o); return o; }
            var h0 = Run(new EffectConfig { Type = EffectType.Wave, Angle = 0 });
            bool colsEqual = Enumerable.Range(0, 8).All(x => Enumerable.Range(1, 4).All(y => h0[y * 8 + x] == h0[x])), colsDiffer = h0[0] != h0[4];
            var v90 = Run(new EffectConfig { Type = EffectType.Wave, Angle = 90 });
            bool rowsEqual = Enumerable.Range(0, 5).All(y => Enumerable.Range(1, 7).All(x => v90[y * 8 + x] == v90[y * 8])), rowsDiffer = v90[0] != v90[2 * 8];
            C("effects follow the layout: 0° wave varies across X only", colsEqual && colsDiffer, "");
            C("effects follow the layout: 90° wave varies down Y only", rowsEqual && rowsDiffer, "");
            var rp = Run(new EffectConfig { Type = EffectType.Ripple, Center = new[] { 0.5, 0.5 }, Colors = new() { "#ff0000", "#00ff00" } });
            bool mirrored = Enumerable.Range(0, 5).All(y => Enumerable.Range(0, 8).All(x => rp[y * 8 + x] == rp[y * 8 + (7 - x)] && rp[y * 8 + x] == rp[(4 - y) * 8 + x]));
            int lit = rp.Count(c => c.R + c.G + c.B > 30);
            C("ripple is symmetric about its centre (and actually lit)", mirrored && lit >= 4, $"mirrored={mirrored}, lit {lit}/{rp.Length}");
            var fire = Run(new EffectConfig { Type = EffectType.Fire, Speed = 0.5 });
            double Lum(Rgb c) => c.R + c.G + c.B;
            C("fire burns from the bottom of the case up", Enumerable.Range(0, 8).Average(x => Lum(fire[4 * 8 + x])) > Enumerable.Range(0, 8).Average(x => Lum(fire[x])),
              $"bottom {Enumerable.Range(0, 8).Average(x => Lum(fire[4 * 8 + x])):0} vs top {Enumerable.Range(0, 8).Average(x => Lum(fire[x])):0}");

            // moving a zone in the layout moves its LEDs inside the real lighting engine
            var cfgL = GlintConfig.Defaults();
            var rigL = new Rig(cfgL, useLhm: false, hardware: false);
            rigL.Lights.Add(new FakeLight("fk", new LightZone("fk:fan1", "Fan", 8, Devices.ZoneKind.Ring)));
            using var eng = new LightingEngine(rigL, cfgL, () => new Dictionary<string, double>());
            double before = eng.LedsForTest.Average(l => l.X);
            cfgL.Lighting.Layout["fk:fan1"] = new ZonePlacement { X = 0.85, Y = 0.1, W = 0.08, H = 0.128 };
            eng.ApplyLayout();
            double after = eng.LedsForTest.Average(l => l.X);
            C("moving a zone on the layout moves its LEDs in the engine", Math.Abs(after - 0.89) < 0.01 && Math.Abs(after - before) > 0.1, $"{before:0.000} → {after:0.000}");
        }

        // ---------------- plugins (offline: bundled effects render, device plugins vs a fake HID port)
        {
            var dir = Plugins.PluginHost.BundledDir;
            var leds2 = new List<Led>();
            for (int z = 0; z < 6; z++) for (int i = 0; i < 8; i++) leds2.Add(new Led(leds2.Count, z, (z + 0.5) / 7, i / 8.0, true));
            for (int i = 0; i < 60; i++) leds2.Add(new Led(leds2.Count, 6, (6 + i / 59.0) / 7, i / 59.0, false));
            var L2 = leds2.ToArray();
            int effects = 0;
            foreach (var f in Directory.Exists(dir) ? Directory.GetFiles(Path.Combine(dir, "effects"), "*.js") : Array.Empty<string>())
            {
                var (ep, _) = Plugins.PluginHost.LoadForTest(f);
                if (ep == null) continue;
                effects++;
                var fx = new Plugins.PluginEffect(new EffectConfig { Type = EffectType.Plugin, Plugin = ep.Info.Name }, ep);
                var ctx = new FxContext { Sensors = new Dictionary<string, double> { ["cpu"] = 70 } };
                var a = new Rgb[L2.Length]; var b = new Rgb[L2.Length];
                bool moved = false; int lit = 0; var sw = Stopwatch.StartNew();
                for (int fr = 0; fr < 90; fr++)
                {
                    ctx.Time = fr / 30.0; ctx.Dt = 1 / 30.0;
                    fx.Render(ctx, L2, b);
                    if (fr > 0 && !a.AsSpan().SequenceEqual(b)) moved = true;
                    lit = Math.Max(lit, b.Count(c => c.R + c.G + c.B > 30));
                    (a, b) = (b, a);
                }
                double ms = sw.Elapsed.TotalMilliseconds / 90;
                C($"plugin effect {ep.Info.Name}", fx.Error == "" && moved && lit > 0 && ms < 4, $"moved={moved} lit={lit}/{L2.Length} {ms:0.00} ms/frame {fx.Error}");
            }
            C("bundled effect plugins found", effects >= 3, $"{effects}");

            // G213 device plugin: exact bytes on a fake port
            var g213 = Path.Combine(dir, "devices", "logitech-g213.js");
            if (File.Exists(g213))
            {
                var (_, def) = Plugins.PluginHost.LoadForTest(g213);
                Func<byte[], byte[]?> g213Sim = w =>
                {
                    var r = new byte[20]; r[0] = 0x11; r[1] = 0xff; r[2] = w[2]; r[3] = w[3];
                    if (w[2] == 0x00 && w[3] == 0x0d && w[4] == 0x80 && w[5] == 0x70) r[4] = 0x0c;   // measured on a real G213
                    return r;
                };
                var port = new FakePort(0x046d, 0xc336, 20, g213Sim);
                var found = def!.OpenWith(port, x => x, (z, n) => n, _ => { });
                var dev = found?.Lights.FirstOrDefault();
                C("G213 plugin: 1 zone of 5 LEDs", dev != null && dev.Zones.Sum(z => z.Leds) == 5, dev == null ? "init failed" : string.Join(",", dev.Zones.Select(z => $"{z.Id}={z.Leds}")));
                dev?.Write(new[] { new Rgb(1, 2, 3), new Rgb(4, 5, 6), new Rgb(7, 8, 9), new Rgb(10, 11, 12), new Rgb(13, 14, 15) });
                var expect = Convert.ToHexString(new byte[] { 0x11, 0xff, 0x0c, 0x3a, 3, 1, 7, 8, 9, 2 }.Concat(new byte[10]).ToArray());
                var frame = port.Writes.Skip(1).ToList();   // first write is the feature lookup
                C("G213 plugin: looks up its feature, then 5 packets with zone 3 byte-exact", Convert.ToHexString(port.Writes[0])[..12] == "11FF000D8070" && frame.Count == 5 && Convert.ToHexString(frame[2]) == expect,
                  frame.Count > 2 ? Convert.ToHexString(frame[2]) : $"{port.Writes.Count} writes");
                var small = Plugins.PluginHost.LoadForTest(g213).device!.OpenWith(new FakePort(0x046d, 0xc336, 8), x => x, (z, n) => n, _ => { });
                var big = Plugins.PluginHost.LoadForTest(g213).device!.OpenWith(new FakePort(0x046d, 0xc336, 64), x => x, (z, n) => n, _ => { });
                C("G213 plugin declines the wrong HID collections (8 / 64 byte)", small == null && big == null, "");
            }
            // template device plugin loads and describes itself
            var tpl = Path.Combine(AppContext.BaseDirectory, "plugin-templates", "device-template.js");
            if (File.Exists(tpl))
            {
                var port = new FakePort(0x1234, 0x5678, 65);
                var f = Plugins.PluginHost.LoadForTest(tpl).device!.OpenWith(port, x => x, (z, n) => n, _ => { });
                f?.Lights[0].Write(new Rgb[30]);
                C("device template works end-to-end", f?.Lights[0].Zones[0].Leds == 30 && port.Writes.Count == 1, $"{port.Writes.Count} writes");
            }
            string H(byte[] b, int n) => Convert.ToHexString(b, 0, Math.Min(n, b.Length));
            var devDir = Path.Combine(dir, "devices");
            Plugins.DevicePluginDef Def(string f) => Plugins.PluginHost.LoadForTest(Path.Combine(devDir, f)).device!;

            // ---- Corsair Commander ST (simulated exactly like the real hub answered)
            {
                var port = new FakePort(0x1b1c, 0x0c32, 65, CommanderSim(true, 65));
                var f = Def("corsair-commander-core.js").OpenWith(port, x => x, (z, n) => n, _ => { });
                var light = f?.Lights.FirstOrDefault(); var fans = f?.Fans.FirstOrDefault();
                C("Commander ST plugin: id st, pump+4 ring zones = 61 LEDs", light != null && light.Id == "st" && light.Zones.Sum(z => z.Leds) == 61 && light.Zones[0].Id == "st:pump" && light.Zones.All(z => z.Kind == ZoneKind.Ring),
                  light == null ? "init failed" : string.Join(",", light.Zones.Select(z => $"{z.Id}={z.Leds}")));
                C("Commander ST plugin: fan channels pump,fan1..4", fans != null && string.Join(",", fans.Channels.Select(c => c.Id)) == "pump,fan1,fan2,fan3,fan4" && fans.Channels[0].IsPump,
                  fans == null ? "" : string.Join(",", fans.Channels.Select(c => c.Id)));
                C("Commander ST plugin: 8 ms frame gap", light?.MinGapMs == 8, $"{light?.MinGapMs}");
                var t = new Dictionary<string, double>(); var r = new Dictionary<string, int>();
                fans?.Poll(t, r);
                C("Commander ST plugin: liquid 44.1 °C, pump 2600 rpm, fan1 1000 rpm", t.GetValueOrDefault("liquid") == 44.1 && r.GetValueOrDefault("pump") == 2600 && r.GetValueOrDefault("fan1") == 1000 && !t.ContainsKey("probe1"),
                  $"{string.Join(",", t)} | {string.Join(",", r)}");
                int before = port.Writes.Count;
                fans?.SetDuty(new Dictionary<string, int> { ["fan1"] = 40, ["pump"] = 70 });
                var duty = port.Writes.Skip(before).FirstOrDefault(w => w[2] == 0x06 && w[3] == 0x01);
                // native: [08][06 01][len=data+2 LE][00 00][07 00][count][ch,0,duty,0]... sorted by channel
                C("Commander ST plugin: duty packet byte-exact", duty != null && H(duty, 20) == "000806010B000000070002000046000100280000",
                  duty == null ? "none" : H(duty, 20));
                before = port.Writes.Count;
                light?.Write(Enumerable.Range(0, 61).Select(i => new Rgb((byte)i, 0, 0)).ToArray());
                var col = port.Writes.Skip(before).ToList();
                C("Commander ST plugin: colour frame = 06 00 header + 3 continuations (61-byte chunks)",
                  col.Count == 4 && H(col[0], 10) == "000806 00 B9000000 1200".Replace(" ", "") && col.Skip(1).All(w => w[2] == 0x07 && w[3] == 0x00),
                  $"{col.Count} packets, first {H(col.FirstOrDefault() ?? Array.Empty<byte>(), 12)}");
                (light as IDisposable)?.Dispose();
                C("Commander ST plugin: back to hardware mode on exit", port.Writes.Count > 0 && H(port.Writes[^1], 6) == "000801030001", H(port.Writes[^1], 6));
            }
            // ---- Corsair Commander Core XT
            {
                var port = new FakePort(0x1b1c, 0x0c2a, 385, CommanderSim(false, 385));
                var f = Def("corsair-commander-core.js").OpenWith(port, x => x, (z, n) => n, _ => { });
                var light = f?.Lights.FirstOrDefault(); var fans = f?.Fans.FirstOrDefault();
                C("Commander XT plugin: id xt, 6 fan rings of 8, 75 ms gap", light != null && light.Id == "xt" && light.Zones.Count == 6 && light.Zones.All(z => z.Leds == 8) && light.MinGapMs == 75,
                  light == null ? "init failed" : $"{string.Join(",", light.Zones.Select(z => $"{z.Id}={z.Leds}"))} gap {light.MinGapMs}");
                var t = new Dictionary<string, double>(); var r = new Dictionary<string, int>();
                fans?.Poll(t, r);
                C("Commander XT plugin: probes 31.0 / 34.1 °C, fan1..6 rpm", t.GetValueOrDefault("probe1") == 31.0 && t.GetValueOrDefault("probe2") == 34.1 && r.GetValueOrDefault("fan6") == 305 && fans!.Channels.Count == 6,
                  $"{string.Join(",", t)} | {string.Join(",", r)}");
                int before = port.Writes.Count;
                light?.Write(new Rgb[48]);
                C("Commander XT plugin: colour frame fits one 384-byte packet", port.Writes.Count - before == 1, $"{port.Writes.Count - before}");
                (light as IDisposable)?.Dispose();
            }
            // ---- ASUS Aura USB (AULA3-AR32, 2 board LEDs + 3 ARGB headers)
            {
                var table = new byte[60]; table[0x1b] = 2; table[0x02] = 3;
                Func<byte[], byte[]?> aura = w =>
                {
                    if (w[0] != 0xec) return null;
                    var r = new byte[65]; r[0] = 0xec;
                    if (w[1] == 0x82) { r[1] = 0x02; System.Text.Encoding.ASCII.GetBytes("AULA3-AR32-0309").CopyTo(r, 2); return r; }
                    if (w[1] == 0xb0) { r[1] = 0x30; table.CopyTo(r, 4); return r; }
                    return null;
                };
                var port = new FakePort(0x0b05, 0x19af, 65, aura);
                var f = Def("asus-aura-usb.js").OpenWith(port, x => x, (z, n) => n, _ => { });
                var light = f?.Lights.FirstOrDefault();
                C("Aura plugin: board(2, single) + 3 ARGB headers of 60", light != null && string.Join(",", light.Zones.Select(z => $"{z.Id}={z.Leds}")) == "aura:board=2,aura:argb1=60,aura:argb2=60,aura:argb3=60" && light.Zones[0].Kind == ZoneKind.Single,
                  light == null ? "init failed" : string.Join(",", light.Zones.Select(z => $"{z.Id}={z.Leds}")));
                var modes = port.Writes.Where(w => w[1] == 0x35).Select(w => w[2]).ToArray();
                C("Aura plugin: direct mode on effect channels 3,0,1,2 (AR32 numbering)", string.Join(",", modes) == "3,0,1,2", string.Join(",", modes));
                int before = port.Writes.Count;
                light?.Write(new Rgb[182]);
                var pk = port.Writes.Skip(before).ToList();
                C("Aura plugin: 10 direct packets, board apply flag 0x84, ARGB chunks of 20", pk.Count == 10 && H(pk[0], 5) == "EC40840002" && H(pk[1], 5) == "EC40000014" && pk[3][2] == 0x80,
                  $"{pk.Count}: {string.Join(" ", pk.Take(4).Select(x => H(x, 5)))}");
            }
            // ---- Corsair Lighting Node Pro
            {
                var port = new FakePort(0x1b1c, 0x0c0b, 65, w => w[1] == 0x02 ? new byte[] { 0, 0, 0, 10, 4 }.Concat(new byte[12]).ToArray() : null);
                var f = Def("corsair-lighting-node.js").OpenWith(port, x => x, (z, n) => n, _ => { });
                var light = f?.Lights.FirstOrDefault();
                C("Lighting Node plugin: 2 channels of 120", light != null && light.Id == "lnp" && light.Zones.Count == 2 && light.Zones.All(z => z.Leds == 120),
                  light == null ? "init failed" : string.Join(",", light.Zones.Select(z => $"{z.Id}={z.Leds}")));
                int before = port.Writes.Count;
                light?.Write(Enumerable.Range(0, 240).Select(i => new Rgb((byte)i, 1, 2)).ToArray());
                var pk = port.Writes.Skip(before).ToList();
                C("Lighting Node plugin: 21 packets (state, 9 planar, x2, commit), red plane exact", pk.Count == 21 && H(pk[0], 4) == "00380002" && H(pk[1], 9) == "003200003200000102" && H(pk[^1], 3) == "0033FF",
                  $"{pk.Count}: {H(pk.ElementAtOrDefault(1) ?? Array.Empty<byte>(), 9)}");
            }
            // ---- Corsair DRAM over a simulated SMBus (two DDR5 sticks at 0x19 / 0x1b, SPD at 0x50)
            {
                var bus = new FakeSmbus();
                foreach (byte a in new byte[] { 0x19, 0x1b }) bus.Regs[a] = new() { [0x43] = 0x1a, [0x44] = 0x04 };
                bus.Regs[0x50] = new() { [0x00] = 0x23 };
                bus.Info[2] = 0x01; bus.Info[3] = 0x06; bus.Info[28] = 4; bus.Info[8] = 9; bus.Info[9] = 1;
                byte crc = 0; foreach (var b in bus.Info) { crc ^= b; for (int k = 0; k < 8; k++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1); }
                bus.Regs[0x19][0x42] = crc; bus.Regs[0x1b][0x42] = crc;
                var addrs = Enumerable.Range(0x18, 8).Concat(Enumerable.Range(0x58, 8)).ToArray();
                var f = Def("corsair-dram.js").OpenSmbusWith(bus, addrs, x => x, (z, n) => n, _ => { });
                var ram = f?.Lights.FirstOrDefault();
                C("DRAM plugin: 2 sticks x 12 LEDs, stick zones, 90 ms gap", ram != null && ram.Id == "ram" && ram.Zones.Count == 2 && ram.Zones.All(z => z.Leds == 12 && z.Kind == ZoneKind.Stick) && ram.MinGapMs == 90,
                  ram == null ? "init failed" : string.Join(",", ram.Zones.Select(z => $"{z.Id}={z.Leds}")));
                C("DRAM plugin: never touched the SPD EEPROM", !bus.Writes.Any(w => w.StartsWith("50")), string.Join(" ", bus.Writes.Take(6)));
                bus.Writes.Clear();
                ram?.Write(Enumerable.Range(0, 24).Select(i => new Rgb((byte)(i + 1), 0, 0)).ToArray());
                var blk = bus.Writes.Where(w => w.StartsWith("19:")).ToList();
                // packet = [12][r,g,b x12 reversed][crc8]: first LED sent is the stick's last colour (12 = 0x0C)
                C("DRAM plugin: block 0x31 (32) + 0x32 (6), reversed order", blk.Count == 2 && blk[0].StartsWith("19:31#0C0C0000") && blk[1].StartsWith("19:32#") && blk[1].Length == 6 + 12,
                  string.Join(" ", blk.Select(b => b[..Math.Min(20, b.Length)])));
            }
            // ---- sensor-only plugin (no LEDs, no fans): its poll() must still be read
            {
                var tmpf = Path.Combine(Path.GetTempPath(), "glint-sensor-only.js");
                File.WriteAllText(tmpf, "module.exports = { type: 'device', id: 'probe', name: 'USB thermometer', vendorId: 0x1234, productIds: [0x0001], init(dev) { return {}; }, poll(dev) { return { temps: { water: 29.5, air: 24.0 } }; } };");
                var f = Plugins.PluginHost.LoadForTest(tmpf).device!.OpenWith(new FakePort(0x1234, 1, 8), x => x, (z, n) => n, _ => { });
                var ctl = f?.Fans.FirstOrDefault();
                var t = new Dictionary<string, double>(); var r = new Dictionary<string, int>();
                ctl?.Poll(t, r);
                C("sensor-only plugin is polled", ctl != null && ctl.Channels.Count == 0 && t.GetValueOrDefault("water") == 29.5 && t.GetValueOrDefault("air") == 24.0,
                  ctl == null ? "not registered" : string.Join(",", t));
            }

            // ---- SMBus safety is enforced by Glint, whatever the plugin does
            {
                var bus = new FakeSmbus();
                bus.Regs[0x20] = new() { [0x00] = 0x11 }; bus.Regs[0x50] = new() { [0x00] = 0x23 };
                string Try(string body)
                {
                    var tmpf = Path.Combine(Path.GetTempPath(), "glint-smbus-evil.js");
                    File.WriteAllText(tmpf, $"module.exports = {{ type: 'smbus', name: 'Evil', addresses: [0x20, 0x21], init(bus) {{ {body} return {{ zones: [{{ id: 'z', leds: 1 }}] }}; }} }};");
                    var logs = new List<string>();
                    var res = Plugins.PluginHost.LoadForTest(tmpf).device!.OpenSmbusWith(bus, new[] { 0x20, 0x21 }, x => x, (z, n) => n, logs.Add);
                    return res == null ? string.Join(" ", logs) : "ALLOWED";
                }
                string spd = Try("bus.readByte(0x50, 0);"), undeclared = Try("bus.readByte(0x30, 0);"), blind = Try("bus.writeByte(0x21, 0, 1);");
                C("SMBus guard: SPD EEPROM blocked", spd.Contains("SPD"), spd);
                C("SMBus guard: undeclared address blocked", undeclared.Contains("not in this plugin"), undeclared);
                C("SMBus guard: write before identify blocked", blind.Contains("identify"), blind);
                C("SMBus guard: identified address may be written", Try("if (bus.readByte(0x20, 0) < 0) return false; bus.writeByte(0x20, 1, 2);") == "ALLOWED" && bus.Writes.Contains("20:01=02"), string.Join(" ", bus.Writes));
                {   // a declined interface (e.g. the G213's other HID collection) must not keep the device id
                    var dc = new Devices.DriverContext { Config = new GlintConfig(), Log = _ => { } };
                    var declined = dc.Claim<object>(u => { u("g213"); return null; });
                    var accepted = dc.Claim<string>(u => u("g213"));
                    var second = dc.Claim<string>(u => u("g213"));
                    C("device id: a declined interface gives its id back", accepted == "g213" && second == "g2132", $"{accepted} {second}");
                }
            }

            // broken plugins must never take Glint down
            var tmp = Path.Combine(Path.GetTempPath(), "glint-plugin-test"); Directory.CreateDirectory(tmp);
            File.WriteAllText(Path.Combine(tmp, "bad-syntax.js"), "module.exports = { type: 'effect', render(ctx, leds, out) { for ( } };");
            string err = "";
            try { Plugins.PluginHost.LoadForTest(Path.Combine(tmp, "bad-syntax.js")); } catch (Exception ex) { err = Plugins.PluginHost.Clean(ex); }
            C("syntax error is reported, not thrown up", err.Length > 0, err);
            File.WriteAllText(Path.Combine(tmp, "loop.js"), "module.exports = { type: 'effect', name: 'Loop', render(ctx, leds, out) { while (true) {} } };");
            var (lp, _) = Plugins.PluginHost.LoadForTest(Path.Combine(tmp, "loop.js"));
            var lfx = new Plugins.PluginEffect(new EffectConfig { Type = EffectType.Plugin, Plugin = "Loop" }, lp!);
            var sw2 = Stopwatch.StartNew(); var outb = new Rgb[L2.Length];
            lfx.Render(new FxContext(), L2, outb);
            C("infinite-loop plugin is cut off quickly", sw2.ElapsedMilliseconds < 1000 && lfx.Error.Length > 0, $"{sw2.ElapsedMilliseconds} ms: {lfx.Error}");
        }

        // ---------------- every bundled device plugin's own simulated-device tests
        foreach (var f in Directory.Exists(Plugins.PluginHost.BundledDir) ? Directory.GetFiles(Path.Combine(Plugins.PluginHost.BundledDir, "devices"), "*.js") : Array.Empty<string>())
            foreach (var r in Plugins.PluginCheck.Run(f)) C($"plugincheck {r.Plugin}: {r.Test}", r.Pass, r.Detail);

        // ---------------- driver packet builders (offline, no hardware)
        foreach (var (drv, run) in Devices.DriverRegistry.PacketChecks)
        {
            try { foreach (var (n, p, d) in run()) C($"{drv}: {n}", p, d); }
            catch (Exception e) { C($"{drv}: packet checks", false, e.Message); }
        }
        // respect the user's switched-off plugins (e.g. a device they don't want touched) even in tests
        var userOff = File.Exists(GlintConfig.FilePath) ? GlintConfig.Load().DisabledPlugins : new List<string>();
        Plugins.PluginHost.Disabled = () => userOff;
        Plugins.PluginHost.LoadAll();
        var hidNames = Devices.DriverRegistry.Hid.Select(d => d.Name).ToList();
        C("bundled device plugins registered (Commander, Lighting Node, Aura, G213 + Corsair DRAM on SMBus)",
          new[] { "Corsair Commander Core", "Corsair Lighting Node", "ASUS Aura (motherboard)", "Logitech G213" }.All(n => hidNames.Contains("Plugin: " + n) || userOff.Any(f => f.Contains(n.Split(' ')[^1].ToLowerInvariant())))
          && Devices.DriverRegistry.SmbusDrivers.Any(d => d.Name == "Plugin: Corsair DRAM"),
          $"{string.Join(", ", hidNames)} | {string.Join(", ", Devices.DriverRegistry.SmbusDrivers.Select(d => d.Name))}");
        C("no plugin failed to load", Plugins.PluginHost.Status.All(x => x.Ok || x.NeedsApproval), string.Join(" | ", Plugins.PluginHost.Status.Where(x => !x.Ok).Select(x => $"{x.File}: {x.Message}")));

        // ---------------- v0.2: LED-count test pattern, setup check, update check, OpenRGB bridge (fake SDK server on 127.0.0.1)
        SelfTestV02.Run(C);
        SelfTestSecurity.Run(C);
        SelfTestUpdater.Run(C);

        // ---------------- Home Assistant over MQTT (in-process fake broker on 127.0.0.1)
        try { HomeAssistantChecks(C); }
        catch (Exception e) { C("HA: checks ran", false, e.ToString()); }

        // ---------------- hardware
        if (hw)
        {
            var proc = Process.GetCurrentProcess();
            var cfg = GlintConfig.Defaults();
            // Generic: works on whatever this PC has. Fan test uses the first plugin-driven controller with spinning, non-pump fans.
            cfg.ActiveProfile = "test";
            cfg.Profiles["test"] = new();
            cfg.Lighting.Effect = new EffectConfig { Type = EffectType.Wave, Speed = 0.3 };
            cfg.Lighting.Fps = 30;
            using var svc = new GlintService(cfg);
            metrics["devices"] = svc.Rig.Log.ToArray();
            metrics["fanOutputs"] = svc.Rig.Fans.Select(f => f.Id).ToArray();
            metrics["lightZones"] = svc.Rig.Lights.SelectMany(l => l.Zones).Select(z => $"{z.Id}={z.Leds}").ToArray();
            C("found at least one RGB device", svc.Rig.Lights.Count > 0, string.Join(", ", svc.Rig.Lights.Select(l => l.Id)));
            Thread.Sleep(3000);
            var hub = svc.Rig.Controllers.FirstOrDefault(h => h.Channels.Any(c => !c.IsPump && svc.Rpm.GetValueOrDefault(h.Id + ":" + c.Id) > 0));
            var xtFans = hub?.Channels.Where(c => !c.IsPump).Select(c => hub.Id + ":" + c.Id).ToList() ?? new();
            cfg.Profiles["test"] = new() { new FanRule { Fans = xtFans, Fixed = 30 } };
            svc.Apply(cfg, save: false);
            double AvgRpm() => xtFans.Select(f => svc.Rpm.GetValueOrDefault(f)).DefaultIfEmpty().Average();
            Thread.Sleep(35000); // hub firmware ramps down ~25 rpm/s; give it time to settle from any prior state
            double low = AvgRpm();
            var cpu0 = proc.TotalProcessorTime; var wall0 = Stopwatch.StartNew();
            long frames0 = svc.Lighting.FramesSent;
            var hi = GlintConfig.Defaults(); hi.ActiveProfile = "test";
            hi.Profiles["test"] = new() { new FanRule { Fans = xtFans, Fixed = 90 } };
            hi.Lighting = cfg.Lighting;
            svc.Apply(hi, save: false);
            Thread.Sleep(20000);
            double high = AvgRpm();
            proc.Refresh();
            double cpuPct = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / wall0.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            double cpuPctOneCore = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / wall0.Elapsed.TotalMilliseconds * 100;
            metrics["fanTestController"] = hub?.Id ?? "(none)"; metrics["rpmAt30"] = low; metrics["rpmAt90"] = high;
            metrics["sensors"] = svc.Sensors.Where(k => !k.Key.StartsWith('/')).ToDictionary(k => k.Key, k => Math.Round(k.Value, 1));
            if (hub != null) C($"{hub.Id} fans respond to duty WHILE animating (30%→90% raises RPM ≥40%)", high > low * 1.4, $"{low:0} → {high:0} rpm");
            C("CPU temperature read (needs admin + PawnIO)", svc.Sensors.ContainsKey("cpu"), svc.Rig.Elevated ? "elevated" : "NOT elevated");
            metrics["lightingFps"] = Math.Round(svc.Lighting.ActualFps, 1);
            metrics["deviceWriteMs"] = svc.Lighting.DeviceMs.ToDictionary(k => k.Key, k => Math.Round(k.Value, 2));
            metrics["lightingFrameMs"] = Math.Round(svc.Lighting.LastFrameMs, 2);
            metrics["framesSentPerSec"] = Math.Round((svc.Lighting.FramesSent - frames0) / wall0.Elapsed.TotalSeconds, 1);
            metrics["deviceFps"] = svc.Lighting.DeviceFps.ToDictionary(k => k.Key, k => Math.Round(k.Value, 1));
            C("render loop holds ≥28 fps at 30 fps cap", svc.Lighting.ActualFps >= 28, $"{svc.Lighting.ActualFps:0.0} fps, render {svc.Lighting.LastFrameMs:0.00} ms");
            foreach (var (id, fps) in svc.Lighting.DeviceFps)
            {
                // Floor from the device's own limits: its required idle gap plus how long one frame takes to write.
                int gap = svc.Rig.Lights.FirstOrDefault(l => l.Id == id)?.MinGapMs ?? 0;
                double perFrame = Math.Max(1000.0 / 30, gap + svc.Lighting.DeviceMs.GetValueOrDefault(id));
                double floor = Math.Round(Math.Min(25, 0.6 * 1000 / perFrame), 1);
                C($"device {id} animates ≥{floor} fps", fps >= floor, $"{fps:0.0} fps, {svc.Lighting.DeviceMs.GetValueOrDefault(id):0.0} ms/write");
            }
            metrics["cpuPercentOfMachine"] = Math.Round(cpuPct, 3);
            metrics["cpuPercentOfOneCore"] = Math.Round(cpuPctOneCore, 2);
            metrics["privateMB"] = proc.PrivateMemorySize64 / 1048576;
            metrics["workingSetMB"] = proc.WorkingSet64 / 1048576;
            // Budget: RAM SMBus writes alone cost ~8% of a core (busy-wait bus, same as the native driver);
            // everything else together stays well under 10%.
            C("CPU < 18% of one core while animating everything (incl. RAM SMBus)", cpuPctOneCore < 18, $"{cpuPctOneCore:0.00}% of one core");

            // static scene cost
            var st = GlintConfig.Defaults(); st.ActiveProfile = "test"; st.Profiles["test"] = hi.Profiles["test"];
            st.Lighting.Effect = new EffectConfig { Type = EffectType.Static, Colors = new() { "#ff5a1f" } };
            svc.Apply(st, save: false);
            Thread.Sleep(2000);
            var cpu1 = proc.TotalProcessorTime; var w1 = Stopwatch.StartNew(); long f1 = svc.Lighting.FramesSent;
            Thread.Sleep(10000);
            proc.Refresh();
            double staticPct = (proc.TotalProcessorTime - cpu1).TotalMilliseconds / w1.Elapsed.TotalMilliseconds * 100;
            metrics["staticCpuPercentOfOneCore"] = Math.Round(staticPct, 3);
            long staticFrames = svc.Lighting.FramesSent - f1; double staticSecs = w1.Elapsed.TotalSeconds;
            metrics["staticFramesPerSec"] = Math.Round(staticFrames / staticSecs, 2);
            // each device gets one keep-alive per 4 s when nothing changes
            double keepAlive = svc.Rig.Lights.Count / 4.0, sentRate = staticFrames / staticSecs;
            C("static scene only sends keep-alives (≤ 1 frame / 4 s per device)", sentRate <= keepAlive * 1.4, $"{sentRate:0.00}/s for {svc.Rig.Lights.Count} devices (keep-alive floor {keepAlive:0.00}/s)");
            // Identify on real hardware: the target device flashes pure white/black, everything else dims
            string idTarget = svc.Lighting.Zones.Select(z => z.devId).FirstOrDefault() ?? "";
            svc.Lighting.Identify(idTarget, 3);
            Thread.Sleep(700);
            var zonesNow = svc.Lighting.Zones;
            var frameNow = svc.Lighting.Preview.ToArray();
            var xt = zonesNow.Where(z => z.devId == idTarget).SelectMany(z => Enumerable.Range(z.start, z.count)).ToList();
            var others = zonesNow.Where(z => z.devId != idTarget).SelectMany(z => Enumerable.Range(z.start, z.count)).ToList();
            bool xtBlink = xt.All(i => frameNow[i] == new Rgb(255, 255, 255)) || xt.All(i => frameNow[i] == Rgb.Black);
            double othersMax = others.Count == 0 ? 0 : others.Max(i => Math.Max(frameNow[i].R, Math.Max(frameNow[i].G, frameNow[i].B)));
            C($"Identify flashes the target ({idTarget}) and dims everything else", xtBlink && othersMax <= 21, $"target uniform white/black={xtBlink}, others max {othersMax}");
            Thread.Sleep(3000);
            metrics["errors"] = svc.LastError + " " + svc.Lighting.Status;
        }

        var report = new { time = DateTime.Now, passed = checks.Count(c => c.Pass), failed = checks.Count(c => !c.Pass), checks, metrics };
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return checks.Count(c => !c.Pass);
    }

    /// <summary>Memory + speed of the plugin runtime under given V8 flags: 6 simulated devices + one heavy effect.</summary>
    public static void EngineBench(string flags, string outPath)
    {
        Microsoft.ClearScript.V8.V8Settings.GlobalFlags = flags switch
        {
            "nobg" => Microsoft.ClearScript.V8.V8GlobalFlags.DisableBackgroundWork,
            "jitless" => Microsoft.ClearScript.V8.V8GlobalFlags.DisableJITCompilation,
            "both" => Microsoft.ClearScript.V8.V8GlobalFlags.DisableBackgroundWork | Microsoft.ClearScript.V8.V8GlobalFlags.DisableJITCompilation,
            _ => Microsoft.ClearScript.V8.V8GlobalFlags.None,
        };
        var proc = Process.GetCurrentProcess();
        long Mb() { GC.Collect(); GC.WaitForPendingFinalizers(); proc.Refresh(); return proc.PrivateMemorySize64 / 1048576; }
        long b0 = Mb();
        var dir = Path.Combine(Plugins.PluginHost.BundledDir, "devices");
        Plugins.DevicePluginDef Def(string f) => Plugins.PluginHost.LoadForTest(Path.Combine(dir, f)).device!;
        var table = new byte[60]; table[0x1b] = 2; table[0x02] = 3;
        Func<byte[], byte[]?> aura = x =>
        {
            var r = new byte[65]; r[0] = 0xec;
            if (x[1] == 0x82) { r[1] = 2; System.Text.Encoding.ASCII.GetBytes("AULA3-AR32-0309").CopyTo(r, 2); return r; }
            if (x[1] == 0xb0) { r[1] = 0x30; table.CopyTo(r, 4); return r; }
            return null;
        };
        var devs = new List<Found>
        {
            Def("corsair-commander-core.js").OpenWith(new FakePort(0x1b1c, 0x0c32, 65, CommanderSim(true, 65)), x => x, (z, n) => n, _ => { })!,
            Def("corsair-commander-core.js").OpenWith(new FakePort(0x1b1c, 0x0c2a, 385, CommanderSim(false, 385)), x => x, (z, n) => n, _ => { })!,
            Def("corsair-lighting-node.js").OpenWith(new FakePort(0x1b1c, 0x0c0b, 65, x => x[1] == 0x02 ? new byte[17] : null), x => x, (z, n) => n, _ => { })!,
            Def("asus-aura-usb.js").OpenWith(new FakePort(0x0b05, 0x19af, 65, aura), x => x, (z, n) => n, _ => { })!,
            Def("logitech-g213.js").OpenWith(new FakePort(0x046d, 0xc336, 20), x => x, (z, n) => n, _ => { })!,
            Def("corsair-commander-core.js").OpenWith(new FakePort(0x1b1c, 0x0c32, 65, CommanderSim(true, 65)), x => x, (z, n) => n, _ => { })!,
        };
        var cpu0 = proc.TotalProcessorTime;
        for (int f = 0; f < 600; f++)
            foreach (var d in devs)
            {
                var l = d.Lights[0];
                l.Write(new Rgb[l.Zones.Sum(z => z.Leds)]);
                if (f % 30 == 0) foreach (var c in d.Fans) c.Poll(new(), new());
            }
        proc.Refresh();
        double devUs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds * 1000 / (600 * devs.Count);
        long withDevs = Mb();
        long liveMB = GC.GetTotalMemory(true) / 1048576;
        // heavy effect plugin
        var leds = Enumerable.Range(0, 555).Select(i => new Led(i, i / 30, i / 555.0, (i % 30) / 30.0, false)).ToArray();
        var (ep, _) = Plugins.PluginHost.LoadForTest(Path.Combine(Plugins.PluginHost.BundledDir, "effects", "aurora.js"));
        var fx = new Plugins.PluginEffect(new EffectConfig { Type = EffectType.Plugin, Plugin = "Aurora" }, ep!);
        var o = new Rgb[555]; var ctx = new FxContext();
        for (int i = 0; i < 60; i++) { ctx.Time = i / 30.0; fx.Render(ctx, leds, o); }
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 300; i++) { ctx.Time = 2 + i / 30.0; fx.Render(ctx, leds, o); }
        double fxMs = sw.Elapsed.TotalMilliseconds / 300;
        using var w = new StreamWriter(outPath, true);
        long afterFx = Mb();
        w.WriteLine($"{flags,-8} 6 devices: private +{withDevs - b0} MB, live managed {liveMB} MB total, device plugin CPU {devUs:0} µs/frame | +V8 effect: private +{afterFx - withDevs} MB, Aurora {fxMs:0.00} ms/frame @555 LEDs");
    }

    /// <summary>Per-device plugin overhead (no real I/O): µs of CPU per render and per poll.</summary>
    public static void DeviceBench(string outPath)
    {
        using var w = new StreamWriter(outPath) { AutoFlush = true };
        var dir = Path.Combine(Plugins.PluginHost.BundledDir, "devices");
        Plugins.DevicePluginDef Def(string f) => Plugins.PluginHost.LoadForTest(Path.Combine(dir, f)).device!;
        var table = new byte[60]; table[0x1b] = 2; table[0x02] = 3;
        var cases = new (string name, Func<Found?> open)[]
        {
            ("Commander ST", () => Def("corsair-commander-core.js").OpenWith(new FakePort(0x1b1c, 0x0c32, 65, CommanderSim(true, 65)), x => x, (z, n) => n, _ => { })),
            ("Commander XT", () => Def("corsair-commander-core.js").OpenWith(new FakePort(0x1b1c, 0x0c2a, 385, CommanderSim(false, 385)), x => x, (z, n) => n, _ => { })),
            ("Lighting Node", () => Def("corsair-lighting-node.js").OpenWith(new FakePort(0x1b1c, 0x0c0b, 65, x => x[1] == 0x02 ? new byte[17] : null), x => x, (z, n) => n, _ => { })),
            ("Aura", () => Def("asus-aura-usb.js").OpenWith(new FakePort(0x0b05, 0x19af, 65, x =>
            {
                var r = new byte[65]; r[0] = 0xec;
                if (x[1] == 0x82) { r[1] = 2; System.Text.Encoding.ASCII.GetBytes("AULA3-AR32-0309").CopyTo(r, 2); return r; }
                if (x[1] == 0xb0) { r[1] = 0x30; table.CopyTo(r, 4); return r; }
                return null;
            }), x => x, (z, n) => n, _ => { })),
        };
        var proc = Process.GetCurrentProcess();
        foreach (var (name, open) in cases)
        {
            var f = open(); var l = f!.Lights[0]; var fan = f.Fans.FirstOrDefault();
            var c = new Rgb[l.Zones.Sum(z => z.Leds)];
            for (int i = 0; i < 50; i++) l.Write(c);
            var cpu0 = proc.TotalProcessorTime; var sw = Stopwatch.StartNew();
            for (int i = 0; i < 400; i++) l.Write(c);
            proc.Refresh();
            double renderUs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds * 1000 / 400;
            double pollUs = 0;
            if (fan != null)
            {
                var t = new Dictionary<string, double>(); var r = new Dictionary<string, int>();
                cpu0 = proc.TotalProcessorTime;
                for (int i = 0; i < 200; i++) fan.Poll(t, r);
                proc.Refresh();
                pollUs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds * 1000 / 200;
            }
            w.WriteLine($"{name}: render {renderUs:0} µs CPU/frame ({c.Length} LEDs), poll {pollUs:0} µs");
            (l as IDisposable)?.Dispose();
        }
    }

    /// <summary>Scripted HID device: records writes; each write may queue a reply via the responder.</summary>
    internal sealed class FakePort(int vid, int pid, int report, Func<byte[], byte[]?>? responder = null, int iface = -1) : Plugins.IHidPort
    {
        readonly Queue<byte[]> _replies = new();
        public List<byte[]> Writes { get; } = new();
        public int VendorId => vid; public int ProductId => pid;
        public string Product => "fake"; public string Serial => "fake";
        public int OutputReportLength => report; public int InputReportLength => report; public int FeatureReportLength => report;
        public int Interface => iface;
        public void Write(byte[] d)
        {
            var b = d.Length < report ? d.Concat(new byte[report - d.Length]).ToArray() : d;
            Writes.Add(b);
            if (responder?.Invoke(b) is byte[] r) _replies.Enqueue(r);
        }
        public byte[]? Read(int timeoutMs) => _replies.Count > 0 ? _replies.Dequeue() : responder == null ? new byte[report] : null;
        public void SendFeature(byte[] d) => Writes.Add(d);
        public byte[] GetFeature(byte id) => new byte[report];
        public void Dispose() { }
    }

    /// <summary>Simulates a Commander ST (pump, 4 fans, 29+4x8 LEDs) or Core XT (6 fans, 6x8 LEDs) like the real ones answered.</summary>
    internal static Func<byte[], byte[]?> CommanderSim(bool st, int report)
    {
        return w =>
        {
            var r = new byte[report];
            r[1] = 0x00; r[2] = w[2];
            byte cmd0 = w[2], cmd1 = w[3], ep = w[4];
            void Data(params int[] d) { for (int i = 0; i < d.Length; i++) r[6 + i] = (byte)d[i]; }
            if (cmd0 == 0x02 && cmd1 == 0x13) { r[4] = (byte)(st ? 2 : 1); r[5] = 0; r[6] = (byte)(st ? 19 : 62); }
            else if (cmd0 == 0x08)
            {
                switch (ep)
                {
                    case 0x1a: if (st) Data(7, 7, 7, 7, 7, 7, 1, 1); else Data(6, 7, 7, 7, 7, 7, 7); break;
                    case 0x20:
                        if (st) Data(7, 2, 0, 29, 0, 2, 0, 8, 0, 2, 0, 8, 0, 2, 0, 8, 0, 2, 0, 8, 0, 3, 0, 0, 0, 3, 0, 0, 0);
                        else Data(7, 0, 0, 0, 0, 2, 0, 8, 0, 2, 0, 8, 0, 2, 0, 8, 0, 2, 0, 8, 0, 2, 0, 8, 0, 2, 0, 8, 0);
                        break;
                    case 0x17:
                        if (st) Data(7, 0x28, 0x0a, 0xe8, 0x03, 0x4c, 0x04, 0xb0, 0x04, 0x14, 0x05, 0, 0, 0, 0); // 2600,1000,1100,1200,1300
                        else Data(6, 0x2c, 0x01, 0x2d, 0x01, 0x2e, 0x01, 0x2f, 0x01, 0x30, 0x01, 0x31, 0x01);  // 300..305
                        break;
                    case 0x21: if (st) Data(2, 0, 0xb9, 0x01, 1, 0, 0); else Data(2, 0, 0x36, 0x01, 0, 0x55, 0x01); break; // 44.1 / 31.0, 34.1
                }
            }
            return r;
        };
    }

    internal sealed class FakeSmbus : Devices.ISmbusPort
    {
        public readonly Dictionary<byte, Dictionary<byte, int>> Regs = new();
        public readonly List<string> Writes = new();
        readonly Dictionary<byte, Queue<int>> _buffers = new();
        public byte[] Info = new byte[32];
        /// <summary>Pointer-addressed memory (ENE-style): a word write to reg 0x00 sets the pointer (byte-swapped), reg 0x81 reads it.</summary>
        public readonly Dictionary<byte, Dictionary<int, int>> Mem = new();
        readonly Dictionary<byte, int> _ptr = new();
        public int ReadByte(byte a, byte c)
        {
            if (!Regs.TryGetValue(a, out var regs)) return -1;
            if (c == 0x40 && _buffers.TryGetValue(a, out var q) && q.Count > 0) return q.Dequeue();
            if (c == 0x81 && Mem.TryGetValue(a, out var mem)) return mem.TryGetValue(_ptr.GetValueOrDefault(a), out var m) ? m : 0;
            return regs.TryGetValue(c, out var v) ? v & 0xff : 0;
        }
        public bool WriteByte(byte a, byte c, byte v)
        {
            Writes.Add($"{a:x2}:{c:x2}={v:x2}");
            if (c == 0x21) _buffers[a] = new Queue<int>(Info.Select(x => (int)x));
            return Regs.ContainsKey(a);
        }
        /// <summary>Word reads return the register's value (test values may be up to 0xFFFF).</summary>
        public int ReadWord(byte a, byte c) => Regs.TryGetValue(a, out var regs) ? (regs.TryGetValue(c, out var v) ? v & 0xffff : 0) : -1;
        public bool WriteWord(byte a, byte c, ushort v)
        {
            Writes.Add($"{a:x2}:{c:x2}=w{v:x4}");
            if (c == 0x00) _ptr[a] = ((v & 0xff) << 8) | (v >> 8);
            return Regs.ContainsKey(a);
        }
        public bool WriteBlock(byte a, byte c, ReadOnlySpan<byte> d) { Writes.Add($"{a:x2}:{c:x2}#{Convert.ToHexString(d)}"); return true; }
    }

    internal sealed class FakeLight(string id, params LightZone[] zones) : Devices.ILightDevice
    {
        public string Id => id; public string Name => "fake";
        public IReadOnlyList<LightZone> Zones => zones;
        public void Write(ReadOnlySpan<Rgb> colors) { }
        public void Dispose() { }
    }

    // ------------------------------------------------------------------ Home Assistant / MQTT

    /// <summary>One-client MQTT 3.1.1 broker for tests: records CONNECT / SUBSCRIBE / PUBLISH and can publish to the client.</summary>
    sealed class FakeBroker : IDisposable
    {
        readonly System.Net.Sockets.TcpListener _l = new(System.Net.IPAddress.Loopback, 0);
        System.Net.Sockets.TcpClient? _c;
        Stream? _s;
        readonly object _w = new();
        public int Port { get; }
        public string? ClientId, User, Pass, WillTopic, WillMsg;
        public int ConnectFlags, Connects;
        public bool Disconnected;
        public readonly List<string> Subs = new();
        public readonly List<(string Topic, string Payload, bool Retain)> Pubs = new();

        public FakeBroker()
        {
            _l.Start();
            Port = ((System.Net.IPEndPoint)_l.LocalEndpoint).Port;
            new Thread(Serve) { IsBackground = true, Name = "fake broker" }.Start();
        }

        public List<(string Topic, string Payload, bool Retain)> Published() { lock (Pubs) return Pubs.ToList(); }

        void Serve()
        {
            try
            {
                _c = _l.AcceptTcpClient();
                _s = _c.GetStream();
                while (true)
                {
                    int h = _s.ReadByte(); if (h < 0) return;
                    int len = 0, mul = 1, b;
                    do { b = _s.ReadByte(); if (b < 0) return; len += (b & 127) * mul; mul *= 128; } while ((b & 128) != 0);
                    var p = new byte[len]; _s.ReadExactly(p, 0, len);
                    int i = 0;
                    string Str() { int n = p[i] << 8 | p[i + 1]; var v = System.Text.Encoding.UTF8.GetString(p, i + 2, n); i += 2 + n; return v; }
                    switch (h >> 4)
                    {
                        case 1:
                            Str(); i++; ConnectFlags = p[i++]; i += 2;
                            ClientId = Str();
                            if ((ConnectFlags & 0x04) != 0) { WillTopic = Str(); WillMsg = Str(); }
                            if ((ConnectFlags & 0x80) != 0) User = Str();
                            if ((ConnectFlags & 0x40) != 0) Pass = Str();
                            Connects++;
                            Write(new byte[] { 0x20, 2, 0, 0 });
                            break;
                        case 8:
                            {
                                byte id0 = p[0], id1 = p[1]; i = 2; int n = 0;
                                while (i < len) { lock (Subs) Subs.Add(Str()); i++; n++; }
                                var ack = new List<byte> { 0x90, (byte)(2 + n), id0, id1 }; ack.AddRange(new byte[n]);
                                Write(ack.ToArray());
                                break;
                            }
                        case 3:
                            {
                                var t = Str();
                                lock (Pubs) Pubs.Add((t, System.Text.Encoding.UTF8.GetString(p, i, len - i), (h & 1) != 0));
                                break;
                            }
                        case 12: Write(new byte[] { 0xD0, 0 }); break;
                        case 14: Disconnected = true; return;
                    }
                }
            }
            catch { }
        }

        void Write(byte[] b) { lock (_w) { _s!.Write(b); _s.Flush(); } }

        public void Send(string topic, string payload)
        {
            var t = System.Text.Encoding.UTF8.GetBytes(topic); var pl = System.Text.Encoding.UTF8.GetBytes(payload);
            int rem = 2 + t.Length + pl.Length;
            var o = new List<byte> { 0x30 };
            do { byte b = (byte)(rem % 128); rem /= 128; if (rem > 0) b |= 128; o.Add(b); } while (rem > 0);
            o.Add((byte)(t.Length >> 8)); o.Add((byte)t.Length); o.AddRange(t); o.AddRange(pl);
            Write(o.ToArray());
        }

        public void Dispose() { try { _c?.Dispose(); } catch { } _l.Stop(); }
    }

    sealed class FakeFanHub : IFanController
    {
        readonly bool _canRelease;
        public FakeFanHub(string id, bool canRelease) { Id = id; _canRelease = canRelease; }
        public string Id { get; }
        public string Name => "Test hub " + Id;
        public IReadOnlyList<FanChannel> Channels { get; } = new[] { new FanChannel("fan1", "Fan 1") };
        public int Duties, Releases;
        public void Poll(Dictionary<string, double> temps, Dictionary<string, int> rpm) { }
        public void SetDuty(IReadOnlyDictionary<string, int> duty) => Duties++;
        public bool ReleaseFans() { Releases++; return _canRelease; }
        public void Dispose() { }
    }

    sealed class ProcEnv : SetupEnv
    {
        public string[] Procs = Array.Empty<string>();
        public override IEnumerable<string> ProcessNames() => Procs;
        public override IEnumerable<(string Name, string Command)> RunEntries() => Array.Empty<(string, string)>();
        public override bool PawnIoInstalled() => true;
        public override string? WebView2Version() => "142.0.0.0";
        public override bool? StartupTask() => true;
    }

    sealed class FakeControl : LibreHardwareMonitor.Hardware.IControl
    {
        public float Min = 0, Max = 100;
        public bool Throw;
        public float? Value, Reported;
        public string Mode = "untouched";
        public int Sets;
        public LibreHardwareMonitor.Hardware.ControlMode ControlMode => Mode == "software" ? LibreHardwareMonitor.Hardware.ControlMode.Software : LibreHardwareMonitor.Hardware.ControlMode.Default;
        public LibreHardwareMonitor.Hardware.Identifier Identifier => new("fake", "control");
        public float MaxSoftwareValue => Max;
        public float MinSoftwareValue => Min;
        public LibreHardwareMonitor.Hardware.ISensor Sensor => null!;
        public float SoftwareValue => Value ?? 0;
        public int FailDefault;   // the next N SetDefault calls throw
        public void SetDefault() { if (FailDefault > 0) { FailDefault--; throw new InvalidOperationException("driver busy"); } Mode = "default"; }
        public void SetSoftware(float value) { if (Throw) throw new InvalidOperationException("device gone"); Value = value; Mode = "software"; Sets++; }
    }

    sealed class FakeHub : IFanController
    {
        public string Id => "fake";
        public string Name => "Test hub";
        public IReadOnlyList<FanChannel> Channels { get; } = new[] { new FanChannel("fan1", "Fan 1") };
        public double Liquid = 31.5;
        public void Poll(Dictionary<string, double> temps, Dictionary<string, int> rpm) { temps["liquid"] = Liquid; rpm["fan1"] = 1200; }
        public void SetDuty(IReadOnlyDictionary<string, int> duty) { }
        public void Dispose() { }
    }

    sealed class FakeStrip : ILightDevice
    {
        public string Id => "teststrip";
        public string Name => "Test strip";
        public IReadOnlyList<LightZone> Zones { get; } = new[] { new LightZone("teststrip:1", "Strip", 10) };
        public void Write(ReadOnlySpan<Rgb> colors) { }
        public void Dispose() { }
    }

    static bool WaitFor(Func<bool> cond, int ms = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(25); }
        return cond();
    }

    static void HomeAssistantChecks(Action<string, bool, string> C)
    {
        const string pw = "t3st-Pässword!";
        using var broker = new FakeBroker();
        var cfg = GlintConfig.Defaults();
        cfg.HomeAssistant = new HomeAssistantConfig { Enabled = true, Host = "127.0.0.1", Port = broker.Port, Username = "glint-test", Password = pw };

        // password handling (no broker needed)
        var probe = GlintConfig.Defaults();
        probe.HomeAssistant = new HomeAssistantConfig { Password = pw };
        var tmp = Path.Combine(Path.GetTempPath(), $"glint-ha-{Environment.ProcessId}.json");
        probe.Save(tmp);
        var text = File.ReadAllText(tmp);
        var back = GlintConfig.Load(tmp);
        File.Delete(tmp);
        C("HA: password saved DPAPI-encrypted, never in plain text", !text.Contains(pw) && !text.Contains("\"password\"") && back.HomeAssistant.PasswordProtected is { Length: > 20 },
          $"{text.Length} chars, protected={back.HomeAssistant.PasswordProtected?.Length}");
        C("HA: password round-trips through DPAPI", back.HomeAssistant.EffectivePassword() == pw && Secret.Unprotect(back.HomeAssistant.PasswordProtected!) == pw, "");
        C("HA: brightness maps 255→100, 128→50, 100%→255", HomeAssistantBridge.FromHa(255) == 100 && HomeAssistantBridge.FromHa(128) == 50 && HomeAssistantBridge.ToHa(100) == 255,
          $"{HomeAssistantBridge.FromHa(255)} {HomeAssistantBridge.FromHa(128)} {HomeAssistantBridge.ToHa(100)}");

        var rig = new Rig(cfg, useLhm: false, hardware: false);
        rig.Controllers.Add(new FakeHub());
        rig.Fans.Add(new FanOutput("fake:fan1", "Fan 1", "Test hub"));
        rig.Lights.Add(new FakeStrip());
        cfg.SensorNames["fake:liquid"] = "Coolant";
        var svc = new GlintService(cfg, rig) { NoSave = true };
        try
        {
            string node = HomeAssistantBridge.Slug(Environment.MachineName);
            string root = $"glint/{node}", avail = root + "/availability";
            C("HA: in-memory config holds no plain password", svc.Config.HomeAssistant.Password == null && JsonSerializer.Serialize(svc.Config, GlintConfig.Json) is var js && !js.Contains(pw), "");

            WaitFor(() => broker.Connects > 0);
            C("HA: connects with username + decrypted password, LWT 'offline' retained on the availability topic",
              broker.User == "glint-test" && broker.Pass == pw && broker.WillTopic == avail && broker.WillMsg == "offline" && (broker.ConnectFlags & 0x24) == 0x24,
              $"user={broker.User} passOk={broker.Pass == pw} will={broker.WillTopic}:{broker.WillMsg} flags=0x{broker.ConnectFlags:x2}");
            C("HA: subscribes to its command topics and HA's status topic", WaitFor(() => { lock (broker.Subs) return broker.Subs.Contains(root + "/cmd/#") && broker.Subs.Contains("homeassistant/status"); }, 3000),
              string.Join(", ", broker.Subs));
            C("HA: availability 'online' published retained", WaitFor(() => broker.Published().Any(p => p.Topic == avail && p.Payload == "online" && p.Retain)), "");

            JsonObject? Disc(string comp, string uid)
            {
                var t = $"homeassistant/{comp}/{node}/{uid}/config";
                WaitFor(() => broker.Published().Any(p => p.Topic == t));
                var p = broker.Published().LastOrDefault(p => p.Topic == t);
                return p.Topic == null ? null : JsonNode.Parse(p.Payload) as JsonObject;
            }
            bool Valid(JsonObject? o, bool command) => o != null && o["unique_id"] is JsonValue && o["state_topic"] is JsonValue && (!command || o["command_topic"] is JsonValue)
                && ((string?)o["device"]?["name"])?.StartsWith("Glint (") == true && (string?)o["availability"]?[0]?["topic"] == avail;

            var temp = Disc("sensor", "temp_fake_liquid");
            C("HA: temperature sensor discovery (valid JSON, unique_id, state_topic, device, availability, °C)",
              Valid(temp, false) && (string?)temp!["device_class"] == "temperature" && (string?)temp["unit_of_measurement"] == "°C" && (string?)temp["name"] == "Coolant temperature",
              temp?.ToJsonString() ?? "not published");
            var rpm = Disc("sensor", "rpm_fake_fan1");
            C("HA: fan RPM + duty sensors discovered", Valid(rpm, false) && (string?)rpm!["unit_of_measurement"] == "RPM" && Valid(Disc("sensor", "duty_fake_fan1"), false), rpm?.ToJsonString() ?? "not published");
            var light = Disc("light", "rgb");
            C("HA: light discovery (json schema, rgb, brightness, effect list = presets)",
              Valid(light, true) && (string?)light!["schema"] == "json" && light["effect_list"]!.AsArray().Any(x => (string?)x == "Fire") && light["supported_color_modes"]!.AsArray().Any(x => (string?)x == "rgb"),
              light?.ToJsonString() ?? "not published");
            var sel = Disc("select", "fan_profile");
            C("HA: select discovery (options = fan profiles)", Valid(sel, true) && sel!["options"]!.AsArray().Select(x => (string?)x).Contains("Quiet"), sel?.ToJsonString() ?? "not published");
            var btn = Disc("button", "identify_teststrip");
            C("HA: identify button per lighting device", btn?["command_topic"] != null && (string?)btn!["payload_press"] == "teststrip" && (string?)btn["name"] == "Identify Test strip", btn?.ToJsonString() ?? "not published");
            string tempState = (string)temp?["state_topic"]! ?? "";
            C("HA: temperature state published", WaitFor(() => broker.Published().Any(p => p.Topic == tempState && p.Payload == "31.5")), tempState);

            string lightCmd = (string?)light?["command_topic"] ?? "", lightState = (string?)light?["state_topic"] ?? "";
            broker.Send(lightCmd, """{"state":"ON","effect":"Fire"}""");
            C("HA: light command {state:ON, effect:<preset>} applies the preset", WaitFor(() => svc.Config.Lighting.Effect.Type == EffectType.Fire && svc.Config.Lighting.Enabled, 3000), svc.Config.Lighting.Effect.Type.ToString());
            C("HA: light state echoes the effect", WaitFor(() => broker.Published().Any(p => p.Topic == lightState && p.Payload.Contains("\"effect\":\"Fire\"")), 3000), broker.Published().LastOrDefault(p => p.Topic == lightState).Payload ?? "");
            broker.Send(lightCmd, """{"state":"ON","brightness":255}""");
            C("HA: light brightness 255 → 100%", WaitFor(() => svc.Config.Lighting.Brightness == 100, 3000), $"{svc.Config.Lighting.Brightness}");
            broker.Send(lightCmd, """{"state":"ON","brightness":128}""");
            C("HA: light brightness 128 → 50%", WaitFor(() => svc.Config.Lighting.Brightness == 50, 3000), $"{svc.Config.Lighting.Brightness}");
            broker.Send(lightCmd, """{"state":"ON","color":{"r":255,"g":0,"b":128}}""");
            C("HA: light colour → static effect of that colour", WaitFor(() => svc.Config.Lighting.Effect.Type == EffectType.Static && svc.Config.Lighting.Effect.Colors.FirstOrDefault() == "#ff0080", 3000),
              $"{svc.Config.Lighting.Effect.Type} {string.Join(",", svc.Config.Lighting.Effect.Colors)}");
            broker.Send(lightCmd, """{"state":"OFF"}""");
            C("HA: light OFF disables lighting", WaitFor(() => !svc.Config.Lighting.Enabled, 3000), "");

            // per-device light (writes Lighting.Overrides[deviceId])
            var dev = Disc("light", "light_teststrip");
            C("HA: per-device light discovery (effects include 'Follow global')", Valid(dev, true) && (string?)dev!["name"] == "Test strip" && dev["effect_list"]!.AsArray().Any(x => (string?)x == "Follow global"),
              dev?.ToJsonString() ?? "not published");
            string devCmd = (string?)dev?["command_topic"] ?? "", devState = (string?)dev?["state_topic"] ?? "";
            broker.Send(devCmd, """{"state":"ON","color":{"r":255,"g":0,"b":0},"brightness":128}""");
            C("HA: device light colour → that device's override only (brightness scales the colour)",
              WaitFor(() => svc.Config.Lighting.Overrides.TryGetValue("teststrip", out var o) && o.Type == EffectType.Static && o.Colors[0] == "#800000", 3000),
              string.Join(",", svc.Config.Lighting.Overrides.Select(o => $"{o.Key}={o.Value.Type}:{string.Join("/", o.Value.Colors)}")));
            C("HA: device light state published (red, brightness 128)", WaitFor(() => broker.Published().Any(p => p.Topic == devState && p.Payload.Contains("\"brightness\":128") && p.Payload.Contains("\"r\":255,\"g\":0")), 3000),
              broker.Published().LastOrDefault(p => p.Topic == devState).Payload ?? "");
            broker.Send(devCmd, """{"state":"OFF"}""");
            C("HA: device light OFF → static black override", WaitFor(() => svc.Config.Lighting.Overrides.TryGetValue("teststrip", out var o) && o.Colors[0] == "#000000", 3000), "");
            broker.Send(devCmd, """{"state":"ON","effect":"Follow global"}""");
            C("HA: device light 'Follow global' removes the override", WaitFor(() => !svc.Config.Lighting.Overrides.ContainsKey("teststrip"), 3000), "");
            broker.Send(devCmd, """{"state":"ON","effect":"Comet"}""");
            WaitFor(() => svc.Config.Lighting.Overrides.ContainsKey("teststrip"), 3000);
            broker.Send(lightCmd, """{"state":"ON","effect":"Fire"}""");
            C("HA: global light effect clears per-device overrides", WaitFor(() => svc.Config.Lighting.Overrides.Count == 0 && svc.Config.Lighting.Effect.Type == EffectType.Fire, 3000),
              string.Join(",", svc.Config.Lighting.Overrides.Keys));

            // fan entity per fan rule (manual override in the config)
            var fan = Disc("fan", "fan_radiator_fans");
            C("HA: fan entity per fan rule (percentage + preset 'auto')", fan?["percentage_command_topic"] != null && fan["preset_modes"]!.AsArray().Any(x => (string?)x == "auto") && (string?)fan["name"] == "Radiator fans" && (string?)fan["availability"]?[0]?["topic"] == avail,
              fan?.ToJsonString() ?? "not published");
            broker.Send((string?)fan?["percentage_command_topic"] ?? "", "80");
            C("HA: fan percentage → manual override stored in the config", WaitFor(() => svc.Config.Active.First(r => r.Name == "Radiator fans").Override == 80, 3000),
              $"{svc.Config.Active.First(r => r.Name == "Radiator fans").Override}");
            string fanState = (string?)fan?["state_topic"] ?? "";
            C("HA: fan state reports manual mode", WaitFor(() => broker.Published().Any(p => p.Topic == fanState && p.Payload.Contains("\"preset\":\"None\"")), 3000),
              broker.Published().LastOrDefault(p => p.Topic == fanState).Payload ?? "");
            var pump = Disc("fan", "fan_aio_pump");
            broker.Send((string?)pump?["percentage_command_topic"] ?? "", "10");
            C("HA: pump percentage is clamped to the rule's minimum", WaitFor(() => svc.Config.Active.First(r => r.Name == "AIO pump").Override == 60, 3000),
              $"{svc.Config.Active.First(r => r.Name == "AIO pump").Override}");
            broker.Send((string?)fan?["preset_mode_command_topic"] ?? "", "auto");
            C("HA: fan preset 'auto' clears the override", WaitFor(() => svc.Config.Active.First(r => r.Name == "Radiator fans").Override == null, 3000), "");

            string selCmd = (string?)sel?["command_topic"] ?? "", selState = (string?)sel?["state_topic"] ?? "";
            broker.Send(selCmd, "Quiet");
            C("HA: select command switches the fan profile", WaitFor(() => svc.Config.ActiveProfile == "Quiet", 3000), svc.Config.ActiveProfile);
            C("HA: profile state published", WaitFor(() => broker.Published().Any(p => p.Topic == selState && p.Payload == "Quiet"), 3000), "");

            int discBefore = broker.Published().Count(p => p.Topic.EndsWith("/config"));
            broker.Send("homeassistant/status", "online");
            C("HA: discovery re-published when HA comes online", WaitFor(() => broker.Published().Count(p => p.Topic.EndsWith("/config")) >= discBefore * 2, 3000),
              $"{discBefore} → {broker.Published().Count(p => p.Topic.EndsWith("/config"))}");

            var st = JsonSerializer.SerializeToNode(svc.HomeAssistant.Status())!;
            C("HA: config changes outside the HA section don't reconnect", broker.Connects == 1 && (bool?)st["connected"] == true && (int?)st["reconnects"] == 0 && (int?)st["entities"] >= 6, st.ToJsonString());
        }
        finally
        {
            string avail = $"glint/{HomeAssistantBridge.Slug(Environment.MachineName)}/availability";
            svc.Dispose();
            C("HA: clean shutdown publishes 'offline' (retained) and disconnects",
              WaitFor(() => broker.Disconnected, 3000) && broker.Published().LastOrDefault(p => p.Topic == avail) is var last && last.Payload == "offline" && last.Retain, "");
        }
    }
}
