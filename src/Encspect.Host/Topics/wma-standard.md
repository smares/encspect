---
id: wma-standard
title: Windows Media Audio, versions 1 and 2
related: wma-pro, wma-voice, wma-lossless, waveformatex, asf-container
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, section 11.1.1
---

**The problem.** Music has to be coded at a bitrate a small fraction of the original, and the coder has to decide where the bits are heard. Windows Media Audio is the codec of the Windows Media platform for this.

**What the codec is.** Windows Media Audio is a transform codec, in the family of MP3 and AAC. Version 1 has the format tag 0x0160 and version 2 the tag 0x0161. Windows Media Audio 9 Professional, Lossless and Voice are different codecs with their own format tags.

**What you see in a file.** The analyzer reads the stream at header level: the WAVEFORMATEX and the codec-specific data the container states, the number of packets and their sizes, the length the container gives, and the encoder and writer the file's metadata names. The frames inside the packets are not read yet.

**Where it is specified.** The ASF specification defines the type-specific data of the stream in section 11.1.1; the frame syntax is not part of it.
