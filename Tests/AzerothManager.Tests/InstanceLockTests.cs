using AzerothManager.Models;
using Xunit;

namespace AzerothManager.Tests;

/// <summary>
/// Lecture d'un verrou d'instance. Les tables étaient vides sur le serveur de référence :
/// comme pour les guildes, ces règles ne sont vérifiables que là.
/// </summary>
public class InstanceLockTests
{
    private static InstanceLock Lock(DateTime? resetAt = null, bool permanent = true,
                                     bool extended = false, long encounters = 0) =>
        new(10, "Arthas", 80, 500, 533, "Naxxramas", 3, resetAt, permanent, extended, encounters);

    [Fact]
    public void Un_verrou_expire_est_dit_expire_plutot_qu_en_negatif()
    {
        var l = Lock(resetAt: DateTime.Now.AddHours(-2));
        Assert.Equal("expiré", l.RemainingText);
    }

    [Fact]
    public void Le_temps_restant_est_compte_en_jours_et_heures()
    {
        var l = Lock(resetAt: DateTime.Now.AddDays(2).AddHours(3).AddMinutes(1));

        Assert.Contains("2 j", l.RemainingText);
        Assert.Contains("3 h", l.RemainingText);
        // Au-delà d'un jour, les minutes sont du bruit.
        Assert.DoesNotContain("min", l.RemainingText);
    }

    [Fact]
    public void Sans_heure_de_reinitialisation_rien_n_est_invente()
    {
        var l = Lock(resetAt: null);

        Assert.Equal("—", l.RemainingText);
        Assert.Equal("—", l.ResetText);
    }

    [Theory]
    [InlineData(true, "permanent")]
    [InlineData(false, "temporaire")]
    public void Le_type_distingue_le_raid_du_donjon(bool permanent, string expected)
        => Assert.Equal(expected, Lock(permanent: permanent).KindText);

    [Fact]
    public void Le_report_de_reinitialisation_est_signale()
    {
        Assert.Equal("prolongé", Lock(extended: true).ExtendedText);
        Assert.Equal("", Lock(extended: false).ExtendedText);
    }

    [Theory]
    [InlineData(0b0000, "—")]
    [InlineData(0b0001, "1 vaincu(s)")]
    [InlineData(0b1011, "3 vaincu(s)")]
    public void Les_boss_vaincus_sont_comptes_dans_le_masque(long mask, string expected)
    {
        // completedEncounters est un masque de bits, pas un compteur.
        Assert.Equal(expected, Lock(encounters: mask).EncountersText);
    }

    [Fact]
    public void Un_personnage_supprime_reste_identifiable_par_son_guid()
    {
        var l = new InstanceLock(77, "", 0, 500, 533, "Naxxramas", 3, null, true, false, 0);
        Assert.Equal("guid 77", l.CharacterText);
    }

    [Fact]
    public void Une_carte_absente_des_dbc_reste_identifiable_par_son_numero()
    {
        // MapName rend une chaîne vide tant que les DBC n'ont pas été importés.
        var l = new InstanceLock(10, "Arthas", 80, 500, 533, "", 3, null, true, false, 0);
        Assert.Equal("carte 533", l.MapText);
    }
}
