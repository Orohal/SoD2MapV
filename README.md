# SoD2MapV — State of Decay 2 Map Viewer und Editor

Nativer Windows-Map-Viewer/-Editor für **State of Decay 2** (UE4) — C# / WinForms + [CUE4Parse](https://github.com/FabianFG/CUE4Parse). Liest alle Spielmappen ohne AES-Key, zeigt sie top-down, editiert Objekte und injiziert Fahrzeuge in Live-Savegames.

<a href="https://www.buymeacoffee.com/Orohal" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;" ></a>

**Aktueller Stand: [v0.4.0](https://github.com/Orohal/SoD2MapV/releases/latest)** — Viewer + Voll-Editor + Fahrzeug-Injektion (GUI/CLI) + echte Karte als Hintergrund. Details: [Repo-Status](#repo-status) (eingeklappt).

## Features

- **888 Karten** aus allen 35 PAKs, fehlerfrei lesbar (eigener LZ4-Linked-Blocks-Decoder)
- **2D-Top-Down-Viewer**: Objekte mit Positionen; Grid, Suchfilter, Legende
- **Voller Editor**: verschieben / rotieren / skalieren / löschen per Drag & Drop, Undo, Export als Mod-`.pak`
- **Echte Karte als Hintergrund** (Trumbull Valley): öffentliche WeMod/Wand-Kacheln, pixelgenau in Weltkoordinaten projiziert, kein manuelles Align nötig
- **Fahrzeug-Injektion in Live-Savegames**: Rechtsklick auf der Karte → Position → Fahrzeug in Save schreiben; per-Save-Kalibrierung, Auto-Backup, Spiel-läuft-Check
- Headless-CLI für alle Funktionen (siehe unten)

## Voraussetzungen & Build

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows), SoD2-Installation
- Fertige EXEs: [Releases](https://github.com/Orohal/SoD2MapV/releases) (self-contained + framework-dependent)
- Bauen: `dotnet build app/SoD2MapViewer.csproj -c Release`

## Nutzung (GUI)

Starten → **Choose game folder** → Karte links wählen:

```sh
dotnet app/bin/Release/net10.0-windows/SoD2MapViewer.dll
```

**Editor:** Checkbox *Enable editing* → Objekt greifen und bearbeiten:

| Taste | Aktion |
|-------|--------|
| Drag | Objekt bearbeiten (je nach Modus) |
| `M` / `R` / `S` | Modus: Move / Rotate / Scale |
| `Entf` | Objekt als gelöscht markieren |
| `Ctrl+Z` | Undo |
| `F` | Fit auf alle Objekte |
| Doppelklick | Zoom auf Cursor |
| Rechtsklick | Kontextmenü (Location kopieren, Modus, Delete) |

**Export mod .pak…** schreibt alle Änderungen in eine `.pak` für `StateOfDecay2\Content\Paks\` (das `_P`-Suffix gibt dem Mod den Vorzug).

### Fahrzeug injizieren

Einmalig **Kalibrierung** (pro Save): Toolbar **„Inject vehicle…"** oder **Rechtsklick auf der Karte** → Slot/Klasse/Position prüfen → **Calibrate…** → Sonde ins Spiel, beobachteten Versatz (Ost/Nord in m) im Dialog eingeben → Fertig. Danach injizieren echte Fahrzeuge exakt an der gewählten Stelle.

- Vor jedem Schreiben: Spiel muss geschlossen sein + automatisches Backup unter `Backups\`
- Sonden-Fahrzeuge: „Delete probe vehicle"
- Kalibrierung persistiert: `%LOCALAPPDATA%\SoD2MapViewer\calibration.json`

### Echte Karte als Hintergrund

Toolbar **„Wand map"** — blendet die echte Trumbull-Valley-Karte (faustite) hinter die Objekte ein. Quelle sind die öffentlichen WebP-Kacheln der WeMod/Wand-Interactive-Map (Zoom 1–5), gecached unter `%LOCALAPPDATA%\SoD2MapViewer\wandtiles`. Die Wand-Projektion (gameCoords ↔ lng/lat) transformiert jede Kachel auf das exakte Weltkoordinaten-Rechteck — die Objekte liegen automatisch passend (verifiziert an den faustite-Basen). Nur für faustite-Karten sinnvoll; nur zur Orientierung geladen, kein Re-Hosting.

<details>
<summary><b>Headless-Modi (CLI)</b></summary>

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

# Wand-Karten-Projektion testen: Weltkoordinate (m) -> Tile + Weltrechteck + dekodiertes Tile als PNG
SoD2MapViewer.exe --wandcheck <X_m> <Y_m>

# Synthetische 200x200-m-Testmap erzeugen: flacher Boden (WaterPlane_01, Scale 20) + 9 Grid-Marker
# mit bekannten Positionen/Yaws/Scales — nur für Viewer/Editor-Tests, nicht für das Spiel bestimmt
SoD2MapViewer.exe --genmap <out.umap>

# Testmap end-to-end validieren (Pak bauen → ParseMap → Transform-Check gegen Sollwerte);
# mit gameDir wird sie zusätzlich als pakchunk997-WindowsNoEditor_P.pak installiert (Datei löschen zum Revert)
SoD2MapViewer.exe --testmap <file.umap> ["D:\Games\State of Decay 2\StateOfDecay2"]

# Dekomprimierten Savegame-Block parsen: Top-Level-Props, VehicleSaveCollection; optional Props eines Eintrags dumpen
SoD2MapViewer.exe --saveprops <file.gvas> [entryIndex]

# Fahrzeug in die VehicleSaveCollection injizieren: klont einen bestehenden Eintrag, patcht Klasse/Transform.
# Koordinaten in UE-cm, yaw in Grad, templateIndex = zu klonender Eintrag (-1 = erster lebender), setGuid=1 = frische GUID
SoD2MapViewer.exe --saveinject <save.sav> <out.sav> <classIndex> <x> <y> <z> [yaw] [templateIndex] [setGuid]

# Fahrzeugeinträge per Index löschen (Bereiche "24-56" oder Liste "24,27")
SoD2MapViewer.exe --savedelete <save.sav> <out.sav> <indexSpec>

# Live-Savegames auflisten (%LOCALAPPDATA%\StateOfDecay2, neueste zuerst)
SoD2MapViewer.exe --saves
```

</details>

<details>
<summary><b>Technik</b></summary>

| Baustein | Umsetzung |
|---|---|
| GUI | C# / WinForms (`net10.0-windows`) |
| Pak-Mounting & Asset-Parsing | [CUE4Parse](https://github.com/FabianFG/CUE4Parse) (`EGame.GAME_StateOfDecay2`) |
| LZ4 Linked Blocks | eigener sequenzieller Decoder (`app/Lz4Linked.cs`) — SoD2 nutzt LZ4-„Linked Blocks" (Rückverweise in die unkomprimierte Ausgabe des Vorgängerblocks); CUE4Parse dekodiert isoliert und scheitert daran. Damit: alle 888 Karten lesbar (`--smoke-all: ok=888 failed=0`) |
| Karten-Objekte | Actors via `RootComponent` → `Transform` (Fallback: `RelativeLocation`/`RelativeRotation`/`RelativeScale3D`) |
| Wand-Hintergrund | WebP-Kacheln (ImageSharp) + veröffentlichte Wand-Projektion (`Lz4Linked.cs`-Nachbar `WandTiles.cs`) |

</details>

<details>
<summary><b>Repo-Status / Versionshistorie</b></summary>

- **v0.4.0** — Echte Karte als Hintergrund (Wand-Tiles, pixelgenaue Projektion, `--wandcheck`); Fenstertitel zeigt Paketversion
- **v0.3.5** — Framework-abhängiger Build als Release-Asset; `--saves`; Auto-Template-Injektion
- **v0.3.x** — **GUI-Injektion**: Rechtsklick → Fahrzeug in Live-Save (Kalibrierungs-Assistent, Auto-Backup, Spiel-läuft-Check, Sonden-Löschung); `--savedelete`
- **Voll-Editor (Phase 3)** — Move/Rotate/Scale/Delete mit Undo, Export als Mod-`.pak` (eigener Pak-Writer)
- **Savegame-Kalibrierung** (empirisch per Sonden-Fahrzeugen): `save = world + shift` (pure Translation, pro Save unterschiedlich); Ost=+X, Nord=−Y; Spiel kann schiefe Einträge ~20 m verschieben (Injektionspunkt auf freier Stellfläche wählen)
- **Basis-Lokalisierung**: faustite-Tile-Weltpositionen über `WorldTileInfo.Position` (x2y3: +496/−992 m); Workflow über `--umapinfo`/`--umapdecode` dokumentiert
- **Bugfixes**: Komponenten-Transform-Zuordnung (1-based `FPackageIndex`), `QuatFromRotator` UE-Konvention, `SetProperty`-Parsing (`SiteCollection`, 439 Sites), GUID-Arrays in `--umapdecode`
- Testmap-Generator (`--genmap`/`--testmap`) für Viewer-Tests ohne Spielinhalte

</details>

## Virenwarnung / False Positive

Die gepackten Single-File-EXEs werden von einzelnen VirusTotal-Engines als generisch verdächtig gemeldet — bekanntes Muster für **unsignte** .NET-Apps („Packer"-Heuristik, kein echter Befund). Windows Defender meldet: **keine Bedrohung**.

**Wenn ungünstig:** Ordner-Variante ohne gepackte EXE nutzen — [SoD2MapViewer-framework-dependent-plain.zip](https://github.com/Orohal/SoD2MapV/releases/latest) (kleine EXE + DLLs, gleiche Funktion). Im eigenen Virenprogramm ggf. Ordner ausschließen.

**Published Hashes (SHA256):**

| Asset | SHA256 |
|---|---|
| `SoD2MapViewer.exe` (self-contained) | `5AEA735BE80C3E20F55D3BA55AB6BF9479C22941BA9A362246450FC806B2C803` |
| `SoD2MapViewer.exe` in framework-dependent.zip (single-file) | `9FE1E0954E269916B67EB5937D45A0874A51CBEB964C0DD369382542FFDD6C2A` |
| `SoD2MapViewer-framework-dependent-plain.zip` | `BF6D453E195DE211B4224473DBF448509320B3FC6F83EA8B924CF298571CC6E8` |

- Selbst bauen: `dotnet build app/SoD2MapViewer.csproj -c Release`
- False-Positive melden (alle Engines): [Microsoft WDSI-Filesubmission](https://www.microsoft.com/wdsi/filesubmission); bei anderen Engines jeweils deren FP-Portal nutzen

## Lizenz

MIT — siehe [LICENSE](LICENSE). Alle Markenrechte an *State of Decay 2* liegen bei Undead Labs / Microsoft.
