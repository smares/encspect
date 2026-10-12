---
id: aac-object-types
title: AAC object types and profiles
related: audio-specific-config, sbr, parametric-stereo, usac, adts
references: ISO/IEC 14496-3 1.5.1, 1.5.2, 1.6.2.1; ISO/IEC 13818-7
---

**Object types.** MPEG-4 audio is a family of codecs. An audio object type names the set of coding tools a stream uses, and the [AudioSpecificConfig](audio-specific-config) states it as a number:

- 1, AAC Main, with prediction from earlier frames, inherited from MPEG-2 AAC.
- 2, AAC LC, low complexity, without prediction: by far the most common.
- 3, AAC SSR, scalable sample rate, which splits the signal into four bands.
- 4, AAC LTP, long-term prediction, an MPEG-4 addition.
- 5, [SBR](sbr), which rebuilds high frequencies on top of an AAC core: HE-AAC.
- 29, [PS](parametric-stereo), parametric stereo on top of SBR: HE-AAC v2.
- 23 and 39, AAC LD and ELD, low delay variants for communication.
- 42, [USAC](usac), unified speech and audio coding: xHE-AAC.

**Profiles and levels.** A profile groups object types a decoder must support, such as the AAC profile with AAC LC, the High Efficiency AAC profile, which adds SBR, and the HE-AAC v2 profile, which adds PS. Levels within a profile limit the channels and sample rates. An HE-AAC v2 decoder also decodes HE-AAC and AAC LC; one that knows only AAC LC can play the core of a backward-compatible HE-AAC stream, without the high frequencies, at the core's rate.

**What you see.** The object type in the AudioSpecificConfig, the core's object type under SBR or PS, and the stream's codec: AAC LC, HE-AAC, HE-AAC v2 or xHE-AAC (USAC). [ADTS](adts) headers can only state types 1 to 4, so HE-AAC in ADTS is recognized by the SBR data in its access units.
