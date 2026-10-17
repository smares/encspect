#!/usr/bin/env python3
"""Writes the byte-exact MP4 fixtures in tests/fixtures/mp4 and the raw AAC (ADTS and ADIF) ones in
tests/fixtures/adts.

The MP4 files are built box by box (mp4_boxes.py): every size, table and offset is under control, and the
access units are AAC raw data blocks that decoders play as silence, so the files decode while the parser
is checked against exactly what was written. Each fixture has a <name>.expected.json manifest:

- "length": the file's length, and "format": the format the analyzer must detect.
- "boxes": every box the analyzer must report, as the path of box types from the top level, its offset and
  the bytes it takes in its parent.
- "streams": the streams the codecs report: codec, output sample rate, channels, channel mode, samples,
  whether the bitrate varies and, where one is named, the encoder and its confidence.
- "units": every access unit (ADTS: frame; MPEG audio: frame) of those streams, as offset and length.
- "fields": node paths (names separated by "/", from the report's root) and the value each must have.
- "findings": the findings the analyzer must produce, as a code plus a string its message contains; no
  others.
- "crosscheck": what crosscheck_mp4_fixtures.py confirms with independent tools: "ffprobe" (the packets),
  "mp4box" (the box structure) and "faad" (the object type and SBR and PS that faad -i reads from the
  configuration, or the ADTS duration and rate). A tool that cannot read a fixture is listed in "skip" with
  the reason.

The ADTS frames follow ISO/IEC 14496-3 1.A.2.2; with protection, the CRC covers the header and, in each raw
data block, the first 192 bits of each single channel element after its ID (filled up with zeros where it is
shorter), as fdk-aac's decoder checks it; with several raw data blocks, the header's CRC covers the header
and the block positions, and each block ends in its own CRC.

The script uses only the standard library and is deterministic, so CI reruns it and fails when the
committed files differ.
"""

import json
import pathlib
import struct
import sys

from mp4_boxes import (
    Bits, Box, Track, asc, box, chan, chnl, cpe, data_atom, dinf, elst, esds, freeform, fullbox, ftyp, hdlr, ilst_meta, interleave, item, mdhd, meta_hdlr, movie, mp4a,
    mvhd, pce_5_1, raw_data_block, rate_index, sbgp, sgpd_roll, stco, stsc, stsd, stsz, stts, text_item, tkhd, u8, u16, u32, u64, udta, units, write,
)

ROOT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures"
MP4 = ROOT / "mp4"
ADTS = ROOT / "adts"

STSD = "MP4/moov/trak/mdia/minf/stbl/stsd/mp4a"
ESDS = STSD + "/esds/ES_Descriptor/DecoderConfigDescriptor"
ASC = ESDS + "/DecoderSpecificInfo/AudioSpecificConfig"
MOOV_AFTER = ["mp4.moov-after-mdat", "not optimized for streaming"]


# The speakers of AAC's channel configurations 1, 2, 6 and 12 in their order (ISO/IEC 14496-3 Table
# 1.19); the 5.1 program config elements here list the same elements in the same order.
SPEAKERS = {1: ["front center"], 2: ["front left", "front right"],
            6: ["front center", "front left", "front right", "back left", "back right", "LFE"],
            8: ["front center", "front left", "front right", "side left", "side right", "back left", "back right", "LFE"]}


def stream(codec, rate, channels, mode, samples, vbr=False, encoder=None, writer=None):
    entry = {"codec": codec, "sample_rate": rate, "channels": channels, "channel_mode": mode, "channel_layout": SPEAKERS[channels],
             "samples": samples, "is_vbr": vbr}
    if encoder:
        entry["encoder"] = list(encoder)
    if writer:
        entry["writer"] = list(writer)
    return entry


def boxes_of(records, length):
    """The boxes as the analyzer shows them: a box that runs past the end of the file is cut there."""
    def printable(path):
        return "".join(c if 0x20 <= ord(c) < 0x7F or c == "©" or c == "/" else "." for c in path)

    return [[printable(path), offset, min(size, length - offset)] for path, offset, size in records if offset < length]


def unit_list(offsets, samples, chunk):
    result = []
    for number, start in enumerate(offsets):
        position = start
        for sample in samples[number * chunk:(number + 1) * chunk]:
            result.append([position, len(sample)])
            position += len(sample)
    return result


def aac_entry(config, rate, channels, average=0, maximum=0, oti=0x40, extra=()):
    return mp4a(channels, rate, esds(oti, config, average, maximum), *extra)


# --- MP4 fixtures ---------------------------------------------------------------------------------

def faad_line(label, channels, rate):
    return {"label": label, "channels": channels, "rate": rate}


def plain(name, description, config, layout, codec, rate, channels, mode, samples=None, output_rate=None, output_channels=None, frame=1024,
          findings=(), fields=None, faad=None, chunk=10, timescale=None, ffprobe_channels=None, faad_channels=None, extra=(), skip=None, **options):
    samples = samples or units(layout, 30)
    output_rate = output_rate or rate
    entry = aac_entry(config, rate, channels, extra=extra)
    track = Track(1, samples, rate, channels, entry, timescale=timescale, chunk=chunk)
    data, records, offsets = movie([track], **options)
    entry = stream(codec, output_rate, output_channels or channels, mode, len(samples) * frame, vbr=True)
    if ffprobe_channels is not None:
        entry["ffprobe_channels"] = ffprobe_channels
    manifest = {
        "description": description,
        "length": len(data),
        "format": "MP4",
        "boxes": boxes_of(records, len(data)),
        "streams": [entry],
        "units": unit_list(offsets[0], samples, chunk),
        "fields": fields or {},
        "findings": [list(f) for f in findings],
        "crosscheck": {"ffprobe": True, "mp4box": True,
                       "faad": faad_line(faad, output_channels or channels if faad_channels is None else faad_channels, output_rate) if faad else None},
    }
    if skip:
        manifest["crosscheck"]["skip"] = skip
        manifest["crosscheck"]["ffprobe"] = "ffprobe" not in skip
        manifest["crosscheck"]["mp4box"] = "mp4box" not in skip
    return name, data, manifest


def he_aac_configs():
    lc = asc(2, 44100, 2)
    return [
        plain("lc-implicit", "AAC LC, stereo, 44100 Hz, with a configuration that signals nothing about SBR: SBR stays possible.", lc, "stereo",
              "AAC LC", 44100, 2, "Stereo", findings=[],
              fields={ASC + "/Audio Object Type": "2 (AAC LC)", ASC + "/Sample Rate": "44100 Hz", ASC + "/Channel Configuration": "2 (stereo)",
                      ASC + "/Frame Length": "1024 samples"}, faad="LC AAC"),
        plain("lc-sbr-absent", "AAC LC whose configuration signals SBR absent through the sync extension 0x2B7, as ffmpeg writes it.",
              asc(2, 44100, 2, sbr=False, signaling="backward"), "stereo", "AAC LC", 44100, 2, "Stereo",
              fields={ASC + "/Sync Extension": "SBR absent", ASC + "/Sync Extension/HasSbr": "false"}, faad="LC AAC"),
        plain("he-aac-hierarchical", "HE-AAC signaled hierarchically: audio object type 5, the output rate, then the AAC LC core at half the rate.",
              asc(2, 22050, 2, sbr=True, signaling="hierarchical", extension_rate=44100), "stereo", "HE-AAC", 22050, 2, "Stereo", output_rate=44100,
              frame=2048, fields={ASC + "/Audio Object Type": "5 (SBR)", ASC + "/Extension Sample Rate": "44100 Hz", ASC + "/Core Audio Object Type": "2 (AAC LC)"},
              faad="LC AAC"),
        plain("he-aac-backward", "HE-AAC signaled backward compatibly: an AAC LC configuration followed by the sync extension 0x2B7 with SBR present.",
              asc(2, 22050, 2, sbr=True, signaling="backward", extension_rate=44100), "stereo", "HE-AAC", 22050, 2, "Stereo", output_rate=44100, frame=2048,
              fields={ASC + "/Sync Extension": "SBR present", ASC + "/Sync Extension/Extension Sample Rate": "44100 Hz"}, faad="HE AAC"),
        plain("he-aac-v2-hierarchical", "HE-AAC v2 signaled hierarchically: audio object type 29 over a mono AAC LC core.",
              asc(2, 22050, 1, sbr=True, ps=True, signaling="hierarchical", extension_rate=44100), "mono", "HE-AAC v2", 22050, 1, "Parametric stereo",
              output_rate=44100, output_channels=2, frame=2048, fields={ASC + "/Audio Object Type": "29 (PS)"}, faad="LC AAC"),
        plain("he-aac-v2-backward", "HE-AAC v2 signaled backward compatibly: the sync extensions 0x2B7 (SBR) and 0x548 (PS) after a mono AAC LC configuration.",
              asc(2, 22050, 1, sbr=True, ps=True, signaling="backward", extension_rate=44100), "mono", "HE-AAC v2", 22050, 1, "Parametric stereo",
              output_rate=44100, output_channels=2, frame=2048, fields={ASC + "/Sync Extension": "SBR and PS present", ASC + "/Sync Extension/HasPs": "true"},
              faad="HE AAC"),
        plain("he-aac-mono", "HE-AAC over a mono core, signaled hierarchically with object type 5: PS stays possible.",
              asc(2, 22050, 1, sbr=True, signaling="hierarchical", extension_rate=44100), "mono", "HE-AAC", 22050, 1, "Mono",
              output_rate=44100, frame=2048, faad="LC AAC", ffprobe_channels=2, faad_channels=2),
        plain("pce-5-1", "AAC LC 5.1 at 48000 Hz whose channel configuration 0 leaves the layout to a program config element in the configuration.",
              asc(2, 48000, 0, pce=pce_5_1), "5.1", "AAC LC", 48000, 6, "6 channels", samples=units("5.1", 20),
              fields={ASC + "/Channel Configuration": "0 (defined in a program config element)", ASC + "/Program Config Element": "6 channels",
                                           ASC + "/Program Config Element/Front Channel Elements": "2"}, faad="LC AAC", faad_channels=0),
    ]


# Core Audio's layout tags (CoreAudioBaseTypes.h): the upper 16 bits name the layout, the lower the channels.
TAG_STEREO = (101 << 16) | 2
TAG_AAC_5_1 = (124 << 16) | 6
TAG_MPEG_7_1_C = (128 << 16) | 8
USE_BITMAP = 1 << 16
USE_DESCRIPTIONS = 0
CHAN = STSD + "/chan"
CHNL = STSD + "/chnl"
DIFFERS = "mp4.channel-layout-differs"
AAC_5_1 = "5.1 (front center, front left, front right, back left, back right, LFE)"


