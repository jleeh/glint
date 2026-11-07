using HidSharp;

namespace Glint.Devices;

/// <summary>How a zone's LEDs are arranged (drives effect geometry and the dashboard preview).</summary>
public enum ZoneKind { Strip, Ring, Matrix, Single, Stick }

/// <summary>A fan channel on a controller. Id is local to the device, e.g. "fan1", "pump".</summary>
public sealed record FanChannel(string Id, string Name, bool IsPump = false);

/// <summary>Any device that can read fan speeds / temperatures and set fan duty.</summary>
public interface IFanController : IDisposable
{
    /// <summary>Short unique prefix, e.g. "st", "cpro", "kraken".</summary>
    string Id { get; }
    string Name { get; }
    IReadOnlyList<FanChannel> Channels { get; }
    /// <summary>Read live values. Keys are local ("fan1", "probe1", "liquid"); the rig prefixes them with Id.</summary>
    void Poll(Dictionary<string, double> temps, Dictionary<string, int> rpm);
    /// <summary>Set duty (0-100) per local channel id.</summary>
    void SetDuty(IReadOnlyDictionary<string, int> duty);
    /// <summary>Hand control back to firmware / BIOS (called on exit).</summary>
    void Release() { }
    /// <summary>Hand only the fans back to the device's own control, leaving lighting with Glint (global fan-control switch
    /// off). false = the device can't (e.g. its hardware mode takes the lighting too): Glint keeps driving its fans.
    /// After a true, the next <see cref="SetDuty"/> takes the fans again.</summary>
    bool ReleaseFans() => false;
}

/// <summary>Context handed to every driver during discovery.</summary>
public sealed class DriverContext
{
    public required Core.GlintConfig Config { get; init; }
    public required Action<string> Log { get; init; }
    public Smbus? Smbus { get; init; }
    public bool Elevated { get; init; }
    /// <summary>LED count override for ports that can't report it (key = zone id, e.g. "unihub:ch1").</summary>
    public int Leds(string zoneId, int fallback) => Config.Lighting.LedCounts.TryGetValue(zoneId, out var v) ? v : fallback;
    /// <summary>Ids already taken; Unique("kraken") returns "kraken", then "kraken2", ...</summary>
    readonly HashSet<string> _ids = new();
    public string Unique(string id)
    {
        if (_ids.Add(id)) return id;
        for (int i = 2; ; i++) if (_ids.Add(id + i)) return id + i;
    }
    /// <summary>Give an id back (the device declined in init), so the next matching interface gets the plain id.</summary>
    public void Release(string id) => _ids.Remove(id);
    /// <summary>Open with an id that is only kept if the open succeeds.</summary>
    public T? Claim<T>(Func<Func<string, string>, T?> open) where T : class
    {
        string? got = null;
        T? r = null;
        try { r = open(id => got = Unique(id)); return r; }
        finally { if (r == null && got != null) Release(got); }
    }
}

/// <summary>What a driver found: any mix of RGB devices and fan controllers.</summary>
public sealed class Found
{
    public List<ILightDevice> Lights { get; } = new();
    public List<IFanController> Fans { get; } = new();
    public static Found Of(params object[] items)
    {
        var f = new Found();
        foreach (var i in items)
        {
            if (i is ILightDevice l) f.Lights.Add(l);
            if (i is IFanController c) f.Fans.Add(c);
        }
        return f;
    }
}

/// <summary>A USB HID driver: claims devices by VID/PID (and optionally interface / usage page).</summary>
public sealed record HidDriver(
    string Name,
    int Vid,
    int[] Pids,
    Func<HidDevice, DriverContext, Found?> Open,
    int Interface = -1,
    int UsagePage = -1,
    int MinOutputReport = 0,
    bool Experimental = true,
    bool UseFeatureReports = false);

/// <summary>An SMBus driver (RAM, some GPUs/boards). Must identify a device by reads before any write.</summary>
public sealed record SmbusDriver(string Name, Func<Smbus, DriverContext, Found?> Probe, bool Experimental = true);

/// <summary>
/// Central list of every driver Glint knows. Drivers register in their own files via
/// <c>[ModuleInitializer]</c>-free static lists below, so adding a driver is one line here.
/// </summary>
public static class DriverRegistry
{
    public static readonly List<HidDriver> Hid = new();
    public static readonly List<SmbusDriver> SmbusDrivers = new();
    /// <summary>Offline checks per driver (packet builders vs reference bytes). Run by `Glint.exe selftest`.</summary>
    public static readonly List<(string Driver, Func<IEnumerable<(string Name, bool Pass, string Detail)>> Run)> PacketChecks = new();

