---
id: wma-bit-reservoir
title: The bit reservoir of Windows Media Audio
related: wma-superframes, wma-frames-and-blocks, bit-reservoir
references: docs/design/wma-standard-format.md
---

**The problem.** Frames differ a lot in size, while the superframes that carry them all have the block alignment's size. A frame sized to fit a superframe would waste bits when the sound is simple and starve it when the sound is complex.

**How it works.** With the bit reservoir the frames are packed one after another through the superframes. A frame that does not end in the superframe it began in goes on in the next one. Each superframe's header counts the frames that begin in it and gives a bit offset: how many bits after the header finish the frame begun earlier. The bits after the last frame that ends in a superframe are the reservoir: the start of the next frame. A superframe in which no frame ends is wholly the middle of a frame, and a frame may span several of them.

**Filler.** A frame that spans superframes may end before the bit offset. The bits between its end and the bit offset are skipped by a decoder. The analyzer calls them stuffing where all are zero and filler otherwise. In Encspect's test files, seven of the 15 files written by the Windows Media Format SDK and Media Foundation (wmvcore 12.0.26100.7309, constant and variable bitrate) have such bits. The analyzer reports them but does not use them for anything else.

**What you see.** The bit offset and the bits it covers in each superframe, the frames that span superframes, the superframes inside a frame, and the largest and average reservoir. The Bit Reservoir graph shows the bytes each superframe leaves. This is not the mechanism of the [bit reservoir of MPEG audio Layer III](bit-reservoir).
