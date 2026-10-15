#!/usr/bin/env python3
"""Checks the AC-3 and E-AC-3 fixtures' manifests (tests/fixtures/ac3) against independent tools.

For every fixture, its manifest's "crosscheck" says what to confirm:

- "ffprobe": ffprobe (ffmpeg 6.1) must report the first stream's codec, sample rate and channels
  ("ffprobe_channels" where ffmpeg takes them from elsewhere, such as an MP4 box or the dependent
  substreams it mixes in), a channel layout that feeds the manifest's speakers (channel_layouts.py;
  ffmpeg places AC-3's only surround pair at the sides, the analyzer at the back as the WAVE channel
  mask does, so a lone pair counts as one position), and exactly the packets the manifest's frames
  make: ffmpeg's parser starts a packet at each AC-3 frame and each independent E-AC-3 frame, and
  joins dependent frames to the one before. For raw files, ffprobe's bitrate must be the first
  packet's, unless "ffprobe_bitrate" is false (ffmpeg gives none for frames it cannot decode).
- "decode": ffmpeg with CRC checking must decode each packet alone without an error, unless the
  packet holds a frame the manifest lists as one decoders reject ("damaged": cut or failing a CRC,
  or a dependent frame without its independent one), which it must reject, as it rejects the frames
  of independent substreams other than 0, which ffmpeg 6.1 does not decode. MP4 files are decoded
  whole, without an error.
- "liba52": liba52 (a52_blocks.c, built here) must end each audio block of every frame in
  "block_ends" at the manifest's bit, and must not reject those frames.
- "skip_insertion" (E-AC-3): with the analyzer's report (--analyzer), every frame is rewritten with
  skip fields of one byte of 0xA5 inserted where the report puts each block's mantissas, the
  skip field syntax switched on and the frame size and CRC updated; ffmpeg must decode the rewritten
  file to exactly the same samples as the original, which it only does when every block's mantissas
  start where the report says. As a negative control, the skip field of each block of the first
  frame is moved a bit earlier and a bit later in turn, and each move must change the samples;
  a block whose mantissas take no bits cannot show a wrong position, so a fixture of such blocks
  turns the control off ("skip_control": false) with its reason. A block's mantissas end where the
  next block's start; the last block's end is not checked this way.
- "skip": the checks that are not run on the fixture, each with its reason.

Object audio (ETSI TS 103 420) is compared with both tools, by mapping their words to the analyzer's rather than
copying them:

- ffprobe's stream profile "Dolby Digital Plus + Dolby Atmos" is ffmpeg's reading of flag_ec3_extension_type_a in
  the additional bit stream information of the frames it decodes (libavcodec/eac3dec.c, eac3_extension_type_a,
  and ac3dec.c, AV_PROFILE_EAC3_DDP_ATMOS), not of the EMDF payloads and not of the dec3 box (libavformat/mov.c
  reads neither dec3's extension nor the JOC payload; movenc.c writes none). It must name the profile exactly when
  a frame of the golden report sets HasExtensionTypeA.
- MediaInfo's "JOC" among the additional features (Format_AdditionalFeatures, "E-AC-3 JOC" as the format) is the
  analyzer's stream with object audio; its commercial name "Dolby Digital Plus with Dolby Atmos" follows from it;
  its ComplexityIndex is the dec3 box's complexity_index_type_a, "Not present" where the box does not set the
  flag, and the last frame's from the additional bit stream information (the analyzer's "Complexity Index Type
  A"), read only after a JOC payload has been seen (File_Ac3.cpp, Streams_Fill and Core_Frame); its
  NumberOfDynamicObjects is the object count of the last OAMD payload (object_audio_metadata_payload and
  program_assignment: the fixtures send dynamic objects only, so it equals the analyzer's "Object Count").
  MediaInfo takes a container wherever Ac3_EMDF_Test finds one before the frame's auxiliary data field; the
  fixtures that put it in that field, or break the test, name their evidence.

Every fixture is also compared with MediaInfo 24.01 (mediainfo_tool.py, check_mediainfo), against
its golden report: the format, dependent substreams, sample rate, channels and layout, bsid,
acmod, lfeon, bsmod (ServiceKind), dialnorm and its range, compr, dynrng, dsurmod and the Dolby
Surround modes, the nominal bitrate and mode, and the frames, samples, duration and stream size
(in MP4, the sample table's frame count). MediaInfo's own readings are asserted as such: it reads
the AC-3 mixlevel as 8 bits instead of A/52's 5, so every later field of a frame with production
information comes out 3 bits late, which mediainfo_bsi reproduces; it names the service "CM" where
bsmod is absent and gives dsurmod 0 where it is not coded. Fixtures on which it reads otherwise
name their evidence.

The analyzer's agreement with the manifests is checked by the tests (Ac3FixtureTests). Needs ffmpeg,
liba52-0.7.4-dev, mediainfo and a C compiler. Exits non-zero when anything disagrees.

    python3 scripts/fixtures/crosscheck_ac3_fixtures.py [--analyzer path/to/encspect]
"""

