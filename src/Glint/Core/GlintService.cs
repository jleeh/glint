using System.Diagnostics;

namespace Glint.Core;

public sealed class GlintService : IDisposable
{
    public Rig Rig { get; }
    public LightingEngine Lighting { get; }
    public FanEngine Engine { get; } = new();
    public GlintConfig Config { get; private set; }
    public HomeAssistantBridge HomeAssistant { get; }
    /// <summary>Optional update check; idle until the tray app calls Start (tests never do).</summary>
    public UpdateChecker Updates { get; }
    /// <summary>One-click / automatic install of a verified release (idle until asked; the tray app starts its background part).</summary>
    public Updater Updater { get; }
    /// <summary>Overclocking (off by default): opens nothing until tuning is enabled and something asks.</summary>
    public Glint.Tuning.TuningService Tuning { get; }
    /// <summary>Crash watch: event-log subscriptions that exist only while an overclock is applied and crash watch is on.</summary>
    public Glint.Tuning.CrashWatch Crashes { get; }
    /// <summary>CPU Curve Optimizer search (CoreCycler): its thermal watchdog exists only while a search runs.</summary>
    public Glint.Tuning.CpuTuning Cpu { get; }
    /// <summary>"Tune with AI": Claude Code (the user's own login) driving the tuning tools over Glint's MCP server.</summary>
    public Glint.Tuning.AiTuner Ai { get; }
    /// <summary>The AI's fan tools (its own profile + curve edits on it only); every change goes through ConfigValidator then <see cref="Apply"/>.</summary>
    public Glint.Tuning.AiFans AiFans { get; }
    /// <summary>Temperatures and fan-rule duty over the last 24 h of real use (for the AI's fan tuning).</summary>
    public Glint.Tuning.ThermalHistory Thermal { get; } = new(Path.Combine(GlintConfig.Dir, "thermal-history.json"));
    /// <summary>Windows notifications: every part of Glint posts here; the tray icon shows what the user's switches allow.</summary>
    public Notifications Notify { get; }
    readonly CoolingAlerts _alerts = new();
    readonly Action<NoticeDraft> _postNotice;
    readonly Func<string, string> _fanName;
    static readonly (string Rule, string Sensor)[] NoLost = [];

