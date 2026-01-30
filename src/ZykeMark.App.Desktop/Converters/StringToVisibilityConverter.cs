using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ZykeMark.App.Desktop.Converters;

/// <summary>
/// Converts a null or empty string to Visibility.Collapsed, and non-null/non-empty to Visibility.Visible.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return string.IsNullOrWhiteSpace(str) ? Visibility.Collapsed : Visibility.Visible;
        }
        return value is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
