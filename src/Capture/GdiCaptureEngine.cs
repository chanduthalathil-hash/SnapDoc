using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SnapDoc.Models;

namespace SnapDoc.CaptureEngine;

/// <summary>
/// Screen capture via GDI BitBlt. This is the reliable, universal path: it works on every
/// Windows version and every GPU.
///
/// BitBlt alone CANNOT capture hardware-accelerated / DWM-composited windows -- modern Windows
/// 11 File Explorer, Settings, and other Mica/Acrylic-styled apps can come back solid black or
/// white instead of real content. CaptureWindow (only) has a PrintWindow-based fallback for
/// this, because it's the one capture mode where the target window is unambiguous -- the user
/// explicitly clicked it, so there's a specific HWND to re-render with no guessing involved.
///
/// CaptureRegion/CaptureCurrentMonitor/CaptureAllScreens do NOT get this fallback. An earlier
/// version tried to infer "the one window that must be blank" for those too (first by enumerating
/// every overlapping window and guessing paint order, then via WindowFromPoint at the region's
/// centre) and every version of that guess eventually produced a capture of the WRONG window --
/// a worse failure than the blank frame it was meant to fix, because it looks like it succeeded.
/// A region/monitor capture has no single unambiguous target to fall back to, so for those modes
/// we'd rather return a plain BitBlt result (occasionally blank on hardware-accelerated content)
/// than silently substitute a guess that might be completely unrelated content.
///
/// True DRM-protected playback (Netflix etc.) still comes back black either way -- that's a
/// hardware content-protection wall, not something PrintWindow or BitBlt can see around. Only a
/// Windows.Graphics.Capture backend with protected-content APIs could, and even that requires
/// the content owner not to block screen capture entirely.
/// </summary>
public sealed class GdiCaptureEngine : ICaptureEngine
{
    public BitmapSource CaptureRegion(Int32Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0)
            throw new ArgumentException("Capture region must have positive size.", nameof(r));
        return BitBltGrab(r.X, r.Y, r.Width, r.Height);
    }

    public BitmapSource CaptureCurrentMonitor()
    {
        var r = GetCurrentMonitorBounds();
        return BitBltGrab(r.X, r.Y, r.Width, r.Height);
    }

    public Int32Rect GetCurrentMonitorBounds()
    {
        GetCursorPos(out POINT p);
        nint hMon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);
        var rc = mi.rcMonitor;
        return new Int32Rect(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
    }

    public BitmapSource CaptureAllScreens()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return BitBltGrab(x, y, w, h);
    }

    public Int32Rect GetWindowBounds(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rc))
            throw new InvalidOperationException("Could not get window bounds.");
        return new Int32Rect(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
    }

    /// <summary>Same grab as <see cref="CaptureRegion"/>, plus the mouse cursor drawn on top --
    /// BitBlt never includes it since it isn't part of any window's drawn content. Used by the
    /// recorder (screenshots don't need it; a cursor mid-frame there would just be noise).</summary>
    public BitmapSource CaptureRegionWithCursor(Int32Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0)
            throw new ArgumentException("Capture region must have positive size.", nameof(r));
        return BitBltGrab(r.X, r.Y, r.Width, r.Height, drawCursor: true);
    }

    /// <summary>The one capture mode with an unambiguous target: hwnd is exactly what the user
    /// clicked, so if BitBlt can't read it, PrintWindow-ing that specific window is a correction,
    /// not a guess.</summary>
    public BitmapSource CaptureWindow(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rc))
            throw new InvalidOperationException("Could not get window bounds.");
        int width = rc.right - rc.left, height = rc.bottom - rc.top;

        nint screenDc = GetDC(IntPtr.Zero);
        nint memDc = CreateCompatibleDC(screenDc);
        nint hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        nint oldObj = SelectObject(memDc, hBitmap);
        try
        {
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, rc.left, rc.top, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("BitBlt failed (protected content?).");

            if (LooksBlank(memDc, 0, 0, width, height))
                PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT); // overwrite the blank grab in place

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(memDc, oldObj);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// The core grab: copy pixels from the screen DC into a memory bitmap, then hand the HBITMAP
    /// to WPF. All handles are released in finally blocks -- leaking a DC or bitmap here is the
    /// classic capture-app resource leak, so we are strict about it.
    ///
    /// Deliberately synchronous: retry/backoff across whole-capture attempts lives in
    /// CaptureController, which can await between attempts. Blocking the calling (UI) thread
    /// with Thread.Sleep here previously made Windows treat SnapDoc as unresponsive mid-capture
    /// and paint over its own windows -- trading one bad capture for a worse one.
    /// </summary>
    private static BitmapSource BitBltGrab(int x, int y, int width, int height, bool drawCursor = false)
    {
        nint screenDc = GetDC(IntPtr.Zero);
        nint memDc = CreateCompatibleDC(screenDc);
        nint hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        nint oldObj = SelectObject(memDc, hBitmap);
        try
        {
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("BitBlt failed (protected content?).");

            if (drawCursor) DrawCursorOverlay(memDc, x, y);

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // make it cross-thread safe; capture may run off the UI thread
            return source;
        }
        finally
        {
            SelectObject(memDc, oldObj);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Paint the current system cursor onto memDc at its real screen position (offset by
    /// the captured region's origin). GetIconInfo hands back two GDI bitmap handles that WE own --
    /// this runs once per recorded frame, so leaking them would exhaust GDI handles over a
    /// multi-minute recording, hence the finally block.</summary>
    private static void DrawCursorOverlay(nint memDc, int regionX, int regionY)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero) return;

        if (!GetIconInfo(ci.hCursor, out ICONINFO info)) return;
        try
        {
            int x = ci.ptScreenPos.X - regionX - info.xHotspot;
            int y = ci.ptScreenPos.Y - regionY - info.yHotspot;
            DrawIcon(memDc, x, y, ci.hCursor);
        }
        finally
        {
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }
    }

    /// <summary>Sample a dense grid of points across the given sub-rect of memDc; true only if
    /// EVERY sample is the same colour (BitBlt's signature for "couldn't read this content"). A
    /// single sample line was too easy to false-positive on a legitimately uniform strip (a
    /// title bar, a plain toolbar) and trigger an unnecessary, risky replacement.</summary>
    private static bool LooksBlank(nint dc, int x, int y, int w, int h)
    {
        if (w < 6 || h < 6) return false; // too small to sample meaningfully; assume real content
        uint? reference = null;
        for (int row = 1; row <= 5; row++)
        {
            for (int col = 1; col <= 5; col++)
            {
                int sx = x + col * w / 6;
                int sy = y + row * h / 6;
                uint pixel = GetPixel(dc, sx, sy);
                if (pixel == CLR_INVALID) return false;
                if (reference == null) reference = pixel;
                else if (pixel != reference) return false;
            }
        }
        return true;
    }

    // ---------------- Win32 P/Invoke ----------------

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000; // include layered/overlapping windows
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint CLR_INVALID = 0xFFFFFFFF;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private const int CURSOR_SHOWING = 0x1;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public nint hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public nint hbmMask; public nint hbmColor; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIcon(nint hdc, int x, int y, nint hIcon);

    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT rc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint flags);

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint hObj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint hObj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint hdc, int x, int y);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hDest, int xDest, int yDest, int w, int h,
                                      nint hSrc, int xSrc, int ySrc, int rop);
}
