using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glint.Core;

public sealed class GlintConfig
{
    /// <summary>Fan/sensor loop period.</summary>
    public int PollMs { get; set; } = 1000;
    /// <summary>Duty used when a curve's sensor is missing or stale.</summary>
    public int FailsafeDuty { get; set; } = 100;
    /// <summary>Global fan-control switch. false = Glint hands every fan it can back to its own control (BIOS curve,
    /// GPU driver, hub firmware) and only reads sensors; hubs that can't release fans without their lighting stay on Glint's curves.</summary>
    public bool FanControl { get; set; } = true;

    /// <summary>Named curves: list of [temperature °C, duty %] points.</summary>
    public Dictionary<string, List<double[]>> Curves { get; set; } = new();

    public string ActiveProfile { get; set; } = "Balanced";
    public Dictionary<string, List<FanRule>> Profiles { get; set; } = new();

    /// <summary>Friendly names for fan outputs, e.g. "xt:fan1" -> "Front top".</summary>
    public Dictionary<string, string> FanNames { get; set; } = new();

    public LightingConfig Lighting { get; set; } = new();
    /// <summary>Friendly names for sensors, e.g. "xt:probe1" -> "Intake air".</summary>
    public Dictionary<string, string> SensorNames { get; set; } = new();
    /// <summary>Plugin files (by file name) that are switched off.</summary>
    public List<string> DisabledPlugins { get; set; } = new();
    /// <summary>Optional LibreHardwareMonitor sensor groups (take effect on restart).</summary>
    public SensorOptions Sensors { get; set; } = new();
    /// <summary>MQTT bridge to Home Assistant (off by default).</summary>
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
    /// <summary>In-game overlay (FPS from ETW present events + sensors).</summary>
    public OverlayConfig Overlay { get; set; } = new();
    /// <summary>Optional update check (one GitHub API call at startup and every 24 h) and optional automatic install.</summary>
    public UpdateOptions Updates { get; set; } = new();
    /// <summary>OpenRGB SDK bridge (off by default): drive devices an OpenRGB server on this PC knows.</summary>
    public OpenRgbConfig OpenRgb { get; set; } = new();

    // ------------------------------------------------------------------ io

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Config folder. GLINT_CONFIG_DIR overrides it (tests use a temp folder so they never touch the real config).</summary>
    public static string Dir => Environment.GetEnvironmentVariable("GLINT_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glint");
    public static string FilePath => Path.Combine(Dir, "config.json");

    public static GlintConfig Load(string? path = null)
    {
        path ??= FilePath;
        if (!UserFs.Exists(path))
        {
            var d = Defaults();
            d.Save(path);
            return d;
        }
        var text = UserFs.Run(() =>
        {
            // a hand-edited (or hostile) file can't make the elevated process allocate gigabytes
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException($"config.json is larger than {MaxFileBytes / 1048576} MB");
            return File.ReadAllText(path);
        });
        var cfg = JsonSerializer.Deserialize<GlintConfig>(text, Json) ?? Defaults();
        cfg.FillNulls();
        cfg.HomeAssistant ??= new();
        bool plain = cfg.HomeAssistant.Password != null;
        cfg.HomeAssistant.Normalize();
        if (plain) try { cfg.Save(path); } catch { } // a hand-typed password is encrypted on first load
        // Older configs had unnamed rules: name them from the fans they drive.
        foreach (var r in cfg.Profiles.Values.SelectMany(x => x).Where(r => r.Name == "Fan rule"))
            r.Name = r.Fans.Count == 1 && r.Fans[0].EndsWith("pump") ? "AIO pump"
                : r.Fans.Count > 0 && r.Fans.All(f => f.StartsWith("st:fan")) ? "Radiator fans"
                : r.Fans.Count > 0 && r.Fans.All(f => f.StartsWith("xt:")) ? "Case fans"
                : r.Fixed != null ? "Fixed speed" : "Fan rule";
        return cfg;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        (HomeAssistant ??= new()).Normalize();
        UserFs.WriteAllTextAtomic(path, JsonSerializer.Serialize(this, Json));
    }

    public const long MaxFileBytes = 16 << 20;

    /// <summary>
    /// "lighting": null, "profiles": null, a null rule in a list... is valid JSON but would crash the engines.
    /// Replace every null that the model doesn't allow with its default (recursively); drop null list items.
    /// </summary>
    public void FillNulls() => Fill(this, 0);

