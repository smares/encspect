---
id: tta-stream
title: The TTA stream
related: tta-seek-table, tta-coding, crc, verification, tta-in-matroska
references: TTA format description (tta.sourceforge.net, "Format Description"); FFmpeg 6.1.1 and libtta-c 2.3 as black-box writers of the files in Encspect's tests
---

**The problem.** A lossless file should let a player start at any second and tell whether its audio is intact, without a complex container.

**How it works.** A TTA1 stream is a 22-byte header, a seek table and the frames. The header begins with the characters "TTA1" and holds, in little-endian numbers, the audio format, the number of channels, the bits per sample, the sample rate, the number of samples in each channel and a CRC-32. Every frame but the last holds the same number of samples, the sample rate times the "frame time" of 256/245, about 1.045 seconds of audio; the last frame holds the rest. A frame is its coded bytes followed by a CRC-32. Frames do not depend on each other, so a decoder can start at any one of them.

**What the files show.** The description does not say how the frame length is rounded or what a CRC-32 covers. In Encspect's test files, written by FFmpeg 6.1.1 and libtta-c 2.3, the frame length is truncated (33,436 samples at 32,000 Hz, not 33,437), and the CRC-32s, computed as zlib does, cover the 18 header bytes before the header's and the coded bytes before a frame's. The analyzer uses these readings.

**Tags and versions.** TTA files carry ID3v1, ID3v2 or APEv2 tags, which the analyzer reads as for any file. The description also lists a second version, "TTA2", as in development; the analyzer reports the signature and reads nothing more.

**Audio format.** The description lists the field without saying what its values mean. In Encspect's test files it is 1, written by FFmpeg 6.1.1 and libtta-c 2.3; libtta-c 2.3 writes 2 for a password-protected stream, which FFmpeg 6.1.1 will not open without the password. That file's frame CRC-32s cover the stored bytes, so the analyzer compares them as usual.

**What you see.** The header's fields and CRC-32 as stored and as computed, the seek table, and every frame with its length, samples and CRC-32. A CRC-32 that differs is a finding. A stream whose CRC-32s all hold, with all the frames its sample count calls for, is verified without decoding ([verification](verification)).
