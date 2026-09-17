namespace AzerothManager.Models;

/// <summary>Personnage rattaché à un compte, pour la fiche du §9.</summary>
public sealed record AccountCharacter(
    int Guid, string Name, int Level, int Race, int Class, long Money, bool Online)
{
    public string MoneyText => ItemReference.Money(Money);
    public string RaceName => GameReference.RaceName(Race);
    public string ClassName => GameReference.ClassName(Class);
}

/// <summary>
/// Compte de jeu, consolidé depuis auth.account, account_access et account_banned.
/// Le niveau GM est défini par royaume dans account_access, et non globalement (§9).
/// </summary>
public sealed record AccountSummary(
    int Id,
    string Username,
    string Email,
    int Expansion,
    string LastIp,
    DateTime? LastLogin,
    DateTime? JoinDate,
    bool Online,
    bool Locked,
    long MuteTime,
    int? GmLevel,
    int? RealmId,
    int CharacterCount,
    bool Banned,
    string BanReason,
    DateTime? BanDate,
    DateTime? UnbanDate)
{
    public string ExpansionName => Expansion switch
    {
        0 => "Classic",
        1 => "Burning Crusade",
        2 => "Wrath of the Lich King",
        _ => Expansion.ToString()
    };

    public string GmLevelText => GmLevel is null or 0
        ? "Joueur"
        : $"Niveau {GmLevel}" + (RealmId is { } r && r >= 0 ? $" (royaume {r})" : " (tous royaumes)");

    /// <summary>Convention AzerothCore : une date de levée égale à la date de sanction vaut bannissement définitif.</summary>
    public bool PermanentBan => Banned && BanDate is { } b && UnbanDate is { } u && b == u;

    public string BanText => !Banned
        ? "—"
        : PermanentBan
            ? $"Définitif — {BanReason}"
            : $"Jusqu'au {UnbanDate:dd/MM/yyyy HH:mm} — {BanReason}";

    public bool Muted => MuteTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public string StateText =>
        Banned ? "Banni" : Muted ? "Muet" : Locked ? "Verrouillé" : Online ? "En ligne" : "—";

    public string LastLoginText => LastLogin?.ToString("dd/MM/yyyy HH:mm") ?? "jamais";
}

/// <summary>Libellés de races et de classes 3.3.5a.</summary>
public static class GameReference
{
    public static string RaceName(int r) => r switch
    {
        1 => "Humain", 2 => "Orc", 3 => "Nain", 4 => "Elfe de la nuit", 5 => "Mort-vivant",
        6 => "Tauren", 7 => "Gnome", 8 => "Troll", 10 => "Elfe de sang", 11 => "Draeneï",
        _ => r.ToString()
    };

    public static string ClassName(int c) => c switch
    {
        1 => "Guerrier", 2 => "Paladin", 3 => "Chasseur", 4 => "Voleur", 5 => "Prêtre",
        6 => "Chevalier de la mort", 7 => "Chaman", 8 => "Mage", 9 => "Démoniste",
        11 => "Druide",
        _ => c.ToString()
    };

    /// <summary>Alliance pour les races 1, 3, 4, 7 et 11 ; Horde pour les autres.</summary>
    public static string FactionName(int race) =>
        race is 1 or 3 or 4 or 7 or 11 ? "Alliance" : "Horde";
}
