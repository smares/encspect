#!/usr/bin/env python3
"""Checks the MP4 and raw AAC fixtures' manifests against independent tools.

For every fixture in tests/fixtures/mp4, tests/fixtures/adts and tests/fixtures/aac, its manifest's
"crosscheck" says what to confirm:

- "ffprobe": ffprobe (ffmpeg 8.0) must list exactly the manifest's access units as its audio packets,
  stream by stream and with edit lists ignored, and report each stream's sample rate and channels (unless
  "ffprobe_streams" is false, for ADTS frames of several protected raw data blocks, which ffmpeg does not
  decode, so it reports no rate). ffmpeg outputs two channels for any mono
  stream with SBR whose configuration does not rule PS out, since PS may be signaled implicitly; the
  manifest counts the channels the configuration signals, so there ffprobe's 2 is expected
  ("ffprobe_channels"); likewise ffmpeg doubles the rate of an ADTS stream whose access units carry SBR,
  which ADTS can only signal implicitly ("ffprobe_sample_rate"). Where the
  manifest records ffprobe's profile ("ffprobe_profile"), it must match: for an MP4 track ffmpeg names a stream HE-AAC only
  once it finds SBR data in an access unit, which the hand-built ones do not carry, so those record none; for a raw ADTS stream it names the profile
  of the ADTS header, since FFmpeg 8.0's parser sets it for every frame header (libavcodec/aac_ac3_parser.c, ff_aac_ac3_parse), after the decoder
  has named the stream from its SBR data, so those record "LC".
  ffprobe's channel layout must feed the speakers of the stream's "channel_layout", in any order
  (channel_layouts.py), where ffprobe's channels are the manifest's: ffmpeg's decoder gives an AAC
  stream the layout its configuration states, whatever a chan or chnl box says.
- "ffprobe_packets": false where ffmpeg's parser does not list the samples as packets (ciphertext that an AC-3, DTS or MPEG audio parser merges until it finds
  a sync word); then only the streams are compared. Where a manifest has "samples" (the protected track's samples, as offset and size, since the analyzer
  reads no frames of them), those are the packets ffprobe must list.
- "encryption": what the tools report of a protected track: "ffprobe" is whether the first packet carries the "Encryption info" side data that ffmpeg
  attaches to the packets of a track whose tenc box protects by default (null where the track is protected another way), and "mediainfo" the track's
  Encryption field ("Encrypted" for an enca entry whatever its tenc says, "iTunes" for drms, absent for aavd). Every other fixture must have no
  Encryption field in MediaInfo.
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
- MediaInfo 26.01 (mediainfo_tool.py, check_mediainfo), on every fixture, against its golden
  report: each track's format and AAC profile, ADTS's MPEG version, sample rate, channels and
  layout, SBR and parametric stereo signaling (implicit, explicit or hierarchical), access units
  and stream size, the track header's and sample table's durations (for a fragmented file, the track
  runs' sample durations), the encoding tool (©too) and the iTunes metadata's title, artist and album.
  Its own readings are asserted as such: the AAC bitrate mode, which follows frame sizes more than
  the encoder's declaration (mediainfo_aac_mode), no duration for
  ADTS, and one frame for ADIF with the duration and stream size from its bitrate. Fixtures on
  which it reads a field otherwise are listed with the evidence (MEDIAINFO_READS_OTHERWISE).

The analyzer's agreement with the manifests is checked by the tests (Mp4FixtureTests). Exits non-zero
when anything disagrees. Needs ffmpeg, gpac, faad and mediainfo.
"""

import json
import math
import pathlib
import re
import subprocess
import sys
import tempfile

import channel_layouts
import faad_trace_tool
import mediainfo_tool as mi
import tool_versions

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


