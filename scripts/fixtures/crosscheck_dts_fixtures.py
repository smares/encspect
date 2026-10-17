#!/usr/bin/env python3
"""Checks the DTS fixtures' manifests (tests/fixtures/dts) against independent tools.

For every fixture, its manifest's "crosscheck" says what to confirm:

- "ffprobe": ffprobe (ffmpeg 8.0) must report the first stream's codec, sampling frequency (the
  extension's where the stream has one, such as 96 kHz with X96; "ffprobe_rate" where ffmpeg reads another), channels, profile (DTS, DTS-ES,
  DTS 96/24, DTS-HD Master Audio, DTS-HD High Resolution Audio, DTS Express, which ffmpeg writes "DTS-HD MA" and "DTS-HD HRA"; "ffprobe_profile": false where ffmpeg reads the
  fixture otherwise), a channel layout that feeds the manifest's speakers (channel_layouts.py;
  ffmpeg places a lone surround pair at the sides, the analyzer at the back as the WAVE channel
  mask does, so a lone pair counts as one position), the nominal bitrate the first frame's RATE
  gives ("ffprobe_bitrate": false where ffmpeg reads it otherwise), and exactly the packets the
  manifest's frames make: ffmpeg's parser starts a packet at each core frame and takes the extension
  substreams after it into the same packet. Raw files are read as "-f dts", since ffmpeg's probe
  looks from byte 4096 on and the fixtures are smaller.
- "decode": ffmpeg with CRC checking must decode each packet alone without an error, unless the
  packet holds a frame the manifest lists as one decoders reject ("damaged": offsets of frames that
  are cut or invalid), which it must reject. MP4 files are decoded whole.
- "libdca": libdca (dca_frames.c, built here) must find the manifest's frames, with the sampling
  frequency, channels ("libdca_channels" where libdca reads the core only), samples per frame and
  nominal bitrate the manifest and the golden report give, and decode each without an error (dca_frame
  and dca_block), except the frames the manifest lists as damaged. libdca reads core frames only and
  skips extension substreams, junk and tags a byte at a time.
- "skip": the checks that are not run on the fixture, each with its reason; every one is printed.

Every fixture is also compared with MediaInfo 26.01 (mediainfo_tool.py, check_mediainfo), against its
golden report: the format and commercial name, the form of the stream (word size and byte order), the
sampling frequency, channels and layout, bit depth, the nominal bitrate and its mode, the samples per
frame, and the frames, samples and duration. MediaInfo's own readings are asserted as such, with the
source line that explains them (File_Dts.cpp of MediaInfoLib v26.01, which differs from v24.01 only in a buffer size, declarations and one DTS-HD identifier); fixtures on which it reads
otherwise name their evidence, and an explained difference that no longer occurs fails the run.

The analyzer's agreement with the manifests is checked by the tests (DtsFixtureTests). Needs ffmpeg,
libdca-dev (headers and library; or LIBDCA_INCLUDE and LIBDCA_LIB), mediainfo and a C compiler. Exits
non-zero when anything disagrees.

    python3 scripts/fixtures/crosscheck_dts_fixtures.py
"""

import json
import os
import pathlib
import subprocess
import sys
import tempfile

import channel_layouts
import mediainfo_tool as mi
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parent
FIXTURES = ROOT.parents[1] / "tests" / "fixtures" / "dts"
CHECKS = ("ffprobe", "ffprobe_profile", "ffprobe_bitrate", "ffprobe_packets", "decode", "libdca")

# ffmpeg's profile names (libavcodec/profiles.c, ff_dca_profiles) for the analyzer's codec names, which are written out in full.
# The DTS:X profiles are dca_xll.c's AV_PROFILE_DTS_HD_MA_X and AV_PROFILE_DTS_HD_MA_X_IMAX.
PROFILES = {"DTS": "DTS", "DTS-ES": "DTS-ES", "DTS 96/24": "DTS 96/24", "DTS-HD Master Audio": "DTS-HD MA",
            "DTS-HD High Resolution Audio": "DTS-HD HRA", "DTS Express": "DTS Express",
            "DTS-HD Master Audio + DTS:X": "DTS-HD MA + DTS:X", "DTS-HD Master Audio + DTS:X IMAX": "DTS-HD MA + DTS:X IMAX"}

# libdca's channel counts by arrangement code (dca.h), without the LFE flag.
LIBDCA_CHANNELS = {0: 1, 1: 2, 2: 2, 3: 2, 4: 2, 5: 3, 6: 3, 7: 4, 8: 4, 9: 5, 10: 6}


