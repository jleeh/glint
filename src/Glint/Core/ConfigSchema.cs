using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Glint.Core;

/// <summary>
/// Valid ranges for config values. The validator and the schema (config.schema) both read these, so the
/// documented range is always the enforced one. Keep this file next to Config.cs when fields change.
/// </summary>
public static class Limits
{
    public const double TempMin = -20, TempMax = 150;          // FanEngine treats readings outside this as lost
    public const int DutyMin = 0, DutyMax = 100;
    public const int FailsafeMin = 30;                          // a fail-safe below this would not protect anything
    public const int PollMin = 100, PollMax = 10000;
    public const double RampMin = 0.1, RampMax = 1000;
    public const double HysteresisMax = 30, SmoothingMax = 600, WeightMax = 100;
    public const int BrightnessMin = 0, BrightnessMax = 100;
    public const int FpsMin = 1, FpsMax = 240, FpsEffectiveMax = 60;
    public const int LedCountMax = 2000;
    public const double SpeedMax = 50, ScaleMax = 100, SizeMax = 1000, DensityMax = 50, PlacementMin = -1, PlacementMax = 2;
    public static readonly string[] WellKnownSensors = { "cpu", "gpu", "gpu.hotspot", "liquid", "storage", "ram" };
    public static readonly string[] SensorFunctions = { "max", "min", "avg" };
    public static readonly Regex Color = new("^#[0-9a-fA-F]{6}$");
    public const double OverlayScaleMin = 0.5, OverlayScaleMax = 4, OverlayOpacityMin = 0.1;
    public const int OverlayOffsetMin = -200, OverlayOffsetMax = 4000, OverlayItemsMax = 24, OverlayLabelMax = 32;
    public static readonly string[] OverlayPositions = { "top-left", "top-right", "bottom-left", "bottom-right" };
    public static readonly string[] OverlayLayouts = { "compact", "detailed" };
    public const int HostMax = 253;
}

/// <summary>What the validator knows about the live system (all optional).</summary>
public sealed class ValidationContext
{
    /// <summary>Sensor ids currently reading (friendly + raw).</summary>
    public IEnumerable<string>? Sensors { get; init; }
    /// <summary>Fan output ids currently connected.</summary>
    public IEnumerable<string>? Fans { get; init; }
    /// <summary>Loaded plugin effect names.</summary>
    public IEnumerable<string>? PluginEffects { get; init; }
    /// <summary>The config being replaced: problems it already had are reported as warnings, not errors,
    /// so an older config never locks every write out; sensors it already uses count as known.</summary>
    public GlintConfig? Previous { get; init; }
}

public sealed record ValidationResult(List<string> Errors, List<string> Warnings)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>One validator for every config write (control API, MCP, dashboard).</summary>
public static class ConfigValidator
{
    /// <summary>Errors only, with no knowledge of the live system (syntax, ranges, references inside the config).</summary>
    public static List<string> Validate(GlintConfig cfg) => Check(cfg, null).Errors;

    /// <summary>Why this is not an acceptable profile name (null = fine): non-empty, at most 40 characters, no control characters, not "Stock".</summary>
    public static string? ProfileNameError(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "a profile has an empty name";
        if (name.Length > 40) return $"profile name \"{name[..20]}…\" is longer than 40 characters";
        if (name.Any(char.IsControl)) return "a profile name contains control characters";
        if (name.Trim().Equals("Stock", StringComparison.OrdinalIgnoreCase)) return "\"Stock\" is reserved (it means no overclock)";
        return null;
    }

    public static ValidationResult Check(GlintConfig cfg, ValidationContext? ctx)
    {
        var errors = new List<string>(); var warnings = new List<string>();
        Run(cfg, ctx, errors, warnings);
        if (ctx?.Previous != null && errors.Count > 0)
        {
            // problems the current config already has are not the caller's fault: warn, don't block
            var before = new List<string>(); Run(ctx.Previous, new ValidationContext { Sensors = ctx.Sensors, Fans = ctx.Fans, PluginEffects = ctx.PluginEffects }, before, new List<string>());
            var old = before.ToHashSet();
            foreach (var e in errors.Where(old.Contains).ToList()) { errors.Remove(e); warnings.Add("(already in the current config) " + e); }
        }
        return new ValidationResult(errors, warnings.Distinct().ToList());
    }

