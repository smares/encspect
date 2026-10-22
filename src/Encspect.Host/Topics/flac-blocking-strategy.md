---
id: flac-blocking-strategy
title: FLAC blocking strategy
related: flac-frames, flac-streaminfo
references: RFC 9639 (FLAC), section 9.1
---

**The problem.** A decoder must know where in the stream a frame belongs, to detect lost frames and to seek, and an encoder may either cut the audio into equal blocks or choose the size of each.

**How it works.** The blocking strategy is one bit of the frame header. With fixed block size, every frame but the last has the size STREAMINFO gives, and the header counts frames from 0: the first sample of a frame is its number times the block size. With variable block size, the encoder may choose a size for every frame, and the header counts samples: it holds the number of the frame's first sample. The number is coded like a UTF-8 character, extended to seven bytes so that it holds up to 36 bits; a frame number of a fixed stream takes at most six bytes and 31 bits.

**Why both.** Fixed blocks have the shortest headers. Variable blocks let an encoder choose the size of every frame, at the cost of a longer number in every header. A stream uses one strategy throughout.

**What you see.** Blocking Strategy in each frame header and in the statistics, and either a frame number or a sample number. A number that does not follow from the frame before is a finding, and so is a strategy that changes.