def ffprobe_encrypted(path):
    """Whether ffprobe attaches the "Encryption info" side data to the first audio packet."""
    result = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "packet_side_data", "-select_streams", "a", "-read_intervals", "%+#1", "-of", "json", str(path)],
                            capture_output=True, text=True)
    try:
        packets = json.loads(result.stdout).get("packets", []) if result.stdout.strip() else []
    except json.JSONDecodeError:
        packets = []
    return any(data.get("side_data_type") == "Encryption info" for packet in packets for data in packet.get("side_data_list", []))


def ffprobe_streams(path):
    result = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries", "stream=sample_rate,channels,channel_layout,profile", "-of", "json", str(path)],
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


# Boxes MP4Box skips without listing: Apple's sbtd box inside an audio sample entry (an unknown box there). The manifests
# list them, with the size the file states, and the comparison with MP4Box's list leaves them out.
UNLISTED_BOXES = {"sbtd"}


def with_unlisted(path, rows):
    """MP4Box's rows with the unlisted boxes added after the esds box of the sample entry that holds them, sized by the file."""
    data = pathlib.Path(path).read_bytes()
    out = list(rows)
    # only inside the sample description box, not in the media data
    stsd = data.find(b"stsd")
    if stsd < 4:
        return out
    stsd_end = stsd - 4 + int.from_bytes(data[stsd - 4:stsd], "big")
    for kind in UNLISTED_BOXES:
        at = data.find(kind.encode("ascii"), stsd, stsd_end)
        while 4 <= at < stsd_end:
            size = int.from_bytes(data[at - 4:at], "big")
            index = next((i for i, (p, _) in enumerate(out) if p.endswith("/esds")), None)
            if index is not None and size >= 8:
                out.insert(index + 1, (out[index][0].rsplit("/", 1)[0] + "/" + kind, size))
            at = data.find(kind.encode("ascii"), at + 1, stsd_end)
    return out


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
            mask = field(element, "Mid/Side Mask Present", "0").split()[0] if shared else "0"
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


# MediaInfo's format and its AAC profile (Format_AdditionalFeatures) for each of the analyzer's codecs.
MEDIAINFO_FORMATS = {"AAC LC": ("AAC", "LC"), "HE-AAC": ("AAC", "LC SBR"), "HE-AAC v2": ("AAC", "LC SBR PS"), "AAC Main": ("AAC", "Main"),
                     "AAC LTP": ("AAC", "LTP"), "AAC SSR": ("AAC", "SSR"), "xHE-AAC (USAC)": ("USAC", None), "ER AAC ELD": ("ER AAC ELD", None)}
