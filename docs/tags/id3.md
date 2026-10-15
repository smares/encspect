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
- Containers hand the ID3v2 tags they hold to the same parser, such as an MP4 file's ID32 box.
- A TSSE frame (TSS in ID3v2.2) is a claim of the encoder role: the specification defines it as the used audio encoder and its settings (ID3v2.4 frames section 4.2.5). The text of ffmpeg's muxer ("Lavf60.16.100"), which ffmpeg writes into it whatever encoded the audio, is a claim of the writer role. Either is low confidence, since the software that wrote the tag fills it in.
