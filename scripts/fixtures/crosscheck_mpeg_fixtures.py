#!/usr/bin/env python3
"""Checks the MPEG fixtures' expected results against independent decoders.

For every fixture in tests/fixtures/mpeg, layer3 and layer2 whose manifest has
"crosscheck": true, ffprobe must report exactly the frames the manifest lists (offset and length
of each), and for protected frames libmad (madplay) must report a CRC failure for exactly the
frames the manifest expects one for. libmad is used for CRCs because ffmpeg 6.1 rejects every
Layer I CRC, correct or not. With "crosscheck": "madplay" or "mpg123", that decoder's count of
decoded frames is compared instead, where ffmpeg resyncs differently or cannot read the stream at
all (free format; libmad also skips the first frame of a LAME free-format stream, so mpg123 counts
those).

Layer II and III fixtures are checked whatever their "crosscheck" value:

- "silent": ffmpeg, mpg123 and libmad (without dither) decode the file to all-zero samples and
  report no error, which only happens when every granule's bits, or every Layer II allocation,
  scalefactor and sample, start and end where the fixture says.
- "scale_info" per Layer II frame: minimp3 reads exactly this allocation, scfsi and these
  scalefactors.
- "mctoolamed": the multichannel decoder decodes every channel to silence with exactly this many
  multichannel CRC errors.
- "mp3guessenc_multichannel": the multichannel parameters mp3guessenc prints.
- "side_info" per frame: minimp3 reads exactly these values (minimp3_side_info.c), or rejects the
  frame where the manifest says it does. Its reservoir before each frame must match, except where
  minimp3 resets or discards it: after a gap, after a frame it rejects, and after a frame whose
  main data it could not find. Frames minimp3 skips before it locks on are only allowed before a
  gap or a stream change.
- "mp3guessenc": the statistics mp3guessenc prints about the stream.

External fixtures list their encoder settings instead of every frame; for them, ffprobe's sample
rate, channel count and bitrate must match those settings, and minimp3 and mp3guessenc must still
report what the manifest recorded.

Needs ffprobe, madplay, mpg123, mp3guessenc, curl, make and a C compiler on the PATH. Exits
non-zero when anything disagrees.
"""

import json
import pathlib
import re
import subprocess
import sys

import mctoolamed_tool
import minimp3_tool
from conformance_mpeg_audio import guessenc as guessenc_multichannel

ROOT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures"
DIRECTORIES = [ROOT / "mpeg", ROOT / "layer3", ROOT / "layer2"]
CHANNELS = {"Mono": 1}
GUESSENC_MODES = {"Simple stereo": "0", "Intensity stereo": "1", "Mid-side stereo": "2", "Intensity and mid-side stereo": "3"}
GUESSENC_BLOCKS = {"Long block": "long", "Switch block": "switch", "Short block": "short", "Mixed block": "mixed"}


def ffprobe_frames(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "frame=pkt_pos,pkt_size", "-of", "json", str(path)],
        capture_output=True,
        text=True,
        check=True,
    )
    return [(int(f["pkt_pos"]), int(f["pkt_size"])) for f in json.loads(result.stdout).get("frames", [])]


def ffprobe_stream(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "stream=sample_rate,channels,bit_rate", "-of", "json", str(path)],
        capture_output=True,
        text=True,
        check=True,
    )
    return json.loads(result.stdout)["streams"][0]


def madplay_output(path):
    result = subprocess.run(["madplay", "-v", "-o", "null:/dev/null", str(path)], capture_output=True, text=True)
    return (result.stdout + result.stderr).replace("\r", "\n").splitlines()


def madplay_crc_failures(path):
    return sum("CRC check failed" in line for line in madplay_output(path))


def mpg123_frame_count(path, samples_per_frame, channels):
    pcm = subprocess.run(["mpg123", "-q", "-s", str(path)], capture_output=True, check=True).stdout
    return len(pcm) // (samples_per_frame * channels * 2)


def frame_shape(fields):
    layer = fields["Header/Layer"]
    samples = 384 if layer == "I" else 576 if layer == "III" and fields["Header/Version"] != "MPEG-1" else 1152
    return samples, 1 if fields["Header/Channel Mode"] == "Mono" else 2


def madplay_frame_count(path):
    for line in madplay_output(path):
        if " frames decoded" in line:
            return int(line.split()[0])
    return 0


