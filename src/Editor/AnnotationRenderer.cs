using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using SnapDoc.Models;

namespace SnapDoc.Editor;

/// <summary>
/// Single source of truth for how each annotation type is drawn. Both the live editor overlay
/// and the export flattener call this, so what you see is exactly what you export.
///
/// ADD A NEW ANNOTATION TYPE: add a case here. That's the only required change.
/// </summary>
public static class AnnotationRenderer
{
    /// <summary>The actual rendered size of a text annotation's content at its current font
    /// settings -- the editor uses this to keep TextAnnotation.Bounds (its hit-test/selection
    /// area) matching what's really on screen, since typing new text or changing font size doesn't
    /// otherwise touch Bounds at all.</summary>
    public static Size MeasureText(TextAnnotation t)
    {
        var typeface = new Typeface(new FontFamily(t.FontFamily), t.FontStyle, t.FontWeight, FontStretches.Normal);
        var ft = new FormattedText(
            string.IsNullOrEmpty(t.Text) ? " " : t.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, t.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);
        return new Size(ft.Width, ft.Height);
    }

    /// <param name="baseImage">The capture's own base image, needed only by BlurAnnotation (it
    /// redacts the underlying pixels rather than drawing anything of its own). Both call sites
    /// (the live editor and CaptureFlattener) always have this on hand.</param>
    public static void Draw(DrawingContext dc, Annotation ann, BitmapSource baseImage)
    {
        if (!ann.IsVisible) return;

        bool pushedOpacity = ann.Opacity < 1.0;
        if (pushedOpacity) dc.PushOpacity(ann.Opacity);
        switch (ann)
        {
            case RectangleAnnotation r: DrawRectangle(dc, r); break;
            case EllipseAnnotation e:   DrawEllipse(dc, e);   break;
            case ArrowAnnotation a:     DrawArrow(dc, a);     break;
            case LineAnnotation l:      DrawLine(dc, l);      break;
            case InkAnnotation ink:     DrawInk(dc, ink);     break;
            case TextAnnotation t:      DrawText(dc, t);      break;
            case StepAnnotation s:      DrawStep(dc, s);      break;
            case BlurAnnotation b:      DrawBlur(dc, b, baseImage); break;
            case CalloutAnnotation c:   DrawCallout(dc, c);   break;
        }
        if (pushedOpacity) dc.Pop();
    }

    private static Pen Stroke(Annotation a) =>
        new(new SolidColorBrush(a.Color), a.Thickness) { LineJoin = PenLineJoin.Round };

    private static void DrawRectangle(DrawingContext dc, RectangleAnnotation r)
    {
        Brush? fill = r.Filled ? new SolidColorBrush(r.Color) { Opacity = 0.25 } : null;
        if (r.CornerRadius > 0)
            dc.DrawRoundedRectangle(fill, Stroke(r), r.Bounds, r.CornerRadius, r.CornerRadius);
        else
            dc.DrawRectangle(fill, Stroke(r), r.Bounds);
    }

    private static void DrawEllipse(DrawingContext dc, EllipseAnnotation e)
    {
        var c = new Point(e.Bounds.X + e.Bounds.Width / 2, e.Bounds.Y + e.Bounds.Height / 2);
        dc.DrawEllipse(null, Stroke(e), c, e.Bounds.Width / 2, e.Bounds.Height / 2);
    }

