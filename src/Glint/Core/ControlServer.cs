using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glint.Devices;
using Glint.Plugins;
using HidSharp;

namespace Glint.Core;

/// <summary>
/// Local control API for scripts and AI agents: JSON lines over a named pipe that only the current
/// user can open. Used by `Glint.exe ctl ...` and the MCP server (`Glint.exe mcp`).
/// Deliberately NOT exposed: approving SMBus plugins (dashboard click only).
/// </summary>
public sealed class ControlServer : IDisposable
{
    /// <summary>Wire format: one compact JSON object per line (same naming as config.json).</summary>
    static readonly JsonSerializerOptions Wire = new(GlintConfig.Json) { WriteIndented = false };
    public static string PipeName => Environment.GetEnvironmentVariable("GLINT_PIPE") is { Length: > 0 } t ? t : "GlintControl_" + Environment.UserName;
    readonly GlintService _svc;
    readonly Action<Action> _ui;
    readonly CancellationTokenSource _cts = new();

    public ControlServer(GlintService svc, Action<Action> ui)
    {
        _svc = svc; _ui = ui;
        new Thread(Listen) { IsBackground = true, Name = "Glint control" }.Start();
    }

    /// <summary>At most this many clients at once (each has a thread); more wait for a free instance.</summary>
    public const int MaxClients = 8;
    /// <summary>Longest request line (characters). Big enough for plugins.add of a 2 MB plugin, small enough that a client can't exhaust memory.</summary>
    public const int MaxLine = 6 << 20;
    int _live;