def guessenc_summary(path):
    """The Layer III statistics mp3guessenc prints, in the shape make_layer3_fixtures.py writes."""
    # The exit status is the guessed encoder, not an error.
    output = subprocess.run(["mp3guessenc", "-e", str(path)], capture_output=True, text=True).stdout
    summary = {"blocks": {"long": 0, "switch": 0, "short": 0, "mixed": 0}, "mode_extensions": {}, "global_gain": {},
               "ancillary": [0, 0, 0]}
    gains = {}
    for line in output.splitlines():
        key, _, value = (part.strip() for part in line.partition(":"))
        number = value.split()[0] if value else ""
        if key in GUESSENC_MODES:
            summary["mode_extensions"][GUESSENC_MODES[key]] = int(number)
        elif key.removesuffix(" granules") in GUESSENC_BLOCKS:
            summary["blocks"][GUESSENC_BLOCKS[key.removesuffix(" granules")]] = int(number)
        elif key in ("Min global gain", "Max global gain"):
            for channel, gain in re.findall(r"([lrc])=\s*(\d+)", value):
                gains.setdefault("1" if channel == "r" else "0", []).append(int(gain))
        elif key == "Max reservoir":
            summary["max_main_data_begin"] = int(number)
        elif key in ("Total amount", "Min packet", "Max packet"):
            summary["ancillary"][["Total amount", "Min packet", "Max packet"].index(key)] = int(number)
        elif key == "Scalefactor scaling used":
            summary["scalefac_scale_used"] = value == "yes"
        elif key == "Scalefactor selection information used":
            summary["scfsi_used"] = value == "yes"
    summary["global_gain"] = {channel: sorted(values) for channel, values in sorted(gains.items())}
    return summary


def decodes_to_silence(path):
    problems = []
    commands = {
        "ffmpeg": ["ffmpeg", "-v", "error", "-i", str(path), "-f", "s16le", "-"],
        "mpg123": ["mpg123", "-q", "-s", str(path)],
        "madplay": ["madplay", "-q", "--no-dither", "--stereo", "-o", "raw:-", str(path)],
    }
    for name, command in commands.items():
        result = subprocess.run(command, capture_output=True)
        if not result.stdout or any(result.stdout) or result.stderr.strip():
            problems.append(f"{name} does not decode to silence without errors: {result.stderr.decode()[:200].strip()}")
    return problems


def check_side_info(path, manifest, dumper):
    problems = []
    rows = {row["offset"]: row for row in minimp3_tool.side_info(dumper, path)}
    frames = manifest["frames"]
    gap_ends = {gap["offset"] + gap["length"] for gap in manifest["gaps"]}
    breaks = [i for i in range(1, len(frames)) if frames[i]["offset"] in gap_ends
              or frames[i]["fields"]["Header/Sample Rate"] != frames[i - 1]["fields"]["Header/Sample Rate"]]
    previous = None
    for i, frame in enumerate(frames):
        row = rows.get(frame["offset"])
        if row is None:
            if not breaks or i >= breaks[-1]:
                problems.append(f"minimp3 does not decode the frame at {frame['offset']}")
            previous = None
            continue
        actual = {key: row[key] for key in ("main_data_begin", "granules") if key in row}
        if actual != frame["side_info"]:
            problems.append(f"minimp3 reads different side information at {frame['offset']}: {json.dumps(actual)[:300]}")
        discarded = previous is None or previous["main_data_begin"] < 0 or previous["reservoir"] < previous["main_data_begin"]
        if frame["offset"] not in gap_ends and not discarded and row["reservoir"] != min(frame["reservoir"], 511):
            problems.append(f"minimp3 has {row['reservoir']} bytes in the reservoir at {frame['offset']}, the manifest {frame['reservoir']}")
        previous = row
    return problems


def check_scale_info(path, manifest, dumper):
    """Layer II allocation, scfsi and scalefactors as minimp3 reads them; scalefactors as floats."""
    rows = {row["offset"]: row for row in minimp3_tool.side_info(dumper, path)}
    normal = lambda info: (info["bands"], info["bound"], info["scale_info_bits"], [
        [{k: [float.fromhex(s) for s in v] if k == "scalefactors" else v for k, v in channel.items()} for channel in subband]
        for subband in info["subbands"]])
    problems = []
    for frame in manifest["frames"]:
        row = rows.get(frame["offset"])
        if row is None or normal(row) != normal(frame["scale_info"]):
            problems.append(f"minimp3 reads different allocation, scfsi or scalefactors at {frame['offset']}")
    return problems