import json
import math
import pathlib
import re
import subprocess
import sys
import tempfile

import ac3_model as m
import channel_layouts
import mediainfo_tool as mi

ROOT = pathlib.Path(__file__).resolve().parent
FIXTURES = ROOT.parents[1] / "tests" / "fixtures" / "ac3"
CHECKS = ("ffprobe", "ffprobe_bitrate", "decode", "liba52", "skip_insertion", "skip_control")


def ffprobe(path):
    out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries",
                          "stream=codec_name,profile,sample_rate,channels,channel_layout,bit_rate:packet=pos,size", "-of", "json", str(path)],
                         capture_output=True, text=True, check=True).stdout
    return json.loads(out)


def packets_of(data, frames):
    out = []
    for offset, length in frames:
        h = m.header(data, offset)
        if out and h and h["eac3"] and h["dependent"]:
            out[-1][1] += length
        else:
            out.append([offset, length])
    return out


def normalized(speakers):
    speakers = list(speakers)
    if "side left" in speakers and "back left" not in speakers:
        speakers = ["back left" if s == "side left" else "back right" if s == "side right" else s for s in speakers]
    return sorted(speakers)


def frame_nodes(report):
    return [n for n in mi.walk(report["root"]) if n.get("kind") == "Frame"]


def extension_complexities(report):
    """Per frame, complexity_index_type_a where the frame's additional bit stream information sets
    flag_ec3_extension_type_a, else None."""
    out = []
    for frame in frame_nodes(report):
        flag = next(find(frame, "HasExtensionTypeA"), None)
        complexity = next(find(frame, "Complexity Index Type A"), None)
        out.append(complexity["value"] if flag is not None and flag.get("value") is True and complexity is not None else None)
    return out


def objects_of(node):
    return int(str(node["value"]).split()[0]) if isinstance(node["value"], str) else int(node["value"])


def mediainfo_complexity(report):
    """What MediaInfo 24.01 reports as ComplexityIndex (File_Ac3.cpp, Streams_Fill): the dec3 box's
    value or "Not present", and the frames' where it differs. The frames' value is set by a frame's
    additional bit stream information once a JOC payload has been parsed, and cleared by every
    container."""
    stream_value = None
    seen_joc = False
    for frame in frame_nodes(report):
        if next(find(frame, "EMDF Container"), None) is not None:
            stream_value = None
        if next(find(frame, "JOC Header"), None) is not None:
            seen_joc = True
        flag = next(find(frame, "HasExtensionTypeA"), None)
        complexity = next(find(frame, "Complexity Index Type A"), None)
        if seen_joc and flag is not None and flag.get("value") is True and complexity is not None:
            stream_value = complexity["value"]
    box = next(find(report["root"], "dec3"), None)
    container = None
    if box is not None:
        flag = next(find(box, "HasExtensionTypeA"), None)
        complexity = next(find(box, "Complexity Index Type A"), None)
        if flag is not None and flag.get("value") is True and complexity is not None:
            container = complexity["value"]
    parts = []
    if box is not None and container is not None:
        parts.append(container)
    if box is not None and container is None and stream_value is not None:
        parts.append("Not present")
    if stream_value is not None and stream_value != container:
        parts.append(stream_value)
    if box is not None and container is not None and stream_value is None:
        parts.append("Not present")
    return " / ".join(str(p) for p in parts) or None


