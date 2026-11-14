---
name: delivery-execution
description: "The worker execution contract — how work is dispatched, verified and reported. Both entry points load it: the conductor for Ready plans and Ready specification task artifacts, and the bug-crusher workflow for a single escalated defect. Use when sequencing a ready queue, isolating parallel workers in worktrees, running test-first or standard mode, applying the developer exit gate and its six-field completion digest, enforcing the iteration circuit-breaker and its failure-cluster tallies, draining findings, running the final review council, or closing out a plan or specification. Do not use as a substitute for the conductor or bug-crusher role bodies: those load this skill and own the routing around it."
license: MIT
---

# Worker Execution Contract

This is the worker execution contract: both entry points load it to learn how work is dispatched, verified and reported, and neither restates these rules in its own words. It is shared by Ready plans and Ready specification task artifacts, and by the `bug-crusher` skill when a single defect escalates to a plan.

Where this contract says "the conductor", read the orchestrating session: the `conductor` for plan- and spec-backed work, the `bug-crusher` main session for a single escalated defect. The role bodies own routing — the ready queue, the tripwires, the escalation decision. This contract owns what happens once work is dispatched, and how each dispatch is proved.

## Ready queue

A task is ready only when its declared dependencies are complete, its file or symbol scope does not overlap work in flight, and its development-mode gate is satisfied. Dispatch ready tasks up to `review.max-concurrent-workers` (default 4, host-configurable in `.kyber-weave/kyber-weave.yml`); when file scope is uncertain, favor parallelism within the cap and accept the conflict risk. (Q4 reversal, issue #386: this supersedes the 2026-10-14 "no max concurrency" directive, which predated per-task artifacts-path isolation.) Re-evaluate the queue after every completion. Component labels and table order are not barriers.

Track one implementation owner per task. Rework uses the same queue and the same dependency and scope rules. Each dispatch carries a self-contained task packet that defines authorized scope: inlined entry-file table, acceptance criteria, dependencies, selected development mode, required contract, and pre-computed test commands. The worker loads the skills its own agent file names. Keep plan and spec files out of the worker context: a plan or spec path in the packet is routing metadata marked not for reading; never instruct a worker to open the plan or spec to learn its task.

When dispatching concurrent workers up to `review.max-concurrent-workers`, isolate parallel workers using separate git worktree checkouts or isolated working contexts with dedicated per-task artifact paths (`dotnet build --artifacts-path .agent_scratchpad/<task-id>/artifacts` for compiler intermediates and `kyber-weave review gates` `--out .agent_scratchpad/<task-id>/task-gate.json` for gate reports). Worktree MCP setup splices `--repo-root` and `--path` to absolute canonical paths <!-- anchor--path to absolute canonical paths -->. Worktree MCP configuration changes must use isolated per-task scratch paths for every tracked MCP configuration the setup touches; never rewrite a tracked configuration in place. If one is touched anyway, restore it (`git checkout -- <path>`) before committing or integrating worktree changes, so machine-specific absolute paths are never committed; the MCP runbook enumerates the per-client configuration files. Worktree MCP setup fails closed and strictly forbids .codegraph symlinks (never create symlinks to `.codegraph/` because background watchers rewrite the canonical db from worktree state). A worktree does not share the canonical checkout's index, so every new worktree indexes itself: `kyber-weave doctor --ensure` runs there before its first task, and that one run also proves the docs server answers. The MCP servers stay bound to the canonical checkout on purpose, so a role with a shell asks its own worktree with `codegraph explore "…"` and treats an MCP `codegraph_explore` answer as describing another tree whenever its provenance `branch=` is not the branch under review; a role with no shell is given the worktree's absolute path in its packet and passes it as `codegraph_explore`'s `projectPath`, which answers from that worktree's own index. It reads files directly only when that call cannot answer for the revision in hand (an error, a not-indexed reply, or an answer whose provenance `branch=` is another branch), and counts each such read as a direct read with the reason, naming the mode: `projectPath: error`, `projectPath: not-indexed` or `projectPath: wrong-branch <got> vs <wanted>`. A worker whose `doctor --ensure` fails follows `stop-on-tool-failure`: the run stops rather than dispatching work that would answer from absent joins. Only a non-zero exit or the literal line `STATUS: TOOL_FAILURE` stops it: a worker's note that it fell back to direct reads, or a `Discovery:` line with zeros, is a warning for the run report. Serialize final branch-head integration (cherry-pick or fast-forward merge onto the shared feature branch) on the main working tree before git push. When an integration conflict occurs, immediately abort the operation on the main working tree (`git cherry-pick --abort` or `git merge --abort`), record conflict details in the execution record, and dispatch a conflict-reconciliation rework dispatch. Guarantee worktree cleanup across all outcomes (`git worktree remove --force <path>` and `git worktree prune` across all exit paths: successful integration, failure, escalation, or cancellation).

## Task file and dispatch

A Ready plan or a Ready specification task artifact carries a task file beside it: `<plan>.tasks.json` under the plans directory, or `<spec-folder>/tasks.json` for a specification. It holds the schema version, the artifact path, the execution digest, and one entry per task — id, the abilities it needs, dependencies, file scope, `session` group and the full prompt (and `role` only when the owner assigned an agent).

Before dispatching from it, the conductor validates the task file. A file whose tasks, file scopes or dependencies disagree with the artifact, or one whose execution digest is stale, is refused rather than dispatched from: a stale prompt describes work the artifact no longer asks for, and the run reports that finding instead of starting the task.

Each dispatch carries the routing headers and the task id, and nothing else: the conductor never pastes, retypes or paraphrases a packet, and never reads the task file to do so. Choose each task's agent by matching the abilities it needs against the descriptions of the agents you can dispatch. The worker's first action is `kyber-weave docs packet <task id> --agent <its name>`, which prints that task's packet from the task file with `{worktree}` filled in. When a later task builds on what earlier workers reported, the conductor writes those reports unchanged to a file and the dispatch adds `--prior-results <file>` to that command, which fills `{prior_results}`. `kyber-weave docs tasks <plan> --list` prints one line per task (id, abilities needed, dependencies, session, file count) when the conductor needs them.

The conductor dispatches by task id. It does not open the plan or the task file to write a packet or decide what a worker should read; the plan stays the owner's approved record and the Arbiter's input.

## Task commits and resumption

A finished task commits on the feature branch with a `Task: <id>` line, so a resumed run can tell which tasks completed and where the work stopped. The worker's report goes in the commit message body — its completion digest and the message that explains it — and the `Task:` line sits in the trailer area beneath, which is what `kyber-weave run start --resume` reads back.

Commit per task, and push after each integrated task. A run that stops early then leaves a record of everything it finished, where a run that committed once at the end leaves nothing to resume from.

## Task session continuity

Record the task id, owning specialist type, and `worker-session-id` on the execution artifact. Store actual handles returned by the harness; when none is available, record that fact and the reason rather than inventing one. Keep a recovery brief containing the task packet, completion evidence, rejected gate results, decisions, failed approaches, and persisted failure-cluster tallies.

Reuse the original worker session for implementation, task-gate rework, verification, and any final-council findings in its scope. The task file's `session` group is how the plan records that grouping: follow-on fixes and later tasks needing the same abilities on one change share a session. Use the harness's continuation or follow-up operation with the recorded handle. Append the findings and current scope/evidence updates without rewriting earlier conversation history. A fresh invocation of the same specialist type is a replacement, not continuation. Keep the task session available until its exit gate and any run-level findings are settled; do not repurpose it for an unrelated task while it awaits rework.

Start a replacement only when continuation is unavailable, the original session cannot be recovered, or its context is unreliable (for example, it repeatedly acts on superseded requirements after correction). Record the replacement reason, pass the recovery brief, and update the handle. A busy session is queued, not replaced merely to avoid waiting. Separate unrelated tasks use separate sessions.

Discover continuation support from the current harness's exposed tools; do not assume it from the model, role name, or renderer. VS Code's stateless `runSubagent` path requires recovery briefs; resumable harnesses use their native session handles. Retaining a conversation supports context reuse but does not guarantee a provider cache hit. Avoid keeping a polluted context merely to preserve cached tokens; correctness takes priority.

## Test-first mode

`test-first` is the default. A task is one unit of behaviour owned by one specialist, and its Test contract row names the tests, the runner and the observable behaviour. In that one dispatch the worker:

1. writes the tests;
2. runs its task gate with `--red` (`task-gate` skill), which passes only on tests that ran and failed, or a build that failed only on symbols missing from the task's own files;
3. implements the behaviour without weakening those tests;
4. runs the task gate again, and its passing report is the one the digest names.

The proof of test-first is those two machine-written reports in the task's scratch folder, the failing run older than the passing one, and the Arbiter records on each return whether they show it (`KW-ARB-MODE-002`). No task writes only failing tests for another to implement, and no task adds a stub so another task's tests compile; `test-dev` takes only tasks whose deliverable is tests. A worker whose test project does not compile because of another task's tests reports the `NOT VERIFIED` line its task gate prints (`KW-REVIEW-053`) and never builds a private runner project.

Follow the build once, test many pattern for rapid iteration: build or compile once, then execute targeted tests using the pre-computed direct assembly test runner the host's test standard gives in its `## Completion` section:
`dotnet <path-to-test-assembly>.dll -class "<Namespace>.<TestClass>"`
or `dotnet test --no-build` for fast loops. Reserve full test suite execution for the final review council, which runs it on the integrated head in the first full review, and for CI; a later review-fix round re-runs only the gates the fix touched. No worker runs it, and the conductor never dispatches a full run, even for a cross-cutting change. A worker's task end runs only the test classes its task touched, because workers finish together and concurrent full runs overload the machine. Name those classes in the task packet's runner command by their fully qualified names (a short name runs zero tests and exits 0), passing `-class` once per class (`-class "<Namespace>.<A>" -class "<Namespace>.<B>"`). A task with no covering test class reports `TESTS: total=0` with the reason; treat `total=0` on a code change as unverified, not passing. When a worker reports that its change reaches shared test infrastructure, it has run a targeted set and named what it did not run; the council and CI cover the rest.

If changing requirements would require weakening or replacing the approved Test contract, return the artifact to its author and reopen approval for that contract.

## Standard mode

`standard` is an explicit opt-out from the failing run, not an opt-out from tests. Each implementation task carries an approved verification contract naming the automated tests and other checks that prove its acceptance criteria.

1. **Implementation:** the specialist delivers the scoped change.
2. **Verification:** the specialist runs the approved automated tests and checks against the current tree.
3. **Gate:** the worker's exit gate and the final council use that current evidence.

Changing the verification contract after approval reopens its approval gate.

## Developer exit gate

Every developer worker ends its task by running the deterministic `task-gate` and reports the result in its completion digest. No agent audits a single task, and the conductor does not judge technical correctness.

An implementation worker whose change itself adds or alters the handling of a value that arrives from outside the system's trust boundary (request input, file content, environment, an external service's response), where it is parsed, converted, or passed to a file or collection API, fills the input-site table for that change before it reports done. Rows read from the application's own database through an existing repository pattern are not such a value, and a change with no new parse or convert site has none: the worker writes one line, `Input sites: none`, in its report and creates no file.

