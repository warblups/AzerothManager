using AzerothManager.Models;
using AzerothManager.Services;
using Xunit;

namespace AzerothManager.Tests;

/// <summary>
/// Analyse des journaux du serveur.
///
/// Le préfixe d'une ligne dépend des drapeaux de l'appender, et **les quatre appenders du
/// serveur de référence sont à 0** : ni horodatage, ni niveau. Les deux configurations
/// doivent donc être couvertes, et c'est la seconde qui avait révélé un défaut réel.
/// </summary>
public class LogLineParserTests
{
    [Theory]
    [InlineData("2024-01-15_14:32:01 ERROR Erreur de chargement", LogSeverity.Error, "Erreur de chargement")]
    [InlineData("2024-01-15 14:32:02 WARN  Table manquante", LogSeverity.Warning, "Table manquante")]
    [InlineData("2024-01-15_14-32-03 INFO  Worldserver demarre", LogSeverity.Info, "Worldserver demarre")]
    [InlineData("[2024-01-15 14:32:04] [FATAL] Plus de cartes", LogSeverity.Error, "Plus de cartes")]
    [InlineData("2024-01-15_14:32:05 TRACE Requete", LogSeverity.Debug, "Requete")]
    [InlineData("2024-01-15_14:32:06.123 DEBUG Detail", LogSeverity.Debug, "Detail")]
    [InlineData("2024-01-15_14:32:07 Message sans niveau", LogSeverity.Other, "Message sans niveau")]
    public void Reconnait_les_formes_de_prefixe(string line, LogSeverity severity, string text)
    {
        var parsed = Assert.Single(LogLineParser.Parse(line, "Server.log"));

        Assert.Equal(severity, parsed.Severity);
        Assert.Equal(text, parsed.Text);
        Assert.NotNull(parsed.At);
    }

    [Fact]
    public void Accepte_le_tiret_comme_separateur_d_heure()
    {
        // Le format de l'horodatage a varié selon les versions : les deux sont acceptés
        // plutôt que d'en figer un.
        var avecDeuxPoints = LogLineParser.Parse("2024-01-15_14:32:03 INFO x", "Server.log")[0];
        var avecTirets = LogLineParser.Parse("2024-01-15_14-32-03 INFO x", "Server.log")[0];

        Assert.Equal(avecDeuxPoints.At, avecTirets.At);
    }

    [Fact]
    public void Une_continuation_herite_du_niveau_et_de_l_heure()
    {
        // Une pile d'appels n'a pas de préfixe : sans héritage, elle serait classée « autre »
        // et disparaîtrait dès qu'on filtre sur les erreurs.
        var lines = LogLineParser.Parse(
            "2024-01-15_14:32:01 ERROR Exception\n   at Frame1\n   at Frame2", "Server.log");

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal(LogSeverity.Error, l.Severity));
        Assert.All(lines, l => Assert.NotNull(l.At));
    }

    [Fact]
    public void Sans_aucun_horodatage_chaque_ligne_est_jugee_seule()
    {
        // Défaut réel : l'héritage contaminait tout un journal écrit sans préfixe — soit la
        // configuration effective du serveur de référence, où 0 ligne sur 3 196 est horodatée.
        var lines = LogLineParser.Parse(
            "ERROR quelque chose a echoue\nUsing configuration file worldserver.conf.\nsuite",
            "Server.log");

        Assert.Equal(LogSeverity.Error, lines[0].Severity);
        Assert.Equal(LogSeverity.Other, lines[1].Severity);
        Assert.Equal(LogSeverity.Other, lines[2].Severity);
        Assert.All(lines, l => Assert.Null(l.At));
    }

    [Theory]
    [InlineData("Ceci contient information et warnaccount")]
    [InlineData("Loading DBErrors and Errors tables")]
    public void Ne_prend_pas_un_mot_contenant_une_etiquette_pour_une_etiquette(string line)
    {
        var parsed = Assert.Single(LogLineParser.Parse(line, "Server.log"));
        Assert.Equal(LogSeverity.Other, parsed.Severity);
    }

    [Fact]
    public void Une_etiquette_en_pleine_ligne_donne_le_niveau_sans_amputer_le_texte()
    {
        var parsed = Assert.Single(LogLineParser.Parse("WARNING: module obsolete", "Server.log"));

        Assert.Equal(LogSeverity.Warning, parsed.Severity);
        Assert.Equal("WARNING: module obsolete", parsed.Text);
    }

    [Fact]
    public void Ignore_les_lignes_vides()
    {
        var lines = LogLineParser.Parse("a\n\n\r\nb", "Server.log");
        Assert.Equal(2, lines.Count);
    }
}
