---
id: monkeys-audio-prediction
title: Monkey's Audio prediction
related: monkeys-audio-file, monkeys-audio-frames, monkeys-audio-range-coding, linear-prediction, mid-side-stereo, wavpack-decorrelation
references: Monkey's Audio SDK 13.44, Source/MACLib/NewPredictor.cpp, NNFilter.cpp, NNFilterGeneric.cpp, NNFilterCommon.h, Prepare.cpp, Interim.cpp, Old/NewPredictorOld.cpp and Old/AntiPredictor*Old.cpp
---

**The problem.** Audio is predictable from what came before it, and the better the prediction, the less is left to code. A better prediction costs more time on both sides.

**How it works.** The encoder first turns a stereo pair into a mid and a side channel ([mid/side](mid-side-stereo)). Each channel then goes through a fixed first-order filter, adaptive filters over its own recent values and over the other channel's, and, from the normal level up, a cascade of neural network filters: adaptive filters whose weights follow the sign of the last coded values. The decoder runs the same stages backwards and adapts the same way, so it needs no weights from the file. The compression level sets the cascade: none at fast (1000), 16 taps at normal, 64 at high, 256 and 32 at extra high, and 1,280, 256 and 16 at insane (5000).

**What you see.** The compression level in the Header node. The cascade makes decoding the main cost of the format: the analyzer reads a file of any level without decoding, and decodes one only when asked to verify the audio, where a file of the highest level takes far longer than one of the normal level. Versions differ: 3.93 and 3.94 predict with one adaptive filter of four taps and code the two channels of a pair on their own, and files before 3.98 adapt the filters with a coarser step. Files before 3.93 use older predictors that undo the prediction of a channel's whole array, at four levels: fast, two small stages; normal, three adaptive stages; high, a filter of 16 taps in front of those; extra high, a filter of 128 taps (256 from 3.83) in front of those. 24-bit files that Monkey's Audio wrote from the addition of 32-bit audio until version 8.50 (the SDK's wording) add up the first stage in 64 bits, which the decoder undoes in a retry when a frame's CRC fails.