The table's columns and cell values are defined once, in the correctness lens of the code-review skill (references/lenses/correctness.md); the worker uses them unchanged.

Every THROWS or accepted wrong cell is fixed before the worker reports done, and each fixed cell is covered by a test that fails without the fix; a fixed cell that no test covers yet goes on the worker's COVERAGE_GAPS line, and the conductor sends it to test-dev before the task clears its exit gate.

When the table applies, the worker saves it as `.agent_scratchpad/<task-id>/input-sites.md`, cites it on an Artifact: line, and reports Input sites: N traced, M files with none in the message body beside its digest.

A fix for a reported defect covers the defect's whole class: the worker states the class in one sentence and names at least three other inputs of that class that the original failure scenario did not name, each covered by a test, on one line: `Defect class: <one sentence> — inputs: <input>; <input>; <input>.` A class input that no test covers yet goes on the worker's COVERAGE_GAPS line, as a fixed cell does above.

Workers precede the six-field completion digest block with the `Discovery: codegraph N, docs N, direct reads N` header; this header is valid and is not rejected as a 7th digest field, and neither is any other line in the list below. The completion digest itself must follow this exact six-field schema, subject to the phased-rollout scope in the bullets below:

```text
STATUS: PASS | FAIL | ESCALATION
FILES: <comma-separated-paths>
TESTS: total=N failed=N passed=N
COMMIT: <commit-sha | NONE>
REMOTE_HEAD: <remote-head-sha | UNPUSHED>
TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE — agent=… gates=… failed=… skipped=… report=… runs=…
```

