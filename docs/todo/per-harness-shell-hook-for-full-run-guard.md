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

**Parked classifier gap.**

- **Unknown value-taking flag before the subcommand (Minor, bypass).** `cargo --some-flag value test`
  and `npm --some-flag value test` classify partial, expected full, when the flag is not in the
  classifier's known list. The unknown flag's value is read as the subcommand. Fix: a generic rule for
  the value of unknown global flags before the subcommand, or refuse an unrecognised shape.

**Deliberate choices, not gaps.** These classify full by design and stay refused unless the caller
passes `--council` or runs from a terminal: a directory target, `npm test --workspace`, `cargo test -p`
(and `--package`), any npm `--prefix` directory on a test script, and a forward scan after a launcher
that finds a `dotnet`, `npm` or `cargo` word after an unknown word (for example `sudo echo please run
dotnet test`). A named class (`-class`) also recovers a dotnet run.

The real closure is still the shell hook.

**Acceptance.** On each listed harness, a developer agent's full-suite command is refused before the
shell runs it, and the refusal names a targeted alternative. Each listed limit has a failing row first
and then passes, or is recorded as still open. The ADR 0059 known-limits list is updated to match.
