---
id: pcm-samples
title: PCM samples in WAV and AIFF
related: wave-format-extensible, channel-layouts, extended-sample-rate
references: Microsoft, Multimedia Programming Interface and Data Specifications 1.0, chapter 3 (WAVE format); Apple, Audio Interchange File Format 1.3 and AIFF-C draft, sound data chunk
---

**The problem.** Uncompressed audio is a list of numbers, and a reader has to know how wide each is, whether it is signed, which byte comes first and how the channels interleave.

**How it works.** Pulse code modulation stores one number per sample of each channel. The samples of all channels at one instant make a sample frame, and the frames follow each other in time. A sample takes the smallest whole number of bytes that holds its bits, the bits sitting at the top of the integer and the rest being zero, so a 12-bit sample takes two bytes.

In WAV the bytes of a sample are little-endian and samples of 9 bits and more are signed; 8-bit samples are unsigned, with 128 as silence. In AIFF the bytes are big-endian and samples are signed at every width. AIFF-C adds codings: sowt is 16-bit little-endian, fl32 and fl64 are IEEE floating-point numbers. A WAV file stores floating-point samples under the format tag 3, as 32 or 64-bit IEEE 754 numbers.

The number of sample frames follows from the size of the audio data divided by the size of a frame. The length in time is that count over the sample rate.

**What you see.** The sample format, the bits of audio and of the container, the byte order, the number of sample frames and the length. No checksum covers the samples, so the analyzer cannot say they are intact; a finding appears where the data does not hold a whole number of frames.
