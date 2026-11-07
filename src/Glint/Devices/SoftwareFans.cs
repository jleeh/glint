using LibreHardwareMonitor.Hardware;

namespace Glint.Devices;

/// <summary>
/// Fan outputs driven through LibreHardwareMonitor controls: motherboard headers, coolers (Kraken, Aquacomputer...)
/// and graphics-card fans. Sends each duty clamped to what the control accepts, re-sends on keep-alive, and hands a
/// fan back to its own firmware / driver (SetDefault) as soon as no rule drives it any more, on exit and on a crash.
/// A GPU fan left on a fixed software duty would otherwise stay there until reboot.
///
/// Another program writing the same fan (MSI Afterburner's fan curve, FanControl, a vendor tool) is detected from the
/// level the hardware reports back: Glint hands that fan back to automatic control (never leaves it parked on a fixed
/// duty), stops driving it, and tries again after <see cref="RetrySec"/> or when the fan leaves every rule. The fail-safe
/// still wins: a fail-safe duty is written to a contested fan too. Thread-safe: the fan loop applies duties, exit /
/// crash handlers release (a hard kill runs no handler; nothing in-process can cover that).
/// </summary>
public sealed class SoftwareFans
{
    /// <summary>Reported level may differ from the one sent by this many percent points without meaning anything.</summary>
    public const float Tolerance = 5;
    /// <summary>Seconds after Glint changes a fan's duty before its read-back is judged (fans and drivers take a moment).</summary>
    public const double SettleSec = 5;
    /// <summary>Seconds before Glint tries a contested fan again (re-detected within seconds if the other program is still at it).</summary>
    public const double RetrySec = 600;
    /// <summary>Fan-stop ("zero RPM") cards stop their fans at low duty requests by design: a read-back of 0% while Glint
    /// asks for this much or less is the card, not another program. Above it a stopped fan is still a conflict.</summary>
    public const int FanStopMaxDuty = 40;

    sealed class Held
    {
        public float Sent;            // value written (after clamping)
        public double ChangedAt;      // when Sent last changed
        public int Low;               // consecutive settled reads clearly below Sent
        public float? HighRef;        // first settled read clearly above Sent (a card minimum looks like this, and stays put)
        public bool FloorNoted, FanStopNoted;
    }

    readonly Dictionary<string, IControl> _controls = new();
    readonly HashSet<string> _fanStop = new();                 // controls on cards with a fan-stop (zero RPM) mode
    readonly Dictionary<string, int> _sent = new();
    readonly Dictionary<string, Held> _held = new();
    readonly Dictionary<string, (string Why, double At)> _contested = new();
    readonly HashSet<string> _failsafeOnContested = new();   // contested fans Glint is holding at the fail-safe duty
    readonly HashSet<string> _releaseFailed = new();
    bool _closed;                                              // ReleaseAll ran (exit / crash): no more writes
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

