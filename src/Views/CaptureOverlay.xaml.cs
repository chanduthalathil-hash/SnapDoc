using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace SnapDoc.Views;

/// <summary>
/// A borderless, click-through-sized overlay spanning the whole virtual desktop. The user drags
/// a rectangle; on mouse-up we convert WPF DIPs to physical pixels and raise RegionSelected.
///
/// DPI: WPF coordinates are device-independent (DIPs). Capture needs physical pixels. We convert
/// using the window's DPI. On mixed-DPI multi-monitor setups this is approximate near monitor
/// boundaries; the robust fix is per-monitor conversion, noted inline below.
/// </summary>
public partial class CaptureOverlay : Window
{
    public event Action<Int32Rect>? RegionSelected;
    public event Action<nint>? WindowPicked;

    /// <summary>If true, single-click picks the window under the cursor instead of dragging a region.</summary>
    public bool WindowPickMode { get; set; }

    /// <summary>
    /// True once a real region/window selection has been made. Set synchronously, before Close()
    /// is called, so callers can tell a genuine selection apart from a cancel (Esc / tiny drag)
    /// from inside their Closed handler. This matters because Close() raises WPF's Closed event
    /// synchronously, but RegionSelected/WindowPicked are deliberately raised one dispatcher tick
    /// later (via Dispatcher.BeginInvoke) so the overlay has actually disappeared before the grab
    /// runs -- so Closed always fires first, even on a successful selection. A caller that only
    /// learned "this was real" from inside the RegionSelected/WindowPicked handler itself would
    /// see Closed fire while that still looked like a cancel, and restore hidden windows too early.
    /// </summary>
    public bool SelectionMade { get; private set; }

    private Point _start;
    private bool _dragging;

    public CaptureOverlay()
    {
        InitializeComponent();

        // Cover the entire virtual desktop (all monitors). GetSystemMetrics returns physical
        // pixels; WPF's Left/Top/Width/Height are DIPs, so we convert using the current system
        // DPI. (An earlier version assigned the physical-pixel values directly, which was wrong
        // on any display not at exactly 100% scaling. A version after that switched to
        // SystemParameters.VirtualScreen*, which are DIPs already -- but WPF caches those values
        // and doesn't reliably refresh them, so they can go stale. GetDpiForSystem/GetSystemMetrics
        // are plain Win32 calls with no caching, so they're always current.)
        double scale = GetDpiForSystem() / 96.0;
        Left = GetSystemMetrics(SM_XVIRTUALSCREEN) / scale;
        Top = GetSystemMetrics(SM_YVIRTUALSCREEN) / scale;
        Width = GetSystemMetrics(SM_CXVIRTUALSCREEN) / scale;
        Height = GetSystemMetrics(SM_CYVIRTUALSCREEN) / scale;

        Loaded += (_, _) => Activate();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        if (WindowPickMode)
            HintText.Text = "Click a window to capture  ·  Esc to cancel";
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (WindowPickMode)
        {
            PickWindowUnderCursor();
            return;
        }
        _start = e.GetPosition(RootCanvas);
        _dragging = true;
        SelectionRect.Visibility = Visibility.Visible;
        SizeLabel.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(RootCanvas);
        double x = Math.Min(p.X, _start.X), y = Math.Min(p.Y, _start.Y);
        double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);

        Canvas.SetLeft(SelectionRect, x); Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w; SelectionRect.Height = h;

        Canvas.SetLeft(SizeLabel, x); Canvas.SetTop(SizeLabel, Math.Max(0, y - 26));
        var dpi = VisualTreeHelper.GetDpi(this);
        SizeText.Text = $"{(int)(w * dpi.DpiScaleX)} × {(int)(h * dpi.DpiScaleY)} px";
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var p = e.GetPosition(RootCanvas);
        double x = Math.Min(p.X, _start.X), y = Math.Min(p.Y, _start.Y);
        double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);

        // Query DPI before Close() -- once the window is closed/torn down, VisualTreeHelper.GetDpi
        // on it is unreliable and can silently fall back to a wrong scale, which would throw off
        // every coordinate below.
        var dpi = VisualTreeHelper.GetDpi(this);

        if (w < 4 || h < 4) { Close(); return; } // ignore accidental clicks

        // Convert DIP -> physical pixels, and offset by the virtual-screen origin.
        int px = (int)Math.Round((Left + x) * dpi.DpiScaleX);
        int py = (int)Math.Round((Top + y) * dpi.DpiScaleY);
        int pw = (int)Math.Round(w * dpi.DpiScaleX);
        int ph = (int)Math.Round(h * dpi.DpiScaleY);

        // Set before Close() -- see SelectionMade's doc comment.
        SelectionMade = true;
        Close(); // hide overlay before grabbing, so it isn't captured

        // Dispatch after the overlay has actually closed/repainted.
        Dispatcher.BeginInvoke(new Action(() =>
            RegionSelected?.Invoke(new Int32Rect(px, py, pw, ph))));
    }

    private void PickWindowUnderCursor()
    {
        GetCursorPos(out POINT pt);
        nint hwnd = WindowFromPoint(pt);
        // walk up to the top-level owner
        nint root = GetAncestor(hwnd, GA_ROOT);
        if (root != IntPtr.Zero) SelectionMade = true;
        Close();
        if (root != IntPtr.Zero)
            Dispatcher.BeginInvoke(new Action(() => WindowPicked?.Invoke(root)));
    }

    // --- Win32 ---
    private const uint GA_ROOT = 2;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
