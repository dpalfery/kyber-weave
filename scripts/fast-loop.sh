#!/usr/bin/env bash
# The fast local inner loop. Answers "did this edit break something" in seconds.
#
#   scripts/fast-loop.sh                  # component-aware fast checks based on changed files
#   scripts/fast-loop.sh --touched        # only the unit test classes that name a changed file's types
#   scripts/fast-loop.sh --class 'KyberWeave.Tests.Area*'  # one area (.NET); the pattern matches the full name
#   scripts/fast-loop.sh --method <FQN>   # one test (.NET)
#   scripts/fast-loop.sh --full           # the declared gate suite, all gates (--all); not for dev agents
#   scripts/fast-loop.sh --scoped         # declared gate suite scoped to changed files; not for dev agents
#
# WHY THIS IS NOT A THINNER `review gates`
#
# ADR 0005 already separated "did this edit break something" from "may the change be
# committed", but only on the reviewer side. It rejected a gate subset outright, for a
# reason that still holds: a review citing a hand-picked subset is citing evidence it
# selected. So the declared suite stays whole, and this script is a different instrument
# that must never be cited as review evidence. `--full` runs the real thing.
#
# THE UNIT TESTS
#
# Every unit test project (Application, Hooks, Kernel, Mcp, Arbiter, Cli, Data), the architecture
# test and the content tests is in memory, so each runs on every .NET loop, all at the same time.
# They run whole unless --class, --method or --touched hands each of them a filter, which narrows
# the run to the classes it names, whichever project they sit in.
#
# tests/KyberWeave.Integration.Tests holds the tests that perform a real Squad deployment or start
# a real program. This script never runs them (requirement 7.3): CI's "Integration tests" job does.
# --touched only reports that an integration class was touched.
#
# THE REPOSITORY-POLICY TESTS
#
# tests/KyberWeave.RepoPolicy.Tests holds the tests whose subject is the repository itself:
# scripts, workflows, gate configuration, docs and source scans. They run here only when a
# changed file is something other than C# under src/ or a test project, with the same
# filter as the unit tests. A C#-only edit runs only the policy tests that read source
# text (POLICY_SOURCE_SCANS): the rest read scripts, workflows, project files and docs.
#
# EVERY COMMAND RUNS DIRECTLY
#
# .NET test, build, and format steps run via `dotnet` so Roslyn/MSBuild build servers stay warm.
# KyberDash (`dash/`) and tray UI (`dash/tray/ui`) commands run via `npm --prefix ...` directly:
# KyberDash does its work in-process, so there are no recursive Node spawners to wrap or reap.
# The full-suite refusal below is this script's own: it needs no wrapper to enforce.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${repo_root}"

