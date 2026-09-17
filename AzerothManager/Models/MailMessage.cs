namespace AzerothManager.Models;

/// <summary>Courrier lu dans characters.mail, avec le décompte de ses pièces jointes.</summary>
public sealed record MailMessage(
    int Id,
    string Subject,
    string Body,
    long Money,
    long Cod,
    int AttachmentCount,
    string AttachmentEntries,
    DateTime DeliverTime,
    DateTime ExpireTime,
    bool Read)
{
    public string MoneyText => Money > 0 ? ItemReference.Money(Money) : "—";
    public string CodText => Cod > 0 ? ItemReference.Money(Cod) : "—";
    public string AttachmentText => AttachmentCount == 0 ? "—" : $"{AttachmentCount} objet(s)";
    public string StateText => Read ? "Lu" : "Non lu";
    public string DeliverText => DeliverTime.ToString("dd/MM/yyyy HH:mm");
    public string ExpireText => ExpireTime.ToString("dd/MM/yyyy");

    /// <summary>Un courrier non encore distribué reste invisible pour le joueur.</summary>
    public bool Pending => DeliverTime > DateTime.Now;
}
