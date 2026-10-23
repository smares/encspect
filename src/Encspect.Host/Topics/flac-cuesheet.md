---
id: flac-cuesheet
title: The FLAC cue sheet
related: flac-metadata-blocks, flac-seek-table
references: RFC 9639 (FLAC), section 8
---

**The problem.** A ripped compact disc is one stream, but its tracks, the index points inside them and its catalog data matter when the disc is burned again or split.

**How it works.** The cue sheet is an optional metadata block modeled on the table of contents of a compact disc. It holds a media catalog number, the number of lead-in samples, a flag for whether it describes a compact disc, and a list of tracks. A track has the offset of its first sample, its number, its ISRC, flags for whether it is audio and has pre-emphasis, and a list of index points, each an offset from the start of the track and a number. Offsets count samples from the start of the audio. On a compact disc the offsets of tracks are multiples of 588 samples, one sector.

**The lead-out.** The last track of a cue sheet is the lead-out, the end of the audio: number 170 on a compact disc, 255 on any other medium. It has no index points.

**What you see.** The fixed fields, each track with its fields and the first of its index points. Findings name a cue sheet whose tracks do not fill its length, one without a lead-out track and reserved bits that are set.
