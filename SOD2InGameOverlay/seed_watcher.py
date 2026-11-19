#!/usr/bin/env python3
"""Seed-Watcher: liest alle INTERVAL Sekunden (default 180) die neueste SoD2-
Savegame-Position ueber SoD2MapViewer --playerpos und schreibt sie nach
SOD2_SEED_FILE (default C:/Users/Public/sod2_seed.txt) - nur wenn sie sich
geaendert hat.

WICHTIG: Das Overlay scannt NUR AUF ANFRAGE ("Scan jetzt"). Dieser Watcher
loest keinen Re-Scan aus; er haelt nur die Seed-Datei aktuell. Siehe README.

Alle Pfade/Intervalle sind ueber Umgebungsvariablen konfigurierbar:
  SOD2_SAVE_ROOT, SOD2_SEED_FILE, SOD2_VIEWER_EXE, SOD2_WATCH_INTERVAL
"""
import os
import re
import subprocess
import time
from pathlib import Path

_SAVE_ROOT = Path(os.environ.get(
    "SOD2_SAVE_ROOT",
    str(Path.home() / "AppData" / "Local" / "StateOfDecay2" / "Saved" / "Steam")))
SEED_FILE = Path(os.environ.get("SOD2_SEED_FILE", "C:/Users/Public/sod2_seed.txt"))
INTERVAL = int(os.environ.get("SOD2_WATCH_INTERVAL", "180"))


def _default_viewer() -> Path:
    base = Path(__file__).resolve().parents[2] / "app" / "bin" / "Release" / "net10.0-windows"
    for candidate in (base / "win-x64" / "SoD2MapViewer.exe", base / "SoD2MapViewer.exe"):
        if candidate.exists():
            return candidate
    return base / "win-x64" / "SoD2MapViewer.exe"   # klarer Fehler im read_player_pos


VIEWER = Path(os.environ.get("SOD2_VIEWER_EXE", str(_default_viewer())))


def newest_save() -> Path | None:
    if not _SAVE_ROOT.exists():
        return None
    saves = [p for p in _SAVE_ROOT.rglob("SaveGame_*.sav") if "\\Release\\" in str(p)]
    return max(saves, key=lambda p: p.stat().st_mtime) if saves else None


def read_player_pos(sav: Path):
    if not VIEWER.exists():
        print(f"[seed_watcher] Viewer nicht gefunden: {VIEWER}")
        return None
    out = subprocess.run([str(VIEWER), "--playerpos", str(sav)],
                         capture_output=True, text=True, timeout=30).stdout
    m = re.search(r"save=([-\d.]+),([-\d.]+),([-\d.]+)", out)
    return tuple(float(x) for x in m.groups()) if m else None


def write_seed(pos):
    txt = f"{pos[0]:.2f} {pos[1]:.2f} {pos[2]:.2f}"
    old = SEED_FILE.read_text(encoding="ascii").strip() if SEED_FILE.exists() else ""
    if old == txt:
        print(f"[seed_watcher] unveraendert: {txt}")
        return
    # atomar: temp im gleichen Verzeichnis, dann os.replace
    tmp = SEED_FILE.with_name(SEED_FILE.name + ".tmp")
    tmp.write_text(txt, encoding="ascii")
    os.replace(tmp, SEED_FILE)
    print(f"[seed_watcher] aktualisiert: {txt}")


def main():
    print(f"[seed_watcher] beobachte {_SAVE_ROOT}, Intervall {INTERVAL}s, Ziel {SEED_FILE}")
    while True:
        try:
            sav = newest_save()
            if sav:
                pos = read_player_pos(sav)
                if pos:
                    write_seed(pos)
                else:
                    print(f"[seed_watcher] keine Position in {sav.name}")
            else:
                print("[seed_watcher] kein Save gefunden")
        except Exception as e:
            print(f"[seed_watcher] Fehler: {e}")
        time.sleep(INTERVAL)


if __name__ == "__main__":
    main()
