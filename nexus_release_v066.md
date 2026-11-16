# SoD2 Map Viewer / Editor v0.6.6 (English)

## Highlights
- **Inject platform at player position (verified in-game):** New menu entry **Inject ▾ → "Plattform an Spieler-Position…"** places a flat concrete platform (default **30×30 m**, size/yaw/height offset configurable, default **+50 cm above ground**) at the player's last saved position — perfect as a flat building ground for prefab placement/modding.
  - **Density-based tile picking:** the correct map tile is found by measuring the density of nearby actor positions around the target in the verified identity frame `world_save = RelLoc + WorldTileInfo.Position` (no more wrong-tile placements).
  - **Robust ground height:** ground Z is estimated as the **median of plausible nearby component Z values** (±5 m band around the save Z) instead of a raw low percentile — the platform reliably sits above the terrain instead of sinking into it.
  - **Safe candidate selection:** only scale-capable StaticMeshComponents with proven flat floor meshes (`concrete_slab_01` / `asphalt_cracked_01`) at least **150 m away** from the target are patched, so no visible hole appears at the source location. Mesh bounds are read live from the game assets.
  - **Byte-exact safety:** only RelativeLocation/RelativeRotation/RelativeScale3D are patched in place; a full roundtrip re-parse must be **byte-identical** or the process aborts. The mod pak only overrides the tile map in memory — original game files are never modified.
  - Install into the game with one confirmation click, or just produce the pak. Verified in-game: platform visible, character gets placed on top of it.
- **In-place SMC patching CLI:** `--patchsmc <in.umap> <out.umap> <export1> <x> <y> <z> [pitch yaw roll] [sx sy sz]` teleports/scales/rotates an existing actor component — the proven mechanism for playing map edits; plus `--packumap` and `--umaproundtrip` (byte-identical verification) and `--injectplatformauto` for the fully headless platform pipeline.
- **Map view readability:** actor dots are now drawn in saturated blue with a 5-px minimum size and a dark outline — clearly visible at any zoom level without being obtrusive.

## Notes
- Requires the .NET 10 runtime (framework-dependent builds).
- Platform injection reads the player position from the save breadcrumbs; the height offset default (+50 cm) reflects measured terrain levels. If your terrain differs, adjust the offset in the dialog.

## Install
- Framework-dependent build (requires .NET 10 runtime). Download one zip, extract, run `SoD2MapViewer.exe`.
- `SoD2MapViewer-framework-dependent.zip`: single-file build.
- `SoD2MapViewer-framework-dependent-plain.zip`: plain (unpacked) build — try this if the single-file build is flagged by AV/Defender.

## Checksums (SHA256)
- `SoD2MapViewer-framework-dependent.zip`: `B5E2BB8AA2CDEFBE2AAF77B3513CD08261A9516B902EF61EBD403B1D63C56DC0`
- `SoD2MapViewer-framework-dependent-plain.zip`: `BD2672A3162C3FC13BA722952FB1D8F03AC6B018840A4683AC9CC33FC5867356`
- `SoD2MapViewer.exe` (single-file): `22B1ABDF44FAAA552DF241632B97B2350A6211B8145AF950E95F2C8C44FB41C8`
- `SoD2MapViewer.exe` (plain): `C8D02E56F04E6012C27532549BAAE9BDCF0C834DD60AB32C31FF59A003EC3E4D`
