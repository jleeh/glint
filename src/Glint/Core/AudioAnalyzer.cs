using NAudio.Wave;

namespace Glint.Core;

/// <summary>
/// System audio (WASAPI loopback) → N log-spaced bands with automatic gain. Only runs while an
/// audio-reactive effect is active.
/// </summary>
public sealed class AudioAnalyzer : IDisposable
{
    const int N = 2048;
    readonly WasapiLoopbackCapture _cap;
    readonly float[] _ring = new float[N];
    int _pos;
    readonly object _lock = new();
    readonly int _channels, _rate;
    float _agc = 0.05f;
    public int Bands { get; }

    public AudioAnalyzer(int bands = 16)
    {
        Bands = bands;
        _cap = new WasapiLoopbackCapture();
        _channels = _cap.WaveFormat.Channels;
        _rate = _cap.WaveFormat.SampleRate;
        _cap.DataAvailable += OnData;
        _cap.StartRecording();
    }

    void OnData(object? s, WaveInEventArgs e)
    {
        // Loopback format is 32-bit float interleaved.
        int frames = e.BytesRecorded / 4 / _channels;
        lock (_lock)
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += BitConverter.ToSingle(e.Buffer, (f * _channels + c) * 4);
                _ring[_pos] = sum / _channels;
                _pos = (_pos + 1) % N;
            }
        }
    }

    readonly double[] _re = new double[N], _im = new double[N];

    public (float[] bands, float level) Analyze()
    {
        lock (_lock)
            for (int i = 0; i < N; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)); // Hann
                _re[i] = _ring[(_pos + i) % N] * w; _im[i] = 0;
            }
        Fft(_re, _im);
        var bands = new float[Bands];
        double fLo = 40, fHi = Math.Min(16000, _rate / 2.0);
        float peak = 0, total = 0;
        for (int b = 0; b < Bands; b++)
        {
            double a = fLo * Math.Pow(fHi / fLo, (double)b / Bands), z = fLo * Math.Pow(fHi / fLo, (double)(b + 1) / Bands);
            int i0 = Math.Max(1, (int)(a * N / _rate)), i1 = Math.Max(i0 + 1, (int)(z * N / _rate));
            double m = 0;
            for (int i = i0; i < i1 && i < N / 2; i++) m = Math.Max(m, Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]));
            bands[b] = (float)m;
            peak = Math.Max(peak, (float)m);
            total += (float)m;
        }
        // AGC: fast attack, slow release, so quiet and loud music both fill the range.
        _agc = peak > _agc ? peak : Math.Max(0.02f, _agc * 0.995f);
        for (int b = 0; b < Bands; b++) bands[b] = Math.Clamp(bands[b] / _agc, 0, 1);
        return (bands, Math.Clamp(total / Bands / _agc, 0, 1));
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }

    public void Dispose() { try { _cap.StopRecording(); } catch { } _cap.Dispose(); }
}
