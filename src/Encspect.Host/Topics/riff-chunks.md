---
id: riff-chunks
title: RIFF chunks
related: waveformatex, rf64, riff-info, id3v2-in-chunks
references: Microsoft, Multimedia Programming Interface and Data Specifications 1.0, chapter 2 (Resource Interchange File Format); Microsoft, Multimedia Data Standards Update, April 1994
---

**The problem.** A file that holds audio also holds things about it: its format, text, markers, a picture. A fixed header cannot leave room for what future software adds, and a reader should not break on parts it does not know.

**How it works.** RIFF cuts a file into chunks. Each starts with a four-character ID, then a 32-bit size that does not count the ID, the size field or the pad byte, then the data. If the size is odd, a pad byte of zero follows, so that every chunk starts at an even offset. A reader skips a chunk whose ID it does not know by its size, which is why new chunks never break old readers.

The whole file is one RIFF chunk: its data starts with a form type (WAVE for a waveform audio file), followed by the chunks. A LIST chunk holds a list type and more chunks; INFO lists carry text, adtl lists carry labels for cue points. A JUNK or PAD chunk is filler with no meaning. In RIFX the integers are big-endian instead of little-endian, and RF64 widens the sizes to 64 bits ([RF64](rf64)).

**What you see.** Every chunk with its ID, size and fields, the pad byte after a chunk of odd size, and chunks the analyzer does not know as a node named by their ID. A size that runs past the end of the file, a missing pad byte or bytes where no chunk starts are reported as findings.