def channel_layouts():
    """The container's statement of the speakers, in QuickTime's chan atom (a layout tag, a channel bitmap or
    channel descriptions) and ISO/IEC 14496-12's chnl box (a defined layout or a position per channel), next to
    AAC configurations that state their own."""
    five = asc(2, 48000, 6)
    lc = "AAC LC"
    return [
        plain("chan-layout-tag", "AAC LC 5.1 with a chan atom whose layout tag is AAC_5_1 (MPEG_5_1_D: C L R Ls Rs LFE), as Apple's encoder "
              "labels AAC 5.1: the same speakers as the channel configuration.", five, "5.1", lc, 48000, 6, "5.1", samples=units("5.1", 20),
              findings=[], extra=[chan(TAG_AAC_5_1)],
              fields={CHAN: AAC_5_1, CHAN + "/Layout Tag": "MPEG_5_1_D: C L R Ls Rs LFE (0x007C0006)"}, faad="LC AAC"),
        plain("chan-bitmap", "AAC LC stereo with a chan atom that names its channels by the bitmap: left and right (bits 0 and 1).",
              asc(2, 44100, 2), "stereo", lc, 44100, 2, "Stereo", findings=[], extra=[chan(USE_BITMAP, 0x3)],
              fields={CHAN: "stereo (front left, front right)", CHAN + "/Layout Tag": "UseChannelBitmap (0x00010000)", CHAN + "/Channel Bitmap": "0x00000003"},
              faad="LC AAC"),
        plain("chan-bitmap-side", "AAC LC 5.1 with a chan atom whose bitmap puts the surround pair at the sides (left and right surround "
              "direct, bits 9 and 10), where the channel configuration's surround pair is at the back: the codec's speakers count, with a warning.",
              five, "5.1", lc, 48000, 6, "5.1", samples=units("5.1", 20), extra=[chan(USE_BITMAP, 0x60F)],
              findings=[[DIFFERS, "states 5.1 (front left, front right, front center, LFE, side left, side right), but the " + lc]],
              fields={CHAN: "5.1 (front left, front right, front center, LFE, side left, side right)", CHAN + "/Channel Bitmap": "0x0000060F"},
              faad="LC AAC"),
        plain("chan-descriptions", "AAC LC 5.1 with a chan atom that describes each channel by its label, in the WAVE order L R C LFE Ls Rs, "
              "the first with spherical coordinates: the same speakers as the channel configuration in another order, so no finding.",
              five, "5.1", lc, 48000, 6, "5.1", samples=units("5.1", 20), findings=[],
              extra=[chan(USE_DESCRIPTIONS, 0, [(1, 2, (-30.0, 0.0, 1.0)), (2, 0, (0, 0, 0)), (3, 0, (0, 0, 0)), (4, 0, (0, 0, 0)),
                                                (5, 0, (0, 0, 0)), (6, 0, (0, 0, 0))])],
              fields={CHAN: "5.1 (front left, front right, front center, LFE, back left, back right)", CHAN + "/Channel Descriptions": "6",
                      CHAN + "/Channel Description": "Left (1)", CHAN + "/Channel Description/Coordinates": "-30, 0, 1"}, faad="LC AAC"),
        plain("chan-mono-core", "AAC LC mono with a chan atom that says stereo: its access units carry no parametric stereo, so they stay mono, "
              "with a warning.", asc(2, 44100, 1), "mono", lc, 44100, 1, "Mono", extra=[chan(TAG_STEREO)],
              findings=[[DIFFERS, "states stereo (front left, front right), but the AAC LC stream states mono"]],
              fields={CHAN: "stereo (front left, front right)", CHAN + "/Layout Tag": "Stereo: L R (0x00650002)"}, faad="LC AAC", faad_channels=2),
        plain("chan-completes-reserved", "AAC LC whose configuration names the reserved channel configuration 8, so it states no speakers, "
              "with a chan atom that says stereo: the stream reports the container's layout.", asc(2, 44100, 8), "stereo", lc, 44100, 2,
              "Reserved", extra=[chan(TAG_STEREO)],
              findings=[["aac.reserved-channel-configuration", "channel configuration 8 is reserved"]],
              fields={CHAN: "stereo (front left, front right)"}, faad=None,
              skip={"ffprobe": "ffmpeg's decoder rejects the reserved channel configuration.", "faad": "faad2 rejects the reserved channel configuration.",
                    "faad_trace": "faad2 rejects the reserved channel configuration."}),
        plain("chan-7-1-rear", "AAC LC 7.1 (channel configuration 12: C L R Ls Rs Lsr Rsr LFE) with a chan atom whose layout tag is "
              "MPEG_7_1_C (L R C LFE Ls Rs Rls Rrs): with a rear pair, both put the surround pair at the sides, so they agree.",
              asc(2, 48000, 12), "7.1", lc, 48000, 8, "7.1", samples=units("7.1", 20), findings=[], extra=[chan(TAG_MPEG_7_1_C)],
              fields={CHAN: "7.1 (front left, front right, front center, LFE, side left, side right, back left, back right)",
                      ASC + "/Channel Configuration": "12 (7.1: center, left/right, left/right surround, left/right rear, LFE)"},
              skip={"faad": "faad2 2.11 does not read channel configuration 12.", "faad_trace": "faad2 2.11 does not read channel configuration 12."}),
        plain("chnl-defined", "AAC LC 5.1 with a chnl box that gives ISO/IEC 23091-3's ChannelConfiguration 6 (C L R Ls Rs LFE) and omits "
              "no channel.", five, "5.1", lc, 48000, 6, "5.1", samples=units("5.1", 20), findings=[], extra=[chnl(defined=6)],
              fields={CHNL: AAC_5_1, CHNL + "/Defined Layout": "6 (" + AAC_5_1 + ")", CHNL + "/Omitted Channels": "0x0000000000000000"},
              faad="LC AAC"),
        plain("chnl-omitted", "AAC LC 5.1 with a chnl box that gives ChannelConfiguration 6 but omits its sixth channel, the LFE: 5.0 "
              "against the configuration's 5.1, a warning.", five, "5.1", lc, 48000, 6, "5.1", samples=units("5.1", 20), extra=[chnl(defined=6, omitted=1 << 5)],
              findings=[[DIFFERS, "states 5.0 (front center, front left, front right, back left, back right)"]],
              fields={CHNL: "5.0 (front center, front left, front right, back left, back right)", CHNL + "/Omitted Channels": "0x0000000000000020"},
              faad="LC AAC", skip={"ffprobe": "ffmpeg 6.1 does not read a chnl box that omits channels (\"Not yet implemented\")."}),
        plain("chnl-positions", "AAC LC stereo with a chnl box that lists a speaker position per channel (0, left front, and 1, right front) "
              "and an object count of 0.", asc(2, 44100, 2), "stereo", lc, 44100, 2, "Stereo", findings=[],
              extra=[chnl(positions=(0, 1), objects=0)],
              fields={CHNL: "stereo (front left, front right)", CHNL + "/Stream Structure": "3 (channels and objects)",
                      CHNL + "/Speaker Position": "0 (left front)", CHNL + "/Object Count": "0"}, faad="LC AAC",
              skip={"mp4box": "GPAC 2.2 reads the omitted channels map in place of the speaker positions, runs past the box and drops it."}),
    ]


def usac_config():
    """UsacConfig (ISO/IEC 23003-3 5.2): 44100 Hz, 1024 core samples with 2:1 SBR, stereo, one channel pair element with SBR and stereoConfigIndex
    0, no configuration extension."""
    bits = Bits()
    bits.put(31, 5).put(42 - 32, 6).put(rate_index(44100), 4).put(2, 4)
    bits.put(rate_index(44100), 5).put(3, 3).put(2, 5)
    bits.put(0, 4)  # numElements - 1 = 0
    bits.put(1, 2)  # ID_USAC_CPE
    bits.put(0, 1).put(1, 1)  # tw_mdct, noiseFilling
    bits.put(0, 1).put(0, 1).put(0, 1)  # harmonicSBR, bs_interTes, bs_pvc
    bits.put(5, 4).put(9, 4).put(0, 1).put(0, 1)  # dflt_start_freq, dflt_stop_freq, no header extras
    bits.put(0, 2)  # stereoConfigIndex
    bits.put(0, 1)  # usacConfigExtensionPresent
    return bits.align().bytes()


def xhe():
    config = usac_config()
    samples = [bytes([0x80 if i % 10 == 0 else 0x00]) + bytes((i * 13) % 50 + 20) for i in range(30)]
    entry = aac_entry(config, 44100, 2)
    track = Track(1, samples, 44100, 2, entry, durations=[2048] * 30)
    data, records, offsets = movie([track], ftyp_box=ftyp(b"mp42", 0, (b"mp42", b"isom")))
    return "xhe-aac-config", data, {
        "description": "xHE-AAC: audio object type 42 with a UsacConfig of one channel pair element with 2:1 SBR; each access unit starts with usacIndependencyFlag.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("xHE-AAC (USAC)", 44100, 2, "Stereo", 30 * 2048, vbr=True)],
        "units": unit_list(offsets[0], samples, 10),
        "fields": {ASC + "/Audio Object Type": "42 (USAC)", ASC + "/UsacConfig/Core SBR Frame Length Index": "3 (1024 core samples, 2:1 SBR, 2048 output samples)",
                   ASC + "/UsacConfig/UsacDecoderConfig": "1 element", ASC + "/UsacConfig/UsacDecoderConfig/Element": "CPE"},
        "findings": [["aac.usac-frames-not-analyzed", "usacIndependencyFlag"]],
        "crosscheck": {"ffprobe": False, "mp4box": True, "mp4box_samples": True, "faad": None,
                       "skip": {"ffprobe": "ffmpeg 6.1 does not implement audio object type 42 and lists no stream; MP4Box lists the samples instead.",
                                "faad": "faad2 2.11 does not read USAC configurations (it fails to open the file)."}},
    }


# exhale's loudness info (ISO/IEC 23003-4 loudnessInfoSet) as its bitStreamWriter.cpp writes it without SBR: one loudnessInfo with a sample
# peak and one program loudness measurement, then loudnessInfoSetExtPresent set and a terminating extension type to fill 9 bytes.
EXHALE_LOUDNESS = bytes.fromhex("001000530823385C00")


def usac_layout_config(fill_element):
    """UsacConfig (ISO/IEC 23003-3 5.2): 44100 Hz, 1024 samples without SBR, stereo; an audio pre-roll extension element with an empty
    configuration, a channel pair element with noise filling, a fill extension element where fill_element is set, and a loudness info
    configuration extension."""
    bits = Bits()
    bits.put(31, 5).put(42 - 32, 6).put(rate_index(44100), 4).put(2, 4)
    bits.put(rate_index(44100), 5).put(1, 3).put(2, 5)
    bits.put(2 if fill_element else 1, 4)  # numElements - 1
    bits.put(3, 2).put(3, 4).put(0, 4).put(0, 1).put(0, 1)  # ID_USAC_EXT, ID_EXT_ELE_AUDIOPREROLL, no config, no default length, no fragmentation
    bits.put(1, 2).put(0, 1).put(1, 1)  # ID_USAC_CPE, tw_mdct, noiseFilling
    if fill_element:
        bits.put(3, 2).put(0, 4).put(0, 4).put(0, 1).put(0, 1)  # ID_USAC_EXT, ID_EXT_ELE_FILL
    bits.put(1, 1).put(0, 2).put(2, 4).put(len(EXHALE_LOUDNESS), 4)  # one configuration extension: ID_CONFIG_EXT_LOUDNESS_INFO
    for byte in EXHALE_LOUDNESS:
        bits.put(byte, 8)
    return bits.align().bytes()


