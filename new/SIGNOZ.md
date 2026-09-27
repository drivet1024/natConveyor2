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
(par exemple `STH-CONV-H-11`). Les niveaux restent contrôlés par `Logging:LogLevel`;
un filtre spécifique peut être défini sous `Logging:OpenTelemetry:LogLevel`.
Pour désactiver l’export, mettre `Enabled` à `false` et redémarrer.

Documentation : [exporteur OTLP .NET](https://github.com/open-telemetry/opentelemetry-dotnet/tree/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol),
[ingestion SigNoz locale](https://signoz.io/docs/ingestion/self-hosted/overview/).