    static readonly NullabilityInfoContext _nullability = new();
    static void Fill(object? o, int depth)
    {
        if (o == null || depth > 12) return;
        var t = o.GetType();
        if (t.IsPrimitive || o is string || t.IsEnum) return;
        if (o is System.Collections.IDictionary dict)
        {
            foreach (var k in dict.Keys.Cast<object>().ToList())
            {
                var v = dict[k];
                if (v == null) { var vt = t.GetGenericArguments().LastOrDefault(); var def = NewOf(vt); if (def != null) dict[k] = def; else dict.Remove(k); }
                else Fill(v, depth + 1);
            }
            return;
        }
        if (o is System.Collections.IList list)
        {
            if (!t.IsArray) for (int i = list.Count - 1; i >= 0; i--) if (list[i] == null) list.RemoveAt(i);
            foreach (var v in list) Fill(v, depth + 1);
            return;
        }
        if (t.Namespace?.StartsWith("Glint") != true) return;
        foreach (var pi in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
            var v = pi.GetValue(o);
            if (v == null)
            {
                if (pi.PropertyType.IsValueType || _nullability.Create(pi).WriteState != NullabilityState.NotNull) continue;
                var def = NewOf(pi.PropertyType);
                if (def != null) pi.SetValue(o, def);
            }
            else Fill(v, depth + 1);
        }
    }

    static object? NewOf(Type? t) =>
        t == null || t.IsValueType ? null : t == typeof(string) ? "" : t.IsArray ? null : t.GetConstructor(Type.EmptyTypes) is { } c ? c.Invoke(null) : null;

    public List<FanRule> Active => Profiles.TryGetValue(ActiveProfile, out var p) ? p : Profiles.Values.FirstOrDefault() ?? new();