def xhe_layouts():
    """The element layout exhale writes, which names it as the encoder, the same with the fill element Fraunhofer's demonstration files and
    libxaac write, which names none, exhale's layout with a tool atom that names other software, the same with a tool atom that names software that cannot write xHE-AAC, and MPEG Surround 2-1-2."""
    out = []
    for name, description, fill_element, tool, encoder, findings in [
        ("xhe-aac-exhale-layout", "xHE-AAC whose UsacConfig has the layout exhale writes: an audio pre-roll element first, then a channel pair "
         "element, no fill element, and loudness info as its only configuration extension. It names exhale as the encoder with medium "
         "confidence.", False, None, ("exhale", "Medium"), []),
        ("xhe-aac-fill-element", "xHE-AAC with exhale's layout but for a fill element after the channel pair element, as Fraunhofer's "
         "demonstration files and libxaac write: no encoder is named.", True, None, None, []),
        ("xhe-aac-exhale-layout-claim", "exhale's layout of the UsacConfig with a tool atom that names other software: the stream's evidence "
         "names exhale as the encoder and the tool atom names the writer, two facts that do not conflict.", False, "hand-built", ("exhale", "Medium"),
         []),
        ("xhe-aac-writer-other-format", "exhale's layout of the UsacConfig with a tool atom that names FAAC 1.30: the stream's evidence "
         "names exhale as the encoder, and FAAC, which writes AAC and not xHE-AAC, as the writer, which is a finding of its own.", False, "FAAC 1.30", ("exhale", "Medium"),
         [["aac.writer-other-format", "which writes AAC, not xHE-AAC (USAC)"]]),
    ]:
        config = usac_layout_config(fill_element)
        samples = [bytes([0x80 if i % 10 == 0 else 0x00]) + bytes((i * 13) % 50 + 20) for i in range(30)]
        track = Track(1, samples, 44100, 2, aac_entry(config, 44100, 2))
        extra = [udta(ilst_meta(text_item(b"\xa9too", tool)))] if tool else []
        data, records, offsets = movie([track], ftyp_box=ftyp(b"mp42", 0, (b"mp42", b"isom")), moov_extra=extra)
        elements = ASC + "/UsacConfig/UsacDecoderConfig"
        out.append((name, data, {
            "description": description, "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
            "streams": [stream("xHE-AAC (USAC)", 44100, 2, "Stereo", 30 * 1024, vbr=True, encoder=encoder, writer=(tool, "Low") if tool else None)],
            "units": unit_list(offsets[0], samples, 10),
            "fields": {elements: f"{3 if fill_element else 2} elements", elements + "/Element[0]/Extension Element Type": "3 (audio pre-roll)",
                       elements + "/Element[1]": "CPE", ASC + "/UsacConfig/UsacConfigExtension/Extension Type": "2 (loudness info)"},
            "findings": [["aac.usac-frames-not-analyzed", "usacIndependencyFlag"]] + findings,
            "crosscheck": {"ffprobe": False, "mp4box": True, "mp4box_samples": True, "faad": None,
                           "skip": {"ffprobe": "ffmpeg 6.1 does not implement audio object type 42 and lists no stream; MP4Box lists the samples instead.",
                                    "faad": "faad2 2.11 does not read USAC configurations (it fails to open the file)."}},
        }))
    return out + [xhe_mps212()]


def xhe_mps212():
    """A channel pair element with MPEG Surround 2-1-2 stereo and residual coding (stereoConfigIndex 2), whose Mps212Config is read, then a
    fill element: Fraunhofer's encoder writes MPEG Surround 2-1-2 at low rates, exhale never."""
    bits = Bits()
    bits.put(31, 5).put(42 - 32, 6).put(rate_index(44100), 4).put(2, 4)
    bits.put(rate_index(44100), 5).put(3, 3).put(2, 5)  # 2:1 SBR, stereo
    bits.put(2, 4)  # numElements - 1
    bits.put(3, 2).put(3, 4).put(0, 4).put(0, 1).put(0, 1)  # ID_USAC_EXT, ID_EXT_ELE_AUDIOPREROLL
    bits.put(1, 2).put(0, 1).put(1, 1)  # ID_USAC_CPE, tw_mdct, noiseFilling
    bits.put(0, 1).put(0, 1).put(0, 1).put(15, 4).put(9, 4).put(0, 1).put(0, 1)  # SbrConfig without header extras
    bits.put(2, 2)  # stereoConfigIndex 2: MPEG Surround 2-1-2 with residual coding
    # Mps212Config: bsFreqRes 2, bsFixedGainDMX 0, bsTempShapeConfig 2, bsDecorrConfig 0, bsHighRateMode 1, bsPhaseCoding 0,
    # bsOttBandsPhasePresent 1 with bsOttBandsPhase 7, bsResidualBands 5, bsPseudoLr 1, bsEnvQuantMode 0.
    bits.put(2, 3).put(0, 3).put(2, 2).put(0, 2).put(1, 1).put(0, 1).put(1, 1).put(7, 5).put(5, 5).put(1, 1).put(0, 1)
    bits.put(3, 2).put(0, 4).put(0, 4).put(0, 1).put(0, 1)  # ID_USAC_EXT, ID_EXT_ELE_FILL
    bits.put(0, 1)  # usacConfigExtensionPresent
    config = bits.align().bytes()
    samples = [bytes([0x80 if i % 10 == 0 else 0x00]) + bytes((i * 13) % 50 + 20) for i in range(30)]
    track = Track(1, samples, 44100, 2, aac_entry(config, 44100, 2), durations=[2048] * 30)
    data, records, offsets = movie([track], ftyp_box=ftyp(b"mp42", 0, (b"mp42", b"isom")))
    pair = ASC + "/UsacConfig/UsacDecoderConfig/Element[1]"
    mps = pair + "/Mps212Config"
    return "xhe-aac-mps212", data, {
        "description": "xHE-AAC with 2:1 SBR whose channel pair element uses MPEG Surround 2-1-2 with residual coding: its Mps212Config is read and the "
                       "fill element after it too. No encoder is named.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("xHE-AAC (USAC)", 44100, 2, "Stereo", 30 * 2048, vbr=True)],
        "units": unit_list(offsets[0], samples, 10),
        "fields": {ASC + "/UsacConfig/UsacDecoderConfig": "3 elements", pair + "/Stereo Config Index": "2",
                   mps + "/Frequency Resolution": "2 (20 parameter bands)", mps + "/Temporal Shaping Config": "2", mps + "/OTT Bands Phase": "7",
                   mps + "/Residual Bands": "5", mps + "/HasPseudoLr": "true", mps + "/Envelope Quantization Mode": "0",
                   ASC + "/UsacConfig/UsacDecoderConfig/Element[2]/Extension Element Type": "0 (fill)", ASC + "/UsacConfig/HasConfigExtension": "false"},
        "findings": [["aac.usac-frames-not-analyzed", "usacIndependencyFlag"]],
        "crosscheck": {"ffprobe": False, "mp4box": True, "mp4box_samples": True, "faad": None,
                       "skip": {"ffprobe": "ffmpeg 6.1 does not implement audio object type 42 and lists no stream; MP4Box lists the samples instead.",
                                "faad": "faad2 2.11 does not read USAC configurations (it fails to open the file)."}},
    }


def eld():
    """AAC ELD (object type 39) with ELDSpecificConfig: 480 samples, no low-delay SBR, no extensions, epConfig 0."""
    bits = Bits()
    bits.put(31, 5).put(39 - 32, 6).put(rate_index(48000), 4).put(1, 4)
    bits.put(1, 1).put(0, 1).put(0, 1).put(0, 1).put(0, 1)  # frameLengthFlag 480, resilience flags, ldSbrPresentFlag
    bits.put(0, 4)  # ELDEXT_TERM
    bits.put(0, 2)  # epConfig
    config = bits.align().bytes()
    samples = [bytes((i * 7) % 30 + 40) for i in range(20)]
    track = Track(1, samples, 48000, 1, aac_entry(config, 48000, 1), durations=[480] * 20)
    data, records, offsets = movie([track])
    return "eld", data, {
        "description": "AAC ELD, mono, 48000 Hz, 480 samples per frame: an error resilient object type, whose access units have no element IDs.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("ER AAC ELD", 48000, 1, "Mono", 20 * 480, vbr=True)],
        "units": unit_list(offsets[0], samples, 10),
        "fields": {ASC + "/Audio Object Type": "39 (ER AAC ELD)", ASC + "/Frame Length": "480 samples", ASC + "/HasLowDelaySbr": "false", ASC + "/Error Protection Config": "0"},
        "findings": [],
        "crosscheck": {"ffprobe": True, "mp4box": True, "faad": None,
                       "skip": {"faad": "faad2 decodes ER AAC LD but not ELD."}},
    }


