#!/usr/bin/env python3
"""Checks the WavPack fixtures' manifests against independent tools.

For every manifest in tests/fixtures/wavpack (make_external_wavpack_fixtures.py,
make_wavpack_fixtures.py):

- wavpack_blocks (wavpack_tool.py: libwavpack 5.6.0 built from the pinned source): every block
  read_next_header finds, with its offset, size, version, first sample, samples, flags and CRC and
  whether WavpackVerifySingleBlock accepts it, against the manifest, for every fixture, damaged ones
  too; only the five that READER_DEPARTURES lists differ on purpose, each with its evidence, and the
  run fails if one of them agrees after all. The figures the library derives from a file that
  opens, against the analyzer's golden report: the version, channels, channel mask,
  sample rate, bits, samples, mode flags (hybrid, lossless, float, fast, high, very high, extra level),
  the MD5 digest, the source format and extension, and the wrapper's size. Then the decoder's whole pass,
  for every fixture and against the manifest's explicit "decode" (crosscheck.decode): the file opened as
  WavpackOpenFileInput opens it for wvunpack (block checksums verified), every sample unpacked with
  WavpackUnpackSamples, and the errors it counts (a block it skips, a checksum or CRC that fails). A file the
  library refuses to open says so, which covers what its init and metadata checks refuse (open_utils.c
  process_metadata, decorr_utils.c, unpack_dsd.c) and the block reader does not.
- wvunpack -ss (wavpack 5.9.0 of Ubuntu 26.04), on the main file alone: channels, source, duration,
  modalities, encoder version, digest, source format and the wrapper.
- wvunpack -v: the file decodes and its block CRCs hold, with its correction file where it has one
  (lossless then), and with -m its digest equals the one stored. Damaged fixtures are decoded too, and
  those listed in REJECTED must fail.
- ffprobe (FFmpeg 8.0, libavformat's wv demuxer): the codec, rate, channels and layout, the bit depth,
  the length from the first block, and the packets, one for each set of blocks at the first block's
  offset and the sets' size. What it refuses is listed in FFPROBE_REFUSES.
- MediaInfo 26.01 (mediainfo_tool.py), against the golden report: the format, the profile (the stream
  version), the compression mode (lossy only for a hybrid file without the sub-block that a correction file needs),
  the DSD setting, the channels and layout, the sample rate, the bit depth, the duration from the first to the
  last block with samples, the stream size and the settings it spells from the config block. MediaInfo accepts
  a stream after two blocks with audio and reads a file under 1 MiB to its end; the fixtures on which it reads
  otherwise name their evidence.

Fixtures that are damaged say why the others do not apply; every skipped check prints its reason.
Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_wavpack_fixtures.py [name ...]
"""

import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

import mediainfo_tool as mi
import tool_versions
import wavpack_model
import wavpack_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "wavpack"
SKIPS = []
INITIAL, FINAL, MONO = 0x800, 0x1000, 4

