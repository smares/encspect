---
id: sbr
title: Spectral band replication (SBR, HE-AAC)
related: mp3pro, parametric-stereo, aac-object-types, audio-specific-config
references: ISO/IEC 14496-3 1.6.2.1, 4.6.18; fdk-aac
---

**The problem.** At low bitrates there are too few bits to code the high frequencies, so a plain AAC encoder cuts them off and the result sounds muffled.

**How it works.** Spectral band replication codes the lower part of the spectrum with an AAC core and rebuilds the upper part in the decoder: it copies the low band upward and shapes the copy with a small amount of side data, the envelope of the high band and how much noise it holds. The core only has to carry the lower half of the spectrum, so it runs at half the output sample rate: each frame of 1024 core samples becomes 2048 output samples. AAC LC with SBR is HE-AAC. [mp3PRO](mp3pro) used the same technique on an MP3 core.

**Signaling.** Hierarchical signaling puts SBR's object type, 5, first in the [AudioSpecificConfig](audio-specific-config), then the output rate and the core's type. Backward-compatible signaling puts the core's configuration first and a sync extension after it, which a decoder without SBR ignores. Implicit signaling states nothing: the configuration describes plain AAC LC at the core's rate, and the decoder finds the SBR data in fill elements while it decodes. [ADTS](adts) can only signal implicitly. A decoder that ignores SBR plays the core alone, without the high band.

**What you see.** HE-AAC as the codec, the output rate as the sample rate, and the sync extension or extension sample rate in the configuration. Where SBR can only be implicit, the codec reads "AAC LC (no SBR signaled explicitly)" with an info finding, since the fill elements after the channel elements are not read yet: fdkaac's HE-AAC in ADTS shows as AAC LC at 22,050 Hz, where ffprobe, which decodes them, reports HE-AAC at 44,100 Hz.
