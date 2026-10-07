"""
Export Titanfall 2 characters from the user's own install into PilotHeim runtime files.

Writes, into the local data folder (never into the repository):
  <name>.phm2   skinned mesh in Unity space (metres, Y up, left-handed), bind poses
  <name>.pha    baked animations: per frame, per bone local position + rotation (Unity space)
Source -> Unity: u = 0.0254 * (-y, z, x). That mirrors handedness, so rotations are
conjugated by the axis matrix and triangle winding is flipped.

Usage:
  python tf2_export.py bt  <titan_buddy.mdl> <anim.mdl>[,<anim.mdl>...] <outdir>
  python tf2_export.py mesh <model.mdl> <outdir> <name> [bodypart=model,...]
"""
import math
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import anim53  # noqa: E402
import mdl53   # noqa: E402

S = 0.0254
M = np.array([[0, -1, 0], [0, 0, 1], [1, 0, 0]], np.float64)   # Source -> Unity axes

# Titan clips used by the mod (state -> Titanfall sequence)
BT_CLIPS = {
    "idle": "@bt_combat_idle",
    "walk_f": "@bt_combat_walk_forward", "walk_b": "@bt_combat_walk_backward",
    "walk_l": "@bt_combat_walk_left", "walk_r": "@bt_combat_walk_right",
    "run_f": "@bt_combat_run_forward", "run_b": "@bt_combat_run_backward",
    "run_l": "@bt_combat_run_left", "run_r": "@bt_combat_run_right",
    "sprint_f": "@bt_combat_sprint_forward_noaim",
    "dash_f": "@bt_dash_forward_01", "dash_b": "@bt_dash_backward_01",
    "dash_l": "@bt_dash_left", "dash_r": "@bt_dash_right",
    "fire": "@bt_fire_stand", "reload": "@bt_stand_reload_01",
    "stumble": "@bt_combat_walk_stumble_01",
}
LOOPING = {"idle", "walk_f", "walk_b", "walk_l", "walk_r", "run_f", "run_b", "run_l", "run_r", "sprint_f"}
MAX_IDLE_FRAMES = 300     # combat idle is 1001 frames; 10 s is plenty and keeps the file small