    /// <summary>Starting config for this rig: AIO on the Commander ST, case fans on the Core XT.</summary>
    public static GlintConfig Defaults()
    {
        var c = new GlintConfig();
        c.Curves["cpu-balanced"] = new() { new[] { 40.0, 25 }, new[] { 60.0, 35 }, new[] { 75.0, 60 }, new[] { 85.0, 100 } };
        c.Curves["gpu-balanced"] = new() { new[] { 45.0, 25 }, new[] { 65.0, 40 }, new[] { 78.0, 70 }, new[] { 85.0, 100 } };
        c.Curves["liquid-balanced"] = new() { new[] { 30.0, 25 }, new[] { 35.0, 40 }, new[] { 40.0, 70 }, new[] { 45.0, 100 } };
        c.Curves["cpu-quiet"] = new() { new[] { 50.0, 20 }, new[] { 70.0, 30 }, new[] { 80.0, 55 }, new[] { 90.0, 100 } };
        c.Curves["gpu-quiet"] = new() { new[] { 55.0, 20 }, new[] { 72.0, 35 }, new[] { 82.0, 65 }, new[] { 88.0, 100 } };
        c.Curves["liquid-quiet"] = new() { new[] { 32.0, 20 }, new[] { 38.0, 35 }, new[] { 43.0, 70 }, new[] { 47.0, 100 } };
        c.Curves["pump"] = new() { new[] { 30.0, 70 }, new[] { 40.0, 85 }, new[] { 45.0, 100 } };

        List<FanRule> Build(string flavour)
        {
            var rules = new List<FanRule>
            {
                new() { Name = "AIO pump", Fans = new() { "st:pump" }, Inputs = new() { new() { Sensor = "liquid", Curve = "pump" } }, MinDuty = 60 },
                // Radiator fans: liquid temp is the real heat load; CPU spikes nudge them, GPU heat soaking the case too.
                new()
                {
                    Name = "Radiator fans",
                    Fans = new() { "st:fan1", "st:fan2", "st:fan3", "st:fan4" },
                    Inputs = new()
                    {
                        new() { Sensor = "liquid", Curve = $"liquid-{flavour}" },
                        new() { Sensor = "cpu", Curve = $"cpu-{flavour}", SmoothingSec = 4 },
                        new() { Sensor = "gpu", Curve = $"gpu-{flavour}" }, // top radiator exhausts GPU heat
                    },
                    Combine = Combine.Max,
                },
                // Case fans: whichever of CPU / GPU / liquid needs the most air wins.
                new()
                {
                    Name = "Case fans",
                    Fans = new() { "xt:fan1", "xt:fan2", "xt:fan3", "xt:fan4", "xt:fan5", "xt:fan6" },
                    Inputs = new()
                    {
                        new() { Sensor = "gpu", Curve = $"gpu-{flavour}" },
                        new() { Sensor = "cpu", Curve = $"cpu-{flavour}", SmoothingSec = 4 },
                        new() { Sensor = "liquid", Curve = $"liquid-{flavour}" },
                    },
                    Combine = Combine.Max,
                },
            };
            return rules;
        }
        c.Profiles["Balanced"] = Build("balanced");
        c.Profiles["Quiet"] = Build("quiet");
        c.Profiles["Full speed"] = new()
        {
            new() { Name = "Everything", Fans = new() { "st:pump", "st:fan1", "st:fan2", "st:fan3", "st:fan4", "xt:fan1", "xt:fan2", "xt:fan3", "xt:fan4", "xt:fan5", "xt:fan6" }, Fixed = 100 },
        };
        c.Lighting = new LightingConfig
        {
            Effect = new EffectConfig { Type = EffectType.Static, Colors = new() { "#ff5a1f" } },
            Presets = new()
            {
                ["Warm static"] = new() { Type = EffectType.Static, Colors = new() { "#ff5a1f" } },
                ["White"] = new() { Type = EffectType.Static, Colors = new() { "#ffffff" } },
                ["Rainbow wave"] = new() { Type = EffectType.Wave, Speed = 0.25 },
                ["Rainbow spin (per fan)"] = new() { Type = EffectType.Wave, PerZone = true, Speed = 0.5 },
                ["Ocean wave"] = new() { Type = EffectType.ColorWave, Colors = new() { "#001a66", "#0077ff", "#00e5ff", "#0044aa" }, Speed = 0.15 },
                ["Sunset gradient"] = new() { Type = EffectType.Gradient, Colors = new() { "#ff2a00", "#ff8a00", "#b000ff" } },
                ["Spinners"] = new() { Type = EffectType.Spin, Colors = new() { "#00ffd0" }, Speed = 0.8, Size = 0.5, Offset = 0.15 },
                ["Comet"] = new() { Type = EffectType.Comet, Colors = new() { "#ff0040", "#ff8000", "#ffff00" }, Speed = 0.35, Size = 0.35 },
                ["Meteor rain"] = new() { Type = EffectType.Meteor, Colors = new() { "#a0d8ff", "#ffffff" }, Speed = 1.2, Density = 0.4 },
                ["Starfield"] = new() { Type = EffectType.Twinkle, Colors = new() { "#ffffff", "#9fc8ff", "#ffe0a0" }, Density = 0.6, Background = "#020210" },
                ["Fire"] = new() { Type = EffectType.Fire, Speed = 0.6 },
                ["Plasma"] = new() { Type = EffectType.Plasma, Speed = 0.4 },
                ["Ripple"] = new() { Type = EffectType.Ripple, Colors = new() { "#00e5ff", "#7a5cff", "#ff3d9a" }, Speed = 0.35, Size = 0.35 },
                ["Diagonal rainbow"] = new() { Type = EffectType.Wave, Speed = 0.2, Angle = 45 },
                ["Rain"] = new() { Type = EffectType.Rain, Colors = new() { "#40a0ff" }, Density = 0.6 },
                ["Marquee"] = new() { Type = EffectType.Marquee, Colors = new() { "#ff0000", "#ffffff" }, Speed = 0.6, Size = 2 },
                ["Purple + sparkles"] = new() { Type = EffectType.Static, Colors = new() { "#3a0070" }, Layers = new() { new() { Type = EffectType.Twinkle, Colors = new() { "#ffffff" }, Density = 0.4 } } },
                ["Audio spectrum"] = new() { Type = EffectType.Audio, Colors = new() { "#ff0040", "#ffb000", "#00ff80", "#00a0ff" } },
                ["Audio beat pulse"] = new() { Type = EffectType.AudioPulse, Colors = new() { "#ff0060", "#00d0ff", "#ffe000" } },
                ["Spectrum cycle"] = new() { Type = EffectType.Spectrum, Speed = 0.05 },
                ["Breathing"] = new() { Type = EffectType.Breathing, Colors = new() { "#2060ff" }, Speed = 0.2 },
                ["CPU temperature"] = new() { Type = EffectType.Temperature, Sensor = "cpu", Colors = new() { "#0060ff", "#00ff60", "#ff2000" }, Range = new[] { 40.0, 85 } },
                ["Max(CPU,GPU) temperature"] = new() { Type = EffectType.Temperature, Sensor = "max(cpu,gpu)", Colors = new() { "#0060ff", "#00ff60", "#ff2000" }, Range = new[] { 40.0, 85 } },
                ["Off"] = new() { Type = EffectType.Off },
            },
            LedCounts = new() { ["aura:argb1"] = 60, ["aura:argb2"] = 60, ["aura:argb3"] = 60, ["lnp:ch1"] = 120, ["lnp:ch2"] = 120 },
        };
        return c;
    }
}

