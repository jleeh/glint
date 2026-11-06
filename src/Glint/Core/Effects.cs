using Glint.Devices;

namespace Glint.Core;

/// <summary>Geometry for one LED: where it sits across the whole rig and around its own ring/strip.</summary>
/// <summary>X/Y = position on the layout canvas (0..1), U = position within its own zone.</summary>
public readonly record struct Led(int Index, int Zone, double X, double U, bool Ring, double Y = 0.5);

/// <summary>Live inputs for reactive effects.</summary>
public sealed class FxContext
{
    public double Time;                         // seconds since start
    public double Dt;                           // seconds since last frame
    public IReadOnlyDictionary<string, double> Sensors = new Dictionary<string, double>();
    public float[] Spectrum = Array.Empty<float>(); // 0..1 per band, low → high (audio effects)
    public float Level;                             // 0..1 overall loudness
}

public abstract class Effect
{
    public EffectConfig Cfg { get; }
    protected readonly Rgb[] Pal;
    protected Effect(EffectConfig c)
    {
        Cfg = c;
        Pal = c.Colors.Count > 0 ? c.Colors.Select(Rgb.Parse).ToArray() : new[] { new Rgb(255, 255, 255) };
    }
    /// <summary>
    /// Position of an LED along the effect's Direction (degrees; 0 = left→right, 90 = top→bottom) across the
    /// layout canvas, 0..1. Aspect-corrected so 45° really is diagonal on the 16:10 canvas.
    /// </summary>
    protected double Proj(in Led l)
    {
        double a = Cfg.Angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a), A = Layout.Aspect;
        double v = (l.X - 0.5) * A * c + (l.Y - 0.5) * s;
        return v / (A * Math.Abs(c) + Math.Abs(s)) + 0.5;
    }
    /// <summary>Distance from a canvas point, aspect-corrected (0 at the point, ~1 across the canvas).</summary>
    protected static double Dist(in Led l, double cx, double cy) { double dx = (l.X - cx) * Layout.Aspect, dy = l.Y - cy; return Math.Sqrt(dx * dx + dy * dy) / Layout.Aspect; }

    /// <summary>True if the output changes over time (drives frame rate vs keep-alive only).</summary>
    public virtual bool Animated => true;
    public abstract void Render(FxContext ctx, ReadOnlySpan<Led> leds, Span<Rgb> o);

    /// <summary>Sample the palette as a looping gradient, p in [0,1).</summary>
    protected Rgb Palette(double p, bool loop = true)
    {
        if (Pal.Length == 1) return Pal[0];
        if (loop)
        {
            p = (p % 1 + 1) % 1 * Pal.Length;
            int i = (int)p;
            return Rgb.Lerp(Pal[i % Pal.Length], Pal[(i + 1) % Pal.Length], p - i);
        }
        p = Math.Clamp(p, 0, 1) * (Pal.Length - 1);
        int j = Math.Min((int)p, Pal.Length - 2);
        return Rgb.Lerp(Pal[j], Pal[j + 1], p - j);
    }

    static readonly Dictionary<EffectType, Func<EffectConfig, Effect>> Factory = new()
    {
        [EffectType.Off] = c => new Solid(c, true),
        [EffectType.Static] = c => new Solid(c, false),
        [EffectType.Gradient] = c => new GradientFx(c),
        [EffectType.Breathing] = c => new Breathing(c),
        [EffectType.Spectrum] = c => new SpectrumCycle(c),
        [EffectType.Wave] = c => new RainbowWave(c),
        [EffectType.ColorWave] = c => new ColorWave(c),
        [EffectType.Spin] = c => new Spin(c),
        [EffectType.Marquee] = c => new Marquee(c),
        [EffectType.Comet] = c => new Comet(c),
        [EffectType.Meteor] = c => new Meteor(c),
        [EffectType.Twinkle] = c => new Twinkle(c),
        [EffectType.Fire] = c => new Fire(c),
        [EffectType.Plasma] = c => new Plasma(c),
        [EffectType.Rain] = c => new Rain(c),
        [EffectType.Strobe] = c => new Strobe(c),
        [EffectType.Temperature] = c => new Temperature(c),
        [EffectType.Audio] = c => new AudioFx(c),
        [EffectType.Ripple] = c => new Ripple(c),
        [EffectType.AudioPulse] = c => new AudioPulse(c),
    };

    public static Effect Create(EffectConfig c)
    {
        Effect e = c.Type == EffectType.Plugin
            ? (Plugins.PluginHost.CreateEffect(c) ?? new Solid(new EffectConfig { Colors = new() { "#280000" } }, false))
            : Factory.TryGetValue(c.Type, out var f) ? f(c) : new Solid(c, false);
        return c.Layers is { Count: > 0 } ? new Layered(e, c.Layers.Select(Create).ToList()) : e;
    }

    public static IEnumerable<EffectType> All => Factory.Keys;
}

