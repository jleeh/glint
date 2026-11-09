using System.Net;
using System.Text.Json;

namespace Glint.Core;

/// <summary>What the last update check found. Available = a newer release than this build exists.</summary>
public sealed record UpdateInfo(string Current, string? Latest, string? Url, bool Available, DateTime? CheckedAt, string? Note);

/// <summary>The release the last check found (internal: the updater builds its download URLs from Tag).</summary>
public sealed record ReleaseInfo(string Tag, Version Version, bool Prerelease, bool Draft);

/// <summary>
/// Optional update check: one request to GitHub's releases/latest API at startup (after a short delay), then whenever the
/// last answer is older than <see cref="Interval"/> (wall clock, so sleep/hibernate can't stretch it), when the dashboard
/// opens with an answer older than <see cref="StaleOnOpen"/>, and when the user clicks "Check now";
/// only while <see cref="UpdateOptions.Check"/> is on. No other network, no telemetry. Downloading and installing is
/// <see cref="Updater"/>'s job (only when the user asks, or opted into automatic installs).
/// Offline, rate-limited, private or missing repo (404): silently "unknown". Runs on the thread pool (no thread of its own).
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>The one place the repository lives (owner/name). Every URL Glint opens, checks or downloads from derives from it.</summary>
    public const string Repo = "jleeh/glint";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ApiRepoUrl = "https://api.github.com/repos/" + Repo;
    public const string ApiUrl = ApiRepoUrl + "/releases/latest";
    public static string ReleasesUrl => RepoUrl + "/releases/latest";
    public static string CurrentVersion => typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    readonly Func<GlintConfig> _cfg;
    readonly HttpMessageInvoker _http;
    readonly string _current;
    readonly CancellationTokenSource _cts = new();
    int _running;
    volatile bool _started;
    public UpdateInfo Info { get; private set; }
    public string Current => _current;
    /// <summary>The release behind <see cref="Info"/> (null when the last check found none).</summary>
    public ReleaseInfo? Release { get; private set; }
    public event Action<UpdateInfo>? Changed;
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    /// <summary>How often the background loop looks at the clock (cheap; it only sends a request once the answer is stale).</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);
    /// <summary>Opening the dashboard re-checks when the last answer is older than this.</summary>
    public static readonly TimeSpan StaleOnOpen = TimeSpan.FromHours(1);
    /// <summary>"Check now" never sends more than one request per this window (GitHub allows 60 unauthenticated/h).</summary>
    public static readonly TimeSpan MinManualGap = TimeSpan.FromSeconds(30);
    public const int MaxResponseBytes = 1 << 20;

    /// <param name="handler">Tests pass a fake handler (no network at all).</param>
    public UpdateChecker(Func<GlintConfig> cfg, HttpMessageHandler? handler = null, string? current = null)
    {
        _cfg = cfg;
        _current = current ?? CurrentVersion;
        _http = new HttpMessageInvoker(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) });
        Info = new UpdateInfo(_current, null, null, false, null, null);
    }

    /// <summary>Background schedule: first check after <paramref name="firstDelay"/>, then every <see cref="Interval"/>
    /// by the wall clock (a single long Task.Delay doesn't count time the PC spent asleep, so a 24 h wait could take days).</summary>
    public void Start(TimeSpan firstDelay)
    {
        _started = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(firstDelay, _cts.Token);
                while (!_cts.IsCancellationRequested)
                {
                    await CheckIfStale(Interval);
                    await Task.Delay(Tick, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>True when there is no answer yet or the last one is older than <paramref name="maxAge"/>.</summary>
    public bool IsStale(TimeSpan maxAge, DateTime? now = null) => Info.CheckedAt is not { } at || (now ?? DateTime.UtcNow) - at >= maxAge;

    /// <summary>Check when enabled and the last answer is older than <paramref name="maxAge"/>; otherwise return it as is.</summary>
    public Task<UpdateInfo> CheckIfStale(TimeSpan maxAge) =>
        _cfg().Updates?.Check == true && IsStale(maxAge) ? CheckNow() : Task.FromResult(Info);

    /// <summary>The user asked ("Check now" / update.check): a fresh answer unless one arrived in the last few seconds.
    /// Works even with the background check switched off: the user explicitly asked for this one request.</summary>
    public Task<UpdateInfo> CheckRequested() => IsStale(MinManualGap) ? CheckNow() : Task.FromResult(Info);

    /// <summary>The setting was just switched on: check now unless a check ran recently.</summary>
    public void OnConfig(GlintConfig c)
    {
        if (_started && c.Updates?.Check == true && IsStale(Interval)) _ = CheckNow();
    }

    /// <summary>One request. Never throws; failures leave Latest unknown with a short note.</summary>
    public async Task<UpdateInfo> CheckNow()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return Info;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            req.Headers.UserAgent.ParseAdd($"Glint/{_current}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.SendAsync(req, timeout.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound) return Set(new UpdateInfo(_current, null, null, false, DateTime.UtcNow, "no published release found"));
            if (!resp.IsSuccessStatusCode) return Set(new UpdateInfo(_current, null, null, false, DateTime.UtcNow, $"GitHub answered {(int)resp.StatusCode}"));
            // a release description is a few KB: never buffer an unbounded answer
            if (resp.Content.Headers.ContentLength > MaxResponseBytes) return Set(new UpdateInfo(_current, null, null, false, DateTime.UtcNow, "unexpected answer from GitHub"));
            var buf = new MemoryStream();
            await using (var st = await resp.Content.ReadAsStreamAsync(timeout.Token))
            {
                var chunk = new byte[16384]; int n;
                while ((n = await st.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    buf.Write(chunk, 0, n);
                    if (buf.Length > MaxResponseBytes) return Set(new UpdateInfo(_current, null, null, false, DateTime.UtcNow, "unexpected answer from GitHub"));
                }
            }
            using var doc = JsonDocument.Parse(buf.ToArray());
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var url = doc.RootElement.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            // only ever open a page of our own repository
            if (url == null || !url.StartsWith(RepoUrl + "/", StringComparison.OrdinalIgnoreCase)) url = ReleasesUrl;
            if (tag == null || Parse(tag) is not { } latest) return Set(new UpdateInfo(_current, null, null, false, DateTime.UtcNow, "release has no version tag"));
            bool pre = doc.RootElement.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True
                       || tag.IndexOfAny(new[] { '-', '+' }) >= 0;
            bool draft = doc.RootElement.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True;
            // releases/latest never returns drafts or pre-releases; if it ever did, they are not offered
            bool newer = !pre && !draft && Parse(_current) is { } cur && latest > cur;
            return Set(new UpdateInfo(_current, latest.ToString(3), url, newer, DateTime.UtcNow, pre || draft ? "latest release is a pre-release" : null), new ReleaseInfo(tag, latest, pre, draft));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or IOException)
        {
            return Set(new UpdateInfo(_current, null, null, false, DateTime.UtcNow, "offline or GitHub unreachable"));
        }
        finally { _running = 0; }
    }

    UpdateInfo Set(UpdateInfo i, ReleaseInfo? r = null)
    {
        Release = r;
        Info = i;
        try { Changed?.Invoke(i); } catch { }
        return i;
    }

    /// <summary>"v1.2.3", "1.2", "v0.3.0-beta.1" → Version (pre-release suffix ignored); null if not a version.</summary>
    public static Version? Parse(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public void Dispose() { _cts.Cancel(); _http.Dispose(); }
}
