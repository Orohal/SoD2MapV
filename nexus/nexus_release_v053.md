# SoD2MapViewer v0.5.3 — Nexus-Release-Notizen (nur Änderungen)

[size=3][b]SoD2MapViewer v0.5.3 — Was ist neu[/b][/size]

[b]Spielerposition & Fahrzeug-Injektion vereinfacht[/b]
[list][*]Button [b]„Show character"[/b]: zeigt die zuletzt gespeicherte Spielerposition
aus dem Save ([c]PlayerBreadcrumbs[/c], mit bestehender Save-Kalibrierung) auf der Karte.
[*]Button [b]„Inject at player"[/b]: füllt Spieler-Pos/Höhe (+10 m Offset) vor;
Hinweis, möglichst auf freier Fläche zu stehen. Blickrichtung ist aus dem Save nicht
ablesbar — Yaw/Position im Dialog bei Bedarf anpassen.
[*]Zahlenfelder akzeptieren Komma oder Punkt; leere Z-/Shift-/Yaw-Felder zählen als 0.[/list]

[b]Hintergrundkarten[/b]
[list][*]Fandom-Map (grobe Orientierung) ist jetzt per [b]„Edit base-map pos"[/b]
verschiebbar, wenn „Fandom map" an und „Vector map" aus ist (Versatz gespeichert).
[*]Vektor-Basemap (faustite/Trumbull): korrektes Asset
([c]faustite_persistent_Background[/c], 180°-Ausrichtung) mit variantenspezifischer
Kalibrierung; Kalibrierung asset-variantenfähig.[/list]