    /// <summary>Interface number from a Windows HID path ("...&amp;mi_02#..."), -1 if single-interface.</summary>
    public static int InterfaceOf(HidDevice d)
    {
        var p = d.DevicePath.ToLowerInvariant();
        int i = p.IndexOf("&mi_", StringComparison.Ordinal);
        return i >= 0 && int.TryParse(p.AsSpan(i + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out var n) ? n : -1;
    }

    public static int UsagePageOf(HidDevice d)
    {
        try
        {
            var items = d.GetReportDescriptor().DeviceItems;
            foreach (var it in items) foreach (var u in it.Usages.GetAllValues()) return (int)(u >> 16);
        }
        catch { }
        return -1;
    }

    static readonly Dictionary<string, string> _claims = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Which driver/plugin took this physical device ("" if none).</summary>
    public static string ClaimedBy(HidDevice d) { lock (_claims) return _claims.GetValueOrDefault(PhysicalKey(d), ""); }

    /// <summary>USB VID/PID pairs of every physical device a Glint driver or plugin claimed.</summary>
    public static HashSet<(int vid, int pid)> ClaimedVidPids()
    {
        lock (_claims) return _claims.Keys.Select(k => k.Split(':')).Where(p => p.Length >= 2)
            .Select(p => (Convert.ToInt32(p[0], 16), Convert.ToInt32(p[1], 16))).ToHashSet();
    }

    static int SafeOut(HidDevice d) { try { return d.GetMaxOutputReportLength(); } catch { return 0; } }
    static int SafeFeature(HidDevice d) { try { return d.GetMaxFeatureReportLength(); } catch { return 0; } }

    /// <summary>Discover everything. Each physical device is claimed once (first matching driver wins).</summary>
    public static (List<ILightDevice> lights, List<IFanController> fans) Discover(DriverContext ctx)
    {
        var lights = new List<ILightDevice>();
        var fans = new List<IFanController>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // vid:pid:serial-ish physical key
        var all = DeviceList.Local.GetHidDevices().ToList();
        foreach (var drv in Hid)
        {
            foreach (var d in all)
            {
                if (d.VendorID != drv.Vid || !drv.Pids.Contains(d.ProductID)) continue;
                if (drv.Interface >= 0 && InterfaceOf(d) != drv.Interface) continue;
                if (drv.UsagePage >= 0 && UsagePageOf(d) != drv.UsagePage) continue;
                int len = drv.UseFeatureReports ? SafeFeature(d) : SafeOut(d);
                if (len < drv.MinOutputReport) continue;
                string key = PhysicalKey(d);
                if (claimed.Contains(key)) continue;
                try
                {
                    var f = drv.Open(d, ctx);
                    if (f == null || (f.Lights.Count == 0 && f.Fans.Count == 0)) continue;
                    claimed.Add(key);
                    lock (_claims) _claims[key] = drv.Name;
                    lights.AddRange(f.Lights);
                    fans.AddRange(f.Fans);
                    ctx.Log($"{drv.Name}{(drv.Experimental ? " [experimental]" : "")}: {string.Join(", ", f.Lights.Select(l => $"{l.Id} {l.Zones.Sum(z => z.Leds)} LEDs").Concat(f.Fans.Select(x => $"{x.Id} {x.Channels.Count} fans")))}");
                }
                catch (Exception e) { ctx.Log($"{drv.Name} {d.VendorID:x4}:{d.ProductID:x4} failed: {e.Message}"); }
            }
        }
        if (ctx.Smbus != null)
            foreach (var drv in SmbusDrivers)
            {
                try
                {
                    var f = drv.Probe(ctx.Smbus, ctx);
                    if (f == null) continue;
                    lights.AddRange(f.Lights); fans.AddRange(f.Fans);
                    if (f.Lights.Count + f.Fans.Count > 0) ctx.Log($"{drv.Name}{(drv.Experimental ? " [experimental]" : "")}: {string.Join(", ", f.Lights.Select(l => $"{l.Id} {l.Zones.Sum(z => z.Leds)} LEDs"))}");
                }
                catch (Exception e) { ctx.Log($"{drv.Name} failed: {e.Message}"); }
            }
        return (lights, fans);
    }

    /// <summary>Strip the interface/collection part so all HID interfaces of one device share a key.</summary>
    static string PhysicalKey(HidDevice d)
    {
        var p = d.DevicePath.ToLowerInvariant();
        // \\?\hid#vid_1b1c&pid_0c2a&mi_00#a&2c68ffdc&0&0000#{guid} -> keep vid/pid + instance chunk minus interface suffix
        string serial = "";
        try { serial = d.GetSerialNumber(); } catch { }
        return $"{d.VendorID:x4}:{d.ProductID:x4}:{(string.IsNullOrEmpty(serial) ? p.Split('#').ElementAtOrDefault(2)?.Split('&').ElementAtOrDefault(1) : serial)}";
    }
}
