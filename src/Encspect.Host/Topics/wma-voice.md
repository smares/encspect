---
id: wma-voice
title: Windows Media Audio Voice
related: wma-voice-superframes, wma-voice-frames, wma-standard, wma-pro, asf-container, waveformatex
references: FFmpeg 6.1.1 libavcodec/wmavoice.c
---

**The problem.** Speech needs far fewer bits than music. A speech coder models the voice instead of the waveform: a filter for the throat and mouth and an excitation for the vocal cords, which a transform codec cannot do at a few kilobits per second.

**What the codec is.** Windows Media Audio Voice (format tag 0x000A) is such a coder, mono only. FFmpeg's decoder builds each frame of 160 samples from line spectral pairs, which describe the filter, and an excitation, with the routines of code-excited linear prediction. The first bit of every superframe tells speech from music; music superframes hold Windows Media Audio Professional data, which FFmpeg's decoder does not support.

**What you see in a file.** The codec-specific data is 46 bytes (wmavoice.c rejects any other size): 18 bytes laid out as Professional's, a 32-bit flags word for the postfilter and the LSP options, and a table that gives each of 17 frame types its code length. FFmpeg's decoder accepts sample rates from 7,990 to 22,097 Hz: below, the range of pitches is too narrow for the pitch of a block to be coded as a step from the one before, and above, the history it keeps of the signal is too short for the longest pitch. Packets are the block alignment's size and hold superframes of three frames.

**What the analyzer reads.** The codec data, each packet's header and spillover, each superframe's header, and each frame's type, pitch and block sizes. The excitation is read only to find where a frame ends: no gains, pulse positions or LSP values are reported, and no audio is built.

**In Encspect's test files.** Six files from the Windows Media Format SDK's writer (wmvcore 12.0.26100; Voice 9: tones at 5 kbit/s and 8 kHz and at 16 kbit/s and 16 kHz, and a speech-like signal at 4, 5 and 8 kbit/s and 8 kHz and at 16 kbit/s and 16 kHz) set the residual LSPs flag in every packet and state no sample count. The analyzer does not use this.

**Where it is specified.** Microsoft has published no specification the project could check; FFmpeg's decoder is the reference.
