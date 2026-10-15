#!/usr/bin/env python3
"""The verdict of ci.yml: every job succeeded, or was skipped on purpose.

Reads the aggregate job's needs context as JSON from the NEEDS environment variable. The "changes"
job decides which jobs a pull request needs and publishes one output per job, named like the job: a job
may be skipped only when that output is "false". A job that is skipped for any other reason (a failed
dependency, a condition nobody meant) fails the check, as does a failed or canceled one.

A change to the pipeline itself (.github/, scripts/ci/) runs every job. The changes job tells that in a plain
shell step, not through changes.py, and publishes it as the "pipeline" output; when it is "true" no skip is
accepted at all, so a change to the filter cannot excuse itself. The workflow repeats that rule in a step
of its own (jq), which this script cannot weaken.
"""

import json
import os
import sys


def verdict(needs):
    problems = []
    changes = needs.get("changes", {})
    outputs = changes.get("outputs") or {}
    if changes.get("result") != "success":
        problems.append(f"changes: {changes.get('result', 'missing')}; without its decision no skip can be trusted")
    for job, info in sorted(needs.items()):
        if job == "changes":
            continue
        result = info.get("result")
        if result == "success":
            continue
        if result == "skipped" and outputs.get(job) == "false" and outputs.get("pipeline") != "true":
            continue
        problems.append(f"{job}: {result}" + (" (skipped, but the change filter wanted it to run)" if result == "skipped" else ""))
    return problems


def main():
    needs = json.loads(os.environ["NEEDS"])
    for job, info in sorted(needs.items()):
        flag = (needs.get("changes", {}).get("outputs") or {}).get(job)
        note = " (skipped by the change filter)" if info.get("result") == "skipped" and flag == "false" else ""
        print(f"{job}: {info.get('result')}{note}")
    problems = verdict(needs)
    for problem in problems:
        print(f"::error::{problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
