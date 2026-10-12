---
id: vorbis-setup-header
title: The Vorbis setup header
related: vorbis-blocks, square-polar-coupling, huffman-coding, vorbis-comments
references: Vorbis I specification, sections 3.2.1, 4.2.4, 6.2.1, 7.2.2 and 8.6.1
---

**Why it exists.** Most codecs fix their tables in the specification. Vorbis instead sends nearly all of its decoder configuration in the third header packet, so an encoder can tune it without changing the format. A decoder must read this header before it can decode any audio packet.

**What it holds,** packed least significant bit first:

- **Codebooks:** Huffman codes, given by each entry's codeword length, some with a table that turns an entry into a vector of values.
- **Floors:** how a packet codes each channel's spectral envelope, the coarse curve the spectrum follows: type 0 as line spectral pairs, type 1 as a piecewise linear curve on a list of positions.
- **Residues:** how the detail left after the floor is coded, by vector quantization in up to eight passes over partitions of the spectrum; type 2 interleaves the channels into one vector.
- **Mappings:** which channels are coupled, by [square polar coupling](square-polar-coupling), and which floor and residue each channel uses.
- **Modes:** the block size and mapping a packet may choose.

**Checks.** The specification and libvorbis refuse a setup header whose codebooks have codeword lengths that do not form a complete prefix code, that names codebooks, floors or residues it does not have, or that sets reserved fields.

**What you see.** Each codebook's dimensions, entries and codeword lengths, each floor, residue, mapping and mode with its key figures, every field at its bits, and findings for what decoders refuse.
