#!/usr/bin/env python3
"""Writes the hand-built AC-3 and E-AC-3 fixtures into tests/fixtures/ac3, with the standard library
only, so CI regenerates them and fails on any difference.

The frames come from ac3_model.py: every field written as ATSC A/52:2018 lays it out, the exponents
sent once with every SNR offset 0, so no mantissa takes a bit and decoders play near silence. The
fixtures cover what encoders do not write: damaged CRCs, a truncated and a cut frame, junk before,
between and after frames, bsid 9 and 10, Annex D's alternate syntax with reserved values, reserved
values and a reserved dialnorm in the bit stream information, inconsistent and invalid audio blocks,
E-AC-3 with two independent substreams and a dependent one that makes 7.1, a dependent substream
before any independent one, frame exponent strategies and fewer blocks per frame, spectral extension,
the adaptive hybrid transform and enhanced coupling (read up to there), a reduced sample rate, and
MP4 files whose dac3 disagrees with the frames and whose dec3 lists a dependent substream.

The object audio fixtures (build_object_audio, with emdf_model.py) add E-AC-3 frames with the extension
flag in addbsi and EMDF containers carrying OAMD and JOC payloads: joc-atmos in the layout of Dolby's
own JOC test file, containers after the last block, in the auxiliary data and in skip fields, dec3
boxes that match, lack, clear or misstate the extension, frames whose audio blocks are not read,
random bytes that announce nothing, a flag and a payload that disagree, and damaged containers.

Each <name>.expected.json manifest lists what the analyzer must report: the file's length and
format, every frame and gap (offset and length), the streams with their codec, rate, channels,
channel mode and layout, samples and whether the bitrate varies, fields by node path, exactly the
findings by code with a part of their message, and what crosscheck_ac3_fixtures.py checks with
ffprobe, ffmpeg and liba52, with a reason for each check it skips.
"""

import json
import pathlib
import random
import sys

import ac3_model as m
import emdf_model as emdf
import mp4_boxes as mp4

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "ac3"

LRS_RRS = 0x8000 >> 6


def stream(codec, rate, acmod, lfeon, samples, layout=None, vbr=False, mode=None, objects=None):
    speakers = layout or m.speakers(acmod, lfeon)
    return {"codec": codec, "sample_rate": rate, "channels": len(speakers), "channel_mode": mode or m.CODING_MODES[acmod], "channel_layout": speakers,
            "samples": samples, "is_vbr": vbr, **({"has_object_audio": True, "joc_object_count": objects} if objects else {})}


def frames_of(data, start=0, end=None):
    """Frames from start to end, one after another."""
    rows = []
    at = start
    end = len(data) if end is None else end
    while at < end:
        h = m.header(data, at)
        rows.append([at, min(h["length"], end - at)])
        at += h["length"]
    return rows


def packets(data, rows):
    """Where ffmpeg's parser starts a packet: at each AC-3 frame and each E-AC-3 independent frame of substream 0."""
    out = []
    for offset, length in rows:
        h = m.header(data, offset)
        if out and h["eac3"] and (h["dependent"] or h["substream"] != 0):
            out[-1][1] += length
        else:
            out.append([offset, length])
    return out


FIXTURES = {}


def round_half_even(numerator, denominator):
    quotient, rest = divmod(numerator, denominator)
    return quotient + (1 if rest * 2 > denominator or (rest * 2 == denominator and quotient % 2) else 0)


def coding_blocks(acmod, blocks=6, blksw=None, remat=None, cpl=None, spx=None, spx_range=(2, 2), chbwcod=0):
    """What each audio block of a frame chooses, from the options the frame was built with and by the rules of ATSC A/52:2018, not from
    the bits: blksw of any full bandwidth channel (5.4.3.1); coupling in use, which the last cplstre with its cplinu sets (5.4.3.7, 5.4.3.8);
    rematrixing, where the flags of the last rematstr (5.4.3.19, 5.4.3.20) have a band set in a 2/0 frame; spectral extension in use
    (Annex E 2.3.3.2); and the end bin of the highest full bandwidth channel or coupling channel, 37 + 3 * (chbwcod + 12) for a channel
    that codes its own bandwidth, 37 + 12 * cplbegf for a coupled channel, 25 + 12 * its begin sub-band for one in spectral extension
    (7.2.2.1; Annex E 3.3.3), and 37 + 12 * (cplendf + 3) for the coupling channel."""
    nf = m.NFCHANS[acmod]
    widths = [chbwcod] * nf if isinstance(chbwcod, int) else list(chbwcod)
    begin = spx_range[0] + 2 if spx_range[0] < 6 else spx_range[0] * 2 - 3
    rows, flags, state, use = [], 0, None, False
    for block in range(blocks):
        if cpl is not None and cpl[block] is not None:
            state = None if cpl[block] is False else tuple(cpl[block])
        if spx is not None and spx[block] is not None:
            use = bool(spx[block])
        if remat is not None and remat[block] is not None:
            flags = remat[block]
        ends = [37 + 12 * state[0] if state else 25 + 12 * begin if use else 37 + 3 * (widths[ch] + 12) for ch in range(nf)]
        if state:
            ends.append(37 + 12 * (state[1] + 3))
        rows.append({"switched": bool(blksw and any(blksw[block])), "coupled": state is not None, "rematrixed": acmod == 2 and flags != 0,
                     "spectral": use, "end": max(ends)})
    return rows


def coding_unit(rate, frames, spectral):
    """The expected values of a program frame: its frames' blocks one after another."""
    rows = [row for frame in frames for row in frame]
    unit = {"switching": [r["switched"] for r in rows], "coupling": [r["coupled"] for r in rows], "rematrixing": [r["rematrixed"] for r in rows]}
    if spectral:
        unit["spectral"] = [r["spectral"] for r in rows]
    unit["bandwidth"] = round_half_even(max(r["end"] for r in rows) * rate, 512)
    return unit

# The block end bits of every frame built, by its bytes, so a manifest lists them for the frames a
# fixture holds unchanged.
ENDS = {}


def ac3(**options):
    ends = []
    frame = m.ac3_frame(ends=ends, **options)
    # The analyzer reads no audio blocks of bsid 9 and 10 and stops at a bandwidth code above 60.
    widest = options.get("chbwcod", 0)
    if options.get("bsid", 8) <= 8 and (max(widest) if isinstance(widest, list) else widest) <= 60:
        ENDS[frame] = ends
    return frame


def eac3(**options):
    ends = []
    frame = m.eac3_frame(ends=ends, **options)
    # Nor does it read past the adaptive hybrid transform, enhanced coupling or at a reduced rate.
    if options.get("fscod", 0) != 3 and not options.get("ecpl") and not options.get("aht_channel"):
        ENDS[frame] = ends
    return frame


def block_ends(data, rows, complete=True):
    ends = {}
    for offset, length in rows:
        frame = data[offset:offset + length]
        if complete and frame in ENDS:
            ends[str(offset)] = ENDS[frame]
    return ends


def fixture(name, extension, description, data, frames, streams, findings, fields=None, gaps=(), crosscheck=None, fmt=None, boxes=None, read=True, coding=None):
    ends = block_ends(data, frames) if read else {}
    FIXTURES[name + extension] = (data, {
        "description": description,
        "length": len(data),
        "format": fmt or ("AC-3"),
        "frames": frames,
        "gaps": [list(g) for g in gaps],
        "streams": streams,
        "fields": fields or {},
        "findings": [list(f) for f in findings],
        "crosscheck": crosscheck or {},
        **({"boxes": boxes} if boxes else {}),
        **({"block_ends": ends} if ends else {}),
        **({"coding": coding} if coding else {}),
    })


# The hand-built frames' SNR offsets are 0, so no mantissa takes a bit (bap 0, A/52 7.2.2.7): a
# skip field moved within the last block changes no sample. The encoder-made and derived fixtures
# run the control.
ZERO_ALLOCATION = "The frames' SNR offsets are 0, so no mantissa takes a bit and a moved skip field in the last block changes no sample."


def silent(count, **options):
    return b"".join(ac3(**options) for _ in range(count))


