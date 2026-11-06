using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Timer = System.Threading.Timer;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glint.Core;

/// <summary>DPAPI (current Windows user) protection for secrets stored in config.json.</summary>
public static class Secret
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Glint.HomeAssistant.v1");
    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
    /// <summary>Null when the blob can't be decrypted (other user / other PC / corrupted).</summary>
    public static string? Unprotect(string b64)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }
}

/// <summary>
/// Home Assistant integration over MQTT with HA's MQTT discovery: one device per PC with temperature, fan RPM /
/// duty and Glint CPU sensors, an RGB light (JSON schema; effects = lighting presets), a fan-profile select and
/// an "Identify" button per lighting device. Commands go through <see cref="GlintService.Apply"/> exactly like
/// the dashboard and control API, so config.json and the dashboard stay in sync.
/// All periodic work runs on one timer tick every 5 s while connected; nothing runs while disabled.
/// </summary>
public sealed class HomeAssistantBridge : IDisposable
{
    const int TickMs = 5000, FullRefreshMs = 60000;
    readonly GlintService _svc;
    readonly object _cfgLock = new();
    readonly Timer _timer;
    MqttClient? _mqtt;
    string _key = "";
    HomeAssistantConfig _cfg = new();
    int _busy;
    volatile bool _rediscover, _stateDirty;
    bool _disposed;

    // topics (fixed for one connection)
    string _node = "", _root = "", _prefix = "", _avail = "", _lightCmd = "", _lightState = "", _profileCmd = "", _profileState = "", _identifyCmd = "", _haStatus = "", _cmd = "";
    JsonObject _device = new();

    sealed class Ent { public required string StateTopic; public double Last = double.NaN; }
    readonly Dictionary<string, Ent> _temps = new(), _rpms = new(), _duties = new();
    Ent? _cpuEnt;
    readonly Dictionary<string, string> _discovered = new();   // config topic → payload last published
    string? _lastLight, _lastProfile;
    /// <summary>Per-device lights and per-rule fans (slug → target), rebuilt with discovery.</summary>
    sealed class Target { public required string Id; public required string StateTopic; public string? Last; }
    readonly Dictionary<string, Target> _devLights = new(), _fans = new();
    const string FollowGlobal = "Follow global";
    long _lastFull, _lastSensors;
    long _cpuT0, _cpuWall0;

    public HomeAssistantBridge(GlintService svc)
    {
        _svc = svc;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static string Slug(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        return sb.ToString().Trim('_') is { Length: > 0 } r ? r : "x";
    }

    // ------------------------------------------------------------------ lifecycle

    static string KeyOf(HomeAssistantConfig c) =>
        c.Enabled ? string.Join('\u0001', c.Host, c.Port, c.Tls, c.Username, c.EffectivePassword(), c.DiscoveryPrefix, c.BaseTopic) : "";

    /// <summary>Called on every config apply: reconnects only when the Home Assistant section changed.</summary>
    public void Configure(HomeAssistantConfig c)
    {
        lock (_cfgLock)
        {
            if (_disposed) return;
            var key = KeyOf(c);
            if (key == _key) { _cfg = c; return; }
            _key = key; _cfg = c;
            Stop();
            if (!c.Enabled || string.IsNullOrWhiteSpace(c.Host)) return;
            _node = Slug(Environment.MachineName);
            _prefix = string.IsNullOrWhiteSpace(c.DiscoveryPrefix) ? "homeassistant" : c.DiscoveryPrefix.Trim('/');
            _root = $"{(string.IsNullOrWhiteSpace(c.BaseTopic) ? "glint" : c.BaseTopic.Trim('/'))}/{_node}";
            _avail = _root + "/availability";
            _cmd = _root + "/cmd";   // every command topic lives under here (one subscription, no echo of our own states)
            _lightCmd = _cmd + "/light"; _lightState = _root + "/light/state";
            _profileCmd = _cmd + "/profile"; _profileState = _root + "/profile/state";
            _identifyCmd = _cmd + "/identify";
            _haStatus = _prefix + "/status";
            _device = new JsonObject
            {
                ["identifiers"] = new JsonArray("glint_" + _node),
                ["name"] = $"Glint ({Environment.MachineName})",
                ["manufacturer"] = "Glint",
                ["model"] = "RGB + fan control",
                ["sw_version"] = typeof(HomeAssistantBridge).Assembly.GetName().Version?.ToString(3) ?? "",
            };
            var m = new MqttClient(new MqttOptions(c.Host.Trim(), c.Port, c.Tls, "glint-" + _node, c.Username, c.EffectivePassword(), _avail));
            m.OnConnected += () => { _rediscover = true; Poke(); };
            m.OnMessage += OnMessage;
            m.Subscribe(_cmd + "/#");
            m.Subscribe(_haStatus);
            _mqtt = m;
            _timer.Change(TickMs, TickMs);
        }
    }

    void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        var m = _mqtt; _mqtt = null;
        m?.Close(_avail, "offline", retain: true);
        lock (_temps) { _temps.Clear(); _rpms.Clear(); _duties.Clear(); _cpuEnt = null; _discovered.Clear(); _lastLight = _lastProfile = null; _devLights.Clear(); _fans.Clear(); }
    }

