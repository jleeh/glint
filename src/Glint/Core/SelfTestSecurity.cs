using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Glint.Devices;
using Glint.Plugins;

namespace Glint.Core;

/// <summary>
/// Security checks (see SECURITY.md): Glint runs elevated, so file access in user folders, plugin sandboxes,
/// the control pipe, the dashboard host and network parsers must hold against a normal-privilege attacker.
/// </summary>
static class SelfTestSecurity
{
    public static void Run(Action<string, bool, string> C)
    {
        void R(string what, Action f) { try { f(); } catch (Exception e) { C($"security: {what} ran", false, e.ToString()); } }
        R("user file access", () => UserFiles(C));
        R("plugin trust", () => Trust(C));
        R("sandboxes", () => Sandboxes(C));
        R("parsers and limits", () => Limits(C));
        R("dashboard host", () => Dashboard(C));
    }

    /// <summary>A folder only "Authenticated Users" may use (protected DACL), with one file in it. Glint's restricted
    /// token is told to treat that group as deny-only, standing in for Administrators (which a non-elevated test
    /// process already has as deny-only): what the restricted token can't reach models what only an admin could.</summary>
    internal sealed class Protected : IDisposable
    {
        public readonly string Dir, Link, Secret;
        const string AuthUsers = "S-1-5-11";
        public Protected(string root)
        {
            Dir = Path.Combine(root, "protected");
            Link = Path.Combine(root, "link");
            Directory.CreateDirectory(Dir);
            Secret = Path.Combine(Dir, "secret.js");
            File.WriteAllText(Secret, "module.exports = { type: 'effect', name: 'secret', render(c, l, o) {} };");
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AuthUsers), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Dir).SetAccessControl(sec);
            var fs = new FileSecurity();
            fs.SetAccessRuleProtection(true, false);
            fs.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AuthUsers), FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(Secret).SetAccessControl(fs);
            Junction(Link, Dir);
            UserFs.TestDisableSids = new[] { AuthUsers };
        }

        public static void Junction(string link, string target)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!; p.StandardOutput.ReadToEnd(); p.WaitForExit(10000);
            if (!Directory.Exists(link)) throw new IOException("mklink /J failed");
        }

        public void Dispose()
        {
            UserFs.TestDisableSids = null;
            try { Directory.Delete(Link); } catch { }
            try
            {
                var me = WindowsIdentity.GetCurrent().User!;
                var sec = new DirectorySecurity();
                sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(Dir).SetAccessControl(sec);
                foreach (var f in Directory.GetFiles(Dir))
                {
                    var fsec = new FileSecurity(); fsec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
                    new FileInfo(f).SetAccessControl(fsec);
                }
                Directory.Delete(Dir, true);
            }
            catch { }
        }
    }

    static bool Throws(Action a) { try { a(); return false; } catch { return true; } }

    static void UserFiles(Action<string, bool, string> C)
    {
        var (denyAdmin, il) = UserFs.Describe();
        C("security: user-folder file access runs de-elevated (Administrators unusable, medium integrity or lower)", UserFs.Active && denyAdmin && il is > 0 and <= 0x2000, $"active={UserFs.Active} adminDenyOnly={denyAdmin} il=0x{il:x}");

        var root = Path.Combine(Path.GetTempPath(), "GlintSec_" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try
        {
            using var p = new Protected(root);
            bool cfgBlocked = Throws(() => GlintConfig.Defaults().Save(Path.Combine(p.Link, "config.json")));
            C("security: config save through a planted junction can't write where the user can't", cfgBlocked && !File.Exists(Path.Combine(p.Dir, "config.json")) && !File.Exists(Path.Combine(p.Dir, "config.json.tmp")), $"threw={cfgBlocked}");
            bool readBlocked = Throws(() => PluginHost.ReadCode(Path.Combine(p.Link, "secret.js")));
            C("security: plugin read through a planted junction can't read what the user can't", readBlocked, $"threw={readBlocked}");
            bool delBlocked = Throws(() => UserFs.Delete(Path.Combine(p.Link, "secret.js")));
            C("security: delete through a planted junction can't delete what the user can't", File.Exists(p.Secret), $"threw={delBlocked}");
            var old = Environment.GetEnvironmentVariable("GLINT_CONFIG_DIR");
            Environment.SetEnvironmentVariable("GLINT_CONFIG_DIR", p.Link);
            try
            {
                bool bakBlocked = Throws(() => LayoutBackups.Save(new(), "test"));
                C("security: layout backup through a planted junction is refused", bakBlocked && !Directory.Exists(Path.Combine(p.Dir, "layout-backups")), $"threw={bakBlocked}");
            }
            finally { Environment.SetEnvironmentVariable("GLINT_CONFIG_DIR", old); }
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        // ordinary use still works through the restricted token
        var ok = Path.Combine(Path.GetTempPath(), "GlintSecOk_" + Environment.ProcessId, "config.json");
        GlintConfig.Defaults().Save(ok);
        C("security: config save + load in a normal user folder still works", File.Exists(ok) && GlintConfig.Load(ok).Profiles.Count > 0, ok);
        try { Directory.Delete(Path.GetDirectoryName(ok)!, true); } catch { }
    }

    static void Trust(Action<string, bool, string> C)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GlintTrust_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            PluginTrust.TestStorePath = Path.Combine(dir, "trusted-plugins.json");
            var file = Path.Combine(dir, "ram.js");
            File.WriteAllText(file, "module.exports = { type: 'smbus', name: 'ok', addresses: [0x20] };");
            PluginTrust.Approve(file);
            var approved = File.ReadAllBytes(file);
            var swapped = Encoding.UTF8.GetBytes("module.exports = { type: 'smbus', name: 'evil', addresses: [0x20] };");
            // the bytes that will run are checked, not the file as it is on disk a moment later
            C("security: SMBus approval is checked against the exact code that runs (no swap after hashing)",
                PluginTrust.IsTrusted(file, approved) && !PluginTrust.IsTrusted(file, swapped), "");
            C("security: a file next to the bundled folder isn't treated as bundled", !PluginHost.IsBundled(Path.TrimEndingDirectorySeparator(PluginHost.BundledDir) + "-evil\\x.js") && PluginHost.IsBundled(Path.Combine(PluginHost.BundledDir, "effects", "x.js")), "");
        }
        finally { PluginTrust.TestStorePath = null; try { Directory.Delete(dir, true); } catch { } }
    }

    static void Sandboxes(Action<string, bool, string> C)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GlintSandbox_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            // device plugins (Jint): no CLR reflection or namespaces through the dev object
            var dev = Path.Combine(dir, "probe-dev.js");
            File.WriteAllText(dev, """
                module.exports = { type: 'device', id: 'probe', name: 'Probe', vendorId: 0x1234, productIds: [1],
                  init(dev) {
                    var r = [];
                    function t(n, f) { try { r.push(n + '=' + f()); } catch (e) { r.push(n + '=threw'); } }
                    t('GetType', function () { return typeof dev.GetType; });
                    t('call', function () { return String(dev.GetType().Assembly); });
                    t('importNamespace', function () { return typeof importNamespace; });
                    t('System', function () { return typeof System; });
                    t('ctorFn', function () { return typeof dev.log.constructor('return typeof importNamespace')(); });
                    dev.log('R:' + r.join(';'));
                    return {};
                  } };
                """);
            var logs = new List<string>();
            PluginHost.LoadForTest(dev).device!.OpenWith(new SelfTest.FakePort(0x1234, 1, 8), x => x, (z, n) => n, logs.Add);
            var res = logs.FirstOrDefault(l => l.Contains("R:")) ?? string.Join(" | ", logs);
            C("security: device plugin sandbox exposes no .NET reflection / namespaces", res.Contains("GetType=undefined") && res.Contains("call=threw") && res.Contains("importNamespace=undefined") && res.Contains("System=undefined") && !res.Contains("ctorFn=function") && !res.Contains("ctorFn=object"), res);

            // effect plugins (V8): no host objects, typed arrays can't grab unbounded memory
            var fx = Path.Combine(dir, "probe-fx.js");
            File.WriteAllText(fx, """
                module.exports = { type: 'effect', name: 'Probe', render(ctx, leds, out) {
                  var r = [];
                  function t(n, f) { try { r.push(n + '=' + f()); } catch (e) { r.push(n + '=threw'); } }
                  t('host', function () { return typeof host + typeof xHost + typeof clr + typeof require + typeof fetch + typeof process; });
                  t('big', function () { var a = new ArrayBuffer(200 * 1024 * 1024); return a.byteLength; });
                  throw new Error('R:' + r.join(';'));
                } };
                """);
            var (ep, _) = PluginHost.LoadForTest(fx);
            using var pe = new PluginEffect(new EffectConfig { Type = EffectType.Plugin, Plugin = "Probe" }, ep!);
            var leds = new[] { new Led(0, 0, 0.5, 0.5, false) };
            pe.Render(new FxContext(), leds, new Rgb[1]);
            C("security: effect plugin sandbox has no host objects", pe.Error.Contains("host=" + string.Concat(Enumerable.Repeat("undefined", 6))), pe.Error);
            C("security: effect plugin can't allocate a 200 MB ArrayBuffer (outside the V8 heap cap)", pe.Error.Contains("big=threw"), pe.Error);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void Limits(Action<string, bool, string> C)
    {
        // control pipe: one endless line is refused instead of buffered
        bool tooLong = false;
        try { ControlServer.ReadLine(new StringReader(new string('x', ControlServer.MaxLine + 10)), ControlServer.MaxLine); }
        catch (InvalidDataException) { tooLong = true; }
        C("security: control pipe refuses an oversized request line", tooLong && ControlServer.ReadLine(new StringReader("{\"a\":1}\r\nnext"), 100) == "{\"a\":1}", "");
        var sec = ControlServer.Security();
        var rules = sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<System.IO.Pipes.PipeAccessRule>().ToList();
        C("security: control pipe denies network clients and allows only this user",
            rules.Any(r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Value == "S-1-5-2")
            && rules.Where(r => r.AccessControlType == AccessControlType.Allow).All(r => r.IdentityReference.Value == WindowsIdentity.GetCurrent().User!.Value || r.IdentityReference.Value == "S-1-5-32-544"),
            string.Join("; ", rules.Select(r => $"{r.AccessControlType} {r.IdentityReference} {r.PipeAccessRights}")));

        // device log: plugins write to it, it must stay bounded
        var log = new LogBuffer();
        for (int i = 0; i < 5000; i++) log.Add(new string('x', 5000));
        C("security: device/plugin log is bounded (lines and length)", log.Count == LogBuffer.MaxLines && log.All(l => l.Length <= LogBuffer.MaxLineLength + 1), $"{log.Count}");

        // OpenRGB server (another program, maybe on the LAN): absurd sizes are rejected, not allocated
        static byte[] Controller(uint leds, uint mh, uint mw)
        {
            var b = new List<byte>();
            void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));
            void U16(int v) => b.AddRange(BitConverter.GetBytes((ushort)v));
            void Str(string s) { U16(s.Length + 1); b.AddRange(Encoding.ASCII.GetBytes(s)); b.Add(0); }
            U32(0); U32(0); Str("dev"); Str("v"); Str("d"); Str("1"); Str("s"); Str("loc");
            U16(0); U32(0);          // no modes, active mode
            U16(1);                  // one zone
            Str("z"); U32(1); U32(0); U32(0); U32(leds);
            if (mh > 0) { U16(8); U32(mh); U32(mw); } else U16(0);
            U16(0);                  // segments (protocol 4)
            U16(0);                  // leds
            return b.ToArray();
        }
        bool Rejects(byte[] d) { try { OpenRgbProtocol.ParseController(0, d, 4); return false; } catch (InvalidDataException) { return true; } catch (Exception) { return false; } }
        C("security: OpenRGB parser rejects a zone claiming 4 billion LEDs", Rejects(Controller(0xFFFFFFFF, 0, 0)), "");
        C("security: OpenRGB parser rejects a 65536x65536 matrix without allocating it", Rejects(Controller(10, 0x10000, 0x10000)) && Rejects(Controller(10, 50000, 50000)), "");
        C("security: OpenRGB parser still accepts a normal device", !Rejects(Controller(10, 0, 0)), "");

        // config.json: valid JSON with nulls where objects belong must not crash the elevated service at startup
        var dir = Path.Combine(Path.GetTempPath(), "GlintCfgNull_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "config.json");
            File.WriteAllText(f, """{ "lighting": null, "curves": null, "profiles": { "Balanced": [ null, { "name": "r", "fans": null, "inputs": [ null ] } ] }, "homeAssistant": null, "overlay": null, "openRgb": null, "updates": null, "sensors": null, "fanNames": null, "disabledPlugins": [ null ] }""");
            string err = "";
            try
            {
                var cfg = GlintConfig.Load(f);
                using var svc = new GlintService(cfg, new Rig(cfg, useLhm: false, hardware: false)) { NoSave = true };
                svc.Apply(cfg, save: false);
                Thread.Sleep(150);
            }
            catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
            C("security: config.json with nulls in place of sections loads and runs", err == "", err);
            using (var fs = new FileStream(f, FileMode.Create)) fs.SetLength(GlintConfig.MaxFileBytes + 1);
            C("security: oversized config.json is refused, not read into memory", Throws(() => GlintConfig.Load(f)), "");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // update check: an unbounded answer is not buffered
        var pad = new string('x', UpdateChecker.MaxResponseBytes + 1000);
        var big = new UpdateChecker(() => new GlintConfig(), new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent($"{{\"tag_name\":\"v9.9.9\",\"html_url\":\"{UpdateChecker.RepoUrl}/releases/tag/v9.9.9\",\"pad\":\"{pad}\"}}") }), "0.1.0");
        var info = big.CheckNow().GetAwaiter().GetResult();
        C("security: update check refuses an oversized response", !info.Available && info.Latest == null, $"{info}");
    }

    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) => Task.FromResult(f(r));
    }

    static void Dashboard(Action<string, bool, string> C)
    {
        string[] bad = { "https://github.com/jleeh/glint@evil.example", "https://github.com/jleeh/glint.evil.example/", "https://github.com/jleeh/glintx",
            "file:///C:/Windows/System32/calc.exe", "https://github.com/jleeh/glint/\" --x", "http://github.com/jleeh/glint/", "https://user@github.com/jleeh/glint/" };
        C("security: only Glint's own / installer https pages can be opened", bad.All(u => !SetupCheck.AllowedUrl(u)) && SetupCheck.AllowedUrl(UpdateChecker.ReleasesUrl) && SetupCheck.AllowedUrl(SetupCheck.PawnIoUrl),
            string.Join(", ", bad.Where(SetupCheck.AllowedUrl)));
        C("security: dashboard accepts messages / navigation only from https://glint.local/",
            UI.WebDashboard.IsLocal("https://glint.local/index.html") && !UI.WebDashboard.IsLocal("https://glint.local.evil.example/") && !UI.WebDashboard.IsLocal("https://evil.example/?https://glint.local/") && !UI.WebDashboard.IsLocal(null), "");
        Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", @"C:\Users\Public\evil");
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--no-sandbox");
        UI.WebDashboard.ClearWebView2Overrides();
        C("security: WEBVIEW2_* overrides from the user environment are dropped before the browser starts",
            Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER") == null && Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") == null, "");
        var elevatedData = UI.WebDashboard.DataFolder(true);
        C("security: elevated dashboard keeps its browser profile under the install folder, not the user profile",
            elevatedData.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) && !elevatedData.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase), elevatedData);
    }
}
