# Encspect

An audio file analyzer. This folder holds:

- `encspect`: the command line tool (`encspect.exe` on Windows). `encspect --help` lists the options, `encspect --version` the version.
- `encspect-gui`: the GUI (`encspect-gui.exe` on Windows). It opens files and folders named on its command line, or from its File menu.
- `LICENSE`: the MIT License of Encspect.
- `THIRD-PARTY-NOTICES.md`: the licenses of the libraries inside the two programs.

Both programs are single files that carry the .NET runtime.

## First start

The programs are not signed.

- **Windows:** SmartScreen asks; choose "More info", then "Run anyway". With Smart App Control on (Windows 11), unsigned programs are blocked without that option; the release notes say more.
- **macOS:** try to open the program, then open System Settings, Privacy & Security, and click "Open Anyway" (available for about an hour), and confirm with your password. Or, in Terminal, clear the quarantine of this folder: `xattr -dr com.apple.quarantine <this folder>`.
- **Linux:** glibc 2.27 or later (not musl). The GUI needs libfontconfig and the system's X11 libraries (Wayland ones for `ENCSPECT_GUI_BACKEND=wayland`); the release notes list them.

## More

- Release notes, documentation and source: https://github.com/smares/encspect
