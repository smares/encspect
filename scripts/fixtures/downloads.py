"""Downloads a file that is pinned by its SHA-256 from the first of its addresses that serves it.

The scripts download what the repository may not or need not hold: the ISO conformance streams,
the mp3PRO samples, other encoders' xHE-AAC and HE-AAC files, mctoolamed, and the encoders that write fixtures: Fraunhofer's, Xing's,
BladeEnc's and Helix's 5.0 sources. Each has its original address and, where
one works, a fallback: the copy the Internet Archive's Wayback Machine keeps, or another mirror. The
SHA-256 decides whether a copy is the right file, so any address is safe to try. The files the
repository may not or does not hold (the ISO streams, the mp3PRO samples and the xHE-AAC samples)
have a last copy in the project's private repository, smares/encspect-private, which needs a read-only token in the
ENCSPECT_PRIVATE_TOKEN environment variable; without it, that address is skipped. check_sources.py, which a scheduled workflow
runs, downloads every file from every address, so a source that disappears is noticed while its
copies still work.
"""

import hashlib
import os
import pathlib
import re
import shutil
import subprocess
import sys
import time

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


# These servers refuse curl's own user agent, so their addresses name a browser's.
BROWSER = "Mozilla/5.0 (X11; Linux x86_64; rv:128.0) Gecko/20100101 Firefox/128.0"
BROWSER_PREFIXES = ("https://www.etsi.org/", "https://www.atsc.org/", "https://download.microsoft.com/")


# curl itself runs with --retry 5 --retry-delay 15 --retry-all-errors, so it repeats every failure, a 404 or a
# certificate failure included, five times. Those retries are too close together to outlast an outage of a minute or
# more, so fetch() makes further rounds after growing delays when the answer was not a definite one. It tries every
# address once before the first round, so a dead original does not delay a working fallback. What counts as transient:
#   - on every host: a failure to resolve, connect, handshake, send or receive, a timeout, a partial transfer, an empty reply
#     (curl exit codes TRANSIENT_CURL_CODES) and an HTTP 429 or 5xx (curl exit 22 with that status);
#   - on the Wayback Machine also an HTTP 404, which it answers intermittently for captures that it serves a minute later,
#     and every other curl failure, a certificate failure included, as before: it refuses clients for a while after many downloads.
# Not retried beyond curl's own five: the other HTTP statuses, a certificate failure, a malformed address (all off the
# Wayback Machine) and a file with the wrong SHA-256 (fetch() compares that after a successful transfer).
# Between 11:41 and 15:02 UTC on 2026-11-01 samples.ffmpeg.org intermittently reset connections or timed out
# (docs/development.md, "Pinned downloads"); the Wayback delays are the older, shorter ones.
# The time spent on failed transfers and on waiting is counted with a monotonic clock against RETRY_BUDGET seconds for
# the whole job: the scripts of a job add to one file under RUNNER_TEMP, so that many failing addresses cannot use up its timeout.
ARCHIVE_PREFIX = "https://web.archive.org/"
ARCHIVE_DELAYS = (10, 30, 60)
HOST_DELAYS = (30, 90, 180)
RETRY_BUDGET = 1200
TRANSIENT_CURL_CODES = frozenset({5, 6, 7, 18, 28, 35, 52, 55, 56, 92})
BUDGET_FILE = "encspect-download-budget"
_clock = time.monotonic
_local_spent = 0.0


def _budget_path():
    temp = os.environ.get("RUNNER_TEMP")
    return pathlib.Path(temp) / BUDGET_FILE if temp else None


def spent():
    """Seconds this job has spent on failed transfers and on waiting to retry them."""
    path = _budget_path()
    if path is None:
        return _local_spent
    try:
        return float(path.read_text())
    except (OSError, ValueError):
        return 0.0


def spend(seconds):
    global _local_spent
    path = _budget_path()
    if path is None:
        _local_spent += seconds
        return
    try:
        path.write_text(str(spent() + seconds))
    except OSError:
        _local_spent += seconds


def retry_delay(url, error, attempt, waited=0):
    """Seconds to wait before the next attempt at url after the failure error, which is curl's exit code
    and message (code 0: not a transfer failure, never retried), or None when it is not retried. attempt
    counts the retries made so far; waited is the budget spent so far."""
    archive = url.startswith(ARCHIVE_PREFIX)
    delays = ARCHIVE_DELAYS if archive else HOST_DELAYS
    if attempt >= len(delays):
        return None
    code, message = error
    if not code:
        return None
    if code == 22:
        status = re.search(r"error: (\d{3})", message)
        if not status or not (status.group(1) == "429" or status.group(1).startswith("5") or (archive and status.group(1) == "404")):
            return None
    elif not archive and code not in TRANSIENT_CURL_CODES:
        return None
    delay = delays[attempt]
    return delay if waited + delay <= RETRY_BUDGET else None


