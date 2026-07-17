#!/usr/bin/env python3
"""Velo output tool: pretty-print the first JSON object/array in the output.

Install: copy into %APPDATA%\\velo\\tools\\ then run from the command
palette (Tools... > json_pretty.py).
"""
import json
import sys

raw = sys.stdin.read()
starts = [i for i in (raw.find("{"), raw.find("[")) if i >= 0]
if not starts:
    sys.exit("no JSON found in the last command's output")
dec = json.JSONDecoder()
try:
    obj, _ = dec.raw_decode(raw[min(starts):])
except ValueError as e:
    sys.exit(f"JSON parse failed: {e}")
print(json.dumps(obj, indent=2, ensure_ascii=False))
