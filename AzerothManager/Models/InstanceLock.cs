namespace AzerothManager.Models;

/// <summary>
/// Verrou d'instance : un personnage lié à une sauvegarde d'instance (§9).
///
/// `character_instance` ne porte que le lien ; la carte, la difficulté et l'heure de
/// réinitialisation vivent dans `instance`, d'où la jointure.
///
/// **La difficulté est laissée en chiffre.** En 3.3.5a, 0 et 1 valent « normal » et
/// « héroïque » pour un donjon, mais « 10 joueurs » et « 25 joueurs » pour un raid, et rien
/// dans ces tables ne dit lequel : `Map.dbc` porterait le type d'instance, que l'import DBC
/// du projet ne conserve pas. Deviner se tromperait sur les raids sans mode héroïque —
/// mieux vaut le chiffre et une légende.
/// </summary>
public sealed record InstanceLock(
    int CharacterGuid,
    string CharacterName,
    int Level,
    int InstanceId,
    int MapId,
    string MapName,
    int Difficulty,
    DateTime? ResetAt,
    bool Permanent,
    bool Extended,
    long CompletedEncounters)
{
    public string CharacterText => string.IsNullOrEmpty(CharacterName)
        ? $"guid {CharacterGuid}"
        : CharacterName;

    public string MapText => string.IsNullOrEmpty(MapName) ? $"carte {MapId}" : MapName;

    public string ResetText => ResetAt is null ? "—" : ResetAt.Value.ToString("dd/MM/yyyy HH:mm");

    /// <summary>Temps restant avant la réinitialisation, ou « expiré » si l'heure est passée.</summary>
    public string RemainingText
    {
        get
        {
            if (ResetAt is null) return "—";
            var left = ResetAt.Value - DateTime.Now;
            if (left <= TimeSpan.Zero) return "expiré";

            var parts = new List<string>();
            if (left.Days > 0) parts.Add($"{left.Days} j");
            if (left.Hours > 0) parts.Add($"{left.Hours} h");
            if (left.Days == 0 && left.Minutes > 0) parts.Add($"{left.Minutes} min");
            return parts.Count == 0 ? "< 1 min" : string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Un verrou permanent est celui d'un raid : il tient jusqu'à la réinitialisation
    /// hebdomadaire. Un verrou temporaire est celui d'un donjon, effacé à la sortie du groupe.
    /// </summary>
    public string KindText => Permanent ? "permanent" : "temporaire";

    /// <summary>`extended` : le joueur a demandé le report de la réinitialisation d'une semaine.</summary>
    public string ExtendedText => Extended ? "prolongé" : "";

    /// <summary>Masque des rencontres déjà vaincues dans cette sauvegarde.</summary>
    public string EncountersText => CompletedEncounters == 0
        ? "—"
        : $"{System.Numerics.BitOperations.PopCount((ulong)CompletedEncounters)} vaincu(s)";
}

/// <summary>
/// Réinitialisation programmée d'une carte, depuis `instance_reset`. Une ligne par couple
/// carte et difficulté — 73 sur le serveur de référence.
/// </summary>
public sealed record InstanceResetSchedule(int MapId, string MapName, int Difficulty, DateTime? ResetAt)
{
    public string MapText => string.IsNullOrEmpty(MapName) ? $"carte {MapId}" : MapName;
    public string ResetText => ResetAt?.ToString("dd/MM/yyyy HH:mm") ?? "—";
}
