using System.Runtime.InteropServices;

namespace Glint.Devices;

/// <summary>
/// SMBus access on AMD (PIIX4-compatible FCH) through the signed PawnIO driver + SmbusPIIX4 module.
/// Every transaction holds the cross-vendor "Access_SMBUS.HTP.Method" mutex so we never collide with
/// other tools (HWiNFO, LHM, vendor RGB apps) on the bus.
/// </summary>
/// <summary>Byte-level SMBus access (real bus or a test fake).</summary>
public interface ISmbusPort
{
    /// <summary>Read byte data; -1 if nothing answered.</summary>
    int ReadByte(byte addr, byte cmd);
    bool WriteByte(byte addr, byte cmd, byte value);
    bool WriteBlock(byte addr, byte cmd, ReadOnlySpan<byte> data);
    /// <summary>Read word data (little-endian, as SMBus sends it); -1 if nothing answered.</summary>
    int ReadWord(byte addr, byte cmd);
    bool WriteWord(byte addr, byte cmd, ushort value);
}

public sealed class Smbus : ISmbusPort, IDisposable
{
    const string Lib = @"C:\Program Files\PawnIO\PawnIOLib.dll";
    [DllImport(Lib)] static extern int pawnio_open(out IntPtr handle);
    [DllImport(Lib)] static extern int pawnio_load(IntPtr handle, byte[] blob, nuint size);
    [DllImport(Lib)] static extern int pawnio_execute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name, ulong[] inArr, nuint inSize, ulong[] outArr, nuint outSize, out nuint returnSize);
    [DllImport(Lib)] static extern int pawnio_close(IntPtr handle);

    const int READ = 1, WRITE = 0, BYTE_DATA = 2, WORD_DATA = 3, BLOCK_DATA = 5;
    readonly IntPtr _h;
    readonly Mutex _bus = new(false, @"Global\Access_SMBUS.HTP.Method");
    public string Identity { get; } = "";

    public static bool Available => File.Exists(Lib);

    public Smbus()
    {
        Check(pawnio_open(out _h), "open");
        using var s = typeof(Smbus).Assembly.GetManifestResourceStream("Glint.Resources.SmbusPIIX4.bin")
            ?? throw new IOException("SmbusPIIX4 module not embedded");
        var blob = new byte[s.Length];
        s.ReadExactly(blob);
        Check(pawnio_load(_h, blob, (nuint)blob.Length), "load SmbusPIIX4");
        var o = new ulong[3];
        if (pawnio_execute(_h, "ioctl_identity", Array.Empty<ulong>(), 0, o, 3, out _) == 0)
            Identity = $"PIIX4 base 0x{o[1]:x4} pci {o[2] & 0xffffffff:x8}";
    }

    static void Check(int hr, string what) { if (hr < 0) throw new IOException($"PawnIO {what} failed 0x{hr:x8}"); }

    T Locked<T>(Func<T> f)
    {
        bool got = false;
        try
        {
            try { got = _bus.WaitOne(200); } catch (AbandonedMutexException) { got = true; }
            if (!got) throw new TimeoutException("SMBus busy");
            return f();
        }
        finally { if (got) _bus.ReleaseMutex(); }
    }

    /// <summary>Read byte data; -1 on NAK / error (used for safe probing).</summary>
    public int ReadByte(byte addr, byte cmd) => Locked(() =>
    {
        var o = new ulong[1];
        int hr = pawnio_execute(_h, "ioctl_smbus_xfer", new ulong[] { addr, READ, cmd, BYTE_DATA }, 4, o, 1, out _);
        return hr < 0 ? -1 : (int)(o[0] & 0xff);
    });

    public bool WriteByte(byte addr, byte cmd, byte value) => Locked(() =>
        pawnio_execute(_h, "ioctl_smbus_xfer", new ulong[] { addr, WRITE, cmd, BYTE_DATA, value }, 5, Array.Empty<ulong>(), 0, out _) >= 0);

    public int ReadWord(byte addr, byte cmd) => Locked(() =>
    {
        var o = new ulong[1];
        int hr = pawnio_execute(_h, "ioctl_smbus_xfer", new ulong[] { addr, READ, cmd, WORD_DATA }, 4, o, 1, out _);
        return hr < 0 ? -1 : (int)(o[0] & 0xffff);
    });

    public bool WriteWord(byte addr, byte cmd, ushort value) => Locked(() =>
        pawnio_execute(_h, "ioctl_smbus_xfer", new ulong[] { addr, WRITE, cmd, WORD_DATA, value }, 5, Array.Empty<ulong>(), 0, out _) >= 0);

    /// <summary>SMBus block write (≤32 bytes). Data travels as [len, bytes...] packed LE into 5 cells.</summary>
    public bool WriteBlock(byte addr, byte cmd, ReadOnlySpan<byte> data)
    {
        if (data.Length > 32) throw new ArgumentException("block > 32");
        var packed = new byte[40];
        packed[0] = (byte)data.Length;
        data.CopyTo(packed.AsSpan(1));
        var inArr = new ulong[9] { addr, WRITE, cmd, BLOCK_DATA, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 5; i++) inArr[4 + i] = BitConverter.ToUInt64(packed, i * 8);
        return Locked(() => pawnio_execute(_h, "ioctl_smbus_xfer", inArr, 9, Array.Empty<ulong>(), 0, out _) >= 0);
    }

    public void Dispose() { pawnio_close(_h); _bus.Dispose(); }
}
