---
id: pns
title: Perceptual noise substitution (PNS)
related: aac-section-data-and-codebooks, intensity-stereo, aac-raw-data-blocks
references: ISO/IEC 14496-3 4.4.2, 4.6.2; ffmpeg libavcodec/aacdec_template.c
---

**The problem.** Some parts of a sound are noise: cymbals, breath, the hiss of a recording. Coding the exact waveform of noise spends bits on detail the ear cannot tell from any other noise of the same loudness in the same band.

**How it works.** Perceptual noise substitution codes such a scalefactor band only by its loudness. The encoder marks it with codebook 13, NOISE_HCB, in the section data, sends no spectral values for it, and puts a noise energy where the band's scalefactor would be: the first noise energy of a channel as a 9-bit value relative to global_gain, the others as differences, with the scalefactor codebook. The decoder fills the band with random values scaled to that energy. What is lost is the waveform of the noise; its spectrum and level remain.

**Where it applies.** PNS is a tool of MPEG-4 AAC. Encoders use it in bands where the signal is noise-like, more at lower bitrates; ffmpeg's encoder turns it on by default, and the fixtures show FAAC using it, while those of fdk-aac show none.

**What you see.** The Noise Substitution Bands of each channel, which lie at its section data, and in the statistics the share of channel streams that use PNS and the share of all coded bands that are noise bands.
