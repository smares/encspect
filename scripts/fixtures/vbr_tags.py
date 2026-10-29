"""Reads and writes the Xing/Info, LAME and VBRI headers in the first frame of an MPEG Layer III stream.

The layouts follow the LAME 3.100 source (libmp3lame/VbrTag.c, which writes Xing/Info and the
LAME tag), Gabriel Bouvigne's Mp3 Info Tag revision 1 specification (the field meanings), and
ffmpeg 6.1 (libavformat/mp3dec.c, the only common reader of VBRI). mp3guessenc, mpg123 and
ffprobe confirm what this module reads; see crosscheck_mpeg_fixtures.py.

- Xing or Info starts after the side information, counted as if the frame had no CRC: 36 bytes
  into an MPEG-1 stereo frame, 21 into MPEG-1 mono and MPEG-2/2.5 stereo, 13 into MPEG-2/2.5
  mono. LAME writes "Info" for CBR and "Xing" otherwise. A flags word says which of frame count,
  byte count, 100-byte TOC and quality follow, in that order.
- The LAME tag follows the Xing fields: 36 bytes, from a 9-byte encoder string to a CRC-16 of
  every byte of the frame before that CRC. Versions before 3.90 wrote only a version string.
- VBRI starts 36 bytes into the frame whatever the mode: version, delay, quality, byte and frame
  counts, then a TOC of entry_count entries of entry_size bytes, each a byte count to multiply
  by the scale.

All values are kept raw here; report_fields() formats them the way the analyzer reports them.
"""

from units import bytes_text
import re
import struct

XING_OFFSETS = {(True, False): 36, (True, True): 21, (False, False): 21, (False, True): 13}
VBRI_OFFSET = 36
LAME_TAG_SIZE = 36

METHODS = {0: "Unknown", 1: "CBR", 2: "ABR", 3: "VBR method 1 (rh)", 4: "VBR method 2 (mtrh)", 5: "VBR method 3 (mt)",
           6: "VBR method 4", 8: "CBR, two-pass", 9: "ABR, two-pass"}
GAIN_NAMES = {0: "Not set", 1: "Radio", 2: "Audiophile"}
GAIN_ORIGINATORS = {0: "Not set", 1: "Set by artist", 2: "Set by user", 3: "Determined automatically", 4: "Simple RMS average"}
STEREO_MODES = ["Mono", "Stereo", "Dual channel", "Joint stereo", "Forced mid/side stereo", "Auto", "Intensity stereo", "Undefined"]
SOURCE_RATES = ["32 kHz or lower", "44.1 kHz", "48 kHz", "Higher than 48 kHz"]
SURROUND = {0: "None", 1: "DPL", 2: "DPL2", 3: "Ambisonic"}
NAMED_PRESETS = {1000: "R3mix", 1001: "Standard", 1002: "Extreme", 1003: "Insane", 1004: "Standard fast", 1005: "Extreme fast",
                 1006: "Medium", 1007: "Medium fast"}


def crc16(data, crc=0):
    """CRC-16 with polynomial 0x8005, reflected, as LAME computes the tag and music CRCs."""
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def is_mpeg1(header):
    return (header[1] >> 3) & 3 == 3


def is_mono(header):
    return header[3] >> 6 == 3


def xing_offset(header):
    return XING_OFFSETS[(is_mpeg1(header), is_mono(header))]


def is_old_lame(text):
    """LAME before 3.90 wrote only a version string, such as "LAME3.88 (beta)"."""
    if not text.startswith("LAME3.") or len(text) < 8 or not text[6:8].isdigit():
        return False
    return text[6] < "9" and (len(text) == 8 or not text[8].isdigit())


def is_lame_tag_start(text):
    """The encoder strings readers take a LAME tag after: "LAME" (Helix's "LAMEH" too) and "L" with a
    version, as LAME 3.99 alphas wrote it ("L3.99a12"), which mp3guessenc accepts (tags.c,
    parse_xing_tag), and ffmpeg's "Lavc" and "Lavf", the only ones ffmpeg reads delay and padding
    after (mp3dec.c, mp3_parse_info_tag)."""
    return text.startswith(("LAME", "Lavc", "Lavf")) or re.match(r"L\d\.\d\d", text) is not None


