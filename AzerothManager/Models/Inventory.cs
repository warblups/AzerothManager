namespace AzerothManager.Models;

/// <summary>
/// Où se trouve un objet. Les quatre premières valeurs viennent de `character_inventory`,
/// les deux suivantes du courrier et de l'hôtel des ventes : un objet « perdu » s'y trouve
/// souvent, et les ignorer ferait conclure à une disparition.
/// </summary>
public enum InventorySection
{
    Equipped,
    Bags,
    Bank,
    Keyring,
    Currency,
    Mail,
    Auction,
    Other
}

/// <summary>Objet d'un personnage, avec l'endroit où il se trouve.</summary>
public sealed record InventoryItem(
    InventorySection Section,
    string Place,
    int SortKey,
    int ItemGuid,
    int Entry,
    string Name,
    int Quality,
    int ItemLevel,
    int DisplayId,
    int Count,
    bool Soulbound,
    int Durability,
    int MaxDurability,
    string CreatorName,
    int ContainerSlots)
{
    /// <summary>Nombre d'objets rangés dans ce sac ; renseigné après lecture de l'inventaire.</summary>
    public int ContentCount { get; init; }

    public string SectionText => InventoryLayout.SectionName(Section);
    public string BoundText => Soulbound ? "lié" : "";
    public string DurabilityText => MaxDurability > 0 ? $"{Durability}/{MaxDurability}" : "";
    public bool IsContainer => ContainerSlots > 0;

    /// <summary>
    /// Vrai si l'objet est dans les poches du personnage, donc à la portée de `.additem`
    /// en retrait. Le courrier et l'hôtel des ventes n'y sont pas.
    /// </summary>
    public bool IsCarried => Section is not (InventorySection.Mail or InventorySection.Auction);

    public string Tooltip =>
        $"{Name} — modèle {Entry}, objet n° {ItemGuid}" +
        (ItemLevel > 0 ? $"\nNiveau d'objet {ItemLevel}" : "") +
        (IsContainer ? $"\nSac de {ContainerSlots} cases, {ContentCount} occupée(s)" : "") +
        (CreatorName.Length > 0 ? $"\nFabriqué par {CreatorName}" : "");
}

/// <summary>
/// Traduit un couple (`bag`, `slot`) de `character_inventory` en emplacement lisible.
///
/// Piège principal de la table : **`bag` vaut 0 pour le personnage lui-même, et sinon le
/// guid de l'`item_instance` du sac** — pas un numéro d'emplacement. Il faut donc savoir
/// dans quel emplacement ce sac est rangé pour dire si l'objet est sur le personnage ou en
/// banque. Les plages viennent de `Player.h` (vérifié aux sources) :
///
/// 0-18 équipement · 19-22 sacs portés · 23-38 sac à dos · 39-66 banque ·
/// 67-73 sacs de banque · 74-85 rachat · 86-117 trousseau · 118-149 monnaies.
///
/// Les emplacements de rachat ne sont **jamais en base** : `_SaveInventory` les efface à
/// chaque sauvegarde. Ils ne figurent ici que pour ne pas étiqueter faux une ligne inattendue.
/// </summary>
public static class InventoryLayout
{
    public const int EquipmentEnd = 19;
    public const int BagStart = 19, BagEnd = 23;
    public const int PackStart = 23, PackEnd = 39;
    public const int BankStart = 39, BankEnd = 67;
    public const int BankBagStart = 67, BankBagEnd = 74;
    public const int BuybackStart = 74, BuybackEnd = 86;
    public const int KeyringStart = 86, KeyringEnd = 118;
    public const int CurrencyStart = 118, CurrencyEnd = 150;

    public static string SectionName(InventorySection section) => section switch
    {
        InventorySection.Equipped => "Équipé",
        InventorySection.Bags => "Sacs",
        InventorySection.Bank => "Banque",
        InventorySection.Keyring => "Trousseau",
        InventorySection.Currency => "Monnaies",
        InventorySection.Mail => "Courrier",
        InventorySection.Auction => "Hôtel des ventes",
        _ => "Autre"
    };

