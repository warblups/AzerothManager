using AzerothManager.Services;
using Xunit;

namespace AzerothManager.Tests;

/// <summary>
/// Lecture d'une réponse SOAP du worldserver.
///
/// Défaut réel corrigé ici : ACSoap répond par une **faute SOAP** dès que le gestionnaire de
/// commande renvoie `false`, et y place la sortie complète de la commande
/// (`soap_sender_fault(soap, printBuffer, printBuffer)`). Or `false` ne signifie pas
/// « échec » côté AzerothCore : `HandleInstanceStatsCommand` affiche ses statistiques puis
/// renvoie `false`. Traiter la faute comme une panne de transport déclenchait un diagnostic
/// SSH qui accusait le compte SOAP alors que la commande avait abouti.
/// </summary>
public class GmCommandServiceTests
{
    private const string Ok = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/" xmlns:ns1="urn:AC">
          <SOAP-ENV:Body>
            <ns1:executeCommandResponse>
              <result>AzerothCore rev. 7f12e89ee5f4
        Connected players: 0.</result>
            </ns1:executeCommandResponse>
          </SOAP-ENV:Body>
        </SOAP-ENV:Envelope>
        """;

    // Réponse relevée pour « .instance stats », qui aboutit tout en renvoyant false.
    private const string Fault = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/">
          <SOAP-ENV:Body>
            <SOAP-ENV:Fault>
              <faultcode>SOAP-ENV:Client</faultcode>
              <faultstring>instances loaded: dungeons (0), battlegrounds (0), arenas (0)
        players in instances: dungeons (0)</faultstring>
            </SOAP-ENV:Fault>
          </SOAP-ENV:Body>
        </SOAP-ENV:Envelope>
        """;

    [Fact]
    public void Une_reponse_normale_rend_le_texte_du_resultat()
    {
        var result = GmCommandService.ParseResult(Ok);

        Assert.StartsWith("AzerothCore rev.", result);
        Assert.Contains("Connected players: 0.", result);
    }

    [Fact]
    public void Une_faute_soap_est_un_refus_de_commande_pas_une_panne_de_transport()
    {
        var ex = Assert.Throws<GmCommandFaultException>(() => GmCommandService.ParseResult(Fault));

        // Le texte de la faute est la sortie de la commande : elle doit être rendue telle
        // quelle, sans y accoler d'explication sur l'état du serveur.
        Assert.Contains("instances loaded: dungeons (0)", ex.Message);
        Assert.DoesNotContain("SOAP", ex.Message);
    }

    [Fact]
    public void Une_reponse_illisible_est_rendue_brute_plutot_que_perdue()
    {
        const string garbage = "503 Service Unavailable";
        Assert.Equal(garbage, GmCommandService.ParseResult(garbage));
    }

    [Theory]
    [InlineData("server info")]
    [InlineData(".server info")]
    [InlineData("pinfo Arthas")]
    [InlineData("ticket list")]
    [InlineData("autobroadcast list")]
    [InlineData("server motd")]
    public void Les_commandes_informatives_restent_permises_en_lecture_seule(string command)
        => Assert.True(GmCommandService.IsAllowedInReadOnly(command));

    [Theory]
    [InlineData("server shutdown 300")]
    [InlineData("guild delete \"Les Chevaliers\"")]
    [InlineData("account delete WARBLUPS")]
    [InlineData("event start 24")]
    [InlineData("ban account WARBLUPS 1d triche")]
    public void Toute_autre_commande_est_refusee_en_lecture_seule(string command)
        => Assert.False(GmCommandService.IsAllowedInReadOnly(command));
}
