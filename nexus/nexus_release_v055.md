# SoD2MapV v0.5.5

## Highlights
- **Move complete houses (GUI) — *in testing, feedback welcome*:** Right-click a house (or pick one in the new **"House:"** dropdown) to select the house prefab plus every object within 40 m as one group — drag together, undo in one step, export as one mod `.pak`. Multi-select via Ctrl+Click and Shift+drag box; Esc clears. The view auto-fits to the selected group.
- **Export fix:** `UmapPatcher` now searches the pristine map bytes by the object's original (pre-edit) coordinates, so moved objects (single or grouped) are patched correctly instead of failing with "location pattern not found". Export continues per-object and reports un-patched objects instead of aborting.
- **Build cleanup:** 0 warnings (nullable/obsolete/API-audit fixes).
- **Mod folder corrected:** Steam/Epic mods go to `%LocalAppData%\StateOfDecay2\Saved\Paks` (not `Content\Paks`); export dialog and README updated.

## Notes
- **House-moving is new and still being tested** — please report any issues (missing objects in the group, failed exports, in-game visibility).
- In-game appearance requires copying the exported `_P.pak` into `Saved\Paks` and restarting the game. Test the exact map/region you edited (the mod patches the specific map tile).
- Injected weapons/items still inherit state from the cloned template instance.