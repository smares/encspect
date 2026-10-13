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

Each <name>.expected.json manifest lists what the analyzer must report: the file's length and
format, every frame and gap (offset and length), the streams with their codec, rate, channels,
channel mode and layout, samples and whether the bitrate varies, fields by node path, exactly the
findings by code with a part of their message, and what crosscheck_ac3_fixtures.py checks with
ffprobe, ffmpeg and liba52, with a reason for each check it skips.
"""

import json
import pathlib
import sys

import ac3_model as m
import mp4_boxes as mp4

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "ac3"

LRS_RRS = 0x8000 >> 6


def stream(codec, rate, acmod, lfeon, samples, layout=None, vbr=False, mode=None):
    speakers = layout or m.speakers(acmod, lfeon)
    return {"codec": codec, "sample_rate": rate, "channels": len(speakers), "channel_mode": mode or m.CODING_MODES[acmod], "channel_layout": speakers,
            "samples": samples, "is_vbr": vbr}


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

# The block end bits of every frame built, by its bytes, so a manifest lists them for the frames a
# fixture holds unchanged.
ENDS = {}


def ac3(**options):
    ends = []
    frame = m.ac3_frame(ends=ends, **options)
    # The analyzer reads no audio blocks of bsid 9 and 10 and stops at a bandwidth code above 60.
    if options.get("bsid", 8) <= 8 and options.get("chbwcod", 0) <= 60:
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


def fixture(name, extension, description, data, frames, streams, findings, fields=None, gaps=(), crosscheck=None, fmt=None, boxes=None, read=True):
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
            crosscheck={"ffprobe": True, "ffprobe_channels": 8, "decode": True, "skip_insertion": False,
                        "skip": {"skip_insertion": "ffmpeg decodes only substream 0, so a change to substream 1 would not show."}})

    # A dependent substream before any independent one.
    data = eac3(strmtyp=1, acmod=2, chanmap=LRS_RRS, words=160) + eac3(acmod=2, words=200) * 3
    fixture("dependent-first", ".eac3", "A dependent frame, then three independent 2/0 frames.", data, frames_of(data),
            [stream("E-AC-3", 48000, 2, 0, 3 * 1536)], [("ac3.dependent-without-independent", "before any independent substream")],
            crosscheck={"ffprobe": True, "ffprobe_bitrate": False, "decode": True, "damaged": [0],
                        "skip": {"ffprobe_bitrate": "ffmpeg gives the bitrate of the first frame it decodes, not of the dependent frame before it."}})

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
            crosscheck={"ffprobe": False, "decode": False,
                        "skip": {"ffprobe": "ffmpeg 6.1 rejects reduced sample rates.", "decode": "ffmpeg 6.1 rejects reduced sample rates."}})

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


def sample_rows(track, chunks, split=False):
    """The frames of an MP4 track's samples: each sample's frames one after another from its offset."""
    rows = []
    for chunk, start in zip(track.chunks(), chunks[0]):
        at = start
        for sample in chunk:
            inner = 0
            while inner < len(sample):
                length = m.header(sample, inner)["length"]
                rows.append([at + inner, length])
                inner += length
            at += len(sample)
    return rows


def main():
    build()
    OUT.mkdir(parents=True, exist_ok=True)
    for name, (data, manifest) in FIXTURES.items():
        (OUT / name).write_bytes(data)
        stem = name.rsplit(".", 1)[0]
        (OUT / (stem + ".expected.json")).write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(FIXTURES)} fixtures written to {OUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