def nominal_bitrate(report):
    """The nominal bitrate in bit/s the golden report's statistics give, or None where they give none or several."""
    statistics = next((n for n in mi.walk(report["root"]) if n.get("kind") == "Statistics"), None)
    text = str(mi.value(statistics, "Nominal Bitrate")) if statistics else ""
    if "," in text:
        return None
    number = mi.leading_number(text)
    return None if number is None else round(number * 1000)


def statistic(report, name):
    """The value of the named node of the golden report's first statistics, or None."""
    statistics = next((n for n in mi.walk(report["root"]) if n.get("kind") == "Statistics"), None)
    return mi.value(statistics, name) if statistics else None


def core_rate(report, fallback):
    """The sampling frequency of the core frames, which libdca reads, from the golden report's statistics; the stream's rate is the highest the extensions state."""
    statistics = next((n for n in mi.walk(report["root"]) if n.get("kind") == "Statistics"), None)
    number = mi.leading_number(str(mi.value(statistics, "Sampling Frequency"))) if statistics else None
    return int(number) if number else fallback


def extension_rate(manifest):
    value = manifest["fields"].get("DTS/Statistics/Extension Sampling Frequency")
    return int(value.split()[0]) if value else None


def normalized(speakers):
    speakers = list(speakers)
    if "side left" in speakers and "back left" not in speakers:
        speakers = ["back left" if s == "side left" else "back right" if s == "side right" else s for s in speakers]
    return sorted(speakers)


def ffprobe(path, manifest):
    raw = path.suffix == ".dts"
    out = subprocess.run(["ffprobe", "-v", "error", *(["-f", "dts"] if raw else []), "-select_streams", "a", "-show_entries",
                          "stream=codec_name,profile,sample_rate,channels,channel_layout,bit_rate,bits_per_raw_sample:stream_tags=:packet=pos,size:format=format_name,duration:format_tags=fileinfo",
                          "-of", "json", str(path)],
                         capture_output=True, text=True, check=True).stdout
    return json.loads(out)


def check_ffprobe(path, manifest, report, problems):
    probe = ffprobe(path, manifest)
    streams = probe.get("streams", [])
    check = manifest["crosscheck"]
    if not streams:
        problems.append("ffprobe finds no audio stream")
        return
    actual, expected = streams[0], manifest["streams"][0]
    rate = check.get("ffprobe_rate") or extension_rate(manifest) or expected["sample_rate"]
    channels = check.get("ffprobe_channels", expected["channels"])
    if actual.get("codec_name") != "dts" or int(actual.get("sample_rate", 0)) != rate or int(actual.get("channels", 0)) != channels:
        problems.append(f"ffprobe: {actual.get('codec_name')}, {actual.get('sample_rate')} Hz, {actual.get('channels')} channels; expected dts, {rate} Hz, {channels}")
    if check.get("ffprobe_profile", True) and actual.get("profile") != PROFILES[expected["codec"]]:
        problems.append(f"ffprobe's profile {actual.get('profile')}, expected {PROFILES[expected['codec']]}")
    known = channel_layouts.FFMPEG.get(actual.get("channel_layout"))
    if "ffprobe_channels" not in check and (known is None or normalized(known) != normalized(expected["channel_layout"])):
        problems.append(f"ffprobe's channel layout {actual.get('channel_layout')}, expected {expected['channel_layout']}")
    storage = statistic(report, "Lossless Storage Bit Width")
    if check.get("decode") and check.get("ffprobe_profile", True) and expected["codec"].startswith("DTS-HD Master Audio") and storage is not None:
        # dca_xll.c: avctx->bits_per_raw_sample = the channel sets' storage bit width.
        if int(actual.get("bits_per_raw_sample") or 0) != mi.leading_number(storage):
            problems.append(f"ffprobe's bits per raw sample {actual.get('bits_per_raw_sample')}, the golden report's storage bit width {storage}")
    packets = [[int(p["pos"]), int(p["size"])] for p in probe.get("packets", [])]
    expected_packets = check.get("ffprobe_packets", manifest["frames"])
    if expected_packets is not False and packets != expected_packets:
        problems.append(f"ffprobe lists {len(packets)} packets {packets[:3]}..., the manifest's frames make {len(expected_packets)} {expected_packets[:3]}...")
    if isinstance(check.get("ffprobe_packets"), list):
        print(f"{path.name}: ffprobe's packets differ from the frames: {check['ffprobe_packets_reason']}")
    nominal = nominal_bitrate(report)
    actual_rate = int(actual.get("bit_rate", 0) or 0)
    if check.get("ffprobe_bitrate", True):
        if nominal is None:
            print(f"{path.name}: skips the bitrate: the frames state more than one nominal bitrate or an open one")
        elif actual_rate != nominal:
            problems.append(f"ffprobe's bitrate {actual.get('bit_rate')}, the first frame's RATE gives {nominal}")


