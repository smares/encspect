# GUI

```sh
dotnet run -c Release --project src/Encspect.Gui [-- <file-or-folder>...]
```

## Main window

The main window lists a folder's files on the right, with these columns:

- format and codec;
- duration;
- bitrate (VBR or CBR);
- sample rate and channel mode;
- channel layout by its name (the cell's tooltip lists the speakers);
- encoder with its confidence;
- "Written by": the software that wrote the file around the audio (the muxer, the container writer, a tagger), with its confidence. It need not be the encoder: a remuxed file keeps the encoder of its audio;
- tags;
- protection: "Protected" for a file with a protected stream, whose tooltip names the scheme and the DRM systems;
- findings by severity.

Columns sort by the figures behind them, so durations and bitrates sort as numbers.

- **Column widths:**
  - Every column has a fixed width in pixels; the list never squeezes a column on its own.
  - A column starts at a set width, or at the width its header needs where that is more: the header text, padding and sort glyph, measured at run time with the font in use, so no header is cut off whatever the system font.
  - The findings column also holds three counts of up to three digits, each after its dot, left-aligned.
  - Columns can be dragged wider, or narrower down to the width their header needs, not further.
  - Where the columns together are wider than the window, the list scrolls horizontally.
  - Name takes the width left over when the window is resized, never less than its set width, until you drag it; after that it keeps your width.

- **Opening files:**
  - Choose a folder in the tree on the left: home, music, desktop and the volumes, with subfolders listed when expanded.
  - File, Open Folder (Cmd+O on macOS, Ctrl+O elsewhere).
  - Drop files and folders onto the window.
  - Files and folders named on the command line open the same way.
- **Analysis:**
  - Files are analyzed in the background, a few at a time, and rows fill in as they are done. A list sorted by a column sorts again as they do, keeping the selected row.
  - Stop cancels, and choosing another folder cancels the run before it.
  - "Include Subfolders" walks the whole tree as the CLI does.
  - Rows keep a file's summary, not its report.

## Summary cache

A folder seen before fills in from the summary cache. A file takes its row from the cache without being analyzed when all of these are unchanged and it was analyzed by the same build:

- size;
- last-write time;
- file ID;
- first and last 64 KiB.

Only the other files are analyzed.

- **Location:** a file in the user's cache folder:
  - Windows: `%LOCALAPPDATA%\Encspect`
  - macOS: `~/Library/Caches/Encspect`
  - elsewhere: `~/.cache/Encspect`, or under `$XDG_CACHE_HOME`
- **Size:** it keeps the 50,000 files used most recently.
- **Refreshing:** View, Refresh (F5, Cmd+R on macOS) lists the folder again. View, Clear Summary Cache empties the cache and analyzes the folder from scratch.
- **Scope:** the detail window never uses the cache, and the CLI keeps none.

## Detail window

- **Opening:**
  - Enter or a double-click opens the selected file in the detail window.
  - The window is reused for the next file and follows the list's selection while it is open.
  - Previous and Next (Cmd or Ctrl with Up and Down) step through the list in its order, and Esc closes it.
  - "Open in New Window" in the list's context menu opens another detail window that stays on its file, for comparing two files.
- **Position:** it opens centered over the main window the first time, and where it was after that, also in later sessions (`settings.json` in the user's application data folder, under `Encspect`).
- **Analysis:** the detail window always analyzes the file again.
- **Contents:**
  - the streams at the top, each with its channel layout and speakers, and the object audio and JOC object count of a stream that has them, and the protection of a protected one ("encrypted: Common Encryption, AES-CTR (cenc); DRM systems: Widevine Content Protection", or "protected, not encrypted" where its samples are in the clear);
  - the structure tree, whose rows are made only as they are shown and whose children are read only when expanded, so a stream of 100,000 frames opens at once;
  - a hex view read from the file as it scrolls. Selecting a node marks its bytes, all its ranges where they are not contiguous, and, for a field of a few bits, the bits themselves, also in the bit strip below.
- **Explanations:**
  - Every row's tooltip says what the field is and what its value means.
  - The panel under the hex view shows that for the selected node, with a "Learn More" link to a background article on the concept behind it, such as mid/side stereo or the bit reservoir.
  - Articles open in a side pane, closed by default, whose links lead to related articles. Topics (F1) opens it on the selected node's article, or on the list of all of them; while the focus is in the graph area it opens the chosen graph's article (see "Graphs").
  - The column headers of the file list, the streams line and the graphs' choices explain what they show too.
- **Format:**
  - Where other formats also match with real evidence, the header says "Also matches:" and offers one button per format (its tooltip gives the evidence). Choosing one analyzes the file again as that format.
  - "Analyze as…" in the header lists every format, and "Detect Automatically", for a file whose format is not detected or detected wrongly. On macOS the same list is File, Analyze as.
  - A forced analysis says so in the header, with the format detection would have chosen. The choice stays while the window shows the file, through a refresh, and is dropped when another file is shown.
  - Forced analyses happen only in the detail window, which never uses the summary cache, so a forced result never reaches the file list or the cache.
- **Verification and decoding:**
  - The header shows a line "Verified: yes (…)", "no (…)" or "unknown (…)" for each stream that stores checksums, worded as the text output of the command line is (`--verify` in [the command line](cli.md)). Without decoding it says that the checksum over the decoded audio, such as FLAC's MD5, was not compared.
  - "Decode Audio" in the header decodes the audio of the file shown, compares those checksums and measures the audio (the panels under "Audio" in the Graphs tab, below). It is one command and one decode: the command line's `--verify` and `--measure` and the GUI's command run the same decode.
  - **Enabled** where some stream's verification says that decoding was not asked for, which every stream of a coding with a decoder says, with checksums (FLAC, WavPack, Monkey's Audio, TrueHD) or without (WAV, Wave64 and AIFF, whose decoding is for the measurements). It analyzes the file again with decoding, which reads all of it and takes longer than the analysis.
  - The file stays on screen while it runs, with the progress bar, and "Stop" in its place ends it and leaves the analysis as it was. When it ends the new analysis replaces the old, keeping the selected node, and the line shows the verdict.
  - A refresh, or analyzing the file as another format, keeps decoding for the file shown; it runs as an ordinary load, with no Stop button, since a file that was decoded once has shown it decodes and the load replaces the analysis as every reload does. Showing another file drops it. On macOS the command is File, Decode Audio. Only the detail window decodes: the file list and the summary cache never do, so a list's verdict comes from coded data alone.
  - A failure to decode (a file that went away, for instance) shows as "Not decoded: …" next to the buttons and leaves the analysis as it was.
- **Findings:** can be filtered by severity. Selecting a finding selects its node.
- **Export:** Cmd or Ctrl+S writes the report as text with offsets or as JSON, as `--offsets` and `--json` would.

## Graphs

The Graphs tab, next to the findings, draws the stream's series: the bitrate and, for Layer III, the bit reservoir fill, per frame and per second of audio, and the distribution of the bitrates, and the category series of its coding choices. A file of several streams has a choice of stream.

- **The list:** the graphs are a vertical list on the left of the graph area, and the chart takes the rest. The list is grouped under the same headings in every format, so an MP3 and an AAC file are compared the same way: "Summary" (the Coding Summary), "Stereo Coding", "Block Switching", "Coded Bandwidth", "Bit Distribution" and "Other", then "Audio", whose panels come from the decoded audio rather than from series (see "Audio panels"). A series says which heading it belongs to (its concept); MPEG audio sets it on the bitrate, the bit reservoir fill, the coded bandwidth and the channel bit share, and the bitrate series of a format that sets none go under "Bit Distribution" by their unit (kbps), and the bit reservoir fill by its (bytes). A heading with no entry for the file is not shown, except "Audio" of an audio stream, which always has its panels or its caption.
- **Long names wrap:** an entry whose name is wider than the list (210 pixels) continues on a second line, so a name is never cut; its tooltip gives the name and then what the graph shows.
- **What opens first:** the first entry of the list, the Coding Summary where the stream has category series (every MPEG file), else the first graph. The entry chosen stays chosen, by its title, for the next file that has one. The selected entry is scrolled into view in the list, whatever selected it, and the list shows its scrollbar when it overflows, and it shows whole items only: its viewport is as high as the headings and entries that fit (the rest stays empty below it), it scrolls from the top of one item to the top of another, and where all fit it has no scrollbar.
- **One entry for each quantity,** with the forms it can be drawn in as a view switcher above the chart, remembered for the next entry that has a form of that kind:
  - a category series: "Over Time", "Smoothed" (only for a stream of more than 30 one-second intervals), "Frames" and "Summary" (see below);
  - a numeric quantity such as the bitrate: "Per Frame", "Over Time" (per second of audio) and, for the bitrate, "Distribution"; the channel bit share adds "First and Second", the two channels' shares as two segments of each interval with the 50% line marked.
  - **"Frame Map"**, the last form of every entry that has a series per frame (every category series and every quantity with a "Per Frame" form): each frame as a square, in rows like text (see "Frame Map" below). The entries without units, the Coding Summary, the quantities with a series per second only and the panels under "Audio", have none. Picking an entry in the list is what changes what the map shows, and the form is remembered for the next entry as the others are.
  - The forms of one quantity are the series of the stream that carry the same `quantity` (JSON). A series that names none is paired by what it is, never by name: a stream's one per-frame and one per-second series in kbps are the bitrate, and the same two in bytes the bit reservoir, until their formats set a `quantity`; MPEG audio does.
- **Layout:** the pane below the tree and hex view starts at half the window and its splitter position is remembered, with a minimum height. The plot is never lower than about 110 pixels (a strip at least what its rows need, 8 pixels each): the legend folds to one line first, a strip whose rows do not fit becomes one stacked row, and what still does not fit scrolls with the mouse wheel. Percent ticks are every 25% where each has room, else every 50%, else the ends.
- **Capitalization:** everything that names something (headings, entries, the view switcher and its "View" label) is Title Case, as the tree's node names and the file list's columns are; values and prose (category names in legends and tables, captions, hover text, descriptions) are sentence case.
- **One font size:** headings, entries, the view switcher and the chart's text (legend, tables, hover) are the app's size; a heading differs by weight and the secondary color. Only the tick labels along the axes are a point smaller (12), since they sit close together; the one-line captions above the plots are the standard size.

- **Hovering** a point shows its value.
- **Clicking** a point selects its frame in the tree and hex view. A bar of the distribution selects the next frame of that bitrate, and a point of a per-second graph the first frame of that second.
- **Selecting** a frame in the tree marks it on the graph.
- **Speed:** graphs of 100,000 frames draw as quickly as short ones, since a line is drawn as the smallest and largest value of each pixel column.
- **Keyboard:** Tab reaches the stream choice, the entries, the view switcher and the graph. Left and Right move a cursor from point to point, from interval to interval over time and from cell to cell in the strip (Ctrl moves by a pixel column's worth of cells, Home and End to the ends), and Enter selects what the cursor shows. In a strip of several rows Up and Down change the row; in a table, Up and Down step like Left and Right.
- **Automation:** the entries and the switcher's buttons have names. The graph's name is its title and its help text lists the legend with the shares and says what the cursor is on, so what the picture shows is in words; the legend entries are not elements of their own.
- **Learn More:** under the chart, a "Learn More: <article title>" link to the background article on what the graph shows, in the style of the node link in the details panel.
  - A category series has its series' article, in every form, the Frame Map included. A legend or key entry has no link of its own: the category values carry no article.
  - A numeric series (the bitrate, the bit reservoir) has none, and neither has the Coding Summary; the link is then not shown.
  - The link is a button: Tab reaches it, Enter or a click opens the Topics pane on the article, and its automation name is its text.
  - F1 opens the article of the chosen graph while the focus is in the graph area (the list, the view switcher, the chart, the link), and as before the selected node's article anywhere else. With the pane open on another article, F1 moves it to the graph's; with the pane open on the graph's, it closes it.
- **Audio:** the last heading of the list; see "Audio panels" below.

### Audio panels

Under the heading "Audio" the list has the panels drawn from the measurements of the decoded audio (`StreamInfo.Measurements`; [the design](design/spectral-analysis.md)): "Waveform", "Spectrogram", "Spectrum", "Loudness" and "Figures". They appear after "Decode Audio" (see "Verification and decoding" above), which also chooses the spectrogram. A stream without measurements has the heading with one line of caption under it instead.

- **Captions before decoding** (sentence case, one line under the heading, and in place of the chart while the stream has no other entry):
  - Decoding not asked for: "Decode the audio to see its waveform and spectrogram." with a "Decode Audio" button, which runs the command of the header.
  - A stream with no decoder, or a decoder its container does not hand the stream to (FLAC, WavPack or TrueHD in Matroska or MP4): "The audio of this FLAC stream is not decoded here." with the codec's name. The text never says no decoder exists, since one may.
  - A stream that cannot be decoded, or that decodes to something the measurements do not take (WavPack's DSD): "The audio cannot be decoded: …" with the reason.
  - Measuring that stopped, or whose blocks changed format, or that failed: the panels it has and a line that says so ("Decoding stopped after 1.5 s of audio; the figures cover what was measured.").
  - A stream of a format whose decoder cannot describe it (a malformed PCM format) has no session and gets the "not decoded here" caption.
- **Spectrogram:** the average power over time (left to right) and frequency (0 Hz at the bottom to half the sample rate at the top), 512 rows and up to 1,024 columns, drawn from the grid's bytes through a fixed ramp of 256 colors (matplotlib's "magma", CC0, credited in the notices) into a bitmap scaled to the panel.
  - The ramp runs from -160 dB (nearly black) to 0 dB (pale yellow), the level of a full-scale sine centered on a bin; a color bar with its levels sits at the right. A cell with no data is black, like a level of -160 dB or less.
  - **Where there is no data:** a column holds the frames that start in it, and a frame is a whole FFT frame of 4,096 samples (two hops of 2,048), so the last frame starts at least 4,096 samples before the stream's end. With one hop per column the last two columns of a stream hold no data (a stream of 1 s has 22 columns, 20 and 21 of which are black); a gap leaves the columns it covers black too, and so does a stretch between gaps shorter than a frame. A column that merges several hops holds the mean of those that have data.
  - **Time axis:** a column is placed by the hop where its frames start, not where they are centered (a frame is centered one hop after its start), and the hover names the middle of the column's hop. The axis runs from the first column's start to the last column's end, which lies up to a column past the end of the stream (1.02 s on a stream of 1.0 s); the stream's end is not drawn, and the black columns at the right are that stretch.
  - A stream whose blocks were all empty has no grids, so it has no Waveform or Spectrogram entry.
  - **Scaling:** a grid with fewer columns than pixels is drawn as blocks, without smoothing, so a column with data does not fade into one without; wider grids are scaled with smoothing.
  - The spectral cutoff is a dashed line across the plot with its frequency; it is white on a dark halo, so it reads on any color of the ramp and needs no color of its own.
  - **Contrast of the line:** white on the ramp's palest entry (#FCFDBF) is 1.05:1, so the halo carries it: black at 85% opacity, 5 px wide (1.75 px beyond the line on each side, so that it shows at 1x scaling), is 5.4:1 against that entry. No hue was added, so the palette validator does not apply.
  - **Hovering** a cell says the time, the frequency and the level: "0:00.5 · 16,774 Hz · -109 dB". On the row of the cutoff (within two rows) the text continues with the cutoff's figures: "Spectral cutoff: 16,796 Hz (a step of 59.1 dB, transition from 16,538 Hz, in 20 of 20 frames with signal)".
  - **Clicking** selects the frame that holds that time in the tree and hex view (see "Finding a frame by time"); the frame is marked on the picture by a white line across it.
- **Waveform:** a lane for each channel, named by its speaker where the stream states them, with the smallest and largest sample of each column as a filled band in the accent color, in 16-bit sample codes; hovering says the time and each channel's range (the first eight channels), clicking selects a frame as in the spectrogram.
- **Spectrum:** the average level of each of the 512 rows over all frames, in dB over frequency, as one line for all channels but the LFE ("Combined") or one line for each channel ("Per Channel", offered for a stream of several channels; the six hues of the category charts, with a legend of the channel names; beyond six the lines are gray). The cutoff is a dashed vertical line with its frequency. Hovering says the frequency and the level of each line, with the cutoff's figures near its line, and marks each line with a dot at that level. The spectrum covers the whole stream, so clicking it selects nothing.
- **Loudness:** the short-term loudness (a window of 3 s, one value every 100 ms) in LUFS as one line over time, with the integrated loudness as a dashed horizontal line and a header with the loudness figures ("Integrated Loudness: -23.0 LUFS · Loudness Range: 5.2 LU · Maximum True Peak Level: …", the first two where the width does not fit all five). The axis is in multiples of 5 LU, at least 20 LU apart, from -70 LUFS up. A stream of more than 1,024 values shows them merged in pairs (the hover says "average of 8 values"); the points sit at their times, so a stretch lost to a gap shows as a longer step of the line between two points (it is not marked otherwise). Hovering says the time and the value; the panel is only offered for a stream of 3 s or more (shorter streams have the figures in "Figures"), and a clip's panel selects no frame.
- **Figures:** the lines the command line prints under "Measured:" ("Spectral Cutoff", "Sample Peak", "RMS Level", "DC Offset", "Clipping", "Digital Silence", "Zero Samples", "Low Bits Unused", "Integrated Loudness", "Loudness Range", "Maximum True Peak Level", "Maximum Momentary Loudness", "Maximum Short-Term Loudness"), worded the same way, each with its description as hover text. They are figures with units, never a verdict.
- **Links in the panels:** each panel has its "Learn More" link under the chart: "Waveform" the article on measurements of decoded audio, "Spectrogram" and "Spectrum" the one on the spectrogram and average spectrum (it defines the average spectrum, of which the spectral cutoff is a figure read off), "Loudness" the one on loudness, and "Figures" the one on measurements. In "Figures" the name of each line is itself the link to the article of that line (hand cursor, underlined on hover and focus, Tab and Enter, automation name and hover text "Learn More: <title>"; a line without an article has a plain name) (the spectral cutoff, levels, loudness, true peak, clipping, DC offset, digital silence, bit depth usage); the line's text is unchanged.
- **Finding a frame by time:** the tree knows frames by byte offset only and no node states the samples it codes, so the lookup (`SampleLocator`) works from what the stream reports: the byte offsets of its frames (the per-frame series) and its sample count.
  - It reads the frames as equally long, the last excepted, which FLAC (a fixed block size), WavPack, Monkey's Audio and TrueHD write: the frames cover the stream's sample count from where they start, and sample position p of N frames and T samples is in frame floor((p - base) × N / T).
  - **Where they start:** the base is the end of what was measured less the sample count, so a stream cut from a longer one (a FLAC whose first frame is at sample 4,096, or a fragment at 2^24) is found from there, and one whose first frame failed to decode (Monkey's Audio) from 0. Where the measuring stopped early the base is the first measured position. A gap lies inside the span, since the measured positions count the samples it skipped, so it moves no frame.
  - That is exact where the last frame is full, and otherwise never earlier than the true frame and at most one frame later; a stream of frames of different lengths (FLAC with a variable block size) can be off by more, and so can a stream whose sample count leaves out the frames the decoder lost. The frame is the one near that time, not a statement about which samples it holds, and the GUI says nothing more than "selects the frame".
  - **The mark:** a frame selected by clicking a panel keeps the clicked column as its mark. A frame selected in the tree is marked in the column of its estimated first sample, which is never later than the true one and less than one frame earlier.
  - A stream without frames (the samples of WAV, Wave64 and AIFF are not framed) has panels, hover text and no click.
- **Keyboard:** Tab reaches the graph, as for the others. In the spectrogram Left and Right move the cursor from column to column (Ctrl by a pixel column's worth), Up and Down from row to row (Ctrl by eight), Home and End to the first and last column, and Enter selects the frame, as clicking does; the cursor starts on the row of the cutoff where there is one. The waveform and the spectrum take Left and Right, Home and End like the lines.
- **Automation:** the graph's name is the panel's title; its help text says the axes ("Time from 0:00.0 to 0:01.0, frequency from 0 Hz to 22,050 Hz, level from -160 to 0 dB"), the cutoff's figures, the legend of a per-channel spectrum, and what the cursor is on. The entries of the list, the form buttons and the "Decode Audio" button have names; each line of "Figures" has its name and text.
- **Colors:** nothing is added to the palettes. The spectrogram's colors are the ramp, the same in light and dark mode on its own black ground; the waveform and the single spectrum line use the accent color; the per-channel lines use the six hues validated above, on the surface of the mode; the cutoff and the marks on the spectrogram are white with a dark halo, and on the surface-colored charts the text color.
- **Speed:** the spectrogram is one bitmap of the grid, made once per analysis, and a frame draws it scaled; the waveform draws one polygon for each lane and the spectrum one path for each line. A test draws grids of 1,024 columns (8 channels, 512 rows) and holds a frame to the 500 ms of the other graphs and a pointer move (which draws a frame again) to 250 ms. Measured here in the headless test with software drawing: 58 ms a frame and 49 ms a move for the spectrogram of 1,024 columns, 33 and 32 ms for the waveform of 8 channels, 17 and 11 ms for the per-channel spectrum.

### Category series

A category series (the Layer III block types, the stereo coding of each frame, AAC's window sequences) is one entry of the list with four forms:

- **Legends and tables:** the legend of an Over Time, Smoothed or Frames chart lists the categories that occur (over time in the order of the stack, largest share first; in the strip in the order of the categories), and the summary tables list every category of the series, a count of 0 grayed, as EncSpot's do. The count and the share of a row are right-aligned in columns measured from their widest text, at least 12 pixels apart.
- **Over Time:** a 100% stacked area with stepped edges, over the intervals of audio time.
  - Each interval is a level that lasts exactly as long as the interval, with a vertical step at each boundary and no gap or slope between intervals, so a sudden switch of coding stays a sudden switch.
  - The category with the largest share over the stream is the bottom layer, the only one with a flat baseline; the legend lists the layers in the same order, bottom first, and the hover text lists the shares in it too (the strip's hover text keeps the rarest first).
  - **Colors follow the stack, not the category** (the project lead, 2026-10-23): the bottom layer takes the muted first hue and the rarer layers above it the next hues in full, so that rare layers stand out; the legend shows the colors drawn. The same category can therefore have another color in the Frames strip and the summary tables, which keep the color of the category, and in another file where it is not the largest. A series of ordered steps keeps its ramp by category, and a reserved category its hatching.
  - Levels are whole pixels, and a category that occurs keeps at least one pixel of its layer, so the picture is not exactly proportional for a very rare one; the hover text gives the true shares. A line of the surface color, one pixel, separates two layers where both are at least 3 pixels high, as in the strip; a rare layer keeps its color, and a full switch shows no stripe at its step.
  - **No names inside the layers** (the project lead, 2026-10-23, after screenshots): the legend, in the order of the stack with the shares, names them, and the hover text and the automation text do too. Each layer is told from the one below and above it by hue and by its place, since the order of the layers is the order of the legend. The colors of adjacent layers differ little in lightness (WCAG contrast ratios between neighbors, bottom to top: 1.18, 1.14, 1.30, 1.24 and 1.84 in light mode, 1.69, 1.14, 1.11, 1.28 and 1.25 in dark mode), so the separation is by hue (the validator's figures below) and by the one-pixel surface line between layers at least 3 pixels high; a thinner layer has no line and borders a layer of another hue.
  - Where intervals outnumber pixel columns, neighboring intervals merge into one level, weighted by their values.
  - A last interval that the stream ends inside (a file of 6.03 seconds has a seventh second of one frame) is as wide as it lasts against the others, in this chart and in the share of the two channels, but at least 3 pixels, taken from the interval before it, so that it can be seen and pointed at on a file of minutes.
  - When that partial last interval holds one to seven frames it keeps its exact shares and is drawn hatched, since a share of a few frames says little: lines of the text color falling to the right (the reserved categories' hatch rises to the right, in the surface color), over the layers' own colors, which show between the lines. Its hover text starts with it, "Last 0.03 s, 1 frame", and a note next to the chart's caption names it, "Last 0.03 s: 1 frame, hatched", shown only while it is hatched. A last interval as long as the others is not hatched, however few frames it holds, nor one without frames, nor the average of the smoothed form, nor a column that merges several intervals (above some 800 intervals, around 14 minutes at 830 pixels), which has no hover note or caption note either.
  - An interval without values (a second that no frame starts in, or no frame of a series that holds a value only in some frames) is a gap, not a level.
  - Hovering shows the interval's shares; clicking selects its first frame.
- **Smoothed:** the same area with each interval averaged with the two on either side (a centered moving average over 5 seconds, the window shrinking at the ends, every value weighing the same). It is an option for reading a trend in a long file, offered only above 30 one-second intervals, and never the default, since it blurs a switch. An interval without values of its own stays a gap, and nothing is averaged into it. Its hover text gives the interval's own shares and the average the chart draws with the number of seconds it holds (three at the ends, fewer around a gap), "average of 3 seconds"; it does not dim the last interval.
- **Frames:** a strip, one cell for each frame. A fixed layout of up to 8 values (Layer III's granule 1 and 2 of each channel, AC-3's 6 blocks) has a row for each value; a variable layout, or one of more values, draws one row whose cell stacks the shares of the unit's values. Where frames outnumber pixel columns, a column stacks the shares of its frames.
  - **Rare categories stay visible:** every category in a column keeps at least one pixel of its stack, taken from the largest, and a column that holds a category in one frame only gets a one-pixel tick above the row in that category's color. At 100,000 frames, a single short granule among 400 values would otherwise be 0.03 pixels. The hover text says the column is drawn with its rare categories enlarged and gives the true counts, and where several frames are marked it says how many and that clicking again steps to the next.
- **Summary:** a table of each category's name, count and share with a bar, like EncSpot's. The **Coding Summary** entry shows the tables of every category series of the stream at once. A series that has values past its caps says how many were counted without a code. Long tables scroll with the mouse wheel.

Interaction:

- **Hovering** says the interval and every share ("0:12 to 0:13 · Short 12.5% (24 values) · Long 87.5% (168 values)", the rarest first), a cell its frame and category ("Frame 1,234 · 0x1A2B · granule 2, channel 1: Short"), a table row its count and share.
- **Clicking** an interval of the over-time view selects the first frame of its interval, found from the time index and not estimated; an empty interval selects nothing. A cell selects its frame, and a column of many frames a frame that holds one of the categories its ticks mark; the ticks of a column share its pixel, so clicking the column again selects the next such frame after the selected one, wrapping around. A table row or a legend entry selects the next frame after the selected one that has its category, wrapping around, so choosing it again steps through them.
- **Selecting** a frame in the tree marks it with a line across the over-time view and the strip.

Colors and text:

- **Palette:** six fixed hues, not the system accent, so a category has one hue in every file and in both modes in the strip and the tables (the modes use different steps). Light: `#2a78d6`, `#eb6834`, `#1baf7a`, `#eda100`, `#e87ba4`, `#008300`. Dark: `#3987e5`, `#d95926`, `#199e70`, `#c98500`, `#d55181`, `#008300`. In the strip and the tables the first category, the plain coding, takes the first hue in a muted step, in the over-time forms the bottom layer does (see Over Time), a mix of 0.7 of the hue with the surface (`#6aa0e2` in light mode, `#285ea0` in dark).
  - An **ordered** series (frame sizes, submodes) uses a ramp of the first hue from light to dark for up to eight steps. Any category past the sixth (past the eighth of a ramp) is one flat gray, "Other"; no hue is ever generated. The legend, the table and the hover name every category.
  - A category the parser marks reserved (`Category.IsReserved`, `isReserved` in JSON) is gray with a hatch, since it marks a value the specification does not allow; the front end never reads the name. A value without a category stays empty, and its hover text says "no category". Stacked segments are separated by a two-pixel gap in the surface color where both are at least four pixels high; a thinner one would vanish in it.
  - With the system's high contrast preference (`Application.PlatformSettings.GetColorValues().ContrastPreference` of Avalonia 12) every second category is hatched too.
- **The validator** (the `dataviz` skill's `validate_palette.js`, run on these values and on the palette as drawn, with the muted first step, against the graph surfaces, `#ffffff` and `#000000`, which a test reads from `SystemRegionBrush`): light mode passes its checks, with a worst adjacent separation for color vision deficiencies of ΔE 9.1 (protan) and 5.8 (tritan, below the method's floor of 6), 19.6 for normal vision, and aqua, yellow and magenta below 3:1 against the surface (2.82, 2.17, 2.69), and the muted blue too (2.71); dark mode passes with 8.4 (protan), 8.7 (tritan), 19.3 and all six at 3:1 or more, the muted blue included. The ordered ramps are one hue in steps and are not categorical, so the validator's checks do not apply to them. The tritan figure and the contrast warnings are accepted because names and shares are always in text: the legend (over time in the order of the stack, so that a layer is found by its place), the table, every hover and the automation text, and the segments of the strip carry their names where wide enough; the layers of the over-time charts carry none (see Over Time).
- **Speed:** a view aggregates once per width and series, in one pass over the codes, and draws at most columns × categories rectangles in the strip and one outline for each layer and run in the over-time charts; the counts of the intervals are made once for a graph, so a new width merges them and does not scan the values again. A test holds 200,000 units of 4 values to the 500 ms a frame the 200,000-point line is held to.

### Frame Map

The form "Frame Map" draws every unit of a series (a frame, packet or block; the entry's series says which) as one square, so that a whole file is seen at once and a file of two million frames costs what a screen of squares does. A frame is a square, never more than the series says: the hover calls it "Frame".

- **Squares:**
  - 10 by 10 device-independent pixels with a gap of 2, rounded to whole physical pixels so that every square has the same size and sharp edges at any scaling.
  - Row-major like text, frame 1 at the top left; as many columns as fit, in steps of 10 (at least 10), so a resize reflows in steps of 10; the number of the row's first frame is in the left margin every fifth row.
  - **A resize keeps its place:** the first frame of the top row stays in the top row when the columns change, and a selected frame that was on screen stays on screen (the selection wins where both cannot hold).
  - **A pane too narrow for 10 columns** (below about 210 pixels): the squares are clipped before the lane and the scrollbar, never over them, and the line above the map reads "Widen the pane to see 10 columns".
  - The pane scrolls vertically, with a scrollbar and the mouse wheel; a short file fills the top of the pane. Only the rows on screen are drawn and a square's color is read from the series when it is drawn, so the map keeps no array per unit.
- **Which squares:** the stream's frames (those of its first series per frame), so that the squares stay in place when another entry is picked; a unit the series has no value for is an empty square with a hairline outline, and the hover says "no value". A series whose units are not among the stream's frames (WMA standard's bit reservoir, one point per superframe) has squares of its own, and the caption says "One square per point of this series".
- **Numbers, the key:**
  - One color per value where the series has at most 16 distinct values, else 8 classes of equal width from its smallest value to its largest. Up to 8 values use the map's single-hue ramp (the first hue, below); 9 to 16 use the multi-hue ramp below.
  - **A higher value has more contrast against the surface:** darker in light mode, lighter in dark mode, where each ramp has steps of its own (not the light list reversed). This holds in the map only; the ordered category series keep their ramp.
  - No legend list: one thin line under the map, the smallest value, a swatch for each class from low to high, the largest value with its unit ("32 kbps" then the swatches then "320 kbps"). A swatch's hover gives its value or range and its count ("128 kbps · 1,263 frames"), and clicking it selects the next frame with that value after the selected one, wrapping around. A series with one value (a CBR stream's bitrate) is one swatch and "Every frame 128 kbps". After the key come "No value" with its count, where there are such squares, and "Finding (first case)", whose click selects the next ringed frame after the selected one, wrapping around, among the severities the Findings tab shows.
- **Declared ranges (the clamp):**
  - A series may declare its range (`Series.DeclaredRange`): MPEG's bitrate declares its table, in kbps (14 steps), the channel bit share 0 to 100 %. The map then shades by the declaration, so that a color means the same in every file of that kind.
  - **Steps:** one color for each declared step, fixed by its position in the list, whether or not the file uses it. The key shows every step and draws an unused one as an outline of its color with nothing inside (hover "0 frames"), which reads as unused on white and on black alike; its ends are the declared first and last step. 128 kbps is the same shade in a CBR file and in a VBR file from 32 kbps.
  - **A fixed range** (`Between`): 8 classes of equal width over the declared minimum and maximum, even where the file has few values, and the key's ends are the declared ones.
  - **Clamp:** a value below the range goes to the first class and one above it to the last; the hover says so ("Frame 1,234 · 0x1A2B · 512 kbps (above the declared range of 32 to 320 kbps)"). A value inside the span of steps but not on one goes to the nearest step (the lower on a tie) and the hover says "not a declared step". The key line counts such squares ("Outside the declared range · 3 frames"). The map neither rings such a square nor words a verdict: a file that breaks its format has findings for that.
  - A series that declares nothing keeps the file's own range.
- **The ramps** (the build's choice; `MapColors`): each mode has steps of its own, validated against its surface with the `dataviz` skill's `validate_palette.js`, because one list cannot clear white and black with an even step. Higher values keep more contrast against the surface in both modes (a test holds this for every number of classes).
  - **Multi-hue, 16 steps** (9 to 16 classes take the steps nearest to evenly spaced ones, the ends included; 14 declared steps take 14 of the 16). The steps were found by a search that maximizes the smallest difference between neighbors under normal, protan, deutan and tritan vision, with the hue moving one way along the list and the lightness falling (light) or rising (dark) evenly:
    - Light, from a lime through green, teal and blue to a dark violet (OKLab L 0.77 down to 0.25): `#b8bd35 #a3b452 #8dab64 #78a171 #62977c #069292 #298294 #39709e #3e609c #425782 #414d6a #31426f #37306f #372956 #361753 #33103a`.
    - Dark, from a violet through blue, teal and green to a yellow (L 0.39 up to 0.90): `#672170 #5f358a #613ea4 #5f52a6 #5a65a5 #5277a2 #67829b #608db7 #7498ae #67abac #7fb5a0 #94c096 #aaca86 #c1d373 #d7dc5b #eee533`.
  - **Single-hue, 8 steps** (up to 8 classes; fewer take steps of it; one class takes a middle step), the hue of the first category color (blue):
    - Light, L 0.76 down to 0.30: `#80b3fe #5f9df8 #4b88e2 #3774cc #2360b6 #0b4ca0 #013a83 #012a63`.
    - Dark, L 0.42 up to 0.90: `#06489c #205db3 #3572ca #4a87e1 #5f9df8 #80b3ff #a6caff #cbe0fe`.
  - **The validator, ordinal mode** (`--ordinal`, surfaces `#ffffff` and `#000000`):
    - Single-hue, both modes: all four checks pass. Lightness is monotone; neighbors are at least 0.065 (light) and 0.067 (dark) apart in OKLab L against the 0.06 floor; the lowest value has 2.14:1 against white and 2.41:1 against black against the 2:1 floor; the hue spread is 0 and 1 degrees. The lowest value clears the floor for every number of classes, since fewer classes take steps of the same list.
    - Multi-hue, light: lightness monotone (pass), the lowest value 2.03:1 against white (pass, floor 2:1), contrast rising from 2.03 to 16.46:1.
    - Multi-hue, dark: lightness monotone (pass), the lowest value 2.03:1 against black (pass), contrast rising from 2.03 to 15.89:1.
  - **The validator, categorical mode, for the adjacent difference** (OKLab delta E times 100, the smallest between neighbors; steps taken as a map takes them):

    | Classes | Mode | Normal | Protan | Deutan | Tritan |
    | --- | --- | --- | --- | --- | --- |
    | 9 | light | 4.5 | 4.7 | 4.3 | 4.2 |
    | 9 | dark | 5.6 | 5.3 | 4.2 | 4.8 |
    | 12 | light | 4.5 | 4.2 | 4.2 | 4.2 |
    | 12 | dark | 4.6 | 4.2 | 4.3 | 4.2 |
    | 14 | light | 4.4 | 4.2 | 4.2 | 4.2 |
    | 14 | dark | 4.5 | 4.2 | 4.2 | 4.2 |
    | 16 | light | 4.4 | 4.2 | 4.2 | 4.2 |
    | 16 | dark | 4.5 | 4.2 | 4.2 | 4.2 |

    - The single-hue ramp at 8 classes: 6.5, 6.6, 6.0 and 6.0 in light mode and 6.8, 6.8, 6.3 and 6.0 in dark mode (normal, protan, deutan, tritan).
    - The previous list, one for both modes, had 3.2 (normal), 1.5 (protan) and 1.8 (tritan) at 14 steps; the new lists are at least 4.2 under every vision, and a test pins 4.1 for 9, 12, 14 and 16 classes (`MapColorsTests`).
  - **Accepted failures:** the ordinal checks of a lightness step of at least 0.06 and of one hue fail for the multi-hue ramps by design (16 steps over L 0.52 are 0.034 apart; the hue spread is 151 and 143 degrees), as the skill says to expect for a sequential ramp. The categorical checks of the lightness band, the chroma floor and the normal-vision floor of 15, and the CVD target of 8, do not apply to a ramp of 16 neighbors (the closest pair is 4.2) and fail; the contrast against the surface is below 3:1 for the three lowest steps (2.03 to 2.7). All of these rest on the same ground as the category colors' accepted figures: a swatch, a square and the hover always carry the value in text, and the key names both ends.
- **Categories:** each category in its color of the strip (`CategoryColors.For`: the six hues with the muted first, the ramp of an ordered series, gray "Other", the hatched gray of a reserved category, every second category hatched with the high contrast preference); the legend under the map lists the categories that occur with their shares, as the strip's does, and clicking an entry selects the next frame with that category. A value without a category is left empty, and the legend's "No value" counts the squares drawn empty ("No value · 12 frames"): those with no unit, no values, or only values without a category; the hover says "no value" for the first and "no category" for a value that has none.
- **Units of several values:** 2 values are left and right halves, 3 are three columns and 4 are 2 x 2 in reading order (Layer III's granule 1 on top, channel 1 left), a physical pixel apart, in cells that differ by at most one physical pixel (at 100 percent a 10-pixel square is 4 and 5 pixels wide for 2 values and 2, 3 and 3 for 3, the wider cells last); more than 4 values, and every variable layout, are a share mix, horizontal bands from the bottom in the order of the categories, each category that occurs at least a pixel high. The hover lists every value with its name ("granule 1, channel 1: Long"), up to 32.
- **Findings:** a finding is a ring on the square of the first frame along its path (a finding of the stream, a tag, a gap or the root lies in no frame and is not on the map). A finding on a frame the series leaves out gets no ring either: the ring is looked up among the map's squares, so a truncated Monkey's Audio frame or a mid-stream Xing frame that the bitrate series skips has none (the Findings tab lists it). The ring is the text color, 1.5 pixels inside the square's edge with a 1-pixel line of the surface inside it, so that one of the two contrasts with any fill. Most findings stand for many cases and point at the first, so the legend says "Finding (first case)" and the hover "Warning: … (first of 50 cases)". The severity filters of the Findings tab apply: a severity hidden there has no ring, mark or text on the map.
- **Selection:** the selected frame has a 2-pixel ring in the accent color outside its square, in the gap; selecting a frame in the tree scrolls its square to the middle only when it is off screen.
- **The lane:** 8 pixels wide between the squares and the scrollbar, as high as the pane, the whole stream in proportion; a 3-pixel mark for each finding in the colors of the findings list's dots, and the selected frame in the text color. Clicking the lane scrolls there.
- **Hovering** a square says its frame, offset and values ("Frame 1,234 · 0x1A2B · 192 kbps") and its findings; **clicking** selects the frame in the tree and hex view. The hover moves with the rows when the wheel, the scrollbar or the lane scrolls under a pointer that does not move.
  - **The tooltip** is above the square when that fits, else below it, else beside it, and never covers the square it describes (the first row's tooltip is below it); the same holds for a key swatch.
  - **A click on a half-visible square** in the bottom or top row scrolls the least that shows it whole; it does not recenter the map. A frame selected in the tree that is off screen altogether is still scrolled to the middle.
- **Keyboard:** Tab reaches the map. The first arrow key puts the cursor, a dashed ring in the text color, on the selected frame or the first square on screen; then Left and Right move it by a frame, Up and Down by a row, Page Up and Page Down by a screen, Home and End to the first and last frame, and Enter selects what the cursor shows. The cursor is scrolled into view.
- **Automation:** the map's name is the entry's title; its help text says what it shows in words: "Frame map of 76 frames, 20 to a row. 32 to 320 kbps, darker is higher, 14 values. 3 frames with findings. Cursor: Frame 4 · 0x…", or the legend of a category series.
- **Speed and memory:** a test draws 2,000,000 numeric units and 200,000 units of 4 values while scrolling to far rows and resizing (12 frames, `ENCSPECT_MAPBENCH` names a file the figures are appended to). Measured here in the headless test with software drawing (four runs): 11.7 to 12.7 ms a frame for 2,000,000 numeric units (3,760 rectangles drawn of 3,840 squares on screen) and 18.3 to 21.8 ms for 200,000 units of 4 values (15,040 rectangles of 3,840 squares), against the 500 ms of the other graphs. The rectangles drawn are counted and may not exceed the squares on screen times the cells of a square, which fails if the drawing stops skipping rows. Two bounds on the bytes allocated on the UI thread while the map is shown, drawn at 12 scroll positions and resized:
  - **Against a small map:** the bytes of a map of 30,000 units (20,000 for the split squares) are subtracted; the difference was under 120 KB, and a per-unit array would add at least 2 MB, so the test fails above 512 KB. This cancels anything allocated for each square drawn, since both maps draw the same squares.
  - **Per frame, absolute:** one frame (an invalidation, a render and a capture) allocated 94 to 95 KB for the numeric map and 145 to 149 KB for the split squares in four runs, whatever the number of units; the test fails above 128 KB and 192 KB (about a third of margin). A `double[4]` for each square drawn, planted as a check, brought them to 290 KB and 345 KB, which the relative bound missed and this one catches.

## About

"About Encspect" shows what build this is.

- **Where:** on macOS in the application menu, as usual there; on Windows and Linux in the Help menu, the menu bar's last menu.
- **Shows:** the name, the version, one line on what Encspect does, the copyright and the MIT License.
- **Version:** the same string as `encspect --version` prints after the program name and as `tool.version` in JSON reports (`0.1.0+<commit>`), read from the same place in the host. It can be selected and copied for a bug report.
- **Third-Party Notices:** the button opens a window with the text of `THIRD-PARTY-NOTICES.md`: the licenses of Avalonia, Skia, HarfBuzz, ANGLE and the .NET runtime. The text is embedded in the executable, so it is there in a single-file build too and is the same file the download carries next to the executables.
- **Keyboard:** Esc and Enter close the window; the Close button has the focus when it opens. Tab reaches the notices button and the version.
- **One window:** choosing About again brings the open window forward.

## Look and platform

- **Capitalization:**
  - Labels that name something (menus and their items, buttons, checkboxes, tabs, column and section headings, list entries, view names, window and dialog titles) are in Title Case.
  - Short function words stay lowercase unless they come first: a, an, the, and, or, of, in, on, for, to, as, by, at, with, from ("Written by", "Analyze as…").
  - Values, messages, hover text, descriptions, status-bar text and captions are in sentence case.
  - Text from the analysis (node names, values, findings, article titles) and product or spec names keep their own spelling.
  - A trailing ellipsis (…) marks a command that asks for more input.
  - `CapitalizationTests` walks the menus, buttons, checkboxes and tabs of the main and detail windows.
- Light and dark mode, the accent color and the UI font follow the system.
- The file dialogs are the system's (on Linux, the desktop portal's; see "Linux: X11 and Wayland").
- On macOS the menus are in the menu bar; the About item is in the application menu.
- Linux runs on X11 by default, which on a Wayland desktop means XWayland. Native Wayland is experimental and opt-in. See "Linux: X11 and Wayland".
- The GUI is built on Avalonia. Its dependencies and their licenses are listed in [third_party/README.md](../third_party/README.md).
- The graphs are drawn by the GUI itself, without a charting library. The accent color follows the system, but the graph palette of the category charts is fixed (see "Category series"), so a category keeps its color in light and dark mode.

## Linux: X11 and Wayland

- **Default:** X11. On a Wayland desktop that runs through XWayland. Setting or unsetting `WAYLAND_DISPLAY` does not change the choice; only `ENCSPECT_GUI_BACKEND` does.
- **Experimental:** Avalonia 12.1 calls its native Wayland backend (`Avalonia.Wayland`) experimental, so it is opt-in: `ENCSPECT_GUI_BACKEND=wayland encspect-gui`. Some paths could not be run here (see below), and the file dialogs differ.
- **`ENCSPECT_GUI_BACKEND`:** `x11` is the default, spelled out. `wayland` uses native Wayland. Any other non-empty value prints a warning naming the accepted values to stderr, and the default is used.
- **Without its display:** a backend that cannot connect (`wayland` without a compositor, `x11` without an X server) ends with one line on stderr naming `ENCSPECT_GUI_BACKEND`, `WAYLAND_DISPLAY` and `DISPLAY`, and exit code 1.
- **Which compositor:** with `wayland`, libwayland connects to `$WAYLAND_DISPLAY`, and when that is unset to `wayland-0` in `$XDG_RUNTIME_DIR`.
- **Libraries:** both backends are managed code over the system's own libraries, and the GUI ships no native library for them.
  - X11: libfontconfig (which Skia's library links, on both backends), libX11, libXrandr, libXi, libXext, libXfixes, libXcursor, libSM and libICE; libgtk-3 and libgdk-3 for the GTK file dialog when no portal answers.
  - Wayland: libfontconfig, libwayland-client, libwayland-cursor, libwayland-egl, libxkbcommon, libgbm and libdrm, with EGL for drawing; glib only if `UseGLibMainLoop` is set, which the GUI does not do.
- **Which one runs:** `encspect-gui --self-check` starts the chosen backend without showing a window and prints one of these:
  - `Windowing backend: X11.` or `Windowing backend: Wayland.`
  - `Windowing backend: unknown.` when the backend started but reported no screen within 2.5 seconds.
  - `Windowing backend: failed to start: <reason>` with exit code 1, when the display is set but the backend cannot start.
  - `Windowing backend: not started, <reason>` with exit code 0, when the chosen backend's display is missing: no `DISPLAY` for X11, no `WAYLAND_DISPLAY` and no `wayland-0` in `XDG_RUNTIME_DIR` for Wayland. A compositor alone therefore leaves the default X11 not started.

### Making Wayland the default

- It is one constant, `WaylandIsDefault` in `src/Encspect.Gui/PlatformBackend.cs`. Set to true, an unset choice starts Wayland where a compositor is reachable and falls back to X11 otherwise.
- The CI step and this section assert and describe the default; change them with it.

### File dialogs

- **X11:** the system dialog through the XDG desktop portal, then GTK, then Avalonia's own managed dialog.
- **Wayland:** the portal, then Avalonia's managed dialog. There is no GTK step.
- **Portal requirements:** `xdg-desktop-portal` with a GTK, GNOME or KDE backend, and a compositor that offers `xdg-foreign` v2 (`zxdg_exporter_v2`), which lets the dialog attach to the window. Without it the portal path is skipped and the managed dialog appears, which looks like Avalonia's, not like the system's.

### What differs on Wayland

- **Detail window position:** Wayland does not let a program place its windows. The detail window comes back in the size it had, and the compositor chooses where it appears. It is not centered over the main window, and windows opened with Open in New Window are not cascaded. The saved position is left as it was, so X11 sessions on the same account still find it; a first session on Wayland saves none, and a later X11 session centers the window.
- **Maximized state:** it is restored, like the size.
- **Window decorations:** where the compositor does not offer server-side decorations (`xdg-decoration`), the backend has the window draw its own title bar and borders.

### What was run, and what was not

- **Run:** the self-check on Weston 13 headless and on Xvfb, and the published GUI failing to start without its display. The tests for a window that cannot be placed (the saved position survives, size and maximized state return, no cascade) run in the headless GUI tests by overriding the placement capability; they do not run on a real Wayland window, and the compositor's handling of positions (a client cannot set one) comes from the protocol, not from a run here.
- **Not run:** these need a desktop session with a portal service and a file manager, which CI's headless Weston lacks.
  - File and folder dialogs, from the backend's API.
  - Drag and drop and the clipboard over the Wayland data device (`wl_data_device`). Dropping files from a file manager arrives as `text/uri-list`; Copy Path puts plain text on the clipboard.
  - Light and dark mode and the accent color, expected to follow the system through the same FreeDesktop portal settings as on X11.