def layout_variants():
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    out = []
    samples = units("stereo", 30)
    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2, average=128000, maximum=160000))
    data, records, offsets = movie([track], moov_first=False, wide=True, large_mdat=True)
    out.append(("co64-largesize", data, {
        "description": "64-bit chunk offsets (co64) and an mdat box with a 64-bit size (size 1 and largesize), before moov.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 30 * 1024)],
        "units": unit_list(offsets[0], samples, 10),
        "fields": {"MP4/mdat/Size": "1 (a 64-bit size follows)", "MP4/mdat/Large Size": f"{next(s for p, o, s in records if p == 'mdat')} bytes",
                   "MP4/moov/trak/mdia/minf/stbl/co64": "3 chunks", ESDS + "/Average Bitrate": "128000 bit/s"},
        "findings": [MOOV_AFTER], "crosscheck": {"ffprobe": True, "mp4box": True, "faad": None,
                                                 "skip": {"faad": "faad2's MP4 reader does not read 64-bit box sizes or chunk offsets.",
                                                          "faad_trace": "faad2's MP4 reader does not read 64-bit box sizes or chunk offsets."}},
    }))

    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))
    data, records, offsets = movie([track], moov_first=False)
    out.append(("moov-after-mdat", data, {
        "description": "moov after mdat: playable, but a player must reach the end of the file first.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 30 * 1024, vbr=True)],
        "units": unit_list(offsets[0], samples, 10), "fields": {},
        "findings": [MOOV_AFTER], "crosscheck": {"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100)},
    }))

    # Two tracks whose chunks alternate in mdat, so each track's samples are not contiguous.
    first = Track(1, units("stereo", 24), 44100, 2, aac_entry(lc, 44100, 2), chunk=6)
    second = Track(2, units("mono", 18, step=11), 22050, 1, aac_entry(asc(2, 22050, 1, sbr=False, signaling="backward"), 22050, 1), chunk=6)
    data, records, offsets = movie([first, second])
    out.append(("two-tracks", data, {
        "description": "Two audio tracks, stereo at 44100 Hz and mono at 22050 Hz, with their chunks interleaved in mdat.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 24 * 1024, vbr=True), stream("AAC LC", 22050, 1, "Mono", 18 * 1024, vbr=True)],
        "units": unit_list(offsets[0], first.samples, 6) + unit_list(offsets[1], second.samples, 6), "fields": {},
        "findings": [], "crosscheck": {"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100),
                                       "skip": {"faad_trace": "faad2 decodes the first track only."}},
    }))
    return out


def gapless():
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    out = []
    # 2112 samples of encoder delay, 44100 samples of audio (one second), the rest padding, as Apple's encoder counts them.
    samples = units("stereo", 46)
    total = 46 * 1024
    delay, length = 2112, 44100
    padding = total - delay - length
    smpb = f" 00000000 {delay:08X} {padding:08X} {length:016X} 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000".encode()
    for name, description, edits, value, finding in [
        ("edit-list-itunsmpb", "An edit list and iTunSMPB that agree: 2112 samples of encoder delay, one second of audio, the rest padding.",
         [(1000, delay)], smpb, None),
        ("gapless-disagree", "An edit list that skips 1024 samples while iTunSMPB gives 2112 samples of encoder delay.",
         [(1000, 1024)], smpb, ["mp4.gapless-disagree", "skips 1024 samples at the start, but iTunSMPB gives an encoder delay of 2112"]),
        ("itunsmpb-only", "iTunSMPB without an edit list.", None, smpb, None),
    ]:
        track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), edits=edits, extra=[sgpd_roll(-1), sbgp(len(samples))])
        meta = udta(ilst_meta(text_item(b"\xa9too", "hand-built"), freeform("iTunSMPB", value)))
        data, records, offsets = movie([track], moov_extra=[meta])
        fields = {"MP4/moov/trak/Gapless/Encoder Delay (iTunSMPB)": f"{delay} samples", "MP4/moov/trak/Gapless/Padding (iTunSMPB)": f"{padding} samples",
                  "MP4/moov/trak/Gapless/Samples (iTunSMPB)": f"{length} samples", "MP4/moov/trak/Gapless/Roll Distance": "-1 samples",
                  "MP4/moov/udta/meta/iTunes Metadata/iTunSMPB/data/Value/Encoder Delay": f"{delay} samples"}
        if edits:
            fields["MP4/moov/trak/Gapless/Encoder Delay (Edit List)"] = f"{edits[0][1]} samples"
            fields["MP4/moov/trak/Gapless/Samples (Edit List)"] = f"{length} samples"
        out.append((name, data, {
            "description": description, "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
            "streams": [stream("AAC LC", 44100, 2, "Stereo", total, vbr=True, writer=("hand-built", "Low"))],
            "units": unit_list(offsets[0], samples, 10), "fields": fields,
            "findings": [finding] if finding else [], "crosscheck": {"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100)},
        }))
    return out


def too_lavc():
    """ffmpeg's AAC encoder signs an access unit (every 256th, unless bit-exact) with its identification in a fill element before the channel
    pair element. The tool atom is set to an encoder ID and name to exercise the rule that such text names the encoder; ffmpeg's muxer writes
    its Lavf version there by default."""
    text = b"Lavc60.31.102"
    bits = Bits()
    # aacenc.c put_bitstream_info: the fill element's count (len + 2, with the escape count past 14), the extension type EXT_FILL, padding up to
    # a byte boundary (3 + 4 + 8 + 4 bits put, so 5), the name, and 12 minus the padding bits of zeros.
    bits.put(6, 3).put(15, 4).put(len(text) + 2 - 14, 8).put(0, 4).put(0, 5)
    for byte in text:
        bits.put(byte, 8)
    bits.put(0, 7)
    cpe(bits)
    bits.put(7, 3)
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    samples = [bits.align().bytes()] + units("stereo", 29)
    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))
    data, records, offsets = movie([track], moov_extra=[udta(ilst_meta(text_item(b"\xa9too", text.decode() + " aac")))])
    return [("aac-too-lavc", data, {
        "description": "AAC LC with ffmpeg's signature in a fill element of its first access unit, laid out as aacenc.c (n6.1.1, lines 787-802) writes it, and a tool atom set to "
                       "a libavcodec encoder ID: the atom adds to the encoder's evidence and names no writer.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 30 * 1024, vbr=True, encoder=("FFmpeg (Lavc60.31.102)", "High"))],
        "units": unit_list(offsets[0], samples, 10), "fields": {},
        "findings": [], "crosscheck": {"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100)},
    })]


def metadata():
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    samples = units("stereo", 20)
    jpeg = b"\xff\xd8\xff\xe0" + bytes(28) + b"\xff\xd9"
    png = b"\x89PNG\r\n\x1a\n" + bytes(24)
    encoding = b"vers" + u32(1) + b"acbf" + u32(2) + b"brat" + u32(256000) + b"cdcv" + u32(0x00010700)
    items = [
        text_item(b"\xa9nam", "Hand-built title"),
        item(b"\xa9ART", data_atom(2, "Artist in UTF-16".encode("utf-16-be"))),
        item(b"soar", data_atom(4, "Sort artist".encode())),
        item(b"sonm", data_atom(5, "Sort title".encode("utf-16-be"))),
        item(b"trkn", data_atom(0, u16(0) + u16(3) + u16(12) + u16(0))),
        item(b"disk", data_atom(0, u16(0) + u16(1) + u16(2))),
        item(b"gnre", data_atom(0, u16(18))),
        item(b"tmpo", data_atom(21, u16(120))),
        item(b"cpil", data_atom(21, u8(1))),
        item(b"rtng", data_atom(21, u8(0xFF))),
        item(b"plID", data_atom(21, struct.pack(">q", -2))),
        item(b"cnID", data_atom(22, u32(4000000000))),
        item(b"geID", data_atom(22, u8(7))),
        item(b"covr", data_atom(13, jpeg), data_atom(14, png), data_atom(27, b"BM" + bytes(14))),
        item(b"xi08", data_atom(65, struct.pack(">b", -5))),
        item(b"xi16", data_atom(66, struct.pack(">h", -300))),
        item(b"xi32", data_atom(67, struct.pack(">i", -70000))),
        item(b"xi64", data_atom(74, struct.pack(">q", -5000000000))),
        item(b"xu08", data_atom(75, u8(200))),
        item(b"xu16", data_atom(76, u16(60000))),
        item(b"xu32", data_atom(77, u32(4000000000))),
        item(b"xu64", data_atom(78, u64(9000000000))),
        item(b"xf32", data_atom(23, struct.pack(">f", 1.5))),
        item(b"xf64", data_atom(24, struct.pack(">d", -2.25))),
        item(b"xpnt", data_atom(70, struct.pack(">ff", 1.0, 2.0))),
        item(b"xrct", data_atom(72, struct.pack(">ffff", 0, 0, 640, 480))),
        item(b"xsjs", data_atom(3, b"S-JIS text")),
        item(b"xbin", data_atom(0, bytes(range(8)))),
        freeform("iTunNORM", b" 00000150 00000160 00001000 00001100 00007D00 00007D00 00007FFF 00007FFF 00007D00 00007D00"),
        freeform("Encoding Params", encoding, kind=0),
        freeform("Custom", b"A freeform text", mean=b"org.example"),
    ]
    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))
    data, records, offsets = movie([track], moov_extra=[udta(ilst_meta(*items))])
    tag = "MP4/moov/udta/meta/iTunes Metadata"
    fields = {
        tag: "31 items",
        tag + "/Title": "Hand-built title", tag + "/Artist": "Artist in UTF-16", tag + "/Sort Artist": "Sort artist", tag + "/Sort Title": "Sort title",
        tag + "/Track Number": "3 of 12", tag + "/Disc Number": "1 of 2", tag + "/Genre (ID3v1)": "18 (ID3v1 genre 17)", tag + "/Tempo": "120",
        tag + "/Compilation": "1", tag + "/Content Rating": "-1", tag + "/Playlist ID": "-2", tag + "/Catalog ID": "4000000000", tag + "/Genre ID": "7",
        tag + "/Cover Art/data[0]": "JPEG image, 34 bytes", tag + "/Cover Art/data[1]": "PNG image, 32 bytes", tag + "/Cover Art/data[2]": "BMP image, 16 bytes",
        tag + "/xi08": "-5", tag + "/xi16": "-300", tag + "/xi32": "-70000", tag + "/xi64": "-5000000000", tag + "/xu08": "200", tag + "/xu16": "60000",
        tag + "/xu32": "4000000000", tag + "/xu64": "9000000000", tag + "/xf32": "1.500", tag + "/xf64": "-2.250", tag + "/xpnt": "1, 2",
        tag + "/xrct": "0, 0, 640, 480", tag + "/xsjs": "S-JIS text", tag + "/xbin": "0x0001020304050607",
        tag + "/iTunNORM/data/Data Type": "1 (UTF-8)",
        tag + "/Encoding Params/data/Value/Bitrate Mode": "2 (constrained VBR)", tag + "/Encoding Params/data/Value/Bitrate": "256000 bit/s",
        tag + "/Encoding Params/data/Value/Version": "1", tag + "/Encoding Params/data/Value/Codec Version": "0x00010700",
        tag + "/Custom": "A freeform text", tag + "/Custom/mean": "org.example",
    }
    return [("ilst-all-types", data, {
        "description": "iTunes metadata with every data type: UTF-8, UTF-16, their sort variants, S/JIS, implicit (track and disc number, ID3v1 genre, binary), "
                       "signed and unsigned integers of every width, floats, point, rectangle, JPEG, PNG and BMP images, and freeform atoms: iTunNORM, "
                       "Encoding Params and one of another mean. Encoding Params says constrained VBR, so the stream's bitrate is variable.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)],
        "units": unit_list(offsets[0], samples, 10), "fields": fields, "findings": [],
        "crosscheck": {"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100), "taglib": True},
    })]


def id3v24(frames):
    body = b""
    for frame_id, text in frames:
        payload = b"\x03" + text.encode("utf-8")
        size = len(payload)
        syncsafe = bytes([(size >> 21) & 0x7F, (size >> 14) & 0x7F, (size >> 7) & 0x7F, size & 0x7F])
        body += frame_id.encode() + syncsafe + b"\0\0" + payload
    size = len(body)
    return b"ID3\x04\x00\x00" + bytes([(size >> 21) & 0x7F, (size >> 14) & 0x7F, (size >> 7) & 0x7F, size & 0x7F]) + body


def id32():
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    samples = units("stereo", 20)
    tag = id3v24([("TIT2", "Title in ID3"), ("TSSE", "LAME 3.100")])
    meta = fullbox("meta", 0, 0, b"", meta_hdlr(b"ID32"), fullbox("ID32", 0, 0, u16(((ord("e") - 0x60) << 10) | ((ord("n") - 0x60) << 5) | (ord("g") - 0x60)) + tag))
    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))
    data, records, offsets = movie([track], moov_extra=[meta])
    return [("id32", data, {
        "description": "An ID32 box (ID3 in MP4) in a meta box with the ID32 handler, holding an ID3v2.4 tag with a title and a TSSE frame. The TSSE "
                       "frame names LAME, which names the AAC stream's encoder with low confidence, as a claim alone does.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True, encoder=("LAME 3.100", "Low"))],
        "units": unit_list(offsets[0], samples, 10),
        "fields": {"MP4/moov/meta/ID32/Language": "eng", "MP4/moov/meta/ID32/ID3v2/TIT2": "Title in ID3"},
        "findings": [], "crosscheck": {"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100)},
    })]


def mpeg_frames(count):
    """MPEG-1 Layer III frames at 128 kbps, 44100 Hz, stereo, without padding: 417 bytes each, silent (zero side information)."""
    frame = bytes([0xFF, 0xFB, 0x90, 0x00]) + bytes(413)
    return [frame] * count


def mp3_in_mp4():
    frames = mpeg_frames(12)
    entry = mp4a(2, 44100, esds(0x6B, None, 128000, 128000))
    track = Track(1, frames, 44100, 2, entry, durations=[1152] * 12, chunk=12)
    data, records, offsets = movie([track])
    return [("mp3-in-mp4", data, {
        "description": "MPEG-1 Layer III in MP4 (object type 0x6B), one chunk of 12 frames, which the MPEG audio parser reads as the same bytes in a file.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [{"codec": "MPEG-1 Layer III", "sample_rate": 44100, "channels": 2, "channel_mode": "Stereo", "channel_layout": SPEAKERS[2],
                     "samples": 12 * 1152, "is_vbr": False}],
        "units": unit_list(offsets[0], frames, 12),
        "fields": {ESDS: "0x6B (MPEG-1 audio)"},
        "findings": [], "crosscheck": {"ffprobe": True, "mp4box": True, "faad": None, "skip": {"faad": "faad2 reads AAC only."}},
    })]


