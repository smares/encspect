#!/usr/bin/env python3
"""Writes the hand-built Vorbis streams that once broke the walk or the decoder (the independent review of the Vorbis decoder), into tests/fixtures/fuzz as .bin files that
FuzzRegressionTests analyzes within 20 s each and crosscheck_vorbis_pcm.py also compares with libvorbis:

- vorbis-residue-book-without-dimensions.bin: a residue pass whose book has lookup type 2 and 0 dimensions (the walk divided by zero);
- vorbis-two-mappings-different-muxes.bin: two mappings that mux the two channels differently, with a residue of type 2 (the decoder took the first mapping's bundles);
- vorbis-partitions-without-books-255-channels.bin: one class word naming 65,535 partitions in 255 channels and eight passes whose book reads nothing (the walk's cost was not
  bounded by the packet's bytes);
- vorbis-setup-states-millions-of-entries-chained.bin: 64 chained streams whose 218-byte setup headers each state a codebook of 4,194,302 entries (the tables cost 0.37 s each).

    make_vorbis_stress_fixtures.py [--check]
"""

import pathlib
import random
import sys

import ogg_model
import vorbis_opus_model as v

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "fuzz"


def pages(serial, packets, granules, cuts):
    return b"".join(ogg_model.page(p.serial, p.sequence, p.granule, p.flags, p.lacing, p.body)[0] for p in ogg_model.pages_for(serial, packets, granules, cuts))


def dimensionless():
    def books(w):
        w.put(1, 8)
        v.codebook(w, 1, [1, 1], lookup=(1, 0.0, 1.0, 1, 0, [0, 1]))
        v.codebook(w, 0, [1, 1], lookup=(2, 0.0, 1.0, 1, 0, []))

    def residues(w):
        w.put(0, 6)
        w.put(1, 16)
        w.put(0, 24); w.put(64, 24); w.put(15, 24)
        w.put(0, 6); w.put(0, 8)
        w.put(1, 3); w.put(0, 1)
        w.put(1, 8)

    setup = v.vorbis_setup(1, books=books, residues=residues)
    audio = []
    for _ in range(8):
        w = v.BitWriter(); w.put(0, 1); w.put(0, 1)
        w.put(1, 1); w.put(100, 8); w.put(100, 8)
        audio.append(w.bytes() + bytes([0x55]) * 8)
    packets = [v.vorbis_identification(1, 44100, (8, 11), (0, 0, 0)), v.vorbis_comment_header(b"test", []), setup] + audio
    return pages(7, packets, [0, 0, 0] + [128 * i for i in range(len(audio))], [1, 2, len(audio)])


def muxes():
    def books(w):
        w.put(1, 8)
        v.codebook(w, 1, [1, 1], lookup=(1, 0.0, 1.0, 1, 0, [0, 1]))
        v.codebook(w, 1, [1, 1], lookup=(1, 0.0, 1.0, 1, 0, [0, 1]))

    def residues(w):
        w.put(0, 6)
        w.put(2, 16)
        w.put(0, 24); w.put(2048, 24); w.put(15, 24)
        w.put(0, 6); w.put(0, 8)
        w.put(1, 3); w.put(0, 1)
        w.put(1, 8)

    def mappings(w):
        w.put(1, 6)
        w.put(0, 16); w.put(0, 1); w.put(0, 1); w.put(0, 2)
        w.put(0, 8); w.put(0, 8); w.put(0, 8)
        w.put(0, 16); w.put(1, 1); w.put(1, 4); w.put(0, 1); w.put(0, 2)
        w.put(0, 4); w.put(1, 4)
        for _ in range(2):
            w.put(0, 8); w.put(0, 8); w.put(0, 8)

    setup = v.vorbis_setup(2, modes=((0, 1), (1, 0)), books=books, residues=residues, mappings=mappings)
    rnd = random.Random(1)
    seq = [0, 1, 1, 0, 0, 1, 1, 1] * 4
    audio = []
    for i, m in enumerate(seq):
        w = v.BitWriter(); w.put(0, 1); w.put(m, 1)
        if m == 1:
            w.put(seq[i - 1] if i else 1, 1); w.put(seq[i + 1] if i + 1 < len(seq) else 1, 1)
        for _ in range(2):
            w.put(1, 1); w.put(200, 8); w.put(200, 8)
        audio.append(w.bytes() + bytes(rnd.randrange(256) for _ in range(300)))
    packets = [v.vorbis_identification(2, 44100, (8, 11), (0, 0, 0)), v.vorbis_comment_header(b"test", []), setup] + audio
    return pages(7, packets, [0, 0, 0] + [-1] * len(audio), [1, 2, len(audio)])


def partitions(channels=255, count=300):
    end = 8192 * channels // 2

    def books(w):
        w.put(1, 8)
        v.codebook(w, 65535, [1], lookup=(1, 0.0, 1.0, 1, 0, [0]))
        v.codebook(w, 1, [0, 0], lookup=(1, 0.0, 1.0, 1, 0, [0, 1]), sparse=True)

    def residues(w):
        w.put(0, 6)
        w.put(2, 16)
        w.put(0, 24); w.put(end, 24); w.put(0, 24)
        w.put(0, 6); w.put(0, 8)
        w.put(7, 3); w.put(1, 1); w.put(31, 5)
        for _ in range(8):
            w.put(1, 8)

    setup = v.vorbis_setup(channels, modes=((0, 0), (1, 0)), books=books, residues=residues)
    w = v.BitWriter(); w.put(0, 1); w.put(1, 1); w.put(1, 1); w.put(1, 1)
    w.put(1, 1); w.put(100, 8); w.put(100, 8)
    for _ in range(channels - 1):
        w.put(0, 1)
    for _ in range(20):
        w.put(0, 1)
    packets = [v.vorbis_identification(channels, 44100, (8, 13), (0, 0, 0)), v.vorbis_comment_header(b"test", []), setup] + [w.bytes()] * count
    return pages(7, packets, [0, 0, 0] + [-1] * count, [1, 2] + [100] * (count // 100 + 1))


def millions(links=64):
    entries = (1 << 22) - 2

    def books(w):
        w.put(1, 8)
        v.codebook(w, 1, [1, 1], lookup=(1, 0.0, 1.0, 1, 0, [0, 1]))
        v.codebook(w, 1, [21, 21] + [22] * (entries - 2), ordered=True)

    setup = v.vorbis_setup(1, books=books)
    packets = [v.vorbis_identification(1, 44100, (8, 11), (0, 0, 0)), v.vorbis_comment_header(b"test", []), setup, bytes([0]), bytes([0])]
    return b"".join(pages(serial, packets, [0, 0, 0, 0, 128], [1, 2, 2]) for serial in range(7, 7 + links))


FILES = {
    "vorbis-residue-book-without-dimensions.bin": dimensionless,
    "vorbis-two-mappings-different-muxes.bin": muxes,
    "vorbis-partitions-without-books-255-channels.bin": partitions,
    "vorbis-setup-states-millions-of-entries-chained.bin": millions,
}


def main(argv):
    check = argv[1:] == ["--check"]
    for name, build in FILES.items():
        data = build()
        path = OUT / name
        if check:
            if not path.exists() or path.read_bytes() != data:
                print(f"{name} differs from what the script writes")
                return 1
        else:
            path.write_bytes(data)
            print(f"{name}: {len(data)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
