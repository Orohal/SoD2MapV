# SoD2MapV — Wiki

**SoD2MapV** is a native Windows map viewer and editor for *State of Decay 2* (Unreal Engine 4). It reads all 888 game maps from the game's PAK archives without an AES key, displays them top-down, lets you edit objects, and exports your changes as a custom `.pak` mod. It also injects vehicles and items into live savegames.

- **GitHub:** https://github.com/Orohal/SoD2MapV
- **Latest release:** [v0.6.3](https://github.com/Orohal/SoD2MapV/releases/latest)
- **License:** MIT

## Pages

| Page | Content |
|---|---|
| [Installation](Installation) | Requirements, download, build from source |
| [GUI](GUI) | Viewer, editor, maps, background map, sites overlay |
| [Modding Workflow](Modding-Workflow) | Moving houses/sites, exporting `.pak` mods, claimable sites |
| [CLI](CLI) | All headless command-line modes |
| [Troubleshooting](Troubleshooting) | AV false positives, common issues, hashes |

## Quick start

1. Download the latest release from [Releases](https://github.com/Orohal/SoD2MapV/releases) (framework-dependent; self-contained only on request).
2. Extract the zip and run `SoD2MapViewer.exe`.
3. Click **Choose game folder** and select your State of Decay 2 installation directory (e.g. `D:\Games\State of Decay 2\StateOfDecay2`).
4. Pick a map from the list on the left. The map renders top-down.
5. Enable editing, drag objects, then **Export mod .pak…** and copy the resulting `pakchunk…_P.pak` into `StateOfDecay2\Content\Paks\`.

## What the app can do

- **Browse all 888 maps** from all 35 PAKs (custom LZ4 linked-blocks decoder).
- **2D top-down viewer** with grid, search filter, legend, zoom/pan.
- **Full editor:** move, rotate, scale, delete objects; undo; multi-select.
- **House groups:** right-click a house (or pick from the **House:** dropdown) to select the prefab plus all objects within 40 m; drag the whole group.
- **Site/outpost moving:** click a tgc site box or its name label to select all contained objects; drag moves the group; the box follows live; export auto-writes the tgc patch. The claimable-site runtime data (`Tagged/Cooked/*_Tags.uasset`) is patched automatically so doors, claim terminals and containers follow the building.
- **Height adjustment (Z):** raise/lower a selection in meters (fixes floating/sinking).
- **Vehicle & item injection** into live savegames (with backup and self-check).
- **Vector background map** from in-game `MapBackgroundData` assets, offline, all 5 regions, auto-anchored to the tile offset; manual fine-tune via drag or `bgcalib_override.json`.
- **tgc site/outpostable overlay:** claimable buildings drawn at their real positions, clickable boxes and names.
- **Session restore:** last game folder, map, filter and selection are remembered.
- **Headless CLI** for parsing, diagnostics, savegame work, rendering.
