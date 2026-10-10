#!/usr/bin/env python3
"""Checks the MPEG fixtures' expected results against independent decoders.

For every fixture in tests/fixtures/mpeg whose manifest has "crosscheck": true, ffprobe must
report exactly the frames the manifest lists (offset and length of each), and for protected
frames libmad (madplay) must report a CRC failure for exactly the frames the manifest expects one
for. libmad is used for CRCs because ffmpeg 6.1 rejects every Layer I CRC, correct or not. With
"crosscheck": "madplay" or "mpg123", that decoder's count of decoded frames is compared instead,
where ffmpeg resyncs differently or cannot read the stream at all (free format; libmad also
skips the first frame of a LAME free-format stream, so mpg123 counts those).

External fixtures list their encoder settings instead of every frame; for them, ffprobe's sample
rate, channel count and bitrate must match those settings.

Needs ffprobe, madplay and mpg123 on the PATH. Exits non-zero on the first disagreement.
"""

import json
import pathlib
import subprocess
import sys

FIXTURES = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "mpeg"
CHANNELS = {"Mono": 1}


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


def check(path, manifest):
    problems = []
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
    for manifest_path in sorted(FIXTURES.glob("*.expected.json")):
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if not manifest.get("crosscheck"):
            continue
        name = manifest_path.name[: -len(".expected.json")]
        path = next(p for p in FIXTURES.glob(name + ".*") if not p.name.endswith(".json"))
        checked += 1
        for problem in check(path, manifest):
            failed += 1
            print(f"{path.name}: {problem}")
    print(f"Cross-checked {checked} fixtures, {failed} problems.", file=sys.stderr)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
