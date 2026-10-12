---
id: padding
title: The padding bit
related: frame-header, bitrate-modes
references: ISO/IEC 11172-3 2.4.2.3
---

**The problem.** A frame's length in bytes follows from its bitrate and the sample rate. At 44.1 kHz the result is not a whole number: a Layer III frame at 128 kbps would need 417.96 bytes. Frames can only have whole bytes.

**How it works.** The encoder writes most frames one slot short, 417 bytes here, and sets the padding bit in some of them, which adds one slot: a byte in Layers II and III, four bytes in Layer I. By padding the right share of frames, the average bitrate comes out exactly as stated. At 32 and 48 kHz the lengths come out whole, so no padding is needed there.

**What you see.** HasPadding in each frame header, UsesPadding in the statistics, and, under each bitrate of the bitrate distribution, how many frames have each length with and without padding. A CBR file at 44.1 kHz normally mixes padded and unpadded frames. Some encoders pad differently, which the frame sizes show.

The padding slot is part of the frame; it usually holds audio data like the rest of the frame, not filler.
