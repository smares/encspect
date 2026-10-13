"""Downloads a file that is pinned by its SHA-256 from the first of its addresses that serves it.

The scripts download what the repository may not or need not hold: the ISO conformance streams,
the mp3PRO samples, other encoders' xHE-AAC files, mctoolamed, and the encoders that write fixtures: Fraunhofer's, Xing's,
BladeEnc's and Helix's 5.0 sources. Each has its original address and, where
one works, a fallback: the copy the Internet Archive's Wayback Machine keeps, or another mirror. The
SHA-256 decides whether a copy is the right file, so any address is safe to try. The files the
repository may not hold (the ISO streams and the mp3PRO samples) have a last copy in the project's
private repository, smares/encspect-private, which needs a read-only token in the
ENCSPECT_PRIVATE_TOKEN environment variable; without it, that address is skipped. check_sources.py, which a scheduled workflow
runs, downloads every file from every address, so a source that disappears is noticed while its
copies still work.
"""

import hashlib
import os
import subprocess
import sys

# The Wayback Machine serves the capture of url closest to the given timestamp, a year or a full
# capture time; "id_" asks for the file as captured, without the archive's banner.
WAYBACK = "https://web.archive.org/web/{timestamp}id_/{url}"


PRIVATE = "https://raw.githubusercontent.com/smares/encspect-private/main/"
TOKEN = "ENCSPECT_PRIVATE_TOKEN"


def archived(url, timestamp):
    return WAYBACK.format(timestamp=timestamp, url=url)


def private(path):
    """The address of path in the private repository."""
    return PRIVATE + path


def has_token(url):
    """Whether url can be tried: any public address, and the private repository's with a token."""
    return not url.startswith(PRIVATE) or bool(os.environ.get(TOKEN))


def download(url, path):
    """Downloads url to path; returns None when it worked, else curl's error. Every error is retried,
    refused connections too: the Wayback Machine refuses a client for a while after many downloads."""
    if not has_token(url):
        return f"{TOKEN} is not set"
    # The token reaches curl on its standard input, not its command line, which other processes can read.
    header = f"Authorization: Bearer {os.environ[TOKEN]}\n" if url.startswith(PRIVATE) else ""
    # A server that stops sending mid-transfer would otherwise hold curl until the job's timeout: below
    # 1 KB/s for a minute, or 15 minutes in all, counts as an error and is retried.
    result = subprocess.run(["curl", "-fsSL", "--retry", "5", "--retry-delay", "15", "--retry-all-errors", "--connect-timeout", "30",
                             "--speed-limit", "1024", "--speed-time", "60", "--max-time", "900",
                             "-H", "@-", "-o", str(path), url], input=header, capture_output=True, text=True)
    return None if result.returncode == 0 else result.stderr.strip().splitlines()[-1] if result.stderr.strip() else f"curl exit {result.returncode}"


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def fetch(addresses, digest, path):
    """Makes path hold the file with SHA-256 digest: keeps it if it already does, else downloads it
    from the first address that serves it, saying so when that is not the first."""
    if path.exists() and sha256(path) == digest:
        return
    failures = []
    for address in addresses:
        error = download(address, path)
        if error is None and sha256(path) == digest:
            if failures:
                print(f"{path.name}: fetched from {address} after {'; '.join(failures)}", file=sys.stderr)
            return
        failures.append(f"{address} has SHA-256 {sha256(path)}" if error is None else f"{address}: {error}")
        path.unlink(missing_ok=True)
    raise SystemExit(f"{path.name}: no address serves the file with SHA-256 {digest}: {'; '.join(failures)}")
