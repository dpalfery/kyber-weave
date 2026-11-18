---
id: kyber-squad/roster-comparison-runbook
title: Roster comparison runbook
doc-type: runbook
status: superseded
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-17
keywords:
  - worker roster
  - tiered roster
  - technology roster
  - comparison
  - step-up
---

# Roster comparison runbook

> [!NOTE]
> **Retired on 2026-11-17.** The owner removed the fourteen per-technology agents without this comparison
> ([ADR 0088](../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md)), so nothing here
> needs running and the two-roster comparison it describes can no longer be set up. Kept for history; not
> current guidance. The one roster and the developer tiers it ships are in
> [Kyber-Squad architecture](architecture.md#12-the-worker-roster-and-developer-tiers).

How to run the same approved plan once per worker roster and record enough evidence to choose
between them. The comparison answers one question: with everything else fixed, does the tiered
roster (`dev-easy`, `dev-medium`, `dev-hard`) finish the same work with less rework, fewer gate
failures and no worse review outcome than today's per-technology agents? The owner's decision,
recorded after these runs, is what authorizes removal of the old agents; nothing here removes
anything.

The roster a run uses is `worker-roster` in the host configuration, recorded in the task file
when it is written. Both rosters stay selectable until the removal phase.

## The three plans

Run exactly three plans, each from the same base commit. Choose them before either run and
record their paths and digests; do not substitute a plan mid-comparison.

| Kind | What it exercises | Selection rule |
|---|---|---|
| Single-technology, disjoint tasks | Dispatch overhead and the ordinary inner loop, with no cross-task coupling to explain a difference | An approved plan with at least three tasks in one technology, whose file lists do not overlap |
| Cross-technology dependency | Hand-offs and sequencing when one task waits for another | An approved plan with at least two technologies and a `Depends on:` edge between them |
| Known rework | The step-up and the failure report a higher tier starts from | An approved plan with a task whose first attempt is expected to fail — a deliberately failing gate or a defect that needs a second attempt — or a completed plan whose record shows at least one rework dispatch |

If no approved plan has a known-rework task, run the third plan with a task whose file list
includes one test file and one source file that the first attempt cannot make pass; record that
it was chosen for this reason. Do not invent a failure in the other two plans: their numbers
are the baseline.

## Fixed inputs

Record these before the first run. A difference in any of them explains a difference in the
result, so the comparison is not read until every one matches.

| Input | Value to record |
|---|---|
| Base commit | The full SHA both runs start from; the same plan files at that commit |
| Harness | One harness, named by its catalog slug; both runs on it |
| Harness version | The version string the harness reports |
| Machine | Host and OS; run sequentially, not in parallel, on an otherwise idle machine |
| Model presets | Unchanged between runs; `fast`, `general` and `dev-hard` resolve as the host configured them |
| Arbiter settings | The same `arbiter` block in both runs; decision-model targeting off, so a difference is the roster and not the model pick |
| Task gates | The same `review.task-gates`; the tiered roster resolves gates by technology, so record the resolved command per task |
| Start command | The same command and prompt that starts the conductor, and the same permission mode |
| Run directory | A fresh worktree at the base commit per run; its path recorded |

One run per roster, per plan: three plan runs per roster, six in all. A run that crashes or is
abandoned is recorded as abandoned and repeated from the same base; do not splice two attempts
into one result.

## Procedure

1. Copy this runbook's tables into a results file beside it for this comparison, one section
   per plan and one subsection per roster.
2. Pick the base commit and the three plans. Record their paths, their `last-reviewed` dates
   and their digests (the task file carries a plan digest; record that, or `sha256sum` the plan
   file when it has no task file).
3. For each roster, create a fresh worktree at the base commit:
   - set `worker-roster: technology` or `worker-roster: tiered` in the configuration;
   - write or refresh the task file, so the roster is recorded there;
   - run the three plans one after another, starting each from the same base commit.
4. Before any cleanup, copy the evidence out of the worktree:
   - `artifacts/arbiter/ledger.jsonl` and `artifacts/arbiter/decisions.jsonl`;
   - `.agent_scratchpad/`, which holds each task's gate reports and review cycles.
   The scratchpad is git-ignored and does not survive a `git clean`; the numbers below cannot
   be recovered afterwards.
5. Fill the measures table from the copied files, naming the file and line for every number.
   Where the table says a measure has no source, write **not measurable** and the reason. Never
   write `0` for a missing measurement: zero is a measured value and changes the comparison.
6. Repeat for the second roster, then compare the two result sections.

## Measures and where each number is read from

| Measure | Definition | Where the number is read from | If there is no source |
|---|---|---|---|
| Tasks completed | Task ids whose last attempt reported `RESULT: PASS` | `artifacts/arbiter/ledger.jsonl`: the post event's `headers.task` and `returns.task-review`; the last event for a task wins. Cross-check the plan's checked tasks after closeout | Ledger absent or unpaired: read the plan's task list after closeout and say so; if neither exists, **not measurable** |
| Attempts per task | Dispatch count per task id | `artifacts/arbiter/ledger.jsonl`: count pre events whose `headers.task` is the id, in file order | Ledger absent: **not measurable**; a conductor transcript is not a substitute |
| Tier of each attempt | The agent that ran the attempt: `dev-easy`, `dev-medium` or `dev-hard` | The same pre event's `target`. Under the `technology` roster the target is the technology agent, so record its name and its `model-profile` (`fast` → easy, `general` → medium, `deep-planning` → hard) from `products/kyber-squad/profiles/models.yml`; a role with no developer tier (planner, reviewer, docs) records **no tier** | `target` absent or the model profile unknown: **tier unknown**, never inferred from the result |
| Task-gate failures | Gate runs that reported a failing gate, per task | `.agent_scratchpad/<task>/task-gate*.json`: a report with any gate whose `passed` is false; the task's `TASK_GATE:` line in the ledger return is the cross-check | Scratchpad cleaned, or the harness kept no reports: **not measurable**. The exact attempt a report belongs to is not recorded; order by the report's modification time inside the attempt window and say so |
| Review findings by severity | Count of findings at `critical`, `major`, `minor` and `nitpick` | `.agent_scratchpad/<task>/review/cycle-<n>/findings.json`: count by each finding's `severity`, across every cycle; a finding id repeated across cycles counts once, at its last severity | No review cycle ran: **not measured (no review cycle)**. A seat transcript without a verdict is not a count |
| Tokens | Fresh input, cache reads, cache writes and output the harness reported | `~/.kyberdash/canon.db` session record for the run's sessions, read through `kyberdash report` or the `dash_query_session_cost_tokens_and_duration` tool; sum the sessions the run started, and record each class separately | The harness exported no usage, or the session was not imported: **not measurable**. Never substitute a token estimate |
| Wall time | Start to finish of the plan run | `canon.db` session `duration_ms` for the run's session. Fallback: first pre event's `at` to the last post event's `at` in `artifacts/arbiter/ledger.jsonl` for that task set | Neither source present: **not measurable** |

Two readings need care:

- **Tokens and wall time include waiting.** They cover the conductor's session, not only the
  worker's own time, because that is what a user experiences. Record any long human pause in
  the results section; it is not a roster difference.
- **A task with no gate declared** reports `NONE_DECLARED` or `NONE_APPLICABLE`. That is not a
  gate failure; record it in a note, not in the failures column.

## Recording the result

For each plan and roster, record:

1. the fixed inputs, from the table above;
2. one row per task: id, completed (pass/fail), attempts, each attempt's tier, gate failures;
3. the review findings by severity, per task and summed for the plan;
4. tokens by class and wall time, with the source path beside each;
5. the measures marked **not measurable**, and why.

Then one summary per roster: plans completed, total attempts, attempts per completed task,
task-gate failures per completed task, findings by severity, tokens by class, wall time.

## What this runbook does not measure

- **Model quality.** Both rosters use the models the host configured; a preset change between
  runs invalidates the comparison.
- **Cost in money.** Tokens are recorded; converting them to money is a separate step with the
  host's own rates, and an unpriced model has no published rate to use.
- **Anything the harness did not report.** A measure with no source is recorded as not
  measurable. The decision is made from what was measured, and the gaps are stated with it.
