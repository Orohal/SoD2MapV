# SOD2InGameOverlay - In-Game-Koordinaten-Overlay

ImGui-Overlay, das **direkt im DX11-Swapchain von State of Decay 2** gerendert wird.
Funktioniert damit auch im exklusiven Fullscreen (kein externes Fenster, kein Nvidia-Container-Problem).

## Build

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Klonte beim ersten Lauf Kiero, MinHook und ImGui nach `vendor/`, konfiguriert CMake (MinGW ucrt64) und baut:
- `build\SOD2Overlay.dll` - das Overlay
- `build\Sod2Injector.exe` - der Launcher

## Nutzung (On-Demand — scan nur wenn gebraucht)

1. SoD2 starten (Shipping-EXE) und eine Community laden.
2. Seed-Datei aktuell halten: `C:\Users\Public\sod2_seed.txt` mit `X Y Z` in cm.
   - Manuell: `SoD2MapViewer.exe --playerpos <save.sav>` → Werte eintragen, oder
   - Optional `python seed_watcher.py` (schreibt alle 3 Min aus dem neuesten Save).
3. `Sod2Injector.exe` ausführen (injiziert `SOD2Overlay.dll` per `LoadLibraryW`).
4. Im Fenster "SOD2 - Koordinaten" auf **„Scan jetzt"** klicken → ~45 s warten → Koordinaten erscheinen.
   - **Während des Schens im Spiel stehen bleiben!** Der Scan sucht die Position, die zum
     Zeitpunkt des Klicks im Speicher steht. Läuft der Char in dieser Zeit weiter, verschiebt
     sich der Wert und der Treffer ist veraltet (`dist` groß oder gar kein Treffer). Erst wenn
     die neuen Koordinaten im Fenster stehen, weiterlaufen.
   - Der Button zeigt währenddessen „Scan läuft... (~40 s)"; danach erscheinen N/O/H.

**Warum nur auf Anfrage?** SoD2 hat **Online-Co-op** und damit ein Anti-Cheat-/Integritäts-
System, das wiederholtes Auslesen des Prozessspeichers als Manipulation interpretiert. Ein
Dauer-Scan (Watcher + Timestamp-Poll + Re-Scan) führte zu Self-Exits des Spiels (teils mit
`unreal-v0-*.dmp`, teils sauber ohne Dump) und hoher CPU-Last. Der Scan läuft deshalb **nur auf
Knopfdruck** im Hintergrund-Thread — danach ist Ruhe und das Spiel bleibt unangetastet.

Zusätzlich verschleiert der 3-stufige Loader die UE4-Strukturen: statische Offsets
(`UWorld+0x140` = GameInstance) sind im entpackten Dump korrekt, im Live-Prozess aber `null`.
Für echtes Live-Tracking müsste das Spiel vollständig disassembliert und die Laufzeit-Offsets
über eine externe, nicht-intrusive Methode bestimmt werden. Das ist bewusst nicht Teil dieses
Standes.

Optional: `Sod2Injector.exe "C:\pfad\zu\SOD2Overlay.dll"`.

## Bedienung

| Element | Funktion |
|---|---|
| Checkbox **Meter** | Einheit m statt cm |
| Checkbox **Velocity** | Geschwindigkeit anzeigen (derzeit nicht befüllt) |
| `X` (Fenster) | Overlay ausblenden (Re-Injektion zum Zurückbringen) |

## Technik / Hinweise

- **Hook:** Kiero + MinHook auf `IDXGISwapChain::Present` (vtable-Index 8) und `ResizeBuffers` (13).
  ImGui rendert in den Swapchain-Backbuffer (RenderTarget wird vorher gesichert und wiederhergestellt).
- **Koordinaten-Ermittlung:** Die statische Pointer-Kette aus dem Report greift **nur im entpackten
  Dump**, nicht im Live-Prozess. Der 3-stufige Loader baut die UE4-Objekte mit verschobenen Offsets
  auf (`UWorld+0x140` = GameInstance ist statisch korrekt, live aber `null`). Deshalb wird der
  Prozessspeicher nach einem Float-Triple nahe dem Save-Seed durchsucht (Cluster-Heuristik:
  dynamischer Heap, Referenzzahl, runde Werte bestrafen). Rückgabewerte: `dist=0.0` … `~30 cm`.
- **Crash-Schutz:** Der Re-Scan validiert jeden Speicherblock mit `CanRead()` (VirtualQuery),
  bevor er ihn liest. Gespeicherte Hot-Regionen können vom Spiel freigegeben werden — ein
  blinder Zugriff hätte sonst einen Access Violation (Spiel-Absturz) verursacht.
- **Save-Schutz:** Watcher und DLL sind **rein lesend** (`ReadProcessMemory` / Datei-Read).
  Es wird nie in den Save oder den Spielprozess geschrieben. Positionsänderungen im Save
  stammen vom Spiel selbst (Autosave / Char-Rotation beim Ausloggen).
- Die DLL wird mit `CreateRemoteThread(LoadLibraryW)` geladen; die Originaldateien des Spiels bleiben unberührt.
- SoD2 direkt starten (Shipping-EXE), nicht über den Loader (`StateOfDecay2.exe`) — der startet elevated.
- Der externe Vorläufer `tools/LiveCoords` nutzte denselben Seed-Scan per `ReadProcessMemory`,
  kann aber bei laufender Char-Rotation nicht automatisch nachziehen (fixer Seed) — daher der Watcher.

## Bekannte Umgebungs-Probleme (nicht vom Overlay verursacht)

- **SoD2 beendet sich gelegentlich selbst** (auch ohne injizierte DLL): kein Crash-Dump, kein
  Overlay-Log → das Spiel/Steam trennt die Verbindung. Die DLL war dann gar nicht geladen.
  Bei echten Access-Violations gibt es einen `unreal-v0-*.dmp` (~71 MB) in
  `%LOCALAPPDATA%\StateOfDecay2\Saved\Logs` — diese nach Tests aufräumen.
- **Der Loader** `StateOfDecay2.exe` läuft elevated; immer die Shipping-EXE direkt starten.
- **Autosave schreibt ständig**: der Watcher ist deshalb auf 3 Min Interval gedrosselt, und die
  DLL startet einen Re-Scan höchstens alle 120 s — sonst entsteht eine Re-Scan-Flut.
