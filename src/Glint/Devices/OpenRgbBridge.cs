using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Glint.Devices;

/// <summary>
/// Minimal client for the OpenRGB SDK protocol (the "SDK Server" in OpenRGB, TCP 6742 by default).
/// Ported from OpenRGB's NetworkProtocol.h / NetworkServer.cpp / RGBController.cpp (GetDeviceDescriptionData,
/// GetModeDescriptionData, GetZoneDescriptionData, GetSegmentDescriptionData, SetColorDescription).
/// Only what Glint needs: version negotiation, controller count + data, SETCUSTOMMODE and UPDATELEDS.
/// Every packet: "ORGB" magic, uint32 device index, uint32 packet id, uint32 data size (little-endian), then data.
/// </summary>
public static class OpenRgbProtocol
{
    public const uint RequestControllerCount = 0, RequestControllerData = 1, RequestProtocolVersion = 40, SetClientName = 50,
        DeviceListUpdated = 100, UpdateLeds = 1050, SetCustomMode = 1100;
    /// <summary>Highest protocol this client parses. v4 = OpenRGB 0.9 (zone segments); v5+ add fields we don't read.</summary>
    public const uint MaxVersion = 4;
    public const int HeaderSize = 16;
    /// <summary>Sanity limits for what a server may claim (OpenRGB itself uses 16-bit LED counts).</summary>
    public const int MaxLeds = 65535, MaxControllers = 256;
    static readonly byte[] Magic = "ORGB"u8.ToArray();

    public static byte[] Packet(uint dev, uint id, ReadOnlySpan<byte> data)
    {
        var b = new byte[HeaderSize + data.Length];
        Magic.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), dev);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)data.Length);
        data.CopyTo(b.AsSpan(HeaderSize));
        return b;
    }

    public static bool IsHeader(ReadOnlySpan<byte> h) => h.Length >= HeaderSize && h[..4].SequenceEqual(Magic);

    /// <summary>UPDATELEDS: uint32 data size, uint16 colour count, then R,G,B,0 per LED (RGBColor = 0x00BBGGRR).</summary>
    public static byte[] UpdateLedsPacket(uint dev, ReadOnlySpan<Rgb> colors)
    {
        int size = 4 + 2 + 4 * colors.Length;
        var d = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(4), (ushort)colors.Length);
        for (int i = 0; i < colors.Length; i++) { d[6 + i * 4] = colors[i].R; d[7 + i * 4] = colors[i].G; d[8 + i * 4] = colors[i].B; }
        return Packet(dev, UpdateLeds, d);
    }

    public static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    /// <summary>Parse a REQUEST_CONTROLLER_DATA reply (starting with its uint32 size) for the negotiated protocol version.</summary>
    public static OpenRgbController ParseController(uint index, ReadOnlySpan<byte> data, uint proto)
    {
        var r = new Reader(data);
        r.U32(); // total size
        int type = r.I32();
        string name = r.Str();
        string vendor = proto >= 1 ? r.Str() : "";
        string description = r.Str(), version = r.Str(), serial = r.Str(), location = r.Str();
        int modes = r.U16();
        r.I32(); // active mode
        for (int m = 0; m < modes; m++)
        {
            r.Str(); r.I32(); r.U32(); r.U32(); r.U32();          // name, value, flags, speed min/max
            if (proto >= 3) { r.U32(); r.U32(); }                  // brightness min/max
            r.U32(); r.U32(); r.U32();                             // colors min/max, speed
            if (proto >= 3) r.U32();                               // brightness
            r.U32(); r.U32();                                      // direction, color mode
            int nc = r.U16(); r.Skip(nc * 4);                      // mode colours
        }
        int nz = r.U16();
        var zones = new List<OpenRgbZone>();
        long total = 0;
        for (int z = 0; z < nz; z++)
        {
            string zn = r.Str();
            int zt = r.I32();
            r.U32(); r.U32();                                      // leds min/max
            // the server is another program (possibly on the LAN): its sizes must not make us allocate without bound
            uint count32 = r.U32();
            if (count32 > MaxLeds) throw new InvalidDataException($"OpenRGB zone \"{zn}\" claims {count32} LEDs");
            int count = (int)count32;
            if ((total += count) > MaxLeds) throw new InvalidDataException("OpenRGB device claims more than 65535 LEDs");
            int mlen = r.U16();
            int h = 0, w = 0; uint[]? map = null;
            if (mlen > 0)
            {
                uint h32 = r.U32(), w32 = r.U32();
                if ((ulong)h32 * w32 > MaxLeds || (ulong)h32 * w32 * 4 > (ulong)r.Remaining) throw new InvalidDataException($"OpenRGB zone \"{zn}\" has a bad matrix size {w32}x{h32}");
                h = (int)h32; w = (int)w32;
                if ((total += (long)h * w) > 2L * MaxLeds) throw new InvalidDataException("OpenRGB device claims too many matrix cells");
                map = new uint[h * w];
                for (int i = 0; i < map.Length; i++) map[i] = r.U32();
            }
            if (proto >= 4)
            {
                int ns = r.U16();
                for (int s = 0; s < ns; s++) { r.Str(); r.I32(); r.U32(); r.U32(); }
            }
            zones.Add(new OpenRgbZone(zn, zt, count, w, h, map));
        }
        int leds = r.U16();
        return new OpenRgbController(index, type, name, vendor, description, version, serial, location, zones, leds);
    }

    ref struct Reader(ReadOnlySpan<byte> d)
    {
        readonly ReadOnlySpan<byte> _d = d;
        int _p;
        ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _p + n > _d.Length) throw new InvalidDataException($"OpenRGB controller data truncated at byte {_p} (+{n} of {_d.Length})");
            var s = _d.Slice(_p, n); _p += n; return s;
        }
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public int U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public void Skip(int n) => Take(n);
        public int Remaining => _d.Length - _p;
        public string Str() { int n = U16(); var s = Take(n); return Encoding.UTF8.GetString(s.Length > 0 && s[^1] == 0 ? s[..^1] : s); }
    }
}

