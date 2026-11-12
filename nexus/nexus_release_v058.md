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

## Downloads (GitHub release)
- `SoD2MapViewer-framework-dependent.zip` — small single-file EXE (requires .NET 10 Desktop Runtime), SHA256 `38999BC7C8F9752DBDA6CC752DCE16297CC8C9FB92F563A5D7D2E41D741ABA6C`
- `SoD2MapViewer-framework-dependent-plain.zip` — unpacked EXE + DLLs (same function, use if a virus scanner flags the packed apphost), SHA256 `F25779D49C487182362E26356E7B105122BCB609ACE71D5EB730277C4C3C69A1`
- Single-file EXE inside framework-dependent.zip: SHA256 `E796BD92C58F05BD872CDBCCBD577DF8ABC4AFB8C800275F0E45A9324C89AB66`
- Unpacked EXE inside plain.zip: SHA256 `EB6EC82542BFDBBA3C5E0A187414C542ECFB8C354918773FB2347408D1CF456B`