    /// <param name="fanStop">The hardware may stop this fan at low duty on its own (AMD Radeon zero-RPM): see <see cref="FanStopMaxDuty"/>.</param>
    public bool Add(string id, IControl control, bool fanStop = false)
    {
        lock (_lock)
        {
            if (!_controls.TryAdd(id, control)) return false;
            if (fanStop) _fanStop.Add(id);
            return true;
        }
    }
    public bool Contains(string id) { lock (_lock) return _controls.ContainsKey(id); }
    /// <summary>True while Glint holds this fan in software mode.</summary>
    public bool Touched(string id) { lock (_lock) return _held.ContainsKey(id); }
    public int Count { get { lock (_lock) return _controls.Count; } }
    /// <summary>Fans another program is controlling, with an explanation. Glint doesn't write them until they leave every rule.</summary>
    public Dictionary<string, string> Contested { get { lock (_lock) return _contested.ToDictionary(k => k.Key, k => k.Value.Why); } }
    public bool IsContested(string id) { lock (_lock) return _contested.ContainsKey(id); }

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
    /// A duty of <paramref name="failsafeDuty"/> or more is written even to a contested fan (the fail-safe always wins).
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, int> duty, bool full, int failsafeDuty = int.MaxValue)
    {
        lock (_lock)
        {
            if (_closed) return;
            double now = _clock();
            foreach (var (id, v) in duty)
            {
                if (!_controls.TryGetValue(id, out var c)) continue;
                if (_contested.TryGetValue(id, out var ct))
                {
                    if (now - ct.At >= RetrySec) { Uncontest(id, c, "trying it again"); }
                    else { ContestedFailsafe(id, c, v >= failsafeDuty ? v : null); continue; }
                }
                _held.TryGetValue(id, out var h);
                // Judge what the hardware reports before writing again (a keep-alive would hide the other program's value).
                if (h != null && Judge(id, c, h, now) is string why)
                {
                    _contested[id] = (why, now);
                    _held.Remove(id); _sent.Remove(id);
                    // Back to automatic control, never parked on a stale duty: if the read-back was wrong, the BIOS /
                    // driver curve still cools; if it was right, the other program goes on writing anyway.
                    try { c.SetDefault(); } catch (Exception e) { _log($"{id} release failed: {e.Message}"); }
                    _log($"{id}: {why}");
                    if (v >= failsafeDuty) ContestedFailsafe(id, c, v);
                    continue;
                }
                if (!full && h != null && _sent.TryGetValue(id, out var old) && old == v) continue;
                float value = Clamp(c, v);
                try { c.SetSoftware(value); }
                catch (Exception e) { _log($"{id} set duty failed: {e.Message}"); continue; }
                if (h == null) { _held[id] = h = new Held { Sent = value, ChangedAt = now }; _log($"{id}: Glint control"); }
                else if (h.Sent != value) { h.Sent = value; h.ChangedAt = now; h.Low = 0; h.HighRef = null; h.FloorNoted = false; h.FanStopNoted = false; }
                _sent[id] = v;
            }
            foreach (var id in _held.Keys.Where(id => !duty.ContainsKey(id)).ToList()) Release(id);
            foreach (var id in _contested.Keys.Where(id => !duty.ContainsKey(id)).ToList())
                Uncontest(id, _controls[id], "no longer in a rule; Glint takes it again if a rule names it");
        }
    }

    /// <summary>Contested fan: hold the fail-safe duty while it's asked for, else leave the fan on automatic control.</summary>
    void ContestedFailsafe(string id, IControl c, int? failsafe)
    {
        if (failsafe is int f)
        {
            try { c.SetSoftware(Clamp(c, f)); if (_failsafeOnContested.Add(id)) _log($"{id}: fail-safe {f}% (beats the other program)"); }
            catch (Exception e) { _log($"{id} set duty failed: {e.Message}"); }
        }
        else if (_failsafeOnContested.Remove(id))
        {
            try { c.SetDefault(); } catch (Exception e) { _log($"{id} release failed: {e.Message}"); }
        }
    }

    void Uncontest(string id, IControl c, string note)
    {
        if (_failsafeOnContested.Remove(id)) try { c.SetDefault(); } catch { }
        _contested.Remove(id);
        _log($"{id}: {note}");
    }

    const string Handed = "Glint handed it back to automatic control and stopped driving it (its fail-safe still applies). Switch the other program's fan control off; Glint tries again every 10 minutes, or take the fan out of its rule and back in";

    /// <summary>null = fine; else why another program must be driving this fan.</summary>
    string? Judge(string id, IControl c, Held h, double now)
    {
        if (now - h.ChangedAt < SettleSec) return null;
        if (_read(c) is not float a || !float.IsFinite(a)) return null; // no read-back: can't tell
        if (a <= 1 && _fanStop.Contains(id) && h.Sent <= FanStopMaxDuty)
        {
            // The card's zero-RPM mode parks the fans at low temperatures; as soon as Glint asks for more than
            // FanStopMaxDuty a stopped fan counts again (and the driver's own curve takes over if it stays stopped).
            if (!h.FanStopNoted) { h.FanStopNoted = true; _log($"{id}: stopped by the card's zero-RPM mode at {h.Sent:0}% (normal)"); }
            h.Low = 0; h.HighRef = null;
            return null;
        }
        if (a < h.Sent - Tolerance)
        {
            // Lower than asked: no card minimum explains that. Two reads in a row, so a single stale sample can't trigger it.
            if (++h.Low >= 2) return $"another program set this fan to {a:0}% (Glint sent {h.Sent:0}%), e.g. MSI Afterburner's fan curve or FanControl. {Handed}";
            return null;
        }
        h.Low = 0;
        if (a > h.Sent + Tolerance)
        {
            // Higher than asked and steady = the card's minimum duty. Moving while Glint holds still = someone else.
            if (h.HighRef is not float r) { h.HighRef = a; return null; }
            if (Math.Abs(a - r) > Tolerance) return $"another program is changing this fan ({r:0}% → {a:0}% while Glint sent {h.Sent:0}%), e.g. MSI Afterburner's fan curve or FanControl. {Handed}";
            if (!h.FloorNoted) { h.FloorNoted = true; _log($"{id}: runs at {a:0}% when {h.Sent:0}% is asked (the hardware's minimum?)"); }
            return null;
        }
        h.HighRef = null;
        return null;
    }

    /// <summary>Hand every fan we hold back to automatic control (exit, crash, shutdown) and refuse further writes, so a
    /// fan-loop tick racing the exit can't take a fan again.</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            _closed = true;
            foreach (var id in _held.Keys.ToList()) Release(id);
            foreach (var id in _failsafeOnContested.ToList()) { try { _controls[id].SetDefault(); } catch { } _failsafeOnContested.Remove(id); }
        }
    }

    void Release(string id)
    {
        try { _controls[id].SetDefault(); }
        catch (Exception e)
        {
            // Still in manual mode: keep it held (the next tick retries) rather than pretend it's released.
            if (_releaseFailed.Add(id)) _log($"{id} release failed: {e.Message}");
            return;
        }
        _releaseFailed.Remove(id);
        _log($"{id}: back to automatic control");
        _held.Remove(id);
        _sent.Remove(id);
    }
}
