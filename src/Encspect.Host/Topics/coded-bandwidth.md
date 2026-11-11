---
id: coded-bandwidth
title: Coded bandwidth
related: opus-toc, lowpass, spectral-cutoff, subband-coding, filter-bank, mpeg-layers, huffman-coding, aac-windows-and-blocks, ac3-audio-blocks
references: RFC 6716 section 2, table 1; section 3.1; ISO/IEC 11172-3 2.4.1.5, 2.4.1.6, 2.4.2.5, 2.4.2.6, 2.4.2.7; ISO/IEC 14496-3 4.5.2, 4.6.11; ATSC A/52:2018 5.4.3.24, 7.2.2.1; Annex E 3.3.3
---

**The problem.** Encoders spend no bits on frequencies they filtered out or chose not to code. The coded data says where coding stopped.

**In Layers I and II.** The bit allocation of every frame gives each of the 32 subbands of the [filterbank](filter-bank) a number of bits, or none, which leaves the subband out ([subband coding](subband-coding)). A frame's coded bandwidth is the upper edge of its highest subband with an allocation other than none, in either channel: at 44,100 Hz, subband 26 ends at 18,605 Hz.

**In Layer III.** Each granule's Huffman-coded data ends where its [big values and count1 regions](huffman-coding) end, after the pairs of line values and the quadruples of -1, 0 and 1 the side information and the code state. The analyzer decodes those codes, so it knows how many of the 576 lines a granule and channel codes, and a frame's coded bandwidth is the upper edge of the highest such line over its granules and channels: at 44,100 Hz, line 400 ends at 15,300 Hz. A short block codes three windows of 192 lines, whose bands set the frequency of a line. The count1 region is read a quadruple at a time, so the edge is known to four lines, 153 Hz at 44,100 Hz.

**In AAC.** ics_info gives each channel stream a max_sfb: only that many [scalefactor bands](aac-windows-and-blocks), from the lowest, are coded. A unit's coded bandwidth is the top of the highest of those bands over its channel streams, from the band table of its window at the core's sample rate: at 44,100 Hz a long window with max_sfb 42 codes up to line 736, 15,848 Hz.

**In AC-3 and E-AC-3.** A channel that codes its own exponents sends chbwcod, and its coefficients end at bin 37 + 3 × (chbwcod + 12); a bin is a 512th of the sample rate wide, so chbwcod 60 ends at 23,719 Hz at 48,000 Hz. Coupling and, in E-AC-3, [spectral extension](eac3-spectral-extension) change where a channel's range ends. A program frame's coded bandwidth is the highest end of its [audio blocks](ac3-audio-blocks), the LFE channel left out.

**In Opus.** The TOC byte of every packet names a bandwidth, narrowband to fullband: 4, 6, 8, 12 or 20 kHz (RFC 6716, table 1). The figure is the top of that band.

**A bound the data states.** The value is what the syntax declares, not a measurement of the sound: a band below it can carry very little. Noise substitution and intensity stereo bands count, and SBR or spectral extension adds content above.

**What you see.** The lowest, most common and highest value of a file's units, and a graph of each unit and each second. It is not the [lowpass frequency](lowpass): a LAME tag's is the setting the encoder filtered with. Nor is it the [spectral cutoff](spectral-cutoff), measured from decoded audio: the three can differ.
