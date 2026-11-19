---
id: correction-files
title: Correction files
related: optimfrog-blocks, wavpack-hybrid-mode, verification, crc
references: WavPack 4 and 5 binary file/block format (David Bryant, April 2020); libwavpack 5.6.0, src/open_utils.c (read_wvc_block, match_wvc_header); OptimFROG 5.100 (ofs with --correction) as a black-box writer of the files in Encspect's tests
---

**The problem.** A lossy file plays anywhere but cannot give the original audio back, and a lossless file can but is large. A pair of files offers both: the main file plays alone, and a second file holds what the lossy coding left out.

**How it works.** The correction file is read together with its main file, one block for each block. WavPack's .wvc file holds blocks that libwavpack matches to the .wv file's blocks by block index, sample count and flags. OptimFROG's .ofc file holds one correction block for each data block of its .ofs file, in the same order, in all five pairs of Encspect's test files. A decoder given both files rebuilds the original audio; given the main file alone it plays the lossy audio.

**How Encspect finds it.** The host looks for the file, never the parser. The name is the input's name without its last extension, a dot and the correction file's extension, in the same folder: x.ofs to x.ofc. A stream or standard input has no folder, so the pair is reported as unchecked.

**What "consistent" rests on.** For OptimFROG, which Encspect cannot decode, it rests on block counts and on the CRC-32 of every block of both files. A correction block states no sample count, rate or position that is known, so a correction file of another encode with as many blocks passes: Encspect's test files include such a pair, and the report calls it consistent. It says the two files belong together and are intact, not that the decoded pair equals the source audio.

**What you see.** A line under the stream with the status (consistent, inconsistent, missing, none written or unchecked), findings for each kind of problem, and the correction file's own tree, with the offsets of its own file. Only a consistent pair is called lossless with its correction file. A consistent OptimFROG pair also raises the claim that the file is the lossy part of a DualStream ([OptimFROG blocks](optimfrog-blocks)).
