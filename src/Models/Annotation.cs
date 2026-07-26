using System;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace SnapDoc.Models;

/// <summary>
/// Base class for anything drawn on top of a capture. Coordinates are in IMAGE pixel space
/// (not screen space), so annotations stay correct regardless of editor zoom or DPI.
///
/// To add a new annotation type: derive from this, add a case to AnnotationRenderer
/// (src/Editor/AnnotationRenderer.cs) and to the tool palette. Nothing else needs to change --
/// exporters flatten via the renderer, so new types export for free.
/// </summary>
public abstract class Annotation
{
    /// <summary>Stable identity, mainly so clone/duplicate/copy-paste can tell a copy apart
    /// from its source even though every other property starts out identical.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Stroke/primary colour.</summary>
    public Color Color { get; set; } = Colors.Red;

    /// <summary>Stroke thickness in image pixels.</summary>
    public double Thickness { get; set; } = 3.0;

    /// <summary>Bounding box in image-pixel coordinates. Used for hit-testing and selection.</summary>
    public Rect Bounds { get; set; }

    /// <summary>Rotation in degrees, clockwise, about the shape's own center.</summary>
    public double Rotation { get; set; } = 0;

    /// <summary>0 (fully transparent) to 1 (fully opaque).</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>Hidden layers are kept (undo-able) but skipped by the renderer/exporters.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Locked annotations stay selectable (so they can be inspected/unlocked) but the
    /// editor refuses to start a move/resize/rotate drag on them while locked.</summary>
    public bool IsLocked { get; set; } = false;

    /// <summary>Short label for the editor's Layers list.</summary>
    public abstract string DisplayName { get; }
}

/// <summary>A rectangle outline (e.g. to frame a UI element).</summary>
public sealed class RectangleAnnotation : Annotation
{
    public bool Filled { get; set; } = false;
    public double CornerRadius { get; set; } = 0;
    public override string DisplayName => "Rectangle";
}

/// <summary>An ellipse outline.</summary>
public sealed class EllipseAnnotation : Annotation
{
    public override string DisplayName => "Ellipse";
}

/// <summary>An arrow from Start to End. The most-used annotation in documentation.</summary>
public sealed class ArrowAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }
    public double HeadSize { get; set; } = 14;
    public override string DisplayName => "Arrow";
}

/// <summary>A plain straight line from Start to End (an arrow with no head).</summary>
public sealed class LineAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }
    public override string DisplayName => "Line";
}

/// <summary>Freehand highlighter or pen stroke.</summary>
public sealed class InkAnnotation : Annotation
{
    public PointCollection Points { get; set; } = new();
    /// <summary>True = translucent highlighter behaviour.</summary>
    public bool Highlighter { get; set; } = false;
    public override string DisplayName => Highlighter ? "Highlight" : "Pen";
}

/// <summary>A text label / callout.</summary>
public sealed class TextAnnotation : Annotation
{
    public string Text { get; set; } = "Text";
    public double FontSize { get; set; } = 24;
    public string FontFamily { get; set; } = "Segoe UI";
    public FontWeight FontWeight { get; set; } = FontWeights.Normal;
    public FontStyle FontStyle { get; set; } = FontStyles.Normal;
    public bool Underline { get; set; } = false;
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;
    public bool Background { get; set; } = true;
    public bool Shadow { get; set; } = false;
    public bool Outline { get; set; } = false;

    // Layers-panel label: a content preview is far more useful than the literal word "Text"
    // once there's more than one text object in a capture.
    public override string DisplayName => string.IsNullOrWhiteSpace(Text) ? "Text (empty)"
        : Text.Length > 24 ? Text[..24] + "…" : Text;
}

/// <summary>
/// Numbered step marker (the "Step Tool"). This is the wedge feature:
/// a sequence of these on captures becomes a numbered how-to guide on export.
/// </summary>
public sealed class StepAnnotation : Annotation
{
    /// <summary>1-based step number. Auto-assigned in draw order; renumbered if one is deleted.</summary>
    public int Number { get; set; }
    public double Radius { get; set; } = 20;
    public override string DisplayName => $"Step {Number}";
}

/// <summary>A redaction region: the underlying capture pixels within Bounds are pixelated,
/// not the annotation itself drawing anything of its own. Pixelation (not a soft blur) is
/// deliberate -- it destroys the underlying detail (text, faces) far more reliably, which is
/// the actual point of a redaction tool.</summary>
public sealed class BlurAnnotation : Annotation
{
    /// <summary>Pixelation cell size in image pixels. Set once at creation from the drawn
    /// region's size; not user-adjustable in this pass.</summary>
    public double BlockSize { get; set; } = 12;
    public override string DisplayName => "Blur";
}

/// <summary>A speech-bubble label with a tail pointing at a specific spot -- for calling out one
/// particular element rather than labeling a general area the way Text does.</summary>
public sealed class CalloutAnnotation : Annotation
{
    public string Text { get; set; } = "Callout";
    public double FontSize { get; set; } = 18;
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Where the tail points to, in image-pixel space. Not independently draggable in
    /// this pass -- OffsetAnnotation/ApplyResize keep it attached proportionally to Bounds when
    /// the bubble is moved or resized, but the user can't grab the tail tip itself yet.</summary>
    public Point TailTip { get; set; }

    public override string DisplayName => string.IsNullOrWhiteSpace(Text) ? "Callout"
        : Text.Length > 24 ? Text[..24] + "…" : Text;
}