def printable(data):
    return all(32 <= b < 127 or b == 0 for b in data) and data[:1] != b"\0"


# --- Reading ----------------------------------------------------------------------------------


def read(frame, music=None):
    """The VBR header in frame (the frame's bytes) as raw values, or None. music is everything
    after the frame up to the end of the stream, for the music CRC."""
    header = frame[:4]
    if (header[1] >> 1) & 3 != 1:
        return None
    if frame[VBRI_OFFSET : VBRI_OFFSET + 4] == b"VBRI":
        return read_vbri(frame)
    offset = xing_offset(header)
    if frame[offset : offset + 4] in (b"Xing", b"Info"):
        return read_xing(frame, offset, music)
    return None


def read_xing(frame, offset, music):
    tag = {"type": frame[offset : offset + 4].decode(), "offset": offset, "truncated": False}
    position = offset + 4

    def take(size):
        nonlocal position
        if position + size > len(frame):
            tag["truncated"] = True
            return None
        data = frame[position : position + size]
        position += size
        return data

    flags = take(4)
    tag["flags"] = struct.unpack(">I", flags)[0] if flags else None
    for bit, key, size in [(1, "frames", 4), (2, "bytes", 4), (4, "toc", 100), (8, "quality", 4)]:
        if tag["flags"] is None or not tag["flags"] & bit or tag["truncated"]:
            tag[key] = None
            continue
        data = take(size)
        tag[key] = None if data is None else list(data) if key == "toc" else struct.unpack(">I", data)[0]
    if tag["truncated"]:
        return tag

    # What follows the Xing fields, raw: decoders read their delay from it whatever it holds.
    tag["after_fields"] = frame[position : position + LAME_TAG_SIZE].hex()
    string = frame[position : position + 9]
    if len(string) == 9 and printable(string):
        text = string.rstrip(b"\0").decode("ascii")
        # Text that does not start a LAME tag is kept as a string, as LAME before 3.90 wrote its version.
        if is_old_lame(text) or not is_lame_tag_start(text):
            end = position
            while end < len(frame) and end - position < 20 and 32 <= frame[end] < 127:
                end += 1
            tag["encoder_string"] = frame[position:end].decode("ascii")
        elif position + LAME_TAG_SIZE <= len(frame):
            tag["lame"] = read_lame(frame, position, music)
    return tag


def read_lame(frame, start, music):
    b = frame[start : start + LAME_TAG_SIZE]
    lame = {
        "offset": start,
        "encoder": b[0:9].rstrip(b"\0").decode("ascii"),
        "revision": b[9] >> 4,
        "method": b[9] & 15,
        "lowpass": b[10],
        "peak": struct.unpack(">I", b[11:15])[0],
        "radio_gain": struct.unpack(">H", b[15:17])[0],
        "audiophile_gain": struct.unpack(">H", b[17:19])[0],
        "flags": b[19],
        "bitrate": b[20],
        "delay": (b[21] << 4) | (b[22] >> 4),
        "padding": ((b[22] & 15) << 8) | b[23],
        "misc": b[24],
        # A sign bit and seven bits of 1.5 dB steps (Mp3 Info Tag, MP3 Gain).
        "mp3_gain": -(b[25] & 0x7F) if b[25] & 0x80 else b[25],
        "preset_surround": struct.unpack(">H", b[26:28])[0],
        "music_length": struct.unpack(">I", b[28:32])[0],
        "music_crc": struct.unpack(">H", b[32:34])[0],
        "tag_crc": struct.unpack(">H", b[34:36])[0],
    }
    lame["tag_crc_computed"] = crc16(frame[: start + 34])
    # ffmpeg 6.1 computes the CRC over the frame's first 190 bytes, with the CRC field still zero,
    # wherever the tag starts; only in an MPEG-1 stereo frame is that the same range.
    zeroed = bytearray(frame[:190])
    if start + 34 < 190:
        zeroed[start + 34 : start + 36] = b"\0\0"
    lame["tag_crc_ffmpeg"] = crc16(bytes(zeroed))
    covered = lame["music_length"] - len(frame)
    # 0 means not set (Mp3 Info Tag, MusicLength).
    lame["music_length_short"] = covered < 0 and lame["music_length"] != 0
    if music is not None and 0 <= covered <= len(music) and lame["music_length"] != 0:
        lame["music_crc_computed"] = crc16(music[:covered])
    else:
        lame["music_crc_computed"] = None
    return lame


