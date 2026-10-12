---
id: aac-raw-data-blocks
title: AAC raw data blocks and syntax elements
related: audio-specific-config, adts, sbr, mid-side-stereo, intensity-stereo, encoder-detection, aac-windows-and-blocks, aac-section-data-and-codebooks, tns, pns, aac-pulse-data, aac-dynamic-range-control
references: ISO/IEC 14496-3 4.4.2.1, 4.5.2.1; ISO/IEC 13818-7
---

**What a frame is.** Each AAC access unit, a frame of 1024 samples per channel, is a raw data block: a series of syntax elements, each opening with a three-bit ID, until an END element closes it. The elements are:

- SCE, a single channel element: one channel, such as mono or the center.
- CPE, a channel pair element: two channels coded together, which may share their block switching and use [mid/side](mid-side-stereo) or [intensity stereo](intensity-stereo) band by band.
- LFE, a low frequency effects channel.
- CCE, a coupling channel element, which adds one signal to several channels.
- DSE, a data stream element: bytes for the application, which decoders skip.
- PCE, a program config element: a channel layout.
- FIL, a fill element: padding that keeps a constant bitrate, and extensions such as [SBR](sbr) data and dynamic range control.

**No lengths.** Elements do not state their lengths. A channel element ends where its Huffman-coded spectral data ends, so finding the next element means decoding the spectrum. Instance tags tell elements apart.

**Traces of the encoder.** Encoders choose the order and the extras. ffmpeg's AAC encoder puts a fill element with its version, such as Lavc60.31.102, at the start of the first access unit and of every 256th after it, and FAAC one with libfaac 1.30 into the first; either names its encoder with high confidence in [encoder detection](encoder-detection).

**What you see.** Every element of each access unit or raw data block, read to its end: the channel elements with their window information, sections, mid/side and intensity stereo bands, TNS filters and pulse data, the bits of their scalefactors and spectral data, and fill elements with their payloads, such as SBR data, dynamic range control and readable text. The statistics count elements, windows, stereo coding, tools and codebooks.
