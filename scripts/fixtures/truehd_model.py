"""An independent reading of Dolby TrueHD and MLP at the level of access units, written from docs/design/truehd-format.md and apart from the
analyzer: the checksums, the major sync, the substream directory and the restart headers, which is what the manifests of the fixtures state,
and the edits that hand-built fixtures make (a changed field with the checks recomputed, or deliberately left broken).

It reads no block: the samples of a unit, the decoded audio and the lossless checks come from FFmpeg's decoder (crosscheck_truehd_fixtures.py
confirms them) or, for a damaged file, from the unit the damage leaves readable.
"""

import struct

RATES = {0: 48000, 1: 96000, 2: 192000, 8: 44100, 9: 88200, 10: 176400}
SYNC_WORD = b"\xf8\x72\x6f"

# TrueHD channel flags: the channels each adds, in the order of the table (docs/design/truehd-format.md, "TrueHD channel flags").
FLAG_CHANNELS = [
    ["front left", "front right"], ["front center"], ["LFE"], ["surround left", "surround right"], ["top front left", "top front right"],
    ["front left of center", "front right of center"], ["back left", "back right"], ["back center"], ["top center"],
    ["discrete", "discrete"], ["front left wide", "front right wide"], ["top front center"], ["LFE 2"],
]

MLP_ARRANGEMENTS = [
    (["front center"], []), (["front left", "front right"], []), (["front left", "front right"], ["back center"]),
    (["front left", "front right"], ["back left", "back right"]), (["front left", "front right"], ["LFE"]),
    (["front left", "front right"], ["LFE", "back center"]), (["front left", "front right"], ["LFE", "back left", "back right"]),
    (["front left", "front right"], ["front center"]), (["front left", "front right"], ["front center", "back center"]),
    (["front left", "front right"], ["front center", "back left", "back right"]), (["front left", "front right"], ["front center", "LFE"]),
    (["front left", "front right"], ["front center", "LFE", "back center"]),
    (["front left", "front right"], ["front center", "LFE", "back left", "back right"]),
    (["front left", "front right", "front center"], ["back center"]), (["front left", "front right", "front center"], ["back left", "back right"]),
    (["front left", "front right", "front center"], ["LFE"]), (["front left", "front right", "front center"], ["LFE", "back center"]),
    (["front left", "front right", "front center"], ["LFE", "back left", "back right"]),
    (["front left", "front right", "back left", "back right"], ["LFE"]), (["front left", "front right", "back left", "back right"], ["front center"]),
    (["front left", "front right", "back left", "back right"], ["front center", "LFE"]),
]


# --- the checks -----------------------------------------------------------------------------------------------------------------------------

def crc_standard(data, polynomial, width, register=0):
    """A standard CRC (most significant bit first, no reflection, no final XOR)."""
    top = 1 << (width - 1)
    mask = (1 << width) - 1
    for byte in data:
        register ^= byte << (width - 8)
        for _ in range(8):
            register = ((register << 1) ^ polynomial) & mask if register & top else (register << 1) & mask
    return register


def remainder_bits(bits, polynomial, width, register=0):
    """The remainder of a message of bits without the zero bits a standard CRC appends: each bit shifts the register left, the generator is
    XORed in where a bit fell out, then the bit is XORed into bit 0 (docs/design/truehd-format.md, "Checksums and check values")."""
    top = 1 << (width - 1)
    mask = (1 << width) - 1
    for bit in bits:
        fell = register & top
        register = (register << 1) & mask
        if fell:
            register ^= polynomial
        register ^= bit
    return register


def bits_of(data, start, count):
    return [(data[(start + i) >> 3] >> (7 - ((start + i) & 7))) & 1 for i in range(count)]


def sync_crc(sync):
    """The major sync's CRC-16 over all of it but its last two bytes."""
    return crc_standard(sync[:-4], 0x2D, 16) ^ int.from_bytes(sync[-4:-2], "big")


