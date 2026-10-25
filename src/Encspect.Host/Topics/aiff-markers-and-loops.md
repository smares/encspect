---
id: aiff-markers-and-loops
title: AIFF markers, comments and instrument loops
related: aiff-container, wave-cue-points
references: Apple, Audio Interchange File Format AIFF-C (draft of 8/26/91), sections 6.0, 7.0 and 9.0
---

**The problem.** Sound editors and sampling keyboards need to point at places in the sound, say what is there, and play a stretch of it over and over to sustain a note.

**How it works.** The Marker Chunk (MARK) lists markers: an ID above zero that is unique in the file, a position in sample frames and a name. A marker lies between two frames, and position 0 is before the first. The Comments Chunk (COMT) holds comments, each with the date it was made, in seconds since January 1, 1904, and optionally the ID of the marker it is about.

The Instrument Chunk (INST) describes how a sampling keyboard plays the sound: the note it was recorded at, a tuning adjustment in cents, the range of notes and velocities it suits, a gain in decibels, and two loops. The sustain loop plays while the key is held, the release loop after it is let go. A loop names two markers as its begin and end, and has a mode: none, forward, or forward and backward.

For compressed sound the marker positions refer to the expanded sound, not to the compressed frames.

**What you see.** Each marker with its ID, position and name, each comment with its date, and the loops of the instrument. A finding appears where a loop or comment names a marker that the Marker Chunk does not hold, or where marker IDs repeat.
