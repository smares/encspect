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
- the faad2 trace (faad_trace_tool.py, faad2 2.11.1 built with a trace of what it reads): for every raw data block the analyzer read, the
  golden report (<name>.report.json, which the tests hold equal to the analyzer's report) must show what faad2 reads: the elements and the
  bit each starts at, each channel pair's common window, ms_mask_present and ms_used flags, each channel stream's window sequence and
  shape, max_sfb, window groups, global gain, pulse, TNS and gain control flags, predictor flag, TNS filters and the codebook of every
  band, the extension payloads' types, the SBR headers' start, stop and crossover fields, the parametric stereo extension, and the
  block's length in bits. It runs on every fixture whose report holds read raw data blocks.
  Blocks that "faad_trace_skip" lists, by index, are not compared, each for the reason given.
- "decode": ffmpeg must decode every access unit without printing an error (ffmpeg -v error ... -f null -).
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

import faad_trace_tool

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


def mp4box_fields(path):
    """Fields of the first movie, track and media header, edit, audio sample entry and decoder configuration that MP4Box reads, as the
    analyzer's node paths and the values it must show for them."""
    with tempfile.TemporaryDirectory() as directory:
        out = pathlib.Path(directory) / "dump.xml"
        subprocess.run(["MP4Box", "-quiet", "-diso", str(path), "-out", str(out)], capture_output=True)
        if not out.exists():
            return {}
        text = out.read_text(encoding="utf-8", errors="replace")

    def attributes(element):
        match = re.search(rf"<{element}\s([^>]*)>", text)
        return dict(re.findall(r'(\w+)="([^"]*)"', match.group(1))) if match else {}

    def duration(value, scale):
        return f"{value} ({int(value) / int(scale):.3f} s)" if int(scale) else f"{value} (no timescale)"

    fields = {}
    movie = attributes("MovieHeaderBox")
    if movie:
        fields["MP4/moov/mvhd/Timescale"] = f"{movie['TimeScale']} units per second"
        fields["MP4/moov/mvhd/Duration"] = duration(movie["Duration"], movie["TimeScale"])
    track = attributes("TrackHeaderBox")
    if track:
        fields["MP4/moov/trak/tkhd/Track ID"] = track["TrackID"]
        if movie:
            fields["MP4/moov/trak/tkhd/Duration"] = duration(track["Duration"], movie["TimeScale"])
    media = attributes("MediaHeaderBox")
    if media:
        fields["MP4/moov/trak/mdia/mdhd/Timescale"] = f"{media['TimeScale']} units per second"
        fields["MP4/moov/trak/mdia/mdhd/Duration"] = duration(media["Duration"], media["TimeScale"])
    edit = attributes("EditListEntry")
    if edit and movie:
        fields["MP4/moov/trak/edts/elst/Edit/Segment Duration"] = duration(edit["Duration"], movie["TimeScale"])
        fields["MP4/moov/trak/edts/elst/Edit/Media Time"] = edit["MediaTime"] if edit["MediaTime"] == "-1" else f"{edit['MediaTime']} units of the media's timescale"
    entry = attributes("MPEGAudioSampleDescriptionBox")
    if entry:
        fields["MP4/moov/trak/mdia/minf/stbl/stsd/mp4a/Channels"] = entry["Channels"]
        fields["MP4/moov/trak/mdia/minf/stbl/stsd/mp4a/Sample Size"] = f"{entry['BitsPerSample']} bits"
    decoder = attributes("DecoderConfigDescriptor")
    if decoder:
        path = "MP4/moov/trak/mdia/minf/stbl/stsd/mp4a/esds/ES_Descriptor/DecoderConfigDescriptor"
        if "maxBitrate" in decoder:
            fields[path + "/Maximum Bitrate"] = f"{decoder['maxBitrate']} bit/s"
        if "avgBitrate" in decoder:
            fields[path + "/Average Bitrate"] = "0 (variable bitrate)" if decoder["avgBitrate"] == "0" else f"{decoder['avgBitrate']} bit/s"
    return fields


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


def analyzer_blocks(report):
    """The raw data blocks the analyzer read, from a report's JSON: MP4 access units, the raw data blocks of ADTS frames and ADIF's."""
    blocks = []

    def walk(node):
        children = node.get("children") or []
        if node.get("kind") == "Frame":
            if node["name"] == "Raw Data Block" and (node.get("children") or []):
                blocks.append(node)
            if node["name"] == "Access Unit" and any(c["name"] == "END" or child_id(c) is not None for c in children):
                blocks.append(node)
            blocks.extend(c for c in children if c["name"] == "Raw Data Block" and (c.get("children") or []))
            return
        for child in children:
            walk(child)

    walk(report["root"])
    return blocks


def child_id(node):
    element = next((c for c in node.get("children") or [] if c["name"] == "Element ID"), None)
    return int(element["value"].split()[0]) if element else None


def bit_of(node, base):
    return (node["offset"] - base) * 8 + (node.get("bitOffset") or 0)


def end_of(node, base):
    return bit_of(node, base) + (node.get("bitLength") if node.get("bitLength") is not None else node["length"] * 8)


def field(node, name, default=None):
    found = next((c for c in node.get("children") or [] if c["name"] == name), None)
    return found["value"] if found else default


def flag(node, name):
    return 1 if field(node, name, False) is True else 0


def analyzer_records(block):
    """A block of the report as faad2's trace records it (faad_trace_tool.py)."""
    base = block["offset"]
    records = []
    end = None
    for element in block.get("children") or []:
        if element["name"] == "END":
            end = end_of(element, base)
            continue
        if element["name"] == "Byte Alignment":
            end = end_of(element, base)
            continue
        number = child_id(element)
        if number is None:
            continue
        records.append(["EL", str(number), str(bit_of(element, base))])
        shared = next((c for c in element.get("children") or [] if c["name"] == "ICS Info"), None)
        if element["name"] == "CPE":
            mask = field(element, "MS Mask Present", "0").split()[0] if shared else "0"
            flags = ""
            if mask == "1":
                flags = band_flags(field(element, "Mid/Side Bands", "none"), groups_of(shared), int(field(shared, "Max SFB")))
            records.append(["CPE", str(flag(element, "HasCommonWindow")), mask, flags] if flags else ["CPE", str(flag(element, "HasCommonWindow")), mask])
        for child in element.get("children") or []:
            if child["name"].startswith("Channel"):
                records.append(stream_record(child, shared))
            elif child["name"] == "Extension Payload":
                kind = int(field(child, "Extension Type").split()[0])
                records.append(["EXT", str(kind)])
                header = next((c for c in child.get("children") or [] if c["name"] == "SBR Header"), None)
                if header:
                    records.append(["SBRHDR", str(field(header, "bs_start_freq")), str(field(header, "bs_stop_freq")), str(field(header, "bs_xover_band"))])
                data = next((c for c in child.get("children") or [] if c["name"] == "SBR Data"), None)
                if data and any(c["name"] == "Parametric Stereo Extension" for c in data.get("children") or []):
                    records.append(["PS"])
    return end, records


def groups_of(info):
    grouping = field(info, "Scale Factor Grouping")
    match = re.search(r"\((\d+) groups", grouping) if grouping else None
    return int(match.group(1)) if match else 1


def band_flags(text, groups, max_sfb):
    flags = [["0"] * max_sfb for _ in range(groups)]
    if text != "none":
        for part in text.split("; "):
            group = 0
            match = re.match(r"group (\d+): (.*)", part)
            if match:
                group, part = int(match.group(1)), match.group(2)
            for item in part.split(", "):
                first, _, last = item.partition("-")
                for band in range(int(first), int(last or first) + 1):
                    flags[group][band] = "1"
    return "".join("".join(row) for row in flags)


def stream_record(channel, shared):
    info = next((c for c in channel.get("children") or [] if c["name"] == "ICS Info"), shared)
    sequence = int(field(info, "Window Sequence").split()[0])
    shape = int(field(info, "Window Shape").split()[0])
    max_sfb = int(field(info, "Max SFB"))
    groups = groups_of(info)
    books = [[0] * max_sfb for _ in range(groups)]
    sections = next((c for c in channel.get("children") or [] if c["name"] == "Section Data"), None)
    for section in (sections or {}).get("children") or []:
        match = re.match(r"(?:group (\d+): )?codebook (\d+) \(.*\), (?:bands? (\d+)(?:-(\d+))?|no bands)$", section["value"])
        if match and match.group(3) is not None:
            for band in range(int(match.group(3)), int(match.group(4) or match.group(3)) + 1):
                books[int(match.group(1) or 0)][band] = int(match.group(2))
    tns = next((c for c in channel.get("children") or [] if c["name"] == "TNS Data"), None)
    filters = int(str(tns["value"]).split()[0]) if tns else 0
    return ["ICS", str(sequence), str(shape), str(max_sfb), str(groups), str(field(channel, "Global Gain")), str(flag(channel, "HasPulseData")),
            str(flag(channel, "HasTnsData")), str(flag(channel, "HasGainControlData")), str(flag(info, "HasPredictorData")), str(filters),
            "".join(format(b, "x") for row in books for b in row)]


def faad_records(block):
    """faad2's trace of a block in the same form: its first SBR extension ID only, as the analyzer reads only the first, and that as PS where it is 2."""
    records = []
    previous = None
    for record in block["records"]:
        if record[0] == "SBREXT":
            if previous == "EXT" or previous == "SBRHDR":
                if record[1] == "2":
                    records.append(["PS"])
            previous = record[0]
            continue
        if record[0] == "CPE" and len(record) == 3:
            record = record[:3]
        records.append(record)
        previous = record[0]
    return block.get("bits"), records


def decode_messages(path):
    result = subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-i", str(path), "-f", "null", "-"], capture_output=True)
    return result.stderr.decode("utf-8", "replace").strip()


def check_trace(path, report, problems, skipped=()):
    """Compares the raw data blocks of the golden report with faad2's trace; returns how many blocks were compared."""
    ours = analyzer_blocks(report)
    if not ours:
        return 0
    theirs = faad_trace_tool.trace(path)
    if len(theirs) != len(ours):
        problems.append(f"faad2 reads {len(theirs)} raw data blocks, the analyzer {len(ours)}")
    for index, (mine, faad) in enumerate(zip(ours, theirs)):
        # A block the analyzer could not read to its end is damaged, which decoders handle each in their own way.
        if analyzer_records(mine)[0] is None or str(index) in skipped:
            continue
        if analyzer_records(mine) != faad_records(faad):
            end, records = analyzer_records(mine)
            bits, theirs_records = faad_records(faad)
            first = next((i for i, (a, b) in enumerate(zip(records, theirs_records)) if a != b), min(len(records), len(theirs_records)))
            problems.append(f"raw data block {index}: the analyzer reads {records[first:first + 2]} (block of {end} bits), "
                            f"faad2 {theirs_records[first:first + 2]} (block of {bits} bits)")
            break
    compared = min(len(ours), len(theirs))
    return compared - sum(1 for key in skipped if int(key) < compared)


def main():
    failures = 0
    checked = 0
    traced = 0
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
            if "faad_trace" not in check.get("skip", {}) and (directory / (path.stem + ".report.json")).exists():
                report = json.loads((directory / (path.stem + ".report.json")).read_text(encoding="utf-8"))[0]
                traced += check_trace(path, report, problems, check.get("faad_trace_skip", {}))
            if check.get("decode"):
                messages = decode_messages(path)
                if messages:
                    problems.append(f"ffmpeg decodes with messages: {messages.splitlines()[0]}")
            checked += 1
            for problem in problems:
                print(f"{path.relative_to(ROOT)}: {problem}")
            failures += bool(problems)
    print(f"{checked} fixtures checked, {traced} raw data blocks against faad2's trace, {failures} disagree.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
