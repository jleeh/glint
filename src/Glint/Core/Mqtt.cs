using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Timer = System.Threading.Timer;

namespace Glint.Core;

/// <summary>Connection settings for <see cref="MqttClient"/>.</summary>
public sealed record MqttOptions(string Host, int Port, bool Tls, string ClientId, string? Username, string? Password,
    string WillTopic, string WillPayload = "offline", int KeepAliveSec = 60);

/// <summary>
/// Minimal MQTT 3.1.1 client: CONNECT (credentials, keep-alive, retained LWT), PUBLISH / SUBSCRIBE at QoS 0,
/// PINGREQ, optional TLS, and auto-reconnect with exponential backoff (capped at 60 s).
/// One background thread owns the connection and sits blocked in a socket read (no polling);
/// a timer that fires every keepAlive/2 seconds sends pings and drops a dead link.
/// </summary>
public sealed class MqttClient : IDisposable
{
    readonly MqttOptions _o;
    readonly Thread _thread;
    readonly ManualResetEventSlim _stopEv = new(false);
    readonly object _writeLock = new();
    readonly Timer _keepAlive;
    volatile bool _stop;
    TcpClient? _tcp;
    Stream? _stream;
    byte[] _wbuf = new byte[512];
    byte[] _rbuf = new byte[512];
    long _lastSend, _lastRecv; // Environment.TickCount64
    readonly List<string> _subs = new();

    public bool Connected { get; private set; }
    public string LastError { get; private set; } = "";
    public int Reconnects { get; private set; }
    /// <summary>Raised on the connection thread after CONNACK (subscriptions are already re-sent).</summary>
    public event Action? OnConnected;
    /// <summary>Raised on the connection thread for every incoming PUBLISH (topic, UTF-8 payload).</summary>
    public event Action<string, string>? OnMessage;

    public MqttClient(MqttOptions o)
    {
        _o = o;
        int period = Math.Max(1, o.KeepAliveSec / 2) * 1000;
        _keepAlive = new Timer(_ => KeepAlive(), null, period, period);
        _thread = new Thread(Run) { IsBackground = true, Name = "Glint MQTT" };
        _thread.Start();
    }

    /// <summary>Topics to subscribe on every (re)connect. Call before or after connecting.</summary>
    public void Subscribe(string topic)
    {
        lock (_subs) { if (_subs.Contains(topic)) return; _subs.Add(topic); }
        if (Connected) SendSubscribe(new[] { topic });
    }

    void Run()
    {
        int backoff = 1;
        while (!_stop)
        {
            try
            {
                Connect();
                backoff = 1;
                ReadLoop();
            }
            catch (Exception e) when (!_stop)
            {
                LastError = e is AggregateException a ? a.InnerException?.Message ?? e.Message : e.Message;
            }
            catch { }
            bool was = Connected;
            Connected = false;
            CloseSocket();
            if (_stop) break;
            if (was) Reconnects++;
            _stopEv.Wait(TimeSpan.FromSeconds(backoff));
            backoff = Math.Min(backoff * 2, 60);
        }
    }

    void Connect()
    {
        var tcp = new TcpClient { NoDelay = true, SendTimeout = 10000 };
        _tcp = tcp;
        if (!tcp.ConnectAsync(_o.Host, _o.Port).Wait(10000)) throw new TimeoutException($"connect to {_o.Host}:{_o.Port} timed out");
        Stream s = tcp.GetStream();
        if (_o.Tls)
        {
            var ssl = new SslStream(s, false);
            ssl.AuthenticateAsClient(_o.Host);
            s = ssl;
        }
        _stream = s;

        // CONNECT
        var body = new List<byte>(64);
        void Str(string v) { var b = Encoding.UTF8.GetBytes(v); body.Add((byte)(b.Length >> 8)); body.Add((byte)b.Length); body.AddRange(b); }
        Str("MQTT");
        body.Add(4); // 3.1.1
        byte flags = 0x02 | 0x04 | 0x20; // clean session, will flag, will retain (QoS 0)
        if (!string.IsNullOrEmpty(_o.Username)) flags |= 0x80;
        if (!string.IsNullOrEmpty(_o.Username) && _o.Password != null) flags |= 0x40;
        body.Add(flags);
        body.Add((byte)(_o.KeepAliveSec >> 8)); body.Add((byte)_o.KeepAliveSec);
        Str(_o.ClientId);
        Str(_o.WillTopic);
        Str(_o.WillPayload);
        if ((flags & 0x80) != 0) Str(_o.Username!);
        if ((flags & 0x40) != 0) Str(_o.Password!);
        WritePacket(0x10, body.ToArray());

        // CONNACK
        var (type, len) = ReadHeader();
        ReadBody(len);
        if (type >> 4 != 2 || len < 2) throw new IOException("broker did not answer CONNECT");
        int rc = _rbuf[1];
        if (rc != 0)
            throw new IOException(rc switch
            {
                1 => "broker refused: protocol version", 2 => "broker refused: client id", 3 => "broker unavailable",
                4 => "broker refused: bad username or password", 5 => "broker refused: not authorised", _ => $"broker refused ({rc})",
            });
        _lastRecv = Environment.TickCount64;
        Connected = true;
        LastError = "";
        string[] subs;
        lock (_subs) subs = _subs.ToArray();
        if (subs.Length > 0) SendSubscribe(subs);
        OnConnected?.Invoke();
    }