def mediainfo_dynamic_objects(report):
    """The object count of the last frame that holds an OAMD payload, which MediaInfo reads as
    NumberOfDynamicObjects after its program_assignment of dynamic objects only."""
    last = None
    for frame in frame_nodes(report):
        for payload in find(frame, "Payload"):
            if str(payload.get("value", "")).startswith("11 "):
                count = next((c for c in payload.get("children", []) if c["name"] == "Object Count"), None)
                if count is not None:
                    last = objects_of(count)
    return last


def check_ffprobe(path, data, manifest, problems):
    probe = ffprobe(path)
    streams = probe.get("streams", [])
    if not streams:
        problems.append("ffprobe finds no audio stream")
        return
    actual, expected = streams[0], manifest["streams"][0]
    codec = "eac3" if expected["codec"] == "E-AC-3" else "ac3"
    channels = manifest["crosscheck"].get("ffprobe_channels", expected["channels"])
    if actual.get("codec_name") != codec or int(actual.get("sample_rate", 0)) != expected["sample_rate"] or int(actual.get("channels", 0)) != channels:
        problems.append(f"ffprobe: {actual.get('codec_name')}, {actual.get('sample_rate')} Hz, {actual.get('channels')} channels; expected {codec}, "
                        f"{expected['sample_rate']} Hz, {channels}")
    known = channel_layouts.FFMPEG.get(actual.get("channel_layout"))
    if "ffprobe_channels" not in manifest["crosscheck"] and (known is None or normalized(known) != normalized(expected["channel_layout"])):
        problems.append(f"ffprobe's channel layout {actual.get('channel_layout')}, expected {expected['channel_layout']}")
    flagged = extension_complexities(mi.golden(path))
    expected_profile = "Dolby Digital Plus + Dolby Atmos" if any(c is not None for c in flagged) else None
    if actual.get("profile") not in ([expected_profile] if expected_profile else [None, "unknown"]):
        problems.append(f"ffprobe's profile {actual.get('profile')!r}, expected {expected_profile!r} (flag_ec3_extension_type_a set in {sum(c is not None for c in flagged)} frames)")
    packets = [[int(p["pos"]), int(p["size"])] for p in probe.get("packets", [])]
    expected_packets = packets_of(data, manifest["frames"]) if manifest["format"] == "AC-3" else packets_of_samples(data, manifest["frames"])
    if packets != expected_packets:
        problems.append(f"ffprobe lists {len(packets)} packets, the manifest's frames make {len(expected_packets)}")
    if manifest["format"] == "AC-3" and expected_packets and manifest["crosscheck"].get("ffprobe_bitrate", True):
        offset, length = expected_packets[0]
        h = m.header(data, offset)
        rate = length * 8 * h["rate"] // (h["blocks"] * 256) if h["eac3"] else [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512,
                                                                                 576, 640][h["bitrate_code"]] * 1000 >> max(0, h["bsid"] - 8)
        if int(actual.get("bit_rate", 0)) != rate:
            problems.append(f"ffprobe's bitrate {actual.get('bit_rate')}, the first packet's {rate}")


def packets_of_samples(data, frames):
    """MP4 samples: ETSI TS 102 366 F.2 puts six blocks of every substream into one sample; the fixtures' samples start where ffmpeg's packets do."""
    return packets_of(data, frames)


def check_decode(path, data, manifest, problems, work):
    damaged = set(manifest["crosscheck"].get("damaged", []))
    if manifest["format"] != "AC-3":
        result = subprocess.run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-i", str(path), "-f", "null", "-"], capture_output=True, text=True)
        if result.stderr.strip():
            problems.append(f"ffmpeg reports errors: {result.stderr.strip().splitlines()[0]}")
        return
    kind = "eac3" if manifest["streams"][0]["codec"] == "E-AC-3" else "ac3"
    for offset, length in packets_of(data, manifest["frames"]):
        piece = work / "packet.bin"
        piece.write_bytes(data[offset:offset + length])
        result = subprocess.run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-f", kind, "-i", str(piece), "-f", "null", "-"],
                                capture_output=True, text=True)
        # ffmpeg 6.1 decodes independent substream 0 only (eac3dec.c), so it rejects the others.
        bad = any(f[0] in damaged for f in manifest["frames"] if offset <= f[0] < offset + length) or m.header(data, offset)["substream"] != 0
        failed = bool(result.stderr.strip())
        if failed != bad:
            problems.append(f"ffmpeg {'rejects' if failed else 'decodes'} the packet at {offset}, which the manifest calls {'damaged' if bad else 'intact'}")


