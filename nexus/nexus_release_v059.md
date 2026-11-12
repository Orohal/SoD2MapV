# SoD2MapV v0.5.9

## Highlights
- **Background map rebuilt (single vector layer, auto-anchored):** only the game-asset vector basemap (`MapBackgroundData`) is used, clearly identified (region/asset/anchor in on-canvas overlay + status line), automatically anchored to the tile/world offset. The Fandom basemap was removed from UI, render and drag. Fixes for Faustite rotation/mapping and culling.
- **tgc Site/Outpostable overlay:** claimable buildings (site boxes from the region `.tgc`) are drawn at their real positions on the background map (checkbox "Sites (tgc)"), matching the automatic anchor.
- **Faustite calibration** reworked (scale 1.0, user-calibrated via drag).

## Notes
- GUI: toolbar now wraps, "Feinjustierung (Drag)" reachable, commit shows a MessageBox with the resulting ox/oy.
- Build: 0 errors.

## Downloads (GitHub release)
- `SoD2MapViewer-framework-dependent.zip` - small single-file EXE (requires .NET 10 Desktop Runtime), SHA256 `99006D3CFE423B527545CA315E5B572B09056436E2CFE2BBBEF70E02852CB866`
- `SoD2MapViewer-framework-dependent-plain.zip` - unpacked EXE + DLLs (same function, use if a virus scanner flags the packed apphost), SHA256 `EF69D85F2369F21917E05D983FB07E49685AC844D66885A9B47EDB9AB17060C9`
- Single-file EXE inside framework-dependent.zip: SHA256 `CC04DC69D82421240436428E5868A3265E77BDD8051B409054D2F9F70E45231E`
- Unpacked EXE inside plain.zip: SHA256 `BEDD8128B82BAB4F0EA435D6BA0A6EF683802C8BA1444B11ECEBD452AFB076A7`
"