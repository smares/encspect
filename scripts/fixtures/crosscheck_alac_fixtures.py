#!/usr/bin/env python3
"""Checks the ALAC fixtures' manifests against independent tools.

For every manifest in tests/fixtures/alac (make_alac_fixtures.py, make_external_alac_fixtures.py), with FFmpeg 8.0.1 and MediaInfo 26.01 (tool_versions.py)
and with `alacconvert`, Apple's reference implementation built unpatched from its pinned source by alac_tool.py. FFmpeg and MediaInfo are black boxes
(docs/design/decoders.md, decision 10); alacconvert is the reference the parser follows.

- ffprobe, on the files whose manifest has no ALAC finding: the codec, the sample rate, the channels, the bits per raw sample (the cookie's bit depth), the
  duration and, for MP4, the number of packets and each packet's offset, size and duration (the frame's samples, from the stts box) against the manifest.
- MediaInfo, on the same files: the format, channels, sample rate, bit depth and the duration.
- The decoders: each file whose manifest carries the input's hashes (every undamaged hand-built file and every encoder-made one) is decoded by FFmpeg and by
  alacconvert, as the manifest's "decoders" list says (FFmpeg reads no ALAC stream with 20-bit samples or an out-of-range cookie), and the hash of each
  channel's samples must be the input's: the frames, as the manifest walks them, hold the samples that were encoded. FFmpeg reorders the channels of more than
  two to its own order, so for those the hashes are compared as sets, and the order is alacconvert's. The files of Apple's afconvert, which have no input,
  are decoded by both and the two outputs compared.
- The damaged files are not decoded: a decoder's answer to a stream that breaks the format is its own choice, not a measure of the walk.

Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_alac_fixtures.py [name ...]
"""

import hashlib
import json
import pathlib
import struct
import subprocess
import sys
import tempfile

import alac_model as m
import alac_tool
import mediainfo_tool as mi
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "alac"
SKIPS = {}
FORMATS = {2: "s16le", 3: "s24le", 4: "s32le"}


def skip(name, check, reason):
    SKIPS.setdefault((check, reason), []).append(name)


def run(arguments, **kw):
    return subprocess.run(arguments, capture_output=True, **kw)


def loads(output):
    try:
        return json.loads(output or b"{}")
    except json.JSONDecodeError:
        return {}


def find(manifest):
    stem = manifest.name[:-len(".expected.json")]
    return next(p for p in DIRECTORY.glob(stem + ".*") if not p.name.endswith(".json"))


def is_damaged(manifest):
    return any(f["code"].startswith("alac.") for f in manifest["findings"])


def check_ffprobe(name, manifest, path, problems):
    probe = run(["ffprobe", "-v", "warning", "-show_streams", "-show_format", "-show_packets", "-of", "json", str(path)])
    document = loads(probe.stdout)
    stream = next((s for s in document.get("streams", []) if s.get("codec_type") == "audio"), None)
    if stream is None:
        problems.append(f"ffprobe finds no audio stream: {probe.stderr.decode(errors='replace').strip()[:200]}")
        return
    cookie = manifest["cookie"]
    info = manifest["stream_infos"][0]
    for key, want in (("codec_name", "alac"), ("sample_rate", str(cookie["rate"])), ("channels", cookie["channels"])):
        if stream.get(key) != want:
            problems.append(f"ffprobe {key} is {stream.get(key)}, the manifest {want}")
    if str(stream.get("bits_per_raw_sample")) != str(cookie["bit_depth"]):
        problems.append(f"ffprobe bits_per_raw_sample is {stream.get('bits_per_raw_sample')}, the cookie {cookie['bit_depth']}")
    duration = float(stream.get("duration") or document.get("format", {}).get("duration") or -1)
    if manifest["container"] == "mp4" and not close(duration, manifest):
        problems.append(f"ffprobe duration is {duration}, the manifest counts {info['samples'] / cookie['rate']}")
    packets = [p for p in document.get("packets", []) if p.get("stream_index") == stream["index"]]
    frames = manifest["frames"]
    if len(packets) != len(frames):
        problems.append(f"ffprobe lists {len(packets)} packets, the manifest {len(frames)} frames")
        return
    for index, (packet, frame) in enumerate(zip(packets, frames)):
        if int(packet["size"]) != frame["size"]:
            problems.append(f"ffprobe packet {index} has {packet['size']} bytes, the manifest's frame {frame['size']}")
        if manifest["container"] == "mp4":
            if int(packet["pos"]) != frame["offset"]:
                problems.append(f"ffprobe packet {index} is at {packet['pos']}, the manifest's frame at {frame['offset']}")
            last = index == len(frames) - 1
            lasts = int(packet.get("duration", -1))
            if lasts != frame["samples"]:
                if last and lasts == cookie["frame_length"]:
                    skip(name, "ffprobe duration of the last packet", "MP4Box states a whole frame for the last packet, FFmpeg the samples it holds; either is accepted")
                else:
                    problems.append(f"ffprobe packet {index} lasts {lasts} samples, the frame holds {frame['samples']}")
    if manifest["container"] != "mp4":
        skip(name, "ffprobe duration, packet offsets and durations", "Matroska times its blocks in milliseconds, so the duration is the last block's time and a packet's duration is a rounding; the sizes are compared")


