#!/usr/bin/env python3
"""Writes the encoder-made Matroska and WebM fixtures in tests/fixtures/matroska (external-*).

FFmpeg 6.1 (libavformat's matroska and webm muxers, matroskaenc.c) and mkvmerge from MKVToolNix (Ubuntu 24.04's mkvtoolnix package, v82.0)
mux the audio of the project's other fixtures, copied unchanged, into Matroska and WebM: AAC (LC, HE-AAC and HE-AAC v2), MPEG audio Layers II
and III, AC-3, E-AC-3, DTS, Vorbis, Opus (also with the ambisonics mapping), WavPack; and FFmpeg's own encoders write FLAC, PCM, ALAC, TTA and Dolby
TrueHD, which the analyzer reports from the TrackEntry only. The muxers' options give a live stream (unknown sizes), no CRC-32 elements, many small
Clusters, a track with video, two audio tracks, tags, chapters and an attachment, a different TimestampScale, no Cues, zlib compression of the frames
and WebM mode.

The muxers write random UIDs, so the files are committed and --manifests-only recomputes the manifests from them without the tools. Each
manifest has the parts of a hand-built one (make_matroska_fixtures.py), worked out by matroska_model.py from the file's bytes, plus the tool
and its arguments ("encoder") and the streams and findings the analyzer must report. crosscheck_matroska_fixtures.py confirms them with ffprobe,
mkvinfo and MediaInfo.
"""

import json
import math
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile
import wave

import matroska_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "matroska"
FIXTURES_DIR = ROOT / "tests" / "fixtures"

FFMPEG = "ffmpeg"
MKVMERGE = "mkvmerge"

# name: (tool, inputs, arguments, expected streams as (codec, sample rate, channels), expected findings as (code, contains)).
# Inputs are paths under tests/fixtures (or the names of the files written below); the arguments go before the inputs for ffmpeg and
# between the output and the inputs for mkvmerge, which takes its options per file where they apply.
NOT_READ = ("matroska.codec-not-analyzed", "frames are not analyzed yet")
MPEG_PACKETS = ("mpeg.packets-not-analyzed", "only one contiguous payload")

