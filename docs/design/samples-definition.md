# Samples: what a stream's units declare, and lost samples

Status: approved by the project lead on 2026-11-02, after three independent reviews, with every recommendation of the [open questions](#open-questions) as listed, the two departures from written rules included (question 1: a checksum failure alone is not lost, status `noted`; question 2: packets of damaged Ogg pages are handed over flagged). The core commit is built (`StreamInfo.LostSamples` and its output, `MappedBytes.IsCut`, Decision A, the topic article and the checks that need no per-format change); the per-format changes are not.

- **Checked at:** `main` 465988f (it holds the Ogg and TTA cross-checks, #209 and #210), `fix/wma-pro-trim` 57ed8cb (PR #212), `feat/wma-container-duration` f95f188, `ci/official-decode-aac` fea550f (a change to the comparison script only; the committed `official-decoders.json` equal b03bdfc's).
- **Figures** were computed by scripts over the committed goldens (`*.report.json`, `*.expected.json`, `*.decode.json`) and the `official-decoders.json` files. A claim no code, golden or document confirms is marked **unverified**.

## Decided

- **The lead, 2026-11-02:** a stream's `samples` are the samples its units declare, at the sample rate the analyzer determines. Units that are damaged or cannot be decoded stay in `samples`, and their declared samples are reported apart as lost samples. One pull request aligns the formats with goldens.
- **The lead, 2026-11-02:** every recommendation of the [open questions](#open-questions), as listed there; the status rules of the AAC step stay scoped to AAC, and the [cross-check statuses](#cross-check-statuses) below apply to every format once the alignment lands.
- **Consequences of it, not separate decisions:** the TTA cut-last-frame `keep` of 2026-11-01 is revisited; WMA Professional's rule (Microsoft's decoder on undamaged streams, PR #212) is the owner's decoder rule that this definition takes for WMA.
- **Standing rules** that settle two questions the earlier draft left open:
  - [PLAN.md](../PLAN.md) (gaps rule): the measurements count a lost frame's positions as lost samples, a lost first or last frame included. The measurer must therefore see the stream's coded-stage start and end (Decision A below).
  - CLAUDE.md, "One concept, one name": the coded stage has one name, **Coded Samples**. Only when the other names go is open ([question 6](#open-questions)).

**Decision A (follows from the above):** the host gives the measurer the stream's coded-stage start and end, taken from the `CodedFrame`s, so a lost first or last unit is in the measured `lostSamples`. Without it `SampleLocator` (`FirstPosition + Samples + LostSamples` against `Total` = `StreamInfo.Samples`) puts every frame's mark early by the lost tail. Tests: `SampleLocatorTests`, `FrameMapGraphTests`, `AudioPanelTests` in `Encspect.Gui.Tests`, with a cut-last-frame case added, and `TrueHdRunsTests` for the TrueHD gap.

- **As built (core commit):** `FirstPosition` stays the first block's, because the grids and graphs start from it, so a lost first unit cannot move it. The record gains `LeadingLostSamples` and `TrailingLostSamples` (inside `LostSamples`, JSON above 0 only), each lost end counts as one gap, and `SampleLocator` ends the span at `FirstPosition + Samples + LostSamples - LeadingLostSamples`. Only a stream whose decoding reached its end (status `Complete` or `Incomplete`) gets them. TrueHD hands unreadable units with 0 samples today, so its gap appears with the TrueHD commit.
  - Where a lost last unit lies past the stream's `samples` (a header count the blocks outrun, `wavpack/built-set-without-final-limit.wv`: 3,000 against blocks that cover 17,000), the locator takes the units' span as the span the frames cover.
  - The measured figures are of a different stage from the list of unchanged Monkey's Audio files below: `built-old-version-3800.ape` measures 12,544 trailing lost samples because the decoder refuses its final frame (`decode.frame-invalid`, 12,544 samples against the 9,216 a version 3.80 frame takes), a real gap in the decoded audio; the stream's own `LostSamples` is the parser's figure and a separate one.

## Today

- **TTA:** a frame cut by the end is left out (`tta.frame-cut`). The lead kept that on 2026-11-01 for four derived, uncommitted variants (`keep`). The committed `built-cut-*` files have no reference (`tta -d` stops on them).
- **MP4:** a sample not wholly in the file is left out (`Mp4Parser.Tracks.cs`: `InFile`, `SampleRanges`, `Packets`). `truncated.m4a` counts 9,216, without the sample of which the file holds 1 byte.
- **Ogg:** a page that fails its CRC, and a truncated page, are dropped whole, as libogg does and PLAN.md states: `PageRecord.IsLost` is `CrcMismatch | Truncated`, `AddPage` calls `Assembler.Lose()` and `PacketBuilder.Feed` returns early. Their packets are not handed to any codec parser, so their positions are shown nowhere ([question 2](#open-questions)).
- **WMA:** see [WMA](#wma).

## Words

- **Unit:** what a format codes one at a time and states the length of in samples: a frame, block, packet, access unit, superframe or raw data block; for PCM, a sample frame.
- **Declared samples** of a unit: the samples per channel the format's decoding process assigns to it, read from its own syntax (a header, a TOC byte, a mode number) or from the stream's fixed parameters (a TTA or Monkey's Audio frame length, an ALAC cookie's frame length). It is the lead's "positions the units declare".
- **`samples`:** the sum over the counted units of their declared samples, less the trims below. A repeated or overlapping unit counts again (the decoy frame of `flac/built-damaged-frame-after-frame.flac`, `built-frame-repeated.flac`).
- **Coded stage:** the sum of the declared samples of the counted units. **Trimmed stage:** the coded stage less the trims the stream states. The parser's coded-stage figure is **Coded Samples**.
- **Cut unit:** a counted unit whose declared length in bytes is more than the bytes present in the file (the data ends first, or the next accepted unit starts first). "Data" is always the bytes in the file, as the lead's decision speaks of positions the file declares, never a size a header states for the whole data. The length is the unit's own (MPEG audio, AC-3, DTS, ADTS), the container's (an MP4 sample size, a Matroska block, an ASF media object), or, for a frame with no length field (FLAC, Monkey's Audio, ALAC, TTA), the frame the file's bytes end inside (Monkey's Audio: the file's bytes, not the descriptor's frame-data size).
- **Lost samples:** the declared samples, at the coded stage, of counted units that
  - are cut, or
  - the parser refuses to read (their syntax breaks, or a check that guards the syntax fails and the parser stops), or
  - sit in a stream whose state was lost (TrueHD units after damage until the next major sync; WMA frames after an invalid frame in the same superframe).

  It is what the parser can show, not a claim that the audio cannot be rebuilt ([question 4](#open-questions)).
- **Missing samples:** positions no unit declares but the numbering or a header total shows (a FLAC sample number that jumps, a TTA header count above the frames present, packets lost with a damaged Ogg page that nothing hands over). Neither samples nor lost samples ([question 3](#open-questions)).
- **Stated samples:** a count without units the analyzer reads (an encrypted track's timing, a WAV fact chunk, the ASF play duration). Not declared samples.

## Definition

### The rule

1. **A unit counts** when the file holds the syntax that declares its samples: its header, TOC byte or mode bits; or its first byte, where the length comes from the stream's parameters (TTA, Monkey's Audio, ALAC) or an index (an MP4 sample table entry); all the bytes of a PCM sample frame. A unit with no byte in the file is not counted ([question 3](#open-questions)).
2. **Its samples** are those it declares, whether or not it can be decoded: a cut, refused or broken unit counts in full. Cut is decided by declared length against bytes present, not by what ends the unit: a unit the next sync cuts before its declared length is cut, also where the declaration is what was damaged ([question 4](#open-questions)).
3. **A unit that declares nothing counts nothing:** bytes skipped while searching for sync (`aac.sync-lost`, `flac.data-skipped`), a Vorbis packet that codes no block (Vorbis I 4.3.1), an Opus part of more than 120 ms (`PartRecord.Samples` gives 0), a Matroska block whose frames are not read (`matroska.block-short`, `matroska.lacing-invalid`), a WavPack block with a cut header (`built-truncated-header.wv`).
4. **A unit the analyzer cannot place** counts nothing: TrueHD units before the first major sync.
5. **A stream whose units are not read** (an encrypted track, a coding without a parser) keeps its stated samples; its lost samples are 0, told apart by `Protection` or a `*.codec-not-analyzed` finding. Encryption alone does not stop the reading: the encrypted TTA files (`tta.audio-format-encrypted`) have their frames read and their CRC-32s checked, so their lost samples are computed.
6. **Samples a limit skips** (record caps, `aac.streams-limited`, `mp4.samples-overlap`, whose samples have bytes but are over the byte budget) are outside the definition. The ADTS parser counts frames past its 2,097,152-record cap from their headers; they count and are lost where the header pass sees a cut.

### Trimming

- **`samples` is the trimmed stage** where the codec's syntax or normative mapping states a trim, as today:
  - Opus: pre-skip (RFC 7845 sections 4.2 and 5.1) and end trimming (section 4.4);
  - Vorbis and Speex: the granule positions' start and end trimming;
  - Musepack: the delay of 481 and the end of the last frame, capped by the header's count;
  - WMA Professional and Lossless: the frames' trims; Professional follows Microsoft's decoder (below);
  - TrueHD: the samples an end of stream marker drops.
- **Container and encoder figures stay their own** (MP4 edit lists, iTunSMPB, LAME and Xing, DiscardPadding). The sample rate is out of scope.
- **Coded Samples** stands next to every trimmed `samples`. Today it is "Decoded Samples" (Opus, Vorbis) and "Coded Samples" (Speex, Musepack, and standard WMA where `samples` follow Microsoft's rule: the valid frames times the frame length); WMA Professional, WMA Lossless and TrueHD have none ("Trimmed Samples" on the WMA branches is the sum of the trims, not a coded-stage figure). Those three gain a node where it differs from `samples`.
- **Lost samples are counted at the coded stage,** inside or outside the trimmed range: a lost first Opus packet inside the pre-skip is lost. A lost unit need not be in `samples`: `asf/writer-stamp-last.asf` has `samples` 0 and one invalid frame.

### WMA

WMA has no public specification, so "declared samples" for an undamaged stream means the owner's decoder rule, computed from the syntax.

- **Professional** (57ed8cb): Microsoft's rule, E = N × F + (F − L_last) / 2 less the start and end cuts ([wma-pro-format.md](wma-pro-format.md), "What Microsoft's decoder outputs"), applies where the stream has no loss, invalid frame or long packet (it also applies to streams that end in an unfinished frame and to streams without the length prefix). Otherwise the frames read without error less their trims.
  - The rule's prediction equals Windows' figure on all 18 streams that end in an unfinished frame (`wma.frame-unfinished`) in the Windows records, among them `pro-stereo-128-cut-open-3` 7,104 (the frames' count 6,144), `cut-open-27` 55,808 (55,296), `Classical_96_24_6_Q75_2_13.wma` 9,029,504 (9,027,584), and two public files where both agree. The analyzer applies the rule to them and keeps the finding. The unfinished frame's F as lost samples ([question 9](#open-questions)) is not built: it waits for this alignment, which adds `LostSamples`.
  - The three public files without the length prefix (Windows 10,803, 33,331 and 1,921 more than the frames before the last packet's) are explained: the analyzer now reads the frames that start in the last packet, and the rule gives Windows' 8,364,595, 8,364,595 and 18,208,641.
- **Standard** (`feat/wma-container-duration`, after the black-box runs 41252306359, 41375872453, 41409006544 and 41412867847): Microsoft's rule, (K − 2) × F + (F − min(B, L)) / 2 with K the frames whose bits are complete, the frame the last superframe announces and holds whole included, applies to version 2 streams with a bit reservoir and variable block lengths and no damage (invalid frame, short or invalid superframe, rejected bit offset, records limit, fewer than two complete frames). Every other standard stream keeps the frames' count (valid frames less the first, times the frame length). The old overlap-tail formula was a fit and is gone; `Container Samples` stays beside it ([development.md](../development.md), "Differences found").
- **Damaged streams:** each invalid frame counts F and is lost, and so are the frames the superframe declares after it. How Microsoft's rule composes with lost frames is [question 9](#open-questions); no black-box run on damage exists, so the cuts applied to damaged streams are **unverified**.

### Duration, frames and bitrate

- **`duration`** is `samples` over the rate, so it includes lost samples.
- **Frame counts and bitrates differ by format today:**

  | Fixture | Frames | Samples | Average bitrate | Basis today |
  |---|---|---|---|---|
  | `ac3/truncated.ac3` | 6 | 9,216 | 179.5 kbps | cut frame in both; (5 × 768 + 468) bytes over 9,216 samples |
  | `mpeg/truncated-last-frame.mp3` | 5 | 5,760 | 128.0 kbps | cut frame in neither |
  | `flac/built-cut-mid-frame.flac` | 4 (cut in) | 12,288 | 265.8 kbps | count includes it, samples do not |
  | `tta/built-cut-mid-frame.tta` | 1 (cut out) | 46,080 | 1.56 kbps | cut frame in neither |
  | `truehd/built-unit-cut.thd` | 80 (cut in) | 3,160 | 1,059.1 kbps | count includes it, samples do not |

- **Frame counts do not change.** The average bitrate is the parser's bytes over the time its samples last, so it changes wherever a newly counted unit's bytes are already in the byte tally (ALAC and TrueHD units whose bytes are all present, FLAC's cut frame). That is accepted; a hidden old-basis figure per parser is not worth it. The change lists name it, and a test holds that `averageBitrate` differs only in goldens with lost samples.
- **Also changing:** finding messages that read the count (`flac.total-samples-mismatch`), MP4 "Track N Samples" ranges, Musepack Coded Samples, TrueHD nodes.

## Lost samples

### Name and meaning

- **Name:** `LostSamples` on `StreamInfo`, `lostSamples` in the stream's JSON, "Lost Samples" as a statistics node and in the text report. Prose says "lost samples"; nothing else names it.
- **The measurements already use the name** (`StreamMeasurements.LostSamples`, `measurements.lostSamples`, "N gaps skipped M samples"). The two figures differ by definition:
  - the stream's: declared samples of lost units, from the parser, without decoding; a subset of the coded stage;
  - the measured one: the positions the output lacks between its first and last block: lost units the decoder dropped plus missing samples at jumps, and, with Decision A, the lost first and last units.
- **Proposal ([question 7](#open-questions)):** one name for the one concept, positions without audio, at two stages; two JSON positions (`streams[].lostSamples`, `streams[].measurements.lostSamples`); each description states its stage. No schema change.
- **A figure, never a verdict.** The description states the basis ("the declared samples of units cut by the end of the data, units that break the syntax, and units that cannot be read until the stream's next sync point"). No finding is raised; the text never says the file is broken.

### What counts

- **Cut:** `mpeg.frame-cut`, `mpeg.frame-truncated`, `ac3.frame-damaged`, `ac3.frame-truncated`, `aac.frame-damaged`, `aac.frame-truncated`, `dts.frame-damaged`, `dts.frame-truncated`, `flac.frame-cut`, `tta.frame-cut`, `truehd.unit-cut`, `wavpack.block-truncated`, `monkeys-audio.frame-cut`, `musepack.frame-truncated`, and the packets the containers flag.
- **Unreadable:** `CodedFrame.IsUnreadable` units (`flac.subframe-invalid`, WavPack's refused blocks, ALAC's unreadable frames), Opus parts that break RFC 6716 section 3.4 (`opus.packet-r1` to `-r7`, `opus.self-delimiting`), AAC blocks with the codes of [question 8](#open-questions), WMA invalid frames.
- **Checksums:** a failing checksum alone does not make a unit lost; a unit is lost when the parser refuses to read it.
  - The TrueHD major sync CRC-16 and the unit parity nibble guard syntax the parser needs and it stops after a failure (`built-sync-crc-mismatch.thd`, `built-parity-mismatch.thd`): lost.
  - Checksums over coded data whose syntax reads leave the unit counted and not lost: FLAC CRC-16, MPEG and AC-3 CRCs, the TTA and WavPack checksums, TrueHD substream CRC-8 and parity (`built-substream-crc-mismatch.thd` is 3,200, not lost). Encspect's FLAC decoder decodes such frames.
  - The Ogg page CRC is not in this list: libogg drops the page, and so does the analyzer today ([question 2](#open-questions)).
- **Not lost:** units a decoder refuses for a reason the parser does not check (they show only in the measurements); streams whose headers a decoder refuses as a whole.

### Where it lives

- **A `StreamInfo.LostSamples` (`long`) and a "Lost Samples" node, both only above 0.** `ReportJsonWriter` already writes `multilingualChannels` and `bitsPerSample` conditionally; unconditional writing would change all 2,602 `.report.json` goldens. An absent field reads as 0.
- **Without decoding,** from per-unit records the parsers keep (`Entry.IsTruncated`, `Frame.Cut`, `walk.IsCut`, unreadable status, Opus `PartStatus`, TrueHD units after damage, WMA invalid frames, the containers' flag). It is added in the pass that adds the unit to `samples`, with no memory per unit. A WavPack set with one refused block is lost once, by `block_samples`.
- **Every counted unit, lost ones included, must reach the session** with its `FirstSample` and `Samples`. FLAC hands the cut frame over (`IsDamaged`), Monkey's Audio too, WavPack marks it unreadable. **TrueHD hands unreadable units with `Samples` 0 and does not advance `FirstSample`:** it must advance by the lost units and hand them over with 40 samples (at 48 kHz). That puts a gap in the decoded stream, so the measurer's FFT frames, gating blocks and runs reset there. No new `CodedFrame` flag is needed.
- **Output:** JSON `lostSamples` after `samples`, the text stream line and the GUI summary, all above 0 only (a field is added; `schemaVersion` stays 1). The CSV column `Lost Samples` is appended, summed over the streams the `Samples` column adds (audio streams, a playback group once by its longest stream, `ReportSummary.cs`). [cli.md](../cli.md) says so and its "six more columns" sentence is updated.

### Invariant and test

- **Statement:** at the coded stage, `Verification.DecodedSamples ≤ coded stage − LostSamples`, with equality where
  - the decoding is `Decoded`,
  - the stream is not DSD (WavPack DSD counts at the DSD rate and decodes to one eighth) or the rate ratio is applied,
  - no `decode.frame-invalid` lies outside the lost units, and
  - no unit overlaps or repeats another.
- **A static check, no new decode pass:** 771 `*.decode.json` goldens already record `decodedSamples` (768 with a report; `DecoderFixtureTests` decodes them). The test reads report and decode golden together, with no shard cost. Today 86 of the 768 differ from `samples`; after the change about 60 still fail equality (about 30 with `NotPossible`, DSD, units Encspect's decoder refuses that the parser does not flag, such as Monkey's Audio `frame-misplaced` or TrueHD `built-parameters-change`, and the cut-but-decodable frames of [question 4](#open-questions)). They are listed with their reason; the `≤` holds everywhere.
- **Measured form:** `--measure` agrees on the decoded-and-damaged cases only. None of the 81 `*.measure.json` goldens (all in `tests/fixtures/measurement`, none damaged) changes.

## What containers hand over

**The gap.** `MappedBytes` carries `EndPosition`, `EndPositionField`, `IsEndOfStream` and `FollowsGap`, and nothing about a cut. A frame that states its own length (ADTS, AC-3, DTS, MPEG audio, TrueHD) shows its cut itself, and so does a FLAC or TTA frame the data ends inside. A raw AAC access unit, an ALAC frame, an Opus, Vorbis or Speex packet and a WMA media object do not: a cut Opus code-0 packet reads as a valid shorter packet.

**The change in Core.Abstractions ([question 10](#open-questions)):** one member on `MappedBytes`, `IsCut`, default false, set by the container: the bytes are not the whole packet. Its packet members are `init` accessors, so they can be set only where a packet is constructed: the core commit adds an `isCut` parameter to the factories (`MappedBytes.Read`, which MP4 uses, and `Opaque`) and a copy method `AsCut()` for packets a content decoder returns (`decoder.Decode` in the Matroska route). A parser that cannot see the cut counts a flagged packet by its declaration (TOC, mode, cookie, configuration: 1,024 per raw AAC block) and records it cut and lost; a parser that sees it ignores the flag. There is no declared-length member: no parser would read it, and for decoded bytes it would be meaningless. `ParseStream` needs nothing (the parser sees the reader's end).

**What each container supplies:**
- **MP4.** A sample whose first byte is in the file is handed with the bytes present and `IsCut` (`truncated.m4a`: 1 byte of the last sample; 10 more samples have no byte and fall under [question 3](#open-questions)). Two changes beyond `Packets`: `Samples` (`Mp4Parser.Tracks.cs:363-370`) folds a sample not wholly in the file into one aggregate entry with the rest of its chunk when all sizes are equal, so the partial sample is split out and `SampleWalk.Placed` counts its bytes present; and `SampleRanges` includes the bytes present, since the MPEG audio `ParseStream` slice ends at the last range and would otherwise never see the cut frame.
- **Matroska and WebM.** A block is cut when its declared end passes the file end (`matroska.element-truncated`) or its Cluster ends first (`matroska.element-overruns-parent`). A separate bool on the block record, not a `BlockStatus` bit: three tests read `block.Status is not (None or LaceSingle)` (`Route.cs:455`, `Blocks.cs:179`, `:247`) and a new status would drop cut blocks from packets and statistics. The unlaced frame and the last Xiph- or EBML-laced frame are cut; a cut inside an explicit lace size breaks the lace (`matroska.lacing-invalid`, rule 3); fixed-size lacing cannot show it, so all its frames are handed cut. With a content encoding, `IsCut` is set on the decoded packet when the encoded frame was cut and decoding still succeeds; where it fails, `matroska.frame-encoding-invalid` stands.
- **Ogg.** See [question 2](#open-questions). Without it nothing changes except an unfinished packet at the end of a stream (`ogg.packet-unfinished`, `eos-inside-packet.spx`; `PacketBuilder` emits a packet only when a run ends, so a pending packet is never emitted today), which is handed with `IsCut`.
- **ASF.** `MediaObjects` drops an incomplete object today (`asf.object-incomplete`) unless it is the WMA Lossless object the file ends inside. It hands every incomplete object (the file ends inside it, the Data Object's stated end or the next object comes first) with `IsCut` and `FollowsGap` where fragments were lost. An object that does not continue (`asf.fragment-order`) stays dropped.
- **WAV's IEC 61937 route** (`Iec61937Route.cs`) also uses `ParsePackets`; its payloads (AC-3, E-AC-3, DTS, ADTS) state their own lengths, so it needs nothing.

**New fixtures:** a cut Vorbis and Opus packet in Ogg, a cut ALAC sample in MP4, a cut standard WMA and WMA Lossless object, a damaged standard WMA and WMA Lossless stream. Committed already: `mp4/truncated.m4a` (raw AAC) and `matroska/live-truncated-unknown-element.mkv` (a SimpleBlock stating 60 bytes more than the file holds).

## Changes per format

Every format gains `LostSamples`; goldens change only where it is above 0 or `samples` changes. Each entry's commit also changes its generator or model ([Rollout](#rollout)).

- **MPEG audio.** A cut frame counts 1,152 and is lost. Eight goldens, none in `layer2`/`layer3`:
  - `mpeg/truncated-last-frame.mp3` 5,760 → 6,912; `cut-frame-stream-change.mp3` first stream 5,760 → 6,912; `vbr/lame-truncated-last-frame.mp3` 8,064 → 9,216;
  - `external-blade080-stereo.mp3` and `blade082` (405 of 418 bytes), `external-shine-joint.mp3` (522 of 523), `external-shine-music.mp3` (415 of 418): 87,552 → 88,704;
  - `damaged-bitrate.mp3` (418 of the 627 bytes its damaged index declares): 8,064 → 9,216, which the fixture's description calls no loss ([question 4](#open-questions)).
- **ADTS, ADIF, AAC.** `truncated.aac` 12,288, lost 1,024; `damaged-length.aac` 30,720, lost 1,024; `sbr-in-truncated-frame.aac` 4,096 at 22,050 Hz, lost 2,048. A header destroyed by damage is a gap and counts nothing (`damaged-header.aac` 29,696). `blocks-missing.aac` stays 10,240 ([question 5](#open-questions)). Broken-syntax blocks by [question 8](#open-questions).
- **MP4.** `truncated.m4a` 9,216 → 10,240, lost 1,024. `sample-outside-mdat.m4a` stays 20,480, its 10 blocks with AAC syntax findings lost 10,240. `chunk-sample-mismatch.m4a` (`aac.element-past-end` 1, the PCE at 0x419, and `aac.prediction-not-allowed` on 3 blocks) lost 4,096 under question 8. `chunk-outside-file.m4a` (10 samples) and empty samples: [question 3](#open-questions).
  - **As built:** the cut sample is reported with the samples outside the file (`mp4.sample-outside-file` names the sample that starts in the file and is analyzed as far as it goes; no new finding); the AAC reading of a cut packet reports no syntax findings (`truncated.m4a` shows `mp4.truncated` and `mp4.sample-outside-file` only); `sample-outside-mdat.m4a` loses 10,240 and `chunk-sample-mismatch.m4a` 4,096 as listed; per question 3 (a) an access unit of 0 bytes counts nothing (no committed fixture has one).
  - The ADTS parser counts the frames past its record cap from their headers and does not see a cut there (a frame that does not fit before the end is left as unread bytes), so those frames are not lost samples; this is a limit in the sense of the rule 6.
- **Matroska.** The container flag; lost samples come from the codec parsers (Opus in the committed files).
- **Ogg Opus.** `opus-packets-corrupt.opus`: `samples` stays 24,648 and 5,448 (Coded Samples 24,960 and 5,760); lost expected 14,400 and 1,920 (24,960 − 10,560, 5,760 − 3,840 against libopus), confirmed by the packet records in the pull request.
- **Ogg Vorbis.** A packet cut by a truncated page whose mode reads is lost; no committed golden has one. The parser reads no floors or residues, so no other unit is lost. **Unverified:** with an overlapped transform a lost packet also changes the next packet's output, so status rule 1 may not hold for `oggdec` on a cut Vorbis fixture.
- **Ogg Speex.** `samples` is Coded Samples less delay and end trimming; Coded Samples counts every audio packet at frames per packet (2) × frame size (160), empty or damaged included. Six files hold damaged or cut packets: `frames-corrupt.spx` 3,160, `bad-crc.spx` 3,160, `eos-inside-packet.spx` 1,560, `huge-lacing.spx` 760, `truncated-page.spx` 6,257, `speex-wideband-layer-skip.spx` 920.
  - `frames-corrupt.spx` (10 packets of 2, 0, 2, 1, 2, 1, 0, 2, 2, 2 frames): the invalid (0 frames), overrun (1 frame) and empty packets count 320 and are lost, 960; the `speex.missing-frames` packet counts the frames present (Q5 (b)). Coded 3,200 → 3,040, `samples` 3,160 → 3,000, lost 960. Empty packets: [question 11](#open-questions).
  - The page-dropping files: [question 2](#open-questions).
- **FLAC.** A cut frame counts its block size, lost: `built-cut-mid-frame.flac` 12,288 → 16,384, lost 4,096 (`flac.total-samples-mismatch` reads the new count). Unreadable frames are lost, `samples` unchanged: `built-damaged-frame-after-frame.flac` 28,096, lost 4,096; `built-residual-bit-flipped.flac` 24,000, lost 4,096 (decoded 19,904); `built-subframe-faults.flac` 4,352 (17 frames of 256), eight unreadable frames lost 2,048 (decoded 2,304).
- **WavPack.** The set's blocks have 11,025, 6,487 and 6,488 samples. `built-truncated-last-block.wv` and `built-md5-file-cut.wv` stay 24,000, lost 6,488 (decoded 17,512). A cut header counts nothing (`built-truncated-header.wv` 17,512). The refused blocks are lost by their set's `block_samples`, once; the parser marks Truncated, Malformed and Refused blocks unreadable (`DecodeFeed.cs`), so the list comes from the block records: `built-block-size-too-small`, `-too-large`, `built-subblock-overrun`, `-decorr-terms-invalid`, `-flags-invalid`, `-channel-info-zero`, `-audio-without-bitstream`, `-block-trailing-byte`, `-unknown-subblocks`, five `built-dsd-*`.
- **Monkey's Audio.** Three frames of 73,728, 73,728 and 12,544. Under [question 3](#open-questions) (a), the frames whose first byte is in the file count:
  - `built-cut-mid-frame.ape`, `built-cut-after-first-frame.ape`: 160,000 → 147,456, lost 73,728 (decoded 73,728);
  - `built-cut-in-seek-table.ape`: → 73,728, lost 73,728 (decoded 0);
  - `built-frame-too-short-for-crc.ape`, `-special-codes.ape`: 160,000, frame 2 refused, lost 12,544 (decoded 147,456);
  - `built-old-3860-last-frame-cut.ape` 20,000, lost 1,568; `built-old-3910-last-frame-cut.ape` 3,000, lost 3,000;
  - `built-frame-data-bytes-too-large.ape`, `built-seek-first-entry-off-by-two.ape`: frame 2 is flagged cut but decodes in full (160,000 decoded): [question 4](#open-questions);
  - `built-frame-data-bytes-too-small.ape`: the file is whole; only the descriptor's frame-data size is small. By the file's bytes frame 1 is not cut (it decodes in full) and frame 2 is refused (`monkeys-audio.frame-too-short`, as in the two files above): 160,000, lost 12,544, decoded 147,456, so the invariant holds with no exception.
  - `built-header-data-too-large.ape` (every frame `frame-misplaced`, 0 decoded), `built-cut-in-terminating-data.ape`, `built-terminating-bytes-too-large.ape`: unchanged, no frame cut.
- **TTA.** A cut frame counts its frame length (the last its remainder) and is lost. This revisits the lead's `keep`. `built-cut-mid-frame.tta` 46,080 → 92,160, lost 46,080; `built-header-samples-too-many.tta` 92,160 → 138,240, lost 46,080; `built-seek-entry-too-large.tta` 92,160 → 100,000, lost 7,840; `built-seek-table-past-end.tta` 0 → 46,080, lost 46,080. Their `tta -d` and ffmpeg references are none. Frames wholly absent follow [question 3](#open-questions).
- **ALAC** (4,096 per frame). A frame whose first byte is present counts and is lost where unreadable: `built-element-invalid-shift.m4a` 5,904 → 10,000; `built-frame-overrun-no-audio.m4a` 5,904 → 10,000; `built-residual-run-too-long.m4a` 6,004 → 10,100, each lost 4,096; `built-frame-overrun-header.m4a` and `-residuals.m4a` stay 10,000, lost 4,096. `built-end-missing.m4a` reads in full: unchanged.
  - **As built:** `built-residual-run-too-long.m4a` stays 6,004 and loses 100: the unreadable frame states a partial element of 100 samples, which are its declared samples (the listed 10,100 assumed 4,096). `built-element-unknown-pce.m4a`, `built-element-unknown-cce.m4a` and `built-element-reserved-bits.m4a` (not listed) are unreadable frames too: 5,904 → 10,000, lost 4,096. `built-tuning-kb-zero.m4a` (a cookie whose kb of 0 makes the walk refuse every frame, `alac.tuning-invalid`) is a configuration refused as a whole, which the definition does not call lost: it stays 10,000 with no lost samples; frames refused for their own reasons are lost.
- **AC-3 and E-AC-3.** Lost 1,536 per cut frame: `cut-frame.ac3`, `truncated.ac3`, `sample-short.mp4` (9,216 for six frames), `truncated-in-skip.eac3` (6,144 for four), `wav/built-iec61937-cut-in-payload.wav` and `-cut-odd-payload.wav` (7,680 for five). `crc-damaged.ac3` (12,288) stays counted, not lost. `bad-blocks.ac3` (`ac3.block-invalid` 3 times, 36 blocks, 18 read): a frame with an invalid block is lost whole, as every AC-3 entry here (the decoder cannot decode the rest of it); 3 frames, lost 4,608 (18 unread blocks of 256 equal it).
- **DTS** (512 per frame). `cut-frame.dts`, `truncated.dts` 3,072, lost 512; `fsize-too-large.dts` 3,072, lost 512; `dtshd-stream-size-wrong.dtshd`, `dtshd-stream-truncated.dtshd`, `exss-overrun.dts` (2,048) are cut in the last frame, whether the core is whole is **unverified** ([question 4](#open-questions)).
- **TrueHD and MLP.** A cut unit and the units after damage count (40 per unit at 48 kHz) and are lost; the damaged unit counts too.
  - `built-unit-cut.thd` 3,160 → 3,200, lost 40; `built-sync-crc-mismatch.thd` 2,560 → 3,200, lost 640; `built-mlp-sync-crc-mismatch.mlp` 1,920 → 2,560, lost 640;
  - `built-parity-mismatch.thd` 2,720 → 3,200, lost 480; `built-directory-end-past-unit.thd` 2,880 → 3,200, lost 320; `built-block-size-invalid.thd`, `built-restart-sync-invalid.thd`, `built-substream-without-restart-header.thd` 1,280 → 1,920, lost 640;
  - unchanged and not lost: `built-restart-crc-mismatch`, `built-substream-crc-mismatch`, `built-substream-parity-mismatch`, `built-mlp-substream-crc-mismatch`;
  - `truehd.unsupported` (`built-mlp-group-rates.mlp`) and `truehd.data-skipped` (`built-data-skipped.thd`): [question 12](#open-questions); `external-truehd-stereo-48k-24-odd-length.thd` (181 units, 7,213 samples, a trim of 27) gains a Coded Samples node.
- **Standard WMA.** The frames' count stays. Invalid frames and the frames their superframe declares after them count F and are lost (`writer-stamp-last.asf`, `writer-stamp-not-last.asf`, `fragments-in-two-streams.asf`: `samples` 0 today).
- **WMA Professional.** On a damaged stream invalid frames count F and are lost. The five unfinished-frame streams: [question 9](#open-questions).
- **WMA Lossless.** Invalid frames count their frame length and are lost; a media object cut by the file counts and is lost.
- **WMA Voice.** A superframe cut at the end (`wma.voice-unfinished`, `voice-truncated.asf` 10,760) counts 480 or its stated count and is lost; invalid superframes (`voice-invalid-superframes.asf` 10,080, `voice-object-not-whole.asf` 7,680), a superframe a decoder drops (`voice-spillover.asf`, `wma.voice-superframe-overrun`) and short objects (`voice-object-short.asf`, `-skip-kept.asf`, `-superframe-in-lead.asf`) are lost by the superframe's count.
- **WAV, Wave64, AIFF.** Whole sample frames of the bytes present; a partial frame counts nothing. No change.
- **Musepack.** A truncated frame counts 1,152 in Coded Samples and is lost; `samples` composes as before (the cap 66,150 does not apply).
  - `sv7-truncated.mpc` Coded Samples 46,080 → 47,232, `samples` 45,599 → 46,751; `sv7-damaged-length.mpc` 4,608 → 5,760, 4,127 → 5,279; `sv7-data-after-tag.mpc` 43,776 → 44,928, 43,295 → 44,447;
  - `sv8-truncated.mpc` (`musepack.packet-truncated`: the cut packet of 4 frames is left out) 41,472 → 46,080, 40,991 → 45,599, lost 4,608;
  - `sv5-band-overrun.mpc`, `sv6-band-overrun.mpc`: one damaged frame, lost 1,152 (as built: `sv6-band-overrun.mpc` 32,256, lost 1,152; `sv5-band-overrun.mpc` also has the unwritten last frame, so its `samples` are 32,927 and lost 2,304); `sv7-resolution-invalid.mpc` (a frame with a resolution out of range, not listed above): `samples` stay at the cap 66,150, lost 1,152;
  - `musepack.last-frame-unwritten` (the SV4/SV5 encoders write whole words) is on four SV5 files, `sv5-band-overrun`, `external-mppsv5-vbr`, `external-mppsv5-left-right-vbr`, `sv5-seek-tag` (31,775 samples, Coded Samples 32,256); counting and losing 1,152 on encoder-written files is a case of [question 4](#open-questions). As built: those four files read `samples` 32,927 (31,775 + 1,152) and lost 1,152; an SV4 or SV5 last frame that is whole stays uncounted, as decoders do not decode it, while one that runs past the end of the data counts and is lost (question 4 (a)).

## Cross-check statuses

**Scope.** The rules decided on 2026-11-02 in [official-decoder-crosschecks.md](official-decoder-crosschecks.md) apply to AAC (step 4) only, as #213 states there. The rules below, approved with this note, apply to every format once the alignment lands; they agree with the AAC rules except where marked.

1. **A unit counted by its declared samples and flagged lost, which the reference refuses or outputs nothing for:** `explained`. The script checks that the reference's total equals the coded stage less `LostSamples` and that the refused units are the lost units, matched by offset and length. *Differs from the AAC rule,* which says "flagged" and not "flagged lost": the AAC rule gives `explained` for any flagged unit the reference refuses, a CRC-failing one included.
2. **The same unit, which the reference conceals:** totals equal, the unit's verdict (concealed against lost) `explained`. A reader that writes audio over a cut (`tta -d` writing a header's count) is this rule.
3. **A merged finding points to the first,** so later flagged units have no finding at their bytes: `noted`. The lost samples still count them.
4. **Official decoders that disagree:** `keep` unless a reading of the specification decides; the status stays the lead's.
5. **The reference outputs fewer than the coded stage less `LostSamples`, or refuses an unflagged unit:** `pending`.
6. **A reference that drops a unit whose checksum fails** (ffmpeg's `-err_detect crccheck` on AC-3, FDK refusing a block): `noted`. *Differs from the AAC rule,* which gives `explained`; this note proposes `noted` because such a unit is not lost ([question 1](#open-questions)). Libogg's page drop is not this rule ([question 2](#open-questions)).
7. **Stage:** the comparison is at the coded stage; for Opus and Vorbis the harness key is `decodedSamples`. A reader at the trimmed stage (`opusdec`, `oggdec`) is compared with `samples` less the lost samples inside the trimmed range; where that cannot be told, `noted`.

The reason strings ("counted by their declared positions") are written by `compare_official_decoders.py` into every `official-decoders.json`; the rollout rewords them to "declared samples".

**Recorded items at the heads above:**
- `opus-packets-corrupt.opus`: stays `explained`, now by rule 1, if the lost samples equal the difference.
- TTA cut last frame (derived variants): totals become equal (rule 2); the `keep` is superseded. Vorbis `keep` items, refused-header streams and standard WMA: unchanged.
- AAC: `truncated.aac` (12,288 against 11,264), `sbr-in-truncated-frame.aac` (4,096 against 2,048, lost 2,048) and `sample-outside-mdat.m4a` (20,480 against 10,240, lost 10,240; six of the ten refused units have no finding at their bytes) are `explained` there and stay so by rule 1. `truncated.m4a` (9,216 against FDK's concealed 10,240) becomes equal, `explained` by rule 2. `damaged-length.aac` (30,720 against 28,672) stays `keep` as the lead's status; only one frame is lost, so rule 1 does not apply. `corrupt-syntax.aac` and `chunk-sample-mismatch.m4a` are rule 2. `blocks-missing.aac` has no difference (10,240 = FDK, unit status `noted`); `crc-blocks*.aac`, `raw-blocks.aac`, `blocks-trailing-bytes.aac` are `keep` because FDK holds back the last frame's blocks; fea550f changes how the script decides that (by the last frame's blocks, with exact shortfall and cap rules) without changing the committed files, so these items and rule 1's exact-total check are rechecked on the next run.

## Rollout

One pull request, `fix/samples-declared-positions`.

- **Order:** `fix/wma-pro-trim` (PR #212) and `feat/wma-container-duration` first; `ci/official-decode-aac` first, unchanged. The Ogg and TTA cross-checks are on main. The AAC, Ogg and TTA drafts come from the official-decode workflow, so the pull request names a run ID on its head.
- **Commits:**
  1. Core: `StreamInfo.LostSamples`, JSON, text, CSV and GUI output, the description, `MappedBytes.IsCut`. No golden changes.
  2. Codec parsers that accept the flag; then per format the parser with tests, the generator or model, the regenerated manifests and the cross-check exceptions; then the goldens alone with a `golden-changes.json` per folder. The Coded Samples nodes of TrueHD and WMA belong to these commits.
  3. The official-decoder drafts with statuses by rule and reworded reason strings.
- **Churn:** about 100 `.report.json` goldens in 17 folders (a code scan finds 98: `ac3 adts alac asf dts flac monkeys-audio mp4 mpeg musepack ogg truehd tta vbr wav wavpack`, plus `matroska` and `mp4/chunk-sample-mismatch.m4a`), plus the Coded Samples nodes (every trimmed WMA Professional and Lossless stream, `truehd/external-truehd-stereo-48k-24-odd-length.thd`). `golden_additions.py` checks only `*.report.json`; the 771 `*.decode.json` change only where the TrueHD gap or a lost unit changes `decodedSamples` or the `decode.*` findings, and are reviewed through the decode tests. No `*.measure.json` changes.
- **Generators and models** (CI reruns them; the `*FixtureTests` compare manifests with the analyzer): MPEG `stream_stats.py`, `make_mpeg_fixtures.py`, `make_layer3_fixtures.py`, `make_vbr_fixtures.py`, `make_external_mpeg_fixtures.py --manifests-only`; AAC and MP4 `aac_block_fixtures.py`, `aac_model.py`, `make_mp4_fixtures.py`; and per format the `make_*_fixtures.py` with its `*_model.py` for FLAC, WavPack, Monkey's Audio (also `make_legacy_monkeys_audio_fixtures.py`, `monkeys_audio_old.py`), TTA, ALAC, AC-3, DTS (and `make_wav_fixtures.py`, `wav_model.py` for IEC 61937), TrueHD, Musepack, Ogg, Vorbis and Opus, Matroska, ASF and WMA Voice (`make_voice_fixtures.py`, `voice_model.py`).
- **Cross-check scripts** with sample exceptions: `crosscheck_mpeg`, `_ac3`, `_dts`, `_flac`, `_alac`, `_asf`, `_monkeys_audio`, `_mp4`, `_musepack`, `_ogg`, `_truehd`, `_tta`, `_wav`, `_wavpack`, `_matroska`; and `compare_official_decoders.py`.
- **Tests:** `SamplesDefinitionTests` (one committed fixture per case of the rule, pinned by value); the static invariant of [Invariant and test](#invariant-and-test); an every-golden check `0 ≤ LostSamples ≤` the coded stage, 0 with `Protection` or `codec-not-analyzed`; the GUI tests of Decision A; `DescriptionTests` for the new node; a test that `averageBitrate` differs only in goldens with lost samples.
- **Docs:** each `docs/formats/*.md` gets one format-specific line naming the units it counts (for example "AC-3 counts six audio blocks of 256 samples per frame from the header, a cut frame included; Lost Samples counts a cut frame's 1,536"); also [cli.md](../cli.md), [gui.md](../gui.md), [development.md](../development.md), [official-decoder-crosschecks.md](official-decoder-crosschecks.md), PLAN.md, and a topic article `samples-and-lost-samples` (about 250 words).
- **Checks:** Release build, tests of the changed formats, `check_docs.py`, formatting, `check_shards.py`, `golden_additions.py`, the cross-checks of the changed formats.

## Open questions

1. **Does a unit whose checksum fails but whose syntax reads count as lost?** (a) No; lost means the parser refuses to read it. (b) Yes. (c) Per format. *Recommendation:* (a), with status rule 6 `noted`, which departs from the AAC rule's `explained`; the lead confirms the departure.
2. **Damaged Ogg pages.** A page failing its CRC and a truncated page are dropped whole today; the units on them are in neither `samples` nor lost samples. The granule used below is the dropped page's own, against the previous page's, and on a CRC-failing page the CRC does not vouch for it. Only packets with bytes present count (question 3 (a)).
   - `bad-crc.spx`: page 3 (granule 3,160 against 1,560, 10 packets of 160, all present) is dropped; 3,160 today.
   - `truncated-page.spx`: page 4 (granule 9,457 against 6,257) holds 520 laced bytes, 100 present: one whole packet and 48 bytes of a second; 6,257 today.
   - `huge-lacing.spx`: the packet over pages 3 and 4 never completes (page 4 truncated, granule −1); 760 today.
   - `eos-inside-packet.spx`: a packet never completes at the stream's end; 1,560 today.

   (a) Keep dropping, as libogg and PLAN.md do: the units stay out of `samples` and of lost samples (missing samples), nothing changes, and this departs from the lead's "damaged positions are reported as lost". The oggz packet comparison stays.
   (b) Hand the packets with bytes present over, flagged, so the units enter `samples` and the cut ones are lost:
   - CRC-failing page: all 10 packets read, so coded 3,200 → 4,800, `samples` 3,160 → 4,760 (the last granule), lost 0 under question 1;
   - truncated page: two packets; coded 6,400 → 7,040, `samples` 6,257 → 6,897, lost 320 (the cut packet; the whole one counts and is not lost);
   - `huge-lacing.spx`: coded 800 → 960, `samples` 760 → 920, lost 160; `eos-inside-packet.spx`: coded 1,600 → 1,760, lost 160. Where the end trimming comes from the last granule position it may absorb the cut packet, so `samples` may stay 1,560 and 760: **unverified**, the pull request computes it;
   - the oggz comparison ("libogg never returns it, so oggz dump lacks its packets, as the analyzer's packets do") then excludes packets the analyzer hands from dropped pages.

   *Recommendation:* (b), which is the lead's decision applied; its cost is the oggz comparison and the packets' bytes from a page libogg refuses.
3. **Do units with no byte in the file count?** MP4 samples wholly outside the file or of 0 bytes, Monkey's Audio and TTA frames past a cut. (a) No; the findings and header nodes carry the claim. (b) Yes, as lost, by `stts` or the header. (c) MP4 only. *Recommendation:* (a).
4. **Is a cut unit lost when Encspect's own decoder decodes it in full, or the parser can show its coded audio complete?** Cut is decided by declared length against bytes present. It overstates in:
   - encoder-written MPEG files a byte or two short (`external-shine-joint.mp3` 522 of 523, `external-blade080-stereo.mp3` 405 of 418), whose missing bytes may be padding past the Layer III main data;
   - `built-frame-data-bytes-too-large.ape` and `built-seek-first-entry-off-by-two.ape`, flagged `monkeys-audio.frame-cut` on frame 2 from the descriptor's size or the seek table, which decode in full (160,000), so the invariant fails on them by design;
   - the four SV5 files with `musepack.last-frame-unwritten`, on every file from that encoder;
   - the DTS-HD last frames, if the core is whole (**unverified**);
   - three files where only the declaration is wrong (`mpeg/damaged-bitrate.mp3`, `adts/damaged-length.aac`, `dts/fsize-too-large.dts`), which a parser cannot tell from `cut-frame-stream-change.mp3`.

   (a) Cut means lost; the description says "cut" and claims nothing about the audio. (b) Refine per format where the parser reads far enough (the Layer III main data end, the AAC END element, FLAC subframes, an ALAC element) or where Encspect's decoder decodes the unit in full; this changes the four encoder-written MPEG files and the Monkey's Audio and Musepack cases above. Monkey's Audio can use its decoder as evidence now. *Recommendation:* (a) in this pull request, (b) later per format with its own goldens; the named files are exceptions of the invariant.
5. **A declaration the bytes contradict** (`aac.blocks-missing`; a Speex packet with `speex.missing-frames`). (a) Trust the declaration: `blocks-missing.aac` becomes 40,960 with 30,720 lost, 75% of a file whose frames are whole, against FDK's 10,240. (b) A declaration contradicted by complete bytes counts what the bytes hold and is not lost; a unit cut by the end counts its declared samples and is lost. (c) Never count a cut unit. *Recommendation:* (b).
6. **When do "Decoded Samples" (Opus, Vorbis) and "Coded Samples" go to one name?** CLAUDE.md decides the name; "Decoded Samples" also collides with `StreamVerification.DecodedSamples`. (a) In this pull request. (b) In its own pull request right after, with the `official-decoders.json` key. *Recommendation:* (b), so the golden changes stay readable.
7. **How do the stream's and the measured lost samples relate?** (a) One name, two stages, two JSON positions, descriptions stating the stage. (b) Split the measured figure into lost units and missing samples (a change of `measurements`). (c) A distinct name for the stream's figure. *Recommendation:* (a): PLAN.md already calls the measured positions "lost samples", the lead's decision names the parser's figure the same, and (b) or (c) would rename a released field or contradict the lead's wording. It is the one place the note accepts one name for two bases.
8. **Which AAC codes make a block lost?** The definition is "the parser refuses to read it". Lost (the syntax breaks; the codes of `corrupt-syntax.aac`, where FDK conceals six units): `aac.section-overrun`, `aac.element-past-end`, `aac.codebook-reserved`, `aac.prediction-not-allowed`, `aac.max-sfb-too-large`, `aac.escape-too-long`. Not lost (soft findings, the parser reads on): `aac.ics-reserved-bit`, `aac.scalefactor-out-of-range`, `aac.tns-order-too-high`, `aac.fill-data-invalid`, `aac.intensity-without-common-window`, `aac.crc-mismatch`, and the three units FDK conceals in `corrupt-soft.aac` (offsets 304, 534, 1,419): `aac.elements-mismatch` with `aac.ms-mask-reserved`, `aac.pulse-in-short-window`, `aac.gain-control-not-allowed`. FDK's concealment of those three is `noted`, not rule 1, since the owner's decoder's behavior is not the criterion (question 1 (c)). *Recommendation:* as listed.
9. **A WMA Professional stream that ends in an unfinished frame (`wma.frame-unfinished`), and loss in a rule stream.** (a) The frame counts F and is lost on the frames' count: 8,192 and 57,344 for `cut-open-3` and `cut-open-27`, further from Windows (7,104, 55,808). (b) It counts nothing. (c) Microsoft's rule applies to these streams, as its prediction equals Windows on all five (corrected 2026-11-02: the Windows records hold 18 such streams, 15 `cut-open` variants and 3 public files, and the prediction equals the Source Reader's figure on all 18; the five named here are the ones that tell the counts apart): `samples` = the rule's figure (7,104, 55,808, 9,029,504, 90,112, 208,896), Coded Samples (N + 1) × F, lost F for the started frame. Where a stream also has lost frames mid-stream, the rule's conditions fail (no loss, no invalid frame) and the fallback count applies, with each lost frame counted F and lost. *Recommendation:* (c): it matches the owner's decoder on every measured cut stream and keeps the lost frame visible.
10. **The cut signal on `MappedBytes`** (Core.Abstractions). (a) `IsCut`, set by the container. (b) The container reports a lost packet to the host with its stated size; the host would need each codec's samples per packet. (c) Cut packets of codecs that cannot see the cut stay uncounted. *Recommendation:* (a).
11. **Empty Speex packets.** (a) They count the header's samples and are lost (the parser's own finding says decoders treat them as lost). (b) They declare nothing (rule 3). *Recommendation:* (a).
12. **TrueHD `truehd.unsupported` units** (`built-mlp-group-rates.mlp`, 1,280, two channel groups at different rates) **and `truehd.data-skipped` units** (`built-data-skipped.thd`, 2,760: units 21 to 31 intact but skipped). (a) Unsupported units count and are lost (conflicts with the definition: they are not damaged, and the rate is not one rate). (b) Neither counts. (c) Unsupported units count as stated, not lost. *Recommendation:* (b).
13. **The JSON form of `lostSamples`.** Above 0 only, or always (a change entry in every folder). *Recommendation:* above 0 only.
14. **The frame count and bitrate basis.** Per format as today, with `averageBitrate` changing only where lost samples appear, or one common basis. *Recommendation:* per format as today.
