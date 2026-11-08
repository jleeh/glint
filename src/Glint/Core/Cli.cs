using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glint.Core;

/// <summary>`Glint.exe ctl ...` (command line) and `Glint.exe mcp` (Model Context Protocol server over stdio).</summary>
public static class Cli
{
    [DllImport("kernel32")] static extern bool AttachConsole(int pid);

    /// <summary>Glint.exe ctl &lt;method&gt; [json-params]   e.g.  ctl plugins.list   ctl profile.set "{\"name\":\"Quiet\"}"</summary>
    public static int Ctl(string[] a)
    {
        if (!Console.IsOutputRedirected) AttachConsole(-1);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        if (a.Length == 0) a = new[] { "help" };
        // params: either one JSON object, or key=value pairs (values parsed as JSON when possible:
        //   ctl profile.set name=Quiet      ctl lighting.brightness value=40      ctl plugins.setEnabled file=lava.js enabled=false
        JsonNode? prms = null;
        if (a.Length > 1)
        {
            if (a[1].StartsWith('@') && File.Exists(a[1][1..])) a[1] = File.ReadAllText(a[1][1..]);   // ctl config.set @params.json
            if (a[1].TrimStart().StartsWith('{'))
            {
                try { prms = JsonNode.Parse(a[1]); }
                catch { stdout.WriteLine("params must be a JSON object or key=value pairs, e.g. name=Quiet"); return 2; }
            }
            else
            {
                var kvs = new JsonObject();
                foreach (var kv in a.Skip(1))
                {
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) { stdout.WriteLine($"expected key=value, got '{kv}'"); return 2; }
                    var raw = kv[(eq + 1)..];
                    JsonNode? val;
                    if (raw.StartsWith('@') && File.Exists(raw[1..]))                                   // code=@my-plugin.js
                    {
                        var text = File.ReadAllText(raw[1..]);
                        try { val = raw.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? JsonNode.Parse(text) : JsonValue.Create(text); }
                        catch { val = JsonValue.Create(text); }
                    }
                    else try { val = JsonNode.Parse(raw); } catch { val = JsonValue.Create(raw); }
                    kvs[kv[..eq]] = val;
                }
                prms = kvs;
            }
        }
        var resp = ControlServer.Call(a[0], prms);
        if (resp == null) { stdout.WriteLine("{\"error\":\"Glint is not running\"}"); return 3; }
        var node = JsonNode.Parse(resp)!;
        var o = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        if (node["error"] is JsonNode err) { stdout.WriteLine(JsonSerializer.Serialize(new { error = (string?)err }, o)); return 1; }
        var res = node["result"];
        stdout.WriteLine(res is JsonValue v && v.TryGetValue<string>(out var str) ? str : res?.ToJsonString(o) ?? "null");
        return 0;
    }

    // ------------------------------------------------------------------ MCP

    static readonly Dictionary<string, (string method, JsonObject schema)> Tools = Build();

    /// <summary>Is there an MCP tool of this name (tests check the AI tuner only asks for tools that exist).</summary>
    public static bool HasTool(string name) => Tools.ContainsKey(name);

    static Dictionary<string, (string, JsonObject)> Build()
    {
        JsonObject Obj(params (string name, string type, string desc, bool req)[] props)
        {
            var p = new JsonObject(); var req = new JsonArray();
            foreach (var x in props)
            {
                p[x.name] = x.type == "any" ? new JsonObject { ["description"] = x.desc } : new JsonObject { ["type"] = x.type, ["description"] = x.desc };
                if (x.req) req.Add(x.name);
            }
            return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
        }
        var d = new Dictionary<string, (string, JsonObject)>();
        void T(string method, JsonObject schema) => d["glint_" + method.Replace('.', '_')] = (method, schema);
        T("status", Obj());
        T("devices", Obj());
        T("sensors.all", Obj());
        T("sensors.list", Obj());
        T("hid.list", Obj(("vendorId", "number", "Only this USB vendor id (e.g. 6940 for 0x1b1c Corsair)", false)));
        T("plugins.list", Obj());
        T("plugins.docs", Obj());
        T("plugins.template", Obj(("kind", "string", "'effect' or 'device'", true)));
        T("plugins.get", Obj(("file", "string", "Plugin file name, e.g. aurora.js", true)));
        T("plugins.add", Obj(("file", "string", "File name ending in .js", true), ("code", "string", "Full JavaScript source (module.exports = {...})", true)));
        T("plugins.check", Obj(("file", "string", "Plugin file name", true)));
        T("plugins.remove", Obj(("file", "string", "User plugin file name", true)));
        T("plugins.setEnabled", Obj(("file", "string", "Plugin file name", true), ("enabled", "boolean", "true to enable", true)));
        T("plugins.reload", Obj());
        T("lighting.presets", Obj());
        T("lighting.preset", Obj(("name", "string", "Preset name", true)));
        T("lighting.set", Obj(("effect", "object", "Effect: { type: 'static'|'wave'|'plugin'|..., colors?: ['#rrggbb'], speed?, plugin?: 'Aurora', params?: {...} }", true),
                             ("target", "string", "'all' (default), a device id (e.g. 'xt') or a zone id (e.g. 'xt:fan3')", false)));
        T("lighting.brightness", Obj(("value", "number", "0-100", true)));
        T("lighting.identify", Obj(("target", "string", "Device id (e.g. 'xt') or zone id (e.g. 'xt:fan3')", true), ("seconds", "number", "How long to flash (default 4)", false)));
        T("lighting.layout", Obj());
        T("lighting.autoLayout", Obj(("confirm", "boolean", "Must be true, and only after the user agreed: replaces their hand-tuned layout (a backup is saved first)", true)));
        T("lighting.layoutBackups", Obj());
        T("lighting.restoreLayout", Obj(("name", "string", "Backup name from lighting.layoutBackups, or 'latest' (default)", false)));
        var points = JsonNode.Parse("""{"type":"array","description":"[[tempC, duty%], ...] with temperatures strictly increasing (-20..150) and duty 0..100","items":{"type":"array","items":{"type":"number"},"minItems":2,"maxItems":2}}""")!.AsObject();
        var rule = JsonNode.Parse("""
            {"type":"object","description":"Fields to set (others keep their value; a new rule starts from defaults: combine max, minDuty 20, maxDuty 100, rampUpPerSec 25, rampDownPerSec 4)","properties":{
              "name":{"type":"string","description":"New name (to rename the rule)"},
              "fans":{"type":"array","items":{"type":"string"},"description":"Fan output ids, e.g. ['xt:fan1','xt:fan2'] (see glint_fans_list). Replaces the list."},
              "inputs":{"type":"array","description":"Replaces the list. Each: { sensor, curve | points, weight?, hysteresis?, smoothingSec? }","items":{"type":"object","properties":{
                 "sensor":{"type":"string","description":"Sensor id (cpu, gpu, liquid, xt:probe1, ...) or max(a,b)/min(a,b)/avg(a,b)"},
                 "curve":{"type":"string","description":"Named curve (glint_fans_list 'curves')"},
                 "points":{"type":"array","description":"Inline curve [[tempC, duty%], ...] instead of a named one"},
                 "weight":{"type":"number","description":"Weight for combine 'weighted' (default 1)"},
                 "hysteresis":{"type":"number","description":"°C drop before backing off (default 2, 0..30)"},
                 "smoothingSec":{"type":"number","description":"Smoothing time constant, s (default 0, 0..600)"}},"required":["sensor"]}},
              "combine":{"type":"string","enum":["max","average","weighted"]},
              "minDuty":{"type":"integer","minimum":0,"maximum":100},
              "maxDuty":{"type":"integer","minimum":0,"maximum":100},
              "rampUpPerSec":{"type":"number","description":"% per second (0.1..1000)"},
              "rampDownPerSec":{"type":"number","description":"% per second (0.1..1000)"},
              "fixed":{"type":["integer","null"],"description":"Hold this duty and ignore inputs; null = use inputs"},
              "override":{"type":["integer","null"],"description":"Manual duty; null = automatic (prefer glint_fans_override)"}}}
            """)!.AsObject();
        T("fans.list", Obj());
        var setRule = Obj(("name", "string", "Rule name (created if it doesn't exist)", true), ("profile", "string", "Profile (default: the active one)", false));
        setRule["properties"]!["rule"] = rule; setRule["required"]!.AsArray().Add("rule");
        T("fans.setRule", setRule);
        T("fans.removeRule", Obj(("name", "string", "Rule name", true), ("profile", "string", "Profile (default: the active one)", false)));
        var ovr = Obj(("name", "string", "Rule name (see glint_fans_list)", true), ("profile", "string", "Profile (default: the active one)", false));
        ovr["properties"]!["percent"] = JsonNode.Parse("""{"type":["number","null"],"description":"Duty % (clamped to the rule's minDuty..maxDuty), or null for automatic"}""");
        ovr["required"]!.AsArray().Add("percent");
        T("fans.override", ovr);
        var curve = Obj(("name", "string", "Curve name", true));
        curve["properties"]!["points"] = points; curve["required"]!.AsArray().Add("points");
        T("curves.set", curve);
        T("curves.remove", Obj(("name", "string", "Curve name (refused while a rule uses it)", true)));
        T("profiles.create", Obj(("name", "string", "New profile name", true), ("from", "string", "Profile to copy (default: the active one)", false)));
        T("profiles.remove", Obj(("name", "string", "Profile name (not the active one)", true)));
        T("profiles.rename", Obj(("from", "string", "Current profile name", true), ("to", "string", "New name (1-40 characters, unique, not 'Stock')", true)));
        T("profile.set", Obj(("name", "string", "Fan profile name", true)));
        T("config.get", Obj());
        T("config.schema", Obj());
        T("config.validate", Obj(("config", "object", "Complete config object to check (nothing is saved)", true)));
        T("config.set", Obj(("config", "object", "Complete config object (get it first, modify, send back)", true)));
        T("config.patch", Obj(("path", "string", "Dotted path in the config, e.g. 'lighting.brightness', 'sensors.storage', 'profiles.Quiet.0.minDuty'", true), ("value", "any", "New value (any JSON)", true)));
        T("overlay.get", Obj());
        var ovItems = JsonNode.Parse("""
            {"type":"array","description":"What to show, in order (replaces the list). Sensors that aren't reading are left out.","items":{"type":"object","properties":{
              "source":{"type":"string","description":"fps | frametime | low1 | time | glint.cpu | fan:<fanId> | duty:<fanId> | a sensor id (cpu, gpu, gpu.hotspot, liquid, ... see glint_sensors_list)"},
              "label":{"type":["string","null"],"description":"Label (null = default)"},
              "color":{"type":["string","null"],"description":"Value colour #rrggbb (null = default)"}},"required":["source"]}}
            """)!.AsObject();
        var ovSet = Obj(("enabled", "boolean", "Overlay on screen", false), ("hotkey", "string", "Toggle hotkey, e.g. 'Ctrl+Shift+O' ('' = none)", false),
            ("position", "string", "top-left | top-right | bottom-left | bottom-right", false), ("offsetX", "integer", "px from the corner at 100% scaling", false),
            ("offsetY", "integer", "px from the corner at 100% scaling", false), ("scale", "number", "Size multiplier 0.5..4", false), ("opacity", "number", "0.1..1", false),
            ("backgroundOpacity", "number", "Panel background 0..1", false), ("layout", "string", "compact | detailed", false),
            ("onlyWhenGame", "boolean", "Only show over fullscreen / borderless-fullscreen apps", false));
        ovSet["properties"]!["accent"] = JsonNode.Parse("""{"type":["string","null"],"description":"#rrggbb, or null for Glint's gradient"}""");
        ovSet["properties"]!["items"] = ovItems;
        T("overlay.set", ovSet);
        T("overlay.toggle", Obj(("enabled", "boolean", "true = show, false = hide; omit to flip", false)));
        T("overlay.stats", Obj());
        T("ha.status", Obj());
        T("setup.check", Obj());
        T("tuning.status", Obj());
        T("tuning.gpu.test", Obj(("core", "integer", "Core clock offset, MHz (0..tuning.status limits.maxCore)", true), ("memAb", "integer", "Memory offset in MSI Afterburner units (0..limits.maxMemAb)", true),
                                 ("seconds", "number", "Test length 10-300 (default 60)", false), ("light", "boolean", "Low-load test at top boost clocks (default false)", false)));
        T("tuning.history", Obj(("offset", "integer", "Skip this many (newest first, default 0)", false), ("limit", "integer", "Up to this many (default 30, max 100)", false)));
        T("tuning.wait", Obj(("seconds", "number", "Max seconds to wait (default 20, max 25)", false)));
        T("tuning.stop", Obj());
        T("tuning.gpu.apply", Obj(("core", "integer", "Core offset MHz", true), ("memAb", "integer", "Memory offset, Afterburner units", true)));
        T("tuning.gpu.save", Obj(("core", "integer", "Core offset MHz", true), ("memAb", "integer", "Memory offset, Afterburner units", true)));
        T("tuning.gpu.stock", Obj());
        T("tuning.cpu.status", Obj());
        T("tuning.cpu.start", Obj(("startValue", "integer", "Curve Optimizer value every core starts at, -50..0 (Ryzen 5000: -30 at most); see tuning.cpu.status curveOptimizer.startHint", true)));
        T("tuning.cpu.finish", Obj(("force", "boolean", "Use the values found so far even though the search hasn't finished (untested cores keep the start value). Default false", false)));
        T("tuning.cpu.stop", Obj());
        T("tuning.crashes", Obj());
        T("tuning.ai.status", Obj());
        T("update.status", Obj());
        T("update.check", Obj());
        T("update.install", Obj(("confirm", "boolean", "Must be true, and only after the user agreed: installs the newer verified release and restarts Glint", true)));
        T("log", Obj(("lines", "number", "How many recent lines (default 100)", false)));
        T("app.restart", Obj());
        return d;
    }

    public static void Print(string text)
    {
        if (!Console.IsOutputRedirected) AttachConsole(-1);
        using var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        o.WriteLine(text);
    }

    /// <summary>Tool names the restricted "tuning" MCP server offers (the AI tuner's whole world).</summary>
    public static bool IsTuningTool(string method) => method.StartsWith("tuning.", StringComparison.Ordinal) && !method.StartsWith("tuning.ai.", StringComparison.Ordinal);

    /// <param name="only">"tuning" = offer and accept only the tuning tools (what "Tune with AI" runs), null = everything.</param>
    public static int Mcp(string? only = null)
    {
        var tools = only == "tuning" ? Tools.Where(t => IsTuningTool(t.Value.method)).ToDictionary(t => t.Key, t => t.Value) : Tools;
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Send(object o) => output.WriteLine(JsonSerializer.Serialize(o));
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); } catch { continue; }
            var id = msg?["id"]?.DeepClone();
            string method = (string?)msg?["method"] ?? "";
            if (id == null) continue; // notification
            object? result = null; object? error = null;
            switch (method)
            {
                case "initialize":
                    result = new
                    {
                        protocolVersion = (string?)msg!["params"]?["protocolVersion"] ?? "2025-06-18",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "glint", version = typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "1.0" },
                        instructions = "Glint controls this PC's RGB lighting and fans. If something doesn't work (no CPU temperature, a device missing, flickering), run glint_setup_check first. For cooling, start with glint_fans_list (rules, curves, sensor ids, live duty/rpm) " +
                                       "and change it with glint_fans_setRule / glint_curves_set / glint_fans_override; glint_config_schema documents every config field. " +
                                       "Writes are validated: {ok:false, errors} means nothing was saved. Never run glint_lighting_autoLayout without the user's consent. " +
                                       "To add hardware or effects, read glint_plugins_docs first, " +
                                       "find the device with glint_hid_list, write a plugin with glint_plugins_add (check the returned status for errors), " +
                                       "then glint_app_restart for device plugins. SMBus plugins must be approved by the user in the dashboard.",
                    };
                    break;
                case "ping": result = new { }; break;
                case "tools/list":
                    var help = ControlServer.Methods.ToDictionary(m => m.Method, m => m.Help);
                    result = new { tools = tools.Select(t => new { name = t.Key, description = help.GetValueOrDefault(t.Value.method, ""), inputSchema = t.Value.schema }) };
                    break;
                case "tools/call":
                    {
                        var name = (string?)msg!["params"]?["name"] ?? "";
                        var args = msg["params"]?["arguments"]?.DeepClone();
                        if (!tools.TryGetValue(name, out var tool)) { error = new { code = -32602, message = "unknown tool " + name }; break; }
                        var resp = ControlServer.Call(tool.method, args, tool.method == "app.restart" ? 5000 : 30000);
                        if (resp == null) { result = new { content = new[] { new { type = "text", text = "Glint is not running (start it from the Start menu or the 'Glint' scheduled task)." } }, isError = true }; break; }
                        var node = JsonNode.Parse(resp)!;
                        bool isErr = node["error"] != null;
                        var payload = isErr ? node["error"] : node["result"];
                        string text = payload is JsonValue v && v.TryGetValue<string>(out var s) ? s : payload?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
                        result = new { content = new[] { new { type = "text", text } }, isError = isErr };
                        break;
                    }
                default: error = new { code = -32601, message = "method not found: " + method }; break;
            }
            if (error != null) Send(new { jsonrpc = "2.0", id, error });
            else Send(new { jsonrpc = "2.0", id, result });
        }
        return 0;
    }
}
