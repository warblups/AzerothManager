# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## État actuel du projet

Solution scaffoldée et fonctionnelle : la coquille (navigation, thème sombre, barre d'état, bandeau Production) et la **verticale Configuration des serveurs** sont en place — CRUD des profils, chiffrement DPAPI, tests MySQL et SSH, sélection du serveur actif. Les autres modules apparaissent dans le rail de navigation, désactivés, avec leur version cible.

La référence fonctionnelle est `Cahier_des_Charges_AzerothCore_Admin_Manager_V1.2.docx` (24 sections). Les V1 et V1.1 sont conservées comme historique et sont périmées : ne pas s'y fier. Le cahier des charges est la source de vérité et il est rédigé en français — la documentation, les commentaires et les libellés d'interface le sont aussi.

**Prochaine étape** : console SQL (AvalonEdit, pas encore référencé) et `GmCommandService`, puis le catalogue d'objets.

Conventions déjà établies dans le code, à suivre :

- Composition manuelle dans `App.OnStartup` — pas de conteneur d'injection, l'échelle ne le justifie pas.
- `ServerContext` porte le profil actif ; les services le lisent **à chaque opération** plutôt que de mettre en cache une chaîne de connexion, pour que le changement de profil les reconfigure tous.
- Le mode lecture seule est appliqué dans `MySqlService.ExecuteAsync`, pas dans l'UI.
- `LocalDatabase.LogHistory` journalise SQL, commandes GM et actions dans la même table.
- `ServerProfile` est un `ObservableObject` : le modèle porte la notification, assumé pour éviter une couche de DTO inutile.

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

La référence visuelle est un outil de travail (Visual Studio, DBeaver, HeidiSQL), **pas** une interface de serveur privé. Thème sombre cohérent avec l'Updater, mais orienté densité et clavier.

- **Densité avant décoration.** Grilles compactes, colonnes triables et redimensionnables, largeurs mémorisées. Pas d'emoji en guise d'icônes (l'Updater en utilise dans ses onglets — ne pas reprendre cette convention ici), pas d'animation décorative, pas de police fantaisie.
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

Le `GmCommandService` est donc transverse et utilisé par la majorité des modules — ce n'est **pas** la vue de l'onglet console. Canal de transport à trancher à l'implémentation : SOAP du worldserver (préférable, il retourne le résultat) ou console distante via SSH.

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
