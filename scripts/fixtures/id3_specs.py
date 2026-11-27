#!/usr/bin/env python3
"""Downloads the specifications the ID3v2 parser's descriptions are written from, pinned by their
SHA-256, into a directory outside the repository, which may not hold them (id3.org, as the
documents stand there):

- ID3v2.2 (id3v2-00): the informal standard of 1998, section 4 for the frames.
- ID3v2.3.0 (id3v2.3.0): the informal standard of 1999, section 4 for the frames.
- ID3v2.4.0 changes (id3v2.4.0-changes): section 4 lists the frames ID3v2.4 deprecated and what replaced them.
- ID3v2.4.0 structure and frames (id3v2.4.0-structure, id3v2.4.0-frames): the frames are in the
  frames document's section 4; the structure document says how text frames hold several values.

    python3 scripts/fixtures/id3_specs.py [directory]
"""

import pathlib
import sys

import downloads

# id3.org has answered HTTP 500 for days at a time. The private repository holds copies of four pages as they were pinned (none of the
# changes document survived), and the Wayback Machine is the last address; each is held to the same SHA-256.
SPECS = [
    ("id3v2-00.html", "b3c2e2bc22a12f46644aa795f8fea2ea293e1b52f943785a5e85fa540eb7e882", ["https://id3.org/id3v2-00", downloads.private("mirrors/specs/id3v2/id3v2-00.html"), downloads.archived("https://id3.org/id3v2-00", "2026")]),
    ("id3v2.3.0.html", "24fb3679f6d433afeea160cead1c64f27e4b5ce6fd1a049e4583129332c16532", ["https://id3.org/id3v2.3.0", downloads.private("mirrors/specs/id3v2/id3v2.3.0.html"), downloads.archived("https://id3.org/id3v2.3.0", "2026")]),
    ("id3v2.4.0-structure.html", "1e1713b0212d9a6ff52ff6bbae84826a0cee651db8aa7d87520bdc4758a6c598", ["https://id3.org/id3v2.4.0-structure", downloads.private("mirrors/specs/id3v2/id3v2.4.0-structure.html"), downloads.archived("https://id3.org/id3v2.4.0-structure", "2026")]),
    ("id3v2.4.0-changes.html", "44438a4e65c14137379c48483cdbf09c3614ebf832906f1c37c864eb3e3cfb1b", ["https://id3.org/id3v2.4.0-changes", downloads.archived("https://id3.org/id3v2.4.0-changes", "2026")]),
    ("id3v2.4.0-frames.html", "ce9fad74c31f687fe5aeb26de4df71a88bfa8f5c4a5f2f08525c5a0f4754b4f5", ["https://id3.org/id3v2.4.0-frames", downloads.private("mirrors/specs/id3v2/id3v2.4.0-frames.html"), downloads.archived("https://id3.org/id3v2.4.0-frames", "2026")]),
]


def main():
    target = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "id3-specs")
    target.mkdir(parents=True, exist_ok=True)
    for name, digest, addresses in SPECS:
        downloads.fetch(addresses, digest, target / name)
        print(f"{target / name}: SHA-256 {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