def restart_crc(data, start_bit, count):
    return remainder_bits(bits_of(data, start_bit, count), 0x1D, 8)


def substream_crc(body):
    """The CRC-8 of the substream's bytes before the parity byte."""
    return crc_standard(body[:-1], 0x63, 8, 0x3C) ^ body[-1]


def substream_parity_ok(body, parity):
    x = parity
    for b in body:
        x ^= b
    return x == 0xA9


def nibble_ok(header, directory):
    x = 0
    for b in header + directory:
        x ^= b
    return ((x >> 4) ^ (x & 0xF)) == 0xF


def set_nibble(unit, sync_length, directory_length):
    """Rewrites the check nibble so that the parity of the unit header and the directory comes out right."""
    directory = bytes(unit[4 + sync_length:4 + sync_length + directory_length])
    x = 0
    for b in bytes([unit[0] & 0xF]) + bytes(unit[1:4]) + directory:
        x ^= b
    folded = (x >> 4) ^ (x & 0xF)
    unit[0] = (unit[0] & 0xF) | ((folded ^ 0xF) << 4)


# --- reading a unit -------------------------------------------------------------------------------------------------------------------------

def sync_length(unit):
    if unit[4:7] != SYNC_WORD or unit[7] not in (0xBA, 0xBB):
        return 0
    if unit[7] == 0xBA and unit[29] & 1:
        return 30 + 2 * (unit[30] >> 4)
    return 28


class Sync:
    def __init__(self, unit):
        s = bytes(unit[4:4 + sync_length(unit)])
        self.bytes = s
        self.length = len(s)
        self.is_truehd = s[3] == 0xBA
        self.crc_stored = int.from_bytes(s[-2:], "big")
        self.crc_computed = sync_crc(s)
        self.crc_ok = self.crc_stored == self.crc_computed
        self.count = s[16] >> 4
        if self.is_truehd:
            self.rate_code = s[4] >> 4
            self.assign6 = ((s[5] & 0xF) << 1) | (s[6] >> 7)
            self.assign8 = ((s[6] & 0x1F) << 8) | s[7]
            self.bits = 24
            self.arrangement = None
        else:
            self.rate_code = s[5] >> 4
            self.assign6 = self.assign8 = 0
            depth = {0: 16, 1: 20, 2: 24}
            self.bits = depth.get(s[4] >> 4)
            self.arrangement = s[7] & 0x1F
        self.rate = RATES.get(self.rate_code)
        self.unit_samples = 40 << (self.rate_code & 7)
        self.signature = int.from_bytes(s[8:10], "big")
        self.info = s[17]
        self.signals_atmos = self.is_truehd and self.count == 4 and bool(self.info & 0x80)

    def usable(self):
        return self.crc_ok and self.rate is not None and self.count >= 1 and (self.count <= 4 if self.is_truehd else self.count <= 2) and self.bits is not None


class Unit:
    def __init__(self, data, offset, length, count=0, is_mlp=False):
        self.offset = offset
        self.data = bytes(data[offset:offset + length])
        self.length = length
        u = self.data
        self.nibble = u[0] >> 4
        self.timing = int.from_bytes(u[2:4], "big")
        self.sync = Sync(u) if sync_length(u) and len(u) >= 4 + sync_length(u) else None
        use_count = self.sync.count if self.sync else count
        self.directory = []
        self.parity_ok = None
        self.substreams = []
        self.dir_start = 4 + (self.sync.length if self.sync else 0)
        if use_count:
            at = self.dir_start
            mlp = (not self.sync.is_truehd) if self.sync else is_mlp
            for _ in range(use_count):
                word = int.from_bytes(u[at:at + 2], "big")
                entry = {"extra": bool(word & 0x8000), "non_restart": bool(word & 0x4000), "check": bool(word & 0x2000), "reserved": bool(word & 0x1000), "end": word & 0xFFF}
                at += 2
                if entry["extra"]:
                    entry["extra_word"] = int.from_bytes(u[at:at + 2], "big")
                    at += 2
                self.directory.append(entry)
            self.dir_end = at
            self.parity_ok = nibble_ok(u[0:4], u[self.dir_start:at])
            previous = 0
            for entry in self.directory:
                start = self.dir_end + 2 * previous
                stop = self.dir_end + 2 * max(entry["end"], previous)
                previous = max(entry["end"], previous)
                body = u[start:min(stop, len(u))]
                sub = {"start": start, "end": min(stop, len(u)), "check": entry["check"], "parity_ok": None, "crc_ok": None, "restart": None}
                if entry["check"] and len(body) >= 3:
                    sub["parity_ok"] = substream_parity_ok(body[:-2], body[-2])
                    sub["crc_ok"] = substream_crc(body[:-2]) == body[-1]
                if len(body) >= 4 and body[0] & 0xC0 == 0xC0:
                    sub["restart"] = restart_header(body, 2)
                self.substreams.append(sub)

    def nominal_samples(self, sync):
        return sync.unit_samples if sync else 0


