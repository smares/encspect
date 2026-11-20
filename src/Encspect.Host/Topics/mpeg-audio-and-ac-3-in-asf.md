---
id: mpeg-audio-and-ac-3-in-asf
title: MPEG audio and AC-3 in ASF files
related: asf-packets, waveformatex, mpeg-layers, ac3-frames-and-bsi
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, sections 5.2 and 9.1; Microsoft Windows SDK, mmreg.h (WAVE_FORMAT_MPEGLAYER3, 0x0055)
---

**The problem.** An ASF audio stream names its coding by the format tag of its WAVEFORMATEX, and its media objects hold whatever that coding produces. MPEG audio and AC-3 have frames of their own, with headers that say their rate and channels, so the container's fields and the frames can disagree.

**How it works.** MPEG Layer III is format tag 0x0055, and its WAVEFORMATEX carries twelve more bytes (the MPEGLAYER3WAVEFORMAT of mmreg.h). AC-3 is format tag 0x2000 (mmreg.h calls it WAVE_FORMAT_DVM), or the extensible format (0xFFFE) with the AC-3 sub-format, which ffmpeg writes for six channels. A media object is one or more whole frames, and the analysis hands each to the coding's parser as a packet.

**What you see.** The stream of the coding, as in an MP3 or AC-3 file, under the Data Object. A media object of either coding that is put together from fragments in several packets is read whole, its nodes placed in the pieces of the file it came from. The sample rate and channels of the WAVEFORMATEX are compared with the frames'.

**In Encspect's test files.** ffmpeg's ASF muxer, copying elementary streams, writes the tags above, each media object a whole frame, and no object is fragmented across packets; the frames decode to the same samples as the source file's.
