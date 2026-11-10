---
id: plans/2026-11-09-arbiter-mode-red-evidence-wiring
title: Arbiter test-first gate reads real RED evidence and honours no-test tasks
doc-type: plan
status: current
component: KyberArbiter
owner: dpalfery
created: 2026-11-09
last-reviewed: 2026-11-09
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - KyberArbiter
---

# Arbiter test-first gate reads real RED evidence and honours no-test tasks

**Status: Ready** — approved for execution 2026-11-09 (AP1).
Development mode `test-first`; autonomy `autonomous`; scale `extends-component` on component `KyberArbiter`.

**Bottom line.** The Arbiter (the hook that gates agent dispatches) refuses every test-first implementation dispatch, because rule `KW-ARB-MODE-001` reads two facts nothing ever sets. This plan wires those facts so a task the approved plan marks as needing no new test is allowed, and a GREEN task (the implementation that makes a failing test pass) is allowed only after a recorded RED return (the failing-test run) for that task. The same gap blocks every task with a dependency through `KW-ARB-READY-001`, so both are fixed together. Every new allow path is listed under [Security hazards](#security-hazards) with what closes it and what stays open.

## Problem and goal

**Reported failure.** A Factory conductor run dispatching task T5 of a `.NET 10 upgrade` plan to `typescript-dev` was refused:

```text
STATUS: ARBITER_ESCALATION
TRIGGER: delegate
RULES: KW-ARB-MODE-001
QUESTION: In test-first mode, does RED evidence exist before an implementation dispatch?
ANSWER: missing (step 0)
EVIDENCE: rule KW-ARB-MODE-001 answered 'missing' on delegate
```

T5's row in that plan's Test contract says `No new test`, so the refusal was false.

**Root cause.**

1. `plan.test-contract.row` is set to a boolean by both plan readers, and the rule compares it to the string `Not applicable`, so the exemption can never match.
2. `ledger.red-evidence` and `ledger.completed-tasks` are never set. `InFlightLedger.RedEvidenceTasks` and `CompletedTasks` have no production caller.
3. No return is ever recorded. `ArbiterEvaluator.BuildLedgerEvent` sets no `Returns` and no `PairDigest`.
4. Dispatch headers are stripped before the worker runs, so a return cannot name its task. It can only be joined to its dispatch, and that join does not work today.
5. On Factory the `Task` return event fires at launch. The report arrives later, through a `TaskOutput` read the adapter ignores.

**Goal.** In a test-first run on a harness that observes returns:

- a task the approved plan marks as no-test is not refused;
- a GREEN task passes only once RED evidence exists for that task or a task it directly depends on, in the same plan;
- a dependent task passes only once its dependencies are recorded complete in the same plan;
- a refusal says what is actually missing.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Fix only MODE-001, or READY-001 too? | Both. | The real T2 decision answered READY-001 `waiting`. Fixing only MODE-001 moves the refusal one rule along, and the owner's outcome is that a test-first run can dispatch. Both need the same return capture. | autonomous: recommendation accepted 2026-11-09; source: decision log of the reported run |
| D2 | What marks a task as no-test? | Both cells together: the `Test project or file` cell starts with `no new test`, `no test`, `no-test`, `not applicable` or `n/a` (any case), and the `RED evidence required` cell is `n/a`, `not applicable` or `none`. | The RED cell alone is unsafe: the reported plan's real GREEN row also says `n/a` there. A bare `none` in the test cell is excluded because an archived plan writes `None; it is the GREEN task`. `test-first-contract.md` gains one exact wording so new plans do not depend on the list. | autonomous: recommendation accepted 2026-11-09; source: wording survey of `docs/archive/plans` |
| D3 | What ties an exemption to approval? | The plan must be Ready or In progress (body `Status:` line first, frontmatter otherwise). When a task file with a string `execution-digest` exists beside the plan, that digest must equal the plan's current digest. | Uses the existing digest, which covers contract rows. No signature exists anywhere in this system, so this raises the bar and does not prove owner approval: see hazard 4. A task file in another shape gives no binding (the reported run's file is one) rather than a refusal. | autonomous: recommendation accepted 2026-11-09; low-confidence, no authoritative source. Cost if wrong: one rule clause, reversible |
| D4 | Whose RED evidence clears a task? | The task's own, or a direct dependency's, in the same `PLAN_FILE`. | Direct only, because a transitive chain would let one RED clear later unrelated work. A task needing a longer link declares the dependency or gets a no-test row. | autonomous: recommendation accepted 2026-11-09; low-confidence, no authoritative source. Cost if wrong: a named false refusal, fixed by a plan amendment |
| D5 | What counts as RED evidence? | A return from a `test-dev` dispatch, joined to its dispatch, whose `RED_EVIDENCE:` is not `none` or a template placeholder, whose `TESTS:` line reports `failed` of at least 1, and whose `COMMIT:` names a commit git can find. | Hook time limit is 5 seconds, so it cannot run the test. This is why hazard 3 stays partly open. `COMMIT: NONE` is refused on purpose; the attestation path (D9) recovers it. | autonomous: recommendation accepted 2026-11-09; source: `task-gate` digest template and the real T1 return |
| D6 | How is a Factory background return captured? | Handle `PostToolUse` on `TaskOutput` when the report says `Status: completed`, and join it to the launch by `task_id`. The conductor reads the worker with `TaskOutput` before dispatching its dependents. | The hook already fires on `TaskOutput`, and the real report text is in the session log. Factory can also deliver the report as a completion message. That path is not observable from this hook and is deferred, see Out of scope. | autonomous: recommendation accepted 2026-11-09; low-confidence, no authoritative source. Factory's hook docs do not state the payload for this case |
| D7 | How does a Claude background return join its dispatch? | A durable launch record in the ledger, written by a new default-interface method `RecordBackgroundLaunch`. | `ClaudeHookAdapter.LaunchLinks` is in memory and the hook is one process per event, so it cannot survive to the return. A default method keeps every existing test fake compiling. | autonomous: recommendation accepted 2026-11-09; source: adapter code |
| D8 | What does "complete" mean? | A joined return from an implementation-class target with `RESULT: PASS`, or `STATUS: PASS` when no `RESULT:` line exists. | `RESULT:` came from the retired `task-reviewer`. The shipped digest has only `STATUS:`. | autonomous: recommendation accepted 2026-11-09; source: `task-gate` skill |
| D9 | Who can attest (`ATTESTED: T1=red`)? | Only a return joined to a `planner`-class dispatch. | Matches `arbiter-escalation.md`. A worker writing the line earns nothing. | autonomous: recommendation accepted 2026-11-09; source: `arbiter-escalation.md` |
| D10 | How does a refusal state its reason? | Enrichers set an `evidence.<rule-id>` fact, and `AppendDecisionAsync` copies it to the rule's `Evidence` when the effect is not allow. | The envelope already prints `Evidence`. Undeclared facts already exist (`plan.digest`), so the trigger catalog is untouched. | autonomous: recommendation accepted 2026-11-09; source: `ArbiterHookDecisionEngine.EntriesOf` |
| D11 | Fix the table parser dropping rows like `T1 (RED)`? | No. | Accepting them changes `ExecutionDigest` for existing plans and marks their task files stale. A dropped row is simply not exempt, which fails safe. | autonomous: recommendation accepted 2026-11-09; source: `PlanDocumentParser.ExecutionDigest` |
| D12 | How to limit overlap with open pull request #562? | Reuse declared fact names. Add new logic in new files. In `ArbiterEvaluator` touch one line in `EvaluateDispatchAsync`, the `BuildLedgerEvent` body, and the step-0 loop plus signature of `AppendDecisionAsync`. In `ArbiterHookDecisionEngine` add one method. | Read #562's branch: it changes only the `Repeat` call in `AppendDecisionAsync`, a separate hunk. `TriggerFactBuilder`, `TriggerClassifier`, `ArbiterTriggerCatalog`, `DecisionLog`, `HeaderBlock` and `ArbiterEscalationEnvelope` are not touched here. In the rule file only the `KW-ARB-MODE-001` block changes. | autonomous: recommendation accepted 2026-11-09; source: #562 branch at its worktree |
| D13 | ADR? | No ADR. | Extends the fact sources of the existing Arbiter design (ADR 0053). Harvest goes to the architecture and runbook pages. | autonomous: recommendation accepted 2026-11-09; ADR waiver: refines existing architecture |
| D14 | Who writes the follow-up todo? | The planner, at finalization, in `docs/todo/arbiter-return-rules-after-header-strip.md`. | The todo records what this plan leaves out, so it lands with the plan. It needs no worker, and it keeps the T3 prompt free of paths under the planning directories. | autonomous: recommendation accepted 2026-11-09; source: the planner's write boundary in `AGENTS.md` Config Reg |
| AP1 | Approve implementation plan and proceed to test-first delivery execution | Approved | Scale is `extends-component` on KyberArbiter. Hazards 3, 4 and 5 stay partly open and hazard 6 stays open, as stated under Security hazards. D3, D4 and D6 are low-confidence. Tasks run test-first: `test-dev`, then `csharp-dev`, then `docs-dev`. | autonomous: recommendation accepted (AUTONOMOUS_APPROVAL: approve-and-execute) 2026-11-09 |