// ------------------------------------------------------------------ basic

sealed class Solid(EffectConfig c, bool off) : Effect(c)
{
    public override bool Animated => false;
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o) => o.Fill(off ? Rgb.Black : Pal[0]);
}

/// <summary>The palette laid across the canvas along Direction (static).</summary>
sealed class GradientFx(EffectConfig c) : Effect(c)
{
    public override bool Animated => false;
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    { for (int i = 0; i < l.Length; i++) o[i] = Palette(Cfg.PerZone ? l[i].U : Proj(l[i]), false); }
}


sealed class Breathing(EffectConfig c) : Effect(c)
{
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        double ph = x.Time * Cfg.Speed;
        double k = 0.5 - 0.5 * Math.Cos(ph * 2 * Math.PI);
        k = k * k; // perceptual: linger in the dark
        o.Fill(Pal[(int)ph % Pal.Length].Scale(k));
    }
}

sealed class SpectrumCycle(EffectConfig c) : Effect(c)
{
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
        => o.Fill(Cfg.Colors.Count > 1 ? Palette(x.Time * Cfg.Speed) : Rgb.FromHsv(x.Time * Cfg.Speed * 360));
}

/// <summary>Rainbow sweeping across the canvas along Direction (or around each ring if PerZone).</summary>
sealed class RainbowWave(EffectConfig c) : Effect(c)
{
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        double d = Cfg.Reverse ? -1 : 1;
        for (int i = 0; i < l.Length; i++)
            o[i] = Rgb.FromHsv(((Cfg.PerZone ? l[i].U : Proj(l[i])) * Cfg.Scale - x.Time * Cfg.Speed * d) * 360);
    }
}


/// <summary>User palette sweeping across the canvas along Direction.</summary>
sealed class ColorWave(EffectConfig c) : Effect(c)
{
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        double d = Cfg.Reverse ? -1 : 1;
        for (int i = 0; i < l.Length; i++)
            o[i] = Palette((Cfg.PerZone ? l[i].U : Proj(l[i])) * Cfg.Scale - x.Time * Cfg.Speed * d);
    }
}


/// <summary>A lit arc spinning around every fan ring; strips get a bouncing segment.</summary>
sealed class Spin(EffectConfig c) : Effect(c)
{
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        double head = x.Time * Cfg.Speed * (Cfg.Reverse ? -1 : 1);
        double width = Math.Clamp(Cfg.Size, 0.05, 1);
        for (int i = 0; i < l.Length; i++)
        {
            double u = l[i].U + l[i].Zone * Cfg.Offset; // stagger fans
            double dist = ((head - u) % 1 + 1) % 1; // 0 at head, grows behind it
            double k = dist < width ? 1 - dist / width : 0;
            var fg = Pal.Length > 1 ? Palette(l[i].Zone * 0.37 + x.Time * 0.05) : Pal[0];
            o[i] = fg.Scale(k * k);
        }
    }
}

/// <summary>Theatre chase: blocks of colour marching along.</summary>
sealed class Marquee(EffectConfig c) : Effect(c)
{
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        int block = Math.Max(1, (int)Cfg.Size);
        int shift = (int)(x.Time * Cfg.Speed * 10) * (Cfg.Reverse ? -1 : 1);
        for (int i = 0; i < l.Length; i++)
        {
            int k = ((l[i].Index + shift) / block % (Pal.Length + 1) + (Pal.Length + 1)) % (Pal.Length + 1);
            o[i] = k < Pal.Length ? Pal[k] : Rgb.Black;
        }
    }
}

