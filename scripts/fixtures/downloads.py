"""Downloads a file that is pinned by its SHA-256 from the first of its addresses that serves it.

The scripts download what the repository may not or need not hold: the ISO conformance streams,
the mp3PRO samples, mctoolamed and Fraunhofer's encoder. Each has its original address and, where
one works, a fallback: the copy the Internet Archive's Wayback Machine keeps, or another mirror. The
SHA-256 decides whether a copy is the right file, so any address is safe to try. check_sources.py, which a scheduled workflow
runs, downloads every file from every address, so a source that disappears is noticed while its
copies still work.
"""

import hashlib
import subprocess
import sys

# The Wayback Machine serves the capture of url closest to the given timestamp, a year or a full
# capture time; "id_" asks for the file as captured, without the archive's banner.
WAYBACK = "https://web.archive.org/web/{timestamp}id_/{url}"


def archived(url, timestamp):
    return WAYBACK.format(timestamp=timestamp, url=url)


def download(url, path):
    """Downloads url to path; returns None when it worked, else curl's error."""
    result = subprocess.run(["curl", "-fsSL", "--retry", "5", "--retry-delay", "10", "--connect-timeout", "30", "-o", str(path), url],
                            capture_output=True, text=True)
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