# MediaInfo's format of the streams whose frames the analyzer does not read; the analyzer's name for the others is MediaInfo's.
ENCRYPTED_FORMATS = {"MPEG-1 Audio": "MPEG Audio", "HE-AAC": "AAC", "HE-AAC v2": "AAC", "AAC Main": "AAC", "Apple Lossless": "ALAC"}
# MediaInfo counts the samples that stts gives a duration (File_Mpeg4.cpp lines 774, 799 and 852
# fill FrameCount and Source_FrameCount from stts_FrameCount, which File_Mpeg4_Elements.cpp line 8755
# sums) and takes the stream size from the sizes stsz lists (stsz_StreamSize, File_Mpeg4.cpp lines
# 801 and 855), whether or not a chunk places the samples in the file; the analyzer leaves out what
# has no place, with a finding.
STTS_COUNT = "MediaInfo counts the samples stts gives a duration (File_Mpeg4.cpp lines 774, 799 and 852: FrameCount from stts_FrameCount, summed in File_Mpeg4_Elements.cpp line 8755)"
UNPLACED_UNITS = STTS_COUNT + ", whether or not the chunks place them in the file; the analyzer leaves out the samples that have no place, with a finding"
UNPLACED_SIZE = "MediaInfo sums the sizes stsz lists (File_Mpeg4_Elements.cpp, stsz: stsz_StreamSize, filled in File_Mpeg4.cpp lines 801 and 855), whether or not the chunks place the samples in the file; the analyzer leaves out the samples that have no place, with a finding"
ADTS_ERRORS = "MediaInfo counts only the ADTS frames whose raw data blocks it parses without an error; its trace (--Details=1) lists every frame"
# Fixtures on which MediaInfo reads a field differently, with the evidence.
NO_CHANNELS = "MediaInfo 26.01 gives this track no channel count; it takes the channels of a coding from its frames (File_Ac3, File_Dts, File_Mpega), which are encrypted"
NO_FORMAT = "MediaInfo names the coding from the esds box of the sample entry (File_Mpeg4_Elements.cpp, the sample entry's codec); the analyzer, with no frma box to name the original format, reports the sample entry's own kind"
MOOV_TRACKS = "MediaInfo lists the audio tracks of moov; the analyzer reports a stream only for samples its tables or fragments place, and these fragments place them for one track or none"
FIRST_ENTRY = "MediaInfo reads the first sample entry of the track (File_Mpeg4_Elements.cpp), which does not say that the track is encrypted; the analyzer reads the entries the chunks use"
MEDIAINFO_READS_OTHERWISE = {
    "mp4/ffmpeg-cenc-mp3": {"channels": NO_CHANNELS},
    "mp4/cenc-ac3": {"channels": NO_CHANNELS},
    "mp4/cenc-dts": {"channels": NO_CHANNELS},
    "mp4/cenc-mpeg-audio": {"channels": NO_CHANNELS},
    "mp4/cenc-second-entry": {"encryption": FIRST_ENTRY},
    "mp4/cenc-entry-out-of-range": {"encryption": FIRST_ENTRY},
    "mp4/cenc-fragmented-no-tfhd": {"encryption": FIRST_ENTRY},
    "mp4/cenc-fragmented-traf-in-trak": {"streams": MOOV_TRACKS},
    "mp4/cenc-fragmented-moof-in-trak": {"streams": MOOV_TRACKS},
    "mp4/cenc-fragmented-two-tfhd": {"streams": MOOV_TRACKS},
    "mp4/cenc-fragmented-two-tfhd-protecting": {"streams": MOOV_TRACKS},
    "mp4/cenc-clear-unparsed": {"channels": "MediaInfo 26.01 gives this track no channel count: it reads an ALAC track's channels from the alac box (File_Mpeg4_Elements.cpp), which the hand-built entry does not have"},
    "mp4/truncated-in-tenc": {"streams": "MediaInfo lists the audio track from its sample entry, which the cut leaves whole; the analyzer reports a stream only for samples its sample tables place (the cut file has no stts, stsc, stsz or stco box)"},
    "mp4/enca-no-sinf": {"format of an encrypted stream": NO_FORMAT},
    "mp4/sinf-no-frma": {"format of an encrypted stream": NO_FORMAT},
    "mp4/ps-in-channel-pair": dict.fromkeys(["format", "PS signaling"], "MediaInfo reads a parametric stereo extension in a channel pair's SBR data (File_Aac_GeneralAudio_Sbr.cpp, sbr_channel_pair_element: ps_data); the analyzer, like the decoders, does not apply it there: FFmpeg 8.0 turns PS off for a configuration of more than one channel (mpeg4audio.c, ff_mpeg4audio_get_config_gb: channels & ~0x01) and skips the extension (aacsbr_template.c, read_sbr_extension), and faad2 2.11 decodes a channel pair's SBR without PS (specrec.c, reconstruct_channel_pair: sbrDecodeCoupleFrame)"),
    "mp4/chunk-outside-file": {"access units": UNPLACED_UNITS, "stream size": UNPLACED_SIZE},
    "mp4/chunk-sample-mismatch": {"access units": UNPLACED_UNITS, "stream size": UNPLACED_SIZE},
    "mp4/truncated": {"access units": UNPLACED_UNITS, "stream size": UNPLACED_SIZE},
    "adts/truncated": {"access units": "MediaInfo counts the complete ADTS frames only, and the last one is cut 5 bytes short"},
    "mp4/huge-sample-count": {"access units": STTS_COUNT + ": the 4294967280 that stts states, of which stsz sizes 20", "sample table duration": "MediaInfo takes the duration of the 4294967280 samples stts states (File_Mpeg4.cpp, Source_Duration), which no sample backs"},
    "mp4/sample-count-mismatch": {"access units": STTS_COUNT + ": 21, where stsz sizes 20"},
    "adts/corrupt-syntax": {"access units": ADTS_ERRORS + " (21 frames, 16 counted)"},
    "adts/damaged-length": {"access units": ADTS_ERRORS + " (30 frames, 28 counted)"},
    "adts/raw-blocks": {"access units": "MediaInfo reads a crc_check in a frame of several raw data blocks also without protection (File_Aac_Main.cpp, adts_frame), which adts_header_error_check holds only when protection_absent is 0 (ISO/IEC 14496-3 1.A.2.2), so it misreads those blocks and counts none of the frames"},
    "adts/stream-change": dict.fromkeys(["streams", "access units"], "MediaInfo reports an ADTS file as one stream (File_Aac.cpp), where the analyzer starts a second one where the sample rate changes"),
    "mp4/id32": dict.fromkeys(["tag title", "tag track"], "MediaInfo skips the ID3v2 tag in an ID32 box (File_Mpeg4_Elements.cpp, moov_udta_ID32: Skip_XX \"ID3v2data\")"),
    "mp4/ilst-all-types": {"tag performer": "MediaInfo writes \"UTF-16 encoding not yet supported\" for an iTunes data atom of type 2 (File_Mpeg4_Elements.cpp, moov_meta_ilst_xxxx_data)"},
    "mp4/trailing-id3v1": dict.fromkeys(["tag title", "tag track"], "MediaInfo's MP4 parser has no tag helper (File_Mpeg4.h: File_Mpeg4 derives from File__Analyze, not File__Tags_Helper), so it reads no ID3v1 tag after the boxes"),
}


