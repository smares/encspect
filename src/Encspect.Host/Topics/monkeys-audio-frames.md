---
id: monkeys-audio-frames
title: Monkey's Audio frames
related: monkeys-audio-file, monkeys-audio-seek-table, monkeys-audio-range-coding, monkeys-audio-prediction, crc, mid-side-stereo
references: Monkey's Audio SDK 13.42, Source/MACLib/APEDecompress.cpp, APEDecompressCore.cpp, UnBitArray.cpp, Prepare.cpp and BitArray.cpp (the encoder's side)
---

**The problem.** The audio is coded in pieces so that a reader can start at any one of them, and each piece has to say whether it decoded to what the encoder saw.

**How it works.** A frame holds the same number of samples as the header says, but for the last frame, which holds the rest. From version 3.93 each frame starts with a 32-bit CRC of its decoded audio, kept in 31 bits (a file before that has one only where its CRC format flag says so; without the flag a frame starts with a 30-bit Rice-coded checksum, which the analyzer does not read): the stored value is the CRC-32 of the samples as WAV data, inverted and shifted right by one. When bit 31 is set, four more bytes of special frame codes follow: 1 for a silent left channel (the only code a mono frame uses: a silent mono frame), 2 for a silent right channel, 4 for a pseudo-stereo frame whose right channel repeats the left. A frame of digital silence then has no coded samples at all. The coded samples start after one dummy byte, and the predictors and the [range coder](monkeys-audio-range-coding) start afresh in every frame. A stereo pair is coded as a [mid and a side channel](mid-side-stereo).

**What you see.** A Frame node with the samples it holds, its Frame CRC and, where present, Special Frame Codes. When you ask the analyzer to verify the audio, it decodes each frame and compares the CRC ([checks over the decoded audio](verification)); a frame that differs is reported. A silent frame has the two words and a few bytes of range coder (13 bytes in Encspect's test file of 160,000 silent stereo samples, which the analyzer does not use as a rule).
