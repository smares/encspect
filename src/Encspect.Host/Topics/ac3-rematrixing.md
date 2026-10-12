---
id: ac3-rematrixing
title: Rematrixing in AC-3
related: mid-side-stereo, ac3-coupling, ac3-audio-blocks
references: ATSC A/52:2018 5.4.3.19, 5.4.3.20, Table 5.15, 7.5; Annex E 3.3.2
---

**The problem.** In a 2/0 program, left and right often carry nearly the same signal. Coding both spends bits twice on what they share, and the coding noise of each channel may stand out where the two are almost identical.

**How it works.** Rematrixing is AC-3's form of [mid/side stereo](mid-side-stereo). The spectrum is split into up to four rematrixing bands, and for each band the encoder chooses to code left and right, or their sum and difference. A decoder turns sum and difference back into left and right; the conversion itself loses nothing. Rematrixing exists only in 2/0, the one audio coding mode with exactly a left and a right channel and no others. With coupling in use, the bands stop where coupling begins, so there are four, three or two of them.

**What you see.** In each block of a 2/0 program, rematstr says whether new flags follow, and rematflg gives each band as left/right or sum/difference. The statistics count the blocks with a band coded as sum/difference and how many bands of all blocks were.
