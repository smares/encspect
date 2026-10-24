---
id: riff-info
title: INFO lists in RIFF files
related: riff-chunks, writer-detection, id3v2-in-chunks
references: Microsoft, Multimedia Programming Interface and Data Specifications 1.0, chapter 2 ("INFO List Chunk", "Storing Strings in RIFF Chunks")
---

**The problem.** A file should be able to say what it is without a separate database: its title, artist, creation date and the program that made it.

**How it works.** A LIST chunk of the type INFO holds chunks of four-character IDs, each with a string ended by a zero byte. The specification lists 23 IDs, among them INAM (name), IART (artist), ICMT (comments), ICOP (copyright), ICRD (creation date), IGNR (genre), IKEY (keywords), ISBJ (subject) and ISFT (software). It says an INFO list may also hold new chunks, which a reader should skip if it does not know them. Writers add their own. The text is in ISO 8859-1 unless a CSET chunk names another code page.

**What you see.** Each chunk with its name and text. The text of the ISFT chunk names the program that wrote the file, which the analyzer weighs as a claim about the writer: it is text any program may fill in, so its confidence is low. IDs that the specification does not list are shown by their ID with their text.
