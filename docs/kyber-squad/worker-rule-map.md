---
id: squad/worker-rule-map
title: Worker rule map — one home and one check per rule
doc-type: reference
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-11
keywords:
  - worker rule map
  - rule home
  - full test suite ban
  - clean-up pass
  - circuit breaker
  - discovery order
  - completion digest
---

# Worker rule map — one home and one check per rule

Every sentence a worker reads is re-sent on every round trip of every worker, so the same rule
written in six agent bodies costs six times the tokens and drifts six ways. The rule map is what
makes removing that duplication safe: for each duplicated sentence it records where the sentence
lives now, the single place it lives after the trim, and the check that still enforces it once it
is there. A rule deleted without its home recorded is simply lost, and nothing would notice.

**The removals that were unblocked have landed**, and each row below records where the sentence
was removed from and what its one home now is. This page was written with every duplicated
sentence still in place, so the removal task could be checked against it row by row; the rows
marked deferred keep their text, for the reason given beside them.

Two parts of this page have different lifetimes. The page itself is a permanent reference
(decision D36 of the plan that created it): it says what enforces each rule, which stays true and
is what stops the text growing back. The per-row inventory is a snapshot taken in the tree before
the removals — the "current homes" column is expected to go stale, and each row's status is
updated as the removal lands rather than the page being archived. The plan that instructs this
work, [`2026-11-07-conductor-run-speed-shrink-reads.md`](../archive/plans/2026-11-07-conductor-run-speed-shrink-reads.md),
was archived at that slice's closeout and is provenance for these rows, not current guidance;
this page is not archived.

## How the rows were found

The rows were derived by searching this tree, not copied from the plan's estimate: decision D27
records that the count is 27 files rather than an earlier 14 and requires the map to be rebuilt by
search, and the Design's "938 bytes across agents" is an estimate in the same breath. Where the
search disagrees with the plan, both numbers are given below and the search wins.

- **The ban.** `grep -rilE "full (test )?suite" products/` matches 26 files in
  `products/kyber-squad/`. The plan says 27. The one extra file a looser search catches is
  `agents/code-reviewer.md` ("a full gate suite"), which states a different rule — one council per
  run, not the full-suite ban — so 26 is the count of files carrying *this* rule.
