#!/usr/bin/env python3
from __future__ import annotations

import os
import re
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

PACKAGE_ID = "scarf.chairsnap"
CONFIG = Path(
    os.environ.get(
        "RIMWORLD_MODS_CONFIG",
        "/home/scarf/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml",
    )
)

running = subprocess.run(
    ["pgrep", "-f", "(^|/)RimWorldLinux( |$)"],
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
    check=False,
).returncode == 0
if running:
    raise SystemExit("RimWorld is running; refusing to edit ModsConfig.xml.")

text = CONFIG.read_text(encoding="utf-8")
root = ET.fromstring(text)
active_mods = root.find("activeMods")
if active_mods is None:
    raise SystemExit("ModsConfig.xml has no <activeMods> element.")

if any((item.text or "").lower() == PACKAGE_ID for item in active_mods.findall("li")):
    raise SystemExit(0)

match = re.search(r"(?P<indent>^[ \t]*)</activeMods>", text, flags=re.MULTILINE)
if match is None:
    raise SystemExit("Could not locate </activeMods> in ModsConfig.xml.")

closing_indent = match.group("indent")
item_indent = closing_indent + "  "
insertion = f"{item_indent}<li>{PACKAGE_ID}</li>\n"
updated = text[: match.start()] + insertion + text[match.start() :]
ET.fromstring(updated)
CONFIG.write_text(updated, encoding="utf-8")
