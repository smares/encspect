# ID3

## ID3v1

- ID3v1, ID3v1.1 and the Enhanced TAG.
- Found at exactly 128 bytes before the end of the file.
- It has almost nothing to validate, so it never gets more than medium confidence.

## ID3v2

- ID3v2.2, 2.3 and 2.4, zlib-compressed frames included.
- Found at the start of the file or directly after another tag. ID3v2.4 tags are also found through their footer, and an ID3v2.4 SEEK frame leads to the next tag.
- Full confidence needs everything to check out, among other things:
  - known flags;
  - an extended header laid out as its version defines it;
  - frames that fit the declared size, with padding only at the end;
  - frames the specification allows once appearing only once;
  - a byte order mark on every UTF-16 string.

  The complete list is in the [plan](../PLAN.md#tag-detection-and-false-positives).
- Containers hand the ID3v2 tags they hold to the same parser, such as an MP4 file's ID32 box.