    public IReadOnlyDictionary<string, double> Sensors { get; private set; } = new Dictionary<string, double>();
    public IReadOnlyDictionary<string, int> Rpm { get; private set; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Duty { get; private set; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, string> Explain { get; private set; } = new Dictionary<string, string>();
    public string LastError { get; private set; } = "";
    public event Action? Updated;
    /// <summary>Raised after a new config is applied (any thread).</summary>
    public event Action<GlintConfig>? ConfigApplied;
    /// <summary>The in-game overlay, when this process runs one (tray app, tests).</summary>
    public GameOverlay.OverlayController? Overlay { get; set; }
    public bool NoSave { get; set; }

    readonly Thread _thread;
    volatile bool _stop;
    readonly FileSystemWatcher? _watch;
    DateTime _selfWrite;
    const int UnassignedDuty = 60;

    public GlintService(GlintConfig cfg, Rig? rig = null)
    {
        (cfg.HomeAssistant ??= new()).Normalize();
        Config = cfg;
        Notify = new Notifications(() => Config.Notifications);
        _postNotice = d => Notify.Post(d);
        _fanName = id => Config.FanNames.TryGetValue(id, out var n) && n.Length > 0 ? n : Rig.Fans.FirstOrDefault(f => f.Id == id)?.Name ?? id;
        Plugins.PluginHost.Disabled = () => Config.DisabledPlugins;
        Rig = rig ?? new Rig(cfg);
        Lighting = new LightingEngine(Rig, cfg, () => Sensors);
        Lighting.DeviceStopped += (name, error) => Notify.Post(NoticeText.DeviceStopped(name, error));
        Lighting.Start();
        HomeAssistant = new HomeAssistantBridge(this);
        HomeAssistant.Configure(cfg.HomeAssistant);
        Updates = new UpdateChecker(() => Config);
        Updater = new Updater(Updates, () => Config);
        Updates.Changed += i => { if (i is { Available: true, Latest: { } v }) Notify.Post(NoticeText.UpdateAvailable(v)); };
        Updater.Changed += s => Notify.Post(NoticeText.Updater(s));   // install progress / errors (the dashboard may be closed)
        Tuning = new Glint.Tuning.TuningService(() => Config, Glint.Tuning.TuningHardware.OpenGpu, Glint.Tuning.TuningHardware.OpenStress,
            () => Sensors.TryGetValue("gpu.memjunction", out var mj) ? mj : null, boost => Engine.GpuBoost = boost ?? 0);
        Crashes = new Glint.Tuning.CrashWatch(() => Config.Tuning ?? new(), () => Tuning.OpenedGpu, () => Tuning.AppliedNow,
            (core, memNvml) => Tuning.BackOff(core, (int)Math.Round(memNvml * (Tuning.AppliedNow?.MemRatio ?? Tuning.State.MemRatio ?? 0.5)), "crash watch"),
            () => Tuning.Stock($"{Config.Tuning?.MaxCrashes} GPU crashes within {Config.Tuning?.CrashWindowDays} days: back to stock until you re-tune"));
        // a crash is always told: what crash watch saw and what it did about it
        Crashes.Acted += (rec, act) => Notify.Post(NoticeText.Crash(rec, act));
        Cpu = new Glint.Tuning.CpuTuning(() => Config, () => Sensors.TryGetValue("cpu", out var c) ? c : null);
        Tuning.ActiveProfile = () => Config.ActiveProfile;
        Ai = new Glint.Tuning.AiTuner(Environment.ProcessPath ?? "Glint.exe");
        Ai.SummaryFor = prof => Tuning.NotesFor(prof);
        Ai.Ended += () =>
        {
            var runProfile = Tuning.CurrentGoal?.ProfileName; var since = Ai.StartedAt; bool gpuRun = Tuning.CurrentGoal?.Gpu == true;
            bool userStopped = _stopRequested; _stopRequested = false;
            var doneProfile = runProfile ?? Ai.TuneGoal?.ProfileName; var aiError = Ai.Error;
            if (doneProfile is { } np)
                try { var text = Ai.Summary ?? (Ai.Error != null ? "The AI run ended early: " + Ai.Error : null); Tuning.EditNotes(np, n => n.Ai = text); } catch { }
            Tuning.CurrentGoal = null; Cpu.Goal = null; Tuning.Stop();   // a run that ends mid-test leaves the card at stock
            // the user is told once, when the whole run is over: after the check against stock below if one follows.
            // A run the user stopped needs no notification.
            void Told() { if (!userStopped) Notify.Post(TuneDoneNotice(doneProfile, aiError)); }
            // every run ends with its result in its profile: if the AI didn't save the GPU result itself, Glint saves the best tested one
            if (runProfile != null && since is DateTime t0 && gpuRun)
                _ = Task.Run(() =>
                {
                    bool verifying = false;
                    try
                    {
                        for (int i = 0; i < 40 && Tuning.IsBusy; i++) Thread.Sleep(500);
                        if (Tuning.SaveBestFromRun(runProfile, t0) is { } note)
                        {
                            Rig.Log.Add("tuning: " + note);
                            if (Config.ActiveProfile == runProfile) Tuning.OnProfileChanged(runProfile);
                        }
                        // every GPU result is checked against stock back to back before the tune counts as done
                        if (!userStopped && !Tuning.StockMode && !Tuning.VerifiedSince(runProfile, t0) && Tuning.HasGpuResult(runProfile))
                        {
                            for (int i = 0; i < 40 && Tuning.IsBusy; i++) Thread.Sleep(500);
                            _tuneDone = () => Notify.Post(TuneDoneNotice(doneProfile, aiError));   // posted when the check ends (JobEnded)
                            verifying = true;
                            Tuning.StartVerify(runProfile);
                        }
                    }
                    catch (Exception e) { LastError = "tuning: " + e.Message; if (verifying) { verifying = false; _tuneDone = null; } }
                    if (!verifying) Told();
                });
            else Told();
        };
        Tuning.JobEnded += name => { if (name == "gpu verify" && Interlocked.Exchange(ref _tuneDone, null) is { } told) told(); };
        // a check against stock the user started by hand; inside a tune, the run's own notification carries the result
        Tuning.Verified += (_, message) => { if (!Ai.Running && _tuneDone == null) Notify.Post(NoticeText.Verified(message)); };
        Cpu.LoadProbe = () =>
        {
            Rig.WantCatalog();
            var cpuHw = Rig.Catalog?.Hardware.FirstOrDefault(h => h.Type == "Cpu");
            if (cpuHw == null) return (null, null, null, null);
            var all = cpuHw.Sensors.Concat(cpuHw.Sub.SelectMany(x => x.Sensors)).ToList();
            double? pw = all.FirstOrDefault(x => x.Type == "Power" && x.Name.Contains("Package"))?.Value;
            var eff = all.Where(x => x.Type == "Clock" && x.Name.StartsWith("Core #") && x.Name.Contains("Effective")).Select(x => x.Value).OfType<double>().ToList();
            var clk = eff.Count > 0 ? eff : all.Where(x => x.Type == "Clock" && x.Name.StartsWith("Core #")).Select(x => x.Value).OfType<double>().ToList();
            double? load = all.FirstOrDefault(x => x.Type == "Load" && x.Name.Contains("Total"))?.Value;
            return (pw, clk.Count > 0 ? clk.Average() : null, clk.Count > 0 ? clk.Max() : null, load);
        };
        Cpu.LoadTested += (profile, advice, bench, verify) =>
        {
            var p = profile != null && Config.Profiles.ContainsKey(profile) ? profile : Config.ActiveProfile;
            try { if (!verify) Tuning.SavePbo(p, advice); Tuning.SaveCpuBench(p, bench, verify); } catch (Exception e) { LastError = "tuning: " + e.Message; }
        };
        Cpu.CoAppliedChanged += why => { try { Tuning.MarkCpuApplied(why == null ? null : Config.ActiveProfile); } catch { } };
        Cpu.PmProbe = () => Rig.ReadPm();
        Cpu.PboAdvice = () => { var p = Tuning.CurrentGoal?.ProfileName ?? Config.ActiveProfile; return Tuning.State.Pbo.TryGetValue(p, out var a) ? a : null; };
        Cpu.TargetProfile = () => Tuning.CurrentGoal?.ProfileName ?? Config.ActiveProfile;
        Cpu.Stopped += () => _ = Task.Run(() =>
        {
            // a search that stopped by itself (watchdog, error, unstable CPU) outside an AI run, which reports its own end
            if (!Ai.Running && Cpu.State.StoppedReason is { Length: > 0 } why && !why.StartsWith("stopped by", StringComparison.Ordinal) && why != "tuning was turned off")
                Notify.Post(NoticeText.CpuSearchStopped(why));
            try
            {
                if (Cpu.State.Search?.Profile is { } sp && Config.Profiles.ContainsKey(sp))
                {
                    var keep = Tuning.CpuFor(sp);
                    var reason = Cpu.State.StoppedReason ?? "stopped";
                    Tuning.EditNotes(sp, n => n.Cpu = $"**CPU undervolt:** not changed: the search stopped ({reason}). The profile keeps {(keep != null ? Glint.Tuning.CpuTuning.CoText(keep) : "the BIOS values")}.");
                }
            }
            catch { }
            try { if (Config.Tuning?.Enabled == true && !Tuning.StockMode && Tuning.CpuFor(Config.ActiveProfile) is { } co) Cpu.ApplyCo(co, "search stopped"); }
            catch (Exception e) { LastError = "cpu: " + e.Message; }
        });
        Cpu.Finished += (profile, final) =>
        {
            var p = profile != null && Config.Profiles.ContainsKey(profile) ? profile : Config.ActiveProfile;
            Tuning.SaveCpu(p, final);
            var cpuState = Cpu.State;
            bool inAiRun = Ai.Running;   // the run's own notification covers a search it started
            _ = Task.Run(() =>
            {
              try
              {
                var biosVals = Cpu.BiosCo(); var saved = final; bool backedOff = false;
                void Note() { try { Tuning.EditNotes(p, n => n.Cpu = Glint.Tuning.CpuTuning.ResultNote(biosVals, saved, cpuState, Tuning.State.CpuBench.TryGetValue(p, out var b) ? b : null, backedOff)); } catch { } }
                if (p != Config.ActiveProfile) { Note(); return; }
                try
                {
                    var mode = Cpu.State.Search?.Mode ?? "performance";
                    void Cool() { for (int i = 0; i < 36 && (Sensors.TryGetValue("cpu", out var tc) ? tc : 0) > 65; i++) Thread.Sleep(5000); }
                    // the profile's stock vs tuned figure: the BIOS values vs the result, alternated inside the same benchmark runs (separate
                    // runs differ by up to ~20%); performance mode backs an undervolt that measures slower off, 5 at a time
                    var bios = Cpu.BiosCo();
                    var vals = final;
                    for (int round = 0; round < 3 && bios != null; round++)
                    {
                        Cool();
                        if (Cpu.CompareCo(bios, vals) is not { Stock: { } st, Tuned: { } tu }) break;
                        Tuning.SaveCpuBench(p, st, tuned: false); Tuning.SaveCpuBench(p, tu, tuned: true);
                        if (mode != "performance" || Glint.Tuning.CpuTuning.NotSlower(st, tu)) break;
                        vals = vals.Select(v => Math.Min(0, v + 5)).ToArray();
                        backedOff = true;
                        if (vals.All(v => v == 0))
                        {
                            Tuning.ClearCpu(p); Cpu.ApplyCo(null, "slower than stock"); LastError = "cpu: every undervolt tried measured slower than the BIOS values, so the profile keeps them (performance mode)";
                            Tuning.EditNotes(p, n => n.Cpu = "**CPU undervolt:** not changed. Every undervolt tried measured slower than the BIOS values (performance mode), so the profile keeps the BIOS values.");
                            return;
                        }
                        Tuning.SaveCpu(p, vals);
                    }
                    saved = vals;
                    Cpu.ApplyCo(vals, "search finished");
                }
                catch (Exception e) { LastError = "cpu: " + e.Message; }
                Note();
              }
              finally
              {
                // told once the profile holds what it will keep (the comparison with the BIOS values can back the result off)
                if (!inAiRun)
                    Notify.Post(NoticeText.CpuSearchDone(p, Tuning.CpuFor(p) is { } kept ? Glint.Tuning.CpuTuning.CoText(kept) : "no undervolt measured faster, so the profile keeps the BIOS values"));
              }
            });
        };
        // a CPU search that finished before profile notes existed: its record is written from the search's saved state
        try
        {
            var cs = Cpu.State;
            if (cs is { Phase: "done", Final: { } fin, Search.Profile: { } cp } && Tuning.CpuFor(cp) is { } co && co.SequenceEqual(fin)
                && !(Tuning.State.Notes.TryGetValue(cp, out var old) && old.Cpu != null))
            {
                var bios = Cpu.BiosCo();
                Tuning.EditNotes(cp, n => { n.Cpu = Glint.Tuning.CpuTuning.ResultNote(bios, co, cs, Tuning.State.CpuBench.GetValueOrDefault(cp), false); if (cs.FinishedAt is DateTime fa) n.Time = fa; });
            }
        }
        catch (Exception e) { LastError = "cpu: " + e.Message; }
        Ai.GoalChanged += g => { Tuning.CurrentGoal = g; Cpu.Goal = g; };
        AiFans = new Glint.Tuning.AiFans(() => Config, c => Apply(c),
            () => new ValidationContext { Sensors = Sensors.Keys.ToList(), Fans = Rig.Fans.Select(f => f.Id).ToList(), PluginEffects = Plugins.PluginHost.Effects.Select(e => e.Name).ToList(), Previous = Config },
            Tuning, () => Duty, () => Rpm, () => { var a = Glint.Tuning.CpuFamilies.For(Glint.Tuning.CpuHardware.CpuName()).AbortC; return a > 0 ? a : 85; },
            () => { var d = new Dictionary<string, string>(); foreach (var f in Rig.Fans) d[f.Id] = f.Name; return d; });
        // goal-based tests read the real GPU fan duty / rpm (the AI profile's curve drives them)
        Tuning.FanProbe = () =>
        {
            var ids = Duty.Keys.Where(k => k.StartsWith("gpu", StringComparison.Ordinal)).ToList();
            if (ids.Count == 0) return null;
            var rp = ids.Where(Rpm.ContainsKey).Select(k => (double)Rpm[k]).ToList();
            return (ids.Average(k => (double)Duty[k]), rp.Count > 0 ? rp.Average() : null);
        };
        // watch only while Glint has an overclock applied (no event-log subscriptions otherwise)
        Tuning.Applied += (core, mem) => { if (core == 0 && mem == 0) Crashes.Stop(); else Crashes.Start(); };
        _thread = new Thread(Loop) { IsBackground = true, Name = "Glint fans", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        try
        {
            UserFs.CreateDirectory(GlintConfig.Dir);
            _watch = new FileSystemWatcher(GlintConfig.Dir, "config.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            _watch.Changed += (_, _) => ReloadSoon();
            _watch.Renamed += (_, _) => ReloadSoon();
            _watch.EnableRaisingEvents = true;
        }
        catch { }
    }

    int _reloadPending;
    void ReloadSoon()
    {
        if ((DateTime.UtcNow - _selfWrite).TotalMilliseconds < 1500) return;
        if (Interlocked.Exchange(ref _reloadPending, 1) == 1) return;
        Task.Delay(400).ContinueWith(_ =>
        {
            _reloadPending = 0;
            try { Apply(GlintConfig.Load(), save: false); }
            catch (Exception e) { LastError = "config: " + e.Message; Notify.Post(NoticeText.ConfigInvalid(e.Message)); }
        });
    }

    Action? _tuneDone;   // posts the "tune finished" notification once the run's check against stock has ended

    /// <summary>The notification for a tune that ended: the profile and what it now holds, or why the run ended early.</summary>
    NoticeDraft? TuneDoneNotice(string? profile, string? error)
    {
        if (profile == null) return error == null ? null : NoticeText.TuneDone("AI tune", error, null, null, null);
        var (gpu, gain) = Tuning.ResultOf(profile);
        return NoticeText.TuneDone(profile, error, gpu, gain, Tuning.CpuFor(profile) is { } co ? Glint.Tuning.CpuTuning.CoText(co) : null);
    }

    /// <summary>Apply a profile's GPU overclock and CPU undervolt off the caller's thread; what could not be applied is told.</summary>
    void ApplyProfileTuning(string profile, bool cpuUnlessStock)
    {
        _ = Task.Run(() =>
        {
            try { if (Tuning.OnProfileChanged(profile) is { } note) Notify.Post(NoticeText.ProfileOc(note)); }
            catch (Exception e) { LastError = "tuning: " + e.Message; }
        });
        _ = Task.Run(() =>
        {
            try { if ((!cpuUnlessStock || !Tuning.StockMode) && Cpu.ApplyCo(Tuning.CpuFor(profile), "profile " + profile) is { } note) Notify.Post(NoticeText.CpuNotApplied(note)); }
            catch (Exception e) { LastError = "cpu: " + e.Message; }
        });
    }

    /// <summary>Plugins that wait for the user's approval (SMBus access): told once each, e.g. after an AI added one.</summary>
    public void NotifyPluginApprovals()
    {
        foreach (var s in Plugins.PluginHost.StatusSnapshot())
            if (s.NeedsApproval) Notify.Post(NoticeText.PluginApproval(s.Name));
    }

    /// <summary>Rename a fan profile (its overclock follows; ActiveProfile follows if it was active). Validated like every
    /// other change; returns an error text, or null when done. The one rename path for the dashboard and the control API.</summary>
    public string? RenameProfile(string from, string to)
    {
        to = (to ?? "").Trim();
        var c = System.Text.Json.JsonSerializer.Deserialize<GlintConfig>(System.Text.Json.JsonSerializer.Serialize(Config, GlintConfig.Json), GlintConfig.Json)!;
        if (!c.Profiles.TryGetValue(from ?? "", out var rules)) return $"no profile \"{from}\" (profiles: {string.Join(", ", c.Profiles.Keys)})";
        if (from == to) return null;
        if (ConfigValidator.ProfileNameError(to) is { } pe) return pe;
        if (c.Profiles.Keys.Any(k => k != from && k.Trim().Equals(to, StringComparison.OrdinalIgnoreCase))) return $"profile \"{to}\" already exists";
        // keep the profile's position in the list
        var ordered = c.Profiles.Select(kv => kv.Key == from ? KeyValuePair.Create(to, kv.Value) : kv).ToList();
        c.Profiles = new Dictionary<string, List<FanRule>>(ordered);
        if (c.ActiveProfile == from) c.ActiveProfile = to;
        var v = ConfigValidator.Check(c, new ValidationContext { Sensors = Sensors.Keys.ToList(), Previous = Config });
        if (!v.Ok) return string.Join("; ", v.Errors.Take(3));
        Tuning?.RenameProfile(from!, to);   // before Apply: Apply clears the overclock of a profile that disappeared
        Apply(c);
        return null;
    }

    /// <summary>Swap in a new config (from UI or file edit).</summary>
    /// <summary>Stop everything tuning is doing: the AI run, a GPU test, the CPU search and a load test.</summary>
    volatile bool _stopRequested;

    public object StopAll()
    {
        _stopRequested = true;   // the AI run's end handler then saves the best proven result but starts no new stress test
        try { Ai.Stop(); } catch { }
        try { Tuning.Stop(); } catch { }
        try { Cpu.StopLoadTest(); } catch { }
        try { Cpu.Stop("stopped by the user"); } catch { }
        return new { stopped = true };
    }

    /// <summary>One click back to stock: stop everything, GPU to stock clocks and default power, CPU back to its BIOS values, and
    /// stay there (also after a restart) until the user picks a profile or starts a tune. Fans keep following the active profile.</summary>
    public object UseStock()
    {
        Tuning.SetStockMode(true);   // first: what StopAll sets off (a stopped search re-applies the profile) must see stock mode
        StopAll();
        try { Tuning.Stock(); } catch { }
        var note = Cpu.ApplyCo(null, "stock");
        return new { stock = true, note };
    }

    /// <summary>Advanced editor: save (or with null clear) a profile's per-core CPU undervolt, and apply it if that profile is in use.</summary>
    public object SaveCpu(string profile, int[]? values)
    {
        if (!Config.Profiles.ContainsKey(profile)) throw new KeyNotFoundException($"no profile \"{profile}\"");
        if (values == null || values.All(v => v == 0)) Tuning.ClearCpu(profile); else Tuning.SaveCpu(profile, values);
        string? note = null;
        if (Config.ActiveProfile == profile && !Tuning.StockMode) note = Cpu.ApplyCo(Tuning.CpuFor(profile), "edited " + profile);
        return new { profile, values = Tuning.CpuFor(profile), note };
    }

    /// <summary>Use a profile: switch to it (fans, GPU overclock, CPU undervolt) and leave stock mode. Picking the active profile
    /// again re-applies it.</summary>
    public object UseProfile(string profile)
    {
        if (!Config.Profiles.ContainsKey(profile)) throw new KeyNotFoundException($"no profile \"{profile}\"");
        Tuning.SetStockMode(false);
        if (Config.ActiveProfile != profile)
        {
            var c = System.Text.Json.JsonSerializer.Deserialize<GlintConfig>(System.Text.Json.JsonSerializer.Serialize(Config, GlintConfig.Json), GlintConfig.Json)!;
            c.ActiveProfile = profile;
            Apply(c);
        }
        else if (Config.Tuning?.Enabled == true) ApplyProfileTuning(profile, cpuUnlessStock: false);
        return new { profile };
    }

    /// <summary>Start a tune: Glint builds the goal's profile first ("Quiet (tuned)"..., fresh fan rules from the detected fans,
    /// switched to it), then Claude Code tunes inside it. Re-running a goal rebuilds that profile from scratch.</summary>
    public object StartTune(string goal, int budgetMinutes, string? context, string? target, string? goalText, string? cpuMode = null, string? gpuMode = null)
    {
        if (Config.Tuning?.Enabled != true) throw new InvalidOperationException("turn tuning on first");
        if (Ai.Running) throw new InvalidOperationException("a tune is already running");   // before anything below changes the running tune
        _stopRequested = false;
        Tuning.GpuMode = gpuMode == "efficiency" ? "efficiency" : "performance";
        Ai.Modes = (Tuning.GpuMode, cpuMode == "efficiency" ? "efficiency" : "performance");
        Cpu.Mode = cpuMode == "efficiency" ? "efficiency" : "performance";
        if (target is null or "all" or "cpu") Cpu.ResetForTune();   // an earlier search's result is in its profile already
        var (g, _) = Glint.Tuning.TuningGoal.Parse(goal, goalText, target);
        Tuning.SetStockMode(false);
        if (g != null) AiFans.Prepare(goal, goalText, null, fresh: true);
        var started = Ai.Start(goal, budgetMinutes, context, target, goalText);
        if (Ai.TuneGoal?.ProfileName is { } pn) Tuning.StartNotes(pn, goal, target ?? "all", DateTime.Now);
        return started;
    }

    public void Apply(GlintConfig cfg, bool save = true)
    {
        (cfg.HomeAssistant ??= new()).Normalize(); // never keep a plain-text MQTT password around
        var oldProfile = Config?.ActiveProfile;
        var oldNames = Config?.Profiles.Keys.ToList();
        Config = cfg;
        // a deleted profile takes its overclock with it (ClearProfile is a no-op for one that has none)
        if (oldNames != null && oldNames.Where(n => !cfg.Profiles.ContainsKey(n)).ToList() is { Count: > 0 } gone)
            _ = Task.Run(() => { try { foreach (var n in gone) Tuning?.ClearProfile(n); } catch (Exception e) { LastError = "tuning: " + e.Message; } });
        // each profile carries its own overclock: switching profile applies it (or stock), off the caller's thread
        if (oldProfile != null && oldProfile != cfg.ActiveProfile && cfg.Tuning?.Enabled == true && Tuning != null && Cpu != null)
            ApplyProfileTuning(cfg.ActiveProfile, cpuUnlessStock: true);
        Tuning?.PruneAiProfiles(cfg.Profiles.Keys);
        Lighting.Apply(cfg);
        if (save && !NoSave) { _selfWrite = DateTime.UtcNow; cfg.Save(); }
        HomeAssistant.Configure(cfg.HomeAssistant); // reconnects only if that section changed
        HomeAssistant.ConfigApplied();
        Updates.OnConfig(cfg);
        Updater.Kick();
        try { ConfigApplied?.Invoke(cfg); } catch { }
    }

    /// <summary>Global switch off: fans on their own control leave the duty map (LHM ones are then released); the rest
    /// (hubs that can't release fans without their lighting) keep their curve and say why.</summary>
    public static void FanControlOff(Dictionary<string, int> duty, Dictionary<string, string> explain, IEnumerable<FanOutput> fans, Func<string, bool> handedBack)
    {
        foreach (var f in fans)
        {
            if (handedBack(f.Id)) { duty.Remove(f.Id); explain[f.Id] = "fan control off: on its own control (BIOS, GPU driver or device firmware)"; }
            else if (duty.ContainsKey(f.Id)) explain[f.Id] = "fan control off, but this hub can't hand its fans back without its lighting, so Glint keeps driving it: " + explain.GetValueOrDefault(f.Id, "");
        }
    }

    /// <summary>The lost (rule, sensor) pairs that matter: a rule that drives no fan this PC has (a default rule for hardware
    /// that isn't here) is not worth a notification.</summary>
    public static List<(string Rule, string Sensor)> LostOnRealFans(IReadOnlyList<(string Rule, string Sensor)> lost, IReadOnlyList<FanRule> rules, IReadOnlySet<string> fans) =>
        lost.Where(l => rules.Any(r => r.Name == l.Rule && r.Fans.Any(fans.Contains))).ToList();

    bool Native(string fanId) => !Rig.Software.Contains(fanId) && Rig.Controllers.Any(c => fanId.StartsWith(c.Id + ":", StringComparison.Ordinal));

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        double last = 0;
        while (!_stop)
        {
            double now = sw.Elapsed.TotalSeconds;
            double cpu0 = CpuMeter.ThreadMs();
            double dt = last == 0 ? 1 : now - last;
            last = now;
            try
            {
                var rpm = new Dictionary<string, int>();
                var s = Rig.ReadSensors(rpm);
                Sensors = s; Rpm = rpm;
                var duty = Engine.Compute(Config, s, dt);
                foreach (var f in Rig.Fans)
                    if (!duty.ContainsKey(f.Id) && Native(f.Id)) // unassigned LHM headers (board, Kraken...) stay on their own control
                    {
                        duty[f.Id] = UnassignedDuty;
                        Engine.LastExplain[f.Id] = $"{UnassignedDuty}% (not in any rule)";
                    }
                Rig.SetFanControl(Config.FanControl);
                if (!Config.FanControl) FanControlOff(duty, Engine.LastExplain, Rig.Fans, Rig.HandedBack);
                Rig.ApplyDuty(duty, Config.FailsafeDuty);
                var contested = Rig.Software.Contested;
                foreach (var (id, why) in contested)
                    if (duty.ContainsKey(id)) Engine.LastExplain[id] = "not driven: " + why;
                // tell the user when a rule lost its sensor or another program took a fan (nothing to do while all is well)
                IReadOnlyList<(string Rule, string Sensor)> lost = NoLost;
                if (Config.FanControl && Engine.Lost.Count > 0) lost = LostOnRealFans(Engine.Lost, Config.Active, Rig.Fans.Select(f => f.Id).ToHashSet());
                _alerts.Check(now, lost, contested, "", Config.FailsafeDuty, _fanName, _postNotice);
                Duty = duty;
                Explain = new Dictionary<string, string>(Engine.LastExplain);
                try
                {
                    var rd = new Dictionary<string, double>();
                    if (Config.Profiles.TryGetValue(Config.ActiveProfile, out var rules))
                        foreach (var r in rules) { var ds = r.Fans.Where(duty.ContainsKey).Select(f => (double)duty[f]).ToList(); if (ds.Count > 0) rd[r.Name] = ds.Average(); }
                    Thermal.Record(DateTime.Now, s, rd);
                }
                catch { }
                LastError = "";
            }
            catch (Exception e)
            {
                LastError = e.Message;
                // Fail-safe: every hub fan, and every LHM fan (board header, GPU) Glint was driving.
                // Indexer, not ToDictionary: a duplicate id must never abort the fail-safe.
                try
                {
                    var fs = new Dictionary<string, int>();
                    foreach (var f in Rig.Fans)
                        if (Native(f.Id) || Rig.Software.Touched(f.Id) || Rig.Software.IsContested(f.Id)) fs[f.Id] = Config.FailsafeDuty;
                    Rig.ApplyDuty(fs, Config.FailsafeDuty);
                }
                catch (Exception fe) { LastError += " · fail-safe: " + fe.Message; }
                try { _alerts.Check(now, NoLost, null, e.Message, Config.FailsafeDuty, _fanName, _postNotice); } catch { }
            }
            CpuMeter.Add("sensors + fans", CpuMeter.ThreadMs() - cpu0);
            Updated?.Invoke();
            if (now - _lastStatus >= 5) { _cpuParts = CpuMeter.Snapshot(now - _lastStatus); _lastStatus = now; WriteStatus(); }
            int sleep = Config.PollMs - (int)((sw.Elapsed.TotalSeconds - now) * 1000);
            if (sleep > 0) Thread.Sleep(sleep);
        }
    }

    double _lastStatus = 0;
    Dictionary<string, double> _cpuParts = new();
    public IReadOnlyDictionary<string, double> CpuParts => _cpuParts;
    /// <summary>Small machine-readable snapshot for verification / scripts (%APPDATA%\Glint\status.json).</summary>
    void WriteStatus()
    {
        if (NoSave) return;
        try
        {
            var proc = Process.GetCurrentProcess();
            var o = new
            {
                time = DateTime.Now, profile = Config.ActiveProfile, elevated = Rig.Elevated, error = LastError,
                sensors = Sensors.Where(k => !k.Key.StartsWith('/')).ToDictionary(k => k.Key, k => Math.Round(k.Value, 1)),
                rpm = Rpm, duty = Duty,
                lighting = new { effect = Config.Lighting.Effect.Type.ToString(), fps = Math.Round(Lighting.ActualFps, 1), devices = Lighting.DeviceFps.ToDictionary(k => k.Key, k => Math.Round(k.Value, 1)), status = Lighting.Status },
                devices = Rig.Log,
                process = new { cpuSeconds = Math.Round(proc.TotalProcessorTime.TotalSeconds, 1), privateMB = proc.PrivateMemorySize64 / 1048576, managedMB = GC.GetTotalMemory(false) / 1048576 },
                cpuPercentOfOneCore = _cpuParts,
                pluginHeapMB = Rig.Lights.OfType<Plugins.JintPluginDevice>().Concat(Rig.Controllers.OfType<Plugins.JintPluginDevice>()).Distinct().ToDictionary(p => p.Id, p => Math.Round(p.HeapMB, 1)),
                sensorReadMs = new { native = Math.Round(Rig.HubReadMs, 1), lhm = Math.Round(Rig.LhmReadMs, 1) },
            };
            var path = Path.Combine(GlintConfig.Dir, "status.json");
            UserFs.WriteAllTextAtomic(path, System.Text.Json.JsonSerializer.Serialize(o, GlintConfig.Json));
        }
        catch { }
    }

    public void Dispose()
    {
        Overlay?.Dispose(); // stops the ETW session
        Ai.Dispose();
        Crashes.Dispose();
        Cpu.Dispose();
        Tuning.Dispose();   // stops a running test (the test itself puts the card back to stock)
        HomeAssistant.Dispose(); // publishes "offline" while the broker link is still up
        _stop = true;
        _thread.Join(3000);
        _watch?.Dispose();
        Lighting.Dispose();
        Updater.Dispose();
        Updates.Dispose();
        Rig.Dispose();
    }
}