def decode_stderr(data, work, level="error"):
    piece = work / "piece.dts"
    piece.write_bytes(data)
    result = subprocess.run(["ffmpeg", "-v", level, "-err_detect", "crccheck", "-f", "dts", "-i", str(piece), "-f", "null", "-"], capture_output=True, text=True)
    return result.stderr.strip()


def check_decode(path, manifest, problems, work):
    check = manifest["crosscheck"]
    if check.get("decode_note"):
        print(f"{path.name}: ffmpeg decodes the damaged frames: {check['decode_note']}")
    if path.suffix != ".dts":
        result = subprocess.run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-i", str(path), "-f", "null", "-"], capture_output=True, text=True)
        if result.stderr.strip():
            problems.append(f"ffmpeg reports errors: {result.stderr.strip().splitlines()[0]}")
        return
    data = path.read_bytes()
    packets = [[int(p["pos"]), int(p["size"])] for p in ffprobe(path, manifest).get("packets", [])]
    for offset, length in packets:
        errors = decode_stderr(data[offset:offset + length], work)
        if errors:
            problems.append(f"ffmpeg rejects the packet at {offset}: {errors.splitlines()[0]}")


def check_decode_errors(path, manifest, problems, work):
    """Each frame alone: ffmpeg must report the error the manifest names for it (a substring of its first message), or none."""
    check = manifest["crosscheck"]
    data = path.read_bytes()
    expectations = {}
    if isinstance(check.get("decode_error"), str):
        for offset, length in manifest["frames"]:
            expectations[offset] = (length, check["decode_error"])
    for key, value in check.get("decode_errors", {}).items():
        length = value[0] if isinstance(value, list) else next(l for o, l in manifest["frames"] if o == int(key))
        expectations[int(key)] = (length, value[1] if isinstance(value, list) else value)
    for offset, (length, wanted) in sorted(expectations.items()):
        errors = decode_stderr(data[offset:offset + length], work, check.get("decode_log", "error"))
        first = errors.splitlines()[0] if errors else None
        if wanted is None and first is not None or wanted is not None and (first is None or wanted not in errors):
            problems.append(f"ffmpeg's error for the frame at {offset} is {first!r}, the manifest names {wanted!r}")


def build_libdca(work):
    include = os.environ.get("LIBDCA_INCLUDE")
    library = os.environ.get("LIBDCA_LIB")
    command = ["cc", "-O2", "-o", str(work / "dca_frames"), str(ROOT / "dca_frames.c")]
    command += [f"-I{include}"] if include else []
    command += [library] if library else ["-ldca"]
    command += ["-lm"]
    subprocess.run(command, check=True)
    return work / "dca_frames"


def check_libdca(tool, path, manifest, report, problems):
    lines = subprocess.run([str(tool), str(path)], capture_output=True, text=True, check=True, env=dict(os.environ)).stdout.splitlines()
    found = [line.split() for line in lines]
    check = manifest["crosscheck"]
    offsets = [int(f[0]) for f in found]
    expected = [f[0] for f in manifest["frames"]]
    if offsets != expected:
        problems.append(f"libdca finds frames at {offsets[:6]}..., the manifest at {expected[:6]}...")
    damaged = set(check.get("libdca_damaged", []))
    nominal = nominal_bitrate(report)
    # The stream each frame belongs to: the streams' samples add up frame by frame.
    streams = [dict(s) for s in manifest["streams"]]
    if len(streams) == 1:
        # The stream's rate and samples are those it delivers, the highest the extensions state; libdca reads the core's.
        core = core_rate(report, streams[0]["sample_rate"])
        streams[0]["samples"] = round(streams[0]["samples"] * core / streams[0]["sample_rate"])
        streams[0]["sample_rate"] = core
    index, used = 0, 0
    for offset, length, flags, rate, bitrate, samples, status in found:
        if used >= streams[index]["samples"] and index + 1 < len(streams):
            index, used = index + 1, 0
        stream = streams[index]
        used += int(samples)
        if int(rate) != stream["sample_rate"]:
            problems.append(f"libdca reads {rate} Hz in the frame at {offset}, the manifest {stream['sample_rate']}")
        channels = LIBDCA_CHANNELS[int(flags) & 0x3F] + (1 if int(flags) & 0x80 else 0)
        if channels != check.get("libdca_channels", stream["channels"]):
            problems.append(f"libdca reads {channels} channels in the frame at {offset}, the manifest {check.get('libdca_channels', stream['channels'])}")
        if len(streams) == 1 and nominal is not None and int(bitrate) != nominal:
            problems.append(f"libdca reads {bitrate} bit/s in the frame at {offset}, the first frame's RATE gives {nominal}")
        if (status == "ok") == (int(offset) in damaged):
            problems.append(f"libdca {'decodes' if status == 'ok' else 'rejects'} the frame at {offset}, which the manifest calls {'damaged' if int(offset) in damaged else 'intact'}")
    total = sum(int(f[5]) for f in found)
    if total != sum(s["samples"] for s in streams):
        problems.append(f"libdca's frames hold {total} samples, the manifest's streams {sum(s['samples'] for s in streams)}")


