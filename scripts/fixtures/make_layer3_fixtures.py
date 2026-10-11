#!/usr/bin/env python3
"""Writes the byte-exact MPEG Layer III side-information and bit-reservoir fixtures in tests/fixtures/layer3.

Each Layer III frame here carries explicit side information (ISO/IEC 11172-3 2.4.1.7 and
2.4.2.7, ISO/IEC 13818-3 2.4.1.7) and main data that is a valid Huffman coding of all-zero
values: zero scalefactors, then big-value pairs coded as (0, 0), then count1 quadruples coded as
0000. part2_3_length is the exact number of those bits, so every decoder outputs pure silence,
and any disagreement about where a granule's bits start or end makes the output non-silent. The
main data is laid out through the bit reservoir: main_data_begin points back into the data of
earlier frames, and bytes no frame uses are ancillary data.

Manifests extend the ones from make_mpeg_fixtures.py. Per frame:

- "fields": header and side-information fields as the report shows them, by node path.
- "side_info": the raw side-information values, compared with minimp3 by crosscheck_mpeg_fixtures.py.
- "reservoir": bytes left in the bit reservoir before the frame: the free bytes after the
  previous frame's main data, the bytes main_data_begin may point back into. The report's
  "Side Info/Reservoir Fill" field shows them up to what main_data_begin can reach (511 bytes in
  MPEG-1, 255 in MPEG-2 and 2.5).
- "data": the frame's bytes after the side information, split by owner: "Main Data" (this
  frame's own, always present, possibly empty), "Bit Reservoir" (main data of a later frame) and
  "Ancillary Data" (bytes no frame's main data uses).

The reservoir model, which the analyzer must follow: main data runs through the data of
consecutive frames of one stream (same version, layer and sample rate), skipping headers and
side information, and continues across junk and tags between frames; a stream change starts an
empty reservoir. A frame's main data starts main_data_begin bytes before its own data and is
ceil(sum of part2_3_length / 8) bytes long. Where main data starts before the stream, overlaps the
previous frame's main data, reaches back across a gap, or does not fit the frame, the bytes are
kept with the earlier owner and the analyzer reports a finding.

The Huffman code of the pair (0, 0) for each table is in ZERO_PAIR_CODE (Table B.7). Where the
regions of a granule use different tables, every table of the granule has the same (0, 0) code,
so the region boundaries do not matter. Scalefactor bit counts follow 2.4.2.7 (MPEG-1,
scalefac_compress to slen1 and slen2, and scfsi) and ISO/IEC 13818-3 2.4.3.2 (MPEG-2 and 2.5,
including the intensity stereo right channel).

The script uses only the standard library and is deterministic, so CI reruns it and fails when
the committed files differ.
"""

import json
import pathlib
import sys

import ancillary_scan
import make_mpeg_fixtures as mpeg
import stream_stats
from make_mpeg_fixtures import Fixture, Frame, cbr_padding, junk, side_info_size

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "layer3"

BLOCK_TYPES = ["Normal", "Start", "Short", "Stop"]
SHORT = 2

ZERO_PAIR_CODE = {0: "", 1: "1", 2: "1", 3: "11", 5: "1", 6: "111", 7: "1", 8: "11", 9: "111", 10: "1", 11: "11",
                  12: "1001", 13: "1", 15: "111"}
ZERO_PAIR_CODE.update({t: "1" for t in range(16, 24)})
ZERO_PAIR_CODE.update({t: "1111" for t in range(24, 32)})
ZERO_QUAD_CODE = ["1", "1111"]  # count1 table A codes 0000 as 1, table B as 1111.

