#!/usr/bin/env python3
"""Generates the application and tray icons (src/AmbientLight.App/Assets/*.ico).

The icons are drawn procedurally (signed distance fields, 4x4 supersampling) with the standard library only,
so they can be regenerated anywhere:

    python3 tools/generate-icons.py

AmbientLight.ico         a dark screen surrounded by a rainbow glow (running)
AmbientLight-paused.ico  the same screen with a grey glow and a pause sign (paused)

Each .ico holds 16/20/24/32/40/48/64 px images as 32-bit DIBs (the tray picks 16-32 px depending on DPI) and a
256 px PNG for Explorer's large views.
"""

import colorsys
import math
import os
import struct
import zlib

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SUPERSAMPLING = 4

SCREEN_FILL = (0.086, 0.094, 0.114)
SCREEN_BORDER = (0.36, 0.39, 0.45)
PAUSED_GLOW = (0.56, 0.58, 0.62)
PAUSE_BARS = (0.92, 0.93, 0.95)


def rounded_rect_distance(px, py, cx, cy, half_w, half_h, radius):
    """Signed distance to a rounded rectangle (negative inside)."""
    qx = abs(px - cx) - (half_w - radius)
    qy = abs(py - cy) - (half_h - radius)
    outside = math.hypot(max(qx, 0.0), max(qy, 0.0))
    inside = min(max(qx, qy), 0.0)
    return outside + inside - radius


def over(top_rgb, top_a, bottom_rgb, bottom_a):
    """Porter-Duff 'over' on straight-alpha colors; returns premultiplied rgb and alpha."""
    out_a = top_a + bottom_a * (1.0 - top_a)
    rgb = tuple(t * top_a + b * bottom_a * (1.0 - top_a) for t, b in zip(top_rgb, bottom_rgb))
    return rgb, out_a


def sample(size, x, y, paused):
    """Color of one supersample at (x, y) in pixel units; returns premultiplied rgb and alpha."""
    c = size / 2.0
    half_w, half_h = size * 0.30, size * 0.21
    radius = size * 0.06
    d = rounded_rect_distance(x, y, c, c, half_w, half_h, radius)

    # Glow: Gaussian falloff outside the screen, hue running around the center.
    sigma = size * 0.13
    glow_a = 0.0
    glow_rgb = (0.0, 0.0, 0.0)
    if d > 0:
        glow_a = 0.95 * math.exp(-((d / sigma) ** 2))
        if paused:
            glow_rgb = PAUSED_GLOW
        else:
            hue = (math.atan2(y - c, x - c) / (2 * math.pi) + 1.0) % 1.0
            glow_rgb = colorsys.hsv_to_rgb(hue, 0.85, 1.0)

    # Screen: dark fill with a lighter rim, anti-aliased by the supersampling.
    screen_a = 1.0 if d <= 0 else 0.0
    border = max(size * 0.035, 0.6)
    screen_rgb = SCREEN_BORDER if -border < d <= 0 else SCREEN_FILL

    if paused and d <= 0:
        bar_w, bar_h, gap = size * 0.06, size * 0.20, size * 0.05
        for bx in (c - gap - bar_w / 2, c + gap + bar_w / 2):
            if abs(x - bx) <= bar_w / 2 and abs(y - c) <= bar_h / 2:
                screen_rgb = PAUSE_BARS

    return over(screen_rgb, screen_a, glow_rgb, glow_a)


def render(size, paused):
    """Returns rows of straight-alpha RGBA bytes, top to bottom."""
    rows = []
    n = SUPERSAMPLING
    for py in range(size):
        row = bytearray()
        for px in range(size):
            acc_r = acc_g = acc_b = acc_a = 0.0
            for sy in range(n):
                for sx in range(n):
                    (r, g, b), a = sample(size, px + (sx + 0.5) / n, py + (sy + 0.5) / n, paused)
                    acc_r += r
                    acc_g += g
                    acc_b += b
                    acc_a += a
            count = n * n
            a = acc_a / count
            if a > 0:
                r, g, b = (acc_r / count) / a, (acc_g / count) / a, (acc_b / count) / a
            else:
                r = g = b = 0.0
            row += bytes(int(round(max(0.0, min(1.0, v)) * 255)) for v in (r, g, b, a))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + row for row in rows)
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def dib(size, rows):
    mask_stride = ((size + 31) // 32) * 4
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, size * size * 4 + mask_stride * size, 0, 0, 0, 0)
    pixels = bytearray()
    for row in reversed(rows):  # bottom-up
        for i in range(0, len(row), 4):
            r, g, b, a = row[i:i + 4]
            pixels += bytes((b, g, r, a))
    mask = bytes(mask_stride * size)  # all zero: transparency comes from the alpha channel
    return header + bytes(pixels) + mask


def write_ico(path, paused):
    images = []
    for size in SIZES:
        rows = render(size, paused)
        images.append((size, png(size, rows) if size >= 256 else dib(size, rows)))

    offset = 6 + 16 * len(images)
    directory = b""
    for size, data in images:
        dimension = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset)
        offset += len(data)

    with open(path, "wb") as file:
        file.write(struct.pack("<HHH", 0, 1, len(images)))
        file.write(directory)
        for _, data in images:
            file.write(data)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(root, "src", "AmbientLight.App", "Assets")
    os.makedirs(assets, exist_ok=True)
    write_ico(os.path.join(assets, "AmbientLight.ico"), paused=False)
    write_ico(os.path.join(assets, "AmbientLight-paused.ico"), paused=True)
    print("Wrote", assets)


if __name__ == "__main__":
    main()
