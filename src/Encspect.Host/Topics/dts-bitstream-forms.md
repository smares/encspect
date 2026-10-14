---
id: dts-bitstream-forms
title: DTS bit stream forms
related: dts-core-frames, frame-sync
references: ETSI TS 102 114 V1.6.1 (2019-08), 5.3; FFmpeg 6.1.1 libavcodec/dca_syncwords.h and dca_core.c; libdca 0.0.7 parse.c
---

**The problem.** A DTS stream may be stored in 16-bit words or in 14-bit words, and in either byte order, so the same frame has four different appearances in a file.

**How it works.** A core stream comes in four forms, told apart by the first bytes of the sync word:

- 16-bit big endian: 7F FE 80 01, the form the specification describes.
- 16-bit little endian: the same bytes with each pair swapped, FE 7F 01 80.
- 14-bit big endian: 1F FF E8 00 07 Fx.
- 14-bit little endian: the same with each pair swapped, FF 1F 00 E8 F0 07.

In the 14-bit forms the 16-bit word holds 14 bits of the stream and repeats bit 13 in its two top bits. A frame of FSIZE + 1 bytes of the stream then takes (FSIZE + 1) x 8 / 14 words in the file, rounded down. The specification describes only the first form; the others are read as FFmpeg's decoder and libdca define them, and both decode frames in every form.

**What you see.** The form in each frame's synchronization, offsets in the file's bytes, and nodes for fields that straddle word boundaries covering the bytes that hold them.
