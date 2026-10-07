"""Build native_defaults.json from the Ghidra reports produced by tools/ghidra.

Titanfall 2 keeps the defaults for player settings and movement ConVars inside
server.dll rather than in the .set files. tools/ghidra/*.java dump them from
YOUR install into reports; this script turns those reports into one JSON file
that PilotHeim reads at runtime. The output stays in your local data folder and
is never committed.

usage: python build_native_defaults.py <reportsDir> <outJson>
"""
import json
import pathlib
import sys


def parse_settings(path):
    out = {}
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if line.startswith("#") or line.startswith("convar"):
            continue
        parts = line.split("\t")
        if len(parts) >= 2 and parts[1] not in ("null", ""):
            out.setdefault(parts[0], parts[1])
    return out


def parse_convars(path):
    convars, stance = {}, {}
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if line.startswith("#"):
            continue
        parts = line.split("\t")
        if parts[0] == "setting":
            if len(parts) >= 3 and parts[2] not in ("null", ""):
                stance.setdefault(parts[1], parts[2])
            continue
        if len(parts) >= 2 and parts[1] not in ("None", "null"):
            convars.setdefault(parts[0], parts[1].rstrip("f").rstrip(".") if parts[1].endswith(".f") else parts[1].rstrip("f"))
    return convars, stance


def main():
    reports = pathlib.Path(sys.argv[1])
    out = pathlib.Path(sys.argv[2])
    settings = parse_settings(reports / "settings_table.txt")
    convars, stance = parse_convars(reports / "convars_server.txt")
    doc = {
        "source": "server.dll (Titanfall 2) via Ghidra; generated locally",
        "settings": settings,
        "stance": stance,
        "convars": convars,
    }
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(doc, indent=1, sort_keys=True), encoding="utf-8")
    print(f"settings={len(settings)} stance={len(stance)} convars={len(convars)} -> {out}")


if __name__ == "__main__":
    main()
