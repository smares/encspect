---
id: replicated-bands
title: Replicated bands
related: sbr, eac3-spectral-extension, celt-folding, spectral-cutoff, spectral-holes, measurements
references: ISO/IEC 14496-3 4.6.18; ATSC A/52:2018 Annex E 3.6.4; RFC 6716 5.3.4.3; Encspect design, docs/design/spectral-analysis.md
---
**The problem.** Some coders rebuild the top of the spectrum from the bottom. [SBR](sbr) in HE-AAC copies the low bands of a filter bank upward, E-AC-3's [spectral extension](eac3-spectral-extension) copies transform coefficients and Opus's CELT [folds](celt-folding) a lower band into one that got no bits. SBR and spectral extension leave the fine structure of one stretch in another.

**How it works.** In every second frame, the analyzer takes each row's log power less its mean over 150 Hz on each side: the fine structure under the envelope. A band of 1 kHz (at least 21 rows) matches the band a fixed offset lower in a frame where the two correlate at 0.5 or more; a frame counts only if the band is new in it, not a repeat of the previous frame, so a steady sound is one trial. A band passes where it matches at one offset in half of its counted frames. The figure is the highest run of passing bands, found where it reaches the top of the content.

**Periodic sounds.** A sustained harmonic sound repeats its fine structure at every multiple of its pitch. A band that also matches at a small offset (under 1 kHz) in half as many frames is left out.

**What lowers it.** Noise added to the copy, an offset that changes from frame to frame, a copy narrower than a band. The figure names no mechanism and no encoder.

**Folding is not detected.** CELT normalizes and spreads the band it folds, so its fine structure correlates too weakly with the source: 0 of 42 Opus files with CELT frames read found.

**Observations.** In Encspect's test files, HE-AAC streams read a stretch up to the top of the content, at offsets that are whole multiples of 689 Hz. It starts within 130 Hz of the start the SBR header states for Winamp's aacPlus 1.3, Fraunhofer and iTunes 12.13, and within 260 Hz under it for qaac 3.07; neroAacEnc 1.5.4.0's HE-AAC mono file starts about 1.5 kHz above it, and its HE-AAC v2 file is an exception: one band from 14,556 Hz. AAC LC, MP3, Vorbis and Opus files at 48 kbps read none. A hand-built E-AC-3 spectral extension file reads the offsets its layout gives. One file per setting; the analyzer does not use these.
