using Glint.Tuning;

namespace Glint.Core;

/// <summary>A notification as it was posted. Shown = it passed the user's switches (Muted names the one that stopped it).</summary>
public sealed record Notice(DateTime Time, string Category, string Level, string Title, string Text, string? Page, bool Shown, string? Muted);

/// <summary>What a part of Glint wants to tell the user. Page = the dashboard page a click opens. A draft with a Key is not
/// posted again within QuietSec of the last one with that key (<see cref="Notifications.Once"/> = once per run).</summary>
public readonly record struct NoticeDraft(string Category, string Level, string Title, string Text, string? Page = null, string? Key = null, double QuietSec = 0);

/// <summary>
/// The one place Windows notifications come from. Parts of Glint post a <see cref="NoticeDraft"/> when something happens that the
/// user should know about with the dashboard closed (a crash, an overclock that was not applied, fans at fail-safe, a device that
/// stopped, an update); the user's switches (config.notifications) decide whether it is shown; the tray icon is the only sink.
/// Passive: no thread, timer or file. The last <see cref="Keep"/> notices stay in memory for the control API.
/// </summary>
public sealed class Notifications
{
    /// <summary>Windows' own limits for a tray notification (NOTIFYICONDATA szInfoTitle / szInfo, less the terminator).</summary>
    public const int TitleMax = 63, TextMax = 255;
    public const int Keep = 50;
    public const double Once = double.PositiveInfinity;
    public const string Crashes = "crashes", Tuning = "tuning", Cooling = "cooling", Devices = "devices", Updates = "updates", Test = "test";
    /// <summary>The categories with a switch of their own, in the order the dashboard lists them.</summary>
    public static readonly string[] Categories = [Crashes, Tuning, Cooling, Devices, Updates];

    readonly Func<NotificationOptions?> _options;
    readonly Func<DateTime> _now;
    readonly object _gate = new();
    readonly List<Notice> _recent = [];
    readonly List<Notice> _waiting = [];                       // posted before the tray icon existed (startup)
    readonly Dictionary<string, DateTime> _lastByKey = [];
    Action<Notice>? _sink;

    public Notifications(Func<NotificationOptions?> options, Func<DateTime>? now = null)
    {
        _options = options; _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Newest last. Includes the ones a switch kept from being shown.</summary>
    public IReadOnlyList<Notice> Recent { get { lock (_gate) return [.. _recent]; } }

    /// <summary>The tray icon attaches once; notices posted while Glint was still starting are delivered then.</summary>
    public void Attach(Action<Notice> sink)
    {
        Notice[] late;
        lock (_gate) { _sink = sink; late = [.. _waiting]; _waiting.Clear(); }
        foreach (var n in late) Deliver(sink, n);
    }

    public Notice? Post(NoticeDraft? draft) => draft is { } d ? Post(d) : null;

    /// <summary>Null = dropped as a repeat of its key. Otherwise the notice as recorded (Shown says whether it went out).</summary>
    public Notice? Post(NoticeDraft d)
    {
        var now = _now();
        Notice n; Action<Notice>? sink;
        lock (_gate)
        {
            if (d.Key != null)
            {
                if (d.QuietSec > 0 && _lastByKey.TryGetValue(d.Key, out var last) && (now - last).TotalSeconds < d.QuietSec) return null;
                if (_lastByKey.Count >= 256) _lastByKey.Clear();   // keys are few (sensors, fans, versions); a runaway source must not grow it
                _lastByKey[d.Key] = now;
            }
            var o = _options() ?? new NotificationOptions();
            string? muted = !o.Enabled ? "notifications are off" : !o.Allows(d.Category) ? $"{d.Category} notifications are off" : null;
            n = new Notice(now, d.Category, d.Level, Fit(d.Title, TitleMax), Fit(d.Text, TextMax), d.Page, muted == null, muted);
            _recent.Add(n);
            if (_recent.Count > Keep) _recent.RemoveAt(0);
            if (!n.Shown) return n;
            sink = _sink;
            if (sink == null) { _waiting.Add(n); if (_waiting.Count > 5) _waiting.RemoveAt(0); return n; }
        }
        Deliver(sink, n);
        return n;
    }

    static void Deliver(Action<Notice> sink, Notice n) { try { sink(n); } catch { } }

    /// <summary>One line of whitespace-normalised text within Windows' limit; cut at a word with an ellipsis only when it must be.</summary>
    public static string Fit(string? s, int max)
    {
        s = string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (s.Length <= max) return s;
        int cut = s.LastIndexOf(' ', max - 1);
        if (cut < max / 2) cut = max - 1;
        return s[..cut].TrimEnd(' ', ',', ';', ':', '.') + "…";
    }
}

/// <summary>
/// The wording of every notification, pure so each is checked against Windows' length limits without any hardware.
/// Short on purpose: a title that says what happened, one or two sentences on what Glint did and what the user can do.
/// </summary>
public static class NoticeText
{
    const string Info = "info", Warn = "warning", Err = "error";
    const string C = Notifications.Crashes, T = Notifications.Tuning, Cool = Notifications.Cooling, D = Notifications.Devices, U = Notifications.Updates;