FIXTURES = {
    "external-ffmpeg-aac-lc": ("ffmpeg", ["aac/faac-1.50.m4a"], ["-c", "copy"], [("AAC", 44100, 2)], [], "mka"),
    "external-ffmpeg-aac-he": ("ffmpeg", ["aac/faac-2.2-he.m4a"], ["-c", "copy"], [("AAC", 44100, 2)], [], "mka"),
    "external-ffmpeg-aac-hev2": ("ffmpeg", ["aac/apple-hev2-m4a-cbr-st44.m4a"], ["-c", "copy"], [("AAC", 44100, 2)], [], "mka"),
    "external-ffmpeg-mp3": ("ffmpeg", ["mpeg/external-ffmpeg-cbr.mp3"], ["-c", "copy"], [("MPEG audio Layer III", 44100, 2)], [MPEG_PACKETS], "mka"),
    "external-ffmpeg-mp2": ("ffmpeg", ["mpeg/external-ffmpeg-mp2.mp2"], ["-c", "copy"], [("MPEG audio Layer II", 48000, 2)], [MPEG_PACKETS], "mka"),
    "external-ffmpeg-ac3-51": ("ffmpeg", ["ac3/external-ffmpeg-5.1-448k.ac3"], ["-c", "copy"], [("AC-3", 48000, 6)], [], "mka"),
    "external-ffmpeg-eac3-51": ("ffmpeg", ["ac3/external-ffmpeg-eac3-5.1-256k.eac3"], ["-c", "copy"], [("E-AC-3", 48000, 6)], [], "mka"),
    "external-ffmpeg-dts-51": ("ffmpeg", ["dts/external-ffmpeg-5.1-48k-768k.dts"], ["-c", "copy"], [("DTS", 48000, 6)], [], "mka"),
    "external-ffmpeg-vorbis": ("ffmpeg", ["ogg/external-ffmpeg-libvorbis.ogg"], ["-c", "copy"], [("Vorbis", 44100, 2)], [], "mka"),
    "external-ffmpeg-vorbis-webm": ("ffmpeg", ["ogg/external-ffmpeg-libvorbis.ogg"], ["-c", "copy"], [("Vorbis", 44100, 2)], [], "webm"),
    "external-ffmpeg-opus": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy"], [("Opus", 48000, 2)], [], "mka"),
    "external-ffmpeg-opus-webm": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy"], [("Opus", 48000, 2)], [], "webm"),
    "external-ffmpeg-opus-ambisonics": ("ffmpeg", ["ogg/external-ffmpeg-libopus-ambisonics.opus"], ["-c", "copy"], [("Opus", 48000, 4)], [], "mka"),
    "external-ffmpeg-wavpack": ("ffmpeg", ["wavpack/external-ffmpeg-apev2-tag.wv"], ["-c", "copy"], [("WavPack", 44100, 2)], [NOT_READ], "mka"),
    "external-ffmpeg-flac": ("ffmpeg", ["sine"], ["-c:a", "flac"], [("FLAC", 44100, 1)], [NOT_READ], "mka"),
    "external-ffmpeg-pcm": ("ffmpeg", ["sine"], ["-c:a", "pcm_s16le"], [("PCM (integer, little-endian)", 44100, 1)], [NOT_READ], "mka"),
    "external-ffmpeg-alac": ("ffmpeg", ["sine"], ["-c:a", "alac"], [("ALAC", 44100, 1)], [NOT_READ], "mka"),
    "external-ffmpeg-tta": ("ffmpeg", ["sine"], ["-c:a", "tta"], [("TTA", 44100, 1)], [NOT_READ], "mka"),
    "external-ffmpeg-truehd": ("ffmpeg", ["sine"], ["-c:a", "truehd", "-strict", "-2"], [("Dolby TrueHD", 44100, 1)], [NOT_READ], "mka"),
    "external-ffmpeg-live": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy", "-live", "1"], [("Opus", 48000, 2)],
                             [("matroska.segment-size-unknown", "unknown")], "mkv"),
    "external-ffmpeg-no-crc": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy", "-write_crc32", "0"], [("Opus", 48000, 2)], [], "mka"),
    "external-ffmpeg-small-clusters": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy", "-cluster_size_limit", "2000", "-cluster_time_limit", "100"],
                                       [("Opus", 48000, 2)], [], "mka"),
    "external-ffmpeg-video": ("ffmpeg", ["testsrc", "ogg/external-ffmpeg-libopus.opus"], ["-c:v", "mpeg4", "-c:a", "copy", "-shortest"], [("Opus", 48000, 2)],
                              [("matroska.track-not-analyzed", "video track")], "mkv"),
    "external-ffmpeg-two-audio": ("ffmpeg", ["aac/faac-1.50.m4a", "ogg/external-ffmpeg-libvorbis.ogg"], ["-map", "0:a", "-map", "1:a", "-c", "copy"],
                                  [("AAC", 44100, 2), ("Vorbis", 44100, 2)], [], "mka"),
    "external-ffmpeg-metadata": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus", "chapters.ffmetadata", "cover.jpg"],
                                 ["-map", "0:a", "-map_metadata", "1", "-map_chapters", "1", "-attach", "cover.jpg", "-metadata:s:t", "mimetype=image/jpeg",
                                  "-c", "copy", "-metadata", "title=A title", "-metadata", "artist=An artist"], [("Opus", 48000, 2)], [], "mka"),
    "external-mkvmerge-mp3": ("mkvmerge", ["mpeg/external-ffmpeg-cbr.mp3"], [], [("MPEG audio Layer III", 44100, 2)], [MPEG_PACKETS], "mka"),
    "external-mkvmerge-aac": ("mkvmerge", ["aac/apple-lc-adts-cbr-st44.aac"], [], [("AAC", 44100, 2)], [], "mka"),
    "external-mkvmerge-aac-he": ("mkvmerge", ["aac/faac-2.2-he-adts.aac"], [], [("AAC", 44100, 2)], [], "mka"),
    "external-mkvmerge-ac3": ("mkvmerge", ["ac3/external-ffmpeg-5.1-448k.ac3"], [], [("AC-3", 48000, 6)], [], "mka"),
    "external-mkvmerge-eac3": ("mkvmerge", ["ac3/external-ffmpeg-eac3-5.1-256k.eac3"], [], [("E-AC-3", 48000, 6)], [], "mka"),
    "external-mkvmerge-dts": ("mkvmerge", ["dts/external-ffmpeg-5.1-48k-768k.dts"], [], [("DTS", 48000, 6)], [], "mka"),
    "external-mkvmerge-vorbis": ("mkvmerge", ["ogg/external-ffmpeg-libvorbis.ogg"], [], [("Vorbis", 44100, 2)], [], "mka"),
    "external-mkvmerge-opus": ("mkvmerge", ["ogg/external-ffmpeg-libopus.opus"], [], [("Opus", 48000, 2)], [], "mka"),
    "external-mkvmerge-opus-webm": ("mkvmerge", ["ogg/external-ffmpeg-libopus.opus"], ["--webm"], [("Opus", 48000, 2)],
                                    [("matroska.webm-timestamp-scale", "20832")], "webm"),
    "external-mkvmerge-wavpack": ("mkvmerge", ["wavpack/external-ffmpeg-apev2-tag.wv"], [], [("WavPack", 44100, 2)], [NOT_READ], "mka"),
    "external-mkvmerge-zlib": ("mkvmerge", ["ac3/external-ffmpeg-5.1-448k.ac3"], ["--compression", "0:zlib"], [("AC-3", 48000, 6)],
                               [("matroska.track-encoding-not-read", "zlib compression")], "mka"),
    "external-mkvmerge-no-cues": ("mkvmerge", ["ogg/external-ffmpeg-libopus.opus"], ["--no-cues", "--cluster-length", "100ms"], [("Opus", 48000, 2)], [], "mka"),
    "external-mkvmerge-timestamp-scale": ("mkvmerge", ["ac3/external-ffmpeg-5.1-448k.ac3"], ["--timestamp-scale", "100000"], [("AC-3", 48000, 6)], [], "mka"),
    "external-mkvmerge-two-tracks": ("mkvmerge", ["ac3/external-ffmpeg-5.1-448k.ac3", "ogg/external-ffmpeg-libopus.opus"],
                                     ["--track-name", "0:Surround", "--language", "0:eng"], [("AC-3", 48000, 6), ("Opus", 48000, 2)], [], "mka"),
    "external-mkvmerge-metadata": ("mkvmerge", ["ogg/external-ffmpeg-libopus.opus"],
                                   ["--title", "A title", "--track-name", "0:A track", "--language", "0:fra", "--chapters", "chapters.txt", "--global-tags", "tags.xml",
                                    "--attach-file", "cover.jpg", "--attachment-mime-type", "image/jpeg"], [("Opus", 48000, 2)],
                                   [], "mka"),
}


