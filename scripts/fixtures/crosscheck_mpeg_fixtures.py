#!/usr/bin/env python3
"""Checks the MPEG fixtures' expected results against independent decoders.

For every fixture in tests/fixtures/mpeg, layer3, layer2 and vbr whose manifest has
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
  multichannel CRC errors, into one channel file for each of the stream's "channel_layout"
  speakers, or as many as its "channels" where the stream reports only its stereo base.
- "channel_layout" per stream: where ffprobe reads the frames ("crosscheck": true), it finds the
  channels of the first frame: the layout's, or two for the stereo base of a multichannel stream.
- "mp3guessenc_multichannel": the multichannel parameters mp3guessenc prints.
- "side_info" per frame: minimp3 reads exactly these values (minimp3_side_info.c), or rejects the
  frame where the manifest says it does. Its reservoir before each frame must match, except where
  minimp3 resets or discards it: after a gap, after a frame it rejects, and after a frame whose
  main data it could not find. Frames minimp3 skips before it locks on are only allowed before a
  gap or a stream change.
- "mp3guessenc": the statistics mp3guessenc prints about the stream.
- The coding statistics of each Layer III region ("statistics", stream_stats.py): mp3guessenc must
  print the same stereo modes, block usage (its "switch" granules are start, stop and reserved
  ones), global gain, ancillary data, largest main_data_begin ("Max reservoir"), use of scalefactor
  scaling, scfsi and padding, and frame sizes per bitrate, wherever it takes the frames the
  statistics count; GUESSENC_READS_OTHERWISE and guessenc_frames_differ say where and why it does
  not. EncSpot (encspot_tool.py) must print the same Av. Reservoir (the mean main_data_begin,
  which it truncates), Max. Reservoir, Scalefac (the share of granules with scalefactor scaling),
  bitrate histogram, frame count and mode where it reads those frames; it skips the first frame
  it finds and, without a VBR header, starts 2000 bytes on, so elsewhere its figures are compared
  with an emulation of how it reads the file (encspot_frames). Its encoder for encoder-written
  fixtures must be the analyzer's or one ENCSPOT_ENCODER_DIFFERENCES lists with the reason. Its
  quality rating is not read: the analyzer gives no verdicts on quality.

- "vbr_header": the raw values of the first frame's Xing, Info, LAME or VBRI header (vbr_tags.py).
  mp3guessenc must report the same header fields, LAME tag fields, tag CRC verdict and music CRC
  verdict; mpg123 the same frame and byte counts, quality, encoder string, VBR method, ReplayGain,
  delay and padding; ffprobe the duration the frame count gives, the start time the delay gives,
  and the encoder string and ReplayGain only when the tag CRC holds. Where these tools read a
  header differently from LAME (mp3guessenc looks for Xing in every layer and checks the music CRC
  over every later frame, ffmpeg drops the frame count of a much longer file and reads a delay
  after any "LAME", mpg123 prints the lowpass byte as the revision), the check follows the tool.
  The peak is the one field no tool reports.
- "mp3guessenc_encoder_string": the encoder string mp3guessenc finds in the tag or ancillary data.
- "mp3guessenc_guess": the encoder mp3guessenc guesses, which for mp3PRO and MP3 Surround follows
  the signature in the ancillary data (ancillary_scan.py). Every fixture must have the OFL block
  mp3guessenc reads ("mp3guessenc_ofl"), or none; where the Fraunhofer encoder wrote one,
  ffmpeg's output must start within 16 samples of its delay ("decoded_onset").
- "statistics" per region (stream_stats.py): mp3guessenc must count the same frames, length,
  average bitrate and bitrate histogram where it takes the same frames as audio (no gaps, one
  region and stream, constant header flags, no finding about frames it skips), and ffmpeg and
  mpg123 must decode the gapless sample count where the VBR header's counts hold and the padding
  covers their 529-sample decoder delay. At an exact tie, mp3guessenc's floating-point rounding
  may differ by one in the last digit. External fixtures record mp3guessenc's figures
  ("mp3guessenc_stream") and the decoders' sample counts ("decoded_samples") instead.

External fixtures list their encoder settings and ffprobe's packets instead of every frame; for
them, ffprobe's sample rate, channel count and bitrate must match those settings, and minimp3 and
mp3guessenc must still report what the manifest recorded.

- getID3: for LAME 3.90 to 3.92, whose tag has no preset field, getID3 must read the same version
  and name the same preset where it names one (getid3_tool.py).

- MediaInfo 26.01 (mediainfo_tool.py), compared with the golden report, on these fixtures and on
  the tag fixtures (tests/fixtures/tags): the version, layer, sample rate, channels, mode, mode
  extension and emphasis of the last frame it reads, and the stream's mode; the frame and sample
  count, bitrate, bitrate mode and stream size that the first frame's Xing, Info or VBRI header
  gives, or the complete frames without one; the LAME tag's encoder string; and the title, artist
  and album of the ID3v2, ID3v1, APE and Lyrics3 tags. Where MediaInfo reads otherwise by its
  source (MEDIAINFO_READS_OTHERWISE, and the rules in check_mediainfo), the difference is checked
  to be the one named: its own readings are asserted (the mode extension's names for Layer I and II,
  the frames it parses of a variable bitrate without a VBR header, no encoder without a LAME tag,
  encoder string or TSSE, no tag fields where no tag holds them), and the summary prints each skip
  with its reason.

Needs ffprobe, madplay, mpg123, mediainfo, php, curl, make and a C and C++ compiler on the PATH; minimp3,
mp3guessenc and EncSpot are built from third_party. Exits
non-zero when anything disagrees.
"""

import collections
import json
import math
import pathlib
import re
import struct
import subprocess
import sys

import encspot_tool
import getid3_tool
import guessenc_tool
import mctoolamed_tool
import mediainfo_tool as mi
import minimp3_tool
import stream_stats
import tool_versions
import vbr_tags
from conformance_mpeg_audio import guessenc as guessenc_multichannel

ROOT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures"
DIRECTORIES = [ROOT / "mpeg", ROOT / "layer3", ROOT / "layer2", ROOT / "vbr", ROOT / "stats"]
TAGS = ROOT / "tags"
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


def ffprobe_packets(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "packet=pos,size", "-of", "json", str(path)],
        capture_output=True,
        text=True,
        check=True,
    )
    return [(int(p["pos"]), int(p["size"])) for p in json.loads(result.stdout).get("packets", [])]


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
    pcm = subprocess.run(["mpg123", "-q", "--no-gapless", "-s", str(path)], capture_output=True, check=True).stdout
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
    output = subprocess.run([guessenc_tool.binary(), "-e", str(path)], capture_output=True, text=True).stdout
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


# Ubuntu 26.04's madplay was linked against a libmad whose mad_build, the string of its build options, has another size than the libmad0 it
# loads, and glibc's loader warns about that copy relocation on stderr (sysdeps/x86_64/dl-machine.h, R_X86_64_COPY). madplay only
# prints mad_build for -V, so decoding does not depend on it; this one line, printed once for the process, is not an error of the stream (count=1: a second
# would be reported).
MADPLAY_LOADER_WARNING = re.compile(rb"^madplay: Symbol `mad_build' has different size in shared object, consider re-linking\r?\n?", re.MULTILINE)


def decodes_to_silence(path, manifest):
    problems = []
    commands = {
        "ffmpeg": ["ffmpeg", "-v", "error", "-i", str(path), "-f", "s16le", "-"],
        "mpg123": ["mpg123", "-q", "-s", str(path)],
        "madplay": ["madplay", "-q", "--no-dither", "--stereo", "-o", "raw:-", str(path)],
    }
    first = manifest["frames"][0]["fields"]
    if first["Header/Bitrate"] == "free":
        # ffmpeg does not read free format, and mpg123 reads free-format Layer II with table B.2a
        # at every rate (layer2.c, II_select_table), where the captions of Tables 3-B.2a and
        # 3-B.2b give B.2b below 48000 Hz, as libmad reads it.
        del commands["ffmpeg"]
        if first["Header/Layer"] == "II" and first["Header/Sample Rate"] != "48000 Hz":
            del commands["mpg123"]
    for name, command in commands.items():
        result = subprocess.run(command, capture_output=True)
        errors = MADPLAY_LOADER_WARNING.sub(b"", result.stderr, count=1) if name == "madplay" else result.stderr
        if not result.stdout or any(result.stdout) or errors.strip():
            problems.append(f"{name} does not decode to silence without errors: {errors.decode()[:200].strip()}")
    return problems


def stream(frame):
    """The analyzer's stream identity: version, layer, sample rate and whether it is free format."""
    fields = frame["fields"]
    return (fields["Header/Version"], fields["Header/Layer"], fields["Header/Sample Rate"], fields["Header/Bitrate"] == "free")


def check_side_info(path, manifest, dumper):
    problems = []
    rows = {row["offset"]: row for row in minimp3_tool.side_info(dumper, path)}
    frames = manifest["frames"]
    gap_ends = {gap["offset"] + gap["length"] for gap in manifest["gaps"]}
    breaks = [i for i in range(1, len(frames)) if frames[i]["offset"] in gap_ends or stream(frames[i]) != stream(frames[i - 1])]
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


GUESSENC_MODES_BY_MISC = {0: "mono", 4: "simple LR stereo", 8: "dual channel", 12: "joint stereo", 16: "forced MS stereo", 20: "auto",
                          24: "intensity stereo"}


def guessenc_tag_lines(path):
    """mp3guessenc's report of the VBR header, the music CRC and the encoder string, by label (first occurrence)."""
    output = subprocess.run([guessenc_tool.binary(), "-v", "-r", "-s", str(path)], capture_output=True, text=True).stdout
    lines = {}
    for line in output.splitlines():
        if " tag detected into the first frame" in line:
            lines.setdefault("detected", line.split()[0])
        elif line.startswith("Unexpected data at "):
            lines.setdefault("unexpected", line)
        elif line.startswith("Music CRC verification"):
            lines.setdefault("music crc", line.split()[-1].rstrip("."))
        elif ":" in line:
            key, _, value = (part.strip() for part in line.partition(":"))
            lines.setdefault(key, value)
    return lines


def guessenc_lame_recognized(encoder):
    """mp3guessenc reads a LAME tag after "LAME", "L<digit>.<digit><digit>" or "lavc" (any case)."""
    return encoder.startswith("LAME") or re.match(r"L\d\.\d\d", encoder) is not None or encoder[:4].lower() == "lavc"


def guessenc_preset(value):
    if value == 0:
        return "No preset."
    if value < 321:
        return f"{value} kbps"
    if 409 < value < 501:
        return f"V{50 - value // 10} / VBR_{value - 400}"
    return {1000: "R3mix.", 1001: "Standard.", 1002: "Extreme.", 1003: "Insane.", 1004: "Standard fast.", 1005: "Extreme fast.",
            1006: "Medium.", 1007: "Medium fast."}.get(value, "Unknown preset.")


def guessenc_strategy(method, bitrate):
    if method in (2, 9):
        prefix = "ABR, "
    elif method in (1, 8):
        prefix = "CBR, "
    elif 2 < method < 7:
        prefix = f"VBR method {'old/rh' if method == 3 else 'mtrh' if method == 4 else 'mt'}, min "
    else:
        prefix = ""
    rate = "unknown" if bitrate == 0 else f"{bitrate} kbps" + (" or higher" if bitrate == 255 else "")
    return prefix + rate


