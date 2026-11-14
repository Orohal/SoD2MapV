# Modding Workflow

This page describes how to move objects/sites in State of Decay 2 and export the result as a playable mod.

## Concepts

- **Tile map** (e.g. `faustite_x2_y3.umap`): contains the actual building meshes and child actors (doors, claim terminals, containers).
- **Region sites map** (e.g. `faustite_sites.umap`): contains the `TaggedSitePrimaryBuildingMarker` that drives the in-game map display of the site.
- **Tagged database** (`Tagged/Cooked/<region>_Tags.uasset` in the chunk-1095 pak): the runtime source for site interactables, containers, rooms, sites and spawn points. This is what makes a moved site **claimable**.

All three must be patched together for a site move to work in-game. The editor handles this automatically.

## Moving a single object

1. Open the tile map.
2. Enable editing.
3. Drag the object (Move mode). Use `R`/`S` for rotate/scale, `Delete` to remove.
4. `Ctrl+Z` to undo.
5. **Export mod .pak…** → save as `pakchunk<chunkId>-WindowsNoEditor_P.pak`.
6. Copy the `.pak` into `<game>\StateOfDecay2\Content\Paks\` and restart the game.

## Moving a house (group)

1. Right-click a house (or pick from the **House:** dropdown).
2. The prefab plus all objects within 40 m are selected.
3. Drag the group; use **Adjust height (Z)…** if it floats/sinks.
4. Export as above.

## Moving a site / outpost (claimable)

This is the full workflow that makes the site claimable at the new location.

1. Open the tile map containing the site.
2. Enable editing and the background map + **Sites (tgc)** overlay.
3. Click the site's box (or its name label in the overlay, or pick it from the **Site:** dropdown). All contained objects are selected.
4. Drag the group to the new location. The box follows live.
5. Use **Adjust height (Z)…** if needed.
6. **Export mod .pak…**. The export automatically:
   - Patches the **tile map** (meshes + child actors).
   - Patches the **region sites map** (`<region>_sites.umap`, matched via `TaggedSitePrimaryBuildingMarker` within < 1 cm) in the same pak.
   - Patches the **tgc** (site boxes/markers/entities) in the chunk-1095 pak.
   - Patches the **cooked tag database** (`Tagged/Cooked/<region>_Tags.uasset`) with byte-exact X/Y/Z triple shifts.
7. The export dialog reports:
   - Objects modified count.
   - **Attached child actor(s) left to follow their building (doors/terminals/containers): N** — these are carried by the moved prefab automatically; their relative transforms stay pristine.
   - **cooked asset: N position(s) shifted, M not found** — M are expected misses (e.g. prefab tile-local coordinates).

### Why child actors must NOT be moved individually

Doors, claim terminals (`OutpostInteractable_C_*`) and containers are **child actors** of the building prefab. Their transforms are stored **relative to the prefab's (yaw-rotated) local frame**, not in world space. Writing the world delta verbatim into that relative transform both double-moves the child (the owner already carries it) and smears it by the prefab's rotation (≈110° for the Autoshop). The result in v0.6.2: the claim terminal landed ~300 m off and was unclaimable.

The v0.6.3 fix detects attached children that move with their building and **leaves their relative transforms untouched** — they follow the prefab automatically. A child moved on its own still gets its delta, correctly rotated into the owner's frame.

## Where to put the exported pak

- **Tile / site moves:** `<game>\StateOfDecay2\Content\Paks\` (chunk patch, e.g. `pakchunk004600-WindowsNoEditor_P.pak` for Faustite). **Not** `Saved\Paks`.
- The `_P` suffix gives the mod priority over the base game.
- Remove the file to revert.

## Reverting

Delete the exported `pakchunk…_P.pak` (and any `_P` paks from older exports) from `Content\Paks\`, then restart the game. Original game files are never modified.

## Notes

- Always test in-game after a move; terrain height may require **Adjust height (Z)**.
- If a moved site is not claimable, check the export dialog's `cooked asset:` line and report region + tile.
- Old v0.6.2 `_P` paks contain the broken child placement — overwrite or delete them when re-exporting with v0.6.3.
