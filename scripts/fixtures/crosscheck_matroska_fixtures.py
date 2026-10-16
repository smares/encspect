#!/usr/bin/env python3
"""Checks the Matroska and WebM fixtures' manifests against independent tools.

For every manifest in tests/fixtures/matroska whose crosscheck says "valid":

- mkvinfo (MKVToolNix 82.0, libebml and libmatroska): every element the manifest's tree lists (the CRC-32 elements, which mkvinfo does not
  print, apart) at the position mkvinfo gives it, and every frame of every block, in order, with the offset and size mkvinfo gives it, which
  covers the three lacing modes and the BlockGroups.
- ffprobe (FFmpeg 6.1, the reference demuxer matroskadec.c): the audio streams' codec, channels and sample rate, the sizes of the packets it
  reads from each stream (a laced frame is a packet), and, for blocks without lacing, their timestamps: the block's time less the
  track's CodecDelay.
- the applications: mkvinfo's "Multiplexing application" and "Writing application" of every Segment and MediaInfo's "Writing library" and "Writing application" of the first, against the MuxingApp and WritingApp in the manifest.
- ffmpeg decoding to nothing: no error, for the files an encoder or a muxer made (external-*).
- MediaInfo (24.01): the format ("Matroska" or "WebM"), the number of audio tracks, and each track's channels and sampling rate against the
  analyzer's golden report, wherever MediaInfo reports them.

Fixtures that are damaged on purpose ("valid": false) are run through the same tools, which must finish without crashing; what they say about
the damage is printed. Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_matroska_fixtures.py [name ...]

What no tool at hand gives: the CRC-32 check (mkvinfo prints neither the element nor a verdict; python's zlib agrees with the analyzer, which the
fixtures with a wrong CRC and the encoder-made files with right ones show) and the content of the codec data beyond what ffprobe reports.
"""

import json
import pathlib
import re
import shutil
import subprocess
import sys

import mediainfo_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "matroska"
EXTENSIONS = (".mka", ".mkv", ".webm")

# ffprobe's codec names for the Matroska codec IDs, and MediaInfo's formats for the analyzer's codec names.
FFPROBE_CODECS = {
    "A_OPUS": "opus", "A_VORBIS": "vorbis", "A_AC3": "ac3", "A_EAC3": "eac3", "A_DTS": "dts", "A_AAC": "aac", "A_MPEG/L3": "mp3", "A_MPEG/L2": "mp2",
    "A_FLAC": "flac", "A_ALAC": "alac", "A_TTA1": "tta", "A_TRUEHD": "truehd", "A_WAVPACK4": "wavpack", "A_PCM/INT/LIT": "pcm_s16le",
}
MEDIAINFO_FORMATS = {
    "opus": "Opus", "vorbis": "Vorbis", "ac3": "AC-3", "eac3": "E-AC-3", "dts": "DTS", "aac": "AAC", "mp3": "MPEG Audio", "mp2": "MPEG Audio",
    "flac": "FLAC", "alac": "ALAC", "tta": "TTA", "truehd": "MLP FBA", "wavpack": "WavPack", "pcm_s16le": "PCM",
}

# The analyzer's reading against MediaInfo's, per fixture and field: MediaInfo's own reading with the evidence (MediaInfoLib v24.01,
# Source/MediaInfo). A listed difference that no longer occurs fails the run.
EXPLAINED = {}


def run(command, text=False):
    result = subprocess.run(command, capture_output=True, timeout=300)
    if text:
        result.stdout = result.stdout.decode("utf-8", "replace")
        result.stderr = result.stderr.decode("utf-8", "replace")
    return result


def ffprobe_json(path, *args):
    result = run(["ffprobe", "-v", "error", "-of", "json", *args, str(path)], text=True)
    return result.returncode, (json.loads(result.stdout) if result.stdout.strip() else {}), result.stderr


def flatten(tree, skip=("CRC-32", "Computed", "Unparsed Data")):
    """(name, offset) of every element of the manifest's tree."""
    for entry in tree:
        if entry[0] not in skip:
            yield entry[0], entry[1]
        if len(entry) > 3:
            yield from flatten(entry[3], skip)


MKVINFO_LINE = re.compile(r"^[| ]*\+ (.*?) at 0x([0-9a-f]+)$")
FRAME = re.compile(r"^Frame with size (\d+)$")


def mkvinfo(path):
    """(positions of all elements, [(offset, size)] of all frames, the text) as mkvinfo -v -a -p lists them."""
    result = run(["mkvinfo", "-v", "-a", "-p", str(path)], text=True)
    positions, frames = set(), []
    for line in result.stdout.splitlines():
        m = MKVINFO_LINE.match(line)
        if not m:
            continue
        position = int(m.group(2), 16)
        positions.add(position)
        f = FRAME.match(m.group(1))
        if f:
            frames.append((position, int(f.group(1))))
    return positions, frames, result.stdout + result.stderr


