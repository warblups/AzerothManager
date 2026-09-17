# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## État actuel du projet

Solution scaffoldée et fonctionnelle. Modules en place : **Configuration des serveurs**, **Console SQL**, **Console GM (SOAP)**, **Catalogue d'objets** (icônes et noms de sorts issus des DBC), **Comptes** et **Courrier en jeu**. Les autres apparaissent dans le rail, désactivés, avec leur version cible.

La référence fonctionnelle est `Cahier_des_Charges_AzerothCore_Admin_Manager_V1.2.docx` (24 sections). Les V1 et V1.1 sont conservées comme historique et sont périmées : ne pas s'y fier. Le cahier des charges est la source de vérité et il est rédigé en français — la documentation, les commentaires et les libellés d'interface le sont aussi.

**Prochaine étape** : Armurerie (la brique DBC est déjà là).

Conventions déjà établies dans le code, à suivre :

- Composition manuelle dans `App.OnStartup` — pas de conteneur d'injection, l'échelle ne le justifie pas.
- `ServerContext` porte le profil actif ; les services le lisent **à chaque opération** plutôt que de mettre en cache une chaîne de connexion, pour que le changement de profil les reconfigure tous.
- Le mode lecture seule est appliqué dans `MySqlService.ExecuteAsync`, pas dans l'UI.
- `LocalDatabase.LogHistory` journalise SQL, commandes GM et actions dans la même table.
- `ServerProfile` est un `ObservableObject` : le modèle porte la notification, assumé pour éviter une couche de DTO inutile.
- **Les écritures SQL s'exécutent dans une transaction, puis demandent validation** en annonçant le nombre de lignes réellement touchées ; un refus provoque un ROLLBACK. C'est plus sûr qu'une confirmation à l'aveugle *avant* exécution, et c'est ce qu'un client SQL générique ne sait pas faire — ne pas régresser vers une simple `MessageBox` préalable.
- Après une écriture validée dans `world`, `SqlEditorService.SuggestReload` propose le `.reload` correspondant, exécuté via le même `GmCommandService`. La liste des tables rechargeables est volontairement courte : n'y ajouter qu'un nom vérifié.
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
│                  ItemCatalogService, ArmoryService, SqlEditorService
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
- **Icônes emoji, comme AzerothUpdater** — convention commune aux deux applications : emoji, deux espaces, libellé (`🖥  Serveurs`). Décision de l'auteur, pour que les deux outils forment un ensemble. Dans le rail, l'emoji vit dans `NavigationItem.Icon`, pas dans `Title`.
- **Clavier d'abord.** Chaque action fréquente a un raccourci : `F5` rafraîchir, `Ctrl+Entrée` exécuter la requête, `Ctrl+F` rechercher, `Ctrl+T` nouvel onglet SQL. Navigation complète sans souris.
- **Contexte permanent en barre d'état** : serveur actif, base, latence, mode lecture seule. Un profil marqué *Production* doit être **visuellement distinct en continu** (bandeau ou accent coloré) : lancer une opération destructive sur le mauvais serveur est le risque principal de cet outil.
- **Jamais de gel de l'interface.** Tout accès MySQL, SSH ou SFTP est `async`/`await`, annulable, avec progression visible. Ces opérations passent par le réseau vers un serveur potentiellement distant.
- **Volumes réels.** `item_template` dépasse 50 000 lignes, `creature` plusieurs centaines de milliers. Virtualisation des grilles obligatoire, recherche et pagination **côté SQL** (`WHERE`/`LIMIT`) — ne jamais charger une table entière en mémoire pour filtrer ensuite.
- **Erreurs dans l'interface, pas en boîte de dialogue.** Les `MessageBox` sont réservées aux confirmations destructives (cf. §21). Le reste s'affiche en ligne ou dans le journal.
- **Feedback explicite.** Toute action d'écriture indique ce qui a été modifié et combien de lignes sont touchées, avant et après.

## Bibliothèques imposées

CommunityToolkit.Mvvm · MySqlConnector · SSH.NET · Microsoft.Data.Sqlite · Serilog · AvalonEdit (éditeur SQL : coloration, auto-complétion, favoris, historique, export CSV, transactions).

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

## Données du client 3.3.5a — icônes et noms de sorts

Vérifié sur les fichiers réels, pas de mémoire :

- **DBC** = format WDBC, en-tête de 20 octets (magie, nb d'enregistrements, nb de champs, taille d'enregistrement, taille du bloc de chaînes), tous les champs sur 4 octets, les chaînes étant des décalages dans le bloc final.
- **`ItemDisplayInfo.dbc` champ 5** = nom d'icône. `item_template.displayid` pointe directement dessus : **`Item.dbc` est inutile**.
- **Noms de sorts** : seize créneaux de langue à partir du **champ 136** de `Spell.dbc`. Un client localisé ne remplit **que le sien** — sur un client français, le créneau anglais est vide pour les 49 839 sorts. Ne jamais coder un créneau en dur : `DbcReader.GetFirstNonEmptyString` balaie la plage.
- **Les icônes sont des TGA**, pas des BLP : 128×128, non compressés, 32 bits BGRA, origine en bas à gauche. WPF ne lit pas le TGA ; `GameClientService.LoadTga` couvre ce seul cas.
- **`spell_dbc` en base ne sert à rien** pour les noms : 4 518 sorts personnalisés seulement, aucun nom localisé.

Le client n'est requis qu'à l'import : tout part dans SQLite (`DbcIcon`, `DbcSpell`, `IconImage`), et l'application est ensuite autonome. Chemin du client dans `Settings` sous `client.path`.

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
- **Pas d'infra de tests lourde, pas de CI, pas d'installeur signé.**
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
