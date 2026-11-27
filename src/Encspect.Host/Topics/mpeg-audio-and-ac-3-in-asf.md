---
id: mpeg-audio-and-ac-3-in-asf
title: MPEG audio, AC-3 and PCM in ASF files
related: asf-packets, waveformatex, mpeg-layers, ac3-frames-and-bsi, pcm-samples
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, sections 5.2 and 9.1; Microsoft Windows SDK, mmreg.h (WAVE_FORMAT_MPEGLAYER3, 0x0055; WAVE_FORMAT_MPEG, 0x0050; WAVE_FORMAT_PCM, WAVE_FORMAT_IEEE_FLOAT, WAVE_FORMAT_ALAW, WAVE_FORMAT_MULAW)
---

**The problem.** An ASF audio stream names its coding by the format tag of its WAVEFORMATEX, and its media objects hold whatever that coding produces. MPEG audio and AC-3 have frames of their own, with headers that say their rate and channels, so the container's fields and the frames can disagree. PCM has no frames, and the WAVEFORMATEX is the only statement of its layout.

**How it works.** MPEG Layer III is format tag 0x0055, and its WAVEFORMATEX carries twelve more bytes (the MPEGLAYER3WAVEFORMAT of mmreg.h); MPEG Layer I and II are format tag 0x0050, with the MPEG1WAVEFORMAT after it. Both go to the MPEG audio parser, which reads the frames' own headers. PCM is format tag 0x0001, IEEE floating point 0x0003, A-law 0x0006 and mu-law 0x0007 (the table WAV and Matroska's A_MS/ACM route by), which the PCM parser reads from the WAVEFORMATEX. AC-3 is format tag 0x2000 (mmreg.h calls it WAVE_FORMAT_DVM), or the extensible format (0xFFFE) with the AC-3 sub-format, which ffmpeg writes for six channels. A media object is one or more whole frames, and the analysis hands each to the coding's parser as a packet.

**What you see.** The stream of the coding, as in an MP3, MP2 or AC-3 file, under the Data Object; for PCM, a stream with the statistics of a WAV file's, under the stream's media objects. A WAVEFORMATEX the PCM parser cannot read leaves the stream unanalyzed, with the reason in a finding. A media object of MPEG audio or AC-3 that is put together from fragments in several packets is read whole, its nodes placed in the pieces of the file it came from. The sample rate and channels of the WAVEFORMATEX are compared with the frames'.

**In Encspect's test files.** ffmpeg's ASF muxer, copying elementary streams, writes the tags above (0x0050 for its Layer II file, the four PCM tags for its PCM files), each media object a whole frame or a run of whole sample frames, the PCM objects of the longer files fragmented across packets; the samples decode to the same ones as the source file's.
