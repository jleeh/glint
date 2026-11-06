using System.Net;
using System.Text.Json;

namespace Glint.Core;

/// <summary>What the last update check found. Available = a newer release than this build exists.</summary>
public sealed record UpdateInfo(string Current, string? Latest, string? Url, bool Available, DateTime? CheckedAt, string? Note);

/// <summary>
/// Optional update check: one request to GitHub's releases/latest API at startup (after a short delay) and every 24 h,
/// only while <see cref="UpdateOptions.Check"/> is on. No other network, no telemetry, never downloads anything.
/// Offline, rate-limited, private or missing repo (404): silently "unknown". Runs on the thread pool (no thread of its own).
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>The one place the repository lives.</summary>
    public const string RepoUrl = "https://github.com/jleeh/glint";
    public const string ApiUrl = "https://api.github.com/repos/jleeh/glint/releases/latest";
    public static string ReleasesUrl => RepoUrl + "/releases/latest";
    public static string CurrentVersion => typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    readonly Func<GlintConfig> _cfg;
    readonly HttpMessageInvoker _http;
    readonly string _current;
    readonly CancellationTokenSource _cts = new();
    int _running;
    volatile bool _started;
    public UpdateInfo Info { get; private set; }
    public event Action<UpdateInfo>? Changed;
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public const int MaxResponseBytes = 1 << 20;

    /// <param name="handler">Tests pass a fake handler (no network at all).</param>
    public UpdateChecker(Func<GlintConfig> cfg, HttpMessageHandler? handler = null, string? current = null)
    {
        _cfg = cfg;
        _current = current ?? CurrentVersion;
        _http = new HttpMessageInvoker(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) });
        Info = new UpdateInfo(_current, null, null, false, null, null);
    }

    /// <summary>Background schedule: first check after <paramref name="firstDelay"/>, then every 24 h while enabled.</summary>
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
                    if (_cfg().Updates?.Check == true) await CheckNow();
                    await Task.Delay(Interval, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>The setting was just switched on: check now unless a check ran recently.</summary>
    public void OnConfig(GlintConfig c)
    {
        if (_started && c.Updates?.Check == true && (Info.CheckedAt == null || DateTime.UtcNow - Info.CheckedAt > Interval)) _ = CheckNow();
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
            bool newer = Parse(_current) is { } cur && latest > cur;
            return Set(new UpdateInfo(_current, latest.ToString(3), url, newer, DateTime.UtcNow, null));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or IOException)
        {
            return Set(new UpdateInfo(_current, null, null, false, DateTime.UtcNow, "offline or GitHub unreachable"));
        }
        finally { _running = 0; }
    }

    UpdateInfo Set(UpdateInfo i)
    {
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
