---
id: todo/per-harness-shell-hook-for-full-run-guard
title: Close the full-run guard with a shell hook in each harness
doc-type: todo
component: ReviewCouncil
status: draft
owner: dpalfery
last-reviewed: 2026-11-06
---

# Close the full-run guard with a shell hook in each harness

[ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) refuses a full test
run through `scripts/proc-guard.sh`, but only when the command is started through that wrapper and
the caller is not a terminal or the council. A raw `dotnet test` typed into a harness's shell
bypasses it. The strongest closure is a shell hook in each harness that runs
`scripts/proc-guard.sh classify -- <argv>` before every shell command and refuses a `full` result.

**Work.** Add a pre-tool-use style hook for each harness the Squad target catalogue lists
(`src/KyberWeave.Core/Squad/Deployment/SquadTarget.cs`), which includes Claude Code, Copilot CLI,
Factory, Pi, OpenCode and Antigravity. The hook replaces the soft caller test (the terminal check)
with the classifier's answer. Each harness has its own hook format, so the shape of each hook is a
per-harness decision and is not assumed here.

**Limits the hook should replace.** The classifier in `scripts/proc-guard.sh` cannot see these
today. The hook should refuse them by its own rule, so each harness's shell is covered:

- A pseudo-terminal shell passes the terminal test, and a typed `--council` passes the guard. The
  hook replaces the terminal check, so neither flag nor terminal is the caller's proof.
- A raw `dotnet test` run that skips the wrapper is not seen by the classifier at all.
- `sh -c`, `bash -c` and `env -S` command strings are not parsed.
- A launcher outside the classifier's list is not unwrapped. Its command is classified only when a
  `dotnet`, `npm` or `cargo` word follows it.
- Other test runners are not classified: `dotnet vstest`, `npx vitest` and `cargo nextest`.
- `scripts/fast-loop.sh` with no arguments runs an unfiltered `cargo test` of the tray crate, outside
  the wrapper ([fast-loop todo](fast-loop-default-tray-cargo-test.md)).

Deliberate over-refusals (a directory target, `npm test --workspace`, `cargo test -p`, a scan that
finds a test word after an unknown word) are not gaps. Keep them refused unless a named class is given.
F4 and F5 below are false refusals that are not deliberate.

**Pass-3 classifier gaps, parked.** A read-only re-check of the classifier after pass 3 found these.
No fix pass is scheduled. Reproduced 2026-11-06 with `scripts/proc-guard.sh classify -- <argv>` run
directly. Under `bash -c '...'` every case reads partial, because shell strings are not parsed, so the
direct form is the test.

- **F1 (Major, bypass).** `cargo -C dash/tray/src-tauri test`: actual partial, expected full. `-C <dir>`
  is a cargo global flag missing from the value-taking list in `is_full_cargo`, so the directory is
  read as the subcommand. Fix: add `-C` to that list.
- **F2 (Major, false refusal).** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj
  --treenode-filter '/*/*/FastLoop*/*'`: actual full, expected partial. `is_filter_flag` knows only
  `-treenode-filter`. Fix: add `--treenode-filter` and `--treenode-filter=`.
- **F3 (Minor, bypass).** `cargo --artifact-dir out test`, `npm --omit dev test`, `npm --tag x test`,
  `npm --otp 123 test`: actual partial, expected full. Unknown value-taking global flags before the
  subcommand hide it. Fix: skip the value of known global flags before the subcommand.
- **F4 (Minor, false refusal).** `cargo test --test smoke` (also `--bin`, `--example`, `--bench`):
  actual full, expected partial. These select one target. Fix: treat them as narrowing.
- **F5 (Minor, false refusal).** `dotnet test --artifacts-path out <other project>`: actual full,
  expected partial. `--artifacts-path` is missing from `dotnet_flag_takes_value`. Fix: add it.
- **F6 (Minor, ambiguity).** `dotnet run --project tests/KyberWeave.Tests --project src/KyberWeave.Cli`:
  actual partial, expected full. The last `--project` wins. Fix: treat two different projects as
  ambiguous, so full.

A one-line fix for F1 and F2 is cheap and optional. The real closure is still the shell hook.

**Acceptance.** On each listed harness, a developer agent's full-suite command is refused before the
shell runs it, and the refusal names a targeted alternative. Each listed limit has a failing row first
and then passes, or is recorded as still open. The ADR 0059 known-limits list is updated to match.
