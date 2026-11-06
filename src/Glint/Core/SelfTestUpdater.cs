using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Glint.Core;

/// <summary>
/// Offline checks for the updater (see SECURITY.md, "Updates"): version logic, download URL / host rules, size caps,
/// SHA256SUMS and attestation verification, the admin-only download folder, the final re-check and the exact launch,
/// automatic-install gating. A fake HTTP handler stands in for GitHub and a fake launcher records what would have been
/// started: no network, no process is ever started, nothing outside a temp folder is touched.
/// </summary>
static class SelfTestUpdater
{
    const string Cur = "0.2.0", Ver = "0.3.0", Tag = "v0.3.0";
    static readonly string SetupName = $"Glint-Setup-{Ver}.exe";
    static readonly string Base = Updater.DownloadPrefix + Tag + "/";
    const string AssetHost = "https://release-assets.githubusercontent.com/github-production-release-asset/1/setup?sig=x";
    const string BundleUrl = "https://tmaproduction.blob.core.windows.net/attestations/1/2026/11/06/1.json.sn?sig=x";

    public static void Run(Action<string, bool, string> C)
    {
        void R(string what, Action f) { try { f(); } catch (Exception e) { C($"updater: {what} ran", false, e.ToString()); } }
        R("version logic", () => Versions(C));
        R("verification", () => Verify(C));
        R("downloads", () => Downloads(C));
        R("folders", () => Folders(C));
        R("automatic install", () => Auto(C));
        R("pieces", () => Pieces(C));
    }

    // ------------------------------------------------------------------ a fake GitHub

    /// <summary>What the fake GitHub serves; every field can be broken by a test.</summary>
    sealed class World
    {
        public byte[] Setup = RandomNumberGenerator.GetBytes(300_000);
        public string? Sums;               // null = generated from Setup
        public string Tag = SelfTestUpdater.Tag;
        public bool Prerelease, Draft;
        public string Repo = UpdateChecker.RepoUrl, Path = Updater.WorkflowPath, Ref = "refs/tags/" + SelfTestUpdater.Tag, Predicate = Updater.ProvenanceType;
        public string? Digest;             // null = the setup's real digest
        public string Attest = "inline";   // inline | url | none | foreignUrl
        public string? Redirect = AssetHost;
        public bool NoLength;
        public readonly List<string> Requests = new();
        public string Hex => Convert.ToHexStringLower(SHA256.HashData(Setup));

        public string SumsText => Sums ?? $"{Hex}  {SetupName}\n{new string('a', 64)}  Glint-{Ver}-portable-win-x64.zip\n";

        public byte[] Bundle()
        {
            var st = new
            {
                _type = Updater.StatementType,
                subject = new[] { new { name = SetupName, digest = new Dictionary<string, string> { ["sha256"] = Digest ?? Hex } } },
                predicateType = Predicate,
                predicate = new { buildDefinition = new { buildType = "https://actions.github.io/buildtypes/workflow/v1", externalParameters = new { workflow = new { @ref = Ref, repository = Repo, path = Path } } } },
            };
            var b = new { mediaType = "application/vnd.dev.sigstore.bundle.v0.3+json", dsseEnvelope = new { payloadType = Updater.InTotoPayload, payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(st)), signatures = Array.Empty<object>() } };
            return JsonSerializer.SerializeToUtf8Bytes(b);
        }

