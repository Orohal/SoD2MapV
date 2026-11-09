# SoD2MapViewer v0.5.0 — Nexus-Release-Notizen (nur Änderungen)

[size=3][b]SoD2MapViewer v0.5.0 — Was ist neu[/b][/size]

[b]Neu: eigene Vektor-Basemap (offline, alle 5 Regionen)[/b]
[list][*]Die Karte wird jetzt aus den Ingame-[i]MapBackgroundData[/i]-Assets gerendert
(Straßen, Flüsse, Seen, Gebäude, Kartenrand) — in den echten Ingame-Farben.
[*]Komplett offline, für alle 5 Regionen (Trumbull Valley, Cascade Hills,
Drucker County, Meagher Valley, Providence Ridge).
[*]Region wird automatisch aus dem geladenen Kartenpfad gewählt.
[*]Kalibrierung per Drag: [b]„Vector map (asset)"[/b] + [b]„Edit base-map pos"[/b]
einschalten, Karte ziehen, loslassen speichert.
[*]CLI: [c]--bgrender[/c] (PNG-Render), [c]--bgset[/c] (manuelle Transform-Werte).[/list]

[b]Entfernt[/b]
[list][*]WeMod/Wand-Kachel-Hintergrund (CDN-Abhängigkeit) samt Kachel-Layer,
[c]--wandcheck[/c], [c]--wandrender[/c], [c]--fancalib[/c].
[*]Die Vektor-Map ersetzt die zuvor ungenau kalibrierte Fandom-Map als
verlässliche Basis.[/list]

[b]Sonstiges[/b]
[list][*]Fix: Regionserkennung für Trumbull-Kartenpfade ([c]maps/trumbull_valley/[/c]).
[*]Neues Paket-Exploration-Tool: [c]--maptex LIST/DUMP/PROPS/JSON[/c].[/list]
