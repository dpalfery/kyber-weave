#!/usr/bin/env bash
# proc-guard.sh - absolute process-cap watchdog for test and build runs.
#
# A test run once fork-bombed node (536 processes) and crashed three developer Macs. This
# wrapper is the safety net: it polls the process table while a command runs and kills the
# whole tree the moment a cap is reached, and afterwards it refuses to call a run clean while
# anything it started is still alive.
#
# Usage:
#   scripts/proc-guard.sh run [--max-node N] [--max-tree N] [--timeout S] [--tty] -- <cmd...>
#   scripts/proc-guard.sh check-leftovers --root <dir> [--since <epoch-seconds>]
#
# Exit codes:
#   86   a cap tripped (stderr names max-tree or max-node); the whole tree was killed
#   87   processes survived the command (run: only when the command itself succeeded)
#   124  --timeout elapsed; the whole tree was killed (same code as timeout(1))
#   2    usage error, or fail-closed: bad root, unreadable clock or process table
#   else the wrapped command's own exit code
#
# Caps are absolute, not relative to a baseline, and reaching the cap trips it:
#   --max-node  all of this user's node, nodejs, bun, deno and tsx processes on the machine, not only
#               the wrapped command's (default 128); polled every 0.5 s
#   --max-tree  descendants of the wrapped command (default 64)
#
# Terminal input: the wrapped command runs as a background job in its own process group. When
# the guard's stdin is a terminal, a read from it would stop that job with SIGTTIN until the
# watchdog fires, so by default stdin is redirected from /dev/null in that case (guarded
# commands are non-interactive test and gate runs). --tty keeps the terminal as stdin instead;
# a command that then reads the terminal is stopped by SIGTTIN and ends only by --timeout.
#
# Test hook: KW_PROC_GUARD_PS names an executable that prints the process table
# (rows: "<pid> <ppid> <user> <comm> <args...>") instead of the real ps.
# Test hooks for check-leftovers --since: KW_PROC_GUARD_NOW (epoch seconds) replaces `date +%s`;
# KW_PROC_GUARD_ETIME names an executable printing rows "<pid> <etime>" (ps etime form) instead of
# `ps -o etime= -p <pid>`. A survivor counts when now - elapsed >= since, or when elapsed is unknown.
# Exported to the wrapped command and to that hook:
#   KW_PROC_GUARD_ROOT      repository root (run) or the --root value (check-leftovers)
#   KW_PROC_GUARD_ROOT_PID  pid of the wrapped command
# Error handling: deliberately no `set -e`. The poll loop, the kill paths and the best-effort
# cleanup rely on commands that are allowed to fail (kill -0, ps of a dead pid, grep with no
# match), so -e would abort a run mid-kill. Instead every substitution that a decision depends on
# (the repo root, the clock, the process table) is checked and the guard fails closed with exit 2.
set -uo pipefail

readonly EXIT_CAP=86
readonly EXIT_LEFTOVERS=87
readonly EXIT_TIMEOUT=124
readonly POLL_INTERVAL=0.5
readonly KILL_GRACE=1

# Owner comparison: the real table prints the numeric uid (ps truncates long user names on some
# systems); a fixture table (KW_PROC_GUARD_PS) prints the account name.
if [[ -n "${KW_PROC_GUARD_PS:-}" ]]; then
  me="$(id -un)"
else
  me="$(id -u)"
fi
self_pid=$$

usage() {
  printf 'usage: proc-guard.sh run [--max-node N] [--max-tree N] [--timeout S] -- <cmd...>\n' >&2
  printf '       proc-guard.sh check-leftovers --root <dir> [--since <epoch-seconds>]\n' >&2
}

# Print the process table: "<pid> <ppid> <user> <comm> <args...>".
table() {
  if [[ -n "${KW_PROC_GUARD_PS:-}" ]]; then
    "${KW_PROC_GUARD_PS}" 2>/dev/null
  else
    ps -axo pid=,ppid=,uid=,comm=,args= 2>/dev/null
  fi
}

