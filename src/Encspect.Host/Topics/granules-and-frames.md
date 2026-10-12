---
id: granules-and-frames
title: Frames and granules
related: mpeg-layers, filter-bank, frame-header, bit-reservoir
references: ISO/IEC 11172-3 2.4.1.7, 2.4.2.7; ISO/IEC 13818-3
---

**Frames.** MPEG audio is cut into frames, each a fixed number of samples: 384 in Layer I, 1152 in Layers II and III, and 576 in Layer III at the lower sample rates of MPEG-2 and 2.5. At 44.1 kHz a Layer III frame lasts about 26 milliseconds. Every frame has its own header, so a decoder can start at any frame.

**Granules.** Layer III works on granules of 576 samples per channel. An MPEG-1 frame holds two granules, an MPEG-2 or 2.5 frame one. For each granule and channel, the side information says how its 576 frequency lines are coded: how many bits they take, which window was used, which Huffman tables, and the gain. Splitting the frame in two lets the encoder choose differently for each half, and lets granule 1 reuse granule 0's scalefactors where they hardly change ([scfsi](scfsi)).

**Side information and main data.** A Layer III frame carries the side information right after its header (and CRC), in 17 or 32 bytes in MPEG-1 and 9 or 17 in MPEG-2. The coded audio itself, the main data, is not tied to the frame's bytes: it may start in earlier frames, through the [bit reservoir](bit-reservoir). So a frame in the tree shows its header, its side information, its own main data, and possibly bytes that belong to later frames.
