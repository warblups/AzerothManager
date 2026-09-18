namespace AzerothManager.Models;

public enum SpawnKind
{
    Creature,
    GameObject
}

/// <summary>
/// Spawn placé dans le monde, depuis world.creature ou world.gameobject.
///
/// Les deux tables partagent l'essentiel — guid, id du modèle, carte, position, orientation,
/// temps de réapparition, commentaire —, ce qui permet un éditeur unique.
///
/// <see cref="ZoneId"/> et <see cref="ZoneName"/> sont **déduits de la position**, pas lus en
/// base : `creature.zoneId` vaut 0 dans 149 887 lignes sur 155 085 sur le serveur de
/// référence. C'est une colonne de cache, pas une donnée de placement.
/// </summary>
public sealed record Spawn(
    SpawnKind Kind,
    int Guid,
    int Entry,
    string Name,
    string NameEnglish,
    int MapId,
    string MapName,
    int ZoneId,
    string ZoneName,
    float X,
    float Y,
    float Z,
    float Orientation,
    int SpawnTimeSecs,
    string Comment)
{
    public string KindText => Kind == SpawnKind.Creature ? "PNJ" : "Objet";
    public string PositionText => $"{X:0}, {Y:0}, {Z:0}";
    public string OrientationText => $"{Orientation:0.00}";
    public string ZoneText => string.IsNullOrEmpty(ZoneName) ? "—" : ZoneName;

    /// <summary>Le nom anglais n'est montré que s'il diffère : sinon la colonne est du bruit.</summary>
    public string NameEnglishText => NameEnglish == Name ? "" : NameEnglish;

    public string SpawnTimeText
    {
        get
        {
            if (SpawnTimeSecs <= 0) return "—";
            var span = TimeSpan.FromSeconds(SpawnTimeSecs);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours} h {span.Minutes} min"
                : span.TotalMinutes >= 1
                    ? $"{(int)span.TotalMinutes} min"
                    : $"{SpawnTimeSecs} s";
        }
    }

    public string CommentText => string.IsNullOrWhiteSpace(Comment) ? "—" : Comment;

    /// <summary>Nom de la table, pour les requêtes, les messages et la journalisation.</summary>
    public string Table => Kind == SpawnKind.Creature ? "creature" : "gameobject";
}

/// <summary>
/// Critères de recherche. La pagination est côté SQL : `creature` compte 155 085 lignes et
/// `gameobject` 97 426, jamais chargées en mémoire (cf. « Volumes réels »).
/// </summary>
public sealed record SpawnFilter(
    SpawnKind Kind,
    string? Text = null,
    int? MapId = null,
    int Page = 0,
    int PageSize = 200);

public sealed record SpawnPage(IReadOnlyList<Spawn> Spawns, int Total, int PageCount);
