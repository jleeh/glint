using System.Globalization;
using Glint.Core;

namespace Glint.GameOverlay;

/// <summary>A reading for an overlay item: value + unit ("°C", "rpm", "%", "MHz", "W", ...).</summary>
public delegate (double value, string unit)? SensorResolver(string source);

/// <summary>Everything an item can show, captured once per overlay tick.</summary>
public sealed class OverlayData
{
    public FrameStats.Result Frames;
    public IReadOnlyDictionary<string, double> Sensors = new Dictionary<string, double>();
    public IReadOnlyDictionary<string, int> Rpm = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Duty = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, string> FanNames = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SensorNames = new Dictionary<string, string>();
    /// <summary>Glint's own CPU use, % of the whole machine (NaN = not measured yet).</summary>
    public double GlintCpu = double.NaN;
    public DateTime Now = DateTime.Now;
    /// <summary>Extra sources (e.g. the hardware sensor catalog: "lhm:/gpu-nvidia/0/clock/0"). Tried after the built-ins.</summary>
    public SensorResolver? Extra;
}

/// <summary>One row/segment of the overlay, ready to draw.</summary>
public readonly record struct OverlayRow(string Source, string Label, string Value, string Unit, string? Color, bool Hero);

public static class OverlayItems
{
    public static readonly string[] Builtins = { "fps", "frametime", "low1", "time", "glint.cpu" };

    public static bool IsFrameSource(string s) => s is "fps" or "frametime" or "low1";

    /// <summary>Rows for the configured items. Sensors that aren't reading are left out; frame items show "–" without data.</summary>
    public static List<OverlayRow> Rows(IEnumerable<OverlayItem> items, OverlayData d)
    {
        var rows = new List<OverlayRow>();
        foreach (var it in items)
        {
            if (string.IsNullOrWhiteSpace(it?.Source)) continue;
            var v = Resolve(it.Source.Trim(), d);
            if (v == null) continue;
            var label = string.IsNullOrWhiteSpace(it.Label) ? DefaultLabel(it.Source.Trim(), d) : it.Label!;
            rows.Add(new OverlayRow(it.Source, label, v.Value.value, v.Value.unit, it.Color, it.Source == "fps"));
        }
        return rows;
    }

    /// <summary>Display text for one source, or null if it has no reading (item hidden).</summary>
    public static (string value, string unit)? Resolve(string src, OverlayData d)
    {
        const string none = "–";
        var f = d.Frames;
        switch (src)
        {
            case "fps": return (f.Fps > 0 ? Num(f.Fps, "fps") : none, "");
            case "frametime": return (f.FrameMs > 0 ? Num(f.FrameMs, "ms") : none, "ms");
            case "low1": return (f.Low1 > 0 ? Num(f.Low1, "fps") : none, "");
            case "time": return (d.Now.ToString("HH:mm", CultureInfo.InvariantCulture), "");
            case "glint.cpu": return double.IsNaN(d.GlintCpu) ? (none, "%") : (d.GlintCpu.ToString(d.GlintCpu < 10 ? "0.0" : "0", CultureInfo.InvariantCulture), "%");
        }
        var r = Reading(src, d);
        return r == null ? null : (Num(r.Value.value, r.Value.unit), r.Value.unit);
    }

    /// <summary>Numeric reading for fan:/duty:/sensor sources, then the pluggable resolver.</summary>
    public static (double value, string unit)? Reading(string src, OverlayData d)
    {
        if (src.StartsWith("fan:", StringComparison.OrdinalIgnoreCase))
            return d.Rpm.TryGetValue(src[4..], out var rpm) ? (rpm, "rpm") : null;
        if (src.StartsWith("duty:", StringComparison.OrdinalIgnoreCase))
            return d.Duty.TryGetValue(src[5..], out var duty) ? (duty, "%") : null;
        if (d.Sensors.TryGetValue(src, out var t) && !double.IsNaN(t)) return (t, "°C");   // the sensors dict is temperatures
        try { if (d.Extra?.Invoke(src) is { } x && !double.IsNaN(x.value)) return x; } catch { }
        return null;
    }

