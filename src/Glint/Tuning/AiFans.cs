using System.Text.Json;
using System.Text.RegularExpressions;
using Glint.Core;

namespace Glint.Tuning;

/// <summary>
/// The AI's fan tools: its own profile ("AI · Quiet" ...), a copy of a base profile, and curve edits on that copy only.
/// Everything the AI sends is validated here (never trusted): the profile must be AI-owned, the rule must belong to the
/// goal's device (GPU or CPU) and not be a pump, and the curve must keep the fans safe (100% before the emergency
/// temperature, a duty floor from 60 °C up, rule limits respected). Changes go through <see cref="ConfigValidator"/> and the
/// owner's apply (GlintService.Apply), copy-on-write: curves other profiles use are never modified. Config writes are rate-limited.
/// </summary>
public sealed class AiFans
{
    public const int MinPoints = 2, MaxPoints = 10;
    public const double MinTempC = 20, MaxTempC = 110, FloorDuty = 30, FloorFromC = 60, EmergencyMarginC = 5, MinWriteGapSec = 5;
    public const string CurvePrefix = "ai:";

    readonly Func<GlintConfig> _cfg;
    readonly Action<GlintConfig> _apply;
    readonly Func<ValidationContext> _ctx;
    readonly TuningService _tuning;
    readonly Func<IReadOnlyDictionary<string, int>> _duty, _rpm;
    readonly Func<DateTime> _now;
    readonly Func<double> _cpuEmergencyC;
    readonly Func<IReadOnlyDictionary<string, string>> _fanNames;
    readonly object _gate = new();
    DateTime _lastWrite = DateTime.MinValue;

    /// <param name="apply">The owner's validated-config sink (GlintService.Apply); called only with a config that passed <see cref="ConfigValidator"/>.</param>
    public AiFans(Func<GlintConfig> cfg, Action<GlintConfig> apply, Func<ValidationContext> ctx, TuningService tuning,
        Func<IReadOnlyDictionary<string, int>> duty, Func<IReadOnlyDictionary<string, int>> rpm, Func<double>? cpuEmergencyC = null,
        Func<IReadOnlyDictionary<string, string>>? fanNames = null, Func<DateTime>? now = null)
    {
        _cfg = cfg; _apply = apply; _ctx = ctx; _tuning = tuning; _duty = duty; _rpm = rpm;
        _cpuEmergencyC = cpuEmergencyC ?? (() => 85);
        _fanNames = fanNames ?? (() => new Dictionary<string, string>());
        _now = now ?? (() => DateTime.UtcNow);
    }

    static GlintConfig Clone(GlintConfig c) => JsonSerializer.Deserialize<GlintConfig>(JsonSerializer.Serialize(c, GlintConfig.Json), GlintConfig.Json)!;

    // ------------------------------------------------------------------ classification

    static IEnumerable<string> Leaves(string sensor) =>
        Regex.Split(sensor ?? "", @"[(),\s]+").Where(x => x.Length > 0 && !Limits.SensorFunctions.Contains(x, StringComparer.OrdinalIgnoreCase));

    static bool GpuSensor(string s) => Leaves(s).Any(l => l.StartsWith("gpu", StringComparison.OrdinalIgnoreCase));
    static bool CpuSensor(string s) => Leaves(s).Any(l => l.StartsWith("cpu", StringComparison.OrdinalIgnoreCase) || l.StartsWith("liquid", StringComparison.OrdinalIgnoreCase));

    /// <summary>Pump rules are never touched: a rule or fan named (or id'd) "pump", or one using the pump curve.</summary>
    public bool IsPump(GlintConfig c, FanRule r)
    {
        var names = _fanNames();
        bool Pumpish(string? s) => s != null && s.Contains("pump", StringComparison.OrdinalIgnoreCase);
        if (Pumpish(r.Name)) return true;
        foreach (var f in r.Fans)
        {
            if (Pumpish(f)) return true;
            if (c.FanNames != null && c.FanNames.TryGetValue(f, out var n) && Pumpish(n)) return true;
            if (names.TryGetValue(f, out var n2) && Pumpish(n2)) return true;
        }
        return r.Inputs.Any(i => Pumpish(i.Curve));
    }

    /// <summary>The device(s) a rule is about: GPU when it drives a graphics-card fan or reads a GPU sensor; CPU when it reads a CPU/liquid sensor.</summary>
    public static (bool Gpu, bool Cpu) Devices(FanRule r) =>
        (r.Fans.Any(f => f.StartsWith("gpu", StringComparison.OrdinalIgnoreCase)) || r.Inputs.Any(i => GpuSensor(i.Sensor)), r.Inputs.Any(i => CpuSensor(i.Sensor)));

