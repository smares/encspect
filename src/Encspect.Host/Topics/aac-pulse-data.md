---
id: aac-pulse-data
title: AAC pulse data
related: aac-section-data-and-codebooks, aac-raw-data-blocks
references: ISO/IEC 14496-3 4.4.2, 4.5.2
---

**The problem.** A section's Huffman codebook is chosen for the largest value in its bands. One or two large values among small ones would push the whole section to a codebook for large values, or into escapes, and cost many bits.

**How it works.** pulse_data lets an encoder take up to four such values out: it codes the spectrum with the large values reduced, and sends each pulse as an offset in spectral lines from the previous one, starting from a scalefactor band given by pulse_start_sfb, and an amplitude of up to 15 that the decoder adds back to the value's magnitude. Pulse data is only allowed in long windows; ffmpeg's decoder rejects it in eight short windows, and a pulse past the last band.

**What you see.** A channel's HasPulseData flag and Pulse Data with its start band and each pulse's offset and amplitude, and in the statistics the channel streams that use it. None of the encoders whose fixtures the analyzer is checked with (ffmpeg, FAAC, fdk-aac) wrote pulse data into them.
