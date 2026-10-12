# Lyrics3

- Lyrics3 v1 and v2, found directly before an ID3v1 tag, where their specification places them.
- **v2:** full confidence needs at least one field, fields that end exactly at the size, and field bytes from 01 to 254.
- **v1:** full confidence needs text without $00 or $FF within 5100 bytes. Lyrics may mention `LYRICSBEGIN`, so the tag starts at the earliest one in those 5100 bytes after which only text follows; a later one is noted.
- A Lyrics3 tag without the ID3v1 tag after it is still reported, with a finding and one level less confidence.