def check(path, manifest, tools):
    dumper, decoder = tools
    problems = []
    if manifest.get("silent"):
        problems += decodes_to_silence(path)
    if manifest.get("frames") and "scale_info" in manifest["frames"][0]:
        problems += check_scale_info(path, manifest, dumper)
    if "mctoolamed" in manifest:
        errors, silent, channels = mctoolamed_tool.decode(decoder, path)
        if errors != manifest["mctoolamed"]["crc_errors"] or not silent:
            problems.append(f"mctoolamed reports {errors} multichannel CRC errors, silent {silent}, {channels} channels")
    if "mp3guessenc_multichannel" in manifest and guessenc_multichannel(path) != manifest["mp3guessenc_multichannel"]:
        problems.append(f"mp3guessenc reports {guessenc_multichannel(path)}, the manifest {manifest['mp3guessenc_multichannel']}")
    if manifest.get("frames") and "side_info" in manifest["frames"][0]:
        problems += check_side_info(path, manifest, dumper)
    if "minimp3_side_info" in manifest and minimp3_tool.side_info(dumper, path) != manifest["minimp3_side_info"]:
        problems.append("minimp3 reads different side information than the manifest recorded")
    if "mp3guessenc" in manifest:
        expected = dict(manifest["mp3guessenc"])
        expected["mode_extensions"] = {k: v for k, v in expected["mode_extensions"].items() if k != "None"}
        actual = guessenc_summary(path)
        if actual != expected:
            problems.append(f"mp3guessenc reports {json.dumps(actual)}, the manifest {json.dumps(expected)}")
    if not manifest.get("crosscheck"):
        return problems

    if manifest["crosscheck"] in ("madplay", "mpg123"):
        if manifest["crosscheck"] == "madplay":
            count = madplay_frame_count(path)
        else:
            count = mpg123_frame_count(path, *frame_shape(manifest["frames"][0]["fields"]))
        if count != len(manifest["frames"]):
            problems.append(f"{manifest['crosscheck']} decodes {count} frames, the manifest lists {len(manifest['frames'])}")
        return problems

    if "frames" in manifest:
        expected = [(f["offset"], f["length"]) for f in manifest["frames"] if not f.get("truncated")]
        try:
            actual = ffprobe_frames(path)
        except subprocess.CalledProcessError as error:
            return [f"ffprobe failed: {error.stderr.strip()[:200]}"]
        # ffprobe drops a truncated final frame, which the manifest marks.
        if actual != expected:
            problems.append(f"ffprobe frames {actual[:4]}... ({len(actual)}) differ from the manifest {expected[:4]}... ({len(expected)})")

        protected = [f for f in manifest["frames"] if f["fields"].get("Header/IsProtected") == "true"]
        if protected:
            expected_failures = sum(f["fields"]["CRC"] != f["fields"]["CRC/Computed"] for f in protected)
            failures = madplay_crc_failures(path)
            if failures != expected_failures:
                problems.append(f"madplay reports {failures} CRC failures, the manifest expects {expected_failures}")

    if "encoder" in manifest:
        settings = manifest["encoder"]
        if settings.get("free_format"):
            count = mpg123_frame_count(path, 1152, CHANNELS.get(settings["channel_mode"], 2))
            if count != manifest["mpg123_frames"]:
                problems.append(f"mpg123 decodes {count} frames, the manifest recorded {manifest['mpg123_frames']}")
        else:
            if ffprobe_frames(path) != [tuple(f) for f in manifest["ffprobe_frames"]]:
                problems.append("ffprobe's frames differ from the ones recorded in the manifest")
            stream = ffprobe_stream(path)
            if int(stream["sample_rate"]) != settings["sample_rate"]:
                problems.append(f"ffprobe sample rate {stream['sample_rate']} differs from {settings['sample_rate']}")
            if int(stream["channels"]) != CHANNELS.get(settings["channel_mode"], 2):
                problems.append(f"ffprobe channels {stream['channels']} differ from {settings['channel_mode']}")
            if settings.get("bitrate") and int(stream["bit_rate"]) != settings["bitrate"] * 1000:
                problems.append(f"ffprobe bitrate {stream['bit_rate']} differs from {settings['bitrate']} kbps")
        if settings.get("protected") and madplay_crc_failures(path) != 0:
            problems.append("madplay reports CRC failures in an encoder-protected stream")

    return problems


def main():
    failed = 0
    checked = 0
    tools = (minimp3_tool.build(), mctoolamed_tool.build())
    for manifest_path in sorted(p for directory in DIRECTORIES for p in directory.glob("*.expected.json")):
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        name = manifest_path.name[: -len(".expected.json")]
        path = next(p for p in manifest_path.parent.glob(name + ".*") if not p.name.endswith(".json"))
        problems = check(path, manifest, tools)
        checked += 1
        for problem in problems:
            failed += 1
            print(f"{path.name}: {problem}")
    print(f"Cross-checked {checked} fixtures, {failed} problems.", file=sys.stderr)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
