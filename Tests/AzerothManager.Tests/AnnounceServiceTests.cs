using AzerothManager.Services;
using Xunit;

namespace AzerothManager.Tests;

/// <summary>
/// Règles de l'arrêt planifié, reprises de `cs_server.cpp` et `Util.cpp`.
///
/// Ces deux fonctions existent pour dire à l'utilisateur ce que le serveur va comprendre
/// **avant** l'envoi : la première parce qu'un délai sans suffixe est lu en secondes, la
/// seconde parce qu'un motif commençant par un nombre est avalé par le code de sortie.
/// </summary>
public class AnnounceServiceTests
{
    [Theory]
    [InlineData("5m", 300)]
    [InlineData("1h30m", 5400)]
    [InlineData("1d", 86400)]
    [InlineData("2h", 7200)]
    [InlineData("45s", 45)]
    [InlineData("1h30m20s", 5420)]
    public void Les_suffixes_de_duree_sont_cumulables(string input, int expected)
        => Assert.Equal(expected, AnnounceService.TryParseDelay(input));

    [Theory]
    [InlineData("300", 300)]
    [InlineData("90", 90)]
    [InlineData("5", 5)]
    public void Un_delai_sans_suffixe_est_lu_en_secondes(string input, int expected)
    {
        // Piège d'usage : « 5 » planifie l'arrêt dans cinq secondes, pas cinq minutes. Le
        // serveur se rabat sur StringTo<int32> quand TimeStringToSecs renvoie 0.
        Assert.Equal(expected, AnnounceService.TryParseDelay(input));
    }

    [Theory]
    [InlineData("5x")]      // caractère hors d/h/m/s : bad format côté serveur
    [InlineData("abc")]
    [InlineData("0")]       // delay <= 0 : LANG_BAD_VALUE
    [InlineData("0m")]
    [InlineData("")]
    [InlineData("   ")]
    public void Les_delais_que_le_serveur_refuserait_sont_refuses_ici(string input)
        => Assert.Null(AnnounceService.TryParseDelay(input));

    [Theory]
    [InlineData("5 minutes de maintenance")]
    [InlineData("0")]
    [InlineData("125 raisons")]
    public void Un_motif_commencant_par_un_code_de_sortie_valide_serait_avale(string reason)
    {
        // La signature est (time, Optional<int32> exitCode, Tail reason) : le nombre part
        // dans exitCode, et cette branche appelle ShutdownServ sans le motif.
        Assert.True(AnnounceService.ReasonWouldBeSwallowed(reason));
    }

    [Theory]
    [InlineData("126 raisons")]   // hors de la plage 0..125
    [InlineData("-1 test")]
    [InlineData("Maintenance")]
    [InlineData("Mise a jour des modules")]
    [InlineData("")]
    public void Un_motif_hors_de_la_plage_du_code_de_sortie_est_conserve(string reason)
        => Assert.False(AnnounceService.ReasonWouldBeSwallowed(reason));

    [Fact]
    public void La_locale_chinoise_traditionnelle_est_zhTW()
    {
        // Le message d'erreur de .server set motd annonce « zhWE », qui n'existe nulle part
        // ailleurs dans le code : localeNames (Common.cpp) dit zhTW. Recopier le message
        // aurait proposé une valeur que IsLocaleValid refuse.
        Assert.Contains("zhTW", AnnounceService.MotdLocales);
        Assert.DoesNotContain("zhWE", AnnounceService.MotdLocales);
        Assert.Equal(9, AnnounceService.MotdLocales.Count);
    }

    [Fact]
    public void Les_six_formes_d_annonce_sont_exposees()
    {
        Assert.Equal(6, AnnounceService.Forms.Count);
        Assert.Equal(6, AnnounceService.Forms.Select(f => f.Command).Distinct().Count());
    }

    [Theory]
    [InlineData(300, "5 min")]
    [InlineData(5400, "1 h 30 min")]
    [InlineData(86400, "1 j")]
    [InlineData(45, "45 s")]
    public void La_duree_retenue_est_formulee_avant_l_envoi(int seconds, string expected)
        => Assert.Equal(expected, AnnounceService.DescribeDelay(seconds));
}
