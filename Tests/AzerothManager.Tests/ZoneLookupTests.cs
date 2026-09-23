using AzerothManager.Services;
using Xunit;
using ZoneRect = AzerothManager.Services.GameClientService.ZoneRect;

namespace AzerothManager.Tests;

/// <summary>
/// Zone déduite d'une position, sur les rectangles réels de WorldMapArea.dbc. Ce sont des
/// cadres de carte qui se chevauchent : la règle de choix est ce que ces tests figent.
/// </summary>
public class ZoneLookupTests
{
    private static readonly ZoneRect Elwynn =
        new(12, 0, -9096.875f, -200f, -10254.166f, -7939.583f, -1935.4166f, 1535.4166f);

    private static readonly ZoneRect Westfall =
        new(40, 0, -10566.666f, 1266.6666f, -11733.333f, -9400f, -483.3333f, 3016.6665f);

    [Fact]
    public void Le_nord_de_la_Marche_de_l_Ouest_n_est_pas_pris_pour_Elwynn()
    {
        // Position réelle de trois personnages, que le serveur situe en zone 40. Le cadre
        // d'Elwynn, à peine plus petit, la contient aussi : c'est ce qui trompait
        // l'ancienne règle du « plus petit rectangle ».
        Assert.True(Elwynn.Contains(-10103f, 1072f));
        Assert.Equal(40, ZoneRect.Pick([Elwynn, Westfall], -10103f, 1072f)?.AreaId);
    }

    [Fact]
    public void Comté_d_or_reste_en_Forêt_d_Elwynn()
    {
        Assert.Equal(12, ZoneRect.Pick([Elwynn, Westfall], -9460f, 60f)?.AreaId);
    }

    [Fact]
    public void Hors_de_tout_rectangle_la_zone_est_inconnue()
    {
        Assert.Null(ZoneRect.Pick([Elwynn, Westfall], 1678f, 1678f));
    }
}
