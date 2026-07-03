using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lumen.App.Converters;

/// <summary>True → Collapsed, False → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>First letter of a string, uppercased — monogram fallbacks.</summary>
public sealed class FirstLetterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } text ? text[..1].ToUpper(culture) : "?";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"active" when the value's string form equals the parameter; otherwise null. Drives Tag triggers.</summary>
public sealed class ActiveWhenEqualConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(
            System.Convert.ToString(value, CultureInfo.InvariantCulture),
            parameter as string,
            StringComparison.OrdinalIgnoreCase)
            ? "active"
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Boolean inversion for IsEnabled-style bindings.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>
/// Visible when the playback state's name appears in the pipe-separated parameter
/// ("Buffering|Opening|Reconnecting"); Collapsed otherwise.
/// </summary>
public sealed class PlaybackStateToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string states)
        {
            return Visibility.Collapsed;
        }

        var name = value.ToString();
        foreach (var candidate in states.Split('|'))
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return Visibility.Visible;
            }
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Null or empty string → Collapsed.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Zero (or null) count → Collapsed; any positive count → Visible.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