def mp3_gap():
    """The frames of mp3-in-mp4 in two chunks with 16 bytes between them, which the MPEG audio parser does not read as one stretch."""
    frames = mpeg_frames(12)
    entry = mp4a(2, 44100, esds(0x6B, None, 128000, 128000))
    track = Track(1, frames, 44100, 2, entry, durations=[1152] * 12, chunk=6)
    data, records, offsets = movie([track])
    second = offsets[0][1]
    mdat = next(o for p, o, s in records if p == "mdat")
    stco_at = next(o for p, o, s in records if p.endswith("stco"))
    data = data[:second] + bytes(16) + data[second:]
    data = data[:mdat] + u32(struct.unpack(">I", data[mdat:mdat + 4])[0] + 16) + data[mdat + 4:]
    data = data[:stco_at + 20] + u32(second + 16) + data[stco_at + 24:]
    records = [(p, o, s + 16 if p == "mdat" else s) for p, o, s in records]
    result = hand_built(
        "mp3-two-chunks-gap", "MPEG-1 Layer III in MP4 (object type 0x6B), 12 frames in two chunks of 6 with 16 bytes between them: the MPEG audio parser reads samples only where they lie in one stretch, "
        "so the track is a stream described by its sample entry and its frames are not read.",
        data, records,
        [{"codec": "MPEG-1 audio", "sample_rate": 44100, "channels": 2, "channel_mode": None, "channel_layout": [], "samples": 12 * 1152, "is_vbr": False,
                     # The stream is described by its sample entry, which states no layout; ffprobe reads the frames and gives stereo.
                     "ffprobe_channels": 2}], [],
        [["mpeg.packets-not-analyzed", "packets"]], fields={ESDS: "0x6B (MPEG-1 audio)"},
        crosscheck={"ffprobe": True, "mp4box": True, "faad": None, "skip": {"faad": "faad2 reads AAC only."}})
    # The frames are not read, so ffprobe's packets are compared with the samples.
    result[2]["samples"] = unit_list([offsets[0][0], second + 16], frames, 6)
    return [result]


def quicktime():
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    out = []
    samples = units("stereo", 20)
    terminator = Box(b"\0\0\0\0")
    # QuickTime sound sample description version 1: 16 more bytes, then a wave box with frma, mp4a, esds and a terminator.
    v1 = mp4a(2, 44100, box("wave", b"", box("frma", b"mp4a"), box("mp4a", u32(0)), esds(0x40, lc), terminator), version=1, extra=u32(1024) + u32(0) + u32(0) + u32(2))
    # Version 2: the fields of version 0 hold fixed values, the rate is a 64-bit float, the channels a 32-bit count.
    v2_extra = u32(72) + struct.pack(">d", 44100.0) + u32(2) + u32(0x7F000000) + u32(0) + u32(0) + u32(0) + u32(1024)
    v2 = Box(b"mp4a", bytes(6) + u16(1) + u16(2) + u16(0) + u32(0) + u16(3) + u16(16) + struct.pack(">h", -2) + u16(0) + u32(0x10000) + v2_extra,
             [box("wave", b"", box("frma", b"mp4a"), esds(0x40, lc), terminator)])
    for name, entry, version, fields in [
        ("quicktime-sound-v1", v1, 1, {STSD + "/Samples Per Packet": "1024", STSD + "/Version": "1"}),
        ("quicktime-sound-v2", v2, 2, {STSD + "/Audio Sample Rate": "44100.000 Hz", STSD + "/Audio Channels": "2", STSD + "/Version": "2"}),
    ]:
        track = Track(1, samples, 44100, 2, entry)
        data, records, offsets = movie([track], ftyp_box=ftyp(b"qt  ", 0x200, (b"qt  ",)))
        asc_path = STSD + "/wave/esds/ES_Descriptor/DecoderConfigDescriptor/DecoderSpecificInfo/AudioSpecificConfig"
        fields = dict(fields)
        fields[asc_path] = "AAC LC"
        out.append((name, data, {
            "description": f"A QuickTime movie (brand qt) with a sound sample description of version {version}, whose esds box sits in a wave box.",
            "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
            "streams": [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)],
            "units": unit_list(offsets[0], samples, 10), "fields": fields, "findings": [],
            "crosscheck": {"ffprobe": True, "mp4box": True, "faad": None, "skip": {"faad": "faad2's MP4 reader does not look for esds inside a wave box.",
                                                                                 "faad_trace": "faad2's MP4 reader does not look for esds inside a wave box."}},
        }))
    return out + quicktime_text()


def text_atom(kind, *items, tail=b""):
    """A user data text atom (QuickTime File Format, "User data atoms"): items of a 16-bit size, a 16-bit language code and the text."""
    return Box(kind, b"".join(u16(len(text)) + u16(language) + text for language, text in items) + tail)


ENG, DEU = 0x15C7, 0x10B5  # packed ISO 639-2/T "eng" and "deu"


