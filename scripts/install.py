#!/usr/bin/env python3
from __future__ import annotations

import os
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RIMWORLD_DIR = Path(os.environ.get("RIMWORLD_DIR", "/media/scarf/@steam/SteamLibrary/steamapps/common/RimWorld"))
DEST = RIMWORLD_DIR / "Mods" / "Auto-Facing-Chairs"
RUNTIME_DIRS = ("About", "Assemblies", "Languages", "Patches")

if DEST.name != "Auto-Facing-Chairs":
    raise SystemExit(f"Refusing unsafe destination: {DEST}")

if DEST.exists():
    shutil.rmtree(DEST)
DEST.mkdir(parents=True)

for name in RUNTIME_DIRS:
    source = ROOT / name
    if source.exists():
        shutil.copytree(source, DEST / name, dirs_exist_ok=True)

print(DEST)
