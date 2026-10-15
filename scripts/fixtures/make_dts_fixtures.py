#!/usr/bin/env python3
"""Writes the hand-built DTS fixtures into tests/fixtures/dts, with the standard library only, so CI
regenerates them and fails on any difference.

The frames come from dts_model.py: every field written as ETSI TS 102 114 V1.6.1 lays it out, the
subframes silent (no subband has bits allocated), so decoders play silence and the fields are all
there is to check. The fixtures cover what encoders do not write: the four forms of the core stream,
XCh, X96 and XXCH in the core, extension substreams with lossless, low bit rate, extended bit rate,
96 kHz and extra channel assets, a substream without a core, and damage: a truncated and a cut
frame, wrong sizes, junk before, between and after frames, reserved and invalid values, a stream that
changes, bad CRCs and sizes in the extension substream, embedded tags, and MP4 files whose ddts box
disagrees with the frames.

Each <name>.expected.json manifest lists what the analyzer must report: the file's length and
format, every frame and gap (offset and length), the streams with their codec, rate, channels,
channel mode and layout, samples and whether the bitrate varies, fields by node path, exactly the
findings by code with a part of their message, the top-level regions where tags are embedded, and
what crosscheck_dts_fixtures.py checks with ffprobe, ffmpeg and libdca, with a reason for each
check it skips.
"""

import copy
import json
import pathlib
import struct
import sys

import dts_model as m
import mp4_boxes as mp4

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "dts"

FIXTURES = {}

STEREO = ["front left", "front right"]
SURROUND_5_1 = ["front center", "front left", "front right", "back left", "back right", "LFE"]
SURROUND_5_1_WITH_REAR = ["front center", "front left", "front right", "side left", "side right", "back left", "back right", "LFE"]


def stream(codec, rate, layout, mode, samples, vbr=False):
    return {"codec": codec, "sample_rate": rate, "channels": len(layout), "channel_mode": mode, "channel_layout": layout, "samples": samples, "is_vbr": vbr}


def fixture(name, extension, description, data, frames, streams, findings, fields=None, gaps=(), crosscheck=None, fmt="DTS", boxes=None, regions=None):
    FIXTURES[name + extension] = (data, {
        "description": description,
        "length": len(data),
        "format": fmt,
        "frames": frames,
        "gaps": [list(g) for g in gaps],
        "streams": streams,
        "fields": fields or {},
        "findings": [list(f) for f in findings],
        "crosscheck": crosscheck or {},
        **({"boxes": boxes} if boxes else {}),
        **({"regions": regions} if regions else {}),
    })


def core(form="16be", size=512, **options):
    return m.to_form(m.core_frame(size=size, **options), form)


def run(count, form="16be", size=512, **options):
    return [core(form, size, **options) for _ in range(count)]


def rows(parts, start=0):
    """The (offset, length) of each part laid one after another from start."""
    out = []
    at = start
    for part in parts:
        out.append([at, len(part)])
        at += len(part)
    return out


def junk(count, seed=11):
    # No byte of it starts a sync word.
    return bytes(0x80 | ((i * 37 + seed) % 97) for i in range(count))


def id3v1():
    return b"TAG" + b"Hand-built title".ljust(30, b"\0") + b"Hand-built artist".ljust(30, b"\0") + b"Album".ljust(30, b"\0") + b"2026" + b"Comment".ljust(28, b"\0") + b"\0\x01\x11"


def id3v2(encoder=None):
    frame = b"TIT2" + (6).to_bytes(4, "big") + b"\0\0" + b"\0Title"
    if encoder:
        frame += b"TSSE" + (len(encoder) + 1).to_bytes(4, "big") + b"\0\0" + b"\0" + encoder.encode("latin-1")
    size = len(frame)
    return b"ID3\x03\0\0" + bytes([size >> 21 & 0x7F, size >> 14 & 0x7F, size >> 7 & 0x7F, size & 0x7F]) + frame


def ape_tag(*items):
    body = b"".join(struct.pack("<II", len(value), 0) + key.encode() + b"\0" + value.encode() for key, value in sorted(items, key=lambda kv: len(kv[0]) + len(kv[1])))
    size = len(body) + 32
    block = lambda flags: b"APETAGEX" + struct.pack("<IIII", 2000, size, len(items), flags) + bytes(8)
    return block(0xA0000000) + body + block(0x80000000)


INVALID_FIELDS_ERRORS = {"0": "Deficit samples are not supported", "512": "Unsupported number of PCM sample blocks (20)", "1024": "Subband sample buffer overflow",
                         "1536": "Invalid number of primary audio channels (3) for audio channel arrangement (2)", "2048": "Invalid subband activity count", "2560": None}


def cc(ffprobe=True, decode=True, libdca=True, skip=None, **extra):
    """A manifest's crosscheck: which tools check the fixture (see crosscheck_dts_fixtures.py), the reason for each check that is off."""
    result = {"ffprobe": ffprobe, "decode": decode, "libdca": libdca, **extra}
    if skip:
        result["skip"] = skip
    return result


LOSSLESS_BITRATE = "ffmpeg's lossless decoder sets the bit rate to 0 (dca_xll.c: avctx->bit_rate = 0), so ffprobe lists none"


def cc_lossless(**extra):
    """The crosscheck of a lossless stream: ffprobe names its profile and rate from the decoded XLL frames, and lists no bit rate."""
    skip = {"ffprobe_bitrate": LOSSLESS_BITRATE, **extra.pop("skip", {})}
    return cc(ffprobe_bitrate=False, skip=skip, **extra)


CUT_NOTE = ("ffmpeg decodes the damaged frame without an error: the frame's silent subframes take a few dozen of its bytes, the rest is padding that no decoder reads, "
            "and a core frame without CRC words (CPF 0) has no other check of its own")
PROFILE_STUB = ("ffmpeg's decoder sets the profile itself when it has decoded a frame (dca_core.c, dca_xll.c), and DTS-HD Master Audio or DTS-HD High Resolution Audio only after it has decoded the "
                "extension, which the stub components do not allow")
XLL_STUB = "ffmpeg rejects the XLL stub, a common header followed by filler, for its missing channel set sub-header (dca_xll.c: 'Invalid XLL sub-header checksum')"


