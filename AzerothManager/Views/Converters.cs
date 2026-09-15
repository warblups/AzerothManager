using System.Globalization;
using System.Windows.Data;

namespace AzerothManager.Views;

/// <summary>Inverse un booléen, pour désactiver un contrôle pendant une opération en cours.</summary>
public sealed class NotBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
