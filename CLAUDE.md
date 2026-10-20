# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## État actuel du projet

Solution scaffoldée et fonctionnelle. Modules en place : **Configuration des serveurs**, **Console SQL**, **Console GM (SOAP)**, **Catalogue d'objets**, **Comptes**, **Courrier en jeu**, **Armurerie**, **Tickets GM**, **Restauration ciblée**, **Carte des joueurs**, **Personnages**, **Modération**, **Téléportation**, **Édition du monde**, **Logs**, **Annonces et arrêt**, **Guildes**, **Inventaire et banque**, **Événements de jeu** et **Verrous d'instance**. **Le socle v1.0 est complet**, et le rail de navigation n'a plus d'entrée désactivée : l'armurerie et les modules v1.1/v1.2 déjà construits ont été avancés grâce à la brique DBC.

La référence fonctionnelle est `Cahier_des_Charges_AzerothCore_Admin_Manager_V1.2.docx` (24 sections). Les V1 et V1.1 sont conservées comme historique et sont périmées : ne pas s'y fier. Le cahier des charges est la source de vérité et il est rédigé en français — la documentation, les commentaires et les libellés d'interface le sont aussi.

**Prochaine étape** : plus aucun module du rail n'est en attente. Restent au cahier des charges, non commencés : **royaumes** (`auth.realmlist`), **réputations**, **quêtes**, **playerbots**, **économie et anti-triche**, puis la v2.0. Métiers/sorts et modification en direct sont **abandonnés en l'état** : leurs commandes sont toutes `Console::No` — voir la section « Ce que SOAP ne peut pas faire ».

Conventions déjà établies dans le code, à suivre :

- Composition manuelle dans `App.OnStartup` — pas de conteneur d'injection, l'échelle ne le justifie pas.
- `ServerContext` porte le profil actif ; les services le lisent **à chaque opération** plutôt que de mettre en cache une chaîne de connexion, pour que le changement de profil les reconfigure tous.
- Le mode lecture seule est appliqué dans `MySqlService.ExecuteAsync`, pas dans l'UI.
- `LocalDatabase.LogHistory` journalise SQL, commandes GM et actions dans la même table.
- `ServerProfile` est un `ObservableObject` : le modèle porte la notification, assumé pour éviter une couche de DTO inutile.
- **Les écritures SQL s'exécutent dans une transaction, puis demandent validation** en annonçant le nombre de lignes réellement touchées ; un refus provoque un ROLLBACK. C'est plus sûr qu'une confirmation à l'aveugle *avant* exécution, et c'est ce qu'un client SQL générique ne sait pas faire — ne pas régresser vers une simple `MessageBox` préalable.
- Après une écriture validée dans `world`, `SqlEditorService.SuggestReload` propose le `.reload` correspondant, exécuté via le même `GmCommandService`. La liste des tables rechargeables est volontairement courte : n'y ajouter qu'un nom vérifié.
- **Un chargement asynchrone garde son ' + chr(96) + 'CancellationTokenSource' + chr(96) + '** (' + chr(96) + '_pending' + chr(96) + ') et vérifie l' + chr(39) + 'annulation **avant de toucher aux collections liées**. Il ne suffit pas d' + chr(39) + 'annuler : une propriété qui déclenche un chargement non attendu — la case « inclure les hors ligne » de la carte des joueurs — fait sinon vider et remplir les mêmes ' + chr(96) + 'ObservableCollection' + chr(96) + ' par deux chargements concurrents. Le symptôme n' + chr(39) + 'est pas seulement une exception : la carte a annoncé 4 zones peuplées là où il y en avait 2.
- AvalonEdit n'expose pas `Text` en propriété liable : passer par `AvalonEditBehaviour.BindableText`.

Le `.docx` est souvent ouvert dans Word (fichier verrouillé). Pour le relire, copier via un `FileStream` en mode `ReadWrite` share, dézipper, puis extraire le texte de `word/document.xml`.

## Projet

AzerothCore Admin Manager : application Windows **C# WPF (.NET 10, MVVM)** qui remplace les outils dispersés (HeidiSQL, console GM, requêtes SQL, scripts) pour administrer un ou plusieurs serveurs AzerothCore 3.3.5a, sous Windows **ou** Linux.

> Cible **`net10.0-windows`**, alignée sur AzerothUpdater (la V1 du cahier des charges disait « .NET 8 » — corrigé en V1.1 ; ne pas revenir en arrière). Comme l'updater : `UseWPF`, `Nullable`/`ImplicitUsings` activés, publication self-contained `win-x64` single-file.

## Architecture cible

Séparation stricte **Views → ViewModels → Services → données**. Les Views ne parlent jamais à MySQL, SSH ou SQLite ; tout passe par un service.

```
AzerothCoreManager
├── Views          (WPF, thème sombre)
├── ViewModels     (CommunityToolkit.Mvvm)
├── Models
├── Services       MySqlService, SshService, GmCommandService,
│                  AccountService, CharacterService, MailService,
│                  ModerationService, TicketService, RestoreService,
│                  WorldEditService, TeleportService,
│                  ItemCatalogService, ArmoryService, InventoryService,
│                  SqlEditorService, WorldServerConfig
├── Data           (SQLite local)
└── Assets
```

Deux univers de données à ne pas confondre :

