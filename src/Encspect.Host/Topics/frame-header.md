---
id: frame-header
title: The MPEG audio frame header
related: frame-sync, mpeg-versions, mpeg-layers, channel-modes, crc
references: ISO/IEC 11172-3 2.4.1.3, 2.4.2.3; ISO/IEC 13818-3
---

Each MPEG audio frame starts with a 4-byte header that tells the decoder everything it needs to read the frame:

- the sync word, which marks the start of a frame ([frame sync](frame-sync));
- the [version](mpeg-versions) (MPEG-1, MPEG-2 or MPEG 2.5) and the [layer](mpeg-layers) (I, II or III);
- whether a [CRC](crc) follows the header;
- the bitrate index and the sample rate index, looked up in tables of the standard;
- the [padding](padding) bit, which adds one slot to the frame;
- a private bit with no defined meaning;
- the [channel mode](channel-modes) and, for joint stereo, the Mode Extension;
- the copyright and original bits, which nothing enforces;
- the [emphasis](emphasis) the audio was given before encoding.

From the bitrate, the sample rate and the padding bit, the frame's length follows, so the decoder knows where the next frame starts. Because every frame carries a full header, a decoder can start anywhere in the stream, and the bitrate, channel mode and even the sample rate can change from frame to frame; the analyzer reports such changes.