# MediaInfo 26.01 reads these fixtures differently from the analyzer; the evidence is in MediaInfoLib v26.01,
# Source/MediaInfo/Audio/File_Dts.cpp. Each entry must still differ, or the run fails.
CORE_ONLY = ("MediaInfo takes a stream as DTS-HD only where it finds the component's sync word in the substream (File_Dts.cpp, Extensions: XLL() sets "
             "presence_Extended_XLL); {why}, so it reports the core: the format of the core, its bitrate, constant")
MIXED = ["commercial name", "bitrate", "bitrate mode"]
MEDIAINFO_READS_OTHERWISE = {
    "exss-bad-component-sync": dict.fromkeys(MIXED, CORE_ONLY.format(why="the lossless component does not start with it, which the analyzer reports as dts.component-sync "
                                                                        "and still names the stream from the asset descriptor's component mask")),
    "exss-bad-crc": dict.fromkeys(MIXED, CORE_ONLY.format(why="it checks the substream header's CRC16 first and skips a substream that fails it (Header - CRC NOK), which the "
                                                              "analyzer reports as dts.crc-mismatch and reads on")),
    "exss-sizes-overrun": dict.fromkeys(MIXED, CORE_ONLY.format(why="it cuts the asset at the end of the substream, 40 bytes of the 160 its size gives, and finds only the core "
                                                                    "component in them; the analyzer reports dts.substream-overrun and names the stream from the descriptor")),
    "exss-two-assets": dict.fromkeys(
        ["format", "commercial name", "channels", "bitrate", "bitrate mode"],
        "MediaInfo reads each asset descriptor's static fields in turn and never skips to the next descriptor by its size (File_Dts.cpp, AssetDescriptor loop, "
        "AssetDescriptFsize is only shown), so the second asset's fields are read from the first one's dynamic fields (127 channels; the lossless component's channel sets, which MediaInfo reads in the first asset, give the sampling frequency, bit depth and samples per frame their right values)"),
    "rate-code": {"bitrate": "MediaInfo gives no bitrate where RATE is not one of the 25 codes of Table 5-7, 29 for open or Unknown (File_Dts.cpp, Streams_Fill_Core: bit_rate<25)"},
    "reserved-values": dict.fromkeys(
        ["commercial name", "channels", "channel layout", "bit depth"],
        "MediaInfo splits PCMR into a two-bit resolution and the ES flag (File_Dts.cpp, Core: bits_per_sample, ES) and so takes PCMR 7, which Table 5-17 lists as invalid, "
        "for 24 bits with the ES flag; it counts LFF 3 as an LFE channel and the ES flag as one more, and lays out ES only for arrangement 3/2 (Streams_Fill_Core_ES)"),
    "user-defined-arrangement": dict.fromkeys(
        ["channels", "channel layout"], "MediaInfo gives no channels for a user defined arrangement (File_Dts.cpp, Streams_Fill_Core: Core_Core_AMODE<16), where the "
                                       "analyzer reports the two channels the core codes as discrete"),
    "xxch": dict.fromkeys(
        ["commercial name", "channels", "channel layout"], "MediaInfo names a core with XXCH \"DTS\" with the feature XXCH and counts only the core's channels "
                                                       "(File_Dts.cpp, Streams_Fill: presence_Core_XXCH fills the core), where the analyzer adds the extension's channels"),
}

# Fixtures added after the first reading of MediaInfo's source, where MediaInfo 26.01's reading was measured: the reason says so.
MEASURED = "measured with MediaInfo 26.01"
BYTES_14 = ("MediaInfo counts the bytes of a 14-bit frame as FSIZE + 1 times 8 over 7, rounded down, and of a 16-bit frame as FSIZE + 1, where a frame that ends inside a word "
            "takes the whole word in the file (" + MEASURED + ": 585 bytes for the frame of 586), so its bitrate is a little under the one the frames' lengths give")
LITTLE_ENDIAN_EXSS = ("MediaInfo finds an extension substream behind a core frame in the big endian 16-bit form only (" + MEASURED + ": it reports the core of these little endian files alone)")
FOURTEEN_BIT_EXTENSION = "MediaInfo finds the extensions of a core frame in the 16-bit forms only (" + MEASURED + ": it reports the core of these 14-bit files alone)"
for fixture_name in ("core-odd-size-14be", "core-odd-size-14le", "core-odd-size-16le"):
    MEDIAINFO_READS_OTHERWISE[fixture_name] = {"bitrate": BYTES_14}
