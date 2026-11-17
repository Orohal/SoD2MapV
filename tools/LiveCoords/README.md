# LiveCoords — eigenständiges Overlay-Projekt

Ziel: **live Spielerkoordinaten im Spiel** anzeigen (für Mapper/Modder), als eigenständiges Tool neben dem Map Viewer.

## Status
- [x] Projektgerüst (WinForms, net10.0-windows, transparentes Always-on-Top-Overlay)
- [ ] REA: Spielerposition im Prozess `StateOfDecay2-Win64-Shipping.exe` finden
- [ ] Memory-Reader (stabiler Offset/Pattern, Schedule alle ~250 ms)
- [ ] Anzeige X/Y/Z in Metern + Himmelsrichtung (+X = Nord, +Y = Osten)
- [ ] Optional: Kopieren der Koordinaten per Klick

## Build
```sh
dotnet build tools/LiveCoords/LiveCoords.csproj -c Release
```

## Bedienung (aktuell)
Overlay erscheint oben links, immer im Vordergrund. Rechtsklick → **Beenden**.
Liefert derzeit nur Platzhalter `--,-- m` — die Position kommt, sobald die REA-Analyse abgeschlossen ist.

## Hinweis
Nur für Single-Player/Modding. Kein Netzwerkzugriff, keine Veränderung von Game-Dateien.
