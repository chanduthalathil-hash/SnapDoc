using System;
using System.Globalization;
using System.Windows.Data;
using SnapDoc.Models;

namespace SnapDoc.Converters;

/// <summary>One-glyph type indicator for the editor's Layers list, so rows are distinguishable
/// at a glance even once DisplayName shows real content (e.g. a Text row's own text) rather than
/// its type name.</summary>
public sealed class AnnotationTypeGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        TextAnnotation => "T",
        ArrowAnnotation => "→",
        LineAnnotation => "—",
        RectangleAnnotation => "▭",
        EllipseAnnotation => "○",
        InkAnnotation ink => ink.Highlighter ? "▉" : "✎",
        StepAnnotation s => s.Number.ToString(),
        BlurAnnotation => "▦",
        CalloutAnnotation => "💬",
        _ => "?"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