    static bool InputRelevant(FanInput i, TuningGoal g) => (g.Gpu && GpuSensor(i.Sensor)) || (g.Cpu && CpuSensor(i.Sensor));

    double EmergencyC(GlintConfig c, bool gpu) =>
        gpu ? TuningLimits.EmergencyCore(c.Tuning?.EmergencyCoreC ?? TuningLimits.DefaultEmergencyCoreC, _tuning.OpenedGpu?.SlowdownC) : _cpuEmergencyC();

    // ------------------------------------------------------------------ profile

    /// <summary>Create (first use) or reuse the AI's profile for a goal and switch Glint to it for the run. The user's profile is left alone and reported in
    /// previousProfile; <see cref="Restore"/> switches back.</summary>
    public object Prepare(string? goal, string? text, string? from)
    {
        var (g, _) = TuningGoal.Parse(goal, text, "gpu");
        if (g == null) throw new ArgumentException("goal must be quiet, balanced, performance or custom");
        if (_tuning.CurrentGoal is { } run && run.Kind != g.Kind)
            throw new InvalidOperationException($"this run's goal is {run.Kind}: only \"{run.ProfileName}\" can be prepared");
        var name = g.ProfileName;
        lock (_gate)
        {
            var cur = _cfg();
            bool exists = cur.Profiles.ContainsKey(name);
            if (exists && !_tuning.IsAiProfile(name))
                throw new InvalidOperationException($"a profile named \"{name}\" already exists and isn't the AI's: rename or delete it first (the AI never edits your own profiles)");
            bool active = cur.ActiveProfile == name;
            if (exists && active) return new { profile = name, created = false, switched = false, previousProfile = _tuning.PreviousProfile, owned = true };
            WriteGap();
            var c = Clone(cur);
            string? previous = _tuning.IsAiProfile(cur.ActiveProfile) ? _tuning.PreviousProfile : cur.ActiveProfile;
            if (!exists)
            {
                var baseName = string.IsNullOrWhiteSpace(from) ? cur.ActiveProfile : from!;
                if (!c.Profiles.TryGetValue(baseName, out var baseRules)) throw new KeyNotFoundException($"no profile \"{baseName}\" to copy from (profiles: {string.Join(", ", c.Profiles.Keys)})");
                c.Profiles[name] = JsonSerializer.Deserialize<List<FanRule>>(JsonSerializer.Serialize(baseRules, GlintConfig.Json), GlintConfig.Json)!;
            }
            c.ActiveProfile = name;
            Commit(c);
            _tuning.MarkAiProfile(name, g.Kind, previous);
            return new { profile = name, created = !exists, switched = true, previousProfile = previous, owned = true,
                         note = "Glint is now on the AI's profile. When the run is over, tuning.profile.restore switches back to your own profile (or the user can pick it in the dashboard)." };
        }
    }

    /// <summary>Switch back to the profile that was active before the AI's.</summary>
    public object Restore()
    {
        lock (_gate)
        {
            var cur = _cfg();
            var prev = _tuning.PreviousProfile;
            if (prev == null || !cur.Profiles.ContainsKey(prev)) throw new InvalidOperationException("no previous profile to go back to");
            if (cur.ActiveProfile == prev) { _tuning.ClearPreviousProfile(); return new { restored = false, profile = prev, note = "already active" }; }
            WriteGap(touch: true);
            var c = Clone(cur); c.ActiveProfile = prev;
            Commit(c);
            _tuning.ClearPreviousProfile();
            return new { restored = true, profile = prev };
        }
    }

    // ------------------------------------------------------------------ status

