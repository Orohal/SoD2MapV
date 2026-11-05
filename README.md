# SoD2MapV — State of Decay 2 Map Viewer

Ein nativer Windows-Map-Viewer für **State of Decay 2** (Unreal Engine 4), gebaut mit C# / WinForms und [CUE4Parse](https://github.com/FabianFG/CUE4Parse).

> **Hinweis:** Diese App ist die **Vorarbeit für einen Map-Editor**. Aktuelle Phase ist ein reiner **Viewer** (nur Lesen). Geplante Phasen:
>
> 1. ✅ **Viewer** — PAKs lesen, Maps listen, 2D-Top-Down-Ansicht mit Objekten
> 2. ⏳ **Positions-Editor MVP** — Objektverschiebung in der Karte
> 3. ⏳ **Voller Editor** — weiteres Bearbeiten von Map-Inhalten
> 4. ⏳ **Mod-Repacking** — geänderte Maps als eigene `.pak` verpacken

## Features

- Mountet **alle 35 `.pak`-Archive** des Spiels — **ohne AES-Key** (die PAKs sind nicht verschlüsselt)
- Findet und listet **888 `.umap`-Karten** aus den gemounteten Archiven
- Parst die gekochten Karten und extrahiert Actor-Positionen (über `RootComponent` → `Transform`)
- 2D-Top-Down-Ansicht der Karte mit Objekten (WinForms, native GUI)
- Headless-Modi für Tests und Diagnose (siehe unten)

## Kompatibilitäts-Fix: LZ4 „Linked Blocks"

State of Decay 2 komprimiert seine Pak-Einträge mit LZ4 in einer **verlinkten Block-Variante**: Rückverweisen innerhalb eines 64-KB-Blocks dürfen in die unkomprimierte Ausgabe des *Vorgängerblocks* zeigen. CUE4Parse dekodiert jeden Block isoliert und schlägt bei solchen Einträgen fehl (betroffen waren u. a. alle großen Weltkarten wie Trumbull Valley).

Diese App bringt deshalb einen eigenen Decoder mit (`Lz4Linked.cs`), der alle Blöcke sequenziell mit einem rollierenden 64-KB-Dictionary dekodiert. Damit parsen **alle 888 Karten fehlerfrei** (`--smoke-all: ok=888 failed=0`).

## Voraussetzungen

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows)
- Eine State of Decay 2-Installation (Pfad wird in der GUI gewählt bzw. per CLI übergeben)

## Build

```sh
dotnet build app/SoD2MapViewer.csproj -c Release
```

## Nutzung

### GUI (Viewer)

Einfach starten und über **Open** das Spielverzeichnis wählen (z. B. `D:\Games\State of Decay 2\StateOfDecay2`):

```sh
dotnet app/bin/Release/net10.0-windows/SoD2MapViewer.dll
```

### Headless-Modi

```sh
# Alle 888 Karten parsen und Erfolg/Fehler zusammenfassen
SoD2MapViewer.exe --smoke-all "D:\Games\State of Decay 2\StateOfDecay2"

# Die ersten Karten parsen und Objekte mit Positionen ausgeben
SoD2MapViewer.exe --smoke "D:\Games\State of Decay 2\StateOfDecay2"

# Low-Level-Diagnose eines einzelnen Pak-Eintrags (Codec-Tests pro Block)
SoD2MapViewer.exe --diag "D:\Games\State of Decay 2\StateOfDecay2" "StateOfDecay2/Content/maps/trumbull_valley/trumbull_valley/trumbull_valley_x0_y0.umap"
```

## Technik

| Baustein | Umsetzung |
|---|---|
| GUI | C# / WinForms (`net10.0-windows`) |
| Pak-Mounting & Asset-Parsing | [CUE4Parse](https://github.com/FabianFG/CUE4Parse) (`EGame.GAME_StateOfDecay2`) |
| LZ4 Linked Blocks | eigener sequenzieller Decoder (`app/Lz4Linked.cs`) |
| Karten-Objekte | Actors via `RootComponent` → `Transform` (Fallback: `RelativeLocation`/`RelativeRotation`/`RelativeScale3D`) |

## Repo-Status

- Vorheriger Stand: `v0.1` (Viewer-Grundlage, 481/888 Karten)
- Aktueller Stand: **alle 888 Karten lesbar** (LZ4-Linked-Blocks-Fix)

## Lizenz

MIT — siehe [LICENSE](LICENSE). Alle Markenrechte an *State of Decay 2* liegen bei Undead Labs / Microsoft.
