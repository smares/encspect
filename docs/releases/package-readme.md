# Encspect

An audio file analyzer. This folder holds:

- `encspect`: the command line tool (`encspect.exe` on Windows). `encspect --help` lists the options, `encspect --version` the version.
- `encspect-gui`: the GUI (`encspect-gui.exe` on Windows). It opens files and folders named on its command line, or from its File menu.
- `LICENSE`: the MIT License of Encspect.
- `THIRD-PARTY-NOTICES.md`: the licenses of the libraries inside the two programs.

`encspect` is a native executable and `encspect-gui` is a single file that carries the .NET runtime; neither needs .NET installed. On x64, `encspect` needs a processor with SSSE3, SSE4.1, SSE4.2 and POPCNT (x86-64-v2: Intel Core since Nehalem, 2008, AMD since Bulldozer, 2011, and Jaguar, 2013; not AMD Phenom II, Athlon II or Bobcat, or Intel Atom before Silvermont) and does not start without them.

## Which download

The command line tool comes in two builds on x64 (Windows, Linux and Intel Mac), in archives named `encspect-<version>-<platform>` and `encspect-<version>-<platform>-v3`:

- **Which one works on your computer:** extract the `-v3` archive and run `encspect --version`. If it prints the version, use it; if it prints "The required instruction sets are not supported by the current CPU." and stops, take the other archive. This answers right on every system.
- **`-v3`:** needs AVX2, FMA and BMI: Intel Core since Haswell (2013) and Xeon of the same age, AMD Excavator (2015) and Zen. Not the Intel Pentium, Celeron and Atom models sold without AVX2, many of them up to about 2021. It is faster on some work (about a quarter to a third on folders of MPEG audio and DTS files, 14% on a folder of FLAC files; TrueHD decoding varies) and the same on the rest.
- **Without `-v3`:** runs where the processor has SSSE3, SSE4.1, SSE4.2 and POPCNT (Intel Core since Nehalem, 2008, AMD since Bulldozer, 2011, and Jaguar, 2013; not AMD Phenom II, Athlon II or Bobcat, or Intel Atom before Silvermont), including the Celeron and Pentium models without AVX2.
- **Other ways to look:** Linux, `grep -m1 -o avx2 /proc/cpuinfo`; Mac, `sysctl -n machdep.cpu.leaf7_features machdep.cpu.features | grep -o AVX2`; Windows, `coreinfo -accepteula -f` from Sysinternals (a `*` next to AVX2 means present, a `-` absent) or the instruction list in CPU-Z. The Arm builds (Windows, Linux, Apple silicon) have one archive.
- The GUI in both archives is the same.

## First start

The programs are not signed.

- **Windows:** SmartScreen asks; choose "More info", then "Run anyway". With Smart App Control on (Windows 11), unsigned programs are blocked without that option; the release notes say more.
- **macOS:** try to open the program, then open System Settings, Privacy & Security, and click "Open Anyway" (available for about an hour), and confirm with your password. Or, in Terminal, clear the quarantine of this folder: `xattr -dr com.apple.quarantine <this folder>`.
- **Linux:** glibc 2.27 or later (not musl). The GUI needs libfontconfig and the system's X11 libraries (Wayland ones for `ENCSPECT_GUI_BACKEND=wayland`); the release notes list them.

## More

- Release notes, documentation and source: https://github.com/smares/encspect
