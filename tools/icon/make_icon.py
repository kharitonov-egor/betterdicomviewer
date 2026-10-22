"""Draws the application icon (a stylised axial CT slice) and writes a multi-size .ico. Pure Python, no dependencies.

Usage: python make_icon.py <output.ico>
"""
import struct
import sys
import zlib

BG = (0x16, 0x20, 0x2B)
BODY = (0xE6, 0xEC, 0xF2)
LUNG = (0x22, 0x2E, 0x3B)
ACCENT = (0x4C, 0xC2, 0xFF)


def shade(x, y):
    """Colour at a point in a 1x1 unit square (0..1), or None for transparent."""
    r = 0.18
    cx, cy = min(max(x, r), 1 - r), min(max(y, r), 1 - r)
    if (x - cx) ** 2 + (y - cy) ** 2 > r * r:
        return None
    color = BG
    if ((x - 0.5) / 0.36) ** 2 + ((y - 0.53) / 0.28) ** 2 <= 1:
        color = BODY
        for sx in (0.36, 0.64):
            if ((x - sx) / 0.11) ** 2 + ((y - 0.5) / 0.16) ** 2 <= 1:
                color = LUNG
        if (x - 0.5) ** 2 + (y - 0.72) ** 2 <= 0.045 ** 2:
            color = ACCENT
        if (x - 0.62) ** 2 + (y - 0.43) ** 2 <= 0.03 ** 2:
            color = ACCENT
    return color


def render(size, ss=4):
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(ss):
                for sx in range(ss):
                    c = shade((px + (sx + 0.5) / ss) / size, (py + (sy + 0.5) / ss) / size)
                    if c is not None:
                        acc[0] += c[0]
                        acc[1] += c[1]
                        acc[2] += c[2]
                        acc[3] += 255
            n = ss * ss
            a = acc[3] // n
            if a:
                row += bytes((acc[0] * 255 // acc[3], acc[1] * 255 // acc[3], acc[2] * 255 // acc[3], a))
            else:
                row += bytes((0, 0, 0, 0))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main(out):
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [png(s, render(s, 4 if s < 128 else 2)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(out, "wb") as f:
        f.write(header + entries + b"".join(images))


if __name__ == "__main__":
    main(sys.argv[1])
