# SoD2MapViewer v0.5.1 — Nexus Release Notes (changes only)

[size=3][b]SoD2MapViewer v0.5.1 — What's new[/b][/size]

[b]Removed[/b]
[list][*]WeMod/Wand tile background (CDN dependency) including the tile layer,
[c]--wandcheck[/c], [c]--wandrender[/c], [c]--fancalib[/c].
[*]The custom vector basemap (rendered from the game's own [i]MapBackgroundData[/i] assets)
remains the reliable offline base.[/list]

[b]Fixed[/b]
[list][*]Region detection for Trumbull map paths ([c]maps/trumbull_valley/[/c]).[/list]

[b]Note on v0.5.0 (base features)[/b]
[list][*]Custom vector basemap (offline, all 5 regions), drag calibration
("Vector map (asset)" + "Edit base-map pos"), CLI [c]--bgrender[/c]/[c]--bgset[/c],
[c]--maptex LIST/DUMP/PROPS/JSON[/c].[/list]
