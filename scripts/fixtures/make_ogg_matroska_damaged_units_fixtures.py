#!/usr/bin/env python3
"""Writes the fixtures of tests/fixtures/ogg/damaged-units, flac/damaged-units and matroska/damaged-units: encoder-written files with the stored CRC of one
Ogg page, or of one Matroska Cluster, changed, so that the packets it covers fail the container's check but their data is intact and still decodes
(docs/design/damaged-units.md, decision 2):

- external-oggenc-q10-page-crc-changed.ogg: external-oggenc-q10.ogg (Vorbis) with the CRC of its fourth page changed;
- external-libopus-stereo-hybrid-20ms-repaged-page-crc-changed.opus: external-libopus-stereo-hybrid-20ms.opus (Opus) with its packets written again at 8 to a page
  (encoder-written files hold two or three audio pages, so one failing page would cover most of the stream) and the CRC of its fifth page changed;
- external-speexenc-wb-repaged-page-crc-changed.spx: external-speexenc-wb.spx (Speex), written again at 8 packets to a page, with the CRC of its fifth page changed;
- flac/damaged-units/external-ogg-24-stereo-spanning-page-crc-changed.ogg: flac/external-ogg-24-stereo-spanning.ogg (FLAC in Ogg) with the CRC of its fourth page changed,
  a page whose last frame goes on in the next page (a packet that spans a failing and a good page);
- external-ffmpeg-small-clusters-cluster-crc-changed.mka: external-ffmpeg-small-clusters.mka (Opus in Matroska) with the CRC-32 of its eighth Cluster changed.

A page written again keeps the packets, the serial number and the first two pages; the granule position of each is the position the packets before it add up to, from the
one the source states for its first audio page, and the last page keeps the source's, which holds the trimming at the end.
"""

import pathlib
import struct

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "fixtures"


def ogg_pages(data):
    """The offsets of the pages: (offset, length)."""
    pages = []
    at = 0
    while at + 27 <= len(data) and data[at:at + 4] == b"OggS":
        segments = data[at + 26]
        length = 27 + segments + sum(data[at + 27:at + 27 + segments])
        pages.append((at, length))
        at += length
    assert at == len(data)
    return pages


def ogg_crc_table():
    table = []
    for i in range(256):
        value = i << 24
        for _ in range(8):
            value = ((value << 1) ^ 0x04C11DB7) if value & 0x80000000 else value << 1
            value &= 0xFFFFFFFF
        table.append(value)
    return table


CRC_TABLE = ogg_crc_table()


def ogg_crc(page):
    value = 0
    for byte in page:
        value = ((value << 8) & 0xFFFFFFFF) ^ CRC_TABLE[(value >> 24) ^ byte]
    return value


def ogg_packets(data):
    """The packets of a single-stream file with the page each ends on, and the pages' granule positions: ([(bytes, page)], [granule])."""
    packets = []
    granules = []
    partial = b""
    for index, (offset, length) in enumerate(ogg_pages(data)):
        segments = data[offset + 26]
        table = data[offset + 27:offset + 27 + segments]
        body = offset + 27 + segments
        granules.append(struct.unpack("<q", data[offset + 6:offset + 14])[0])
        for size in table:
            partial += data[body:body + size]
            body += size
            if size < 255:
                packets.append((partial, index))
                partial = b""
    assert not partial
    return packets, granules