def check_liba52(tool, path, manifest, problems):
    lines = subprocess.run([str(tool), str(path)], capture_output=True, text=True, check=True).stdout.splitlines()
    found = {line.split()[0]: line.split()[2:] for line in lines}
    for offset, ends in manifest.get("block_ends", {}).items():
        if found.get(offset) != [str(e) for e in ends]:
            problems.append(f"liba52 ends the blocks of the frame at {offset} at {found.get(offset)}, the manifest at {ends}")


def bits_of(data):
    return "".join(f"{b:08b}" for b in data)


def bytes_of(bits):
    return bytes(int(bits[i:i + 8], 2) for i in range(0, len(bits), 8))


def report_frames(analyzer, path):
    doc = json.loads(subprocess.run([analyzer, "--json", str(path)], capture_output=True, text=True, check=True).stdout)
    frames = []

    def walk(node):
        for child in node.get("children", []):
            if child.get("kind") == "Frame":
                frames.append(child)
            else:
                walk(child)

    walk(doc[0]["root"])
    return frames


def find(node, name):
    if node["name"] == name:
        yield node
    for child in node.get("children", []):
        yield from find(child, name)


def insert_skips(data, frames, moved=None):
    """Every frame with a one-byte skip field before each block's mantissas; moved = (frame, block,
    bits) shifts one of them."""
    rewritten = bytearray()
    for index, frame in enumerate(frames):
        start, length = frame["offset"], frame["length"]
        bits = bits_of(data[start:start + length])

        def bit(node):
            return (node["offset"] - start) * 8 + node.get("bitOffset", 0)

        flag = bit(next(find(frame, "HasSkipFields")))
        mantissas = [bit(n) for n in find(frame, "Mantissas")]
        if moved is not None and moved[0] == index:
            mantissas[moved[1]] += moved[2]
        out = bits[:flag] + "1"
        previous = flag + 1
        for position in mantissas:
            out += bits[previous:position] + "1" + f"{1:09b}" + "10100101"
            previous = position
        out += bits[previous:len(bits) - 17]
        out += "0" * (-(len(out) + 17) % 16)
        out += bits[len(bits) - 17]
        words = (len(out) + 16) // 16
        out = out[:21] + f"{words - 1:011b}" + out[32:]
        body = bytes_of(out)
        rewritten += body + m.crc16(body[2:]).to_bytes(2, "big")
    return bytes(rewritten)


def pcm(source):
    return subprocess.run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-f", "eac3", "-i", str(source), "-f", "s32le", "-"],
                          capture_output=True).stdout


def check_skip_insertion(analyzer, path, problems, work, control):
    data = path.read_bytes()
    frames = report_frames(analyzer, path)
    target = work / "rewritten.eac3"
    target.write_bytes(insert_skips(data, frames))
    original = pcm(path)
    if original != pcm(target):
        problems.append("ffmpeg decodes the file with skip fields at the report's mantissa positions to other samples")
    if not control:
        return
    blocks = sum(1 for _ in find(frames[0], "Mantissas"))
    for block in range(blocks):
        for shift in (-1, 1):
            target.write_bytes(insert_skips(data, frames, (0, block, shift)))
            if pcm(target) == original:
                problems.append(f"moving the skip field of block {block} of the first frame by {shift:+d} bit leaves the samples unchanged")


# MediaInfo's names for the bit stream modes (File_Ac3.cpp, AC3_Mode).
MEDIAINFO_SERVICES = ["CM", "ME", "VI", "HI", "D", "C", "E", "VO"]

