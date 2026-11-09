// Ported from Headroom (MIT, Headroom contributors)
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.Json;
using Glint.Core;

namespace Glint.Tuning;

/// <summary>One recorded crash. Applied* = the offsets in force when it happened (null = stock, so it never counts).</summary>
public sealed record CrashRecord(long RecordId, string Channel, DateTime Time, string Kind, string Suspect, string Detail,
    string? Bugcheck, int? Core, int? AppliedCore, int? AppliedMem, string ActionText, bool Counts, string? Step);

/// <summary>What to do about a crash. Kind: none | backoff | revert. Step: which clock a back-off lowered (core | mem).</summary>
public sealed record CrashAction(string Kind, string Text, bool Counts, string? Step = null, int NewCore = 0, int NewMemNvml = 0);

/// <summary>
/// The decision rules, pure so they are tested without an event log or a GPU. GPU-suspect crash with an overclock applied:
/// back off one step (core -15 MHz, then memory -250 Afterburner units, alternating, never below 0); MaxCrashes of them
/// within CrashWindowDays puts the GPU back to stock. A soft app crash counts only as the 2nd within 24 h. CPU-suspect
/// events never change anything (CPU settings live in the BIOS). Crashes at stock are recorded, never counted.
/// </summary>
public static class CrashDecision
{
    public const int CoreStepMhz = 15, MemStepAfterburner = 250;
    public const string None = "none", Backoff = "backoff", Revert = "revert";

    public static bool IsStock((int Core, int MemNvml, double MemRatio)? a) => a is not { } x || (x.Core <= 0 && x.MemNvml <= 0);

    public static CrashAction Decide(TuningConfig cfg, CrashRecord rec, IReadOnlyList<CrashRecord> history, (int Core, int MemNvml, double MemRatio)? applied)
    {
        if (rec.Suspect == "cpu")
            return new(None, "CPU-suspect: nothing changed (CPU settings live in the BIOS)" + (rec.Core is { } c ? $", note core {c}" : ""), false);
        if (rec.Suspect != "gpu") return new(None, "Cause unknown: nothing changed", false);
        if (IsStock(applied)) return new(None, "No GPU overclock was active: nothing changed", false);
        if (rec.Kind == CrashEvents.GpuAppCrash &&
            !history.Any(h => h.Kind == CrashEvents.GpuAppCrash && h.AppliedCore != null && Math.Abs((rec.Time - h.Time).TotalHours) <= 24))
            return new(None, "Soft event (a game crashed in the driver): watching, no change yet", false);

        var a = applied!.Value;
        int days = Math.Max(1, cfg.CrashWindowDays), max = Math.Max(1, cfg.MaxCrashes);
        // a revert starts a fresh slate: crashes before it belonged to the settings that were thrown away
        var lastRevert = history.Where(h => h.Step == "revert").Select(h => h.Time).DefaultIfEmpty(DateTime.MinValue).Max();
        var since = new[] { rec.Time.AddDays(-days), lastRevert }.Max();
        var window = history.Where(h => h.Counts && h.Time > since).ToList();
        if (window.Count + 1 >= max)
            return new(Revert, $"Reverted to stock: {window.Count + 1} GPU-suspect crashes in {days} days. Re-run the tuner to try again.", true, "revert");
        if (!cfg.AutoBackoff) return new(None, "Auto back-off is off: nothing changed", true);

        string? last = window.Select(h => h.Step).LastOrDefault(s => s != null);
        string[] order = last == "core" ? ["mem", "core"] : ["core", "mem"];
        foreach (var kind in order)
        {
            if (kind == "core" && a.Core > 0)
            {
                int n = Math.Max(0, a.Core - CoreStepMhz);
                return new(Backoff, $"Auto back-off after a GPU crash: core offset {a.Core:+0;-0;0} -> {n:+0;-0;0} MHz", true, "core", n, a.MemNvml);
            }
            if (kind == "mem" && a.MemNvml > 0)
            {
                int n = Math.Max(0, a.MemNvml - (int)Math.Round(MemStepAfterburner * (a.MemRatio > 0 ? a.MemRatio : 0.5)));
                return new(Backoff, $"Auto back-off after a GPU crash: memory offset lowered by {MemStepAfterburner} (Afterburner units)", true, "mem", a.Core, n);
            }
        }
        return new(Revert, "Reverted to stock: nothing left to back off. Re-run the tuner to try again.", true, "revert");
    }
}