/// <summary>In-game overlay. Ranges and enums live in <see cref="Limits"/> (validator + schema).</summary>
public sealed class OverlayConfig
{
    /// <summary>Overlay on screen (the tray toggle / hotkey flip this).</summary>
    public bool Enabled { get; set; }
    /// <summary>Global hotkey that toggles the overlay, e.g. "Ctrl+Shift+O". "" = none.</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+O";
    /// <summary>top-left, top-right, bottom-left or bottom-right of the foreground window's monitor.</summary>
    public string Position { get; set; } = "top-left";
    /// <summary>Distance from the corner, in pixels at 100% display scaling.</summary>
    public int OffsetX { get; set; } = 16;
    public int OffsetY { get; set; } = 16;
    /// <summary>Size multiplier on top of the monitor's DPI scaling.</summary>
    public double Scale { get; set; } = 1;
    /// <summary>Whole-overlay opacity, 0.1..1.</summary>
    public double Opacity { get; set; } = 1;
    /// <summary>Panel background opacity, 0 (text only) .. 1.</summary>
    public double BackgroundOpacity { get; set; } = 0.72;
    /// <summary>"#rrggbb" accent, or null for Glint's gradient.</summary>
    public string? Accent { get; set; }
    /// <summary>"compact" (one line) or "detailed" (rows with labels).</summary>
    public string Layout { get; set; } = "detailed";
    /// <summary>Only show while a fullscreen / borderless-fullscreen app (not the desktop or Glint) is in front.</summary>
    public bool OnlyWhenGame { get; set; }
    /// <summary>What to show, in order. Sensors that aren't reading are left out.</summary>
    public List<OverlayItem> Items { get; set; } = DefaultItems();

    public static List<OverlayItem> DefaultItems() => new()
    {
        new() { Source = "fps" }, new() { Source = "low1" }, new() { Source = "cpu" },
        new() { Source = "gpu" }, new() { Source = "gpu.hotspot" }, new() { Source = "liquid" },
    };
}

public sealed class OverlayItem
{
    /// <summary>"fps", "frametime", "low1", "time", "glint.cpu", "fan:&lt;id&gt;" (rpm), "duty:&lt;id&gt;" (%), or a sensor id.</summary>
    public string Source { get; set; } = "fps";
    public string? Label { get; set; }
    /// <summary>"#rrggbb" for the value, or null for the default.</summary>
    public string? Color { get; set; }
}

public sealed class UpdateOptions
{
    /// <summary>Ask GitHub for the latest release at startup and every 24 h (the check itself never downloads anything).</summary>
    public bool Check { get; set; } = true;
    /// <summary>Install a newer, verified release by itself while the PC is idle (no game in front, not on battery,
    /// at most one attempt per version per day). Off by default; needs <see cref="Check"/>.</summary>
    public bool AutoInstall { get; set; }
}

public sealed class OpenRgbConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;
    /// <summary>OpenRGB devices to leave alone: device name (or part of it) or Glint id ("orgb-...").</summary>
    public List<string> Exclude { get; set; } = new();
}

public sealed class SensorOptions
{
    public bool Storage { get; set; } = true;
    public bool Memory { get; set; } = true;
    public bool Psu { get; set; } = true;
    public bool Controllers { get; set; } = true;
}

public sealed class HomeAssistantConfig
{
    public bool Enabled { get; set; }
    /// <summary>MQTT broker host name or IP (e.g. the Home Assistant machine running the Mosquitto add-on).</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public bool Tls { get; set; }
    public string? Username { get; set; }
    /// <summary>Input only: a plain password given here (dashboard / API / hand edit) is encrypted into
    /// <see cref="PasswordProtected"/> and cleared; it is never written to disk. "" clears the stored password.</summary>
    public string? Password { get; set; }
    /// <summary>The password, DPAPI-encrypted for the current Windows user (base64).</summary>
    public string? PasswordProtected { get; set; }
    public string DiscoveryPrefix { get; set; } = "homeassistant";
    public string BaseTopic { get; set; } = "glint";

    /// <summary>Move a plain <see cref="Password"/> into <see cref="PasswordProtected"/>.</summary>
    public void Normalize()
    {
        if (Password == null) return;
        PasswordProtected = Password.Length == 0 ? null : Secret.Protect(Password);
        Password = null;
    }

    /// <summary>The usable password (decrypted), or null.</summary>
    public string? EffectivePassword() => Password is { Length: > 0 } p ? p : PasswordProtected is { Length: > 0 } b ? Secret.Unprotect(b) : null;
}

public enum Combine { Max, Average, Weighted }