    void ReadLoop()
    {
        while (!_stop)
        {
            var (type, len) = ReadHeader();
            ReadBody(len);
            _lastRecv = Environment.TickCount64;
            if (type >> 4 == 3) // PUBLISH
            {
                int tl = (_rbuf[0] << 8) | _rbuf[1];
                int qos = (type >> 1) & 3;
                int p = 2 + tl + (qos > 0 ? 2 : 0);
                if (p > len) continue;
                var topic = Encoding.UTF8.GetString(_rbuf, 2, tl);
                var payload = Encoding.UTF8.GetString(_rbuf, p, len - p);
                if (qos == 1) WritePacket(0x40, new[] { _rbuf[2 + tl], _rbuf[3 + tl] }); // PUBACK (we only subscribe at QoS 0, but be polite)
                try { OnMessage?.Invoke(topic, payload); } catch { }
            }
            // SUBACK, PINGRESP: nothing to do
        }
    }

    (int type, int len) ReadHeader()
    {
        int type = ReadByte();
        int len = 0, mul = 1;
        for (int i = 0; i < 4; i++)
        {
            int b = ReadByte();
            len += (b & 0x7f) * mul;
            if ((b & 0x80) == 0) return (type, len);
            mul <<= 7;
        }
        throw new IOException("malformed packet length");
    }

    int ReadByte()
    {
        int b = _stream!.ReadByte();
        if (b < 0) throw new IOException("connection closed by broker");
        return b;
    }

    void ReadBody(int len)
    {
        if (len > 1 << 20) throw new IOException("packet too large");
        if (_rbuf.Length < len) _rbuf = new byte[Math.Max(len, _rbuf.Length * 2)];
        _stream!.ReadExactly(_rbuf, 0, len);
    }

    void KeepAlive()
    {
        if (!Connected) return;
        long now = Environment.TickCount64;
        if (now - _lastRecv > _o.KeepAliveSec * 1500L) { LastError = "keep-alive timeout"; CloseSocket(); return; } // unblocks the read → reconnect
        if (now - _lastSend >= _o.KeepAliveSec * 500L) { try { WritePacket(0xC0, ReadOnlySpan<byte>.Empty); } catch { CloseSocket(); } }
    }

    /// <summary>QoS 0 publish. Returns false (and drops the message) when not connected.</summary>
    public bool Publish(string topic, string payload, bool retain = false)
    {
        if (!Connected) return false;
        try
        {
            lock (_writeLock)
            {
                int tl = Encoding.UTF8.GetByteCount(topic), pl = Encoding.UTF8.GetByteCount(payload);
                int rem = 2 + tl + pl;
                EnsureW(rem + 5);
                int i = 0;
                _wbuf[i++] = (byte)(0x30 | (retain ? 1 : 0));
                i = PutLen(i, rem);
                _wbuf[i++] = (byte)(tl >> 8); _wbuf[i++] = (byte)tl;
                i += Encoding.UTF8.GetBytes(topic, 0, topic.Length, _wbuf, i);
                i += Encoding.UTF8.GetBytes(payload, 0, payload.Length, _wbuf, i);
                _stream!.Write(_wbuf, 0, i);
                _stream.Flush();
                _lastSend = Environment.TickCount64;
            }
            return true;
        }
        catch (Exception e) { LastError = e.Message; CloseSocket(); return false; }
    }

    ushort _pid;
    void SendSubscribe(IEnumerable<string> topics)
    {
        var body = new List<byte>();
        ushort id = ++_pid == 0 ? ++_pid : _pid;
        body.Add((byte)(id >> 8)); body.Add((byte)id);
        foreach (var t in topics)
        {
            var b = Encoding.UTF8.GetBytes(t);
            body.Add((byte)(b.Length >> 8)); body.Add((byte)b.Length); body.AddRange(b); body.Add(0);
        }
        try { WritePacket(0x82, body.ToArray()); } catch (Exception e) { LastError = e.Message; CloseSocket(); }
    }

    void WritePacket(byte header, ReadOnlySpan<byte> body)
    {
        lock (_writeLock)
        {
            EnsureW(body.Length + 5);
            int i = 0;
            _wbuf[i++] = header;
            i = PutLen(i, body.Length);
            body.CopyTo(_wbuf.AsSpan(i));
            i += body.Length;
            var s = _stream ?? throw new IOException("not connected");
            s.Write(_wbuf, 0, i);
            s.Flush();
            _lastSend = Environment.TickCount64;
        }
    }

    void EnsureW(int n) { if (_wbuf.Length < n) _wbuf = new byte[Math.Max(n, _wbuf.Length * 2)]; }

    int PutLen(int i, int len)
    {
        do
        {
            byte b = (byte)(len % 128);
            len /= 128;
            if (len > 0) b |= 0x80;
            _wbuf[i++] = b;
        } while (len > 0);
        return i;
    }

    void CloseSocket()
    {
        Connected = false;
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }

    /// <summary>Clean shutdown: optionally publish a final message (e.g. retained "offline"), then DISCONNECT.</summary>
    public void Close(string? lastTopic = null, string? lastPayload = null, bool retain = true)
    {
        if (_stop) return;
        if (Connected)
        {
            if (lastTopic != null) Publish(lastTopic, lastPayload ?? "", retain);
            try { WritePacket(0xE0, ReadOnlySpan<byte>.Empty); } catch { }
        }
        _stop = true;
        _stopEv.Set();
        _keepAlive.Dispose();
        CloseSocket();
        _thread.Join(2000);
    }

    public void Dispose() => Close();
}