    public static string Num(double v, string unit)
    {
        string fmt = unit switch
        {
            "ms" => "0.0",
            "V" => "0.000",
            "GHz" => "0.00",
            "GB" => "0.0",
            "fps" or "°C" or "rpm" or "%" or "MHz" or "W" or "MB" => "0",
            _ => Math.Abs(v) >= 100 ? "0" : "0.0",
        };
        return v.ToString(fmt, CultureInfo.InvariantCulture);
    }

    public static string DefaultLabel(string src, OverlayData? d = null)
    {
        switch (src)
        {
            case "fps": return "FPS";
            case "frametime": return "Frame";
            case "low1": return "1% low";
            case "time": return "Time";
            case "glint.cpu": return "Glint";
            case "cpu": return "CPU";
            case "gpu": return "GPU";
            case "gpu.hotspot": return "Hotspot";
            case "liquid": return "Liquid";
            case "storage": return "SSD";
            case "ram": return "RAM";
        }
        if (src.StartsWith("fan:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("duty:", StringComparison.OrdinalIgnoreCase))
        {
            var id = src[(src.IndexOf(':') + 1)..];
            return d?.FanNames.TryGetValue(id, out var n) == true ? n : id;
        }
        if (d?.SensorNames.TryGetValue(src, out var sn) == true) return sn;
        var leaf = src.TrimEnd('/');
        int slash = leaf.LastIndexOf('/');
        return slash >= 0 ? leaf[(slash + 1)..] : leaf;
    }

    /// <summary>Null if the source is well-formed (whether a sensor exists is checked against the live list separately).</summary>
    public static string? SourceSyntax(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "source is empty";
        if (s.Length > 200) return "source is too long";
        if ((s.StartsWith("fan:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("duty:", StringComparison.OrdinalIgnoreCase)) && s.IndexOf(':') == s.Length - 1)
            return $"\"{s}\" needs a fan id after ':' (see fans.list)";
        if (s.IndexOfAny(new[] { '(', ')', ',' }) >= 0) return $"\"{s}\": expressions aren't supported here, use one sensor id";
        return null;
    }
}

/// <summary>"Ctrl+Shift+O" ↔ RegisterHotKey modifiers + virtual key.</summary>
public static class Hotkey
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    /// <summary>Null for "" (no hotkey) or a malformed string; check <see cref="Error"/> for which.</summary>
    public static (uint mods, uint vk)? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        uint mods = 0; uint vk = 0;
        foreach (var raw in s.Split('+'))
        {
            var t = raw.Trim().ToLowerInvariant();
            switch (t)
            {
                case "ctrl": case "control": mods |= Ctrl; continue;
                case "shift": mods |= Shift; continue;
                case "alt": mods |= Alt; continue;
                case "win": case "windows": mods |= Win; continue;
            }
            if (vk != 0 || t.Length == 0) return null;
            if (t.Length == 1 && char.IsAsciiLetterOrDigit(t[0])) vk = char.ToUpperInvariant(t[0]);
            else if (t[0] == 'f' && int.TryParse(t[1..], out int fn) && fn is >= 1 and <= 24) vk = (uint)(0x70 + fn - 1);
            else if (Enum.TryParse<Keys>(t, true, out var k) && (int)k is > 0 and < 256 && !int.TryParse(t, out _)) vk = (uint)k;
            else return null;
        }
        return vk == 0 ? null : (mods, vk);
    }

    public static string? Error(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = Parse(s);
        if (p == null) return $"hotkey \"{s}\" isn't valid (e.g. \"Ctrl+Shift+O\", \"Alt+F10\"; \"\" for none)";
        if (p.Value.mods == 0 && !(p.Value.vk is >= 0x70 and <= 0x87)) return $"hotkey \"{s}\" needs a modifier (Ctrl, Shift, Alt or Win) unless it is a function key";
        return null;
    }
}
