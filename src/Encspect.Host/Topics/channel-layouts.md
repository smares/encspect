---
id: channel-layouts
title: Channel layouts
related: dts-channel-arrangements, channel-modes, mpeg-multichannel, parametric-stereo, iso-base-media-format, opus-channel-mapping, eac3-substreams
references: ITU-R BS.775; ISO/IEC 14496-3 Table 1.19; ISO/IEC 13818-3; ATSC A/52:2018 Table 5.8, ETSI TS 102 114 Table 5-4, Annex E 2.3.1.8; ISO/IEC 14496-12 12.2.4; ISO/IEC 23091-3; Vorbis I specification 4.3.9; RFC 7845 5.1.1.2; Apple Core Audio, CoreAudioBaseTypes.h (AudioChannelLayout)
---

**The problem.** A stream holds numbered channels, but a player has to know which speaker each one feeds.

**How it works.** A layout gives every channel a speaker position. ITU-R BS.775 places the fronts of 5.1 at 30 degrees left and right of the center and the surround pair at 100 to 120 degrees; 7.1 adds a second surround pair, so one pair sits at the sides and the other at the back. The ".1" is the LFE channel, which carries only the lowest frequencies, for a subwoofer.

Each format fixes the order of its channels. AAC's 5.1 starts with the center (ISO/IEC 14496-3 Table 1.19), the MPEG-2 multichannel extension with the left and right of its stereo base (ISO/IEC 13818-3), Vorbis with front left, center and front right and the LFE last (Vorbis I specification, section 4.3.9), an order Opus (RFC 7845, section 5.1.1.2) and AC-3 share; a program config element lists AAC's channels itself, and E-AC-3's [dependent substreams](eac3-substreams) add locations to a program. Containers can state a layout too: QuickTime's chan box and ISO/IEC 14496-12's chnl box. DTS's core names one of 16 arrangements, and its extensions add speakers by masks ([DTS arrangements](dts-channel-arrangements)).

Some channels feed no speaker. In dual channel the two channels are independent programs, such as two languages. MPEG-2's multilingual channels, up to seven, carry other languages or commentary that a decoder plays in place of a speaker channel.

**What you see.** Each stream's layout, by its common name such as 5.1 where it has one, with the speakers in the stream's own order; channels without a position are discrete, and multilingual channels are counted apart. Where a container's layout and the codec's disagree, a finding says so, and the codec's counts.
