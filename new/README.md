# Conveyor Control

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
TCP spontanées ne déclenchent pas de SMS. Les messages identifient le site,
le dépôt par son nom, le convoyeur, la ligne ou toutes les lignes, et l’heure locale.
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
