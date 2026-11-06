using System.Text.Json;
using Glint.Devices;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Glint.Plugins;

/// <summary>
/// Device plugins run on Jint (pure .NET interpreter): ~1-3 MB per device instead of ~19 MB for a V8 isolate,
/// and each device keeps its own engine so slow USB waits never block another device. Device code is
/// I/O-bound (a few hundred µs of script per frame), so an interpreter is plenty. Effect plugins stay on V8.
/// </summary>
internal static class JintHost
{
    public static Engine NewEngine(int timeoutMs) => new(o => o
        .Strict()
        .LimitMemory(32_000_000)
        .TimeoutInterval(TimeSpan.FromMilliseconds(timeoutMs))
        .LimitRecursion(256)
        .CatchClrExceptions());

    public static Engine Boot(string code, string file, int timeoutMs)
    {
        var e = NewEngine(timeoutMs);
        e.Execute(PluginHost.Prelude);
        e.Execute(code, Path.GetFileName(file));
        return e;
    }

    /// <summary>Run a plugin file once (sandboxed) and project its metadata to JSON. No V8 involved.</summary>
    public static JsonElement Describe(string code, string file)
    {
        var e = Boot(code, file, 2000);
        var json = e.Evaluate("""
            (function (m) {
              var num = function (v) { return typeof v === 'number' ? v : undefined; };
              return JSON.stringify({
                type: m.type || 'effect', name: m.name, description: m.description, params: m.params || {}, preview: m.preview || [],
                hasRender: typeof m.render === 'function', vendorId: num(m.vendorId), productIds: m.productIds || [],
                interface: num(m.interface), usagePage: num(m.usagePage), reportSize: num(m.reportSize),
                featureReports: !!m.featureReports, addresses: m.addresses || []
              });
            })(module.exports)
            """).AsString();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public static string Clean(Exception e) => e switch
    {
        Jint.Runtime.JavaScriptException js => js.Error.ToString() + (js.Location.Start.Line > 0 ? $" (line {js.Location.Start.Line})" : ""),
        _ when e.GetType().Name.Contains("Timeout") => "Took too long (timed out)",
        _ when e.GetType().Name.Contains("Parse") => "Syntax error: " + e.Message,
        _ => e.InnerException != null ? Clean(e.InnerException) : e.Message,
    };
}

/// <summary>byte[] ⇄ Uint8Array for one engine.</summary>
internal sealed class JintBytes(Engine e)
{
    readonly JsValue _from = e.Evaluate("(function (a) { return a instanceof Uint8Array ? a : Uint8Array.from(a, function (x) { return x & 255; }); })");
    public JsValue ToJs(byte[] b) => e.Invoke(_from, JsValue.FromObject(e, Array.ConvertAll(b, x => (double)x)));
    public JsValue ToJs(ReadOnlySpan<byte> b) => ToJs(b.ToArray());
    public static byte[] FromJs(JsValue v)
    {
        switch (v.ToObject())
        {
            case byte[] b: return b;
            case object[] arr: return Array.ConvertAll(arr, x => (byte)(Convert.ToInt32(x) & 0xFF));
            case double[] d: return Array.ConvertAll(d, x => (byte)((int)x & 0xFF));
        }
        if (v is ObjectInstance o && o.Get("length").IsNumber())
        {
            int n = (int)o.Get("length").AsNumber();
            var r = new byte[n];
            for (int i = 0; i < n; i++) r[i] = (byte)((int)o.Get(i).AsNumber() & 0xFF);
            return r;
        }
        throw new ArgumentException("expected an array of bytes");
    }
}

/// <summary>Common API surface for Jint device plugins.</summary>
public abstract class JintApiBase(string name, Action<string> log, Func<string, int, int> leds)
{
    internal string Id = "";
    public void log(string msg) => log_($"[{name}] {msg}");
    readonly Action<string> log_ = log;
    public void sleep(int ms) => Thread.Sleep(Math.Clamp(ms, 0, 1000));
    public double now() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public int ledCount(string zone, int fallback) => leds($"{Id}:{zone}", fallback);
}

public sealed class JintHidApi : JintApiBase
{
    readonly IHidPort _p; readonly JintBytes _b;
    internal JintHidApi(JintBytes b, IHidPort p, string name, Action<string> log, Func<string, int, int> leds) : base(name, log, leds) { _b = b; _p = p; }
    public int vendorId => _p.VendorId;
    public int productId => _p.ProductId;
    public string product => _p.Product;
    public string serial => _p.Serial;
    public int interfaceNumber => _p.Interface;
    public int reportSize => _p.OutputReportLength;
    public int inputSize => _p.InputReportLength;
    public int featureSize => _p.FeatureReportLength;
    public void write(JsValue data) => _p.Write(JintBytes.FromJs(data));
    public JsValue read(int timeoutMs = 100) { var r = _p.Read(timeoutMs); return r == null ? JsValue.Null : _b.ToJs(r); }
    public void sendFeature(JsValue data) => _p.SendFeature(JintBytes.FromJs(data));
    public JsValue getFeature(int reportId) => _b.ToJs(_p.GetFeature((byte)reportId));
}

/// <summary>SMBus API with Glint-enforced safety (same rules as everywhere: declared addresses, never SPD, identify before write).</summary>
public sealed class JintSmbusApi : JintApiBase
{
    readonly ISmbusPort _bus; readonly HashSet<int> _allowed; readonly HashSet<int> _seen = new();
    internal JintSmbusApi(ISmbusPort bus, IEnumerable<int> allowed, string name, Action<string> log, Func<string, int, int> leds) : base(name, log, leds)
    { _bus = bus; _allowed = allowed.ToHashSet(); }
    void Check(int addr, bool write)
    {
        if (addr is >= 0x50 and <= 0x57) throw new InvalidOperationException($"SMBus 0x{addr:x2} is the SPD EEPROM: always blocked");
        if (!_allowed.Contains(addr)) throw new InvalidOperationException($"SMBus 0x{addr:x2} is not in this plugin's declared addresses");
        if (write && !_seen.Contains(addr)) throw new InvalidOperationException($"SMBus 0x{addr:x2}: read/identify the device before writing to it");
    }
    public int readByte(int addr, int reg) { Check(addr, false); int v = _bus.ReadByte((byte)addr, (byte)reg); if (v >= 0) _seen.Add(addr); return v; }
    public bool writeByte(int addr, int reg, int value) { Check(addr, true); return _bus.WriteByte((byte)addr, (byte)reg, (byte)value); }
    public int readWord(int addr, int reg) { Check(addr, false); int v = _bus.ReadWord((byte)addr, (byte)reg); if (v >= 0) _seen.Add(addr); return v; }
    public bool writeWord(int addr, int reg, int value) { Check(addr, true); return _bus.WriteWord((byte)addr, (byte)reg, (ushort)value); }
    public bool writeBlock(int addr, int reg, JsValue data) { Check(addr, true); return _bus.WriteBlock((byte)addr, (byte)reg, JintBytes.FromJs(data)); }
}

/// <summary>A device plugin instance on Jint: RGB zones and/or fan channels / sensors.</summary>
public sealed class JintPluginDevice : ILightDevice, IFanController
{
    readonly Engine _e;
    readonly ObjectInstance _exp;
    readonly JintApiBase _api;
    readonly JsValue _dev;
    readonly JintBytes _bytes;
    readonly IDisposable? _res;
    readonly object _lock = new();
    readonly Action<string> _log;
    int _gap;
    public string Id { get; }
    public string Name { get; }
    public bool Initialized { get; }
    public bool HasPoll => _exp.Get("poll").IsObject();
    public IReadOnlyList<LightZone> Zones { get; } = new List<LightZone>();
    public IReadOnlyList<FanChannel> Channels { get; } = new List<FanChannel>();
    public int MinGapMs => _gap;
    public double HeapMB => 0;

