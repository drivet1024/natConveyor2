# Conveyor Control

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
du poids et des dimensions pour les deux lignes, immédiatement. Un poids absent
ou nul, ou une dimension absente ou nulle, entraîne la chute 98 lorsque le bouton
est actif, y compris pour un tri par code postal ou une absence de lecture.
La protection contre plusieurs expéditions (code 99) reste prioritaire.
Lorsque le bouton est désactivé, le tri conserve sa décision sans déviation liée
aux mesures. Les contrôles existants des valeurs maximales restent actifs avec
le bouton vert. Le compteur **Code 98** compte les traitements enregistrés avec
la chute 98 ; son pourcentage utilise le total des colis de la ligne.
Il se remet à zéro avec les autres compteurs.

Le bouton modifie l'état courant. Pour conserver cet état après redémarrage,
enregistrer **Activer le code 98 (poids et dimensions)** dans Configuration.

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