/// <summary>An OpenRGB zone. Type 0 single, 1 linear, 2 matrix. Map (matrix only): cell → LED index in the zone, 0xFFFFFFFF = none.</summary>
public sealed record OpenRgbZone(string Name, int Type, int Leds, int Width, int Height, uint[]? Map);
/// <summary>An OpenRGB controller (device). Type follows OpenRGB's device_type (1 = DRAM, 5 = keyboard, 6 = mouse, ...).</summary>
public sealed record OpenRgbController(uint Index, int Type, string Name, string Vendor, string Description, string Version, string Serial, string Location, List<OpenRgbZone> Zones, int Leds);

/// <summary>One TCP connection to an OpenRGB SDK server. Requests and LED writes are serialised by a lock.</summary>
public sealed class OpenRgbClient : IDisposable
{
    readonly TcpClient _tcp;
    readonly NetworkStream _s;
    readonly object _lock = new();
    public uint Protocol { get; private set; }
    public string Server { get; }

    OpenRgbClient(TcpClient tcp, string server) { _tcp = tcp; _s = tcp.GetStream(); Server = server; _tcp.NoDelay = true; }

    /// <summary>Connect, negotiate the protocol version and send the client name. Throws if the server isn't there.</summary>
    public static OpenRgbClient Connect(string host, int port, int timeoutMs = 1000)
    {
        var tcp = new TcpClient();
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(timeoutMs)) throw new TimeoutException($"no answer from {host}:{port}");
        }
        catch (AggregateException e) { tcp.Dispose(); throw e.InnerException ?? e; }
        catch { tcp.Dispose(); throw; }
        var c = new OpenRgbClient(tcp, $"{host}:{port}");
        try
        {
            c._s.ReadTimeout = 2000; c._s.WriteTimeout = 2000;
            // Servers older than protocol 1 never answer this request: wait briefly, then assume version 0.
            c.Send(0, OpenRgbProtocol.RequestProtocolVersion, OpenRgbProtocol.U32(OpenRgbProtocol.MaxVersion));
            var v = c.Receive(OpenRgbProtocol.RequestProtocolVersion, 600);
            c.Protocol = v is { Length: >= 4 } ? Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(v), OpenRgbProtocol.MaxVersion) : 0;
            c.Send(0, OpenRgbProtocol.SetClientName, "Glint\0"u8);
            return c;
        }
        catch { c.Dispose(); throw; }
    }

    public int ControllerCount()
    {
        lock (_lock)
        {
            Send(0, OpenRgbProtocol.RequestControllerCount, ReadOnlySpan<byte>.Empty);
            var r = Receive(OpenRgbProtocol.RequestControllerCount, 2000) ?? throw new IOException("OpenRGB did not answer the controller count");
            if (r.Length < 4) throw new InvalidDataException("OpenRGB sent a short controller count");
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(r);
            if (n > OpenRgbProtocol.MaxControllers) throw new InvalidDataException($"OpenRGB claims {n} controllers");
            return (int)n;
        }
    }

    public OpenRgbController Controller(uint index)
    {
        lock (_lock)
        {
            Send(index, OpenRgbProtocol.RequestControllerData, Protocol >= 1 ? OpenRgbProtocol.U32(Protocol) : ReadOnlySpan<byte>.Empty);
            var r = Receive(OpenRgbProtocol.RequestControllerData, 3000) ?? throw new IOException($"OpenRGB did not send controller {index}");
            return OpenRgbProtocol.ParseController(index, r, Protocol);
        }
    }

    public void SetCustomMode(uint index) { lock (_lock) Send(index, OpenRgbProtocol.SetCustomMode, ReadOnlySpan<byte>.Empty); }

    public void UpdateLeds(uint index, ReadOnlySpan<Rgb> colors)
    {
        var pkt = OpenRgbProtocol.UpdateLedsPacket(index, colors);
        lock (_lock)
        {
            Drain();
            _s.Write(pkt);
        }
    }

    void Send(uint dev, uint id, ReadOnlySpan<byte> data) => _s.Write(OpenRgbProtocol.Packet(dev, id, data));

    /// <summary>Notifications (device list updated, ...) can arrive at any time; LED writes never read, so drop them here.</summary>
    void Drain()
    {
        var junk = new byte[4096];
        while (_tcp.Available > 0) _s.Read(junk, 0, Math.Min(junk.Length, _tcp.Available));
    }

    /// <summary>Read packets until one with <paramref name="id"/> arrives (others are skipped). Null on timeout.</summary>
    byte[]? Receive(uint id, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var h = new byte[OpenRgbProtocol.HeaderSize];
        while (true)
        {
            int left = (int)(deadline - Environment.TickCount64);
            if (left <= 0) return null;
            _s.ReadTimeout = left;
            try { ReadExact(h); }
            catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }) { return null; }
            if (!OpenRgbProtocol.IsHeader(h)) throw new InvalidDataException("not an OpenRGB SDK server (bad magic)");
            uint pid = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12));
            if (size > 16 * 1024 * 1024) throw new InvalidDataException("OpenRGB packet too large");
            var data = new byte[size];
            ReadExact(data);
            if (pid == id) return data;
        }
    }

    void ReadExact(byte[] b)
    {
        int got = 0;
        while (got < b.Length)
        {
            int n = _s.Read(b, got, b.Length - got);
            if (n == 0) throw new IOException("OpenRGB closed the connection");
            got += n;
        }
    }

    public void Dispose() { try { _s.Dispose(); } catch { } _tcp.Dispose(); }
}