def quicktime_text():
    """The text atoms of moov/udta and trak/udta shown as tags: several languages and encodings in one atom, and the damage of a size and of a tail."""
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    samples = units("stereo", 20)
    # 0x8E is an e with an acute accent in Macintosh Roman.
    atoms = [
        text_atom(b"\xa9nam", (0, b"Caf\x8e title"), (DEU, "Handgefertigter Titel \u00fcber Caf\u00e9".encode("utf-8"))),
        text_atom(b"\xa9ART", (ENG, b"Hand-built artist")),
        text_atom(b"\xa9alb", (0x7FFF, b"Unspecified album")),
        text_atom(b"\xa9day", (0, b"2026")),
        text_atom(b"\xa9swr", (0, b"hand-built 1.0")),
        text_atom(b"\xa9des", (ENG, b"\xfe\xff" + "Utf sixteen".encode("utf-16-be"))),
        text_atom(b"\xa9xyz", (ENG, b"Unlisted text atom")),
        Box(b"\xa9qqq", b"not text"),
        text_atom(b"\xa9cmt", (ENG, b"cut"), tail=u16(40) + u16(ENG) + b"short"),
        text_atom(b"\xa9cpy", (ENG, b"Hand-built copyright"), tail=b"\0\0"),
        ilst_meta(text_item(b"\xa9gen", "Hand-built genre")),
        box("WLOC", u16(10) + u16(20)),
    ]
    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), after_mdia=[udta(text_atom(b"\xa9nam", (ENG, b"Track title")))])
    data, records, offsets = movie([track], ftyp_box=ftyp(b"qt  ", 0x200, (b"qt  ",)), moov_extra=[udta(*atoms)])
    tag = "MP4/moov/QuickTime User Data/Title"
    return [("quicktime-udta-text", data, {
        "description": "A QuickTime movie whose user data holds text atoms: a title in two languages and encodings (Macintosh Roman, UTF-8), an artist, an album of the unspecified "
                       "Macintosh language, a UTF-16 description, a software atom (the writer), an item list (genre), an unlisted atom that follows the text layout and one that does not, a comment whose second "
                       "item states more bytes than the atom holds, a copyright with two bytes left over, and a window location. The track's user data holds a title.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True, writer=("hand-built 1.0", "Low"))],
        "units": unit_list(offsets[0], samples, 10),
        "fields": {tag: "Caf\u00e9 title", tag + "/Text Item[0]/Language": "0 (English, Macintosh language code)", tag + "/Text Item[1]/Language": "deu",
                   tag + "/Text Item[1]/Text": "Handgefertigter Titel \u00fcber Caf\u00e9", "MP4/moov/QuickTime User Data/Artist": "Hand-built artist",
                   "MP4/moov/QuickTime User Data/Album/Text Item/Language": "32767 (unspecified Macintosh language)",
                   "MP4/moov/QuickTime User Data/Description": "Utf sixteen", "MP4/moov/QuickTime User Data/Software": "hand-built 1.0",
                   "MP4/moov/QuickTime User Data/\u00a9xyz": "Unlisted text atom", "MP4/moov/QuickTime User Data/meta/iTunes Metadata/Genre": "Hand-built genre", "MP4/moov/trak/QuickTime User Data/Title": "Track title"},
        "findings": [["mp4.text-size-invalid", "states 40 bytes, but 5 follow"], ["mp4.text-item-truncated", "fewer than the 4 bytes"]],
        "crosscheck": {"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100)},
    })]


def chapters():
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    samples = units("stereo", 20)
    titles = [(0, "Intro"), (2500000, "Second chapter")]
    chpl = fullbox("chpl", 1, 0, u32(0) + u8(len(titles)) + b"".join(u64(t) + u8(len(s.encode())) + s.encode() for t, s in titles))
    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))
    data, records, offsets = movie([track], moov_extra=[udta(chpl)])
    return [("nero-chapters", data, {
        "description": "Nero chapters (chpl, version 1) in udta.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)],
        "units": unit_list(offsets[0], samples, 10),
        "fields": {"MP4/moov/udta/chpl/Chapters": "2", "MP4/moov/udta/chpl/Chapter[1]": "00:00:00.250 Second chapter"},
        "findings": [], "crosscheck": {"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100)},
    })]


def fragmented():
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    samples = units("stereo", 30)
    entry = aac_entry(lc, 44100, 2)
    stbl = box("stbl", b"", stsd(entry), stts([]), stsc([]), stsz([]), stco([]))
    trak = box("trak", b"", tkhd(1, 0), box("mdia", b"", mdhd(44100, 0), hdlr(b"soun"), box("minf", b"", fullbox("smhd", 0, 0, bytes(4)), dinf(), stbl)))
    mvex = box("mvex", b"", fullbox("mehd", 0, 0, u32(30 * 1024 * 1000 // 44100)), fullbox("trex", 0, 0, u32(1) + u32(1) + u32(1024) + u32(0) + u32(0)))
    moov = box("moov", b"", mvhd(1000, 0), trak, mvex)
    head = [ftyp(b"iso6", 0, (b"iso6", b"dash", b"mp41")), moov]
    data, records = write(head)

    def fragment(sequence, chunk, decode_time, offset):
        def build(data_offset):
            sizes = b"".join(u32(len(s)) for s in chunk)
            trun = fullbox("trun", 0, 0x201, u32(len(chunk)) + struct.pack(">i", data_offset) + sizes)
            traf = box("traf", b"", fullbox("tfhd", 0, 0x20000, u32(1)), fullbox("tfdt", 1, 0, u64(decode_time)), trun)
            return box("moof", b"", fullbox("mfhd", 0, 0, u32(sequence)), traf)

        moof_bytes, _ = write([build(0)], offset)
        moof = build(len(moof_bytes) + 8)
        return [moof, box("mdat", b"".join(chunk))]

    boxes = list(head)
    unit_rows = []
    decode = 0
    for sequence, start in enumerate(range(0, 30, 10)):
        offset = len(write(boxes)[0])
        chunk = samples[start:start + 10]
        moof, mdat = fragment(sequence + 1, chunk, decode, offset)
        boxes += [moof, mdat]
        moof_length = len(write([moof], offset)[0])
        position = offset + moof_length + 8
        for sample in chunk:
            unit_rows.append([position, len(sample)])
            position += len(sample)
        decode += len(chunk) * 1024
    mfra_body = fullbox("tfra", 1, 0, u32(1) + u32(0) + u32(1) + u64(0) + u64(len(write(head)[0])) + u8(1) + u8(1) + u8(1))
    tail = box("mfra", b"", mfra_body, fullbox("mfro", 0, 0, u32(0)))
    data, records = write(boxes + [tail])
    # mfro states the size of mfra.
    mfra_size = len(write([tail])[0])
    data = data[:-4] + u32(mfra_size)
    return [("fragmented", data, {
        "description": "A fragmented MP4: moov with empty sample tables and mvex, then three moof and mdat pairs whose track runs give each sample's size "
                       "and the data offset from the moof (default-base-is-moof), and an mfra with a tfra and mfro.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 30 * 1024, vbr=True)],
        "units": unit_rows,
        "fields": {"MP4/moof[1]/mfhd": "Movie Fragment Header, sequence 2", "MP4/moof/traf/trun": "10 samples", "MP4/moov/mvex/trex": "Track Extends, track 1"},
        "findings": [], "crosscheck": {"ffprobe": True, "mp4box": True, "faad": None,
                                       "skip": {"faad": "faad2's MP4 reader reads samples from the sample tables only, which a fragmented file leaves empty.",
                                                "faad_trace": "faad2's MP4 reader reads samples from the sample tables only, which a fragmented file leaves empty."}},
    })]


SKIP_ALL = {"ffprobe": False, "mp4box": False, "faad": None, "skip": {"all": "a damaged or oddly built file, which tools read in their own ways"}}


def hand_built(name, description, data, records, streams, units_rows, findings, fields=None, crosscheck=None):
    return (name, data, {
        "description": description, "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)), "streams": streams,
        "units": units_rows, "fields": fields or {}, "findings": [list(f) for f in findings], "crosscheck": crosscheck or SKIP_ALL,
    })


def structure():
    """Boxes where ISO/IEC 14496-12 does not place them, tables that overlap, and the long lists whose entries are not all shown."""
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    samples = units("stereo", 20)
    out = []

    data, records = write([ftyp(), box("mdat", bytes(64)), box("mfra", b"", box("moov", b"", mvhd(1000, 0)))])
    out.append(hand_built(
        "moov-in-mfra", "ftyp, mdat and an mfra box that holds a moov box: moov belongs at the top level (8.2.1), so it is shown and reported but is not the movie.",
        data, records, [], [], [["mp4.box-misplaced", "lies in a mfra box"], ["mp4.moov-missing", "no moov box"]]))

    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))
    offsets = movie([track])[2]
    second = box("moov", b"", mvhd(1000, 20 * 1024 * 1000 // 44100), track.trak(offsets[0], 1000))
    data, records, offsets = movie([track], after=[second])
    out.append(hand_built(
        "second-moov-with-track", "A second moov box after mdat, with a track over the same samples: players read the first moov box, so the second one's track is shown and not analyzed.",
        data, records, [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)], unit_list(offsets[0], samples, 10), [["mp4.duplicate-box", "second moov box"]]))

    # A moov box that is not the movie, with another timescale, before the real one: it sets none of the movie's figures.
    data, records, offsets = movie([track], before=[box("mfra", b"", box("moov", b"", mvhd(1, 0)))])
    out.append(hand_built(
        "moov-in-mfra-before-moov", "An mfra box before the movie, holding a moov box whose mvhd has a timescale of 1: it is shown and reported, and the movie is read in the real moov box's timescale.",
        data, records, [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)], unit_list(offsets[0], samples, 10), [["mp4.box-misplaced", "lies in a mfra box"]]))

    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), before_mdia=[udta(ilst_meta(text_item(b"\xa9nam", "Metadata before mdia")))])
    data, records, offsets = movie([track])
    out.append(hand_built(
        "track-meta-before-mdia", "A trak box whose udta box holds a meta box with the metadata handler mdir before mdia: the track's handler is the one in mdia (8.4.3), so the track is read as sound.",
        data, records, [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)], unit_list(offsets[0], samples, 10), [],
        crosscheck={"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100)}))

    # 1,000 empty edits and one that plays media past the end of the track's media: the edits past the 1,000 shown have no node.
    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), edits=[(1, -1)] * 1000 + [(1000, 0)])
    data, records, offsets = movie([track])
    out.append(hand_built(
        "edit-list-beyond-unlisted", "An edit list of 1,001 entries, the first 1,000 empty edits and the last one playing a second of a media that lasts 0.46 s: the last edit is not listed, so the finding points to the box that counts the entries past 1,000.",
        data, records, [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)], unit_list(offsets[0], samples, 10),
        [["mp4.edit-list-empty-edit", "empty edit"], ["mp4.edit-beyond-media", "past the media's 20480"]],
        fields={"MP4/moov/trak/edts/elst/More Entries": "1 entry"}))

    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), extra=[])
    data, records, offsets = movie([track], moov_extra=[udta(ilst_meta(item(b"\xa9too", data_atom(0, b"fdkaac 1.0.0"))))])
    out.append(hand_built(
        "too-data-type-0", "A \u00a9too item whose data atom has the implicit type 0 and holds bytes that read as text: only a text data type names software, so no writer is claimed.",
        data, records, [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)], unit_list(offsets[0], samples, 10), [],
        fields={"MP4/moov/udta/meta/iTunes Metadata/Encoding Tool/data/Data Type": "0 (implicit)"},
        crosscheck={"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100)}))

    text = b"hand-built"
    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))
    data, records, offsets = movie([track], ftyp_box=ftyp(b"qt  ", 0x200, (b"qt  ",)), moov_extra=[box("udta", b"", Box(b"\xa9nam", u16(len(text)) + u16(0) + text), after=u32(0))])
    out.append(hand_built(
        "quicktime-udta-terminator", "A QuickTime movie whose user data list ends in the optional 32-bit zero (QuickTime File Format, \"User data list\"): it is shown as a terminator, with no warning.",
        data, records, [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)], unit_list(offsets[0], samples, 10), [],
        fields={"MP4/moov/QuickTime User Data/Terminator": "0", "MP4/moov/QuickTime User Data/Title": "hand-built"},
        crosscheck={"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100)}))

    # Samples of 6 bytes in 50 chunks that all start at the same 600 bytes: 5,000 samples from a few hundred bytes of mdat.
    entry = mp4a(2, 44100, format=b"alac")
    track = Track(1, [bytes(6)] * 100, 44100, 2, entry, chunk=100, stsz_box=fullbox("stsz", 0, 0, u32(6) + u32(5000)), stsc_box=stsc([(1, 100, 1)]),
                  stts_entries=[(5000, 1024)], mdhd_duration=5000 * 1024)
    start = movie([track], stco_boxes=[stco([0] * 50)])[2][0][0]
    data, records, offsets = movie([track], stco_boxes=[stco([start] * 50)])
    out.append(hand_built(
        "samples-overlap-chunks", "An alac track of 5,000 samples of 6 bytes in 50 chunks that all start at the same offset: valid samples do not overlap, so the walk over the chunks stops where the samples add up to more bytes than the file holds.",
        data, records, [], [], [["mp4.samples-overlap", "add up to more bytes than the file has left"], ["mp4.codec-not-analyzed", "Apple Lossless"]]))

    # 30 runs of 20 samples of 30 bytes, each placed at the start of the same 600 bytes of mdat.
    stbl = box("stbl", b"", stsd(entry), stts([]), stsc([]), stsz([]), stco([]))
    trak = box("trak", b"", tkhd(1, 0), box("mdia", b"", mdhd(44100, 0), hdlr(b"soun"), box("minf", b"", fullbox("smhd", 0, 0, bytes(4)), dinf(), stbl)))
    moov = box("moov", b"", mvhd(1000, 0), trak, box("mvex", b"", fullbox("trex", 0, 0, u32(1) + u32(1) + u32(1024) + u32(0) + u32(0))))
    mdat = box("mdat", bytes(600))
    runs = [fullbox("trun", 0, 0x201, u32(20) + struct.pack(">i", 0) + b"".join(u32(30) for _ in range(20))) for _ in range(30)]

    def build(base):
        return [ftyp(b"iso6", 0, (b"iso6", b"dash", b"mp41")), moov, box("moof", b"", fullbox("mfhd", 0, 0, u32(1)), box("traf", b"", fullbox("tfhd", 0, 1, u32(1) + u64(base)), *runs)), mdat]
    data, records = write(build(0))
    data, records = write(build(next(o for p, o, s in records if p == "mdat") + 8))
    out.append(hand_built(
        "samples-overlap-fragments", "An alac track whose track fragment holds 30 runs of 20 samples of 30 bytes, every run placed at the same 600 bytes of mdat: the samples of the fragments are cut where they add up to more bytes than the file holds.",
        data, records, [], [], [["mp4.samples-overlap", "track fragments add up to more bytes than the file has left"], ["mp4.codec-not-analyzed", "Apple Lossless"]]))
    return out


def corrupt():
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    out = []
    samples = units("stereo", 20)

    def file(name, description, findings, tracks=None, units_rows=None, streams=None, fields=None, crosscheck=None, mutate=None, keep=None, **options):
        tracks = tracks if tracks is not None else [Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))]
        options.pop("hide", None)
        data, records, offsets = movie(tracks, **options)
        if keep:
            records = [r for r in records if keep(r)]
        if mutate:
            data = mutate(data, records)
        rows = units_rows if units_rows is not None else (unit_list(offsets[0], tracks[0].samples, tracks[0].chunk) if tracks else [])
        out.append((name, data, {
            "description": description, "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
            "streams": streams if streams is not None else [stream("AAC LC", 44100, 2, "Stereo", len(rows) * 1024, vbr=True)],
            "units": rows, "fields": fields or {}, "findings": [list(f) for f in findings],
            "crosscheck": crosscheck or {"ffprobe": False, "mp4box": False, "faad": None, "skip": {"all": "a damaged file, which tools read in their own ways"}},
        }))

    file("box-overruns-parent", "A free box in udta that declares 1000 bytes, more than udta holds; it is read up to udta's end.",
         [["mp4.box-overruns-parent", "declares 1000 bytes"]], moov_extra=[udta(Box(b"free", bytes(12), size=1000))])

    def cut(data, records):
        return data[:len(data) - 300]
    full = movie([Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))], moov_first=True)
    cut_length = len(full[0]) - 300
    rows = [r for r in unit_list(full[2][0], samples, 10) if r[0] + r[1] <= cut_length]
    file("truncated", "A file cut 300 bytes before the end of mdat: the mdat box declares more than the file holds, and the last samples lie outside it.",
         [["mp4.truncated", "but the file ends after"], ["mp4.sample-outside-file", "lie outside the file"]], mutate=cut, units_rows=rows)

    big = [Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), stsz_box=None)]

    def outside(data, records):
        # The last chunk offset points beyond the end of the file.
        stco_offset = next(o for p, o, s in records if p.endswith("stco"))
        return data[:stco_offset + 16 + 4] + u32(len(data) + 1000) + data[stco_offset + 16 + 8:]
    file("chunk-outside-file", "A chunk offset beyond the end of the file: its 10 samples are reported and left out.",
         [["mp4.sample-outside-file", "10 samples of track 1 lie outside the file"]], tracks=big, mutate=outside,
         units_rows=unit_list(movie(big)[2][0], samples, 10)[:10])

    def into_moov(data, records):
        stco_offset = next(o for p, o, s in records if p.endswith("stco"))
        moov_offset = next(o for p, o, s in records if p == "moov")
        return data[:stco_offset + 16] + u32(moov_offset + 8) + data[stco_offset + 20:]
    plain_offsets = movie(big)[2][0]
    file("sample-outside-mdat", "The first chunk offset points into moov: its samples lie outside every mdat box and are still read.",
         # The samples read from moov are no raw data blocks, which their findings show.
         [["mp4.sample-outside-mdat", "lie outside every mdat box"], ["aac.prediction-not-allowed", "6 raw data blocks are not read to their end"],
          ["aac.max-sfb-too-large", "gives max_sfb 61"], ["aac.element-past-end", "ends before its END element"], ["aac.section-overrun", "past max_sfb 1"]],
         tracks=big, mutate=into_moov,
         units_rows=unit_list([next(o for p, o, s in movie(big)[1] if p == "moov") + 8] + plain_offsets[1:], samples, 10))

    file("box-size-zero-and-small", "A udta box whose last box has size 0, which only the last box of the file may have, so it is read to the end of udta; "
         "and a second udta box holding a box whose size of 4 is smaller than its header, which ends the reading of that udta.",
         [["mp4.box-to-end", "has size 0"], ["mp4.box-size-invalid", "states a size smaller than its header"]],
         moov_extra=[udta(Box(b"free", bytes(4)), Box(b"skip", bytes(8), size=0)), udta(Box(b"bad!", bytes(8), size=4))],
         keep=lambda r: not r[0].endswith("bad!"))

    nested = Box(b"free", bytes(4))
    for _ in range(60):
        nested = box("udta", b"", nested)
    file("deep-nesting", "udta boxes nested 60 deep inside moov: boxes deeper than 40 levels are not read.",
         [["mp4.nesting-too-deep", "nest more than 40 levels"]], moov_extra=[nested], keep=lambda r: r[0].count("/") <= 40)

    huge = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), stsz_box=fullbox("stsz", 0, 0, u32(0) + u32(0xFFFFFFF0) + b"".join(u32(len(s)) for s in samples)),
                 stts_entries=[(0xFFFFFFF0, 1024)])
    file("huge-sample-count", "stsz declares 4294967280 samples but holds 20 sizes, and stts gives 4294967280 samples a duration: nothing is allocated for them.",
         [["mp4.entries-exceed-box", "declares 4294967280 sample sizes, but holds room for 20"], ["mp4.sample-count-mismatch", "gives durations to 4294967280 samples"],
          ["mp4.duration-mismatch", "mdhd box of track 1"]], tracks=[huge])

    dup = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), extra=[stsd(aac_entry(lc, 44100, 2))])
    file("duplicate-boxes", "A second mvhd in moov and a second stsd in stbl.", [["mp4.duplicate-box", "second mvhd box"], ["mp4.duplicate-box", "second stsd box"]],
         tracks=[dup], moov_extra=[mvhd(1000, 0)])

    data, records = write([ftyp(), box("mdat", bytes(64))])
    out.append(("moov-missing", data, {
        "description": "ftyp and mdat without moov.", "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [], "units": [], "fields": {}, "findings": [["mp4.moov-missing", "no moov box"]],
        "crosscheck": {"ffprobe": False, "mp4box": True, "faad": None, "skip": {"ffprobe": "ffmpeg refuses a file without moov.", "faad": "faad2 finds no track."}},
    }))

    mismatch = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), stts_entries=[(21, 1024)])
    file("sample-count-mismatch", "stts gives 21 samples a duration, stsz sizes 20.",
         [["mp4.sample-count-mismatch", "gives durations to 21 samples, its stsz box sizes 20"], ["mp4.duration-mismatch", "mdhd box of track 1"]], tracks=[mismatch])

    longer = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), mdhd_duration=20 * 1024 + 4410)
    file("duration-mismatch", "mdhd gives a duration 0.1 s longer than the samples' durations in stts.",
         [["mp4.duration-mismatch", "but its samples' durations in stts add up to 20480"]], tracks=[longer])

    fewer = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), stsc_box=stsc([(1, 5, 1)]))
    # Chunk 1 holds samples 1 to 5 and chunk 2 samples 6 to 10, at the offsets of the chunks the file was laid out with, which held 10 each.
    chunk_offsets = movie([fewer])[2][0]
    placed = unit_list([chunk_offsets[0]], samples[:5], 5) + unit_list([chunk_offsets[1]], samples[5:10], 5)
    file("chunk-sample-mismatch", "stsc puts 5 samples into each of the 2 chunks, while stsz sizes 20: the last 10 samples have no place.",
         # From the sixth sample on, the sizes stsz gives do not fit the bytes at the second chunk, so what is read there is no raw data block.
         [["mp4.chunk-sample-mismatch", "hold 10 samples, but stsz sizes 20"], ["aac.unit-trailing-bytes", "holds 16 bytes after the END element"],
          ["aac.tns-order-too-high", "higher order than the 12"], ["aac.element-past-end", "The PCE at"], ["aac.ics-reserved-bit", "3 raw data blocks"],
          ["aac.prediction-not-allowed", "3 raw data blocks are not read to their end"]], tracks=[fewer], units_rows=placed)

    sizes_bad = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2), edits=[(500, -5)])
    file("edit-list-invalid", "An edit list whose media time is -5, which ISO/IEC 14496-12 does not define.",
         [["mp4.edit-list-invalid-time", "media time below -1"]], tracks=[sizes_bad])
    return out


