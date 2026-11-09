using System.Runtime.InteropServices;

namespace Glint.Tuning;

/// <summary>
/// Offline checks for the GPU tuning layer: hard caps, driver-range ceilings, temperature validity, the simulated
/// stress and the P/Invoke struct layouts. Never loads nvml.dll or nvcuda.dll.
/// </summary>
public static class GpuTuningTests
{
    public static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail) => check("tuning.gpu: " + name, ok, detail);
        static bool Throws<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        // hard caps on the real class's clamp helper and on the simulation (which shares it)
        bool refused = Throws<ArgumentOutOfRangeException>(() => Nvml.CheckOffset(ClockKind.Core, -1))
            && Throws<ArgumentOutOfRangeException>(() => Nvml.CheckOffset(ClockKind.Core, TuningLimits.MaxCoreOffset + 1))
            && Throws<ArgumentOutOfRangeException>(() => Nvml.CheckOffset(ClockKind.Memory, -1))
            && Throws<ArgumentOutOfRangeException>(() => Nvml.CheckOffset(ClockKind.Memory, TuningLimits.MaxMemOffsetNvml + 1));
        C("offset refused outside hard caps", refused, "core <0 or >500 and memory <0 or >6000 must throw");
        bool accepted = !Throws<ArgumentOutOfRangeException>(() => Nvml.CheckOffset(ClockKind.Core, 0))
            && !Throws<ArgumentOutOfRangeException>(() => Nvml.CheckOffset(ClockKind.Core, TuningLimits.MaxCoreOffset))
            && !Throws<ArgumentOutOfRangeException>(() => Nvml.CheckOffset(ClockKind.Memory, TuningLimits.MaxMemOffsetNvml));
        C("offset accepted at the caps", accepted, "0 and the caps themselves are valid");
        var wide = new SimulatedGpu(new SimConfig { CoreRange = new(-5000, 5000), MemRange = new(-5000, 9000) });
        C("simulated card enforces the same caps", Throws<ArgumentOutOfRangeException>(() => wide.SetOffset(ClockKind.Core, 501)) && wide.GetOffset(ClockKind.Core) == 0 && wide.SetCalls == 0,
            "a refused set must not change state");

        // ceilings
        var (c1, m1) = TuningLimits.Ceilings(new(-100, 200), new(-500, 2000));
        C("ceilings use the driver range", c1 == 200 && m1 == 1000, $"core={c1} mem(AB)={m1}");
        var (c2, m2) = TuningLimits.Ceilings(new(-100, 9999), new(-500, 90000));
        C("ceilings never pass the hard caps", c2 == TuningLimits.MaxCoreOffset && m2 == TuningLimits.MaxMemOffsetAfterburner, $"core={c2} mem(AB)={m2}");
        var (c3, m3) = TuningLimits.Ceilings(null, null);
        C("ceilings fall back when the driver reports no range", c3 == 150 && m3 == 500, $"core={c3} mem(AB)={m3}");

        // temperatures
        C("ValidTemp rejects 0 and 255", TuningLimits.ValidTemp(0) == null && TuningLimits.ValidTemp(255) == null && TuningLimits.ValidTemp(null) == null && TuningLimits.ValidTemp(-3) == null, "unreadable values");
        C("ValidTemp keeps real temperatures", TuningLimits.ValidTemp(1) == 1 && TuningLimits.ValidTemp(72.5) == 72.5, "valid values");

        // simulated stress
        static SimulatedStress Ready(SimulatedGpu g) { var s = new SimulatedStress(g); s.Calibrate(); s.Golden(); return s; }
        var g1 = new SimulatedGpu(new SimConfig { UnstableCore = 120 });
        var s1 = Ready(g1);
        g1.SetOffset(ClockKind.Core, 100);
        var okRun = s1.RunCore(2, false, () => { });
        g1.SetOffset(ClockKind.Core, 150);
        var badRun = s1.RunCore(2, false, () => { });
        C("sim stress passes below and fails above the unstable core threshold", okRun.Ok && !badRun.Ok && badRun.Errors > 0 && !badRun.Fault, $"+100 ok={okRun.Ok}, +150 ok={badRun.Ok} errors={badRun.Errors}");

        var g2 = new SimulatedGpu(new SimConfig { UnstableMem = 1000 });
        var s2 = Ready(g2);
        g2.SetOffset(ClockKind.Memory, 800);
        var m_ok = s2.RunMemory(2, () => { });
        g2.SetOffset(ClockKind.Memory, 1200);
        var m_bad = s2.RunMemory(2, () => { });
        C("sim memory test fails above the unstable memory threshold, with lower bandwidth", m_ok.Ok && !m_bad.Ok && m_bad.GBps < m_ok.GBps, $"ok={m_ok.GBps:F0} GB/s, bad={m_bad.GBps:F0} GB/s");

        var g3 = new SimulatedGpu(new SimConfig { EccSupported = true, EccAboveMem = 600 });
        var s3 = Ready(g3);
        g3.SetOffset(ClockKind.Memory, 500);
        s3.RunMemory(2, () => { });
        long? eccStable = g3.EccCorrected();
        g3.SetOffset(ClockKind.Memory, 900);
        s3.RunMemory(2, () => { });
        long? eccGrown = g3.EccCorrected();
        C("simulated ECC-corrected errors grow only above the memory threshold", eccStable == 0 && eccGrown > eccStable, $"stable={eccStable} grown={eccGrown}");
        C("ECC is null when unsupported", new SimulatedGpu().EccCorrected() == null, "no ECC -> null");

        var g4 = new SimulatedGpu(new SimConfig { FaultCore = 200 });
        var s4 = Ready(g4);
        g4.SetOffset(ClockKind.Core, 250);
        var fault = s4.RunCore(1, false, () => { });
        var stillDead = s4.RunMemory(1, () => { });
        s4.Reopen();
        g4.SetOffset(ClockKind.Core, 0);
        var afterReopen = s4.RunCore(1, false, () => { });
        C("driver fault gives Fault=true until Reopen, golden kept", fault.Fault && !fault.Ok && stillDead.Fault && afterReopen.Ok && !afterReopen.Fault && s4.ReopenCalls == 1, $"fault={fault.Fault} dead={stillDead.Fault} afterReopen ok={afterReopen.Ok}");

        var g5 = new SimulatedGpu();
        var s5 = Ready(g5);
        bool aborted = false; int ticks = 0;
        try { s5.RunCore(5, false, () => { if (++ticks == 3) throw new TuneAbort("hot"); }); } catch (TuneAbort) { aborted = true; }
        C("TuneAbort from tick propagates out of the stress run", aborted && ticks == 3 && g5.Load == 0, $"aborted={aborted} ticks={ticks} load reset={g5.Load == 0}");
        var g6 = new SimulatedGpu();
        g6.SetOffset(ClockKind.Core, 50);
        C("golden refuses non-stock clocks", Throws<InvalidOperationException>(() => new SimulatedStress(g6).Golden()), "golden at +50 must throw");
        var hot = new SimulatedGpu(); var sh = Ready(hot);
        double idleT = hot.Sample().CoreC!.Value; double loadT = 0;
        sh.RunCore(1, false, () => loadT = Math.Max(loadT, hot.Sample().CoreC!.Value));
        C("sim temperature rises with load and memory junction is left to the service", loadT > idleT + 10 && hot.Sample().MemJunctionC == null, $"idle={idleT:F1} load={loadT:F1}");
        var gr = new SimulatedGpu(); gr.SetOffset(ClockKind.Core, 100); gr.LockCoreClocks(500, 2000); gr.ResetAll();
        C("ResetAll zeroes offsets and clears the clock lock", gr.GetOffset(ClockKind.Core) == 0 && gr.Locked == null && gr.ResetCalls == 1, "reset");

        // workstation detection and throttle names
        bool ws = new[] { "NVIDIA RTX PRO 6000 Blackwell", "NVIDIA RTX A4000", "Quadro P2200", "Tesla T4", "NVIDIA H100 PCIe", "NVIDIA L40S" }.All(Nvml.IsWorkstationName);
        bool gm = !new[] { "NVIDIA GeForce RTX 5090", "NVIDIA GeForce RTX 4070 Ti SUPER", "NVIDIA GeForce GTX 1080" }.Any(Nvml.IsWorkstationName);
        C("IsWorkstation matches pro cards, not GeForce", ws && gm, $"pro={ws} geforce-clean={gm}");
        var thr = Nvml.ThrottleNames(0x4 | 0x40);
        C("throttle reason bits decode", thr.Count == 2 && thr[0] == "power cap" && thr[1] == "HW thermal" && Nvml.ThrottleNames(0).Count == 0, string.Join(",", thr));

        // P/Invoke layouts (wrong sizes would corrupt the driver's output buffer)
        int co = Marshal.SizeOf<Nvml.ClockOffset>(), ut = Marshal.SizeOf<Nvml.Util>(), mi = Marshal.SizeOf<Nvml.MemInfo>(), pc = Marshal.SizeOf<Nvml.PciInfo>();
        C("NVML struct layouts", co == 24 && ut == 8 && mi == 24 && pc == 68 && Nvml.ClockOffsetVersion == (24u | 1u << 24), $"clockOffset={co} util={ut} mem={mi} pci={pc} ver=0x{Nvml.ClockOffsetVersion:X}");

        // PTX sanity: three entry points, no unfilled template placeholders
        var ptx = CudaStress.Ptx();
        C("PTX has the three kernels and no template leftovers", ptx.Contains(".entry burn") && ptx.Contains(".entry mfill") && ptx.Contains(".entry mcheck") && !ptx.Contains("{o}") && !ptx.Contains("{{") && !ptx.Contains("%%") && ptx.Contains("0f3D4CCCCD"), "kernel text");
    }
}
