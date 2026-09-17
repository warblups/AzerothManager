namespace AzerothManager.Models;

/// <summary>Objet équipé dans l'un des dix-neuf emplacements du personnage.</summary>
public sealed record EquippedItem(
    int Slot, int Entry, string Name, int Quality, int ItemLevel, int DisplayId)
{
    public string SlotName => EquipmentSlots.Name(Slot);
    public bool IsEmpty => Entry == 0;
    public string Display => IsEmpty ? "—" : Name;
    public string LevelText => IsEmpty ? "" : $"niveau {ItemLevel}";
}

/// <summary>Emplacements d'équipement, repris de l'énumération EquipmentSlots d'AzerothCore.</summary>
public static class EquipmentSlots
{
    public const int Count = 19;

    private static readonly string[] Names =
    [
        "Tête", "Cou", "Épaules", "Chemise", "Torse", "Taille", "Jambes", "Pieds",
        "Poignets", "Mains", "Doigt 1", "Doigt 2", "Bijou 1", "Bijou 2", "Dos",
        "Main droite", "Main gauche", "Distance", "Tabard"
    ];

    public static string Name(int slot) => slot >= 0 && slot < Names.Length ? Names[slot] : slot.ToString();
}

/// <summary>Statistiques lues dans character_stats, telles que le serveur les a enregistrées.</summary>
public sealed record CharacterStats(
    int MaxHealth, int MaxMana,
    int Strength, int Agility, int Stamina, int Intellect, int Spirit,
    int Armor,
    int ResHoly, int ResFire, int ResNature, int ResFrost, int ResShadow, int ResArcane,
    float BlockPct, float DodgePct, float ParryPct, float CritPct,
    float RangedCritPct, float SpellCritPct,
    int AttackPower, int RangedAttackPower, int SpellPower, int Resilience);

/// <summary>
/// Fiche d'armurerie (§12). Tout est lu en base et enrichi par les DBC importés :
/// aucun service externe, et les objets personnalisés sont couverts comme les autres.
/// </summary>
public sealed record CharacterSheet(
    int Guid,
    int AccountId,
    string AccountName,
    string Name,
    int Race,
    int Class,
    int Gender,
    int Level,
    long Money,
    long Xp,
    bool Online,
    int TotalTimeSeconds,
    int MapId,
    string MapName,
    int ZoneId,
    string ZoneName,
    int HonorPoints,
    int ArenaPoints,
    int TotalKills,
    string GuildName,
    string GuildRank,
    CharacterStats? Stats,
    IReadOnlyList<EquippedItem> Equipment,
    int AchievementCount)
{
    public string RaceName => GameReference.RaceName(Race);
    public string ClassName => GameReference.ClassName(Class);
    public string FactionName => GameReference.FactionName(Race);
    public string GenderName => Gender == 0 ? "Masculin" : "Féminin";
    public string MoneyText => ItemReference.Money(Money);
    public string LocationText => ZoneName == "—" ? MapName : $"{ZoneName} ({MapName})";
    public string GuildText => string.IsNullOrEmpty(GuildName) ? "Sans guilde" : $"{GuildName} — {GuildRank}";

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

    /// <summary>Niveau d'objet moyen de l'équipement porté, hors chemise et tabard.</summary>
    public string AverageItemLevel
    {
        get
        {
            var worn = Equipment
                .Where(e => !e.IsEmpty && e.Slot != 3 && e.Slot != 18)
                .ToList();
            return worn.Count == 0 ? "—" : ((int)worn.Average(e => e.ItemLevel)).ToString();
        }
    }
}
