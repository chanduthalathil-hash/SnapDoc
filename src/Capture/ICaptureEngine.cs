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

    /// <summary>Grab the entire virtual desktop (all monitors).</summary>
    BitmapSource CaptureAllScreens();

    /// <summary>Grab a single top-level window by its handle.</summary>
    BitmapSource CaptureWindow(nint hwnd);
}
