"""A reader and writer for TTA1 streams, written from the TTA authors' format description (https://tta.sourceforge.net/en/tta-format-description/,
"TTA1 header", the seek table and "TTA1 frame") and from what encoder-written files show, apart from the analyzer. It works out each fixture's
manifest: the header, the seek table, every frame with its CRC-32, the stream info, the verification and the findings.

What the description says, and what the files add:

- The header is the signature "TTA1", then little-endian: the audio format (2 bytes), the channels (2), the bits per sample (2), the sample rate
  (4), the data length in samples (4) and a CRC-32 (4), which covers the 18 bytes before it (encoder-written files: zlib's crc32 of them).
- The seek table follows the header: one 4-byte entry for each frame, then a CRC-32 over the entries. The number of frames is the number of
  samples over the samples in a frame, rounded up. An entry is the length of its frame in bytes with the frame's CRC-32 (the entries add up to
  the length of the data in encoder-written files).
- A frame is its coded bytes and a CRC-32 over them. The "frame time" is 1.04489795918367346939 seconds (= 256/245), so a frame holds that
  many times the sample rate in samples, truncated: the files of FFmpeg 6.1.1 and libtta-c 2.3 at 32,000 Hz (33,436.7 samples) have frames of
  33,436, and libtta-c's at 24,000 Hz (25,077.55) frames of 25,077, which fixes the rounding. Every frame holds as many samples but the last,
  which holds the rest.
- Audio format 1 is what both encoders write; libtta-c 2.3 writes 2 for a stream encoded with a password (the frame CRC-32s of that file are
  those of the stored bytes, like the others), FFmpeg 6.1.1 calls 2 an encrypted stream that needs a password ("Missing password for encrypted
  stream") and refuses 3. The description defines neither.
"""

import struct
import zlib

SIGNATURE = b"TTA1"
HEADER_LENGTH = 22


def crc32(data):
    return zlib.crc32(data) & 0xFFFFFFFF


def frame_length(rate):
    return rate * 256 // 245


