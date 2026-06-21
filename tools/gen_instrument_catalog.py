#!/usr/bin/env python3
"""Regenerate data/instruments/README.md - the catalog index of the bundled instrument DB.

One row per <model>.json. Run from the repo root:  python tools/gen_instrument_catalog.py
Keeps the catalog honest as models are added/split (see issue #41); previously hand-edited,
which let counts go stale.
"""
import json, glob, os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
DB = os.path.join(ROOT, "data", "instruments")
OUT = os.path.join(DB, "README.md")

rows, total_cmds = [], 0
for f in sorted(glob.glob(os.path.join(DB, "*.json"))):
    d = json.load(open(f, encoding="utf-8"))
    cmds = len(d.get("commands") or [])
    total_cmds += cmds
    ident = d.get("identity") or {}
    cmd = ident.get("command")
    identity = "`" + cmd + "`" if cmd else "(none)"
    rows.append((d.get("manufacturer") or "?", d.get("model") or "?",
                 d.get("category") or "?", cmds, identity))

# Sort by manufacturer, then model.
rows.sort(key=lambda r: (r[0].lower(), r[1].lower()))

lines = [
    "# Instrument database catalog",
    "",
    "Auto-generated index of the bundled instrument command database.",
    f"**{len(rows)} models, {total_cmds} documented commands.** Each is one `<model>.json` file in this folder.",
    "",
    "Regenerate with `python tools/gen_instrument_catalog.py` after adding or editing definitions.",
    "",
    "All entries were extracted from vendor programming/operating manuals; only documented",
    "commands are included. `(none)` under Identity means the instrument has no identification",
    "query (typically listen-only legacy HP-IB gear).",
    "",
    "| Model | Manufacturer | Category | Commands | Identity |",
    "|-------|--------------|----------|---------:|----------|",
]
lines += [f"| {m} | {mfr} | {cat} | {n} | {idy} |" for mfr, m, cat, n, idy in rows]
lines.append("")

open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(lines))
print(f"Wrote {OUT}: {len(rows)} models, {total_cmds} commands")
