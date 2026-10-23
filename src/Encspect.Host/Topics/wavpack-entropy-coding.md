---
id: wavpack-entropy-coding
title: WavPack entropy coding
related: wavpack-decorrelation, wavpack-hybrid-mode, wavpack-blocks, rice-coding
references: libwavpack 5.6.0, src/read_words.c (the comment at its start), src/write_words.c, src/entropy_utils.c
---

**The problem.** After the predictors, what is left of each sample is a small signed number whose size changes from moment to moment: quiet passages leave tiny residuals, loud ones large. A fixed code wastes bits on one or the other.

**How it works.** WavPack follows the size of each channel's residuals with three running medians, updated after every sample. A value is sent as which zone it falls in (below the first median, between the first and the second, between the second and the third, or beyond the third), as a count of one bits, then its position inside the zone, then its sign. The zones are chosen so that each holds 3.5 times fewer samples than the one before: the medians sit at 5/7, 10/49 and 20/343 of the samples. The comment in libwavpack's source puts a plain Rice code on such data at about 1.4 bits a sample without the sign and this scheme at about 1.22. A count of 16 ones continues in an escape code for large values, and stretches of zeros in near silence are sent as a length. In hybrid mode the position inside the zone is sent only as far as an error limit that the bitrate control works out for each sample, and the correction file holds the rest ([hybrid mode](wavpack-hybrid-mode)).

**What you see.** The Entropy Variables sub-block of each block holds the starting medians, three for each channel as logarithms of 16 bits. The audio bitstream itself is not shown; when you ask the analyzer to verify the audio, it decodes it with this scheme and the [decorrelation](wavpack-decorrelation) passes.
