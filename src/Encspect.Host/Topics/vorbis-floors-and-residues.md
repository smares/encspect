---
id: vorbis-floors-and-residues
title: Vorbis floors and residues
related: vorbis-setup-header, vorbis-blocks, huffman-coding, square-polar-coupling, coded-bandwidth
references: Vorbis I specification, sections 4.3.2 to 4.3.6, 7.2.3 and 8.6.2 to 8.6.5
---

**The problem.** A block's spectrum has a coarse shape and fine detail. Vorbis codes them apart for each channel: the floor says how loud each part of the spectrum is, the residue says what is left once the floor is divided out.

**The floor.** A type 1 floor is a piecewise linear curve over a list of positions from the setup header. The packet gives the height of the first two positions and then, partition by partition, how far each further position lies from where the line between its neighbors would put it. A floor can also be marked unused, which leaves the channel silent for that block (a decoder that finds the packet cut short does the same). Type 0 floors, which code line spectral pairs, are defined but rare.

**The residue.** The spectrum is cut into partitions of a size the setup header states, from a first to a last coefficient. A classbook codeword names the class of several partitions at once, and each class lists a [codebook](huffman-coding) for each of up to eight passes. Every pass reads the entries of its codebook for the partitions whose class has a book in that pass, and adds the vectors the entries stand for to the spectrum. A class with no book in any pass costs only its class word. Residue type 2 interleaves the channels of a submap into one vector; type 0 interleaves the values of each vector across the partition.

**Channels.** [Square polar coupling](square-polar-coupling) turns a pair of residue vectors from magnitude and angle back into two channels before the floors multiply them in.

**What you see.** The analyzer reads every packet's floors and residues. The end of the highest partition that reads an entry gives the packet's [coded bandwidth](coded-bandwidth).