        public HttpResponseMessage Serve(HttpRequestMessage r)
        {
            string u = r.RequestUri!.AbsoluteUri;
            Requests.Add(u);
            static HttpResponseMessage Ok(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
            if (u == UpdateChecker.ApiUrl)
                return Ok(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tag_name = Tag, html_url = UpdateChecker.RepoUrl + "/releases/tag/" + Tag, prerelease = Prerelease, draft = Draft })));
            if (u == Base + Updater.SumsName) return Ok(Encoding.UTF8.GetBytes(SumsText));
            if (u == Base + SetupName && Redirect != null)
            {
                var red = new HttpResponseMessage(HttpStatusCode.Found);
                red.Headers.Location = new Uri(Redirect);
                return red;
            }
            if (u == Base + SetupName || (Redirect != null && u == new Uri(Redirect).AbsoluteUri))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = NoLength ? new NoLengthContent(Setup) : new ByteArrayContent(Setup) };
            if (u.StartsWith(Updater.AttestationApi))
            {
                if (Attest == "none" || u != Updater.AttestationApi + Hex) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") };
                object a = Attest switch
                {
                    "inline" => new { repository_id = 1, bundle = JsonDocument.Parse(Bundle()).RootElement, bundle_url = (string?)null },
                    "foreignUrl" => new { repository_id = 1, bundle = (JsonElement?)null, bundle_url = (string?)"https://evil.example/bundle.json.sn" },
                    _ => (object)new { repository_id = 1, bundle = (JsonElement?)null, bundle_url = (string?)BundleUrl },
                };
                return Ok(JsonSerializer.SerializeToUtf8Bytes(new { attestations = new[] { a } }));
            }
            if (u == new Uri(BundleUrl).AbsoluteUri) return Ok(SnappyLiteral(Bundle()));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(f(r));
    }

    /// <summary>A body without Content-Length (chunked), to prove the cap holds while streaming.</summary>
    sealed class NoLengthContent(byte[] b) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream s, TransportContext? c) => s.WriteAsync(b).AsTask();
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    /// <summary>Valid snappy: the length, then literal chunks (what a compressor may emit for incompressible data).</summary>
    static byte[] SnappyLiteral(byte[] data)
    {
        var o = new List<byte>();
        uint n = (uint)data.Length;
        while (n >= 0x80) { o.Add((byte)(n | 0x80)); n >>= 7; }
        o.Add((byte)n);
        for (int i = 0; i < data.Length; i += 60000)
        {
            int len = Math.Min(60000, data.Length - i) - 1;
            o.Add(61 << 2); o.Add((byte)len); o.Add((byte)(len >> 8));
            o.AddRange(data.AsSpan(i, len + 1).ToArray());
        }
        return o.ToArray();
    }

    sealed record Launch(string Exe, string[] Args, string Dir, string HashAtLaunch, bool InheritedOnly, string Acl);

    /// <summary>One updater against a world, in its own temp root. The current user is added to the folder ACL only so
    /// this non-elevated test can write there (see Options.TestSids); the app never does that.</summary>
    sealed class Rig : IDisposable
    {
        public readonly World W;
        public readonly GlintConfig Cfg = GlintConfig.Defaults();
        public readonly UpdateChecker Check;
        public readonly Updater U;
        public readonly List<Launch> Launches = new();
        public readonly string Temp, Root;
        public TimeSpan Idle = TimeSpan.FromMinutes(30);
        public bool Game, Battery, Installed = true, Elevated = true;
        public DateTime Now = new(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);
        public static readonly string Me = WindowsIdentity.GetCurrent().User!.Value;

        public Rig(World? w = null, Action<Updater.Options>? tweak = null, string? root = null, bool testSids = true, string current = Cur)
        {
            W = w ?? new World();
            Temp = Path.Combine(Path.GetTempPath(), "GlintUpdTest_" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(Temp);
            Root = root ?? Path.Combine(Temp, "Glint", "updates");
            var h = new FakeHttp(W.Serve);
            Check = new UpdateChecker(() => Cfg, h, current);
            var o = new Updater.Options
            {
                Handler = h, Root = Root, TestSids = testSids ? new[] { Me } : null,
                Elevated = () => Elevated, Installed = () => Installed, Idle = () => Idle, GameInFront = () => Game, OnBattery = () => Battery, Now = () => Now,
                Launcher = (exe, args, dir) =>
                {
                    string hash = File.Exists(exe) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(exe))) : "";
                    var acl = File.Exists(exe) ? new FileInfo(exe).GetAccessControl() : null;
                    var rules = acl?.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList() ?? new();
                    Launches.Add(new Launch(exe, args.ToArray(), dir, hash, rules.Count > 0 && rules.All(r => r.IsInherited),
                        string.Join(",", rules.Select(r => r.IdentityReference.Value + ":" + r.FileSystemRights))));
                    return true;
                },
            };
            tweak?.Invoke(o);
            U = new Updater(Check, () => Cfg, o);
        }

        public UpdateStatus Install(bool auto = false) => U.InstallAsync(auto).GetAwaiter().GetResult();
        public string[] Leftovers() => Directory.Exists(Root) ? Directory.GetDirectories(Root) : Array.Empty<string>();
        public void Dispose()
        {
            U.Dispose(); Check.Dispose();
            try { Directory.Delete(Temp, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ checks

    static void Versions(Action<string, bool, string> C)
    {
        foreach (var (cur, tag, pre, draft, want, what) in new[]
        {
            ("0.2.0", "v0.3.0", false, false, true, "newer → offered"),
            ("0.2.0", "v0.2.0", false, false, false, "equal → not offered"),
            ("0.4.1", "v0.3.0", false, false, false, "older → not offered (no downgrades)"),
            ("0.2.9", "v0.2.10", false, false, true, "0.2.10 > 0.2.9 (numeric, not text)"),
            ("0.2.0", "v0.3.0", true, false, false, "pre-release flag → not offered"),
            ("0.2.0", "v0.3.0-rc.1", false, false, false, "pre-release tag → not offered"),
            ("0.2.0", "v0.3.0", false, true, false, "draft → not offered"),
            ("0.2.0", "v0.3", false, false, false, "tag that isn't vX.Y.Z → not installable"),
        })
        {
            using var r = new Rig(new World { Tag = tag, Prerelease = pre, Draft = draft }, current: cur);
            var info = r.Check.CheckNow().Result;
            bool offered = r.U.Candidate() != null;
            C($"updater: version {what}", offered == want && (tag == "v0.3" || info.Available == want), $"current {cur}, tag {tag}: candidate {offered}, available {info.Available}");
        }
        using (var r = new Rig(new World { Tag = "v0.2.0" }))
        {
            var s = r.Install();
            C("updater: nothing newer → install refused before any download", s.State == "failed" && s.Message!.Contains("No newer release") && r.Launches.Count == 0
                && !r.W.Requests.Any(u => u.StartsWith(Updater.DownloadPrefix)) && r.Leftovers().Length == 0, $"{s} {string.Join(" ", r.W.Requests)}");
        }
        using (var r = new Rig()) { r.Installed = false; var s = r.Install(); C("updater: a portable copy never installs (no request at all)", s.State == "failed" && s.Message!.Contains("portable") && r.W.Requests.Count == 0 && r.Launches.Count == 0, $"{s}"); }
        using (var r = new Rig()) { r.Elevated = false; var s = r.Install(); C("updater: not elevated → refused (no request at all)", s.State == "failed" && s.Message!.Contains("administrator") && r.W.Requests.Count == 0, $"{s}"); }
    }

    static void Verify(Action<string, bool, string> C)
    {
        // the good path, with every hop and the exact launch
        using (var r = new Rig())
        {
            var states = new List<string>();
            r.U.Changed += s => { lock (states) if (states.Count == 0 || states[^1] != s.State) states.Add(s.State); };
            var s = r.Install();
            var l = r.Launches.SingleOrDefault();
            var dir = l == null ? "" : Path.GetDirectoryName(l.Exe)!;
            var wantArgs = Updater.SetupArgs.Append("/LOG=" + Path.Combine(r.Root, "last-setup.log")).ToArray();
            C("updater: valid release → setup launched once, by exact absolute path of the verified file", s.State == "installing" && l != null
                && Path.IsPathFullyQualified(l.Exe) && l.Exe == Path.GetFullPath(l.Exe) && Path.GetFileName(l.Exe) == SetupName
                && Path.GetDirectoryName(dir) == r.Root && Guid.TryParseExact(Path.GetFileName(dir), "N", out _) && l.HashAtLaunch == r.W.Hex, $"{s} {l}");
            C("updater: silent setup arguments keep the user's tasks (no /TASKS) and skip the SignalRGB step", l != null && l.Args.SequenceEqual(wantArgs)
                && l.Args.Contains("/SILENT") && l.Args.Contains("/SUPPRESSMSGBOXES") && l.Args.Contains("/NORESTART") && !l.Args.Any(a => a.StartsWith("/TASKS")) && l.Dir == dir,
                l == null ? "no launch" : string.Join(" ", l.Args));
            C("updater: progress goes downloading → verifying → installing", states.SequenceEqual(new[] { "downloading", "verifying", "installing" }), string.Join(" → ", states));
            C("updater: only the release API, the repo's release downloads, the asset host it redirected to and the attestation API were contacted",
                r.W.Requests.SequenceEqual(new[] { UpdateChecker.ApiUrl, Base + Updater.SumsName, Base + SetupName, new Uri(AssetHost).AbsoluteUri, Updater.AttestationApi + r.W.Hex }), string.Join(" | ", r.W.Requests));
            C("updater: the downloaded setup only carries the folder's inherited admin-only permissions", l != null && l.InheritedOnly && !l.Acl.Contains("S-1-5-32-545") && !l.Acl.Contains("S-1-1-0") && !l.Acl.Contains("S-1-5-11"), l?.Acl ?? "");
        }

        // each broken piece is rejected, nothing is launched, the folder is gone
        foreach (var (what, tweak, expect) in new (string, Action<World>, string)[]
        {
            ("hash mismatch (setup differs from SHA256SUMS)", w => w.Sums = $"{new string('0', 64)}  {SetupName}\n", "doesn't match"),
            ("SHA256SUMS without a line for the setup", w => w.Sums = $"{w.Hex}  Glint-{Ver}-portable-win-x64.zip\n", "no valid line"),
            ("SHA256SUMS line for a similar name only", w => w.Sums = $"{w.Hex}  {SetupName}.bak\n", "no valid line"),
            ("no attestation (older release)", w => w.Attest = "none", "no build attestation"),
            ("attestation from another workflow", w => w.Path = ".github/workflows/ci.yml", "another workflow"),
            ("attestation from another ref", w => w.Ref = "refs/heads/main", "refs/heads/main"),
            ("attestation from another tag", w => w.Ref = "refs/tags/v0.2.0", "refs/tags/v0.2.0"),
            ("attestation from another repository", w => w.Repo = "https://github.com/someone/glint", "another repository"),
            ("attestation for another digest", w => w.Digest = new string('b', 64), "different file"),
            ("attestation that isn't SLSA provenance", w => w.Predicate = "https://in-toto.io/attestation/release/v0.2", "not SLSA provenance"),
            ("attestation bundle on an unexpected host", w => w.Attest = "foreignUrl", "unexpected host"),
        })
        {
            using var r = new Rig();
            tweak(r.W);
            var s = r.Install();
            C($"updater: rejects {what}", s.State == "failed" && s.Message!.Contains(expect) && r.Launches.Count == 0 && r.Leftovers().Length == 0, $"{s.Message} | leftovers {r.Leftovers().Length}");
        }

        // GitHub's API usually links the bundle (snappy-compressed) instead of inlining it
        using (var r = new Rig(new World { Attest = "url" }))
        {
            var s = r.Install();
            C("updater: attestation bundle fetched from bundle_url (snappy) and accepted", s.State == "installing" && r.Launches.Count == 1 && r.W.Requests.Contains(new Uri(BundleUrl).AbsoluteUri), $"{s}");
        }
        using (var r = new Rig(new World { Attest = "url", Ref = "refs/tags/v9.9.9" }))
            C("updater: a linked bundle is checked just as strictly", r.Install() is { State: "failed" } s2 && s2.Message!.Contains("v9.9.9") && r.Launches.Count == 0, $"{r.U.Status}");

        // TOCTOU: the file is swapped after verification → caught by the final re-check
        using (var r = new Rig(tweak: o => o.TestBeforeLaunch = p => File.WriteAllBytes(p, Encoding.ASCII.GetBytes("MZ evil"))))
        {
            var s = r.Install();
            C("updater: a setup changed after verification is not launched (hash re-checked before launch)", s.State == "failed" && s.Message!.Contains("changed after it was verified") && r.Launches.Count == 0 && r.Leftovers().Length == 0, $"{s}");
        }
    }

    static void Downloads(Action<string, bool, string> C)
    {
        foreach (var (what, redirect) in new[]
        {
            ("another host", "https://evil.example/Glint-Setup-0.3.0.exe"),
            ("plain http", "http://release-assets.githubusercontent.com/x"),
            ("a look-alike host", "https://release-assets.githubusercontent.com.evil.example/x"),
            ("another repository's releases", "https://github.com/someone/glint/releases/download/v0.3.0/Glint-Setup-0.3.0.exe"),
            ("credentials in the URL", "https://user@release-assets.githubusercontent.com/x"),
        })
        {
            using var r = new Rig(new World { Redirect = redirect });
            var s = r.Install();
            C($"updater: download redirected to {what} → refused", s.State == "failed" && s.Message!.Contains("unexpected host") && r.Launches.Count == 0 && !r.W.Requests.Contains(new Uri(redirect).AbsoluteUri), $"{s.Message}");
        }
        using (var r = new Rig(new World { Redirect = "https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/setup" }))
            C("updater: redirect to objects.githubusercontent.com is allowed", r.Install().State == "installing", $"{r.U.Status}");
        using (var r = new Rig(new World { Redirect = null }))
            C("updater: a direct (unredirected) download works", r.Install().State == "installing", $"{r.U.Status}");

        C("updater: first-hop URL rules", Updater.AllowedDownload(new Uri(Base + SetupName), false) && Updater.AllowedDownload(new Uri(Updater.AttestationApi + new string('a', 64)), false)
            && !Updater.AllowedDownload(new Uri("https://github.com/someone/glint/releases/download/v1.0.0/x.exe"), false)
            && !Updater.AllowedDownload(new Uri("https://objects.githubusercontent.com/x"), false)       // asset hosts only via a redirect
            && !Updater.AllowedDownload(new Uri("https://api.github.com/repos/someone/glint/attestations/sha256:aa"), false)
            && !Updater.AllowedDownload(new Uri("https://github.com:8443/jleeh/glint/releases/download/v1.0.0/x.exe"), false), "");

        // size caps: by Content-Length and while streaming without one
        foreach (bool noLen in new[] { false, true })
        {
            using var r = new Rig(new World { NoLength = noLen }, o => o.MaxSetupBytes = 100_000);
            var s = r.Install();
            C($"updater: setup larger than the cap refused ({(noLen ? "no Content-Length, cut off while streaming" : "by Content-Length")})",
                s.State == "failed" && s.Message!.Contains("larger than") && r.Launches.Count == 0 && r.Leftovers().Length == 0, $"{s.Message}");
        }
        C("updater: default size cap is 200 MB", Updater.DefaultMaxSetupBytes == 200L << 20, "");
        using (var r = new Rig(new World { Sums = new string('x', Updater.MaxSumsBytes + 10) }))
            C("updater: oversized SHA256SUMS refused", r.Install() is { State: "failed" } s3 && s3.Message!.Contains("larger than"), $"{r.U.Status}");
    }

    static void Folders(Action<string, bool, string> C)
    {
        // the default location is machine-wide and admin-only, never the user's TEMP / APPDATA
        using (var r0 = new Rig())
        using (var def = new Updater(r0.Check, () => r0.Cfg))
        {
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            C("updater: downloads go to %ProgramData%\\Glint\\updates (not TEMP / APPDATA)", def.Root == Path.Combine(pd, "Glint", "updates")
                && !def.Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                && !def.Root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
                && !def.Root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase), def.Root);
        }

        // the ACL the app uses (as built, and as written to disk by the install)
        var acl = Updater.AdminOnly();
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        string Desc(IEnumerable<FileSystemAccessRule> rs) => string.Join(", ", rs.Select(x => $"{x.IdentityReference.Value}:{x.FileSystemRights}:{x.AccessControlType}:{x.InheritanceFlags}:{(x.IsInherited ? "inh" : "")}"));
        bool AdminOnlyRules(List<FileSystemAccessRule> rs, params string[] extra)
        {
            var allowed = new HashSet<string>(new[] { "S-1-5-32-544", "S-1-5-18", Updater.OwnerRightsSid }.Concat(extra));
            return rs.All(x => allowed.Contains(x.IdentityReference.Value) && x.AccessControlType == AccessControlType.Allow && !x.IsInherited)
                && rs.Any(x => x.IdentityReference.Value == "S-1-5-32-544" && x.FileSystemRights.HasFlag(FileSystemRights.FullControl))
                && rs.Any(x => x.IdentityReference.Value == "S-1-5-18" && x.FileSystemRights.HasFlag(FileSystemRights.FullControl))
                && rs.Where(x => x.IdentityReference.Value == Updater.OwnerRightsSid).All(x => (x.FileSystemRights & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteData | FileSystemRights.Delete)) == 0);
        }
        C("updater: folder ACL = Administrators + SYSTEM full control only, inheritance off, owner Administrators, owner's implicit rights removed",
            acl.AreAccessRulesProtected && AdminOnlyRules(rules) && rules.Count == 3 && acl.GetOwner(typeof(SecurityIdentifier))?.Value == "S-1-5-32-544"
            && rules.All(x => x.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)), Desc(rules));

        using (var r = new Rig(tweak: o => o.TestBeforeLaunch = p =>
        {
            var d = new DirectoryInfo(Path.GetDirectoryName(p)!);
            var a = d.GetAccessControl();
            _seen = (a.AreAccessRulesProtected, a.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList(),
                     new DirectoryInfo(Path.GetDirectoryName(d.FullName)!).GetAccessControl().AreAccessRulesProtected);
        }))
        {
            _seen = default;
            r.Install();
            var (prot, rs, rootProt) = _seen;
            C("updater: the created download folder really is admin-only on disk (inspected ACL; the test user is there only because the test asked)",
                rs != null && prot && rootProt && AdminOnlyRules(rs, Rig.Me) && rs.Count >= 3, rs == null ? "not reached" : Desc(rs));
        }
        if (IsElevated())
        {
            var p = Path.Combine(Path.GetTempPath(), "GlintUpdAcl_" + Guid.NewGuid().ToString("N"));
            try
            {
                Updater.NewSafeDir(p);
                var a = new DirectoryInfo(p).GetAccessControl();
                var rs = a.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
                C("updater: (elevated) a real NewSafeDir on disk: protected, admin-only, owned by Administrators", a.AreAccessRulesProtected && AdminOnlyRules(rs) && a.GetOwner(typeof(SecurityIdentifier))?.Value == "S-1-5-32-544", Desc(rs));
            }
            finally { try { Directory.Delete(p, true); } catch { } }
        }

        // a root someone else created, or a link, is refused
        if (!IsElevated())
        {
            using var r = new Rig(testSids: false);
            Directory.CreateDirectory(r.Root); // owned by this (non-admin) user
            var s = r.Install();
            C("updater: an updates folder not owned by an administrator is refused", s.State == "failed" && s.Message!.Contains("wasn't created by an administrator") && r.Launches.Count == 0, $"{s.Message}");
        }
        using (var r = new Rig())
        {
            var target = Path.Combine(r.Temp, "elsewhere");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(Path.GetDirectoryName(r.Root)!);
            bool made = MakeJunction(r.Root, target);
            var s = r.Install();
            C("updater: an updates folder that is a junction is refused", made && s.State == "failed" && s.Message!.Contains("is a link") && r.Launches.Count == 0 && Directory.GetFileSystemEntries(target).Length == 0, $"junction {made}: {s.Message}");
        }

        // cleanup on start: old install folders go, nothing else
        using (var r = new Rig())
        {
            r.U.EnsureRoot();
            var a = Updater.NewSafeDir(Path.Combine(r.Root, Guid.NewGuid().ToString("N")), new[] { Rig.Me });
            var b = Updater.NewSafeDir(Path.Combine(r.Root, Guid.NewGuid().ToString("N")), new[] { Rig.Me });
            File.WriteAllText(Path.Combine(a, "Glint-Setup-0.1.0.exe"), "x");
            File.WriteAllText(Path.Combine(r.Root, "auto-attempt.json"), "{}");
            int n = r.U.CleanupOld();
            C("updater: old download folders are removed on start (other files kept)", n == 2 && !Directory.Exists(a) && !Directory.Exists(b) && File.Exists(Path.Combine(r.Root, "auto-attempt.json")), $"{n}");
        }
    }

    static (bool, List<FileSystemAccessRule>?, bool) _seen;

    static void Auto(Action<string, bool, string> C)
    {
        using var r = new Rig();
        r.Check.CheckNow().Wait();
        C("updater: automatic install is off by default", !GlintConfig.Defaults().Updates.AutoInstall && r.U.AutoBlocker() == "automatic install is off", $"{r.U.AutoBlocker()}");
        r.Cfg.Updates.AutoInstall = true;
        r.Idle = TimeSpan.FromMinutes(9);
        C("updater: auto waits for 10 minutes without input", r.U.AutoBlocker()?.Contains("in use") == true && r.U.TryAutoInstall().Result != null && r.Launches.Count == 0, $"{r.U.AutoBlocker()}");
        r.Idle = TimeSpan.FromMinutes(11); r.Game = true;
        C("updater: auto never runs with a fullscreen app / game in front", r.U.AutoBlocker()?.Contains("game") == true && r.Launches.Count == 0, $"{r.U.AutoBlocker()}");
        r.Game = false; r.Battery = true;
        C("updater: auto never runs on battery", r.U.AutoBlocker()?.Contains("battery") == true, $"{r.U.AutoBlocker()}");
        r.Battery = false; r.Cfg.Updates.Check = false;
        C("updater: auto needs the update check on", r.U.AutoBlocker() == "automatic install is off", $"{r.U.AutoBlocker()}");
        r.Cfg.Updates.Check = true;
        C("updater: idle, no game, on AC → auto may install", r.U.AutoBlocker() == null, $"{r.U.AutoBlocker()}");

        // a failing release is tried once a day, never in a loop (and the record survives a restart)
        var good = r.W.Sums; r.W.Sums = $"{new string('0', 64)}  {SetupName}\n";
        var first = r.U.TryAutoInstall().Result;
        var again = r.U.TryAutoInstall().Result;
        C("updater: auto attempts a version at most once per day (a failure doesn't loop)", first == null && r.U.Status.State == "failed" && again?.StartsWith("already tried") == true && r.Launches.Count == 0, $"{first} / {again}");
        using (var r2 = new Updater(r.Check, () => r.Cfg, new Updater.Options { Root = r.Root, TestSids = new[] { Rig.Me }, Elevated = () => true, Installed = () => true, Idle = () => TimeSpan.FromHours(1), GameInFront = () => false, OnBattery = () => false, Now = () => r.Now.AddHours(2), Launcher = (_, _, _) => throw new Exception("must not launch") }))
            C("updater: the once-a-day record survives a restart of Glint", r2.AutoBlocker()?.StartsWith("already tried") == true, $"{r2.AutoBlocker()}");
        r.W.Sums = good; r.Now = r.Now.AddHours(25);
        var next = r.U.TryAutoInstall().Result;
        C("updater: next day, auto installs the verified release", next == null && r.U.Status.State == "installing" && r.Launches.Count == 1 && r.Launches[0].HashAtLaunch == r.W.Hex, $"{next} {r.U.Status}");

        // conditions change during the download → postponed, nothing started
        using var r3 = new Rig(tweak: o => { });
        r3.Check.CheckNow().Wait();
        r3.Cfg.Updates.AutoInstall = true;
        r3.U.Changed += s => { if (s.State == "verifying") r3.Game = true; };
        var res = r3.U.TryAutoInstall().Result;
        C("updater: auto re-checks the conditions before launching (a game started meanwhile → postponed)", res == null && r3.U.Status.State == "failed" && r3.U.Status.Message!.Contains("postponed") && r3.Launches.Count == 0, $"{r3.U.Status}");
    }

    static void Pieces(Action<string, bool, string> C)
    {
        string h = new('c', 64);
        C("updater: SHA256SUMS parsing (exact name, '*' binary marker, CRLF, conflicting duplicates refused)",
            Updater.ExpectedHash($"{h}  {SetupName}\r\n", SetupName) == h && Updater.ExpectedHash($"{h} *{SetupName}\n", SetupName) == h
            && Updater.ExpectedHash($"{h}  x/{SetupName}\n", SetupName) == null && Updater.ExpectedHash($"{h}  {SetupName}\n{new string('d', 64)}  {SetupName}\n", SetupName) == null
            && Updater.ExpectedHash($"{h[..63]}  {SetupName}\n", SetupName) == null && Updater.ExpectedHash($"{h.ToUpperInvariant()}  {SetupName}\n", SetupName) == h, "");

        // snappy: a known vector with an overlapping back-reference, and malformed input
        var v = new byte[] { 9, 0x08, (byte)'a', (byte)'b', (byte)'c', 0x09, 0x03 };
        C("updater: snappy decoder (literal + overlapping copy)", Encoding.ASCII.GetString(Snappy.Decode(v, 100)) == "abcabcabc", "");
        bool Bad(byte[] b, int max = 1000) { try { Snappy.Decode(b, max); return false; } catch (InvalidDataException) { return true; } }
        C("updater: snappy decoder rejects bad offsets, truncation, overruns and size bombs", Bad(new byte[] { 9, 0x08, 97, 98, 99, 0x09, 0x05 }) && Bad(new byte[] { 9, 0x08, 97, 98 })
            && Bad(new byte[] { 3, 0x08, 97, 98, 99, 0x09, 0x03 }) && Bad(new byte[] { 0xff, 0xff, 0xff, 0x7f }, 1 << 20) && Bad(new byte[] { 2, 0x00, 97 }), "");

        // the setup check offers the install only when this copy can do it
        var inp = new SetupCheck.SetupInput { Elevated = true, Ring0 = true, SmbusOk = true, Cpu = 50, UpdatesOn = true, CannotInstall = null,
            Update = new UpdateInfo("0.2.0", "0.3.0", UpdateChecker.RepoUrl + "/releases/tag/v0.3.0", true, DateTime.UtcNow, null) };
        var env = new FakeEnv();
        var it = SetupCheck.Run(inp, env).Items.First(i => i.Id == "version");
        C("updater: setup check offers \"Install update\" when this copy can install", it is { Status: "warn", Fix: "installUpdate", FixLabel: "Install update" } && it.Detail.Contains("attestation"), $"{it}");
        inp.CannotInstall = "this is a portable copy: download the new version from the release page";
        it = SetupCheck.Run(inp, env).Items.First(i => i.Id == "version");
        C("updater: a portable copy gets the release page instead, with the reason", it is { Fix: "openUrl" } && it.Detail.Contains("portable"), $"{it}");
        inp.CannotInstall = null; inp.Install = new UpdateStatus("failed", "0.3.0", "Update not installed: no valid build attestation.", 0, 0, DateTime.UtcNow);
        it = SetupCheck.Run(inp, env).Items.First(i => i.Id == "version");
        C("updater: setup check shows why the last install failed", it.Detail.Contains("no valid build attestation"), it.Detail);

        // config
        var c = GlintConfig.Defaults(); c.Updates.AutoInstall = true; c.Updates.Check = false;
        C("updater: validator rejects autoInstall without the update check", ConfigValidator.Validate(c).Any(e => e.StartsWith("updates.autoInstall")), string.Join("; ", ConfigValidator.Validate(c)));
        c.Updates.Check = true;
        C("updater: autoInstall with the check validates", ConfigValidator.Validate(c).Count == 0, string.Join("; ", ConfigValidator.Validate(c)));
        var schema = ConfigValidator.Schema();
        C("updater: schema documents updates.autoInstall", schema["fields"]?["updates"]?["fields"]?["autoInstall"]?["default"]?.GetValue<bool>() == false, "");
    }

    sealed class FakeEnv : SetupEnv
    {
        public override IEnumerable<string> ProcessNames() => Array.Empty<string>();
        public override IEnumerable<(string Name, string Command)> RunEntries() => Array.Empty<(string, string)>();
        public override bool PawnIoInstalled() => true;
        public override string? WebView2Version() => "1.0";
        public override bool? StartupTask() => true;
    }

    static bool IsElevated() { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }

    /// <summary>A directory junction (normal users may create these), via cmd's mklink.</summary>
    static bool MakeJunction(string link, string target)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }
}
