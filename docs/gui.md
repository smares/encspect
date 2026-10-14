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
- tags;
- findings by severity.

Columns sort by the figures behind them, so durations and bitrates sort as numbers.

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
  - the streams at the top, each with its channel layout and speakers;
  - the structure tree, whose rows are made only as they are shown and whose children are read only when expanded, so a stream of 100,000 frames opens at once;
  - a hex view read from the file as it scrolls. Selecting a node marks its bytes, all its ranges where they are not contiguous, and, for a field of a few bits, the bits themselves, also in the bit strip below.
- **Explanations:**
  - Every row's tooltip says what the field is and what its value means.
  - The panel under the hex view shows that for the selected node, with a "Learn more" link to a background article on the concept behind it, such as mid/side stereo or the bit reservoir.
  - Articles open in a side pane, closed by default, whose links lead to related articles. Topics (F1) opens it on the selected node's article, or on the list of all of them.
  - The column headers of the file list, the streams line and the graphs' choices explain what they show too.
- **Findings:** can be filtered by severity. Selecting a finding selects its node.
- **Export:** Cmd or Ctrl+S writes the report as text with offsets or as JSON, as `--offsets` and `--json` would.

## Graphs

The Graphs tab, next to the findings, draws the stream's series: the bitrate and, for Layer III, the bit reservoir fill, per frame and per second of audio, and the distribution of the bitrates. A file of several streams has a choice of stream.

- **Hovering** a point shows its value.
- **Clicking** a point selects its frame in the tree and hex view. A bar of the distribution selects the next frame of that bitrate, and a point of a per-second graph the first frame of that second.
- **Selecting** a frame in the tree marks it on the graph.
- **Speed:** graphs of 100,000 frames draw as quickly as short ones, since a line is drawn as the smallest and largest value of each pixel column.
- **Waveform and spectrogram:** these need decoded audio, and no format has a decoder yet, which the tab says in one line.

## Look and platform

- Light and dark mode, the accent color and the UI font follow the system.
- The file dialogs are the system's.
- On macOS the menus are in the menu bar.
- Linux runs natively on Wayland when a compositor is reachable (`WAYLAND_DISPLAY`) and on X11 otherwise, which includes XWayland. See "Linux: Wayland and X11".
- The GUI is built on Avalonia. Its dependencies and their licenses are listed in [third_party/README.md](../third_party/README.md).
- The graphs are drawn by the GUI itself, without a charting library.

## Linux: Wayland and X11

- **Choice:** the GUI starts on Wayland when a compositor is reachable and otherwise on X11, which also covers XWayland sessions.
- **Experimental:** Avalonia 12.1 calls its Wayland backend (`Avalonia.Wayland`) experimental. If a window misbehaves on your desktop, force X11.
- **`ENCSPECT_GUI_BACKEND`:** `x11` forces X11 (through XWayland in a Wayland session), `wayland` forces Wayland and fails to start without a compositor, anything else or unset chooses as above.
  ```sh
  ENCSPECT_GUI_BACKEND=x11 encspect-gui
  ```
- **Libraries:** both backends are managed code over the system's own libraries (libwayland-client and EGL, or the X11 libraries); the GUI ships no native library for them.
- **Which one runs:** `encspect-gui --self-check` prints `Windowing backend: Wayland.` or `Windowing backend: X11.` when `WAYLAND_DISPLAY` or `DISPLAY` is set, and says it did not start one otherwise.

### What differs on Wayland

- **Detail window position:** Wayland does not let a program place its windows. The detail window comes back in the size it had, and the compositor chooses where it appears. It is not centered over the main window, and windows opened with Open in New Window are not cascaded. The saved position is left as it was, so X11 sessions on the same account still find it.
- **Maximized state:** it is restored, like the size.
- **Window decorations:** where the compositor does not offer server-side decorations (`xdg-decoration`), the backend has the window draw its own title bar and borders.

### From the backend's API, not run here

- **File and folder dialogs:** Avalonia.Wayland builds on Avalonia.FreeDesktop, whose storage provider uses the XDG desktop portal, so a portal service (`xdg-desktop-portal` with a GTK, GNOME or KDE backend) is needed.
- **Drag and drop, and the clipboard:** the backend implements both over the Wayland data device (`wl_data_device`), including files offered as a `text/uri-list`, which is what dropping files from a file manager and Copy Path use.
- **Light and dark mode and the accent color:** these are expected to follow the system through the same FreeDesktop portal settings as on X11.
- **Why not run:** these need a desktop session with a portal service and a file manager. CI uses a headless Weston without them. What was run: the self-check on Weston 13 headless and on Xvfb, the published GUI starting on Weston, and the window position behavior above.