def expected_guessenc(tag, music_crc):
    expected = {}
    if tag["type"] == "VBRI":
        size = tag["entry_size"]
        expected.update({"detected": "VBRI", "Tag version": str(tag["version"]), "Encoder delay": f"{tag['delay']} samples",
                         "File size": f"{tag['bytes']} bytes", "Number of frames": str(tag["frames"]), "Quality": str(tag["quality"]),
                         "TOC": f"{size * tag['toc_entries']} bytes ({tag['toc_entries']} entries, {size} byte{'' if size == 1 else 's'} each)"})
        return expected
    expected.update({"detected": "Xing", "File size": f"{tag['bytes'] or 0} bytes", "Number of frames": str(tag["frames"] or 0),
                     "TOC": "100 bytes (100 entries, 1 byte each)" if tag["toc"] else "no"})
    lame = tag.get("lame")
    if lame and guessenc_lame_recognized(lame["encoder"]):
        misc, flags, verified = lame["misc"], lame["flags"], lame["tag_crc"] == lame["tag_crc_computed"]
        expected.update({
            "Lame tag": "yes",
            # mp3guessenc drops the spaces LAME pads a short version with.
            "Lame short string": lame["encoder"].rstrip(" "),
            "Tag revision": "invalid!" if lame["revision"] == 15 else str(lame["revision"]),
            "Bitrate strategy": guessenc_strategy(lame["method"], lame["bitrate"]),
            "Lowpass value": str(lame["lowpass"] * 100) if lame["lowpass"] else "unknown",
            "nspsytune": "yes" if flags & 0x10 else "no",
            "nssafejoint": "yes" if flags & 0x20 else "no",
            "nogap continued": "yes" if flags & 0x40 else "no",
            "nogap continuation": "yes" if flags & 0x80 else "no",
            "ATH type": str(flags & 15),
            "Encoder delay (start)": f"{lame['delay']} samples",
            "Encoder padding (end)": f"{lame['padding']} samples",
            "Encoding mode": GUESSENC_MODES_BY_MISC.get(misc & 0x1C, "other"),
            "Unwise settings": "used" if misc & 0x20 else "not used",
            "Source frequency": ["32 kHz or below", "44.1 kHz", "48 kHz", "higher than 48 kHz"][misc >> 6],
            "Preset": guessenc_preset(lame["preset_surround"] & 0x7FF),
            "Originally encoded": f"{lame['music_length']} bytes",
            "Tag verification": "passed" if verified else "failed",
        })
        # mp3guessenc checks the music CRC, when the tag's holds, over every frame after the tag
        # frame, not over the music length.
        if not verified:
            expected["music crc"] = "performed"
        else:
            expected["music crc"] = "passed" if lame["music_crc"] == music_crc else "failed"
    else:
        expected["Lame tag"] = "no"
    return expected


def expected_mpg123(tag):
    """The notes mpg123 -vvv prints about a Xing/Info header and LAME tag (libmpg123 parse.c, check_lame_tag)."""
    notes = ["Xing/Lame/Info header detected"]
    if tag["frames"] is not None:
        notes.append(f"Xing: {tag['frames']} frames")
    if tag["bytes"] is not None:
        notes.append(f"Xing: {tag['bytes']} bytes")
    if tag["quality"] is not None:
        notes.append(f"Xing: quality = {tag['quality']}")
    lame = tag.get("lame")
    if lame:
        notes.append(f"Info: Encoder: {lame['encoder']}")
        version = re.match(r"LAME(\d+)\.(\d+)", lame["encoder"])
        if lame["encoder"].startswith("LAME") and not version:
            notes.append("Info: Cannot determine LAME version.")
        notes.append(f"Info: vbr mode {lame['method']}")
        offset = 6 if version and (int(version[1]), int(version[2])) < (3, 95) else 0
        gains = [0.0, 0.0]
        for value in (lame["radio_gain"], lame["audiophile_gain"]):
            name, origin = value >> 13, (value >> 10) & 7
            if origin and name in (1, 2):
                gains[name - 1] = (-0.1 if value & 0x200 else 0.1) * (value & 0x1FF) + (offset if origin == 3 else 0)
        notes.append(f"Info: Radio Gain = {gains[0]:03.1f}dB")
        notes.append(f"Info: Audiophile Gain = {gains[1]:03.1f}dB")
        if lame["method"] in (2, 9):
            notes.append(f"Info: ABR rate = {lame['bitrate']}")
        notes.append(f"Encoder delay = {lame['delay']}; padding = {lame['padding']}")
    return notes


def frames_after_tag(manifest):
    """The complete frames of the tag's stream after its frame, up to a change of version or sample
    rate, where mp3guessenc stops checking the music CRC."""
    if "frames" in manifest:
        stream = [manifest["frames"][0]["fields"][k] for k in ("Header/Version", "Header/Sample Rate")]
        frames = []
        for f in manifest["frames"][1:]:
            if [f["fields"][k] for k in ("Header/Version", "Header/Sample Rate")] != stream:
                break
            if not f.get("truncated"):
                frames.append((f["offset"], f["length"]))
        return frames
    return [tuple(f) for f in manifest.get("ffprobe_packets", [])]


def check_vbr_header(path, manifest):
    """mp3guessenc, mpg123 and ffprobe must read the first frame's VBR header as the manifest records it."""
    tag = manifest["vbr_header"]
    problems = []
    data = path.read_bytes()
    frames = frames_after_tag(manifest)
    music = b"".join(data[offset : offset + length] for offset, length in frames)
    # Without a frame list (free format), the bytes after the tag frame, which the manifest's own CRC covers.
    lame = tag.get("lame") if tag else None
    music_crc = vbr_tags.crc16(music) if frames or not lame else lame["music_crc_computed"]
    guessenc = guessenc_tag_lines(path)
    layer3 = "frames" not in manifest or manifest["frames"][0]["fields"]["Header/Layer"] == "III"
    if tag is None:
        # mp3guessenc looks for Xing in every layer; LAME, ffmpeg and mpg123 only in Layer III.
        if "detected" in guessenc and layer3:
            problems.append(f"mp3guessenc finds a {guessenc['detected']} header the manifest does not list")
    elif "unexpected" in guessenc:
        # mp3guessenc stops at some leading tags, such as an ID3v2.4 tag with a SEEK frame, and
        # never reaches the header.
        pass
    elif tag["truncated"]:
        if guessenc.get("detected") != ("VBRI" if tag["type"] == "VBRI" else "Xing"):
            problems.append("mp3guessenc does not find the truncated VBR header")
    else:
        expected = expected_guessenc(tag, music_crc)
        # mp3guessenc reads 0xFFFFFFFF, as Helix writes it for CBR, as -1: its mark for no quality,
        # and marks Gogo's "GOGO" in that place the same way (tags.c).
        if tag["type"] != "VBRI" and tag["quality"] == int.from_bytes(b"GOGO", "big"):
            expected["Quality"] = ""
        elif tag["type"] != "VBRI" and tag["quality"] is not None and tag["quality"] != 0xFFFFFFFF:
            expected["Quality"] = str(tag["quality"])
        for key, value in expected.items():
            # mp3guessenc appends the -q and -V switches a LAME VBR quality stands for.
            actual = guessenc.get(key, "").split(" (")[0] if key == "Quality" else guessenc.get(key)
            if actual != value:
                problems.append(f"mp3guessenc reports {key} {actual!r}, the manifest implies {value!r}")
    if "mp3guessenc_encoder_string" in manifest and guessenc.get("Encoder string") != manifest["mp3guessenc_encoder_string"]:
        problems.append(f"mp3guessenc finds the encoder string {guessenc.get('Encoder string')!r}, the manifest "
                        f"{manifest['mp3guessenc_encoder_string']!r}")

    notes = [line[len("Note: "):] for line in subprocess.run(["mpg123", "-vvv", "-t", str(path)], capture_output=True, text=True).stderr.splitlines()
             if line.startswith("Note: ") and (line.startswith("Note: Xing") or line.startswith("Note: Info: ") or line.startswith("Note: Encoder delay"))]
    # mpg123 prints the lowpass byte as "rev", never reads the peak, and compares the byte count with the file.
    notes = [n for n in notes if not n.startswith(("Info: rev ", "Info: peak", "Xing stream size")) and "Old LAME detected" not in n]
    if tag is None or tag["type"] == "VBRI":
        if notes:
            problems.append(f"mpg123 reads a header the manifest does not expect: {notes}")
    elif not tag["truncated"] and "encoder_string" not in tag and notes != expected_mpg123(tag):
        problems.append(f"mpg123 notes {notes}, the manifest implies {expected_mpg123(tag)}")

    # ffmpeg does not read free format.
    if ffmpeg_reads(tag) and not tag["truncated"] and not manifest.get("encoder", {}).get("free_format"):
        stream = json.loads(subprocess.run(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(path)],
                                           capture_output=True, text=True, check=True).stdout)["streams"][0]
        # ffprobe reports the rate of the last stream of joined files, but reads the tag with its own.
        rate = int(manifest["frames"][0]["fields"]["Header/Sample Rate"].split()[0]) if "frames" in manifest else int(stream["sample_rate"])
        mpeg1 = rate_is_mpeg1(rate)
        # ffmpeg drops the frame count of a file much longer than the header's byte count, as
        # "invalid concatenated file" (mp3dec.c, mp3_parse_info_tag).
        size = len(data) - tag["frame_offset"] - 4
        concatenated = tag["bytes"] and size > tag["bytes"] and size - tag["bytes"] > tag["bytes"] >> 4
        if tag["frames"] and not concatenated:
            # FFmpeg 8.0 leaves the encoder delay and padding out of the duration (mp3dec.c, mp3_parse_vbr_tags: the duration is frames * samples per frame
            # less start_pad and end_pad, which mp3_parse_info_tag reads from the raw bytes after any LAME, Lavf or Lavc string); 6.1.1 gave the frames' duration.
            trimmed = sum(ffmpeg_pads(tag) or (0, 0))
            duration = f"{(tag['frames'] * (1152 if mpeg1 else 576) - trimmed) / rate:.6f}"
            if stream.get("duration") != duration:
                problems.append(f"ffprobe duration {stream.get('duration')}, the header's frame count implies {duration}")
        lame = tag.get("lame")
        delay = ffmpeg_delay(tag)
        start = f"{(delay + 529) / rate:.6f}" if delay is not None else "0.000000"
        if stream.get("start_time") != start:
            problems.append(f"ffprobe start time {stream.get('start_time')}, the manifest implies {start}")
        valid = lame is not None and lame["tag_crc"] == lame["tag_crc_computed"]
        encoder = stream.get("tags", {}).get("encoder")
        if encoder != (lame["encoder"] if valid else None):
            problems.append(f"ffprobe encoder {encoder!r}, the manifest implies {lame['encoder'] if valid else None!r}")
        gains = valid and (lame["radio_gain"] >> 13 == 1 or lame["audiophile_gain"] >> 13 == 2)
        has_gain = any(d.get("side_data_type") == "Replay Gain" for d in stream.get("side_data_list", []))
        if has_gain != bool(gains):
            problems.append(f"ffprobe {'finds' if has_gain else 'does not find'} ReplayGain, the manifest implies the opposite")
    return problems


