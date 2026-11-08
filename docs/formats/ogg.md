# Ogg

The Ogg container (RFC 3533). Its codecs have their own pages: [Speex](speex.md), [Vorbis](vorbis.md), [Opus](opus.md) and [FLAC](flac.md).

## What is read

- **Pages:** capture pattern, flags, granule position, serial and sequence numbers, the CRC-32 (checked) and the segment table. Each logical stream lists **Verified Page Checksums** (the pages whose CRC-32 equals the one computed) and, above 0, **Failed Page Checksums**; a page the data cuts short cannot be checked and is in neither.
- **Page CRC:** polynomial 0x04C11DB7, initial value 0, unreflected, over the page with the CRC field zeroed. A page cannot exceed RFC 3533's 65,307 bytes, since its segment count and lacing values are bytes.
- **Sync,** as for MPEG frames: at the start of the data and after damage, a page counts only when it passes its CRC and the next page or the end of the data follows it. Once in sync, the next page is read where the previous ends.
  - A page whose CRC fails but whose length the next page confirms is shown as a damaged page; one that another page starts inside, or the data ends inside, as cut short; anything else up to the next chain is a gap.
  - Sniffing needs the same chain, so "OggS" inside other data is not taken for Ogg. A chain that starts after up to 64 KiB of other data sniffs weaker (60) than MPEG audio (90).
- **Declared end:** Ogg states no size, but each page states its own length, so the chain of pages from the start of the data ends where the next bytes are not a page that fits and the last page passes its CRC.
  - A trailing tag signature before that end lies inside the last pages and is a near miss.
  - ID3v2 before the Ogg data and APE and ID3v1 after it, which taggers write, are the file's tags.
- **Two passes:** the scan keeps a 56-byte record and the segment table of each page and follows each stream's packets through their lacing values, reporting what breaks the specification, without building nodes.
  - Then each stream's packets are read again page by page and handed on, so the packets a codec gets are the ones the scan counted.
  - Page nodes are built in blocks of 1,024 when read.
- **Window:** the scan reads through two windows of 256 KiB and replaces the one used least recently, so a search that probes ahead of its position does not read the window again for every candidate. A candidate page is checked against the page that must follow it before its CRC is computed.
- **Logical streams:** each stream's packets are followed across pages, in multiplexed and chained files, with resync after damage.
  - A beginning-of-stream page after other pages, or after every stream of the current link has ended, starts the next link of a chain; streams of one link are multiplexed.
- **Codecs:** each stream's packets go to the codec parser its first packet names.
  - The first packet is recognized for Speex, Vorbis, Opus, FLAC, Theora, Skeleton, CELT, Kate, Dirac, PCM and VP8 by their identification headers. A stream whose codec has no parser gets its node with the pages' figures and an info finding; an unknown first packet gets one too.
- **Packets handed over:** the packets of a page that fails its CRC reach the codec parser as the page holds them, and so do the packets with bytes present on a truncated page, the one the data cuts flagged as cut (`MappedBytes.IsCut`); a packet left unfinished at the end of a stream is handed over cut. libogg drops these pages, so a decoder lacks their packets; the codec parsers count the packets' declared samples and record a cut packet as lost samples. The granule position of a truncated page is not used.
- **Length:** a logical stream that its codec parser reports once also lists **Container Samples** (its last granule position less the stream's Start Offset) and **Container Duration** (that length at the stream's sampling rate); for Opus the position counts at 48 kHz and includes the pre-skip.
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
- a serial number used twice, in one link or in a later link of a chain (RFC 3533, section 4: unique "within the scope of the physical bitstream"); two files joined end to end often break it;
- a stream that ends without its end-of-stream page (at the end of the data, before the next link, or at a damaged end-of-stream page, which decoders drop), pages after the end of stream, and a stream that goes on after the next link's beginning-of-stream pages;
- sequence repeats, a granule position on a page that completes no packet, or -1 on one that does;
- a page that starts a fresh packet while one is unfinished (the packet is dropped, as the flag says; libogg joins them instead), and a continued-packet flag with nothing unfinished (the bytes are skipped, as libogg skips them);
- a stream that ends inside a packet, and packets over 64 MiB, which are not read.

Findings of one kind are reported once per stream, with a count.

## Reference for sample counts

