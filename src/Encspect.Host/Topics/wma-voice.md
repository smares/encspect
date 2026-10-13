---
id: wma-voice
title: Windows Media Audio Voice
related: wma-standard, wma-pro, asf-container, waveformatex, bit-reservoir
references: FFmpeg 6.1.1 libavcodec/wmavoice.c
---

**The problem.** Speech needs far fewer bits than music. A speech coder models the voice instead of the waveform: a filter for the throat and mouth and an excitation for the vocal cords, which a transform codec cannot do at a few kilobits per second.

**What the codec is.** Windows Media Audio Voice (format tag 0x000A) is such a coder. FFmpeg's decoder builds each frame from line spectral pairs, which describe the filter, and an excitation, with the routines of code-excited linear prediction (wmavoice.c includes its CELP and ACELP filters). A frame has at most 160 samples, and a superframe, which the container carries as a packet, at most three frames.

**What you see in a file.** The codec-specific data is 46 bytes (wmavoice.c rejects any other size), with a 32-bit flags word at byte 18 that turns the codec's options on and off. FFmpeg's decoder accepts sample rates from 322 to 22,097 Hz; the project's fixtures are mono, 5 kbit/s at 8 kHz and 16 kbit/s at 16 kHz.

**What the analyzer reads.** The stream is identified from the container's figures, and its packets are counted. The codec-specific data and the frames are not read.

**Where it is specified.** Microsoft has published no specification the project could check; FFmpeg's decoder is the reference.
