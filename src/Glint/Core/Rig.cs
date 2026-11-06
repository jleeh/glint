using Glint.Devices;
using LibreHardwareMonitor.Hardware;

namespace Glint.Core;

/// <summary>One sensor in <see cref="Rig.SensorCatalog"/>. Aliases = friendly ids usable as fan-curve inputs (first is the nicest).</summary>
public sealed record CatalogSensor(string Id, string Name, string Type, string Unit, double? Value, double? Min, double? Max, List<string>? Aliases);
/// <summary>One hardware node (CPU, GPU, board, Super I/O chip, drive, hub...) with its sensors and sub-hardware.</summary>
public sealed record CatalogHardware(string Id, string Name, string Type, List<CatalogSensor> Sensors, List<CatalogHardware> Sub);
/// <summary>The whole sensor tree (HWMonitor-style).</summary>
public sealed record SensorCatalogData(DateTime Time, List<CatalogHardware> Hardware, string? Error);

/// <summary>A controllable fan output.</summary>
public sealed record FanOutput(string Id, string Name, string Device);

/// <summary>
/// All hardware in one place. Native drivers come from <see cref="DriverRegistry"/> (HID + SMBus);
/// LibreHardwareMonitor adds CPU/GPU/board/storage/RAM/PSU/cooling-controller sensors and every
/// fan header it can drive (motherboard Super I/O, NZXT Kraken pumps, Aquacomputer, ...).
/// </summary>
public sealed class Rig : IDisposable
{
    public readonly List<IFanController> Controllers = new();
    public readonly List<ILightDevice> Lights = new();
    public readonly List<FanOutput> Fans = new();
    public readonly List<string> Log = new();
    readonly Computer? _lhm;
    Smbus? _smbus;
    readonly Dictionary<string, ISensor> _lhmControls = new();
    readonly Dictionary<string, ISensor> _lhmFanRpm = new();
    readonly Dictionary<string, ISensor> _lhmTemps = new();
    readonly HashSet<string> _lhmTouched = new();
    public bool Elevated { get; }
    public bool LhmRing0 { get; private set; }
    public double HubReadMs, LhmReadMs;

    readonly SensorOptions _sensorCfg;

    /// <param name="nativeDevices">false = LibreHardwareMonitor only (no HID / SMBus): read-only sensor benchmarks.</param>
    public Rig(GlintConfig cfg, bool useLhm = true, bool hardware = true, bool nativeDevices = true)
    {
        _sensorCfg = new SensorOptions { Storage = cfg.Sensors.Storage, Memory = cfg.Sensors.Memory, Psu = cfg.Sensors.Psu, Controllers = cfg.Sensors.Controllers };
        if (!hardware) { Log.Add("no-hardware mode (test)"); return; }
        Elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        if (!nativeDevices) Log.Add("LHM-only mode (benchmark)");
        else if (Elevated && Smbus.Available)
        {
            try { _smbus = new Smbus(); Log.Add($"SMBus {_smbus.Identity}"); }
            catch (Exception e) { Log.Add($"SMBus unavailable: {e.Message}"); }
        }
        else Log.Add("SMBus devices (RAM RGB) need admin + PawnIO (install.ps1)");

        if (nativeDevices)
        {
            var ctx = new DriverContext { Config = cfg, Log = Log.Add, Smbus = _smbus, Elevated = Elevated };
            var (lights, fans) = DriverRegistry.Discover(ctx);
            Lights.AddRange(lights);
            Controllers.AddRange(fans);
        }
        foreach (var c in Controllers)
            foreach (var ch in c.Channels)
                Fans.Add(new FanOutput($"{c.Id}:{ch.Id}", ch.Name, c.Name));

        if (useLhm)
        {
            try
            {
                _lhm = new Computer
                {
                    IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true,
                    IsStorageEnabled = cfg.Sensors.Storage, IsMemoryEnabled = cfg.Sensors.Memory, IsPsuEnabled = cfg.Sensors.Psu,
                    // Controller covers NZXT Kraken / GRID, Aquacomputer, Heatmaster, T-Balancer, ...
                    IsControllerEnabled = cfg.Sensors.Controllers,
                };
                _lhm.Open();
                Index();
            }
            catch (Exception e) { Log.Add($"LibreHardwareMonitor failed: {e.Message}"); _lhm = null; }
        }
    }

