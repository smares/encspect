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
import subprocess
import sys

from make_external_mp4_fixtures import boxes, tables

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "mp4"
NAME = "external-ffmpeg-mp3-two-tracks-in-mp4"
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
LAVF = "FFmpeg (Lavf60.16.100)"
SOURCE = "aevalsrc=0.5*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s=44100:d=0.5:c=stereo"


def manifest(path, command):
    from crosscheck_mp4_fixtures import ffprobe_packets, ffprobe_streams, mp4box_fields

    data = path.read_bytes()
    probed = ffprobe_streams(path)
    units, streams = [], []
    for number in range(2):
        rows, timescale, duration, _ = tables(data, number)
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