for form in ("14be", "14le"):
    for source in ("extension-missing", "xch", "x96", "xxch", "xxch-header-overrun"):
        MEDIAINFO_READS_OTHERWISE[f"{form}-{source}"] = {"bitrate": BYTES_14}
    MEDIAINFO_READS_OTHERWISE[f"{form}-xch"].update(dict.fromkeys(["commercial name", "channels", "channel layout"], FOURTEEN_BIT_EXTENSION))
    MEDIAINFO_READS_OTHERWISE[f"{form}-x96"].update(dict.fromkeys(["commercial name", "sampling rate", "samples per frame"], FOURTEEN_BIT_EXTENSION))
    MEDIAINFO_READS_OTHERWISE[f"{form}-xxch"].update(MEDIAINFO_READS_OTHERWISE["xxch"])
    MEDIAINFO_READS_OTHERWISE[f"{form}-xxch-header-overrun"]["commercial name"] = FOURTEEN_BIT_EXTENSION
MEDIAINFO_READS_OTHERWISE["16le-xxch"] = MEDIAINFO_READS_OTHERWISE["xxch"]
MEDIAINFO_READS_OTHERWISE["xxch-bad-crc"] = MEDIAINFO_READS_OTHERWISE["xxch"]
for fixture_name in ("xxch-header-overrun", "16le-xxch-header-overrun"):
    MEDIAINFO_READS_OTHERWISE[fixture_name] = {"format": "MediaInfo 26.01 does not recognize the file as DTS (" + MEASURED + "): the XXCH extension of 12 bytes ends the core frame inside its header"}
for fixture_name in ("exss-lossless", "exss-bad-crc", "exss-bad-component-sync", "exss-descriptor-overrun", "exss-sizes-overrun", "exss-two-assets", "exss-assets-overrun", "exss-component-header-overrun", "exss-high-resolution"):
    MEDIAINFO_READS_OTHERWISE.setdefault("le16-" + fixture_name, {}).update(dict.fromkeys(MIXED, LITTLE_ENDIAN_EXSS))
MEDIAINFO_READS_OTHERWISE["exss-component-header-overrun"] = dict.fromkeys(MIXED, CORE_ONLY.format(why="the lossless component is a sync word and four bytes, which MediaInfo does not take for the component (" + MEASURED + "), where the analyzer reports dts.header-overrun and names the stream from the descriptor"))
MEDIAINFO_READS_OTHERWISE["exss-assets-overrun"] = dict.fromkeys(MIXED, CORE_ONLY.format(why="it ends the asset where its size says, 60 bytes, and finds no component's sync word in the first 60 bytes, which the analyzer reports as dts.substream-overrun and names the stream from the descriptor (" + MEASURED + ")"))
MEDIAINFO_READS_OTHERWISE["xbr-bad-crc"] = dict.fromkeys(MIXED, CORE_ONLY.format(why="it checks the XBR frame header's CRC16 and leaves a component that fails it (" + MEASURED + "), which the analyzer reports as dts.crc-mismatch and reads on"))
MEDIAINFO_READS_OTHERWISE["exss-misaligned"] = dict.fromkeys(MIXED, "MediaInfo looks for the extension substream right after the core frame, not where 32-bit alignment puts it, and reads this one (" + MEASURED + "), where the analyzer and FFmpeg's decoder (dcadec.c) do not")
for fixture_name in ("le16-exss-high-resolution", "xbr-bad-crc"):
    MEDIAINFO_READS_OTHERWISE[fixture_name].pop("bitrate mode")
MEDIAINFO_READS_OTHERWISE["xll-future-version"] = dict.fromkeys(
    MIXED, "MediaInfo's XLL() returns at once for a lossless extension whose version is not 0 (File_Dts.cpp: Version=Begin>>12, if (Version) return), so it finds no lossless "
           "extension and reports the core: the format of the core, its bitrate, constant")
MEDIAINFO_READS_OTHERWISE["xll-lt-rt"] = {"channel layout": "MediaInfo reads the channel mask of a lossless channel set only where each channel feeds a loudspeaker (File_Dts.cpp, XLL(): "
                                                          "One2OneMapChannels2Speakers), so it gives an Lt/Rt asset no channel layout (measured with MediaInfo 26.01)"}
