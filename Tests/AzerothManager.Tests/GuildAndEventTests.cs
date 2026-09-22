using AzerothManager.Models;
using AzerothManager.Services;
using Xunit;

namespace AzerothManager.Tests;

/// <summary>
/// Lecture des journaux de guilde et des événements mondiaux.
///
/// Les tables de guilde étaient vides sur le serveur de référence : ces règles n'ont donc
/// jamais pu être vérifiées sur des lignes réelles, ce qui est précisément la raison de les
/// figer ici.
/// </summary>
public class GuildBankEventTests
{
    private static GuildBankEvent Event(GuildBankEventType type, long itemOrMoney, int stack = 1,
                                        string itemName = "") =>
        new(1, 0, type, 42, "Arthas", itemOrMoney, stack, 0, new DateTime(2026, 1, 2, 3, 4, 5), itemName);

    [Theory]
    [InlineData(GuildBankEventType.DepositMoney)]
    [InlineData(GuildBankEventType.WithdrawMoney)]
    [InlineData(GuildBankEventType.RepairMoney)]
    public void Les_types_4_5_et_6_portent_un_montant(GuildBankEventType type)
    {
        // ItemOrMoney a deux sens : le confondre afficherait le nom de l'objet dont
        // l'identifiant vaut le montant.
        var e = Event(type, 5000000);

        Assert.True(e.IsMoney);
        Assert.Contains("500o", e.SubjectText);
    }

    [Theory]
    [InlineData(GuildBankEventType.DepositItem)]
    [InlineData(GuildBankEventType.WithdrawItem)]
    [InlineData(GuildBankEventType.MoveItem)]
    [InlineData(GuildBankEventType.MoveItem2)]
    public void Les_autres_types_portent_un_objet(GuildBankEventType type)
    {
        var e = Event(type, 19019, stack: 3, itemName: "Thunderfury");

        Assert.False(e.IsMoney);
        Assert.Equal("Thunderfury ×3", e.SubjectText);
    }

    [Fact]
    public void Un_objet_sans_nom_reste_identifiable_par_son_entree()
    {
        var e = Event(GuildBankEventType.WithdrawItem, 19019);
        Assert.Equal("objet 19019", e.SubjectText);
    }

    [Fact]
    public void Un_deplacement_montre_l_onglet_de_depart_et_d_arrivee()
    {
        var e = new GuildBankEvent(1, 2, GuildBankEventType.MoveItem, 42, "Arthas",
                                   19019, 1, 5, DateTime.Now, "Thunderfury");
        Assert.Equal("2 → 5", e.TabText);
    }

    [Fact]
    public void Un_membre_inconnu_est_designe_par_son_guid()
    {
        var e = new GuildBankEvent(1, 0, GuildBankEventType.DepositItem, 77, "",
                                   19019, 1, 0, DateTime.Now, "");
        Assert.Equal("guid 77", e.PlayerText);
    }
}

public class GuildEventTests
{
    [Fact]
    public void Une_action_avec_auteur_nomme_la_cible_puis_l_auteur()
    {
        // PlayerGuid1 est la cible, PlayerGuid2 l'auteur : les inverser attribuerait
        // l'exclusion à celui qui la subit.
        var e = new GuildEvent(1, GuildEventType.UninvitePlayer, 10, "Arthas", 20, "Jaina", 0, DateTime.Now);

        Assert.StartsWith("Arthas", e.WhoText);
        Assert.Contains("Jaina", e.WhoText);
    }

    [Fact]
    public void Une_action_sans_auteur_ne_montre_que_la_cible()
    {
        var e = new GuildEvent(1, GuildEventType.LeaveGuild, 10, "Arthas", 0, "", 0, DateTime.Now);
        Assert.Equal("Arthas", e.WhoText);
    }

    [Theory]
    [InlineData(GuildEventType.PromotePlayer, "3")]
    [InlineData(GuildEventType.DemotePlayer, "3")]
    [InlineData(GuildEventType.JoinGuild, "")]
    public void Le_rang_n_est_montre_que_pour_un_changement_de_rang(GuildEventType type, string expected)
    {
        var e = new GuildEvent(1, type, 10, "Arthas", 20, "Jaina", 3, DateTime.Now);
        Assert.Equal(expected, e.RankText);
    }
}

public class GuildRankTests
{
    [Fact]
    public void Le_retrait_illimite_est_dit_plutot_qu_affiche_en_cuivre()
    {
        // 0xFFFFFFFF est la valeur du maître de guilde : la rendre en pièces d'or
        // afficherait un nombre absurde.
        var rank = new GuildRank(0, "Maître de guilde", 0xFFFFFFFF, uint.MaxValue);
        Assert.Equal("illimité", rank.BankMoneyText);
    }

