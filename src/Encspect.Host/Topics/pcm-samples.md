---
id: pcm-samples
title: PCM samples in WAV and AIFF
related: wave-format-extensible, channel-layouts, extended-sample-rate, bit-depth-usage, measurements
references: Microsoft, Multimedia Programming Interface and Data Specifications 1.0, "Data Packing for PCM WAVE Files"; Apple, Audio Interchange File Format 1.3, Common Chunk and Sample Points; AIFF-C draft, sound data chunk
---

**The problem.** A reader of uncompressed audio has to know how wide each number is, whether it is signed, which byte comes first and how the channels interleave.

**How it works.** PCM stores one number per sample of each channel; the samples of all channels at one instant make a sample frame, and the frames follow each other. A sample takes the smallest whole number of bytes that holds its bits, the bits sitting in the most significant bits of that integer and the rest being zero, so a 12-bit sample takes two bytes (Microsoft's specification for WAV, AIFF 1.3 for AIFF).

In WAV the bytes are little-endian; samples of 1 to 8 bits are unsigned, with 128 as silence for 8 bits, and samples of 9 bits or more are signed. In AIFF the bytes are big-endian and samples are signed. AIFF-C adds codings: sowt is little-endian (read at the width the Common Chunk states), fl32 and fl64 are IEEE floating-point numbers. WAV stores floats under the format tag 3, as 32 or 64 bits. A-law and mu-law spend 8 bits on a sample.

The number of sample frames comes from the size of the audio data over a frame's size in WAV, and from the Common Chunk in AIFF.

**Decoding.** The samples are the data, so decoding is unpacking, done only when measurements or output are asked for. It outputs the container's value as stored. Integers of more than 32 bits are not decoded.

**What you see.** The sample format, bits, byte order, sample frames and length. No checksum covers the samples, so the analyzer cannot say they are intact and shows no verification line; a finding appears where the data does not hold a whole number of frames. CI compares the decoded samples of the test files with FFmpeg's; docs/development.md lists the differences.
