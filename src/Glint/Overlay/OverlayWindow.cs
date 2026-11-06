using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Glint.GameOverlay;

/// <summary>Where the overlay is drawn. The real one is a layered window; tests use <see cref="OffscreenSurface"/>.</summary>
public interface IOverlaySurface : IDisposable
{
    /// <summary>Draw rows at a screen position (physical pixels). Called on the UI thread.</summary>
    void Present(IReadOnlyList<OverlayRow> rows, OverlayStyle style, Point topLeftOrRight, bool alignRight, bool alignBottom, double opacity);
    void Hide();
}

/// <summary>Keeps the last frame in memory; never creates a window.</summary>
public sealed class OffscreenSurface : IOverlaySurface
{
    public Bitmap? Last { get; private set; }
    public int Presents { get; private set; }
    public bool Shown { get; private set; }
    public Rectangle Bounds { get; private set; }
    public void Present(IReadOnlyList<OverlayRow> rows, OverlayStyle style, Point anchor, bool right, bool bottom, double opacity)
    {
        Last?.Dispose();
        Last = OverlayRenderer.Render(rows, style);
        Bounds = new Rectangle(right ? anchor.X - Last.Width : anchor.X, bottom ? anchor.Y - Last.Height : anchor.Y, Last.Width, Last.Height);
        Presents++; Shown = true;
    }
    public void Hide() => Shown = false;
    public void Dispose() { Last?.Dispose(); Last = null; }
}

/// <summary>
/// Borderless, click-through, topmost, never-activated, not in Alt-Tab (WS_EX_LAYERED | TRANSPARENT | TOPMOST |
/// NOACTIVATE | TOOLWINDOW). Pixels go in with UpdateLayeredWindow from one reused premultiplied DIB section that
/// GDI+ draws into directly, so a redraw allocates nothing but the strings.
/// </summary>
public sealed class LayeredOverlayWindow : NativeWindow, IOverlaySurface
{
    IntPtr _dc, _dib, _old, _bits;
    Bitmap? _wrap;
    Size _size;
    bool _shown;

    public LayeredOverlayWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Glint overlay",
            Style = unchecked((int)0x80000000),                            // WS_POPUP
            ExStyle = 0x80000 | 0x20 | 0x8 | 0x8000000 | 0x80,              // LAYERED | TRANSPARENT | TOPMOST | NOACTIVATE | TOOLWINDOW
        });
    }

    public void Present(IReadOnlyList<OverlayRow> rows, OverlayStyle style, Point anchor, bool right, bool bottom, double opacity)
    {
        var sz = OverlayRenderer.Measure(rows, style);
        if (sz.IsEmpty) { Hide(); return; }
        Ensure(sz);
        using (var g = Graphics.FromImage(_wrap!))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            OverlayRenderer.Draw(g, rows, style);
        }
        GdiFlush();
        var dst = new POINT { X = right ? anchor.X - sz.Width : anchor.X, Y = bottom ? anchor.Y - sz.Height : anchor.Y };
        var size = new SIZE { cx = sz.Width, cy = sz.Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 25, 255), AlphaFormat = 1 };
        IntPtr screen = GetDC(IntPtr.Zero);
        try { UpdateLayeredWindow(Handle, screen, ref dst, ref size, _dc, ref src, 0, ref blend, 2 /* ULW_ALPHA */); }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        // games raise their own windows; stay above them without taking focus
        SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, (uint)(0x1 | 0x2 | 0x10 | (_shown ? 0 : 0x40))); // NOSIZE|NOMOVE|NOACTIVATE|(SHOWWINDOW)
        _shown = true;
    }

    void Ensure(Size sz)
    {
        if (_wrap != null && sz == _size) return;
        Free();
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = sz.Width, biHeight = -sz.Height, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            _dc = CreateCompatibleDC(screen);
            _dib = CreateDIBSection(screen, ref bi, 0, out _bits, IntPtr.Zero, 0);
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        _old = SelectObject(_dc, _dib);
        _wrap = new Bitmap(sz.Width, sz.Height, sz.Width * 4, PixelFormat.Format32bppPArgb, _bits);
        _size = sz;
    }

    void Free()
    {
        _wrap?.Dispose(); _wrap = null;
        if (_dc != IntPtr.Zero) { SelectObject(_dc, _old); DeleteDC(_dc); _dc = IntPtr.Zero; }
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
        _size = Size.Empty;
    }

    public void Hide()
    {
        if (!_shown) return;
        ShowWindow(Handle, 0);
        _shown = false;
        Free();  // nothing kept while hidden
    }

    public void Dispose()
    {
        Free();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("user32")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("gdi32")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32")] static extern bool GdiFlush();
    [DllImport("gdi32")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}

/// <summary>The foreground window: whose frames to count, which monitor to draw on, and whether it looks like a game.</summary>
public readonly record struct ForegroundInfo(IntPtr Hwnd, int Pid, Rectangle Window, Rectangle Monitor, Rectangle Work, double DpiScale, string ClassName)
{
    /// <summary>Covers its whole monitor (exclusive or borderless fullscreen).</summary>
    public bool Fullscreen => !Monitor.IsEmpty && Window.Left <= Monitor.Left && Window.Top <= Monitor.Top && Window.Right >= Monitor.Right && Window.Bottom >= Monitor.Bottom;
}

public static class Foreground
{
    static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase) { "explorer", "Glint", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "LockApp" };

    public static bool IsGame(ForegroundInfo f, string processName) =>
        f.Pid != 0 && f.Pid != Environment.ProcessId && f.Fullscreen && !ShellClasses.Contains(f.ClassName) && !NotGames.Contains(processName);

    public static ForegroundInfo Probe()
    {
        var h = GetForegroundWindow();
        GetWindowThreadProcessId(h, out uint pid);
        var mon = MonitorFromWindow(h, 2 /* NEAREST */);
        var mi = new MONITORINFO { cbSize = 40 };
        GetMonitorInfoW(mon, ref mi);
        double scale = GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1;
        RECT wr = default;
        if (h != IntPtr.Zero) GetWindowRect(h, out wr);
        var cls = new char[64];
        int n = h == IntPtr.Zero ? 0 : GetClassNameW(h, cls, cls.Length);
        return new ForegroundInfo(h, (int)pid, wr.R, mi.rcMonitor.R, mi.rcWork.R, scale, new string(cls, 0, Math.Max(0, n)));
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, Rt, B; public Rectangle R => Rectangle.FromLTRB(L, T, Rt, B); }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32")] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, char[] buf, int max);
    [DllImport("shcore")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
}