def restart_header(body, start):
    """The restart header that begins at bit start of a substream's bytes, or None where it does not begin with its sync."""
    value = lambda first, count: int("".join(map(str, bits_of(body, start + first, count))) or "0", 2)
    if value(0, 13) != 0x18F5:
        return None
    m = value(38, 4)
    count = 113 + 6 * (m + 1)
    if (start + count + 8 + 7) // 8 > len(body):
        return None
    header = {
        "bit": start, "noise_type": value(13, 1), "first": value(30, 4), "last": value(34, 4), "matrix_last": m, "seed": value(46, 23),
        "block_size_check": value(88, 1), "lossless": value(89, 8), "assignments": [value(113 + 6 * i, 6) for i in range(m + 1)],
        "crc_stored": value(count, 8), "crc_computed": restart_crc(body, start, count), "bits": count,
    }
    header["crc_ok"] = header["crc_stored"] == header["crc_computed"]
    return header


def set_bits(data, start, count, value):
    for i in range(count):
        bit = (value >> (count - 1 - i)) & 1
        position = start + i
        mask = 0x80 >> (position & 7)
        if bit:
            data[position >> 3] |= mask
        else:
            data[position >> 3] &= ~mask & 0xFF


# --- speakers -------------------------------------------------------------------------------------------------------------------------------

def truehd_list(assignment):
    names = []
    has_rear = bool(assignment & (1 << 6))
    for flag, channels in enumerate(FLAG_CHANNELS):
        if assignment & (1 << flag):
            if flag == 3:
                names += ["side left", "side right"] if has_rear else ["back left", "back right"]
            else:
                names += channels
    return names


def channel_list(sync, substream, count, channels):
    """The channel list of a substream that outputs the given number of channels: the first candidate of that size."""
    if sync.is_truehd:
        six = truehd_list(sync.assign6) if sync.assign6 else None
        eight = truehd_list(sync.assign8) if sync.assign8 else None
        stereo = ["front left", "front right"]
        if count == 1:
            candidates = [eight, six]
        elif substream == 0:
            candidates = [stereo]
        elif count == 2:
            candidates = [six, eight]
        elif substream == 1:
            candidates = [six]
        else:
            candidates = [eight, six]
    else:
        arrangement = MLP_ARRANGEMENTS[sync.arrangement] if sync.arrangement < len(MLP_ARRANGEMENTS) else None
        if count >= 2 and substream == 0:
            candidates = [["front left", "front right"]]
        else:
            candidates = [arrangement[0] + arrangement[1] if arrangement else None]
    for candidate in candidates:
        if candidate is not None and len(candidate) == channels:
            return candidate
    return None


# --- editing --------------------------------------------------------------------------------------------------------------------------------

