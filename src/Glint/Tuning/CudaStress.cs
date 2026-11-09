// Ported from Headroom (MIT, Headroom contributors)
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Glint.Tuning;

/// <summary>A CUDA driver API call failed (the context is probably dead: <see cref="CudaStress.Reopen"/> after a driver reset).</summary>
public sealed class CudaException(string fn, int code) : Exception($"{fn} -> CUDA error {code}") { public int Code { get; } = code; }

/// <summary>
/// Compute + VRAM stress through the CUDA driver API (nvcuda.dll from System32, shipped with the NVIDIA driver, so no
/// toolkit). Kernels are hand-written PTX the driver JIT-compiles.
/// Core test: 8 chaotic logistic-map chains per thread in FP32 FMA. One flipped bit grows into a completely different
/// result, so any computation error from an unstable core clock is a checksum mismatch against the golden result taken
/// at stock clocks. The light mode runs the same work in short bursts with idle gaps (~1/3 duty): a low average load,
/// so the card boosts to the top of its voltage/frequency curve, where big core offsets usually fail first.
/// Memory test: fills VRAM with a hash pattern (128-bit stores), reads it back and counts mismatches, and measures
/// bandwidth, because GDDR7 retries transfers on errors so an unstable memory clock first shows as LOST bandwidth.
/// CUDA errors become <see cref="StressResult.Fault"/>; <see cref="TuneAbort"/> from the tick callback propagates.
/// Compile-checked only in tests (real DLL = human-run hardware test); logic is covered through <see cref="SimulatedStress"/>.
/// </summary>
public sealed class CudaStress : IGpuStress
{
    static class C
    {
        const string L = "nvcuda.dll";
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuInit(uint flags);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuDeviceGet(out int dev, int ordinal);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuCtxCreate_v2(out IntPtr ctx, uint flags, int dev);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuCtxDestroy_v2(IntPtr ctx);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuCtxSetCurrent(IntPtr ctx);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuDeviceGetAttribute(out int v, int attr, int dev);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuModuleLoadDataEx(out IntPtr mod, byte[] image, uint n, int[] opts, IntPtr[] vals);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuModuleUnload(IntPtr mod);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuModuleGetFunction(out IntPtr f, IntPtr mod, string name);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuMemAlloc_v2(out ulong p, nuint n);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuMemFree_v2(ulong p);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuMemGetInfo_v2(out nuint free, out nuint total);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuLaunchKernel(IntPtr f, uint gx, uint gy, uint gz, uint bx, uint by, uint bz, uint shared, IntPtr stream, IntPtr[] args, IntPtr extra);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuCtxSynchronize();
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuMemcpyDtoH_v2(byte[] dst, ulong src, nuint n);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuMemcpyDtoH_v2(uint[] dst, ulong src, nuint n);
        [DllImport(L), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int cuMemsetD32_v2(ulong p, uint v, nuint n);
    }

    const int Block = 256, Chains = 8;
    const long Chunk = 512L << 20;
    static readonly uint[] Seeds = [0x1234567, 0x0BADF00D, 0x5EED, 0x7777];
    static readonly TimeSpan TickEvery = TimeSpan.FromMilliseconds(100);

    readonly object _l = new();
    readonly double _targetMs;
    readonly double _vramFraction;
    readonly Dictionary<string, string> _ref = [];   // "{light}:{seed}" -> checksum at stock
    IntPtr _ctx, _mod;
    int _dev, _sms;
    readonly Dictionary<string, IntPtr> _fn = [];
    readonly List<ulong> _bufs = [];
    ulong _out, _err;
    int _blocks;
    byte[] _host = [];
    readonly List<ulong> _chunks = [];
    int _iters = 2000;
    bool _disposed;

    public double MemoryGiB { get; private set; }

    public CudaStress(double targetMs = 120, double vramFraction = 0.80) { _targetMs = targetMs; _vramFraction = vramFraction; Open(); }

    static void Ck(string fn, int r) { if (r != 0) throw new CudaException(fn, r); }

