---
id: aiff-common-chunk
title: The AIFF Common Chunk
related: aiff-container, extended-sample-rate, aiff-c-compression, pcm-samples
references: Apple, Audio Interchange File Format AIFF-C (draft of 8/26/91), section 4.0; Apple, Audio Interchange File Format AIFF, version 1.3
---

**The problem.** A player needs the figures that describe the sound before it reads a single sample: how many channels, how long, how wide the samples are and how fast they play.

**How it works.** The Common Chunk (COMM) holds the number of channels, the number of sample frames, the sample size in bits and the sample rate. A sample frame holds one sample of every channel, so the length of the sound is the number of frames over the sample rate. The sample size is 1 to 32 bits; for compressed sound it is the size before compression. The sample rate is not an integer but an 80-bit extended number ([extended sample rate](extended-sample-rate)).

In AIFF-C the chunk continues with a compression type, an ID such as NONE, and a compression name, a Pascal-style string for people ([compression types](aiff-c-compression)). The chunk is 18 bytes in AIFF and 22 plus the name in AIFF-C. The specification says the number of frames in this chunk is the clearest statement of the length, not the size of the sound data chunk.

**What you see.** The four figures, the sample rate with the exponent and mantissa that it is computed from, and in AIFF-C the type and the name. The stream's length is the number of frames over the rate, and a finding appears where the sound data holds fewer frames than the chunk states.