def frame_count(rate, samples):
    length = frame_length(rate)
    return 0 if length == 0 else -(-samples // length)


def header(fmt=1, channels=2, bits=16, rate=44100, samples=0, crc=None, signature=SIGNATURE):
    body = signature + struct.pack("<HHHII", fmt, channels, bits, rate, samples)
    return body + struct.pack("<I", crc32(body) if crc is None else crc)


def table(sizes, crc=None):
    body = b"".join(struct.pack("<I", s) for s in sizes)
    return body + struct.pack("<I", crc32(body) if crc is None else crc)


def frame(payload, crc=None):
    return payload + struct.pack("<I", crc32(payload) if crc is None else crc)


def payload(index, length):
    """Deterministic bytes standing for a frame's coded samples; the analyzer does not read them."""
    out = bytearray()
    state = 0x5EED + index * 7919
    for _ in range(length):
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        out.append((state >> 16) & 0xFF)
    return bytes(out)


def stream(rate=44100, channels=2, bits=16, samples=100000, lengths=None, fmt=1):
    """A whole stream: header, seek table and frames whose coded bytes are lengths[i] long (default 200 + 100 * i)."""
    count = frame_count(rate, samples)
    lengths = lengths if lengths is not None else [200 + 100 * i for i in range(count)]
    frames = [frame(payload(i, n)) for i, n in enumerate(lengths)]
    return header(fmt, channels, bits, rate, samples) + table([len(f) for f in frames]) + b"".join(frames)


class Stream:
    """The structure of the TTA stream in data[start:end]."""

    def __init__(self, data, start=0, end=None):
        end = len(data) if end is None else end
        self.data, self.start, self.end = data, start, end
        self.header = None
        self.kind = "none"
        self.table = None
        self.frames = []
        self.stream_end = start
        self.trailing = None
        self.findings = []
        self._read()

    def _read(self):
        d, s, e = self.data, self.start, self.end
        head = d[s:min(e, s + 4)]
        if len(head) < 4 or head not in (b"TTA1", b"TTA2"):
            self.kind = "none"
            self.findings.append(("tta.no-signature", 1))
            return
        if head == b"TTA2":
            self.kind = "v2"
            self.stream_end = e
            self.findings.append(("tta.version-2", 1))
            return
        if e - s < HEADER_LENGTH:
            self.kind = "short"
            self.stream_end = e
            self.findings.append(("tta.header-truncated", 1))
            return
        raw = d[s:s + HEADER_LENGTH]
        fmt, channels, bits, rate, samples, stored = struct.unpack("<HHHIII", raw[4:])
        computed = crc32(raw[:18])
        self.header = {"format": fmt, "channels": channels, "bits": bits, "rate": rate, "samples": samples, "crc": stored, "computed": computed}
        self.kind = "tta1"
        f = self.findings
        if stored != computed:
            f.append(("tta.header-crc-mismatch", 1))
        if fmt == 2:
            f.append(("tta.audio-format-encrypted", 1))
        elif fmt != 1:
            f.append(("tta.audio-format-unknown", 1))
        if channels == 0:
            f.append(("tta.channels-zero", 1))
        if bits not in (8, 16, 24):
            f.append(("tta.bits-per-sample-unusual", 1))
        if rate == 0:
            f.append(("tta.sample-rate-zero", 1))
            self.stream_end = e
            return
        if samples == 0:
            f.append(("tta.no-samples", 1))

        expected = frame_count(rate, samples)
        table_at = s + HEADER_LENGTH
        available = e - table_at
        listed = min(expected, available // 4)
        sizes = list(struct.unpack(f"<{listed}I", d[table_at:table_at + 4 * listed]))
        complete = listed == expected and available >= 4 * expected + 4
        stored_table = struct.unpack("<I", d[table_at + 4 * expected:table_at + 4 * expected + 4])[0] if complete else None
        computed_table = crc32(d[table_at:table_at + 4 * listed])
        self.table = {"offset": table_at, "expected": expected, "entries": sizes, "crc": stored_table, "computed": computed_table, "complete": complete}
        if not complete:
            f.append(("tta.seek-table-truncated", 1))
        elif stored_table != computed_table:
            f.append(("tta.seek-table-crc-mismatch", 1))

        position = table_at + 4 * expected + 4
        crc_failures = too_small = 0
        missing = 0
        for size in sizes:
            entry = {"offset": position, "size": size, "status": []}
            if position >= e:
                entry["status"] = ["missing"]
                missing += 1
            elif size < 4:
                entry["status"] = ["small"]
                too_small += 1
            elif e - position < size:
                entry["status"] = ["cut"]
            else:
                body = d[position:position + size - 4]
                entry["crc"] = struct.unpack("<I", d[position + size - 4:position + size])[0]
                entry["computed"] = crc32(body)
                if entry["crc"] != entry["computed"]:
                    entry["status"] = ["crc"]
                    crc_failures += 1
            self.frames.append(entry)
            position += size
        if crc_failures:
            f.append(("tta.frame-crc-mismatch", crc_failures))
        if any("cut" in x["status"] for x in self.frames):
            f.append(("tta.frame-cut", 1))
        if missing:
            f.append(("tta.frames-missing", 1))
        if too_small:
            f.append(("tta.frame-too-small", too_small))
        self.stream_end = min(max(position, table_at + 4 * expected + 4), e)
        if position < e:
            self.trailing = (position, e - position)
            f.append(("tta.data-after-frames", 1))

    def samples_of(self, index):
        h = self.header
        length = frame_length(h["rate"])
        count = frame_count(h["rate"], h["samples"])
        return length if index < count - 1 else h["samples"] - (count - 1) * length if index == count - 1 else length

    def complete_frames(self):
        return [(i, x) for i, x in enumerate(self.frames) if not x["status"] or x["status"] == ["crc"]]

    def stream_info(self):
        h = self.header
        if h is None:
            return None
        done = self.complete_frames()
        samples = sum(self.samples_of(i) for i, _ in done) if h["rate"] else 0
        duration = samples / h["rate"] if h["rate"] else 0
        bits_total = sum(x["size"] * 8 for _, x in done)
        average = bits_total * h["rate"] / samples / 1000 if samples else None
        return {"codec": "TTA", "sample_rate": h["rate"], "channels": h["channels"], "samples": samples, "duration": duration, "bits_per_sample": h["bits"],
                "average_bitrate": average, "layout": []}

    def verification(self):
        h = self.header
        if h is None:
            return None
        checksums = [{"name": "header CRC-32", "covers": "header", "coversAllAudio": False, "stored": 1, "checked": 1, "failed": int(h["crc"] != h["computed"])}]
        if self.table and self.table["crc"] is not None:
            checksums.append({"name": "seek table CRC-32", "covers": "header", "coversAllAudio": False, "stored": 1, "checked": 1, "failed": int(self.table["crc"] != self.table["computed"])})
        done = len(self.complete_frames())
        cut = sum("cut" in x["status"] for x in self.frames)
        small = sum("small" in x["status"] for x in self.frames)
        missing = sum("missing" in x["status"] for x in self.frames)
        bad = sum(x["status"] == ["crc"] for x in self.frames)
        covers_all = h["crc"] == h["computed"] and h["samples"] > 0 and h["rate"] > 0 and done == frame_count(h["rate"], h["samples"]) and not cut and not small and not missing
        checksums.append({"name": "frame CRC-32", "covers": "codedData", "coversAllAudio": covers_all, "stored": done + cut, "checked": done, "failed": bad})
        failed = any(c["failed"] for c in checksums)
        unchecked = any(c["stored"] != c["checked"] for c in checksums)
        verified = False if failed else None if unchecked else True if any(c["coversAllAudio"] and c["checked"] > 0 for c in checksums) else None
        return {"isVerified": verified, "decoding": "noDecoder", "checksums": checksums}


def manifest(data, start=0, end=None, tags=(), extra=None):
    """The expected result for the stream in data[start:end]; tags are (name, offset, length) of the tags around it that the host owns."""
    s = Stream(data, start, end)
    regions = []
    if s.kind == "none":
        regions.append({"name": "Data", "offset": start, "length": s.end - start})
    else:
        regions.append({"name": "TTA", "offset": start, "length": s.stream_end - start})
        if s.trailing:
            regions.append({"name": "Trailing Data", "offset": s.trailing[0], "length": s.trailing[1]})
    for name, offset, length in tags:
        regions.append({"name": name, "offset": offset, "length": length})
    regions.sort(key=lambda r: r["offset"])
    info = s.stream_info()
    verification = s.verification()
    h = s.header
    result = {
        "length": len(data),
        "regions": regions,
        "header": None if h is None else {"format": h["format"], "channels": h["channels"], "bits": h["bits"], "rate": h["rate"], "samples": h["samples"], "crc": f"{h['crc']:08x}",
                                          "crc_ok": h["crc"] == h["computed"]},
        "frame_length": frame_length(h["rate"]) if h else None,
        "table": None if not s.table else {"offset": s.table["offset"], "expected": s.table["expected"], "entries": s.table["entries"],
                                           "crc": None if s.table["crc"] is None else f"{s.table['crc']:08x}", "crc_ok": None if s.table["crc"] is None else s.table["crc"] == s.table["computed"]},
        "frames": [{"offset": x["offset"], "size": x["size"], "status": x["status"], **({"crc": f"{x['crc']:08x}"} if "crc" in x else {})} for x in s.frames],
        "stream_infos": [info] if info else [],
        "verification": {"default": verification} if verification else {},
        "findings": [{"code": c, **({"count": n} if n != 1 else {})} for c, n in s.findings],
    }
    if extra:
        result.update(extra)
    return result
