---
id: monkeys-audio-frames
title: Monkey's Audio frames
related: monkeys-audio-file, monkeys-audio-seek-table, monkeys-audio-range-coding, monkeys-audio-prediction, crc, mid-side-stereo
references: Monkey's Audio SDK 13.42, Source/MACLib/APEDecompress.cpp, APEDecompressCore.cpp, UnBitArray.cpp, Prepare.cpp and BitArray.cpp (the encoder's side)
---

**The problem.** The audio is coded in pieces so that a reader can start at any one of them, and each piece has to say whether it decoded to what the encoder saw.

**How it works.** A frame holds the same number of samples as the header says, but for the last frame, which holds the rest. Each frame starts with a 32-bit CRC of its decoded audio, kept in 31 bits: the stored value is the CRC-32 of the samples as WAV data, inverted and shifted right by one. When bit 31 is set, four more bytes of special frame codes follow: 1 for a silent left channel (a silent mono frame), 2 for a silent right channel, 4 for a pseudo-stereo frame whose right channel repeats the left. A frame of digital silence then has no coded samples at all. The coded samples start after one dummy byte, and the predictors and the [range coder](monkeys-audio-range-coding) start afresh in every frame. A stereo pair is coded as a [mid and a side channel](mid-side-stereo).

**What you see.** A Frame node with the samples it holds, its Frame CRC and, where present, Special Frame Codes. When you ask the analyzer to verify the audio, it decodes each frame and compares the CRC ([checks over the decoded audio](verification)); a frame that differs is reported. In Encspect's test files a silent stereo file of 160,000 samples takes 172 bytes.
