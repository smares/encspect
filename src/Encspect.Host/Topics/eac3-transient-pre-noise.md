---
id: eac3-transient-pre-noise
title: Transient pre-noise processing in E-AC-3
related: block-switching, ac3-audio-blocks, filter-bank, eac3-substreams
references: ATSC A/52:2018 Annex E 2.3.2.4, 2.3.2.21 to 2.3.2.23, 3.7
---

**The problem.** A transform coder spreads the quantization noise of a block over the whole of its window. Where a sharp attack follows quiet audio, the noise is heard before the attack, as [pre-echo](block-switching); A/52 calls it pre-noise. Block switching shortens the window, but at low bitrates some noise remains.

**How it works.** The encoder finds the transient and sends, for each channel and frame, its location, in steps of 4 samples from the first decoded sample of the frame, and a time scaling length. The location may lie in a later frame. The decoder takes the pre-noise to run from the start of the audio block before the one that holds the transient up to the transient. It copies decoded audio from before the pre-noise into a buffer and writes it over the output, starting the time scaling length, the pre-noise and 256 samples before the transient: cross-faded in over 256 samples, then copied, then cross-faded out over the last 128 samples before the transient. Quiet audio from before the attack thus replaces the noise.

**What the specification leaves open.** A/52 says the reference decoder shall implement it, and that nearly any pair of constant amplitude cross-fade windows may be used, Hanning windows having been shown to give good results. Encspect's decoder uses Hanning windows, and holds its output back until no later frame can still correct it, about 1,500 samples at most.

**What you see.** The flag that says a frame carries the data, and for each channel with it the location in samples and the length. Of the decoders Encspect is cross-checked with, FFmpeg, oxideav-ac3 and Core Audio do not apply the processing; the Dolby Digital Plus decoder of Windows 10 applies it to some of the transients of Encspect's test file, its corrections one block (256 samples) later than Encspect's, which reads the location as A/52 states it.
