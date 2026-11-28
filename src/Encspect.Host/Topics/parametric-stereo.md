---
id: parametric-stereo
title: Parametric stereo (HE-AAC v2)
related: sbr, intensity-stereo, mid-side-stereo, aac-object-types
references: ISO/IEC 14496-3 1.6.2.1, 1.6.6.3, subpart 8 (parametric stereo); fdk-aac
---

**The problem.** At the lowest bitrates, even with [SBR](sbr) there are too few bits to code two channels.

**How it works.** Parametric stereo codes a single channel, a mono downmix, and describes the stereo image with a few parameters per frequency band and stretch of time: how much louder one channel is than the other, how alike the two are, and their phase differences. The decoder spreads the mono signal back into two channels by these parameters, which take far fewer bits than a second channel. They travel inside the SBR data, so PS always comes with SBR: AAC LC with SBR and PS is HE-AAC v2. Unlike [intensity stereo](intensity-stereo), it restores how wide the sound is.

**Decoding.** A hybrid filter bank splits the lowest QMF bands into finer subbands. A decorrelator of all-pass filters and delays makes a second, uncorrelated signal. Per band, a two-by-two matrix mixes the two, interpolated between envelope borders. Two procedures build it: Ra, for the coherence modes 0 to 2, and Rb, for 3 to 5. Where the extension carries phase differences, the matrix also turns each channel by its band's phase.

**Signaling.** As with SBR, in three ways: hierarchically, with object type 29 first in the configuration; backward compatibly, with a second sync word, 0x548, and a PS flag after SBR's; or implicitly, only in the SBR data. Without the flag, a decoder assumes PS for a single channel with SBR (1.6.6.3): two output channels. A flag sent as 0 keeps one.

**What Encspect decodes.** The full tool: 34 bands where a stream uses them (the baseline of levels 2 to 4 maps them to 20), both mixing procedures and the phase differences, which the baseline skips (8.A.4).

**What you see.** HE-AAC v2 as the codec and Parametric stereo as the channel mode: the channel configuration says 1, mono, since the core codes one channel, and the stream reports two output channels. The PS extension's ID in the SBR data is found, so implicit PS turns the codec into HE-AAC v2 too, with an info finding. A single channel with SBR and no PS data is HE-AAC with one channel, which the decoder outputs on both. Only a bitstream of exactly one single channel element may contain PS data (the text after Table 1.12, 8.A.1), so a stream of more channels gets a finding instead.
