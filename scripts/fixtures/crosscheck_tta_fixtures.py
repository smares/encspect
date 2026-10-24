#!/usr/bin/env python3
"""Checks the TTA fixtures' manifests against independent tools.

For every manifest in tests/fixtures/tta (make_external_tta_fixtures.py, make_tta_fixtures.py), with FFmpeg 8.0.1 and MediaInfo 26.01 of Ubuntu
26.04 (the manifests were first checked with FFmpeg 6.1.1 and MediaInfo 26.01 built from source). Neither tool is a reference decoder whose
source was read for the parser (docs/design/decoders.md, decision 10): both are black boxes.

- ffprobe opens the file or refuses it. The rule written down here comes from the files FFmpeg 6.1.1 opened and refused, and is checked against
  every manifest: it opens the file when the signature is TTA1, the header's CRC-32 holds ("Header CRC error" otherwise), the seek table is
  there whole with a CRC-32 that holds ("Seek table CRC error" otherwise), the audio format is not 2 (it asks for a password) or 3 (invalid; 0 and 1 open), the
  channels, the sample rate and the samples are not 0 and the bits per sample are at most 24. A refusal is the manifest's prediction too, so
  the header's and the table's CRC-32s are confirmed as the checks a reader makes.
- Where it opens the file: the codec, the sample rate, the channels, the bits per sample and the duration (the header's samples over the
  rate) against the manifest's header. For a file whose frames are all there, its packets are the manifest's frames: one packet for each, at
  the frame's offset, of its length and of as many samples (the packet duration, in samples) as the manifest gives.
- FFmpeg decodes each encoder-written file with `-err_detect crccheck` and no error: the frames are intact. A control flips one bit of a
  frame's coded bytes in a copy of one such file, and FFmpeg must then report "CRC error": the frame's CRC-32 covers its coded bytes.
- MediaInfo, on the files whose manifest lists no TTA finding: the format, channels, sample rate, bit depth, duration and the stream size
  (the stream's bytes without the tags around it).

Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_tta_fixtures.py [name ...]
"""

import json
import pathlib
import subprocess
import sys
import tempfile

import mediainfo_tool as mi
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "tta"
SKIPS = {}


# Differences between MediaInfo and the manifest that are understood: name -> (what MediaInfo reports as the stream size, why).
STREAM_SIZE_DIFFERS = {
    "built-id3v1-lookalike-in-last-frame": (
        "722", "MediaInfo takes the 128 bytes before the end of the file for an ID3v1 tag, as a reader does that goes by the position and the signature; the seek table says they are "
               "the last frame's, so the stream is all 850 bytes (the false positive the stream's declared end prevents)"),
}


def skip(name, check, reason):
    SKIPS.setdefault((check, reason), []).append(name)


def run(arguments, **kw):
    return subprocess.run(arguments, capture_output=True, **kw)


def loads(output):
    """ffprobe's JSON, or {} where it failed and wrote only the start of a document."""
    try:
        return json.loads(output or b"{}")
    except json.JSONDecodeError:
        return {}


def find(manifest):
    stem = manifest.name[:-len(".expected.json")]
    return next(p for p in DIRECTORY.glob(stem + ".*") if not p.name.endswith(".json"))


def opens(manifest):
    """Whether ffprobe is expected to open the file, and the reason when it is not, by the rule in the description."""
    header, table = manifest["header"], manifest["table"]
    if header is None:
        return False, "no TTA1 header"
    if not header["crc_ok"]:
        return False, "Header CRC error"
    if header["format"] == 2:
        return False, "Missing password"
    if header["format"] == 3 or header["channels"] == 0 or header["rate"] == 0 or header["samples"] == 0 or header["bits"] > 24:
        return False, "invalid header field"
    if table is None or table["crc"] is None:
        return False, "seek table cut off"
    if not table["crc_ok"]:
        return False, "Seek table CRC error"
    return True, ""


def check_ffprobe(name, manifest, path, problems):
    expected, reason = opens(manifest)
    probe = run(["ffprobe", "-v", "warning", "-show_streams", "-show_format", "-of", "json", str(path)])
    document = loads(probe.stdout)
    stream = next((s for s in document.get("streams", []) if s.get("codec_type") == "audio"), None)
    if not expected:
        if stream is not None:
            problems.append(f"ffprobe opens the file; the rule predicts a refusal ({reason})")
        elif reason in ("Header CRC error", "Missing password") and reason.encode() not in probe.stderr:
            problems.append(f"ffprobe refuses the file without saying \"{reason}\": {probe.stderr.decode(errors='replace').strip()[:200]}")
        elif reason == "Seek table CRC error" and not any(m in probe.stderr for m in (b"Seek table CRC error", b"Invalid argument")):
            # FFmpeg checks the sample count against the table before the table's CRC-32 where the count is far off.
            problems.append(f"ffprobe refuses the file without saying \"{reason}\" or \"Invalid argument\": {probe.stderr.decode(errors='replace').strip()[:200]}")
        return False
    if stream is None:
        problems.append(f"ffprobe refuses the file; the rule predicts it opens: {probe.stderr.decode(errors='replace').strip()[:200]}")
        return False
    header = manifest["header"]
    for key, want in (("codec_name", "tta"), ("sample_rate", str(header["rate"])), ("channels", header["channels"])):
        if stream.get(key) != want:
            problems.append(f"ffprobe {key} is {stream.get(key)}, the manifest {want}")
    if str(stream.get("bits_per_raw_sample")) != str(header["bits"]):
        problems.append(f"ffprobe bits_per_raw_sample is {stream.get('bits_per_raw_sample')}, the manifest {header['bits']}")
    duration = float(stream.get("duration") or document.get("format", {}).get("duration") or -1)
    if abs(duration - header["samples"] / header["rate"]) > 0.001:
        problems.append(f"ffprobe duration is {duration}, the header counts {header['samples'] / header['rate']}")
    return True


