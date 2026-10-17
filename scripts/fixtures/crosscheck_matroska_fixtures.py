#!/usr/bin/env python3
"""Checks the Matroska and WebM fixtures' manifests against independent tools.

For every manifest in tests/fixtures/matroska whose crosscheck says "valid":

- mkvinfo (MKVToolNix 97.0, libebml and libmatroska): every element the manifest's tree lists (the CRC-32 elements, which mkvinfo does not
  print, apart) at the position mkvinfo gives it, and every frame of every block, in order, with the offset and size mkvinfo gives it, which
  covers the three lacing modes and the BlockGroups.
- ffprobe (FFmpeg 8.0, the reference demuxer matroskadec.c): the audio streams' codec, channels and sample rate, the sizes of the packets it
  reads from each stream (a laced frame is a packet), and, for blocks without lacing, their timestamps: the block's time less the
  track's CodecDelay.
- the applications: mkvinfo's "Multiplexing application" and "Writing application" of every Segment and MediaInfo's "Writing library" and "Writing application" of the first, against the MuxingApp and WritingApp in the manifest.
- the protection, on every fixture (those without a ContentEncryption are the negative control): mkvinfo's order, scope, type and encryption
  algorithm of each track's ContentEncodings against the manifest's, and the golden report's protection against what RFC 9559 (section
  5.1.4.1.31.8 and following) makes of the manifest's encodings: a type 1 encoding whose algorithm is not 0 protects, its samples are encrypted
  when its scope covers the frames (bit 1, or bit 4 over an encoding that covers them), and its scheme names the algorithm and, for AES, the cipher mode. mkvinfo 82.0 listed
  ContentEncAESSettings as an unknown element and the fields this script reads (order, scope, type, algorithm) do not include the cipher mode, which
  97.0 may print since its libmatroska knows the element (KaxSemantic.cpp), so the cipher mode comes from the manifest, which the independent reader of
  matroska_model.py wrote. MediaInfo 26.01 prints no Encryption field for a Matroska track (File_Mk.cpp fills none; the ContentEncryption elements are only
  named, ATO2), which is compared as absent, so the script fails when a later version starts to report one. A report with another number of streams than the manifest has audio
  tracks is not compared, and the script prints why.
- ffmpeg decoding to nothing: no error, for the files an encoder or a muxer made (external-*).
- MediaInfo (26.01): the format ("Matroska" or "WebM"), the number of audio tracks, and each track's channels and sampling rate against the
  analyzer's golden report, wherever MediaInfo reports them.

Fixtures that are damaged on purpose ("valid": false) are run through the same tools, which must finish without crashing; what they say about
the damage is printed. Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_matroska_fixtures.py [name ...]

What no tool at hand gives: the CRC-32 check (mkvinfo prints neither the element nor a verdict; python's zlib agrees with the analyzer, which the
fixtures with a wrong CRC and the encoder-made files with right ones show) and the content of the codec data beyond what ffprobe reports.
"""

import json
import os
import pathlib
import re
import shutil
import subprocess
import sys

import check_matroska_encodings
import mediainfo_tool
import tool_versions

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

# The analyzer's reading against MediaInfo's, per fixture and field: MediaInfo's own reading with the evidence (MediaInfoLib v26.01,
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


# The fixtures whose protection could not be compared with a tool or the report, for the summary.
NOT_COMPARED = []


def mkvinfo_encodings(text):
    """The ContentEncodings of every track mkvinfo lists, in order: [[(order, scope, type, encryption algorithm or None)]] per track."""
    tracks = []
    for line in text.splitlines():
        if re.match(r"^\| \+ Track(?: at 0x[0-9a-f]+)?$", line):
            tracks.append([])
        elif not tracks:
            continue
        elif re.match(r"^\|\s+\+ Content encoding(?: at 0x[0-9a-f]+)?$", line):
            tracks[-1].append([0, 1, 0, None])  # mkvinfo prints no element that has its default
        elif tracks[-1] and (m := re.match(r"^\|\s+\+ (Order|Scope|Type|Encryption algorithm): (\d+)", line)):
            tracks[-1][-1][("Order", "Scope", "Type", "Encryption algorithm").index(m.group(1))] = int(m.group(2))
    return [[tuple(e) for e in track] for track in tracks]


