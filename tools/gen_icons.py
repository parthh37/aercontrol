#!/usr/bin/env python3
"""Generates AerControl icons (teal rounded square, white droplet, mist arcs) with no
third-party dependencies. Writes the Windows MSIX assets, tray .ico, widget images and the
macOS AppIcon set. Re-run after tweaking colours:  python3 tools/gen_icons.py
"""
import math
import os
import struct
import subprocess
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOP = (20, 184, 166)     # teal-500
BOTTOM = (13, 110, 125)  # deep teal
SS = 3                   # supersampling factor


def png_bytes(w, h, rgba):
    raw = b"".join(b"\x00" + bytes(rgba[y * w * 4:(y + 1) * w * 4]) for y in range(h))
    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def in_rounded_rect(x, y, x0, y0, x1, y1, r):
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def in_droplet(x, y, cx, cy, s):
    # Circle at the bottom plus a triangle-ish tip formed by two tangent lines.
    r = 0.22 * s
    by = cy + 0.08 * s
    if (x - cx) ** 2 + (y - by) ** 2 <= r * r:
        return True
    tip = by - 0.42 * s
    if tip <= y <= by:
        half = r * (y - tip) / (by - tip) * 1.08
        return abs(x - cx) <= half
    return False


def on_arc(x, y, cx, cy, radius, width, a0, a1):
    d = math.hypot(x - cx, y - cy)
    if abs(d - radius) > width / 2:
        return False
    a = math.degrees(math.atan2(-(y - cy), x - cx))
    return a0 <= a <= a1


def render(w, h, background=True, glyph_scale=1.0):
    s = min(w, h) * glyph_scale
    cx, cy = w / 2, h / 2
    out = bytearray(w * h * 4)
    for py in range(h):
        for px in range(w):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(SS):
                for sx in range(SS):
                    x = px + (sx + 0.5) / SS
                    y = py + (sy + 0.5) / SS
                    col = None
                    if background:
                        m = min(w, h) * 0.06
                        if in_rounded_rect(x, y, m, m, w - m, h - m, min(w, h) * 0.22):
                            t = y / h
                            col = tuple(TOP[i] + (BOTTOM[i] - TOP[i]) * t for i in range(3)) + (255,)
                    white = in_droplet(x, y, cx - 0.06 * s, cy + 0.02 * s, s * 0.9)
                    for k, rad in enumerate((0.20, 0.30)):
                        if on_arc(x, y, cx - 0.02 * s, cy - 0.12 * s, rad * s, 0.045 * s, 15, 75):
                            white = True
                    if white:
                        col = (255, 255, 255, 255)
                    if col:
                        for i in range(4):
                            acc[i] += col[i]
            n = SS * SS
            o = (py * w + px) * 4
            a = acc[3] / n
            for i in range(3):
                out[o + i] = min(255, int(acc[i] / max(acc[3] / 255, 1e-6))) if a else 0
            out[o + 3] = int(a)
    return out


def write_png(path, w, h, **kw):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(png_bytes(w, h, render(w, h, **kw)))
    print("wrote", os.path.relpath(path, ROOT))


def write_padded(path, w, h, icon):
    """Icon centred on a transparent canvas (wide tiles, splash screen)."""
    img = render(icon, icon)
    out = bytearray(w * h * 4)
    ox, oy = (w - icon) // 2, (h - icon) // 2
    for y in range(icon):
        out[((oy + y) * w + ox) * 4:((oy + y) * w + ox + icon) * 4] = img[y * icon * 4:(y + 1) * icon * 4]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(png_bytes(w, h, out))
    print("wrote", os.path.relpath(path, ROOT))


def write_ico(path, sizes):
    images = [png_bytes(s, s, render(s, s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(header + entries + b"".join(images))
    print("wrote", os.path.relpath(path, ROOT))


def main():
    win = os.path.join(ROOT, "windows", "src", "AerControl.App", "Assets")
    write_png(os.path.join(win, "Square44x44Logo.png"), 44, 44)
    write_png(os.path.join(win, "Square44x44Logo.targetsize-24_altform-unplated.png"), 24, 24)
    write_png(os.path.join(win, "Square150x150Logo.png"), 150, 150)
    write_png(os.path.join(win, "StoreLogo.png"), 50, 50)
    write_png(os.path.join(win, "LockScreenLogo.png"), 24, 24)
    write_padded(os.path.join(win, "Wide310x150Logo.png"), 310, 150, 120)
    write_padded(os.path.join(win, "SplashScreen.png"), 620, 300, 200)
    write_png(os.path.join(win, "Widget", "Icon.png"), 64, 64)
    write_padded(os.path.join(win, "Widget", "Screenshot.png"), 300, 304, 160)
    write_ico(os.path.join(win, "AerControl.ico"), [16, 24, 32, 48, 64, 256])

    # macOS asset catalog + .icns for the SwiftPM dev bundle.
    mac = os.path.join(ROOT, "macos", "App", "Assets.xcassets", "AppIcon.appiconset")
    iconset = os.path.join(ROOT, "macos", "build", "AppIcon.iconset")
    images = []
    for pt in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            px = pt * scale
            name = f"icon_{pt}x{pt}{'@2x' if scale == 2 else ''}.png"
            write_png(os.path.join(mac, name), px, px)
            os.makedirs(iconset, exist_ok=True)
            with open(os.path.join(mac, name), "rb") as src, open(os.path.join(iconset, name), "wb") as dst:
                dst.write(src.read())
            images.append({"idiom": "mac", "size": f"{pt}x{pt}", "scale": f"{scale}x", "filename": name})
    import json
    with open(os.path.join(mac, "Contents.json"), "w") as f:
        json.dump({"images": images, "info": {"version": 1, "author": "xcode"}}, f, indent=2)
    with open(os.path.join(ROOT, "macos", "App", "Assets.xcassets", "Contents.json"), "w") as f:
        json.dump({"info": {"version": 1, "author": "xcode"}}, f, indent=2)
    if subprocess.run(["which", "iconutil"], capture_output=True).returncode == 0:
        subprocess.run(["iconutil", "-c", "icns", iconset, "-o",
                        os.path.join(ROOT, "macos", "App", "AppIcon.icns")], check=True)
        print("wrote macos/App/AppIcon.icns")


if __name__ == "__main__":
    main()
