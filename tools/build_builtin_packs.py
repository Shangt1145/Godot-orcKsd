# -*- coding: utf-8 -*-
"""Refresh official card assets through the complete OrC parser and native compiler.

Keeps existing native assets; compiles new mechanics through the same pipeline used
by the game. Missing mechanics are recorded for implementation in the JSON report.
"""
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
raise SystemExit(subprocess.call([
    "dotnet", "run", "--project", str(root / "tools/OfficialCards"), "--",
    str(root), "--refresh", "--write", *sys.argv[1:]
]))