CONFIGURATION=Release
POLICY_TEST_DLL="tests/KyberWeave.RepoPolicy.Tests/bin/${CONFIGURATION}/net10.0/KyberWeave.RepoPolicy.Tests.dll"
# The unit projects (Application, Hooks, Kernel, Mcp, Arbiter, Cli, Data, Architecture) and the content tests: pure or
# fake-backed, so they run on every .NET loop, whole unless a filter narrows them.
UNIT_TEST_DLLS=(
  "tests/KyberWeave.Application.Tests/bin/${CONFIGURATION}/net10.0/KyberWeave.Application.Tests.dll"
  "tests/KyberWeave.Hooks.Tests/bin/${CONFIGURATION}/net10.0/KyberWeave.Hooks.Tests.dll"
  "tests/KyberWeave.Kernel.Tests/bin/${CONFIGURATION}/net10.0/KyberWeave.Kernel.Tests.dll"
  "tests/KyberWeave.Mcp.Tests/bin/${CONFIGURATION}/net10.0/KyberWeave.Mcp.Tests.dll"
  "tests/KyberWeave.Arbiter.Tests/bin/${CONFIGURATION}/net10.0/KyberWeave.Arbiter.Tests.dll"
  "tests/KyberWeave.Cli.Tests/bin/${CONFIGURATION}/net10.0/KyberWeave.Cli.Tests.dll"
  "tests/KyberWeave.Data.Tests/bin/${CONFIGURATION}/net10.0/KyberWeave.Data.Tests.dll"
  "tests/KyberWeave.Architecture.Tests/bin/${CONFIGURATION}/net10.0/KyberWeave.Architecture.Tests.dll"
  "tests/KyberWeave.Content.Tests/bin/${CONFIGURATION}/net10.0/KyberWeave.Content.Tests.dll"
)
# The test projects --touched maps a changed type to. The integration project is listed so a touched integration
# class is reported, never run.
TOUCHED_TEST_ROOTS=(
  tests/KyberWeave.Application.Tests tests/KyberWeave.Hooks.Tests tests/KyberWeave.Kernel.Tests tests/KyberWeave.Mcp.Tests
  tests/KyberWeave.Arbiter.Tests tests/KyberWeave.Cli.Tests tests/KyberWeave.Data.Tests tests/KyberWeave.Architecture.Tests
  tests/KyberWeave.Content.Tests tests/KyberWeave.Integration.Tests
)
# The policy tests that read source text. A C#-only edit can trip them (an unlisted process
# spawn, a banned upstream name, ambient I/O in Domain), and they only read files, so they run
# on every .NET loop.
POLICY_SOURCE_SCANS=(-class KyberWeave.Tests.ProcessSpawnSitesTests -class KyberWeave.Tests.UpstreamSeveranceTests -class KyberWeave.Tests.DomainPurityTests -class KyberWeave.Tests.ReviewDiagnosticCodeUniquenessTests)

step() { printf '\n=== %s ===\n' "$1"; }

test_log=""
scan_log=""
cleanup() {
  if [[ -n "${test_log:-}" && -f "${test_log}" ]]; then
    rm -f "${test_log}"
  fi
  if [[ -n "${scan_log:-}" && -f "${scan_log}" ]]; then
    rm -f "${scan_log}"
  fi
}
trap cleanup EXIT

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  cat <<'EOF'
Usage: scripts/fast-loop.sh [OPTIONS]

Fast local inner loop for Kyber-Weave developers.

Options:
  (no options)     Component-aware fast checks based on changed workspace files.
  --touched        Run only the unit test classes that mention a type declared in a changed
                   C# file, or that sit in a changed test file. Falls back to every unit project
                   whole when nothing matches or more than 40 classes match.
  --class PATTERN  Run .NET tests whose fully qualified class name matches
                   (KyberWeave.Tests.Area*). A bare Area* matches nothing.
  --method FQN     Run one specific .NET test method.
  --full           Run all declared repository gates unscoped (--all).
                   Includes the full test suite: not for dev agents.
  --scoped         Run declared repository gates scoped to changed files.
                   Runs the full product suite on a .NET change: not for dev agents.
  --help, -h       Show this help message.

--full and --scoped exit 89 without running anything when stdin is not an
interactive terminal (a dev agent's shell). Run them from a terminal, or use --class / --method.
EOF
  exit 0
fi

# The full-suite modes belong to the review council and CI. A non-interactive caller (a dev agent)
# is refused with exit 89 before any build or other work; this script is the only guard the modes need.
refuse_full_suite_mode() {
  if [[ -t 0 ]]; then
    return 0
  fi
  printf 'fast-loop: refused: %s is a full test-suite run. Only the review council and CI run the full suite;\n' "$1" >&2
  printf 'fast-loop: dev agents run targeted sets instead. Use one of these:\n' >&2
  printf "fast-loop:   scripts/fast-loop.sh --class 'KyberWeave.Tests.<Area>*'\n" >&2
  printf 'fast-loop:   scripts/fast-loop.sh --method <FQN>\n' >&2
  printf 'fast-loop:   kyber-weave review gates . --agent <name>\n' >&2
  printf 'fast-loop: to run the full suite on purpose from a terminal, run this script there directly.\n' >&2
  exit 89
}

case "${1:-}" in
  --full|--scoped) refuse_full_suite_mode "$1" ;;
esac

