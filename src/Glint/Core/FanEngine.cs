namespace Glint.Core;

/// <summary>
/// Turns sensor readings into fan duties. Each rule has N (sensor, curve) inputs that are combined
/// (max / average / weighted), then clamped, ramp-limited and applied to every fan in the rule.
/// Pure logic: no hardware, so it is unit-testable from the selftest command.
/// </summary>
public sealed class FanEngine
{
    /// <summary>Minimum duty for graphics-card fans while a GPU stress test runs (0 = none). Set by the tuning service.</summary>
    public volatile int GpuBoost;
    readonly Dictionary<string, double> _smoothed = new();
    readonly Dictionary<string, double> _held = new();      // hysteresis-held temperature per input
    readonly Dictionary<string, double> _duty = new();      // last output per fan
    public Dictionary<string, string> LastExplain { get; } = new();

    public static double Interpolate(IReadOnlyList<double[]> pts, double t)
    {
        if (pts.Count == 0) return 100;
        if (t <= pts[0][0]) return pts[0][1];
        for (int i = 1; i < pts.Count; i++)
        {
            if (t <= pts[i][0])
            {
                var (t0, d0, t1, d1) = (pts[i - 1][0], pts[i - 1][1], pts[i][0], pts[i][1]);
                return t1 == t0 ? d1 : d0 + (d1 - d0) * (t - t0) / (t1 - t0);
            }
        }
        return pts[^1][1];
    }

    /// <summary>Resolve "max(a,b)", "min(a,b)", "avg(a,b)" or a plain id.</summary>
    public static double Resolve(string id, IReadOnlyDictionary<string, double> s)
    {
        id = id.Trim();
        int p = id.IndexOf('(');
        if (p > 0 && id.EndsWith(')'))
        {
            var fn = id[..p].ToLowerInvariant();
            var vals = id[(p + 1)..^1].Split(',').Select(x => Resolve(x, s)).ToList();
            if (vals.Any(double.IsNaN)) return double.NaN; // a missing input is a failure, not ignored
            return fn switch { "max" => vals.Max(), "min" => vals.Min(), "avg" => vals.Average(), _ => double.NaN };
        }
        return s.TryGetValue(id, out var v) ? v : double.NaN;
    }

    /// <summary>Compute duties for all fans in the profile.</summary>
    static string InputKey(FanRule rule, FanInput inp) =>
        $"{rule.Name}|{inp.Sensor}|{inp.Curve}|{(inp.Points == null ? "" : string.Join(";", inp.Points.Select(p => string.Join(",", p))))}";

