---
id: coded-bandwidth
title: Coded bandwidth
related: lowpass, subband-coding, filter-bank, mpeg-layers, aac-windows-and-blocks
references: ISO/IEC 11172-3 2.4.1.5, 2.4.1.6, 2.4.2.5, 2.4.2.6; ISO/IEC 14496-3 4.5.2, 4.6.11
---

**The problem.** Encoders spend no bits on frequencies they filtered out or chose not to code. The coded data says where coding stopped, so the analyzer can show a stream's reach without decoding it.

**In Layers I and II.** The bit allocation of every frame gives each of the 32 subbands of the [filterbank](filter-bank) a number of bits, or none, which leaves the subband out ([subband coding](subband-coding)). Subbands are equal slices, each a 64th of the sample rate wide. The coded bandwidth of a frame is the upper edge of its highest subband with an allocation other than none, in either channel: at 44,100 Hz, subband 26 ends at 18,605 Hz.

**In AAC.** ics_info gives each channel stream a max_sfb: only that many [scalefactor bands](aac-windows-and-blocks), from the lowest, are coded; the bands above are zero. The coded bandwidth of an access unit or ADTS frame is the top of the highest of those bands over its channel streams, from the band table of its window (long, or short for eight short windows) at the core's sample rate: at 44,100 Hz a long window with max_sfb 42 codes up to line 736, 15,848 Hz.

**A bound the data states.** The value is what the syntax declares, not a measurement of the sound: a subband or band below the bound can carry very little. AAC's noise substitution and intensity stereo bands count as coded, and SBR adds content above the core, which this does not show.

**What you see.** The statistics give the lowest, most common and highest value of a file's units; the graph shows each unit and each second.

**Not the lowpass.** The [lowpass](lowpass) in a LAME tag is the setting the encoder filtered with, the coded bandwidth is where the coded data reaches, and a lowpass measured from decoded audio would need a decoder.

**Layer III** has no such field in its side information. Where its coded spectrum ends lies in Huffman-coded data the analyzer does not walk, so Layer I and II files only get the statistics and graph.
