# Esquisses sans les chutes 38 et 39

Même source et méthode que dans le dossier parent : 20 soirées du lundi au vendredi, du 31 août au 25 septembre 2026, convoyeur du haut (SOURCE_ID NULL ou 1). Aucune modification de l’application.

Les chutes 38 et 39 restent grisées sur le plan. Leurs passages sont exclus des couleurs, classements, courbes et totaux affichés. Les données originales sont conservées.

- Chute 38 exclue : 56 087 passages.
- Chute 39 exclue : 55 330 passages.
- Passages représentés : 296 471.
- Passages hors plan, toujours séparés : 11 710.
- Réconciliation : 296 471 + 56 087 + 55 330 + 11 710 = 419 598.
- Échelle commune recalculée : 0 à 98,2 passages moyens par chute et par demi-heure. Elle mesure le volume, pas la capacité ni la saturation.
- Pic du volume représenté : 22 h 30–23 h, 1 035,6 passages moyens par soirée.

Reproduction : `python artifacts/chute-soiree/render_sketches.py --source 1 --exclude 38 39` (numpy et matplotlib requis).

Les PNG originaux restent disponibles dans le dossier parent.
