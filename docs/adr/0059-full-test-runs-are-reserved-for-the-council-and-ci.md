---
id: adr/0059-full-test-runs-are-reserved-for-the-council-and-ci
title: Full test runs are reserved for the review council and CI
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-06
code-refs:
  - FullSuiteGate
  - FullSuiteSelection
  - FullSuiteRefusal
---

# ADR 0059: Full test runs are reserved for the review council and CI

## Status

Accepted, 2026-11-06.

## Context

A full test run is the whole .NET suite, the whole `dash/` npm suite, or a bare `cargo test`.
It takes minutes of CPU and memory. Overlapping full runs on one machine slow each other and
make the load-sensitive test classes flaky ([ADR 0047](0047-kyberdash-self-spawns-through-one-guarded-chokepoint.md)).
A developer agent needs the tests that cover its change, not the whole suite. A prose rule
cannot stop a run, so the rule is enforced in code.

## Decision

1. **A full test run is reserved for the review council and CI.** A developer agent runs only
   the tests that cover its change.
2. **Three guard layers share one definition of a full run.**
   - **A. `scripts/proc-guard.sh`.** `classify -- <argv>` prints `full` or `partial` and starts
     nothing. `run` refuses a full run with exit 89 before it waits for a test slot and before
     it starts anything, unless `--council` is given or stdin is an interactive terminal. The
     wrapper consumes `--council` and does not forward it to the command.
   - **B. `kyber-weave review gates --council`.** A gate that declares `full-suite: true` in
     `.kyber-weave/kyber-weave.yml` is refused unless `--council` is given or stdin is an
     interactive terminal. A refused gate is a blocking failure: diagnostic `KW-REVIEW-022`
     (Error), the command exits 1, and the other gates still run. The `--agent` task gates are
     not affected. The rule lives in `FullSuiteGate`
     (`src/KyberWeave.Core/Configuration/FullSuiteGate.cs`), and the command applies it in
     `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs`.
   - **C. `scripts/fast-loop.sh`.** `--full`, `--scoped` and `--regenerate` exit 89 before any
     build when stdin is not a terminal. This script has no `--council` flag.
3. **The full-run definition.** The classifier first strips leading `env`, `timeout`, `nice`,
   `time`, `command` and `exec` prefixes. Then:
   - `dotnet test`, and the direct test runner dll, is full unless a class, method, trait or
     filter option narrows it, or it is aimed at another project. The full targets are no
     target, a solution file, a directory, and the `KyberWeave.Tests` project or dll.
   - The `dash` npm test script is full when no path follows `--`.
   - A bare `cargo test`, with no positional filter, is full.
   - Ambiguous input counts as full. A wrongly refused partial run is recoverable; a wrongly
     admitted full run is not.
4. **Caller identity is a flag or a terminal.** A caller is the review council when it passes
   `--council`, or a person when stdin is an interactive terminal. Both are flags or
   terminal checks. Neither is an environment variable.
5. **The shipped gates carry the key.** The `test`, `ts-test` and `cargo-test` gates in
   `.kyber-weave/kyber-weave.yml` set `full-suite: true`. The CI review gate template passes
   `--council`. A whole-suite gate without the key runs unrefused, so a new one must set it.

## Alternatives Considered

- **A prose rule only.** Rejected. A written rule cannot stop a run; only a refusal in the
  command path does.
- **An environment variable that marks the council.** Rejected. Every child process inherits
  it, and an agent can set it.
- **Routing at the Arbiter ([ADR 0053](0053-kyber-arbiter-three-step-decision-gates.md)).**
  Rejected. The Arbiter gates agent dispatch, not the shell, so it cannot see the commands a
  worker runs.
- **A per-harness shell hook that runs the classifier before every command.** This is the
  strongest closure, but each harness has its own hook mechanism, so it is deferred. The
  follow-up is tracked in
  [the per-harness shell hook todo](../todo/per-harness-shell-hook-for-full-run-guard.md).

## Known Limits

- A shell with a pseudo-terminal passes the terminal test.
- Typing `--council` passes the guard.
- A raw `dotnet test` run that bypasses `proc-guard.sh` is not seen.
- `sh -c` and `env -S` command strings are not parsed.
- Other launchers (`nohup`, `caffeinate`, `sudo`, absolute-path wrappers) are not unwrapped.
- `dotnet run --project <test project>` and `npm --prefix <absolute path> run test` classify as
  partial.
- A directory target is treated as full, so some partial runs are refused.
- The default path of `scripts/fast-loop.sh` runs an unfiltered `cargo test` of the tray crate
  for a tray Rust change. It does not go through `proc-guard.sh`, so no refusal applies. Tracked in
  [the fast-loop tray cargo test todo](../todo/fast-loop-default-tray-cargo-test.md).

## Release Ordering

The CLI, `scripts/proc-guard.sh`, the `full-suite` configuration key and
`templates/github-actions/kyber-weave-review-gate.yml` ship together. An older CLI, such as
`0.1.8-rc.19`, warns `KW-CONFIG-002` on the `full-suite` key and does not know `--council`. A host
that pins an older CLI must update it before its council can run the guarded gates.

## Consequences

- A developer agent that needs more than its targeted tests asks the council or CI to run the
  full suite; it does not run it itself.
- Host configurations generated by `kyber-weave docs init` do not yet emit `full-suite` on their
  whole-suite gates. Tracked in
  [the docs init full-suite key todo](../todo/docs-init-emit-full-suite-gate-key.md).
