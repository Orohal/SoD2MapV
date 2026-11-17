# Mesh viewer, favorites & language

## Mesh viewer (GUI)

Open with the **Mesh-Viewer** button in the toolbar (or press `V`).

- **Asset** field: enter a short name (`asphalt_slab_01`) or a provider path, then **Load**.
- **Search...** opens a filter dialog over all mounted assets (e.g. `antenna`, `wall`, `tower`); double-click a hit to load it.
- Mouse drag orbits, mouse wheel zooms, double-click or `R` resets the view.
- Readout: mesh/path, **Size X/Y/Z in cm**, **Origin X/Y/Z cm**, vertex and triangle counts.

Presets include `DLC2_UpgradeableWall` (Blueprint fallback), `greybox_wall_climbable/blocking`, `grocery_store_sign`, `asphalt_slab_01` and more.

Headless equivalents:

```sh
SoD2MapViewer.exe --meshview "<gameDir>" asphalt_slab_01 out.png [yawDeg] [pitchDeg]
SoD2MapViewer.exe --meshsearch "<gameDir>" tower 40
```

## Favorites

- **Maps:** check the box next to a map in the left tree. A region node checks all of its maps.
- **Objects:** multi-select rows in the object table → **★ Favorites ▾** → mark / unmark / show only favorites / **copy names**.
- The **Favorites** panel at the bottom lists everything with `🗺` (map) or `📍` (object). Double-click a map to load it; double-click an object to select it in the current view (if it is visible).
- The bottom area is a splitter: drag the divider between Favorites and Events to resize. The sizes are persisted in `session.json`.

## Shortcuts

| Key | Action |
|---|---|
| `M` / `R` / `S` | Move / Rotate / Scale mode — press again to switch it off |
| `P` | Show character / player position |
| `V` | Open mesh viewer |
| `F` | Fit all objects |
| `Ctrl+Z` | Undo |
| `Delete` / `Esc` | Delete selection / clear selection |
| `? Hilfe` | Full cheat sheet (German or English) |

## Language (DE/EN)

The **🌐 DE / 🌐 EN** button toggles the UI language. The two dictionaries `lang_de.json` and `lang_en.json` sit next to `SoD2MapViewer.exe` and can be edited without rebuilding (keep the same keys in both). Switching saves the choice to `session.json` and restarts the viewer immediately so every label is translated. CLI output and the status log are intentionally not localized.
