---
id: flac-compression-levels
title: FLAC compression levels
related: flac-subframes, flac-frames, flac-channel-assignment, linear-prediction, rice-coding, encoder-detection
references: "flac --help" and the source of libFLAC 1.1.0 to 1.5.0 (BSD-3-Clause), built from its release tarballs; the files libFLAC and 15 releases of FFmpeg's native encoder (0.11.5 to 8.0.1) write in Encspect's tests
---

**The problem.** A better predictor costs the encoder time, so encoders offer a choice between fast and small. A person with a finished file wants to know which choice was made, and the format does not say.

**How it works.** A compression level is a bundle of encoder settings, and none of it is stored. libFLAC's `flac` sets, for each level 0 to 8, the block size (1,152 samples where no linear predictor is used, 4,096 otherwise, 4,608 up to release 1.1.3), the highest [linear predictor](linear-prediction) order (0, 6, 8 or 12), the highest partition order of the [Rice-coded](rice-coding) residual (3 to 6) and how the [channel assignment](flac-channel-assignment) is chosen: not at all, adaptively, or by trying every assignment. Level 7 itself changed: up to release 1.3.0 it stops at order 8, from 1.3.1 at order 12. The adaptive levels (1 and 4) choose the stereo coding in a simpler way than the others, which rules some frames out.

**FFmpeg.** FFmpeg's native encoder has levels 0 to 12 and sizes its blocks by the sample rate. In Encspect's test files from FFmpeg 0.11.5 to 8.0.1 levels 0 to 2 write fixed predictors only, levels 3 to 12 linear predictors of precision 15, and the highest order grows with the level (6, 8, 12, then up to 32).

**What you see.** The block size, the predictor orders, the partition orders and the stereo coding in the statistics. The Compression Level node names the encoder and every level whose limits the frames fit, with the evidence, such as "libFLAC 7 or 8": levels that limit the same figures go together, and a level above is never left out because the frames do not use its extra room. The claim is of medium confidence where the frames reach the highest orders of the lowest level they fit, or where they exceed the limits of every level but one group; low where they only fit. A file coded with settings of its own, such as another block size or a higher partition order, fits no level and is not named.

**Where it stops.** A level is a guess from the settings, not a record of what was run.
