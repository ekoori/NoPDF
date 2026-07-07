using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NoPdf.App.Converters;

/// <summary>Converts a "#RRGGBB" string to a SolidColorBrush (for palette swatches).</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && Color.TryParse(s, out var c))
            return new SolidColorBrush(c);
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