    internal JintPluginDevice(string file, string code, string name, Func<JintBytes, JintApiBase> makeApi, IDisposable? res, Func<string, string> unique, Action<string> log)
    {
        Name = name; _res = res; _log = log;
        _e = JintHost.Boot(code, file, 5000);
        _exp = _e.Evaluate("module.exports").AsObject();
        _bytes = new JintBytes(_e);
        _api = makeApi(_bytes);
        _dev = JsValue.FromObject(_e, _api);
        JsValue Val(string k) { var v = _exp.Get(k); return v is Jint.Native.Function.Function f ? _e.Invoke(f, _exp, new object[] { _dev }) : v; }
        var idv = Val("id");
        Id = _api.Id = unique(idv.IsString() ? idv.AsString() : Slug(name));
        _gap = Num(Val("minGapMs"));
        try
        {
            var r = _exp.Get("init").IsObject() ? _e.Invoke(_exp.Get("init"), _exp, new object[] { _dev }) : JsValue.Undefined;
            if (r.IsBoolean() && !r.AsBoolean()) { log($"[{Name}] init declined (device not recognised)"); return; }
            var spec = r.IsObject() ? r.AsObject() : _exp;
            if (r.IsObject() && !spec.Get("minGapMs").IsUndefined()) _gap = Num(spec.Get("minGapMs"));
            var json = _e.Invoke(_e.Evaluate("(function(s){return JSON.stringify({zones:s.zones||[],fans:s.fans||[]});})"), spec).AsString();
            var doc = JsonDocument.Parse(json).RootElement;
            string S(JsonElement o, string k, string d) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : d;
            int I(JsonElement o, string k, int d) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
            Zones = doc.GetProperty("zones").EnumerateArray().Select(z =>
            {
                string zid = S(z, "id", "zone");
                var kind = Enum.TryParse<ZoneKind>(S(z, "kind", "strip"), true, out var k) ? k : ZoneKind.Strip;
                return new LightZone($"{Id}:{zid}", S(z, "name", zid), I(z, "leds", 1), kind, I(z, "cols", 0), S(z, "category", ""));
            }).ToList();
            Channels = doc.GetProperty("fans").EnumerateArray().Select(f => new FanChannel(S(f, "id", "fan"), S(f, "name", S(f, "id", "fan")),
                f.TryGetProperty("pump", out var p) && p.ValueKind == JsonValueKind.True)).ToList();
            Initialized = true;
        }
        catch (Exception e) { log($"[{Name}] init failed: {JintHost.Clean(e)}"); }
    }

