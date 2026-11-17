# SoD2 Map Viewer / Editor v0.6.7 (English)

## Highlights
- **Mini Mesh Viewer (GUI + CLI):** New **Mesh-Viewer** button opens a software-rendered 3D preview with orbit (mouse drag), zoom (wheel) and a bounds readout (Size/Origin in cm, vertex/triangle counts). Loads `UStaticMesh` / `USkeletalMesh` (LOD0) with a Blueprint-export fallback. The **"Suche..."** dialog searches every mounted asset by filter (e.g. `antenna`, `wall`, `sign`) and loads a hit with one double-click.
- **Headless mesh tools:** `--meshview <gameDir> <asset> <out.png> [yaw] [pitch]` renders a shaded PNG and prints exact bounds; `--meshsearch <gameDir> <filter> [limit]` lists matching assets. Ideal for measuring meshes without starting the game.
- **Layout & control rework:** Toolbar reorganized into three grouped rows (game/filter · selection/actions · mode/view) so nothing gets clipped or wraps unpredictably. All status messages are now collected in **one timestamped log box** at the bottom with a clear button and a compact live info row (mode, mouse position, zoom, selection count).
- **Favorites & multi-select:** The map list has checkboxes plus a **"★ Nur Favoriten"** filter; the object table supports multi-select and a **"★ Favoriten ▾"** menu (mark / unmark / show only favorites / **copy names**). Favorites persist in `session.json`.
- **Editing aids:** Visible mode buttons (Move/Rotate/Scale, synced with M/R/S), a German **"? Hilfe"** shortcut dialog, a snap grid (off / 0.5 / 1 / 5 m) for precise moves, and live cursor coordinates with a 16-point compass in the verified frame **+X = North, +Y = East**.
- **Polish:** Unified dark theme (`UiTheme`) and German UI labels across the app and dialogs; compact, readable canvas HUD (the long shortcut hint moved into the help dialog).

## Notes
- Requires the .NET 10 runtime (framework-dependent builds).
- Original game files are never modified; mod paks only override assets in memory.

## Install
- Framework-dependent build (requires .NET 10 runtime). Download one zip, extract, run `SoD2MapViewer.exe`.
- `SoD2MapViewer-framework-dependent.zip`: single-file build.
- `SoD2MapViewer-framework-dependent-plain.zip`: plain (unpacked) build — try this if the single-file build is flagged by AV/Defender.

## Checksums (SHA256)
- `SoD2MapViewer-framework-dependent.zip`: `21ACA2EB776E1082320B935F96C2E4CD83F354B4AC810F5CA9BBC85BE3AF9EE9`
- `SoD2MapViewer-framework-dependent-plain.zip`: `8B393D9B2387DD97F1B24740366D83AB73923C46CEEE8670FC099EA13714EDBC`
- `SoD2MapViewer.exe` (single-file): `B40E562DFBFE3D698670B381E01034A8F240035B92CC77326B3E4460F26BEE6F`
- `SoD2MapViewer.exe` (plain): `593151D7742452BC2512C98D68D021EA1D6E3A5FD70F69A19AD538E34ADD20C7`