    [Fact]
    public void Un_plafond_ordinaire_est_rendu_en_monnaie()
    {
        var rank = new GuildRank(3, "Membre", 0, 10000);
        Assert.Equal("1o 0a 0c", rank.BankMoneyText);
    }
}

/// <summary>Événements mondiaux : durées en minutes, et état actif lu chez le serveur.</summary>
public class GameEventTests
{
    [Theory]
    [InlineData(525600, "1 an")]        // valeur réelle d'un événement annuel
    [InlineData(1051200, "2 ans")]
    [InlineData(131040, "91 j")]        // Foire de Sombrelune
    [InlineData(20160, "14 j")]         // fête de deux semaines
    [InlineData(10079, "6 j 23 h 59 min")]
    [InlineData(120, "2 h")]
    [InlineData(0, "—")]
    public void Les_durees_sont_exprimees_en_minutes(long minutes, string expected)
        => Assert.Equal(expected, Duration.FromMinutes(minutes));

    [Fact]
    public void L_etat_actif_est_extrait_de_la_sortie_reelle_de_event_activelist()
    {
        // Sortie relevée sur le serveur de référence.
        const string output = """
            11 - Harvest Festival  [active]
            24 - Brewfest  [active]
            103 - Sun's Reach Reclamation Phase 2 Permanent  [active]
            """;

        var ids = GameEventService.ParseActiveIds(output);

        Assert.Equal(3, ids.Count);
        Assert.Contains(11, ids);
        Assert.Contains(24, ids);
        Assert.Contains(103, ids);
    }

    [Fact]
    public void Une_absence_d_evenement_actif_ne_donne_aucun_identifiant()
    {
        Assert.Empty(GameEventService.ParseActiveIds("No event found."));
        Assert.Empty(GameEventService.ParseActiveIds(""));
    }

    [Theory]
    [InlineData(12, 0, "+12")]
    [InlineData(0, 5, "−5")]
    [InlineData(12, 5, "+12 / −5")]
    [InlineData(0, 0, "—")]
    public void Les_spawns_ajoutes_et_retires_sont_distingues(int added, int removed, string expected)
    {
        // Un eventEntry négatif désigne un spawn retiré pendant l'événement : les additionner
        // laisserait croire que l'événement ajoute ce qu'il enlève.
        var e = new GameEventInfo(1, "Test", null, null, 525600, 20160, 0, 0, false, 2,
                                  added, removed, false);
        Assert.Equal(expected, e.SpawnsText);
    }

    [Theory]
    [InlineData(0, "jamais")]
    [InlineData(1, "toujours")]
    [InlineData(2, "selon la config")]
    public void L_annonce_a_trois_etats_dont_un_delegue_a_la_configuration(int announce, string expected)
    {
        var e = new GameEventInfo(1, "Test", null, null, 525600, 20160, 0, 0, false, announce, 0, 0, false);
        Assert.Equal(expected, e.AnnounceText);
    }
}

/// <summary>
/// Origine réelle de la planification d'un événement.
///
/// Défaut trouvé en comparant l'affichage du module à `.event info` sur le serveur : la base
/// donnait 15 jours pour Brewfest, le serveur en annonçait 16. `SetHolidayEventTime` écrase
/// période et durée depuis `Holidays.dbc` — mais seulement quand `holidayStage` n'est pas
/// nul, la fonction sortant immédiatement sinon.
/// </summary>
public class GameEventScheduleSourceTests
{
    private static GameEventInfo Event(int holidayId, int holidayStage) =>
        new(24, "Brewfest", null, null, 525600, 21600, holidayId, holidayStage, false, 2, 0, 0, true);

    [Fact]
    public void Une_etape_de_fete_fait_primer_le_dbc_sur_la_base()
    {
        var e = Event(holidayId: 372, holidayStage: 1);

        Assert.True(e.ScheduleOverriddenByHoliday);
        Assert.Equal("Holidays.dbc", e.ScheduleSourceText);
    }

    [Fact]
    public void Un_identifiant_de_fete_seul_ne_suffit_pas_a_ecraser_la_planification()
    {
        // C'est holidayStage qui déclenche l'écrasement, pas holiday : confondre les deux
        // ferait avertir à tort sur des événements dont la base fait autorité.
        var e = Event(holidayId: 372, holidayStage: 0);

        Assert.False(e.ScheduleOverriddenByHoliday);
        Assert.Equal("base", e.ScheduleSourceText);
    }

    [Fact]
    public void Un_evenement_ordinaire_est_planifie_par_la_base()
    {
        var e = Event(holidayId: 0, holidayStage: 0);
        Assert.False(e.ScheduleOverriddenByHoliday);
    }
}
