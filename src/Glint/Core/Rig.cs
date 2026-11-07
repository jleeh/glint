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
/// fan it can drive (motherboard Super I/O headers, NZXT Kraken pumps, Aquacomputer, graphics-card fans, ...).
/// </summary>
public sealed class Rig : IDisposable
{
    public readonly List<IFanController> Controllers = new();
    public readonly List<ILightDevice> Lights = new();
    public readonly List<FanOutput> Fans = new();
    public readonly LogBuffer Log = new();
    readonly Computer? _lhm;
    Smbus? _smbus;
    /// <summary>LHM-driven fan outputs (board headers, coolers, graphics-card fans).</summary>
    public readonly SoftwareFans Software;
    readonly Dictionary<string, ISensor> _lhmFanRpm = new();
    readonly Dictionary<string, ISensor> _lhmTemps = new();
    public bool Elevated { get; }
    public bool LhmRing0 { get; private set; }
    /// <summary>SMBus (RAM RGB) state: null = working, else why not (no admin, no PawnIO, unsupported chipset...).</summary>
    public string? SmbusError { get; private set; } = "not started";
    public bool SmbusOk => _smbus != null;
    /// <summary>OpenRGB bridge, when enabled in the config (connected once at startup).</summary>
    public OpenRgbBridge? OpenRgb { get; private set; }
    public double HubReadMs, LhmReadMs;

    readonly SensorOptions _sensorCfg;