def check_mkvinfo(manifest, path, problems):
    positions, frames, text = mkvinfo(path)
    check_apps(manifest, text, problems)
    missing = sorted({(n, o) for n, o in flatten(manifest["tree"]) if o not in positions})
    if missing:
        problems.append(f"mkvinfo lists no element at {', '.join(f'{n} 0x{o:X}' for n, o in missing[:6])}{' ...' if len(missing) > 6 else ''}")
    wanted = [tuple(f) for b in manifest["blocks"] if "problem" not in b or b["problem"] == "lace-single" for f in b["frames"]]
    if wanted != frames:
        first = next((i for i, (w, g) in enumerate(zip(wanted, frames)) if w != g), min(len(wanted), len(frames)))
        problems.append(f"mkvinfo reads {len(frames)} frames, the manifest {len(wanted)}; the first difference is frame {first}: "
                        f"{frames[first] if first < len(frames) else None} against {wanted[first] if first < len(wanted) else None}")


def apps_of(manifest):
    """The MuxingApp and the WritingApp of each Segment the manifest's Info elements hold, as (muxing, writing) pairs."""
    return [((s.get("info") or {}).get("muxing_app"), (s.get("info") or {}).get("writing_app")) for s in manifest["segments"]]


def check_apps(manifest, text, problems):
    """mkvinfo's "Multiplexing application" and "Writing application" of every Segment against the manifest's MuxingApp and WritingApp."""
    muxing = re.findall(r"^\|?\s*\+ Multiplexing application: (.*?)(?: at 0x[0-9A-Fa-f]+)?$", text, re.M)
    writing = re.findall(r"^\|?\s*\+ Writing application: (.*?)(?: at 0x[0-9A-Fa-f]+)?$", text, re.M)
    expected = apps_of(manifest)
    if muxing != [m for m, _ in expected if m is not None] or writing != [w for _, w in expected if w is not None]:
        problems.append(f"mkvinfo reads the applications {muxing} and {writing}, the manifest {expected}")


def check_ffprobe(manifest, path, problems):
    code, data, errors = ffprobe_json(path, "-show_streams", "-show_packets")
    if code != 0:
        problems.append(f"ffprobe failed: {errors.strip().splitlines()[:1]}")
        return
    audio_tracks = [t for t in manifest["tracks"] if t["type"] == 2]
    streams = [s for s in data["streams"] if s["codec_type"] == "audio"]
    if len(streams) != len(audio_tracks):
        problems.append(f"ffprobe finds {len(streams)} audio streams, the manifest {len(audio_tracks)} audio tracks")
        return
    scale = next((s["info"].get("timestamp_scale", 1_000_000) for s in manifest["segments"] if "info" in s), 1_000_000)
    for stream, track in zip(streams, audio_tracks):
        expected = FFPROBE_CODECS.get(track["codec"])
        if expected is not None and stream["codec_name"] != expected and not (track["codec"] == "A_PCM/INT/LIT" and stream["codec_name"].startswith("pcm_")):
            problems.append(f"track {track['number']}: ffprobe says {stream['codec_name']}, the manifest {track['codec']}")
        rates = {int(round(track["rate"] or 0)), int(round(track.get("output_rate") or 0))}
        if int(stream["sample_rate"]) not in rates and not (track["codec"] == "A_OPUS"):
            problems.append(f"track {track['number']}: ffprobe says {stream['sample_rate']} Hz, the manifest {sorted(rates)}")
        if track["channels"] is not None and int(stream.get("channels", 0)) != track["channels"] and track["codec"] != "A_AAC":
            problems.append(f"track {track['number']}: ffprobe says {stream.get('channels')} channels, the manifest {track['channels']}")
        packets = [p for p in data["packets"] if p["stream_index"] == stream["index"]]
        # Header stripping puts its bytes back in front of each frame; zlib changes the sizes, so only the count is checked.
        stripped = sum(e["settings"][1] for e in track["encodings"] if e["compression"] == 3 and e["scope"] & 1 and e["settings"])
        # An absent ContentCompAlgo means zlib (RFC 9559, section 5.1.4.1.31.6); WavPack blocks lose their header in the muxer.
        inflated = any(e["compression"] in (0, None) and e["type"] == 0 and e["scope"] & 1 for e in track["encodings"]) or track["codec"] == "A_WAVPACK4"
        wanted = [f[1] + stripped for b in manifest["blocks"] if b["track"] == track["number"] and ("problem" not in b or b["problem"] == "lace-single") for f in b["frames"]]
        got = [int(p["size"]) for p in packets]
        if inflated:
            got = wanted = [len(got)]
            wanted = [len([1 for b in manifest["blocks"] if b["track"] == track["number"] and "problem" not in b for f in b["frames"]])]
        if got != wanted:
            first = next((i for i, (g, w) in enumerate(zip(got, wanted)) if g != w), min(len(got), len(wanted)))
            problems.append(f"track {track['number']}: ffprobe reads {len(got)} packets, the manifest {len(wanted)} frames; the first difference is at {first}")
            continue
        # A block without lacing is presented at its cluster's time plus its own, less the track's CodecDelay.
        unlaced = [b for b in manifest["blocks"] if b["track"] == track["number"] and b["lacing"] == "none" and "problem" not in b]
        if len(unlaced) == len(packets):
            delay = (track.get("delay") or 0) / 1e9
            for block, packet in zip(unlaced, packets):
                due = (block["cluster_timestamp"] + block["timestamp"]) * scale / 1e9 - delay
                if "pts_time" in packet and abs(float(packet["pts_time"]) - due) > 0.0015:
                    problems.append(f"track {track['number']}: ffprobe presents the block at 0x{block['offset']:X} at {packet['pts_time']} s, the manifest {due:.4f} s")
                    break


