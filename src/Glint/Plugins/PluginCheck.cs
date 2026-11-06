using Jint;
using System.Text.Json;
using Glint.Core;
using Glint.Devices;

namespace Glint.Plugins;

/// <summary>
/// Runs the `tests` a plugin declares against a simulated device: proof that a plugin speaks its protocol
/// correctly without owning the hardware. Format (see PLUGINS.md):
///   tests: [{ name, device: { productId, reportSize, inputSize, interface }, replies: [{ when: 'hex', reply: 'hex' }],
///             colors: 'ramp' | ['#rrggbb', ...], expectZones: n, expectInit: ['hex'...], expectFrame: ['hex'...],
///             bus: { '0x19': { '0x43': 26 } }, expectBusWrites: ['19:31#0C...'] }]
/// Hex values are prefixes of the full report as written (report id first); spaces are ignored.
/// </summary>
public static class PluginCheck
{
    public sealed record Result(string Plugin, string Test, bool Pass, string Detail);

    static string Hex(byte[] b) => Convert.ToHexString(b);
    static string Norm(string h) => h.Replace(" ", "").Replace("-", "").ToUpperInvariant();

    public static List<Result> Run(string file)
    {
        var results = new List<Result>();
        var code = PluginHost.ReadCode(file);
        string name = Path.GetFileName(file);
        JsonElement desc, tests;
        try
        {
            desc = JintHost.Describe(code, file);
            var e = JintHost.Boot(code, file, 2000);
            tests = JsonDocument.Parse(e.Evaluate("JSON.stringify(module.exports.tests || [])").AsString()).RootElement;
        }
        catch (Exception ex) { results.Add(new(name, "loads", false, JintHost.Clean(ex))); return results; }
        string type = desc.GetProperty("type").GetString() ?? "effect";
        if (type == "effect") return results;
        if (tests.GetArrayLength() == 0) { results.Add(new(name, "has tests", false, "no tests declared")); return results; }
        var def = new DevicePluginDef(file, code, desc.TryGetProperty("name", out var n) ? n.GetString() ?? name : name);
        foreach (var t in tests.EnumerateArray())
        {
            string tname = t.TryGetProperty("name", out var tn) ? tn.GetString() ?? "test" : "test";
            try { results.Add(RunOne(name, tname, def, desc, type, t)); }
            catch (Exception ex) { results.Add(new(name, tname, false, ex.Message)); }
        }
        return results;
    }

