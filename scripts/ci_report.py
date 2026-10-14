#!/usr/bin/env python3
"""Lists the CI runs on main (pushes, the nightly schedule, manual runs) of the last days and, for each red
one, which jobs and steps failed and which tests. Run it at the start of a working session.

    python3 scripts/ci_report.py [--days 14 | --since 2026-10-01] [--repo smares/encspect] [--branch main] [--all]

- Uses only the Python standard library and GitHub's REST API. Without a token only what the public API serves
  is available: the runs, their jobs and steps, and the annotations of a failed job (which carry the failed
  tests and the problem lines the pipeline reports). With GITHUB_TOKEN (or GH_TOKEN) set, the failed jobs'
  logs are read as well, and the failed tests are listed from them.
- Lists every workflow on main (CI, the weekly sources check, the WMA fixtures). --all adds the green runs.
- Exits with 1 when a run is red, 0 when none is, 2 when GitHub could not be reached.
"""

import argparse
import datetime
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

API = "https://api.github.com"
EVENTS = ("push", "schedule", "workflow_dispatch")
RED = ("failure", "timed_out", "startup_failure")
TEST_FAILURE = re.compile(r"^\s*(?:\d{4}-\d\d-\d\dT[\d:.]+Z\s+)?((?:Failed\s+|\[FAIL\]\s+)[A-Za-z0-9_.]+\.[A-Za-z0-9_.]+(?:\(.*?\))?)(?:\s+\[[<\s\d.]+m?s\])?\s*$", re.MULTILINE)
LOG_ERROR = re.compile(r"##\[error\](.*)")


DENIED = set()


class NoAccess(Exception):
    """GitHub answered 401, 403 or 404."""


class KeepRedirect(urllib.request.HTTPRedirectHandler):
    """Hands a redirect back instead of following it: a log's address redirects to a signed storage URL,
    which must not receive the token."""

    def redirect_request(self, *args, **kwargs):
        return None


def request(url, token, accept="application/vnd.github+json"):
    headers = {"Accept": accept, "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "encspect-ci-report"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    opener = urllib.request.build_opener(KeepRedirect)
    try:
        try:
            with opener.open(urllib.request.Request(url, headers=headers), timeout=60) as response:
                return response.read().decode("utf-8", errors="replace")
        except urllib.error.HTTPError as error:
            if error.code not in (301, 302, 303, 307, 308):
                raise
            target = error.headers["Location"]
        with urllib.request.urlopen(urllib.request.Request(target, headers={"User-Agent": headers["User-Agent"]}), timeout=120) as response:
            return response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as error:
        if error.code in (401, 403, 404):
            detail = ""
            try:
                detail = json.loads(error.read().decode()).get("message", "")
            except (ValueError, OSError):
                pass
            raise NoAccess(f"{error.code} {detail}".strip()) from error
        raise


def get_json(path, token, **query):
    parts = "&".join(f"{k}={urllib.parse.quote(str(v), safe='>=:')}" for k, v in query.items())
    return json.loads(request(f"{API}{path}" + (f"?{parts}" if parts else ""), token))


def runs_since(repo, branch, since, token):
    found = []
    for page in range(1, 6):
        data = get_json(f"/repos/{repo}/actions/runs", token, branch=branch, created=f">={since}", per_page=100, page=page,
                        exclude_pull_requests="true")
        found += data["workflow_runs"]
        if len(data["workflow_runs"]) < 100:
            break
    return [r for r in found if r["event"] in EVENTS]


def job_log(job, repo, token):
    """The log of a job, or None when GitHub wants a token for it."""
    if not token:
        return None
    try:
        return request(f"{API}/repos/{repo}/actions/jobs/{job['id']}/logs", token, accept="*/*")
    except NoAccess as error:
        DENIED.add(str(error))
        return None


def describe_failure(run, repo, token, out):
    jobs = get_json(f"/repos/{repo}/actions/runs/{run['id']}/jobs", token, per_page=100, filter="latest")["jobs"]
    failed = [j for j in jobs if j["conclusion"] in ("failure", "timed_out", "cancelled")]
    if not failed:
        out.append("      no failed job is listed (the run itself failed: see its page)")
    for job in failed:
        steps = [s["name"] for s in job.get("steps", []) if s["conclusion"] == "failure"]
        out.append(f"      job: {job['name']} ({job['conclusion']})" + (f" - failed step: {'; '.join(steps)}" if steps else ""))
        out.append(f"           {job['html_url']}")
        lines = []
        log = job_log(job, repo, token)
        if log is not None:
            lines = list(dict.fromkeys(m.group(1).strip() for m in TEST_FAILURE.finditer(log)))
            lines += [f"error: {m.group(1).strip()}" for m in LOG_ERROR.finditer(log)][:5]
        else:
            try:
                annotations = get_json(f"/repos/{repo}/check-runs/{job['id']}/annotations", token, per_page=50)
            except NoAccess:
                annotations = []
            lines = list(dict.fromkeys(a["message"].strip().splitlines()[0] for a in annotations if a.get("annotation_level") == "failure" and a.get("message")))
        for line in lines[:25]:
            out.append(f"             - {line[:200]}")
        if len(lines) > 25:
            out.append(f"             - and {len(lines) - 25} more")
        if not lines:
            out.append("             (no failing tests or problem lines found; " + ("the log has none" if log is not None else "the job log needs a token: set GITHUB_TOKEN, or open the job") + ")")


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--repo", default="smares/encspect")
    parser.add_argument("--branch", default="main")
    parser.add_argument("--days", type=int, default=14)
    parser.add_argument("--since", help="a date, YYYY-MM-DD; overrides --days")
    parser.add_argument("--all", action="store_true", help="list the green runs too")
    args = parser.parse_args()
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    since = args.since or (datetime.date.today() - datetime.timedelta(days=args.days)).isoformat()

    try:
        runs = runs_since(args.repo, args.branch, since, token)
    except (NoAccess, urllib.error.URLError, OSError) as error:
        print(f"Could not read the runs of {args.repo} from GitHub: {error}", file=sys.stderr)
        return 2

    red = [r for r in runs if r["conclusion"] in RED]
    running = [r for r in runs if r["status"] != "completed"]
    print(f"{args.repo}, {args.branch}, since {since}: {len(runs)} runs, {len(red)} red, {len(running)} not finished"
          + ("" if token else " (no GITHUB_TOKEN: job logs are not read)"))
    out = []
    for run in sorted(runs, key=lambda r: r["created_at"], reverse=True):
        verdict = run["conclusion"] or run["status"]
        if run in red or args.all or run["status"] != "completed":
            stamp = run["created_at"].replace("T", " ")[:16]
            out.append(f"  {stamp}  {run['name']:<22} {run['event']:<17} {verdict.upper():<10} {run['head_sha'][:7]}  {run['html_url']}")
            if run in red:
                out.append(f"      {run['display_title']}")
                try:
                    describe_failure(run, args.repo, token, out)
                except (NoAccess, urllib.error.URLError, OSError) as error:
                    out.append(f"      the jobs could not be read ({error}); open the run")
    print("\n".join(out) if out else "  nothing to raise.")
    if DENIED:
        print(f"\nThe token could not read the job logs ({'; '.join(sorted(DENIED))}); it needs read access to Actions. "
              "Failing tests come from the annotations instead, which name only what the pipeline reports.")
    return 1 if red else 0


if __name__ == "__main__":
    sys.exit(main())
