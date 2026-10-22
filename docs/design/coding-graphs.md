# Coding graphs: per-frame coding choices and stream summaries

Status: a design for the project lead's sign-off. Nothing here is implemented. The decisions are collected under [Decisions](#decisions), and claims that no source read for this design confirms are marked **unverified**.

## Scope

What the lead asked for:

- Graphs over time of the coding choices each frame makes:
  - the stereo coding of joint stereo frames (left/right, mid/side, intensity stereo, mid/side and intensity stereo);
  - the distribution of long and short blocks (long, start, short, stop and mixed for Layer III; the window sequences for AAC);
  - whatever else the parsers read that is worth a graph.
- Summaries over the whole stream, as EncSpot's window shows them: the share of mid/side, intensity stereo and plain stereo frames, of long and short blocks, and so on.

What the plan already settles, and this design keeps:

- Both front ends are thin views over the same result model, "so neither can show something the other cannot" (plan, "CLI and GUI"). Every graph and summary the GUI draws is therefore in the JSON report, and its summary in the text tree.
- The GUI draws its own graphs, from the streams' series alone, without a charting library (plan, "CLI and GUI"; [gui.md](../gui.md), "Graphs").
- Front ends read kinds and streams, never node names (`CLAUDE.md`).
- Reports keep compact records per frame and a bounded number of bytes per frame (`MemoryTests`).

What is not in scope:

- Anything that needs the entropy-coded data the parsers do not walk: Layer III scalefactors and Huffman-coded spectra, DTS core subframes, Vorbis floors and residues, Opus SILK and CELT, Musepack SV8 frames inside packets. The plan puts these under "Later" ("An entropy pass, format by format").
- Anything from decoded audio (lowpass measured from the spectrum, waveform, spectrogram), which the [decoder design](decoders.md) leaves to host-side PCM analyses.

Sources read for this design:

- The code at 2310aa3: `StreamInfo` and `Series`; the MPEG parser's `MpegAudioParser.Tally.cs`, `.Statistics.cs`, `.Streams.cs`, `Layer3SideInfo.cs` and the frame record `Entry`; AAC's `CodingTally`, `CodingStatistics`, `StreamFigures`, `StreamSetup` and `BlockReader`; AC-3's `Program`, `FrameResult`, `FrameSyntax.Blocks.cs` and `StreamFigures`; DTS's `UnitSyntax.*`, `Facts`, `DtsStream`; Musepack's `Sv7Parser`, `OldStreamParser`, `Sv8Parser`; Vorbis's, Opus's and Speex's records and reports; WMA's `StandardDecoder`, `StandardStream`, `StandardReport`, `ProRecords`, `ProReport`, `VoiceRecords`; WavPack's `Model`, `Reader`, `Summary`; the host's JSON and text writers and `ReportSummary`; the GUI's `Graph`, `GraphsViewModel`, `GraphView`, `Palette`; `MemoryTests`, `ScreenshotTests`, `GraphViewTests`; the cross-check tools in `scripts/fixtures`.
- The vendored references: minimp3 (`third_party/minimp3/minimp3.h`: `L3_huffman`, `L3_reorder`), EncSpot (`third_party/encspot/lib/Mp3File.cpp`: `ProcessNextFrame`), mp3guessenc.
- The decoder contract under way on `feature/decoder-contract` (its working tree, read only), for the fields it adds to `StreamInfo`, the writers and `ReportSummary`.
- The specification sections the parsers cite in their code and descriptions; where this design cites a section no parser cites, it says so.

## The design in short

