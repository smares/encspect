#!/usr/bin/env python3
"""Downloads DASH-IF's registry of content protection identifiers (https://dashif.org/identifiers/content_protection/), pinned by its SHA-256, and checks
that the table of ProtectionSystems (src/Encspect.Core.Abstractions/ProtectionSystems.cs) holds exactly the system IDs and names of its "Protection System
Specific Identifiers" table:

    python3 scripts/fixtures/dashif_systems.py [directory]

The pssh box of ISO/IEC 23001-7 names a DRM system by a 16-byte system ID; this registry is the public list of the IDs that systems submitted, and the
analyzer names a system only from it. The page changes when a system is added: the new SHA-256 and the new rows of the C# table then go in together.
"""

import html
import pathlib
import re
import sys

import downloads

NAME = "dashif-content-protection.html"
SHA256 = "acd094ba1619d8308548076904445fab1f2b22afd2f7ec292481c8131e698232"
URL = "https://dashif.org/identifiers/content_protection/"
# No archive address: none of the Wayback Machine's captures of the page (22 since 2023, with four different contents) has the pinned bytes. The private
# repository keeps a copy of the pinned page as the fallback.
ADDRESSES = [URL, downloads.private("registries/dashif-content-protection.html")]
TABLE = pathlib.Path(__file__).resolve().parents[2] / "src" / "Encspect.Core.Abstractions" / "ProtectionSystems.cs"


def fetch(directory):
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    downloads.fetch(ADDRESSES, SHA256, directory / NAME)
    return directory / NAME


def registry(path):
    """{system ID: name} of the registry's table of protection system specific identifiers."""
    page = path.read_text(encoding="utf-8")
    page = page[page.index("Protection System Specific Identifiers"):]
    systems = {}
    for row in re.findall(r"<tr[^>]*>(.*?)</tr>", page, flags=re.S):
        cells = [re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", cell))).strip() for cell in re.findall(r"<t[dh][^>]*>(.*?)</t[dh]>", row, flags=re.S)]
        if cells and re.fullmatch(r"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", cells[0]):
            systems[cells[0].lower()] = cells[1]
    return systems


def table():
    """{system ID: name} of the C# table."""
    return {identifier: name for identifier, name in re.findall(r'\[new\("([0-9a-f-]{36})"\)\] = "([^"]*)",', TABLE.read_text(encoding="utf-8"))}


def main():
    path = fetch(sys.argv[1] if len(sys.argv) > 1 else ".cache/dashif")
    expected, actual = registry(path), table()
    problems = [f"{identifier}: the registry has {expected.get(identifier)!r}, the table {actual.get(identifier)!r}" for identifier in sorted(expected.keys() | actual.keys())
                if expected.get(identifier) != actual.get(identifier)]
    for problem in problems:
        print(problem)
    print(f"{len(expected)} systems in the registry, {len(actual)} in the table, {len(problems)} differ.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
