# ID3v1 and ID3v2

## ID3v1

- ID3v1, ID3v1.1 and the Enhanced TAG.
- Found at exactly 128 bytes before the end of the file.
- It has almost nothing to validate, so it never gets more than medium confidence.

## ID3v2

- ID3v2.2, 2.3 and 2.4, zlib-compressed frames included.
- Found at the start of the file or directly after another tag.
- An ID3v2.4 tag with the footer flag set is also found from its end: its footer ("3DI" and the header's version, flags and size, section 3.4) is looked for at the end of the file and just before another trailing tag. A tag appended without a footer cannot be found from the end and stays data.
- An ID3v2.4 SEEK frame gives the minimum offset of the next tag; the host searches from there.
- Full confidence needs everything to check out, among other things:
  - known flags;
  - an extended header laid out as its version defines it;
  - frames that fit the declared size, with padding only at the end;
  - frames of a kind the specification allows once appearing only once: text and URL frames other than the user-defined ones, WCOM and WOAR, a picture of type 1 or 2, and frames such as MCDI or PCNT;
  - a byte order mark on every UTF-16 string;
  - at least one frame;
  - a data length indicator with every compressed ID3v2.4 frame.
- A size that is not syncsafe in the tag header leaves the tag's extent unknown, so the candidate is rejected. A size that runs past the end of the file does not say where the tag ends either, so the tag ends where its frames and the zero padding after them stop, and what follows is left to the other parsers.
- **Frame descriptions by the tag's version:** a frame's description cites the specification of the version of the tag it is in, with the section that version gives the frame: ID3v2.2 section 4 for the three-letter IDs ("The ID3v2.2 form of TIT2. … (ID3v2.2 section 4.2.1)"), ID3v2.3.0 section 4 for an ID3v2.3 tag, and the ID3v2.4.0 frames document's section 4 for an ID3v2.4 tag (experimental IDs: ID3v2.2 section 3.2, ID3v2.3.0 section 3.3, ID3v2.4.0 structure section 4).
  - A frame the version does not define says so: TDRC in an ID3v2.3 tag "(ID3v2.4.0 frames section 4.2.5; not part of ID3v2.3)", TYER in an ID3v2.4 tag "(ID3v2.3.0 section 4.2.1; not part of ID3v2.4, which replaced it with TDRC, ID3v2.4.0 changes section 4)".
  - The section numbers come from the headings of the documents on id3.org, which `scripts/fixtures/id3_specs.py` downloads pinned by SHA-256 (the summary table of the ID3v2.4.0 frames document gives 4.2.2 for TXXX, whose section is 4.2.6; the section's own heading is followed). `Id3v2FrameDescriptionTests` holds the wording per version.
- **A frame the tag's version does not define** is an info finding, `id3v2.frame-not-in-version`, in both directions: a frame ID3v2.3 defines and ID3v2.4 does not (TYER, TDAT, TIME, TORY, TRDA, TSIZ, IPLS, RVAD, EQUA) in an ID3v2.4 tag, with what ID3v2.4 did with it ("replaced it with TDRC", or "deprecated it" for TSIZ, as section 4 of the ID3v2.4.0 changes document words it, which the finding and the descriptions cite), and a frame only ID3v2.4 defines (TDRC, TDOR, TDEN, TDRL, TDTG, TIPL, TMCL, TMOO, TPRO, TSOA, TSOP, TSOT, TSST, RVA2, EQU2, ASPI, SEEK, SIGN) in an ID3v2.3 tag. The frame is still read and described. A frame no version defines (a program's own, an experimental one) has no finding, and ID3v2.2 has no case of its own: its IDs have three letters, theirs four. The table of the IDs each version defines is in `Id3v2FrameTexts.cs` with each frame's section, and `scripts/fixtures/check_id3_frames.py` checks it against the pinned documents. Fixtures: `id3v24-frame-from-id3v23`, `id3v23-frame-from-id3v24`.
- **Compressed frames** are decompressed (zlib, which .NET provides) up to the 1 MiB limit of other decoded data, and the declared size and the zlib checksum are checked.
- **Strings after the terminator** of an ID3v2.2 or ID3v2.3 text frame: section 4.2 of both says that what follows a terminator "should be ignored and not be displayed", but some taggers separate several values (several artists) by terminators, as ID3v2.4 does. Each string is shown as a `Text` node of its own, the frame's value joins them with "; ", and an info finding (`id3v2.text-after-terminator`) says that the frame has several strings, that the version's section 4.2 says to ignore what follows the first and that ID3v2.4 separates values this way. The fixture `id3v23-text-after-terminator` holds it (hand-built by `make_tag_fixtures.py`).
- Two writer bugs are read, each with a finding:
  - **Plain frame sizes in ID3v2.4** (`id3v2.frame-size-not-syncsafe`). Some writers, old iTunes versions among them, kept the ID3v2.3 form of the size (section 3.3 of ID3v2.3, a plain 32-bit integer) instead of the syncsafe one (ID3v2.4 section 4).
    - A size with a byte of $80 or more cannot be syncsafe and is read as plain frame by frame. Below 128 bytes both readings agree.
    - A plain size whose bytes all lie below $80 is decided for the whole tag, before its frames are parsed. Each reading is walked from the first frame (after the extended header, if there is one): it fits when every frame has an ID of A-Z and 0-9 and lies inside the tag, and the frames end exactly at the end of the tag or at zero padding that fills the rest. The ID need not be a known one.
    - The syncsafe reading is used whenever it fits. The plain one is used only when it fits and the syncsafe one does not.
    - In a truncated tag (the declared end lies past the end of the file) the last frame may run past the cut, and a chain that ends or whose zero padding runs to the cut proves no more than that. Both fit less well than an exact end. A syncsafe tag that is cut short therefore stays syncsafe; the plain reading is used only when it fits (at the cut or past it) and the syncsafe one does not fit at all.
    - A chain that reaches padding with bytes other than zero does not fit, like one that breaks earlier.
    - A tag that fits neither way stays syncsafe, and the frame findings report the damage.
  - **Extended header flag without an extended header** (`id3v2.extended-header-missing`, ID3v2.3 and 2.4). If the first four bytes of the tag body are four characters of A-Z and 0-9, as every frame ID is (the declared ones, the chapter frames and the frames iTunes and podcast writers add, and the experimental ones starting with X, Y or Z), no extended header can start there: its size is a small number, never four letters or digits. The tag is read without an extended header. In ID3v2.2 the same flag means compression and is not tested.
- Containers hand the ID3v2 tags they hold to the same parser, such as an MP4 file's ID32 box.
- A TSSE frame (TSS in ID3v2.2) is a claim of the encoder role: the specification defines it as the used audio encoder and its settings (ID3v2.3 section 4.2.1, ID3v2.4 frames section 4.2.5). The text of ffmpeg's muxer ("Lavf60.16.100"), which ffmpeg writes into it whatever encoded the audio, is a claim of the writer role, and its libavcodec encoder ID ("Lavc…") one of the encoder role. Either is low confidence, since the software that wrote the tag fills it in.
