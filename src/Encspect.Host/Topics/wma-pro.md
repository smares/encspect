---
id: wma-pro
title: Windows Media Audio Professional
related: wma-standard, wma-frames-and-blocks, mid-side-stereo, waveformatex
references: FFmpeg libavcodec/wmaprodec.c (6.1.1)
---

Windows Media Audio Professional (format tag 0x0162) is an MDCT codec like WMA standard and AAC, with up to eight channels.

**Frames and subframes.** A packet holds one or more frames, and a frame can cross a packet boundary. Every frame holds the same number of samples, which the codec data states together with a few decode flags. A frame splits into a variable number of subframes of 2^N samples each, with N from 7 to 12, so a transient can use short subframes while the rest of the frame uses long ones.

**Channels.** Each channel has its own subframe layout. Where several channels have subframes at the same position and length, they form a channel group. A group can use channel transforms such as mid/side coding, and a transform may apply to single scale factor bands instead of the whole subframe.

**Coefficients.** The spectrum is sent as Huffman-coded vectors of four, two or one values. An encoder can switch to run-level coding for sparse subframes. Scale factors are sent per frame, first as differences and, where channels or subframes share them, as run-level codes. Each subframe length and position shares a quantization factor that a per-channel modifier adjusts.

**In a file.** The stream's codec-specific data holds the decode flags and the channel mask; the report shows them with the frames and subframes read from the packets.

The reference is the open decoder in FFmpeg (libavcodec/wmaprodec.c).
