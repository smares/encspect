---
id: pcm-in-mp4
title: PCM in MP4 and QuickTime files
related: pcm-samples, iso-base-media-format, channel-layouts
references: ISO/IEC 23003-5 (ipcm, fpcm, pcmC); QuickTime File Format, Sound sample descriptions
---

**The problem.** Uncompressed audio in an MP4 file needs the sample entry to say what WAV's format chunk says: the width of a sample, whether it is an integer or a float, and which byte comes first.

**How it works.** An ipcm entry holds integers and an fpcm entry floats. Its pcmC box gives the bits of a sample and, in bit 0 of its format flags, whether the bytes are little-endian (ISO/IEC 23003-5); a chnl box may give the speakers. QuickTime's entries say it by their code: sowt is 16-bit little-endian, twos is 8 or 16-bit big-endian, raw is 8-bit unsigned, in24 and in32 are 24 and 32-bit integers, fl32 and fl64 are floats, and an enda box in the entry's wave box makes the last four little-endian. A linear PCM entry (lpcm) of sound description version 2 gives the same in format flags: float, big-endian, signed, packed.

**What you see.** The entry's boxes, and the stream as a WAV file's: sample frames, bits, byte order, rate and channels. A sample of the track is one or more whole sample frames, so the frames are counted from the bytes; a finding names bytes that do not fill a frame. In Encspect's test files, ffmpeg's muxer writes ipcm and fpcm entries in MP4 files and the QuickTime codes in .mov files. An entry that does not state its layout (ipcm without a pcmC box) is named and not analyzed.
