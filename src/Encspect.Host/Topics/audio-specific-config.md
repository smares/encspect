---
id: audio-specific-config
title: The AudioSpecificConfig
related: aac-object-types, sbr, adts, mp4-sample-tables
references: ISO/IEC 14496-3 1.6.2.1, 4.4.1, 4.4.1.1; ISO/IEC 14496-1 7.2.6.7; ISO/IEC 14496-14 5.6
---

**The problem.** AAC access units do not say how they are coded. A decoder has to know the object type, the sample rate and the channels before it reads the first one.

**What it holds.** The AudioSpecificConfig is a few bytes of bit fields that say exactly that: the [audio object type](aac-object-types) in five bits, the sampling frequency as a four-bit index into a table of common rates, or after index 15 as a 24-bit number, and the channel configuration, 1 for mono, 2 for stereo, up to 7.1. Then come the settings of the object type: for AAC, whether a frame holds 1024 or 960 samples. A channel configuration of 0 leaves the layout to a program config element, which lists the single channel and channel pair elements for the front, side and back speakers.

**Signaling extensions.** The config also says whether [SBR](sbr) and parametric stereo are present. In hierarchical signaling the first object type is SBR or PS, followed by the output rate and the core's object type. In backward-compatible signaling the core's configuration comes first, then a sync word, 0x2B7, and the SBR flag, which a decoder that knows nothing of SBR never reads.

**Where it is stored.** In MP4 the config is the DecoderSpecificInfo inside the `esds` box of the `mp4a` sample entry. [ADTS](adts) has none: each frame header repeats a subset of it, the object type, the rate index and the channel configuration.

**What you see.** An AudioSpecificConfig node under the DecoderSpecificInfo with each field at its bits, and the codec it adds up to as its value.
