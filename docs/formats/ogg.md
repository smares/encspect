# Ogg

The Ogg container (RFC 3533). Its codecs have their own pages: [Speex](speex.md), [Vorbis](vorbis.md), [Opus](opus.md) and [FLAC](flac.md).

## What is read

- **Pages:** capture pattern, flags, granule position, serial and sequence numbers, the CRC-32 (checked) and the segment table.
- **Logical streams:** each stream's packets are followed across pages, in multiplexed and chained files, with resync after damage.
- **Codecs:** each stream's packets go to the codec parser its first packet names.
- **Playback groups:** the streams of one link play at the same time, so they share a playback group and the summary counts the longest once; the links of a chain follow one another and add up.
- **Limits:**
  - a stretch of data follows 4,096 logical streams plus one per 8 KiB of data; the pages of later streams are recorded and counted in `ogg.too-many-streams` (an info naming the limit), not followed;
  - the searches for the next page after damage share one budget of checksum work in proportion to the data (four times its size and 1 MiB); `ogg.resync-limit` says it was spent, and what no page was found for is read as a gap.

## Findings

What breaks RFC 3533, such as:

- a missing beginning- or end-of-stream page;
- a gap in the page sequence;
- granule positions that go back;
- continuation flags that do not match;
- a serial number used twice, in one link or in a later link of a chain (RFC 3533, section 4: unique "within the scope of the physical bitstream").

## Reference for sample counts

- **Xiph's decoders are the reference** for the number of samples of an Opus or Vorbis stream. They are run on a Linux runner by `.github/workflows/official-decode.yml` (see "Opus and Vorbis decoding by libopus and libvorbis" in [development.md](../development.md), which also lists the differences found), two ways, each in a child process of its own:
  - **the reader path:** `opusdec` and `oggdec`, which apply the format's trimming; their samples are counted from the PCM bytes they wrote, with the channel count from their own output;
  - **the direct path:** a small harness (`scripts/fixtures/ogg_decode.c`) that splits the file into packets with libogg and feeds libopus (`opus_multistream_decode_float`, the projection decoder for mapping family 3) or libvorbis (`vorbis_synthesis`) one packet at a time, so that damaged data reaches the decoder. It trims nothing: libvorbis gets `granulepos` -1 and no end-of-stream flag on every packet. A third run gives libvorbis the real granule positions.
- **Two stages.** The coded stage is what the packets code, before any trimming: the direct path's total, set against "Decoded Samples". The trimmed stage is what remains after the pre-skip, the start and the end trimming: the reader's total, set against the stream's `samples`. The analyzer's own trimming is thereby held to the owner's reader; the harness computes none.
- **The reference** is the reader's total only when it reached the end without a failed read, the direct path fed every page, and the reader's count lies between what the granule positions of the streams' last pages and the coded total allow; else the trimmed stage has no reference. The coded stage's reference is the direct path's total when libogg dropped no page; a stream whose headers the decoder refuses is listed with that verdict. A partial total is recorded and never used. `oggdec` and `opusdec` report neither a partial read nor a lost page by their exit status: on the one fixture with a chain of two links of equal parameters, `oggdec` writes the first link and stops with exit 0, and `opusdec` exits 0 after "Decoding error.".
- **FFmpeg is the second cross-check**, for files with one logical stream (it decodes the first audio stream only): its default count at the trimmed stage and, for Opus, `-flags2 +skip_manual` at the coded stage. A difference from the reference is listed per file, not hidden by agreement with the analyzer.
- The reference figures, the run, and every difference with its status are in `tests/fixtures/ogg/official-decoders.json`, which `OggOfficialDecodersTests` holds the analyzer to.
- Derived damage variants (cuts, a changed byte, a removed or repeated page or packet; `scripts/fixtures/derive_variants.py`) are decoded the same way and matched by SHA-256; they are not committed.
- Only Opus and Vorbis are in scope; Speex has no official decoder run.
- The analyzer's counts have not been changed to the reference. The project lead decided the differences of step 2 on 2026-11-01:
  - **`keep`:** `vorbis-packet-errors.ogg` and `vorbis-vendor-variants.ogg` (the analyzer agrees with libvorbis fed packet by packet, with and without the real granule positions; `oggdec` writes fewer samples, a reader behavior), and the streams whose headers the decoder refuses while the analyzer counts samples (the analyzer describes what the packets code, next to its findings on the invalid headers).
  - **`explained`:** `opus-packets-corrupt.opus` at the coded stage: "Decoded Samples" counts the positions the packets declare, the packets libopus refuses carry findings, and a future Encspect Opus decoder leaves them as gaps counted as lost samples (gaps, not concealment).
  - **Declined:** accepting a reader's count when it equals the libvorbis run with the granule positions; both runs share libvorbis's granule handling, so agreement shows a complete read, not correct trimming.
  - **Variants** (in the comparison only): a repeated page, which `opusdec` and `oggdec` skip while the analyzer and libogg count it (14 of 24 files), is `noted`, since the analyzer equals libogg's own packet stream. A repeated packet, where the analyzer is 38 to 128 samples below `oggdec` and ffmpeg agrees with `oggdec` (10 of 24 files), is to be looked into in a follow-up; there is no conclusion yet.

## Not covered

- **Recognized, not analyzed:** the mappings of Theora, Skeleton, CELT, Kate, Dirac, PCM and VP8 are told apart by their first packet and named, but only their pages are read; the stream gets `ogg.codec-not-analyzed`, and no stream figures, packets or tags.
- **Unknown mappings:** a first packet no mapping names is `ogg.codec-unknown`; its pages are read too.
- **Analyzed:** Speex, Vorbis and Opus.
- **Streams past the limit** above, and the packets of any stream over 64 MiB, are not read.
- Anything that needs decoding: no audio is rebuilt.
