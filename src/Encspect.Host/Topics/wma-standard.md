---
id: wma-standard
title: Windows Media Audio, versions 1 and 2
related: wma-superframes, wma-frames-and-blocks, wma-bit-reservoir, waveformatex, asf-container
references: FFmpeg 6.1.1 libavcodec/wmadec.c, wma.c, wma.h, wma_common.c; Advanced Systems Format (ASF) Specification, revision 01.20.03, section 11.1.1
---

**The problem.** Music has to be coded at a bitrate a small fraction of the original, and the coder has to decide where the bits are heard. Windows Media Audio is the codec of the Windows Media platform for this.

**How it works.** Windows Media Audio is a transform codec, in the family of MP3 and AAC. It cuts the audio into frames of 512 to 2,048 samples per channel, chosen by the sample rate, and transforms each with an MDCT. For every block it sends a gain, exponents that give the spectrum's envelope, and the quantized coefficients as run-length codes from Huffman codebooks. Version 1 (format tag 0x0160) and version 2 (0x0161) differ in how bands are laid out and in details such as byte alignment. Options in the codec-specific data switch on the bit reservoir, variable block lengths and the way exponents are coded. The reference here is FFmpeg's decoder, which reads Microsoft's streams.

**What you see.** The encode options, and for every frame its blocks with their sizes, mid/side flag, gain, noise substitution, exponents and coefficients. The statistics give block sizes, mid/side share and the bit reservoir. Windows Media Audio 9 Professional and Lossless are different codecs with their own format tags.