    static string End(string s) { s = s.Trim(); return s.Length == 0 || s[^1] is '.' or '!' or '?' or '…' ? s : s + "."; }
    static string Off(int v) => v.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ crashes

    /// <summary>Crash watch saw something. Null for an event that is part of an incident already reported, or that happened at stock.</summary>
    public static NoticeDraft? Crash(CrashRecord rec, CrashAction act)
    {
        if (act.Quiet) return null;
        string what = rec.Detail.Trim().TrimEnd('.');
        if (rec.Suspect == "cpu")
            return new(C, Warn, "CPU hardware error detected",
                $"{what}. Glint changed nothing: if it keeps happening, loosen {(rec.Core is int c ? $"core {c}'s" : "the")} Curve Optimizer value.", "tuning", $"crash-cpu-{rec.Core}", 3600);
        if (rec.Suspect != "gpu")
            return new(C, Warn, "Crash detected", $"{what}. The cause is unknown, so Glint changed nothing.", "tuning");
        if (act.Kind == CrashDecision.Revert)
            return new(C, Warn, "GPU crash: overclock removed", $"{what}. {End(act.Text)}", "tuning");
        if (act.Kind == CrashDecision.Backoff)
            return new(C, Warn, "GPU crash: overclock lowered",
                $"{what}. {(act.Step == "core" ? $"Core offset {Off(rec.AppliedCore ?? 0)} → {Off(act.NewCore)} MHz" : $"Memory offset lowered by {CrashDecision.MemStepAfterburner}")}. It is lowered again if the crashes continue.", "tuning");
        if (rec.Kind == CrashEvents.GpuAppCrash && !act.Counts)
            return new(C, Warn, "Game crash in the GPU driver", $"{what}. One crash proves nothing, so Glint is watching: a second within 24 hours lowers the overclock.", "tuning");
        return new(C, Warn, "GPU crash detected", $"{what}. {End(act.Text)}", "tuning");
    }

    /// <summary>What happened to the saved tune when Glint started. Null when everything was applied as usual.</summary>
    /// <param name="gpuNote">What <see cref="TuningService.OnStartup"/> returned.</param>
    /// <param name="gpuError">Why applying the GPU overclock threw (another tool holds the card, no GPU...).</param>
    /// <param name="cpuCrashNote">What <see cref="TuningService.OnCpuCrash"/> returned.</param>
    /// <param name="cpuHeldBack">The profile has a CPU undervolt and it was left off because of the crash.</param>
    /// <param name="cpuError">Why applying the CPU undervolt failed.</param>
    public static NoticeDraft? TuningStartup(bool crashed, string? gpuNote, string? gpuError, string? cpuCrashNote, bool cpuHeldBack, string? cpuError)
    {
        const string crashWhy = "the last shutdown was a crash";
        var more = new List<string>();
        if (crashed)
        {
            bool gpu = gpuNote?.Contains(crashWhy, StringComparison.Ordinal) == true;
            string head = "Windows was not shut down cleanly";
            head += gpu && cpuHeldBack ? ", so the GPU overclock and CPU undervolt were not applied."
                  : gpu ? ", so the GPU overclock was not applied."
                  : cpuHeldBack ? ", so the CPU undervolt was not applied."
                  : " (a crash, freeze or power loss). Glint had no overclock or undervolt to hold back.";
            string? hint = gpu && cpuHeldBack ? "Pick the profile again to apply them." : gpu || cpuHeldBack ? "Pick the profile again to apply it." : null;
            if (gpuNote != null && !gpu) more.Add(End(gpuNote));
            if (gpuError != null) more.Add(End("GPU overclock not re-applied: " + gpuError));
            if (cpuCrashNote != null) more.Add(End(cpuCrashNote));
            if (cpuError != null) more.Add(End(cpuError));
            // the hint is the first thing to go when a long profile name would push the text past Windows' limit
            string rest = string.Join(' ', more);
            if (hint != null && head.Length + hint.Length + rest.Length + 2 <= Notifications.TextMax) head += " " + hint;
            return new(C, Warn, "Started after a crash", rest.Length > 0 ? head + " " + rest : head, "tuning");
        }
        if (gpuNote != null) more.Add(End(gpuNote));
        if (gpuError != null) more.Add(End("GPU overclock not re-applied: " + gpuError));
        if (cpuCrashNote != null) more.Add(End(cpuCrashNote));
        if (cpuError != null) more.Add(End(cpuError));
        if (more.Count == 0) return null;
        bool gpuOff = gpuError != null || gpuNote?.StartsWith("Saved overclock not applied", StringComparison.Ordinal) == true;
        return new(T, Warn, gpuOff ? "Overclock not applied" : cpuError != null ? "CPU undervolt not applied" : "Tuning", string.Join(' ', more), "tuning");
    }

    // ------------------------------------------------------------------ tuning

    /// <summary>Switching profile (or picking it again) could not apply its overclock.</summary>
    public static NoticeDraft ProfileOc(string note) => new(T, Warn, "Overclock not applied", End(note), "tuning", "oc:" + note, 60);

    /// <summary>The CPU undervolt of a profile could not be applied.</summary>
    public static NoticeDraft CpuNotApplied(string note) => new(T, Warn, "CPU undervolt not applied", End(note), "tuning", "co:" + note, 60);

    /// <summary>An AI tune ended: its profile and what is in it, or why it ended early.</summary>
    public static NoticeDraft TuneDone(string profile, string? error, (int Core, int MemAb, int? PowerPct)? gpu, double? gainPct, string? cpu)
    {
        if (error != null) return new(T, Warn, "Tune ended early", $"{profile}: {End(error)}", "tuning");
        var parts = new List<string>();
        if (gpu is { } g)
            parts.Add($"GPU core {Off(g.Core)} / memory {Off(g.MemAb)}{(g.PowerPct is int p ? $" at {p}% power" : "")}{(gainPct is double x ? $", {x.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}% better than stock" : "")}.");
        if (cpu != null) parts.Add($"CPU undervolt {cpu}.");
        return new(T, Info, "Tune finished", $"{profile} is ready. {string.Join(' ', parts)}".TrimEnd(), "tuning");
    }

    /// <summary>A GPU overclock was measured against stock outside an AI run (the run's own notice carries the result otherwise).</summary>
    public static NoticeDraft Verified(string message) => new(T, Info, "GPU overclock checked against stock", End(message), "tuning");

    public static NoticeDraft CpuSearchDone(string profile, string values) => new(T, Info, "CPU undervolt search finished", $"{profile}: {values}.", "tuning");

    /// <summary>The CPU search stopped by itself (watchdog, error, unstable CPU): never posted for a stop the user asked for.</summary>
    public static NoticeDraft CpuSearchStopped(string reason) => new(T, Warn, "CPU undervolt search stopped", End(char.ToUpperInvariant(reason[0]) + reason[1..]), "tuning");

    // ------------------------------------------------------------------ cooling

    public static NoticeDraft SensorLost(string sensor, IReadOnlyCollection<string> rules, int failsafe) =>
        new(Cool, Warn, "Sensor lost: fans at fail-safe speed",
            $"\"{sensor}\" is not reporting, so the rule{(rules.Count == 1 ? "" : "s")} {string.Join(", ", rules.Take(3))}{(rules.Count > 3 ? $" and {rules.Count - 3} more" : "")} run{(rules.Count == 1 ? "s" : "")} at {failsafe}% until it is back.", "cooling", "sensor:" + sensor, 600);

    public static NoticeDraft FanContested(string fan, string why) =>
        new(Cool, Warn, "Another program is controlling a fan", $"{fan}: {End(why)}", "cooling", "fan:" + fan, Notifications.Once);

    public static NoticeDraft LoopError(string message, int failsafe) =>
        new(Cool, Err, "Fan control problem", $"{End(message)} Fans are held at the fail-safe speed ({failsafe}%) until it clears.", "cooling", "loop", 600);

    public static NoticeDraft NoCpuTemp() =>
        new(Cool, Warn, "CPU temperature unavailable", "Glint needs admin rights and the PawnIO driver to read it (run install.ps1). CPU-driven curves are at fail-safe speed.", "setup", "no-cpu-temp", Notifications.Once);

    // ------------------------------------------------------------------ devices and Glint itself

    public static NoticeDraft DeviceStopped(string device, string error) =>
        new(D, Warn, "Lighting device stopped responding", $"{device}: {End(error)} Its lighting is paused; restart Glint to try again.", "devices", "dev:" + device, Notifications.Once);

    public static NoticeDraft PluginApproval(string plugin) =>
        new(D, Info, "A plugin needs your approval", $"{plugin} wants SMBus access. Approve it on the Plugins page, or leave it switched off.", "plugins", "approve:" + plugin, Notifications.Once);

    public static NoticeDraft HotkeyTaken(string hotkey) =>
        new(D, Warn, "Overlay hotkey is taken", $"{hotkey} is already used by another app. Pick a different one on the Game overlay page.", "overlay", "hotkey:" + hotkey, Notifications.Once);

    public static NoticeDraft ConfigInvalid(string error) =>
        new(D, Warn, "config.json was not loaded", $"{End(error)} Glint keeps the settings it had.", "settings", "config", 30);

    // ------------------------------------------------------------------ updates

    public static NoticeDraft UpdateAvailable(string version) =>
        new(U, Info, $"Glint {version} is available", "Install it from the tray menu or the Settings page.", "settings", "update:" + version, Notifications.Once);

    /// <summary>Install progress worth a notification (the dashboard may be closed): the hand-over to the setup, and a failure.</summary>
    public static NoticeDraft? Updater(UpdateStatus s) =>
        s.Message == null ? null
        : s.State == "failed" ? new(U, Warn, "Glint update", s.Message, "settings")
        : s.State == "installing" ? new(U, Info, "Glint update", s.Message, "settings")
        : null;

    public static NoticeDraft TestNotice() =>
        new(Notifications.Test, Info, "Glint notifications work", "This is what a notification from Glint looks like. Click it to open the dashboard.", "settings");
}

/// <summary>
/// Turns what the fan loop already knows (sensors a rule lost, fans another program took, a loop error) into notifications:
/// once when a problem has lasted <see cref="AfterSec"/> (start-up readings are often empty for a moment), again only after
/// it cleared and came back. Costs nothing while all is well: the loop passes empty collections and every branch is skipped.
/// </summary>
public sealed class CoolingAlerts
{
    public const double AfterSec = 20;
    readonly Dictionary<string, double> _lostSince = [];
    readonly HashSet<string> _lostTold = [];
    readonly HashSet<string> _contestedTold = [];
    string _error = ""; double _errorSince; bool _errorTold;

