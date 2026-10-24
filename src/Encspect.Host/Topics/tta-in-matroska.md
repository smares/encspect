---
id: tta-in-matroska
title: TTA in Matroska
related: tta-stream, tta-seek-table, matroska-tracks, crc
references: Matroska codec mapping, A_TTA1 (matroska.org, "Codec Mappings"); FFmpeg 6.1.1 as a black-box muxer of the file in Encspect's tests
---

**The problem.** A container already knows a track's sample rate, channels and sample size, and it has its own index, so the header and the seek table of a TTA file repeat what it holds.

**How it works.** The codec mapping for A_TTA1 says each Matroska frame is a TTA frame kept intact, with its CRC-32, and that the header and the seek table are dropped. The track's SamplingFrequency, Channels and BitDepth give the figures the header held. The mapping also says the CodecPrivate contains the TTA header structure, the 22 bytes of the header. In Encspect's test file, written by FFmpeg 6.1.1, there is no CodecPrivate: the muxer leaves it out, and FFmpeg builds the 22-byte header from the track's figures when it reads the file.

**What you see.** Each frame is read as it is in a TTA file, from its packet, and its CRC-32 is compared. Where the CodecPrivate holds the header, it is shown at its bytes with its CRC-32, and the sample count in it tells whether all the frames the stream calls for are there, which a stream verified by its CRC-32s needs. Without it the sample rate, channels and bits come from the track, the frames are compared, and the stream is not called verified, since no sample count says how many frames there should be.
