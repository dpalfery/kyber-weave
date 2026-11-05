#!/usr/bin/env bash
# Stand-in for dotnet, selected by KW_PROC_GUARD_DOTNET. proc-guard.sh runs it as
# `dotnet build-server shutdown` only after the wrapped command has ended, so creating the marker
# file here switches fake-date-fails-after-marker.sh to failing exactly at the leftover scan.
# Starts nothing.
set -euo pipefail

: >"${KW_PROC_GUARD_TEST_MARKER:?marker path}"