def check_protection(name, manifest, path, problems, tally, report):
    """mkvinfo's ContentEncodings against the manifest's, the golden report's protection against the RFC's reading of them, and MediaInfo's Encryption."""
    _, _, text = mkvinfo(path)
    wanted = [[(e["order"], e["scope"], e["type"], e["encryption"]) for e in t["encodings"]] for t in manifest["tracks"]]
    got = mkvinfo_encodings(text)
    if len(got) != len(wanted):
        print(f"{name}: mkvinfo's ContentEncodings not compared: it lists {len(got)} tracks, the manifest {len(wanted)}, the file being damaged")
        NOT_COMPARED.append(name)
    elif got != wanted:
        problems.append(f"mkvinfo reads the ContentEncodings {got}, the manifest {wanted}")
    audio = [t for t in manifest["tracks"] if t["type"] == 2]
    if report is not None and len(report["streams"]) != len(audio):
        print(f"{name}: the report's protection not compared: it has {len(report['streams'])} streams, the manifest {len(audio)} audio tracks")
        NOT_COMPARED.append(name)
    elif report is not None:
        for track, stream in zip(audio, report["streams"]):
            expected = check_matroska_encodings.reference_protection(track["codec"], track["private"] is not None, track["encodings"])
            protection = stream.get("protection")
            actual = (protection["scheme"], protection["isEncrypted"]) if protection else None
            if actual != expected:
                problems.append(f"track {track['number']}: the report's protection is {actual}, the reference model gives {expected}")
    tracks = mediainfo_tool.read(path)
    if tracks is None or len(tracks["Audio"]) != len(audio):
        tally.skip("encryption", "MediaInfo reads no tracks or other audio tracks than the manifest, the file being damaged")
        return
    for info in tracks["Audio"]:
        tally.compare("encryption", info.get("Encryption"), None)


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


OPUS_FLUSH = re.compile(r"^\[opus @ [0-9a-fx]+\] Error parsing Opus packet header\.$")


def check_decode(path, problems, opus_tracks=0):
    result = run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a", "-f", "null", "-"], text=True)
    # The attached picture is probed although it is not mapped.
    lines = [l for l in result.stderr.strip().splitlines() if "[mjpeg" not in l]
    # FFmpeg 8.0's Opus parser checks the empty packet that flushes it at the end of the stream (libavcodec/opus/parser.c, opus_parse: for a demuxer that
    # sets PARSER_FLAG_COMPLETE_FRAMES it calls set_frame_duration whatever buf_size is, and ff_opus_parse_packet refuses a packet of 0 bytes); 6.1.1's
    # opus_find_frame_end returned for an empty buffer before parsing. The message is that flush, once for each Opus track, and no packet is lost.
    # Exactly one line for each Opus track is required, so the entry expires if a later FFmpeg stops printing it (a real "Error parsing" would otherwise
    # hide behind it).
    flushes = [l for l in lines if OPUS_FLUSH.match(l)]
    if len(flushes) != opus_tracks:
        problems.append(f"ffmpeg prints {len(flushes)} Opus flush messages for {opus_tracks} Opus tracks; FFmpeg 8.0.1 prints one for each (opus/parser.c)")
    else:
        lines = [l for l in lines if not OPUS_FLUSH.match(l)]
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
    tool_versions.require("ffmpeg", "ffprobe", "mkvinfo")
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
                check_decode(path, problems, sum(1 for t in manifest["tracks"] if t["type"] == 2 and t["codec"] == "A_OPUS"))
            report = golden_report(path)
            if report is not None:
                check_mediainfo(name, manifest, path, tally, report)
        else:
            for tool, command in (("mkvinfo", ["mkvinfo", "-v", "-a", str(path)]), ("ffprobe", ["ffprobe", "-v", "error", "-show_streams", str(path)])):
                result = run(command, text=True)
                if result.returncode < 0:
                    problems.append(f"{tool} crashed (signal {-result.returncode})")
        check_protection(name, manifest, path, problems, tally, golden_report(path))
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
    print(f"{len(NOT_COMPARED)} protection comparisons skipped (listed above)")
    # The analyzer's reading of random and tied encodings against the model needs the command line app (dotnet build src/Encspect.Cli -c Release); a missing
    # app fails the run, and the check is skipped only when ENCSPECT_SKIP_ENCODINGS is set.
    if os.environ.get("ENCSPECT_SKIP_ENCODINGS"):
        print("random encodings not checked: ENCSPECT_SKIP_ENCODINGS is set")
    elif not check_matroska_encodings.CLI_DLL.exists():
        print(f"random encodings not checked, and that fails the run: {check_matroska_encodings.CLI_DLL} is not built (dotnet build src/Encspect.Cli -c Release)")
        failures += 1
    elif subprocess.run([sys.executable, str(pathlib.Path(__file__).with_name("check_matroska_encodings.py"))]).returncode != 0:
        failures += 1
    print(f"{checked} fixtures checked, {failures} with problems")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
