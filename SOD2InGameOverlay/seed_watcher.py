#!/usr/bin/env python3
"""Seed watcher — every 60 s, read the newest SoD2 save via SoD2MapViewer
(--playerpos) and write the player position to C:\\Users\\Public\\sod2_seed.txt,
but ONLY if it changed. The in-game DLL watches the file timestamp and re-scans.

Using the proven C# MapViewer avoids reimplementing the GVAS property tree.
"""
import subprocess
import time
import re
from pathlib import Path

SAVE_ROOT = Path.home() / "AppData" / "Local" / "StateOfDecay2" / "Saved" / "Steam"
SEED_FILE = Path("C:/Users/Public/sod2_seed.txt")
VIEWER = Path("D:/tmp/Stateofmap/app/bin/Release/net10.0-windows/win-x64/SoD2MapViewer.exe")
INTERVAL = 180   # char rotates ~every 10 min; 3 min is plenty and avoids rescan storms


def newest_save() -> Path | None:
    if not SAVE_ROOT.exists(): return None
    saves = [p for p in SAVE_ROOT.rglob("SaveGame_*.sav") if "\\Release\\" in str(p)]
    return max(saves, key=lambda p: p.stat().st_mtime) if saves else None


def read_player_pos(sav: Path):
    if not VIEWER.exists(): return None
    out = subprocess.run([str(VIEWER), "--playerpos", str(sav)],
                         capture_output=True, text=True, timeout=30).stdout
    m = re.search(r"save=([-\d.]+),([-\d.]+),([-\d.]+)", out)
    return tuple(float(x) for x in m.groups()) if m else None


def write_seed(pos):
    txt = f"{pos[0]:.2f} {pos[1]:.2f} {pos[2]:.2f}"
    old = SEED_FILE.read_text(encoding="ascii").strip() if SEED_FILE.exists() else ""
    if old != txt:
        SEED_FILE.write_text(txt, encoding="ascii")
        print(f"[seed_watcher] updated: {txt}")
    else:
        print(f"[seed_watcher] unchanged: {txt}")


def main():
    print(f"[seed_watcher] watching {SAVE_ROOT}, interval {INTERVAL}s")
    while True:
        try:
            sav = newest_save()
            if sav:
                pos = read_player_pos(sav)
                if pos:
                    write_seed(pos)
                else:
                    print(f"[seed_watcher] no position in {sav.name}")
            else:
                print("[seed_watcher] no save found")
        except Exception as e:
            print(f"[seed_watcher] error: {e}")
        time.sleep(INTERVAL)


if __name__ == "__main__":
    main()
