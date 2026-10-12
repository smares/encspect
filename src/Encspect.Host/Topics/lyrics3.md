---
id: lyrics3
title: Lyrics3
related: id3v1, tag-placement
references: id3.org, Lyrics3 and Lyrics3 v2.00
---

**What it is.** Lyrics3 stores song lyrics in an MP3 file, in a block directly before the [ID3v1](id3v1) tag at the end of the file. It was made for players that show the lyrics while a song plays.

**Lyrics3 v1** is simply the text "LYRICSBEGIN", up to 5100 characters of lyrics, and "LYRICSEND". It has no size field, so a reader searches backward for LYRICSBEGIN, and lyrics must not contain the markers.

**Lyrics3 v2.00** has fields, each with a three-letter ID and a five-digit size: lyrics (LYR) with optional [mm:ss] timestamps per line, an indications field (IND), additional information (INF), the lyrics' author (AUT), the full album, artist and title where ID3v1's 30 characters are too few (EAL, EAR, ETT) and links to images (IMG). A six-digit size and "LYRICS200" end the tag, so a reader can find its start from the end.

**What you see.** Each field with its ID, size and data, the lyrics line by line with their timestamps, and findings where fields break the format or disagree with the ID3v1 tag. A Lyrics3 tag must be followed by an ID3v1 tag; without one, readers do not find it, and the analyzer lowers its confidence.
