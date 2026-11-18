"""What a WavPack file of version 1 to 3 holds, worked out from its bytes with docs/design/wavpack-legacy.md ("The format"), apart from the analyzer: the
manifests make_external_wavpack_legacy_fixtures.py writes. Nothing here reads a decoder's output or runs a tool; the sources it follows are libwavpack 5.6.0's
unpack3_open.c and unpack3.h (BSD) and 3.97's wavpack.c.

A file is the source WAV file's header (every chunk up to and including the data chunk's 8-byte header), the WavPack header `wvpk`, one bitstream and, where the
encoder kept bytes that followed the audio, a trailer of 16 bytes of 0xFF and those bytes as chunks. A raw file of version 3 (-r1, -r2) starts with the WavPack
header. The header is 10 bytes in version 1, 12 in version 2 and 36 in version 3. Without a decoder the end of the bitstream is the start of the trailer when
one chains to the end of the file (a run of 16 bytes of 0xFF that chunk headers follow to the last byte), else the end of the file.
"""

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


def manifest(data, extra=None):
    """The manifest of a legacy file: the regions, the WAV header, the WavPack header with its flags, the figures a reader derives, the trailer and what libwavpack's
    legacy decoder is expected to answer (the SHA-256 of its samples and the original decoder's output come from `extra`)."""
    result = {"length": len(data)}
    stored = chunks_before_data(data)
    raw = stored is None
    start = 0 if raw else stored[2]
    if data[start:start + 4] != b"wvpk":
        raise ValueError("no WavPack header")
    ck_size, version = struct.unpack_from("<IH", data, start + 4)
    size = HEADER_SIZE[version]
    header = {"offset": start, "length": size, "ck_size": ck_size, "version": version}
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
    wav = None
    if not raw:
        riff_size, found, _, data_size = stored
        fmt = next((c for c in found if c[0] == "fmt "), None)
        wav = {"riff_size": riff_size, "chunks": [{"id": i, "offset": o, "size": s} for i, o, s in found], "data_size": data_size, "data_offset": start}
        if fmt:
            tag, channels, rate, byte_rate, align, wbits = struct.unpack_from("<HHIIHH", data, fmt[1] + 8)
            wav["format"] = {"tag": tag, "channels": channels, "sample_rate": rate, "byte_rate": byte_rate, "block_align": align, "bits_per_sample": wbits}
        result["wav"] = wav
    mono = bool(flags & 1) if version == 3 else (wav or {}).get("format", {}).get("channels") == 1
    channels = 1 if mono else 2
    rate = RAW_RATE if raw else wav["format"]["sample_rate"]
    width = 3 if (flags & 0x20) else 2
    per_sample = (wav["format"]["bits_per_sample"] if not raw and "format" in wav else RAW_BITS) if not (flags & 0x20 and raw) else 24
    from_wav = None if raw else data_size // (channels * width)
    samples = total if version == 3 else from_wav
    mode = "lossless" if not bits else ("hybrid" if (version == 3 and flags & 0x0400) else "lossy")
    # The old lossy mode (versions 2 and 3 without the new high flag) caps the bits of a sample; the hybrid mode of version 3 targets bits per sample times 256.
    result["figures"] = {"channels": channels, "sample_rate": rate, "bits_per_sample": per_sample, "bytes_per_sample": width, "samples": samples,
                         "mode": mode, "samples_from_data_chunk": from_wav, "total_samples_match": None if from_wav is None or version != 3 else from_wav == total}
    end = len(data)
    found = trailer(data, start + size)
    if found:
        end = found[0]
        result["trailer"] = {"offset": found[0], "length": len(data) - found[0], "marker": 16, "chunks": [{"id": i, "size": s} for i, s in found[1]]}
    result["bitstream"] = {"offset": start + size, "length": end - (start + size)}
    result["libwavpack_legacy"] = libwavpack_expectation(version, channels, mono, raw, flags, bits, samples if samples is not None else 0)
    if "libwavpack_legacy" in result and result["libwavpack_legacy"].get("opens") and from_wav is not None:
        result["libwavpack_legacy"]["samples_unpacked"] = 0 if (version == 1 and channels == 1) else from_wav
    if extra:
        result.update(extra)
    return result
