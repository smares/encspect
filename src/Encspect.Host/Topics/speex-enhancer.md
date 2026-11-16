---
id: speex-enhancer
title: Speex perceptual enhancer
related: speex-modes, speex-excitation
references: libspeex 1.2.1 (nb_celp.c, filters.c: multicomb); Speex manual, section 2.1
---

**The problem.** Coding noise between the harmonics of a voiced sound is more audible than the same noise under them. A decoder can reduce it by sharpening the excitation's periodicity after decoding.

**How it works.** libspeex's decoder has an enhancer that is on unless an application turns it off (speexdec turns it on). For the narrowband layer of a submode with a comb gain (every one but 0 and 1), it looks at the decoded excitation of two subframes at a time and finds the lag, to a third of a sample, that matches its pitch. It adds back the excitation one such lag away, and, where the lag is above 40 samples, two lags away, with gains that fall as the match worsens, and then rescales the result to the energy it had. The gain of the comb filter is 0.05 for the highest submode and rises to 0.6 for the lowest that has one.

**In Speex's fixed-point build.** The enhancer halves the samples when any exceeds 16,383 and restores them afterward, so the lowest bit of the excitation it works on is lost then. The enhancer works on the excitation, not on the output, and the output of every analysis that decodes Speex is the enhanced one.

**What you see.** Nothing in the report names the enhancer; it shows only in the decoded samples.
