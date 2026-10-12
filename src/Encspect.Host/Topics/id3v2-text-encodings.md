---
id: id3v2-text-encodings
title: Text encodings in ID3v2
related: id3v2, id3v1
references: ID3v2.3.0 section 3.3; ID3v2.4.0 section 4
---

Each ID3v2 frame that holds text starts with a byte that says how its strings are encoded:

- **0, ISO-8859-1**: one byte per character, Western European letters only. Strings end with one zero byte.
- **1, UTF-16 with a byte order mark**: two bytes per character for most of Unicode. The byte order mark at the start of each string says whether the bytes come high first or low first. ID3v2.2 calls it UCS-2. Strings end with two zero bytes.
- **2, UTF-16BE without a byte order mark** (ID3v2.4 only): always high byte first.
- **3, UTF-8** (ID3v2.4 only): one to four bytes per character, ASCII unchanged.

**Why it matters.** [ID3v1](id3v1) had no way to say which characters its bytes meant, so names in other alphabets turned to garbage on other computers. ID3v2 states the encoding of every frame. Problems remain where programs write what the version does not allow, such as UTF-8 in an ID3v2.3 tag, or UTF-16 without a byte order mark; readers then have to guess.

**What you see.** The Encoding field of each text frame, the decoded text, and findings for encodings the version does not define, missing byte order marks, and bytes that are not valid in the stated encoding. URLs and some other fields are always ISO-8859-1, whatever the encoding byte says.
