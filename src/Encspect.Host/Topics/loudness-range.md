---
id: loudness-range
title: Loudness range
related: loudness, measurements, bwf-broadcast-extension
references: EBU Tech 3342 (Loudness Range, 2023); EBU Tech 3341; ITU-R BS.1770-5
---

**The problem.** Two programs can have the same integrated loudness and still differ a great deal: a commercial stays at one level, a film moves between whispers and explosions. A peak level does not show it, and the difference between the loudest and the quietest moment is decided by single events.

**How it works.** EBU Tech 3342 measures the spread of the short-term loudness.

- The short-term loudness (a window of 3 s, [loudness](loudness)) is taken at least ten times a second; the analyzer takes it every 100 ms.
- Values below -70 LUFS are dropped, then those more than 20 LU below the power average of what is left, so that silence and background noise do not set the lower end.
- The loudness range, in LU, is the difference between the 95th and the 10th percentile of the values that remain. The lower percentile keeps a fade-out from deciding the result, the upper one a single gunshot.

The analyzer keeps the values in bins of 0.01 LU, so its percentiles are exact to that resolution; Tech 3342 allows a tolerance of 1 LU.

**What you see.** Loudness Range in LU, computed from the decoded audio. A stream shorter than 3 s has none; a stream whose short-term loudness is above +30 LUFS reads +30 LUFS for both percentiles, the top of the analyzer's bins. It is a measured figure; a Broadcast Wave bext chunk states its own loudness range, which is another figure. It is not the DR value of other meters, which Encspect does not measure.
