namespace AzerothManager.Models;

public sealed record ItemStat(int Type, int Value)
{
    public string Label => ItemReference.StatName(Type);
    public string Text => (Value > 0 ? "+" : "") + Value + " " + Label;
}

public sealed record ItemSpell(int SpellId, int Trigger)
{
    public string Text => $"{ItemReference.SpellTrigger(Trigger)} : sort {SpellId}";
}

/// <summary>Fiche complète d'un objet, chargée à la demande sur un seul entry.</summary>
public sealed record ItemDetail(
    ItemSummary Summary,
    int Bonding,
    int Durability,
    int Armor,
    float DamageMin,
    float DamageMax,
    int DamageType,
    int Delay,
    string Description,
    IReadOnlyList<ItemStat> Stats,
    IReadOnlyList<ItemSpell> Spells,
    IReadOnlyList<int> Sockets,
    int HolyRes, int FireRes, int NatureRes, int FrostRes, int ShadowRes, int ArcaneRes)
{
    public bool IsWeapon => Delay > 0 && DamageMax > 0;

    public string DamageText => IsWeapon
        ? $"{DamageMin:0} - {DamageMax:0} dégâts {ItemReference.DamageSchool(DamageType)}"
        : "";

    public string SpeedText => Delay > 0 ? $"Vitesse {Delay / 1000.0:0.00}" : "";

    /// <summary>Dégâts par seconde, calculés comme en jeu : moyenne des dégâts divisée par la vitesse.</summary>
    public string DpsText => IsWeapon
        ? $"({(DamageMin + DamageMax) / 2.0 / (Delay / 1000.0):0.0} dégâts par seconde)"
        : "";

    public string BondingText => ItemReference.Bonding(Bonding);

    public string ResistanceText
    {
        get
        {
            var parts = new List<string>();
            if (HolyRes != 0) parts.Add($"+{HolyRes} résistance au Sacré");
            if (FireRes != 0) parts.Add($"+{FireRes} résistance au Feu");
            if (NatureRes != 0) parts.Add($"+{NatureRes} résistance à la Nature");
            if (FrostRes != 0) parts.Add($"+{FrostRes} résistance au Givre");
            if (ShadowRes != 0) parts.Add($"+{ShadowRes} résistance à l'Ombre");
            if (ArcaneRes != 0) parts.Add($"+{ArcaneRes} résistance aux Arcanes");
            return string.Join("\n", parts);
        }
    }

    public bool HasResistance => ResistanceText.Length > 0;
    public bool HasSockets => Sockets.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string SocketsText => string.Join(", ", Sockets.Select(ItemReference.SocketColor));
}