def read_vbri(frame):
    tag = {"type": "VBRI", "offset": VBRI_OFFSET, "truncated": False}
    fixed = frame[VBRI_OFFSET + 4 : VBRI_OFFSET + 26]
    if len(fixed) < 22:
        tag["truncated"] = True
        return tag
    (tag["version"], tag["delay"], tag["quality"], tag["bytes"], tag["frames"], tag["toc_entries"], tag["toc_scale"],
     tag["entry_size"], tag["frames_per_entry"]) = struct.unpack(">HHHIIHHHH", fixed)
    toc = []
    position = VBRI_OFFSET + 26
    # Fraunhofer's reader knows TOC entries of 1 to 4 bytes; with another size the TOC is not read.
    tag["entry_size_invalid"] = tag["toc_entries"] > 0 and not 1 <= tag["entry_size"] <= 4
    for _ in range(0 if tag["entry_size_invalid"] else tag["toc_entries"]):
        if position + tag["entry_size"] > len(frame):
            tag["truncated"] = True
            break
        toc.append(int.from_bytes(frame[position : position + tag["entry_size"]], "big"))
        position += tag["entry_size"]
    tag["toc"] = toc
    return tag


# --- Writing ----------------------------------------------------------------------------------


def xing_bytes(tag_id, frames=None, byte_count=None, toc=None, quality=None, flags=None):
    if flags is None:
        flags = (frames is not None) | (byte_count is not None) << 1 | (toc is not None) << 2 | (quality is not None) << 3
    data = tag_id.encode() + struct.pack(">I", flags)
    if frames is not None:
        data += struct.pack(">I", frames)
    if byte_count is not None:
        data += struct.pack(">I", byte_count)
    if toc is not None:
        data += bytes(toc)
    if quality is not None:
        data += struct.pack(">I", quality)
    return data


def lame_bytes(encoder="LAME3.100", revision=0, method=1, lowpass=0, peak=0, radio_gain=0, audiophile_gain=0, flags=0,
               bitrate=0, delay=576, padding=0, misc=0, mp3_gain=0, preset_surround=0, music_length=0, music_crc=0):
    """The LAME tag without its CRC, which covers the frame bytes before it."""
    return (encoder.encode().ljust(9, b"\0") + bytes([revision << 4 | method, lowpass]) + struct.pack(">IHH", peak, radio_gain, audiophile_gain)
            + bytes([flags, bitrate, delay >> 4, (delay & 15) << 4 | padding >> 8, padding & 255, misc, 0x80 | -mp3_gain if mp3_gain < 0 else mp3_gain])
            + struct.pack(">HIH", preset_surround, music_length, music_crc))


def vbri_bytes(version=1, delay=0, quality=75, byte_count=0, frames=0, toc=(), toc_scale=1, entry_size=2, frames_per_entry=1):
    data = b"VBRI" + struct.pack(">HHHIIHHHH", version, delay, quality, byte_count, frames, len(toc), toc_scale, entry_size, frames_per_entry)
    return data + b"".join(value.to_bytes(entry_size, "big") for value in toc)


def gain(name, originator, decibels):
    tenths = round(abs(decibels) * 10)
    return name << 13 | originator << 10 | (decibels < 0) << 9 | tenths


# --- Report fields ----------------------------------------------------------------------------


def gain_text(value):
    tenths = value & 0x1FF
    return f"{'-' if value & 0x200 and tenths else '+'}{tenths // 10}.{tenths % 10} dB"


def preset_text(value):
    if value == 0:
        return "None"
    if 8 <= value <= 320:
        return f"{value} kbps"
    if 410 <= value <= 500 and value % 10 == 0:
        return f"V{(500 - value) // 10}"
    return NAMED_PRESETS.get(value, f"Unknown ({value})")


