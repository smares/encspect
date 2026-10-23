---
id: aac-windows-and-blocks
title: AAC windows, block switching and grouping
related: block-switching, coded-bandwidth, tns, aac-section-data-and-codebooks, aac-raw-data-blocks
references: ISO/IEC 14496-3 4.4.2, 4.5.2, 4.6.11; ffmpeg libavcodec/aacdec_template.c
---

**The problem.** AAC transforms each frame into 1024 frequency lines over a window of 2048 samples that overlaps its neighbors by half. Quantization noise spreads over the whole window, so ahead of a sudden attack it can be heard as pre-echo, as in MP3 [block switching](block-switching).

**Window sequences.** ics_info gives each channel's window_sequence: one long window (only long), eight short windows of 128 lines each (eight short), and the transitions between them, long start before short windows and long stop after them, whose shapes let the overlapping halves match. [TNS](tns) is a second tool against pre-echo, within a long window.

**Window shapes.** window_shape picks the shape of the window's slopes: a sine window or a Kaiser-Bessel derived (KBD) window. Each frame's bit sets the shape of its window's second half; the first half keeps the previous frame's.

**Observed choices.** In Encspect's test files, and not used by the analyzer to name an encoder:
- Apple's encoder (AudioToolbox, macOS 15.7.9) wrote sine windows, except in three files encoded at `-q 127`, its maximum quality, where 84% to 100% of the channel streams use KBD.
- On short test signals (about 1.5 s, with one or two transitions), fdkaac 1.0.0 and ffmpeg's AAC encoder (Lavc60.31.102) used KBD in long and stop windows and sine in start and short windows. How much of a file is KBD depends on the signal.
- FAAC 1.30 to 2.2 wrote sine only.

**Grouping.** Eight short windows code their spectra one after another, and scale_factor_grouping joins neighboring windows into groups that share one set of sections and scalefactors, which saves bits where the windows are alike.

**max_sfb.** Only the lowest max_sfb scalefactor bands are coded; the bands above are zero. The top of the highest of them is the [coded bandwidth](coded-bandwidth).

**What you see.** Each channel's ICS Info with its window sequence, shape, max_sfb and grouping; in the statistics the window sequences and shapes per channel stream, the max_sfb distributions and the coded bandwidth.

**The graphs.** Window Sequences shows the sequence of every channel stream of every access unit or frame, and Block Sizes groups them as long, with the start and stop windows, and short: the share of each per second, and a strip with a cell per unit.