# Fixtures whose blocks the analyzer takes differently from libwavpack's reader on purpose, with the evidence
# (Reader.Extent, BlockHeader.IsPlausible): libwavpack takes the size a header states, and a header of a
# version it does not decode is no header to it. The run fails if libwavpack lists the same blocks as the manifest.
READER_DEPARTURES = {
    "built-block-size-too-large": "the first block states 100 bytes more than it has; libwavpack's reader takes the stated size (13598 bytes), the analyzer ends the "
                                  "block where the next block's header begins (13498, wavpack.block-size-mismatch)",
    "built-block-trailing-byte": "the first block has a byte after its last sub-block and states one byte more than the 13499 it has; libwavpack takes the stated size "
                                 "(13500 bytes), the analyzer ends the block where the next header begins (13499, wavpack.block-size-mismatch)",
    "built-truncated-last-block": "the last block is cut 1000 bytes before its end; libwavpack lists it with the size it states (23058 bytes) though it cannot read them, "
                                  "the analyzer with the bytes that are there (22058, wavpack.block-truncated)",
    "built-seek-tag-between-blocks": "an ID3v2.4 tag sits between the first block and the others; libwavpack's reader skips the tag's bytes and lists all 3 blocks, the analyzer "
                                     "ends the stream at the tag, which an ID3v2.4 SEEK frame found, and lists the block before it (wavpack.data-after-tag)",
    "built-version-unsupported": "the blocks have stream version 0x411; read_next_header refuses a header of a version above 0x410 (MAX_STREAM_VERS), so libwavpack finds "
                                 "no block, while the analyzer relaxes the minor version and reports it (wavpack.version-unsupported)",
}
# Fixtures libwavpack's decoder must refuse, with the evidence.
REJECTED = {
    "built-bad-checksum": "a block whose checksum fails is counted as a CRC error",
    "built-checksum-missing": "a block that says it has a checksum and has none fails WavpackVerifySingleBlock",
    "built-truncated-last-block": "the last block is cut",
    "built-subblock-overrun": "a sub-block runs past its block",
    "built-block-size-too-small": "the block's sub-blocks do not end where the block does",
    "built-decorr-terms-invalid": "libwavpack refuses a decorrelation term of 0",
    "built-audio-without-bitstream": "a block without a bitstream cannot be unpacked",
    "built-unknown-subblocks": "a sub-block that is not optional and not known is refused",
    "built-dsd-high-short": "init_dsd_block_high refuses a high mode block of fewer than 20 bytes after the mode in a stereo block",
    "built-dsd-high-rate-step": "init_dsd_block_high refuses a rate step other than RATE_S (20)",
    "built-weights-without-terms": "read_decorr_weights refuses more weights than there are passes (none, without terms)",
    "built-reorder-too-long": "read_new_config_info refuses more reordering bytes than the layout has channels",
    "built-subblock-sizes-mixed": "read_entropy_vars refuses an entropy variables sub-block of 8 bytes in a stereo block",
}
# Fixtures libwavpack opens but whose figures it gives differently from the analyzer's on purpose, with the evidence.
FIGURES_DIFFER = {
    "built-dsd-rate-overflow": "WavpackGetNativeSampleRate multiplies the sample rate and the DSD multiplier in 32 bits (common_utils.c), so 2,147,483,647 Hz times 2^31 wraps; "
                               "the analyzer holds the bit rate to 2,147,483,647 Hz and reports wavpack.sample-rate-implausible",
}
# What ffprobe cannot read, and why (FFmpeg 8.0.1, libavformat/wvdec.c, which differs from the 6.1.1 that ffmpeg_sources.py pins by API renames only).
FFPROBE_REFUSES = {
    "external-lossless-3ch-unassigned": "wv_read_block_header refuses a channel info of one byte, a count with no mask: \"Insufficient channel information\"",
    "external-from-caf": "the same: the CAF's stereo layout tag leaves a channel info of the count alone",
    "built-garbage-prefix": "wv_probe looks for the header at the first byte of the file",
    "built-sample-rate-missing": "wv_read_block_header cannot take a rate from the header's index 15 without a sample rate sub-block: \"Cannot determine custom sampling rate\"",
}
# What ffprobe reads with a sample rate of 0: wvdec.c leaves the custom rate unset when its sub-block gives 0 ("Custom sample rate missing"),
# where libwavpack falls back to 44100 (open_utils.c 229-234).
FFPROBE_NO_RATE = {
    "built-sample-rate-zero-custom": "the sample rate sub-block of 0 Hz leaves the index 15 with no rate, so ffprobe's stream has a rate of 0",
}
# What ffprobe reads differently: wvdec.c builds the channel layout from the channel mask (av_channel_layout_from_mask),
# so channels without a speaker in it are not counted, and libavcodec/wavpack.c refuses to decode a mask that names fewer
# speakers than the channel count ("Channel mask does not match the channel count"), which leaves no bits per raw sample.
FFPROBE_COUNTS_THE_MASK = {"external-lossless-4ch-two-unassigned", "built-channel-identities"}
# ffprobe's packets end at the first bytes that are no block header (wv_read_block_header: ff_wv_parse_header fails).
MEDIAINFO_DSD = ("MediaInfo 26.01 reads DSD: the sample rate is the header's index rate shifted by the frame rate multiplier of sub-block 0x0E and by 3 for the DSD flag, "
                 "and it fills no bit depth for a DSD file (File_Wvpk.cpp, Data_Parse_Fill, id_0E)")
