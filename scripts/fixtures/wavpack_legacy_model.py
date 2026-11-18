"""What a WavPack file of version 1 to 3 holds, worked out from its bytes with docs/design/wavpack-legacy.md ("The format"), apart from the analyzer: the
manifests make_external_wavpack_legacy_fixtures.py writes. Nothing here reads a decoder's output or runs a tool; the sources it follows are libwavpack 5.6.0's
unpack3_open.c and unpack3.h (BSD) and 3.97's wavpack.c.

A file is the source WAV file's header (every chunk up to and including the data chunk's 8-byte header), the WavPack header `wvpk`, one bitstream and, where the
encoder kept bytes that followed the audio, a trailer of 16 bytes of 0xFF and those bytes as chunks. A raw file of version 3 (-r1, -r2) starts with the WavPack
header. The header is 10 bytes in version 1, 12 in version 2 and 36 in version 3. Without a decoder the end of the bitstream is the start of the trailer when
one chains to the end of the file (a run of 16 bytes of 0xFF that chunk headers follow to the last byte), else the end of the file.
"""

import re
import struct

FLAG_NAMES = [(0x0001, "mono"), (0x0002, "fast"), (0x0004, "raw"), (0x0008, "calc-noise"), (0x0010, "high"), (0x0020, "bytes-3"), (0x0040, "over-20"),
              (0x0080, "wvc"), (0x0100, "lossy-shape"), (0x0200, "very-fast"), (0x0400, "new-high"), (0x0800, "cancel-extreme"), (0x1000, "cross-decorr"),
              (0x2000, "new-decorr"), (0x4000, "joint-stereo"), (0x8000, "extreme-decorr")]
NOT_STORED = 0x0008 | 0x0080 | 0x0200
HEADER_SIZE = {1: 10, 2: 12, 3: 36}
MARKER = b"\xff" * 16
MAX_CHUNK = 1 << 20
RAW_RATE = 44100
RAW_BITS = 16


def chunks_before_data(data):
    """The RIFF header and chunks of a stored WAV header: (riff size, [(id, offset, size)], offset of the WavPack header, the data chunk's size), or None when
    the file does not start with a RIFF/WAVE header whose chunks reach a data chunk."""
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        return None
    p, found = 12, []
    while p + 8 <= len(data):
        ident = data[p:p + 4].decode("latin-1")
        size = struct.unpack_from("<I", data, p + 4)[0]
        found.append((ident, p, size))
        if ident == "data":
            return struct.unpack_from("<I", data, 4)[0], found, p + 8, size
        p += 8 + size + (size & 1)
    return None


def trailer(data, start):
    """(offset, [(id, size)]) of the first run of 16 bytes of 0xFF after start that chunk headers follow to the last byte of the file, else None."""
    p = data.find(MARKER, start)
    while p >= 0:
        q, found = p + 16, []
        while q + 8 <= len(data):
            ident = data[q:q + 4]
            size = struct.unpack_from("<I", data, q + 4)[0]
            if not all(32 <= b < 127 for b in ident) or q + 8 + size > len(data):
                break
            found.append((ident.decode("latin-1"), size))
            q += 8 + size + (size & 1)
            if q >= len(data) or (q == len(data) + 1 and size & 1):
                break
        if found and q >= len(data):
            return p, found
        p = data.find(MARKER, p + 1)
    return None


def libwavpack_expectation(version, channels, mono, raw, flags, bits, samples):
    """What libwavpack 5.6.0 built with its legacy decoder answers for the file (unpack3_open.c, unpack3.c): the files it opens and the samples it unpacks. A raw
    file starts with `w`, not the `R` of RIFF, and goes to the reader of version 4; a version 1 mono file opens, and its samples are not unpacked (unpack3.c
    decodes mono from version 2); a file the flag kludge refuses does not open."""
    if raw:
        return {"opens": False}
    refused = version == 3 and (flags & 0x8000) and ((flags & NOT_STORED) or (bits and (flags & 0x0400) and (flags & (0x0002 | 0x0010 | 0x1000))))
    if refused:
        return {"opens": False}
    return {"opens": True, "version": version, "channels": 1 if (mono or channels == 1) else 2, "samples_unpacked": 0 if (version == 1 and channels == 1) else samples}


def effective_flags(version, flags):
    """The flags as the decoder takes them after the kludge in unpack3_open.c: with the extreme flag "cancel extreme" clears both, without it the cross-channel flag is
    cleared; versions 1 and 2 store none."""
    if version != 3:
        return 0
    if not flags & 0x8000:
        return flags & ~0x1000
    return flags & ~(0x8000 | 0x0800) if flags & 0x0800 else flags


def compression_mode(version, flags, bits):
    """The speed mode the flags show, by libwavpack's rule (unpack3_open.c): without a bit limit and the high flag a file is fast, the fast flag makes it very fast,
    the high flag normal and with the extreme decorrelator high; a hybrid file is high with the extreme decorrelator and normal otherwise."""
    f = effective_flags(version, flags)
    if version == 3 and bits and f & 0x0400:
        return "high" if f & 0x8000 else "normal"
    if f & 0x0002:
        return "very fast"
    return ("high" if f & 0x8000 else "normal") if f & 0x0010 else "fast"


