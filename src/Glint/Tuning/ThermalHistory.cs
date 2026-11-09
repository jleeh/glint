using System.Text.Json;

namespace Glint.Tuning;

/// <summary>One 30-second sample: temperatures and each fan rule's average duty.</summary>
public sealed record ThermalSample(DateTime T, Dictionary<string, double> Temp, Dictionary<string, double> Duty);

/// <summary>
/// Temperatures and fan duty over real use (the last 24 h, one sample per 30 s, kept across restarts) so the AI tunes fan curves
/// for what the PC actually reaches under sustained load (games, renders), not only for short stress tests.
/// </summary>
public sealed class ThermalHistory
{
    public static readonly string[] Keys = { "cpu", "gpu", "gpu.hotspot", "liquid" };
    readonly object _gate = new();
    readonly List<ThermalSample> _s = new();
    readonly string? _path;
    DateTime _lastSave = DateTime.MinValue;
    public TimeSpan Keep { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan Every { get; set; } = TimeSpan.FromSeconds(30);

    public ThermalHistory(string? path)
    {
        _path = path;
        try { if (path != null && File.Exists(path)) _s.AddRange(JsonSerializer.Deserialize<List<ThermalSample>>(File.ReadAllText(path)) ?? new()); } catch { }
    }

    public int Count { get { lock (_gate) return _s.Count; } }

    /// <summary>Add a sample if <see cref="Every"/> has passed since the last one.</summary>
    public void Record(DateTime now, IReadOnlyDictionary<string, double> sensors, Dictionary<string, double> ruleDuty)
    {
        lock (_gate)
        {
            if (_s.Count > 0 && now - _s[^1].T < Every) return;
            var t = new Dictionary<string, double>();
            foreach (var k in Keys) if (sensors.TryGetValue(k, out var v) && v is > 0 and < 150) t[k] = Math.Round(v, 1);
            if (t.Count == 0) return;
            _s.Add(new ThermalSample(now, t, ruleDuty.ToDictionary(x => x.Key, x => Math.Round(x.Value))));
            _s.RemoveAll(x => now - x.T > Keep);
            if (_path != null && now - _lastSave > TimeSpan.FromMinutes(5))
            {
                _lastSave = now;
                try { File.WriteAllText(_path, JsonSerializer.Serialize(_s)); } catch { }
            }
        }
    }

    static object Stats(IEnumerable<double> v)
    {
        var l = v.OrderBy(x => x).ToList();
        if (l.Count == 0) return new { };
        double P(double q) => l[Math.Min(l.Count - 1, (int)Math.Round(q * (l.Count - 1)))];
        return new { p50 = P(0.5), p95 = P(0.95), max = l[^1] };
    }

    /// <summary>
    /// Summary for the AI: per-sensor and per-rule typical / p95 / max, and the sustained-load periods (5 min or more with the GPU at
    /// 60 °C+ or the coolant 5 °C+ above its typical level) with their peaks and the duty the fans ran at.
    /// </summary>
    public object Summary(DateTime now, double hours)
    {
        List<ThermalSample> s;
        lock (_gate) s = _s.Where(x => now - x.T <= TimeSpan.FromHours(Math.Clamp(hours, 0.5, 24))).ToList();
        if (s.Count == 0) return new { samples = 0, note = "no history yet: Glint records a sample every 30 s while it runs" };
        var temps = Keys.Where(k => s.Any(x => x.Temp.ContainsKey(k))).ToDictionary(k => k, k => Stats(s.Where(x => x.Temp.ContainsKey(k)).Select(x => x.Temp[k])));
        var rules = s.SelectMany(x => x.Duty.Keys).Distinct().ToDictionary(r => r, r => Stats(s.Where(x => x.Duty.ContainsKey(r)).Select(x => x.Duty[r])));
        double liqTyp = s.Where(x => x.Temp.ContainsKey("liquid")).Select(x => x.Temp["liquid"]).DefaultIfEmpty(0).OrderBy(x => x).ElementAt(s.Count(x => x.Temp.ContainsKey("liquid")) / 2);
        bool Busy(ThermalSample x) => x.Temp.GetValueOrDefault("gpu") >= 60 || (liqTyp > 0 && x.Temp.GetValueOrDefault("liquid") >= liqTyp + 5);
        var periods = new List<object>();
        int i = 0;
        while (i < s.Count)
        {
            if (!Busy(s[i])) { i++; continue; }
            int j = i;
            while (j + 1 < s.Count && Busy(s[j + 1]) && s[j + 1].T - s[j].T < TimeSpan.FromMinutes(2)) j++;
            var seg = s.GetRange(i, j - i + 1);
            double mins = (seg[^1].T - seg[0].T).TotalMinutes;
            if (mins >= 5)
                periods.Add(new
                {
                    start = seg[0].T, minutes = Math.Round(mins),
                    peak = Keys.Where(k => seg.Any(x => x.Temp.ContainsKey(k))).ToDictionary(k => k, k => seg.Where(x => x.Temp.ContainsKey(k)).Max(x => x.Temp[k])),
                    end = Keys.Where(k => seg[^1].Temp.ContainsKey(k)).ToDictionary(k => k, k => seg[^1].Temp[k]),
                    avgDuty = seg.SelectMany(x => x.Duty.Keys).Distinct().ToDictionary(r => r, r => Math.Round(seg.Where(x => x.Duty.ContainsKey(r)).Average(x => x.Duty[r]))),
                    maxDuty = seg.SelectMany(x => x.Duty.Keys).Distinct().ToDictionary(r => r, r => seg.Where(x => x.Duty.ContainsKey(r)).Max(x => x.Duty[r])),
                });
            i = j + 1;
        }
        return new
        {
            samples = s.Count, from = s[0].T, to = s[^1].T, temps, ruleDuty = rules,
            sustainedLoad = periods.TakeLast(12).ToList(),
            read = "temps / ruleDuty: typical (p50), p95 and max over real use. sustainedLoad: periods of 5+ min with the GPU at 60 C+ or the coolant 5 C+ above typical (games, renders): the peaks there are what the curves must handle, and avgDuty is how hard each rule ran. Coolant lags by minutes: judge it from these, not from a short test.",
        };
    }
}
