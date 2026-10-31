---
id: wma-voice
title: Windows Media Audio Voice
related: wma-voice-superframes, wma-voice-frames, wma-standard, wma-pro, asf-container, waveformatex
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, section 11.1.1; FFmpeg 6.1.1 libavcodec/wmavoice.c
---

**The problem.** Speech needs far fewer bits than music. A speech coder models the voice instead of the waveform: a filter for the throat and mouth and an excitation for the vocal cords, which a transform codec cannot do at a few kilobits per second.

**What the codec is.** Windows Media Audio Voice (format tag 0x000A) is such a coder, mono only. Its stream is a series of superframes of 480 samples, each of three frames of 160 samples. The first bit of every superframe tells speech from music; music superframes hold Windows Media Audio Professional data, which FFmpeg's decoder does not support.

**What you see in a file.** The codec-specific data is 46 bytes, and FFmpeg's decoder refuses any other size: 18 bytes laid out as Professional's, a 32-bit flags word for the postfilter, the denoise settings and the number of line spectral pairs (LSPs), and a table that gives each of 17 frame types its code. The decoder accepts sample rates from 7,990 to 22,097 Hz, and the rate sets the widths of the pitch fields. Packets are the block alignment's size.

**What the analyzer reads.** The codec data, each packet's header and spillover, each superframe's header, and each frame's type, pitch and block sizes. The excitation is read only to find where a frame ends: no gains, pulse positions or LSP values are reported, and no audio is built. A stream whose configuration FFmpeg's decoder rejects is described by what the container states, and its packets are counted.

**In Encspect's test files.** Every packet of the files from the Windows Media Format SDK's writer sets the residual LSPs flag, and no superframe of them states a sample count. The analyzer does not use this.

**Where it is specified.** Microsoft has published no specification of the codec that the project could check; FFmpeg's decoder is the reference.