/// <summary>A bright band with a fading tail sweeping across the canvas along Direction.</summary>
sealed class Comet(EffectConfig c) : Effect(c)
{
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        double tail = Math.Clamp(Cfg.Size, 0.02, 1);
        double head = (x.Time * Cfg.Speed) % (1 + tail);           // fully exits before re-entering
        for (int i = 0; i < l.Length; i++)
        {
            double p = Cfg.PerZone ? l[i].U : Proj(l[i]);
            if (Cfg.Reverse) p = 1 - p;
            double d = head - p;
            if (Cfg.PerZone) d = ((d % 1) + 1) % 1;
            double k = d >= 0 && d < tail ? Math.Pow(1 - d / tail, 2.2) : 0;
            o[i] = Palette(p + x.Time * 0.05).Scale(k);
        }
    }
}


// ------------------------------------------------------------------ stateful / particle

abstract class Stateful(EffectConfig c) : Effect(c)
{
    protected float[] A = Array.Empty<float>();
    protected readonly Random Rng = new(1234);
    protected void Ensure(int n) { if (A.Length != n) A = new float[n]; }
}

/// <summary>Random sparkles that fade out, optional base colour underneath.</summary>
sealed class Twinkle(EffectConfig c) : Stateful(c)
{
    readonly int[] _col = new int[4096];
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        Ensure(l.Length);
        double decay = Math.Exp(-x.Dt * 3 * Math.Max(0.1, Cfg.Speed * 5));
        double spawn = Cfg.Density * x.Dt * l.Length;
        while (spawn > 0) { if (Rng.NextDouble() < spawn) { int k = Rng.Next(l.Length); A[k] = 1; _col[k % _col.Length] = Rng.Next(Pal.Length); } spawn -= 1; }
        var bg = Rgb.Parse(Cfg.Background ?? "#000000");
        for (int i = 0; i < l.Length; i++)
        {
            A[i] = (float)(A[i] * decay);
            o[i] = Rgb.Lerp(bg, Pal[_col[i % _col.Length] % Pal.Length], A[i]);
        }
    }
}

/// <summary>Meteors streak across the canvas along Direction at random offsets, leaving fading tails.</summary>
sealed class Meteor(EffectConfig c) : Stateful(c)
{
    readonly List<(double along, double across, double v, int col)> _m = new();
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        Ensure(l.Length);
        double a = Cfg.Angle * Math.PI / 180, perp = a + Math.PI / 2;
        double Across(in Led q) => ((q.X - 0.5) * Layout.Aspect * Math.Cos(perp) + (q.Y - 0.5) * Math.Sin(perp)) / (Layout.Aspect * Math.Abs(Math.Cos(perp)) + Math.Abs(Math.Sin(perp))) + 0.5;
        // spawn each meteor in the lane of a real LED so it always crosses something
        if (l.Length > 0 && Rng.NextDouble() < Cfg.Density * x.Dt * 4)
            _m.Add((-0.1, Across(l[Rng.Next(l.Length)]), Cfg.Speed * (0.6 + Rng.NextDouble() * 0.8), Rng.Next(Pal.Length)));
        for (int i = 0; i < _m.Count; i++) _m[i] = _m[i] with { along = _m[i].along + _m[i].v * x.Dt };
        _m.RemoveAll(m => m.along > 1.2 + Cfg.Size);
        var hue = new int[l.Length];
        double fade = Math.Exp(-x.Dt * 5);
        for (int i = 0; i < l.Length; i++)
        {
            A[i] = (float)(A[i] * fade);
            double along = Proj(l[i]);
            // position across the travel direction, 0..1
            double across = Across(l[i]);
            foreach (var m in _m)
                if (Math.Abs(across - m.across) < 0.045 && Math.Abs(along - m.along) < 0.03) { A[i] = 1; hue[i] = m.col; }
        }
        for (int i = 0; i < l.Length; i++) o[i] = Pal[hue[i] % Pal.Length].Scale(A[i]);
    }
}


/// <summary>Flames rising from the bottom of the canvas (whole case burns upward).</summary>
sealed class Fire(EffectConfig c) : Effect(c)
{
    static readonly Rgb[] Heat = { new(0, 0, 0), new(120, 10, 0), new(255, 60, 0), new(255, 160, 20), new(255, 240, 160) };
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        double t = x.Time * Cfg.Speed;
        for (int i = 0; i < l.Length; i++)
        {
            double n = Noise.Fbm(l[i].X * 10, l[i].Y * 6 + t * 3, 3);       // noise scrolls upward
            double h = Math.Clamp(n * 1.35 + (l[i].Y - 0.55) * 1.1, 0, 1);  // hotter low in the case
            o[i] = Sample(Pal.Length > 2 ? Pal : Heat, h);
        }
    }
    static Rgb Sample(Rgb[] p, double t) { t *= p.Length - 1; int j = Math.Min((int)t, p.Length - 2); return Rgb.Lerp(p[j], p[j + 1], t - j); }
}


