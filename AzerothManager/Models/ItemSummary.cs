namespace AzerothManager.Models;

/// <summary>
/// Vue synthétique d'une ligne de world.item_template. Volontairement réduite aux
/// colonnes utiles à la recherche et à la sélection : la table compte plus de
/// cent trente colonnes, en charger l'intégralité pour lister n'aurait pas de sens.
/// </summary>
public sealed record ItemSummary(
    int Entry,
    string Name,
    string NameEn,
    int Quality,
    int ItemLevel,
    int RequiredLevel,
    int Class,
    int Subclass,
    int InventoryType,
    int DisplayId,
    long SellPrice,
    long BuyPrice,
    int Stackable)
{
    /// <summary>Vrai si le nom affiché est une traduction ; l'anglais reste utile pour les commandes GM et les sources externes.</summary>
    public bool IsTranslated => !string.Equals(Name, NameEn, StringComparison.Ordinal);

    public string QualityName => ItemReference.QualityName(Quality);
    public string ClassName => ItemReference.ClassName(Class);
    public string SlotName => ItemReference.InventoryTypeName(InventoryType);
    public string SellPriceText => ItemReference.Money(SellPrice);
    public string BuyPriceText => ItemReference.Money(BuyPrice);
}

/// <summary>Libellés et conversions propres à 3.3.5a.</summary>
public static class ItemReference
{
    public static string QualityName(int q) => q switch
    {
        0 => "Médiocre",
        1 => "Commun",
        2 => "Inhabituel",
        3 => "Rare",
        4 => "Épique",
        5 => "Légendaire",
        6 => "Artefact",
        7 => "Héritage",
        _ => q.ToString()
    };

    /// <summary>Couleurs de qualité du jeu, pour que la lecture soit immédiate.</summary>
    public static string QualityColor(int q) => q switch
    {
        0 => "#9d9d9d",
        1 => "#ffffff",
        2 => "#1eff00",
        3 => "#0070dd",
        4 => "#a335ee",
        5 => "#ff8000",
        6 => "#e6cc80",
        7 => "#e6cc80",
        _ => "#eaeaea"
    };

    public static string ClassName(int c) => c switch
    {
        0 => "Consommable",
        1 => "Conteneur",
        2 => "Arme",
        3 => "Gemme",
        4 => "Armure",
        5 => "Composant",
        6 => "Projectile",
        7 => "Marchandise",
        8 => "Générique",
        9 => "Recette",
        10 => "Argent",
        11 => "Carquois",
        12 => "Quête",
        13 => "Clé",
        14 => "Permanent",
        15 => "Divers",
        16 => "Glyphe",
        _ => c.ToString()
    };

    public static string InventoryTypeName(int t) => t switch
    {
        0 => "—",
        1 => "Tête",
        2 => "Cou",
        3 => "Épaules",
        4 => "Chemise",
        5 => "Torse",
        6 => "Taille",
        7 => "Jambes",
        8 => "Pieds",
        9 => "Poignets",
        10 => "Mains",
        11 => "Doigt",
        12 => "Bijou",
        13 => "Une main",
        14 => "Bouclier",
        15 => "Arc",
        16 => "Dos",
        17 => "Deux mains",
        18 => "Sac",
        19 => "Tabard",
        20 => "Robe",
        21 => "Main droite",
        22 => "Main gauche",
        23 => "Tenue en main gauche",
        24 => "Munition",
        25 => "Projectile",
        26 => "Arme à distance",
        28 => "Relique",
        _ => t.ToString()
    };

    /// <summary>Convertit un prix en pièces de cuivre vers la notation or / argent / cuivre.</summary>
    public static string Money(long copper)
    {
        if (copper <= 0) return "—";
        var g = copper / 10000;
        var s = copper % 10000 / 100;
        var c = copper % 100;
        return g > 0 ? $"{g}o {s}a {c}c" : s > 0 ? $"{s}a {c}c" : $"{c}c";
    }
}