    static Result RunOne(string plugin, string tname, DevicePluginDef def, JsonElement desc, string type, JsonElement t)
    {
        var logs = new List<string>();
        Found? f;
        SelfTest.FakePort? port = null; SelfTest.FakeSmbus? bus = null;
        if (type == "smbus")
        {
            bus = new SelfTest.FakeSmbus();
            if (t.TryGetProperty("bus", out var b))
                foreach (var dev in b.EnumerateObject())
                {
                    var regs = new Dictionary<byte, int>();
                    foreach (var r in dev.Value.EnumerateObject()) regs[Convert.ToByte(r.Name, 16)] = r.Value.GetInt32();
                    bus.Regs[Convert.ToByte(dev.Name, 16)] = regs;
                }
            if (t.TryGetProperty("busMem", out var bm))
                foreach (var dev in bm.EnumerateObject())
                {
                    var mem = new Dictionary<int, int>();
                    foreach (var r in dev.Value.EnumerateObject())
                    {
                        int at = Convert.ToInt32(r.Name, 16);
                        var vals = r.Value.ValueKind == JsonValueKind.String ? r.Value.GetString()!.Select(ch => (int)ch) : r.Value.EnumerateArray().Select(x => x.GetInt32());
                        foreach (var v in vals) mem[at++] = v;
                    }
                    bus.Mem[Convert.ToByte(dev.Name, 16)] = mem;
                }
            if (t.TryGetProperty("busInfo", out var bi)) bus.Info = Convert.FromHexString(Norm(bi.GetString()!));
            // device-info buffers report their CRC-8 at 0x42 like the real modules
            byte crc = 0; foreach (var x in bus.Info) { crc ^= x; for (int k = 0; k < 8; k++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1); }
            foreach (var regs in bus.Regs.Values) regs.TryAdd(0x42, crc);
            var addrs = desc.GetProperty("addresses").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            f = def.OpenSmbusWith(bus, addrs, x => x, (z, d) => d, logs.Add);
        }
        else
        {
            var d = t.TryGetProperty("device", out var dv) ? dv : default;
            int Get(string k, int fallback) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(k, out var v) ? v.GetInt32() : fallback;
            int pid = Get("productId", desc.GetProperty("productIds").EnumerateArray().First().GetInt32());
            int report = Get("reportSize", desc.TryGetProperty("reportSize", out var rs) && rs.ValueKind == JsonValueKind.Number ? rs.GetInt32() : 65);
            var replies = t.TryGetProperty("replies", out var rp)
                ? rp.EnumerateArray().Select(x => (when: Norm(x.GetProperty("when").GetString()!), reply: Convert.FromHexString(Norm(x.GetProperty("reply").GetString()!)))).ToList()
                : new();
            port = new SelfTest.FakePort(desc.GetProperty("vendorId").GetInt32(), pid, report,
                w => { var h = Hex(w); foreach (var r in replies) if (h.StartsWith(r.when)) return r.reply.Length < Get("inputSize", report) ? r.reply.Concat(new byte[Get("inputSize", report) - r.reply.Length]).ToArray() : r.reply; return null; },
                Get("interface", -1));
            f = def.OpenWith(port, x => x, (z, dflt) => dflt, logs.Add);
        }
        bool wantDecline = t.TryGetProperty("expectDeclined", out var xd) && xd.GetBoolean();
        if (f == null) return new(plugin, tname, wantDecline, wantDecline ? "declined as expected" : "init declined/failed: " + string.Join(" | ", logs));
        if (wantDecline) return new(plugin, tname, false, "expected the plugin to decline this device");
        var light = f.Lights.FirstOrDefault();
        int leds = light?.Zones.Sum(z => z.Leds) ?? 0;
        var problems = new List<string>();
        if (t.TryGetProperty("expectZones", out var ez) && ez.GetInt32() != leds) problems.Add($"expected {ez.GetInt32()} LEDs, got {leds}");
        int initWrites = port?.Writes.Count ?? bus!.Writes.Count;
        if (t.TryGetProperty("expectInit", out var ei))
        {
            var got = (port?.Writes.Take(initWrites).Select(Hex) ?? bus!.Writes).ToList();
            foreach (var want in ei.EnumerateArray().Select(x => Norm(x.GetString()!)))
                if (!got.Any(g => g.StartsWith(want, StringComparison.OrdinalIgnoreCase))) problems.Add($"init never sent {want}");
        }
        if (light != null)
        {
            var colors = new Rgb[leds];
            if (t.TryGetProperty("colors", out var cs) && cs.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < leds; i++) colors[i] = Rgb.Parse(cs[i % cs.GetArrayLength()].GetString()!);
            else for (int i = 0; i < leds; i++) colors[i] = new Rgb((byte)(i * 3), (byte)(i * 3 + 1), (byte)(i * 3 + 2)); // "ramp"
            light.Write(colors);
            var frame = port != null ? port.Writes.Skip(initWrites).Select(Hex).ToList() : bus!.Writes.Skip(initWrites).ToList();
            if (t.TryGetProperty("expectFrame", out var ef))
            {
                var want = ef.EnumerateArray().Select(x => Norm(x.GetString()!)).ToList();
                for (int i = 0; i < want.Count; i++)
                    if (i >= frame.Count || !frame[i].StartsWith(want[i], StringComparison.OrdinalIgnoreCase)) { problems.Add($"frame packet {i}: expected {want[i]}…, got {(i < frame.Count ? frame[i][..Math.Min(frame[i].Length, want[i].Length + 8)] : "nothing")}"); break; }
            }
            if (bus != null && bus.Writes.Any(w => w.Length > 2 && w[0] == '5' && w[1] is >= '0' and <= '7' && w[2] == ':')) problems.Add("wrote to the SPD EEPROM range!");
            if (t.TryGetProperty("expectBusWrites", out var eb))
                foreach (var want in eb.EnumerateArray().Select(x => x.GetString()!))
                    if (!frame.Any(g => g.StartsWith(want, StringComparison.OrdinalIgnoreCase))) problems.Add($"frame never wrote {want}");
        }
        (light as IDisposable ?? f.Fans.FirstOrDefault() as IDisposable)?.Dispose();
        return new(plugin, tname, problems.Count == 0, problems.Count == 0 ? $"{leds} LEDs ok" : string.Join("; ", problems));
    }
}
