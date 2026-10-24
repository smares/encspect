---
id: tta-stream
title: The TTA stream
related: tta-seek-table, tta-coding, crc, verification, tta-in-matroska
references: TTA format description (tta.sourceforge.net, "Format Description"); FFmpeg 6.1.1 as a black-box reader and writer of the files in Encspect's tests
---

**The problem.** A lossless file should let a player start at any second and tell whether its audio is intact, without a complicated container.

**How it works.** A TTA1 stream is a 22-byte header, a seek table and the frames. The header begins with the characters "TTA1" and holds, in little-endian numbers, the audio format, the number of channels, the bits per sample, the sample rate, the number of samples in each channel and a CRC-32 over the 18 bytes before it. Every frame but the last holds the same number of samples: the sample rate times 256/245, about 1.045 seconds of audio, truncated to a whole number. The last frame holds the rest. A frame is its coded bytes followed by a CRC-32 over them. Frames do not depend on each other, so a decoder can start at any one of them.

**Tags and versions.** The description says TTA files carry ID3v1, ID3v2 or APEv2 tags; the analyzer reads them as it does for any file. It also lists a second version, "TTA2", with another header and its seek table at the end, as in development; the analyzer reports the signature and reads nothing more.

**Audio format.** The description lists the field without saying what its values mean. In Encspect's test files, written by FFmpeg 6.1.1, it is 1, and FFmpeg reads 2 as an encrypted stream that needs a password.

**What you see.** The header's fields and the CRC-32 as stored and as computed, the seek table, and every frame with its length, samples and CRC-32. A CRC-32 that differs is a finding. A stream whose header and seek table CRC-32s and every frame CRC-32 hold, with all the frames its sample count calls for, is verified without decoding ([verification](verification)).
