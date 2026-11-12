# SoD2MapV v0.5.8

## Highlights
- **Multi-Map export:** New **"Multi-Map"** checkbox plus **"Finalize Export (N)"** button. Several patched maps are combined into a **single** chunk patch (`pakchunk<chunkId>-WindowsNoEditor_P.pak`).
- Why it matters: the on-map display of an outpost is driven by the `TaggedSitePrimaryBuildingMarker` in `<region>_sites.umap`, while the claim prompt comes from `OutpostInteractable_C` in the tile map. Both maps must be patched **together in one pak** for the moved outpost to appear on the in-game map.
- Workflow: check "Multi-Map" → edit the sites map (`*_sites.umap`) → Export mod .pak… → edit the tile map (building + `OutpostInteractable_C` move together) → Export mod .pak… → "Finalize Export (N)" and save as `pakchunk<chunkId>-WindowsNoEditor_P.pak` into `…\StateOfDecay2\Content\Paks`.
- Re-exporting the same map replaces its pending entry; edit flags are reset after adding.

## Notes
- Map shows the moved outpost, but full claimability (room count, safe/unsafe state) still depends on the game's site registration (save/site GUIDs); map-actor patches alone are not sufficient.
- GUI fix: "Multi-Map" checkbox is now readable in the dark theme.
- Build: 0 warnings / 0 errors.