MEDIAINFO_READS_OTHERWISE["xll-x-imax-d1"] = dict.fromkeys(
    ["commercial name", "IMAX feature"], "FFmpeg's dca_xll.c takes 0xF14000D0 and 0xF14000D1 for DTS:X IMAX (it ignores the lowest bit), MediaInfo's Extensions2 only 0xF14000D0, and reads "
                                         "0xF14000D1 as DTS:X (File_Dts.cpp: case 0x02000850 and 0xF14000D1 set presence_Extended_X alone)")
MEDIAINFO_READS_OTHERWISE["xll-x-rejected-set"] = dict.fromkeys(
    ["commercial name", "objects in the channel layout", "DTS:X feature"], "FFmpeg's dca_xll.c refuses the channel set (a source resolution beyond its storage width) in parse_sub_headers before it looks for the "
    "DTS:X word, and so does the analyzer; MediaInfo's XLL() reads the word after the band data whatever the sets hold (measured with MediaInfo 26.01) and reports DTS:X")
for fixture_name, refusal in (
    ("xll-x-downmix-type-7", "downmix type 7"),
    ("xll-x-storage-width-18", "a storage width of 18 bits, which it does not implement"),
    ("xll-x-no-mask", "a disabled channel mask, which it does not implement"),
    ("xll-x-segment-samples", "segments of more than 512 samples (parse_common_header)"),
    ("xll-x-no-segment-samples", "a segment of no samples (parse_common_header)"),
):
    MEDIAINFO_READS_OTHERWISE[fixture_name] = dict.fromkeys(
        ["commercial name", "objects in the channel layout", "DTS:X feature"], f"FFmpeg's dca_xll.c refuses {refusal} in the common header or the sub-headers before it looks for the DTS:X word, "
        "and so does the analyzer; MediaInfo's XLL() reads the word after the band data whatever the header holds (measured with MediaInfo 26.01) and reports DTS:X")
MEDIAINFO_READS_OTHERWISE["exss-remapping-without-mask"] = {"channel layout": "FFmpeg refuses an asset with speaker remapping sets and no speaker activity mask (dca_exss.c), and the analyzer reports it; MediaInfo's "
    "extension substream parser stops reading the asset descriptor at the first remapping set (File_Dts.cpp: 'break; //Not finnished!') and gives the asset no speaker mask, so no channel layout (measured with MediaInfo 24.01)"}
MEDIAINFO_READS_OTHERWISE["exss-only-static-first-only"] = {"bitrate": "MediaInfo's BitRate_Get(true) adds the substream size HD_size of the last extension substream header it parsed, here a unit without static fields "
    "of 52 bytes, to the frame duration code it kept from the first (39000 bit/s), where the manifest's first unit has 60 bytes (45000 bit/s) (measured with MediaInfo 24.01)"}
MEDIAINFO_READS_OTHERWISE["rate-lossless"] = {"bitrate": MEDIAINFO_READS_OTHERWISE["rate-code"]["bitrate"]}
for amode in range(10, 16):
    MEDIAINFO_READS_OTHERWISE[f"amode-{amode}"] = {"channel layout": "MediaInfo's speaker positions for the arrangements above 9 differ from Table 5-4's (" + MEASURED + "); the analyzer's rest on the table"}

# MediaInfo's commercial names (File_Dts.cpp, Streams_Fill) for the analyzer's codec names.
COMMERCIAL = {"DTS": "DTS", "DTS-ES": "DTS-ES Discrete", "DTS 96/24": "DTS 96/24", "DTS Express": "DTS Express",
              "DTS-HD Master Audio + DTS:X": "DTS-HD MA + DTS:X", "DTS-HD Master Audio + DTS:X IMAX": "DTS-HD MA + IMAX Enhanced"}


def frames_of(report):
    stream = report["streams"][0]
    return [n for index in stream["nodes"] for n in mi.walk(mi.stream_node(report, {"nodes": [index]})) if n.get("kind") == "Frame"]


