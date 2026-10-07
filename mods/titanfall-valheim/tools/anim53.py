"""
Titanfall 2 (studiomdl v53) animation decoder for PilotHeim.

Per-bone RLE records (found by inspection of the user's titan_buddy_*.mdl files):
  +0  float posscale        scale for animated position values
  +4  u8 bone, u8 flags, i16 pad
  +8  rotation  : Quaternion64 (flag 0x04), none (0x10, rest rotation),
                  else 3 x i16 offsets (+pad) to Source RLE value streams:
                  euler = value * bone.rotscale + bone.rot (rest added unless the anim is delta)
  +16 position  : Vector48 half floats (flag 0x02), else 3 x i16 offsets to RLE streams:
                  value * record posscale (+ bone.pos unless delta)
  +22 scale     : Vector48 half floats (flag 0x08), else offsets: value * bone.scalescale + bone.scale
  +28 i32 size of this record (0 = last)
Bones without a record keep their rest transform. Long animations are split into
sections of `sectionframes` frames (section table: one i32 data offset per section).
"""
import math
import struct

RAWPOS, RAWROT, RAWSCALE, NOROT = 0x02, 0x04, 0x08, 0x10
POS_ADD_REST = 1.0      # Source behaviour, confirmed by posing BT (walk) against its mesh


def q64(raw):
    v = struct.unpack("<Q", raw)[0]
    x = ((v & 0x1FFFFF) - 1048576) / 1048576.5
    y = (((v >> 21) & 0x1FFFFF) - 1048576) / 1048576.5
    z = (((v >> 42) & 0x1FFFFF) - 1048576) / 1048576.5
    w = math.sqrt(max(0.0, 1.0 - x * x - y * y - z * z))
    if v >> 63:
        w = -w
    return (x, y, z, w)


def half3(b, o):
    return list(struct.unpack_from("<3e", b, o))


def anim_value(b, o, frame):
    """Source ExtractAnimValue: RLE runs of (valid, total) followed by `valid` shorts."""
    k = frame
    while True:
        valid, total = b[o], b[o + 1]
        if total == 0:
            return 0
        if total > k:
            if valid > k:
                return struct.unpack_from("<h", b, o + 2 + 2 * k)[0]
            return struct.unpack_from("<h", b, o + 2 + 2 * (valid - 1))[0]
        k -= total
        o += 2 + 2 * valid


def angle_quaternion(x, y, z):
    """Source AngleQuaternion for RadianEuler (roll=x, pitch=y, yaw=z)."""
    sr, cr = math.sin(x * 0.5), math.cos(x * 0.5)
    sp, cp = math.sin(y * 0.5), math.cos(y * 0.5)
    sy, cy = math.sin(z * 0.5), math.cos(z * 0.5)
    srXcp, crXsp = sr * cp, cr * sp
    crXcp, srXsp = cr * cp, sr * sp
    return (srXcp * cy - crXsp * sy, crXsp * cy + srXcp * sy, crXcp * sy - srXsp * cy, crXcp * cy + srXsp * sy)


class AnimDesc:
    def __init__(self, data, off):
        I = lambda o: struct.unpack_from("<i", data, o)[0]
        self.data, self.off = data, off
        e = data.index(b"\0", off + I(off + 4))
        self.name = data[off + I(off + 4):e].decode("latin1")
        self.fps = struct.unpack_from("<f", data, off + 8)[0]
        self.flags = I(off + 12)
        self.numframes = I(off + 16)
        self.animindex = I(off + 32)
        self.sectionindex = I(off + 52)
        self.sectionframes = I(off + 56)

    def records_for_frame(self, frame):
        if self.sectionframes > 0:
            s = frame // self.sectionframes
            base = self.off + struct.unpack_from("<i", self.data, self.off + self.sectionindex + 4 * s)[0]
            return base, frame - s * self.sectionframes
        return self.off + self.animindex, frame

    @property
    def delta(self):
        return bool(self.flags & 0x04)

    def pose(self, frame, rest, fallback=None):
        """Local (pos, quat, scale) per bone for one frame.
        rest = per bone dict with pos, quat, rot, rotscale, scale, scalescale (from the model);
        fallback = optional [(pos, quat)] for bones this animation has no record for."""
        b = self.data
        add = 0.0 if self.delta else 1.0
        if fallback:
            out = [(list(fp), tuple(fq), list(r.get("scale", [1.0, 1.0, 1.0]))) for (fp, fq), r in zip(fallback, rest)]
        else:
            out = [(list(r["pos"]), tuple(r["quat"]), list(r.get("scale", [1.0, 1.0, 1.0]))) for r in rest]
        p, f = self.records_for_frame(min(frame, self.numframes - 1))
        while True:
            posscale = struct.unpack_from("<f", b, p)[0]
            bone, flags = b[p + 4], b[p + 5]
            size = struct.unpack_from("<i", b, p + 28)[0]
            if bone < len(out):
                r = rest[bone]
                # rotation
                if flags & RAWROT:
                    q = q64(b[p + 8:p + 16])
                elif flags & NOROT:
                    q = tuple(r["quat"])
                else:
                    offs = struct.unpack_from("<3h", b, p + 8)
                    e = [(anim_value(b, p + 8 + offs[j], f) * r["rotscale"][j] if offs[j] else 0.0) + add * r["rot"][j]
                         for j in range(3)]
                    q = angle_quaternion(*e)
                # position
                if flags & RAWPOS:
                    pos = half3(b, p + 16)
                else:
                    offs = struct.unpack_from("<3h", b, p + 16)
                    pos = [(anim_value(b, p + 16 + offs[j], f) * posscale if offs[j] else 0.0) + add * r["pos"][j] * POS_ADD_REST
                           for j in range(3)]
                # scale
                if flags & RAWSCALE:
                    sc = half3(b, p + 22)
                else:
                    offs = struct.unpack_from("<3h", b, p + 22)
                    sc = [(anim_value(b, p + 22 + offs[j], f) * r["scalescale"][j] if offs[j] else 0.0) + add * r["scale"][j]
                          for j in range(3)]
                out[bone] = (pos, q, sc)
            if size <= 0:
                break
            p += size
        return out


def read_anims(path):
    data = open(path, "rb").read()
    I = lambda o: struct.unpack_from("<i", data, o)[0]
    n, ai = I(0xB8), I(0xBC)
    return data, [AnimDesc(data, ai + k * 92) for k in range(n)]