def sbr_signaling(report, stream_node, codec):
    """MediaInfo's Format_Settings_SBR and _PS for the stream: "Implicit" where the access units
    carry SBR or PS that the configuration does not signal, "NBC" (not backward compatible) for
    hierarchical signaling, object type 5 or 29 first, "Explicit" for the backward-compatible sync
    extensions, which can also signal their absence (File_Aac_Main.cpp, AudioSpecificConfig)."""
    config = next((n for n in mi.walk(report["root"]) if n["name"] == "AudioSpecificConfig" and n.get("children")), None)
    implicit = any(f["code"] == "aac.implicit-sbr" for f in report["findings"])
    object_type = mi.leading_number(mi.value(config, "Audio Object Type")) if config else None
    flags = {n["name"]: n.get("value") for n in mi.walk(config)} if config else {}
    sbr = ps = None
    if object_type in (5, 29):
        sbr = "Yes (NBC)"
    elif "HasSbr" in flags:
        sbr = "Yes (Explicit)" if flags["HasSbr"] else "No (Explicit)"
    elif implicit:
        sbr = "Yes (Implicit)"
    if object_type == 29:
        ps = "Yes (NBC)"
    elif "HasPs" in flags:
        ps = "Yes (Explicit)" if flags["HasPs"] else "No (Explicit)"
    elif codec == "HE-AAC v2":
        ps = "Yes (Implicit)"
    return sbr, ps


