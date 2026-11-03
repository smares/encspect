---
id: samples-and-lost-samples
title: Samples and lost samples
related: measurements, gapless-playback, granule-position, mp4-sample-tables
references: Encspect's samples definition (docs/design/samples-definition.md)
---

**The problem.** A frame cut by the end of a file may or may not be audio. Encspect counts by what the file declares, not by what a decoder manages to play.

**How it works.** A unit is what a format codes one at a time and states the length of: a frame, a block, a packet, an access unit or, for PCM, a sample frame. The stream's samples are the samples per channel its units declare, read from the unit's syntax or the stream's fixed parameters and added up without decoding. A unit that cannot be used stays in the samples. Its declared samples are also reported as lost samples, at the stage before any trim, when
- the data ends before the unit does,
- the parser stops reading the unit because its syntax breaks, or
- the stream's state was lost, so units after damage cannot be read until the next sync point.

A checksum that fails over data the parser still reads does not make a unit lost. Where a format states a trim (Opus pre-skip, granule positions), the samples are those after the trim and the coded samples those before it.

**What it is not.** Positions no unit declares, though a number or a header total shows them, are missing samples, and a count with no units to read, such as an encrypted track's timing, is only stated. Neither is lost. The lost samples of the measurements are another figure: the positions between the first and last coded sample that no decoded block covers.

**What you see.** The samples and the duration they give, and a Lost Samples statistic where there are any. It is a figure with a basis, not a statement that the audio cannot be rebuilt.
