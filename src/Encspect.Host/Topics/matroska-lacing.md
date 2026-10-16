---
id: matroska-lacing
title: Blocks and lacing in Matroska
related: matroska-container, matroska-timestamps, ogg-pages
references: RFC 9559 sections 10.1 to 10.3 and 5.1.4.1.12
---

**The problem.** Audio frames can be a few dozen bytes, and an element header for each would waste a large share of the file. Matroska lets a block hold several frames of the same track.

**The block.** A SimpleBlock, or the Block inside a BlockGroup, starts with the track number as a variable-size integer, a signed 16-bit timestamp relative to its Cluster, and a flags byte. The flags say whether the block is a keyframe (SimpleBlock only), invisible or discardable, and which lacing it uses. The frames follow.

**Four ways to lace.**

- No lacing: one frame, the rest of the block.
- Xiph lacing: the number of frames minus one, then the size of each frame but the last as a run of 255s ended by a smaller byte (a size of 510 is 255, 255, 0), as in [Ogg](ogg-pages).
- EBML lacing: the same count, then the first size as a variable-size integer and each next size as a signed difference from the one before.
- Fixed-size lacing: the count only; the frames share the block equally.

The last frame takes what remains of the block. Lacing must not hold a single frame, a track whose FlagLacing is 0 must not use it, and the frames of a lace must follow each other without a gap, because only the first has a timestamp.

**What you see.** For each block its track, timestamp and flags, the lacing header with the sizes it states, and every frame at its bytes. A lacing header whose sizes do not add up to the block is a finding, and the frames of that block are not read.
