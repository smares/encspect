---
id: ath
title: The absolute threshold of hearing (ATH)
related: lowpass, noise-shaping, lame-tag
references: LAME documentation; LAME tag specification (Gabriel Bouvigne)
---

**What it is.** The absolute threshold of hearing is the quietest sound a person can hear at each frequency, in silence. It is lowest, so hearing is most sensitive, around 2 to 5 kHz, and it rises steeply toward the low and high ends of the range.

**Why encoders use it.** A perceptual encoder may let quantization noise rise to whatever the ear cannot hear. Where loud sounds mask nearby frequencies, the masking threshold sets that limit; where the music is quiet, the absolute threshold does. Sounds and noise below it need no bits at all.

**In LAME.** LAME's psychoacoustic model offers several curves for the threshold, chosen with --athtype, and adjusts how the threshold follows the music's loudness. The curve affects how the encoder treats quiet passages and the high frequencies.

**What you see.** The LAME tag's ATH Type field records which curve LAME used, as a number whose meaning depends on the LAME version. Together with the other settings in the tag, it helps identify the preset a file was made with.
