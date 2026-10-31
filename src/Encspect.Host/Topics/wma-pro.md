---
id: wma-pro
title: Windows Media Audio Professional
related: wma-standard, wma-lossless, waveformatex, channel-layouts
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, section 11.1.1
---

Windows Media Audio Professional (format tag 0x0162) is a transform codec like Windows Media Audio versions 1 and 2 and AAC, for up to eight channels and sample sizes above 16 bits.

**What you see in a file.** The analyzer reads the stream at header level: the WAVEFORMATEX and the codec-specific data the container states, the number of packets and their sizes, the length the container gives, and the encoder and writer the file's metadata names. The frames inside the packets are not read yet.

**Where it is specified.** The ASF specification names the format tag; Microsoft has published no specification of the frame syntax that the project could check.
