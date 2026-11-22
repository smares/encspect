---
id: flac-compression-levels
title: FLAC compression levels
related: flac-subframes, flac-frames, flac-channel-assignment, linear-prediction, rice-coding, encoder-detection
references: "flac --help" of libFLAC 1.2.1 to 1.5.0 (BSD-3-Clause), built from its release tarballs; the files libFLAC and FFmpeg 6.1.1's native encoder write in Encspect's tests
---

**The problem.** A better predictor costs the encoder time, so encoders offer a choice between fast and small. A person with a finished file wants to know which choice was made, and the format does not say.

**How it works.** A compression level is a bundle of encoder settings, and none of it is stored. libFLAC's `flac` sets, for each level 0 to 8, the block size (1,152 samples where no linear predictor is used, 4,096 otherwise), the highest [linear predictor](linear-prediction) order (0, 6, 8 or 12), the highest partition order of the [Rice-coded](rice-coding) residual (3 to 6) and how the [channel assignment](flac-channel-assignment) is chosen: not at all, adaptively, or by trying every assignment. Some settings leave no mark in the frames: the window of the analysis that the higher levels try, and the exhaustive model search that levels 7 and 8 of releases up to 1.3.0 add. Level 7 itself changed: up to release 1.3.0 it stops at order 8, from 1.3.1 at order 12.

**FFmpeg.** FFmpeg's native encoder has levels 0 to 12 and sizes its blocks by the sample rate. In Encspect's test files from FFmpeg 6.1.1 levels 0 to 2 write fixed predictors only, levels 3 to 12 linear predictors of precision 15, and the highest order grows with the level (6, 8, 12, then up to 32).

**What you see.** The block size, the predictor orders, the partition orders and the stereo coding in the statistics. The Compression Level node names the levels whose limits the frames fit, with the evidence. Levels that limit the same figures are named together, such as 7 or 8. The claim is of medium confidence where the frames reach the highest orders the levels set, low where they only fit. A file coded with settings of its own, such as another block size or a higher partition order, fits no level and is not named.

**Where it stops.** A stream too short to reach the limits is named by every level it fits. A level is a guess from the settings, not a record of what was run.