## Scale and Design harvest

Declared scale: `extends-component` on `KyberArbiter`.

## Design harvest

| Target component | Target doc type | Durable item | Destination |
|---|---|---|---|
| KyberArbiter | architecture | Where `ledger.red-evidence`, `ledger.completed-tasks` and `plan.test-contract.row` come from, how a return joins its dispatch, and the `evidence.<rule-id>` fact | `docs/kyber-arbiter/architecture.md` |
| KyberArbiter | runbook | What a MODE-001 or READY-001 refusal means, how to recover, Factory's `TaskOutput` requirement, and the stated limits | `docs/kyber-arbiter/runbook.md` |

## Investigation findings

Read revision `cdcfb0d5` directly from the worktree, because the index and docs servers are bound to another checkout. Discovery: codegraph 0, docs 2, direct reads about 45 (reason for each indexed-code read: server bound to another revision). Evidence from the reported run was read from its session log, decision log and plan.

The escalation's findings were checked and hold, with these additions:

- **The RED cell is not an exemption signal.** The reported plan's T2 (GREEN) row also says `n/a` there. Rule D2 needs the test-surface cell too.
- **The parser drops `T1 (RED)` and `T2 (GREEN)` rows** because the id cell fails `PlanTaskIdRegex`. Those tasks have no contract row. That is safe, see D11.
- **READY-001 has the same gap.** The real T2 decision answered `waiting`, and `CompletedTasks` keys on a marker from a retired agent.
- **The join is broken in more ways than the escalation states.** `PairDigest` is never written, so Factory, which has no call id, can never pair. A `Task` id is reused when a session is resumed, so the join must take the latest launch.
- **The real T5 prompt contains no backticked path,** so `delegation.paths` is empty. A check that the prompt's paths stay inside the task's files cannot bind an exemption, and it would wrongly refuse absolute worktree paths. Rejected, see hazard 5.
- **The real T1 return satisfies D5:** `TESTS: total=6 failed=6 passed=0`, `COMMIT: 0e480e6`, three-part `RED_EVIDENCE:`.
- **Factory delivers completion two ways.** The session shows both a `TaskOutput` result and a pushed completion message that fires only `UserPromptSubmit`, where this hook is not registered.
- **`git.changed-paths.since-dispatch` is keyed on a hash of the unstripped prompt,** so return rules stay blind on stripped dispatches. Left alone on purpose, see Out of scope.

