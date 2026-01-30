using System.Globalization;
using System.Windows.Data;

namespace ZykeMark.App.Desktop.Converters;

/// <summary>
/// Converts HasReport boolean to appropriate tooltip text for PDF button.
/// </summary>
public sealed class HasReportToTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool hasReport)
        {
            return hasReport ? "Open PDF report" : "No PDF report for this session";
        }
        return "No PDF report available";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
