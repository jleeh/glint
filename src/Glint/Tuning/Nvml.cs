// Ported from Headroom (MIT, Headroom contributors)
using System.Runtime.InteropServices;
using System.Text;

namespace Glint.Tuning;

/// <summary>An NVML call failed. <see cref="Code"/> is the nvmlReturn_t (3 = not supported, 4 = no permission, 15 = GPU lost).</summary>
public sealed class NvmlException(string fn, int code) : Exception($"{fn} failed: {Describe(code)} ({code})")
{
    public int Code { get; } = code;
    public static string Describe(int c) => c switch
    {
        1 => "uninitialized", 2 => "invalid argument", 3 => "not supported", 4 => "no permission (run Glint as administrator)",
        5 => "already initialized", 6 => "not found", 8 => "insufficient power", 9 => "driver not loaded", 10 => "timeout",
        12 => "lib rm version mismatch", 15 => "GPU is lost", _ => "error",
    };
}

/// <summary>
/// The real GPU controls: NVIDIA's management library (nvml.dll, shipped with the driver) loaded from System32 only,
/// never from the app folder. Mirrors what Headroom's nvml.py does: clock offsets through the newer
/// nvmlDeviceSetClockOffsets (applied to P0 for games and P2 for compute) or, on older drivers, the per-domain VF-offset
/// calls; the driver's own offset range; clock locks; throttle reasons. It never changes power limits or fans.
/// <see cref="SetOffset"/> enforces <see cref="TuningLimits"/> itself so no caller can cross the hard caps.
/// Compile-checked only in tests: logic is covered through <see cref="SimulatedGpu"/>, the real DLL is for a human-run hardware test.
/// </summary>
public sealed class Nvml : IGpuControl
{
    const int Gfx = 0, Mem = 2;
    const uint ClockOffsetV1 = 24 | (1u << 24);   // sizeof(nvmlClockOffset_t) | version 1 << 24

    [StructLayout(LayoutKind.Sequential)]
    internal struct ClockOffset { public uint Version; public int Type, PState, OffsetMHz, MinMHz, MaxMHz; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Util { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemInfo { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PciInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] BusIdLegacy;
        public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] BusId;
    }
    internal static uint ClockOffsetVersion => ClockOffsetV1;