## Security hazards

Today the gate fails closed, so each item below is an allow path this fix adds or depends on.

**Plainly: hazards 1 and 2 are closed. Hazards 3, 4 and 5 stay partly open. Hazard 6 stays fully open.** A worker can still report a failing test that did not fail, someone who can edit the plan and regenerate its task file can still mark a task no-test, and a dispatch can still name another exempt task for the same specialist.

| # | Hazard | Result | How |
|---|---|---|---|
| 1 | An empty list passes `exists: true`. | Closed | The rule tests `plan.task in ledger.red-evidence`. The fact is either absent or a list holding the current task, never empty. `ledger.completed-tasks` holds only ids the plan parser produced, so a `*` cannot act as a glob in `subset-of`. |
| 2 | RED evidence is not scoped. | Closed | Query is per `PLAN_FILE`, then intersected with the task and its direct dependencies. |
| 3 | Worker-written text claims RED. | Partly open | Closed: only `test-dev` returns count, placeholders and `none` do not, `failed` must be at least 1, the commit must exist, and a worker's `ATTESTED:` is ignored. **Open:** the Arbiter cannot prove the test failed, because the hook cannot run tests in its time limit. GREEN acceptance, the task gate and CI are the backstop. |
| 4 | Editing a plan grants an exemption. | Partly open | Closed: Draft and Blocked plans grant none, an edit after finalization mismatches the task-file digest, and both cells must agree. **Open:** whoever can edit the plan and regenerate the task file can still mark a task no-test, because approval is plain text with no signature. The change shows in the pull request diff, and every use is in the decision log as `not-required`. |
| 5 | A dispatch names an exempt task it is not doing. | Partly open | Closed: OWNER-001 requires the task's own specialist, READY-001 still requires its dependencies, and the id must exist in the plan. **Open:** a dispatch to the right specialist naming another exempt task, with different work in prose, passes. A path check was rejected (finding above). `KW-ARB-SCOPE-002` is the model check when a provider is set, and the return-side `KW-ARB-DIFF-001` is the intended backstop, which does not yet work on stripped dispatches. Tracked in `docs/todo/arbiter-return-rules-after-header-strip.md`. |
| 6 | `delegation.target: test-dev` is always `not-required`. | Stays open | Not added by this fix, and `test-dev` is how RED gets produced, so it cannot need RED first. The agent's contract is tests only. RED credit now requires a `test-dev` return, which narrows what the path can earn. |

## Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/Arbiter/ArbiterRedEvidenceFlowTests.cs`, `tests/KyberWeave.Tests/Arbiter/ArbiterMode001RuleTests.cs`, additions to `FactoryHookAdapterTests.cs` and `ClaudeHandbackTests.cs` | See T1 commands | Real hook runs refuse or allow per the Goal, and refusals name the reason | The filter fails on assertions, with no build error, on the unmodified source | n/a (T2 carries GREEN) |
| T2 | Same classes | See T2 commands | Same assertions, unweakened | From T1 | Every named class passes with a non-zero count |
| T3 | No new test — documentation and skill wording; content tests and docs gates replace it | See T3 commands | Docs state the new behaviour and limits | n/a | Named content classes pass; `docs validate` and `docs drift` report zero findings |

## Tasks

### T1 — Failing contract tests for RED evidence and no-test tasks (RED)

- **Files:**
  - `tests/KyberWeave.Tests/Arbiter/ArbiterRedEvidenceFlowTests.cs` — new class `ArbiterRedEvidenceFlowTests`
  - `tests/KyberWeave.Tests/Arbiter/ArbiterMode001RuleTests.cs` — new class `ArbiterMode001RuleTests`
  - `tests/KyberWeave.Tests/Arbiter/FactoryHookAdapterTests.cs` — class `FactoryHookAdapterTests`, added cases only
  - `tests/KyberWeave.Tests/Arbiter/ClaudeHandbackTests.cs` — class `ClaudeHandbackTests`, added cases only
  - `tests/KyberWeave.Tests/Fixtures/arbiter-hooks/factory/post-task-launched.json` — new
  - `tests/KyberWeave.Tests/Fixtures/arbiter-hooks/factory/post-taskoutput-completed.json` — new
  - `tests/KyberWeave.Tests/Fixtures/arbiter-hooks/factory/post-taskoutput-running.json` — new
- **Depends on:** none
- **Required skills:** `test-dev`

