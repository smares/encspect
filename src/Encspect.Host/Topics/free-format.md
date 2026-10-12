---
id: free-format
title: Free format
related: frame-header, bitrate-modes, frame-sync
references: ISO/IEC 11172-3 2.4.2.3
---

**What it is.** The bitrate index 0 in a frame header means free format: the stream has a fixed bitrate that is not one of the table's values. Free format exists for applications that need a bitrate in between, or above the table's highest; LAME, for example, can write free-format streams up to 640 kbps.

**How a decoder copes.** The header does not give a free-format frame's length, so a decoder finds it from the distance to the next frame's header, once, and then expects every frame of the stream to have that length, plus the padding slot where it is set. ISO/IEC 11172-3 requires the bitrate to stay fixed within a free-format stream. Not every decoder supports free format, and some support it only up to a limit.

**What you see.** The Bitrate field shows "free", the analyzer reports the frame length it found, and the bitrate graphs compute each frame's bitrate from its length. A free-format stream whose frame length changes is reported as a warning.