def bitrate_field(method):
    if method in (2, 9):
        return "Average Bitrate"
    if 3 <= method <= 6:
        return "Minimum Bitrate"
    return "Bitrate"


def report_fields(tag, toc_entries=(0, 1, 50, 99)):
    """The analyzer's report of the tag, as node paths relative to the frame."""
    fields = {}
    if tag["type"] == "VBRI":
        if tag["truncated"] and "version" not in tag:
            return {"VBRI": None}
        fields.update({
            "VBRI/Version": str(tag["version"]),
            "VBRI/Delay": str(tag["delay"]),
            "VBRI/Quality": str(tag["quality"]),
            "VBRI/Byte Count": str(tag["bytes"]),
            "VBRI/Frame Count": str(tag["frames"]),
            "VBRI/TOC Entries": str(tag["toc_entries"]),
            "VBRI/TOC Scale": str(tag["toc_scale"]),
            "VBRI/TOC Entry Size": bytes_text(tag['entry_size']),
            "VBRI/Frames Per TOC Entry": str(tag["frames_per_entry"]),
            "VBRI/TOC": f"{len(tag['toc'])} entries",
        })
        for i in sorted({0, len(tag["toc"]) - 1}):
            if 0 <= i < len(tag["toc"]):
                fields[f"VBRI/TOC/Entry {i}"] = f"{tag['toc'][i] * tag['toc_scale']} bytes"
        return fields

    fields["Xing"] = tag["type"]
    if tag["flags"] is None:
        return fields
    fields["Xing/Flags"] = f"0x{tag['flags']:08X}"
    for bit, name in [(1, "HasFrameCount"), (2, "HasByteCount"), (4, "HasToc"), (8, "HasQuality")]:
        fields[f"Xing/Flags/{name}"] = "true" if tag["flags"] & bit else "false"
    if tag["frames"] is not None:
        fields["Xing/Frame Count"] = str(tag["frames"])
    if tag["bytes"] is not None:
        fields["Xing/Byte Count"] = str(tag["bytes"])
    if tag["toc"] is not None:
        fields["Xing/TOC"] = "100 entries"
        for i in toc_entries:
            fields[f"Xing/TOC/Entry {i}"] = str(tag["toc"][i])
    if tag["quality"] is not None:
        # Gogo-no-coda writes its name where the quality belongs.
        fields["Xing/Quality"] = ('"GOGO", which Gogo writes instead of a quality' if tag["quality"] == int.from_bytes(b"GOGO", "big")
                                  else str(tag["quality"]))
    if "encoder_string" in tag:
        fields["Encoder String"] = tag["encoder_string"]
    if "lame" in tag:
        fields.update(lame_fields(tag["lame"], tag["quality"]))
    return fields


