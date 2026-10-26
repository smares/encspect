---
id: coded-bandwidth
title: Coded bandwidth
related: lowpass, spectral-cutoff, subband-coding, filter-bank, mpeg-layers, aac-windows-and-blocks, ac3-audio-blocks
references: ISO/IEC 11172-3 2.4.1.5, 2.4.1.6, 2.4.2.5, 2.4.2.6; ISO/IEC 14496-3 4.5.2, 4.6.11; ATSC A/52:2018 5.4.3.24, 7.2.2.1; Annex E 3.3.3
---

**The problem.** Encoders spend no bits on frequencies they filtered out or chose not to code. The coded data says where coding stopped, without decoding.

**In Layers I and II.** The bit allocation of every frame gives each of the 32 subbands of the [filterbank](filter-bank) a number of bits, or none, which leaves the subband out ([subband coding](subband-coding)). Each subband is a 64th of the sample rate wide. A frame's coded bandwidth is the upper edge of its highest subband with an allocation other than none, in either channel: at 44,100 Hz, subband 26 ends at 18,605 Hz.

**In AAC.** ics_info gives each channel stream a max_sfb: only that many [scalefactor bands](aac-windows-and-blocks), from the lowest, are coded. A unit's coded bandwidth is the top of the highest of those bands over its channel streams, from the band table of its window at the core's sample rate: at 44,100 Hz a long window with max_sfb 42 codes up to line 736, 15,848 Hz.

**In AC-3 and E-AC-3.** A channel that codes its own exponents sends chbwcod, and its coefficients end at bin 37 + 3 × (chbwcod + 12); a bin is a 512th of the sample rate wide, so chbwcod 60 ends at 23,719 Hz at 48,000 Hz. A coupled channel's own range ends where coupling begins, the coupling channel goes to the end of the coupling range, and in E-AC-3 a channel in [spectral extension](eac3-spectral-extension) ends where that begins. A program frame's coded bandwidth is the highest end of its [audio blocks](ac3-audio-blocks), the LFE channel left out.

**A bound the data states.** The value is what the syntax declares, not a measurement of the sound: a band below it can carry very little. Noise substitution and intensity stereo bands count, and SBR or spectral extension adds content above.

**What you see.** The lowest, most common and highest value of a file's units, and a graph of each unit and each second. It is not the [lowpass frequency](lowpass): a LAME tag's is the setting the encoder filtered with. Nor is it the [spectral cutoff](spectral-cutoff), which is measured from decoded audio, for the streams that have a decoder; three figures that can differ.

**Layer III** has no such field: where its coded spectrum ends lies in Huffman-coded data the analyzer does not walk.
