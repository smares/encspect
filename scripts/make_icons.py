#!/usr/bin/env python3
"""Regenerates the raster icon files of the GUI from the SVG sources in src/Encspect.Gui/Assets/Icon.

Sources: F2.svg (the small form, 16 to 32 px), E.svg (the large form, 40 px and up; rounded tile). The .ico holds
Microsoft's set 16, 20, 24, 32, 40, 48, 64 and 256, plus 128; 40 px is the large form, which is legible there. Rendered with
Playwright's Chromium (scripts/icons/render_svg.js), light-theme colors. The .ico and .icns containers are written here
directly (PNG-compressed entries), so no imaging library is needed.

Usage: python3 scripts/make_icons.py     (needs node, and playwright for node: NODE_PATH may name its folder)
"""
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ICON = ROOT / "src" / "Encspect.Gui" / "Assets" / "Icon"
RENDER = Path(__file__).resolve().parent / "icons" / "render_svg.js"

SMALL = {"F2.svg": [16, 20, 24, 32]}
LARGE = {"E.svg": [16, 32, 40, 48, 64, 128, 256, 512, 1024]}
# icns entry types holding PNG data, by pixel size (macOS names them for the point size and scale)
ICNS_TYPES = [(16, b"icp4"), (32, b"icp5"), (64, b"icp6"), (128, b"ic07"), (256, b"ic08"), (512, b"ic09"), (1024, b"ic10")]


def render(jobs, folder):
    args = []
    paths = {}
    for svg, sizes in jobs.items():
        for size in sizes:
            out = folder / f"{Path(svg).stem}-{size}.png"
            args += [str(ICON / svg), str(size), str(out)]
            paths[(svg, size)] = out
    subprocess.run(["node", str(RENDER), *args], check=True)
    return {key: path.read_bytes() for key, path in paths.items()}


def write_ico(entries, target):
    entries = sorted(entries)
    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    directory = b""
    data = b""
    for size, png in entries:
        directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    target.write_bytes(header + directory + data)


def write_icns(pngs, target):
    body = b"".join(kind + struct.pack(">I", 8 + len(pngs[size])) + pngs[size] for size, kind in ICNS_TYPES)
    target.write_bytes(b"icns" + struct.pack(">I", 8 + len(body)) + body)


def main():
    with tempfile.TemporaryDirectory() as tmp:
        folder = Path(tmp)
        small = render(SMALL, folder)
        large = render(LARGE, folder)
    ico = [(s, p) for (_, s), p in small.items()] + [(s, p) for (_, s), p in large.items() if s >= 40 and s <= 256]
    write_ico(ico, ICON / "encspect.ico")
    # The icns takes the large form at every size: macOS draws its icons on tiles of their own.
    by_size = {s: p for (_, s), p in large.items()}
    write_icns(by_size, ICON / "encspect.icns")
    (ICON / "encspect-256.png").write_bytes(by_size[256])
    (ICON / "encspect-128.png").write_bytes(by_size[128])
    print("wrote", ", ".join(sorted(p.name for p in ICON.iterdir() if p.suffix in (".ico", ".icns", ".png"))))


if __name__ == "__main__":
    sys.exit(main())
