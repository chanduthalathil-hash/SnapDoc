using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Microsoft.Win32;
using SnapDoc.Editor;
using SnapDoc.Export;
using SnapDoc.Models;
using Capture = SnapDoc.Models.Capture;

namespace SnapDoc.Views;

/// <summary>
/// The annotation editor. Renders the capture and lets the user draw annotations, which are
/// stored on the Capture model (image-pixel coordinates) and re-rendered via AnnotationRenderer.
/// One editor edits one capture; Export can pull the whole workspace into a document.
/// </summary>
public partial class EditorWindow : Window
{
    private enum Tool { Select, Arrow, Rectangle, Ellipse, Line, Highlight, Text, Step, ColorPicker, Crop, Blur, Callout }

    private readonly Capture _capture;
    private Tool _tool = Tool.Select;
    private Color _color = Colors.Red;
    private double _thickness = 8.0;

    private Annotation? _current;   // annotation being drawn
    private bool _isCropMarquee;
    private Point _startPt;

    // ---- selection / move / resize / rotate ----
    private enum HandleKind { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Start, End, Rotate }
    private enum DragMode { None, Move, Resize, Rotate }

    private Annotation? _selected;
    private bool _syncingSelection; // guards LayersList<->canvas selection sync against feedback loops
    private DragMode _dragMode = DragMode.None;
    private HandleKind _dragHandle;
    private AnnotationSnapshot _dragBefore;
    private Point _dragAnchor;

    private const double HandleScreenSize = 9;   // constant ON-SCREEN handle size, DIPs -- divided
    private const double HandleHitPadding = 4;   // by CanvasZoom.ScaleX wherever used so it doesn't
                                                  // shrink/grow with zoom like everything else does.
    private const double RotateHandleGap = 28;   // DIPs above the box, same zoom-correction treatment.

    // ---- in-place text editing ----
    private TextAnnotation? _editingText;
    private bool _editingIsNew;

    // "Last used" text formatting, mirroring how _color/_thickness already work as defaults for
    // the next drawn shape -- new text picks these up instead of always resetting to class defaults.
    private string _defaultFontFamily = "Segoe UI";
    private double _defaultFontSize = 24;
    private FontWeight _defaultFontWeight = FontWeights.Normal;
    private FontStyle _defaultFontStyle = FontStyles.Normal;
    private bool _defaultUnderline = false;
    private TextAlignment _defaultAlignment = TextAlignment.Left;

    // ---- text properties panel ----
    private bool _suppressPropertyEvents; // true while populating controls from a newly-selected object
    private bool _propertiesActive;
    private bool _wasAnnotationsTabActive;
    private string? _propContentBefore;
    private double _sliderBeforeValue;

    // ---- context menu / clipboard-lite ----
    private Annotation? _copiedAnnotation; // in-app only, deliberately not the OS clipboard -- see
                                            // the "Copy" toolbar button, which already means something
                                            // else (flatten the whole image to the OS clipboard).

    private readonly Stack<EditAction> _undoStack = new();
    private readonly Stack<EditAction> _redoStack = new();

    public EditorWindow(Capture capture)
    {
        InitializeComponent();
        _capture = capture;
        BaseImage.Source = capture.Image;
        CanvasHost.Width = capture.PixelWidth;
        CanvasHost.Height = capture.PixelHeight;
        AnnotationLayer.Width = capture.PixelWidth;
        AnnotationLayer.Height = capture.PixelHeight;
        SelectionLayer.Width = capture.PixelWidth;
        SelectionLayer.Height = capture.PixelHeight;

        TitleBox.Text = capture.Title;
        CaptionBox.Text = capture.Caption;
        TitleBox.TextChanged += (_, _) => _capture.Title = TitleBox.Text;
        CaptionBox.TextChanged += (_, _) => _capture.Caption = CaptionBox.Text;

        CreatedText.Text = capture.CreatedAt.ToString("g");
        ImageSizeText.Text = $"{capture.PixelWidth} x {capture.PixelHeight}";
        CanvasSizeText.Text = $"{capture.PixelWidth} x {capture.PixelHeight} px";
        FileSizeText.Text = GetFileSizeText(capture);

        LayersList.ItemsSource = _capture.Annotations;
        SetColor(_color);
        Loaded += (_, _) =>
        {
            ZoomToFit();
            // Defensive: this window is often created right after SnapDoc hides/re-shows its own
            // windows to keep them out of the screenshot, and on some systems (RDP/VMs, low-tier
            // rendering) a window created mid that shuffle can come up with a blank first frame
            // until something forces a repaint. Costs nothing on systems where it was never needed.
            BaseImage.InvalidateVisual();
            CanvasHost.InvalidateVisual();
        };

        RefreshCanvas();
    }

    // Persist annotations to the on-disk PNG (written at capture time) when the editor closes,
    // so the saved file reflects whatever the user drew, not just the raw screenshot.
    protected override void OnClosed(EventArgs e)
    {
        CommitTextEdit(); // otherwise text typed but not yet clicked away from would be silently lost
        try { App.Workspace.SavePng(_capture); } catch { /* non-fatal */ }
        base.OnClosed(e);
    }

    private void BackToLibrary_Click(object sender, RoutedEventArgs e) => Close();
    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    private static string GetFileSizeText(Capture cap)
    {
        if (string.IsNullOrEmpty(cap.FilePath) || !System.IO.File.Exists(cap.FilePath)) return "Not saved yet";
        double kb = new System.IO.FileInfo(cap.FilePath).Length / 1024.0;
        return kb >= 1024 ? $"{kb / 1024:0.0} MB" : $"{kb:0} KB";
    }

    // ---- tabs ----

    private void TabCanvas_Click(object sender, RoutedEventArgs e)
    {
        CanvasTab.Visibility = Visibility.Visible;
        AnnotationsTab.Visibility = Visibility.Collapsed;
    }

    private void TabAnnotations_Click(object sender, RoutedEventArgs e)
    {
        CanvasTab.Visibility = Visibility.Collapsed;
        AnnotationsTab.Visibility = Visibility.Visible;
    }

    private void LayerVisibility_Click(object sender, RoutedEventArgs e) => RefreshCanvas();

