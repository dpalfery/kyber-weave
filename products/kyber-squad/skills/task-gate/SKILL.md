---
name: task-gate
description: "Runs the deterministic task-gate review gates over only the files one developer task changed, applies their fixes, and reports a TASK_GATE digest line. Use before a developer agent reports a task done, when a TASK_GATE result or the task-gate is requested, or when a per-agent review gate must pass before handoff. Do not use when the request reviews a whole branch or pull request rather than one task's exit gate."
license: MIT
metadata:
  author: dpalfery
  version: 0.1.0
---

# Task Gate

The task gate is the exit check a developer agent runs on its own work before it reports
a task done. It is deterministic: the review gates declared for the agent in
`.kyber-weave/kyber-weave.yml` run through the `kyber-weave` CLI, so the result is the
gate's output and not the agent's opinion of its own change.

The gate covers one task. It never reviews the whole run, and it never inspects files the
task did not change.

## Inputs

- `<self>`: the agent name, for example `csharp-dev`. Pass it to `--agent` exactly.
- `<task-id>`: the task identifier from the plan or dispatch that assigned the work.
- `<files>`: only the files this task created or modified. Build the list from the task's
  own edits. Do not add files another worker is editing.
- `<gate-id>`: the id of a declared gate, taken from the gate output when one fails.

## Command

Use the `kyber-weave` command on `PATH`. Inside this checkout, when it is not on `PATH`, use
the fallback `dotnet run --project src/KyberWeave.Cli -c Release --` in its place.

Fix pass, scoped to this task:

```bash
kyber-weave review gates . --agent <self> --paths <files> --task <task-id> --fix --out .agent_scratchpad/<task-id>/task-gate.json
```

Verification pass, the same scope with `--fix` removed:

```bash
kyber-weave review gates . --agent <self> --paths <files> --task <task-id> --out .agent_scratchpad/<task-id>/task-gate.json
```

The verification pass is the one whose result counts. A `--fix` run that changed files is
not a pass until a run without `--fix` reports every declared gate as passing.

## Optional fix pass for .NET work

When the task changed C# files, the mechanical findings can be erased before the gate runs.
Follow the `resharper-clt` skill's fix pass, scoped with `--include` to this task's files.
Run only the `cleanupcode` fix pass. Never run `InspectCode` from this gate: inspection is a
once-per-run review gate, and a per-task run loads the whole solution.

## Loop and the 2-cycle cap

1. Run the gate with `--fix`, then run it again without `--fix` to verify.
2. If every gate passes, stop and report `TASK_GATE: PASS`.
3. If a gate still fails, make the fix that the failing gate output calls for, then run one
   fix-and-rerun cycle. A cycle is one `--fix` run followed by one verification run.
4. The loop has a 2-cycle cap. After two fix-and-rerun cycles, stop even when a gate still fails.
5. When the cap is reached with a failure, return the escalation below. Do not run a third cycle.

A gate that cannot run for a reason outside the task is not a failure to fix. Report it as
skipped with its reason, and do not claim a pass for it.

A whole-project gate, one whose argv has no `{changed}` (for example `dash-typecheck` or
`dotnet-build`), can fail on a file this task never touched while another worker is still
editing. When that happens, re-run the gate once after the other change settles, or rebase
onto the latest base, before counting the failure as rework or escalating. Report the re-run
in the completion digest beside the `TASK_GATE:` line, not inside its fixed fields.

## Escalation

When the 2-cycle cap is reached and the gate still fails, return this instead of a pass:

```text
STATUS: ESCALATION
CIRCUIT_BREAKER_TRIGGER: <ITERATION_CAP_EXCEEDED | THRASH_OSCILLATION_DETECTED | INVARIANT_CONTRADICTION | BLAST_RADIUS_EXCEEDED>
FAILURE_CLUSTER: task-gate/<gate-id>
```

The trigger set is closed; this gate raises `ITERATION_CAP_EXCEEDED` when the 2-cycle cap is spent. Use `task-gate/<gate-id>` with
the id of the gate that stayed red.

## Digest line

Every developer completion digest carries exactly one `TASK_GATE:` line, in this form:

```text
TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE — agent=<name> gates=<n> failed=<ids|none> skipped=<n> report=<path> runs=<k>
```

Choose the status word by these rules:

- `PASS`: at least one declared gate ran, and every gate that ran passed on the verification run.
- `FAIL`: at least one gate still failed when the loop stopped. Pair it with the escalation.
- `NONE_DECLARED`: no gates are declared for `<name>`. This is a valid result, and it is not a pass by omission.
- `NONE_APPLICABLE`: gates are declared for `<name>`, but every declared gate was skipped because none applies to this host or to this change, so none ran. This is a valid result, and it is not a pass. A declared gate set in which every gate is skipped never reports `PASS`.
- `UNAVAILABLE`: the CLI was not found on `PATH` and the fallback could not run. This is never a pass.

Fill the fields from the report at `report=<path>`. `gates` counts declared gates, `failed`
lists failing gate ids or `none`, `skipped` counts gates recorded as skipped, and `runs`
counts CLI executions in this task, including the fix runs.

## Boundaries

- Never report a task done without this digest line.
- Never edit the gate declarations to make a gate pass. A gate that fails on the task's own
  files is a finding to fix, not a configuration to change.
- Never run `InspectCode` from this skill.
