namespace AzerothManager.Models;

/// <summary>
/// Forme d'une annonce. Les six commandes sont `Console::Yes` — vérifié dans
/// `cs_message.cpp` —, donc toutes disponibles par SOAP. Seule `.whispers` de ce fichier
/// est `Console::No`, et elle ne relève pas de l'annonce.
/// </summary>
public enum AnnounceKind
{
    /// <summary>`.announce` — message système dans le canal de discussion, à tout le monde.</summary>
    Chat,

    /// <summary>`.notify` — notification au milieu de l'écran, à tout le monde.</summary>
    Screen,

    /// <summary>`.gmannounce` — discussion, aux maîtres de jeu connectés seulement.</summary>
    GmChat,

    /// <summary>`.gmnotify` — écran, aux maîtres de jeu connectés seulement.</summary>
    GmScreen,

    /// <summary>`.nameannounce` — discussion, à tous, préfixé du nom de l'émetteur.</summary>
    NamedChat,

    /// <summary>`.gmnameannounce` — discussion, aux maîtres de jeu, préfixé du nom.</summary>
    NamedGmChat
}

/// <summary>Une forme d'annonce et ce qu'elle produit réellement en jeu.</summary>
public sealed record AnnounceForm(AnnounceKind Kind, string Command, string Label, string Effect);

/// <summary>
/// Annonce récurrente de `auth.autobroadcast`.
///
/// La table vit dans la base d'authentification, pas dans `world` : les commandes
/// `.autobroadcast` passent par `LoginDatabase` et filtrent sur `realmid`, la valeur `-1`
/// valant tous les royaumes.
/// </summary>
public sealed record AutoBroadcastEntry(int Id, int RealmId, int Weight, string Text)
{
    public string RealmText => RealmId < 0 ? "tous" : RealmId.ToString();
}

/// <summary>
/// État de la diffusion automatique lu dans `worldserver.conf`.
///
/// Ajouter une annonce récurrente pendant que `AutoBroadcast.On = 0` ne produit rien :
/// la ligne part en base et reste muette. Le module le vérifie au lieu de laisser croire
/// à une panne.
/// </summary>
public sealed record AutoBroadcastConfig(bool Enabled, int TimerMs, bool Centered, string Source)
{
    public string TimerText => TimerMs <= 0
        ? "—"
        : TimerMs % 60000 == 0 ? $"{TimerMs / 60000} min" : $"{TimerMs / 1000} s";

    public string Summary => Enabled
        ? $"Diffusion automatique active, toutes les {TimerText}" +
          (Centered ? ", au centre de l'écran." : ", dans la discussion.")
        : "Diffusion automatique désactivée : AutoBroadcast.On = 0. Les annonces " +
          "récurrentes seront enregistrées mais jamais diffusées.";
}
