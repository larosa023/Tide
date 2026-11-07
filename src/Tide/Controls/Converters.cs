using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Tide.Core;

namespace Tide.Controls;

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            long l => Format.Bytes(l),
            double d => Format.Bytes(d),
            int i => Format.Bytes(i),
            _ => "",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? (d < 1 ? "—" : Format.Rate(d)) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class AgoConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime t ? Format.Ago(t) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the bound value is true / non-null / non-empty (or the reverse with Invert).</summary>
public sealed class VisibleConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool v = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            null => false,
            _ => true,
        };
        return v ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Binds a RadioButton's IsChecked to an int/enum property: checked when value == parameter.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter == null) return Binding.DoNothing;
        if (targetType.IsEnum) return Enum.Parse(targetType, parameter.ToString()!);
        if (targetType == typeof(int)) return int.Parse(parameter.ToString()!, CultureInfo.InvariantCulture);
        return parameter.ToString()!;
    }
}

/// <summary>Visible when the bound value's text equals the parameter (used to switch pages).</summary>
public sealed class PageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Turns a 0..1 share into a bar width inside a fixed track.</summary>
public sealed class ShareWidthConverter : IValueConverter
{
    public double Max { get; set; } = 120;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? Math.Max(2, Math.Min(1, d) * Max) : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
