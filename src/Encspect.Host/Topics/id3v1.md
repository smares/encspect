---
id: id3v1
title: ID3v1 and the Enhanced TAG
related: id3v2, tag-placement, lyrics3
references: id3.org, ID3v1; ID3v1.1 (Michael Mutschler)
---

**What it is.** ID3v1 was the first common way to store a song's title and artist in an MP3 file, from 1996. It is a block of exactly 128 bytes at the very end of the file, starting with "TAG", with fixed fields: title, artist and album of 30 characters each, a 4-character year, a 30-character comment and a genre byte. ID3v1.1 later took the last two bytes of the comment for a zero byte and a track number.

**Why at the end.** Appending 128 bytes changes nothing before them, so a tag can be added or edited without rewriting the audio, and players that do not know it play the bytes as a short burst of garbage or skip them. See [where tags sit](tag-placement).

**Limits.** The fields are short, the text encoding is undefined (in practice ISO-8859-1 or the computer's code page), and the genre is a number from a fixed list of 80, which Winamp extended. These limits led to [ID3v2](id3v2).

**The Enhanced TAG** is a 227-byte block starting "TAG+" directly before the ID3v1 tag, with 60 more characters for title, artist and album, a free-text genre, a speed and start and end times. No authoritative specification exists and few programs read it.

**What you see.** Each field with its bytes. ID3v1 has nothing to validate beyond its signature and printable text, so the analyzer never gives it more than medium confidence.
