---
name: task-gate
description: "The occasional parts of a developer task's exit gate: the 3-cycle cap and its escalation, the whole-project gate caveat, .NET build isolation, and the status words and completion digest format. The two commands themselves are in the worker baseline, and each run prints its own next step and, when it passes, the filled digest. Use when a task gate keeps failing, an escalation is due, or a TASK_GATE status word needs explaining. Do not use when the request reviews a whole branch or pull request rather than one task's exit gate."
license: MIT
metadata:
  author: dpalfery
  version: 0.2.0
---

# Task Gate

The task gate is the exit check a developer agent runs on its own work before it reports a task
done: the review gates declared for the agent in `.kyber-weave/kyber-weave.yml`, run by
`kyber-weave review gates . --agent <self> --task <task-id>`. It is deterministic,
so the result is the gates' output and not the agent's opinion of its change. It covers one task and
never inspects files the task did not change.

The two commands are in the worker baseline. Each run ends with a `next:` line naming the next
step, and the passing run prints the completion digest with the fields it knows filled in. This
skill holds what a run does not print.

## Command

Use the `kyber-weave` command on `PATH`. Inside this checkout, when it is not on `PATH`, use the
source-run form the host's `AGENTS.md` declares — `dotnet run --project <the CLI project> -c Release --`.
With `--task` and no `--paths`, the task's files and the test classes its Test contract names come
from the active plan, and the report goes to `.agent_scratchpad/<task-id>/task-gate.json`. When no
active plan has the task, name the files this task created or modified, one `--paths` per file, and
never another worker's files.

A run with `--fix` applies each gate's fix command first. It never counts: only a run without
`--fix` that reports every declared gate passing is a pass.

## Failing run (test-first)

In a test-first task, after writing the tests and before implementing, run the gate once with
`--red`; its report goes to `.agent_scratchpad/<task-id>/task-gate-red.json`, so the passing run does
not overwrite it. It passes only when tests ran and failed, or when the build failed only on symbols
missing from this task's own files; otherwise it prints `RED_REJECTED: <reason>`. Do not add a stub
to make the tests compile. The two reports, failing run first, are the proof the Arbiter reads; the
digest names only the passing one.

## Fix pass for .NET work

When the task changed C# files, run the gate with `--fix`: it applies the fix form of each format gate
the host declares, scoped to this task's files, and erases the mechanical findings those gates report.
Run no format command a declared gate does not cover.

## Loop and the 3-cycle cap

A cycle is one `--fix` run followed by one verification run. When a gate fails, make the fix its
output calls for and run one cycle. The loop has a 3-cycle cap: after three cycles, stop even when a
gate still fails, return the escalation below, and do not run a fourth.

A gate that cannot run for a reason outside the task is not a failure to fix. Report it as skipped
with its reason, and do not claim a pass for it.

A whole-project gate, one whose argv has no `{changed}` (for example `dash-typecheck` or
`dotnet-build`), can fail on a file this task never touched while another worker is still editing.
Re-run it once after the other change settles, or rebase onto the latest base, before counting the
failure as rework or escalating. Report the re-run beside the `TASK_GATE:` line, not inside its
fixed fields.

## .NET isolation

.NET `dotnet build` task gates run with `-nr:false -p:UseSharedCompilation=false` and a per-task
`--artifacts-path .agent_scratchpad/{task}/artifacts` (or `-p:BaseOutputPath=` /
`-p:BaseIntermediateOutputPath=` naming `{task}` where the command takes no `--artifacts-path`).
Always pass `--task <task-id>` so `{task}` expands; without it the run is refused and no gate
process starts.

## Escalation

When the 3-cycle cap is reached and the gate still fails, return this instead of a pass:

```text
STATUS: ESCALATION
CIRCUIT_BREAKER_TRIGGER: <ITERATION_CAP_EXCEEDED | THRASH_OSCILLATION_DETECTED | INVARIANT_CONTRADICTION | BLAST_RADIUS_EXCEEDED>
FAILURE_CLUSTER: task-gate/<gate-id>
```

The trigger set is closed; this gate raises `ITERATION_CAP_EXCEEDED` when the 3-cycle cap is spent.
Use `task-gate/<gate-id>` with the id of the gate that stayed red. The full failure loop — the
iteration cap, the JEV checkpoints and the payload each trigger carries — is in
[references/rework.md](references/rework.md).

## Digest line

Every developer completion digest carries exactly one `TASK_GATE:` line, in this form:

```text
TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE — agent=<name> gates=<n> failed=<ids|none> skipped=<n> report=<path> runs=<k>
```

- `PASS`: at least one declared gate ran, and every gate that ran passed on the verification run.
- `FAIL`: at least one gate still failed when the loop stopped. Pair it with the escalation.
- `NONE_DECLARED`: no gates are declared for `<name>`. A valid result, not a pass by omission.
- `NONE_APPLICABLE`: gates are declared, but every one was skipped because none applies to this host or change. A valid result, not a pass; a gate set in which every gate is skipped never reports `PASS`.
- `UNAVAILABLE`: the CLI was not found on `PATH` and the fallback could not run. Never a pass.

The fields come from the report at `report=<path>`: `gates` counts declared gates, `failed` lists
failing gate ids or `none`, `skipped` counts gates recorded as skipped, and `runs` counts CLI
executions in this task, including the fix runs. The passing run prints the line filled in except
`runs`.

## Completion digest

The task ends with this digest, the report format every developer worker returns, written here once
so no agent body carries a second copy. The passing run prints it with `STATUS`, `FILES` and the
`TASK_GATE:` line filled in. The fields are fixed and appear in this order, in one contiguous block:

```text
STATUS: PASS | FAIL | ESCALATION
FILES: <comma-separated-paths>
TESTS: total=N failed=N passed=N
COMMIT: <commit-sha>
REMOTE_HEAD: <remote-head-sha | UNPUSHED>
TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE — agent=<name> gates=<n> failed=<ids|none> skipped=<n> report=<path> runs=<k>
```

Lines that are not one of the six digest fields are never counted or rejected as a seventh field. They are exactly these shapes: the Discovery: codegraph N, docs N, direct reads N header; COVERAGE_GAPS: followed by free text; Input sites: N traced, M files with none (or Input sites: none); Defect class: followed by one sentence and the inputs it covers; and Artifact: or Baseline: followed by one repository-relative or absolute path. When the circuit breaker trips, return the escalation in
[references/rework.md](references/rework.md) instead of this digest.

## Boundaries

- Never report a task done without this digest line.
- Never edit the gate declarations to make a gate pass. A gate that fails on the task's own
  files is a finding to fix, not a configuration to change.
