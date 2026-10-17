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
- Item limit, so that a tag of millions of items (11 bytes each at the least) cannot grow the report:
  - every item is checked, and the first 1,024 become nodes; the other items are counted in one Info finding, `ape.items-limited`, which points at the Items node and gives how many are not listed;
  - the findings about items (`ape.duplicate-key`, `ape.key-forbidden`, `ape.item-flags-unknown`, `ape.item-type-reserved`, `ape.text-not-utf8`, `ape.v1-flags`, `ape.v1-not-ascii`, `ape.v1-zero-byte`) are one per kind with a count and point at the first case, or at the Items node when that item is not listed;
  - repeated keys are looked for against the keys of the listed items, so two unlisted items that repeat each other's key are not reported;
  - an `Encoder` item that is not listed gives no claim.
- A text item with the key `Encoder` (in any case: FFmpeg writes `encoder`, with its Lavf version) is a claim of the writer role, which the format parsers resolve apart from the encoder. No source at hand lists an APEv2 key for the encoder (the key list could not be checked), so the item is given to neither role and goes to the writer, unless the text is a libavcodec encoder ID (`Lavc…`), which names the encoder in any field (ffmpeg's muxer version `Lavf…` stays the writer). The software that writes the tag fills it in, so a claim has low confidence. An empty item is none.
