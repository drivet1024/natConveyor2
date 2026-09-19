# Déployer Conveyor sans dépôt sur le serveur

GitHub compile et teste l'application sur une machine Windows hébergée. Le runner
installé sur ton serveur télécharge uniquement le paquet compilé et les scripts
de déploiement, puis installe Conveyor dans `C:\natconveyor2-dev`.
Le serveur n'a besoin ni du dépôt, ni de Git, ni du SDK .NET. Le runtime .NET est
inclus dans la publication Windows x64.

## Première installation

### 1. Depuis ton PC de développement

Faire un commit et un push sur `main` du workflow `.github/workflows/conveyor.yml`,
du dossier `scripts` et des changements de l'application.
Ce premier push lance une compilation. Si le serveur n'est pas encore préparé,
le job de déploiement peut échouer : le paquet reste téléchargeable après la
réussite du job de compilation.

Pour produire uniquement le paquet, aller dans **Actions → Conveyor Windows →
Run workflow**, sélectionner `main` et décocher **Deployer sur le serveur**.

### 2. Sur le serveur

Installer PowerShell 7 (`pwsh`) et garder le runner GitHub Actions à jour,
avec les labels `self-hosted`, `Windows`, `X64`.

Ouvrir la session Windows qui utilise RSLinx. Depuis la page de l'exécution
GitHub Actions, télécharger l'artefact **conveyor-windows-x64** et extraire le ZIP,
par exemple dans `C:\Conveyor-install`. On y trouve deux dossiers :

- `app` : application compilée et runtime ;
- `scripts` : installation de la tâche et déploiement.

Dans PowerShell 7, depuis cette session RSLinx :

```powershell
& 'C:\Conveyor-install\scripts\Register-ConveyorTask.ps1'
```

Cette commande crée le dossier `C:\natconveyor2-dev` et la tâche **Conveyor.Web**
avec l'utilisateur Windows courant. Si elle existe déjà, vérifier sa configuration
dans le Planificateur de tâches au lieu de la recréer.

### 3. Copier la configuration une seule fois

Transférer depuis ton PC de développement vers `C:\natconveyor2-dev` :

- `new/Conveyor.Web/appsettings.json` ;
- `new/Conveyor.Web/conveyor.settings.json`, s'il existe et contient tes paramètres
  enregistrés depuis l'interface ;
- `appsettings.Production.json`, si ton installation l'utilise.

Vérifier les adresses, les ports, les lignes activées, les paramètres DDE et MySQL.
Ces fichiers sont exclus du paquet GitHub et conservés à chaque mise à jour.
L'application utilise **Production** : `appsettings.Development.json` n'est pas chargé.

### 4. Lancer le premier déploiement

Dans GitHub, aller dans **Actions → Conveyor Windows → Run workflow**, choisir
`main` et laisser **Deployer sur le serveur** coché.
Le runner copie les fichiers compilés, démarre la tâche et vérifie la réponse HTTP.
Ouvrir ensuite **http://localhost:5164** sur le serveur.

Si une ancienne instance utilise déjà ce port ou les ports des appareils, l'arrêter
avant ce premier déploiement. Le script n'arrête que l'exécutable du dossier cible.

## Mises à jour suivantes

Un push sur `main` suffit : compilation et tests sur GitHub, téléchargement puis
redémarrage sur le serveur. Plus besoin de copier les scripts ou les binaires à la main.
La compilation est unique, puis deux jobs déploient le même paquet :
**Installer sur CONV-QC** et **Installer sur STH-CONV-H-11**.
Un échec des tests empêche les deux déploiements. Un échec ou un runner hors ligne
sur un ordinateur n'annule pas le déploiement sur l'autre. Les déploiements sont
sérialisés séparément pour chaque ordinateur.

Au démarrage et après chaque redémarrage, la ligne principale activée au démarrage ouvre aussi la connexion automate DDE (ou TCP selon la configuration), sans clic sur START.

