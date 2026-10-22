---
id: flac-streaminfo
title: STREAMINFO
related: flac-metadata-blocks, flac-frames, verification, crc
references: RFC 9639 (FLAC), section 8.2
---

**The problem.** A decoder needs the sample rate, the channels and the bit depth before it reads a frame, and a verifier needs something to compare the decoded audio with.

**How it works.** STREAMINFO is the first metadata block, 34 bytes long. It states the smallest and largest block size and frame size of the stream, the sample rate in 20 bits, the channels less 1 in 3 bits, the bits per sample less 1 in 5 bits, and the number of samples of each channel in 36 bits. The block sizes run from 16 to 65,535; the smallest does not count the last frame, which may be shorter. A frame size or a sample count of 0 means the encoder did not know it, as when an encoder cannot seek back to fill the fields in. The frames may leave their own sample rate and bit depth to these fields.

**The MD5.** The last 16 bytes are an MD5 of all the samples of all the channels, interleaved, signed and little-endian, each sample rounded up to whole bytes. An MD5 of 0 means the encoder did not know it. Because it covers decoded audio, it can be compared only after the frames are decoded; the analyzer does so when asked to verify the audio ([verification](verification)).

**What you see.** Each field as a node, "unknown" where a field holds 0, and findings for values the specification does not allow, such as a block size below 16 samples.
