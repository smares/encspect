"""An independent reading of the samples WAV, Wave64 and AIFF store as they are, for the manifests' decoded_pcm_md5 and their cross-check.

Written from the specifications of the sample layouts (Microsoft's WAVEFORMATEX documentation for PCM and IEEE float, the AIFF-C draft and Apple's
QuickTime File Format Specification for the types of AIFF-C), apart from the analyzer; the companded codings are the G.711 segment formulas, checked against FFmpeg. What the analyzer's decoder
"pcm" outputs is the canonical layout of docs/design/decoders.md ("The PCM path"): the samples interleaved, signed, little-endian, ceil(bits / 8) bytes each
(floats as their IEEE 754 bytes), 8-bit unsigned samples minus 128, and A-law and mu-law expanded to 16 bits.

    decode(kind, width, channels, big, body)   {"samples", "md5", "bits", "format"} of the whole sample frames of body, or {"not_possible": reason}

kind is "signed", "unsigned", "float", "alaw" or "mulaw"; width is the bytes of one sample.
"""

import hashlib
import struct


def alaw(code):
    """The 16-bit value of an A-law byte: the even bits of the byte are inverted, then a sign, a 3-bit segment and a 4-bit step (the segment formulas of
    G.711, in the scale of 16-bit samples that FFmpeg's decoder outputs, which built-alaw-all-codes.wav checks for all 256 codes)."""
    a = code ^ 0x55
    step = (a & 0x0F) << 4
    segment = (a >> 4) & 7
    if segment == 0:
        magnitude = step + 8
    else:
        magnitude = (step + 0x108) << (segment - 1)
    return magnitude if a & 0x80 else -magnitude


def mulaw(code):
    """The 16-bit value of a mu-law byte: all bits are inverted, then a sign, a 3-bit segment and a 4-bit step, with a bias of 33 in 14 bits (the segment
    formulas of G.711, in the scale of 16-bit samples that FFmpeg's decoder outputs, which built-mulaw-all-codes.wav checks for all 256 codes)."""
    u = ~code & 0xFF
    magnitude = (((u & 0x0F) << 3) + 0x84) << ((u >> 4) & 7)
    return 0x84 - magnitude if u & 0x80 else magnitude - 0x84


def decode(kind, width, channels, big, body):
    frame = width * channels
    frames = len(body) // frame
    body = bytes(body[:frames * frame])
    if kind in ("signed", "unsigned"):
        if width > 4:
            return {"not_possible": f"integer samples stored in {width * 8} bits are wider than 32 bits"}
        out = bytearray(len(body))
        for k in range(width):
            out[k::width] = body[width - 1 - k::width] if big else body[k::width]
        if kind == "unsigned":
            # The sample minus 2^(bits - 1): the top bit of the most significant byte, which is the last of the little-endian bytes, flipped.
            top = bytes(b ^ 0x80 for b in out[width - 1::width])
            out[width - 1::width] = top
        return {"samples": frames, "md5": hashlib.md5(out).hexdigest(), "bits": width * 8, "format": "integer"}
    if kind == "float":
        out = bytearray(len(body))
        for k in range(width):
            out[k::width] = body[width - 1 - k::width] if big else body[k::width]
        return {"samples": frames, "md5": hashlib.md5(out).hexdigest(), "bits": width * 8, "format": "float"}
    table = [alaw(c) if kind == "alaw" else mulaw(c) for c in range(256)]
    out = struct.pack(f"<{len(body)}h", *[table[b] for b in body])
    return {"samples": frames, "md5": hashlib.md5(out).hexdigest(), "bits": 16, "format": "integer"}