Pour DDE, garder ouverte la session Windows de RSLinx. Elle peut être verrouillée.
La tâche utilise cette session, même si le runner fonctionne comme service.
Le compte du runner doit pouvoir écrire dans le dossier cible, arrêter Conveyor
et gérer la tâche planifiée. La connexion DDE doit être vérifiée sur le serveur.

## Paramètres facultatifs GitHub

Dans **Settings → Secrets and variables → Actions → Variables**, on peut remplacer :

| Variable | Valeur par défaut |
| --- | --- |
| `CONVEYOR_DEPLOY_PATH` | `C:\natconveyor2-dev` |
| `CONVEYOR_TASK_NAME` | `Conveyor.Web` |
| `CONVEYOR_HEALTH_URL` | `http://localhost:5164/api/lines` |

Pour changer l'adresse d'écoute, modifier aussi les arguments de la tâche Windows.
Chaque runner possède un label distinct, déjà attribué dans GitHub :

| Ordinateur | Label de déploiement |
| --- | --- |
| `CONV-QC` | `conveyor-qc` |
| `STH-CONV-H-11` | `conveyor-sth` |

Ne pas attribuer ces deux labels au même runner. La préparation initiale (PowerShell 7,
tâche Windows, session RSLinx et fichiers de configuration) doit être faite sur
**chacun des deux ordinateurs**. Aucun dépôt ni SDK n'est nécessaire sur ces PC.
Le chemin par défaut reste `C:\natconveyor2-dev` et la tâche `Conveyor.Web` sur chacun.

Pour des chemins, tâches ou ports différents, utiliser les variables propres au PC :

| PC | Chemin | Tâche | URL de vérification |
| --- | --- | --- | --- |
| CONV-QC | `CONVEYOR_QC_DEPLOY_PATH` | `CONVEYOR_QC_TASK_NAME` | `CONVEYOR_QC_HEALTH_URL` |
| STH-CONV-H-11 | `CONVEYOR_STH_DEPLOY_PATH` | `CONVEYOR_STH_TASK_NAME` | `CONVEYOR_STH_HEALTH_URL` |

Ces variables prennent priorité sur les variables communes. Les fichiers
`appsettings*.json` et `conveyor.settings.json` de chaque PC restent locaux et ne
sont pas remplacés. Configurer sur chaque PC sa base, ses appareils et ses lignes.
Deux applications visant la même base ou les mêmes appareils ne sont pas isolées
par le simple fait d'utiliser deux runners.

Le paquet est conservé sept jours dans GitHub. La vérification HTTP attend environ
une minute ; elle ne valide pas les connexions physiques aux appareils. Le job
signale les erreurs sans retour automatique à la version précédente.

Pendant le remplacement des fichiers, la tâche est temporairement désactivée.
Le script attend la sortie des processus Conveyor du dossier cible et vérifie
les verrous avant toute copie. Un verrou temporaire est réessayé pendant 30 secondes.
Si un fichier reste verrouillé, vérifier les instances ouvertes manuellement et
les droits du compte runner ; le script ne termine pas les autres programmes.
La tâche est réactivée même en cas d'erreur, mais l'application n'est relancée
qu'après une copie réussie. Après un correctif du script, utiliser la nouvelle
exécution déclenchée par le push : relancer un ancien job réutilise son ancien paquet.

Références : [transfert d'artefacts entre jobs](https://docs.github.com/en/actions/tutorials/store-and-share-data),
[téléchargement d'artefacts](https://github.com/actions/download-artifact).

Le numéro affiché à côté de CENTRE DE CONTRÔLE est intégré à la compilation. Sur GitHub Actions, `V` est suivi du numéro d’exécution du workflow, incrémenté à chaque nouvelle exécution. Compilation, tests et publication d’une même exécution conservent le même numéro, y compris sur les deux serveurs. Une relance de la même exécution conserve ce numéro. En local, chaque compilation incrémente un compteur `.build-version` exclu de Git (première compilation : V2).
