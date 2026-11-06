using System.Text.Json;
using Glint.Core;
using Glint.Devices;
using HidSharp;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Microsoft.ClearScript.JavaScript;

namespace Glint.Plugins;

/// <summary>Result of loading one plugin file (shown on the dashboard's Devices page).</summary>
public sealed record PluginStatus(string File, string Kind, string Name, bool Ok, string Message, bool NeedsApproval = false);

/// <summary>
/// Approvals for plugins that ask for powerful buses (SMBus). Stored next to Glint.exe — in Program Files
/// when installed, which only an elevated process can write — and keyed by the file's SHA-256, so editing
/// a plugin revokes its approval and a normal-privilege program can't approve its own script.
/// </summary>
public static class PluginTrust
{
    /// <summary>Tests only: a store in a temp folder.</summary>
    internal static string? TestStorePath;
    static string StorePath => TestStorePath ?? Path.Combine(AppContext.BaseDirectory, "trusted-plugins.json");
    public static string Hash(byte[] content) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
    static Dictionary<string, string> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? new(); } catch { return new(); }
    }
    /// <param name="content">The exact bytes that will run (hashing the file again later would let it be swapped in between).</param>
    public static bool IsTrusted(string file, byte[] content)
    {
        if (PluginHost.IsBundled(file)) return true;
        return Load().TryGetValue(Path.GetFileName(file), out var h) && h == Hash(content);
    }
    public static void Approve(string file)
    {
        var d = Load();
        d[Path.GetFileName(file)] = Hash(PluginHost.ReadBytes(file));
        File.WriteAllText(StorePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>An effect plugin's metadata (for the dashboard gallery + parameter UI).</summary>
public sealed record PluginEffectInfo(string Name, string Description, JsonElement Params, string[] Preview, string File);

/// <summary>
/// Loads JavaScript plugins from %APPDATA%\Glint\plugins (user) and &lt;install&gt;\plugins (bundled examples).
/// Plugins run on V8 (ClearScript) with no .NET, file or network access, a heap cap and a per-call watchdog.
/// Effect plugins hot-reload on save; device plugins are picked up at startup (or "Restart Glint").
/// </summary>
public static class PluginHost
{
    public static string UserDir => Path.Combine(GlintConfig.Dir, "plugins");
    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "plugins");
    public static readonly List<PluginStatus> Status = new();
    /// <summary>Status as one consistent list (a reload clears and refills it under the lock).</summary>
    public static List<PluginStatus> StatusSnapshot() { lock (_lock) return Status.ToList(); }
    static readonly Dictionary<string, EffectPlugin> _effects = new(StringComparer.OrdinalIgnoreCase);
    static FileSystemWatcher? _watch;
    public static event Action? EffectsChanged;
    /// <summary>File names the user switched off (set from config by the service).</summary>
    public static Func<IEnumerable<string>> Disabled = () => Array.Empty<string>();
    /// <summary>Every plugin file found (bundled + user), for the manager page and the control API.</summary>
    public static IEnumerable<string> AllFiles => Files();
    public static bool IsBundled(string file) => Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(BundledDir)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Largest plugin file Glint will read.</summary>
    public const int MaxPluginBytes = 2 << 20;

    /// <summary>Read a plugin file as the signed-in user (the user folder may contain links planted to reach other files).</summary>
    internal static byte[] ReadBytes(string file) => UserFs.Run(() =>
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > MaxPluginBytes) throw new InvalidDataException($"plugin file is larger than {MaxPluginBytes / 1048576} MB");
        var b = new byte[fs.Length];
        fs.ReadExactly(b);
        return b;
    });

    /// <summary>Same decoding as File.ReadAllText (BOM-aware, UTF-8 default).</summary>
    internal static string Decode(byte[] b)
    {
        using var r = new StreamReader(new MemoryStream(b), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    public static string ReadCode(string file) => Decode(ReadBytes(file));
    static readonly object _lock = new();

    public static IReadOnlyList<PluginEffectInfo> Effects
    {
        get { lock (_lock) return _effects.Values.Select(e => e.Info).OrderBy(e => e.Name).ToList(); }
    }

    /// <summary>Prelude available to every plugin as the global <c>glint</c>.</summary>
    internal const string Prelude = """
        var module = { exports: {} }; var exports = module.exports;
        var glint = {
          clamp: function (v, a, b) { return v < a ? a : v > b ? b : v; },
          lerp: function (a, b, t) { return a + (b - a) * t; },
          hex: function (s) { s = String(s || '#000000').replace('#', ''); var n = parseInt(s, 16) || 0; return [(n >> 16) & 255, (n >> 8) & 255, n & 255]; },
          mix: function (c1, c2, t) { return [c1[0] + (c2[0] - c1[0]) * t, c1[1] + (c2[1] - c1[1]) * t, c1[2] + (c2[2] - c1[2]) * t]; },
          hsv: function (h, s, v) {
            if (s === undefined) s = 1; if (v === undefined) v = 1;
            h = ((h % 360) + 360) % 360 / 60; var i = Math.floor(h) % 6, f = h - Math.floor(h), p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
            var r = [[v, t, p], [q, v, p], [p, v, t], [p, q, v], [t, p, v], [v, p, q]][i];
            return [r[0] * 255, r[1] * 255, r[2] * 255];
          },
          palette: function (cols, p) {
            if (!cols || !cols.length) return [255, 255, 255];
            if (cols.length === 1) return cols[0];
            p = ((p % 1) + 1) % 1 * cols.length; var i = Math.floor(p);
            return glint.mix(cols[i % cols.length], cols[(i + 1) % cols.length], p - i);
          },
          noise: function (x, y) {
            function h(a, b) { var n = (a * 374761393 + b * 668265263) | 0; n = Math.imul(n ^ (n >>> 13), 1274126177); return ((n ^ (n >>> 16)) & 0xffff) / 65535; }
            var xi = Math.floor(x), yi = Math.floor(y), xf = x - xi, yf = y - yi;
            xf = xf * xf * (3 - 2 * xf); yf = yf * yf * (3 - 2 * yf);
            var a = h(xi, yi), b = h(xi + 1, yi), c = h(xi, yi + 1), d = h(xi + 1, yi + 1);
            return a + (b - a) * xf + (c - a) * yf + (a - b - c + d) * xf * yf;
          },
          fbm: function (x, y, oct) { var s = 0, a = 0.5, f = 1; oct = oct || 3; for (var i = 0; i < oct; i++) { s += glint.noise(x * f, y * f) * a; f *= 2; a *= 0.5; } return s; }
        };
        """;

    /// <summary>A sandboxed V8 engine: no host types, 64 MB heap.</summary>
    /// <summary>One V8 isolate for all effect plugins (they all run on the lighting thread), created on first use.</summary>
    static readonly Lazy<V8Runtime> _runtime = new(() => new V8Runtime("glint-effects", new V8RuntimeConstraints { MaxOldSpaceSize = 64, MaxNewSpaceSize = 4, MaxArrayBufferAllocation = MaxArrayBufferBytes }));
    /// <summary>Typed arrays live outside the V8 heap: cap them too, or one plugin could allocate gigabytes.</summary>
    public const ulong MaxArrayBufferBytes = 64UL << 20;

    internal static V8ScriptEngine NewEngine(string name)
    {
        var e = _runtime.Value.CreateScriptEngine(name, V8ScriptEngineFlags.DisableGlobalMembers);
        e.Execute("glint-prelude", Prelude);
        return e;
    }

    /// <summary>Run with a watchdog: the call is interrupted if it exceeds <paramref name="ms"/>.</summary>
    internal static T Guard<T>(V8ScriptEngine e, int ms, Func<T> f)
    {
        using var t = new System.Threading.Timer(_ => { try { e.Interrupt(); } catch { } }, null, ms, Timeout.Infinite);
        try { return f(); }
        catch (ScriptInterruptedException) { throw new TimeoutException("plugin took too long"); }
    }

    public static void LoadAll()
    {
        lock (_lock)
        {
            Status.Clear(); _effects.Clear();
            DriverRegistry.Hid.RemoveAll(d => d.Name.StartsWith("Plugin: "));
            DriverRegistry.SmbusDrivers.RemoveAll(d => d.Name.StartsWith("Plugin: "));
            try { UserFs.Run(() => { Directory.CreateDirectory(UserDir); SeedUserDir(); }); } catch { }
            var off = Disabled().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files())
            {
                if (off.Contains(Path.GetFileName(file))) { Status.Add(new PluginStatus(Path.GetFileName(file), Kind(file), NameOf(file), true, "disabled")); continue; }
                try { LoadFile(file, effectsOnly: false); }
                catch (Exception e) { Status.Add(new PluginStatus(Path.GetFileName(file), Kind(file), NameOf(file), false, Clean(e))); }
            }
        }
        if (_watch == null)
        {
            try
            {
                _watch = new FileSystemWatcher(UserDir, "*.js") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
                _watch.Changed += (_, _) => ReloadSoon(); _watch.Created += (_, _) => ReloadSoon();
                _watch.Deleted += (_, _) => ReloadSoon(); _watch.Renamed += (_, _) => ReloadSoon();
                _watch.EnableRaisingEvents = true;
            }
            catch { }
        }
    }

    /// <summary>First run: put the guide + templates next to the user's plugins (templates as .txt so they don't load).</summary>
    static void SeedUserDir()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "plugin-templates");
        if (!Directory.Exists(src)) return;
        var guide = Path.Combine(UserDir, "HOW TO WRITE PLUGINS.md");
        if (!File.Exists(guide) && File.Exists(Path.Combine(src, "PLUGINS.md"))) File.Copy(Path.Combine(src, "PLUGINS.md"), guide);
        var tdir = Path.Combine(UserDir, "templates");
        Directory.CreateDirectory(tdir);
        foreach (var t in Directory.GetFiles(src, "*.js"))
        {
            var dest = Path.Combine(tdir, Path.GetFileName(t) + ".txt");
            if (!File.Exists(dest)) File.Copy(t, dest);
        }
    }

    /// <summary>Cheap metadata read (kind/name) without registering anything.</summary>
    static readonly Dictionary<string, (DateTime t, string kind, string name)> _meta = new();
    static (string kind, string name) Meta(string file)
    {
        var t = UserFs.Run(() => File.GetLastWriteTimeUtc(file));
        if (_meta.TryGetValue(file, out var m) && m.t == t) return (m.kind, m.name);
        string kind = "?", name = Path.GetFileNameWithoutExtension(file);
        try
        {
            var d = JintHost.Describe(ReadCode(file), file);
            kind = d.GetProperty("type").GetString() ?? "effect";
            if (d.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString()!;
        }
        catch { }
        _meta[file] = (t, kind, name);
        return (kind, name);
    }
    static string Kind(string f) => Meta(f).kind;
    static string NameOf(string f) => Meta(f).name;

    public static string? FindFile(string name) => Files().FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>User plugins override bundled ones with the same file name.</summary>
    static IEnumerable<string> Files() => UserFs.Run(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { BundledDir, UserDir })
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.js", SearchOption.AllDirectories))
                    map[Path.GetFileName(f)] = f;
        return map.Values.OrderBy(f => f).ToList();
    });

    static int _reloading;
    public static void ReloadSoon()
    {
        if (Interlocked.Exchange(ref _reloading, 1) == 1) return;
        Task.Delay(400).ContinueWith(_ => { _reloading = 0; ReloadNow(); });
    }

    /// <summary>Reload effect plugins now (device plugins keep running until restart; they own hardware).</summary>
    public static void ReloadNow()
    {
        lock (_lock)
        {
            var devStatus = Status.Where(s => s.Kind is "device" or "smbus").ToList();
            Status.Clear(); _effects.Clear();
            var off = Disabled().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Files())
            {
                if (off.Contains(Path.GetFileName(f))) { Status.Add(new PluginStatus(Path.GetFileName(f), Kind(f), NameOf(f), true, "disabled")); continue; }
                try { LoadFile(f, effectsOnly: true); }
                catch (Exception e) { Status.Add(new PluginStatus(Path.GetFileName(f), Kind(f), NameOf(f), false, Clean(e))); }
            }
            // keep the "running" status of device plugins that loaded at startup
            foreach (var d in devStatus)
            {
                int i = Status.FindIndex(s => s.File == d.File);
                if (i >= 0 && Status[i].Ok && Status[i].Message.StartsWith("restart")) Status[i] = d;
            }
        }
        EffectsChanged?.Invoke();
    }

    internal static string Clean(Exception e)
    {
        if (e is TimeoutException) return "Took too long (timed out)";
        if (e.GetType().Namespace?.StartsWith("Jint") == true || e.GetType().Namespace?.StartsWith("Acornima") == true || e.GetType().Namespace?.StartsWith("Esprima") == true) return JintHost.Clean(e);
        if (e is ScriptEngineException se) return (se.ErrorDetails ?? se.Message).Split('\n')[0].Trim();
        return e.InnerException != null && e is not IOException ? Clean(e.InnerException) : e.Message;
    }

    static string? Str(JsonElement d, string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int Opt(JsonElement d, string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;

    static void LoadFile(string file, bool effectsOnly)
    {
        var bytes = ReadBytes(file);
        var code = Decode(bytes);
        var d = JintHost.Describe(code, file);
        string type = Str(d, "type") ?? "effect";
        string name = Str(d, "name") ?? Path.GetFileNameWithoutExtension(file);
        if (type == "effect")
        {
            if (!d.GetProperty("hasRender").GetBoolean()) throw new InvalidOperationException("effect plugins need a render(ctx, leds, out) function");
            var info = new PluginEffectInfo(name, Str(d, "description") ?? "", d.GetProperty("params").Clone(),
                d.GetProperty("preview").EnumerateArray().Select(x => x.ToString()).ToArray(), file);
            _effects[name] = new EffectPlugin(file, code, info);
            Status.Add(new PluginStatus(Path.GetFileName(file), "effect", name, true, info.Description));
        }
        else if (type == "device")
        {
            if (effectsOnly) { Status.Add(new PluginStatus(Path.GetFileName(file), "device", name, true, "restart Glint to load device changes")); return; }
            int vid = Opt(d, "vendorId");
            if (vid < 0) throw new InvalidOperationException("device plugins need vendorId: 0x....");
            var pids = d.GetProperty("productIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (pids.Length == 0) throw new InvalidOperationException("device plugins need productIds: [0x....]");
            var def = new DevicePluginDef(file, code, name);
            DriverRegistry.Hid.Add(new HidDriver($"Plugin: {name}", vid, pids, (dv, ctx) => def.Open(new HidSharpPort(dv), ctx),
                Interface: Opt(d, "interface"), UsagePage: Opt(d, "usagePage"), MinOutputReport: Math.Max(0, Opt(d, "reportSize")), Experimental: true,
                UseFeatureReports: d.GetProperty("featureReports").GetBoolean()));
            Status.Add(new PluginStatus(Path.GetFileName(file), "device", name, true, $"{vid:x4}:{string.Join(",", pids.Select(p => p.ToString("x4")))}"));
        }
        else if (type == "smbus")
        {
            var addrs = d.GetProperty("addresses").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (addrs.Length == 0) throw new InvalidOperationException("smbus plugins must declare addresses: [0x.., ...]");
            var blocked = addrs.Where(a => a is >= 0x50 and <= 0x57 or < 0x03 or > 0x77).ToArray();
            if (blocked.Length > 0) throw new InvalidOperationException($"addresses not allowed: {string.Join(", ", blocked.Select(a => $"0x{a:x2}"))} (SPD EEPROM / reserved)");
            if (!PluginTrust.IsTrusted(file, bytes))
            {
                Status.Add(new PluginStatus(Path.GetFileName(file), "smbus", name, false, "Needs your approval: this plugin wants SMBus access", NeedsApproval: true));
                return;
            }
            if (effectsOnly) { Status.Add(new PluginStatus(Path.GetFileName(file), "smbus", name, true, "restart Glint to load device changes")); return; }
            var def = new DevicePluginDef(file, code, name);
            DriverRegistry.SmbusDrivers.Add(new SmbusDriver($"Plugin: {name}", (bus, ctx) => def.OpenSmbus(bus, addrs, ctx), Experimental: true));
            Status.Add(new PluginStatus(Path.GetFileName(file), "smbus", name, true, $"SMBus {string.Join(" ", addrs.Select(a => $"0x{a:x2}"))}"));
        }
        else throw new InvalidOperationException($"unknown plugin type '{type}' (use 'effect', 'device' or 'smbus')");
    }

    public static Effect? CreateEffect(EffectConfig cfg)
    {
        EffectPlugin? p;
        lock (_lock) _effects.TryGetValue(cfg.Plugin ?? "", out p);
        return p == null ? null : new PluginEffect(cfg, p);
    }

    /// <summary>Load a single file for tests / plugintest (no registry side effects).</summary>
    internal static (EffectPlugin? effect, DevicePluginDef? device) LoadForTest(string file)
    {
        var code = ReadCode(file);
        var d = JintHost.Describe(code, file);
        string type = Str(d, "type") ?? "effect";
        string name = Str(d, "name") ?? Path.GetFileNameWithoutExtension(file);
        if (type is "device" or "smbus") return (null, new DevicePluginDef(file, code, name));
        return (new EffectPlugin(file, code, new PluginEffectInfo(name, "", d.GetProperty("params").Clone(), Array.Empty<string>(), file)), null);
    }

    internal static V8ScriptEngine Boot(string code, string file)
    {
        var e = NewEngine(Path.GetFileName(file));
        Guard(e, 2000, () => { e.Execute(Path.GetFileName(file), code); return 0; });
        return e;
    }
}

public sealed class EffectPlugin(string file, string code, PluginEffectInfo info)
{
    public string File { get; } = file;
    public string Code { get; } = code;
    public PluginEffectInfo Info { get; } = info;
}

/// <summary>Runs a JS effect plugin: render(ctx, leds, out) once per frame. Each instance has its own engine/state.</summary>
public sealed class PluginEffect : Effect, IDisposable
{
    readonly V8ScriptEngine _e;
    readonly ScriptObject _exp, _ctx, _sensors;
    readonly bool _animated;
    ScriptObject? _leds;
    ITypedArray<byte>? _out;
    byte[] _buf = Array.Empty<byte>();
    int _n = -1;
    public string Error { get; private set; } = "";

    public PluginEffect(EffectConfig cfg, EffectPlugin p) : base(cfg)
    {
        _e = PluginHost.Boot(p.Code, p.File);
        _exp = (ScriptObject)_e.Evaluate("module.exports");
        _animated = _exp["animated"] is not false;
        var saved = cfg.Params != null ? JsonSerializer.Serialize(cfg.Params) : "{}";
        _ctx = (ScriptObject)_e.Evaluate($$"""
            (function () {
              var p = module.exports.params || {}, saved = {{saved}}, out = {};
              for (var k in p) out[k] = (k in saved) ? saved[k] : p[k].default;
              for (var k in saved) if (!(k in out)) out[k] = saved[k];
              for (var k in out) {
                if (p[k] && p[k].type === 'colors' && out[k]) out[k] = out[k].map(glint.hex);
                if (p[k] && p[k].type === 'color') out[k] = glint.hex(out[k]);
              }
              return { time: 0, dt: 0, params: out, sensors: {}, spectrum: [], level: 0, state: {}, colors: {{JsonSerializer.Serialize(cfg.Colors)}}.map(glint.hex) };
            })()
            """);
        _sensors = (ScriptObject)_ctx["sensors"];
        if (_exp["init"] is ScriptObject) PluginHost.Guard(_e, 500, () => _exp.InvokeMethod("init", _ctx));
    }

    public override bool Animated => _animated;

    public override void Render(FxContext x, ReadOnlySpan<Led> leds, Span<Rgb> o)
    {
        try
        {
            if (_n != leds.Length || _leds == null || _out == null)
            {
                _n = leds.Length;
                var json = JsonSerializer.Serialize(leds.ToArray().Select(l => new { i = l.Index, x = Math.Round(l.X, 4), y = Math.Round(l.Y, 4), u = Math.Round(l.U, 4), zone = l.Zone, ring = l.Ring }));
                _leds = (ScriptObject)_e.Evaluate("(" + json + ")");
                _out = (ITypedArray<byte>)_e.Evaluate($"new Uint8ClampedArray({_n * 3})");
                _buf = new byte[_n * 3];
            }
            _ctx["time"] = x.Time; _ctx["dt"] = x.Dt; _ctx["level"] = (double)x.Level;
            foreach (var (k, v) in x.Sensors) _sensors[k] = v;
            if (x.Spectrum.Length > 0) _ctx["spectrum"] = _e.Evaluate("[" + string.Join(",", x.Spectrum.Select(f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) + "]");
            PluginHost.Guard(_e, 100, () => _exp.InvokeMethod("render", _ctx, _leds, _out));
            _out.ReadBytes(0, (ulong)_buf.Length, _buf, 0);
            for (int i = 0; i < o.Length && i * 3 + 2 < _buf.Length; i++) o[i] = new Rgb(_buf[i * 3], _buf[i * 3 + 1], _buf[i * 3 + 2]);
            Error = "";
        }
        catch (Exception e)
        {
            Error = PluginHost.Clean(e);
            o.Fill(new Rgb(40, 0, 0)); // dim red = plugin error
        }
    }

    public void Dispose() => _e.Dispose();
}

// ============================================================ devices

/// <summary>Minimal HID surface given to device plugins (and faked in tests).</summary>
public interface IHidPort : IDisposable
{
    int VendorId { get; }
    int ProductId { get; }
    string Product { get; }
    string Serial { get; }
    int OutputReportLength { get; }
    int InputReportLength { get; }
    int FeatureReportLength { get; }
    int Interface { get; }
    void Write(byte[] data);
    byte[]? Read(int timeoutMs);
    void SendFeature(byte[] data);
    byte[] GetFeature(byte reportId);
}

public sealed class HidSharpPort : IHidPort
{
    readonly HidDevice _d;
    readonly HidStream _s;
    public HidSharpPort(HidDevice d)
    {
        _d = d;
        _s = d.Open();
        _s.WriteTimeout = 500;
    }
    public int VendorId => _d.VendorID;
    public int ProductId => _d.ProductID;
    public string Product { get { try { return _d.GetProductName(); } catch { return ""; } } }
    public string Serial { get { try { return _d.GetSerialNumber(); } catch { return ""; } } }
    public int OutputReportLength { get { try { return _d.GetMaxOutputReportLength(); } catch { return 0; } } }
    public int FeatureReportLength { get { try { return _d.GetMaxFeatureReportLength(); } catch { return 0; } } }
    public int InputReportLength { get { try { return _d.GetMaxInputReportLength(); } catch { return 0; } } }
    public int Interface => DriverRegistry.InterfaceOf(_d);
    public void Write(byte[] data) => _s.Write(Pad(data, OutputReportLength));
    public byte[]? Read(int timeoutMs)
    {
        _s.ReadTimeout = Math.Max(1, timeoutMs);
        try { var b = new byte[Math.Max(1, _d.GetMaxInputReportLength())]; int n = _s.Read(b, 0, b.Length); return b[..n]; }
        catch (TimeoutException) { return null; }
    }
    public void SendFeature(byte[] data) => _s.SetFeature(Pad(data, FeatureReportLength));
    public byte[] GetFeature(byte reportId) { var b = new byte[FeatureReportLength]; b[0] = reportId; _s.GetFeature(b); return b; }
    static byte[] Pad(byte[] d, int len) => len > d.Length ? d.Concat(new byte[len - d.Length]).ToArray() : d;
    public void Dispose() => _s.Dispose();
}

public sealed class DevicePluginDef(string file, string code, string name)
{
    public string File { get; } = file;
    public string Name { get; } = name;

    public Found? Open(IHidPort port, DriverContext ctx) => ctx.Claim(u => OpenWith(port, u, (z, d) => ctx.Leds(z, d), ctx.Log));

    public Found? OpenWith(IHidPort port, Func<string, string> unique, Func<string, int, int> leds, Action<string> log)
        => Wrap(new JintPluginDevice(File, code, Name, b => new JintHidApi(b, port, Name, log, leds), port, unique, log));

    public Found? OpenSmbus(ISmbusPort bus, int[] addresses, DriverContext ctx) => ctx.Claim(u => OpenSmbusWith(bus, addresses, u, (z, d) => ctx.Leds(z, d), ctx.Log));

    public Found? OpenSmbusWith(ISmbusPort bus, int[] addresses, Func<string, string> unique, Func<string, int, int> leds, Action<string> log)
        => Wrap(new JintPluginDevice(File, code, Name, _ => new JintSmbusApi(bus, addresses, Name, log, leds), null, unique, log));

    static Found? Wrap(JintPluginDevice dev)
    {
        if (!dev.Initialized) { dev.Dispose(); return null; }
        var f = new Found();
        if (dev.Zones.Count > 0) f.Lights.Add(dev);
        // fans, or a poll() that reports sensors only (temperature probes, flow meters, read-only AIOs)
        if (dev.Channels.Count > 0 || dev.HasPoll) f.Fans.Add(dev);
        return f;
    }
}