    /// <summary>The fan rules that matter for the GPU and the CPU in a profile, their curves and live duty / rpm.</summary>
    public object Status(string? profile)
    {
        var c = _cfg();
        var name = string.IsNullOrWhiteSpace(profile) ? c.ActiveProfile : profile!;
        if (!c.Profiles.TryGetValue(name, out var rules)) throw new KeyNotFoundException($"no profile \"{name}\" (profiles: {string.Join(", ", c.Profiles.Keys)})");
        bool owned = _tuning.IsAiProfile(name);
        var goal = _tuning.CurrentGoal;
        var duty = _duty(); var rpm = _rpm();
        object Rule(FanRule r)
        {
            var (gpu, cpu) = Devices(r);
            bool pump = IsPump(c, r);
            string? why = RefusalReason(c, name, r, goal, owned, pump);
            return new
            {
                name = r.Name, device = new[] { gpu ? "gpu" : null, cpu ? "cpu" : null }.Where(x => x != null), pump,
                untouchable = pump, editable = why == null, whyNot = why,
                combine = r.Combine.ToString().ToLowerInvariant(), minDuty = r.MinDuty, maxDuty = r.MaxDuty, @fixed = r.Fixed, @override = r.Override,
                fans = r.Fans.Select(f => new { id = f, duty = duty.TryGetValue(f, out var d) ? d : (int?)null, rpm = rpm.TryGetValue(f, out var rp) ? rp : (int?)null }),
                inputs = r.Inputs.Select((i, ix) => new
                {
                    index = ix, sensor = i.Sensor, curve = i.Curve, ownCurve = i.Curve != null && i.Curve.StartsWith(CurvePrefix + name + "|", StringComparison.Ordinal),
                    points = i.Points ?? (i.Curve != null && c.Curves.TryGetValue(i.Curve, out var p) ? p : null),
                    smoothingSec = i.SmoothingSec, hysteresis = i.Hysteresis,
                }),
            };
        }
        var rel = rules.Where(r => { var (g, cp) = Devices(r); return g || cp; }).Select(Rule).ToList();
        return new
        {
            profile = name, activeProfile = c.ActiveProfile, aiOwned = owned, goal = goal?.Kind, goalTarget = goal?.Target,
            gpuRules = rules.Where(r => Devices(r).Gpu).Select(r => r.Name), cpuRules = rules.Where(r => Devices(r).Cpu).Select(r => r.Name),
            rules = rel,
            curveRules = new
            {
                points = $"{MinPoints}-{MaxPoints}", tempC = $"{MinTempC}-{MaxTempC} strictly increasing", duty = "0-100, never decreasing, within the rule's minDuty..maxDuty",
                reach100AtOrBelowC = new { gpu = EmergencyC(c, true) - EmergencyMarginC, cpu = _cpuEmergencyC() - EmergencyMarginC },
                floor = $"at least {FloorDuty}% from {FloorFromC} °C up", pumps = "never touched", minSecondsBetweenWrites = MinWriteGapSec,
            },
        };
    }

    string? RefusalReason(GlintConfig c, string profile, FanRule r, TuningGoal? goal, bool owned, bool pump)
    {
        if (!owned) return $"profile \"{profile}\" isn't the AI's: the AI never edits your own profiles";
        if (pump) return "pump: never touched";
        var (gpu, cpu) = Devices(r);
        if (!gpu && !cpu) return "not a GPU or CPU cooling rule";
        if (goal == null) return "no AI tuning goal is running";
        if (!((goal.Gpu && gpu) || (goal.Cpu && cpu))) return $"not relevant to this run's target ({goal.Target})";
        if (r.Fixed != null) return "the rule holds a fixed duty (no curve)";
        if (r.MaxDuty < 100) return $"the rule's maxDuty is {r.MaxDuty}%: its curve could never reach 100% before the emergency temperature";
        return null;
    }

    // ------------------------------------------------------------------ setCurve