MEDIAINFO_ONE_BLOCK = ("MediaInfo 26.01 accepts a stream after two blocks with audio (Frame_Count_Valid=2, File_Wvpk.cpp) and fills nothing from a file that has one (observed)")
MEDIAINFO_ID3V2 = ("observed: MediaInfo 26.01 counts the leading ID3v2 tag in the General stream size (26 bytes) and fills none of its fields for a WavPack file")
MEDIAINFO_READS_OTHERWISE = {
    "built-leading-id3v2": {"tag title": MEDIAINFO_ID3V2, "tag track": MEDIAINFO_ID3V2},
}


def skip(name, check, reason):
    SKIPS.append(f"{name}: {check} skipped: {reason}")


def run(arguments, **kw):
    return subprocess.run(arguments, capture_output=True, text=True, **kw)


def find(manifest_path):
    stem = manifest_path.name[:-len(".expected.json")]
    return next(p for p in DIRECTORY.glob(stem + ".*") if p.suffix != ".json")


def libwavpack(tool, path):
    """The figures and blocks wavpack_blocks prints."""
    lines = run([str(tool), str(path)]).stdout.splitlines()
    facts, blocks = {}, []
    for line in lines:
        parts = line.split(" ")
        if parts[0] == "block":
            blocks.append(parts[1:])
        elif parts[0] == "open":
            facts["open_error"] = line
        else:
            facts[parts[0]] = " ".join(parts[1:])
    return facts, blocks


def sets_of(blocks):
    """(offset, size) of each set of blocks: from an initial to a final block."""
    result, current = [], None
    for b in blocks:
        flags = int(b["flags"], 16)
        if not b["samples"]:
            continue
        if flags & INITIAL or current is None:
            current = [b["offset"], 0]
            result.append(current)
        current[1] += b["length"]
        if flags & FINAL:
            current = None
    return result


def statistic(report, name):
    stream = next(c for c in report["root"]["children"] if c.get("kind") == "Stream")
    node = next((c for c in stream["children"] if c.get("kind") == "Statistics"), None)
    return None if node is None else next((c.get("value") for c in node["children"] if c["name"] == name), None)


def check_decode(name, manifest, facts, problems):
    """The decoder's pass against the manifest's explicit expectation: refused, or this many samples with this many errors."""
    expected = manifest["crosscheck"].get("decode")
    if expected is None:
        problems.append("the manifest has no crosscheck.decode")
        return
    if not expected["opens"]:
        if "decode_open_error" not in facts:
            problems.append(f"libwavpack opens the file and unpacks {facts.get('decode_samples')} samples, which the manifest says it refuses")
        return
    if "decode_open_error" in facts:
        problems.append(f"libwavpack refuses to open the file ({facts['decode_open_error']}), which the manifest says it unpacks")
        return
    got = {"samples": int(facts["decode_samples"]), "errors": int(facts["decode_errors"])}
    if got != {"samples": expected["samples"], "errors": expected["errors"]}:
        problems.append(f"libwavpack unpacks {got['samples']} samples with {got['errors']} errors, the manifest {expected['samples']} with {expected['errors']}")


