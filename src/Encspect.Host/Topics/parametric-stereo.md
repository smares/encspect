---
id: parametric-stereo
title: Parametric stereo (HE-AAC v2)
related: sbr, intensity-stereo, mid-side-stereo, aac-object-types
references: ISO/IEC 14496-3 1.6.2.1, 1.6.6.3, subpart 8 (parametric stereo); fdk-aac
---

**The problem.** At the lowest bitrates, even with [SBR](sbr) there are too few bits to code two channels.

**How it works.** Parametric stereo codes a single channel, a mono downmix, and describes the stereo image with a few parameters per frequency band and stretch of time: how much louder one channel is than the other, how alike the two are, and their phase differences. The decoder spreads the mono signal back into two channels by the level and coherence parameters, which take far fewer bits than a second channel. They travel inside the SBR data, and the stereo is rebuilt in SBR's filter bank, so parametric stereo always comes with SBR: AAC LC with SBR and PS is HE-AAC v2. Unlike [intensity stereo](intensity-stereo), it also restores how wide and diffuse the sound is.

**Decoding.** A hybrid filter bank splits the lowest QMF bands (the first three or five) into finer subbands (12 or 32). A decorrelator, a chain of all-pass filters and delays, makes a second, uncorrelated signal from the mono one. Per band, a two-by-two matrix of level difference and coherence mixes the two, interpolated between envelope borders.

**Signaling.** As with SBR, in three ways: hierarchically, with object type 29 first in the configuration; backward compatibly, with a second sync word, 0x548, and a PS flag after SBR's; or implicitly, only in the SBR data. Without the flag, a decoder assumes PS for a single channel with SBR (1.6.6.3): two output channels, PS in the blocks that carry it, the one channel on both in the others. A flag sent as 0 keeps one channel.

**What Encspect decodes.** It runs the 34-band hybrid filter bank where a stream uses 34 bands (the baseline of levels 2 to 4 maps them to 20, Table 8.46) and mixes with matrix Ra in every mode; it does not decode the inter-channel phase differences or the unrestricted mixing with matrix Rb (coherence modes 3 to 5, 8.A.4). So it is neither the baseline decoder of HE-AAC levels 2 to 4 nor the unrestricted tool of level 5 (Table 1.12, note 1).

**What you see.** HE-AAC v2 as the codec and Parametric stereo as the channel mode: the channel configuration says 1, mono, since the core codes one channel, and the stream reports two output channels. The PS data's extension ID inside the SBR data of each access unit is found, so PS signaled only implicitly turns the codec into HE-AAC v2 too, with an info finding, however late the first such block comes (the decoder runs it from the first block that carries PS data, 8.A.1). A single channel with SBR and no PS data is HE-AAC with one channel, which the decoder still outputs on both. Only a bitstream of exactly one single channel element may contain PS data (the text after Table 1.12, 8.A.1), so a stream of more channels gets a finding instead; fdk-aac and ffmpeg apply it to a core of one channel only too.
