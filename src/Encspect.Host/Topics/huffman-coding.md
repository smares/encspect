---
id: huffman-coding
title: Huffman coding of the spectrum
related: granules-and-frames, global-gain, scalefactors, bit-reservoir
references: ISO/IEC 11172-3 2.4.2.7, 2.4.3.4, Annex B
---

**The idea.** After quantization, most of a granule's 576 frequency lines are small numbers, and the high ones are often zero. Huffman coding gives frequent values short codes and rare ones long codes, so this typical spectrum takes few bits.

**Three regions.** Layer III splits the lines of each granule, from low to high frequencies, into three regions:

- the **big values** region, coded in pairs of lines, where values may be large; big_values gives the number of pairs;
- the **count1** region, coded in groups of four lines whose values are only -1, 0 or 1, with table A or B (count1table_select);
- the **rzero** region, the rest, which is all zero and takes no bits.

**The region split.** The big values region is split again into up to three parts, and each part picks one of the Huffman tables of the standard with table_select, since low and high frequencies have different statistics. region0_count and region1_count give where the parts end, in scalefactor bands. Tables for large values add extra bits (linbits) for values above 15.

**How long.** part2_3_length gives the bits of a granule's scalefactors and Huffman code together, so the decoder knows where the next granule's data starts. All of it is part of the main data, which may lie in earlier frames ([bit reservoir](bit-reservoir)). The analyzer reads these fields from the side information; it does not decode the Huffman data itself.
