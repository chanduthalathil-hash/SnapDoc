using System.Windows;
using System.Windows.Media.Imaging;
using SnapDoc.Models;

namespace SnapDoc.CaptureEngine;

/// <summary>
/// Turns a screen region into a bitmap. Region capture (the drag rectangle) is driven
/// by the overlay window (Views/CaptureOverlay); this interface does the actual pixel grab
/// once the region is known.
/// </summary>
public interface ICaptureEngine
{
    /// <summary>Grab a rectangle of the virtual desktop, in physical pixels.</summary>
    BitmapSource CaptureRegion(Int32Rect regionPhysicalPixels);

    /// <summary>Grab the monitor that currently contains the cursor.</summary>
    BitmapSource CaptureCurrentMonitor();

    /// <summary>Bounds (physical pixels) of the monitor that currently contains the cursor --
    /// the region a screen recording covers, before <see cref="CaptureRegion"/> is used to
    /// actually grab each frame.</summary>
    Int32Rect GetCurrentMonitorBounds();

    /// <summary>Grab the entire virtual desktop (all monitors).</summary>
    BitmapSource CaptureAllScreens();

    /// <summary>Grab a single top-level window by its handle.</summary>
    BitmapSource CaptureWindow(nint hwnd);

    /// <summary>Bounds (physical pixels) of a top-level window right now -- used to fix a
    /// recording's region once at start, since the encoder needs a constant frame size for the
    /// whole recording even if the window itself later moves or resizes.</summary>
    Int32Rect GetWindowBounds(nint hwnd);

    /// <summary>Grab a rectangle of the virtual desktop with the current mouse cursor drawn on
    /// top, in physical pixels -- BitBlt alone never includes the cursor since it isn't part of
    /// the window's own drawn content.</summary>
    BitmapSource CaptureRegionWithCursor(Int32Rect regionPhysicalPixels);
}