def build():
    # The four forms of the core stream, the same six stereo frames.
    for form, name, size in (("16be", "core-16be", 512), ("16le", "core-16le", 512), ("14be", "core-14be", 504), ("14le", "core-14le", 504)):
        parts = run(6, form, size)
        data = b"".join(parts)
        wide = "14-bit words with 14 bits in each 16-bit word" if form.startswith("14") else "16-bit words"
        order = "big endian" if form.endswith("be") else "little endian"
        fixture(name, ".dts", f"Six stereo core frames of 512 samples at 48 kHz in {wide}, {order}.", data, rows(parts),
                [stream("DTS", 48000, STEREO, "2/0", 6 * 512)], [],
                crosscheck={"ffprobe": True, "decode": True, "libdca": True})

    # A 5.1 core frame with an LFE channel, dialogue normalization and the optional header fields.
    parts = run(6, "16be", 1024, amode=9, lff=1, dialnorm=7, dynf=1, timef=1, fixed=0, hdcd=1, aspf=1, nch=5, subs=[30] * 5, vqsub=[31] * 5,
                joinx=[0] * 5, thuff=[0] * 5, shuff=[6] * 5, bhuff=[5] * 5, sel=[[1, 3, 3, 3, 3, 7, 7, 7, 7, 7] for _ in range(5)])
    data = b"".join(parts)
    fixture("core-5-1", ".dts", "Six 3/2 core frames with an LFE channel, dialogue normalization 7, dynamic range coefficients, a time stamp and the HDCD flag.", data,
            rows(parts), [stream("DTS", 48000, SURROUND_5_1, "3/2", 6 * 512)], [],
            crosscheck={"ffprobe": True, "decode": True, "libdca": True})

    # Mono at 11.025 kHz, which has the smallest frames.
    parts = run(8, "16be", 256, amode=0, sfreq=6, nblks=7)
    data = b"".join(parts)
    fixture("core-mono-11k", ".dts", "Eight mono core frames of 256 samples at 11.025 kHz.", data, rows(parts),
            [stream("DTS", 11025, ["front center"], "1/0", 8 * 256)], [],
            crosscheck={"ffprobe": True, "decode": True, "libdca": True})

    # CPF set: the header, audio header and subframe CRC words are there.
    parts = run(4, "16be", 512, cpf=1)
    data = b"".join(parts)
    fixture("core-crc-words", ".dts", "Four stereo core frames with CPF set, so the header, audio header and subframe CRC words follow their fields.", data, rows(parts),
            [stream("DTS", 48000, STEREO, "2/0", 4 * 512)], [("dts.crc-present", "CPF is 1")],
            crosscheck={"ffprobe": True, "decode": True, "libdca": True})

    # Core extensions.
    p = m.defaults()
    xch_ext = m.xch(p=p)
    parts = [m.to_form(m.core_frame(size=1024, amode=9, nch=5, ext_id=0, ext=1, extensions=[xch_ext], subs=[30] * 5, vqsub=[31] * 5, joinx=[0] * 5, thuff=[0] * 5,
                                    shuff=[6] * 5, bhuff=[5] * 5, sel=[[1, 3, 3, 3, 3, 7, 7, 7, 7, 7] for _ in range(5)]), "16be") for _ in range(4)]
    data = b"".join(parts)
    fixture("xch", ".dts", "Four 3/2 core frames with an XCh extension for a back center channel (DTS-ES 6.1).", data, rows(parts),
            [stream("DTS-ES", 48000, SURROUND_5_1[:5] + ["back center"], "3/2", 4 * 512)], [],
            crosscheck=cc(libdca_channels=5))
    legacy = m.xch(p=p, legacy=True)
    parts = [m.core_frame(size=1024, amode=9, nch=5, ext_id=0, ext=1, extensions=[legacy], subs=[30] * 5, vqsub=[31] * 5, joinx=[0] * 5, thuff=[0] * 5,
                          shuff=[6] * 5, bhuff=[5] * 5, sel=[[1, 3, 3, 3, 3, 7, 7, 7, 7, 7] for _ in range(5)]) for _ in range(4)]
    data = b"".join(parts)
    fixture("xch-legacy-size", ".dts", "Four 3/2 core frames whose XCh size field gives the extension's size itself, not one less.", data, rows(parts),
            [stream("DTS-ES", 48000, SURROUND_5_1[:5] + ["back center"], "3/2", 4 * 512)], [("dts.extension-size-legacy", "not one less")],
            crosscheck=cc(libdca_channels=5))
    parts = [m.core_frame(size=1024, amode=9, nch=5, ext_id=2, ext=1, extensions=[m.x96(p=m.defaults(amode=9, nch=5))], subs=[30] * 5, vqsub=[31] * 5,
                          joinx=[0] * 5, thuff=[0] * 5, shuff=[6] * 5, bhuff=[5] * 5, sel=[[1, 3, 3, 3, 3, 7, 7, 7, 7, 7] for _ in range(5)], sfreq=13)
             for _ in range(4)]
    data = b"".join(parts)
    fixture("x96", ".dts", "Four 3/2 core frames with an X96 extension, which doubles the sampling frequency to 96 kHz.", data, rows(parts),
            [stream("DTS 96/24", 96000, SURROUND_5_1[:5], "3/2", 8 * 512)], [],
            fields={"DTS/Statistics/Extension Sampling Frequency": "96000 Hz"},
            crosscheck={"ffprobe": True, "decode": True, "libdca": True})
    parts = [m.core_frame(size=1024, amode=9, nch=5, ext_id=6, ext=1, extensions=[m.xxch(chsets=((2, 0x180),), core_mask=0x1F)], subs=[30] * 5,
                          vqsub=[31] * 5, joinx=[0] * 5, thuff=[0] * 5, shuff=[6] * 5, bhuff=[5] * 5, sel=[[1, 3, 3, 3, 3, 7, 7, 7, 7, 7] for _ in range(5)])
             for _ in range(4)]
    data = b"".join(parts)
    fixture("xxch", ".dts", "Four 3/2 core frames with an XXCH extension that adds a pair of rear channels (DTS-ES 7.1, XXCH).", data, rows(parts),
            [stream("DTS-ES", 48000, ["front center", "front left", "front right", "side left", "side right", "back left", "back right"], "3/2", 4 * 512)], [],
            crosscheck=cc(ffprobe=False, decode=False, decode_error="DSYNC check failed", libdca_channels=5, skip={"ffprobe": "ffmpeg's decoder rejects the XXCH stub, whose channel set data is filler (dca_core.c: 'DSYNC check failed'), so ffprobe gets 5 channels from the core and no profile of the extension", "decode": "The XXCH channel set data is filler, which ffmpeg rejects at its DSYNC check."}))
    return build_substreams()


