---
id: wma-voice
title: Windows Media Audio Voice
related: wma-standard, wma-pro, asf-container, waveformatex
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, section 11.1.1
---

**The problem.** Speech needs far fewer bits than music. A speech coder models the voice instead of the waveform: a filter for the throat and mouth and an excitation for the vocal cords, which a transform codec cannot do at a few kilobits per second.

**What the codec is.** Windows Media Audio Voice (format tag 0x000A) is such a coder, for mono speech at low bitrates.

**What you see in a file.** The analyzer reads the stream at header level: the WAVEFORMATEX and the codec-specific data the container states, the number of packets and their sizes, the length the container gives, and the encoder and writer the file's metadata names. The frames inside the packets are not read yet.

**Where it is specified.** Microsoft has published no specification of the codec that the project could check.
