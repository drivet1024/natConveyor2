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

## Sauvegarde en continu des compteurs

Dans **Configuration → Sauvegarde des compteurs**, activer la sauvegarde et
choisir l’heure fixe de début du shift (20 h 00 par défaut, heure locale du serveur).
Enregistrer et redémarrer pour appliquer. Aucun enregistrement en simulation.
Les anciennes options `SaveTime` et `EndOfDay` ne pilotent plus la sauvegarde
ni les resets lorsque cette fonction est activée.

Toutes les cinq secondes, les compteurs sont capturés sur disque, puis mis à jour
sur la même ligne MySQL : dépôt, `line_id`, date de début du shift et mode.
`INSERT_DATE` reste la date de début du shift, même après minuit.
La production alimente `conveyor_stats_dde` par ligne et `conveyor_stats_dde_global`
pour le total du convoyeur. La maintenance alimente `conveyor_stats_dde_maintenance`
par ligne. Le total de production exclut toujours la maintenance ; les pourcentages
globaux sont calculés sur les sommes, pas par moyenne des pourcentages.
La table globale représente une seule instance de convoyeur par base locale.
Les trois tables doivent exister ; la maintenance doit avoir `line_id`
(voir `scripts/sql/Add-Maintenance-LineId.sql`).

Une table complémentaire `conveyor_counter_state` est créée automatiquement
(droit CREATE nécessaire au premier lancement). Elle conserve tous les compteurs
exacts en JSON, indexés par dépôt, ligne, début de shift et mode. L’écriture du
détail et de la ligne statistique se fait dans une transaction InnoDB.
Au redémarrage, les deux modes du shift courant sont lus avant de connecter les
appareils. Une capture locale non synchronisée prend priorité sur la base.
Si la restauration échoue, le démarrage des appareils attend et réessaie toutes
les cinq secondes ; les erreurs sont journalisées. Un shift absent démarre à zéro.
Pour les anciennes lignes sans détail JSON, seuls les champs existants sont
restaurables : les codes 98/68 sont estimés depuis les pourcentages arrondis, le
tri total est affecté au compteur par expédition et les autres compteurs restent
à zéro. Les nouvelles captures permettent une restauration exacte.

Au début du shift suivant, les deux modes sont remis à zéro, même si les appareils
sont déconnectés. La dernière capture de l’ancien shift est conservée sur disque
avant le reset. Le reset manuel reste disponible et concerne le mode affiché.
Le choix du shift de tri ne modifie pas l’heure fixe du shift statistique.

Le fichier `data/counter-statistics.json` conserve les dernières captures en
attente par shift, ligne et mode. Le conserver lors des mises à jour ; le compte
du service doit pouvoir écrire dans ce dossier. Les échecs MySQL sont réessayés
toutes les cinq secondes sans bloquer le traitement des colis. Une coupure brutale
peut perdre les événements survenus depuis la dernière capture locale (environ
cinq secondes en fonctionnement normal). Une erreur disque diffère le reset.

| Colonne | Valeur sauvegardée |
| --- | --- |
| `DEPOT_ID`, `line_id` | Identifiants configurés |
| `INSERT_DATE` | Début du shift |
| `NB_SCANNED` | Total des colis |
| `NB_REJECTED` | Rejets |
| `NB_RECYCLED` | Code 97 |
| `NB_SORTED` | Tris par expédition + tris par code postal |
| `NB_WEIGHT_ERROR` | Erreurs de poids (`ScaleErrors` à l’écran) |
| `PC_WEIGHT_ERROR` | Erreurs de poids / colis lus (`TotalParcels - NoReads`) × 100 |
| `NB_SCALE_ERROR` | Fautes balance (`ScaleFaults`, nombre d’impulsions) |
| `PC_SCALE_ERROR` | Fautes balance / total des colis × 100 |
| `PC_REJECTED`, `PC_RECYCLED`, `PC_CODE98`, `PC_CODE68` | Compteur / total × 100, arrondi à deux décimales |
| `PC_FULLCHUTE`, `PC_CODE42` | NULL à la création, aucune mesure disponible |

Les quatre colonnes WEIGHT_ERROR/SCALE_ERROR doivent exister dans les trois tables.
Les pourcentages valent zéro lorsque le dénominateur est nul et sont arrondis à
deux décimales. Les erreurs globales sont additionnées par ligne avant de calculer
les pourcentages. Sans détail JSON, les deux nombres d’erreurs sont également relus
depuis leurs colonnes (NULL devient zéro).

Dans **DÉMARRER**, choisir production ou maintenance après confirmation que le
convoyeur est arrêté. Chaque mode conserve ses compteurs ; le mode apparaît dans
l’en-tête de chaque ligne et reste mémorisé au redémarrage. Les scans opérationnels
et les commandes automate gardent leur fonctionnement existant.

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
