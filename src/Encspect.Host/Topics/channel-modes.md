---
id: channel-modes
title: Channel modes
related: joint-stereo, mid-side-stereo, frame-header, channel-layouts
references: ISO/IEC 11172-3 2.4.2.3
---

Every MPEG audio frame says in its header how its channels are coded. There are four modes:

- **Stereo**: two channels, left and right, coded independently.
- **Joint stereo**: two channels that may be coded together with the tools the Mode Extension field selects; see [joint stereo](joint-stereo).
- **Dual channel**: two independent mono channels, such as a program in two languages. They are not a stereo pair, though most players play them as one, so the stream's [channel layout](channel-layouts) has two discrete channels.
- **Mono**: a single channel, which takes about half the side information and audio data of two.

The mode can change from frame to frame, so the Channel Modes statistics count the frames of each. A change in the middle of a file usually means it was put together from parts.

The Stereo Coding statistics look at the frames' coding rather than their mode: they count left/right stereo, mid/side stereo, intensity stereo and both tools together for joint stereo frames, with dual channel and mono frames as entries of their own. Channel Bit Share shows how the frames of two channels divide their audio bits between the channels.

**Observed in Encspect's test files.** The test files `external-lame-dual-music` and `external-lame-stereo-music` hold six seconds with different content in each channel, encoded by LAME 3.100 at a constant 128 kbps with `-m d` (dual channel) and `-m s` (left/right stereo). In them the first channel has 44.1% to 55.9% (dual channel) and 46.4% to 58.0% (stereo) of a frame's part2_3_length, and 49.8% and 49.9% over the whole file. `external-twolame-dual-music`, TwoLAME 0.4.0 at 192 kbps with `-m d` in Layer II, gives 34.9% to 52.9% of a frame's channel bits, and 49.7% overall. So in these files the dual channel frames do not divide their bits equally frame by frame. They are three encodes, and the analyzer does not use these figures.

The channel mode also decides how much side information a Layer III frame carries (17 bytes for mono, 32 for two channels in MPEG-1) and, in Layer II, which bitrates are allowed: ISO/IEC 11172-3 allows some low bitrates only for mono and the highest ones only for two channels.