    void Open()
    {
        Ck("cuInit", C.cuInit(0));
        Ck("cuDeviceGet", C.cuDeviceGet(out _dev, 0));
        Ck("cuCtxCreate_v2", C.cuCtxCreate_v2(out _ctx, 0, _dev));
        try
        {
            Ck("cuDeviceGetAttribute", C.cuDeviceGetAttribute(out _sms, 16, _dev)); // MULTIPROCESSOR_COUNT
            var ptx = Encoding.ASCII.GetBytes(Ptx() + "\0");
            var log = Marshal.AllocHGlobal(8192);
            try
            {
                Marshal.WriteByte(log, 0);
                int r = C.cuModuleLoadDataEx(out _mod, ptx, 2, [5, 6], [log, 8192]); // CU_JIT_ERROR_LOG_BUFFER, ..._SIZE_BYTES
                if (r != 0) throw new CudaException("cuModuleLoadDataEx: " + Marshal.PtrToStringAnsi(log), r);
            }
            finally { Marshal.FreeHGlobal(log); }
            _fn.Clear();
            foreach (var n in new[] { "burn", "mfill", "mcheck" })
            { Ck("cuModuleGetFunction", C.cuModuleGetFunction(out var f, _mod, n)); _fn[n] = f; }
            _blocks = _sms * 16;
            _host = new byte[(long)_blocks * Block * 4];
            _out = Alloc((nuint)_host.Length);
            _err = Alloc(4);
            _chunks.Clear();
            C.cuMemGetInfo_v2(out var free, out _);
            long want = (long)(free * _vramFraction);
            while (want >= Chunk)
            {
                if (C.cuMemAlloc_v2(out var p, (nuint)Chunk) != 0) break;
                _bufs.Add(p); _chunks.Add(p);
                want -= Chunk;
            }
            MemoryGiB = _chunks.Count * Chunk / (double)(1L << 30);
        }
        catch { Close(); throw; }
    }

    ulong Alloc(nuint n) { Ck("cuMemAlloc_v2", C.cuMemAlloc_v2(out var p, n)); _bufs.Add(p); return p; }

    void Close()
    {
        try
        {
            if (_ctx != 0) C.cuCtxSetCurrent(_ctx);
            foreach (var b in _bufs) C.cuMemFree_v2(b);
            if (_mod != 0) C.cuModuleUnload(_mod);
            if (_ctx != 0) C.cuCtxDestroy_v2(_ctx);
        }
        finally { _bufs.Clear(); _chunks.Clear(); _ctx = 0; _mod = 0; MemoryGiB = 0; }
    }