def refresh(unit, count=0, is_mlp=False, sync_crc_too=True, parity_too=True, substreams=True, skip=()):
    """Recomputes the checks of an edited unit (a bytearray) in place; skip names the ones to leave as they are
    ('sync', 'nibble', 'parity', 'crc8')."""
    u = unit
    sl = sync_length(u)
    if sl and sync_crc_too and "sync" not in skip:
        crc = sync_crc(bytes(u[4:4 + sl]))
        u[4 + sl - 2:4 + sl] = crc.to_bytes(2, "big")
    parsed = Unit(bytes(u), 0, len(u), count, is_mlp)
    if substreams and parsed.directory:
        for sub, entry in zip(parsed.substreams, parsed.directory):
            if entry["check"] and sub["end"] - sub["start"] >= 3:
                body = bytes(u[sub["start"]:sub["end"] - 2])
                if "parity" not in skip:
                    x = 0
                    for b in body:
                        x ^= b
                    u[sub["end"] - 2] = x ^ 0xA9
                if "crc8" not in skip:
                    u[sub["end"] - 1] = substream_crc(body)
    if parsed.directory and parity_too and "nibble" not in skip:
        set_nibble(u, sl, parsed.dir_end - parsed.dir_start)
    return u


def set_restart_crc(unit_substream, header):
    """Recomputes a restart header's CRC-8 in a substream's bytes (a bytearray)."""
    crc = restart_crc(bytes(unit_substream), header["bit"], header["bits"])
    set_bits(unit_substream, header["bit"] + header["bits"], 8, crc)


def split_units(data, start=0):
    """The (offset, length) of the units that follow one another by their length words from start, as far as the data holds them whole."""
    units = []
    at = start
    while at + 4 <= len(data):
        length = (((data[at] & 0xF) << 8) | data[at + 1]) * 2
        if length < 4 or at + length > len(data):
            break
        units.append((at, length))
        at += length
    return units


# --- the manifest ---------------------------------------------------------------------------------------------------------------------------

