---
id: dialogue-normalization
title: Dialogue normalization and dynamic range control
related: ac3-frames-and-bsi, dts-core-frames, replaygain, loudness
references: ATSC A/52:2018 5.4.2.8 to 5.4.2.10, 5.4.3.3 to 5.4.3.6, 7.6, 7.7; ETSI TS 102 114 5.4.2 Tables 5-8 and 5-20
---

**The problem.** Programs differ in loudness: a film mixed with quiet dialogue and loud explosions, then a commercial mixed loud. And listeners differ in how much of a film's dynamic range they want at night or in a car.

**Dialogue normalization.** The encoder writes dialnorm, how many dB the average dialogue lies below digital full scale, from 1 to 31. The playback system sets its gain from it, so dialogue plays at the same level whatever the program. The value 0 is reserved and read as 31.

**Dynamic range control.** dynrng, which a block may carry, is a gain from about -24 to +24 dB that the encoder computes to bring loud passages down and quiet ones up toward dialogue level. Decoders apply it by default, and may apply less of it, or none, at the listener's choice. compr, once per frame, has twice the range at half the resolution, for heavy compression where peaks must stay limited, such as an RF output.

**In DTS.** DIALNORM and VERNUM together give the gain DNG: 0 to -15 dB for VERNUM 7, -16 to -31 dB for VERNUM 6, and 0 dB, no normalization, for other revisions. It scales the decoder's output. With the DYNF flag, each subframe also carries a dynamic range coefficient.

**What you see.** dialnorm and compr in each frame's bit stream information, dynrng in the blocks that carry it, with their gains in dB. The statistics list the dialnorm values and count the frames with compr and the blocks with dynrng. Unlike [ReplayGain](replaygain), which a tag adds after encoding, these values are part of the coding and travel with every frame. The dialnorm value is stated by whoever encoded the stream; the [loudness](loudness) the analyzer computes from the decoded audio is another figure, and neither is called right or wrong.