def attempt(url, path):
    """One transfer of url to path: None when it worked, else (exit code, message). The time of a failure
    counts toward the budget."""
    if not has_token(url):
        return 0, f"{TOKEN} is not set"
    start = _clock()
    error = _curl(url, path)
    if error is not None:
        spend(_clock() - start)
    return error


def download(url, path, sleep=time.sleep):
    """Downloads url to path, retrying a transient error after growing delays (see retry_delay); returns
    None when it worked, else curl's error message. check_sources.py uses it for one address; fetch() orders
    its own rounds."""
    tries = 0
    while True:
        error = attempt(url, path)
        if error is None:
            return None
        delay = retry_delay(url, error, tries, spent())
        if delay is None:
            return error[1]
        print(f"{url}: {error[1]}; retrying in {delay} s", file=sys.stderr)
        spend(delay)
        sleep(delay)
        tries += 1


def _curl(url, path):
    """One curl run: None when it worked, else (exit code, last line of its error message)."""
    # The token reaches curl on its standard input, not its command line, which other processes can read.
    header = f"Authorization: Bearer {os.environ[TOKEN]}\n" if url.startswith(PRIVATE) else ""
    # A server that stops sending mid-transfer would otherwise hold curl until the job's timeout: below
    # 1 KB/s for a minute, or 15 minutes in all, counts as an error and is retried.
    agent = ["-A", BROWSER] if url.startswith(BROWSER_PREFIXES) else []
    result = subprocess.run(["curl", "-fsSL", "--retry", "5", "--retry-delay", "15", "--retry-all-errors", "--connect-timeout", "30",
                             "--speed-limit", "1024", "--speed-time", "60", "--max-time", "900", *agent,
                             "-H", "@-", "-o", str(path), url], input=header, capture_output=True, text=True)
    if result.returncode == 0:
        return None
    lines = result.stderr.strip().splitlines()
    return result.returncode, lines[-1] if lines else f"curl exit {result.returncode}"


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


CACHE_VARIABLE = "ENCSPECT_DOWNLOAD_CACHE"


def cached(digest):
    """The file with SHA-256 digest in the download cache named by ENCSPECT_DOWNLOAD_CACHE (CI keeps it
    between runs), or None. The cache is addressed by content and every copy is checked against its
    name, so it can never serve a file other than the pinned one, whatever a pin was changed to."""
    directory = os.environ.get(CACHE_VARIABLE)
    if not directory:
        return None
    entry = pathlib.Path(directory) / digest
    if entry.is_file() and sha256(entry) == digest:
        os.utime(entry)  # CI drops what nobody has used for a month
        return entry
    return None


def remember(path, digest):
    """Keeps a file that a public address served. Never call it for a file the private repository served: CI
    keeps this cache in the public Actions cache, which a pull request from a fork can restore."""
    directory = os.environ.get(CACHE_VARIABLE)
    if directory:
        pathlib.Path(directory).mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, pathlib.Path(directory) / digest)


def _try(address, digest, path):
    """One attempt at address: None when path now holds the pinned file, else the error."""
    error = attempt(address, path)
    if error is None and sha256(path) == digest:
        return None
    if error is None:
        error = (0, f"has SHA-256 {sha256(path)}")
    path.unlink(missing_ok=True)
    return error


def fetch(addresses, digest, path, sleep=time.sleep):
    """Makes path hold the file with SHA-256 digest: keeps it if it already does, else copies it from the
    download cache, else downloads it from the first address that serves it, saying so when that is not
    the first. Every address is tried once before any is tried again, in rounds after growing delays."""
    if path.exists() and sha256(path) == digest:
        return
    if (entry := cached(digest)) is not None:
        shutil.copyfile(entry, path)
        return
    errors = {}

    def served(address):
        if (error := _try(address, digest, path)) is None:
            if errors:
                print(f"{path.name}: fetched from {address} after {'; '.join(f'{a}: {e[1]}' for a, e in errors.items())}", file=sys.stderr)
            if not address.startswith(PRIVATE):
                remember(path, digest)
            return True
        errors[address] = error
        return False

    for address in addresses:
        if served(address):
            return
    for round_number in range(max(len(ARCHIVE_DELAYS), len(HOST_DELAYS))):
        due = sorted((retry_delay(a, e, round_number), a) for a, e in errors.items() if retry_delay(a, e, round_number) is not None)
        waited = 0
        for delay, address in due:
            if retry_delay(address, errors[address], round_number, spent()) is None:
                continue
            if delay > waited:
                print(f"{path.name}: retrying in {delay - waited} s (round {round_number + 1})", file=sys.stderr)
                spend(delay - waited)
                sleep(delay - waited)
                waited = delay
            if served(address):
                return
    raise SystemExit(f"{path.name}: no address serves the file with SHA-256 {digest}: {'; '.join(f'{a}: {e[1]}' for a, e in errors.items())}")
