---
id: tag-placement
title: Where tags sit in a file
related: id3v1, id3v2, ape-tag, lyrics3, frame-sync
references: ID3v2.4.0 sections 3 and 5; APEv2 specification; id3.org, Lyrics3
---

**The problem.** An MPEG audio file is just frames one after another, with no container to hold metadata. Tags were added around the audio, and decoders that know nothing of them have to survive them.

**At the start.** [ID3v2](id3v2) tags sit before the audio, so a player that streams the file gets the title and the picture first. Changing a tag there may require rewriting the whole file, which [padding](id3v2-padding) avoids. A tag may also be found after another one at the start.

**At the end.** [ID3v1](id3v1), [APE](ape-tag) and [Lyrics3](lyrics3) tags sit after the audio, where they can be added or changed by rewriting only the end of the file. Readers find them by their fixed size or by a footer they read backward: ID3v1 in the last 128 bytes, Lyrics3 and APE just before it or at the end, and an ID3v2.4 tag through its footer.

**Why structure matters.** Tag signatures are short: "TAG" can occur in audio, in a picture, or inside another tag; "APETAGEX" itself contains "TAG". So the analyzer looks for each tag only where its specification places it, accepts it by its structure, and lets each tag claim its bytes; where candidates compete, the strongest evidence wins. The Detection node of every tag shows its confidence and the checks behind it, and findings report candidates that were rejected and why.

**For decoders**, a tag between frames is a stretch that is not audio; they resync after it, and the analyzer shows the audio on each side as a separate region.
