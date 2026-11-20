# Damaged units in the measurements

Status: signed off by the lead, 2026-11-20 (every decision as recommended, the [`CLAUDE.md`](../../CLAUDE.md) rule and the order of pull requests). Milestone 0.4, the item "Damaged units withheld from the measurements" of the [plan](../plan.md#04-free-decoders-and-routes-complete). Claims about the code were checked at ac6f3df39 (main).

- **Why a document of its own:** the rule crosses three designs, the decoder contract ([decoders.md](decoders.md)), the withheld output of the lossy decoders ([lossy-decoders.md](lossy-decoders.md#withheld-output)) and the measurements ([spectral-analysis.md](spectral-analysis.md)), and it brings its own decisions and pull requests. [decoders.md](decoders.md) is over 1,800 lines; it gets a short section that links here, as [correction-files.md](correction-files.md) is linked from the format docs.

## The lead's decision (2026-11-20)

- A unit whose checksum fails is treated like a gap for the measurements.
- It is still decoded, so the stream's checksums (MD5 and the like) and the findings stay as they are.
- Its output is withheld from the measurements and counted as withheld, and the next unit follows a gap.
- Today's convention is the opposite: `CodedFrame.IsDamaged` (`src/Encspect.Core.Abstractions/DecodeSetup.cs`) says "the decoder still decodes what it can, so a whole-stream checksum then says the audio differs", and nothing keeps that output out of the measurements.
- The question came from WavPack: libwavpack mutes a block whose CRC fails, and Encspect keeps its audio ([decoders.md](decoders.md#as-built-in-step-3-wavpack), "Deviations").

## Scope

- **In:** every decoder in `src/Encspect.Formats.*/Decoding`, the host's decode session and measurer, the contract types they share, and the container checksums over packets a decoder receives (Ogg, Matroska).
- **Out:** the verification (`StreamVerification`, the tallies, `IsVerified`), the findings, the tree, and the PCM a front end's `Output` sink receives (decision 4). Decoders that do not exist yet (TAK, WMA, AAC, the WavPack pair with its correction applied) adopt the rule in their own pull requests.

## Survey

### Per-unit checks and what happens today

"Coded data" and "decoded audio" are the coverage terms of [decoders.md](decoders.md#what-each-format-stores).

| Format | Per-unit checks (coverage) | On a failure today | Measured today |
| --- | --- | --- | --- |
| FLAC | frame header CRC-8 (header), frame CRC-16 (coded data), both compared by the parser | the frame is handed with `IsDamaged` (CRC-8 or CRC-16 fails, cut, unreadable, extent found), decoded and written; no decoder reads the flag | yes |
| TTA | frame CRC-32 (coded data), parser | handed with `IsDamaged` (`Feeding.cs`), decoded and written; every frame starts from the same state | yes |
| WavPack 4 and 5 | block checksum (coded data), parser; block header CRC and extended CRC (decoded audio), decoder | `IsDamaged` for a checksum that fails or is missing and an oversized block; decoded. A CRC failure raises `wavpack.audio-crc-mismatch` and the samples are kept (libwavpack zeros the block, `unpack_utils.c` 377-386). A block with fewer samples than its set's first block, a bitstream that ends early or the mute limit mutes the block: it is written as zeros (0x55 for DSD) and fails its CRC. A set is held in planes and written on its final block | yes; muted blocks count as digital silence |
| WavPack pair (`.wvc`) | correction block checksum (coded data), tallied | decoding still gives the lossy audio; applying the correction is a later pull request ([correction-files.md](correction-files.md#pull-requests-in-order)) | yes, as lossy |
| WavPack 1 to 3 | none per unit: version 3 stores one audio CRC of the whole stream (and `crc2`, of the lossless audio, not compared) | the decoder compares the CRC when every sample is decoded | yes |
| Monkey's Audio | frame CRC-32, or the 30-bit checksum before 3.93 without the CRC flag (decoded audio), decoder; the descriptor MD5 is of the whole file | the frame is written chunk by chunk before its CRC is known, except 24-bit frames, which are held for the interim-mode retry (`DecodeHeld` up to 2^23 samples over all channels, `DecodeTwice` above). A failure raises `monkeys-audio.frame-crc-mismatch` (or `-checksum-mismatch`) and keeps the samples. `IsDamaged` marks only cut frames | yes |
| TrueHD, MLP | major sync CRC-16, parity nibble, restart header CRC (header); substream parity byte and CRC-8 (coded data), parser; lossless check per restart interval (decoded audio), decoder | `IsDamaged` where a substream's parity or CRC-8 fails; decoded with the state continuing. The filter histories run on across restart headers ([truehd-format.md](truehd-format.md#values-a-restart-header-resets)). The lossless check is compared at the next restart header, over output already written | yes |
| ALAC, WAV and AIFF PCM | none | | yes |
| MPEG audio | CRC-16 over the header and the side information (Layer III) or the allocations and scfsi (Layers I and II), declared with `Header` coverage; the multichannel extension's CRC; parser | not passed on; `IsDamaged` marks only a Huffman overrun, which no checksum shows | yes |
| AC-3, E-AC-3 | CRC1 and CRC2, E-AC-3's one CRC, each dependent frame's (coded data), parser | `IsDamaged` where any fails (`DecodeFeed.cs`); decoded; the decoder ignores the flag | yes |
| DTS core | HCRC, never compared (TS 102 114 says it shall not be applied); the extensions' header CRCs cover no core audio | | yes |
| Musepack | the SV8 stream header's CRC-32 only | | yes |
| Vorbis, Opus, Speex | none of their own. The Ogg page CRC fails: the packets are handed as the page holds them ([ogg.md](../formats/ogg.md)), with no flag, since `MappedBytes` has none for it. Matroska's CRC-32 elements are verified by the parser, and the blocks carry no flag either | yes |
| TAK, OptimFROG, WMA Lossless, AAC | no decoder yet. TAK has frame data CRC-24s, OptimFROG block CRC-32s, AAC the ADTS CRC, WMA Lossless none | | no decoder |

### What the measurers do today

- The host forwards every block a decoder writes to the stream's measurer, which sees only positions.
- A jump between blocks is withheld where the decoder reported it (`DecodeContext.Withhold`) and lost for the rest (`StreamMeasurer.RecordJump`). Each jump is a gap: the levels, the loudness and the true peak resume after it (`ResumeAt`), and `Gaps` counts it.
- No host code reads `IsDamaged`. So a failed unit's output is measured like any other: WavPack's muted blocks enter the silence stretches and the levels, and a damaged FLAC frame's samples enter the peaks, the clipping runs and the spectrum.
- [spectral-analysis.md](spectral-analysis.md) says nothing about damaged units.

### Key facts

- **Known before decoding** (the parser compares the check): FLAC, TTA, WavPack's block checksum, TrueHD's substream checks, AC-3, MPEG.
- **Known only by the decoder:** WavPack's block CRCs, Monkey's Audio's frame CRCs, TrueHD's lossless checks.
  - WavPack already holds a set until its CRCs are known.
  - Monkey's Audio holds only 24-bit frames.
  - TrueHD's check covers an interval of units already written.
- **State across units:** FLAC, TTA, ALAC, WavPack 4 and 5 and Monkey's Audio carry no audio state from one unit to the next; TrueHD and every lossy decoder do.
- **No per-unit check at all:** WavPack 1 to 3, ALAC, PCM, DTS core, Musepack, and Vorbis, Opus and Speex outside a checked container.

## The rule

### Which failures count

- A unit is withheld when it adds to `Failed` of a per-unit tally that counts, as the tally's own rules decide. So a check the unit states and lacks is a failure where the tally counts it as one: WavPack's missing block checksum, a TTA frame shorter than its CRC-32.
- **Counting tallies (decision 1):**
  - every check over the unit's coded data or its decoded audio;
  - per-unit checks of the fields the unit is decoded with that no wider check covers: MPEG's CRC-16 and its multichannel CRC;
  - not a header check whose fields a wider check also covers, or that states only stream parameters: FLAC's CRC-8 (its CRC-16 covers the header too), TrueHD's major sync and restart header CRCs, TTA's header and seek table CRCs, TAK's object CRCs, Musepack's stream header CRC.
- **Container checksums (decision 2):** a packet with a byte on an Ogg page that fails its CRC, or inside a Matroska element whose CRC-32 fails, counts as failing. The container's tallies stay out of the codec's verification, as now.
- **Never per unit:** checks of the whole stream (FLAC's and WavPack's MD5, Monkey's Audio's descriptor MD5, WavPack 3's CRC, LAME's music CRC). They cannot say where the damage is.

### What is withheld, and the gap after it

- The positions of a failed unit, from `FirstSample` for `Samples`, are withheld from the measurements with the reason **checksum failed**.
- The output that depends on the unit's state is withheld with the same reason (decision 3).
- The decoder still decodes the unit and writes its output to the front end's sink (decision 4).
- The measurer leaves the part of each written block inside such a span out of every figure and counts it as withheld. Measuring resumes after the span as after a jump, so a run of failed units is one gap. That is the "next unit follows a gap" of the lead's decision, for the measurements.
- A failed first or last unit is leading or trailing withheld, as the decoder's spans are now.
- No finding is added: the checksum's own finding names the unit, and the measurement line says how many samples were left out (decision 5).

### State across units

- **Lossless decoders keep their state.** Resetting would change the checks after the unit and the whole-stream ones, which the decision keeps.
  - FLAC, TTA, ALAC, WavPack 4 and 5 and Monkey's Audio carry no audio state across units, so the span is the unit alone.
  - A WavPack set is withheld whole when any of its blocks fails, since the measurements withhold positions, not channels.
  - TrueHD: the substream's following units up to its next restart header are withheld too. The filter histories run on past it, so damage can outlast the span; the next lossless check then fails and is reported (decision 6).
- **Lossy decoders keep their state** (decision 3). After a failed unit they withhold from the measurements the span their after-gap rule withholds ([lossy-decoders.md](lossy-decoders.md#withheld-output)): 1,056 samples for Layer III, 480 for Layers I and II, 768 for MPEG-2 multichannel Layer II, 256 for AC-3 and E-AC-3, and each Ogg codec's own.
  - For an overlap and a polyphase history the span is exact, as after a gap: the output after it is what an undamaged stream decodes to.
  - For recursive state (Opus, Speex) the span is the same estimate as after a gap. Its PCM cross-check on a damaged fixture holds it.

### Units with no check

- A unit of a stream or format that stores no per-unit check (WavPack 1 to 3, ALAC, PCM, DTS core, Musepack, an MP3 frame without the protection bit, Vorbis outside a checked container) is measured as today. Nothing is withheld on suspicion.
- Damage that the parser finds without a checksum (an MPEG Huffman overrun, a FLAC frame cut after its audio) keeps today's rules: a unit the decoder cannot decode is lost, and one it decodes is measured. `IsDamaged` keeps that meaning.
- A check that is not compared because the data ends inside the unit counts as not checked, not as failed, as in the tallies: the cut-unit rules of [samples-definition.md](samples-definition.md) apply.
- Each format doc says, under what is not covered, which of its units no per-unit check protects.

### What stays unchanged

- `IFrameDecoder.Decode` returns the samples it wrote, which still include a failed unit's. So `DecodedSamples`, `DecodeEnd.IsComplete` and the comparison of the whole-stream checksums are unchanged.
- The rule never sets `CodedFrame.FollowsGap`: that flag would stop the MD5's comparison and make TrueHD reset.
- The tallies, `IsVerified`, every finding, the tree and the front end's PCM are unchanged. Only `StreamMeasurements` changes: fewer samples, more withheld, more gaps.

### Contract (Core.Abstractions)

- `CodedFrame.FailsChecksum`: the parser compared a counting check over the unit and it failed (or is missing where stated). It is set beside `IsDamaged`, whose comment is rewritten to "cut or syntax damage".
- `DecodeContext.WithholdFromMeasurements(long firstSample, long samples)`: the decoder reports positions whose output it still writes but the measurements leave out.
  - It is for a check over decoded audio, and for the dependent span after a failed unit.
  - Spans come in stream order, before the `Write` of their first position, as `Withhold`'s do.
- `WithholdReason.ChecksumFailed`, the fifth reason: text "checksum failed", JSON `checksumFailed`, with a field in `WithheldCounts`.
- `MappedBytes.FailsChecksum` for packets (decision 2): set by the container, read by the codec parser into `CodedFrame.FailsChecksum`.
- **Host:** the decode session passes each `FailsChecksum` unit's span to the measurer before it calls the decoder, and keeps spans that come before the output is declared, as it keeps `Withhold`'s. The measurer cuts the spans out of written blocks, as it cuts `TrimInner` ranges, but counts them as withheld and resumes after them. The list is bounded by `MaxPendingSpans` and pruned as blocks pass.

### Per format, as recommended

| Format | Withheld when | Flagged by | Work |
| --- | --- | --- | --- |
| FLAC | frame CRC-16 fails | parser | the flag |
| TTA | frame CRC-32 fails, or the frame is shorter than one | parser | the flag |
| WavPack 4 and 5 | block checksum fails or is missing; block header CRC or extended CRC fails, muted blocks included; the whole set | parser, decoder | the flag; the decoder marks the set before it writes it |
| Monkey's Audio | frame CRC-32 or 30-bit checksum fails | decoder | every frame held until its CRC is known (decision 6): `DecodeHeld` for all widths up to 2^23 samples, `DecodeTwice` above; the old decoder computes the CRC before it writes |
| TrueHD, MLP | a substream's parity or CRC-8 fails; then its units up to the next restart header | parser, decoder | the flag; the decoder marks the following units |
| MPEG audio | CRC-16 or the multichannel CRC fails; then the after-gap span | parser, decoder | the flag; the decoder marks the span |
| AC-3, E-AC-3 | CRC1, CRC2, the E-AC-3 CRC or a dependent frame's fails; then 256 samples | parser, decoder | `IsDamaged` there becomes `FailsChecksum`; the decoder marks the span |
| Codecs in Ogg or Matroska | a page or element CRC over the packet fails; then the codec's after-gap span | container, codec parser, decoder | the packet flag in Ogg, Matroska and the codec parsers of packets; each lossy decoder marks its span |
| WavPack 1 to 3, ALAC, PCM, DTS core, Musepack | never | | none |

## Effects

### Goldens

- `.decode.json`: none changes. Decoded samples, PCM hashes, tallies and findings are untouched; a pull request that changes one has broken the rule.
- `.measure.json`: no committed one belongs to a fixture with a failing per-unit check (checked over every `.measure.json` and its report), so none changes. The fixtures that show the rule get new ones (below).
- The report goldens (`.report.json`) do not hold measurements and do not change.

### Fixtures that show the change

- **Measurement goldens added for committed fixtures:**
  - FLAC: `built-crc16-fails`, `built-residual-bit-flipped` (as built, this one is lost, not withheld: see "As built, pull request 2");
  - TTA: `built-frame-crc-changed` and `built-frame-crc-changed-every-frame` (every frame withheld, nothing measured: decision 7), as built; `built-frame-crc-fails*` hold bytes in place of coded samples, so no frame decodes;
  - WavPack: `built-block-crc-changed`, `built-audio-bit-flipped`, `built-stereo-mute-limit` and `built-bitstream-cut` (muted blocks), `built-bad-checksum`, `built-checksum-missing`;
  - Monkey's Audio: `built-frame-crc-changed`, `built-24-bit-frame-crc-changed`, `built-old-3850-checksum-changed`;
  - TrueHD: `built-substream-crc-mismatch`, `built-audio-bit-flipped`;
  - AC-3: `crc-damaged`, `substreams-dependent-crc`;
  - MPEG audio: `mpeg1-layer3-crc-mismatch`, `layer2/crc-mismatch`;
  - containers: `ogg/bad-crc.spx` and `matroska/crc-damaged.mka` (Opus).
- **One new measurement fixture** (`make_measurement_fixtures.py`): a FLAC tone with silence stretches and a full-scale run, with a residual bit flipped in one frame.
  - `measurement_model.py` withholds that frame. The golden then shows a stretch split at the gap and a peak the damaged frame no longer sets.
  - `flac -t` rejects the file, so the measurement cross-check leaves it out of that step with its reason, as it does for the gap files.

### Tests

- **Host:** `FailsChecksum` and `WithholdFromMeasurements` spans in the measurer: merged runs, one gap each, leading and trailing parts, a span before the output is declared, the span cap, trims around them.
- **Decode session:** a failed unit leaves `DecodedSamples`, `IsComplete` and the MD5 comparison as they are (a FLAC fixture with a failing CRC-16 and a correct MD5 is not possible, so a test decoder holds it).
- **Per format:** the withheld positions equal the positions of the units the tallies count as failed, read from the parser's records. One theory over the fixtures above.
- **Monkey's Audio:** `MemoryTests` for the held frame, and `DecoderWorkTests` for the copy and for `DecodeTwice` above the cap, which is charged as now.
- `MeasurementDeterminismTests`, the finding-coverage and the description tests: unchanged, since no figure, finding or node is new. `WithheldCounts` gains a field.

### Cross-checks

- The PCM cross-checks compare the front end's PCM (`tests/Encspect.PcmDump`), which decision 4 leaves unchanged, so they compare the same positions as now.
- `compare_pcm.py` needs no change. A lossy fixture whose damaged unit a reference conceals is already listed by position.
- The measurement cross-check (`crosscheck_measurement_fixtures.py`) runs FFmpeg's `astats` and `silencedetect` over the decoded file. For the new fixture it compares the model's figures, which leave out the frame, and leaves `astats` out with its reason.

### Docs and articles

- **Designs:**
  - [decoders.md](decoders.md): the section that links here, and the `IsDamaged` comment;
  - [lossy-decoders.md](lossy-decoders.md#withheld-output): the fifth reason;
  - [spectral-analysis.md](spectral-analysis.md): how the measurer treats the spans;
  - [samples-definition.md](samples-definition.md): withheld samples now also belong to units that are written.
- **Front ends:** [cli.md](../cli.md) for the JSON key and the text line ("1 gap, 4,608 samples withheld (checksum failed)"), and [gui.md](../gui.md) for the caption.
- **Format docs:** flac, tta, wavpack, monkeys-audio, truehd, mpeg-audio, ac3, ogg, matroska, opus, speex and vorbis. Each says what is withheld and what no check protects. The WavPack "Deviations" note says the measurements no longer read a failed block, which libwavpack mutes.
- **Articles:**
  - `withheld-samples`: the reason, and that it is the one reason whose audio is still written;
  - `measurements`;
  - `verification` and `crc`: a failed per-unit check also keeps the unit out of the measurements.

## Not covered

- **TrueHD's lossless check:** its failure is reported and the interval it covers stays measured (decision 6). Withholding it would need each interval held to the next restart header and a write at the end of the stream, which the contract does not allow.
- **Damage without a failed check** (above).
- **Writer bugs that fail every unit:** the whole stream is withheld. Monkey's Audio files of SDK 13.42 and 13.43 with seven channels fail every frame CRC (`external-normal-16-7ch-sdk-1342`), so they get no measurements, with the reason (decision 7).
- **Codecs whose decoders do not exist yet** (TAK, AAC, WMA, the WavPack pair with its correction applied): each applies the rule in its decoder's pull request. TAK's frame data CRC-24 and AAC's ADTS CRC are counting checks.

## Decisions for the lead

Decided by the lead, 2026-11-20: every recommendation below.

1. **Which checks count.**
   - (a) Coded data and decoded audio only.
   - (b) (a), plus the per-unit checks of the fields a unit is decoded with that no wider check covers: MPEG's CRC-16 and multichannel CRC.
   - (c) Every per-unit check, header CRCs included (FLAC's CRC-8, TrueHD's restart header CRC).
   - **Recommended: (b).** MPEG's CRC-16 is the only check an MP3 or MP2 frame has. It covers the side information, whose damage gives bursts that the peaks and the spectrum would read as audio. (c) withholds FLAC frames whose CRC-16 holds.
2. **Container checksums.**
   - (a) A packet with a byte on a failing Ogg page or in a failing Matroska CRC-32 element counts, through `MappedBytes.FailsChecksum`.
   - (b) Only the codec's own checks.
   - **Recommended: (a).** The page CRC is the only check Vorbis, Opus and Speex have. The container's tallies still stay out of the codec's verification.
3. **The decoder's state after a failed unit (lossy, and TrueHD).**
   - (a) It keeps its state and withholds from the measurements the output that depends on the unit: its after-gap span, and for TrueHD up to the next restart header.
   - (b) It resets as after a gap (`FollowsGap` on the next unit), which also withholds that output from the front end's PCM and changes the decode goldens of the damaged lossy fixtures.
   - **Recommended: (a).** It keeps the rule to the measurements, where the lead put it. For the overlaps and filterbanks of MPEG audio and AC-3 the spans are exact either way. For TrueHD a reset would change the lossless checks after it.
4. **The front end's PCM.**
   - (a) A failed unit's audio is still written to `DecodeOptions.Output`; only the measurements leave it out.
   - (b) It is withheld from the output too, like the decoders' own withheld spans.
   - **Recommended: (a).** `DecodedSamples`, every PCM hash and the PCM cross-checks stay as they are. (b) also needs `IsComplete` to count the withheld samples, or the MD5 is no longer compared.
5. **How it is reported.**
   - (a) In the existing withheld figure, with a fifth reason, "checksum failed", which counts the unit and the output that depends on it.
   - (b) A figure of its own ("Damaged Samples").
   - **Recommended: (a).** One figure for positions decoded but not measured, as decision 4 of [lossy-decoders.md](lossy-decoders.md#decisions) chose. The JSON writes a reason only where it is above 0, so no golden changes.
6. **Checks known only after the output is written.**
   - (a) Monkey's Audio holds every frame until its CRC is known, up to 2^23 samples, and decodes twice above that, with the machinery its 24-bit retry already has. TrueHD's lossless check withholds nothing.
   - (b) Also hold TrueHD's restart intervals, which needs a write at the end of the stream (a contract change).
   - (c) Neither: only checks known before decoding, and WavPack's, count.
   - **Recommended: (a).** Monkey's Audio has no other per-frame check, and a stereo frame of the SDK's largest default size (1,179,648 samples per channel at insane, [decoders.md](decoders.md#memory)) holds 9.4 MB, under the cap. TrueHD's substream CRC-8s already flag the damaged units.
7. **A stream in which every unit fails.**
   - (a) No exception: nothing is measured, and the withheld figure says why.
   - (b) Measure it anyway, assuming a writer bug.
   - **Recommended: (a).** Encspect cannot tell damage from a writer that computes the check wrongly. A writer bug that a fixture proves is handled where it belongs: the parser does not count the check, as DTS's HCRC is never compared.

## Pull requests, in order

1. **This design** (signed off, 2026-11-20), with its rule in [`CLAUDE.md`](../../CLAUDE.md)'s architecture rules, approved by the lead.
2. **The contract, the host and the measurer, with FLAC and TTA:**
   - `CodedFrame.FailsChecksum`, `DecodeContext.WithholdFromMeasurements`, `WithholdReason.ChecksumFailed`, the session and the measurer, and the writers;
   - the article, and the new measurement fixture;
   - FLAC's and TTA's parsers set the flag. They need no decoder change, so the host path is tested end to end on real fixtures from the first pull request.
3. **WavPack, Monkey's Audio and TrueHD:** the decoders' spans, the held Monkey's Audio frames, their measurement goldens and tests.
4. **MPEG audio and AC-3:** the parser flags and the decoders' dependent spans.
5. **Ogg and Matroska** (decision 2): `MappedBytes.FailsChecksum`, the containers, the codec parsers of packets and the Ogg codecs' spans.

- **Recommended:** this order, not all at once. Each group touches other decoders and reviewers, and each brings its own goldens. The contract comes with a user, so it is not reviewed in the abstract.
- Later decoders (TAK, item 13; the WavPack pair, item 3; AAC and WMA in 0.5) apply the rule in their own pull requests.

## As built, pull request 2

- **Two fixtures differ from the list above:**
  - `flac/built-residual-bit-flipped` fails its CRC-16 but its frame cannot be decoded, so its positions are lost, not withheld; its golden pins that, and `flac/built-crc16-fails` shows the rule.
  - The two TTA fixtures `built-frame-crc-fails` and `built-frame-crc-fails-every-frame` hold bytes in place of coded samples and decode to nothing. `built-frame-crc-changed` and `built-frame-crc-changed-every-frame` are derived from the encoder-written `external-8000-mono.tta` with stored CRC-32s changed (`make_tta_fixtures.py`), so their frames decode; they carry the TTA measurement goldens.
- **The new measurement fixture** `measurement/synthetic-levels-crc16-fails` (FLAC, one frame with a bit flipped, built by `make_measurement_fixtures.py`):
  - `measurement_model.py` leaves the frame out and the test holds the analyzer to its `withheld_samples`.
  - The measurement cross-check skips it for `flac -t`, `flac -d`, FFmpeg's decode, `astats`, `ebur128` and `silencedetect`, and the lossyWAV-figures cross-check skips it because PcmDump still writes the frame; each skip prints its reason.
- **Trims:** the part of a failed unit's span that the stream's syntax trims (before the start, from the end on, inside) is counted as trimmed, as for a decoder's withheld span; only the part inside the stream is withheld.
- **A block wholly inside a span** is left out before it is looked at, so a failed unit whose block does not fit the declared output does not end the measuring.
- **The GUI** shows "No audio was measured: 400 samples withheld (checksum failed)." for a stream in which every unit fails.
- **`DecodeContext.Withhold`** refuses `ChecksumFailed`; a decoder reports written positions with `WithholdFromMeasurements`.

## As built, pull request 3

- **WavPack 4 and 5:**
  - The parser flags a block whose checksum fails or is missing (`FailsChecksum`, beside `IsDamaged`, which keeps the oversized block too). The decoder keeps one flag for the set being gathered: set by the parser's flag, by a block CRC that fails (a muted block's included) and by an extra CRC that fails. At the set's final block it calls `WithholdFromMeasurements` for the whole set before it writes the planes, so a muted block's zeros are no longer digital silence.
  - The host reports the flagged block's own span first; the measurer clips the repeat, so the set is withheld once.
  - A set that is never written (an unreadable block, more samples than the set holds at once) is lost or not measured, not withheld.
  - With and without a correction file the main file's blocks behave the same: the correction is not applied, so only the main file's blocks are decoded. A damaged correction block withholds nothing (`WavPackDamagedUnitsTests`). The first block libwavpack opens the file with is refused when its checksum fails, so a failed first block is lost, not withheld.
  - WavPack 1 to 3 is unchanged.
- **Monkey's Audio:**
  - Every frame of every width is held until its CRC is known: `DecodeHeld` for the 24-bit retry (which reports before it releases the final attempt), the new `DecodeHeldOnce` for the other widths and for 24-bit audio in interim mode, `DecodeTwice` and the new `DecodeTwiceOnce` above `DefaultMaxHeldSamples`. `Release` converts floats, since 32-bit float files now go through it.
  - A frame whose coded data breaks the syntax writes the chunks decoded before the break, as it did, and fails with `decode.frame-invalid`; its CRC is unknown, so nothing is withheld.
  - `OldFrameDecoder` makes the samples twice for a file with a CRC (once to hash, once to write) and charges both; the 30-bit checksum is over the arrays and needs no first pass.
  - The decoded PCM of every fixture is unchanged (the decode goldens).
- **TrueHD and MLP:**
  - The parser flags a unit in which a substream's parity byte or CRC-8 fails (`UnitStatus.CheckFails`); a restart header CRC does not count.
  - The decoder keeps `_isDependent`, set by a flagged unit and cleared by a unit in which every substream it reads has a restart header, and by a reset. A unit in between is withheld for its output samples, before it is written; the unit that restarts every substream is not.
  - The substream checks of the fourth (Atmos) substream are not compared, so its units are never flagged.
  - A lossless check that fails withholds nothing (`built-audio-bit-flipped`).
- **Goldens added** (new `.measure.json`, nothing existing changed): WavPack `built-block-crc-changed`, `built-audio-bit-flipped`, `built-stereo-mute-limit`, `built-bitstream-cut`, `built-bad-checksum`, `built-checksum-missing`; Monkey's Audio `built-frame-crc-changed`, `built-24-bit-frame-crc-changed`, `built-old-3830-crc-changed`, `built-old-3850-checksum-changed`, `built-old-3860-crc-changed`; TrueHD `built-substream-crc-mismatch`, `built-substream-parity-mismatch`, `built-mlp-substream-crc-mismatch`, `built-audio-bit-flipped` (nothing withheld). No new fixture was needed.
- **Cross-checks:** none of the WavPack, Monkey's Audio and TrueHD cross-checks reads a measurement, and the PCM they compare is unchanged. The lossyWAV figures cross-check already leaves out a stream with withheld samples; its WavPack and Monkey's Audio files are undamaged.
- **Tests:** `WavPackDamagedUnitsTests` (the withheld set, muted blocks not silence, a pair with and without its correction file), `MonkeysAudioDamagedUnitsTests` (withheld frames, the cap with `MonkeysAudioDecoder(0)` giving the same PCM and measurements), `TrueHdDamagedUnitsTests` (the span to the next restart, a lossless check that withholds nothing), and the measure goldens and the unchanged-PCM test in each.
