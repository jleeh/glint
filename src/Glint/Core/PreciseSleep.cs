using System.Runtime.InteropServices;

namespace Glint.Core;

/// <summary>
/// Sub-millisecond sleeps via a high-resolution waitable timer (Win10 1803+). Unlike timeBeginPeriod
/// this doesn't raise the system-wide timer rate, so it costs nothing for the rest of the machine.
/// </summary>
public sealed class PreciseSleep : IDisposable
{
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr CreateWaitableTimerExW(IntPtr attr, IntPtr name, uint flags, uint access);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
    [DllImport("kernel32")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    readonly IntPtr _h = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0x2 /*HIGH_RESOLUTION*/, 0x1F0003);

    public void Sleep(double ms)
    {
        if (ms <= 0) return;
        if (_h == IntPtr.Zero) { Thread.Sleep((int)Math.Ceiling(ms)); return; }
        long due = -(long)(ms * 10_000); // relative, 100 ns units
        SetWaitableTimer(_h, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
        WaitForSingleObject(_h, 0xFFFFFFFF);
    }

    public void Dispose() { if (_h != IntPtr.Zero) CloseHandle(_h); }
}