# --full keeps the declared suite whole. It is the merge question, and it runs everything.
if [[ "${1:-}" == "--full" ]]; then
  step "full declared gate suite"
  exec dotnet run --project src/KyberWeave.Cli -c "${CONFIGURATION}" -- \
    review gates . --all --out artifacts/gates.json
fi

# --scoped runs declared repository gates scoped to changed files against base.
if [[ "${1:-}" == "--scoped" ]]; then
  step "scoped declared gate suite"
  exec dotnet run --project src/KyberWeave.Cli -c "${CONFIGURATION}" -- \
    review gates . --out artifacts/gates.json
fi

# Parse and validate arguments before the build so a typo fails before the (slower) build.
test_args=()
touched=0
case "${1:-}" in
  --touched) touched=1 ;;
  --class)  test_args+=(-class "${2:?--class needs a pattern}") ;;
  --method) test_args+=(-method "${2:?--method needs a fully-qualified name}") ;;
  "") ;;
  *) printf 'unknown option: %s\n' "$1" >&2; exit 2 ;;
esac

# Component routing: if --class or --method was supplied, focus on .NET and docs validate.
# Otherwise, inspect changed workspace paths to run targeted fast checks.
changed_cs=()
needs_dotnet=0
needs_dash_server=0
needs_dash=0
needs_tray_ui=0
needs_rust=0
needs_docs=0
needs_policy=0
dash_related_files=()

