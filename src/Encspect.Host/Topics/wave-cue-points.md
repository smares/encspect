---
id: wave-cue-points
title: Cue points, labels and loops in WAV
related: riff-chunks, riff-info, bwf-broadcast-extension
references: Microsoft, Multimedia Programming Interface and Data Specifications 1.0, chapter 3 (Cue-Points, Playlist and Associated Data chunks); Microsoft, Multimedia Data Standards Update, April 1994 (inst and smpl chunks); EBU Tech 3306, annex A.3 and A.4
---

**The problem.** Editors and samplers need to mark places in the audio, name them, play sections in an order and loop a stretch of a note, and the format has to carry all of it.

**How it works.** The cue chunk lists cue points: a name that is unique in the file and a position in samples, with fields that locate the point in a data chunk or a block of compressed data. The playlist chunk orders segments between cue points, each played a number of times. A LIST of the type adtl attaches labels, notes, text over a stretch of audio, or whole files to cue points by their names. The smpl chunk is for samplers: the MIDI note that plays the sample at its pitch, a fine tuning, a SMPTE offset and loops with a start, an end and a play count. The inst chunk holds a note, a gain and the ranges of notes and velocities the sample is meant for.

Because the cue chunk addresses with 32 bits, RF64 files define a marker chunk of their own, with 64-bit positions and the label in the same record.

**What you see.** The cue points, playlist segments, labels and loops with all their fields. A finding appears where a label names a cue point that the cue chunk does not hold, where two cue points share a name, or where a count does not fit the chunk.
