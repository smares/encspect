# Tags

Tag parsers are separate from the format parsers. The host finds the tags of a file and hands each one to its parser; containers hand the tags they hold to the same parsers.

| Tag | Page |
|---|---|
| ID3v1 and ID3v2 | [id3.md](id3.md) |
| APEv1 and APEv2 | [ape.md](ape.md) |
| Lyrics3 v1 and v2 | [lyrics3.md](lyrics3.md) |
| Vorbis comments | [vorbis-comments.md](vorbis-comments.md) |

## How tags are found

Tags are found by position and structure, never by a signature alone, because short signatures collide with each other and with audio data. The full rules, and the fixtures that prove them, are in the plan's section [Tag detection and false positives](../PLAN.md#tag-detection-and-false-positives).

- **Positions:** each tag type is looked for only where its specification allows it:
  - ID3v1 at exactly 128 bytes before the end of the file;
  - APE through its footer at the end or just before another trailing tag, or (APEv2) through its header at the start or directly after a leading tag;
  - ID3v2 at the start or directly after a leading tag, and an ID3v2.4 tag also through its footer ("3DI") at the end or just before another trailing tag;
  - Lyrics3 directly before an ID3v1 tag, where its specification places it, and also without one, with a finding.

  The one guided search follows an ID3v2.4 SEEK frame to the next tag.
- **Validation:** structure decides how confident a tag is, not whether it exists. A tag that breaks its specification stays in the report, with a finding for each violation and a lower confidence.
- **Ownership:** where candidates claim the same bytes, the strongest evidence wins, and a signature inside bytes another tag or a container already owns is rejected. Rejected candidates become findings.
