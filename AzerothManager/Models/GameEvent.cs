namespace AzerothManager.Models;

/// <summary>
/// Événement mondial de `world.game_event` (§9).
///
/// **`Occurrence` et `Length` sont en minutes** : `GameEventMgr` les multiplie par `MINUTE`.
/// Sur le serveur de référence, un événement annuel porte 525 600 (une année de minutes) et
/// une fête de deux semaines 20 160. Les lire en secondes donnerait des périodes absurdes.
///
/// <see cref="IsActive"/> ne se déduit pas de la base : l'état vivant est tenu par
/// `GameEventMgr` en mémoire, et un événement lancé à la main n'y laisse aucune trace. Il
/// vient de `.event activelist`.
/// </summary>
public sealed record GameEventInfo(
    int Id,
    string Description,
    DateTime? Start,
    DateTime? End,
    long OccurrenceMinutes,
    long LengthMinutes,
    int HolidayId,
    int HolidayStage,
    bool WorldEvent,
    int Announce,
    int SpawnsAdded,
    int SpawnsRemoved,
    bool IsActive)
{
    public string Name => string.IsNullOrWhiteSpace(Description) ? $"Événement {Id}" : Description;

    public string OccurrenceText => Duration.FromMinutes(OccurrenceMinutes);
    public string LengthText => Duration.FromMinutes(LengthMinutes);

    public string ActiveText => IsActive ? "actif" : "";

    /// <summary>
    /// `announce` : 1 annonce toujours, 0 jamais, 2 suit `Event.Announce` de la
    /// configuration — vérifié dans `ApplyNewEvent`.
    /// </summary>
    public string AnnounceText => Announce switch
    {
        0 => "jamais",
        1 => "toujours",
        2 => "selon la config",
        _ => Announce.ToString()
    };

    public string HolidayText => HolidayId == 0 ? "" : HolidayId.ToString();

    /// <summary>
    /// Vrai quand `GameEventMgr::SetHolidayEventTime` écrasera la période et la durée avec
    /// celles de `Holidays.dbc`. La fonction sort immédiatement si `holidayStage` vaut 0 :
    /// **c'est l'étape, pas l'identifiant de fête, qui déclenche l'écrasement**.
    ///
    /// Conséquence pratique : modifier `occurence` ou `length` en base pour un tel événement
    /// ne sert à rien, le DBC reprenant la main à chaque chargement. C'est ce qui explique
    /// qu'un Brewfest à 15 jours en base soit annoncé à 16 jours par `.event info`.
    /// </summary>
    public bool ScheduleOverriddenByHoliday => HolidayStage != 0;

    public string ScheduleSourceText => ScheduleOverriddenByHoliday ? "Holidays.dbc" : "base";
    public string WorldEventText => WorldEvent ? "mondial" : "";

    /// <summary>
    /// Fenêtre déclarée en base. Beaucoup d'événements ont une date de début à zéro : leur
    /// planification est portée par le seul couple période/durée.
    /// </summary>
    public string WindowText => Start is null
        ? "—"
        : End is null || End <= Start
            ? Start.Value.ToString("dd/MM/yyyy")
            : $"{Start.Value:dd/MM/yyyy} → {End.Value:dd/MM/yyyy}";

    /// <summary>
    /// Ce que l'événement change dans le monde. Un `eventEntry` **négatif** dans
    /// `game_event_creature` ou `game_event_gameobject` désigne un spawn **retiré** pendant
    /// l'événement : `ApplyNewEvent` appelle `GameEventUnspawn(-eventId)`. Les compter avec
    /// les autres laisserait croire que l'événement ajoute ce qu'il enlève.
    /// </summary>
    public string SpawnsText => (SpawnsAdded, SpawnsRemoved) switch
    {
        (0, 0) => "—",
        (var a, 0) => $"+{a}",
        (0, var r) => $"−{r}",
        var (a, r) => $"+{a} / −{r}"
    };
}

/// <summary>Mise en forme d'une durée exprimée en minutes, comme `game_event` les stocke.</summary>
public static class Duration
{
    private const long MinutesPerYear = 525600;

    public static string FromMinutes(long minutes)
    {
        if (minutes <= 0) return "—";

        if (minutes % MinutesPerYear == 0)
        {
            var years = minutes / MinutesPerYear;
            return years == 1 ? "1 an" : $"{years} ans";
        }

        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var rest = minutes % 60;

        var parts = new List<string>();
        if (days > 0) parts.Add($"{days} j");
        if (hours > 0) parts.Add($"{hours} h");
        if (rest > 0) parts.Add($"{rest} min");
        return string.Join(" ", parts);
    }
}
