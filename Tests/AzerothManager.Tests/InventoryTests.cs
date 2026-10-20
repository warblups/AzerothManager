using AzerothManager.Models;
using Xunit;

namespace AzerothManager.Tests;

/// <summary>
/// Emplacements de `character_inventory` et archivage des objets. Les plages viennent de
/// `Player.h`, les règles d'archivage d'`ItemHandler.cpp` : la banque et `recovery_item`
/// étaient vides sur le serveur de référence, ces tests sont le seul endroit où elles tiennent.
/// </summary>
public class InventoryTests
{
    // Guid d'item_instance du sac → emplacement où il est rangé.
    private static readonly Dictionary<int, int> Bags = new()
    {
        [473590] = 19,  // premier sac porté
        [500000] = 67   // premier sac de banque
    };

    [Theory]
    [InlineData(0, InventorySection.Equipped, "Tête")]
    [InlineData(18, InventorySection.Equipped, "Tabard")]
    [InlineData(19, InventorySection.Bags, "Emplacement de sac 1")]
    [InlineData(23, InventorySection.Bags, "Sac à dos, case 1")]
    [InlineData(38, InventorySection.Bags, "Sac à dos, case 16")]
    [InlineData(39, InventorySection.Bank, "Banque, case 1")]
    [InlineData(66, InventorySection.Bank, "Banque, case 28")]
    [InlineData(67, InventorySection.Bank, "Emplacement de sac de banque 1")]
    [InlineData(86, InventorySection.Keyring, "Trousseau, case 1")]
    [InlineData(118, InventorySection.Currency, "Monnaies, case 1")]
    public void Les_plages_de_Player_h_sont_respectees(int slot, InventorySection section, string place)
    {
        var located = InventoryLayout.Locate(0, slot, Bags);
        Assert.Equal(section, located.Section);
        Assert.Equal(place, located.Place);
    }

    [Fact]
    public void Bag_est_le_guid_du_sac_et_non_un_emplacement()
    {
        // Le contenu d'un sac porté est sur le personnage, celui d'un sac de banque en banque :
        // seul l'emplacement du sac permet de le dire.
        Assert.Equal((InventorySection.Bags, "Sac 1, case 3"),
                     Strip(InventoryLayout.Locate(473590, 2, Bags)));
        Assert.Equal((InventorySection.Bank, "Sac de banque 1, case 1"),
                     Strip(InventoryLayout.Locate(500000, 0, Bags)));
    }

    [Fact]
    public void Un_conteneur_inconnu_n_est_pas_range_au_hasard()
    {
        Assert.Equal(InventorySection.Other, InventoryLayout.Locate(123, 0, Bags).Section);
    }

    [Fact]
    public void Le_contenu_d_un_sac_se_trie_juste_apres_lui()
    {
        var bag = InventoryLayout.Locate(0, 19, Bags).SortKey;
        var inside = InventoryLayout.Locate(473590, 0, Bags).SortKey;
        var nextBag = InventoryLayout.Locate(0, 20, Bags).SortKey;
        Assert.True(bag < inside && inside < nextBag);
    }

    [Fact]
    public void Vendor_seul_n_archive_rien()
    {
        // La vente passe par recoveryItem, qui exige ItemDelete.Method = 1.
        var config = new ItemDeleteConfig(Method: false, Vendor: true, 3, 80, 0, "worldserver.conf");
        Assert.False(config.Archives);
        Assert.Contains("malgré ItemDelete.Vendor = 1", config.Summary);
    }

    [Fact]
    public void Method_seul_n_archive_pas_les_ventes()
    {
        var config = new ItemDeleteConfig(Method: true, Vendor: false, 3, 80, 0, "worldserver.conf");
        Assert.True(config.Archives);
        Assert.Contains("pas les ventes", config.Summary);
    }

    [Theory]
    [InlineData(0, "Blups", "Blups")]
    [InlineData(0, "", "un personnage supprimé")]
    [InlineData(2, "", "l'hôtel des ventes")]
    [InlineData(3, "", "un PNJ")]
    public void L_expediteur_depend_du_type_de_courrier(int type, string name, string expected)
    {
        Assert.Equal(expected, InventoryLayout.MailSender(type, name));
    }

    private static (InventorySection, string) Strip((InventorySection S, string P, int K) l) => (l.S, l.P);
}