    void Launch(string name, int grid, params object[] args)
    {
        // each argument lives in unmanaged memory for the call; cuLaunchKernel takes an array of pointers to them
        var mem = new IntPtr[args.Length];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                mem[i] = Marshal.AllocHGlobal(8);
                if (args[i] is ulong u) Marshal.WriteInt64(mem[i], unchecked((long)u)); else Marshal.WriteInt32(mem[i], unchecked((int)Convert.ToUInt32(args[i])));
            }
            Ck("cuLaunchKernel", C.cuLaunchKernel(_fn[name], (uint)grid, 1, 1, Block, 1, 1, 0, 0, mem, 0));
        }
        finally { foreach (var m in mem) if (m != 0) Marshal.FreeHGlobal(m); }
    }

    static void Sync() => Ck("cuCtxSynchronize", C.cuCtxSynchronize());

    void Once(int blocks, uint seed) { Launch("burn", blocks, _out, (uint)_iters, seed); Sync(); }

    string Checksum(int blocks)
    {
        int n = blocks * Block * 4;
        if (n == _host.Length) Ck("cuMemcpyDtoH_v2", C.cuMemcpyDtoH_v2(_host, _out, (nuint)n));
        else { var tmp = new byte[n]; Ck("cuMemcpyDtoH_v2", C.cuMemcpyDtoH_v2(tmp, _out, (nuint)n)); return Convert.ToHexString(SHA256.HashData(tmp)); }
        return Convert.ToHexString(SHA256.HashData(_host));
    }

    static string Key(bool light, uint seed) => $"{(light ? 1 : 0)}:{seed}";

    public void Calibrate()
    {
        lock (_l)
        {
            Ck("cuCtxSetCurrent", C.cuCtxSetCurrent(_ctx));
            for (int i = 0; i < 6; i++)
            {
                var sw = Stopwatch.StartNew();
                Once(_blocks, 1);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms > 5)
                {
                    _iters = Math.Max(1000, (int)(_iters * _targetMs / ms));
                    if (Math.Abs(ms - _targetMs) < _targetMs * 0.25) break;
                }
                else _iters *= 8;
            }
        }
    }

    public void Golden()
    {
        lock (_l)
        {
            Ck("cuCtxSetCurrent", C.cuCtxSetCurrent(_ctx));
            foreach (var s in Seeds)   // light mode runs the same launches (in bursts), so one set of references serves both
            {
                Once(_blocks, s); var a = Checksum(_blocks);
                Once(_blocks, s); var b = Checksum(_blocks);
                if (a != b) throw new InvalidOperationException("GPU gave different results at STOCK clocks - not safe to tune (already unstable or a driver problem).");
                _ref[Key(false, s)] = a;
                _ref[Key(true, s)] = a;
            }
        }
    }

    static StressResult Fault(long launches, long errors, Exception e) => new(false, launches, errors, 0, null, e.Message, true);

    public StressResult RunCore(double seconds, bool light, Action tick)
    {
        lock (_l)
        {
            long launches = 0, errors = 0;
            try
            {
                if (_ref.Count == 0) throw new InvalidOperationException("Golden() has not run");
                Ck("cuCtxSetCurrent", C.cuCtxSetCurrent(_ctx));
                int blocks = _blocks;
                var t0 = Stopwatch.StartNew(); var last = Stopwatch.StartNew();
                double busy = 0;
                for (int i = 0; t0.Elapsed.TotalSeconds < seconds; i++)
                {
                    var s = Seeds[i % Seeds.Length];
                    var t = Stopwatch.StartNew();
                    Once(blocks, s);
                    double ran = t.Elapsed.TotalSeconds;
                    busy += ran;
                    launches++;
                    if (Checksum(blocks) != _ref[Key(light, s)]) errors++;
                    if (last.Elapsed >= TickEvery) { tick(); last.Restart(); }
                    if (light)
                    {   // idle twice as long as the burst: low average load, top boost clocks during the bursts
                        var idle = Stopwatch.StartNew();
                        while (idle.Elapsed.TotalSeconds < ran * 2 && t0.Elapsed.TotalSeconds < seconds)
                        {
                            Thread.Sleep(Math.Max(1, Math.Min(50, (int)((ran * 2 - idle.Elapsed.TotalSeconds) * 1000))));
                            if (last.Elapsed >= TickEvery) { tick(); last.Restart(); }
                        }
                    }
                }
                double rate = busy > 0 ? launches * (double)blocks * Block * _iters * Chains / busy : 0;
                return new(errors == 0, launches, errors, rate, null, errors == 0 ? null : $"{errors} of {launches} compute results differ from stock", false);
            }
            catch (CudaException e) { return Fault(launches, errors, e); }
        }
    }

    public StressResult RunMemory(double seconds, Action tick)
    {
        lock (_l)
        {
            long passes = 0, errors = 0;
            try
            {
                if (_chunks.Count == 0) throw new InvalidOperationException("no VRAM could be allocated for the memory test");
                Ck("cuCtxSetCurrent", C.cuCtxSetCurrent(_ctx));
                int blocks = _sms * 8; uint n4 = (uint)(Chunk / 16);
                var t0 = Stopwatch.StartNew(); var last = Stopwatch.StartNew();
                double busy = 0, moved = 0; uint seed = 0x9E37;
                var herr = new uint[1];
                while (t0.Elapsed.TotalSeconds < seconds)
                {
                    seed = unchecked(seed * 1103515245 + 12345) & 0x7FFFFFFF;
                    Ck("cuMemsetD32_v2", C.cuMemsetD32_v2(_err, 0, 1));
                    var t = Stopwatch.StartNew();
                    for (int k = 0; k < _chunks.Count; k++) Launch("mfill", blocks, _chunks[k], n4, seed ^ (uint)k);
                    Sync();
                    if (last.Elapsed >= TickEvery) { tick(); last.Restart(); }
                    for (int k = 0; k < _chunks.Count; k++) Launch("mcheck", blocks, _chunks[k], n4, seed ^ (uint)k, _err);
                    Sync();
                    busy += t.Elapsed.TotalSeconds;
                    moved += 2.0 * _chunks.Count * Chunk;
                    Ck("cuMemcpyDtoH_v2", C.cuMemcpyDtoH_v2(herr, _err, 4));
                    errors += herr[0]; passes++;
                    if (last.Elapsed >= TickEvery) { tick(); last.Restart(); }
                }
                double gbps = busy > 0 ? moved / busy / 1e9 : 0;
                return new(errors == 0, passes, errors, t0.Elapsed.TotalSeconds > 0 ? passes / t0.Elapsed.TotalSeconds : 0, gbps, errors == 0 ? null : $"{errors} VRAM mismatches", false);
            }
            catch (CudaException e) { return Fault(passes, errors, e); }
        }
    }

    /// <summary>After a driver reset the context is dead: throw it away and start over. Golden results and calibration are kept.</summary>
    public void Reopen()
    {
        lock (_l)
        {
            try { Close(); } catch { }
            Open();
        }
    }

    public void Dispose()
    {
        lock (_l) { if (_disposed) return; _disposed = true; try { Close(); } catch { } }
    }

    const string Hash = """
            xor.b32 {o}, {i}, {s};
            shr.u32 {t}, {o}, 16;  xor.b32 {o}, {o}, {t};
            mul.lo.u32 {o}, {o}, 0x7feb352d;
            shr.u32 {t}, {o}, 15;  xor.b32 {o}, {o}, {t};
            mul.lo.u32 {o}, {o}, 0x846ca68b;
            shr.u32 {t}, {o}, 16;  xor.b32 {o}, {o}, {t};

        """;

    static string H(string o) => Hash.Replace("{o}", o).Replace("{i}", "%r10").Replace("{s}", "%r3").Replace("{t}", "%r11");

    /// <summary>The PTX module text (internal so tests can check its structure).</summary>
    internal static string Ptx()
    {
        var init = new StringBuilder(); var step = new StringBuilder(); var sums = new StringBuilder();
        for (int k = 0; k < Chains; k++)
        {
            uint bits = BitConverter.SingleToUInt32Bits(0.05f + 0.005f * k);
            init.Append($"    fma.rn.f32 %f{k + 1}, %f0, 0f3F4CCCCD, 0f{bits:X8};\n");
            step.Append($"    neg.f32 %g{k}, %f{k + 1};\n    fma.rn.f32 %g{k}, %g{k}, %f{k + 1}, %f{k + 1};\n    mul.rn.f32 %f{k + 1}, %g{k}, %f20;\n");
            if (k > 0) sums.Append($"    add.f32 %f9, %f9, %f{k + 1};\n");
        }
        var fill = new StringBuilder(); var check = new StringBuilder();
        for (int k = 0; k < 4; k++)
        {
            fill.Append($"    add.u32 %r10, %r9, {k};\n").Append(H($"%rv{k}"));
            check.Append($"    add.u32 %r10, %r9, {k};\n").Append(H($"%rh{k}"))
                 .Append($"    setp.ne.u32 %q, %rv{k}, %rh{k};\n    @%q add.u32 %r12, %r12, 1;\n");
        }
        return $$"""
.version 6.0
.target sm_50
.address_size 64

.visible .entry burn(.param .u64 p_out, .param .u32 p_iters, .param .u32 p_seed)
{
    .reg .pred %p;
    .reg .b32 %r<12>;
    .reg .f32 %f<24>;
    .reg .f32 %g<8>;
    .reg .b64 %rd<4>;
    ld.param.u64 %rd1, [p_out];
    ld.param.u32 %r1, [p_iters];
    ld.param.u32 %r2, [p_seed];
    mov.u32 %r3, %ctaid.x;
    mov.u32 %r4, %ntid.x;
    mov.u32 %r5, %tid.x;
    mad.lo.u32 %r6, %r3, %r4, %r5;
    xor.b32 %r7, %r6, %r2;
    mul.lo.u32 %r7, %r7, 0x9E3779B1;
    shr.u32 %r8, %r7, 9;
    or.b32 %r8, %r8, 0x3F800000;
    mov.b32 %f0, %r8;
    sub.f32 %f0, %f0, 0f3F800000;
{{init}}    mov.f32 %f20, 0f407F5C29;
LOOP:
{{step}}    sub.u32 %r1, %r1, 1;
    setp.ne.u32 %p, %r1, 0;
    @%p bra LOOP;
    mov.f32 %f9, %f1;
{{sums}}    mul.wide.u32 %rd2, %r6, 4;
    add.u64 %rd3, %rd1, %rd2;
    st.global.f32 [%rd3], %f9;
    ret;
}

.visible .entry mfill(.param .u64 p_buf, .param .u32 p_n4, .param .u32 p_seed)
{
    .reg .pred %p;
    .reg .b32 %r<14>;
    .reg .b32 %rv<4>;
    .reg .b64 %rd<4>;
    ld.param.u64 %rd1, [p_buf];
    ld.param.u32 %r2, [p_n4];
    ld.param.u32 %r3, [p_seed];
    mov.u32 %r4, %ctaid.x;
    mov.u32 %r5, %ntid.x;
    mov.u32 %r6, %tid.x;
    mad.lo.u32 %r7, %r4, %r5, %r6;
    mov.u32 %r8, %nctaid.x;
    mul.lo.u32 %r8, %r8, %r5;
FL:
    setp.ge.u32 %p, %r7, %r2;
    @%p bra FE;
    shl.b32 %r9, %r7, 2;
{{fill}}    mul.wide.u32 %rd2, %r7, 16;
    add.u64 %rd3, %rd1, %rd2;
    st.global.v4.u32 [%rd3], {%rv0, %rv1, %rv2, %rv3};
    add.u32 %r7, %r7, %r8;
    bra FL;
FE:
    ret;
}

.visible .entry mcheck(.param .u64 p_buf, .param .u32 p_n4, .param .u32 p_seed, .param .u64 p_err)
{
    .reg .pred %p;
    .reg .pred %q;
    .reg .b32 %r<14>;
    .reg .b32 %rv<4>;
    .reg .b32 %rh<4>;
    .reg .b64 %rd<6>;
    ld.param.u64 %rd1, [p_buf];
    ld.param.u32 %r2, [p_n4];
    ld.param.u32 %r3, [p_seed];
    ld.param.u64 %rd4, [p_err];
    mov.u32 %r4, %ctaid.x;
    mov.u32 %r5, %ntid.x;
    mov.u32 %r6, %tid.x;
    mad.lo.u32 %r7, %r4, %r5, %r6;
    mov.u32 %r8, %nctaid.x;
    mul.lo.u32 %r8, %r8, %r5;
    mov.u32 %r12, 0;
CL:
    setp.ge.u32 %p, %r7, %r2;
    @%p bra CE;
    mul.wide.u32 %rd2, %r7, 16;
    add.u64 %rd3, %rd1, %rd2;
    ld.global.v4.u32 {%rv0, %rv1, %rv2, %rv3}, [%rd3];
    shl.b32 %r9, %r7, 2;
{{check}}    add.u32 %r7, %r7, %r8;
    bra CL;
CE:
    setp.eq.u32 %p, %r12, 0;
    @%p bra CX;
    atom.global.add.u32 %r13, [%rd4], %r12;
CX:
    ret;
}

""";
    }
}