def build_substreams():
    five_one = dict(channels=6, mask=0x0F)
    # Core with an extension substream: lossless, the core component inside.
    def unit(assets, **options):
        return m.core_frame(size=512) + m.exss([dict(a) for a in assets], **options)

    parts = [unit([dict(components=["core", "xll"], sizes={"core": 96, "xll": 64}, channels=2, mask=0x02)]) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-lossless", ".dts", "Four units of a stereo core frame and an extension substream whose asset has a core and a lossless (XLL) component.", data, rows(parts),
            [stream("DTS-HD Master Audio", 48000, STEREO, "2/0", 4 * 512, vbr=True)], [],
            crosscheck=cc_lossless())
    parts = [unit([dict(components=["xbr", "x96", "xxch"], channels=2, mask=0x02, component_options={"xxch": {"core_mask": 0x06}})]) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-high-resolution", ".dts", "Four units of a core frame and an extension substream with XBR, X96 and XXCH components.", data, rows(parts),
            [stream("DTS-HD High Resolution Audio", 48000, STEREO, "2/0", 4 * 512)], [],
            crosscheck=cc(ffprobe_profile=False, decode=False, decode_error="Invalid number of bits for XBR scale factor index", skip={"ffprobe_profile": PROFILE_STUB, "decode": "ffmpeg reads the XBR frame header and its CRC, then rejects the channel set data, which is filler (dca_core.c: 'Invalid number of bits for XBR scale factor index')"}))
    parts = [unit([dict(mode=2, channels=2, mask=0x02)]) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-lbr", ".dts", "Four units of a core frame and an extension substream with a low bit rate (LBR) asset.", data, rows(parts),
            [stream("DTS Express", 48000, STEREO, "2/0", 4 * 512)], [],
            crosscheck=cc(ffprobe_profile=False, skip={"ffprobe_profile": PROFILE_STUB}))
    parts = [m.exss([dict(mode=2, channels=2, mask=0x02)]) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-only", ".dts", "Four extension substreams alone, each with an LBR asset: no core frame.", data, rows(parts),
            [stream("DTS Express", 48000, STEREO, None, 4 * 512)], [],
            crosscheck=cc(ffprobe=False, decode=False, decode_error="No valid DCA sub-stream found", libdca=False, skip={"ffprobe": "ffmpeg's parser reads the sampling frequency and the profile (DTS Express) from the LBR header, but its decoder rejects the substream ('No valid DCA sub-stream found', dcadec.c), so ffprobe gets no channels", "decode": "ffmpeg rejects the LBR stub, whose component is a header followed by filler", "libdca": "libdca reads core frames only."}))
    return build_damage()


