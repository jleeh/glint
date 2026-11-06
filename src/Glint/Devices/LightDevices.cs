
namespace Glint.Devices;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static Rgb Parse(string s)
    {
        s = s.Trim().TrimStart('#');
        if (s.Length != 6) return Black;
        var v = Convert.ToInt32(s, 16);
        return new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
    public override string ToString() => $"#{R:x2}{G:x2}{B:x2}";
    public Rgb Scale(double f) => new((byte)Math.Clamp(R * f, 0, 255), (byte)Math.Clamp(G * f, 0, 255), (byte)Math.Clamp(B * f, 0, 255));
    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
    public static Rgb FromHsv(double h, double s = 1, double v = 1)
    {
        h = ((h % 360) + 360) % 360 / 60.0;
        int i = (int)h; double f = h - i;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return new((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}

/// <summary>One addressable LED zone (a fan, a strip, a pump head, mobo LEDs).</summary>
/// <summary>One addressable LED zone. For Matrix zones, Cols gives the grid width (row-major LEDs).</summary>
public sealed record LightZone(string Id, string Name, int Leds, ZoneKind Kind = ZoneKind.Strip, int Cols = 0, string Category = "");

public interface ILightDevice : IDisposable
{
    string Id { get; }
    string Name { get; }
    IReadOnlyList<LightZone> Zones { get; }
    /// <summary>Colours for every zone in order, concatenated.</summary>
    void Write(ReadOnlySpan<Rgb> colors);
    /// <summary>Idle time the controller needs between frames (measured, see README).</summary>
    int MinGapMs => 0;
}
