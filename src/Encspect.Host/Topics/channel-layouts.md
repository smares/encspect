---
id: channel-layouts
title: Channel layouts
related: channel-modes, mpeg-multichannel, parametric-stereo, iso-base-media-format
references: ITU-R BS.775; ISO/IEC 14496-3 Table 1.19; ISO/IEC 13818-3; ISO/IEC 14496-12 12.2.4; ISO/IEC 23091-3; Apple Core Audio, CoreAudioBaseTypes.h (AudioChannelLayout)
---

**The problem.** A stream holds numbered channels, but a player has to know which speaker each one feeds.

**How it works.** A layout gives every channel a speaker position. ITU-R BS.775 places the fronts of 5.1 at 30 degrees left and right of the center and the surround pair at 100 to 120 degrees; 7.1 adds a second surround pair, so one pair sits at the sides and the other at the back. The ".1" is the LFE channel, which carries only the lowest frequencies, for a subwoofer, and so counts as a fraction of a channel.

Each format fixes the order of its channels. AAC's 5.1 starts with the center (ISO/IEC 14496-3 Table 1.19), the MPEG-2 multichannel extension with the left and right of its stereo base (ISO/IEC 13818-3), and a program config element lists AAC's channels itself. Containers can state a layout too: QuickTime's chan box holds Core Audio's layout tag, channel bitmap or per-channel labels, and ISO/IEC 14496-12's chnl box a configuration or speaker positions of ISO/IEC 23091-3.

Some channels feed no speaker. In dual channel the two channels are independent programs, such as two languages. MPEG-2's multilingual channels, up to seven, carry other languages or commentary that a decoder plays in place of a speaker channel.

**What you see.** Each stream's layout, by its common name such as 5.1 where it has one, with the speakers in the stream's own order; channels without a position are discrete, and multilingual channels are counted apart. Where a container's layout and the codec's disagree, a finding says so, and the codec's counts.
