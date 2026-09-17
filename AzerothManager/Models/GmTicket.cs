namespace AzerothManager.Models;

/// <summary>
/// Ticket créé en jeu par un joueur (characters.gm_ticket).
/// Les colonnes de position permettent de se rendre sur le lieu signalé.
/// </summary>
public sealed record GmTicket(
    int Id,
    int Type,
    int PlayerGuid,
    string PlayerName,
    string Description,
    DateTime CreateTime,
    DateTime LastModifiedTime,
    int MapId,
    string MapName,
    float X, float Y, float Z,
    string AssignedTo,
    string ClosedBy,
    string Comment,
    string Response,
    bool Completed,
    bool Escalated,
    bool Viewed,
    bool NeedMoreHelp,
    bool PlayerOnline)
{
    /// <summary>
    /// C'est la colonne type qui fait foi, et non closedBy : un ticket clos depuis la
    /// console garde closedBy à zéro. IsClosed() côté serveur teste type != OPEN.
    /// </summary>
    public bool Open => Type == 0;

    public bool Assigned => !string.IsNullOrEmpty(AssignedTo);

    public string StateText => Type switch
    {
        1 => "Clos",
        2 => "Personnage supprimé",
        _ => Completed ? "Traité"
           : Escalated ? "Escaladé"
           : NeedMoreHelp ? "Relancé"
           : Assigned ? "Assigné"
           : "Ouvert"
    };

    public string AssignedText => Assigned ? AssignedTo : "—";
    public string ClosedByText => string.IsNullOrEmpty(ClosedBy) ? "—" : ClosedBy;
    public string CreatedText => CreateTime.ToString("dd/MM/yyyy HH:mm");
    public string PositionText => $"{MapName} ({X:0}, {Y:0}, {Z:0})";
    public string OnlineText => PlayerOnline ? "En ligne" : "—";

    /// <summary>Première ligne de la description, pour la liste.</summary>
    public string Summary
    {
        get
        {
            var text = (Description ?? "").ReplaceLineEndings(" ").Trim();
            return text.Length <= 90 ? text : text[..90] + "…";
        }
    }
}

/// <summary>
/// Personnage supprimé, encore récupérable : AzerothCore conserve la ligne dans
/// characters et bascule le nom dans deleteInfos_Name (§9).
/// </summary>
public sealed record DeletedCharacter(
    int Guid,
    string Name,
    int AccountId,
    string AccountName,
    DateTime DeleteDate,
    int Level,
    int Race,
    int Class)
{
    public string RaceName => GameReference.RaceName(Race);
    public string ClassName => GameReference.ClassName(Class);
    public string DeletedText => DeleteDate.ToString("dd/MM/yyyy HH:mm");
    public int DaysSinceDeletion => (int)(DateTime.Now - DeleteDate).TotalDays;

    /// <summary>Le compte d'origine doit exister pour que la restauration aboutisse.</summary>
    public bool AccountMissing => string.IsNullOrEmpty(AccountName);
}