- **Session:** `red-evidence-red`
- **Objective:** Write tests that fail on assertions against today's code. Use only symbols that exist now, so the run compiles. Drive the real engine the way `ArbiterUnobservableReturnsTests.RealCommand` does: `HookCommand.Run` over `HarnessAdapterRegistry.CreateDefault` and a real `ArbiterHookDecisionEngine`, in a temporary git repository with a real commit, with a config enabling only `KW-ARB-MODE-001` and `KW-ARB-READY-001`. Read outcomes from stdout and `decisions.jsonl`.
- **`ArbiterRedEvidenceFlowTests` cases.** Each is one test. The plan fixture mirrors a real one: T1 (test-dev, RED), T2 (csharp-dev, `Depends on: T1`), T5 (typescript-dev, row `No new test — build and lint replace it`, RED cell `n/a`), T9 (csharp-dev, no dependencies, real test cell, RED cell `n/a`), body `**Status:** Ready`.
  - Factory T5 dispatch is allowed; MODE-001 answers `not-required`.
  - T5 is refused when the plan body says `**Status:** Draft`, and the envelope `EVIDENCE:` names the status.
  - T5 is refused when the task file holds a stale string `execution-digest`, and allowed when it holds the current one (`PlanDocumentParser.ExecutionDigest`), or when the file has a differently shaped digest.
  - T9 is refused: `n/a` in the RED cell alone is not an exemption.
  - T2 with no RED return is refused, and `EVIDENCE:` names T1 rather than the generic text.
  - Factory full flow: pre T1, `Task` post with launch text and `task_id:`, `TaskOutput` post with the completed report, then T2 is allowed with MODE-001 `present` and READY-001 `ready`.
  - Resume: same `task_id`, first return `RED_EVIDENCE: none`, second launch and a real return, then T2 is allowed. After the first return only, T2 is refused.
  - `TaskOutput` with `Status: running` records nothing.
  - RED under another `PLAN_FILE` does not clear T2.
  - T1's RED does not clear T9.
  - T2 refused for each of: `RED_EVIDENCE: none`; the literal template placeholder; `TESTS: ... failed=0`; `COMMIT: NONE`; a commit id git cannot find; the same digest returned by a `csharp-dev` dispatch.
  - `ATTESTED: T1=red` from a worker return is ignored. From an `architect` dispatch return it clears T2.
  - `ATTESTED: *=complete` from an `architect` return does not satisfy READY-001 for T2.
  - Claude foreground: pre and `PostToolUse` `completed` with the same `tool_use_id`, then T2 is allowed.
  - Claude background: `PostToolUse` `async_launched` with `agentId`, then `SubagentHandback` with `agent_id`, each as a separate `HookCommand.Run`, then T2 is allowed.
- **`ArbiterMode001RuleTests` cases** (`RuleEngine.Decide` on shipped rules): `plan.task` T2 with `ledger.red-evidence` `["T2"]` answers `present`; with `[]`, `["T1"]`, or absent answers `missing`; `plan.test-contract.row` `true` or `RED required` does not answer `not-required`; `Not applicable` does.
- **`FactoryHookAdapterTests` additions:** `TaskOutput` post with a completed report reaches the engine with call id `bg:<task_id>`; running does not reach it and loads no config; a `Task` post with launch text passes `bg:<task_id>`; object-shaped `tool_response` is read through its string fields.
- **`ClaudeHandbackTests` addition:** a handback passes call id `bg:<agent_id>`.
- **Fixture text:** use the real shapes: launch `Task launched in background.\ntask_id: <uuid>\nsession_id: <uuid>\nsubagent_type: test-dev\n...`; report `Task ID: <uuid>\nSubagent Type: Test-dev\nDescription: ...\nStatus: completed\nDuration: 1.0s\n\n<report>`. A post payload's `tool_input.prompt` must have its header lines removed, as the harness sends it after the strip; a fixture that keeps them passes for the wrong reason. An allowed dispatch to an implementation target writes `permissionDecision: allow` with `updatedInput`, so assert "not denied" rather than empty stdout.
- **Commands:**
  - `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterRedEvidenceFlowTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterMode001RuleTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.FactoryHookAdapterTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ClaudeHandbackTests"`
- **Acceptance criteria:** The build passes. The filter runs a non-zero count. The new cases fail on assertions. Existing cases in the two edited classes still pass. Report the failures on the `RED_EVIDENCE:` line.

### T2 — Wire the facts, return capture and refusal reasons (GREEN)

