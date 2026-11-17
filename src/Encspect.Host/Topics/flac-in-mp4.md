---
id: flac-in-mp4
title: FLAC in MP4
related: flac-stream, flac-streaminfo, flac-metadata-blocks, flac-in-ogg, iso-base-media-format
references: Xiph.org, Encapsulation of FLAC in ISO Base Media File Format; RFC 9639 (FLAC)
---

**The problem.** An MP4 file keeps a coding's settings in the sample entry of its track and the coded data in samples, so FLAC needs a sample entry that says what the stream is and a rule for what a sample holds.

**How it works.** The sample entry has the code fLaC, with the channel count, the bits of a sample and the sample rate in the fields every audio entry has. Its dfLa box holds a version and flags, which are 0, and then the stream's metadata blocks as FLAC's own format lays them out: the STREAMINFO block first, each block with its 4-byte header, the last with the last-block flag. A sample of the track is one FLAC frame, so the frames lie in the media data and the blocks in the movie box.

**What you see.** The dfLa box with its blocks, read as the blocks of a FLAC file are, and the track's frames as the frames of a native FLAC stream, with their checksums. In Encspect's test files, ffmpeg's muxer writes a dfLa box with the STREAMINFO block alone. A finding names an entry that states another sample rate, channel count or bit depth than the STREAMINFO block.