    static string Slug(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(12).ToArray()) is { Length: > 0 } x ? x : "plugin";
    static int Num(JsValue v) => v.IsNumber() ? (int)v.AsNumber() : 0;

    byte[] _buf = Array.Empty<byte>();
    public void Write(ReadOnlySpan<Rgb> colors)
    {
        if (!_exp.Get("render").IsObject()) return;
        lock (_lock)
        {
            if (_buf.Length != colors.Length * 3) _buf = new byte[colors.Length * 3];
            for (int i = 0; i < colors.Length; i++) { _buf[i * 3] = colors[i].R; _buf[i * 3 + 1] = colors[i].G; _buf[i * 3 + 2] = colors[i].B; }
            try { _e.Invoke(_exp.Get("render"), _exp, new object[] { _dev, _bytes.ToJs(_buf) }); }
            catch (Exception e) { throw new IOException(JintHost.Clean(e)); }
        }
    }

    public void Poll(Dictionary<string, double> temps, Dictionary<string, int> rpm)
    {
        if (!HasPoll) return;
        lock (_lock)
        {
            var r = _e.Invoke(_exp.Get("poll"), _exp, new object[] { _dev });
            if (!r.IsObject()) return;
            var json = _e.Invoke(_e.Evaluate("(function(r){return JSON.stringify({rpm:r.rpm||{},temps:r.temps||{}});})"), r).AsString();
            var doc = JsonDocument.Parse(json).RootElement;
            foreach (var p in doc.GetProperty("rpm").EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) rpm[p.Name] = (int)p.Value.GetDouble();
            foreach (var p in doc.GetProperty("temps").EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) temps[p.Name] = p.Value.GetDouble();
        }
    }

    public void SetDuty(IReadOnlyDictionary<string, int> duty)
    {
        if (!_exp.Get("setDuty").IsObject()) return;
        lock (_lock) _e.Invoke(_exp.Get("setDuty"), _exp, new object[] { _dev, _e.Evaluate("(" + JsonSerializer.Serialize(duty) + ")") });
    }

    bool _released, _disposed;
    public void Release()
    {
        if (_released || !Initialized || !_exp.Get("shutdown").IsObject()) return;
        _released = true;
        lock (_lock) { try { _e.Invoke(_exp.Get("shutdown"), _exp, new object[] { _dev }); } catch (Exception e) { _log($"[{Name}] shutdown: {JintHost.Clean(e)}"); } }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (!_released) Release();
        _res?.Dispose();
    }
}
