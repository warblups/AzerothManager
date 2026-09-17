using AzerothManager.Models;

namespace AzerothManager.Services;

/// <summary>Pièce jointe d'un courrier à envoyer.</summary>
public sealed record MailAttachment(int Entry, int Count, string Name, int Quality, int DisplayId)
{
    public string Text => Count > 1 ? $"{Name} ×{Count}" : Name;
}

public sealed record MailSendReport(int Sent, int Failed, string Message);

/// <summary>
/// Courrier en jeu (§9). Canal imposé : la commande GM.
///
/// Insérer dans characters.mail à la main supposerait de créer aussi les lignes
/// d'item_instance des pièces jointes et d'en gérer les identifiants — fragile pour
/// aucun gain. Le serveur le fait correctement.
///
/// Trois commandes distinctes, toutes Console::Yes donc disponibles par SOAP :
///   .send mail   &lt;joueur&gt; "sujet" "texte"
///   .send items  &lt;joueur&gt; "sujet" "texte" id[:qté] …   (12 pièces jointes au maximum)
///   .send money  &lt;joueur&gt; "sujet" "texte" &lt;montant&gt;
///
/// L'or et les objets ne peuvent pas voyager dans le même courrier : le module envoie
/// alors deux courriers, et le dit.
/// </summary>
public sealed class MailService
{
    public const int MaxAttachments = 12;

    private readonly GmCommandService _gm;
    private readonly MySqlService _mySql;

    public MailService(GmCommandService gm, MySqlService mySql)
    {
        _gm = gm;
        _mySql = mySql;
    }

    /// <summary>
    /// Sujet et texte sont des QuotedString côté serveur : les guillemets internes
    /// casseraient l'analyse de la commande, on les neutralise.
    /// </summary>
    private static string Quote(string value) =>
        "\"" + (value ?? "").Replace("\"", "'").ReplaceLineEndings(" ").Trim() + "\"";

    public async Task<MailSendReport> SendAsync(
        string character, string subject, string body,
        IReadOnlyList<MailAttachment> attachments, long copper,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(character))
            return new MailSendReport(0, 1, "Destinataire manquant.");

        if (attachments.Count > MaxAttachments)
            return new MailSendReport(0, 1, $"Le serveur limite un courrier à {MaxAttachments} pièces jointes.");

        var sent = 0;
        var problems = new List<string>();

        // Courrier avec pièces jointes, ou courrier simple.
        if (attachments.Count > 0)
        {
            var items = string.Join(' ', attachments.Select(a => a.Count > 1 ? $"{a.Entry}:{a.Count}" : $"{a.Entry}"));
            var r = await _gm.ExecuteAsync(
                $"send items {character} {Quote(subject)} {Quote(body)} {items}", ct);
            if (r.Success) sent++; else problems.Add(r.Output);
        }
        else if (copper <= 0)
        {
            var r = await _gm.ExecuteAsync($"send mail {character} {Quote(subject)} {Quote(body)}", ct);
            if (r.Success) sent++; else problems.Add(r.Output);
        }

        // L'or exige son propre courrier.
        if (copper > 0)
        {
            var r = await _gm.ExecuteAsync(
                $"send money {character} {Quote(subject)} {Quote(body)} {copper}", ct);
            if (r.Success) sent++; else problems.Add(r.Output);
        }

        return problems.Count == 0
            ? new MailSendReport(sent, 0, sent > 1
                ? $"{character} : deux courriers envoyés, l'or ne pouvant accompagner des objets."
                : $"{character} : courrier envoyé.")
            : new MailSendReport(sent, problems.Count, $"{character} : " + string.Join(" · ", problems));
    }

    /// <summary>
    /// Envoi en masse. Une commande par destinataire : le serveur n'offre rien de groupé,
    /// et boucler reste préférable à une insertion SQL fragile.
    /// </summary>
    public async Task<MailSendReport> SendBulkAsync(
        IReadOnlyList<string> characters, string subject, string body,
        IReadOnlyList<MailAttachment> attachments, long copper,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sent = 0;
        var failed = 0;
        var problems = new List<string>();

        foreach (var character in characters)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Envoi à {character}…");

            var report = await SendAsync(character, subject, body, attachments, copper, ct);
            sent += report.Sent;
            if (report.Failed > 0)
            {
                failed += report.Failed;
                if (problems.Count < 5) problems.Add(report.Message);
            }
        }

        var message = $"{sent} courrier(s) envoyé(s) à {characters.Count} destinataire(s).";
        if (failed > 0) message += $" {failed} échec(s) : " + string.Join(" · ", problems);
        return new MailSendReport(sent, failed, message);
    }

    /// <summary>Destinataires possibles : tous les personnages, hors supprimés.</summary>
    public async Task<IReadOnlyList<string>> AllCharacterNamesAsync(int minLevel = 1, CancellationToken ct = default)
    {
        var names = new List<string>();
        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT name FROM characters
            WHERE deleteDate IS NULL AND level >= @lvl
            ORDER BY name
            """;
        cmd.Parameters.AddWithValue("@lvl", minLevel);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) names.Add(rd.GetString(0));
        return names;
    }

    /// <summary>
    /// Boîte de réception d'un personnage. Lecture en SQL : le courrier déjà envoyé
    /// n'est plus adressable par commande.
    /// </summary>
    public async Task<IReadOnlyList<MailMessage>> InboxAsync(string character, CancellationToken ct = default)
    {
        var list = new List<MailMessage>();
        await using var cnx = await _mySql.OpenAsync(MySqlService.Db.Characters, ct);

        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.subject, m.body, m.money, m.cod, m.has_items,
                   m.deliver_time, m.expire_time, m.checked,
                   (SELECT COUNT(*) FROM mail_items mi WHERE mi.mail_id = m.id) AS jointes,
                   (SELECT GROUP_CONCAT(ii.itemEntry) FROM mail_items mi
                      JOIN item_instance ii ON ii.guid = mi.item_guid
                    WHERE mi.mail_id = m.id) AS entries
            FROM mail m
            JOIN characters c ON c.guid = m.receiver
            WHERE c.name = @name
            ORDER BY m.deliver_time DESC
            LIMIT 200
            """;
        cmd.Parameters.AddWithValue("@name", character);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new MailMessage(
                Id: rd.GetInt32(0),
                Subject: rd.IsDBNull(1) ? "" : rd.GetString(1),
                Body: rd.IsDBNull(2) ? "" : rd.GetString(2),
                Money: rd.GetInt64(3),
                Cod: rd.GetInt64(4),
                AttachmentCount: rd.GetInt32(9),
                AttachmentEntries: rd.IsDBNull(10) ? "" : rd.GetString(10),
                DeliverTime: DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(6)).LocalDateTime,
                ExpireTime: DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(7)).LocalDateTime,
                Read: rd.GetInt32(8) != 0));
        }
        return list;
    }
}