    private static void DrawArrow(DrawingContext dc, ArrowAnnotation a)
    {
        Vector dir = a.End - a.Start;
        double length = dir.Length;
        if (length < 0.01) return; // zero-length: no direction to draw anything along, avoid NaN
        dir.Normalize();
        var perp = new Vector(-dir.Y, dir.X); // perpendicular to the shaft -- valid for any angle,
                                               // no left-to-right/top-to-bottom assumption anywhere here

        // Scales with stroke thickness (was a fixed-ish constant before) so a 1px arrow still gets
        // a visible head and a 32px arrow gets a proportionally bigger one, not the same size either way.
        double headLength = Math.Max(10, a.Thickness * 4);
        // A pure Thickness*3 floor goes needle-thin once headLength hits its 10px clamp on very
        // thin strokes (e.g. Thickness=1 -> width 3 vs length 10); tie the minimum to headLength
        // too so the head keeps a sane, recognizable triangle shape at every stroke width.
        double headWidth = Math.Max(a.Thickness * 3, headLength * 0.6);

        // Short arrow: don't let the head overshoot past Start or invert -- clamp to the arrow's
        // actual length, keeping the same width/length aspect ratio rather than just capping length.
        if (headLength > length)
        {
            headWidth *= length / headLength;
            headLength = length;
        }

        var basePoint = a.End - dir * headLength;
        var barb1 = basePoint + perp * (headWidth / 2);
        var barb2 = basePoint - perp * (headWidth / 2);

        // Shaft stops at the head's base, not the tip -- drawing it all the way to a.End (the old
        // behavior) left the constant-width line extending into the head's footprint, where the
        // triangle (which narrows to a point) no longer fully covers it, so the shaft's edges
        // visibly poked out past the head near the tip. Flat caps explicitly, so neither end grows
        // past its logical point (Round/Square would add Thickness/2 beyond basePoint and Start).
        var pen = Stroke(a);
        pen.StartLineCap = pen.EndLineCap = PenLineCap.Flat;
        dc.DrawLine(pen, a.Start, basePoint);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(a.End, true, true); // tip sits exactly at the endpoint -- no overshoot, no gap
            ctx.LineTo(barb1, true, false);
            ctx.LineTo(barb2, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(new SolidColorBrush(a.Color), null, geo);
    }

    private static void DrawLine(DrawingContext dc, LineAnnotation l) => dc.DrawLine(Stroke(l), l.Start, l.End);

    private static void DrawInk(DrawingContext dc, InkAnnotation ink)
    {
        if (ink.Points.Count < 2) return;
        var pen = Stroke(ink);
        var brush = (SolidColorBrush)pen.Brush;
        if (ink.Highlighter) { brush.Opacity = 0.4; pen.Thickness = ink.Thickness * 3; }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(ink.Points[0], false, false);
            for (int i = 1; i < ink.Points.Count; i++)
                ctx.LineTo(ink.Points[i], true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static void DrawText(DrawingContext dc, TextAnnotation t)
    {
        var typeface = new Typeface(new FontFamily(t.FontFamily), t.FontStyle, t.FontWeight, FontStretches.Normal);
        var ft = new FormattedText(
            t.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, t.FontSize, new SolidColorBrush(t.Color),
            VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip)
        { TextAlignment = t.Alignment };
        if (t.Underline) ft.SetTextDecorations(TextDecorations.Underline);

        var origin = new Point(t.Bounds.X, t.Bounds.Y);

        bool rotated = Math.Abs(t.Rotation) > 0.01;
        if (rotated)
        {
            var center = new Point(t.Bounds.X + t.Bounds.Width / 2, t.Bounds.Y + t.Bounds.Height / 2);
            dc.PushTransform(new RotateTransform(t.Rotation, center.X, center.Y));
        }

        if (t.Background)
        {
            var bg = new Rect(origin.X - 4, origin.Y - 2, ft.Width + 8, ft.Height + 4);
            dc.DrawRectangle(new SolidColorBrush(Colors.White) { Opacity = 0.85 }, null, bg);
        }

        if (t.Shadow || t.Outline)
        {
            // FormattedText has no built-in stroke/shadow -- build its outline Geometry once and
            // draw THAT instead of calling DrawText, so Outline can supply a Pen and Shadow can
            // draw an offset translucent copy behind it. Plain DrawText stays the fast path when
            // neither effect is on, since geometry constuction is needless work most text won't use.
            var geo = ft.BuildGeometry(origin);
            if (t.Shadow)
            {
                var shadowGeo = geo.Clone();
                shadowGeo.Transform = new TranslateTransform(2, 2);
                dc.DrawGeometry(new SolidColorBrush(Colors.Black) { Opacity = 0.45 }, null, shadowGeo);
            }
            Pen? outlinePen = t.Outline ? new Pen(Brushes.Black, Math.Max(1, t.FontSize / 16.0)) : null;
            dc.DrawGeometry(new SolidColorBrush(t.Color), outlinePen, geo);
        }
        else
        {
            dc.DrawText(ft, origin);
        }

        if (rotated) dc.Pop();
    }

    private static void DrawStep(DrawingContext dc, StepAnnotation s)
    {
        var center = new Point(s.Bounds.X + s.Radius, s.Bounds.Y + s.Radius);
        dc.DrawEllipse(new SolidColorBrush(s.Color), new Pen(Brushes.White, 2), center, s.Radius, s.Radius);

        var ft = new FormattedText(
            s.Number.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            s.Radius, Brushes.White, 1.0);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    // Pixelating a region is real per-pixel work (unlike every other annotation, which is cheap
    // vector drawing) -- and Redraw() reruns on every mouse-move during ANY drag anywhere on the
    // canvas, not just this shape's own. Recomputing from scratch every frame would be wasted work
    // the moment there's more than one Blur region or the user is dragging something else nearby.
    // Cache the pixelated result per-annotation; ConditionalWeakTable means it's simply forgotten
    // (no manual cleanup) once the BlurAnnotation itself is deleted/GC'd.
    private static readonly ConditionalWeakTable<BlurAnnotation, PixelateCache> _blurCache = new();
    private sealed class PixelateCache
    {
        public Rect Bounds;
        public double BlockSize;
        public BitmapSource? SourceImage; // Crop swaps in a whole new base image (SetImage) --
        public BitmapSource? Bitmap;      // compare by reference so a crop always invalidates,
    }                                     // even in the edge case where Bounds happens not to shift

    private static void DrawBlur(DrawingContext dc, BlurAnnotation b, BitmapSource baseImage)
    {
        int x = (int)Math.Max(0, Math.Round(b.Bounds.X));
        int y = (int)Math.Max(0, Math.Round(b.Bounds.Y));
        int w = (int)Math.Min(baseImage.PixelWidth - x, Math.Round(b.Bounds.Width));
        int h = (int)Math.Min(baseImage.PixelHeight - y, Math.Round(b.Bounds.Height));
        if (w < 1 || h < 1) return;

        var cache = _blurCache.GetOrCreateValue(b);
        if (cache.Bitmap == null || cache.Bounds != b.Bounds || cache.BlockSize != b.BlockSize ||
            !ReferenceEquals(cache.SourceImage, baseImage))
        {
            cache.Bitmap = Pixelate(baseImage, x, y, w, h, b.BlockSize);
            cache.Bounds = b.Bounds;
            cache.BlockSize = b.BlockSize;
            cache.SourceImage = baseImage;
        }
        dc.DrawImage(cache.Bitmap, new Rect(x, y, w, h));
    }

    /// <summary>Block-average pixelation via a manual pixel buffer, not a soft blur -- a Gaussian
    /// blur can leave text/faces partially legible (sharpening can claw some of it back); replacing
    /// each cell with a flat average destroys the underlying detail outright, which is the actual
    /// point of a redaction tool. Manual buffer math (not e.g. a scaled-down-then-up BitmapSource)
    /// so the result doesn't depend on a GPU interpolation mode silently smoothing it back out.</summary>
    private static BitmapSource Pixelate(BitmapSource baseImage, int x, int y, int w, int h, double blockSizeSetting)
    {
        var cropped = new CroppedBitmap(baseImage, new Int32Rect(x, y, w, h));
        var converted = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        converted.CopyPixels(pixels, stride, 0);

        int blockSize = Math.Max(2, (int)blockSizeSetting);
        byte[] outPixels = new byte[stride * h];
        for (int by = 0; by < h; by += blockSize)
        {
            int bh = Math.Min(blockSize, h - by);
            for (int bx = 0; bx < w; bx += blockSize)
            {
                int bw = Math.Min(blockSize, w - bx);
                long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int yy = 0; yy < bh; yy++)
                {
                    int rowStart = (by + yy) * stride + bx * 4;
                    for (int xx = 0; xx < bw; xx++)
                    {
                        int idx = rowStart + xx * 4;
                        sumB += pixels[idx]; sumG += pixels[idx + 1]; sumR += pixels[idx + 2]; sumA += pixels[idx + 3];
                    }
                }
                int count = bw * bh;
                byte avgB = (byte)(sumB / count), avgG = (byte)(sumG / count),
                     avgR = (byte)(sumR / count), avgA = (byte)(sumA / count);
                for (int yy = 0; yy < bh; yy++)
                {
                    int rowStart = (by + yy) * stride + bx * 4;
                    for (int xx = 0; xx < bw; xx++)
                    {
                        int idx = rowStart + xx * 4;
                        outPixels[idx] = avgB; outPixels[idx + 1] = avgG; outPixels[idx + 2] = avgR; outPixels[idx + 3] = avgA;
                    }
                }
            }
        }

        var output = new WriteableBitmap(w, h, baseImage.DpiX, baseImage.DpiY, PixelFormats.Bgra32, null);
        output.WritePixels(new Int32Rect(0, 0, w, h), outPixels, stride, 0);
        output.Freeze();
        return output;
    }

    private static void DrawCallout(DrawingContext dc, CalloutAnnotation c)
    {
        var fill = new SolidColorBrush(c.Color);
        // Scaled down from the raw stroke Thickness (a bubble border reads as UI chrome, not a
        // drawn line -- the full stroke value would look disproportionately heavy) while still
        // giving the Stroke control something meaningful to do for a selected Callout.
        var border = new Pen(new SolidColorBrush(c.Color), Math.Max(1, c.Thickness * 0.3));

        DrawCalloutTail(dc, c.Bounds, c.TailTip, fill);
        dc.DrawRoundedRectangle(fill, border, c.Bounds, 8, 8);

        var typeface = new Typeface(new FontFamily(c.FontFamily), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var ft = new FormattedText(
            c.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, c.FontSize,
            Brushes.White, VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, c.Bounds.Width - 16),
            MaxTextHeight = Math.Max(1, c.Bounds.Height - 12),
            TextAlignment = TextAlignment.Center
        };
        var textOrigin = new Point(c.Bounds.X + 8, c.Bounds.Y + (c.Bounds.Height - ft.Height) / 2);
        dc.DrawText(ft, textOrigin);
    }

    /// <summary>The tail is a small triangle from two points on whichever bubble edge sits between
    /// the bubble's center and the tip, converging on the tip -- so it looks attached regardless of
    /// which side of the bubble the tip ends up on.</summary>
    private static void DrawCalloutTail(DrawingContext dc, Rect bounds, Point tip, Brush fill)
    {
        if (bounds.Contains(tip)) return; // tip inside the bubble: no meaningful tail to draw

        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        double dx = tip.X - center.X, dy = tip.Y - center.Y;
        double baseHalf = Math.Min(14, Math.Min(bounds.Width, bounds.Height) / 4);
        Point baseA, baseB;

        if (Math.Abs(dx) * bounds.Height > Math.Abs(dy) * bounds.Width)
        {
            double edgeX = dx > 0 ? bounds.Right : bounds.Left;
            double t = Math.Clamp((tip.Y - bounds.Y) / bounds.Height, 0.15, 0.85);
            double anchorY = bounds.Y + t * bounds.Height;
            baseA = new Point(edgeX, anchorY - baseHalf);
            baseB = new Point(edgeX, anchorY + baseHalf);
        }
        else
        {
            double edgeY = dy > 0 ? bounds.Bottom : bounds.Top;
            double t = Math.Clamp((tip.X - bounds.X) / bounds.Width, 0.15, 0.85);
            double anchorX = bounds.X + t * bounds.Width;
            baseA = new Point(anchorX - baseHalf, edgeY);
            baseB = new Point(anchorX + baseHalf, edgeY);
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(baseA, true, true);
            ctx.LineTo(tip, true, false);
            ctx.LineTo(baseB, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(fill, null, geo);
    }
}
