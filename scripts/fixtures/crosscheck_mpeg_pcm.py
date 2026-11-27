#!/usr/bin/env python3
"""Compares the PCM that Encspect's MPEG audio decoder delivers with minimp3's and FFmpeg's, by position
(docs/formats/mpeg-audio.md, "Decoding"; docs/design/lossy-decoders.md, "The PCM cross-checks"; docs/development.md, "MPEG audio decoding").

    crosscheck_mpeg_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--keep DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll" (the default is that path
relative to the repository). With no FILE the script takes every MPEG audio fixture of tests/fixtures/mpeg-audio that its reference can decode as a whole (see EXPLAINED).

For each file:

- PcmDump writes the samples of the decoded stream (floats at full scale 1.0, interleaved) with the runs of positions they cover. Positions are untrimmed
  and count from the first audio frame: a Xing, Info or VBRI header frame holds no audio and is not a position.
- minimp3 (CC0, third_party/minimp3, built by minimp3_tool.py with -ffp-contract=off) decodes the file to floats with the same full scale. Its first frame is dropped
  where it is a VBR header frame, which minimp3 decodes as a frame of silence. A frame it skips (no main data, or damage) is zeros in the reference and its positions
  are left out of the comparison and listed.
- FFmpeg decodes the file to 32-bit floats as a second reference, with `-flags2 +skip_manual` so that it applies none of the encoder delay a LAME tag states.
  Its demuxer reads the VBR header frame itself, so the positions agree. A file FFmpeg decodes with another frame count than minimp3's is listed.
- compare_pcm.py compares by position within the bounds of the design (per channel the RMS of the difference at most 2^-15 / sqrt(12) of full scale, its
  largest magnitude at most 2^-14): the floats are compared as floats, nothing is rounded to 16 bits.
- Positions only one side has are listed: those the analyzer withheld after a gap or for missing main data, and those a reference conceals. A file whose
  reference differs for a reason listed in EXPLAINED is compared with that reference's frames left out; a file outside the bound for any other reason fails the run.

Exit status 0 where every bound holds, 1 where one fails, 2 for a usage error.
"""

import argparse
import json
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import mctoolamed_tool  # noqa: E402
import minimp3_tool  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")

# Fixtures a reference decodes differently, by reference ("minimp3" or "ffmpeg"), with what is known. An entry is a pending item for the lead, not an explanation: the
# bound is not widened for it (docs/design/lossy-decoders.md, "The PCM cross-checks").
R1 = (
    "FFmpeg 6.1.1 alone differs, in a few granules of channel 0 (long blocks of plain stereo; up to 0.026 of full scale at its largest, on external-plugger-pro-cbr, granule 92: no big values and a part2_3_length of 2 bits in channel 0) and the tails into the next ones: the analyzer matches mpg123 1.32.5 (largest difference 8e-7), madplay 0.15.2b (1.4e-6) and minimp3 (3e-7), the ranges are FFMPEG_RANGES; the cause in FFmpeg is not known (black box)")
R2 = (
    "FFmpeg 6.1.1 and mpg123 1.32.5 agree with each other and differ from the analyzer by up to 0.0095 (madplay 0.15.2b 1.1e-6 and minimp3 3e-7 agree with the analyzer), in two ways. "
    "(1) In a frame of intensity stereo (MPEG-2 LSF) with mid/side stereo they take the largest value a position's slen can code as an ordinary intensity position (on intensity-mpeg2-largest-position.mp3 FFmpeg 6.1.1 does so for all three lengths, mpg123 1.32.5 for position 1 of slen 1 and 3 of slen 2 but not 7 of slen 3), where ISO/IEC 13818-3 "
    "2.4.3.2 (bullet 2) makes it the illegal position, whose band is decoded with the mid/side equations (left/right without mid/side stereo), as the analyzer does; the first differing granule "
    "of external-fhg-acm34-mpeg2 holds positions 15 and 1 (slen 4 and 1) in bands 11 to 16, above the intensity bound. "
    "(2) For the top band, which has no transmitted position (long band 21, short band 12 per window; Table B.2 says its scalefactor is not transmitted), they copy band 20's (11's) position, "
    "where the analyzer, as minimp3, uses the center position when band 20 (11) lies below the bound and band 20's otherwise; neither standard defines a position there. "
    "A scratch build of minimp3 with both changes (no illegal MPEG-2 position, the top band always copying) matches FFmpeg to 4.6e-5 and mpg123 to 2.7e-4 (FFmpeg's last two granules excluded), "
    "with either change alone it does not (acm34 against mpg123: 0.0049 with (1) alone, 0.0095 with (2) alone). The analyzer follows the standard where it speaks (1) and minimp3's choice where it does not (2)")