    static class N
    {
        const string L = "nvml.dll";
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlInit_v2();
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlShutdown();
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr h);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetName(IntPtr h, byte[] b, uint n);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetVbiosVersion(IntPtr h, byte[] b, uint n);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlSystemGetDriverVersion(byte[] b, uint n);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetPciInfo_v3(IntPtr h, ref PciInfo p);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr h, ref MemInfo m);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr h, out uint lo, out uint hi);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr h, out uint v);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceSetPowerManagementLimit(IntPtr h, uint mw);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr h, out uint mw);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetTemperatureThreshold(IntPtr h, int threshold, out uint c);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetClockInfo(IntPtr h, int kind, out uint v);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetTemperature(IntPtr h, int sensor, out uint v);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetPowerUsage(IntPtr h, out uint mw);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr h, ref Util u);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetFanSpeed(IntPtr h, out uint v);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetCurrentClocksEventReasons(IntPtr h, out ulong v);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetPerformanceState(IntPtr h, out int v);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetClockOffsets(IntPtr h, ref ClockOffset c);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceSetClockOffsets(IntPtr h, ref ClockOffset c);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetGpcClkVfOffset(IntPtr h, out int v);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetMemClkVfOffset(IntPtr h, out int v);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetGpcClkMinMaxVfOffset(IntPtr h, out int lo, out int hi);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetMemClkMinMaxVfOffset(IntPtr h, out int lo, out int hi);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceSetGpcClkVfOffset(IntPtr h, int v);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceSetMemClkVfOffset(IntPtr h, int v);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceSetGpuLockedClocks(IntPtr h, uint lo, uint hi);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceResetGpuLockedClocks(IntPtr h);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetEccMode(IntPtr h, out int current, out int pending);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetTotalEccErrors(IntPtr h, int errType, int counter, out ulong v);
    }

    enum Api { None, ClockOffsets, VfOffset }

    static readonly (ulong Bit, string Name)[] ThrottleBits =
        [(0x1, "idle"), (0x2, "app clocks"), (0x4, "power cap"), (0x8, "HW slowdown"), (0x20, "SW thermal"), (0x40, "HW thermal"), (0x80, "power brake"), (0x100, "display clocks")];

    readonly object _l = new();
    readonly IntPtr _h;
    readonly Api _api;
    bool _disposed;

    public string Name { get; }
    public string Driver { get; }
    public string Vbios { get; }
    public string? SubsystemVendor { get; }
    public double VramGiB { get; }
    public (double Default, double Max) PowerLimitW { get; }
    double _powerMinW;

    public double? PowerLimitNowW { get { lock (_l) return N.nvmlDeviceGetEnforcedPowerLimit(_h, out var mw) == 0 ? mw / 1000.0 : null; } }

    /// <summary>Within the card's own constraints only (the board firmware's ceiling); no voltage control exists.</summary>
    public void SetPowerLimitW(double watts)
    {
        if (!(watts >= _powerMinW && watts <= PowerLimitW.Max) || PowerLimitW.Max <= 0)
            throw new ArgumentOutOfRangeException(nameof(watts), $"power limit {watts:0} W is outside this card's {_powerMinW:0}..{PowerLimitW.Max:0} W");
        lock (_l) Chk("nvmlDeviceSetPowerManagementLimit", N.nvmlDeviceSetPowerManagementLimit(_h, (uint)Math.Round(watts * 1000)));
    }

    public double? SlowdownC { get { lock (_l) return N.nvmlDeviceGetTemperatureThreshold(_h, 1, out var c) == 0 ? TuningLimits.ValidTemp(c) : null; } }   // NVML_TEMPERATURE_THRESHOLD_SLOWDOWN
    public bool IsWorkstation { get; }

    public Nvml()
    {
        Chk("nvmlInit_v2", N.nvmlInit_v2());
        try
        {
            Chk("nvmlDeviceGetHandleByIndex_v2", N.nvmlDeviceGetHandleByIndex_v2(0, out _h));
            Name = Str(b => N.nvmlDeviceGetName(_h, b, (uint)b.Length), "nvmlDeviceGetName", 96);
            Driver = Str(b => N.nvmlSystemGetDriverVersion(b, (uint)b.Length), "nvmlSystemGetDriverVersion", 80);
            Vbios = TryStr(b => N.nvmlDeviceGetVbiosVersion(_h, b, (uint)b.Length), 32);
            var pci = new PciInfo { BusIdLegacy = new byte[16], BusId = new byte[32] };
            SubsystemVendor = N.nvmlDeviceGetPciInfo_v3(_h, ref pci) == 0 ? (pci.PciSubSystemId & 0xFFFF).ToString("x4") : null;
            var m = new MemInfo();
            Chk("nvmlDeviceGetMemoryInfo", N.nvmlDeviceGetMemoryInfo(_h, ref m));
            VramGiB = m.Total / (double)(1L << 30);
            double def = 0, max = 0;
            if (N.nvmlDeviceGetPowerManagementDefaultLimit(_h, out var d) == 0) def = d / 1000.0;
            if (N.nvmlDeviceGetPowerManagementLimitConstraints(_h, out var lo, out var hi) == 0) { max = hi / 1000.0; _powerMinW = lo / 1000.0; }
            PowerLimitW = (def, max);
            IsWorkstation = IsWorkstationName(Name);
            _api = DetectApi();
        }
        catch { N.nvmlShutdown(); throw; }
    }

    /// <summary>Open the card, or null plus the reason (no NVIDIA driver, no admin...). Never throws.</summary>
    public static Nvml? TryOpen(out string? error)
    {
        try { error = null; return new Nvml(); }
        catch (Exception e) when (e is NvmlException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { error = e.Message; return null; }
    }

    static void Chk(string fn, int r) { if (r != 0) throw new NvmlException(fn, r); }

    static string Str(Func<byte[], int> call, string fn, int size)
    {
        var b = new byte[size];
        Chk(fn, call(b));
        return Encoding.ASCII.GetString(b).TrimEnd('\0');
    }
    static string TryStr(Func<byte[], int> call, int size)
    {
        var b = new byte[size];
        return call(b) == 0 ? Encoding.ASCII.GetString(b).TrimEnd('\0') : "";
    }

    /// <summary>Workstation / data-centre cards: tuning them is opt-in because their drivers and warranties differ.</summary>
    public static bool IsWorkstationName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"\b(RTX\s+PRO|RTX\s+A\d+|Quadro|Tesla|A100|A30|A40|H100|H200|L4|L40S?|T4|V100)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The hard caps, applied to every offset write: core 0..MaxCoreOffset, memory 0..MaxMemOffsetNvml (NVML units).</summary>
    public static void CheckOffset(ClockKind kind, int mhz)
    {
        int hi = kind == ClockKind.Core ? TuningLimits.MaxCoreOffset : TuningLimits.MaxMemOffsetNvml;
        if (mhz < 0 || mhz > hi)
            throw new ArgumentOutOfRangeException(nameof(mhz), mhz, $"{kind} offset {mhz:+#;-#;0} MHz is outside Glint's limit (0 to +{hi})");
    }

    public static IReadOnlyList<string> ThrottleNames(ulong bits) => [.. ThrottleBits.Where(t => (bits & t.Bit) != 0).Select(t => t.Name)];

    Api DetectApi()
    {
        try
        {
            var s = new ClockOffset { Version = ClockOffsetV1, Type = Gfx, PState = 0 };
            if (N.nvmlDeviceGetClockOffsets(_h, ref s) == 0) return Api.ClockOffsets;
        }
        catch (EntryPointNotFoundException) { }
        try { return N.nvmlDeviceGetGpcClkVfOffset(_h, out _) == 0 ? Api.VfOffset : Api.None; }
        catch (EntryPointNotFoundException) { return Api.None; }
    }

    static int Dom(ClockKind k) => k == ClockKind.Core ? Gfx : Mem;

    (int Value, int Min, int Max) Read(ClockKind kind)
    {
        if (_api == Api.VfOffset)
        {
            int v, lo = -1000, hi = 1000;
            if (kind == ClockKind.Core)
            {
                Chk("nvmlDeviceGetGpcClkVfOffset", N.nvmlDeviceGetGpcClkVfOffset(_h, out v));
                try { if (N.nvmlDeviceGetGpcClkMinMaxVfOffset(_h, out var a, out var b) == 0) (lo, hi) = (a, b); } catch (EntryPointNotFoundException) { }
            }
            else
            {
                Chk("nvmlDeviceGetMemClkVfOffset", N.nvmlDeviceGetMemClkVfOffset(_h, out v));
                try { if (N.nvmlDeviceGetMemClkMinMaxVfOffset(_h, out var a, out var b) == 0) (lo, hi) = (a, b); } catch (EntryPointNotFoundException) { }
            }
            return (v, lo, hi);
        }
        if (_api == Api.None) throw new NvmlException("clock offsets (not supported by this card or driver)", 3);
        var s = new ClockOffset { Version = ClockOffsetV1, Type = Dom(kind), PState = 0 };
        Chk("nvmlDeviceGetClockOffsets", N.nvmlDeviceGetClockOffsets(_h, ref s));
        return (s.OffsetMHz, s.MinMHz, s.MaxMHz);
    }

    public OffsetRange? Range(ClockKind kind)
    {
        lock (_l)
        {
            try { var (_, lo, hi) = Read(kind); return lo == 0 && hi == 0 ? null : new OffsetRange(lo, hi); }
            catch (NvmlException) { return null; }
        }
    }

    public int GetOffset(ClockKind kind) { lock (_l) return Read(kind).Value; }

    public void SetOffset(ClockKind kind, int mhz)
    {
        CheckOffset(kind, mhz);
        lock (_l)
        {
            switch (_api)
            {
                case Api.VfOffset:
                    Chk("nvmlDeviceSet*ClkVfOffset", kind == ClockKind.Core ? N.nvmlDeviceSetGpcClkVfOffset(_h, mhz) : N.nvmlDeviceSetMemClkVfOffset(_h, mhz));
                    break;
                case Api.ClockOffsets:
                    // P0 is what games use, P2 what CUDA/compute uses: set both so the stress test sees the same clocks.
                    foreach (var ps in new[] { 0, 2 })
                    {
                        var s = new ClockOffset { Version = ClockOffsetV1, Type = Dom(kind), PState = ps, OffsetMHz = mhz };
                        Chk("nvmlDeviceSetClockOffsets", N.nvmlDeviceSetClockOffsets(_h, ref s));
                    }
                    break;
                default: throw new NvmlException("clock offsets (not supported by this card or driver)", 3);
            }
        }
    }

    public void LockCoreClocks(int minMhz, int maxMhz)
    {
        if (minMhz <= 0 || maxMhz < minMhz) throw new ArgumentOutOfRangeException(nameof(maxMhz), "invalid clock lock range");
        lock (_l) Chk("nvmlDeviceSetGpuLockedClocks", N.nvmlDeviceSetGpuLockedClocks(_h, (uint)minMhz, (uint)maxMhz));
    }

    public void UnlockCoreClocks() { lock (_l) Chk("nvmlDeviceResetGpuLockedClocks", N.nvmlDeviceResetGpuLockedClocks(_h)); }

    public IReadOnlyList<string> ResetAll()
    {
        var errs = new List<string>();
        foreach (var k in new[] { ClockKind.Core, ClockKind.Memory })
            try { SetOffset(k, 0); } catch (NvmlException e) { errs.Add(e.Message); }
        try { UnlockCoreClocks(); } catch (NvmlException e) when (e.Code == 3) { } catch (NvmlException e) { errs.Add(e.Message); }
        // back to the card's default power limit (NOT_SUPPORTED on cards without power control is fine)
        if (PowerLimitW.Default > 0 && PowerLimitNowW is double now && Math.Abs(now - PowerLimitW.Default) > 0.5)
            try { SetPowerLimitW(PowerLimitW.Default); } catch (NvmlException e) when (e.Code == 3) { } catch (Exception e) { errs.Add(e.Message); }
        return errs;
    }

    public GpuSample Sample()
    {
        lock (_l)
        {
            double? temp = N.nvmlDeviceGetTemperature(_h, 0, out var t) == 0 ? TuningLimits.ValidTemp(t) : null;
            double w = N.nvmlDeviceGetPowerUsage(_h, out var mw) == 0 ? mw / 1000.0 : 0;
            N.nvmlDeviceGetClockInfo(_h, Gfx, out var core);
            N.nvmlDeviceGetClockInfo(_h, Mem, out var mem);
            var u = new Util();
            N.nvmlDeviceGetUtilizationRates(_h, ref u);
            int fan = N.nvmlDeviceGetFanSpeed(_h, out var f) == 0 ? (int)f : 0;
            var thr = N.nvmlDeviceGetCurrentClocksEventReasons(_h, out var bits) == 0 ? ThrottleNames(bits) : [];
            return new GpuSample(temp, null, w, (int)core, (int)mem, (int)u.Gpu, fan, thr);
        }
    }

    /// <summary>The card's performance state (0 = P0), or -1 when unreadable. Not on the interface: diagnostics only.</summary>
    public int PState() { lock (_l) return N.nvmlDeviceGetPerformanceState(_h, out var p) == 0 ? p : -1; }

    public long? EccCorrected()
    {
        lock (_l)
        {
            try
            {
                if (N.nvmlDeviceGetEccMode(_h, out var cur, out _) != 0 || cur == 0) return null;
                return N.nvmlDeviceGetTotalEccErrors(_h, 0, 0, out var v) == 0 ? (long)v : null;
            }
            catch (EntryPointNotFoundException) { return null; }
        }
    }

    public void Dispose()
    {
        lock (_l) { if (_disposed) return; _disposed = true; N.nvmlShutdown(); }
    }
}
