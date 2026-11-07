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
        Plugins.PluginHost.Disabled = () => Config.DisabledPlugins;
        Rig = rig ?? new Rig(cfg);
        Lighting = new LightingEngine(Rig, cfg, () => Sensors);
        Lighting.Start();
        HomeAssistant = new HomeAssistantBridge(this);
        HomeAssistant.Configure(cfg.HomeAssistant);
        Updates = new UpdateChecker(() => Config);
        Updater = new Updater(Updates, () => Config);
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
            catch (Exception e) { LastError = "config: " + e.Message; }
        });
    }

    /// <summary>Swap in a new config (from UI or file edit).</summary>
    public void Apply(GlintConfig cfg, bool save = true)
    {
        (cfg.HomeAssistant ??= new()).Normalize(); // never keep a plain-text MQTT password around
        Config = cfg;
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
                foreach (var (id, why) in Rig.Software.Contested)
                    if (duty.ContainsKey(id)) Engine.LastExplain[id] = "not driven: " + why;
                Duty = duty;
                Explain = new Dictionary<string, string>(Engine.LastExplain);
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