def check_packets(name, manifest, path, problems):
    frames = manifest["frames"]
    if any(f["status"] not in ([], ["crc"]) for f in frames):
        skip(name, "ffprobe packets", "the data lacks frames the seek table lists, or a length is wrong, so the packets need not be the frames")
        return
    probe = run(["ffprobe", "-v", "error", "-show_packets", "-select_streams", "a:0", "-of", "json", str(path)])
    packets = loads(probe.stdout).get("packets", [])
    header = manifest["header"]
    length = header["rate"] * 256 // 245
    count = len(frames)
    if len(packets) != count:
        problems.append(f"ffprobe lists {len(packets)} packets, the manifest {count} frames")
        return
    for index, (packet, frame) in enumerate(zip(packets, frames)):
        samples = length if index < count - 1 else header["samples"] - (count - 1) * length
        if (int(packet["pos"]), int(packet["size"]), int(packet["duration"])) != (frame["offset"], frame["size"], samples):
            problems.append(f"ffprobe packet {index} is at {packet['pos']} of {packet['size']} bytes and {packet['duration']} samples; the manifest {frame['offset']}, {frame['size']}, {samples}")


def check_decode(name, path, work, problems):
    result = run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-i", str(path), "-f", "null", "-"])
    if result.returncode != 0 or result.stderr.strip():
        problems.append(f"ffmpeg does not decode the file cleanly: {result.stderr.decode(errors='replace').strip()[:200]}")


def check_control(manifest, path, work, problems):
    """A bit flipped in a frame's coded bytes is a CRC error to FFmpeg: the frame CRC-32 covers them."""
    frame = manifest["frames"][0]
    data = bytearray(path.read_bytes())
    data[frame["offset"] + frame["size"] // 2] ^= 1
    flipped = work / "flipped.tta"
    flipped.write_bytes(data)
    result = run(["ffmpeg", "-v", "warning", "-err_detect", "crccheck", "-i", str(flipped), "-f", "null", "-"])
    if b"CRC error" not in result.stderr:
        problems.append(f"ffmpeg does not report a CRC error for a flipped bit in the first frame: {result.stderr.decode(errors='replace').strip()[:200]}")


def check_mediainfo(name, manifest, path, problems):
    if any(f["code"].startswith("tta.") for f in manifest["findings"]):
        skip(name, "MediaInfo", "the fixture is damaged or unusual, so MediaInfo need not read what the manifest holds")
        return
    tracks = mi.read_or_problem(path, problems)
    if tracks is None:
        return
    audio = tracks["Audio"][0] if tracks["Audio"] else None
    if audio is None:
        problems.append("MediaInfo finds no audio stream")
        return
    header = manifest["header"]
    for key, want in (("Format", "TTA"), ("SamplingRate", str(header["rate"])), ("Channels", str(header["channels"])), ("BitDepth", str(header["bits"]))):
        if audio.get(key) != want:
            problems.append(f"MediaInfo {key} is {audio.get(key)}, the manifest {want}")
    duration = float(audio.get("Duration", -1))
    if abs(duration - header["samples"] / header["rate"]) > 0.002:
        problems.append(f"MediaInfo duration is {duration}, the header counts {header['samples'] / header['rate']}")
    stream = next(r for r in manifest["regions"] if r["name"] == "TTA")
    size = audio.get("StreamSize")
    if name in STREAM_SIZE_DIFFERS:
        reported, why = STREAM_SIZE_DIFFERS[name]
        if size != reported:
            problems.append(f"MediaInfo's stream size is {size}; the listed difference expects {reported} ({why})")
        else:
            skip(name, "MediaInfo stream size", why)
    elif size != str(stream["length"]):
        problems.append(f"MediaInfo stream size is {size}, the manifest's stream is {stream['length']} bytes")


def main():
    tool_versions.require("ffmpeg", "ffprobe")
    mi.require()
    chosen = set(a for a in sys.argv[1:] if not a.startswith("-"))
    failures = 0
    checked = 0
    with tempfile.TemporaryDirectory() as folder:
        work = pathlib.Path(folder)
        for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
            name = manifest_path.name[:-len(".expected.json")]
            if chosen and name not in chosen:
                continue
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            path = find(manifest_path)
            problems = []
            did_open = check_ffprobe(name, manifest, path, problems)
            if did_open:
                check_packets(name, manifest, path, problems)
                check_mediainfo(name, manifest, path, problems)
            else:
                skip(name, "ffprobe fields, packets and MediaInfo", "ffprobe refuses the fixture, so the tools have no figures to compare")
            if name.startswith("external-"):
                check_decode(name, path, work, problems)
                check_control(manifest, path, work, problems)
            else:
                skip(name, "FFmpeg decoding", "the frames of a hand-built fixture hold bytes in place of coded samples")
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