def signal_wav(path):
    """One second of a 440 Hz sine, 16 bit mono at 44.1 kHz."""
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(44100)
        w.writeframes(b"".join(struct.pack("<h", int(12000 * math.sin(2 * math.pi * 440 * i / 44100))) for i in range(44100)))


def helper_files(work):
    work = pathlib.Path(work)
    signal_wav(work / "sine.wav")
    (work / "cover.jpg").write_bytes(bytes([0xFF, 0xD8, 0xFF, 0xE0]) + bytes(range(256)) * 2 + bytes([0xFF, 0xD9]))
    (work / "chapters.ffmetadata").write_text(
        ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=700\ntitle=First\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=700\nEND=1400\ntitle=Second\n", encoding="utf-8")
    (work / "chapters.txt").write_text("CHAPTER01=00:00:00.000\nCHAPTER01NAME=First\nCHAPTER02=00:00:00.700\nCHAPTER02NAME=Second\n", encoding="utf-8")
    (work / "tags.xml").write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n<Tags><Tag><Targets/><Simple><Name>ARTIST</Name><String>An artist</String></Simple>'
        '<Simple><Name>TITLE</Name><String>A title</String></Simple></Tag></Tags>\n', encoding="utf-8")


def path_of(name, work):
    if name == "sine":
        return str(pathlib.Path(work) / "sine.wav")
    if name == "testsrc":
        return "testsrc"
    candidate = FIXTURES_DIR / name
    return str(candidate if candidate.exists() else pathlib.Path(work) / name)


