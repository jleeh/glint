using LibreHardwareMonitor.Hardware;

namespace Glint.Devices;

/// <summary>
/// Fan outputs driven through LibreHardwareMonitor controls: motherboard headers, coolers (Kraken, Aquacomputer...)
/// and graphics-card fans. Sends each duty clamped to what the control accepts, re-sends on keep-alive, and hands a
/// fan back to its own firmware / driver (SetDefault) as soon as no rule drives it any more, on exit and on a crash.
/// A GPU fan left on a fixed software duty would otherwise stay there until reboot.
///
/// Another program writing the same fan (MSI Afterburner's fan curve, FanControl, a vendor tool) is detected from the
/// level the hardware reports back: Glint then stops driving that fan and leaves it to the other program (no reset),
/// until the fan is taken out of every rule. Thread-safe: the fan loop applies duties, exit / crash handlers release.
/// </summary>
public sealed class SoftwareFans
{
    /// <summary>Reported level may differ from the one sent by this many percent points without meaning anything.</summary>
    public const float Tolerance = 5;
    /// <summary>Seconds after Glint changes a fan's duty before its read-back is judged (fans and drivers take a moment).</summary>
    public const double SettleSec = 5;

    sealed class Held
    {
        public float Sent;            // value written (after clamping)
        public double ChangedAt;      // when Sent last changed
        public int Low;               // consecutive settled reads clearly below Sent
        public float? HighRef;        // first settled read clearly above Sent (a card minimum looks like this, and stays put)
        public bool FloorNoted;
    }

    readonly Dictionary<string, IControl> _controls = new();
    readonly Dictionary<string, int> _sent = new();
    readonly Dictionary<string, Held> _held = new();
    readonly Dictionary<string, string> _contested = new();
    readonly object _lock = new();
    readonly Action<string> _log;
    readonly Func<IControl, float?> _read;
    readonly Func<double> _clock;

    /// <param name="read">Level the hardware reports for a control (default: LHM's control sensor, refreshed every sensor tick).</param>
    /// <param name="clock">Seconds, monotonic (tests pass their own).</param>
    public SoftwareFans(Action<string>? log = null, Func<IControl, float?>? read = null, Func<double>? clock = null)
    {
        _log = log ?? (_ => { });
        _read = read ?? (c => c.Sensor?.Value);
        _clock = clock ?? (() => Environment.TickCount64 / 1000.0);
    }

    public bool Add(string id, IControl control) { lock (_lock) return _controls.TryAdd(id, control); }
    public bool Contains(string id) { lock (_lock) return _controls.ContainsKey(id); }
    /// <summary>True while Glint holds this fan in software mode.</summary>
    public bool Touched(string id) { lock (_lock) return _held.ContainsKey(id); }
    public int Count { get { lock (_lock) return _controls.Count; } }
    /// <summary>Fans another program is controlling, with an explanation. Glint doesn't write them until they leave every rule.</summary>
    public Dictionary<string, string> Contested { get { lock (_lock) return new(_contested); } }

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
            double now = _clock();
            foreach (var (id, v) in duty)
            {
                if (!_controls.TryGetValue(id, out var c) || _contested.ContainsKey(id)) continue;
                _held.TryGetValue(id, out var h);
                // Judge what the hardware reports before writing again (a keep-alive would hide the other program's value).
                if (h != null && Judge(id, c, h, now) is string why)
                {
                    _contested[id] = why;
                    _held.Remove(id); _sent.Remove(id); // not ours any more: no keep-alive, no reset on release
                    _log($"{id}: {why}");
                    continue;
                }
                if (!full && h != null && _sent.TryGetValue(id, out var old) && old == v) continue;
                float value = Clamp(c, v);
                try { c.SetSoftware(value); }
                catch (Exception e) { _log($"{id} set duty failed: {e.Message}"); continue; }
                if (h == null) { _held[id] = h = new Held { Sent = value, ChangedAt = now }; _log($"{id}: Glint control"); }
                else if (h.Sent != value) { h.Sent = value; h.ChangedAt = now; h.Low = 0; h.HighRef = null; h.FloorNoted = false; }
                _sent[id] = v;
            }
            foreach (var id in _held.Keys.Where(id => !duty.ContainsKey(id)).ToList()) Release(id);
            foreach (var id in _contested.Keys.Where(id => !duty.ContainsKey(id)).ToList())
            {
                _contested.Remove(id);
                _log($"{id}: no longer in a rule; Glint will take it again if a rule names it");
            }
        }
    }

    /// <summary>null = fine; else why another program must be driving this fan.</summary>
    string? Judge(string id, IControl c, Held h, double now)
    {
        if (now - h.ChangedAt < SettleSec) return null;
        if (_read(c) is not float a || !float.IsFinite(a)) return null; // no read-back: can't tell
        if (a < h.Sent - Tolerance)
        {
            // Lower than asked: no card minimum explains that. Two reads in a row, so a single stale sample can't trigger it.
            if (++h.Low >= 2) return $"another program set this fan to {a:0}% (Glint sent {h.Sent:0}%), e.g. MSI Afterburner's fan curve or FanControl. Glint stopped driving it; switch the other program's fan control off, then take the fan out of its rule and put it back (or restart Glint)";
            return null;
        }
        h.Low = 0;
        if (a > h.Sent + Tolerance)
        {
            // Higher than asked and steady = the card's minimum duty. Moving while Glint holds still = someone else.
            if (h.HighRef is not float r) { h.HighRef = a; return null; }
            if (Math.Abs(a - r) > Tolerance) return $"another program is changing this fan ({r:0}% → {a:0}% while Glint sent {h.Sent:0}%), e.g. MSI Afterburner's fan curve or FanControl. Glint stopped driving it; switch the other program's fan control off, then take the fan out of its rule and put it back (or restart Glint)";
            if (!h.FloorNoted) { h.FloorNoted = true; _log($"{id}: runs at {a:0}% when {h.Sent:0}% is asked (the hardware's minimum?)"); }
            return null;
        }
        h.HighRef = null;
        return null;
    }

    /// <summary>Hand every fan we hold back to automatic control (exit, crash, shutdown). Contested fans are left alone.</summary>
    public void ReleaseAll() { lock (_lock) foreach (var id in _held.Keys.ToList()) Release(id); }

    void Release(string id)
    {
        try { _controls[id].SetDefault(); _log($"{id}: back to automatic control"); }
        catch (Exception e) { _log($"{id} release failed: {e.Message}"); }
        _held.Remove(id);
        _sent.Remove(id);
    }
}
