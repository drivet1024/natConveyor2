# Conveyor Control

## Redémarrage RSLinx depuis AUTOMATE

L’option **Redémarrer RSLinx automatiquement si l’automate se déconnecte** est
activée par défaut dans Configuration → AUTOMATE. Tant que les connexions de la
ligne principale sont demandées, une surveillance indépendante vérifie le même
état que le voyant toutes les cinq secondes (transport et qualité des lectures).
Après au moins 45 secondes d’indisponibilité continue, elle envoie un SMS d’alerte
puis tente de redémarrer RSLinx, avec un SMS de résultat. La disparition de la panne
pendant 30 secondes consécutives déclenche un SMS de rétablissement et réarme la
surveillance. Les alertes utilisent les paramètres Twilio et les destinataires existants.

Une seule tentative est effectuée par incident, avec au moins cinq minutes entre
deux tentatives, même si la connexion oscille. Un échec exige une intervention
ou une reconnexion stable avant réarmement. Les déconnexions volontaires de la
ligne principale, la simulation et les redémarrages manuels en cours ne déclenchent
pas cette reprise. Désactiver l’option conserve les alertes de déconnexion et de
rétablissement sans redémarrage automatique. L’automatisme reste soumis aux limites
du redémarrage local (protocole, serveur OPC et droits Windows).

En reprise automatique, la dernière valeur de marche reçue avant la panne peut
être périmée : elle ne bloque pas le redémarrage RSLinx demandé après la perte
de communication. Aucune commande de marche ou d’arrêt du convoyeur n’est envoyée
par cette reprise. La commande manuelle conserve son blocage lorsque la dernière
valeur indique un convoyeur en marche. La temporisation et la limite par incident
sont en mémoire et repartent à zéro au redémarrage de Conveyor.Web.

La tuile **AUTOMATE** est cliquable et conserve son voyant et son état. Elle ouvre
une fenêtre de confirmation **Redémarrer RSLinx**. Le convoyeur doit être arrêté :
si l’automate le signale en marche, la commande est refusée. Si RSLinx ne répond
plus et que l’état est inconnu, vérifier l’arrêt physique avant de confirmer.
Cette commande ne démarre pas le convoyeur et ne remet pas les compteurs à zéro.

Dans **Configuration → AUTOMATE**, régler le mode de redémarrage :

- **Service Windows** (par défaut) : nom exact du service, `RSLinx` par défaut.
  L’application demande son arrêt, attend l’état arrêté, le démarre puis attend
  l’état en cours d’exécution. Aucun service dépendant n’est arrêté de force.
- **Application Windows** : chemin absolu de `RSLINX.exe`. Conveyor.Web doit tourner
  dans la même session interactive que RSLinx. La commande refuse une instance
  située dans une autre session ou un autre chemin, ainsi qu’un service RSLinx
  actif. Elle tente une fermeture normale, puis termine l’instance ciblée si elle
  ne se ferme pas sous dix secondes, et relance le même exécutable. Le démarrage
  utilise les paramètres par défaut de cet exécutable, sans arguments additionnels.

