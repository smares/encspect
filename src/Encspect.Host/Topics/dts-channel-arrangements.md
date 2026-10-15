---
id: dts-channel-arrangements
title: DTS channel arrangements
related: dts-core-frames, dts-core-extensions, channel-layouts, channel-modes
references: ETSI TS 102 114 V1.6.1 (2019-08), 5.4.2 Table 5-4, 6.5.3 and 7.5.3 Tables 6-22 and 7-10
---

**The problem.** A decoder has to know how many channels follow and which speaker each feeds, in a format with many arrangements.

**How it works.** The core header's AMODE selects one of 16 arrangements in Table 5-4: 1/0 mono, a dual mono, 2/0 stereo (also as sum and difference, or left total and right total), 3/0, 2/1, 3/1, 2/2, 3/2 and several with center speakers, overhead or more surrounds, up to 8 channels. Values from 16 are user defined. The core codes at most five channels (PCHS); the LFE channel is a flag (LFF) of the frame. More channels come from XCh and XXCH, and the extension substream states its own speaker mask for each asset, in Table 7-10. The channel sets of the lossless extension (XLL) each have a mask with one bit for each speaker in the order of Table 6-22, as XXCH's, which FFmpeg reads that way; the stream's layout is the union of them.

A speaker mask names positions: center, left and right, the surround pair, LFE, center surround, rear pair, height and wide positions and others. In the report, a lone surround pair is the back pair and, where a rear pair exists too, the surround pair is the side pair, as for other formats.

**What you see.** The arrangement as a short name such as 3/2, the channel count including the LFE channel and the speaker of each channel. A user defined AMODE is an info finding, and its channels are reported as discrete.
