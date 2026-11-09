# SoD2MapV — State of Decay 2 Map Viewer und Editor

Nativer Windows-Map-Viewer/-Editor für **State of Decay 2** (UE4) — C# / WinForms + [CUE4Parse](https://github.com/FabianFG/CUE4Parse). Liest alle Spielmappen ohne AES-Key, zeigt sie top-down, editiert Objekte und injiziert Fahrzeuge in Live-Savegames.

<a href="https://www.buymeacoffee.com/Orohal" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;" ></a>

**Aktueller Stand: [v0.5.3](https://github.com/Orohal/SoD2MapV/releases/latest)** — Viewer + Voll-Editor + Fahrzeug-Injektion (GUI/CLI) + **eigene Vektor-Basemap** (offline, aus den Ingame-`MapBackgroundData`-Assets, alle 5 Regionen). Details: [Repo-Status](#repo-status) (eingeklappt).

## Features

- **888 Karten** aus allen 35 PAKs, fehlerfrei lesbar (eigener LZ4-Linked-Blocks-Decoder)
- **2D-Top-Down-Viewer**: Objekte mit Positionen; Grid, Suchfilter, Legende
- **Voller Editor**: verschieben / rotieren / skalieren / löschen per Drag & Drop, Undo, Export als Mod-`.pak`
- **Echte Karte als Hintergrund**: (a) **Vektor-Basemap** aus den Ingame-`MapBackgroundData`-Assets (Straßen/Flüsse/Seen/Gebäude/Border), offline, alle 5 Regionen, per Drag kalibrierbar; (b) öffentliche WeMod/Wand-Kacheln, pixelgenau in Weltkoordinaten projiziert
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

Toolbar **„Vector map (asset)"** — blendet die eigene Vektor-Karte aus den Ingame-`MapBackgroundData`-Assets (`StateOfDecay2/Content/maps/Backgrounds/*`) ein: Straßen, Flüsse, Seen, Gebäude und die Karten-Grenze, in den echten Ingame-Farben (`MapUIInfo_BP`). Komplett offline, für alle 5 Regionen. Bei Abweichung: Checkbox **„Edit base-map pos"** einschalten und die Karte per Drag ausrichten (Loslassen speichert die Kalibrierung). CLI: `--bgrender` (PNG-Render), `--bgset` (manuelle Transform-Werte).

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
| Vektor-Basemap | Ingame-`MapBackgroundData`-Assets (Straßen/Flüsse/Seen/Gebäude/Border), gerendert in `MapUIInfo_BP`-Farben (`app/BgMaps.cs`) — komplett offline |

</details>

<details>
<summary><b>Repo-Status / Versionshistorie</b></summary>

- **v0.5.3** — **Spielerposition & vereinfachte Injektion**: Button „Show character“ zeigt die letzte Spielerposition aus dem Save (`PlayerBreadcrumbs`, mit Kalibrierung). „Inject at player“ füllt Pos/Höhe (+10 m Offset) vor und gibt Hinweis auf freie Fläche; Zahlenfelder akzeptieren `,`/`.` und leere Werte als `0`. **Fandom-Map verschiebbar** (nur zur groben Orientierung): „Edit base-map pos“ bei aktiver Fandom-/inaktiver Vektor-Map, Versatz gespeichert. **Vektor-Basemap-Fixes**: `faustite` nutzt `faustite_persistent_Background` (+180°) mit variantenspezifischer Kalibrierung; Kalibrierung asset-variantenfähig, Vektor standardmäßig an/über Fandom. CLI: `--bgrenderasset`.
- **v0.5.2** — **Map-Liste nach Regionen gruppiert**: unter „filter maps“ als ein-/ausklappbare Baumansicht (Trumbull Valley, Cascade Hills, Drucker County, Meagher Valley, Providence Ridge, Other); Filter expandiert Treffer-Gruppen.
- **v0.5.1** — **WeMod/Wand-Kachel-Map entfernt** (CDN-Abhängigkeit): Kachel-Layer, Tile-Projektion, `--wandcheck`/`--wandrender`/`--fancalib` sind weg; Regions-Konfigurationen und die Vektor-Basemap bleiben. Rebuild der Release-Artefakte.
- **v0.5.0** — **Eigene Vektor-Basemap (Option 2)**: rendert die Ingame-`MapBackgroundData`-Assets (`maps/Backgrounds/*_Background`: Road/River/Lakes/Building/Border-Buffer) direkt zu einer offline-Karte in den echten Ingame-Farben (`MapUIInfo_BP`) — für alle 5 Regionen. Widget↔Welt-Transform per Drag kalibrierbar („Edit base-map pos"), dazu CLI `--bgrender` / `--bgset`. Neue Tooling-Modi: `--maptex LIST/DUMP/PROPS/JSON` (Pak-Exploration). Fix: Trumbull-Kartenpfad (`maps/trumbull_valley/`) in der Regionserkennung. **Entfernt:** WeMod/Wand-Kachel-Hintergrund (CDN-Abhängigkeit) samt `--wandcheck`/`--wandrender`/`--fancalib`. Abhängigkeiten: `CUE4Parse-Conversion` 1.2.2.202611, `SixLabors.ImageSharp` 3.1.12. **Hinweis:** die Vektor-Map ersetzt die (ungenau kalibrierte) Fandom-Map als verlässliche Basis.
- **v0.4.2** — **Hintergrund für alle 5 Regionen** (faustite→Trumbull Valley, amethyst→Cascade Hills, basalt→Drucker County, copper→Meagher Valley, danburite→Providence Ridge — Zuordnung verifiziert über Sublevel-Namen + Render-Checks); Region wird automatisch aus dem Kartenpfad gewählt. Kachel-Zeichnung: grobe Ebene zuerst, Zoom klemmt auf die verlässlichen CDN-Ebenen z4/z5 (z1–z3 der WeMod-CDN sind lückenhaft), fehlende Kacheln werden aus der Elternebene hochskaliert gefüllt. Fix: Kachel-Cache mischte vorher Dateien verschiedener Regionen (Cache-Dateinamen sind jetzt pro Region/GUID — war Ursache des „Flecken/Löcher-Mosaiks"). CLI: `--wandrender`, `--tileinfo` (WorldTileInfo-Offsets je Region). **⚠ Rohbau-Hinweis:** die WeMod-CDN-Pyramide ist derzeit serverseitig unvollständig (Kacheln verschwinden/werden nachgeladen) — Löcher/Flecken im Hintergrund sind bis auf weiteres möglich; an der Anbindung wird weitergearbeitet (Community-Basemap als Fallback geplant)
- **v0.4.1** — **Wand-Hintergrund auto-ausgerichtet**: Kachel-Offset kommt aus dem UMAP-Header (`WorldTileInfo.Position`, pro Karte); keine manuelle Kalibrierung mehr (`--wandrender` ohne Offsets testbar)
- **v0.4.0** — Echte Karte als Hintergrund (Wand-Tiles, pixelgenaue Projektion, `--wandcheck`); Fenstertitel zeigt Paketversion; ungepackte Variante gegen Viren-FP
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

**Published Hashes (SHA256) — v0.5.3:**

| Asset | SHA256 |
|---|---|
| `SoD2MapViewer.exe` (self-contained, ~126 MB) | `C8DF7AEFF26A7446E1C373EF94B7B5838D33463486F8302C260BC575CD57A69D` |
| `SoD2MapViewer.exe` (single-file, in framework-dependent.zip) | `626C20712EEDCD73FE2E1819FB375F47B28340FE54EB9F947FBFAC7188149804` |
| `SoD2MapViewer-framework-dependent.zip` | `D1082551C638F883CD6BF9ADEFFBD734FD533E598DCB5EB1670373D54D2DD63D` |
| `SoD2MapViewer-framework-dependent-plain.zip` | `8D3F38E03CF91AC8325DBEC13363EC9B9264A01D9DFD98EC9C20242BF846BB1A` |
| `SoD2MapViewer.exe` (ungepackt, in plain.zip) | `59A198BD83642742903D2DF1BEAFA187EA38883D0773ACA1767FC1DFAEDE12B0` |

Historisch: die framework-dependent Single-File-EXE von **v0.4.0** (`9FE1E0954E269916B67EB5937D45A0874A51CBEB964C0DD369382542FFDD6C2A`) wurde von 9 VirusTotal-Engines generisch gemeldet (Packer-Heuristik der Single-File-Apphosts).

- Selbst bauen: `dotnet build app/SoD2MapViewer.csproj -c Release`
- False-Positive melden (alle Engines): [Microsoft WDSI-Filesubmission](https://www.microsoft.com/wdsi/filesubmission); bei anderen Engines jeweils deren FP-Portal nutzen

## Lizenz

MIT — siehe [LICENSE](LICENSE). Alle Markenrechte an *State of Decay 2* liegen bei Undead Labs / Microsoft.
