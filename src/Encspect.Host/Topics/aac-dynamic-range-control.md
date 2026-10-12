---
id: aac-dynamic-range-control
title: AAC dynamic range control
related: aac-raw-data-blocks, replaygain
references: ISO/IEC 14496-3 4.4.2.7, 4.5.2.7; fdk-aac libAACdec/include/aacdecoder_lib.h
---

**The problem.** A recording made for a quiet room, with soft passages and loud peaks, is hard to follow in a car or late at night. Compressing the dynamic range at the encoder would take it away from every listener.

**How it works.** MPEG-4 AAC can carry gain changes alongside the audio and leave it to the decoder whether to apply them. dynamic_range_info, an extension payload of type EXT_DYNAMIC_RANGE in a fill element, splits the spectrum into one or more bands and gives each a gain change in steps of 0.25 dB, a sign that says whether it attenuates or boosts, and optionally the program's reference level, also in 0.25 dB steps below full scale. It can name the program config element it belongs to and leave channels out. A decoder scales the changes by the listener's setting: fdk-aac's decoder, for one, has separate factors for the attenuating and the boosting gains and a target reference level, and applies nothing by default for MPEG-4 DRC boosts.

**What you see.** The fill element's Extension Payload of type EXT_DYNAMIC_RANGE with its fields: the bands and their tops, the reference level and each band's gain change. The statistics count the payloads and their bytes among the fill elements.
