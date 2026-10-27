---
id: clipping
title: Clipping runs
related: levels, digital-silence, bit-depth-usage
references: Encspect design, docs/design/spectral-analysis.md (Levels)
---

**The problem.** A format holds samples only up to its largest and smallest code. A signal that would go beyond them is cut off at that limit, and the samples stay at the limit for as long as the signal would have been beyond it.

**How it works.** The analyzer counts runs of consecutive samples that sit at the same extreme: the most positive or the most negative integer code, or, for floats, at or beyond full scale. A run counts from three samples on. Three is a convention of this analysis, not of any specification: a single sample at the limit can be a peak that touches it, and two can be a coincidence. The figures are the number of runs, the samples in them and the longest. A run ends at a gap in the decoded blocks, and runs of opposite extremes side by side are two runs of their own.

**What you see.** Clipping in the report and the clipping object in JSON. In Encspect's test files, a square wave that alternates every 50 samples between +32,767 and -32,768 reads 100 runs of 50 over 5,000 samples, and noise with one run of 100 samples, five of 3 to 5 samples, and runs of 1 and 2 samples that are not counted reads 6 runs, 118 samples and a longest of 100.

**What it shows.** Runs at the limit are consistent with a signal that was limited or cut at that level, and a legitimate full-scale square wave reads the same. The figure does not say where the cut was made or by what, and a signal cut before a later gain change leaves no run at the limit.
