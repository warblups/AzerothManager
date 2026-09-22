namespace AzerothManager.Models;

/// <summary>
/// Guilde de `characters.guild`.
///
/// `BankMoney` est en **cuivre**, comme tous les montants du serveur. `CreateDate` est un
/// horodatage Unix.
/// </summary>
public sealed record GuildSummary(
    int Id,
    string Name,
    int LeaderGuid,
    string LeaderName,
    int MemberCount,
    long BankMoney,
    DateTime? CreatedAt,
    string Motd,
    string Info)
{
    public string CreatedText => CreatedAt?.ToString("dd/MM/yyyy") ?? "—";
    public string BankMoneyText => ItemReference.Money(BankMoney, dashWhenZero: false);
    public string LeaderText => string.IsNullOrEmpty(LeaderName) ? $"guid {LeaderGuid}" : LeaderName;
}

/// <summary>
/// Membre de guilde. `guild_member.rank` renvoie à `guild_rank.rid` **pour la même guilde** :
/// les rangs sont numérotés par guilde, pas globalement.
/// </summary>
public sealed record GuildMember(
    int Guid,
    string Name,
    int Level,
    int ClassId,
    int RankId,
    string RankName,
    bool Online,
    string PublicNote,
    string OfficerNote)
{
    public string OnlineText => Online ? "en ligne" : "";
    public string RankText => string.IsNullOrEmpty(RankName) ? RankId.ToString() : $"{RankName} ({RankId})";
}

/// <summary>Rang de guilde. `rights` est un masque de droits, `BankMoneyPerDay` en cuivre.</summary>
public sealed record GuildRank(int RankId, string Name, long Rights, long BankMoneyPerDay)
{
    /// <summary>`0xFFFFFFFF` signifie « sans limite » — c'est la valeur du maître de guilde.</summary>
    public string BankMoneyText => BankMoneyPerDay >= uint.MaxValue
        ? "illimité"
        : ItemReference.Money(BankMoneyPerDay, dashWhenZero: false);

    public string RightsText => $"0x{Rights:X}";
}

/// <summary>Onglet de banque de guilde.</summary>
public sealed record GuildBankTab(int TabId, string Name, string Icon, string Text)
{
    public string Label => string.IsNullOrWhiteSpace(Name) ? $"Onglet {TabId}" : $"{Name} ({TabId})";
}

/// <summary>
/// Objet posé dans un onglet de banque.
///
/// `guild_bank_item` ne stocke que le **guid** de l'objet : comme pour `character_inventory`,
/// il faut joindre `item_instance` pour obtenir l'`itemEntry`, puis `world.item_template`.
/// </summary>
public sealed record GuildBankItem(
    int TabId,
    int SlotId,
    int ItemGuid,
    int Entry,
    string Name,
    int Quality,
    int Count,
    int DisplayId);

/// <summary>
/// Type d'événement de `guild_bank_eventlog`, tel que `GuildBankEventLogTypes` le définit.
/// </summary>
public enum GuildBankEventType
{
    DepositItem = 1,
    WithdrawItem = 2,
    MoveItem = 3,
    DepositMoney = 4,
    WithdrawMoney = 5,
    RepairMoney = 6,
    MoveItem2 = 7,
    BuySlot = 9
}

/// <summary>
/// Ligne du journal de banque — c'est la pièce qui rend un litige de vol arbitrable (§9).
///
/// **`ItemOrMoney` a deux sens selon `EventType`** : un identifiant d'objet pour les
/// mouvements d'objets, un montant en cuivre pour les mouvements d'argent. Les lire de la
/// même façon afficherait « objet 5000000 » là où il y a 500 pièces d'or.
/// </summary>
public sealed record GuildBankEvent(
    int LogGuid,
    int TabId,
    GuildBankEventType Type,
    int PlayerGuid,
    string PlayerName,
    long ItemOrMoney,
    int StackCount,
    int DestTabId,
    DateTime At,
    string ItemName)
{
    public bool IsMoney => Type is GuildBankEventType.DepositMoney
                                or GuildBankEventType.WithdrawMoney
                                or GuildBankEventType.RepairMoney;

    public string TypeText => Type switch
    {
        GuildBankEventType.DepositItem => "Dépôt d'objet",
        GuildBankEventType.WithdrawItem => "Retrait d'objet",
        GuildBankEventType.MoveItem or GuildBankEventType.MoveItem2 => "Déplacement",
        GuildBankEventType.DepositMoney => "Dépôt d'argent",
        GuildBankEventType.WithdrawMoney => "Retrait d'argent",
        GuildBankEventType.RepairMoney => "Réparation",
        GuildBankEventType.BuySlot => "Achat d'onglet",
        _ => $"Type {(int)Type}"
    };

    /// <summary>Ce qui a bougé : un montant, ou un objet et sa quantité.</summary>
    public string SubjectText => IsMoney
        ? ItemReference.Money(ItemOrMoney, dashWhenZero: false)
        : (string.IsNullOrEmpty(ItemName) ? $"objet {ItemOrMoney}" : ItemName)
          + (StackCount > 1 ? $" ×{StackCount}" : "");

    public string PlayerText => string.IsNullOrEmpty(PlayerName) ? $"guid {PlayerGuid}" : PlayerName;
    public string AtText => At.ToString("dd/MM/yyyy HH:mm");
    public string TabText => Type == GuildBankEventType.MoveItem || Type == GuildBankEventType.MoveItem2
        ? $"{TabId} → {DestTabId}"
        : TabId.ToString();
}

/// <summary>Type d'événement de `guild_eventlog` (`GuildEventLogTypes`).</summary>
public enum GuildEventType
{
    InvitePlayer = 1,
    JoinGuild = 2,
    PromotePlayer = 3,
    DemotePlayer = 4,
    UninvitePlayer = 5,
    LeaveGuild = 6
}

/// <summary>Ligne du journal de guilde : qui a été invité, promu, rétrogradé ou exclu, et par qui.</summary>
public sealed record GuildEvent(
    int LogGuid,
    GuildEventType Type,
    int PlayerGuid1,
    string PlayerName1,
    int PlayerGuid2,
    string PlayerName2,
    int NewRank,
    DateTime At)
{
    public string TypeText => Type switch
    {
        GuildEventType.InvitePlayer => "Invitation",
        GuildEventType.JoinGuild => "Arrivée",
        GuildEventType.PromotePlayer => "Promotion",
        GuildEventType.DemotePlayer => "Rétrogradation",
        GuildEventType.UninvitePlayer => "Exclusion",
        GuildEventType.LeaveGuild => "Départ",
        _ => $"Type {(int)Type}"
    };

    private static string Name(string name, int guid) =>
        string.IsNullOrEmpty(name) ? (guid == 0 ? "" : $"guid {guid}") : name;

    /// <summary>
    /// `PlayerGuid1` est la cible, `PlayerGuid2` l'auteur quand l'action en a un — une
    /// invitation, une promotion ou une exclusion sont le fait de quelqu'un.
    /// </summary>
    public string WhoText
    {
        get
        {
            var target = Name(PlayerName1, PlayerGuid1);
            var author = Name(PlayerName2, PlayerGuid2);
            return author.Length == 0 ? target : $"{target}  ←  par {author}";
        }
    }

    public string RankText => Type is GuildEventType.PromotePlayer or GuildEventType.DemotePlayer
        ? NewRank.ToString()
        : "";

    public string AtText => At.ToString("dd/MM/yyyy HH:mm");
}