def stereo_coding(version, flags, bits, mono):
    """How the two channels are coded, from the branch of unpack3.c the flags select: sum and difference in versions 1 and 2 and in the lossless fast mode, left and
    right in the lossy and hybrid modes unless the high flag, joint stereo and no fast flag, adaptive in the other lossless modes without cross-channel decorrelation,
    and then joint stereo says mid/side."""
    if mono:
        return None
    if version != 3:
        return "Mid/side stereo"
    f = effective_flags(version, flags)
    if bits:
        return "Mid/side stereo" if not f & 0x0002 and f & (0x0010 | 0x0400) and f & 0x4000 else "Left/right stereo"
    if f & 0x0002:
        return "Mid/side stereo"
    if not f & 0x0010 or not f & 0x1000:
        return "Adaptive stereo"
    return "Mid/side stereo" if f & 0x4000 else "Left/right stereo"


def wav_findings(wav, version):
    """The cases of wavpack.legacy-wav-format-unsupported: what libwavpack's open refuses in the stored WAV header (unpack3_open.c)."""
    found = []
    fmts = [c for c in wav["chunks"] if c["id"] == "fmt "]
    if not fmts:
        found.append("no format")
    elif fmts[-1]["size"] < 16:
        found.append("short")
    fmt = wav.get("format")
    if any(c["size"] > MAX_CHUNK for c in wav["chunks"] if c["id"] != "data"):
        found.append("large chunk")
    if fmt:
        channels, bits, align = fmt["channels"], fmt["bits_per_sample"], fmt["block_align"]
        if fmt["tag"] != 1:
            found.append("format tag")
        if channels == 0 or channels > 2:
            found.append("channels")
        if fmt["sample_rate"] == 0:
            found.append("sample rate")
        if bits < 16 or bits > 24:
            found.append("bits")
        if channels in (1, 2) and (align // channels > 3 or align % channels or align // channels < (bits + 7) // 8):
            found.append("block align")
    return found


def findings(header, flags, wav, raw, from_data):
    """The findings the analyzer is expected to raise (code and severity), worked out from the header: the design's "Findings"."""
    found = []
    if header.get("cut"):
        return [("warning", "wavpack.legacy-header-truncated")]
    version = header["version"]
    if version not in (1, 2, 3):
        return [("warning", "wavpack.version-unsupported")]
    if header["ck_size"] != HEADER_SIZE[version] - 8:
        found.append(("info", "wavpack.legacy-header-size"))
    if wav:
        found += [("warning", "wavpack.legacy-wav-format-unsupported")] * len(wav_findings(wav, version))
    bits = header.get("bits", 0)
    if version == 3:
        if from_data is not None and header["total_samples"] != from_data and not raw:
            found.append(("warning", "wavpack.total-samples-mismatch"))
        if flags & 0x8000:
            if flags & NOT_STORED:
                found.append(("warning", "wavpack.flags-invalid"))
            if bits and flags & 0x0400 and flags & (0x0002 | 0x0010):
                found.append(("warning", "wavpack.flags-invalid"))
            if bits and flags & 0x1000:
                found.append(("warning", "wavpack.flags-invalid"))
        fmt = (wav or {}).get("format")
        if fmt and fmt["channels"] in (1, 2) and bool(flags & 1) != (fmt["channels"] == 1):
            found.append(("warning", "wavpack.legacy-channels-mismatch"))
        if raw and header["extra_bc"] > 3:
            found.append(("warning", "wavpack.legacy-extra-bytes-invalid"))
    return found


def manifest(data, extra=None):
    """The manifest of a legacy file: the regions, the WAV header, the WavPack header with its flags, the figures a reader derives, the trailer, the findings the
    analyzer is expected to raise and what libwavpack's legacy decoder is expected to answer (the SHA-256 of its samples and the original decoder's output come from
    `extra`). A file with a damaged header gets what can be read of it and its findings; one that holds no WavPack header at all raises ValueError."""
    result = {"length": len(data)}
    stored = chunks_before_data(data)
    raw = stored is None
    start = 0 if raw else stored[2]
    if data[start:start + 4] != b"wvpk":
        raise ValueError("no WavPack header")
    version = struct.unpack_from("<H", data, start + 8)[0] if len(data) >= start + 10 else -1
    header = {"offset": start}
    size = HEADER_SIZE.get(version, 0)
    if not raw:
        riff_size, found, _, data_size = stored
        fmt = [c for c in found if c[0] == "fmt "]
        fmt = fmt[-1] if fmt else None
        wav = {"riff_size": riff_size, "chunks": [{"id": i, "offset": o, "size": s} for i, o, s in found], "data_size": data_size, "data_offset": start}
        if fmt and fmt[2] >= 16:
            tag, channels, rate, byte_rate, align, wbits = struct.unpack_from("<HHIIHH", data, fmt[1] + 8)
            wav["format"] = {"tag": tag, "channels": channels, "sample_rate": rate, "byte_rate": byte_rate, "block_align": align, "bits_per_sample": wbits}
    else:
        wav, fmt = None, None
    if version not in HEADER_SIZE or len(data) < start + size:
        if wav:
            result["wav"] = wav
        header.update({"length": min(len(data) - start, size or 10), "version": version})
        header["cut"] = len(data) < start + max(size, 10) or (version in HEADER_SIZE and len(data) < start + size)
        header["ck_size"] = struct.unpack_from("<I", data, start + 4)[0] if len(data) >= start + 8 else 0
        result["header"] = header
        result["findings"] = [{"severity": s, "code": c} for s, c in findings(header, 0, None, raw, None)]
        result["stream"] = False
        return result
    ck_size = struct.unpack_from("<I", data, start + 4)[0]
    header.update({"length": size, "ck_size": ck_size, "version": version})
    flags, shift, total, bits, crc, crc2, extension, extra_bc, extras = 0, 0, None, 0, None, None, None, 0, b""
    if version >= 2:
        bits = struct.unpack_from("<H", data, start + 10)[0]
        header["bits"] = bits
    if version == 3:
        flags, shift, total, crc, crc2 = struct.unpack_from("<HHIII", data, start + 12)
        extension = data[start + 28:start + 32].rstrip(b"\0").decode("latin-1")
        extra_bc = data[start + 32]
        extras = data[start + 33:start + 36]
        header.update({"flags": f"{flags:04x}", "flag_names": [name for bit, name in FLAG_NAMES if flags & bit], "shift": shift, "total_samples": total,
                       "crc": f"{crc:08x}", "crc2": f"{crc2:08x}", "extension": extension, "extra_bc": extra_bc, "extras": extras.hex()})
    result["header"] = header
    if wav:
        result["wav"] = wav
    fmt = (wav or {}).get("format")
    mono = bool(flags & 1) if raw or (version == 3 and not fmt) else (fmt["channels"] == 1 if fmt else False)
    if not raw and fmt and version == 3 and fmt["channels"] not in (1, 2):
        mono = bool(flags & 1)
    channels = 1 if mono else 2
    rate = RAW_RATE if raw else (fmt["sample_rate"] if fmt else 0)
    width = 3 if (flags & 0x20) else 2
    per_sample = (fmt["bits_per_sample"] if fmt else 0) if not raw else (24 if flags & 0x20 else RAW_BITS)
    # libwavpack's count of a version 3 file and the one of versions 1 and 2: the data chunk's size over a sample of all channels, 3 bytes over 16 bits.
    from_data = None if raw or not fmt or fmt["channels"] not in (1, 2) else wav["data_size"] // fmt["channels"] // (3 if fmt["bits_per_sample"] > 16 else 2)
    samples = total if version == 3 else (from_data or 0)
    mode = "lossless" if not bits else ("hybrid" if (version == 3 and flags & 0x0400) else "lossy")
    # The old lossy mode (versions 2 and 3 without the new high flag) caps the bits of a sample; the hybrid mode of version 3 targets bits per sample times 256.
    result["figures"] = {"channels": channels, "sample_rate": rate, "bits_per_sample": per_sample, "bytes_per_sample": width, "samples": samples,
                         "mode": mode, "compression_mode": compression_mode(version, flags, bits), "stereo_coding": stereo_coding(version, flags, bits, mono),
                         "samples_from_data_chunk": from_data, "total_samples_match": None if from_data is None or version != 3 else from_data == total}
    end = len(data)
    found = trailer(data, start + size)
    if found:
        end = found[0]
        result["trailer"] = {"offset": found[0], "length": len(data) - found[0], "marker": 16, "chunks": [{"id": i, "size": s} for i, s in found[1]]}
    result["bitstream"] = {"offset": start + size, "length": end - (start + size)}
    expected = findings(header, flags, wav, raw, from_data)
    if not found:
        run = re.search(b"\xff{16}", data[start + size:])
        if run:
            expected.append(("info", "wavpack.legacy-trailer-unrecognized"))
    if end == start + size:
        expected.append(("warning", "wavpack.legacy-bitstream-missing"))
    result["findings"] = [{"severity": s, "code": c} for s, c in sorted(expected, key=lambda f: (f[1], f[0]))]
    result["stream"] = bool(rate and (raw or (fmt and fmt["channels"] in (1, 2))))
    refusals = wav_findings(wav, version) if wav else []
    if "no format" in refusals:
        # libwavpack reads its WAV header structure uninitialized then: no answer is stated.
        result["libwavpack_legacy"] = {}
    elif refusals:
        result["libwavpack_legacy"] = {"opens": False}
    else:
        result["libwavpack_legacy"] = libwavpack_expectation(version, channels, mono, raw, flags, bits, samples if samples is not None else 0)
    if result["libwavpack_legacy"].get("opens") and from_data is not None:
        result["libwavpack_legacy"]["samples_unpacked"] = 0 if (version == 1 and channels == 1) else from_data
    if extra:
        result.update(extra)
    return result