# A/52 5.4.2.14 gives mixlevel 5 bits; MediaInfo reads 8 in an AC-3 frame (File_Ac3.cpp, "mixlevel -
# Mixing Level", Skip_S1(8, ...)), 5 in E-AC-3, so in a frame with audprodie it reads every later
# field 3 bits late: time codes, addbsi, Annex D's xbsi2 (dsurexmod, dheadphonmod) and dynrng.
MIXLEVEL = "MediaInfo reads the AC-3 mixlevel as 8 bits where A/52 5.4.2.14 gives 5 (File_Ac3.cpp, Skip_S1(8, \"mixlevel\")), so it reads the later fields of a frame with production information 3 bits late; compared with that reading (mediainfo_bsi)"
# File_Ac3.cpp, AC3_Channels and AC3_dynrng.
MEDIAINFO_CHANNELS = [2, 1, 2, 3, 3, 4, 4, 5]
MEDIAINFO_DYNRNG = [6.02, 12.04, 18.06, 24.08, -18.06, -12.04, -6.02, 0.00]


def mediainfo_bsi(data, offset):
    """An AC-3 frame's xbsi2 modes and first dynrng as MediaInfo reads them (File_Ac3.cpp, the
    bsid<=0x09 branch of Core_Frame), its 8-bit mixlevel and all."""
    bits = "".join(f"{b:08b}" for b in data[offset:offset + 64])
    position = 40
    def read(count):
        nonlocal position
        position += count
        return int(bits[position - count:position], 2)
    bsid, _, acmod = read(5), read(3), read(3)
    if acmod & 1 and acmod != 1:
        read(2)
    if acmod & 4:
        read(2)
    if acmod == 2:
        read(2)
    read(1 + 5)
    for _ in range(2 if acmod == 0 else 1):
        if read(1):
            read(8)
        if read(1):
            read(8)
        if read(1):
            read(8 + 2)
        if acmod == 0 and _ == 0:
            read(5)
    read(2)
    modes = {}
    if bsid == 6:
        if read(1):
            read(2 + 3 + 3 + 3 + 3)
        if read(1):
            modes = {"dsurexmod": read(2), "dheadphonmod": read(2)}
            read(1 + 8 + 1)
    else:
        for _ in range(2):
            if read(1):
                read(14)
    if read(1):
        read(8 * (read(6) + 1))
    read(2 * MEDIAINFO_CHANNELS[acmod])
    dynrng = read(8) if read(1) else None
    if dynrng is not None:
        modes["dynrng"] = 0.0 if dynrng == 0 else MEDIAINFO_DYNRNG[dynrng >> 5] + 20 * math.log10((0x20 + (dynrng & 0x1F)) / 64)
    return modes

# MediaInfo takes an E-AC-3 stream's bitrate from the first frame of each substream
# (File_Ac3.cpp, frmsizplus1_Max, set once) and calls it CBR (Streams_Fill, "CBR" by default);
# without a frame count of its own, it derives the duration from the file size and that bitrate, and
# the samples and stream size from the duration rounded to the millisecond.
FIRST_FRAME_RATE = "MediaInfo takes the bitrate of the first frame of each substream (File_Ac3.cpp, frmsizplus1_Max) and derives the duration, samples and stream size from it and the file size"
COMPLETE_FRAMES = "MediaInfo counts the complete frames as the file size over the first frame's size (File_Ac3.cpp, Streams_Finish: \"Only complete frames\"), where the analyzer counts every frame, the cut one with a finding"
DEPENDENT_FIRST = "MediaInfo adds the channels and bitrate of the dependent frame the stream starts with, which A/52 Annex E 2.3.1.2 places after an independent frame and the analyzer reports without it, and derives the samples, duration and stream size from that bitrate"

AUXILIARY_FIELD = "MediaInfo searches the frame for the container but parses it only if it starts before the auxiliary data field (File_Ac3.cpp, Core_Frame: BitsAtEnd<EMDF_RemainPos), which this fixture's container, inside that field, does not"

