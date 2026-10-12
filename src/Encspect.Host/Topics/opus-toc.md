---
id: opus-toc
title: The Opus TOC byte and frame packing
related: opus-modes, opus-pre-skip, bitrate-modes
references: RFC 6716, sections 3.1 to 3.4 and appendix B; RFC 7845, section 3
---

**The TOC byte.** Every Opus packet starts with a table-of-contents byte. Its top five bits are the configuration, one of 32 combinations of mode, audio bandwidth and frame duration (2.5 to 60 ms); the next bit says whether the frames are stereo; the last two are the frame count code. Nothing outside the packet is needed to know how long it lasts.

**Packing frames.** Code 0 holds one frame, code 1 two frames of equal size, code 2 two frames whose first length is stated, and code 3 any number of frames up to 120 ms, after a frame count byte that also says whether the frames vary in size (VBR) and whether padding follows. Lengths take one byte below 252 and two bytes above, up to 1275 bytes per frame. A frame of no bytes carries no data, as in discontinuous transmission. CBR encoders use code 3 padding to give every packet the same size.

**Well-formed packets.** Section 3.4 lists seven rules, R1 to R7: a packet has at least one byte, no frame exceeds 1275 bytes, code 1 splits evenly, code 2's stated length fits, code 3 holds at least one frame and at most 120 ms, and its frame count, padding and lengths fit the packet. Decoders reject a packet that breaks one.

**Multistream.** In Ogg, a packet of a multichannel stream holds one Opus packet per stream. All but the last use the self-delimiting framing of appendix B, which adds the last frame's length so the next stream's packet can be found.

**What you see.** Each packet's TOC, frame count byte, lengths, frames and padding, the statistics of codes and stereo flags, and findings for packets that break R1 to R7.