# Count this user's JavaScript runtime processes (basename of comm: node, nodejs, bun, deno, tsx).
# The count is machine-wide for this user, not relative to the wrapped command: the cap is an
# absolute ceiling against fork-bombs, and it also counts unrelated node processes.
count_node() {
  table | awk -v u="${me}" '
    $3 == u {
      n = split($4, a, "/")
      if (a[n] == "node" || a[n] == "nodejs" || a[n] == "bun" || a[n] == "deno" || a[n] == "tsx") c++
    }
    END { print c + 0 }'
}

# Fail closed on a root that is empty, not a directory, or the filesystem root (which would match
# nearly every command line). Prints the reason on stderr; returns 2 when refused.
validate_root() {
  local root="$1" trimmed physical
  # Strip every trailing slash: "//" and "///" are the filesystem root as well.
  trimmed="${root%"${root##*[!/]}"}"
  if [[ -z "${root}" || -z "${trimmed}" || ! -d "${root}" ]]; then
    printf 'proc-guard: refusing root "%s": it must be an existing directory other than /\n' "${root}" >&2
    return 2
  fi
  # A symlink or ".." can still resolve to the filesystem root.
  physical="$(cd -P "${root}" 2>/dev/null && pwd -P)" || physical=""
  if [[ -z "${physical}" || "${physical}" == "/" ]]; then
    printf 'proc-guard: refusing root "%s": it resolves to the filesystem root or cannot be resolved\n' "${root}" >&2
    return 2
  fi
  return 0
}

# Real wall clock in epoch seconds; non-zero (nothing printed) when date fails.
real_now() {
  local value
  value="$("${KW_PROC_GUARD_DATE:-date}" +%s 2>/dev/null)"
  if [[ ! "${value}" =~ ^[0-9]+$ ]]; then
    printf 'proc-guard: cannot read the clock (date +%%s failed)\n' >&2
    return 1
  fi
  printf '%s\n' "${value}"
}

# Epoch seconds, or the KW_PROC_GUARD_NOW fixture; non-zero (nothing printed) when the clock fails.
epoch_now() {
  local value
  if [[ -n "${KW_PROC_GUARD_NOW:-}" ]]; then
    value="${KW_PROC_GUARD_NOW}"
    if [[ ! "${value}" =~ ^[0-9]+$ ]]; then
      printf 'proc-guard: KW_PROC_GUARD_NOW must be epoch seconds\n' >&2
      return 1
    fi
    printf '%s\n' "${value}"
    return 0
  fi
  real_now
}

# Print the pids of every descendant of the given pid, one per line.
descendants_of() {
  table | awk -v root="$1" '
    { ppid[$1] = $2; pids[NR] = $1 }
    END {
      mark[root] = 1
      changed = 1
      while (changed) {
        changed = 0
        for (i = 1; i <= NR; i++) {
          p = pids[i]
          if (!(p in mark) && (ppid[p] in mark)) { mark[p] = 1; changed = 1; print p }
        }
      }
    }'
}

# Pids that are never leftovers: proc-guard itself, its ancestors (the caller's own shell may be
# an orphan that mentions the repo root), and everything in proc-guard's own process group.
protected_pids() {
  local cur="${self_pid}" next hops=0 own_pgid
  printf '%s\n' "${self_pid}"
  while ((hops < 64)); do
    next="$(ps -o ppid= -p "${cur}" 2>/dev/null | tr -d ' ')" || true
    if [[ ! "${next}" =~ ^[0-9]+$ ]] || ((next <= 1)); then
      break
    fi
    printf '%s\n' "${next}"
    cur="${next}"
    hops=$((hops + 1))
  done
  own_pgid="$(ps -o pgid= -p "${self_pid}" 2>/dev/null | tr -d ' ')" || true
  if [[ "${own_pgid}" =~ ^[0-9]+$ ]]; then
    group_members "${own_pgid}"
  fi
}

# Print this user's re-parented (ppid 1) rows whose command line names the root on a path
# boundary (the root followed by "/", whitespace or end; never a bare prefix such as root-2),
# leaving out proc-guard's own ancestors and process group.
orphans_for_root() {
  local protected
  protected="$(protected_pids | tr '\n' ' ')"
  table | awk -v u="${me}" -v root="${1%/}" -v prot="${protected}" '
    function names_root(line,   start, pos, before, after) {
      start = 1
      while ((pos = index(substr(line, start), root)) > 0) {
        pos += start - 1
        before = (pos > 1) ? substr(line, pos - 1, 1) : " "
        after = substr(line, pos + length(root), 1)
        if (before !~ /[A-Za-z0-9_.-]/ && (after == "" || after == "/" || after ~ /[ \t]/)) return 1
        start = pos + 1
      }
      return 0
    }
    BEGIN { n = split(prot, parts, " "); for (i = 1; i <= n; i++) skip[parts[i]] = 1 }
    $2 == 1 && $3 == u && !($1 in skip) && names_root($0)'
}

# Convert ps etime ([[dd-]hh:mm:]ss) to seconds; -1 when empty.
etime_to_seconds() {
  local rest="$1" days=0 part total=0 field
  if [[ -z "${rest}" ]]; then
    printf -- '-1\n'
    return
  fi
  if [[ "${rest}" == *-* ]]; then
    days="${rest%%-*}"
    rest="${rest#*-}"
    if [[ ! "${days}" =~ ^[0-9]+$ ]]; then
      printf -- '-1\n'
      return
    fi
  fi
  # Anything but digits separated by single colons (fractions, trailing colon, empty field) is unknown.
  if [[ ! "${rest}" =~ ^[0-9]+(:[0-9]+){0,2}$ ]]; then
    printf -- '-1\n'
    return
  fi
  local IFS=':'
  for part in ${rest}; do
    field=$((10#${part}))
    total=$((total * 60 + field))
  done
  printf '%s\n' $((total + 10#${days} * 86400))
}

# Seconds of elapsed time for a pid from ps etime, or the KW_PROC_GUARD_ETIME fixture; -1 when unknown.
elapsed_seconds() {
  local raw
  if [[ -n "${KW_PROC_GUARD_ETIME:-}" ]]; then
    raw="$("${KW_PROC_GUARD_ETIME}" 2>/dev/null | awk -v p="$1" '$1 == p { print $2; exit }')" || true
  else
    raw="$(ps -o etime= -p "$1" 2>/dev/null | tr -d ' ')" || true
  fi
  etime_to_seconds "${raw}"
}

signal_pids() {
  local sig="$1" pid
  shift
  for pid in "$@"; do
    if [[ "${pid}" =~ ^[0-9]+$ ]] && ((pid > 1)) && ((pid != self_pid)); then
      if [[ -n "${KW_PROC_GUARD_KILL:-}" ]]; then
        # Test hook: record the signal instead of sending it.
        "${KW_PROC_GUARD_KILL}" "${sig}" "${pid}" 2>/dev/null || true
      else
        kill "-${sig}" "${pid}" 2>/dev/null || true
      fi
    fi
  done
}

# Kill the wrapped command's process group and every known descendant: TERM, grace, KILL.
kill_tree() {
  local root="$1" pids
  pids="$( { descendants_of "${root}"; printf '%s\n' "${tracked[@]+"${tracked[@]}"}"; } | sort -u)"
  # shellcheck disable=SC2086
  signal_pids TERM ${pids}
  kill -TERM -- "-${root}" 2>/dev/null || true
  kill -TERM "${root}" 2>/dev/null || true
  sleep "${KILL_GRACE}"
  # shellcheck disable=SC2086
  signal_pids KILL ${pids}
  kill -KILL -- "-${root}" 2>/dev/null || true
  kill -KILL "${root}" 2>/dev/null || true
}

# True when the wrapped command is a dotnet invocation, or a test points the guard at a fake dotnet.
wraps_dotnet() {
  local word
  [[ -n "${KW_PROC_GUARD_DOTNET:-}" ]] && return 0
  for word in "$@"; do
    [[ "${word##*/}" == "dotnet" ]] && return 0
  done
  return 1
}

# Best-effort, bounded (20 s) `dotnet build-server shutdown`; errors are ignored.
shutdown_build_servers() {
  local dotnet_bin="${KW_PROC_GUARD_DOTNET:-dotnet}" shutdown_pid waited=0
  command -v "${dotnet_bin}" >/dev/null 2>&1 || return 0
  "${dotnet_bin}" build-server shutdown >/dev/null 2>&1 &
  shutdown_pid=$!
  while kill -0 "${shutdown_pid}" 2>/dev/null; do
    if ((waited >= 20)); then
      kill -KILL "${shutdown_pid}" 2>/dev/null || true
      break
    fi
    sleep 1
    waited=$((waited + 1))
  done
  { wait "${shutdown_pid}"; } 2>/dev/null || true
}

# Pids still in the wrapped command's process group, per the process table's pgid column.
group_members() {
  ps -axo pid=,pgid= 2>/dev/null | awk -v g="$1" -v me="${self_pid}" '$2 == g && $1 != me { print $1 }'
}

# Command line of a pid: the process table first, then the real ps.
args_of() {
  local line
  line="$(table | awk -v p="$1" '$1 == p { $1 = $2 = $3 = $4 = ""; sub(/^ +/, ""); print; exit }')"
  if [[ -z "${line}" ]]; then
    line="$(ps -o args= -p "$1" 2>/dev/null)" || true
  fi
  printf '%s\n' "${line}"
}

# Allow-list of known benign long-lived .NET servers.
is_benign_dotnet_server() {
  case "$1" in
    *VBCSCompiler*) return 0 ;;
    *MSBuild.dll*nodeReuse*|*MSBuild.dll*nodemode*) return 0 ;;
    *dotnet*build-server*) return 0 ;;
    *) return 1 ;;
  esac
}

# Describe one pid for a survivor listing.
describe_pid() {
  local line
  line="$(args_of "$1")"
  printf '%s %s\n' "$1" "${line:-(command line unavailable)}"
}

check_leftovers() {
  local root="" since="" since_given=0
  while (($#)); do
    case "$1" in
      --root) root="${2:-}"; shift 2 ;;
      --since) since="${2:-}"; since_given=1; shift 2 ;;
      *) usage; return 2 ;;
    esac
  done
  if ((since_given == 1)) && [[ ! "${since}" =~ ^[0-9]+$ ]]; then
    printf 'proc-guard: --since must be a non-negative integer of epoch seconds\n' >&2
    return 2
  fi
  if [[ -z "${root}" ]]; then
    usage
    return 2
  fi
  validate_root "${root}" || return 2
  export KW_PROC_GUARD_ROOT="${root}"
  local found
  local rows row pid age now
  rows="$(orphans_for_root "${root}")"
  if ((since_given == 1)) && [[ -n "${rows}" ]]; then
    now="$(epoch_now)" || return 2
    local kept=""
    while IFS= read -r row; do
      [[ -z "${row}" ]] && continue
      pid="${row%% *}"
      age="$(elapsed_seconds "${pid}")"
      if ((age < 0 || now - age >= since)); then
        kept+="${row}"$'\n'
      fi
    done <<<"${rows}"
    rows="${kept%$'\n'}"
  fi
  found="$(printf '%s' "${rows}" | awk 'NF { pid = $1; $1 = $2 = $3 = $4 = ""; sub(/^ +/, ""); print pid "\t" $0 }')"
  if [[ -n "${found}" ]]; then
    printf 'proc-guard: leftover processes under %s (pid, command):\n%s\n' "${root}" "${found}"
    return "${EXIT_LEFTOVERS}"
  fi
  return 0
}

