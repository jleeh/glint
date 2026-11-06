using Glint.Devices;

namespace Glint.Core;

/// <summary>Where a lighting zone sits on the layout canvas. Normalised 0..1 on both axes (canvas is 16:10).</summary>
public sealed class ZonePlacement
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 0.08;
    public double H { get; set; } = 0.08;
    /// <summary>Degrees clockwise.</summary>
    public double Rotation { get; set; }
}

/// <summary>A zone as the layout code sees it.</summary>
public sealed record LayoutZone(string Id, string DeviceId, string Name, int Leds, ZoneKind Kind, int Cols, string Category);

/// <summary>Canvas geometry + a role-based auto-arrange (Glint can't see inside the case, so it makes an educated first guess).</summary>
public static class Layout
{
    /// <summary>Canvas width / height. Shared with the dashboard (web/app.js uses the same value).</summary>
    public const double Aspect = 1.6;

    /// <summary>Canvas position (0..1, 0..1) of every LED in a zone, in the zone's LED order.</summary>
    public static (double x, double y)[] Positions(ZonePlacement p, ZoneKind kind, int leds, int cols)
    {
        var pts = new (double x, double y)[leds];
        if (leds == 0) return pts;
        // work in "canvas units" (x scaled by Aspect) so circles stay round, then rotate about the centre
        double cx = (p.X + p.W / 2) * Aspect, cy = p.Y + p.H / 2, w = p.W * Aspect, h = p.H;
        double rad = p.Rotation * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        (double, double) Put(double lx, double ly) => ((cx + lx * cos - ly * sin) / Aspect, cy + lx * sin + ly * cos);
        switch (kind)
        {
            case ZoneKind.Ring:
                {
                    double r = Math.Min(w, h) / 2 * 0.85;
                    for (int i = 0; i < leds; i++) { double a = 2 * Math.PI * i / leds - Math.PI / 2; pts[i] = Put(Math.Cos(a) * r, Math.Sin(a) * r); }
                    break;
                }
            case ZoneKind.Matrix when cols > 0:
                {
                    int rows = (leds + cols - 1) / cols;
                    for (int i = 0; i < leds; i++)
                    {
                        double fx = cols == 1 ? 0.5 : (i % cols) / (double)(cols - 1), fy = rows == 1 ? 0.5 : (i / cols) / (double)(rows - 1);
                        pts[i] = Put((fx - 0.5) * w * 0.92, (fy - 0.5) * h * 0.85);
                    }
                    break;
                }
            case ZoneKind.Single:
                for (int i = 0; i < leds; i++) pts[i] = Put(leds == 1 ? 0 : (i / (double)(leds - 1) - 0.5) * w * 0.6, 0);
                break;
            default: // Strip / Stick: a line along the rectangle's long axis
                {
                    bool vertical = h > w;
                    double len = (vertical ? h : w) * 0.94;
                    for (int i = 0; i < leds; i++)
                    {
                        double f = leds == 1 ? 0 : i / (double)(leds - 1) - 0.5;
                        pts[i] = vertical ? Put(0, f * len) : Put(f * len, 0);
                    }
                    break;
                }
        }
        return pts;
    }

    static readonly string[] Peripheral = { "keyboard", "mouse", "mousepad", "headset", "speaker", "monitor", "peripheral" };