    private void ComingSoon_Click(object sender, RoutedEventArgs e)
    {
        string name = (sender as FrameworkElement)?.Tag as string ?? "This tool";
        MessageBox.Show($"{name} is planned for a future version.", "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- zoom ----

    /// <summary>Scale the capture down (never up) so it fits the visible canvas area on open --
    /// full-resolution captures are usually much bigger than the editor window.</summary>
    private void ZoomToFit()
    {
        var availableW = CanvasScroller.ActualWidth - 48;  // minus the canvas Border padding
        var availableH = CanvasScroller.ActualHeight - 48;
        if (availableW <= 0 || availableH <= 0 || _capture.PixelWidth == 0 || _capture.PixelHeight == 0) return;

        var fit = Math.Min(availableW / _capture.PixelWidth, availableH / _capture.PixelHeight);
        SetZoom(Math.Min(1.0, fit));
    }

    private void SetZoom(double scale)
    {
        scale = Math.Clamp(scale, 0.1, 3.0);
        CanvasZoom.ScaleX = CanvasZoom.ScaleY = scale;
        ZoomPercentText.Text = $"{scale * 100:0}%";
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e) => ZoomToFit();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(CanvasZoom.ScaleX + 0.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(CanvasZoom.ScaleX - 0.1);

    // ---- drawing ----

    /// <summary>Re-render all annotations onto the overlay canvas via a DrawingVisual host.</summary>
    private void Redraw()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            foreach (var a in _capture.Annotations)
            {
                // The live InlineTextEditor IS the visual for whatever's being edited right now --
                // drawing the model's own (stale, uncommitted) text underneath it would double it up.
                if (a == _editingText) continue;
                AnnotationRenderer.Draw(dc, a, _capture.Image);
            }

        var host = new VisualHost(dv);
        AnnotationLayer.Children.Clear();
        AnnotationLayer.Children.Add(host);
    }

    /// <summary>Redraw the annotations AND the selection adorner together, so no call site can
    /// update annotations (add/move/resize/recolor/delete/undo) without the selection chrome
    /// staying in sync with it. Use this instead of Redraw() everywhere except inside Redraw() itself.</summary>
    private void RefreshCanvas()
    {
        // A redo can re-apply a delete of the currently-selected annotation (or an undo could
        // revert its creation) -- guard against drawing an adorner around something no longer
        // actually in the capture, and against leaving the text-properties panel stuck showing a
        // now-gone object's stale values.
        if (_selected != null && !_capture.Annotations.Contains(_selected))
        {
            _selected = null;
            _syncingSelection = true; LayersList.SelectedItem = null; _syncingSelection = false;
            DeleteBtn.IsEnabled = false;
            ShowPropertiesFor(null);
        }

        // Typing new text or changing font/size doesn't otherwise touch Bounds at all -- without
        // this, a box created for a short line, later edited to something longer, keeps its
        // original (now too-small) hit-test area while the visibly rendered text extends well
        // beyond it, so clicking on clearly-visible text can silently miss. Skipped mid-drag so
        // this doesn't fight a resize handle's own Bounds/FontSize changes while they're happening.
        if (_selected is TextAnnotation ta && _dragMode == DragMode.None)
            UpdateTextBounds(ta);

        Redraw();
        DrawSelectionAdorner();
    }

    private static void UpdateTextBounds(TextAnnotation t)
    {
        var size = AnnotationRenderer.MeasureText(t);
        double w = Math.Max(20, size.Width), h = Math.Max(t.FontSize, size.Height);
        if (Math.Abs(t.Bounds.Width - w) > 0.5 || Math.Abs(t.Bounds.Height - h) > 0.5)
            t.Bounds = new Rect(t.Bounds.X, t.Bounds.Y, w, h);
    }

    // ---- selection: effective bounds + hit-testing ----
    // Annotation.Bounds is only authoritative for Rectangle/Ellipse/Text/Step -- Arrow/Line never
    // write to it (they mutate Start/End directly) and Ink's real footprint is its point cloud, so
    // hit-testing and selection handles always go through this instead of touching .Bounds directly.

    private static Rect GetEffectiveBounds(Annotation a) => a switch
    {
        ArrowAnnotation ar => new Rect(ar.Start, ar.End),
        LineAnnotation ln => new Rect(ln.Start, ln.End),
        InkAnnotation ink when ink.Points.Count > 0 => ComputeInkBounds(ink.Points),
        InkAnnotation => Rect.Empty,
        _ => a.Bounds
    };

    private static Rect ComputeInkBounds(PointCollection points)
    {
        double minX = points[0].X, minY = points[0].Y, maxX = minX, maxY = minY;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double lenSq = ab.LengthSquared;
        if (lenSq < 1e-6) return (p - a).Length;
        double t = Math.Clamp(((p - a).X * ab.X + (p - a).Y * ab.Y) / lenSq, 0, 1);
        var proj = a + ab * t;
        return (p - proj).Length;
    }

    private static double DistanceToPolyline(Point p, PointCollection points)
    {
        if (points.Count == 0) return double.MaxValue;
        if (points.Count == 1) return (p - points[0]).Length;
        double min = double.MaxValue;
        for (int i = 1; i < points.Count; i++)
            min = Math.Min(min, DistanceToSegment(p, points[i - 1], points[i]));
        return min;
    }

    // Hit-testing always matches the shape's own actual rendered geometry, never a generic bbox
    // proxy for it -- segment distance for Arrow/Line/Ink (a near-diagonal line's bbox is mostly
    // empty space, which would select it from clicks nowhere near the actual stroke), true
    // circle/ellipse distance for Step/Ellipse (their bbox corners sit outside the visible round
    // shape), and bbox-contains only for the types whose bbox genuinely IS their visible footprint
    // (Rectangle, Text). This runs in un-rotated LOCAL space throughout (see ToLocal below).
    //
    // Instance method (not static) so pad can be zoom-corrected: resize handles already stay a
    // constant ~9 screen px regardless of zoom (HandleScreenSize / CanvasZoom.ScaleX), but this
    // pad was a fixed 4 IMAGE px -- at low zoom (e.g. 26%) that's under 1 screen px of forgiveness,
    // while a small marker's 8 correctly-zoom-corrected handles visually dominate the tiny space
    // around it. Net effect: body clicks became needle-precise while handle clicks stayed easy,
    // which reads exactly like "only the handles are clickable." Matching the same treatment here
    // fixes that for every shape, not just Step.
    private bool HitTest(Annotation a, Point p)
    {
        double pad = 6.0 / CanvasZoom.ScaleX;
        switch (a)
        {
            case ArrowAnnotation ar: return DistanceToSegment(p, ar.Start, ar.End) <= Math.Max(pad, ar.Thickness / 2 + pad);
            case LineAnnotation ln: return DistanceToSegment(p, ln.Start, ln.End) <= Math.Max(pad, ln.Thickness / 2 + pad);
            case InkAnnotation ink:
                // DrawInk renders a highlighter 3x thicker than ink.Thickness -- match that here so
                // clicking anywhere on the visible fat stroke actually hits it, not just its centerline.
                double inkStroke = ink.Highlighter ? ink.Thickness * 3 : ink.Thickness;
                return DistanceToPolyline(p, ink.Points) <= Math.Max(pad, inkStroke / 2 + pad);
            case StepAnnotation step:
            {
                var localP = ToLocal(p, step.Bounds, step.Rotation);
                var center = new Point(step.Bounds.X + step.Radius, step.Bounds.Y + step.Radius);
                double dist = (localP - center).Length;
                bool hit = dist <= step.Radius + pad;
                return hit;
            }
            case EllipseAnnotation:
            {
                var b = GetEffectiveBounds(a);
                var localP = ToLocal(p, b, a.Rotation);
                double rx = b.Width / 2 + pad, ry = b.Height / 2 + pad;
                if (rx < 1 || ry < 1) return false;
                var c = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
                double nx = (localP.X - c.X) / rx, ny = (localP.Y - c.Y) / ry;
                return nx * nx + ny * ny <= 1.0;
            }
            case TextAnnotation text:
            {
                var tb = text.Bounds;
                var localP = ToLocal(p, tb, text.Rotation);
                var inflated = tb; inflated.Inflate(pad, pad);
                bool hit = inflated.Contains(localP);
                return hit;
            }
            default:
                var bb = GetEffectiveBounds(a);
                // Rotation is never baked into Bounds (see RotatePoint/ToLocal doc below) -- undo
                // it on the click point instead of on the shape, so an unrotated bbox test still works.
                var lp = ToLocal(p, bb, a.Rotation);
                bb.Inflate(pad, pad);
                return bb.Contains(lp);
        }
    }

    private Annotation? HitTestTopmost(Point p) =>
        _capture.Annotations.Reverse().FirstOrDefault(a => a.IsVisible && HitTest(a, p));

    // Bounds/Start/End/Points are always stored in the shape's own LOCAL, unrotated frame; Rotation
    // is applied as a pure transform on top at render/hit-test time (AnnotationRenderer.DrawText
    // pushes a RotateTransform, never rewrites Bounds). RotatePoint/ToLocal are the two ends of
    // that: RotatePoint maps a local point INTO rotated/global space (used for handle positions,
    // which must be drawn where the rotated shape actually appears); ToLocal maps a global point
    // (e.g. a mouse click) back OUT of rotation, so hit-testing/resize math can stay simple
    // axis-aligned arithmetic in local space, matching how it already worked before rotation existed.
    private static Point RotatePoint(Point p, Point center, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double dx = p.X - center.X, dy = p.Y - center.Y;
        return new Point(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    // bounds/rotationDegrees are passed explicitly (not read live off an Annotation) so callers
    // mid-drag can pin the rotation center to the shape's PRE-drag bounds for the whole gesture --
    // reading it live off a Bounds that's being mutated every MouseMove would make the center (and
    // therefore the whole inverse-rotation) drift frame to frame. See ApplyResize.
    private static Point ToLocal(Point p, Rect bounds, double rotationDegrees)
    {
        if (rotationDegrees == 0) return p;
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        return RotatePoint(p, center, -rotationDegrees);
    }

    // ---- selection: handles ----

    // Instance method (not static) because the rotation handle's gap needs CanvasZoom.ScaleX to
    // stay a constant on-screen distance, same reasoning as HandleScreenSize/HandleHitPadding.
    private IEnumerable<(Point Pt, HandleKind Kind)> HandlePointsFor(Annotation a)
    {
        if (a is ArrowAnnotation arrow)
        {
            yield return (arrow.Start, HandleKind.Start);
            yield return (arrow.End, HandleKind.End);
            yield break;
        }
        if (a is LineAnnotation line)
        {
            yield return (line.Start, HandleKind.Start);
            yield return (line.End, HandleKind.End);
            yield break;
        }

        var b = GetEffectiveBounds(a);
        var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        var raw = new (Point Pt, HandleKind Kind)[]
        {
            (new Point(b.Left, b.Top), HandleKind.TopLeft),
            (new Point(b.Left + b.Width / 2, b.Top), HandleKind.Top),
            (new Point(b.Right, b.Top), HandleKind.TopRight),
            (new Point(b.Right, b.Top + b.Height / 2), HandleKind.Right),
            (new Point(b.Right, b.Bottom), HandleKind.BottomRight),
            (new Point(b.Left + b.Width / 2, b.Bottom), HandleKind.Bottom),
            (new Point(b.Left, b.Bottom), HandleKind.BottomLeft),
            (new Point(b.Left, b.Top + b.Height / 2), HandleKind.Left),
        };
        // Box handles must rotate WITH the shape -- otherwise they'd visually detach from a
        // rotated shape's actual corners even though they still work correctly underneath.
        foreach (var (pt, kind) in raw)
            yield return a.Rotation == 0 ? (pt, kind) : (RotatePoint(pt, center, a.Rotation), kind);

        if (a is TextAnnotation)
        {
            double gap = RotateHandleGap / CanvasZoom.ScaleX;
            var above = new Point(center.X, b.Top - gap);
            yield return (a.Rotation == 0 ? above : RotatePoint(above, center, a.Rotation), HandleKind.Rotate);
        }
    }

    private HandleKind? HitTestHandle(Point p)
    {
        if (_selected == null) return null;
        double r = (HandleScreenSize / 2 + HandleHitPadding) / CanvasZoom.ScaleX;
        foreach (var (pt, kind) in HandlePointsFor(_selected))
            if ((p - pt).Length <= r) return kind;
        return null;
    }

    private static Cursor CursorFor(HandleKind kind) => kind switch
    {
        HandleKind.TopLeft or HandleKind.BottomRight => Cursors.SizeNWSE,
        HandleKind.TopRight or HandleKind.BottomLeft => Cursors.SizeNESW,
        HandleKind.Top or HandleKind.Bottom => Cursors.SizeNS,
        HandleKind.Left or HandleKind.Right => Cursors.SizeWE,
        HandleKind.Rotate => Cursors.Hand,
        _ => Cursors.Cross // Start/End endpoint handles
    };

    /// <summary>Draws the selection outline + handles on SelectionLayer only -- never touches
    /// AnnotationLayer, so this can never affect what gets exported/flattened.</summary>
    private void DrawSelectionAdorner()
    {
        SelectionLayer.Children.Clear();
        // While actively editing text, InlineTextEditor itself is the visual affordance --
        // drawing handles/outline around it too would be redundant clutter.
        if (_selected == null || !_selected.IsVisible || _selected == _editingText) return;

        try
        {
        double hs = HandleScreenSize / CanvasZoom.ScaleX; // constant on-screen size at any zoom
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var accent = Color.FromRgb(0x2D, 0x7D, 0xD2);
            // 2 screen px, up from 1.2 -- at low zoom a small marker's outline was thin enough to
            // be easy to miss entirely, even though selection was working correctly underneath it.
            var handlePen = new Pen(new SolidColorBrush(accent), 2.0 / CanvasZoom.ScaleX);

            if (_selected is ArrowAnnotation or LineAnnotation)
            {
                foreach (var (pt, _) in HandlePointsFor(_selected))
                    dc.DrawEllipse(Brushes.White, handlePen, pt, hs / 2, hs / 2);
            }
            else
            {
                var b = GetEffectiveBounds(_selected);
                var outlinePen = new Pen(new SolidColorBrush(accent), 2.0 / CanvasZoom.ScaleX)
                { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };

                // PushTransform is simpler and less error-prone than manually rotating 4 corners
                // to build a rotated outline path -- DrawRectangle just draws into the rotated space.
                if (_selected.Rotation != 0)
                {
                    var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
                    dc.PushTransform(new RotateTransform(_selected.Rotation, center.X, center.Y));
                    DrawOutlineFor(dc, _selected, b, outlinePen);
                    dc.Pop();
                }
                else
                {
                    DrawOutlineFor(dc, _selected, b, outlinePen);
                }

                foreach (var (pt, kind) in HandlePointsFor(_selected))
                {
                    if (kind == HandleKind.Rotate)
                    {
                        // A connecting "stalk" from the box's (rotated) top-center to the handle,
                        // so it reads as attached to the shape rather than floating independently.
                        var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
                        var topCenter = new Point(center.X, b.Top);
                        if (_selected.Rotation != 0) topCenter = RotatePoint(topCenter, center, _selected.Rotation);
                        dc.DrawLine(handlePen, topCenter, pt);
                        dc.DrawEllipse(Brushes.White, handlePen, pt, hs / 2, hs / 2);
                    }
                    else
                    {
                        dc.DrawRectangle(Brushes.White, handlePen, new Rect(pt.X - hs / 2, pt.Y - hs / 2, hs, hs));
                    }
                }
            }
        }
        SelectionLayer.Children.Add(new VisualHost(dv));
        }
        catch { /* rendering the selection adorner is cosmetic -- a failure here shouldn't crash the editor */ }
    }

    // A dashed bounding-box outline reads fine for boxy shapes, but for round ones (Step, Ellipse)
    // it visually disconnects from the actual selected shape -- especially a small Step marker at
    // low zoom, where a square drawn around a tiny circle can look like it's selecting empty space
    // next to the marker rather than the marker itself. Hug the real geometry instead.
    private static void DrawOutlineFor(DrawingContext dc, Annotation a, Rect b, Pen outlinePen)
    {
        if (a is StepAnnotation or EllipseAnnotation)
        {
            var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            dc.DrawEllipse(null, outlinePen, center, b.Width / 2, b.Height / 2);
        }
        else
        {
            dc.DrawRectangle(null, outlinePen, b);
        }
    }

    private void UpdateSelectCursor(Point p)
    {
        var handle = HitTestHandle(p);
        if (handle != null) { AnnotationLayer.Cursor = CursorFor(handle.Value); return; }
        AnnotationLayer.Cursor = HitTestTopmost(p) != null ? Cursors.SizeAll : Cursors.Arrow;
    }

    private void SetSelected(Annotation? a)
    {
        _selected = a;
        _syncingSelection = true;
        LayersList.SelectedItem = a;
        _syncingSelection = false;
        DeleteBtn.IsEnabled = a != null;
        ShowPropertiesFor(a);
        SyncStrokeControlForSelection(a);
        DrawSelectionAdorner();
    }

    // Step markers don't render Thickness at all (DrawStep hardcodes its own border), so the
    // Stroke control would silently do nothing useful for one -- relabel it "Size:" and repurpose
    // it to directly control Radius instead whenever a Step is selected, reusing the existing
    // dropdown/slider rather than adding a whole separate control just for one annotation type.
    private void SyncStrokeControlForSelection(Annotation? a)
    {
        _suppressPropertyEvents = true;
        try
        {
            if (a is StepAnnotation step)
            {
                StrokeLabelText.Text = "Size:";
                StrokeWidthSlider.Value = step.Radius;
                var match = StrokeWidthCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Content as string == step.Radius.ToString("0"));
                StrokeWidthCombo.SelectedItem = match; // null (no matching preset) just clears selection, which is fine
            }
            else if (a is BlurAnnotation blur)
            {
                // Blur doesn't render a stroke at all -- like Step before this same treatment, the
                // Stroke control would otherwise silently do nothing for a selected Blur. Repurpose
                // it to control pixelation chunkiness instead.
                StrokeLabelText.Text = "Chunk:";
                StrokeWidthSlider.Value = blur.BlockSize;
                var match = StrokeWidthCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Content as string == blur.BlockSize.ToString("0"));
                StrokeWidthCombo.SelectedItem = match;
            }
            else
            {
                StrokeLabelText.Text = "Stroke:";
            }
        }
        finally { _suppressPropertyEvents = false; }
    }

    private void LayersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        SetSelected(LayersList.SelectedItem as Annotation);
    }

