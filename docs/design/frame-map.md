# Frame Map: one square per frame

Status: approved by the project lead on 2026-10-27, decision 6 amended twice as described under [Numeric shading](#numeric-shading); the second amendment (declared ranges, 2026-10-27) is built as rollout step 0 (`ValueRange`, `Series.DeclaredRange`, the JSON member, the MPEG opt-in); the map itself is built as rollout step 1 (see [As built](#as-built)). Claims about the code were checked at e8f51bc, and those of the second amendment (`Series`, the JSON writer, the MPEG parser) at c666406; figures that were worked out rather than measured are marked **estimate**, and claims no source read for this design confirms are marked **unverified**. One throwaway prototype outside the repository measured the drawing cost (see [Speed and memory](#speed-and-memory)); nothing of it is committed. The decisions are collected under [Decisions](#decisions).

- An interactive mockup was shown to the lead. It was built from Encspect's JSON report of a damaged LAME `-V2` MP3. It is a private page and is not linked here.

## Scope

What the lead asked for, in their words:

- "A square grid … where each square is a frame or whatever unit the format uses."
- "For bitrate we can show the square in a lighter or darker shade, for stereo mode we can use different colors, etc."
- "The user can toggle what type of information the square grid should show (bitrate, stereo mode, long/short blocks, etc.)"
- "An additional view beside our existing diagrams."
- "You could also click on a square from that grid to view the details of that specific frame."
- "The grid can be scrollable so the square is always the same size and either the screen is half filled for short files or the grid is scrollable for large files."
- Added later: bitrate, stereo coding and block types are only examples. The map works over **any** per-unit series, numeric or category, so every series a parser emits today, and any it adds later, is shown with no GUI change. New series stay out of this design's build and are listed as [follow-ups](#follow-ups-per-unit-series-worth-adding) the lead can pick.

What the plan and the earlier designs settle, and this design keeps:

- Both front ends are thin views over the same result model, "so neither can show something the other cannot" (plan, "CLI and GUI"). The map is another form of series the reports already hold; it adds no figure of its own.
- Front ends read kinds and streams, never node names (`CLAUDE.md`).
- The Graphs tab's list, view switcher, palette, hover, click, keyboard and automation behavior ([gui.md](../gui.md), "Graphs" and "Category series"; [coding-graphs.md](coding-graphs.md)).
- Step 4 of the [measurements rollout](spectral-analysis.md#rollout) adds the entries "Waveform", "Spectrogram" and "Spectrum" under a heading "Audio" in the same tab ([spectral-analysis.md](spectral-analysis.md#gui)).

Not in scope: new series, and any parser or result-model change other than the optional declared range of [Numeric shading](#declared-ranges-the-second-amendment) (one property of `Series`, one JSON field, and the MPEG series that opt in); the CLI gains no option (the map is a picture of series the JSON already writes).

Sources read for the second amendment: `StreamInfo.cs` (`Series`), the series output of `ReportJsonWriter.cs`, `MpegHeader.cs` (the bitrate tables), `MpegAudioParser.Streams.cs` (the "Bitrate" series and how streams are split) and `MpegAudioParser.Coding.cs` ("Channel Bit Share"), `docs/cli.md`, and the fixture folders, counted by script.

Sources read: `CLAUDE.md`, `docs/PLAN.md`, `docs/gui.md`, the two designs above; `Graph.cs`, `GraphsViewModel.cs`, `CategoryGraphs.cs`, `DetailViewModel.cs`, `FrameLocator.cs`, `GraphView.cs`, `GraphView.Categories.cs`, `CategoryColors.cs`, `Palette.cs`, `Styles.axaml` and `DetailWindow.axaml` of the GUI; `StreamInfo.cs`, `CategorySeries.cs` and `Finding.cs` of `Core.Abstractions`; every parser's construction of `Series` and `CategorySeriesBuilder`; the finding collectors of the parsers; `GraphViewTests` and `CategoryViewTests`; the `dataviz` skill's color formula.

## The design in short

- **A form, not a window.** "Frame Map" is one more form in the view switcher of every entry that has a per-unit series, next to "Over Time", "Smoothed", "Frames", "Summary" and "Per Frame", "Over Time", "Distribution". Picking another entry in the list is what toggles what the map shows, and the form is remembered as the others are.
- **Any per-unit series.** It is offered for every `Series` on the `FrameOffset` axis and every `CategorySeries`, by type alone, so a series a parser adds later appears with no GUI change. The map itself needs no parser or model change (checked against the code, see [What it reads](#what-it-reads)); only the optional [declared range](#declared-ranges-the-second-amendment) does, and a series without one is shaded from the file's own range.
- **Squares of one size,** 10 device-independent pixels with a gap of 2, in rows like text, as many columns as fit in steps of 10; only the rows on screen are drawn.
- **Numbers as one color per value** where a series has at most 16 distinct values (a single-hue ramp up to 8, a multi-hue ramp for 9 to 16), else 8 classes of equal width, over the file's own range; a series that **declares a range** (MPEG's bitrate steps, a percentage's 0 to 100) is shaded from the declared steps or ends instead, so colors compare across files; **categories in the strip's colors**, a unit of several values as a split square or a share mix.
- **Findings as rings** around the square their node lies in, with marks beside the scrollbar; clicking a square selects its frame, and selecting a frame in the tree rings its square.

## What it reads

### The series, verified

- The map needs, per unit, a byte offset and a value or a category. Every per-unit series has both: `Series.X` holds "the byte offset of each frame" on the `FrameOffset` axis (`StreamInfo.cs`), and `CategorySeries.X` "the byte offset of each unit", with the codes read through `CategorySeries.Reader(firstUnit)`, which starts at any unit (`CategorySeries.cs`).
- Clicking already works by offset: `Graph.FrameAt` returns an offset, `GraphsViewModel.FrameRequested` hands it to `DetailViewModel.SelectFrameAt`, which finds the frame through the tree's containment (`FrameLocator`). Marking already works too: `DetailViewModel.SelectedNode` calls `Graphs.MarkFrame` with the offset of the selected row's frame, and each `Graph` answers `IndexOfFrame`.
- So the map is a new `Graph` subclass (`FrameMapGraph`, beside `LineGraph` and `CategoryGraph`) and a new `GraphForm` value. The map reads existing members only; the one addition, the optional declared range, is below.

### What a square is

- A square is a point of the series: a frame, packet, block or program frame as the [inventory](#inventory-the-per-unit-series-today) lists. The map never says more than the series does; the hover names it "Frame", as the per-frame line and the strip already do (`LineGraph.Describe`, the strip's hover).
- **Squares stay in place when the entry changes.** A stream's frame offsets are those of its first per-frame series (`GraphsViewModel.For` takes them the same way for the per-second graphs). A series whose offsets are all among them is drawn on those squares, with an empty square where it has no value: MPEG's "Coded Bandwidth" and "Channel Bit Share" and AC-3's and AAC's "Coded Bandwidth" keep x values of their own where frames lack a value, and fall in this case. Toggling between them and the bitrate then keeps every square where it was.
- A series whose units are not among the frames is drawn on squares of its own, and the caption says "One square per point of this series". The case to expect is WMA standard's "Bit Reservoir", one point per superframe at the superframe's offset, beside a bitrate per frame at offsets inside the superframes (`StandardReport.MakeSeries`); the map decides by the offsets, never by the format.

### Findings: one square per kind, not every case

- Findings point at a node, which the host resolves to `Finding.Path` (`Finding.cs`). The square of a finding is the frame on that path: the first node of kind `Frame` along it, whose offset is looked up among the map's units. A finding on the stream, the root, a tag or a gap lies in no frame and is not on the map.
- **Most findings stand for many cases and point at the first.** `Finding.cs` says so ("findings of one kind are reported once per stream, pointing at the first case", with `Count`), and the collectors of MPEG audio, AAC, FLAC, WavPack, Monkey's Audio, Musepack, WMA, WAV, MP4, Matroska, Ogg, Opus, Speex and Vorbis aggregate by code. Fifty CRC mismatches therefore ring one square, the first.
- The map says this in words rather than suggest more: the legend entry reads "Finding (first case)", and the hover of a ringed square "Warning: … (first of 50 cases)". Marking every case needs a per-unit series of the check itself, which is a parser change: the first [follow-up](#follow-ups-per-unit-series-worth-adding).

## Inventory: the per-unit series today

Read from the parsers' code at e8f51bc. Only MPEG audio, AAC and AC-3 build category series; the other formats have a bitrate per unit, and the wave-1 and wave-2 category series that [coding-graphs.md](coding-graphs.md#the-survey) plans for Vorbis, Opus, Speex, Musepack, DTS, WMA, WavPack, FLAC, Monkey's Audio and TTA are not built yet. Containers (MP4, Matroska, Ogg, ASF, DTS-HD) carry the series of the codec parser they hand the payload to, and none where they do not (`matroska.codec-not-analyzed`).

| Format | A square is | Numeric series per unit | Category series |
| --- | --- | --- | --- |
| MPEG audio | a frame | "Bitrate" (kbps; the header's bitrate, the computed one for free format), "Reservoir Fill" (bytes, Layer III), "Coded Bandwidth" (Hz, Layers I and II), "Channel Bit Share" (%, streams with a two-channel frame) | "Channel Modes", "Stereo Coding", "Joint Stereo Modes" (Layer III with joint stereo), "Intensity Stereo Bounds" (Layers I and II, ordered), "Block Usage" (Layer III; 4, 2 or 1 values: granules × channels), "Block Sizes" (a grouping of "Block Usage") |
| AAC | an ADTS frame, an access unit (MP4, Matroska) or an ADIF block | "Bitrate", "Coded Bandwidth" (Hz) | "Window Sequences" (one value per channel stream, variable), "Block Sizes" (its grouping), "Stereo Coding" (one value per channel pair, variable) |
| AC-3 and E-AC-3 | a program frame | "Bitrate", "Coded Bandwidth" (Hz) | "Blocks With Block Switching", "Blocks With Coupling", "Blocks With Rematrixing", "Blocks With Spectral Extension" (E-AC-3): one value per audio block, 6 fixed for AC-3, variable for E-AC-3 |
| DTS | a frame | "Bitrate" | none |
| Vorbis, Opus, Speex | a packet | "Bitrate" | none |
| Musepack | a frame (SV4 to SV7) or an audio packet (SV8) | "Bitrate" | none |
| WMA standard | a frame; a superframe for the reservoir | "Bitrate", "Bit Reservoir" (bytes) | none |
| WMA Pro | a frame | "Bitrate" | none |
| WMA Voice | a superframe | "Bitrate" | none |
| WavPack | a block | "Bitrate" | none |
| FLAC, Monkey's Audio, TTA | a frame | "Bitrate" | none |
| TrueHD and MLP | a run of 16 access units | "Bitrate" | none |
| WAV, Wave64, AIFF | (no units) | none | none |

The per-second series ("… Over Time") have no units and no map.

## Where it sits

- **A form of each entry.** `GraphForm.Map`, titled "Frame Map", with the tooltip "Every frame as a square, in rows like text". It is offered on an entry that has a per-frame series: the numeric quantities' `Per Frame` series and every category series. The Coding Summary, quantities with only a per-second series, and step 4's "Audio" entries have no units and no map.
- **Remembered as the other forms are.** Choosing it sets the preferred form; an entry without a map keeps its first form, and the next entry with one is drawn as a map again (`GraphsViewModel.SelectForm` and `Choose`, `GraphChoice.SelectForm`, as built). So "toggling what the grid shows" is picking an entry in the list, with the list's headings ("Stereo Coding", "Block Switching", "Bit Distribution"…) as the menu.
- **Beside step 4's panels.** Step 4 adds entries under "Audio"; the map adds a form to other entries. The two do not touch the same entries, only the same files (`GraphsViewModel`, `DetailWindow.axaml`, `GraphView`), so the map is built after step 4 merges.

## Layout

### Square size and zoom

Options:

- **A fixed size** (the lead's "the square is always the same size"): one number for every file.
- **A fixed size with zoom** (Ctrl and the wheel, or plus and minus, over a few steps such as 6, 10 and 16).
- **A size that fits the file to the pane**, which the lead ruled out.

Recommended: **10 by 10 device-independent pixels with a 2-pixel gap** (a pitch of 12), and no zoom in this build.

- 10 pixels holds a 2 × 2 split square of 4-pixel cells with a 1-pixel inner gap, and still shows about 4,000 squares in a pane of 1,000 by 600. A 2-pixel gap in the surface color is the strip's separator between stacked segments (gui.md, "Palette"), and keeps neighbors of close shades apart.
- Avalonia lays out in device-independent pixels, so a square is 20 physical pixels at 200%. At 125% or 150% the pitch is rounded to whole physical pixels so that every square has the same size and sharp edges (**unverified** how Avalonia 12 snaps rectangles drawn in `Render`; the implementation rounds by `RenderScaling` itself).
- Zoom is a follow-up the lead can ask for. The overview of a long file is what "Per Frame" and "Over Time" already give.

### Reading order and columns

Row-major, wrapping like text, frame 1 at the top left. Options for the columns:

- **As many as fit:** fills the width, but the row starts are odd numbers and change with every resize.
- **The largest multiple of 10 that fits** (at least 10): rows start at frames 1, 51, 101 at 50 columns, a resize reflows only in steps of 10, and at most 9 columns of space stay empty on the right.
- **A fixed count** (50 or 100): rows align across files, but a narrow pane scrolls sideways and a wide one is half empty.
- **One row per second** from the stream's time index: rows align with time, but rows are ragged (38 or 39 MPEG frames), only category series carry a `TimeIndex`, and TrueHD's 300 runs a second at 192 kHz do not fit a row.

Recommended: **the largest multiple of 10 that fits**, with the frame number of the row's first square in the left margin every fifth row (the label is the axis size, 12, which needs more than a pitch). The hover gives the exact frame. Times stay out of the margin: a numeric-only stream has no time per frame (`LineGraph.FrameAt` estimates one from the duration, which is wrong for packets of different lengths).

### Scrolling and drawing

- The map scrolls vertically, with a scrollbar and the mouse wheel; a short file fills the top of the pane and leaves the rest empty, as the lead asked.
- Only the rows on screen are drawn, and the map keeps no array per unit: a square's class or category is read from the series when it is drawn. The numbers that justify this are under [Speed and memory](#speed-and-memory).
- **Marks beside the scrollbar:** a lane 8 pixels wide between the grid and the scrollbar, as high as the pane, the whole stream in proportion. A finding is a 3-pixel mark in its severity's color (`ErrorBrush`, `WarningBrush`, `InfoBrush`, the colors of the findings list's dots), and the selected frame a mark in the text color. Clicking the lane scrolls there. Drawing marks inside Avalonia's own `ScrollBar` would need its template changed, which is **unverified**; a lane of the map's own needs nothing of the theme.

## Units with several values

A unit can hold several values: Layer III's granules × channels (4, 2 or 1), AC-3's 6 blocks, a variable number of AAC channel streams and pairs or E-AC-3 blocks. Options:

- **Split squares:** one cell per value inside the square.
- **One value at a time:** a choice of lane, as the strip's rows, with the other values hidden.
- **A share mix:** the square stacked by the shares of its values, as the strip's single row draws a variable layout (gui.md, "Frames").

Recommended: **split squares for a fixed layout of 2 to 4 values, a share mix otherwise.**

- Split cells follow the grid's own reading order, in the parser's order of the values: 2 values as left and right halves; 4 as 2 × 2, so Layer III's granule 1 is the top row (channel 1, channel 2) and granule 2 the bottom row. The map reads only `ValuesPerUnit` and the order, never `ValueNames`, which only the hover shows.
- A fixed layout of more than 4 values (AC-3's 6 blocks: 6 cells of under 3 pixels) and every variable layout draw a share mix: horizontal bands from the bottom in the order of the categories, each category that occurs keeping at least 1 pixel, as in the strip. The hover lists every value.
- One value at a time would show a quarter of a Layer III file's block types at once, and the strip already offers it per row.

## Numeric shading

Options for the mapping:

- **Linear over the stream's range,** as a continuous shade: exact, but one outlier squeezes the rest into a few shades, and close shades cannot be told apart.
- **Quantiles:** every shade used equally, but the steps are odd numbers and the shade no longer reads as an amount.
- **The format's own steps** (MPEG's bitrate indexes): not generic, which the lead's "any series" rules out.
- **A few classes of equal width,** or one class per value where there are few.
- **One color per value up to 16, else classes of equal width:** the chosen option, below.

Chosen: **one color per value up to 16 distinct values, else 8 classes of equal width.**

- **Reason (the lead's):** a frame has one bitrate, so grouping bitrates in ranges (32 to 64 as one class) hides steps the file really has. Each step (32, 48, 64, ...) gets its own color.
- **Up to 16 distinct values:** one color per value. An MPEG VBR stream's header bitrates (`MpegAudioParser.Streams.cs`, 14 indexes) get one color per step, and a CBR stream gets one even color.
  - Up to 8 distinct values: one step each on `CategoryColors.Ramp` of the first hue, the ramp ordered category series already use (gui.md, "Palette"). Its 8 steps are 1.71 to 10.1 : 1 against the white surface and 1.25 to 1.32 between neighbors (computed for this design from the code's formula; **estimate** until a test reads them).
  - 9 to 16 distinct values: a multi-hue sequential ramp, so that 14 to 16 neighboring steps stay distinguishable. Neighboring steps of one hue would differ too little in contrast.
  - The multi-hue ramp's exact colors are chosen in the build. They are checked with the `dataviz` skill's validator in light and dark mode, and higher values keep more contrast against the surface (decision 7).
  - The mockup used ColorBrewer's YlGnBu as a stand-in. That is an observation about the mockup, not a choice.
- **More than 16 distinct values** (computed bitrates of AAC, FLAC or DTS from each unit's size, the reservoir fill, the channel bit share): 8 classes of equal width from the stream's smallest value to its largest, on the 8-step single-hue ramp, unless the series declares a range (below).
- **Direction:** a higher value has more contrast against the surface: darker in light mode, lighter in dark mode, where the ramp is reversed (the `dataviz` skill's sequential rule, "flips anchor in dark"). The ordered category series do not flip today, so decision 7 settles this for the map only.
- **Key, not a list** (the lead, 2026-10-27: a shaded series needs no legend list): one thin line under the map, the smallest value, a row of swatches from low to high, the largest value with its unit ("32 kbps ▮▮▮▮▮▮▮▮▮▮▮▮▮▮ 320 kbps"); for classes of equal width the ends are the stream's range. It stays because the direction flips between light and dark mode (decision 7), the range differs per series, and automation text needs the colors in words.
  - The hover gives each square's exact value, and a swatch's tooltip its value or range and its count ("128 kbps · 1,263 frames"); the counts per value are also in the Distribution form.
  - Clicking a swatch selects the next frame of that value or class, wrapping around.
  - "No value" follows the key with its count where the series has such frames. A single value is one line, "Every frame 128 kbps", which is what a CBR stream's map shows: one even color.
  - Category series keep their named legend (see [Categories](#categories)), since a color does not name a category.
- **No value:** a frame the series has no value for, or a value that is not finite, is an empty square with a hairline outline in the grid color (`Palette.Grid`), so that its place is seen; the hover says "no value".
- **Diverging quantities** (the channel bit share around 50%) get the same ramp in this build; a scale with a midpoint would need the series to say so, a model change left as a follow-up. The [declared range](#declared-ranges-the-second-amendment) below is its first half: it fixes the ends at 0 and 100, and the midpoint would be one more optional member of the same property.

### Declared ranges: the second amendment

Decided by the project lead on 2026-10-27, after the first build was designed. The reasons, from the discussion:

- The default scale runs from the file's lowest to its highest value, and that stays the default.
- Starting at 0 would waste shades wherever a series has a high floor: FLAC's bitrate at about 600 to 1,400 kbps, the global gain, the frame size.
- One absolute scale across formats is impossible.
- A series whose format defines a natural range can declare it, so that a color means the same in every file of that kind.

#### The result model: one optional property

- **`Series.DeclaredRange`**, of a new type `ValueRange` in `Core.Abstractions` (`StreamInfo.cs`, beside `Series`), `init`-only and null by default, so every series that sets nothing behaves as today. Two forms, built by factory methods so that a range cannot hold both:
  - `ValueRange.OfSteps(values)`: the steps the format allows, such as the MPEG bitrate table, in the series' unit. Strictly ascending, finite, at least 2.
  - `ValueRange.Between(minimum, maximum)`: a fixed minimum and maximum, in the series' unit, such as 0 and 100 for a percentage. `minimum < maximum`, both finite.
- The constructor throws `ArgumentException` for a range that breaks these rules, as `Series` does for mismatched lengths; a parser builds its table from constants, so this is a programming error, not an input error.
- The name follows the lead's "declared ranges". `Range` is avoided because it is `System.Range`; the property says what the parser does (declares), and a front end reads it as a fact the parser states, not a judgment about the audio.
- **The JSON** gets one optional member on a numeric series, written after `quantity` and only when set:
  - `"declaredRange": { "steps": [32, 40, 48, ...] }` or `"declaredRange": { "minimum": 0, "maximum": 100 }`. The numbers are in the series' `unit`, written as the `x` and `values` are.
  - A field is added and none changes meaning, so `schemaVersion` stays 1 (`docs/cli.md`, "Schema version").
  - With `--explain` there is no separate text: the series' own `description` says what the range is. The field is documented in the JSON section of `docs/cli.md`, and `Series.DeclaredRange` has a comment in the style of its neighbors.
- **The text tree does not print it:** it prints a series' values only, and a declared range is a hint for a front end's shading, not a figure of the file.

#### What the map does with it

- **Steps** (`OfSteps`): one color per declared step, whether or not the file uses it.
  - The color is fixed by the step's position in the declared list, on the ramps of this section: a single-hue ramp for up to 8 steps, the multi-hue ramp for 9 to 16. The one-color-per-value rule above applies to the declared steps, not to the file's distinct values. More than 16 steps would use 8 classes of equal width from the first step to the last; no series declares that many.
  - The same step has the same color in every file of that kind: 128 kbps is the same shade in a CBR file and in a VBR file whose lowest step is 32.
  - A file's map shows only the steps it uses; the others are absent from the squares.
  - **The key line shows every declared step and dims the swatches of unused steps** (reduced opacity, tooltip "0 frames"), with the declared ends as its smallest and largest value. The alternative, omitting them, would make the key's length and ends depend on the file, which is what the declaration is there to avoid, and a dimmed gap also shows which steps an encoder never used. The key stays thin: 14 swatches fit the line, as the 14 steps of today's key do.
  - The colors depend on the position, so a table of another version or layer has other values at the same position (MPEG-2 Layer III's step 3 is 24 kbps, MPEG-1's is 48); comparability holds between files of the same kind, and the key names the ends.
- **A fixed range** (`Between`): the shades are 8 classes of equal width over the declared minimum to the declared maximum, not over the file's values, on the 8-step single-hue ramp.
  - This holds even where the file has at most 16 distinct values: one color per value would give a value a color that depends on the file, which defeats the declaration.
  - The key's ends read the declared minimum and maximum with the unit ("0 % ▮▮▮▮▮▮▮▮ 100 %").
- **Values outside the range clamp** to the end class (below the minimum or the first step to the first; above the maximum or the last step to the last) and are marked in the hover: "Frame 1,234 · 0x1A2B · 512 kbps (above the declared range of 32 to 320 kbps)". A value inside the span of steps but not on one goes to the nearest step and is marked "not a declared step". The key line gives the count of such squares after "No value", where there are any.
  - A file can break its format, and saying so is a finding's job, not the map's; the map neither rings the square nor words a verdict.
- **A series without a declared range** is shaded as before: one color per value up to 16 distinct values, else 8 classes from the file's lowest to its highest value, and a key from the smallest to the largest value.

#### Who declares what in the first build

Declared:

- **MPEG "Bitrate" (per frame):** the bitrate table of the stream's version and layer, in kbps, without the free (0) and reserved (15) indexes: 14 steps for MPEG-1 Layer I, II and III, and for MPEG-2 and 2.5 Layer I and II/III. The tables are `Mpeg1Bitrates` and `Mpeg2Bitrates` in `MpegHeader.cs`, indexed by layer; the parser takes the step list from the same arrays, so there is one table.
  - A **free-format** stream declares none: its bitrates are computed from the frame lengths (`MpegAudioParser.Streams.cs`) and fall between steps.
  - **A change of version or layer mid-stream** does not arise within a stream: the parser starts another stream at each change of version, layer, sample rate or free format (`MpegAudioParser.Streams.cs`, the `MaxStreams` finding), so each stream has one table. A stream that could mix would declare none.
- **MPEG "Channel Bit Share" (per frame):** 0 to 100 %. Its per-second form, "Channel Bit Share Over Time", declares the same range, since it is the same quantity; it has no map, but the line views may use it later.
- **Other percentage series:** none. A search of the series built in `src` (unit `"%"`) finds only these two; the other percentages in reports are figures of nodes (`Share`, `Overall`) and of the measurements, not series.

Not declared; each keeps the file's own range:

- **MPEG "Bitrate Over Time"** and the other per-second series: an average over a second falls between steps, and they have no map.
- **Reservoir Fill and Bit Reservoir** (bytes): the bound depends on the main data begin field and the stream's bitrate, so no table or percentage is natural; the file's range shows what the stream used.
- **Coded bandwidth** (Hz): the ceiling is the sample rate's half, but the floor is what an encoder chose, and the shades should resolve that choice, not the whole band.
- **Computed bitrates** of AAC, FLAC, Opus, Vorbis, Speex, DTS, WavPack, Monkey's Audio, Musepack, TTA, TrueHD and WMA: no table; each is a size over a duration, with floors that differ by an order of magnitude between files (FLAC about 600 to 1,400 kbps).
- **Frame size and global gain** (follow-ups, not built): a high floor, and the range is a property of the encoder's settings, not of the format.

The channel bit share's diverging scale centered on 50 % stays a [follow-up](#follow-ups-per-unit-series-worth-adding), as before; the declared range is its first half.

#### The costs

- **Goldens:** every JSON report of an MPEG stream with a bitrate table gains the field: on 1 series for mono, 3 for two channels, and 0 for free-format mono. By a script over `tests/fixtures`: 478 JSON files hold a stream with an MPEG audio codec, of which 299 are `.report.json` goldens and 179 are `.expected.json`; the 300 reports with an MPEG "Bitrate" series are the ones that could change, and 294 do. The other 6 are free-format streams with no channel bit share (`stats/free-format-statistics`, `layer3/free-format-no-main-data`, `mpeg/{mpeg1,mpeg2,mpeg25}-layer3-free-format` and `mpeg/free-format-length-change`). The `.expected.json` files hold no series and do not change. These include a few non-MPEG containers that carry MPEG (WAV 12, MP4 2, Ogg 2, AAC and ADTS 2 each). **294 goldens change**, each by added lines only, so the diff is checked by script (a golden's lines minus the added `declaredRange` lines equal the old golden). Inline JSON in `CategorySeriesWriterTests` and `MemoryTests` is checked the same way.
- **Size:** an MPEG report grows by about 100 bytes per stream (14 numbers and the member names), not per frame.
- **Docs:** `docs/cli.md` (the JSON section: the field, its two forms, and that `schemaVersion` stays 1), `docs/formats/mpeg-audio.md` (which series declare what), `docs/gui.md` ("Frame Map": the key and the clamp).
- **Compatibility:** a reader that ignores unknown members sees no change; one that rejects them already fails on every earlier field addition (cli.md, "Schema version").

## Categories

- **The strip's colors, not the stack's:** each category in its own color from `CategoryColors.For`, the six fixed hues with the muted first step, the ramp for an ordered series, flat gray "Other" past the sixth, the hatched gray of a reserved category, and every second category hatched under the high contrast preference. A category thus has the same color in the strip, the summary tables and the map.
- **No new colors:** the palette, its surfaces and the validator's figures in gui.md stay as recorded. The rings and marks below use the text color, the surface, the accent and the findings list's severity brushes, none of which is a series color.
- **Legend and text:** the legend lists the categories that occur in the order of the categories, with their counts, as the strip's does; names and shares are always in text, which is what the accepted tritan figure and contrast warnings rely on (gui.md, "The validator").
- A value without a category stays empty, as in the strip ("no category" in the hover); a unit past the caps, whose values are counted without a code, says so in the hover.

## Findings overlay

Options:

- **A ring in the severity's color:** shows the severity at a glance, but the error red and warning brown sit close to the palette's orange and yellow, so a ring on such a fill or beside it can vanish.
- **A ring in the text color with a surface line inside it:** reads against any fill and any surface; the severity is in the hover and the marks.
- **A tint of the whole square:** hides the value the square shows.

Recommended: **the ring in the text color**, 1.5 pixels inside the square's edge with a 1-pixel line of the surface color inside it (the `dataviz` skill's surface ring on overlapping marks), so that one of the two contrasts with whatever fill is under it.

- **Severity** is in the colored marks beside the scrollbar and in the hover, "Error: … (first of 50 cases)".
- **No toggle of its own:** the rings follow the severity filters of the Findings tab (`ShowErrors`, `ShowWarnings`, `ShowInfos`), so one setting hides a severity in both. The rings never cover a fill, so they need no off switch; the alternative is a "Findings" checkbox in the switcher's row.
- **The selected frame** has a 2-pixel ring of the accent color outside the square, in the gap, as the line graph marks a selected frame in the accent. A square that is both shows both rings.

## Interaction

The strip's patterns, with the grid's two dimensions:

- **Hovering** a square says its frame, offset and value, as the strip and the per-frame line do: "Frame 1,234 · 0x1A2B · 192 kbps", "Frame 1,234 · 0x1A2B · granule 1, channel 1: Long · granule 1, channel 2: Long · granule 2, channel 1: Short · granule 2, channel 2: Short", followed by the finding where it has one.
- **Clicking** a square selects its frame in the tree and hex view (`FrameAt`). A legend entry selects the next frame after the selected one in that class or category, wrapping around, as the strip's legend does.
- **Selecting** a frame in the tree rings its square and scrolls it into view when it is off screen; the map does not scroll while the selection stays on screen.
- **Keyboard:** Tab reaches the map as it reaches the graph. Left and Right move the cursor by a frame, Up and Down by a row, Page Up and Page Down by a screen, Home and End to the first and last frame; Enter selects what the cursor shows. The cursor is a dashed ring in the text color and is scrolled into view. In the strip Up and Down change the row; in the map they move by a row of the grid.
- **Automation:** the map's name is the entry's title, as a graph's is. Its help text says what the picture shows in words: "Frame map of 9,188 frames, 50 to a row", the key or legend in words with the counts ("32 to 320 kbps, darker is higher, 14 values"), the number of frames with findings, and what the cursor is on.
- **Capitalization:** the one new label is the switcher's "Frame Map" (Title Case, no function word), which `CapitalizationTests` reads with the other buttons. Everything else is sentence case: the tooltip, the caption, the legend ("No value", "Finding (first case)"), the hover. Series names are text from the analysis and keep their spelling.

## Streams without per-unit series

- WAV, Wave64 and AIFF have no units, and a coding inside a container that is not handed to its codec parser has no series; the Graphs tab shows them as today.
- Entries with a per-second series only and the Coding Summary have no "Frame Map" in their switcher. A per-second map would be a calendar of seconds, which "Over Time" already draws.
- A stream with a single unit draws one square.

## Speed and memory

### Bounds

- The longest streams are 1 to 2 million units: a 10-hour MP3 (38.28 frames a second at 44.1 kHz) has about 1.38 million frames, a 10-hour Opus stream of 20 ms packets 1.8 million, and the AAC and TrueHD records stop at 2,097,152 (`aac.frames-limit`, `truehd.units-limit`). At 80 columns 2 million squares are 25,000 rows, 300,000 pixels of content (**estimate**, from the pitch).
- **Drawing:** one rectangle per square on screen, four for a split square, and at most one band per category for a share mix; nothing per unit off screen.
- **Memory:** no array per unit. The classes need one pass over the values when a series is first drawn (smallest, largest, up to 17 distinct values); the findings a list of their squares; the mark lane one entry per pixel of its height. The series themselves are already held by the report.

### Measured

A throwaway Avalonia 12.1.4 control in the scratchpad, on the headless platform with Skia as `GraphViewTests` uses it, a window of 1,000 by 600, 2,000,000 values in 8 classes, scrolled and resized every frame. One run on the shared 4-core machine (load average 1.0 to 1.6), so the figures are an order of magnitude, not a bound:

| Case | Rectangles drawn | `Render` | A frame, with the headless capture | Managed heap with the window open |
| --- | --- | --- | --- | --- |
| Visible rows only, one cell a square | 4,080 | 0.3 to 1.1 ms | 24 to 25 ms | 17 MiB, of which 15 the values |
| Visible rows only, 2 × 2 cells | 16,320 | 0.9 to 1.0 ms | 33 to 41 ms | 24 MiB |
| Every square drawn and clipped | 2,000,000 | 147 to 152 ms | 1.24 to 1.30 s | 1,050 MiB |

- Drawing the visible rows is far inside the 500 ms a frame that the line and strip tests hold (`GraphViewTests`, `CategoryViewTests`).
- Drawing everything and letting the clip discard it is not: the recorded drawing operations alone held about 1 GB. Drawing only the visible rows is therefore required, not an option.

## Tests

- **`FrameMapGraphTests`** (view model): the classes (a series of 8, 9, 16 and 17 distinct values: one color per value at 8, 9 and 16, equal-width classes at 17; one value; values that are not finite), a series with x values of its own on the stream's squares and one with units of its own, the square of a finding found through its path (a finding on a gap, the stream or the root has none), the columns rule, the unit at a point and the point of a unit, the next frame of a legend entry.
- **`FrameMapViewTests`** (headless): a click selects the frame, a frame selected in the tree is ringed and scrolled into view, the keys, the hover text, a click on the mark lane, the automation help text, the form remembered across entries, and no "Frame Map" on an entry without units.
- **Speed and memory:** 2,000,000 numeric units, and 200,000 units of 4 values, drawn while scrolling to far rows and resizing, under the 500 ms a frame of the existing tests; the rectangles drawn are counted and must stay below the squares on screen times the cells of a square, which fails if the drawing stops skipping rows; the managed heap grows by less than a bound set from the first run with the map open, which fails if a per-unit array appears.
- **Colors:** a pixel test samples squares of a scripted series and requires the palette's colors (`CategoryColors.For`) and the steps of both ramps (single-hue and multi-hue), in light and dark mode.
- **Multi-hue ramp:** a validator check with the `dataviz` skill's validator, run on its colors in light and dark mode, requiring that higher values keep more contrast against the surface.
- **`ScreenshotTests`,** light and dark, saved with `ENCSPECT_SCREENSHOTS` for review: the block usage of an MPEG fixture with short blocks (split squares), the bitrate of a VBR fixture, an AC-3 flag series (share mix) and a damaged fixture whose findings point at frames, each found by script from the goldens rather than assumed.
- **`CapitalizationTests`:** unchanged; "Frame Map" is checked with the other buttons.
- **Declared ranges (the second amendment):**
  - **Model test** (`SeriesRangeTests`): `OfSteps` and `Between` accept valid input and keep the members; they throw for unsorted, repeated, non-finite or fewer than 2 steps and for `minimum >= maximum`; a series without a range has null.
  - **Writer test** (in `SeriesRangeTests`): `declaredRange` is written with `steps` or with `minimum` and `maximum` only when set, after `quantity`, and is absent from every other series; the text tree is unchanged.
  - **MPEG tests** (`MpegCodingSeriesTests`): the bitrate declares the 14 steps of MPEG-1 Layer III and of MPEG-2 Layer III, the table of Layers I and II, and none for a free-format stream; the channel bit share declares 0 to 100; and the declared steps equal the nonzero, unreserved entries of the header tables.
  - **Map test** (`FrameMapGraphTests`): the same bitrate has the same color in two files with different ranges (a CBR file at 128 kbps and a VBR file from 32 to 320 kbps), a step a file does not use is absent from its squares and dimmed in the key, a fixed range spans the declared ends where the file's values are few, and the key's ends are the declared ones.
  - **Clamp test** (`FrameMapGraphTests`): a value below, above and between the steps goes to the end class or the nearest step, and the hover says so; a file that breaks its format draws without a ring.
- **Goldens:** about 300 MPEG `.report.json` goldens change by the added field only, checked by script; no other golden changes. The map itself writes nothing.

## Rollout

0. **Recommended: a small preceding pull request for the declared range,** which can merge before step 4 because nothing in it touches the GUI: `ValueRange` and `Series.DeclaredRange`, the JSON writer, the MPEG opt-in, the model, writer and MPEG tests, `docs/cli.md` and `docs/formats/mpeg-audio.md`, and the roughly 300 re-blessed goldens.
   - Reason: the Frame Map pull request should be reviewed for its drawing, not buried under 300 golden files; the preceding one is reviewed for the model and the writer, and its golden diff is checked by script as added lines only.
   - It is also the part with a public contract (the JSON), so it gets its own review and its own green pipeline, and the map then builds on a merged model.
   - If the lead prefers one pull request, the work is the same and goes in the Frame Map pull request after step 4.
1. **One pull request after step 4 of the measurements merges:** `GraphForm.Map`, `FrameMapGraph`, the map's drawing in `GraphView` (a partial file of its own, as the category views are), the mark lane, the tests above (the map and clamp tests of the declared range, if step 0 was separate); `docs/gui.md` ("Graphs": the form, and a "Frame Map" section); `docs/PLAN.md` (the GUI list's graphs). An independent review on Opus with high effort follows, as after each milestone.
2. **Follow-ups the lead picks,** each a pull request of its format with its fixtures, goldens and cross-checks, as coding-graphs.md's waves are: the map shows each new series on the day it merges.

## Follow-ups: per-unit series worth adding

None of these is part of this design's build. "Cheap" means the parser keeps the field in its records today, checked in the code where marked; "new" means it reads or keeps something it does not now. Each series is anchored to a node and held equal to a tally as coding-graphs.md requires, so each also reaches the JSON and the text tree.

Already approved in [coding-graphs.md](coding-graphs.md) (waves 1 and 2, not built), which the map shows as they come:

- Vorbis block sizes and modes; Opus modes, bandwidths, frame sizes and channel counts; Speex submodes; Musepack coded bandwidth and mid/side stereo (wave 1).
- FLAC stereo coding (left/side, side/right, mid/side, left/right), block size, subframe types (constant, verbatim, fixed, LPC), fixed and linear predictor orders, coefficient precision, residual coding method, partition orders, wasted bits and compression ratio; WavPack mid/side stereo and decorrelation passes; Monkey's Audio stereo coding, silent frames and compression ratio; TTA compression ratio; DTS coded bandwidth, sum/difference and joint intensity coding; WMA standard block sizes, mid/side stereo, noise substitution and coded bandwidth; WMA Voice frame types; AAC window shapes, TNS, noise substitution, pulse data, SBR and parametric stereo, bands coded mid/side; Layer III scalefactor scaling and global gain (wave 2).

New candidates, for the lead to pick:

| Series | Formats | Source | Cost | Why on a map |
| --- | --- | --- | --- | --- |
| **Frame checks** (category: CRC matches, CRC differs, no CRC) | MPEG audio | `Entry.Crc` and `Entry.ComputedCrc` in the frame record (code) | cheap | Marks every damaged frame, where findings ring only the first case; shows whether the encoder wrote CRCs at all. |
| | AC-3 and E-AC-3 | the CRC verdicts in the 16-byte frame record (plan, "Phase 2: AC-3 and E-AC-3") | cheap | The same. |
| | FLAC | `Crc16Fails` and `Crc8Fails` of the frame entry (`Model.cs`) | cheap | The same, for the CRC-8 and CRC-16 apart. |
| | TTA | each frame record's stored and computed CRC-32 (`Model.cs`, `IsCrcValid`) | cheap | The same. |
| | DTS, TrueHD and MLP, AAC (ADTS) | their CRC checks (`Crc16.cs`, `Checks.cs`, the ADTS error check) | **unverified** which verdicts are kept per unit | The same. |
| **Frame size** (bytes) | every format with a bitrate per unit | the unit's length in its record | cheap | Differs from the bitrate only where units differ in duration (Vorbis and Opus packets, FLAC variable block sizes); elsewhere it repeats the bitrate, so it is worth adding only there. |
| **Padding** (category) | MPEG audio | the padding bit of the header kept in `Entry.Header` | cheap | Shows the padding pattern of a CBR stream, which encoders set differently (**unverified** which, so the series is shown, not interpreted). |
| **Follows a gap** (category: in sync, after a gap) | MPEG audio, AAC (ADTS), AC-3, DTS | the gap entries between frames (`Entry.Gap` in MPEG) | cheap in MPEG; **unverified** in the others | Shows every resync, where a finding rings the first. |
| **Main data begin** (bytes) | MPEG Layer III | `Entry.MainDataBegin` (code) | cheap | How far back each frame reaches into the bit reservoir, next to "Reservoir Fill". |
| **part2_3_length** (bits per granule and channel) | MPEG Layer III | the side information in the shared store, at known bit positions | cheap to derive, as "Block Usage" is | Where the encoder spends bits, per granule; a numeric series of 4 values per unit, which needs numeric series with several values per unit: a model change. |
| **SCFSI** (category per channel) | MPEG-1 Layer III | the side information, as above | cheap to derive | Whether an encoder reuses scalefactors; some encoders never set it (**unverified**, needs fixtures). |
| **Packets per page, packets that span pages** | Ogg (Vorbis, Opus, Speex, FLAC) | the page and packet layout the Ogg parser reads; a packet's `Ranges` | **new** for the Ogg parser, which reports no series; whose series it would be (the container's or the codec's) is a design question of its own | How a muxer pages the stream; the stream's start and end pages. |
| **Per-unit levels** (peak, clipping, loudness) | formats with a decoder | the measurements | **new, and against a rule:** `CLAUDE.md` bounds a measurer's memory per stream whatever the stream's length, and the measurements keep grids of at most 1,024 columns | Only a change of that rule makes them per unit; the waveform grid of step 4 already shows them over time. |

## Alternatives considered and rejected

- **A separate entry "Frame Map" under "Summary" with its own picker of the series:** the picker would repeat the list beside it, and the list's headings already sort the series by concept.
- **A tab or window of its own:** the lead asked for a view beside the existing diagrams, and the Graphs tab's stream choice, list and selection plumbing would be built twice.
- **The strip at a fixed cell width, scrolled sideways:** one long row is hard to scan, and the strip exists for category series already; the map adds numbers and the wrap.
- **One row per second of audio:** rows align with time, but only category series carry a time index, the rows are ragged, and TrueHD's runs at 192 kHz do not fit a row (see [Reading order and columns](#reading-order-and-columns)).
- **Drawing every square and letting the clip discard them:** about 1.3 s a frame and 1 GB at 2 million squares, measured.
- **A ring in the severity's color on the square:** too close to the palette's orange and yellow; the severity is in the marks and the hover.

## Decisions

The project lead approved all 12 on 2026-10-27, as recommended, with one change to decision 6; later the same day the lead amended decision 6 a second time (declared ranges).

1. **Placement:** (approved) a form "Frame Map" in each entry's view switcher, remembered as the other forms are; toggling what it shows is picking the entry. Alternative: an entry of its own with a picker; a tab of its own.
2. **Any per-unit series, no model change for the map:** (approved; the optional declared range of decision 6 is the one addition) offered for every `FrameOffset` series and every category series by type; squares on the stream's frame offsets so they stay in place between entries, a series of other units on squares of its own. Alternative: a list of supported series, which every new series would have to join.
3. **Square size:** (approved) 10 by 10 device-independent pixels with a 2-pixel gap, rounded to whole physical pixels; no zoom in this build. Alternative: 8 with 1 (more squares, no 2 × 2 split), 12 with 2; zoom in three steps.
4. **Columns:** (approved) row-major, the largest multiple of 10 that fits, frame numbers every fifth row in the margin, no times. Alternative: as many as fit; a fixed 50 or 100; one row per second.
5. **Several values per unit:** (approved) split squares in reading order for fixed layouts of 2 to 4 values; a share mix for more and for variable layouts; the hover lists every value. Alternative: one value at a time; a share mix everywhere.
6. **Numeric shading** (approved, amended twice): one color per value where a series has at most 16 distinct values (single-hue ramp up to 8, multi-hue sequential ramp for 9 to 16, colors chosen and validated in the build); otherwise 8 classes of equal width over the stream's range; a thin key line from the smallest to the largest value instead of a legend list (the lead, 2026-10-27), each swatch's count in its tooltip, clicking a swatch steps through its frames; empty outlined squares for no value. Amended on the lead's question why bitrate steps should be grouped when a frame has one bitrate. **Second amendment (2026-10-27, declared ranges):** a series may declare its range, `Series.DeclaredRange`, either the steps the format allows or a fixed minimum and maximum, written in the JSON only when set. Steps get one color each, fixed across files, with unused steps dimmed in the key; a fixed range spans the declared ends; values outside clamp to the end class and are marked in the hover. In the first build the MPEG bitrate declares its table (14 steps) and the MPEG channel bit share declares 0 to 100 %; everything else keeps the file's range. Costs: about 300 MPEG goldens change, and the JSON gains one optional field. Alternative: the file's range everywhere; linear and continuous; quantiles.
7. **Ramp direction in dark mode:** (approved) flip it so a higher value has more contrast against the surface, in the map only, as the `dataviz` rule says. Alternative: flip the ordered category series too, for one rule; flip neither.
8. **Categories:** (approved) the strip's colors per category, hatching and "Other" unchanged, no new colors and no new validator run. Alternative: the stack's colors of the over-time form.
9. **Findings:** (approved) a ring in the text color with a surface line inside it, on the square of the frame each finding points at, labeled as the first case; no toggle, the Findings tab's severity filters apply. Alternative: rings in the severity's color; a "Findings" checkbox of its own.
10. **Scroll marks:** (approved) a lane of the map's own beside the scrollbar, findings in the severity brushes and the selected frame in the text color, clickable. Alternative: marks inside `ScrollBar`'s template (**unverified** in Avalonia 12).
11. **Selection:** (approved) the selected frame has an accent ring outside its square and is scrolled into view only when off screen. Alternative: the text color, as the strip's marker line uses.
12. **Rollout:** (approved; the declared range recommended as a small preceding pull request, step 0 of the [rollout](#rollout), for the lead to confirm) one pull request after measurements step 4, with a review; follow-up series as pull requests of their formats, chosen from the [list](#follow-ups-per-unit-series-worth-adding), the per-unit frame checks first since they make the findings overlay complete. Alternative: before step 4, rebasing step 4 onto it.

## Unverified claims and estimates

- **Unverified:** how Avalonia 12 snaps rectangles to physical pixels at fractional scaling; whether its `ScrollBar` template can carry marks.
- **Unverified:** which CRC verdicts DTS, TrueHD and MLP and AAC keep per unit, and whether AAC, AC-3 and DTS keep their gaps per unit, for the follow-ups.
- **Unverified:** the multi-hue ramp's validator figures (contrast against the surface and between neighbors, light and dark) are not measured yet.
- **Estimates:** the golden count of the second amendment (about 300, counted by script over the fixture folders, to be confirmed when the goldens are re-blessed); the ramp's contrast figures (from the code's formula, not read from a running theme); the 2-million-unit content height; the class pass's cost.
- **Measured once,** on a shared machine: the drawing figures above. The tests set the bounds that hold.

## As built

Rollout step 1, built on `feature/frame-map` from main at 854146d. Everything above is built as written; this section records the choices the design left to the build and the places where the build differs.

- **The multi-hue ramp** (`MapColors.MultiHue`): 16 steps interpolated in sRGB between six anchors, light to dark, `#c8b44a #7bb26a #3ea08f #2a83ae #3b5db2 #2a3a95`: `#c8b44a #aeb355 #95b35f #7bb26a #67ac76 #52a683 #3ea08f #379699 #318da4 #2a83ae #3076af #356ab1 #3b5db2 #3551a8 #30469f #2a3a95`. A map of n classes (9 to 16) takes the steps at the integers nearest i x 15 / (n - 1). The validator's figures are in [gui.md](../gui.md#frame-map): lightness monotone, the light end 2.08:1 against white and the dark end 2.14:1 against black, contrast rising with the value from 2.08 to 9.82:1 (light) and 2.14 to 10.08:1 (dark), neighboring steps 3.2 or more in OKLab delta E (x100) among 14 to 16 steps. The validator's one-hue and 0.06 lightness-step checks fail by design for this ramp and are accepted.
- **Three values in a unit** (not specified: the design says 2 to 4 are split): three columns.
- **Heap bound replaced by an allocation bound.** The design asks for a bound on the managed heap set from the first run. The retained heap moved by several MB between runs with the same map (a probe measured +1 to +5 MB for the window alone and even a negative growth in the 2,000,000-unit test, from the platform's surfaces and earlier tests' garbage), so it cannot catch a per-unit array. The test compares the bytes allocated on the UI thread while a map of 2,000,000 units (200,000 of 4 values) is shown, drawn at 12 scroll positions and resized, with those of a map of 30,000 (20,000) units after a warm-up run; the difference was under 70 KB and the bound is 512 KB, which a per-unit array of one byte would exceed (2 MB).
- **Speed, measured** (headless test, Skia, a window of 1,000 by 600, 4-core shared machine, 12 frames each scrolled to far rows and resized): 2,000,000 numeric units 9.8 to 12.8 ms a frame on average (maximum 20.8), 3,760 rectangles drawn for 3,840 squares on screen; 200,000 units of 4 values 18.1 to 20.0 ms (maximum 29.6), 15,040 rectangles for 3,840 squares of 4 cells. The design's prototype measured 24 to 41 ms a frame with the capture; both are far inside the 500 ms bound.
- **The scrollbar** is Avalonia's own `ScrollBar`, a visual child of `GraphView` that is arranged beside the lane (the design left open whether `ScrollBar` could carry the marks; the lane is the map's own drawing, as decided). Its 14 pixels, the lane's 8 and the gaps are always reserved, so the columns do not change when a file grows past one screen.
- **Severity filters** reach the map through `GraphsViewModel.ShownSeverities` (set by `DetailViewModel` with the Findings tab's three flags) and `GraphView.ShownSeverities`; rings, marks, the legend entry, the help text and the hover all follow them.
- **Findings** are resolved in `GraphsViewModel.MapFindings(report)` by walking each finding's path from the root to the first node of kind `Frame`; `GraphsViewModel.For(stream, findings)` hands them to every map of the report, which looks the offsets up among its squares.
- **The key's click** on a swatch and on a legend entry reuse `GraphView.CategoryClicked` and `GraphsViewModel.ActivateCategory` (the entry is a class for a numeric map and a category for a category map); a click on "Finding (first case)" does nothing.
- **"No value"** in a category map is shown only for squares with no unit at all (a series on the stream's frames that skips some), with no count; a value without a category stays empty and is not counted.
- **Existing tests** that list the forms of an entry now include "Frame Map" (`GraphTests`, `CategoryGraphTests`, `CategoryViewTests`, `MpegGraphsTests`, `AacGraphsTests`, `Ac3GraphsTests`, `CapitalizationTests`).
- **Fixtures** were found by script from the goldens and are listed in [development.md](../development.md); the screenshots are `map-block-usage`, `map-bitrate`, `map-ac3-coupling` and `map-findings`, light and dark.
- **Not built:** the follow-up series, zoom, and any parser or host change (none was needed).
