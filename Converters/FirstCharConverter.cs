using System.Globalization;
using System.Windows.Data;

namespace RecompVault.Converters;

public class FirstCharConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value?.ToString() ?? "";
        return string.IsNullOrWhiteSpace(s) ? "?" : s.Substring(0, 1).ToUpperInvariant();
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}