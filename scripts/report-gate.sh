#!/usr/bin/env bash
# report-gate.sh - run a command whose result is a report file, and stop it if it has written a
# complete report but does not exit.
#
# Written for the `inspectcode` review gate. On macOS 27.3 Beta, .NET never returns from closing a
# listening socket while another thread is blocked in accept() on it. ReSharper InspectCode does
# that at exit, so it wrote its whole report and then hung until the gate's time limit, and a
# review read a finished analysis as one that never ran. This wrapper counts that run as done.
#
# Usage:
#   scripts/report-gate.sh --report <path> (--ends-with <text> | --contains <text>)
#                          [--grace <seconds>] [--timeout <seconds>] -- <cmd...>
#
# What counts as done (exactly one of --ends-with and --contains is required):
#   - <path> is removed before the command starts, so only this run can have written it.
#   - --ends-with: the report is complete when its last non-blank text is <text> (for InspectCode's
#     XML, "</Report>") and its size has not changed for --grace seconds (default 20).
#   - --contains: the report is complete when <text> appears anywhere in it (searched as a fixed
#     string), and --grace seconds (default 20) have passed since it was first seen.
#   - A command still running after that is stopped, with its whole process tree, and the run
#     exits 0. Standard error says so.
#
# What does not:
#   - A command that hangs before its report is complete. Without --timeout this script keeps
#     waiting, so the caller's own time limit reports the hang.
#   - A command that exits 0 without a complete report. That exits 1.
#
# --timeout <seconds> (optional) is a limit on the whole run. A command that is not done when it is
# reached is stopped, with its whole process tree, and the run exits 124.
#
# Exit codes:
#   0    the command exited 0 with a complete report, or was stopped after writing one
#   1    the command exited 0 but left no complete report
#   2    usage error, or pgrep is missing
#   124  --timeout was reached before the run was done; the command's tree was killed
#   143  this script got SIGINT or SIGTERM; the command's tree was killed first
#   else the command's own exit code

set -u

usage() {
  printf 'usage: report-gate.sh --report <path> (--ends-with <text> | --contains <text>) [--grace <seconds>] [--timeout <seconds>] -- <cmd...>\n' >&2
  exit 2
}

report=""
ends_with=""
contains=""
grace=20
timeout=""

while [ $# -gt 0 ]; do
  case "$1" in
    --report)
      [ $# -ge 2 ] || usage
      report="$2"
      shift 2
      ;;
    --ends-with)
      [ $# -ge 2 ] || usage
      ends_with="$2"
      shift 2
      ;;
    --contains)
      [ $# -ge 2 ] || usage
      contains="$2"
      shift 2
      ;;
    --grace)
      [ $# -ge 2 ] || usage
      grace="$2"
      shift 2
      ;;
    --timeout)
      [ $# -ge 2 ] || usage
      timeout="$2"
      shift 2
      ;;
    --)
      shift
      break
      ;;
    *)
      usage
      ;;
  esac
done

if [ -z "${report}" ] || [ $# -eq 0 ]; then
  usage
fi
# Exactly one of the two finish signals.
if { [ -z "${ends_with}" ] && [ -z "${contains}" ]; } || { [ -n "${ends_with}" ] && [ -n "${contains}" ]; }; then
  usage
fi
case "${grace}" in
  '' | *[!0-9]*) usage ;;
esac
if [ -n "${timeout}" ]; then
  case "${timeout}" in
    *[!0-9]*) usage ;;
  esac
  [ "${timeout}" -gt 0 ] || usage
fi

# Stopping the command means killing its whole tree, and the tree is found with pgrep. Without it only
# the direct child would die and the run would still exit 0, so refuse to start instead.
if ! command -v pgrep >/dev/null 2>&1; then
  printf 'report-gate: pgrep is required to stop the command and its children, and it is not on the PATH\n' >&2
  exit 2
fi

# Every descendant of a pid, deepest first, so a parent cannot respawn what was just killed.
descendants_of() {
  local child
  for child in $(pgrep -P "$1" 2>/dev/null); do
    descendants_of "${child}"
    printf '%s\n' "${child}"
  done
}

kill_tree() {
  local pids
  pids="$(descendants_of "$1")"
  # shellcheck disable=SC2086
  kill -KILL ${pids} "$1" 2>/dev/null
  wait "$1" 2>/dev/null
}

# Prints the report's size when it is complete, and nothing when it is not.
complete_size() {
  [ -s "${report}" ] || return 0
  local tail_text
  tail_text="$(tail -c 4096 "${report}" 2>/dev/null | tr -d '[:space:]')"
  case "${tail_text}" in
    *"$(printf '%s' "${ends_with}" | tr -d '[:space:]')") wc -c < "${report}" | tr -d '[:space:]' ;;
  esac
}

# Succeeds when the report shows a finished run, in whichever mode was asked for.
report_finished() {
  if [ -n "${contains}" ]; then
    grep -F -q -- "${contains}" "${report}" 2>/dev/null
  else
    [ -n "$(complete_size)" ]
  fi
}

rm -f -- "${report}"

"$@" &
pid=$!
trap 'kill_tree "${pid}"; exit 143' INT TERM

settled_size=""
settled_for=0
seen_at=""
start=${SECONDS}

while kill -0 "${pid}" 2>/dev/null; do
  sleep 1
  done_now=0
  if [ -n "${contains}" ]; then
    if grep -F -q -- "${contains}" "${report}" 2>/dev/null; then
      [ -n "${seen_at}" ] || seen_at=${SECONDS}
      [ $((SECONDS - seen_at)) -ge "${grace}" ] && done_now=1
      size="$(wc -c < "${report}" 2>/dev/null | tr -d '[:space:]')"
    else
      seen_at=""
      size=""
    fi
  else
    size="$(complete_size)"
    if [ -n "${size}" ] && [ "${size}" = "${settled_size}" ]; then
      settled_for=$((settled_for + 1))
    else
      settled_size="${size}"
      settled_for=0
    fi
    [ -n "${size}" ] && [ "${settled_for}" -ge "${grace}" ] && done_now=1
  fi

  if [ "${done_now}" -eq 1 ] && kill -0 "${pid}" 2>/dev/null; then
    printf 'report-gate: the command wrote a complete report (%s, %s bytes) but did not exit within %s seconds; stopping it and counting the run as done\n' \
      "${report}" "${size}" "${grace}" >&2
    kill_tree "${pid}"
    trap - INT TERM
    exit 0
  fi

  if [ -n "${timeout}" ] && [ $((SECONDS - start)) -ge "${timeout}" ] && kill -0 "${pid}" 2>/dev/null; then
    printf 'report-gate: the command did not finish within %s seconds; stopping it\n' "${timeout}" >&2
    kill_tree "${pid}"
    trap - INT TERM
    exit 124
  fi
done

wait "${pid}"
status=$?
trap - INT TERM

if [ "${status}" -eq 0 ] && ! report_finished; then
  printf 'report-gate: the command exited 0 but left no complete report at %s\n' "${report}" >&2
  exit 1
fi

exit "${status}"