- **Files:**
  - `src/KyberWeave.Core/Arbiter/Rules/default-rules.yml` — rule `KW-ARB-MODE-001` only
  - `src/KyberWeave.Core/Arbiter/Facts/ArbiterLedgerEvent.cs` — `ArbiterReturnMarkers`, `ArbiterLedgerEvent.PairDigest` doc
  - `src/KyberWeave.Core/Arbiter/Facts/InFlightLedger.cs` — `MatchPre`, new overloads
  - `src/KyberWeave.Core/Arbiter/Facts/ArbiterReturnMarkerReader.cs` — new
  - `src/KyberWeave.Core/Arbiter/Facts/LedgerFacts.cs` — new
  - `src/KyberWeave.Core/Arbiter/Plans/PlanTestContract.cs` — new
  - `src/KyberWeave.Core/Arbiter/Plans/PlanDocument.cs` — `PlanContractRow` remark
  - `src/KyberWeave.Core/Arbiter/ArbiterEvaluator.cs` — `EvaluateDispatchAsync`, `AppendDecisionAsync`, `BuildLedgerEvent`
  - `src/KyberWeave.Arbiter/Hooks/HookPlanReader.cs` — `Enrich`
  - `src/KyberWeave.Cli/Commands/Arbiter/ArbiterCommandComposition.cs` — class `ArbiterCliPlanReader`, method `Enrich`
  - `src/KyberWeave.Arbiter/Hooks/IContextualHookDecisionEngine.cs` — new default method
  - `src/KyberWeave.Arbiter/Hooks/ArbiterHookDecisionEngine.cs` — one new method
  - `src/KyberWeave.Arbiter/Hooks/Adapters/FactoryHookAdapter.cs` — `IsPassThrough`, `HandlePost`
  - `src/KyberWeave.Arbiter/Hooks/Adapters/ClaudeHookAdapter.cs` — `HandlePost`, `HandleHandback`
- **Depends on:** T1
- **Required skills:** `csharp-dev`

- **Session:** `red-evidence-green`
- **Objective:** Make T1 pass without weakening any assertion.
- **Rule.** In `KW-ARB-MODE-001`, replace the clause `{ fact: ledger.red-evidence, exists: true }` with `{ fact: plan.task, in: ledger.red-evidence }`. Change nothing else in the file.
- **`PlanTestContract`** (new, `KyberWeave.Core.Arbiter.Plans`): `Classify(PlanDocument plan, string planText, string resolvedPlanPath, string taskId)` returns `(string? RowFact, string? RefusalReason)`.
  - No row: `RowFact` null.
  - Row not exempt under D2: `RowFact` is the string `RED required`. Never the raw cell.
  - Exempt under D2 and bound under D3: `Not applicable`.
  - Exempt but not bound: `RED required`, with `RefusalReason` saying which condition failed.
  - Approval reads the body `Status:` line with emphasis removed, so both `**Status:** Ready` and `**Status: Ready**` match. Approved means starting with Ready, In progress or In-progress. With no body line, frontmatter `status` of `ready` or `in-progress`.
  - Task file: the plan path with `.md` replaced by `.tasks.json`. Read `execution-digest` when it is a JSON string and compare to `PlanDocumentParser.ExecutionDigest(plan)` ignoring case. Missing, unreadable or another shape gives no binding.
