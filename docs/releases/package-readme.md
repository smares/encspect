# Encspect

An audio file analyzer. This folder holds:

- `encspect`: the command line tool (`encspect.exe` on Windows). `encspect --help` lists the options, `encspect --version` the version.
- `encspect-gui`: the GUI (`encspect-gui.exe` on Windows). It opens files and folders named on its command line, or from its File menu.
- `LICENSE`: the MIT License of Encspect.
- `THIRD-PARTY-NOTICES.md`: the licenses of the libraries inside the two programs.

Both programs are single files that carry the .NET runtime.

## First start

The programs are not signed.

- **Windows:** SmartScreen asks; choose "More info", then "Run anyway".
- **macOS:** clear the quarantine of the download, once, for this folder: `xattr -dr com.apple.quarantine <this folder>`. Or Control-click the program in Finder, choose Open, and confirm.
- **Linux:** the GUI needs the system's X11 libraries (Wayland ones for `ENCSPECT_GUI_BACKEND=wayland`); the release notes list them.

## More

- Release notes, documentation and source: https://github.com/smares/encspect
