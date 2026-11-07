# SoD2MapV — State of Decay 2 Map Viewer und Editor

Ein nativer Windows-Map-Viewer für **State of Decay 2** (Unreal Engine 4), gebaut mit C# / WinForms und [CUE4Parse](https://github.com/FabianFG/CUE4Parse).

> **Hinweis:** Diese App ist die **Vorarbeit für einen Map-Editor**. Geplante Phasen:
>
> 1. ✅ **Viewer** — PAKs lesen, Maps listen, 2D-Top-Down-Ansicht mit Objekten
> 2. ✅ **Positions-Editor MVP** — Objekte verschieben (Drag & Drop, Undo) + Export als Mod-`.pak`
> 3. ✅ **Voller Editor** — Rotation, Scale, Delete (Markieren + Null-Überschreiben) + Undo
> 4. ◐ **Mod-Repacking** — eigener SoD2-`.pak`-Writer ist verifiziert; Feinschliff folgt
> 5. ⏳ **Aufhören, schreiend im Kreis zu laufen, weil hier noch 1000 Fehler sind!**

## Features

- Mountet **alle 35 `.pak`-Archive** des Spiels — **ohne AES-Key** (die PAKs sind nicht verschlüsselt)
- Findet und listet **888 `.umap`-Karten** aus den gemounteten Archiven
- Parst die gekochten Karten und extrahiert Actor-Positionen (über `RootComponent` → `Transform`)
- 2D-Top-Down-Ansicht der Karte mit Objekten (WinForms, native GUI)
- Headless-Modi für Tests und Diagnose (siehe unten)
- **Editor (Phase 3):** Objekte verschieben (Move), rotieren (Rotate), skalieren (Scale) und löschen (Delete) per Drag & Drop, alle Änderungen per Undo rückgängig, Export als Mod-`.pak` mit allen Modifikationen

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

### GUI (Viewer / Editor MVP)

Einfach starten und über **Choose game folder** das Spielverzeichnis wählen (z. B. `D:\Games\State of Decay 2\StateOfDecay2`):

```sh
dotnet app/bin/Release/net10.0-windows/SoD2MapViewer.dll
```

**Editor MVP:** Checkbox *Enable editing* aktivieren → Objekt auf der Karte greifen und bearbeiten:

| Taste | Aktion |
|-------|--------|
| Drag | Objekt verschieben (Move-Modus) |
| `M` | Modus: Move (Position) |
| `R` | Modus: Rotate (Yaw-Drehung, gelber Pfeil) |
| `S` | Modus: Scale (uniform, cyaner Rahmen) |
| `Entf` | Objekt als gelöscht markieren (rote Durchstreichung) |
| `Ctrl+Z` | Undo letzte Änderung (Pos / Rot / Scale / Delete) |
| `F` | View auf alle Objekte zoomen (Fit) |
| Doppelklick | Zoomen auf Cursor-Position |
| Rechtsklick | Kontextmenü (Location kopieren, Modus wechseln, Delete) |

**Export mod .pak…** schreibt alle Änderungen (Position, Rotation, Scale, Delete) als eigene `.pak`. Gelöschte Objekte haben ihre Komponentendaten mit Nullen überschrieben. Die `.pak` in `StateOfDecay2\Content\Paks\` kopieren — das `_P`-Suffix sorgt dafür, dass das Spiel dem Mod-Pak den Vorzug gibt.

### Headless-Modi

```sh
# Alle 888 Karten parsen und Erfolg/Fehler zusammenfassen
SoD2MapViewer.exe --smoke-all "D:\Games\State of Decay 2\StateOfDecay2"

# Die ersten Karten parsen und Objekte mit Positionen ausgeben
SoD2MapViewer.exe --smoke "D:\Games\State of Decay 2\StateOfDecay2"

# Low-Level-Diagnose eines einzelnen Pak-Eintrags (Codec-Tests pro Block)
SoD2MapViewer.exe --diag "D:\Games\State of Decay 2\StateOfDecay2" "StateOfDecay2/Content/maps/trumbull_valley/trumbull_valley/trumbull_valley_x0_y0.umap"

