---
id: replicated-bands
title: Replicated bands
related: sbr, eac3-spectral-extension, celt-folding, spectral-cutoff, spectral-holes, measurements
references: ISO/IEC 14496-3 4.6.18; ATSC A/52:2018 Annex E 3.6.4; RFC 6716 5.3.4.3; Encspect design, docs/design/spectral-analysis.md
---
**The problem.** Some coders rebuild the top of the spectrum from the bottom. [SBR](sbr) in HE-AAC copies the low bands of a filter bank upward, E-AC-3's [spectral extension](eac3-spectral-extension) copies transform coefficients, and Opus's CELT [folds](celt-folding) a lower band into one that got no bits. The decoded spectrum repeats the fine structure of one stretch in another.

**How it works.** In every second frame, the analyzer takes the logarithm of each row's power and subtracts its mean over 150 Hz on each side; what is left is the fine structure under the envelope. A band of 1 kHz matches the band a fixed offset lower in a frame where the two correlate at 0.5 or more. A band passes where it matches at one offset in at least half of the frames with signal. The figure is the highest run of passing bands, found where it reaches the top of the content, with its offsets.

**Periodic sounds.** A sustained harmonic sound repeats its fine structure at every multiple of its pitch. A band that also matches at an offset under 1 kHz in at least half as many frames is left out, so a held note reads as none.

**What lowers it.** The noise a coder adds to the copy, an offset that changes from frame to frame, a copy narrower than a band, and frames with little in the band. The figure cannot tell the mechanism and names no encoder.

**Observations.** In Encspect's test files, HE-AAC streams from qaac 3.07, Winamp's aacPlus 1.3 and Fraunhofer encoders, neroAacEnc 1.5.4.0 and iTunes 12.13 read a stretch starting within 130 Hz of the start their SBR header states (neroAacEnc 1.5.4.0 about 1.4 kHz above it) up to the top of the content, at offsets that are whole multiples of 689 Hz (fs / 64 at 44.1 kHz). AAC LC, MP3, Vorbis and Opus files at 48 kbps read none. A hand-built E-AC-3 file with spectral extension reads the offsets its layout gives. Opus at 16 kbps on a harmonic source reads a stretch at the top, at 32 kbps one below it. One file per setting; the analyzer does not use these.
