---
id: adts
title: ADTS and ADIF, raw AAC transports
related: aac-raw-data-blocks, audio-specific-config, crc, bitrate-modes, frame-sync, sbr
references: ISO/IEC 14496-3 1.A.2.1, 1.A.2.2, 1.A.3.2.1; ISO/IEC 13818-7 6.1, 6.2; fdk-aac
---

**The problem.** [Raw data blocks](aac-raw-data-blocks) carry no sync word and no length, and a bare AAC stream has no [AudioSpecificConfig](audio-specific-config). Without a container, a decoder could neither find the frames nor know how to decode them.

**ADTS.** The audio data transport stream puts a header of 7 bytes before every frame, 9 with a CRC. It starts with twelve set bits, the sync word, and states the MPEG version, the object type minus one, the sample rate index, the channel configuration, the frame's length in bytes and how many raw data blocks it holds. Like an MPEG audio frame, every frame can be found on its own, so a decoder can start anywhere, which suits broadcast and files with the .aac extension. Its layer field is always 0, which MPEG audio never uses, so the two kinds of headers are never confused.

**Buffer fullness.** Each header states how full the decoder's input buffer is after the frame, in 32-bit words per channel, so a decoder that starts mid-stream knows how long to wait before it decodes. 0x7FF marks a [variable bitrate](bitrate-modes).

**Limits.** The object type has two bits, so only AAC Main, LC, SSR and LTP fit: [SBR](sbr) and parametric stereo can only be signaled implicitly. An optional [CRC](crc) covers the header and the first bits of each channel element.

**ADIF.** The audio data interchange format has one header at the start of the file and none between the raw data blocks, so a decoder must start at the beginning. It is rare.

**What you see.** ADTS regions of frames, found by a chain of consistent headers, with each header's fields, the CRC check, gaps between frames, and statistics per stream.