Worktree workers committing locally report their `<commit-sha>`, while uncommitted/unpushed task runs report `NONE` and `UNPUSHED`.

Lines that are not one of the six digest fields are never counted or rejected as a seventh field. They are exactly these shapes: the Discovery: codegraph N, docs N, direct reads N header; COVERAGE_GAPS: followed by free text; Input sites: N traced, M files with none (or Input sites: none); Defect class: followed by one sentence and the inputs it covers; and Artifact: or Baseline: followed by one repository-relative or absolute path. A `report=<path>` inside the `TASK_GATE:` line is part of that field, not a separate line.

- **Missing line or `FAIL`:** reject the digest, except a valid escalation under the circuit-breaker section below. Create a self-contained rework item for the original worker session (use the replacement policy above only when needed) naming the failed gate ids and the report path. The rework counts in the per-cluster tally below, keyed `task-gate/<gate-id>` for each failed gate, or `task-gate/missing` when the line is absent. After 2 rework dispatches for a key, the circuit-breaker trips on it. During the phased rollout only `csharp-dev` and `test-dev` must return the full six-field block; every other developer worker may return its standard completion, meaning the completion report its own agent body specifies, plus a `TASK_GATE:` line; the conductor's requirement for that line overrides the agent body, so a standard completion without it is rejected as `task-gate/missing`, and the `FILES`, `TESTS`, `COMMIT` and `REMOTE_HEAD` fields are not required of it. The missing-or-`FAIL` `TASK_GATE` rejection and its `task-gate/<gate-id>` / `task-gate/missing` tally apply to every developer worker.
- **`PASS`:** the task's gate is complete. It does not authorize merge.
- **`NONE_DECLARED`:** valid; the repository declares no gates for that agent.
- **`NONE_APPLICABLE`:** valid; gates are declared for that agent, but every one was skipped for this host or change, so none ran. Accept it as `NONE_DECLARED` is accepted. It is not a pass, and it never reads as `PASS`.
- **`UNAVAILABLE`:** the gate could not run. It is never a pass: record it in the findings collection with the worker's reason and continue.
- Re-run nothing from memory: rework ends with a fresh `TASK_GATE:` line against the current tree.