def qmat(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def matq(R):
    t = R[0, 0] + R[1, 1] + R[2, 2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        return ((R[2, 1] - R[1, 2]) / s, (R[0, 2] - R[2, 0]) / s, (R[1, 0] - R[0, 1]) / s, 0.25 * s)
    if R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
        s = math.sqrt(1.0 + R[0, 0] - R[1, 1] - R[2, 2]) * 2
        return (0.25 * s, (R[0, 1] + R[1, 0]) / s, (R[0, 2] + R[2, 0]) / s, (R[2, 1] - R[1, 2]) / s)
    if R[1, 1] > R[2, 2]:
        s = math.sqrt(1.0 + R[1, 1] - R[0, 0] - R[2, 2]) * 2
        return ((R[0, 1] + R[1, 0]) / s, 0.25 * s, (R[1, 2] + R[2, 1]) / s, (R[0, 2] - R[2, 0]) / s)
    s = math.sqrt(1.0 + R[2, 2] - R[0, 0] - R[1, 1]) * 2
    return ((R[0, 2] + R[2, 0]) / s, (R[1, 2] + R[2, 1]) / s, 0.25 * s, (R[1, 0] - R[0, 1]) / s)


def to_unity_tr(pos, quat):
    """Local transform (Source) -> (Unity position, Unity quaternion)."""
    R = M @ qmat(quat) @ M.T
    return list(S * (M @ np.array(pos, np.float64))), matq(R)


def to_unity_matrix(m34):
    """3x4 row-major Source matrix -> 4x4 Unity matrix (column-vector convention)."""
    T = np.eye(4)
    T[:3, :] = np.array(m34, np.float64).reshape(3, 4)
    C = np.eye(4); C[:3, :3] = S * M
    Ci = np.eye(4); Ci[:3, :3] = M.T / S
    return C @ T @ Ci


class Out:
    def __init__(self, magic):
        self.b = bytearray(magic)

    def s(self, txt):
        e = txt.encode("utf-8"); self.b += struct.pack("<H", len(e)) + e

    def i(self, *v): self.b += struct.pack("<%di" % len(v), *v)
    def f(self, *v): self.b += struct.pack("<%df" % len(v), *v)

    def save(self, path):
        with open(path, "wb") as fh:
            fh.write(self.b)


def write_mesh(m, submeshes, path, rest_pose=None):
    """PHM2: bones (name, parent, rest local pos/rot, bindpose 4x4) + submeshes."""
    o = Out(b"PHM2")
    o.s(m["name"])
    o.i(len(m["bones"]))
    for k, bone in enumerate(m["bones"]):
        o.s(bone["name"]); o.i(bone["parent"])
        p, q = rest_pose[k] if rest_pose else (bone["pos"], bone["quat"])
        up, uq = to_unity_tr(p, q)
        o.f(*up); o.f(*uq)
        bp = to_unity_matrix(bone["poseToBone"])
        o.f(*[bp[r, c] for c in range(4) for r in range(4)])       # column-major like Unity
    o.i(len(submeshes))
    for mat, sm in sorted(submeshes.items()):
        o.s(m["textures"][mat] if 0 <= mat < len(m["textures"]) else f"material_{mat}")
        o.i(len(sm["verts"]), len(sm["tris"]) * 3)
        for (w, bones, nb, pos, nrm, uv) in sm["verts"]:
            up = S * (M @ np.array(pos)); un = M @ np.array(nrm)
            o.f(*up, *un, uv[0], 1.0 - uv[1])
            ws = [w[i] if i < nb else 0.0 for i in range(3)]
            bs = [bones[i] if i < nb else 0 for i in range(3)]
            o.b += struct.pack("<3B", *bs); o.f(*ws)
        for a, b2, c in sm["tris"]:
            o.i(a, c, b2)                                            # mirrored -> flip winding
    o.save(path)


def write_anims(m, clips, path, fallback=None):
    """PHA1: bone count, then clips (name, fps, loop, speed u/s, frames x bones x (pos3, rot4))."""
    o = Out(b"PHA1")
    o.i(len(m["bones"]))
    o.i(len(clips))
    for key, a in clips:
        n = a.numframes if key != "idle" else min(a.numframes, MAX_IDLE_FRAMES)
        poses = [a.pose(f, m["bones"], fallback) for f in range(n)]
        # clip speed from the jx_c_start motion tracker (body stays in place)
        speed = 0.0
        tracker = next((i for i, b in enumerate(m["bones"]) if b["name"] == "jx_c_start"), -1)
        if tracker >= 0 and n > 2:
            p0, p1 = np.array(poses[0][tracker][0]), np.array(poses[n - 2][tracker][0])
            speed = float(np.linalg.norm(p1 - p0) / ((n - 2) / a.fps))
        o.s(key); o.f(a.fps); o.i(1 if key in LOOPING else 0); o.f(speed); o.i(n)
        for pose in poses:
            for (p, q, sc) in pose:
                up, uq = to_unity_tr(p, q)
                o.f(*up, *uq)
        print(f"  clip {key:9s} <- {a.name:36s} frames {n:4d} fps {a.fps:.0f} speed {speed:6.1f} u/s")
    o.save(path)


def export_bt(model_path, anim_paths, outdir):
    m = mdl53.read_mdl(model_path)
    subs, _ = mdl53.build(m, {})
    anims = {}
    for ap in anim_paths:
        _, descs = anim53.read_anims(ap)
        for d in descs:
            anims.setdefault(d.name, d)
    clips = [(k, anims[v]) for k, v in BT_CLIPS.items() if v in anims]
    missing = [v for v in BT_CLIPS.values() if v not in anims]
    if missing:
        print("missing clips:", missing)
    # rest = the animated reference pose (the bind skeleton is exploded at the hands)
    ref = anims.get("@ref")
    rest = [(p, q) for (p, q, _) in ref.pose(0, m["bones"])] if ref else None
    os.makedirs(outdir, exist_ok=True)
    write_mesh(m, subs, os.path.join(outdir, "bt.phm2"), rest)
    write_anims(m, clips, os.path.join(outdir, "bt.pha"), rest)
    print("BT:", len(m["bones"]), "bones,", sum(len(s["verts"]) for s in subs.values()), "verts,",
          sum(len(s["tris"]) for s in subs.values()), "tris,", len(subs), "materials")


MAYA_TO_SOURCE = (0.5, 0.5, 0.5, 0.5)       # root rotation every Titanfall @ref applies (Y-up -> Z-up)


def export_mesh(model_path, outdir, name, choose, yup_root=False):
    m = mdl53.read_mdl(model_path)
    subs, _ = mdl53.build(m, choose)
    rest = None
    if yup_root:
        # character models are authored Y-up; their animations rotate the root into Source Z-up
        rest = [(b["pos"], b["quat"]) for b in m["bones"]]
        rest[0] = (rest[0][0], MAYA_TO_SOURCE)
    os.makedirs(outdir, exist_ok=True)
    write_mesh(m, subs, os.path.join(outdir, name + ".phm2"), rest)
    print(name, ":", len(m["bones"]), "bones,", sum(len(s["verts"]) for s in subs.values()), "verts")


if __name__ == "__main__":
    if sys.argv[1] == "bt":
        export_bt(sys.argv[2], sys.argv[3].split(","), sys.argv[4])
    elif sys.argv[1] == "mesh":
        ch = {}
        for kv in (sys.argv[5].split(",") if len(sys.argv) > 5 and "=" in sys.argv[5] else []):
            k, v = kv.split("="); ch[k] = int(v)
        export_mesh(sys.argv[2], sys.argv[3], sys.argv[4], ch, yup_root="--yup" in sys.argv)
