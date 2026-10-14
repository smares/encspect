# APE tags

- APEv1 and APEv2.
- Found through the footer at the end of the file or just before another trailing tag. An APEv2 tag can also be found through its header at the start of the file or directly after a leading tag; APEv1 has no header.
- Full confidence needs:
  - a header and footer that agree in everything but the is-header flag;
  - a footer that does not claim to be missing;
  - items that end exactly at the footer;
  - text items in UTF-8.
- An APE tag whose header puts `TAG` at 128 bytes before the end of the file wins over a supposed ID3v1 tag there. The [plan](../PLAN.md#tag-detection-and-false-positives) explains this collision.
- Musepack SV8 chapters hold APEv2 items, which go to this parser.
- A text item with the key `Encoder` (in any case: FFmpeg writes `encoder`, with its Lavf version) is an encoder claim, which the format parsers weigh as they do an ID3v2 TSSE frame. The software that writes the tag fills it in, so a claim has low confidence. An empty item is none.