    /// <summary>
    /// Educated first layout from what each zone is: AIO pump on the CPU, its fans along the top radiator,
    /// other hub fans at the front then bottom/rear, RAM beside the CPU, board LEDs at the chipset, strips
    /// around the case edges, and keyboard/mouse on the desk below the case.
    /// </summary>
    public static Dictionary<string, ZonePlacement> AutoArrange(IReadOnlyList<LayoutZone> zones)
    {
        var map = new Dictionary<string, ZonePlacement>();
        double fan = 0.085, fanH = fan * Aspect; // square on screen
        ZonePlacement Sq(double cx, double cy, double size) => new() { X = cx - size / 2, Y = cy - size * Aspect / 2, W = size, H = size * Aspect };

        // pumps → CPU socket
        var pumps = zones.Where(z => z.Kind == ZoneKind.Ring && (z.Id.EndsWith(":pump") || z.Category == "pump")).ToList();
        for (int i = 0; i < pumps.Count; i++) map[pumps[i].Id] = Sq(0.42 + i * 0.09, 0.38, 0.075);

        // fans of a hub that also has a pump → radiator along the top
        var aioDevices = pumps.Select(p => p.DeviceId).ToHashSet();
        var radiator = zones.Where(z => z.Kind == ZoneKind.Ring && aioDevices.Contains(z.DeviceId) && !map.ContainsKey(z.Id)).ToList();
        for (int i = 0; i < radiator.Count; i++) map[radiator[i].Id] = Sq(0.22 + i * (fan + 0.016), 0.155, fan);

        // all other fans: front column (3), then bottom row, then rear
        var slots = new List<(double x, double y)>();
        for (int i = 0; i < 3; i++) slots.Add((0.105, 0.25 + i * (fanH + 0.045)));
        for (int i = 0; i < 4; i++) slots.Add((0.25 + i * (fan + 0.016), 0.705));
        slots.Add((0.665, 0.30)); slots.Add((0.665, 0.30 + fanH + 0.045));
        int s = 0;
        foreach (var z in zones.Where(z => z.Kind == ZoneKind.Ring && !map.ContainsKey(z.Id) && !Peripheral.Contains(z.Category)))
        {
            var (x, y) = s < slots.Count ? slots[s] : (0.20 + (s - slots.Count) * 0.05, 0.62);
            map[z.Id] = Sq(x, y, fan); s++;
        }

        // RAM sticks → beside the CPU
        var ram = zones.Where(z => z.Kind == ZoneKind.Stick && !map.ContainsKey(z.Id)).ToList();
        for (int i = 0; i < ram.Count; i++) map[ram[i].Id] = new() { X = 0.53 + i * 0.03, Y = 0.27, W = 0.012, H = 0.24 };

        // peripherals → the desk under the case
        double deskX = 0.08;
        foreach (var z in zones.Where(z => !map.ContainsKey(z.Id) && (Peripheral.Contains(z.Category) || z.Kind == ZoneKind.Matrix)))
        {
            bool small = z.Category is "mouse" or "headset" || (z.Kind == ZoneKind.Single && z.Leds <= 2);
            double w = small ? 0.05 : z.Category == "mousepad" ? 0.22 : 0.40;
            map[z.Id] = new() { X = deskX, Y = 0.86, W = w, H = small ? 0.08 : 0.10 };
            deskX += w + 0.03;
        }

        // single board LEDs → chipset area
        int b = 0;
        foreach (var z in zones.Where(z => !map.ContainsKey(z.Id) && z.Kind == ZoneKind.Single))
            map[z.Id] = new() { X = 0.36 + (b++) * 0.06, Y = 0.50, W = 0.05, H = 0.04 };

        // strips → around the case edges: front, bottom, top, rear, then inner rows
        var edges = new List<ZonePlacement>
        {
            new() { X = 0.025, Y = 0.10, W = 0.012, H = 0.62 },
            new() { X = 0.10, Y = 0.795, W = 0.58, H = 0.012 },
            new() { X = 0.10, Y = 0.045, W = 0.58, H = 0.012 },
            new() { X = 0.735, Y = 0.10, W = 0.012, H = 0.62 },
        };
        int e = 0;
        foreach (var z in zones.Where(z => !map.ContainsKey(z.Id)))
        {
            map[z.Id] = e < edges.Count ? edges[e] : new() { X = 0.30, Y = 0.585 - (e - edges.Count) * 0.035, W = 0.34, H = 0.012 };
            e++;
        }
        return map;
    }
}

/// <summary>
/// Copies of lighting.layout taken before anything automatic replaces it (config dir\layout-backups\*.json,
/// newest 10 kept), so a hand-tuned layout is never lost to lighting.autoLayout.
/// </summary>
public static class LayoutBackups
{
    public const int Keep = 10;
    public static string Dir => Path.Combine(GlintConfig.Dir, "layout-backups");

    sealed class Backup
    {
        public DateTime Time { get; set; }
        public string Reason { get; set; } = "";
        public Dictionary<string, ZonePlacement> Layout { get; set; } = new();
    }

    /// <summary>Write a backup; returns its name.</summary>
    public static string Save(Dictionary<string, ZonePlacement> layout, string reason)
    {
        // runs as the (non-elevated) user: the folder is user-writable, a planted link must not reach other files
        return UserFs.Run(() =>
        {
            Directory.CreateDirectory(Dir);
            var now = DateTime.Now;
            string name = now.ToString("yyyyMMdd-HHmmss-fff");
            for (int n = 2; File.Exists(Path.Combine(Dir, name + ".json")); n++) name = now.ToString("yyyyMMdd-HHmmss-fff") + "-" + n;
            var path = Path.Combine(Dir, name + ".json");
            File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(new Backup { Time = now, Reason = reason, Layout = layout }, GlintConfig.Json));
            File.Move(path + ".tmp", path, true);
            foreach (var old in Files().Skip(Keep)) try { File.Delete(old); } catch { }
            return name;
        });
    }

    /// <summary>Backup files, newest first (names sort by time).</summary>
    static IEnumerable<string> Files() => UserFs.Run(() =>
        Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.json").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ThenByDescending(Path.GetFileName, StringComparer.Ordinal).ToList() : new List<string>());

    public static List<object> List() => UserFs.Run(() => Files().Select(f =>
    {
        try
        {
            var b = System.Text.Json.JsonSerializer.Deserialize<Backup>(File.ReadAllText(f), GlintConfig.Json)!;
            return (object)new { name = Path.GetFileNameWithoutExtension(f), time = b.Time, reason = b.Reason, zones = b.Layout.Count };
        }
        catch (Exception e) { return new { name = Path.GetFileNameWithoutExtension(f), error = e.Message }; }
    }).ToList());

    /// <summary>Load a backup by name, or "latest".</summary>
    public static (string name, Dictionary<string, ZonePlacement> layout) Load(string name) => UserFs.Run(() => LoadInner(name));

    static (string name, Dictionary<string, ZonePlacement> layout) LoadInner(string name)
    {
        string? file = name is "" or "latest" ? Files().FirstOrDefault()
            : Path.Combine(Dir, Path.GetFileNameWithoutExtension(Path.GetFileName(name)) + ".json");
        if (file == null || !File.Exists(file)) throw new FileNotFoundException(name is "" or "latest" ? "no layout backups yet" : $"no layout backup \"{name}\" (see lighting.layoutBackups)");
        var b = System.Text.Json.JsonSerializer.Deserialize<Backup>(File.ReadAllText(file), GlintConfig.Json) ?? throw new InvalidDataException("unreadable backup");
        return (Path.GetFileNameWithoutExtension(file), b.Layout ?? new());
    }
}
