---
id: adr/0065-the-self-updater-hands-everything-after-the-swap-to-the-new-binary
title: The self-updater hands everything after the swap to the new binary
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-09
component: Distribution
decided-by: []
supersedes: []
code-refs:
  - SelfUpdater
---

# ADR 0065: The self-updater hands everything after the swap to the new binary

## Status

Accepted, 2026-11-08.

## Context

`kyber-weave update` replaces its own single-file program. A running process cannot load an
assembly it has not loaded yet from a file that has been replaced under it, so the steps that
ran after the swap in the old process could fail for that reason. The arbiter hook refresh
crashed with `Could not load file or assembly YamlDotNet` and exited 1 although every binary
was installed. The same process also rendered hooks from the old release's templates, because
it was the old release's code that ran.

## Decision

After the swap, the updating process runs nothing but the hand-off. It starts the installed
kyber-weave as `kyber-weave update <version> --finish-update` and relays that process's exit
code. The finishing process runs the KyberDash steps and the arbiter hook refresh in its own
image, downloads nothing, and refuses to run unless its version equals the requested one. The
updating process prints the one outcome line on success.

## Alternatives considered

- **Move the post-swap work before the swap.** The refresh would render the old release's hook
  source, the CLI would stay un-swapped when a KyberDash step fails, and rollback of a swap-last
  update would break.
- **Pre-load every referenced assembly in the old process.** The stale-template defect remains,
  and the approach depends on a runtime loading mechanism that cannot be enumerated in advance.
- **Make the refresh failure non-fatal only.** Hooks would stay stale after an update reported
  success. This is kept as the backstop: any failure after the install still exits 1 and names
  the command that finishes the update.
- **Let the new binary perform the swap.** This is a larger change, and the old process would
  still have to spawn it.

## Consequences

- Any new post-update step belongs in the finishing process, never in the updating process.
- The hand-off adds one process start and a 15-minute bound that stops the whole process tree
  on expiry. Before the first commit, the old process rehearses the start path once with
  `--version` under a 30-second bound, so the code that starts the hand-off is loaded while the
  old image is still whole.
- A pinned older release that does not know `--finish-update` fails the hand-off. The message
  then gives the pinned retry, `kyber-weave update <version>`.
- The first update away from a build that predates this decision is still run by that old
  build's updater, so it can show the old error once. The binaries are installed; running
  `kyber-weave update` once more refreshes the hooks.
