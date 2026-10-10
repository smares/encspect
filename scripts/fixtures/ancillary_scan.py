"""What the analyzer finds in the ancillary data of a Layer III region: mp3PRO, MP3 Surround and an
Original File Length (OFL) block, computed from the region's ancillary segments.

This is the second implementation the analyzer is checked against; mp3guessenc 0.27.6, whose rules
it follows, confirms both (crosscheck_mpeg_fixtures.py). The definitions:

- A frame's ancillary segment is what follows its main data in the bit reservoir's byte stream,
  rounded up to a whole byte, until the next frame's main data starts; the last frame's runs to
  the end of its data. A segment is skipped (None) where the next frame's main data starts before
  the stream, overlaps this frame's main data, or this frame's main data does not fit; a frame
  holding a VBR header is not part of the scan.
- mp3PRO and MP3 Surround encoders start every segment with two signature bytes: the AND of the
  first two bytes of all segments must keep the bits 0xC0 and 0x08 for mp3PRO, in MPEG-2 and 2.5,
  and 0xCF and 0x30 for MP3 Surround, in MPEG-1 at 44100 or 48000 Hz. An empty segment clears both.
- The first segment of an MPEG-1 stream may instead hold an OFL block: 0xB4, a 16-bit delay and a
  32-bit original length in samples, recognized as mp3guessenc does by its second byte, the delay's
  high byte, being 0x04 (plain MP3) or 0x08 (MP3 Surround, whose signature then follows at bytes
  10 and 11). The delay counts the samples before the audio in a plain decoder's output, its own
  529 included; mp3guessenc adds 576 for MP3 Surround, the analyzer reports the value as stored.
- Unlike mp3guessenc, the analyzer requires at least MIN_SEGMENTS scanned segments, so a short
  stream does not match a signature by chance (1 in 8 per frame for mp3PRO's three bits).
"""

MIN_SEGMENTS = 8

MP3PRO = "mp3PRO"
SURROUND = "MP3 Surround"


def ofl(segment, version):
    """The OFL block at the start of a stream's first segment: (delay, original length, surround)."""
    if version != "MPEG-1" or segment is None or len(segment) <= 9 or segment[0] != 0xB4 or segment[1] not in (0x04, 0x08):
        return None
    return int.from_bytes(segment[1:3], "big"), int.from_bytes(segment[3:7], "big"), segment[1] == 0x08


def scan(segments, version, rate):
    """segments: each audio frame's ancillary segment in stream order (bytes, or None when skipped).
    Returns the enhancement (MP3PRO, SURROUND or None), the scanned bytes, and the OFL block."""
    signature = [0xFF, 0xFF]
    scanned = 0
    total = 0
    block = ofl(segments[0], version) if segments else None
    for k, segment in enumerate(segments):
        if segment is None:
            continue
        scanned += 1
        total += len(segment)
        if not segment:
            signature = [0, 0]
        elif k == 0 and block is not None:
            if block[2]:
                for i in (0, 1):
                    if len(segment) > 10 + i:
                        signature[i] &= segment[10 + i]
        else:
            for i in (0, 1):
                if len(segment) > i:
                    signature[i] &= segment[i]
    enhancement = None
    if scanned >= MIN_SEGMENTS and total > 0:
        if version != "MPEG-1" and signature[0] & 0xC0 == 0xC0 and signature[1] & 0x08 == 0x08:
            enhancement = MP3PRO
        elif version == "MPEG-1" and rate in (44100, 48000) and signature[0] & 0xCF == 0xCF and signature[1] & 0x30 == 0x30:
            enhancement = SURROUND
    return enhancement, total, block
