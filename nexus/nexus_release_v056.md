# SoD2MapV v0.5.6

## Highlights
- **Tile moves now load in-game (chunk patch export):** Map/tile edits are exported as a patch of the owning content chunk (e.g. Faustite `pakchunk004600-WindowsNoEditor_P.pak` in `<game>\StateOfDecay2\Content\Paks`) so SoD2 actually loads the modified tiles. This fixes the case where moved buildings never appeared in-game.
- **Height (Z) adjustment:** New **"Adjust height (Z)…"** action applies a meter delta to the current selection / house group (with Ctrl+Z undo). Use it after X/Y moves to correct floating/sinking on different terrain.
- **Player marker height:** "Show character" now displays the player's Z height on the map marker (in addition to the status bar).
- **Session restore:** The app remembers and restores the last game folder, opened map, filters, and object selection between runs.

## Notes
- For tile/map chunk patches, copy the exported `pakchunk<chunkId>-WindowsNoEditor_P.pak` to `<game>\StateOfDecay2\Content\Paks` (not `Saved\Paks`) and fully restart the game. Remove the file to revert.
- House/group moves: verify the exact edited tile in-game; after relocating, adjust Z as needed so buildings sit on the ground.
- Build: 0 warnings / 0 errors.
