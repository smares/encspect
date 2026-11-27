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
- **Out:** the verification (`StreamVerification`, the tallies, `IsVerified`), the findings, the tree, and the PCM a front end's `Output` sink receives (decision 4). Decoders that do not exist yet (WMA, the WavPack pair with its correction applied) adopt the rule in their own pull requests.

## Survey

### Per-unit checks and what happens today

"Coded data" and "decoded audio" are the coverage terms of [decoders.md](decoders.md#what-each-format-stores).

| Format | Per-unit checks (coverage) | On a failure today | Measured today |
| --- | --- | --- | --- |
| FLAC | frame header CRC-8 (header), frame CRC-16 (coded data), both compared by the parser | the frame is handed with `IsDamaged` (CRC-8 or CRC-16 fails, cut, unreadable, extent found), decoded and written; no decoder reads the flag | yes |
| TTA | frame CRC-32 (coded data), parser | handed with `IsDamaged` (`Feeding.cs`), decoded and written; every frame starts from the same state | yes |
| WavPack 4 and 5 | block checksum (coded data), parser; block header CRC and extended CRC (decoded audio), decoder | `IsDamaged` for a checksum that fails or is missing and an oversized block; decoded. A CRC failure raises `wavpack.audio-crc-mismatch` and the samples are kept (libwavpack zeros the block, `unpack_utils.c` 377-386). A block with fewer samples than its set's first block, a bitstream that ends early or the mute limit mutes the block: it is written as zeros (0x55 for DSD) and fails its CRC. A set is held in planes and written on its final block | yes; muted blocks count as digital silence |
| WavPack pair (`.wvc`) | correction block checksum (coded data), tallied | a block whose correction block matches and holds is decoded to the lossless audio; one whose correction block is missing, cut or fails its checksum is decoded as the lossy part, raises `wavpack.correction-block-missing`, counts as a failed block CRC and withholds its set ([correction-files.md](correction-files.md#pull-requests-in-order)) | yes |
| WavPack 1 to 3 | none per unit: version 3 stores one audio CRC of the whole stream (and `crc2`, of the lossless audio, not compared) | the decoder compares the CRC when every sample is decoded | yes |
| Monkey's Audio | frame CRC-32, or the 30-bit checksum before 3.93 without the CRC flag (decoded audio), decoder; the descriptor MD5 is of the whole file | the frame is written chunk by chunk before its CRC is known, except 24-bit frames, which are held for the interim-mode retry (`DecodeHeld` up to 2^23 samples over all channels, `DecodeTwice` above). A failure raises `monkeys-audio.frame-crc-mismatch` (or `-checksum-mismatch`) and keeps the samples. `IsDamaged` marks only cut frames | yes |
| TrueHD, MLP | major sync CRC-16, parity nibble, restart header CRC (header); substream parity byte and CRC-8 (coded data), parser; lossless check per restart interval (decoded audio), decoder | `IsDamaged` where a substream's parity or CRC-8 fails; decoded with the state continuing. The filter histories run on across restart headers ([truehd-format.md](truehd-format.md#values-a-restart-header-resets)). The lossless check is compared at the next restart header, over output already written | yes |
| ALAC, WAV and AIFF PCM | none | | yes |
| MPEG audio | CRC-16 over the header and the side information (Layer III) or the allocations and scfsi (Layers I and II), declared with `Header` coverage; the multichannel extension's CRC; parser | not passed on; `IsDamaged` marks only a Huffman overrun, which no checksum shows | yes |
| AC-3, E-AC-3 | CRC1 and CRC2, E-AC-3's one CRC, each dependent frame's (coded data), parser | `IsDamaged` where any fails (`DecodeFeed.cs`); decoded; the decoder ignores the flag | yes |
| DTS core | HCRC, never compared (TS 102 114 says it shall not be applied); the extensions' header CRCs cover no core audio | | yes (in Matroska a core frame in a failing Cluster or Segment CRC-32 is withheld, with the 480 samples after it: pull request 6) |
| Musepack | the SV8 stream header's CRC-32 only | | yes |
| Vorbis, Opus, Speex | none of their own. The Ogg page CRC fails: the packets are handed as the page holds them ([ogg.md](../formats/ogg.md)), with no flag, since `MappedBytes` has none for it. Matroska's CRC-32 elements are verified by the parser, and the blocks carry no flag either | yes |
| TAK | frame header CRC-24 and frame data CRC-24 (coded data), parser; the MD5 of the whole stream, decoder | handed with `IsDamaged` and `FailsChecksum` where either CRC-24 fails; decoded and written, every frame on its own | yes |
| AAC | the ADTS CRC-16 of a protected frame (coded data), parser | `FailsChecksum` on each block of a frame whose CRC fails (`DecodeFeed.Decode`); decoded and written, and the measurements withhold the block and the one after it, whose overlap holds the failed frame's state, as "checksum failed" (no gap is made) | yes |
| OptimFROG, WMA Lossless | no decoder yet. OptimFROG has block CRC-32s, WMA Lossless none | | no decoder |

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
| TAK | frame header CRC-24 or frame data CRC-24 fails | parser | the flag; every frame decodes on its own |
| WavPack 4 and 5 | block checksum fails or is missing; block header CRC or extended CRC fails, muted blocks included; the whole set | parser, decoder | the flag; the decoder marks the set before it writes it |
| Monkey's Audio | frame CRC-32 or 30-bit checksum fails | decoder | every frame held until its CRC is known (decision 6): `DecodeHeld` for all widths up to 2^23 samples, `DecodeTwice` above; the old decoder computes the CRC before it writes |
| TrueHD, MLP | a substream's parity or CRC-8 fails; then its units up to the next restart header | parser, decoder | the flag; the decoder marks the following units |
| MPEG audio | CRC-16 or the multichannel CRC fails; then the after-gap span | parser, decoder | the flag; the decoder marks the span |
| AC-3, E-AC-3 | CRC1, CRC2, the E-AC-3 CRC or a dependent frame's fails; then 256 samples | parser, decoder | `IsDamaged` there becomes `FailsChecksum`; the decoder marks the span |
| Codecs in Ogg or Matroska | a page or element CRC over the packet fails; then the codec's after-gap span | container, codec parser, decoder | the packet flag in Ogg, Matroska and the codec parsers of packets; each lossy decoder marks its span |
| WavPack 1 to 3, ALAC, PCM, DTS core, Musepack | never by their own checks (a codec in Ogg or Matroska follows the row above; DTS in Matroska: a failing Cluster or Segment CRC-32, then 480 samples) | | none of their own |

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
- **Codecs whose decoders do not exist yet** (WMA, the WavPack pair with its correction applied): each applies the rule in its decoder's pull request. AAC's decoder applies it: the ADTS CRC is a counting check, and a block of a frame that fails it is decoded, written and withheld with the block after it. TAK's decoder applies it (item 12): the parser flags a frame whose header CRC-24 or data CRC-24 fails (`Feeding.cs`), both counting checks, since the frame header holds the sample count of the last frame and no wider check covers it; every frame decodes on its own, so the span is the frame alone.

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
6. **The container checksums over MPEG audio, AC-3 and E-AC-3, DTS, and TrueHD and MLP packets:** the frames flagged from the packets they lie in, the DTS decoder's span.

- **Recommended:** this order, not all at once. Each group touches other decoders and reviewers, and each brings its own goldens. The contract comes with a user, so it is not reviewed in the abstract.
- Later decoders (the WavPack pair, item 3; WMA in 0.5) apply the rule in their own pull requests; TAK's (item 12) and AAC's have.

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
  - With and without a correction file the main file's own failures behave the same. Since the correction is applied ([correction-files.md](correction-files.md#pull-requests-in-order), item 5), a block of a pair whose correction block is missing, cut or fails its checksum is decoded as the lossy part and counts as a failed block, so its set is withheld (`WavPackDamagedUnitsTests`). The first block libwavpack opens the file with is refused when its checksum fails, so a failed first block is lost, not withheld.
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
- **Goldens added** (new `.measure.json`, nothing existing changed): WavPack `built-block-crc-changed`, `built-audio-bit-flipped`, `built-stereo-mute-limit`, `built-bitstream-cut`, `built-bad-checksum`, `built-checksum-missing`; Monkey's Audio `built-frame-crc-changed`, `built-24-bit-frame-crc-changed`, `built-old-3830-crc-changed`, `built-old-3850-checksum-changed`, `built-old-3860-crc-changed`; TrueHD `built-substream-crc-mismatch`, `built-substream-parity-mismatch`, `built-mlp-substream-crc-mismatch`, `built-audio-bit-flipped` (nothing withheld). One fixture was added, for a set of several blocks (below).
- **Cross-checks:** none of the WavPack, Monkey's Audio and TrueHD cross-checks reads a measurement, and the PCM they compare is unchanged. The lossyWAV figures cross-check already leaves out a stream with withheld samples; its WavPack and Monkey's Audio files are undamaged.
- **Tests:** `WavPackDamagedUnitsTests` (the withheld set, muted blocks not silence, a pair with and without its correction file), `MonkeysAudioDamagedUnitsTests` (withheld frames, the cap with `MonkeysAudioDecoder(0)` giving the same PCM and measurements), `TrueHdDamagedUnitsTests` (the span to the next restart, a lossless check that withholds nothing), and the measure goldens and the unchanged-PCM test in each.
- **After the review of pull request 3:**
  - **A set of several blocks:** `wavpack/damaged-units/set-block-checksum-changed.wv` is `external-lossless-3ch-unassigned.wv` (three blocks to a set) with the stored checksum of the second block of its first set changed (`make_wavpack_damaged_units_fixtures.py`, regenerated in CI). The test and the golden show the whole set withheld once (11,025 samples, 1 gap) and no other position, though the failed block is not the set's last.
  - **The work bound of Monkey's Audio is 16,384 units a byte** (`MonkeysAudioDecoder.PerInputByte`, 8,192 before): a frame above the cap on held samples is decoded twice at every width now, which doubles the most a legitimate file charges for a byte (9,490 units, 13,112 at 32 bits). Only frames above 2^23 samples over all channels are affected (8 or more channels at insane), so no stereo file `mac` writes pays for the second pass. A crafted file is charged for what it takes, so its bound in time doubles (9.8 s per MB). `DecoderChargeTests.AnInsaneFileDecodedTwiceAboveTheCapStaysUnderTheAllowanceForItsBytes` holds it. The silent 10,000,000-sample frames of `ComplexityTests` and of `MonkeysAudioReviewFixTests` cost two passes, so their budgets rose to 2^31 and 2^33.
  - **The tests the design asks for:** `MemoryTests.DecodingAFullFrameOfMonkeysAudioHoldsOneBufferOfItsSamples` (the held frame: one buffer of the frame's samples), and the `DecoderWorkTests` cases `monkeys-audio-held-16-bit-full-frames` (the copy into the held samples) and `monkeys-audio-above-cap-insane-16-bit` (the second pass, with the cap lowered to 0).
  - **A stop or a budget crossed inside a frame** delivers none of that frame's samples now, since a frame is held until its CRC is known; before, the chunks decoded so far were written. Only the measurements of a stopped run see the difference. A frame whose coded data breaks the syntax still writes the chunks before the break.
  - **TrueHD's span** ends at a unit in which every substream the decoder reads has a restart header, not at the failed substream's next restart header as the table above words it: the output of a higher substream depends on the lower ones. Every fixture restarts all substreams at a major sync, so no fixture tells the two apart.
  - **The WavPack pair** is tested on files changed in memory (the checksum of the second block of the main file, and of the first block of the correction file), not on a committed pair; the main file's rule is pinned by the committed goldens.


## As built, pull request 4

- **MPEG audio:**
  - The parser flags a protected frame whose stored CRC-16 differs from the one it computed (`Entry.FailsCrc`, the same test the frame CRC tally counts): Layer III in the frame loop, Layers I and II in the feed of the deferred Layer II and the Layer I path. A frame whose covered bytes are not all there is not compared, so not flagged. `IsDamaged` keeps its Huffman overrun meaning.
  - The decoder keeps its state after the frame and, after writing it, sets the span to withhold: 1,056 samples of Layer III, 480 of Layers I and II, 768 of a multichannel stream (the after-gap constants). The following frames report it with `WithholdFromMeasurements` as they are written, so a Layer I span (384-sample frames) runs on into the frame after the next. A gap, an unavailable frame or any reset clears it; a second failed frame sets it again.
  - The multichannel extension's CRC is not a second flag: a frame of a stream that carries the extension in full whose CRC fails is not decoded (unreadable), so it is lost and the output after it is withheld as after a gap, as before.
- **AC-3 and E-AC-3:**
  - `DecodeFeed` sets `FailsChecksum` where it set `IsDamaged` (crc1 or crc2 fails, E-AC-3's CRC, a dependent frame's CRC); `IsDamaged` is no longer set for AC-3. Crc2 covers the whole frame, so a frame whose crc1 fails fails both.
  - Both decoders (`FrameDecoder`, `Eac3FrameDecoder`) report the first block, 256 samples, of the unit after a failed one, unless a gap or a lost frame reset them (which withholds the same block already). The unit's own output is withheld by the host.
- **Beyond the span** (the design says nothing of these; the lead decided them in review: the forward reach of pre-noise is withheld, the rest is not covered):
  - A Layer III frame whose main data begin in a frame that failed its CRC is not withheld for that, only the after-gap span is. The CRC covers the failed frame's side information, not the main data a later frame reads from it.
  - **AC-3's dither generators** keep running over a damaged frame's blocks, so their sequence can differ from an undamaged decode's for the rest of the stream; the noise has the same statistics, as after a gap's reseed.
  - **E-AC-3's transient pre-noise processing** (Annex E 3.7), forward: `PreNoise` keeps the positions a failed frame leaves damaged (its samples and the block after it) and, where a later correction reads any of them, reports the samples it writes with `WithholdFromMeasurements` just before they leave, so the span reaches as far as the correction does (up to 1,536 samples read, 1,024 written before the transient). A correction made from such samples is a source for the next, and a transient that a failed frame itself states (up to 4,092 samples ahead, with a length that is damaged data too) makes its correction dependent whatever it reads.
  - **E-AC-3's pre-noise, backward** (not covered): a failed frame's own transient data can rewrite up to 1,024 samples of the frame before it that are still buffered. The contract takes spans in stream order and the host has reported the failed frame's span already, so a span starting earlier would be clipped; closing this needs the measurer to insert spans in order, which this pull request does not change. Those samples are measured as decoded. Two failed frames within 1,024 samples of each other meet the same limit: a dependent range of the first that ends before the second frame is clipped, since the host has reported the second's span first.
- **MPEG audio, a failed frame inside an after-gap span:** the dependent span and the after-gap span both count from the next frame's start, and the larger is withheld, not their sum.
- **Fixtures** (`make_mpeg_ac3_damaged_units_fixtures.py`, regenerated in CI): encoder-written files with the stored CRC of one frame changed, so the frame decodes: `mpeg-audio/damaged-units/layer1-crc-changed.mp1`, `external-twolame-crc-stereo-crc-changed.mp2`, `external-lame-crc-crc-changed.mp3`, and `ac3/damaged-units/external-ffmpeg-stereo-192k-crc1-crc2-changed.ac3` (one frame's crc1 and another's crc2). Their decoded PCM equals their sources' (a test).
- **Goldens added** (new `.measure.json`, nothing existing changed): `mpeg-audio/mpeg1-layer3-crc-mismatch`, `mpeg-audio/layer2/crc-mismatch`, the four files above, `ac3/crc-damaged` and `ac3/substreams-dependent-crc`. They show 1,152 + 1,056 samples (Layer III), 1,152 + 480 (Layer II), 384 + 480 (Layer I) and 1,536 + 256 per failed AC-3 frame, one gap each.
- **Tests:** `PreNoiseTests` (a correction that reads damaged samples reports what it writes, one that does not reports nothing, a chained correction and a transient of a damaged frame report theirs), `Ac3DamagedUnitsTests` on `eac3-signal-transient.eac3` with the CRC of one frame changed in memory (more than the frame and the block after it is withheld, exactly that for the file without transient data, and the PCM is unchanged), `MpegAudioDamagedUnitsTests` (a Layer I frame that fails right after a gap withholds 384 + 480), `MpegAudioDamagedUnitsTests` and `Ac3DamagedUnitsTests` (the withheld span, the decoded PCM and samples unchanged against the undamaged file, the goldens, nothing withheld where nothing fails).
- **Cross-checks:** none reads a measurement of these files, and the PCM cross-checks compare the unchanged decoded PCM.

## As built, pull request 5

- **The flag:** `MappedBytes.FailsChecksum` (init-only, beside `IsCut` and `FollowsGap`, copied by `AsCut` and the new `AsChecksumFailed`, not carried by `Slice`) says the container's own check fails over the packet. The codec parser passes it on as `CodedFrame.FailsChecksum`; it is no tally of the codec's, so the verification, the findings and the tree stay as they were.
- **Ogg:** a packet is marked when a byte of it lies on a page whose CRC fails (`PageStatus.CrcMismatch`), whether the packet ends on that page, continues past it or begins on it (a packet that spans a failing page and a good one is marked once, whole). A packet cut by a truncated page is marked by the same test; a page that is truncated has no CRC to compare, so it marks nothing. An empty packet has no byte and is not marked.
- **Matroska:** a block is marked when a CRC-32 element that was compared and does not match covers it.
  - A Cluster's CRC-32 covers the Cluster's elements after it, so every block of that Cluster is marked (`BlockRecord.FailsChecksum`, set when the Cluster ends and its CRC is compared).
  - The Segment's CRC-32 (the first child of the Segment, compared when the Segment's size is known and the file holds all of it) covers every Cluster, so every block of the Segment is marked; it is compared before the first Cluster is read.
  - A CRC-32 that is not compared (misplaced, not four bytes, cut by the end of the file or of its parent) marks nothing, as it raises no mismatch. A BlockGroup's own CRC-32 is not compared by the parser today, so it marks nothing (`matroska.crc-position` and `matroska.crc-size` stay as they are).
  - A frame that a ContentEncoding undoes (zlib, stripped bytes) keeps the mark (`MappedBytes.AsChecksumFailed`).
- **Codec parsers that pass it on** (one packet or frame, one unit): FLAC (`State.AddFrame` takes the flag next to the frame's own CRC-16 status, which is untouched), Vorbis, Opus, Speex, ALAC, TTA (beside the frame's CRC-32 compare) and WavPack in Matroska (the block built from a packet), and PCM in Matroska (`A_PCM/*`: the slices end where the failing packets begin and end, so exactly the failing packets' sample frames are withheld, and a sample frame that spans a good and a failing packet goes with the failing side; `APcmClusterThatFailsItsCrcWithholdsOnlyItsOwnSampleFrames`). Not covered here, and done in pull request 6: MPEG audio, AC-3 and E-AC-3, DTS, and TrueHD and MLP in a container.
- **The Ogg codecs' spans** (state kept after the failed packet, so the output that depends on it is reported with `WithholdFromMeasurements`; the packet's own output is withheld by the host):
  - **Vorbis:** the samples the next packet returns, all of them (`blockSize(previous) / 4 + blockSize / 4`: the overlap of the failed packet's right half with its left half). That is the span the decoder withholds after a gap, and it is exact. A packet after a gap, or one the decoder does not decode, clears it.
  - **Opus:** 3,840 samples (80 ms, RFC 7845's pre-roll) from the end of the packet, counted from the next packets' starts and reported as they are written, so it runs on over several packets; where an after-gap span or an empty frame's span overlaps it the larger is withheld, not the sum, and a reset clears it. It is an estimate, as the span after a gap is.
  - **Speex:** 20 frames from the end of the packet (3,200, 6,400 or 12,800 samples), the same estimate as after a gap, run on over the following packets and cleared by a reset.
  - **FLAC, ALAC, TTA and WavPack** keep no audio state across units: the span is the unit alone (WavPack's set, as in pull request 3).
- **Fixtures** (`make_ogg_matroska_damaged_units_fixtures.py`, regenerated in CI): encoder-written files with the stored CRC of one page or one Cluster changed, so the packets decode and the decoded PCM equals the source's (a test):
  - `ogg/damaged-units/external-oggenc-q10-page-crc-changed.ogg` (Vorbis, fourth page);
  - `ogg/damaged-units/external-libopus-stereo-hybrid-20ms-repaged-page-crc-changed.opus` and `external-speexenc-wb-repaged-page-crc-changed.spx`: the encoder's files written again at 8 packets to a page (the encoders' files hold two or three audio pages, so one failing page would cover most of the stream), the granule positions from the source's;
  - `flac/damaged-units/external-ogg-24-stereo-spanning-page-crc-changed.ogg` (a frame that spans the failing page and the next);
  - `matroska/damaged-units/external-ffmpeg-small-clusters-cluster-crc-changed.mka` (Opus, eighth Cluster of 16).
- **Goldens added** (new `.measure.json`, nothing existing changed): those five, `ogg/bad-crc.spx` and `matroska/crc-damaged.mka`. They show, with one gap each and nothing lost: 7,104 + 1,024 samples (Vorbis), 8 x 960 + 3,840 (Opus), 8 x 320 + 20 x 320 (Speex), one frame of 16,384 (FLAC), 5 x 960 + 3,840 (Matroska) and the failing page's or Cluster's own packets in the two old files, which end the stream.
- **Existing tests changed:** `SpeexGapTests` damaged a packet without writing its page's CRC again, which the rule now withholds whole; it writes the CRC again so that the packet alone is damaged. Nothing else changed: no decode golden, no PCM hash and no other measurement golden belongs to a file with a failing page or Cluster CRC.
- **Tests:** `OggDamagedUnitsTests` and `MatroskaDamagedUnitsTests` (the withheld positions against the pages' and Clusters' own layout, the decoded PCM, samples and verification unchanged against the undamaged files, the goldens, a stream whose every audio page fails, a Cluster CRC changed in each of FLAC, Vorbis, Opus, ALAC, TTA, WavPack and PCM in Matroska, a Segment CRC that holds and one that fails).
- **Cross-checks:** none reads a measurement of these files, and the PCM cross-checks compare the unchanged decoded PCM.

## As built, pull request 6

- **The rule:** a frame is flagged (`CodedFrame.FailsChecksum`) when any of its bytes comes from a packet with `MappedBytes.FailsChecksum`. The decoders then withhold it and the output that depends on it exactly as for a frame whose own check fails (pull requests 3 and 4). The containers that mark packets are Ogg and Matroska; none of them carries these codecs in Ogg, so in practice it is Matroska's Cluster and Segment CRC-32.
- **MPEG audio** reads the packets as a byte stream (`PacketImage`, laid out at their file offsets with the container's own bytes left out), so a frame can lie across packets. The image records the ranges that come from flagged packets (`PacketImage.Failing`, those that touch merged, sorted), and a frame is flagged when its byte range meets one (`BuildContext.FailsContainerCheck`, a binary search): in the Layer III loop and the Layer I path through `Feed`, and in the deferred Layer II feed. The span is the one of a frame whose CRC fails: 1,056, 480 and 768 samples. An unprotected frame has no check of its own, but a container check counts as the unit's own (decision 2), so it is flagged all the same.
- **AC-3 and E-AC-3, DTS, and TrueHD and MLP** read a packet's frames one after another, each wholly inside its packet (a sample, an access unit), so a frame takes the packet's flag directly: `AddFrame` (AC-3 and E-AC-3, with a dependent frame's flag joining its program frame's), the core frame of a DTS unit, and `AddUnit` (TrueHD and MLP). TrueHD's span is that of a unit whose substream check fails: up to the next unit in which every substream restarts. A flag on a frame the parser cannot hand to a decoder (cut, unreadable) changes nothing: that frame is lost.
- **DTS (the decision):** the survey gives the DTS core no per-unit check ("never": the HCRC is not compared, and the extensions' header CRCs cover no core audio), so it has no rule of its own. The "Codecs in Ogg or Matroska" row of the per-format table covers it (a container's check counts as the unit's own, decision 2), and the decoder is a lossy one that keeps its state, so the lossy rule applies: the frame itself is withheld by the host, and the decoder reports the 480 samples after it (`DtsDecoder`'s after-gap span: the filter bank's history; the subband prediction converges within it, an estimate) with `WithholdFromMeasurements` as the frames after it are written, the larger of this span and an after-gap span where they overlap, and a reset clears it. A DTS frame in a raw file, `.dtshd`, MP4 or WAVE has no check and is never withheld. Only the core frame is flagged; an extension substream in the same packet is not decoded.
- **Layer III main data:** a frame whose main data (from `main_data_begin` back from its data, to its end) lie partly in a failing packet is flagged too, from the runs the bit reservoir already records for it (`BuildContext.RunsFailContainerCheck`), so it is withheld with its after-gap span like a failed frame. The container's CRC-32 covers those bytes, so the frame's output depends on them, which follows the lead's step 3 call that forward dependence is withheld. Only container ranges count: a frame CRC-16 still does not reach main data.
- **Beyond the span:** the spans of the other decoders are as they are for their own checks.
- **Fixtures** (`make_ogg_matroska_damaged_units_fixtures.py`, regenerated in CI): the encoder-written Matroska files of these codecs hold one Cluster, so a failing CRC-32 would cover the whole stream. `matroska/damaged-units/<source>-small-clusters-cluster-crc-changed.mka` is the source (`external-ffmpeg-mp3`, `-mp2`, `-ac3-51`, `-eac3-51`, `-dts-51`, `external-mkvmerge-truehd-50`, `external-ffmpeg-mlp-51`) with its blocks written again a few to a Cluster, each Cluster with a CRC-32 (no SeekHead and no Cues), and the CRC-32 of the fourth Cluster changed. The frames are the encoder's own, so the decoded PCM equals the source's (a test, and the `.decode.json` goldens of the AC-3 and E-AC-3 files are the sources').
- **Goldens added** (new `.measure.json`, nothing existing changed), each with one gap and nothing lost: Layer III 5 x 1,152 + 1,056 (the 3 frames of the failed Cluster and the 2 after it that read main data back into it), Layer II 12 x 1,152 + 480, AC-3 and E-AC-3 2 x 1,536 + 256, DTS 6 x 512 + 480, TrueHD 15 x 40 + 160 and MLP 30 x 40 + 320 samples.
- **Tests:** `MatroskaByteStreamDamagedUnitsTests` (the withheld samples, a Layer III frame whose own bytes are clean but whose reservoir bytes lie in the failing Cluster, the unchanged PCM, decoded samples and verification against the source, measuring adding nothing to the report, the goldens).
- **Cross-checks:** none reads a measurement of these files, and the PCM cross-checks compare the unchanged decoded PCM.
