---
id: dts-extension-substream
title: DTS extension substreams
related: dts-core-frames, dts-core-extensions, dts-channel-arrangements, dts-lossless-extension, dtshd-file, crc, bitrate-modes
references: ETSI TS 102 114 V1.6.1 (2019-08), 7.2 to 7.5, Annex E; FFmpeg 6.1.1 libavcodec/dca_exss.c and dca_parser.c
---

**The problem.** Beyond 1.5 Mbit/s, or in a lossless or low bit rate form, the data no longer fits behind the core, which legacy decoders must keep playing.

**How it works.** DTS-HD adds extension substreams after the core frame, each starting with the sync word 0x64582025 and a header with a CRC. The header lists audio assets; each asset has a descriptor with its sampling frequency, bit depth, channel count and speaker mask, and a mask of its coding components:

- the core, with its sync word remapped to 0x02B09261,
- XBR, extra bits for the residual signal of the core,
- XXCH and X96, the extensions of the core carried here,
- LBR, a low bit rate coding of its own (DTS Express),
- XLL, a lossless coding that reproduces the source bit for bit (DTS-HD Master Audio).

The descriptor's sizes give where each component starts, and each begins with its own sync word. A stream of substreams alone, without a core, is possible.

**What you see.** The substream header, each asset and its components, and the profile: XLL makes DTS-HD Master Audio (with a DTS:X word after its band data, DTS-HD Master Audio + DTS:X), LBR DTS Express, XBR, XXCH or X96 DTS-HD High Resolution Audio (names from FFmpeg, not the specification). The components are checked by their sync words and the headers' CRCs; XLL's channel sets and navigation index are read (see `dts-lossless-extension`); the audio data is not decoded. In MP4, the ddts box states the construction.