- **Both plan readers:** replace the boolean `plan.test-contract.row` with `RowFact`, and when `RefusalReason` is set add fact `evidence.KW-ARB-MODE-001` labelled derived.
- **`ArbiterReturnMarkers`:** add optional trailing `[property: JsonPropertyName("tests-failed")] int? TestsFailed` and `[property: JsonPropertyName("commit")] string? Commit`.
- **`ArbiterReturnMarkerReader`** (new): `Read(string? output, Func<string, bool> commitExists)` returns markers or null. Normalise CRLF. Take the last line-anchored match of each: `^RED_EVIDENCE:` (trimmed, capped at 500 characters); `^RESULT:\s+(PASS|FAIL)\b`, else `^STATUS:\s+(PASS|FAIL|ESCALATION)\b` mapped PASS to PASS and others to FAIL, into `TaskReview`; `^STATUS:\s+[A-Z_]+` into `PlannerStatus`; `^ATTESTED:` split on commas; `^TESTS:.*\bfailed=(\d+)\b`; `^COMMIT:\s*([0-9a-fA-F]{7,64})\s*$`, set only when `commitExists` is true.
- **`ArbiterEvaluator.BuildLedgerEvent`:** set `PairDigest` to lowercase SHA-256 of `target + "\0" + HeaderBlock.Strip(prompt)` when the prompt is not empty. On the post phase set `Returns` from the reader, with `commitExists` calling `GitFacts.RefExists(ev.Cwd, sha)` when `ev.Cwd` is set, otherwise false.
- **`InFlightLedger`:**
  - `MatchPre`: when no pre matches by call id and `PairDigest` is null, and `CallId` starts with `bg:`, return the `PreId` of the latest earlier post event with the same `CallId` and a non-null `PreId`. Latest, because a resumed session reuses the id.
  - New overloads `RedEvidenceTasks(string planFile)` and `CompletedTasks(string planFile)`, joining each post to its pre by `PreId`, matching the pre's `Headers.PlanFile` after trimming and normalising backslashes, implementing D5, D8 and D9. Target class comes from `TriggerClassifier.TargetClassFor`. The parameterless overloads stay as they are.
- **`LedgerFacts.Enrich(facts, ledger, classification)`** (new), called in `EvaluateDispatchAsync` right after `GateReportFacts.Enrich`, only when the bag contains `ledger.red-evidence`. With `PLAN_FILE` and `plan.task` known:
  - `ledger.red-evidence` becomes a one-item list holding `plan.task` when the plan's RED set intersects the task and its `plan.task.depends-on`; otherwise it stays null, never empty.
  - `ledger.completed-tasks` becomes `plan.task.depends-on` intersected with the plan's completed set.
  - Set `evidence.KW-ARB-MODE-001` when not credited, unless a reason is already set: name the task, the plan, the tasks that could supply RED, and for each the reason its latest `test-dev` return did not count, or that no return was recorded. Say that on Factory a finished worker must be read with `TaskOutput`.
  - Set `evidence.KW-ARB-READY-001` naming the dependencies not recorded complete.
- **`AppendDecisionAsync`:** add a final `ArbiterFactSet facts` parameter, passed by both callers. In the step-0 loop set `Evidence` from `evidence.<rule id>` when the value is a string and the effect is not allow. Leave the rest of the method unchanged, because open pull request #562 edits the `Repeat` line.
- **`IContextualHookDecisionEngine.RecordBackgroundLaunch`:** default no-op method with parameters `(string harness, string? target, string prompt, string launchId, HookContext context, string? sessionId)`. **`ArbiterHookDecisionEngine`** implements it by appending one post-phase ledger event, with `CallId` `bg:<launchId>`, the same `PairDigest`, target and session, and no rule evaluation. Move the digest into one internal helper shared with `BuildLedgerEvent`.
- **`FactoryHookAdapter`:**
  - `IsPassThrough` on `PostToolUse`: `TaskOutput` passes through unless its response text has a line `Status: completed`.
  - `HandlePost`: for `TaskOutput`, read text from `tool_response` (a string, or all string values of an object or array joined by newlines), take `tool_input.task_id`, and call `DecidePost` with target null, empty prompt and call id `bg:<task_id>`.
  - For `Task`, when the response text has a line `task_id: <id>`, pass `bg:<id>` as the call id. Otherwise null, as today.
- **`ClaudeHookAdapter`:** on `async_launched` with an `agentId`, also call `RecordBackgroundLaunch` when the engine is contextual. In `HandleHandback` pass `bg:<agent_id>` as the call id.
- **`PlanDocument`:** correct the `RedEvidenceRequired` remark, which claims MODE-001 reads the cell directly.
- **Commands:**
  - `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterRedEvidenceFlowTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterMode001RuleTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.FactoryHookAdapterTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ClaudeHandbackTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ClaudeHookAdapterTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterUnobservableReturnsTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterLedgerTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterDefaultRulesTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterExistsFalseRuleTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterRealEngineTests"`
- **Acceptance criteria:** Every named class passes with a non-zero count and no test weakened. `ArbiterExistsFalseRuleTests` needs no edit, because the rule keeps its `exists: false` clause. The trigger catalog is unchanged.

