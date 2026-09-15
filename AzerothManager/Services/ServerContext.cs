using AzerothManager.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzerothManager.Services;

/// <summary>
/// Profil de serveur actif, unique pour toute l'application (§13).
/// En changer doit reconfigurer tous les services : ceux-ci lisent le contexte
/// à chaque opération plutôt que de mettre en cache une chaîne de connexion.
/// </summary>
public partial class ServerContext : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActive))]
    [NotifyPropertyChangedFor(nameof(IsProduction))]
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private ServerProfile? _active;

    /// <summary>Dernière latence MySQL mesurée, affichée en barre d'état.</summary>
    [ObservableProperty] private long _latencyMs;

    [ObservableProperty] private string _status = "Aucun serveur actif";

    public bool HasActive => Active is not null;
    public bool IsProduction => Active?.IsProduction ?? false;
    public bool IsReadOnly => Active?.ReadOnly ?? false;
    public string DisplayName => Active?.Name ?? "—";

    /// <summary>Profil actif, ou exception explicite. Évite les NullReference dans les services.</summary>
    public ServerProfile RequireActive() =>
        Active ?? throw new InvalidOperationException(
            "Aucun serveur actif. Sélectionnez un profil dans Configuration des serveurs.");
}
