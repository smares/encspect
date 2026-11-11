---
id: coded-bandwidth
title: Coded bandwidth
related: opus-toc, lowpass, spectral-cutoff, subband-coding, filter-bank, mpeg-layers, huffman-coding, aac-windows-and-blocks, ac3-audio-blocks, vorbis-floors-and-residues
references: RFC 6716 section 2, table 1; section 3.1; ISO/IEC 11172-3 2.4.1.5, 2.4.1.6, 2.4.2.5, 2.4.2.6, 2.4.2.7; ISO/IEC 14496-3 4.5.2, 4.6.11; ATSC A/52:2018 5.4.3.24, 7.2.2.1; Annex E 3.3.3; Vorbis I specification, section 8.6
---

**The problem.** Encoders spend no bits on frequencies they filtered out or chose not to code. The coded data says where coding stopped.

**In Layers I and II.** The bit allocation of every frame gives each of the 32 subbands of the [filterbank](filter-bank) a number of bits, or none, which leaves the subband out ([subband coding](subband-coding)). The figure is the upper edge of the highest subband with an allocation, in either channel.

**In Layer III.** The analyzer decodes each granule's Huffman data, so it knows how many of the 576 lines the [big values and count1 regions](huffman-coding) code. The figure is the upper edge of the highest of them over the granules and channels.

**In AAC.** ics_info gives each channel stream a max_sfb: only that many [scalefactor bands](aac-windows-and-blocks), from the lowest, are coded. The figure is the top of the highest of those bands over the channel streams.

**In AC-3 and E-AC-3.** A channel that codes its own exponents sends chbwcod, and its coefficients end at bin 37 + 3 × (chbwcod + 12); a bin is a 512th of the sample rate wide. Coupling and, in E-AC-3, [spectral extension](eac3-spectral-extension) change where a channel's range ends. The figure is the highest end over the [audio blocks](ac3-audio-blocks), the LFE channel left out.

**In Vorbis.** A packet's residue codes the spectrum in partitions, and a partition costs bits only where its class has a codebook in some pass ([floors and residues](vorbis-floors-and-residues)). The figure is the upper edge of the highest partition that reads an entry, in any channel, at the packet's block size.

**In Opus.** The TOC byte of every packet names a bandwidth, narrowband to fullband: 4, 6, 8, 12 or 20 kHz (RFC 6716, table 1). The figure is the top of that band.

**A bound the data states.** The value is what the syntax declares, not a measurement of the sound: a band below it can carry very little, noise substitution and intensity stereo bands count, and SBR or spectral extension adds content above.

**What you see.** The lowest, most common and highest value of a file's units, and a graph of each unit and each second. It is neither the [lowpass frequency](lowpass), the setting a LAME tag states, nor the [spectral cutoff](spectral-cutoff), measured from decoded audio: the three can differ.
