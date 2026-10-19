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
  - Every column is at least as wide as its header text, padding and sort glyph, measured at run time with the font in use, so no header is cut off or collapses, whatever the system font.
  - The findings column also holds three counts of up to three digits, each after its dot, left-aligned.
  - Name takes the width that is left, down to a minimum.
  - The other columns start at a set width, or at the width their header needs where that is more, and can be widened, not made narrower than that.
  - Where the columns together are wider than the window, the list scrolls horizontally instead of squeezing them.

- **Opening files:**
  - Choose a folder in the tree on the left: home, music, desktop and the volumes, with subfolders listed when expanded.
  - File, Open Folder (Cmd+O on macOS, Ctrl+O elsewhere).
  - Drop files and folders onto the window.
  - Files and folders named on the command line open the same way.
- **Analysis:**
  - Files are analyzed in the background, a few at a time, and rows fill in as they are done. A list sorted by a column sorts again as they do, keeping the selected row.
  - Stop cancels, and choosing another folder cancels the run before it.
  - "Include subfolders" walks the whole tree as the CLI does.
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
  - The panel under the hex view shows that for the selected node, with a "Learn more" link to a background article on the concept behind it, such as mid/side stereo or the bit reservoir.
  - Articles open in a side pane, closed by default, whose links lead to related articles. Topics (F1) opens it on the selected node's article, or on the list of all of them.
  - The column headers of the file list, the streams line and the graphs' choices explain what they show too.
- **Format:**
  - Where other formats also match with real evidence, the header says "Also matches:" and offers one button per format (its tooltip gives the evidence). Choosing one analyzes the file again as that format.
  - "Analyze as…" in the header lists every format, and "Detect automatically", for a file whose format is not detected or detected wrongly. On macOS the same list is File, Analyze As.
  - A forced analysis says so in the header, with the format detection would have chosen. The choice stays while the window shows the file, through a refresh, and is dropped when another file is shown.
  - Forced analyses happen only in the detail window, which never uses the summary cache, so a forced result never reaches the file list or the cache.
- **Findings:** can be filtered by severity. Selecting a finding selects its node.
- **Export:** Cmd or Ctrl+S writes the report as text with offsets or as JSON, as `--offsets` and `--json` would.

## Graphs

The Graphs tab, next to the findings, draws the stream's series: the bitrate and, for Layer III, the bit reservoir fill, per frame and per second of audio, and the distribution of the bitrates. A file of several streams has a choice of stream.

- **Hovering** a point shows its value.
- **Clicking** a point selects its frame in the tree and hex view. A bar of the distribution selects the next frame of that bitrate, and a point of a per-second graph the first frame of that second.
- **Selecting** a frame in the tree marks it on the graph.
- **Speed:** graphs of 100,000 frames draw as quickly as short ones, since a line is drawn as the smallest and largest value of each pixel column.
- **Waveform and spectrogram:** these need decoded audio, and no format has a decoder yet, which the tab says in one line.

## About

"About Encspect" shows what build this is.

- **Where:** on macOS in the application menu, as usual there; on Windows and Linux in the Help menu, the menu bar's last menu.
- **Shows:** the name, the version, one line on what Encspect does, the copyright and the MIT License.
- **Version:** the same string as `encspect --version` prints after the program name and as `tool.version` in JSON reports (`0.1.0+<commit>`), read from the same place in the host. It can be selected and copied for a bug report.
- **Third-Party Notices:** the button opens a window with the text of `THIRD-PARTY-NOTICES.md`: the licenses of Avalonia, Skia, HarfBuzz, ANGLE and the .NET runtime. The text is embedded in the executable, so it is there in a single-file build too and is the same file the download carries next to the executables.
- **Keyboard:** Esc and Enter close the window; the Close button has the focus when it opens. Tab reaches the notices button and the version.
- **One window:** choosing About again brings the open window forward.

## Look and platform

- Light and dark mode, the accent color and the UI font follow the system.
- The file dialogs are the system's (on Linux, the desktop portal's; see "Linux: X11 and Wayland").
- On macOS the menus are in the menu bar; the About item is in the application menu.
- Linux runs on X11 by default, which on a Wayland desktop means XWayland. Native Wayland is experimental and opt-in. See "Linux: X11 and Wayland".
- The GUI is built on Avalonia. Its dependencies and their licenses are listed in [third_party/README.md](../third_party/README.md).
- The graphs are drawn by the GUI itself, without a charting library.

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
