# Installation

## Requirements

- Windows 10/11
- [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (framework-dependent builds) — or use the self-contained build on request
- A working installation of *State of Decay 2* (Steam or Epic)

## Download (recommended)

1. Go to [Releases](https://github.com/Orohal/SoD2MapV/releases).
2. Download one of:
   - `SoD2MapViewer-framework-dependent.zip` — single-file build (smaller download, one EXE).
   - `SoD2MapViewer-framework-dependent-plain.zip` — unpacked build (use this if your antivirus flags the single-file EXE).
3. Extract into a folder of your choice.
4. Run `SoD2MapViewer.exe`.

> **Virus warnings:** unsigned .NET single-file apphosts are sometimes flagged by generic "packer" heuristics. Windows Defender reports no threat. If your AV complains, use the `plain` variant or add an exclusion. Hashes are published in the release notes and the README.

## Build from source

```sh
git clone https://github.com/Orohal/SoD2MapV.git
cd SoD2MapV
dotnet build app/SoD2MapViewer.csproj -c Release
# output: app/bin/Release/net10.0-windows/SoD2MapViewer.dll  (run with dotnet)
# or publish a single-file EXE:
dotnet publish app/SoD2MapViewer.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o release/fw
```

## First run

1. Start the app.
2. Click **Choose game folder** and select your State of Decay 2 installation directory (the folder containing `StateOfDecay2\Content\Paks`).
3. The app mounts all PAKs and lists 890 maps grouped by region.
4. Select a map — it renders top-down.

## Configuration / data locations

| Path | Purpose |
|---|---|
| `%LOCALAPPDATA%\SoD2MapViewer\session.json` | Last game folder, map, filter, selection |
| `%LOCALAPPDATA%\SoD2MapViewer\calibration.json` | Per-save vehicle injection calibration |
| `%LOCALAPPDATA%\SoD2MapViewer\bgcalib_override.json` | Background-map fine-tune offsets |
| `%LOCALAPPDATA%\StateOfDecay2\Saved\Paks\` | Mod `.pak` target for savegame mods (not tile mods) |
| `<game>\StateOfDecay2\Content\Paks\` | Target for tile/map chunk patches (`pakchunk…_P.pak`) |