def trailing_tags():
    lc = asc(2, 44100, 2, sbr=False, signaling="backward")
    samples = units("stereo", 20)
    out = []
    id3v1 = b"TAG" + b"Appended title".ljust(30, b"\0") + bytes(30) + bytes(30) + b"2026" + bytes(30) + b"\xff"
    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))
    data, records, offsets = movie([track], trailer=id3v1)
    out.append(("trailing-id3v1", data, {
        "description": "An ID3v1 tag appended after the last box: it lies after the end the boxes declare, so it is the file's tag.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)], "units": unit_list(offsets[0], samples, 10),
        "fields": {"ID3v1/Title": "Appended title"}, "findings": [],
        "crosscheck": {"ffprobe": True, "mp4box": False, "faad": faad_line("LC AAC", 2, 44100), "skip": {"mp4box": "MP4Box reports the 128 bytes after the last box as a box of its own."}},
    }))

    # mdat last, with "TAG" exactly 128 bytes before the end of the file: inside the box, so no ID3v1 tag.
    filler = b"TAG" + bytes(125)
    track = Track(1, samples, 44100, 2, aac_entry(lc, 44100, 2))
    data, records, offsets = movie([track], moov_first=True, mdat_extra=filler)
    out.append(("tag-inside-mdat", data, {
        "description": "\"TAG\" 128 bytes before the end of the file, inside the last box (mdat): the box owns those bytes, so it is no ID3v1 tag.",
        "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)),
        "streams": [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)], "units": unit_list(offsets[0], samples, 10),
        "fields": {}, "findings": [["tag.near-miss", "part of the MP4 data"]],
        "crosscheck": {"ffprobe": True, "mp4box": True, "faad": faad_line("LC AAC", 2, 44100)},
    }))
    return out


# --- ADTS -----------------------------------------------------------------------------------------

def crc16(bits_list):
    """CRC-16 (polynomial 0x8005, initial 0xFFFF) over a list of (value, width) bit fields."""
    crc = 0xFFFF
    for value, width in bits_list:
        for i in range(width - 1, -1, -1):
            bit = (value >> i) & 1
            top = (crc >> 15) & 1
            crc = (crc << 1) & 0xFFFF
            if top ^ bit:
                crc ^= 0x8005
    return crc


