using System.Runtime.InteropServices;

namespace SnapDoc.Services;

/// <summary>
/// Marks a window as invisible to screen-capture APIs (BitBlt, PrintWindow, Windows.Graphics.Capture)
/// while still rendering normally on the real display -- the same mechanism meeting apps use to hide
/// their own toolbars/controls from a screen share. Requires Windows 10 2004+; silently no-ops on
/// older builds rather than failing the whole recording over a cosmetic issue.
/// </summary>
public static class CaptureExclusion
{
    private const uint WDA_NONE = 0x0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    public static void ExcludeFromCapture(nint hwnd)
    {
        try { SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); } catch { /* best effort */ }
    }

    public static void IncludeInCapture(nint hwnd)
    {
        try { SetWindowDisplayAffinity(hwnd, WDA_NONE); } catch { /* best effort */ }
    }
}