# Fixtures on which MediaInfo reads a field differently, with the evidence.
MEDIAINFO_READS_OTHERWISE = {
    "bsid-10": {"format": "MediaInfo knows the frame size of bsid 0 to 9 and 11 to 16 only (File_Ac3.cpp, FrameSynchPoint_Test), so it never syncs on bsid 10, which A/52 does not define (Annex E 2.3.1.6 has decoders mute bsid 9 and 10, as the analyzer's ac3 finding cites)"},
    "bsid-9": {"duration": "MediaInfo gives bsid 9 frames 16 ms (File_Ac3.cpp, Streams_Finish, \"Unofficial hack for low sample rate\"), which disagrees with its own 6144 samples at 24000 Hz, 0.256 s"},
    "cut-frame": dict.fromkeys(["frames", "samples", "duration", "stream size"], COMPLETE_FRAMES),
    "truncated": dict.fromkeys(["frames", "samples", "duration", "stream size"], COMPLETE_FRAMES),
    "dependent-first": dict.fromkeys(["channels", "channel layout", "bitrate", "samples", "duration", "stream size"], DEPENDENT_FIRST),
    "derived-ffmpeg-eac3-mono-48k-spx-delta": dict.fromkeys(["bitrate", "bitrate mode", "samples", "duration", "stream size"], FIRST_FRAME_RATE),
    "substreams": dict.fromkeys(["samples", "duration", "stream size"], FIRST_FRAME_RATE),
    "joc-auxiliary-data": {"joint object coding": AUXILIARY_FIELD},
    "joc-short": {"frames": "MediaInfo reads the one-byte JOC payload's joc_header and joc_info past the payload and then skips to the end of the data (File_Ac3.cpp, emdf_container: \"Problem during emdf_payload parsing, going to end directly\"), so it does not count that frame",
                  "joint object coding": "MediaInfo reads joc_header and joc_info without regard to the payload's size (File_Ac3.cpp, joc_header, joc_info), so the one-byte JOC payload gives it a second joc_num_objects, and it reports JOC only when all frames agree on one (HasJOC: joc_num_objects_map.size()==1)"},
    "external-ffmpeg-stereo-44k-64k": dict.fromkeys(["samples", "stream size"], "MediaInfo derives the samples and stream size from its duration rounded to the millisecond: 0.522 s at 44100 Hz is 23020 samples, at 64 kbps 4176 bytes, where 15 frames hold 23040 samples in 4180 bytes"),
}


def dialnorm(value):
    """A dialnorm as decoders read it: A/52 5.4.2.8 reads the reserved 0 as -31 dB, as the analyzer
    states and MediaInfo does (File_Ac3.cpp, "dialnorm==0?-31")."""
    match = re.search(r"read as (-?\d+) dB", str(value))
    return int(match.group(1)) if match else mi.leading_number(value)


def mediainfo_frames(report):
    return [n for n in mi.walk(report["root"]) if n.get("kind") == "Frame"]


def check_object_audio(audio, report, stream, features, tally):
    """MediaInfo's JOC reading against the analyzer's object audio, see the module's notes."""
    joc = "JOC" in features
    tally.compare("joint object coding", joc, bool(stream.get("hasObjectAudio")))
    if stream["codec"] != "E-AC-3":
        return
    tally.compare("commercial name", audio.get("Format_Commercial_IfAny"), "Dolby Digital Plus with Dolby Atmos" if joc else "Dolby Digital Plus")
    extra = audio.get("extra", {})
    tally.compare("complexity index", extra.get("ComplexityIndex"), mediainfo_complexity(report) if joc else None)
    objects = mediainfo_dynamic_objects(report)
    tally.compare("dynamic objects", extra.get("NumberOfDynamicObjects"), None if not joc or objects is None else str(objects))


