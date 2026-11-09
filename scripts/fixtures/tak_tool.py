"""TAK 2.3.3, the archive the TAK fixtures are made with: Thomas Becker's "Tom's lossless Audio Kompressor" for Windows, pinned by SHA-256.

- The archive holds the GUI (`Applications/Tak.exe`), the command-line encoder (`Applications/Takc.exe`), the decoder library
  (`Deco_Lib/x64/tak_deco_lib.dll` and `x86/`), the SDK (`Sdk/Header/*`, `Sdk/Doc/*.html`, and `Sdk/Source/takStream.pas`, the author's
  source of the container) and the Winamp plugin. The applications, the library and the SDK are "FREEWARE, but only for private,
  non-commercial use", also free for educational and scientific use; commercial use needs the author's written approval; the intact
  archive may be redistributed without a fee. takStream.pas has its own terms: "Do with it, whatever you want", asking that the module
  is not distributed without the SDK and that a modified version says it is not an official release. The archive is therefore never
  committed: it is fetched into the download cache (or the runner's temporary folder), and only the files Takc.exe writes are committed.
- Takc.exe and the library run on a Windows CI runner only (.github/workflows/tak-fixtures.yml), as black boxes, and never on a developer
  machine or a Linux runner. takStream.pas is read by the parser's author (docs/design/tak-optimfrog-headers.md, "Sources and licenses").
- Addresses: the author's site (HTTP only; the HTTPS address fails with a TLS alert), the Wayback Machine's capture of 2026-10-14 and the
  copy in the project's private repository (encoders/, which the license allows).
- Author's page: http://www.thbeck.de/Tak/Tak.html (the docs link this, not the archive).

Usage: python3 scripts/fixtures/tak_tool.py [directory]    extracts the archive to the directory and prints the folder.
"""

import pathlib
import sys
import tempfile
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import downloads  # noqa: E402

NAME = "TAK_2.3.3.zip"
SHA256 = "40d26992266f1377d576b88b84b88d2ab256dc321e47f90314402a80692aba6a"
SIZE = 1765343
ORIGINAL = "http://www.thbeck.de/Download/TAK_2.3.3.zip"
ADDRESSES = [ORIGINAL, downloads.archived(ORIGINAL, "20261014"), downloads.private("encoders/" + NAME)]
VERSION = "2.3.3"
AUTHOR_PAGE = "http://www.thbeck.de/Tak/Tak.html"

TAKC = "Applications/Takc.exe"
LIBRARY_X64 = "Deco_Lib/x64/tak_deco_lib.dll"
STREAM_SOURCE = "Sdk/Source/takStream.pas"


def sources():
    """(name, SHA-256, addresses) of every file this script downloads."""
    yield NAME, SHA256, ADDRESSES


def fetch(directory):
    """The path of the archive in directory, downloaded and checked."""
    path = pathlib.Path(directory) / NAME
    downloads.fetch(ADDRESSES, SHA256, path)
    return path


def extract(directory=None):
    """The folder the archive is extracted to inside directory (a new temporary one by default), with the archive's names checked to stay inside it."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="tak-"))
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / f"tak-{VERSION}"
    if not target.exists():
        with zipfile.ZipFile(fetch(directory)) as archive:
            for member in archive.namelist():
                if not (target / member).resolve().is_relative_to(target.resolve()):
                    raise ValueError(f"{member} is outside the package folder")
            archive.extractall(target)
    return target


def main():
    print(extract(sys.argv[1] if len(sys.argv) > 1 else None))


if __name__ == "__main__":
    main()