/// <summary>Demoscene plasma over the whole canvas: smooth flowing colour fields in 2D.</summary>
sealed class Plasma(EffectConfig c) : Effect(c)
{
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        double t = x.Time * Cfg.Speed, sc = Math.Max(0.2, Cfg.Scale);
        for (int i = 0; i < l.Length; i++)
        {
            double a = l[i].X * 8 * sc, b = l[i].Y * 6 * sc;
            double v = Math.Sin(a + t * 3) + Math.Sin(b * 1.3 + t * 2) + Math.Sin(a * 0.5 + b * 0.8 + t * 1.7) + Math.Sin(Math.Sqrt(a * a + b * b + 4) + t);
            double p = v / 8 + 0.5;
            o[i] = Cfg.Colors.Count > 1 ? Palette(p) : Rgb.FromHsv(p * 360 + t * 40);
        }
    }
}


/// <summary>Drops falling down the canvas (along Direction, default straight down) at random positions.</summary>
sealed class Rain(EffectConfig c) : Stateful(c)
{
    readonly List<(double x, double y, double v)> _d = new();
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        Ensure(l.Length);
        if (Rng.NextDouble() < Cfg.Density * x.Dt * 30) _d.Add((Rng.NextDouble(), -0.05, 0.5 + Rng.NextDouble() * 0.6));
        double sp = Math.Max(0.1, Cfg.Speed);
        for (int i = 0; i < _d.Count; i++) _d[i] = _d[i] with { y = _d[i].y + _d[i].v * sp * x.Dt };
        _d.RemoveAll(d => d.y > 1.1);
        double fade = Math.Exp(-x.Dt * 7);
        var bg = Rgb.Parse(Cfg.Background ?? "#000010");
        for (int i = 0; i < l.Length; i++)
        {
            A[i] = (float)(A[i] * fade);
            foreach (var d in _d)
            {
                double dx = (l[i].X - d.x) * Layout.Aspect, dy = l[i].Y - d.y;
                if (Math.Abs(dx) < 0.03 && dy < 0.01 && dy > -0.06) A[i] = Math.Max(A[i], (float)(1 + dy * 10));
            }
            o[i] = Rgb.Lerp(bg, Palette(l[i].X), A[i]);
        }
    }
}


sealed class Strobe(EffectConfig c) : Effect(c)
{
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        double ph = x.Time * Cfg.Speed * 4;
        bool on = ph % 1 < 0.15;
        o.Fill(on ? Pal[(int)ph % Pal.Length] : Rgb.Black);
    }
}

// ------------------------------------------------------------------ reactive

/// <summary>Colour follows a sensor (or "max(cpu,gpu)") across Range via the palette.</summary>
sealed class Temperature(EffectConfig c) : Effect(c)
{
    double _shown = double.NaN;
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        double v = FanEngine.Resolve(Cfg.Sensor ?? "cpu", x.Sensors);
        if (double.IsNaN(v)) { o.Fill(new Rgb(255, 0, 255)); return; } // magenta = sensor missing
        _shown = double.IsNaN(_shown) ? v : _shown + (v - _shown) * (1 - Math.Exp(-x.Dt / 1.5));
        var r = Cfg.Range ?? new[] { 40.0, 85 };
        double p = (_shown - r[0]) / Math.Max(1, r[1] - r[0]);
        var col = Palette(p, false);
        // optional subtle pulse that speeds up as it gets hot
        if (Cfg.Speed > 0)
        {
            double k = 0.85 + 0.15 * Math.Sin(x.Time * (1 + p * 6) * Cfg.Speed * 6.283);
            col = col.Scale(k);
        }
        o.Fill(col);
    }
}

