# LiveCoords — eigenständiges Overlay-Projekt

Ziel: **live Spielerkoordinaten im Spiel** anzeigen (für Mapper/Modder), als eigenständiges Tool neben dem Map Viewer.

## Status
- [x] Projektgerüst (WinForms, net10.0-windows, transparentes Always-on-Top-Overlay)
- [x] Memory-Reader (read-only `ReadProcessMemory`, 250 ms Polling)
- [x] Anzeige X/Y/Z in Metern + 16-teilige Himmelsrichtung (+X = Nord, +Y = Osten)
- [x] Headless-Scanner: exakt (`--scan`), toleranzbasiert (`--scannear`), Auto-Cluster (`--auto`)
- [x] Direkter Adress-Modus (`--addr`) für eine laufende Sitzung
- [ ] Pointer-Pfad statt absoluter Adresse (überlebt Spiel-Neustart ohne Rescan)
- [ ] Optional: Kopieren der Koordinaten per Klick

## Bedienung

```sh
# Auto: Player-Cluster anhand der Save-Koordinaten finden und Overlay starten
LiveCoords.exe --auto <pid> <saveX> <saveY> <saveZ> <tolCm>

# Direkt: bekannte Adresse dieser Sitzung anzeigen
LiveCoords.exe --addr 0x... <pid>

# Nur scannen (kein Fenster)
LiveCoords.exe --scannear <pid> <x> <y> <z> <tolCm> [maxHits]
```

Rechtsklick auf das Overlay → **Beenden**. Das Overlay ist `TopMost`; in echtem exklusivem Fullscreen wird es vom Spiel verdeckt (Borderless/„Fullscreen optimiert" funktioniert).

## Build
```sh
dotnet build tools/LiveCoords/LiveCoords.csproj -c Release
```

## Bedienung (aktuell)
Overlay erscheint oben links, immer im Vordergrund. Rechtsklick → **Beenden**.
Liefert derzeit nur Platzhalter `--,-- m` — die Position kommt, sobald die REA-Analyse abgeschlossen ist.

## Hinweis
Nur für Single-Player/Modding. Kein Netzwerkzugriff, keine Veränderung von Game-Dateien.
