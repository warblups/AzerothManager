namespace AzerothManager.Models;

/// <summary>Nature de la cible sanctionnée : les trois tables ont des clés différentes.</summary>
public enum SanctionKind
{
    Account,
    Character,
    Ip,
    Mute
}

/// <summary>
/// Sanction consolidée depuis auth.account_banned, auth.ip_banned,
/// characters.character_banned et auth.account_muted (§9).
///
/// Les quatre tables ont des colonnes proches mais pas identiques : ce type les ramène
/// à une forme commune pour qu'un historique unique puisse les présenter côte à côte.
/// </summary>
public sealed record Sanction(
    SanctionKind Kind,
    string Target,
    DateTime Date,
    DateTime? Until,
    string By,
    string Reason,
    bool Active)
{
    public string KindText => Kind switch
    {
        SanctionKind.Account => "Compte",
        SanctionKind.Character => "Personnage",
        SanctionKind.Ip => "Adresse IP",
        SanctionKind.Mute => "Mute",
        _ => Kind.ToString()
    };

    /// <summary>
    /// Convention AzerothCore : une date de levée égale à la date de sanction vaut
    /// bannissement définitif.
    /// </summary>
    public bool Permanent => Until is { } u && u == Date;

    public bool Expired => !Permanent && Until is { } u && u <= DateTime.Now;

    public string StateText => !Active ? "Levée" : Expired ? "Expirée" : "En cours";

    public string DurationText => Permanent
        ? "Définitif"
        : Until is { } u ? $"jusqu'au {u:dd/MM/yyyy HH:mm}" : "—";

    public string DateText => Date.ToString("dd/MM/yyyy HH:mm");
    public string ByText => string.IsNullOrWhiteSpace(By) ? "—" : By;
    public string ReasonText => string.IsNullOrWhiteSpace(Reason) ? "—" : Reason;
}