def check_mediainfo(path, tally):
    """MediaInfo must report the format and its commercial name, the word size and byte order of the stream, the sampling frequency,
    channels and layout, the bit depth, the bitrate its first frame gives (File_Dts.cpp, BitRate_Get: the frame's bytes in the file over
    its samples), the bitrate mode and the samples per frame."""
    tally.start(path.stem)
    report = mi.golden(path)
    info = tally.read(path, full=True)
    audio = info["Audio"][0] if info and info["Audio"] else {}
    if not report["streams"]:
        tally.skip("everything", "the file holds no audio stream")
        return tally.problems
    stream = report["streams"][0]
    tally.compare("format", audio.get("Format"), "DTS LBR" if stream["codec"] == "DTS Express" else "DTS")
    if not audio:
        return tally.problems
    if path.suffix == ".mp4":
        tally.skip("form and layout", "MediaInfo reads MP4's DTS from the sample entry (File_Mpeg4), without the stream's form or channels")
        return tally.problems
    frames = frames_of(report)
    first = frames[0]
    core = mi.child(first, "Core Substream")
    sync = mi.child(core, "Synchronization", "Sync Word")
    tally.compare("commercial name", audio.get("Format_Commercial"), COMMERCIAL.get(stream["codec"], stream["codec"]))
    if sync is not None:
        tally.compare("word size", audio.get("Format_Settings_Mode"), str(mi.leading_number(mi.value(sync, "Word Size"))))
        tally.compare("byte order", audio.get("Format_Settings_Endianness"), "Big" if mi.value(sync, "IsBigEndian") else "Little")
    statistics = next(n for n in mi.walk(report["root"]) if n.get("kind") == "Statistics")
    extension = mi.leading_number(mi.value(statistics, "Extension Sampling Frequency"))
    tally.compare("sampling rate", audio.get("SamplingRate"), str(extension or stream["sampleRate"]))
    tally.compare("channels", audio.get("Channels"), str(stream["channels"]))
    layout = (audio.get("ChannelLayout") or "")
    # File_Dts.cpp, Streams_Fill: the DTS:X and IMAX cases add " Objects" to the layout.
    has_x = statistic(report, "HasDtsX") is True or statistic(report, "HasDtsXImax") is True
    tally.compare("objects in the channel layout", str(layout.endswith(" Objects")), str(has_x))
    tally.compare("channel layout", mi.speakers(layout.removesuffix(" Objects")), mi.normalized(stream["channelLayout"]))
    tally.compare("bit depth", audio.get("BitDepth"), str(stream.get("bitsPerSample")))
    if stream["codec"].startswith("DTS-HD Master Audio"):
        # File_Dts.cpp, Streams_Fill: the features "XLL", then "X" and "IMAX" (Extensions2 sets X for both words, IMAX for 0xF14000D0 alone).
        features = (audio.get("Format_AdditionalFeatures") or "").split()
        tally.compare("DTS:X feature", str("X" in features), str(has_x))
        tally.compare("IMAX feature", str("IMAX" in features), str(statistic(report, "HasDtsXImax") is True))
    exss = mi.child(first, "Extension Substream")
    blocks = mi.leading_number(mi.value(core, "Frame Header", "Sample Blocks")) if core is not None else None
    rate = mi.leading_number(mi.value(core, "Frame Header", "Sampling Frequency")) if core is not None else stream["sampleRate"]
    samples = (blocks or 16) * 32
    lossless = stream["codec"].startswith("DTS-HD Master Audio")
    # File_Dts.cpp, BitRate_Get: the core's frame bytes in the file over its samples (none where an LBR component stands in for the core),
    # plus the extension substream's bytes over its samples (WithHD); a lossless stream has no bitrate, and is variable.
    expected = None
    if not lossless:
        extended = stream["codec"] in ("DTS-HD High Resolution Audio", "DTS Express")
        expected = 0 if stream["codec"] == "DTS Express" or core is None else core["length"] * 8 * rate / samples
        expected += exss["length"] * 8 * stream["sampleRate"] / samples if extended and exss is not None else 0
    if path.suffix == ".dtshd":
        tally.skip("bitrate", "MediaInfo gives a .dtshd file's bit rate from the stream chunk's size over the duration (measured with MediaInfo 26.01: 2,944 bytes in 43 ms are 547,721 bit/s); "
                              "its nominal and maximum bit rates are compared with the chunks")
    else:
        tally.compare("bitrate", audio.get("BitRate"), None if expected is None else str(round(expected)))
    if core is not None:
        tally.compare("samples per frame", audio.get("SamplesPerFrame"), str(round(samples * (extension or rate) / rate)))
    tally.compare("bitrate mode", audio.get("BitRate_Mode"), "VBR" if lossless else "CBR")
    tally.skip("duration, samples and stream size", "MediaInfo derives them from the file size and the first frame's bitrate (File_Dts.cpp, Streams_Finish)")
    return tally.problems


def chunk(report, chunk_id):
    return next((c for c in report["root"]["children"] if c["name"] == chunk_id), None)