    static void Run(GlintConfig c, ValidationContext? ctx, List<string> E, List<string> W)
    {
        void Range(string what, double v, double lo, double hi) { if (double.IsNaN(v) || v < lo || v > hi) E.Add($"{what} = {v} is out of range {lo}..{hi}"); }

        Range("pollMs", c.PollMs, Limits.PollMin, Limits.PollMax);
        Range("failsafeDuty", c.FailsafeDuty, Limits.FailsafeMin, Limits.DutyMax);

        // ---- curves
        c.Curves ??= new();
        foreach (var (name, pts) in c.Curves)
        {
            if (string.IsNullOrWhiteSpace(name)) E.Add("curves: a curve has an empty name");
            CheckPoints($"curve \"{name}\"", pts, E, W);
        }

        // ---- sensors / fans known to the live system
        var known = new HashSet<string>(Limits.WellKnownSensors, StringComparer.OrdinalIgnoreCase);
        bool haveLive = ctx?.Sensors != null;
        var live = new HashSet<string>(ctx?.Sensors ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        known.UnionWith(live);
        if (ctx?.Previous != null)
            foreach (var r in ctx.Previous.Profiles.Values.SelectMany(x => x)) foreach (var i in r.Inputs) known.UnionWith(SensorLeaves(i.Sensor));
        var fans = ctx?.Fans == null ? null : new HashSet<string>(ctx.Fans, StringComparer.OrdinalIgnoreCase);

        // ---- profiles
        c.Profiles ??= new();
        if (c.Profiles.Count == 0) E.Add("profiles: there must be at least one fan profile");
        else if (!c.Profiles.ContainsKey(c.ActiveProfile ?? "")) E.Add($"activeProfile \"{c.ActiveProfile}\" is not one of the profiles ({string.Join(", ", c.Profiles.Keys)})");
        foreach (var dup in c.Profiles.Keys.GroupBy(k => k.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            E.Add($"profiles: names must be unique ignoring case ({string.Join(", ", dup.Select(x => "\"" + x + "\""))})");
        foreach (var (pname, rules) in c.Profiles)
        {
            if (ProfileNameError(pname) is { } pe) E.Add($"profiles: {pe}");
            if (rules == null) { E.Add($"profile \"{pname}\" is null (use [] for an empty profile)"); continue; }
            foreach (var dup in rules.GroupBy(r => r.Name).Where(g => g.Count() > 1)) E.Add($"profile \"{pname}\": rule name \"{dup.Key}\" is used {dup.Count()} times (names must be unique)");
            foreach (var dup in rules.SelectMany(r => r.Fans.Select(f => (f, r.Name))).GroupBy(x => x.f, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                W.Add($"profile \"{pname}\": fan \"{dup.Key}\" is in several rules ({string.Join(", ", dup.Select(x => x.Name))}); the last one wins");
            foreach (var r in rules)
            {
                string at = $"profile \"{pname}\" rule \"{r.Name}\"";
                if (string.IsNullOrWhiteSpace(r.Name)) E.Add($"profile \"{pname}\": a rule has an empty name");
                Range($"{at}: minDuty", r.MinDuty, Limits.DutyMin, Limits.DutyMax);
                Range($"{at}: maxDuty", r.MaxDuty, Limits.DutyMin, Limits.DutyMax);
                if (r.MinDuty > r.MaxDuty) E.Add($"{at}: minDuty {r.MinDuty} is greater than maxDuty {r.MaxDuty}");
                if (r.Fixed is int f) Range($"{at}: fixed", f, Limits.DutyMin, Limits.DutyMax);
                if (r.Override is int o) Range($"{at}: override", o, Limits.DutyMin, Limits.DutyMax);
                Range($"{at}: rampUpPerSec", r.RampUpPerSec, Limits.RampMin, Limits.RampMax);
                Range($"{at}: rampDownPerSec", r.RampDownPerSec, Limits.RampMin, Limits.RampMax);
                if (!Enum.IsDefined(r.Combine)) E.Add($"{at}: combine must be max, average or weighted");
                r.Fans ??= new(); r.Inputs ??= new();
                if (r.Fans.Count == 0) W.Add($"{at}: has no fans (it does nothing)");
                foreach (var fan in r.Fans)
                {
                    if (string.IsNullOrWhiteSpace(fan)) E.Add($"{at}: empty fan id");
                    else if (fans != null && !fans.Contains(fan)) W.Add($"{at}: fan \"{fan}\" is not currently connected (kept; see fans.list for connected ids)");
                }
                if (r.Fixed == null && r.Inputs.Count == 0) E.Add($"{at}: needs at least one input (or a fixed duty); without inputs it runs at the fail-safe duty");
                for (int i = 0; i < r.Inputs.Count; i++)
                {
                    var inp = r.Inputs[i];
                    string ia = $"{at} input {i + 1} ({inp.Sensor})";
                    var syntax = SensorSyntax(inp.Sensor);
                    if (syntax != null) E.Add($"{ia}: {syntax}");
                    else foreach (var leaf in SensorLeaves(inp.Sensor))
                    {
                        if (!known.Contains(leaf)) E.Add($"{ia}: unknown sensor \"{leaf}\" (use an id from fans.list 'sensors' or sensors.list)");
                        else if (haveLive && !live.Contains(leaf)) W.Add($"{ia}: sensor \"{leaf}\" is not reading right now (the rule will run at the fail-safe duty until it is)");
                    }
                    if (inp.Points != null) CheckPoints($"{ia} inline points", inp.Points, E, W);
                    else if (string.IsNullOrWhiteSpace(inp.Curve)) E.Add($"{ia}: needs a curve name or inline points");
                    else if (!c.Curves.ContainsKey(inp.Curve)) E.Add($"{ia}: unknown curve \"{inp.Curve}\" (curves: {string.Join(", ", c.Curves.Keys)})");
                    Range($"{ia}: weight", inp.Weight, 0, Limits.WeightMax);
                    Range($"{ia}: hysteresis", inp.Hysteresis, 0, Limits.HysteresisMax);
                    Range($"{ia}: smoothingSec", inp.SmoothingSec, 0, Limits.SmoothingMax);
                }
                if (r.Combine == Combine.Weighted && r.Fixed == null && r.Inputs.Count > 0 && r.Inputs.Sum(x => x.Weight) <= 0)
                    E.Add($"{at}: combine 'weighted' needs at least one input with weight > 0");
            }
        }

        // ---- lighting
        var L = c.Lighting ??= new();
        Range("lighting.brightness", L.Brightness, Limits.BrightnessMin, Limits.BrightnessMax);
        Range("lighting.fps", L.Fps, Limits.FpsMin, Limits.FpsMax);
        if (L.Fps > Limits.FpsEffectiveMax) W.Add($"lighting.fps {L.Fps}: animated effects are capped at {Limits.FpsEffectiveMax}");
        var plugins = ctx?.PluginEffects == null ? null : new HashSet<string>(ctx.PluginEffects, StringComparer.OrdinalIgnoreCase);
        if (L.Effect == null) E.Add("lighting.effect is missing");
        else CheckEffect("lighting.effect", L.Effect, plugins, E, W, 0);
        foreach (var (k, fx) in L.Overrides ?? new()) CheckEffect($"lighting.overrides[\"{k}\"]", fx, plugins, E, W, 0);
        foreach (var (k, fx) in L.Presets ?? new()) CheckEffect($"lighting.presets[\"{k}\"]", fx, plugins, E, W, 0);
        foreach (var (k, n) in L.LedCounts ?? new()) Range($"lighting.ledCounts[\"{k}\"]", n, 0, Limits.LedCountMax);
        foreach (var (k, p) in L.Layout ?? new()) CheckPlacement($"lighting.layout[\"{k}\"]", p, E);

        // ---- Home Assistant
        var ha = c.HomeAssistant;
        if (ha != null)
        {
            Range("homeAssistant.port", ha.Port, 1, 65535);
            if (ha.Enabled && string.IsNullOrWhiteSpace(ha.Host)) E.Add("homeAssistant.host is required when homeAssistant.enabled is true");
            foreach (var (n, v) in new[] { ("discoveryPrefix", ha.DiscoveryPrefix), ("baseTopic", ha.BaseTopic) })
                if (string.IsNullOrWhiteSpace(v) || v.IndexOfAny(new[] { '+', '#', ' ' }) >= 0 || v.StartsWith('/') || v.EndsWith('/'))
                    E.Add($"homeAssistant.{n} \"{v}\" must be a non-empty MQTT topic without spaces, wildcards (+ #) or leading/trailing '/'");
        }
        if (c.Sensors == null) E.Add("sensors options are missing (use { storage, memory, psu, controllers })");
        if (c.Updates == null) E.Add("updates is missing (use { check: true, autoInstall: false })");
        else if (c.Updates.AutoInstall && !c.Updates.Check) E.Add("updates.autoInstall needs updates.check (the check is what finds a new release); switch autoInstall off first or check on");

        // ---- OpenRGB bridge
        if (c.OpenRgb == null) E.Add("openRgb is missing (use {} for the defaults)");
        else
        {
            Range("openRgb.port", c.OpenRgb.Port, 1, 65535);
            if (string.IsNullOrWhiteSpace(c.OpenRgb.Host) || c.OpenRgb.Host.Length > Limits.HostMax || c.OpenRgb.Host.Any(ch => char.IsWhiteSpace(ch) || ch is '/' or '\\' or '@'))
                E.Add($"openRgb.host \"{c.OpenRgb.Host}\" must be a host name or IP address (e.g. 127.0.0.1)");
            if (c.OpenRgb.Exclude == null) E.Add("openRgb.exclude is missing (use [] for none)");
            else if (c.OpenRgb.Exclude.Any(string.IsNullOrWhiteSpace)) E.Add("openRgb.exclude has an empty entry");
        }

        // ---- tuning (overclocking; off by default)
        if (c.Tuning == null) E.Add("tuning is missing (use {} for the defaults)");
        else
        {
            var t = c.Tuning;
            if (!(t.EmergencyCoreC >= Glint.Tuning.TuningLimits.MinEmergencyC && t.EmergencyCoreC <= Glint.Tuning.TuningLimits.MaxEmergencyCoreC))
                E.Add($"tuning.emergencyCoreC must be {Glint.Tuning.TuningLimits.MinEmergencyC}..{Glint.Tuning.TuningLimits.MaxEmergencyCoreC} °C");
            if (!(t.EmergencyMemC >= Glint.Tuning.TuningLimits.MinEmergencyC + 10 && t.EmergencyMemC <= Glint.Tuning.TuningLimits.MaxEmergencyMemC))
                E.Add($"tuning.emergencyMemC must be {Glint.Tuning.TuningLimits.MinEmergencyC + 10}..{Glint.Tuning.TuningLimits.MaxEmergencyMemC} °C");
            Range("tuning.maxCrashes", t.MaxCrashes, 1, 20);
            Range("tuning.crashWindowDays", t.CrashWindowDays, 1, 90);
        }

        // ---- overlay
        if (c.Overlay == null) E.Add("overlay is missing (use {} for the defaults)");
        else CheckOverlay(c.Overlay, haveLive ? live : null, fans, E, W);
    }

    public static void CheckOverlay(OverlayConfig o, HashSet<string>? liveSensors, HashSet<string>? fans, List<string> E, List<string> W)
    {
        void Range(string what, double v, double lo, double hi) { if (double.IsNaN(v) || v < lo || v > hi) E.Add($"overlay.{what} = {v} is out of range {lo}..{hi}"); }
        if (GameOverlay.Hotkey.Error(o.Hotkey) is string hk) E.Add("overlay." + hk);
        if (!Limits.OverlayPositions.Contains(o.Position)) E.Add($"overlay.position \"{o.Position}\" must be one of {string.Join(", ", Limits.OverlayPositions)}");
        if (!Limits.OverlayLayouts.Contains(o.Layout)) E.Add($"overlay.layout \"{o.Layout}\" must be compact or detailed");
        Range("offsetX", o.OffsetX, Limits.OverlayOffsetMin, Limits.OverlayOffsetMax);
        Range("offsetY", o.OffsetY, Limits.OverlayOffsetMin, Limits.OverlayOffsetMax);
        Range("scale", o.Scale, Limits.OverlayScaleMin, Limits.OverlayScaleMax);
        Range("opacity", o.Opacity, Limits.OverlayOpacityMin, 1);
        Range("backgroundOpacity", o.BackgroundOpacity, 0, 1);
        if (o.Accent != null && !Limits.Color.IsMatch(o.Accent)) E.Add($"overlay.accent \"{o.Accent}\" is not a #rrggbb colour (or null for the Glint gradient)");
        if (o.Items == null) { E.Add("overlay.items is missing (use [] for nothing)"); return; }
        if (o.Items.Count > Limits.OverlayItemsMax) E.Add($"overlay.items has {o.Items.Count} entries (max {Limits.OverlayItemsMax})");
        if (o.Items.Count == 0) W.Add("overlay.items is empty: the overlay shows nothing");
        for (int i = 0; i < o.Items.Count; i++)
        {
            var it = o.Items[i];
            string at = $"overlay.items[{i}]";
            if (it == null) { E.Add($"{at} is null"); continue; }
            if (GameOverlay.OverlayItems.SourceSyntax(it.Source) is string se) { E.Add($"{at}.source: {se}"); continue; }
            if (it.Label != null && it.Label.Length > Limits.OverlayLabelMax) E.Add($"{at}.label is longer than {Limits.OverlayLabelMax} characters");
            if (it.Color != null && !Limits.Color.IsMatch(it.Color)) E.Add($"{at}.color \"{it.Color}\" is not a #rrggbb colour");
            var src = it.Source;
            if (GameOverlay.OverlayItems.Builtins.Contains(src)) continue;
            if (src.StartsWith("fan:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("duty:", StringComparison.OrdinalIgnoreCase))
            {
                var id = src[(src.IndexOf(':') + 1)..];
                if (fans != null && !fans.Contains(id)) W.Add($"{at}: fan \"{id}\" is not connected right now (hidden until it is)");
            }
            else if (liveSensors != null && !liveSensors.Contains(src) && !Limits.WellKnownSensors.Contains(src, StringComparer.OrdinalIgnoreCase))
                W.Add($"{at}: sensor \"{src}\" is not reading right now (hidden until it is)");
        }
    }

    public static void CheckPoints(string at, List<double[]>? pts, List<string> E, List<string> W)
    {
        if (pts == null || pts.Count == 0) { E.Add($"{at}: needs at least one [tempC, duty%] point"); return; }
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (p == null || p.Length != 2) { E.Add($"{at}: point {i + 1} must be [tempC, duty%]"); return; }
            if (double.IsNaN(p[0]) || p[0] < Limits.TempMin || p[0] > Limits.TempMax) E.Add($"{at}: point {i + 1} temperature {p[0]} °C is outside {Limits.TempMin}..{Limits.TempMax}");
            if (double.IsNaN(p[1]) || p[1] < Limits.DutyMin || p[1] > Limits.DutyMax) E.Add($"{at}: point {i + 1} duty {p[1]}% is outside {Limits.DutyMin}..{Limits.DutyMax}");
            if (i > 0 && pts[i - 1] is { Length: 2 } q)
            {
                if (p[0] <= q[0]) E.Add($"{at}: temperatures must strictly increase (point {i + 1}: {p[0]} °C after {q[0]} °C)");
                else if (p[1] < q[1]) W.Add($"{at}: duty drops from {q[1]}% to {p[1]}% as temperature rises (point {i + 1})");
            }
        }
    }

    static void CheckEffect(string at, EffectConfig? fx, HashSet<string>? plugins, List<string> E, List<string> W, int depth)
    {
        if (fx == null) { E.Add($"{at} is null"); return; }
        if (!Enum.IsDefined(fx.Type)) E.Add($"{at}.type: unknown effect type ({string.Join(", ", Enum.GetNames<EffectType>().Select(Camel))})");
        if (fx.Type == EffectType.Plugin)
        {
            if (string.IsNullOrWhiteSpace(fx.Plugin)) E.Add($"{at}: type 'plugin' needs 'plugin' (the plugin effect's name)");
            else if (plugins != null && !plugins.Contains(fx.Plugin)) W.Add($"{at}: plugin effect \"{fx.Plugin}\" is not loaded (see plugins.list)");
        }
        foreach (var col in fx.Colors ?? new()) if (col == null || !Limits.Color.IsMatch(col)) E.Add($"{at}.colors: \"{col}\" is not a #rrggbb colour");
        if (fx.Background != null && !Limits.Color.IsMatch(fx.Background)) E.Add($"{at}.background: \"{fx.Background}\" is not a #rrggbb colour");
        void Range(string what, double v, double lo, double hi) { if (double.IsNaN(v) || v < lo || v > hi) E.Add($"{at}.{what} = {v} is out of range {lo}..{hi}"); }
        Range("speed", fx.Speed, 0, Limits.SpeedMax);
        Range("scale", fx.Scale, 0, Limits.ScaleMax);
        Range("size", fx.Size, 0, Limits.SizeMax);
        Range("density", fx.Density, 0, Limits.DensityMax);
        Range("offset", fx.Offset, -100, 100);
        Range("angle", fx.Angle, -3600, 3600);
        if (fx.Range != null && (fx.Range.Length != 2 || fx.Range[0] >= fx.Range[1])) E.Add($"{at}.range must be [low, high] with low < high");
        if (fx.Center != null && (fx.Center.Length != 2 || fx.Center.Any(v => v < 0 || v > 1))) E.Add($"{at}.center must be [x, y] with both in 0..1");
        if (fx.Type == EffectType.Temperature)
        {
            if (string.IsNullOrWhiteSpace(fx.Sensor)) E.Add($"{at}: type 'temperature' needs 'sensor' (e.g. \"cpu\" or \"max(cpu,gpu)\")");
            else if (SensorSyntax(fx.Sensor) is string s) E.Add($"{at}.sensor: {s}");
        }
        if (fx.Layers != null)
        {
            if (depth > 2) E.Add($"{at}.layers: nesting too deep");
            else for (int i = 0; i < fx.Layers.Count; i++) CheckEffect($"{at}.layers[{i}]", fx.Layers[i], plugins, E, W, depth + 1);
        }
    }

    public static void CheckPlacement(string at, ZonePlacement? p, List<string> E)
    {
        if (p == null) { E.Add($"{at} is null"); return; }
        if (new[] { p.X, p.Y, p.W, p.H, p.Rotation }.Any(v => double.IsNaN(v) || double.IsInfinity(v))) { E.Add($"{at}: values must be finite numbers"); return; }
        if (p.X < Limits.PlacementMin || p.X > Limits.PlacementMax || p.Y < Limits.PlacementMin || p.Y > Limits.PlacementMax) E.Add($"{at}: x/y must be within {Limits.PlacementMin}..{Limits.PlacementMax} (canvas is 0..1)");
        if (p.W <= 0 || p.H <= 0 || p.W > Limits.PlacementMax || p.H > Limits.PlacementMax) E.Add($"{at}: w/h must be > 0 and <= {Limits.PlacementMax}");
    }

    /// <summary>Null if the sensor expression is well-formed ("cpu", "xt:probe1", "max(cpu,avg(gpu,liquid))").</summary>
    public static string? SensorSyntax(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "sensor is empty";
        id = id.Trim();
        int p = id.IndexOf('(');
        if (p < 0) return id.IndexOfAny(new[] { ')', ',' }) >= 0 ? $"malformed sensor expression \"{id}\"" : null;
        if (!id.EndsWith(')')) return $"malformed sensor expression \"{id}\" (missing ')')";
        var fn = id[..p].Trim().ToLowerInvariant();
        if (!Limits.SensorFunctions.Contains(fn)) return $"unknown sensor function \"{fn}\" (use max, min or avg)";
        var args = SplitArgs(id[(p + 1)..^1]);
        if (args == null || args.Count == 0) return $"malformed sensor expression \"{id}\"";
        foreach (var a in args) if (SensorSyntax(a) is string e) return e;
        return null;
    }

    /// <summary>Plain sensor ids used by an expression.</summary>
    public static IEnumerable<string> SensorLeaves(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) yield break;
        id = id.Trim();
        int p = id.IndexOf('(');
        if (p > 0 && id.EndsWith(')'))
        {
            foreach (var a in SplitArgs(id[(p + 1)..^1]) ?? new()) foreach (var l in SensorLeaves(a)) yield return l;
        }
        else yield return id;
    }

    static List<string>? SplitArgs(string s)
    {
        var res = new List<string>(); int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') { if (--depth < 0) return null; }
            else if (s[i] == ',' && depth == 0) { res.Add(s[start..i].Trim()); start = i + 1; }
        }
        if (depth != 0) return null;
        res.Add(s[start..].Trim());
        return res.Any(string.IsNullOrEmpty) ? null : res;
    }

    static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];

