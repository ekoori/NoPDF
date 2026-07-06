using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NoPdf.App.Converters;

/// <summary>Returns true when the bound enum value equals the converter parameter.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && value.Equals(parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
