---
id: id3v2-in-chunks
title: ID3v2 tags in WAV and AIFF chunks
related: id3v2, tag-placement, riff-chunks
references: ID3v2.4.0 main structure, section 3; FFmpeg libavformat/wavdec.c and aiffdec.c (the chunk IDs that readers accept)
---

**The problem.** WAV and AIFF files have no tag of their own for artist, album and cover art, though players want them, and the ID3v2 tag is what the rest of the audio world uses.

**How it works.** Software stores a complete ID3v2 tag as the data of a chunk. WAV files use the chunk ID id3 or ID3 with a trailing blank, AIFF files use ID3 with a trailing blank; the data is the tag as it would be at the start of an MP3 file, starting with the three letters ID3. The chunk is an ordinary one: it has a size, a pad byte when the size is odd, and lies inside the RIFF or FORM chunk.

The tag keeps its own size, so the chunk may hold padding after it. A tag placed before the container, as in an MP3 file, or after it, as an ID3v1 tag, is not in a chunk; those are found by their place in the file, not by a chunk ID.

**What you see.** The chunk, and under it the tag as it is shown everywhere else, with its frames. If the chunk's data does not start with a valid tag, a finding says why the tag was not accepted.