Enregistrer et redémarrer Conveyor.Web applique les réglages. Le compte exécutant
Conveyor.Web doit avoir les droits Windows d’arrêt/démarrage du service ou de
gestion du processus RSLinx ; aucune élévation automatique n’est effectuée.
Voir [les modes RSLinx documentés par Rockwell](https://www.rockwellautomation.com/en-us/docs/rslinx-classic/4-60/rslinx-classic-help-ditamap/troubleshooting/frequently-asked-questions--faq-/miscellaneous/run-rslinx-classic-as-a-service.html)
et [les droits de gestion des services Windows](https://learn.microsoft.com/en-us/troubleshoot/windows-server/windows-security/grant-users-rights-manage-services).

Le redémarrage concerne seulement le serveur local hébergeant Conveyor.Web.
Il est refusé en simulation, avec une passerelle TCP ou avec un serveur OPC
distant. Pendant l’opération, les commandes de marche et de connexion concurrentes
sont refusées. Les échanges automate sont temporairement fermés, puis réouverts
si les connexions de la ligne principale étaient actives. Si la reconnexion échoue,
les contrôles périodiques reprennent les tentatives. Les connexions volontairement
désactivées restent désactivées. Vérifier le voyant et les lectures : un processus
ou service relancé ne garantit pas encore le retour des communications automate.
Le délai du redémarrage Windows est limité à 90 secondes ; les erreurs et refus
d’accès sont consignés dans les journaux. Aucun redémarrage réel n’est effectué
par les tests automatisés.

## Sauvegarde quotidienne des compteurs

Dans **Configuration → Sauvegarde des compteurs**, activer la sauvegarde, choisir
l’heure fixe de début du shift et l’heure de sauvegarde dans MySQL. Les valeurs
proposées sont 20 h 00 et 08 h 25 ; les horaires utilisent l’heure locale du serveur.
**Enregistrer et redémarrer** applique les changements. La sauvegarde est désactivée
par défaut et aucun enregistrement n’est effectué en simulation.

L’application utilise la connexion MySQL déjà configurée et la table existante
`conveyor_stats_dde`, avec la colonne `line_id`. Chaque ligne utilisée possède
son propre enregistrement, même si elle est temporairement déconnectée. Renseigner
un **ID de ligne MySQL (lineId)** positif et distinct pour chacune dans Configuration.
Cet identifiant est transmis tel quel à `line_id`, sans conversion depuis le numéro
visuel de la ligne. Une seule instance doit écrire les statistiques d’un dépôt
et d’une ligne dans cette base. Une ligne déjà présente pour le même `DEPOT_ID`,
`line_id` et `INSERT_DATE` est conservée, sans nouvelle insertion, lors d’un nouvel essai.

Les statistiques de production sont également additionnées dans
`conveyor_stats_dde_global`. Les compteurs de maintenance sont enregistrés
séparément par ligne dans `conveyor_stats_dde_maintenance`, avec le même `line_id`
MySQL configuré que pour la production. Les doublons de maintenance sont vérifiés
par `DEPOT_ID`, `line_id` et `INSERT_DATE`. Si la colonne manque encore, exécuter
une seule fois `scripts/sql/Add-Maintenance-LineId.sql` depuis la racine du dépôt
sur la base locale ; les anciennes lignes restent à NULL, sans attribution inventée.
La table globale n’a ni `line_id` ni `conveyor_id` : elle représente le convoyeur
de cette instance et nécessite une base locale distincte par convoyeur. Ses doublons
sont vérifiés par `DEPOT_ID` et `INSERT_DATE`. Les
pourcentages globaux sont recalculés à partir des sommes des compteurs, sans
moyenne des pourcentages par ligne. Les trois tables doivent exister avant activation.

Dans la fenêtre **DÉMARRER**, le NIP permet de choisir **DÉMARRER** pour la
production ou **MAINTENANCE**. Le démarrage en maintenance et le retour en
production depuis la maintenance exigent un convoyeur arrêté, confirmé par le
tag de marche à 0 avec connexion automate disponible et lectures saines. Un
état inconnu ou en marche bloque le bouton et la commande côté serveur. L’envoi
d’une commande d’arrêt seul ne suffit pas : attendre son retour automate.
Le choix s’applique aux deux lignes après réussite
de l’envoi de la commande automate. **ARRÊTER** conserve le mode ; un démarrage
normal revient en production. Le mode actif est affiché au-dessus de la supervision
et mémorisé dans `conveyor.settings.json` pour le prochain redémarrage. Un échec
de mémorisation est signalé dans le résultat et les journaux.

Chaque ligne possède deux jeux de compteurs indépendants. L’écran montre le mode
actif ; changer de mode retrouve ses compteurs précédents. Un colis déjà en cours
de traitement conserve son jeu de compteurs même si le mode change avant la fin
de son traitement. Le reset manuel remet à zéro uniquement le mode affiché ;
le reset quotidien remet à zéro les deux modes après capture programmée.

À l’heure de sauvegarde, la production effectuée pendant le shift est enregistrée
dans les tables normale et globale, et la maintenance dans sa propre table, même
si le mode actif a changé entre-temps. Un shift exclusivement en maintenance ne
crée aucune statistique de production tant que le mode maintenance reste actif.
La maintenance n’alimente jamais les compteurs sauvegardés en production.
Les commandes de marche et les scans opérationnels conservent leurs écritures
habituelles ; cette séparation concerne les trois tables de statistiques.

| Colonne | Valeur sauvegardée |
| --- | --- |
| `ID` | Auto-incrément de la table |
| `DEPOT_ID` | Identifiant du dépôt configuré |
| `line_id` | ID de ligne MySQL configuré |
| `INSERT_DATE` | Date et heure du début du shift associé à l’heure de sauvegarde |
| `NB_SCANNED` | `TotalParcels` de la ligne |
| `NB_REJECTED` | `Rejected` de la ligne |
| `NB_RECYCLED` | `Code97` de la ligne |
| `NB_SORTED` | `SortedByWaybill + SortedByPostalCode` de la ligne |
| `PC_REJECTED`, `PC_RECYCLED` | Compteur correspondant / `NB_SCANNED` × 100 |
| `PC_CODE98`, `PC_CODE68` | Compteur de la ligne correspondant / `NB_SCANNED` × 100 |
| `PC_FULLCHUTE`, `PC_CODE42` | `NULL` : aucune mesure correspondante disponible |

Les pourcentages sont arrondis à deux décimales et valent zéro sans scans.
`NB_SORTED` représente les tris logiciels par route d’expédition ou code postal,
pas une confirmation du passage physique ; un tri détourné vers 97 peut aussi
être compté parmi ces tris. Les rejets suivent le compteur affiché, qui exclut
les no-reads. Les codes 98 sont comptés après insertion du scan et les codes 68
suivent la dernière valeur de transfert, comme à l’écran. Aucun total automate
historique n’est inventé pour remplacer ces compteurs logiciels.

Exemple : début à 20 h 00 et sauvegarde le 24 septembre à 08 h 25 donnent
`INSERT_DATE = 2026-09-23 20:00:00`. Le choix du shift de tri dans la supervision
ne modifie pas cet horaire fixe. Il s’agit d’une photo des compteurs au moment
de la sauvegarde, depuis leur dernière remise à zéro ; une remise à zéro manuelle
en cours de shift réduit donc les valeurs qui seront sauvegardées.

Le contrôle de l’échéance s’effectue toutes les cinq secondes. La sauvegarde
doit précéder ou coïncider avec la remise à zéro quotidienne de chaque ligne,
également réglable dans cette section. Quand les heures coïncident, la capture
est écrite sur disque avant le reset, puis envoyée en base. L’écriture SQL ne
bloque pas le traitement des colis. Une erreur de capture sur disque diffère
le reset quotidien et apparaît dans les journaux.

Le fichier `data/counter-statistics.json`, dans le dossier de l’application,
conserve les captures en attente et la dernière échéance capturée. Il est exclu
de Git et des publications. Le compte du service doit pouvoir écrire dans ce
dossier ; le conserver lors des mises à jour. En cas d’échec MySQL, la capture
est réessayée toutes les minutes, y compris après redémarrage, sans relire les
compteurs déjà remis à zéro. Les réussites et erreurs sont visibles dans les logs.

Les compteurs en cours restent en mémoire : cette fonction ne les restaure pas
après un arrêt avant la capture. Si l’application démarre après une échéance
manquée, elle n’invente pas de statistiques pour ce shift ; seules les captures
déjà présentes sur disque sont reprises. L’application doit rester active
pendant le shift et à l’heure prévue pour une sauvegarde complète.

## Alertes SMS Twilio

Dans **Configuration → SMS Twilio**, activer les alertes et saisir AccountSid,
AuthToken, le numéro Twilio émetteur et les numéros destinataires au format
international (`+15145551234`). Séparer les destinataires par point-virgule,
virgule ou saut de ligne ; les doublons sont supprimés. Utiliser **Enregistrer
et redémarrer** pour appliquer. Un champ AuthToken vide conserve le token actuel,
qui n’est jamais réaffiché. Ces paramètres, dont le token en clair, sont enregistrés
dans le fichier local `conveyor.settings.json`, exclu de Git : limiter son accès
au compte du service et aux administrateurs.

Les alertes couvrent le Reset Data terminé ou interrompu (suppression possiblement
partielle), les resets de compteurs manuels ou quotidiens, les commandes de
démarrage/arrêt envoyées à l’automate (avec cause d’arrêt), et l’activation ou
l’arrêt des connexions appareils par ligne, y compris au démarrage/arrêt de
l’application. Une activation des connexions ne confirme pas la connexion
physique de chaque appareil : les voyants restent la référence. Les coupures
TCP spontanées ne déclenchent pas de SMS. Les messages identifient
le dépôt par son nom, la ligne ou toutes les lignes, et l’heure locale.
Le champ **Configuration → Général → Nom du dépôt** personnalise le nom envoyé.
Sans valeur, le dépôt 1 utilise Saint-Hubert, 28 Gilmore, 2 Québec et 12 Toronto ;
les autres affichent « Nom non configuré » jusqu’à la saisie du nom. Enregistrer
et redémarrer pour appliquer.

Les SMS sont désactivés par défaut et aucun appel Twilio n’est effectué en mode
simulation. L’envoi utilise une file en mémoire de 200 alertes, avec un délai
maximal de 10 secondes par destinataire. Les échecs sont consignés dans les
journaux sans bloquer les commandes ni les autres destinataires. Aucun nouvel
essai automatique n’est effectué pour éviter les doublons après un délai dépassé.
Une file pleine rejette la nouvelle alerte avec un avertissement ; un arrêt brutal
peut perdre les alertes en attente. « Accepté par Twilio » ne confirme pas la
livraison au téléphone ; vérifier la console Twilio pour le suivi.

L’émetteur doit appartenir au compte Twilio et permettre les SMS. Avec un compte
d’essai, les destinataires doivent être vérifiés chez Twilio. Référence :
[API Messages Twilio](https://www.twilio.com/docs/messaging/api/message-resource).

Remplacement ASP.NET Core/Blazor de l'application WinForms `Nat_Conveyor`. Une instance supervise jusqu'à deux lignes indépendantes.

## Démarrage sécurisé

```powershell
dotnet run --project Conveyor.Web
```

La configuration livrée utilise `Conveyor:Simulation=true`; aucun équipement réel n'est contacté. Le bouton **Colis test** valide le parcours complet.

Pour la production, définir les secrets hors du fichier JSON, par exemple :

```powershell
$env:Conveyor__Simulation='false'
$env:Conveyor__Database__ConnectionString='Server=...;Database=nationex;User ID=...;Password=...;SslMode=Required'
dotnet run --project Conveyor.Web --urls http://0.0.0.0:5080
```

Ne recopiez pas les mots de passe de l'ancien `App.config` : ils doivent d'abord être remplacés.

## Flux d'une ligne

Dans **Configuration → Général**, **Nombre de lignes utilisées** permet de choisir
une ou deux lignes (`Conveyor:LineCount`). Utiliser **Enregistrer et redémarrer**
pour appliquer le choix. Avec une ligne, seule la première est affichée et ses
connexions peuvent démarrer ; les paramètres de la seconde restent enregistrés.
Le choix **Activée au démarrage** reste indépendant pour chaque ligne utilisée.
Les anciennes configurations gardent leur nombre de lignes jusqu'à modification.

Le cadre **Réception en direct**, au-dessus de la dernière décision, affiche la
dernière trame complète de chaque appareil, l'heure de réception à la milliseconde
et un numéro de réception. La notification part dès le découpage TCP, avant la
file de traitement et les accès à la base. Les trames invalides ou vides restent
visibles, avec leurs caractères de contrôle. L'affichage conserve au maximum
4 096 caractères par appareil ; le traitement reçoit toujours la trame entière.
Il affiche la dernière valeur, pas un historique exhaustif de toutes les trames.
La latence visible dépend de la connexion du navigateur ; aucun délai de
rafraîchissement périodique n'est ajouté. Le cadre reste vide au démarrage tant
qu'aucune donnée n'est reçue. La dernière décision affiche « Aucun colis traité »
jusqu'au premier traitement, sans données de démonstration.

Les réglages **Tri et validation** de la page Configuration sont globaux aux deux
lignes et enregistrés sous `Conveyor:Sorting`. Ils sont appliqués aux deux lignes
au démarrage. Pour une ancienne configuration sans cette section, les valeurs
de la ligne ayant le plus petit identifiant (ligne principale) sont reprises.
Les anciens champs par ligne restent compatibles mais sont remplacés par les
valeurs globales à l'enregistrement et au démarrage.

- Caméra : serveur TCP, messages terminés par `CR`.
- Dimensionneur : serveur TCP ou client TCP, trames de 16 caractères encadrées par STX/ETX.
- Balance : serveur TCP; protocoles `Delimited`, `Fixed16From0` et `Fixed16From1`.
- Base : tables historiques `conveyor_shipment`, `conveyor_shift_route`, `location`, `scan_history`, `scan_noWB`, `code86` et `code98`.
- Automate : passerelle TCP compatible avec le protocole existant `p;tag;valeur`.

Chaque ligne possède ses propres ports, état, compteurs, annulation et tampon de mesures. Les ports doivent être uniques dans une même instance.

Les voyants sont verts uniquement lorsqu'une connexion physique est réellement ouverte, gris lorsque l'appareil est déconnecté et orange en simulation. La page `/logs` affiche jusqu'aux 2 000 événements les plus récents en mémoire, avec filtres par niveau et recherche. Les journaux sont également disponibles avec `GET /api/logs`.

La page `/configuration` permet de modifier la base MySQL, les ports et protocoles des appareils, la passerelle automate et les règles de tri des deux lignes. Elle écrit les surcharges dans `Conveyor.Web/conveyor.settings.json`; ce fichier est exclu de Git car il peut contenir une chaîne de connexion. Le bouton **Enregistrer et redémarrer** relance automatiquement le processus sur la même adresse.

Lorsque `Conveyor:Database:ConnectionString` contient une chaîne MySQL, la base réelle est utilisée même si `Conveyor:Simulation` est activé pour les appareils. Sans chaîne de connexion, un dépôt simulé est utilisé et son voyant reste orange.

## API d'exploitation

Le bouton **98** de la supervision active (vert) ou désactive (rouge) la validation
du poids et des dimensions pour la ligne concernée, immédiatement et indépendamment
de l’autre ligne. Le bouton reste cliquable lorsqu’il est rouge. Un poids absent
ou nul, ou une dimension absente ou nulle, entraîne la chute 98 lorsque le bouton
est actif, y compris pour un tri par code postal ou une absence de lecture.
La protection contre plusieurs expéditions (code 99) reste prioritaire.
Lorsque le bouton est désactivé, le tri conserve sa décision sans déviation liée
aux mesures. Les contrôles existants des valeurs maximales restent actifs avec
le bouton vert. Le compteur **Code 98** compte les traitements enregistrés avec
la chute 98 ; son pourcentage utilise le total des colis de la ligne.
Il se remet à zéro avec les autres compteurs.

Chaque bouton modifie l'état courant de sa ligne. Au redémarrage de l'application,
les deux lignes reprennent la valeur globale **Activer le code 98 (poids et dimensions)**
enregistrée dans Configuration.

- `GET /api/lines`
- `POST /api/lines/{id}/start`
- `POST /api/lines/{id}/restart`
- `POST /api/lines/{id}/stop`
- `POST /api/lines/{id}/reset`

## Passage en production

1. Faire tourner le mode simulation avec une copie anonymisée des routes.
2. Vérifier le format exact des trames de chaque modèle de balance et dimensionneur.
3. Tester la passerelle automate avec le convoyeur physiquement isolé.
4. Configurer les deux lignes et les tags automate propres au site.
5. Installer le service derrière HTTPS et une authentification réseau; les commandes de contrôle ne doivent pas être exposées publiquement.

### Reset des convertisseurs

L’engrenage à côté de 98 ouvre les commandes **Reset Balance** et **Reset Dimensionneur** de la ligne. Dans Configuration, chaque bloc appareil dispose d’un modèle **Vieux (Old)** / **Nouveau (New)** et d’une adresse IP de convertisseur distincte de la connexion TCP des mesures. Enregistrer et redémarrer pour appliquer ces paramètres. Les commandes reprennent les points d’accès HTTP et les identifiants par défaut du `SerialConverter` historique. Une commande acceptée ne confirme pas encore le retour des mesures : surveiller le voyant et les trames reçues. Les commandes matérielles sont désactivées en simulation.

### Choix DDE ou OPC DA

Dans **Configuration → AUTOMATE → Protocole automate**, choisir **DDE / RSLinx**, **OPC DA / RSLinx Classic** ou la passerelle TCP existante. Enregistrer et redémarrer applique le choix à la connexion commune des deux lignes. Les paramètres DDE et les tags de chaque ligne sont conservés lors du changement de protocole. Le mode simulation ne crée aucune connexion OPC.

En OPC DA, renseigner le **ProgID** exact du serveur enregistré (valeur proposée : `RSLinx OPC Server`), l’ordinateur hébergeant RSLinx (vide pour le serveur web local), le sujet RSLinx et la période d’actualisation demandée (100 ms par défaut, ajustable de 50 à 60000 ms ; le serveur peut réviser cette période). Le sujet `NATIONEX` et le tag `COLISDDE_M30` donnent l’Item ID `[NATIONEX]COLISDDE_M30`. Les tags déjà qualifiés, comme `[AUTRE_SUJET]TRANSFER`, sont transmis tels quels. Un sujet vide transmet les tags sans préfixe. Le tag commun de démarrage du convoyeur, configurable globalement et initialisé à `DEPART_SYSTEMES`, utilise aussi ce sujet, comme les tags chute, transfert et faute balance.

La connexion utilise les abonnements OPC DA 2.05 et une lecture de contrôle au maximum toutes les cinq secondes, avec une échéance de trois secondes pour les transactions asynchrones. Les valeurs de mauvaise qualité ne remplacent pas la dernière valeur valide et rendent le voyant automate indisponible. Les booléens sont normalisés en `1`/`0` ; les écritures utilisent le type déclaré par le serveur. Une coupure ou un échec de lecture déclenche une tentative de reconnexion au contrôle suivant. Une déconnexion volontaire désactive la reconnexion. Les commandes précédentes ne sont jamais rejouées.

Le client utilise le compte Windows exécutant l’application, via COM/DCOM, avec authentification au niveau intégrité des paquets. Le serveur doit disposer des composants OPC Classic et des autorisations d’accès/activation et de retour des notifications pour ce compte. Le déploiement est en **Windows x64** : choisir un serveur OPC accessible aux clients 64 bits. Selon l’édition/version de RSLinx, le ProgID approprié peut différer ; Rockwell documente notamment une connexion « RSLinx Local OPC Server » pour les clients 64 bits des éditions Single Node/OEM à partir de 4.12. Ne pas modifier le nom au hasard : reprendre celui vérifié dans le client de test OPC du poste.

Références : [interfaces OPC DA de RSLinx](https://www.rockwellautomation.com/en-us/docs/rslinx-classic/4-60/rslinx-classic-help-ditamap/how-to---/collect-data/use-opc/opc-custom-interfaces.html), [compatibilité RSLinx 4.12 et clients 64 bits](https://compatibility.rockwellautomation.com/GeneratedReleaseNote.aspx?v1=59238), [authentification COM](https://learn.microsoft.com/en-us/windows/win32/com/authentication-level). Les tests automatisés couvrent les réglages, lectures, écritures, qualité et reconnexions avec un faux serveur ; la validation des communications physiques reste à effectuer avec RSLinx.

### Fermeture de la chute 39 et recirculation 97

Le champ **Configuration → AUTOMATE → Tag fermeture chute 39** vaut `CLOSE_CHUTE_39` par défaut et s’applique aux deux lignes. Après enregistrement et redémarrage, il est surveillé en DDE ou OPC DA. Une valeur `1` remplace toute destination automate 39 par 97 au moment du traitement du colis ; `0` rétablit le tri normal. Les autres destinations restent inchangées. En OPC, le sujet configuré est ajouté au tag comme aux autres Item IDs. Le mode passerelle TCP ne fournit pas cette lecture.

La décision enregistrée dans le scan et affichée comme dernier tri indique 97 avec le motif « chute 39 fermée ». Chaque ligne affiche **Code 97 · Recirculation**, son total et son pourcentage, avec le même flash vert que les autres compteurs. Le compteur compte une fois chaque colis dont l’envoi vers 97 revient sans erreur, même si l’enregistrement MySQL échoue ensuite ; il ne constitue pas une confirmation du passage physique dans la chute. Il suit la remise à zéro et la durée de vie des autres compteurs.

La dernière valeur valide du tag est conservée en cas de valeur invalide ou d’interruption des lectures ; avant la première valeur reçue, le détournement est inactif. Une remise à zéro des compteurs ne change pas l’état de fermeture reçu.

### Diagnostic et reprise DDE

Pendant que les connexions des appareils sont démarrées, les contrôles périodiques effectuent une lecture DDE directe d’un tag à tour de rôle, au maximum une fois toutes les cinq secondes pour les deux lignes ensemble. Un cycle complet prend donc au moins cinq secondes par tag. Le contrôle passe son tour si une écriture est déjà en cours. Les lectures de contrôle et les opérations d’abonnement ont un délai DDE de 1 seconde par opération.

Si une lecture directe trouve une valeur différente de la dernière notification, sans nouvelle notification reçue pendant la lecture, elle actualise la valeur et relance l’abonnement. Une valeur constante ne provoque pas de reconnexion. Les abonnements ayant échoué sont réessayés lorsque la lecture directe du tag réussit. Une conversation coupée, un échec de reprise d’abonnement ou une série d’échecs de lecture sur au moins un tour complet (minimum trois échecs consécutifs) provoquent une nouvelle connexion au contrôle suivant. Un tag invalide isolé ne provoque pas de reconnexions répétées si les autres répondent. Une déconnexion volontaire désactive la reprise automatique. Aucune commande d’écriture passée n’est rejouée lors d’une reconnexion.

Le voyant AUTOMATE tient compte des erreurs de lecture et d’abonnement ; la connexion de transport reste distinguée pour permettre les commandes lorsque les écritures fonctionnent encore. L’heure de réception est actualisée même si la valeur reçue est identique, y compris après une lecture de contrôle. Le filtrage historique du code 68 dans la case AUTOMATE est conservé ; la case TRANSFERT continue de l’afficher.

Dans les journaux, « Lecture directe DDE reçue … sans notification correspondante » indique un écart entre lecture directe et abonnement ; « Lecture directe DDE échouée » indique que la requête elle-même n’aboutit pas. Les tentatives de reconnexion et reprises d’abonnement sont également consignées. Ces diagnostics permettent de distinguer les symptômes, mais ne prouvent pas à eux seuls une panne de RSLinx. Le contrôle périodique ne garantit pas la récupération des changements transitoires survenus entre deux lectures.

### Commandes de marche du convoyeur

Configurer **Conveyor ID** dans les paramètres globaux, puis enregistrer et redémarrer. Les boutons **DÉMARRER / ARRÊTER** à côté d’AUTOMATE commandent ensemble les deux lignes via `START=1` / `START=0`, comme DDEFrm. Démarrer demande confirmation ; arrêter impose PAUSE (0), JAM (1) ou DOWN (2). Après l’envoi, une ligne est insérée dans `conveyor_action` avec la date locale, CONVEYOR_ID, ACTION (1/0) et CAUSE (NULL au démarrage). Un échec d’insertion après envoi est signalé sans renvoyer la commande. La simulation n’insère aucune action réelle. CONNECTER/DÉCONNECTER dans l’engrenage restent les commandes de connexion aux appareils.
