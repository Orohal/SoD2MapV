# SoD2MapV v0.5.7

## Highlights
- **Coordinate-space fix (world vs. tile-local):** Player marker, view centering, and right-click injection point are now correctly transformed between world and tile-local coordinate spaces (using the tile offset). Previously, world coordinates were written into tile-local space, causing objects dragged onto the player marker to end up at wrong in-game positions.
- **Function actors follow the building:** `OutpostInteractable_C` and `HomesiteInteractable_C` are now linked to their building prefab via the `Owner` property and are automatically moved together with the building mesh (drag + "Adjust height (Z)" + export). Bugfix: linked-actor delta is now applied absolutely (not cumulatively per mouse-move).
- **Bugfix:** cumulative delta application for owner-linked actors corrected.

## Notes
- Outpost claim functionality may additionally depend on the game's site registration mechanism (save-site GUIDs). Moving the interactables is a prerequisite but may not alone make the outpost claimable in-game.
- Build: 0 warnings / 0 errors.