def check_libwavpack(name, manifest, path, report, tool, problems):
    """The blocks libwavpack's reader finds against the manifest, for every fixture, and the figures of those it opens."""
    facts, blocks = libwavpack(tool, path)
    expected = [[str(b["offset"]), str(b["length"]), b["version"], str(b["index"]), str(b["samples"]), b["flags"], b["crc"], "1" if b["verifies"] else "0"]
                for b in manifest["blocks"]]
    found = [[b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7]] for b in blocks]
    first = next((i for i, (a, b) in enumerate(zip(found, expected)) if a != b), min(len(found), len(expected)))
    if name in READER_DEPARTURES:
        if found == expected:
            problems.append("libwavpack lists the same blocks as the manifest, which READER_DEPARTURES says it does not; remove the entry")
        else:
            skip(name, "libwavpack's block list", READER_DEPARTURES[name])
    elif found != expected:
        problems.append(f"libwavpack reads {len(found)} blocks and the manifest has {len(expected)}; they differ at {first}: {found[first:first + 1]} and {expected[first:first + 1]}")
    if not manifest["stream_infos"] or not report["streams"]:
        skip(name, "libwavpack's figures", "the analyzer finds no audio stream to compare them with")
        return facts
    info = manifest["stream_infos"][0]
    stream = report["streams"][0]
    is_dsd = statistic(report, "IsDsd")
    if "open_error" in facts:
        if name in REJECTED or not manifest["crosscheck"]["valid"] or info["correction"]:
            skip(name, "libwavpack's figures", f"libwavpack refuses to open the file ({facts['open_error'].removeprefix('open error: ')}), so it gives none")
            return facts
        problems.append(facts["open_error"])
        return facts
    if not manifest["crosscheck"]["valid"]:
        skip(name, "libwavpack's figures", FIGURES_DIFFER.get(name, manifest.get("description", "the fixture is damaged on purpose") + ": the figures libwavpack derives from it are its own"))
        return facts
    mask = int(facts["channel_mask"], 16)
    layout = [n for bit, n in enumerate(SPEAKER_NAMES) if mask >> bit & 1]
    layout += ["discrete"] * (int(facts["channels"]) - len(layout))
    pairs = [("channels", int(facts["channels"]), stream["channels"]), ("sample rate", int(facts["sample_rate"]), stream["sampleRate"]),
             ("bits per sample", int(facts["bits_per_sample"]) // (8 if is_dsd else 1), stream["bitsPerSample"]),
             ("layout", layout, stream["channelLayout"]),
             ("hybrid", facts["hybrid"] == "1", statistic(report, "IsHybrid")), ("lossless", facts["lossless"] == "1", statistic(report, "IsLossless")),
             ("float", facts["float"] == "1", statistic(report, "IsFloatingPoint")),
             ("encoder version", "WavPack 5.0 or later" if facts["version"] == "5" else "WavPack 4 stream, no WavPack 5 features", stream["encoder"])]
    if manifest["crosscheck"]["valid"]:
        pairs.append(("samples", int(facts["samples"]) * (8 if is_dsd else 1), stream["samples"]))
    if statistic(report, "Compression Mode") is not None:
        mode = "very high" if facts["very_high"] == "1" else "high" if facts["high"] == "1" else "fast" if facts["fast"] == "1" else "normal"
        pairs.append(("compression mode", mode, statistic(report, "Compression Mode")))
        pairs.append(("extra level", facts["extra"] if facts["extra"] != "-1" else "none", str(statistic(report, "Extra Processing Level"))))
    md5 = statistic(report, "MD5 Checksum")
    pairs.append(("MD5 digest", facts.get("md5"), md5))
    if statistic(report, "Source Format") is not None:
        pairs.append(("source format", FORMAT_NAMES[int(facts["file_format"])], statistic(report, "Source Format")))
    for label, got, want in pairs:
        if got != want:
            problems.append(f"libwavpack's {label} is {got!r}, the report's {want!r}")
    # libwavpack gathers the wrapper sub-blocks of the first block (OPEN_WRAPPER): header and trailer.
    wrapper = sum(c["value"] for c in walk_blocks(report) if c["name"] in ("RIFF Header", "RIFF Trailer", "Alternate Header", "Alternate Trailer"))
    if int(facts["wrapper_bytes"]) != wrapper:
        problems.append(f"libwavpack's wrapper is {facts['wrapper_bytes']} bytes, the report's first block's {wrapper}")
    return facts


SPEAKER_NAMES = ["front left", "front right", "front center", "LFE", "back left", "back right", "front left of center", "front right of center", "back center",
                 "side left", "side right", "top center", "top front left", "top front center", "top front right", "top back left", "top back center", "top back right"]
FORMAT_NAMES = ["WAV", "Wave64", "CAF", "DSDIFF", "DSF", "AIFF"]


def walk_blocks(report):
    stream = next(c for c in report["root"]["children"] if c.get("kind") == "Stream")
    for block in stream["children"]:
        if block["name"] in ("Block", "Metadata Block"):
            yield from block["children"]
            return


def wvunpack_summary(path, work):
    """wvunpack -ss on a copy of the main file alone."""
    copy = pathlib.Path(work) / "alone.wv"
    shutil.copy(path, copy)
    text = run(["wvunpack", "-ss", str(copy)]).stdout
    return dict(re.findall(r"^([a-z0-9 ]+?):\s+(.*)$", text, re.M))


def check_wvunpack_summary(name, manifest, path, report, work, problems):
    s = wvunpack_summary(path, work)
    info = manifest["stream_infos"][0]
    stream = report["streams"][0]
    is_dsd = statistic(report, "IsDsd")
    bits = stream["bitsPerSample"]
    rate = stream["sampleRate"]
    source = "1-bit DSD" if is_dsd else f"{bits}-bit floats" if statistic(report, "IsFloatingPoint") else f"{bits}-bit ints"
    expected = {"channels": f"{info['channels']} ", "source": f"{source} at {rate} Hz", "encoder version": "5" if stream["encoder"].startswith("WavPack 5") else "4"}
    for key, want in expected.items():
        if not s.get(key, "").startswith(want):
            problems.append(f"wvunpack's {key} is {s.get(key)!r}, expected {want!r}")
    duration = stream["duration"]
    shown = s.get("duration", "")
    match = re.fullmatch(r"(\d+):(\d\d):(\d\d)\.(\d\d)", shown)
    if match is None or abs(int(match[1]) * 3600 + int(match[2]) * 60 + int(match[3]) + int(match[4]) / 100 - duration) > 0.011:
        problems.append(f"wvunpack's duration is {shown!r}, expected {duration:.3f} s")
    modes = set(s.get("modalities", "").split(", "))
    want = set()
    want.add("hybrid lossy" if statistic(report, "IsHybrid") else "lossless")
    mode = statistic(report, "Compression Mode")
    if mode == "high":
        want.add("high")
    elif mode == "very high":
        want.add("very high")
    elif mode == "fast":
        want.add("fast")
    level = statistic(report, "Extra Processing Level")
    if isinstance(level, int):
        want.add(f"extra-{level}")
    if statistic(report, "IsHybrid") and config_flags(report).get("UsesDynamicNoiseShaping"):
        want.add("dns")
    if not want <= modes:
        problems.append(f"wvunpack's modalities are {sorted(modes)}, expected at least {sorted(want)}")
    if md5 := statistic(report, "MD5 Checksum"):
        if s.get("original md5") != md5:
            problems.append(f"wvunpack's digest is {s.get('original md5')!r}, the report's {md5!r}")
    elif "original md5" in s:
        problems.append("wvunpack shows a digest the report does not")
    ext = statistic(report, "Source Extension")
    if ext is not None and f"with '{ext}' extension" not in s.get("source format", ""):
        problems.append(f"wvunpack's source format is {s.get('source format')!r}, the report's extension {ext!r}")


def check_wvunpack_verify(name, manifest, path, report, work, problems):
    """wvunpack -v: the file decodes. A hybrid file with a correction file next to it is lossless."""
    valid = manifest["crosscheck"]["valid"]
    copy = pathlib.Path(work) / "x.wv"
    shutil.copy(path, copy)
    digest = report and statistic(report, "MD5 Checksum")
    result = run(["wvunpack", "-v", "-m", str(copy)], timeout=60)
    result.stdout += result.stderr
    ok = result.returncode == 0
    if name in REJECTED:
        if ok:
            problems.append(f"wvunpack -v verifies the file, which it must refuse: {REJECTED[name]}")
        return
    if valid and not ok:
        problems.append(f"wvunpack -v fails: {(result.stdout + result.stderr).strip().splitlines()[-1]}")
    if valid and digest:
        mine = re.findall(r"unpacked md5:\s+([0-9a-f]{32})", result.stdout)
        if mine != [digest]:
            problems.append(f"wvunpack's unpacked digest {mine} differs from the stored {digest}")


def check_pair(name, path, work, problems):
    """A main file with its correction file: lossless, with the digest where there is one."""
    wvc = path.with_name(path.name[:-len(".wv")] + "-wvc.wvc")
    shutil.copy(path, pathlib.Path(work) / "x.wv")
    shutil.copy(wvc, pathlib.Path(work) / "x.wvc")
    result = run(["wvunpack", "-v", str(pathlib.Path(work) / "x.wv")], timeout=60)
    text = result.stdout + result.stderr
    if result.returncode != 0 or "(+.wvc)" not in text or "(lossless" not in text:
        problems.append(f"wvunpack -v does not verify the pair as lossless: {text.strip().splitlines()[-1] if text.strip() else result.stderr}")


def ffprobe(path):
    result = run(["ffprobe", "-v", "error", "-show_streams", "-show_packets", "-of", "json", str(path)])
    return result.returncode, json.loads(result.stdout or "{}")


def check_ffprobe(name, manifest, path, report, problems):
    code, data = ffprobe(path)
    if name in FFPROBE_NO_RATE:
        if code != 0 or not data.get("streams") or data["streams"][0].get("sample_rate") != "0":
            problems.append(f"ffprobe is expected to read a sample rate of 0 ({FFPROBE_NO_RATE[name]})")
        skip(name, "ffprobe", FFPROBE_NO_RATE[name])
        return
    if name in FFPROBE_REFUSES:
        if code == 0 and data.get("streams"):
            problems.append(f"ffprobe reads the file, which the list says it refuses ({FFPROBE_REFUSES[name]})")
        skip(name, "ffprobe", FFPROBE_REFUSES[name])
        return
    if code != 0 or not data.get("streams"):
        problems.append(f"ffprobe fails on the file: {code}")
        return
    stream = data["streams"][0]
    info = manifest["stream_infos"][0]
    is_dsd = statistic(report, "IsDsd")
    rate = report["streams"][0]["sampleRate"] // (8 if is_dsd else 1)
    bits = 8 if is_dsd else report["streams"][0]["bitsPerSample"]
    layout = report["streams"][0].get("channelLayoutName")
    channels = info["channels"]
    if name in FFPROBE_COUNTS_THE_MASK:
        channels = sum(1 for n in info["layout"] if n != "discrete")
        layout = None
        bits = int(stream.get("bits_per_raw_sample", 0))
    checks = [("codec", stream["codec_name"], "wavpack"), ("sample rate", int(stream["sample_rate"]), rate), ("channels", stream["channels"], channels),
              ("bits per raw sample", int(stream.get("bits_per_raw_sample", 0)), bits)]
    if layout and stream.get("channel_layout"):
        checks.append(("channel layout", stream["channel_layout"].split("(")[0], layout))
    declared = info["declared"]
    if declared is not None:
        checks.append(("length", stream["duration_ts"], declared))
    for label, got, want in checks:
        if got != want:
            problems.append(f"ffprobe's {label} is {got!r}, expected {want!r}")
    # The packets end at the first bytes that are no block header.
    chain = []
    for b in manifest["blocks"]:
        if chain and chain[-1]["offset"] + chain[-1]["length"] != b["offset"]:
            break
        chain.append(b)
    sets = sets_of(chain)
    packets = [(int(p["pos"]), int(p["size"])) for p in data.get("packets", [])]
    if packets != [(o, s) for o, s in sets]:
        problems.append(f"ffprobe's packets are {packets[:4]}..., the sets of blocks {sets[:4]}...")


MEDIAINFO_SETTINGS = [("IsFastMode", "-f"), ("IsHighMode", "-h"), ("IsVeryHighMode", "-hh"), ("CreatesCorrectionFile", "-c"), ("OptimizesCorrectionFile", "-cc"),
                      ("StoresMd5", "-m"), ("UsesDynamicNoiseShaping", "-use-dns")]


def config_flags(report):
    for node in walk_blocks(report):
        if node["name"] == "Config Block":
            for data in node["children"]:
                if data["name"] == "Data":
                    return {f["name"]: f["value"] for flags in data["children"] if flags["name"] == "Flags" for f in flags["children"]}
    return {}


def fill_block(chained, size):
    """The block in whose Data_Parse MediaInfo runs Data_Parse_Fill (File_Wvpk.cpp): the first where Frame_Count (counted for every block, then taken back
    for a block of stream version 4 without samples) reaches Frame_Count_Valid (2), or where the file ends; None where neither happens."""
    count = 0
    for b in chained:
        count += 1
        if int(b["version"], 16) >> 8 == 0x4 and not b["samples"]:
            count -= 1
        if count >= 2 or b["offset"] + b["length"] == size:
            return b
    return None


def jumps_to_end(chained, size):
    """Whether Data_Parse_Fill jumps to the last 512 KiB (File_Wvpk.cpp, GoToFromEnd): at least 1 MiB must be left from File_Offset+Buffer_Offset, which in
    Data_Parse is after the block's 8-byte header ("wvpk" and its size)."""
    filled = fill_block(chained, size)
    return filled is not None and size - (filled["offset"] + 8) >= 2 * 512 * 1024


def check_mediainfo(name, manifest, path, report, tally):
    problems = tally.start(name)
    info = tally.read(path, full=True)
    audio = info["Audio"][0] if info and info["Audio"] else {}
    stream = report["streams"][0] if report["streams"] else None
    if stream is None:
        tally.compare("format where the analyzer finds no stream", audio.get("Format"), None)
        return problems
    # MediaInfo takes a block as one only where another header follows it at once (File_Wvpk.cpp, Synchronize).
    every = manifest["blocks"]
    chained = [b for i, b in enumerate(every) if i + 1 == len(every) or every[i + 1]["offset"] == b["offset"] + b["length"]]
    blocks = [b for b in chained if b["samples"]]
    if len(blocks) < 2:
        tally.compare("format of a file with one block of audio (" + MEDIAINFO_ONE_BLOCK + ")", audio.get("Format"), None)
        return problems
    tally.compare("format", audio.get("Format"), "WavPack")
    first, second = blocks[0], blocks[1]
    version = int(first["version"], 16)
    tally.compare("profile", audio.get("Format_Profile"), f"{version >> 8}.{version & 0xFF:02d}")
    hybrid = statistic(report, "IsHybrid")
    # File_Wvpk.cpp, Data_Parse_Fill: "Lossy" for a hybrid file unless sub-block 0x07 ("info needed for hybrid lossless (wvc) mode", id_07) was met in the
    # blocks parsed so far, which are the first two; Format_Settings holds "DSD" for a DSD file and nothing else.
    data = path.read_bytes()
    correction = any(i & 0x3F == 0x07 for b in chained[:2] for i, *_ in wavpack_model.sub_blocks(data[b["offset"]:b["offset"] + b["length"]])[0])
    tally.compare("compression mode", audio.get("Compression_Mode"), "Lossy" if hybrid and not correction else "Lossless")
    tally.compare("settings", audio.get("Format_Settings"), "DSD" if statistic(report, "IsDsd") else None)
    channels = stream["channels"]
    if path.suffix == ".wvc":
        # A correction file has no channel info: MediaInfo takes the mono flag of the second block.
        channels = 1 if int(second["flags"], 16) & MONO else 2
    tally.compare("channels", audio.get("Channels"), str(channels))
    if channels > 2 and stream["channelLayout"] and all(n in mi.SPEAKERS.values() for n in stream["channelLayout"]):
        tally.compare("channel layout", mi.speakers(audio.get("ChannelLayout")), mi.normalized(stream["channelLayout"]))
    is_dsd = statistic(report, "IsDsd")
    if is_dsd:
        tally.compare("sample rate of DSD (" + MEDIAINFO_DSD + ")", audio.get("SamplingRate"), str(stream["sampleRate"]))
        tally.compare("bit depth of DSD (" + MEDIAINFO_DSD + ")", audio.get("BitDepth"), None)
    else:
        tally.compare("sample rate", audio.get("SamplingRate"), str(stream["sampleRate"]))
        tally.compare("bit depth", audio.get("BitDepth"), str(8 * (1 + (int(first["flags"], 16) & 3))))
    # File_Wvpk.cpp, Streams_Finish: the first block's index to the end of the last block with samples. Data_Parse_Fill runs in the block where the count of
    # blocks reaches 2 (fill_block) and jumps to the last 512 KiB only where at least 1 MiB is left from the end of that block's header (jumps_to_end),
    # so a smaller file is read to its end.
    if jumps_to_end(chained, path.stat().st_size):
        tally.skip("duration", "MediaInfo jumps to the last 512 KiB of a file over 1 MiB (File_Wvpk.cpp, Data_Parse_Fill), where it finds the last block by its synchronization")
    else:
        rate = stream["sampleRate"] // 8 if is_dsd else stream["sampleRate"]
        last = blocks[-1]
        duration = (last["index"] + last["samples"] - first["index"]) * 1000 // rate
        tally.compare("duration from the first to the last block with samples", audio.get("Duration"), f"{duration / 1000:.3f}")
    size = path.stat().st_size
    tally.compare("stream size", audio.get("StreamSize"), str(size - sum(c["length"] for c in report["root"]["children"] if c.get("kind") == "Tag")))
    flags = config_flags(report)
    if flags:
        text = audio.get("Encoded_Library_Settings") or ""
        want = [token for key, token in MEDIAINFO_SETTINGS if flags.get(key)]
        tally.compare("settings it spells from the config block", [t for t in want if t in text.split(" ")], want)
    mi.compare_tags(tally, info["General"], report)
    return problems


def main():
    mi.require()
    tool_versions.require("ffprobe", "wvunpack")
    wavpack_tool_dir = os.environ.get("WAVPACK_TOOLS_DIR")
    tool = wavpack_tool.build_blocks(pathlib.Path(wavpack_tool_dir) if wavpack_tool_dir else None)
    names = sys.argv[1:]
    tally = mi.Tally(MEDIAINFO_READS_OTHERWISE)
    failures = 0
    for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
        name = manifest_path.name[:-len(".expected.json")]
        if names and name not in names:
            continue
        path = find(manifest_path)
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        report = mi.golden(path)
        valid = manifest["crosscheck"]["valid"]
        problems = []
        is_wvc = path.suffix == ".wvc"
        with tempfile.TemporaryDirectory() as work:
            facts = check_libwavpack(name, manifest, path, report, tool, problems)
            check_decode(name, manifest, facts, problems)
            if not valid:
                skip(name, "ffprobe and MediaInfo", manifest.get("description", "the fixture is damaged on purpose") + ": the tools read it in ways of their own")
            if is_wvc:
                skip(name, "wvunpack and ffprobe", "a correction file cannot be unpacked or demuxed alone")
            else:
                if valid and manifest["stream_infos"]:
                    check_wvunpack_summary(name, manifest, path, report, work, problems)
                check_wvunpack_verify(name, manifest, path, report, work, problems)
                pair = path.with_name(path.name[:-len(".wv")] + "-wvc.wvc")
                if valid and pair.exists():
                    check_pair(name, path, work, problems)
                if valid and manifest["stream_infos"]:
                    check_ffprobe(name, manifest, path, report, problems)
        if valid:
            problems += check_mediainfo(name, manifest, path, report, tally)
        if problems:
            failures += 1
            print(f"FAIL {name}: " + "; ".join(problems))
        else:
            print(f"ok   {name}")
    for line in SKIPS:
        print(line)
    for problem in tally.unused():
        print(f"FAIL {problem}")
        failures += 1
    tally.print(sys.stdout)
    if failures:
        raise SystemExit(f"{failures} fixtures disagree with the tools")


if __name__ == "__main__":
    main()
