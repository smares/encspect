# ID3v2.4 frame size encoding: requirements for a clean-room rule

**Status:** written on 2026-10-29 by a Claude agent (Opus) for a clean-room rewrite. The agent read the GPL 2 source the removed code followed, the ID3v2 specifications, the current parser, the committed fixtures and their golden reports. This text contains no code and does not describe that source's rule. Whoever implements the rule designs it from the requirements below and the specifications, and must not read the sources listed under [Implementer's sources](#implementers-sources).

It covers three members of `src/Encspect.Tags.Id3/Id3v2TagParser.cs`, which are stubs until the rewrite:

- `HasPlainFrameSizes`: the tag-wide decision whether an ID3v2.4 tag stores its frame sizes as plain integers.
- `FollowsDirectly`: whether a tag that sets the extended header flag starts with a frame instead.
- `FrameIds`: the set of known frame IDs both of them use.

## The problem

- ID3v2.3 section 3.3 stores a frame's size as a plain 32-bit big-endian integer (`$xx xx xx xx`).
- ID3v2.4 section 4 stores it as a 32-bit syncsafe integer (`4 * %0xxxxxxx`, section 6.2): seven bits per byte, the high bit of each byte zero. The ID3v2.4 changes document says the frame header's size field was rewritten "to avoid false synchronisations".
- Some writers of ID3v2.4 tags, old iTunes versions among them, kept writing the ID3v2.3 form. Read as syncsafe, such a size is wrong whenever the frame holds 128 bytes or more. The frame then ends inside its own data, and the frames after it are lost.
- Below 128 bytes, both readings give the same number. A tag whose frames are all smaller than that reads the same either way.
- A size with a byte of $80 or more cannot be syncsafe. The parser already reads such a size as a plain integer, frame by frame (below). Only a plain size whose four bytes all lie below $80 needs a decision on the whole tag. $00 00 01 2C (300 bytes) is one: read as syncsafe, it gives 172.

## What the parser does around the decision

The rule is one yes-or-no decision per ID3v2.4 tag. It is made once, before the frames are parsed.

- **Inputs:**
  - the tag's bytes as stored, from the first frame to the end of the tag body. The first frame starts right after the header, or after the extended header when the tag has one.
  - The end of the tag body is the header's size after the 10-byte header (section 3.1), or the end of the file if the file ends first. A footer lies outside that range.
  - The bytes are not resynchronized: ID3v2.4 unsynchronizes frame by frame (section 6.1), and the size counts the stored bytes (section 4).
- **Output:** whether every frame size of the tag is read as a plain integer. It is stored in `ParseState.PlainSizes` and is not shown in the report itself. It shows only through the frame nodes, their `Size` values and the finding below.
- **Per-frame fallback (kept, outside the rewrite):** when the decision is no, an ID3v2.4 frame size with any byte of $80 or more is still read as a plain integer. When the decision is yes, every frame size of the tag is read that way.
- **Finding:** for each ID3v2.4 frame read as plain, if its plain value differs from its syncsafe reading, the parser raises Warning `id3v2.frame-size-not-syncsafe` on the frame node: "Frame {ID} at 0x{offset} stores its size as a plain integer instead of a syncsafe one, as old iTunes versions did." It raises none for a frame where both readings agree.
- ID3v2.2 and ID3v2.3 are not affected. The parser does not call the rule for them.

## Evidence in the committed fixtures

There is no iTunes-written file in the repository. `scripts/fixtures/make_tag_fixtures.py` builds both plain-size fixtures by hand. 77 ID3v2.4 tags occur in the golden reports under `tests/fixtures`, in tag fixtures and inside other formats' files. They were written by `make_tag_fixtures.py`, other fixture scripts, mutagen and ffmpeg. Only four of them hold a frame of 128 bytes or more, so only those four depend on the decision:

| Fixture (`tests/fixtures/tags/`) | Layout | Read as syncsafe | Read as plain | Required outcome |
| --- | --- | --- | --- | --- |
| `id3v24-itunes-plain-sizes.mp3` | Tag body 347 bytes, from byte 10 to 357. TIT2 at 10: size bytes $00 00 01 2C, a text of 299 "t". TPE1 at 320: size 7. Then 20 zero bytes of padding. | TIT2 is 172 bytes and ends at 192, inside its text. The next "frame ID" is `tttt`, which is not a valid ID, and its size runs far past the tag. | TIT2 is 300 bytes and ends at 320. TPE1 ends at 337. Zero padding runs to the end of the tag. | **Plain.** The golden has TIT2 at 10 (310 bytes, size 300), TPE1 at 320 (17 bytes), padding of 20 bytes, and one `id3v2.frame-size-not-syncsafe` for TIT2. |
| `id3v24-plain-frame-sizes.mp3` | Tag body 227 bytes. TIT2 at 10: size $00 00 00 C9 (201). TPE1 at 221: size 6. TPE1 ends exactly at the tag end, with no padding. | The size byte $C9 is not syncsafe. Read anyway, TIT2 ends at 93, inside its text (`xxxx`). | TIT2 ends at 221. TPE1 ends at 237, the end of the tag. | **Either.** The per-frame fallback reads TIT2 as plain on its own, and TPE1's two readings agree. The golden has TIT2 (211 bytes, size 201), TPE1 (16 bytes, size 6) and one finding for TIT2. |
| `id3v24-text-cut.mp3` | Tag body 1,048,627 bytes. TIT2 (7 bytes), then USLT with syncsafe size $00 40 00 10 (1,048,592), then 8 bytes of padding. | Ends at the padding. | USLT would be 4,194,320 bytes, past the end of the tag. | **Syncsafe.** No `id3v2.frame-size-not-syncsafe`. |
| `id3v24-footer-tag-collision.mp3` | Tag body 184 bytes. TXXX with syncsafe size $00 00 01 2E (174) ends exactly at the end of the body. A footer follows. | Ends exactly at the end of the tag. | TXXX would be 302 bytes, past the end of the tag. | **Syncsafe.** No `id3v2.frame-size-not-syncsafe`. |

In the other 73 ID3v2.4 tags, every frame is below 128 bytes, so either decision gives the same report. In all 73, the syncsafe reading walks from frame to frame and ends exactly at the end of the tag or at zero padding that runs to it. The rule should answer syncsafe for them, the specification's reading.

## Requirements for the rule

**Outcomes:**

- The outcomes in the table above, and syncsafe for every other ID3v2.4 tag of the goldens. Then every golden and manifest stays as it is.
- Where the specification's reading gives a consistent tag, the rule keeps it. Section 4 defines a consistent tag:
  - frames follow each other from the first frame on;
  - each has a four-character ID of capital A–Z and 0–9 and a syncsafe size;
  - each fits inside the tag;
  - after the last frame comes either the end of the tag or padding of $00 bytes up to it (section 3.3: no padding between frames).
- The rule answers plain only when the plain reading explains the tag and the syncsafe one does not.

**Cases the rule must not misread:**

- **A valid syncsafe tag whose sizes also make sense as plain sizes.** For example, a frame of 128 bytes or more whose plain reading happens to land on another plausible frame header, on padding or exactly on the end of the tag. The syncsafe reading is consistent there, so it wins. No such fixture exists yet. The implementer adds one (see "Tests" below).
- **Padding:**
  - Under the plain reading, a frame of a syncsafe tag can jump into the zero padding and look like a short chain that ends in padding. That must not beat a consistent syncsafe reading.
  - Bytes after the last frame that are not all zero (`id3v2.padding-not-zero`, fixture `id3v2-padding-garbage`) make neither reading consistent. The tag stays syncsafe, and the existing padding findings apply.
- **An extended header:** the frames start after it, so the rule never reads the extended header as a frame. ID3v2.4 section 3.2 gives its size as a syncsafe integer that counts the whole extended header. Seven ID3v2.4 tags of the goldens have one, all with frames below 128 bytes.
- **Unknown frame IDs:**
  - Section 4 allows any ID of A–Z and 0–9, and IDs that start with X, Y or Z are free for experimental frames. A frame whose ID is not in the known list is still a frame. It must not make a consistent syncsafe tag look damaged.
  - A tag with plain sizes and an unknown ID should still be read as plain.
  - An ID with other characters (`tttt` above) is not a frame header.
- **A truncated tag (`id3v2.truncated`):**
  - The body ends at the end of the file, so the last frame of either reading may run past it.
  - A syncsafe tag cut short must stay syncsafe.
  - Reading a cut plain-size tag as plain, when the plain chain is consistent up to the cut and the syncsafe one is not, is preferred. No fixture pins it.
- **Small or empty input:** a body too short for a frame header, a body that is all padding, or no frames at all (`id3v24-no-frames`) answers syncsafe.

**Behavior and cost:**

- Never throw. On damaged input, answer syncsafe and let the frame parser report the damage.
- The work is at most one pass over the frame headers per reading tried, so a tag of 256 MB of tiny frames stays within `ComplexityTests`' bounds.
- Reads go through the `ByteReader`, which checks cancellation. It throws `EndOfStreamException` for a position outside its range, so the rule stays inside the tag body.
- The parser instance holds no state between calls (CLAUDE.md).

**Documentation:**

- Describe the rule in `docs/tags/id3.md`.
- Restore the plain-size clause of the ID3v2 bullet in `docs/PLAN.md` (the ID3v2 bullet under "v1 vertical slice: MP3") in the rule's own terms. It now points to this file.

One rule that meets these requirements is the one the license audit proposed: choose the plain reading only when its frame chain ends exactly at the padding or the end of the tag, with valid IDs, and the syncsafe chain does not. A model of that rule, written for this description, gives the required outcome on all 77 tags. The implementer may choose it or another one.

## Extended header flag without an extended header (`FollowsDirectly`)

- **The problem:**
  - ID3v2.3 and ID3v2.4 set bit 6 of the header flags when an extended header follows the header (ID3v2.3 section 3.1, ID3v2.4 section 3.1).
  - Some taggers set the flag without writing one, so the first frame starts right after the header.
  - Read as an extended header, the frame's ID becomes a size, and the tag is lost.
- **What the specification says an extended header starts with:**
  - ID3v2.3 section 3.2: a 4-byte size of 6 or 10 that excludes itself.
  - ID3v2.4 section 3.2: a syncsafe size of at least 6 that counts the whole extended header, then a "number of flag bytes" of $01.
  - Four letters or digits are never such a size in a tag of any real length: a plain size of at least $30 30 30 30, or a syncsafe one of more than 100 MB.
- **Required behavior:**
  - For a tag of version 3 or 4 with the flag set, decide from the first four bytes of the body whether a frame starts there.
  - If one does, read the tag without an extended header and raise Warning `id3v2.extended-header-missing` on the tag node: "The header sets the extended header flag, but a frame follows it directly; the tag is read without an extended header."
  - Otherwise, parse the extended header as now.
  - The flag is bit 6 in version 2 too, but there it means compression (ID3v2.2 section 3.1), so version 2 is excluded.
- **Fixtures:**
  - `id3v23-extended-flag-without-header` starts its body with `TIT2`: no extended header, the finding, TIT2 at 10 and TPE1 at 26, and 16 bytes of padding.
  - Eleven tags in the goldens set the flag and have a real extended header. They start with $00 00 00 06, 0A, 07, 08, 0B, 0C, 0F and 10. They must keep their extended headers and findings: `id3v24-basic`, `id3v24-extended-header-size-and-flags`, `id3v24-extended-header-crc-length`, `id3v24-extended-header-flag-bytes`, `id3v24-crc-mismatch`, `id3v24-restrictions-violated`, `id3v24-restrictions-tag-size`, `id3v23-crc-mismatch`, `id3v23-padding-size-mismatch`, `id3v23-unsync`, `id3v23-extended-header-undefined-flags`.
- **Implementer's choice:** whether the test needs a known frame ID or accepts any ID of A–Z and 0–9 (with a size that could not be an extended header). No fixture pins it. Either way the result is findings, never an exception.

## The frame ID list (`FrameIds`)

- **Outcome:** the implementation takes the second choice above, so the list was dropped: `FollowsDirectly` accepts any four characters of A-Z and 0-9, which covers the experimental IDs and frames outside the specifications such as the podcast frames. The rest of this section is kept as the record of what a list would have held.

- **What it is for:**
  - `FollowsDirectly`, to recognize a frame where the extended header should be.
  - Optionally the new plain-size rule.
  - Nothing else reads it. The per-frame descriptions in `Id3v2FrameTexts.cs` and the once-only list `SingleFrameIds` are separate and unaffected.
- **Rebuild it from the specifications' lists:**
  - ID3v2.3.0 section 4, "Declared ID3v2 frames": 74 IDs.
  - ID3v2.4.0 Native Frames section 4: 83 IDs.
  - Their union is 92 IDs: ID3v2.4 drops EQUA, IPLS, RVAD, TDAT, TIME, TORY, TRDA, TSIZ and TYER, and adds 18.
  - ID3v2.2 (section 4 of the 1998 document) has three-character IDs. Neither use reads an ID3v2.2 header, so its list is not needed unless a new use needs it.
- **Additions:**
  - The removed list held 14 IDs that no ID3v2 version declares.
  - No fixture contains any of them, so no fixture needs an addition.
  - An addition needs a checkable source:
    - CHAP and CTOC are defined by the id3.org "ID3v2 Chapter Frame Addendum" (version 1.0).
    - TCMP, TSO2, TSOC, GRP1, MVNM and MVIN are already described in `Id3v2FrameTexts.cs` as frames iTunes writes.
    - Other non-standard IDs go in only with a source or a fixture, cited next to the list.

## Tests and how to run them

**The check:**

- `TagFixtureTests` (manifest and golden report of each tag fixture).
- `FindingCoverageTests.EveryCodeTheParsersEmitIsAsserted` (both codes must stay asserted by a manifest).
- `DescriptionTests`, `CorruptTagFileTests` (mutated tag fixtures and random files produce reports).
- `TagLibCrossCheckTests`, whose `Explained` entries for `id3v24-itunes-plain-sizes` and `id3v24-plain-frame-sizes` record that TagLib# reads those sizes as syncsafe.
- The other formats' fixture tests, for ID3v2.4 tags inside their files.

**Failing until the rewrite** (with the stubs, which answer no to both questions and hold an empty list):

- `id3v24-itunes-plain-sizes`: TIT2 cut short, and no TPE1.
- `id3v23-extended-flag-without-header`: the frame is read as an extended header.
- Their manifest, golden and TagLib# cross-check rows fail.
- `id3v24-plain-frame-sizes` passes through the per-frame fallback.

**Run:**

- `dotnet test tests/Encspect.Tests -c Release --filter "FullyQualifiedName~TagFixtureTests|FullyQualifiedName~TagLibCrossCheckTests|FullyQualifiedName~FindingCoverageTests|FullyQualifiedName~CorruptTagFileTests|FullyQualifiedName~DescriptionTests"`.
- Then the full test project. Golden reports must not change. Regenerating them with `ENCSPECT_UPDATE_GOLDEN=1` is a sign the rule is wrong.

**Fixtures to add** with `make_tag_fixtures.py`, with manifests:

- a syncsafe tag with a frame of 128 bytes or more whose plain reading lands on a plausible chain: expected syncsafe, no finding;
- a plain-size tag with an extended header;
- a plain-size tag with an experimental `X…` frame;
- a plain-size tag cut by the end of the file: the outcome the implementer chooses, documented.

**Black-box cross-checks:**

- TagLib# (in `TagLibCrossCheckTests`).
- mutagen 1.47, run as a program on the fixtures (for example its `mid3v2 --list`), never read. It is not installed in every session.

## Implementer's sources

**May read:**

- this description;
- the ID3v2 specifications at id3.org:
  - "ID3 tag version 2" (ID3v2.2, 1998);
  - "ID3 tag version 2.3.0";
  - "ID3 tag version 2.4.0 – Main Structure", "– Native Frames" and "– Changes";
  - "ID3v2 Chapter Frame Addendum";
- the rest of the repository, including the stubs, the fixtures, `make_tag_fixtures.py` and the goldens;
- TagLib# and mutagen as programs whose output is compared, not as source.

**Must not read:**

- mutagen's source code (any version; its `_tags.py` and `_frames.py` above all) and other GPL or LGPL ID3v2 readers;
- the git history of the stubbed members (`HasPlainFrameSizes`, `FollowsDirectly`, `FrameIds` in `Id3v2TagParser.cs`) and of the `docs/PLAN.md` line this rewrite replaced;
- the describer's scratch folder;
- the license audit reports (`scratchpad/reviews/license-audit-*.md`).
