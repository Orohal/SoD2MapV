# SoD2 Map Viewer / Editor v0.6.2 (English)

## Highlights
- **Cooked tag-database patch (runtime fix):** Runtime loads `Tagged/Cooked/<region>_Tags.uasset` (`TaggedDatabaseAsset.Database`) for sites/interactables/containers/rooms/spawn points, not the compiled `.tgc` JSON. v0.6.2 now patches **both** tag databases in the chunk-1095 pak (JSON + cooked asset) with exact X/Y/Z triple shifts, so claim terminals, doors, containers and spawn points should follow a moved site/outpost building.
- **Site move workflow:** Existing site-aware tile + region site-map export stays in place (auto multi-map, chunk patch, tgc/site boxes). Export status/message now reports how many cooked-asset positions were shifted and how many (if any) were not found.

## Known issue / in-game test pending
- **Claimable-site runtime move is not yet confirmed in-game.** v0.6.1 showed meshes move while claim/doors/containers stayed at the original location (even with a fresh save). v0.6.2 targets the confirmed runtime source (`Tagged/Cooked/*_Tags.uasset`); if claim/doors/containers still do not follow, report the export status line (`cooked asset: N position(s) shifted, M not found`) and region/tile.

## Install
- Framework-dependent build (requires .NET 10 runtime). Download one zip, extract, run `SoD2MapViewer.exe`.
- `SoD2MapViewer-framework-dependent.zip`: single-file build.
- `SoD2MapViewer-framework-dependent-plain.zip`: plain (unpacked) build — try this if the single-file build is flagged by AV/Defender.

## Assets
- `SoD2MapViewer-framework-dependent.zip`
- `SoD2MapViewer-framework-dependent-plain.zip`
- `nexus_release_v062.md`

## Checksums (SHA256)
- `SoD2MapViewer-framework-dependent.zip`: `66ac1f2830b63a0e4b007200421561a1d1822cb13749d6d6003de0c5342c996d`
- `SoD2MapViewer-framework-dependent-plain.zip`: `854917b66bf3e1ace1dbbe9a275cf273afe415a23ab1124559dd2d110b212daa`
- `SoD2MapViewer.exe` (single-file): `119890d3f9fbc2cb2cc18eddf0edb88cec2a7893f855fd523a7119a2ffd3df33`
- `SoD2MapViewer.exe` (plain): `779e4aaff378380b55219409ee1913b664f24198819aae79a37bf458f8712c97`
