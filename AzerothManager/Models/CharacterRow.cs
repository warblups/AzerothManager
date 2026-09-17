namespace AzerothManager.Models;

/// <summary>Destination de téléportation, lue dans world.game_tele.</summary>
public sealed record TeleportDestination(int Id, string Name, int MapId, float X, float Y, float Z)
{
    public override string ToString() => Name;
}

/// <summary>
/// Personnage, tel que listé par le module du même nom. Distinct de CharacterSheet,
/// qui sert l'armurerie : ici on veut de quoi chercher et agir, pas une fiche complète.
/// </summary>
public sealed record CharacterRow(
    int Guid,
    string Name,
    int AccountId,
    string AccountName,
    int Level,
    int Race,
    int Class,
    int Gender,
    long Money,
    int MapId,
    int ZoneId,
    string ZoneName,
    bool Online,
    int AtLogin,
    int TotalTimeSeconds,
    DateTime? LogoutTime)
{
    public string RaceName => GameReference.RaceName(Race);
    public string ClassName => GameReference.ClassName(Class);
    public string FactionName => GameReference.FactionName(Race);
    public string GenderName => Gender == 0 ? "M" : "F";
    public string MoneyText => ItemReference.Money(Money, dashWhenZero: false);
    public string StateText => Online ? "En ligne" : "—";
    public string LogoutText => LogoutTime?.ToString("dd/MM/yyyy HH:mm") ?? "jamais";

    public string PlayedText
    {
        get
        {
            var span = TimeSpan.FromSeconds(TotalTimeSeconds);
            return span.TotalDays >= 1
                ? $"{(int)span.TotalDays} j {span.Hours} h"
                : $"{(int)span.TotalHours} h {span.Minutes} min";
        }
    }

    /// <summary>Drapeaux en attente à la prochaine connexion, décodés depuis at_login.</summary>
    public string AtLoginText
    {
        get
        {
            if (AtLogin == 0) return "—";
            var flags = new List<string>();
            if ((AtLogin & 0x01) != 0) flags.Add("renommage");
            if ((AtLogin & 0x02) != 0) flags.Add("sorts réinitialisés");
            if ((AtLogin & 0x04) != 0) flags.Add("talents réinitialisés");
            if ((AtLogin & 0x08) != 0) flags.Add("customisation");
            if ((AtLogin & 0x10) != 0) flags.Add("talents du familier");
            if ((AtLogin & 0x20) != 0) flags.Add("première connexion");
            if ((AtLogin & 0x40) != 0) flags.Add("changement de faction");
            if ((AtLogin & 0x80) != 0) flags.Add("changement de race");
            if ((AtLogin & 0x100) != 0) flags.Add("points d'attribut réinitialisés");
            if ((AtLogin & 0x200) != 0) flags.Add("arène réinitialisée");
            return string.Join(", ", flags);
        }
    }

    public bool HasPendingFlags => AtLogin != 0;
}
