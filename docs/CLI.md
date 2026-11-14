# CLI (headless modes)

All functions are available from the command line. The GUI is optional.

```sh
SoD2MapViewer.exe <mode> [args...]
```

## Parsing / diagnostics

| Command | Description |
|---|---|
| `--smoke-all <gameDir>` | Parse all 888 maps, print ok/failed summary. |
| `--smoke <gameDir>` | Parse the first maps, print objects with positions. |
| `--diag <gameDir> <umapPath>` | Low-level codec diagnostics per block. |
| `--umapinfo <gameDir> <file.umap> [filter] [hexOff] [hexLen]` | Pak structure of a `.umap` (summary, imports, exports, name-map sidecar). |
| `--umapdecode <gameDir> <file.umap> <exportIndex>` | Decode tagged properties of an export byte-exactly. |
| `--genmap <out.umap>` | Generate a synthetic 200×200 m test map. |
| `--testmap <file.umap> [gameDir]` | Validate a test map end-to-end (and install it as `pakchunk997…_P.pak` if gameDir given). |

## Maps / assets

| Command | Description |
|---|---|
| `--bgrender <region> <out.png>` | Render the vector background map to PNG. |
| `--bgprops <asset>` | Print asset properties. |
| `--bgfit <region>` | Auto-calibrate background against tgc site boxes. |
| `--bgset <region> <ox> <oy> <scale>` | Set manual background transform. |
| `--maptex LIST\|DUMP\|PROPS\|JSON` | Pak exploration helpers. |

## Savegames

| Command | Description |
|---|---|
| `--saveprops <file.gvas> [entryIndex]` | Parse a decompressed save block; dump top-level props or a single entry. |
| `--saveinject <save.sav> <out.sav> <classIndex> <x> <y> <z> [yaw] [templateIndex] [setGuid]` | Inject a vehicle (clone + patch transform). |
| `--savedelete <save.sav> <out.sav> <indexSpec>` | Delete vehicle entries by index/range. |
| `--saveitemlist <save.sav> [filter]` | List item classes in the save's ItemLibrary. |
| `--saveitems <save.sav> <out.sav> <survivorId> <itemType> <classPath1> <stack1> […]` | Inject item stacks into a survivor's backpack. |
| `--saves` | List live savegames (newest first). |

## Site diagnostics

| Command | Description |
|---|---|
| `--sites <gameDir> <mapPath>` | List the region tag database and the sites that own entities in the given tile. |
| `--tgcpatch <gameDir> <mapPath> <prefabName> <dx> <dy> <dz> <outPak>` | Apply a tgc patch with the given delta (low-level). |

Coordinates are in UE centimeters; yaw in degrees. See the README for full argument details.