EXPLAINED = {
    "mc-3-1-crosstalk-decided.mp2": {"mctoolamed": "dyn_cross_mode 5 to 7, which 13818-3 2.5.2.15 forbids in 3/1, are in the fixture and read differently by the two decoders; the allowed modes of 3/1, 3/2, 3/0, 2/1 and the second stereo program agree within 1 LSB (see development.md, \"Multichannel extension\")"},
    "mc-3-1-crosstalk-side.mp2": {"mctoolamed": "forbidden dyn_cross_mode values (5 to 7) and the copy sources the text and the reference software read differently (13818-3 2.5.2.15, the base channel of a copy)"},
    "mc-3-1-crosstalk-flag.mp2": {"mctoolamed": "forbidden dyn_cross_mode values (5 to 7)"},
    "mc-3-1-crosstalk-flag-right.mp2": {"mctoolamed": "forbidden dyn_cross_mode values (5 to 7)"},
    "mc-2-2-crosstalk-side.mp2": {"mctoolamed": "2/2 with dynamic crosstalk written for the text's reading, which mctoolamed reads differently, and the forbidden dyn_cross_mode values 5 to 7"},
    "mc-2-2-crosstalk-flag.mp2": {"mctoolamed": "the forbidden dyn_cross_mode values 5 to 7 of 2/2"},
    "mc-forbidden-values.mp2": {"mctoolamed": "dyn_cross_mode 15 of 3/2 and a nonzero tc_allocation with dematrix_procedure 3, which 13818-3 2.5.2.15 forbids, are in the fixture and read differently by the two decoders (each alone, in every frame of a variant, differs; the valid lfe_allocation 15 and a scalefactor index of 63 agree)"},
    "mc-second-stereo-crosstalk.mp2": {"mctoolamed": "tc_allocation 3, which 13818-3 2.5.2.15 leaves unassigned in 3/0 + 2/0, is in the fixture"},
    "mc-center-reserved-second-stereo.mp2": {"mctoolamed": "the reserved center value 2 (13818-3 2.5.2.13: not defined), which the analyzer reads as no center"},
    "intensity-mpeg2-largest-position.mp3": {
        "ffmpeg": "FFmpeg 6.1.1 differs from the analyzer by 0.0196 of full scale (minimp3 and the analyzer agree): "
                  "it decodes the largest value a position's slen can code (1 of slen 1, 3 of slen 2, 7 of slen 3, in this MPEG-2 fixture) as an ordinary intensity position, where ISO/IEC 13818-3 2.4.3.2 (bullet 2) makes it "
                  "the illegal position, whose band is decoded with the mid/side equations; the right channel of the fixture is all zero, so every band is intensity coded. mpg123 1.32.5 (not a reference of this script) "
                  "does the same for slen 1 and 2 and follows the analyzer for slen 3. The analyzer and minimp3 follow the standard",
    },
    "intensity-mixed-windows.mp3": {
        "minimp3": "minimp3 (as of commit ea99364f) takes one intensity stereo bound for the three windows of a mixed block, where ISO/IEC 13818-3 2.4.3.2 (bullet 3) finds each window's bound individually; "
                   "its output differs from the analyzer's by 0.031 of full scale, and FFmpeg 6.1.1 (8.9e-8), mpg123 1.32.5 (3.7e-8) and madplay 0.15.2b (4.1e-7) match the analyzer; "
                   "a scratch build of minimp3 that takes the bound of each window (the long bands the highest of the three) is bit-identical to the analyzer's output"
    },
    "intensity-mixed-windows-mpeg2.mp3": {
        "minimp3": "the same cause in an MPEG-2 frame (0.058 of full scale): FFmpeg 6.1.1 (2.2e-7) and madplay 0.15.2b (4.4e-7) match the analyzer; mpg123 1.32.5 differs (0.023) for another reason, listed in docs/formats/mpeg-audio.md (it applies position 0 to short band 11 of windows 1 and 2 of an MPEG-2 mixed block)",
    },
    "external-plugger-pro-cbr.mp3": {"ffmpeg": R1},
    "external-plugger-cbr.mp3": {"ffmpeg": R1},
    "external-plugger-original.mp3": {"ffmpeg": R1},
    "external-plugger-48k.mp3": {"ffmpeg": R1},
    "external-8hz-flags.mp3": {"ffmpeg": R1},
    "external-fhg-acm19-mpeg2.mp3": {"ffmpeg": R2},
    "external-fhg-acm34-mpeg2.mp3": {"ffmpeg": R2},
    "external-nero-rom-mp3pro-cbr64.mp3": {
        "ffmpeg": "in the granules 327 to 355 (positions 188,352 to 205,056; intensity stereo frames of a tone in the left channel, where the right channel codes no lines and has "
                  "scalefac_compress 90) FFmpeg 6.1.1 and 8.0.1 put the tone into the right channel as well (RMS 0.21 to 0.25 of full scale, the analyzer's 0.003 or less); observed "
                  "with the same file, mpg123 1.32.5 (`mpg123 -e f32 --no-gapless`) does the same and madplay 0.15.2b (`madplay -S -o raw:`) decodes the right channel as silent, as "
                  "minimp3 and the analyzer do, and the right channel of the kit's source music-44k-stereo.wav has an RMS of 0 from 8.6 to 9.2 s. The comparison with FFmpeg "
                  "leaves those positions out (FFMPEG_RANGES)"
    },
    "external-nero-rom-mp3pro-vbr-low.mp3": {
        "ffmpeg": "the same difference in the granules 327 to 355 (positions 188,352 to 205,056), where the right channel again has scalefac_compress 90, and in the granules 233 and "
                  "234 (positions 134,208 to 135,360; intensity stereo, MPEG-2) a largest difference of 1.2e-4 of full scale in 18 and 24 samples above the bound. The comparison "
                  "with FFmpeg leaves those positions out (FFMPEG_RANGES); observed with the same file, madplay 0.15.2b decodes the first as the analyzer does"
    },
    "external-xing-mpeg2.mp3": {
        "ffmpeg": "the references disagree: in the granules of frames 61 to 64 of this tompg file (MPEG-2 joint stereo, intensity and mid/side stereo, a transient after near silence) the largest "
                  "differences are 0.0236 of full scale between minimp3 and FFmpeg 6.1.1 (mp3float and mp3 agree to 3e-5), 0.0221 between minimp3 and mpg123 1.32.5 and 0.0174 between FFmpeg and "
                  "mpg123; the analyzer keeps minimp3, the reference it follows operation by operation"
    },
    "external-lame357w-vbr.mp3": {
        "minimp3": "in one granule (frame 42, granule 0, side channel: big_values 1, a count1 region of 144 quadruples) the last quadruple starts at line 574 and does not fit in the 576 lines; "
                   "minimp3 decodes its first two values (lines 574 and 575, both nonzero here), and the analyzer, libmad, mpg123 and FFmpeg do not: 0.0084 of full scale near 22 kHz. A minimp3 "
                   "changed to stop before a quadruple that does not fit equals the analyzer's output (docs/development.md, MPEG audio decoding)"
    },
    "side-info-mono.mp3": {
        "minimp3": "the private bits of this hand-built mono stream are set, and minimp3 reads them as scfsi bits that its decoder uses; the analyzer reads the side info as the specification lays it out"
    },
    "side-info-long-blocks.mp3": {
        "minimp3": "the private bits of this hand-built stereo stream are 111, which minimp3 reads as scfsi bits of the first granule (as in side-info-mono.mp3): it takes the scalefactors of "
                   "the second channel's first granule from the previous values and reads 6 bits of them instead of 41, so its Huffman data start 35 bits earlier than the specification places them"
    },
    "reservoir-across-gap.mp3": {
        "ffmpeg": "after the 40 bytes of junk (less than a frame) the analyzer withholds 1,056 samples and decodes the frames whose main data reach back across the junk, as minimp3 does; "
                  "FFmpeg's output differs from theirs in positions that both output (largest 11.4 of full scale, RMS 0.97 against the analyzer); the cause is not established"
    },
}

