# Third-party sources

Tools the fixture scripts build to cross-check the analyzer. They are committed rather than downloaded or installed, so the checks keep working when the original downloads disappear. The analyzer does not use them.

| Directory | What | Origin | License |
|---|---|---|---|
| `minimp3` | `minimp3.h`, a single-header MP3 decoder by lieff | [lieff/minimp3](https://github.com/lieff/minimp3) at commit `ea99364f61c14656440e8d77e9c233ccf3124633`, SHA-256 `57e437c5c1f0e8b243885d3929c8973b5e6c778451e0100ab4251d19915cb3ad` | CC0 1.0 (`minimp3/LICENSE`) |
| `getid3` | getID3 by James Heinrich: `getid3.php`, `getid3.lib.php`, `module.audio.mp3.php` and the ID3v1, ID3v2, APE and Lyrics3 tag modules, unchanged | [JamesHeinrich/getID3](https://github.com/JamesHeinrich/getID3) at commit `9ee5678f1cfb68f6ccc9efc08511c61cbdb78b5a` | Used under the LGPL 3.0, one of the licenses it offers (`getid3/license.txt`, `getid3/licenses/`) |
| `encspot` | EncSpot Console 2.01 by GuerillaSoft, as ported to Linux by Francis Russell (CMake build, buffer overflow fixes, the scalefactor regression fix of 2011-03-07), unchanged | `encspot-2.01+mtn20110729.tar.bz2` from [unchartedbackwaters.co.uk](https://www.unchartedbackwaters.co.uk/pyblosxom/static/encspot) (`https://www.unchartedbackwaters.co.uk/files/encspot/encspot-2.01+mtn20110729.tar.bz2`), SHA-256 `6b1be223393f94b5646f26c9da34c8b6addc2b0c64244b7e976a22b43a252014`. `debian/` is the port's own packaging, with its copyright file. | BSD 3-clause ("New BSD", `encspot/LICENSE`) |
| `mp3guessenc` | mp3guessenc 0.27.6 beta by Elio Blanca and others | The Ubuntu and Debian source package `0.27.6~beta+dfsg-1`: `mp3guessenc_0.27.6~beta+dfsg.orig.tar.gz` (SHA-256 `e6da8a7b629dcacdf9770592bacf3017e6aece68a3cca60d887bcde3d7b786a4`) with the patches of `mp3guessenc_0.27.6~beta+dfsg-1.debian.tar.xz` (SHA-256 `3056e3b7bdb7bd2dea934d6f5229a36b514b644b2a8dbcab3ac2c4ad141cf43a`) applied, which only change the Makefile and drop the build date from `-V`. `debian/` keeps those patches and Debian's copyright file. | LGPL 2.1 or later (`mp3guessenc/COPYING`) |

`scripts/fixtures/minimp3_tool.py`, `scripts/fixtures/guessenc_tool.py` and `scripts/fixtures/encspot_tool.py` build them; `scripts/fixtures/getid3_tool.py` runs getID3 with PHP. Change nothing in these directories except to update a tool, and then update this table.

# GUI dependencies

The GUI (`src/Encspect.Gui`) uses Avalonia from NuGet, at the exact versions in `Directory.Packages.props`. Every package it brings in is free and open source; Avalonia's TreeDataGrid, which moved to a commercial license, is not used. Skia and HarfBuzz are native libraries that Avalonia draws and shapes text with; they are the one kind of native library the project ships (see the plan's non-goals).

| Package | Version | What | License |
|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.4 | The UI framework, its desktop platforms and the Fluent theme | MIT |
| Avalonia.Controls.DataGrid | 12.1.2 | The file list | MIT |
| Avalonia.Native, Avalonia.Win32, Avalonia.X11, Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi, Avalonia.Skia, Avalonia.HarfBuzz, Avalonia.Remote.Protocol | 12.1.4 | Platform backends, drawing and text shaping (pulled in by Avalonia.Desktop) | MIT |
| Avalonia.BuildServices | 11.3.2 | Avalonia's build-time usage telemetry; it runs during builds only, not in the app, and `AVALONIA_TELEMETRY_OPTOUT=1` turns it off | MIT |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | ANGLE, OpenGL on Direct3D for Windows | BSD 3-Clause |
| SkiaSharp and SkiaSharp.NativeAssets.* | 3.119.4 | Skia, the 2D graphics library | MIT (Skia itself BSD 3-Clause) |
| HarfBuzzSharp and HarfBuzzSharp.NativeAssets.* | 8.3.1.3 | HarfBuzz, text shaping | MIT (HarfBuzz itself Old MIT) |
| MicroCom.Runtime | 0.11.6 | COM-style interop for the macOS backend | MIT |
| Tmds.DBus.Protocol | 0.94.1 | D-Bus for the Linux backend | MIT |

The GUI tests (`tests/Encspect.Gui.Tests`) add Avalonia.Headless.XUnit, Avalonia.Headless and Avalonia.Fonts.Inter 12.1.4 (MIT), and xUnit v3 3.2.2 with its packages (Apache 2.0), which the headless test support requires; the other tests stay on xUnit 2. None of these ship with the GUI.