    /// <summary>Config was applied (by anyone): publish light / select state soon if it changed.</summary>
    public void ConfigApplied() { _stateDirty = true; Poke(); }

    void Poke() { if (_mqtt != null) try { _timer.Change(0, TickMs); } catch (ObjectDisposedException) { } }

    public object Status()
    {
        var c = _cfg; var m = _mqtt;
        int n; lock (_temps) n = _discovered.Count;
        return new
        {
            enabled = c.Enabled, connected = m?.Connected ?? false,
            broker = string.IsNullOrWhiteSpace(c.Host) ? "" : $"{(c.Tls ? "mqtts" : "mqtt")}://{c.Host}:{c.Port}",
            lastError = m?.LastError ?? (c.Enabled && string.IsNullOrWhiteSpace(c.Host) ? "no broker host set" : ""),
            entities = n, reconnects = m?.Reconnects ?? 0,
            discoveryPrefix = _prefix, baseTopic = _root,
        };
    }

    public void Dispose()
    {
        lock (_cfgLock) { if (_disposed) return; _disposed = true; Stop(); }
        _timer.Dispose();
    }

    // ------------------------------------------------------------------ periodic publish

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        double cpu0 = CpuMeter.ThreadMs();
        try
        {
            var m = _mqtt;
            if (m == null || !m.Connected) return;
            lock (_temps) TickLocked(m);
        }
        catch { }
        finally
        {
            CpuMeter.Add("home assistant", CpuMeter.ThreadMs() - cpu0);
            _busy = 0;
        }
    }

    void TickLocked(MqttClient m)
    {
        long now = Environment.TickCount64;
        bool rediscover = _rediscover;
        _rediscover = false;
        if (rediscover)
        {
            _discovered.Clear();
            m.Publish(_avail, "online", retain: true);
        }
        bool full = rediscover || now - _lastFull >= FullRefreshMs;
        bool sensorsDue = full || now - _lastSensors >= TickMs - 500;
        bool stateDirty = _stateDirty;
        _stateDirty = false;
        var cfg = _svc.Config;
        var sensors = _svc.Sensors; var rpm = _svc.Rpm; var duty = _svc.Duty;

        // Discovery: rebuilt on (re)connect, every full refresh, after config changes (names, presets, profiles)
        // and when a new sensor / fan shows up. Only payloads that differ from what was sent are published.
        bool newKeys = false;
        foreach (var k in sensors.Keys) if (!k.StartsWith('/') && !_temps.ContainsKey(k)) { newKeys = true; break; }
        if (!newKeys) foreach (var k in rpm.Keys) if (!_rpms.ContainsKey(k)) { newKeys = true; break; }
        if (!newKeys) foreach (var k in duty.Keys) if (!_duties.ContainsKey(k) && IsFan(k)) { newKeys = true; break; }
        if (full || stateDirty || newKeys) Discover(m, cfg, sensors, rpm, duty);

        if (full)
        {
            _lastFull = now; _lastLight = _lastProfile = null;
            foreach (var t in _devLights.Values) t.Last = null;
            foreach (var t in _fans.Values) t.Last = null;
        }
        if (sensorsDue)
        {
            _lastSensors = now;
            foreach (var (k, v) in sensors)
                if (_temps.TryGetValue(k, out var e)) Send(m, e, Math.Round(v, 1), 0.5, full, "0.0");
            foreach (var (k, v) in rpm)
                if (_rpms.TryGetValue(k, out var e)) Send(m, e, v, 50, full, "0");
            foreach (var (k, v) in duty)
                if (_duties.TryGetValue(k, out var e)) Send(m, e, v, 1, full, "0");
            if (_cpuEnt != null) Send(m, _cpuEnt, Math.Round(GlintCpuPercent(), 1), 1, full, "0.0");
        }

        var light = LightState(cfg);
        if (light != _lastLight && m.Publish(_lightState, light, retain: true)) _lastLight = light;
        var prof = cfg.ActiveProfile;
        if (prof != _lastProfile && m.Publish(_profileState, prof, retain: true)) _lastProfile = prof;
        if (stateDirty || full)
            foreach (var t in _devLights.Values)
            {
                var st = DeviceLightState(cfg, t.Id);
                if (st != t.Last && m.Publish(t.StateTopic, st, retain: true)) t.Last = st;
            }
        if (sensorsDue || stateDirty)
            foreach (var t in _fans.Values)
            {
                var st = FanState(cfg, t.Id, duty);
                if (st != null && st != t.Last && m.Publish(t.StateTopic, st, retain: true)) t.Last = st;
            }
    }

    bool IsFan(string id)
    {
        foreach (var f in _svc.Rig.Fans) if (f.Id == id) return true;
        return false;
    }

    static void Send(MqttClient m, Ent e, double v, double minDelta, bool force, string fmt)
    {
        if (!force && !double.IsNaN(e.Last) && Math.Abs(v - e.Last) < minDelta) return;
        if (m.Publish(e.StateTopic, v.ToString(fmt, CultureInfo.InvariantCulture), retain: false)) e.Last = v;
    }

    [DllImport("kernel32")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);

    /// <summary>Whole-process CPU since the previous call, % of one core.</summary>
    double GlintCpuPercent()
    {
        GetProcessTimes(GetCurrentProcess(), out _, out _, out var k, out var u);
        long cpu = k + u, wall = DateTime.UtcNow.Ticks;
        double pct = _cpuWall0 == 0 || wall <= _cpuWall0 ? 0 : (cpu - _cpuT0) * 100.0 / (wall - _cpuWall0);
        _cpuT0 = cpu; _cpuWall0 = wall;
        return pct;
    }

    // ------------------------------------------------------------------ discovery

    static readonly Dictionary<string, string> BuiltinSensorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = "CPU", ["gpu"] = "GPU", ["gpu.hotspot"] = "GPU hotspot", ["liquid"] = "Liquid",
        ["storage"] = "Storage (hottest)", ["ram"] = "RAM",
    };

    string SensorName(GlintConfig cfg, string id) =>
        cfg.SensorNames.TryGetValue(id, out var n) && n.Length > 0 ? n : BuiltinSensorNames.TryGetValue(id, out var b) ? b : id;

    string FanName(GlintConfig cfg, string id)
    {
        if (cfg.FanNames.TryGetValue(id, out var n) && n.Length > 0) return n;
        var f = _svc.Rig.Fans.FirstOrDefault(x => x.Id == id);
        return f != null ? $"{f.Device} {f.Name}".Trim() : id;
    }

    static readonly JsonSerializerOptions JsonOut = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    JsonObject Base(string name, string uid) => new()
    {
        ["name"] = name,
        ["unique_id"] = "glint_" + _node + "_" + uid,
        ["device"] = _device.DeepClone(),
        ["availability"] = new JsonArray(new JsonObject { ["topic"] = _avail }),
    };

    void Config(MqttClient m, string component, string uid, JsonObject payload)
    {
        var topic = $"{_prefix}/{component}/{_node}/{uid}/config";
        var s = payload.ToJsonString(JsonOut);
        if (_discovered.TryGetValue(topic, out var prev) && prev == s) return;
        if (m.Publish(topic, s, retain: true)) _discovered[topic] = s;
    }

    void Discover(MqttClient m, GlintConfig cfg, IReadOnlyDictionary<string, double> sensors, IReadOnlyDictionary<string, int> rpm, IReadOnlyDictionary<string, int> duty)
    {
        JsonObject Sensor(string name, string uid, string state, string unit, string? cls, string? icon, int precision)
        {
            var o = Base(name, uid);
            o["state_topic"] = state;
            o["unit_of_measurement"] = unit;
            if (cls != null) o["device_class"] = cls;
            if (icon != null) o["icon"] = icon;
            o["state_class"] = "measurement";
            o["suggested_display_precision"] = precision;
            return o;
        }
        foreach (var id in sensors.Keys)
        {
            if (id.StartsWith('/')) continue;   // raw LibreHardwareMonitor ids: the friendly aliases cover them
            var uid = "temp_" + Slug(id);
            if (!_temps.ContainsKey(id)) _temps[id] = new Ent { StateTopic = $"{_root}/sensor/{uid}/state" };
            Config(m, "sensor", uid, Sensor(SensorName(cfg, id) + " temperature", uid, _temps[id].StateTopic, "°C", "temperature", null, 1));
        }
        foreach (var id in rpm.Keys)
        {
            var uid = "rpm_" + Slug(id);
            if (!_rpms.ContainsKey(id)) _rpms[id] = new Ent { StateTopic = $"{_root}/sensor/{uid}/state" };
            Config(m, "sensor", uid, Sensor(FanName(cfg, id) + " speed", uid, _rpms[id].StateTopic, "RPM", null, "mdi:fan", 0));
        }
        foreach (var id in duty.Keys)
        {
            if (!IsFan(id)) continue;   // profile rules may name fans this PC doesn't have
            var uid = "duty_" + Slug(id);
            if (!_duties.ContainsKey(id)) _duties[id] = new Ent { StateTopic = $"{_root}/sensor/{uid}/state" };
            Config(m, "sensor", uid, Sensor(FanName(cfg, id) + " duty", uid, _duties[id].StateTopic, "%", null, "mdi:fan-chevron-up", 0));
        }
        _cpuEnt ??= new Ent { StateTopic = $"{_root}/sensor/glint_cpu/state" };
        Config(m, "sensor", "glint_cpu", Sensor("Glint CPU usage", "glint_cpu", _cpuEnt.StateTopic, "%", null, "mdi:chip", 1));

        var light = Base("RGB", "rgb");
        light["schema"] = "json";
        light["state_topic"] = _lightState;
        light["command_topic"] = _lightCmd;
        light["brightness"] = true;
        light["brightness_scale"] = 255;
        light["supported_color_modes"] = new JsonArray("rgb");
        light["effect"] = true;
        light["effect_list"] = new JsonArray(cfg.Lighting.Presets.Keys.Select(k => (JsonNode)k).ToArray());
        light["icon"] = "mdi:led-strip-variant";
        Config(m, "light", "rgb", light);

        var sel = Base("Fan profile", "fan_profile");
        sel["state_topic"] = _profileState;
        sel["command_topic"] = _profileCmd;
        sel["options"] = new JsonArray(cfg.Profiles.Keys.Select(k => (JsonNode)k).ToArray());
        sel["icon"] = "mdi:fan";
        Config(m, "select", "fan_profile", sel);

        var presets = cfg.Lighting.Presets.Keys.ToList();
        foreach (var l in _svc.Rig.Lights)
        {
            var slug = Slug(l.Id);
            if (!_devLights.ContainsKey(slug)) _devLights[slug] = new Target { Id = l.Id, StateTopic = $"{_root}/light/{slug}/state" };
            var dl = Base(l.Name, "light_" + slug);
            dl["schema"] = "json";
            dl["state_topic"] = _devLights[slug].StateTopic;
            dl["command_topic"] = $"{_lightCmd}/{slug}";
            dl["brightness"] = true;
            dl["brightness_scale"] = 255;
            dl["supported_color_modes"] = new JsonArray("rgb");
            dl["effect"] = true;
            dl["effect_list"] = new JsonArray(presets.Prepend(FollowGlobal).Select(k => (JsonNode)k).ToArray());
            Config(m, "light", "light_" + slug, dl);
        }
        foreach (var r in cfg.Active)
        {
            var slug = Slug(r.Name);
            if (_fans.TryGetValue(slug, out var existing) && existing.Id != r.Name) continue; // two rules with the same slug: first wins
            if (existing == null) _fans[slug] = new Target { Id = r.Name, StateTopic = $"{_root}/fan/{slug}/state" };
            var f = Base(r.Name, "fan_" + slug);
            var st = _fans[slug].StateTopic;
            f["command_topic"] = $"{_cmd}/fan/{slug}/power";
            f["state_topic"] = st;
            f["state_value_template"] = "{{ value_json.state }}";
            f["percentage_command_topic"] = $"{_cmd}/fan/{slug}/percentage";
            f["percentage_state_topic"] = st;
            f["percentage_value_template"] = "{{ value_json.percentage }}";
            f["preset_mode_command_topic"] = $"{_cmd}/fan/{slug}/preset";
            f["preset_mode_state_topic"] = st;
            f["preset_mode_value_template"] = "{{ value_json.preset }}";
            f["preset_modes"] = new JsonArray("auto");
            f["icon"] = r.Fans.Any(x => x.EndsWith("pump")) ? "mdi:pump" : "mdi:fan";
            Config(m, "fan", "fan_" + slug, f);
        }
        foreach (var l in _svc.Rig.Lights)
        {
            var uid = "identify_" + Slug(l.Id);
            var b = Base("Identify " + l.Name, uid);
            b["command_topic"] = _identifyCmd;
            b["payload_press"] = l.Id;
            b["device_class"] = "identify";
            Config(m, "button", uid, b);
        }
    }

    // ------------------------------------------------------------------ light state / commands

    public static int ToHa(int pct) => (int)Math.Round(Math.Clamp(pct, 0, 100) * 255 / 100.0);
    public static int FromHa(int v) => (int)Math.Round(Math.Clamp(v, 0, 255) * 100 / 255.0);

    static string LightState(GlintConfig cfg)
    {
        var L = cfg.Lighting;
        var o = new JsonObject
        {
            ["state"] = L.Enabled && L.Effect.Type != EffectType.Off ? "ON" : "OFF",
            ["brightness"] = ToHa(L.Brightness),
            ["color_mode"] = "rgb",
        };
        var hex = L.Effect.Colors.FirstOrDefault();
        if (hex != null && TryHex(hex, out var r, out var g, out var b)) o["color"] = new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b };
        var fx = JsonSerializer.Serialize(L.Effect, GlintConfig.Json);
        foreach (var (name, p) in L.Presets)
            if (JsonSerializer.Serialize(p, GlintConfig.Json) == fx) { o["effect"] = name; break; }
        return o.ToJsonString(JsonOut);
    }

    static string Hex(int r, int g, int b) => $"#{r:x2}{g:x2}{b:x2}";

    /// <summary>Static colour → (full-brightness colour, HA brightness = its largest channel).</summary>
    static (int r, int g, int b, int bri) Split(string hex)
    {
        if (!TryHex(hex, out var r, out var g, out var b)) return (255, 255, 255, 255);
        int mx = Math.Max(r, Math.Max(g, b));
        if (mx == 0) return (255, 255, 255, 0);
        return ((int)Math.Round(r * 255.0 / mx), (int)Math.Round(g * 255.0 / mx), (int)Math.Round(b * 255.0 / mx), mx);
    }

    static string Scale(int r, int g, int b, int bri) => Hex(r * bri / 255, g * bri / 255, b * bri / 255);

    static bool IsBlack(EffectConfig fx) => fx.Type == EffectType.Off
        || fx.Type == EffectType.Static && (fx.Layers == null || fx.Layers.Count == 0) && fx.Colors.All(c => TryHex(c, out var r, out var g, out var b) && r + g + b == 0);

    static string? PresetName(LightingConfig L, EffectConfig fx)
    {
        var j = JsonSerializer.Serialize(fx, GlintConfig.Json);
        foreach (var (name, p) in L.Presets) if (JsonSerializer.Serialize(p, GlintConfig.Json) == j) return name;
        return null;
    }

    /// <summary>A lighting device's state: its override if it has one, else the global effect ("Follow global").
    /// Per-device brightness is carried in a static colour (HA brightness = brightest channel).</summary>
    static string DeviceLightState(GlintConfig cfg, string id)
    {
        var L = cfg.Lighting;
        var o = new JsonObject { ["color_mode"] = "rgb" };
        if (!L.Overrides.TryGetValue(id, out var fx))
        {
            o["state"] = L.Enabled && L.Effect.Type != EffectType.Off ? "ON" : "OFF";
            o["brightness"] = ToHa(L.Brightness);
            if (L.Effect.Colors.FirstOrDefault() is { } h && TryHex(h, out var r, out var g, out var b)) o["color"] = new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b };
            o["effect"] = FollowGlobal;
            return o.ToJsonString(JsonOut);
        }
        o["state"] = L.Enabled && !IsBlack(fx) ? "ON" : "OFF";
        if (fx.Type == EffectType.Static && fx.Colors.Count > 0)
        {
            var (r, g, b, bri) = Split(fx.Colors[0]);
            o["brightness"] = bri; o["color"] = new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b };
        }
        else
        {
            o["brightness"] = ToHa(L.Brightness);
            if (fx.Colors.FirstOrDefault() is { } h && TryHex(h, out var r, out var g, out var b)) o["color"] = new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b };
        }
        if (PresetName(L, fx) is { } pn) o["effect"] = pn;
        return o.ToJsonString(JsonOut);
    }

    /// <summary>Fan rule state: current duty of its fans, and "auto" unless a manual override is set.</summary>
    string? FanState(GlintConfig cfg, string ruleName, IReadOnlyDictionary<string, int> duty)
    {
        var rule = cfg.Active.FirstOrDefault(r => r.Name == ruleName);
        if (rule == null) return null;
        int n = 0, sum = 0;
        foreach (var f in rule.Fans) if (duty.TryGetValue(f, out var d) && IsFan(f)) { n++; sum += d; }
        if (n == 0) foreach (var f in rule.Fans) if (duty.TryGetValue(f, out var d)) { n++; sum += d; }
        int pct = n == 0 ? rule.Override ?? 0 : (int)Math.Round(sum / (double)n);
        return $"{{\"state\":\"ON\",\"percentage\":{pct},\"preset\":\"{(rule.Override == null ? "auto" : "None")}\"}}";
    }

    static bool TryHex(string s, out int r, out int g, out int b)
    {
        r = g = b = 0;
        s = s.TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        r = v >> 16 & 255; g = v >> 8 & 255; b = v & 255;
        return true;
    }

    static GlintConfig Clone(GlintConfig c) => JsonSerializer.Deserialize<GlintConfig>(JsonSerializer.Serialize(c, GlintConfig.Json), GlintConfig.Json)!;

    void OnMessage(string topic, string payload)
    {
        try
        {
            if (topic == _haStatus) { if (payload.Trim() == "online") { _rediscover = true; Poke(); } return; }
            if (topic == _lightCmd) LightCommand(payload);
            else if (topic == _profileCmd)
            {
                var name = payload.Trim();
                if (_svc.Config.Profiles.ContainsKey(name) && _svc.Config.ActiveProfile != name)
                {
                    var c = Clone(_svc.Config); c.ActiveProfile = name; _svc.Apply(c);
                }
                else ConfigApplied(); // unknown / unchanged: re-send the real state so HA doesn't show a wrong option
            }
            else if (topic.StartsWith(_lightCmd + "/"))
            {
                Target? t; lock (_temps) _devLights.TryGetValue(topic[(_lightCmd.Length + 1)..], out t);
                if (t != null) DeviceLightCommand(t.Id, payload);
            }
            else if (topic.StartsWith(_cmd + "/fan/"))
            {
                var parts = topic[(_cmd.Length + 5)..].Split('/');
                Target? t = null;
                if (parts.Length == 2) lock (_temps) _fans.TryGetValue(parts[0], out t);
                if (t != null) FanCommand(t.Id, parts[1], payload.Trim());
            }
            else if (topic == _identifyCmd)
            {
                var id = payload.Trim();
                if (_svc.Rig.Lights.Any(l => l.Id == id)) _svc.Lighting.Identify(id);
            }
        }
        catch { ConfigApplied(); }
    }

    void FanCommand(string ruleName, string what, string payload)
    {
        var c = Clone(_svc.Config);
        var rule = c.Active.FirstOrDefault(r => r.Name == ruleName);
        if (rule == null) { ConfigApplied(); return; }
        switch (what)
        {
            case "percentage":
                if (!double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { ConfigApplied(); return; }
                rule.Override = Math.Clamp((int)Math.Round(v), rule.MinDuty, rule.MaxDuty); // pumps: never below the rule's minimum
                break;
            case "preset":
                if (payload != "auto") { ConfigApplied(); return; }
                rule.Override = null;
                break;
            case "power": // fans can't stop: OFF = hold the rule's minimum, ON = back to the curve
                rule.Override = payload == "OFF" ? rule.MinDuty : null;
                break;
            default: return;
        }
        _svc.Apply(c);
    }

    void DeviceLightCommand(string id, string payload)
    {
        if (JsonNode.Parse(payload) is not JsonObject j) return;
        var c = Clone(_svc.Config);
        var L = c.Lighting;
        L.Overrides.TryGetValue(id, out var cur);
        var state = (string?)j["state"];
        int? bri = j["brightness"] is JsonValue bv && bv.TryGetValue<double>(out var bd) ? Math.Clamp((int)Math.Round(bd), 0, 255) : null;
        var fxName = (string?)j["effect"];
        if (state == "OFF") L.Overrides[id] = new EffectConfig { Type = EffectType.Static, Colors = new() { "#000000" } };
        else
        {
            if (state == "ON") L.Enabled = true;
            if (fxName == FollowGlobal) L.Overrides.Remove(id);
            else if (fxName != null && L.Presets.TryGetValue(fxName, out var preset))
                L.Overrides[id] = JsonSerializer.Deserialize<EffectConfig>(JsonSerializer.Serialize(preset, GlintConfig.Json), GlintConfig.Json)!;
            if (j["color"] is JsonObject col)
            {
                int r = Math.Clamp((int?)col["r"] ?? 0, 0, 255), g = Math.Clamp((int?)col["g"] ?? 0, 0, 255), b = Math.Clamp((int?)col["b"] ?? 0, 0, 255);
                int k = bri ?? (cur is { Type: EffectType.Static } && cur.Colors.Count > 0 && !IsBlack(cur) ? Split(cur.Colors[0]).bri : 255);
                L.Overrides[id] = new EffectConfig { Type = EffectType.Static, Colors = new() { Scale(r, g, b, k) } };
            }
            else if (bri != null && fxName == null)
            {
                // brightness alone: rescale a solid colour (the device's own, or the global one it follows)
                var src = cur ?? L.Effect;
                if (src.Type == EffectType.Static && src.Colors.Count > 0 && !(cur != null && IsBlack(cur)))
                {
                    var (r, g, b, _) = Split(src.Colors[0]);
                    L.Overrides[id] = new EffectConfig { Type = EffectType.Static, Colors = new() { Scale(r, g, b, bri.Value) } };
                }
                else if (cur != null && IsBlack(cur)) L.Overrides[id] = new EffectConfig { Type = EffectType.Static, Colors = new() { Scale(255, 255, 255, bri.Value) } };
            }
            else if (state == "ON" && fxName == null && cur != null && IsBlack(cur)) L.Overrides.Remove(id); // "on" from off: follow global again
        }
        _svc.Apply(c);
    }

    void LightCommand(string payload)
    {
        var j = JsonNode.Parse(payload) as JsonObject;
        if (j == null) return;
        var c = Clone(_svc.Config);
        var L = c.Lighting;
        var state = (string?)j["state"];
        if (state == "OFF") L.Enabled = false;
        else if (state == "ON") L.Enabled = true;
        if (j["brightness"] is JsonValue bv && bv.TryGetValue<double>(out var bri)) L.Brightness = FromHa((int)Math.Round(bri));
        if ((string?)j["effect"] is { } fxName && L.Presets.TryGetValue(fxName, out var preset))
        {
            L.Effect = JsonSerializer.Deserialize<EffectConfig>(JsonSerializer.Serialize(preset, GlintConfig.Json), GlintConfig.Json)!;
            L.Overrides.Clear(); L.Enabled = true;
        }
        if (j["color"] is JsonObject col)
        {
            int r = Math.Clamp((int?)col["r"] ?? 0, 0, 255), g = Math.Clamp((int?)col["g"] ?? 0, 0, 255), b = Math.Clamp((int?)col["b"] ?? 0, 0, 255);
            L.Effect = new EffectConfig { Type = EffectType.Static, Colors = new() { $"#{r:x2}{g:x2}{b:x2}" } };
            L.Overrides.Clear(); L.Enabled = true;
        }
        if (L.Enabled && L.Effect.Type == EffectType.Off && state == "ON" && j["effect"] == null && j["color"] == null)
            L.Effect = new EffectConfig { Type = EffectType.Static, Colors = new() { "#ffffff" } }; // "on" from an Off effect: plain white
        _svc.Apply(c);
    }
}
