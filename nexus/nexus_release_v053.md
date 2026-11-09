# SoD2MapViewer v0.5.3 — Nexus Release Notes (changes only)

[size=3][b]SoD2MapViewer v0.5.3 — What's new[/b][/size]

[b]Player position & easier vehicle injection[/b]
[list][*][b]"Show character"[/b] button: displays the player's last saved position
from the savegame ([c]PlayerBreadcrumbs[/c], using existing save calibration) on the map.
[*][b]"Inject at player"[/b] button: prefills player position/height (+10 m offset);
includes a hint to stand in an open area. Facing direction is not stored in breadcrumbs
— adjust yaw/position in the dialog if needed.
[*]Number fields accept comma or dot; empty Z/shift/yaw fields count as 0.[/list]

[b]Background maps[/b]
[list][*]The Fandom map (rough orientation) is now draggable via [b]"Edit base-map pos"[/b]
when "Fandom map" is on and "Vector map" is off (offset is saved).
[*]Vector basemap (faustite/Trumbull): uses the correct asset
([c]faustite_persistent_Background[/c], 180° orientation) with variant-aware calibration.[/list]