def lame_fields(lame, quality):
    p = "LAME Tag"
    fields = {
        p: lame["encoder"],
        f"{p}/Encoder": lame["encoder"],
        f"{p}/Revision": str(lame["revision"]),
        f"{p}/VBR Method": METHODS.get(lame["method"], f"Reserved ({lame['method']})"),
        f"{p}/Lowpass": f"{lame['lowpass'] * 100} Hz" if lame["lowpass"] else "Unknown",
        f"{p}/Peak": f"{lame['peak'] / (1 << 23):.6f}" if lame["peak"] else "Unknown",
    }
    for key, name in [("radio_gain", "Radio Replay Gain"), ("audiophile_gain", "Audiophile Replay Gain")]:
        value = lame[key]
        if value == 0:
            fields[f"{p}/{name}"] = "Not set"
            continue
        fields[f"{p}/{name}"] = gain_text(value)
        fields[f"{p}/{name}/Name"] = GAIN_NAMES.get(value >> 13, f"Reserved ({value >> 13})")
        fields[f"{p}/{name}/Originator"] = GAIN_ORIGINATORS.get((value >> 10) & 7, f"Reserved ({(value >> 10) & 7})")
        # LAME before 3.95 computed ReplayGain against 83 dB, later versions against 89 dB
        # (mpg123, parse.c).
        old = re.fullmatch(r"LAME3\.(\d\d)(\D.*)?", lame["encoder"])
        if (value >> 10) & 7 == 3 and old and int(old.group(1)) < 95:
            fields[f"{p}/{name}/Reference Level"] = ("83 dB, as LAME before 3.95 computed it; later versions use 89 dB, so this "
                                                     "gain is 6 dB lower than theirs")
    flags = lame["flags"]
    fields.update({
        f"{p}/Encoding Flags/UsesNsPsytune": "true" if flags & 0x10 else "false",
        f"{p}/Encoding Flags/UsesNsSafeJoint": "true" if flags & 0x20 else "false",
        f"{p}/Encoding Flags/IsNoGapContinued": "true" if flags & 0x40 else "false",
        f"{p}/Encoding Flags/IsNoGapContinuation": "true" if flags & 0x80 else "false",
        f"{p}/ATH Type": str(flags & 15),
    })
    bitrate = lame["bitrate"]
    fields[f"{p}/{bitrate_field(lame['method'])}"] = "Unknown" if bitrate == 0 else "255 kbps or more" if bitrate == 255 else f"{bitrate} kbps"
    misc = lame["misc"]
    fields.update({
        f"{p}/Encoder Delay": f"{lame['delay']} samples",
        f"{p}/Encoder Padding": f"{lame['padding']} samples",
        f"{p}/Noise Shaping": str(misc & 3),
        f"{p}/Stereo Mode": STEREO_MODES[(misc >> 2) & 7],
        f"{p}/HasUnwiseSettings": "true" if misc & 0x20 else "false",
        f"{p}/Source Sample Rate": SOURCE_RATES[misc >> 6],
        f"{p}/MP3 Gain": f"{'-' if lame['mp3_gain'] < 0 else '+'}{abs(lame['mp3_gain']) * 3 // 2}.{abs(lame['mp3_gain']) * 15 % 10} dB",
        f"{p}/Surround": SURROUND.get((lame["preset_surround"] >> 11) & 7, f"Reserved ({(lame['preset_surround'] >> 11) & 7})"),
        f"{p}/Preset": preset_text(lame["preset_surround"] & 0x7FF),
        f"{p}/Music Length": f"{lame['music_length']} bytes" if lame["music_length"] else "Not set",
        f"{p}/Music CRC": f"0x{lame['music_crc']:04X}",
        f"{p}/Tag CRC": f"0x{lame['tag_crc']:04X}",
        f"{p}/Tag CRC/Computed": f"0x{lame['tag_crc_computed']:04X}",
    })
    if lame["music_crc_computed"] is not None:
        fields[f"{p}/Music CRC/Computed"] = f"0x{lame['music_crc_computed']:04X}"
    preset = old_preset(lame, quality)
    if preset and lame["preset_surround"] & 0x7FF == 0:
        fields[f"{p}/Preset/Settings Match"] = (f"{preset}; LAME 3.90 to 3.92 leave the preset out, but the VBR method, Xing quality, noise "
                                                "shaping, stereo mode, ATH type and lowpass this tag holds are the ones it sets")
    return fields