def mediainfo_aac_mode(report, node, track_box):
    """The bitrate mode MediaInfo gives an AAC stream, which follows frame sizes more than the
    encoder's declaration that the analyzer reports (ADTS buffer fullness 0x7FF, ADIF's
    bitstream_type, esds avgBitrate 0 by ISO/IEC 14496-1 7.2.6.6.2):

    - ADIF: bitstream_type (File_Aac_Main.cpp, adif_header).
    - ADTS: VBR where the largest frame exceeds the smallest by more than 2 %, and none otherwise
      (File_Aac.cpp, Streams_Finish, which writes CBR only at parse speed 1, the default being 0.5).
      Streams_Update's reading of the buffer fullness reaches no output, since Open_Buffer_Finalize
      runs it after Streams_Fill has copied the stream's fields (File__Analyze.cpp).
    - MP4: from the esds's avgBitrate where it is set and not the "buggy" 0x1000 that also fills
      bufferSizeDB and maxBitrate: CBR where maxBitrate is 0 or within 0.5 % above it, VBR otherwise
      (File_Mpeg4_Descriptors.cpp, DecoderConfigDescriptor). Otherwise, in a file without fragments
      and a track without stss, VBR where the largest sample stsz sizes exceeds the smallest by more
      than 1 %, CBR otherwise (File_Mpeg4.cpp, Streams_Finish, lines 1379-1396), and none else."""
    if node["name"] == "ADIF":
        return "VBR" if str(mi.value(node, "ADIF Header", "Bitstream Type")).startswith("1") else "CBR"
    statistics = mi.child(node, "Statistics")
    if node["name"] == "ADTS":
        return "VBR" if mi.value(statistics, "Largest Frame") > mi.value(statistics, "Smallest Frame") * 1.02 else None
    if track_box is None:
        return None
    config = next((n for n in mi.walk(track_box) if n["name"] == "DecoderConfigDescriptor"), None)
    average, maximum, buffer = (mi.leading_number(mi.value(config, name)) or 0 for name in ("Average Bitrate", "Maximum Bitrate", "Buffer Size"))
    if average > 0 and not buffer == average == maximum == 0x1000:
        return "CBR" if not maximum or average <= maximum <= average * 1.005 else "VBR"
    if any(n["name"] == "moof" for n in mi.walk(report["root"])) or any(n["name"] == "stss" for n in mi.walk(track_box)):
        return None
    return "VBR" if mi.value(statistics, "Smallest Access Unit") * (1.005 + 0.005) < mi.value(statistics, "Largest Access Unit") else "CBR"


def fragment_ticks(report, track_box):
    """The sum of the track's sample durations over its movie fragments: each track run's own
    durations, or the track fragment header's default, or the track extends box's (ISO/IEC 14496-12
    8.8.3, 8.8.7, 8.8.8)."""
    track_id = mi.value(mi.child(track_box, "tkhd"), "Track ID")
    trex = next((n for n in mi.walk(report["root"]) if n["name"] == "trex" and mi.value(n, "Track ID") == track_id), None)
    ticks = 0
    for fragment in (n for n in mi.walk(report["root"]) if n["name"] == "traf"):
        header = mi.child(fragment, "tfhd")
        if mi.value(header, "Track ID") != track_id:
            continue
        default = mi.value(header, "Default Sample Duration") or mi.value(trex, "Default Sample Duration") or 0
        for run in (n for n in fragment.get("children", []) if n["name"] == "trun"):
            durations = [mi.value(sample, "Duration") for sample in run.get("children", []) if sample["name"] == "Sample"]
            ticks += sum(durations) if durations and None not in durations else mi.value(run, "Sample Count") * default
    return ticks


def milliseconds(seconds):
    """Seconds as MediaInfo writes a duration: rounded to the millisecond (ZenLib's float64_int64s)."""
    return f"{math.floor(seconds * 1000 + 0.5) / 1000:.3f}"


