using System.Diagnostics;
using Microsoft.Win32;

namespace Glint.Core;

/// <summary>One line on the Setup check page. Status: ok | warn | fail. Fix: what the button does (see <see cref="SetupCheck"/>).</summary>
public sealed record SetupItem(string Id, string Title, string Status, string Detail, string? FixLabel = null, string? Fix = null, string? FixArg = null, bool Important = false);
public sealed record SetupReport(DateTime Time, int Ok, int Warn, int Fail, bool ImportantFail, List<SetupItem> Items);

/// <summary>What the checks read from the system. The default reads the real PC (read-only); tests pass a fake.</summary>
public class SetupEnv
{
    public virtual IEnumerable<string> ProcessNames() { foreach (var p in Process.GetProcesses()) using (p) yield return p.ProcessName; }
    /// <summary>Startup entries from HKCU/HKLM ...\CurrentVersion\Run: (value name, command).</summary>
    public virtual IEnumerable<(string Name, string Command)> RunEntries()
    {
        var list = new List<(string, string)>();
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" })
                try
                {
                    using var k = root.OpenSubKey(path);
                    if (k == null) continue;
                    foreach (var n in k.GetValueNames()) list.Add((n, k.GetValue(n)?.ToString() ?? ""));
                }
                catch { }
        return list;
    }
    public virtual bool PawnIoInstalled() => Devices.Smbus.Available;
    public virtual string? WebView2Version()
    {
        try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { return null; }
    }
    /// <summary>Is the "Glint" logon task registered? null = couldn't tell.</summary>
    public virtual bool? StartupTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/query /tn Glint") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            return p.WaitForExit(4000) ? p.ExitCode == 0 : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// "Is my PC set up right?" in one list: admin rights, PawnIO, WebView2, CPU temperature, apps that fight over the same
/// hardware, plugins, devices that declined, the startup task, version / update, OpenRGB bridge. Computed on demand only
/// (dashboard opening the page, Re-check, setup.check); nothing here runs on a timer.
/// </summary>
public static class SetupCheck
{
    public const string PawnIoUrl = "https://pawnio.eu";
    public const string WebView2Url = "https://developer.microsoft.com/microsoft-edge/webview2/";
    public static readonly string InstallUrl = UpdateChecker.RepoUrl + "#install";
    /// <summary>URLs a fix button may open (nothing else).</summary>
    /// <summary>The only pages Glint ever opens: the two installers' pages and its own repository (exact https URLs, nothing that
    /// could be a file, another scheme or another site).</summary>
    public static bool AllowedUrl(string? u) => u != null && u.Length < 512 && !u.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\\' or '^' or '`')
        && (u == PawnIoUrl || u == WebView2Url || u == UpdateChecker.RepoUrl || u.StartsWith(UpdateChecker.RepoUrl + "/", StringComparison.Ordinal) || u.StartsWith(UpdateChecker.RepoUrl + "#", StringComparison.Ordinal))
        && Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme == Uri.UriSchemeHttps && x.UserInfo.Length == 0;

    /// <summary>Apps that drive the same RGB / fan hardware: name, process names, startup-entry keywords, what it fights over.</summary>
    public static readonly (string Name, string[] Processes, string[] RunKeys, string What)[] Conflicts =
    {
        ("SignalRGB", new[] { "SignalRgb", "SignalRgbLauncher" }, new[] { "signalrgb" }, "RGB devices"),
        ("Corsair iCUE", new[] { "iCUE", "Corsair.Service", "CorsairDeviceControlService" }, new[] { "icue" }, "Corsair devices"),
        ("ASUS Armoury Crate", new[] { "ArmouryCrate", "ArmouryCrate.Service", "LightingService", "ArmourySocketServer", "AacAmbientLighting" }, new[] { "armourycrate", "lightingservice" }, "ASUS Aura lighting"),
        ("MSI Center / Mystic Light", new[] { "MSI.CentralServer", "MSI.Center", "MSI_Center", "LEDKeeper2", "MysticLight", "Mystic_Light_Service" }, new[] { "msi center", "msi.center", "mysticlight", "mystic light", "ledkeeper" }, "MSI lighting"),
        ("NZXT CAM", new[] { "NZXT CAM" }, new[] { "nzxt cam", "nzxtcam" }, "NZXT coolers and controllers"),
        ("Lian Li L-Connect", new[] { "L-Connect 3", "L-Connect", "LConnect3", "L-Connect 3.Service" }, new[] { "l-connect", "lconnect" }, "Lian Li hubs"),
        ("Razer Synapse", new[] { "Razer Synapse", "Razer Synapse 3", "RazerAppEngine", "Razer Synapse Service", "Razer Central" }, new[] { "razer synapse", "razerappengine", "razer central" }, "Razer lighting"),
        ("Logitech G HUB", new[] { "lghub", "lghub_agent", "lghub_system_tray" }, new[] { "lghub" }, "Logitech lighting"),
        ("OpenRGB", new[] { "OpenRGB" }, new[] { "openrgb" }, "RGB devices"),
        ("FanControl", new[] { "FanControl" }, new[] { "fancontrol" }, "fan headers"),
    };

    public static SetupReport Run(GlintService svc, SetupEnv? env = null) => Run(new SetupInput(svc), env ?? new SetupEnv());

    /// <summary>Everything the checks need from a running Glint (tests build one by hand).</summary>
    public sealed class SetupInput
    {
        public bool Elevated, SmbusOk, Ring0, BridgeOn;
        public string? SmbusError;
        public double? Cpu;
        public List<string> Log = new();
        public List<Plugins.PluginStatus> Plugins = new();
        public List<string> DisabledPlugins = new();
        public UpdateInfo? Update;
        public bool UpdatesOn, AutoInstall;
        /// <summary>null = this copy can install updates itself; else why not.</summary>
        public string? CannotInstall = "not checked";
        public UpdateStatus? Install;
        public Devices.OpenRgbStatus? Bridge;
        public SetupInput() { }
        public SetupInput(GlintService s)
        {
            Elevated = s.Rig.Elevated; SmbusOk = s.Rig.SmbusOk; SmbusError = s.Rig.SmbusError; Ring0 = s.Rig.LhmRing0;
            Cpu = s.Sensors.TryGetValue("cpu", out var c) ? c : null;
            Log = s.Rig.Log.ToList(); Plugins = global::Glint.Plugins.PluginHost.Status.ToList(); DisabledPlugins = s.Config.DisabledPlugins.ToList();
            Update = s.Updates.Info; UpdatesOn = s.Config.Updates?.Check == true; AutoInstall = s.Config.Updates?.AutoInstall == true;
            CannotInstall = s.Updater.CannotInstall(); Install = s.Updater.Status;
            BridgeOn = s.Config.OpenRgb?.Enabled == true; Bridge = s.Rig.OpenRgb?.Status;
        }
    }

    public static SetupReport Run(SetupInput g, SetupEnv env)
    {
        var items = new List<SetupItem>();
        void Add(SetupItem i) => items.Add(i);

        // ---- admin
        Add(g.Elevated
            ? new("elevated", "Running as administrator", "ok", "Glint can read CPU temperatures, control motherboard fan headers and reach RAM lighting.")
            : new("elevated", "Not running as administrator", "fail", "Without admin rights Glint can't read CPU temperatures, control motherboard fan headers or reach RAM lighting. The installer's startup task runs Glint as admin without a UAC prompt.",
                  "Restart as administrator", "restartAdmin", null, true));

        // ---- PawnIO
        bool pawn = env.PawnIoInstalled();
        if (!pawn)
            Add(new("pawnio", "PawnIO driver not installed", "fail", "PawnIO is the signed driver Glint (and LibreHardwareMonitor) use for CPU temperatures, motherboard fans and RAM lighting. Install it, then restart Glint.",
                "Get PawnIO", "openUrl", PawnIoUrl, true));
        else if (!g.Elevated)
            Add(new("pawnio", "PawnIO installed (not usable without admin)", "warn", "PawnIO is installed, but only an administrator process can use it.", "Restart as administrator", "restartAdmin"));
        else if (!g.Ring0)
            Add(new("pawnio", "PawnIO installed but not working", "fail", "The PawnIO driver is installed but sensors can't use it. Restart Windows; if that doesn't help, reinstall PawnIO.", "Get PawnIO", "openUrl", PawnIoUrl, true));
        else if (!g.SmbusOk)
            Add(new("pawnio", "PawnIO working (no SMBus)", "warn", $"CPU and motherboard sensors work. RAM lighting over SMBus is unavailable: {g.SmbusError ?? "unknown reason"} (Glint's SMBus access currently supports AMD chipsets)."));
        else
            Add(new("pawnio", "PawnIO working", "ok", "Sensors and the SMBus (RAM lighting) are available."));

        // ---- WebView2
        var wv = env.WebView2Version();
        Add(wv != null
            ? new("webview2", "WebView2 runtime " + wv, "ok", "The dashboard runtime is installed.")
            : new("webview2", "WebView2 runtime missing", "fail", "The dashboard needs Microsoft's Evergreen WebView2 runtime (Glint itself, the tray and the API work without it).", "Get WebView2", "openUrl", WebView2Url));

        // ---- CPU temperature
        Add(g.Cpu is double cpu
            ? new("cpuTemp", $"CPU temperature readable ({cpu:0.0} °C)", "ok", "Fan rules that follow the CPU work.")
            : new("cpuTemp", "CPU temperature not readable", "fail", "Rules that use the CPU temperature run at the fail-safe speed (usually 100%). Glint needs to run as administrator with PawnIO installed.",
                  g.Elevated ? null : "Restart as administrator", g.Elevated ? null : "restartAdmin", null, true));

        // ---- conflicting apps
        var procs = new HashSet<string>(env.ProcessNames(), StringComparer.OrdinalIgnoreCase);
        var runs = env.RunEntries().ToList();
        int found = 0;
        foreach (var (name, pn, keys, what) in Conflicts)
        {
            bool running = pn.Any(procs.Contains);
            var run = runs.FirstOrDefault(r => keys.Any(k => r.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || r.Command.Contains(k, StringComparison.OrdinalIgnoreCase)));
            bool atStart = run.Name != null;
            if (!running && !atStart) continue;
            string id = "conflict:" + name.ToLowerInvariant().Replace(' ', '-');
            if (name == "OpenRGB" && g.BridgeOn)
            {
                Add(new(id, "OpenRGB " + (running ? "running" : "set to start"), "ok", "Glint's OpenRGB bridge uses it, and only for devices Glint doesn't drive itself."));
                continue;
            }
            found++;
            string where = running && atStart ? "is running and starts with Windows" : running ? "is running" : "starts with Windows";
            Add(new(id, $"{name} {where}", running ? "fail" : "warn",
                $"{name} also controls {what}. Two apps writing to the same device makes colours flicker and fan speeds jump. Quit it and remove it from startup (or switch off its lighting/fan control)."
                + (atStart ? $" Startup entry: \"{run.Name}\"." : ""),
                "Open startup apps", "startupApps", null, running));
        }
        if (found == 0) Add(new("conflicts", "No conflicting apps", "ok", "No other RGB or fan-control app is running or set to start with Windows."));

        // ---- plugins
        var approve = g.Plugins.Where(p => p.NeedsApproval).ToList();
        var broken = g.Plugins.Where(p => !p.Ok && !p.NeedsApproval && p.Message != "disabled" && !g.DisabledPlugins.Contains(p.File, StringComparer.OrdinalIgnoreCase)).ToList();
        if (approve.Count > 0)
            Add(new("plugins:approval", $"{approve.Count} plugin{(approve.Count == 1 ? "" : "s")} waiting for your approval", "warn",
                $"{string.Join(", ", approve.Select(p => p.Name))}: SMBus plugins only run after you approve them on the Plugins page.", "Review on Plugins page", "page", "plugins"));
        if (broken.Count > 0)
            Add(new("plugins:failed", $"{broken.Count} plugin{(broken.Count == 1 ? "" : "s")} failed to load", "fail",
                string.Join("; ", broken.Take(4).Select(p => $"{p.File}: {p.Message}")) + (broken.Count > 4 ? $"; and {broken.Count - 4} more" : ""), "Open Plugins page", "page", "plugins"));
        if (approve.Count == 0 && broken.Count == 0) Add(new("plugins", "Plugins loaded", "ok", $"{g.Plugins.Count(p => p.Ok)} plugins loaded without problems."));

        // ---- devices whose plugin declined in init (identification didn't match)
        // Not problems: SMBus plugins that probed and found none of their RAM (they scan the bus, nothing was "claimed"),
        // and spare interfaces of a device whose lighting interface did open (e.g. a keyboard's other HID collections).
        var smbusNames = g.Plugins.Where(p => p.Kind == "smbus").Select(p => p.Name).ToHashSet();
        var working = g.Log.Where(l => l.StartsWith("Plugin: ")).Select(l => l[8..].Split(" [")[0].Split(':')[0].Trim()).ToHashSet();
        static string Who(string l) => l.StartsWith('[') && l.IndexOf(']') > 0 ? l[1..l.IndexOf(']')] : "";
        var declined = g.Log.Where(l => l.Contains("init declined") || (l.Contains(" failed: ") && !l.StartsWith("LibreHardwareMonitor")))
            .Where(l => !(l.Contains("init declined") && (smbusNames.Contains(Who(l)) || working.Contains(Who(l)))))
            .Distinct().ToList();
        Add(declined.Count == 0
            ? new("devices", "No devices declined", "ok", "Every device a plugin claimed identified itself as expected.")
            : new("devices", $"{declined.Count} device{(declined.Count == 1 ? "" : "s")} declined or failed", "warn",
                "A plugin matched the USB ids but the device didn't identify as expected (or failed to open). Usually another app has it open, or it's a variant the plugin doesn't know: " + string.Join("; ", declined.Take(4)),
                "Open Devices page", "page", "devices"));

        // ---- startup task
        var task = env.StartupTask();
        Add(task switch
        {
            true => new SetupItem("startupTask", "Starts with Windows", "ok", "The \"Glint\" logon task is registered (runs Glint as admin without a UAC prompt)."),
            false => new SetupItem("startupTask", "Not set to start with Windows", "warn", "The \"Glint\" logon task isn't registered, so fans and lighting stay on their hardware defaults until you start Glint. Run the setup again (or install.ps1 for the portable version).", "How to install", "openUrl", InstallUrl),
            _ => new SetupItem("startupTask", "Startup task unknown", "warn", "Couldn't ask the Task Scheduler whether the \"Glint\" logon task exists."),
        });

        // ---- version / update
        var u = g.Update;
        string cur = u?.Current ?? UpdateChecker.CurrentVersion;
        if (!g.UpdatesOn) Add(new("version", $"Glint {cur}", "ok", "Update check is off (Settings)."));
        else if (u?.Available == true && g.CannotInstall == null)
        {
            string how = "Install downloads the setup from Glint's GitHub releases, checks it against the release's SHA-256 list and GitHub's build attestation, runs it and restarts Glint.";
            string extra = g.Install is { State: "failed", Message: { } m } ? " Last attempt: " + m
                : g.Install is { State: "downloading" or "verifying" or "installing", Message: { } p } ? " Now: " + p
                : g.AutoInstall ? " Automatic install is on: it happens when the PC has been idle for 10 minutes." : "";
            Add(new("version", $"Update available: v{u.Latest}", "warn", $"You have Glint {cur}. {how}{extra}", "Install update", "installUpdate"));
        }
        else if (u?.Available == true) Add(new("version", $"Update available: v{u.Latest}", "warn", $"You have Glint {cur}. Download the new version from the release page ({g.CannotInstall}).", "Open release page", "openUrl", u.Url ?? UpdateChecker.ReleasesUrl));
        else if (u?.Latest != null) Add(new("version", $"Glint {cur} (latest)", "ok", "You're on the newest release."));
        else Add(new("version", $"Glint {cur}", "ok", u?.CheckedAt == null ? "Update check hasn't run yet." : $"Couldn't check for updates ({u.Note})."));

        // ---- OpenRGB bridge
        if (g.BridgeOn)
        {
            var b = g.Bridge;
            if (b == null) Add(new("openrgb", "OpenRGB bridge enabled (restart needed)", "warn", "The bridge connects when Glint starts.", "Restart Glint", "restart"));
            else if (!b.Connected) Add(new("openrgb", "OpenRGB SDK server not reachable", "fail", $"Couldn't connect to {b.Server} ({b.Error}). Start OpenRGB, enable its SDK server (SDK Server tab, Start Server), then restart Glint.", "Restart Glint", "restart", null, true));
            else Add(new("openrgb", $"OpenRGB bridge: {b.Devices.Count} device{(b.Devices.Count == 1 ? "" : "s")}", b.Error != null ? "warn" : "ok",
                $"Connected to {b.Server} (protocol {b.Protocol}).{(b.Skipped.Count > 0 ? " Skipped: " + string.Join("; ", b.Skipped) + "." : "")}{(b.Error != null ? " Error: " + b.Error : "")}"));
        }

        int ok = items.Count(i => i.Status == "ok"), warn = items.Count(i => i.Status == "warn"), fail = items.Count(i => i.Status == "fail");
        return new SetupReport(DateTime.UtcNow, ok, warn, fail, items.Any(i => i.Important && i.Status == "fail"), items);
    }
}
