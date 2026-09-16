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

/// <summary>Traduit la qualité d'un objet en sa couleur du jeu, pour une lecture immédiate.</summary>
public sealed class QualityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var quality = value is int q ? q : 1;
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
            .ConvertFromString(AzerothManager.Models.ItemReference.QualityColor(quality))!;
        return new System.Windows.Media.SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
