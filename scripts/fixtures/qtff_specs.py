#!/usr/bin/env python3
"""Downloads the specification the QuickTime sample entries of the MP4 parser are read against,
pinned by its SHA-256, into a directory outside the repository, which may not hold it:

- Apple, QuickTime File Format Specification (2001-03-01): the sample description atoms (chapter 2), the sound sample description
  of versions 0, 1 and 2 and the uncompressed sound formats (chapter 3, "Sound Media").

    python3 scripts/fixtures/qtff_specs.py [directory]

Its text says that in version 0 a sound sample is an uncompressed sample and that the sample tables "documented uncompressed samples"; it does not
say what size the sample size atom states for such a sample (the size of a sample frame, as ffmpeg writes, or 1).
"""

import pathlib
import sys

import downloads

NAME = "qtff-2001.pdf"
SHA256 = "fbd57904da54174e86df1aca03b3099af45166690f5eb9a59543d8de7c1649a3"
ADDRESSES = ["https://developer.apple.com/standards/qtff-2001.pdf"]


def fetch(directory):
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    downloads.fetch(ADDRESSES, SHA256, directory / NAME)
    return directory / NAME


def main():
    path = fetch(sys.argv[1] if len(sys.argv) > 1 else "qtff-specs")
    print(f"{path}: SHA-256 {SHA256}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
