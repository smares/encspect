---
id: wma-standard
title: Windows Media Audio, versions 1 and 2
related: wma-superframes, wma-bit-reservoir, wma-frames-and-blocks, noise-substitution, wma-pro, wma-voice, wma-lossless, waveformatex, asf-container
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, section 11.1.1; docs/design/wma-standard-format.md
---

**The problem.** Music has to be coded at a bitrate a small fraction of the original, and the coder has to decide where the bits are heard. Windows Media Audio is the codec of the Windows Media platform for this.

**What the codec is.** Windows Media Audio is a transform codec, in the family of MP3 and AAC. Version 1 has the format tag 0x0160 and version 2 the tag 0x0161. Windows Media Audio 9 Professional, Lossless and Voice are different codecs with their own format tags.

**Encode options.** The codec-specific data of the stream carries a word of encode options. Its bits say whether the exponents are coded with a scale factor codebook or as line spectral pairs, whether superframes use a bit reservoir, whether frames may be divided into blocks of several sizes, and, with variable block lengths, how many sizes there are. The sampling rate and the bitrate fix the rest: the frame length, whether noise substitution is used, and the widths of some fields.

**What you see in a file.** The analyzer reads the codec-specific data, every superframe, every frame and every block down to the end of its coefficients, with the figures counted over them. No audio is rebuilt, so the values of the coefficients and exponents are not shown.

**Where it is specified.** The ASF specification defines the type-specific data of the stream in section 11.1.1; the frame syntax is not part of it. Encspect reads it from a description of the syntax written for it.
