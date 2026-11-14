# SoD2 Map Viewer / Editor v0.6.3 (English)

## Highlights
- **Moved sites are now fully claimable in-game (confirmed):** v0.6.2 moved the building meshes but claim terminals, doors and containers stayed behind. Root cause: child actors nested under a building prefab store their transforms **relative to the prefab's (yaw-rotated) local frame**. The old export wrote the world-space delta verbatim into that relative transform, which double-moved the child (the owner already carries it) and smeared it by the prefab's rotation — in testing the claim terminal landed ~300 m off.
- **The fix:** on export, the editor now detects attached child actors (doors, `OutpostInteractable` claim terminals, containers) that move together with their building and **leaves their relative transforms untouched** — they are carried by the moved prefab automatically. A child moved individually still gets its delta, correctly rotated into the owner's local frame. The export dialog reports how many child actors were left to follow their building.
- **In-game verified:** building, claim terminal, doors and containers all at the new position, site claimable.

## Upgrade notes
- Re-export your site moves with v0.6.3 (the v0.6.2 paks contain the broken child placement). Old v0.6.2 `_P` paks in the game's `Paks` folder should be deleted or overwritten by the new export.

## Install
- Framework-dependent build (requires .NET 10 runtime). Download one zip, extract, run `SoD2MapViewer.exe`.
- `SoD2MapViewer-framework-dependent.zip`: single-file build.
- `SoD2MapViewer-framework-dependent-plain.zip`: plain (unpacked) build — try this if the single-file build is flagged by AV/Defender.

## Assets
- `SoD2MapViewer-framework-dependent.zip`
- `SoD2MapViewer-framework-dependent-plain.zip`
- `nexus_release_v063.md`

## Checksums (SHA256)
- `SoD2MapViewer-framework-dependent.zip`: `846c1b932ebfb537168dbdde0865e580a9e72b6f3aa66d55000e25b69bd92709`
- `SoD2MapViewer-framework-dependent-plain.zip`: `9703c33946ddada7e0e5e1210ddb659d16f9c926031e6b89392c3972a2bd77fa`
- `SoD2MapViewer.exe` (single-file): `8bed04a685c5c6f0c10f5df1cc1098d402a839c4d76b95e2881123959753386d`
- `SoD2MapViewer.exe` (plain): `d06acdc7ebb52fd43489ff706dd530a140b6d3bc42f3ffbc0710ccd8ec831232`
