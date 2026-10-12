---
id: frame-sync
title: Frame sync and gaps
related: frame-header, tag-placement, granules-and-frames
references: ISO/IEC 11172-3 2.4.1.3, 2.4.2.3
---

**The problem.** An MPEG audio file has no table of contents and no container around its frames: it is just frames, one after another. A decoder has to find where each frame starts.

**How it works.** Every frame starts with a sync word of 11 or 12 set bits, followed by header fields from which the frame's length follows. Once the decoder has found one frame, it knows where the next one starts. The sync word is short, though, and any data can contain it by chance, so a decoder that finds one cannot be sure it found a frame.

**How the analyzer decides.** A header only counts when a chain of consistent frames follows it: two more frames of the same version, layer and sample rate exactly where the lengths predict, or, for a very short file, frames whose content checks out to the end of the data. Once in sync, frames are followed by their lengths, not searched for.

**Gaps.** Everything between frames that is not a frame is shown as a Gap: data before the first frame, bytes where sync was lost and found again, or data after the last frame. Tags sit between the audio and are shown as tags, not gaps; the audio on each side of a tag is a separate region. A gap in the middle of the audio usually means damage, or files that were cut or joined. Decoders skip gaps, but a Layer III frame right after one may miss main data it expected in the [bit reservoir](bit-reservoir).
