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

The channel mode also decides how much side information a Layer III frame carries (17 bytes for mono, 32 for two channels in MPEG-1) and, in Layer II, which bitrates are allowed: ISO/IEC 11172-3 allows some low bitrates only for mono and the highest ones only for two channels.
