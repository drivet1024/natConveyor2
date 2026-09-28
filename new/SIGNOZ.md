# Logs SigNoz

Les logs `ILogger` (messages structurés, exceptions et scopes) sont exportés en
OTLP par lots. La console et la page des logs restent disponibles. L’export est
activé par `signoz.settings.json` vers `http://192.168.1.152:4317` en gRPC.
Ce fichier livré fournit les valeurs par défaut; les paramètres habituels
de l’application et `conveyor.settings.json` ont priorité. Aucun historique local
n’est réexpédié. La file d’export est en mémoire, sans garantie de livraison
pendant une panne prolongée du collecteur.

Pour remplacer ces valeurs, ajouter cette section à la racine du fichier **local** `appsettings.json`
du serveur, puis redémarrer l’application :

```json
{
  "SigNoz": {
    "Enabled": true,
    "Endpoint": "http://SERVEUR-SIGNOZ:4317",
    "Protocol": "grpc",
    "ServiceName": "Conveyor.Web"
  }
}
```

Avec HTTP/protobuf, utiliser `Protocol: "http/protobuf"` et l’adresse complète
`http://SERVEUR-SIGNOZ:4318/v1/logs`. Le port de l’interface web SigNoz n’est
pas l’adresse d’ingestion OTLP.

Pour SigNoz Cloud, utiliser l’adresse d’ingestion régionale HTTPS fournie par
SigNoz et configurer `SigNoz__Headers=signoz-access-token=...` dans l’environnement
du compte qui lance l’application. Ne pas committer la clé. Les autres réglages
peuvent également être fournis par `SigNoz__Enabled`, `SigNoz__Endpoint`,
`SigNoz__Protocol` et `SigNoz__ServiceName` (ne pas définir la même clé dans le
fichier `conveyor.settings.json`, chargé après les variables d’environnement).

Dans SigNoz, filtrer `service.name = Conveyor.Web` et `host.name` égal au serveur
(les sévérités exportées sont `TRACE`, `DEBUG`, `INFO`, `WARN`, `ERROR`, `FATAL`)
(par exemple `STH-CONV-H-11`). Les niveaux restent contrôlés par `Logging:LogLevel`;
un filtre spécifique peut être défini sous `Logging:OpenTelemetry:LogLevel`.
Pour désactiver l’export, mettre `Enabled` à `false` et redémarrer.

Documentation : [exporteur OTLP .NET](https://github.com/open-telemetry/opentelemetry-dotnet/tree/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol),
[ingestion SigNoz locale](https://signoz.io/docs/ingestion/self-hosted/overview/).

## Diagnostic des lectures OPC DA

Filtrer les messages contenant `Diagnostic OPC`, catégorie
`Conveyor.Web.Infrastructure.OpcDaPlcGateway`.
Une surveillance indépendante des lectures contrôle toutes les 5 secondes :

- absence de réception **acceptée** depuis 30 secondes pour un tag lu périodiquement;
- absence de contrôle terminé depuis 30 secondes, opération en cours et nombre de contrôles sautés à cause du verrou occupé;
- reprise des réceptions/contrôles, avec durée de l’interruption.

Les avertissements sont limités à un par tag et par période de 30 secondes,
plus un avertissement global pour les contrôles. Les tags configurés
uniquement en subscription (notamment chute/transfert) ne déclenchent pas
d’alarme pour un simple silence : une valeur inchangée peut légitimement ne
générer aucune notification. Ils déclenchent cependant une alerte si des
horodatages anciens sont rejetés et qu’aucune réception n’a été acceptée
depuis 30 secondes. Les compteurs partagés restent lus périodiquement.
Les alertes de tags indiquent la dernière arrivée, la source (`subscription`
ou `control-read`), la valeur, l’horodatage OPC, la qualité et le nombre de
réceptions rejetées pour horodatage ancien. Une réception valide de la même
valeur rafraîchit bien la surveillance.

Pour une capture détaillée temporaire, définir dans la configuration locale
`Logging:OpenTelemetry:LogLevel:Conveyor.Web.Infrastructure.OpcDaPlcGateway`
à `Debug`. Cela inclut chaque réception (acceptée ou rejetée), son horodatage
OPC précédent, et le début/la durée/le nombre de valeurs des contrôles.
Revenir à `Information` après le diagnostic pour limiter le volume.
La surveillance n’effectue aucune reconnexion ni écriture et ne change pas
les règles d’acceptation des valeurs ou l’état de santé existant.