    // ---- in-place text editing ----

    private void BeginTextEdit(TextAnnotation t, bool isNew)
    {
        CommitTextEdit(); // finish whatever (if anything) was already being edited first
        _editingText = t;
        _editingIsNew = isNew;

        InlineTextEditor.FontFamily = new FontFamily(t.FontFamily);
        InlineTextEditor.FontSize = t.FontSize;
        InlineTextEditor.FontWeight = t.FontWeight;
        InlineTextEditor.FontStyle = t.FontStyle;
        InlineTextEditor.TextAlignment = t.Alignment;
        InlineTextEditor.Foreground = new SolidColorBrush(t.Color);
        InlineTextEditor.Text = t.Text;
        InlineTextEditor.Margin = new Thickness(t.Bounds.X, t.Bounds.Y, 0, 0);
        InlineTextEditor.Width = Math.Max(80, t.Bounds.Width);
        InlineTextEditor.Height = Math.Max(t.FontSize + 14, t.Bounds.Height);
        InlineTextEditor.Visibility = Visibility.Visible;

        RefreshCanvas();
        InlineTextEditor.Focus();
        InlineTextEditor.SelectAll();
    }

    /// <summary>Ctrl+Enter or clicking outside (LostKeyboardFocus) both call this. A brand-new,
    /// still-empty text box is discarded with no undo entry at all -- mirrors the old dialog-based
    /// AddText's "empty input does nothing" guard, just without a dialog to cancel out of.</summary>
    private void CommitTextEdit()
    {
        if (_editingText == null) return;
        var t = _editingText;
        bool isNew = _editingIsNew;
        _editingText = null;
        _editingIsNew = false;
        InlineTextEditor.Visibility = Visibility.Collapsed;

        string newText = InlineTextEditor.Text;
        if (isNew)
        {
            if (!string.IsNullOrWhiteSpace(newText))
            {
                t.Text = newText;
                PushAnnotation(t);
                SetSelected(t);
            }
        }
        else
        {
            if (newText != t.Text) Do(new TextEditAction(t, t.Text, newText));
            SetSelected(t); // so the just-edited text's selection/Bounds stay in sync, same as new text
        }
        RefreshCanvas();
    }

    /// <summary>Escape: abandon the edit. A new text box is simply dropped (it was never added to
    /// the collection); an existing one keeps whatever .Text it already had, untouched.</summary>
    private void CancelTextEdit()
    {
        if (_editingText == null) return;
        _editingText = null;
        _editingIsNew = false;
        InlineTextEditor.Visibility = Visibility.Collapsed;
        RefreshCanvas();
    }

    private void InlineTextEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTextEdit();

