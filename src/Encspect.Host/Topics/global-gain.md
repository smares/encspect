---
id: global-gain
title: Global gain
related: scalefactors, scalefac-scale-and-preflag, huffman-coding
references: ISO/IEC 11172-3 2.4.2.7, 2.4.3.4
---

**What it is.** Every granule and channel of a Layer III frame has an 8-bit global_gain, the step size of the quantizer for all its frequency lines. Each step up makes the decoded sound 1.5 dB louder relative to the coded values. The [scalefactors](scalefactors) then adjust the level band by band, and short windows can be lowered further with subblock_gain, 12 dB per step.

**How encoders use it.** The encoder's rate loop searches for the global gain at which the quantized spectrum just fits the bits it has for the granule: a larger step means smaller numbers, which take fewer bits, and more quantization noise. So the global gain follows both the loudness of the music and the bits available.

**Changing the volume without decoding.** Since every frame's global gain scales its whole output, a program can make a file louder or quieter by adding the same number to every global_gain, without decoding and re-encoding it. MP3Gain does this in steps of 1.5 dB, and LAME's tag has an MP3 Gain field to record it; MP3Gain also keeps undo information in an APE tag. Such a change alters the audio frames, so the LAME tag's music CRC no longer matches.

**What you see.** Each granule shows its Global Gain, and the statistics show the smallest and largest value per channel, as mp3guessenc does.
