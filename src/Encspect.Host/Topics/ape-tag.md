---
id: ape-tag
title: APE tags
related: tag-placement, replaygain, id3v1
references: APEv1 and APEv2 specifications (Monkey's Audio, wiki.hydrogenaud.io)
---

**What it is.** The APE tag comes from Monkey's Audio, a lossless format, and is also used by Musepack, WavPack and other formats, and in MP3 files by some taggers and MP3Gain. It is a list of items, each a key and a value: keys are free text such as "Title" or "REPLAYGAIN_TRACK_GAIN", values are UTF-8 text in APEv2 (any number of values, separated by zero bytes), binary data such as a cover picture, or a link.

**Where it sits.** An APE tag ends with a 32-byte footer starting "APETAGEX", which gives the version, the tag's size and the number of items. Readers find the footer at the end of the file, or just before an ID3v1 tag, and read the tag from there, so it can be added or changed without touching the audio. APEv2 tags may also have a header that repeats the footer.

**Versions.** APEv1 (version 1000) has only text values and no header; APEv2 (version 2000) adds the header, UTF-8, binary items and flags.

**What you see.** The header and footer with their fields, each item with its size, flags, key and values, and findings where the header and footer disagree, keys repeat or text is not UTF-8. The APE preamble contains "TAG", which can sit exactly where an ID3v1 tag is expected; the analyzer checks the structure so it does not mistake one for the other ([where tags sit](tag-placement)).
