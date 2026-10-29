# DTS: the core extension search and the DTS:X gate of the lossless extension

A clean-room description of three small pieces of the DTS parser and its fixture model, for an implementer who has not read FFmpeg's DTS decoder (LGPL). It states what the code must do so that every golden report, manifest and test stays as it is. The specification is ETSI TS 102 114 V1.6.1 (2019-08), fetched by `scripts/fixtures/dts_specs.py`; clause and table numbers below are its.

The three pieces:

- **A.** `UnitSyntax.FindExtension` (`src/Encspect.Formats.Dts/UnitSyntax.Extensions.cs`): where in a core frame its XCh, X96 or XXCH extension starts.
- **B.** The DTS:X gate of the lossless extension (`UnitSyntax.Lossless.cs`: `ReadXllFrame`, `ReadXllSetFields`, `ReadXllNavigation`; `UnitSyntax.Components.cs`: `ReadXll`): the conditions under which the word after the band data is not read.
- **C.** `_set_header` in `scripts/fixtures/dts_model.py`: the writer of a channel set sub-header (Table 8-4) for the fixtures.

Everything else in these files (the field reading, the trees, the findings, the word comparison itself in `ReadXllExtensionData`) stays as it is.

## A. Finding the core extension

### Inputs and output

- Called once per core frame of the core substream, after the frame header and the primary audio coding header have been read, while the file is analyzed (the node pass reuses the result kept in `UnitLayout.Extension`).
- Inputs: the core header (`ExtensionAudioId` is EXT_AUDIO_ID, `HasExtendedAudio` is EXT_AUDIO, 5.4.2 Table 5-2), `baseByte` (the frame's first byte in unit coordinates, 0 for the core substream), `availablePacked` (the frame's bytes in the bit-packed stream that the file has), `declared` (FSIZE + 1, the frame's size in bytes by its header), and the current bit position `_pos`, which is the end of the audio coding header.
- All positions are in the bit-packed stream (the 16-bit big-endian layout of 5.4 that `UnitView` holds); 14-bit and little-endian forms have already been converted. During the analysis the view holds every byte of the core frame that the file has and nothing after it; `PeekWord` returns null for a word that is not wholly there.
- Output: `null`, or a `FoundExtension(kind, byte, isLegacySize)`: the kind (`CoreExtension.Xch`, `X96` or `Xxch`), the unit byte of the extension's sync word, and for XCh whether the size field gives the distance itself (below).

### Rules

1. Only EXT_AUDIO = 1 with EXT_AUDIO_ID 0 (XCh), 2 (X96) or 6 (XXCH) is searched (Table 5-3); anything else returns `null` without a search.
2. **Candidates** are the 32-bit words at offsets from the frame's first byte that are multiples of 4 (6.4.2, 6.2.4.2 and 6.5.2: the sync words are aligned to 32 bits). The first candidate is the first such offset at or after the end of the audio coding header, the bit position rounded up to a whole byte and then to a multiple of 4 bytes. The last candidate is the last word that lies wholly within both `declared` and `availablePacked` bytes.
3. A candidate counts only if it equals the kind's sync word (XChSYNC 0x5A5A5A5A, SYNC96 0x1D95F262, SYNCXXCh 0x47004A03) and the 32-bit word after it is in the view.
4. The **distance** of a candidate is `declared` minus its offset from the frame's first byte: the bytes from the sync word to the end of the frame as FSIZE states it, whether or not the file has them.
5. **XCh** (6.4.2, Table 6-17): XChFSIZE is the top 10 bits of the following word. The candidate is the extension when XChFSIZE + 1 is at least 96 (XChFSIZE 95 to 1,023 is the valid range) and the distance equals XChFSIZE + 1, or equals XChFSIZE: the NOTE of 6.4.2 accepts both for legacy streams. In the second case `isLegacySize` is true, and `ReadXch` then reports `dts.extension-size-legacy` (`Issue.ExtensionSizeLegacy`).
6. **X96** (6.2.4.2, Table 6-1): FSIZE96 is the top 12 bits of the following word plus 1. The candidate is the extension when FSIZE96 is at least 96 (the valid range is 96 to 4,096) and the distance equals FSIZE96 exactly.
7. **XXCH** (6.5.2, Table 6-21): nuHeaderSizeXXCh is the top 6 bits of the following word plus 1. A candidate qualifies when the header size is at least 11 bytes and at most the distance. It is the extension when the CRC-16 of Annex B (`Crc16.Compute`) over the header size minus 4 bytes after the sync word (from the header size field through nCRC16HeaderXXCh, the stored CRC included) comes to 0. A qualifying candidate whose CRC does not hold is still accepted, as a fallback, when `XxchSizesReachEnd` holds for it (the header size plus each pnuChSetFsizeXXCh reach the distance, less up to 3 alignment bytes); that helper is Encspect's own and stays.
8. **Which candidate wins:** the one nearest the end of the frame. For XCh and X96 that is the qualifying candidate at the highest offset. For XXCH it is the highest candidate whose CRC holds; only when no candidate's CRC holds is it the highest fallback candidate. With no winner the result is `null`, and the caller reports `dts.extension-missing` with `HowFound`'s text (unchanged).
9. The XXCH CRC reads the header bytes with `UnitView.Peek`, which throws `EndOfStreamException` when they are not all in the view. The caller already catches that as "the core frame ends inside its header", which a frame the file cuts short gives; keep this behavior.

### Where this differs from the specification

- The specification asks for the distance check ("MANDATORY" for XCh in 6.4.2, "shall match" for X96 in 6.2.4.2) but names no search order. Searching from the end means a pseudo sync word inside the audio data cannot hide the real one. No fixture has two qualifying candidates, so the order is not pinned, but it is the current behavior.
- The minima are the specification's ranges for XCh and X96. For the XXCH header the syntax admits 10 bytes at the least (one channel set, a 1-bit mask, alignment and the CRC); the current bound of 11 is kept so that no report changes. No fixture has an XXCH header under 11 bytes, and none distinguishes these minima at all (checked with a model of these rules; see Verification).
- The specification has no fallback for an XXCH header whose CRC fails; the fallback is Encspect's, pinned by `xxch-bad-crc`.

## B. The DTS:X gate of the lossless extension

### What the gate decides

- No specification describes DTS:X. After the band data of a lossless frame, `ReadXllExtensionData` reads the 32-bit word at the end of the band data, rounded up to a multiple of 4 bytes from the XLL sync word, when that position is before the frame's end and 4 bytes are there. 0x02000850 is DTS:X; 0xF14000D0 and 0xF14000D1 are DTS:X IMAX. This part is unchanged.
- The gate decides whether that word is read at all. The current behavior is to read it only where FFmpeg 6.1.1's DTS decoder reaches it: the decoder gives up on a lossless frame for a set of header values, some invalid and some it does not implement, before it looks at the word. These are behaviors observed of FFmpeg, recorded in the fixture manifests and in `docs/formats/dts.md` ("DTS:X").
- Its observable effects are the asset's `XllExtension` (and from it the profile "DTS-HD Master Audio + DTS:X" or "+ DTS:X IMAX" and the statistics `HasDtsX` and `HasDtsXImax`) and the node after the band data: "Extension Data" with its "Sync Word" when the word is read and known, "Data After Band Data" otherwise. A refused frame with a word that is neither DTS:X nor IMAX therefore reports exactly what an accepted one does.
- In the code, the gate is the `isWordRead` argument of `ReadXllExtensionData`: it is `allFacts && !refused`, where `allFacts` is the existing test that every channel set gave its facts (below) and `refused` is what this section describes. The stubs leave `_isXllRefused` false and `XllCommon.IsRefused` false.

### The conditions (any one refuses the frame)

The analyzer, unlike FFmpeg, does not stop at the first refusal: it reads every field and shows it. The outcome is therefore the union of the conditions below, and the order in which they are tested makes no difference. Each is read from a field the analyzer already reads; "set" is a channel set sub-header of 8.3.2, Table 8-4.

Common header (8.3.1, Table 8-1), passed as `XllCommon.IsRefused`:

| Condition | Specification | Fixture |
|---|---|---|
| nLLFrameSize more than 245,760 bytes (240 KiB) | no limit | none |
| nSegmentsInFrame more than 1,024 (exponent above 10) | no limit | none |
| nSmplInSeg of 1 (exponent 0) or more than 512 (exponent above 9) | 8.3.1 allows at most 512 (256 at 48 kHz or below, which is a `dts.reserved-value` finding, not a refusal); 1 is allowed | `xll-x-no-segment-samples`, `xll-x-segment-samples` (1,024) |
| nSegmentsInFrame times nSmplInSeg more than 65,536 (exponents summing to more than 16) | no limit | none |
| nNumChSetsInFrame more than 3 | up to 16 | none |

Each set (8.3.2, Table 8-4):

| Condition | Specification | Fixture |
|---|---|---|
| nChSetLLChannel more than 8 | up to 16 | none |
| nBitWidth (storage width) other than 16, 20 or 24 | 1 to 32 | `xll-x-storage-width-18` |
| the sampling frequency of sFreqIndex (Table 8-5) above 192,000 Hz | 352.8 and 384 kHz are defined | none |
| nFsInterpolate other than 0 | Table 8-6 defines factors | `xll-x-interpolated` |
| nReplacementSet other than 0 | Table 8-7 defines sets 1 to 3 | none with the word (`xll-replacement-set` has none) |
| one-to-one mapping: bPrimaryChSet set on any set but the first, or clear on the first | | none |
| one-to-one mapping: nLLDownmixType 7 (read only with downmix coefficients in a primary set) | Table 8-8: unused | `xll-x-downmix-type-7` |
| one-to-one mapping: bHierChSet clear while nNumChSetsInFrame is not 1 | | none |
| one-to-one mapping: bChMaskEnabled clear (polar speaker positions) | allowed | `xll-x-no-mask` |
| not one-to-one: nChSetLLChannel other than 2, or nNumChSetsInFrame other than 1, or bMappingCoeffsPresent set | allowed | none |
| sampling frequency above 96 kHz and bXtraFreqBands set (4 bands) | allowed | none |
| a set whose number of frequency bands, sampling frequency, nBitResolution or nBitWidth differs from the first set's | | none |

The one-to-one mapping is the asset's bOne2OneMapChannels2Speakers (`AssetFacts.IsOneToOne`). The comparison with the first set uses the sets that gave facts; where a set gave none, `allFacts` already stops the word. The downmix type 7 case keeps its `dts.reserved-value` finding; refusing it agrees with Table 8-8.

Conditions in the code that stays, listed so the whole gate is in one place:

- A set whose fields contradict each other gives no facts, so `allFacts` is false and the word is not read: nBitResolution above nBitWidth (`dts.lossless-inconsistent`, fixture `xll-x-rejected-set`), or a channel mask whose number of speakers is not nChSetLLChannel (`dts.lossless-inconsistent`).
- A set whose CRC-16 fails or that does not fit its stated size ends the reading of the frame before the navigation index.
- A navigation index whose CRC-16 fails is read but `ReadXllExtensionData` is not called (fixture `xll-x-navigation-bad-crc`, no node after the band data).
- A version above 1 reads no set (`dts.reserved-value`).
- A band data size of the frame's size or more puts the end of the band data past the frame, so there is no word.

### The size of the navigation index

Before the navigation index (8.4.2) is read, its entries are counted the way FFmpeg 6.1.1 bounds them: the largest number of frequency bands of any set, times nSegmentsInFrame, times nNumChSetsInFrame. This product can be larger than the true number of entries (each set's own bands times the segments, summed), which the reading itself uses. When the product is above 1,024:

- a field "Navigation Index" from the start of the index to the end of what is readable (`Math.Min(_limit, endBit)`), valued as the product in "entries" (singular "entry"), with `Explain.XllNavigation` and `Topics.Lossless`;
- a `dts.lossless-inconsistent` finding on that field, currently worded "the navigation index would have N entries (the most bands, times the segments, times the channel sets), more than the 1024 that FFmpeg reads";
- nothing else of the frame: no sizes, CRC, band data or word.

No fixture reaches this case. The wording may change; the behavior is the current one.

### FFmpeg as the black box

The fixture manifests (`scripts/fixtures/dts_lossless_fixtures.py`, its `undecodable` cases) record, for each fixture listed in the tables, that ffmpeg's decoder refuses the frame and the message it logs, and that ffprobe names the DTS:X profiles for `xll-x`, `xll-x-imax`, `xll-x-imax-d1` and `dtshd-x`. `crosscheck_dts_fixtures.py` checks this with ffmpeg 8.0. The conditions without a fixture rest on the behavior recorded in `docs/formats/dts.md`. MediaInfo 24.01 reads the word after a set that FFmpeg refuses; the analyzer keeps to FFmpeg's behavior.

## C. `_set_header` in `scripts/fixtures/dts_model.py`

`_set_header(s, hier_ofs, mask_bits, size_bits, scalable)` returns one channel set sub-header as bytes, with Table 8-4's fields in its order. Every field is written most significant bit first with `mp4_boxes.Bits`. CI regenerates the fixtures and fails on any difference, so the output must stay byte for byte the same for every set the fixtures build.

Arguments: `s` is the dict of `xll_set` (unchanged), `hier_ofs` the channels of the hierarchical sets before this one (`xll_frame` counts them), `mask_bits` nBits4ChMask, `size_bits` nBits4SSize, and `scalable` bScalableLSBs (Table 8-1).

The fields, in this order (n is `s["channels"]`):

1. nChSetHeaderSize: 10 bits, written as 0 and filled in at the end.
2. nChSetLLChannel − 1 (4 bits); the residual channel encoding `s["residual"]` (n bits); nBitResolution − 1 and nBitWidth − 1 (5 bits each, from `bit_res` and `storage`); sFreqIndex (4 bits, `MAX_SAMPLE_RATE_CODES[s["rate"]]`, Table 8-5); nFsInterpolate (2 bits, `interp`); nReplacementSet (2 bits, `replacement`); and when it is not 0, bActiveReplaceSet (1 bit, `active`).
3. With `one_to_one`: bPrimaryChSet (`primary`); bDownmixCoeffCodeEmbedded (1 when `dmix` is given); with `dmix`, bDownmixEmbedded (`dmix["embedded"]`); with `dmix` and `primary`, nLLDownmixType (3 bits, `dmix.get("type", 1)`); bHierChSet (`hier`).
4. Still with `one_to_one` and `dmix`, the downmix coefficients of 8.3.2 ("DownmixCoeffs"), 9 bits each (C.9):
   - a primary set: M times n codes of 0, where M is the number of channels Table 8-8's type mixes to (1/0 gives 1; Lo/Ro and Lt/Rt 2; 3/0 and 2/1 3; 2/2 and 3/1 4; 7, unused, 0), as clause 5.8.2 also counts them;
   - any other set: for each of the `hier_ofs` channels before it, a scale code of 0x040 followed by n codes of 0, (n + 1) times `hier_ofs` codes in all. 0x040 is a scale index inside the range C.9 allows for a scale (index 40 to 240) and is what the committed fixtures hold.
5. Still with `one_to_one`: bChMaskEnabled (0 when `polar`, else 1); then with `polar`, 25 zero bits for each of the n channels (RadiusDelta, Theta and Phi); otherwise nChMask (`mask`, `mask_bits` bits).
6. Without `one_to_one`: bMappingCoeffsPresent as 0, and nothing else of that branch.
7. When the sampling frequency is above 96 kHz: bXtraFreqBands (`extra_bands`). The number of frequency bands is 4 with it, 2 without, and 1 at 96 kHz or below (`_bands_of`).
8. For each band, all zero: bPWChDecorEnabled (1 bit, so no channel order and no pair coefficients follow), n adaptive predictor orders of 4 bits, n fixed predictor orders of 2 bits; no reflection coefficient follows an order of 0. Then:
   - band 0, only with `scalable`: nLSBFsize[0] (`size_bits` bits), n scalable LSB widths and n bit width adjustments (4 bits each);
   - every further band: bEmbDMixInExtBand (1 bit) only for a one-to-one set with `dmix` whose `embedded` is true; bMSBLSBSplitEnInExtBands (1 bit, 0); bFlagScalableResExtBand (1 bit, 0).
9. `header_pad` zero bytes (the reserved field), alignment to a byte, and 16 bits for nCRC16SubHeader.

Then, with the total length in bytes (at most 1,024): the stated size is `size_override` when given, else the length; its value minus 1 goes into the first 10 bits (the 8 bits of byte 0 and the top 2 of byte 1). The CRC-16 (`crc16`, Annex B) is computed over every byte before the CRC field, from the size field on, after the size is written, and stored big-endian in the last two bytes (8.3.2: the CRC covers the sub-header from nChSetHeaderSize to ByteAlign). With `bad_crc`, the last byte is then XORed with 0xFF.

`DMIX_PRIMARY_CHANNELS`, the table of step 4, may be defined again from Table 8-8 under any name.

## Verification

- **Tests** (they need `tests/fixtures/dts` and `tests/conformance`, so a full checkout):
  - `DtsFixtureTests`: every DTS fixture's manifest and golden report (`*.expected.json`, `*.report.json`). For A: `xch`, `xch-legacy-size`, `x96`, `xxch`, `xxch-bad-crc` (the fallback), `xxch-header-overrun`, `xxch-mask-bits-5`, `extension-missing`, and the 14-bit and little-endian forms (`14be-*`, `14le-*`, `16le-*` of `xch`, `x96`, `xxch`, `extension-missing`, `xxch-header-overrun`). For B: every `xll-*`, `exss-lossless*` and `dtshd-*` fixture, and especially `xll-x`, `xll-x-imax`, `xll-x-imax-d1`, `xll-x-other-word`, `dtshd-x` and the refused `xll-x-*` fixtures of the tables.
  - `DtsLosslessTests` (`TheWordAfterTheBandDataNamesDtsX`, `TheDtsXWordIsNotLookedForAfterARejectedChannelSet`, `TheDtsXWordIsNotLookedForAfterAFailingNavigationIndex`, `AFrameWithoutRoomAfterItsBandDataHasNoExtensionData`), `DtsTests.TheFourFormsReadTheSameFields`, `CorruptDtsFileTests`, `DescriptionTests` and `FindingCoverageTests`.
  - Run: `dotnet test tests/Encspect.Tests -c Release --filter "FullyQualifiedName~Dts|FullyQualifiedName~DescriptionTests|FullyQualifiedName~FindingCoverageTests"`.
- **Fixtures for C:** `python3 scripts/fixtures/make_dts_fixtures.py`, then `git status tests/fixtures/dts` must show no change.
- **Black-box cross-checks:**
  - `python3 scripts/fixtures/crosscheck_dts_fixtures.py` (ffprobe and ffmpeg 8.0, libdca through `dca_frames.c`, MediaInfo 26.01): ffprobe's profiles (DTS-ES with XCh, DTS 96/24 with X96, the DTS:X profiles), ffmpeg's decode of each packet with the refusals the manifests list, libdca's frames.
  - `python3 scripts/fixtures/check_dts_hd_samples.py --analyzer <encspect>`: the three real DTS-HD Master Audio files of the FFmpeg sample archive (one with two channel sets), which the script downloads; none has DTS:X, and their findings must stay exactly as listed.
- **This description was checked** with a model of rules A1 to A8 run over every golden report of `tests/fixtures/dts` (83 core frames with an extension or `dts.extension-missing`, all agree) and a model of the gate in B run over every lossless frame whose word after the band data is DTS:X or IMAX (44 frames, all agree; without the gate 28 of them disagree).

## Implementer's sources

- May read: this description; ETSI TS 102 114 V1.6.1 (`scripts/fixtures/dts_specs.py` fetches it); the rest of the repository, including `docs/formats/dts.md`, the tests, the fixture scripts, the fixtures and their golden reports; the output of ffprobe, ffmpeg, libdca and MediaInfo run as black boxes.
- Must not read: FFmpeg's source (`libavcodec/dca_core.c`, `dca_xll.c`, `dca_exss.c` and the rest of its DTS decoder); the git history of the stubbed members (`FindExtension`, the refusal statements of `ReadXllFrame`, `ReadXllSetFields`, `ReadXllNavigation` and `ReadXll`, and `_set_header`, before the stub commit); the describer's scratch folder; the audit reports `scratchpad/reviews/license-audit-*.md`.
