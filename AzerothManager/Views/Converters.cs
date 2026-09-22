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

/// <summary>Résout le displayid d'un objet en son icône, depuis le cache local.</summary>
public sealed class DisplayIdToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int displayId ? AzerothManager.Services.GameClientService.Current?.Icon(displayId) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Couleur d'une ligne de journal selon son niveau. Mêmes teintes que l'onglet Logs
/// d'AzerothUpdater, pour que les deux applications se lisent de la même façon ; une action
/// de l'administrateur prend l'accent, afin de ressortir au milieu du journal du serveur.
/// </summary>
public sealed class LogSeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is AzerothManager.Models.LogLine line && line.IsAdmin
            ? "Accent"
            : value is AzerothManager.Models.LogLine l
                ? l.Severity switch
                  {
                      AzerothManager.Models.LogSeverity.Error => "Error",
                      AzerothManager.Models.LogSeverity.Warning => "Warning",
                      AzerothManager.Models.LogSeverity.Info => "Text",
                      AzerothManager.Models.LogSeverity.Debug => "Muted",
                      _ => "Text"
                  }
                : "Text";

        return System.Windows.Application.Current?.TryFindResource(key)
            ?? System.Windows.Media.Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