Other task sessions may continue with non-overlapping ready work while a task awaits rework.

## Iteration circuit-breaker and loop detection

The conductor enforces an iteration circuit-breaker to halt thrashing test-fix loops across rework cycles.

A **failure cluster** is keyed by the failing test ID first observed for that cluster, recorded on the execution artifact when the cluster is created (when no test IDs exist, the failing subsystem, job, or step label). A newly failing test joins the recorded cluster whose key it most recently co-failed with; if it co-fails with none, or with more than one, it forms a new cluster keyed by itself. Every resumed or replacement worker receives the recorded key; it does not re-derive one from whatever is failing now. Distinct keys remain distinct clusters for A/B oscillation detection.

The per-cluster **dispatch tally** lives on the run's persisted execution artifact (or the task artifact when the run has not yet written one), independently of session lifetime. Each time the conductor dispatches rework for a failure cluster, it reads that tally, increments it, and writes it back. Resuming a worker counts as a rework dispatch just as starting a replacement does. Re-evaluating the queue reads the tally; it never increments it. A tally at or above the cluster limit trips the breaker before the dispatch, not after it. Resumption, replacement, and compaction never reset this tally or oscillation history. The worker's inner 3-iteration cap applies to each implementation or rework dispatch, not the lifetime of the retained session.

