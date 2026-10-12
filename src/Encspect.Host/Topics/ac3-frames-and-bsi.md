---
id: ac3-frames-and-bsi
title: AC-3 syncframes and bit stream information
related: ac3-audio-blocks, eac3-substreams, dialogue-normalization, crc, channel-layouts
references: ATSC A/52:2018 5.1 to 5.4, 7.10, Annex D, Annex E 2.2; ETSI TS 102 366 Annex F
---

**What it is.** AC-3 (Dolby Digital) and E-AC-3 (Dolby Digital Plus) cut audio into syncframes that a decoder can start at. Each begins with the sync word 0x0B77. An AC-3 frame then holds crc1, the sample rate and a frame size code that fixes its length, a constant bitrate from 32 to 640 kbps. Six audio blocks of 256 samples per channel follow, then auxiliary data and crc2.

**Bit stream information.** Before the blocks, the bit stream information says what the frame carries: bsid, the syntax version (up to 8 for AC-3, 16 for E-AC-3); bsmod, the service, such as a complete main mix or commentary; acmod, the channels, from 1/0 (one center channel) to 3/2 (three front, two surround), plus lfeon for an LFE channel; downmix levels; dialogue normalization and compression; and production details, copyright and time codes. With bsid 6, Annex D's alternate syntax replaces the time codes with the preferred stereo downmix and its levels.

**E-AC-3.** An E-AC-3 frame states its own length in words, has one to six blocks, and names a substream, so one bit stream can carry several programs and extra channels. It has one CRC over the whole frame.

**CRCs.** crc1 covers the first 5/8 of an AC-3 frame and crc2 all of it; both are written so the CRC over what they cover comes to zero.

**What you see.** Each frame with its sync information, every bit stream information field with its meaning, the audio blocks, the auxiliary data and the CRCs with whether they hold. In MP4, the dac3 or dec3 box repeats some of these fields, and a finding says where box and frames disagree.
