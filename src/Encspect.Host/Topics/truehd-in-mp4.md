---
id: truehd-in-mp4
title: Dolby TrueHD in MP4
related: truehd-access-units, truehd-substreams, iso-base-media-format
references: Encspect's test files, written by ffmpeg 6.1.1; docs/design/truehd-format.md
---

**The problem.** The access units of a Dolby TrueHD stream carry their parameters in the major sync, which only some units have, while an MP4 reader wants the stream's rate and channels from the movie box before it reads a sample.

**How it works.** The sample entry has the code mlpa, and a dmlp box repeats what the stream's first major sync says: its 32 bits of format information, the peak data rate and reserved bits. A sample of the track is one access unit. The entry's channel count and sample rate are those of the stream; in the files ffmpeg's muxer writes, the sampling rate is a 32-bit integer where audio entries hold a 16.16 fixed-point number, so read as one it is a fraction of a hertz.

**What you see.** The dmlp box's fields as nodes, and the access units read as the units of a raw .thd file are, with their checksums and the lossless check. A finding names a dmlp box that is shorter than its fields, or whose fields differ from the first major sync's, and an entry whose rate or channel count differs from the stream's. The samples of an encrypted track are not read.