# The settings LAME 3.90 to 3.92 write for their presets while the tag's preset field is 0, each from a
# committed encode of the "music" signal of make_external_mpeg_fixtures.py at 44.1 kHz stereo. The key
# is the VBR method, the Xing quality, the bitrate field (None for the VBR methods 3 and 4, where it
# follows -b and not the preset), the lowpass in 100 Hz, the ATH type, the noise shaping and the stereo mode.
# The Windows build of 3.91 and HydrogenAudio's 3.90.3 write some lowpasses 100 Hz higher, and 3.90.3 noise
# shaping 1 for standard and extreme, so those have rows of their own.
OLD_PRESETS = {
    # external-lame390-alt-standard (3.90.1), external-lame392-alt-fast-standard
    (3, 78, None, 190, 4, 2, 3): "--alt-preset standard",
    # external-lame3903-alt-standard
    (3, 78, None, 190, 4, 1, 3): "--alt-preset standard",
    # external-lame390-alt-extreme (48 kHz), external-lame391w-alt-extreme
    (3, 78, None, 195, 2, 2, 3): "--alt-preset extreme",
    (3, 78, None, 196, 2, 2, 3): "--alt-preset extreme",
    # external-lame3903-alt-extreme
    (3, 78, None, 196, 2, 1, 3): "--alt-preset extreme",
    # external-lame390-alt-insane, external-lame3903-alt-insane
    (1, 58, 255, 205, 2, 1, 3): "--alt-preset insane",
    (1, 58, 255, 206, 2, 1, 3): "--alt-preset insane",
    # external-lame392-alt-fast-standard
    (4, 78, None, 190, 4, 2, 3): "--alt-preset fast standard",
    # external-lame390-alt-fast-extreme, external-lame391w-alt-fast-extreme
    (4, 78, None, 195, 2, 2, 3): "--alt-preset fast extreme",
    (4, 78, None, 196, 2, 2, 3): "--alt-preset fast extreme",
    # external-lame390-r3mix, external-lame391w-r3mix
    (4, 88, None, 195, 3, 1, 3): "--r3mix",
    (4, 88, None, 196, 3, 1, 3): "--r3mix",
    # external-lame390-preset-mw-us, external-lame391w-preset-mw-us
    (1, 65, 40, 75, 2, 1, 0): "--preset mw-us",
    (1, 65, 40, 76, 2, 1, 0): "--preset mw-us",
    # external-lame390-preset-fm, -radio, -tape, -hifi, -cd, -studio
    (1, 67, 112, 150, 2, 1, 3): "--preset fm",
    (1, 67, 128, 150, 2, 1, 3): "--preset radio",
    (1, 67, 128, 180, 2, 1, 3): "--preset tape",
    (1, 78, 160, 180, 2, 1, 3): "--preset hifi",
    (1, 88, 192, 0, 2, 1, 1): "--preset cd",
    (1, 99, 255, 0, 2, 1, 1): "--preset studio",
}
OLD_PRESET_ENCODERS = ("LAME3.90", "LAME3.90.", "LAME3.91", "LAME3.92")


def old_preset(lame, quality):
    """The preset whose settings the tag of LAME 3.90 to 3.92 holds, or None. The ABR and CBR alt-presets
    name a bitrate, so a rule stands in for a row each (external-lame390-alt-abr, external-lame390-alt-cbr)."""
    if lame["encoder"].rstrip(" ") not in OLD_PRESET_ENCODERS or quality is None:
        return None
    method, bitrate, misc = lame["method"], lame["bitrate"], lame["misc"]
    noise_shaping, stereo = misc & 3, (misc >> 2) & 7
    if method in (1, 2) and quality == 58 and noise_shaping == 2 and stereo == 3 and lame["flags"] & 15 == 2 and 1 <= bitrate <= 254:
        return f"--alt-preset {bitrate}" if method == 2 else f"--alt-preset cbr {bitrate}"
    key = (method, quality, None if method in (3, 4) else bitrate, lame["lowpass"], lame["flags"] & 15, noise_shaping, stereo)
    return OLD_PRESETS.get(key)


def findings(tag, frame_offset, cut=True):
    """The findings the analyzer reports about the tag: (code, text the message contains). cut says
    whether the stream after the tag holds less than its header counts, which explains a music
    length that runs past the end of the file."""
    result = []
    at = f"0x{frame_offset:X}"
    if tag["truncated"]:
        result.append(("mpeg.vbr-header-truncated", at))
    if tag.get("entry_size_invalid"):
        result.append(("mpeg.vbri-entry-size-invalid", f"{tag['entry_size']} bytes long"))
    if tag["type"] == "VBRI" and tag.get("version", 1) != 1:
        result.append(("mpeg.vbri-version-unknown", f"version {tag['version']}"))
    if tag["type"] != "VBRI" and tag.get("toc") and any(b < a for a, b in zip(tag["toc"], tag["toc"][1:])):
        result.append(("mpeg.xing-toc-decreasing", at))
    lame = tag.get("lame")
    if lame:
        if lame["tag_crc"] != lame["tag_crc_computed"]:
            if lame["tag_crc"] == lame["tag_crc_ffmpeg"]:
                result.append(("mpeg.lame-tag-crc-ffmpeg", at))
            else:
                result.append(("mpeg.lame-tag-crc-mismatch", at))
        if lame["music_length"] == 0:
            result.append(("mpeg.music-length-not-set", at))
        elif lame["music_length_short"]:
            result.append(("mpeg.music-length-short", f"music length of {lame['music_length']} bytes"))
        elif lame["music_crc_computed"] is None:
            result.append(("mpeg.music-crc-not-verified", at) if cut else ("mpeg.music-length-long", f"music length of {lame['music_length']} bytes"))
        elif lame["music_crc"] != lame["music_crc_computed"]:
            # With --nogap, LAME continues the music CRC from the previous file.
            result.append(("mpeg.music-crc-nogap" if lame["flags"] & 0x80 else "mpeg.music-crc-mismatch", at))
    return result