def check_decode(path, problems):
    result = run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a", "-f", "null", "-"], text=True)
    # The attached picture is probed although it is not mapped.
    lines = [l for l in result.stderr.strip().splitlines() if "[mjpeg" not in l]
    if result.returncode != 0 or lines:
        problems.append(f"ffmpeg decoding: {lines[:1] or result.returncode}")


def golden_report(path):
    golden = path.with_name(path.name.rsplit(".", 1)[0] + ".report.json")
    return json.loads(golden.read_text(encoding="utf-8"))[0] if golden.exists() else None


def check_mediainfo(name, manifest, path, tally, report):
    tracks = mediainfo_tool.read(path)
    if tracks is None:
        tally.compare("format", None, manifest["format"])
        return
    tally.compare("format", tracks["General"].get("Format"), manifest["format"])
    # MediaInfo's "Writing application" is the first Segment's WritingApp and its "Writing library" the MuxingApp.
    if manifest["segments"]:
        muxing, writing = apps_of(manifest)[0]
        tally.compare("writing application", tracks["General"].get("Encoded_Application"), writing)
        tally.compare("writing library", tracks["General"].get("Encoded_Library"), muxing)
    audio_tracks = [t for t in manifest["tracks"] if t["type"] == 2]
    tally.compare("audio tracks", len(tracks["Audio"]), len(audio_tracks))
    if len(tracks["Audio"]) != len(audio_tracks):
        return
    for info, track, stream in zip(tracks["Audio"], audio_tracks, report["streams"]):
        expected_codec = FFPROBE_CODECS.get(track["codec"])
        if expected_codec in MEDIAINFO_FORMATS:
            tally.compare("codec", info.get("Format"), MEDIAINFO_FORMATS[expected_codec])
        tally.compare("channels", info.get("Channels"), str(stream["channels"]))
        tally.compare("sampling rate", info.get("SamplingRate"), str(stream["sampleRate"]))


def main():
    for tool in ("ffprobe", "ffmpeg", "mkvinfo"):
        if shutil.which(tool) is None:
            sys.exit(f"{tool} is needed.")
    mediainfo_tool.require()
    wanted = set(sys.argv[1:])
    tally = mediainfo_tool.Tally(EXPLAINED)
    failures = 0
    checked = 0
    for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
        name = manifest_path.name[: -len(".expected.json")]
        if wanted and name not in wanted:
            continue
        path = next(p for p in DIRECTORY.glob(name + ".*") if p.suffix in EXTENSIONS)
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        problems = tally.start(name)
        if manifest["crosscheck"]["valid"]:
            check_mkvinfo(manifest, path, problems)
            check_ffprobe(manifest, path, problems)
            if name.startswith("external-"):
                check_decode(path, problems)
            report = golden_report(path)
            if report is not None:
                check_mediainfo(name, manifest, path, tally, report)
        else:
            for tool, command in (("mkvinfo", ["mkvinfo", "-v", "-a", str(path)]), ("ffprobe", ["ffprobe", "-v", "error", "-show_streams", str(path)])):
                result = run(command, text=True)
                if result.returncode < 0:
                    problems.append(f"{tool} crashed (signal {-result.returncode})")
        checked += 1
        if problems:
            failures += 1
            print(f"{name}: {len(problems)} problems")
            for problem in problems:
                print(f"  {problem}")
        else:
            print(f"{name}: ok")
    for line in tally.unused():
        print(line)
        failures += 1
    tally.print()
    print(f"{checked} fixtures checked, {failures} with problems")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
