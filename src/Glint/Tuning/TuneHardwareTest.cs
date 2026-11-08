using System.Diagnostics;
using System.Text.Json;
using Glint.Core;

namespace Glint.Tuning;

/// <summary>
/// <c>Glint.exe tunetest report.json --hw</c>: the real-hardware half of the tuning checks, for a human to run on their own
/// PC (as administrator, with tuning code paths the offline suites can only simulate). Without --hw it does nothing but say
/// so. With it: NVML opens, reports the card and the driver's offset range; a tiny offset (+25 core / +100 memory) is
/// applied, read back and reset; the CUDA stress test JIT-compiles, calibrates, records reference results and runs short
/// compute and VRAM passes at stock with zero errors. Always ends at stock. Writes a JSON report; exit code = failures.
/// </summary>
public static class TuneHardwareTest
{
    public static int Run(string outPath, bool hw)
    {
        var checks = new List<object>();
        var metrics = new Dictionary<string, object?>();
        int fails = 0;
        void C(string n, bool p, string d = "") { checks.Add(new { name = "tunetest: " + n, pass = p, detail = d }); if (!p) fails++; Console.WriteLine((p ? "ok   " : "FAIL ") + n + (d != "" ? "  " + d : "")); }
        if (!hw)
        {
            C("real hardware not requested (add --hw, run as administrator, close MSI Afterburner)", true);
            Write(outPath, checks, metrics);
            return 0;
        }
        Nvml? nv = null;
        CudaStress? cu = null;
        try
        {
            nv = Nvml.TryOpen(out var err);
            C("NVML opens", nv != null, err ?? "");
            if (nv == null) return Finish();
            metrics["gpu"] = nv.Name; metrics["driver"] = nv.Driver; metrics["vbios"] = nv.Vbios; metrics["vramGiB"] = nv.VramGiB;
            metrics["subsystemVendor"] = nv.SubsystemVendor; metrics["powerLimitW"] = nv.PowerLimitW; metrics["workstation"] = nv.IsWorkstation;
            var rc = nv.Range(ClockKind.Core); var rm = nv.Range(ClockKind.Memory);
            metrics["rangeCore"] = rc; metrics["rangeMem"] = rm;
            C("card info readable", nv.Name.Length > 0 && nv.Driver.Length > 0, $"{nv.Name} · driver {nv.Driver}");
            C("driver reports an offset range", rc != null || rm != null, $"core {rc} · memory {rm}");
            var s0 = nv.Sample();
            metrics["idle"] = s0;
            C("sample at idle is plausible", s0.CoreC is > 5 and < 100 && s0.CoreMhz > 0, JsonSerializer.Serialize(s0));
            metrics["eccCorrected"] = nv.EccCorrected();

            C("hard cap enforced on the real card (+501 core refused)", Throws(() => nv.SetOffset(ClockKind.Core, TuningLimits.MaxCoreOffset + 1)));
            nv.SetOffset(ClockKind.Core, 25); nv.SetOffset(ClockKind.Memory, 100);
            int gc = nv.GetOffset(ClockKind.Core), gm = nv.GetOffset(ClockKind.Memory);
            C("small offset applies and reads back (+25 core / +100 memory)", gc == 25 && gm == 100, $"read core {gc}, memory {gm}");
            var resetErrs = nv.ResetAll();
            C("reset to stock", nv.GetOffset(ClockKind.Core) == 0 && nv.GetOffset(ClockKind.Memory) == 0, string.Join("; ", resetErrs));

            var sw = Stopwatch.StartNew();
            cu = new CudaStress();
            C("CUDA stress test opens and JIT-compiles its kernels", true, $"{sw.ElapsedMilliseconds} ms");
            cu.Calibrate(); cu.Golden();
            C("calibration + reference results at stock", true, $"VRAM test size {cu.MemoryGiB:0.0} GiB");
            int ticks = 0; var tickWatch = Stopwatch.StartNew();
            void Tick() => ticks++;
            var full = cu.RunCore(10, false, Tick);
            C("compute test at stock: zero errors", full.Ok && full.Errors == 0, JsonSerializer.Serialize(full));
            var light = cu.RunCore(8, true, Tick);
            C("light-load test at stock: zero errors", light.Ok && light.Errors == 0, JsonSerializer.Serialize(light));
            var mem = cu.RunMemory(8, Tick);
            C("VRAM test at stock: zero errors, plausible bandwidth", mem.Ok && mem.Errors == 0 && mem.GBps is > 50, JsonSerializer.Serialize(mem));
            double perSec = ticks / Math.Max(1, tickWatch.Elapsed.TotalSeconds);
            C("watchdog tick cadence (~10 per second)", perSec is > 4 and < 40, $"{perSec:0.0}/s");
            bool aborted = false; var abortWatch = Stopwatch.StartNew();
            try { cu.RunCore(30, false, () => { if (abortWatch.Elapsed.TotalSeconds > 1) throw new TuneAbort("test"); }); }
            catch (TuneAbort) { aborted = true; }
            C("a stop request ends a test promptly", aborted && abortWatch.Elapsed.TotalSeconds < 3, $"{abortWatch.Elapsed.TotalSeconds:0.0} s");
            metrics["load"] = nv.Sample();
        }
        catch (Exception e) { C("no unexpected error", false, e.ToString()); }
        finally
        {
            try { cu?.Dispose(); } catch { }
            try { if (nv != null) { nv.ResetAll(); C("ends at stock", nv.GetOffset(ClockKind.Core) == 0 && nv.GetOffset(ClockKind.Memory) == 0); } } catch { }
            try { nv?.Dispose(); } catch { }
        }
        return Finish();

        int Finish() { Write(outPath, checks, metrics); return fails; }
    }

    static bool Throws(Action a) { try { a(); return false; } catch { return true; } }

    static void Write(string path, List<object> checks, Dictionary<string, object?> metrics) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { time = DateTime.Now, checks, metrics }, GlintConfig.Json));
}
