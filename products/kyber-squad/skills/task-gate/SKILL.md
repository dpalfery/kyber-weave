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
the source-run form the host's `AGENTS.md` declares in its place —
`dotnet run --project <the CLI project> -c Release --`.

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

## Fix pass for .NET work

When the task changed C# files, the mechanical findings are erased by `dotnet format` and
`dotnet format analyzers`, scoped with `--include` to this task's files.

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

## .NET isolation

.NET `dotnet build` task gates run with `-nr:false -p:UseSharedCompilation=false` and a
per-task `--artifacts-path .agent_scratchpad/{task}/artifacts` (or `-p:BaseOutputPath=` /
`-p:BaseIntermediateOutputPath=` naming `{task}` where the command takes no
`--artifacts-path`). Always pass `--task <task-id>` so `{task}` expands; without it the
run is refused and no gate process starts.

## Escalation

When the 2-cycle cap is reached and the gate still fails, return this instead of a pass:

```text
STATUS: ESCALATION
CIRCUIT_BREAKER_TRIGGER: <ITERATION_CAP_EXCEEDED | THRASH_OSCILLATION_DETECTED | INVARIANT_CONTRADICTION | BLAST_RADIUS_EXCEEDED>
FAILURE_CLUSTER: task-gate/<gate-id>
```

The trigger set is closed; this gate raises `ITERATION_CAP_EXCEEDED` when the 2-cycle cap is spent. Use `task-gate/<gate-id>` with
the id of the gate that stayed red. The full failure loop — the iteration cap, the JEV
checkpoints and the payload each trigger carries — is in [references/rework.md](references/rework.md).

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

## Completion digest

The task ends with this digest. It is the report format every developer worker returns, and it
is written here once so no agent body carries a second copy of it. The `TASK_GATE:` line holds
the result the section above defines; the other fields are fixed and appear in this order, in
one contiguous block:

```text
STATUS: PASS | FAIL | ESCALATION
FILES: <comma-separated-paths>
TESTS: total=N failed=N passed=N
COMMIT: <commit-sha | NONE>
REMOTE_HEAD: <remote-head-sha | UNPUSHED>
TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE — agent=<name> gates=<n> failed=<ids|none> skipped=<n> report=<path> runs=<k>
RED_EVIDENCE: <runner filter> — <failing tests> — <reason>
```

`RED_EVIDENCE:` is part of the template, not an aside; it follows the six digest fields and is never counted as a seventh. The in-flight ledger reads its value from
the returned output as its `ledger.red-evidence` fact, and Arbiter rule `KW-ARB-MODE-001` uses
that fact to gate test-first plans: an implementation dispatch with no RED evidence on record
escalates. Valid RED is tests that ran and failed on an assertion, or, in a compiled language, a build
that failed only because the tests name symbols the GREEN task creates (the classic first red);
a build that breaks for any other reason is not RED. A test-authoring task does not judge this
by prose: it runs `kyber-weave review gates . --agent test-dev --paths <its test files> --task <slug> --red`,
which passes only on those two shapes, lists the missing symbols it accepted, skips test gates that
cannot run on a broken build, ends with a `TASK_GATE:` line and either `RED_REJECTED: <reason>` or the
line to paste, `RED_EVIDENCE: <filter> — compile: missing <symbols>` or
`RED_EVIDENCE: <filter> — <failing tests> — <assertion>`. It does not add a production stub to
make the tests compile. Emit `RED_EVIDENCE: none` when the work
recorded no RED run, such as a maintenance pass with no failing-first cycle. Anything but a
literal `none` counts as evidence, so the fields must be the values actually run, never
placeholders.

Lines that are not one of the six digest fields are never counted or rejected as a seventh field. They are exactly these shapes: the Discovery: codegraph N, docs N, direct reads N header; COVERAGE_GAPS: followed by free text; RED_EVIDENCE: followed by the runner filter, the failing tests and the reason; Input sites: N traced, M files with none; Defect class: followed by one sentence and the inputs it covers; and Artifact: or Baseline: followed by one repository-relative or absolute path. When the circuit breaker trips, return the escalation in
[references/rework.md](references/rework.md) instead of this digest.

## Boundaries

- Never report a task done without this digest line.
- Never edit the gate declarations to make a gate pass. A gate that fails on the task's own
  files is a finding to fix, not a configuration to change.