def simulate(data, framing, unreadable=(), lossless_failures=(), decode_until=None):
    """The unit-level reading of a stream by the rules of docs/formats/truehd.md, for the manifests: which units are read, which wait for a major
    sync, and the counts the verification states.

    framing: ("unit", offset, length[, cut]) and ("skipped", offset, length), in file order. unreadable: the units (indexes in the framing) with a
    substream that breaks the syntax, and which substream it is (the others are still read), which the fixture's builder knows. lossless_failures: (index, substream) of restart headers whose lossless check the
    builder changed. decode_until: the index from which the decoder refuses units (the stream changes its format). A unit with an end of stream
    marker is not modeled: only the last unit of a file has one."""
    has_synced = False
    synced = False
    count = 0
    is_mlp = False
    open_intervals = {0: False, 1: False, 2: False}
    valid = {0: False, 1: False, 2: False}
    t = {"sync_units": 0, "sync_failures": 0, "parity_checked": 0, "parity_failures": 0, "restart_headers": 0, "restart_failures": 0, "substream_checked": 0,
         "substream_parity_failures": 0, "substream_crc_failures": 0, "without_check": 0, "unreadable": 0, "cut": 0, "skipped": 0, "leading": 0, "after_damage": 0,
         "lossless_stored": {0: 0, 1: 0, 2: 0}, "lossless_checked": {0: 0, 1: 0, 2: 0}, "lossless_failed": {0: 0, 1: 0, 2: 0}, "gap": False,
         "syncs": [], "restarts": [], "failures": [], "no_check_data": [], "channels": {}}
    status = []

    def reset():
        nonlocal synced
        synced = False
        for k in open_intervals:
            open_intervals[k] = False
            valid[k] = False

    for index, entry in enumerate(framing):
        if entry[0] == "skipped":
            t["skipped"] += entry[2]
            if has_synced:
                t["gap"] = True
            reset()
            status.append("skipped")
            continue
        _, offset, length, *rest = entry
        cut = bool(rest and rest[0])
        unit = Unit(data, offset, length, count if has_synced else 0, is_mlp)
        if unit.sync is None and not synced:
            if has_synced:
                t["after_damage"] += 1
                t["gap"] = True
                status.append("after-damage")
            else:
                t["leading"] += 1
                status.append("leading")
            continue
        is_unreadable = False
        if unit.sync is not None:
            t["sync_units"] += 1
            sync = unit.sync
            t["syncs"].append({"unit": index, "crc_ok": sync.crc_ok, "usable": sync.usable(), "rate": sync.rate, "substreams": sync.count, "mlp": not sync.is_truehd,
                               "signature": sync.signature, "info": sync.info, "assign6": sync.assign6, "assign8": sync.assign8, "arrangement": sync.arrangement,
                               "bits": sync.bits})
            if not sync.crc_ok:
                t["sync_failures"] += 1
            if not sync.usable():
                is_unreadable = True
            else:
                count = sync.count
                is_mlp = not sync.is_truehd
                synced = True
                has_synced = True
        if cut:
            t["cut"] += 1
            is_unreadable = True
        elif not is_unreadable and unit.directory:
            t["parity_checked"] += 1
            if not unit.parity_ok:
                t["parity_failures"] += 1
                is_unreadable = True
            previous = 0
            for entry_ in unit.directory:
                if entry_["non_restart"] == (unit.sync is not None) or entry_["end"] < previous or unit.dir_end + 2 * entry_["end"] > unit.length:
                    is_unreadable = True
                previous = max(previous, entry_["end"])
        if not is_unreadable:
            faulty = unreadable.get(index) if isinstance(unreadable, dict) else (0 if index in unreadable else None)
            if faulty is not None:
                is_unreadable = True
            for k, (entry_, sub) in enumerate(zip(unit.directory, unit.substreams)):
                if k >= 3:
                    continue
                if entry_["check"]:
                    t["substream_checked"] += 1
                    if sub["parity_ok"] is False:
                        t["substream_parity_failures"] += 1
                    if sub["crc_ok"] is False:
                        t["substream_crc_failures"] += 1
                else:
                    t["without_check"] += 1
                if sub["restart"] is not None and k != faulty:
                    r = sub["restart"]
                    t["restart_headers"] += 1
                    if not r["crc_ok"]:
                        t["restart_failures"] += 1
                    if open_intervals[k]:
                        t["lossless_stored"][k] += 1
                    open_intervals[k] = True
                    if valid[k] and not is_unreadable and (decode_until is None or index < decode_until):
                        t["lossless_checked"][k] += 1
                        if (index, k) in lossless_failures:
                            t["lossless_failed"][k] += 1
                    valid[k] = True
                    t["channels"].setdefault(k, r["matrix_last"] + 1)
        # What the nodes of a unit that was read show; a unit that was not has the nodes of its header alone, or of its major sync.
        if not is_unreadable:
            t["restarts"] += [{"unit": index, "sub": k, "bit": sub["restart"]["bit"], "crc_ok": sub["restart"]["crc_ok"], "lossless": sub["restart"]["lossless"]}
                              for k, sub in enumerate(unit.substreams) if sub["restart"] is not None and k < 3]
            for k, (entry_, sub) in enumerate(zip(unit.directory, unit.substreams)):
                if not entry_["check"]:
                    t["no_check_data"].append([index, k])
                elif sub["parity_ok"] is False or sub["crc_ok"] is False:
                    t["failures"].append({"unit": index, "sub": k, "parity_ok": sub["parity_ok"], "crc_ok": sub["crc_ok"]})
        if is_unreadable:
            t["unreadable"] += 1
            t["gap"] = True
            reset()
            status.append("cut" if cut else "unreadable")
        else:
            status.append("read")
    t["status"] = status
    return t


