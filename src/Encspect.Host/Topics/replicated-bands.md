---
id: replicated-bands
title: Replicated bands
related: sbr, eac3-spectral-extension, celt-folding, spectral-cutoff, spectral-holes, measurements
references: ISO/IEC 14496-3 4.6.18; ATSC A/52:2018 Annex E 3.6.4; RFC 6716 5.3.4.3; Encspect design, docs/design/spectral-analysis.md
---
**The problem.** Some coders rebuild the top of the spectrum from the bottom. [SBR](sbr) in HE-AAC copies the low bands of a filter bank upward, E-AC-3's [spectral extension](eac3-spectral-extension) copies transform coefficients, and Opus's CELT [folds](celt-folding) a lower band into one that got no bits. The decoded spectrum repeats the fine structure of one stretch in another.

**How it works.** In every second frame, the analyzer subtracts from the logarithm of each row's power its mean over 150 Hz on each side: the fine structure under the envelope. A band of 1 kHz (at least 21 rows) matches the band a fixed offset lower in a frame where the two correlate at 0.5 or more in a frame that is new for the band: a steady sound is one trial, not many. A band passes where it matches at one offset in at least half of the frames with signal. The figure is the highest run of passing bands, found where it reaches the top of the content, with its offsets.

**Periodic sounds.** A sustained harmonic sound repeats its fine structure at every multiple of its pitch. A band that also matches at an offset under 1 kHz in at least half as many frames is left out.

**What lowers it.** The noise a coder adds to the copy, an offset that changes from frame to frame, and a copy narrower than a band. The figure names no mechanism and no encoder.

**Observations.** In Encspect's test files, HE-AAC streams read a stretch up to the top of the content, at offsets that are whole multiples of 689 Hz. It starts within 130 Hz of the start the SBR header states for Winamp's aacPlus 1.3, Fraunhofer and iTunes 12.13, and within 260 Hz under it for qaac 3.07; neroAacEnc 1.5.4.0's HE-AAC mono file starts about 1.5 kHz above it, and its HE-AAC v2 files are an exception: one band from 14,556 Hz. AAC LC, MP3, Vorbis and Opus files at 48 kbps read none. A hand-built E-AC-3 file with spectral extension reads the offsets its layout gives; Dolby's own file reads under the threshold. Opus on a harmonic source reads stretches below the top. One file per setting; the analyzer does not use these.
