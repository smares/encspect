---
id: tak-frames
title: TAK frames
related: tak-metadata, tak-presets, crc, verification
references: TAK 2.3.3 SDK, Sdk/Source/takStream.pas ("Audio frames", "Frame-Info", "Frame-Header"); Takc.exe 2.3.3 as a black-box writer of the files in Encspect's tests
---

**The problem.** A lossless file should let a reader find the audio after damage, start in the middle and check the coded data without decoding it.

**How it works.** The audio is a run of frames. A frame is a header, the coded data and a CRC-24 over the coded data. The header holds the sync word 0xA0FF (the bytes FF A0), flags for the last frame, an info frame and metadata ("not supported"), a 21-bit frame number, for the last frame its sample count, and a CRC-24 over the header. An info frame, which takStream.pas makes obligatory every 2 seconds, also repeats the stream info, so a reader can start there. The header states no length: a frame ends where the next one begins, and the CRC-24 that ends the data is the 3 bytes before that header.

**What the analyzer does.** It finds each frame by its sync word and accepts it when the header's CRC-24 holds or, where it fails, when the frame number is the one expected or the data CRC-24 before it holds. A data CRC-24 that differs and a break in the frame numbers are findings. The coded audio is not read.

**What the files show.** In the 33 files Takc 2.3.3 wrote for Encspect's tests, which last at most a second, every header CRC-24 and every data CRC-24 holds, the first and the last frame are info frames, only the last is flagged as the last frame and states its samples, and no info frame states a previous distance. The CRC-24 over the data covers the bytes between the header's CRC-24 and its own.

**What you see.** Each frame with its header fields and both CRC-24s as stored and as computed, its bitrate in the graphs, and the frames and checksums counted in the statistics. A stream whose CRC-24s all hold, with all the frames its sample count calls for, is verified without decoding ([verification](verification)).
