# SoD2MapV v0.6.1

## Highlights
- **Site name is clickable:** the drawn site label next to the box (e.g. "Autoshop") now works like the box itself - clicking it selects all objects of that site, then drag in Move mode to relocate.
- **Automatic multi-map detection:** when a moved site shifts its `TaggedSitePrimaryBuildingMarker`, the editor automatically finds and patches the region's site map (`faustite_sites.umap`, `danburite_site.umap`, ...) by matching actor positions (<1 cm) and packs it into the **same pak** as the tile. The manual Multi-Map checkbox + "Finalize Export" workflow is gone (auto-covered).
- **Bugfixes:** tgc yaw was interpreted as radians (6302 deg instead of 110 deg) - site boxes/hit-testing now rotate correctly. Site dropdown is sorted alphabetically, shows the object count, only lists sites that actually contain objects, and can re-select the same site repeatedly.

## Known issue
- In-game the building meshes follow the move, but the runtime site actors (claim terminal, doors, containers) stay at the original position - they are apparently not driven (only) by the compiled tgc. Under investigation (see README to-do).

## Downloads (GitHub release)
- `SoD2MapViewer-framework-dependent.zip` - small single-file EXE (requires .NET 10 Desktop Runtime), SHA256 `4948AB79A02F4BF4AFD7B8617E0EBCCB2F60450BAEDA538C0FBF60A7729F4F26`
- `SoD2MapViewer-framework-dependent-plain.zip` - unpacked EXE + DLLs (same function, use if a virus scanner flags the packed apphost), SHA256 `26F3722D6746EE7951C5FFE2947A225EEC3C62105885A7447DBC1A82B72E8396`
- Single-file EXE inside framework-dependent.zip: SHA256 `A92BE96CD6E03C7508857B7ECE95E8DDFE82E92D84BC40490F291C86984806E7`
- Unpacked EXE inside plain.zip: SHA256 `B94BCFB830B315CAA76C0C2F740B47C6C8F80BAF08CDDADD0C936F6B1781DA58`
