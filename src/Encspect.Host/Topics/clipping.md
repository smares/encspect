---
id: clipping
title: Clipping runs
related: levels, digital-silence, bit-depth-usage
references: Encspect design, docs/design/spectral-analysis.md (Levels; Step 6)
---

**The problem.** A format holds samples only up to its largest and smallest code. A signal beyond them is cut off and stays at the limit while it would be beyond it.

**How it works.** The analyzer counts runs of consecutive samples at the same extreme. A run counts from three samples on, a convention of this analysis and not of a specification: one sample can be a peak that touches the limit, and two a coincidence. The figures are the runs, the samples in them and the longest.

**The extremes of the bits in use.** The most negative code, -2^(B - 1), has every low bit 0, so it is the same however many low bits are unused. The most positive code is 2^(B - 1) less 2^k when k low bits are never set ([bit-depth usage](bit-depth-usage)): 16-bit audio in 24-bit samples tops out at 0x7FFF00. The analyzer counts at the extremes of the bits in use, the declared width less the stream's Low Bits Unused, so both signs count; a positive run counted earlier is dropped when later samples show fewer bits in use. This holds from 8 bits in use on; with fewer, the zeros come more often from the signal than from padding. Floats whose samples are all 16-bit or all 24-bit values use the extremes of that grid, 1 - 2^-15 or 1 - 2^-23, and -1.0; other floats count at or beyond full scale.

**What you see.** In Encspect's test files, 16-bit noise shifted left by 8 in 24-bit samples, with runs at 0x7FFF00 and -0x800000, reads 4 runs, 60 samples and a longest of 50 at 16 bits in use; a run at 0x7FFE00 is not counted.

**What is not covered.** G.711 streams are not covered. A float sample that is NaN or infinite is counted apart (a finding) and read as 0, so it is never part of a run, whatever its sign. A lossyWAV stream zeroes a different number of low bits per block; a block with more zeroed bits than the stream's clips below its positive extreme and is not counted there.

**What it shows.** Runs at the limit fit a signal that was limited or cut there, and a full-scale square wave reads the same.