/// <summary>An OpenRGB device as a Glint lighting device. Glint's frame is copied into OpenRGB's LED order and sent with UPDATELEDS.</summary>
public sealed class OpenRgbDevice : ILightDevice
{
    readonly OpenRgbClient _client;
    readonly OpenRgbController _c;
    readonly List<(int glintStart, int count, int orgbStart, uint[]? map)> _segs = new();
    readonly Rgb[] _out;
    bool _custom;
    public string Id { get; }
    public string Name => _c.Name;
    public IReadOnlyList<LightZone> Zones { get; }
    /// <summary>OpenRGB forwards frames to the hardware itself; ~30 fps is plenty and keeps its queue short.</summary>
    public int MinGapMs => 33;
    public OpenRgbController Controller => _c;

    public OpenRgbDevice(OpenRgbClient client, OpenRgbController c, string id)
    {
        _client = client; _c = c; Id = id;
        string cat = c.Type switch { 5 => "keyboard", 6 => "mouse", 7 => "mousepad", 8 or 9 => "headset", 4 => "strip", _ => "" };
        var zones = new List<LightZone>();
        var used = new HashSet<string>();
        int glint = 0, orgb = 0;
        for (int i = 0; i < c.Zones.Count; i++)
        {
            var z = c.Zones[i];
            int start = orgb; orgb += z.Leds;
            if (z.Leds <= 0) continue;
            string zid = Slug(z.Name); if (zid.Length == 0) zid = "zone" + (i + 1);
            for (int k = 2; !used.Add(zid); k++) zid = Slug(z.Name) + k;
            bool matrix = z.Type == 2 && z.Map != null && z.Width > 0 && z.Height > 0 && z.Map.Length == z.Width * z.Height;
            var kind = matrix ? ZoneKind.Matrix : z.Type == 0 || z.Leds == 1 ? ZoneKind.Single : ZoneKind.Strip;
            int n = matrix ? z.Width * z.Height : z.Leds;
            zones.Add(new LightZone($"{id}:{zid}", z.Name, n, kind, matrix ? z.Width : 0, cat));
            _segs.Add((glint, n, start, matrix ? z.Map : null));
            glint += n;
        }
        Zones = zones;
        _out = new Rgb[Math.Max(c.Leds, orgb)];
    }

