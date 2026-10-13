#!/usr/bin/env python3
"""Downloads the Advanced Systems Format (ASF) Specification, revision 01.20.03 (Microsoft, December
2004), pinned by its SHA-256, into a directory outside the repository, which may not hold it:

    python3 scripts/fixtures/asf_specs.py [directory]

It is a Word document (ASF_Specification.doc). The section numbers in the ASF parser's
descriptions and articles are its.
"""

import pathlib
import subprocess
import sys

import downloads

NAME = "ASF_Specification.doc"
SHA256 = "88370623aa0b5347e7a6e1ea86c7b1c766bd79daf1a37ebf8560a5657665797b"
URL = "https://download.microsoft.com/download/7/9/0/790fecaa-f64a-4a5e-a430-0bccdab3f1b4/ASF_Specification.doc"
ADDRESSES = [URL, downloads.archived(URL, "2024")]
AGENT = "Mozilla/5.0 (X11; Linux x86_64; rv:128.0) Gecko/20100101 Firefox/128.0"


def fetch(directory):
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    original = downloads.download

    def as_browser(url, path):
        result = subprocess.run(["curl", "-fsSL", "--retry", "5", "--retry-delay", "15", "--connect-timeout", "30", "--speed-limit", "1024",
                                 "--speed-time", "60", "--max-time", "900", "-A", AGENT, "-o", str(path), url], capture_output=True, text=True)
        return None if result.returncode == 0 else result.stderr.strip() or f"curl exit {result.returncode}"

    downloads.download = as_browser
    try:
        downloads.fetch(ADDRESSES, SHA256, directory / NAME)
    finally:
        downloads.download = original
    return directory / NAME


def main():
    path = fetch(sys.argv[1] if len(sys.argv) > 1 else "asf-specs")
    print(f"{path}: SHA-256 {SHA256}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