    // ------------------------------------------------------------------ schema

    /// <summary>
    /// Every config field with type, meaning, valid range/enum, default and example (served as config.schema).
    /// Hand-written so it can explain intent; the ranges come from <see cref="Limits"/>.
    /// </summary>
    public static JsonObject Schema()
    {
        JsonObject F(string type, string desc, object? def = null, object? example = null, double? min = null, double? max = null, IEnumerable<string>? @enum = null, JsonObject? fields = null, JsonObject? items = null)
        {
            var o = new JsonObject { ["type"] = type, ["description"] = desc };
            if (min != null) o["min"] = min;
            if (max != null) o["max"] = max;
            if (@enum != null) o["enum"] = new JsonArray(@enum.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
            if (def != null) o["default"] = System.Text.Json.JsonSerializer.SerializeToNode(def, GlintConfig.Json);
            if (example != null) o["example"] = System.Text.Json.JsonSerializer.SerializeToNode(example, GlintConfig.Json);
            if (fields != null) o["fields"] = fields;
            if (items != null) o["items"] = items;
            return o;
        }
        var effectTypes = Enum.GetNames<EffectType>().Select(Camel).ToList();
        var sensorDoc = "Sensor id: a friendly id (" + string.Join(", ", Limits.WellKnownSensors) + "), a device id like 'xt:probe1' / 'st:liquid', a raw LibreHardwareMonitor id from sensors.list, " +
                        "or an expression max(a,b) / min(a,b) / avg(a,b) (nestable). A missing or implausible reading (outside " + Limits.TempMin + ".." + Limits.TempMax + " °C) makes the rule run at failsafeDuty.";
        var points = F("array", $"Curve points [[tempC, duty%], ...]. Temperatures strictly increasing within {Limits.TempMin}..{Limits.TempMax}; duty {Limits.DutyMin}..{Limits.DutyMax}. Linear between points, flat beyond the ends.",
            example: new[] { new[] { 40.0, 25 }, new[] { 60.0, 35 }, new[] { 75.0, 60 }, new[] { 85.0, 100 } });

        var input = new JsonObject
        {
            ["sensor"] = F("string", sensorDoc, "cpu", "max(cpu,gpu)"),
            ["curve"] = F("string", "Name of a curve in 'curves'. Required unless 'points' is given.", example: "cpu-balanced"),
            ["points"] = F("array|null", "Inline curve instead of a named one (same format as a curve). Wins over 'curve'.", example: new[] { new[] { 30.0, 30 }, new[] { 45.0, 100 } }),
            ["weight"] = F("number", "Weight when the rule's combine is 'weighted'.", 1, 2, 0, Limits.WeightMax),
            ["hysteresis"] = F("number", "°C the temperature must fall below its peak before the curve backs off (stops fans hunting).", 2, 3, 0, Limits.HysteresisMax),
            ["smoothingSec"] = F("number", "Exponential smoothing time constant on the reading, seconds (use ~4 for spiky CPU temps).", 0, 4, 0, Limits.SmoothingMax),
        };
        var rule = new JsonObject
        {
            ["name"] = F("string", "Unique name within the profile. Used by fans.override, fans.setRule and Home Assistant.", "Fan rule", "Case fans"),
            ["fans"] = F("array of string", "Fan output ids this rule drives (see fans.list / devices). Ids of fans not currently connected are allowed (warning).", example: new[] { "xt:fan1", "xt:fan2" }),
            ["inputs"] = F("array of input", "Sensor + curve pairs; each gives a duty, then they are combined.", fields: input),
            ["combine"] = F("string", "How input duties combine: max = the hottest wins, average, weighted (by input weight).", "max", "max", @enum: new[] { "max", "average", "weighted" }),
            ["fixed"] = F("integer|null", "If set, ignore inputs and hold this duty %.", null, 100, Limits.DutyMin, Limits.DutyMax),
            ["override"] = F("integer|null", "Manual duty % (Home Assistant / fans.override). Clamped to minDuty..maxDuty. null = automatic. The fail-safe still wins (lost sensor, or curve at failsafeDuty).", null, 50, Limits.DutyMin, Limits.DutyMax),
            ["minDuty"] = F("integer", "Lowest duty % the rule outputs (pumps: keep >= 60). Must be <= maxDuty.", 20, 30, Limits.DutyMin, Limits.DutyMax),
            ["maxDuty"] = F("integer", "Highest duty % the rule outputs.", 100, 100, Limits.DutyMin, Limits.DutyMax),
            ["rampUpPerSec"] = F("number", "Max duty increase per second (% per s). Fail-safe, fixed and manual jumps are not limited.", 25, 25, Limits.RampMin, Limits.RampMax),
            ["rampDownPerSec"] = F("number", "Max duty decrease per second (% per s). Low = fans spin down gently.", 4, 4, Limits.RampMin, Limits.RampMax),
        };
        var effect = new JsonObject
        {
            ["type"] = F("string", "Effect. off; static (colors[0]); gradient (palette across direction 'angle'); breathing (fades through colors); spectrum (whole rig cycles hue, or palette); " +
                "wave (rainbow along angle, perZone = around each ring); colorWave (palette sweeping); spin (arc around each fan ring: size = arc, offset = stagger); marquee (blocks, size = block LEDs); " +
                "comet (band + tail, size = tail); meteor (streaks, density); twinkle (sparkles, density, background); fire; plasma (scale); rain (density, background); strobe; " +
                "temperature (colour from 'sensor' across 'range' via colors); audio (spectrum analyser); audioPulse (flash on bass, background); ripple (rings from 'center', size, scale); " +
                "plugin (a JavaScript effect: set 'plugin' and its 'params').", "static", "wave", @enum: effectTypes),
            ["colors"] = F("array of string", "Palette, '#rrggbb' each.", new[] { "#ffffff" }, new[] { "#ff0040", "#00a0ff" }),
            ["speed"] = F("number", "Cycles per second.", 0.2, 0.5, 0, Limits.SpeedMax),
            ["sensor"] = F("string|null", "For type temperature: sensor id or expression (same syntax as fan inputs).", example: "max(cpu,gpu)"),
            ["range"] = F("[number, number]|null", "For type temperature: [low °C, high °C] mapped across the palette. low < high.", new[] { 40.0, 85 }, new[] { 35.0, 80 }),
            ["perZone"] = F("boolean", "Run around each fan ring / strip instead of across the whole rig canvas.", false, true),
            ["reverse"] = F("boolean", "Reverse direction.", false, true),
            ["scale"] = F("number", "How many repeats of the pattern fit across the rig.", 1, 2, 0, Limits.ScaleMax),
            ["size"] = F("number", "Tail / arc / band length (0..1), or block size in LEDs for marquee.", 0.3, 0.5, 0, Limits.SizeMax),
            ["angle"] = F("number", "Direction across the layout canvas, degrees (0 = left to right, 90 = top to bottom).", 0, 45),
            ["center"] = F("[number, number]|null", "Origin for ripple, [x, y] in 0..1 of the canvas.", null, new[] { 0.45, 0.42 }),
            ["offset"] = F("number", "Per-zone phase offset (staggered spinners).", 0, 0.15),
            ["density"] = F("number", "Spawn rate for twinkle / meteor / rain.", 0.5, 0.6, 0, Limits.DensityMax),
            ["background"] = F("string|null", "Background '#rrggbb' for twinkle / rain / ripple / audioPulse.", null, "#020210"),
            ["plugin"] = F("string|null", "For type plugin: the plugin effect's name (plugins.list, kind effect).", null, "Aurora"),
            ["params"] = F("object|null", "For type plugin: values of the plugin's declared params (see plugins.get).", null, new Dictionary<string, object> { ["speed"] = 0.5 }),
            ["layers"] = F("array of effect|null", "Effects added on top (additive), e.g. static base + twinkle sparkles. Max 3 levels deep.", null),
        };
        var placement = new JsonObject
        {
            ["x"] = F("number", "Left edge on the 16:10 canvas (0..1).", 0, 0.4, Limits.PlacementMin, Limits.PlacementMax),
            ["y"] = F("number", "Top edge (0..1).", 0, 0.3, Limits.PlacementMin, Limits.PlacementMax),
            ["w"] = F("number", "Width as a fraction of canvas width (> 0).", 0.08, 0.085, 0, Limits.PlacementMax),
            ["h"] = F("number", "Height as a fraction of canvas height (> 0). A square on screen has h = w * 1.6.", 0.08, 0.136, 0, Limits.PlacementMax),
            ["rotation"] = F("number", "Degrees clockwise.", 0, 90),
        };
        var fields = new JsonObject
        {
            ["pollMs"] = F("integer", "Sensor read / fan update period in ms.", 1000, 1000, Limits.PollMin, Limits.PollMax),
            ["fanControl"] = F("boolean", "Global fan-control switch. false = Glint stops driving fans: motherboard headers, coolers and GPU fans go back to their BIOS / driver curves (and hubs whose plugin can release fans alone go back to their firmware); Glint keeps reading sensors. Hubs that can't hand fans back without also taking their lighting stay on Glint's curves. Glint's fail-safe doesn't apply to fans it has handed back.", true, false),
            ["failsafeDuty"] = F("integer", "Duty % used when a sensor a rule needs is missing or implausible, or when a curve reaches it. Beats manual overrides.", 100, 100, Limits.FailsafeMin, Limits.DutyMax),
            ["curves"] = F("object: name -> points", "Named fan curves, referenced by rule inputs. Edit with curves.set / curves.remove.", example: new Dictionary<string, object> { ["cpu-balanced"] = new[] { new[] { 40.0, 25 }, new[] { 85.0, 100 } } }, items: points),
            ["activeProfile"] = F("string", "Name of the profile in use. Must be a key of 'profiles'. Switch with profile.set.", "Balanced", "Quiet"),
            ["profiles"] = F("object: name -> array of rule", "Fan profiles. Each is a list of rules; each rule drives some fans. Edit with fans.setRule / fans.removeRule / profiles.create / profiles.remove.", fields: rule),
            ["fanNames"] = F("object: fanId -> string", "Friendly names for fan outputs (display only).", example: new Dictionary<string, string> { ["xt:fan1"] = "Front top" }),
            ["sensorNames"] = F("object: sensorId -> string", "Friendly names for sensors (display only).", example: new Dictionary<string, string> { ["xt:probe1"] = "Intake air" }),
            ["disabledPlugins"] = F("array of string", "Plugin file names switched off (use plugins.setEnabled).", example: new[] { "aurora.js" }),
            ["sensors"] = F("object", "Optional LibreHardwareMonitor sensor groups (take effect after app.restart).", fields: new JsonObject
            {
                ["storage"] = F("boolean", "Drive temperatures (SMART), friendly id 'storage' = hottest drive.", true, false),
                ["memory"] = F("boolean", "DIMM temperatures, friendly id 'ram'.", true, false),
                ["psu"] = F("boolean", "PSU sensors (psu:*).", true, false),
                ["controllers"] = F("boolean", "Fan / cooler controllers seen by LibreHardwareMonitor.", true, false),
            }),
            ["updates"] = F("object", "Update check and one-click / automatic install. Installs only a newer release of this repository whose setup matches the release's SHA256SUMS.txt and has a GitHub build attestation from its release workflow (update.status, update.install).", fields: new JsonObject
            {
                ["check"] = F("boolean", "Ask GitHub for the latest release (one request to the releases/latest API at startup and every 24 h; no other network, no telemetry). Shown on the Setup check page, Settings and the tray menu.", true, false),
                ["autoInstall"] = F("boolean", "Install a newer verified release by itself: only after 10 minutes without keyboard/mouse input, with no fullscreen game in front and not on battery; at most one attempt per version per day. Needs check. Off by default.", false, true),
            }),
            ["tuning"] = F("object", "Overclocking, AI-driven: GPU core/memory offsets verified by Glint's own CUDA stress test with the power limit raised to the card's own maximum (never voltage), per-core Ryzen Curve Optimizer, and crash watch with automatic back-off. Everything is off by default; tuning.status shows the hardware, limits, results and crashes.", fields: new JsonObject
            {
                ["enabled"] = F("boolean", "Show the Tuning page and allow tests. Off = Glint never touches clocks.", false, true),
                ["allowWorkstation"] = F("boolean", "Allow tuning a workstation / data-centre GPU (RTX PRO, RTX A, Quadro). They're validated at stock clocks, so this is an explicit opt-in.", false, true),
                ["applyAtStartup"] = F("boolean", "No longer used (kept for old configs): the active profile's overclock and undervolt always re-apply at startup, never right after a crash; repeated crashes revert them.", false, true),
                ["emergencyCoreC"] = F("number", "Emergency stop for tests (GPU back to stock). Reaching the card's own thermal limit is not a failure: it throttles and the tuner looks for the best sustained performance. Never more than 3 °C past the card's slowdown point.", 90, 90, Glint.Tuning.TuningLimits.MinEmergencyC, Glint.Tuning.TuningLimits.MaxEmergencyCoreC),
                ["emergencyMemC"] = F("number", "Emergency stop on memory-junction temperature.", 100, 100, Glint.Tuning.TuningLimits.MinEmergencyC + 10, Glint.Tuning.TuningLimits.MaxEmergencyMemC),
                ["unlockPower"] = F("boolean", "Raise the GPU power limit to the card's own maximum (its firmware ceiling) during tests and while a result is applied; back to default at stock. No voltage control.", true, true),
                ["crashWatch"] = F("boolean", "While an overclock is applied, watch the event log for driver resets, hardware errors and bugchecks.", true, true),
                ["autoBackoff"] = F("boolean", "On a GPU crash with an overclock applied, lower it a step (core -15 MHz, then memory -250).", true, true),
                ["maxCrashes"] = F("integer", "This many GPU crashes within crashWindowDays puts the GPU back to stock.", 3, 3, 1, 20),
                ["crashWindowDays"] = F("integer", "Window for maxCrashes, in days.", 7, 7, 1, 90),
            }),
            ["openRgb"] = F("object", "OpenRGB bridge (off by default; takes effect after app.restart). When enabled and an OpenRGB SDK server is reachable at startup, its devices become Glint lighting devices ('orgb-...'). Devices Glint already drives natively are skipped so two drivers never fight. Check with setup.check.", fields: new JsonObject
            {
                ["enabled"] = F("boolean", "Connect to the OpenRGB SDK server at startup.", false, true),
                ["host"] = F("string", "SDK server host (OpenRGB > SDK Server tab).", "127.0.0.1", "127.0.0.1"),
                ["port"] = F("integer", "SDK server port.", 6742, 6742, 1, 65535),
                ["exclude"] = F("array of string", "OpenRGB devices to leave alone: a device name (or part of one) or a Glint id ('orgb-1a2b3c').", example: new[] { "Razer" }),
            }),
            ["homeAssistant"] = F("object", "MQTT bridge to Home Assistant (off by default). Check with ha.status.", fields: new JsonObject
            {
                ["enabled"] = F("boolean", "Connect to the broker and publish discovery.", false, true),
                ["host"] = F("string", "MQTT broker host name or IP. Required when enabled.", "", "homeassistant.local"),
                ["port"] = F("integer", "Broker port (1883 plain, 8883 TLS).", 1883, 8883, 1, 65535),
                ["tls"] = F("boolean", "Use TLS.", false, true),
                ["username"] = F("string|null", "Broker user name.", null, "glint"),
                ["password"] = F("string|null", "Write-only: a plain password given here is encrypted for this Windows user into passwordProtected and never stored. \"\" clears it.", null, "secret"),
                ["passwordProtected"] = F("string|null", "Encrypted password (do not edit).", null),
                ["discoveryPrefix"] = F("string", "Home Assistant discovery prefix. No spaces, '+', '#', or leading/trailing '/'.", "homeassistant", "homeassistant"),
                ["baseTopic"] = F("string", "Topic prefix for Glint's state/command topics. Same rules as discoveryPrefix.", "glint", "glint"),
            }),
            ["overlay"] = F("object", "In-game overlay: FPS / frame time / 1% low from ETW present events (no injection, anti-cheat safe) plus sensors, drawn in a click-through topmost window on the foreground window's monitor. Toggle with overlay.toggle, the tray menu or the hotkey; read live values with overlay.stats. Exclusive-fullscreen games may hide it (borderless / windowed show it).", fields: new JsonObject
            {
                ["enabled"] = F("boolean", "Overlay on (what the tray toggle / hotkey / overlay.toggle flip).", false, true),
                ["hotkey"] = F("string", "Global toggle hotkey: modifiers Ctrl, Shift, Alt, Win + one key (A-Z, 0-9, F1-F24 or a key name). \"\" = none.", "Ctrl+Shift+O", "Alt+F10"),
                ["position"] = F("string", "Corner of the foreground window's monitor.", "top-left", "top-right", @enum: Limits.OverlayPositions),
                ["offsetX"] = F("integer", "Horizontal distance from the corner, px at 100% display scaling.", 16, 24, Limits.OverlayOffsetMin, Limits.OverlayOffsetMax),
                ["offsetY"] = F("integer", "Vertical distance from the corner, px at 100% display scaling.", 16, 24, Limits.OverlayOffsetMin, Limits.OverlayOffsetMax),
                ["scale"] = F("number", "Size multiplier (on top of the monitor's DPI scaling).", 1, 1.25, Limits.OverlayScaleMin, Limits.OverlayScaleMax),
                ["opacity"] = F("number", "Whole-overlay opacity.", 1, 0.9, Limits.OverlayOpacityMin, 1),
                ["backgroundOpacity"] = F("number", "Panel background opacity (0 = text only, with a drop shadow).", 0.72, 0.5, 0, 1),
                ["accent"] = F("string|null", "Accent '#rrggbb' for the side bar and FPS number; null = Glint's gradient.", null, "#00e5ff"),
                ["layout"] = F("string", "compact = one line; detailed = rows with labels (FPS as a large number).", "detailed", "compact", @enum: Limits.OverlayLayouts),
                ["onlyWhenGame"] = F("boolean", "Only show while a fullscreen or borderless-fullscreen window (not the desktop or Glint) is in front.", false, true),
                ["items"] = F("array of item", $"What to show, in order (max {Limits.OverlayItemsMax}). Sensors that aren't reading are left out.", OverlayConfig.DefaultItems(), fields: new JsonObject
                {
                    ["source"] = F("string", "fps | frametime | low1 (1% low, 99th-percentile frame time over ~10 s) | time | glint.cpu (Glint's own CPU %) | fan:<fanId> (rpm) | duty:<fanId> (%) | a sensor id (cpu, gpu, gpu.hotspot, liquid, storage, ram, xt:probe1, ... see sensors.list).", "fps", "gpu.hotspot"),
                    ["label"] = F("string|null", $"Label shown (max {Limits.OverlayLabelMax} chars); null = a default (FPS, 1% low, CPU, GPU, fan name...).", null, "Hotspot"),
                    ["color"] = F("string|null", "Value colour '#rrggbb'; null = white (FPS: the accent).", null, "#ff6a3d"),
                }),
            }),
            ["lighting"] = F("object", "RGB lighting.", fields: new JsonObject
            {
                ["enabled"] = F("boolean", "Lighting on/off.", true, true),
                ["brightness"] = F("integer", "Global brightness %.", 100, 60, Limits.BrightnessMin, Limits.BrightnessMax),
                ["fps"] = F("integer", $"Frame cap for animated effects (values above {Limits.FpsEffectiveMax} are capped).", 30, 30, Limits.FpsMin, Limits.FpsMax),
                ["effect"] = F("effect", "The effect for all lights (lighting.set / lighting.preset).", fields: effect),
                ["overrides"] = F("object: deviceId|zoneId -> effect", "Per device ('aura') or zone ('xt:fan1') effects that replace the main one there.", example: new Dictionary<string, object> { ["xt:fan1"] = new { type = "static", colors = new[] { "#ff0000" } } }),
                ["presets"] = F("object: name -> effect", "Saved effects (lighting.presets / lighting.preset)."),
                ["ledCounts"] = F("object: portId -> integer", "LED counts for ports that cannot report them ('aura:argb1', 'lnp:ch1'). 0 disables the port.", example: new Dictionary<string, int> { ["aura:argb1"] = 60 }, min: 0, max: Limits.LedCountMax),
                ["layout"] = F("object: zoneId -> placement", "Where each zone sits on the 16:10 layout canvas; zones not listed are auto-arranged. Usually hand-tuned by the user in the dashboard: prefer small edits; lighting.autoLayout replaces it (requires confirm, backs up first; restore with lighting.restoreLayout).", fields: placement),
            }),
        };
        return new JsonObject
        {
            ["about"] = "Glint config (config.json). Field names are camelCase. Read the live values with config.get; change one value with config.patch (dotted path, e.g. 'profiles.Quiet.0.minDuty'), or use the dedicated tools (fans.setRule, curves.set, fans.override, lighting.set). Every write is validated; an invalid change is rejected with {ok:false, errors:[...]} and nothing is saved.",
            ["fields"] = fields,
        };
    }
}
