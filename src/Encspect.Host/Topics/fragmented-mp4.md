---
id: fragmented-mp4
title: Fragmented MP4
related: iso-base-media-format, mp4-sample-tables
references: ISO/IEC 14496-12 8.8, 8.16.3
---

**The problem.** A classic MP4 file describes all its samples in one `moov` box, whose [sample tables](mp4-sample-tables) are complete only when the recording ends. A live stream cannot wait for that, and a recording that is cut off loses its tables.

**How it works.** A fragmented file keeps the tracks in `moov` with empty sample tables and an `mvex` box, whose `trex` boxes give each track's defaults for sample duration, size and flags. Then pairs of boxes follow: a `moof` box that describes the next stretch of samples, and an `mdat` box that holds them. Inside `moof`, a `traf` box per track has a header (`tfhd`) with the track ID, a base offset and defaults; a `tfdt` box with the decoding time of its first sample; and `trun` boxes, runs of samples with each one's size and duration where the defaults do not hold, and a data offset from the base. Each fragment can be written, sent and read on its own.

**Where it is used.** Streaming formats such as MPEG-DASH send fragments as separate segments, often with a segment index (`sidx`). ffmpeg writes fragmented files with -movflags frag_keyframe+empty_moov. An `mfra` box at the end may list the fragments where playback can start.

**What you see.** A `moof` box for each fragment, with its track fragments and runs. The analyzer places each run's samples from the base offset that applies: the one `tfhd` states, the start of `moof`, or the end of the previous track fragment's data. It appends them to the track's samples from `moov` and reports runs that point outside the file.
