#!/usr/bin/env python3
"""Writes the hand-built Dolby TrueHD and MLP fixtures in tests/fixtures/truehd (built-*): one fault each, in a stream cut from an encoder-made
fixture (make_external_truehd_fixtures.py), with the checks that the fault leaves alone recomputed so that only it shows.

Each manifest states what truehd_model.py (apart from the analyzer) reads from the file and what the analyzer must report: the units, the
checks that differ, the findings, the verification without and with decoding, and, where FFmpeg decodes the file as the analyzer must, the
MD5 of the decoded audio. crosscheck_truehd_fixtures.py confirms them with FFmpeg and MediaInfo where the tools read the file.

    python3 scripts/fixtures/make_truehd_fixtures.py [--manifests-only] [name ...]
"""

import hashlib
import json
import pathlib
import subprocess
import sys

import make_external_truehd_fixtures as ext
import truehd_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "truehd"
BUILDERS = {}


class Base:
    """The units of an encoder-made fixture, from the first, to be edited: bytearrays and the samples each holds."""

    def __init__(self, name, units, skip=0):
        path = next(p for p in OUT.glob(f"external-{name}.*") if p.suffix in (".thd", ".mlp"))
        data = path.read_bytes()
        framing = m.split_units(data)
        manifest = json.loads(path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json").read_text(encoding="utf-8"))
        n = manifest["sampling"]["unit_samples"]
        counts = [n] * (len(framing) - 1) + [manifest["decoded_samples"] - n * (len(framing) - 1)]
        self.name = name
        self.extension = path.suffix
        self.units = [bytearray(data[o:o + l]) for o, l in framing][skip:skip + units]
        self.counts = counts[skip:skip + units]
        self.unit_samples = n
        self.count = m.Unit(data, framing[0][0], framing[0][1]).sync.count
        self.is_mlp = path.suffix == ".mlp"


class Built:
    """A fixture under construction: pieces ("unit", bytes), ("cut", bytes), ("junk", bytes), the samples of each unit, and what the fault means."""

    def __init__(self, base, description):
        self.base = base
        self.pieces = [["unit", u] for u in base.units]
        self.counts = list(base.counts)
        self.description = description
        self.unreadable = {}
        self.lossless_failures = set()
        self.findings = []
        self.decode_findings = []
        self.ffmpeg = False
        self.extension = base.extension
        self.base.count = base.count

    def unit(self, index):
        return self.pieces[index][1]

    def refresh(self, index, **options):
        m.refresh(self.pieces[index][1], count=self.base.count, is_mlp=False, **options)


def builder(name):
    def register(function):
        BUILDERS[name] = function
        return function
    return register


def substream(unit, k, count):
    parsed = m.Unit(bytes(unit), 0, len(unit), count)
    return parsed.substreams[k]["start"], parsed.substreams[k]["end"]


def finding(code, contains):
    return {"code": code, "contains": contains}


@builder("sync-crc-mismatch")
def sync_crc_mismatch():
    b = Built(Base("truehd-stereo-48k-24", 80), "The CRC of the major sync of unit 32 differs: the unit cannot be read, and the stream waits for the next major sync, 15 units on.")
    b.unit(32)[4 + 28 - 1] ^= 1
    b.refresh(32, sync_crc_too=False)
    b.findings = [finding("truehd.major-sync-crc-mismatch", "is computed over it"), finding("truehd.units-after-damage", "follow damage")]
    return b


@builder("parity-mismatch")
def parity_mismatch():
    b = Built(Base("truehd-stereo-48k-24", 80), "The check nibble of unit 20 is changed, so the parity of its header and directory fails: the unit cannot be read and the next 11 wait for the next major sync.")
    b.unit(20)[0] ^= 0x10
    b.findings = [finding("truehd.parity-mismatch", "does not come out right"), finding("truehd.units-after-damage", "follow damage")]
    return b


@builder("substream-crc-mismatch")
def substream_crc_mismatch():
    b = Built(Base("truehd-stereo-48k-24", 80), "The CRC-8 of unit 10's substream is changed: the unit is read and decodes, the stream is not verified.")
    start, end = substream(b.unit(10), 0, 1)
    b.unit(10)[end - 1] ^= 1
    b.findings = [finding("truehd.substream-crc-mismatch", "differs from the one computed")]
    b.ffmpeg = True
    return b


@builder("substream-parity-mismatch")
def substream_parity_mismatch():
    b = Built(Base("truehd-stereo-48k-24", 80), "The parity byte of unit 11's substream is changed; its CRC-8 does not cover it and holds.")
    start, end = substream(b.unit(11), 0, 1)
    b.unit(11)[end - 2] ^= 0x10
    b.findings = [finding("truehd.substream-parity-mismatch", "0xA9")]
    b.ffmpeg = True
    return b


@builder("restart-crc-mismatch")
def restart_crc_mismatch():
    b = Built(Base("truehd-stereo-48k-24", 80), "The CRC-8 of the restart header in unit 16 differs; the header's values are intact, and the substream's own checks are recomputed.")
    u = b.unit(16)
    start, end = substream(u, 0, 1)
    body = bytearray(u[start:end])
    header = m.restart_header(body, 2)
    m.set_bits(body, header["bit"] + header["bits"], 8, header["crc_computed"] ^ 0x55)
    u[start:end] = body
    b.refresh(16)
    b.findings = [finding("truehd.restart-header-crc-mismatch", "is computed over it")]
    b.ffmpeg = True
    return b


@builder("lossless-check-mismatch")
def lossless_check_mismatch():
    b = Built(Base("truehd-stereo-48k-24", 80), "The lossless check in the restart header of unit 32 is changed, with its CRC-8 and the substream's checks recomputed, so that only the lossless check of the interval before it fails.")
    u = b.unit(32)
    start, end = substream(u, 0, 1)
    body = bytearray(u[start:end])
    header = m.restart_header(body, 2)
    m.set_bits(body, header["bit"] + 89, 8, header["lossless"] ^ 0x5A)
    m.set_restart_crc(body, header)
    u[start:end] = body
    b.refresh(32)
    b.lossless_failures = {(32, 0)}
    b.decode_findings = ["truehd.lossless-check-failed"]
    return b


@builder("audio-bit-flipped")
def audio_bit_flipped():
    b = Built(Base("truehd-stereo-48k-24", 80), "One bit of the coded residual of unit 20 is flipped and the unit's checks are recomputed, so every coded check holds and only the lossless check at the restart header of unit 32 shows it.")
    u = b.unit(20)
    start, end = substream(u, 0, 1)
    # 14 bits before the end of the blocks: FFmpeg's decoder reads the file and reports nothing but the lossless check (found by trying bits from the end back).
    position = (end - 2) * 8 - 1 - 14
    u[position >> 3] ^= 0x80 >> (position & 7)
    b.refresh(20)
    b.lossless_failures = {(32, 0)}
    b.decode_findings = ["truehd.lossless-check-failed"]
    return b


@builder("unit-cut")
def unit_cut():
    b = Built(Base("truehd-stereo-48k-24", 80), "The file ends in the middle of its last unit.")
    last = b.pieces[-1][1]
    b.pieces[-1] = ["cut", last[:len(last) // 2]]
    b.findings = [finding("truehd.unit-cut", "ends inside the access unit")]
    b.unreadable_last = True
    return b


@builder("directory-end-past-unit")
def directory_end_past_unit():
    b = Built(Base("truehd-stereo-48k-24", 80), "The directory of unit 40 puts the end of its substream 100 words past the unit: the unit cannot be read and the next seven wait for the next major sync.")
    u = b.unit(40)
    parsed = m.Unit(bytes(u), 0, len(u), 1)
    at = parsed.dir_start
    word = int.from_bytes(u[at:at + 2], "big")
    u[at:at + 2] = ((word & 0xF000) | min(0xFFF, (word & 0xFFF) + 100)).to_bytes(2, "big")
    b.refresh(40, substreams=False)
    b.findings = [finding("truehd.directory-invalid", "ends past the end of the unit"), finding("truehd.units-after-damage", "follow damage")]
    return b


@builder("data-skipped")
def data_skipped():
    b = Built(Base("truehd-stereo-48k-24", 80), "37 bytes of junk lie between units 20 and 21: the stream is found again at the next major sync, in unit 32.")
    junk = bytes((0xA5 ^ (7 * i)) & 0xFF for i in range(37))
    # The junk and the units up to the next major sync are one stretch the analyzer skips.
    stretch = junk + b"".join(bytes(p[1]) for p in b.pieces[21:32])
    b.pieces[21:32] = [["junk", stretch]]
    b.counts[21:32] = [None]
    b.findings = [finding("truehd.data-skipped", "no access unit that checks out")]
    return b


@builder("leading-units")
def leading_units():
    b = Built(Base("truehd-stereo-48k-24", 72, skip=8), "The stream starts eight units before its first major sync, as a stream cut out of a longer one does.")
    b.findings = [finding("truehd.units-before-major-sync", "before the first major sync")]
    b.ffmpeg = True
    return b


@builder("signature-unexpected")
def signature_unexpected():
    b = Built(Base("truehd-stereo-48k-24", 48), "The signature field of the first major sync is 0xB753, with the CRC recomputed.")
    u = b.unit(0)
    u[4 + 8 + 1] = 0x53
    b.refresh(0)
    b.findings = [finding("truehd.unexpected-value", "signature")]
    b.ffmpeg = True
    return b


@builder("parameters-change")
def parameters_change():
    first = Base("truehd-stereo-48k-24", 40)
    b = Built(first, "A second stream, 96 kHz, follows the first one: its major sync changes the sampling rate, which decoding does not follow.")
    other = Base("truehd-stereo-96k-24", 40)
    b.pieces += [["unit", u] for u in other.units]
    b.counts += other.counts
    b.findings = [finding("truehd.stream-parameters-change", "changes the stream's format")]
    b.parameters_change_from = 40
    b.decode_findings = ["decode.frame-invalid"]
    return b


@builder("tiny-truehd-stereo")
def tiny_truehd_stereo():
    b = Built(Base("truehd-stereo-48k-24", 12), "Twelve units of a stereo stream, a major sync and eleven units, for a golden report that lists every node.")
    b.golden_full = True
    b.ffmpeg = True
    return b


@builder("tiny-truehd-51")
def tiny_truehd_51():
    b = Built(Base("truehd-51-48k-24-cholesky", 12), "Twelve units of a 5.1 stream of two substreams, for a golden report that lists every node.")
    b.golden_full = True
    b.ffmpeg = True
    return b


@builder("tiny-mlp-stereo")
def tiny_mlp_stereo():
    b = Built(Base("mlp-stereo-48k-24", 12), "Twelve units of an MLP stream, for a golden report that lists every node.")
    b.golden_full = True
    b.ffmpeg = True
    return b


@builder("mlp-substream-crc-mismatch")
def mlp_substream_crc_mismatch():
    b = Built(Base("mlp-stereo-48k-24", 64), "The CRC-8 of unit 9's substream is changed in an MLP stream: the unit is read and decodes, the stream is not verified.")
    start, end = substream(b.unit(9), 0, 1)
    b.unit(9)[end - 1] ^= 1
    b.findings = [finding("truehd.substream-crc-mismatch", "differs from the one computed")]
    b.ffmpeg = True
    return b


@builder("mlp-sync-crc-mismatch")
def mlp_sync_crc_mismatch():
    b = Built(Base("mlp-stereo-48k-24", 64), "The CRC of the major sync of unit 16 differs in an MLP stream.")
    b.unit(16)[4 + 28 - 1] ^= 1
    b.findings = [finding("truehd.major-sync-crc-mismatch", "is computed over it"), finding("truehd.units-after-damage", "follow damage")]
    return b


def with_substreams(units, base_count, add, atmos):
    """Rewrites units of a two-substream stream with one or two more substreams: a copy of the second (the third) and, for Atmos, a fourth of
    other bytes; the major sync states the count, and with atmos, bit 7 of its substream info."""
    out = []
    for unit in units:
        parsed = m.Unit(bytes(unit), 0, len(unit), base_count)
        sync_length = parsed.sync.length if parsed.sync else 0
        entries = [dict(e) for e in parsed.directory]
        bodies = [bytes(unit[s["start"]:s["end"]]) for s in parsed.substreams]
        bodies.append(bodies[1])
        entries.append(dict(entries[1]))
        if add == 2:
            fourth = bytes((0x3C ^ (i * 29)) & 0xFF for i in range(40))
            bodies.append(fourth)
            entries.append({"extra": False, "non_restart": entries[0]["non_restart"], "check": True, "reserved": False, "end": 0})
        new = bytearray(unit[:4 + sync_length])
        if parsed.sync:
            new[4 + 16] = (len(bodies) << 4) | (new[4 + 16] & 0x0F)
            if atmos:
                new[4 + 17] |= 0x80
        end_words = 0
        directory = bytearray()
        for e, body in zip(entries, bodies):
            end_words += len(body) // 2
            word = (0x8000 if e["extra"] else 0) | (0x4000 if e["non_restart"] else 0) | (0x2000 if e["check"] else 0) | (0x1000 if e["reserved"] else 0) | end_words
            directory += word.to_bytes(2, "big")
            if e["extra"]:
                directory += e["extra_word"].to_bytes(2, "big")
        new += directory
        for body in bodies:
            new += body
        length = len(new) // 2
        new[0] = (new[0] & 0xF0) | (length >> 8)
        new[1] = length & 0xFF
        m.refresh(new, count=len(bodies))
        out.append(new)
    return out


@builder("three-substreams")
def three_substreams():
    base = Base("truehd-51-48k-24-cholesky", 48)
    b = Built(base, "A 5.1 stream with a third substream, a copy of the second: the highest substream the analyzer reads outputs the presentation, and every presentation has its lossless check.")
    b.pieces = [["unit", u] for u in with_substreams(base.units, 2, 1, False)]
    b.base.count = 3
    b.ffmpeg = True
    return b


@builder("atmos-signaled")
def atmos_signaled():
    base = Base("truehd-51-48k-24-cholesky", 48)
    b = Built(base, "A 5.1 stream with a copy of the second substream as the third and a fourth of other bytes, and bit 7 of the substream info set: what a stream with Dolby Atmos signals. The first three substreams are read, the fourth is only checked against the directory.")
    b.pieces = [["unit", u] for u in with_substreams(base.units, 2, 2, True)]
    b.base.count = 4
    b.findings = [finding("truehd.atmos-signaled", "Dolby Atmos")]
    b.ffmpeg = True
    return b


@builder("substream-without-restart-header")
def substream_without_restart_header():
    base = Base("truehd-51-48k-24-cholesky", 48)
    b = Built(base, "The first block of unit 16's second substream does not state a restart header (its second bit is cleared), though the unit has a major sync: the substream cannot be read.")
    u = b.unit(16)
    start, end = substream(u, 1, 2)
    u[start] &= ~0x40 & 0xFF
    b.refresh(16)
    b.unreadable = {16: 1}
    b.findings = [finding("truehd.substream-without-restart-header", "no restart header"), finding("truehd.units-after-damage", "follow damage")]
    return b


@builder("restart-sync-invalid")
def restart_sync_invalid():
    base = Base("truehd-51-48k-24-cholesky", 48)
    b = Built(base, "The sync of the restart header in unit 16's first substream is damaged (a bit of 0x18F5 flipped), with the substream's checks recomputed: the substream cannot be read.")
    u = b.unit(16)
    start, end = substream(u, 0, 2)
    u[start + 1] ^= 0x04
    b.refresh(16)
    b.unreadable = {16: 0}
    b.findings = [finding("truehd.restart-header-invalid", "does not begin with its sync"), finding("truehd.units-after-damage", "follow damage")]
    return b


@builder("block-size-invalid")
def block_size_invalid():
    base = Base("truehd-stereo-48k-24", 48)
    b = Built(base, "The block size in the first decoding parameters of unit 32 is 0, with the substream's checks recomputed: the substream cannot be read.")
    u = b.unit(32)
    start, end = substream(u, 0, 1)
    body = bytearray(u[start:end])
    header = m.restart_header(body, 2)
    at = header["bit"] + header["bits"] + 8
    # The presence flags' present bit, then the block size's present bit and its nine bits.
    assert (body[at >> 3] >> (7 - (at & 7))) & 1 == 0
    m.set_bits(body, at + 2, 9, 0)
    u[start:end] = body
    b.refresh(32)
    b.unreadable = {32: 0}
    b.findings = [finding("truehd.parameters-invalid", "block size"), finding("truehd.units-after-damage", "follow damage")]
    return b


# --- units written again with bits inserted --------------------------------------------------------------------------------------------------

CLI = ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect"


def block_map(units, extension):
    """Where the blocks of each substream-0 unit lie, read from the analyzer's tree of the units (the CLI built in Release): per unit, the blocks as
    (start bit, end bit of the decoding parameters, end bit of the block's last flag), all counted from the substream's first bit."""
    import tempfile
    with tempfile.TemporaryDirectory() as folder:
        path = pathlib.Path(folder) / f"units{extension}"
        path.write_bytes(b"".join(bytes(u) for u in units))
        report = json.loads(subprocess.run([str(CLI), "--json", str(path)], capture_output=True, check=True, text=True).stdout)[0]

    def bit(node):
        return node["offset"] * 8 + (node.get("bitOffset") or 0)

    def end(node):
        return bit(node) + (node["bitLength"] if "bitLength" in node else node["length"] * 8)

    def find(node, name):
        return [c for c in node.get("children", []) if c["name"] == name or c["name"].startswith(name)]

    stream = report["root"]["children"][0]
    result = []
    for unit in find(stream, "Access Unit"):
        substream = find(unit, "Substream 0")[0]
        origin = bit(substream)
        blocks = []
        for block in find(substream, "Block "):
            parameters = find(block, "Decoding Parameters")[0]
            blocks.append((bit(block) - origin, end(parameters) - origin, end(block) - origin))
        result.append((blocks, [c["name"] for c in block.get("children", [])]))
    return result


def bits_of_unit(data, start, stop):
    return [(data[i >> 3] >> (7 - (i & 7))) & 1 for i in range(start, stop)]


def rewrite_substream(unit, count, blocks, inserts, replaces=()):
    """The unit (a bytearray) with bits inserted into its first substream and its lengths, padding, parity byte, CRC-8 and parity nibble worked
    out again. inserts maps a bit of the substream to the bits (a list) inserted before it; replaces maps a bit to a bit that replaces it."""
    parsed = m.Unit(bytes(unit), 0, len(unit), count)
    sub = parsed.substreams[0]
    body = bits_of_unit(unit[sub["start"]:sub["end"]], 0, (sub["end"] - sub["start"]) * 8)
    content_end = blocks[-1][2]
    tail = body[content_end:]
    assert not any(tail[:(-content_end) % 16 or 0]), "padding bits are used"
    padding = (-content_end) % 16
    assert len(tail) == padding + 16, "the unit has an end of stream marker"
    bits = body[:content_end]
    for at, value in dict(replaces).items():
        bits[at] = value
    out = []
    for i, value in enumerate(bits):
        out += inserts.get(i, [])
        out.append(value)
    out += [0] * ((-len(out)) % 16)
    data = bytearray(int("".join(map(str, out[i:i + 8])), 2) for i in range(0, len(out), 8))
    data += b"\0\0"
    sync_length = parsed.sync.length if parsed.sync else 0
    head = bytearray(unit[:parsed.dir_end])
    end_words = len(data) // 2
    word = int.from_bytes(head[parsed.dir_start:parsed.dir_start + 2], "big")
    head[parsed.dir_start:parsed.dir_start + 2] = ((word & 0xF000) | end_words).to_bytes(2, "big")
    result = head + data
    words = len(result) // 2
    result[0] = (result[0] & 0xF0) | (words >> 8)
    result[1] = words & 0xFF
    m.refresh(result, count=count)
    return result


@builder("block-size-check")
def block_size_check():
    base = Base("truehd-stereo-48k-24", 3)
    b = Built(base, "The restart header of the first unit states the block size check, so every block carries the 16-bit size field before its residuals and the check byte after them: the three units are written again that way, and their blocks are read.")
    layout = block_map(base.units, base.extension)
    units = []
    for index, unit in enumerate(base.units):
        blocks, _ = layout[index]
        inserts = {}
        replaces = []
        for start, parameters_end, block_end in blocks:
            data_end = block_end - 1
            inserts[parameters_end] = [int(c) for c in format(16 + data_end - parameters_end, "016b")]
            inserts[data_end] = [0] * 8
        if index == 0:
            parsed = m.Unit(bytes(unit), 0, len(unit), 1)
            sub = parsed.substreams[0]
            header = sub["restart"]
            replaces = [(header["bit"] + 88, 1)]
        new = rewrite_substream(unit, 1, blocks, inserts, replaces)
        if index == 0:
            parsed = m.Unit(bytes(new), 0, len(new), 1)
            sub = parsed.substreams[0]
            body = bytearray(new[sub["start"]:sub["end"]])
            m.set_restart_crc(body, m.restart_header(body, 2))
            new[sub["start"]:sub["end"]] = body
            m.refresh(new, count=1)
        units.append(new)
    b.pieces = [["unit", u] for u in units]
    b.findings = [finding("truehd.block-size-check", "carries the check data")]
    return b


@builder("parameters-change-within-unit")
def parameters_change_within_unit():
    base = Base("truehd-stereo-48k-24", 3)
    b = Built(base, "The second block of the first unit states output shifts, which the first block's parameters do not: the unit changes them within itself.")
    blocks, _ = block_map(base.units, base.extension)[0]
    assert len(blocks) == 2
    start, parameters_end, block_end = blocks[1]
    unit = base.units[0]
    parsed = m.Unit(bytes(unit), 0, len(unit), 1)
    sub = parsed.substreams[0]
    body = bits_of_unit(unit[sub["start"]:sub["end"]], 0, (sub["end"] - sub["start"]) * 8)
    # The parameters begin with the presence flags' present bit, the block size's present bit and its nine bits, then the matrices' present bit and
    # the output shifts' present bit.
    at = start + 2
    assert body[at] == 0 and body[at + 1] == 1, "block size present"
    at += 11
    assert body[at] == 0 and body[at + 1] == 0, "no matrices, no output shifts"
    channels = sub["restart"]["matrix_last"] + 1
    new = rewrite_substream(unit, 1, blocks, {at + 2: [0, 0, 0, 1] * channels}, [(at + 1, 1)])
    b.pieces = [["unit", new]] + [["unit", u] for u in base.units[1:]]
    b.findings = [finding("truehd.parameters-change-within-unit", "changes the matrices")]
    return b


# --- the manifests --------------------------------------------------------------------------------------------------------------------------

def framing_of(pieces):
    data = bytearray()
    framing = []
    for kind, piece in pieces:
        framing.append(("unit" if kind in ("unit", "cut") else "skipped", len(data), len(piece)) + ((True,) if kind == "cut" else ()))
        data += piece
    return bytes(data), framing


def manifest_of(b, path, data, framing, previous):
    """The manifest of a built fixture: what the model reads and the analyzer must report."""
    result = m.manifest(data, framing, b.counts, b.unreadable, b.lossless_failures, getattr(b, "parameters_change_from", None))
    manifest = {"description": b.description, **result, "findings": b.findings, "decode_findings": b.decode_findings, "regions": [{"name": result["format"], "offset": 0, "length": len(data)}],
                "crosscheck": {"ffmpeg": b.ffmpeg}}
    if not getattr(b, "golden_full", False):
        manifest["golden_hides"] = ["frames"]
    if b.ffmpeg:
        if previous is not None and "decoded_pcm_md5" in previous and previous.get("length") == len(data):
            manifest["decoded_pcm_md5"] = previous["decoded_pcm_md5"]
        else:
            values, channels, ff_layout = ext.ffmpeg_pcm(path)
            info = result["stream_infos"][0]
            samples, digest = ext.canonical(values, channels, ff_layout, info["layout"], info["bits_per_sample"])
            assert samples == result["decoded_samples"], f"{path.name}: the model counts {result['decoded_samples']} samples, FFmpeg {samples}"
            manifest["decoded_pcm_md5"] = digest
    return manifest


def main():
    only_manifests = "--manifests-only" in sys.argv
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    count = 0
    for name, function in BUILDERS.items():
        if chosen and name not in chosen:
            continue
        b = function()
        data, framing = framing_of(b.pieces)
        path = OUT / f"built-{name}{b.extension}"
        manifest_path = ext.manifest_path(path)
        previous = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else None
        if not only_manifests:
            path.write_bytes(data)
        ext.write_manifest(path, manifest_of(b, path, data, framing, previous))
        count += 1
    print(f"Wrote {count} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