- **Cluster-level retry limit:** Maximum of 2 rework dispatches for the same failing fixture or subsystem failure cluster across the run. If a failure cluster persists across 2 rework attempts, the circuit-breaker trips on that cluster.
- **Oscillation detection:** If a fix for Failure Cluster A causes regression in Failure Cluster B, and a fix for B regresses A (or rework alternates between failure signatures), the loop detector trips immediately.
- **Circuit-breaker escalation:** When the circuit-breaker trips—or when a worker returns a valid `STATUS: ESCALATION`—halt rework for that task immediately. Do not dispatch further workers for that failure cluster. A valid escalation is `STATUS: ESCALATION` with exactly one `CIRCUIT_BREAKER_TRIGGER:` token from the closed set (`ITERATION_CAP_EXCEEDED`, `THRASH_OSCILLATION_DETECTED`, `INVARIANT_CONTRADICTION`, `BLAST_RADIUS_EXCEEDED`) and its required payload lines (`FAILURE_CLUSTER` and `RECOMMENDED_ACTION` always, plus `CONTRADICTORY_INVARIANTS` for `INVARIANT_CONTRADICTION` and `BLAST_RADIUS` for `BLAST_RADIUS_EXCEEDED`); only a valid escalation is exempt from the `FILES`, `TESTS`, `COMMIT`, `REMOTE_HEAD` and `TASK_GATE` rules. A `STATUS: ESCALATION` without a valid `CIRCUIT_BREAKER_TRIGGER` is not an escalation: it is checked and rejected like any completion digest. A valid escalation wins the routing (halt rework, record `ESCALATION: circuit-breaker`, architect mediation) but never completes the task or clears its exit gate; a `TASK_GATE: FAIL` it carries is recorded in the finding with its gate ids, and the task stays open until the conductor, once architect mediation has resolved the cluster, dispatches a follow-up worker that returns a fresh passing `TASK_GATE:` line against the current tree; the halt bars dispatch only until that mediation completes. The conductor accepts a valid escalation's payload without rejection for missing completion fields or `TASK_GATE`. Record the finding in the run's findings collection with the key `ESCALATION: circuit-breaker`. Include `CIRCUIT_BREAKER_TRIGGER: <ITERATION_CAP_EXCEEDED | THRASH_OSCILLATION_DETECTED | INVARIANT_CONTRADICTION | BLAST_RADIUS_EXCEEDED>` plus the affected failure cluster, the contradictory invariants and blast radius where the trigger carries them, and the worker's `RECOMMENDED_ACTION`. Reject any other trigger token; do not invent a reason.
- **Same-failure advisory:** A rework dispatch opens with `FAILURE-CLUSTER: <key>` as its first body line, using the key the cluster recorded, and carries a `FAILURE:` section with the failing output. The Arbiter compares that output with the cluster's previous attempt. An `ARBITER_SAME_FAILURE: yes prior=<id> decision=<id>` advisory means the same failure repeated: escalate per the circuit-breaker rules now, rather than dispatching another retry. The advisory does not deny the rework. The first failure of a cluster is only recorded. The rule is shipped in shadow, so the advisory appears only once it is enforced.
- **Budget exceeded:** A dispatch that runs past its budget of tool calls or minutes is stopped by the Arbiter, and the worker returns `STATUS: BUDGET_EXCEEDED` with what is done and what is not. Treat it as a failed attempt: it counts in the cluster's tally, and its rework goes one model up the ladder, or to `architect` when no higher model can be chosen. Never send the same packet back to the same worker on the same model.
- **Architect mediation:** Tripped circuit-breakers must not be ignored or bypassed. When the queue drains to the findings collection, `architect` investigates the failure cluster, assesses whether coupled test fixtures assert conflicting invariants or legacy details, and authors an intake recommendation or Draft plan to resolve the architectural conflict.

