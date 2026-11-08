---
id: todo/run-start-command-undocumented
title: The run-start command is documented nowhere
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-07
---

# The run-start command is documented nowhere

Two decisions in the conductor run-speed work describe a command that no canonical page carries:
`kyber-weave run start --harness <token> --type <type> --slug <slug> [--resume <branch-or-pr>]
[--failing-version <version>] [--json]`, which fetches origin, branches from the remote base
branch without checking out `main`, reports its own exit codes (`0` ready, `1` tool failure under
the stop rule, `2` refused input, `3` the CodeGraph binary missing) and, where the conductor has no
shell, is dispatched to `github-devops` with the exact command and a `RUN-START: true` marker.

The command is delivered by the run-start slice, whose pull request is still open. This branch
carries none of it: `src/KyberWeave.Core/RunStart/` does not exist here and
[the CLI command surface](../cli-command-surface.md) documents no `run start` verb. So the two
decisions — one on where a run starts from, one on the command itself — have no destination in this
tree, and writing them into the command surface from here would document a verb this repository does
not ship.

Close it in the slice that ships the command: add the verb, its flags and its exit-code rule to
[the CLI command surface](../cli-command-surface.md), and give the conductor's run-start step the
command it now names. If the surface is deliberately deferred further, say where the rule lives
until then rather than leaving it in a plan nobody reads.

This todo is complete when `run start` appears in the CLI command surface with the exit-code
contract, and no live plan is the only place its rules are stated.
