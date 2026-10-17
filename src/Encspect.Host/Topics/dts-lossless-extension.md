---
id: dts-lossless-extension
title: DTS lossless extension (XLL)
related: dts-extension-substream, dts-channel-arrangements, crc, bitrate-modes
references: ETSI TS 102 114 V1.6.1 (2019-08), clause 8; FFmpeg 6.1.1 libavcodec/dca_xll.c; MediaInfoLib v24.01 File_Dts.cpp; the real files of check_dts_hd_samples.py (FFmpeg sample archive, A-codecs/DTS/dts)
---

**The problem.** A lossy core cannot give back the source exactly. DTS-HD Master Audio adds a lossless layer (XLL) that codes what the core gets wrong, or the whole signal where there is no core, so the decoded audio equals the input bit for bit.

**How it works.** One XLL frame starts with the sync word 0x41A29547 and a common header with a CRC-16. Behind it come:

- a sub-header for each channel set (CRC-16 each): its channels, the source resolution and the storage width of its samples, its sampling frequency, whether it is a primary set, the downmix coefficients that fold it into the sets before it, a channel mask of one bit for each speaker and, for each frequency band, the parameters of the decoder: pairwise channel decorrelation, predictor orders, reflection coefficients and the sizes of the least significant parts;
- the navigation index (NAVI, CRC-16): the byte size of the band data for each frequency band, segment and channel set;
- the band data: the entropy-coded residuals of each segment, with the frequency bands that sampling frequencies above 96 kHz add.

Channel sets form a hierarchy. The 7.1 stream of the FFmpeg sample archive ("Master Audio 7.1.dts") is a primary 5.1 set plus a set of the two rear channels; the layout is the union of the masks. The residual bits of a set say whether it carries the original audio or its difference from the core.

**What you see.** Each sub-header and its fields, the navigation index, and the verdict of every CRC-16. The stream reports the source resolution, sampling frequency and layout of the channel sets. The band data is not decoded.

**DTS:X.** No specification describes DTS:X. FFmpeg's decoder aligns the end of the band data to 32 bits from the frame's sync word and, if bytes remain in the frame, compares the word there with 0x02000850 (DTS:X) and 0xF14000D0 or 0xF14000D1 (DTS:X IMAX). MediaInfo reads 0x02000850 and 0xF14000D1 as DTS:X and 0xF14000D0 as IMAX. The analyzer reports that word and nothing about the data behind it.
