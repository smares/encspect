#!/usr/bin/env python3
"""Downloads the specifications the Matroska parser, its descriptions and its articles are written from,
pinned by their SHA-256, into a directory outside the repository, which may not hold them:

- RFC 9559, Matroska Media Container Format Specifications (October 2024): the element definitions
  (section 5), the block structure and lacing (section 10), timestamps (section 11), content encodings
  (section 5.1.4.1.31) and livestreaming (section 23).
- RFC 8794, Extensible Binary Meta Language (July 2020): variable-size integers (section 4), element IDs
  and sizes (sections 5 and 6, including unknown sizes), the EBML header (section 11.2), the CRC-32 and Void
  elements (section 11.3).
- The Matroska element registry, ebml_matroska.xml of github.com/ietf-wg-cellar/matroska-specification, pinned by the commit
  1a943ead9bb4c67b02b966cebafe51c8c074e703 (and the file's SHA-256): the elements RFC 9559 does not list that muxers write (the
  hidden flag of an edition, the enabled flag of a chapter, the chapter's tracks, the minimum-version-5 elements), and the default
  value of every element (RFC 8794, section 6.1: an empty element has it).
- draft-ietf-cellar-codec-20, Matroska Codec Mapping (August 2026): the codec IDs and what each stores in
  CodecPrivate and in the frames (section 3.4 for audio). It is the registry of the codec IDs the parser
  routes by.

    python3 scripts/fixtures/matroska_specs.py [directory]

The section numbers in the Matroska parser's descriptions and articles are the RFCs'. The element table in
src/Encspect.Formats.Matroska/Elements.cs is generated from RFC 9559 and RFC 8794 by matroska_tables.py, which
checks it. The WebM Container Guidelines (https://www.webmproject.org/docs/container/) are a web page and cannot
be pinned; the parser cites them by address.
"""

import pathlib
import sys

import downloads

SPECS = [
    ("rfc9559.txt", "f251c1e9b6879bb308000fbd9b2359de9472160c56d121b9345b6433afb00cb7",
     ["https://www.rfc-editor.org/rfc/rfc9559.txt", "https://www.ietf.org/rfc/rfc9559.txt"]),
    ("rfc8794.txt", "3b39e9f494f040e3b7b91d9bbe2441af5f4b4e2ae4a374b8dcf917ab9aecd409",
     ["https://www.rfc-editor.org/rfc/rfc8794.txt", "https://www.ietf.org/rfc/rfc8794.txt"]),
    ("ebml_matroska.xml", "a42a9c6ea47dfd83589ce04a508b6a7f5d038560cee11dee1e4cbe2e746d0939",
     ["https://raw.githubusercontent.com/ietf-wg-cellar/matroska-specification/1a943ead9bb4c67b02b966cebafe51c8c074e703/ebml_matroska.xml"]),
    ("draft-ietf-cellar-codec-20.txt", "7d4bf97dff8c126183c66dfe9d7983065fc44493efe5f45ddb16a74e244d60aa",
     ["https://www.ietf.org/archive/id/draft-ietf-cellar-codec-20.txt"]),
]


def fetch(directory):
    """Returns the directory that holds the files."""
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    for name, digest, addresses in SPECS:
        downloads.fetch(addresses, digest, directory / name)
    return directory


def main():
    directory = fetch(sys.argv[1] if len(sys.argv) > 1 else "matroska-specs")
    for name, digest, _ in SPECS:
        print(f"{directory / name}: SHA-256 {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
