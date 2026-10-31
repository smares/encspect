# ID3

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
  - frames the specification allows once appearing only once;
  - a byte order mark on every UTF-16 string.

  The complete list is in the [plan](../PLAN.md#tag-detection-and-false-positives).
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
