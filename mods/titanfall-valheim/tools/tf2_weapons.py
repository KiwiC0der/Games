"""
Export the pilot weapons' world models (the gun Jack holds) from the user's Titanfall 2 install.

For every scripts/weapons/mp_weapon_*.txt with a "playermodel", extract that .mdl from the VPKs,
write <assets>/weapons/<weapon id>.phm2 and copy the materials it uses from a Legion+ material
export into <assets>/materials (then run tf2_textures.py on them).

Usage: python tf2_weapons.py <scripts/weapons> <vpk dir> <tfvpktool dir> <legion materials dir> <assets>
"""
import os
import re
import shutil
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mdl53        # noqa: E402
import tf2_export   # noqa: E402

RX = re.compile(r'"?playermodel"?\s+"([^"]+)"', re.I)


def playermodel(path, weapons_dir, depth=0):
    txt = open(path, encoding="latin1").read()
    m = RX.search(txt)
    if m:
        return m.group(1).replace("\\", "/")
    b = re.search(r'#base\s+"([^"]+)"', txt)
    if b and depth < 4:
        return playermodel(os.path.join(weapons_dir, b.group(1)), weapons_dir, depth + 1)
    return None


def main(weapons_dir, vpk_dir, tool_dir, legion_mats, assets):
    raw = os.path.join(assets, "..", "models_raw")
    want = {}
    for f in sorted(os.listdir(weapons_dir)):
        if f.startswith("mp_weapon_") and f.endswith(".txt"):
            mdl = playermodel(os.path.join(weapons_dir, f), weapons_dir)
            if mdl and mdl.endswith(".mdl"):
                want[f[:-4]] = mdl
    print(f"{len(want)} pilot weapons with world models")
    missing = [m for m in set(want.values()) if not os.path.exists(os.path.join(raw, m))]
    if missing:
        rx = "^(" + "|".join(re.escape(m) for m in missing) + ")$"
        for v in sorted(os.listdir(vpk_dir)):
            if v.endswith("_dir.vpk") and v.startswith("englishclient_"):
                subprocess.run(["node", os.path.join(tool_dir, "extract.js"), os.path.join(vpk_dir, v), raw, rx],
                               capture_output=True)
    out = os.path.join(assets, "weapons")
    os.makedirs(out, exist_ok=True)
    mats_out = os.path.join(assets, "materials")
    done = 0
    for wid, mdl in want.items():
        src = os.path.join(raw, mdl)
        if not os.path.exists(src):
            print(f"  {wid:32s} model not found: {mdl}")
            continue
        try:
            m = mdl53.read_mdl(src)
            subs, _ = mdl53.build(m, {})
            tf2_export.write_mesh(m, subs, os.path.join(out, wid + ".phm2"))
        except SystemExit as e:
            print(f"  {wid:32s} skipped: {e}")
            continue
        for k in subs:
            name = os.path.basename(m["textures"][k].replace("\\", "/")) if 0 <= k < len(m["textures"]) else None
            if name and os.path.isdir(os.path.join(legion_mats, name)) and not os.path.isdir(os.path.join(mats_out, name)):
                shutil.copytree(os.path.join(legion_mats, name), os.path.join(mats_out, name))
        done += 1
        print(f"  {wid:32s} <- {mdl}")
    print(f"exported {done} weapon models")


if __name__ == "__main__":
    main(*sys.argv[1:6])
