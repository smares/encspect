---
id: pcm-samples
title: PCM samples in WAV and AIFF
related: wave-format-extensible, channel-layouts, extended-sample-rate, bit-depth-usage, measurements
references: Microsoft, Multimedia Programming Interface and Data Specifications 1.0, chapter 3 (WAVE format); Apple, Audio Interchange File Format 1.3, Common Chunk and Sample Points; AIFF-C draft, sound data chunk
---

**The problem.** Uncompressed audio is a list of numbers, and a reader has to know how wide each is, whether it is signed, which byte comes first and how the channels interleave.

**How it works.** Pulse code modulation stores one number per sample of each channel. The samples of all channels at one instant make a sample frame, and the frames follow each other in time. A sample takes the smallest whole number of bytes that holds its bits, so a 12-bit sample takes two bytes. In AIFF the sample is left-justified in those bytes with the remaining bits zero (AIFF 1.3, "Sample Points"); WAV states the bits that hold audio (wValidBitsPerSample) and the analyzer does not rely on where they sit.

In WAV the bytes of a sample are little-endian; samples of 9 bits and more are signed, and samples of 1 to 8 bits are unsigned bytes, with 128 as silence. In AIFF the bytes are big-endian and samples are signed at every width. AIFF-C adds codings: sowt is little-endian (the analyzer reads it at the width the Common Chunk states), fl32 and fl64 are IEEE floating-point numbers. WAV stores floats under the format tag 3, as 32 or 64-bit IEEE 754 numbers. A-law and mu-law spend 8 bits on a sample.

The sample frames number the audio data's size over a frame's, and the length is that count over the sample rate.

**Decoding.** The samples are the data, so decoding is unpacking, and the analyzer does it only when measurements or output are asked for. It outputs the container's value as stored (8-bit WAV minus 128, companded samples expanded to 16 bits), so for 20 valid bits in 24 the [bit-depth usage](bit-depth-usage) shows the low bits never set. Integers of more than 32 bits are not decoded.

**What you see.** The sample format, the bits of audio and of the container, the byte order, the number of sample frames and the length. No checksum covers the samples, so the analyzer cannot say they are intact and shows no verification line; a finding appears where the data does not hold a whole number of frames. CI compares the decoded samples of the test files with FFmpeg's; docs/development.md lists the differences.
