---
id: flac-seek-table
title: The FLAC seek table
related: flac-metadata-blocks, flac-frames, flac-streaminfo
references: RFC 9639 (FLAC), section 8
---

**The problem.** A FLAC stream has no index, so a player that wants to jump to the middle would have to read frames from the start to find where the middle is.

**How it works.** The seek table is an optional metadata block of seek points, 18 bytes each: the number of the first sample of a frame, the offset in bytes of that frame from the first byte of the first frame, and the number of samples in the frame. A player picks the last point at or before the sample it wants, goes to its offset and reads on from there, which is accurate to a frame.

**Placeholders.** A point whose sample number is all ones is a placeholder. It reserves room, so a tool can fill the table in later without rewriting the file. Placeholders belong at the end, after the points that name samples, and the sample numbers of those rise from point to point.

**What you see.** The seek points with their fields, the first few in detail. Findings name a table whose length is not a whole number of points, sample numbers that do not rise, a point after a placeholder, and a second table.