/// <summary>
/// Watches Windows' event log for driver resets, WHEA hardware errors and bugchecks while an overclock is applied.
/// Glint runs all the time, so it subscribes in-process (<see cref="EventLogWatcher"/>: the OS calls back, no thread or
/// polling of ours) only between <see cref="Start"/> and <see cref="Stop"/>; the owner calls those while an overclock is
/// applied and <c>Tuning.CrashWatch</c> is on. Start also scans once for a crash since the last boot (Kernel-Power 41 /
/// bugcheck 1001 are only logged after the reboot, i.e. possibly while Glint wasn't running). Events are deduped by
/// record id in crashes.json (capped) so a restart never counts a crash twice. The tuning service owns the saved state:
/// this class only calls the injected callbacks and never knows how offsets are stored.
/// </summary>
public sealed class CrashWatch : IDisposable
{
    public const int MaxEvents = 200, MaxSeen = 500;
    readonly Func<TuningConfig> _cfg;
    readonly Func<IGpuControl?> _gpu;
    readonly Func<(int Core, int MemNvml, double MemRatio)?> _applied;
    readonly Action<int, int> _save;
    readonly Action _revert;
    readonly string _file, _dumpDir;
    readonly int _tpc;
    readonly object _gate = new();
    readonly List<EventLogWatcher> _watchers = [];
    List<CrashRecord> _events = [];
    List<string> _seen = [];

    /// <summary>Raised (on whichever thread handled the event) for every classified crash, after the action was applied.</summary>
    public event Action<CrashRecord, CrashAction>? Acted;
    public string? LastError { get; private set; }
    public bool Running { get { lock (_gate) return _watchers.Count > 0; } }

    /// <param name="gpu">Resolved when an action is applied (null = no GPU control right now: the action is only recorded).</param>
    /// <param name="applied">Offsets in force now: core MHz, memory in NVML units, and the NVML/Afterburner ratio; null = stock.</param>
    /// <param name="saveOffsets">Persist the lowered (core, memNvml) as the saved result.</param>
    /// <param name="revertToStock">Persist "back at stock" (the GPU itself is reset here through <paramref name="gpu"/>).</param>
    public CrashWatch(Func<TuningConfig> cfg, Func<IGpuControl?> gpu, Func<(int Core, int MemNvml, double MemRatio)?> currentApplied,
        Action<int, int> saveOffsets, Action revertToStock, string? file = null, int threadsPerCore = 2, string? minidumpDir = null)
    {
        _cfg = cfg; _gpu = gpu; _applied = currentApplied; _save = saveOffsets; _revert = revertToStock;
        _file = file ?? Path.Combine(GlintConfig.Dir, "crashes.json");
        _dumpDir = minidumpDir ?? Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Minidump");
        _tpc = Math.Max(1, threadsPerCore);
        Load();
    }

    public IReadOnlyList<CrashRecord> Records { get { lock (_gate) return [.. _events]; } }