def ogg_page(serial, sequence, flags, granule, packets):
    table = bytearray()
    for packet in packets:
        table += b"\xff" * (len(packet) // 255) + bytes([len(packet) % 255])
    assert len(table) <= 255
    page = bytearray(b"OggS\x00" + bytes([flags]) + struct.pack("<qIII", granule, serial, sequence, 0) + bytes([len(table)]) + table + b"".join(packets))
    page[22:26] = struct.pack("<I", ogg_crc(page))
    return bytes(page)


def opus_samples(packet):
    toc = packet[0]
    config = toc >> 3
    frame = [10, 20, 40, 60, 10, 20, 40, 60, 10, 20, 40, 60, 10, 20, 10, 20, 2.5, 5, 10, 20, 2.5, 5, 10, 20, 2.5, 5, 10, 20, 2.5, 5, 10, 20][config]
    code = toc & 3
    count = 1 if code == 0 else 2 if code in (1, 2) else packet[1] & 0x3F
    return int(frame * 48 * count)


def speex_samples(header):
    (frame_size,) = struct.unpack("<i", header[56:60])
    (frames_per_packet,) = struct.unpack("<i", header[64:68])
    return frame_size * frames_per_packet


def ogg_repage(data, headers, per_page, samples_of):
    """The file again with `headers` packets in the first pages, then `per_page` packets to a page."""
    packets, granules = ogg_packets(data)
    pages = ogg_pages(data)
    serial = struct.unpack("<I", data[14:18])[0]
    audio = packets[headers:]
    durations = [samples_of(p, [h for h, _ in packets[:headers]]) for p, _ in audio]
    # The granule position of the source's first audio page fixes where the count starts.
    first_page = audio[0][1]
    start = granules[first_page] - sum(d for d, (_, page) in zip(durations, audio) if page == first_page)
    output = bytearray()
    sequence = 0
    for offset, length in pages[:first_page]:
        output += data[offset:offset + length]
        sequence += 1
    for first in range(0, len(audio), per_page):
        chunk = audio[first:first + per_page]
        is_last = first + per_page >= len(audio)
        granule = granules[-1] if is_last else start + sum(durations[:first + len(chunk)])
        output += ogg_page(serial, sequence, 4 if is_last else 0, granule, [p for p, _ in chunk])
        sequence += 1
    return bytes(output)


def write(directory, name, data):
    target = FIXTURES / directory
    target.mkdir(exist_ok=True)
    (target / name).write_bytes(data)


def ogg_change(data, page):
    """The file with the stored CRC of page `page` changed."""
    data = bytearray(data)
    offset, _ = ogg_pages(data)[page]
    data[offset + 22] ^= 0x55
    return bytes(data)


def vint(data, at):
    """The EBML variable-size integer at `at`: (length in bytes, value with the marker bit removed)."""
    first = data[at]
    length = 1
    while length <= 8 and not first & (0x80 >> (length - 1)):
        length += 1
    value = first & ((0x80 >> (length - 1)) - 1)
    for k in range(1, length):
        value = (value << 8) | data[at + k]
    return length, value


def matroska_change(data, cluster):
    """The file with the stored CRC-32 of Cluster `cluster` changed."""
    data = bytearray(data)
    seen = -1
    at = 0
    while True:
        at = data.find(b"\x1f\x43\xb6\x75", at)
        assert at >= 0
        size_length, _ = vint(data, at + 4)
        body = at + 4 + size_length
        assert data[body] == 0xBF and data[body + 1] == 0x84, "the Cluster's first child is not a CRC-32 element"
        seen += 1
        if seen == cluster:
            data[body + 2] ^= 0x55
            break
        at += 4
    return bytes(data)


def main():
    def read(name):
        return (FIXTURES / name).read_bytes()

    write("ogg/damaged-units", "external-oggenc-q10-page-crc-changed.ogg", ogg_change(read("ogg/external-oggenc-q10.ogg"), 3))
    opus = ogg_repage(read("ogg/external-libopus-stereo-hybrid-20ms.opus"), 2, 8, lambda packet, headers: opus_samples(packet))
    write("ogg/damaged-units", "external-libopus-stereo-hybrid-20ms-repaged-page-crc-changed.opus", ogg_change(opus, 4))
    speex = ogg_repage(read("ogg/external-speexenc-wb.spx"), 2, 8, lambda packet, headers: speex_samples(headers[0]))
    write("ogg/damaged-units", "external-speexenc-wb-repaged-page-crc-changed.spx", ogg_change(speex, 4))
    write("flac/damaged-units", "external-ogg-24-stereo-spanning-page-crc-changed.ogg", ogg_change(read("flac/external-ogg-24-stereo-spanning.ogg"), 3))
    write("matroska/damaged-units", "external-ffmpeg-small-clusters-cluster-crc-changed.mka", matroska_change(read("matroska/external-ffmpeg-small-clusters.mka"), 7))


if __name__ == "__main__":
    main()