    /// <summary>The OpenRGB colour array for one Glint frame (matrix cells mapped to their LEDs). Exposed for tests.</summary>
    public Rgb[] Map(ReadOnlySpan<Rgb> colors)
    {
        foreach (var (g, n, o, map) in _segs)
        {
            if (map == null) { colors.Slice(g, Math.Min(n, colors.Length - g)).CopyTo(_out.AsSpan(o)); continue; }
            for (int cell = 0; cell < n && g + cell < colors.Length; cell++)
                if (map[cell] != 0xFFFFFFFF && o + map[cell] < _out.Length) _out[o + map[cell]] = colors[g + cell];
        }
        return _out;
    }

    public void Write(ReadOnlySpan<Rgb> colors)
    {
        if (!_custom) { _client.SetCustomMode(_c.Index); _custom = true; } // "direct" mode, so OpenRGB doesn't run its own effect
        _client.UpdateLeds(_c.Index, Map(colors));
    }

    static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    public void Dispose() { }
}

/// <summary>Bridge state for the Setup check page / setup.check.</summary>
public sealed record OpenRgbStatus(bool Enabled, bool Connected, string Server, uint Protocol, List<string> Devices, List<string> Skipped, string? Error);

/// <summary>
/// Connects once at startup (only when enabled) and turns the server's devices into Glint lighting devices, skipping
/// anything Glint already drives natively (matched by USB VID/PID from OpenRGB's "HID: ..." location, else by name)
/// and anything the user excluded, so two drivers never fight over one device.
/// </summary>
public sealed class OpenRgbBridge : IDisposable
{
    readonly OpenRgbClient? _client;
    public List<OpenRgbDevice> Devices { get; } = new();
    public OpenRgbStatus Status { get; private set; }

    OpenRgbBridge(OpenRgbClient? client, OpenRgbStatus status) { _client = client; Status = status; }