    /// <summary>Cores with WHEA errors in the window, for the "loosen these in the BIOS" hint.</summary>
    public IReadOnlyDictionary<int, int> CoresToLoosen()
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, _cfg().CrashWindowDays));
        lock (_gate) return _events.Where(e => e.Core != null && e.Time >= since).GroupBy(e => e.Core!.Value).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>Subscribe (no-op when already running or Tuning.CrashWatch is off) and scan once for crashes since boot.</summary>
    public void Start(bool scanSinceBoot = true)
    {
        if (!_cfg().CrashWatch) return;
        lock (_gate)
        {
            if (_watchers.Count > 0) return;
            foreach (var (chan, xp) in new[] { ("System", CrashEvents.SystemXPath), ("Application", CrashEvents.AppXPath) })
                try
                {
                    var w = new EventLogWatcher(new EventLogQuery(chan, PathType.LogName, xp));
                    w.EventRecordWritten += OnRecord;
                    w.Enabled = true;
                    _watchers.Add(w);
                }
                catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException) { LastError = $"{chan}: {ex.Message}"; }
        }
        if (scanSinceBoot) Task.Run(ScanSinceBoot);
    }

    public void Stop()
    {
        lock (_gate)
        {
            foreach (var w in _watchers) { w.Enabled = false; w.EventRecordWritten -= OnRecord; w.Dispose(); }
            _watchers.Clear();
        }
    }

    public void Dispose() => Stop();

    void OnRecord(object? sender, EventRecordWrittenEventArgs e)
    {
        try
        {
            using var r = e.EventRecord;
            if (r != null && CrashEvents.Parse(r.ToXml()) is { } ev) Process(ev);
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    /// <summary>Crashes logged since the last boot that we haven't seen (1001 first, so its 41 twin is dropped as a duplicate).</summary>
    public void ScanSinceBoot()
    {
        try
        {
            long ms = Environment.TickCount64 + 120_000;
            var found = new List<CrashEvent>();
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, CrashEvents.ScanXPath(ms)));
            for (EventRecord? r; found.Count < 50 && (r = reader.ReadEvent()) != null;)
                using (r) if (CrashEvents.Parse(r.ToXml()) is { } ev) found.Add(ev);
            foreach (var ev in found.OrderBy(e => e.Id == 1001 ? 0 : 1).ThenBy(e => e.Time)) Process(ev);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException) { LastError = ex.Message; }
    }

    /// <summary>Classify, decide, apply and record one event. Null = ignored (uninteresting, duplicate or already seen).</summary>
    public CrashRecord? Process(CrashEvent ev)
    {
        CrashRecord rec; CrashAction act;
        lock (_gate)
        {
            string key = $"{ev.Channel}:{ev.RecordId}";
            if (_seen.Contains(key)) return null;
            var c = CrashEvents.Classify(ev, _tpc);
            if (c == null) return null;
            _seen.Add(key);
            // Kernel-Power 41 and bugcheck 1001 describe the same crash: keep the richer 1001
            if (c.Kind == CrashEvents.UnexpectedShutdown && _events.Any(e => e.Kind == CrashEvents.Bugcheck && Math.Abs((e.Time - ev.Time).TotalSeconds) <= 900)) { Save(); return null; }
            string detail = c.Detail, suspect = c.Suspect; uint? code = c.Bugcheck;
            if (c.Kind == CrashEvents.UnexpectedShutdown && code == null && CrashEvents.NewestMinidump(_dumpDir) is { } dump
                && CrashEvents.ReadMinidump(dump) is { } m && Math.Abs((File.GetLastWriteTimeUtc(dump) - ev.Time).TotalSeconds) < 900)
            {
                code = m.Code; suspect = CrashEvents.BugcheckSuspect(code); detail += $" (minidump {CrashEvents.Hex(m.Code)})";
            }
            var applied = _applied(); bool stock = CrashDecision.IsStock(applied);
            rec = new(ev.RecordId, ev.Channel, ev.Time, c.Kind, suspect, detail, code is { } b ? CrashEvents.Hex(b) : null, c.Core,
                stock ? null : applied!.Value.Core, stock ? null : applied!.Value.MemNvml, "", false, null);
            act = CrashDecision.Decide(_cfg(), rec, _events, applied);
            rec = rec with { ActionText = Apply(act), Counts = act.Counts, Step = act.Step };
            _events.Add(rec);
            if (_events.Count > MaxEvents) _events = _events[^MaxEvents..];
            if (_seen.Count > MaxSeen) _seen = _seen[^MaxSeen..];
            Save();
        }
        Acted?.Invoke(rec, act);
        return rec;
    }

    /// <summary>Carry out <paramref name="a"/> through the GPU control and the persistence callbacks; returns the text to record.</summary>
    string Apply(CrashAction a)
    {
        if (a.Kind == CrashDecision.None) return a.Text;
        try
        {
            var gpu = _gpu();
            if (a.Kind == CrashDecision.Backoff)
            {
                gpu?.SetOffset(ClockKind.Core, a.NewCore);
                gpu?.SetOffset(ClockKind.Memory, a.NewMemNvml);
                _save(a.NewCore, a.NewMemNvml);
            }
            else { gpu?.ResetAll(); _revert(); }
            return gpu == null ? a.Text + " (no GPU control: saved state only)" : a.Text;
        }
        catch (Exception ex) { return a.Text + $" (applying failed: {ex.Message})"; }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    sealed record Store(List<string>? Seen, List<CrashRecord>? Events);

    void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var s = JsonSerializer.Deserialize<Store>(File.ReadAllText(_file), Json);
            _seen = [.. (s?.Seen ?? []).TakeLast(MaxSeen)];
            _events = [.. (s?.Events ?? []).TakeLast(MaxEvents)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { LastError = ex.Message; }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Store(_seen, _events), Json));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; }
    }
}
