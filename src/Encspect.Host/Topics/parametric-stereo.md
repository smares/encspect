---
id: parametric-stereo
title: Parametric stereo (HE-AAC v2)
related: sbr, intensity-stereo, mid-side-stereo, aac-object-types
references: ISO/IEC 14496-3 1.6.2.1, subpart 8 (parametric stereo); fdk-aac
---

**The problem.** At the lowest bitrates, even with [SBR](sbr) there are too few bits to code two channels.

**How it works.** Parametric stereo codes a single channel, a mono downmix, and describes the stereo image with a few parameters per frequency band and stretch of time: how much louder one channel is than the other, how alike the two are, and their phase differences. The decoder spreads the mono signal back into two channels by those parameters, which take far fewer bits than a second channel. They travel inside the SBR data, and the stereo is rebuilt in SBR's filter bank, so parametric stereo always comes with SBR: AAC LC with SBR and PS is HE-AAC v2. Unlike [intensity stereo](intensity-stereo), which keeps one channel and its direction band by band in the core, parametric stereo also restores how wide and diffuse the sound is.

**Signaling.** As with SBR, in three ways: hierarchically, with object type 29 first in the configuration; backward compatibly, with a second sync word, 0x548, and a PS flag after SBR's; or implicitly, only in the SBR data.

**What you see.** HE-AAC v2 as the codec and Parametric stereo as the channel mode. The configuration's channel configuration is 1, mono, since the core codes one channel, while the stream reports two output channels. A decoder without PS plays the mono downmix. The PS data's extension ID inside the SBR data of each access unit is found, so PS signaled only implicitly turns the codec into HE-AAC v2 too, with an info finding.
