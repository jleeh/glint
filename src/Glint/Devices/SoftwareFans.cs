using LibreHardwareMonitor.Hardware;

namespace Glint.Devices;

/// <summary>
/// Fan outputs driven through LibreHardwareMonitor controls: motherboard headers, coolers (Kraken, Aquacomputer...)
/// and graphics-card fans. Sends each duty clamped to what the control accepts, re-sends on keep-alive, and hands a
/// fan back to its own firmware / driver (SetDefault) as soon as no rule drives it any more, on exit and on a crash.
/// A GPU fan left on a fixed software duty would otherwise stay there until reboot.
/// Thread-safe: the fan loop applies duties, process-exit / crash handlers release.
/// </summary>
public sealed class SoftwareFans
{
    readonly Dictionary<string, IControl> _controls = new();
    readonly Dictionary<string, int> _sent = new();
    readonly HashSet<string> _touched = new();
    readonly object _lock = new();
    readonly Action<string> _log;

    public SoftwareFans(Action<string>? log = null) => _log = log ?? (_ => { });

    public bool Add(string id, IControl control) { lock (_lock) return _controls.TryAdd(id, control); }
    public bool Contains(string id) { lock (_lock) return _controls.ContainsKey(id); }
    /// <summary>True while Glint holds this fan in software mode.</summary>
    public bool Touched(string id) { lock (_lock) return _touched.Contains(id); }
    public int Count { get { lock (_lock) return _controls.Count; } }

    /// <summary>Duty → the value sent, inside the control's own software range (some GPUs have a floor, e.g. 30%).</summary>
    public static float Clamp(IControl c, int duty)
    {
        float lo = Math.Max(0, c.MinSoftwareValue), hi = Math.Min(100, c.MaxSoftwareValue);
        if (!(hi > lo)) { lo = 0; hi = 100; } // missing / nonsense range: plain percent
        return Math.Clamp(duty, lo, hi);
    }

    /// <summary>
    /// Apply every duty that names one of our controls; <paramref name="full"/> re-sends unchanged ones (keep-alive).
    /// Fans we hold that are absent from <paramref name="duty"/> go back to automatic control.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, int> duty, bool full)
    {
        lock (_lock)
        {
            foreach (var (id, v) in duty)
            {
                if (!_controls.TryGetValue(id, out var c)) continue;
                if (!full && _touched.Contains(id) && _sent.TryGetValue(id, out var old) && old == v) continue;
                try { c.SetSoftware(Clamp(c, v)); }
                catch (Exception e) { _log($"{id} set duty failed: {e.Message}"); continue; }
                if (_touched.Add(id)) _log($"{id}: Glint control");
                _sent[id] = v;
            }
            foreach (var id in _touched.Where(id => !duty.ContainsKey(id)).ToList()) Release(id);
        }
    }

    /// <summary>Hand every fan we hold back to automatic control (exit, crash, shutdown).</summary>
    public void ReleaseAll() { lock (_lock) foreach (var id in _touched.ToList()) Release(id); }

    void Release(string id)
    {
        try { _controls[id].SetDefault(); _log($"{id}: back to automatic control"); }
        catch (Exception e) { _log($"{id} release failed: {e.Message}"); }
        _touched.Remove(id);
        _sent.Remove(id);
    }
}