def version(tool):
    if tool == "ffmpeg":
        return subprocess.run([FFMPEG, "-version"], capture_output=True, text=True).stdout.split()[2]
    return subprocess.run([MKVMERGE, "--version"], capture_output=True, text=True).stdout.split("\n")[0].split(" (")[0]


def encode(name, tool, inputs, arguments, extension, work):
    target = OUT / f"{name}.{extension}"
    if tool == "ffmpeg":
        command = [FFMPEG, "-v", "error", "-y", "-bitexact"]
        mapped = False
        for item in inputs:
            if item == "testsrc":
                command += ["-f", "lavfi", "-i", "testsrc=duration=1:size=64x48:rate=10"]
            elif item.endswith(".ffmetadata"):
                command += ["-f", "ffmetadata", "-i", path_of(item, work)]
            elif item == "cover.jpg":
                continue
            else:
                command += ["-i", path_of(item, work)]
        command += [a if a != "cover.jpg" else path_of("cover.jpg", work) for a in arguments]
        command += [str(target)]
        subprocess.run(command, check=True, cwd=work)
        return {"tool": f"ffmpeg {version('ffmpeg')}", "arguments": arguments, "inputs": inputs}
    command = [MKVMERGE, "-o", str(target)]
    arguments = list(arguments)
    resolved = [path_of(a, work) if a in ("chapters.txt", "tags.xml", "cover.jpg") else a for a in arguments]
    command += resolved
    for item in inputs:
        command.append(path_of(item, work))
    subprocess.run(command, check=True, capture_output=True, cwd=work)
    return {"tool": f"mkvmerge {version('mkvmerge')}", "arguments": arguments, "inputs": inputs}


def manifest(name, encoder, streams, findings, extension):
    data = (OUT / f"{name}.{extension}").read_bytes()
    result = {"description": f"{encoder['tool']} {' '.join(encoder['arguments'])}".strip()}
    result.update(m.manifest(data))
    result["encoder"] = encoder
    result["streams"] = [{"codec": c, "sample_rate": r, "channels": n} for c, r, n in streams]
    result["findings"] = [{"code": code, "contains": contains} for code, contains in findings]
    result["crosscheck"] = {"valid": True}
    return result


def main():
    only_manifests = "--manifests-only" in sys.argv
    if not only_manifests:
        missing = [tool for tool in (FFMPEG, MKVMERGE) if shutil.which(tool) is None]
        if missing:
            raise SystemExit(f"Missing {', '.join(missing)}; install ffmpeg and mkvtoolnix, or pass --manifests-only.")
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        if not only_manifests:
            helper_files(work)
        for name, (tool, inputs, arguments, streams, findings, extension) in FIXTURES.items():
            path = OUT / f"{name}.expected.json"
            if only_manifests:
                encoder = json.loads(path.read_text(encoding="utf-8"))["encoder"]
            else:
                encoder = encode(name, tool, inputs, arguments, extension, work)
            path.write_text(json.dumps(manifest(name, encoder, streams, findings, extension), indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"Wrote {len(FIXTURES)} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