MAGICS = (b"Xing", b"Info", b"VBRI")


# Fixtures that make mixed blocks, which no encoder writes: the short granules of these files with mixed_block_flag set (ISO/IEC 11172-3 2.4.2.7), one for each MPEG-1
# sample rate row of the band tables, written next to the dumps. minimp3 and the analyzer must agree on them.
MIXED_SOURCES = ["external-helix-r11-mono.mp3", "external-blade-stereo.mp3", "external-fhg-music-cbr.mp3", "external-fhg-mono-cbr.mp3"]


def mixed_variant(source, directory, binary):
    """Writes the source with mixed_block_flag set on every short granule of an MPEG-1 Layer III file; returns the path, or None where the file has none."""
    data = bytearray(source.read_bytes())
    frames = minimp3_tool.decode_pcm(binary, source, directory / "mixed-probe.f32")
    changed = 0
    for frame in frames:
        base = frame["offset"]
        header = data[base:base + 4]
        if frame["layer"] != 3 or (header[1] >> 3) & 3 != 3:
            continue
        channels = 1 if (header[3] >> 6) == 3 else 2
        start = (4 + (0 if header[1] & 1 else 2)) * 8 + 9 + (5 if channels == 1 else 3) + 4 * channels
        for index in range(2 * channels):
            at = start + index * 59 + 33
            bit = lambda n: (data[base + (at + n) // 8] >> (7 - (at + n) % 8)) & 1
            if bit(0) and (bit(1) << 1 | bit(2)) == 2:
                data[base + (at + 3) // 8] |= 0x80 >> ((at + 3) % 8)
                changed += 1
    if not changed:
        return None
    target = directory / ("mixed-" + source.name)
    target.write_bytes(data)
    return target


# The files of several decoded streams (a change of stream in the file): the comparison takes the positions of one stream, so they are not compared. A file that has several streams
# and is not listed, or a listed one that has one, fails the run.
MULTIPLE_STREAMS = (
    "cut-frame-stream-change.mp3", "free-format-to-fixed.mp3", "layer-change.mp3", "ancillary-streams-apart.mp3", "mp3pro-then-mpeg1.mp3", "ofl-in-each-stream.mp3", "reservoir-free-to-fixed.mp3",
    "reservoir-stream-change.mp3", "sample-rate-change.mp3", "sample-rate-change-statistics.mp3", "stream-change-after-gap.mp3", "streams-limited.mp3", "lame-after-joined-frames.mp3",
    "lame-before-joined-frames.mp3",
)

# The files that decode no stream (no chain of frames, so nothing is decoded and there is nothing to compare). A listed file that decodes one, and an unlisted file that decodes none, fails the run.
NO_STREAM = (
    "no-frames.mp3", "free-format-period-4.mp3", "free-format-weak-chain.mp3", "protected-weak-chain.mp3", "lone-frame-overflow.mp1", "lone-frames-forbidden-allocation.mp1",
    "lone-frame-invalid-side-info.mp3", "scalefactor-overflow.mp2", "mpeg1-layer1-allocation-overflow.mp1", "mpeg1-layer1-overflow.mp1",
)

# The fixtures the default run leaves out, each with the reason; a name that is no fixture, or a fixture that is left out unlisted, fails the run.
NOT_COMPARED = {
    "ape-encoder-lavc.mp3": "a tag fixture of eight frames, written for the encoder claims of the tag parsers: its audio is no decoder test",
    "id3v22-tss-names-encoder.mp3": "a tag fixture of eight frames, written for the encoder claims of the tag parsers: its audio is no decoder test",
    "id3v24-tsse-ffmpeg.mp3": "a tag fixture of eight frames, written for the encoder claims of the tag parsers: its audio is no decoder test",
}


def default_files():
    every = sorted(FIXTURES.glob("mpeg-audio/**/*.mp3")) + sorted(FIXTURES.glob("mpeg-audio/**/*.mp2")) + sorted(FIXTURES.glob("mpeg-audio/**/*.mp1"))
    unknown = sorted(set(NOT_COMPARED) - {p.name for p in every})
    if unknown:
        raise SystemExit(f"NOT_COMPARED names no fixture: {', '.join(unknown)}")
    return [p for p in every if p.name not in NOT_COMPARED]


def is_header_frame(data, offset, length):
    frame = data[offset:offset + min(length, 200)]
    return any(magic in frame for magic in MAGICS)


def minimp3_reference(binary, path, work):
    """(raw bytes of the floats at the analyzer's positions, channels, skipped position ranges), or None where minimp3 decodes nothing."""
    target = work / "minimp3.f32"
    frames = minimp3_tool.decode_pcm(binary, path, target)
    decoded = [f for f in frames if f["samples"]]
    if not decoded:
        return None
    data = path.read_bytes()
    raw = target.read_bytes()
    channels = decoded[0]["channels"]
    out = bytearray()
    skipped = []
    position = 0
    cursor = 0
    for number, frame in enumerate(frames):
        count = frame["samples"] * frame["channels"] * 4
        if number == 0 and frame["samples"] and is_header_frame(data, frame["offset"], frame["bytes"]):
            cursor += count
            continue
        if frame["samples"] and frame["channels"] != channels:
            return None
        if frame["samples"] == 0:
            # A frame minimp3 skips is zeros here, and its positions are left out of the comparison.
            nominal = 384 if frame["layer"] == 1 else 576 if frame["layer"] == 3 and frame["hz"] < 32000 else 1152
            if frame["channels"] == 0:
                continue
            out += bytes(nominal * channels * 4)
            skipped.append((position, position + nominal))
            position += nominal
            continue
        out += raw[cursor:cursor + count]
        cursor += count
        position += frame["samples"]
    return bytes(out), channels, skipped, decoded[0]["layer"], decoded[0]["hz"]


# The positions of a file that the comparison with FFmpeg leaves out because FFmpeg differs there for a reason listed in EXPLAINED: [first, end), end exclusive. A difference
# outside them fails, so a change elsewhere in these files is not hidden by the listing, and a range that is not needed fails as stale.
FFMPEG_RANGES = {
    "external-nero-rom-mp3pro-cbr64.mp3": [[188352, 205056]],
    "external-nero-rom-mp3pro-vbr-low.mp3": [[134208, 135360], [188352, 205056]],
    # Plugger+ 0.4 and 8hz-MP3 0.2b (old Wine encoders; Fraunhofer's l3codecp 3.4 file listed below is MPEG-2). Only FFmpeg differs from the analyzer on the MPEG-1 files, in 3 to 5 granules of channel 0 with tails into
    # the next ones (up to 0.026 of full scale): mpg123 1.32.5 (`mpg123 -e f32 --no-gapless`), madplay 0.15.2b and minimp3 all match the analyzer (largest difference 8e-7, 1.4e-6 and 3e-7),
    # FFmpeg 6.1.1 differs from all four. The first granules are the ones listed in the ranges; the granules where it begins hold long blocks of plain stereo (no intensity or
    # mid/side stereo, no mixed blocks), e.g. external-plugger-pro-cbr granule 92: channel 0 has no big values and a part2_3_length of 2 bits (count1 region only), channel 1 288 big values.
    # The two MPEG-2 files (l3codeca 1.9 and l3codecp 3.4, joint stereo with mid/side and intensity stereo together) differ for the two causes in R2; their ranges are the granules where they show.
    "external-plugger-pro-cbr.mp3": [[52992, 54720]],
    "external-plugger-cbr.mp3": [[43776, 45504], [70848, 71424]],
    "external-plugger-original.mp3": [[43776, 45504], [70848, 71424]],
    "external-plugger-48k.mp3": [[65664, 67392]],
    "external-8hz-flags.mp3": [[52992, 54720]],
    "external-fhg-acm19-mpeg2.mp3": [[1152, 1728], [36288, 37440]],
    "external-fhg-acm34-mpeg2.mp3": [[0, 1728], [5760, 10944], [17856, 20736], [22464, 24192], [26496, 29376], [31680, 33408], [35712, 36864], [44928, 46080]],
}

# The positions only minimp3 outputs, which the analyzer does not, in the fixtures that have them: the frames it does not decode (a truncated or cut frame, a VBR header
# frame) and the output it withholds after a gap or an unavailable frame. Pinned, so that a change in what is withheld fails here, B2's return (nothing withheld after an
# unavailable frame) included; each span is also held to the rules: whole frames, then at most the withheld span (1,056 samples in Layer III, 480 in Layers I and II).
ONLY_IN_MINIMP3 = {
    "damaged-bitrate.mp3": [[3456, 4608]],
    "external-nero-rom-mp3pro-vbr-low.mp3": [[221184, 222336]],
    "external-blade080-stereo.mp3": [[69120, 73632], [87552, 88704]],
    "external-blade082-stereo.mp3": [[87552, 88704]],
    "external-shine-joint.mp3": [[87552, 88704]],
    "external-shine-music.mp3": [[87552, 88704]],
    "main-data-overflow.mp3": [[2304, 4512]],
    "reservoir-across-gap.mp3": [[4608, 5664]],
    "reservoir-before-stream-later.mp3": [[1152, 3360]],
    "reservoir-cut-start.mp3": [[0, 2208]],
    "reservoir-splice.mp3": [[4608, 6816]],
    "side-info-invalid.mp3": [[1152, 3360], [3456, 5664]],
    "sync-inside-header.mp3": [[5760, 6912]],
    "truncated-last-frame.mp3": [[5760, 6912]],
    "lame-joined-other-version.mp3": [[11520, 12672]],
    "lame-mid-stream.mp3": [[11520, 12672]],
    "lame-truncated-last-frame.mp3": [[8064, 9216]],
    "main-data-before-header-frame.mp3": [[0, 2208]],
    "main-data-in-header-frame.mp3": [[0, 2208]],
    # The same with 100 bytes of junk between the header frame and the audio: the first frames' main data begins in the header frame's unused bytes, which are not audio main
    # data (the bit reservoir holds the frames' main data only; the header frame has none), so the analyzer withholds the frames that need them (as it does without the junk),
    # and minimp3 decodes them from the header frame's bytes, skipping the junk, as it does in main-data-in-header-frame.mp3.
    "main-data-in-header-frame-after-junk.mp3": [[0, 2208]],
    # LAME 3.20 to 3.70 (Rarewares' Windows builds) end every stream with a frame cut short (the finding mpeg-audio.frame-truncated, which each of these manifests lists):
    # the analyzer decodes whole frames only, as in truncated-last-frame.mp3, and minimp3 outputs the last frame's 1,152 samples.
    "external-lame320w-cbr.mp3": [[88704, 89856]],
    "external-lame320w-vbr.mp3": [[88704, 89856]],
    "external-lame334w-cbr.mp3": [[88704, 89856]],
    "external-lame357w-cbr.mp3": [[87552, 88704]],
    "external-lame357w-vbr.mp3": [[87552, 88704]],
    "external-lame370w-cbr.mp3": [[88704, 89856]],
    "external-lame370w-vbr.mp3": [[88704, 89856]],
    # Plugger+, 8hz-MP3, M3E and Fraunhofer's l3codeca 1.9 (through acmenc) end every one of their test files with a frame cut short (l3codecp 3.3 and 3.4's files end whole) (mpeg-audio.frame-truncated, which each manifest lists):
    # the analyzer decodes whole frames only, as ISO/IEC 11172-3 and 13818-3 define a frame by its length, and so do mpg123 and libmad (checked on external-plugger-cbr and
    # external-fhg-acm19-mono-16k with mpg123 1.32 and madplay 0.15.2b: 76 and 52 frames, the analyzer's samples); only minimp3 outputs the last frame's samples.
    "external-8hz-48k.mp3": [[95616, 96768]],
    "external-8hz-cbr.mp3": [[87552, 88704]],
    "external-8hz-flags.mp3": [[87552, 88704]],
    "external-8hz-mono.mp3": [[87552, 88704]],
    "external-fhg-acm19-mono-16k.mp3": [[29952, 30528]],
    "external-fhg-acm19-mpeg2.mp3": [[42624, 43200]],
    "external-m3e-48k.mp3": [[97920, 99072]],
    "external-m3e-cbr.mp3": [[24192, 25344]],
    "external-m3e-mono.mp3": [[91008, 92160]],
    "external-m3e-vbr.mp3": [[88704, 89856]],
    "external-m3e-vbr4.mp3": [[88704, 89856]],
    "external-plugger-48k.mp3": [[95616, 96768]],
    "external-plugger-cbr.mp3": [[87552, 88704]],
    "external-plugger-flags.mp3": [[21888, 23040]],
    "external-plugger-mono.mp3": [[87552, 88704]],
    "external-plugger-original.mp3": [[87552, 88704]],
    "external-plugger-pro-cbr.mp3": [[87552, 88704]],
}


def frame_samples(layer, hz):
    """Samples of a frame: 384 in Layer I, 1,152 in Layer II, and in Layer III 1,152 (MPEG-1) or 576 (MPEG-2 and 2.5, whose rates are below 32 kHz)."""
    return 384 if layer == 1 else 576 if layer == 3 and hz < 32000 else 1152


def position_problems(path, outcome, layer, hz):
    if path.name.startswith("mixed-"):
        return []
    frame = frame_samples(layer, hz)
    withheld = 480 if layer in (1, 2) else 1056
    problems = []
    spans = outcome["notComparedOnlyInReference"]
    for first, end in spans:
        length = end - first
        if length % frame not in (0, withheld % frame) or length < min(frame, withheld):
            problems.append(f"minimp3 decodes {length} samples at {first} that the analyzer does not output: not whole frames of {frame} with or without {withheld} withheld")
    if spans != ONLY_IN_MINIMP3.get(path.name, []):
        problems.append(f"minimp3 decodes the positions {spans} that the analyzer does not output, expected {ONLY_IN_MINIMP3.get(path.name, [])}")
    return problems


def ffmpeg_reference(ffmpeg, path, layer):
    # The float decoder of the layer: FFmpeg's default decoders of Layers I and II are fixed-point and round to 16 bits.
    decoder = {1: "mp1float", 2: "mp2float", 3: "mp3float"}.get(layer)
    result = subprocess.run([ffmpeg, "-v", "error", "-nostdin", "-flags2", "+skip_manual", *(["-c:a", decoder] if decoder else []), "-i", str(path),
                             "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True)
    if result.returncode != 0 or not result.stdout:
        return None
    return result.stdout


def compare(manifest, reference, channels, label, extra=()):
    raw = manifest.parent / f"{label}.f32"
    raw.write_bytes(reference)
    output = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest), "--reference", str(raw), "--reference-format", "float32",
                             "--reference-channels", str(channels), "--json", *extra], capture_output=True, text=True)
    if output.returncode == 2:
        return None, [f"{label}: {output.stderr.strip()}"]
    result = json.loads(output.stdout)
    problems = []
    if output.returncode != 0:
        worst = max(result["channels"], key=lambda c: c["max"])
        problems.append(f"{label}: outside the bound (largest {worst['max']:.3g}, RMS {worst['rms']:.3g})")
    return result, problems


def mctoolamed_reference(decoder, path, stream):
    """The multichannel decoder's output (a black box derived from the ISO reference software) as one raw file of interleaved little-endian 16-bit samples in the analyzer's channel
    order, or None: the full-bandwidth channels in the order of its channel files, which the layout follows, then the LFE, which the analyzer holds for 96 samples (the decoder
    writes twelve a frame)."""
    channels, lfe = mctoolamed_tool.decode_pcm(decoder, path)
    layout = stream["channelLayout"]
    wide = [c for speaker, c in zip((x for x in layout if x != "LFE"), channels)]
    if len(wide) != sum(1 for x in layout if x != "LFE") or ("LFE" in layout) != (lfe is not None):
        return None
    samples = stream["samples"]
    planes = [c[:2 * samples] for c in wide]
    if lfe is not None:
        planes.append(b"".join(lfe[i:i + 2] * 96 for i in range(0, len(lfe), 2))[:2 * samples])
    if any(len(p) != 2 * samples for p in planes):
        return None
    out = bytearray(2 * samples * len(planes))
    for c, plane in enumerate(planes):
        out[2 * c::2 * len(planes)] = plane[0::2]
        out[2 * c + 1::2 * len(planes)] = plane[1::2]
    return bytes(out)


def compare_multichannel(manifest, stream, reference):
    raw = manifest.parent / "mctoolamed.s16"
    raw.write_bytes(reference)
    output = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest), "--reference", str(raw), "--reference-format", "int16",
                             "--reference-channels", str(stream["channels"]), "--json"], capture_output=True, text=True)
    if output.returncode == 2:
        return [f"mctoolamed: {output.stderr.strip()}"]
    result = json.loads(output.stdout)
    if output.returncode != 0:
        worst = max(result["channels"], key=lambda c: c["max"])
        return [f"mctoolamed: outside the bound (largest {worst['max']:.3g} {result['unit']}, RMS {worst['rms']:.3g} {result['unit']})"]
    return []


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--keep", type=pathlib.Path, help="keep the dumps and the references in this folder")
    args = parser.parse_args(argv)
    files = args.files or default_files()
    if not files:
        print("no MPEG audio file")
        return 2
    binary = minimp3_tool.build_pcm()
    failed = compared = multichannel = 0
    seen_multiple = set()
    seen_none = set()
    decoder = None
    mixed_directory = pathlib.Path(tempfile.mkdtemp(prefix="mixed-"))
    if not args.files:
        files = files + [m for name in MIXED_SOURCES if (m := mixed_variant(FIXTURES / "mpeg-audio" / name, mixed_directory, binary))]
    with tempfile.TemporaryDirectory() as temporary:
        base = args.keep or pathlib.Path(temporary)
        base.mkdir(parents=True, exist_ok=True)
        for number, path in enumerate(files):
            dump = base / f"dump{number}"
            result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
            if result.returncode != 0:
                print(f"FAIL {path.name}: PcmDump exits {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-300:]}")
                failed += 1
                continue
            manifest = dump / f"0000-{path.name}.pcm.json"
            streams = json.loads(manifest.read_text())["streams"] if manifest.exists() else []
            if len(streams) != 1:
                table, seen = (NO_STREAM, seen_none) if not streams else (MULTIPLE_STREAMS, seen_multiple)
                seen.add(path.name)
                if path.name in table:
                    print(f"skip {path.name}: {len(streams)} decoded streams (one is compared)")
                else:
                    print(f"FAIL {path.name}: {len(streams)} decoded streams, and the file is not in {'NO_STREAM' if not streams else 'MULTIPLE_STREAMS'}")
                    failed += 1
                continue
            work = base / f"work{number}"
            work.mkdir(exist_ok=True)
            problems = []
            notes = []
            agreeing = set()
            if streams[0]["channels"] > 2:
                # The multichannel extension: minimp3 and FFmpeg decode the base stereo only, so mctoolamed is the reference.
                decoder = decoder or mctoolamed_tool.build()
                reference = mctoolamed_reference(decoder, path, streams[0])
                problems = [f"mctoolamed writes other channels than the analyzer's {streams[0]['channels']}"] if reference is None else compare_multichannel(manifest, streams[0], reference)
                note = f"mctoolamed, {streams[0]['channels']} channels"
                for name, reason in EXPLAINED.get(path.name, {}).items():
                    listed = [p for p in problems if p.startswith(name + ":")]
                    if listed:
                        problems = [p for p in problems if p not in listed]
                        note += f"; {name} listed: {reason}"
                    elif name == "mctoolamed" and reference is not None:
                        problems.append(f"{name} agrees with the analyzer, so its EXPLAINED entry for {path.name} is not needed: remove it")
                multichannel += 1
                failed += 1 if problems else 0
                print(f"FAIL {path.name}: " + "; ".join(problems) if problems else f"ok   {path.name} ({note})")
                continue
            reference = minimp3_reference(binary, path, work)
            layer = reference[3] if reference is not None else None
            if reference is None:
                notes.append("minimp3 decodes no frames or changes its channels")
            else:
                raw, channels, skipped, _, _ = reference
                if streams[0]["channels"] != channels:
                    problems.append(f"the analyzer decoded {streams[0]['channels']} channels, minimp3 {channels}")
                else:
                    outcome, found = compare(manifest, raw, channels, "minimp3", [a for first, end in skipped for a in ("--exclude", f"{first}:{end}")])
                    problems += found
                    if outcome is not None:
                        compared += 1
                        if not found:
                            agreeing.add("minimp3")
                        only = outcome["notComparedOnlyInAnalyzer"] + outcome["notComparedOnlyInReference"]
                        if only:
                            notes.append(f"minimp3: positions only one side has: analyzer {outcome['notComparedOnlyInAnalyzer']}, reference {outcome['notComparedOnlyInReference']}")
                        problems += position_problems(path, outcome, layer, reference[4])
            # FFmpeg and minimp3 do not treat mixed blocks alike (they differ by up to 1.5 of full scale on these files), so only minimp3 is the reference for them.
            second = None if path.name.startswith("mixed-") else ffmpeg_reference(args.ffmpeg, path, layer)
            if path.name.startswith("mixed-"):
                notes.append("mixed blocks: minimp3 alone is the reference")
            elif second is None:
                notes.append("ffmpeg decodes nothing")
            else:
                outcome, found = compare(manifest, second, streams[0]["channels"], "ffmpeg")
                if outcome is not None and not found:
                    agreeing.add("ffmpeg")
                for number_, stale in enumerate(FFMPEG_RANGES.get(path.name, [])):
                    # A range is needed only if the bound fails with every other range left out and this one not.
                    others = [r for k, r in enumerate(FFMPEG_RANGES[path.name]) if k != number_]
                    _, without = compare(manifest, second, streams[0]["channels"], "ffmpeg", [a for first, end in others for a in ("--exclude", f"{first}:{end}")])
                    if not without:
                        problems.append(f"the comparison with FFmpeg is within the bound without the range {stale} of FFMPEG_RANGES: remove it")
                if found and path.name in FFMPEG_RANGES:
                    _, again = compare(manifest, second, streams[0]["channels"], "ffmpeg", [a for first, end in FFMPEG_RANGES[path.name] for a in ("--exclude", f"{first}:{end}")])
                    # Listed (and so removed below) only if the bound holds outside the ranges; the message of a failure there has no "ffmpeg:" prefix to be listed by.
                    found = ["ffmpeg: outside the bound only at the positions of FFMPEG_RANGES"] if not again else [f"FFmpeg is outside the bound beyond the positions of FFMPEG_RANGES ({again[0]})"]
                problems += found
                if outcome is not None:
                    only = outcome["notComparedOnlyInAnalyzer"] + outcome["notComparedOnlyInReference"]
                    if only:
                        notes.append(f"ffmpeg: positions only one side has: analyzer {outcome['notComparedOnlyInAnalyzer']}, reference {outcome['notComparedOnlyInReference']}")
            note = "; ".join(notes)
            for reference, reason in EXPLAINED.get(path.name, {}).items():
                listed = [p for p in problems if p.startswith(reference + ":")]
                if listed:
                    problems = [p for p in problems if p not in listed]
                    note = (note + "; " if note else "") + f"{reference} listed: {reason}"
                elif reference in agreeing:
                    problems.append(f"{reference} agrees with the analyzer, so its EXPLAINED entry for {path.name} is not needed: remove it")
            if problems:
                failed += 1
                print(f"FAIL {path.name}: " + "; ".join(problems))
            else:
                print(f"ok   {path.name}" + (f" ({note})" if note else ""))
    if not args.files:
        for name in sorted(set(MULTIPLE_STREAMS) - seen_multiple):
            print(f"FAIL {name}: listed in MULTIPLE_STREAMS and has one decoded stream: remove it")
            failed += 1
        for name in sorted(set(NO_STREAM) - seen_none):
            print(f"FAIL {name}: listed in NO_STREAM and decodes a stream: remove it")
            failed += 1
    print(f"{compared} files compared with minimp3, {multichannel} with mctoolamed, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
