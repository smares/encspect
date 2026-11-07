# Lossy decoders: what decoding the lossy formats adds to the contract

Status: draft of 2026-11-07, for an independent review and the project lead's sign-off; nothing of it is built. It designs the "Later" item of the [plan](../PLAN.md) on lossy decoders, which the measurements of decoded audio ([spectral-analysis.md](spectral-analysis.md)) and gap wave 3 wait for. The decisions for the lead are under [Decisions for the project lead](#decisions-for-the-project-lead). Claims that no source read for this design confirms are marked **unverified**, figures worked out rather than measured **estimate**.

- The decoder contract of [decoders.md](decoders.md) applies as it is: `IDecoder`, `IFrameDecoder`, the session the parser feeds while it reads, `CodedFrame`, the work budget, the exception boundary, the PCM sink. This text adds only what lossy decoding brings, and says where a part of the contract must change and why.
- Two parts change: the deterministic math (`DetMath`) moves from the host to `Core.Abstractions`, since decoders cannot reference the host, and the determinism rules of `CLAUDE.md` extend from measurement code to the float code of the lossy decoders and of the parser code they share. Everything else is a use of the contract as built.
- The lead's rule of 2026-11-01 (no concealment, `decode.frame-invalid`, positions counted as lost) is applied, and extended to the output that a lost unit's state reaches into (decision 3).

## Scope

- **In:**
  - decoders for MPEG audio Layers I, II and III; AAC (LC, then SBR and parametric stereo, then the other object types the parser reads); AC-3 and E-AC-3; DTS core; Vorbis; Opus (SILK, CELT, hybrid); Speex; Musepack SV4 to SV8; WMA standard, Professional and Voice;
  - the entropy pass each needs in its parser, and the gap wave 3 figures it gives;
  - sources and licenses, output and determinism, verification and the PCM cross-checks, gaps and priming, cost, the order of the pull requests.
