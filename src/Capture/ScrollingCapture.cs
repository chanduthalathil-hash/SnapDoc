using System;
using System.Windows.Media.Imaging;

namespace SnapDoc.CaptureEngine;

/// <summary>
/// Stitched scrolling-window capture (long web pages, chat logs). STUB for v2.
///
/// APPROACH when you build it:
///   1. Capture the visible region.
///   2. Send WM_MOUSEWHEEL / scroll the target by roughly one viewport minus an overlap band.
///   3. Capture again; find the overlap by template-matching the overlap band; stitch.
///   4. Repeat until the bottom stops changing.
/// The fiddly parts are variable scroll amounts and fixed headers/footers that must be de-duplicated.
/// </summary>
public sealed class ScrollingCapture
{
    public BitmapSource Capture(nint hwnd) =>
        throw new NotImplementedException(
            "Scrolling capture is a v2 feature. See the class comment for the stitching approach.");
}
