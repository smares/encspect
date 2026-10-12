#!/usr/bin/env python3
"""Checks the MP4 and raw AAC fixtures' manifests against independent tools.

For every fixture in tests/fixtures/mp4, tests/fixtures/adts and tests/fixtures/aac, its manifest's
"crosscheck" says what to confirm:

- "ffprobe": ffprobe (ffmpeg 6.1) must list exactly the manifest's access units as its audio packets,
  stream by stream and with edit lists ignored, and report each stream's sample rate and channels (unless
  "ffprobe_streams" is false, for ADTS frames of several protected raw data blocks, which ffmpeg does not
  decode, so it reports no rate). ffmpeg outputs two channels for any mono
  stream with SBR whose configuration does not rule PS out, since PS may be signaled implicitly; the
  manifest counts the channels the configuration signals, so there ffprobe's 2 is expected
  ("ffprobe_channels"); likewise ffmpeg doubles the rate of an ADTS stream whose access units carry SBR,
  which ADTS can only signal implicitly ("ffprobe_sample_rate"). Where the
  manifest records ffprobe's profile ("ffprobe_profile"), it must match: ffmpeg names a stream HE-AAC only
  once it finds SBR data in an access unit, which the hand-built ones do not carry, so those record none.
- "mp4box": MP4Box -diso (GPAC 2.2) must list the same boxes with the same sizes, in the same order and
  nesting. MP4Box writes the type of an iTunes item whose first byte is 0xA9 as "A9" and three letters.
- "mp4box_samples": where ffmpeg cannot read a track (USAC), MP4Box -dtsx must list the same sample sizes.
- "faad": faad -i (faad2 2.11) must print, for an MP4, the label, channels and sample rate the manifest
  gives: faad names the object type its reading of the configuration ends with, the core's "LC AAC" under
  hierarchical signaling and "HE AAC" where the sync extension follows the core, with the output rate
  either way; it prints 2 channels for any mono stream, since it decodes PS that a stream may signal
  implicitly, and 0 for a program config element. For
  ADTS, it must print the duration of 1024 samples per frame (whatever number of raw data blocks a frame
  holds) and the sample rate.
- "skip": the tools that are not run on the fixture, each with the reason.

The analyzer's agreement with the manifests is checked by the tests (Mp4FixtureTests). Exits non-zero
when anything disagrees.
"""

import json
import pathlib
import re
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures"
DIRECTORIES = [ROOT / "mp4", ROOT / "adts", ROOT / "aac"]
EXTENSIONS = {".m4a", ".mp4", ".mov", ".m4b", ".3gp", ".aac", ".mp3"}


def ffprobe_packets(path):
    # Every packet, also those before an edit list's start, which ffmpeg otherwise drops.
    result = subprocess.run(["ffprobe", "-v", "error", "-ignore_editlist", "1", "-select_streams", "a", "-show_entries", "packet=stream_index,pos,size", "-of", "json",
                             str(path)], capture_output=True, text=True, check=True)
    streams = {}
    for packet in json.loads(result.stdout).get("packets", []):
        streams.setdefault(int(packet["stream_index"]), []).append([int(packet["pos"]), int(packet["size"])])
    return [row for index in sorted(streams) for row in streams[index]]


def ffprobe_streams(path):
    result = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries", "stream=sample_rate,channels,profile", "-of", "json", str(path)],
                            capture_output=True, text=True, check=True)
    return json.loads(result.stdout).get("streams", [])


def mp4box_boxes(path):
    """The boxes of MP4Box's XML dump as (path, size), read tolerantly: values of binary atoms make the dump ill-formed XML."""
    with tempfile.TemporaryDirectory() as directory:
        out = pathlib.Path(directory) / "dump.xml"
        subprocess.run(["MP4Box", "-quiet", "-diso", str(path), "-out", str(out)], capture_output=True)
        if not out.exists():
            return None
        text = out.read_text(encoding="utf-8", errors="replace")
    rows = []
    stack = []
    for match in re.finditer(r"<(/?)(\w+)((?:\s+[\w:]+=\"[^\"]*\")*)\s*(/?)>", text):
        closing, tag, attributes, selfclosing = match.groups()
        if closing:
            if stack and stack[-1][0] == tag:
                stack.pop()
            continue
        size = re.search(r'\sSize="(\d+)"', attributes)
        kind = re.search(r'\sType="([^"]*)"', attributes)
        if not size or not kind:
            continue
        name = kind.group(1)
        if len(name) == 5 and name.startswith("A9"):
            name = "©" + name[2:]
        elif re.fullmatch(r"[0-9A-F]{8}", name):
            name = "".join(chr(b) if 0x20 <= b < 0x7F else "." for b in bytes.fromhex(name))
        full = "/".join([s[1] for s in stack] + [name])
        rows.append((full, int(size.group(1))))
        if not selfclosing:
            stack.append((tag, name))
    return rows