    static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-').Replace("--", "-");

    /// <summary>Prefix for LHM hardware: motherboard headers stay "mb:", controllers get their own.</summary>
    static string Prefix(IHardware hw)
    {
        var root = hw.Parent ?? hw;
        return root.HardwareType switch
        {
            HardwareType.Motherboard => "mb",
            HardwareType.Psu => "psu",
            HardwareType.Cooler or HardwareType.EmbeddedController => Slug(root.Name),
            _ => Slug(root.Name),
        };
    }

    void Index()
    {
        if (_lhm == null) return;
        int temps = 0;
        foreach (var hw in _lhm.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
            foreach (var s in hw.Sensors.Concat(hw.SubHardware.SelectMany(x => x.Sensors)))
            {
                if (s.SensorType == SensorType.Temperature) { _lhmTemps[s.Identifier.ToString()] = s; temps++; }
                bool fanCapable = hw.HardwareType is HardwareType.Motherboard or HardwareType.Cooler or HardwareType.EmbeddedController;
                if (fanCapable && s.SensorType == SensorType.Control && s.Control != null)
                {
                    string pre = Prefix(s.Hardware);
                    string id = pre + ":" + s.Name.Replace("Fan Control", "Fan").Trim();
                    if (_lhmControls.TryAdd(id, s)) Fans.Add(new FanOutput(id, s.Name, (s.Hardware.Parent ?? s.Hardware).Name));
                }
                if (fanCapable && s.SensorType == SensorType.Fan) _lhmFanRpm[Prefix(s.Hardware) + ":" + s.Name.Trim()] = s;
                if (hw.HardwareType == HardwareType.Cpu && s.SensorType == SensorType.Temperature && s.Value is > 0) LhmRing0 = true;
            }
        }
        var kinds = _lhm.Hardware.GroupBy(h => h.HardwareType).Select(g => $"{g.Count()} {g.Key}");
        Log.Add($"LHM: {string.Join(", ", kinds)}; {temps} temps, {_lhmControls.Count} controllable fan headers, ring0={(LhmRing0 ? "yes" : "no (needs admin + PawnIO)")}");
    }