- A new type in `Core.Abstractions`, `CategorySeries`, holds one category per frame, or per granule and channel, block and channel or packet and stream: compact codes of one byte each, the categories with names and descriptions, and the counts over the whole stream. It sits beside `Series`, which keeps holding numbers.
- The parsers record the codes while they read the frames, as they count their tallies now. A Layer III frame of MPEG-1 stereo keeps 6 bytes more: 4 block types, its joint stereo mode and its channel mode.
- The whole-stream summary stays what the parsers already report: the tally nodes under Statistics ("Block Usage", "Joint Stereo Modes"). Each category series carries the same categories, under the same names, anchored to that node; the counts over the series are computed by the shared type, and a test holds them equal to the tally.
- Coded bandwidth over time is a numeric `Series`, "Coded Bandwidth" in Hz, wherever a header or side information states it without decoding.
- JSON adds a `categorySeries` array to each stream, so `schemaVersion` stays 1. The text tree prints each category's share per second after its tally node. The CSV does not change in this rollout.
- The GUI draws three views of a category series (share per second, a per-frame strip, a summary table like EncSpot's) in a fixed palette checked for color vision deficiencies, always with names and shares in text.
- Rollout: the contract after the decoder contract merges, MPEG as the reference, then the other formats in parallel.

## The survey

What each parser reads per frame, packet or block today that is worth a graph or a summary.

- **Read** says whether the parser reads the field now; **Tallied** whether its Statistics node counts it now.
- **Kept per unit** is what the design adds per frame, packet or block: one byte per value of a category series, 8 bytes per value of a numeric series. The x values (frame offsets) are shared with the stream's per-frame bitrate series and cost nothing more.
- **Status:** "code" is verified from the parser's code at 2310aa3, "spec" from a specification or reference source cited there, **unverified** otherwise.
- **Wave:** 1 is the first rollout, 2 the second wave, "later" waits for the entropy pass or a parser change.

### MPEG audio, all layers

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Channel modes | header mode, ISO/IEC 11172-3 2.4.2.3 | yes, in the frame record's header | "Channel Modes" | 1 byte per frame, or none: derived from the kept header | code | 1 |
| Joint stereo modes, Layer III | mode_extension: bit 1 mid/side stereo, bit 0 intensity stereo (2.4.2.3) | yes, in the kept header | "Joint Stereo Modes" (joint stereo frames only) | 1 byte per frame, or none (derived) | code | 1 |
| Intensity stereo bounds, Layers I and II | mode_extension: bands 4, 8, 12 or 16 to 31 (2.4.2.3) | yes, in the kept header | "Intensity Stereo Bounds" | 1 byte per frame, or none (derived) | code | 1 |
| Block usage, Layer III | block_type, window_switching_flag, mixed_block_flag per granule and channel (2.4.1.7, 2.4.2.7) | yes, side information kept in the store | "Block Usage", granules counted per channel: long, start, short, mixed, stop, reserved | 4 bytes per MPEG-1 stereo frame (2 granules × 2 channels), 2 for MPEG-2 stereo, 1 or 2 for mono | code | 1 |
| Coded bandwidth, Layers I and II | the highest subband with a bit allocation other than 0 in either channel; 32 subbands of fs/64 each | yes, the allocation is read for every frame (Layer II's Annex B tables) | no | 8 bytes per frame (1 byte recorded while reading, the value built at the end) | code (reading); the Hz conversion spec | 1 |
| Scalefactor scaling, Layer III | scalefac_scale per granule and channel (2.4.2.7) | yes | share of granules ("Scalefactor Scaling") | 4 bytes per frame | code | 2 |
| Global gain, Layer III | global_gain per granule and channel | yes | smallest and largest per channel | 8 bytes per value as a numeric series | code | 2 |
| big_values region end, Layer III | big_values × 2 lines (2.4.2.7); lines above it are the count1 region, whose end needs Huffman decoding | big_values yes; count1 no (the parser does no Huffman decoding) | no | 8 bytes per frame | code; minimp3 `L3_huffman` reads count1 until part2_3_length's bits run out | later |
| Intensity stereo bound, Layer III | where the right channel's zero region begins (2.4.3.4) | no: it needs the Huffman-coded spectrum | no | | spec, from minimp3's `L3_intensity_stereo` | later |

What big_values tells and what it does not:

- It is the number of pairs of frequency lines, from the lowest, in the big-values region, where values may exceed 1 (the parser's own description of the field).
- Above it lies the count1 region of quadruples of -1, 0 or 1, which runs until the granule's part2_3_length bits are spent (minimp3 `L3_huffman`). Its end, and with it the highest line that is not zero, needs Huffman decoding.
- So big_values × 2 is neither the coded bandwidth nor a bound of it: nonzero lines of ±1 may lie above it, and the big-values region may end in zeros.
- In short blocks the lines are coded band by band, each band's three windows one after the other (minimp3 `L3_reorder` undoes that order), so the line index still maps to frequency at the granularity of a scalefactor band.
- It does follow an encoder's lowpass where the encoder filters the input, but a graph of it named as bandwidth would mislead. It waits for the entropy pass, where the end of the count1 region gives the real highest coded line.

How EncSpot counts, from its source (`Mp3File.cpp`, `ProcessNextFrame`), which the cross-check needs:

- Its mode counts take the mode extension of joint stereo frames and count every other frame (stereo, dual channel, mono) as mode 0, so its "plain stereo" share is the report's stereo, dual channel and mono frames plus the joint stereo frames coded left/right.
- It counts long, short and mixed granules; start and stop blocks count as long.
- Its per-frame lists (the ones its window graphs) keep the mode extension of every frame and one block type per frame, the last switched granule's.

### AAC (ADTS, ADIF, MP4, Matroska)

The AAC codec parser serves every container that routes AAC to it. There is no LATM or LOAS parser (no source in the tree mentions either), so LATM has nothing to survey.

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Window sequences | window_sequence of each channel stream (ISO/IEC 14496-3 4.4.2, ics_info): only long, long start, eight short, long stop | yes, every raw data block of Main, LC, SSR and LTP | "Window Sequences", per channel stream | 1 byte per channel stream (2 for a stereo access unit) | code | 1 |
| Stereo coding of channel pairs | ms_mask_present and ms_used (4.6.8.1); intensity stereo codebooks 14 and 15 in the second channel (4.6.8.2) | yes | "Channel Pairs": common window, mid/side masks, mid/side bands, intensity stereo pairs and bands | 1 byte per channel pair element | code | 1 |
| Coded bandwidth | max_sfb of each channel stream, taken to the end of its band in the window's swb_offset table (long or short, 1024 or 960 lines), in Hz | yes; the long and short tables are in `StreamSetup` | the most common long max_sfb as "Bandwidth" (text) | 8 bytes per access unit | code | 1 |
| Window shapes | window_shape: sine, KBD | yes | "Window Shapes" | 1 byte per channel stream | code | 2 |
| TNS, noise substitution, pulse data | tns_data_present (4.6.9), codebook 13 bands (4.6.2, as the `pns` article cites it), pulse_data_present (4.5.2, as `aac-pulse-data` cites it) | yes | shares of channel streams | 1 byte per channel stream each | code | 2 |
| SBR and parametric stereo per access unit | SBR payloads in fill elements | yes | "SBR", "Parametric Stereo" | 1 byte per access unit | code | 2 |
| Bands coded mid/side per access unit | count of ms_used bands over the pair's bands | yes | the share over the stream | 8 bytes per access unit as a numeric series | code | 2 |

- Coded bandwidth from max_sfb is the top of the highest band the channel stream codes, whatever its codebook. Bands coded with the zero codebook, noise substitution or intensity stereo lie below it, and SBR adds content above the core that this does not show. The description says so.
- The tally counts a pair's stereo coding only where it has a common window (`TallyPair` returns before the masks otherwise). Whether a decoder applies intensity stereo in a pair without a common window is **unverified**; the category series keeps the tally's rule and gives such pairs a category of their own.
- Past 2,097,152 frame or block records an ADTS or ADIF stream is only counted (`aac.frames-limit`, `aac.blocks-limit`), and the graphs and coding statistics stop there. The category series stop at the same unit.

### AC-3 and E-AC-3

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Block switching | blksw per channel and block (ATSC A/52 5.4.3.1); E-AC-3 only where blkswe is set | yes, every block read | "Blocks With Block Switching", per block (any channel) | 1 byte per block: 6 per AC-3 frame, 1 to 6 in E-AC-3 | code | 1 |
| Coupling | cplinu per block (5.4.3.8) | yes | "Blocks With Coupling" | 1 byte per block | code | 1 |
| Rematrixing (2/0 only) | rematflg per band and block (5.4.3.20) | yes | "Blocks With Rematrixing", "Bands Coded as Sum/Difference" | 1 byte per block | code | 1 |
| Spectral extension (E-AC-3) | spxinu per block (Annex E 2.3.3.2) | yes | "Blocks With Spectral Extension" | 1 byte per block | code | 1 |
| Coded bandwidth | the end bin of each full-bandwidth channel: 37 + 3 × (chbwcod + 12) where it codes its own exponents, the end of the coupling range (37 + 12 × (cplendf + 3)) for a coupled channel; bin k is k × fs / 512 Hz | yes, `_endMant` per channel and block | no | 8 bytes per frame (the highest over its blocks and channels) | code | 1 |
| Exponent strategies | D15, D25, D45, reuse per channel and block | yes | "Exponent Strategies" | up to 30 bytes per frame | code | later |
| Dynamic range gain, delta bit allocation | per block | yes | counted per block | 1 byte per block each | code | later |

- Spectral extension regenerates the band above its begin frequency from lower bands, so that band is not coded data; the coded bandwidth of a channel in spectral extension ends where spectral extension begins (bin 25 + 12 × its begin sub-band, as the parser sets `_endMant`). The LFE channel is left out.
- Blocks the parser does not read (adaptive hybrid transform, enhanced coupling, reduced sample rates; `StopReason`) get no category, as they count in "Audio Blocks" but not in "Audio Blocks Read".
- Per-channel block switching would be 6 × channels bytes a frame and has no tally; it is left for later.

### DTS and DTS-HD

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Coded bandwidth of the core | SUBS + 2 active subbands per channel (ETSI TS 102 114 5.4.3), 32 subbands of fs/64 | yes, the audio coding header of every core frame | no | 8 bytes per frame (the highest over the primary channels) | code | 2 |
| Sum/difference coding | SUMF (front pair), SUMS (surround pair), 5.4.2 | yes (`Facts`) | no | 1 byte per frame | code | 2 |
| Joint intensity coding | JOINX per channel (5.4.3) | yes | no | 1 byte per channel | code | 2 |
| Frames by kind and extension | core, XCh, X96, XXCH, extension substream | yes | "Frames With Extension" and the frame counts | 1 byte per frame | code | later |
| Transient modes | TMODE per subband, in the subframes | no (core subframes are "Later" in the plan) | no | | spec | later |

- Joint intensity coding fills a channel's subbands above its active ones from a source channel, so the highest active subband over all channels is the coded bandwidth (the parser's description of SUBS).

### Vorbis

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Block sizes | the mode's blockflag (Vorbis I 4.3.1), with the previous and next window flags | yes, in the packet record (`PacketFlags.Long`, `PreviousLong`, `NextLong`) | "Long Blocks", "Short Blocks" | none recorded: derived from the record; 1 byte per packet in the series | code | 1 |
| Modes | mode number per packet | yes, in the record | "Modes" | 1 byte per packet | code | 1 |
| Coded bandwidth | residue_end of each residue (8.6.1) | yes, in the setup header | no | | code | not a series |

- residue_end is fixed per residue, so the bandwidth it implies changes only with the mode a packet chooses; the "Modes" series already shows that. Where the coded spectrum really ends needs the floors and residues, which wait for the entropy pass.
- Coupling (square polar mapping) is fixed per mapping, so a per-packet stereo coding graph would repeat the mode as well.

### Opus

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Modes | TOC config: SILK-only, hybrid, CELT-only (RFC 6716 3.1) | yes, the TOC byte is in the record | "Modes", per frame | none recorded; 1 byte per stream packet in the series, plus a weight byte (its frames) | code | 1 |
| Bandwidth | TOC config: narrowband (4 kHz) to fullband (20 kHz), RFC 6716 table 1 | yes | "Bandwidths", per frame | as above | code | 1 |
| Frame sizes | TOC config: 2.5 to 60 ms | yes | "Frame Sizes", per frame | as above | code | 1 |
| Stereo coding | TOC s bit: stereo or mono packet | yes | "Stereo Coding", per stream packet | 1 byte per stream packet | code | 1 |

- Opus states its coded audio bandwidth in every packet, so its "Bandwidth" is a category series, not a number: the five bandwidths are what the format defines, and their names carry the frequency.
- The tally counts modes, bandwidths and frame sizes per frame and stereo coding per stream packet. All frames of a packet share its TOC, so a weight of the packet's frames makes the series' counts equal to the tally's.
- What SILK or CELT does inside a frame (CELT's intensity stereo, its band energies) needs the range decoder: later.

### Speex

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Narrowband submode | 4-bit submode per frame (libspeex 1.2.1 `nb_decode`); 0 is a frame without speech data | yes, the 4-byte frame record | frames of each submode per layer | none recorded; 1 byte per frame in the series | code | 1 |
| Wideband and ultra-wideband submodes | 3-bit submode per layer (`sb_decode`) | yes | as above | 1 byte per frame and layer | code | 1 |

- Ogg Speex states the frames per packet in its header, so a packet is the unit and its frames are the slots; the x values are the packets', shared with the bitrate series.

### Musepack SV4 to SV8

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Coded bandwidth, SV4 to SV7 | used subbands: the highest subband with a resolution other than 0 in either channel, plus 1 (libmpcdec r495 `mpc_decoder_read_bitstream_sv7`, 1.2.6 for SV4 to SV6); 32 subbands of fs/64 | yes, `UsedBands` in the 24-byte frame record | no | none recorded; 8 bytes per frame in the series | code | 1 |
| Mid/side stereo, SV4 to SV7 | the mid/side flag of each coded subband | yes, `MidSide` mask in the record | "Frames With Mid/Side", "Subband Stereo Coding" | none recorded; 1 byte per frame | code | 1 |
| Noise substitution, SV7 | subband channels at resolution -1 | yes, `NoiseChannels` | "Noise-Substituted Channels" total | 8 bytes per frame as a numeric series | code | 2 |
| SV8 | the frames inside audio packets | no: audio packets are not read | no | | code | later |

- The header's maximum band is a bound for the whole stream; the per-frame used subbands are the coded bandwidth over time, at the resolution of one subband (689 Hz at 44.1 kHz).

### WMA standard, Pro and Voice

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Block sizes, standard | block length per block, from frame length >> n (FFmpeg 6.1.1 `wmadec.c` `wma_decode_block`) | yes, `BlockRecord.BlockBits` | "Block Sizes" | 1 byte per block | code | 2 |
| Mid/side stereo, standard | ms_stereo bit per block | yes, `BlockRecord.IsMidSide` | "Mid/Side Blocks" | 1 byte per block | code | 2 |
| Noise substitution, standard | noise-coded bands per block | yes, `NoiseBands0`, `NoiseBands1` | "Noise-Substituted Blocks" | 1 byte per block | code | 2 |
| Coded bandwidth, standard | the last nonzero coefficient of each channel, from the run-level codes the parser walks; (first coded line + last + 1) × (fs / 2) / block length, where version 1 codes from line 3 (`CoefsStart`) | yes, `BlockRecord.Last0`, `Last1` | no | 8 bytes per block | code | 2 |
| Subframe sizes and channel transforms, Pro | subframe length per channel; transform per channel group | yes (`ProSubframeRecord`, `ProGroupRecord`) | "Subframe Sizes", "Channel Groups" | 1 byte per subframe and group | code | later |
| Frame types, Voice | the frame type of each frame (`wmavoice.c`) | yes, `VoiceFrameRecord.Type` | frames by type | 1 byte per frame | code | 2 |

- WMA fixes two bounds per block size for the whole stream (`StandardParameters`, after `ff_wma_init`): `CoefsEnd`, 91% of the block's lines, past which no coefficient is coded, and `HighBandStart`, from the bits per sample, where noise coding may begin. The last nonzero coefficient is where each block's coded spectrum really ends; noise-substituted bands are synthesized and not counted.
- Blocks lie inside frames that may span superframes, so a block's x value is its frame's offset, and choosing a block selects its frame.

### WavPack

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Mid/side stereo | the joint stereo flag of each stereo block's header | yes, the header is in the block record | "Mid/Side Blocks" | none recorded; 1 byte per block | code | 2 |
| Decorrelation passes | decorrelation terms per block | yes | the smallest and largest | 8 bytes per block as a numeric series | code | 2 |

- A WavPack block holds about half a second to a second of audio, so these graphs have few points; they are cheap and fill the summary.

### What goes in the first rollout

- **Wave 1**, one series per tally the lead named, in the formats that read them per frame today: MPEG (channel modes, joint stereo modes, intensity stereo bounds, block usage, coded bandwidth of Layers I and II), AAC (window sequences, stereo coding of channel pairs, coded bandwidth), AC-3 and E-AC-3 (block switching, coupling, rematrixing, spectral extension, coded bandwidth), Vorbis (block sizes, modes), Opus (modes, bandwidth, frame sizes, stereo coding), Speex (submodes), Musepack SV4 to SV7 (coded bandwidth, mid/side stereo).
- **Wave 2:** the further tallies of MPEG and AAC, DTS, WMA standard and Voice, WavPack.
- **Later:** what needs the entropy pass (Layer III's big_values region end and intensity stereo bound, DTS transient modes, Musepack SV8), WMA Pro's subframes and transforms, AC-3's exponent strategies and per-channel block switching.

## The model

### Options considered

- **Generalize `Series`** with a kind and integer values. JSON's `series` entries would then hold values that are no numbers in a unit, which changes the meaning of a field consumers already read: `schemaVersion` 2, and every graph in the GUI would branch on the kind.
- **Nodes per frame** for the categories. They exist already (the side information), and front ends must not read names.
- **A category series beside `Series` (recommended).** `Series` stays as it is; the new type has its own array in JSON and its own charts in the GUI.

### Units, slots and weights

- A **unit** is what the parser records once: a frame, an access unit, a packet, a block. Its x value is its byte offset, as for a per-frame `Series`, and choosing it in a graph selects that node.
- A **slot** is one value a unit carries. A Layer III frame of MPEG-1 has 2 granules × 2 channels = 4 slots of block types; an AC-3 frame 6 slots, one per block; an AAC access unit one slot per channel stream; a single slot is the common case.
- The number of slots is fixed per series: the largest a unit of the stream can have. A slot a unit does not have holds `CategorySeries.None`: the second channel of a mono frame in a stream that changes between mono and stereo (an MPEG stream does not end at a channel mode change), the blocks of an E-AC-3 frame of fewer blocks, a block the parser could not read.
- Every value of every slot counts once: block usage counts granules per channel, as mp3guessenc and EncSpot count them and as the tally does, and window sequences count channel streams.
- A **weight** says how many of the tally's things a value stands for, where that is not one: an Opus stream packet stands for its frames. Weights are optional and cost nothing where absent.

### Time

- A category series may carry a `TimeIndex`: the start of each interval of audio in seconds and the first unit that starts in it. It is the same partition the parser's per-second series use ("the frames that start in that second"), and its starts are shared with the "Bitrate Over Time" series' x values.
- AAC's per-second series merges neighboring seconds past 131,072 entries; the index follows the same intervals.
- A stream without per-second series (Vorbis whose long blocks last longer than a second, from a corrupt sample rate) has no index, and its graph shows frames only.

### Types in Core.Abstractions

All in `Encspect.Core`.

```csharp
// One category a series' values take, such as a Layer III block type. Name is written as the tally entry for it is named
// ("Mid/side stereo", "Short"), Description as a node's (constant, citing the specification), Topic as a node's.
public sealed record Category(string Name, string Description)
{
    public string? Topic { get; init; }
}

// The intervals of audio time a parser counts its per-second series over: the start of each in seconds, shared with
// that series' x values, and the index of the first unit that starts in it. Starts and FirstUnits have the same length.
public sealed record TimeIndex(ReadOnlyMemory<double> Starts, ReadOnlyMemory<int> FirstUnits);

// One category per unit (frame, access unit, packet, block) and slot (granule and channel, block, channel stream) over a
// stream, for graphs and for the summary over the stream. Immutable once built, so the writers and the GUI read it on
// any thread; built by CategorySeriesBuilder.
public sealed class CategorySeries
{
    // The code of a slot without a value.
    public const byte None = byte.MaxValue;

    // At most 254 categories (codes 0 to 253) and 64 slots.
    public const int MaxCategories = 254;

    public const int MaxSlots = 64;

    public string Name { get; }

    // In the order the parser gives; index 0 is the plain coding (long blocks, left/right stereo), which the GUI draws
    // in the first color of its palette.
    public IReadOnlyList<Category> Categories { get; }

    public int Slots { get; }

    // What each slot is, such as "granule 1, channel 2"; empty where Slots is 1.
    public IReadOnlyList<string> SlotNames { get; }

    // The byte offset of each unit, the same memory as the stream's per-frame Series where they share units.
    public ReadOnlyMemory<double> X { get; }

    public int Count { get; }

    // The code of one slot of one unit: an index into Categories, or None.
    public byte this[int unit, int slot] { get; }

    // What the value stands for in the counts: 1 unless the parser gave weights.
    public int WeightAt(int unit, int slot);

    // Per category, the weighted values over the whole series, and their sum: computed once when built.
    public IReadOnlyList<long> Counts { get; }

    public long Total { get; }

    // Adds the weighted values of the units first to end - 1 to counts, one entry per category: the per-second shares,
    // the GUI's pixel columns and the summary of a range all use it.
    public void CountRange(int first, int end, Span<long> counts);

    public TimeIndex? Time { get; }

    // The tally node whose entries are these categories; the text tree prints the per-second shares after it.
    public Node? Anchor { get; }

    public string? Description { get; init; }

    public string? Topic { get; init; }
}

// Records a category series while a parser reads its units, in chunks, so nothing is copied when it is built and nothing
// is kept per unit beyond the codes (and the weights, once a weight other than 1 is added).
public sealed class CategorySeriesBuilder
{
    public CategorySeriesBuilder(string name, IReadOnlyList<Category> categories, int slots = 1, IReadOnlyList<string>? slotNames = null);

    public int Count { get; }

    // One unit of one slot.
    public void Add(byte code, byte weight = 1);

    // One unit; codes.Length is Slots, weights empty or of the same length.
    public void Add(ReadOnlySpan<byte> codes, ReadOnlySpan<byte> weights = default);

    // x must hold Count values. Throws ArgumentException on a code past the categories, a mismatched x or a TimeIndex
    // whose first units do not rise within 0 to Count: a parser bug, which the host reports as parse.failed.
    public CategorySeries Build(ReadOnlyMemory<double> x, Node? anchor, string description, string? topic = null, TimeIndex? time = null);
}
```

`StreamInfo` gets one property, after `Series`:

```csharp
public sealed record StreamInfo
{
    // Categories per frame for graphs and for the summary over the stream, such as the Layer III block types.
    public IReadOnlyList<CategorySeries> CategorySeries { get; init; } = [];
}
```

- The storage is the builder's chunks (`ChunkedList<byte>`, which `Core.Abstractions` already has), handed to the series without a copy.
- No per-call state: a builder belongs to one stream of one analysis, as a tally does.
- The parser stores no other per-frame state for a series: codes it can derive from its records at the end (Opus's TOC, Vorbis's packet flags, MPEG's header) are added to the builder in the pass that builds the other series, and codes it cannot (Layer III block types, AC-3's block flags) are added while it reads the frame.

### The summary over the stream

- **Reported by the parser, as now.** The tally nodes under Statistics are the summary in the tree: "Block Usage: 496 granules", "Short: 16 granules (3.2%)". They keep their names, values and scope.
- **The same categories on the series.** Each category is named as its tally entry is, and the series is anchored to the tally node. A format whose tally lacks the split a series makes gets the tally node with it (AAC's stereo coding of channel pairs, below), so no figure exists only on the series.
- **Counts derived once, by the shared type.** `CategorySeries.Counts` is computed when the series is built. JSON writes it beside each category, and the GUI's summary reads it, never a node.
- **Held equal by a test.** `CategorySummaryTests` walks every fixture and compares each series' counts with the leading count of its anchor's entries, where the series covers exactly the anchor's units. That is every stream except an MPEG stream that a tag splits into several regions or a region that holds several streams: there the tally covers a region and the series a stream, as the bitrate distribution and the "Bitrate Over Time" series already differ. The JSON's counts then cover the stream and the tree's the region, and each says what it covers.
- **Considered:** the host derives the summary nodes from the series, and the parsers drop their tally code. That changes every golden's Statistics node, loses the region scope, and puts nodes of the host into a parser's tree.
- **One figure, one name:** a category has one name in the tree, the series, JSON and the GUI. What the series adds over the tally is time, not new figures.

### Names across formats

- The stereo codings of MPEG Layer III and AAC channel pairs are the same four: "Left/right stereo", "Mid/side stereo", "Intensity stereo", "Mid/side and intensity stereo", as `JointStereoModes` names them. AAC adds "Separate windows" for pairs without a common window.
- Block types keep their format's names: Layer III's "Long", "Start", "Short", "Mixed", "Stop", "Reserved"; AAC's "Only Long", "Long Start", "Eight Short", "Long Stop"; AC-3's blocks with and without block switching; Vorbis's long and short blocks. They are different mechanisms, and the articles say how they correspond.
- "Coded Bandwidth" is the one name for the highest frequency a frame's coded data reaches, from the syntax. It is not the lowpass:
  - The LAME tag's "Lowpass" is the encoder's setting.
  - A lowpass measured from decoded audio would be a figure of the host-side PCM analyses of the decoder design.
  - The articles keep the three apart.
- AAC's statistics node "Bandwidth" ("highest coded band ends at about 16,000 Hz (max_sfb 49)") is the same concept, so it becomes "Coded Bandwidth" with the AAC rollout.

### Coded bandwidth

- A numeric `Series` "Coded Bandwidth", unit "Hz", per frame, at the frame offsets the bitrate series uses. `Series` does not change.
- Per frame, the highest over the frame's channels and blocks: a graph of the stream's reach, which a lowpass shapes. Per-channel values are left for later.
- A Statistics node "Coded Bandwidth" with the lowest, most common and highest value, and a per-second series "Coded Bandwidth Over Time" (each second's highest), anchored to it, so the text tree shows it as it shows "Bitrate Over Time".
- Each format's description says what its value is and what it leaves out (SBR and spectral extension above, noise substitution, intensity stereo and joint intensity coding).

### Memory budget

Kept per frame, against the bounds `MemoryTests` holds now:

| Stream | Added per unit | Kept today, measured or stated | Bound in `MemoryTests` (kept, peak) |
| --- | --- | --- | --- |
| MPEG-1 Layer III stereo | 6 bytes: block usage 4, joint stereo mode 1, channel mode 1 | about 130 bytes (plan, "Large inputs") | 250, 350 per frame |
| MPEG Layer II stereo | 10 bytes: channel mode 1, intensity stereo bound 1, coded bandwidth 8 | as above | 250, 350 |
| AAC stereo (ADTS) | 11 bytes: window sequences 2, stereo coding 1, coded bandwidth 8 | 91 to 116 bytes per access unit (plan) | 220, 250 per frame (ADTS); 110, 180 per sample (MP4) |
| AC-3 2/0, 6 blocks | 26 bytes: 3 block series × 6 slots, coded bandwidth 8 | 70 bytes per mono frame (plan) | 220, 300 |
| Smallest E-AC-3 frames (1 block) | at most 12 bytes | 46 bytes (plan) | 90, 180 |
| Opus | 4 bytes and 3 weight bytes | 40 bytes per packet (plan) | about twice that |
| Speex | 1 or 2 bytes per frame | 30 bytes per packet (plan) | 80, 110 per packet |
| Vorbis | 2 bytes per packet | 26 bytes per packet (plan) | about twice that |
| Musepack SV7 | 9 bytes per frame | 42 bytes, peak 59 (`MemoryTests`) | about twice that |

- Every addition fits the bounds as they are: no bound is raised. Each format's pull request adds a check that the report keeps at most `Slots` bytes per unit per series (weights included) plus chunk overhead, from `CategorySeries`' own size, so a regression that keeps more per unit fails there and not only in the total.
- The peak grows by the codes only: the numeric series are built at the end as now, and the codes are not copied when built.
- A crafted file cannot make a series grow faster than the records it already keeps: a slot is recorded only for a field the parser read from the file (a block type, a block switch flag, a TOC byte), units stop where the parser's record limits stop the graphs (`aac.frames-limit`, `wma.records-limit`, Matroska's frame budget), and `MaxSlots` bounds a unit. An AAC access unit has at most 48 channel streams counted (`CodingTally.MaxGainChannels`); an Opus multistream packet past 64 stream packets is graphed in its first 64, which only the ambisonics families (2 and 3) and family 255 can reach; the series then leaves the others' slots out, as series stop at the record limits.

## Output

### JSON

Each stream gets `categorySeries` after `series`:

```json
"categorySeries": [
  { "name": "Block Usage",
    "slots": ["granule 1, channel 1", "granule 1, channel 2", "granule 2, channel 1", "granule 2, channel 2"],
    "categories": [ { "name": "Long", "count": 480 }, { "name": "Start", "count": 4 }, { "name": "Short", "count": 8 },
                    { "name": "Mixed", "count": 0 }, { "name": "Stop", "count": 4 }, { "name": "Reserved", "count": 0 } ],
    "total": 496,
    "xSeries": "Bitrate",
    "runs": [[0, 300], [1, 4], [2, 8], [4, 4], [0, 180]],
    "seconds": [0, 39, 78] } ]
```

- `categories`: every category of the series, with `count` (weighted); with `--explain` also `description` and `topic`, and the series' own.
- `slots`: present where a unit has more than one value.
- `xSeries` names the per-frame series of the stream whose `x` the units share; where they share none, `x` holds the offsets as a series' does.
- `runs`: the codes as runs of `[code, length]`, slot by slot within each unit and unit by unit, `null` for a slot without a value. Runs keep a long stream's JSON short (a CBR file of long blocks is one run) and a golden diff readable.
- `weights`: runs of weights, present only where a weight is not 1.
- `seconds`: the first unit of each interval of `TimeIndex`, whose starts are the `x` of the stream's "Bitrate Over Time"; absent without an index.
- **Schema version:** a field is added and none changes meaning, so `schemaVersion` stays 1, as the plan's rule and the decoder design's `verification` do. A consumer that reads `series` sees the new numeric series ("Coded Bandwidth") as further entries of the same shape.

### Text

- After a series' anchor, one line per category that occurs, with its share of each interval in percent, rounded half to even to 0.1, as the tallies round:

```
        Block Usage: 496 granules
          Long: 480 granules (96.8%)
          Start: 4 granules (0.8%)
          Short: 8 granules (1.6%)
          Stop: 4 granules (0.8%)
          Short per second: 0,3.2,1.6
          Start per second: 0,1.6,0.8
          Stop per second: 0,1.6,0.8
          Long per second: 100,93.6,96.8
```

- The plain category comes last, since it is the remainder. A series without a time index prints no lines; its summary is the tally above.
- With `--offsets`, the anchor's location, as anchored series have now. With `--hide statistics`, the anchor is hidden and the lines with it. With `--explain`, each line's category description.
- The per-frame codes are not printed in the text tree, as per-frame numeric series are not.

### CSV

- No change in this rollout. Shares of block types or stereo codings are format-specific, and a column per share would be empty for most files.
- Leaving it alone also leaves `ReportSummary` and the GUI's summary cache format alone, which the decoder contract bumps to 6.
- Considered: `Short Block Share` and `Mid/Side Share` for the first audio stream, from its first matching series. A later step, if folder exports need them.

### Golden reports

The contract changes no golden: no parser produces a series yet. Each format's pull request then changes the goldens of the fixtures whose streams get series:

- MPEG: the fixtures in `mpeg`, `layer2`, `layer3`, `vbr`, `stats`, the tag fixtures that hold MPEG audio, and the MP4 and WAV payloads the MPEG codec parser reads. The pull request counts them with a script and names the count.
- AAC: `aac`, `adts`, and the `mp4` and `matroska` fixtures with AAC tracks; AC-3: `ac3` and its MP4 and Matroska tracks; and so on.

How the diffs stay reviewable:

- **Run-length codes,** above: a golden gains a few dozen lines per series, not one per frame.
- **Two commits per format:** the code with its tests, then the regenerated goldens (`ENCSPECT_UPDATE_GOLDEN=1`) alone.
- **A check of the golden commit:** `scripts/ci/golden_additions.py <old> <new>` removes the new keys (`categorySeries`, the "Coded Bandwidth" series and nodes, a format's new tally node) from each new golden and requires the result to equal the old golden byte for byte. A reviewer then reads only what was added, and an unintended change anywhere else fails the check. The renamed AAC node is listed in the script's rename table for that one pull request.
- `golden_hides` (development.md) does not hide category series: they are small, and the golden is where a per-frame change shows.

## GUI

### Charts

The Graphs tab offers each category series as a chip, as it offers each `Series` now, with three views:

- **Over time:** a 100% stacked column per interval of the time index, the categories stacked in their order with the plain category at the bottom. Where intervals outnumber pixel columns, neighboring intervals merge into one column, weighted by their values, as a line of more points than pixels draws each column's smallest and largest value now.
- **Frames:** a strip with one row per slot (four rows for Layer III: granule 1 and 2 of each channel), each unit a cell in its category's color. Where units outnumber pixel columns, a column shows the shares of its units as a stacked cell, so a rare short block still shows as a sliver rather than vanishing under the most common category.
- **Summary:** a table like EncSpot's: each category's name, count and share, with a horizontal bar. A "Coding Summary" chip shows the tables of every category series of the stream at once (for MPEG: channel modes, joint stereo modes, block usage), which is EncSpot's panel.

"Coded Bandwidth" is a `Series` and draws as a line, with no change to the line graph.

### Interaction

- **Hovering:** a column shows its interval and every share, "0:12 to 0:13 · Short 12.5% (24 granules) · Long 87.5% (168 granules)"; a strip cell its unit, "Frame 1,234 · 0x1A2B · granule 2, channel 1: Short"; a summary bar its count and share.
- **Clicking** a column selects the first unit of its interval, from `TimeIndex.FirstUnits`, exactly rather than estimated from the duration as per-second points are now. A strip cell selects its unit. A summary bar, or a legend entry, selects the next unit after the selected one that has its category, so choosing it again steps through them, as a bar of the bitrate distribution does.
- **Selecting** a frame in the tree marks it: a vertical line across the over-time and strip views, in the foreground color.
- **Keyboard:** the chips and views are reached with Tab; Left and Right move a cursor from unit to unit in the strip and from interval to interval over time, Enter selects. The existing line graph gets the same keys.

### Palette

- A fixed categorical palette, not the system accent, so a category has the same color in every file and in both modes. Index 0, the plain coding, takes the first color.
- The default palette of the data visualization method this design follows, its first six slots: blue, orange, aqua, yellow, magenta, green. Light: `#2a78d6`, `#eb6834`, `#1baf7a`, `#eda100`, `#e87ba4`, `#008300`. Dark: `#3987e5`, `#d95926`, `#199e70`, `#c98500`, `#d55181`, `#008300`.
- Its validator, run for this design, passes both modes: worst adjacent separation for color vision deficiencies ΔE 9.1 (light) and 8.4 (dark), normal vision 19.6 and 19.3. In light mode aqua, yellow and magenta fall below 3:1 against the surface, which obliges visible labels or a table view; the summary table and the legend are that.
- The validator measured against its own surfaces (`#fcfcfb`, `#1a1a19`). Whether the result holds against Fluent's `SystemRegionBrush` in both modes is **unverified** until the implementation runs the validator with the theme's colors and records them in a test.
- A "Reserved" or other invalid category is drawn in gray with a hatch, never in a hue of the palette, since it marks a value the specification does not allow.
- A slot without a value is left empty, the surface showing through.
- Segments are separated by a 1-pixel gap in the surface color, so neighbors of similar lightness stay apart.

### Accessibility

- Never color alone: a legend with names and shares is always shown; the summary is a table with text; every hover says the category by name; series of up to four categories label their segments directly where they are wide enough.
- A hatch is available per category for high contrast settings, where the system's contrast theme is on.
- The views are reachable and usable by keyboard (above), and the chips and legend entries have automation names.

### Speed

- A view aggregates once per width and per series, in one pass over the codes (`CountRange` per pixel column), and draws at most columns × categories rectangles. For 100,000 frames of 4 slots, that is 400,000 bytes read per resize.
- No timing is measured yet. The test below bounds it with the line graph's existing test as the reference.

### Tests

- `GraphTests`: aggregation per column against a brute-force count, weights, `None` slots, intervals merged into columns, the frame each click selects, the next unit of a category after the selected one.
- `GraphViewTests`: a series of 200,000 units in 4 slots draws and answers the pointer within the bound the 200,000-point line test uses.
- `ScreenshotTests`, light and dark: the Graphs tab of a LAME VBR fixture with short blocks on each of the three views, and of an AAC fixture's window sequences; saved with `ENCSPECT_SCREENSHOTS` for review, as the existing screenshots are.
- A pixel test that needs no image comparison: a scripted series of known categories, rendered headless, sampled at fixed columns, whose colors must be the palette's.
- `DetailViewModelTests`: the Coding Summary lists the categories and counts the series carry, read from the stream and not the tree.

## Descriptions and articles

### Descriptions

- Every `CategorySeries` has a `Description` saying what one value stands for and what the series counts ("The block type of each granule and channel of every complete audio frame, counted per channel as the Block Usage statistics count them (ISO/IEC 11172-3 2.4.2.7)").
- Every `Category` has one, written once as a constant by the parser that knows the field, citing the section: Layer III's "Short" is "block_type 2 without mixed_block_flag: three short transforms in every subband (ISO/IEC 11172-3 2.4.2.7)".
- The tally entry and its category share the constant, so the tree and the graph say the same.
- Every "Coded Bandwidth" series says how its format derives the value and what it leaves out.
- `DescriptionTests` gains the walk over every fixture's category series: a series or category without a description, or with a topic that does not exist, fails.

### Articles

Existing articles, extended with one paragraph each, where they say what you see:

- `block-switching`: the Block Usage graph and summary, and how the other formats' block mechanisms correspond (AAC's window sequences, AC-3's block switching, Vorbis's two block sizes, WMA's block sizes), each linking its own article.
- `joint-stereo`: the graph of joint stereo modes over time, and EncSpot's counting of every frame that is not joint stereo as mode 0, so its plain stereo share differs from the report's, by how much the channel modes say.
- `lowpass`: that coded bandwidth, from the syntax, shows where the coded spectrum ends without decoding, and why it is not the lowpass.
- The format articles as their formats land: `aac-windows-and-blocks`, `ac3-audio-blocks`, `vorbis-blocks`, `opus-toc`, `speex-modes`, `musepack-subband-coding`, `wma-frames-and-blocks`.

A new shared article, `coded-bandwidth`:

- The problem: encoders spend no bits on frequencies they filtered out or chose not to code, and the coded data says where they stopped.
- How each format states it: Layers I and II's bit allocation, AAC's max_sfb, AC-3's chbwcod and coupling range, Musepack's used subbands, DTS's active subbands, WMA's last coefficient, Opus's bandwidth in the TOC.
- What it does not show: SBR and spectral extension above, noise substitution, intensity stereo and joint intensity coding within, quiet content that the coded data still reaches.
- How it relates to the LAME tag's lowpass and to a lowpass measured from decoded audio.
- References: the sections in the survey above. About 300 words.

Observations of how often an encoder switches blocks or codes mid/side go into articles only with the fixture backing `CLAUDE.md` asks for, and none is planned here.

## Verification

### Per-frame cross-checks

The tools CI already builds or installs give per-frame references for most series:

| Format | Per frame or block | Per stream |
| --- | --- | --- |
| MPEG Layer III | minimp3's side information per granule and channel (`minimp3_side_info.c`): block_type and mixed_block_flag | mp3guessenc's block usage and stereo mode counts; EncSpot's mode and block counts, through an emulation of its counting (above) |
| MPEG Layer II | minimp3's allocation per subband and channel (same tool): the highest allocated subband | mp3guessenc |
| AAC | faad2's trace (`faad_trace_tool.py`): window_sequence, max_sfb, ms_mask_present, ms_used, every band's codebook | the tally |
| AC-3 | none in CI: `a52_blocks.c` prints where each block ends, not its fields. Whether liba52 0.7.4's `a52.h` exposes blksw, cplinu, rematflg and the end mantissa per channel to extend it is **unverified**; otherwise the hand-built fixtures of `ac3_model.py` carry the expected values in their manifests | the tally |
| Musepack SV4 to SV7 | libmpcdec's used subbands and mid/side mask per frame (`mpcdec_frames.c`) | the tally |
| Opus, Vorbis, Speex | the independent model `ogg_model.py` for the TOC and the packet flags; speexdec's per-frame bitrates, which the submodes give | opusinfo, the tally |
| WavPack | libwavpack's block headers (`wavpack_blocks.c`) | the tally |
| WMA, DTS | the hand-built fixtures' models (`asf_model.py`, `dts_model.py`); `dca_frames.c` prints no audio coding header fields | the tally |

- The cross-check scripts compare unit by unit, so a series that is right in sum but wrong in order fails.
- ffprobe and libavcodec's debug output have no per-frame block type or stereo mode for these codecs that the scripts read today; this design does not add one.

### Fixtures

- Existing encoder fixtures carry most categories: LAME's VBR files switch blocks; FFmpeg's and FAAC's AAC switch windows; the AC-3 encoders switch blocks and rematrix.
- Hand-built ones, from the existing models, for what encoders rarely write:
  - Layer III intensity stereo and both tools together; mixed and reserved blocks;
  - a stream that changes between mono and joint stereo, so slots are empty;
  - MPEG-2's one granule; a truncated last frame and a VBR header frame, which no series counts;
  - Layer II intensity stereo bounds;
  - E-AC-3 frames of 1, 2 and 3 blocks; spectral extension;
  - AAC pairs without a common window;
  - Opus packets of several frames and a multistream file.
- Each manifest lists the expected counts per category, which `CategorySummaryTests` holds against both the series and the tally.

### Tests

- **Contract:** `CategorySeriesTests`: building, `None`, weights, `CountRange`, a `TimeIndex` that does not fit (an exception, so the host's `parse.failed`), the 254-category and 64-slot limits; the JSON runs written and read back; the text lines' rounding.
- **Summary:** `CategorySummaryTests` (above), on every fixture.
- **Descriptions:** `DescriptionTests`, extended.
- **Memory:** the existing `MemoryTests` bounds hold, and each format adds its per-series check (above).
- **Complexity:** `ComplexityTests` with the largest slot counts per byte a crafted file can reach (AAC access units of 48 single channel elements, E-AC-3 frames of one block in 5 channels, Opus multistream packets) stays linear in the file's length.
- **Threads:** `ParallelAnalysisMatchTests` covers the series through the JSON it compares.
- **Decoding:** with decoding on, the category series are the same, as the decoder design holds for the tree.

## Fitting alongside the decoder contract

The decoder contract, being built on `feature/decoder-contract`, adds to `StreamInfo` the property `Verification` after `Protection`, writes `verification` in JSON before `encoder`, adds a verification line to the text tree under each stream, `Is Verified` to the CSV and `IsVerified` to `ReportSummary` (summary cache format 6), and changes several lines of `CLAUDE.md`. This design touches different places, so the two merge without conflict:

- `StreamInfo`: `CategorySeries` after `Series`, the last property.
- JSON: `categorySeries` after `series`, the last field of a stream.
- Text: lines after a series' anchor node, in the tree's node writer, not in the stream lines.
- CSV and `ReportSummary`: untouched, so the summary cache stays at the decoder's 6.
- `CLAUDE.md`: one sentence the decoder contract does not touch, in "Architecture rules": "Format parsers report their streams as `StreamInfo` with series for graphs" becomes "Format parsers report their streams as `StreamInfo` with numeric series and category series for graphs, whose categories carry the names of the tally entries they count."
- **The decoder design's host-side PCM analyses** add figures from decoded audio to the stream, never nodes, through one field the first such analysis adds. Category series are the parsers', from the coded syntax, and decoding never changes them. A lowpass those analyses measure is a figure of its own, named "lowpass" as the article does; "Coded Bandwidth" stays the syntax's. Should a PCM analysis report a per-second series, it uses `Series` and `TimeIndex` as they are.

## Rollout

1. **Contract**, one pull request, after the decoder contract merges:
   - `Category`, `TimeIndex`, `CategorySeries`, `CategorySeriesBuilder` and `StreamInfo.CategorySeries`;
   - the JSON and text writers; `scripts/ci/golden_additions.py`;
   - the GUI's three views, palette, Coding Summary and keyboard handling, tested with scripted series (`TestParsers.cs`);
   - `CategorySeriesTests`, the `DescriptionTests` extension, `CategorySummaryTests` (empty until a parser produces series);
   - documentation: `docs/cli.md` (JSON and text), `docs/gui.md` ("Graphs"), `docs/formats/README.md` ("Series"), the `CLAUDE.md` sentence above.
   - No parser changes, no golden changes.
2. **MPEG, the reference:**
   - channel modes, joint stereo modes, intensity stereo bounds, block usage, coded bandwidth of Layers I and II, with the "Coded Bandwidth" statistics node;
   - minimp3, mp3guessenc and EncSpot cross-checks; the hand-built fixtures; goldens in their own commit;
   - the articles `coded-bandwidth`, `block-switching`, `joint-stereo`, `lowpass`; `docs/formats/mpeg-audio.md`.
   - A milestone review of the contract and MPEG follows, on Opus with high effort; its findings are fixed before step 3.
3. **Wave 1, in parallel, a pull request each:** AAC (with the "Stereo Coding" tally node of channel pairs and the rename to "Coded Bandwidth"), AC-3 and E-AC-3, Vorbis, Opus, Speex, Musepack. Each with its cross-check, fixtures, golden commit, format page and article paragraph.
4. **Wave 2,** the same way: the further MPEG and AAC tallies, DTS, WMA standard and Voice, WavPack.
5. **Later:** the series that wait for the entropy pass, WMA Pro, a CSV column if wanted.

What is serial:

- Everything in `Core.Abstractions`, the host's writers and the GUI is in step 1 alone. A format that finds the contract lacking changes it in a small pull request of its own, merged before the format's, never inside it.
- The shared articles (`coded-bandwidth`, `block-switching`, `mid-side-stereo`) get one paragraph or line from each format pull request: small conflicts of text, merged in order.
- `CategorySummaryTests`, `DescriptionTests` and the memory tests are generic and need no change per format beyond fixtures and the per-series check.

## Decisions

1. **Model:** a `CategorySeries` beside `Series`, one byte per value, slots for granules, channels and blocks, optional weights, its own JSON array; `schemaVersion` stays 1. Alternative: one `Series` with a kind, which changes `series` for consumers (version 2).
2. **Summary:** the parsers' tally nodes stay the summary in the tree; each series uses the same category names, is anchored to its tally node, and its counts (written in JSON, read by the GUI) are held equal to the tally by a test. Alternative: the host builds summary nodes from the series and the parsers drop their tallies.
3. **Memory:** parsers record codes while reading, at most one byte per value and nothing else per frame (6 bytes for an MPEG-1 stereo Layer III frame); no `MemoryTests` bound is raised. Alternative: derive the codes at the end from the kept side information, a second pass with allocations per frame.
4. **Coded bandwidth:** a numeric "Coded Bandwidth" series in Hz where the syntax states it (Layers I and II, AAC, AC-3, Musepack, then DTS and WMA); Opus keeps its signaled bandwidth as categories; Layer III's big_values waits for the entropy pass, since without count1 it bounds nothing. Alternative: graph big_values now as "Big Values Region End".
5. **Output:** JSON writes categories with counts, slot names, the per-second index and run-length codes; the text tree prints each category's share per second after its tally node; the CSV stays as it is. Alternative: CSV columns for the mid/side and short block shares, which bump the summary cache again.
6. **GUI:** per series an over-time view, a per-frame strip with one row per slot and an EncSpot-like summary table, plus a Coding Summary of all series; a fixed six-color palette validated for color vision deficiencies in both modes, with names and shares always in text. Alternative: the summary and over-time views only, without the strip.
7. **Rollout:** contract after the decoder contract, MPEG as the reference with a milestone review, then AAC, AC-3, Vorbis, Opus, Speex and Musepack in parallel; DTS, WMA and WavPack in a second wave. Alternative: every format in the first wave.
8. **Goldens:** regenerated in a commit of their own per format, checked by a script that removing the additions gives the old golden byte for byte. Alternative: hide category series from the goldens and rely on manifests.

## Open risks and unverified claims

- **Unverified:** whether liba52 0.7.4 exposes AC-3's per-block fields to a cross-check tool; without it AC-3's series rest on the hand-built fixtures and the tally.
- **Unverified:** whether decoders apply AAC intensity stereo in a channel pair without a common window; the series follows the tally's rule and gives such pairs a category of their own.
- **Unverified:** the palette against Fluent's actual surfaces in light and dark; the validator ran against the method's reference surfaces.
- **Not measured:** the GUI's drawing time at 100,000 frames and the parsers' added time; the estimates above are reasoning, not runs.
- **Scope of MPEG summaries:** a stream split by an embedded tag has its series over the stream and its tally per region, so the JSON counts and the tree's differ there, as the bitrate figures already do. The equality test skips those streams.
- **Run-length codes** make goldens short for steady streams; a stream that switches every frame (block types of some encoders) gives about one run per frame and slot, which the golden commit's size will show per format.
- **The CI report** (`scripts/ci_report.py`) did not answer within a minute in this session, so the state of main and the nightly runs was not checked for this design.