def close(duration, manifest):
    """Whether an MP4 duration is the samples' or, as MP4Box writes it, the whole frames' that hold them."""
    cookie, samples = manifest["cookie"], manifest["stream_infos"][0]["samples"]
    frames = -(-samples // cookie["frame_length"]) * cookie["frame_length"]
    return any(abs(duration - n / cookie["rate"]) <= 0.002 for n in (samples, frames))


def check_mediainfo(name, manifest, path, problems):
    tracks = mi.read_or_problem(path, problems)
    if tracks is None:
        return
    audio = tracks["Audio"][0] if tracks["Audio"] else None
    if audio is None:
        problems.append("MediaInfo finds no audio stream")
        return
    cookie = manifest["cookie"]
    info = manifest["stream_infos"][0]
    for key, want in (("Format", "ALAC"), ("SamplingRate", str(cookie["rate"])), ("Channels", str(cookie["channels"])), ("BitDepth", str(cookie["bit_depth"]))):
        if audio.get(key) == want:
            continue
        if manifest["container"] == "mp4" and key in ("Channels", "BitDepth") and cookie["length"] != 24 and audio.get(key) is None:
            skip(name, "MediaInfo channels and bit depth", "MediaInfo 26.01 shows neither for an alac box whose cookie is longer than the 24-byte configuration (it holds the channel layout info); observed on these files")
        elif manifest["container"] != "mp4" and key == "BitDepth" and audio.get(key) is None:
            skip(name, "MediaInfo bit depth", "MediaInfo 26.01 shows none for a Matroska ALAC track (mkvmerge writes no BitDepth element); the other fields are compared")
        else:
            problems.append(f"MediaInfo {key} is {audio.get(key)}, the manifest {want}")
    duration = float(audio.get("Duration", -1))
    if manifest["container"] == "mp4" and not close(duration, manifest):
        problems.append(f"MediaInfo duration is {duration}, the manifest counts {info['samples'] / cookie['rate']}")


def interleaved(raw, channels, width):
    """The channels' bytes of interleaved little-endian samples, each channel's as one bytes object."""
    step = channels * width
    return [b"".join(raw[i + c * width:i + (c + 1) * width] for i in range(0, len(raw) - step + 1, step)) for c in range(channels)]


def hashes(planes):
    return [hashlib.md5(p).hexdigest() for p in planes]


def ffmpeg_planes(path, channels, width, work):
    out = work / "ffmpeg.raw"
    out.unlink(missing_ok=True)
    fmt = FORMATS[width]
    result = run(["ffmpeg", "-v", "error", "-y", "-i", str(path), "-c:a", f"pcm_{fmt}", "-f", fmt, str(out)])
    if result.returncode != 0 or not out.exists():
        return None, result.stderr.decode(errors="replace").strip()[:200]
    return interleaved(out.read_bytes(), channels, width), ""


def alacconvert_planes(program, data, manifest, path, work):
    """The channels' samples alacconvert decodes from the file's frames, which it takes in a CAF file (it reads no MP4 file)."""
    cookie_bytes, packets, _ = (m.m4a if manifest["container"] == "mp4" else m.mka)(data)
    cookie = manifest["cookie"]
    count = manifest["stream_infos"][0]["samples"]
    caf = work / "in.caf"
    caf.write_bytes(m.caf(cookie_bytes, [data[o:o + s] for o, s in packets], cookie["rate"], cookie["channels"], cookie["bit_depth"], cookie["frame_length"], count))
    width = {16: 2, 20: 4, 24: 3, 32: 4}[cookie["bit_depth"]]
    out = work / ("out.wav" if cookie["channels"] <= 2 else "out.caf")
    out.unlink(missing_ok=True)
    result = run([str(program), str(caf), str(out)], text=True)
    if result.returncode != 0 or not out.exists():
        return None, f"alacconvert failed: {(result.stdout + result.stderr).strip()[:200]}"
    body = out.read_bytes()
    if out.suffix == ".wav":
        samples = body[body.index(b"data") + 8:]
        return interleaved(samples, cookie["channels"], width), ""
    # A CAF file of linear PCM: big-endian unless the format flags say little-endian (kCAFLinearPCMFormatFlagIsLittleEndian = 2).
    at = body.index(b"desc") + 12
    flags = struct.unpack(">I", body[at + 12:at + 16])[0]
    bytes_per_frame = struct.unpack(">I", body[at + 16:at + 20])[0]
    start = body.index(b"data") + 12 + 4
    samples = body[start:]
    width = bytes_per_frame // cookie["channels"]
    planes = interleaved(samples, cookie["channels"], width)
    if not flags & 2:
        planes = [b"".join(p[i:i + width][::-1] for i in range(0, len(p), width)) for p in planes]
    return planes, ""


def check_decoders(name, manifest, path, data, program, work, problems):
    cookie = manifest["cookie"]
    spec = manifest.get("input")
    width = spec["width"] if spec else {16: 2, 20: 4, 24: 3, 32: 4}[cookie["bit_depth"]]
    wanted = spec["decoders"] if spec else ["ffmpeg", "alacconvert"]
    got = {}
    if "ffmpeg" in wanted:
        planes, error = ffmpeg_planes(path, cookie["channels"], width, work)
        if planes is None:
            problems.append(f"ffmpeg does not decode the file: {error}")
        else:
            got["ffmpeg"] = hashes(planes)
    else:
        skip(name, "ffmpeg decoding", "the manifest lists no ffmpeg among the decoders that read this stream")
    if "alacconvert" in wanted:
        planes, error = alacconvert_planes(program, data, manifest, path, work)
        if planes is None:
            problems.append(error)
        else:
            got["alacconvert"] = hashes(planes)
    else:
        skip(name, "alacconvert decoding", "the manifest lists no alacconvert among the decoders that read this stream")
    if spec:
        expected = spec["channel_md5"]
        # FFmpeg orders the channels of more than two by its own layouts, and its encoder writes them in the order of the stream's layout, not the input's.
        encoder = manifest.get("encoder", {})
        reordered = encoder.get("tool", "").startswith("ffmpeg") or encoder.get("source", "").startswith("external-ffmpeg-")
        for tool, found in got.items():
            if cookie["channels"] > 2 and (tool == "ffmpeg" or reordered):
                if sorted(found) != sorted(expected):
                    problems.append(f"{tool} decodes channels whose hashes are not the input's (compared as a set): {found} against {expected}")
            elif found != expected:
                problems.append(f"{tool} decodes channels whose hashes are not the input's: {found} against {expected}")
    elif len(got) == 2:
        a, b = got["ffmpeg"], got["alacconvert"]
        same = sorted(a) == sorted(b) if cookie["channels"] > 2 else a == b
        if not same:
            problems.append(f"ffmpeg and alacconvert decode different samples: {a} against {b}")


def main():
    tool_versions.require("ffmpeg", "ffprobe")
    mi.require()
    chosen = set(a for a in sys.argv[1:] if not a.startswith("-"))
    failures = 0
    checked = 0
    with tempfile.TemporaryDirectory() as folder:
        work = pathlib.Path(folder)
        program = alac_tool.build(pathlib.Path(tempfile.gettempdir()) / "alac-build")
        for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
            name = manifest_path.name[:-len(".expected.json")]
            if chosen and name not in chosen:
                continue
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            path = find(manifest_path)
            problems = []
            if is_damaged(manifest) or manifest["stream_infos"] is None or not manifest["cookie"] or not manifest["cookie"]["complete"]:
                skip(name, "ffprobe, MediaInfo and the decoders", "the manifest lists a finding of the parser (a fixture that breaks the format on purpose, or a cookie with bytes after the configuration), so a tool's answer is its own choice")
            else:
                if manifest.get("crosscheck", {}).get("ffprobe") is False:
                    skip(name, "ffprobe and MediaInfo", "FFmpeg 8.0.1 reads a Matroska track's CodecPrivate as the configuration alone and refuses the older forms of the cookie (the manifest's description)")
                else:
                    check_ffprobe(name, manifest, path, problems)
                    check_mediainfo(name, manifest, path, problems)
                check_decoders(name, manifest, path, path.read_bytes(), program, work, problems)
            checked += 1
            if problems:
                failures += 1
                print(f"FAIL {name}")
                for problem in problems:
                    print(f"    {problem}")
    for (check, reason), names in sorted(SKIPS.items()):
        print(f"skipped {check} for {len(names)} fixtures: {reason}")
    print(f"{checked} fixtures checked, {failures} with disagreements.")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
