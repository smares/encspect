#!/usr/bin/env python3
"""Writes the byte-exact MPEG Layer II fixtures in tests/fixtures/layer2.

Each Layer II frame here carries explicit audio data (ISO/IEC 11172-3 2.4.1.6 and 2.4.2.6): a bit
allocation per subband and channel from one of the allocation tables (Annex B, Table B.2a to
B.2d, and ISO/IEC 13818-3 Table B.1 for the lower sample rates), scfsi and scalefactors, and
samples that are all coded as zero, so every decoder outputs pure silence and any disagreement
about where the allocation, scalefactors or samples end makes it decode noise. The bytes after the
samples are ancillary data.

Some fixtures carry the MPEG-2 multichannel extension (ISO/IEC 13818-3) in the ancillary data of
their MPEG-1 frames: the multichannel header, its CRC, the composite status information, the LFE
allocation, the allocation and scfsi of the additional channels (which the CRC covers), then
their prediction coefficients, scalefactors and zero samples. Dynamic crosstalk, multilingual
channels and extension bitstreams in their full form are left to the ISO conformance streams
(see conformance_mpeg_audio.py), which the cross-check runs too.

Manifests follow make_mpeg_fixtures.py. Per frame, "fields" lists the fields as the report shows
them, by node path; "scale_info" holds what minimp3 reads (see minimp3_side_info.c); "mc_crc"
whether the multichannel CRC holds. Fixture-level "silent" asks ffmpeg, mpg123 and libmad to
decode silence, "mctoolamed" asks the ISO-derived multichannel decoder to decode every channel to
silence with the given number of multichannel CRC errors, and "mp3guessenc_multichannel" lists
the multichannel parameters mp3guessenc prints, verbatim.

The script uses only the standard library and is deterministic, so CI reruns it and fails when
the committed files differ.
"""

from units import bytes_text
import pathlib
import struct
import sys

import make_mpeg_fixtures as mpeg
from make_mpeg_fixtures import Fixture, Frame, cbr_padding

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "layer2"

# Quantization levels per allocation index for each group of subbands; None is no allocation.
A0 = [None, 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535]
A3 = [None, 3, 5, 7, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 65535]
A11 = [None, 3, 5, 7, 9, 15, 31, 65535]
A23 = [None, 3, 5, 65535]
C0 = [None, 3, 5, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767]
C2 = [None, 3, 5, 9, 15, 31, 63, 127]
L0 = [None, 3, 5, 7, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383]
L4 = [None, 3, 5, 9, 15, 31, 63, 127]
L11 = [None, 3, 5, 9]
TABLES = [
    [A0] * 3 + [A3] * 8 + [A11] * 12 + [A23] * 4,  # B.2a
    [A0] * 3 + [A3] * 8 + [A11] * 12 + [A23] * 7,  # B.2b
    [C0] * 2 + [C2] * 6,  # B.2c
    [C0] * 2 + [C2] * 10,  # B.2d
    [L0] * 4 + [L4] * 7 + [L11] * 19,  # ISO/IEC 13818-3 B.1
]
TABLE_NAMES = ["B.2a", "B.2b", "B.2c", "B.2d", "B.1 (MPEG-2)"]
NBAL = {16: 4, 8: 3, 4: 2}
GROUPED = {3: 5, 5: 7, 9: 10}  # three samples in one codeword
SCALEFACTORS_PER_SCFSI = {0: 3, 1: 2, 2: 1, 3: 2}

CENTER = ["None", "Present", "Reserved", "Phantom"]
SURROUND = ["None", "Mono", "Stereo", "Second stereo programme"]
# (surround, center coded) -> (additional channels, tc_allocation bits, dyn_cross_mode bits, 2-bit
# predsi fields per group at dyn_cross_mode 0); ISO/IEC 13818-3, the syntax of mc_composite_status_info.
MC_SHAPE = {
    (0, 0): (0, 0, 0, 0),
    (0, 1): (1, 2, 1, 2),
    (1, 0): (1, 2, 1, 2),
    (1, 1): (2, 3, 3, 4),
    (2, 0): (2, 2, 3, 4),
    (2, 1): (3, 3, 4, 6),
    (3, 0): (2, 0, 0, 0),
    (3, 1): (3, 2, 1, 2),
}


def table_for(version, rate, bitrate, channels):
    """The allocation table index: 0 to 3 for B.2a to B.2d, 4 for the MPEG-2 table.

    The sampling frequency and the bitrate per channel select it (ISO/IEC 11172-3 2.4.3.3.1);
    bitrate is None for free format.
    """
    if version != "1":
        return 4
    if bitrate is None:
        return 0 if rate == 48000 else 1
    per_channel = bitrate // channels
    if per_channel <= 48:
        return 3 if rate == 32000 else 2
    if per_channel <= 80 or rate == 48000:
        return 0
    return 1


def zero_code(levels):
    """Bits and codeword of three zero samples."""
    if levels in GROUPED:
        z = (levels - 1) // 2
        return GROUPED[levels], z + z * levels + z * levels * levels
    bits = levels.bit_length()
    z = (1 << (bits - 1)) - 1
    return 3 * bits, (z << (2 * bits)) | (z << bits) | z


def minimp3_class(levels):
    return {3: 17, 5: 18, 9: 19}.get(levels) or levels.bit_length()


