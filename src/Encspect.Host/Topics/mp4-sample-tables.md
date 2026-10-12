---
id: mp4-sample-tables
title: MP4 sample tables
related: iso-base-media-format, fragmented-mp4, audio-specific-config, edit-lists-and-priming
references: ISO/IEC 14496-12 8.5.1, 8.5.2, 8.6.1.2, 8.6.2, 8.7.3, 8.7.4, 8.7.5
---

**The problem.** The media data of an MP4 file is a stretch of bytes without headers. For audio, each sample of a track is one access unit, such as an AAC frame of 1024 samples per channel. A player has to know where each one starts, how long it is and when it plays.

**How it works.** The sample table box `stbl` of each track answers with a few compact tables:

- `stsd`, the sample descriptions: how the samples are coded, such as `mp4a` with an [AudioSpecificConfig](audio-specific-config) in its `esds` box.
- `stts`, decoding time to sample: runs of samples that last the same time, so a constant frame length takes one entry.
- `stsz`, the size of every sample, or one size for all.
- `stco` or `co64`, the file offset of every chunk. A chunk is a run of samples stored back to back.
- `stsc`, sample to chunk: from which chunk on each chunk holds how many samples.
- `stss`, the sync samples a decoder can start at; without it, every sample is one.

To find a sample, a reader looks up its chunk through `stsc`, takes the chunk's offset from `stco`, and adds the sizes of the samples before it in that chunk from `stsz`. Its time is the sum of the durations before it in `stts`.

**What you see.** Each table with its first 1000 entries and a More Entries node for the rest, which are still read. The analyzer locates every sample this way and hands the samples to the codec's parser, whose nodes show each access unit. Findings report tables that disagree on the number of samples or place samples outside the file.
