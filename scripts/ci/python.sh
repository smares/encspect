#!/usr/bin/env bash
# Runs the Python the runner has: python3 on Linux and macOS, python on Windows.
exec "$(command -v python3 || command -v python)" "$@"