- **Xiph's decoders are the reference** for the number of samples of an Opus or Vorbis stream. They are run on a Linux runner by `.github/workflows/official-decode.yml` (see "Opus and Vorbis decoding by libopus and libvorbis" in [development.md](../development.md), which also lists the differences found), two ways, each in a child process of its own:
  - **the reader path:** `opusdec` and `oggdec`, which apply the format's trimming; their samples are counted from the PCM bytes they wrote, with the channel count from their own output;
  - **the direct path:** a small harness (`scripts/fixtures/ogg_decode.c`) that splits the file into packets with libogg and feeds libopus (`opus_multistream_decode_float`, the projection decoder for mapping family 3) or libvorbis (`vorbis_synthesis`) one packet at a time, so that damaged data reaches the decoder. It trims nothing: libvorbis gets `granulepos` -1 and no end-of-stream flag on every packet. A third run gives libvorbis the real granule positions.
- **Two stages.** The coded stage is what the packets code, before any trimming: the direct path's total, set against "Coded Samples". The trimmed stage is what remains after the pre-skip, the start and the end trimming: the reader's total, set against the stream's `samples`. The analyzer's own trimming is thereby held to the owner's reader; the harness computes none.
- **The reference** is the reader's total only when it reached the end without a failed read, the direct path fed every page, and the reader's count lies between what the granule positions of the streams' last pages and the coded total allow; else the trimmed stage has no reference. The coded stage's reference is the direct path's total when libogg dropped no page; a stream whose headers the decoder refuses is listed with that verdict. A partial total is recorded and never used. `oggdec` and `opusdec` report neither a partial read nor a lost page by their exit status: on the one fixture with a chain of two links of equal parameters, `oggdec` writes the first link and stops with exit 0, and `opusdec` exits 0 after "Decoding error.".
- **FFmpeg is the second cross-check**, for files with one logical stream (it decodes the first audio stream only): its default count at the trimmed stage and, for Opus, `-flags2 +skip_manual` at the coded stage. A difference from the reference is listed per file, not hidden by agreement with the analyzer.
- The reference figures, the run, and every difference with its status are in `tests/fixtures/ogg/official-decoders.json`, which `OggOfficialDecodersTests` holds the analyzer to.
- Derived damage variants (cuts, a changed byte, a removed or repeated page or packet; `scripts/fixtures/derive_variants.py`) are decoded the same way and matched by SHA-256; they are not committed.
- Only Opus and Vorbis are in scope; Speex has no official decoder run.
- The analyzer's Vorbis end trimming follows libvorbis's granule handling since run 41457775152 (see below). The project lead decided the differences of step 2 on 2026-11-01:
  - **`keep`:** `vorbis-packet-errors.ogg` and `vorbis-vendor-variants.ogg` (the analyzer agrees with libvorbis fed packet by packet, with and without the real granule positions; `oggdec` writes fewer samples, a reader behavior), and the streams whose headers the decoder refuses while the analyzer counts samples (the analyzer describes what the packets code, next to its findings on the invalid headers).
  - **`explained`:** `opus-packets-corrupt.opus` at the coded stage: "Coded Samples" counts the positions the packets declare, the packets libopus refuses carry findings, and a future Encspect Opus decoder leaves them as gaps counted as lost samples (gaps, not concealment).
  - **Declined:** accepting a reader's count when it equals the libvorbis run with the granule positions; both runs share libvorbis's granule handling, so agreement shows a complete read, not correct trimming.
  - **Variants** (in the comparison only): a repeated page of an Opus file, which `opusdec` skips while the analyzer and libogg count it (10 of 24 files), is `noted`, since the analyzer equals libogg's own packet stream (the 4 Vorbis files with a repeated page are below). A repeated packet put the analyzer 38 to 128 samples below `oggdec` in 10 of the 14 Vorbis files, and a repeated end-of-stream page 384 or 986 above it in 4 (the Opus variants of the repeated packet were equal); the cause was the Vorbis end trimming (counted from the first page's position, taken from every position and not at every end-of-stream page, where libvorbis counts from the latest position it takes and trims at each one), and since run 41457775152 the analyzer equals `oggdec` in all 14 Vorbis files and `opusdec` in the 10 Opus files with a repeated packet. The 10 Opus files with a repeated page remain `noted`.

## Not covered

- **Recognized, not analyzed:** the mappings of Theora, Skeleton, CELT, Kate, Dirac, PCM and VP8 are told apart by their first packet and named, but only their pages are read; the stream gets `ogg.codec-not-analyzed`, and no stream figures, packets or tags.
- **Unknown mappings:** a first packet no mapping names is `ogg.codec-unknown`; its pages are read too.
- **Analyzed:** Speex, Vorbis and Opus.
- **Streams past the limit** above, and the packets of any stream over 64 MiB, are not read.
- Anything that needs decoding: no audio is rebuilt.

## Articles

- **Articles:** `ogg-pages`, `granule-position`.
- **Shared with other formats:** `crc`.
- The Ogg page CRC points to `ogg-pages`, since `crc` is about the MPEG frame CRC; `crc` links to `ogg-pages`.
