#!/usr/bin/env python3
"""Writes the byte-exact Xing, Info, LAME tag and VBRI fixtures in tests/fixtures/vbr.

Each fixture is a Layer III stream whose first frame holds a VBR header (see vbr_tags.py for the
layouts and their sources), followed by frames with zero side information, which every decoder
plays as silence. The header's counts, TOC, music length and CRCs are computed from the frames
that follow, unless the fixture breaks one on purpose.

Manifests extend the ones from make_mpeg_fixtures.py:

- The fields of a frame that holds a header include the header's fields as the report shows
  them, by node path ("Xing/Frame Count", "LAME Tag/Encoder Delay", "VBRI/TOC/Entry 0").
- "vbr_header": the raw values of the first frame's header, which crosscheck_mpeg_fixtures.py
  compares with what mp3guessenc, mpg123 and ffprobe read.
- "detected_encoder": the encoder the analyzer names, how confident it is, or null. A LAME tag
  whose CRC holds, or a VBRI header, is high confidence; a LAME tag whose CRC fails is medium;
  an encoder name found only in ancillary data, where LAME writes "LAME" and its version into
  bits no frame uses, is low.
- "silent": ffmpeg, mpg123 and libmad decode the file to silence without an error.
- "mp3guessenc_encoder_string": the encoder string mp3guessenc finds in ancillary data, which it
  only searches on byte boundaries.

The script uses only the standard library and is deterministic, so CI reruns it and fails when
the committed files differ.
"""

import pathlib
import sys

import make_mpeg_fixtures as mpeg
import vbr_tags
import make_layer3_fixtures as layer3
from make_layer3_fixtures import Bits, Granule, Layer3Fixture, Layer3Frame
from make_mpeg_fixtures import Fixture, Frame

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "vbr"


class VbrFixture(Fixture):
    out = OUT

    def __init__(self, name, description, encoder=None, silent=True, crosscheck=True):
        super().__init__(name, ".mp3", description, crosscheck=crosscheck)
        self.encoder, self.silent = encoder, silent
        self.headers = []
        self.extra = {}

    def add_stream(self, tag_frame, audio, truncate_last=None):
        """Adds a frame holding a VBR header, then the audio frames, and records the header."""
        start = len(self.data)
        self.add_frames([tag_frame] + audio, truncate_last=truncate_last)
        frame_bytes = bytes(self.data[start : start + tag_frame.length()])
        music = bytes(self.data[start + tag_frame.length() :])
        tag = vbr_tags.read(frame_bytes, music)
        assert tag is not None, self.name
        entry = self.frames[-len(audio) - 1]
        entry["fields"].update(vbr_tags.report_fields(tag))
        tag["frame_offset"] = start
        self.layout[-len(audio) - 1]["header"] = tag
        self.headers.append(tag)
        for code, contains in vbr_tags.findings(tag, start, cut_short(tag, self.frames[-len(audio) - 1 :])):
            self.finding(code, contains)
        return self

    def manifest(self):
        manifest = super().manifest()
        manifest["silent"] = self.silent
        # The tools read the header in the file's first frame only.
        manifest["vbr_header"] = self.headers[0] if self.headers and self.headers[0]["frame_offset"] == self.frames[0]["offset"] else None
        manifest["detected_encoder"] = self.encoder
        manifest.update(self.extra)
        return manifest


class VbrLayer3Fixture(Layer3Fixture):
    """A VBR header frame before Layer III frames whose main data the reservoir model follows
    (make_layer3_fixtures.py), the header frame included."""

    out = OUT

    def __init__(self, name, description, encoder=None, silent=True, crosscheck=True):
        super().__init__(name, description, crosscheck=crosscheck, silent=silent, guessenc=False)
        self.encoder = encoder
        self.headers = []

    def add_stream(self, tag_frame, audio):
        tag_frame.is_header = True
        start = len(self.data)
        self.add_frames([tag_frame] + audio)
        frame_bytes = bytes(self.data[start : start + tag_frame.length()])
        tag = vbr_tags.read(frame_bytes, bytes(self.data[start + tag_frame.length() :]))
        tag_frame.tag_end = tag_end(tag)
        entry = self.frames[-len(audio) - 1]
        entry["fields"].update(vbr_tags.report_fields(tag))
        tag["frame_offset"] = start
        self.layout[-len(audio) - 1]["header"] = tag
        self.headers.append(tag)
        for code, contains in vbr_tags.findings(tag, start, cut_short(tag, self.frames[-len(audio) - 1 :])):
            self.finding(code, contains)
        return self

    def manifest(self):
        manifest = super().manifest()
        manifest["vbr_header"] = self.headers[0] if self.headers else None
        manifest["detected_encoder"] = self.encoder
        return manifest


def cut_short(tag, entries):
    """Whether the stream after a header (entries: its frame and the frames after it) holds less than
    the header counts: a truncated frame, or fewer frames or bytes."""
    counted = sum(e["length"] for e in entries)
    return (any(e.get("truncated") for e in entries) or (tag.get("frames") or 0) > len(entries)
            or (tag.get("bytes") or 0) > counted)


