# Coding graphs: per-frame coding choices and stream summaries

Status: approved by the project lead on 2026-10-22, after two independent reviews, with four changes: dual channel is its own entry of "Stereo Coding" (decision 4), a "Channel Bit Share" series for MPEG (decision 6), the same concepts graphed in every format under the same headings (decision 8), and the real reason AAC and AC-3 record while reading (decision 3). The contract step (rollout step 1) is built, as [As built](#as-built-the-contract-step) says, and so are MPEG audio, the reference (step 2: [As built](#as-built-the-mpeg-step)), AAC (step 3: [As built](#as-built-aac)), and AC-3 and E-AC-3 (step 3: [As built](#as-built-ac-3-and-e-ac-3)); the other formats are not. On 2026-10-23 the lead added FLAC, Monkey's Audio, TTA and WAV and AIFF to wave 2 (decision 11); their survey is under [FLAC](#flac), [Monkey's Audio](#monkeys-audio), [TTA](#tta) and [WAV and AIFF](#wav-and-aiff), and the lead approved the "Prediction" heading they bring and "side/right" as FLAC's name everywhere (decision 12). On 2026-10-23 the lead chose the stepped stacked area for a category series over time, with smoothing optional (decision 13, built as [As built](#as-built-the-stacked-area-2026-10-23) says), and that its colors follow the stack and its layers carry their names. The decisions are collected under [Decisions](#decisions), and claims that no source read for this design confirms are marked **unverified**.

## As built: the stacked area (2026-10-23)

The project lead compared chart forms in screenshots and chose the stacked area (decision 13). What it changes in the GUI:

- **Forms of a category series:** "Over Time" (the stepped stacked area), "Smoothed" (above 30 one-second intervals only), "Frames" and "Summary". `GraphForm` has `Smoothed` beside `OverTime`; `CategoryGraph.IsOverTime` covers both. The column drawing and a form of one line for each category were built as prototypes and removed.
- **Layers and legend:** the bottom layer is the category with the largest share over the stream (`CategoryColumns.StackOrder`). The legend lists the layers bottom first: it runs left to right and wraps downward, so the baseline layer comes first as the stack's first row would, and the entries fall in the order of the shares in the file. The strip and the summary tables keep the order of the categories.
- **Smoothing:** the counts of each interval are summed with those of the two intervals on either side (a shorter window at the ends), so every value weighs the same, and an interval with no values stays a gap; `CategoryGraph.SmoothedCounts` makes them once.
- **Colors (the project lead, 2026-10-23, decision 13):** `CategoryColors.ForStack` gives the palette's slots to the categories in the order of the stack, so the muted first hue is the bottom layer's and the rarer layers stand out; the legend shows the colors drawn. The strip and the summary tables keep the colors of the categories, so a category can have another color in the area (the legend of each view is consistent within it). The layers carry no names (labels were built and rejected after screenshots); the strip's segment names use black or white by contrast (`CategoryColors.Readable`, at least 4.5 : 1 on every palette color), which replaces the strip's older luminance rule.
- **Layers:** levels are whole pixels; a 1-pixel surface line separates layers only where both are at least 3 pixels high, as the strip does, so the one-pixel minimum of a rare category stays its own color and a full switch shows no stripe.
- **Last interval:** `CategoryGraph.LastInterval` says whether it is a partial last interval (shorter than the one before it, the duration known) of one to seven frames (`FewFrames` is 8), which the area draws hatched (the project lead, 2026-10-23, in place of a dimming that did not explain itself): lines of the text color falling to the right over the layers' own colors, the opposite lean to the reserved categories' hatch, with a note "Last 0.03 s: 1 frame, hatched" next to the caption, shown only while it is hatched, and the hover text unchanged. Eight frames are about a fifth of a second of MPEG audio; the count is of frames and not of seconds, since a stream's frames per second differ by format. The last slot is at least 3 pixels wide, taken from the one before it. The smoothed form, a last interval as long as the others, one without frames and a column that merges intervals are not hatched.
- **List of graphs:** the heading and entry spacing is tighter, the view switcher's row has one height, so that the list does not change size between entries, and the note about the decoder sits under the chart and not under the list; the list shows whole items only (the viewport is as high as the headings and entries that fit, the rest stays empty below it, and it scrolls from the top of one item to the top of another, ending where the last item ends).

## As built: the contract step

Where the contract step differs from, or adds to, what is written below:

- **Core:** `CategorySeries` also exposes `HasWeights`, `Map` (a grouping's map), `StorageBytes` (the bytes of codes, count bytes, weights and index it keeps, for the formats' per-series memory checks) and `Reader()`, a struct that reads units one after another in time proportional to their values, which the JSON writer needs since a variable layout's lookup by unit costs up to 1,023 steps. A builder is used once (`Build` seals it), rejects a weight of 0, and checks codes at `Build` as designed. `Build` accepts an `x` that equals `xSeries.X` by content as well as by identity. A unit of a fixed layout is coded whole or not at all past the cap.
- **JSON:** `valuesPerUnit` is written for every fixed layout of more than one value (the example above lacked it, the prose asks for it).
- **Text:** a category's line is its name followed by "Share Over Time", so "Long, start and stop Share Over Time"; streams are numbered only where more than one stream anchors series to the same node.
- **GUI:**
  - Navigation is the list of the review round, not chips; the Coding Summary sits under a "Summary" heading before the concept headings.
  - A hover lists "values" (`24 values`), not the series' noun, since a value label such as "granule and channel" has no plural; it lists the rarest category first and at most eight.
  - A category with `IsReserved` is the invalid one that is drawn hatched (the first version recognized the name; the review rejected that). Under Avalonia 12's high contrast preference (`PlatformSettings.GetColorValues().ContrastPreference`) every second category is hatched too.
  - A strip column of many frames selects a frame that holds a category its ticks mark (a category in one frame of the column only); the ticks share the column's pixel, so clicking again steps to the next such frame. The first version took the first frame of the column's rarest category, which left a lone category unreachable where another category was as rare in the same column (found by CI on Windows and macOS, where the plot's width put the two in one column).
  - The Channel Bit Share view is built in the review round (see above), with a scripted series.
- **Tests:** the summary table `category-summary.json` has a schema of its own (`form` entries or flag, `anchor`, `denominator`, `withCategory`, `findings`), documented in `docs/development.md`, and its harness is tested on reports built in the test. `ScreenshotTests` run the category views on a parser that wraps the MPEG one and adds test-only series.
- **Golden check:** the change file's schema is in `scripts/ci/golden_additions.py` and `docs/development.md`; its CI step is in the `fixtures` job, which runs when a golden or change file changes, with a checkout two commits deep.
- **Palette validator:** the `dataviz` skill's validator was run on the two palettes against `#ffffff` and `#000000`; its output matches the figures below and is in `docs/gui.md`.

### Review round and the lead's navigation (2026-10-22)

After the independent review of the contract step, and by the project lead's decision:

- **Navigation:** the chip rows are replaced by a vertical list on the left of the graph area, grouped under the concept headings ("Summary", "Stereo Coding", "Block Switching", "Coded Bandwidth", "Bit Distribution", "Other"), with the chart in the rest of the pane. One entry stands for one quantity, with a view switcher for its forms: a category series offers over time, frames and summary; a numeric quantity per frame, over time and, for the bitrate, its distribution. The lead chose a left list over a dropdown; the dropdown is the fallback if the screenshots do not look right.
  - Each form is a `Graph` of its own (`CategoryGraph` is made for one form; `GraphChoice` is the entry, `GraphFormChoice` a form), and the remembered form is per kind (`GraphForm`: per frame or frames, over time, summary).
  - The forms of a numeric quantity are linked by an explicit field, `Series.Quantity`, written to JSON as `quantity` only where set, so `schemaVersion` stays 1. No parser sets it yet, so no golden changes; until a format does, a stream's one per-frame and one per-second series in the same unit (kbps, bytes) are paired by unit and axis, never by name. A pull request that sets `Quantity` on its series adds `quantity` to its goldens with a change file entry.
  - One font size: headings, entries, the view switcher and the chart's text are the app's size, a heading set apart by weight and color; only the tick labels along the axes are a point smaller (12), because they sit close together; the one-line captions above the plots are the standard size.
- **`Category.IsReserved`** replaces recognizing the name "Reserved" (`isReserved` in JSON where true).
- **Channel Bit Share view** is built here (`ShareSplitGraph`, the form "First and Second"): per interval a 100% column of two segments, the first channel at the bottom, the 50% line marked, from the per-second series of a quantity in percent with the concept "Bit Distribution". Tests drive it with a scripted series.
- **The summary harness** reads the tallies as they are: the entries are text ("4 granules (1.7%)"), so the harness, which is test code, parses the leading count, and an entry a tally omits is 0. The alternative, retyping the entries as integers with the share as text, would change every golden for a test's convenience. The tallies of a group's regions are summed, found by the table's anchor name below each region.
- **The golden check** reads a golden as the array of reports it is, takes additions back before it reattaches moved nodes, and can add a key to existing series elements.
- **Layout:** the pane starts at half the window height with a remembered splitter position and a minimum height; the plot keeps at least about 110 pixels (a strip what its rows need), the legend folds first, a strip that cannot give each row eight pixels becomes one stacked row, and the rest scrolls; percent ticks follow the room between them.
- **Capitalization:** names in the graph pane (headings, entries, the view switcher and its "View" label) are Title Case; values and prose are sentence case.
- **Rounding:** the GUI's shares round half to even as the tree and the text do.
- **Palette:** the muted first step is a mix of 0.7 with the surface, which passes the validator in both modes (0.55 did not).
- **Builder:** a refused unit changes no count, values without a category are not counted as uncoded, and a time index's starts must be finite and nondecreasing.

## As built: the MPEG step

What the MPEG pull request built, and where it differs from what is written above:

- **Category series** (all derived at the end, in the pass that builds the bitrate series, from the kept frame records): "Channel Modes", "Stereo Coding" (concept `stereoCoding`), "Joint Stereo Modes" (Layer III), "Intensity Stereo Bounds" (Layers I and II, ordered), "Block Usage" (`blockSwitching`, a fixed layout of granules times the channels of the stream: 4 values per MPEG-1 frame, 2 in MPEG-2 and 2.5, and 2 or 1 in a stream without a two-channel frame) and its grouping "Block Sizes". Each is anchored to its tally node, a series exists where its tally node does (a file without joint stereo frames has no "Joint Stereo Modes"), and all share the stream's time index and the x values of "Bitrate".
- **Numeric series:** "Coded Bandwidth" (Hz, `codedBandwidth`, Layers I and II) and "Channel Bit Share" (%, `bitDistribution`, streams with a two-channel frame), each per frame and per second ("Over Time": the second's highest bandwidth, bits over bits for the share), with the quantities of the same names. "Bitrate" and "Bitrate Over Time" get the concept `bitDistribution` and the quantity `Bitrate`, "Reservoir Fill" and "Reservoir Fill Over Time" the same concept and the quantity `Reservoir Fill`.
- **New tally nodes:** "Stereo Coding" (frames), "Block Sizes" (granules), "Coded Bandwidth" (children "Lowest", "Most Common", "Highest", in whole hertz) and "Channel Bit Share" (children "Lowest", "Overall", "Highest", tenths of a percent). The two last are read from the kept audio data and side information when the statistics are built (`FrameFigures`), not counted while the frames are read; the series use the same function.
- **Deviations from the design above:**
  - The categories of "Joint Stereo Modes" are in the order of "Stereo Coding" (left/right, mid/side, intensity stereo, both), not in the mode extension's order the tally lists them in, so that a category has the same color in both charts; the harness matches entries by name.
  - A value of "Coded Bandwidth" is in whole hertz, rounded half to even ((subband + 1) times the sample rate over 64, 689 Hz for subband 0 at 44,100 Hz), and a frame with no allocation has 0 Hz. A frame whose allocation and scalefactors do not fit its bytes has no value, and neither has a frame of "Channel Bit Share" whose channels have no bits, or a mono frame: the series then has its own x values, else the bitrate series' (the budget's 8 bytes per value). A second without a value has no point in the per-second form.
  - Where both numeric series lack the same frames (a Layer II frame whose allocation does not fit), they keep one array of x values, so the design's 27 bytes a frame hold; a series that lacks frames the other has keeps an x of its own (8 more bytes a frame).
  - The shares are rounded to a tenth of a percent when they are made (per frame and per second), so the series, the node and the tests agree.
  - The first channel's bits of a Layer I or II frame count the scalefactors above the intensity stereo bound, as the design says, and the allocation and samples above it count toward neither channel.
  - `Series.Concept` is also set on the bitrate and reservoir series (the design left that to the format's pull request), and `Quantity` on all four forms.
  - The Graphs tab of an MPEG file lists "Coding Summary" first, which changes what the default entry is for every MPEG file; the older GUI tests that took the first entry for the bitrate select it by title now. `CategoryTestData` replaces the series the MPEG parser derives with its test ones.
- **Fixtures:** `external-lame-dual-music`, `external-lame-joint-music` and `external-lame-stereo-music` (LAME 3.100, 128 kbps constant, `-t`, six seconds of different content in each channel at 44,100 Hz) and `external-twolame-dual-music` (TwoLAME 0.4.0, Layer II, 192 kbps, dual channel); the manifests hide the frames from the golden (`golden_hides`), which also makes the MediaInfo comparison skip them. The hand-built and encoder-made manifests hold the statistics' new nodes from `stream_stats.py`.
- **Verification:** `MpegCodingSeriesTests` (per frame, from the frames' own nodes), `FrameFiguresTests`, the manifests' statistics, mp3guessenc's mode extension counts and sum and its block counts, and EncSpot's counting (an emulation of `ProcessNextFrame`, since the console port does not print its counts) in `crosscheck_mpeg_fixtures.py`.

## As built: AAC

What the AAC pull request built, and where it differs from what is written above:

- **Category series** (recorded while reading, by `CodingRecorder`, which `BlockLog` owns and the block reader calls from the places that count the same things for the tally): "Window Sequences" (concept `blockSwitching`, a variable layout of one value per channel stream, `valueLabel` "channel stream"), its grouping "Block Sizes" ("Long, start and stop", "Short") and "Stereo Coding" (concept `stereoCoding`, one value per channel pair, "channel pair"; "Left/right stereo", "Mid/side stereo", "Intensity stereo", "Mid/side and intensity stereo", "Reserved", the first four in MPEG audio's order so a category has one color in both formats). Each is anchored to its tally node and shares the stream's time index and the x values of "Bitrate". "Stereo Coding" is created at the first channel pair and fills the units before it with empty ones, so a mono stream keeps nothing for it.
- **Numeric series:** "Coded Bandwidth" (Hz, `codedBandwidth`) per unit and "Coded Bandwidth Over Time" (each second's highest), with the quantity "Coded Bandwidth". "Bitrate" and "Bitrate Over Time" get the concept `bitDistribution` and the quantity "Bitrate", as MPEG audio's do (the design left that to the format's pull request).
- **The node "Coded Bandwidth"** replaces "Bandwidth" and has no value of its own but the children "Lowest", "Most Common" and "Highest" in whole hertz, as MPEG audio's has. They are the unit's figures (the highest top of a band over its channel streams, long or short window), not the most common max_sfb of long windows as the old text was, so the node exists for every stream with a channel stream, where "Bandwidth" existed only for streams whose most common long max_sfb was above 0. The statistics' "Max SFB (Long Windows)" and "(Short Windows)" still show the max_sfb values.
- **The tally changed as designed:** the intensity stereo loop runs over the second channel's bands whether or not the pair has a common window; "Intensity Stereo" is the share of all channel pairs and "Intensity Stereo Bands" the share of the second channels' bands of all pairs (new `ChannelBands`), where both were shares of the common window pairs; "Mid/Side Bands" keeps its denominator but no longer counts bands where either channel uses noise substitution (codebook 13, 14 or 15 of the first channel, 13 of the second; below). The "Intensity Stereo" node is always in the "Channel Pairs" block of a stream with channel pairs, as a share that may be 0%, where it appeared only with intensity stereo bands; "Intensity Stereo Bands" and "Out of Phase" still need one. The two nodes appear for a stream with intensity stereo in a pair without a common window even where no pair has one. "Stereo Coding" counts "channel pair"s, the unit of "Channel Pairs" being "element"; its entries are the series' categories, as is "Block Sizes".
- **Deviations from the design above:**
  - The coded bandwidth is recorded per unit as a 4-byte whole hertz while reading and becomes the series' 8-byte values when the stream's series are built, so a stream holds 12 bytes a unit for it at its peak and 8 after.
  - The pair classification gives "Reserved" before intensity stereo only for a pair with a common window, where ms_mask_present exists; a pair without one has no mask.
  - An ADIF stream whose last block breaks has that block as a unit of the series (its complete channel streams are in the tally) though it has no point of the bitrate series; the series then have the blocks' offsets for x and no `xSeries`.
  - A series' value counts past 254 are coded in the first 254 and counted as designed; the test builds a block of 300 of the smallest single channel elements for it.
  - The summary table gives a series that two formats share a list of denominators, of which the first found counts (`CategorySummary`): "Stereo Coding" (`["Channel Pairs", "Frames"]`) and "Block Sizes" (`["Block Usage", "Window Sequences"]`). AAC's frames in no stream are not read, so no finding is listed for its rows.
  - **Mid/side excludes noise substitution bands.** The first version counted a band as mid/side whenever ms_used was set and the second channel was no intensity stereo band. A review showed FFmpeg 6.1.1 (`apply_mid_side_stereo`) skips a band when either channel uses codebook 13, 14 or 15 (`band_type >= NOISE_BT`). The rule is FFmpeg's alone: faad2 (`ms_decode`) skips only the first channel's noise bands and the second channel's intensity stereo bands, and fdk-aac (`MapMidSideMaskToPnsCorrelation`) only bands where both channels use noise substitution. The tally, "Stereo Coding", the fixture model and the faad2 cross-check apply FFmpeg's rule (the cross-check reconciles faad2's decision with it). The goldens of the FAAC files that use ms_used on noise bands change in "Mid/Side Bands".
  - **ms_mask_present 2 and the sign of intensity stereo.** Only FFmpeg inverts the sign of an intensity stereo band for ms_mask_present 2; faad2 does so for 1 only, and fdk-aac (v2.0.2, `stereo.cpp` lines 1154-1160) clears the mid/side flags before intensity stereo is applied, so it agrees with faad2. No specification text was available to settle it; "Out of Phase" follows faad2 and fdk-aac, and the disagreement is stated in its description and in the intensity stereo article. The first version followed FFmpeg, on the belief that fdk-aac did too.
  - **Intensity stereo without a common window.** FFmpeg and faad2 apply it; fdk-aac applies it only with a common window (`channel.cpp` lines 228-231), and its `stereo.cpp` quotes ISO/IEC 14496-3 4.6.8.2.3 as allowing the codebooks only there. The classification keeps counting such a pair as intensity stereo, as the two decoders decode it, and the new warning `aac.intensity-without-common-window` reports it, worded as what fdk-aac's source says.
  - `golden_additions.py` gets small fixes, in a commit of their own: a rename counts only where the old report has the old name (AAC's "Coded Bandwidth" is a rename in some goldens and an addition in the others, and an entry cannot say both), and an array a stream has empty in both reports is left alone. The review added three more: a renamed node that survives under its old name fails, the `changed` entries of a renamed node are listed once under the new name, and an added node whose children are not listed (unless the old report has them) fails.
- **Fixtures:** no encoder-made ones. The hand-built `adts/stereo-coding` and `adts/stereo-coding-reserved` (pairs of every coding, with and without a common window, with and without a mid/side flag on intensity stereo bands, and ms_mask_present 3) come from `aac_block_fixtures.py`, whose manifests hold the codings a Python function computes by the specification's rule. The review added `adts/stereo-coding-elements` (several pairs, a single channel element and a low frequency element in one unit) and noise bands with ms_used to `stereo-coding`, and Window Sequences, Block Sizes, Stereo Coding and Coded Bandwidth to the manifests of the hand-built ADTS fixtures. The goldens of 207 files change (aac 52, adts 54, matroska 20, mp4 82) and two are new; 11 of them change the share values of "Intensity Stereo" and "Intensity Stereo Bands" (FAAC 1.x and 2.1 and ffmpeg's encoder, whose pairs mostly lack a common window), the three FAAC 2.x files change their "Mid/Side Bands" value (faac-2.0-he-24k 2.1% to 0.1%, faac-2.1-he-24k-adts 2.3% to 0.1%, faac-2.1-he-51-adts 4 to 0) because FAAC writes ms_used on noise substitution bands, and five gain the "Intensity Stereo" node (no intensity stereo band, 0%).
- **Verification:** `AacCodingSeriesTests` (per unit against the nodes of the unit's own blocks, on every MP4, ADTS and ADIF fixture), the faad2 trace per unit in `crosscheck_mp4_fixtures.py` (the pair classification from faad2's own mid/side decisions and codebooks, and the bandwidth from the trace, with the 960-sample band tables for a 960-sample frame; 124 fixtures, 3,865 units, the others reported as skipped with their reason), `MemoryTests` at two unit counts and the GUI tests of `AacGraphsTests`. FFmpeg 6.1.1's `decode_cpe` and `apply_intensity_stereo` were read for the intensity stereo rule: `apply_intensity_stereo` runs outside the `common_window` block, over the second channel's bands, and `ms_present == 3` is an error.
- **Memory, measured** on linux-x64 (a stereo unit adds 5 bytes of codes and count bytes and 8 for the bandwidth, 13 as designed): an ADTS frame of ffmpeg's encoder keeps 126 bytes with a peak of 140 (116 and 133 before) against the bounds 220 and 250, and an MP4 sample 74 with a peak of 113 (60 and 100) against 110 and 180, which is the figure the pull request measures against (the design's 110 per sample, not the larger one). No bound is raised. The two-count tests read 124 bytes kept per further ADTS frame and 67 per further MP4 sample. The two-count tests assert the kept growth only: their peak half read 0 whatever the code did (the sampler never sees the transient) and was removed; the single-count tests keep the peak bounds above. The bandwidth series' growth is asserted on the bytes of its array.

## As built: AC-3 and E-AC-3

What the AC-3 pull request built, and where it differs from what is written above. The project lead approved the two departures marked below on 2026-10-23.

- **Category series** (recorded while reading: the block reader decides each flag in `CommitBlock`, `FrameResult` carries them per block, `Program.Add` hands them to `CodingRecorder`, which keeps one byte of flags per block read and one count and end bin per unit, and `Program.Complete` ends the unit with the figures' point; the series are made from those bytes when the stream's figures are built): "Blocks With Block Switching" (concept `blockSwitching`), "Blocks With Coupling" and "Blocks With Rematrixing" (`stereoCoding`) and, for E-AC-3, "Blocks With Spectral Extension", each a flag series of "With ..." and "Without ..." whose first count is the node's value. A series exists where its tally node does (coupling from the independent frame's acmod 2 and up, rematrixing at acmod 2, spectral extension in E-AC-3). Each is anchored to its node and shares the stream's time index (new in `StreamFigures`, whose intervals are whole seconds) and the x values of "Bitrate", which gets the concept `bitDistribution` and the quantity "Bitrate" with "Bitrate Over Time".
- **Numeric series and node:** "Coded Bandwidth" (Hz, `codedBandwidth`) per program frame and "Coded Bandwidth Over Time", each second's highest, with the quantity "Coded Bandwidth", and the statistics node "Coded Bandwidth" with "Lowest", "Most Common" and "Highest" in whole hertz, after "Auxiliary Data". The end bin of each block is one byte while reading (at most 253; 255 for no value), the unit's highest is kept, and the values become 8 bytes each when the series are built.
- **Departures from the design above:**
  - **Spectral extension is under "Coded Bandwidth",** not "Stereo Coding" (approved by the project lead, 2026-10-23). The design's concept table lists no heading for it, and the band above its begin frequency is rebuilt, not coded, which is what that heading shows. It is a category series under a heading that otherwise holds numeric series; the survey row and the concept table carry it.
  - **A block that is not 2/0 is "without rematrixing", not `None`** (approved by the project lead, 2026-10-23): in a program whose independent frame is 2/0 a dependent frame of another audio coding mode adds blocks to "Audio Blocks Read" (the tally of its node counts 2/0 blocks only), and a flag series whose total is that number could not hold `None` for them (`coding-eac3-dependent`). The 1+1 mode has no rematrixing series at all, since its program has no such node; the survey row and the 1+1 paragraph say so.
  - **The block tallies count blocks read to their end.** The first version recorded the flags where the tally counted them, and a block that breaks later (an invalid field, mantissas past the frame, the adaptive hybrid transform or enhanced coupling) counted in "Blocks With ..." though it is in no "Audio Blocks Read", so a flag series, whose total is that number, could not equal its tally. `CommitBlock` now counts, once a block's mantissas are stepped over, every counter of the Statistics node that is per block: the four flags, rematrixing and its bands, dynamic range gain, delta bit allocation and the exponent strategies, so that "blocks read" means one thing in the node. Of the goldens that change, two change a value: "Bands Coded as Sum/Difference" of `bad-blocks` (its denominator 84 becomes 72) and "Exponent Strategies" of `truncated-in-skip` (126 becomes 120, "Reuse" 102 becomes 96); the change file lists each for its file alone (an entry may name the goldens it applies to, new in `golden_additions.py`).
  - **The layout follows the units, and never contradicts a tally.** An AC-3 stream whose program frames have six blocks or fewer is a fixed layout of six values (empty for a block not read); an E-AC-3 stream is variable; and so is an AC-3 core with E-AC-3 dependent frames, which ETSI TS 102 366 J.1.5.2.2 specifies as the form of a program of more than 5.1 channels (the AC-3 bit stream is independent substream 0, and the report names the stream E-AC-3). The first version chose the layout from the independent frame's key in the constructor and truncated a unit past six values without a finding, so that case's series said no block ever switched while the tree said 16; the review found it. The layout is now chosen when the figures are built, from the longest unit, which needs a byte of flags for each block while reading instead of the codes. `coding-ac3-core-dependent`, `coding-eac3-nine-frames` (54 values) and tests hold it.
  - **The design's fixed-layout figure holds:** a stereo frame keeps 3 series of 6 codes (18 bytes) and the 8 of the bandwidth (26), and an E-AC-3 program frame of an independent and a dependent frame of six blocks 4 × (12 + 1) and 8 (60).
  - **No grouping**, since AC-3 has no block sizes to group; "Block Switching" holds the one series.
- **Fixtures:** the encoder-written ones cover block switching (aften's 1 of 96 blocks), coupling in every block or none, mixed rematrixing and spectral extension in every block or none. The hand-built `coding-*` fixtures (`make_ac3_fixtures.py` on `ac3_model.py`) add block switching in single channels, rematrixing flags sent, reused and cleared, coupling that starts, stops and changes range within a frame with the rematrixing band counts that follow, 3/2 with LFE and coupling of five channels, 1+1, E-AC-3 frames of 6, 3, 2 and 1 blocks with spectral extension that starts and stops, E-AC-3 program frames with dependent frames of 2/0 and mono and a changing number of blocks, a program frame of nine frames, an AC-3 core with dependent frames, and spectral extension that starts late with the default band structure. Their manifests hold the expected values, computed from the options each frame was built with.
- **Verification:** `Ac3CodingSeriesTests` (every unit of every fixture against its frames' nodes; the manifests' values; shape; a breaking block; the core with dependent frames; 54 values), a traced liba52 for AC-3 (below), `MemoryTests` at two counts, `Ac3GraphsTests`. **The per-block reference is a patched build of liba52 0.7.4's original source**, as the design suggested (`a52_trace_tool.py`): it prints blksw, chincpl, rematflg and the end bins after each `a52_block()`, and agrees with the series on 233 frames of 23 fixtures. E-AC-3 has no per-block reference; the values rest on the model of the manifests and on the nodes.
- **FFmpeg and the default band structure** (found by the project's fixtures, explained by the review): TS 102 366 E.1.3.3.7 loads the default spectral extension band structure at the first block of a frame that uses spectral extension without sending one; FFmpeg 6.1.1 loads it in block 0 only (`ac3dec.c`, `decode_band_structure`, lines 812-813), so a frame whose spectral extension starts later without a structure of its own is read with whatever structure the decoder holds: zeros in a fresh decoder (as in the per-packet check), eight bands of coordinates for sub-bands 5 to 13 where the default gives five, and it fails; a running decoder reuses the structure an earlier frame loaded in block 0, and may or may not fail ("exponent -1 is out-of-range"). The analyzer and the model follow the specification. The frames that must decode in ffmpeg send their band structure explicitly; `coding-eac3-spx-default` keeps the default, and its manifest expects ffmpeg to reject the frames that start late (an ffmpeg bug, which the decode check would notice if a newer build fixed it). Sub-band ranges are written as Annex E counts them, begin to end with the end exclusive.
- **Follow-up, not built:** a per-block reference for E-AC-3: a traced FFmpeg build that prints `block_switch`, `cpl_in_use`, `rematrixing_flags`, `spx_in_use` and `end_freq` per block of `decode_audio_block`, like the liba52 trace. It must set the band structure as the specification does, or its numbers repeat the misreading above.
- **Memory, measured** on linux-x64: a mono AC-3 frame keeps 85 bytes (peak 89 to 102 over runs, the series and the flags that make them existing together) against 70 and 64 before, bounds 220 and 300; the two-count test reads 125 bytes kept per further stereo frame (bound 220), and 158 per further E-AC-3 program frame with a dependent frame (151 before the fixture sent explicit band structures, which are kept bits), six blocks each, against the bound 190 the test sets (the design gave none). No existing bound is raised. The tiny E-AC-3 frames are unchanged (46 kept: their blocks overrun, so no series grows).
- **GUI:** the list of the Graphs tab cut the longest entries ("Blocks With Block Switching", "Blocks With Spectral Extension") at its width, with no marker. Entries now wrap at the list's width and the tooltip names the entry before its description (a small change to `DetailWindow.axaml` and `GraphChoice.ToolTipText`, with a test), so wave 2's long names ("Subframes With Wasted Bits") read whole too.
- **Capitalization, a proposed rule** (for the lead; `CLAUDE.md` is unchanged): the names of series are text from the analysis and keep the spelling of their tally nodes, so `CapitalizationTests` should exempt series titles as a class instead of listing each ("Blocks With ..."), which wave 2's "Subframes With Wasted Bits" would otherwise add to. Until then the four AC-3 names are listed.

## Scope

What the lead asked for:

- Graphs over time of the coding choices each frame makes:
  - the stereo coding of joint stereo frames (left/right, mid/side, intensity stereo, mid/side and intensity stereo);
  - the distribution of long and short blocks (long, start, short, stop and mixed for Layer III; the window sequences for AAC);
  - whatever else the parsers read that is worth a graph.
- Summaries over the whole stream, as EncSpot's window shows them: the share of mid/side, intensity stereo and simple stereo frames, of long and short blocks, and so on. EncSpot's shares are over all frames, so this design delivers shares over all frames ("Stereo Coding", "Block Sizes"), beside the existing tallies that cover joint stereo frames only and that count start and stop blocks apart.

What the plan already settles, and this design keeps:

- Both front ends are thin views over the same result model, "so neither can show something the other cannot" (plan, "CLI and GUI"). Every graph and summary the GUI draws is therefore in the JSON report, and its summary in the text tree.
- The GUI draws its own graphs, from the streams' series alone, without a charting library (plan, "CLI and GUI"; [gui.md](../gui.md), "Graphs").
- Front ends read kinds and streams, never node names (`CLAUDE.md`).
- Reports keep compact records per frame and a bounded number of bytes per frame (`MemoryTests`).

What is not in scope:

- Anything that needs the entropy-coded data the parsers do not walk: Layer III scalefactors and Huffman-coded spectra, DTS core subframes, Vorbis floors and residues, Opus SILK and CELT, Musepack SV8 frames inside packets. The plan puts these under "Later" ("An entropy pass, format by format").
- Anything from decoded audio (lowpass measured from the spectrum, waveform, spectrogram), which the [decoder design](decoders.md) leaves to host-side PCM analyses.

Sources read for this design:

- The code at 2310aa3: `StreamInfo` and `Series`; the MPEG parser's `MpegAudioParser.Tally.cs`, `.Statistics.cs`, `.Streams.cs`, `Layer3SideInfo.cs` and the frame record `Entry`; AAC's `CodingTally`, `CodingStatistics`, `StreamFigures`, `StreamSetup`, `BlockReader` and `RawAacParser`; AC-3's `Program`, `FrameResult`, `FrameSyntax.Blocks.cs` and `StreamFigures`; DTS's `UnitSyntax.*`, `Facts`, `DtsStream`; Musepack's `Sv7Parser`, `OldStreamParser`, `Sv8Parser`; Vorbis's, Opus's and Speex's records and reports; WMA's `StandardDecoder`, `StandardStream`, `StandardReport`, `ProRecords`, `ProReport`, `VoiceRecords`; WavPack's `Model`, `Reader`, `Summary`; the host's JSON and text writers and `ReportSummary`; the GUI's `Graph`, `GraphsViewModel`, `GraphView`, `Palette`; `MemoryTests`, `ScreenshotTests`, `GraphViewTests`; the cross-check tools in `scripts/fixtures`.
- The vendored references: minimp3 (`third_party/minimp3/minimp3.h`: `L3_huffman`, `L3_reorder`), EncSpot (`third_party/encspot/lib/Mp3File.cpp`: `ProcessNextFrame`; the vendored source is the console port, `third_party/encspot/README`), mp3guessenc.
- Sources the independent review read, whose statements this design cites where it relies on them: FFmpeg 6.1.1 (`aacdec_template.c` `decode_cpe`, `ac3dec.c`), liba52 0.7.4 (`a52.h`, `parse.c`).
- The decoder contract under way on `feature/decoder-contract` (its working tree, read only), for the fields it adds to `StreamInfo`, the writers and `ReportSummary`.
- The specification sections the parsers cite in their code and descriptions; where this design cites a section no parser cites, it says so.
- For the lossless formats of wave 2 (decision 11), read at f9b67fb:
  - the FLAC parser (`Model.cs`, `State.cs`, `Summary.cs`, `StreamFigures.cs`, `SubframeSyntax.cs`, `Nodes.cs`), `MemoryTests.ManyFlacFramesStayCompact`, `crosscheck_flac_fixtures.py` and the FLAC goldens;
  - RFC 9639, sections 4.1, 4.2, 9.1.1, 9.1.3, 9.1.6, 9.2.1, 9.2.2, 9.2.5, 9.2.6 and 9.2.7;
  - FFmpeg 6.1.1 from the archive `ffmpeg_sources.py` pins: `apedec.c`, `libavformat/ape.c`, `tta.c` (6.1.1 has no `ttadec.c`; the TTA decoder is `libavcodec/tta.c`) and `libavformat/tta.c` from its list, and `adpcm.c`, `adpcm_data.c`, `riff.c` and `aiffdec.c` extracted from the same archive, which its list does not hold;
  - Monkey's Audio SDK 13.42 (the archive the decoder design pinned then, SHA-256 0a763b35…; `Prepare.cpp` was re-read in 13.44, SHA-256 0c5c1643…, which the decoder now follows): `Prepare.h`, `Prepare.cpp`, `APECompressCore.cpp`, `APECompressCreate.cpp`, `APEDecompressCore.cpp`, `APEHeader.cpp`, `APEInfo.h`, `Old/UnMACOld.cpp`;
  - libtta-c 2.3 (SHA-256 68ba6db2…): `libtta.c`, `libtta.h`;
  - `flac -a` of flac 1.4.3, run for this design on three committed fixtures; CI pins flac 1.5.0, whose output was not run here.

## The design in short

- A new type in `Core.Abstractions`, `CategorySeries`, holds the category of each value of each unit (a frame, access unit, packet or block): compact codes of one byte each, a unit carrying as many values as it has (a count byte per unit where that varies), the categories with names and descriptions, and the counts over the whole stream. It sits beside `Series`, which keeps holding numbers. A series can also be a grouping of another one (long, start and stop blocks as "long") that costs no bytes per value.
- The whole-stream summary stays what the parsers report, the tally nodes under Statistics, and gains two nodes over all frames: "Stereo Coding" (MPEG frames, AAC channel pairs) and "Block Sizes" (long against short). Each category series carries the categories of the tally node it is anchored to, under the same names. The counts over the series are computed by the shared type, and a data-driven test holds them equal to the tally.
- Codes are derived at the end from the records the parser keeps already wherever they hold the fields (MPEG, Vorbis, Opus, Speex, Musepack, WMA, WavPack); only AAC and AC-3, which keep no per-frame fields, record them while reading.
- Coded bandwidth over time is a numeric `Series`, "Coded Bandwidth" in Hz, wherever a header or the coded syntax states it without decoding.
- JSON adds a `categorySeries` array to each stream, omitted when empty, so `schemaVersion` stays 1 and no golden changes until a parser produces a series. The text tree prints each category's share per second after its tally node. The CSV does not change in this rollout.
- The GUI draws four forms of a category series (share per second, a per-frame strip that keeps rare categories visible, a summary table like EncSpot's) in a fixed palette with a rule for series that have more categories than colors, always with names and shares in text.
- Rollout: the contract after the decoder contract merges, MPEG as the reference, then the other formats in parallel.

## The survey

What each parser reads per frame, packet or block today that is worth a graph or a summary.

- **Read** says whether the parser reads the field now; **Tallied** whether its Statistics node counts it now.
- **Kept per unit** is what the design adds per frame, packet or block to what the parser keeps now: one byte per value of a category series (plus a count byte per unit where the number of values varies, and a weight byte per value where a weight is used), 8 bytes per value of a numeric series. "Derived" means the codes are computed at the end from the kept records; "recorded" means the parser adds them while reading. The x values (frame offsets) are shared with the stream's per-frame bitrate series and cost nothing more.
- **Status:** "code" is verified from the parser's code at 2310aa3 (FLAC's at f9b67fb), "spec" from a specification or reference source cited there, **unverified** otherwise. Monkey's Audio, TTA and WAV have no parser yet, so their rows are "spec" at most, and "planned" in **Read** and **Tallied** means what the parser of the decoder design's rollout step 3 is to read.
- **Wave:** 1 is the first rollout, 2 the second wave, "later" waits for the entropy pass or a parser change; "not a series" is a fact of the whole stream or nothing a parser can split per frame.

### MPEG audio, all layers

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Channel modes | header mode, ISO/IEC 11172-3 2.4.2.3 | yes, in the frame record's header | "Channel Modes" | 1 byte per frame, derived from the kept header | code | 1 |
| Joint stereo modes, Layer III | mode_extension: bit 1 mid/side stereo, bit 0 intensity stereo (2.4.2.3) | yes, in the kept header | "Joint Stereo Modes" (joint stereo frames only) | 1 byte per frame, derived; `None` for a frame that is not joint stereo, which the tally does not count | code | 1 |
| Stereo coding, all frames (new) | the channel mode with mode_extension: left/right stereo, the three joint stereo codings, and dual channel and mono frames as entries of their own | yes, in the kept header | new node "Stereo Coding" over all complete frames | 1 byte per frame, derived | code | 1 |
| Intensity stereo bounds, Layers I and II | mode_extension: bands 4, 8, 12 or 16 to 31 (2.4.2.3) | yes, in the kept header | "Intensity Stereo Bounds" | 1 byte per frame, derived | code | 1 |
| Block usage, Layer III | block_type, window_switching_flag, mixed_block_flag per granule and channel (2.4.1.7, 2.4.2.7) | yes, side information kept in the store | "Block Usage", granules counted per channel: long, start, short, mixed, stop, reserved | 4 bytes per MPEG-1 stereo frame (2 granules × 2 channels), 2 for MPEG-2 stereo, 1 or 2 for mono; derived by reading the block_type bits at their known positions in the stored side information | code | 1 |
| Block sizes, all granules (new) | the same fields, grouped as EncSpot counts them: long (with start and stop), short, mixed, reserved | yes | new node "Block Sizes" | none: a grouping of "Block Usage" | code | 1 |
| Coded bandwidth, Layers I and II | the highest subband with a bit allocation other than 0 in either channel; 32 subbands of fs/64 each | yes, the allocation is in the stored audio data | no | 8 bytes per frame (the numeric series), derived at the end from the stored allocation | code; the Hz conversion spec | 1 |
| Channel bit share, Layers I to III (new) | how the frame's audio bits divide between its two coded channels. Layer III: part2_3_length of each granule and channel (2.4.1.7, 2.4.2.7), summed over the frame's granules. Layers I and II: the bits the frame gives each channel's subbands (allocation, scale factors, samples) from the allocation and scale factor selection; above the intensity stereo bound only the allocation and the samples are coded once for both channels (ISO/IEC 11172-3 2.4.1.5 and 2.4.1.6), so only those shared bits are left out of both shares and of the total; the scale factor selection and scale factors stay per channel and count into each channel's share | yes: part2_3_length is in the kept side information (`Layer3SideInfo`), the allocation and scale factors in the kept Layer I and II audio data, which holds the frame up to its samples; the grouped Layer II samples' bit counts follow from the allocation alone (below) | no | 16 bytes per two-channel frame (a numeric series with its own x), derived at the end; absent in a mono stream | code (Layer III); spec and `minimp3_side_info.c` (Layers I and II) | 1 |
| Scalefactor scaling, Layer III | scalefac_scale per granule and channel (2.4.2.7) | yes | share of granules ("Scalefactor Scaling") | 4 bytes per frame | code | 2 |
| Global gain, Layer III | global_gain per granule and channel | yes | smallest and largest per channel | 8 bytes per value as a numeric series | code | 2 |
| big_values region end, Layer III | big_values × 2 lines (2.4.2.7); lines above it are the count1 region, whose end needs Huffman decoding | big_values yes; count1 no (the parser does no Huffman decoding) | no | 8 bytes per frame | code; minimp3 `L3_huffman` reads count1 until part2_3_length's bits run out | later |
| Intensity stereo bound, Layer III | where the right channel's zero region begins (2.4.3.4) | no: it needs the Huffman-coded spectrum | no | | spec, from minimp3's `L3_intensity_stereo` | later |

What big_values tells and what it does not:

- It is the number of pairs of frequency lines, from the lowest, in the big-values region, where values may exceed 1 (the parser's own description of the field).
- Above it lies the count1 region of quadruples of -1, 0 or 1, which runs until the granule's part2_3_length bits are spent (minimp3 `L3_huffman`). Its end, and with it the highest line that is not zero, needs Huffman decoding.
- So big_values × 2 bounds the highest line whose magnitude exceeds 1, but neither the coded bandwidth nor the highest nonzero line: lines of ±1 may lie above it, and the big-values region may end in zeros.
- In short blocks the lines are coded band by band, each band's three windows one after the other (minimp3 `L3_reorder` undoes that order), so the line index still maps to frequency at the granularity of a scalefactor band.
- It does follow an encoder's lowpass where the encoder filters the input, but a graph of it named as bandwidth would mislead. It waits for the entropy pass, where the end of the count1 region gives the real highest coded line. EncSpot recorded it (`bigvalues` in `ProcessNextFrame`, for frames whose last switched granule is short, 0 otherwise), so a reader of its output may expect a graph of it; Decision 6 covers that.

How EncSpot counts, from its source (`Mp3File.cpp`, `ProcessNextFrame`), which the cross-check and the new all-frames nodes need:

- Its mode counts take the mode extension of joint stereo frames and count every other frame (stereo, dual channel, mono) as mode 0. Its mode 0 share is therefore the report's "Stereo Coding" entries "Left/right stereo", "Dual channel" and "Mono" together; its modes 1 to 3 are the report's mid/side, intensity stereo, and both.
- It counts long, short and mixed granules; start and stop blocks count as long, as the report's "Block Sizes" entry "Long, start and stop" does. The existing "Block Usage" keeps them apart.
- Its per-frame lists keep the mode extension of every frame and one block type per frame, the last switched granule's. What the Windows program drew from them cannot be checked: the vendored source is the console port.

### AAC (ADTS, ADIF, MP4, Matroska)

The AAC codec parser serves every container that routes AAC to it. There is no LATM or LOAS parser (no source in the tree mentions either), so LATM has nothing to survey.

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Window sequences | window_sequence of each channel stream (ISO/IEC 14496-3 4.4.2, ics_info): only long, long start, eight short, long stop | yes, every raw data block of Main, LC, SSR and LTP | "Window Sequences", per channel stream | variable: 1 byte per channel stream of every raw data block of the unit, recorded, plus a count byte | code | 1 |
| Block sizes, all channel streams (new) | the same field, grouped: long (with start and stop), short | yes | new node "Block Sizes" | none: a grouping of "Window Sequences" | code | 1 |
| Stereo coding of channel pairs, all pairs (new) | ms_mask_present and ms_used (4.6.8.1); intensity stereo codebooks 14 and 15 in the second channel (4.6.8.2); rule below | yes | new node "Stereo Coding" over every channel pair; "Channel Pairs" keeps its counts | variable: 1 byte per channel pair of the unit, recorded, plus a count byte | code | 1 |
| Coded bandwidth | max_sfb of each channel stream, taken to the end of its band in the window's swb_offset table (long or short, 1024 or 960 lines), in Hz | yes; the long and short tables are in `StreamSetup` | the most common long max_sfb as "Bandwidth" (text), renamed "Coded Bandwidth" | 8 bytes per unit (the highest over its channel streams), recorded as it is read | code | 1 |
| Window shapes | window_shape: sine, KBD | yes | "Window Shapes" | 1 byte per channel stream | code | 2 |
| TNS, noise substitution, pulse data | tns_data_present (4.6.9), codebook 13 bands (4.6.2, as the `pns` article cites it), pulse_data_present (4.5.2, as `aac-pulse-data` cites it) | yes | shares of channel streams | 1 byte per channel stream each | code | 2 |
| SBR and parametric stereo per access unit | SBR payloads in fill elements | yes | "SBR", "Parametric Stereo" | 1 byte per access unit | code | 2 |
| Bands coded mid/side per access unit | count of ms_used bands over the pair's bands | yes | the share over the stream | 8 bytes per access unit as a numeric series | code | 2 |

- Coded bandwidth from max_sfb is the top of the highest band the channel stream codes, whatever its codebook. Bands coded with the zero codebook, noise substitution or intensity stereo lie below it, and SBR adds content above the core that this does not show. The description says so.
- **The unit** is what shares x with the stream's "Bitrate" series: an ADTS frame (`RawAacParser` adds one bitrate point per frame, for all its raw data blocks together) or an access unit of MP4 or Matroska. It holds the channel streams of every raw data block it contains, so a unit carries a variable number of values. Nothing bounds that number but the 64 KiB a raw data block may span (`BlockLog.MaxBlockBytes`) times the blocks of a frame; `CodingTally.MaxGainChannels` (48) bounds only the global-gain statistics.
- **Classifying a channel pair**, per pair and independent of the window (an earlier draft hid intensity stereo in pairs without a common window):
  - intensity stereo: some band of the second channel uses codebook 14 or 15, in any channel pair;
  - mid/side stereo: at least one band that is **not** an intensity stereo band has ms_used set, ms_mask_present 2 counting as all bands set; it can occur only with a common window. Bands where either channel uses noise substitution do not count (FFmpeg's rule, see "As built: AAC"). In a band of codebook 14 or 15, ms_used inverts the intensity stereo sign (ISO/IEC 14496-3 4.6.8.2.3, and FFmpeg's `apply_intensity_stereo`: `c *= 1 - 2 * cpe->ms_mask[idx]`), so it is not mid/side coding there; the tally already leaves such bands out of "Mid/Side Bands" (`TallyPair`'s `else if (ms)`);
  - both: both conditions hold; neither: left/right stereo;
  - ms_mask_present 3 is reserved by the specification and gets its own category, "Reserved", which takes precedence over intensity stereo.
- **Why:** FFmpeg 6.1.1 (`decode_cpe`, `aacdec_template.c`) reads the intensity stereo codebooks from the section data of every pair and calls `apply_intensity_stereo(ac, cpe, ms_present)` outside its `if (common_window)` block, so a pair without a common window can use intensity stereo. FFmpeg rejects ms_present 3 as an error.
- **The tally changes to match** (`TallyPair` returns before the masks where there is no common window, so "Channel Pairs" does not count intensity stereo in such pairs). The AAC pull request changes it so that:
  - the intensity stereo loop runs over the second channel's groups and `max_sfb` when there is no common window (`TallyPair` uses `first.Groups` and `first.MaxSfb`, which are the pair's only where the window is common);
  - the entries whose denominator is the pairs with a common window ("Intensity Stereo", "Intensity Stereo Bands", which use `tally.CommonWindow`) take all pairs as the denominator; the mid/side entries keep the common-window pairs, as mid/side needs one;
  - the numbers in AAC goldens change (listed with the rename of "Bandwidth"); the node "Channel Pairs" keeps its name and entries.
- Past 2,097,152 frame or block records an ADTS or ADIF stream is only counted (`aac.frames-limit`, `aac.blocks-limit`), and the graphs and coding statistics stop there. The category series stop at the same unit, so the series and the tally still agree.

### AC-3 and E-AC-3

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Block switching | blksw per channel and block (ATSC A/52 5.4.3.1); E-AC-3 only where blkswe is set | yes, every block read | "Blocks With Block Switching", per block (any channel); a value node without entries | 1 byte per block of the program frame, recorded: 6 per frame in an AC-3 stream (a fixed layout); in E-AC-3 the blocks of the independent frame and of every dependent frame, up to 9 frames and 54 blocks (a variable layout, plus a count byte) | code | 1 |
| Coupling | cplinu per block (5.4.3.8) | yes | "Blocks With Coupling", a value node | as above | code | 1 |
| Rematrixing (2/0 only) | rematflg per band and block (5.4.3.20) | yes | "Blocks With Rematrixing", "Bands Coded as Sum/Difference" | as above; a block that is not 2/0 (a dependent frame of another mode in a 2/0 program) counts as without rematrixing, not `None` (approved by the project lead, 2026-10-23) | code | 1 |
| Spectral extension (E-AC-3) | spxinu per block (Annex E 2.3.3.2); listed under "Coded Bandwidth" (approved by the project lead, 2026-10-23) | yes | "Blocks With Spectral Extension", a value node | as above | code | 1 |
| Coded bandwidth | the end bin of each full-bandwidth channel: 37 + 3 × (chbwcod + 12) where it codes its own exponents; for a coupled channel the end of the coupling range (37 + 12 × (cplendf + 3)); bin k is k × fs / 512 Hz | yes: the highest of `_endMant[ch]` of the channels and, where a channel is coupled, `_endMant[Cpl]` (a coupled channel's own `_endMant` is the coupling begin, 37 + 12 × cplbegf) | no | 8 bytes per program frame (the highest over its blocks and channels) | code; chbwcod as FFmpeg `ac3dec.c` and liba52 `parse.c` | 1 |
| Exponent strategies | D15, D25, D45, reuse per channel and block | yes | "Exponent Strategies" | up to 30 bytes per frame | code | later |
| Dynamic range gain, delta bit allocation | per block | yes | counted per block | 1 byte per block each | code | later |

- Spectral extension regenerates the band above its begin frequency from lower bands, so that band is not coded data; the coded bandwidth of a channel in spectral extension ends where spectral extension begins (bin 25 + 12 × its begin sub-band, as the parser sets `_endMant`). The LFE channel is left out.
- **The unit** is the program frame, the point of the bitrate series: an independent frame with the dependent frames that follow it (`Program.Add`, Annex E 2.3.1.2), up to 8 of them. The tallies add the dependent frames' blocks to the program (`Program.cs` runs the block tallies for dependents too), so the series must as well, or it cannot equal the tally on a 7.1 file. The number of blocks per frame can change within a stream (`ProgramKey` does not include it), which is why the number of values varies per unit.
- Blocks the parser does not read (adaptive hybrid transform, enhanced coupling, reduced sample rates; `StopReason`) get no value, as they count in "Audio Blocks" but not in "Audio Blocks Read".
- The tallies above are single values without entries. Their series are **flag series** of two categories (named after the tally, such as "With coupling" and "Without coupling"), whose "with" count equals the node and whose total equals "Audio Blocks Read"; see [The summary over the stream](#the-summary-over-the-stream).
- Per-channel block switching would be 6 × channels bytes a frame and has no tally; it is left for later.
- AC-3's frame record keeps the bits its nodes are built from, not its fields (see [When the codes are made](#when-the-codes-are-made)), so these codes are recorded while reading.

### DTS and DTS-HD

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Coded bandwidth of the core | SUBS + 2 active subbands per channel (ETSI TS 102 114 5.4.3), 32 subbands of fs/64 | yes, the audio coding header of every core frame | no | 8 bytes per frame (the highest over the primary channels) | code | 2 |
| Sum/difference coding | SUMF (front pair), SUMS (surround pair), 5.4.2 | yes (`Facts`: `IsSumDifferenceFront`, `IsSumDifferenceSurround`) | no | 1 byte per frame, recorded while reading (or derived from `Facts` if the pull request keeps it there) | code | 2 |
| Joint intensity coding | JOINX per channel (5.4.3) | read into the syntax walker's array `_joinx` only, not kept in `Facts` | no | 1 byte per channel, recorded while reading | code | 2 |
| Frames by kind and extension | core, XCh, X96, XXCH, extension substream | yes | "Frames With Extension" and the frame counts | 1 byte per frame | code | later |
| Transient modes | TMODE per subband, in the subframes | no (core subframes are "Later" in the plan) | no | | spec | later |

- Joint intensity coding fills a channel's subbands above its active ones from a source channel, so the highest active subband over all channels is the coded bandwidth (the parser's description of SUBS).

### Vorbis

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Block sizes | the mode's blockflag (Vorbis I 4.3.1), with the previous and next window flags | yes, in the packet record (`PacketFlags.Long`, `PreviousLong`, `NextLong`) | "Long Blocks" and "Short Blocks", two sibling nodes; a parent "Block Sizes" is added so the series has one node to anchor to | none: a grouping of "Modes" | code | 1 |
| Modes | mode number per packet | yes, in the record | "Modes" | 1 byte per packet, derived | code | 1 |
| Coded bandwidth | residue_end of each residue (8.6.1) | yes, in the setup header | no | | code | not a series |

- residue_end is fixed per residue, so the bandwidth it implies changes only with the mode a packet chooses; the "Modes" series already shows that. Where the coded spectrum really ends needs the floors and residues, which wait for the entropy pass.
- Coupling (square polar mapping) is fixed per mapping, so a per-packet stereo coding graph would repeat the mode as well.

### Opus

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Modes | TOC config: SILK-only, hybrid, CELT-only (RFC 6716 3.1) | yes, the TOC byte is in the record | "Modes", per frame | derived; 1 byte per stream packet in the series, plus a weight byte (its frames) | code | 1 |
| Bandwidths | TOC config: narrowband (4 kHz) to fullband (20 kHz), RFC 6716 table 1 | yes | "Bandwidths", per frame | as above | code | 1 |
| Frame sizes | TOC config: 2.5 to 60 ms | yes | "Frame Sizes", per frame | as above | code | 1 |
| Channel counts | TOC s bit: stereo or mono packet | yes | "Stereo Coding" today, per stream packet; renamed "Channel Counts" so that "Stereo Coding" names one concept (left/right, mid/side, intensity stereo) in every format | 1 byte per stream packet, derived | code | 1 |

- Opus states its bandwidth in every packet, so its "Bandwidths" is a category series, not a number: the five bandwidths are what the format defines, and their names carry the frequency. It is not "Coded Bandwidth": that name is for the highest frequency the coded data reaches, which for Opus needs SILK or CELT's range decoder.
- The tally counts modes, bandwidths and frame sizes per frame and stereo coding per stream packet. All frames of a packet share its TOC, so a weight of the packet's frames makes the series' counts equal to the tally's.
- What SILK or CELT does inside a frame (CELT's intensity stereo, its band energies) needs the range decoder: later.

### Speex

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Narrowband submode | 4-bit submode per frame (libspeex 1.2.1 `nb_decode`); 0 is a frame without speech data | yes, the 4-byte frame record | frames of each submode per layer | 1 byte per frame in the series, derived | code | 1 |
| Wideband and ultra-wideband submodes | 3-bit submode per layer (`sb_decode`) | yes | as above | 1 byte per frame and layer, derived | code | 1 |

- Ogg Speex states the frames per packet in its header, so a packet is the unit and its frames are the values of the unit; the x values are the packets', shared with the bitrate series.

### Musepack SV4 to SV8

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Coded bandwidth, SV4 to SV7 | used subbands: the highest subband with a resolution other than 0 in either channel, plus 1 (libmpcdec r495 `mpc_decoder_read_bitstream_sv7`, 1.2.6 for SV4 to SV6); 32 subbands of fs/64. Resolution -1 is noise substitution and is not 0, so a noise-substituted subband counts as coded | yes, `UsedBands` in the 24-byte frame record | no | 8 bytes per frame in the series, derived | code | 1 |
| Mid/side stereo, SV4 to SV7 | the mid/side flag of each coded subband | yes, `MidSide` mask in the record | "Frames With Mid/Side", a value node; "Subband Stereo Coding" | 1 byte per frame, derived; a flag series | code | 1 |
| Noise substitution, SV7 | subband channels at resolution -1 | yes, `NoiseChannels` | "Noise-Substituted Channels" total | 8 bytes per frame as a numeric series | code | 2 |
| SV8 | the frames inside audio packets | no: audio packets are not read | no | | code | later |

- The header's maximum band is a bound for the whole stream; the per-frame used subbands are the coded bandwidth over time, at the resolution of one subband (689 Hz at 44.1 kHz).

### WMA standard, Pro and Voice

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Block sizes, standard | block length per block, from frame length >> n (FFmpeg 6.1.1 `wmadec.c` `wma_decode_block`) | yes, `BlockRecord.BlockBits` | "Block Sizes" | 1 byte per block, derived | code | 2 |
| Mid/side stereo, standard | ms_stereo bit per block | yes, `BlockRecord.IsMidSide` | "Mid/Side Blocks" ("x of y stereo blocks") | 1 byte per block, derived; a flag series over stereo blocks, `None` for mono | code | 2 |
| Noise substitution, standard | noise-coded bands per block | yes, `NoiseBands0`, `NoiseBands1` | "Noise-Substituted Blocks" | 1 byte per block, derived | code | 2 |
| Coded bandwidth, standard | the last nonzero coefficient of each channel, from the run-level codes the parser walks; (first coded line + last + 1) × (fs / 2) / block length, where version 1 codes from line 3 (`CoefsStart`). This is a coded value, not a signaled bound | yes, `BlockRecord.Last0`, `Last1` | no | 16 bytes per block: the value, and its own x because blocks share their frame's offset | code | 2 |
| Subframe sizes and channel transforms, Pro | subframe length per channel; transform per channel group | yes (`ProSubframeRecord`, `ProGroupRecord`) | "Subframe Sizes", "Channel Groups" | 1 byte per subframe and group | code | later |
| Frame types, Voice | the frame type of each frame (`wmavoice.c`) | yes, `VoiceFrameRecord.Type` | frames by type | 1 byte per frame, derived | code | 2 |

- WMA fixes two bounds per block size for the whole stream (`StandardParameters`, after `ff_wma_init`): `CoefsEnd`, 91% of the block's lines, past which no coefficient is coded, and `HighBandStart`, from the bits per sample, where noise coding may begin. The last nonzero coefficient is where each block's coded spectrum really ends; noise-substituted bands are synthesized and not counted.
- Blocks lie inside frames that may span superframes, so a block's x value is its frame's offset, and choosing a block selects its frame. Several blocks then share an x value, so the block series carry their own x (8 bytes per block, counted in the table) and are not shared with the bitrate series.

### WavPack

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Mid/side stereo | the joint stereo flag of each stereo block's header | yes, the header is in the block record | "Mid/Side Blocks" ("x of y stereo blocks") | 1 byte per block, derived; a flag series over stereo blocks | code | 2 |
| Decorrelation passes | decorrelation terms per block | yes | the smallest and largest | 8 bytes per block as a numeric series | code | 2 |

- A WavPack block holds a whole number of samples chosen by the encoder, so these graphs have as many points as the file has blocks; their cost is the bytes in the table.

### FLAC

Native FLAC and FLAC in Ogg, one parser (`src/Encspect.Formats.Flac`, merged). Section numbers are RFC 9639's.

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Stereo coding, two-channel frames | the channel bits: 0b0001 left and right each coded by itself, 0b1000 left/side, 0b1001 side/right, 0b1010 mid/side (9.1.3, Table 16; 4.2) | yes, `Entry.ChannelCode` of every frame (`Model.cs` 92) | "Independent Frames", "Left/Side Frames", "Right/Side Frames", "Mid/Side Frames": four sibling nodes over every two-channel frame (`Summary.cs` 256-269); a parent "Stereo Coding" replaces them (below) | 1 byte per frame, derived from the kept entry; `None` for a frame of other than two channels | code | 2 |
| Block size, variable block-size streams | the block size bits and the uncommon block size (9.1.1, Table 14; 9.1.6) | yes, `Entry.BlockSize` (`Model.cs` 83) | "Smallest Block Size", "Largest Block Size", "Blocking Strategy", value nodes | 8 bytes per frame as a numeric series "Block Size" in samples, derived; only in a stream whose frames have more than one size apart from a shorter last frame | code | 2 |
| Subframe types | the subframe type bits: constant, verbatim, fixed predictor, linear predictor (9.2.1, Table 19) | yes, every subframe of a walked frame (`SubframeFacts.Type`); kept per subframe only for the first 4,096 frames (`State.MaxDetailedFrames`) | "Subframe Types" with "Constant", "Verbatim", "Fixed Predictor", "Linear Predictor" | variable: 1 byte per subframe and a count byte, recorded while reading | code | 2 |
| Fixed predictor orders | the order, 0 to 4, of a fixed predictor subframe (9.2.1, 9.2.5) | yes | counted (`State.FixedOrders`, `State.cs` 511) but not reported; a node "Fixed Predictor Orders" with "Order 0" to "Order 4" is added | variable, as above; `None` for a subframe that has no fixed predictor; ordered | code | 2 |
| Linear predictor order | the order, 1 to 32, of a linear predictor subframe (9.2.1, 9.2.6) | yes | "Average Predictor Order", "Highest Predictor Order" | 8 bytes per frame as a numeric series, the highest order of the frame's linear predictor subframes, recorded while reading; its own x (8 more) where a frame has none | code | 2 |
| Coefficient precision | the predictor coefficient precision, 1 to 15 bits (9.2.6, Table 22) | yes | "Coefficient Precision", the range over the stream | 8 bytes per frame as a numeric series, the highest of the frame's linear predictor subframes; it lacks the same frames as the order series and shares its x | code | 2 |
| Residual coding method | partitioned Rice code with 4-bit or 5-bit parameters (9.2.7, Table 23) | yes, `SubframeFacts.ResidualMethod` | "Rice With 5-Bit Parameters", a value node over the subframes with a residual | variable, as above; a flag series, `None` for constant and verbatim subframes | code | 2 |
| Partition orders | the 4-bit partition order of the residual (9.2.7) | yes | "Partition Orders" with "Order 0" to "Order 15" | variable, as above; ordered; `None` for constant and verbatim subframes | code | 2 |
| Wasted bits | the wasted bits flag and its unary count (9.2.1, 9.2.2) | yes | "Subframes With Wasted Bits", a value node | variable, as above; a flag series | code | 2 |
| Compression ratio (new) | the frame's coded bits over its PCM size, block size × channels × bits per sample (9.1.1, 9.1.3, 9.1.4), in percent | yes: the frame's bits are those of its bitrate point (`StreamFigures`), its channels and bit depth are in the entry | no; a node "Compression Ratio" (lowest, overall, highest) is added | 8 bytes per frame, derived at the end from the bitrate points and the entries | code | 2 |
| Channel bit share | the first subframe's share of the bits of the frame's two subframes | yes: each subframe's start and end bit (`SubframeFacts.StartBit`, `EndBit`, `SubframeSyntax.cs` 112) | no | 16 bytes per two-channel frame | code | later |

- **The unit** is a frame with a bitrate point: every frame that is not cut (`State.cs` 342). The series share x with "Bitrate". A frame of FLAC in Ogg is one packet and the same unit.
- **When the codes are made:**
  - The stereo coding, block size and compression ratio are derived at the end from the 56-byte `Entry` every frame keeps (`Model.cs` 70) and the bitrate points, in the pass that builds the numeric series.
  - The subframe series are recorded while reading, in `State.Count` (`State.cs` 491), where their tallies are counted. The parser keeps subframe records (`FrameDetail`) only for the first 4,096 frames (`State.cs` 18); past them a frame is its entry alone, so nothing is left to derive from. FLAC holds one stream per parse (`State`), so a builder per stream exists while the frames are read.
- **Layout of the subframe series:** variable, a count byte per frame. A frame's channel count can differ from STREAMINFO's (`flac.frame-differs-from-streaminfo`), which is known only when the frame is read; a stream whose frames agree pays only the count byte. The strip then draws one row of stacked shares, not a row per subframe.
- **Equal to the tally:**
  - The new "Stereo Coding" node counts the complete two-channel frames, as MPEG's counts all complete frames, while the four nodes count every frame, a cut one included (`State.cs` 350).
  - The subframe tallies count the walked frames (`isWalked`, `State.cs` 209 and 356). That can include a frame whose walk completes but whose CRC-16 is cut off: the cut is found after the walk (`State.cs` 290) and the frame has no bitrate point. The FLAC pull request counts the subframe tallies over the frames that have a bitrate point, so [the list of legitimate differences](#the-summary-over-the-stream) stays as it is.
- **The "Stereo Coding" node** replaces the four sibling nodes, as Vorbis gets a parent "Block Sizes". Its entries are "Left/right stereo" (the two channels each coded by itself, MPEG's and AAC's entry of that name), "Left/side stereo", "Side/right stereo" and "Mid/side stereo". It anchors the series. The four nodes' figures are not kept twice under other names.
- **Side/right, not right/side:**
  - RFC 9639 names the assignments left-side, side-right and mid-side (4.2, Table 16), in the order their subframes are coded.
  - The parser's header value, `docs/formats/flac.md` and the `flac-channel-assignment` article write "side/right".
  - The tally node "Right/Side Frames", one sentence of the decoder design, libFLAC (`RIGHT_SIDE` in `flac -a`) and FFmpeg (`FLAC_CHMODE_RIGHT_SIDE`, `flac.h` 41) say right/side.
  - The category follows the project's "side/right", and the cross-check maps libFLAC's names.
- **Block size as a number:** a variable block-size stream may hold any size from 16 to 65,535 samples (4.1), more than a category series' 255 categories, and "Block Sizes" names long against short elsewhere. A fixed block-size stream has one size but for a shorter last frame (4.1), so it gets no series.
- **Predictor orders in two forms:** the five fixed orders are categories, ordered. The 32 linear predictor orders and 15 precisions are numbers, since a ramp has eight steps (see [Palette](#palette)). Both numeric series have a per-second form ("Over Time", each second's highest) anchored to "Highest Predictor Order" and "Coefficient Precision".
- **Compression ratio:** the frame's coded bits over block size × channels × bits per sample. It equals the bitrate over the PCM bitrate, so in a stream whose channels and bit depth do not change it draws the bitrate's shape on another scale. Its per-second form is the bits over the PCM bits of the frames that start in the second. The lead asked for it; the alternative is the node alone.
- **Channel bit share** is later: the lead did not ask for it, and the first subframe is the left channel, the side channel or the mid channel depending on the assignment, which the description would have to carry frame by frame.
- **No coded bandwidth:** FLAC predicts samples in the time domain (4.3) and codes no frequency bands.
- **Fixtures,** found by a script over the goldens at f9b67fb (one count per golden):
  - left/side frames in 13 fixtures, side/right in 6, mid/side in 67, two channels each coded by itself in 64;
  - constant subframes in 3, verbatim in 6, fixed predictors in 95, linear predictors in 84; wasted bits in 5; 5-bit Rice parameters in 14;
  - a variable blocking strategy only in the hand-built `built-variable-block-sizes` and `built-variable-block-strategy`, and a mixed one in `built-blocking-strategy-changes`.
  - The longest holds 24 frames, so no committed fixture passes the 4,096 detailed frames. The FLAC pull request adds a stream past them, where the subframe series continue from what was recorded while reading.
- **Cross-check:** `crosscheck_flac_fixtures.py` already runs `flac -a -F` and reads only each frame's offset. Run for this design with flac 1.4.3 on `external-16-wasted-bits.flac`, `external-24-stereo.flac` and `built-subframe-types.flac`, its output gives per frame `bits`, `blocksize` and `channel_assignment` (`INDEPENDENT`, `LEFT_SIDE`, `RIGHT_SIDE`, `MID_SIDE`), and per subframe `wasted_bits`, `type` (`CONSTANT`, `VERBATIM`, `FIXED`, `LPC`), `order`, `qlp_coeff_precision`, `residual_type` (`RICE`, `RICE2`) and `partition_order`. Comparing them unit by unit makes libFLAC an independent reader of every FLAC series. Whether flac 1.5.0, which CI pins, prints the same subframe lines is **unverified**.

### Monkey's Audio

No parser yet: it comes with its decoder in step 3 of the [decoder design's rollout](decoders.md#rollout), reading the headers, the seek table and the start of each frame, and leaving the range-coded data to the decoder. These graphs follow that parser.

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Stereo coding, two-channel streams | the frame start: a 32-bit CRC word whose bit 31 says that a 32-bit word of special codes follows, from version 3.83 (above 3820: SDK `APEInfo.h` 64, `APEDecompressCore.cpp` 381-397; FFmpeg `apedec.c` 766-783). Codes 1 and 2 mark one silent channel each, 4 pseudo-stereo (`Prepare.h` 6-9; `apedec.c` 46-48). With both channels silent nothing is coded; with pseudo-stereo only the first coded channel, X (`APECompressCore.cpp` 104-140; `APEDecompressCore.cpp` 290-311; `apedec.c` 1509-1512, 1657-1658) | planned: the frame starts | planned: a node "Stereo Coding" with "Mid/side stereo", "Pseudo-stereo" and "Silence" | 1 byte per frame, derived from the frame record, which keeps the codes | spec | 2, after the parser |
| Silent frames, mono streams | code 1: nothing is coded (`APECompressCore.cpp` 142-150; `APEDecompressCore.cpp` 344-353; `apedec.c` 1482-1488) | planned | planned: a node "Silent Frames" | 1 byte per frame; a flag series | spec | 2, after the parser |
| Bitrate per frame | the frame's size: the next frame's position in the seek table minus its own, the last frame's to the end of the data before the WAV tail (`ape.c` 269-294); its blocks, the header's blocks per frame or, for the last frame, its own count (`ape.c` 225-230, 284) | planned: the seek table | planned | the format's bitrate series | spec | 2, after the parser |
| Compression ratio (new) | the frame's bytes over blocks × channels × bytes per sample, in percent | planned | planned: a node "Compression Ratio" | 8 bytes per frame | spec | 2, after the parser |
| Compression level | the header's level, one per file (`ape.c` 185, 197); it chooses the filters (`apedec.c` 81-87, 274-286) | planned | | | spec | not a series |
| Blocks per frame | one per file: 73,728, × 4 at extra high and × 16 at insane in SDK 13.44 (`APECompressCreate.cpp` 77-81); 9,216 or 73,728 by version before 3.95 (`ape.c` 225-230) | planned | | | spec | not a series |
| Channel bit share | X and Y are decoded value by value from one range-coded stream (`APEDecompressCore.cpp` 314-323), so their bits cannot be told apart without the decoder, and decoding never changes the parser's nodes | no | | | spec | not a series |

- **What can and cannot vary per frame:**
  - A stereo frame always codes X, close to the average of the two channels, and Y, their difference (`Prepare.cpp` 415-416 for 16 bits, the same conversion in the 8-, 24- and 32-bit branches), and the decoder undoes it in every coded stereo frame (`apedec.c` 1522-1530). The SDK's comment on the same conversion of a multichannel file's front pair calls it mid-side (`Prepare.cpp` 110). So the coding is mid/side in every coded frame, and no frame chooses left/right.
  - What a frame can choose is to code nothing (silence) or only X (pseudo-stereo: the two channels are equal in every sample, `Prepare.cpp` 422-430).
  - Silence takes precedence over pseudo-stereo: both decoders test it first (`APEDecompressCore.cpp` 292-303; `apedec.c` 1484 and 1509, a pseudo-stereo frame taking the mono path at 1657).
  - One silent channel alone (code 1 or 2) changes nothing in decoding (`APEDecompressCore.cpp` 292-303), so such a frame counts as "Mid/side stereo". The series does not name that channel: the SDK's variables call the first sample of each pair R (`Prepare.cpp` 405-406), so its "left silence" flag is about the second channel of the WAV data.
- **Who writes the codes:** SDK 13.44's encoder sets them only in its 16-bit branches (`Prepare.cpp` 299-449); its 8-, 24- and 32-bit branches set none, so its stereo files of those depths are "Mid/side stereo" in every frame. Whether other encoders or earlier SDK versions set them at other depths is **unverified**. Both decoders honor them at any depth for one and two channels.
- **More than two channels:** the decoder ignores the codes (`APEDecompressCore.cpp` 276-289), and a multichannel file's channels are paired, or coded each by itself, by its channel count and bit depth alone (`Prepare.cpp` 104-245 for 24 bits, 303-397 for 16): no series.
- **Older files:** version 3.82 and earlier have no special codes (`APEInfo.h` 64); without the CRC format flag a Rice-coded value of 0 marks a silent frame instead (`Old/UnMACOld.cpp` 125-132). Wave 2 covers files above version 3820; how the planned parser reads the older frame starts is left to its pull request.
- **Time:** a frame of 73,728 samples lasts 1.67 s at 44,100 Hz, 6.7 s at extra high and 26.8 s at insane, so a file has few units and most seconds start no frame. The over-time view prints "-" for those; the frames view is the useful one.
- **No block switching and no coded bandwidth:** the frame length is fixed per file, and the coding is time-domain prediction without frequency bands.
- **Cross-check:** FFmpeg's debug log names a silent frame ("pure silence mono", "pure silence stereo", `apedec.c` 1486, 1511) but not pseudo-stereo, and the SDK keeps its codes in a private member (`APEDecompressCore.h` 59). Pseudo-stereo therefore has no per-frame reference among the tools read; the hand-built fixtures' model carries it. ffprobe's packet sizes are the frame sizes rounded up to 4 bytes plus the 8 bytes `ape.c` puts before each packet (`ape.c` 303, 399-404), which the script maps.

### TTA

The parser is built (step 3 of the [decoder design's rollout](decoders.md#rollout), [As built](decoders.md#as-built-in-step-3-tta)): a format parser and a "tta" codec parser for Matroska, without a decoder. It reads every frame to compare the frame CRC, and reports the bitrate per frame and per second as the other lossless formats do; the compression ratio below is not built, and the series carry no concept yet.

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Bitrate per frame | the frame sizes of the seek table (`libavformat/tta.c` 119-127); every frame 256 × sample rate / 245 samples (`tta.c` 202; `libavformat/tta.c` 87; `libtta.c` 274), the last frame the rest (`libavformat/tta.c` 88-90; `libtta.c` 515-517) | planned: every frame | planned | the format's bitrate series | spec | 2, after the parser |
| Compression ratio (new) | the frame's bytes over samples × channels × bytes per sample, in percent | planned | planned: a node "Compression Ratio" | 8 bytes per frame | spec | 2, after the parser |
| Stereo coding | the same decorrelation of every frame with more than one channel (`tta.c` 341-346; `libtta.c` 719-733) | | | | spec | not a series |
| Frame length | fixed by the sample rate, as above | | | | spec | not a series |
| Format | 1 simple, 2 encrypted, one per file (`libtta.h` 113-114; `tta.c` 145-155) | planned | | | spec | not a series |

- **Nothing in a frame states a choice.** A frame has no header: it is the Rice-coded values of every channel, sample by sample, then its CRC-32 (`tta.c` 271-362). Each frame starts the filters and the adaptive Rice coder from the same values (`tta.c` 257-268; `libtta.c` 519-524), the prediction is fixed by the bit depth (`tta.c` 327-334), and the encrypted format changes only the filters' starting coefficients, from the password (`tta.c` 262-266).
- **Only the size varies,** so the bitrate and the compression ratio are TTA's graphs. A frame lasts 256 / 245 s, about 1.045 s, at any sample rate, so the per-frame and per-second forms are close to each other.
- **Channel bit share** would need the Rice codes walked, since the channels' values alternate in one bit stream; the parser compares the CRC over the bytes and walks none.
- In Matroska the frames carry no header either, and give the same series.
- **Cross-check:** ffprobe's packet sizes are the seek table's frame sizes (`libavformat/tta.c` 119-127).

### WAV and AIFF

No parser yet: step 3 of the [decoder design's rollout](decoders.md#rollout) brings WAV and AIFF parsers without decoders.

- **PCM has nothing to graph.** Uncompressed and floating-point PCM in WAV, AIFF and AIFF-C stores every sample as it is: there are no frames and no coding choices, and the bitrate is the sample rate × channels × bits, the same everywhere. WAV and AIFF of PCM therefore add no series in wave 2.
- **What the samples show** (levels, peaks, clipping, DC offset) needs no parser but the samples, and belongs to the decoder design's host-side PCM analyses, not to this design.
- **A data chunk routed to a codec parser** (MPEG audio in WAV, the plan's example of a route) gets that codec's series.
- **Compressed data in WAV and AIFF-C** that the syntax shows without decoding:

| Candidate | Field and source | Read | Tallied | Kept per unit | Status | Wave |
| --- | --- | --- | --- | --- | --- | --- |
| Block predictors, Microsoft ADPCM (format tag 2, `riff.c` 524) | 1 byte per channel at the start of each block: the index of the predictor coefficient pair the block uses, followed by its starting delta and two samples (FFmpeg 6.1.1 `adpcm.c` 1234-1285, which accepts indices 0 to 6, the seven pairs of `adpcm_data.c` 60-62) | no: no WAV parser exists, and the decoder design's step 3 does not plan one that walks the blocks of a compressed data chunk | no | 1 byte per block and channel, a fixed layout | spec | later |
| IMA ADPCM block headers (WAV format tag 0x11, `riff.c` 532; AIFF-C `ima4`) | per channel and block the first sample and the step index (`adpcm.c` 1148-1160); AIFF-C packs 34 bytes per channel with a 9-bit predictor and a 7-bit step index (`aiffdec.c` 156-157; `adpcm.c` 1116-1119) | no | no | | spec | not a series |

- The Microsoft ADPCM predictor index is a choice the encoder makes per block, readable at a fixed position of each block. It waits for a WAV parser that walks the blocks, and no Microsoft ADPCM fixture is committed; the lead can move it to wave 2 when the WAV parser's scope is set. Which pairs a file's format chunk lists, and whether it may list more than the seven, is **unverified**: no description of the format chunk's extension was read.
- The IMA step index is the adaptive quantizer's state, which the decoder loads (`adpcm.c` 1151-1153), not a choice among codings. It follows the signal's level, which is the PCM analyses' figure.

### What goes in the first rollout

- **Wave 1**, one series per tally the lead named, in the formats that read them per frame today: MPEG (channel modes, stereo coding, joint stereo modes, intensity stereo bounds, block usage, block sizes, coded bandwidth of Layers I and II, channel bit share of Layers I to III), AAC (window sequences, block sizes, stereo coding, coded bandwidth), AC-3 and E-AC-3 (block switching, coupling, rematrixing, spectral extension, coded bandwidth), Vorbis (block sizes, modes), Opus (modes, bandwidths, frame sizes, channel counts), Speex (submodes), Musepack SV4 to SV7 (coded bandwidth, mid/side stereo).
- **Wave 2:** the further tallies of MPEG and AAC, DTS (coded bandwidth, sum/difference, joint intensity), WMA standard (block sizes, mid/side stereo, coded bandwidth) and Voice, WavPack (mid/side stereo).
- **Wave 2, the lossless formats (decision 11):**
  - FLAC: stereo coding (with the "Stereo Coding" node over the complete two-channel frames), block size in variable block-size streams, subframe types, fixed predictor orders, linear predictor order, coefficient precision, residual coding method, partition orders, wasted bits, compression ratio;
  - Monkey's Audio: stereo coding of two-channel streams (mid/side stereo, pseudo-stereo, silence), silent frames of mono streams, compression ratio; after its parser and decoder, step 3 of the [decoder design's rollout](decoders.md#rollout);
  - TTA: compression ratio, the only figure a TTA frame varies in besides its bitrate; after its parser, from the same step;
  - WAV and AIFF: nothing. PCM has no coding choice per frame, and what its samples show belongs to the host-side PCM analyses.
- **Later:** what needs the entropy pass (Layer III's big_values region end and intensity stereo bound, DTS transient modes, which need the core subframe side information, Opus CELT's transient, intensity, dual stereo and mid/side coding, which need the range decoder, Musepack SV8), WMA Pro's subframes and transforms, AC-3's exponent strategies and per-channel block switching, FLAC's channel bit share, the block predictors of Microsoft ADPCM in WAV (once a WAV parser walks the blocks of its data chunk).

## The model

### Options considered

- **Generalize `Series`** with a kind and integer values. JSON's `series` entries would then hold values that are no numbers in a unit, which changes the meaning of a field consumers already read: `schemaVersion` 2, and every graph in the GUI would branch on the kind.
- **Nodes per frame** for the categories. They exist already (the side information), and front ends must not read names.
- **A category series beside `Series` (recommended).** `Series` stays as it is; the new type has its own array in JSON and its own charts in the GUI.
- **A fixed number of slots per unit** (an earlier draft). It cannot hold AAC or E-AC-3, below, and is replaced by units with a variable number of values.

### Units and values

- A **unit** is what the parser records once, and the point of the stream's bitrate series: a frame, an ADTS frame or access unit, a program frame, a packet, a block. Its x value is its byte offset, and choosing it in a graph selects that node.
- A **value** is one coded choice a unit carries: a granule of one channel, a block, a channel stream, a channel pair. Every value counts once in the series' counts, as the tally counts it: block usage counts granules per channel, window sequences count channel streams.
- A unit carries **as many values as it has**:
  - Fixed layouts (Layer III granules and channels, Speex frames and layers): every unit has the same number, stored once for the series. A value a unit lacks holds `CategorySeries.None`, such as the second channel of a mono frame in a stream that changes between mono and stereo.
  - Variable layouts (AAC channel streams of every raw data block in a frame, E-AC-3 blocks of a program frame with its dependent frames, whose block count can change): one count byte per unit.
  - Fixed layouts also include an AC-3 stream (6 blocks per frame) and an Opus file (one value per stream packet, the stream count being stated in OpusHead).
  - Past `MaxCodedValues` a unit has no coded values: `ValueCount` returns 0 for it and the indexer returns `None`; its values are in `Counts`.
- Why this is needed, from the code:
  - **AAC.** `BlockReader` counts every individual_channel_stream into the window sequence tally. A raw data block may span 64 KiB (`BlockLog.MaxBlockBytes`), enough for thousands of minimal channel elements, and an ADTS frame may hold several raw data blocks, with one bitrate point for all of them (`RawAacParser`, `figures.Add(frame.Offset, …)`). The 48 of `CodingTally.MaxGainChannels` bounds only the global-gain statistics.
  - **E-AC-3.** A program frame is an independent frame with up to 8 dependent frames, up to 54 blocks, and the tallies count all of them. The block count of a program is not part of `ProgramKey` and can change within a stream.
- A **weight** says how many of the tally's things a value stands for, where that is not one: an Opus stream packet stands for its frames. Weights are optional and cost a byte per value only in a series that uses them.
- **Caps, and what the tally does past them:**
  - At most 254 values of a unit are coded; at most 2^24 values per series. A value past either cap is **counted without a code**: it adds to `Counts` and `Total`, so the series still equals the tally, and the graphs draw the coded values only. The series says how many values are uncoded (`UncodedValues`), and the views and JSON show it.
  - So memory per series is bounded by a constant (16 MiB of codes, plus counts and weights), whatever a crafted file does, and the tallies are unaffected.
  - Units stop where the parsers' record limits stop the graphs (`aac.frames-limit`, `wma.records-limit`, Matroska's frame budget); the tallies stop there too.

### Time

- A category series may carry a `TimeIndex`: the start of each interval of audio in seconds and the first unit that starts in it. It is the same partition the parser's per-second series use ("the frames that start in that second"), and its starts are shared with the "Bitrate Over Time" series' x values.
- The first units are nondecreasing, not strictly rising: an interval that no unit starts in (AAC at implausible sample rates) repeats the previous value. The GUI draws such an interval as a gap, and the text prints "-" for it.
- AAC's per-second series merges neighboring seconds past 131,072 entries; the index follows the same intervals.
- A stream without per-second series (Vorbis whose long blocks last longer than a second, from a corrupt sample rate) has no index, and its graph shows frames only.

### Types in Core.Abstractions

All in `Encspect.Core`.

```csharp
// The concept a graph shows, which front ends group by in place of names: the GUI lists the series of a stream under one
// heading per concept, whatever the format. None for a series that belongs to no shared concept (modes, frame sizes).
public enum GraphConcept { None, StereoCoding, BlockSwitching, CodedBandwidth, BitDistribution }

// One category a series' values take, such as a Layer III block type. Name is written as the tally entry for it is named
// ("Mid/side stereo", "Short"), Description as a node's (constant, citing the specification), Topic as a node's.
public sealed record Category(string Name, string Description)
{
    public string? Topic { get; init; }
}

// The intervals of audio time a parser counts its per-second series over: the start of each in seconds, shared with
// that series' x values, and the index of the first unit that starts in it. Starts and FirstUnits have the same length;
// FirstUnits is nondecreasing and within 0 to the series' Count.
public sealed record TimeIndex(ReadOnlyMemory<double> Starts, ReadOnlyMemory<int> FirstUnits);

// One category per value and unit over a stream, for graphs and for the summary over the stream. Immutable once built,
// so the writers and the GUI read it on any thread; built by CategorySeriesBuilder, or as a grouping by Group.
public sealed class CategorySeries
{
    // The code of a value without a category. Codes 0 to 254 are categories.
    public const byte None = 255;

    public const int MaxCategories = 255;

    // Coded values of one unit, and of one series; further values are counted without a code.
    public const int MaxValuesPerUnit = 254;

    public const int MaxCodedValues = 1 << 24;

    // Named as the tally node it is anchored to.
    public string Name { get; }

    // Which shared heading the GUI lists it under.
    public GraphConcept Concept { get; }

    // In the order the parser gives; index 0 is the plain coding (long blocks, left/right stereo).
    public IReadOnlyList<Category> Categories { get; }

    // Whether the categories are ordered, such as frame sizes or submodes; the GUI then draws a ramp of one hue.
    public bool IsOrdered { get; }

    // What one value is, such as "granule and channel", "block", "channel stream".
    public string ValueLabel { get; }

    // Values per unit when every unit has the same number, else 0.
    public int ValuesPerUnit { get; }

    // For a fixed layout, what each value is, such as "granule 1, channel 2"; empty otherwise.
    public IReadOnlyList<string> ValueNames { get; }

    // The byte offset of each unit.
    public ReadOnlyMemory<double> X { get; }

    // The per-frame numeric series of the stream whose x this series shares, or null; the JSON names it.
    public Series? XSeries { get; }

    // Units.
    public int Count { get; }

    // The coded values of a unit (a lookup by a sparse index of one int per 1,024 units for a variable layout).
    public int ValueCount(int unit);

    // The code of one value of one unit: an index into Categories, or None.
    public byte this[int unit, int value] { get; }

    // What the value stands for in the counts: 1 unless the parser gave weights.
    public int WeightAt(int unit, int value);

    // Per category, the weighted values over the whole series, uncoded ones included, and their sum: kept as the
    // series is built, so a series equals its tally even past the caps.
    public IReadOnlyList<long> Counts { get; }

    public long Total { get; }

    public long UncodedValues { get; }

    // Adds the weighted values of the units first to end - 1 to counts, one entry per category: the per-second shares and
    // the GUI's pixel columns use it. The second form counts only one value of each unit (a fixed layout's lane).
    public void CountRange(int first, int end, Span<long> counts);

    public void CountRange(int first, int end, int value, Span<long> counts);

    public TimeIndex? Time { get; }

    // The tally node whose entries are these categories; the text tree prints the per-second shares after it.
    public Node? Anchor { get; }

    // For a grouping, the series it groups; otherwise null.
    public CategorySeries? Source { get; }

    public string Description { get; }

    public string? Topic { get; }

    // A grouping of this series: each code maps to a category of the new series (map has one entry per category here).
    // It shares this series' storage and costs the map, not a byte per value; Counts are the sums of this series'. A grouping inherits its source's `Concept`, so "Block Sizes" sits under "Block Switching" with "Block Usage" without a parameter.
    public CategorySeries Group(string name, IReadOnlyList<Category> categories, ReadOnlySpan<byte> map, Node? anchor, string description, string? topic = null);
}

// Records a category series while a parser reads or derives its units, in chunks, so nothing is copied when it is built
// and nothing is kept per unit beyond the codes, the count byte of a variable layout and the weights.
public sealed class CategorySeriesBuilder
{
    // valuesPerUnit 0 means a variable layout.
    public CategorySeriesBuilder(string name, IReadOnlyList<Category> categories, string valueLabel, int valuesPerUnit = 0, IReadOnlyList<string>? valueNames = null, bool isOrdered = false, GraphConcept concept = GraphConcept.None);

    public int Count { get; }

    // One unit of one value.
    public void Add(byte code, byte weight = 1);

    // One unit; codes.Length equals valuesPerUnit for a fixed layout, else is any length, and weights are empty or of the
    // same length. Values past the caps are counted without a code.
    public void Add(ReadOnlySpan<byte> codes, ReadOnlySpan<byte> weights = default);

    // x must hold Count values. Throws ArgumentException on a code past the categories, a mismatched x or a TimeIndex
    // that is not nondecreasing within 0 to Count: a parser bug, which the host reports as parse.failed.
    public CategorySeries Build(ReadOnlyMemory<double> x, Series? xSeries, Node? anchor, string description, string? topic = null, TimeIndex? time = null);
}
```

`Series` gets one optional property of the same type, additive like the rest: `Concept { get; init; }`, `None` by default; "Coded Bandwidth" and "Channel Bit Share" set theirs. `StreamInfo` gets one property, after `Series`:

```csharp
public sealed record StreamInfo
{
    // Categories per frame for graphs and for the summary over the stream, such as the Layer III block types.
    public IReadOnlyList<CategorySeries> CategorySeries { get; init; } = [];
}
```

- The storage is the builder's chunks (`ChunkedList<byte>`, which `Core.Abstractions` already has) for the codes, the count bytes and the weights, handed to the series without a copy.
- No per-call state: a builder belongs to one stream of one analysis, as a tally does.

### When the codes are made

- **Derive at the end wherever the kept records hold the fields.** This is the rule, because the parsers that keep a record per frame decide their streams late and already have the data:
  - **MPEG.** `AddStreams` splits the frame records into streams afterwards (`IsSameStream`), frames past `MaxStreams` are in no stream (`mpeg.streams-limited`), and a version change switches between 4 and 2 granule values. A builder per stream therefore cannot exist while the frames are read. The header is in the frame record, and the Layer III side information and the Layer II allocation are in the shared store (`build.Store`), so the codes are read at known bit positions in the pass that builds the per-stream `Series` (`MpegAudioParser.Streams.cs`), with no new per-frame state and no `Layer3SideInfo` object.
  - **Vorbis** (packet flags and mode), **Opus** (TOC byte), **Speex** (frame record), **Musepack** (frame record), **WMA** (block and voice records), **WavPack** (block header): the same.
- **Record while reading in AAC, AC-3 and DTS, for a reason checked in the code.** Neither parser keeps the decoded fields; each keeps the bits and rebuilds its nodes by reading the syntax again:
  - AAC keeps the bits of each raw data block in `UnitStore`, minus the ranges its nodes only show as a whole (scalefactors, Huffman-coded spectral data, fill, SBR data), with a 24-byte record per access unit.
  - AC-3 keeps the bits its nodes are built from in a `ByteStore`, without the exponent groups and mantissas, and builds its nodes by running `FrameSyntax` over them again (`FrameRecords`, `new FrameSyntax(FrameBits.ForNodes())`).
  - DTS keeps a 24-byte record per unit and the kept bytes of its layout in a `ByteStore`, and builds its nodes from them (`DtsAnalysis`, `UnitRecord`); its JOINX and SUBS live only in the syntax walker's arrays.
  - Their syntax is variable-length: the position of a field depends on the fields before it (an AAC channel stream's section data, an AC-3 block's exponent strategies and coupling). Reading a field back therefore means walking the syntax over every frame, which is the work of the parse itself; recording the code in the pass that already walks it costs one byte.
  - MPEG's side information has a fixed layout, so its fields are read straight from the kept bits at known positions (and the Layer I and II allocation is walked from the header's table, with no entropy code). Vorbis, Opus, Speex, Musepack, WMA and WavPack keep decoded fields in their records.
  - Their builders belong to the stream the parser is reading. This settles the earlier "unverified: what AC-3's frame record keeps".
- **FLAC both ways, for a reason checked in the code at f9b67fb** (decision 11): its header series (stereo coding, block size, compression ratio) are derived from the entry every frame keeps, and its subframe series are recorded while reading, since subframe records are kept only for the first 4,096 frames (see [FLAC](#flac)).
- **Monkey's Audio and TTA** derive at the end from the frame records their parsers will keep: the special codes (1 byte) and each frame's size.
- The cost of deriving is one more pass over the records, which every other series already makes; it allocates nothing per frame.

### The summary over the stream

- **Reported by the parser, as now.** The tally nodes under Statistics are the summary in the tree: "Block Usage: 480 granules", "Short: 24 granules (5.0%)". They keep their names, values and scope, except for the renames below.
- **Two nodes over all frames are added**, because the lead asked for EncSpot's shares and EncSpot's are over all frames (Decision 4):
  - "Stereo Coding": MPEG frames, with entries "Left/right stereo", "Mid/side stereo", "Intensity stereo", "Mid/side and intensity stereo" and the two entries of frames with no joint coding, "Dual channel" (two independent channels) and "Mono"; AAC channel pairs, with the same four and "Reserved". In Layers I and II a joint stereo frame is intensity stereo above its bound.
  - "Block Sizes": Layer III granules as "Long, start and stop", "Short", "Mixed" and "Reserved", the way EncSpot counts them; AAC channel streams as "Long, start and stop" and "Short". Vorbis gets the parent node "Block Sizes" over its "Long Blocks" and "Short Blocks".
  - The existing nodes stay as the detail: "Joint Stereo Modes" over joint stereo frames only (mp3guessenc's and EncSpot's mode extension counts), "Block Usage" with start and stop apart.
  - The series "Joint Stereo Modes" gives a Layer III frame that is not joint stereo the code `None`, so its counts equal the tally, which counts joint stereo frames only. The series "Stereo Coding" gives it left/right stereo, "Dual channel" or "Mono" by its channel mode.
  - **AC-3's 1+1 mode** (acmod 0, two independent channels) is named by the AC-3 parser as the spec does: "Audio Coding Mode" shows "0: 1+1 (channel 1, channel 2)" (`Ac3Names.CodingMode`), "1+1" where short. It is a stream-level fact, not a per-frame choice, so no AC-3 series splits by it: block switching and spectral extension count its blocks as they count any, coupling cannot occur in it (A/52 reads cplinu in every mode, but FFmpeg 6.1.1 rejects coupling in mono and dual mono, `ac3dec.c`, and the parser adds "Blocks With Coupling" only for acmod 2 and up, `Program.cs`), and the 1+1 program has no rematrixing series (its node is 2/0 only; in a 2/0 program a block of another mode counts as without rematrixing). "Dual channel" is therefore MPEG's entry; AC-3 keeps "1+1".
- **The same categories on the series.** Each category is named as its tally entry is, and the series is named as and anchored to the tally node. "Block Sizes" is a grouping of "Block Usage" (AAC: of "Window Sequences"), so it costs no byte per value.
- **Counts derived once, by the shared type.** `CategorySeries.Counts` is kept as the series is built. JSON writes it beside each category, and the GUI's summary reads it, never a node.
- **Held equal by a test, from data.** `CategorySummaryTests` walks every fixture and compares each series with its anchor, using a table in the test project (`category-summary.json`) that gives each series name its form and the nodes to compare. A series missing from the table fails, so no exception is guessed. Two forms:
  - **Entries:** the series' count for each category equals the value of the anchor's entry of that name, and the total equals the denominator node the table names (the frames, granules, packets or stream packets of Statistics), never the anchor's own value, which for "Channel Modes", Opus' "Modes", "Bandwidths" and "Frame Sizes", Speex' submodes and Vorbis' "Modes" is the number of distinct values used, and for "Channel Counts" the number of stereo packets. Used where the tally has entries (Layer III, AAC, Opus, Speex, Vorbis' new parent).
  - **Flag series:** two categories, named after the tally and written by the parser as constants ("With block switching" and "Without block switching", "With mid/side stereo" and "Without mid/side stereo"), for a tally that is a single value without entries: AC-3's "Blocks With Block Switching", "Blocks With Coupling", "Blocks With Rematrixing", "Blocks With Spectral Extension", Musepack's "Frames With Mid/Side", WMA's and WavPack's "Mid/Side Blocks". The "with" count equals the value; the total equals the denominator node the table names ("Audio Blocks Read", the frames, the stereo blocks); the "without" count is the total minus the "with" count, a complement stated in the description, not a figure of its own.
- **Where series and tally legitimately differ, exhaustively:**
  - an MPEG stream that a tag splits into several regions, or a region that holds several streams: the tally covers a region and the series a stream, so the test compares per connected group: the regions and streams that overlap (a stream that a tag splits across two regions joins both) form one group, and it sums the tallies over the group's regions and the series over its streams. The JSON's counts then cover the stream while the tree's cover the region (as the bitrate figures already differ);
  - MPEG frames in no stream (`mpeg.streams-limited`): the sum is at most the tally, and the table lists the finding that allows it;
  - nothing else. Past a record limit both stop at the same unit, and past a cap the series counts the uncoded values, so AAC, Opus multistream and the rest stay equal.
- **Several streams anchored to one node:** the text tree labels each stream's lines with its number ("Long Share Over Time (stream 2)"), so they do not read as duplicates.
- **Considered:** the host derives the summary nodes from the series, and the parsers drop their tally code. That changes every golden's Statistics node, loses the region scope, and puts nodes of the host into a parser's tree.
- **One figure, one name:** a category has one name in the tree, the series, JSON and the GUI. What the series adds over the tally is time, not new figures.

### Names across formats

One concept, one name, checked across the tally nodes, series, JSON and docs:

| Concept | Name | Where | Change to an existing node |
| --- | --- | --- | --- |
| Left/right, mid/side, intensity stereo, both, per frame (MPEG) or channel pair (AAC) | "Stereo Coding" | new node in MPEG and AAC | none |
| Opus's stereo or mono packets (TOC s bit) | "Channel Counts" | Opus | rename of "Stereo Coding", which names the concept above; every Opus golden |
| Joint stereo frames only, with the four codings | "Joint Stereo Modes" | MPEG | none |
| The same four codings of a channel pair, whatever the window | "Stereo Coding" with "Channel Pairs" for the window and mask counts | AAC | the counts in "Channel Pairs" follow the classification rule |
| Long against short transforms | "Block Sizes" | new in MPEG and AAC; new parent in Vorbis; WMA's own node is a second form, whose entries are lengths in samples. Each format's node and series descriptions say which form it is | Vorbis: "Long Blocks" and "Short Blocks" move under the parent; every Vorbis golden |
| Highest frequency the coded data reaches | "Coded Bandwidth" | new in the formats of the survey | AAC "Bandwidth" renamed; every AAC golden |
| The bandwidth a packet states | "Bandwidths" | Opus | none |
| FLAC's channel assignment of two-channel frames: left/right, left/side, side/right, mid/side | "Stereo Coding" | new parent in FLAC, over the complete two-channel frames | "Independent Frames", "Left/Side Frames", "Right/Side Frames" and "Mid/Side Frames" move under it as "Left/right stereo", "Left/side stereo", "Side/right stereo" and "Mid/side stereo"; every FLAC golden of a two-channel stream |
| Monkey's Audio frames: mid/side stereo, pseudo-stereo, silence | "Stereo Coding" | new in the Monkey's Audio parser | none: the parser is new |
| The length of a frame in samples, where it varies | "Block Size", a numeric series | FLAC | none; a third form of block switching beside "Block Sizes" (long against short) and WMA's lengths in samples |
| A frame's coded size against its PCM size | "Compression Ratio" | new in FLAC, Monkey's Audio and TTA | none |

- The word "plain stereo" is not used. EncSpot's mode 0 is two entries of "Stereo Coding", as above.
- AAC's pair categories are the four codings of `JointStereoModes`, named alike, plus "Reserved". There is no "Separate windows" category: it hid intensity stereo.
- Block types keep their format's names: Layer III's "Long", "Start", "Short", "Mixed", "Stop", "Reserved"; AAC's "Only Long", "Long Start", "Eight Short", "Long Stop"; AC-3's blocks with and without block switching; Vorbis's long and short blocks. They are different mechanisms, and the articles say how they correspond.
- Series names are their anchors' names; the Opus series is "Bandwidths", not "Bandwidth".
- FLAC's entries are written as MPEG's and AAC's are ("Left/right stereo", "Mid/side stereo"), with the format's own "Left/side stereo" and "Side/right stereo", as MPEG has "Dual channel" and AC-3 "1+1". Monkey's Audio's "Pseudo-stereo" is the SDK's name (`APE_SPECIAL_FRAME_PSEUDO_STEREO`), as AC-3 keeps its specification's names.

### The same concepts in every format

The lead asked that where other formats have concepts like MP3's, they are graphed too, under the same headings, so a user compares an MP3 and an AAC file the same way. Three headings, each a `GraphConcept`:

| Concept (heading) | MPEG audio | AAC | AC-3, E-AC-3 | Vorbis | Opus | Musepack | WMA standard | WavPack | DTS | FLAC | Monkey's Audio | TTA | WAV and AIFF |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Stereo Coding** | "Stereo Coding", "Joint Stereo Modes", "Intensity Stereo Bounds" (wave 1) | "Stereo Coding" of channel pairs (wave 1) | "Blocks With Rematrixing", "Blocks With Coupling" (wave 1) | none: coupling is fixed per mapping | CELT's intensity, dual stereo and mid/side: inside the range-coded data (later) | "Frames With Mid/Side" (wave 1) | "Mid/Side Blocks" (wave 2) | "Mid/Side Blocks" (wave 2) | SUMF and SUMS, JOINX (wave 2) | "Stereo Coding" of two-channel frames (wave 2) | "Stereo Coding": mid/side stereo, pseudo-stereo, silence (wave 2, after its parser) | none: the same decorrelation in every frame | none: PCM makes no choice |
| **Block Switching** | "Block Usage", "Block Sizes" (wave 1) | "Window Sequences", "Block Sizes" (wave 1) | "Blocks With Block Switching" (wave 1) | "Block Sizes" (wave 1) | CELT's transient flag: range-coded (later) | none | "Block Sizes" (wave 2) | none | TMODE: core subframe data (later) | "Block Size" in samples, variable block-size streams (wave 2) | none: the blocks per frame are fixed per file | none: 256 × sample rate / 245 samples per frame | none |
| **Coded Bandwidth** | "Coded Bandwidth", Layers I and II (wave 1) | "Coded Bandwidth" (wave 1) | "Coded Bandwidth", "Blocks With Spectral Extension" (E-AC-3; wave 1) | not a series (residue_end is fixed per residue) | the signaled "Bandwidths", a different figure | "Coded Bandwidth" (wave 1) | "Coded Bandwidth" (wave 2) | none | "Coded Bandwidth" of the core (wave 2) | none: time-domain prediction, no frequency bands | none: as FLAC | none: as FLAC | none: PCM |

- **Not yet read, and what each needs:**
  - **DTS.** SUMF and SUMS are read and kept in `Facts`; JOINX and SUBS are read (the audio coding header, code at 2310aa3) but live only in the syntax walker's arrays (`_joinx`, `_subs`). Keeping them costs a few bytes per frame in the kept record (`UnitRecord`: 1 byte per channel for JOINX, and the coded bandwidth's 8-byte value is built from SUBS), or they are recorded while reading; either way no new per-frame state beyond that. They are wave 2. The transient modes (TMODE) sit in the core subframes, behind side information of variable length (prediction modes, bit allocation, transition modes; ETSI TS 102 114 clause 5, section number **unverified** here). They need a subframe side information reader, which the plan puts under "Later"; they are a later wave.
  - **Opus CELT.** Its transient flag, intensity and dual stereo, and the mid/side coding of a stereo band (CELT's default unless dual stereo is set, RFC 6716 clause 4.3, **unverified** here), are coded by the range decoder in the frame. The Opus parser reads the TOC byte and the frame packing and does not run the range decoder, so none of these can be graphed until the entropy pass: a later wave.
- **Naming decision.** The concept is one heading; the series keep the names of their tallies, so no golden is renamed for it:
  - **AC-3's rematrixing is grouped under "Stereo Coding" and keeps its specification name.** It codes the left and right channels' bands as sum and difference (A/52 5.4.3.20, 2/0 only), the operation mid/side stereo names in Layer III and AAC. "One concept, one name" argues for renaming it; against that, the existing node "Blocks With Rematrixing" and "Bands Coded as Sum/Difference" are the specification's words, rematrixing is chosen per band and block and exists only in 2/0, and a rename would change every AC-3 golden for a label. The heading carries the comparison, and the descriptions carry the concept: the descriptions of the rematrixing nodes and series (and of DTS' SUMF and SUMS) and the `mid-side-stereo` article word it as mid/side coding, for example "mid/side (sum/difference) coding of the band". The node names stay the existing specification-derived ones; renaming would change every AC-3 golden for no gain in meaning, since the description says the shared concept. `CLAUDE.md` is not changed.
  - **AC-3's coupling is grouped under "Stereo Coding" with intensity stereo and keeps its name,** since both replace high-frequency detail of several channels by one shared signal with per-channel scaling (the `intensity-stereo` and `coupling` articles say so; the claim is only that they are alike, not equal). Coupling applies to more than two channels, so the heading is the user's comparison, not a claim of identity.
  - **Written out:** the abbreviations for mid/side and intensity stereo never appear; "mid/side", "intensity stereo" and "sum/difference" are written out wherever they appear.
- **In the GUI** the Graphs tab lists a stream's series under these headings, "Stereo Coding", "Block Switching", "Coded Bandwidth" and "Bit Distribution" (the bitrate series, "Channel Bit Share"), and then "Other" for the rest (modes, frame sizes, submodes). The same heading in an MP3 and an AAC file holds that file's own series, so a user finds the same question in the same place. The bitrate series ("Bitrate", "Bitrate Over Time", "Reservoir Fill") get no `Concept` in the contract pull request, since setting it would add `concept` to every golden that has series; the GUI places them under "Bit Distribution" by their unit: kbps for the bitrate series, as `GraphsViewModel.For` already recognizes them, and bytes for "Reservoir Fill", which `For` learns in the contract pull request. A format's pull request may set the concept on them later, together with its golden changes. A heading with no series for the file is not shown.

#### The lossless formats under these headings

FLAC, Monkey's Audio, TTA and WAV and AIFF (decision 11), beyond the three rows of the table:

- **Bit Distribution:** "Bitrate", "Bitrate Over Time" and the new "Compression Ratio" with its per-second form, in FLAC, Monkey's Audio and TTA, with the concept `bitDistribution` and their quantities set in each format's pull request, as MPEG set them. PCM in WAV and AIFF has a constant bitrate and no series.
- **Prediction (new, decision 12):** a fifth `GraphConcept`, `Prediction` (JSON `prediction`), for the predictor a lossless coder chooses per frame or block. No shared heading fits it: it is neither stereo coding nor block switching nor bandwidth nor bits. It holds:
  - FLAC: "Subframe Types", "Fixed Predictor Orders", "Linear Predictor Order" and "Coefficient Precision" (wave 2);
  - WavPack: its "Decorrelation passes" (wave 2), which would otherwise sit under "Other";
  - Monkey's Audio and TTA: none, since their filters are fixed per file (the compression level) or per bit depth.
  - The GUI lists it after "Bit Distribution" and before "Other". Adding it changes `Core.Abstractions` (the enum), the JSON writer and the GUI's headings, so it is a small pull request of its own before FLAC's, as [the rollout](#rollout) asks of a change to the contract. If the lead rejects it, these series have no concept and sit under "Other".
- **Other:** FLAC's "Rice With 5-Bit Parameters", "Partition Orders" and "Subframes With Wasted Bits", which only FLAC chooses per frame among the formats surveyed, and Monkey's Audio's "Silent Frames" of mono streams.

### Channel bit share

A numeric `Series` "Channel Bit Share" for MPEG Layers I to III (wave 1, with MPEG), `Concept` `BitDistribution`. The lead wants it to show how a dual channel stream splits its bitrate.

- **Type:** one numeric series, not one per channel and not categories: the value of a frame is the first coded channel's share of the frame's audio bits, in percent, so the second channel's share is the rest. Unit "%", with its own x (the offsets of the frames that have a share), so a frame without a share has no point: a mono frame in a stream that changes channel modes, a frame whose two channels have no attributable bits.
- **Absent for mono.** A mono stream has no series; a stream with two channels (stereo, joint stereo, dual channel) has one.
- **Layer III:** part2_3_length of each granule and channel, summed over the frame's granules (two in MPEG-1, one in MPEG-2), from the kept side information. It counts the scale factors and the Huffman-coded values, not the header, side information or padding.
- **Layers I and II:** the bits the frame gives each channel's subbands: allocation fields, scale factors and samples, from the allocation and scale factor selection in the kept audio data. Above the bound of a joint stereo frame only the allocation and the samples are coded once for both channels (2.4.1.5 for Layer I, 2.4.1.6 for Layer II) and are attributable to neither: those bits are left out of both shares and of the total. The scale factor selection (Layer II) and the scale factors are coded per channel there and count into each channel's share. The description says so. The bits of grouped Layer II samples follow from the allocation alone: a grouped codeword of 5, 7 or 10 bits covers three samples for 3, 5 or 9 levels (Annex B's quantization classes), and `minimp3_side_info.c` already prints the quantization class per subband and channel (17 to 19 for grouped).
- **First channel:** left in left/right, dual channel and intensity stereo; mid in a mid/side frame. The description says so, and the views name the channels "first" and "second" with left and right in the tooltip for the frames that are left/right.
- **Statistics:** a node "Channel Bit Share" with the lowest, bit-weighted overall and highest share of the first channel, and a per-second series "Channel Bit Share Over Time", anchored to it, as "Coded Bandwidth" is. The overall share is the sum of the first channel's bits over the sum of both channels' bits. The first channel is left in some frames and mid in others, so the per-second and overall figures mix left/right and mid/side frames in a stream that switches; the descriptions of the series and the node say so plainly.
- **Memory:** 16 bytes per two-channel frame (value and own x), derived at the end; see the table.
- **What the standard requires, and what LAME documents.** ISO/IEC 11172-3 gives no rule for dividing the bits between the channels of a dual channel stream; that the standard does not require an equal split is the lead's reading and is **unverified** against the standard's text, which is not in the repository. LAME's documentation says otherwise of its own encoder: the 3.94 manual (`doc/html/switchs.html`, `doc/man/lame.1`) and the 3.99.5 man page say that in dual channel mode "each channel will have exactly half of the bitrate". The `USAGE` of LAME 3.94beta, 3.98beta8, 3.99alpha12 and 3.99.5 lists "DUAL CHANNELS   Not supported." in its stereo section, while all four still offer `-m d`. These are statements of documentation, not observations; the design and the articles do not repeat them as fact about the encoder.
- **A fixture shows what the series would:** `tests/fixtures/mpeg/external-lame-dual.mp3` exists (`lame --quiet -t -b 96 -m d`, 0.5 s of a 440/660 Hz tone at 32,000 Hz, 15 frames; its golden, `external-lame-dual.report.json`, names the encoder "LAME 3.100" from the ancillary data). From its golden's "Part 2 and 3 Length" nodes, the first channel's share of the frame's part2_3_length is between 45.0% and 52.5% over the 15 frames (a computation made for this design, not yet an analyzer output). That is one test file and says nothing about the encoder in general.
- **Fixtures planned:** the MPEG pull request adds a dual channel file of several seconds with different content in each channel, from LAME `-m d` at constant bitrates, and a Layer II dual channel file (`tests/fixtures/layer2/dual-channel.mp3` exists), beside a joint stereo and a plain stereo file for contrast; the series and the cross-check (mp3guessenc's frame sizes, minimp3's side information for part2_3_length) pin the numbers.
- **Wording rule (`CLAUDE.md`):** an article may state how an encoder divides the bits only as observed behavior, with the encoder version and settings, scoped to "Encspect's test files", naming exceptions and saying the analyzer does not use it. Until the fixtures above back a sentence, the observation stays in `docs/formats/mpeg-audio.md`, and the `joint-stereo` article says only what the series shows.

### Coded bandwidth

"Coded Bandwidth" is the upper edge of the highest unit of frequency (subband, scalefactor band, bin or coefficient) that a frame's data codes something for, read from the coded syntax without decoding. Formats state it in two ways, and the description of each series says which:

| Format | Value | Kind |
| --- | --- | --- |
| Layers I and II | highest subband with a nonzero bit allocation in either channel | a stated bound |
| AAC | max_sfb of each channel stream, to the end of its band | a stated bound |
| AC-3, E-AC-3 | chbwcod end, coupling end, spectral extension begin | a stated bound |
| Musepack | highest subband with a resolution other than 0, noise-substituted subbands included | a stated bound |
| DTS core | highest active subband (SUBS + 2) | a stated bound |
| WMA standard | the last coefficient coded nonzero | a coded value, at coefficient resolution |

- What it leaves out, per format: SBR, spectral extension and DTS joint intensity coding add content above or within it; noise substitution and intensity stereo are coded within it (Musepack's noise subbands count as coded, as above; AAC's and WMA's noise-substituted bands are not removed).
- A numeric `Series` "Coded Bandwidth", unit "Hz", per frame, at the frame offsets the bitrate series uses (WMA's own, per block), with `Concept` `CodedBandwidth`. `Series` keeps its meaning; it gains only the optional `Concept`.
- Per frame, the highest over the frame's channels and blocks: a graph of the stream's reach, which a lowpass shapes. Per-channel values are left for later.
- FLAC, Monkey's Audio and TTA predict samples in the time domain, and PCM stores them, with no subbands, bands or coefficients, so the lossless formats of wave 2 have no coded bandwidth. A lowpass in their audio is a figure of the PCM analyses.
- A Statistics node "Coded Bandwidth" with the lowest, most common and highest value, and a per-second series "Coded Bandwidth Over Time" (each second's highest), anchored to it, so the text tree shows it as it shows "Bitrate Over Time".
- It is not the lowpass:
  - The LAME tag's "Lowpass" is the encoder's setting.
  - A lowpass measured from decoded audio would be a figure of the host-side PCM analyses of the decoder design.
  - The articles keep the three apart. Opus's signaled "Bandwidths" is a fourth thing and is named as such in the article.

### Memory budget

Bytes added per unit to what the report keeps now, against the bounds `MemoryTests` holds:

- A category series costs its values (1 byte each), a count byte per unit in a variable layout, a weight byte per value where weights are used, and 4 bytes per 1,024 units of index. A grouping costs nothing per value. A numeric series costs 8 bytes per value.

| Stream | Added per unit | Kept today (plan, `MemoryTests`) | Bound in `MemoryTests` (kept, peak) |
| --- | --- | --- | --- |
| MPEG-1 Layer III stereo | 23 bytes: channel modes 1, stereo coding 1, joint stereo modes 1, block usage 4, channel bit share 16; block sizes is a grouping | about 130 bytes per frame | 250, 350 |
| MPEG Layer II stereo | 27 bytes: channel modes 1, stereo coding 1, intensity stereo bounds 1, coded bandwidth 8, channel bit share 16 | as above | 250, 350 |
| AAC stereo | 13 bytes: window sequences 2 and its count byte, stereo coding 1 and its count byte, coded bandwidth 8 (all recorded while reading) | 91 to 116 bytes per access unit | 220, 250 per frame (ADTS); 110, 180 per sample (MP4) |
| AC-3 2/0, 6 blocks | 26 bytes: block switching, coupling, rematrixing 6 each, coded bandwidth 8; fixed layout, no count byte | 70 bytes per mono frame, measured on a 1/0 fixture | 220, 300 |
| Smallest E-AC-3 frames (1 block) | 16 bytes: 4 flag series of 1 value and a count byte, coded bandwidth 8 | 46 bytes | 90, 180 |
| E-AC-3 5.1 with a dependent frame | about 60 bytes: 12 blocks in each of 4 series, 4 count bytes, coded bandwidth 8 | not measured | not yet set |
| Opus | 7 bytes per single-stream packet (a fixed layout): three TOC series of a code and a weight byte each, channel counts 1; a multistream file adds the same per further stream | 40 bytes per packet | about twice that |
| Speex | 1 or 2 bytes per frame | 30 bytes per packet | 80, 110 per packet |
| Vorbis | 1 byte per packet (modes; block sizes is a grouping) | 26 bytes per packet | about twice that |
| Musepack SV7 | 9 bytes: mid/side flag 1, coded bandwidth 8 | 42 bytes, peak 59 | about twice that |
| WMA standard | 19 bytes per block: block sizes 1, mid/side flag 1, noise substitution 1, coded bandwidth with its own x 16 | not measured | not yet set |
| FLAC stereo | 48 bytes: stereo coding 1; subframe types, fixed predictor orders, residual coding method, partition orders and wasted bits 3 each (2 subframes and a count byte); linear predictor order 8 and its own x 8; coefficient precision 8 (the same x); compression ratio 8. A variable block-size stream adds block size 8 | a 56-byte entry and 16 bytes of the bitrate graph per frame, and the subframe records of the first 4,096 frames (`ManyFlacFramesStayCompact`) | 200, 260 |
| Monkey's Audio | 9 bytes per frame: stereo coding or silent frames 1, compression ratio 8; a frame lasts 1.67 s or more at 44,100 Hz | parser not built | not yet set |
| TTA | 8 bytes per frame: compression ratio; a frame lasts about 1.045 s | parser built without the compression ratio (`ManyTtaFramesStayCompact` measures 56 kept and 80 peak bytes per frame in all) | not yet set |

- Every addition fits the bounds as they are, with two things to measure, not assume: AAC in MP4 (13 added to the plan's 91 bytes per access unit, measured on a 195 MB M4A, against a bound of 110 per sample; `MemoryTests`' own MP4 test measures about 60 per sample on linux-x64, so the margin there is larger, and the AAC pull request states which of the two figures it measures against) and E-AC-3 with dependent frames, for which the AC-3 pull request adds a memory test whose bound is its measured figure plus this addition. No existing bound is raised. If AAC in MP4 reaches 110, the AAC pull request says so and the lead decides; nothing is raised silently.
- FLAC adds the most of the lossless formats, and records its subframe series and two numeric series while reading, so its peak grows by them, and by the copy of the numeric values into arrays at the end that the memory test's comment describes for the bitrate. The FLAC pull request measures both figures against 200 and 260 rather than assuming them.
- The earlier draft's AC-3 figure added stereo series to a baseline measured on mono, and left out the E-AC-3 dependent frames and WMA's own x; the table above does not.
- The peak grows by the codes only for the series recorded while reading (AAC, AC-3); the series derived at the end are built in the pass that builds the numeric series, and their chunks are not copied when built.
- Each format's pull request adds a check that the report keeps at most the bytes above per unit per series, from `CategorySeries`' own size, so a regression that keeps more fails there and not only in the total.
- A crafted file cannot make a series grow faster than the records it already keeps, by the caps above: 254 coded values per unit, 2^24 per series. `ComplexityTests` is sized on the real worst case: a 64 KiB raw data block of the smallest single channel elements the syntax allows, and ADTS frames of several such blocks (the test measures how many fit), E-AC-3 frames of one block in 5 channels with 8 dependent frames, and Opus multistream packets. Its time and memory stay linear in the file's length. An Opus packet of more than 254 stream packets (a family 255 file) is coded in its first 254 and counted in full.

## Output

### JSON

Each stream gets `categorySeries` after `series`, **omitted when the stream has none**, as the writer would otherwise add an empty array to every golden. The example is the Layer III stream of the text example below (120 frames, 40 per second):

```json
"categorySeries": [
  { "name": "Block Usage",
    "valueLabel": "granule and channel",
    "valueNames": ["granule 1, channel 1", "granule 1, channel 2", "granule 2, channel 1", "granule 2, channel 2"],
    "categories": [ { "name": "Long", "count": 432 }, { "name": "Start", "count": 12 }, { "name": "Short", "count": 24 },
                    { "name": "Mixed", "count": 0 }, { "name": "Stop", "count": 12 }, { "name": "Reserved", "count": 0 } ],
    "total": 480,
    "xSeries": "Bitrate",
    "runs": [[0, 198], [1, 2], [2, 4], [4, 2], [0, 76], [1, 2], [2, 4], [4, 2], [0, 52], [1, 2], [2, 4], [4, 2],
             [0, 24], [1, 2], [2, 4], [4, 2], [0, 24], [1, 2], [2, 4], [4, 2], [0, 24], [1, 2], [2, 4], [4, 2], [0, 34]],
    "seconds": [0, 40, 80] } ]
```

The codes in `runs` are indices into `categories` (Long 0, Start 1, Short 2, Mixed 3, Stop 4, Reserved 5).

- `concept`: `stereoCoding`, `blockSwitching`, `codedBandwidth` or `bitDistribution` on a series (numeric or category) that has one; absent otherwise. Front ends group by it and never by name.
- `categories`: every category of the series, with `count` (weighted, uncoded values included); with `--explain` also `description` and `topic`, and the series' own.
- `valueNames`: present for a fixed layout of more than one value; `valuesPerUnit` then gives the number. A variable layout writes `valueCounts` as runs of `[count, units]`.
- `uncodedValues`: present only when not zero.
- `xSeries` names the per-frame series of the stream whose `x` the units share, as the model carries it (`CategorySeries.XSeries`); where they share none, `x` holds the offsets as a series' does. In the model, `x` given to `Build` must be `xSeries.X` when `xSeries` is given, and `Build` throws `ArgumentException` otherwise.
- `runs`: the codes as runs of `[code, length]`, value by value within each unit and unit by unit, `null` for a value without a category. Runs keep a long stream's JSON short (a CBR file of long blocks is one run) and a golden diff readable.
- `weights`: runs of weights, present only where a weight is not 1.
- `seconds`: the first unit of each interval of `TimeIndex`, whose starts are the `x` of the stream's "Bitrate Over Time"; absent without an index.
- A grouping writes `groupOf` (the source's name) and `map` (the new category of each source code), with no runs.
- **Schema version:** a field is added and none changes meaning, so `schemaVersion` stays 1, as the plan's rule and the decoder design's `verification` do. A consumer that reads `series` sees the new numeric series ("Coded Bandwidth") as further entries of the same shape.

### Text

- After a series' anchor and its children, at the anchor's depth as `WriteSeries` writes anchored series, one line per category that occurs, in the series' category order, with its share of each interval in percent, rounded half to even to 0.1, as the tallies round. The names follow "Bitrate Over Time": the category, "Share Over Time". An interval without values prints "-".

```
      Block Usage: 480 granules
        Long: 432 granules (90.0%)
        Start: 12 granules (2.5%)
        Short: 24 granules (5.0%)
        Stop: 12 granules (2.5%)
      Long Share Over Time: 100,90,80
      Start Share Over Time: 0,2.5,5
      Short Share Over Time: 0,5,10
      Stop Share Over Time: 0,2.5,5
```

- The example stream is invented for round numbers: 3 seconds of 40 frames, 4 values per frame; seconds 2 and 3 hold 2 and 4 switches, each a frame with start blocks in granule 2, a frame of short blocks and a frame with stop blocks in granule 1. The JSON runs above follow from it.
- A series without a time index prints no lines; its summary is the tally above.
- With `--offsets`, the anchor's location, as anchored series have now. With `--hide statistics`, the anchor is hidden and the lines with it. With `--explain`, each line's category description.
- The per-frame codes are not printed in the text tree, as per-frame numeric series are not.

### CSV

- No change in this rollout. Shares of block types or stereo codings are format-specific, and a column per share would be empty for most files.
- Leaving it alone also leaves `ReportSummary` and the GUI's summary cache format alone, which the decoder contract bumps to 6.
- Considered: `Short Block Share` and `Mid/Side Share` for the first audio stream, from its first matching series. A later step, if folder exports need them.

### Golden reports

The contract changes no golden: it writes `categorySeries` and the share lines only for a stream that has category series, and no parser produces one yet. The contract's pull request shows this by running the golden tests unchanged. Each format's pull request then changes the goldens of the fixtures whose streams get series, new nodes or renames:

- MPEG: the fixtures in `mpeg`, `layer2`, `layer3`, `vbr`, `stats`, the tag fixtures that hold MPEG audio, and the MP4 and WAV payloads the MPEG codec parser reads. The pull request counts them with a script and names the count.
- AAC: `aac`, `adts`, and the `mp4` and `matroska` fixtures with AAC tracks (about 100 files hold the node "Bandwidth" now); AC-3: `ac3` and its MP4 and Matroska tracks; Opus: the Opus fixtures in `ogg` and their MP4 and Matroska tracks (143 files hold the node "Stereo Coding" now); Vorbis: 73 files hold "Long Blocks"; and so on.
- **Renames and their goldens:** the Opus pull request renames "Stereo Coding" to "Channel Counts"; the AAC pull request renames "Bandwidth" to "Coded Bandwidth" and changes "Channel Pairs" counts to the classification rule; the Vorbis pull request moves "Long Blocks" and "Short Blocks" under "Block Sizes". Each format regenerates its own goldens in its own pull request, so a rename adds no golden file to a pull request beyond those the new series change anyway.
- **Beyond the goldens, the renames touch the models and manifests:** `scripts/fixtures/vorbis_opus_model.py` writes `statistics["Stereo Coding"]` and its "Stereo" and "Mono" entries (lines 981 to 983) and "Long Blocks" (lines 585 and 604), and the manifests derived from that model change with them (Opus and Vorbis pull requests); the AAC model's expectations for "Channel Pairs" and "Bandwidth" change with the classification rule and the rename (AAC pull request). Each pull request lists these files in its description; the change file describes golden contents only.

How the diffs stay reviewable:

- **Run-length codes,** above: a golden gains a few dozen lines per series, not one per frame.
- **Two commits per format:** the code with its tests, then the regenerated goldens (`ENCSPECT_UPDATE_GOLDEN=1`) alone.
- **The check, `scripts/ci/golden_additions.py`**, written and tested in the contract's pull request (with `test_golden_additions.py`, as the other `scripts/ci` scripts have). It is opt-in: a pull request that changes goldens without a change file is reviewed as today.
  - It compares goldens at the pull request's merge base with its head, so it does not depend on finding "the golden commit" and survives a squash merge.
  - It compares **parsed trees**, never bytes: it parses both JSON documents, removes what the change file lists as added, reverses what it lists as renamed or moved, ignores the fields it lists as changed, and requires the rest to be equal. Re-serializing in Python does not reproduce the writer's indentation and number formatting, and byte equality is not what matters.
  - It handles **path shifts**: a finding's `path` is a list of child indices, and a node added among a node's children moves the indices behind it. The change file gives each added node's parent and position (and each moved node's old and new), and the script maps every old path through them before comparing.
  - **Where the tables live:** one file per format pull request, `tests/fixtures/<format>/golden-changes.json`, committed in that pull request's golden commit: four kinds of entry, each naming the goldens it covers by path or folder (a file may cover goldens in `mp4/`, `matroska/` or `ogg/` as well as its own folder):
    - **added:** keys and nodes (`categorySeries`, the "Coded Bandwidth" series and node, the new tally nodes), with parent and position;
    - **renamed:** old name, new name, parent;
    - **moved:** the node, its old parent, its new parent and position (Vorbis' "Long Blocks" and "Short Blocks");
    - **changed:** the path and field whose value may differ (AAC's "Channel Pairs" counts).
    The script itself is not edited per pull request.
  - **Who runs it:** a step of the CI workflow's test job, only when the pull request's diff adds or changes a change file. It reads only those files; change files of earlier pull requests stay committed and are ignored. A reviewer can run it locally with the same arguments. Adding the step changes the CI workflow, which Decision 10 puts to the lead.
- `golden_hides` (development.md) does not hide category series: they are small, and the golden is where a per-frame change shows.

## GUI

### Charts

The Graphs tab lists a stream's series under the headings of their `Concept` ("Stereo Coding", "Block Switching", "Coded Bandwidth", "Bit Distribution", then "Other", from [The same concepts in every format](#the-same-concepts-in-every-format)), and offers each category series as a chip, as it offers each `Series` now, with four forms:

- **Over Time:** a 100% stacked area with stepped edges (decision 13) over the intervals of the time index: one level per interval with a vertical step at each boundary, the category with the largest share over the stream at the bottom in the muted step, and the rarer layers above it in full colors so that they stand out (colors follow the stack in this form, decision 13); the layers carry no names, the legend in the order of the stack names them (decision 13). Where intervals outnumber pixel columns, neighboring intervals merge into one level, weighted by their values, as a line of more points than pixels draws each column's smallest and largest value now. A partial last interval that holds fewer than 8 frames is hatched and named in its hover text and in a note by the caption. **Smoothed** is the same area averaged over 5 intervals, offered above 30 intervals only.
- **Frames:** a strip, one row per value for a fixed layout of up to 8 values (Layer III's granule 1 and 2 of each channel, AC-3's 6 blocks), each unit a cell in its category's color. A variable layout, or a fixed one of more than 8 values, draws one row whose cell shows the shares of the unit's values stacked. Where units outnumber pixel columns, a column shows the shares of its units as a stacked cell.
  - **Rare categories stay visible:** every category present in a column gets at least 1 pixel of its stack, taken from the largest, and a column holding a category in at most 1 of its units is marked with a one-pixel tick above the row in that category's color. At 100,000 frames over about 1,000 columns, a single short granule of 400 values would be 0.03 pixels without this rule. The hover text says the column is drawn with its rare categories enlarged, and gives the true counts.
  - Rows use `CountRange(first, end, value, counts)`, the shares lane `CountRange(first, end, counts)`.
- **Channel bit share** (a numeric series of the concept `BitDistribution`): the line graph, and below it a view of the left and right shares over time: per interval a 100% column of two segments, the first channel at the bottom, in the first two palette hues, with the 50% line marked. Hovering says "first channel 48.3% · second channel 51.7% (left/right)". Clicking selects the interval's first frame, as for the other per-second graphs.
- **Summary:** a table like EncSpot's: each category's name, count and share, with a horizontal bar. A "Coding Summary" chip shows the tables of every category series of the stream at once (for MPEG: channel modes, stereo coding, joint stereo modes, block usage, block sizes), which is EncSpot's panel and delivers its shares over all frames from the report's own figures. Series with uncoded values say how many.

"Coded Bandwidth" is a `Series` and draws as a line, with no change to the line graph.

### Interaction

- **Hovering:** an interval shows itself and every share, "0:12 to 0:13 · Short 12.5% (24 granules) · Long 87.5% (168 granules)", led by "Last 0.03 s, 1 frame" for a last interval of few frames; a strip cell its unit, "Frame 1,234 · 0x1A2B · granule 2, channel 1: Short"; a summary bar its count and share.
- **Clicking** an interval selects the first unit of its interval, from `TimeIndex.FirstUnits`, exactly rather than estimated from the duration as per-second points are now; an empty interval selects nothing. A strip cell selects its unit. A summary bar, or a legend entry, selects the next unit after the selected one that has its category, so choosing it again steps through them, as a bar of the bitrate distribution does.
- **Selecting** a frame in the tree marks it: a vertical line across the over-time and strip views, in the foreground color.
- **Keyboard:** the chips and views are reached with Tab; Left and Right move a cursor from unit to unit in the strip and from interval to interval over time, Enter selects. The existing line graph gets the same keys.

### Palette

- A fixed categorical palette, not the system accent, so a category has the same hue in every file and in both modes (the two modes use different steps of one hue, not the same color). Index 0, the plain coding, takes the first hue in a muted step; in the over-time forms the palette's slots go to the layers in the order of the stack, so the bottom layer, the largest, is the muted one (decision 13).
- The source is the default palette of the `dataviz` skill (its `references/palette.md` and validator), which is not a file of this repository. The implementation commits the six values in `Palette` and the validator's output in [gui.md](../gui.md), so the claim becomes checkable. Its first six slots: blue, orange, aqua, yellow, magenta, green. Light: `#2a78d6`, `#eb6834`, `#1baf7a`, `#eda100`, `#e87ba4`, `#008300`. Dark: `#3987e5`, `#d95926`, `#199e70`, `#c98500`, `#d55181`, `#008300`.
- The validator's results, run for this design and reproduced by the review: worst adjacent separation for color vision deficiencies ΔE 9.1 (light) and 8.4 (dark), normal vision 19.6 and 19.3; **tritan separation 5.8 in light mode, below the method's floor of 6**, which this design accepts only because names and shares are always in text (Decision 8). Against the method's reference surfaces, aqua, yellow and magenta are below 3:1 in light mode (2.74, 2.11, 2.62).
- **Against the app's graph surfaces:** sampled from the Graphs tab screenshots of an earlier session (not committed), the surfaces are `#ffffff` in light mode and `#000000` in dark. The same validator gives aqua 2.82, yellow 2.17 and magenta 2.69 below 3:1 in light mode (which obliges the visible labels and the table below) and all six at 3:1 or more in dark. The implementation reads `SystemRegionBrush` in both modes, runs the validator with the theme's colors and records the result in a test; a surface that changes is then caught.
- **More categories than colors:** a series uses the six hues for its first six categories, in the order the parser gives them.
  - An **ordered** series (`IsOrdered`: frame sizes, block sizes, submodes, Opus bandwidths) uses a ramp of one hue, light to dark in order, up to eight steps.
  - Any category past the sixth (past the eighth for a ramp) is drawn in one flat mid gray, "Other", in the strip and the area. The legend, the summary table and the hover list every real category by name and share, so nothing is lost but the color. This covers Speex's 9 narrowband submodes plus "no wideband layer", Vorbis' up to 64 modes, Opus' 6 frame sizes and WMA's 5 block sizes. A generated hue is never added.
- A "Reserved" or other invalid category is drawn in gray with a hatch, never in a hue of the palette, since it marks a value the specification does not allow; it is distinct from the flat gray of "Other".
- A value without a category is left empty, the surface showing through.
- Stacked segments of a strip column are separated by a 2-pixel gap in the surface color, and the layers of the over-time area by a 1-pixel line of it, so neighbors of similar lightness stay apart; strip cells narrower than 4 pixels have none.

### Accessibility

- Never color alone: a legend with names and shares is always shown; the summary is a table with text; every hover says the category by name; the segments of the strip carry their names directly where wide enough, in black or white, whichever has the higher contrast with the color (at least 4.5 : 1 on every palette color); the layers of the over-time charts carry no names, so the legend lists them in the order of the stack with their shares, the hover text lists the shares in the same order, and the graph's automation text carries both. Adjacent layers differ little in lightness (WCAG contrast ratios between neighbors, bottom to top: 1.18, 1.14, 1.30, 1.24 and 1.84 in light mode, with the muted first hue; 1.69, 1.14, 1.11, 1.28 and 1.25 in dark mode), so they are told apart by hue (the validator's adjacent separations above) and by the one-pixel surface line between layers at least 3 pixels high; a thinner layer has none and borders a layer of another hue.
- A hatch is available per category for high contrast settings. How Avalonia 12 exposes the system's contrast preference to the application is **unverified**; the implementation names the API it uses, or the hatch is a setting of the tab.
- The views are reachable and usable by keyboard (above), and the chips and legend entries have automation names.

### Speed

- A view aggregates once per width and per series, in one pass over the codes (`CountRange` per pixel column), and draws at most columns × categories rectangles in the strip and one outline for each layer and run over time, from per-interval counts a graph keeps once. For 100,000 frames of 4 values, that is 400,000 bytes read per resize.
- No timing is measured yet. The test below bounds it with the line graph's existing test as the reference.

### Tests

- `GraphTests`: aggregation per column against a brute-force count, weights, `None` values, intervals merged into columns, the frame each click selects, the next unit of a category after the selected one, the rule for rare categories (a lone short granule keeps at least one pixel), the "Other" fold past the sixth category.
- `GraphViewTests`: a series of 200,000 units in 4 values draws and answers the pointer within the bound the 200,000-point line test uses.
- `ScreenshotTests`, light and dark: the Graphs tab of an MPEG fixture with short blocks on each of its forms, with a scripted series of more than 30 seconds for the smoothed one, and of an AAC fixture's window sequences; saved with `ENCSPECT_SCREENSHOTS` for review, as the existing screenshots are. Which fixtures carry short blocks is found by script (the golden's block usage counts), not assumed.
- A pixel test that needs no image comparison: a scripted series of known categories, rendered headless, sampled at fixed columns, whose colors must be the palette's.
- `DetailViewModelTests`: the Coding Summary lists the categories and counts the series carry, read from the stream and not the tree.

## Descriptions and articles

### Descriptions

- Every `CategorySeries` has a `Description` saying what one value stands for and what the series counts ("The block type of each granule and channel of every complete audio frame, counted per channel as the Block Usage statistics count them (ISO/IEC 11172-3 2.4.2.7)").
- Every `Category` has one, written once as a constant by the parser that knows the field, citing the section: Layer III's "Short" is "block_type 2 without mixed_block_flag: three short transforms in every subband (ISO/IEC 11172-3 2.4.2.7)".
- The tally entry and its category share the constant, so the tree and the graph say the same.
- Every "Coded Bandwidth" series says how its format derives the value, whether it is a stated bound or a coded value, and what it leaves out.
- `DescriptionTests` gains the walk over every fixture's category series: a series or category without a description, or with a topic that does not exist, fails.

### Articles

Existing articles, extended with one paragraph each, where they say what you see:

- `block-switching`: the Block Usage and Block Sizes graphs and summary, and how the other formats' block mechanisms correspond (AAC's window sequences, AC-3's block switching, Vorbis's two block sizes, WMA's block sizes), each linking its own article.
- `joint-stereo`: the graphs of stereo coding and joint stereo modes over time, and EncSpot's counting of every frame that is not joint stereo as mode 0: its mode 0 share is the report's "Left/right stereo", "Dual channel" and "Mono" entries together, which the report now shows apart.
- `lowpass`: that coded bandwidth, from the syntax, shows where the coded spectrum ends without decoding, and why it is not the lowpass.
- The format articles as their formats land: `aac-windows-and-blocks`, `ac3-audio-blocks`, `vorbis-blocks`, `opus-toc`, `speex-modes`, `musepack-subband-coding`, `wma-frames-and-blocks`.

A new shared article, `coded-bandwidth`:

- The problem: encoders spend no bits on frequencies they filtered out or chose not to code, and the coded data says where they stopped.
- How each format states it, and whether that is a stated bound or a coded value (the table above): Layers I and II's bit allocation, AAC's max_sfb, AC-3's chbwcod and coupling range, Musepack's used subbands, DTS's active subbands, WMA's last coefficient. Opus states its bandwidth in the TOC, which is a different figure, "Bandwidths".
- What it does not show: SBR and spectral extension above, noise substitution, intensity stereo and joint intensity coding within, quiet content that the coded data still reaches.
- How it relates to the LAME tag's lowpass and to a lowpass measured from decoded audio.
- References: the sections in the survey above. About 300 words.

Observations of how often an encoder switches blocks or codes mid/side go into articles only with the fixture backing `CLAUDE.md` asks for, and none is planned here.

## Verification

### Per-frame cross-checks

The tools CI already builds or installs give per-frame references for some series; for the rest the references are the project's own models, which are checks of consistency and not independent readers, and the table says which is which.

| Format | Per frame or block | Per stream |
| --- | --- | --- |
| MPEG Layer III | minimp3's side information per granule and channel (`minimp3_side_info.c`): block_type and mixed_block_flag; it rejects reserved blocks, so the reserved fixture compares as "rejected" | mp3guessenc's block usage and stereo mode counts; EncSpot's mode and block counts, through an emulation of its counting (above) |
| MPEG Layer II | minimp3's allocation per subband and channel (same tool): the highest allocated subband | mp3guessenc |
| AAC | faad2's trace (`faad_trace_tool.py`): window_sequence, max_sfb, ms_mask_present, ms_used, every band's codebook; the pair classification is computed from these by the script | the tally |
| AC-3 | liba52 0.7.4's `a52.h` declares `a52_state_t` with `chincpl`, `cplendmant`, `rematflg` and `endmant[5]`: extending `a52_blocks.c` (which reads `buffer_start` and `bits_left` after each `a52_block()` now) to print them cross-checks coupling, rematrixing and the end mantissa per block. **Block switching has no per-block reference:** `blksw` is a local array of `a52_block` (`parse.c`) and is not exposed. | the tally |
| E-AC-3 | none: liba52 does not decode E-AC-3. The hand-built fixtures of `ac3_model.py` carry the expected values in their manifests | the tally |
| Musepack SV4 to SV7 | libmpcdec's used subbands and mid/side mask per frame (`mpcdec_frames.c`) | the tally |
| Opus, Vorbis | the project's own model `vorbis_opus_model.py` (`toc_info`) for the TOC and the packet flags; not independent | the tally; opusinfo gives pre-skip, gain, channels, rates, counts, comments, length and hard CBR (`crosscheck_ogg_fixtures.py`), none of the modes, bandwidths or stereo counts, so it checks none of these series |
| Speex | speexdec's per-frame bitrates, which the submodes give | the tally |
| WavPack | libwavpack's block headers (`wavpack_blocks.c`) | the tally |
| WMA, DTS | the hand-built fixtures' models (`asf_model.py`, `dts_model.py`); `dca_frames.c` prints no audio coding header fields | the tally |
| FLAC | `flac -a`, which `crosscheck_flac_fixtures.py` runs already: channel assignment, block size and bits per frame, and per subframe its type, order, coefficient precision, wasted bits, residual coding method and partition order (see [FLAC](#flac)) | the tally |
| Monkey's Audio | FFmpeg's debug log names silent frames; pseudo-stereo has none, so the hand-built fixtures' model carries it; ffprobe's packet sizes for the bitrate | the tally |
| TTA | ffprobe's packet sizes, the seek table's | the tally |

- The cross-check scripts compare unit by unit, so a series that is right in sum but wrong in order fails.
- ffprobe and libavcodec's debug output have no per-frame block type or stereo mode for these codecs that the scripts read today; this design does not add one.
- The new cross-checks and the golden-additions check are documented in `docs/development.md`.

### Fixtures

- Which existing encoder fixtures carry which categories is found by a script that reads the goldens' tallies, as each format's pull request begins; this design names none as fact.
- Hand-built ones, from the existing models, for what encoders rarely write:
  - Layer III intensity stereo and both tools together; mixed and reserved blocks;
  - a stream that changes between mono and joint stereo, so values are empty;
  - MPEG-2's one granule; a truncated last frame and a VBR header frame, which no series counts;
  - Layer II intensity stereo bounds;
  - E-AC-3 frames of 1, 2 and 3 blocks, with dependent frames and a block count that changes; spectral extension;
  - AAC pairs without a common window that use intensity stereo; ms_mask_present 3; ADTS frames of several raw data blocks;
  - Opus packets of several frames and a multistream file.
- Each manifest lists the expected counts per category, which `CategorySummaryTests` holds against both the series and the tally.

### Tests

- **Contract:** `CategorySeriesTests`: building, `None`, weights, fixed and variable layouts with the sparse index, values past the caps counted without a code, groupings, `CountRange` in both forms, a `TimeIndex` that does not fit (an exception, so the host's `parse.failed`) and one with an empty interval, the 255-category limit; the JSON runs written and read back; the text lines' rounding.
- **Summary:** `CategorySummaryTests` (above), on every fixture, from its data table.
- **Descriptions:** `DescriptionTests`, extended.
- **Memory:** the existing `MemoryTests` bounds hold, each format adds its per-series check (above), and the AC-3 pull request adds the E-AC-3 dependent-frame test.
- **Complexity:** `ComplexityTests` on the worst cases above stays linear in the file's length.
- **Threads:** `ParallelAnalysisMatchTests` covers the series through the JSON it compares.
- **Decoding:** with decoding on, the category series are the same, as the decoder design holds for the tree.
- **Golden check:** `test_golden_additions.py`, with a pair of small goldens, a rename and a path shift.

## Fitting alongside the decoder contract

The decoder contract, being built on `feature/decoder-contract`, adds to `StreamInfo` the property `Verification` after `Protection`, writes `verification` in JSON before `encoder`, adds a verification line to the text tree under each stream, `Is Verified` to the CSV and `IsVerified` to `ReportSummary` (summary cache format 6), and changes several lines of `CLAUDE.md`. This design lands after it and rebases onto it. Both change `ReportTextWriter`'s `Options` record and the `WriteNode` signature (the contract adds `Verified`), so those two places are merged by hand when the contract's pull request has merged, not before; the other places are different:

- `StreamInfo`: `CategorySeries` after `Series`, the last property.
- JSON: `categorySeries` after `series`, the last field of a stream.
- Text: lines after a series' anchor node, in the tree's node writer, not in the stream lines.
- CSV and `ReportSummary`: untouched, so the summary cache stays at the decoder's 6.
- `CLAUDE.md`: one sentence the decoder contract does not touch, in "Architecture rules": "Format parsers report their streams as `StreamInfo` with series for graphs" becomes "Format parsers report their streams as `StreamInfo` with numeric series and category series for graphs, whose categories carry the names of the tally entries they count."
- **The decoder design's host-side PCM analyses** add figures from decoded audio to the stream, never nodes, through one field the first such analysis adds. Category series are the parsers', from the coded syntax, and decoding never changes them. A lowpass those analyses measure is a figure of its own, named "lowpass" as the article does; "Coded Bandwidth" stays the syntax's. Should a PCM analysis report a per-second series, it uses `Series` and `TimeIndex` as they are.

## Rollout

1. **Contract**, one pull request, after the decoder contract merges:
   - `Category`, `TimeIndex`, `CategorySeries`, `CategorySeriesBuilder` and `StreamInfo.CategorySeries`;
   - the JSON and text writers, writing nothing for a stream without category series; `scripts/ci/golden_additions.py` with its test and its CI step;
   - the GUI's forms, palette, Coding Summary and keyboard handling, tested with scripted series (`TestParsers.cs`);
   - `CategorySeriesTests`, the `DescriptionTests` extension, `CategorySummaryTests` (empty until a parser produces series);
   - documentation: `docs/cli.md` (JSON and text), `docs/gui.md` ("Graphs", and "Look and platform", where the accent color follows the system but the graph palette is fixed), `docs/development.md` (the golden-additions check and the change files), `docs/formats/README.md` ("Series"), `docs/PLAN.md` (the graph list, the sentence that the coding statistics are counted "so nothing is kept per frame", the "Large inputs" per-frame figures), the `CLAUDE.md` sentence above; the code comments that say nothing is kept per frame (`MpegAudioParser.Tally.cs`, `CodingTally.cs`) change in the pull requests of their formats.
   - No parser changes, no golden changes.
2. **MPEG, the reference:**
   - channel modes, stereo coding, joint stereo modes, intensity stereo bounds, block usage, block sizes, coded bandwidth of Layers I and II, channel bit share of Layers I to III, with the "Coded Bandwidth" and "Channel Bit Share" statistics nodes;
   - minimp3, mp3guessenc and EncSpot cross-checks; the hand-built fixtures and the dual channel fixtures; the lines for the new cross-checks in `docs/development.md`; goldens in their own commit with their change file;
   - the articles `coded-bandwidth`, `block-switching`, `joint-stereo`, `lowpass`; `docs/formats/mpeg-audio.md`, `docs/development.md` for the cross-checks.
   - A milestone review of the contract and MPEG follows, on Opus with high effort; its findings are fixed before step 3.
3. **Wave 1, in parallel, a pull request each:** AAC (with the "Stereo Coding" and "Block Sizes" nodes, the pair classification and the rename to "Coded Bandwidth"), AC-3 and E-AC-3, Vorbis (with the "Block Sizes" parent), Opus (with the rename to "Channel Counts"), Speex, Musepack. Each with its cross-check, fixtures, golden commit and change file, format page, `docs/development.md` lines and article paragraph.
4. **Wave 2,** the same way: the further MPEG and AAC tallies, DTS, WMA standard and Voice, WavPack, and the lossless formats of decision 11:
   - the "Prediction" heading, if the lead approves decision 12, in a small contract pull request before FLAC's;
   - FLAC, with the "Stereo Coding" node and its moves and renames, the "Fixed Predictor Orders" and "Compression Ratio" nodes, the `flac -a` comparison in `crosscheck_flac_fixtures.py`, and a fixture past 4,096 frames;
   - Monkey's Audio and TTA, each after its parser from step 3 of the [decoder design's rollout](decoders.md#rollout), in that parser's pull request or the one after it;
   - WAV and AIFF add no series.
5. **Later:** the series that wait for the entropy pass, WMA Pro, a CSV column if wanted.

What is serial:

- Everything in `Core.Abstractions`, the host's writers and the GUI is in step 1 alone. A format that finds the contract lacking changes it in a small pull request of its own, merged before the format's, never inside it.
- The shared articles (`coded-bandwidth`, `block-switching`, `joint-stereo`) get one paragraph or line from each format pull request: small conflicts of text, merged in order.
- `CategorySummaryTests`, `DescriptionTests` and the memory tests are generic and need no change per format beyond fixtures, the per-series check and the format's rows in the summary table.

## Decisions

Decisions 1 to 10 were approved by the project lead on 2026-10-22; 3, 4, 6 and 8 changed at approval, as marked. Decisions 11 and 12 are the lead's of 2026-10-23.

1. **Model:** a `CategorySeries` beside `Series`: one byte per value, units with a variable number of values (a count byte, a sparse index), caps that count values without coding them, optional weights, groupings that cost nothing per value, its own JSON array; `schemaVersion` stays 1. Alternative: a fixed slot count per series, which cannot hold AAC or E-AC-3 dependent frames; or one `Series` with a kind (version 2).
2. **Summary:** the parsers' tally nodes stay the summary in the tree; each series uses the same category names, is anchored to its node, and its counts are held equal to the tally by a data-driven test, with flag series for value-only tallies. Alternative: the host builds summary nodes from the series and the parsers drop their tallies.
3. **When codes are made (changed: the reason is stated):** derive at the end from the kept records wherever they hold the fields (MPEG first, then Vorbis, Opus, Speex, Musepack, WMA, WavPack), which needs no new per-frame state and allows one builder per stream; record while reading only in AAC, AC-3 and DTS, which keep their bits and not their fields and have a variable-length syntax that would have to be walked again to read a field back (MPEG's side information has a fixed layout). Alternative: record while reading everywhere, which cannot split MPEG by stream and keeps bytes the records already hold.
4. **EncSpot-style shares over all frames (changed: dual channel apart):** add the nodes "Stereo Coding" (left/right, mid/side, intensity stereo, both, dual channel, mono) and "Block Sizes" (long with start and stop, short, mixed) in MPEG and AAC, and keep "Joint Stereo Modes" and "Block Usage" as the detail. EncSpot's mode 0 is the report's left/right, dual channel and mono entries together. **Changed at approval:** dual channel and mono are separate entries, since dual channel codes its two channels independently. Alternative: keep only the joint-stereo-only tallies and let the reader add up the tables; combining them only in the GUI breaks the plan's rule that the GUI shows nothing the reports cannot.
5. **Names:** "Stereo Coding" means the four codings everywhere, so Opus's node of that name becomes "Channel Counts"; AAC's "Bandwidth" becomes "Coded Bandwidth"; Vorbis gets the parent "Block Sizes". Each format regenerates its goldens in its own pull request, with the renames in its change file. Alternative: keep Opus's and AAC's names and call the new concepts something else, leaving two names for coded bandwidth and two meanings of "Stereo Coding".
6. **Coded bandwidth and bit share (changed: Channel Bit Share added):** a numeric "Coded Bandwidth" series in Hz where the syntax states it or the coded values show it (Layers I and II, AAC, AC-3, Musepack, then DTS and WMA), each description saying which; a numeric "Channel Bit Share" series (the first coded channel's share of the frame's audio bits, MPEG Layers I to III, absent for mono) shows how dual channel splits the bitrate; Opus keeps its signaled "Bandwidths" as categories; Layer III's big_values waits for the entropy pass, since it bounds only the lines above magnitude 1. Alternative: graph big_values now as "Big Values Region End", as EncSpot recorded it.
7. **Output:** JSON writes categories with counts, the per-second index and run-length codes, and nothing for a stream without series; the text tree prints each category's share per second after its tally node; the CSV stays as it is. Alternative: CSV columns for the mid/side and short block shares, which bump the summary cache again.
8. **GUI (changed: shared headings):** per series an over-time view, a per-frame strip that keeps rare categories visible, and an EncSpot-like summary table, plus a Coding Summary of all series, with a stream's series listed under the same headings in every format ("Stereo Coding", "Block Switching", "Coded Bandwidth"); AC-3's rematrixing and coupling sit under "Stereo Coding" and keep their specification names; a fixed six-hue palette with a flat gray "Other" past the sixth category and ramps for ordered series, always with names and shares in text. Its tritan separation is 5.8 in light mode, below the method's 6, accepted because of the text. Alternative: the summary and over-time views only, without the strip.
9. **Rollout and goldens:** contract after the decoder contract (no golden changes), MPEG as the reference with a milestone review, then the wave-1 formats in parallel; goldens regenerated in a commit of their own per format, checked by comparing parsed trees with a per-format change file (additions, renames, moves, changed values, path shifts). Alternative: every format in the first wave; or hiding category series from the goldens and relying on manifests.
10. **CI step (changes the workflow):** an opt-in step that runs the golden-additions check only on pull requests that add or change a change file, leaving other golden changes to review as today. Recommended. Alternative: require a change file on every pull request that touches a golden, which gates unrelated parser fixes, or no CI step and a local script for reviewers only.
11. **Lossless formats (the project lead, 2026-10-23):** "graphs for FLAC, Monkey's Audio, TTA and WAV/AIFF in wave 2". From the survey: FLAC graphs its stereo coding, block size, subframe types, predictor orders, coefficient precision, residual coding method, partition orders, wasted bits and compression ratio, recording its subframe series while reading because it keeps subframe records only for the first 4,096 frames; Monkey's Audio graphs its pseudo-stereo and silent frames and its compression ratio, and TTA its compression ratio, each after its parser; WAV and AIFF of PCM have nothing to graph, and Microsoft ADPCM's block predictors wait for a WAV parser that walks the blocks.
12. **A "Prediction" heading and FLAC's "Stereo Coding" node (approved by the project lead, 2026-10-23, with "side/right" as the name everywhere, the tally node "Right/Side Frames" and the sentence in `decoders.md` included).** A fifth `GraphConcept`, `Prediction`, for FLAC's subframe types, predictor orders and coefficient precision and WavPack's decorrelation passes, added in a small contract pull request before FLAC's. FLAC's four stereo nodes move under a new "Stereo Coding" node over the complete two-channel frames, renamed "Left/right stereo", "Left/side stereo", "Side/right stereo" and "Mid/side stereo" after the project's and RFC 9639's side/right, which changes every FLAC golden of a two-channel stream. Alternative: no new concept, the series under "Other"; and a new "Stereo Coding" node beside the four, which would show each figure twice under two names.
13. **Stepped stacked area for category series over time, smoothing optional; columns and lines rejected (the project lead, 2026-10-23), with colors following the stack; labels inside the chart rejected after screenshots, the legend names the layers (the project lead, 2026-10-23).** A category series over time is a 100% stacked area with stepped edges, one level per second, the largest category at the bottom; a centered moving average over 5 seconds is a second form, "Smoothed", offered above 30 seconds and never the default. A stepped area keeps a sudden switch sudden and has no gaps between seconds. In this form the bottom layer, the largest, takes the muted color and the rarer layers above it full colors, the legend showing the colors drawn; the strip and the summary tables keep the colors of the categories. The layers carry no names, since the legend lists them in the order of the stack: names were built inside layers and the lead rejected them after screenshots. A partial last interval of fewer than 8 frames keeps its exact shares and is drawn hatched, with a note by the caption (the lead rejected a dimming that did not explain itself). Alternatives: the stacked columns of the first version, which read as many bars on a long file; and one stepped line for each category, which for two categories repeats the same curve mirrored.

## Open risks and unverified claims

- **Unverified:** how Avalonia 12 exposes the system's contrast preference, for the hatch.
- **Unverified:** the palette against Fluent's `SystemRegionBrush` in light and dark; the graph surfaces sampled from screenshots are `#ffffff` and `#000000`, and the implementation records the theme's.
- **Not measured:** the GUI's drawing time at 100,000 frames and the parsers' added time; the estimates above are reasoning, not runs. The AAC in MP4 bound (13 added bytes against 19 of margin) and E-AC-3 with dependent frames are the memory figures to measure first.
- **Scope of MPEG summaries:** a stream split by an embedded tag has its series over the stream and its tally per region, so the JSON counts and the tree's differ there, as the bitrate figures already do. The equality test sums the series of a region's streams.
- **Unverified, lossless formats:** whether flac 1.5.0's `flac -a` prints the subframe lines flac 1.4.3 prints; whether encoders other than SDK 13.44 set Monkey's Audio's special codes at depths other than 16 bits; how the Monkey's Audio parser reads the frame starts of files before version 3.90; which coefficient pairs a Microsoft ADPCM format chunk may list.
- **Not measured:** FLAC's added bytes and peak against `ManyFlacFramesStayCompact`'s bounds (48 to 56 bytes added per stereo frame by the table above).
- **Run-length codes** make goldens short for steady streams; a stream that switches every frame (block types of some encoders) gives about one run per frame and value, which the golden commit's size will show per format.