### T3 — Documentation and shipped wording

- **Files:**
  - `products/kyber-squad/skills/task-gate/SKILL.md` — `## Completion digest`, the sentence beginning "Anything but a literal"
  - `products/kyber-squad/skills/delivery-execution/SKILL.md` — sequencing text for background workers
  - `products/kyber-squad/agents/architect/references/test-first-contract.md` — the no-test bullet
  - `docs/kyber-arbiter/architecture.md` — facts paragraph and rule table note
  - `docs/kyber-arbiter/runbook.md` — refusal and recovery section
- **Depends on:** T2
- **Required skills:** `docs-dev`

- **Session:** `red-evidence-docs`
- **Objective:**
  - `task-gate`: state that RED counts only from a `test-dev` return with `TESTS: failed` of at least 1 and a commit git can find. Keep the strings `RED_EVIDENCE: <runner filter> — <failing tests> — <reason>` and `RED_EVIDENCE: none`, which a test pins.
  - `delivery-execution`: on a harness that reports background work out of band (Factory), read a finished worker with `TaskOutput` before dispatching a task that depends on it, because that read is what the Arbiter records.
  - `test-first-contract.md`: a no-test row writes `No new test — <what replaces it>` in the test cell and `n/a` in the RED cell.
  - `architecture.md` and `runbook.md`: per the Design harvest, including the hazards left open.
- **Commands:**
  - `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.ArbiterTestDevContractTests|FullyQualifiedName~KyberWeave.Tests.SquadCanonicalContentTests"`
  - `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - `dotnet run --project src/KyberWeave.Cli -- docs drift .`
- **Acceptance criteria:** Both content classes pass with a non-zero count. Both docs commands report zero findings.

## Dependency graph

T1 → T2 → T3. Each step consumes the previous step's output. T2 makes T1's tests pass, and T3 documents what T2 built.

## Out of scope

- **Making return rules work on stripped dispatches.** `KW-ARB-DIFF-001` and the snapshot join in `src/KyberWeave.Arbiter/Hooks/HookGitFacts.cs` have never run on these dispatches, and turning them on adds new blocking behaviour that needs its own contract. Tracked in the follow-up todo (D14).
- **Wiring `ledger.in-flight.paths`.** It adds no allow path, so this fix does not need it. Tracked in the follow-up todo (D14).
- **Factory's pushed completion message.** Its hook payload is undocumented and the Arbiter is not registered for that event. Tracked in the follow-up todo (D14).
- **The worktree root lookup** in `src/KyberWeave.Arbiter/Hooks/HookCommand.cs`. A separate defect from the same run.
- **The table parser and malformed task files.** See D11. The reported run's task file fails `KW-DOC-TASKS-001` on its own.
- **`src/KyberWeave.Core/Arbiter/Facts/TriggerFactBuilder.cs` and the other files of pull request #562.**

## Risks and verification gates

- **This fix's own delivery.** A run where `conductor` dispatches T2 is refused by this very bug on the installed binary. A `bug-crusher` dispatch classifies as `investigate` and does not run MODE-001 (`TriggerClassifier.ClassifyTrigger`), so deliver through that route.
- **False refusal is the expected failure mode.** Wording outside D2 or `COMMIT: NONE` refuses with a named reason. Recovery is a plan amendment or attestation.
- **Other harnesses.** Only Factory and Claude background returns are handled specially. Harnesses with synchronous returns work through the call-id and digest join.
- **Rebase.** This branch rebases after #562 merges. Expect at most a small conflict in `AppendDecisionAsync`.
- **Gates:** the T1, T2 and T3 commands above; each developer task ends with its own `task-gate` run.
- **Live check after merge:** rerun the reported Factory plan on the release containing this fix, and confirm T5 dispatches and T2 waits for T1's real return.

## Review and closeout

The end-of-run review is the `code-reviewer` council under the `code-review` skill, over the whole change. `docs-dev` closeout harvests per the Design harvest, adds the ADR waiver from D13 to the archive row, removes any task file, and archives this plan in the pull request that delivers it.