# Pak-Struktur eines .umap-Files analysieren (Summary, Imports, Exports + Name-Map-Sidecar <file>.names.txt)
SoD2MapViewer.exe --umapinfo "D:\Games\State of Decay 2\StateOfDecay2" <file.umap> [exportFilter] [hexOff] [hexLen]

# Tagged Properties eines Exports byte-exakt dekodieren (FName-Auflösung, Tag-Offsets, Trailer-Bytes)
SoD2MapViewer.exe --umapdecode "D:\Games\State of Decay 2\StateOfDecay2" <file.umap> <exportIndex0based>

# Synthetische 200x200-m-Testmap erzeugen: flacher Boden (WaterPlane_01, Scale 20) + 9 Grid-Marker
# mit bekannten Positionen/Yaws/Scales — nur für Viewer/Editor-Tests, nicht für das Spiel bestimmt
SoD2MapViewer.exe --genmap <out.umap>

# Testmap end-to-end validieren (Pak bauen → ParseMap → Transform-Check gegen Sollwerte);
# mit gameDir wird sie zusätzlich als pakchunk997-WindowsNoEditor_P.pak installiert (Datei löschen zum Revert)
SoD2MapViewer.exe --testmap <file.umap> ["D:\Games\State of Decay 2\StateOfDecay2"]

# Dekomprimierten Savegame-Block parsen: Top-Level-Props, VehicleSaveCollection (Klassen-Liste,
# alle Fahrzeuge mit Klasse/Position/GUID); optional Props eines Eintrags rekursiv dumpen
SoD2MapViewer.exe --saveprops <file.gvas> [entryIndex]

# Fahrzeug in die VehicleSaveCollection injizieren: klont einen bestehenden Eintrag und patcht
# VehicleClassIndex + Transform (Rotation aus Yaw, Translation). Schreibt <out>, Eingabe bleibt
# unverändert; Original-Footer wird kopiert, Größen/Counts aller Ahnen werden korrigiert.
# Koordinaten in UE-cm (x/y/z), yaw in Grad, templateIndex = zu klonender Eintrag (Standard 0)
SoD2MapViewer.exe --saveinject <save.sav> <out.sav> <classIndex> <x> <y> <z> [yaw] [templateIndex]
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
- Aktueller Stand: **alle 888 Karten lesbar** (LZ4-Linked-Blocks-Fix), **Phase 3 (Voller Editor) abgeschlossen** (Rotation, Scale, Delete, Undo, Export)
- Release `v0.3.2` auf GitHub (selbstständige Windows-EXE, .NET 10 Runtime inklusive)
- Testmap-Generator (`--genmap`/`--testmap`/`--umapsum`): generiert flache 200×200-m-Testkarte (`sod2_testmap`) als Pak `pakchunk997-WindowsNoEditor_P.pak` — Viewer-Test ohne Spielinhalte
- Bugfixes: Komponenten-Transforms waren um einen Export verschoben zugeordnet (`FPackageIndex` ist 1-based) — jeder Actor zeigte die Position der Komponente seines **Nachbars**; `QuatFromRotator` folgte nicht der UE-Konvention (`FRotator::Quaternion()`). Beide gefixt und per Testmap + Referenz-Koordinaten verifiziert.
- GUI: Objekt-Grid zeigt X/Y/Z jetzt in Metern (vorher rohe UE-Zentimeter)
- Savegame-Injektion (`--saveprops`/`--saveinject`): VehicleSaveCollection wird rekursiv gefunden (nestet unter `MapSaveCollection` → `MapSaves` → …); `--saveinject` klont einen bestehenden Fahrzeugintrag, patcht Klasse/Position/Yaw und korrigiert Count/Größen aller Ahnen inkl. der validierten `hsize`-Felder der Struct-Arrays. 4× Military Truck (Klasse 5) auf P1–P4 in `SaveGame_Vanilla_1.sav` injiziert und verifiziert (28 Einträge, alle 13 Props intakt)

## Lizenz

MIT — siehe [LICENSE](LICENSE). Alle Markenrechte an *State of Decay 2* liegen bei Undead Labs / Microsoft.
<br>
<a href="https://www.buymeacoffee.com/Orohal" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;" ></a>