    /// <summary>Snapshot of every temperature, with friendly aliases.</summary>
    public Dictionary<string, double> ReadSensors(Dictionary<string, int>? rpm = null)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        // Sensors page open (or sensors.all asked): switch every LHM group on; closed: back to the configured set.
        bool want = CatalogWanted;
        if (want != _catalogOn) SetFullCatalog(want);
        List<CatalogHardware> nativeCat = _catalogOn ? new() : _noNative;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var c in Controllers)
        {
            var temps = new Dictionary<string, double>();
            var speeds = new Dictionary<string, int>();
            double c0 = CpuMeter.ThreadMs();
            try { c.Poll(temps, speeds); }
            catch (Exception e) { Log.Add($"{c.Id} read failed: {e.Message}"); continue; }
            finally { CpuMeter.Add("poll " + c.Id, CpuMeter.ThreadMs() - c0); }
            foreach (var (k, v) in temps)
            {
                d[$"{c.Id}:{k}"] = v;
                if (k == "liquid") d.TryAdd("liquid", v); // first AIO's coolant is "liquid"
            }
            if (rpm != null) foreach (var (k, v) in speeds) rpm[$"{c.Id}:{k}"] = v;
            if (_catalogOn) nativeCat.Add(NativeGroup(c, temps, speeds));
        }
        HubReadMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lhm != null)
        {
            double c0 = CpuMeter.ThreadMs();
            // Storage (SMART), DIMM (SPD over SMBus) and PSU sensors change slowly and are costly to read: every 10 s.
            bool slowTick = (DateTime.UtcNow - _lastSlow).TotalSeconds >= 10;
            if (slowTick) _lastSlow = DateTime.UtcNow;
            foreach (var hw in _lhm.Hardware)
            {
                bool slow = hw.HardwareType is HardwareType.Storage or HardwareType.Memory or HardwareType.Psu;
                if (slow && !slowTick && !_catalogOn) continue;
                double h0 = CpuMeter.ThreadMs();
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
                CpuMeter.Add("lhm " + hw.HardwareType.ToString().ToLowerInvariant(), CpuMeter.ThreadMs() - h0);
            }
            _ = c0;
            double gpuCore = double.NaN, gpuHot = double.NaN, cpu = double.NaN, nvme = double.NaN, dimm = double.NaN;
            foreach (var (key, s) in _lhmTemps)
            {
                if (s.Value is not float v || v <= 0 || v > 150) continue;
                // Limits/thresholds are reported as "temperatures" by some drives and DIMMs: never treat them as readings.
                var rootType = (s.Hardware.Parent ?? s.Hardware).HardwareType;
                if (rootType is HardwareType.Storage or HardwareType.Memory && LimitName(s.Name)) continue;
                d[key] = v;
                var root = s.Hardware.Parent ?? s.Hardware;
                switch (root.HardwareType)
                {
                    case HardwareType.Cpu when s.Name.Contains("Tctl") || s.Name == "CPU Package" || s.Name.StartsWith("Core (Tdie"):
                        cpu = double.IsNaN(cpu) ? v : Math.Max(cpu, v); break;
                    case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when !root.Name.Contains("Radeon(TM) Graphics"):
                        if (s.Name == "GPU Core" && double.IsNaN(gpuCore)) gpuCore = v;
                        if (s.Name == "GPU Hot Spot" && double.IsNaN(gpuHot)) gpuHot = v;
                        break;
                    case HardwareType.Motherboard:
                        d["mb:" + s.Name] = v; break;
                    case HardwareType.Storage:
                        {
                            // One reading per drive: the composite temperature if the drive has one, else its first sensor.
                            string dk = $"disk:{Slug(root.Name)}";
                            bool composite = s.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase);
                            if (composite || !d.ContainsKey(dk) || !_diskComposite.Contains(dk))
                            {
                                if (composite) _diskComposite.Add(dk);
                                if (composite || !d.ContainsKey(dk)) d[dk] = v;
                            }
                            break;
                        }
                    case HardwareType.Memory:
                        dimm = double.IsNaN(dimm) ? v : Math.Max(dimm, v); break;
                    case HardwareType.Psu:
                        d[$"psu:{Slug(s.Name)}"] = v; break;
                    case HardwareType.Cooler or HardwareType.EmbeddedController:
                        string pre = Prefix(s.Hardware);
                        d[$"{pre}:{Slug(s.Name)}"] = v;
                        if (s.Name.Contains("Liquid", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Coolant", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Water", StringComparison.OrdinalIgnoreCase))
                            d.TryAdd("liquid", v);
                        break;
                }
            }
            if (!double.IsNaN(cpu)) d["cpu"] = cpu;
            if (!double.IsNaN(gpuCore)) d["gpu"] = gpuCore;
            if (!double.IsNaN(gpuHot)) d["gpu.hotspot"] = gpuHot;
            var disks = d.Where(k => k.Key.StartsWith("disk:")).Select(k => k.Value).ToList();
            if (disks.Count > 0) d["storage"] = disks.Max();
            if (!double.IsNaN(dimm)) d["ram"] = dimm;
            if (rpm != null) foreach (var (k, s) in _lhmFanRpm) if (s.Value is float r) rpm[k] = (int)r;
        }
        if (_catalogOn)
        {
            double b0 = CpuMeter.ThreadMs();
            _catalog = BuildCatalog(_lhm?.Hardware ?? (IEnumerable<IHardware>)Array.Empty<IHardware>(), nativeCat, _minMax, Interlocked.Exchange(ref _resetMinMax, 0) == 1);
            CpuMeter.Add("sensors page", CpuMeter.ThreadMs() - b0);
        }
        LhmReadMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return d;
    }

    DateTime _lastSlow = DateTime.MinValue;

    // ------------------------------------------------------------------ full sensor catalog (Sensors page / sensors.all)

    long _catalogUntil;
    bool _catalogOn;
    int _resetMinMax;
    volatile SensorCatalogData? _catalog;
    readonly Dictionary<string, (double min, double max)> _minMax = new();
    static readonly List<CatalogHardware> _noNative = new();

    /// <summary>Keep the full catalog live for the next 3 s (the dashboard calls this every second while the
    /// Sensors page is open, like <see cref="LightingEngine.WantField"/>). Costs nothing once it lapses.</summary>
    public void WantCatalog() => Interlocked.Exchange(ref _catalogUntil, Environment.TickCount64 + 3000);
    public bool CatalogWanted => Environment.TickCount64 < Interlocked.Read(ref _catalogUntil);
    /// <summary>Value of one catalog sensor by id (keeps the catalog live while someone keeps asking, e.g. the game overlay).</summary>
    public (double value, string unit)? CatalogValue(string id)
    {
        WantCatalog();
        var c = Catalog;
        if (c == null) return null;
        if (!ReferenceEquals(c, _indexedFor))
        {
            var idx = new Dictionary<string, CatalogSensor>();
            void Walk(IEnumerable<CatalogHardware> hs) { foreach (var h in hs) { foreach (var s in h.Sensors) idx[s.Id] = s; Walk(h.Sub); } }
            Walk(c.Hardware);
            _index = idx; _indexedFor = c;
        }
        return _index.TryGetValue(id, out var x) && x.Value is double v ? (v, x.Unit) : null;
    }
    Dictionary<string, CatalogSensor> _index = new();
    SensorCatalogData? _indexedFor;

    /// <summary>Restart min/max tracking on the next sensor tick.</summary>
    public void ResetCatalogMinMax() => Interlocked.Exchange(ref _resetMinMax, 1);
    /// <summary>The latest catalog (built on the sensor thread while wanted), or null.</summary>
    public SensorCatalogData? Catalog => _catalogOn ? _catalog : null;

    /// <summary>Runs on the sensor thread: turns every LHM group on (Sensors page open) or back to the configured set.</summary>
    void SetFullCatalog(bool on)
    {
        _catalogOn = on;
        if (on) { _minMax.Clear(); _resetMinMax = 0; } else _catalog = null;
        if (_lhm == null) return;
        double c0 = CpuMeter.ThreadMs();
        try
        {
            _lhm.IsStorageEnabled = on || _sensorCfg.Storage;
            _lhm.IsMemoryEnabled = on || _sensorCfg.Memory;
            _lhm.IsPsuEnabled = on || _sensorCfg.Psu;
            _lhm.IsControllerEnabled = on || _sensorCfg.Controllers;
            _lhm.IsNetworkEnabled = on;
        }
        catch (Exception e) { Log.Add($"LHM catalog switch failed: {e.Message}"); }
        CpuMeter.Add("sensors page", CpuMeter.ThreadMs() - c0);
    }

    /// <summary>
    /// Every sensor of every hardware LibreHardwareMonitor can see (plus Glint's own hubs), for the Sensors page and AI tooling.
    /// With a running service pass its rig: the catalog is switched on for a few seconds and the next sensor tick's snapshot is
    /// returned (min/max since the catalog was switched on). Without one, a private read-only LHM instance with every group
    /// enabled is opened, read twice and closed (min/max are LHM's own over those reads). Serialize with <see cref="GlintConfig.Json"/>.
    /// </summary>
    public static SensorCatalogData SensorCatalog(Rig? live = null, int waitMs = 2500)
    {
        if (live != null && (live._lhm != null || live.Controllers.Count > 0))
        {
            live.WantCatalog();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < waitMs)
            {
                if (live.Catalog is { } c && (DateTime.UtcNow - c.Time).TotalSeconds < 3) return c;
                Thread.Sleep(50);
            }
            return live.Catalog ?? new SensorCatalogData(DateTime.UtcNow, new(), "sensor thread did not answer in time");
        }
        var pc = new Computer
        {
            IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsStorageEnabled = true, IsMemoryEnabled = true,
            IsPsuEnabled = true, IsControllerEnabled = true, IsNetworkEnabled = true,
        };
        try
        {
            pc.Open();
            for (int i = 0; i < 2; i++) foreach (var hw in pc.Hardware) { hw.Update(); foreach (var sub in hw.SubHardware) sub.Update(); }
            return BuildCatalog(pc.Hardware, new(), null, false);
        }
        catch (Exception e) { return new SensorCatalogData(DateTime.UtcNow, new(), e.Message); }
        finally { pc.Close(); }
    }

    static CatalogHardware NativeGroup(IFanController c, Dictionary<string, double> temps, Dictionary<string, int> speeds)
    {
        var list = new List<CatalogSensor>();
        foreach (var (k, v) in temps)
        {
            var aliases = new List<string> { $"{c.Id}:{k}" };
            if (k == "liquid") aliases.Add("liquid");
            list.Add(new CatalogSensor($"{c.Id}:{k}", k, "Temperature", "°C", Clean(v), null, null, aliases));
        }
        foreach (var (k, v) in speeds) list.Add(new CatalogSensor($"{c.Id}:{k}:rpm", k, "Fan", "RPM", v, null, null, null));
        return new CatalogHardware(c.Id, c.Name, "FanHub", list, new());
    }

    static string Unit(SensorType t) => t switch
    {
        SensorType.Temperature => "°C", SensorType.Voltage => "V", SensorType.Current => "A", SensorType.Power => "W",
        SensorType.Clock => "MHz", SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => "%",
        SensorType.Frequency => "Hz", SensorType.Fan => "RPM", SensorType.Flow => "L/h", SensorType.Data => "GB",
        SensorType.SmallData => "MB", SensorType.Throughput => "B/s", SensorType.TimeSpan => "s", SensorType.Energy => "mWh",
        SensorType.Noise => "dBA", SensorType.Conductivity => "µS/cm", _ => "",
    };

    static double? Clean(double? v) => v is double x && double.IsFinite(x) ? Math.Round(x, Math.Abs(x) < 10 ? 3 : 1) : null;

    /// <summary>Friendly / fan-curve ids a raw sensor feeds (mirrors the aliasing in <see cref="ReadSensors"/>).</summary>
    static List<string>? Aliases(ISensor s)
    {
        var root = s.Hardware.Parent ?? s.Hardware;
        if (s.SensorType == SensorType.Fan && root.HardwareType is HardwareType.Motherboard or HardwareType.Cooler or HardwareType.EmbeddedController)
            return new() { Prefix(s.Hardware) + ":" + s.Name.Trim() };
        if (s.SensorType == SensorType.Control && s.Control != null && root.HardwareType is HardwareType.Motherboard or HardwareType.Cooler or HardwareType.EmbeddedController)
            return new() { Prefix(s.Hardware) + ":" + s.Name.Replace("Fan Control", "Fan").Trim() };
        if (s.SensorType != SensorType.Temperature) return null;
        if (root.HardwareType is HardwareType.Storage or HardwareType.Memory && LimitName(s.Name)) return null;
        var a = new List<string> { s.Identifier.ToString() };
        switch (root.HardwareType)
        {
            case HardwareType.Cpu when s.Name.Contains("Tctl") || s.Name == "CPU Package" || s.Name.StartsWith("Core (Tdie"): a.Insert(0, "cpu"); break;
            case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when !root.Name.Contains("Radeon(TM) Graphics"):
                if (s.Name == "GPU Core") a.Insert(0, "gpu");
                if (s.Name == "GPU Hot Spot") a.Insert(0, "gpu.hotspot");
                break;
            case HardwareType.Motherboard: a.Insert(0, "mb:" + s.Name); break;
            case HardwareType.Storage: a.Insert(0, $"disk:{Slug(root.Name)}"); a.Insert(0, "storage"); break;
            case HardwareType.Memory: a.Insert(0, "ram"); break;
            case HardwareType.Psu: a.Insert(0, $"psu:{Slug(s.Name)}"); break;
            case HardwareType.Cooler or HardwareType.EmbeddedController:
                a.Insert(0, $"{Prefix(s.Hardware)}:{Slug(s.Name)}");
                if (s.Name.Contains("Liquid", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Coolant", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Water", StringComparison.OrdinalIgnoreCase)) a.Insert(0, "liquid");
                break;
        }
        return a;
    }

    static SensorCatalogData BuildCatalog(IEnumerable<IHardware> hardware, List<CatalogHardware> native, Dictionary<string, (double min, double max)>? mm, bool reset)
    {
        if (reset) mm?.Clear();
        CatalogHardware Hw(IHardware h) => new(h.Identifier.ToString(), h.Name, h.HardwareType.ToString(),
            h.Sensors.OrderBy(x => x.SensorType).ThenBy(x => x.Index).Select(x =>
            {
                string id = x.Identifier.ToString();
                double? v = Clean(x.Value), lo, hi;
                if (mm == null) { lo = Clean(x.Min); hi = Clean(x.Max); }
                else if (v is double cur)
                {
                    var r = mm.TryGetValue(id, out var o) ? (Math.Min(o.min, cur), Math.Max(o.max, cur)) : (cur, cur);
                    mm[id] = r; lo = r.Item1; hi = r.Item2;
                }
                else { lo = hi = null; if (mm.TryGetValue(id, out var o)) { lo = o.min; hi = o.max; } }
                return new CatalogSensor(id, x.Name, x.SensorType.ToString(), Unit(x.SensorType), v, lo, hi, Aliases(x));
            }).ToList(),
            h.SubHardware.Select(Hw).ToList());
        var list = hardware.Select(Hw).ToList();
        if (mm != null)
            foreach (var g in native)
                for (int i = 0; i < g.Sensors.Count; i++)
                {
                    var x = g.Sensors[i];
                    if (x.Value is not double cur) continue;
                    var r = mm.TryGetValue(x.Id, out var o) ? (Math.Min(o.min, cur), Math.Max(o.max, cur)) : (cur, cur);
                    mm[x.Id] = r;
                    g.Sensors[i] = x with { Min = r.Item1, Max = r.Item2 };
                }
        list.AddRange(native);
        return new SensorCatalogData(DateTime.UtcNow, list, null);
    }
    readonly HashSet<string> _diskComposite = new();
    static bool LimitName(string n) => System.Text.RegularExpressions.Regex.IsMatch(n, "limit|threshold|critical|warning|resolution|max|min|high|low|trip", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Every raw temperature sensor (for sensors.list / AI tooling): id, device, name, value, whether it's ignored.</summary>
    public List<object> ListSensors()
    {
        var list = new List<object>();
        foreach (var (key, s) in _lhmTemps)
            list.Add(new { id = key, device = (s.Hardware.Parent ?? s.Hardware).Name, deviceType = (s.Hardware.Parent ?? s.Hardware).HardwareType.ToString(), name = s.Name, value = s.Value is float f ? Math.Round(f, 1) : (double?)null, ignored = (s.Hardware.Parent ?? s.Hardware).HardwareType is HardwareType.Storage or HardwareType.Memory && LimitName(s.Name) ? "limit/threshold" : s.Value is float g && (g <= 0 || g > 150) ? "implausible" : null });
        return list;
    }
    readonly Dictionary<string, int> _sent = new();
    DateTime _lastFull;

    /// <summary>Apply duties. Each controller gets one batched call (re-sent every 5 s as keep-alive).</summary>
    public void ApplyDuty(IReadOnlyDictionary<string, int> duty)
    {
        bool full = (DateTime.UtcNow - _lastFull).TotalSeconds > 5;
        if (full) _lastFull = DateTime.UtcNow;
        foreach (var c in Controllers)
        {
            var mine = duty.Where(k => k.Key.StartsWith(c.Id + ":")).ToList();
            if (mine.Count == 0) continue;
            if (!full && mine.All(k => _sent.TryGetValue(k.Key, out var v) && v == k.Value)) continue;
            try { c.SetDuty(mine.ToDictionary(k => k.Key[(c.Id.Length + 1)..], k => k.Value)); }
            catch (Exception e) { Log.Add($"{c.Id} set duty failed: {e.Message}"); continue; }
            foreach (var (k, v) in mine) _sent[k] = v;
        }
        foreach (var (k, v) in duty)
        {
            if (!_lhmControls.TryGetValue(k, out var s)) continue;
            if (_sent.TryGetValue(k, out var old) && old == v && !full) continue;
            s.Control.SetSoftware(v);
            _lhmTouched.Add(k);
            _sent[k] = v;
        }
    }

    public void Dispose()
    {
        foreach (var k in _lhmTouched) try { _lhmControls[k].Control.SetDefault(); } catch { }
        foreach (var c in Controllers) { try { c.Release(); } catch { } }
        foreach (var l in Lights) try { l.Dispose(); } catch { }
        foreach (var c in Controllers) try { c.Dispose(); } catch { }
        _lhm?.Close();
        _smbus?.Dispose();
    }
}
