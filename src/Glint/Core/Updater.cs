using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glint.Core;

/// <summary>Where an install is. State: idle | downloading | verifying | installing | failed.</summary>
public sealed record UpdateStatus(string State, string? Version, string? Message, long Bytes, long Total, DateTime At);

/// <summary>
/// One-click (and optional automatic) update. Glint runs elevated, so this is treated like the installer's own
/// downloads (see SECURITY.md, "Updates"):
/// <list type="bullet">
/// <item>Only a release of <see cref="UpdateChecker.Repo"/> that is newer than this build (no pre-releases or drafts).</item>
/// <item>Downloads only from that repository's /releases/download/v&lt;version&gt;/ URLs, following redirects only to
///   GitHub's release-asset hosts, with size caps and timeouts, into a fresh folder under %ProgramData%\Glint\updates
///   that only Administrators and SYSTEM can open (inheritance off). Never the user's TEMP or APPDATA.</item>
/// <item>The setup must match its line in the release's SHA256SUMS.txt AND have a GitHub build-provenance attestation
///   (api.github.com) whose subject is that digest, made by this repository's release workflow for exactly that tag.</item>
/// <item>The hash is checked again right before launch while the file is held open against writes, and the setup is
///   started by absolute path with its TEMP inside the protected folder.</item>
/// </list>
/// Any mismatch aborts, deletes the folder and reports why. No thread of its own: work runs on the thread pool only
/// while an install (or a pending automatic install) is in progress.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string SetupPrefix = "Glint-Setup-";
    public const string SumsName = "SHA256SUMS.txt";
    public const string DownloadPrefix = UpdateChecker.RepoUrl + "/releases/download/";
    public const string AttestationApi = UpdateChecker.ApiRepoUrl + "/attestations/sha256:";
    public const string WorkflowPath = ".github/workflows/release.yml";
    public const string ProvenanceType = "https://slsa.dev/provenance/v1";
    public const string StatementType = "https://in-toto.io/Statement/v1";
    public const string InTotoPayload = "application/vnd.in-toto+json";
    /// <summary>Hosts GitHub redirects release downloads to (exact names, https only).</summary>
    public static readonly string[] AssetHosts = { "objects.githubusercontent.com", "release-assets.githubusercontent.com" };
    /// <summary>Where the attestations API stores bundles it doesn't inline (bundle_url; snappy-compressed JSON).</summary>
    public static readonly string[] BundleHosts = { "tmaproduction.blob.core.windows.net" };
    public const long DefaultMaxSetupBytes = 200L << 20;
    public const int MaxSumsBytes = 64 << 10, MaxApiBytes = 8 << 20, MaxBundleBytes = 8 << 20;
    public static readonly TimeSpan AutoIdle = TimeSpan.FromMinutes(10), AutoRetry = TimeSpan.FromHours(24), AutoPoll = TimeSpan.FromMinutes(10);
    /// <summary>Setup switches: silent with progress, no prompts, no reboot, keep the tasks chosen last time (Inno restores
    /// them) except the one-off "disable SignalRGB" step. PawnIO / WebView2 tasks only exist when those are missing.</summary>
    public static readonly string[] SetupArgs = { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL", "/MERGETASKS=!disablesignalrgb" };
    static readonly Regex TagRx = new(@"^v(\d{1,4})\.(\d{1,4})\.(\d{1,6})$");
    static readonly Regex HexRx = new("^[0-9a-f]{64}$");

    /// <summary>Everything outside the process: tests replace all of it (no network, no process start, no real input state).</summary>
    public sealed class Options
    {
        public HttpMessageHandler? Handler;
        /// <summary>Folder holding the per-install folders (default %ProgramData%\Glint\updates).</summary>
        public string? Root;
        /// <summary>Starts the verified setup: (absolute exe path, arguments, working/TEMP folder) → started.</summary>
        public Func<string, IReadOnlyList<string>, string, bool>? Launcher;
        public Func<bool>? Elevated, Installed, GameInFront, OnBattery;
        public Func<TimeSpan>? Idle;
        public Func<DateTime>? Now;
        public long MaxSetupBytes = DefaultMaxSetupBytes;
        /// <summary>Tests only: extra SIDs given access to the folders / accepted as their owner, so a non-elevated test
        /// process can use them. Never set in the app.</summary>
        public string[]? TestSids;
        /// <summary>Tests only: runs after verification, before the final hash re-check (simulates a swapped file).</summary>
        public Action<string>? TestBeforeLaunch;
    }

    readonly UpdateChecker _check;
    readonly Func<GlintConfig> _cfg;
    readonly Options _o;
    readonly HttpMessageInvoker _http;
    readonly CancellationTokenSource _cts = new();
    int _busy, _autoLoop;
    string? _activeDir;
    public UpdateStatus Status { get; private set; }
    public event Action<UpdateStatus>? Changed;
    public string Root { get; }

    public Updater(UpdateChecker check, Func<GlintConfig> cfg, Options? options = null)
    {
        _check = check; _cfg = cfg; _o = options ?? new Options();
        Root = _o.Root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Glint", "updates");
        // redirects are followed by hand so every hop is checked against the allowed hosts
        _http = new HttpMessageInvoker(_o.Handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(1), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) });
        Status = new UpdateStatus("idle", null, null, 0, 0, Now);
    }

    DateTime Now => _o.Now?.Invoke() ?? DateTime.UtcNow;
    bool Elevated => _o.Elevated?.Invoke() ?? IsElevated();
    bool Installed => _o.Installed?.Invoke() ?? File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
    public bool Busy => Volatile.Read(ref _busy) == 1;

    /// <summary>Can this copy update itself? (null = yes, else why not, in plain words.)</summary>
    public string? CannotInstall()
    {
        if (!Installed) return "this is a portable copy: download the new version from the release page";
        if (!Elevated) return "Glint isn't running as administrator, which installing needs";
        return null;
    }

    /// <summary>The version an install would get (a newer, non-pre-release release with a plain vX.Y.Z tag), else null.</summary>
    public ReleaseInfo? Candidate()
    {
        var r = _check.Release;
        if (r == null || r.Prerelease || r.Draft || !TagRx.IsMatch(r.Tag)) return null;
        if (UpdateChecker.Parse(_check.Current) is not { } cur || r.Version <= cur) return null;
        return r;
    }

    /// <summary>Everything the dashboard / API shows about updates.</summary>
    public object Describe()
    {
        var i = _check.Info; var c = _cfg().Updates;
        return new
        {
            current = i.Current, latest = i.Latest, available = i.Available, url = i.Url ?? UpdateChecker.ReleasesUrl, checkedAt = i.CheckedAt, note = i.Note,
            check = c?.Check ?? false, autoInstall = c?.AutoInstall ?? false,
            canInstall = CannotInstall() == null, cannotInstall = CannotInstall(),
            install = Status,
        };
    }

    UpdateStatus Set(string state, string? version, string? msg, long bytes = 0, long total = 0)
    {
        var s = new UpdateStatus(state, version, msg, bytes, total, Now);
        Status = s;
        try { Changed?.Invoke(s); } catch { }
        return s;
    }

    /// <summary>Start an install in the background (dashboard / tray / API). Returns at once with the status.</summary>
    public UpdateStatus Start()
    {
        if (Busy) return Status;
        _ = InstallAsync();
        return Status;
    }

    /// <summary>Check, download, verify, launch. Never throws; the result is the final status ("installing" = the setup
    /// was started and will close and restart Glint; "failed" = nothing was run, Message says why).</summary>
    public async Task<UpdateStatus> InstallAsync(bool auto = false)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Status;
        string? dir = null; string? ver = null;
        try
        {
            if (CannotInstall() is { } why) return Set("failed", null, "Can't install: " + why + ".");
            Set("downloading", null, "Checking for the latest release…");
            if (Candidate() == null) await _check.CheckNow();   // a fresh answer from releases/latest
            if (Candidate() is not { } rel) return Set("failed", null, _check.Info.Latest is { } l ? $"No newer release to install (latest is {l})." : "Couldn't find a newer release (" + (_check.Info.Note ?? "update check failed") + ").");
            ver = rel.Version.ToString(3);
            if (auto) RecordAutoAttempt(ver);

            string setupName = SetupPrefix + ver + ".exe";
            string baseUrl = DownloadPrefix + rel.Tag + "/";
            EnsureRoot();
            dir = NewSafeDir(Path.Combine(Root, Guid.NewGuid().ToString("N")), _o.TestSids);
            _activeDir = dir;

            Set("downloading", ver, "Downloading checksums…");
            var sums = await Download(baseUrl + SumsName, MaxSumsBytes, null);
            string expected = ExpectedHash(Encoding.UTF8.GetString(sums), setupName) ?? throw new UpdateException($"{SumsName} has no valid line for {setupName}");

            string path = Path.Combine(dir, setupName);
            Set("downloading", ver, $"Downloading {setupName}…");
            string actual = await DownloadToFile(baseUrl + setupName, path, _o.MaxSetupBytes, ver);

            Set("verifying", ver, "Verifying checksum and build attestation…");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)))
                throw new UpdateException($"{setupName} doesn't match {SumsName} (got {actual[..12]}…, expected {expected[..12]}…)");
            await VerifyAttestation(actual, ver);

            if (auto && AutoBlocker(requireRelease: false) is { } later) throw new UpdateException("postponed: " + later);

            _o.TestBeforeLaunch?.Invoke(path);
            Set("installing", ver, $"Starting the Glint {ver} setup. Glint closes and restarts by itself.");
            // TOCTOU: hold the file open against writes/renames from here until the process has started, and hash it again
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var again = Convert.ToHexStringLower(await SHA256.HashDataAsync(hold));
                if (again != actual) throw new UpdateException("the downloaded setup changed after it was verified");
                string full = Path.GetFullPath(path);
                var args = SetupArgs.Append("/LOG=" + Path.Combine(Root, "last-setup.log")).ToArray();
                bool started = (_o.Launcher ?? Launch)(full, args, dir);
                if (!started) throw new UpdateException("the setup could not be started");
            }
            dir = null; // the setup is using it; removed on the next start
            return Status;
        }
        catch (Exception e)
        {
            string msg = e switch
            {
                UpdateException u => u.Message,
                HttpRequestException or IOException when e.Message.Length > 0 => "download failed: " + e.Message,
                OperationCanceledException => "download timed out",
                UnauthorizedAccessException => "access denied: " + e.Message,
                _ => e.Message,
            };
            return Set("failed", ver, "Update not installed: " + msg + ".");
        }
        finally
        {
            if (dir != null) { TryDelete(dir); _activeDir = null; }
            _busy = 0;
        }
    }

    // ------------------------------------------------------------------ verification pieces (public for the tests)

    /// <summary>The hash for exactly <paramref name="file"/> in a SHA256SUMS text ("hex  name" per line), or null if
    /// missing, malformed or listed twice with different hashes.</summary>
    public static string? ExpectedHash(string sums, string file)
    {
        string? found = null;
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = Regex.Match(line, @"^([0-9a-fA-F]{64}) [ *](.+)$");
            if (!m.Success || m.Groups[2].Value != file) continue;
            var h = m.Groups[1].Value.ToLowerInvariant();
            if (found != null && found != h) return null;
            found = h;
        }
        return found;
    }

    /// <summary>Does this Sigstore bundle (JSON) carry an in-toto SLSA provenance statement for <paramref name="hex"/>
    /// made by this repository's release workflow for tag v<paramref name="version"/>? null = yes, else the reason.</summary>
    public static string? CheckBundle(ReadOnlySpan<byte> bundleJson, string hex, string version)
    {
        try
        {
            using var b = JsonDocument.Parse(bundleJson.ToArray());
            if (!b.RootElement.TryGetProperty("dsseEnvelope", out var env) || env.ValueKind != JsonValueKind.Object) return "no DSSE envelope";
            if (Str(env, "payloadType") != InTotoPayload) return "not an in-toto statement";
            var payload = Str(env, "payload");
            if (payload == null) return "empty payload";
            using var st = JsonDocument.Parse(Convert.FromBase64String(payload));
            var r = st.RootElement;
            if (Str(r, "_type") != StatementType) return "unknown statement type";
            if (Str(r, "predicateType") != ProvenanceType) return $"predicate {Str(r, "predicateType")} is not SLSA provenance v1";
            if (!r.TryGetProperty("subject", out var subj) || subj.ValueKind != JsonValueKind.Array
                || !subj.EnumerateArray().Any(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.Object
                                                   && string.Equals(Str(d, "sha256"), hex, StringComparison.OrdinalIgnoreCase)))
                return "statement is for a different file (digest mismatch)";
            if (!r.TryGetProperty("predicate", out var pred) || !pred.TryGetProperty("buildDefinition", out var bd) || !bd.TryGetProperty("externalParameters", out var ep)
                || !ep.TryGetProperty("workflow", out var wf) || wf.ValueKind != JsonValueKind.Object) return "no workflow in the provenance";
            if (Str(wf, "repository") != UpdateChecker.RepoUrl) return $"built by another repository ({Str(wf, "repository")})";
            if (Str(wf, "path") != WorkflowPath) return $"built by another workflow ({Str(wf, "path")})";
            if (Str(wf, "ref") != "refs/tags/v" + version) return $"built from {Str(wf, "ref")}, not refs/tags/v{version}";
            return null;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { return "malformed attestation"; }
    }

    static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    async Task VerifyAttestation(string hex, string version)
    {
        var list = await Download(AttestationApi + hex, MaxApiBytes, "application/vnd.github+json", notFound: true);
        if (list.Length == 0) throw new UpdateException("GitHub has no build attestation for this setup (releases before attestations were added can't be installed from Glint)");
        var reasons = new List<string>();
        using var doc = JsonDocument.Parse(list);
        if (!doc.RootElement.TryGetProperty("attestations", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            throw new UpdateException("GitHub has no build attestation for this setup");
        foreach (var a in arr.EnumerateArray().Take(20))
        {
            byte[]? bundle = null;
            if (a.TryGetProperty("bundle", out var inline) && inline.ValueKind == JsonValueKind.Object) bundle = Encoding.UTF8.GetBytes(inline.GetRawText());
            else if (Str(a, "bundle_url") is { } bu)
            {
                if (!Uri.TryCreate(bu, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !BundleHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase) || u.UserInfo.Length > 0)
                { reasons.Add("bundle stored on an unexpected host"); continue; }
                var raw = await Download(bu, MaxBundleBytes, null);
                bundle = raw.Length > 0 && raw[0] == (byte)'{' ? raw : Snappy.Decode(raw, MaxBundleBytes * 4);
            }
            if (bundle == null) { reasons.Add("empty attestation"); continue; }
            var why = CheckBundle(bundle, hex, version);
            if (why == null) return;
            reasons.Add(why);
        }
        throw new UpdateException("no valid build attestation from Glint's release workflow: " + string.Join("; ", reasons.Distinct().Take(3)));
    }

    // ------------------------------------------------------------------ downloads

    /// <summary>Is this URL one we may fetch? First hop: the exact asset / API URL we built. Redirects (downloads only):
    /// https to GitHub's asset hosts or back into this repository's release downloads.</summary>
    public static bool AllowedDownload(Uri u, bool redirect)
    {
        if (u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0 || !u.IsDefaultPort) return false;
        string s = u.AbsoluteUri;
        if (s.StartsWith(DownloadPrefix, StringComparison.Ordinal) && !s[DownloadPrefix.Length..].Contains("..")) return true;
        if (!redirect) return s.StartsWith(AttestationApi, StringComparison.Ordinal) || BundleHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);
        return AssetHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);
    }

    async Task<HttpResponseMessage> Get(string url, string? accept, CancellationToken ct)
    {
        var u = new Uri(url);
        bool download = url.StartsWith(DownloadPrefix, StringComparison.Ordinal);
        if (!AllowedDownload(u, false)) throw new UpdateException($"refusing to download from {u.Host}");
        for (int hop = 0; hop < 6; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, u);
            req.Headers.UserAgent.ParseAdd($"Glint/{_check.Current}");
            if (accept != null) req.Headers.Accept.ParseAdd(accept);
            var resp = await _http.SendAsync(req, ct);
            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308)
            {
                var loc = resp.Headers.Location;
                resp.Dispose();
                if (!download || loc == null) throw new UpdateException("unexpected redirect");
                var next = loc.IsAbsoluteUri ? loc : new Uri(u, loc);
                if (!AllowedDownload(next, true)) throw new UpdateException($"download redirected to an unexpected host ({next.Host})");
                u = next;
                continue;
            }
            return resp;
        }
        throw new UpdateException("too many redirects");
    }

    /// <summary>Small download into memory, capped. notFound: 404 returns an empty array instead of failing.</summary>
    async Task<byte[]> Download(string url, int max, string? accept, bool notFound = false)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        t.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await Get(url, accept, t.Token);
        if (notFound && resp.StatusCode == HttpStatusCode.NotFound) return Array.Empty<byte>();
        if (!resp.IsSuccessStatusCode) throw new UpdateException($"{Path.GetFileName(new Uri(url).AbsolutePath)}: GitHub answered {(int)resp.StatusCode}");
        if (resp.Content.Headers.ContentLength > max) throw new UpdateException($"{Path.GetFileName(new Uri(url).AbsolutePath)} is larger than expected");
        var buf = new MemoryStream();
        await using var st = await resp.Content.ReadAsStreamAsync(t.Token);
        var chunk = new byte[16384]; int n;
        while ((n = await st.ReadAsync(chunk, t.Token)) > 0)
        {
            buf.Write(chunk, 0, n);
            if (buf.Length > max) throw new UpdateException($"{Path.GetFileName(new Uri(url).AbsolutePath)} is larger than expected");
        }
        return buf.ToArray();
    }

    /// <summary>Stream the setup to disk (new file in the protected folder), hashing as it goes. Returns lowercase hex.</summary>
    async Task<string> DownloadToFile(string url, string path, long max, string ver)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        total.CancelAfter(TimeSpan.FromMinutes(20));
        using var resp = await Get(url, null, total.Token);
        if (!resp.IsSuccessStatusCode) throw new UpdateException($"{Path.GetFileName(path)}: GitHub answered {(int)resp.StatusCode}");
        long? len = resp.Content.Headers.ContentLength;
        if (len > max) throw new UpdateException($"{Path.GetFileName(path)} is larger than the {Size(max)} limit");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var st = await resp.Content.ReadAsStreamAsync(total.Token))
        await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            var chunk = new byte[1 << 16]; long got = 0; int n; var last = DateTime.UtcNow;
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                stall.CancelAfter(TimeSpan.FromSeconds(60)); // no data for a minute: give up
                n = await st.ReadAsync(chunk, stall.Token);
                if (n <= 0) break;
                got += n;
                if (got > max) throw new UpdateException($"{Path.GetFileName(path)} is larger than the {Size(max)} limit");
                sha.AppendData(chunk, 0, n);
                await fs.WriteAsync(chunk.AsMemory(0, n), total.Token);
                if ((DateTime.UtcNow - last).TotalMilliseconds > 250) { last = DateTime.UtcNow; Set("downloading", ver, $"Downloading {Path.GetFileName(path)}…", got, len ?? 0); }
            }
            if (len is long l && got != l) throw new UpdateException("download incomplete");
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    static string Size(long b) => b >= 1 << 20 ? $"{b >> 20} MB" : $"{b >> 10} KB";

    // ------------------------------------------------------------------ protected folders

    static readonly string[] AdminSids = { "S-1-5-32-544", "S-1-5-18" }; // Administrators, SYSTEM
    static readonly string[] TrustedOwners = { "S-1-5-32-544", "S-1-5-18", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" }; // + TrustedInstaller

    public const string OwnerRightsSid = "S-1-3-4";

    /// <summary>Administrators + SYSTEM full control (plus test SIDs), inherited by everything inside, nothing inherited
    /// from above, owned by Administrators. The OWNER RIGHTS entry takes away an owner's implicit right to change
    /// permissions: on PCs where new files are owned by the creating user rather than Administrators, that user's
    /// non-elevated programs could otherwise re-open the downloaded setup for writing.</summary>
    public static DirectorySecurity AdminOnly(string[]? extra = null)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in AdminSids.Concat(extra ?? Array.Empty<string>()))
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(OwnerRightsSid), FileSystemRights.ReadAttributes, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (extra == null) acl.SetOwner(new SecurityIdentifier(AdminSids[0])); // tests (not elevated) can't assign it
        return acl;
    }

    /// <summary>Create <paramref name="path"/> (must not exist) with the admin-only ACL. Like helper.ps1's New-SafeDir.</summary>
    public static string NewSafeDir(string path, string[]? extra = null)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new UpdateException("download folder already exists");
        new DirectoryInfo(path).Create(AdminOnly(extra));
        CheckTrusted(path, extra);
        return path;
    }

    /// <summary>%ProgramData%\Glint and %ProgramData%\Glint\updates: created admin-only, or if they exist, real folders
    /// (no junction / link) owned by an administrator, re-locked to admin-only. Anything else is refused: a folder a
    /// normal user created there could have its contents swapped.</summary>
    public void EnsureRoot()
    {
        var parent = Path.GetDirectoryName(Root)!;
        foreach (var d in new[] { parent, Root })
        {
            if (!Directory.Exists(d))
            {
                if (File.Exists(d)) throw new UpdateException($"{d} is a file");
                new DirectoryInfo(d).Create(AdminOnly(_o.TestSids));
            }
            CheckTrusted(d, _o.TestSids);
            new DirectoryInfo(d).SetAccessControl(AdminOnly(_o.TestSids));
        }
    }

    static void CheckTrusted(string d, string[]? extra)
    {
        var di = new DirectoryInfo(d);
        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UpdateException($"{d} is a link, not a folder; delete it and try again");
        var owner = di.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))?.Value;
        if (owner == null || !TrustedOwners.Concat(extra ?? Array.Empty<string>()).Contains(owner))
            throw new UpdateException($"{d} wasn't created by an administrator; delete it and try again");
    }

    /// <summary>Remove finished / abandoned install folders (on start, after a short delay). Leaves the root alone if it
    /// isn't trustworthy, and never follows links.</summary>
    public int CleanupOld()
    {
        int n = 0;
        try
        {
            if (!Directory.Exists(Root)) return 0;
            CheckTrusted(Root, _o.TestSids);
            foreach (var d in Directory.GetDirectories(Root))
            {
                if (d == _activeDir || !Guid.TryParseExact(Path.GetFileName(d), "N", out _)) continue;
                if (TryDelete(d)) n++;
            }
        }
        catch { }
        return n;
    }

    static bool TryDelete(string d)
    {
        try
        {
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d, false); // the link only
            else Directory.Delete(d, true);
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ automatic install

    /// <summary>Why an automatic install can't run now (null = it may). Probes are injectable for the tests.</summary>
    public string? AutoBlocker(bool requireRelease = true)
    {
        var c = _cfg().Updates;
        if (c is not { AutoInstall: true, Check: true }) return "automatic install is off";
        if (requireRelease && Candidate() == null) return "no newer release";
        if (CannotInstall() is { } why) return why;
        if (requireRelease && Busy) return "an install is already running";
        var idle = _o.Idle?.Invoke() ?? IdleTime();
        if (idle < AutoIdle) return $"PC in use (idle {idle.TotalMinutes:0} of {AutoIdle.TotalMinutes:0} min)";
        if (_o.GameInFront?.Invoke() ?? GameInFront()) return "a fullscreen app or game is in front";
        if (_o.OnBattery?.Invoke() ?? OnBattery()) return "running on battery";
        if (requireRelease && LastAutoAttempt() is { } last && last.Version == Candidate()!.Version.ToString(3) && Now - last.At < AutoRetry)
            return $"already tried {last.Version} at {last.At:u}; next try after {last.At + AutoRetry:u}";
        return null;
    }

    /// <summary>One automatic-install decision: install if nothing blocks it. Returns the blocker, or null when an
    /// install was attempted (its outcome is in <see cref="Status"/>).</summary>
    public async Task<string?> TryAutoInstall()
    {
        if (AutoBlocker() is { } why) return why;
        await InstallAsync(auto: true);
        return null;
    }

    sealed record Attempt(string Version, DateTime At);
    Attempt? _lastAttempt;
    string AttemptFile => Path.Combine(Root, "auto-attempt.json");

    Attempt? LastAutoAttempt()
    {
        if (_lastAttempt != null) return _lastAttempt;
        try
        {
            if (!File.Exists(AttemptFile)) return null;
            CheckTrusted(Root, _o.TestSids);
            using var d = JsonDocument.Parse(File.ReadAllText(AttemptFile));
            return _lastAttempt = new Attempt(d.RootElement.GetProperty("version").GetString()!, d.RootElement.GetProperty("at").GetDateTime().ToUniversalTime());
        }
        catch { return null; }
    }

    /// <summary>Remembered across restarts (in the admin-only folder), so a failing update is tried once a day, never in a loop.</summary>
    void RecordAutoAttempt(string version)
    {
        _lastAttempt = new Attempt(version, Now);
        try { EnsureRoot(); File.WriteAllText(AttemptFile, JsonSerializer.Serialize(new { version, at = _lastAttempt.At })); } catch { }
    }

    /// <summary>Tray app only: clean up old folders shortly after start, and while automatic install is on and a newer
    /// release is known, look every 10 minutes whether the PC is idle. Nothing runs while there's nothing to install.</summary>
    public void StartBackground()
    {
        _ = Task.Run(async () => { try { await Task.Delay(TimeSpan.FromMinutes(2), _cts.Token); CleanupOld(); } catch { } });
        _check.Changed += _ => Kick();
    }

    /// <summary>Settings or the check result changed: start the idle watch if there is something to install.</summary>
    public void Kick()
    {
        if (_cfg().Updates is not { AutoInstall: true, Check: true } || Candidate() == null) return;
        if (Interlocked.Exchange(ref _autoLoop, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested && _cfg().Updates is { AutoInstall: true, Check: true } && Candidate() != null)
                {
                    await Task.Delay(AutoPoll, _cts.Token);
                    var why = await TryAutoInstall();
                    if (why == null || why.StartsWith("already tried")) break; // attempted (or done for today): the next check result restarts the watch
                }
            }
            catch { }
            finally { _autoLoop = 0; }
        });
    }

    // ------------------------------------------------------------------ the real PC

    static bool IsElevated()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
    [StructLayout(LayoutKind.Sequential)] struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    public static TimeSpan IdleTime()
    {
        var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref i)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - i.dwTime));
    }

    /// <summary>The overlay's rule: a fullscreen (exclusive or borderless) window of another app is in front.</summary>
    public static bool GameInFront()
    {
        try
        {
            var f = GameOverlay.Foreground.Probe();
            if (f.Pid == 0) return false;
            string name; try { using var p = System.Diagnostics.Process.GetProcessById(f.Pid); name = p.ProcessName; } catch { name = ""; }
            return GameOverlay.Foreground.IsGame(f, name);
        }
        catch { return true; } // unsure: don't interrupt
    }

    /// <summary>On battery (AC line offline). Desktops report online or unknown.</summary>
    public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    // ------------------------------------------------------------------ launching the setup

    const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    /// <summary>CreateProcess with the exact absolute path (no search), TEMP/TMP inside the protected folder (Inno
    /// unpacks its setup.tmp there, not in the user's TEMP), and out of Glint's job when allowed so the setup survives
    /// Glint being closed by it.</summary>
    static bool Launch(string exe, IReadOnlyList<string> args, string dir)
    {
        if (!Path.IsPathFullyQualified(exe) || args.Any(a => a.Contains('"'))) return false;
        var cmd = new StringBuilder().Append('"').Append(exe).Append('"');
        foreach (var a in args) cmd.Append(' ').Append(a.Contains(' ') ? "\"" + a + "\"" : a);
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        env["TEMP"] = dir; env["TMP"] = dir;
        var block = new StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) block.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        block.Append('\0');
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        foreach (var flags in new[] { CREATE_UNICODE_ENVIRONMENT | CREATE_BREAKAWAY_FROM_JOB, CREATE_UNICODE_ENVIRONMENT })
        {
            var c = new StringBuilder(cmd.ToString());
            if (CreateProcessW(exe, c, IntPtr.Zero, IntPtr.Zero, false, flags, block.ToString(), dir, ref si, out var pi))
            {
                CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                return true;
            }
            if (Marshal.GetLastWin32Error() != 5) break; // only retry when breaking away from the job isn't allowed
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, string env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);

    public void Dispose() { _cts.Cancel(); _http.Dispose(); }
}

