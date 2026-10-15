#!/usr/bin/env bash
# The shell of the Linux jobs' run steps (defaults.run.shell: bash scripts/ci/step-shell.sh {0}). It runs
# the step's script the way GitHub's own bash does (bash -e with pipefail) and, when the script fails, adds a
# short account to the job's step summary: the lines of the step's output that name a problem and the
# last ones. A passing step costs nothing, and a failing one needs no step of its own to be explained.
#
# CI_SUMMARY_FILES names a directory whose files are listed too (the inputs the fuzzer saved);
# CI_STEP_SUMMARY=off leaves the summary to the job (the test steps write one from their TRX files).
set -u
directory="${RUNNER_TEMP:-/tmp}/ci-logs"
mkdir -p "$directory"
log="$directory/step-$$-$(date +%s).log"
bash --noprofile --norc -eo pipefail "$1" 2>&1 | tee "$log"
status=${PIPESTATUS[0]}
if [ "$status" -ne 0 ] && [ "${CI_STEP_SUMMARY:-on}" != "off" ]; then
  python=$(command -v python3 || command -v python || true)
  if [ -n "$python" ]; then
    command_line=$(grep -v '^\s*\(#\|$\)' "$1" | head -n 1 | cut -c1-200)
    "$python" "$(dirname "$0")/summarize.py" logs "$log" --title "Job ${GITHUB_JOB:-?}: \`${command_line}\` exited with ${status}" ${CI_SUMMARY_FILES:+--files "$CI_SUMMARY_FILES"} || true
  fi
fi
exit "$status"