    /// <summary>
    /// Who may open the pipe: only this Windows user, never over the network. When elevated, only administrators may
    /// create further instances of the name, so a normal-privilege program can't serve a fake instance to our clients.
    /// </summary>
    public static PipeSecurity Security()
    {
        var sec = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        sec.AddAccessRule(new PipeAccessRule(me.User!, elevated ? PipeAccessRights.ReadWrite : PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        if (elevated) sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return sec;
    }

    void Listen()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // no instance of ours alive: insist on being the first, so we never join a name someone else squatted
                var opts = PipeOptions.Asynchronous | (Volatile.Read(ref _live) == 0 ? PipeOptions.FirstPipeInstance : 0);
                pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, MaxClients,
                    PipeTransmissionMode.Byte, opts, 0, 0, Security());
                Interlocked.Increment(ref _live);
                try { pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult(); }
                catch { Interlocked.Decrement(ref _live); throw; }
                var p = pipe; pipe = null;
                new Thread(() => Serve(p)) { IsBackground = true, Name = "Glint control client" }.Start();
            }
            catch (OperationCanceledException) { break; }
            catch { pipe?.Dispose(); Thread.Sleep(500); }
        }
    }

    /// <summary>One client. Nothing a client does (disconnecting mid-reply, garbage input) may take Glint down.</summary>
    void Serve(NamedPipeServerStream pipe)
    {
        try { ServeInner(pipe); }
        catch { }
        finally { try { pipe.Dispose(); } catch { } Interlocked.Decrement(ref _live); }
    }

    /// <summary>ReadLine with a length cap: a client sending one endless line gets an error and is dropped.</summary>
    internal static string? ReadLine(TextReader r, int max)
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            int c = r.Read();
            if (c < 0) return sb.Length > 0 ? sb.ToString() : null;
            if (c == '\n') { if (sb.Length > 0 && sb[^1] == '\r') sb.Length--; return sb.ToString(); }
            if (sb.Length >= max) throw new InvalidDataException($"request longer than {max} characters");
            sb.Append((char)c);
        }
    }

    void ServeInner(NamedPipeServerStream pipe)
    {
        using (var r = new StreamReader(pipe, leaveOpen: true))
        using (var w = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true })
        {
            string? line;
            while (true)
            {
                try { line = ReadLine(r, MaxLine); }
                catch (InvalidDataException e) { w.WriteLine(JsonSerializer.Serialize(new { id = (int?)null, error = e.Message }, Wire)); return; }
                if (line == null) break;
                JsonNode? id = null;
                try
                {
                    var req = JsonNode.Parse(line)!;
                    id = req["id"]?.DeepClone();
                    var result = Handle((string)req["method"]!, req["params"] as JsonObject ?? new JsonObject());
                    w.WriteLine(JsonSerializer.Serialize(new { id, result }, Wire));
                }
                catch (Exception e)
                {
                    w.WriteLine(JsonSerializer.Serialize(new { id, error = e.Message }, Wire));
                }
            }
        }
    }

    /// <summary>Every method, with a one-line description (also served as "help").</summary>
    public static readonly (string Method, string Help, string Params)[] Methods =
    {
        ("help", "List all methods.", ""),
        ("status", "Live temperatures, fan RPM/duty, profile, lighting, per-device frame rates, CPU/memory use.", ""),
        ("devices", "Detected lighting zones (id, LEDs, kind) and fan outputs.", ""),
        ("sensors.all", "HWMonitor-style tree of EVERY hardware sensor (temperatures, voltages, clocks, loads, power, fans, data...) with value/min/max/unit. Sensor ids can be used as overlay items.", ""),
        ("sensors.list", "Every raw temperature sensor (id, device, name, value) plus the friendly ids usable in fan curves (cpu, gpu, liquid, storage, ram, st:liquid, ...).", ""),
        ("hid.list", "Every USB HID device on the PC (vid, pid, interface, usage page, report sizes) and which driver claimed it. Use this to target a new device plugin.", "{ vendorId?: number }"),
        ("plugins.list", "All plugins: file, kind, name, source (bundled/user), enabled, ok, message.", ""),
        ("plugins.docs", "The full plugin authoring guide (read this before writing a plugin).", ""),
        ("plugins.template", "A starting template.", "{ kind: 'effect' | 'device' }"),
        ("plugins.get", "Source code of a plugin.", "{ file }"),
        ("plugins.add", "Create or replace a plugin in the user plugins folder and load it. Returns its load status (errors included). Device plugins load after app.restart.", "{ file: 'name.js', code }"),
        ("plugins.check", "Run a device plugin's declared tests against simulated devices (see 'Testing' in plugins.docs). Use after plugins.add.", "{ file }"),
        ("plugins.remove", "Delete a user plugin (bundled ones can only be disabled).", "{ file }"),
        ("plugins.setEnabled", "Switch a plugin on or off.", "{ file, enabled }"),
        ("plugins.reload", "Reload effect plugins now.", ""),
        ("lighting.presets", "Names of saved lighting presets.", ""),
        ("lighting.preset", "Apply a preset to all lights.", "{ name }"),
        ("lighting.set", "Set an effect for all lights or one device/zone. effect = { type, colors?, speed?, ..., plugin?, params? }.", "{ effect, target?: 'all' | deviceId | zoneId }"),
        ("lighting.brightness", "Set brightness 0-100.", "{ value }"),
        ("lighting.identify", "Flash a device or zone white on the real hardware for a few seconds (to find which is which).", "{ target: deviceId | zoneId, seconds? }"),
        ("lighting.layout", "Effective layout: every zone's placement on the 16:10 canvas (x, y, w, h, rotation; 0..1).", ""),
        ("lighting.autoLayout", "WARNING: overwrites the user's hand-tuned layout with an automatic arrangement based on what each device is. Only with the user's consent; requires confirm=true. The current layout is backed up first (lighting.layoutBackups / lighting.restoreLayout).", "{ confirm: true }"),
        ("lighting.layoutBackups", "Saved layout backups (newest first): name, time, number of zones. Made automatically before lighting.autoLayout / lighting.restoreLayout; the last 10 are kept.", ""),
        ("lighting.restoreLayout", "Restore a layout backup (name from lighting.layoutBackups, or 'latest'). The layout being replaced is backed up too.", "{ name?: string | 'latest' }"),
        ("fans.list", "Fan control overview: the global fanControl switch (config.patch path=fanControl value=false hands fans back to BIOS / driver / firmware), every profile's rules (fans with friendly names and live duty/rpm, inputs, combine, min/max, ramps, fixed, override), the curves, the fail-safe duty, and every sensor id usable as an input (with live values).", ""),
        ("fans.setRule", "Create or update a fan rule by name (profile defaults to the active one). Partial updates merge: only the fields you give change; 'inputs' and 'fans' replace the whole list. Give rule.name to rename. Validated; invalid → {ok:false, errors} and nothing saved.", "{ name, rule: { fans?, inputs?: [{ sensor, curve | points, weight?, hysteresis?, smoothingSec? }], combine?, minDuty?, maxDuty?, rampUpPerSec?, rampDownPerSec?, fixed?, override?, name? }, profile? }"),
        ("fans.removeRule", "Delete a fan rule by name (profile defaults to the active one).", "{ name, profile? }"),
        ("fans.override", "Manually set a rule's fans to a duty %, or percent=null for automatic (curve) again. Same as the Home Assistant fan entity: clamped to the rule's min/max; the fail-safe (lost sensor or curve at fail-safe duty) still wins. Profile defaults to the active one.", "{ name, percent: number | null, profile? }"),
        ("curves.set", "Create or replace a named fan curve: points [[tempC, duty%], ...], temperatures strictly increasing, duty 0-100.", "{ name, points }"),
        ("curves.remove", "Delete a named curve. Refused (with the rules using it) if any rule input references it.", "{ name }"),
        ("profiles.create", "Create a fan profile as a copy of an existing one (default: the active profile). Does not switch to it (use profile.set).", "{ name, from? }"),
        ("profiles.remove", "Delete a fan profile (not the active one).", "{ name }"),
        ("profile.set", "Switch fan profile.", "{ name }"),
        ("config.get", "The whole config.", ""),
        ("config.schema", "Description of every config field: type, meaning, valid range / enum, default, example (fan rules, curves, lighting effects and their params, layout, Home Assistant, sensor options). Read before editing the config.", ""),
        ("config.set", "Replace the whole config (get it first, modify, send back). Validated; invalid → {ok:false, errors} and nothing saved.", "{ config }"),
        ("config.patch", "Change one setting by dotted path, e.g. path='lighting.brightness' value=60 or path='sensors.storage' value=false. Validated; invalid → {ok:false, errors} and nothing saved.", "{ path, value }"),
        ("config.validate", "Check a whole config without saving it: {ok, errors, warnings}.", "{ config }"),
        ("ha.status", "Home Assistant (MQTT) bridge: enabled, connected, broker, last error, number of discovered entities. Configure with config.patch path=homeAssistant value={ enabled, host, port, tls, username, password, discoveryPrefix, baseTopic }.", ""),
        ("overlay.get", "In-game overlay settings: enabled, hotkey, position, offsetX/Y, scale, opacity, backgroundOpacity, accent, layout, onlyWhenGame, items [{ source, label?, color? }].", ""),
        ("overlay.set", "Change overlay settings (partial: only the fields given change; 'items' replaces the list). Item sources: fps, frametime, low1, time, glint.cpu, fan:<id>, duty:<id>, or a sensor id. Validated; invalid → {ok:false, errors} and nothing saved.", "{ enabled?, hotkey?, position?: 'top-left'|'top-right'|'bottom-left'|'bottom-right', offsetX?, offsetY?, scale?, opacity?, backgroundOpacity?, accent?: '#rrggbb'|null, layout?: 'compact'|'detailed', onlyWhenGame?, items? }"),
        ("overlay.toggle", "Show / hide the in-game overlay (enabled given → set it, else flip).", "{ enabled?: boolean }"),
        ("overlay.stats", "Live overlay readings: fps, frameTimeMs, low1 (1% low), the target (foreground) process, which present events are used, and whether the ETW session is running.", ""),
        ("tuning.status", "Overclocking status: whether tuning is enabled, the GPU (name, driver, workstation, whether it may be tuned and why not), this card's limits (max core / memory offsets from the driver's range, temperature cut-offs, max test length), the memory-offset ratio, the saved result, whether crash watch reverted it, the last 30 tests (newest first) and the running job. Read this first.", ""),
        ("tuning.gpu.test", "Start a GPU stress test at core / memory offsets (MHz; memory in MSI Afterburner units) for N seconds (10-300). Glint's CUDA test checks every result bit for bit against stock, watches temperatures, driver resets and ECC errors, holds the GPU fans up, and puts the card back to stock when it ends. light=true tests at low load (top boost clocks, where big core offsets fail first). Returns at once: poll tuning.wait. A crash or freeze mid-test is recorded as a failure on the next start.", "{ core, memAb, seconds?, light? }"),
        ("tuning.wait", "Wait up to N seconds (max 25) for the running tuning job; returns { busy, phase, progress, result }. Call again while busy.", "{ seconds? }"),
        ("tuning.stop", "Stop the running tuning job (the card goes back to stock).", ""),
        ("tuning.gpu.apply", "Apply core / memory offsets now (until stock or reboot). Refused unless a full (non-light) test at or above both values passed on this driver.", "{ core, memAb }"),
        ("tuning.gpu.save", "Save core / memory offsets as the result: what 'apply at startup' re-applies and what crash watch backs off from. Same proof rule as tuning.gpu.apply.", "{ core, memAb }"),
        ("tuning.gpu.stock", "GPU back to stock clocks now (stops a running test first).", ""),
        ("tuning.cpu.status", "CPU Curve Optimizer tuning (Ryzen 5000/7000/9000 only): CPU name and family, whether it can be tuned here and why not, microcode advisory (Intel 13th/14th gen), whether CoreCycler is bundled, the search (phase idle/searching/stopped/done, running, per-core values found so far, adjustments, failed cores, WHEA errors since it started, final values with margin, the BIOS sheet) and a sensible start value (startHint).", ""),
        ("tuning.cpu.start", "Start the automatic per-core Curve Optimizer search with CoreCycler (y-cruncher, one core at a time, +1 on every error): startValue -50..0 is where each core starts (Ryzen 5000 allows -30 at most). Takes SEVERAL HOURS and resumes by itself after a freeze (the user presses reset) or reboot. Glint stops the stress test at the CPU temperature limit or if the temperature sensor fails. Only start it when the user asked for CPU tuning. Poll tuning.cpu.status.", "{ startValue }"),
        ("tuning.cpu.finish", "End the search: stops CoreCycler, final values = found + 3 margin (never positive), and the BIOS sheet to type in (Curve Optimizer can't be set permanently from Windows). Refused while the search is still working unless force=true (cores it hasn't reached would be untested).", "{ force? }"),
        ("tuning.cpu.stop", "Stop the CPU search and its stress test now. What was found so far is kept (tuning.cpu.finish still works).", ""),
        ("tuning.crashes", "Crash watch: recent GPU driver resets, hardware (WHEA) errors, bluescreens and game crashes in the NVIDIA driver, what Glint did about each (back-off, revert to stock, recorded only), and CPU cores whose errors suggest loosening their Curve Optimizer value.", ""),
        ("tuning.ai.start", "Start 'Tune with AI': Claude Code (the user's own login; Windows or WSL install) tunes through the tuning tools only. goal: gpu | cpu | all. Only start it when the user asked.", "{ goal?, budgetMinutes? }"),
        ("tuning.ai.stop", "Stop the AI tuning run (the card goes back to stock if a test was running).", ""),
        ("tuning.ai.status", "AI tuning run: running, goal, which Claude Code install, error, final summary and the transcript.", ""),
        ("setup.check", "Setup check: is this PC set up right for Glint? Admin rights, PawnIO driver, WebView2, CPU temperature, conflicting RGB/fan apps (running or starting with Windows), GPU fan tools while Glint drives GPU fans, fans another program has taken over, plugins needing approval or failing, devices that declined, the startup task, version + update, OpenRGB bridge. Each item: status ok/warn/fail, plain-language detail, and the fix. Start here when something doesn't work.", ""),
        ("update.status", "Updates: this version, the latest release, whether it can be installed from here (canInstall / cannotInstall), autoInstall, and the install progress (install.state idle|downloading|verifying|installing|failed, install.message).", ""),
        ("update.install", "Install the newer release: downloads the setup from Glint's GitHub releases, checks it against SHA256SUMS.txt and GitHub's build attestation for the release workflow, then runs it silently (Glint closes and restarts). Only with the user's consent; requires confirm=true. Poll update.status for progress.", "{ confirm: true }"),
        ("log", "Recent device / plugin log lines.", "{ lines?: number }"),
        ("app.restart", "Restart Glint (needed after adding or changing device plugins).", ""),
    };

    /// <summary>A required whole-number parameter (no silent 0 for a missing or mistyped value: offsets go to hardware).</summary>
    static int Int(JsonObject p, string key) =>
        p[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) && Math.Abs(d) < 100000 ? (int)Math.Round(d)
        : throw new ArgumentException($"{key} must be a number");

    object Handle(string method, JsonObject p)
    {
        string S(string k) => (string?)p[k] ?? throw new ArgumentException($"missing '{k}'");
        GlintConfig Clone() => JsonSerializer.Deserialize<GlintConfig>(JsonSerializer.Serialize(_svc.Config, GlintConfig.Json), GlintConfig.Json)!;
        switch (method)
        {
            case "help": return Methods.Select(m => new { method = m.Method, help = m.Help, @params = m.Params });
            case "status":
                {
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    return new
                    {
                        profile = _svc.Config.ActiveProfile,
                        sensors = _svc.Sensors.Where(k => !k.Key.StartsWith('/')).ToDictionary(k => k.Key, k => Math.Round(k.Value, 1)),
                        rpm = _svc.Rpm, duty = _svc.Duty, explain = _svc.Explain,
                        lighting = new { _svc.Config.Lighting.Enabled, _svc.Config.Lighting.Brightness, effect = _svc.Config.Lighting.Effect, overrides = _svc.Config.Lighting.Overrides.Keys, fps = Math.Round(_svc.Lighting.ActualFps, 1), devices = _svc.Lighting.DeviceFps, error = _svc.Lighting.Status },
                        cpuPercentOfOneCore = _svc.CpuParts,
                        process = new { privateMB = proc.PrivateMemorySize64 / 1048576 },
                        error = _svc.LastError,
                    };
                }
            case "devices":
                return new
                {
                    lights = _svc.Rig.Lights.Select(l => new { l.Id, l.Name, zones = l.Zones.Select(z => new { z.Id, z.Name, z.Leds, kind = z.Kind.ToString().ToLowerInvariant() }) }),
                    fans = _svc.Rig.Fans.Select(f => new { f.Id, f.Name, f.Device }),
                };
            case "sensors.all":
                return Rig.SensorCatalog(_svc.Rig);
            case "sensors.list":
                return new { friendly = _svc.Sensors.Where(k => !k.Key.StartsWith('/')).ToDictionary(k => k.Key, k => Math.Round(k.Value, 1)), raw = _svc.Rig.ListSensors() };
            case "hid.list":
                {
                    int? vid = (int?)p["vendorId"];
                    return DeviceList.Local.GetHidDevices().Where(d => vid == null || d.VendorID == vid).Select(d =>
                    {
                        string Try(Func<string> f) { try { return f(); } catch { return ""; } }
                        int Len(Func<int> f) { try { return f(); } catch { return 0; } }
                        return new
                        {
                            vendorId = $"0x{d.VendorID:x4}", productId = $"0x{d.ProductID:x4}",
                            product = Try(d.GetProductName), manufacturer = Try(d.GetManufacturer),
                            @interface = DriverRegistry.InterfaceOf(d), usagePage = $"0x{DriverRegistry.UsagePageOf(d):x4}",
                            outputReport = Len(d.GetMaxOutputReportLength), inputReport = Len(d.GetMaxInputReportLength), featureReport = Len(d.GetMaxFeatureReportLength),
                            claimedBy = DriverRegistry.ClaimedBy(d),
                        };
                    }).OrderBy(x => x.vendorId).ThenBy(x => x.productId).ToList();
                }
            case "plugins.list": return PluginList(_svc);
            case "plugins.docs": return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plugin-templates", "PLUGINS.md"));
            case "plugins.template":
                return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plugin-templates", (string?)p["kind"] == "device" ? "device-template.js" : "effect-template.js"));
            case "plugins.get":
                return PluginHost.ReadCode(PluginHost.FindFile(S("file")) ?? throw new FileNotFoundException("no such plugin"));
            case "plugins.add":
                {
                    var file = SafeName(S("file"));
                    var code = S("code");
                    if (System.Text.Encoding.UTF8.GetByteCount(code) > PluginHost.MaxPluginBytes) throw new ArgumentException($"code is larger than {PluginHost.MaxPluginBytes / 1048576} MB");
                    var path = Path.Combine(PluginHost.UserDir, file);
                    // as the signed-in user, not as administrator: the plugins folder is user-writable and may hold planted links
                    UserFs.Run(() => { Directory.CreateDirectory(PluginHost.UserDir); File.WriteAllText(path, code); });
                    PluginHost.ReloadNow();
                    var st = PluginHost.Status.FirstOrDefault(x => x.File.Equals(file, StringComparison.OrdinalIgnoreCase));
                    return new { file, path, status = st, note = st?.Kind is "device" or "smbus" ? "Device plugins load on app.restart." : null };
                }
            case "plugins.check":
                {
                    var f = PluginHost.FindFile(S("file")) ?? throw new FileNotFoundException("no such plugin");
                    var res = PluginCheck.Run(f);
                    return new { passed = res.Count(r => r.Pass), failed = res.Count(r => !r.Pass), results = res };
                }
            case "plugins.remove":
                {
                    var f = PluginHost.FindFile(S("file")) ?? throw new FileNotFoundException("no such plugin");
                    if (PluginHost.IsBundled(f)) throw new InvalidOperationException("bundled plugins can't be deleted; use plugins.setEnabled false");
                    UserFs.Delete(f);
                    PluginHost.ReloadNow();
                    return new { removed = Path.GetFileName(f) };
                }
            case "plugins.setEnabled":
                {
                    var file = Path.GetFileName(S("file"));
                    bool on = (bool?)p["enabled"] ?? true;
                    var c = Clone();
                    c.DisabledPlugins.RemoveAll(x => x.Equals(file, StringComparison.OrdinalIgnoreCase));
                    if (!on) c.DisabledPlugins.Add(file);
                    _svc.Apply(c);
                    PluginHost.ReloadNow();
                    return new { file, enabled = on, note = "Device plugins change on app.restart." };
                }
            case "plugins.reload": PluginHost.ReloadNow(); return PluginList(_svc);
            case "lighting.presets": return _svc.Config.Lighting.Presets.Keys;
            case "lighting.preset":
                {
                    var c = Clone();
                    if (!c.Lighting.Presets.TryGetValue(S("name"), out var fx)) throw new KeyNotFoundException("no such preset");
                    c.Lighting.Effect = fx; c.Lighting.Overrides.Clear(); c.Lighting.Enabled = true;
                    _svc.Apply(c); return new { ok = true };
                }
            case "lighting.set":
                {
                    EffectConfig fx;
                    try { fx = p["effect"].Deserialize<EffectConfig>(GlintConfig.Json) ?? throw new ArgumentException("missing 'effect'"); }
                    catch (JsonException e) { return Fail("effect: " + e.Message + $" (types: {string.Join(", ", Enum.GetNames<EffectType>())}; see config.schema)"); }
                    var c = Clone();
                    string target = (string?)p["target"] ?? "all";
                    if (target == "all") { c.Lighting.Effect = fx; c.Lighting.Overrides.Clear(); } else c.Lighting.Overrides[target] = fx;
                    c.Lighting.Enabled = true;
                    return Commit(c, extra: x => x["target"] = target);
                }
            case "lighting.identify": _svc.Lighting.Identify(S("target"), (double?)p["seconds"] ?? 4); return new { ok = true };
            case "lighting.layout": return new { aspect = Layout.Aspect, zones = _svc.Lighting.LayoutZones, placements = _svc.Lighting.Placements };
            case "lighting.autoLayout":
                {
                    if ((bool?)p["confirm"] != true)
                        return new { ok = false, changed = false, message = "Not done: this replaces the user's layout (zone positions on the canvas, often hand-tuned in the dashboard) with an automatic guess. Ask the user first, then call again with confirm=true. The current layout will be backed up (lighting.restoreLayout undoes it)." };
                    var c = Clone();
                    c.Lighting.Layout = Layout.AutoArrange(_svc.Lighting.LayoutZones);
                    string? backup = null;
                    var r = Commit(c, () => backup = LayoutBackups.Save(_svc.Config.Lighting.Layout, "autoLayout"), x => x["placements"] = JsonSerializer.SerializeToNode(c.Lighting.Layout, GlintConfig.Json));
                    if (backup != null) r["backup"] = backup;
                    return r;
                }
            case "lighting.layoutBackups": return LayoutBackups.List();
            case "lighting.restoreLayout":
                {
                    var name = (string?)p["name"] ?? "latest";
                    var (file, layout) = LayoutBackups.Load(name);
                    var c = Clone();
                    c.Lighting.Layout = layout;
                    string? backup = null;
                    var r = Commit(c, () => backup = LayoutBackups.Save(_svc.Config.Lighting.Layout, "before restore"), x => { x["restored"] = file; x["zones"] = layout.Count; });
                    if (backup != null) r["backup"] = backup;
                    return r;
                }
            case "lighting.brightness": { var c = Clone(); c.Lighting.Brightness = Math.Clamp((int)p["value"]!, 0, 100); return Commit(c); }
            case "profile.set":
                {
                    var c = Clone();
                    if (!c.Profiles.ContainsKey(S("name"))) throw new KeyNotFoundException("no such profile: " + string.Join(", ", c.Profiles.Keys));
                    c.ActiveProfile = S("name"); return Commit(c);
                }
            case "fans.list": return FanList();
            case "fans.setRule":
                {
                    var c = Clone();
                    var (pname, rules) = Profile(c, p);
                    var name = S("name");
                    var fields = p["rule"] as JsonObject ?? new JsonObject(p.Where(kv => kv.Key is not ("name" or "profile" or "rule")).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
                    int ix = rules.FindIndex(r => r.Name == name);
                    var node = JsonSerializer.SerializeToNode(ix >= 0 ? rules[ix] : new FanRule { Name = name }, GlintConfig.Json)!.AsObject();
                    foreach (var (k, v) in fields)
                    {
                        var key = node.Select(x => x.Key).FirstOrDefault(x => x.Equals(k, StringComparison.OrdinalIgnoreCase)) ?? k;
                        if (!typeof(FanRule).GetProperties().Any(pi => pi.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                            return Fail($"unknown rule field '{k}' (fields: name, fans, inputs, combine, fixed, override, minDuty, maxDuty, rampUpPerSec, rampDownPerSec)");
                        node[key] = v?.DeepClone();
                    }
                    FanRule rule;
                    try { rule = node.Deserialize<FanRule>(GlintConfig.Json) ?? throw new ArgumentException("bad rule"); }
                    catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return Fail("rule: " + e.Message); }
                    if (rule.Name != name && rules.Any(r => r.Name == rule.Name)) return Fail($"profile \"{pname}\" already has a rule named \"{rule.Name}\"");
                    if (ix >= 0) rules[ix] = rule; else rules.Add(rule);
                    return Commit(c, extra: x => { x["profile"] = pname; x["created"] = ix < 0; x["rule"] = JsonSerializer.SerializeToNode(rule, GlintConfig.Json); });
                }
            case "fans.removeRule":
                {
                    var c = Clone();
                    var (pname, rules) = Profile(c, p);
                    if (rules.RemoveAll(r => r.Name == S("name")) == 0) return Fail($"profile \"{pname}\" has no rule \"{S("name")}\" (rules: {string.Join(", ", rules.Select(r => r.Name))})");
                    return Commit(c, extra: x => x["profile"] = pname);
                }
            case "fans.override":
                {
                    var c = Clone();
                    var (pname, rules) = Profile(c, p);
                    var rule = rules.FirstOrDefault(r => r.Name == S("name"));
                    if (rule == null) return Fail($"profile \"{pname}\" has no rule \"{S("name")}\" (rules: {string.Join(", ", rules.Select(r => r.Name))})");
                    var pv = p["percent"];
                    if (pv == null || (pv is JsonValue sv && sv.TryGetValue<string>(out var str) && str.Equals("auto", StringComparison.OrdinalIgnoreCase))) rule.Override = null;
                    else if (pv is JsonValue nv && nv.TryGetValue<double>(out var d) && !double.IsNaN(d)) rule.Override = Math.Clamp((int)Math.Round(d), rule.MinDuty, rule.MaxDuty);
                    else return Fail("percent must be a number 0-100, or null / 'auto' for automatic");
                    return Commit(c, extra: x =>
                    {
                        x["profile"] = pname; x["rule"] = rule.Name; x["override"] = rule.Override;
                        x["mode"] = rule.Override == null ? "auto" : "manual";
                        if (rule.Override != null) x["note"] = $"clamped to the rule's {rule.MinDuty}-{rule.MaxDuty}%; the fail-safe ({c.FailsafeDuty}%) still wins if a sensor is lost or a curve reaches it";
                        if (pname != c.ActiveProfile) x["note"] = $"profile \"{pname}\" is not active; the override applies when it is";
                    });
                }
            case "curves.set":
                {
                    var c = Clone();
                    var name = S("name");
                    List<double[]>? pts;
                    try { pts = p["points"].Deserialize<List<double[]>>(GlintConfig.Json); }
                    catch (JsonException e) { return Fail("points must be [[tempC, duty%], ...]: " + e.Message); }
                    c.Curves[name] = pts!;
                    return Commit(c, extra: x => { x["curve"] = name; x["usedBy"] = JsonSerializer.SerializeToNode(CurveUsers(c, name)); });
                }
            case "curves.remove":
                {
                    var c = Clone();
                    var name = S("name");
                    if (!c.Curves.ContainsKey(name)) return Fail($"no curve \"{name}\" (curves: {string.Join(", ", c.Curves.Keys)})");
                    var users = CurveUsers(c, name);
                    if (users.Count > 0) return Fail($"curve \"{name}\" is in use by: {string.Join("; ", users)}. Point those inputs at another curve first (fans.setRule).");
                    c.Curves.Remove(name);
                    return Commit(c);
                }
            case "profiles.create":
                {
                    var c = Clone();
                    var name = S("name").Trim();
                    if (name.Length == 0) return Fail("name is empty");
                    if (c.Profiles.ContainsKey(name)) return Fail($"profile \"{name}\" already exists");
                    var from = (string?)p["from"] ?? c.ActiveProfile;
                    if (!c.Profiles.TryGetValue(from, out var src)) return Fail($"no profile \"{from}\" (profiles: {string.Join(", ", c.Profiles.Keys)})");
                    c.Profiles[name] = JsonSerializer.Deserialize<List<FanRule>>(JsonSerializer.Serialize(src, GlintConfig.Json), GlintConfig.Json)!;
                    return Commit(c, extra: x => { x["profile"] = name; x["copiedFrom"] = from; });
                }
            case "profiles.remove":
                {
                    var c = Clone();
                    var name = S("name");
                    if (!c.Profiles.ContainsKey(name)) return Fail($"no profile \"{name}\" (profiles: {string.Join(", ", c.Profiles.Keys)})");
                    if (name == c.ActiveProfile) return Fail($"\"{name}\" is the active profile; switch to another one first (profile.set)");
                    c.Profiles.Remove(name);
                    return Commit(c);
                }
            case "config.get": return Redacted(_svc.Config);
            case "config.schema": return ConfigValidator.Schema();
            case "config.validate":
                {
                    GlintConfig c;
                    try { c = p["config"].Deserialize<GlintConfig>(GlintConfig.Json) ?? throw new ArgumentException("missing 'config'"); }
                    catch (JsonException e) { return Fail("config: " + e.Message); }
                    var v = ConfigValidator.Check(c, Ctx());
                    return new { ok = v.Ok, errors = v.Errors, warnings = v.Warnings };
                }
            case "config.set":
                {
                    GlintConfig c;
                    try { c = p["config"].Deserialize<GlintConfig>(GlintConfig.Json) ?? throw new ArgumentException("missing 'config'"); }
                    catch (JsonException e) { return Fail("config: " + e.Message); }
                    KeepSecret(c);
                    return Commit(c);
                }
            case "config.patch":
                {
                    var root = JsonSerializer.SerializeToNode(_svc.Config, GlintConfig.Json)!.AsObject();
                    var parts = S("path").Split('.', StringSplitOptions.RemoveEmptyEntries);
                    JsonNode cur = root;
                    for (int i = 0; i < parts.Length - 1; i++)
                        cur = (cur is JsonObject o ? o[parts[i]] : cur is JsonArray a && int.TryParse(parts[i], out var ix) ? a[ix] : null)
                              ?? throw new KeyNotFoundException($"no '{string.Join('.', parts[..(i + 1)])}' in config");
                    var leaf = parts[^1];
                    var value = p["value"]?.DeepClone();
                    if (cur is JsonObject obj) obj[leaf] = value;
                    else if (cur is JsonArray arr && int.TryParse(leaf, out var li)) arr[li] = value;
                    else throw new KeyNotFoundException("bad path");
                    GlintConfig c;
                    try { c = root.Deserialize<GlintConfig>(GlintConfig.Json) ?? throw new ArgumentException("patch produced an invalid config"); }
                    catch (JsonException e) { return Fail($"{S("path")}: {e.Message}"); }
                    // never echo a password back
                    var shown = value?.DeepClone();
                    if (shown is JsonObject vo) foreach (var k in vo.Select(x => x.Key).Where(k => k.Contains("password", StringComparison.OrdinalIgnoreCase)).ToList()) vo[k] = "***";
                    else if (S("path").Contains("password", StringComparison.OrdinalIgnoreCase)) shown = "***";
                    return Commit(c, extra: x => { x["path"] = S("path"); x["value"] = shown; });
                }
            case "overlay.get": return _svc.Config.Overlay;
            case "overlay.set":
                {
                    var c = Clone();
                    var fields = p["overlay"] as JsonObject ?? p;
                    var node = JsonSerializer.SerializeToNode(c.Overlay ?? new OverlayConfig(), GlintConfig.Json)!.AsObject();
                    var known = typeof(OverlayConfig).GetProperties().Select(pi => pi.Name).ToList();
                    foreach (var (k, v) in fields)
                    {
                        var key = known.FirstOrDefault(x => x.Equals(k, StringComparison.OrdinalIgnoreCase));
                        if (key == null) return Fail($"unknown overlay field '{k}' (fields: {string.Join(", ", known.Select(x => char.ToLowerInvariant(x[0]) + x[1..]))})");
                        node[char.ToLowerInvariant(key[0]) + key[1..]] = v?.DeepClone();
                    }
                    try { c.Overlay = node.Deserialize<OverlayConfig>(GlintConfig.Json) ?? throw new ArgumentException("bad overlay"); }
                    catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return Fail("overlay: " + e.Message); }
                    return Commit(c, extra: x => x["overlay"] = JsonSerializer.SerializeToNode(c.Overlay, GlintConfig.Json));
                }
            case "overlay.toggle":
                {
                    var c = Clone();
                    c.Overlay ??= new();
                    c.Overlay.Enabled = (bool?)p["enabled"] ?? !c.Overlay.Enabled;
                    return Commit(c, extra: x => { x["enabled"] = c.Overlay.Enabled; x["hotkey"] = c.Overlay.Hotkey; });
                }
            case "overlay.stats":
                return _svc.Overlay?.Stats() ?? (object)new { enabled = _svc.Config.Overlay?.Enabled ?? false, visible = false, note = "no overlay in this process" };
            case "ha.status": return _svc.HomeAssistant.Status();
            case "setup.check": return SetupCheck.Run(_svc);
            case "tuning.status": return _svc.Tuning.Status();
            case "tuning.gpu.test":
                return _svc.Tuning.StartGpuTest(Int(p, "core"), Int(p, "memAb"), (double?)p?["seconds"] ?? 60, (bool?)p?["light"] ?? false);
            case "tuning.wait": return _svc.Tuning.Wait((double?)p?["seconds"] ?? 20);
            case "tuning.stop": return _svc.Tuning.Stop();
            case "tuning.gpu.apply": return _svc.Tuning.Apply(Int(p, "core"), Int(p, "memAb"));
            case "tuning.gpu.save": return _svc.Tuning.SaveResult(Int(p, "core"), Int(p, "memAb"));
            case "tuning.gpu.stock": return _svc.Tuning.Stock();
            case "tuning.cpu.status": return _svc.Cpu.Status();
            case "tuning.cpu.start": return _svc.Cpu.StartSearch(Int(p, "startValue"));
            case "tuning.cpu.finish": return _svc.Cpu.Finish((bool?)p?["force"] ?? false);
            case "tuning.cpu.stop": return _svc.Cpu.Stop();
            case "tuning.crashes":
                return new { watching = _svc.Crashes.Running, error = _svc.Crashes.LastError, events = _svc.Crashes.Records.TakeLast(50).Reverse(), cpuCoresToLoosen = _svc.Crashes.CoresToLoosen() };
            case "tuning.ai.start":
                if (_svc.Config.Tuning?.Enabled != true) return Fail("tuning is off (Settings → Tuning)");
                return _svc.Ai.Start((string?)p["goal"] ?? "gpu", (int?)(double?)p["budgetMinutes"] ?? 45);
            case "tuning.ai.stop": return _svc.Ai.Stop();
            case "tuning.ai.status": return _svc.Ai.Status();
            case "update.status": return _svc.Updater.Describe();
            case "update.install":
                {
                    if ((bool?)p["confirm"] != true)
                        return new { ok = false, started = false, message = "Not done: installing an update closes and restarts Glint. Ask the user first, then call again with confirm=true." };
                    if (_svc.Updater.CannotInstall() is { } why) return new { ok = false, started = false, message = "Can't install here: " + why + "." };
                    if (_svc.Updater.Busy) return new { ok = true, started = false, message = "An install is already running.", status = _svc.Updater.Status };
                    var st = _svc.Updater.Start();
                    return new { ok = true, started = true, message = "Started; poll update.status. Glint closes and restarts when the setup runs.", status = st };
                }
            case "log": return _svc.Rig.Log.TakeLast((int?)p["lines"] ?? 100);
            case "app.restart": _ui(() => Program.Restart()); return new { restarting = true };
            default: throw new ArgumentException($"unknown method '{method}' (try 'help')");
        }
    }

    ValidationContext Ctx() => new()
    {
        Sensors = _svc.Sensors.Keys.ToList(),
        Fans = _svc.Rig.Fans.Select(f => f.Id).ToList(),
        PluginEffects = PluginHost.Effects.Select(e => e.Name).ToList(),
        Previous = _svc.Config,
    };

    const string Hidden = "***";

    /// <summary>config.get without the stored MQTT password: even encrypted, it doesn't belong in agent transcripts or ctl output.</summary>
    static JsonNode Redacted(GlintConfig c)
    {
        var n = JsonSerializer.SerializeToNode(c, GlintConfig.Json)!;
        if (n["homeAssistant"] is JsonObject ha && ha["passwordProtected"] != null) ha["passwordProtected"] = Hidden;
        return n;
    }

    /// <summary>A config sent back from config.get carries "***": keep the stored password.</summary>
    void KeepSecret(GlintConfig c)
    {
        if (c.HomeAssistant?.PasswordProtected == Hidden) c.HomeAssistant.PasswordProtected = _svc.Config.HomeAssistant?.PasswordProtected;
    }

    static JsonObject Fail(params string[] errors) => new() { ["ok"] = false, ["errors"] = new JsonArray(errors.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()) };

    /// <summary>The one write path: validate, then (only if valid) run <paramref name="before"/> and apply + save.
    /// Invalid → {ok:false, errors, warnings} and nothing changes.</summary>
    JsonObject Commit(GlintConfig c, Action? before = null, Action<JsonObject>? extra = null)
    {
        var v = ConfigValidator.Check(c, Ctx());
        var res = new JsonObject { ["ok"] = v.Ok };
        if (!v.Ok) res["errors"] = new JsonArray(v.Errors.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        if (v.Warnings.Count > 0) res["warnings"] = new JsonArray(v.Warnings.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        if (!v.Ok) return res;
        before?.Invoke();
        _svc.Apply(c);
        if (extra != null)
        {
            var x = new JsonObject();
            extra(x);
            foreach (var (k, n) in x.ToList()) { x.Remove(k); res[k] = n; }
        }
        return res;
    }

    static (string name, List<FanRule> rules) Profile(GlintConfig c, JsonObject p)
    {
        var name = (string?)p["profile"] ?? c.ActiveProfile;
        if (!c.Profiles.TryGetValue(name, out var rules)) throw new KeyNotFoundException($"no profile \"{name}\" (profiles: {string.Join(", ", c.Profiles.Keys)})");
        return (name, rules);
    }

    static List<string> CurveUsers(GlintConfig c, string curve) =>
        c.Profiles.SelectMany(pr => pr.Value.SelectMany(r => r.Inputs.Where(i => i.Points == null && i.Curve == curve).Select(i => $"profile \"{pr.Key}\" rule \"{r.Name}\" ({i.Sensor})"))).Distinct().ToList();

    object FanList()
    {
        var cfg = _svc.Config;
        var names = cfg.FanNames;
        var connected = _svc.Rig.Fans.ToDictionary(f => f.Id, f => f.Name, StringComparer.OrdinalIgnoreCase);
        object Fan(string id) => new
        {
            id,
            name = names.TryGetValue(id, out var n) ? n : connected.TryGetValue(id, out var cn) ? cn : id,
            connected = connected.ContainsKey(id),
            duty = _svc.Duty.TryGetValue(id, out var d) ? d : (int?)null,
            rpm = _svc.Rpm.TryGetValue(id, out var r) ? r : (int?)null,
            explain = _svc.Explain.TryGetValue(id, out var e) ? e : null,
        };
        var live = _svc.Sensors.Where(k => !k.Key.StartsWith('/')).ToDictionary(k => k.Key, k => (double?)Math.Round(k.Value, 1));
        var sensors = Limits.WellKnownSensors.ToDictionary(k => k, k => live.TryGetValue(k, out var v) ? v : null);
        foreach (var (k, v) in live) sensors[k] = v;
        var inRule = cfg.Profiles.Values.SelectMany(x => x).SelectMany(r => r.Fans).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new
        {
            activeProfile = cfg.ActiveProfile,
            fanControl = cfg.FanControl,
            failsafeDuty = cfg.FailsafeDuty,
            profiles = cfg.Profiles.ToDictionary(pr => pr.Key, pr => pr.Value.Select(r => new
            {
                name = r.Name,
                active = pr.Key == cfg.ActiveProfile,
                fans = r.Fans.Select(Fan),
                inputs = r.Inputs.Select(i => new { sensor = i.Sensor, curve = i.Points == null ? i.Curve : null, points = i.Points, weight = i.Weight, hysteresis = i.Hysteresis, smoothingSec = i.SmoothingSec, value = Math.Round(FanEngine.Resolve(i.Sensor, _svc.Sensors), 1) is var t && !double.IsNaN(t) ? t : (double?)null }),
                combine = r.Combine.ToString().ToLowerInvariant(),
                minDuty = r.MinDuty, maxDuty = r.MaxDuty, rampUpPerSec = r.RampUpPerSec, rampDownPerSec = r.RampDownPerSec,
                @fixed = r.Fixed, @override = r.Override, mode = r.Fixed != null ? "fixed" : r.Override != null ? "manual" : "auto",
            }).ToList()),
            curves = cfg.Curves,
            sensors,
            sensorExpressions = "Inputs also accept max(a,b), min(a,b), avg(a,b) over these ids (nestable). Raw ids from sensors.list work too.",
            fansNotInAnyRule = _svc.Rig.Fans.Where(f => !inRule.Contains(f.Id)).Select(f => Fan(f.Id)),
        };
    }

    static string SafeName(string f)
    {
        f = Path.GetFileName(f);
        if (!System.Text.RegularExpressions.Regex.IsMatch(f, @"^[A-Za-z0-9][A-Za-z0-9._ -]{0,80}\.js$")) throw new ArgumentException("file must be a simple name ending in .js");
        return f;
    }

    public static object PluginList(GlintService? svc = null)
    {
        var status = PluginHost.StatusSnapshot().GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var list = PluginHost.AllFiles.Select(f =>
        {
            var name = Path.GetFileName(f);
            status.TryGetValue(name, out var st);
            return new
            {
                file = name, path = f, source = PluginHost.IsBundled(f) ? "bundled" : "user",
                kind = st?.Kind ?? "?", name = st?.Name ?? Path.GetFileNameWithoutExtension(f),
                enabled = st?.Message != "disabled", ok = st?.Ok ?? false, message = st?.Message ?? "", needsApproval = st?.NeedsApproval ?? false,
            };
        }).OrderBy(x => x.kind).ThenBy(x => x.name).Cast<object>().ToList();
        if (svc != null) list.Insert(0, BuiltinGpuFans(svc));
        return list;
    }

    /// <summary>Graphics-card fans are built in (LibreHardwareMonitor talks to the GPU driver, which a sandboxed plugin can't);
    /// listed with the plugins so they can be found and switched off like one. Changes take effect on restart.</summary>
    static object BuiltinGpuFans(GlintService svc)
    {
        bool want = !svc.Config.DisabledPlugins.Contains(Rig.GpuFansBuiltin, StringComparer.OrdinalIgnoreCase);
        var rig = svc.Rig;
        int fans = rig.Fans.Count(f => f.Id.StartsWith("gpu", StringComparison.Ordinal) && rig.Software.Contains(f.Id));
        string msg = want != rig.GpuFansOn ? (want ? "switched on: restart Glint" : "switched off: restart Glint")
            : !want ? "disabled"
            : fans > 0 ? $"built in · {fans} fan{(fans == 1 ? "" : "s")} on {string.Join(", ", rig.GpuFanCards)}"
            : "built in · no controllable graphics-card fans found";
        return new
        {
            file = Rig.GpuFansBuiltin, path = "", source = "built-in", kind = "device", name = "Graphics card fans",
            enabled = want, ok = !want || fans > 0, message = msg, needsApproval = false,
        };
    }

    public void Dispose() => _cts.Cancel();

    // ------------------------------------------------------------------ client

    /// <summary>One request → one JSON response line (null if Glint isn't running).</summary>
    public static string? Call(string method, JsonNode? prms, int timeoutMs = 15000)
    {
        using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        try { c.Connect(Math.Min(timeoutMs, 3000)); } catch (TimeoutException) { return null; }
        var w = new StreamWriter(c, leaveOpen: true) { AutoFlush = true };
        var r = new StreamReader(c, leaveOpen: true);
        try
        {
            w.WriteLine(JsonSerializer.Serialize(new { id = 1, method, @params = prms }));
            var t = r.ReadLineAsync();
            return t.Wait(timeoutMs) ? t.Result : null;
        }
        catch (IOException) { return null; }
    }
}
