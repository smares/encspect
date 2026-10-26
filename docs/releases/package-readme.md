# Encspect

An audio file analyzer. This folder holds:

- `encspect`: the command line tool (`encspect.exe` on Windows). `encspect --help` lists the options, `encspect --version` the version.
- `encspect-gui`: the GUI (`encspect-gui.exe` on Windows). It opens files and folders named on its command line, or from its File menu.
- `LICENSE`: the MIT License of Encspect.
- `THIRD-PARTY-NOTICES.md`: the licenses of the libraries inside the two programs.

`encspect` is a native executable and `encspect-gui` is a single file that carries the .NET runtime; neither needs .NET installed. On x64, `encspect` needs a processor with SSE4.2 and POPCNT (x86-64-v2, any x64 processor since about 2009) and does not start without them.

## Which download

The command line tool comes in two builds on x64 (Windows, Linux and Intel Mac), in archives named `encspect-<version>-<platform>` and `encspect-<version>-<platform>-v3`:

- **`-v3`:** for processors with AVX2: Intel Haswell (2013) and later Core and Xeon, AMD Excavator (2015) and Zen. It is faster on some work (about a quarter to a third on folders of MPEG audio and DTS files, 14% on a folder of FLAC files, 19% on TrueHD decoding) and the same on the rest. On a processor without AVX2 it does not start and prints "The required instruction sets are not supported by the current CPU."
- **Without `-v3`:** runs on every x64 processor from about 2009 on (SSE4.2 and POPCNT), including many Intel Celeron and Pentium models sold up to about 2021, which have no AVX2.
- **Check:** Linux, `grep -m1 -o avx2 /proc/cpuinfo`; Mac, `sysctl -n machdep.cpu.leaf7_features machdep.cpu.features | grep -o AVX2`; Windows, `coreinfo | findstr AVX2` (Sysinternals) or the instruction list in CPU-Z. Arm builds (Windows, Linux, Apple silicon) have one archive.
- The GUI in both archives is the same.

## First start

The programs are not signed.

- **Windows:** SmartScreen asks; choose "More info", then "Run anyway". With Smart App Control on (Windows 11), unsigned programs are blocked without that option; the release notes say more.
- **macOS:** try to open the program, then open System Settings, Privacy & Security, and click "Open Anyway" (available for about an hour), and confirm with your password. Or, in Terminal, clear the quarantine of this folder: `xattr -dr com.apple.quarantine <this folder>`.
- **Linux:** glibc 2.27 or later (not musl). The GUI needs libfontconfig and the system's X11 libraries (Wayland ones for `ENCSPECT_GUI_BACKEND=wayland`); the release notes list them.

## More

- Release notes, documentation and source: https://github.com/smares/encspect