SLEN = [(0, 0), (0, 1), (0, 2), (0, 3), (3, 0), (1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (2, 3), (3, 1), (3, 2), (3, 3), (4, 2), (4, 3)]

# ISO/IEC 13818-3 Table B.? (nr_of_sfb_block): for each scalefactor partition table, the number of
# scalefactors per slen for long, short and mixed blocks.
NR_OF_SFB = [
    [[6, 5, 5, 5], [9, 9, 9, 9], [6, 9, 9, 9]],
    [[6, 5, 7, 3], [9, 9, 12, 6], [6, 9, 12, 6]],
    [[11, 10, 0, 0], [18, 18, 0, 0], [15, 18, 0, 0]],
    [[7, 7, 7, 0], [12, 12, 12, 0], [6, 15, 12, 0]],
    [[6, 6, 6, 3], [12, 9, 9, 6], [6, 12, 9, 6]],
    [[8, 8, 5, 0], [15, 12, 9, 0], [6, 18, 9, 0]],
]


def lsf_slen(scalefac_compress, intensity_right):
    """slen1 to slen4 and the partition table of MPEG-2 and 2.5 scalefactors."""
    sfc = scalefac_compress
    if not intensity_right:
        if sfc < 400:
            return [(sfc >> 4) // 5, (sfc >> 4) % 5, (sfc % 16) >> 2, sfc % 4], 0
        if sfc < 500:
            x = sfc - 400
            return [(x >> 2) // 5, (x >> 2) % 5, x % 4, 0], 1
        x = sfc - 500
        return [x // 3, x % 3, 0, 0], 2
    i = sfc >> 1
    if i < 180:
        return [i // 36, (i % 36) // 6, (i % 36) % 6, 0], 3
    if i < 244:
        x = i - 180
        return [(x % 64) >> 4, (x % 16) >> 2, x % 4, 0], 4
    x = i - 244
    return [x // 3, x % 3, 0, 0], 5


class Bits:
    def __init__(self):
        self.bits = []

    def put(self, value, count):
        self.bits += [(value >> (count - 1 - i)) & 1 for i in range(count)]

    def put_code(self, code, repeat=1):
        self.bits += [int(c) for c in code] * repeat

    def __len__(self):
        return len(self.bits)

    def bytes(self):
        padded = self.bits + [0] * (-len(self.bits) % 8)
        return bytes(int("".join(map(str, padded[i : i + 8])), 2) for i in range(0, len(padded), 8))


class Granule:
    """Side information of one granule and channel, and its zero-valued main data."""

    def __init__(self, big_values=0, global_gain=0, scalefac_compress=0, block_type=0, switched=None, mixed=0,
                 tables=(0, 0, 0), subblock_gain=(0, 0, 0), regions=(0, 0), preflag=0, scalefac_scale=0,
                 count1_table=0, quads=0, part2_3_length=None):
        self.big_values, self.global_gain, self.scalefac_compress = big_values, global_gain, scalefac_compress
        self.block_type = block_type
        self.switched = block_type != 0 if switched is None else switched
        self.mixed, self.subblock_gain, self.regions = mixed, subblock_gain, regions
        self.tables = tuple(tables[:2]) if self.switched else tuple(tables)
        self.preflag, self.scalefac_scale, self.count1_table = preflag, scalefac_scale, count1_table
        self.quads, self.length_override = quads, part2_3_length
        assert big_values * 2 + quads * 4 <= 576 or big_values > 288

    def zero_pair_code(self):
        codes = {ZERO_PAIR_CODE.get(t) for t in self.tables}
        assert len(codes) == 1 or self.big_values == 0, f"tables {self.tables} code (0, 0) differently"
        return codes.pop() if len(codes) == 1 else ""

    def huffman_bits(self):
        bits = Bits()
        if self.big_values:
            bits.put_code(self.zero_pair_code(), self.big_values)
        bits.put_code(ZERO_QUAD_CODE[self.count1_table], self.quads)
        return bits.bits


class Layer3Frame(Frame):
    def __init__(self, granules, main_data_begin=0, private_bits=0, scfsi=(0, 0), **kwargs):
        super().__init__(layer=3, **kwargs)
        self.granules, self.main_data_begin, self.private_bits = granules, main_data_begin, private_bits
        self.scfsi = scfsi
        self.area = bytearray(self.area_length())
        assert len(granules) == self.granule_count() and all(len(g) == self.channels() for g in granules)

    def mpeg1(self):
        return self.version == "1"

    def channels(self):
        return 1 if self.mode == "Mono" else 2

    def granule_count(self):
        return 2 if self.mpeg1() else 1

    def side_info_length(self):
        return side_info_size(self.version, self.mode)

    def area_length(self):
        return self.length() - 4 - (2 if self.protected else 0) - self.side_info_length()

    def part2_bits(self, gr, ch):
        g = self.granules[gr][ch]
        short = g.switched and g.block_type == SHORT
        if self.mpeg1():
            slen1, slen2 = SLEN[g.scalefac_compress]
            if short:
                return (17 if g.mixed else 18) * slen1 + 18 * slen2
            groups = [6 * slen1, 5 * slen1, 5 * slen2, 5 * slen2]
            reused = self.scfsi[ch] if gr == 1 else 0
            return sum(bits for i, bits in enumerate(groups) if not reused >> (3 - i) & 1)
        intensity_right = ch == 1 and self.mode == "Joint stereo" and self.mode_ext & 1
        slen, table = lsf_slen(g.scalefac_compress, intensity_right)
        kind = (2 if g.mixed else 1) if short else 0
        return sum(s * n for s, n in zip(slen, NR_OF_SFB[table][kind]))

    def granule_bits(self, gr, ch):
        return [0] * self.part2_bits(gr, ch) + self.granules[gr][ch].huffman_bits()

    def part2_3_length(self, gr, ch):
        g = self.granules[gr][ch]
        return len(self.granule_bits(gr, ch)) if g.length_override is None else g.length_override

    def main_data_length(self):
        total = sum(self.part2_3_length(gr, ch) for gr in range(self.granule_count()) for ch in range(self.channels()))
        return (total + 7) // 8

    def main_data(self):
        bits = Bits()
        for gr in range(self.granule_count()):
            for ch in range(self.channels()):
                bits.bits += self.granule_bits(gr, ch)
        return bits.bytes()

    def side_info(self):
        bits = Bits()
        if self.mpeg1():
            bits.put(self.main_data_begin, 9)
            bits.put(self.private_bits, 5 if self.channels() == 1 else 3)
            for ch in range(self.channels()):
                bits.put(self.scfsi[ch], 4)
        else:
            bits.put(self.main_data_begin, 8)
            bits.put(self.private_bits, 1 if self.channels() == 1 else 2)
        for gr in range(self.granule_count()):
            for ch in range(self.channels()):
                g = self.granules[gr][ch]
                bits.put(self.part2_3_length(gr, ch), 12)
                bits.put(g.big_values, 9)
                bits.put(g.global_gain, 8)
                bits.put(g.scalefac_compress, 4 if self.mpeg1() else 9)
                bits.put(1 if g.switched else 0, 1)
                if g.switched:
                    bits.put(g.block_type, 2)
                    bits.put(g.mixed, 1)
                    for table in g.tables:
                        bits.put(table, 5)
                    for gain in g.subblock_gain:
                        bits.put(gain, 3)
                else:
                    for table in g.tables:
                        bits.put(table, 5)
                    bits.put(g.regions[0], 4)
                    bits.put(g.regions[1], 3)
                if self.mpeg1():
                    bits.put(g.preflag, 1)
                bits.put(g.scalefac_scale, 1)
                bits.put(g.count1_table, 1)
        data = bits.bytes()
        assert len(data) == self.side_info_length()
        return data

    def bytes(self):
        header = self.header()
        side_info = self.side_info()
        if not self.protected:
            return header + side_info + bytes(self.area)
        self.crc = mpeg.crc16(header[2:4] + side_info) ^ (0x0001 if self.bad_crc else 0)
        return header + self.crc.to_bytes(2, "big") + side_info + bytes(self.area)

    def is_rejected_by_minimp3(self):
        """minimp3 rejects big_values above 288, block type 0 with window switching, and main data that does not fit."""
        slots = [(gr, ch) for gr in range(self.granule_count()) for ch in range(self.channels())]
        for gr, ch in slots:
            g = self.granules[gr][ch]
            if g.big_values > 288 or (g.switched and g.block_type == 0):
                return True
        return sum(self.part2_3_length(gr, ch) for gr, ch in slots) > (self.area_length() + self.main_data_begin) * 8

    def fields(self):
        fields = super().fields()
        fields["Side Info/Main Data Begin"] = f"{self.main_data_begin} bytes"
        fields["Side Info/Private Bits"] = str(self.private_bits)
        if self.mpeg1():
            for ch in range(self.channels()):
                fields[f"Side Info/SCFSI/Channel {ch}"] = format(self.scfsi[ch], "04b")
        for gr in range(self.granule_count()):
            for ch in range(self.channels()):
                g = self.granules[gr][ch]
                path = f"Side Info/Granule {gr}/Channel {ch}/"
                fields[path + "Part 2 and 3 Length"] = f"{self.part2_3_length(gr, ch)} bits"
                fields[path + "Big Values"] = str(g.big_values)
                fields[path + "Global Gain"] = str(g.global_gain)
                fields[path + "Scalefactor Compress"] = str(g.scalefac_compress)
                fields[path + "Block Type"] = BLOCK_TYPES[g.block_type] if g.switched == (g.block_type != 0) else "Reserved"
                fields[path + "Table Select"] = ", ".join(map(str, g.tables))
                if g.switched:
                    fields[path + "IsMixedBlock"] = "true" if g.mixed else "false"
                    fields[path + "Subblock Gain"] = ", ".join(map(str, g.subblock_gain))
                else:
                    fields[path + "Region 0 Count"] = str(g.regions[0])
                    fields[path + "Region 1 Count"] = str(g.regions[1])
                if self.mpeg1():
                    fields[path + "HasPreflag"] = "true" if g.preflag else "false"
                fields[path + "HasCoarseScalefactors"] = "true" if g.scalefac_scale else "false"
                fields[path + "Count1 Table"] = "AB"[g.count1_table]
        return fields

    def raw_side_info(self):
        """The values minimp3_side_info.c prints for this frame."""
        if self.is_rejected_by_minimp3():
            return {"main_data_begin": -1}
        has_short = any(g.block_type == SHORT for row in self.granules for g in row)
        granules = []
        for gr in range(self.granule_count()):
            for ch in range(self.channels()):
                g = self.granules[gr][ch]
                entry = {
                    "part2_3_length": self.part2_3_length(gr, ch),
                    "big_values": g.big_values,
                    "global_gain": g.global_gain,
                    "scalefac_compress": g.scalefac_compress,
                    "block_type": g.block_type,
                    "scalefac_scale": g.scalefac_scale,
                    "count1_table": g.count1_table,
                    "table_select": list(g.tables),
                }
                if g.switched:
                    entry["mixed_block_flag"] = g.mixed
                    entry["subblock_gain"] = list(g.subblock_gain)
                else:
                    entry["region0_count"], entry["region1_count"] = g.regions
                if self.mpeg1():
                    entry["preflag"] = g.preflag
                    if not has_short and gr == 1:
                        entry["scfsi"] = self.scfsi[ch]
                granules.append(entry)
        return {"main_data_begin": self.main_data_begin, "granules": granules}


# --- Main data layout ---------------------------------------------------------------------------


def pack(frames, ancillary=b"\x00"):
    """Writes each frame's main data main_data_begin bytes before its own data; other bytes are ancillary."""
    stream = bytearray((ancillary * (sum(f.area_length() for f in frames) // len(ancillary) + 1))[: sum(f.area_length() for f in frames)])
    position = 0
    previous_end = 0
    for frame in frames:
        start = position - frame.main_data_begin
        data = frame.main_data()
        end = start + frame.main_data_length()
        if not frame.is_rejected_by_minimp3():
            assert start >= previous_end, f"main data of frame {frames.index(frame)} overlaps the previous frame's"
            assert end <= position + frame.area_length(), f"main data of frame {frames.index(frame)} does not fit"
        for i, byte in enumerate(data):
            if 0 <= start + i < position + frame.area_length():
                stream[start + i] = byte
        previous_end = max(previous_end, min(end, position + frame.area_length()))
        position += frame.area_length()
    position = 0
    for frame in frames:
        frame.area[:] = stream[position : position + frame.area_length()]
        position += frame.area_length()
    return frames


# --- Expectations -------------------------------------------------------------------------------


def is_header(frame):
    """Whether the frame holds a Xing, Info or VBRI header: a frame without main data that keeps its
    place in the reservoir, as minimp3 and mp3guessenc keep it, and has no ancillary segment."""
    return getattr(frame, "is_header", False)


def area_length(frame):
    if is_header(frame):
        return frame.length() - 4 - (2 if frame.protected else 0) - side_info_size(frame.version, frame.mode)
    return frame.area_length()


def area(frame):
    return frame.bytes()[frame.length() - area_length(frame):] if is_header(frame) else frame.area


def main_data_begin(frame):
    return 0 if is_header(frame) else frame.main_data_begin


def main_data_length(frame):
    return 0 if is_header(frame) else frame.main_data_length()


class Layer3Fixture(Fixture):
    out = OUT

    def __init__(self, name, description, crosscheck=True, silent=True, guessenc=True):
        super().__init__(name, ".mp3", description, crosscheck)
        self.silent, self.guessenc = silent, guessenc
        self.layer3 = []
        self.extra = {}

    def finding(self, code, contains):
        # The analyzer reports each kind once per file, pointing at the first case.
        if all(existing["code"] != code for existing in self.findings):
            super().finding(code, contains)
        return self

    def add_frames(self, frames, truncate_last=None):
        start = len(self.frames)
        super().add_frames(frames, truncate_last)
        for frame, entry in zip(frames, self.frames[start:]):
            self.layer3.append((frame, entry))
        return self

    def model(self):
        """What the analyzer reports for main data, reservoir and ancillary bytes (see the module docstring)."""
        chains, chain = [], []
        for frame, entry in self.layer3:
            if chain and not same_stream(chain[-1][0], frame):
                chains.append(chain)
                chain = []
            chain.append((frame, entry))
        if chain:
            chains.append(chain)

        for chain in chains:
            # Positions in the chain's main-data stream: frame data only, without headers, side
            # information and anything between frames.
            starts, position = [], 0
            for frame, _ in chain:
                starts.append(position)
                position += area_length(frame)
            owner = [None] * position
            previous_end = 0
            ancillary_after = []
            audio_frames = 0
            for k, (frame, entry) in enumerate(chain):
                area_start, area_end = starts[k], starts[k] + area_length(frame)
                begin = area_start - main_data_begin(frame)
                end = begin + main_data_length(frame)
                if not is_header(frame):
                    entry["reservoir"] = area_start - previous_end
                    reach = 511 if frame.mpeg1() else 255
                    entry["fields"]["Side Info/Reservoir Fill"] = f"{min(entry['reservoir'], reach)} bytes"
                    entry["side_info"] = frame.raw_side_info()
                    if frame.main_data_begin > entry["reservoir"]:
                        if audio_frames == 0:
                            self.finding("mpeg.main-data-before-stream", f"0x{entry['offset']:X}")
                        else:
                            self.finding("mpeg.main-data-overlap", f"0x{entry['offset']:X}")
                    elif begin < area_start and crosses_gap(chain, starts, begin, k):
                        self.finding("mpeg.reservoir-across-gap", f"0x{entry['offset']:X}")
                    elif begin < area_start and any(is_header(f) and begin < starts[j] + area_length(f) for j, (f, _) in enumerate(chain[:k])):
                        self.finding("mpeg.main-data-in-vbr-header-frame", f"0x{entry['offset']:X}")
                    audio_frames += 1
                if end > area_end:
                    self.finding("mpeg.main-data-overflow", f"0x{entry['offset']:X}")
                for p in range(max(begin, previous_end, 0), min(end, area_end)):
                    owner[p] = k
                if k:
                    ancillary_after.append(max(begin, 0) - previous_end if begin > previous_end else 0)
                previous_end = max(previous_end, min(end, area_end))
                entry["main_data_length"] = main_data_length(frame)
            ancillary_after.append(position - previous_end)

            for k, (frame, entry) in enumerate(chain):
                file_start = entry["offset"] + entry["length"] - area_length(frame)
                if is_header(frame):
                    # After its header, the frame's bytes are unused or main data of later frames.
                    tag_end = frame.tag_end - (entry["length"] - area_length(frame))
                    for p in range(starts[k] + tag_end, starts[k] + area_length(frame)):
                        if owner[p] is not None:
                            name, value = "Bit Reservoir", f"of the main data of the frame at 0x{chain[owner[p]][1]['offset']:X}"
                        else:
                            name, value = "Unused", ""
                        length = 1
                        while p + length < starts[k] + area_length(frame) and owner[p + length] == owner[p]:
                            length += 1
                        if name not in entry["fields"]:
                            entry["fields"][name] = f"{length} bytes {value}".strip()
                    continue
                own = sum(1 for p in range(starts[k], starts[k] + frame.area_length()) if owner[p] == k)
                earlier = sum(1 for p in range(starts[k]) if owner[p] == k)
                value = f"{frame.main_data_length()} bytes" + (f", {earlier} of them in earlier frames" if earlier else "")
                runs = []
                for p in range(starts[k], starts[k] + frame.area_length()):
                    o = owner[p]
                    name = "Ancillary Data" if o is None else "Main Data" if o == k else "Bit Reservoir"
                    if runs and runs[-1]["name"] == name and runs[-1]["owner"] == o:
                        runs[-1]["length"] += 1
                    else:
                        runs.append({"name": name, "offset": file_start + p - starts[k], "length": 1, "owner": o})
                if not any(r["name"] == "Main Data" for r in runs):
                    runs.insert(0, {"name": "Main Data", "offset": file_start, "length": 0, "owner": k})
                for run in runs:
                    o = run.pop("owner")
                    if run["name"] == "Main Data":
                        run["value"] = value
                    elif run["name"] == "Bit Reservoir":
                        run["value"] = f"{run['length']} bytes of the main data of the frame at 0x{chain[o][1]['offset']:X}"
                    else:
                        run["value"] = f"{run['length']} bytes"
                entry["data"] = runs
                assert own == next(r for r in runs if r["name"] == "Main Data")["length"]
            for (frame, entry), anc in zip(chain, ancillary_after):
                entry["ancillary_after"] = anc

    def guessenc_summary(self):
        """The statistics mp3guessenc 0.27.6 prints, computed from the frames."""
        blocks = {"long": 0, "switch": 0, "short": 0, "mixed": 0}
        modes = {}
        gains = {}
        for frame, entry in self.layer3:
            if is_header(frame):
                continue
            ext = frame.mode_ext if frame.mode == "Joint stereo" else None
            modes[str(ext)] = modes.get(str(ext), 0) + 1
            for row in frame.granules:
                for ch, g in enumerate(row):
                    kind = "long" if not g.switched else ("mixed" if g.mixed else "short") if g.block_type == SHORT else "switch"
                    blocks[kind] += 1
                    gains.setdefault(ch, []).append(g.global_gain)
        ancillary = [entry["ancillary_after"] for _, entry in self.layer3 if entry["ancillary_after"] > 0]
        return {
            "blocks": blocks,
            "mode_extensions": modes,
            "global_gain": {str(ch): [min(v), max(v)] for ch, v in gains.items()},
            "max_main_data_begin": max(main_data_begin(frame) for frame, _ in self.layer3),
            "ancillary": [sum(ancillary), min(ancillary), max(ancillary)] if ancillary else [0, 0, 0],
            "scalefac_scale_used": any(g.scalefac_scale for f, _ in self.layer3 if not is_header(f) for row in f.granules for g in row),
            "scfsi_used": any(f.mpeg1() and any(f.scfsi) for f, _ in self.layer3 if not is_header(f)),
        }

    def segments(self):
        """Each frame's ancillary segment (ancillary_scan.py), by frame offset: from the end of its
        main data to the start of the next frame's, or None where that is not defined. A truncated
        frame or a stream change ends the stream, so the frame before it keeps the rest of its data."""
        result = {}
        stream, previous, previous_end, key = bytearray(), None, None, None
        for frame, entry in self.layer3 + [(None, None)]:
            if frame is None or entry.get("truncated") or key != (frame.version, frame.rate):
                if previous is not None:
                    result[previous] = None if previous_end is None else bytes(stream[previous_end:])
                stream, previous, previous_end, key = bytearray(), None, None, None
                if frame is None or entry.get("truncated"):
                    continue
                key = (frame.version, frame.rate)
            start = len(stream)
            stream += area(frame)
            if is_header(frame):
                continue
            begin = start - frame.main_data_begin
            end = begin + frame.main_data_length()
            if previous is not None:
                fits = previous_end is not None and 0 <= begin and previous_end <= begin
                result[previous] = bytes(stream[previous_end:begin]) if fits else None
            previous = entry["offset"]
            previous_end = end if begin >= 0 and end <= len(stream) else None
        return result

    def ancillary(self):
        """Adds what each region's ancillary data holds (ancillary_scan.py) and its findings."""
        segments = self.segments()
        for region in self.regions:
            if region["name"] != "MPEG Audio":
                continue
            inside = [(frame, entry) for frame, entry in self.layer3
                      if region["offset"] <= entry["offset"] < region["offset"] + region["length"] and entry["offset"] in segments]
            if not inside:
                continue
            first = inside[0][0]
            version = mpeg.VERSION_NAMES[first.version]
            enhancement, total, block = ancillary_scan.scan([segments[entry["offset"]] for _, entry in inside], version, first.rate)
            audio = [frame for frame, entry in self.layer3
                     if region["offset"] <= entry["offset"] < region["offset"] + region["length"] and not entry.get("truncated")
                     and not is_header(frame)]
            ticks = sum(stream_stats.samples_per_frame(mpeg.VERSION_NAMES[f.version], "III") * (stream_stats.CLOCK // f.rate) for f in audio)
            fields = {}
            if enhancement:
                fields["Enhancement"] = enhancement
                fields["Enhancement/Ancillary Data"] = f"{total} bytes"
                fields["Enhancement/Data Rate"] = stream_stats.kbps_tenths_text(total * 8, ticks)
                code = "mpeg.mp3pro" if enhancement == ancillary_scan.MP3PRO else "mpeg.mp3-surround"
                self.finding(code, f"{total} bytes")
            if block:
                delay, length, _ = block
                fields["Original File Length"] = f"{length} samples"
                fields["Original File Length/Delay"] = f"{delay} samples"
                fields["Original File Length/Original Length"] = f"{length} samples"
                fields["Original File Length/Duration"] = stream_stats.duration_text(length * (stream_stats.CLOCK // first.rate))
                samples = sum(stream_stats.samples_per_frame(mpeg.VERSION_NAMES[f.version], "III") for f in audio)
                if delay + length > samples:
                    self.finding("mpeg.ofl-exceeds-stream", f"{delay + length} samples")
            if fields:
                region["ancillary"] = fields

    def manifest(self):
        self.model()
        self.ancillary()
        manifest = super().manifest()
        manifest["silent"] = self.silent
        if self.guessenc:
            manifest["mp3guessenc"] = self.guessenc_summary()
        for entry in manifest["frames"]:
            entry.pop("ancillary_after", None)
        manifest.update(self.extra)
        return manifest


def same_stream(a, b):
    return a.version == b.version and a.rate == b.rate


def crosses_gap(chain, starts, begin, k):
    """Whether main data starting at stream position begin reaches into a frame before a gap."""
    for j in range(k, 0, -1):
        previous, current = chain[j - 1][1], chain[j][1]
        if previous["offset"] + previous["length"] != current["offset"]:
            return True
        if starts[j - 1] <= begin:
            return False
    return False


FIXTURES = []


def fixture(func):
    FIXTURES.append(func)
    return func


def frames_with_padding(specs, bitrate, rate, **kwargs):
    padding = cbr_padding(len(specs), bitrate, rate)
    return [Layer3Frame(bitrate=bitrate, rate=rate, padding=p, **dict(kwargs, **spec)) for spec, p in zip(specs, padding)]


def fill(slots, total_bits):
    """Zero-valued granules for the given number of slots whose bits add up to total_bits."""
    granules, left = [], total_bits
    for i in range(slots):
        share = left // (slots - i)
        big_values = min(288, share // 4)
        quads = min((576 - 2 * big_values) // 4, share - 4 * big_values)
        granules.append(Granule(big_values=big_values, tables=(24, 25, 31), quads=quads, global_gain=150 + i, regions=(7, 7)))
        left -= 4 * big_values + quads
    assert left == 0, f"{total_bits} bits do not fit {slots} granules"
    return granules


def stereo_granules(bits_per_frame, granule_count=2, channels=2):
    flat = fill(granule_count * channels, bits_per_frame)
    return [flat[i * channels : (i + 1) * channels] for i in range(granule_count)]


# --- Side information -----------------------------------------------------------------------------


@fixture
def long_blocks():
    f = Layer3Fixture(
        "side-info-long-blocks",
        "MPEG-1 Layer III stereo, long blocks only: every side-information field with values away from zero, scfsi in "
        "both channels, private bits, and no bit reservoir (main_data_begin 0).",
    )
    specs = [
        dict(private_bits=0b101, scfsi=(0b1010, 0b0001), granules=[
            [Granule(big_values=100, global_gain=0, scalefac_compress=5, tables=(1, 5, 7), regions=(0, 0), quads=50),
             Granule(big_values=50, global_gain=255, tables=(3, 8, 11), regions=(15, 7), quads=30, count1_table=1, preflag=1)],
            [Granule(big_values=20, global_gain=128, scalefac_compress=12, tables=(6, 9, 15), regions=(7, 3), quads=10, scalefac_scale=1),
             Granule(big_values=288, global_gain=77, scalefac_compress=15, tables=(16, 20, 23), regions=(3, 1))],
        ]),
        dict(private_bits=0b010, scfsi=(0b1111, 0b0110), granules=[
            [Granule(big_values=200, global_gain=90, tables=(0, 0, 0), regions=(5, 2), quads=40),
             Granule(big_values=10, global_gain=91, scalefac_compress=9, tables=(24, 27, 31), regions=(9, 4), quads=100, count1_table=1)],
            [Granule(big_values=5, global_gain=92, scalefac_compress=9, tables=(12, 12, 12), regions=(2, 6), quads=12, preflag=1, scalefac_scale=1),
             Granule(big_values=0, global_gain=93, scalefac_compress=1, tables=(13, 2, 10), regions=(11, 5), quads=144)],
        ]),
        dict(granules=[
            [Granule(), Granule()],
            [Granule(), Granule(global_gain=210, quads=1)],
        ]),
        dict(private_bits=0b111, scfsi=(0b0101, 0b1000), granules=[
            [Granule(big_values=143, global_gain=140, scalefac_compress=4, tables=(5, 1, 17), regions=(4, 2), quads=1),
             Granule(big_values=143, global_gain=141, scalefac_compress=7, tables=(5, 1, 17), regions=(4, 2), quads=1)],
            [Granule(big_values=143, global_gain=142, scalefac_compress=4, tables=(5, 1, 17), regions=(4, 2), quads=1),
             Granule(big_values=143, global_gain=143, scalefac_compress=7, tables=(5, 1, 17), regions=(4, 2), quads=1)],
        ]),
    ]
    return f.add_frames(pack(frames_with_padding(specs, 128, 44100, mode="Stereo")))


@fixture
def mono_long_blocks():
    f = Layer3Fixture(
        "side-info-mono",
        "MPEG-1 Layer III mono at 48000 Hz: 17 bytes of side information, five private bits, one channel per granule.",
    )
    specs = [
        dict(private_bits=0b10101, scfsi=(0b1001,), granules=[
            [Granule(big_values=60, global_gain=170, scalefac_compress=14, tables=(7, 10, 13), regions=(6, 3), quads=20)],
            [Granule(big_values=60, global_gain=171, scalefac_compress=14, tables=(7, 10, 13), regions=(6, 3), quads=20, preflag=1)],
        ]),
        dict(private_bits=0b01010, scfsi=(0b0000,), granules=[
            [Granule(big_values=1, global_gain=1, tables=(9, 9, 9), quads=0, count1_table=1)],
            [Granule(big_values=0, global_gain=2, quads=143, count1_table=1, scalefac_scale=1)],
        ]),
        dict(granules=[[Granule(global_gain=210)], [Granule(global_gain=210)]]),
    ]
    return f.add_frames(pack(frames_with_padding(specs, 64, 48000, mode="Mono")))


@fixture
def block_types():
    f = Layer3Fixture(
        "side-info-block-types",
        "MPEG-1 Layer III joint stereo with CRC: start, short, mixed and stop blocks next to normal ones, subblock "
        "gains, and scfsi only on channels without short blocks. Frames with MS stereo give both channels the same "
        "block type; the others mix block types (libmad rejects both otherwise). The CRC covers the side "
        "information.",
    )
    specs = [
        dict(mode_ext=2, scfsi=(0b0011, 0b1100), granules=[
            [Granule(big_values=30, global_gain=160, scalefac_compress=3, tables=(1, 2, 5), regions=(3, 3), quads=10),
             Granule(big_values=30, global_gain=161, scalefac_compress=3, tables=(1, 2, 5), regions=(3, 3), quads=10)],
            [Granule(big_values=40, global_gain=162, scalefac_compress=6, block_type=1, tables=(7, 10), subblock_gain=(1, 2, 3), quads=5),
             Granule(big_values=40, global_gain=163, scalefac_compress=6, block_type=1, tables=(7, 10), subblock_gain=(0, 0, 7), quads=5)],
        ]),
        dict(mode_ext=0, scfsi=(0b0000, 0b0000), granules=[
            [Granule(big_values=50, global_gain=164, scalefac_compress=10, block_type=2, tables=(13, 16), subblock_gain=(7, 0, 5), quads=3),
             Granule(big_values=50, global_gain=165, scalefac_compress=10, block_type=2, mixed=1, tables=(13, 16), subblock_gain=(2, 2, 2), quads=3)],
            [Granule(big_values=60, global_gain=166, scalefac_compress=13, block_type=2, tables=(3, 11), subblock_gain=(4, 5, 6), quads=2, scalefac_scale=1),
             Granule(big_values=60, global_gain=167, scalefac_compress=13, block_type=3, tables=(3, 11), subblock_gain=(0, 1, 0), quads=2)],
        ]),
        dict(mode_ext=0, scfsi=(0b0101, 0b0000), granules=[
            [Granule(big_values=70, global_gain=168, scalefac_compress=8, block_type=3, tables=(6, 15), quads=0, preflag=1),
             Granule(big_values=70, global_gain=169, scalefac_compress=8, block_type=2, mixed=1, tables=(6, 15), subblock_gain=(3, 3, 3), quads=0)],
            [Granule(big_values=10, global_gain=170, scalefac_compress=2, tables=(15, 9, 6), regions=(1, 1), quads=0),
             Granule(big_values=10, global_gain=171, scalefac_compress=2, block_type=3, tables=(9, 6), quads=0, count1_table=1)],
        ]),
        dict(mode_ext=2, granules=[
            [Granule(global_gain=172), Granule(global_gain=173)],
            [Granule(global_gain=174), Granule(global_gain=175)],
        ]),
    ]
    return f.add_frames(pack(frames_with_padding(specs, 160, 44100, mode="Joint stereo", protected=True)))


@fixture
def mpeg2_intensity():
    f = Layer3Fixture(
        "side-info-mpeg2-intensity",
        "MPEG-2 Layer III joint stereo at 22050 Hz with MS and intensity stereo: 9-bit scalefac_compress values from "
        "each scalefactor partition table, including the intensity stereo right channel, one granule per frame, and "
        "two private bits.",
    )
    pairs = [(0, 0), (123, 100), (399, 359), (400, 360), (455, 487), (499, 488), (500, 511), (511, 2)]
    specs = []
    for i, (left, right) in enumerate(pairs):
        block = [0, 0, 1, 2, 2, 3, 0, 0][i]
        mixed = 1 if i == 4 else 0
        specs.append(dict(mode_ext=3, private_bits=i % 4, granules=[[
            Granule(big_values=40 + i, global_gain=150 + i, scalefac_compress=left, block_type=block, mixed=mixed,
                    tables=(2, 5, 7), regions=(i, 7 - i), subblock_gain=(i % 8, 0, 1), quads=8),
            Granule(big_values=20 + i, global_gain=140 + i, scalefac_compress=right, block_type=block, mixed=mixed,
                    tables=(11, 8, 3), regions=(7 - i, i), subblock_gain=(0, i % 8, 2), quads=4, count1_table=i % 2),
        ]]))
    return f.add_frames(pack(frames_with_padding(specs, 64, 22050, version="2", mode="Joint stereo")))


@fixture
def mpeg25_mono():
    f = Layer3Fixture(
        "side-info-mpeg25-mono",
        "MPEG-2.5 Layer III mono at 8000 Hz: 9 bytes of side information, one private bit, short and mixed blocks.",
    )
    specs = []
    for i, (block, mixed) in enumerate([(0, 0), (1, 0), (2, 0), (2, 1), (3, 0), (0, 0)]):
        specs.append(dict(private_bits=i % 2, granules=[[
            Granule(big_values=30 + i, global_gain=180 + i, scalefac_compress=[0, 37, 222, 405, 503, 12][i], block_type=block,
                    mixed=mixed, tables=(16, 18, 21), regions=(3, 4), subblock_gain=(i, 7 - i, 1), quads=6, scalefac_scale=i % 2),
        ]]))
    return f.add_frames(pack(frames_with_padding(specs, 16, 8000, version="2.5", mode="Mono")))


@fixture
def invalid_side_info():
    f = Layer3Fixture(
        "side-info-invalid",
        "MPEG-1 Layer III joint stereo with six broken frames between valid ones: big_values 300 (above 288), window "
        "switching with the reserved block type 0, the Huffman tables 4 and 14 that do not exist, scalefactors "
        "longer than part2_3_length, and two that libmad rejects: scfsi on a channel with short blocks, and MS "
        "stereo with different block types in the two channels. Frames stay frames; each problem is reported once.",
        crosscheck=False,
        silent=False,
        guessenc=False,
    )
    ok = lambda gain: [[Granule(global_gain=gain), Granule(global_gain=gain)], [Granule(global_gain=gain), Granule(global_gain=gain)]]
    specs = [
        dict(granules=ok(100)),
        dict(granules=[[Granule(big_values=300, global_gain=101), Granule(global_gain=101)], [Granule(), Granule()]]),
        dict(granules=ok(102)),
        dict(granules=[[Granule(global_gain=103, switched=True, block_type=0, tables=(1, 1)), Granule()], [Granule(), Granule()]]),
        dict(granules=ok(104)),
        dict(granules=[[Granule(global_gain=105, tables=(4, 0, 14)), Granule()], [Granule(), Granule()]]),
        dict(granules=[[Granule(global_gain=106, scalefac_compress=15, part2_3_length=50), Granule()], [Granule(), Granule()]]),
        dict(granules=ok(107)),
        dict(scfsi=(0b1010, 0), granules=[[Granule(global_gain=108), Granule()], [Granule(block_type=2), Granule()]]),
        dict(granules=ok(109)),
        dict(mode_ext=2, granules=[[Granule(global_gain=110, block_type=1), Granule(global_gain=110)], [Granule(), Granule()]]),
        dict(granules=ok(111)),
    ]
    f.add_frames(pack(frames_with_padding(specs, 128, 44100, mode="Joint stereo")))
    f.finding("mpeg.big-values-too-large", "300")
    f.finding("mpeg.block-type-reserved", "block type 0")
    f.finding("mpeg.huffman-table-undefined", "table")
    f.finding("mpeg.scalefactors-exceed-granule", "scalefactor")
    f.finding("mpeg.scfsi-with-short-blocks", "scfsi")
    f.finding("mpeg.stereo-block-types-differ", "block type")
    return f


# --- Bit reservoir ----------------------------------------------------------------------------------


def reservoir_chain(lengths_and_begins, bitrate=128, rate=44100, mode="Joint stereo", version="1", ancillary=b"Ancillary data. "):
    """Frames whose main data has the given lengths in bytes and main_data_begin values."""
    channels = 1 if mode == "Mono" else 2
    granule_count = 2 if version == "1" else 1
    specs = []
    for length, begin in lengths_and_begins:
        bits = length * 8 - (3 if length else 0)
        specs.append(dict(main_data_begin=begin, granules=stereo_granules(bits, granule_count, channels)))
    return pack(frames_with_padding(specs, bitrate, rate, version=version, mode=mode), ancillary)


CHAIN = [
    (100, 0),    # leaves 281 bytes
    (300, 200),  # 200 from the reservoir, 100 in its own data
    (560, 282),  # takes the whole reservoir and 278 bytes of its own data
    (50, 0),     # the 104 bytes left in frame 2 become ancillary data
    (20, 300),   # all of its main data lies in frame 3
    (10, 500),   # so does this one, after ancillary data of frame 4
    (560, 511),  # the largest main_data_begin MPEG-1 allows
    (382, 0),    # fills its own data exactly
    (0, 0),      # no main data at all
    (300, 381),
    (200, 81),
    (40, 0),     # the rest of the last frame is ancillary data
]


@fixture
def reservoir():
    f = Layer3Fixture(
        "reservoir-chain",
        "MPEG-1 Layer III frames whose main data runs through the bit reservoir: main data split across frames, frames "
        "whose main data lies entirely in an earlier frame, two frames' main data inside one frame, the largest "
        "main_data_begin (511), a frame with no main data, and text as ancillary data.",
    )
    return f.add_frames(reservoir_chain(CHAIN))


@fixture
def reservoir_mpeg2():
    f = Layer3Fixture(
        "reservoir-mpeg2-max",
        "MPEG-2 Layer III mono at 22050 Hz: frames without main data whose main_data_begin points back keep their "
        "data in the reservoir, then main_data_begin reaches its MPEG-2 maximum of 255.",
    )
    # An empty frame with main_data_begin 0 would give up the reservoir before it, so they point back.
    chain = [(0, 0), (0, 91), (0, 182), (140, 255), (30, 0), (60, 61), (20, 0)]
    return f.add_frames(reservoir_chain(chain, bitrate=32, rate=22050, mode="Mono", version="2"))


@fixture
def reservoir_cut():
    f = Layer3Fixture(
        "reservoir-cut-start",
        "The reservoir chain without its first two frames, as when a file is cut: the first frame's main data begins "
        "before the stream, so decoders lose it, and the frames after it are fine.",
        silent=False,
        guessenc=False,
    )
    return f.add_frames(reservoir_chain(CHAIN)[2:])


@fixture
def reservoir_splice():
    f = Layer3Fixture(
        "reservoir-splice",
        "The reservoir chain with frames 4 and 5 cut out: frame 6 points back 511 bytes, past the 332 free bytes "
        "after frame 3, into the main data of frames 2 and 3. Frames are missing, which nothing but the reservoir "
        "shows.",
        silent=False,
        guessenc=False,
    )
    frames = reservoir_chain(CHAIN)
    return f.add_frames(frames[:4] + frames[6:])


@fixture
def reservoir_gap():
    f = Layer3Fixture(
        "reservoir-across-gap",
        "The reservoir chain with 40 bytes of junk between frames 3 and 4: frame 4's main data lies in frame 3, "
        "across the junk. ffmpeg keeps the reservoir across the gap, minimp3 and libmad empty it. Four frames come "
        "first, because two frames before junk are not a chain.",
        crosscheck=False,
        silent=False,
        guessenc=False,
    )
    frames = reservoir_chain(CHAIN)
    f.add_frames(frames[:4])
    f.gap(junk(40, 61))
    f.add_frames(frames[4:])
    return f.finding("mpeg.sync-lost", "40 bytes")


@fixture
def reservoir_stream_change():
    f = Layer3Fixture(
        "reservoir-stream-change",
        "Four frames at 44100 Hz, then frames at 48000 Hz whose first main_data_begin is 120: a new stream starts with "
        "an empty reservoir, so that main data begins before the stream.",
        silent=False,
        guessenc=False,
    )
    f.add_frames(reservoir_chain([(100, 0), (200, 0), (300, 100), (50, 0)]))
    second = reservoir_chain([(0, 0), (200, 0), (300, 120), (100, 50), (60, 0)], rate=48000)
    f.add_frames(second[2:])
    return f.finding("mpeg.stream-changed", "48000 Hz")


@fixture
def main_data_overflow():
    f = Layer3Fixture(
        "main-data-overflow",
        "MPEG-1 Layer III stereo where the third frame declares more main data (part2_3_length) than the reservoir "
        "and its own data hold.",
        silent=False,
        guessenc=False,
    )
    frames = reservoir_chain([(100, 0), (300, 0), (40, 0), (100, 0), (60, 0)])
    frames[2].granules[0][0].length_override = 4000
    frames[2].granules[1][1].length_override = 4000
    for frame in frames:
        frame.area[:] = bytes(len(frame.area))
    return f.add_frames(pack(frames))


# --- mp3PRO, MP3 Surround and OFL -------------------------------------------------------------------


def enhanced_chain(segments, main=100, **kwargs):
    """Frames with main bytes of main data each; every frame's ancillary segment starts with the
    given bytes, or is empty for None: the next frame's main data then starts right after it."""
    probe = reservoir_chain([(main, 0)] * len(segments), **kwargs)
    specs = [(main, probe[k - 1].area_length() - main if k and segments[k - 1] is None else 0) for k in range(len(segments))]
    frames = reservoir_chain(specs, ancillary=b"\x5a", **kwargs)
    stream = bytearray(b"".join(frame.area for frame in frames))
    position = 0
    for k, (frame, data) in enumerate(zip(frames, segments)):
        at = position - specs[k][1] + main
        if data:
            following = len(stream) if k + 1 == len(frames) else position + frame.area_length() - specs[k + 1][1]
            assert at + len(data) <= following, "the bytes do not fit the ancillary segment"
            stream[at : at + len(data)] = data
        position += frame.area_length()
    position = 0
    for frame in frames:
        frame.area[:] = stream[position : position + frame.area_length()]
        position += frame.area_length()
    return frames


def signatures(count, first, second):
    """Two-byte segment starts that all keep the bits first and second and vary in the others."""
    return [bytes([first | (k * 37 + 5) & 0xFF, second | (k * 91 + 17) & 0xFF]) for k in range(count)]


MP3PRO = dict(bitrate=64, rate=22050, version="2")
# mp3guessenc's guess for constant-bitrate frames without other evidence.
OLD_XING = "Xing (old)"
SURROUND = dict(bitrate=128, rate=44100)


def ofl_block(delay, length, rest=b""):
    return b"\xb4" + delay.to_bytes(2, "big") + length.to_bytes(4, "big") + rest


def enhancement_fixture(name, description, segments, guess, encoder=None, ofl=None, **kwargs):
    f = Layer3Fixture(name, description)
    f.add_frames(enhanced_chain(segments, **kwargs))
    f.extra = {"detected_encoder": encoder, "mp3guessenc_guess": guess, "mp3guessenc_ofl": ofl}
    return f


@fixture
def mp3pro_signature():
    return enhancement_fixture(
        "mp3pro-signature",
        "Twelve MPEG-2 Layer III frames at 22050 Hz whose ancillary data starts with the mp3PRO signature: every first "
        "byte keeps the bits 0xC0, every second byte the bit 0x08.",
        signatures(12, 0xC0, 0x08), "Fraunhofer IIS mp3PRO encoder", {"name": "mp3PRO encoder", "confidence": "Medium"}, **MP3PRO)


@fixture
def mp3pro_signature_broken():
    segments = signatures(12, 0xC0, 0x08)
    segments[6] = bytes([segments[6][0], segments[6][1] & ~0x08 & 0xFF])
    return enhancement_fixture(
        "mp3pro-signature-broken",
        "The mp3PRO signature with the bit 0x08 missing from the second byte of the seventh frame's ancillary data: not "
        "mp3PRO.", segments, OLD_XING, **MP3PRO)


@fixture
def mp3pro_frame_without_ancillary():
    segments = signatures(12, 0xC0, 0x08)
    segments[5] = None
    return enhancement_fixture(
        "mp3pro-frame-without-ancillary",
        "The mp3PRO signature, except that the sixth frame has no ancillary data at all, as an mp3PRO encoder never "
        "writes: not mp3PRO.", segments, OLD_XING, **MP3PRO)


@fixture
def mp3pro_signature_mpeg1():
    return enhancement_fixture(
        "mp3pro-signature-mpeg1",
        "The mp3PRO signature in MPEG-1 frames at 44100 Hz. mp3PRO only exists for MPEG-2 and 2.5, so it is not mp3PRO.",
        signatures(12, 0xC0, 0x08), OLD_XING, **SURROUND)


@fixture
def mp3pro_short_stream():
    return enhancement_fixture(
        "mp3pro-short-stream",
        "Six frames with the mp3PRO signature. mp3guessenc takes them as mp3PRO; the analyzer needs at least eight, so "
        "that the signature's three bits do not match by chance (1 in 8 per frame).",
        signatures(6, 0xC0, 0x08), "Fraunhofer IIS mp3PRO encoder", **MP3PRO)


@fixture
def mp3_surround_signature():
    return enhancement_fixture(
        "mp3-surround-signature",
        "Twelve MPEG-1 Layer III frames at 44100 Hz whose ancillary data starts with the MP3 Surround signature: every "
        "first byte keeps the bits 0xCF, every second byte the bits 0x30.",
        signatures(12, 0xCF, 0x30), "Fraunhofer IIS mp3Surround 5.1 encoder",
        {"name": "Fraunhofer MP3 Surround encoder", "confidence": "Medium"}, **SURROUND)


@fixture
def mp3_surround_signature_32khz():
    return enhancement_fixture(
        "mp3-surround-signature-32khz",
        "The MP3 Surround signature at 32000 Hz, a sample rate MP3 Surround does not use: not MP3 Surround.",
        signatures(12, 0xCF, 0x30), OLD_XING, bitrate=128, rate=32000)


@fixture
def ofl_plain():
    segments = signatures(12, 0, 0)
    segments[0] = ofl_block(1201, 12000, b"\x00\x00\x00")
    return enhancement_fixture(
        "ofl-plain",
        "An Original File Length block at the start of the first frame's ancillary data, as Fraunhofer's encoders write "
        "it for plain MP3: a delay of 1201 samples and 12000 samples of original audio, which the 13824 samples of the "
        "twelve frames hold.",
        segments, OLD_XING, {"name": "Fraunhofer", "confidence": "Medium"}, {"delay": 1201, "length": 12000},
        **SURROUND)


@fixture
def ofl_surround():
    segments = signatures(12, 0xCF, 0x30)
    segments[0] = ofl_block(2176, 11000, b"\x00\x00\x00\xdf\x3c")
    return enhancement_fixture(
        "ofl-surround",
        "MP3 Surround with an Original File Length block: the first frame's ancillary data starts with the block (delay "
        "2176, 11000 samples), and its signature follows at bytes 10 and 11. mp3guessenc adds 576 samples to the delay.",
        segments, "Fraunhofer IIS mp3Surround 5.1 encoder with OFL",
        {"name": "Fraunhofer MP3 Surround encoder", "confidence": "Medium"}, {"delay": 2752, "length": 11000}, **SURROUND)


@fixture
def ofl_exceeds_stream():
    segments = signatures(12, 0, 0)
    segments[0] = ofl_block(1201, 20000, b"\x00\x00\x00")
    return enhancement_fixture(
        "ofl-exceeds-stream",
        "An Original File Length block that claims 20000 samples after a delay of 1201, more than the 13824 samples of "
        "the twelve frames, as when a file is cut after encoding.",
        segments, OLD_XING, {"name": "Fraunhofer", "confidence": "Medium"}, {"delay": 1201, "length": 20000},
        **SURROUND)


# --- Encoder traits ------------------------------------------------------------------------------


def filled_chain(count, scalefac_scale=0, **kwargs):
    """Frames whose main data fills their own data exactly: main_data_begin 0, long blocks, no
    ancillary data, as Shine writes them."""
    probe = reservoir_chain([(100, 0)] * count, **kwargs)
    frames = reservoir_chain([(frame.area_length(), 0) for frame in probe], **kwargs)
    for frame in frames:
        for row in frame.granules:
            row[0].scalefac_scale = scalefac_scale
    return pack(frames)


@fixture
def shine_traits():
    f = Layer3Fixture(
        "shine-traits",
        "Twenty MPEG-1 frames whose main data fills each frame without the bit reservoir, short blocks, scalefactor "
        "scaling or ancillary data: Shine's traits in the fewest frames the analyzer guesses from.",
    )
    f.add_frames(filled_chain(20))
    f.extra = {"detected_encoder": {"name": "Shine", "confidence": "Low"}}
    return f


@fixture
def shine_traits_short():
    f = Layer3Fixture(
        "shine-traits-short",
        "Nineteen frames with Shine's traits: too few to guess an encoder from.",
    )
    f.add_frames(filled_chain(19))
    f.extra = {"detected_encoder": None}
    return f


@fixture
def mpeg2_scalefactor_scaling():
    f = Layer3Fixture(
        "mpeg2-scalefactor-scaling",
        "Twenty-four MPEG-2 frames that use scalefactor scaling and fill their data. MPEG-2 has no scfsi, so its absence "
        "points to no encoder, and scaling rules out Shine.",
    )
    f.add_frames(filled_chain(24, scalefac_scale=1, bitrate=64, rate=22050, version="2"))
    f.extra = {"detected_encoder": None}
    return f


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
