"""
Titanfall 2 model (studiomdl v53) reader and converter for PilotHeim.

Reads a .mdl extracted from the user's own Titanfall 2 VPKs (VTX/VVD are embedded
in v53 models) and writes a compact PilotHeim mesh file (.phm) that the mod loads
at runtime, plus an optional .obj for inspection. No game data is stored in the
repository: inputs and outputs live in the local data folder.

v53 differences from Source 2013 (v49) found by inspection:
  * studiohdr has an extra sznameindex after the checksum (+4 to every later field)
  * bones are 244 bytes (adds scale / scalescale vectors and extra trailing data)
  * VTX (0x1ac), VVD (0x1b0), PHY (0x1b8) offsets with sizes at 0x1bc/0x1c0/0x1c8
Usage:
  python mdl53.py <model.mdl> <out.phm> [--obj out.obj] [--bodygroups 1,0,...]
"""
import argparse
import json
import struct
import sys

PHM_MAGIC = b"PHM1"


class Reader:
    def __init__(self, data):
        self.b = data

    def i32(self, o): return struct.unpack_from("<i", self.b, o)[0]
    def u16(self, o): return struct.unpack_from("<H", self.b, o)[0]
    def i16(self, o): return struct.unpack_from("<h", self.b, o)[0]
    def u8(self, o): return self.b[o]
    def f32(self, o): return struct.unpack_from("<f", self.b, o)[0]
    def fs(self, o, n): return list(struct.unpack_from("<%df" % n, self.b, o))

    def cstr(self, o):
        e = self.b.index(b"\0", o)
        return self.b[o:e].decode("latin1")


# ----------------------------------------------------------------------------- mdl
def read_mdl(path):
    data = open(path, "rb").read()
    r = Reader(data)
    ident, ver = data[:4], r.i32(4)
    if ident != b"IDST" or ver != 53:
        raise SystemExit(f"{path}: not a v53 studio model ({ident!r} v{ver})")
    m = {"name": r.cstr(16), "bones": [], "textures": [], "cdtextures": [], "bodyparts": [], "skins": []}

    # bones (244 bytes in v53)
    nb, bi = r.i32(0xA0), r.i32(0xA4)
    for i in range(nb):
        o = bi + i * 244
        m["bones"].append({
            "name": r.cstr(o + r.i32(o)),
            "parent": r.i32(o + 4),
            "pos": r.fs(o + 32, 3),
            "quat": r.fs(o + 44, 4),
            "rot": r.fs(o + 60, 3),
            "scale": r.fs(o + 72, 3),
            "rotscale": r.fs(o + 96, 3),
            "scalescale": r.fs(o + 108, 3),
            "poseToBone": r.fs(o + 120, 12),       # 3x4 row-major, model space -> bone space
            "flags": r.i32(o + 184),
        })

    # textures (material names) and search paths
    nt, ti = r.i32(0xD0), r.i32(0xD4)
    tsize = (r.i32(0xDC) - ti) // max(1, nt) if nt else 0
    for i in range(nt):
        o = ti + i * tsize
        m["textures"].append(r.cstr(o + r.i32(o)))
    ncd, cdi = r.i32(0xD8), r.i32(0xDC)
    for i in range(ncd):
        m["cdtextures"].append(r.cstr(r.i32(cdi + i * 4)))

    # skin families: numskinfamilies x numskinref shorts
    nref, nfam, si = r.i32(0xE0), r.i32(0xE4), r.i32(0xE8)
    for f in range(nfam):
        m["skins"].append([r.i16(si + (f * nref + k) * 2) for k in range(nref)])

    # bodyparts -> models -> meshes
    nbp, bpi = r.i32(0xEC), r.i32(0xF0)
    for p in range(nbp):
        o = bpi + p * 16
        part = {"name": r.cstr(o + r.i32(o)), "base": r.i32(o + 8), "models": []}
        nm, mi = r.i32(o + 4), r.i32(o + 12)
        for k in range(nm):
            mo = o + mi + k * 148
            mdl = {
                "name": data[mo:mo + 64].split(b"\0")[0].decode("latin1"),
                "numvertices": r.i32(mo + 64 + 16),
                "vertexindex": r.i32(mo + 64 + 20),
                "meshes": [],
            }
            nmesh, meshi = r.i32(mo + 64 + 8), r.i32(mo + 64 + 12)
            for j in range(nmesh):
                so = mo + meshi + j * 116
                mdl["meshes"].append({
                    "material": r.i32(so),
                    "numvertices": r.i32(so + 8),
                    "vertexoffset": r.i32(so + 12),
                })
            part["models"].append(mdl)
        m["bodyparts"].append(part)

    m["vtx"] = (r.i32(0x1AC), r.i32(0x1BC))
    m["vvd"] = (r.i32(0x1B0), r.i32(0x1C0))
    m["_data"] = data
    return m