def build_damage():
    frame = core()
    cut_note = "ffmpeg's parser keeps the damaged bytes in the packets around them."
    same = [[0, 512], [512, 512], [1024, 512], [1536, 512], [2048, 512], [2560, 512]]

    # A truncated last frame.
    data = frame * 5 + frame[:300]
    fixture("truncated", ".dts", "Six stereo core frames, the last cut after 300 bytes.", data, same[:5] + [[2560, 300]],
            [stream("DTS", 48000, STEREO, "2/0", 6 * 512)], [("dts.frame-truncated", "has 300 bytes")],
            crosscheck={"ffprobe": False, "decode": False, "libdca": False,
                        "skip": {"ffprobe": "ffmpeg drops the truncated frame.", "decode": "ffmpeg drops the truncated frame.", "libdca": "libdca's tool reads whole frames only."}})

    # Junk before, between and after frames.
    junk_bytes = junk(100)
    data = junk_bytes[:30] + frame * 3 + junk_bytes[:50] + frame * 3 + junk_bytes[:20]
    parts = [[30 + i * 512, 512] for i in range(3)] + [[30 + 1536 + 50 + i * 512, 512] for i in range(3)]
    fixture("junk-around", ".dts", "30 bytes of junk, three frames, 50 bytes of junk, three frames, 20 bytes of junk.", data, parts,
            [stream("DTS", 48000, STEREO, "2/0", 6 * 512)],
            [("dts.leading-junk", "30 bytes before the first frame"), ("dts.sync-lost", "Sync is lost for 50 bytes"), ("dts.trailing-data", "20 bytes after")],
            gaps=[(0, 30), (30 + 1536, 50), (len(data) - 20, 20)],
            crosscheck={"ffprobe": False, "decode": True, "libdca": True, "skip": {"ffprobe": "ffmpeg's parser counts the junk into the packets around it."}})

    # A frame whose second half is missing: the next frame's sync word starts inside it.
    data = frame * 2 + frame[:300] + frame * 3
    parts = [[0, 512], [512, 512], [1024, 300], [1324, 512], [1836, 512], [2348, 512]]
    fixture("cut-frame", ".dts", "Six stereo core frames, the third cut to its first 300 bytes.", data, parts, [stream("DTS", 48000, STEREO, "2/0", 6 * 512)],
            [("dts.frame-damaged", "cut short by the next frame's sync word")],
            crosscheck=cc(ffprobe=False, libdca=False, decode_note=CUT_NOTE, skip={"ffprobe": cut_note, "libdca": "libdca's tool reads frames by their sizes, so it does not resync after the cut frame, and the next frame's bytes are the cut frame's missing ones."}))

    # FSIZE too small for the frame to be one: the header is invalid and the bytes are a gap.
    bad = m.core_frame(size=512, fsize=60)
    data = frame * 3 + bad + frame * 2
    parts = [[0, 512], [512, 512], [1024, 512], [2048, 512], [2560, 512]]
    fixture("bad-fsize", ".dts", "Six stereo core frames, the fourth with FSIZE 60, which no frame can have (a frame has at least 96 bytes).", data, parts,
            [stream("DTS", 48000, STEREO, "2/0", 5 * 512)],
            [("dts.header-invalid", "FSIZE is 60"), ("dts.sync-lost", "Sync is lost for 512 bytes")], gaps=[(1536, 512)],
            crosscheck=cc(ffprobe=False, libdca=False, decode=False, decode_errors={"1536": [512, "Invalid core frame size"]}, skip={"decode": "ffmpeg rejects the frame whose FSIZE is 60 (dca_core.c: 'Invalid core frame size')", "ffprobe": cut_note, "libdca": "libdca's tool stops at a frame size it rejects (frame sizes under 96 bytes)."}))

    # FSIZE too large: the frame would run over the next one, which starts a chain inside it.
    long_frame = m.core_frame(size=512, fsize=767)
    data = frame * 3 + long_frame + frame * 2
    fixture("fsize-too-large", ".dts", "Six stereo core frames, the fourth with an FSIZE that reaches 256 bytes into the fifth.", data, same,
            [stream("DTS", 48000, STEREO, "2/0", 6 * 512)], [("dts.frame-damaged", "cut short by the next frame's sync word"), ("dts.rate-exceeded", "768 bytes")],
            crosscheck=cc(ffprobe=False, libdca=False, decode_note=CUT_NOTE, skip={"ffprobe": cut_note, "libdca": "libdca's tool reads frames by their sizes, so the frame with the large FSIZE swallows the next one."}))

    # Reserved and invalid values in the frame header and the audio header.
    parts = run(4, "16be", 512, fixed=1, lff=3, pcmr=7, shuff=[7, 7], bhuff=[7, 7], aspf=1)
    data = b"".join(parts)
    fixture("reserved-values", ".dts", "Four stereo core frames with FixedBit set, LFF 3, PCMR 7 and SHUFF and BHUFF 7.", data, rows(parts),
            [stream("DTS", 48000, STEREO, "2/0", 4 * 512)], [("dts.reserved-value", "reserves")],
            crosscheck=cc(ffprobe=False, decode=False, decode_error="Reserved bit set", libdca=False, skip={"ffprobe": "ffmpeg's decoder rejects the frames ('Reserved bit set': FixedBit), so ffprobe gets no sampling frequency or channels", "decode": "ffmpeg rejects the set FixedBit, the first invalid field it reads (dca.c, ff_dca_parse_core_frame_header)", "libdca": "libdca stops at the invalid tables."}))
    parts = run(4, "16be", 512, amode=20, nch=2)
    data = b"".join(parts)
    fixture("user-defined-arrangement", ".dts", "Four core frames with AMODE 20, a user defined channel arrangement of two channels.", data, rows(parts),
            [stream("DTS", 48000, ["discrete", "discrete"], "user defined", 4 * 512)], [("dts.user-defined-arrangement", "AMODE 20")],
            crosscheck=cc(ffprobe=False, decode=False, decode_error="Unsupported audio channel arrangement (20)", libdca=False, skip={"ffprobe": "ffmpeg's decoder rejects the arrangement, so ffprobe gets no sampling frequency or channels", "decode": "ffmpeg supports the arrangements up to 15 only (dca.c, DCA_AMODE_COUNT)", "libdca": "libdca does not decode user defined arrangements (its flags stop at DCA_CHANNEL_MAX)."}))
    parts = run(4, "16be", 512, rate=30)
    data = b"".join(parts)
    fixture("rate-code", ".dts", "Four stereo core frames with RATE 30 (variable), which Table 5-7 does not list.", data, rows(parts),
            [stream("DTS", 48000, STEREO, "2/0", 4 * 512)], [("dts.rate-code", "RATE is not one of the codes")],
            crosscheck={"ffprobe": True, "decode": True, "libdca": True})
    parts = run(4, "16be", 512, vernum=9, rate=0)
    data = b"".join(parts)
    fixture("revision-and-rate", ".dts", "Four stereo core frames with VERNUM 9 and a targeted rate of 32 kbit/s, far below what the frames use.", data, rows(parts),
            [stream("DTS", 48000, STEREO, "2/0", 4 * 512)], [("dts.revision-incompatible", "revision incompatible"), ("dts.rate-exceeded", "512 bytes")],
            crosscheck={"ffprobe": True, "decode": True, "libdca": True})

    # A stream that changes its configuration: stereo at 48 kHz, then 5.1 at 44.1 kHz.
    first = run(3, "16be", 512)
    second = run(3, "16be", 1024, amode=9, lff=1, sfreq=8, nch=5, subs=[30] * 5, vqsub=[31] * 5, joinx=[0] * 5, thuff=[0] * 5, shuff=[6] * 5, bhuff=[5] * 5,
                 sel=[[1, 3, 3, 3, 3, 7, 7, 7, 7, 7] for _ in range(5)])
    data = b"".join(first + second)
    fixture("stream-changed", ".dts", "Three stereo core frames at 48 kHz, then three 3/2 frames with an LFE channel at 44.1 kHz.", data, rows(first + second),
            [stream("DTS", 48000, STEREO, "2/0", 3 * 512), stream("DTS", 44100, SURROUND_5_1, "3/2", 3 * 512)], [("dts.stream-changed", "The stream changes at 0x600")],
            crosscheck=cc(ffprobe=False, skip={"ffprobe": "ffprobe reports the first stream only and its packets continue across the change."}))

    # Tags around the stream.
    parts = run(5)
    body = b"".join(parts)
    tag2 = id3v2("dtsenc 1.0")
    ape = ape_tag(("Title", "Hand-built"), ("Encoder", "Hand-built"))
    data = tag2 + body + ape + id3v1()
    fixture("tags", ".dts", "An ID3v2 tag, five stereo core frames, an APEv2 tag and an ID3v1 tag.", data, rows(parts, len(tag2)),
            [stream("DTS", 48000, STEREO, "2/0", 5 * 512)], [("dts.encoder-claim", "APE Encoder item")],
            fields={"DTS/Encoder": "dtsenc 1.0"},
            regions=[["ID3v2", 0, len(tag2)], ["DTS", len(tag2), len(body)], ["APE", len(tag2) + len(body), len(ape)], ["ID3v1", len(data) - 128, 128]],
            crosscheck=cc(libdca=False, ffprobe_packets=[[47 + 512 * i, 512] for i in range(4)] + [[47 + 4 * 512, 754]],
                          ffprobe_packets_reason="ffmpeg's last packet takes the APEv2 and ID3v1 tags that end the file into it",
                          skip={"libdca": "libdca's tool does not skip tags."}))
    return build_invalid_fields()


def channels(n, **options):
    """Options for a frame that codes n channels whatever its arrangement says."""
    return dict(nch=n, subs=[30] * n, vqsub=[31] * n, joinx=[0] * n, thuff=[0] * n, shuff=[6] * n, bhuff=[5] * n,
                sel=[[1, 3, 3, 3, 3, 7, 7, 7, 7, 7] for _ in range(n)], **options)


def build_invalid_fields():
    same6 = [[i * 512, 512] for i in range(6)]
    parts = [core(ftype=1, short=5), core(nblks=19), core(subfs=3), core(**channels(3, amode=2)), core(subs=[31, 31]), core(joinx=[1, 0])]
    data = b"".join(parts)
    fixture("invalid-core-fields", ".dts", "Six stereo core frames, each with one invalid field: SHORT 5 in a normal frame, 20 blocks, 4 subframes for 16 blocks, "
            "3 primary channels for 2/0, 33 active subbands, and a joint source that comes after its channel.", data, same6,
            [stream("DTS", 48000, STEREO, "2/0", 5 * 512 + 640, vbr=True)],
            [("dts.frame-type", "SHORT is 5"), ("dts.block-count", "20 blocks"), ("dts.subframe-count", "4 subframes"), ("dts.channel-count", "3 primary channels"),
             ("dts.subband-count", "33 active subbands"), ("dts.joint-source", "channel 1 copies channel 1")],
            crosscheck=cc(ffprobe_packets=False, decode=False, decode_errors=INVALID_FIELDS_ERRORS, libdca=False, skip={"ffprobe_packets": "ffmpeg's parser takes a sync word as a frame only when the bits after it are those of a normal frame, FTYPE 1 and SHORT 31 (dca_parser.c, IS_CORE_MARKER), so it skips the first frame and reports the first packet at position 0", "decode": "ffmpeg rejects five of the six frames, each with its own error (decode_errors), and accepts the joint source after its channel", "libdca": "libdca reads the frames with 3 primary channels, 33 subbands and the others as it can; its reading of invalid fields says nothing"}))
    # An extension the header announces and the frame does not hold.
    parts = run(4, "16be", 512, ext_id=0, ext=1)
    data = b"".join(parts)
    fixture("extension-missing", ".dts", "Four stereo core frames whose header says an XCh extension follows, with no sync word where its size would put it.", data,
            rows(parts), [stream("DTS", 48000, STEREO, "2/0", 4 * 512)], [("dts.extension-missing", "XCh")],
            crosscheck=cc(decode=False, decode_error="XCH sync word not found", skip={"decode": "ffmpeg looks for the XCh sync word as the frame's header says and rejects the frame without it (dca_core.c: 'XCH sync word not found')"}))
    return build_substream_damage()


