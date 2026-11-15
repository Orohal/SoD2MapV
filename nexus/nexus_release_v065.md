# SoD2 Map Viewer / Editor v0.6.5 (English)

## Highlights
- **New group (clone + identity, verified in-game):** A community save can now be duplicated as a **separate, playable community**: the tool copies the selected `.sav` to the **next free slot name** (`SaveGame_Vanilla_2` → `SaveGame_Vanilla_3.sav`) and rewrites the **identity GUIDs (`ID` + `GameId`)** with fresh random values inside the save. The game then treats the copy as its own community — verified in-game (appears in the community list and loads/plays fine). The world state (vehicles, items, base) is copied verbatim, so the new group starts as a full clone of the source community and can be used as a fresh testing ground for map exploration.
- **"Neue Gruppe…" button in both inject dialogs:** The vehicle and item inject dialogs each gain a **"Neue Gruppe…"** button next to "Kopie erstellen". The clone is created, validated (self-check: reparse, GUID comparison, vehicle count) and immediately selected in the dialog — so you can inject vehicles/items into the fresh group right away.
- **`--saveclone` CLI:** `SoD2MapViewer.exe --saveclone <save.sav> [dst.sav]` — clones to the next free slot name (or an explicit destination) with rewritten identity GUIDs. Fail-safe: an existing destination is never overwritten, and if the self-check fails the written file is deleted again.
- **Fix:** removed a duplicate `_btnCopySave.Click` registration in the item inject dialog (the copy handler ran twice per click).

## Notes
- Requires the .NET 10 runtime (framework-dependent builds).
- The display name of the clone stays the same as the source community (e.g. "Mourners") — you may want to rename it in-game to tell the two apart.
- Nothing changes about the export workflow: moving a site and exporting still patches tile map + region sites map + tgc + cooked tag database so moved outposts stay claimable.

## Install
- Framework-dependent build (requires .NET 10 runtime). Download one zip, extract, run `SoD2MapViewer.exe`.
- `SoD2MapViewer-framework-dependent.zip`: single-file build.
- `SoD2MapViewer-framework-dependent-plain.zip`: plain (unpacked) build — try this if the single-file build is flagged by AV/Defender.

## Assets
- `SoD2MapViewer-framework-dependent.zip`
- `SoD2MapViewer-framework-dependent-plain.zip`
- `nexus_release_v065.md`

## Checksums (SHA256)
- `SoD2MapViewer-framework-dependent.zip`: `48d3433e58f9f477d5e2032250f58b3fde87306ef35b2b22e8105bd0ee206360`
- `SoD2MapViewer-framework-dependent-plain.zip`: `ec44a7d98befad7ac0b211dce01a2143e0aceff143d9f32a539e183605f8fbd2`
- `SoD2MapViewer.exe` (single-file): `848247b4dae747d962d15abdace146b9006b376909c9b33dbe428d2191e04191`
- `SoD2MapViewer.exe` (plain): `fd638e353e33d47f91fbcf841bc4a9465d7f15e9e13e5efcec9b59c1d9b227b8`
