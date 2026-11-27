---
id: long-term-prediction
title: Long-term prediction (LTP)
related: aac-object-types, aac-windows-and-blocks, tns, aac-prediction
references: ISO/IEC 14496-3 4.4.2, 4.6.7 (Table 4.152), 4.B.10
---

**The problem.** A voiced sound or a sustained note repeats with the period of its pitch, so a window of it resembles the output the decoder has already produced one or more periods earlier. AAC Main's predictor looks at single spectral lines across frames and cannot use that; long-term prediction works on the time signal.

**How it works.** The decoder takes its own earlier output at a lag the stream sends (0 to 2,047 samples), scales it by one of eight coefficients (Table 4.152, 0.57 to 1.37), windows it as the block is windowed and transforms it with a forward MDCT into a predicted spectrum. In every scalefactor band whose ltp_long_used flag is set, up to band 40, that spectrum is added to the decoded one, so the stream only codes what the prediction missed. The part of the lag that reaches into the window being decoded comes from the overlap, the aliased half of the last window's transform. A block with temporal noise shaping passes the predicted spectrum through the TNS analysis filter first, since the TNS synthesis filter follows. Where a band codes noise substitution the noise wins, and no prediction is added.

**What you see.** In each long window of an AAC LTP stream, ltp_data_present, and where it is set the lag, the coefficient index and one flag for each band. The statistics count the channel streams that use it and the share of bands.

**Only long windows.** The tool is restricted to long, long start and long stop windows (4.6.7.1); eight short windows carry no ltp_data. A coupling channel element switched dependently may not appear in a stream of this object type (4.6.7.4.3).

**Where it is specified.** ISO/IEC 14496-3 4.6.7. Its encoder side is described in 4.B.10.