- **Out:**
  - decoders of the lossless formats that wait under "Later" (ALAC, TTA, WMA Lossless), and DTS-HD's lossless extension (XLL), which has a lossless coding of its own;
  - DTS extensions beyond the core (XCh, XXCH, X96, XBR, LBR), the MPEG-2 multichannel extension of Layer II, Dolby's object audio (JOC) and AC-4 and MPEG-H: listed as follow-ups or not covered where each format's decoder says so;
  - parallel decoding of lossy frames (see [Parallel decoding](#parallel-decoding));
  - any change to the measurements beyond the trimmed range of decision 7.

## The design in short

- **One decoder per coding, one pull request each,** in the order of [Order](#order): MPEG audio first, then AAC LC, Vorbis, Opus, HE-AAC, AC-3, E-AC-3, Musepack, DTS core, Speex, the WMA codecs and the rarer AAC object types. A groundwork pull request without a format comes first.
- **Sources:** free specifications where they exist (A/52, ETSI TS 102 366 and 102 114, the Vorbis I specification, RFC 6716 with RFC 8251) and permissive reference code (minimp3, libvorbis, libopus, libspeex, libmpcdec, each license read for this design). AAC needs ISO/IEC 14496-3, which is not free (decision 1); WMA needs clean-room descriptions of its reconstruction, which the existing descriptions leave out (decision 11).
- **The entropy pass joins the parsers.** Each lossy parser walks its coded spectral or excitation data in every analysis, with the code that takes an optional destination ("The second entropy pass" in decoders.md). That walk gives the wave 3 figures, and with decoding on it fills the session's lent buffers, so the decoder reads no coded bit itself.
- **Output:** planar `Float32` blocks, full scale ±1.0, unclipped and undithered, at the stream's output rate, untrimmed positions; 16-bit `Integer` for the integer Speex decoder. No contract change.
- **Determinism:** the decoders fall under the same rules as the measurements (no fused multiply-add, `DetMath` instead of the platform's math library, sums and transforms in a fixed order, element-wise `Vector128` only), because their output feeds figures that must be byte-identical on six platforms and in NativeAOT. A hash of each decoder's raw output, pinned per fixture, holds it.
- **Verification:** a lossy stream stores no checksum over decoded audio, so its decoder declares none (as `pcm` does): `--verify` alone does not decode it, `--measure` does, and no lossy stream is ever "verified" by decoding (decision 2). Correctness comes from the conformance streams and test vectors with their reference output, and from PCM cross-checks against the reference decoders within a stated tolerance.
- **Gaps:** a frame that cannot be decoded outputs nothing; the decoder resets, and also withholds the output after it that depends on the lost state (one unit for a filterbank or overlapped transform, 80 ms for Opus), which the measurements count as lost positions too (decision 3).
- **Cost:** each decoder charges its stages in work units, declares its units per byte from its lowest legitimate bitrate (HE-AAC and Opus need more than the default 2,048), brings its cases to `DecoderWorkTests`, and is held to at most 2.5 times the fastest reference decoder's time on the benchmark files.

## Formats and order

### What each decoder covers

| Decoder (codec ID) | Covers | Leaves out, with the reason |
| --- | --- | --- |
| `mpeg-audio` | MPEG-1, MPEG-2 and MPEG-2.5, Layers I, II and III, free format | the MPEG-2 multichannel extension (the base channels, the compatible stereo, are decoded; ISO/IEC 13818-3 is not at hand, see [mpeg-clean-room-rules.md](mpeg-clean-room-rules.md)) |
| `aac` and `aac-adts` | step 1: AAC LC; step 2: SBR (HE-AAC) and parametric stereo (HE-AAC v2); step 3: AAC Main, LTP, ER AAC LC, LD and ELD (with low-delay SBR); step 4: USAC (xHE-AAC) | SSR gain control (FDK refuses it, as the cross-check records), MPEG Surround beyond USAC's 2-1-2, DRC and loudness normalization (decision 6) |
| `ac-3` and `eac3` | step 1: AC-3 (A/52); step 2: E-AC-3 (spectral extension, adaptive hybrid transform, enhanced coupling, reduced-rate blocks, dependent substreams) | dynamic range and dialogue normalization gains, downmix (decision 6); object audio |
| `dts` | the core, at the core rate, in all four bitstream forms | every extension (XCh, XXCH, X96, XBR, LBR, XLL): follow-ups |
| `vorbis` | Vorbis I, floors 0 and 1, residues 0 to 2 | nothing |
| `opus` | SILK, CELT and hybrid, mono, stereo and the multistream mappings of RFC 7845, output at 48 kHz | packet loss concealment and forward error correction (the lead's rule) |
| `speex` | narrowband, wideband and ultra-wideband, in-band stereo | nothing |
| `musepack` | SV7 and SV8; SV4 to SV6 as libmpcdec 1.2.6 decodes them | the SV4 to SV6 streams that libmpcdec 1.2.6 refuses (the parser reads their header only) |
| `wma-standard`, `wma-pro`, `wma-voice` | versions 1 and 2, Professional, Voice | music superframes inside Voice, which FFmpeg does not decode and no description covers |

### Order

Value is how many files a decoder makes measurable and how much the measurements say about them; risk is the source situation and the size of the reconstruction.

| Step | Decoder | Value | Risk | Why here |
| --- | --- | --- | --- | --- |
| 0 | groundwork (no format) | | low | `DetMath` in `Core.Abstractions`, the determinism scan for decoder assemblies, the PCM dump tool and comparison script, the gap rule in the measurer's reasons |
| 1 | MPEG audio (I, II, III) | highest: the most common lossy format | lowest: minimp3 (CC0) is a complete reference, and ISO's conformance streams come with reference PCM | sets the pattern every later decoder follows: walk, lending, output, cross-check |
| 2 | AAC LC | very high | medium: the specification is not free (decision 1) | if the lead does not buy it, AAC moves behind Opus and becomes a clean-room job |
| 3 | Vorbis | high | low: libvorbis (BSD) and a free specification | the Ogg harness exists |
| 4 | Opus | high | medium: large, but the reference is normative and every packet's final range coder state must match exactly | the strongest check any lossy decoder can have |
| 5 | HE-AAC (SBR, parametric stereo) | high | high: two large tools, QMF banks | builds on step 2 |
| 6 | AC-3 | medium | low: A/52 is free and complete | Core Audio is the official reference on the macOS runners |
| 7 | E-AC-3 | medium | medium: five more tools in ETSI TS 102 366 | after AC-3 |
| 8 | Musepack | low to medium | low: libmpcdec (BSD), small | its tables are in the parser already |
| 9 | DTS core | medium | medium: the specification gives pseudo-code and tables, but no free official decoder exists | FFmpeg and libdca stay black boxes |
| 10 | Speex | low | low: libspeex (BSD), small, integer | bit-exact against a fixed-point build |
| 11 to 13 | WMA standard, Professional, Voice | medium, Voice low | high: clean-room descriptions of the reconstruction come first (decision 11) | Microsoft's decoder on the Windows runners is the reference |
| 14 | AAC Main, LTP, ER AAC | low | medium | rare object types |
| 15 | USAC (xHE-AAC) | low today | highest: a second codec (arithmetic coding, LPD with ACELP and TCX, eSBR, MPEG Surround 2-1-2) | last |

## Sources and licenses

### Per format

Each license was read from its own text for this design (2026-11-07), as noted; this is a reading for the project, not legal advice.

| Format | Specification | Permissive reference code | License read | Path |
| --- | --- | --- | --- | --- |
| MPEG audio | ISO/IEC 11172-3 and 13818-3, not free and not at hand (mpeg-clean-room-rules.md) | minimp3 at `ea99364f`, vendored in `third_party/minimp3` (Layers I to III, float) | CC0 1.0 (`third_party/minimp3/LICENSE`) | follow minimp3 closely; the Layer III Huffman tables (Annex B) and the band tables come from it as format data |
| AAC | ISO/IEC 14496-3 (and 23003-3 for USAC), not free | none: faad2 is GPL (Debian's copyright: GPL-2+), FFmpeg LGPL, FDK AAC has Fraunhofer's own license, Apple publishes no AAC decoder, the ISO/IEC 14496-5 reference software's license was never read (official-decoder-crosschecks.md) | FDK's text: [official-decoder-crosschecks.md](official-decoder-crosschecks.md), "FDK AAC"; faad2 from Ubuntu's `libfaad2` copyright file | decision 1: buy the specification, else a clean-room description from FFmpeg's decoder |
| AC-3, E-AC-3 | ATSC A/52:2018 and ETSI TS 102 366, both free (the scratchpad holds `A52-2018.pdf` and `ts_102366v010401p.pdf`) | none needed; liba52 is GPL (Ubuntu's copyright: GPL-2+) and is not read | | the specifications alone |
| DTS core | ETSI TS 102 114 V1.6.1, free: the core's syntax and decoding in pseudo-code, Huffman and VQ tables and the 512-tap QMF coefficients in Annex D | none needed; libdca is GPL-2+ with LGPL parts, FFmpeg LGPL; neither is read | | the specification alone |
| Vorbis | Vorbis I specification, free | libvorbis 1.3.7 | BSD-3-Clause (Ubuntu's `libvorbis0a` copyright file) | specification first, libvorbis for behavior the specification leaves open |
| Opus | RFC 6716, whose normative part is the reference decoder in its Appendix A, updated by RFC 8251 | the RFC's code; libopus 1.5.2 | the RFC's code under the Simplified BSD License (RFC 6716, Copyright Notice); libopus 1.5.2's `COPYING` (BSD-3-Clause, read from the release tarball, SHA-256 `65c1d2f7…`) | follow the RFC's decoder as updated by RFC 8251; libopus 1.5.2 where the RFC says its repository is current |
| Speex | none normative; the Speex manual describes the codec | libspeex 1.2.1, already pinned by `speex_tool.py` and in `THIRD-PARTY-NOTICES.md` for the parser | BSD-3-Clause (`COPYING`); only `src/getopt*` is LGPL, and it is the command-line tool's | follow libspeex's fixed-point decoder (decision 10) |
| Musepack | no specification of the decoding (the SV8 container is specified on musepack.net) | libmpcdec r495 (SV7, SV8) and 1.2.6 (SV4 to SV7), the archives the project pins | `libmpcdec/*` BSD-3-Clause (`COPYING` and each file's header); **but** `include/mpc/minimax.h`, `mpcmath.h`, `datatypes.h` and `common/fastmath.c` are LGPL 2.1 (Ubuntu's `libmpcdec6` copyright file, and `mpcmath.h`'s own header) | follow `libmpcdec/*`; the LGPL headers are not read or ported. `mpc_decoder.c` includes `minimax.h`, which holds only minimum, maximum and clip macros, so nothing of it is needed |
| WMA | none published | none: FFmpeg's decoders are the only detailed source (LGPL) | | clean-room, decision 11 |

### The clean room

- **WMA:** [wma-standard-format.md](wma-standard-format.md), [wma-pro-format.md](wma-pro-format.md) and [wma-voice-format.md](wma-voice-format.md) describe the syntax down to the end of the coefficients and say so: "No audio is rebuilt, so the dequantization, the noise generator, the line spectral pair curve, the transform and the windows are left out". A decoder needs those. So each WMA decoder starts with a supplement to its description, written by an agent that reads FFmpeg 6.1.1's `wmadec.c`, `wma.c`, `wmaprodec.c` or `wmavoice.c` and includes no code, and is implemented by an agent that has read none of them, as for TrueHD. The numeric tables it needs (the Voice LSP, gain and codebook tables of `wmavoice_data.h`, the windows if FFmpeg tabulates them) are format data generated the way `wma_tables.py` generates the others ("Where the format tables come from" in [development.md](../development.md)).
- **AAC without the specification:** the same split over FFmpeg's `aacdec_template.c`, `aacsbr_template.c`, `aacps.c` and their tables; the largest description the project would have written (decision 1).
- **Nowhere else:** the specifications and permissive sources above cover every other format. FFmpeg, faad2, liba52 and libdca are run as black boxes only.

### Notices

- CLAUDE.md asks for a notice where code follows a BSD, MIT or Apache implementation closely. Each decoder that follows one carries a short notice in the derived files and its row in `THIRD-PARTY-NOTICES.md` and `scripts/release/licenses`:
  - libvorbis (BSD-3-Clause), the RFC 6716 code (Simplified BSD) and libopus (BSD-3-Clause), libspeex (the existing row, extended to the decoder), libmpcdec (the existing row, extended to the requantization and synthesis);
  - minimp3 is CC0, which asks for nothing; it gets a row and file notices all the same, as the CC0 "magma" colormap has one, so that the source of the code is on record.
- Code written from a specification (A/52, TS 102 366, TS 102 114, ISO/IEC 14496-3) carries no notice; its tables are specification data and `development.md` names each table's source and check, as CLAUDE.md asks.

## The parser and decoder split

### The entropy pass joins the parsers

CLAUDE.md: "A decoder reads the coded syntax with its parser's code and adds only reconstruction". Today the lossy parsers stop before most of their entropy-coded data:

| Parser | Reads today | Stops before |
| --- | --- | --- |
| MPEG audio | headers, Layer III side information and the reservoir placement of each granule's main data; Layer I and II allocations, scfsi and scalefactors | Layer III scalefactors and Huffman data; Layer I and II samples |
| AAC | every raw data block of Main, LC, SSR and LTP element by element, the Huffman-coded spectral data included (to reach the fill elements); every SBR and PS codeword | the values: nothing is kept; ER AAC and USAC raw data blocks are not read |
| AC-3 | every audio block: exponents, bit allocation, mantissa lengths (to reach the skip fields and auxiliary data) | the mantissa values; E-AC-3 blocks with the adaptive hybrid transform or enhanced coupling past that point, reduced-rate frames |
| DTS | core header, audio coding header (JOINX, SUBS) | the subframes: side information, scale factors, samples |
| Vorbis | the setup header (codebooks, floors, residues, mappings, modes), each packet's mode | floors and residues |
| Opus | TOC byte and frame packing | the range-coded SILK and CELT data |
| Speex | submodes, in-band requests, the lengths they imply | the field values (LSPs, pitch, gains, excitation) |
| Musepack | SV7 and SV4 to SV6 frames: used subbands, mid/side and noise-filled subbands; SV8 packets | scalefactors and samples; the frames inside SV8 audio packets |
| WMA | standard and Professional frames to the end of their coefficients; Voice frames down to the sizes that decide where a frame ends | the values; Voice's LSPs, pitch, gains and codebook indexes |

The plan's "Later" lists this work as "an entropy pass, format by format, MP3 first". This design makes that pass the first half of each decoder:

- The missing syntax goes into **parser code**, in the format project outside its `Decoding` namespace, as one function per structure with an optional destination, the pattern of FLAC's `SubframeSyntax.Read` (decoders.md, "Who owns the bits"): given none, it walks the bits and returns the facts the report shows; given buffers, it also stores the values.
- **It runs in every analysis,** not only when decoding:
  - the wave 3 figures below come from it, and they belong in the default report and its goldens, which decoding never changes;
  - it finds damage that the side information cannot show (a Huffman region that ends before or after its stated length, a code past a table, a residue that runs past the packet), which becomes the parser's findings and, where the unit cannot be read, `CodedFrame.IsUnreadable`;
  - the rules of "After the milestone review" in decoders.md hold: the walk counts against the parse budget, and its loops stop at the end of the data (a twelve-byte Vorbis packet that claims an 8,192-sample block costs about twelve bytes).
- **Node building stays apart from the walk:** the walk returns facts and stores values; nodes are built from the facts by the parser, as now, lazily where the format builds them lazily (AC-3's records, the blocked children). A walk that stores values builds no node.
- **Cost of the structure pass:** the walk makes the analysis of a lossy file slower; the target is at most 1.5 times the structure-analysis time of today's benchmark files of 300 s (development.md, "Analysis speed of the lossy formats"), **estimate**, measured in each pull request.
- **One pull request or two per format:** where the walk adds figures, nodes or findings to many goldens, it is its own pull request before the decoder, so that the decoder's pull request changes only `.decode.json` and `.measure.json` goldens. Recommendation; the lead's "one pull request per decoder" is kept for the decoders themselves.

### Lending for lossy frames

The session lends the parser its sample buffers (decoders.md, "The second entropy pass"). For lossy codings the buffers hold quantized values, not samples, which `SampleBuffers` serves without a change:

- Its 32-bit planes hold integers; every walk above produces integers: quantized spectral values (MPEG, AAC, AC-3 mantissas, Musepack, WMA), scalefactor and exponent indexes, Vorbis floor values and residue codebook entry numbers, CELT's coarse and fine energy indexes and pulse vectors, SILK's and Speex's parameter indexes and excitation.
- **The layout over the planes is the format's own,** documented on the syntax type both the parser and the decoder use (they live in the same project): for Layer III, per granule and channel, 576 values, then the scalefactors. Nothing in `Core.Abstractions` learns about it.
- **Frames are always walked when decoding.** A lossy decoder decodes only frames with `CodedFrame.IsWalked`; it has no reader of its own for the entropy-coded data. A frame the session cannot lend for (past the 2^24-sample ceiling, which no lossy frame reaches: AAC's largest is 1,024 values in 48 channels, Vorbis's 4,096 in 255) is `decode.frame-too-large`.
- **Side information** a decoder needs beyond the lent values (an MPEG frame header and side information, an AAC element's window and band layout) is read again from the frame's bytes with the parser's code: a few dozen bytes per frame read twice by one piece of code, as decoders.md accepts for a configuration.
- **Layer III main data** lies partly in earlier frames' bytes. With lending the decoder never needs those bytes: the parser's walk, which knows the placement already, decodes them into the lent planes. So `CodedFrame.Bytes` stays the frame's own bytes, its node the frame's node, and the decoder keeps no reservoir.
- **Why not a second read:** the rule of decoders.md (lend where a second read would cost a quarter of the decoding or more) was measured for FLAC. For the lossy codings the transform dominates the decoding, so the second read may cost less than a quarter (**estimate**). Lending is chosen all the same, because the walk runs in every analysis anyway, lending saves the read whatever its share, and one path is simpler to test than two. The MPEG pull request measures the share once, for the record.

### What moves into parser code, per format

- **MPEG audio:** Layer III scalefactors (with scfsi and the MPEG-2 intensity stereo scalefactor partitions) and the Huffman decoding of the big-values and count1 regions with the tables of Annex B; Layer I and II samples, including Layer II's grouped codes.
- **AAC:** the walk keeps its values (scalefactors, spectral values, pulse, TNS, prediction and LTP parameters, mid/side and intensity flags); SBR envelope and noise floor values and the PS parameters are kept instead of discarded; for steps 3 and 4, the ER AAC raw data blocks (with the error resilience tools of its object types) and the USAC elements with their arithmetic-coded spectra, which the parser does not read at all today.
- **AC-3, E-AC-3:** mantissa values (grouped codes of bit allocation pointers 1, 2 and 4), the coupling coordinates and rematrixing flags already read; E-AC-3's adaptive hybrid transform (gain-adaptive quantization and vector quantization), enhanced coupling and spectral extension parameters, and reduced-rate blocks.
- **DTS core:** each subframe's side information (prediction modes and coefficients, bit allocation, transient modes, scale factors, joint intensity), the samples in all their codings (plain, Huffman, block code, high-frequency VQ), the LFE samples.
- **Vorbis:** floor 0 and floor 1 packet decoding, residue partition classifications and values, with the setup the parser already reads.
- **Opus:** the range decoder (RFC 6716, section 4.1); SILK's header bits, LSF stages, gains, pitch lags, LTP filters and excitation (section 4.2.7); CELT's coarse and fine energy, the bit allocation, the PVQ codes and the anti-collapse, postfilter and transient bits (section 4.3). CELT's allocation is integer arithmetic the syntax needs to know how many bits each band takes, so it is parser code too.
- **Speex:** the values the walk skips by size today: LSP indexes, pitch, gains and innovation codebook indexes of every submode, and the wideband layers' fields.
- **Musepack:** SV7 scalefactor indexes and their differential coding, the samples with the Huffman tables the parser already carries; SV8's frames inside audio packets (no length of their own, so only the walk finds where each ends).
- **WMA:** the coefficient values the walk decodes and discards, the exponents or LSP values and the noise values; Voice's frame fields.

### Gap wave 3 figures this unlocks

| Figure | Formats | From |
| --- | --- | --- |
| "Coded Bandwidth" | MPEG Layer III | the end of the count1 region per granule (mpeg-audio.md, "Not yet covered") |
| "Coded Bandwidth" | Vorbis | the highest residue partition coded and the floor's used range per channel (vorbis.md) |
| "Coded Bandwidth" | xHE-AAC (USAC), AAC-ELD | their raw data blocks read element by element, steps 3 and 4 of AAC (aac.md) |
| "Coded Bandwidth" | DTS core | the subframes' bit allocation (dts.md) |
| "Stereo Coding" | DTS core | joint intensity and sum/difference per subframe, beyond the header flags (dts.md) |
| "Block Switching" | DTS core (TMODE), Opus CELT (transient flag) | the subframe side information; CELT's range-coded bits (coding-graphs.md, "later") |
| "Stereo Coding" | Opus CELT (intensity, dual stereo, mid/side of a band) | the range-coded bits (coding-graphs.md, "later") |
| "Stereo Coding" | TrueHD | the matrix parameters of the substream blocks (truehd.md) |

- TrueHD's stereo coding does not wait for a lossy decoder: its decoder exists and reads the matrix parameters with the parser's code, so the figure needs only the parser's tally. It can be built now, apart from this design.
- Each figure keeps the name, unit and axis of the series it joins (CLAUDE.md, "A statistic ... is built for every format that has the same concept"), and each format's doc moves the line from "not covered yet" to what is read.

## Output and determinism

### The PCM handed to the sink

- **Format:** `SampleFormat.Float32`, planar, as `PcmBlock` defines it; `PcmFormat.BitsPerSample` 32, so the per-sample charge is `DecodeWork.ForSample(4)`, 22 units. The integer Speex decoder writes 16-bit `Integer` blocks (decision 10).
- **Scale:** full scale is ±1.0 (an integer decoder's 16-bit codes are the same scale through the measurer's 2^-(bits - 1)). A decoder never clips, saturates or dithers: overs are real figures of decoded lossy audio, and the measurements count them ("Samples Over Full Scale"). Where a reference decoder clips (any 16-bit output, `opusdec` with its soft clipping), the cross-check compares within full scale only.
- **Rate:** the stream's output rate as the parser reports it: twice the core rate for HE-AAC, 48 kHz for Opus whatever the input rate the header states (RFC 7845 recommends it, and the parser counts at it), the core rate for DTS.
- **Channels:** every coded channel in the format's order, with the parser's `ChannelLayout`; no downmix. MPEG-2 Layer II multichannel streams output their two base channels, which carry the MPEG-1 compatible stereo, and say so in the measurement's reason.
- **Positions:** untrimmed, at the coded stage, as decoders.md fixes it; the first unit's output starts at position 0, priming included. Decision 7 is about what the measurer does with the trims.
- **Format changes:** a decoder never writes a block that differs from what it declared. The parsers already end a stream where the configuration changes (MPEG, ADTS, AC-3, DTS: one stream rule), so each stream has its own session; a change inside a stream that the parser does not split is `decode.frame-invalid` for the unit. Opus is the one format whose units may differ from the stream (mono frames in a stereo stream, RFC 6716 section 2.1.2): its decoder outputs the channels the header states, a mono frame on both, as that section describes.

### Decoders fall under the determinism rules

The goldens of `--measure` compare figures byte for byte on all six CI platforms and against the NativeAOT build. The figures are computed from the decoder's PCM, so a decoder whose last bit differs between platforms moves a quantized figure sooner or later, as [spectral-analysis.md](spectral-analysis.md) measured for the FFT ("a flip somewhere is a matter of time"). The lossless decoders are integer code and exact; the lossy ones are floating point. So:

- **The measurement rules of CLAUDE.md apply to every lossy decoder** and to the parser code it shares that computes in floating point:
  - no fused multiply-add, no `*Native` or `*Estimate` API, no `TensorPrimitives`, no BCL reduction over floating-point spans;
  - no platform math library: tables of cosines, windows (sine, Kaiser-Bessel derived, Vorbis's), powers (x^(4/3), 2^(x/4)), the Opus and Speex exponentials and logarithms, and the SBR band edges come from `DetMath` or from tables in the source;
  - `Vector128` element-wise only, never `Vector<T>` and never a horizontal sum: a polyphase window or a filter computes several output samples in its lanes, each lane's sum in the scalar order;
  - every sum, butterfly and filter in an order that depends only on the transform's size; one scalar reference per kernel, and the vector path bit-identical to it, tested with `DOTNET_EnableHWIntrinsic=0` as the decoders' vector paths are ("Vector paths of the decoders" in development.md);
  - each float kernel marked `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`, so the test's short input runs the same tier as the analysis;
  - no `ArrayPool<T>.Shared` (decoders.md already says so for buffers).
- **What does not carry over:**
  - NaN and huge values cannot arise from coded data that the walk has checked, except through crafted scalefactors and gains; a decoder clamps its gain exponents to the range its specification allows (the parser reports a value outside it) instead of counting non-finite samples. The measurer still counts and replaces any non-finite sample it gets.
  - Subnormal flushing at fixed positions is the measurer's rule for its own recursive filters. Decoders with recursive state (Opus's de-emphasis and postfilter, Speex's and WMA Voice's synthesis filters, DTS's prediction, AAC Main prediction) flush below 1e-30 at the start of each unit, a fixed position in the stream, since a crafted stream of one click and then silence would otherwise run at a twentieth of the speed its work charge assumes. A decoder's unit is its own grid, so this is not the "end of a decoder's block" the measurement rule forbids.
- **Single precision:** the decoders compute in `float`, as their references do (minimp3, libvorbis, CELT); tables are computed once in double with `DetMath` and rounded to `float`. Double would halve the vector width for no figure the measurements can show: the difference between the two is far below the 16-bit criterion of the cross-checks. Where a specification computes in double (none known), the decoder follows it.
- **Random noise:** the noise of AAC's noise substitution, AC-3's dither of zero-bit mantissas, WMA's noise coding and Vorbis's none: each decoder uses one generator with a fixed seed at the start of the stream and after each gap, so its output is deterministic; the cross-check cannot compare those bands sample by sample (see [The PCM cross-checks](#the-pcm-cross-checks)).

### Integer decoders

- **Opus:** SILK and its resampler are integer code in both of libopus's builds (`silk/float` holds only encoder files, libopus 1.5.2's `silk_sources.mk`), so they are exact in any port. CELT has a float and a fixed-point implementation; RFC 6716 makes the float configuration's output normative (Appendix A) and accepts any decoder within `opus_compare`'s threshold with the same final range coder state (section 6). The recommendation is the float CELT (decision 10): normative, and its output is not limited to 16 bits, as the fixed-point build's `opus_decode` is.
- **Speex:** libspeex builds a float or a fixed-point decoder; neither is normative. The fixed-point one is integer and exact, so the port is bit-exact against libspeex 1.2.1 built with `--enable-fixed-point` from the pinned source, a far stronger check than a tolerance (decision 10).
- **The others** define their decoding in real numbers (A/52, TS 102 114, the Vorbis I specification, ISO/IEC 14496-3) or have only float references (minimp3, libmpcdec's default build): float decoders under the rules above.

### How it is held

- `MeasurementDeterminismTests.BannedCalls` scans the compiled lossy format assemblies whole (parsers and decoders), not only their `Decoding` namespaces, since the walk's shared code can compute in floating point. Today three parsers call `Math.Pow`: AAC's SBR band edges (`Sbr.cs` line 172, rounded to integers, so a last-bit difference can move a band edge at a tie), Vorbis's `lookup1_values` and `float32_unpack` (`Setup.cs` lines 271 and 712) and WMA's codebook check (`Vlc.cs` line 152). They move to `DetMath`, `Math.ScaleB` or integer code in the groundwork pull request, with their goldens unchanged.
- **`DetMath` moves to `Core.Abstractions`** (namespace `Encspect.Core`, public), since decoders and parsers may reference nothing else; the measurements use it from there. Its hash tests move with it. CLAUDE.md's layout line for `Core.Abstractions` gains it.
- **`DecoderDeterminismTests`:** the raw float output of each lossy decoder on its fixtures hashes to the value its `.decode.json` golden holds (`pcmSha256`, the IEEE bits in little-endian order), on every platform; `scripts/check_aot.sh` with `--measure` covers NativeAOT. The hash is the analyzer's own, a pin of determinism and not a claim of correctness, so it departs from decoders.md's "never from the analyzer" for manifests deliberately: correctness is the cross-checks' job (below).

## Accuracy and verification

### What `--verify` means for a lossy stream

- A lossy stream stores no checksum over decoded audio. Its coded checksums (MPEG CRC-16, AC-3's CRC1 and CRC2, the ADTS CRC, Ogg page CRCs) are the parser's, compared in every analysis, and decision 4 of decoders.md keeps them out of the verification.
- **So a lossy decoder declares no checksum** (`IDecoder.DeclaresChecksums` false), as the `pcm` decoder does: `--verify` alone opens no session for a lossy stream, `--measure` and a front end's `Output` do, and the stream's verification has `HasChecksums` false. Its `DecodeSetup.IsLossy` is true, so if a later change declares a checksum the wording is "verified (lossy stream)" from the start.
- **What a decode tells:** the frames the decoder could not decode (`decode.frame-invalid`), withheld output (decision 3) and the decoded samples, in the measurement's status and reason ("1 frame was not decoded"). It never says "verified", "intact" or "lossless".
- **Why not decode under `--verify`:** with the entropy walk in the parser, the structure pass already finds the syntax damage a decode would; the decoder adds reconstruction, which fails on almost nothing the walk has accepted. Decoding a folder of MP3 files to compare nothing would cost minutes (decision 2).

### Conformance criteria per format

| Format | Criterion | Test material |
| --- | --- | --- |
| MPEG audio | ISO/IEC 11172-4 and 13818-4 "full accuracy": the RMS of the difference from the reference output below 2^-15/√12 of full scale and its largest magnitude at most 2^-14 (**unverified**: from secondary sources; the standards' texts were not read) | the 35 ISO/IEC 13818-4 audio streams the MPEG cross-check already downloads (ISO's publicly available standards site), which carry the reference output as 16-bit PCM per channel (`test01_l.pcm` and the others, read for this design); the Layer III streams in ISO/IEC 14496-26's DVD1 (`compressedMpeg12/add-opt/l3_*.mpg`) with `referencesWav`. No MPEG-1 conformance folder was found on that site under the name tried (**unverified**) |
| AAC | ISO/IEC 14496-4 and 14496-26 define criteria by output width (**unverified**: the texts were not read) | 14496-26's `compressedAdts`, `compressedMp4` and `compressedAdif` with `referencesWav`, and 14496-4's `audio_conformance`, both on ISO's site under its license for electronic inserts ("in their original format without any modifications for the purposes specified"), so downloaded at pinned SHA-256 and never committed, like the 13818-4 streams |
| Opus | RFC 6716 section 6: within `opus_compare`'s threshold for every test vector at every output rate and channel count, and the same final range coder state as the reference for every packet; RFC 8251 replaces the test vectors and allows either set | RFC 8251's test vectors, downloaded at a pinned SHA-256 (their license is **unverified**); `opus_compare` built from the RFC's code |
| Vorbis | none: the specification states no accuracy | libvorbis's output, by the tolerance below |
| AC-3, E-AC-3 | none in A/52 or TS 102 366 that is known here; Dolby's test signals are not free (**unverified**) | Core Audio's output (macOS runners) and FFmpeg's |
| DTS core | none in TS 102 114 that is known here (**unverified**) | FFmpeg's and libdca's output, black boxes |
| Speex | none | bit-exact equality with libspeex 1.2.1's fixed-point build |
| Musepack | none | libmpcdec's `mpcdec` output, by the tolerance |
| WMA | none published | Microsoft's decoder on the Windows runners (the WMA check's Source Reader and DMO paths, with PCM written), FFmpeg second |

### The PCM cross-checks

How a decoder's output is compared with a reference decoder's, in the cross-check job of its format:

- **What is compared:** the analyzer's PCM, written by a small dump tool in the test tree (`tests/Encspect.PcmDump`, not shipped: the library with an `Output` sink that writes each stream's float samples and their positions), with the reference's, at the coded stage: the reference's direct path where the harness has one (minimp3 with float output, libvorbis with granule positions cleared as the official-decoder harness already does, libopus's float decoder, FDK fed access units, Core Audio's `AudioConverter`), never a path that trims.
- **Alignment:** by position; positions either side withholds or does not output (gaps, concealed units, priming a reference drops) are left out and listed, never compared.
- **Width:** where the reference writes 16-bit PCM, the analyzer's floats are rounded to nearest and clipped to 16 bits first, so the reference's own rounding is not counted as a difference; where it writes floats, both are compared as floats, and samples where either reaches full scale are left out where the reference clips.
- **The tolerance:** per channel, the RMS of the difference at most 2^-15/√12 of full scale (about -101 dBFS) and its largest magnitude at most 2^-14 (about -84 dBFS), the full-accuracy criterion of ISO/IEC 11172-4 above.
  - **Basis:** it is the one numeric bound among these formats' conformance documents known here, and it is the error of rounding to 16 bits, so a decoder within it differs from the reference by no more than any 16-bit output of either would. It is far below what the measurements resolve (tenths of a dB, a cutoff found from a step of tens of dB).
  - The criterion is applied to every format, MPEG's conformance streams included; Opus adds `opus_compare` and the range coder state, Speex demands equality.
  - A decoder that fails it on a file is a bug until shown otherwise; the difference goes to the lead with the file, as the official-decoder cross-checks' `pending` items do, before any tolerance is widened.
- **Bands of random noise** (AAC noise substitution, AC-3 dither, WMA noise coding) cannot match sample by sample, since each decoder draws its own noise. Frames that use them are compared by the energy of each scalefactor band or exponent band in the decoded spectrum, within 0.5 dB (**estimate**, to be set from the first fixtures), and the strict comparison runs on fixtures without the tools, which every encoder can be told to write.
- **What a reference conceals** (FDK's noise substitution for a damaged unit, libvorbis or libopus where they conceal) is left out of the comparison and recorded as `explained`, as the lead's rule of 2026-11-01 says.
- **FFmpeg** is always a second column, as in the official-decoder cross-checks; a difference between the references is listed per file.

### Fixtures and goldens

- **Encoder-written fixtures** are committed already for every format (LAME, ffmpeg, FAAC, fdkaac, oggenc, opusenc, speexenc, the Musepack encoders, Microsoft's WMA encoders); each decoder's pull request adds `.decode.json` goldens with the PCM hash and `.measure.json` goldens for a few of them.
- **Hand-built damage**, per decoder: a frame whose walk fails (the parser's finding, no decode), a frame cut short, frames removed from the middle (a gap and the withheld output after it), for Layer III a frame whose main data begins in a removed frame, for Vorbis and Opus a lost packet, for AAC an SBR frame coded as a time difference after a gap.
- **Downloaded, never committed:** the ISO conformance streams with their reference PCM, RFC 8251's test vectors, the real files the format cross-checks already fetch (mp3PRO, xHE-AAC, HE-AAC, Dolby's E-AC-3 kit, the DTS samples, the WMA samples).
- **Tests:** `DecoderFixtureTests` decodes every fixture of the format with a hashing sink and holds the hash, the decoded samples and the tree (unchanged by decoding); since lossy decoders declare no checksum, these tests, `MemoryTests` and the fuzzer pass an `Output` sink or `Measure`, so that the sessions open at all.

## Gaps, concealment and priming

### The rule, and what decoding adds to it

The lead's rule of 2026-11-01: a frame the decoder cannot decode is a gap, never concealed audio; `decode.frame-invalid`, no output for it, its positions counted as lost by the measurements. Lossy decoding adds one question the rule does not answer: the output **after** a lost unit, which in every lossy coding depends on the lost unit's state.

- An overlapped transform (MDCT in Layer III, AAC, AC-3, Vorbis, CELT, WMA) adds the second half of the previous unit's inverse transform to the first half of the current one; without the previous unit the first unit after the gap holds time-domain aliasing that does not cancel.
- A polyphase filterbank (Layers I to III, Musepack, DTS, SBR's QMF banks) keeps the last 15 subband vectors (480 samples of history at 32 subbands).
- Recursive state (CELT's energy prediction, postfilter and de-emphasis; SILK's and Speex's LPC and pitch state; DTS's subband prediction; AAC Main prediction) converges only gradually.
- Syntax state: Layer III main data in earlier frames' bytes, SBR envelopes coded as differences in time, Musepack's scalefactors coded as differences from the previous frame's.

**Recommendation (decision 3):** after a gap, or a unit that was unreadable or not decoded, the decoder resets its state and withholds the output that depends on the lost state, and returns 0 samples for the units it withholds; the measurer sees the next block's position jump and counts the withheld positions as lost, as it counts the gap. Withheld output is not a finding (`decode.frame-invalid` was raised for the unit that caused it). What each decoder withholds:

| Decoder | State that crosses units | Withheld after a gap |
| --- | --- | --- |
| MPEG Layer III | main data in earlier frames; the hybrid transform's overlap (one granule) and the synthesis history (480 samples) | the frames whose main data begin before the gap (they are not decodable at all, see below); then whole frames covering one granule plus 480 samples: one frame of MPEG-1 (1,152), two of MPEG-2 and 2.5 (576 each) |
| MPEG Layers I and II | the synthesis history (480 samples) | the first frame of Layer II (1,152); the first two of Layer I (384 each) |
| AAC LC, LTP | overlap (one frame); LTP's history of two frames | one frame; three with LTP |
| AAC Main | backward-adaptive prediction over every earlier frame | until a frame that resets every predictor (`predictor_reset` reaches each group), as the specification's resets intend |
| SBR, PS | QMF banks, time-differential envelope and noise coding, smoothing; PS's decorrelation | until the first SBR frame coded without time differences, and the core's frame |
| AC-3, E-AC-3 | overlap (one block) | the first audio block |
| DTS core | the synthesis history and the subband prediction | the first frame (512 samples cover the history; the prediction **estimate** converges within it) |
| Vorbis | overlap with the previous block | the first packet after a reset returns no audio: "Data is not returned from the first frame; it must be used to 'prime' the decode engine" (Vorbis I, section 1.3.2), so nothing extra |
| Opus | CELT's energy prediction, postfilter and de-emphasis; SILK's state; the resampler | 80 ms (3,840 samples): RFC 7845's pre-roll for "sufficient history to the decoder that it will have converged" (sections 4.2 and 4.6) |
| Speex | LPC, pitch and enhancer state | one frame (20 ms) **estimate**, set from the fixtures |
| Musepack | the synthesis history; scalefactors coded as a difference from the previous frame's (libmpcdec r495, `mpc_decoder.c`: SV7 always, SV8 except in the key frame that starts each packet) | SV8: up to the next packet; SV7, which has no key frames: a band's output until the band has had an absolutely coded scalefactor since the gap, so possibly the rest of the stream |
| WMA standard, Professional | overlap; WMA standard's LSP or exponent state | one frame; the supplements settle it (decision 11) |
| WMA Voice | LPC, pitch and postfilter state | set by its supplement |

- **Exact where it can be:** an overlap and a polyphase history are pure functions of the last unit, so the output after the withheld span equals what an undamaged stream decodes to. Recursive state is not exact; the spans for it are the specifications' convergence times or estimates, and their articles say so.
- **The alternative** is to output everything after a reset; the measurements would then measure aliasing and convergence artifacts as audio, which the rule's intent ("never concealed audio") argues against.

### The bit reservoir across a lost frame

- A Layer III frame's main data can begin in earlier frames (main_data_begin, up to 511 bytes in MPEG-1, 255 in MPEG-2). Where they begin before the start of the parser's reservoir (a stream's first frames, after the scan closes) or in a frame the parser counted as lost (cut, refused), the frame cannot be decoded, however intact its own bytes are.
- The parser knows this already: it places every granule's main data (mpeg-audio.md, "Reservoir").
- **Recommendation (decision 4):** the parser counts such a frame as lost (Lost Samples, and the stream's `lostSamples`) and hands it with `IsUnreadable`, since it is a unit "in a stream whose state was lost", the third case of [samples-definition.md](samples-definition.md). That changes the lost samples of MPEG fixtures with gaps and of streams whose first frame points before the start (an encoder writes main_data_begin 0 there, so only damage and cut files are affected; **estimate**). The alternative leaves the parser as it is and makes it the decoder's `decode.frame-invalid`, a unit the decoder refuses for a reason the parser checks anyway.
- Junk between two frames does not close the reservoir (mpeg-audio.md), so a frame after junk decodes where its main data lie in the frames before.

### Opus frames of 0 bytes

- RFC 6716 section 3.2.1: a frame length of 0 means "no frame is available, either because it was dropped during transmission ... or because the encoder decided it was not necessary to transmit it (DTX)", and the decoder is to conceal it.
- **Recommendation (decision 5):** no concealment, as for any unit with nothing to decode: the decoder outputs nothing for it, resets, and withholds the 80 ms after it as after a gap; the measurements count the positions as lost, and the reason names frames of 0 bytes. The parser counts their declared samples as now (they are not lost in its sense: nothing is damaged). Alternatives: write zeros (silence the encoder did not code), or run libopus's concealment (against the rule).

### Priming, delay and trims

Positions stay untrimmed (decoders.md). What each format puts before and after the audio, and who states it:

| Format | What the decoder outputs first | Stated by the stream's syntax | Stated by a container or encoder |
| --- | --- | --- | --- |
| MPEG audio | the encoder's delay plus the decoder's (528 + 1 samples by LAME's convention, **unverified** here) | nothing | the LAME tag's delay and padding, the Xing frame count |
| AAC | one frame of priming (two with SBR, **unverified**) | nothing in ADTS | MP4 edit lists, iTunSMPB |
| AC-3, E-AC-3, DTS | the encoder's delay | nothing | MP4 edit lists |
| Vorbis | the first packet returns nothing; the rest by overlap | start and end trimming from granule positions | |
| Opus | everything | pre-skip and end trimming (RFC 7845, 4.2 and 4.4) | |
| Speex | the encoder's lookahead (40, 143, 349 samples) | granule positions (speex.md) | |
| Musepack | 481 samples of delay | the header's sample count and the last frame's end | |
| WMA | half a frame of overlap | the frames' trims (Professional, Lossless); Microsoft's rule for standard | ASF's preroll |

- **Recommendation (decision 7):** the measurer measures the trimmed range where the stream's own syntax states the trim, the cases [samples-definition.md](samples-definition.md) puts in `samples` (Opus, Vorbis, Speex, Musepack, WMA Professional and Lossless, TrueHD's end of stream marker), and counts what it leaves out as trimmed samples, a figure apart from lost samples; container and encoder figures (LAME tag, iTunSMPB, edit lists) stay stated figures and are not applied. Then the measured samples equal the stream's `samples` where nothing is lost, and Opus's pre-skip, which holds the decoder's convergence, does not reach the levels and the true peak. The alternative keeps spectral-analysis.md's rule (measure as decoded, trim later); the third is to apply every trim, the stated ones included.
- The PCM the sink receives stays untrimmed either way; the dump tool and the cross-checks compare at the coded stage.

## Cost

### Work units per format

The unit of decoders.md applies unchanged: one predictor or filter tap on one sample of one channel, about 0.6 ns. Every decoder charges `DecodeWork.ForSample(4)` (22) per output sample of each channel, nothing per coded byte (its frames are walked), and its stages before it runs them. The drafted weights, all **estimates**, each measured and fixed by its pull request and held by `DecoderWorkTests` to at most twice the FLAC reference's time per unit:

| Stage | Units | Formats |
| --- | --- | --- |
| dequantization, per coefficient | 2 | all transform codings |
| mid/side, intensity stereo, coupling, rematrixing, channel transforms, per coefficient they touch | 1 per input channel | MPEG, AAC, AC-3, Vorbis, WMA, CELT |
| inverse transform, per output sample | 2 × log2 of the transform length (22 for 2,048, 16 for 256, 10 for Layer III's 36) | MPEG III, AAC, AC-3, Vorbis, CELT, WMA |
| window and overlap-add, per output sample | 2 | as above |
| polyphase synthesis, 32 bands and 512 taps, per output sample | 26 (16 taps and the matrixing) | MPEG, Musepack, DTS |
| TNS, per coefficient filtered | its order | AAC |
| AAC Main prediction, per coefficient | 10 | AAC Main |
| LTP, per frame | a forward transform of the frame plus its taps | AAC LTP |
| SBR, per output sample | 60 (analysis and synthesis banks, generation, adjustment) | HE-AAC, USAC |
| parametric stereo, per output sample | 40 | HE-AAC v2 |
| Vorbis floor, per coefficient | 2 | Vorbis |
| CELT postfilter and de-emphasis, per sample | 6 | Opus |
| SILK synthesis, per internal sample | the LPC order (10 or 16) plus 5 for LTP | Opus |
| Opus resampler, per output sample | 16 | Opus |
| Speex synthesis and enhancer, per sample | 40 | Speex |
| DTS subband prediction, per subband sample predicted | 4 | DTS |

- **Per frame:** a decoder charges a frame before it decodes it, at most 2^27 units a charge. Every frame of the drafted weights fits but Opus's: a packet of 120 ms at 48 kHz in 255 channels is about 1.5 × 10^8 units, so Opus charges and checks the token per stream of a multistream packet.
- **What the floor of the charge sees:** `ForSample(4)` times the samples returned, as now; withheld units return 0 and charge their work, so they never trip it.

### Per-byte allowance

`IDecoder.WorkPerInputByte` is derived per decoder from its lowest legitimate bitrate, as Monkey's Audio's 8,192 was, so that a legitimate file decodes in full under the default budget:

| Decoder | Lowest legitimate case, **estimate** | Units per byte it needs | Declared |
| --- | --- | --- | --- |
| MPEG audio | MPEG-2.5, 8 kbps, 8 kHz | about 600 | 2,048 |
| AAC LC | 8 kbps, 8 kHz mono | about 500 | 8,192, the `aac` decoder's figure for HE-AAC |
| HE-AAC v2 | 16 kbps, 48 kHz stereo output | about 4,800 | 8,192 |
| Vorbis | about 32 kbps, 44.1 kHz stereo | about 1,300 | 2,048 |
| Opus | 6 kbps, 48 kHz output | about 2,800 mono, 4,200 stereo at 8 kbps | 8,192 |
| Speex | the 2.15 kbps vocoder mode; ultra-wideband at about 4 kbps | about 2,200 to 3,600 | 4,096 |
| AC-3, DTS, Musepack, WMA standard | their lowest rates | under 1,000 | 2,048 |
| WMA Voice | 4 kbps at 16 kHz | about 2,400 | 4,096 |

- A crafted file of 100 MB at 8,192 units a byte stops after about 8 minutes, as Monkey's Audio's does; the floor, 2^35 units, still covers anything under 4 MiB.
- Free-format MPEG frames can be smaller than any listed bitrate; such a file below about 8 kbps would stop with `decode.limit`, which the MPEG pull request checks against the free-format fixtures.
- Opus frames of 0 bytes are not decoded (decision 5), so DTX costs nothing.

### Memory per stream

Nothing grows with the stream (decoders.md, "Memory"): a session holds one frame's lent and output buffers and the state that crosses units.

| Decoder | Largest state, **estimate** | Ceiling |
| --- | --- | --- |
| MPEG audio | 2 channels × (576 overlap + 1,024 synthesis history) floats | negligible |
| AAC | per channel 1,024 overlap, 2,048 LTP history, SBR's QMF buffers (about 40 KB), PS; up to 48 channels from a PCE | about 4 MB |
| AC-3, E-AC-3 | up to 16 channels × 256 overlap | negligible |
| DTS core | 6 channels × 512 synthesis history plus LFE | negligible |
| Vorbis | up to 255 channels × 4,096 overlap and an 8,192-sample block | about 12 MB |
| Opus | up to 255 streams of decoder state (about 30 KB each) and a 120 ms frame | about 10 MB |
| Speex, Musepack | 2 channels | negligible |
| WMA | 8 channels × the frame (up to 8,192 for Professional) | about 1 MB |

A frame past a ceiling is `decode.frame-too-large`; `MemoryTests` gets a long stream of each.

### Parallel decoding

[parallel-decoding.md](parallel-decoding.md) is approved and deferred; it takes the formats whose frames decode apart from each other. No lossy coding is such a format, but some come close:

- **Finite history** (an overlap, a polyphase history): a worker that decodes one unit before its batch, and discards that unit's output, starts its batch with state equal to the serial run's, bit for bit, since the state is a pure function of that unit. MPEG audio, AAC LC, AC-3, Vorbis and Musepack SV8 batched by packets (its scalefactors refer across frames only inside a packet) are such codings; the cost is one unit decoded twice per batch.
- **Infinite history** (CELT's energy prediction, SILK, Speex, DTS's prediction, AAC Main): not parallel without changing the output.
- **Recommendation:** none of it in the lossy decoders' pull requests; the table goes to the parallel design's "Independence per format" when that design is built.

### Speed targets

- **The reference:** "Analysis speed of the lossy formats" in development.md (the benchmark files of 300 s) gives FFmpeg's MP3 decoder at 0.56 s for the 4.8 MB file, start included; each decoder's pull request times the fastest reference for its format on the same files (minimp3's tool, FFmpeg, `oggdec`, `opusdec`, FDK through its harness, `mpcdec`, `speexdec`).
- **The target:** the analysis with decoding to a sink that keeps nothing, minus the analysis without decoding, at most 2.5 times the fastest reference's decoding time, in the JIT and the NativeAOT build. The lossless decoders reached 1.7 to 3.2 times after two speed passes (decoders.md); 2.5 is the middle of that range. A decoder above it gets a speed pass before the next decoder starts.
- **In absolute terms,** an hour of stereo MP3 should decode in under 10 s on the benchmark machine (**estimate**), well under the budget's floor of about 21 s.

## Contract changes

- `DetMath` moves to `Core.Abstractions`, public, with its tests; `Encspect.Host.Measurement` uses it from there.
- `MeasurementDeterminismTests`' scan covers the lossy format assemblies; the three `Math.Pow` calls in parsers go.
- `CLAUDE.md`, in the groundwork pull request:
  - Layout, `src/Encspect.Core.Abstractions`: "… the shared lazy-children list (`BlockedChildren`) with the record containers it is built from (`ChunkedList`, `ByteStore`), and the deterministic math (`DetMath`) that measurements and lossy decoders share."
  - Architecture rules, the measurement determinism item, first sentence: "Measurement code, and the floating-point code of decoders and of the parser code they share, is deterministic, so reports are byte-identical …", and its last sentence: "`MeasurementDeterminismTests` pins hashes of the kernels' raw output, `DecoderDeterminismTests` those of the decoders' PCM, and the scan covers the compiled measurement and lossy format assemblies."
- No change to `IDecoder`, `IFrameDecoder`, `CodedFrame`, `DecodeSetup`, `SampleBuffers`, `PcmBlock` or the router.

## Rollout

0. **Groundwork** (one pull request, no format): `DetMath` in `Core.Abstractions`; the scan over the lossy format assemblies and the three `Math.Pow` replacements, goldens unchanged; `DecoderDeterminismTests` with a scripted float decoder; `tests/Encspect.PcmDump` and `scripts/fixtures/compare_pcm.py` with the tolerance and the band-energy comparison; the withheld-output reason in the measurer's text; the `lossy-decoding` article (what Encspect's decoding of a lossy stream is, that it is not bit-exact with other decoders and within what it agrees, what gaps and withheld output mean); CLAUDE.md's lines.
1. **MPEG audio:** the Layer III walk and the Layer I and II samples in the parser (with Coded Bandwidth for Layer III and decision 4's lost frames), possibly as its own pull request; then the `mpeg-audio` decoder following minimp3, the 13818-4 and 14496-26 streams against their reference PCM, minimp3, mpg123 and FFmpeg as references.
2. **AAC LC** (if decision 1 buys the specification; else after step 4): the walk keeps its values; the decoder; 14496-26 and 14496-4 streams; FDK, Core Audio, Media Foundation and FFmpeg.
3. **Vorbis:** floors and residues in the parser with Coded Bandwidth; the decoder; libvorbis and FFmpeg.
4. **Opus:** the range decoder, SILK and CELT syntax in the parser with CELT's block switching and stereo coding; the decoder; RFC 8251's vectors, `opus_compare`, the range state, libopus and FFmpeg.
5. **HE-AAC:** SBR and PS values kept; the decoder's SBR and PS; the downloaded SBR conformance and aacPlus check streams against FDK and Core Audio.
6. **AC-3**, then 7. **E-AC-3** (the walk of the tools the parser stops at, then their reconstruction); Core Audio and FFmpeg; Dolby's E-AC-3 kit as downloaded input.
8. **Musepack** (SV8 frames in the parser first), 9. **DTS core** (the subframes in the parser with Coded Bandwidth, stereo coding and block switching), 10. **Speex**.
11. to 13. **WMA standard, Professional, Voice:** each after its clean-room supplement (decision 11) and Microsoft's decoder writing PCM on the Windows runner.
14. **AAC Main, LTP and ER AAC** (with AAC-ELD's Coded Bandwidth), 15. **USAC** (with xHE-AAC's Coded Bandwidth).

Each step brings, as decoders.md's rollout did: its fixtures and `.decode.json` and `.measure.json` goldens, `DecoderWorkTests` cases (its worst cases: Layer III short blocks with intensity stereo at the smallest frames; AAC with eight short windows, TNS of the highest order and 48 channels; SBR with the most envelopes and PS with 34 bands; Vorbis 8,192-sample blocks in 255 channels; Opus 120 ms hybrid and multistream packets; and so on), `MemoryTests` and fuzz coverage, its format doc's "Decoding" section, its notices, its articles, and a milestone review after steps 1 and 4 (the pattern, then the hardest exact check).

## Decisions for the project lead

1. **AAC's source.** ISO/IEC 14496-3 is not free and no permissive AAC decoder exists. Options: (a) buy ISO/IEC 14496-3 (the current edition with its amendments) and, for step 15, ISO/IEC 23003-3, and write the decoder from them; (b) a clean-room description from FFmpeg's AAC, SBR and PS decoder, as for WMA, then an implementer who has not read it; (c) follow FDK AAC, whose license is not BSD, MIT or Apache and makes use conditional on patent licenses, so the project's MIT code would carry its conditions. Recommendation: (a), the authoritative text and the smallest risk; (b) if the lead does not buy it; never (c).
2. **`--verify` for lossy streams.** Options: (a) as `pcm`: no checksum declared, `--verify` alone decodes nothing, `--measure` decodes; (b) `--verify` decodes them too and reports the frames that do not decode. Recommendation: (a): the walk in the structure pass finds the damage a decode would, and a folder of MP3 files would cost minutes for no verdict.
3. **The output after a gap.** Options: (a) reset and withhold the output that depends on the lost state (one unit for overlaps and filterbanks, 80 ms for Opus, the table above), counted as lost positions; (b) reset and output everything, artifacts included. Recommendation: (a), which keeps the measurements free of aliasing and convergence that the stream never coded.
4. **Layer III frames whose main data begin before the reservoir or in a lost frame.** Options: (a) the parser counts them as lost and hands them unreadable, which changes `lostSamples` in goldens of damaged MPEG fixtures; (b) the decoder refuses them (`decode.frame-invalid`), the parser unchanged. Recommendation: (a), samples-definition.md's "state was lost" case, and the parser knows it already.
5. **Opus frames of 0 bytes (DTX or lost).** Options: (a) a gap with 80 ms withheld after it; (b) zeros; (c) libopus's concealment. Recommendation: (a), the RFC's own reading that no frame is available; (c) breaks the rule.
6. **Gains a decoder could apply.** AC-3's dynamic range and dialogue normalization, DTS's dynamic range, AAC's DRC, USAC's loudness normalization, Opus's output gain, any downmix. Options: (a) none; (b) none but Opus's output gain, which RFC 7845 says players "SHOULD apply ... by default" and `opusdec` applies; (c) each format's reference default. Recommendation: (b): the coded signal everywhere, and Opus's header gain because the format defines it as part of decoding; the cross-checks configure the references the same way where a tool allows it (what FFmpeg and Core Audio apply by default is **unverified** and recorded by the first runs).
7. **Measuring the trimmed range.** Options: (a) measure the range the stream's syntax trims to (Opus, Vorbis, Speex, Musepack, WMA, TrueHD) and count the rest as trimmed samples; (b) keep measuring as decoded, untrimmed; (c) also apply container and encoder figures. Recommendation: (a), so measured samples equal `samples` and priming does not reach the levels; it changes spectral-analysis.md's rule, in the groundwork pull request.
8. **Where the entropy pass runs.** Options: (a) in every analysis, the parser's walk, with the wave 3 figures in the default report and a structure pass up to about 1.5 times slower for lossy files; (b) only when decoding, which cannot give the figures, since decoding never changes the report's nodes or statistics. Recommendation: (a).
9. **Determinism scope.** Options: (a) the measurement rules for every lossy decoder and the parser code it shares, `DetMath` moved to `Core.Abstractions`; (b) tolerate platform differences in decoders and compare `.measure.json` goldens with a tolerance. Recommendation: (a); (b) gives up the byte-identical reports the project holds everywhere else.
10. **Integer or float, Opus and Speex.** Options for Opus: (a) the float CELT (normative per RFC 6716, unclipped output, exact SILK and range state); (b) the fixed-point CELT (bit-exact against libopus's fixed-point build, but `opus_decode` limits it to 16 bits, so overs are lost). For Speex: (a) the fixed-point decoder, bit-exact against libspeex's fixed-point build, with the perceptual enhancer on as libspeex's narrowband default and `speexdec` set it; (b) the float decoder within the tolerance. Recommendation: Opus (a), Speex (a).
11. **WMA.** Options: (a) a clean-room supplement per codec describing its reconstruction, then decoders, Microsoft's decoder as the PCM reference; (b) stop at the parsers, no WMA measurements. Recommendation: (a) for standard and Professional; Voice last and only if the lead wants it, since it is a speech codec of a format Microsoft no longer ships encoders for (**unverified**) with few files.
12. **Patents.** AAC, HE-AAC, AC-3, E-AC-3, DTS and WMA may be covered by patents; MP3's are reported expired (**unverified**). No legal conclusion is drawn here, as decoders.md draws none for TrueHD and WMA Lossless; the lead decides whether a format waits for advice.

## Unverified claims and estimates

- **Unverified:** the ISO/IEC 11172-4 and 13818-4 full-accuracy criterion (from secondary sources); the 14496-4 and 14496-26 criteria; the license of RFC 8251's test vectors; the absence of free AC-3 and DTS conformance material; MPEG-1 conformance streams on ISO's site (one folder name tried); LAME's 528 + 1 decoder delay and HE-AAC's priming; FFmpeg's and Core Audio's defaults for decoder-side gains; the expiry of MP3 patents; Microsoft's encoders for WMA Voice.
- **Estimates:** every work weight and per-byte figure above, the memory figures, the 1.5 times of the structure pass, the 0.5 dB band-energy tolerance, the withheld spans for recursive state (DTS, Speex), the share of a second read for lossy codings, the hour of MP3 in 10 s.

## Sources read

- The repository at `origin/main` adb9673: `CLAUDE.md`; `docs/PLAN.md` ("Later", "Phase 3, continued"); `docs/design/decoders.md`, `spectral-analysis.md`, `samples-definition.md`, `official-decoder-crosschecks.md`, `parallel-decoding.md`, `coding-graphs.md` (the concept table), `mpeg-clean-room-rules.md` (its source statement), the status and scope lines of `wma-standard-format.md`, `wma-pro-format.md` and `wma-voice-format.md`; the format docs' "not covered" lines and what they read; `development.md` ("Analysis speed of the lossy formats", "Where the format tables come from", the conformance downloads); `THIRD-PARTY-NOTICES.md`, `third_party/README.md`; the shapes of the lossy parsers, `MappedBytes`, `DetMath` and `MeasurementDeterminismTests`.
- Licenses: `third_party/minimp3/LICENSE` and `minimp3.h` (the vendored commit); libopus 1.5.2's `COPYING`, `silk_sources.mk`, `celt/mathops.h` and `src/opus_decoder.c` (release tarball from GitHub, SHA-256 `65c1d2f7…`); libmpcdec 1.2.6's and r495's `COPYING`, file headers and includes (the archives in the scratchpad that the Musepack work pinned); libspeex 1.2.1's `COPYING`, `nb_celp.c` and `sb_celp.c` decoder defaults and `speexdec.c`; Ubuntu's copyright files of `libopus0`, `libvorbis0a`, `libogg0`, `libspeex1`, `libmpcdec6`, `libfaad2`, `liba52-0.7.4`, `libdca0` and `libfdk-aac2` (their license fields only).
- Specifications: RFC 6716 (sections 1, 3.2.1, 4.2.9, 6, Appendix A), RFC 8251 (abstract, test vectors), RFC 7845 (sections 4.2, 4.6, 5.1); the Vorbis I specification (accuracy statements); ETSI TS 102 114 V1.6.1 (table of contents, core decoding overview).
- ISO's publicly available standards site, 2026-11-07: the listings of `ISO_IEC_13818-4_2004_Conformance_Testing/Audio` (and the contents of `test01.tar.gz`), `ISO_IEC_14496-4_2004_Conformance_Testing` and `ISO_IEC_14496-26_2010_Bitstreams` (DVD1's `README_audio.txt` and folder names), and the license text those pages show.
- Not read, by the clean-room rules: FFmpeg, faad2, liba52, libdca, mutagen, libtta, getID3, mp3guessenc and mctoolamed source.
