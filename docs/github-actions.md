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
Les deux jobs s'appellent **Compiler et tester sur GitHub** et **Installer sur le serveur**.
Un échec des tests empêche le déploiement. Les déploiements sont sérialisés.

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
Si plusieurs runners ont les labels ci-dessus, ajouter un label spécifique au
serveur dans `jobs.deploy.runs-on` pour sélectionner la bonne machine.

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
