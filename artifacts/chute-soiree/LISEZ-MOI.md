# Esquisses — achalandage du convoyeur du haut

Ces trois PNG sont des propositions à examiner avant toute modification de l’application.

## Source et périmètre

- Source : MySQL central 192.168.1.153:3307, base nationex, table parcel_history. Lecture seule.
- Filtres demandés : DEPOT_ID = 1, EXCEPTION = 903, SOURCE_TYPE = 200, temps basé sur DATE_LIV.
- Correspondance confirmée par l’utilisateur : SOURCE_ID NULL ou 1 = convoyeur du haut; SOURCE_ID 3 = convoyeur au sol.
- Analyse des 20 soirées du lundi au vendredi du 31 août au 25 septembre 2026, 16 h à 4 h. Les événements après minuit sont rattachés à la soirée précédente. La soirée du 28 septembre, encore en cours à l’extraction, est exclue. Le 7 septembre est conservé comme lundi; les jours fériés ne sont pas filtrés.
- DATE_LIV est utilisé tel qu’enregistré. À l’extraction, les derniers événements étaient datés vers 21 h 42 alors que NOW() serveur indiquait 01 h 43 UTC le lendemain : comportement compatible avec l’heure locale de Montréal. Aucune conversion de fuseau supplémentaire n’a été appliquée.
- Lignes annulées exclues (VOID). Dédoublonnage exact sur PARCEL_ID + DATE_LIV + CHUTE_NO, toutes sources du haut réunies : 11 doublons retirés.
- Les repassages à des heures différentes sont conservés : il s’agit d’achalandage en passages historiques, pas de colis uniques ni d’une confirmation physique de chute.

## Calcul et contrôles

Volume par chute et demi-heure / 20 soirées. Les créneaux sans événement contribuent zéro à cette moyenne. Les 20 soirées ont des données; la complétude de chaque créneau ne peut pas être garantie par l’historique seul.

419 598 passages dans le périmètre, dont 407 888 sur les chutes présentes dans le schéma actuel. Les 11 710 autres (2,8 %) ne sont pas replacés arbitrairement. Leur répartition est conservée dans summary.json. Les principales valeurs hors plan sont 19, 20 et 98.

Pic moyen des chutes représentées : 22 h 30–23 h, environ 1 438 passages. À ce créneau : chute 38 = 210,1 passages moyens; 39 = 192,3; 1 = 80,4; 23 = 79,05; 32 = 72,6. Valeurs arrondies sur les PNG.

La couleur représente un volume absolu moyen, avec une échelle commune. Elle ne mesure ni la saturation ni le pourcentage de capacité : les capacités des chutes ne sont pas connues. Les cartes et grilles montrent uniquement les chutes du plan actuel.

## Les trois propositions

1. **01-plan-par-heure.png** : plan coloré pour une demi-heure choisie, volumes au bout des chutes, classement des cinq premières et profil horaire. Le curseur est une interaction proposée, pas une fonction implémentée dans le PNG.
2. **02-grille-chutes-heures.png** : toutes les chutes et heures d’un seul coup d’œil; chiffre et colonne de droite indiquent le pic moyen de chaque chute. Les couleurs sont comparables entre lignes.
3. **03-phases-de-soiree.png** : quatre plans comparables par phases de trois heures, classement des trois premières chutes et profil total moyen. Les valeurs de phase sont normalisées en passages / 30 min. Le ruban montre les percentiles 25 et 75 entre soirées, pas un intervalle de confiance ni une prévision.

Recommandation : option 1 pour intégrer l’analyse au plan existant; option 2 pour repérer l’ensemble des périodes chargées et préparer la répartition du personnel.

## Reproduction

upper-history.sql est la requête définitive. upper-history.json contient uniquement des agrégats, aucun identifiant de colis. render_sketches.py produit les trois images avec Python, numpy et matplotlib. summary.json contient les contrôles chiffrés. history.sql/history.json correspondent à l’exploration initiale de toutes les sources avant exclusion des fins de semaine; ils ne servent pas au rendu final.

Aucun code applicatif n’a été modifié, aucun déploiement effectué.
