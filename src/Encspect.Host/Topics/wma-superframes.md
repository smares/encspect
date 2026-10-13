---
id: wma-superframes
title: Windows Media Audio superframes
related: wma-bit-reservoir, wma-standard, asf-packets, waveformatex
references: FFmpeg 6.1.1 libavcodec/wmadec.c (wma_decode_superframe), wma.c (byte_offset_bits)
---

**The problem.** The frames of a transform codec differ in size, while the ASF container carries media objects of a size the stream states once, the block alignment.

**How it works.** Windows Media Audio packs its frames into superframes, each the size of the block alignment from the stream's WAVEFORMATEX, and the container carries each as a media object. Without the bit reservoir a superframe is one frame followed by padding. With it, a superframe starts with a header: a 4-bit index, a 4-bit count of frames that end in the superframe and a bit offset of a width the bitrate sets. The offset says how many bits at the start finish the frame that began in the previous superframe; the frames that begin in this one follow, and the last may go on in the next.

**What you see.** For each superframe its header fields, the end of the frame before it, the frames in it and the bits it leaves for the next, with their sizes. Findings report superframes shorter than the block alignment, invalid headers and frames that do not end where the next superframe's bit offset says.
