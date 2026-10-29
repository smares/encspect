---
id: wma-superframes
title: Windows Media Audio superframes
related: wma-bit-reservoir, wma-frames-and-blocks, wma-standard, asf-packets
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, section 11.1.1; docs/design/wma-standard-format.md
---

**The problem.** A container hands a decoder media objects of one size, the block alignment of the stream, but the frames of a transform codec need different numbers of bits.

**How it works.** One media object is one superframe. Without the bit reservoir a superframe holds exactly one frame, read from its first bit, and the bits after the frame are padding that nothing reads. With the bit reservoir a superframe starts with a header: a 4-bit superframe index, a 4-bit count of the frames that begin in it, and a bit offset whose width follows from the bitrate and the frame length. The bit offset counts the bits after the header that finish a frame begun in an earlier superframe; the frames that begin here follow them.

**What you see.** Each superframe in the tree with its header fields, the bits of each frame, and the bytes left for the next superframe. A media object shorter than the block alignment, a frame count that leaves no frame to end in the superframe, and a bit offset past the superframe's end are findings, because a decoder rejects them.

**Padding.** In Encspect's test files, ffmpeg's wmav1 and wmav2 encoders pad every superframe with 0x4E bytes after the frame, and the files of Microsoft's encoders, which use the bit reservoir, have no such padding. The analyzer uses the padding to name ffmpeg's encoders.