/// <summary>Spectrum analyser across the canvas: bass at the start of Direction, treble at the end.</summary>
sealed class AudioFx(EffectConfig c) : Effect(c)
{
    float[] _smooth = Array.Empty<float>();
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        var s = x.Spectrum;
        if (s.Length == 0) { o.Fill(Rgb.Black); return; }
        if (_smooth.Length != s.Length) _smooth = new float[s.Length];
        float rel = (float)Math.Exp(-x.Dt * 6);
        for (int b = 0; b < s.Length; b++) _smooth[b] = Math.Max(s[b], _smooth[b] * rel);
        for (int i = 0; i < l.Length; i++)
        {
            int band = Math.Clamp((int)(Proj(l[i]) * s.Length), 0, s.Length - 1);
            float lvl = _smooth[band];
            // height on the canvas = level (bars rise from the bottom); rings light the fraction of the ring
            double h = l[i].Ring ? l[i].U : 1 - l[i].Y;
            double k = h <= lvl ? 1 : Math.Max(0, 1 - (h - lvl) * 8);
            o[i] = Palette((double)band / s.Length, false).Scale(k);
        }
    }
}

/// <summary>Rings expanding outward from a point on the canvas (default: the CPU area).</summary>
sealed class Ripple(EffectConfig c) : Effect(c)
{
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        double t = x.Time * Cfg.Speed, width = Math.Clamp(Cfg.Size, 0.05, 1);
        double cx = Cfg.Center?.Length == 2 ? Cfg.Center[0] : 0.45, cy = Cfg.Center?.Length == 2 ? Cfg.Center[1] : 0.42;
        for (int i = 0; i < l.Length; i++)
        {
            double d = Dist(l[i], cx, cy) * Cfg.Scale * 4;
            double ph = (d - t) % 1; if (ph < 0) ph += 1;
            double k = Math.Pow(Math.Max(0, 1 - ph / width), 2);      // bright leading edge, fading behind
            o[i] = Palette(Math.Floor(d - t) * 0.37 + ph * 0.1).Scale(Cfg.Reverse ? 1 - k : k);
        }
    }
}


/// <summary>Whole rig flashes on the beat (bass), colour cycles through the palette.</summary>
sealed class AudioPulse(EffectConfig c) : Effect(c)
{
    double _env, _hue;
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        float bass = x.Spectrum.Length > 0 ? x.Spectrum.Take(Math.Max(1, x.Spectrum.Length / 6)).Max() : 0;
        if (bass > _env) { _env = bass; _hue += 0.13; }
        else _env *= Math.Exp(-x.Dt * 5);
        var bg = Rgb.Parse(Cfg.Background ?? "#000000");
        var fg = Palette(_hue);
        o.Fill(Rgb.Lerp(bg, fg, Math.Clamp(_env * 1.2, 0, 1)));
    }
}

/// <summary>Base effect plus additive layers (e.g. static base + twinkle sparkles).</summary>
sealed class Layered(Effect baseFx, List<Effect> layers) : Effect(baseFx.Cfg)
{
    Rgb[] _tmp = Array.Empty<Rgb>();
    public override bool Animated => baseFx.Animated || layers.Any(l => l.Animated);
    public override void Render(FxContext x, ReadOnlySpan<Led> l, Span<Rgb> o)
    {
        baseFx.Render(x, l, o);
        if (_tmp.Length != o.Length) _tmp = new Rgb[o.Length];
        foreach (var layer in layers)
        {
            layer.Render(x, l, _tmp);
            for (int i = 0; i < o.Length; i++)
                o[i] = new Rgb((byte)Math.Min(255, o[i].R + _tmp[i].R), (byte)Math.Min(255, o[i].G + _tmp[i].G), (byte)Math.Min(255, o[i].B + _tmp[i].B));
        }
    }
}

public static class Noise
{
    static double Hash(int x, int y) { unchecked { int h = x * 374761393 + y * 668265263; h = (h ^ (h >> 13)) * 1274126177; return ((h ^ (h >> 16)) & 0xffff) / 65535.0; } }
    static double Smooth(double t) => t * t * (3 - 2 * t);
    public static double Value(double x, double y)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double xf = Smooth(x - xi), yf = Smooth(y - yi);
        double a = Hash(xi, yi), b = Hash(xi + 1, yi), c = Hash(xi, yi + 1), d = Hash(xi + 1, yi + 1);
        return a + (b - a) * xf + (c - a) * yf + (a - b - c + d) * xf * yf;
    }
    public static double Fbm(double x, double y, int oct)
    {
        double s = 0, amp = 0.5, f = 1;
        for (int i = 0; i < oct; i++) { s += Value(x * f, y * f) * amp; f *= 2; amp *= 0.5; }
        return s;
    }
}
