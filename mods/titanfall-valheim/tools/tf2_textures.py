"""
Pre-compress exported Titanfall material textures into GPU-ready PHTEX files so the mod
uploads them with Texture2D.LoadRawTextureData instead of decoding PNGs on the main thread
(BT went from a 7.5 s hitch to milliseconds).

PHTEX: "PHT1", u32 format (10 = DXT1, 12 = DXT5, Unity TextureFormat ids), u32 width,
u32 height, u32 mip count, then the mip chain (largest first), as Unity expects.
  _col -> DXT1 (DXT5 if it has alpha), _ilm -> DXT1, _nml -> DXT5nm (x in alpha, y in green)

Usage: python tf2_textures.py <assets/materials> [max_size=2048]
"""
import io
import os
import struct
import sys

from PIL import Image

DXT1, DXT5 = 10, 12
SUFFIXES = ("col", "nml", "ilm")


def encode_level(img, fmt):
    buf = io.BytesIO()
    img.save(buf, format="DDS", pixel_format="DXT1" if fmt == DXT1 else "DXT5")
    data = buf.getvalue()
    return data[128:]                       # strip the DDS header (no DX10 extension for DXT1/5)


def convert(png, out, max_size):
    suffix = os.path.splitext(png)[0].rsplit("_", 1)[-1].lower()
    im = Image.open(png).convert("RGBA")
    if suffix == "nml":
        r, g, b, a = im.split()
        im = Image.merge("RGBA", (Image.new("L", im.size, 255), g, Image.new("L", im.size, 0), r))
        fmt = DXT5
    elif suffix == "col" and im.getchannel("A").getextrema()[0] < 250:
        fmt = DXT5
    else:
        fmt = DXT1
    w, h = im.size
    scale = min(1.0, max_size / max(w, h))
    if scale < 1.0:
        w, h = max(4, int(w * scale)), max(4, int(h * scale))
        im = im.resize((w, h), Image.LANCZOS)
    levels, cw, ch = [], w, h
    level = im
    while True:
        levels.append(encode_level(level, fmt))
        if cw <= 4 or ch <= 4:
            break
        cw, ch = max(4, cw // 2), max(4, ch // 2)
        level = level.resize((cw, ch), Image.BOX)
    with open(out, "wb") as f:
        f.write(b"PHT1" + struct.pack("<IIII", fmt, w, h, len(levels)))
        for d in levels:
            f.write(d)
    return fmt, w, h, len(levels)


def main(root, max_size=2048):
    n = 0
    for mat in sorted(os.listdir(root)):
        d = os.path.join(root, mat)
        if not os.path.isdir(d):
            continue
        for suffix in SUFFIXES:
            png = os.path.join(d, f"{mat}_{suffix}.png")
            if not os.path.exists(png):
                continue
            out = png[:-4] + ".phtex"
            if os.path.exists(out) and os.path.getmtime(out) >= os.path.getmtime(png):
                continue
            fmt, w, h, mips = convert(png, out, max_size)
            n += 1
            print(f"  {mat}_{suffix}: {'DXT1' if fmt == DXT1 else 'DXT5'} {w}x{h}, {mips} mips")
    print(f"converted {n} textures")


if __name__ == "__main__":
    main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 2048)
