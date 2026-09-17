namespace AzerothManager.Models;

/// <summary>
/// Continent affichable, avec son rectangle de projection.
///
/// Les bornes proviennent de WorldMapArea.dbc du client 3.3.5a, lignes dont areaID vaut
/// zéro — celles qui décrivent le continent entier. Quatre constantes vérifiées valent
/// mieux qu'un import de DBC pour un si petit jeu de données ; la table reste disponible
/// si l'on veut un jour descendre au niveau de la zone.
/// </summary>
public sealed record Continent(
    string Key, string Label, int MapId,
    float Left, float Right, float Top, float Bottom)
{
    public static readonly Continent[] All =
    [
        new("Azeroth",     "Royaumes de l'est", 0,    18172.0f, -22569.2f, 11176.3f, -15973.3f),
        new("Kalimdor",    "Kalimdor",          1,    17066.6f, -19733.2f, 12799.9f, -11733.3f),
        new("Expansion01", "Outreterre",        530,  12996.0f,  -4468.0f,  5821.4f,  -5821.4f),
        new("Northrend",   "Norfendre",         571,   9217.2f,  -8534.2f, 10593.4f,  -1240.9f)
    ];

    /// <summary>Taille réellement affichée par le client dans le damier de tuiles 1024×768.</summary>
    public const double Width = 1002;
    public const double Height = 668;

    /// <summary>
    /// Les axes du monde sont tournés par rapport à l'image : l'abscisse de la carte suit
    /// l'opposé de position_y, et l'ordonnée l'opposé de position_x.
    ///
    /// Le couple Left/Right borne position_y et le couple Top/Bottom borne position_x —
    /// l'inverse de ce que les noms laissent croire. Deux vérifications le confirment :
    /// le rapport des amplitudes (40741 / 27150 = 1,501) reproduit celui de l'image
    /// (1002 / 668 = 1,500), et le centre d'une zone calculé depuis son propre rectangle
    /// correspond bien aux positions réelles de ses joueurs.
    /// </summary>
    public (double X, double Y) Project(float worldX, float worldY) =>
        ((Left - worldY) / (Left - Right) * Width,
         (Top - worldX) / (Top - Bottom) * Height);

    public static Continent? ForMap(int mapId) => All.FirstOrDefault(c => c.MapId == mapId);
}

/// <summary>Joueur connecté, projeté sur la carte de son continent.</summary>
public sealed record PlayerPosition(
    int Guid, string Name, int Level, int Race, int Class,
    int MapId, int ZoneId, string ZoneName,
    float WorldX, float WorldY,
    double X, double Y)
{
    public string RaceName => GameReference.RaceName(Race);
    public string ClassName => GameReference.ClassName(Class);
    public string FactionName => GameReference.FactionName(Race);
    public bool IsAlliance => FactionName == "Alliance";

    /// <summary>Position du marqueur, décalée d'un rayon pour centrer la pastille.</summary>
    public double MarkerX => X - 5;
    public double MarkerY => Y - 5;

    public string Tooltip =>
        $"{Name} — niveau {Level} {RaceName} {ClassName}\n{ZoneName}\n({WorldX:0}, {WorldY:0})";
}

/// <summary>
/// Population d'une zone, projetée au centre de celle-ci. Le détail des joueurs n'est
/// pas repris : seuls les effectifs par faction comptent à cette échelle.
/// </summary>
public sealed record ZonePopulation(
    int ZoneId, string ZoneName, int MapId, int Alliance, int Horde, double X, double Y)
{
    public int Total => Alliance + Horde;
    public string Counts => $"{Alliance} · {Horde}";
    public bool AllianceMajority => Alliance >= Horde;

    /// <summary>Diamètre croissant avec l'effectif, mais plafonné pour rester lisible.</summary>
    public double Size => Math.Min(46, 20 + Math.Sqrt(Total) * 6);

    public double MarkerX => X - Size / 2;
    public double MarkerY => Y - Size / 2;

    public string Tooltip => $"""
        {ZoneName}
        {Alliance} Alliance · {Horde} Horde
        """;
}