def bit_fields(data, start, count):
    return [((data[(start + i) // 8] >> (7 - (start + i) % 8)) & 1, 1) for i in range(count)]


def element_region(block):
    """The bits an ADTS CRC covers in a raw data block whose first element is a single channel element of at least 192 bits."""
    assert block[0] >> 5 == 0
    return bit_fields(block, 3, 192)


def adts_header(length, rate=44100, channels=2, profile=1, mpeg2=False, protect=False, fullness=0x7FF, blocks=1, private=0):
    bits = Bits()
    bits.put(0xFFF, 12).put(1 if mpeg2 else 0, 1).put(0, 2).put(0 if protect else 1, 1).put(profile, 2).put(rate_index(rate), 4).put(private, 1)
    bits.put(channels, 3).put(0, 1).put(0, 1).put(0, 1).put(0, 1).put(length, 13).put(fullness, 11).put(blocks - 1, 2)
    return bits.bytes()


def adts_frame(blocks, rate=44100, channels=2, profile=1, mpeg2=False, protect=False, fullness=0x7FF, bad_header_crc=False, bad_block_crc=False):
    count = len(blocks)
    header_length = 7 + (2 + 2 * (count - 1) if protect else 0)
    length = header_length + sum(len(b) for b in blocks) + (2 * count if protect and count > 1 else 0)
    header = adts_header(length, rate, channels, profile, mpeg2, protect, fullness, count)
    if not protect:
        return header + b"".join(blocks)
    if count == 1:
        crc = crc16(bit_fields(header, 0, 56) + element_region(blocks[0]))
        return header + u16(crc ^ (0x0101 if bad_header_crc else 0)) + blocks[0]
    positions = []
    position = 0
    for block in blocks[:-1]:
        position += len(block) + 2
        positions.append(position)
    head = header + b"".join(u16(p) for p in positions)
    crc = crc16(bit_fields(head, 0, len(head) * 8))
    body = b""
    for number, block in enumerate(blocks):
        block_crc = crc16(element_region(block)) ^ (0x0101 if bad_block_crc and number == 1 else 0)
        body += block + u16(block_crc)
    return head + u16(crc ^ (0x0101 if bad_header_crc else 0)) + body


def adts_fixtures():
    out = []

    def add(name, description, data, frames, streams, findings=(), fields=None, skip=None, units_rows=None, extension=".aac", format="AAC", ffprobe_streams=True):
        rows = units_rows if units_rows is not None else [[o, l] for o, l in frames]
        # faad prints the duration of 1024 samples per frame, whatever number of raw data blocks a frame holds.
        rate = streams[0]["sample_rate"]
        check = {"ffprobe": "ffprobe" not in (skip or {}), "faad": None if "faad" in (skip or {}) else {"seconds": f"{len(frames) * 1024 / rate:.3f}", "rate": rate}}
        if skip:
            check["skip"] = skip
        if not ffprobe_streams or name == "raw-blocks":
            check.setdefault("skip", {})["faad_trace"] = "faad2 reads one raw data block per ADTS frame."
        if not ffprobe_streams:
            check["ffprobe_streams"] = False
            check.setdefault("skip", {})["ffprobe_streams"] = "ffmpeg 6.1 does not decode ADTS frames of several protected raw data blocks, so it reports no sample rate."
        out.append((name + extension, data, {
            "description": description, "length": len(data), "format": format, "boxes": [],
            "streams": streams, "units": rows, "fields": fields or {}, "findings": [list(f) for f in findings],
            "crosscheck": check,
        }))

    def concat(frames):
        data = b""
        rows = []
        for frame in frames:
            rows.append((len(data), len(frame)))
            data += frame
        return data, rows

    stereo = units("stereo", 30)
    data, rows = concat([adts_frame([u], fullness=0x100) for u in stereo])
    add("cbr", "ADTS AAC LC stereo at 44100 Hz with a buffer fullness of 0x100 in every frame, so a constant bitrate.", data, rows,
        [stream("AAC LC", 44100, 2, "Stereo", 30 * 1024)], [],
        {"ADTS/Frame/Buffer Fullness": "256", "ADTS/Frame/Profile": "1 (AAC LC)", "ADTS/Frame/Raw Data Block": "CPE"})

    data, rows = concat([adts_frame([u]) for u in stereo])
    add("vbr", "ADTS with a buffer fullness of 0x7FF in every frame, which signals a variable bitrate.", data, rows,
        [stream("AAC LC", 44100, 2, "Stereo", 30 * 1024, vbr=True)], [],
        {"ADTS/Frame/Buffer Fullness": "0x7FF (variable bitrate)"})

    mono = units("mono", 20, zero_bands=40)
    data, rows = concat([adts_frame([u], channels=1, protect=True) for u in mono])
    add("crc-mono", "Protected ADTS frames of one single channel element longer than 192 bits each: the CRC covers the header and the element's first 192 "
        "bits, and every frame's verifies.", data, rows, [stream("AAC LC", 44100, 1, "Mono", 20 * 1024, vbr=True)], [],
        {"ADTS/Frame/CRC/Check": "verified", "ADTS/Frame/IsProtected": "true"})

    frames = [adts_frame([u], channels=1, protect=True, bad_header_crc=(i == 5)) for i, u in enumerate(mono)]
    data, rows = concat(frames)
    add("crc-mono-damaged", "Protected mono frames, one of which has a wrong CRC, which fails.", data, rows,
        [stream("AAC LC", 44100, 1, "Mono", 20 * 1024, vbr=True)], [["aac.crc-mismatch", "1 ADTS frame fails the CRC check"]],
        {"ADTS/Frame[5]/CRC/Check": "mismatch", "ADTS/Frame[4]/CRC/Check": "verified"})

    groups = [mono[i:i + 4] for i in range(0, 20, 4)]
    data, rows = concat([adts_frame(g, channels=1, protect=True) for g in groups])
    add("crc-blocks", "Protected frames of four raw data blocks each: the header's CRC covers the header and the block positions, and each block ends in a "
        "CRC over its single channel element's first 192 bits.", data, rows, [stream("AAC LC", 44100, 1, "Mono", 20 * 1024, vbr=True)],
        [], {"ADTS/Frame/Raw Data Blocks": "4", "ADTS/Frame/CRC/Check": "verified", "ADTS/Frame/Raw Data Block CRC[2]/Check": "verified"},
        ffprobe_streams=False)

    data, rows = concat([adts_frame(g, channels=1, protect=True, bad_header_crc=(i == 2), bad_block_crc=(i == 3)) for i, g in enumerate(groups)])
    add("crc-blocks-damaged", "Frames of four raw data blocks, one with a wrong header CRC, which fails, and one with a wrong CRC after its second block, "
        "which fails too.", data, rows, [stream("AAC LC", 44100, 1, "Mono", 20 * 1024, vbr=True)],
        [["aac.crc-mismatch", "2 ADTS frames fail the CRC check"]],
        {"ADTS/Frame[2]/CRC/Check": "mismatch", "ADTS/Frame[3]/Raw Data Block CRC[1]/Check": "mismatch"},
        ffprobe_streams=False)

    groups = [stereo[i:i + 3] for i in range(0, 30, 3)]
    data, rows = concat([adts_frame(g) for g in groups])
    add("raw-blocks", "Unprotected frames of three raw data blocks each: only the first block's start is known.", data, rows,
        [stream("AAC LC", 44100, 2, "Stereo", 30 * 1024, vbr=True)], [], {"ADTS/Frame/Raw Data Blocks": "3"})

    frames = [adts_frame([u]) for u in stereo]
    damaged = list(frames)
    damaged[10] = b"\x00\x00" + damaged[10][2:]
    data, rows = concat(damaged)
    rows = [r for i, r in enumerate(rows) if i != 10]
    add("damaged-header", "Frame 11's sync word is overwritten: its bytes are a gap, and sync is found again at the next frame.", data, rows,
        [stream("AAC LC", 44100, 2, "Stereo", 29 * 1024, vbr=True)], [["aac.sync-lost", "ADTS sync is lost for"]],
        skip={"ffprobe": "ffmpeg resyncs after damage in its own way.", "faad": "faad2 stops at the damaged frame."})

    long_frame = bytearray(frames[7])
    length = len(long_frame) + 50
    long_frame[3] = (long_frame[3] & 0xFC) | (length >> 11)
    long_frame[4] = (length >> 3) & 0xFF
    long_frame[5] = ((length & 7) << 5) | (long_frame[5] & 0x1F)
    damaged = list(frames)
    damaged[7] = bytes(long_frame)
    data, rows = concat(damaged)
    add("damaged-length", "Frame 8 states a length 50 bytes too long: the next frame's header inside it cuts it short where the damage is.", data, rows,
        [stream("AAC LC", 44100, 2, "Stereo", 30 * 1024, vbr=True)], [["aac.frame-damaged", "cut short by the next frame's header"]],
        skip={"ffprobe": "ffmpeg resyncs after damage in its own way.", "faad": "faad2 stops at the damaged frame."})

    data, rows = concat(frames[:20])
    junk = bytes(range(1, 101))
    data = junk + data + b"trailing bytes"
    rows = [(o + 100, l) for o, l in rows]
    add("junk-around", "100 bytes of junk before the first frame and 14 after the last.", data, rows,
        [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)],
        [["aac.leading-junk", "100 bytes before the first ADTS frame"], ["aac.trailing-data", "14 bytes after the last ADTS frame"]],
        skip={"ffprobe": "ffmpeg's ADTS probe does not look past junk at the start.", "faad": "faad2 reads ADTS only from the first byte.",
              "faad_trace": "faad2 reads ADTS only from the first byte."})

    data, rows = concat(frames[:15] + [adts_frame([u], rate=48000) for u in stereo[:15]])
    add("stream-change", "15 frames at 44100 Hz, then 15 at 48000 Hz, as when two files are joined: two streams.", data, rows,
        [stream("AAC LC", 44100, 2, "Stereo", 15 * 1024, vbr=True),
         stream("AAC LC", 48000, 2, "Stereo", 15 * 1024, vbr=True)],
        [["aac.stream-changed", "from AAC LC, 44100 Hz, channel configuration 2 to AAC LC, 48000 Hz"]],
        skip={"ffprobe": "ffmpeg keeps one stream across the change of sample rate."})

    data, rows = concat([adts_frame([u], mpeg2=True) for u in stereo[:20]])
    add("mpeg2", "MPEG-2 ADTS (ID 1), whose profile 1 is LC as in MPEG-4.", data, rows,
        [stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)], [], {"ADTS/Frame/Version": "MPEG-2"})

    data, rows = concat(frames[:12])
    data = data[:-5]
    rows = rows[:11] + [(rows[11][0], len(data) - rows[11][0])]
    add("truncated", "The last frame is cut 5 bytes short.", data, rows,
        [stream("AAC LC", 44100, 2, "Stereo", 12 * 1024, vbr=True)], [["aac.frame-truncated", "of its"]],
        skip={"ffprobe": "ffmpeg drops the cut frame.", "faad": "faad2 does not count the cut frame."})

    # ADTS with channel configuration 0: each raw data block starts with a program config element.
    def pce_block():
        bits = Bits()
        bits.put(5, 3)
        start = bits.count
        bits.put(0, 4).put(1, 2).put(rate_index(48000), 4)
        bits.put(2, 4).put(0, 4).put(1, 4).put(1, 2).put(0, 3).put(0, 4)
        bits.put(0, 1).put(0, 1).put(0, 1)
        bits.put(0, 1).put(0, 4).put(1, 1).put(0, 4).put(1, 1).put(1, 4).put(0, 4)
        bits.align()
        bits.put(0, 8)
        _ = start
        from mp4_boxes import cpe, sce
        sce(bits)
        cpe(bits, 0)
        cpe(bits, 1)
        sce(bits, element=3)
        bits.put(7, 3)
        return bits.align().bytes()

    data, rows = concat([adts_frame([pce_block()], rate=48000, channels=0) for _ in range(12)])
    add("pce", "Channel configuration 0: every raw data block starts with a program config element for 5.1.", data, rows,
        [stream("AAC LC", 48000, 6, "6 channels", 12 * 1024, vbr=True)], [],
        {"ADTS/Frame/Raw Data Block": "PCE, SCE, CPE, CPE, LFE", "ADTS/Frame/Channel Configuration": "0 (defined in a program config element)"})

    # ADIF: the header with one program config element, then raw data blocks with nothing between them.
    bits = Bits()
    bits.put(int.from_bytes(b"ADIF", "big"), 32).put(0, 1).put(0, 1).put(0, 1).put(0, 1).put(128000, 23).put(0, 4)
    bits.put(0, 20)
    bits.put(0, 4).put(1, 2).put(rate_index(44100), 4).put(1, 4).put(0, 4).put(0, 4).put(0, 2).put(0, 3).put(0, 4).put(0, 1).put(0, 1).put(0, 1)
    bits.put(1, 1).put(0, 4)
    bits.align()
    bits.put(0, 8)
    header = bits.bytes()
    body = b"".join(stereo[:20])
    data = header + body
    out.append(("adif.aac", data, {
        "description": "ADIF: a constant bitrate of 128000 bit/s and one program config element with a channel pair, then 20 raw data blocks.",
        "length": len(data), "format": "AAC", "boxes": [],
        "streams": [{"codec": "AAC LC", "sample_rate": 44100, "channels": 2, "channel_mode": "2 channels", "channel_layout": SPEAKERS[2],
                     "samples": 20 * 1024, "is_vbr": False}],
        "units": [[len(header) + sum(len(u) for u in stereo[:i]), len(stereo[i])] for i in range(20)],
        "fields": {"ADIF/ADIF Header/Bitrate": "128000 bit/s", "ADIF/Raw Data Block": "CPE", "ADIF/Raw Data Block[1]": "CPE, FIL",
                   "ADIF/Statistics/Blocks": "20 blocks", "ADIF/Statistics/Largest Block": f"{max(len(u) for u in stereo[:20])} bytes"},
        "findings": [],
        "crosscheck": {"ffprobe": False, "faad": None, "skip": {"ffprobe": "ffmpeg does not read ADIF.", "faad": "faad2 reads ADIF headers but reports no duration."}},
    }))

    # ADTS headers inside an MP3: three consistent ADTS frames in the gap between MPEG frames ranks below the MPEG stream.
    mpeg = b"".join(mpeg_frames(10))
    adts = b"".join(frames[:3])
    data = mpeg[:417 * 5] + adts + mpeg[417 * 5:]
    out.append(("adts-inside-mp3.mp3", data, {
        "description": "Ten MPEG-1 Layer III frames with three consistent ADTS frames between the fifth and sixth: MPEG audio wins, the ADTS bytes are a gap.",
        "length": len(data), "format": "MPEG Audio", "boxes": [],
        "streams": [{"codec": "MPEG-1 Layer III", "sample_rate": 44100, "channels": 2, "channel_mode": "Stereo", "channel_layout": SPEAKERS[2],
                     "samples": 10 * 1152, "is_vbr": False}],
        "units": [[i * 417 + (len(adts) if i >= 5 else 0), 417] for i in range(10)], "fields": {},
        "findings": [["mpeg.sync-lost", f"Sync is lost for {len(adts)} bytes"]],
        "crosscheck": {"ffprobe": False, "faad": None, "skip": {"ffprobe": "the MPEG fixtures' cross-check covers MPEG audio; ffmpeg resyncs inside the gap its own way."}},
    }))
    return out


def write_fixture(directory, name, data, manifest):
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / (name if "." in name else name + (".mp4" if manifest.get("boxes") and name in ("fragmented", "mp3-in-mp4", "mp3-two-chunks-gap") else ".m4a"))
    if "." not in name and name.startswith("quicktime"):
        path = directory / (name + ".mov")
    path.write_bytes(data)
    stem = path.with_suffix("")
    (stem.parent / (stem.name + ".expected.json")).write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


def main():
    import aac_block_fixtures

    fixtures = he_aac_configs() + [xhe(), eld()] + xhe_layouts() + layout_variants() + channel_layouts() + gapless() + too_lavc() + metadata() + id32() + mp3_in_mp4() + quicktime() + chapters() + fragmented() + structure() + mp3_gap() + corrupt() + trailing_tags()
    for name, data, manifest in fixtures:
        write_fixture(MP4, name, data, manifest)
    for name, data, manifest in adts_fixtures():
        write_fixture(ADTS, name, data, manifest)
    for directory, name, data, manifest in aac_block_fixtures.fixtures():
        write_fixture(ADTS if directory == "adts" else MP4, name, data, manifest)
    print(f"{len(fixtures)} MP4 fixtures in {MP4}, the ADTS ones in {ADTS}")


if __name__ == "__main__":
    sys.exit(main())