/// <summary>A verification or download failure with a message for the user.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>Snappy block-format decoder (GitHub stores attestation bundles snappy-compressed). Bounds-checked: bad input
/// throws <see cref="InvalidDataException"/>, never reads or writes outside the buffers.</summary>
public static class Snappy
{
    public static byte[] Decode(ReadOnlySpan<byte> src, int maxLen)
    {
        int i = 0; ulong len = 0;
        for (int shift = 0; ; shift += 7)
        {
            if (i >= src.Length || shift > 28) throw new InvalidDataException("bad snappy length");
            byte b = src[i++];
            len |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80) break;
        }
        if (len > (ulong)maxLen) throw new InvalidDataException("snappy data too large");
        var dst = new byte[(int)len]; int o = 0;
        while (i < src.Length)
        {
            byte tag = src[i++];
            int kind = tag & 3, n, off;
            if (kind == 0)
            {
                n = tag >> 2;
                if (n >= 60)
                {
                    int nb = n - 59; if (i + nb > src.Length) throw new InvalidDataException("bad snappy literal");
                    n = 0; for (int k = 0; k < nb; k++) n |= src[i + k] << (8 * k);
                    i += nb;
                    if (n < 0) throw new InvalidDataException("bad snappy literal");
                }
                n += 1;
                if (n > src.Length - i || n > dst.Length - o) throw new InvalidDataException("bad snappy literal");
                src.Slice(i, n).CopyTo(dst.AsSpan(o)); i += n; o += n;
                continue;
            }
            if (kind == 1) { if (i + 1 > src.Length) throw new InvalidDataException("bad snappy copy"); n = ((tag >> 2) & 7) + 4; off = ((tag >> 5) << 8) | src[i]; i += 1; }
            else if (kind == 2) { if (i + 2 > src.Length) throw new InvalidDataException("bad snappy copy"); n = (tag >> 2) + 1; off = src[i] | src[i + 1] << 8; i += 2; }
            else { if (i + 4 > src.Length) throw new InvalidDataException("bad snappy copy"); n = (tag >> 2) + 1; off = src[i] | src[i + 1] << 8 | src[i + 2] << 16 | src[i + 3] << 24; i += 4; }
            if (off <= 0 || off > o || n > dst.Length - o) throw new InvalidDataException("bad snappy copy");
            for (int k = 0; k < n; k++, o++) dst[o] = dst[o - off]; // may overlap
        }
        if (o != dst.Length) throw new InvalidDataException("snappy data truncated");
        return dst;
    }
}
