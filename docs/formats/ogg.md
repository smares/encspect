# Ogg

The Ogg container (RFC 3533). Its codecs have their own pages: [Speex](speex.md), [Vorbis](vorbis.md) and [Opus](opus.md).

## What is read

- **Pages:** capture pattern, flags, granule position, serial and sequence numbers, the CRC-32 (checked) and the segment table.
- **Logical streams:** each stream's packets are followed across pages, in multiplexed and chained files, with resync after damage.
- **Codecs:** each stream's packets go to the codec parser its first packet names.
- **Playback groups:** the streams of one link play at the same time, so they share a playback group and the summary counts the longest once; the links of a chain follow one another and add up.
- **Limits:**
  - a stretch of data follows its first 4,096 logical streams; the pages of later ones are recorded and counted in `ogg.too-many-streams`, not followed;
  - the search for the next page after damage spends a budget of checksum work in proportion to the data (four times its size and 1 MiB); `ogg.resync-limit` says it was spent, and what no page was found for is read as a gap.

## Findings

What breaks RFC 3533, such as:

- a missing beginning- or end-of-stream page;
- a gap in the page sequence;
- granule positions that go back;
- continuation flags that do not match;
- a serial number used twice, in one link or in a later link of a chain (RFC 3533, section 4: unique "within the scope of the physical bitstream").

## Not covered

- **Recognized, not analyzed:** the mappings of FLAC, Theora, Skeleton, CELT, Kate, Dirac, PCM and VP8 are told apart by their first packet and named, but only their pages are read; the stream gets `ogg.codec-not-analyzed`, and no stream figures, packets or tags.
- **Unknown mappings:** a first packet no mapping names is `ogg.codec-unknown`; its pages are read too.
- **Analyzed:** Speex, Vorbis and Opus.
- **Streams past the limit** above, and the packets of any stream over 64 MiB, are not read.
- Anything that needs decoding: no audio is rebuilt.