    /// <param name="nativeDevices">false = LibreHardwareMonitor only (no HID / SMBus): read-only sensor benchmarks.</param>
    public Rig(GlintConfig cfg, bool useLhm = true, bool hardware = true, bool nativeDevices = true)
    {
        Software = new SoftwareFans(Log.Add);
        _sensorCfg = new SensorOptions { Storage = cfg.Sensors.Storage, Memory = cfg.Sensors.Memory, Psu = cfg.Sensors.Psu, Controllers = cfg.Sensors.Controllers };
        if (!hardware) { Log.Add("no-hardware mode (test)"); return; }
        Elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        if (!nativeDevices) Log.Add("LHM-only mode (benchmark)");
        else if (Elevated && Smbus.Available)
        {
            try { _smbus = new Smbus(); SmbusError = null; Log.Add($"SMBus {_smbus.Identity}"); }
            catch (Exception e) { SmbusError = e.Message; Log.Add($"SMBus unavailable: {e.Message}"); }
        }
        else { SmbusError = !Elevated ? "Glint is not running as administrator" : "PawnIO is not installed"; Log.Add("SMBus devices (RAM RGB) need admin + PawnIO (install.ps1)"); }

        if (nativeDevices)
        {
            var ctx = new DriverContext { Config = cfg, Log = Log.Add, Smbus = _smbus, Elevated = Elevated };
            var (lights, fans) = DriverRegistry.Discover(ctx);
            Lights.AddRange(lights);
            Controllers.AddRange(fans);
            // OpenRGB bridge: only when switched on; one connect attempt (loopback refuses instantly when OpenRGB isn't running)
            if (cfg.OpenRgb is { Enabled: true } orgb)
            {
                OpenRgb = OpenRgbBridge.Connect(orgb, Lights.ToList(), DriverRegistry.ClaimedVidPids(), ctx.Unique, Log.Add);
                Lights.AddRange(OpenRgb.Devices);
            }
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
                if (Software.Count > 0)
                {
                    // A crash or a kill must not leave a fan (above all a GPU fan) parked on a fixed software duty.
                    AppDomain.CurrentDomain.ProcessExit += ReleaseSoftwareFans;
                    AppDomain.CurrentDomain.UnhandledException += ReleaseSoftwareFans;
                }
            }
            catch (Exception e) { Log.Add($"LibreHardwareMonitor failed: {e.Message}"); _lhm = null; }
        }
    }

    void ReleaseSoftwareFans(object? _, EventArgs __) { try { Software.ReleaseAll(); } catch { } }

    /// <summary>Discrete graphics cards (the integrated Radeon is skipped, as for the "gpu" temperature alias).</summary>
    static bool DiscreteGpu(IHardware root) =>
        root.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && !root.Name.Contains("Radeon(TM) Graphics");

    /// <summary>Fan-id prefix per discrete GPU: "gpu" for the first, then "gpu2", "gpu3"... in LHM's order.</summary>
    static Dictionary<IHardware, string> GpuPrefixes(IEnumerable<IHardware> hardware)
    {
        var d = new Dictionary<IHardware, string>();
        foreach (var h in hardware) if (DiscreteGpu(h)) d[h] = d.Count == 0 ? "gpu" : $"gpu{d.Count + 1}";
        return d;
    }

    /// <summary>"GPU Fan 1" → "Fan 1" (the prefix already says GPU).</summary>
    public static string GpuFanName(string sensorName)
    {
        var n = sensorName.Trim();
        return n.StartsWith("GPU ", StringComparison.OrdinalIgnoreCase) && n.Length > 4 ? n[4..].Trim() : n;
    }

    static bool FanCapable(IHardware root, Dictionary<IHardware, string> gpus) =>
        root.HardwareType is HardwareType.Motherboard or HardwareType.Cooler or HardwareType.EmbeddedController || gpus.ContainsKey(root);

    /// <summary>Fan id for an LHM fan / control sensor: "mb:Fan #2", "kraken-x63:Pump", "gpu:Fan 1". Control and RPM share it.</summary>
    static string FanKey(ISensor s, Dictionary<IHardware, string> gpus)
    {
        var root = s.Hardware.Parent ?? s.Hardware;
        string name = s.Name.Replace("Fan Control", "Fan").Trim();
        return gpus.TryGetValue(root, out var p) ? p + ":" + GpuFanName(name) : Prefix(s.Hardware) + ":" + name;
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
        var gpus = GpuPrefixes(_lhm.Hardware);
        foreach (var hw in _lhm.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
            foreach (var s in hw.Sensors.Concat(hw.SubHardware.SelectMany(x => x.Sensors)))
            {
                if (s.SensorType == SensorType.Temperature) { _lhmTemps[s.Identifier.ToString()] = s; temps++; }
                bool fanCapable = FanCapable(hw, gpus);
                if (fanCapable && s.SensorType == SensorType.Control && s.Control != null)
                {
                    string id = FanKey(s, gpus);
                    if (Software.Add(id, s.Control)) Fans.Add(new FanOutput(id, s.Name, (s.Hardware.Parent ?? s.Hardware).Name));
                }
                if (fanCapable && s.SensorType == SensorType.Fan) _lhmFanRpm[FanKey(s, gpus)] = s;
                if (hw.HardwareType == HardwareType.Cpu && s.SensorType == SensorType.Temperature && s.Value is > 0) LhmRing0 = true;
            }
        }
        var kinds = _lhm.Hardware.GroupBy(h => h.HardwareType).Select(g => $"{g.Count()} {g.Key}");
        Log.Add($"LHM: {string.Join(", ", kinds)}; {temps} temps, {Software.Count} controllable fans ({Fans.Count(f => f.Id.StartsWith("gpu"))} on graphics cards), ring0={(LhmRing0 ? "yes" : "no (needs admin + PawnIO)")}");
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
    static List<string>? Aliases(ISensor s, Dictionary<IHardware, string> gpus)
    {
        var root = s.Hardware.Parent ?? s.Hardware;
        if (s.SensorType == SensorType.Fan && FanCapable(root, gpus)) return new() { FanKey(s, gpus) };
        if (s.SensorType == SensorType.Control && s.Control != null && FanCapable(root, gpus)) return new() { FanKey(s, gpus) };
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
        var hwList = hardware.ToList();
        var gpus = GpuPrefixes(hwList);
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
                return new CatalogSensor(id, x.Name, x.SensorType.ToString(), Unit(x.SensorType), v, lo, hi, Aliases(x, gpus));
            }).ToList(),
            h.SubHardware.Select(Hw).ToList());
        var list = hwList.Select(Hw).ToList();
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
    readonly HashSet<string> _handedBack = new();
    bool _fansOff;
    public bool FansOff => _fansOff;

    /// <summary>
    /// Global fan-control switch (fan loop thread). Off: every hub whose plugin can release its fans alone does so and
    /// gets no more duties; LHM fans (board headers, coolers, GPU fans) are released by leaving them out of the duty
    /// map (<see cref="SoftwareFans.Apply"/>). On again: everything is re-sent at once.
    /// </summary>
    public void SetFanControl(bool on)
    {
        if (on != _fansOff) return; // unchanged
        _fansOff = !on;
        if (_fansOff)
        {
            Log.Add("fan control off");
            foreach (var c in Controllers)
            {
                bool ok = false;
                try { ok = c.ReleaseFans(); }
                catch (Exception e) { Log.Add($"{c.Id} release fans failed: {e.Message}"); }
                if (ok) { _handedBack.Add(c.Id); Log.Add($"{c.Id}: fans handed back to the device"); }
                else Log.Add($"{c.Id}: can't hand its fans back without its lighting; Glint keeps driving them");
            }
        }
        else
        {
            _handedBack.Clear(); _sent.Clear(); _lastFull = DateTime.MinValue;
            Log.Add("fan control on");
        }
    }

    /// <summary>True when the switch is off and this fan is on its own control (not driven by Glint).</summary>
    public bool HandedBack(string fanId) =>
        _fansOff && (Software.Contains(fanId) || _handedBack.Any(id => fanId.StartsWith(id + ":", StringComparison.Ordinal)));

    /// <summary>Apply duties. Each controller gets one batched call (re-sent every 5 s as keep-alive). <paramref name="duty"/>
    /// must name every LHM fan Glint should keep driving: the rest are handed back to their firmware / driver.</summary>
    public void ApplyDuty(IReadOnlyDictionary<string, int> duty)
    {
        bool full = (DateTime.UtcNow - _lastFull).TotalSeconds > 5;
        if (full) _lastFull = DateTime.UtcNow;
        foreach (var c in Controllers)
        {
            if (_handedBack.Contains(c.Id)) continue; // fan control off and this hub runs its own fans
            var mine = duty.Where(k => k.Key.StartsWith(c.Id + ":")).ToList();
            if (mine.Count == 0) continue;
            if (!full && mine.All(k => _sent.TryGetValue(k.Key, out var v) && v == k.Value)) continue;
            try { c.SetDuty(mine.ToDictionary(k => k.Key[(c.Id.Length + 1)..], k => k.Value)); }
            catch (Exception e) { Log.Add($"{c.Id} set duty failed: {e.Message}"); continue; }
            foreach (var (k, v) in mine) _sent[k] = v;
        }
        Software.Apply(duty, full); // LHM fans not in `duty` go back to automatic control
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= ReleaseSoftwareFans;
        AppDomain.CurrentDomain.UnhandledException -= ReleaseSoftwareFans;
        Software.ReleaseAll();
        foreach (var c in Controllers) { try { c.Release(); } catch { } }
        foreach (var l in Lights) try { l.Dispose(); } catch { }
        OpenRgb?.Dispose();
        foreach (var c in Controllers) try { c.Dispose(); } catch { }
        _lhm?.Close();
        _smbus?.Dispose();
    }
}

/// <summary>
/// The device / plugin log: bounded (plugins write to it through dev.log and must not be able to grow it without
/// limit), thread-safe (device threads, the control pipe and the dashboard use it at once), enumerated as a snapshot.
/// </summary>
public sealed class LogBuffer : IEnumerable<string>
{
    public const int MaxLines = 1000, MaxLineLength = 1000;
    readonly Queue<string> _q = new();
    public void Add(string line)
    {
        line ??= "";
        if (line.Length > MaxLineLength) line = line[..MaxLineLength] + "…";
        lock (_q)
        {
            _q.Enqueue(line);
            while (_q.Count > MaxLines) _q.Dequeue();
        }
    }
    public int Count { get { lock (_q) return _q.Count; } }
    public IEnumerator<string> GetEnumerator() { string[] snap; lock (_q) snap = _q.ToArray(); return ((IEnumerable<string>)snap).GetEnumerator(); }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
