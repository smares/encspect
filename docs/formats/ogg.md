# Ogg

The Ogg container (RFC 3533). Its codecs have their own pages: [Speex](speex.md), [Vorbis](vorbis.md) and [Opus](opus.md).

## What is read

- **Pages:** capture pattern, flags, granule position, serial and sequence numbers, the CRC-32 (checked) and the segment table.
- **Logical streams:** each stream's packets are followed across pages, in multiplexed and chained files, with resync after damage.
- **Codecs:** each stream's packets go to the codec parser its first packet names.

## Findings

What breaks RFC 3533, such as:

- a missing beginning- or end-of-stream page;
- a gap in the page sequence;
- granule positions that go back;
- continuation flags that do not match.
