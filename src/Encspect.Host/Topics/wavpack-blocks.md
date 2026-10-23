---
id: wavpack-blocks
title: WavPack blocks
related: wavpack-sub-blocks, wavpack-hybrid-mode, wavpack-audio-checksums, wavpack-in-matroska, mid-side-stereo, channel-layouts, crc, ape-tag
references: WavPack 4 and 5 binary file/block format (David Bryant, April 2020), sections 1.0 and 2.0; libwavpack 5.6.0, include/wavpack.h and src/open_utils.c
---

**The problem.** A lossless file of hours has to be played from any point and must survive damage, ideally without a table that says where its parts are.

**How it works.** A WavPack file is a run of blocks, each starting with a 32-byte header: the characters "wvpk", the block's size, the stream version, the index of its first sample, the number of samples in it, flags and a CRC of the decoded audio ([checks over the decoded audio](wavpack-audio-checksums)). The index and the file's total sample count take 40 bits each. A block decodes by itself, to one or two channels. More channels repeat a set of blocks, a stereo block for each pair and a mono block for each other channel, the first flagged initial and the last final: a 5.1 file has four blocks in a set. A block without samples holds metadata only. There is no seek table; a player reads headers.

**Correction files.** A hybrid file may come with a second file, .wvc, of the same blocks, whose headers differ from the main file's only in size and CRC ([hybrid mode](wavpack-hybrid-mode)).

**What you see.** Each block with its header fields and sub-blocks ([sub-blocks](wavpack-sub-blocks)). There is no magic number at the start, so the analyzer accepts a header only where its checks hold (an even size under 1 MiB, major version 4, fewer than 196,608 samples, as libwavpack checks) and the block ends at another such header or at the end of the data. Bytes between blocks, a block that states a size it does not have and gaps in the block indexes are findings. Tags, normally APEv2 or ID3v1, follow the last block.
