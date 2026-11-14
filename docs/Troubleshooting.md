# Troubleshooting

## "Some suspicious files" on Nexus / antivirus warnings

The single-file EXE is an unsigned .NET 10 apphost. Packing single-file EXEs triggers generic "packer" heuristics on several AV engines — a known false positive pattern for unsigned .NET tools. **Windows Defender reports no threat.**

Options:
1. Use the **`plain` (unpacked) variant** (`SoD2MapViewer-framework-dependent-plain.zip`) — same functionality, no packed apphost.
2. Add an exclusion for the app folder in your AV.
3. Verify the published SHA256 hashes (in the release notes and README) match your download.
4. Build from source yourself: `dotnet build app/SoD2MapViewer.csproj -c Release`.

A manual code-review request to Nexus support is tracked in `nexus/support_code_review_email.md`.

## Moved site not claimable in-game

- Make sure you exported with **v0.6.3 or later** (older exports leave doors/terminals/containers behind).
- Check the export dialog: `cooked asset: N position(s) shifted, M not found`. A few `not found` are expected (prefab tile-local coordinates).
- Delete old `pakchunk…_P.pak` files from v0.6.2 in `Content\Paks\` before re-exporting.
- The pak must go into `<game>\StateOfDecay2\Content\Paks\`, **not** `Saved\Paks`.
- Fully restart the game after copying the pak.

## Buildings float or sink after a move

Use **Adjust height (Z)…** to correct the height for the new terrain. Undo is available.

## Map doesn't load / "failed to mount paks"

- Verify the game folder points at the installation root (containing `StateOfDecay2\Content\Paks`).
- Some paks may be encrypted; the app will prompt for an AES key. Paste the 64-hex-digit key or choose Skip for unencrypted installs.

## Vehicle injection: "game must be closed"

The save file is locked while the game runs. Close the game, run the injection, then start the game. A backup is created under `Backups\` automatically.

## Item injection: "not enough free slots"

Each injected item needs one free backpack slot in the target survivor. Free up slots or pick a different survivor.

## Background map misaligned

- Faustite/Trumbull is calibrated; the other four regions may need fine-tuning.
- Enable **Feinjustierung (Drag)** and drag the map; the offset is committed on release.
- Use `--bgfit` for automatic calibration against tgc site boxes, or `--bgset` for manual values.
- Overrides are stored in `%LOCALAPPDATA%\SoD2MapViewer\bgcalib_override.json`.

## Published hashes (v0.6.3)

| Asset | SHA256 |
|---|---|
| `SoD2MapViewer-framework-dependent.zip` | `846c1b932ebfb537168dbdde0865e580a9e72b6f3aa66d55000e25b69bd92709` |
| `SoD2MapViewer-framework-dependent-plain.zip` | `9703c33946ddada7e0e5e1210ddb659d16f9c926031e6b89392c3972a2bd77fa` |
| `SoD2MapViewer.exe` (single-file) | `8bed04a685c5c6f0c10f5df1cc1098d402a839c4d76b95e2881123959753386d` |
| `SoD2MapViewer.exe` (plain) | `d06acdc7ebb52fd43489ff706dd530a140b6d3bc42f3ffbc0710ccd8ec831232` |