    public Dictionary<string, int> Compute(GlintConfig cfg, IReadOnlyDictionary<string, double> sensors, double dtSec)
    {
        var result = new Dictionary<string, int>();
        LastExplain.Clear();
        var seen = new HashSet<string>();
        foreach (var (rule, ri) in cfg.Active.Select((r, i) => (r, i)))
        {
            double target;
            string why;
            bool lost = false;   // a sensor this rule needs is missing / implausible
            if (rule.Fixed is int f)
            {
                target = f; why = $"fixed {f}%";
            }
            else if (rule.Inputs.Count == 0)
            {
                target = cfg.FailsafeDuty; why = "no inputs"; lost = true;
            }
            else
            {
                var parts = new List<(double duty, double w, string label)>();
                for (int ii = 0; ii < rule.Inputs.Count; ii++)
                {
                    var inp = rule.Inputs[ii];
                    // State belongs to the input itself (rule + sensor + curve), not to its position: removing or adding an
                    // input must not hand another input's smoothed / held temperature to its neighbour.
                    string key = InputKey(rule, inp);
                    seen.Add(key);
                    double raw = Resolve(inp.Sensor, sensors);
                    if (double.IsNaN(raw) || raw < -20 || raw > 150)
                    {
                        _smoothed.Remove(key); _held.Remove(key); lost = true;
                        parts.Add((cfg.FailsafeDuty, inp.Weight, $"{inp.Sensor}=MISSING→{cfg.FailsafeDuty}%"));
                        continue;
                    }
                    double t = raw;
                    if (inp.SmoothingSec > 0 && _smoothed.TryGetValue(key, out var prev))
                        t = prev + (raw - prev) * (1 - Math.Exp(-dtSec / inp.SmoothingSec));
                    _smoothed[key] = t;
                    // Hysteresis: follow rises immediately, only fall once we're H below the held value.
                    double held = _held.TryGetValue(key, out var h) ? h : t;
                    if (t > held) held = t;
                    else if (t < held - inp.Hysteresis) held = t + inp.Hysteresis;
                    _held[key] = held;
                    var pts = inp.Points ?? (inp.Curve != null && cfg.Curves.TryGetValue(inp.Curve, out var c) ? c : null);
                    double d = pts == null ? cfg.FailsafeDuty : Interpolate(pts, held);
                    // show the value the duty came from: smoothing / hysteresis can hold it above the live reading
                    parts.Add((d, inp.Weight, Math.Abs(held - raw) >= 0.05 ? $"{inp.Sensor}={raw:0.0}° (held {held:0.0}°)→{d:0}%" : $"{inp.Sensor}={raw:0.0}°→{d:0}%"));
                }
                target = rule.Combine switch
                {
                    Combine.Average => parts.Average(p => p.duty),
                    Combine.Weighted => parts.Sum(p => p.duty * p.w) / Math.Max(1e-9, parts.Sum(p => p.w)),
                    _ => parts.Max(p => p.duty),
                };
                why = $"{rule.Combine.ToString().ToLowerInvariant()}[{string.Join(", ", parts.Select(p => p.label))}]";
            }
            bool hot = rule.Fixed == null && target >= cfg.FailsafeDuty; // curve has reached the fail-safe duty
            target = Math.Clamp(target, rule.MinDuty, rule.MaxDuty);
            if (rule.Fixed is int fx) target = fx;
            // Manual override (Home Assistant / API): holds its duty until "auto", but never beats the fail-safe
            // (a missing sensor or a curve that has reached the fail-safe duty).
            bool manual = false;
            if (rule.Override is int ov)
            {
                if (rule.Fixed == null && (lost || hot)) { target = Math.Max(target, cfg.FailsafeDuty); why = $"fail-safe beats manual {ov}%: {why}"; }
                else { target = Math.Clamp(ov, rule.MinDuty, rule.MaxDuty); why = $"manual {target:0}%"; manual = true; }
            }

            foreach (var fan in rule.Fans)
            {
                // GPU stress test running: graphics-card fans held at least at the boost (only ever raises a duty)
                double tgt = GpuBoost > 0 && fan.StartsWith("gpu", StringComparison.Ordinal) ? Math.Max(target, GpuBoost) : target;
                bool boosted = tgt > target;
                double cur = _duty.TryGetValue(fan, out var c0) ? c0 : tgt;
                double up = rule.RampUpPerSec * dtSec, down = rule.RampDownPerSec * dtSec;
                // Failsafe / fixed jumps are never ramp-limited upward.
                if (tgt >= cfg.FailsafeDuty || rule.Fixed != null || manual || boosted) cur = tgt;
                else cur = tgt > cur ? Math.Min(tgt, cur + up) : Math.Max(tgt, cur - down);
                _duty[fan] = cur;
                result[fan] = (int)Math.Round(cur);
                LastExplain[fan] = boosted ? $"{result[fan]}% ← tuning test boost" : $"{result[fan]}% ← {why}";
            }
        }
        // forget inputs that no longer exist (renamed / removed rules and inputs)
        foreach (var k in _smoothed.Keys.Where(k => !seen.Contains(k)).ToList()) _smoothed.Remove(k);
        foreach (var k in _held.Keys.Where(k => !seen.Contains(k)).ToList()) _held.Remove(k);
        return result;
    }
}