def ffmpeg_pads(tag):
    """The encoder delay and padding ffmpeg reads (mp3dec.c, mp3_parse_info_tag: the 24 bits at byte 21 after the Xing fields, 12 bits each), after any string
    starting LAME, Lavf or Lavc, a full tag or not; None without one."""
    after = bytes.fromhex(tag.get("after_fields", "")) if tag else b""
    if len(after) < 24 or after[:4] not in (b"LAME", b"Lavf", b"Lavc"):
        return None
    return after[21] << 4 | after[22] >> 4, (after[22] & 15) << 8 | after[23]


def ffmpeg_delay(tag):
    """The encoder delay ffmpeg reads, see ffmpeg_pads."""
    pads = ffmpeg_pads(tag)
    return None if pads is None else pads[0]


def rate_is_mpeg1(rate):
    return rate in (32000, 44100, 48000)


def ffmpeg_reads(tag):
    """Whether ffmpeg reads the header: it ignores a VBRI header of a version other than 1 (mp3dec.c,
    mp3_parse_vbri_tag)."""
    return tag is not None and (tag["type"] != "VBRI" or tag.get("version") == 1)


def ffprobe_skips_first(manifest):
    """ffmpeg skips the first frame when it holds a Layer III Xing, Info or VBRI header with a frame or byte count."""
    tag = manifest.get("vbr_header")
    return ffmpeg_reads(tag) and bool(tag.get("frames") or tag.get("bytes"))


def guessenc_stream(path):
    """mp3guessenc's figures for the stream: frames, length, average bitrate and the bitrate histogram."""
    output = subprocess.run([guessenc_tool.binary(), "-m", "-f", str(path)], capture_output=True, text=True).stdout
    stream = {"histogram": {}}
    in_stream = False
    for line in output.splitlines():
        if line.startswith("Detected MPEG stream"):
            in_stream = True
        key, _, value = (part.strip() for part in line.partition(":"))
        if in_stream and key in ("Length", "Data rate", "Number of frames") and key not in stream:
            stream[key] = value.removesuffix(" (free format bitstream)")
        match = re.match(r"\s*(\d+|free) kbps\s*:\s*(\d+)\s*\(\s*([\d.]+)%\)", line)
        if match:
            stream["histogram"][match[1]] = (int(match[2]), match[3])
    return stream


def region_infos(manifest, region):
    """A region's frames as stream_stats.FrameInfo, from the manifest."""
    infos = []
    for frame in manifest["frames"]:
        if not region["offset"] <= frame["offset"] < region["offset"] + region["length"]:
            continue
        fields = frame["fields"]
        bitrate = fields["Header/Bitrate"]
        header = {} if "Xing" in fields or any(k.startswith("VBRI") for k in fields) else None
        infos.append(stream_stats.FrameInfo(frame["offset"], frame["length"], fields["Header/Version"], fields["Header/Layer"],
                                            int(fields["Header/Sample Rate"].split()[0]),
                                            None if bitrate == "free" else int(bitrate.split()[0]),
                                            header=header, truncated=frame.get("truncated", False)))
    return infos


def guessenc_problems(actual, statistics, exact):
    """Differences between mp3guessenc's stream figures and a region's statistics; at an exact tie
    either rounding counts as agreement (stream_stats.agrees)."""
    problems = []
    if actual.get("Number of frames") != statistics["Statistics/Frames"]:
        problems.append(f"mp3guessenc counts {actual.get('Number of frames')} frames, the manifest {statistics['Statistics/Frames']}")
    hours, minutes, seconds = actual.get("Length", "0:0:0").split(":")
    ms = (int(hours) * 3600 + int(minutes) * 60) * 1000 + stream_stats.Fraction(seconds) * 1000
    if not stream_stats.agrees(str(ms), exact["ms"], stream_stats.Fraction(1)):
        problems.append(f"mp3guessenc's length {actual.get('Length')} disagrees with {statistics['Statistics/Duration']}")
    rate = actual.get("Data rate", "0 kbps").split()[0]
    if not stream_stats.agrees(rate, exact["kbps"], stream_stats.Fraction(1, 10)):
        problems.append(f"mp3guessenc's data rate {actual.get('Data rate')} disagrees with {statistics['Statistics/Average Bitrate']}")
    # mp3guessenc leaves free-format frames out of its histogram.
    expected_bars = {str(b): c for b, c in exact["counts"].items() if b is not None}
    if {k: v[0] for k, v in actual["histogram"].items()} != expected_bars:
        problems.append(f"mp3guessenc's histogram {actual['histogram']} disagrees with the counts {expected_bars}")
    for bitrate, (_, percent) in actual["histogram"].items():
        share = exact["percent"].get(int(bitrate))
        if share is None or not stream_stats.agrees(percent, share, stream_stats.Fraction(1, 10)):
            problems.append(f"mp3guessenc's {bitrate} kbps share {percent}% disagrees with {share}")
    return problems


def guessenc_guess(path):
    """The encoder mp3guessenc guesses, from its "Maybe this file is encoded by" line, or None."""
    output = subprocess.run([guessenc_tool.binary(), str(path)], capture_output=True, text=True).stdout
    for line in output.splitlines():
        if line.startswith("Maybe this file is encoded by "):
            return line[len("Maybe this file is encoded by "):].strip()
    return None


def guessenc_ofl(path):
    """The delay and original length mp3guessenc reads from an Original File Length block, or None.
    For MP3 Surround it adds 576 samples to the delay it reads."""
    output = subprocess.run([guessenc_tool.binary(), str(path)], capture_output=True, text=True).stdout
    if "Original File Length block found" not in output:
        return None
    ofl = {}
    for line in output.splitlines():
        key, _, value = (part.strip() for part in line.partition(":"))
        if key == "Encoder delay":
            ofl["delay"] = int(value.split()[0])
        elif key == "Length of original audio":
            ofl["length"] = int(value.split()[0])
    return ofl


def decoded_onset(path):
    """The first sample of ffmpeg's mono downmix louder than 200: where an encoded tone that starts at
    sample 0 comes out, the encoder's and the decoder's delay together."""
    pcm = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-ac", "1", "-f", "s16le", "-"], capture_output=True).stdout
    for i in range(0, len(pcm) - 1, 2):
        if abs(int.from_bytes(pcm[i : i + 2], "little", signed=True)) > 200:
            return i // 2
    return None


DECODER_DELAY = 529


def decoded_samples(path, channels):
    """Samples per channel ffmpeg and mpg123 output with their gapless trimming, which follows the LAME tag."""
    counts = {}
    for name, command in [("ffmpeg", ["ffmpeg", "-v", "error", "-i", str(path), "-f", "s16le", "-"]),
                          ("mpg123", ["mpg123", "-q", "-s", str(path)])]:
        counts[name] = len(subprocess.run(command, capture_output=True).stdout) // (2 * channels)
    return counts


# Findings about frames mp3guessenc skips or reads differently; the others leave its counts alone.
GUESSENC_NEUTRAL = ("mpeg.vbr-", "mpeg.lame-", "mpeg.music-", "mpeg.xing-", "mpeg.gapless-")
# mp3guessenc reads free format, but takes every frame to be as long as the first.
GUESSENC_NEUTRAL_CODES = ("mpeg.free-format",)
VARIABLE_HEADER_FIELDS = {"Header/Bitrate", "Header/HasPadding", "Header/Mode Extension"}


def guessenc_counts_like_us(path, manifest):
    """Whether mp3guessenc takes the same frames as audio: it skips frames whose constant header
    fields change, frames that fail their CRC or hold invalid side information, and Layer II modes
    the standard does not allow; it reads "Xing" in any layer, and only the first VBR header."""
    frames = manifest["frames"]
    constant = [{k: v for k, v in f["fields"].items() if k.startswith("Header/") and k not in VARIABLE_HEADER_FIELDS} for f in frames]
    headers = sum(1 for f in frames if "Xing" in f["fields"] or "VBRI/Version" in f["fields"] or "VBRI" in f["fields"])
    # mp3guessenc finds the length of a free-format Layer I frame no better than 640 kbps of a fixed one: it counts one frame of
    # mpeg1-layer1-free-format.mp1 and a data rate of 640 kbps.
    layer1_free = any(c.get("Header/Layer") == "I" and c.get("Header/Bitrate") == "free" for c in [f["fields"] for f in frames])
    return (sum(1 for r in manifest["regions"] if "statistics" in r) == 1 and not manifest["gaps"] and not layer1_free
            and all(c == constant[0] for c in constant) and headers <= 1
            and all(f["code"].startswith(GUESSENC_NEUTRAL) or f["code"] in GUESSENC_NEUTRAL_CODES for f in manifest["findings"])
            and ("detected" in guessenc_tag_lines(path)) == (manifest.get("vbr_header") is not None))


def check_statistics(path, manifest):
    """mp3guessenc must count the frames, length, average bitrate and histogram of a single-region
    stream as the manifest does, and ffmpeg and mpg123 must decode the gapless sample count where
    the VBR header's counts hold; decoders trim by the counts the header claims."""
    problems = []
    regions = [r for r in manifest["regions"] if "statistics" in r]
    if guessenc_counts_like_us(path, manifest):
        problems += guessenc_problems(guessenc_stream(path), regions[0]["statistics"], stream_stats.exact(region_infos(manifest, regions[0])))
    # Decoders play the frames of a joined stream too, and differ on a truncated last frame, which
    # ffmpeg decodes as far as it goes and mpg123 leaves out.
    consistent = not any(f["code"] in ("mpeg.vbr-frame-count-mismatch", "mpeg.vbr-header-mid-stream", "mpeg.stream-changed")
                         for f in manifest["findings"])
    truncated = any(f["code"] == "mpeg.frame-truncated" for f in manifest["findings"])
    for region in regions:
        count = region["statistics"].get("Statistics/Gapless/Sample Count")
        padding = int(region["statistics"].get("Statistics/Gapless/Encoder Padding", "0 samples").split()[0])
        # Decoders discard their own 529-sample delay at the end too; with less padding (the first
        # file of a --nogap sequence) those samples belong to the next file.
        if count is not None and consistent and padding >= DECODER_DELAY:
            channels = 1 if manifest["frames"][0]["fields"]["Header/Channel Mode"] == "Mono" else 2
            for decoder, samples in decoded_samples(path, channels).items():
                if samples != int(count) and not (truncated and decoder == "mpg123"):
                    problems.append(f"{decoder} decodes {samples} samples with gapless trimming, the manifest {count}")
    return problems


# LAME releases whose tag has no preset field, which getID3 and the analyzer read from its settings.
PRESETLESS_LAME = ("LAME3.90", "LAME3.90.", "LAME3.91", "LAME3.92")


def check_getid3(path, manifest):
    """getID3 must read the same LAME version and, where it names one, the same preset. It does not
    name the ABR and CBR alt-presets, which it reports as the bitrate mode."""
    lame = (manifest.get("vbr_header") or {}).get("lame")
    if not lame or lame["encoder"].rstrip(" ") not in PRESETLESS_LAME:
        return []
    problems = []
    version, options = getid3_tool.encoder(path)
    expected_version = "LAME" + manifest["detected_encoder"]["name"][len("LAME "):]
    if version != expected_version:
        problems.append(f"getID3 reads the encoder {version!r}, the manifest implies {expected_version!r}")
    match = manifest.get("vbr_header_fields", {}).get("LAME Tag/Preset/Settings Match")
    preset = match.split(";")[0] if match else None
    if options and options.startswith("--"):
        if options != preset:
            problems.append(f"getID3 guesses {options!r}, the manifest implies {preset!r}")
    elif preset and not re.fullmatch(r"--alt-preset (cbr )?\d+", preset):
        problems.append(f"getID3 names no preset ({options!r}), the manifest implies {preset!r}")
    return problems


