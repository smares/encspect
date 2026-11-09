"""The OptimFROG encoders the fixtures and checks are made with: OptimFROG 5.100 for Linux x64 (SDK 1.600) by Florin Ghido, pinned by SHA-256.

- The package holds `ofr` (lossless), `off` (IEEE float), `ofs` (DualStream), the SDK (`SDK/Library/libOptimFROG.so.0`, `SDK/OptimFROG/*.h`,
  `SDK/Documentation/format.txt`, the author's specification of the compressed file format) and `license.html`: "completely free for personal, and
  also for non-commercial and commercial use"; the unmodified release packages may be redistributed under their original names. The author
  asks that the packages are not linked directly but through the download page, http://losslessaudio.org/Downloads.php (HTTPS has a
  self-signed certificate), which is the address docs and provenance files name. The package's own address is here only.
- The binaries are statically linked and need no build. They run in CI only (.github/workflows/optimfrog-fixtures.yml and the ci.yml
  cross-check job), as black boxes: their output is read with format.txt, no code is read.
- Addresses: the author's site (HTTP only), the Wayback Machine's capture of 2022-08-13 and the copy in the project's private repository
  (encoders/, which the license allows).

Usage: python3 scripts/fixtures/optimfrog_tool.py [directory]    extracts the package to the directory and prints the folder.
"""

import pathlib
import sys
import tempfile
import zipfile

import downloads

NAME = "OptimFROG_Linux_x64_5100.zip"
SHA256 = "d45079f8121918acda221c062943facca9bddb8680e5055508add077d69751f4"
ORIGINAL = "http://losslessaudio.org/Downloads/A1C0/5100/Linux/OptimFROG_Linux_x64_5100.zip"
ADDRESSES = [ORIGINAL, downloads.archived(ORIGINAL, "20220813"), downloads.private("encoders/" + NAME)]
VERSION = "5.100"
DOWNLOAD_PAGE = "http://losslessaudio.org/Downloads.php"


def sources():
    """(name, SHA-256, addresses) of every file this script downloads."""
    yield NAME, SHA256, ADDRESSES


def fetch(directory):
    """The path of the archive in directory, downloaded and checked."""
    path = pathlib.Path(directory) / NAME
    downloads.fetch(ADDRESSES, SHA256, path)
    return path


def extract(directory=None):
    """The folder the package is extracted to inside directory (a new temporary one by default), with the archive's names checked to stay inside it."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="optimfrog-"))
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / f"optimfrog-{VERSION}"
    if not target.exists():
        with zipfile.ZipFile(fetch(directory)) as archive:
            for member in archive.namelist():
                if not (target / member).resolve().is_relative_to(target.resolve()):
                    raise ValueError(f"{member} is outside the package folder")
            archive.extractall(target)
    package = next((p for p in target.iterdir() if p.is_dir()), target)
    for name in ("ofr", "off", "ofs"):
        (package / name).chmod(0o755)
    return package


def library(package):
    """The path of the SDK library in the extracted package."""
    return pathlib.Path(package) / "SDK" / "Library" / "libOptimFROG.so.0"


def main():
    print(extract(sys.argv[1] if len(sys.argv) > 1 else None))


if __name__ == "__main__":
    main()
