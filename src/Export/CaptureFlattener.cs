using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapDoc.Editor;
using SnapDoc.Models;

namespace SnapDoc.Export;

/// <summary>
/// Renders a capture's base image + all annotations into a single flat bitmap.
/// Every exporter uses this so annotations appear identically across formats, and new
/// annotation types (handled by AnnotationRenderer) export with no exporter changes.
/// </summary>
public static class CaptureFlattener
{
    public static BitmapSource Flatten(Capture capture)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            // 1) base image at native pixel size
            dc.DrawImage(capture.Image, new Rect(0, 0, capture.PixelWidth, capture.PixelHeight));
            // 2) annotations on top, in z-order
            foreach (var ann in capture.Annotations)
                AnnotationRenderer.Draw(dc, ann, capture.Image);
        }

        var rtb = new RenderTargetBitmap(
            capture.PixelWidth, capture.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Flatten and return PNG bytes -- convenient for exporters that embed images.</summary>
    public static byte[] FlattenToPng(Capture capture)
    {
        var bmp = Flatten(capture);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
