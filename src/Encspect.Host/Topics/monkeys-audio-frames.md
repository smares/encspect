---
id: monkeys-audio-frames
title: Monkey's Audio frames
related: monkeys-audio-file, monkeys-audio-seek-table, monkeys-audio-range-coding, monkeys-audio-prediction, crc, mid-side-stereo
references: Monkey's Audio SDK 13.42, Source/MACLib/APEDecompress.cpp, APEDecompressCore.cpp, UnBitArray.cpp, Prepare.cpp and BitArray.cpp (the encoder's side), and Old/UnMACOld.cpp, Old/APEDecompressCoreOld.cpp for the versions before 3.93
---

**The problem.** The audio is coded in pieces so that a reader can start at any one of them, and each piece has to say whether it decoded to what the encoder saw.

**How it works.** A frame holds the number of samples the header says, but for the last frame, which holds the rest. It starts with the CRC of its decoded audio: the CRC-32 of the samples as WAV data, inverted and shifted right by one, kept in 31 bits (up to version 3.82 in all 32 bits, unshifted). A file of version 3.93 or later always has it, an older file only where its CRC format flag says so; without the flag a frame starts with a Rice-coded checksum of 30 bits, the sum of the absolute values of its samples (compared like a CRC, but not in files of 3.90 to 3.92). From version 3.83, when bit 31 is set, four more bytes of special frame codes follow: 1 for a silent left channel (the only code a mono frame uses), 2 for a silent right channel, 4 for a pseudo-stereo frame whose right channel repeats the left. A frame of digital silence then has no coded samples. Prediction and coding start afresh in every frame, and a stereo pair is coded as a [mid and a side channel](mid-side-stereo). From version 3.93 the channels' values alternate in the [range-coded](monkeys-audio-range-coding) data; before it a frame codes the whole mid channel, then the whole side channel. Frames start on a byte boundary from version 3.81; in 3.80 they follow each other bit by bit.

**What you see.** A Frame node with the samples it holds, its Frame CRC and, where present, Special Frame Codes. When you ask the analyzer to verify the audio, it decodes each frame and compares the CRC ([checks over the decoded audio](verification)); a frame that differs is reported.