    private void InlineTextEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { CommitTextEdit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelTextEdit(); e.Handled = true; }
    }

    private void BeginSelectDrag(Point p)
    {
        var handle = HitTestHandle(p);
        if (handle != null && _selected != null)
        {
            // Locked: stay selected and do nothing, rather than falling through to a body hit-test
            // that can miss (the rotate handle sits outside the shape's own bounds) and wrongly
            // deselect on what's clearly still a click "on" the selected object's own chrome.
            if (_selected.IsLocked) return;
            _dragMode = handle.Value == HandleKind.Rotate ? DragMode.Rotate : DragMode.Resize;
            _dragHandle = handle.Value;
            _dragBefore = Snapshot(_selected);
            _dragAnchor = p;
            AnnotationLayer.CaptureMouse();
            return;
        }

        var hit = HitTestTopmost(p);
        if (hit != null)
        {
            SetSelected(hit);
            if (!hit.IsLocked) // still selectable while locked, just not draggable
            {
                _dragMode = DragMode.Move;
                _dragBefore = Snapshot(_selected!);
                _dragAnchor = p;
                AnnotationLayer.CaptureMouse();
            }
            return;
        }

        SetSelected(null);
    }

    private void ApplySelectDrag(Point p)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_dragMode)
        {
            case DragMode.Move:
                var delta = p - _dragAnchor;
                _dragAnchor = p;
                OffsetAnnotation(_selected!, delta);
                break;
            case DragMode.Rotate:
                ApplyRotate(_selected!, p, shift);
                break;
            default:
                ApplyResize(_selected!, _dragHandle, p, shift);
                break;
        }
    }

    // Angle from the shape's (pre-drag, stable-for-the-whole-gesture) center to the live cursor --
    // a global-space calculation, unlike resize below, since "which way is the handle pointing"
    // is inherently about the shape's orientation on screen, not its own local coordinate frame.
    private void ApplyRotate(Annotation a, Point p, bool shift)
    {
        var b = _dragBefore.Bounds;
        var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        double angle = Math.Atan2(p.Y - center.Y, p.X - center.X) * 180.0 / Math.PI + 90.0; // +90: "up" = 0 degrees
        if (shift) angle = Math.Round(angle / 15.0) * 15.0; // Shift snaps to 15-degree steps
        a.Rotation = angle;
    }

    private void ApplyResize(Annotation a, HandleKind handle, Point p, bool shift)
    {
        if (a is ArrowAnnotation arrow)
        {
            var fixedEnd = handle == HandleKind.Start ? arrow.End : arrow.Start;
            var moved = shift ? SnapAngle(fixedEnd, p) : p;
            if (handle == HandleKind.Start) arrow.Start = moved; else arrow.End = moved;
            return;
        }
        if (a is LineAnnotation line)
        {
            var fixedEnd = handle == HandleKind.Start ? line.End : line.Start;
            var moved = shift ? SnapAngle(fixedEnd, p) : p;
            if (handle == HandleKind.Start) line.Start = moved; else line.End = moved;
            return;
        }
        if (a is StepAnnotation step)
        {
            // A circle has no "corners" to anchor a resize against the way a rectangle does. The
            // old approach ran Step through the same rectangular delta/ResizeBounds math as
            // Rectangle/Ellipse, then forced the result square using only the WIDTH-derived scale
            // -- so dragging any handle in a non-diagonal direction (nearly always, in practice)
            // silently discarded the vertical component of the drag, leaving the circle's actual
            // edge somewhere other than where the cursor was. That's the "resize works but the
            // shape ends up in the wrong place" bug. Fix: keep the center fixed (from drag-start)
            // and let the radius track the cursor's straight-line distance from it directly, so the
            // circle's edge always passes through wherever the cursor actually is, from any handle.
            var stepBounds = _dragBefore.Bounds;
            var stepCenter = new Point(stepBounds.X + stepBounds.Width / 2, stepBounds.Y + stepBounds.Height / 2);
            var localCursor = ToLocal(p, stepBounds, a.Rotation);
            double newRadius = Math.Max(6, (localCursor - stepCenter).Length);
            step.Radius = newRadius;
            step.Bounds = new Rect(stepCenter.X - newRadius, stepCenter.Y - newRadius, newRadius * 2, newRadius * 2);
            return;
        }

        var originalBounds = _dragBefore.Bounds;
        // Undo the shape's rotation on both points before computing a delta, so resize math stays
        // simple axis-aligned arithmetic in the shape's own local frame -- pinned to _dragBefore's
        // bounds (not a live GetEffectiveBounds) for the rotation center, same reasoning as
        // ToLocal's doc comment: using the live, mutating Bounds here would make the center (and
        // the whole inverse-rotation) drift frame to frame as the resize itself changes Bounds.
        var localP = ToLocal(p, originalBounds, a.Rotation);
        var localAnchor = ToLocal(_dragAnchor, originalBounds, a.Rotation);
        var delta = localP - localAnchor;
        if (shift) delta = ConstrainAspect(originalBounds, handle, delta);
        var newBounds = ResizeBounds(originalBounds, handle, delta);

        switch (a)
        {
            case InkAnnotation ink:
                ScaleInkPoints(ink, originalBounds, newBounds);
                a.Bounds = newBounds;
                break;
            case TextAnnotation text:
                // Text.Bounds' size isn't used for rendering (only .X/.Y as the draw origin) --
                // map the drag onto FontSize instead, proportional to the height change.
                if (originalBounds.Height > 1)
                    text.FontSize = Math.Max(6, _dragBefore.FontSize * (newBounds.Height / originalBounds.Height));
                a.Bounds = newBounds;
                break;
            case CalloutAnnotation callout:
                // TailTip isn't independently draggable (see the model's doc comment) -- keep it
                // attached at the same fractional position relative to the bubble as before the
                // resize, rather than leaving it pointing at a now-stale absolute location.
                if (originalBounds.Width > 1 && originalBounds.Height > 1)
                {
                    double fx = (_dragBefore.TailTip.X - originalBounds.X) / originalBounds.Width;
                    double fy = (_dragBefore.TailTip.Y - originalBounds.Y) / originalBounds.Height;
                    callout.TailTip = new Point(newBounds.X + fx * newBounds.Width, newBounds.Y + fy * newBounds.Height);
                }
                a.Bounds = newBounds;
                break;
            // StepAnnotation is handled above, before this shared rectangular delta/ResizeBounds
            // math -- a circle needs different resize semantics (see the comment up there).
            default: // Rectangle, Ellipse, Blur
                a.Bounds = newBounds;
                break;
        }
    }

    private static Rect ResizeBounds(Rect original, HandleKind handle, Vector delta)
    {
        double x = original.X, y = original.Y, w = original.Width, h = original.Height;
        if (handle is HandleKind.Left or HandleKind.TopLeft or HandleKind.BottomLeft) { x += delta.X; w -= delta.X; }
        if (handle is HandleKind.Right or HandleKind.TopRight or HandleKind.BottomRight) { w += delta.X; }
        if (handle is HandleKind.Top or HandleKind.TopLeft or HandleKind.TopRight) { y += delta.Y; h -= delta.Y; }
        if (handle is HandleKind.Bottom or HandleKind.BottomLeft or HandleKind.BottomRight) { h += delta.Y; }
        if (w < 0) { x += w; w = -w; }
        if (h < 0) { y += h; h = -h; }
        return new Rect(x, y, w, h);
    }

    // Aspect lock only makes sense on corner handles -- edge-midpoint handles have just one free
    // axis, so Shift is a deliberate no-op there rather than a bug.
    private static Vector ConstrainAspect(Rect original, HandleKind handle, Vector delta)
    {
        if (handle is not (HandleKind.TopLeft or HandleKind.TopRight or HandleKind.BottomLeft or HandleKind.BottomRight))
            return delta;
        if (original.Width < 1 || original.Height < 1) return delta;

        var (sx, sy) = handle switch
        {
            HandleKind.TopLeft => (-1, -1),
            HandleKind.TopRight => (1, -1),
            HandleKind.BottomLeft => (-1, 1),
            _ => (1, 1) // BottomRight
        };
        double aspect = original.Width / original.Height;
        double growX = delta.X * sx, growY = delta.Y * sy; // growth in each handle's own "outward" sign
        double driven = Math.Abs(growX) >= Math.Abs(growY) * aspect ? growX : growY * aspect;
        return new Vector(driven * sx, (driven / aspect) * sy);
    }

    private static void ScaleInkPoints(InkAnnotation ink, Rect oldBounds, Rect newBounds)
    {
        double sx = oldBounds.Width > 1e-6 ? newBounds.Width / oldBounds.Width : 1;
        double sy = oldBounds.Height > 1e-6 ? newBounds.Height / oldBounds.Height : 1;
        var scaled = new PointCollection();
        foreach (var p in ink.Points)
            scaled.Add(new Point(newBounds.X + (p.X - oldBounds.X) * sx, newBounds.Y + (p.Y - oldBounds.Y) * sy));
        ink.Points = scaled;
    }

    // Snaps to the nearest 45-degree increment (covers 0/45/90/135/... and their mirrors), keeping
    // distance from the fixed end unchanged so the drag still feels like it's tracking the cursor.
    private static Point SnapAngle(Point fixedEnd, Point dragged)
    {
        var v = dragged - fixedEnd;
        double len = v.Length;
        if (len < 1e-6) return dragged;
        double angle = Math.Atan2(v.Y, v.X);
        double step = Math.PI / 4;
        double snapped = Math.Round(angle / step) * step;
        return new Point(fixedEnd.X + len * Math.Cos(snapped), fixedEnd.Y + len * Math.Sin(snapped));
    }

    private void Layer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Any new click on the canvas finishes a pending text edit first -- SetActiveTool only
        // catches tool-SWITCH transitions; clicking elsewhere on the canvas without changing tools
        // (e.g. double-click text to edit it, then click a different shape while still on Select)
        // would otherwise leave _editingText active while also starting an unrelated selection/drag.
        CommitTextEdit();

        _startPt = e.GetPosition(AnnotationLayer);

        if (_tool == Tool.Select)
        {
            if (e.ClickCount == 2 && HitTestTopmost(_startPt) is TextAnnotation text)
            {
                BeginTextEdit(text, isNew: false);
                e.Handled = true; // see the Tool.Text case below for why this matters
                return;
            }
            if (e.ClickCount == 2 && HitTestTopmost(_startPt) is CalloutAnnotation callout)
            {
                EditCalloutText(callout);
                e.Handled = true;
                return;
            }
            BeginSelectDrag(_startPt);
            return;
        }

        switch (_tool)
        {
            case Tool.Arrow:
                _current = new ArrowAnnotation { Color = _color, Thickness = _thickness, Start = _startPt, End = _startPt };
                break;
            case Tool.Line:
                _current = new LineAnnotation { Color = _color, Thickness = _thickness, Start = _startPt, End = _startPt };
                break;
            case Tool.Rectangle:
                _current = new RectangleAnnotation { Color = _color, Thickness = _thickness, Bounds = new Rect(_startPt, _startPt) };
                break;
            case Tool.Ellipse:
                _current = new EllipseAnnotation { Color = _color, Thickness = _thickness, Bounds = new Rect(_startPt, _startPt) };
                break;
            case Tool.Highlight:
                _current = new InkAnnotation { Color = _color, Thickness = _thickness, Highlighter = true, Points = new PointCollection { _startPt } };
                break;
            case Tool.Text:
                BeginTextEdit(new TextAnnotation
                {
                    Color = _color, Text = "",
                    FontFamily = _defaultFontFamily, FontSize = _defaultFontSize,
                    FontWeight = _defaultFontWeight, FontStyle = _defaultFontStyle,
                    Underline = _defaultUnderline, Alignment = _defaultAlignment,
                    Bounds = new Rect(_startPt.X, _startPt.Y, 160, _defaultFontSize + 14)
                }, isNew: true);
                // Without this, WPF's own default mouse-down focus handling runs right after this
                // handler returns and steals keyboard focus back from InlineTextEditor -- confirmed
                // by the diagnostic log: focus was acquired, then lost again within ~4ms on every
                // attempt, committing the (still-empty) box before the user could type anything.
                e.Handled = true;
                return;
            case Tool.Step:
                AddStep(_startPt);
                return;
            case Tool.ColorPicker:
                PickColor(_startPt);
                return;
            case Tool.Crop:
                _current = new RectangleAnnotation { Color = Colors.DeepSkyBlue, Thickness = 1.5, Bounds = new Rect(_startPt, _startPt) };
                _isCropMarquee = true;
                break;
            case Tool.Blur:
                // Click-drag like Rectangle -- BlockSize is kept in sync with the drag size live,
                // see the BlurAnnotation case in Layer_MouseMove.
                _current = new BlurAnnotation { Color = _color, Bounds = new Rect(_startPt, _startPt) };
                break;
            case Tool.Callout:
                AddCallout(_startPt);
                return;
        }

        if (_current != null)
        {
            if (_isCropMarquee) _capture.Annotations.Add(_current); // preview only, not undo-able
            else PushAnnotation(_current);
        }
        AnnotationLayer.CaptureMouse();
    }

    // Right-click: select whatever's under the cursor (if anything) and offer a context menu.
    // Implemented generically for any annotation type, not just Text -- Duplicate/Copy/Reorder/
    // Delete are equally useful on a Rectangle or Arrow, and the asymmetry of "right-click only
    // works on Text" would be a worse surprise than the small extra generality costs.
    private void Layer_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(AnnotationLayer);
        var hit = _tool == Tool.Select ? HitTestTopmost(p) : null;
        if (hit != null) SetSelected(hit);

        var menu = new ContextMenu();
        if (hit is TextAnnotation text)
        {
            var editItem = new MenuItem { Header = "Edit Text" };
            editItem.Click += (_, _) => BeginTextEdit(text, isNew: false);
            menu.Items.Add(editItem);
        }
        if (hit != null)
        {
            var dup = new MenuItem { Header = "Duplicate (Ctrl+D)" };
            dup.Click += (_, _) => DuplicateSelected();
            menu.Items.Add(dup);

            var copy = new MenuItem { Header = "Copy (Ctrl+C)" };
            copy.Click += (_, _) => _copiedAnnotation = hit;
            menu.Items.Add(copy);

            var forward = new MenuItem { Header = "Bring Forward" };
            forward.Click += (_, _) => Reorder(hit, +1);
            menu.Items.Add(forward);

            var backward = new MenuItem { Header = "Send Backward" };
            backward.Click += (_, _) => Reorder(hit, -1);
            menu.Items.Add(backward);

            menu.Items.Add(new Separator());

            var del = new MenuItem { Header = "Delete" };
            del.Click += (_, _) => Delete_Click(sender, e);
            menu.Items.Add(del);
        }
        else if (_copiedAnnotation != null)
        {
            var paste = new MenuItem { Header = "Paste (Ctrl+V)" };
            paste.Click += (_, _) => PasteCopied();
            menu.Items.Add(paste);
        }

        if (menu.Items.Count == 0) return;
        menu.PlacementTarget = AnnotationLayer;
        menu.IsOpen = true;
    }

    private static Annotation CloneAnnotation(Annotation a)
    {
        // NOTE: add a case here whenever a new Annotation subtype is added (mirrors the same
        // maintenance note on AnnotationRenderer.Draw and HandlePointsFor).
        Annotation clone = a switch
        {
            RectangleAnnotation r => new RectangleAnnotation { Filled = r.Filled, CornerRadius = r.CornerRadius },
            EllipseAnnotation => new EllipseAnnotation(),
            ArrowAnnotation ar => new ArrowAnnotation { Start = ar.Start, End = ar.End, HeadSize = ar.HeadSize },
            LineAnnotation ln => new LineAnnotation { Start = ln.Start, End = ln.End },
            InkAnnotation ink => new InkAnnotation { Points = new PointCollection(ink.Points), Highlighter = ink.Highlighter },
            TextAnnotation t => new TextAnnotation
            {
                Text = t.Text, FontSize = t.FontSize, FontFamily = t.FontFamily, FontWeight = t.FontWeight,
                FontStyle = t.FontStyle, Underline = t.Underline, Alignment = t.Alignment, Background = t.Background,
                Shadow = t.Shadow, Outline = t.Outline
            },
            StepAnnotation s => new StepAnnotation { Number = s.Number, Radius = s.Radius },
            BlurAnnotation bl => new BlurAnnotation { BlockSize = bl.BlockSize },
            CalloutAnnotation c => new CalloutAnnotation
            {
                Text = c.Text, FontSize = c.FontSize, FontFamily = c.FontFamily, TailTip = c.TailTip
            },
            _ => throw new NotSupportedException($"CloneAnnotation has no case for {a.GetType().Name}")
        };
        clone.Color = a.Color;
        clone.Thickness = a.Thickness;
        clone.Bounds = a.Bounds;
        clone.Opacity = a.Opacity;
        clone.Rotation = a.Rotation;
        return clone;
    }

    private void DuplicateSelected()
    {
        if (_selected == null) return;
        var clone = CloneAnnotation(_selected);
        OffsetAnnotation(clone, new Vector(16, 16));
        PushAnnotation(clone);
        SetSelected(clone);
    }

    private void PasteCopied()
    {
        if (_copiedAnnotation == null) return;
        var clone = CloneAnnotation(_copiedAnnotation); // clone again each paste, so repeated
        OffsetAnnotation(clone, new Vector(16, 16));     // pastes don't all alias one object
        PushAnnotation(clone);
        SetSelected(clone);
    }

    private sealed class ReorderAction : EditAction
    {
        private readonly Annotation _a;
        private readonly int _before, _after;
        public ReorderAction(Annotation a, int before, int after) { _a = a; _before = before; _after = after; }
        public override void Apply(EditorWindow w) => Move(w, _after);
        public override void Revert(EditorWindow w) => Move(w, _before);
        private void Move(EditorWindow w, int index)
        {
            w._capture.Annotations.Remove(_a);
            w._capture.Annotations.Insert(Math.Clamp(index, 0, w._capture.Annotations.Count), _a);
        }
    }

    private void Reorder(Annotation a, int direction)
    {
        int current = _capture.Annotations.IndexOf(a);
        int target = current + direction;
        if (target < 0 || target >= _capture.Annotations.Count) return; // already at that extreme
        Do(new ReorderAction(a, current, target));
        RefreshCanvas();
    }

    private void Layer_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(AnnotationLayer);

        if (_tool == Tool.Select && e.LeftButton != MouseButtonState.Pressed)
        {
            UpdateSelectCursor(p);
            return;
        }

        if (_dragMode != DragMode.None && e.LeftButton == MouseButtonState.Pressed && _selected != null)
        {
            ApplySelectDrag(p);
            RefreshCanvas();
            return;
        }

        if (_current == null || e.LeftButton != MouseButtonState.Pressed) return;

        switch (_current)
        {
            case ArrowAnnotation arrow: arrow.End = p; break;
            case LineAnnotation line: line.End = p; break;
            case InkAnnotation ink: ink.Points.Add(p); break;
            case RectangleAnnotation or EllipseAnnotation or BlurAnnotation:
                _current.Bounds = new Rect(
                    Math.Min(p.X, _startPt.X), Math.Min(p.Y, _startPt.Y),
                    Math.Abs(p.X - _startPt.X), Math.Abs(p.Y - _startPt.Y));
                // Keep the pixelation chunky-enough-to-redact for whatever size the user is
                // currently dragging out, rather than a fixed cell size that's too fine on a big
                // region or too coarse (barely any blocks at all) on a small one.
                if (_current is BlurAnnotation blurCur)
                    blurCur.BlockSize = Math.Max(6, Math.Min(blurCur.Bounds.Width, blurCur.Bounds.Height) * 0.1);
                break;
        }
        RefreshCanvas();
    }

    private void Layer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        AnnotationLayer.ReleaseMouseCapture();

        if (_dragMode != DragMode.None && _selected != null)
        {
            var after = Snapshot(_selected);
            if (!SnapshotsEqual(after, _dragBefore)) // skip clicks that never actually moved anything
                Do(new GeometryChangeAction(_selected, _dragBefore, after));
            _dragMode = DragMode.None;
            RefreshCanvas();
            return;
        }

        if (_isCropMarquee && _current != null)
        {
            var bounds = _current.Bounds;
            _capture.Annotations.Remove(_current);
            _isCropMarquee = false;
            _current = null;
            RefreshCanvas();
            ApplyCrop(bounds);
            return;
        }

        _current = null;
        RefreshCanvas();
    }

    private void AddStep(Point at)
    {
        int n = _capture.Annotations.OfType<StepAnnotation>().Count() + 1;
        PushAnnotation(new StepAnnotation
        {
            Color = _color, Number = n,
            Bounds = new Rect(at.X - 20, at.Y - 20, 40, 40)
        });
        RefreshCanvas();
    }

    // Click-to-place, like Step: a fixed-size bubble appears above-right of the click with a tail
    // pointing back down at it, then a short InputBox prompt for the label. Reuses the simple
    // dialog pattern (not the inline-editor machinery Text uses) since a callout's label is
    // typically short and this avoids generalizing BeginTextEdit for a second annotation type.
    private void AddCallout(Point at)
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox("Callout text:", "Callout", "Callout");
        if (string.IsNullOrEmpty(input)) return;
        PushAnnotation(new CalloutAnnotation
        {
            Color = _color, Thickness = _thickness, Text = input,
            Bounds = new Rect(at.X - 20, at.Y - 80, 160, 50),
            TailTip = at
        });
        RefreshCanvas();
    }

    private void EditCalloutText(CalloutAnnotation callout)
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox("Callout text:", "Callout", callout.Text);
        if (string.IsNullOrEmpty(input) || input == callout.Text) return;
        // Reuses the generic PropertyChangeAction<T> (introduced for the text-properties panel)
        // rather than a dedicated action class just for this one field.
        Do(new PropertyChangeAction<string>(callout, (a, v) => ((CalloutAnnotation)a).Text = v, callout.Text, input));
        SetSelected(callout);
        RefreshCanvas();
    }

    // ---- colour picker ----

    private void PickColor(Point imagePt)
    {
        if (imagePt.X >= 0 && imagePt.Y >= 0 && imagePt.X < _capture.PixelWidth && imagePt.Y < _capture.PixelHeight)
        {
            try
            {
                var px = new CroppedBitmap(_capture.Image, new Int32Rect((int)imagePt.X, (int)imagePt.Y, 1, 1));
                var converted = new FormatConvertedBitmap(px, PixelFormats.Bgra32, null, 0);
                byte[] bytes = new byte[4];
                converted.CopyPixels(bytes, 4, 0);
                SetColor(Color.FromRgb(bytes[2], bytes[1], bytes[0]));
            }
            catch { /* sampling can fail right at the image edge; ignore */ }
        }
        _tool = Tool.Select;
        SetActiveTool(ToolSelectBtn);
    }

    // ---- crop ----

    private void ApplyCrop(Rect r)
    {
        int x = (int)Math.Max(0, Math.Round(r.X));
        int y = (int)Math.Max(0, Math.Round(r.Y));
        int w = (int)Math.Min(_capture.PixelWidth - x, Math.Round(r.Width));
        int h = (int)Math.Min(_capture.PixelHeight - y, Math.Round(r.Height));
        if (w < 4 || h < 4) return; // drag was too small to be a deliberate crop; just ignore it

        var cropped = new CroppedBitmap(_capture.Image, new Int32Rect(x, y, w, h));
        cropped.Freeze();

        Do(new CropAction(_capture.Image, cropped, new Vector(-x, -y)));
        RefreshCanvas();
        ZoomToFit();
    }

    /// <summary>Swaps in a new base image and resizes the canvas to match. Used by crop
    /// (and its undo/redo, which swap back to the pre-crop image).</summary>
    private void SetImage(BitmapSource img)
    {
        _capture.Image = img;
        CanvasHost.Width = AnnotationLayer.Width = SelectionLayer.Width = img.PixelWidth;
        CanvasHost.Height = AnnotationLayer.Height = SelectionLayer.Height = img.PixelHeight;
        BaseImage.Source = img;
        ImageSizeText.Text = $"{img.PixelWidth} x {img.PixelHeight}";
        CanvasSizeText.Text = $"{img.PixelWidth} x {img.PixelHeight} px";
    }

    private static void OffsetAnnotation(Annotation a, Vector offset)
    {
        a.Bounds = new Rect(a.Bounds.X + offset.X, a.Bounds.Y + offset.Y, a.Bounds.Width, a.Bounds.Height);
        switch (a)
        {
            case ArrowAnnotation arrow:
                arrow.Start += offset; arrow.End += offset;
                break;
            case LineAnnotation line:
                line.Start += offset; line.End += offset;
                break;
            case InkAnnotation ink:
                for (int i = 0; i < ink.Points.Count; i++) ink.Points[i] += offset;
                break;
            case CalloutAnnotation callout:
                // Otherwise moving the bubble leaves the tail pointing at the old absolute spot,
                // visually detaching it from the bubble it's supposed to be attached to.
                callout.TailTip += offset;
                break;
        }
    }

    // ---- tool + colour handlers ----
    private void Tool_Select(object s, RoutedEventArgs e)      { _tool = Tool.Select;      SetActiveTool(s); }
    private void Tool_Arrow(object s, RoutedEventArgs e)       { _tool = Tool.Arrow;       SetActiveTool(s); }
    private void Tool_Line(object s, RoutedEventArgs e)        { _tool = Tool.Line;        SetActiveTool(s); }
    private void Tool_Rectangle(object s, RoutedEventArgs e)   { _tool = Tool.Rectangle;   SetActiveTool(s); }
    private void Tool_Ellipse(object s, RoutedEventArgs e)     { _tool = Tool.Ellipse;     SetActiveTool(s); }
    private void Tool_Highlight(object s, RoutedEventArgs e)   { _tool = Tool.Highlight;   SetActiveTool(s); }
    private void Tool_Text(object s, RoutedEventArgs e)        { _tool = Tool.Text;        SetActiveTool(s); }
    private void Tool_Step(object s, RoutedEventArgs e)        { _tool = Tool.Step;        SetActiveTool(s); }
    private void Tool_ColorPicker(object s, RoutedEventArgs e) { _tool = Tool.ColorPicker; SetActiveTool(s); }
    private void Tool_Blur(object s, RoutedEventArgs e)        { _tool = Tool.Blur;        SetActiveTool(s); }
    private void Tool_Callout(object s, RoutedEventArgs e)     { _tool = Tool.Callout;     SetActiveTool(s); }

    // Crop can be triggered from the rail (ToggleButton) or the Canvas tab's Quick Action (plain
    // Button) -- either way, arm the crop tool and let the next drag on the canvas define the region.
    private void Tool_Crop(object s, RoutedEventArgs e)
    {
        _tool = Tool.Crop;
        SetActiveTool(ToolCropBtn);
    }

    // Only one tool rail button should read as "active" at a time (they're independent
    // ToggleButtons, not a RadioButton group, so we uncheck the siblings by hand).
    private void SetActiveTool(object active)
    {
        CommitTextEdit(); // switching tools mid-edit finishes it rather than leaving it stranded
        foreach (var rail in new ToggleButton?[]
                 { ToolSelectBtn, ToolArrowBtn, ToolLineBtn, ToolRectBtn, ToolEllipseBtn,
                   ToolHighlightBtn, ToolTextBtn, ToolStepBtn, ToolColorPickerBtn, ToolCropBtn,
                   ToolBlurBtn, ToolCalloutBtn })
            if (rail != null) rail.IsChecked = ReferenceEquals(rail, active);
    }

    // Sets the default colour for the NEXT new shape, and -- new in this pass -- if a shape is
    // currently selected, live-applies the change to it too (as one undo entry) instead of only
    // ever affecting shapes drawn after this point.
    private void SetColor(Color c)
    {
        _color = c;
        CurrentColorSwatch.Background = new SolidColorBrush(c);
        if (_selected != null && _selected.Color != c)
        {
            Do(new RecolorAction(_selected, _selected.Color, c));
            RefreshCanvas();
        }
    }

    private void Color_Red(object s, MouseButtonEventArgs e)    => SetColor(Colors.Red);
    private void Color_Blue(object s, MouseButtonEventArgs e)   => SetColor(Color.FromRgb(0x2D, 0x7D, 0xD2));
    private void Color_Green(object s, MouseButtonEventArgs e)  => SetColor(Color.FromRgb(0x2E, 0x7D, 0x32));
    private void Color_Yellow(object s, MouseButtonEventArgs e) => SetColor(Color.FromRgb(0xF9, 0xA8, 0x25));
    private void Color_Black(object s, MouseButtonEventArgs e)  => SetColor(Colors.Black);
    private void Color_White(object s, MouseButtonEventArgs e)  => SetColor(Colors.White);

    // Same apply-to-selection-or-set-default treatment as SetColor, shared by both the dropdown
    // and the slider below -- neither tries to stay mirrored with the other, both just call this.
    // A selected Step routes to ApplyStepRadius instead: see SyncStrokeControlForSelection for why.
    private void SetThickness(double t)
    {
        _thickness = t;
        if (_selected is StepAnnotation step)
        {
            ApplyStepRadius(step, t);
        }
        else if (_selected is BlurAnnotation blur)
        {
            double blockSize = Math.Max(2, t);
            if (Math.Abs(blur.BlockSize - blockSize) < 0.01) return;
            Do(new PropertyChangeAction<double>(blur, (a, v) => ((BlurAnnotation)a).BlockSize = v, blur.BlockSize, blockSize));
            RefreshCanvas();
        }
        else if (_selected != null && !Equals(_selected.Thickness, t))
        {
            Do(new RestrokeAction(_selected, _selected.Thickness, t));
            RefreshCanvas();
        }
    }

    // Resizing a Step via this control keeps it centered on the same point (unlike dragging a
    // corner handle, which anchors the opposite corner instead) -- both are valid ways to resize,
    // this one just matches what you'd expect from a "make it bigger/smaller" numeric control.
    private void ApplyStepRadius(StepAnnotation step, double radius)
    {
        radius = Math.Max(6, radius);
        if (Math.Abs(step.Radius - radius) < 0.01) return;
        var before = Snapshot(step);
        var center = new Point(step.Bounds.X + step.Bounds.Width / 2, step.Bounds.Y + step.Bounds.Height / 2);
        step.Radius = radius;
        step.Bounds = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        Do(new GeometryChangeAction(step, before, Snapshot(step)));
        RefreshCanvas();
    }

    private void StrokeWidthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPropertyEvents) return;
        if (StrokeWidthCombo.SelectedItem is ComboBoxItem { Content: string s } && double.TryParse(s, out var v))
            SetThickness(v);
    }

    private void StrokeWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressPropertyEvents) return;
        SetThickness(e.NewValue);
    }

    // ---- text properties panel ----
    // Replaces the Canvas/Annotations tabs (and their tab buttons) whenever a Text object is
    // selected, per SetSelected. Every control here follows the same apply-to-selection pattern
    // established by SetColor/SetThickness above: mutate, and if it actually changed, push one
    // PropertyChangeAction and RefreshCanvas().

    private void ShowPropertiesFor(Annotation? a)
    {
        if (a is TextAnnotation t)
        {
            if (!_propertiesActive)
            {
                _wasAnnotationsTabActive = AnnotationsTab.Visibility == Visibility.Visible;
                _propertiesActive = true;
            }
            TabButtonsPanel.Visibility = Visibility.Collapsed;
            TextPropertiesHeader.Visibility = Visibility.Visible;
            CanvasTab.Visibility = Visibility.Collapsed;
            AnnotationsTab.Visibility = Visibility.Collapsed;
            TextPropertiesTab.Visibility = Visibility.Visible;
            PopulatePropertiesPanel(t);
        }
        else if (_propertiesActive)
        {
            _propertiesActive = false;
            TabButtonsPanel.Visibility = Visibility.Visible;
            TextPropertiesHeader.Visibility = Visibility.Collapsed;
            TextPropertiesTab.Visibility = Visibility.Collapsed;
            CanvasTab.Visibility = _wasAnnotationsTabActive ? Visibility.Collapsed : Visibility.Visible;
            AnnotationsTab.Visibility = _wasAnnotationsTabActive ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PopulatePropertiesPanel(TextAnnotation t)
    {
        _suppressPropertyEvents = true;
        try
        {
            if (PropFontCombo.Items.Count == 0)
                PropFontCombo.ItemsSource = Fonts.SystemFontFamilies
                    .Select(f => f.Source).Distinct().OrderBy(name => name).ToList();
            PropFontCombo.SelectedItem = t.FontFamily;

            PropFontSizeCombo.Text = t.FontSize.ToString("0");
            PropBoldBtn.IsChecked = t.FontWeight == FontWeights.Bold;
            PropItalicBtn.IsChecked = t.FontStyle == FontStyles.Italic;
            PropUnderlineBtn.IsChecked = t.Underline;
            PropAlignLeftBtn.IsChecked = t.Alignment == TextAlignment.Left;
            PropAlignCenterBtn.IsChecked = t.Alignment == TextAlignment.Center;
            PropAlignRightBtn.IsChecked = t.Alignment == TextAlignment.Right;
            PropOpacitySlider.Value = t.Opacity * 100;
            PropRotationSlider.Value = t.Rotation;
            PropRotationText.Text = $"{t.Rotation:0}°";
            PropShadowBtn.IsChecked = t.Shadow;
            PropOutlineBtn.IsChecked = t.Outline;
            PropLockBtn.IsChecked = t.IsLocked;
            PropVisibleBtn.IsChecked = t.IsVisible;
            PropContentBox.Text = t.Text;
            _propContentBefore = t.Text;
        }
        finally { _suppressPropertyEvents = false; }
    }

    private void PropContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected is not TextAnnotation t) return;
        t.Text = PropContentBox.Text; // live preview; the undo entry is pushed once on lose-focus below
        RefreshCanvas();
    }

    private void PropContentBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_selected is not TextAnnotation t) return;
        if (PropContentBox.Text != _propContentBefore)
        {
            Do(new TextEditAction(t, _propContentBefore ?? "", PropContentBox.Text));
            _propContentBefore = PropContentBox.Text;
            RefreshCanvas();
        }
    }

    private void PropFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected is not TextAnnotation t) return;
        if (PropFontCombo.SelectedItem is not string family || family == t.FontFamily) return;
        _defaultFontFamily = family;
        Do(new PropertyChangeAction<string>(t, (a, v) => ((TextAnnotation)a).FontFamily = v, t.FontFamily, family));
        RefreshCanvas();
    }

    private void ApplyFontSize(double v)
    {
        if (_selected is not TextAnnotation t || Math.Abs(t.FontSize - v) < 0.01) return;
        _defaultFontSize = v;
        Do(new PropertyChangeAction<double>(t, (a, val) => ((TextAnnotation)a).FontSize = val, t.FontSize, v));
        RefreshCanvas();
    }

    private void PropFontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPropertyEvents) return;
        if (PropFontSizeCombo.SelectedItem is ComboBoxItem { Content: string s } && double.TryParse(s, out var v))
            ApplyFontSize(v);
    }

    private void PropFontSizeCombo_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressPropertyEvents) return;
        if (double.TryParse(PropFontSizeCombo.Text, out var v)) ApplyFontSize(v);
    }

    private void PropBold_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected is not TextAnnotation t) return;
        var w = PropBoldBtn.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        _defaultFontWeight = w;
        Do(new PropertyChangeAction<FontWeight>(t, (a, v) => ((TextAnnotation)a).FontWeight = v, t.FontWeight, w));
        RefreshCanvas();
    }

    private void PropItalic_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected is not TextAnnotation t) return;
        var style = PropItalicBtn.IsChecked == true ? FontStyles.Italic : FontStyles.Normal;
        _defaultFontStyle = style;
        Do(new PropertyChangeAction<FontStyle>(t, (a, v) => ((TextAnnotation)a).FontStyle = v, t.FontStyle, style));
        RefreshCanvas();
    }

    private void PropUnderline_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected is not TextAnnotation t) return;
        bool v = PropUnderlineBtn.IsChecked == true;
        _defaultUnderline = v;
        Do(new PropertyChangeAction<bool>(t, (a, val) => ((TextAnnotation)a).Underline = val, t.Underline, v));
        RefreshCanvas();
    }

    private void PropAlignLeft_Click(object sender, RoutedEventArgs e) => ApplyAlignment(TextAlignment.Left);
    private void PropAlignCenter_Click(object sender, RoutedEventArgs e) => ApplyAlignment(TextAlignment.Center);
    private void PropAlignRight_Click(object sender, RoutedEventArgs e) => ApplyAlignment(TextAlignment.Right);

    // Three independent ToggleButtons, not a RadioButton group -- uncheck siblings by hand, same
    // pattern as SetActiveTool. Setting IsChecked in code does not re-raise Click, so this can't loop.
    private void ApplyAlignment(TextAlignment align)
    {
        if (_suppressPropertyEvents || _selected is not TextAnnotation t) return;
        PropAlignLeftBtn.IsChecked = align == TextAlignment.Left;
        PropAlignCenterBtn.IsChecked = align == TextAlignment.Center;
        PropAlignRightBtn.IsChecked = align == TextAlignment.Right;
        _defaultAlignment = align;
        if (t.Alignment == align) return;
        Do(new PropertyChangeAction<TextAlignment>(t, (a, v) => ((TextAnnotation)a).Alignment = v, t.Alignment, align));
        RefreshCanvas();
    }

    // Opacity/Rotation sliders both capture their "before" value on ANY mouse-down on the slider
    // (thumb drag or a plain track click both start here) and push one undo entry on mouse-up --
    // Thumb.DragStarted/DragCompleted alone would miss a plain track click, which moves the Thumb
    // without ever starting a drag on it.
    private void PropOpacitySlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    { if (_selected is TextAnnotation t) _sliderBeforeValue = t.Opacity; }

    private void PropOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressPropertyEvents || _selected is not TextAnnotation t) return;
        t.Opacity = e.NewValue / 100.0;
        RefreshCanvas();
    }

    private void PropOpacitySlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_selected is not TextAnnotation t) return;
        if (Math.Abs(t.Opacity - _sliderBeforeValue) > 0.001)
            Do(new PropertyChangeAction<double>(t, (a, v) => a.Opacity = v, _sliderBeforeValue, t.Opacity));
    }

    private void PropRotationSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    { if (_selected is TextAnnotation t) _sliderBeforeValue = t.Rotation; }

    private void PropRotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PropRotationText.Text = $"{e.NewValue:0}°";
        if (_suppressPropertyEvents || _selected is not TextAnnotation t) return;
        t.Rotation = e.NewValue;
        RefreshCanvas();
    }

    private void PropRotationSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_selected is not TextAnnotation t) return;
        if (Math.Abs(t.Rotation - _sliderBeforeValue) > 0.001)
            Do(new PropertyChangeAction<double>(t, (a, v) => a.Rotation = v, _sliderBeforeValue, t.Rotation));
    }

    private void PropShadow_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected is not TextAnnotation t) return;
        bool v = PropShadowBtn.IsChecked == true;
        Do(new PropertyChangeAction<bool>(t, (a, val) => ((TextAnnotation)a).Shadow = val, t.Shadow, v));
        RefreshCanvas();
    }

    private void PropOutline_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected is not TextAnnotation t) return;
        bool v = PropOutlineBtn.IsChecked == true;
        Do(new PropertyChangeAction<bool>(t, (a, val) => ((TextAnnotation)a).Outline = val, t.Outline, v));
        RefreshCanvas();
    }

    private void PropLock_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected == null) return;
        bool v = PropLockBtn.IsChecked == true;
        Do(new PropertyChangeAction<bool>(_selected, (a, val) => a.IsLocked = val, _selected.IsLocked, v));
        RefreshCanvas();
    }

    private void PropVisible_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected == null) return;
        bool v = PropVisibleBtn.IsChecked == true;
        Do(new PropertyChangeAction<bool>(_selected, (a, val) => a.IsVisible = val, _selected.IsVisible, v));
        RefreshCanvas();
    }

    // ---- undo / redo ----
    // A small command history: adding an annotation and cropping are both EditActions, so Undo
    // reverts either kind the same way instead of crop being a special, un-undoable case.

    private abstract class EditAction
    {
        public abstract void Apply(EditorWindow w);
        public abstract void Revert(EditorWindow w);
    }

    private sealed class AddAnnotationAction : EditAction
    {
        private readonly Annotation _a;
        public AddAnnotationAction(Annotation a) => _a = a;
        public override void Apply(EditorWindow w)
        {
            if (!w._capture.Annotations.Contains(_a)) w._capture.Annotations.Add(_a);
        }
        public override void Revert(EditorWindow w) => w._capture.Annotations.Remove(_a);
    }

    private sealed class CropAction : EditAction
    {
        private readonly BitmapSource _before, _after;
        private readonly Vector _offset; // image-pixel shift applied to annotations going forward
        public CropAction(BitmapSource before, BitmapSource after, Vector offset)
        { _before = before; _after = after; _offset = offset; }

        public override void Apply(EditorWindow w)
        {
            foreach (var a in w._capture.Annotations) OffsetAnnotation(a, _offset);
            w.SetImage(_after);
        }
        public override void Revert(EditorWindow w)
        {
            foreach (var a in w._capture.Annotations) OffsetAnnotation(a, -_offset);
            w.SetImage(_before);
        }
    }

    // A structural snapshot of an annotation's mutable geometry fields, used by Move/Resize undo.
    // Move+Resize share ONE action type (below) built on whole-state snapshots rather than deltas:
    // the live drag already mutates the annotation on every MouseMove, so a delta pushed through
    // Do() (which calls Apply() immediately) would double-apply on top of what the drag already
    // did. A snapshot sidesteps that: Apply() just re-asserts values the object already holds, so
    // pushing one action on drag-end is safe regardless of how the object got to its current state.
    private readonly record struct AnnotationSnapshot(Rect Bounds, Point Start, Point End, PointCollection? Points, double FontSize, double Radius, double Rotation, Point TailTip);

    private static AnnotationSnapshot Snapshot(Annotation a) => new(
        a.Bounds,
        a switch { ArrowAnnotation ar => ar.Start, LineAnnotation ln => ln.Start, _ => default },
        a switch { ArrowAnnotation ar => ar.End, LineAnnotation ln => ln.End, _ => default },
        a is InkAnnotation ink ? new PointCollection(ink.Points) : null, // clone -- Points is mutated in place elsewhere
        a is TextAnnotation t ? t.FontSize : 0,
        a is StepAnnotation s ? s.Radius : 0,
        a.Rotation,
        a is CalloutAnnotation c ? c.TailTip : default);

    private static void Restore(Annotation a, AnnotationSnapshot s)
    {
        a.Bounds = s.Bounds;
        a.Rotation = s.Rotation;
        switch (a)
        {
            case ArrowAnnotation ar: ar.Start = s.Start; ar.End = s.End; break;
            case LineAnnotation ln: ln.Start = s.Start; ln.End = s.End; break;
            case InkAnnotation ink when s.Points != null: ink.Points = new PointCollection(s.Points); break;
            case TextAnnotation t: t.FontSize = s.FontSize; break;
            case StepAnnotation st: st.Radius = s.Radius; break;
            case CalloutAnnotation c: c.TailTip = s.TailTip; break;
        }
    }

    // record struct's generated Equals would compare Points by reference, not value -- Snapshot()
    // always clones a fresh PointCollection, so two snapshots of an UNCHANGED ink annotation would
    // never appear equal and every no-op click would wrongly push an undo entry. Compare by value instead.
    private static bool SnapshotsEqual(AnnotationSnapshot a, AnnotationSnapshot b)
    {
        if (a.Bounds != b.Bounds || a.Start != b.Start || a.End != b.End || a.TailTip != b.TailTip ||
            Math.Abs(a.FontSize - b.FontSize) > 0.001 || Math.Abs(a.Radius - b.Radius) > 0.001 ||
            Math.Abs(a.Rotation - b.Rotation) > 0.001)
            return false;
        if (a.Points == null || b.Points == null) return a.Points == b.Points;
        if (a.Points.Count != b.Points.Count) return false;
        for (int i = 0; i < a.Points.Count; i++)
            if (a.Points[i] != b.Points[i]) return false;
        return true;
    }

    // Generic undo entry for the simple scalar properties added by the text-properties panel
    // (font, size, weight, style, underline, alignment, opacity, shadow, outline, lock, visible)
    // -- one class instead of ~8 near-identical ones, each just a captured setter + before/after.
    private sealed class PropertyChangeAction<T> : EditAction
    {
        private readonly Annotation _a;
        private readonly Action<Annotation, T> _setter;
        private readonly T _before, _after;
        public PropertyChangeAction(Annotation a, Action<Annotation, T> setter, T before, T after)
        { _a = a; _setter = setter; _before = before; _after = after; }
        public override void Apply(EditorWindow w) => _setter(_a, _after);
        public override void Revert(EditorWindow w) => _setter(_a, _before);
    }

    private sealed class GeometryChangeAction : EditAction // covers both Move and Resize
    {
        private readonly Annotation _a;
        private readonly AnnotationSnapshot _before, _after;
        public GeometryChangeAction(Annotation a, AnnotationSnapshot before, AnnotationSnapshot after)
        { _a = a; _before = before; _after = after; }
        public override void Apply(EditorWindow w) => Restore(_a, _after);
        public override void Revert(EditorWindow w) => Restore(_a, _before);
    }

    private sealed class RecolorAction : EditAction
    {
        private readonly Annotation _a;
        private readonly Color _before, _after;
        public RecolorAction(Annotation a, Color before, Color after) { _a = a; _before = before; _after = after; }
        public override void Apply(EditorWindow w) => _a.Color = _after;
        public override void Revert(EditorWindow w) => _a.Color = _before;
    }

    private sealed class RestrokeAction : EditAction
    {
        private readonly Annotation _a;
        private readonly double _before, _after;
        public RestrokeAction(Annotation a, double before, double after) { _a = a; _before = before; _after = after; }
        public override void Apply(EditorWindow w) => _a.Thickness = _after;
        public override void Revert(EditorWindow w) => _a.Thickness = _before;
    }

    private sealed class TextEditAction : EditAction
    {
        private readonly TextAnnotation _a;
        private readonly string _before, _after;
        public TextEditAction(TextAnnotation a, string before, string after) { _a = a; _before = before; _after = after; }
        public override void Apply(EditorWindow w) => _a.Text = _after;
        public override void Revert(EditorWindow w) => _a.Text = _before;
    }

    private sealed class DeleteAnnotationAction : EditAction
    {
        private readonly Annotation _a;
        private readonly int _index; // z-order position at delete time -- Revert must Insert here,
        public DeleteAnnotationAction(Annotation a, int index) { _a = a; _index = index; }  // not
        public override void Apply(EditorWindow w) => w._capture.Annotations.Remove(_a);    // Add
        public override void Revert(EditorWindow w) =>                                       // (append),
            w._capture.Annotations.Insert(Math.Min(_index, w._capture.Annotations.Count), _a); // or undoing a delete of a non-topmost shape silently reorders it
    }

    private void Do(EditAction action)
    {
        action.Apply(this);
        _undoStack.Push(action);
        _redoStack.Clear();
    }

    private void PushAnnotation(Annotation a) => Do(new AddAnnotationAction(a));

    private void Undo_Click(object s, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        action.Revert(this);
        _redoStack.Push(action);
        RenumberSteps();
        RefreshCanvas();
    }

    private void Redo_Click(object s, RoutedEventArgs e)
    {
        if (_redoStack.Count == 0) return;
        var action = _redoStack.Pop();
        action.Apply(this);
        _undoStack.Push(action);
        RenumberSteps();
        RefreshCanvas();
    }

    private void Delete_Click(object s, RoutedEventArgs e)
    {
        if (_selected == null) return;
        Do(new DeleteAnnotationAction(_selected, _capture.Annotations.IndexOf(_selected)));
        SetSelected(null);
        RenumberSteps();
        RefreshCanvas();
    }

    // Del/Esc/Ctrl+Z/Ctrl+Y/Ctrl+C/Ctrl+V/Ctrl+D. Guarded against TextBox focus so typing in the
    // Title/Caption boxes, InlineTextEditor, or the properties-panel content box -- all real
    // TextBoxes -- behaves as normal text editing instead of being hijacked into annotation-level
    // operations (e.g. Ctrl+Z while typing should undo a character, not the last canvas edit).
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (e.Key == Key.Delete && _selected != null)
        {
            Delete_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _selected != null)
        {
            SetSelected(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z)
        {
            Undo_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Y)
        {
            Redo_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C && _selected != null)
        {
            _copiedAnnotation = _selected;
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.V && _copiedAnnotation != null)
        {
            PasteCopied();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.D && _selected != null)
        {
            DuplicateSelected();
            e.Handled = true;
        }
    }

    private void RenumberSteps()
    {
        int n = 1;
        foreach (var step in _capture.Annotations.OfType<StepAnnotation>())
            step.Number = n++;
    }

    private async void Ocr_Click(object s, RoutedEventArgs e)
    {
        if (!App.OcrEngine.IsAvailable)
        {
            MessageBox.Show("No OCR language is available. Install a Windows language pack, " +
                            "or wire up the Tesseract backend.", "OCR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _capture.OcrText = await App.OcrEngine.RecognizeAsync(_capture.Image);
        MessageBox.Show(string.IsNullOrWhiteSpace(_capture.OcrText)
            ? "No text found."
            : _capture.OcrText, "Extracted text", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- output ----

    private void Copy_Click(object s, RoutedEventArgs e)
    {
        CommitTextEdit(); // so the flattened image reflects text just typed but not yet clicked away from
        try { Clipboard.SetImage(CaptureFlattener.Flatten(_capture)); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    // Quick save: silently overwrite the PNG already on disk (written at capture time).
    private void Save_Click(object s, RoutedEventArgs e)
    {
        CommitTextEdit(); // otherwise text typed but not yet clicked away from would be silently lost
        try
        {
            App.Workspace.SavePng(_capture);
            FileSizeText.Text = GetFileSizeText(_capture);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveAs_Click(object s, RoutedEventArgs e)
    {
        CommitTextEdit();
        var dlg = new SaveFileDialog
        {
            FileName = _capture.SuggestedFileName,
            DefaultExt = ".png",
            Filter = "PNG image (*.png)|*.png"
        };
        if (dlg.ShowDialog() == true)
        {
            var bmp = CaptureFlattener.Flatten(_capture);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(dlg.FileName);
            enc.Save(fs);
        }
    }

    private async void Export_Click(object s, RoutedEventArgs e)
    {
        CommitTextEdit();
        // Export just this capture as a one-step document. (Multi-capture export lives in MainWindow.)
        var exporters = new IExporter[]
        {
            new MarkdownExporter(), new PdfExporter(), new WordExporter(), new PowerPointExporter()
        };
        var filter = string.Join("|", exporters.Select(x => $"{x.FormatName} (*{x.Extension})|*{x.Extension}"));

        var dlg = new SaveFileDialog { FileName = _capture.SuggestedFileName, Filter = filter };
        if (dlg.ShowDialog() != true) return;

        var chosen = exporters[dlg.FilterIndex - 1];
        var options = new ExportOptions
        {
            DocumentTitle = string.IsNullOrWhiteSpace(_capture.Title) ? "SnapDoc Export" : _capture.Title,
            AsStepGuide = true,
            IncludeOcrText = !string.IsNullOrWhiteSpace(_capture.OcrText)
        };
        try
        {
            await chosen.ExportAsync(new[] { _capture }, dlg.FileName, options);
            MessageBox.Show($"Exported to {dlg.FileName}", "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary>Hosts a DrawingVisual inside the WPF visual tree so we can draw annotations cheaply.</summary>
internal sealed class VisualHost : FrameworkElement
{
    private readonly Visual _visual;
    public VisualHost(Visual v) { _visual = v; }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;
}