- **Serveur distant** — les trois bases AzerothCore (`auth`, `characters`, `world`) via MySqlConnector, plus l'accès système via SSH.NET.
- **Local** — une base SQLite (Microsoft.Data.Sqlite) : `Servers` (profils de connexion), `Favorites` (requêtes SQL), `History` (actions **et commandes GM**), `Settings` (clé/valeur), `ItemCache` (icônes/infobulles pour l'armurerie), `TeleportPoints` (destinations personnelles, complément de `world.game_tele`).

### Multi-serveurs et abstraction de l'OS

L'application gère plusieurs profils de connexion, **un seul actif à la fois** ; changer de profil doit reconfigurer tous les services. Chaque opération système a deux implémentations derrière une même abstraction — c'est la contrainte structurante du projet :

| Action        | Windows          | Linux           |
|---------------|------------------|-----------------|
| Logs          | lecture locale   | SFTP            |
| Commandes     | console          | SSH / screen    |
| État serveur  | process          | systemctl       |
| Configuration | fichiers locaux  | SFTP            |

### Règles de sécurité (non négociables, cf. §21 du cahier des charges)

- Mots de passe chiffrés via **DPAPI** ; aucune donnée sensible en clair, ni en base SQLite ni dans les logs.
- Confirmation obligatoire avant tout `UPDATE`, `DELETE` ou `DROP` lancé depuis l'éditeur SQL.
- Journalisation (Serilog) de toutes les actions administrateur.
- Mode lecture seule configurable, qui doit bloquer les écritures au niveau des services, pas seulement griser l'UI.

## Interface — mode professionnel

Densité et clavier d'abord, mais **la palette et les conventions visuelles sont celles d'AzerothUpdater**, pour que les deux applications du même auteur forment un ensemble : fond `#1a1a2e`, surfaces `#16213e` / `#0f3460`, saisie `#0d1117`, accent `#e94560`, texte `#eaeaea`, atténué `#8892b0`. Boutons plats à coins arrondis, onglets soulignés en accent.

Deux écarts assumés : le bandeau Production est en `#8e1d2d`, plus sombre que l'accent pour rester distinguable d'un accent déjà rouge ; et la coloration SQL utilise `Themes/SqlDark.xshd`, la définition `TSQL` d'AvalonEdit visant un fond blanc et devenant illisible ici.

- **Densité avant décoration.** Grilles compactes, colonnes triables et redimensionnables, largeurs mémorisées. Pas d'animation décorative, pas de police fantaisie.
- **Icônes emoji, comme AzerothUpdater** — convention commune aux deux applications : emoji, deux espaces, libellé (`🖥  Serveurs`). Décision de l'auteur, pour que les deux outils forment un ensemble. Dans le rail, l'emoji vit dans `NavigationItem.Icon`, pas dans `Title`. Deux modules ne partagent pas la même icône : la carte des joueurs porte `🧭`, l'édition du monde gardant `🗺`.
- **Le rail est groupé et replié par défaut.** Vingt modules ne se parcourent pas d'un coup d'œil : ils sont rangés en cinq catégories — Serveur, Comptes et personnages, Support, Monde, Outils — choisies sur la nature du travail, pas sur les versions du cahier des charges. Points à respecter :
  - **Une seule `ListBox`, groupée par `CollectionViewSource`**, et non une liste par catégorie : c'est ce qui préserve la sélection unique et la navigation au clavier. Le `GroupStyle` remplace le `GroupItem` par un `Expander` (style `NavGroup` du thème).
  - **La clé de groupe est l'objet `NavigationCategory`**, pas son intitulé : l'en-tête lie alors directement `Name.IsExpanded`, WPF exposant la clé de groupe sous le nom `Name`.
  - La préférence `nav.expanded` de la table `Settings` retient **ce qui est déplié**, pas ce qui est replié. Enregistrer l'ouverture donne le bon défaut des deux côtés : rien n'est ouvert au premier lancement, et un module ajouté plus tard arrive replié comme les autres.
  - Tout étant replié, l'entrée sélectionnée est invisible : le groupe qui la contient porte donc son intitulé en accent (`NavigationCategory.IsCurrent`). Sans ce repère, on perd toute notion de l'endroit où l'on se trouve.
  - Pas d'emoji sur les en-têtes de groupe : ce sont des intitulés de section, et les distinguer du niveau en dessous vaut mieux que de répéter la convention.
- **Clavier d'abord.** Chaque action fréquente a un raccourci : `F5` rafraîchir, `Ctrl+Entrée` exécuter la requête, `Ctrl+F` rechercher, `Ctrl+T` nouvel onglet SQL. Navigation complète sans souris.
- **Contexte permanent en barre d'état** : serveur actif, base, latence, mode lecture seule. Un profil marqué *Production* doit être **visuellement distinct en continu** (bandeau ou accent coloré) : lancer une opération destructive sur le mauvais serveur est le risque principal de cet outil.
- **Jamais de gel de l'interface.** Tout accès MySQL, SSH ou SFTP est `async`/`await`, annulable, avec progression visible. Ces opérations passent par le réseau vers un serveur potentiellement distant.
- **Volumes réels.** `item_template` dépasse 50 000 lignes, `creature` plusieurs centaines de milliers. Virtualisation des grilles obligatoire, recherche et pagination **côté SQL** (`WHERE`/`LIMIT`) — ne jamais charger une table entière en mémoire pour filtrer ensuite.
- **Erreurs dans l'interface, pas en boîte de dialogue.** Les `MessageBox` sont réservées aux confirmations destructives (cf. §21). Le reste s'affiche en ligne ou dans le journal.
- **Feedback explicite.** Toute action d'écriture indique ce qui a été modifié et combien de lignes sont touchées, avant et après.

## Bibliothèques imposées

CommunityToolkit.Mvvm · MySqlConnector · SSH.NET · Microsoft.Data.Sqlite · Serilog · AvalonEdit (éditeur SQL : coloration, auto-complétion, favoris, historique, export CSV, transactions).

## Diagnostic d'échec SOAP : chercher la cause en amont

« SOAP injoignable » est vrai mais inutile. Sur le serveur de référence, trois pannes successives ont toutes produit ce message, et **aucune ne venait de SOAP** :

1. mot de passe MySQL divergent entre `authserver.conf` et `worldserver.conf` — le worldserver plantait en boucle ;
2. `DataDir` non déclaré, donc `"."`, résolu vers le `cwd` que PM2 impose (`server/etc`) alors que les données sont dans `server/data` — « Failed to find map files for starting areas » ;
3. worldserver simplement arrêté.

D'où `ServerDiagnosticService` : à chaque échec de connexion d'une commande GM, il interroge le serveur par SSH et explique. Sonde (vérifiée sur le serveur réel) :

- `pm2 describe worldserver | grep '│ status'` puis `'│ restarts'`, découpés sur le caractère `│` — `pm2 jlist` est un piège, un `grep` non contextualisé y rapporte les valeurs du **premier** processus, pas du worldserver.
- `ss -tln | grep ':8085'` pour le worldserver, `':7878 '` (espace final) pour l'adresse d'écoute de SOAP.
- `tail ~/.pm2/logs/worldserver-error.log`, en filtrant le bruit récurrent (« priority class »).

Le diagnostic distingue alors : worldserver absent, SOAP non activé, SOAP sur la boucle locale sans tunnel coché, ou problème de compte. **C'est du diagnostic, pas de la gestion de processus** : la frontière du §3 tient.

Le serveur de référence tourne sous **PM2** (`authserver` et `worldserver`), chemin `/home/warblups/server` avec `bin/ data/ etc/ logs/`.

## Méthode : vérifier contre le serveur avant d'écrire le code

Le profil actif est dans la base locale, mot de passe chiffré DPAPI sous la session Windows de l'utilisateur. Une session lancée sous ce compte peut donc **interroger le vrai serveur en lecture seule** pour valider une requête avant de l'intégrer. Reconstruire le script au besoin : lire la ligne active de `Servers`, déchiffrer via `ProtectedData.Unprotect` avec l'entropie `AzerothManager.Secrets.v1`, charger le `MySqlConnector.dll` du dossier de build, refuser tout ce qui n'est pas `SELECT`/`SHOW`/`DESCRIBE`, et ne jamais afficher de mot de passe.

Cette méthode a évité plusieurs erreurs réelles : `StatsCount` n'existe pas dans `item_template`, `dmg_min1` est un `Single` et `socketColor_1` un `SByte`, `character_stats` était vide, les paramètres dans `LIMIT`/`OFFSET` passent bien. Conclusion générale : **vérifier les noms de colonnes, les types et les offsets DBC sur les fichiers et le serveur réels**, jamais de mémoire. Les élargissements de type sont tolérés par MySqlConnector (`Byte`/`UInt16`/`UInt32` → `GetInt32`, `COUNT` → `GetInt32`), ce qui a été mesuré et non supposé. **`UInt64` → `GetInt64` passe également**, bien que ce ne soit pas un élargissement mais un changement de signe à taille égale : mesuré sur un `CAST(… AS UNSIGNED)`, parce que `guild.BankMoney` est un `bigint unsigned` et que les tables de guilde étaient vides — sans ligne à lire, seul un type reproduit permettait de trancher.

**Les sources du core sont sur le serveur de référence**, dans `~/azerothcore`. C'est le moyen le plus sûr de trancher une question de commande : lire la `ChatCommandTable` du `cs_*.cpp` concerné donne le nom exact, la signature du gestionnaire et le drapeau `Console::Yes`/`No`, sans rien supposer. Cette lecture a déjà évité plusieurs erreurs — voir la section « Annonces et arrêt planifié ».

Mais **la source n'est pas toujours cohérente avec elle-même** : le message d'erreur de `.server set motd` annonce une locale `zhWE` qui n'existe nulle part ailleurs dans le code, la vraie valeur de `localeNames` (`Common.cpp`) étant `zhTW`. Recopier un littéral trouvé dans un message ne remplace pas la table qui fait foi, et exécuter la commande pour de vrai reste le dernier mot.

Conséquence pratique : fermer l'application avant tout `dotnet build`, l'exécutable étant verrouillé.

### Vérifier l'interface à l'écran, pas au compilateur

**Un XAML qui compile ne prouve rien.** Un `GroupStyle`, un `MouseBinding` avec `RelativeSource`, une `StaticResource` mal nommée : tout cela produit une génération à zéro erreur et un écran vide ou inerte. Les défauts se voient en lançant l'application et en la regardant.

La capture se fait depuis PowerShell : `GetWindowRect` sur le `MainWindowHandle` du processus, puis `Graphics.CopyFromScreen`. Pour aller plus loin, `SetCursorPos` et `mouse_event` suffisent à cliquer, ce qui permet d'atteindre un module, de charger des données et d'ouvrir un panneau sans toucher à la souris. Relever les coordonnées sur une première capture plutôt que de les estimer : le rail se décale dès qu'un groupe s'ouvre.

Deux défauts trouvés ainsi le même jour, invisibles au build : un libellé du rail tronqué par le retrait des entrées, et « Elfe de la nuit » coupé dans un panneau de 300 px — corrigé en retirant une colonne au profit d'une infobulle.

Pour seulement **vérifier que le code compile** sans demander la fermeture de l'application, copier le projet (sans `bin/` ni `obj/`) dans un dossier temporaire et y lancer `dotnet build`. Rediriger `BaseOutputPath`/`BaseIntermediateOutputPath` **ne marche pas** : WPF génère alors les `*.g.cs` dans les deux `obj` et le compilateur voit chaque `InitializeComponent` en double (443 erreurs `CS0111`/`CS0102`). La copie complète, elle, compile proprement.

## Canal d'exécution : console GM ou SQL (CdC §5)

Règle structurante — chaque fonction passe par une commande GM, par du SQL, ou par les deux, et le choix découle du besoin, pas de la commodité d'implémentation :

- **Console GM** — l'action immédiate sur une cible **connectée** ou sur l'état vivant du monde : téléporter, invoquer, déplacer un PNJ, poser un waypoint, morpher, appliquer une aura, mode GM. Écrire ces états directement en base serait écrasé à la prochaine sauvegarde du joueur.
- **SQL** — la masse, le hors-ligne, l'historique et l'analyse : modifier des milliers d'objets, inspecter un joueur déconnecté, corréler, restaurer un personnage supprimé.
- **Les deux** — toute écriture dans `world` : SQL pour l'écriture, puis `.reload` pour la prise en compte.

Le `GmCommandService` est donc transverse et utilisé par la majorité des modules — ce n'est **pas** la vue de l'onglet console.

**Canal tranché : SOAP.** Vérifié dans les sources AzerothCore (`src/server/apps/worldserver/ACSoap`) — espace de noms `urn:AC`, préfixe `ns1`, méthode `executeCommand(command) -> result`, authentification HTTP Basic avec un **compte de jeu de niveau `SEC_ADMINISTRATOR` (gmlevel 3)**, pas l'utilisateur MySQL.

Deux pièges qui ont dicté l'implémentation :

- `SOAP.IP` vaut `127.0.0.1` par défaut : le port n'est pas joignable depuis l'extérieur. Et l'authentification Basic circule **en clair**. D'où le **tunnel SSH** (`ForwardedPortLocal`, port local 0) plutôt qu'exposer `SOAP.IP` sur le réseau. Le tunnel est monté par appel — simple, sans état partagé ; à mettre en cache si la latence gêne.
- En mode lecture seule, les commandes GM sont refusées **sauf** une liste blanche d'informatives (`GmCommandService.ReadOnlyAllowed` : `server info`, `pinfo`, `lookup`, `ticket list`…), une commande GM étant par défaut une écriture.
- **Une faute SOAP n'est pas une panne.** ACSoap répond par `soap_sender_fault(soap, printBuffer, printBuffer)` dès que le gestionnaire renvoie `false`, en y plaçant **la sortie complète de la commande**. Or `false` n'indique pas un échec côté AzerothCore : `HandleInstanceStatsCommand` affiche ses statistiques puis renvoie `false`. `GmCommandService` distingue donc `GmCommandFaultException` (refus de la commande, transport intact) du reste (transport en panne), et **ne lance le diagnostic SSH que pour le second**. Avant cette distinction, une commande parfaitement aboutie se voyait accoler « l'échec vient probablement du compte SOAP » — le contraire de ce que vise `ServerDiagnosticService`. Le tunnel doit aussi relayer cette exception sans réessayer sur l'autre adresse, faute de quoi la commande serait exécutée deux fois.

## Données du client 3.3.5a — icônes et noms de sorts

Vérifié sur les fichiers réels, pas de mémoire :

- **DBC** = format WDBC, en-tête de 20 octets (magie, nb d'enregistrements, nb de champs, taille d'enregistrement, taille du bloc de chaînes), tous les champs sur 4 octets, les chaînes étant des décalages dans le bloc final.
- **`ItemDisplayInfo.dbc` champ 5** = nom d'icône. `item_template.displayid` pointe directement dessus : **`Item.dbc` est inutile**.
- **Noms de sorts** : seize créneaux de langue à partir du **champ 136** de `Spell.dbc`. Un client localisé ne remplit **que le sien** — sur un client français, le créneau anglais est vide pour les 49 839 sorts. Ne jamais coder un créneau en dur : `DbcReader.GetFirstNonEmptyString` balaie la plage.
- **Les icônes sont des TGA**, pas des BLP : 128×128, non compressés, 32 bits BGRA, origine en bas à gauche. WPF ne lit pas le TGA ; `GameClientService.LoadTga` couvre ce seul cas.
- **`spell_dbc` en base ne sert à rien** pour les noms : 4 518 sorts personnalisés seulement, aucun nom localisé.
- **`Map.dbc`** : nom localisé à partir du champ **5**. **`AreaTable.dbc`** : à partir du champ **11**. Même règle que les sorts — balayer la plage, ne pas figer un créneau.

Le client n'est requis qu'à l'import : tout part dans SQLite (`DbcIcon`, `DbcSpell`, `IconImage`), et l'application est ensuite autonome. Chemin du client dans `Settings` sous `client.path`.

## Carte des joueurs — où sont les ressources du client

Piège principal : **l'art d'interface et les DBC ne sont pas dans `common.MPQ`** mais dans l'archive de **langue**, sous `Data\<locale>\locale-<locale>.mpq`. Chercher dans les archives racine ne donne rien.

- Fonds de continent : `Interface\WorldMap\<Dossier>\<Dossier>1..12.blp`, douze tuiles BLP2/DXT de 256×256 en 4 colonnes × 3 lignes. Le client n'affiche que **1002×668** du damier 1024×768 : recadrer pareil, sinon la projection est décalée.
- Dossiers : `Azeroth` (carte 0), `Kalimdor` (1), `Expansion01` (530), `Northrend` (571).
- **Le texte des continents est gravé dans l'image** : prendre l'archive de la langue du client, pas la première venue.
- Bornes de projection dans `WorldMapArea.dbc`, lignes où `areaID = 0`. Les axes du monde sont tournés :
  `pixelX = (Top − position_y) / (Top − Bottom) × largeur` et `pixelY = (Left − position_x) / (Left − Right) × hauteur`.
- `tools/extract_worldmaps.py` (mpyq + Pillow) extrait et assemble les quatre PNG dans `%AppData%\AzerothManager\maps`. **Les images ne sont jamais versionnées** : ce sont des ressources du jeu, elles restent sur le poste.
- Une carte d'instance n'a pas de fond : ces joueurs sont listés à part plutôt que placés au hasard.

## Tickets GM — pièges relevés (vérifié aux sources)

- **C'est la colonne `type` qui fait foi pour l'état**, pas `closedBy` : côté serveur `IsClosed()` teste `type != TICKET_TYPE_OPEN`. Un ticket clos depuis la console garde `closedBy` à zéro. Filtrer sur `closedBy` afficherait des tickets clos comme ouverts. `type` : 0 ouvert, 1 clos, 2 personnage supprimé.
- **`assignedTo` et `closedBy` sont des GUID de personnage**, pas des noms : joindre `characters` pour afficher un nom.
- Toutes les commandes `.ticket *` sont `Console::Yes`. Signatures : `assign <id> <nom>`, `comment <id> <texte>`, `response append <id> <texte>`, `complete <id> [réponse]`, `close <id>`, `escalate <id>`, `delete <id>`, `viewid <id>`.
- `.go ticket <id>`, `.appear` et `.summon` agissent sur le **personnage GM qui exécute** : ils exigent une session connectée, d'où l'avertissement dans l'interface.

## Restauration ciblée

- La suppression d'un personnage conserve la ligne dans `characters` et déplace le nom dans `deleteInfos_Name`, avec `deleteInfos_Account` et `deleteDate`. D'où la lecture en SQL.
- `.character deleted restore <recherche> [nouveauNom] [nouveauCompte]` — le nouveau nom sert si l'ancien a été repris, le nouveau compte si celui d'origine a disparu.
- **Sans rapport avec `.account delete`**, qui efface les personnages en dur : ceux-là n'apparaissent jamais dans la liste des récupérables.

## Armurerie — pièges du schéma (vérifié sur serveur réel)

- **`character_stats` n'est écrite qu'à la déconnexion** du personnage. Elle peut être absente — c'était le cas du seul personnage du serveur de test — ou périmée pour un joueur connecté. L'interface le dit au lieu d'afficher des zéros trompeurs.
- `character_inventory` ne stocke que le **guid** de l'objet : il faut joindre `item_instance` pour obtenir l'`itemEntry`, puis `item_template`.
- Équipement porté = `bag = 0` et `slot < 19` (énumération `EquipmentSlots`). Les emplacements vides sont conservés dans la fiche : elle doit montrer ce qui manque.
- Guilde : `guild_member` → `guild` (nom) et `guild_rank` (`guildid` + `rid` = `gm.rank` → `rname`).
- Les jointures inter-bases sont qualifiées avec les noms de base du profil, le serveur MySQL étant commun.

## Modération — quatre tables, une seule vue

Le module Comptes sanctionne un compte à la fois ; celui-ci répond à « qui est sanctionné, par qui, pourquoi ». Il consolide quatre tables aux colonnes voisines mais non identiques :

- `auth.account_banned` (id, bandate, unbandate, bannedby, banreason, **active**)
- `auth.ip_banned` (ip, bandate, unbandate, bannedby, banreason) — **pas de colonne `active`** : l'état se déduit de `unbandate`
- `characters.character_banned` (guid, …, active) — jointure inter-bases
- `auth.account_muted` (guid, mutedate, **mutetime**, mutedby, mutereason) — `mutetime` est une **durée**, pas une date de fin

Convention commune : `unbandate == bandate` vaut **définitif**.

Commandes : `.ban account|character|ip|playeraccount <cible> <durée> <motif>`, `.unban …`, `.baninfo …`. **`ban playeraccount` bannit le compte d'un personnage** sans avoir à le chercher. Les écritures passent par les commandes, jamais par SQL : elles tiennent les tables à jour **et déconnectent le joueur** — un INSERT laisserait un banni connecté.

Un bannissement d'IP touche tout un foyer : l'interface l'avertit avant confirmation.

## Personnages — la limite de `.modify money`

Toutes les actions acceptent un nom de joueur, **sauf les commandes `.modify`** : `HandleModifyMoneyCommand(ChatHandler*, Tail money)` n'a pas de `PlayerIdentifier` et agit sur la **cible sélectionnée en jeu**. Il n'existe donc aucun moyen de fixer l'or d'un joueur par son nom.

D'où la règle appliquée : **l'or s'écrit en SQL, et seulement hors ligne**. Un joueur connecté garde son état en mémoire et le serveur écraserait l'écriture à la prochaine sauvegarde — l'interface le refuse et l'explique, plutôt que de laisser croire à une modification qui sera perdue.

Commandes qui, elles, acceptent un nom : `.character level|rename|customize|changefaction|changerace <nom>`, `.tele name <nom> <destination>` (et `$home`), `.recall`, `.summon`, `.appear`, `.revive`, `.kick`.

Les services de personnage posent un **drapeau** dans `characters.at_login` : l'effet n'a lieu qu'à la prochaine connexion. Le module décode le masque pour montrer ce qui est en attente.

`world.game_tele` contient 1 989 destinations sur le serveur de référence — c'est la bibliothèque du module Téléportation.

Cette table **ne porte pas de zone**, seulement `map` et les coordonnées. Le nom de zone français affiché est donc déduit de la position par `GameClientService.ZoneAt`, le même mécanisme que la carte des joueurs et l'édition du monde. Il exige que les DBC aient été importés : sans `DbcZone`, la colonne affiche « — » au lieu de mentir.

## Courrier — commandes et limites (vérifié aux sources)

Canal imposé : la commande GM. Insérer dans `characters.mail` à la main supposerait de créer les lignes d'`item_instance` des pièces jointes et d'en gérer les identifiants — fragile pour aucun gain.

- `.send mail <joueur> "sujet" "texte"` · `.send items <joueur> "sujet" "texte" id[:qté] …` · `.send money <joueur> "sujet" "texte" <montant>`
- Sujet et texte sont des `QuotedString` : ils doivent être entre guillemets, et les guillemets internes cassent l'analyse — `MailService.Quote` les neutralise.
- **`MAX_MAIL_ITEMS = 12`** pièces jointes par courrier.
- **L'or et les objets ne peuvent pas voyager ensemble** : `.send items` ne prend pas de montant. Joindre les deux provoque **deux courriers**, et l'interface le dit.
- `.send money` accepte le cuivre brut ou une notation `10g5s`.
- Aucune commande d'envoi groupé : l'envoi en masse boucle sur les destinataires, une commande chacun.
- Lecture par SQL : `mail` + `mail_items` + jointure `item_instance` sur `item_guid` pour obtenir l'`itemEntry`.

## Comptes — schéma auth (vérifié sur serveur réel)

- `account` porte `salt`/`verifier` (SRP6), `expansion`, `last_ip`, `last_login`, `joindate`, `online`, `locked`, `mutetime`/`mutereason`/`muteby`.
- `account_access` (id, gmlevel, **RealmID**, comment) — le rang est **par royaume**, `-1` valant tous.
- `account_banned` (id, bandate, unbandate, bannedby, banreason, **active**) : `unbandate == bandate` signifie **définitif**.
- `ip_banned` (ip, bandate, unbandate, bannedby, banreason).
- Les deux bases étant sur le même serveur MySQL, le comptage des personnages se fait par sous-requête inter-bases qualifiée avec le nom de base du profil.
- Commandes câblées, toutes `Console::Yes` donc utilisables par SOAP : `.account create|delete`, `.account set password|gmlevel|addon`, `.ban account <nom> <durée> <motif>` (durée `10m`/`2h`/`1d`, `0` = définitif), `.unban account`, `.mute <personnage> <minutes> <motif>`, `.unmute`, `.kick`. **Mute et kick visent un personnage, pas un compte.**

## Comptes — contrainte SRP6 (vérifié aux sources)

**Ne jamais créer un compte ni changer un mot de passe par SQL.** Dans `AccountMgr::CreateAccount`, le mot de passe devient un couple SRP6 `salt` + `verifier` via `SRP6::MakeRegistrationData(username, password)` : un `INSERT` dans `auth.account` supposerait de réimplémenter SRP6 en C#. Ces deux opérations passent obligatoirement par la commande GM.

- `.account create <nom> <motdepasse> [email]` et `.account set password` — tous deux `Console::Yes`, donc disponibles via SOAP.
- `.account password` est `Console::No` : c'est le libre-service joueur, il exige une session. Ne pas le confondre avec `.account set password`.
- Nom et mot de passe sont mis en **majuscules** avant calcul du verifier : les mots de passe sont insensibles à la casse.
- Limites : nom 17, mot de passe 16, email 255 (`MAX_ACCOUNT_STR` / `MAX_PASS_STR` / `MAX_EMAIL_STR`). À valider dans l'interface.

Le module Comptes est donc hybride : création, mot de passe, niveau GM, extension et email par commande GM ; recherche, listing, IP, bannissements et historique par SQL.

## Modules d'administration de jeu (CdC §9)

Le §8 décrit des *entités*. Les modules ci-dessous correspondent au **geste quotidien d'un GM** et constituent la valeur d'usage réelle de l'application.

| Module | Tables / mécanisme | Note |
|---|---|---|
| Courrier en jeu | `characters.mail`, `mail_items` | Envoi unitaire ou en masse avec or et objets attachés. Canal de dédommagement après rollback/bug et de récompense d'événement. |
| Services de personnage | drapeaux `characters.at_login` | Renommage, customisation, changement de race/faction, reset talents/sorts. Le geste d'admin le plus banal d'AzerothCore. |
| Modération | `account_banned`, `character_banned`, `ip_banned`, `mutetime`/`mutereason` | Durées, motifs, kick, et **historique des sanctions avec son auteur**. |
| Tickets GM | `characters.gm_ticket` | Lire, assigner, répondre, clôturer — sinon l'admin doit rester connecté en GM. |
| Restauration ciblée | `deleteInfos_Account` / `deleteInfos_Name` / `deleteDate` sur `characters` | Équivalent `.character deleted restore`, plus restauration d'objets. **Distinct** de la restauration de base entière de l'Updater, qui écrase tout le monde. |
| Verrous d'instance | `instance`, `character_instance`, `instance_reset` | Reset de lockout par joueur ou par groupe. |
| Événements de jeu | `world.game_event` + `.event` | Activation, désactivation, planification des événements mondiaux. |
| Annonces / arrêt en jeu | `.announce`, `.notify`, `.server shutdown` | Annonces récurrentes, MOTD, arrêt avec compte à rebours annoncé. Pas le cycle de vie des binaires (→ Updater). |
| Royaumes | `auth.realmlist`, `auth.account_access` | Flag, population forcée. **Les niveaux GM sont par royaume** (`account_access.RealmID`) — le CdC les décrit à tort comme globaux. |
| Économie / anti-triche | `auctionhouse`, appender DB (table `logs`) si activé | Masse monétaire, distribution de l'or, détection de pics et de duplications. |
| Banque de guilde | `guild_bank_item`, `guild_bank_tab`, `guild_bank_eventlog` | Les litiges de vol de banque de guilde sont un motif de ticket récurrent. |

**Règle d'intégration `.reload`** : toute écriture dans la base `world` depuis l'éditeur SQL doit proposer le `.reload` correspondant via la console GM. Sans lui, la modification reste invisible jusqu'au redémarrage et passe pour un bug.

## Édition du monde et personnage avancé (CdC §10-11)

Majoritairement des commandes GM, donc un personnage GM connecté est requis. L'apport de l'application est la persistance, la recherche et la traçabilité que la saisie en jeu ne donne pas.

- **PNJ** (`world.creature`) — placement, déplacement, `.npc set model/movetype`, dialogue et emotes pour l'animation d'événements, `.possess`.
- **Waypoints** (`world.waypoint_data`) — édition d'un trajet de patrouille complet, pas point par point.
- **GameObjects** (`world.gameobject`) — portes, coffres, portails, phasage. Tables et commandes distinctes des créatures : ne pas fusionner les deux modules.
- **Téléportation** (`world.game_tele`) — bibliothèque de destinations par continent, sauts par coordonnées ou vers une entité, `.groupsummon`. **`.recall`** ramène un joueur à sa position précédente : c'est le filet de sécurité après une téléportation ratée, à exposer systématiquement à côté de toute action de téléportation.
- **Métiers et sorts** — `.learn all recipes <métier>`, `.maxskill`. Motif de support récurrent (perte de recettes).
- **Modification en direct**, **familiers**, **apparence/auras**, **mode GM et cheats** — voir §11.
- **`.list item`** — retrouve les détenteurs d'un objet ; c'est ce qui rend l'enquête sur une duplication actionnable, à rattacher au module économie.

## Ce que SOAP ne peut pas faire — le recensement Console::No

Toutes les commandes GM ne sont pas exécutables à distance. `Console::No` signifie que la commande a besoin d'une **session de jeu** : elle relève la position ou la cible du personnage qui l'exécute. SOAP n'en a aucune, donc elle échoue — il ne faut pas livrer le bouton.

Sont ainsi hors de portée, et il est inutile de réessayer : `.npc add`, `.npc move`, `.npc set *`, `.wp add|modify|show`, `.gobject add|move|turn`, `.learn all recipes`, `.maxskill`, `.modify *` (dont `.modify money`), `.account password`, `.go *`, `.appear`, `.summon` côté maître de jeu. En conséquence, **« Métiers et sorts » et « Modification en direct » du §11 n'ont pas d'implémentation possible par ce canal** ; le dire est plus utile que de livrer une interface qui renvoie une erreur.

Le canal de repli est le SQL, et c'est ce que fait le module Édition du monde.

## Édition du monde — spawns en SQL (CdC §10)

`WorldEditService` couvre `world.creature` (155 085 lignes) et `world.gameobject` (97 426) dans un éditeur unique : les deux tables partagent `guid`, `id`, `map`, `position_*`, `orientation`, `spawntimesecs` et `Comment`. Recherche par nom, par identifiant de modèle ou par guid, filtre par carte, pagination côté SQL. Les noms français viennent de `creature_template_locale` / `gameobject_template_locale`, locale **`frFR`** (8 locales présentes, la colonne s'appelle `Name` pour les créatures et `name` pour les objets).

Trois points vérifiés sur le serveur réel, à ne pas redécouvrir :

- **`zoneId` et `areaId` de ces tables ne sont pas fiables** : `zoneId = 0` pour 149 887 des 155 085 lignes de `creature` et 58 415 des 97 426 de `gameobject`. Ce sont des caches remplis au besoin par le serveur, pas des données de placement. La zone est **déduite de la position** via `GameClientService.ZoneAt` — exactement comme dans la carte des joueurs et la bibliothèque de téléportation. Même mécanisme, trois modules : ne pas le réécrire.
- **Il n'existe ni `.reload creature` ni `.reload gameobject`.** `cs_reload.cpp` recharge les *modèles* (`creature_template`, `creature_text`, `gameobject_template_locale`, `spawn_group`…), jamais les *spawns*. La règle « écriture dans `world` → proposer le `.reload` » du §9 **ne s'applique donc pas ici** : une position ou un temps de réapparition modifié ne prend effet qu'au redémarrage du worldserver. Le module l'annonce au lieu d'offrir un `.reload` inerte.
- **Les tables satellites ne nomment pas toutes leur colonne `guid`.** À la suppression d'un spawn, une ligne orpheline provoque une erreur au chargement du monde, d'où le nettoyage en transaction : `creature_addon`(guid), `creature_formations`(**leaderGUID**, **memberGUID**), `game_event_creature`(guid), `pool_creature`(guid) ; `gameobject_addon`(guid), `game_event_gameobject`(guid), `pool_gameobject`(guid). Cas particulier de **`linked_respawn`** : ses colonnes `guid`/`linkedGuid` portent un guid de créature **ou** de gameobject selon `linkType` (0 créature→créature, 1 créature→objet, 2 objet→objet, 3 objet→créature). Les deux espaces de guid étant indépendants, filtrer sans le `linkType` supprimerait le lien d'une créature qui porte le même numéro. Chaque table est testée dans `information_schema` avant usage : elle peut manquer selon les modules installés.

Le guid et l'identifiant de modèle ne sont pas modifiables : changer l'un revient à créer un autre spawn, changer l'autre à en faire une créature différente. Le placement, lui, reste impossible — voir la section précédente.

## Logs — la moitié qui revient au Manager

Le §3 partage les journaux en deux, et c'est ce partage qui dicte le module : la lecture **en direct** d'`Auth.log` et `Server.log` appartient à l'Updater, qui la fait déjà avec son propre `Platform/LogTail.cs`. Le Manager prend l'autre moitié : **consultation d'une fenêtre + filtrage + corrélation avec les actions administrateur**. D'où l'absence volontaire de rafraîchissement automatique ici — le dupliquer serait réimplémenter l'Updater.

La corrélation est le seul apport qu'un `tail` ne donne pas : `LogService.ReadAdminActions` relit la table `History` locale (SQL, commandes GM, actions) sur l'intervalle du journal chargé et **entrelace les deux par l'horodatage**. Deux conséquences :

- L'historique est horodaté sur le poste, le journal sur le serveur, et les horloges ne sont pas synchronisées à la seconde : l'intervalle est élargi d'**une minute de chaque côté**.
- Sans horodatage dans les lignes, l'entrelacement n'a aucun sens : `LogReadResult.HasTimestamps` le signale, l'interface désactive la case et explique qu'il manque le drapeau 1 à l'appender. Ne pas inventer un ordre.

### Le serveur de référence n'écrit aucun préfixe (vérifié)

Mesuré sur les fichiers réels, et c'est le fait le plus important du module : **les quatre appenders ont leurs drapeaux à 0**.

```
Appender.Console=1,4,0,"1 9 3 6 5 8"        Appender.Console=1,5,0,…   (authserver)
Appender.Server=2,5,0,Server.log,w          Appender.Auth=2,5,0,Auth.log,w
Appender.Playerbots=2,5,0,Playerbots.log,w
Appender.Errors=2,2,0,Errors.log,w
```

Le troisième champ est le masque de drapeaux : **1 = horodatage, 2 = niveau, 4 = type de filtre**. À 0, les lignes n'ont ni date ni étiquette. Sur 3 196 lignes réelles d'`Auth.log`, `DBImport.log`, `Server.log` et `worldserver-error.log` : **0 ligne horodatée**, et 23 lignes seulement reçoivent un niveau — toutes le même `Can't set process priority class, error: Permission denied`, c'est-à-dire le bruit récurrent que `ServerDiagnosticService` filtre déjà.

Conséquences, assumées dans l'interface plutôt que subies :

- La corrélation avec l'historique est **indisponible en l'état** — c'est pourtant l'apport du module. Elle le redevient en passant les drapeaux à `3` (`Appender.Server=2,5,3,Server.log,w`) puis en redémarrant. Le rappel de l'interface donne cette ligne exacte.
- Le filtre par niveau serait un **leurre** : tout tombe dans « Autres ». D'où `LogReadResult.HasLevels`, qui désactive les cases et le bouton « Problèmes » — sinon un clic viderait la grille sans explication. Seule la recherche par texte reste utile sur ce serveur.
- Un journal sans préfixe est aussi le cas qui rendait l'héritage de niveau dangereux (voir plus bas) : ce n'était pas une hypothèse d'école, c'est la configuration réelle.

Contenu réel des deux dossiers, qui confirme de ne pas coder les noms en dur — `DBImport.log` et `Playerbots.log` auraient été manqués :

- `~/server/logs` : `Auth.log`, `DBImport.log`, `Errors.log` (vide), `Playerbots.log` (vide), `Server.log`
- `~/.pm2/logs` : `authserver-error.log`, `authserver-out.log`, `worldserver-error.log` (2,1 Mo), **`worldserver-out.log` (9,9 Mo)** — qui justifie à lui seul de ne transférer que la fin.

Points tranchés, à ne pas redécouvrir :

- **Aucun nom de fichier n'est codé en dur** : le dossier est listé. Selon les appenders un serveur écrit `Server.log` et `Auth.log`, mais aussi `DBErrors.log`, `Errors.log`, des `gm_*.log` — et sous PM2 les `worldserver-error.log` / `worldserver-out.log`, **seuls à contenir un plantage survenu avant l'ouverture de `Server.log`** (c'est exactement le cas « Failed to find map files » du diagnostic SOAP). `/home/<user>/.pm2/logs` est donc un emplacement candidat, construit explicitement : **SFTP n'a pas de shell, donc pas d'expansion de `~`**.
- **Le listage passe par `SftpClient.ListDirectory`**, pas par un `ls` analysé au texte : taille et date arrivent déjà typées, et la mise en forme de `ls` varie d'une distribution à l'autre.
- **Seule la fin du fichier est transférée** (`SshService.ReadTailAsync` se positionne par décalage ; en local, `FileShare.ReadWrite` est obligatoire, le serveur garde son journal ouvert). Quand la lecture commence au milieu du fichier, **le premier saut de ligne est la seule frontière sûre** : avant lui on a une ligne tronquée et peut-être un caractère accentué coupé en plein milieu de sa séquence UTF-8.
- **Le préfixe d'une ligne dépend des drapeaux de l'appender** (1 = horodatage, 2 = niveau, 4 = type de filtre) : les trois parties sont optionnelles, et le format de l'horodatage a varié selon les versions. `LogLineParser` accepte donc plusieurs formes (`_`, espace ou `T` entre date et heure ; `:` ou `-` dans l'heure ; crochets et fractions de seconde facultatifs) **au lieu d'en figer une de mémoire**. L'analyse est isolée dans un fichier sans dépendances pour être éprouvée seule, ce qui a servi deux fois : d'abord sur des exemples pendant que le serveur était éteint, puis en mesure sur les journaux réels.
- **L'héritage de niveau est borné.** Une ligne sans préfixe est la suite d'un message multi-ligne (pile d'appels, bloc SQL) et hérite de l'heure et du niveau de la ligne qui l'a ouverte — sinon elle disparaîtrait au premier filtre. Mais l'héritage n'a lieu **que si un horodatage a déjà été vu** : dans un journal écrit sans préfixe du tout, le niveau de la première ligne contaminerait tout le fichier.
- Le niveau est cherché **en mot entier** (`\b`), comme dans l'Updater : sinon « information » donne INFO et « warnaccount » donne WARN.

Le filtrage (niveaux, texte) s'applique à la fenêtre déjà lue, **sans relire le fichier** : changer une case ne doit pas relancer un transfert SFTP. La quantité lue est un choix explicite de l'interface (200 Ko / 1 Mo / 4 Mo) et l'en-tête annonce ce qui a réellement été transféré ; l'affichage est plafonné à 5 000 lignes, la fin étant conservée.

## Annonces et arrêt planifié (CdC §9)

Module entièrement piloté par commandes GM, et c'est ce qui le rend faisable : les tables de `cs_server.cpp`, `cs_message.cpp` et `cs_autobroadcast.cpp` sont **toutes `Console::Yes`** — seul `.whispers` ne l'est pas, et il ne relève pas de l'annonce. Rien ici n'exige de personnage connecté, contrairement aux §10-11.

Pièges vérifiés aux sources, puis à l'exécution :

- **`.server shutdown` n'arrête pas ce serveur.** PM2 tourne avec `autorestart = true` (257 redémarrages relevés sur le worldserver) : le processus sort et PM2 le relance aussitôt. Arrêt et redémarrage y produisent donc le même effet — un redémarrage avec compte à rebours annoncé. Pour arrêter réellement, c'est l'Updater. La frontière du §3 tient, et l'interface le dit au lieu de laisser croire à un arrêt.
- **Le motif d'un arrêt ne peut pas commencer par un nombre de 0 à 125.** La signature est `(std::string time, Optional<int32> exitCode, Tail reason)` : le nombre est pris pour le code de sortie, et surtout la branche qui le reçoit appelle `ShutdownServ(delay, 0, *exitCode)` **sans le motif**. Le texte disparaît en silence. `AnnounceService.ReasonWouldBeSwallowed` refuse plutôt que de réécrire le message de l'utilisateur, et **aucun code de sortie n'est jamais transmis**.
- **Un délai sans suffixe est lu en secondes.** `TimeStringToSecs` (`Util.cpp`) accepte `d`, `h`, `m`, `s`, cumulables (`1h30m`), et renvoie 0 sur tout autre caractère ; le gestionnaire se rabat alors sur un entier nu. Donc `5` planifie l'arrêt dans **cinq secondes**, pas cinq minutes. `TryParseDelay` reproduit la règle pour afficher la durée retenue avant l'envoi.
- **Les annonces signées affichent « Console ».** `HandleNameAnnounceCommand` lit `handler->GetSession()` et retombe sur la chaîne `"Console"` quand il n'y en a pas — c'est exactement le cas de SOAP. On ne peut pas signer une annonce du nom d'un personnage depuis l'application.
- **`.server set motd` exige une locale**, et l'identifiant de royaume optionnel la précède : `.server set motd Bonjour à tous` échoue avec « locale (Bonjour) is not valid ». En `enUS` la valeur va dans `auth.motd`, sinon dans `auth.motd_localized`. **La locale chinoise traditionnelle est `zhTW`** : le message d'erreur de la commande dit `zhWE`, qui n'existe nulle part ailleurs — coquille d'AzerothCore, repérée en comparant la sortie réelle de `.server motd` à `localeNames`.
- **`.server set closed` n'est pas persistant** : l'état retombe au redémarrage du worldserver.

Annonces récurrentes — `.autobroadcast list|add|remove|locale`, toutes `Console::Yes` :

- La table est **`auth.autobroadcast`**, pas dans `world` : les commandes passent par `LoginDatabase` et filtrent sur `realmid`, la valeur `-1` valant tous les royaumes. La liste est lue en SQL plutôt qu'en analysant la sortie de `.autobroadcast list`, pour récupérer identifiant et poids exploitables.
- **Aucun `.reload` à proposer** : `HandleAutobroadcastAddCommand` et `…Remove…` appellent eux-mêmes `sAutobroadcastMgr->LoadAutobroadcasts()`. La règle « écriture dans `world` → `.reload` » du §9 ne s'applique pas ici, et un bouton `.reload` serait redondant.
- **`AutoBroadcast.On = 0` sur le serveur de référence** : une annonce ajoutée part en base et n'est jamais diffusée. Le module lit `AutoBroadcast.On`, `Timer` et `Center` dans `worldserver.conf` (localement sous Windows, par SSH sous Linux) et l'annonce — sans quoi l'absence de diffusion passerait pour un défaut de l'application. C'est de la consultation de configuration, pas de la gestion : la frontière du §3 tient.

## Inventaire et banque (CdC §8)

Le module répond à « j'ai perdu mon objet ». D'où son périmètre : **tout ce qui appartient au personnage**, et pas seulement ses poches. Un objet « perdu » attend souvent au courrier ou à l'hôtel des ventes, et ne pas l'y chercher ferait conclure à une disparition. Consultation en SQL, écritures par commande GM, comme les guildes.

Schéma vérifié sur le serveur réel :

- **`character_inventory.bag` vaut 0 pour le personnage, sinon c'est le guid de l'`item_instance` du sac**, pas un numéro d'emplacement. Pour savoir si le contenu d'un sac est porté ou en banque, il faut d'abord relever l'emplacement du sac lui-même : c'est le piège principal de la table, isolé dans `InventoryLayout.Locate` et couvert par les tests.
- Plages de `Player.h` : 0-18 équipement, 19-22 sacs portés, 23-38 sac à dos, 39-66 banque, 67-73 sacs de banque, 74-85 rachat, 86-117 trousseau, 118-149 monnaies.
- **Les emplacements de rachat ne sont jamais en base** : `_SaveInventory` les efface à chaque sauvegarde. Un objet vendu n'existe donc en base que s'il a été archivé.
- `item_instance.flags & 1` = lié (`ITEM_FIELD_FLAG_SOULBOUND`) ; `creatorGuid` est un guid de personnage.
- `mail_items.receiver` est dénormalisé : on filtre dessus sans passer par `mail`. Seul `messageType = 0` porte un guid de personnage en `sender` (2 hôtel des ventes, 3 PNJ, 4 objet, 5 calendrier — `Mail.h`).
- `auctionhouse.time` est l'échéance de l'enchère. **Un personnage de l'AHbot porte plus de 45 000 enchères** : la section est plafonnée (`InventoryService.AuctionLimit`) et l'en-tête annonce le total réel.
- La banque, le courrier et `recovery_item` sont vides sur le serveur de référence ; 2 234 personnages, bots compris.

Commandes, lues dans `cs_misc.cpp` et `cs_item.cpp`, toutes `Console::Yes` :

- **`.additem <joueur> <id> <n>` n'ajoute qu'à un joueur connecté** (`LANG_PLAYER_NOT_FOUND` sinon). Hors ligne, l'objet part par courrier (`MailService`) au lieu d'échouer, et l'interface annonce le canal pris.
- **`.additem <joueur> <id> -<n>` retire, connecté ou non.** Hors ligne, le serveur écrit lui-même en base et nettoie les tables satellites (cadeau, remboursement, échange lié, `item_loot_storage`). Le retrait se fait **par modèle, jamais par pile** : on ne peut pas viser un exemplaire. En ligne, l'ordre est fixe (sac à dos, trousseau, sacs, équipement, banque) ; hors ligne, la requête n'a pas d'`ORDER BY` et **épargne un sac qui contient encore des objets**. Le courrier et l'hôtel des ventes ne sont jamais touchés. La confirmation annonce le total d'exemplaires concernés.
- **`.item restore <id> <joueur>`** renvoie par courrier une ligne de `recovery_item` ; `.item restore list` n'est pas utilisée, la table étant lue en SQL.
- Aucune écriture SQL ici : un `DELETE` laisserait l'objet en mémoire chez un joueur connecté, et le serveur le réécrirait à la sauvegarde suivante.

**Archivage : `ItemDelete.Method` commande tout.** La destruction comme la vente passent par `WorldSession::recoveryItem`, qui ne range rien si `Method` vaut 0 ; `Vendor` ne fait qu'y ajouter les ventes, et le rachat au marchand retire l'archive. **`Vendor = 1` seul est un piège** : `.item restore` est alors accepté (`HasItemDeletionConfig` teste `Method || Vendor`) mais la table reste vide. Seuils par défaut : qualité rare (3) et niveau d'objet 80, conservation illimitée (`KeepDays = 0`). Le serveur de référence a tout à 0. Le module lit ces réglages par `WorldServerConfig`, **le lecteur de `worldserver.conf` commun avec les annonces**, et le dit plutôt que de montrer une liste vide sans explication.

## Guildes et banque de guilde (CdC §8 et §9)

Module hybride selon le §5 : **SQL pour la consultation**, qui porte ici l'essentiel de la valeur — le §9 range les litiges de vol de banque parmi les motifs de ticket récurrents, et ce qui les tranche est le *journal*, pas une commande — et **commandes GM pour les écritures**, faute de quoi le worldserver ignorerait un membre inséré à la main jusqu'au redémarrage.

Les sept commandes `.guild` sont `Console::Yes` (`cs_guild.cpp`). Une seule contrainte de fond :

- **`.guild create` exige un joueur connecté** : `if (!target || !target->IsConnected())` refuse, la guilde étant construite à partir de l'objet `Player` en mémoire. En revanche `invite`, `uninvite` et `rank` **fonctionnent hors ligne**, en passant par `sCharacterCache->GetCharacterGuildIdByGuid`. Ne pas généraliser la contrainte de `create` aux autres.
- Les noms de guilde sont des **`QuotedString`** : sans guillemets, un nom en deux mots est tronqué. Même traitement que dans `MailService`.
- `.guild delete "Nom"` appelle `Disband()` : guilde, rangs, membres et contenu de banque disparaissent, sans reprise possible depuis l'application.
- `.guild rank <joueur> <rid>` : `ChangeMemberRank` refuse un `rid` absent de *cette* guilde. L'interface vérifie contre les rangs chargés avant d'envoyer.

Schéma vérifié sur le serveur réel :

- `guild` — `BankMoney` est un **`bigint unsigned` en cuivre**, `createdate` un horodatage Unix, `motd` 128 caractères et `info` 500.
- `guild_member.rank` renvoie à `guild_rank.rid` **de la même guilde** : les rangs sont numérotés par guilde, pas globalement. La jointure doit donc porter sur `guildid` *et* `rid`.
- `guild_rank` — `rights` est un masque, `BankMoneyPerDay` en cuivre, `0xFFFFFFFF` valant « sans limite » (le maître de guilde).
- `guild_bank_item` ne stocke que le **guid** de l'objet : joindre `item_instance` pour l'`itemEntry`, puis `world.item_template` — exactement comme `character_inventory` dans l'armurerie.
- `guild_bank_eventlog` — **`ItemOrMoney` a deux sens selon `EventType`** : un identifiant d'objet pour les mouvements d'objets, un **montant en cuivre** pour les types 4, 5 et 6 (dépôt, retrait, réparation). C'est le piège principal de la table : joindre `item_template` sans filtrer le type afficherait le nom de l'objet dont l'identifiant vaut le montant, soit « 500 pièces d'or » rendu en nom d'épée. Types : 1 dépôt d'objet, 2 retrait, 3 déplacement, 4 dépôt d'argent, 5 retrait d'argent, 6 réparation, 7 déplacement, 9 achat d'onglet.
- `guild_eventlog` — `PlayerGuid1` est la **cible**, `PlayerGuid2` l'**auteur** quand l'action en a un. Types : 1 invitation, 2 arrivée, 3 promotion, 4 rétrogradation, 5 exclusion, 6 départ.

**Les huit tables de guilde sont vides sur le serveur de référence** : aucune guilde n'y a été fondée. Comme pour `character_stats` dans l'armurerie, le module le dit plutôt que de laisser croire à une panne — et les requêtes ont dû être validées autrement, en les exécutant pour vérifier colonnes et jointures, et en reproduisant les types à part.

## Événements de jeu (CdC §9)

Les quatre commandes `.event` — `activelist`, `start`, `stop`, `info` — sont `Console::Yes`. Le serveur de référence porte **182 événements**, 11 460 liens de créature et 32 363 liens d'objet.

**Deux mécanismes distincts, et c'est l'essentiel du module :**

- `.event start` / `.event stop` agissent **en mémoire seulement**. `GameEventMgr` modifie son `_gameEvent[id]` sans rien écrire dans `world.game_event` : au redémarrage, la planification reprend la main.
- Modifier la planification en base est **persistant mais inerte à chaud** : il n'existe **pas de `.reload game_event`** (`cs_reload.cpp` ne recharge que `game_event_npc_vendor`). Même situation que les spawns de l'édition du monde — la règle « écriture dans `world` → `.reload` » du §9 ne s'applique pas.

Il n'y a donc aucun moyen d'obtenir les deux à la fois, et l'interface le dit au lieu de laisser croire à un bug.

Pièges vérifiés, dont un trouvé en comparant le module au serveur :

- **`occurence` et `length` sont en minutes** : `GameEventMgr` les multiplie par `MINUTE`. 525 600 pour un événement annuel, 20 160 pour une fête de deux semaines. Les lire en secondes donnerait des périodes absurdes. La durée ne peut pas dépasser la période, sinon l'événement ne s'arrête jamais.
- **`Holidays.dbc` écrase la planification de la base.** Le module affichait 15 jours pour Brewfest quand `.event info` en annonçait 16 : `SetHolidayEventTime` remplace `Length` — et souvent `Occurence` et `Start` — par les valeurs du DBC. Elle sort immédiatement si **`holidayStage` vaut 0** : c'est bien l'**étape**, pas `holiday`, qui déclenche l'écrasement. Pour ces événements, modifier `occurence`/`length` en base ne sert à rien, même après redémarrage ; la colonne « Planif. » dit d'où vient réellement la valeur.
- **Un `eventEntry` négatif** dans `game_event_creature` / `game_event_gameobject` désigne un spawn **retiré** pendant l'événement : `ApplyNewEvent` appelle `GameEventUnspawn(-eventId)`. Les additionner aux autres laisserait croire que l'événement ajoute ce qu'il enlève. Vu en vrai : Invasion du Fléau `+332 / −50`, Sanssaint `+10 145 / −7`.
- **`announce`** : 1 annonce toujours, 0 jamais, **2 suit `Event.Announce` de la configuration** (`ApplyNewEvent`).
- **L'état actif ne se recalcule pas depuis la base.** Ce serait réimplémenter `IsActiveEvent`, et se tromper dès qu'un événement a été lancé à la main — puisque cela ne laisse aucune trace en base. Il est demandé au serveur par `.event activelist`, dont la sortie console est `<id> - <description>  [active]`.
- `game_event_quest` **n'existe pas** sur ce serveur : chaque table satellite est testée dans `information_schema` avant usage, comme dans l'édition du monde.

## Verrous d'instance (CdC §9)

**Le canal est le SQL, faute de mieux.** `.instance listbinds` et `.instance unbind` sont **`Console::No`** : elles agissent sur le joueur sélectionné ou sur la session du maître de jeu. Seule `.instance stats` du groupe est `Console::Yes` — et elle revient en faute SOAP, voir la section sur le canal d'exécution.

**Une levée en base n'agit qu'au redémarrage du worldserver, et c'est plus strict que la règle de l'or.** `InstanceSaveMgr::LoadCharacterBinds` (appelée par `LoadInstances`, elle-même appelée une fois depuis `World.cpp`) lit **toute** la table `character_instance` au démarrage et garde les liens dans `playerBindStorage` pour la durée de vie du serveur — **pour tous les personnages, connectés ou non**. Écrire hors ligne ne suffit donc pas ici, contrairement à `characters.money`. La suppression est bien persistante ; c'est son effet qui est différé.

- **Ne pas nettoyer `instance`.** Le serveur supprime lui-même les lignes orphelines au démarrage (`DELETE i.* FROM instance AS i LEFT JOIN character_instance AS ci ON i.id = ci.instance WHERE ci.guid IS NULL`), ainsi que les `creature_respawn` et `gameobject_respawn` associés. Les effacer depuis l'application risquerait d'emporter une instance encore référencée.
- **La difficulté est laissée en chiffre.** En 3.3.5a, 0 et 1 valent « normal » et « héroïque » pour un donjon, mais « 10 » et « 25 joueurs » pour un raid, 2 et 3 étant leurs modes héroïques. Rien dans `instance` ni `character_instance` ne dit le type ; `Map.dbc` le porterait, mais l'import DBC du projet ne conserve que le nom. L'inférer depuis les difficultés présentes dans `instance_reset` se tromperait sur les raids sans mode héroïque — le chiffre brut et une légende valent mieux.
- `character_instance` : `guid`, `instance`, `permanent`, `extended`. **`permanent`** distingue le verrou de raid (jusqu'à la réinitialisation hebdomadaire) du verrou de donjon ; **`extended`** signale un report d'une semaine demandé par le joueur.
- `instance` : `id`, `map`, `resettime`, `difficulty`, **`completedEncounters`** — un **masque de bits** des rencontres vaincues, pas un compteur.
- `instance_reset` (mapid, difficulty, resettime) porte les réinitialisations programmées : 73 lignes sur le serveur de référence, et souvent la vraie réponse à « quand ce raid rouvre-t-il ? ».

`instance` et `character_instance` sont vides sur le serveur de référence, `instance_saved_data` n'y existe pas.

## Licence et référence externe (CdC §4)

Le projet est sous **GPL-3.0** (dépôt `warblups/AzerothManager`), licence choisie pour être identique à celle de l'addon **AzerothAdmin** (dérivé de TrinityAdmin/MangAdmin), qui a servi de référence au découpage fonctionnel.

Conséquence pratique : **reprendre son code et ses données est permis**, dans les deux sens — `TeleportTable.lua` (destinations par continent, la reprise la plus utile, pour le module Téléportation), `DBC.lua`, `Models.lua`. Conserver les en-têtes de copyright d'origine et mentionner la provenance dans le fichier concerné.

Le dépôt a été créé en AGPL-3.0 puis basculé en GPL-3.0 avant tout code — ne pas réintroduire l'AGPL.

## Armurerie

Fiche de personnage complète et lisible : équipement avec icônes et infobulles, statistiques calculées, talents, hauts faits, statistiques PvP, guilde. C'est l'extension naturelle de la « Fiche personnage » du §18.

**Aucune API officielle ne couvre un serveur privé 3.3.5a.** La stratégie est donc *hors-ligne d'abord*, l'API externe n'étant qu'un enrichissement :

1. **Source de vérité — les bases du serveur.** `characters.character_inventory` + `world.item_template` donnent l'équipement réel, y compris les objets custom ajoutés par les modules, qu'aucun site externe ne connaît.
2. **Rendu hors-ligne — les DBC extraits par l'Updater.** `<serveur>\data\dbc\` (`Item.dbc`, `ItemDisplayInfo.dbc`) fait le lien `displayid` → nom d'icône. C'est exact, sans conditions d'utilisation, et cela vaut pour les objets custom. Belle synergie : la sortie d'extraction de l'Updater devient l'entrée de l'armurerie. Sur serveur Linux, récupérer et mettre en cache localement via SFTP.
3. **Enrichissement optionnel** — infobulles formatées, icônes CDN, modèle 3D. Mettre en cache dans la base SQLite locale, respecter les limites de débit, et **dégrader proprement** : hors ligne ou API indisponible, la fiche doit rester complète à partir des points 1 et 2.

Points à trancher avant d'implémenter le point 3, à ne pas supposer résolus :

- L'**API Battle.net** (namespaces Classic) est officielle et fournit médias et icônes, mais exige OAuth, et ses identifiants d'objets ne correspondent pas exactement à 3.3.5a — et jamais aux objets custom.
- Les **infobulles et le visualiseur 3D de Wowhead** sont la solution retenue par la plupart des armureries de serveurs privés, mais ce n'est pas une API publique : usage toléré, sensible aux conditions d'utilisation, et les URL exactes doivent être vérifiées avant d'être codées en dur plutôt que reprises de mémoire.

## Périmètre par version

- **v1.0** — socle : configuration multi-serveurs, MySQL, SSH, console SQL, console GM, comptes, personnages, catalogue d'objets, courrier, modération, services `at_login`.
- **v1.1** — support et confort : tickets GM, restauration ciblée, téléportation, métiers et sorts, modification en direct, armurerie, guildes, réputation.
- **v1.2** — édition du monde : PNJ, waypoints, GameObjects, événements, verrous d'instance, annonces, royaumes, playerbots, quêtes.
- **v2.0** — visualiseur 3D, cartes, éditeur de spawns graphique, économie et anti-triche.

**Ordre de construction imposé par les dépendances, à l'intérieur de la v1.0** : configuration des serveurs → accès MySQL → console SQL et `GmCommandService` → catalogue d'objets → les modules qui s'appuient dessus.

### Contexte d'usage — ce qu'il ne faut pas construire

Outil personnel, pour un serveur fréquenté par quelques amis / une guilde. En conséquence :

- **Pas de rôles ni de permissions.** Le mode lecture seule reste, mais comme garde-fou contre la fausse manip en production, pas comme gestion d'accès.
- **Pas de CI, pas d'installeur signé.** Il existe en revanche un projet de tests **volontairement étroit** (`Tests/AzerothManager.Tests`, xUnit, `dotnet test`) : il ne couvre que la **logique pure** — analyse d'une ligne de journal, règles de délai et de motif d'un arrêt, sens des colonnes à double signification, mise en forme des durées et des montants. Rien de ce qui touche MySQL, SSH ou SOAP n'y est simulé : la vérification contre le serveur réel reste la méthode du projet, et un faux serveur ne prouverait rien. Les tests servent à figer ce que la lecture des sources a coûté cher à établir, en particulier quand le serveur ne permet pas de l'observer — les tables de guilde y sont vides. Le projet applicatif étant publié self-contained, le projet de tests doit l'être aussi (`SelfContained`), sinon le SDK refuse la référence (NETSDK1151) ; il accède aux membres `internal` par `InternalsVisibleTo`.
- L'historique sert à retrouver ce qu'on a fait il y a trois jours, pas à arbitrer entre administrateurs.
- Priorité : outils GM et édition du monde d'abord, puis courrier et restauration. Modération légère ; économie et anti-triche en dernier.

Le multi-serveurs et l'exigence de densité de l'interface ne sont **pas** concernés par cet allègement : il y a une prod et un bac à sable, et c'est l'outil du quotidien.

Deux briques sont transverses et se construisent en premier, jamais comme de simples onglets :

- `GmCommandService` — utilisé par la majorité des modules (cf. §5).
- `ItemCatalogService` — sélecteur d'objets réutilisable (`world.item_template`, icônes, filtres qualité/niveau/classe/slot) qui alimente courrier, inventaire, banque, loot, hôtel des ventes et armurerie.

Ne pas anticiper les modules v1.1+ tant que le socle v1.0 n'est pas en place.

## Commandes

Rien n'est encore scaffoldé. Une fois la solution créée :

```powershell
dotnet build
dotnet run --project AzerothCoreManager\AzerothCoreManager.csproj
dotnet test
dotnet test --filter "FullyQualifiedName~NomDuTest"

# Publication (même profil que AzerothUpdater)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ..\publish
```

## Frontière avec AzerothUpdater

`..\AzerothUpdater` (dépôt Git distinct — ne pas y toucher depuis ici) est l'application WPF .NET 10 du même auteur qui couvre **l'infrastructure** du même serveur : git pull du core et des modules, catalogue de modules, chaîne CMake/MSBuild, copie des binaires, extraction DBC/maps/vmaps/mmaps, sauvegardes `mysqldump` + restauration, démarrage/arrêt d'authserver/worldserver/MySQL, tail des logs. Elle est **en cours de portage vers un serveur AzerothCore Linux** en plus de Windows : le partage des responsabilités ne peut donc pas s'appuyer sur l'OS.

La séparation est **fonctionnelle** :

- **Updater = le serveur en tant que logiciel** — compiler, déployer, sauvegarder, démarrer/arrêter, extraire les données client.
- **Manager = le serveur en tant que jeu** — comptes, personnages, objets, monde, joueurs connectés, support.

Ne jamais réimplémenter ici le build, le git, l'extraction ou les `mysqldump` : si un besoin infra apparaît, il va dans l'Updater. Le §14 du cahier des charges (état du serveur via process/systemctl, lecture des `.conf`) recoupe l'Updater — s'en tenir à de la **consultation** et aux opérations de jeu (annonce, arrêt planifié avec compte à rebours, `.reload`), pas à la gestion du cycle de vie des binaires.

L'Updater reste la référence pour les conventions communes : `CredentialProtector` (DPAPI), `LogTailHelper`, l'accès SSH.NET, le thème sombre et la publication self-contained single-file.
