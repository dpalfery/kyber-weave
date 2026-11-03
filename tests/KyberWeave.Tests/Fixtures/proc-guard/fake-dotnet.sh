#!/usr/bin/env bash
# Stand-in for the dotnet executable, selected by KW_PROC_GUARD_DOTNET. The guard runs
# `dotnet build-server shutdown` before its leftover scan; this fake starts nothing and
# exits at once, so no test ever touches a real build server.
set -euo pipefail
exit 0
