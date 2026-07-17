#!/usr/bin/env python3
"""Velo output tool: keep only the error-ish lines of the last command.

Install: copy into %APPDATA%\\velo\\tools\\ then run from the command
palette (Tools... > errors.py).
"""
import re
import sys

PAT = re.compile(r"error|fail|exception|warning|traceback|panic", re.I)

for line in sys.stdin:
    if PAT.search(line):
        sys.stdout.write(line)
