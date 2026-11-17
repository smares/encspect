#!/usr/bin/env python3
"""Writes the MPEG audio fixture of tests/fixtures/mp4 whose samples lie in several stretches of the file, as ffmpeg 6.1's mov muxer (the ffmpeg package of Ubuntu 24.04) writes it.

`external-ffmpeg-mp3-two-tracks-in-mp4.mp4` holds the same half second of a tone twice, as two tracks of MPEG-1 Layer III (libmp3lame, 128 kbps): the muxer writes a sample at a time and
alternates the tracks, so each track's 21 frames lie in 21 chunks with the other track's chunks between them. The manifest has the boxes MP4Box lists as path and size (the offset
is the walk's own, from the file's bytes), the samples of both tracks read from the sample tables (stsz, stsc, stco; compared with ffprobe's packets), the streams ffprobe reports and
what the analyzer must show of them.

    python3 scripts/fixtures/make_external_mpeg_in_mp4_fixtures.py [--manifests-only]

Needs ffmpeg 6.1, MP4Box (gpac 2.2) and ffprobe (apt-get install ffmpeg gpac); --manifests-only rewrites the manifest from the committed file.
"""

import json
import pathlib
import struct
import subprocess
import sys

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "mp4"
NAME = "external-ffmpeg-mp3-two-tracks-in-mp4"
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
LAVF = "FFmpeg (Lavf60.16.100)"
SOURCE = "aevalsrc=0.5*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s=44100:d=0.5:c=stereo"
CONTAINERS = {"moov", "trak", "mdia", "minf", "stbl", "dinf", "edts", "udta", "ilst"}


def boxes(data):
    """Every box with its offset and size, with the path MP4Box prints."""
    rows = []

    def walk(start, end, path):
        while start + 8 <= end:
            size, kind = struct.unpack(">I4s", data[start:start + 8])
            if size < 8 or start + size > end:
                return
            name = "".join(chr(b) if 0x20 <= b < 0x7F else "©" if b == 0xA9 else "." for b in kind)
            here = path + [name]
            rows.append(["/".join(here), start, size])
            if name in CONTAINERS or (path and path[-1] == "ilst"):
                walk(start + 8, start + size, here)
            elif name == "meta":
                walk(start + 12, start + size, here)
            elif name in ("stsd", "dref"):
                walk(start + 16, start + size, here)
            elif name == "mp4a":
                walk(start + 8 + 28, start + size, here)
            start += size

    walk(0, len(data), [])
    return rows


def tracks(data):
    """Each track's samples as offset and size, and its media duration and timescale, from stsz, stsc and stco (ISO/IEC 14496-12 8.7)."""
    def children(start, end):
        while start + 8 <= end:
            size, kind = struct.unpack(">I4s", data[start:start + 8])
            if size < 8:
                return
            yield kind.decode("latin1"), start, start + size
            start += size

    def find(start, end, path):
        for kind, begin, finish in children(start, end):
            if kind == path[0]:
                return find(begin + 8, finish, path[1:]) if len(path) > 1 else (begin, finish)
        return None

    moov = find(0, len(data), ["moov"])
    result = []
    for kind, begin, finish in children(moov[0] + 8, moov[1]):
        if kind != "trak":
            continue
        stbl = find(begin + 8, finish, ["mdia", "minf", "stbl"])
        mdhd = find(begin + 8, finish, ["mdia", "mdhd"])
        stsz, stsc, stco = (find(stbl[0] + 8, stbl[1], [name]) for name in ("stsz", "stsc", "stco"))
        uniform, count = struct.unpack(">II", data[stsz[0] + 12:stsz[0] + 20])
        sizes = [uniform] * count if uniform else list(struct.unpack(f">{count}I", data[stsz[0] + 20:stsz[0] + 20 + 4 * count]))
        runs = struct.unpack(f">{struct.unpack('>I', data[stsc[0] + 12:stsc[0] + 16])[0] * 3}I", data[stsc[0] + 16:stsc[1]])
        runs = [runs[i:i + 3] for i in range(0, len(runs), 3)]
        chunks = struct.unpack(f">{struct.unpack('>I', data[stco[0] + 12:stco[0] + 16])[0]}I", data[stco[0] + 16:stco[1]])
        rows, index = [], 0
        for number, offset in enumerate(chunks, 1):
            for _ in range([r for r in runs if r[0] <= number][-1][1]):
                if index < len(sizes):
                    rows.append([offset, sizes[index]])
                    offset += sizes[index]
                    index += 1
        timescale, duration = struct.unpack(">II", data[mdhd[0] + 20:mdhd[0] + 28])
        result.append((rows, timescale, duration))
    return result


def manifest(path, command):
    from crosscheck_mp4_fixtures import ffprobe_packets, ffprobe_streams, mp4box_fields

    data = path.read_bytes()
    probed = ffprobe_streams(path)
    units, streams = [], []
    for number, (rows, timescale, duration) in enumerate(tracks(data)):
        units += rows
        rate = int(probed[number]["sample_rate"])
        streams.append({"codec": "MPEG-1 Layer III", "sample_rate": rate, "channels": 2, "channel_mode": "Joint stereo", "channel_layout": ["front left", "front right"],
                        "samples": len(rows) * 1152, "is_vbr": False, "encoder": ["LAME 3.100", "Low"], "writer": [LAVF, "Low"]})
    if ffprobe_packets(path) != units:
        sys.exit("ffprobe lists other packets than the sample tables")
    return {
        "description": "Two tracks of MPEG-1 Layer III in one MP4 file as ffmpeg's muxer writes them: every sample of a track is a chunk of its own, between the chunks of the other, so each "
                       "track's 21 frames lie in 21 stretches of the file, and the MPEG audio parser reads them as one stream each.",
        "encoder": {"tool": f"{FFMPEG_VERSION} (ffmpeg package of Ubuntu 24.04)", "command": command},
        "length": len(data), "format": "MP4", "boxes": boxes(data), "streams": streams, "units": units, "fields": mp4box_fields(path),
        "findings": [["mp4.moov-after-mdat", "not optimized for streaming"]],
        "crosscheck": {"ffprobe": True, "mp4box": True, "faad": None, "decode": True, "skip": {"faad": "faad2 reads AAC only."}},
    }


def main(arguments):
    path = OUT / (NAME + ".mp4")
    target = OUT / (NAME + ".expected.json")
    if "--manifests-only" in arguments:
        command = json.loads(target.read_text(encoding="utf-8"))["encoder"]["command"]
    else:
        version = subprocess.run(["ffmpeg", "-version"], capture_output=True, text=True).stdout.splitlines()[0]
        if not version.startswith(FFMPEG_VERSION):
            sys.exit(f"{FFMPEG_VERSION} is required, found {version}")
        command = ["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", SOURCE, "-map", "0:a", "-map", "0:a", "-c:a", "libmp3lame", "-b:a", "128k", "{out}"]
        subprocess.run([str(path) if a == "{out}" else a for a in command], check=True, capture_output=True)
    target.write_text(json.dumps(manifest(path, command), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{path.name}: {path.stat().st_size} bytes")


if __name__ == "__main__":
    main(sys.argv[1:])
