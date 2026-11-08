# Tags

Tag parsers are separate from the format parsers. The host finds the tags of a file and hands each one to its parser; containers hand the tags they hold to the same parsers.

| Tag | Page |
|---|---|
| ID3v1 and ID3v2 | [id3.md](id3.md) |
| APEv1 and APEv2 | [ape.md](ape.md) |
| Lyrics3 v1 and v2 | [lyrics3.md](lyrics3.md) |
| Vorbis comments | [vorbis-comments.md](vorbis-comments.md) |

## How tags are found

Tags are found by position and structure, never by a signature alone, because short signatures collide with each other and with audio data. The rules and the fixtures that prove them are below; the decision behind them is in the plan, [Tag detection and false positives](../plan.md#tag-detection-and-false-positives).

- **Positions:** each tag type is looked for only where its specification allows it:
  - ID3v1 at exactly 128 bytes before the end of the file;
  - APE through its footer at the end or just before another trailing tag, or (APEv2) through its header at the start or directly after a leading tag;
  - ID3v2 at the start or directly after a leading tag, and an ID3v2.4 tag also through its footer ("3DI") at the end or just before another trailing tag;
  - Lyrics3 directly before an ID3v1 tag, where its specification places it, and also without one, with a finding.

  The one guided search follows an ID3v2.4 SEEK frame to the next tag.
- **Validation:** structure decides how confident a tag is, not whether it exists. A tag that breaks its specification stays in the report, with a finding for each violation and a lower confidence.
- **Ownership:** where candidates claim the same bytes, the strongest evidence wins, and a signature inside bytes another tag or a container already owns is rejected. Rejected candidates become findings.

### Position

- Signatures are never scanned for across the whole file.
- **The one guided search:** an ID3v2.4 SEEK frame gives the minimum offset of the next tag, and ID3v2.4 section 5 says to search from there. The host searches at most 1 MiB of unclaimed data from that offset and accepts only a fully validated tag.

### Validation

- A candidate is rejected only when its extent cannot be determined:
  - an ID3v2 size that is not syncsafe;
  - an APE size that points outside the file;
  - a Lyrics3 v2 size that does not point at `LYRICSBEGIN`;
  - a footer without a matching header.
- Everything else that breaks a specification is reported the way a lost MPEG sync is: the tag stays in the report, each violation becomes a finding, and the confidence drops.
- The host enforces it: a tag with any warning is at most medium confidence.
- What full confidence needs is listed per tag: [ID3](id3.md), [APE](ape.md), [Lyrics3](lyrics3.md).

### Evaluation order and ownership

- Tags are looked for only at anchors. When several candidates claim the same anchor, the one with the strongest evidence wins.
- **Equal confidence:** the longer candidate wins. They all end at the anchor, so the shorter one's signature lies inside the longer one's structure, which owns those bytes (a `TAG` in an Enhanced TAG's artist field).
- **Remaining ties:** the order APE, Lyrics3, ID3v2 found through its footer, ID3v1, Enhanced TAG breaks them.
- **A tag its specification places before another** (Lyrics3 and the Enhanced TAG before ID3v1) is reported without it too, with a finding and one level less confidence, so a correctly placed competitor wins.
- **The claim map:** the host keeps one, so a signature inside an already claimed region (an image in an ID3v2 tag, an APE item, an ID3v1 field) is rejected.
- **Containers that state their size** own their bytes the same way: a trailing candidate that starts before the declared end is one of the container's chunks, a near miss, which the container parser hands to the tag parser itself.
- **Evidence:** every candidate carries a confidence level and its reasons. Losing and rejected candidates become findings, such as "TAG at EOF-128 rejected: part of an APE tag header".

### The APE and ID3v1 collision

- `APETAGEX` contains `TAG` at offset 3.
- An APE tag with a header that totals 131 bytes (32 header, 67 items, 32 footer) and ends the file places that `TAG` at EOF-128, exactly where ID3v1 is expected.
- A reader that trusts the signature reports a garbage ID3v1 tag made of the APE header, items and footer.
- The ownership rule prevents this: the APE tag validates fully, its evidence outweighs the bare `TAG`, and it claims the bytes.

### Fixtures that prove it

- The 131-byte APE tag above with no ID3v1 tag: the APE tag only, plus a near-miss finding. A script writes its exact bytes; no external tool is needed.
- A real ID3v1 tag directly after an APE tag, and an APE tag after an ID3v1 tag.
- Lyrics3 v1 and v2 before ID3v1, and every plausible stacking order of ID3v2, APE, Lyrics3 and ID3v1.
- ID3v2.4 tags found through their footer: at EOF, before APE and ID3v1, at both ends of the file, as the whole file, and with `TAG` at EOF-128 inside the tag. A tag appended without a footer cannot be found from the end (ID3v2.4 section 3.4) and stays data.
- Tags embedded inside cover art in an ID3v2 tag.
- Random data seeded with `TAG`, `ID3` and `APETAGEX` at random offsets: nothing is detected outside validated positions.
- User text that spells another tag's identifier exactly where a probe looks for it (ID3v1 fields, APE text items, Lyrics3 lyrics and fields, appended ID3v2 frames), each holding every identifier at every probe distance.
  - The real tags must come out unchanged, without warnings.
  - The one exception is what a specification forbids in that text: Lyrics3 v1 lyrics must not contain `LYRICSBEGIN` or `LYRICSEND`, so those two are tested as violations, with a warning.
- Truncated tags and concatenated files.