    /// <summary>
    /// Emplacement d'un objet. <paramref name="bagSlots"/> associe le guid de chaque sac du
    /// personnage à l'emplacement où il est rangé.
    /// </summary>
    public static (InventorySection Section, string Place, int SortKey) Locate(
        int bag, int slot, IReadOnlyDictionary<int, int> bagSlots)
    {
        if (bag != 0)
        {
            // Rangé dans un sac : c'est l'emplacement du sac qui dit où l'on est. La clé de
            // tri place le contenu juste après le sac lui-même.
            if (!bagSlots.TryGetValue(bag, out var bagSlot))
                return (InventorySection.Other, $"Conteneur n° {bag}, case {slot + 1}", 100_000 + slot);

            var key = bagSlot * 1000 + slot + 1;
            return bagSlot is >= BagStart and < BagEnd
                ? (InventorySection.Bags, $"Sac {bagSlot - BagStart + 1}, case {slot + 1}", key)
                : bagSlot is >= BankBagStart and < BankBagEnd
                    ? (InventorySection.Bank, $"Sac de banque {bagSlot - BankBagStart + 1}, case {slot + 1}", key)
                    : (InventorySection.Other, $"Conteneur en emplacement {bagSlot}, case {slot + 1}", key);
        }

        var own = slot * 1000;
        return slot switch
        {
            < EquipmentEnd => (InventorySection.Equipped, EquipmentSlots.Name(slot), own),
            < BagEnd => (InventorySection.Bags, $"Emplacement de sac {slot - BagStart + 1}", own),
            < PackEnd => (InventorySection.Bags, $"Sac à dos, case {slot - PackStart + 1}", own),
            < BankEnd => (InventorySection.Bank, $"Banque, case {slot - BankStart + 1}", own),
            < BankBagEnd => (InventorySection.Bank, $"Emplacement de sac de banque {slot - BankBagStart + 1}", own),
            < BuybackEnd => (InventorySection.Other, $"Rachat, case {slot - BuybackStart + 1}", own),
            < KeyringEnd => (InventorySection.Keyring, $"Trousseau, case {slot - KeyringStart + 1}", own),
            < CurrencyEnd => (InventorySection.Currency, $"Monnaies, case {slot - CurrencyStart + 1}", own),
            _ => (InventorySection.Other, $"Emplacement {slot}", own)
        };
    }

    /// <summary>
    /// Expéditeur d'un courrier, selon `messageType` (`Mail.h`) : seul `MAIL_NORMAL` porte
    /// un guid de personnage, les autres un identifiant de PNJ, d'objet ou d'hôtel des ventes.
    /// </summary>
    public static string MailSender(int messageType, string characterName) => messageType switch
    {
        0 => characterName.Length > 0 ? characterName : "un personnage supprimé",
        2 => "l'hôtel des ventes",
        3 => "un PNJ",
        4 => "un objet du monde",
        5 => "le calendrier",
        _ => "le serveur"
    };
}

/// <summary>
/// Objet archivé dans `recovery_item`, récupérable par `.item restore`. Le serveur ne
/// l'y range que si `ItemDelete.Method` ou `ItemDelete.Vendor` est actif.
/// </summary>
public sealed record RecoveryItem(
    int Id, int Entry, int Count, DateTime? DeletedAt, string Name, int Quality, int DisplayId)
{
    public string DeletedText => DeletedAt?.ToString("dd/MM/yyyy HH:mm") ?? "—";
}

/// <summary>
/// Réglages `ItemDelete.*` de `worldserver.conf`. Sans eux, aucun objet détruit ou vendu
/// n'est archivé et `.item restore` n'a rien à rendre — l'interface doit le dire.
///
/// **`ItemDelete.Method` commande tout** (vérifié aux sources) : la destruction comme la
/// vente passent par `WorldSession::recoveryItem`, qui ne range rien si `Method` vaut 0.
/// `Vendor` ne fait qu'étendre l'archivage aux ventes. `Vendor = 1` seul est le piège :
/// `.item restore` est alors accepté par le serveur, mais la table reste vide.
/// </summary>
public sealed record ItemDeleteConfig(
    bool Method, bool Vendor, int MinQuality, int MinItemLevel, int KeepDays, string Source)
{
    public bool Archives => Method;

    public string Summary
    {
        get
        {
            if (!Method)
                return (Vendor
                        ? "Archivage inactif malgré ItemDelete.Vendor = 1 : la vente passe par le même contrôle " +
                          "que la destruction, qui exige ItemDelete.Method = 1. "
                        : "Archivage désactivé : ItemDelete.Method = 0. ") +
                       "Un objet détruit ou vendu disparaît sans trace, et .item restore n'a rien à rendre. " +
                       "Pour l'activer, passer ItemDelete.Method à 1 dans worldserver.conf, puis redémarrer.";

            var what = Vendor ? "objets détruits et vendus" : "objets détruits (pas les ventes : ItemDelete.Vendor = 0)";
            var keep = KeepDays > 0 ? $"conservés {KeepDays} jour(s)" : "conservés sans limite";
            return $"Archivage actif : {what}, à partir de la qualité {ItemReference.QualityName(MinQuality)} " +
                   $"et du niveau d'objet {MinItemLevel}, {keep}. Racheter un objet au marchand retire " +
                   "son archive.";
        }
    }
}
