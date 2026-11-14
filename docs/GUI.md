# GUI

## Main window

- **Map list (left):** all 888 maps grouped by region (Trumbull Valley, Cascade Hills, Drucker County, Meagher Valley, Providence Ridge, Other). Filter box expands matching groups.
- **Canvas (center):** top-down view of the selected map's objects.
- **Toolbar / menus:** editing toggle, modes, undo, export, injection, background map, sites overlay.
- **Status bar:** current operation, counts, calibration info.

## Navigation

| Action | Effect |
|---|---|
| Drag (empty canvas) | Pan |
| Mouse wheel | Zoom |
| Double-click | Zoom to cursor |
| `F` | Fit all objects into view |

## Editing

Enable with the **Enable editing** checkbox. Then:

| Key | Action |
|---|---|
| Drag | Move selected object |
| `M` / `R` / `S` | Mode: Move / Rotate / Scale |
| `Delete` | Mark object as deleted |
| `Ctrl+Z` | Undo |
| Right-click | Context menu (copy location, mode, delete) |

Multi-select: `Ctrl`+click or drag-box (`Shift`+drag).

## Object properties

Objects are parsed from the cooked UE4 map (actors via `RootComponent` → `Transform`, with fallback to `RelativeLocation`/`RelativeRotation`/`RelativeScale3D`). Each has position, rotation (quaternion) and scale in UE centimeters.

## House groups

Right-click a house (or pick from the **House:** dropdown) to select the house prefab plus every object within 40 m. The whole group drags together; undo and export work on the group. The selection auto-fits the view.

## Height adjustment (Z)

**Adjust height (Z)…** applies a meter delta to the current selection / house group (with undo). Use it after an X/Y move to correct floating or sinking on different terrain.

## Background map (vector)

Toolbar **"Hintergrundkarte (Spiel-Asset)"** overlays the in-game vector map built from `MapBackgroundData` assets (roads, rivers, lakes, buildings, border) in the real in-game colors. Fully offline, all 5 regions, auto-anchored to the tile/world offset.

- Checkbox **Sites (tgc)** draws the claimable site/outpostable boxes from the region tgc at their real positions.
- Checkbox **Feinjustierung (Drag)** lets you drag the map to fine-tune alignment; releasing commits the calibration (ox/oy shown).
- CLI: `--bgrender`, `--bgprops`, `--bgfit`, `--bgset`.

## tgc site / outpostable overlay

Claimable buildings and site boxes from the region `.tgc` are drawn at their real positions. The boxes are **clickable** — clicking a box (or its name label) selects all objects of that site. The **Site:** dropdown lists the sites of the current tile (`OUT Autoshop (84D09511)`), sorted alphabetically with object counts, and jumps the view to the site.

**Region-wide site selection:** clicking a **region group node** in the map list (e.g. "Trumbull Valley (37)") lists **all sites of the region** across all tiles, labelled with their tile (`OUT Autoshop (84D09511)  @ faustite_x2_y3`). Selecting a site from another tile loads that tile automatically, selects the site and centres the view.

## Vehicle injection

Toolbar **Inject vehicle…** (or right-click on the map → position). Calibrate once per save (probe vehicle → measure offset → enter East/North in meters). Then inject real vehicles at the chosen spot. Requires the game to be closed; automatic backup under `Backups\`. Calibration persists in `%LOCALAPPDATA%\SoD2MapViewer\calibration.json`.

**Inject at player (v0.5.3+):** click **Show character** first (reads the last saved player position from `PlayerBreadcrumbs`), then **Inject at player**. The dialog is pre-filled with player pos/height (+10 m offset). Pick a free area so the vehicle doesn't spawn inside an object.

## Item injection

Menu **Inject ▾ → Inject item…**: choose a save (backup created, game must be closed, self-check after), pick a survivor (default: last played), then set items. Presets are derived automatically from the chosen save. Each item needs a free backpack slot. CLI equivalents: `--saveitems`, `--saveitemlist`.

## Session restore

The app remembers the last game folder, opened map, filter and selection (`%LOCALAPPDATA%\SoD2MapViewer\session.json`) and restores them on startup.