def tag_end(tag):
    """Where the analyzer's VBR header nodes end in the frame, before what is unused."""
    if tag["type"] == "VBRI":
        return vbr_tags.VBRI_OFFSET + 26 + len(tag["toc"]) * tag["entry_size"]
    assert "lame" in tag, "only VBRI and LAME tags are placed in a reservoir chain here"
    return tag["lame"]["offset"] + vbr_tags.LAME_TAG_SIZE


FIXTURES = []


def fixture(func):
    FIXTURES.append(func)
    return func


def encoder(name, confidence):
    return {"name": name, "confidence": confidence}


def body_offset(frame, offset):
    return offset - 4 - (2 if frame.protected else 0)


def put(frame, offset, data):
    """Writes data offset bytes into the frame, cut at the frame's end like a header that does not fit."""
    start = body_offset(frame, offset)
    frame.payload = (start, data[: frame.length() - 4 - (2 if frame.protected else 0) - start])


def toc(audio):
    """LAME's seek table: for each percent of the frames, the share of the audio bytes before it, in 256ths."""
    lengths = [f.length() for f in audio]
    total = sum(lengths)
    return [min(255, 256 * sum(lengths[: i * len(audio) // 100]) // total) for i in range(100)]


def xing_frame(audio, tag_id="Xing", bitrate=128, fields="FBTQ", quality=57, frames=None, toc_values=None, lame=None,
               protected=False, tag_crc=None, music_crc=None, after_xing=b""):
    """A frame like the one LAME writes first: same version, rate and mode as the audio, zero side
    information, then Xing or Info, optionally a LAME tag (a dict of vbr_tags.lame_bytes arguments)."""
    a = audio[0]
    frame = Frame(version=a.version, layer=3, bitrate=bitrate, rate=a.rate, mode=a.mode, protected=protected)
    offset = vbr_tags.xing_offset(a.header())
    audio_bytes = b"".join(f.bytes() for f in audio)
    xing = vbr_tags.xing_bytes(
        tag_id,
        frames=(len(audio) if frames is None else frames) if "F" in fields else None,
        byte_count=frame.length() + len(audio_bytes) if "B" in fields else None,
        toc=(toc(audio) if toc_values is None else toc_values) if "T" in fields else None,
        quality=quality if "Q" in fields else None,
    )
    if lame is None:
        put(frame, offset, xing + after_xing)
        return frame
    arguments = dict(music_length=frame.length() + len(audio_bytes), music_crc=vbr_tags.crc16(audio_bytes))
    arguments.update(lame)
    if music_crc is not None:
        arguments["music_crc"] = music_crc
    tag = xing + vbr_tags.lame_bytes(**arguments)
    put(frame, offset, tag + b"\0\0")
    data = frame.bytes()
    crc = vbr_tags.crc16(data[: offset + len(tag)]) if tag_crc is None else tag_crc(data, offset + len(tag))
    put(frame, offset, tag + crc.to_bytes(2, "big"))
    return frame


def vbri_frame(audio, bitrate, entry_size=2, toc_scale=1, frames_per_entry=1, delay=1105, quality=75, entries=None, version=1):
    a = audio[0]
    frame = Frame(version=a.version, layer=3, bitrate=bitrate, rate=a.rate, mode=a.mode)
    lengths = [f.length() for f in audio]
    segments = [sum(lengths[i : i + frames_per_entry]) // toc_scale for i in range(0, len(lengths), frames_per_entry)]
    if entries is not None:
        segments = (segments * (entries // len(segments) + 1))[:entries]
    # Fraunhofer's encoders count the VBRI frame itself, as mp3guessenc notes.
    data = vbr_tags.vbri_bytes(version=version, delay=delay, quality=quality, byte_count=frame.length() + sum(lengths), frames=len(audio) + 1,
                               toc=segments, toc_scale=toc_scale, entry_size=entry_size, frames_per_entry=frames_per_entry)
    put(frame, vbr_tags.VBRI_OFFSET, data)
    return frame


def vbr_audio(bitrates, **kwargs):
    return [Frame(bitrate=b, **kwargs) for b in bitrates]


LAME_EVERY_FIELD = dict(
    encoder="LAME3.99r", revision=1, method=1, lowpass=160, peak=0x400000,
    radio_gain=vbr_tags.gain(1, 3, -3.4), audiophile_gain=vbr_tags.gain(2, 1, 1.2),
    flags=0x10 | 0x40 | 0x80 | 3, bitrate=128, delay=576, padding=1234,
    misc=2 | 1 << 2 | 1 << 5 | 2 << 6, mp3_gain=-2, preset_surround=2 << 11 | 1001,
)
LAME_VBR = dict(encoder="LAME3.100", method=4, lowpass=195, flags=0x10 | 0x20 | 5, bitrate=32, delay=576, padding=960,
                misc=3 << 2 | 1 << 6, preset_surround=480)
LAME_CBR = dict(encoder="LAME3.100", method=1, lowpass=170, flags=0x10 | 4, bitrate=128, delay=576, padding=1566,
                misc=1 << 2 | 1 << 6, preset_surround=128)


# --- Xing and Info ----------------------------------------------------------------------------


@fixture
def xing_all_fields():
    audio = vbr_audio([128, 160, 96, 192, 112, 128, 224, 64, 160, 128], rate=44100, mode="Joint stereo")
    f = VbrFixture("xing-all-fields", "MPEG-1 Layer III, 44100 Hz, joint stereo, ten frames from 64 to 224 kbps after a Xing header "
                   "with frame count, byte count, TOC and quality, and no LAME tag.")
    return f.add_stream(xing_frame(audio), audio)


@fixture
def xing_frames_only():
    audio = vbr_audio([64, 96, 80, 128, 64, 96], rate=32000, mode="Mono")
    f = VbrFixture("xing-frames-only", "MPEG-1 Layer III mono, 32000 Hz: the Xing header (21 bytes in) has only the frame count.")
    return f.add_stream(xing_frame(audio, bitrate=64, fields="F"), audio)


@fixture
def xing_bytes_quality():
    audio = vbr_audio([64, 32, 96, 64, 48, 80], version="2", rate=22050, mode="Stereo")
    f = VbrFixture("xing-bytes-quality", "MPEG-2 Layer III stereo, 22050 Hz: the Xing header (21 bytes in) has the byte count and "
                   "quality, so the quality follows the byte count directly.")
    return f.add_stream(xing_frame(audio, bitrate=64, fields="BQ", quality=78), audio)


@fixture
def xing_truncated():
    audio = vbr_audio([8, 16, 8, 24, 8], version="2.5", rate=8000, mode="Mono")
    f = VbrFixture("xing-truncated", "MPEG-2.5 Layer III mono, 8000 Hz, 8 kbps: the frame holding the Xing header is 72 bytes long, "
                   "so the TOC its flags announce does not fit; the frame and byte counts before it do.")
    return f.add_stream(xing_frame(audio, bitrate=8), audio)


@fixture
def xing_toc_decreasing():
    audio = vbr_audio([128] * 8, rate=48000, mode="Stereo")
    table = toc(audio)
    table[40], table[41] = table[41] + 3, table[40]
    f = VbrFixture("xing-toc-decreasing", "MPEG-1 Layer III stereo, 48000 Hz: entry 41 of the Xing TOC is smaller than entry 40, "
                   "so seeking by it goes backwards.")
    return f.add_stream(xing_frame(audio, toc_values=table), audio)


@fixture
def xing_in_layer2():
    f = VbrFixture("xing-in-layer2", "MPEG-1 Layer II frames with \"Xing\" where a Layer III frame would hold it; Layer II frames "
                   "carry no VBR header, so these bytes are ancillary data.", crosscheck=True)
    f.extension = ".mp2"
    frames = mpeg.stream(5, layer=2, bitrate=192, rate=48000, mode="Stereo")
    first = frames[0]
    put(first, 36, vbr_tags.xing_bytes("Xing", frames=5))
    f.add_frames(frames)
    return f


# --- LAME tags --------------------------------------------------------------------------------


@fixture
def lame_every_field():
    audio = mpeg.stream(10, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-every-field", "An Info header and a LAME tag with every field set to a distinctive value: revision 1, "
                   "lowpass 16 kHz, peak 0.5, radio and audiophile ReplayGain, all four encoding flags but one, delay and "
                   "padding, noise shaping 2, unwise settings, 48 kHz source, MP3 gain -3 dB, DPL2 surround and the Standard "
                   "preset. \"LAME3.99r\" is a release of 3.99 with a patch level the tag has no room for.",
                   encoder=encoder("LAME 3.99.x", "High"))
    return f.add_stream(xing_frame(audio, tag_id="Info", lame=LAME_EVERY_FIELD), audio)


@fixture
def lame_vbr_mono():
    audio = vbr_audio([64, 96, 80, 128, 112, 64, 96, 80], rate=44100, mode="Mono")
    f = VbrFixture("lame-vbr-mono", "MPEG-1 Layer III mono, 44100 Hz: Xing 21 bytes into the frame, so the LAME tag CRC covers "
                   "the first 175 bytes, not 190.", encoder=encoder("LAME 3.100", "High"))
    return f.add_stream(xing_frame(audio, lame=LAME_VBR), audio)


@fixture
def lame_mpeg2_mono():
    audio = vbr_audio([32, 48, 40, 64, 32, 56], version="2", rate=24000, mode="Mono")
    f = VbrFixture("lame-mpeg2-mono", "MPEG-2 Layer III mono, 24000 Hz: Xing 13 bytes into the frame.",
                   encoder=encoder("LAME 3.100", "High"))
    return f.add_stream(xing_frame(audio, bitrate=64, lame=LAME_VBR), audio)


@fixture
def lame_mpeg25_stereo():
    audio = vbr_audio([32, 48, 40, 64, 32, 56], version="2.5", rate=11025, mode="Joint stereo")
    f = VbrFixture("lame-mpeg25-stereo", "MPEG-2.5 Layer III joint stereo, 11025 Hz: Xing 21 bytes into the frame.",
                   encoder=encoder("LAME 3.100", "High"))
    return f.add_stream(xing_frame(audio, bitrate=64, lame=LAME_VBR), audio)


@fixture
def lame_tag_crc_mismatch():
    audio = vbr_audio([128, 160, 96, 192, 128], rate=48000, mode="Joint stereo")
    f = VbrFixture("lame-tag-crc-mismatch", "A LAME tag whose CRC does not match the bytes before it, so the tag may be damaged; "
                   "ffmpeg then ignores its encoder string and ReplayGain.", encoder=encoder("LAME 3.100", "Medium"))
    return f.add_stream(xing_frame(audio, lame=LAME_VBR, tag_crc=lambda data, end: vbr_tags.crc16(data[:end]) ^ 0x0100), audio)


@fixture
def lame_music_crc_mismatch():
    audio = mpeg.stream(8, bitrate=96, rate=32000, mode="Mono")
    f = VbrFixture("lame-music-crc-mismatch", "A LAME tag whose music CRC does not match the frames after it, as after MP3Gain "
                   "or any other edit of the audio data.", encoder=encoder("LAME 3.100", "High"))
    return f.add_stream(xing_frame(audio, tag_id="Info", bitrate=96, lame=dict(LAME_CBR, bitrate=96, preset_surround=96), music_crc=0x1234), audio)


@fixture
def lame_ffmpeg_crc():
    audio = mpeg.stream(6, bitrate=64, rate=44100, mode="Mono")
    f = VbrFixture("lame-ffmpeg-crc", "An Info header and LAME tag 21 bytes into an MPEG-1 mono frame whose tag CRC covers the "
                   "frame's first 190 bytes with the CRC field zero, as ffmpeg 6.1 writes it, instead of the 175 bytes before "
                   "the CRC.", encoder=encoder("FFmpeg (Lavc60.31)", "Medium"))

    def ffmpeg_crc(data, end):
        zeroed = bytearray(data[:190])
        zeroed[end : end + 2] = b"\0\0"
        return vbr_tags.crc16(bytes(zeroed))

    lame = dict(encoder="Lavc60.31", method=0, delay=576, padding=1566)
    return f.add_stream(xing_frame(audio, tag_id="Info", bitrate=64, quality=0, lame=lame, tag_crc=ffmpeg_crc), audio)


@fixture
def lame_old_string():
    audio = vbr_audio([128, 160, 96, 192, 128, 112], rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-old-string", "A Xing header followed by \"LAME3.88 (beta)\": LAME before 3.90 wrote only its version "
                   "string, so there are no LAME tag fields or CRC to read.", encoder=encoder("LAME 3.88 (beta)", "Medium"))
    return f.add_stream(xing_frame(audio, after_xing=b"LAME3.88 (beta)"), audio)


@fixture
def lame_protected():
    audio = mpeg.stream(8, bitrate=128, rate=44100, mode="Stereo", protected=True)
    f = VbrFixture("lame-protected", "A CRC-protected stream: LAME writes Info 36 bytes into the frame, as without a CRC, so its "
                   "first two bytes are the last two of the side information the frame CRC covers.",
                   encoder=encoder("LAME 3.100", "High"), silent=False)
    return f.add_stream(xing_frame(audio, tag_id="Info", protected=True, lame=dict(LAME_CBR, misc=1 << 2 | 1 << 6)), audio)


@fixture
def lame_truncated_music():
    audio = mpeg.stream(10, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-truncated-music", "A LAME tag for ten frames, of which the file holds six, so the music CRC cannot be "
                   "verified.", encoder=encoder("LAME 3.100", "High"))
    tag = xing_frame(audio, tag_id="Info", lame=LAME_CBR)
    f.add_stream(tag, audio[:6])
    lame = f.headers[0]["lame"]
    assert lame["music_crc_computed"] is None
    return f


@fixture
def lame_mid_stream():
    first = mpeg.stream(5, bitrate=128, rate=44100, mode="Joint stereo")
    second = mpeg.stream(5, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-mid-stream", "Two LAME files concatenated without tags: the second Info frame sits in the middle of "
                   "the stream, where decoders that only look at the first frame play it as a frame of silence.",
                   encoder=encoder("LAME 3.100", "High"))
    f.add_stream(xing_frame(first, tag_id="Info", lame=LAME_CBR), first)
    f.add_stream(xing_frame(second, tag_id="Info", lame=LAME_CBR), second)
    return f.finding("mpeg.vbr-header-mid-stream", f"0x{f.frames[6]['offset']:X}")


@fixture
def lame_music_length_short():
    audio = mpeg.stream(6, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-music-length-short", "A LAME tag whose music length is 100 bytes, shorter than its own frame: the music "
                   "CRC is not verified.", encoder=encoder("LAME 3.100", "High"))
    return f.add_stream(xing_frame(audio, tag_id="Info", lame=dict(LAME_CBR, music_length=100), music_crc=0), audio)


@fixture
def lame_music_length_not_set():
    audio = mpeg.stream(6, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-music-length-not-set", "A LAME tag whose music length is 0, which the Mp3 Info Tag specification "
                   "defines as not set: no warning, and the music CRC is not verified.", encoder=encoder("LAME 3.100", "High"))
    return f.add_stream(xing_frame(audio, tag_id="Info", lame=dict(LAME_CBR, music_length=0), music_crc=0), audio)


@fixture
def lame_music_length_long():
    audio = mpeg.stream(6, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-music-length-long", "A LAME tag whose music length runs 1000 bytes past the end of the file, though "
                   "the stream holds every frame and byte its Info header counts: the length is wrong, and the music CRC is "
                   "not verified.", encoder=encoder("LAME 3.100", "High"))
    length = Frame(bitrate=128, rate=44100, mode="Joint stereo").length() * 7 + 1000
    return f.add_stream(xing_frame(audio, tag_id="Info", lame=dict(LAME_CBR, music_length=length)), audio)


@fixture
def lame_truncated_last_frame():
    audio = mpeg.stream(8, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-truncated-last-frame", "A LAME stream whose file ends 100 bytes into the last of the eight frames its "
                   "Info header counts. The frame was written whole, so the frame count and the gapless sample count include "
                   "it; bytes are missing, so the byte count does not match and the music CRC is not verified.",
                   encoder=encoder("LAME 3.100", "High"), crosscheck=False)
    f.add_stream(xing_frame(audio, tag_id="Info", lame=LAME_CBR), audio, truncate_last=100)
    return f.finding("mpeg.frame-truncated", "100 of")


@fixture
def lame_old_gain_reference():
    audio = vbr_audio([128, 160, 96, 192, 128, 112], rate=44100, mode="Joint stereo")
    lame = dict(LAME_VBR, encoder="LAME3.94", radio_gain=vbr_tags.gain(1, 3, -5.2), audiophile_gain=vbr_tags.gain(2, 2, 1.0))
    f = VbrFixture("lame-old-gain-reference", "A LAME 3.94 tag with ReplayGain it determined itself: before 3.95, LAME computed "
                   "it against 83 dB instead of 89 dB, so the gain reads 6 dB lower than a later version's, which the report "
                   "says. The audiophile gain, set by the user, is shown as stored.", encoder=encoder("LAME 3.94", "High"))
    return f.add_stream(xing_frame(audio, lame=lame), audio)


@fixture
def lame_after_gap():
    first = mpeg.stream(5, bitrate=128, rate=44100, mode="Joint stereo")
    second = mpeg.stream(5, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-after-gap", "Two LAME files joined with 300 bytes of junk between them: the second Info frame follows "
                   "a gap, but players that resync play it as silence all the same, so it is in the middle of the stream.",
                   encoder=encoder("LAME 3.100", "High"), silent=False, crosscheck=False)
    f.add_stream(xing_frame(first, tag_id="Info", lame=LAME_CBR), first)
    junk = bytes(range(1, 256))[:300] + bytes(range(1, 46))
    f.gap(junk)
    f.finding("mpeg.sync-lost", f"{len(junk)} bytes")
    f.add_stream(xing_frame(second, tag_id="Info", lame=LAME_CBR), second)
    return f.finding("mpeg.vbr-header-mid-stream", f"0x{f.frames[6]['offset']:X}")


@fixture
def lame_after_joined_frames():
    first = mpeg.stream(6, bitrate=112, rate=48000, mode="Stereo")
    second = mpeg.stream(5, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-after-joined-frames", "Six frames at 48 kHz without a header, then a LAME file at 44.1 kHz, as when "
                   "files are joined: the LAME tag describes only the five frames after it, so it does not name the region's "
                   "encoder with its own confidence, and its gapless figures say which frames they cover.",
                   encoder=encoder("LAME 3.100", "Low"))
    f.add_frames(first)
    f.add_stream(xing_frame(second, tag_id="Info", lame=LAME_CBR), second)
    f.finding("mpeg.stream-changed", "48000 Hz")
    return f.finding("mpeg.vbr-header-mid-stream", f"0x{f.frames[6]['offset']:X}")


@fixture
def lame_before_joined_frames():
    first = mpeg.stream(5, bitrate=128, rate=44100, mode="Joint stereo")
    second = mpeg.stream(6, bitrate=112, rate=48000, mode="Stereo")
    f = VbrFixture("lame-before-joined-frames", "A LAME file at 44.1 kHz, then six frames at 48 kHz without a header: the LAME "
                   "tag describes only the first five audio frames, so it names the region's encoder with medium confidence, "
                   "and its gapless figures say which frames they cover.", encoder=encoder("LAME 3.100", "Medium"))
    f.add_stream(xing_frame(first, tag_id="Info", lame=LAME_CBR), first)
    f.add_frames(second)
    return f.finding("mpeg.stream-changed", "44100 Hz")


@fixture
def lame_joined_other_version():
    first = mpeg.stream(5, bitrate=128, rate=44100, mode="Joint stereo")
    second = mpeg.stream(5, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-joined-other-version", "Two LAME files joined, the second with a LAME 3.99 tag: the region holds frames "
                   "of two encoders, so the first tag names its encoder with medium confidence.",
                   encoder=encoder("LAME 3.100", "Medium"))
    f.add_stream(xing_frame(first, tag_id="Info", lame=LAME_CBR), first)
    f.add_stream(xing_frame(second, tag_id="Info", lame=dict(LAME_CBR, encoder="LAME3.99r")), second)
    return f.finding("mpeg.vbr-header-mid-stream", f"0x{f.frames[6]['offset']:X}")


@fixture
def lame_tag_text():
    audio = mpeg.stream(6, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-tag-text", "An Info header followed by other text where a LAME tag would start: only \"LAME\", "
                   "\"L\" with a version and ffmpeg's \"Lavc\" and \"Lavf\" start one, as mp3guessenc and ffmpeg read it, so "
                   "the text is no tag, names no encoder and gives no gapless figures.")
    lame = vbr_tags.lame_bytes(**dict(LAME_CBR, encoder="Converted"))
    return f.add_stream(xing_frame(audio, tag_id="Info", after_xing=b"Converted by SomeTool" + lame[9:]), audio)


@fixture
def lame_tag_lavf_remux():
    audio = mpeg.stream(24, bitrate=128, rate=44100, mode="Joint stereo", original=1)
    f = VbrFixture("lame-tag-lavf-remux", "An Info header and a LAME tag with ffmpeg's \"Lavf\", as ffmpeg -c copy writes them "
                   "for a stream whose encoder it does not know, before 24 frames that show Xing's habits: a remux names no "
                   "encoder, so the frames do, with low confidence and the tag as support.",
                   encoder=encoder("Xing", "Low"))
    lame = dict(encoder="Lavf", method=0, delay=576, padding=1566)
    return f.add_stream(xing_frame(audio, tag_id="Info", quality=0, lame=lame), audio)


@fixture
def lame_tag_lavf_alone():
    audio = mpeg.stream(6, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-tag-lavf-alone", "An Info header and a LAME tag with ffmpeg's \"Lavf\" before frames that show no "
                   "encoder's habits: a remux names no encoder, and nothing else does.")
    lame = dict(encoder="Lavf", method=0, delay=576, padding=1566)
    return f.add_stream(xing_frame(audio, tag_id="Info", quality=0, lame=lame), audio)


# The Info frame of an MPEG-1 joint stereo frame at 128 kbps holds 381 bytes after its side
# information, 156 of them the Xing fields and the LAME tag.
INFO_AREA = 381


@fixture
def main_data_in_header_frame():
    f = VbrLayer3Fixture(
        "main-data-in-header-frame",
        "An Info frame with a LAME tag before frames whose first main_data_begin is 200, as ffmpeg -ss -c copy writes a "
        "cut: its main data begins in the Info frame's unused bytes. minimp3 decodes it from them, mpg123 and ffmpeg skip "
        "the Info frame, so the frame is decoded with data missing.",
        encoder=encoder("LAME 3.100", "High"), silent=False, crosscheck=False,
    )
    chain = layer3.reservoir_chain([(0, 0), (300, 200), (100, 0), (250, 50), (80, 0), (120, 0)])
    assert chain[0].area_length() == INFO_AREA and chain[0].length() == 417
    audio = chain[1:]
    info = xing_frame(audio, tag_id="Info", lame=LAME_CBR)
    start, tag = info.payload
    reservoir = bytes(chain[0].area[INFO_AREA - 200 :])
    info.payload = (start, tag + bytes(INFO_AREA - 200 - len(tag)) + reservoir)
    return f.add_stream(info, audio)


@fixture
def main_data_before_header_frame():
    f = VbrLayer3Fixture(
        "main-data-before-header-frame",
        "An Info frame with a LAME tag before frames whose first main_data_begin is 450, more than the Info frame's 381 "
        "bytes: the main data begins before the stream, which was cut.",
        encoder=encoder("LAME 3.100", "High"), silent=False, crosscheck=False,
    )
    chain = layer3.reservoir_chain([(0, 0), (0, 381), (300, 450), (100, 0), (250, 50), (80, 0), (120, 0)])
    audio = chain[2:]
    return f.add_stream(xing_frame(audio, tag_id="Info", lame=LAME_CBR), audio)


# --- VBRI -------------------------------------------------------------------------------------


@fixture
def vbri_protected_stream():
    audio = vbr_audio([128, 160, 96, 192, 112, 128, 224, 64], rate=44100, mode="Joint stereo", protected=True)
    f = VbrFixture("vbri-protected-stream", "A VBRI header 36 bytes into the first frame, with 2-byte TOC entries, before a "
                   "CRC-protected stream; Fraunhofer's encoders leave the VBRI frame unprotected.",
                   encoder=encoder("Fraunhofer", "High"))
    return f.add_stream(vbri_frame(audio, bitrate=128), audio)


@fixture
def vbri_mpeg2_mono():
    audio = vbr_audio([32, 48, 40, 64, 32, 56, 48, 40], version="2", rate=22050, mode="Mono")
    f = VbrFixture("vbri-mpeg2-mono", "MPEG-2 Layer III mono, 22050 Hz: VBRI is 36 bytes into the frame in every mode; 3-byte TOC "
                   "entries with a scale of 2, two frames per entry.", encoder=encoder("Fraunhofer", "High"))
    return f.add_stream(vbri_frame(audio, bitrate=64, entry_size=3, toc_scale=2, frames_per_entry=2), audio)


@fixture
def vbri_entry_size_4():
    audio = vbr_audio([64, 96, 80, 128, 112, 64], rate=48000, mode="Mono")
    f = VbrFixture("vbri-entry-size-4", "MPEG-1 Layer III mono, 48000 Hz, VBRI with 4-byte TOC entries.",
                   encoder=encoder("Fraunhofer", "High"))
    return f.add_stream(vbri_frame(audio, bitrate=64, entry_size=4), audio)


@fixture
def vbri_entry_size_1():
    audio = vbr_audio([32, 48, 40, 64, 32, 56], version="2.5", rate=11025, mode="Stereo")
    f = VbrFixture("vbri-entry-size-1", "MPEG-2.5 Layer III stereo, 11025 Hz, VBRI with 1-byte TOC entries and a scale of 2.",
                   encoder=encoder("Fraunhofer", "High"))
    return f.add_stream(vbri_frame(audio, bitrate=64, entry_size=1, toc_scale=2), audio)


@fixture
def vbri_truncated():
    audio = vbr_audio([128, 160, 96, 192, 112, 128], rate=44100, mode="Stereo")
    f = VbrFixture("vbri-truncated", "A VBRI header whose TOC announces 200 entries of 2 bytes, more than the 417-byte frame "
                   "holds.", encoder=encoder("Fraunhofer", "High"))
    return f.add_stream(vbri_frame(audio, bitrate=128, entries=200), audio)


@fixture
def vbri_entry_size_invalid():
    audio = vbr_audio([128, 160, 96, 192, 112, 128], rate=44100, mode="Stereo")
    f = VbrFixture("vbri-entry-size-invalid", "A VBRI header with 5-byte TOC entries; Fraunhofer's reader knows 1 to 4 bytes, so "
                   "the TOC is not read, although it fits the frame.", encoder=encoder("Fraunhofer", "High"))
    return f.add_stream(vbri_frame(audio, bitrate=128, entry_size=5), audio)


@fixture
def vbri_version_unknown():
    audio = vbr_audio([128, 160, 96, 192, 112, 128], rate=44100, mode="Stereo")
    f = VbrFixture("vbri-version-unknown", "A VBRI header of version 7, where only version 1 is known; ffmpeg ignores the header.",
                   encoder=encoder("Fraunhofer", "High"))
    return f.add_stream(vbri_frame(audio, bitrate=128, version=7), audio)


@fixture
def vbri_mp3pro():
    f = VbrLayer3Fixture(
        "vbri-mp3pro",
        "A VBRI header before twelve MPEG-2 frames at 22050 Hz with the mp3PRO signature in their ancillary data: "
        "Fraunhofer's mp3PRO encoder.",
        encoder=encoder("Fraunhofer mp3PRO encoder", "High"),
    )
    audio = layer3.enhanced_chain(layer3.signatures(12, 0xC0, 0x08), **layer3.MP3PRO)
    return f.add_stream(vbri_frame(audio, bitrate=64), audio)


# --- Encoder names in ancillary data ----------------------------------------------------------


def ancillary_frame(granule_bits, lead=b"", tail=b""):
    """A frame whose granules each code granule_bits quadruples of zeros (one bit each with count1
    table A), then lead and LAME's alternating 0x55/0xAA fill in the ancillary bits after them, and
    tail in the last bytes of the frame."""
    frame = Layer3Frame([[Granule(quads=granule_bits), Granule(quads=granule_bits)] for _ in range(2)], bitrate=128, rate=44100,
                        mode="Stereo")
    bits = Bits()
    for gr in range(2):
        for ch in range(2):
            bits.bits += frame.granule_bits(gr, ch)
    for byte in lead:
        bits.put(byte, 8)
    while len(bits) < (frame.area_length() - len(tail)) * 8:
        bits.put(1 - len(bits) % 2, 1)
    for byte in tail:
        bits.put(byte, 8)
    frame.area[:] = bits.bytes()
    return frame


def signature_frames(count, signature, granule_bits):
    return [ancillary_frame(granule_bits, lead=signature) for _ in range(count)]


@fixture
def lame_signature():
    f = VbrFixture("lame-signature", "No VBR header, but every frame's ancillary data starts with \"LAME3.99r\" on a byte "
                   "boundary, as LAME pads unused bits; the 3.99 release whose patch level that does not say.",
                   encoder=encoder("LAME 3.99.x", "Low"))
    f.extra["mp3guessenc_encoder_string"] = "LAME3.99r"
    return f.add_frames(signature_frames(6, b"LAME3.99r", 2))


@fixture
def lame_signature_tsse():
    f = VbrFixture("lame-signature-tsse", "LAME's version in ancillary data and in an ID3v2.3 TSSE frame, as LAME writes it with "
                   "tags: the frames and the tag agree, which together make medium confidence.",
                   encoder=encoder("LAME 3.99.x", "Medium"))
    f.extra["mp3guessenc_encoder_string"] = "LAME3.99r"
    f.tag("ID3v2", mpeg.id3v2("Signed", extra_frames=mpeg.tsse_frame("LAME 32bits version 3.99.5 (http://lame.sf.net)")))
    return f.add_frames(signature_frames(6, b"LAME3.99r", 2))


@fixture
def lame_tsse_differs():
    audio = mpeg.stream(8, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("lame-tsse-differs", "An ID3v2.3 TSSE frame that names BladeEnc before a stream whose LAME tag names LAME, as "
                   "after re-encoding with tags kept: the LAME tag decides, the claim is reported.",
                   encoder=encoder("LAME 3.100", "High"))
    f.tag("ID3v2", mpeg.id3v2("Re-encoded", extra_frames=mpeg.tsse_frame("BladeEnc 0.94.2")))
    f.add_stream(xing_frame(audio, tag_id="Info", lame=LAME_CBR), audio)
    return f.finding("mpeg.encoder-claim-differs", "\"BladeEnc 0.94.2\", but the stream points to LAME 3.100")


@fixture
def lame_signature_unaligned():
    f = VbrFixture("lame-signature-unaligned", "No VBR header; each granule codes three bits, so the ancillary data and the "
                   "\"LAME3.100\" in it start 4 bits into a byte, where a byte-aligned search does not find it.",
                   encoder=encoder("LAME 3.100", "Low"))
    f.extra["mp3guessenc_encoder_string"] = None
    return f.add_frames(signature_frames(6, b"LAME3.100", 3))


@fixture
def lame_signature_across_frames():
    f = VbrFixture("lame-signature-across-frames", "No VBR header; the first frame's ancillary data ends with \"LAM\" and the "
                   "second frame, which has no main data, starts with \"E3.100\", so the signature only shows where the "
                   "ancillary data is read across the frame boundary.", encoder=encoder("LAME 3.100", "Low"))
    f.extra["mp3guessenc_encoder_string"] = None
    frames = [ancillary_frame(2, tail=b"LAM"), ancillary_frame(0, lead=b"E3.100")] + [ancillary_frame(0) for _ in range(4)]
    return f.add_frames(frames)


@fixture
def lame_signature_cut():
    f = VbrFixture("lame-signature-cut", "No VBR header; only the last frame's ancillary data holds LAME's version, and it ends "
                   "with the data after \"LAME3.10\", where LAME ran out of bits: the version may be cut short, so only LAME "
                   "is named.", encoder=encoder("LAME", "Low"))
    f.extra["mp3guessenc_encoder_string"] = "LAME3.10"
    return f.add_frames([ancillary_frame(2) for _ in range(5)] + [ancillary_frame(2, tail=b"LAME3.10")])


@fixture
def lame_signature_tsse_flame():
    f = VbrFixture("lame-signature-tsse-flame", "LAME's version in ancillary data and an ID3v2.3 TSSE frame naming \"Flame 2.5\": "
                   "a name that only contains \"lame\" does not name LAME, so the claim does not raise the confidence and is "
                   "reported as naming another encoder.", encoder=encoder("LAME 3.100", "Low"))
    f.extra["mp3guessenc_encoder_string"] = "LAME3.100"
    f.tag("ID3v2", mpeg.id3v2("Flamed", extra_frames=mpeg.tsse_frame("Flame 2.5")))
    f.add_frames(signature_frames(6, b"LAME3.100", 2))
    return f.finding("mpeg.encoder-claim-differs", "\"Flame 2.5\", but the stream points to LAME 3.100")


@fixture
def tsse_lavf_alone():
    audio = mpeg.stream(24, bitrate=128, rate=44100, mode="Joint stereo")
    f = VbrFixture("tsse-lavf-alone", "An ID3v2.3 TSSE frame with ffmpeg's version, which its muxer writes whatever encoded "
                   "the stream, before frames without a header that show no encoder's habits: no encoder is named.")
    f.tag("ID3v2", mpeg.id3v2("Muxed", extra_frames=mpeg.tsse_frame("Lavf60.16.100")))
    return f.add_frames(audio)


# --- LAME's version in the tag and in ancillary data --------------------------------------------


@fixture
def lame_version_tag_and_ancillary():
    frames = signature_frames(6, b"LAME3.98 (beta 8)", 2)
    f = VbrFixture("lame-version-tag-and-ancillary", "A LAME tag with \"LAME3.98b\" before frames whose ancillary data holds "
                   "\"LAME3.98 (beta 8)\", as LAME 3.98 beta 8 writes both: they agree, and the ancillary data says which beta.",
                   encoder=encoder("LAME 3.98 (beta 8)", "High"))
    f.extra["mp3guessenc_encoder_string"] = "LAME3.98 (beta 8)"
    return f.add_stream(xing_frame(frames, tag_id="Info", lame=dict(LAME_CBR, encoder="LAME3.98b")), frames)


@fixture
def lame_version_differs():
    frames = signature_frames(6, b"LAME3.98 (beta 8)", 2)
    f = VbrFixture("lame-version-differs", "A LAME tag with \"LAME3.99r\" before frames whose ancillary data holds "
                   "\"LAME3.98 (beta 8)\": the tag and the frames name different versions, so the tag's is shown and the "
                   "report says so.", encoder=encoder("LAME 3.99.x", "High"))
    f.extra["mp3guessenc_encoder_string"] = "LAME3.98 (beta 8)"
    f.add_stream(xing_frame(frames, tag_id="Info", lame=dict(LAME_CBR, encoder="LAME3.99r")), frames)
    return f.finding("mpeg.lame-version-differs", "LAME 3.99.x, but the version LAME wrote into the ancillary data names LAME 3.98 (beta 8)")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for path in OUT.glob("*"):
        if not path.name.endswith(".report.json"):
            path.unlink()
    names = set()
    for build in FIXTURES:
        f = build()
        assert f.name not in names, f.name
        names.add(f.name)
        f.write()
    print(f"Wrote {len(names)} fixtures to {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