def check_mediainfo(path, tally, check=None):
    """MediaInfo must report each audio track's format and AAC profile, ADTS's MPEG version, sample
    rate, channels and layout, SBR and PS signaling, bitrate mode, access units and stream size, and
    for MP4 the duration, the encoding tool and the iTunes metadata's title, artist and album."""
    problems = tally.start(f"{path.parent.name}/{path.stem}")
    report = json.loads((path.parent / (path.stem + ".report.json")).read_text(encoding="utf-8"))[0]
    info = tally.read(path)
    audio = info["Audio"] if info else []
    tally.compare("streams", len(audio), len(report["streams"]))
    expected_encryption = (check or {}).get("encryption", {}).get("mediainfo")
    for stream, track in zip(report["streams"], audio):
        node = mi.stream_node(report, stream)
        codec = stream["codec"]
        # MediaInfo reads the sample entry only (File_Mpeg4_Elements.cpp, the sample entry's codec: drms is "iTunes", enca "Encrypted").
        tally.compare("encryption", track.get("Encryption"), expected_encryption if stream.get("protection") else None)
        if stream.get("protection", {}).get("isEncrypted") or (stream.get("isProtected") and not any(n.get("kind") == "Frame" for n in mi.walk(node))):
            # No frames are read (a coding with no parser reads none, encrypted or not), so neither side counts any: the configuration's figures are compared.
            tally.compare("format of an encrypted stream", track.get("Format"), ENCRYPTED_FORMATS.get(codec, codec if codec != "AAC LC" else "AAC"))
            tally.compare("sample rate", track.get("SamplingRate"), str(stream["sampleRate"]))
            tally.compare("channels", track.get("Channels", track.get("Channels_Original")), str(stream["channels"]))
            continue
        if codec.startswith("MPEG"):
            version, layer = codec.split()[0][5:], {"I": "1", "II": "2", "III": "3"}[codec.split()[-1]]
            tally.compare("format", (track.get("Format"), track.get("Format_Version"), track.get("Format_Profile")), ("MPEG Audio", version, f"Layer {layer}"))
            continue
        tally.compare("format", (track.get("Format"), track.get("Format_AdditionalFeatures")), MEDIAINFO_FORMATS.get(codec, (codec, None)))
        frame = next((n for n in mi.walk(node) if n.get("kind") == "Frame"), None)
        if node["name"] == "ADTS" and frame is not None:
            tally.compare("MPEG version", track.get("Format_Version"), mi.value(frame, "Version").removeprefix("MPEG-"))
        tally.compare("sample rate", track.get("SamplingRate"), str(stream["sampleRate"]))
        # With a chan box, MediaInfo gives the configuration's channels as Channels_Original.
        tally.compare("channels", track.get("Channels", track.get("Channels_Original")), str(stream["channels"]))
        if stream["channelLayout"]:
            # The analyzer gives the configuration's layout, and the container's where the configuration names none. MediaInfo 26.01 gives HE-AAC v2
            # the layout "L R" with its two channels (File_Aac_GeneralAudio.cpp, FillInfosHEAACv2), which is the analyzer's.
            reserved = any(f["code"] == "aac.reserved-channel-configuration" for f in report["findings"])
            layout = track.get("ChannelLayout") if reserved or codec == "HE-AAC v2" else track.get("ChannelLayout_Original", track.get("ChannelLayout"))
            tally.compare("channel layout", mi.speakers(layout), mi.normalized(stream["channelLayout"]))
        if codec.startswith(("AAC", "HE-AAC")):
            sbr, ps = sbr_signaling(report, node, codec)
            tally.compare("SBR signaling", track.get("Format_Settings_SBR"), sbr)
            tally.compare("PS signaling", track.get("Format_Settings_PS"), ps)
            tracks = [n for n in mi.walk(report["root"]) if n["name"] == "trak"]
            index = report["streams"].index(stream)
            tally.compare("bitrate mode", track.get("BitRate_Mode"), mediainfo_aac_mode(report, node, tracks[index] if index < len(tracks) else None))
        if node["name"] == "ADIF":
            # ADIF holds no frames: MediaInfo counts one, takes the file's duration from its size at
            # the header's bitrate, rounded to the millisecond, and the stream size from that duration
            # (File__Analyze_Streams_Finish.cpp, Streams_Finish_StreamOnly and Streams_Finish_Global).
            bitrate = mi.leading_number(mi.value(node, "ADIF Header", "Bitrate"))
            duration = math.floor(path.stat().st_size * 8000 / bitrate + 0.5) if bitrate else None
            tally.compare("frames, duration and stream size of ADIF", (track.get("FrameCount"), track.get("Duration"), track.get("StreamSize")),
                          ("1", f"{duration / 1000:.3f}", str(math.floor(bitrate * duration / 8000 + 0.5))) if duration else ("1", None, None))
            continue
        statistics = mi.child(node, "Statistics")
        units = mi.value(statistics, "Access Units") or mi.value(statistics, "Frames")
        tally.compare("access units", track.get("Source_FrameCount", track.get("FrameCount")), str(units))
        tally.compare("stream size", track.get("Source_StreamSize", track.get("StreamSize")), str(mi.byte_count(node)))
        if node["name"] == "ADTS":
            # MediaInfo gives an ADTS stream's duration only at parse speed 1 or from its risky
            # bitrate estimate, both off by default (File_Aac.cpp, Streams_Update and Streams_Finish).
            tally.compare("ADTS duration", track.get("Duration"), None)
            continue
        # File_Mpeg4.cpp, Streams_Finish: the duration is the track header's, in the movie's time
        # scale, and where the sample table's differs from it, that one is Source_Duration, rounded
        # to the millisecond.
        index = report["streams"].index(stream)
        tracks = [n for n in mi.walk(report["root"]) if n["name"] == "trak"]
        movie = mi.leading_number(next((mi.value(n, "Timescale") for n in mi.walk(report["root"]) if n["name"] == "mvhd"), None))
        if index >= len(tracks) or not movie:
            continue
        header = mi.leading_number(mi.value(mi.child(tracks[index], "tkhd"), "Duration"))
        if not header:
            # A fragmented file's track header gives no duration; MediaInfo then sums the track
            # runs' sample durations (File_Mpeg4_Elements.cpp, moof_traf_trun).
            media = next((n for n in mi.walk(tracks[index]) if n["name"] == "mdhd"), None)
            tally.compare("fragments' duration", track.get("Duration"), milliseconds(fragment_ticks(report, tracks[index]) / mi.leading_number(mi.value(media, "Timescale"))))
        else:
            tally.compare("track header duration", track.get("Duration"), milliseconds(header / movie))
        media = next((n for n in mi.walk(tracks[index]) if n["name"] == "mdhd"), None)
        table = next((n for n in mi.walk(tracks[index]) if n["name"] == "stts"), None)
        ticks = re.match(r"\d+ samples?, (\d+)", str(mi.value(table) or ""))
        if "Source_Duration" in track and ticks:
            tally.compare("sample table duration", track["Source_Duration"], milliseconds(int(ticks.group(1)) / mi.leading_number(mi.value(media, "Timescale"))))
    general = info["General"] if info else {}
    tools = [n.get("value") for n in mi.walk(report["root"]) if n["name"] == "Encoding Tool"]
    tally.compare("encoding tool", general.get("Encoded_Application"), tools[0] if tools else None)
    mi.compare_tags(tally, general, report)
    return problems