# --- LAME's version ---------------------------------------------------------------------------


def lame_release(text):
    """A LAME version as the tag (get_lame_very_short_version: "LAME3.98b", "LAME3.99r", "L3.99a12",
    "LAME3.100") or the ancillary data (get_lame_short_version after "LAME": "LAME3.98 (beta 8)",
    "LAME3.99.5") holds it, as a dict of major, minor, kind ("alpha", "beta", "release" for a patch
    level above 0, "plain" without a marker), patch (or None) and whether nine bytes left no room for
    a marker; None for anything else."""
    raw = text.rstrip("\0")
    match = re.fullmatch(r"(?:LAME|L(?=\d))(\d+)\.(\d+)(.*)", raw.rstrip(" "))
    if not match:
        return None
    major, minor, tail = match.groups()
    if not tail:
        return {"major": major, "minor": minor, "kind": "plain", "patch": None, "full": len(raw) >= 9 and not raw.endswith(" ")}
    for kind, pattern in [("release", r"\.(\d*)|r(\d*)"), ("alpha", r"a(\d*)| \(alpha(?: (\d+))?\)"), ("beta", r"b(\d*)| \(beta(?: (\d+))?\)")]:
        marker = re.fullmatch(pattern, tail)
        if marker:
            digits = next((g for g in marker.groups() if g), "")
            return {"major": major, "minor": minor, "kind": kind, "patch": int(digits) if 0 < len(digits) < 6 else None, "full": False}
    return None


def release_name(release):
    version = f"LAME {release['major']}.{release['minor']}"
    if release["kind"] == "plain":
        return version
    if release["kind"] == "release":
        return f"{version}.{release['patch'] if release['patch'] is not None else 'x'}"
    return f"{version} ({release['kind']}{'' if release['patch'] is None else ' ' + str(release['patch'])})"


def releases_agree(tag, drained):
    """Whether the version in the ancillary data can be the one the tag's stands for."""
    if (tag["major"], tag["minor"]) != (drained["major"], drained["minor"]):
        return False
    if tag["full"] and tag["kind"] == "plain":
        return True
    return tag["kind"] == drained["kind"] and (tag["patch"] is None or drained["patch"] is None or tag["patch"] == drained["patch"])


def lame_name(text):
    """The encoder name for LAME's version text: "LAME 3.98 (beta)", "LAME 3.99.x", or as written."""
    release = lame_release(text)
    if release:
        return release_name(release)
    text = text.rstrip("\0 ")
    if text.startswith("LAME"):
        return f"LAME {text[4:]}" if len(text) > 4 else "LAME"
    return f"LAME {text[1:]}"


def tag_and_ancillary_name(tag_text, drained_text):
    """The name from a LAME tag's version and the whole version LAME wrote into the ancillary data
    (or None): the more specific of the two where they agree, the tag's otherwise. Also whether
    they disagree, and whether the ancillary version is evidence too, which it is where it agrees
    and has a marker (alpha, beta or a release's patch level)."""
    tag = lame_release(tag_text)
    drained = lame_release(drained_text) if drained_text else None
    if tag is None or drained is None:
        return lame_name(tag_text), False, False
    if not releases_agree(tag, drained):
        return release_name(tag), True, False
    if drained["kind"] == "plain":
        return release_name(tag), False, False
    more = (drained["patch"] is not None and tag["patch"] is None) or (tag["full"] and tag["kind"] == "plain")
    return release_name(drained if more else tag), False, True
