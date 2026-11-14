# SoD2 Map Viewer / Editor v0.6.4 (English)

## Highlights
- **Region-wide site selection:** Clicking a **region group node** in the map list (e.g. "Trumbull Valley (37)") now lists **all sites of the region** across all tiles in the Site dropdown, labelled with their tile (`OUT Autoshop (84D09511)  @ faustite_x2_y3`). Selecting a site from another tile loads that tile automatically, selects the site (all contained objects) and centres the view. Tile data comes from the region tgc (`<tile>:`-prefixed entity paths), so no extra map parsing is needed.
- **Persistent item presets:** Item presets are now stored in `%LOCALAPPDATA%\SoD2MapViewer\item_presets.json` and survive sessions and save switches. New buttons in the Inject-item dialog: **"Preset speichern"** (save the current item type/class path with a name), **"Preset löschen"** (delete a stored preset), and **"Presets aus allen Saves"** (bulk-scan every savegame and add all missing item classes as presets). Presets are cross-save compatible — class paths are portable; the cloned item instance template still comes from the target save.
- **Toolbar reorganization:** The top toolbar is now organized into four labelled groups — **Game | Filter | Selection | Actions** — with the view toggles appended. "Enable editing (drag objects)" sits next to the fine-tune checkbox (red label). Preset dropdowns are sorted alphabetically.

## Notes
- Requires the .NET 10 runtime (framework-dependent builds).
- Nothing changes about the export workflow: moving a site and exporting still patches tile map + region sites map + tgc + cooked tag database so moved outposts stay claimable.

## Install
- Framework-dependent build (requires .NET 10 runtime). Download one zip, extract, run `SoD2MapViewer.exe`.
- `SoD2MapViewer-framework-dependent.zip`: single-file build.
- `SoD2MapViewer-framework-dependent-plain.zip`: plain (unpacked) build — try this if the single-file build is flagged by AV/Defender.

## Assets
- `SoD2MapViewer-framework-dependent.zip`
- `SoD2MapViewer-framework-dependent-plain.zip`
- `nexus_release_v064.md`

## Checksums (SHA256)
- `SoD2MapViewer-framework-dependent.zip`: `332598df73f835e5ae7ed48f24340e70ad0999cb3ef426035e81dc65466072ee`
- `SoD2MapViewer-framework-dependent-plain.zip`: `17777b8c0e69c64dedec395407741b0683d66ba89ac18308145dc2c8b7bd97df`
- `SoD2MapViewer.exe` (single-file): `12e64dacbfbae9234785b0ba8481b40a2e58ef559aed03d2237edfc984bfb30`
- `SoD2MapViewer.exe` (plain): `13be5806cec1a92bee71a1ced1176d8af84278d1a39869c4285fde693971aad9`