    /// <param name="native">Lighting devices Glint drives itself.</param>
    /// <param name="nativeVidPids">USB VID/PID pairs claimed by Glint's drivers.</param>
    public static OpenRgbBridge Connect(Core.OpenRgbConfig cfg, IReadOnlyList<ILightDevice> native, ISet<(int vid, int pid)> nativeVidPids, Func<string, string> unique, Action<string> log)
    {
        string server = $"{cfg.Host}:{cfg.Port}";
        OpenRgbClient client;
        try { client = OpenRgbClient.Connect(cfg.Host, cfg.Port); }
        catch (Exception e)
        {
            log($"OpenRGB bridge: can't reach the SDK server at {server} ({Short(e)})");
            return new OpenRgbBridge(null, new OpenRgbStatus(true, false, server, 0, new(), new(), Short(e)));
        }
        var bridge = new OpenRgbBridge(client, new OpenRgbStatus(true, true, server, client.Protocol, new(), new(), null));
        try
        {
            int n = client.ControllerCount();
            for (uint i = 0; i < n; i++)
            {
                var c = client.Controller(i);
                if (SkipReason(c, cfg, native, nativeVidPids) is string why) { bridge.Status.Skipped.Add($"{c.Name}: {why}"); continue; }
                var dev = new OpenRgbDevice(client, c, unique(StableId(c)));
                if (dev.Zones.Count == 0) { bridge.Status.Skipped.Add($"{c.Name}: no LEDs"); continue; }
                bridge.Devices.Add(dev);
                bridge.Status.Devices.Add($"{dev.Id} {c.Name}");
            }
            log($"OpenRGB bridge: {server} protocol {client.Protocol}, {bridge.Devices.Count} device(s){(bridge.Status.Skipped.Count > 0 ? $", skipped {string.Join("; ", bridge.Status.Skipped)}" : "")}");
        }
        catch (Exception e)
        {
            log($"OpenRGB bridge: listing devices failed ({Short(e)})");
            bridge.Status = bridge.Status with { Error = Short(e) };
        }
        return bridge;
    }

    /// <summary>"orgb-" + 6 hex digits of a hash of name + location: stable across restarts and OpenRGB's device order.</summary>
    public static string StableId(OpenRgbController c)
    {
        uint h = 2166136261;
        foreach (var ch in c.Name + "|" + c.Location) { h ^= ch; h *= 16777619; }
        return $"orgb-{h & 0xFFFFFF:x6}";
    }

    static readonly Regex VidPid = new(@"vid_([0-9a-f]{4}).*?pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);

    /// <summary>Why this OpenRGB device must not be driven by the bridge, or null.</summary>
    public static string? SkipReason(OpenRgbController c, Core.OpenRgbConfig cfg, IReadOnlyList<ILightDevice> native, ISet<(int vid, int pid)> nativeVidPids)
    {
        string id = StableId(c);
        if (cfg.Exclude.Any(x => x.Equals(id, StringComparison.OrdinalIgnoreCase) || c.Name.Contains(x.Trim(), StringComparison.OrdinalIgnoreCase))) return "excluded in settings";
        if (c.Location.StartsWith("HID", StringComparison.OrdinalIgnoreCase) && VidPid.Match(c.Location) is { Success: true } m)
        {
            var key = (Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16));
            return nativeVidPids.Contains(key) ? $"Glint drives this USB device ({key.Item1:x4}:{key.Item2:x4}) itself" : null;
        }
        static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");
        var n = Norm(c.Name);
        foreach (var d in native)
        {
            var dn = Norm(d.Name);
            if (dn.Length >= 4 && (dn == n || (dn.Length >= 8 && n.Contains(dn)) || (n.Length >= 8 && dn.Contains(n)))) return $"Glint drives \"{d.Name}\" itself";
        }
        // RAM on the SMBus: Glint's own DRAM plugins own the bus addresses when they found sticks
        if (c.Type == 1 && native.Any(d => d.Zones.Any(z => z.Kind == ZoneKind.Stick))) return "Glint drives the RAM lighting itself";
        return null;
    }

    static string Short(Exception e) => (e is AggregateException a ? a.InnerException ?? e : e).Message;

    public void Dispose() => _client?.Dispose();
}
