using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Glint.Core;

/// <summary>Per-component CPU accounting: each worker thread adds its own thread CPU time under a name.</summary>
public static class CpuMeter
{
    [DllImport("kernel32")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32")] static extern bool GetThreadTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    static readonly ConcurrentDictionary<string, double> _total = new();
    static readonly ConcurrentDictionary<string, double> _last = new();

    /// <summary>CPU milliseconds used by the calling thread so far.</summary>
    public static double ThreadMs() { GetThreadTimes(GetCurrentThread(), out _, out _, out var k, out var u); return (k + u) / 10_000.0; }

    public static void Add(string part, double ms) => _total.AddOrUpdate(part, ms, (_, v) => v + ms);

    /// <summary>% of one core per part since the previous call.</summary>
    public static Dictionary<string, double> Snapshot(double windowSec)
    {
        var d = new Dictionary<string, double>();
        foreach (var (k, v) in _total)
        {
            double prev = _last.GetValueOrDefault(k);
            d[k] = Math.Round((v - prev) / (windowSec * 1000) * 100, 2);
            _last[k] = v;
        }
        return d;
    }
}