# --- Coding statistics, mp3guessenc and EncSpot ---------------------------------------------------

GUESSENC_STEREO = {"Simple stereo": "Left/right stereo", "Intensity stereo": "Intensity stereo", "Mid-side stereo": "Mid/side stereo",
                   "Intensity and mid-side stereo": "Mid/side and intensity stereo"}


def guessenc_coding(path):
    """The coding statistics mp3guessenc 0.27.6 prints about the stream (mp3guessenc.c, show_info):
    frames, stereo modes, block usage, global gain, ancillary data, the largest main_data_begin
    ("Max reservoir"), whether scalefactor scaling, scfsi and padding are used, and the frame
    histogram with its size distribution, each count with the share it prints."""
    output = subprocess.run([guessenc_tool.binary(), "-e", str(path)], capture_output=True, text=True).stdout
    report = {"stereo": {}, "blocks": {}, "gains": {}, "histogram": {}}
    started = False
    for line in output.splitlines():
        started |= line.startswith("Detected MPEG stream")
        if not started:
            continue
        bar = re.match(r"^\s*(?:(\d+) kbps|Custom frames)\s*:\s*(\d+)\s*\(\s*([\d.]+)%\), size distr: \[(.*)\]$", line)
        if bar:
            sizes = [(int(n), int(size)) for n, size in re.findall(r"(\d+) x\s*(\d+) B", bar[4])]
            report["histogram"]["free" if bar[1] is None else int(bar[1])] = (int(bar[2]), bar[3], sizes)
            continue
        key, _, value = (part.strip() for part in line.partition(":"))
        share = re.match(r"(\d+)\s*\(\s*([\d.]+)%\)", value)
        if key in GUESSENC_STEREO and share:
            report["stereo"][GUESSENC_STEREO[key]] = (int(share[1]), share[2])
        elif key.endswith(" block granules") and share:
            report["blocks"][key.split()[0]] = (int(share[1]), share[2])
        elif key in ("Min global gain", "Max global gain"):
            for channel, gain in re.findall(r"([lrc])=\s*(\d+)", value):
                report["gains"].setdefault("1" if channel == "r" else "0", {})[key.split()[0]] = int(gain)
        elif key == "Total amount":
            total = re.match(r"(\d+) bytes \(\s*([\d.]+)%\)", value)
            report["ancillary"], report["ancillary_share"] = int(total[1]), total[2]
        elif key in ("Number of frames", "Audio samples per frame", "Min packet", "Max packet", "Max reservoir") and key not in report:
            report[key] = int(value.split()[0])
        elif key in ("Bitrate", "Audio frequency") and key not in report:
            report[key] = value.split()[0]
        elif key in ("Scalefactor scaling used", "Scalefactor selection information used", "Padding used"):
            report[key] = value == "yes"
    return report


def count_of(text):
    return int(text.split()[0]) if text else 0


def statistics_of(manifest):
    """The coding statistics of a fixture's single MPEG Audio region, by node path, or None where
    it has several."""
    if "statistics" in manifest:
        return manifest["statistics"]
    regions = [r["statistics"] for r in manifest["regions"] if "statistics" in r]
    return regions[0] if len(regions) == 1 else None


def statistics_frames(fields):
    return sum(count_of(v) for k, v in fields.items() if k.startswith("Statistics/Channel Modes/"))


def is_layer3(manifest):
    if "encoder" in manifest:
        return manifest["encoder"]["layer"] == 3
    return bool(manifest["frames"]) and manifest["frames"][0]["fields"]["Header/Layer"] == "III"


# Findings in whose files mp3guessenc reads other frames or other main data than the analyzer, though
# it may count as many frames, and why.
GUESSENC_READS_OTHERWISE = {
    "mpeg.frame-cut": "mp3guessenc reads a frame whose header is damaged at the length that header gives",
    "mpeg.free-format-length-changed": "mp3guessenc takes every free-format frame to be as long as the first",
    "mpeg.main-data-in-vbr-header-frame": "mp3guessenc skips the VBR header frame, so it does not find main data that begins there "
                                          "and leaves out the ancillary segments next to it",
}


def guessenc_frames_differ(path, manifest, counted, ours):
    """Why mp3guessenc takes other frames than the statistics, or None where it takes theirs. It
    reads the bit reservoir anew after data it resyncs over, reads the files of
    GUESSENC_READS_OTHERWISE otherwise, and in hand-built fixtures skips or resyncs at the frames
    guessenc_counts_like_us lists (header fields that change, a CRC that fails, a stream change),
    where the analyzer keeps them. It leaves out a truncated last frame with its ancillary segment,
    as the statistics do, but counts one whose main data is all there (BladeEnc 0.80 to 0.83 cut
    only stuffing off the last frame), which the statistics leave out like every truncated frame."""
    codes = {f["code"] if isinstance(f, dict) else f for f in manifest.get("findings", [])}
    if manifest.get("gaps"):
        return "mp3guessenc reads the bit reservoir anew after data it resyncs over"
    for code in sorted(codes & set(GUESSENC_READS_OTHERWISE)):
        return GUESSENC_READS_OTHERWISE[code]
    if counted == ours:
        return None
    if counted == ours + 1 and "mpeg.frame-truncated" in codes:
        return "mp3guessenc also counts the truncated last frame, whose main data is all there"
    if "frames" in manifest:
        kept = dict(manifest, findings=[f for f in manifest["findings"] if f["code"] != "mpeg.frame-truncated"])
        if not guessenc_counts_like_us(path, kept):
            return "mp3guessenc skips or resyncs at frames the analyzer keeps (guessenc_counts_like_us)"
    return f"mp3guessenc counts {counted} frames, the statistics {ours}, for no known reason"


def guessenc_coding_problems(path, manifest):
    """mp3guessenc's coding statistics against the manifest's, for a Layer III fixture whose frames
    it counts as the statistics do. Shares agree when mp3guessenc's floating-point text is the exact
    share rounded half to even, or its other neighbor at an exact tie (stream_stats.agrees). Its
    stereo mode shares are of all frames, so they are compared where every frame is joint stereo."""
    fields = statistics_of(manifest)
    if fields is None or not is_layer3(manifest):
        return [], "skipped: not one Layer III region"
    actual = guessenc_coding(path)
    frames = statistics_frames(fields)
    reason = guessenc_frames_differ(path, manifest, actual.get("Number of frames"), frames)
    if reason is not None:
        return ([reason] if reason.endswith("no known reason") else []), reason
    problems = []
    tenth = stream_stats.Fraction(1, 10)

    def compare(name, ours, theirs, whole=None):
        count, share = theirs if isinstance(theirs, tuple) else (theirs, None)
        if ours != count:
            problems.append(f"mp3guessenc: {name} {count}, the manifest {ours}")
        elif share is not None and whole and not stream_stats.agrees(share, stream_stats.Fraction(count * 100, whole), tenth):
            problems.append(f"mp3guessenc: {name} share {share}%, exact {float(stream_stats.Fraction(count * 100, whole)):.3f}%")

    joint = count_of(fields.get("Statistics/Joint Stereo Modes"))
    for name in GUESSENC_STEREO.values():
        compare(name, count_of(fields.get(f"Statistics/Joint Stereo Modes/{name}")), actual["stereo"].get(name, (0, None)),
                joint if joint == frames else None)
    granules = count_of(fields.get("Statistics/Block Usage"))
    blocks = {kind: count_of(fields.get(f"Statistics/Block Usage/{kind}")) for kind in stream_stats.BLOCK_KINDS}
    # mp3guessenc's "switch" granules are start, stop and reserved ones.
    ours = {"Long": blocks["Long"], "Switch": blocks["Start"] + blocks["Stop"] + blocks["Reserved"], "Short": blocks["Short"], "Mixed": blocks["Mixed"]}
    for kind, count in ours.items():
        compare(f"{kind.lower()} block granules", count, actual["blocks"].get(kind, (0, None)), granules)
    gains = {ch: {"Min": int(fields[f"Statistics/Global Gain/Channel {ch}/Minimum"]), "Max": int(fields[f"Statistics/Global Gain/Channel {ch}/Maximum"])}
             for ch in "01" if f"Statistics/Global Gain/Channel {ch}/Minimum" in fields}
    if gains != actual["gains"]:
        problems.append(f"mp3guessenc: global gain {actual['gains']}, the manifest {gains}")

    total = count_of(fields["Statistics/Ancillary Data"])
    compare("ancillary bytes", total, actual["ancillary"])
    compare("smallest ancillary amount", count_of(fields.get("Statistics/Ancillary Data/Smallest Nonzero Amount")), actual.get("Min packet", 0))
    compare("largest ancillary amount", count_of(fields.get("Statistics/Ancillary Data/Largest Amount")), actual.get("Max packet", 0))
    # Its share is of the frames' lengths, which its histogram lists, and its data rate over their duration.
    length = sum(n * size for _, _, sizes in actual["histogram"].values() for n, size in sizes)
    exact_share = stream_stats.Fraction(total * 100, length)
    if not stream_stats.agrees(actual["ancillary_share"], exact_share, tenth) or fields["Statistics/Ancillary Data/Share"] != stream_stats.tenths_text(exact_share, "%"):
        problems.append(f"mp3guessenc: ancillary share {actual['ancillary_share']}%, the manifest {fields['Statistics/Ancillary Data/Share']}")
    exact_rate = stream_stats.Fraction(total * 8 * int(actual["Audio frequency"]), actual["Number of frames"] * actual["Audio samples per frame"] * 1000)
    if not stream_stats.agrees(actual["Bitrate"], exact_rate, tenth) or fields["Statistics/Ancillary Data/Data Rate"] != stream_stats.tenths_text(exact_rate, "kbps"):
        problems.append(f"mp3guessenc: ancillary data rate {actual['Bitrate']} kbps, the manifest {fields['Statistics/Ancillary Data/Data Rate']}")
    compare("max reservoir", count_of(fields["Statistics/Maximum Main Data Begin"]), actual["Max reservoir"])
    for key, name in [("Scalefactor scaling used", "UsesScalefactorScaling"), ("Scalefactor selection information used", "UsesScfsi"),
                      ("Padding used", "UsesPadding")]:
        if (fields[f"Statistics/{name}"] == "true") != actual[key]:
            problems.append(f"mp3guessenc: {key} {actual[key]}, the manifest {fields[f'Statistics/{name}']}")

    histogram = {}
    for path_, value in fields.items():
        parts = path_.split("/")
        if len(parts) == 4 and parts[1] == "Bitrate Distribution" and parts[3] in ("Unpadded", "Padded"):
            sizes = [(int(n), int(size)) for n, size in re.findall(r"(\d+) frames? of (\d+) bytes", value)]
            key = "free" if parts[2] == "free" else int(parts[2].split()[0])
            histogram.setdefault(key, {})[parts[3]] = sizes
    for key in set(histogram) | set(actual["histogram"]):
        ours_sizes = histogram.get(key, {}).get("Unpadded", []) + histogram.get(key, {}).get("Padded", [])
        count, share, sizes = actual["histogram"].get(key, (0, None, []))
        if ours_sizes != sizes:
            problems.append(f"mp3guessenc: {key} kbps frames {sizes}, the manifest {ours_sizes}")
        elif share is not None and not stream_stats.agrees(share, stream_stats.Fraction(count * 100, frames), tenth):
            problems.append(f"mp3guessenc: {key} kbps share {share}%")
    return problems, None