def build_substream_damage():
    def unit(assets, core_size=512, **options):
        return m.core_frame(size=core_size) + m.exss([dict(a) for a in assets], **options)

    asset = dict(components=["core", "xll"], sizes={"core": 96, "xll": 64}, channels=2, mask=0x02)
    name = "DTS-HD Master Audio"

    parts = [unit([asset], bad_crc=True) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-bad-crc", ".dts", "Four units whose extension substream header CRC is wrong.", data, rows(parts),
            [stream(name, 48000, STEREO, "2/0", 4 * 512, vbr=True)], [("dts.crc-mismatch", "CRC")],
            crosscheck=cc_lossless(decode=False, decode_error="Invalid EXSS header checksum", skip={"decode": "ffmpeg checks the extension substream header's CRC16 and rejects it (dca_exss.c: 'Invalid EXSS header checksum'); its parser names the profile from the asset descriptor without that check (dca_parser.c)"}))
    broken = dict(asset, bad_sync=["xll"])
    parts = [unit([broken]) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-bad-component-sync", ".dts", "Four units whose lossless component does not start with its sync word.", data, rows(parts),
            [stream(name, 48000, STEREO, "2/0", 4 * 512, vbr=True)], [("dts.component-sync", "sync word")],
            crosscheck=cc(ffprobe_profile=False, skip={"ffprobe_profile": PROFILE_STUB}))
    two = [dict(components=["core", "xll"], sizes={"core": 96, "xll": 64}, channels=2, mask=0x02, index=0),
           dict(mode=2, channels=2, mask=0x02, index=1)]
    parts = [unit(two) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-two-assets", ".dts", "Four units whose extension substream holds two assets: a core with a lossless component, and a low bit rate one.", data, rows(parts),
            [stream(name, 48000, STEREO, "2/0", 4 * 512, vbr=True)], [],
            fields={"DTS/Statistics/Audio Assets": "2 assets"},
            crosscheck=cc(ffprobe_profile=False, skip={"ffprobe_profile": PROFILE_STUB}))
    parts = [unit([asset], wide=True) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-wide-header", ".dts", "Four units whose extension substream uses the wide header form (12-bit header size, 20-bit sizes).", data, rows(parts),
            [stream(name, 48000, STEREO, "2/0", 4 * 512, vbr=True)], [],
            crosscheck=cc_lossless())
    aux = dict(mode=3, sizes={"aux": 32}, data_pad=32, channels=2, mask=0x02)
    parts = [unit([aux]) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-auxiliary-asset", ".dts", "Four units whose extension substream holds an asset with coding mode 3, auxiliary.", data, rows(parts),
            [stream("DTS", 48000, STEREO, "2/0", 4 * 512)], [("dts.coding-mode-auxiliary", "coding mode 3")],
            crosscheck=cc(ffprobe_profile=False, skip={"ffprobe_profile": "ffmpeg reads no component of the auxiliary asset and leaves the profile DTS (dca_exss.c, coding mode 3 sets no extension mask)"}))
    parts = [unit([dict(asset, size_field=4)]) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-descriptor-overrun", ".dts", "Four units whose asset descriptor gives a size of 4 bytes, less than its fields take.", data, rows(parts),
            [stream(name, 48000, STEREO, "2/0", 4 * 512, vbr=True)], [("dts.asset-descriptor-overrun", "4 bytes")],
            crosscheck=cc(ffprobe_profile=False, decode=False, decode_error="Read past end of EXSS asset descriptor", skip={"ffprobe_profile": PROFILE_STUB, "decode": "ffmpeg rejects the descriptor whose fields run past its size (dca_exss.c: 'Read past end of EXSS asset descriptor')"}))
    parts = [m.core_frame(size=512) + m.exss([dict(asset)], size=72)[:72] for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-sizes-overrun", ".dts", "Four units whose extension substream is 72 bytes long, less than its header and the sizes of its assets' components add up to.", data,
            rows(parts), [stream(name, 48000, STEREO, "2/0", 4 * 512, vbr=True)], [("dts.substream-overrun", "more than the substream's")],
            crosscheck=cc(ffprobe_profile=False, decode=False, decode_error="EXSS asset out of bounds", skip={"ffprobe_profile": PROFILE_STUB, "decode": "ffmpeg rejects the assets' sizes that add up to more than the substream (dca_exss.c: 'EXSS asset out of bounds')"}))
    short_core = m.core_frame(size=512, sync=m.SYNC_EXSS_CORE)[:20]
    stub = dict(components=["core"], sizes={"core": 20}, raw={"core": short_core}, channels=2, mask=0x02)
    parts = [unit([stub]) for _ in range(4)]
    data = b"".join(parts)
    fixture("exss-core-header-overrun", ".dts", "Four units whose extension substream holds a core component of 20 bytes, so the core frame ends inside its header.", data, rows(parts),
            [stream("DTS", 48000, STEREO, "2/0", 4 * 512)], [("dts.header-overrun", "core frame ends inside its header")],
            crosscheck=cc(ffprobe_profile=False, skip={"ffprobe_profile": PROFILE_STUB}))
    # The last substream declares more bytes than the file has.
    whole = unit([asset])
    over = (m.core_frame(size=512) + m.exss([dict(asset)], size=2000))[:512 + 150]
    data = whole * 3 + over
    fixture("exss-overrun", ".dts", "Three units and a fourth whose extension substream declares 2,000 bytes but the file ends 150 bytes into it.", data,
            [[i * len(whole), len(whole)] for i in range(3)] + [[3 * len(whole), len(data) - 3 * len(whole)]],
            [stream(name, 48000, STEREO, "2/0", 4 * 512, vbr=True)], [("dts.frame-truncated", "The last frame")],
            crosscheck=cc_lossless(decode=False, decode_errors={**{str(i * len(whole)): None for i in range(3)}, str(3 * len(whole)): "Packet too short for EXSS frame"}, skip={"decode": "the last packet is cut inside its extension substream, which ffmpeg rejects (dca_exss.c: 'Packet too short for EXSS frame'); the packets before it decode"}))
    return build_mp4()


