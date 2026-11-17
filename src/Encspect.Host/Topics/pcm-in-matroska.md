---
id: pcm-in-matroska
title: PCM in Matroska files
related: pcm-samples, matroska-tracks, channel-layouts
references: draft-ietf-cellar-codec, section 3.4 (A_PCM/INT/LIT, A_PCM/INT/BIG, A_PCM/FLOAT/IEEE); RFC 9559, section 5.1.4.1.29 (Audio)
---

**The problem.** Uncompressed audio in a Matroska file needs the track to say what WAV's format chunk says: the width of a sample, whether it is an integer or a float, and which byte comes first. A track has no format chunk, so the codec ID and the Audio element carry it.

**How it works.** Three codec IDs name the coding: A_PCM/INT/LIT for integers with the least significant byte first, A_PCM/INT/BIG for integers with the most significant byte first, and A_PCM/FLOAT/IEEE for IEEE 754 floats (draft-ietf-cellar-codec, section 3.4). The width of a sample comes from the Audio element's BitDepth, so a track without one cannot be read as samples. The track has no CodecPrivate. SamplingFrequency defaults to 8000 Hz and Channels to 1 (RFC 9559, section 5.1.4.1.29). The samples are counted from the bytes of the track's blocks.

**What you see.** The track's frames as one node with the statistics of a WAV file: sample frames, bits, byte order, rate and channels. Bytes that do not fill a whole sample frame are a finding, as in WAV.

**In Encspect's test files.** FFmpeg 6.1.1 writes A_PCM/INT/LIT for 16, 24 and 32-bit little-endian integers and for 8-bit samples, A_PCM/INT/BIG for 16-bit big-endian integers and A_PCM/FLOAT/IEEE for 32 and 64-bit floats; mkvmerge 82.0 writes A_PCM/INT/LIT from a WAV file. Encspect reads 8-bit samples as unsigned, as WAV does, and the floats as little-endian; FFmpeg decodes these files to the same samples. The analyzer applies the layout the codec ID and the BitDepth give, and no other rule.