# ----------------------------------------------------------------------------- vvd
def read_vvd(data, off):
    r = Reader(data)
    if data[off:off + 4] != b"IDSV":
        raise SystemExit("embedded VVD missing")
    num_lod_verts = [r.i32(off + 16 + 4 * i) for i in range(8)]
    nfix, fixstart, vstart = r.i32(off + 48), r.i32(off + 52), r.i32(off + 56)
    n = num_lod_verts[0]
    raw = []
    for i in range(n):
        o = off + vstart + i * 48
        w = r.fs(o, 3)
        bones = [data[o + 12], data[o + 13], data[o + 14]]
        nbones = data[o + 15]
        pos = r.fs(o + 16, 3)
        nrm = r.fs(o + 28, 3)
        uv = r.fs(o + 40, 2)
        raw.append((w, bones, nbones, pos, nrm, uv))
    if nfix:
        # LOD 0 vertex order comes from the fixup table
        verts = []
        for k in range(nfix):
            fo = off + fixstart + k * 12
            lod, src, cnt = r.i32(fo), r.i32(fo + 4), r.i32(fo + 8)
            if lod >= 0:
                verts.extend(raw[src:src + cnt])
        if len(verts) == n:
            raw = verts
    return raw


# ----------------------------------------------------------------------------- vtx
def read_vtx(data, off, mdl, strip_group_size, strip_size):
    """Returns {(bodypart, model, mesh): [triangle mesh-vertex indices]} for LOD 0."""
    r = Reader(data)
    base = off
    if r.i32(base) != 7:
        raise SystemExit("embedded VTX is not version 7")
    nbp, bpo = r.i32(base + 28), r.i32(base + 32)
    out = {}
    for p in range(nbp):
        po = base + bpo + p * 8
        nm, mo_rel = r.i32(po), r.i32(po + 4)
        for k in range(nm):
            mo = po + mo_rel + k * 8
            nlod, lodo = r.i32(mo), r.i32(mo + 4)
            if nlod <= 0:
                continue
            lo = mo + lodo                         # LOD 0
            nmesh, mesho = r.i32(lo), r.i32(lo + 4)
            for j in range(nmesh):
                me = lo + mesho + j * 9
                nsg, sgo = r.i32(me), r.i32(me + 4)
                tris = []
                for g in range(nsg):
                    sg = me + sgo + g * strip_group_size
                    nv, vo, ni, io = r.i32(sg), r.i32(sg + 4), r.i32(sg + 8), r.i32(sg + 12)
                    orig = [r.u16(sg + vo + v * 9 + 4) for v in range(nv)]
                    idx = [r.u16(sg + io + q * 2) for q in range(ni)]
                    for q in range(0, ni - 2, 3):
                        tris.append((orig[idx[q]], orig[idx[q + 1]], orig[idx[q + 2]]))
                out[(p, k, j)] = tris
    return out


def detect_strip_group_size(data, off):
    """v7 strip groups are 25 bytes (classic) or 33 (with topology); pick the one that parses sanely."""
    r = Reader(data)
    nbp, bpo = r.i32(off + 28), r.i32(off + 32)
    for size in (33, 25):
        try:
            for p in range(nbp):
                po = off + bpo + p * 8
                nm, mo_rel = r.i32(po), r.i32(po + 4)
                for k in range(nm):
                    mo = po + mo_rel + k * 8
                    if r.i32(mo) <= 0:
                        continue
                    lo = mo + r.i32(mo + 4)
                    nmesh, mesho = r.i32(lo), r.i32(lo + 4)
                    for j in range(nmesh):
                        me = lo + mesho + j * 9
                        nsg, sgo = r.i32(me), r.i32(me + 4)
                        for g in range(nsg):
                            sg = me + sgo + g * size
                            nv, ni = r.i32(sg), r.i32(sg + 8)
                            if not (0 < nv < 70000 and 0 < ni < 600000 and ni % 3 == 0):
                                raise ValueError
            return size
        except (ValueError, struct.error, IndexError):
            continue
    raise SystemExit("could not determine VTX strip group size")


# ----------------------------------------------------------------------------- export
def mat3x4_inverse(m):
    """Inverse of a rigid 3x4 (rotation + translation) row-major matrix."""
    R = [[m[0], m[1], m[2]], [m[4], m[5], m[6]], [m[8], m[9], m[10]]]
    t = [m[3], m[7], m[11]]
    Rt = [[R[j][i] for j in range(3)] for i in range(3)]
    ti = [-(Rt[i][0] * t[0] + Rt[i][1] * t[1] + Rt[i][2] * t[2]) for i in range(3)]
    return [Rt[0][0], Rt[0][1], Rt[0][2], ti[0], Rt[1][0], Rt[1][1], Rt[1][2], ti[1], Rt[2][0], Rt[2][1], Rt[2][2], ti[2]]


