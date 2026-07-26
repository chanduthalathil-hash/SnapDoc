using System;
using System.Globalization;
using System.Windows.Data;

namespace SnapDoc.Converters;

/// <summary>ListBoxItem.AlternationIndex is 0-based; gallery position badges should read 1-based.</summary>
public sealed class IndexPlusOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i ? (i + 1).ToString() : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