# EncSpot Console 2.01 (third_party/encspot, lib/Mp3File.cpp), emulated over a file's bytes to know
# which frames it reads. It analyzes Layer III only (it reads every frame as Layer III) and rejects
# free format. Its first steps (ProcessFrames): after an ID3v2 tag it finds the first frame
# (SeekNextHeader: a header followed by four more at the lengths they give), looks for "Xing",
# "Info" or "VBRI" in the 2000 bytes from there (GetVBRTags), and returns to that frame only where
# it finds one; otherwise the file position stays 2000 bytes on, so it finds the first frame again
# from there. It always skips the frame it finds, taking it for the VBR header frame, and reads the
# frames after it up to an ID3v1 tag or the end of the file, counting a truncated last frame.
ENCSPOT_TABSEL = [
    [[128, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448], [128, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
     [128, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]],
    [[128, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256], [128, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
     [128, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]],
]
ENCSPOT_FREQS = [44100, 48000, 32000, 22050, 24000, 16000, 11025, 12000, 8000]
ENCSPOT_MODES = ["stereo", "joint stereo", "dual channel", "mono"]
# sizeof(XHEADDATA) and sizeof(VBRIDATA) on x86-64: GetVBRTags takes a tag that leaves room for them.
XHEADDATA_SIZE, VBRIDATA_SIZE = 96, 1048


def encspot_header(data, pos):
    """EncSpot's MakeHeader: (frame length, id, layer, channels, frequency, bitrate index, mode) of
    the header at pos, or None where it takes none there."""
    if pos + 4 > len(data):
        return None
    head = int.from_bytes(data[pos : pos + 4], "big")
    if head & 0xFFE00000 != 0xFFE00000 or not (head >> 17) & 3 or (head >> 12) & 15 == 15 or (head >> 10) & 3 == 3:
        return None
    mpeg_id, idex, rate = (head >> 19) & 1, (head >> 20) & 1, (head >> 10) & 3
    freq = ENCSPOT_FREQS[rate + (1 - mpeg_id) * 3 if idex else 6 + rate]
    layer, index, mode = 4 - ((head >> 17) & 3), (head >> 12) & 15, (head >> 6) & 3
    length = ENCSPOT_TABSEL[1 - mpeg_id][layer - 1][index] * 144000 // (freq << (1 - mpeg_id)) + ((head >> 9) & 1)
    return length, mpeg_id, layer, 1 if mode == 3 else 2, freq, index, mode


def encspot_seek(data, start):
    """EncSpot's SeekNextHeader from start, or None where it fails."""
    window = min(1000 * 1024, len(data) - start)
    for i in range(max(window, 0) // 2):
        if data[start + i] != 0xFF:
            continue
        j, safety, length = i, 0, 1
        while safety < 5 and length:
            header = encspot_header(data, start + j) if j + 4 <= window else None
            length = header[0] if header else 0
            j += length
            if j + 4 >= window:
                return None
            safety += 1
        if safety == 5:
            return start + i
    return None


def encspot_frames(data):
    """The offsets of the frames EncSpot reads and the header it skips, or a reason it fails or
    resyncs, which is not emulated."""
    tag_end = 10 + (data[6] << 21 | data[7] << 14 | data[8] << 7 | data[9]) if data[:3] == b"ID3" else 0
    first = encspot_seek(data, tag_end)
    if first is None:
        return "EncSpot finds no frame"
    read = min(2000, len(data) - first)
    window = data[first : first + read]
    found = [p for p in (window.find(b"Xing"), window.find(b"Info")) if p >= 0]
    vbri = window.find(b"VBRI")
    returns = (found and min(found) + XHEADDATA_SIZE <= read) or (vbri >= 0 and vbri + VBRIDATA_SIZE <= read)
    skipped = encspot_seek(data, first if returns else first + read)
    if skipped is None:
        return "EncSpot finds no frame"
    base = encspot_header(data, skipped)
    if base[5] == 0:
        return "EncSpot rejects free format"
    id3v1 = len(data) - 128 if data[-128:-125] == b"TAG" else -1
    frames = []
    position = skipped + base[0]
    while position != id3v1 and position < len(data):
        header = encspot_header(data, position)
        if header is None or header[2:5] != base[2:5] or header[5] == 0:
            return "EncSpot resyncs (ReportOutOfSyncError or a header it does not take), which is not emulated"
        frames.append(position)
        position += header[0]
    return skipped, frames


def float32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


def encspot_expected(data, skipped, frames):
    """EncSpot's report over the frames it reads (mp3data_string::update and get_report)."""
    base = encspot_header(data, skipped)
    padded = data + bytes(64)  # readBits reads zeros past the end
    begins, scaled, counts = [], 0, {}
    for offset in frames:
        header = encspot_header(data, offset)
        coding = stream_stats.read_frame(padded, offset, header[0])
        side = coding["side_info"]
        begins.append(side["main_data_begin"])
        scaled += sum(g["scalefac_scale"] for g in side["granules"])
        counts[header[5]] = counts.get(header[5], 0) + 1
    n = len(frames)
    granules = (2 if base[1] else 1) * base[3] * n
    return {
        "Frames": str(n),
        "Av. Reservoir": str(sum(begins) // n if n else 0),
        "Max. Reservoir": str(max(begins, default=0)),
        "Scalefac": "not used" if not scaled or not n else "%.1f%%" % (scaled * 100.0 / granules),
        "Mode": ENCSPOT_MODES[base[6]],
        "bitrates": {ENCSPOT_TABSEL[1 - base[1]][base[2] - 1][i]: "%.1f" % float32(float32(c * 100) / n) for i, c in counts.items()},
    }


def fixture_frames(manifest):
    """The offsets of a fixture's frames the statistics count: complete, without a VBR header. An
    encoder-written fixture lists the packets ffprobe found, all frames but the VBR header frame;
    its last is truncated where its findings say."""
    if "frames" in manifest:
        return [f["offset"] for f in manifest["frames"]
                if not f.get("truncated") and "Xing" not in f["fields"] and not any(k.startswith("VBRI") for k in f["fields"])]
    packets = [offset for offset, _ in manifest["ffprobe_packets"]]
    return packets[:-1] if "mpeg.frame-truncated" in manifest.get("findings", []) else packets


# Encoders EncSpot names for encoder-written fixtures where the analyzer names another one or none:
# EncSpot's name and why. Its label is the encoder field of a Xing or Info tag where there is one,
# and otherwise a "LAME", "GOGO", "MPGE", "VBRI" or mp3PRO string in the first 1024 bytes after the
# ID3v2 tag or in the last 1024 (GetLabels); without a label it names, EncSpot guesses from the habits of
# the encoders of 2002 over the frames it reads (mp3data_string::guessenc); the analyzer names an
# encoder only from a tag, a signature or habits that encodes with that encoder show every time
# (docs/PLAN.md), so these stay differences. Hand-built frames come from no encoder, so EncSpot's
# guess about them is not compared.
BLADE_AS_GOGO = ("Gogo (before 3.0)", "short blocks without scfsi or scalefactor scaling in plain stereo or mono at one bitrate name BladeEnc "
                 "in EncSpot only where the mean main_data_begin (its Av. Reservoir) is below 40 bytes and a frame is padded; these BladeEnc "
                 "encodes use the bit reservoir more (47 to 63 bytes)")
SHINE_AS_XING = ("Xing (new)", "without short blocks, intensity stereo or dual channel, EncSpot names Xing, the new one where scfsi is used; "
                 "its Shine rule wants dual channel")
NO_SCFSI_AS_XING = ("Xing (old)", "without short blocks, scfsi, intensity stereo or dual channel, EncSpot names the old Xing outside joint "
                    "stereo (its Plugger rule wants plain stereo without scalefactor scaling or the original bit)")
LAME_UNLABELED = ("Gogo (after 3.0)", "EncSpot's label is the encoder field of ffmpeg's Xing tag, \"Lavc\", which names nothing, so it guesses: "
                  "short blocks with scfsi and scalefactor scaling name Gogo after 3.0; the analyzer reads LAME's version from the ancillary "
                  "data behind ffmpeg's tag")
HELIX_SCALING = ("Gogo (after 3.0)", "short blocks with scfsi and scalefactor scaling name Gogo after 3.0 in EncSpot's guess; the analyzer knows "
                 "Helix without its tag from the copyright and original bits with scfsi and scalefactor scaling")
HELIX_PLAIN = ("Lame (old) or m3e", "short blocks with scfsi but no scalefactor scaling name old LAME or m3e in EncSpot's guess; the analyzer "
               "knows Helix without its tag from the copyright and original bits with scfsi and scalefactor scaling in other frames")
ENCSPOT_ENCODER_DIFFERENCES = {
    "external-ffmpeg-bitexact": ("Gogo (after 3.0)", "EncSpot's label is the encoder field of ffmpeg's Xing tag, \"Lavf lame\" (the muxer's replacement of the bit-exact "
                                 "\"Lavc libmp3lame\", mp3enc.c:250-252), which names nothing, so it guesses: short blocks with scfsi and scalefactor scaling "
                                 "name Gogo after 3.0; the analyzer reads LAME's version from the ancillary data behind ffmpeg's tag"),
    "external-blade-crc": BLADE_AS_GOGO,
    "external-blade-mono": BLADE_AS_GOGO,
    "external-blade-stereo": BLADE_AS_GOGO,
    "external-blade082-stereo": BLADE_AS_GOGO,
    "external-blade084-stereo": BLADE_AS_GOGO,
    "external-blade0920-stereo": BLADE_AS_GOGO,
    "external-blade0927-stereo": BLADE_AS_GOGO,
    "external-blade0936-stereo": BLADE_AS_GOGO,
    "external-ffmpeg-remux-blade": BLADE_AS_GOGO,
    "external-blade080-stereo": ("Blade", "BladeEnc 0.80 drains an overflowing bit reservoir into ancillary data mid-stream, as the ISO dist10 "
                                 "encoder does, which leaves it unnamed by the analyzer and mp3guessenc; EncSpot's mean main_data_begin "
                                 "of 22 bytes is below its threshold of 40"),
    "external-ffmpeg-cbr": LAME_UNLABELED,
    "external-ffmpeg-mono": LAME_UNLABELED,
    "external-lame399a-cbr": ("Gogo (after 3.0)", "EncSpot's label is the encoder field of the LAME tag, \"L3.99a12\", which it does not read "
                              "as LAME, so it guesses: short blocks with scfsi and scalefactor scaling name Gogo after 3.0"),
    "external-ffmpeg-shine": SHINE_AS_XING,
    "external-shine-joint": SHINE_AS_XING,
    "external-shine-mono": SHINE_AS_XING,
    "external-shine-music": SHINE_AS_XING,
    "external-shine-mpeg2": NO_SCFSI_AS_XING,
    "external-shine-silence": NO_SCFSI_AS_XING,
    "external-fhg-mono-cbr": NO_SCFSI_AS_XING,
    "external-helix-no-tag-mono": HELIX_SCALING,
    "external-helix-r11-mono": HELIX_SCALING,
    "external-helix-no-tag": HELIX_PLAIN,
    "external-helix-r11-cbr": HELIX_PLAIN,
    "external-helix-r11-vbr": HELIX_PLAIN,
}

# EncSpot's names by the analyzer's: the start of EncSpot's name for each encoder the analyzer names.
ENCSPOT_NAMES = {"LAME": ("Lame",), "BladeEnc": ("Blade",), "Fraunhofer": ("FhG",), "Shine": ("Shine",), "Xing": ("Xing",), "Gogo": ("Gogo",),
                 "Helix MP3 Encoder": (), "TwoLAME or Xing": (), "mp3PRO": ("RCA", "Thomson")}


def encspot_names_same(theirs, ours):
    """Whether EncSpot's encoder name is the analyzer's encoder, or both name none."""
    if ours is None:
        return False
    family = next((key for key in ENCSPOT_NAMES if ours.startswith(key)), ours)
    return theirs.startswith(ENCSPOT_NAMES.get(family, (family,)) or ("\0",))


def encspot_problems(path, manifest):
    """EncSpot's report against the fixture's statistics (Av. Reservoir, which it truncates to whole
    bytes, Max. Reservoir, Scalefac, the bitrate histogram, frames and mode) where it reads the
    frames they count, and otherwise against its emulation over the frames it reads; and its
    encoder against the one the analyzer names. Returns the problems and how it was compared."""
    fields = statistics_of(manifest)
    if fields is None or not is_layer3(manifest) or manifest.get("encoder", {}).get("free_format") or "ffprobe_packets" not in manifest and "frames" not in manifest:
        return [], "skipped: not one Layer III region EncSpot reads (several regions, another layer or free format)"
    data = path.read_bytes()
    actual = encspot_tool.report(path)
    emulated = encspot_frames(data)
    if isinstance(emulated, str):
        expected_error = emulated in ("EncSpot finds no frame", "EncSpot rejects free format")
        if expected_error != ("error" in actual):
            return [f"EncSpot: {actual.get('error', 'reads the file')}, the emulation: {emulated}"], None
        return [], f"skipped: {emulated}"
    skipped, frames = emulated
    lame = (manifest.get("vbr_header") or {}).get("lame")
    if "error" in actual and lame and (lame["misc"] >> 2) & 7 == 7:
        # mp3data_string::update_lame indexes its seven stereo mode names with the field's eight values.
        return [], "skipped: EncSpot aborts on the LAME tag's stereo mode 7, which the Helix encoder writes"
    if "error" in actual:
        return [f"EncSpot fails ({actual['error']}) where the emulation reads {len(frames)} frames"], None
    problems = []
    expected = encspot_expected(data, skipped, frames)
    for key, value in expected.items():
        if actual.get(key) != value:
            problems.append(f"EncSpot: {key} {actual.get(key)!r}, its emulation {value!r}")
    if actual.get("Sync Errors") != "none":
        problems.append(f"EncSpot reports sync errors: {actual.get('Sync Errors')}")

    direct = frames == fixture_frames(manifest)
    if direct:
        frames_count = statistics_frames(fields)
        # The mean is shown to a tenth, so it lies within half a tenth of the text.
        average = stream_stats.Fraction(fields["Statistics/Average Main Data Begin"].split()[0])
        half = stream_stats.Fraction(1, 20)
        if not (average - half) // 1 <= int(actual["Av. Reservoir"]) <= (average + half) // 1:
            problems.append(f"EncSpot: Av. Reservoir {actual['Av. Reservoir']}, the manifest {fields['Statistics/Average Main Data Begin']}")
        if actual["Max. Reservoir"] != fields["Statistics/Maximum Main Data Begin"].split()[0]:
            problems.append(f"EncSpot: Max. Reservoir {actual['Max. Reservoir']}, the manifest {fields['Statistics/Maximum Main Data Begin']}")
        scaling = "not used" if fields["Statistics/UsesScalefactorScaling"] == "false" else fields["Statistics/Scalefactor Scaling"].replace(" ", "")
        if actual["Scalefac"] != scaling:
            problems.append(f"EncSpot: Scalefac {actual['Scalefac']}, the manifest {scaling}")
        if actual["Frames"] != str(frames_count):
            problems.append(f"EncSpot: {actual['Frames']} frames, the manifest {frames_count}")
        for key, value in fields.items():
            parts = key.split("/")
            if len(parts) == 3 and parts[1] == "Bitrate Distribution":
                count = count_of(value)
                share = actual["bitrates"].get(int(parts[2].split()[0]))
                if share is None or not stream_stats.agrees(share, stream_stats.Fraction(count * 100, frames_count), stream_stats.Fraction(1, 10)):
                    problems.append(f"EncSpot: {parts[2]} share {share}%, the manifest {value}")
        # Its mode is the skipped frame's, which ffmpeg's VBR header frame gives as stereo in a joint stereo stream.
        modes = [k.split("/")[-1] for k in fields if k.startswith("Statistics/Channel Modes/")]
        if len(modes) == 1 and ENCSPOT_MODES[encspot_header(data, skipped)[6]] == modes[0].lower() and actual["Mode"] != modes[0].lower():
            problems.append(f"EncSpot: mode {actual['Mode']}, the manifest {modes[0]}")

    if "encoder" in manifest and actual["Encoder"] != "Unknown":
        ours = manifest["detected_encoder"]["name"] if manifest["detected_encoder"] else None
        theirs = actual["Encoder"]
        known = ENCSPOT_ENCODER_DIFFERENCES.get(path.stem)
        if encspot_names_same(theirs, ours):
            if known is not None:
                problems.append(f"EncSpot names {theirs!r} as the analyzer names {ours!r}; drop it from ENCSPOT_ENCODER_DIFFERENCES")
        elif known is None or known[0] != theirs:
            problems.append(f"EncSpot names the encoder {theirs!r}, the analyzer {ours!r}, which ENCSPOT_ENCODER_DIFFERENCES does not list")
    how = None if direct else "compared with its emulation, as it reads other frames than the statistics"
    return problems, how


def check(path, manifest, tools):
    dumper, decoder = tools
    problems = []
    if manifest.get("silent"):
        problems += decodes_to_silence(path, manifest)
    if manifest.get("frames") and "scale_info" in manifest["frames"][0]:
        problems += check_scale_info(path, manifest, dumper)
    if "mctoolamed" in manifest:
        errors, silent, channels = mctoolamed_tool.decode(decoder, path)
        if errors != manifest["mctoolamed"]["crc_errors"] or not silent:
            problems.append(f"mctoolamed reports {errors} multichannel CRC errors, silent {silent}, {channels} channels")
        speakers = {len(s["channel_layout"]) for s in manifest.get("streams", []) if "channel_layout" in s}
        if "channels" in manifest["mctoolamed"]:
            speakers = {manifest["mctoolamed"]["channels"]}
        if speakers and speakers != {channels}:
            problems.append(f"mctoolamed writes {channels} channels, the manifest expects {sorted(speakers)}")
    if manifest.get("crosscheck") is True and manifest.get("streams") and "channel_layout" in manifest["streams"][0]:
        # A multichannel stream's base frames are stereo. Where the channel mode changes, ffprobe 8.0.1 reports the last mode and 6.1.1 reported the first. The
        # 8.0.1 side is in the source: the parser sets the layout from every header it takes (mpegaudio_parser.c:86-88, av_channel_layout_default), and
        # avformat_find_stream_info copies the codec context to the stream's parameters (demux.c, avcodec_parameters_from_context). That 6.1.1 reported the
        # first mode was observed, not traced in its source. Seen on channel-mode-change.mp3, built from source.
        base = min(len(manifest["streams"][0]["channel_layout"]), 2)
        last_mode = manifest["frames"][-1]["fields"].get("Header/Channel Mode") if manifest.get("frames") else None
        if last_mode is not None:
            base = 1 if last_mode == "Mono" else 2
        if int(ffprobe_stream(path)["channels"]) != base:
            problems.append(f"ffprobe finds {ffprobe_stream(path)['channels']} channels, the manifest's last frame implies {base}")
    if "mp3guessenc_multichannel" in manifest and guessenc_multichannel(path) != manifest["mp3guessenc_multichannel"]:
        problems.append(f"mp3guessenc reports {guessenc_multichannel(path)}, the manifest {manifest['mp3guessenc_multichannel']}")
    if manifest.get("frames") and "side_info" in manifest["frames"][0]:
        problems += check_side_info(path, manifest, dumper)
    if "minimp3_side_info" in manifest and minimp3_tool.side_info(dumper, path) != manifest["minimp3_side_info"]:
        problems.append("minimp3 reads different side information than the manifest recorded")
    if "vbr_header" in manifest:
        problems += check_vbr_header(path, manifest)
        problems += check_getid3(path, manifest)
    if "regions" in manifest:
        problems += check_statistics(path, manifest)
    if "mp3guessenc" in manifest:
        expected = dict(manifest["mp3guessenc"])
        expected["mode_extensions"] = {k: v for k, v in expected["mode_extensions"].items() if k != "None"}
        actual = guessenc_summary(path)
        if actual != expected:
            problems.append(f"mp3guessenc reports {json.dumps(actual)}, the manifest {json.dumps(expected)}")
    if "mp3guessenc_guess" in manifest and guessenc_guess(path) != manifest["mp3guessenc_guess"]:
        problems.append(f"mp3guessenc guesses {guessenc_guess(path)!r}, the manifest recorded {manifest['mp3guessenc_guess']!r}")
    if guessenc_ofl(path) != manifest.get("mp3guessenc_ofl"):
        problems.append(f"mp3guessenc reads the OFL block as {guessenc_ofl(path)}, the manifest recorded {manifest.get('mp3guessenc_ofl')}")
    for tool, compare in (("mp3guessenc", guessenc_coding_problems), ("EncSpot", encspot_problems)):
        found, how = compare(path, manifest)
        problems += found
        COMPARED[tool][how or "compared with the statistics"] += 1
    if not manifest.get("crosscheck"):
        return problems

    if manifest["crosscheck"] in ("madplay", "mpg123"):
        if manifest["crosscheck"] == "madplay":
            count = madplay_frame_count(path)
        else:
            count = mpg123_frame_count(path, *frame_shape(manifest["frames"][0]["fields"]))
        # Both skip a VBR header in the first frame, as ffmpeg does.
        expected = len(manifest["frames"]) - (1 if ffprobe_skips_first(manifest) else 0)
        if count != expected:
            problems.append(f"{manifest['crosscheck']} decodes {count} frames, the manifest lists {expected} audio frames")
        return problems

    if "frames" in manifest:
        expected = [(f["offset"], f["length"]) for f in manifest["frames"] if not f.get("truncated")]
        if ffprobe_skips_first(manifest):
            expected = expected[1:]
            # ffmpeg skips delay + 529 samples when decoding, so frames it skips whole are not listed.
            delay = ffmpeg_delay(manifest["vbr_header"])
            if delay is not None:
                samples, _ = frame_shape(manifest["frames"][0]["fields"])
                expected = expected[(delay + 529) // samples :]
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

    if "decoded_onset" in manifest and decoded_onset(path) != manifest["decoded_onset"]:
        problems.append(f"ffmpeg's output starts at sample {decoded_onset(path)}, the manifest recorded {manifest['decoded_onset']}")
    if "decoded_onset" in manifest:
        # The tone comes out within a few samples of the delay the OFL block stores, as the filter bank
        # spreads it; a delay without the decoder's 529 samples would be far off.
        delay = manifest["mp3guessenc_ofl"]["delay"] - (576 if manifest.get("enhancement") == "MP3 Surround" else 0)
        if abs(manifest["decoded_onset"] - delay) > 16:
            problems.append(f"ffmpeg's output starts at sample {manifest['decoded_onset']}, not at the OFL delay {delay}")
    if "mp3guessenc_stream" in manifest:
        # Through JSON, so the histogram's tuples compare as the manifest's lists.
        actual = json.loads(json.dumps(guessenc_stream(path)))
        if actual != manifest["mp3guessenc_stream"]:
            problems.append(f"mp3guessenc's stream figures {actual} differ from the ones recorded in the manifest")
    if "decoded_samples" in manifest:
        settings = manifest["encoder"]
        actual = decoded_samples(path, CHANNELS.get(settings["channel_mode"], 2))
        if actual != manifest["decoded_samples"]:
            problems.append(f"ffmpeg and mpg123 decode {actual} samples, the manifest recorded {manifest['decoded_samples']}")
    if "encoder" in manifest:
        settings = manifest["encoder"]
        if settings.get("free_format"):
            count = mpg123_frame_count(path, 1152, CHANNELS.get(settings["channel_mode"], 2))
            if count != manifest["mpg123_frames"]:
                problems.append(f"mpg123 decodes {count} frames, the manifest recorded {manifest['mpg123_frames']}")
        else:
            if ffprobe_packets(path) != [tuple(f) for f in manifest["ffprobe_packets"]]:
                problems.append("ffprobe's packets differ from the ones recorded in the manifest")
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

# MediaInfo (mediainfo_tool.py) reads MPEG audio in File_Mpega.cpp: it syncs where frame headers
# chain and parses 128 frames (Frame_Count_Valid at the default ParseSpeed of 0.5); where the 128th
# lies in the file's first half, it jumps to 16 KiB before the end (GoToFromEnd) and parses the
# frames from there, otherwise it parses on. It reports the format, sample rate, channels, mode,
# mode extension and emphasis of the last frame it parsed. A Xing, Info or VBRI (version 1) header in
# one of the first three frames gives the frame count and the stream's bytes (the byte count less
# the header's frame), a LAME tag the encoder; a LAME tag of revision 0 to 2 with a CBR method drops
# the frame count (Header_Encoders_Lame). Without a frame count, a constant bitrate's frames are the
# stream's bytes over the frame length without padding, the bytes coming from the file
# (Streams_Finish). Bitrates near a standard one are rounded to it unless the mode is already VBR
# (File__Analyze_Streams.cpp, Audio_BitRate_Rounding), and float32_int32s rounds half up.
MEDIAINFO_FRAMES = 128
MEDIAINFO_MODES = {"Joint stereo": "Joint stereo", "Dual channel": "Dual mono"}
# File_Mpega.cpp, Mpega_Codec_Profile_Extension: Layer III's names, which it gives whatever the layer.
MEDIAINFO_EXTENSION_NAMES = [None, "Intensity Stereo", "MS Stereo", "Intensity Stereo + MS Stereo"]
MEDIAINFO_EXTENSIONS = {"Intensity stereo": "Intensity Stereo", "Mid/side stereo": "MS Stereo", "Mid/side and intensity stereo": "Intensity Stereo + MS Stereo"}
MEDIAINFO_EMPHASIS = {"50/15 µs": "50/15ms", "Reserved": "Reserved", "CCITT J.17": "CCITT"}
MEDIAINFO_STANDARD_RATES = [(7500, 8500, 8000), (15000, 17000, 16000), (23000, 25000, 24000), (31000, 33000, 32000), (38000, 42000, 40000),
                            (46000, 50000, 48000), (54000, 58000, 56000), (62720, 65280, 64000), (78400, 81600, 80000), (94080, 97920, 96000),
                            (109760, 114240, 112000), (125440, 130560, 128000), (156800, 163200, 160000), (188160, 195840, 192000),
                            (219520, 228480, 224000), (250880, 261120, 256000), (282240, 293760, 288000), (313600, 326400, 320000),
                            (344960, 359040, 352000), (376320, 391680, 384000), (407680, 424320, 416000), (439040, 456960, 448000)]
# MediaInfo does not sync on free format, whose bitrate index 0 it takes for a false start
# (File_Mpega.cpp, Synchronize), so it must find no MPEG audio in a free-format file.
MEDIAINFO_FILE_BYTES = ("without a byte count from a VBR header, MediaInfo takes the stream's bytes from the file size less the tags (File_Mpega.cpp, "
                        "Streams_Finish: VBR_FileSize), so it counts bytes between and around the frames that the analyzer does not take as frames, "
                        "and derives its frame count from them")
MEDIAINFO_END_TAGS = ("observed, not found in the source: without a byte count from a VBR header, MediaInfo's bytes of the stream come out as "
                      "the file size less the tags at the start and twice the tags at the end, checked to the byte here, where "
                      "File_Mpega.cpp, Streams_Finish subtracts File_EndTagSize once; so they fall short by the end tags' size, and its "
                      "frame count with them. Measured on external-blade-mono's 16,128 bytes of frames: with a 128-byte ID3v1 tag it gives 16,000, "
                      "with a 79-byte APE tag with a header 16,049, with both 15,921; with a 47-byte APE tag without a header, which "
                      "Tag/File__Tags.cpp sizes 32 bytes larger, 16,128, and with that and ID3v1 16,049, which this rule does not fit")
MEDIAINFO_LONE = ("MediaInfo takes a single frame for a stream where it ends exactly at the end of the file (File_Mpega.cpp, Synchronize), "
                  "where the analyzer needs a chain of frames")
# Fixtures on which MediaInfo reads a field differently, with the evidence.
MEDIAINFO_READS_OTHERWISE = {
    "mpeg/lone-frame-overflow": {"format": MEDIAINFO_LONE},
    "mpeg/lone-frames-forbidden-allocation": {"format": MEDIAINFO_LONE},
    "layer3/lone-frame-invalid-side-info": {"format": MEDIAINFO_LONE},
    "mpeg/tagged-stream": {"stream size": "MediaInfo sizes the 52-byte APE tag, which has no header, as 84 bytes (Tag/File__Tags.cpp, DetectBeginOfEndTags_Test: ApeTag_Size+=32 for version 2000, whatever the header flag; its trace: \"ApeTag (84 bytes)\"), and its byte count falls those 84 bytes short of the frames' 3343"},
    "vbr/lame-before-joined-frames": dict.fromkeys(["bitrate mode", "samples", "bitrate"], "MediaInfo reports the two streams as one (File_Mpega.cpp): their frames' two bitrates without a VBR header in the first frames make it VBR (Streams_Fill, BitRate_Count), so it gives no bitrate or sample count, and it counts the frames of both"),
    "mpeg/channel-mode-change": {"stream's mode": "the channel mode changes within the stream (mpeg.channel-mode-changed): MediaInfo gives the last frame's, mono, and the analyzer the stream's first audio frame's, joint stereo"},
    "vbr/xing-in-layer2": {"bitrate mode": "MediaInfo reads \"Xing\" as a VBR header in any layer (File_Mpega.cpp, Header_Xing), where Layer II frames carry none and the bytes are ancillary data"},
    "vbr/xing-truncated": dict.fromkeys(["frames", "samples", "bitrate", "stream size"], "MediaInfo drops a Xing header whose announced fields do not fit its frame (File_Mpega.cpp, Header_Xing: \"Error tag size\"), the counts before the TOC too, and counts its frame as audio"),
}


def float32(value):
    return struct.unpack("f", struct.pack("f", value))[0]


def rounded_bitrate(value):
    """File__Analyze_Streams.cpp, Audio_BitRate_Rounding, for MPEG audio."""
    return next((standard for low, high, standard in MEDIAINFO_STANDARD_RATES if low <= value <= high), value)


def mpeg_frames(report):
    """The analyzer's frames in file order, each as (node, header fields)."""
    found = []
    for node in mi.walk(report["root"]):
        header = mi.child(node, "Header") if node.get("kind") == "Frame" else None
        if header is not None and mi.value(header, "Layer"):
            found.append((node, {c["name"]: c.get("value") for c in header["children"]}))
    return found


def samples_per_frame(header):
    """ISO/IEC 11172-3 and 13818-3: 384 samples in Layer I, 1152 in Layer II and in MPEG-1 Layer III, 576 in MPEG-2 and 2.5 Layer III."""
    return 384 if header["Layer"] == "I" else 576 if header["Layer"] == "III" and header["Version"] != "MPEG-1" else 1152


def unpadded_length(header):
    """The frame length without padding, as MediaInfo divides by it (File_Mpega.cpp, Streams_Finish)."""
    slots = 4 if header["Layer"] == "I" else 1
    per_second = samples_per_frame(header) // 8 // slots
    return per_second * header["Bitrate"] * 1000 / header["Sample Rate"] * slots


def nominal_length(header):
    if not isinstance(header["Bitrate"], int) or not header["Sample Rate"]:
        return None
    padding = (4 if header["Layer"] == "I" else 1) if header["HasPadding"] else 0
    return int(unpadded_length(header)) // (4 if header["Layer"] == "I" else 1) * (4 if header["Layer"] == "I" else 1) + padding


def lame_encoder(frame, data):
    """The LAME tag's encoder string and VBR method where MediaInfo takes it as one (File_Mpega.cpp,
    Header_Encoders_Lame: "LAME" and a version from 3.90 on, "LAMEH" or "L3.99"), else None."""
    tag = mi.child(frame, "LAME Tag")
    if tag is None:
        return None
    start = tag["offset"]
    text = data[start:start + 9]
    accepted = (text[:4] == b"LAME" and text[5:6] == b"." and (text[4:5] > b"3" or text[4:6] == b"3." and (text[6:7] == b"9" or b"0" <= text[8:9] <= b"9"))
                or text[:5] == b"LAMEH" or text[:4] == b"L3.9" and text[4:5] == b"9")
    # MediaInfo's strings end at a NUL byte. It takes the VBR method only from a tag of revision 0
    # to 2 ((Flags&0xF0)<=0x20); None stands for no method.
    flags = data[start + 9]
    return (text.split(b"\0")[0].decode("latin-1"), flags & 0x0F if flags & 0xF0 <= 0x20 else None) if accepted else None


def check_mediainfo(path, tally):
    """MediaInfo must report the version, layer, sample rate, channels, mode, mode extension and
    emphasis of the last frame it reads, the frames, samples, bitrate, bitrate mode and stream size
    that a VBR header or the stream's bytes give, the LAME tag's encoder, and the tags' title, artist
    and album."""
    problems = tally.start(f"{path.parent.name}/{path.stem}")
    if mi.golden_hides(path):
        # The streams-limited fixture: MediaInfo's frame count and last header depend on where it resynchronizes after each of 65 changes of the sample rate
        # (File_Mpega.cpp reads 128 frames and then the last 16 KiB; measured with 24.01: 44100 Hz and 207 frames for 198), and the comparison needs the
        # frames' headers, which its golden report leaves out.
        tally.skip("all fields", "the sample rate changes every three frames, and the golden report has no frames (golden_hides)")
        return problems
    report = mi.golden(path)
    info = tally.read(path)
    general, audio = (info["General"], info["Audio"]) if info else ({}, [])
    frames = mpeg_frames(report)
    codes = {f["code"] for f in report["findings"]}
    track = next((t for t in audio if t.get("Format") == "MPEG Audio"), None)
    if not frames:
        tally.compare("format", track and track.get("Format"), None)
        mi.compare_tags(tally, general, report)
        return problems
    if all(h["Bitrate"] == "free" for _, h in frames):
        tally.compare("format of free format", track and track.get("Format"), None)
        return problems
    tally.compare("format", track and track.get("Format"), "MPEG Audio")
    if track is None:
        return problems
    data = path.read_bytes()
    fixed = [(n, h) for n, h in frames if h["Bitrate"] != "free"]
    read = fixed
    if len(fixed) > MEDIAINFO_FRAMES and fixed[MEDIAINFO_FRAMES - 1][0]["offset"] < len(data) / 2:
        read = fixed[:MEDIAINFO_FRAMES] + [(n, h) for n, h in fixed[MEDIAINFO_FRAMES:] if n["offset"] >= len(data) - 16 * 1024]
    complete = [(n, h) for n, h in read if n["length"] == nominal_length(h)] or read
    last = complete[-1][1]
    tally.compare("version and layer", (track.get("Format_Version"), track.get("Format_Profile")),
                  (last["Version"].removeprefix("MPEG-"), "Layer " + {"I": "1", "II": "2", "III": "3"}[last["Layer"]]))
    tally.compare("sample rate", track.get("SamplingRate"), str(last["Sample Rate"]))
    tally.compare("channels", track.get("Channels"), "1" if last["Channel Mode"] == "Mono" else "2")
    if any(len(s.get("channelLayout") or []) > 2 for s in report["streams"]):
        # MediaInfo does not read the ISO/IEC 13818-3 multichannel extension (File_Mpega.cpp has
        # no mc_header), so it gives the base frames' two channels.
        tally.compare("channels of a multichannel stream", track.get("Channels"), "2")
    tally.compare("mode", track.get("Format_Settings_Mode"), MEDIAINFO_MODES.get(last["Channel Mode"]))
    if len(report["streams"]) == 1:
        # The stream's mode, which a VBR header frame must not decide.
        tally.compare("stream's mode", track.get("Format_Settings_Mode"), MEDIAINFO_MODES.get(report["streams"][0].get("channelMode")))
    else:
        tally.skip("stream's mode", "several streams")
    if last["Layer"] == "III":
        tally.compare("mode extension", track.get("Format_Settings_ModeExtension"), MEDIAINFO_EXTENSIONS.get(last.get("Mode Extension")))
    else:
        # MediaInfo names a Layer I or II bound with Layer III's stereo tools (File_Mpega.cpp,
        # Mpega_Codec_Profile_Extension), from the bits of the last frame.
        bits = (data[complete[-1][0]["offset"] + 3] >> 4) & 3
        tally.compare("Layer I and II mode extension bits", track.get("Format_Settings_ModeExtension"), MEDIAINFO_EXTENSION_NAMES[bits])
    tally.compare("emphasis", track.get("Format_Settings_Emphasis"), MEDIAINFO_EMPHASIS.get(last.get("Emphasis")))

    first = fixed[0][0]
    xing = mi.child(first, "Xing")
    vbri = mi.child(first, "VBRI") if mi.value(first, "VBRI", "Version") == 1 else None
    header = xing or vbri
    lame = lame_encoder(first, data) if xing else None
    reported_bytes = None
    claimed = mi.value(header, "Frame Count") if header else None
    constant_method = lame is not None and lame[1] in (1, 8)
    byte_count = mi.value(header, "Byte Count") if header else None
    if xing and byte_count and byte_count > first["length"]:
        stream_bytes = byte_count - first["length"]
    elif vbri and byte_count:
        stream_bytes = byte_count
    else:
        # Without a byte count, the bytes of the complete frames, the header's frame among them
        # (Streams_Finish); a frame cut short is reported by the analyzer with a finding.
        stream_bytes = sum(n["length"] for n, h in fixed if n["length"] == nominal_length(h))
        # MediaInfo takes them from the file instead; where that makes a difference, it must be
        # one of the two its source explains, checked to the byte.
        tags = [c for c in report["root"]["children"] if c.get("kind") == "Tag"]
        end_tags = sum(c["length"] for c in tags if c["offset"] > first["offset"])
        file_bytes = len(data) - sum(c["length"] for c in tags)
        reported = int(track.get("StreamSize") or -1)
        if reported != stream_bytes and reported in (file_bytes, file_bytes - end_tags) and (reported == file_bytes or end_tags):
            reason = MEDIAINFO_FILE_BYTES if reported == file_bytes else MEDIAINFO_END_TAGS
            tally.skip("stream size", reason)
            stream_bytes = None
            # Its frame count then follows from its own byte count, which is compared below.
            reported_bytes = reported
    if claimed and not constant_method:
        frames_count = claimed
        mode = "CBR" if xing and mi.value(first, "Xing") == "Info" else "VBR"
        bitrate = math.floor(float32(float32(stream_bytes / claimed) * last["Sample Rate"] / (samples_per_frame(last) // 8)) + 0.5)
    else:
        constant = constant_method or len({h["Bitrate"] for _, h in read}) == 1
        mode = "CBR" if constant else "VBR"
        frames_count = math.floor((stream_bytes or reported_bytes) / unpadded_length(last) + 0.5) if constant else None
        bitrate = last["Bitrate"] * 1000 if constant else None
    if frames_count is not None and (not header or (xing and mi.value(first, "Xing") == "Info")):
        # MediaInfo 26.01 counts the frames it synchronizes on: FrameInfo.PTS runs from 0 at the first, whatever it holds, and the frame count and duration
        # come from that run (File_Mpega.cpp, Synchronize: "Frame_Count == 0 && !IsSub", and Streams_Finish), so a frame cut short counts and so does an Info
        # header's frame, and neither the header's frame count (24.01 took it, less its own frame) nor the stream's bytes (24.01 divided them by the frame
        # length) decides.
        frames_count = len(fixed)
    tally.compare("bitrate mode", track.get("BitRate_Mode"), mode)
    if frames_count is None:
        # Without a VBR header, File_Mpega.cpp gives a variable bitrate no frame count, sample count
        # or average (that only by its risky estimate, off by default: Streams_Fill); the generic
        # Streams_Finish_StreamOnly_Audio (File__Analyze_Streams_Finish.cpp) then counts the frames
        # it parsed, which leaves out a frame shorter than its header says ("Partial frame" in
        # File_Mpega.cpp, Data_Parse).
        parsed = sum(1 for n, h in read if n["length"] == nominal_length(h))
        tally.compare("frames parsed of a variable bitrate without a VBR header", track.get("FrameCount"), str(parsed))
        # 26.01 fills them from the duration its PTS run gives (File_Mpega.cpp, Streams_Finish: Audio_Duration from FrameInfo.PTS, to the millisecond), the
        # samples at the sample rate and the bitrate over the stream's bytes; 24.01 gave none.
        milliseconds = round(sum(samples_per_frame(h) * 1000 / h["Sample Rate"] for n, h in read if n["length"] == nominal_length(h)))
        tally.compare("samples and bitrate of a variable bitrate without a VBR header", (track.get("SamplingCount"), track.get("BitRate")),
                      (str(round(milliseconds * last["Sample Rate"] / 1000)), str(round((stream_bytes if stream_bytes is not None else reported_bytes) * 8000 / milliseconds))))
    else:
        tally.compare("frames", track.get("FrameCount"), str(frames_count))
        if last["Layer"] == "I" and last["Version"] != "MPEG-1":
            # MediaInfo multiplies the frames of MPEG-2 and 2.5 Layer I by 1152 samples (File_Mpega.cpp,
            # Streams_Finish), where ISO/IEC 13818-3 gives Layer I 384.
            tally.compare("samples of MPEG-2 and 2.5 Layer I", track.get("SamplingCount"), str(frames_count * 1152))
        else:
            tally.compare("samples", track.get("SamplingCount"), str(frames_count * samples_per_frame(last)))
    if bitrate is not None:
        # Audio_BitRate_Rounding keeps the exact value where the mode is already VBR, which a
        # LAME tag's VBR or ABR method sets before the bitrate (Header_Encoders_Lame, Lame_BitRate_Mode).
        exact = lame is not None and lame[1] in (2, 3, 4, 5, 6, 9)
        tally.compare("bitrate", track.get("BitRate"), str(bitrate if exact else rounded_bitrate(bitrate)))
    if stream_bytes is not None:
        tally.compare("stream size", track.get("StreamSize"), str(stream_bytes))

    # A TSSE frame's encoder goes to the general track only.
    library = mi.text(track.get("Encoded_Library") or general.get("Encoded_Library"))
    if lame is not None:
        tally.compare("encoder", library, lame[0])
    elif library is None:
        # MediaInfo guesses no encoder from the coding (File_Mpega.cpp, Encoded_Library_Guess is
        # commented out), so it names none without a LAME tag, an encoder string or an ID3v2 TSSE.
        # It ignores ID3v2.2's TSS (File_Id3v2.cpp, Fill_Name: "case Elements::TSS  : break;").
        texts = [n.get("value") for n in mi.walk(report["root"]) if n["name"] == "TSSE"]
        if texts or any(m in data for m in (b"LAME", b"GOGO", b"MPGE")):
            tally.compare("encoder", library, texts[0] if texts else "an encoder string")
        else:
            tally.compare("encoder without a LAME tag, encoder string or TSSE", library, None)
    else:
        # Without a LAME tag it takes, MediaInfo keeps 20 bytes after the first "LAME" in the
        # frames, or names Gogo for "GOGO" or "MPGE" (Header_Encoders), or takes an ID3v2 TSSE.
        encoder = report["streams"][0].get("encoder") or ""
        texts = [n.get("value") for n in mi.walk(report["root"]) if n["name"] in ("TSSE", "TSS")]
        family = "LAME" if library.startswith(("LAME", "L3.9")) else "Gogo" if library.startswith("Gogo") else None
        if family is not None:
            tally.compare("encoder family", family if encoder.startswith(family) else encoder, family)
        else:
            tally.compare("encoder from TSSE", library, library if library in texts else texts)
    mi.compare_tags(tally, general, report)
    return problems


# How each fixture's coding statistics were compared with mp3guessenc and EncSpot, or why not.
COMPARED = {"mp3guessenc": collections.Counter(), "EncSpot": collections.Counter()}


def main():
    failed = 0
    checked = 0
    mi.require()
    tool_versions.require("ffmpeg", "ffprobe", "mpg123", "madplay", "php")
    tally = mi.Tally(MEDIAINFO_READS_OTHERWISE)
    tools = (minimp3_tool.build(), mctoolamed_tool.build())
    for manifest_path in sorted(p for directory in DIRECTORIES for p in directory.glob("*.expected.json")):
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        name = manifest_path.name[: -len(".expected.json")]
        path = next(p for p in manifest_path.parent.glob(name + ".*") if not p.name.endswith(".json"))
        problems = check(path, manifest, tools) + check_mediainfo(path, tally)
        checked += 1
        for problem in problems:
            failed += 1
            print(f"{path.name}: {problem}")
    # The tag fixtures are MPEG audio files too; MediaInfo reads their tags where it finds the audio.
    for manifest_path in sorted(TAGS.glob("*.expected.json")):
        name = manifest_path.name[: -len(".expected.json")]
        path = next(p for p in manifest_path.parent.glob(name + ".*") if not p.name.endswith(".json"))
        for problem in check_mediainfo(path, tally):
            failed += 1
            print(f"{path.name}: {problem}")
    for problem in tally.unused():
        failed += 1
        print(problem)
    tally.print()
    for tool, counts in COMPARED.items():
        for how, count in sorted(counts.items(), key=lambda c: (-c[1], c[0])):
            print(f"{tool}: {count} fixtures, {how}", file=sys.stderr)
    print(f"Cross-checked {checked} fixtures, {failed} problems.", file=sys.stderr)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