def main():
    mi.require()
    tool_versions.require("ffmpeg", "ffprobe", "MP4Box", "faad")
    tally = mi.Tally(MEDIAINFO_READS_OTHERWISE)
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
            wanted = manifest.get("samples") or manifest["units"]
            if check.get("ffprobe"):
                packets = ffprobe_packets(path)
                if check.get("ffprobe_packets", True) and packets != wanted:
                    first = next((i for i, (a, b) in enumerate(zip(packets, wanted)) if a != b), min(len(packets), len(wanted)))
                    problems.append(f"ffprobe lists {len(packets)} packets, the manifest {len(wanted)} samples; first difference at {first}")
                probed = ffprobe_streams(path)
                if len(probed) != len(manifest["streams"]):
                    problems.append(f"ffprobe finds {len(probed)} audio streams, the manifest {len(manifest['streams'])}")
                for expected, actual in zip(manifest["streams"] if check.get("ffprobe_streams", True) else [], probed):
                    channels = expected.get("ffprobe_channels", expected["channels"])
                    rate = expected.get("ffprobe_sample_rate", expected["sample_rate"])
                    if int(actual.get("sample_rate", 0)) != rate or int(actual.get("channels", 0)) != channels:
                        problems.append(f"ffprobe: {actual.get('sample_rate')} Hz, {actual.get('channels')} channels; expected {rate} Hz, {channels}")
                    if "ffprobe_channels" not in expected and channel_layouts.same_speakers(actual.get("channel_layout"), expected["channel_layout"]) is not True:
                        problems.append(f"ffprobe's channel layout {actual.get('channel_layout')}, expected {expected['channel_layout']}")
                    if "ffprobe_profile" in expected and actual.get("profile") != expected["ffprobe_profile"]:
                        problems.append(f"ffprobe's profile {actual.get('profile')}, expected {expected['ffprobe_profile']}")
            if (encryption := check.get("encryption", {})).get("ffprobe") is not None:
                if ffprobe_encrypted(path) != encryption["ffprobe"]:
                    problems.append(f"ffprobe {'finds no' if encryption['ffprobe'] else 'finds'} \"Encryption info\" side data on the first packet")
            if check.get("mp4box"):
                boxes = mp4box_boxes(path)
                # MP4Box reads one data atom per iTunes item, so it does not list an item's further ones (cover art with several images).
                expected = [(b[0], b[2]) for i, b in enumerate(manifest["boxes"])
                            if not (re.search(r"/ilst/[^/]+/data$", b[0]) and i > 0 and manifest["boxes"][i - 1][0] == b[0])
                            and b[0].rsplit("/", 1)[-1] not in UNLISTED_BOXES]
                if boxes != expected:
                    if boxes is None:
                        problems.append("MP4Box cannot read the file")
                    else:
                        first = next((i for i, (a, b) in enumerate(zip(boxes, expected)) if a != b), min(len(boxes), len(expected)))
                        problems.append(f"MP4Box: box {first} is {boxes[first] if first < len(boxes) else None}, the manifest's {expected[first] if first < len(expected) else None}")
            if check.get("mp4box_samples"):
                sizes = mp4box_sample_sizes(path)
                if sizes != [u[1] for u in wanted]:
                    problems.append(f"MP4Box lists {None if sizes is None else len(sizes)} samples of other sizes than the manifest's {len(wanted)}")
            if faad := check.get("faad"):
                line = faad_info(path) or ""
                if "seconds" in faad:
                    ok = line.startswith(f"ADTS, {faad['seconds']} sec") and line.endswith(f" {faad['rate']} Hz")
                else:
                    ok = line.startswith(faad["label"] + "\t") and f", {faad['channels']} ch, {faad['rate']} Hz" in line
                if not ok:
                    problems.append(f"faad -i prints {line!r}, expected {faad}")
            if not {"faad_trace", "all"} & set(check.get("skip", {})) and (directory / (path.stem + ".report.json")).exists():
                report = json.loads((directory / (path.stem + ".report.json")).read_text(encoding="utf-8"))[0]
                traced += check_trace(path, report, problems, check.get("faad_trace_skip", {}))
            if check.get("decode"):
                messages = decode_messages(path)
                if messages:
                    problems.append(f"ffmpeg decodes with messages: {messages.splitlines()[0]}")
            problems += check_mediainfo(path, tally, check)
            checked += 1
            for problem in problems:
                print(f"{path.relative_to(ROOT)}: {problem}")
            failures += bool(problems)
    for problem in tally.unused():
        print(problem)
        failures += 1
    tally.print()
    print(f"{checked} fixtures checked, {traced} raw data blocks against faad2's trace, {failures} disagree.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