## Findings and final council

When the ready queue is empty and every task has cleared its exit gate, drain a non-empty findings collection through `architect`. Any resulting Draft plan goes through its normal user approval gate before execution.

With the collection empty, the documentation-impact review for the round is finished and every edit it caused is in the tree, so the council reads the change that will ship. Run the final review over the accumulated run, and again after each fix round of the review loop below: dispatch `code-reviewer`; on a harness where sub-agents cannot dispatch, load the `review-council` skill instead. Sub-agents on this harness can dispatch: `{{subagents-dispatch}}`. The council reviews the pushed branch, or the local branch when the push fails; it never waits for a pull request. `REQUEST_CHANGES` starts the review loop below. The verdict goes in the PR description when one exists and is not a conductor gate. The pull request is marked ready once required CI passes, and review happens after that (the autonomy contract sets the 60-minute CI wait).

The final review runs alone and last, in the foreground of this session, and nothing else runs while it does: no docs work — no documentation-impact review, no `docs-dev` dispatch, no closeout — and no edits in the reviewed tree, by you or by any worker, until it returns. A review that shares the tree with concurrent docs work or edits is reviewing something that keeps moving, so a finding it raises may be against a line that has already changed. Everything the round produces, the documentation-impact review included, is finished before the council starts; the only work that follows it is the fix rounds of the review loop, and the closeout an approval unlocks. Each fix round finishes before the next review starts, so every cycle also reads a tree that is standing still.

**The review dispatch.** It is one dispatch: `code-reviewer` runs the gates, starts every lens itself and returns the verdict. Give it the tree under review, the base ref, and, after the first cycle, the prior cycle folder. Do not restate the change, list files or areas, or ask for tests or checks: the council's gate run measures the change and writes `evidence.md`, the diff, the changed files and every gate's result, into the cycle folder, and every seat starts from that. You never start a seat yourself except through the `review-council` skill.

### Review loop (critical and major findings, failed blocking gates)

The council re-reviews after every fix round until it returns APPROVE. After `REQUEST_CHANGES`, the conductor sends every critical and major finding, and every failed blocking gate, to the worker session that owns its scope. That fix round is ordinary rework: the same queue and scope rules, the same continuation of the owning session, a count in the per-cluster tally, and a fresh `TASK_GATE:` line against the current tree. Each fix-round dispatch carries one finding, or one tightly related group, with its file and line, so a round that stalls stalls on one thing. The packet never asks the worker to restore a file to base and re-apply the change: the fix edits the current tree. Then `code-reviewer` runs again over the new head. A failed blocking gate is fixed inside the same loop: it goes to the worker that owns the failing scope, like a finding. Nothing bypasses the loop, and a blocking-gate failure (the blocking-gate verdict rule: docs validate, docs drift, build, test, and analyzers) is not a separate path. Every review after the first runs in the verifier mode of the dp-code-reviewer skill: it re-checks the prior findings and the fix delta, not a full council. It re-runs only the gates whose inputs the fix touched (`--gate <id>`), never the full test suite; CI is the full re-check.

In this skill, a finding verified as real is one that survived the council's adjudication. A fix for a review finding verified as real ships with a test that fails without the fix, bot review rounds included; a finding classified as a nitpick needs no test. In test-first mode the owning worker writes that test and runs it failing before the fix, in the same rework dispatch.

The loop stops after 5 review cycles, or after 3 cycles in a row in which the count of surviving critical and major findings does not fall; the findings still open are then listed in the pull request as pending human review, and delivery continues. A finding whose rework trips the circuit-breaker stays open: it counts toward that stop and is listed with the rest. Minor/Info findings never loop: they are reported in the verdict and the pull request, and no cycle starts for them.

The loop runs on the open pull request: delivery never waits for it, and required CI alone decides ready.