def check_dtshd(path, manifest, report, problems, tally):
    """The container: ffprobe's demuxer (dtshddec.c) and MediaInfo (File_Dts.cpp, FileHeader_Parse and Header_Begin) must read the chunks as the golden
    report does: the format, FILEINFO's text, the length AUPR-HDR gives, and MediaInfo's frame and sample counts, nominal and maximum bit rates, stream size
    and time code."""
    probe = ffprobe(path, manifest) if manifest["crosscheck"].get("ffprobe") else {}
    if "format" in probe and probe["format"].get("format_name") != "dtshd":
        problems.append(f"ffprobe's format is {probe['format'].get('format_name')}, expected dtshd")
    info = chunk(report, "FILEINFO")
    fileinfo = probe.get("format", {}).get("tags", {}).get("fileinfo")
    if info is not None and probe and fileinfo != mi.value(info, "Text"):
        problems.append(f"ffprobe's fileinfo tag is {fileinfo!r}, FILEINFO's text {mi.value(info, 'Text')!r}")
    presentation = chunk(report, "AUPR-HDR")
    audio_info = tally.read(path, full=True)
    audio = audio_info["Audio"][0] if audio_info and audio_info["Audio"] else {}
    if presentation is None or not audio:
        return
    number = lambda *names: mi.leading_number(mi.value(presentation, *names))
    frames, per_frame, rate, original, delay = number("Frames"), number("Samples per Frame"), number("Maximum Sampling Frequency"), number("Original Samples"), number("Codec Delay")
    if probe.get("streams"):
        # dtshddec.c: st->duration = Num_Frames_Total * Samples_Per_Frame_At_Max_Fs, in the time base 1/sample rate.
        if rate and int(probe["streams"][0].get("sample_rate", 0)) == rate:
            seconds = float(probe["format"].get("duration", 0))
            if abs(seconds - frames * per_frame / rate) > 0.0005:
                problems.append(f"ffprobe's duration {seconds} s, AUPR-HDR's frames and samples per frame make {frames * per_frame / rate}")
    # File_Dts.cpp: FrameCount is Num_Frames_Total, SamplingCount the original samples (plus the codec delay where it is a frame or more).
    tally.compare("container frame count", audio.get("FrameCount"), str(frames))
    tally.compare("container sampling count", audio.get("SamplingCount"), str(original + (delay if delay >= per_frame else 0)))
    core, extension = chunk(report, "CORESSMD"), chunk(report, "EXTSS_MD")
    header = chunk(report, "DTSHDHDR")
    nominal = (mi.leading_number(mi.value(core, "Bit Rate")) if core else 0) + (mi.leading_number(mi.value(extension, "Average Bit Rate")) if extension else 0)
    tally.compare("container nominal bitrate", audio.get("BitRate_Nominal"), str(nominal * 1000) if nominal else None)
    peak = mi.leading_number(mi.value(extension, "Peak Bit Rate")) if extension else None
    tally.compare("container maximum bitrate", audio.get("BitRate_Maximum"), str(((mi.leading_number(mi.value(core, "Bit Rate")) if core else 0) + peak) * 1000) if peak else None)
    stream_chunk = chunk(report, "STRMDATA")
    if stream_chunk is not None:
        tally.compare("container stream size", audio.get("StreamSize"), str(mi.leading_number(mi.value(stream_chunk, "Chunk Size"))))
    code = mi.value(header, "Time Code") if header else None
    if code and audio.get("extra", {}).get("TimeCode"):
        hours, minutes, seconds, samples = code.replace(" and ", ":").replace(" samples", "").split(":")
        tally.compare("container time code", audio["extra"]["TimeCode"], f"{hours}:{minutes}:{seconds}:00+{samples}samples")
    else:
        tally.skip("container time code", "MediaInfo reports no time code where DTSHDHDR's frame rate is 0 or reserved, or the header is missing")


def main():
    failures = checked = 0
    mi.require()
    tool_versions.require("ffmpeg", "ffprobe", "dca")
    tally = mi.Tally(MEDIAINFO_READS_OTHERWISE)
    with tempfile.TemporaryDirectory() as directory:
        work = pathlib.Path(directory)
        tool = build_libdca(work)
        for manifest_path in sorted(FIXTURES.glob("*.expected.json")):
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            stem = manifest_path.name[:-len(".expected.json")]
            path = next(p for p in FIXTURES.iterdir() if p.name.startswith(stem + ".") and not p.name.endswith(".json"))
            report = mi.golden(path)
            check = manifest["crosscheck"]
            problems = []
            for name in CHECKS:
                if check.get(name) is False and name not in check.get("skip", {}):
                    problems.append(f"the {name} check is off without a reason")
            for name, reason in check.get("skip", {}).items():
                print(f"{path.name}: skips {name}: {reason}")
            if check.get("ffprobe"):
                check_ffprobe(path, manifest, report, problems)
            if check.get("decode"):
                check_decode(path, manifest, problems, work)
            if "decode_error" in check or "decode_errors" in check:
                check_decode_errors(path, manifest, problems, work)
            if check.get("libdca"):
                check_libdca(tool, path, manifest, report, problems)
            problems += check_mediainfo(path, tally)
            if path.suffix == ".dtshd":
                before = len(tally.problems)
                check_dtshd(path, manifest, report, problems, tally)
                problems += tally.problems[before:]
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
