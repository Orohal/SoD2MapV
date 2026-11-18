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

## Nutzung

1. SoD2 starten und eine Community laden (das Spiel muss laufen)
2. Seed-Datei anlegen: `C:\Users\Public\sod2_seed.txt` mit den Save-Koordinaten als `X Y Z` in cm.
   Example: `-57352.55 33225.02 20941.89`
   - Die Koordinaten come aus dem letzten Save: `SoD2MapViewer.exe --playerpos <save.sav>`
   - Ohne Kalibrierungsdatei gilt `world = save` (direkt übernehmen).
3. `Sod2Injector.exe` ausführen (injiziert `SOD2Overlay.dll` per `LoadLibraryW`)
4. ~40 s warten (einmaliger RAM-Scan im Hintergrund)
5. Im Spiel erscheint oben links das Fenster "SOD2 - Koordinaten" mit den gefundenen Koordinaten.

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
- **Koordinaten-Ermittlung:** Die statische Pointer-Kette aus dem Report greift zur Laufzeit nicht:
  die PE hat manipulierte Section-Header und die RTTI ist entfernt (`0xCCCCCCCC`). Stattdessen wird
  **einmalig** der Prozessspeicher nach einem Float-Triple nahe dem Save-Seed durchsucht
  (Cluster-Heuristik: dynamischer Heap, Referenzzahl, runde Werte bestrafen).
  Der gefundene Wert wird als statische Messung angezeigt — **kein Live-Tracking beim Laufen**.
- Die DLL wird mit `CreateRemoteThread(LoadLibraryW)` geladen; die Originaldateien des Spiels bleiben unberührt.
- SoD2 direkt starten (Shipping-EXE), nicht über den Loader (`StateOfDecay2.exe`) — der startet elevated.
- Der externe Vorläufer `tools/LiveCoords` nutzte denselben Seed-Scan per `ReadProcessMemory`.
