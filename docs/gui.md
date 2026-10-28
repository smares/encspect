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
  - Articles open in a side pane, closed by default, whose links lead to related articles. Topics (F1) opens it on the selected node's article, or on the list of all of them.
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

- **The list:** the graphs are a vertical list on the left of the graph area, and the chart takes the rest. The list is grouped under the same headings in every format, so an MP3 and an AAC file are compared the same way: "Summary" (the Coding Summary), "Stereo Coding", "Block Switching", "Coded Bandwidth", "Bit Distribution" and "Other". A series says which heading it belongs to (its concept); MPEG audio sets it on the bitrate, the bit reservoir fill, the coded bandwidth and the channel bit share, and the bitrate series of a format that sets none go under "Bit Distribution" by their unit (kbps), and the bit reservoir fill by its (bytes). A heading with no entry for the file is not shown.
- **Long names wrap:** an entry whose name is wider than the list (210 pixels) continues on a second line, so a name is never cut; its tooltip gives the name and then what the graph shows.
- **What opens first:** the first entry of the list, the Coding Summary where the stream has category series (every MPEG file), else the first graph. The entry chosen stays chosen, by its title, for the next file that has one. The selected entry is scrolled into view in the list, whatever selected it, and the list shows its scrollbar when it overflows, and it shows whole items only: its viewport is as high as the headings and entries that fit (the rest stays empty below it), it scrolls from the top of one item to the top of another, and where all fit it has no scrollbar.
- **One entry for each quantity,** with the forms it can be drawn in as a view switcher above the chart, remembered for the next entry that has a form of that kind:
  - a category series: "Over Time", "Smoothed" (only for a stream of more than 30 one-second intervals), "Frames" and "Summary" (see below);
  - a numeric quantity such as the bitrate: "Per Frame", "Over Time" (per second of audio) and, for the bitrate, "Distribution"; the channel bit share adds "First and Second", the two channels' shares as two segments of each interval with the 50% line marked.
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
- **Waveform and spectrogram:** these need decoded audio, and no format has a decoder yet, which the tab says in one line.

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