Each cycle writes to `.agent_scratchpad/<task-id>/review/cycle-<n>/`: the `seats` folder, `gates.json`, the `findings.json` the verdict command writes, and the `cycle.json` the tools write; keep it until the run ends. The conductor records each cycle's number and its count of surviving critical and major findings on the execution record, which is how it sees the stop, and passes the prior cycle folder in the next `code-reviewer` packet so the verifier re-checks what that cycle found.

## Closeout

After approval, close out the execution artifact. A plan closes in two steps, in this order:

1. **Harvest: one `docs-dev` dispatch.** It harvests the plan's decisions and Design harvest into canonical documentation and fills the plan's Closeout mapping, then checks its work with `kyber-weave docs closeout <PLAN_FILE>`, which names anything still missing. It does not archive. The harvest is not a plan task: send it with `PLAN_FILE:` and no `TASK:`, and no task id in the text; docs-dev needs no packet. Never invent a task id for it.
2. **Archive: your tool.** When docs-dev returns `CLOSEOUT_COMPLETE`, call `docs_archive_plan_after_closeout_is_complete` with the run's worktree as `projectPath` and the `PLAN_FILE`. On `result: refused`, send its findings unchanged to `docs-dev` as another harvest dispatch, then call the tool again. Those findings go to `docs-dev` only: never to another role, and never move, delete or edit plan files by hand. On `result: archived`, call `docs_validate_governed_document_schema` with `mergeReady: true` and `docs_detect_drift_between_documents_and_code` with the same `projectPath`; a finding goes to `docs-dev`.

Merge-ready validation fails on an open plan (`KW-DOC-LIFECYCLE-003`) and on its task file (`-005`) until the archive runs, so nothing runs it before step 2. Call the archive and finalize tools only while no worker is dispatched: the Arbiter attributes changed files to the dispatches in flight. Only when you do not hold the archive tool, make the harvest dispatch's first body line `ARCHIVE: true`, and docs-dev archives with `kyber-weave docs closeout <PLAN_FILE> --archive` itself.

Spec-backed work: one `docs-dev` dispatch verifies requirements, migrates durable facts, synchronizes the specification index, and archives the specification.

### Documentation-impact review

Before closeout completes, run the documentation-impact review step:

- Keep a run record of asked/decided+why/files changed; no transcripts.
- Dispatch docs-dev with that run record.
- Make the impact-review dispatch's first body line `DOCS-IMPACT: true`, after the routing headers. The Arbiter reads only that line to decide its docs-impact shortcut, so without it the shortcut never fires. Do not send it on the closeout dispatch.
- Require DOCS_REVIEW rows in the closeout evidence before proceeding.

### Closeout completion

Only a successful closeout completes the objective. Record Q2 CLOSEOUT_COMPLETE in the execution artifact and carry the outcome in the PR description.

## Arbiter routing headers

Unless you hold `arbiter_evaluate_agent_dispatch_or_return` (see Arbiter fallback), every dispatch starts with `KYBER-ARBITER: true`. Dispatches that execute or audit a task — implementation specialist invocations, `review-triage` audits, and the `code-reviewer` end-of-run review dispatch — also carry `PLAN_FILE:` and `TASK:`; the `docs-dev` harvest carries `PLAN_FILE:` only.

`PLAN_FILE:` is always the repository-relative path with `/` separators, never the absolute path of a checkout or worktree, even when the run works in a worktree. Sent any other way, the Arbiter cannot read it as a header and falls back to the file on disk or its decision model, which is slower and less certain.

## Worker packets

A packet to an implementation specialist is its whole context. State in the packet that the worker does not open plan, spec, or todo files and performs only the work the packet describes.

## Escalations

On `STATUS: ARBITER_ESCALATION`, follow `NEXT` and do not retry the blocked dispatch unchanged:

- `REPEAT: 1`: dispatch architect with the envelope as a planner dispatch whose marker is the envelope.
- `REPEAT` 2 or greater: record a run finding and stop that task; do not dispatch architect again for it. The findings drain takes the finding.
- A `malformed` planner answer: re-issue the planner dispatch as planning work with a recognised marker; do not send the envelope onward. A repeat stops as above.