def verification(t, decoded):
    """The verification record of a stream: without decoding (the lossless checks not compared) or with it."""
    covers_all = t["substream_checked"] > 0 and t["without_check"] == 0 and not t["gap"] and t["unreadable"] == 0 and t["cut"] == 0 and t["skipped"] == 0
    tallies = [
        ["major sync CRC-16", "header", False, t["sync_units"], t["sync_units"], t["sync_failures"]],
        ["unit parity nibble", "header", False, t["parity_checked"], t["parity_checked"], t["parity_failures"]],
        ["restart header CRC-8", "header", False, t["restart_headers"], t["restart_headers"], t["restart_failures"]],
        ["substream parity byte", "codedData", covers_all, t["substream_checked"], t["substream_checked"], t["substream_parity_failures"]],
        ["substream CRC-8", "codedData", covers_all, t["substream_checked"], t["substream_checked"], t["substream_crc_failures"]],
    ]
    for k in sorted(t["channels"]):
        tallies.append([f"lossless check, substream {k} ({t['channels'][k]}-channel presentation)", "decodedAudio", False, t["lossless_stored"][k],
                        t["lossless_checked"][k] if decoded else 0, t["lossless_failed"][k] if decoded else 0])
    if any(c[5] > 0 for c in tallies):
        verdict = False
    elif any(c[3] != c[4] for c in tallies):
        verdict = None
    elif any(c[2] and c[4] > 0 for c in tallies):
        verdict = True
    else:
        verdict = None
    return {"isVerified": verdict, "decoding": "decoded" if decoded else "notRequested",
            "checksums": [{"name": n, "covers": cov, "coversAllAudio": all_, "stored": stored, "checked": checked, "failed": failed}
                          for n, cov, all_, stored, checked, failed in tallies]}


def stream_info(t, data, framing, counts, average=False):
    """The stream the analyzer reports: from the first usable major sync, the highest of the first three substreams, the units read."""
    sync_entry = next((s for s in t["syncs"] if s["usable"]), None)
    if sync_entry is None or not t["channels"]:
        return None
    first = Unit(data, framing[sync_entry["unit"]][1], framing[sync_entry["unit"]][2])
    top = min(first.sync.count, 3) - 1
    channels = t["channels"][top]
    layout = channel_list(first.sync, top, first.sync.count, channels)
    samples = sum(c for c, s in zip(counts, t["status"]) if s == "read" and c is not None)
    rate = first.sync.rate
    info = {"codec": "MLP" if not first.sync.is_truehd else "Dolby TrueHD", "sample_rate": rate, "channels": channels, "layout": layout or [], "bits_per_sample": first.sync.bits,
            "samples": samples, "duration": samples / rate}
    if average:
        total_bits = sum(f[2] for f in framing[sync_entry["unit"]:]) * 8
        info["average_bitrate"] = total_bits * rate / samples / 1000
    return info


def manifest(data, framing, counts, unreadable=(), lossless_failures=(), decode_until=None, average=False):
    """Everything a manifest states that the model gives, for a file whose entries are framing (see simulate) and whose units hold counts samples."""
    t = simulate(data, framing, unreadable, lossless_failures, decode_until)
    info = stream_info(t, data, framing, counts, average)
    decoded = sum(c for i, (c, s) in enumerate(zip(counts, t["status"])) if s == "read" and c is not None and (decode_until is None or i < decode_until))
    entries = []
    for i, entry in enumerate(framing):
        if entry[0] == "skipped":
            entries.append(["skipped", entry[1], entry[2]])
        else:
            unit = Unit(data, entry[1], entry[2])
            entries.append(["unit", entry[1], entry[2], 1 if unit.sync is not None else 0, t["status"][i]])
    return {
        "length": len(data), "format": info["codec"] if info else "Dolby TrueHD", "entries": entries, "syncs": t["syncs"], "restarts": t["restarts"],
        "failures": t["failures"], "no_check_data": t["no_check_data"], "stream_infos": [info] if info else [], "decoded_samples": decoded,
        "verification": {"default": verification(t, False), "verify": verification(t, True)},
        "counts": {"leading": t["leading"], "after_damage": t["after_damage"], "unreadable": t["unreadable"], "cut": t["cut"], "skipped_bytes": t["skipped"]},
    }