    /// <param name="lost">(rule, sensor) pairs the fan engine could not read this pass.</param>
    /// <param name="contested">Fans another program is driving (id → why), or null when there are none.</param>
    /// <param name="loopError">The fan loop's error this pass ("" = it ran).</param>
    public void Check(double nowSec, IReadOnlyList<(string Rule, string Sensor)> lost, IReadOnlyDictionary<string, string>? contested,
        string loopError, int failsafe, Func<string, string> fanName, Action<NoticeDraft> post)
    {
        if (lost.Count > 0 || _lostSince.Count > 0)
        {
            foreach (var (_, sensor) in lost) _lostSince.TryAdd(sensor, nowSec);
            foreach (var gone in _lostSince.Keys.Where(k => !lost.Any(l => l.Sensor == k)).ToList()) { _lostSince.Remove(gone); _lostTold.Remove(gone); }
            foreach (var (sensor, since) in _lostSince)
                if (nowSec - since >= AfterSec && _lostTold.Add(sensor))
                    post(NoticeText.SensorLost(sensor, lost.Where(l => l.Sensor == sensor).Select(l => l.Rule).Distinct().ToList(), failsafe));
        }
        if (contested is { Count: > 0 })
        {
            foreach (var (id, why) in contested)
                if (_contestedTold.Add(id)) post(NoticeText.FanContested(fanName(id), why));
            if (_contestedTold.Count > contested.Count) _contestedTold.RemoveWhere(id => !contested.ContainsKey(id));
        }
        else if (_contestedTold.Count > 0) _contestedTold.Clear();

        if (loopError.Length == 0) { _error = ""; _errorTold = false; return; }
        if (_error.Length == 0) { _error = loopError; _errorSince = nowSec; }
        if (!_errorTold && nowSec - _errorSince >= AfterSec) { _errorTold = true; post(NoticeText.LoopError(loopError, failsafe)); }
    }
}