run_guarded() {
  local max_node=128 max_tree=64 timeout_s=0 keep_tty=0
  while (($#)); do
    case "$1" in
      --max-node) max_node="${2:-}"; shift 2 ;;
      --max-tree) max_tree="${2:-}"; shift 2 ;;
      --timeout) timeout_s="${2:-}"; shift 2 ;;
      --tty) keep_tty=1; shift ;;
      --) shift; break ;;
      *) usage; return 2 ;;
    esac
  done
  local value
  for value in "${max_node}" "${max_tree}" "${timeout_s}"; do
    if [[ ! "${value}" =~ ^[0-9]+$ ]]; then
      printf 'proc-guard: caps and timeout must be non-negative integers\n' >&2
      return 2
    fi
  done
  if (($# == 0)); then
    usage
    return 2
  fi

  local root started_at pid
  root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." 2>/dev/null && pwd)" || root=""
  validate_root "${root}" || return 2
  export KW_PROC_GUARD_ROOT="${root}"
  started_at="$(real_now)" || return 2
  # With the real process table, an empty read means ps failed: every cap would silently pass.
  if [[ -z "${KW_PROC_GUARD_PS:-}" && -z "$(table)" ]]; then
    printf 'proc-guard: cannot read the process table (ps failed); refusing to run unguarded\n' >&2
    return 2
  fi

  # Own process group (job control), and the command learns its own pid before it execs.
  set -m
  if [[ -t 0 && "${keep_tty}" -eq 0 ]]; then
    bash -c 'export KW_PROC_GUARD_ROOT_PID=$$; exec "$@"' proc-guard-exec "$@" </dev/null &
  else
    bash -c 'export KW_PROC_GUARD_ROOT_PID=$$; exec "$@"' proc-guard-exec "$@" &
  fi
  pid=$!
  set +m
  export KW_PROC_GUARD_ROOT_PID="${pid}"

  tracked=()
  seen=""
  tripped=""
  trap 'kill_tree "${pid}"; exit 143' INT TERM

  local tree node now kids
  while kill -0 "${pid}" 2>/dev/null; do
    kids="$(descendants_of "${pid}")"
    if [[ -n "${kids}" ]]; then
      # shellcheck disable=SC2206
      tracked=(${kids})
      seen+="${kids}"$'\n'
    fi
    tree=$(printf '%s' "${kids}" | grep -c . || true)
    if ((tree >= max_tree)); then
      tripped="max-tree"
      printf 'proc-guard: --max-tree cap tripped (%s descendants, cap %s); killing the process tree\n' "${tree}" "${max_tree}" >&2
      break
    fi
    node="$(count_node)"
    if ((node >= max_node)); then
      tripped="max-node"
      printf 'proc-guard: --max-node cap tripped (%s node processes, cap %s); killing the process tree\n' "${node}" "${max_node}" >&2
      break
    fi
    if ! now="$(real_now)"; then
      kill_tree "${pid}"
      { wait "${pid}"; } 2>/dev/null || true
      trap - INT TERM
      return 2
    fi
    if ((timeout_s > 0 && now - started_at >= timeout_s)); then
      tripped="timeout"
      printf 'proc-guard: --timeout %ss elapsed; killing the process tree\n' "${timeout_s}" >&2
      break
    fi
    sleep "${POLL_INTERVAL}"
  done

  if [[ -n "${tripped}" ]]; then
    kill_tree "${pid}"
    { wait "${pid}"; } 2>/dev/null || true
    trap - INT TERM
    if [[ "${tripped}" == "timeout" ]]; then
      return "${EXIT_TIMEOUT}"
    fi
    return "${EXIT_CAP}"
  fi

  local rc=0
  { wait "${pid}"; rc=$?; } 2>/dev/null
  trap - INT TERM

  # .NET build servers (VBCSCompiler, MSBuild node reuse) outlive every dotnet build by design.
  # Ask them to exit cleanly first, so only a server that ignores the request is a nuisance.
  local shutdown_attempted=0
  if wraps_dotnet "$@"; then
    shutdown_build_servers
    shutdown_attempted=1
  fi

  # Leftover check: tracked descendants still alive, members of the command's process group,
  # and re-parented processes that carry the repo root and started after this run began.
  local candidates="" linked="" candidate age run_age row
  if ! now="$(real_now)"; then
    # Fail closed: a zero fallback would drop genuinely old orphans and hide survivors.
    trap - INT TERM
    return 2
  fi
  run_age=$((now - started_at))
  for candidate in "${tracked[@]+"${tracked[@]}"}"; do
    if kill -0 "${candidate}" 2>/dev/null; then
      candidates+="${candidate}"$'\n'
      linked+="${candidate}"$'\n'
    fi
  done
  while IFS= read -r candidate; do
    [[ -n "${candidate}" ]] && candidates+="${candidate}"$'\n' && linked+="${candidate}"$'\n'
  done < <(group_members "${pid}" | grep -vxF -f <(protected_pids) || true)
  while IFS= read -r row; do
    [[ -z "${row}" ]] && continue
    candidate="${row%% *}"
    age="$(elapsed_seconds "${candidate}")"
    # Unknown age (age -1) counts as new: the table listed it, so it is a survivor.
    if ((age < 0 || age <= run_age + 2)); then
      candidates+="${candidate}"$'\n'
      # Only a positive link to this run allows a kill: the pid was a descendant at some poll,
      # or it started no earlier than the wrapped command did. Unknown age is reported only.
      if printf '%s\n' "${seen}" | grep -qxF "${candidate}" || ((age >= 0 && age <= run_age)); then
        linked+="${candidate}"$'\n'
      fi
    fi
  done < <(orphans_for_root "${root}")
  candidates="$(printf '%s' "${candidates}" | sort -u | grep . || true)"

  # A wrapped tool (not dotnet itself) may have run dotnet inside: if any candidate is a benign
  # .NET server, ask the servers to exit now and give their clients a moment to follow.
  if ((shutdown_attempted == 0)); then
    while IFS= read -r candidate; do
      [[ -z "${candidate}" ]] && continue
      if is_benign_dotnet_server "$(args_of "${candidate}")"; then
        shutdown_build_servers
        shutdown_attempted=1
        sleep "${KILL_GRACE}"
        break
      fi
    done <<<"${candidates}"
  fi

  # Split into benign .NET servers (only once shutdown was attempted) and real survivors.
  # A candidate with no command line has exited since the scan, so it is not a survivor.
  local survivors="" killable="" benign="" cmdline
  while IFS= read -r candidate; do
    [[ -z "${candidate}" ]] && continue
    cmdline="$(args_of "${candidate}")"
    [[ -z "${cmdline}" ]] && continue
    if ((shutdown_attempted == 1)) && is_benign_dotnet_server "${cmdline}"; then
      benign+="${candidate}"$'\n'
      printf 'proc-guard: note: .NET build server survived shutdown, killed (not a leftover): %s %s\n' "${candidate}" "${cmdline}" >&2
    else
      survivors+="${candidate}"$'\n'
      if printf '%s\n' "${linked}" | grep -qxF "${candidate}"; then
        killable+="${candidate}"$'\n'
      fi
    fi
  done <<<"${candidates}"
  if [[ -n "${benign}" ]]; then
    # shellcheck disable=SC2086
    signal_pids KILL ${benign}
  fi
  survivors="$(printf '%s' "${survivors}" | grep . || true)"

  if [[ -n "${survivors}" ]]; then
    printf 'proc-guard: processes survived the command (pid, command); killing those linked to this run:\n' >&2
    while IFS= read -r candidate; do
      describe_pid "${candidate}" >&2 || true
    done <<<"${survivors}"
    # shellcheck disable=SC2086
    signal_pids TERM ${killable}
    sleep "${KILL_GRACE}"
    # shellcheck disable=SC2086
    signal_pids KILL ${killable}
    kill -KILL -- "-${pid}" 2>/dev/null || true
    if ((rc == 0)); then
      return "${EXIT_LEFTOVERS}"
    fi
  fi
  kill -KILL -- "-${pid}" 2>/dev/null || true
  return "${rc}"
}

main() {
  local mode="${1:-}"
  [[ $# -gt 0 ]] && shift
  case "${mode}" in
    run) run_guarded "$@" ;;
    check-leftovers) check_leftovers "$@" ;;
    *) usage; return 2 ;;
  esac
}

tracked=()
seen=""
tripped=""
main "$@"
exit $?
