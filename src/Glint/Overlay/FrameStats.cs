namespace Glint.GameOverlay;

/// <summary>
/// Rolling frame statistics from present timestamps (any monotonic tick clock, e.g. QPC).
/// FPS and frame time are averaged over the last second of presents; the 1% low is the frame rate at the
/// 99th-percentile frame time over the last ~10 s (the PresentMon / CapFrameX "1% low" percentile definition).
/// Not thread-safe on its own: <see cref="PresentTracker"/> locks around it.
/// </summary>
public sealed class FrameStats
{
    readonly long[] _t;
    readonly double _freq;
    int _head, _count;            // _head = next write slot
    double[] _scratch = Array.Empty<double>();

    public FrameStats(double ticksPerSecond, int capacity = 8192)
    {
        _freq = ticksPerSecond;
        _t = new long[capacity];
    }

    public int Count => _count;
    public long Last => _count == 0 ? long.MinValue : _t[(_head - 1 + _t.Length) % _t.Length];

    public void Add(long ts)
    {
        if (_count > 0 && ts <= Last) return;  // duplicate / out of order (buffers from several CPUs)
        _t[_head] = ts;
        _head = (_head + 1) % _t.Length;
        if (_count < _t.Length) _count++;
    }

    public void Clear() { _head = 0; _count = 0; }

    long At(int back) => _t[(_head - 1 - back + 2 * _t.Length) % _t.Length];   // 0 = newest

    /// <summary>Presents with a timestamp at or after <paramref name="since"/>.</summary>
    public int CountSince(long since)
    {
        int n = 0;
        while (n < _count && At(n) >= since) n++;
        return n;
    }

    public readonly record struct Result(double Fps, double FrameMs, double Low1, int Samples);

    /// <param name="now">Current time on the same clock; presents older than <paramref name="staleSec"/> count as "no data".</param>
    public Result Compute(long now, double avgSec = 1, double lowSec = 10, double staleSec = 2.5)
    {
        if (_count < 2) return default;
        long last = Last;
        if ((now - last) / _freq > staleSec) return default;
        // average over the last second of presents (anchored at the newest one: ETW delivers in ~1 s batches)
        long from = last - (long)(avgSec * _freq);
        int n = 0;
        while (n + 1 < _count && At(n + 1) >= from) n++;
        if (n == 0) { n = 1; }                       // one interval longer than the window (very low fps)
        double span = (last - At(n)) / _freq;
        double fps = span > 0 ? n / span : 0;
        // 1% low: 99th percentile of frame times in the low window
        long lowFrom = last - (long)(lowSec * _freq);
        int m = 0;
        while (m + 1 < _count && At(m + 1) >= lowFrom) m++;
        if (m == 0) m = 1;
        if (_scratch.Length < m) _scratch = new double[Math.Max(m, 256)];
        for (int i = 0; i < m; i++) _scratch[i] = (At(i) - At(i + 1)) / _freq;
        Array.Sort(_scratch, 0, m);
        double p99 = _scratch[Math.Clamp((int)Math.Ceiling(0.99 * m) - 1, 0, m - 1)];
        double low = p99 > 0 ? 1 / p99 : 0;
        return new Result(fps, fps > 0 ? 1000 / fps : 0, low, m);
    }
}

/// <summary>Where a present event came from. DXGI Present::Start is exactly one per Present() call; the DxgKrnl
/// events also cover Vulkan / OpenGL / D3D9 (which never touch DXGI).</summary>
public static class PresentStream
{
    public const int Dxgi = 1;
    /// <summary>DxgKrnl event ids used: 166 Blit, 168 Flip, 171 PresentHistory_Start, 184 Present_Info, 215 PresentHistoryDetailed_Start.</summary>
    public static readonly ushort[] DxgKrnlIds = { 166, 168, 171, 184, 215 };
    public static int DxgKrnl(int eventId) => 1000 + eventId;
    public static string Name(int s) => s == 0 ? "none" : s == Dxgi ? "dxgi" : $"dxgkrnl:{s - 1000}";
}

/// <summary>
/// Present events → frame stats for the foreground process. Each event kind is kept as its own stream and only one
/// is reported: DXGI if the process presents through DXGI, otherwise the busiest DxgKrnl stream (picking one stream
/// means a present that shows up as several kernel events is never counted twice).
/// History is kept for the last few foreground processes, so a moment of focus elsewhere (a notification, a launcher
/// or chat pop-up, alt-tab) doesn't wipe the game's numbers: if the window in front isn't presenting, the most
/// recent process that is (within the stale window) is shown instead.
/// Called from the ETW thread (<see cref="OnPresent"/>) and the overlay timer (<see cref="Read"/>).
/// </summary>
public sealed class PresentTracker
{
    const int Keep = 4;
    readonly object _lock = new();
    readonly Dictionary<int, Dictionary<int, FrameStats>> _byPid = new();
    readonly List<int> _recent = new();            // most recent foreground first
    volatile int[] _watch = Array.Empty<int>();     // read lock-free on the ETW thread
    readonly double _freq;
    volatile int _target;
    public long Events;           // presents seen for watched processes (diagnostics)
    /// <summary>The process whose numbers the last <see cref="Read"/> returned (0 = none).</summary>
    public int ShownPid { get; private set; }

    public PresentTracker(double ticksPerSecond) => _freq = ticksPerSecond;

    /// <summary>The foreground process. Older targets keep their history (up to a few processes).</summary>
    public int TargetPid
    {
        get => _target;
        set
        {
            if (value == _target || value == 0) return;
            lock (_lock)
            {
                _target = value;
                _recent.Remove(value); _recent.Insert(0, value);
                while (_recent.Count > Keep) { _byPid.Remove(_recent[^1]); _recent.RemoveAt(_recent.Count - 1); }
                _watch = _recent.ToArray();
            }
        }
    }

    public void OnPresent(int pid, long ts, int stream)
    {
        if (pid == 0 || Array.IndexOf(_watch, pid) < 0) return;
        lock (_lock)
        {
            if (!_byPid.TryGetValue(pid, out var streams)) _byPid[pid] = streams = new();
            if (!streams.TryGetValue(stream, out var s)) streams[stream] = s = new FrameStats(_freq);
            s.Add(ts);
            Events++;
        }
    }

    public (FrameStats.Result stats, int stream) Read(long now)
    {
        lock (_lock)
        {
            foreach (var pid in _recent)   // foreground first, then the processes that were in front before it
            {
                var r = ReadPid(pid, now);
                if (r.stream != 0 && r.stats.Fps > 0) { ShownPid = pid; return r; }
            }
            ShownPid = 0;
            return (default, 0);
        }
    }

    (FrameStats.Result stats, int stream) ReadPid(int pid, long now)
    {
        if (!_byPid.TryGetValue(pid, out var streams)) return (default, 0);
        long recent = now - (long)(2.5 * _freq);
        if (streams.TryGetValue(PresentStream.Dxgi, out var d) && d.Count > 1 && d.Last >= recent)
            return (d.Compute(now), PresentStream.Dxgi);
        int best = 0, bestN = 1; FrameStats? bs = null;
        foreach (var (k, s) in streams)
        {
            if (k == PresentStream.Dxgi) continue;
            int n = s.CountSince(recent);
            if (n > bestN) { best = k; bestN = n; bs = s; }
        }
        return bs == null ? (default, 0) : (bs.Compute(now), best);
    }
}