def minimp3_scalefactor(levels, index):
    """The float minimp3 computes for a scalefactor, as a hexadecimal float."""
    def f32(x):
        return struct.unpack("f", struct.pack("f", x))[0]
    divisors = [3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535, 3, 5, 9]
    bases = [9.53674316e-07, 7.56931807e-07, 6.00777173e-07]
    cls = minimp3_class(levels)
    dq = f32(f32(bases[index % 3]) / f32(divisors[cls - 2]))
    return float.hex(dq * (1 << 21 >> index // 3))


def expand_scalefactors(scfsi, values):
    """The scalefactors of the three parts of a frame from the transmitted ones (2.4.2.6, scfsi)."""
    if scfsi == 0:
        return values
    if scfsi == 1:
        return [values[0], values[0], values[1]]
    if scfsi == 2:
        return values * 3
    return [values[0], values[1], values[1]]


class Bits:
    def __init__(self):
        self.bits = []

    def put(self, value, count):
        assert 0 <= value < (1 << count), f"{value} does not fit {count} bits"
        self.bits += [(value >> (count - 1 - i)) & 1 for i in range(count)]

    def __len__(self):
        return len(self.bits)

    def bytes(self):
        padded = self.bits + [0] * (-len(self.bits) % 8)
        return bytes(int("".join(map(str, padded[i : i + 8])), 2) for i in range(0, len(padded), 8))


def crc16(bits, crc=0xFFFF):
    for bit in bits:
        top = (crc >> 15) & 1
        crc = (crc << 1) & 0xFFFF
        if top ^ bit:
            crc ^= 0x8005
    return crc


class Multichannel:
    """The MPEG-2 multichannel extension of one frame, without dynamic crosstalk."""

    def __init__(self, center=1, surround=2, lfe=0, audio_mix=0, dematrix=0, ml=0, ml_fs=0, ml_layer=0,
                 copyright_bit=0, copyright_start=0, n_ad_bytes=None, tc_alloc=(0,), prediction=None,
                 lfe_alloc=0, seed=1, bad_crc=False, continues=False, declared_ad_bytes=None, empty=False):
        self.center, self.surround, self.lfe, self.audio_mix, self.dematrix = center, surround, lfe, audio_mix, dematrix
        self.ml, self.ml_fs, self.ml_layer = ml, ml_fs, ml_layer
        self.copyright_bit, self.copyright_start = copyright_bit, copyright_start
        self.n_ad_bytes = n_ad_bytes  # None: no extension bitstream
        self.tc_alloc = tc_alloc  # one value for all subband groups, or twelve
        self.prediction = prediction  # eight entries: None or a list of predsi values
        self.lfe_alloc, self.seed, self.bad_crc = lfe_alloc if lfe else 0, seed, bad_crc
        # The side information continues in the extension bitstream: the frame keeps only the bits
        # that fit before its n_ad_bytes of MPEG-1 ancillary data.
        self.continues = continues
        # An n_ad_bytes in the header other than the ancillary bytes the frame holds; the CRC still
        # covers the header as written.
        self.declared_ad_bytes = declared_ad_bytes
        # No allocation in the additional channels, so the extension takes only a few bytes.
        self.empty = empty
        self.channels, self.tc_bits, _, self.predictors = MC_SHAPE[(surround, center & 1)]

    def configuration(self):
        front = 3 if self.center in (1, 3) else 2
        text = f"{front}/0 + 2/0" if self.surround == 3 else f"{front}/{[0, 1, 2][self.surround]}"
        if self.center == 3:
            text += " (phantom center)"
        if self.lfe:
            text += " + LFE"
        return text

    def write(self, bits, rate, fields):
        """Appends the extension to bits and records its fields; returns the CRC bits' position."""
        table = TABLES[0 if rate == 48000 else 1]
        start = len(bits)
        header = Bits()
        header.put(1 if self.n_ad_bytes is not None else 0, 1)
        if self.n_ad_bytes is not None:
            header.put(self.n_ad_bytes if self.declared_ad_bytes is None else self.declared_ad_bytes, 8)
        for value, count in [(self.center, 2), (self.surround, 2), (self.lfe, 1), (self.audio_mix, 1), (self.dematrix, 2),
                             (self.ml, 3), (self.ml_fs, 1), (self.ml_layer, 1), (self.copyright_bit, 1),
                             (self.copyright_start, 1)]:
            header.put(value, count)
        bits.bits += header.bits
        crc_at = len(bits)
        bits.put(0, 16)

        covered = Bits()
        covered.put(1 if len(self.tc_alloc) == 1 else 0, 1)
        covered.put(0, 1)  # dyn_cross_on
        covered.put(1 if self.prediction else 0, 1)
        for value in self.tc_alloc:
            covered.put(value, self.tc_bits)
        if self.prediction:
            for group in self.prediction:
                covered.put(1 if group is not None else 0, 1)
                if group is not None:
                    assert len(group) == self.predictors
                    for predsi in group:
                        covered.put(predsi, 2)
        if self.lfe:
            covered.put(self.lfe_alloc, 4)
        alloc, scfsi, scalefactors = {}, {}, {}
        for sb in range(len(table)):
            for ch in range(2, 2 + self.channels):
                if self.center == 3 and sb >= 12 and ch == 2:
                    continue
                index = (self.seed * 7 + sb * 3 + ch * 5) % 4 if sb < 12 and not self.empty else 0
                covered.put(index, NBAL[len(table[sb])])
                alloc[(ch, sb)] = index
        for sb in range(len(table)):
            for ch in range(2, 2 + self.channels):
                if alloc.get((ch, sb)):
                    value = (self.seed + sb + ch) % 4
                    covered.put(value, 2)
                    scfsi[(ch, sb)] = value
        crc = crc16(covered.bits, crc16(header.bits)) ^ (0x0001 if self.bad_crc else 0)
        bits.bits += covered.bits
        side_end = len(bits)
        bits.bits[crc_at : crc_at + 16] = [(crc >> (15 - i)) & 1 for i in range(16)]

        if self.prediction:
            for group in self.prediction:
                for predsi in group or []:
                    if predsi:
                        bits.put(0, 3)  # delay compensation
                        for _ in range(predsi):
                            bits.put(127, 8)  # coefficient index 127 is 0.0
        if self.lfe_alloc:
            bits.put(30, 6)
        for sb in range(len(table)):
            for ch in range(2, 2 + self.channels):
                if alloc.get((ch, sb)):
                    values = [(sb * 3 + ch + k) % 63 for k in range(SCALEFACTORS_PER_SCFSI[scfsi[(ch, sb)]])]
                    scalefactors[(ch, sb)] = values
                    for value in values:
                        bits.put(value, 6)
        for _ in range(12):
            if self.lfe_alloc:
                n = self.lfe_alloc + 1
                bits.put((1 << (n - 1)) - 1, n)
            for sb in range(len(table)):
                for ch in range(2, 2 + self.channels):
                    if alloc.get((ch, sb)):
                        count, code = zero_code(table[sb][alloc[(ch, sb)]])
                        bits.put(code, count)

        node = "Multichannel/"
        fields["Multichannel"] = self.configuration()
        fields[node + "Header/HasExtensionBitstream"] = "true" if self.n_ad_bytes is not None else "false"
        if self.n_ad_bytes is not None:
            fields[node + "Header/Ancillary Bytes"] = str(self.n_ad_bytes)
        fields[node + "Header/Center"] = CENTER[self.center]
        fields[node + "Header/Surround"] = SURROUND[self.surround]
        fields[node + "Header/HasLfe"] = "true" if self.lfe else "false"
        fields[node + "Header/Audio Mix"] = str(self.audio_mix)
        fields[node + "Header/Dematrix Procedure"] = str(self.dematrix)
        fields[node + "Header/Multilingual Channels"] = str(self.ml)
        fields[node + "Header/Multilingual Sample Rate"] = "Half" if self.ml_fs else "Full"
        fields[node + "Header/Multilingual Layer"] = "I" if self.ml_layer else "II"
        fields[node + "Header/HasCopyrightIdentification"] = "true" if self.copyright_bit else "false"
        fields[node + "Header/IsCopyrightIdentificationStart"] = "true" if self.copyright_start else "false"
        stored = crc
        fields[node + "CRC"] = f"0x{stored:04X}"
        fields[node + "CRC/Computed"] = f"0x{stored ^ (0x0001 if self.bad_crc else 0):04X}"
        fields[node + "Composite Status/Channel Allocation"] = ", ".join(map(str, self.tc_alloc))
        fields[node + "Composite Status/HasDynamicCrosstalk"] = "false"
        fields[node + "Composite Status/HasPrediction"] = "true" if self.prediction else "false"
        if self.prediction:
            fields[node + "Composite Status/Prediction"] = ", ".join("1" if g is not None else "0" for g in self.prediction)
        if self.lfe:
            fields[node + "LFE Allocation"] = f"{self.lfe_alloc + 1} bits" if self.lfe_alloc else "None"
        for (ch, sb), index in alloc.items():
            levels = table[sb][index]
            fields[f"{node}Bit Allocation/Subband {sb}/Channel {ch}"] = f"{levels} levels" if levels else "None"
        for (ch, sb), value in scfsi.items():
            fields[f"{node}SCFSI/Subband {sb}/Channel {ch}"] = str(value)
        return start, side_end


class Layer2Frame(Frame):
    def __init__(self, seed=0, allocation=None, scalefactor_override=None, ancillary=b"", mc=None, overflows=False, **kwargs):
        super().__init__(layer=2, **kwargs)
        self.seed, self.ancillary, self.mc = seed, ancillary, mc
        # The audio data runs past the frame, which keeps only what fits.
        self.overflows = overflows
        self.channels = 1 if self.mode == "Mono" else 2
        self.table_index = table_for(self.version, self.rate, self.bitrate, self.channels)
        self.table = TABLES[self.table_index]
        self.sblimit = len(self.table)
        self.bound = min((self.mode_ext + 1) * 4, self.sblimit) if self.mode == "Joint stereo" else self.sblimit
        self.scalefactor_override = scalefactor_override
        if allocation is not None:
            self.allocation = allocation
            return
        # Small allocations in a third of the subbands, and in one subband that moves with the seed,
        # the largest allocation that still leaves the frame room for its ancillary data and
        # multichannel extension.
        special = (self.seed * 7) % min(self.sblimit, 11)
        for index in range(len(self.table[special]) - 1 - self.seed % 3, 0, -1):
            self.allocation = self.default_allocation(special, index)
            if self.audio_bits() <= self.budget():
                return
        raise ValueError("no allocation fits the frame")

    def default_allocation(self, special, special_index):
        allocation = []
        for ch in range(self.channels):
            row = []
            for sb in range(self.sblimit):
                n = len(self.table[sb])
                if sb == special and ch == 0:
                    row.append(special_index)
                elif (sb + ch + self.seed) % 3 == 0 and sb < 20:
                    row.append(1 + (self.seed + sb + ch) % min(n - 1, 3))
                else:
                    row.append(0)
            allocation.append(row)
        return allocation

    def audio_bits(self):
        bits = 32 + (16 if self.protected else 0)
        for sb in range(self.sblimit):
            nbal = NBAL[len(self.table[sb])]
            for ch in self.coded_channels(sb):
                bits += nbal
                levels = self.table[sb][self.alloc(ch, sb)]
                if levels:
                    bits += 12 * zero_code(levels)[0]
            for ch in range(self.channels):
                if self.alloc(ch, sb):
                    bits += 2 + 6 * 3
        return bits

    def budget(self):
        if self.mc is not None and self.mc.continues:
            return self.length() * 8 - 8 * len(self.ancillary) - 64
        reserve = 8 * len(self.ancillary) + (500 + 1500 * self.mc.channels if self.mc is not None else 0)
        return self.length() * 8 - reserve

    def coded_channels(self, sb):
        return range(self.channels if sb < self.bound else 1)

    def alloc(self, ch, sb):
        return self.allocation[ch if sb < self.bound else 0][sb]

    def build(self):
        """The frame's bits after the header, its fields, and what minimp3 reads."""
        bits = Bits()
        fields = {}
        crc_at = None
        if self.protected:
            crc_at = len(bits)
            bits.put(0, 16)
        alloc_start = len(bits)
        for sb in range(self.sblimit):
            for ch in self.coded_channels(sb):
                bits.put(self.alloc(ch, sb), NBAL[len(self.table[sb])])
        scfsi, scalefactors = {}, {}
        for sb in range(self.sblimit):
            for ch in range(self.channels):
                if self.alloc(ch, sb):
                    scfsi[(ch, sb)] = (self.seed + sb * 3 + ch) % 4
                    bits.put(scfsi[(ch, sb)], 2)
        scfsi_end = len(bits)
        for sb in range(self.sblimit):
            for ch in range(self.channels):
                if self.alloc(ch, sb):
                    values = [(self.seed * 11 + sb * 5 + ch * 3 + k * 17) % 63 for k in range(SCALEFACTORS_PER_SCFSI[scfsi[(ch, sb)]])]
                    if self.scalefactor_override and self.scalefactor_override[0] == (ch, sb):
                        values[0] = self.scalefactor_override[1]
                    scalefactors[(ch, sb)] = values
                    for value in values:
                        bits.put(value, 6)
        scale_end = len(bits)
        for _ in range(12):
            for sb in range(self.sblimit):
                for ch in self.coded_channels(sb):
                    if self.alloc(ch, sb):
                        count, code = zero_code(self.table[sb][self.alloc(ch, sb)])
                        bits.put(code, count)
        samples_end = len(bits)

        mc_range = None
        if self.mc is not None:
            mc_range = self.mc.write(bits, self.rate, fields)
        self.payload_bits = bits
        self.offsets = (alloc_start, scfsi_end, scale_end, samples_end, mc_range)

        for sb in range(self.sblimit):
            for ch in self.coded_channels(sb):
                name = "Channels 0 and 1" if sb >= self.bound and self.channels == 2 else f"Channel {ch}"
                levels = self.table[sb][self.alloc(ch, sb)]
                fields[f"Bit Allocation/Subband {sb}/{name}"] = f"{levels} levels" if levels else "None"
        for (ch, sb), value in scfsi.items():
            fields[f"SCFSI/Subband {sb}/Channel {ch}"] = str(value)
        for (ch, sb), values in scalefactors.items():
            fields[f"Scalefactors/Subband {sb}/Channel {ch}"] = ", ".join(map(str, values))
        fields["Samples"] = f"{samples_end - scale_end} bits"
        fields["Bit Allocation"] = f"Table {TABLE_NAMES[self.table_index].split()[0]}"

        subbands = []
        for sb in range(self.sblimit):
            row = []
            for ch in range(self.channels):
                levels = self.table[sb][self.alloc(ch, sb)]
                entry = {"class": minimp3_class(levels) if levels else 0}
                if levels:
                    values = scalefactors[(ch, sb)]
                    expanded = expand_scalefactors(scfsi[(ch, sb)], values)
                    entry["scfsi"] = scfsi[(ch, sb)]
                    entry["scalefactors"] = [minimp3_scalefactor(levels, v) for v in expanded]
                row.append(entry)
            subbands.append(row)
        self.scale_info = {
            "layer": 2,
            "bands": self.sblimit,
            "bound": 0 if self.channels == 1 else self.bound,
            "scale_info_bits": scale_end,
            "subbands": subbands,
        }
        return bits, fields, crc_at, scfsi_end

    def bytes(self):
        header = self.header()
        bits, fields, crc_at, scfsi_end = self.build()
        body_length = self.length() - 4
        if self.mc is not None and self.mc.continues:
            room = 8 * (body_length - len(self.ancillary))
            assert self.offsets[4][1] > room, "the side information fits the frame"
            bits.bits = bits.bits[:room]
        data = bytearray(bits.bytes())
        if self.overflows:
            assert len(data) > body_length and 8 * body_length >= scfsi_end, "the audio data fits, or the CRC's bits do not"
            data = data[:body_length]
        assert len(data) + len(self.ancillary) <= body_length, f"{len(data)} + {len(self.ancillary)} > {body_length}"
        data += bytes(body_length - len(data) - len(self.ancillary)) + self.ancillary
        if self.protected:
            # CRC-16 over the last two header bytes, the bit allocation and scfsi (2.4.3.1).
            covered = [((header[2 + i // 8] >> (7 - i % 8)) & 1) for i in range(16)] + bits.bits[16:scfsi_end]
            self.crc = crc16(covered) ^ (0x0001 if self.bad_crc else 0)
            data[0:2] = self.crc.to_bytes(2, "big")
        self.fields_cache = fields
        return header + bytes(data)

    def fields(self):
        fields = super().fields()
        fields.update(self.fields_cache)
        offset = 4
        alloc_start, scfsi_end, scale_end, samples_end, mc_range = self.offsets
        mc_start = mc_range[0] if mc_range else None
        data_end = (mc_start if mc_start is not None else samples_end)
        ancillary_start = (data_end + 7) // 8
        if self.mc is not None and self.mc.continues:
            # Unverifiable, so reported as a possible extension, not as multichannel nodes.
            for key in [k for k in fields if k.startswith("Multichannel")]:
                del fields[key]
            fields["Ancillary Data"] = bytes_text(self.length() - 4 - ancillary_start)
        elif self.mc is not None:
            mc_end = self.length() - 4 - (self.mc.n_ad_bytes or 0)
            fields["Multichannel/Data"] = bytes_text(mc_end - (mc_range[1] + 7) // 8)
            if self.mc.n_ad_bytes:
                fields["Ancillary Data"] = bytes_text(self.mc.n_ad_bytes)
        else:
            length = self.length() - 4 - ancillary_start
            if length:
                fields["Ancillary Data"] = bytes_text(length)
        return fields


# --- Fixture assembly -------------------------------------------------------------------------------


class Layer2Fixture(Fixture):
    out = OUT

    def __init__(self, name, description, extension=".mp2", crosscheck=True, silent=True, mctoolamed=None, guessenc=None, mc_accepted=True):
        super().__init__(name, extension, description, crosscheck)
        self.silent, self.mctoolamed, self.guessenc = silent, mctoolamed, guessenc
        # Whether the analyzer takes the frames' multichannel data for an extension, so the stream
        # reports its channels; False where it is only a candidate.
        self.mc_accepted = mc_accepted
        self.layer2 = []
        self.layer2_frames = []

    def add_frames(self, frames, truncate_last=None):
        start = len(self.frames)
        self.layer2_frames += [frame for frame in frames if isinstance(frame, Layer2Frame)]
        super().add_frames(frames, truncate_last)
        for frame, entry in zip(frames, self.frames[start:]):
            entry["scale_info"] = frame.scale_info
            if frame.mc is not None:
                entry["mc_crc"] = not frame.mc.bad_crc
        return self

    def manifest(self):
        manifest = super().manifest()
        # A multichannel extension the analyzer accepts gives the stream its configuration's
        # speakers: left and right from the base, the center, the surround channels or the second
        # stereo programme's two discrete channels, then the LFE (a reserved center counts as none).
        mc = next((frame.mc for frame in self.layer2_frames if frame.mc is not None), None)
        if mc is not None and self.mc_accepted:
            layout = ["front left", "front right"] + (["front center"] if mc.center in (1, 3) else [])
            layout += [[], ["back center"], ["back left", "back right"], ["discrete", "discrete"]][mc.surround]
            layout += ["LFE"] if mc.lfe else []
            for entry in manifest["streams"]:
                entry["channel_layout"] = layout
        manifest["silent"] = self.silent
        if self.mctoolamed is not None:
            manifest["mctoolamed"] = self.mctoolamed
        if self.guessenc is not None:
            manifest["mp3guessenc_multichannel"] = self.guessenc
        return manifest


FIXTURES = []


def fixture(func):
    FIXTURES.append(func)
    return func


def stream(count, bitrate, rate, version="1", **kwargs):
    padding = cbr_padding(count, bitrate, rate, layer=2) if version == "1" else [0] * count
    frames = []
    for i in range(count):
        spec = {key: (value(i) if callable(value) else value) for key, value in kwargs.items()}
        spec.setdefault("seed", i)
        frames.append(Layer2Frame(version=version, bitrate=bitrate, rate=rate, padding=padding[i], **spec))
    return frames


def text(length, phrase=b"Ancillary data. "):
    return (phrase * (length // len(phrase) + 1))[:length]


# --- Allocation tables ------------------------------------------------------------------------------


@fixture
def table_a():
    f = Layer2Fixture("table-b2a", "MPEG-1 Layer II stereo at 48000 Hz and 192 kbps (96 per channel): allocation table "
                      "B.2a with 27 subbands, varied allocations, scfsi and scalefactors, ancillary text, and CRC.")
    return f.add_frames(stream(6, 192, 48000, mode="Stereo", protected=True, ancillary=text(40)))


@fixture
def table_b():
    f = Layer2Fixture("table-b2b", "MPEG-1 Layer II stereo at 44100 Hz and 256 kbps (128 per channel): allocation table "
                      "B.2b with 30 subbands.")
    return f.add_frames(stream(6, 256, 44100, mode="Stereo"))


@fixture
def table_c():
    f = Layer2Fixture("table-b2c", "MPEG-1 Layer II mono at 48000 Hz and 48 kbps: allocation table B.2c with 8 subbands.")
    return f.add_frames(stream(6, 48, 48000, mode="Mono", protected=True))


@fixture
def table_d():
    f = Layer2Fixture("table-b2d", "MPEG-1 Layer II stereo at 32000 Hz and 64 kbps (32 per channel): allocation table "
                      "B.2d with 12 subbands.")
    return f.add_frames(stream(6, 64, 32000, mode="Stereo"))


@fixture
def table_mpeg2():
    f = Layer2Fixture("table-mpeg2-joint", "MPEG-2 Layer II joint stereo at 24000 Hz and 64 kbps: table B.1 of ISO/IEC "
                      "13818-3 with 30 subbands, the bound rotating through subbands 4, 8, 12 and 16, with CRC.")
    return f.add_frames(stream(8, 64, 24000, version="2", mode="Joint stereo", mode_ext=lambda i: i % 4, protected=True))


@fixture
def mpeg2_mono():
    f = Layer2Fixture("table-mpeg2-mono", "MPEG-2 Layer II mono at 16000 Hz and 32 kbps: table B.1.")
    return f.add_frames(stream(6, 32, 16000, version="2", mode="Mono"))


@fixture
def joint_stereo():
    f = Layer2Fixture("joint-stereo-bounds", "MPEG-1 Layer II joint stereo at 44100 Hz and 256 kbps: above the bound "
                      "(subband 4, 8, 12 or 16, changing per frame) one allocation serves both channels.")
    return f.add_frames(stream(8, 256, 44100, mode="Joint stereo", mode_ext=lambda i: (i * 3) % 4, protected=True))


@fixture
def dual_channel():
    f = Layer2Fixture("dual-channel", "MPEG-1 Layer II dual channel at 48000 Hz and 256 kbps.")
    return f.add_frames(stream(6, 256, 48000, mode="Dual channel"))


# --- Damage -------------------------------------------------------------------------------------------


@fixture
def crc_mismatch():
    f = Layer2Fixture("crc-mismatch", "MPEG-1 Layer II with CRC; the third frame's CRC is wrong, which libmad reports.",
                      silent=False)
    frames = stream(6, 192, 48000, mode="Stereo", protected=True)
    frames[2].bad_crc = True
    f.add_frames(frames)
    return f.finding("mpeg.crc-mismatch", "1 frame")


@fixture
def undefined_scalefactor():
    f = Layer2Fixture("scalefactor-undefined", "MPEG-1 Layer II whose fourth frame uses scalefactor index 63, which "
                      "Table B.1 does not define. TwoLAME writes it too, and decoders accept it.")
    frames = stream(6, 192, 48000, mode="Stereo")
    first = next((ch, sb) for sb in range(frames[3].sblimit) for ch in range(2) if frames[3].alloc(ch, sb))
    frames[3].scalefactor_override = (first, 63)
    f.add_frames(frames)
    return f.finding("mpeg.scalefactor-undefined", "63")


@fixture
def scalefactor_overflow():
    f = Layer2Fixture(
        "scalefactor-overflow",
        "MPEG-2 Layer II stereo at 24000 Hz and 8 kbps with CRC: 48-byte frames whose bit allocation and scfsi fit, "
        "but whose scalefactors run past the frame. mpg123 and libmad reject them; the analyzer reports the overflow "
        "and still verifies the CRC, which covers only the allocation and scfsi, and fails in the third frame.",
        crosscheck=False,
        silent=False,
    )
    allocation = [[1] * 30, [1] * 30]
    frames = [Layer2Frame(version="2", bitrate=8, rate=24000, mode="Stereo", protected=True, allocation=allocation,
                          seed=i, overflows=True) for i in range(6)]
    frames[2].bad_crc = True
    f.add_frames(frames)
    for frame, entry in zip(frames, f.frames):
        del entry["scale_info"]
        entry["fields"] = mpeg.Frame.fields(frame)
        entry["fields"]["Data"] = bytes_text(frame.length() - 6)
    f.finding("mpeg.crc-mismatch", "1 frame fails")
    return f.finding("mpeg.audio-data-overflow", "6 frames need more bits for their Layer II audio data")


# --- Multichannel -------------------------------------------------------------------------------------


def mc_stream(count, bitrate, rate, mc, **kwargs):
    return stream(count, bitrate, rate, mode="Stereo", mc=lambda i: mc(i), **kwargs)


def guessenc(extension, lfe, channels, ml, configuration):
    yes = lambda flag: "yes" if flag else "no"
    return {"extension": yes(extension), "lfe": yes(lfe), "channels": str(channels), "ml": str(ml), "config": configuration}


@fixture
def mc_32_lfe():
    f = Layer2Fixture(
        "mc-3-2-lfe",
        "MPEG-1 Layer II at 48000 Hz and 384 kbps carrying MPEG-2 multichannel 3/2 + LFE: center and stereo "
        "surround, an LFE allocation and samples, prediction in three subband groups, and CRC on the base frames.",
        mctoolamed={"crc_errors": 0}, guessenc=guessenc(False, True, 3, 0, "3/2"),
    )
    prediction = [[1, 0, 2, 3, 0, 1], None, [0, 0, 0, 0, 0, 0], None, None, [3, 3, 3, 3, 3, 3], None, None]
    return f.add_frames(mc_stream(6, 384, 48000, lambda i: Multichannel(center=1, surround=2, lfe=1, lfe_alloc=3 + i % 3,
                                                                         dematrix=i % 4, prediction=prediction, seed=i),
                                  protected=True))


@fixture
def mc_21():
    f = Layer2Fixture(
        "mc-2-1",
        "MPEG-1 Layer II at 44100 Hz and 256 kbps carrying 2/1 (mono surround) with a transmission channel "
        "allocation per subband group.",
        mctoolamed={"crc_errors": 0}, guessenc=guessenc(False, False, 1, 0, "2/1"),
    )
    return f.add_frames(mc_stream(6, 256, 44100, lambda i: Multichannel(center=0, surround=1, tc_alloc=[(g + i) % 4 for g in range(12)],
                                                                         audio_mix=i % 2, seed=i)))


@fixture
def mc_second_stereo():
    f = Layer2Fixture(
        "mc-second-stereo",
        "MPEG-1 Layer II at 48000 Hz and 384 kbps carrying 3/0 + 2/0: a center channel and a second stereo programme.",
        mctoolamed={"crc_errors": 0}, guessenc=guessenc(False, False, 3, 0, "3/0 and 2/0"),
    )
    return f.add_frames(mc_stream(6, 384, 48000, lambda i: Multichannel(center=1, surround=3, tc_alloc=(i % 4,), seed=i + 2)))


@fixture
def mc_phantom():
    f = Layer2Fixture(
        "mc-phantom-center",
        "MPEG-1 Layer II at 48000 Hz and 384 kbps carrying 3/2 with phantom center coding: the center channel has no "
        "allocation above subband 11.",
        mctoolamed={"crc_errors": 0}, guessenc=guessenc(False, False, 3, 0, "3/2"),
    )
    return f.add_frames(mc_stream(6, 384, 48000, lambda i: Multichannel(center=3, surround=2, tc_alloc=(i % 8,), seed=i + 4,
                                                                         copyright_bit=1, copyright_start=1 if i == 0 else 0)))


@fixture
def mc_configuration_change():
    f = Layer2Fixture(
        "mc-configuration-change",
        "MPEG-1 Layer II at 48000 Hz and 384 kbps carrying 3/2 in its first three frames and 2/0 + 2/0 in the last three, "
        "every multichannel CRC intact: the extension is reported in each frame, but the stream, whose speakers change, "
        "reports its stereo base, with an info. mctoolamed decodes all six frames into the five channels of the first.",
        mctoolamed={"crc_errors": 0, "channels": 5}, mc_accepted=False,
    )
    f.add_frames(mc_stream(6, 384, 48000, lambda i: Multichannel(center=1 if i < 3 else 0, surround=2 if i < 3 else 3, tc_alloc=(i % 4 if i < 3 else 0,),
                                                                  seed=i + 6)))
    return f.finding("mpeg.multichannel-configuration-changes", "from 3/2 to 2/0 + 2/0")


@fixture
def mc_extension():
    f = Layer2Fixture(
        "mc-extension-header",
        "MPEG-1 Layer II at 48000 Hz and 384 kbps whose multichannel header announces an extension bitstream and "
        "12 bytes of MPEG-1 ancillary data at the end of each frame. The multichannel side information fits the base "
        "frame; the extension bitstream itself is a separate file and not part of the fixture, so only mp3guessenc "
        "checks it.",
        guessenc=guessenc(True, False, 3, 0, "3/2"),
    )
    return f.add_frames(mc_stream(6, 384, 48000, lambda i: Multichannel(center=1, surround=2, n_ad_bytes=12, seed=i),
                                  ancillary=text(12, b"MPEG-1 anc. ")))


@fixture
def mc_unverified():
    f = Layer2Fixture(
        "mc-unverified",
        "MPEG-1 Layer II at 48000 Hz and 128 kbps whose multichannel side information (3/2 + LFE) continues in an "
        "extension bitstream in every frame: 255 bytes of MPEG-1 ancillary data, the most n_ad_bytes can announce, "
        "leave too little room in the base frame. Without the extension bitstream no multichannel CRC can be "
        "checked, so the extension is reported as possible, not as fact, and the stream as its stereo base.",
        mc_accepted=False,
    )
    sparse = [[1, 1, 1, 1] + [0] * 23, [1, 1, 1, 1] + [0] * 23]
    f.add_frames(mc_stream(6, 128, 48000, lambda i: Multichannel(center=1, surround=2, lfe=1, lfe_alloc=2, n_ad_bytes=255, seed=i,
                                                                  continues=True), ancillary=bytes(i % 251 for i in range(255)),
                           allocation=sparse))
    return f.finding("mpeg.multichannel-unverified", "3/2 + LFE")


@fixture
def mc_ancillary_overflow():
    f = Layer2Fixture(
        "mc-ancillary-overflow",
        "MPEG-1 Layer II at 48000 Hz and 112 kbps carrying 3/2 with an extension bitstream and 12 bytes of MPEG-1 "
        "ancillary data, whose fourth frame's multichannel header declares 255 bytes: more than the frame holds after "
        "the header and its CRC. That frame's multichannel data is taken to end after the CRC, and the bytes after "
        "it are ancillary data.",
    )
    sparse = [[1, 1, 1, 1] + [0] * 23, [1, 1, 1, 1] + [0] * 23]
    frames = mc_stream(6, 112, 48000, lambda i: Multichannel(center=1, surround=2, n_ad_bytes=12, seed=i, empty=True,
                                                              declared_ad_bytes=255 if i == 3 else None),
                       ancillary=text(12, b"MPEG-1 anc. "), allocation=sparse)
    f.add_frames(frames)
    frame, entry = frames[3], f.frames[3]
    del entry["mc_crc"]
    fields = entry["fields"]
    for key in [k for k in fields if k.startswith("Multichannel/") and not k.startswith(("Multichannel/Header", "Multichannel/CRC"))]:
        del fields[key]
    del fields["Multichannel/CRC/Computed"]
    fields["Multichannel/Header/Ancillary Bytes"] = "255"
    mc_start = frame.offsets[4][0]
    crc_end = mc_start + 24 + 16
    fields["Ancillary Data"] = bytes_text(frame.length() - 4 - (crc_end + 7) // 8)
    return f.finding("mpeg.multichannel-ancillary-overflow", "1 frame declares more MPEG-1 ancillary bytes")


@fixture
def mc_crc_mismatch():
    f = Layer2Fixture(
        "mc-crc-mismatch",
        "Multichannel 3/2 at 48000 Hz whose fourth frame has a wrong multichannel CRC: mctoolamed drops that frame's "
        "additional channels.",
        mctoolamed={"crc_errors": 1}, guessenc=guessenc(False, False, 3, 0, "3/2"),
    )
    f.add_frames(mc_stream(6, 384, 48000, lambda i: Multichannel(center=1, surround=2, seed=i, bad_crc=i == 3)))
    return f.finding("mpeg.multichannel-crc-mismatch", "1 frame")


@fixture
def mc_center_reserved():
    f = Layer2Fixture(
        "mc-center-reserved",
        "Multichannel at 48000 Hz whose header uses the reserved center value 2 in every frame, with valid "
        "multichannel CRCs: read as no center, so 2/2, and reported.",
    )
    f.add_frames(mc_stream(6, 384, 48000, lambda i: Multichannel(center=2, surround=2, seed=i)))
    return f.finding("mpeg.multichannel-center-reserved", "6 frames")


@fixture
def mc_chance_crc():
    f = Layer2Fixture(
        "mc-chance-crc",
        "MPEG-1 Layer II at 48000 Hz and 384 kbps whose ancillary data reads as a multichannel extension in all 20 "
        "frames, but whose multichannel CRC holds in only two, as random data passes it about once in 65,536 frames: "
        "two passes among 20 frames are chance, so no multichannel extension is reported and the stream is its stereo base.",
        mc_accepted=False,
    )
    frames = mc_stream(20, 384, 48000, lambda i: Multichannel(center=1, surround=2, seed=i, bad_crc=i not in (4, 13)))
    f.add_frames(frames)
    for frame, entry in zip(frames, f.frames):
        del entry["mc_crc"]
        fields = entry["fields"]
        for key in [k for k in fields if k.startswith("Multichannel")]:
            del fields[key]
        fields["Ancillary Data"] = bytes_text(frame.length() - 4 - (frame.offsets[3] + 7) // 8)
    return f


@fixture
def free_format_48000():
    f = Layer2Fixture(
        "free-format-48000",
        "Free-format MPEG-1 Layer II stereo at 48000 Hz with 240-byte frames (40 kbps per channel) and CRC. Free format "
        "takes table B.2a at 48000 Hz whatever the frame length, where the fixed bitrate rules would give B.2c. ffmpeg "
        "does not read free format, so the cross-check counts libmad's frames.",
        crosscheck="madplay",
    )
    f.add_frames([Layer2Frame(bitrate=None, free_length=240, rate=48000, mode="Stereo", protected=True, seed=i) for i in range(6)])
    return f.finding("mpeg.free-format", "240")


@fixture
def free_format_44100():
    f = Layer2Fixture(
        "free-format-44100",
        "Free-format MPEG-1 Layer II stereo at 44100 Hz with 555-byte frames (85 kbps per channel) and CRC: table "
        "B.2b, where the fixed bitrate rules would give B.2d.",
        crosscheck="madplay",
    )
    f.add_frames([Layer2Frame(bitrate=None, free_length=555, rate=44100, mode="Stereo", protected=True, seed=i) for i in range(6)])
    return f.finding("mpeg.free-format", "555")


@fixture
def free_format_unprotected():
    f = Layer2Fixture(
        "free-format-48000-unprotected",
        "Free-format MPEG-1 Layer II stereo at 48000 Hz with 240-byte frames and no CRC, which ISO/IEC 11172-3 allows. "
        "Nothing but the frames themselves says they are MPEG audio, which is all the analyzer needs when no other "
        "format claims the file.",
        crosscheck="madplay",
    )
    f.add_frames([Layer2Frame(bitrate=None, free_length=240, rate=48000, mode="Stereo", protected=False, seed=i) for i in range(6)])
    return f.finding("mpeg.free-format", "240")


@fixture
def free_format_first_crc_mismatch():
    f = Layer2Fixture(
        "free-format-44100-crc-mismatch",
        "Free-format MPEG-1 Layer II stereo at 44100 Hz with 555-byte frames and CRC; the first frame's CRC is wrong, as "
        "after damage, and the stream is still MPEG audio.",
        crosscheck="madplay",
        silent=False,
    )
    frames = [Layer2Frame(bitrate=None, free_length=555, rate=44100, mode="Stereo", protected=True, seed=i) for i in range(6)]
    frames[0].bad_crc = True
    f.add_frames(frames)
    f.finding("mpeg.free-format", "555")
    return f.finding("mpeg.crc-mismatch", "1 frame")


@fixture
def not_multichannel():
    f = Layer2Fixture(
        "ancillary-not-multichannel",
        "MPEG-1 Layer II at 48000 Hz and 384 kbps whose ancillary data is text: no multichannel CRC holds, so no "
        "multichannel extension is reported.",
    )
    return f.add_frames(stream(6, 384, 48000, mode="Stereo", ancillary=text(700)))


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