- **The standard templates.** The plan says "the 5 standard templates keep their line". Search
  finds 9 of the 14 templates under `products/kyber-squad/standards/` carrying a test-runner or
  ban statement: three state the ban verbatim (`go`, `rust`, `typescript`) and six state the
  filtered-run rule in their `## Completion` section (`csharp`, `data-access-layer`, `maui`,
  `pulumi`, `sql`, `test`). Every template keeps its line either way, for the reason in
  [what stays duplicated](#what-stays-duplicated-and-why).
- **The Discovery order.** 21 agent bodies and one skill carry a `## Discovery order` heading; 20
  of those agents are `invocation: subagent`, which is the only invocation the baseline injector
  reaches.
- **The completion-gate block.** Six agent bodies carry the same "Completion gate — diagnostics"
  block, bullet for bullet, with the stray database-specific and worktree bullets beside it:
  `csharp-dev`, `test-dev`, `dal-dev`, `maui-dev`, `pulumi-dev`, `react-dev`.

```mermaid
flowchart LR
    A["Sentence duplicated<br/>across agent bodies"] --> B1["shared/worker-baseline.md<br/>injected once per worker render"]
    A --> B2["skills/task-gate/SKILL.md<br/>and references/rework.md"]
    A --> B3["host standard<br/>## Completion section"]
    B1 --> E1["SquadWorkerBaselineInjectionTests<br/>exactly once, per target"]
    B2 --> E2["SquadPerformanceContractTests<br/>ArbiterTestDevContractTests"]
    B3 --> E3["TestCodingStandardDocuments...<br/>WorkerSurfacesSpecifyBuildOnce..."]
```

Read every "Enforced by" cell as the answer to one question: if this sentence were deleted and
nobody noticed, what fails? Where the cell says "no test pins this", the row is a finding, not a
gap to paper over — the seeded-defect trial is the only thing that would catch that loss.

## Report formats and the completion digest

Single home after the trim: [`skills/task-gate/SKILL.md`](../../products/kyber-squad/skills/task-gate/SKILL.md),
the skill every developer worker loads when it finishes.

| ID | Rule — what a worker must do | Removed in T52 from | Single home after | Enforced by |
|---|---|---|---|---|
| A1 | End the task with the six-field digest, in this order: `STATUS`, `FILES`, `TESTS`, `COMMIT`, `REMOTE_HEAD`, `TASK_GATE`. | `agents/csharp-dev.md` → `## Completion digest`, which now points at the skill. The copy in `skills/delivery-execution/SKILL.md` and the conductor's requirement line stay, for the reason in [what stays duplicated](#what-stays-duplicated-and-why). | `skills/task-gate/SKILL.md` → `## Completion digest`, carrying the contiguous template | `SquadPerformanceContractTests.TheTaskGateSkillSpecifiesTheExplicitSixFieldCompletionDigestSchema`; `SquadPerformanceContractTests.SharedExecutionSkill_DeclaresDeterministicTaskGate_AndConductorOffloadsIt` holds the block in `delivery-execution` |
| A2 | Record RED evidence on one machine-readable line: `RED_EVIDENCE: <runner filter> — <failing tests> — <reason>`, or `none`. | `agents/test-dev.md` → `## Completion digest`, which now points at the skill | the same `skills/task-gate/SKILL.md` section, as a seventh field | `ArbiterTestDevContractTests` — three cases, all reading `skills/task-gate/SKILL.md`; the ledger fact `ledger.red-evidence` and Arbiter rule `KW-ARB-MODE-001` consume the value |
| A3 | When the circuit breaker trips, return the escalation block instead: `STATUS: ESCALATION`, one closed `CIRCUIT_BREAKER_TRIGGER` token and its payload lines. | `agents/csharp-dev.md`, `agents/test-dev.md` and `agents/github-devops.md` → `## Completion digest`. The conductor's exemption line and the closed token set in `skills/task-gate/SKILL.md` → `## Escalation` stay | `skills/task-gate/references/rework.md` → `## The escalation` | `SquadCanonicalContentTests.TaskGateSkillDefinesTheDeterministicReviewGateContract`, which asserts the closed four-token set on the skill; `WorkerRuleSingleHomeTests.TheFailureLoopRulesAreInNoDeveloperAgentBody` |

The three agent bodies above are the only developer agents whose report *format* is the six-field
block. The other developer agents report their own agent-specific completion plus a `TASK_GATE:`
line, which the shared execution contract calls their standard completion, and that stays.

## The failure loop

Single home after the trim: `products/kyber-squad/skills/task-gate/references/rework.md`, the
reference the task gate loads after a failed fix. That file did not exist when this page was
written, which is why these rows were deferred then; T52 wrote it first, from the text in the
bodies, and removed the duplicates in the same change.

Current homes for B1 to B5 were `agents/csharp-dev.md`, `agents/test-dev.md` and
`agents/github-devops.md`, each in `## Workflow` step 6 (the four-bullet "JEV Checkpoints and
Iteration Circuit-Breaker" block) and in `## Hard rules` (the "Never enter an unconstrained
test-fix loop" bullet). The block is gone from all three, step numbering closes the gap, and the
hard-rule bullet is now a one-line pointer to the reference.

| ID | Rule — what a worker must do | Removed in T52 from | Single home after | Enforced by |
|---|---|---|---|---|
| B1 | Cap the work at 3 incremental test-fix iterations against one failing fixture or failure cluster within one dispatch, then halt and trip the breaker. | `agents/csharp-dev.md`, `agents/test-dev.md`, `agents/github-devops.md` → `## Workflow` step 6 | `skills/task-gate/references/rework.md` → `## The iteration cap` | `WorkerRuleSingleHomeTests.TheFailureLoopRulesAreInNoDeveloperAgentBody`: `rework.md` carries `CIRCUIT_BREAKER_TRIGGER`, and none of the three bodies may |
| B2 | Checkpoint 1, blast radius: if the fix needs files outside the authorised scope, halt and raise `BLAST_RADIUS_EXCEEDED`. | same 3 bodies | `rework.md` → `## The three JEV checkpoints` | as B1 |
| B3 | Checkpoint 2, oscillation: a one-way regression costs one of the 3 iterations; only A→B→A or a repeating failure signature raises `THRASH_OSCILLATION_DETECTED`. | same 3 bodies | `rework.md` → `## The three JEV checkpoints` | as B1 |
| B4 | Checkpoint 3, invariant contradiction: never bend production code to satisfy a legacy fixture that contradicts the approved design; raise `INVARIANT_CONTRADICTION`. | same 3 bodies | `rework.md` → `## The three JEV checkpoints` | as B1 |
| B5 | Never enter an unconstrained test-fix loop: 3 non-converging iterations, an oscillation or a contradiction means escalate rather than keep trying. | same 3 bodies, `## Hard rules` (each now carries a pointer only) | `rework.md` → `## Never enter an unconstrained test-fix loop` | as B1 |
| B6 | Conductor side: at most 2 rework dispatches per failure cluster, the tally kept on the execution artifact, and an A/B oscillation tripwire that fires immediately. | `agents/conductor.md` → `## Shared lifecycle invariants` | `skills/delivery-execution/SKILL.md` → `## Iteration circuit-breaker and loop detection` | `SquadCanonicalContentTests.LoadConductorDefinesIterationCircuitBreakerAndOscillationDetection` asserts the skill and deliberately "not the conductor's own body" |

## The full-suite ban

Single home after the trim: [`shared/worker-baseline.md`](../../products/kyber-squad/shared/worker-baseline.md),
the always-on block `SquadWorkerBaselineInjector` applies to every subagent render. Its line
reads:

> **Never run the full test suite.** Run the test classes your task names. The review council and
> CI run the full suite on the integrated head.

| ID | Rule — what a worker must do | Removed in T52 from | Single home after | Enforced by |
|---|---|---|---|---|
| C1 | Never run the full test suite, even for a cross-cutting change — the review council and CI run it; run the tests covering the change and any test that references a changed symbol. | 11 agent bodies, one identical bullet each: `dal-dev`, `github-devops`, `go-dev`, `maui-dev`, `pulumi-dev`, `python-dev`, `react-dev`, `rust-dev`, `sql-database-architect`, `tauri-dev`, `typescript-dev` (`## Hard rules` for all but `sql-database-architect` and `tauri-dev`, whose bullet sat in `### Completion gate`) | the baseline block | `SquadPerformanceContractTests.TheSharedBaselineIsTheOnePlaceTheFullSuiteBanIsWritten` — the baseline matches `never run the full test suite` and **no** file under `agents/` may; `SquadWorkerBaselineInjectionTests.EachWorkerRenderCarriesTheBaselineExactlyOnce`, one case per shipped target |
| C2 | Inside the "build once, test many" bullet: the same ban, with the clause "even for a cross-cutting change". | `agents/csharp-dev.md` → `## Workflow` step 7, the ban sentence only. The rest of the bullet stays: "only the test classes … task touched", "once per review cycle", "integrated head", "`TESTS: total=0` with the reason", "shared test infrastructure", "targeted set" and "cross-cutting" are all still there | the baseline block | as C1, plus `TaskEndRunsTouchedTestClassesAndLeavesTheFullSuiteToTheCouncil` and `WorkerSurfacesRunATargetedSetOnSharedTestInfrastructure` |
| C3 | The same ban inside the same bullet. | `agents/test-dev.md` → `## Workflow` step 5, the ban sentence only, on the same terms as C2 | the baseline block | as C2 |
| C4 | A dev agent never runs the full suite; it runs the tests covering its change. | `skills/csharp-dev/references/build-commands.md` → `## Run tests`: the ban half of the sentence. The filter command and the "the review council and CI run it" sentence stay, so a host reading the reference alone still sees which run is the gate | the baseline block | **No test pins this line**; the seeded-defect trial is the only check that the injected line replaces it |

Two rows that look like this rule but are **not** it, and stay where they are:

- `agents/conductor.md` — "No worker runs the full suite, and the conductor never dispatches a full
  run". This is the dispatcher's rule, not a worker's, and the baseline is never injected into the
  conductor (the injector's `Apply` covers `SquadInvocation.Subagent` only).
- The C# and test standards state the same expectation in their `## Completion` sections. See
  [what stays duplicated](#what-stays-duplicated-and-why).

## Discovery order, the packet rule, and batched reads

Intended single home: the baseline block, whose first and second paragraphs are the Discovery
order and the plan-read ban, and whose fourth is the batched-read line.

| ID | Rule — what a worker must do | Homes, and what T52 removed | Single home after | Enforced by |
|---|---|---|---|---|
| D1 | Discover before you read: ask `codegraph_explore` first, then the kw-docs tools, and read a file directly only for what an index does not cover. | `## Discovery order` in 20 subagent agent bodies (`architect`, `azure-reader`, `bug-crusher-investigator`, `code-reviewer`, `csharp-dev`, `dal-dev`, `docs-dev`, `github-devops`, `go-dev`, `maui-dev`, `pulumi-dev`, `python-dev`, `react-dev`, `review-lens`, `review-triage`, `rust-dev`, `sql-database-architect`, `tauri-dev`, `test-dev`, `typescript-dev`). **Left in place** | the baseline block | `SquadWorkerBaselineInjectionTests.TheBaselineTextLivesInOneSharedSourceFile` and `.EachWorkerRenderCarriesTheBaselineExactlyOnce` cover the baseline — **but the section stays**: `SquadCanonicalContentTests.LoadCodeReadingAgentsDirectIndexedDiscoveryBeforeRawReads` still requires, per body, a `## Discovery order` section naming `codegraph_explore`, both docs tools and "Read or search a file directly" in that order |
| D2 | Report what the discovery cost: `Discovery: codegraph N, docs N, direct reads N`, with a reason for each direct read of indexed code. | the same agent bodies; `skills/delivery-execution/SKILL.md` → `## Developer exit gate`; `agents/conductor.md` → `## Shared lifecycle invariants`. **Left in place** | `skills/task-gate/SKILL.md` as a report format — the baseline carries no such line today, so the baseline would have to be amended for this to move there | the same canonical pin as D1; `ConductorSurfacesSpecifyDiscoveryHeaderPrecedesDigest` also requires the phrase in the conductor and in `delivery-execution` |
| D3 | Plans are the conductor's: do not open a file under `<plan-index>` or `<specification-index>`, or any plan folder; return a blocker when the packet is insufficient. | `## Scope` in 13 developer agent bodies: `csharp-dev`, `dal-dev`, `github-devops`, `go-dev`, `maui-dev`, `pulumi-dev`, `python-dev`, `react-dev`, `rust-dev`, `sql-database-architect`, `tauri-dev`, `test-dev`, `typescript-dev` — the duplicate sentence removed, "Your packet is your whole scope." kept. `docs-dev` keeps only its closeout exception, which is the one part of the rule that is its own | the baseline block ("**Plans are the conductor's.**") | `SquadWorkerBaselineInjectionTests`, whose required phrase list includes `<plan-index>`; Arbiter rule `KW-ARB-PLANREAD-001` on the `delegate` trigger refuses a dispatch that sends a worker into a plan. No test pins the agent-body copy |
| D4 | Batch independent reads into one round trip per question. | nowhere in the agent bodies — it is new text in the baseline | the baseline block | `SquadWorkerBaselineInjectionTests` ("one round trip"). Listed so nothing "restores" it into agent bodies |

The two review seats report discovery too. `review-lens` and `review-triage` put
`Discovery: codegraph N, docs N, direct reads N` directly above their attestation line, below any
`Index fallback:` line, and the verdict engine flags a findings or `NO FINDINGS` seat that wrote
none, that read files directly with no index lookup and no fallback line, or that wrote an
`Index fallback:` value outside the three modes, as `KW-REVIEW-044`. A `SKIPPED` seat is exempt. The
correctness seat adds one closing line, `Input sites: N traced, M files with none`, directly
above its `Discovery:` line; a correctness seat that reported findings or `NO FINDINGS` without
it draws `KW-REVIEW-047`, and a `SKIPPED` correctness seat is exempt. The closing-line order and the input-site table live in `review-lens`
and the correctness lens file, not in the baseline.

## The task-end fix pass and build isolation

Single homes after the trim: `skills/resharper-clt/SKILL.md` for the mechanical fix pass,
`skills/task-gate/SKILL.md` for the exit-gate procedure and `.NET isolation`, and the host
coding standards' `## Completion` section for the commands themselves.

| ID | Rule — what a worker must do | Homes, and what T52 removed | Single home after | Enforced by |
|---|---|---|---|---|
| E1 | Fix mechanically before sweeping: `dotnet format`, `dotnet format analyzers`, then `dotnet jb cleanupcode`, each scoped with `--include` to the changed files. | removed from 5 agent bodies, `## Workflow`: `csharp-dev`, `dal-dev`, `maui-dev`, `pulumi-dev`, `test-dev` | `skills/resharper-clt/SKILL.md` → `## Running the fix pass before you claim work is complete` | `WorkerRuleSingleHomeTests.TheCleanupCodePassIsNotRequiredInAnyAgentBody` and `.TheCleanupCodePassKeepsItsHomeInTheSkillThatRunsIt`. Decision D43 turned the pass off in agent bodies |
| E2 | The task-end pass does not run `dotnet format analyzers`; the style gate covers what it would report. | removed with E1's bullet from the same 5 bodies | the `format-style` task gate, declared once in `.kyber-weave/kyber-weave.yml` → `review.task-gates` | the gate itself: `dotnet format KyberWeave.sln style --verify-no-changes --severity warn`, aliased to every .NET agent |
| E3 | Do not run ReSharper InspectCode from a task gate; inspection is a review gate, run once per review cycle. | removed from 5 agent bodies: `csharp-dev`, `dal-dev`, `maui-dev`, `pulumi-dev`, `test-dev` | `skills/resharper-clt/SKILL.md` → `## Two cadences, and which one you are in`; `skills/task-gate/SKILL.md` → `## Optional fix pass for .NET work` and `## Boundaries` | the `inspectcode` entry under `review.gates` |
| E4 | Take a language-diagnostics baseline over the files you may change before the first edit, write it under `.agent_scratchpad`, and cite it in the message body beside the digest. | **left in place** in 6 agent bodies: `csharp-dev`, `dal-dev`, `maui-dev`, `pulumi-dev`, `react-dev`, `test-dev` | `skills/task-gate/SKILL.md` would have to state the procedure; it does not today, and the **citation** half is pinned in the two bodies | `SpecialistPromptsClarifyArtifactAndBaselinePathsBelongInAccompanyingMessageBody` requires the baseline-plus-message-body line in `csharp-dev` and `test-dev` |
| E5 | Sweep again after the last edit, over your own files only; never workspace-wide. | **left in place** in 6 agent bodies | `skills/task-gate/SKILL.md` does not carry it, so a removal would lose the rule. See [what T52 left](#what-t52-left-and-why) | **No test pins this.** The reason it exists — a workspace-wide sweep reads another worker's half-finished tree — is also stated in the conductor's dispatch rules, which are not this row |
| E6 | A scoped build, `tsc --noEmit`, `dotnet test` or `git diff --check` measures something else and does not clear the completion gate. | **left in place** in 6 agent bodies | `skills/task-gate/SKILL.md` does not carry it today. See [what T52 left](#what-t52-left-and-why) | **No test pins this** |
| E7 | Every diagnostic counts; fix every finding in your scope, or escalate it with file, line and reason. | **left in place** in 6 agent bodies | `skills/task-gate/SKILL.md` does not carry it today. See [what T52 left](#what-t52-left-and-why) | **No dedicated test.** The nearest pin is `SquadPerformanceContractTests` on the exit gate's report line, which does not cover this rule |
| F1 | Isolate build output before running anything in a shared checkout: `--artifacts-path`, or `-p:BaseOutputPath=` / `-p:BaseIntermediateOutputPath=` where the command takes no such flag. | removed from 3 of the 5 bodies: `dal-dev`, `maui-dev`, `pulumi-dev`. `csharp-dev` and `test-dev` keep the bullet, which `SpecialistSurfacesPreserveAdversarialRedGreenAndForbidCollapsedRedGreen` pins | `skills/task-gate/SKILL.md` → `## .NET isolation`; the standards' `## Completion` sections for the commands | the task gates already pass `-p:BaseOutputPath=.agent_scratchpad/{task}/artifacts/bin/` and `--artifacts-path`, so the isolation happens whether or not a worker reads the rule |
| F2 | The three cases: a shared checkout needs the flag; your own worktree does not; an explicit `--artifacts-path` overrides the worktree path, and the assembly then sits under `<dir>/bin/<test-project>/release/`. | `agents/csharp-dev.md`, `agents/test-dev.md` | the C# and test standards' `## Completion` sections | `SpecialistSurfacesClarifyWorktreeOutputIsolationBypassesArtifactsPath`, on both agents — **pinned, keep** |
| F3 | Build once, test many: reuse the build with `--no-build`, or run the compiled assembly directly as `dotnet <assembly>.dll -class "<Namespace>.<Class>"`, fully qualified because a short name runs zero tests and exits 0. | `agents/csharp-dev.md`, `agents/test-dev.md`, `skills/test-dev/SKILL.md` | the host test standard's `## Completion` section, which carries the concrete path | `WorkerSurfacesSpecifyBuildOnceTestManyWithDirectAssemblyRunner`, on all three: each must name the test standard and its `## Completion` section, so the pointer stays and only the local path moves. `TestCodingStandardDocumentsDirectAssemblyRunnerAndBuildOnceIteration` pins the standard |
| F4 | Zero-stash RED proofs: never `git stash`, and prove RED on a clean base checkout, a throwaway copy or an isolated worktree. | `agents/test-dev.md` → `## Workflow` step 5; `skills/delivery-execution/SKILL.md` → `## Test-first mode`; `agents/conductor.md` → `## Shared lifecycle invariants` | `skills/delivery-execution/SKILL.md` → `## Test-first mode` | `ConductorSurfacesSpecifyZeroStashRedProofs` (both files) and `TestDevSurfacesSpecifyZeroStashRedProofRule` — **pinned in the conductor too, keep** |

## The conductor's copy of the execution contract

The plan removes the 2.7 KB of `conductor.md` that is word for word in the execution contract.
Most of it is blocked: six theories assert the same phrases in **both** `agents/conductor.md` and
`skills/delivery-execution/SKILL.md`, so the conductor's copy cannot go until those theories drop
`ConductorAgentPath` — which is a test change, not a documentation one. T52 therefore removed
572 bytes of the conductor body, not the 2.7 KB the plan estimated: G6 and G7, the two rows no
theory pins there.

| ID | Rule — what a worker must do | Current homes (before the trim) | Single home after | Enforced by |
|---|---|---|---|---|
| G1 | Send a self-contained packet: inlined entry-file table, acceptance criteria, dependencies, mode, pre-computed test commands, and no plan or spec in the worker's context. | `agents/conductor.md` → `## Shared lifecycle invariants`; `skills/delivery-execution/SKILL.md` → `## Ready queue`, `## Task file and dispatch` | `delivery-execution` | `ConductorSurfacesSpecifyInlinedTaskPacketContents` — asserts both files. **Blocked** |
| G2 | Give each parallel worker a worktree or isolated context with per-task artifact paths, and serialize integration before push. | both files | `delivery-execution` → `## Ready queue` | `ConductorSurfacesSpecifyWorktreeAndArtifactsParallelization` — both files. **Blocked**, though `ConductorExecutionContractSpecifiesWorktreeCleanupAndConflictHandling` pins only the skill, so the MCP-setup and cleanup sentences are the removable part |
| G3 | Keep RED authorship and GREEN implementation adversarial, bundle same-role micro-tasks, and keep the independent verification pass at task completion. | both files | `delivery-execution` → `## Ready queue` | `ConductorSurfacesPreserveAdversarialRedGreenBoundaryAndBundleSameRoleTasks` — both files. **Blocked** |
| G4 | Report discovery before the digest; the two review seats report it above their attestation line instead, and the verdict engine flags a findings or `NO FINDINGS` seat's gaps (`KW-REVIEW-044`; a `SKIPPED` seat is exempt). | both files | `delivery-execution` → `## Developer exit gate` | `ConductorSurfacesSpecifyDiscoveryHeaderPrecedesDigest` — both files. **Blocked** |
| G5 | Exempt a valid `STATUS: ESCALATION` from the six-field rejection, and route a tripped breaker to architect mediation. | both files, plus `docs/kyber-squad/requirements.md` | `delivery-execution` → `## Iteration circuit-breaker and loop detection` | `ConductorAndRequirementsExemptCircuitBreakerEscalationFromSixFieldRejection` — three files. **Blocked** |
| G6 | Keep the original worker session for rework and verification; a busy session is queued, not replaced. | `agents/conductor.md` → `## Shared lifecycle invariants`: **shortened in T52** to a pointer at the skill's `## Task session continuity`; `delivery-execution` keeps the section | `delivery-execution` → `## Task session continuity` | `SquadCanonicalContentTests.ConductorRetainsOnlyWorkerSessionsForTaskRework` asserts the skill only |
| G7 | Bounded retries: the 2-rework-dispatch cap and the oscillation tripwire (row B6). | `agents/conductor.md` → `## Shared lifecycle invariants`: **removed in T52** | `delivery-execution` → `## Iteration circuit-breaker and loop detection` | `SquadCanonicalContentTests.LoadConductorDefinesIterationCircuitBreakerAndOscillationDetection` asserts the skill only |

Row G7 duplicates B6 from the other side: B6 records the removal of the duplication *inside* the
skill pair, G7 records the conductor-side copy that leaves.

## What stays duplicated, and why

Duplication is not automatically waste. These placements are deliberate, and T52 must leave them
alone.

| Kept | Why |
|---|---|
| The full-suite line in all 14 standard templates, and the ban sentence in `standards/go`, `standards/rust` and `standards/typescript` | A standard is host-owned project content: it ships into a host repository, is read on its own, and has no Squad render around it. Removing the line would leave that host with a rule it cannot see. |
| The ban in `skills/bug-crusher/SKILL.md` | `SquadPerformanceContractTests.BugCrusherSkillIsToldNeverToRunTheFullTestSuite` requires the phrase there. The skill is a primary session, which the baseline never reaches. |
| The investigator's ban, which now arrives by injection rather than in its body | `bug-crusher-investigator` is a dispatched subagent, so the baseline reaches it. `SquadPerformanceContractTests.BugCrusherInvestigatorIsToldNeverToRunTheFullTestSuiteByItsRender` asserts the ban in the investigator's **render** for every shipped harness, which is where the rule now reaches that agent; a body copy would fail `TheSharedBaselineIsTheOnePlaceTheFullSuiteBanIsWritten`. T52 removed the body copy — see [the pin conflict](#the-pin-conflict-t52-resolved). |
| The ban in `skills/kilo-review-loop/SKILL.md`, `skills/go-dev/references/gates-and-platforms.md`, `skills/rust-dev/references/gates-and-platforms.md`, `skills/typescript-dev/references/esm-and-gates.md` | `KiloReviewLoopGivesNoLocalFullRunRoute` and `LanguageGateTablesGiveATargetedTestCommand` require the ban in each. A gate table's reader must see the limit beside the command it limits. |
| "No worker runs the full suite, and the conductor never dispatches a full run" in `agents/conductor.md` and in `skills/delivery-execution/SKILL.md` | `ConductorSurfacesNeverDispatchAFullRun` requires both. This is the dispatcher's rule; the baseline reaches only `invocation: subagent` bodies. |
| The six-field block and the `TASK_GATE:` line in `skills/delivery-execution/SKILL.md` alongside its new home in `skills/task-gate/SKILL.md` | Two audiences: the conductor requires the digest, the worker emits it. Both are pinned (`ConductorSurfacesSpecifyExplicitSixFieldWorkerDigestSchema`, `SharedExecutionSkill_DeclaresDeterministicTaskGate_AndConductorOffloadsIt`, `TheTaskGateSkillSpecifiesTheExplicitSixFieldCompletionDigestSchema`). |
| The closed `CIRCUIT_BREAKER_TRIGGER` token set in `skills/task-gate/SKILL.md` alongside its procedure in `references/rework.md` | The skill raises the escalation the reference explains; `SquadCanonicalContentTests.TaskGateSkillDefinesTheDeterministicReviewGateContract` asserts the closed set on the skill file itself. |
| Each developer agent's own agent-specific completion, plus its `TASK_GATE:` line | The shared contract's phased rollout keeps those: only `csharp-dev` and `test-dev` must return the full six-field block, and the conductor rejects a missing or `FAIL` `TASK_GATE` line from every developer worker. `SquadCanonicalContentTests.NonDeveloperAgentBodiesNeverEmitTaskGateDigest` keeps it out of the non-developer bodies. |
| `review-lens` and `review-triage`'s `## Discovery order`, with its seat-specific `Discovery:` placement | A seat's closing lines are read by the verdict engine, so they report discovery directly above the attestation line rather than before a digest; `SquadCanonicalContentTests` asserts the `Discovery: codegraph N, docs N, direct reads N` line for them, and `KW-REVIEW-044` flags a findings or `NO FINDINGS` seat that omits it. |
| The zero-stash and worktree-isolation text in `agents/test-dev.md`, `agents/conductor.md` and the two .NET agent bodies | Pinned by `TestDevSurfacesSpecifyZeroStashRedProofRule`, `ConductorSurfacesSpecifyZeroStashRedProofs`, `SpecialistSurfacesClarifyWorktreeOutputIsolationBypassesArtifactsPath` and `SpecialistPromptsClarifyArtifactAndBaselinePathsBelongInAccompanyingMessageBody`. |
| The test-runner statement in the 9 standard templates that carry one | Same reason as the first row, and `TestStandardDefinesSharedTestInfrastructureAndGivesNoFullRunRoute` pins the test standard's version. |

## Rows still deferred, and why

**Rows B1 to B5 and A3 were deferred on a home that did not exist, and are now done.**
`products/kyber-squad/skills/task-gate/references/rework.md` was not in this tree when the page
was written. T52 wrote it from the text in the bodies — the iteration cap, the three JEV
checkpoints, the closed trigger vocabulary and the escalation payload — and then removed the
copies in the same change. `WorkerRuleSingleHomeTests` fails in both directions here: the
reference must carry `CIRCUIT_BREAKER_TRIGGER` **and** none of the three bodies may.

**Rows D1 and D2 are deferred on a test pin, not on a missing home.**
`SquadCanonicalContentTests.LoadCodeReadingAgentsDirectIndexedDiscoveryBeforeRawReads` requires
each of 19 agent bodies to carry a `## Discovery order` section that names `codegraph_explore`,
the two docs tools and "Read or search a file directly", in that order, and — for every agent but
the two review seats — the `Discovery: codegraph N, docs N, direct reads N` line. The injected
baseline does not satisfy it: the block has no heading, names no kw-docs tool, and carries no
counts header. T52 removed neither section. Whoever unblocks it must first move that pin to the
render (so the injected block is what is asserted) or narrow it to the fields that are genuinely
per-agent. This is the pin that blocks its own removal: the section it protects is the duplicate
of the injected block.

**Rows G1 to G5 are deferred on test pins.**
Six theories in `SquadPerformanceContractTests` assert the same phrases in `agents/conductor.md`
and in `skills/delivery-execution/SKILL.md`. Removing the conductor copy without changing those
theories turns them red. T52 left all five. It also left the MCP-setup and cleanup sentences
inside G2, which this page once called removable: `SquadWorktreeMcpContractTests` pins the
splice and fail-closed sentences in the conductor, so that pointer was wrong for them. The
cleanup sentence alone is still pinned only in the skill, and it stays because it sits inside the
one G2 bullet whose other sentences this page had already ruled out of scope.

## The pin conflict T52 resolved

`BugCrusherSurfacesAreToldNeverToRunTheFullTestSuite` required the ban sentence in
`agents/bug-crusher-investigator.md`, and `TheSharedBaselineIsTheOnePlaceTheFullSuiteBanIsWritten`
forbids any file under `agents/` from matching `never run the full test suite`. Both cannot hold:
the investigator is `invocation: subagent`, so the baseline injector reaches it on every render.

T52 repointed the pin rather than keeping a body copy or deleting the assertion. The one theory
became two cases with the same subject — "this agent is told never to run the full suite" — and
different evidence:

- `BugCrusherSkillIsToldNeverToRunTheFullTestSuite` reads `skills/bug-crusher/SKILL.md`, unchanged.
  That skill is a primary session, which no render wraps.
- `BugCrusherInvestigatorIsToldNeverToRunTheFullTestSuiteByItsRender` renders every shipped
  harness and asserts the regex in the investigator's rendered file, which carries the injected
  baseline. That is where the rule reaches this agent now.

The test file is `tests/KyberWeave.Tests/SquadPerformanceContractTests.cs`; the pin's subject and
its regex are unchanged, only its evidence moved. No assertion was weakened.

**One conflict outside the text, still open.**
The baseline's plan-read rule ("Do not open a file under `<plan-index>` or
`<specification-index>`") is injected into `review-lens`, which is a subagent. The
`intent-alignment` lens is documented to read the plan or specification that states the change's
intent. The gap is recorded rather than fixed here: either the injector exempts the review seats
the way the report-format pins already do, or the lens's stated access changes. This is a code or
skill decision, not a documentation one.

## What T52 left, and why

Four rows name `skills/task-gate/SKILL.md` as the single home, but the skill does not carry them:
E4's baseline procedure, E5's sweep scope, E6's "this is not evidence" rule and E7's
every-diagnostic rule. Removing the agent-body copy would have deleted the rule rather than moved
it, so all four stay where they are. Unblocking them means writing the procedure into the skill
first — the same order A1, A2 and the failure loop followed here, where the home was written
before the duplicate was deleted.

## Agent bodies this map names

Decision D60 derives the migration-report set from the map, because an agent body edited without
its report updated fails the canonical suite. Of the agent bodies whose text moved,
**13 reports needed a new digest** and three bodies have no report and may not be given one:

| Agent body edited by T52 | Migration report |
|---|---|
| `agents/bug-crusher-investigator.md`, `conductor.md`, `csharp-dev.md`, `dal-dev.md`, `docs-dev.md`, `github-devops.md`, `maui-dev.md`, `pulumi-dev.md`, `python-dev.md`, `react-dev.md`, `sql-database-architect.md`, `tauri-dev.md`, `test-dev.md` | `products/kyber-squad/migration/<agent>.md` — the recorded `final-body-sha256` updated in the same change |
| `agents/go-dev.md`, `agents/rust-dev.md`, `agents/typescript-dev.md` | none, and none may be created: `SquadCanonicalContentTests.LoadNetNewAgentsCarryNoFabricatedMigrationRecord` names them as net-new agents, and `LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest` pins the report-name set to the 18 migrated agents |

The migration directory holds **18** reports, not the 19 the plan's D60 records. This page's
first version named 11 edited bodies and missed `bug-crusher-investigator.md` and
`conductor.md`, whose rows the pin conflict and the conductor trim then moved; both have a report
and both were updated.

## What T52 did with this page

1. Read every row before editing a file, and edited only the rows this page did not defer.
2. Turned each "current homes" cell into "removed in T52 from" for the rows applied, and marked
   the rows left with the reason.
3. Recorded the byte counts against the plan's estimate, below.
4. Left every deferred row, every "what T52 left" row and every entry under
   [what stays duplicated](#what-stays-duplicated-and-why) as it is.
5. Recorded the one gate it could not read: the seeded-defect trial's per-row verdicts were not
   in this tree when the removals were applied. `.agent_scratchpad/T51/` is absent, and
   `.agent_scratchpad/` is gitignored, so no trial artifact is committed and these rows carry no
   trial result to check against. T52 applied the rows the plan authorizes by name, plus the
   unblocked rows whose named home already carries the rule, and left everything else. Recording
   the trial's verdicts per row is still owed, and it is why the cells above describe the tree
   rather than the trial.

## What T52 removed, in bytes

Measured against the parent commit, per file family.

| Family | Before | After | Change |
|---|---|---|---|
| `products/kyber-squad/agents/` (16 bodies) | 188,353 | 168,341 | **−20,012** |
| `skills/task-gate/SKILL.md` | 6,030 | 8,003 | +1,973 |
| `skills/task-gate/references/rework.md` (new) | 0 | 4,795 | +4,795 |
| `skills/csharp-dev/references/build-commands.md` | 866 | 847 | −19 |

The plan estimated 938 bytes across the agents, and 2.7 KB out of `conductor.md`. The agent-side
removal is 21 times the estimate because the estimate counted the ban alone; the failure-loop
blocks, the completion digest templates and the per-agent fix-pass bullets were each several
times larger. The conductor contribution is 572 bytes, not 2.7 KB, because G1 to G5 are pinned
there. The two largest single wins are `test-dev.md` (−4,434) and `csharp-dev.md` (−3,574), and
those two are the bodies re-sent most often in this programme's own runs.

What the word count does not show: the two new homes are read once per task rather than once per
worker, and a developer worker's render no longer carries a copy of a rule the baseline already
gave it.
