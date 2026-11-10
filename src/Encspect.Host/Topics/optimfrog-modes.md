---
id: optimfrog-modes
title: OptimFROG modes and presets
related: optimfrog-blocks, encoder-detection, bitrate-modes
references: OptimFROG 5.100 SDK, SDK/Documentation/format.txt (appendix D) and changelog.txt; `ofr --help` of 5.100 for the presets; OptimFROG 5.100 as a black-box writer of the files in Encspect's tests
---

**The problem.** People comparing files want to know how hard an encoder worked; the main block keeps a compression byte for that.

**How it works.** The byte holds the mode in its upper five bits and the speedup in its lower three (format.txt, appendix D). The modes are fast, normal, high, extra, best, ultra, insane, highnew, extranew, bestnew, ultranew, extrafast, turbonew, fastnew and normalnew; the speedups are 1x, 2x and 4x. The changelog of 4.506 says the encoder writes the exact mode for information only, and that the speedup is deprecated. A decoder does not need either.

**What the files show.** In Encspect's test files from OptimFROG 5.100:

- Every file stores the speedup 4x.
- The modes the specification lists last are not what the command line's names give: `--mode turbonew`, `fastnew`, `normalnew`, `extra` and `extrafast` all store "extra"; `highnew` and `highnew-light` store "highnew", and likewise for extranew, bestnew and ultranew with their `-light` forms.
- The presets 0 and 1 store "fast", 2 "normal", 3 "high", 4 to 6 "extra", 7 "highnew", 8 "extranew", 9 "bestnew", 10 and max "ultranew". The presets set options the header does not store, so the report names a mode, never a preset.
- The two bytes in each data block that the specification calls reserved differ between these files: 0x42 or 0x41 in the low byte for the older modes, 0xC2 for the newer ones. The analyzer shows them; it uses only the low byte, as described under DualStream below.

**DualStream.** In the files of `ofs` 5.100 in Encspect's tests the two reserved bytes of every data block read 0x1101 (stereo) or 0x0901 (mono), and never so in a lossless file. The analyzer uses the low byte (0x01) alone, not the high byte or any pattern of the modes, as a low-confidence sign of the lossy part of a DualStream, medium when the file name ends in .ofs too; the pairing with the correction file in a later release will confirm it.

**What you see.** The mode and speedup as stored, with this caveat in their descriptions. Files from before 4.506 can store "extra" for what the encoder called extranew.