def check_mediainfo(path, tally):
    """MediaInfo must report the format, rate, channels and layout, bit stream mode, bsid, acmod and
    lfeon, dialnorm, compr, dynrng and Dolby Surround modes of the first frames, the nominal bitrate,
    and the frames, samples, duration and stream size of the complete frames."""
    tally.start(path.stem)
    report = mi.golden(path)
    # dialnorm_Minimum and dialnorm_Maximum are only in the full output.
    info = mi.read(path, full=True)
    audio = info["Audio"][0] if info and info["Audio"] else {}
    stream = report["streams"][0]
    tally.compare("format", audio.get("Format"), stream["codec"])
    if not audio:
        return tally.problems
    frames = mediainfo_frames(report)
    dependent = [f for f in frames if mi.value(f, "Bit Stream Information", "Stream Type") == "1 (dependent)"]
    features = (audio.get("Format_AdditionalFeatures") or "").split()
    tally.compare("dependent substreams", " ".join(t for t in features if t != "JOC") or None, "Dep" if dependent else None)
    check_object_audio(audio, report, stream, features, tally)
    tally.compare("sample rate", audio.get("SamplingRate"), str(stream["sampleRate"]))
    tally.compare("channels", audio.get("Channels"), str(stream["channels"]))
    tally.compare("channel layout", mi.speakers(audio.get("ChannelLayout")), mi.normalized(stream["channelLayout"]))
    extra = audio.get("extra", {})
    first = next(f for f in frames if f not in dependent)
    bsi = mi.child(first, "Bit Stream Information")

    def field(frame, *names):
        return mi.leading_number(mi.value(frame, "Bit Stream Information", *names))

    tally.compare("bsid", extra.get("bsid"), str(max(field(f, "Bit Stream ID") for f in frames)))
    acmods = [str(field(f, "Audio Coding Mode")) for f in [first] + dependent[:1]]
    lfes = ["1" if mi.value(f, "Bit Stream Information", "HasLfe") else "0" for f in [first] + dependent[:1]]
    tally.compare("acmod", extra.get("acmod"), " / ".join(acmods))
    tally.compare("lfeon", extra.get("lfeon"), " / ".join(lfes))
    mode = mi.value(bsi, "Bit Stream Mode")
    if mode is None:
        # Without informational metadata an E-AC-3 frame carries no bsmod; MediaInfo then reports
        # CM (File_Ac3.cpp, bsmod=0).
        tally.compare("service kind without bsmod", audio.get("ServiceKind"), "CM")
    else:
        tally.compare("service kind", audio.get("ServiceKind"), MEDIAINFO_SERVICES[mi.leading_number(mode)])
    dialnorms = [dialnorm(mi.value(f, "Bit Stream Information", "Dialogue Normalization")) for f in frames if f not in dependent]
    tally.compare("dialnorm", extra.get("dialnorm"), str(dialnorms[0]))
    tally.compare("dialnorm minimum and maximum", (extra.get("dialnorm_Minimum"), extra.get("dialnorm_Maximum")), (str(min(dialnorms)), str(max(dialnorms))))
    compr = mi.value(bsi, "Compression Gain")
    tally.compare("compr", None if extra.get("compr") is None else round(float(extra["compr"]), 1), compr)
    # MediaInfo reads frames of bsid 0 to 9 with the AC-3 syntax (File_Ac3.cpp, bsid<=0x09).
    production = mi.value(bsi, "HasProductionInfo") and field(first, "Bit Stream ID") <= 9
    surround = mi.leading_number(mi.value(bsi, "Dolby Surround Mode"))
    if field(first, "Audio Coding Mode") != 2:
        pass
    elif surround is None:
        # Without informational metadata an E-AC-3 frame carries no dsurmod; MediaInfo then
        # reports 0 (File_Ac3.cpp, dsurmod=0).
        tally.compare("dsurmod without informational metadata", extra.get("dsurmod"), " / ".join(["0"] * len(acmods)))
    else:
        tally.compare("dsurmod", extra.get("dsurmod"), " / ".join([str(surround)] * len(acmods)))
    expected_mode = None
    if surround == 2:
        expected_mode = "Dolby Surround"
    if production:
        tally.skip("dynrng and surround mode against the frames", MIXLEVEL)
        read = mediainfo_bsi(path.read_bytes(), first["offset"])
        tally.compare("dynrng as MediaInfo misreads it", extra.get("dynrng"), None if read.get("dynrng") is None else f"{read['dynrng']:.2f}")
        misread = {2: "Dolby Surround EX", 3: "Dolby Pro Logic IIz"}.get(read.get("dsurexmod"), expected_mode)
        misread = "Dolby Headphone" if read.get("dheadphonmod") == 2 else misread
        tally.compare("surround mode as MediaInfo misreads it", audio.get("Format_Settings_Mode"), misread)
    else:
        block = next((c for c in first.get("children", []) if c.get("kind") == "SideInfo"), None)
        dynrng = mi.value(block, "Dynamic Range Gain") if block is not None and mi.value(block, "HasDynamicRange") else None
        if stream["codec"] == "E-AC-3":
            # MediaInfo does not read E-AC-3 audio blocks (File_Ac3.cpp, "Not finished, for reference only").
            tally.compare("E-AC-3 dynrng", extra.get("dynrng"), None)
        else:
            tally.compare("dynrng", None if extra.get("dynrng") is None else round(float(extra["dynrng"]), 2), dynrng)
        ex = mi.leading_number(mi.value(bsi, "Extended Information 2", "Dolby Surround EX Mode"))
        headphone = mi.leading_number(mi.value(bsi, "Extended Information 2", "Dolby Headphone Mode"))
        expected_mode = {2: "Dolby Surround EX", 3: "Dolby Pro Logic IIz"}.get(ex, expected_mode)
        expected_mode = "Dolby Headphone" if headphone == 2 else expected_mode
        tally.compare("surround mode", audio.get("Format_Settings_Mode"), expected_mode)
    statistics = next(n for n in mi.walk(report["root"]) if n.get("kind") == "Statistics")
    nominal = mi.leading_number(mi.value(statistics, "Nominal Bitrate"))
    tally.compare("bitrate", audio.get("BitRate"), str(math.floor((nominal or stream["averageBitrate"]) * 1000 + 0.5)))
    tally.compare("bitrate mode", audio.get("BitRate_Mode"), "VBR" if stream["isVbr"] else "CBR")
    contained = path.suffix == ".mp4"
    if contained:
        tally.compare("frames in the sample table", audio.get("Source_FrameCount", audio.get("FrameCount")), str(mi.value(statistics, "Frames") and mi.leading_number(mi.value(statistics, "Frames"))))
        return tally.problems
    independent = [f for f in frames if f not in dependent]
    tally.compare("frames", audio.get("FrameCount"), str(len(independent)))
    tally.compare("samples", audio.get("SamplingCount"), str(stream["samples"]))
    tally.compare("duration", audio.get("Duration"), f"{stream['duration']:.3f}")
    tally.compare("stream size", audio.get("StreamSize"), str(sum(f["length"] for f in frames)))
    return tally.problems