if [[ ${#test_args[@]} -gt 0 ]]; then
  needs_dotnet=1
  needs_docs=1
  needs_policy=1
else
  changed_files=()
  while IFS= read -r file || [[ -n "${file}" ]]; do
    [[ -z "${file}" ]] && continue
    changed_files+=("${file}")
  done < <({ git diff --name-only HEAD 2>/dev/null; git diff --name-only --cached 2>/dev/null; git ls-files --others --exclude-standard 2>/dev/null; } | sort -u)

  changed_cs=()
  for file in "${changed_files[@]+"${changed_files[@]}"}"; do
    if [[ "${file}" == *.cs && -f "${file}" ]]; then
      changed_cs+=("${file}")
    fi
  done

  if [[ ${#changed_files[@]} -eq 0 ]]; then
    needs_dotnet=1
    needs_docs=1
    needs_policy=1
  else
    for file in "${changed_files[@]+"${changed_files[@]}"}"; do
      case "$file" in
        *.md) needs_docs=1 ;;
      esac

      case "$file" in
        dash/tray/src-tauri/*)
          needs_rust=1
          ;;
        dash/tray/ui/*)
          needs_tray_ui=1
          needs_dash=1
          ;;
        dash/server/* | dash/server.tests/*)
          needs_dash_server=1
          ;;
        dash/*.md | docs/*)
          needs_docs=1
          ;;
        dash/*)
          needs_dash=1
          if [[ -f "$file" ]]; then
            case "$file" in
              *.ts|*.tsx|*.js|*.jsx|*.mjs|*.cjs)
                dash_related_files+=("${file#dash/}")
                ;;
            esac
          fi
          ;;
        README.md | CHANGELOG.md | CONTRIBUTING.md | SECURITY.md)
          needs_docs=1
          ;;
        *)
          needs_dotnet=1
          case "$file" in
            *.md | .kyber-weave/* | src/*)
              needs_docs=1
              ;;
          esac
          # A C#-only edit to product code or product tests runs only the policy source scans:
          # the rest of the policy tests read scripts, workflows, project files and docs.
          case "$file" in
            src/*.cs | tests/KyberWeave.Integration.Tests/*.cs | tests/KyberWeave.Testing/*.cs | tests/KyberWeave.Kernel.Tests/*.cs | tests/KyberWeave.Mcp.Tests/*.cs | tests/KyberWeave.Arbiter.Tests/*.cs | tests/KyberWeave.Cli.Tests/*.cs | tests/KyberWeave.Data.Tests/*.cs) ;;
            *) needs_policy=1 ;;
          esac
          ;;
      esac
    done
  fi
fi

if [[ "${needs_dotnet}" -eq 1 ]]; then
  step "build (Release, warnings-as-errors, AnalysisMode=all)"
  dotnet build KyberWeave.sln -c "${CONFIGURATION}"

  if [[ "${touched}" -eq 1 && ${#test_args[@]} -eq 0 ]]; then
    # A changed file's own types, found by name in the test sources. It is a heuristic over text, not a
    # call graph: it can miss a test that reaches the type only indirectly, which is why CI still runs everything.
    touched_types=()
    for file in "${changed_cs[@]+"${changed_cs[@]}"}"; do
      while IFS= read -r type_name; do
        touched_types+=("${type_name}")
      done < <(grep -oE '^[[:space:]]*((public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe|new)[[:space:]]+)*(class|record([[:space:]]+(class|struct))?|struct|interface|enum)[[:space:]]+[A-Za-z0-9_]+' "${file}" | awk '{print $NF}')
    done
    touched_files=()
    for file in "${changed_cs[@]+"${changed_cs[@]}"}"; do
      for root in "${TOUCHED_TEST_ROOTS[@]}"; do
        case "${file}" in
          "${root}"/*.cs) touched_files+=("${file}") ;;
        esac
      done
    done
    for type_name in "${touched_types[@]+"${touched_types[@]}"}"; do
      for root in "${TOUCHED_TEST_ROOTS[@]}"; do
        while IFS= read -r file; do
          touched_files+=("${file}")
        done < <(grep -rlwF --include='*.cs' -- "${type_name}" "${root}" 2>/dev/null)
      done
    done
    touched_classes=()
    integration_classes=()
    while IFS= read -r file; do
      [[ -z "${file}" ]] && continue
      namespace_name="$(sed -nE 's/^[[:space:]]*namespace[[:space:]]+([A-Za-z0-9_.]+).*/\1/p' "${file}" | head -1)"
      [[ -z "${namespace_name}" ]] && namespace_name="KyberWeave.Tests"
      while IFS= read -r class_name; do
        if [[ "${file}" == tests/KyberWeave.Integration.Tests/*.cs ]]; then
          integration_classes+=("${namespace_name}.${class_name}")
        else
          touched_classes+=("${namespace_name}.${class_name}")
        fi
      done < <(grep -oE '^[[:space:]]*((public|internal)[[:space:]]+)((static|sealed|abstract|partial|readonly|ref|unsafe|new)[[:space:]]+)*(class|record([[:space:]]+(class|struct))?)[[:space:]]+[A-Za-z0-9_]+' "${file}" | awk '{print $NF}')
    done < <(printf '%s\n' "${touched_files[@]+"${touched_files[@]}"}" | sort -u)
    # Integration classes are never run here (requirement 7.3): CI's "Integration tests" job runs them.
    if [[ ${#integration_classes[@]} -gt 0 ]]; then
      printf 'fast-loop: integration classes touched (%s): CI runs them.\n' \
        "$(printf '%s\n' "${integration_classes[@]}" | sort -u | wc -l | tr -d ' ')" >&2
    fi
    touched_total=$(printf '%s\n' "${touched_classes[@]+"${touched_classes[@]}"}" | sort -u | grep -c . || true)
    if [[ ${#touched_classes[@]} -gt 0 && ${touched_total} -le 40 ]]; then
      while IFS= read -r class; do
        test_args+=(-class "${class}")
      done < <(printf '%s\n' "${touched_classes[@]}" | sort -u)
      step "tests (touched: $(printf '%s\n' "${touched_classes[@]}" | sort -u | wc -l | tr -d ' ') classes)"
    elif [[ ${#touched_classes[@]} -gt 0 ]]; then
      printf 'fast-loop: --touched found %s test classes; running every unit project whole instead.\n' "${touched_total}" >&2
    fi
  fi

  if [[ ${#test_args[@]} -eq 0 ]]; then
    # The scale envelope is a size-stress test, not an edit check; keep it out of the loop.
    test_args+=(-trait- "Category=Scale")
    step "tests (every unit project, whole)"
  else
    step "tests (filtered)"
  fi

  # A filter that names no test class runs zero tests and exits 0, which reads as a pass. The runner's
  # own total is the only evidence a filter matched, so read it from the unit logs themselves. The policy
  # project can run with the same filter and report a total even when every unit project matched nothing,
  # which is why the unit totals are summed here and the combined log is only a fallback for a whole run.
  has_test_filter=0
  for arg in "${test_args[@]+"${test_args[@]}"}"; do
    case "${arg}" in
      -class|-method) has_test_filter=1 ;;
    esac
  done
  # Every run always happens, so a unit failure cannot hide the policy results; the loop
  # fails after all of them, naming each exit status. The projects are independent and small, so the
  # unit projects run at the same time. Each is its own background process and its status comes back
  # through `wait <pid>`: a variable set inside a backgrounded command never reaches this shell, which
  # is how a failing project would otherwise read as a pass.
  test_log="$(mktemp "${TMPDIR:-/tmp}/kw-fast-loop.XXXXXX")"
  unit_status=0
  policy_status=0
  unit_total=0
  unit_pids=()
  unit_logs=()
  for unit_dll in "${UNIT_TEST_DLLS[@]}"; do
    unit_log="$(mktemp "${TMPDIR:-/tmp}/kw-fast-loop-unit.XXXXXX")"
    unit_logs+=("${unit_log}")
    dotnet "${unit_dll}" "${test_args[@]+"${test_args[@]}"}" >"${unit_log}" 2>&1 &
    unit_pids+=($!)
  done
  for index in "${!unit_pids[@]}"; do
    unit_exit=0
    wait "${unit_pids[${index}]}" || unit_exit=$?
    cat "${unit_logs[${index}]}" | tee -a "${test_log}"
    unit_run_total="$(sed $'s/\x1b\\[[0-9;]*m//g' "${unit_logs[${index}]}" | grep -oE 'Total: [0-9]+' | head -1 | grep -oE '[0-9]+' || true)"
    unit_total=$(( unit_total + ${unit_run_total:-0} ))
    rm -f "${unit_logs[${index}]}"
    if [[ "${unit_exit}" -ne 0 ]]; then
      unit_status="${unit_exit}"
    fi
  done
  policy_total=0
  if [[ "${needs_policy}" -eq 1 ]]; then
    step "repository-policy tests (same filter)"
    policy_log="$(mktemp "${TMPDIR:-/tmp}/kw-fast-loop-policy.XXXXXX")"
    dotnet "${POLICY_TEST_DLL}" "${test_args[@]+"${test_args[@]}"}" >"${policy_log}" 2>&1 || policy_status=$?
    cat "${policy_log}" | tee -a "${test_log}"
    policy_total="$(sed $'s/\x1b\\[[0-9;]*m//g' "${policy_log}" | grep -oE 'Total: [0-9]+' | head -1 | grep -oE '[0-9]+' || true)"
    rm -f "${policy_log}"
  else
    # Checked on its own: the unit total must not stand in for a scan run that matched nothing.
    step "repository-policy source scans"
    scan_log="$(mktemp "${TMPDIR:-/tmp}/kw-fast-loop-scans.XXXXXX")"
    dotnet "${POLICY_TEST_DLL}" "${POLICY_SOURCE_SCANS[@]}" | tee "${scan_log}" || policy_status=$?
    # A scan run that matched nothing is a policy failure, reported with the unit status below.
    if [[ "${policy_status}" -eq 0 ]] && ! sed $'s/\x1b\\[[0-9;]*m//g' "${scan_log}" | grep -Eq 'Total: [1-9][0-9]*,'; then
      printf 'the policy source scans ran no tests; check POLICY_SOURCE_SCANS\n' >&2
      policy_status=1
    fi
  fi
  if [[ "${unit_status}" -ne 0 || "${policy_status}" -ne 0 ]]; then
    printf '\ntests failed: unit projects exit %s, repository-policy tests exit %s\n' \
      "${unit_status}" "${policy_status}" >&2
    exit $(( unit_status != 0 ? unit_status : policy_status ))
  fi
  if [[ "${has_test_filter}" -eq 1 ]]; then
    if [[ "${touched}" -eq 1 && "${unit_total}" -eq 0 ]]; then
      printf 'no unit tests ran; the filter matched nothing:\n' >&2
      for ((index = 0; index < ${#test_args[@]}; index += 2)); do
        printf '  %s %s\n' "${test_args[${index}]}" "${test_args[$((index + 1))]}" >&2
      done
      exit 1
    fi
    # An explicit --class / --method may select only repository-policy tests, so the two runs are counted together.
    if [[ "${touched}" -eq 0 && $(( unit_total + policy_total )) -eq 0 ]]; then
      printf 'no unit or repository-policy tests ran; the filter matched nothing:\n' >&2
      for ((index = 0; index < ${#test_args[@]}; index += 2)); do
        printf '  %s %s\n' "${test_args[${index}]}" "${test_args[$((index + 1))]}" >&2
      done
      exit 1
    fi
  fi
  if ! sed $'s/\x1b\\[[0-9;]*m//g' "${test_log}" | grep -Eq 'Total: [1-9][0-9]*,'; then
    printf 'no tests ran; check the --class / --method filter\n' >&2
    exit 1
  fi

  # Format and style are checked on the changed C# files only: the formatter loads and analyses the whole
  # solution either way, but verifying every file costs about a minute that a loop does not need. With no
  # changed file to name (a clean tree, or --class / --method), the whole solution is checked as before.
  # CI checks the whole solution, so a rule that reaches across files is still caught there.
  format_scope=()
  if [[ ${#changed_cs[@]} -gt 0 ]]; then
    format_scope=(--include "${changed_cs[@]}")
  fi
  step "format (whitespace${format_scope:+, changed files})"
  dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal "${format_scope[@]+"${format_scope[@]}"}"
  step "format (style${format_scope:+, changed files})"
  dotnet format KyberWeave.sln style --verify-no-changes --no-restore -v minimal "${format_scope[@]+"${format_scope[@]}"}"
fi

if [[ "${needs_dash_server}" -eq 1 ]]; then
  step "kyberdash server build (Release, warnings-as-errors)"
  dotnet build dash/server/KyberDash.sln -c "${CONFIGURATION}"

  step "kyberdash server format (whitespace)"
  dotnet format dash/server/KyberDash.sln whitespace --verify-no-changes --no-restore -v minimal

  step "kyberdash server tests"
  dotnet test dash/server/KyberDash.sln -c "${CONFIGURATION}" --no-build
fi

if [[ "${needs_docs}" -eq 1 ]]; then
  step "docs validate (no CodeGraph index required)"
  if [[ "${needs_dotnet}" -eq 0 && ! -f "src/KyberWeave.Cli/bin/${CONFIGURATION}/net10.0/kyber-weave.dll" ]]; then
    step "build CLI for docs validate"
    dotnet build src/KyberWeave.Cli -c "${CONFIGURATION}"
  fi
  dotnet run --project src/KyberWeave.Cli -c "${CONFIGURATION}" --no-build -- \
    docs validate .
fi

if [[ "${needs_dash}" -eq 1 ]]; then
  step "dash typecheck"
  npm --prefix dash run typecheck

  step "dash lint"
  npm --prefix dash run lint

  if [[ ${#dash_related_files[@]} -gt 0 ]]; then
    step "dash tests (related: ${#dash_related_files[@]} files)"
    npm --prefix dash run test:related -- "${dash_related_files[@]}"
  fi

  if [[ "${needs_tray_ui}" -eq 1 ]]; then
    if [[ ! -d "dash/tray/ui/node_modules" ]]; then
      printf '\nError: dash/tray/ui/node_modules is missing. Run: npm --prefix dash/tray/ui ci\n' >&2
      exit 1
    fi
    step "tray ui typecheck"
    npm --prefix dash/tray/ui run typecheck

    step "tray ui test"
    npm --prefix dash/tray/ui run test
  fi
fi

if [[ "${needs_rust}" -eq 1 ]]; then
  step "tray rust (fmt, clippy, test)"
  (cd dash/tray/src-tauri && cargo fmt --check && cargo clippy -- -D warnings && cargo test)
fi

printf '\nFast tier clean. Push; CI runs the full suite.\n'