def build(m, choose):
    data = m["_data"]
    verts = read_vvd(data, m["vvd"][0])
    sgsize = detect_strip_group_size(data, m["vtx"][0])
    tris_by_mesh = read_vtx(data, m["vtx"][0], m, sgsize, 0)
    submeshes = {}                                     # material index -> (verts, tris)
    for p, part in enumerate(m["bodyparts"]):
        k = choose.get(part["name"], 0)
        if k < 0 or k >= len(part["models"]):
            continue
        mdl = part["models"][k]
        mbase = mdl["vertexindex"] // 48
        for j, mesh in enumerate(mdl["meshes"]):
            tris = tris_by_mesh.get((p, k, j), [])
            if not tris:
                continue
            sm = submeshes.setdefault(mesh["material"], {"map": {}, "verts": [], "tris": []})
            for tri in tris:
                face = []
                for v in tri:
                    g = mbase + mesh["vertexoffset"] + v
                    if g not in sm["map"]:
                        sm["map"][g] = len(sm["verts"])
                        sm["verts"].append(verts[g])
                    face.append(sm["map"][g])
                sm["tris"].append(face)
    return submeshes, sgsize


def write_phm(m, submeshes, path):
    out = bytearray(PHM_MAGIC)
    def s(txt):
        e = txt.encode("utf-8")
        out.extend(struct.pack("<H", len(e))); out.extend(e)
    s(m["name"])
    out.extend(struct.pack("<i", len(m["bones"])))
    for bone in m["bones"]:
        s(bone["name"])
        out.extend(struct.pack("<i", bone["parent"]))
        out.extend(struct.pack("<12f", *mat3x4_inverse(bone["poseToBone"])))   # bind pose, model space
    out.extend(struct.pack("<i", len(submeshes)))
    for mat, sm in sorted(submeshes.items()):
        s(m["textures"][mat] if 0 <= mat < len(m["textures"]) else f"material_{mat}")
        out.extend(struct.pack("<ii", len(sm["verts"]), len(sm["tris"]) * 3))
        for (w, bones, nb, pos, nrm, uv) in sm["verts"]:
            ws = [w[i] if i < nb else 0.0 for i in range(3)]
            bs = [bones[i] if i < nb else 0 for i in range(3)]
            out.extend(struct.pack("<3f3f2f", *pos, *nrm, *uv))
            out.extend(struct.pack("<3B", *bs)); out.extend(struct.pack("<3f", *ws))
        for face in sm["tris"]:
            out.extend(struct.pack("<3I", *face))
    open(path, "wb").write(out)


def write_obj(submeshes, path):
    with open(path, "w") as f:
        base = 1
        for mat, sm in sorted(submeshes.items()):
            f.write(f"g mat{mat}\n")
            for (_, _, _, pos, nrm, uv) in sm["verts"]:
                f.write("v %.4f %.4f %.4f\n" % tuple(pos))
            for (_, _, _, pos, nrm, uv) in sm["verts"]:
                f.write("vt %.5f %.5f\n" % (uv[0], 1 - uv[1]))
            for face in sm["tris"]:
                a, b2, c = (x + base for x in face)
                f.write(f"f {a}/{a} {b2}/{b2} {c}/{c}\n")
            base += len(sm["verts"])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mdl"); ap.add_argument("out")
    ap.add_argument("--obj"); ap.add_argument("--info", action="store_true")
    ap.add_argument("--choose", default="", help="bodypart=model,... (default model 0)")
    a = ap.parse_args()
    m = read_mdl(a.mdl)
    choose = {}
    for kv in filter(None, a.choose.split(",")):
        k, v = kv.split("=")
        choose[k] = int(v)
    submeshes, sgsize = build(m, choose)
    nv = sum(len(s["verts"]) for s in submeshes.values())
    nt = sum(len(s["tris"]) for s in submeshes.values())
    allpos = [v[3] for s in submeshes.values() for v in s["verts"]]
    lo = [min(p[i] for p in allpos) for i in range(3)] if allpos else [0, 0, 0]
    hi = [max(p[i] for p in allpos) for i in range(3)] if allpos else [0, 0, 0]
    info = {
        "model": m["name"], "bones": len(m["bones"]), "vertices": nv, "triangles": nt,
        "stripGroupSize": sgsize, "bboxMin": lo, "bboxMax": hi,
        "materials": [m["textures"][k] if k < len(m["textures"]) else k for k in sorted(submeshes)],
        "bodyparts": [(p["name"], [x["name"] for x in p["models"]]) for p in m["bodyparts"]],
        "cdtextures": m["cdtextures"], "skins": m["skins"],
    }
    print(json.dumps(info, indent=1))
    if a.info:
        return
    write_phm(m, submeshes, a.out)
    if a.obj:
        write_obj(submeshes, a.obj)


if __name__ == "__main__":
    main()