def sample_rows(track, chunks):
    rows_out = []
    for chunk, start in zip(track.chunks(), chunks[0]):
        at = start
        for sample in chunk:
            rows_out.append([at, len(sample)])
            at += len(sample)
    return rows_out


def build_mp4():
    frames = [m.core_frame(size=512) for _ in range(6)]
    skip = {"libdca": "libdca reads raw DTS only."}

    def movie(ddts, samples=frames):
        entry = mp4.mp4a(2, 48000, mp4.box("ddts", ddts), format=b"dtsc")
        track = mp4.Track(1, samples, 48000, 2, entry, durations=[512] * len(samples))
        data, boxes, chunks = mp4.movie([track])
        return data, [[p, o, s] for p, o, s in boxes], sample_rows(track, chunks)

    data, boxes, rows_ = movie(m.ddts(48000, 384000, 384000, duration=0, construction=1, core_layout=2, core_size=512))
    fixture("mp4-dtsc", ".mp4", "Six stereo core frames in MP4 with a dtsc sample entry and a ddts box that matches them.", data, rows_,
            [stream("DTS", 48000, STEREO, "2/0", 6 * 512)], [], fmt="MP4", boxes=boxes,
            fields={"MP4/moov/trak/mdia/minf/stbl/stsd/dtsc/ddts/Core Size": "512 bytes"},
            crosscheck=cc(libdca=False, skip=skip))
    data, boxes, rows_ = movie(m.ddts(96000, 1000000, 500000, depth=16, duration=1, construction=5, core_lfe=1, core_layout=9, core_size=700, reserved=3))
    fixture("mp4-ddts-differs", ".mp4", "Six stereo core frames in MP4 whose ddts box gives 96 kHz, another frame duration, stream construction, LFE flag, layout and size, and sets reserved bits.",
            data, rows_, [stream("DTS", 48000, STEREO, "2/0", 6 * 512)],
            [("dts.config-differs", "DTSSamplingFrequency"), ("dts.config-differs", "FrameDuration"), ("dts.config-differs", "StreamConstruction"),
             ("dts.config-differs", "CoreLFEPresent"), ("dts.config-differs", "CoreLayout"), ("dts.config-differs", "CoreSize"), ("dts.config-differs", "avgBitrate"),
             ("dts.config-reserved", "Reserved bits")],
            fmt="MP4", boxes=boxes, crosscheck=cc(libdca=False, skip=skip))
    data, boxes, rows_ = movie(m.ddts(48000, 384000, 384000, construction=1, core_layout=2, core_size=512)[:12])
    fixture("mp4-ddts-truncated", ".mp4", "Six stereo core frames in MP4 whose ddts box ends after 12 bytes, inside its fields.", data, rows_,
            [stream("DTS", 48000, STEREO, "2/0", 6 * 512)], [("dts.config-truncated", "ends inside a field")], fmt="MP4", boxes=boxes,
            crosscheck=cc(libdca=False, skip=skip))
    samples = [frames[0] + bytes(range(1, 9))] + frames[1:]
    data, boxes, rows_ = movie(m.ddts(48000, 384000, 384000, core_layout=2, core_size=512), samples)
    fixture("mp4-sample-not-frame", ".mp4", "Six stereo core frames in MP4, the first sample followed by eight bytes that are no frame.", data,
            [[o, 512] for o, _ in rows_], [stream("DTS", 48000, STEREO, "2/0", 6 * 512)], [("dts.sample-not-frame", "no core frame")],
            gaps=[(rows_[0][0] + 512, 8)], fmt="MP4", boxes=boxes, crosscheck=cc(libdca=False, ffprobe_packets=[list(r) for r in rows_],
                          ffprobe_packets_reason="an MP4 file's packets are its samples, here the frame and the 8 bytes after it",
                          skip=skip))
    return build_review()


def derive(source, form, name, what):
    """The fixture `source` with every frame written in another form of the stream (m.to_form), under a new name: the same findings, streams and
    cross-checks, the frames at the offsets the form's lengths give."""
    data, manifest = FIXTURES[source + ".dts"]
    parts = [m.to_form(data[offset:offset + length], form) for offset, length in manifest["frames"]]
    assert not manifest["gaps"]
    derived = copy.deepcopy(manifest)
    derived["description"] = f"{manifest['description'].rstrip('.')}, written in {what}."
    derived["frames"] = rows(parts)
    derived["length"] = sum(len(part) for part in parts)
    derived["crosscheck"].pop("decode_errors", None)
    if form.startswith("14") and any(offset % 4 for offset, _ in derived["frames"]):
        derived["crosscheck"]["libdca"] = False
        derived["crosscheck"].setdefault("skip", {})["libdca"] = "libdca (dca_frames, 0.0.7) does not find a 14-bit frame that starts at an offset that is not a multiple of 4: its bitstream reader works on 32-bit words"
    FIXTURES[name + ".dts"] = (b"".join(parts), derived)


FORM_NAMES = {"16le": "16-bit words, little endian", "14be": "14-bit words, big endian", "14le": "14-bit words, little endian"}


