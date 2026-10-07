"""
Render a .phm (PilotHeim mesh) to PNG with a tiny z-buffered rasterizer, to check
converted Titanfall models without a 3D package. Optional texture-free shading only.
Usage: python phm_preview.py model.phm out.png [--yaw 30] [--size 768] [--zup]
"""
import argparse
import math
import struct

import numpy as np
from PIL import Image


def read_phm(path):
    b = open(path, "rb").read()
    assert b[:4] == b"PHM1", "not a PHM1 file"
    o = 4
    def s():
        nonlocal o
        n = struct.unpack_from("<H", b, o)[0]; o += 2
        t = b[o:o + n].decode("utf-8"); o += n
        return t
    name = s()
    nb = struct.unpack_from("<i", b, o)[0]; o += 4
    bones = []
    for _ in range(nb):
        bn = s(); parent = struct.unpack_from("<i", b, o)[0]; o += 4
        m = struct.unpack_from("<12f", b, o); o += 48
        bones.append((bn, parent, m))
    ns = struct.unpack_from("<i", b, o)[0]; o += 4
    subs = []
    vsize = 32 + 3 + 12
    for _ in range(ns):
        mat = s()
        nv, ni = struct.unpack_from("<ii", b, o); o += 8
        pos = np.zeros((nv, 3), np.float32); nrm = np.zeros((nv, 3), np.float32)
        for i in range(nv):
            v = struct.unpack_from("<3f3f2f", b, o)
            pos[i] = v[0:3]; nrm[i] = v[3:6]
            o += vsize
        idx = np.frombuffer(b, np.uint32, ni, o).reshape(-1, 3).copy(); o += ni * 4
        subs.append((mat, pos, nrm, idx))
    return name, bones, subs


def render(subs, out, yaw=30.0, size=768, zup=False):
    allp = np.concatenate([p for _, p, _, _ in subs])
    if zup:
        conv = lambda p: p[:, [0, 2, 1]] * np.array([1, 1, -1], np.float32)
    else:
        conv = lambda p: p
    allp = conv(allp)
    c = (allp.min(0) + allp.max(0)) / 2
    ext = (allp.max(0) - allp.min(0)).max() * 0.55
    a = math.radians(yaw)
    R = np.array([[math.cos(a), 0, math.sin(a)], [0, 1, 0], [-math.sin(a), 0, math.cos(a)]], np.float32)
    zbuf = np.full((size, size), np.inf, np.float32)
    img = np.zeros((size, size, 3), np.float32) + np.array([0.12, 0.13, 0.15], np.float32)
    light = np.array([0.4, 0.7, -0.6], np.float32); light /= np.linalg.norm(light)
    palette = [(0.75, 0.55, 0.35), (0.45, 0.6, 0.75), (0.6, 0.7, 0.45), (0.8, 0.8, 0.8), (0.7, 0.45, 0.6),
               (0.5, 0.5, 0.35), (0.35, 0.6, 0.6), (0.85, 0.65, 0.3), (0.55, 0.55, 0.7), (0.65, 0.4, 0.35)]
    for si, (_, pos, nrm, idx) in enumerate(subs):
        p = (conv(pos) - c) @ R.T
        sx = (p[:, 0] / ext * 0.5 + 0.5) * size
        sy = (0.5 - p[:, 1] / ext * 0.5) * size
        sz = p[:, 2]
        col = np.array(palette[si % len(palette)], np.float32)
        for t in idx:
            x = sx[t]; y = sy[t]; z = sz[t]
            e1 = p[t[1]] - p[t[0]]; e2 = p[t[2]] - p[t[0]]
            n = np.cross(e1, e2); ln = np.linalg.norm(n)
            if ln < 1e-12:
                continue
            shade = 0.35 + 0.65 * abs(float(np.dot(n / ln, light)))
            x0, x1 = max(int(x.min()), 0), min(int(x.max()) + 1, size - 1)
            y0, y1 = max(int(y.min()), 0), min(int(y.max()) + 1, size - 1)
            if x0 > x1 or y0 > y1:
                continue
            gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
            d = (y[1] - y[2]) * (x[0] - x[2]) + (x[2] - x[1]) * (y[0] - y[2])
            if abs(d) < 1e-9:
                continue
            w0 = ((y[1] - y[2]) * (gx - x[2]) + (x[2] - x[1]) * (gy - y[2])) / d
            w1 = ((y[2] - y[0]) * (gx - x[2]) + (x[0] - x[2]) * (gy - y[2])) / d
            w2 = 1 - w0 - w1
            inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
            if not inside.any():
                continue
            zz = w0 * z[0] + w1 * z[1] + w2 * z[2]
            sub = zbuf[y0:y1 + 1, x0:x1 + 1]
            upd = inside & (zz < sub)
            sub[upd] = zz[upd]
            img[y0:y1 + 1, x0:x1 + 1][upd] = col * shade
    Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).save(out)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("phm"); ap.add_argument("png")
    ap.add_argument("--yaw", type=float, default=30.0)
    ap.add_argument("--size", type=int, default=768)
    ap.add_argument("--zup", action="store_true")
    a = ap.parse_args()
    _, bones, subs = read_phm(a.phm)
    print(f"bones {len(bones)}, submeshes {len(subs)}, verts {sum(len(s[1]) for s in subs)}")
    render(subs, a.png, a.yaw, a.size, a.zup)