public sealed class FanRule
{
    public string Name { get; set; } = "Fan rule";
    /// <summary>Output ids, e.g. "st:pump", "xt:fan3", "mb:Fan #2".</summary>
    public List<string> Fans { get; set; } = new();
    public List<FanInput> Inputs { get; set; } = new();
    public Combine Combine { get; set; } = Combine.Max;
    /// <summary>If set, ignore inputs and hold this duty.</summary>
    public int? Fixed { get; set; }
    /// <summary>Manual duty set from Home Assistant / the API ("auto" clears it). Clamped to Min/MaxDuty.
    /// The fail-safe still wins: a lost sensor or a curve at the fail-safe duty overrides it.</summary>
    public int? Override { get; set; }
    public int MinDuty { get; set; } = 20;
    public int MaxDuty { get; set; } = 100;
    /// <summary>Max duty change per second (software ramp on top of the hub's own).</summary>
    public double RampUpPerSec { get; set; } = 25;
    public double RampDownPerSec { get; set; } = 4;
}

public sealed class FanInput
{
    /// <summary>Sensor id ("cpu", "gpu", "liquid", "xt:probe1", "max(cpu,gpu)", any LHM identifier).</summary>
    public string Sensor { get; set; } = "cpu";
    /// <summary>Named curve, or inline Points.</summary>
    public string? Curve { get; set; }
    public List<double[]>? Points { get; set; }
    public double Weight { get; set; } = 1;
    /// <summary>Temperature must fall this far below the peak before the curve backs off.</summary>
    public double Hysteresis { get; set; } = 2;
    /// <summary>Exponential smoothing time constant applied to the sensor, seconds.</summary>
    public double SmoothingSec { get; set; } = 0;
}

public enum EffectType { Off, Static, Gradient, Breathing, Spectrum, Wave, ColorWave, Spin, Marquee, Comet, Meteor, Twinkle, Fire, Plasma, Rain, Strobe, Temperature, Audio, AudioPulse, Ripple, Plugin }

public sealed class EffectConfig
{
    public EffectType Type { get; set; } = EffectType.Static;
    public List<string> Colors { get; set; } = new() { "#ffffff" };
    /// <summary>Cycles per second.</summary>
    public double Speed { get; set; } = 0.2;
    public string? Sensor { get; set; }
    public double[]? Range { get; set; }
    /// <summary>Run around each fan ring / strip instead of across the whole rig.</summary>
    public bool PerZone { get; set; }
    public bool Reverse { get; set; }
    /// <summary>How many repeats of the pattern fit across the rig.</summary>
    public double Scale { get; set; } = 1;
    /// <summary>Effect-specific size: tail/arc length (0-1) or marquee block size (LEDs).</summary>
    public double Size { get; set; } = 0.3;
    /// <summary>Direction across the layout canvas in degrees (0 = left→right, 90 = top→bottom).</summary>
    public double Angle { get; set; }
    /// <summary>Origin on the canvas for radial effects (Ripple), [x, y] in 0..1.</summary>
    public double[]? Center { get; set; }
    /// <summary>Per-zone phase offset (staggered spinners).</summary>
    public double Offset { get; set; } = 0;
    /// <summary>Spawn rate for twinkle/meteor/rain.</summary>
    public double Density { get; set; } = 0.5;
    public string? Background { get; set; }
    /// <summary>For Type = Plugin: the plugin effect's name.</summary>
    public string? Plugin { get; set; }
    /// <summary>For plugin effects: saved values of the plugin's declared params.</summary>
    public Dictionary<string, JsonElement>? Params { get; set; }
    /// <summary>Additional effects added on top (additive blend).</summary>
    public List<EffectConfig>? Layers { get; set; }
}

public sealed class LightingConfig
{
    public bool Enabled { get; set; } = true;
    public int Brightness { get; set; } = 100;
    /// <summary>Frame cap for animated effects. Static effects only send a keep-alive.</summary>
    public int Fps { get; set; } = 30;
    public EffectConfig Effect { get; set; } = new();
    /// <summary>Per zone / per device overrides keyed by zone id ("xt:fan1") or device id ("aura").</summary>
    public Dictionary<string, EffectConfig> Overrides { get; set; } = new();
    public Dictionary<string, EffectConfig> Presets { get; set; } = new();
    /// <summary>LED counts for ports that can't report them ("aura:argb1", "lnp:ch1"). 0 disables.</summary>
    public Dictionary<string, int> LedCounts { get; set; } = new();
    /// <summary>Where each zone sits on the layout canvas (zone id → placement). Missing zones are auto-arranged.</summary>
    public Dictionary<string, ZonePlacement> Layout { get; set; } = new();
}