def build():
    # Clean 2/0 frames with metadata in the bit stream information and a dynamic range word.
    frames = [ac3(dialnorm=24, compr=0xEF, audprod=(25, 1), timecod1=(13 << 9) | (45 << 3) | 2, timecod2=(3 << 11) | (12 << 6) | 7,
                          addbsi=b"Encspect", dynrng=0x14) for _ in range(8)]
    data = b"".join(frames)
    fixture("bsi-fields", ".ac3", "Eight 2/0 frames with dialnorm 24, compr, production information, both time codes, addbsi and a dynrng word in block 0.",
            data, frames_of(data), [stream("AC-3", 48000, 2, 0, 8 * 1536)], [],
            fields={"AC-3/Frame/Bit Stream Information/Dialogue Normalization": "-24 dB",
                    "AC-3/Frame/Bit Stream Information/Compression Gain": "-6.30 dB",
                    "AC-3/Frame/Bit Stream Information/Mixing Level": "105 dB SPL",
                    "AC-3/Frame/Bit Stream Information/Room Type": "1 (large room, X curve monitor)",
                    "AC-3/Frame/Bit Stream Information/Time Code 1": "13:45:16",
                    "AC-3/Frame/Bit Stream Information/Additional Info": "45 6E 63 73 70 65 63 74",
                    "AC-3/Frame/Audio Block/Dynamic Range Gain": "+4.22 dB",
                    "AC-3/Frame/Sync Information/CRC1/Check": "verified",
                    "AC-3/Frame/Error Check/CRC2/Check": "verified"},
            crosscheck={"ffprobe": True, "decode": True, "liba52": True})

    # CRC damage: a bit flipped in the first 5/8 (crc1 and crc2 fail) and in the last 3/8 (crc2 only).
    frames = [bytearray(ac3()) for _ in range(8)]
    frames[2][40] ^= 0x10
    frames[5][700] ^= 0x01
    data = b"".join(bytes(f) for f in frames)
    fixture("crc-damaged", ".ac3", "Eight 2/0 frames; frame 3 has a bit flipped in its first 5/8, frame 6 one in its last 3/8.",
            data, frames_of(data), [stream("AC-3", 48000, 2, 0, 8 * 1536)],
            [("ac3.crc1-mismatch", "fails in 1 frame"), ("ac3.crc2-mismatch", "fails in 2 frames")],
            fields={"AC-3/Frame[2]/Sync Information/CRC1/Check": "mismatch", "AC-3/Frame[5]/Sync Information/CRC1/Check": "verified",
                    "AC-3/Frame[5]/Error Check/CRC2/Check": "mismatch"},
            crosscheck={"ffprobe": True, "decode": True, "liba52": True, "damaged": [len(frames[0]) * 2, len(frames[0]) * 5]})

    # A truncated last frame.
    data = silent(6)[:-300]
    fixture("truncated", ".ac3", "Six 2/0 frames, the last cut 300 bytes short.", data, frames_of(data), [stream("AC-3", 48000, 2, 0, 6 * 1536)],
            [("ac3.frame-truncated", "of its 768 bytes")],
            crosscheck={"ffprobe": False, "decode": False, "liba52": False,
                        "skip": {"ffprobe": "ffmpeg drops the truncated frame.", "decode": "ffmpeg drops the truncated frame.",
                                 "liba52": "liba52's tool reads whole frames only."}})

    # Junk before, between and after frames.
    frame = ac3()
    junk = bytes((i * 37 + 11) % 251 for i in range(100))
    data = junk[:30] + frame * 3 + junk[:50] + frame * 3 + junk[:20]
    rows = frames_of(data, 30, 30 + 3 * 768) + frames_of(data, 30 + 3 * 768 + 50, len(data) - 20)
    fixture("junk-around", ".ac3", "30 bytes of junk, three frames, 50 bytes of junk, three frames, 20 bytes of junk.", data, rows,
            [stream("AC-3", 48000, 2, 0, 6 * 1536)],
            [("ac3.leading-junk", "30 bytes before the first frame"), ("ac3.sync-lost", "Sync is lost for 50 bytes"), ("ac3.trailing-data", "20 bytes after")],
            gaps=[(0, 30), (30 + 3 * 768, 50), (len(data) - 20, 20)],
            crosscheck={"ffprobe": False, "decode": True, "liba52": True,
                        "skip": {"ffprobe": "ffmpeg's parser counts the junk into the packets around it."}})

    # Leading junk, then E-AC-3 frames whose unused tail holds what looks like an ADTS header of the
    # frame's own length: the headers chain from frame to frame, as the bytes of a real E-AC-3 header
    # do in csi_miami_stereo_128_spx.eac3. The chain of CRC-checked E-AC-3 frames is the stronger
    # evidence (it starts first and its frames pass their CRCs), so the file is E-AC-3, not AAC.
    adts = bytes.fromhex("FFF14C80401FFC")
    frames = []
    for _ in range(8):
        frame = bytearray(eac3(acmod=2, words=256))
        assert not any(frame[300:307])
        frame[300:307] = adts
        frames.append(bytes(m.fix_crc2(frame)))
    junk = bytes((i * 37 + 11) % 251 for i in range(100))
    assert b"\xFF" not in junk
    data = junk + b"".join(frames)
    data_adts = data
    fixture("junk-adts-in-frames", ".eac3", "100 bytes of junk, then eight 2/0 E-AC-3 frames of 512 bytes, each with the bytes of an ADTS header of 512 bytes at its offset 300.",
            data, frames_of(data, 100), [stream("E-AC-3", 48000, 2, 0, 8 * 1536)], [("ac3.leading-junk", "100 bytes before the first frame")],
            gaps=[(0, 100)], read=False,
            crosscheck={"ffprobe": False, "decode": True, "liba52": False,
                        "skip": {"ffprobe": "ffmpeg's parser counts the junk into the packets around it.",
                                 "liba52": "liba52 does not decode E-AC-3."}})

    # The same file with its first 110 bytes cut off: the junk and the start of the first frame are gone, so the ADTS look-alike in that
    # partial frame (at offset 290) starts before the first whole frame (at 502). Both chains are class 50 and overlap; the AC-3 chain
    # wins because its seven frames pass their CRCs and the look-alike's do not exist.
    cut = data_adts[110:]
    fixture("junk-adts-in-frames-cut", ".eac3", "The file junk-adts-in-frames with its first 110 bytes cut off: the partial first frame holds the ADTS look-alike, which starts before the seven whole E-AC-3 frames.",
            cut, frames_of(cut, 502), [stream("E-AC-3", 48000, 2, 0, 7 * 1536)], [("ac3.leading-junk", "502 bytes before the first frame")],
            gaps=[(0, 502)], read=False,
            crosscheck={"ffprobe": False, "decode": True, "liba52": False,
                        "skip": {"ffprobe": "ffmpeg's parser counts the leading bytes into the packets around it.",
                                 "liba52": "liba52 does not decode E-AC-3."}})

    # The same with an MPEG header: AC-3 frames of 768 bytes whose unused tail holds the bytes of a
    # CRC-protected MPEG-2.5 Layer I header of 768 bytes, which chain from frame to frame as in
    # monsters_inc_2.0_192.ac3 with junk in front. Their CRCs do not hold, so MPEG audio ranks the
    # chain 30 and the AC-3 chain, class 50, wins.
    mpeg = bytes.fromhex("FFE6C5BA")
    frames = []
    for _ in range(8):
        frame = bytearray(ac3())
        assert not any(frame[500:504]) and len(frame) == 768
        frame[500:504] = mpeg
        frames.append(bytes(m.fix_crc2(m.fix_crc1(frame))))
    junk = bytes((i * 37 + 11) % 251 for i in range(100))
    assert b"\xFF" not in junk
    data = junk + b"".join(frames)
    fixture("junk-mpeg-in-frames", ".ac3", "100 bytes of junk, then eight 2/0 AC-3 frames of 768 bytes, each with the bytes of a protected MPEG-2.5 Layer I header of 768 bytes at its offset 500.",
            data, frames_of(data, 100), [stream("AC-3", 48000, 2, 0, 8 * 1536)], [("ac3.leading-junk", "100 bytes before the first frame")],
            gaps=[(0, 100)], read=False,
            crosscheck={"ffprobe": False, "decode": True, "liba52": True,
                        "skip": {"ffprobe": "ffmpeg's parser counts the junk into the packets around it."}})

    # A frame whose second half is missing: the next frame's sync word starts inside it.
    frame = ac3()
    data = frame * 2 + frame[:400] + frame * 3
    rows = [[0, 768], [768, 768], [1536, 400], [1936, 768], [2704, 768], [3472, 768]]
    fixture("cut-frame", ".ac3", "Six 2/0 frames, the third cut to its first 400 bytes.", data, rows, [stream("AC-3", 48000, 2, 0, 6 * 1536)],
            [("ac3.frame-damaged", "cut short by the next frame's sync word")],
            crosscheck={"ffprobe": False, "decode": True, "liba52": False,
                        "skip": {"ffprobe": "ffmpeg's parser keeps the cut frame's bytes in a packet with the next frame.",
                                 "liba52": "liba52's tool reads frames by their sizes, so it does not resync after the cut frame."},
                        "damaged": [1536]})

    # The configuration changes twice: 2/0, mono, 2/0. Each run is a stream.
    data = silent(2) + silent(2, acmod=1) + silent(2)
    fixture("stream-changes", ".ac3", "Two 2/0 frames, two mono frames, two 2/0 frames: the configuration changes twice and each run is its own stream.", data, frames_of(data),
            [stream("AC-3", 48000, 2, 0, 2 * 1536), stream("AC-3", 48000, 1, 0, 2 * 1536), stream("AC-3", 48000, 2, 0, 2 * 1536)],
            [("ac3.stream-changed", "from AC-3, 48000 Hz, 2/0 to AC-3, 48000 Hz, 1/0; the streams change 2 times")],
            crosscheck={"ffprobe": False, "decode": False, "liba52": True,
                        "skip": {"ffprobe": "ffprobe reports the first stream's parameters only.",
                                 "decode": "ffmpeg's decoder takes the first frame's channel layout and drops the frames that differ."}})

    # bsid 9 and 10: AC-3 syntax at half and a quarter of the sample rate, which A/52 does not define.
    data = silent(4, bsid=9, frmsizecod=10)
    fixture("bsid-9", ".ac3", "Four frames with bsid 9, which ffmpeg and liba52 decode at half the rate.", data, frames_of(data),
            [stream("AC-3", 24000, 2, 0, 4 * 1536)], [("ac3.bsid-undefined", "bsid 9 or 10")],
            crosscheck={"ffprobe": True, "decode": True, "liba52": False,
                        "skip": {"liba52": "The frames' audio blocks are not read, so there are no block positions to confirm."}})
    data = silent(4, bsid=10, frmsizecod=10)
    fixture("bsid-10", ".ac3", "Four frames with bsid 10, which ffmpeg and liba52 decode at a quarter of the rate.", data, frames_of(data),
            [stream("AC-3", 12000, 2, 0, 4 * 1536)], [("ac3.bsid-undefined", "bsid 9 or 10")],
            crosscheck={"ffprobe": True, "decode": True, "liba52": False,
                        "skip": {"liba52": "The frames' audio blocks are not read, so there are no block positions to confirm."}})

    # Annex D: 3/2 + LFE with both extra bit stream information fields, a reserved Lt/Rt surround
    # level and nonzero xbsi2 bits.
    data = silent(4, bsid=6, acmod=7, lfeon=1, frmsizecod=28, xbsi1=(1, 3, 1, 4, 5), xbsi2=(2, 0, 1, 0x5A, 1))
    fixture("annex-d", ".ac3", "Four 3/2 + LFE frames with bsid 6: xbsi1 with a reserved Lt/Rt surround level, xbsi2 with reserved bits set.",
            data, frames_of(data), [stream("AC-3", 48000, 7, 1, 4 * 1536)], [("ac3.reserved-value", "reserves")],
            fields={"AC-3/Frame/Bit Stream Information/Extended Information 1/Preferred Downmix": "1 (Lt/Rt downmix preferred)",
                    "AC-3/Frame/Bit Stream Information/Extended Information 1/Lt/Rt Center Mix Level": "-1.5 dB",
                    "AC-3/Frame/Bit Stream Information/Extended Information 1/Lt/Rt Surround Mix Level": "1 (reserved)",
                    "AC-3/Frame/Bit Stream Information/Extended Information 2/Dolby Surround EX Mode": "2 (Dolby Surround EX or Dolby Pro Logic IIx encoded)",
                    "AC-3/Frame/Bit Stream Information/Extended Information 2/A/D Converter Type": "1 (HDCD)"},
            crosscheck={"ffprobe": True, "decode": True, "liba52": True})

    # dmixmod 3: reserved in Table D2.2, though encoders (Dolby's among them) write it for Dolby Pro Logic II.
    data = silent(4, bsid=6, acmod=7, lfeon=1, frmsizecod=28, xbsi1=(3, 2, 4, 2, 4))
    fixture("dmixmod-reserved", ".ac3", "Four 3/2 + LFE frames with bsid 6 whose xbsi1 sets dmixmod 3, which Table D2.2 reserves.",
            data, frames_of(data), [stream("AC-3", 48000, 7, 1, 4 * 1536)], [("ac3.dmixmod-reserved", "dmixmod is 3")],
            fields={"AC-3/Frame/Bit Stream Information/Extended Information 1/Preferred Downmix": "3 (reserved)"},
            crosscheck={"ffprobe": True, "decode": True, "liba52": True})

    # Reserved values in the bit stream information of a 3/2 frame.
    data = silent(4, acmod=7, frmsizecod=28, cmixlev=3, surmixlev=3, dialnorm=0, langcod=0x41, audprod=(10, 3))
    fixture("reserved-values", ".ac3", "Four 3/2 frames with the reserved cmixlev, surmixlev and roomtyp, dialnorm 0 and a language code.",
            data, frames_of(data), [stream("AC-3", 48000, 7, 0, 4 * 1536)],
            [("ac3.reserved-value", "reserves"), ("ac3.dialnorm-reserved", "dialnorm is 0"), ("ac3.language-code", "langcod is not 0xFF")],
            fields={"AC-3/Frame/Bit Stream Information/Center Mix Level": "3 (reserved)",
                    "AC-3/Frame/Bit Stream Information/Dialogue Normalization": "0 (reserved, read as -31 dB)"},
            crosscheck={"ffprobe": True, "decode": True, "liba52": True})

    # Inconsistent and invalid blocks: rematstr 0 in block 0, then a bandwidth code above 60.
    data = silent(3, rematstr0=0) + silent(3, chbwcod=61)
    fixture("bad-blocks", ".ac3", "Three 2/0 frames without rematrixing flags in block 0, three with chbwcod 61.", data, frames_of(data),
            [stream("AC-3", 48000, 2, 0, 6 * 1536)],
            [("ac3.block-inconsistent", "rematstr is 0 in block 0"), ("ac3.block-invalid", "chbwcod is above 60")],
            crosscheck={"ffprobe": True, "decode": False, "liba52": False,
                        "skip": {"decode": "ffmpeg rejects the frames with chbwcod 61, as A/52 5.4.3.24 says to.",
                                 "liba52": "liba52 reads chbwcod 61 past the bins it has, so its block ends say nothing."}})

    # E-AC-3: independent substream 0 (3/2 + LFE) with a dependent substream that adds the rear
    # surround pair (7.1), and independent substream 1 (2/0).
    unit = eac3(acmod=7, lfeon=1, words=400, bsmod=0) + eac3(strmtyp=1, acmod=2, chanmap=LRS_RRS, words=160) \
        + eac3(substream=1, acmod=2, words=200, bsmod=2)
    data = unit * 4
    seven_one = [m.FL, m.FC, m.FR, m.SL, m.SR, m.LFE, m.BL, m.BR]
    fixture("substreams", ".eac3", "Four access units of independent substream 0 (3/2 + LFE), a dependent substream with the rear surround pair, and "
            "independent substream 1 (2/0).", data, frames_of(data),
            [stream("E-AC-3", 48000, 7, 1, 4 * 1536, layout=seven_one), stream("E-AC-3", 48000, 2, 0, 4 * 1536)], [],
            fields={"AC-3/Frame[1]/Bit Stream Information/Channel Map": "0x0200 (left/right rear surround)",
                    "AC-3/Frame[1]/Bit Stream Information/Stream Type": "1 (dependent)",
                    "AC-3/Statistics[1]": "independent substream 1"},
            crosscheck={"ffprobe": True, "ffprobe_packets": "substream 0", "ffprobe_fields": False, "ffprobe_bitrate": False, "decode": True, "skip_insertion": False,
                        "skip": {"skip_insertion": "ffmpeg decodes only substream 0, so a change to substream 1 would not show.",
                                 "ffprobe_fields": "FFmpeg 8.0.1's AC-3 parser no longer takes a frame of substream 1 as the start of a packet: its header parser returns an error "
                                                   "for any substream ID but 0 (libavcodec/ac3_parser.c:133, eac3_parse_header), so ac3_sync does not find it and the frame joins "
                                                   "the packet before it, and the parser cannot read that packet's sample rate or channels (ffprobe: 0 Hz, 2 channels). It lists "
                                                   "four packets, each starting at a frame of substream 0 and holding the frames up to the next; 6.1 listed one more for each frame "
                                                   "of substream 1. The stream is valid E-AC-3 (ETSI TS 102 366 E.1.2.1 allows substream IDs 0 to 7).",
                                 "ffprobe_bitrate": "ffprobe gives no bitrate (N/A) for the stream it cannot read the parameters of."}})

    # A dependent substream before any independent one.
    data = eac3(strmtyp=1, acmod=2, chanmap=LRS_RRS, words=160) + eac3(acmod=2, words=200) * 3
    fixture("dependent-first", ".eac3", "A dependent frame, then three independent 2/0 frames.", data, frames_of(data),
            [stream("E-AC-3", 48000, 2, 0, 3 * 1536)], [("ac3.dependent-without-independent", "before any independent substream")],
            crosscheck={"ffprobe": True, "ffprobe_bitrate": False, "decode": True, "damaged": [0],
                        "skip": {"ffprobe_bitrate": "ffmpeg gives the bitrate of the first frame it decodes, not of the dependent frame before it."}})

    # Three dependent frames of substream 0 after each independent frame: a program frame holds each dependent substream once, so the
    # second and third are left out of the figures (Annex E 2.3.1.2).
    unit = eac3(acmod=2, words=200) + eac3(strmtyp=1, acmod=2, chanmap=LRS_RRS, words=160) * 3
    data = unit * 3
    fixture("dependent-repeated", ".eac3", "Three access units of an independent 2/0 frame and three dependent frames, all of dependent substream 0, with the rear surround pair.",
            data, frames_of(data), [stream("E-AC-3", 48000, 2, 0, 3 * 1536, layout=[m.FL, m.FR, m.BL, m.BR])],
            [("ac3.dependent-excess", "6 dependent frames are left out of the figures and statistics after a dependent substream ID repeats")],
            crosscheck={"ffprobe": False, "decode": False,
                        "skip": {"ffprobe": "The stream breaks Annex E 2.3.1.2, so what ffmpeg's parser makes of the repeated substream IDs says nothing about the figures.",
                                 "decode": "ffmpeg decodes the repeated dependent substreams as it likes; the check says nothing about the figures."}})

    # E-AC-3 with two blocks per frame and frame exponent strategies, mono; and the same rate with
    # six blocks and expstre 0.
    data = eac3(acmod=1, numblkscod=1, words=64, bsmod=0) * 6
    fixture("two-blocks", ".eac3", "Six mono frames of two blocks each.", data, frames_of(data), [stream("E-AC-3", 48000, 1, 0, 6 * 512)], [],
            fields={"AC-3/Frame/Bit Stream Information/Blocks Code": "2 blocks"},
            crosscheck={"ffprobe": True, "decode": True, "skip_insertion": True, "skip_control": False, "skip": {"skip_control": ZERO_ALLOCATION}})
    data = eac3(acmod=2, expstre=0, words=200) * 4
    fixture("frame-exponents", ".eac3", "Four 2/0 frames whose exponent strategies come from frmchexpstr (Table E2.10).", data, frames_of(data),
            [stream("E-AC-3", 48000, 2, 0, 4 * 1536)], [],
            fields={"AC-3/Frame/Audio Frame/Frame Exponent Strategy (left)": "0 (D15, R, R, R, R, R)"},
            crosscheck={"ffprobe": True, "decode": True, "skip_insertion": True, "skip_control": False, "skip": {"skip_control": ZERO_ALLOCATION}})

    # Spectral extension, and coupling in E-AC-3.
    data = eac3(acmod=2, spx=True, words=200) * 4
    fixture("spectral-extension", ".eac3", "Four 2/0 frames with spectral extension in both channels from sub-band 4 to 7.", data, frames_of(data),
            [stream("E-AC-3", 48000, 2, 0, 4 * 1536)], [],
            fields={"AC-3/Frame/Audio Block/Spectral Extension Begin Code": "2 (sub-band 4)"},
            crosscheck={"ffprobe": True, "decode": True, "skip_insertion": True, "skip_control": False, "skip": {"skip_control": ZERO_ALLOCATION}})
    data = eac3(acmod=7, cplinu=True, words=320) * 4
    fixture("coupling", ".eac3", "Four 3/2 frames with coupling from sub-band 3 in every block.", data, frames_of(data),
            [stream("E-AC-3", 48000, 7, 0, 4 * 1536)], [],
            crosscheck={"ffprobe": True, "decode": True, "skip_insertion": True, "skip_control": False, "skip": {"skip_control": ZERO_ALLOCATION}})

    build_coding()

    # Where the analyzer stops: the adaptive hybrid transform, enhanced coupling, a reduced rate.
    data = eac3(acmod=2, ahte=1, aht_channel=1, words=200) * 3
    fixture("aht", ".eac3", "Three 2/0 frames whose left channel uses the adaptive hybrid transform; the blocks are read up to its mantissas.",
            data, frames_of(data), [stream("E-AC-3", 48000, 2, 0, 3 * 1536)], [("ac3.aht-not-read", "adaptive hybrid transform")],
            crosscheck={"ffprobe": True, "decode": False, "skip": {"decode": "The frames hold no valid AHT mantissas, so decoders play noise or reject them."}})
    data = eac3(acmod=7, cplinu=True, ecpl=True, words=320) * 3
    fixture("enhanced-coupling", ".eac3", "Three 3/2 frames with enhanced coupling in block 0; the blocks are read up to it.", data, frames_of(data),
            [stream("E-AC-3", 48000, 7, 0, 3 * 1536)], [("ac3.enhanced-coupling-not-read", "enhanced coupling")],
            crosscheck={"ffprobe": True, "ffprobe_bitrate": False, "decode": False,
                        "skip": {"decode": "ffmpeg does not decode enhanced coupling.", "ffprobe_bitrate": "ffmpeg gives no bitrate for frames it cannot decode."}})
    data = eac3(acmod=2, fscod=3, fscod2=0, words=128) * 3
    fixture("reduced-rate", ".eac3", "Three 2/0 frames at 24 kHz (fscod2 0).", data, frames_of(data), [stream("E-AC-3", 24000, 2, 0, 3 * 1536)],
            [("ac3.reduced-rate-not-read", "reduced sample rate")],
            crosscheck={"ffprobe": True, "ffprobe_fields": "channels", "ffprobe_bitrate": False, "decode": False,
                        "skip": {"ffprobe_fields": "FFmpeg 8.0.1's AC-3 parser takes the sample rate from the header (24000 Hz, ac3_parser.c, eac3_parse_header) and the packets from the frame "
                                                   "sizes, but leaves the channels of an E-AC-3 stream to the E-AC-3 decoder (aac_ac3_parser.c:132), which refuses a reduced sample rate "
                                                   "(eac3dec.c:316-322, AVERROR_PATCHWELCOME): ffprobe gives 0 channels and no channel layout.",
                                 "ffprobe_bitrate": "ffprobe gives no bitrate (N/A): the bit rate comes from the decoder that refuses the stream (aac_ac3_parser.c:163-167, eac3dec.c:316-322).",
                                 "decode": "FFmpeg 8.0.1's E-AC-3 decoder refuses a reduced sample rate (eac3dec.c:316-322, AVERROR_PATCHWELCOME)."}})

    # MP4: a dac3 box that disagrees with the frames, and a dec3 box with a dependent substream.
    frames = [ac3() for _ in range(6)]
    entry = mp4.mp4a(2, 48000, mp4.box("dac3", m.silent_dac3(0, 8, 0, 7, 1, 14, reserved=3)), format=b"ac-3")
    track = mp4.Track(1, frames, 48000, 2, entry, durations=[1536] * 6)
    data, boxes, chunks = mp4.movie([track])
    fixture("dac3-differs", ".mp4", "Six 2/0 frames in MP4 whose dac3 box gives 3/2 + LFE at 384 kbps and sets its reserved bits.", data,
            sample_rows(track, chunks), [stream("AC-3", 48000, 2, 0, 6 * 1536)],
            [("ac3.config-differs", "gives acmod 7"), ("ac3.config-differs", "gives lfeon 1"), ("ac3.config-differs", "gives bit_rate_code 14"),
             ("ac3.config-reserved", "Reserved bits")],
            fmt="MP4", boxes=[[p, o, s] for p, o, s in boxes],
            crosscheck={"ffprobe": True, "decode": True})
    # A sample that ends inside its frame.
    frames = [ac3()[:700]] + [ac3() for _ in range(5)]
    entry = mp4.mp4a(2, 48000, mp4.box("dac3", m.silent_dac3(0, 8, 0, 2, 0, 10)), format=b"ac-3")
    track = mp4.Track(1, frames, 48000, 2, entry, durations=[1536] * 6)
    data, boxes, chunks = mp4.movie([track])
    fixture("sample-short", ".mp4", "Six 2/0 frames in MP4 with a matching dac3 box, the first sample 68 bytes short of its frame.", data,
            sample_rows(track, chunks), [stream("AC-3", 48000, 2, 0, 6 * 1536)],
            [("ac3.frame-truncated", "A sample ends inside its frame")], fmt="MP4", boxes=[[p, o, s] for p, o, s in boxes], read=False,
            crosscheck={"ffprobe": False, "decode": False,
                        "skip": {"ffprobe": "ffmpeg's AC-3 parser drops the sample that ends inside its frame, so ffprobe lists five packets where the file has six samples.",
                                 "decode": "ffmpeg drops the short sample's frame."}})
    unit = eac3(acmod=7, lfeon=1, words=400, bsmod=0) + eac3(strmtyp=1, acmod=2, chanmap=LRS_RRS, words=160)
    samples = [unit] * 4
    rate = (len(unit) * 8 * 48000 // 1536) // 1000
    entry = mp4.mp4a(2, 48000, mp4.box("dec3", m.dec3(rate, [(0, 16, 0, 0, 7, 1, 1, 0x002)])), format=b"ec-3")
    track = mp4.Track(1, samples, 48000, 8, entry, durations=[1536] * 4)
    data, boxes, chunks = mp4.movie([track])
    fixture("dec3-dependent", ".mp4", "Four samples of a 3/2 + LFE substream and a dependent substream with the rear pair, in MP4 with a matching dec3 box.",
            data, sample_rows(track, chunks, split=True), [stream("E-AC-3", 48000, 7, 1, 4 * 1536, layout=seven_one)], [], fmt="MP4", boxes=[[p, o, s] for p, o, s in boxes],
            fields={"MP4/moov/trak/mdia/minf/stbl/stsd/ec-3/dec3/Channel Locations": "0x002 (left/right rear surround)",
                    "MP4/moov/trak/mdia/minf/stbl/stsd/ec-3/dec3/Data Rate": f"{rate} kbps"},
            crosscheck={"ffprobe": True, "decode": True})


def build_object_audio():
    """E-AC-3 with object audio (ETSI TS 103 420): EMDF containers in skip fields, the unused bits
    after the last block and the auxiliary data, the addbsi extension and dec3's extension, and what
    breaks each. joc-atmos follows the layout of Dolby's own JOC test file (DD+ Online Delivery Kit
    v1.4.1, AVSync_1280x720p_25fps_h264_6ch_640kbps_ddp_joc.mp4): a 5.1 frame whose block 2 holds the
    container at the start of its skip field."""
    seven_one = [m.FL, m.FC, m.FR, m.SL, m.SR, m.LFE, m.BL, m.BR]
    five_one_core = lambda **options: eac3(acmod=7, lfeon=1, words=400, bsmod=0, **options)

    def dependent(tail=None, addbsi=None, skips=None):
        return eac3(strmtyp=1, acmod=2, chanmap=LRS_RRS, words=160, tail=tail, addbsi=addbsi, skips=skips)

    def atmos_frame():
        return eac3(acmod=7, lfeon=1, words=1024, bsmod=0, addbsi=emdf.addbsi(16), skips={2: atmos})

    # 5.1 as Dolby writes it: the container (OAMD with 16 objects of which one is the LFE channel, JOC
    # with 15 objects and the 90 degree phase shifted downmix, protection of 32 and 8 bits) fills the
    # skip field of block 2, and the frame's addbsi sets the extension with complexity index 16.
    atmos = emdf.atmos_container()
    data = atmos_frame() * 4
    fixture("joc-atmos", ".eac3", "Four 5.1 frames laid out like Dolby's JOC test file: block 2 holds an EMDF container with an OAMD payload of 16 objects "
            "(one of them the LFE channel) and a JOC payload of 15 objects in its skip field, and the addbsi sets the extension with complexity index 16.",
            data, frames_of(data), [stream("E-AC-3", 48000, 7, 1, 4 * 1536, objects=15)], [],
            fields={"AC-3/Frame[1]/Bit Stream Information/Additional Info/HasExtensionTypeA": "true",
                    "AC-3/Frame[1]/Bit Stream Information/Additional Info/Complexity Index Type A": "16",
                    "AC-3/Frame[1]/Audio Block[2]/Skip Field": f"{len(atmos)} bytes",
                    "AC-3/Frame[1]/Audio Block[2]/Skip Field/EMDF Container/Container Length": f"{len(atmos) - 4} bytes",
                    "AC-3/Frame[1]/Audio Block[2]/Skip Field/EMDF Container/Payload[0]/Payload ID": "11 (object audio metadata)",
                    "AC-3/Frame[1]/Audio Block[2]/Skip Field/EMDF Container/Payload[0]/Object Count": "16 objects",
                    "AC-3/Frame[1]/Audio Block[2]/Skip Field/EMDF Container/Payload[1]/Payload ID": "14 (joint object coding)",
                    "AC-3/Frame[1]/Audio Block[2]/Skip Field/EMDF Container/Payload[1]/Payload Size": "320 bytes",
                    "AC-3/Frame[1]/Audio Block[2]/Skip Field/EMDF Container/Payload[1]/JOC Header/JOC Object Count": "15 objects",
                    "AC-3/Frame[1]/Audio Block[2]/Skip Field/EMDF Container/Protection/Primary Protection Length": "2 (32 bits)",
                    "AC-3/Frame[1]/Audio Block[2]/Skip Field/EMDF Container/Protection/Secondary Protection Length": "1 (8 bits)",
                    "AC-3/Statistics/Frames With Object Audio": "4 frames",
                    "AC-3/Statistics/JOC Object Count": "15 objects"},
            crosscheck={"ffprobe": True, "decode": True, "skip_insertion": False,
                        "skip": {"skip_insertion": "The frames already hold skip fields, which the rewrite does not combine with its own."}})

    # The file ends inside block 2's skip field, which fits its frame: the frame is truncated, not overrun.
    cut = 3 * len(atmos_frame()) + 264
    fixture("truncated-in-skip", ".eac3", "Three 5.1 frames of joc-atmos and a fourth cut in the middle of block 2's skip field.", data[:cut], frames_of(data[:cut]),
            [stream("E-AC-3", 48000, 7, 1, 4 * 1536, vbr=True, objects=15)], [("ac3.frame-truncated", "of its 2048 bytes")],
            crosscheck={"ffprobe": False, "decode": False, "skip_insertion": False,
                        "skip": {"ffprobe": "ffmpeg drops the truncated frame.", "decode": "ffmpeg drops the truncated frame.",
                                 "skip_insertion": "The frames already hold skip fields, which the rewrite does not combine with its own."}})

    # The same frames in MP4, with dec3 carrying the extension (TS 103 420 C.3.1).
    unit = atmos_frame()
    def mp4_of(name, description, box, findings, units_=4, unit_=unit, fields=None, objects=15, acmod=7, lfeon=1, channels=6):
        samples = [unit_] * units_
        entry = mp4.mp4a(2, 48000, mp4.box("dec3", box((len(unit_) * 8 * 48000 // 1536) // 1000)), format=b"ec-3")
        track = mp4.Track(1, samples, 48000, channels, entry, durations=[1536] * units_)
        data_, boxes, chunks = mp4.movie([track])
        fixture(name, ".mp4", description, data_, sample_rows(track, chunks, split=True), [stream("E-AC-3", 48000, acmod, lfeon, units_ * 1536, objects=objects)],
                findings, fmt="MP4", boxes=[[p, o, s] for p, o, s in boxes], fields=fields or {},
                crosscheck={"ffprobe": True, "decode": True})

    base = [(0, 16, 0, 0, 7, 1, 0, 0)]
    mp4_of("joc-dec3", "The frames of joc-atmos in MP4 with a dec3 box that sets flag_ec3_extension_type_a and complexity index 16.",
           lambda r: m.dec3(r, base, extension=(1, 16)), [],
           fields={"MP4/moov/trak/mdia/minf/stbl/stsd/ec-3/dec3/HasExtensionTypeA": "true",
                   "MP4/moov/trak/mdia/minf/stbl/stsd/ec-3/dec3/Complexity Index Type A": "16"})
    mp4_of("joc-dec3-absent", "The frames of joc-atmos in MP4 whose dec3 box ends after its substreams.",
           lambda r: m.dec3(r, base), [("ac3.config-differs", "ends after its substreams")])
    mp4_of("joc-dec3-clear", "The frames of joc-atmos in MP4 whose dec3 box clears flag_ec3_extension_type_a.",
           lambda r: m.dec3(r, base, extension=(0, 0)), [("ac3.config-differs", "gives flag_ec3_extension_type_a 0")])
    mp4_of("joc-dec3-complexity", "The frames of joc-atmos in MP4 whose dec3 box gives complexity index 12, where the frames give 16.",
           lambda r: m.dec3(r, base, extension=(1, 12)), [("ac3.config-differs", "gives complexity_index_type_a 12")])
    mp4_of("joc-dec3-unmatched", "Four 5.1 frames without object audio in MP4 whose dec3 box sets flag_ec3_extension_type_a and has reserved bits set.",
           lambda r: m.dec3(r, base, extension=(1, 16, 0x55)), [("ac3.config-differs", "gives flag_ec3_extension_type_a 1"), ("ac3.config-reserved", "Reserved bits")],
           unit_=eac3(acmod=7, lfeon=1, words=1024, bsmod=0), objects=None)

    # Frames whose audio blocks are not read (the adaptive hybrid transform) still carry the flag and the
    # container, which sits in the bits after block 0, and dec3 agrees with them.
    aht = eac3(acmod=2, ahte=1, aht_channel=1, words=300, addbsi=emdf.addbsi(4), tail=emdf.object_audio(objects=4, size=24))
    mp4_of("joc-aht", "Four 2/0 frames in MP4 whose left channel uses the adaptive hybrid transform, so their audio blocks are not read, with the extension flag "
           "and a container for 4 objects after block 0, and a dec3 box that sets the extension with complexity index 4.",
           lambda r: m.dec3(r, [(0, 16, 0, 0, 2, 0, 0, 0)], extension=(1, 4)), [("ac3.aht-not-read", "adaptive hybrid transform")], unit_=aht, objects=4, acmod=2,
           lfeon=0, channels=2,
           fields={"MP4/moov/trak/mdia/minf/stbl/stsd/ec-3/dec3/Complexity Index Type A": "4"})
    fixtures_aht = FIXTURES["joc-aht.mp4"][1]
    fixtures_aht["crosscheck"] = {"ffprobe": True, "decode": False,
                                  "skip": {"decode": "The frames hold no valid AHT mantissas, so decoders play noise or reject them."}}

    # 7.1: a 5.1 core and a dependent substream with the rear surround pair; the container follows the
    # dependent substream's last block, and only that substream sets the extension (TS 103 420 8.2).
    container = emdf.object_audio(objects=15)
    data = (five_one_core() + dependent(tail=container, addbsi=emdf.addbsi(16))) * 4
    fixture("joc-after-blocks", ".eac3", "Four access units of a 5.1 substream and a dependent substream with the rear surround pair: the dependent frames hold "
            "an EMDF container for 15 objects in the bits after their last block and set the addbsi extension.",
            data, frames_of(data), [stream("E-AC-3", 48000, 7, 1, 4 * 1536, layout=seven_one, objects=15)], [],
            fields={"AC-3/Frame[1]/Auxiliary Data/EMDF Container/Container Length": f"{len(container) - 4} bytes",
                    "AC-3/Frame[1]/Auxiliary Data/EMDF Container/Payload[1]/JOC Header/JOC Object Count": "15 objects",
                    "AC-3/Frame[1]/Auxiliary Data/EMDF Container/Payload[0]/Object Count": "15 objects"},
            crosscheck={"ffprobe": True, "ffprobe_channels": 8, "decode": True, "skip_insertion": False,
                        "skip": {"skip_insertion": "ffmpeg decodes only substream 0, so a change to the dependent substream would not show."}})

    # The core and the dependent substream both set the extension; one flag per access unit is enough.
    data = (five_one_core(addbsi=emdf.addbsi(16)) + dependent(tail=container, addbsi=emdf.addbsi(16))) * 4
    fixture("joc-flag-both", ".eac3", "Four access units whose 5.1 core and dependent frames both set the addbsi extension, with the container in the "
            "dependent frames: no frame is judged alone.", data, frames_of(data),
            [stream("E-AC-3", 48000, 7, 1, 4 * 1536, layout=seven_one, objects=15)], [],
            crosscheck={"ffprobe": True, "ffprobe_channels": 8, "decode": True, "skip_insertion": False,
                        "skip": {"skip_insertion": "ffmpeg decodes only substream 0, so a change to the dependent substream would not show."}})

    # Random bytes in the unused bits and a skip field, some starting like a container, announce nothing.
    rng = random.Random(20261015)
    frames = []
    for i in range(30):
        tail = bytes(rng.randrange(256) for _ in range(90))
        if i % 3 == 0:
            tail = b"\x58\x38" + tail[2:]
        skip = bytes(rng.randrange(256) for _ in range(40))
        if i % 4 == 0:
            skip = b"\x58\x38" + skip[2:]
        frames.append(eac3(acmod=2, words=160, tail=tail, skips={0: skip}))
    data = b"".join(frames)
    fixture("emdf-noise", ".eac3", "30 seeded 2/0 frames with random bytes after the last block and in a skip field, a third and a quarter of them starting with "
            "the EMDF sync word, and no extension flag: no container is found and nothing is reported.", data, frames_of(data),
            [stream("E-AC-3", 48000, 2, 0, 30 * 1536)], [],
            crosscheck={"ffprobe": True, "decode": True, "skip_insertion": False,
                        "skip": {"skip_insertion": "The frames already hold skip fields, which the rewrite does not combine with its own."}})

    container = emdf.object_audio(objects=15)

    # The container inside the auxiliary data field proper (auxdatae 1, auxdatal): MediaInfo 24.01 searches the
    # frame for a container but parses one only if it starts before that field.
    data = eac3(acmod=7, lfeon=1, words=400, bsmod=0, addbsi=emdf.addbsi(16), aux=container) * 4
    fixture("joc-auxiliary-data", ".eac3", "Four 5.1 frames whose auxiliary data field (auxdatal, auxdatae 1) holds an EMDF container for 15 objects "
            "and whose addbsi sets the extension.", data, frames_of(data), [stream("E-AC-3", 48000, 7, 1, 4 * 1536, objects=15)], [],
            fields={"AC-3/Frame/Auxiliary Data/EMDF Container/Payload[1]/Payload ID": "14 (joint object coding)",
                    "AC-3/Frame/Auxiliary Data Length": f"{len(container) * 8} bits"},
            crosscheck={"ffprobe": True, "decode": True, "skip_insertion": False,
                        "skip": {"skip_insertion": "The frames' auxiliary data would move with the rewritten sizes; the check adds nothing for them."}})

    # A container the specification leaves open in many ways: other payloads, every configuration
    # option, a key ID and protection of each length, and a container after another.
    odd = emdf.container([(1, bytes(range(20))), (3, b"\x01\x02"), (36, bytes(7)), (6, bytes(0))], key=9, protection=(3, 2),
                         config=[{"group": 1, "codecdata": 0x5A}, {"offset": 300, "duration": 5000, "priority": 3, "proc": 2}, {"discard": 1},
                                 {"aligned": 0, "group": 0}], version=0)
    data = eac3(acmod=2, words=300, tail=odd + emdf.object_audio(objects=3, dmx=0, size=12), addbsi=emdf.addbsi(3)) * 3
    fixture("emdf-payloads", ".eac3", "Three 2/0 frames whose auxiliary data hold two EMDF containers: one with payloads 1, 3, 36 (an extended ID) and 6 "
            "under different payload configurations, key ID 9 and 128 and 32 bit protection, and one with the object audio of 3 objects.",
            data, frames_of(data), [stream("E-AC-3", 48000, 2, 0, 3 * 1536, objects=3)], [],
            fields={"AC-3/Frame/Auxiliary Data/EMDF Container[0]/Key ID Extension": "2",
                    "AC-3/Frame/Auxiliary Data/EMDF Container[0]/Payload[2]/Payload ID Extension": "5",
                    "AC-3/Frame/Auxiliary Data/EMDF Container[0]/Protection/Primary Protection Length": "3 (128 bits)",
                    "AC-3/Frame/Auxiliary Data/EMDF Container[1]/Payload[1]/JOC Header/Downmix Configuration": "0 (5.X: left, right, center, left surround, right surround)"},
            crosscheck={"ffprobe": True, "decode": True, "skip_insertion": False,
                        "skip": {"skip_insertion": "The frames' auxiliary data would move with the rewritten sizes; the check adds nothing for them."}})

    # The flag and the payload disagree frame by frame.
    data = eac3(acmod=2, words=300, addbsi=emdf.addbsi(16)) * 3 + eac3(acmod=2, words=300, tail=container) * 3
    fixture("joc-flag-mismatch", ".eac3", "Three 2/0 frames that set the addbsi extension without a container, then three with a container and no "
            "addbsi.", data, frames_of(data), [stream("E-AC-3", 48000, 2, 0, 6 * 1536, objects=15)],
            [("ac3.extension-without-joc", "no EMDF container in the frames holds a joint object coding payload"),
             ("ac3.joc-without-extension", "none of the frames sets flag_ec3_extension_type_a")],
            crosscheck={"ffprobe": True, "decode": True, "skip_insertion": False,
                        "skip": {"skip_insertion": "The frames' auxiliary data would move with the rewritten sizes; the check adds nothing for them."}})

    # Damaged containers. Each file's frames hold one case.
    good = emdf.object_audio(objects=4, size=24)
    cases = [
        ("emdf-truncated", "an emdf_container_length of 900 bytes, past the auxiliary data", emdf.object_audio(objects=4, size=24, length=900),
         [("ac3.emdf-truncated", "gives 900 bytes")]),
        ("emdf-length-long", "6 bytes after its last field that emdf_container_length counts", emdf.object_audio(objects=4, size=24, trailing=bytes(6)),
         [("ac3.emdf-length", "fields end after")]),
        ("emdf-length-short", "an emdf_container_length one byte short of its protection bits", emdf.object_audio(objects=4, size=24, length=len(good) - 4 - 1),
         [("ac3.emdf-length", "run past the")]),
        ("emdf-payload-size", "a JOC payload size of 200 bytes in a container that holds fewer",
         emdf.cut(emdf.container([(emdf.OAMD, emdf.oamd_payload(4, size=12)), (emdf.JOC, emdf.joc_payload(4, size=200))]), 60),
         [("ac3.emdf-payload-size", "the payload with ID 14"), ("ac3.extension-without-joc", "no EMDF container")]),
        ("emdf-reserved", "protection_length_primary 0, which is reserved", emdf.object_audio(objects=4, size=24, protection=(0, 0)),
         [("ac3.emdf-reserved", "protection_length_primary is 0")]),
        ("emdf-padding", "bits in its last byte after the last field that are not 0",
         emdf.container([(emdf.OAMD, emdf.oamd_payload(4, size=12)), (emdf.JOC, emdf.joc_payload(4, size=12))], key=7, pad_bits=0x5),
         [("ac3.emdf-reserved", "fill the last byte")]),
        ("emdf-version", "emdf_version 5 (3 and an extension of 2), which Annex H does not define", emdf.container([(emdf.OAMD, emdf.oamd_payload(4))], version=5),
         [("ac3.emdf-version-not-read", "version Annex H does not define"), ("ac3.extension-without-joc", "no EMDF container")]),
        ("joc-reserved", "a reserved joc_dmx_config_idx", emdf.container([(emdf.OAMD, emdf.oamd_payload(4)), (emdf.JOC, emdf.joc_payload(4, dmx=6))]),
         [("ac3.joc-reserved", "joc_dmx_config_idx is 6")]),
        ("joc-short", "a JOC payload of one byte, shorter than its header",
         emdf.container([(emdf.OAMD, emdf.oamd_payload(4)), (emdf.JOC, b"\x20")]), [("ac3.object-payload-short", "shorter than the 12 bits")]),
        ("joc-incomplete", "a JOC payload and no OAMD payload", emdf.container([(emdf.JOC, emdf.joc_payload(4))]),
         [("ac3.object-audio-incomplete", "no OAMD payload")]),
    ]
    for name, what, damaged, findings in cases:
        good_frame = eac3(acmod=2, words=300, tail=good, addbsi=emdf.addbsi(4))
        bad_frame = eac3(acmod=2, words=300, tail=damaged, addbsi=emdf.addbsi(4))
        data = good_frame + bad_frame + good_frame
        fixture(name, ".eac3", f"Three 2/0 frames with an EMDF container for 4 objects in the auxiliary data; the second has {what}.", data, frames_of(data),
                [stream("E-AC-3", 48000, 2, 0, 3 * 1536, objects=4)], findings,
                crosscheck={"ffprobe": True, "decode": True, "skip_insertion": False,
                            "skip": {"skip_insertion": "The frames' auxiliary data would move with the rewritten sizes; the check adds nothing for them."}})


def build_coding():
    """Fixtures for the coding series: what each audio block chooses, with the expected values in the manifest's "coding" (one list of program
    frames per stream), computed by coding_blocks from the options each frame is built with."""
    ok = {"ffprobe": True, "decode": True, "skip_insertion": False,
          "skip": {"skip_insertion": "The check rewrites E-AC-3 frames with skip fields; the coding fixtures' blocks would need nothing new from it."}}

    def ac3_frame(**o):
        keep = {k: o.pop(k) for k in ("blksw", "remat", "cpl") if k in o}
        rows = coding_blocks(o.get("acmod", 2), blksw=keep.get("blksw"), remat=keep.get("remat"), cpl=keep.get("cpl"), chbwcod=o.get("chbwcod", 0))
        return ac3(**o, **keep), rows

    # 2/0 with block switching in single channels, rematrixing flags sent, reused and cleared, and different bandwidths per channel.
    sw = [(0, 0), (1, 0), (0, 1), (1, 1), (0, 0), (1, 0)]
    quiet = [(0, 0)] * 6
    options = [
        dict(chbwcod=20, blksw=quiet, remat=[0b1111, None, None, None, None, None]),
        dict(chbwcod=20, blksw=sw, remat=[0b0101, None, 0, None, 0b0001, None]),
        dict(chbwcod=[30, 45], blksw=[(1, 1)] * 6, remat=[0, None, None, None, None, None]),
        dict(chbwcod=60, blksw=quiet, remat=[0b1010, 0, 0b1000, None, None, 0b1111]),
        dict(chbwcod=0, blksw=[(0, 1)] + [(0, 0)] * 5, remat=[0b0001, None, None, 0, None, None]),
        dict(chbwcod=[12, 3], blksw=sw[::-1], remat=[0, None, 0b0010, None, None, 0]),
    ]
    built = [ac3_frame(**o) for o in options]
    data = b"".join(f for f, _ in built)
    fixture("coding-ac3-stereo", ".ac3", "Six 2/0 frames with block switching in one or both channels of some blocks, rematrixing flags sent in block 0 and again later, "
            "reused and cleared, and bandwidth codes that differ between the channels.", data, frames_of(data), [stream("AC-3", 48000, 2, 0, 6 * 1536)], [],
            crosscheck={"ffprobe": True, "decode": True, "liba52": True, "skip_insertion": False, "skip": ok["skip"]}, coding=[[coding_unit(48000, [r], False) for _, r in built]])

    # 2/0 with coupling that starts, stops, changes its range and starts again within a frame, which changes the number of rematrixing bands.
    options = [
        dict(cpl=[(3, 10), None, None, False, False, (3, 10)], remat=[0b1001, None, 0b0110, None, None, None], blksw=quiet),
        dict(cpl=[(0, 6), None, (2, 6), None, False, (1, 4)], remat=[0b11, None, 0b101, None, 0b1111, 0b010], blksw=sw),
        dict(cpl=[False, None, None, None, None, None], remat=[0, None, None, None, None, None], blksw=quiet),
        dict(cpl=[(3, 10)] * 1 + [None] * 5, remat=[0b0001, None, None, None, None, None], blksw=[(1, 0)] * 6, chbwcod=40),
        dict(cpl=[(5, 8), None, None, None, None, False], remat=[0, None, None, None, None, 0b1000], blksw=quiet, chbwcod=30),
    ]
    built = [ac3_frame(**o) for o in options]
    data = b"".join(f for f, _ in built)
    fixture("coding-ac3-coupling", ".ac3", "Five 2/0 frames with coupling that starts, stops, changes its range and starts again within a frame, rematrixing flags for the "
            "number of bands each range gives, and one frame with no coupling.", data, frames_of(data), [stream("AC-3", 48000, 2, 0, 5 * 1536)], [],
            crosscheck={"ffprobe": True, "decode": True, "liba52": True, "skip_insertion": False, "skip": ok["skip"]}, coding=[[coding_unit(48000, [r], False) for _, r in built]])

    # 3/2 with LFE: coupling of all five channels in some blocks, block switching in single channels, no rematrixing.
    options = [
        dict(acmod=7, lfeon=1, cpl=[(2, 9), None, False, None, (2, 9), None], blksw=[(0,) * 5, (1, 0, 0, 0, 0), (0,) * 5, (0, 0, 0, 0, 1), (0,) * 5, (1,) * 5]),
        dict(acmod=7, lfeon=1, cpl=[False, None, None, None, None, None], blksw=[(0,) * 5] * 6, chbwcod=[40, 40, 40, 20, 20]),
    ]
    built = [ac3_frame(**o) for o in options]
    data = b"".join(f for f, _ in built)
    fixture("coding-ac3-surround", ".ac3", "Two 3/2 frames with an LFE channel, coupling of all five channels in some blocks and block switching in single channels.",
            data, frames_of(data), [stream("AC-3", 48000, 7, 1, 2 * 1536)], [],
            crosscheck={"ffprobe": True, "decode": True, "liba52": True, "skip_insertion": False, "skip": ok["skip"]}, coding=[[coding_unit(48000, [r], False) for _, r in built]])

    # 1+1: two independent channels, which have no rematrixing and no coupling.
    options = [dict(acmod=0, blksw=[(0, 0), (1, 0), (0, 1), (1, 1), (0, 0), (0, 0)], chbwcod=[10, 40]), dict(acmod=0, blksw=[(0, 0)] * 6, chbwcod=[50, 5])]
    built = [ac3_frame(**o) for o in options]
    data = b"".join(f for f, _ in built)
    fixture("coding-ac3-dual-mono", ".ac3", "Two 1+1 frames with block switching in one or both channels of some blocks and different bandwidths.", data, frames_of(data),
            [stream("AC-3", 48000, 0, 0, 2 * 1536)], [],
            crosscheck={"ffprobe": True, "ffprobe_channels": 2, "decode": True, "liba52": True, "skip_insertion": False, "skip": ok["skip"]}, coding=[[coding_unit(48000, [r], False) for _, r in built]])

    # E-AC-3: frames of 6, 3, 2 and 1 blocks in one stream, with block switching, rematrixing and spectral extension that starts and stops.
    def eac3_frame(**o):
        keep = {k: o.pop(k) for k in ("blksw", "remat", "spx_blocks", "spx_range", "spx_structure") if k in o}
        blocks = [1, 2, 3, 6][o.get("numblkscod", 3)]
        spx = keep.get("spx_blocks")
        rows = coding_blocks(o.get("acmod", 2), blocks=blocks, blksw=keep.get("blksw"), remat=keep.get("remat"), spx=spx, spx_range=keep.get("spx_range", (2, 2)))
        return eac3(**o, **keep), rows

    six = [
        dict(numblkscod=3, blksw=sw, remat=[0b0110, None, 0, None, 0b1000, None], spx_blocks=[True, None, False, None, True, None], spx_range=(3, 5), spx_structure=True),
        dict(numblkscod=3, blksw=quiet, remat=[0, None, None, None, None, 0b0001], spx_blocks=[False] + [None] * 5),
        dict(numblkscod=2, blksw=[(1, 1), (0, 0), (0, 1)], remat=[0b1111, 0, None], spx_blocks=[False, True, None], spx_range=(1, 3), spx_structure=True),
        dict(numblkscod=1, blksw=[(0, 1), (1, 0)], remat=[0b0011, None], spx_blocks=[True, False], spx_range=(3, 5), spx_structure=True),
        dict(numblkscod=0, blksw=[(1, 1)], remat=[0b0100], spx_blocks=[True], spx_range=(3, 5), spx_structure=True),
        dict(numblkscod=3, blksw=quiet, remat=[0b1111, None, None, None, None, None], spx_blocks=[True] * 6, spx_range=(3, 5), spx_structure=True),
    ]
    built = [eac3_frame(acmod=2, words=256, **o) for o in six]
    data = b"".join(f for f, _ in built)
    samples = sum(256 * [1, 2, 3, 6][o["numblkscod"]] for o in six)
    fixture("coding-eac3-blocks", ".eac3", "Six 2/0 frames of 6, 6, 3, 2, 1 and 6 blocks with block switching, rematrixing flags and spectral extension that starts and stops.",
            data, frames_of(data), [stream("E-AC-3", 48000, 2, 0, samples)], [],
            crosscheck={"ffprobe": True, "decode": True, "skip_insertion": True, "skip_control": False, "skip": {"skip_control": ZERO_ALLOCATION}}, coding=[[coding_unit(48000, [r], True) for _, r in built]])

    # E-AC-3 program frames of an independent 2/0 frame and a dependent frame: the blocks of both make the frame's values, a dependent mono
    # frame that adds the center channel has no rematrixing, and the number of blocks changes between program frames.
    program = [
        (dict(numblkscod=3, blksw=sw, remat=[0b0110, None, 0, None, 0b1000, None], spx_blocks=[False] * 6),
         dict(acmod=2, numblkscod=3, blksw=quiet, remat=[0b0001, None, None, None, None, None], spx_blocks=[True] * 6, spx_range=(3, 5), spx_structure=True, words=160)),
        (dict(numblkscod=2, blksw=[(0, 0), (1, 0), (0, 0)], remat=[0, None, 0b0011], spx_blocks=[False] * 3),
         dict(acmod=1, numblkscod=2, blksw=[(1,), (0,), (1,)], spx_blocks=[False] * 3, words=160)),
        (dict(numblkscod=0, blksw=[(1, 1)], remat=[0b1111], spx_blocks=[True], spx_range=(3, 5), spx_structure=True), None),
        (dict(numblkscod=3, blksw=quiet, remat=[0b1000] + [None] * 5, spx_blocks=[True, None, False, None, None, None], spx_range=(3, 5), spx_structure=True),
         dict(acmod=2, numblkscod=3, blksw=sw, remat=[0, None, 0b0100, None, None, None], spx_blocks=[False] * 6, words=160)),
    ]
    chunks, units = [], []
    for independent, dependent in program:
        frame, rows = eac3_frame(acmod=2, words=256, **independent)
        frames = [rows]
        if dependent is not None:
            more, dependent_rows = eac3_frame(strmtyp=1, chanmap=0x4000 if dependent["acmod"] == 1 else LRS_RRS, **dependent)
            frame += more
            frames.append(dependent_rows)
        chunks.append(frame)
        units.append(coding_unit(48000, frames, True))
    data = b"".join(chunks)
    fixture("coding-eac3-dependent", ".eac3", "Four E-AC-3 program frames: an independent 2/0 frame with a dependent 2/0 frame, with a dependent mono frame that adds the center "
            "channel, alone with one block, and with a dependent frame again, the number of blocks changing between them.", data, frames_of(data),
            [stream("E-AC-3", 48000, 2, 0, sum(256 * [1, 2, 3, 6][p[0]["numblkscod"]] for p in program), layout=[m.FL, m.FR, m.BL, m.BR], vbr=True)],
            [("ac3.dependent-layout-changed", "add other channels in 2 of its frames than in its first")],
            crosscheck={"ffprobe": False, "decode": True, "skip_insertion": False,
                        "skip": {"ffprobe": "Dependent substreams of different layouts make what ffmpeg's parser reports of the channels differ from the analyzer's first-frame layout.",
                                 "skip_insertion": "The check rewrites the frames of one stream; ffmpeg decodes the independent substream and mixes in the dependent frames only as it likes, so a "
                                                    "moved skip field in a dependent frame is not guaranteed to change the samples."}}, coding=[units])

    # An AC-3 core with E-AC-3 dependent frames, the form ETSI TS 102 366 J.1.5.2.2 gives a program of more than 5.1 channels (the AC-3 bit stream is
    # independent substream 0 of the Enhanced AC-3 bit stream): the program frame has the core's six blocks and the dependent frame's, 12 values in each
    # series, which no fixed layout of six holds; a program frame with no dependent frame has six.
    core_program = [
        (dict(blksw=quiet, remat=[0b1111, None, None, None, None, None], cpl=[(3, 10), None, False, None, None, None]),
         dict(numblkscod=3, blksw=sw, remat=[0b0001] + [None] * 5, spx_blocks=[False] * 6, words=160)),
        (dict(blksw=sw, remat=[0, None, 0b0101, None, None, None], cpl=[False] + [None] * 5), None),
        (dict(blksw=[(1, 1)] * 6, remat=[0b0011] + [None] * 5, cpl=[(2, 9), None, None, None, None, None]),
         dict(numblkscod=3, blksw=quiet, remat=[0b1000, None, None, 0, None, None], spx_blocks=[True] * 6, spx_range=(3, 5), spx_structure=True, words=160)),
        (dict(blksw=quiet, remat=[0b1010, None, None, None, None, None], cpl=[False] + [None] * 5),
         dict(numblkscod=3, blksw=[(1, 0)] * 6, remat=[0] + [None] * 5, spx_blocks=[False] * 6, words=160)),
    ]
    chunks, units = [], []
    for core_options, dependent in core_program:
        frame, rows = ac3_frame(**core_options)
        frames = [rows]
        if dependent is not None:
            more, dependent_rows = eac3_frame(strmtyp=1, acmod=2, chanmap=LRS_RRS, **dependent)
            frame += more
            frames.append(dependent_rows)
        chunks.append(frame)
        units.append(coding_unit(48000, frames, False))
    data = b"".join(chunks)
    fixture("coding-ac3-core-dependent", ".ac3", "Four program frames of an AC-3 2/0 frame, three of them followed by an E-AC-3 dependent frame that adds the rear pair: 12 values in each "
            "series where there is a dependent frame, 6 where there is none.", data, frames_of(data),
            [stream("E-AC-3", 48000, 2, 0, 4 * 1536, layout=[m.FL, m.FR, m.BL, m.BR], vbr=True)],
            [("ac3.dependent-layout-changed", "add other channels in 1 of its frames than in its first")],
            crosscheck={"ffprobe": False, "decode": False, "skip_insertion": False,
                        "skip": {"ffprobe": "ffmpeg's parser takes the AC-3 core and the E-AC-3 frames as two codecs; what it reports of the stream is no reference for the analyzer's.",
                                 "decode": "The check decodes each packet alone as one codec, and a packet that joins an AC-3 frame and a dependent E-AC-3 frame is neither.",
                                 "skip_insertion": "The core is AC-3, whose frames the check does not rewrite."}}, coding=[units])

    # Spectral extension that starts after a block without it, with the default band structure the specification loads at the first block that
    # uses spectral extension (ETSI TS 102 366 E.1.3.3.7), which ffmpeg loads only in block 0 (libavcodec/ac3dec.c, decode_band_structure:
    # "if (!blk) memcpy(...default...)", called from spx_strategy only): a frame decoded alone reads sub-bands 5 to 13 with the zeros of a fresh
    # context, 8 bands of coordinates where the default merges three, and fails. The frame that starts in block 0 decodes.
    defaults = [
        dict(numblkscod=3, blksw=quiet, remat=[0b0110, None, None, None, None, None], spx_blocks=[True, None, False, None, None, None], spx_range=(3, 5)),
        dict(numblkscod=3, blksw=sw, remat=[0, None, 0b1111, None, None, None], spx_blocks=[False, True, None, None, False, None], spx_range=(3, 5)),
        dict(numblkscod=3, blksw=quiet, remat=[0b1000, None, None, None, None, None], spx_blocks=[False, None, None, True, None, None], spx_range=(3, 5)),
    ]
    built = [eac3_frame(acmod=2, words=256, **o) for o in defaults]
    data = b"".join(f for f, _ in built)
    fixture("coding-eac3-spx-default", ".eac3", "Three 2/0 frames of six blocks: spectral extension from block 0 and, in the other two, starting in block 1 and in block 3 after blocks without "
            "it, all with the default band structure; the frames that start late are the ones ffmpeg rejects.", data, frames_of(data), [stream("E-AC-3", 48000, 2, 0, 3 * 1536)], [],
            crosscheck={"ffprobe": True, "decode": True, "damaged": [512, 1024], "skip_insertion": False,
                        "skip": {"skip_insertion": "ffmpeg rejects two of the frames (an ffmpeg bug, see the fixture's description), so the samples are no reference.",
                                 "damaged": "Expected failures, not damage: ffmpeg 6.1.1 loads the default spectral extension band structure only in block 0 of a frame that has it there, where "
                                            "ETSI TS 102 366 E.1.3.3.7 loads it whenever spectral extension starts without a structure of its own."}},
            coding=[[coding_unit(48000, [r], True) for _, r in built]])

    # The largest program frame: an independent frame and eight dependent frames of six blocks, 54 values in each series (Annex E 2.3.1.2 allows one
    # dependent frame for each of the substream IDs 0 to 7), then smaller ones.
    def nine(blocks, dependents, flavor):
        pieces, frames = [], []
        code = {1: 0, 2: 1, 3: 2, 6: 3}[blocks]
        independent, rows = eac3_frame(acmod=2, words=256, numblkscod=code, blksw=[(flavor % 2, 0)] * blocks, remat=[0b0101] + [None] * (blocks - 1),
                                       spx_blocks=[False] * blocks)
        pieces.append(independent)
        frames.append(rows)
        for ident in range(dependents):
            frame, rows = eac3_frame(strmtyp=1, substream=ident, acmod=2, words=160, numblkscod=code, blksw=[((ident + flavor) % 2, 1)] * blocks,
                                     remat=[(ident + 1) & 15] + [None] * (blocks - 1), spx_blocks=[False] * blocks)
            pieces.append(frame)
            frames.append(rows)
        return b"".join(pieces), coding_unit(48000, frames, True)

    shapes = [nine(6, 8, 0), nine(6, 8, 1), nine(3, 4, 0), nine(1, 8, 1)]
    data = b"".join(f for f, _ in shapes)
    fixture("coding-eac3-nine-frames", ".eac3", "Four E-AC-3 program frames of an independent 2/0 frame and 8, 8, 4 and 8 dependent 2/0 frames of 6, 6, 3 and 1 blocks: 54, 54, 15 and 9 "
            "values in each series.", data, frames_of(data), [stream("E-AC-3", 48000, 2, 0, 256 * (6 + 6 + 3 + 1), vbr=True)],
            [("ac3.dependent-layout-changed", "add other channels in 1 of its frames than in its first")],
            crosscheck={"ffprobe": False, "decode": False, "skip_insertion": False,
                        "skip": {"ffprobe": "ffmpeg's parser joins the dependent frames to their independent frame and reports the channels of the stream it decodes, which the check does not compare for a program of nine frames.",
                                 "decode": "ffmpeg decodes the independent substream only and the check decodes each packet; the dependent frames' content is no reference here.",
                                 "skip_insertion": "The dependent frames are not decoded by ffmpeg, so a moved skip field there changes no sample."}}, coding=[[u for _, u in shapes]])


def sample_rows(track, chunks, split=False):
    """The frames of an MP4 track's samples: each sample's frames one after another from its offset."""
    rows = []
    for chunk, start in zip(track.chunks(), chunks[0]):
        at = start
        for sample in chunk:
            inner = 0
            while inner < len(sample):
                length = min(m.header(sample, inner)["length"], len(sample) - inner)
                rows.append([at + inner, length])
                inner += length
            at += len(sample)
    return rows


def main():
    build()
    build_object_audio()
    OUT.mkdir(parents=True, exist_ok=True)
    for name, (data, manifest) in FIXTURES.items():
        (OUT / name).write_bytes(data)
        stem = name.rsplit(".", 1)[0]
        (OUT / (stem + ".expected.json")).write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(FIXTURES)} fixtures written to {OUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