def mp4box_sample_sizes(path):
    """The sample sizes MP4Box -dtsx lists, track after track."""
    with tempfile.TemporaryDirectory() as directory:
        out = pathlib.Path(directory) / "samples.txt"
        subprocess.run(["MP4Box", "-quiet", "-dtsx", str(path), "-out", str(out)], capture_output=True)
        if not out.exists():
            return None
        lines = out.read_text(encoding="utf-8", errors="replace").splitlines()
    return [int(line.split("\t")[3]) for line in lines if line.startswith("Sample ")]


def faad_info(path):
    result = subprocess.run(["faad", "-i", str(path)], capture_output=True)
    text = (result.stdout + result.stderr).replace(b"\0", b"").decode("latin-1")
    for line in text.splitlines():
        line = line.strip()
        if re.match(r"^(LC|HE|Main|LTP|SSR|LD) AAC\s", line) or line.startswith("ADTS,"):
            return line
    return None


def main():
    failures = 0
    checked = 0
    for directory in DIRECTORIES:
        if not directory.exists():
            continue
        for path in sorted(p for p in directory.iterdir() if p.suffix in EXTENSIONS):
            manifest = json.loads((directory / (path.stem + ".expected.json")).read_text(encoding="utf-8"))
            check = manifest.get("crosscheck", {})
            problems = []
            if check.get("ffprobe"):
                packets = ffprobe_packets(path)
                if packets != manifest["units"]:
                    first = next((i for i, (a, b) in enumerate(zip(packets, manifest["units"])) if a != b), min(len(packets), len(manifest["units"])))
                    problems.append(f"ffprobe lists {len(packets)} packets, the manifest {len(manifest['units'])} access units; first difference at {first}")
                probed = ffprobe_streams(path)
                if len(probed) != len(manifest["streams"]):
                    problems.append(f"ffprobe finds {len(probed)} audio streams, the manifest {len(manifest['streams'])}")
                for expected, actual in zip(manifest["streams"] if check.get("ffprobe_streams", True) else [], probed):
                    channels = expected.get("ffprobe_channels", expected["channels"])
                    rate = expected.get("ffprobe_sample_rate", expected["sample_rate"])
                    if int(actual.get("sample_rate", 0)) != rate or int(actual.get("channels", 0)) != channels:
                        problems.append(f"ffprobe: {actual.get('sample_rate')} Hz, {actual.get('channels')} channels; expected {rate} Hz, {channels}")
                    if "ffprobe_profile" in expected and actual.get("profile") != expected["ffprobe_profile"]:
                        problems.append(f"ffprobe's profile {actual.get('profile')}, expected {expected['ffprobe_profile']}")
            if check.get("mp4box"):
                boxes = mp4box_boxes(path)
                # MP4Box reads one data atom per iTunes item, so it does not list an item's further ones (cover art with several images).
                expected = [(b[0], b[2]) for i, b in enumerate(manifest["boxes"])
                            if not (re.search(r"/ilst/[^/]+/data$", b[0]) and i > 0 and manifest["boxes"][i - 1][0] == b[0])]
                if boxes != expected:
                    if boxes is None:
                        problems.append("MP4Box cannot read the file")
                    else:
                        first = next((i for i, (a, b) in enumerate(zip(boxes, expected)) if a != b), min(len(boxes), len(expected)))
                        problems.append(f"MP4Box: box {first} is {boxes[first] if first < len(boxes) else None}, the manifest's {expected[first] if first < len(expected) else None}")
            if check.get("mp4box_samples"):
                sizes = mp4box_sample_sizes(path)
                if sizes != [u[1] for u in manifest["units"]]:
                    problems.append(f"MP4Box lists {None if sizes is None else len(sizes)} samples of other sizes than the manifest's {len(manifest['units'])}")
            if faad := check.get("faad"):
                line = faad_info(path) or ""
                if "seconds" in faad:
                    ok = line.startswith(f"ADTS, {faad['seconds']} sec") and line.endswith(f" {faad['rate']} Hz")
                else:
                    ok = line.startswith(faad["label"] + "\t") and f", {faad['channels']} ch, {faad['rate']} Hz" in line
                if not ok:
                    problems.append(f"faad -i prints {line!r}, expected {faad}")
            checked += 1
            for problem in problems:
                print(f"{path.relative_to(ROOT)}: {problem}")
            failures += bool(problems)
    print(f"{checked} fixtures checked, {failures} disagree.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
