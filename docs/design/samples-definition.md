# Samples: the positions a stream's units declare, and lost samples

Status: draft, 2026-11-02, for the project lead; nothing in it is built. It designs the lead's decision of 2026-11-02 that "samples" means one thing across formats. Claims about the repository were checked at 465988f (`main`) and on the branches `fix/wma-pro-trim` (a5035f1), `feat/wma-container-duration` (60ee85c) and `ci/official-decode-aac` (f3e4611). A claim no code, golden or document confirms is marked **unverified**; a figure that only the rollout pull request can compute is marked **to compute**.

- **The decision:** a stream's `samples` are the positions its units declare, at the sample rate the analyzer determines. Positions of units that are damaged or cannot be decoded stay in `samples` and are reported apart, as a lost-samples figure.
- **Why:** the lead's gaps-not-concealment rule ([PLAN.md](../PLAN.md), "Later", lossy decoders; [official-decoder-crosschecks.md](official-decoder-crosschecks.md), decision of 2026-11-01 on concealment) already says that a frame a decoder cannot decode is a gap whose positions count as lost samples. Today each format counts such a unit its own way, so the cross-checks against the official decoders reach a different status for the same situation in each format (see [Today](#today)).
- **What this note settles:** the definition per format family and how trimming composes with it ([Definition](#definition)), the lost-samples figure ([Lost samples](#lost-samples)), what changes per format ([Changes per format](#changes-per-format)), how the cross-check statuses follow ([Cross-check statuses](#cross-check-statuses)), the rollout ([Rollout](#rollout)) and the questions for the lead ([Open questions](#open-questions)).
- **Out of scope:** which sample rate a stream has (SBR doubling, a DTS extension's rate, the rate iTunSMPB counts at); the definition takes the rate as the analyzer determines it. The pending AAC items that are rate questions (`sbr-in-frames-failing-crc.aac`, `sbr-signaled-absent.m4a`, `faac-2.1-he-51-adts.aac`) are not touched.

## Today

The examples the lead named, with the code that makes them:

- **TTA** leaves a frame cut by the end of the data out (`tta.frame-cut`; "a frame the data cuts short is not counted", `Explain.StatSamples`); the lead kept it on 2026-11-01 for the four cut-last-frame variants (`keep`).
- **MP4** leaves out a sample that does not lie wholly inside the file (`Mp4Parser.Tracks.cs`, `InFile` and `SampleRanges`): `truncated.m4a` counts 9,216, without the sample of which the file holds 1 byte.
- **Raw ADTS** counts a cut frame by its header: `truncated.aac` counts 12,288 with a 16-of-21-byte last frame.
- **Opus** counts packets that libopus refuses by their TOC bytes (`opus-packets-corrupt.opus`, `explained`).
- **Ogg** streams whose headers the decoder refuses still have their packets counted (`keep`).
- **WMA Professional** follows Microsoft's decoder on undamaged streams with the length prefix (`fix/wma-pro-trim`); standard WMA is to follow the same overlap-tail rule (the lead, 2026-11-02), where `feat/wma-container-duration` still records its coded count as `explained`.

The full inventory is in [Changes per format](#changes-per-format).

## Words

- **Unit:** what a format codes one at a time and states the length of in samples: a frame, a block, a packet, an access unit, a superframe; for PCM, a sample frame.
- **Declared positions** of a unit: the samples per channel that the format's decoding process assigns to the unit, read from the unit's own syntax (a frame header, a TOC byte, a mode number) or, where the unit has none, from the stream's fixed parameters (a TTA or Monkey's Audio frame length, an ALAC cookie's frame length).
- **Coded stage:** the sum of the declared positions of the counted units. **Trimmed stage:** the coded stage less the trims the stream states (below). Both terms are those of [official-decoder-crosschecks.md](official-decoder-crosschecks.md).
- **Lost samples:** the declared positions, at the coded stage, of counted units for which no audio can be reconstructed.
- **Missing positions:** positions that no unit in the file declares but that the stream's numbering or a header total shows (a FLAC sample number that jumps, a TTA header count above the frames present). They are neither samples nor lost samples here; see question 5.
- **Stated samples:** a count the stream or container states without units the analyzer reads (an encrypted track's timing, a WAV fact chunk, the ASF header's play duration). It is not declared positions.

## Definition

### The rule

1. **A unit counts** when the file holds the syntax that declares its positions: its header, TOC byte or mode bits for a unit that describes itself; its first byte, for a unit whose length in samples comes from the stream's parameters (TTA, Monkey's Audio, ALAC) or from an index (an MP4 sample table entry); all of its bytes for a PCM sample frame, which has no declaration but its bytes. A unit wholly outside the file is not counted (question 2).
2. **Its positions** are those it declares, whether or not it can be decoded: a cut, refused or syntactically broken unit counts in full.
3. **A unit that declares nothing counts nothing:** bytes skipped while searching for sync (`aac.sync-lost`, `flac.data-skipped`), a Vorbis packet that codes no block (empty, not audio, an undefined mode: the decoder skips it, Vorbis I 4.3.1), an Opus part whose frames would last more than 120 ms (`PartRecord.Samples` gives 0), a Matroska block whose frames are not read (`matroska.block-short`, `matroska.lacing-invalid`).
4. **A unit the analyzer cannot place** for lack of the stream's parameters counts nothing: TrueHD units before the first major sync (`truehd.units-before-major-sync`), whose sampling rate is not known yet.
5. **A stream whose units are not read** (encrypted, a coding without a parser) keeps its stated samples, and its lost samples are not known (null).
6. **Positions a limit skips** (records caps, `aac.streams-limited`) are outside the definition; where a parser counts units past a cap from their headers, as the ADTS parser does, they count and are not lost.

### Per format family

- **Frames of a fixed size per header:** MPEG audio (384, 1,152 or 576 per frame by layer and version), AC-3 and E-AC-3 (256 per audio block, the blocks from the header), DTS (the core's blocks of 32, at the rate the stream delivers), ADTS (1,024 per raw data block, at the output rate, the blocks from `number_of_raw_data_blocks_in_frame`; question 4), ADIF (1,024 per block read), Musepack (1,152 per frame), TrueHD and MLP (a fixed number per access unit at the rate of the last major sync: 640 per 16 units at 48 kHz in the Dolby fixture, [truehd-format.md](truehd-format.md)).
- **Packets with their own duration:** Opus (the frames of the TOC and the frame count byte times the frame duration, RFC 6716 3.1), Vorbis (each block returns a quarter of the previous block and a quarter of its own, the first nothing, Vorbis I 4.3.8), Speex (frames per packet times the frame size; **unverified** in detail, see [Changes per format](#changes-per-format)).
- **Units placed by a container's index:** MP4 (each sample of the sample table, counted by its codec parser's declaration; the `stts` durations are the container's statement), Matroska and WebM (each frame of a block, counted by its codec parser), ASF (the media objects' frames, counted by the WMA parsers; the header's play duration is the stated `Container Samples` of `feat/wma-container-duration`, not declared positions).
- **Lossless formats with checksums:** FLAC (the frame header's block size), WavPack (`block_samples` of the first block of a set), Monkey's Audio (the header's blocks per frame, the final frame's own count), TTA (the frame length from the rate, 256/245 of a second; the last frame the remainder of the header's count), TrueHD (above), ALAC (the cookie's frame length, or the count an element states), WMA Lossless (each frame's samples), PCM (one position per sample frame of `nBlockAlign` bytes). Where the analyzer decodes these, the decoder's positions are the `CodedFrame.FirstSample` the parser gives, so the decoder never changes `samples`.

### Trimming

- **`samples` is the trimmed stage where the codec's own syntax or its normative mapping states a trim,** as today, and the coded stage elsewhere. The trims applied today, kept:
  - Opus: the pre-skip of the identification header and the end trimming of the last granule position (RFC 7845 4, 4.4);
  - Vorbis and Speex: the start and end trimming of the granule positions;
  - Musepack: the synthesis filter's delay of 481 and the end of the last frame that is not audio (libmpcdec r495), capped by the header's count;
  - WMA Professional and Lossless: the frames' trims; WMA Professional on `fix/wma-pro-trim`: Microsoft's start and end cuts;
  - TrueHD: the samples an end of stream marker drops.
- **Container presentation and encoder metadata stay figures of their own** and are not applied, as today: MP4 edit lists ("Samples (Edit List)") and iTunSMPB ("Samples (iTunSMPB)"), the LAME and Xing delay and padding ("Gapless" "Sample Count"), Matroska's DiscardPadding ([matroska.md](../formats/matroska.md): read and shown, not applied). Opus in Matroska takes the pre-skip from the OpusHead, which the parser compares with CodecDelay.
- **A coded-stage figure stands next to every trimmed `samples`:** "Decoded Samples" (Opus, Vorbis), "Coded Samples" (Speex, Musepack), "Trimmed Samples" (the trims of WMA Professional and Lossless). The two names for the coded stage break "one concept, one name" (question 7).
- **Lost samples are counted at the coded stage,** inside or outside the trimmed range: a lost first Opus packet inside the pre-skip is lost too. Reason: the measurements read untrimmed positions ([spectral-analysis.md](spectral-analysis.md), "Lossy formats later"), and a coded-stage figure compares with them without a second trimming rule.

### The decoder-rule figures of WMA

- **Microsoft's rule for WMA Professional is declared positions.** On `fix/wma-pro-trim` it is computed from the frames' own syntax: the number of frames N, the frame size F, the first and last subframe sizes, and the first and last frames' trims (E = N × F + (F − L_last) / 2, less the start and end cuts; [wma-pro-format.md](wma-pro-format.md), "What Microsoft's decoder outputs" on that branch). The tail (F − L_last) / 2 is the second half of the last frame's overlap, which the owner's decoder outputs: positions the frames declare, as the overlap rule of Vorbis I 4.3.8 declares Vorbis's.
- **The overlap-tail rule for standard WMA is declared positions too,** once it is computed from the frames in the same way.
- **A count sized from the header is not.** Microsoft's output for the Windows-written standard WMA files equals the play duration less the preroll rounded up to 512 samples (256 at 22.05 kHz), a fit on those files only (`feat/wma-container-duration`). The analyzer does not take it as `samples`; it stays visible as `Container Samples`.
- **Damaged WMA streams:** each invalid frame counts F and is lost, and so are the frames the superframe's frame count declares after an invalid frame that ends its superframe (standard WMA, `StdText.StatInvalidFrames`). How Microsoft's tail and cuts apply to a damaged stream is not known until black-box runs on damage exist ([asf-wma.md](../formats/asf-wma.md), "Reference for sample counts"); until then the cuts of the undamaged rule are applied to the frames counted, **unverified** against Microsoft.

### Duration, frames and bitrate

- **`duration` is `samples` divided by the rate,** so it includes lost positions.
- **Frame counts and the average bitrate keep their basis:** frame statistics count the units read whole, and the average bitrate is the bits of whole units over their positions, so a cut last frame does not lower it. Reason: fewer golden changes, and a bitrate over bytes the file does not hold would be a guess. Each parser's "Samples" description says which units it counts.

## Lost samples

### Name and meaning

- **Name:** `LostSamples` on `StreamInfo`, `lostSamples` in the stream's JSON, "Lost Samples" as a statistics node and in the text report. The measurements already use `LostSamples` and `Gaps` for decoded audio ([spectral-analysis.md](spectral-analysis.md), `StreamMeasurements`): the positions a decoder's output skipped. It is the same concept, positions without audio, at the parser's stage: no new word is introduced, and no other name ("damaged samples", "missing samples") is used for it anywhere.
- **The parser's figure is a subset of `samples`;** the measurements' figure also covers missing positions between units (a FLAC frame number that jumps makes the next block's position jump). So for a complete measurement whose decoder refuses exactly the units the parser flags, measured `Samples` equals `StreamInfo.Samples` less `StreamInfo.LostSamples`, and measured `LostSamples` is at least the parser's (question 5 and question 6).
- **A figure, never a verdict:** a count in samples with its basis in the description ("the declared samples of units cut by the end of the data, units that break the syntax, and units that cannot be read until the stream's next sync point"). It raises no finding; the findings on the units say what happened to them. The text never says the file is broken or incomplete because of it.

### What counts

- **Lost:** counted units that are
  - cut by the end of the data, or by the next unit's sync (`mpeg.frame-cut`, `ac3.frame-damaged`, `aac.frame-damaged`, `dts.frame-damaged`);
  - syntactically broken so that a decoder cannot decode them: the parser's unreadable units (`CodedFrame.IsUnreadable`: `flac.subframe-invalid`, WavPack's refused blocks), Opus parts that break RFC 6716 3.4 (`opus.packet-r1` to `-r7`, `opus.self-delimiting`), AAC raw data blocks whose syntax breaks (`aac.section-overrun`, `aac.element-past-end`, `aac.codebook-reserved`; the list per code is **to compute**), WMA invalid frames;
  - unreadable because the stream's state was lost: TrueHD units after damage until the next major sync (`truehd.units-after-damage`), WMA frames after an invalid frame in the same superframe.
- **Not lost:** units whose CRC fails but whose syntax reads (question 1); units a decoder refuses for a reason the parser does not check (they appear only in the measurements, as `decode.frame-invalid` and a position jump); a stream whose headers the decoder refuses as a whole (that is `decode.not-possible` for a decoder, and the 2026-11-01 `keep` for the Ogg streams stands).
- **Null:** a stream whose units are not read (encrypted, not analyzed); 0 for a stream whose units all read.

### Where it lives and how it is computed

- **A `StreamInfo` field** (`long?`), since front ends read kinds and streams, never node names. A "Lost Samples" node in the stream's statistics, with its `Description`, is added only where the figure is above 0, so undamaged goldens do not change.
- **Without decoding,** from what each parser already records per unit: the cut flag (`Entry.IsTruncated` in MPEG audio, `Frame.Cut` and the truncated state in AC-3, `walk.IsCut` in FLAC), the unreadable status, the Opus `PartStatus`, the TrueHD units after damage, the WMA invalid frames. The figure is the sum of the declared positions of the flagged units, added in the same pass that adds them to `samples`, with no extra memory per unit.
- **In `CodedFrame`,** `IsDamaged` covers both a failed checksum and a cut today. The decoded formats need the cut told apart (a new `IsCut`, or the parser's lost figure alone), so that the host's PCM accounting and the parser's figure agree; **to decide in the pull request**, with no change to decoding behavior.
- **Output:** JSON `lostSamples` after `samples` in the stream, written where not null (a field is added and none changes meaning, so `schemaVersion` stays 1); the text report's stream line; the GUI's stream summary; a CSV column `Lost Samples` appended at the end, as the measured columns were ([cli.md](../cli.md)).

## Changes per format

"Declared" marks a format that already counts by declared positions. Every format gains `LostSamples`; goldens change only where it is above 0 or where `samples` changes. The changed goldens per folder are listed by the pull request with a `golden-changes.json`; the figures below are the ones the committed goldens let me state.

- **MPEG audio.** Today a cut frame is left out (`IsAudio` excludes `IsTruncated`, `MpegAudioParser.Statistics.cs`); CRC failures count. **Changes:** a cut frame counts and is lost.
  - `mpeg/truncated-last-frame.mp3`: 5,760 → 6,912, lost 1,152;
  - `mpeg/cut-frame-stream-change.mp3`: first stream 5,760 → 6,912, lost 1,152;
  - other cut frames in `layer2`, `layer3`, `vbr`: **to compute** by a scan of `mpeg.frame-cut` and `mpeg.frame-truncated` in the goldens.
- **ADTS, ADIF, AAC.** Declared for a cut frame (`adts/truncated.aac` 12,288; `damaged-length.aac` 30,720; `sbr-in-truncated-frame.aac` counts both blocks of its cut frame); a header destroyed by damage is a gap and counts nothing (`damaged-header.aac` 29,696). **Changes:** lost samples (1,024 for `truncated.aac` and `damaged-length.aac`, 2,048 for `sbr-in-truncated-frame.aac`, blocks with broken syntax **to compute**); a frame holding fewer blocks than its header declares (`aac.blocks-missing`, counted by the blocks it holds) counts the header's blocks under question 4 (a): `blocks-missing.aac` 10,240 → 40,960, lost 30,720.
- **MP4.** Today a sample not wholly in the file, a sample of 0 bytes and the samples past `mp4.samples-overlap` are not analyzed; samples outside every mdat are read and counted. **Changes:** a sample whose first byte is in the file is handed to the codec parser cut, as a cut ADTS frame is, and counts with its declared positions, lost: `truncated.m4a` 9,216 → 10,240, lost 1,024. Samples wholly outside the file (`chunk-outside-file.m4a`, 10 samples) and empty samples follow question 2; samples outside every mdat question 3.
- **Matroska and WebM.** Declared: a block cut by the end of the file is read up to the end (`ChildEnd`) and its frames go to the codec parser, which counts them by their own declaration. Blocks whose frames are not read count nothing (rule 3). **Changes:** none of its own; lost samples come from the codec parsers.
- **Ogg Opus.** Declared (TOC). **Changes:** the refused parts are lost. `opus-packets-corrupt.opus`: `samples` unchanged; lost **to compute**, expected to be 14,400 and 1,920 for the two streams if libopus refuses exactly the flagged packets (24,960 − 10,560 and 5,760 − 3,840, [development.md](../development.md)), **unverified** until computed.
- **Ogg Vorbis.** Declared (modes and block sizes). **Changes:** a packet cut by a truncated page whose mode reads is lost; no golden with one is known (**to compute**). The parser does not read floors and residues, so no other unit is lost.
- **Ogg Speex.** `samples` comes from the granule positions, "Coded Samples" from the frames (`frames-corrupt.spx` holds `speex.frame-invalid`, `speex.frame-overrun` and `speex.missing-frames`). Which of those frames "Coded Samples" counts was not checked: **unverified**; the pull request applies the rule to them.
- **Ogg FLAC:** as FLAC. **The Ogg layer:** packets in a page whose CRC fails count (`bad-crc.spx` 3,160, as `missing-eos.spx`), so question 1 decides whether they are lost; a sequence gap is missing positions.
- **FLAC.** Today a cut frame is left out (`State.cs`, `isCut`); frames failing the CRC-8 or CRC-16 and unreadable frames (`flac.subframe-invalid`) count. **Changes:** a cut frame whose header reads counts its block size and is lost; unreadable frames are lost.
  - `flac/built-cut-mid-frame.flac`: 12,288 → 16,384, lost 4,096 (the cut fourth frame's header states 4,096); `flac.total-samples-mismatch` reads the new count;
  - `built-damaged-frame-after-frame.flac`: `samples` unchanged, the unreadable frame lost.
- **WavPack.** Declared: a block cut by the end counts its `block_samples` (`built-truncated-last-block.wv` 24,000); a cut header counts nothing (`built-truncated-header.wv`). **Changes:** truncated and refused blocks are lost (`built-truncated-last-block.wv`, `built-md5-file-cut.wv`; values **to compute**).
- **Monkey's Audio.** Today `samples` is the header's total even where frames are cut or absent (`built-cut-after-first-frame.ape` and `built-cut-mid-frame.ape`: 160,000). **Changes:** under question 2 (a), the frames whose first byte is in the file, the cut one lost; the header's total stays as its node ("the samples in a frame", the number of frames), with `monkeys-audio.data-cut`. Values **to compute**.
- **TTA.** Today a cut frame is left out; frames failing their CRC-32 count. **Changes:** a cut frame counts its frame length (the last frame its remainder) and is lost. This revisits the lead's `keep` of 2026-11-01 for the cut last frame.
  - `tta/built-cut-mid-frame.tta`: 46,080 → 92,160, lost 46,080 (the second of three frames of a 100,000-sample file at 44.1 kHz, 46,080 each but the last);
  - frames wholly absent (`built-cut-after-frame.tta`, `built-cut-after-table.tta`) follow question 2.
- **ALAC.** Today "a frame that was not read, or whose first element is cut off, adds none" (`Explain.StatSamples`). **Changes:** a frame whose first byte is present counts its declared length and is lost where cut or broken. Which goldens change (`built-end-missing`, the cut fixtures in `alac`): **to compute**.
- **AC-3 and E-AC-3.** Declared: frames cut by the end, by the next sync, inside an MP4 sample or an IEC 61937 burst count (`cut-frame.ac3`, `truncated.ac3` and `sample-short.mp4` 9,216 for six frames; `truncated-in-skip.eac3` 6,144 for four; `built-iec61937-cut-in-payload.wav` 7,680 for five), and so do frames failing crc1 or crc2 (`crc-damaged.ac3` 12,288). **Changes:** lost 1,536 for each cut frame in those files.
- **DTS.** Declared: `cut-frame.dts` and `truncated.dts` count 3,072 for six frames. **Changes:** lost 512 each; `dtshd-stream-truncated.dtshd` **to compute**.
- **TrueHD and MLP.** Today a cut unit and the units after damage until the next major sync are left out. **Changes:** both count at the rate of the last major sync and are lost.
  - `truehd/built-unit-cut.thd`: 3,160 → 3,200, lost 40;
  - `built-sync-crc-mismatch.thd`: 2,560 → 3,200, lost 640 (the unit with the failing major sync and the 15 after it);
  - `built-mlp-sync-crc-mismatch.mlp`: 1,920 → 1,920 plus the units after damage (**to compute**).
- **Standard WMA.** Today the valid frames less the first, times the frame length; invalid frames are dropped and end their superframe. **Changes:** the overlap-tail rule on undamaged streams (the lead's 2026-11-02 statement; its own pull request, see [Rollout](#rollout)); invalid frames and the frames their superframe declares after them count and are lost.
- **WMA Professional.** On `fix/wma-pro-trim`: Microsoft's rule on undamaged streams with the length prefix, else the frames read without error less their trims. **Changes:** on damaged streams, invalid frames count F and are lost; the three public files without the length prefix and the cut cases listed there as `keep` are untouched.
- **WMA Lossless.** Today each valid frame's samples less its end trim; invalid frames are dropped. **Changes:** invalid frames count their frame length and are lost.
- **WMA Voice.** Today the superframes read, 480 each or the count a superframe states. **Changes:** a superframe cut at the end (`wma.voice-unfinished`, `asf/voice-truncated.asf` 10,760) counts 480 (or its stated count where the field is present) and is lost; **to compute**.
- **WAV, Wave64 and AIFF (PCM).** Declared: whole sample frames of the bytes present; a data chunk that runs past the file counts what it holds (`built-truncated-data.wav` 40). A partial sample frame has no declaration of its own (its bytes are the declaration) and counts nothing. **Changes:** none. Compressed codings with a fact chunk keep their stated samples; IEC 61937 payloads follow their codec.
- **Musepack.** Today "Coded Samples" is the complete frames times 1,152 and `samples` the coded stage less the delay, capped by the header; a truncated frame is left out (`Sv7Parser.cs`, `Flags.Truncated`), and frames after a damaged run are not read. **Changes:** a truncated frame counts in "Coded Samples" and is lost; `samples` composes as before, so the header's cap still applies (`sv7-truncated.mpc`, `sv7-damaged-length.mpc`, `sv8-truncated.mpc`: **to compute**). Frames after a damaged run stay uncounted (rule 1: they are not read).

## Cross-check statuses

The statuses of [official-decoder-crosschecks.md](official-decoder-crosschecks.md) ("Differences and decisions") follow from the definition by rule, so that steps 2 to 6 reach the same status for the same situation:

1. **A unit the analyzer counts by its declared positions and flags as lost, which the reference refuses or outputs nothing for:** `explained`. At the coded stage the check is arithmetic: the reference's total equals `samples` less `LostSamples`. The comparison script marks it, with no review per file.
2. **The same unit, which the reference conceals:** the totals are equal, and the unit's verdict (concealed against lost) is `explained`, as the 2026-11-01 rule on concealment says.
3. **A finding that merges several units points to the first,** so later flagged units have no finding at their bytes: `noted`, as decided for the Ogg step. The lost figure still counts them, since it comes from the unit records and not from the findings.
4. **The official decoders of a format disagree among themselves:** `keep`, unless a reading of the specification decides.
5. **The reference outputs fewer positions than `samples` less `LostSamples`, or refuses a unit the analyzer does not flag:** `pending`, as today. The definition does not explain a decoder's own resync or a check the parser lacks.
6. **A reader that writes a header's count over a cut** (`tta -d` on the four cut-last-frame variants) now equals the analyzer, whose count includes the cut frame.

What this does to the items recorded so far:

- `opus-packets-corrupt.opus` (`explained`): stays `explained`, now by rule 1, if the lost figure equals the difference (**to compute**).
- TTA cut last frame (4 variants, `keep`): no difference left (rule 6); the lead's `keep` is superseded.
- `vorbis-packet-errors.ogg`, `vorbis-vendor-variants.ogg` (`keep`) and the streams whose headers the decoder refuses (`keep`): unchanged; nothing in them is a lost unit.
- On `ci/official-decode-aac`: `truncated.aac` (`pending`, 12,288 against FDK's 11,264) becomes `explained` by rule 1; `truncated.m4a` (`explained`, 9,216 against FDK's concealed 10,240) becomes equal, with its unit `explained` by rule 2; `damaged-length.aac` (30,720 against 28,672) stays `pending` (FDK also outputs nothing for the unit after the damaged one, rule 5); `sample-outside-mdat.m4a` (20,480 against 10,240) is `explained` only if the units with AAC syntax findings are the 10 FDK refuses (**to compute**); `blocks-missing.aac` and the multi-block files depend on question 4 and stay `pending` until the new figures are compared.
- Standard WMA: with the overlap-tail rule the differences to Microsoft are computed again; where Microsoft's figure follows the header's duration and the frames give another, the difference is `noted` (Microsoft sizes its output from the header), with `Container Samples` beside it.

## Rollout

One pull request, `fix/samples-declared-positions`, after the three branches in flight have merged:

- **Order with the branches:**
  - `fix/wma-pro-trim` first: its rule is declared positions, and this pull request only adds lost samples on damaged Professional streams.
  - `feat/wma-container-duration`: its `Container Samples` is a stated figure and stays. Its text that standard WMA keeps its coded count as `explained` is replaced when standard WMA follows the overlap-tail rule, in a pull request of its own after `fix/wma-pro-trim` (the rule's terms are shared); this note fixes only that the rule is declared positions.
  - `ci/official-decode-aac` first, unchanged: it changes no analyzer figure. This pull request regenerates its drafts, and the statuses follow the rules above.
- **Commits** (two per format, as [development.md](../development.md) asks for goldens a script can state):
  1. Core: `StreamInfo.LostSamples`, its JSON, text, CSV and GUI output, the description, and the `CodedFrame` change; no golden changes, since every parser leaves it null until its own commit.
  2. Per format, in the order of [Changes per format](#changes-per-format): the parser with its tests, then the regenerated goldens alone with a `golden-changes.json` in each changed folder (`mpeg`, `layer2`, `layer3`, `vbr`, `adts`, `aac`, `mp4`, `matroska`, `ogg`, `flac`, `wavpack`, `monkeys-audio`, `tta`, `alac`, `ac3`, `wav`, `dts`, `truehd`, `asf`, `musepack`), whose note states the rule ("a cut frame now counts its declared samples, which are lost").
  3. The official-decoder drafts of `ogg`, `tta`, `aac`, `adts` and `mp4`, with statuses by rule and the superseded `keep` noted with the date.
- **Tests:**
  - `SamplesDefinitionTests`: per format, one committed fixture for each case of the rule (a cut unit counts and is lost; a CRC failure counts and is not lost under question 1 (a); a unit with no declaration counts nothing; missing positions are neither; a trim leaves `LostSamples` at the coded stage), pinned by value;
  - an invariant over every golden: `0 ≤ LostSamples ≤` the coded stage, null exactly where the stream's units are not read;
  - over every decoded fixture with `--measure`: measured `Samples` equals `Samples` less `LostSamples` where the decoder refuses nothing else;
  - `DescriptionTests` covers the new node; the cross-check tests read the new figures.
- **Docs:** each `docs/formats/*.md` gets one line under what is reported ("samples count the declared samples of every unit read, cut ones included; Lost Samples counts those of cut and unreadable units"); [cli.md](../cli.md) (JSON and CSV), [gui.md](../gui.md), [development.md](../development.md) (the cross-check statuses), [official-decoder-crosschecks.md](official-decoder-crosschecks.md) (the rules above), one line in [PLAN.md](../PLAN.md) next to the gaps rule.
- **Topic article:** declared positions, trimming and lost samples together are a concept every format brings and no article covers (`measurements`, `gapless-playback` and `edit-lists-and-priming` each touch a part). A new `samples-and-lost-samples` article of about 250 words, related to those three.
- **Checks before the pull request:** the Release build, the tests of every changed format with their golden, description and finding-coverage tests, `scripts/ci/check_docs.py`, formatting, `check_shards.py`, and the cross-checks of the changed formats.

## Decisions

1. **One definition, declared positions, for every format.** Reason: the gaps rule already counts an undecodable frame's positions as lost, and a figure that means the same thing in every format lets the cross-check statuses follow by rule. Alternative: count only decodable units, which makes `samples` depend on a decoder the analyzer does not have for most lossy formats.
2. **Lost samples as a subset of `samples`, at the coded stage.** Reason: the invariant with the measured samples, and trims stay one rule per format. Alternative: lost samples at the trimmed stage, which needs every trim rule twice.
3. **Trims kept where they are today, and made explicit.** Reason: the codec-defined trims are part of the decoding process, the container's and encoder's are presentation; changing either is a separate decision with its own goldens. Alternative: always report the coded stage as `samples`, which changes every Opus, Vorbis, Speex, Musepack and WMA golden.
4. **Frame counts and bitrates keep their basis.** Reason: they describe the bytes read, and the change stays in `samples`, `duration` and the new figure.
5. **The name `LostSamples`, shared with the measurements.** Reason: the same concept at two stages; a second name would break "one concept, one name".

## Open questions

1. **Does a unit whose CRC fails but whose syntax reads count as lost?**
   - (a) No: it counts, and the checksum tallies of `StreamVerification` and the findings report it. Encspect's FLAC decoder decodes such frames (`IsDamaged`), so the measurements show no loss there either.
   - (b) Yes: the AC-3 cross-check holds FFmpeg to refusing exactly the damaged frames, those failing a CRC among them, and the analyzer's own `ac3.crc1-mismatch` message says that decoders that check crc1 drop the frame.
   - (c) Per format, by what the owner's decoder does.
   - Recommendation: (a). Lost means no audio can be rebuilt; a failed CRC says the audio may differ, which the verification already says, and a decoder that drops such a frame is rule 1 of the statuses anyway.
2. **Do units with no byte in the file count?** MP4 samples wholly outside the file (`chunk-outside-file.m4a`) or of 0 bytes, samples past `mp4.samples-overlap`, Monkey's Audio and TTA frames past a cut.
   - (a) No: a unit with no byte in the file is not in the stream. The findings and the header nodes carry the claim (`mp4.sample-outside-file`, `tta.frames-missing`, `monkeys-audio.data-cut`). Monkey's Audio changes from the header's total to the frames present, as FLAC and WavPack count today.
   - (b) Yes, as lost: MP4 by the `stts` durations, Monkey's Audio and TTA by the header, so a cut file reports its original length.
   - (c) Only MP4, whose sample table lists each unit, while the totals of TTA and Monkey's Audio stay claims.
   - Recommendation: (a). It needs no rule for which totals to trust, and the claim stays visible next to the count.
3. **Are MP4 samples outside every mdat declared positions?**
   - (a) Yes, counted as today, lost only where their codec parser flags them.
   - (b) Lost as a whole, since their bytes are not media data.
   - Recommendation: (a). The table declares them and the codec parser reads what they hold; `sample-outside-mdat.m4a` already shows AAC syntax findings on such bytes.
4. **Is a cut unit's declaration trusted where its bytes cannot hold it?** A frame whose header declares more ADTS raw data blocks than it holds (`aac.blocks-missing`), a last unit whose declared length runs far past the end of the file.
   - (a) Yes, where the unit passes the format's own acceptance (the chain of consistent frames for MPEG audio, ADTS, AC-3 and DTS; a length within the format's maximum for its parameters): its positions count and the missing part is lost.
   - (b) Only the positions whose data starts in the file (for ADTS, the blocks present, as today).
   - (c) Never count a cut unit.
   - Recommendation: (a). It is the gaps rule applied to the declaration the format makes, and acceptance already guards against a false sync.
5. **Do missing positions between units count as lost?** Positions shown by FLAC sample numbers, WavPack block indexes or granule positions that jump.
   - (a) No: they are neither samples nor lost; the findings carry their size, and the measurements count them as their gaps.
   - (b) Yes: `LostSamples` adds them, so it equals the measurements' figure on every decoded stream.
   - Recommendation: (a). It keeps the parser's figure a subset of `samples` and measured `Samples` = `Samples` − `LostSamples`; (b) would make lost samples depend on which formats number their units.
6. **Do the measurements count lost units at the start and the end?** The measurer counts a gap only when a block's position jumps (`StreamMeasurer`), so a lost last frame is in no figure.
   - (a) The host gives the measurer the stream's declared end (and start), so leading and trailing lost units count in the measured `LostSamples`.
   - (b) Leave it and document it.
   - Recommendation: (a), in the same pull request; measurement goldens of decoded cut fixtures change (**to compute**).
7. **One name for the coded stage?** "Decoded Samples" (Opus, Vorbis) and "Coded Samples" (Speex, Musepack) name the same concept.
   - (a) "Coded Samples" everywhere, with the key in `official-decoders.json` renamed: the analyzer decodes neither format, so "decoded" misleads.
   - (b) Keep both.
   - Recommendation: (a), as its own pull request right after this one, so that this one's golden changes stay readable.
