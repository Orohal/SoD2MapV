# SoD2MapV v0.6.0

## Highlights
- **Site selection & move by click/name:** the orange/blue tgc site boxes are now **clickable** - a click inside a box selects all objects of that site (including owner-linked Outpost-/Homesite-Interactables); dragging anywhere in the box then relocates the whole group (Undo via button/Ctrl+Z). The box follows the drag **live** (visual).
- **"Site:" dropdown** lists the tile's sites (e.g. `OUT Autoshop (84D09511)`) and selects by name (view jumps to the site).
- On export the tgc patch (`pakchunk001095`) shifts site boxes/markers/entities by the same delta, so the claim position follows the building.
- New **Undo** button in the toolbar.

## Notes
- Requires the Sites overlay ("Sites (tgc)") for the clickable boxes.
- Full claimability in-game still depends on the game's site registration (save/site GUIDs); in-game validation of the moved outpost is pending.
- Build: 0 errors.

## Downloads (GitHub release)
- `SoD2MapViewer-framework-dependent.zip` - small single-file EXE (requires .NET 10 Desktop Runtime), SHA256 `A53532ADCF669B08D3BF3435A28CDAEAAFFCAC0A7C61F885686DEEEA192A4C19`
- `SoD2MapViewer-framework-dependent-plain.zip` - unpacked EXE + DLLs (same function, use if a virus scanner flags the packed apphost), SHA256 `1869D0F2DBF0EF51571245D7F340E9CDA50E92E6718841DA73580F9CB863B05C`
- Single-file EXE inside framework-dependent.zip: SHA256 `C58115D7A83E4CBD1388708968463D16AD86C3AAB53D3E0117CF7BAAF9F0F772`
- Unpacked EXE inside plain.zip: SHA256 `D2D8D54F0EFB55A11B940F996BAFD011E72C983E2F07C5D0F5E4A0D7AEAAC406`