    /// <summary>Replace one input's curve on an AI-owned profile (rule relevant to the goal's device, never a pump), validated, copy-on-write.</summary>
    public object SetCurve(string? profile, string? rule, string? input, IReadOnlyList<double[]>? points)
    {
        lock (_gate)
        {
            var cur = _cfg();
            if (string.IsNullOrWhiteSpace(profile)) throw new ArgumentException("profile is required");
            if (!cur.Profiles.TryGetValue(profile!, out var rules)) throw new KeyNotFoundException($"no profile \"{profile}\"");
            bool owned = _tuning.IsAiProfile(profile);
            if (!owned) throw new InvalidOperationException($"profile \"{profile}\" isn't the AI's: the AI never edits your own profiles");
            var r = rules.FirstOrDefault(x => x.Name == rule) ?? throw new KeyNotFoundException($"no rule \"{rule}\" in \"{profile}\"");
            var goal = _tuning.CurrentGoal;
            if (RefusalReason(cur, profile!, r, goal, owned, IsPump(cur, r)) is { } why) throw new InvalidOperationException($"rule \"{r.Name}\" can't be changed: {why}");
            if (r.Inputs.Count == 0) throw new InvalidOperationException($"rule \"{r.Name}\" has no inputs");
            int ix = -1;
            if (string.IsNullOrWhiteSpace(input)) { if (r.Inputs.Count == 1) ix = 0; else throw new ArgumentException($"rule \"{r.Name}\" has {r.Inputs.Count} inputs: say which (index or sensor)"); }
            else if (int.TryParse(input, out var n)) ix = n;
            else ix = r.Inputs.FindIndex(i => i.Sensor.Equals(input, StringComparison.OrdinalIgnoreCase));
            if (ix < 0 || ix >= r.Inputs.Count) throw new KeyNotFoundException($"rule \"{r.Name}\" has no input \"{input}\"");
            if (!InputRelevant(r.Inputs[ix], goal!)) throw new InvalidOperationException($"input {ix} ({r.Inputs[ix].Sensor}) isn't about this run's target ({goal!.Target})");
            bool gpuInput = GpuSensor(r.Inputs[ix].Sensor);
            var pts = CheckPoints(points, r, EmergencyC(cur, gpuInput));
            WriteGap();
            var c = Clone(cur);
            var rc = c.Profiles[profile!].First(x => x.Name == r.Name);
            // copy-on-write: a curve of its own, named after this profile, rule and input; others keep theirs
            var curve = $"{CurvePrefix}{profile}|{r.Name}|{ix}";
            c.Curves[curve] = pts;
            rc.Inputs[ix].Curve = curve; rc.Inputs[ix].Points = null;
            Commit(c);
            return new { ok = true, profile, rule = r.Name, input = ix, curve, points = pts };
        }
    }

    /// <summary>Every rule a curve must satisfy (public for tests). Returns the points rounded to 0.1.</summary>
    public static List<double[]> CheckPoints(IReadOnlyList<double[]>? pts, FanRule r, double emergencyC)
    {
        if (pts == null) throw new ArgumentException("points are required");
        if (pts.Count < MinPoints || pts.Count > MaxPoints) throw new ArgumentException($"a curve needs {MinPoints}-{MaxPoints} points (got {pts.Count})");
        var o = new List<double[]>();
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (p == null || p.Length != 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1])) throw new ArgumentException($"point {i + 1} must be [tempC, duty%]");
            if (p[0] < MinTempC || p[0] > MaxTempC) throw new ArgumentException($"point {i + 1}: temperature {p[0]} °C must be within {MinTempC}..{MaxTempC}");
            if (p[1] < 0 || p[1] > 100) throw new ArgumentException($"point {i + 1}: duty {p[1]}% must be within 0..100");
            if (p[1] < r.MinDuty || p[1] > r.MaxDuty) throw new ArgumentException($"point {i + 1}: duty {p[1]}% is outside the rule's {r.MinDuty}..{r.MaxDuty}%");
            if (i > 0 && p[0] <= pts[i - 1][0]) throw new ArgumentException($"point {i + 1}: temperatures must strictly increase");
            if (i > 0 && p[1] < pts[i - 1][1]) throw new ArgumentException($"point {i + 1}: duty must not decrease as temperature rises");
            o.Add(new[] { Math.Round(p[0], 1), Math.Round(p[1], 1) });
        }
        double limit = emergencyC - EmergencyMarginC;
        if (!o.Any(p => p[1] >= 100 && p[0] <= limit)) throw new ArgumentException($"the curve must reach 100% at or below {limit:0} °C (the emergency temperature {emergencyC:0} °C minus {EmergencyMarginC:0})");
        if (FanEngine.Interpolate(o, FloorFromC) < FloorDuty) throw new ArgumentException($"duty must be at least {FloorDuty}% from {FloorFromC} °C up (it is {FanEngine.Interpolate(o, FloorFromC):0}% at {FloorFromC} °C)");
        return o;
    }

    // ------------------------------------------------------------------ writes

    void WriteGap(bool touch = false)
    {
        var now = _now();
        var wait = MinWriteGapSec - (now - _lastWrite).TotalSeconds;
        if (wait > 0) throw new InvalidOperationException($"rate limited: config writes are limited to one per {MinWriteGapSec:0} s, wait {Math.Ceiling(wait):0} s");
        _lastWrite = now;
    }

    /// <summary>The one validated path: ConfigValidator first, then the owner's Apply (never called with an invalid config).</summary>
    void Commit(GlintConfig c)
    {
        var v = ConfigValidator.Check(c, _ctx());
        if (!v.Ok) throw new InvalidOperationException("Glint refused the change: " + string.Join("; ", v.Errors.Take(5)));
        _apply(c);
    }
}