def main():
    analyzer = sys.argv[sys.argv.index("--analyzer") + 1] if "--analyzer" in sys.argv else None
    failures = checked = 0
    mi.require()
    tally = mi.Tally(MEDIAINFO_READS_OTHERWISE)
    with tempfile.TemporaryDirectory() as directory:
        work = pathlib.Path(directory)
        tool = work / "a52_blocks"
        subprocess.run(["cc", "-O2", "-o", str(tool), str(ROOT / "a52_blocks.c"), "-la52", "-lm"], check=True)
        for manifest_path in sorted(FIXTURES.glob("*.expected.json")):
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            stem = manifest_path.name[:-len(".expected.json")]
            path = next(p for p in FIXTURES.iterdir() if p.name.startswith(stem + ".") and not p.name.endswith(".json"))
            data = path.read_bytes()
            check = manifest["crosscheck"]
            problems = []
            for name in CHECKS:
                if check.get(name) is False and name not in check.get("skip", {}):
                    problems.append(f"the {name} check is off without a reason")
            if check.get("ffprobe"):
                check_ffprobe(path, data, manifest, problems)
            if check.get("decode"):
                check_decode(path, data, manifest, problems, work)
            if check.get("liba52"):
                check_liba52(tool, path, manifest, problems)
            if check.get("skip_insertion"):
                if analyzer is None:
                    print(f"{path.name}: the skip insertion check needs --analyzer; not run")
                else:
                    check_skip_insertion(analyzer, path, problems, work, check.get("skip_control", True))
            problems += check_mediainfo(path, tally)
            checked += 1
            for problem in problems:
                print(f"{path.name}: {problem}")
            failures += bool(problems)
    for problem in tally.unused():
        print(problem)
        failures += 1
    tally.print()
    print(f"{checked} fixtures checked, {failures} disagree.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
