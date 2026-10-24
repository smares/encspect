---
id: monkeys-audio-prediction
title: Monkey's Audio prediction
related: monkeys-audio-file, monkeys-audio-frames, monkeys-audio-range-coding, linear-prediction, mid-side-stereo, wavpack-decorrelation
references: Monkey's Audio SDK 13.42, Source/MACLib/NewPredictor.cpp, NNFilter.cpp, NNFilterGeneric.cpp, NNFilterCommon.h, Prepare.cpp, Interim.cpp and Old/NewPredictorOld.cpp
---

**The problem.** Audio is predictable from what came before it, and the better the prediction, the less is left to code. A better prediction costs more time on both sides.

**How it works.** The encoder first turns a stereo pair into a mid and a side channel ([mid/side](mid-side-stereo)). Each channel then goes through a fixed first-order filter, adaptive filters over its own recent values and over the other channel's, and, from the normal level up, a cascade of neural network filters: adaptive filters whose weights follow the sign of the last coded values. The decoder runs the same stages backwards and adapts the same way, so it needs no weights from the file. The compression level sets the cascade: none at fast (1000), 16 taps at normal, 64 at high, 256 and 32 at extra high, and 1,280, 256 and 16 at insane (5000), 1,552 taps for every sample of every channel.

**What you see.** The compression level in the Header node. The cascade makes decoding the main cost of the format: the analyzer reads a file of any level without decoding, and decodes one only when asked to verify the audio, where a file of the highest level takes far longer than one of the normal level. The files of version 3.93 and later are decoded. Version 3.93 and 3.94 predict with one adaptive filter of four taps and code the two channels of a pair on their own, and files before 3.98 adapt the neural network filters with a coarser step; files before 3.93 use the SDK's older coding, which is not decoded yet. 24-bit files from Monkey's Audio 5.01 to 8.51 add up the first stage in 64 bits, which the decoder undoes in an "interim" retry when a frame's CRC fails.