def build_review():
    five = dict(components=["core", "xll"], sizes={"core": 96, "xll": 64}, channels=2, mask=0x02)
    name = "DTS-HD Master Audio"

    def unit(assets, core_size=512, **options):
        return m.core_frame(size=core_size) + m.exss([dict(a) for a in assets], **options)

    # Sizes that end inside a 16-bit word (7.5.1 aligns only what follows a core frame to 32 bits).
    for form, label, size, frame in (("16be", "core-odd-size-16be", 1001, 1001), ("16le", "core-odd-size-16le", 1001, 1002),
                                     ("14be", "core-odd-size-14be", 1000, 1144), ("14le", "core-odd-size-14le", 1000, 1144)):
        parts = [core(form, size) for _ in range(3)]
        assert all(len(part) == frame for part in parts)
        fixture(label, ".dts", f"Three stereo core frames whose FSIZE + 1 is {size}, which does not end on a whole {'16-bit' if form.startswith('16') else '14-bit'} word, in {FORM_NAMES.get(form, '16-bit words, big endian')}: each takes {frame} bytes in the file.",
                b"".join(parts), rows(parts), [stream("DTS", 48000, STEREO, "2/0", 3 * 512)], [], crosscheck=cc())

    # 7.5.1: one to three null bytes align the next sync word to 32 bits.
    parts = [m.core_frame(size=1001) + bytes(3) for _ in range(3)]
    fixture("core-padded", ".dts", "Three stereo core frames of 1,001 bytes, each followed by the three null bytes that align the next sync word to 32 bits.", b"".join(parts), rows(parts),
            [stream("DTS", 48000, STEREO, "2/0", 3 * 512)], [], crosscheck=cc())
    parts = [m.core_frame(size=1001) + bytes(3) + m.exss([dict(five)]) for _ in range(3)]
    parts = [part + bytes(-len(part) % 4) for part in parts]
    fixture("exss-padded", ".dts", "Three units of a core frame of 1,001 bytes, three null bytes, a lossless extension substream and the null bytes that align the next sync word.", b"".join(parts), rows(parts),
            [stream(name, 48000, STEREO, "2/0", 3 * 512, vbr=True)], [],
            crosscheck=cc_lossless())
    parts = [m.core_frame(size=1001) + m.exss([dict(five)]) for _ in range(3)]
    fixture("exss-misaligned", ".dts", "Three units of a core frame of 1,001 bytes followed at once by an extension substream, whose sync word is then not on a 32-bit boundary.", b"".join(parts), rows(parts),
            [stream("DTS", 48000, STEREO, "2/0", 3 * 512)], [("dts.substream-misaligned", "3 bytes before the boundary")],
            crosscheck=cc())

    # An extension substream's sync word after the last frame, with a header that states 4,096 bytes and a CRC16 that fails.
    header = ((0x64582025 << 43) | (1 << 32) | (4095 << 20) | 0xFFFFF) << 5
    flood = header.to_bytes(10, "big") + bytes(6)
    frames = [m.core_frame(size=512) for _ in range(3)]
    data = b"".join(frames) + flood * 2
    fixture("trailing-substream-sync", ".dts", "Three stereo core frames, then two extension substream sync words with headers that state 4,096 bytes and fail their CRC16.", data, rows(frames),
            [stream("DTS", 48000, STEREO, "2/0", 3 * 512)], [("dts.trailing-data", "32 bytes after the last frame")], gaps=[(1536, 32)],
            crosscheck=cc(ffprobe=False, decode=False, skip={"ffprobe": "ffmpeg's parser takes the bytes after the last frame into its last packet",
                                                              "decode": "ffmpeg's last packet holds the bytes after the last frame, which its decoder reads as an extension substream and rejects for its header CRC16 (dca_exss.c: 'Invalid EXSS header checksum')"}))

    # Findings on groups of fields, whose nodes differ between the forms.
    parts = [unit([dict(five, asset_size=60)]) for _ in range(2)]
    fixture("exss-assets-overrun", ".dts", "Two units whose asset has a size of 60 bytes, less than its core and lossless components add up to.", b"".join(parts), rows(parts),
            [stream(name, 48000, STEREO, "2/0", 2 * 512, vbr=True)], [("dts.substream-overrun", "asset 1's components add up to 160 bytes, more than the 60")],
            crosscheck=cc(ffprobe_profile=False, decode=False, decode_error="Invalid extension size in EXSS asset descriptor", skip={"ffprobe_profile": PROFILE_STUB, "decode": "ffmpeg rejects the asset whose size is less than its components (dca_exss.c: 'Invalid extension size in EXSS asset descriptor')"}))
    stub = dict(components=["core", "xll"], sizes={"core": 96, "xll": 8}, raw={"xll": m.SYNC_XLL.to_bytes(4, "big") + bytes(4)}, channels=2, mask=0x02)
    parts = [unit([stub]) for _ in range(2)]
    fixture("exss-component-header-overrun", ".dts", "Two units whose lossless component is its sync word and four bytes, so the data ends inside its header.", b"".join(parts), rows(parts),
            [stream(name, 48000, STEREO, "2/0", 2 * 512, vbr=True)], [("dts.header-overrun", "ends inside the XLL component's header")],
            crosscheck=cc(ffprobe_profile=False, decode=False, decode_error="Invalid XLL common header checksum", skip={"ffprobe_profile": PROFILE_STUB, "decode": XLL_STUB}))
    parts = [m.core_frame(size=1024, amode=9, nch=5, ext_id=6, ext=1, extensions=[m.xxch_overrun()], subs=[30] * 5, vqsub=[31] * 5, joinx=[0] * 5, thuff=[0] * 5,
                          shuff=[6] * 5, bhuff=[5] * 5, sel=[[1, 3, 3, 3, 3, 7, 7, 7, 7, 7] for _ in range(5)]) for _ in range(2)]
    fixture("xxch-header-overrun", ".dts", "Two 3/2 core frames with an XXCH extension of 12 bytes, whose header CRC16 holds and whose fields need more bits than it has.", b"".join(parts), rows(parts),
            [stream("DTS-ES", 48000, SURROUND_5_1[:5], "3/2", 2 * 512)], [("dts.header-overrun", "ends inside the XXCH extension's header")],
            crosscheck=cc(ffprobe_profile=False, libdca_channels=5, skip={"ffprobe_profile": "ffmpeg's decoder names the profile DTS-ES only when it has decoded the extension (dca_core.c), which is cut short here"}))

    # A bad CRC16 in the header of XXCH in the core, of XBR in a substream.
    ext_options = dict(amode=9, lff=2, ext=1, ext_id=6)
    p = m.defaults(amode=9, lff=2)
    parts = [m.core_frame(size=1024, extensions=[m.xxch(chsets=((2, 0x180),), core_mask=0x3F, bad_crc=(i == 1), crc_chset=True, p=p)], **ext_options) for i in range(3)]
    fixture("xxch-bad-crc", ".dts", "Three 3/2 core frames with an LFE channel and an XXCH extension whose frame header CRC16 fails in the second.", b"".join(parts), rows(parts),
            [stream("DTS-ES", 48000, SURROUND_5_1_WITH_REAR, "3/2", 3 * 512)],
            [("dts.crc-mismatch", "XXCH frame header's CRC16")], fields={"DTS/Statistics/Frames Failing a CRC": "1 frame"},
            crosscheck=cc(ffprobe=False, decode=False, decode_errors={"0": "DSYNC check failed", "1024": "XXCH sync word not found", "2048": "DSYNC check failed"}, libdca_channels=6,
                          skip={"ffprobe": "ffmpeg's decoder rejects the XXCH stub, whose channel set data is filler (dca_core.c: 'DSYNC check failed')",
                                "decode": "The XXCH channel set data is filler, which ffmpeg rejects at its DSYNC check; the frame whose header CRC16 fails is not an XXCH frame to it at all ('XXCH sync word not found'), where the analyzer keeps the extension and reports dts.crc-mismatch"}))
    parts = [m.core_frame(size=512) + m.exss([dict(components=["xbr"], channels=2, mask=0x02, component_options={"xbr": {"bad_crc": True}})]) for _ in range(3)]
    fixture("xbr-bad-crc", ".dts", "Three units whose extension substream has an XBR component with a frame header CRC16 that fails.", b"".join(parts), rows(parts),
            [stream("DTS-HD High Resolution Audio", 48000, STEREO, "2/0", 3 * 512)], [("dts.crc-mismatch", "XBR frame header's CRC16")], fields={"DTS/Statistics/Frames Failing a CRC": "3 frames"},
            crosscheck=cc(ffprobe_profile=False, decode=False, decode_error="Invalid XBR frame header checksum", skip={"ffprobe_profile": PROFILE_STUB, "decode": "ffmpeg rejects the XBR frame header whose CRC16 fails (dca_core.c: 'Invalid XBR frame header checksum')"}))

    # Profile precedence and the delivered sampling frequency.
    parts = [unit([dict(components=["lbr", "xll"], channels=2, mask=0x02)]) for _ in range(3)]
    fixture("exss-lbr-and-xll", ".dts", "Three units of a core frame and an extension substream whose asset has both a low bit rate (LBR) and a lossless (XLL) component.", b"".join(parts), rows(parts),
            [stream("DTS Express", 48000, STEREO, "2/0", 3 * 512, vbr=True)], [],
            crosscheck=cc_lossless(ffprobe_profile=False, skip={"ffprobe_profile": "ffmpeg's parser names a core with an extension substream by its XLL component first (dca_parser.c), so ffprobe reads DTS-HD MA where the analyzer names the stream by LBR first, as ffmpeg's decoder does (dcadec.c), which the stub LBR component does not allow it to confirm"}))
    parts = [unit([dict(five, max_rate=96000)]) for _ in range(3)]
    fixture("exss-lossless-96k", ".dts", "Three units of a 48 kHz core frame and an extension substream whose lossless asset names 96 kHz as its highest sampling frequency.", b"".join(parts), rows(parts),
            [stream(name, 96000, STEREO, "2/0", 3 * 1024, vbr=True)], [], fields={"DTS/Statistics/Sampling Frequency": "48000 Hz", "DTS/Statistics/Extension Sampling Frequency": "96000 Hz"},
            crosscheck=cc_lossless())

    # RATE 31, which 5.6 uses for lossless streams; termination frames in every form.
    parts = run(4, "16be", 512, rate=31)
    fixture("rate-lossless", ".dts", "Four stereo core frames with RATE 31, which Table 5-7 does not list and 5.6 uses to select the lossless step-size table.", b"".join(parts), rows(parts),
            [stream("DTS", 48000, STEREO, "2/0", 4 * 512)], [("dts.rate-lossless", "RATE is 31")], crosscheck={"ffprobe": True, "decode": True, "libdca": True})
    for form, size in (("16be", 512), ("16le", 512), ("14be", 504), ("14le", 504)):
        parts = [core(form, size) for _ in range(3)] + [core(form, size, ftype=0, short=10, nblks=7)]
        label = {"16be": "16-bit words, big endian"}.get(form, FORM_NAMES.get(form))
        fixture(f"termination-{form}", ".dts", f"Three stereo core frames and a termination frame of 8 blocks, in {label}.", b"".join(parts), rows(parts),
                [stream("DTS", 48000, STEREO, "2/0", 3 * 512 + 256, vbr=True)], [], fields={"DTS/Statistics/Termination Frames": "1 frame"},
                crosscheck=cc(ffprobe_packets=False, decode_errors={str(rows(parts)[3][0]): "Deficit samples are not supported"}, decode=False, libdca=False,
                              skip={"ffprobe_packets": "ffmpeg's parser takes a sync word as a frame only when the bits after it are those of a normal frame, FTYPE 1 and SHORT 31 (dca_parser.c, IS_CORE_MARKER), so the termination frame is part of the packet before it",
                                    "decode": "ffmpeg's decoder rejects termination frames in every form (dca.c: 'Deficit samples are not supported')",
                                    "libdca": "libdca's sync search reads FTYPE 1 and SHORT 31 in the 14-bit forms and its tool stops at a frame it rejects"}))

    # Table 5-4's arrangements above 9, which FFmpeg and libdca reject; the labels rest on the table alone.
    nine = "ffmpeg (dca.c, DCA_AMODE_COUNT is 10: 'Unsupported audio channel arrangement') and libdca (arrangements up to 9) reject AMODE 10 to 15; the channels and layout in this manifest rest on ETSI TS 102 114 Table 5-4 alone, with no tool to confirm them"
    layouts = {
        10: ("4/2", ["front left of center", "front right of center", "front left", "front right", "back left", "back right"]),
        11: ("3/2 (overhead)", ["front center", "front left", "front right", "back left", "back right", "top center"]),
        12: ("3/3", ["front center", "back center", "front left", "front right", "back left", "back right"]),
        13: ("5/2", ["front left of center", "front center", "front right of center", "front left", "front right", "back left", "back right"]),
        14: ("4/4", ["front left of center", "front right of center", "front left", "front right", "discrete", "discrete", "discrete", "discrete"]),
        15: ("5/3", ["front left of center", "front center", "front right of center", "front left", "front right", "back left", "back center", "back right"]),
    }
    for amode, (mode, layout) in layouts.items():
        parts = run(2, "16be", 1024, amode=amode)
        fixture(f"amode-{amode}", ".dts", f"Two core frames with AMODE {amode}, which Table 5-4 lists with {len(layout)} channels.", b"".join(parts), rows(parts),
                [stream("DTS", 48000, layout, mode, 2 * 512)], [], crosscheck=cc(ffprobe=False, decode=False, libdca=False, skip={"ffprobe": nine, "decode": nine, "libdca": nine}))

    # The same damage in the little endian and 14-bit forms, where a group of fields covers other bytes than a field.
    for source in ("exss-lossless", "exss-bad-crc", "exss-bad-component-sync", "exss-descriptor-overrun", "exss-sizes-overrun", "exss-core-header-overrun",
                   "exss-two-assets", "exss-assets-overrun", "exss-component-header-overrun", "exss-auxiliary-asset", "exss-high-resolution"):
        derive(source, "16le", source.replace("exss-", "le16-exss-"), FORM_NAMES["16le"])
    for source in ("xch", "x96", "xxch", "extension-missing", "xxch-header-overrun"):
        for form in ("16le", "14be", "14le"):
            derive(source, form, f"{form}-{source}", FORM_NAMES[form])
    return None


def main():
    build()
    import dts_lossless_fixtures
    api = dict(fixture=fixture, rows=rows, cc=cc, stream=stream)
    dts_lossless_fixtures.build(api)
    dts_lossless_fixtures.build_dtshd(api)
    OUT.mkdir(parents=True, exist_ok=True)
    for name, (data, manifest) in FIXTURES.items():
        (OUT / name).write_bytes(data)
        stem = name.rsplit(".", 1)[0]
        (OUT / (stem + ".expected.json")).write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(FIXTURES)} fixtures written to {OUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
