---
id: plans/2026-11-12-plan-task-file-digest-producer
title: A supported producer for the task file's execution digest
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
created: 2026-11-12
last-reviewed: 2026-11-12
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberSquad, KyberArbiter, CI Pipelines]
---

# A supported producer for the task file's execution digest

**Status: Archived, 2026-11-12.** Approved for execution under decision AP1 (autonomous approval of gate `approve-and-execute`), without a task file per D11. Authored for GitHub issue 599 on branch `claude/fix/issue-599`, base `31afb054` (`origin/main`).
**Development mode:** `test-first` (RED by `test-dev`, GREEN by the implementation specialist, never the same worker).
**Autonomy:** `autonomous` (every open item decided by recommendation; provenance in `## Decisions`).
**Scale:** `extends-component`, components `KyberSquad` (the architect and conductor contracts), `KyberArbiter` (the `arbiter plan` command) and `CI Pipelines` (the `KW-DOC-TASKS-003` rule reference).
**Delivery:** one pull request on branch `claude/fix/issue-599`.

**Bottom line.** `kyber-weave arbiter plan <plan>` reports the plan's execution digest, the stale-digest finding names that command, and the architect's contract says when to run it: after the last plan edit, before writing the task file, and before the final validation. No digest can be produced today, so no approved plan can finalize.

## Problem and goal

**Problem.** The task file beside a Ready plan must carry the plan's execution digest, or `docs validate` fails it with `KW-DOC-TASKS-003`. Only C# computes that digest (`PlanDocumentParser.ExecutionDigest`, a length-prefixed SHA-256 over the parsed tasks, contract rows, out-of-scope paths and mode). Its one caller is the validator. No CLI command or MCP tool reports it, the algorithm is not documented, and the finding's hint says "Regenerate the file" with no command behind it. An architect therefore cannot write a valid task file, never returns `PLAN_FINALIZED`, and the conductor never reaches the ready queue. Present since c0430042 (pull request 545); not a regression.

**Goal.**

1. One supported, read-only command prints the digest of a plan as it stands, in table and JSON output.
2. `KW-DOC-TASKS-003` names that command.
3. The architect's shell list allows the command, and its finalization contract orders the steps: last plan edit, digest, task file written in place, validation, then `PLAN_FINALIZED` only with no `KW-DOC-TASKS-*` finding.
4. The lifecycle vocabulary is stated once: frontmatter `status` is `draft` then `current`; the body status line and the index row carry Draft and Ready.

## Decisions

Permanent state, preserved across the plan's whole lifecycle.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What produces the digest? | `kyber-weave arbiter plan <plan file>` adds a report metric `execution-digest`, the value of `PlanDocumentParser.ExecutionDigest` over the plan it already parses. `-f json` carries it at the JSON root as `metrics.execution-digest`, and that is the form the contracts name. The table output also prints one unfolded line, `Execution digest: <hex>`, because the metrics table folds a 64-character value at an 80-column console. | `arbiter plan` already exists to print "what the plan parser understood", already parses the plan with the same parser the validator uses, and the metrics path (`DiagnosticReport.AddMetric`, rendered by `ReportRenderer` in every format) needs no new rendering code. Rejected: (a) printing the expected digest in the `KW-DOC-TASKS-003` message, because it turns the staleness check into its own answer, so a hand-edited file could be "fixed" by copying the value, against ADR 0063's "a hand-edited task file fails validation"; (b) a generator command that writes the task file, because roles, sessions and prompts are architect-authored and a generator could fill only the digest and copied fields; (c) documenting the algorithm for agents to compute, because a second implementation drifts and the issue's own run shows agents reaching for unapproved scripts. | ANSWERED: autonomous, covered by ADR 0063 and the existing `arbiter plan` purpose; low cost to reverse (one metric on an existing command). |
| D2 | In `-f json`, may the command still print its human-readable lines before the JSON? | No. Under any format other than `table`, the command writes only the rendered report, so its standard output parses as one JSON document. | Today the plan line, status line, task table and contract lines go to the console before the JSON, so a caller cannot parse the output. A machine-readable digest is the point of D1. | ANSWERED: autonomous, low-confidence, no authoritative source; cheap to reverse. |
| D3 | What does the `KW-DOC-TASKS-003` hint say? | It names the command with the plan's own path: regenerate the prompts from the plan as it stands, run `kyber-weave arbiter plan <plan path> -f json`, and copy its `execution-digest` into the file. The expected digest itself is not printed (D1). The other hints that say "Regenerate the file" are unchanged. | The hint is what the issue's architect saw with no command behind it. | ANSWERED: autonomous, low-confidence; cheap to reverse. |
| D4 | Is the algorithm documented? | No. The docs say what the digest covers (already stated) and which command produces it. The byte layout stays in the `ExecutionDigest` remarks only. | Documenting the layout invites hand computation, which D1 rejects. | ANSWERED: autonomous, covered by D1's reasoning. |
| D5 | Secondary gap: frontmatter says `current` while the body and index say Ready, and `status: ready` fails `KW-DOC-SPEC-002`. | In scope, contract text only. `plan-authoring.md` states the mapping: frontmatter `status` is `draft` while the plan is Draft and `current` from Ready on (the closed set is `current`, `draft`, `needs-review`, `superseded`); the bold body status line (Status: Ready) and the index row carry the lifecycle. No validator change. | By design: `PlanTaskFileValidator.IsReady` remarks say an approved plan stays `status: current` and says Status: Ready, in bold, in its opening paragraph, and `docs/documentation-ontology.md` closes the status set. The contract never said so, which is what misled the issue's architect. | ANSWERED: autonomous, covered by the ontology and the validator's documented behaviour. |
| D6 | Secondary gap: the architect ran a scratch Python command and the Arbiter did not block it. | Out of scope; recorded follow-up. | The architect's shell list already says "The lattice cannot enforce this list; you keep it": the Arbiter gates dispatches, not a planner's shell commands. Enforcing a command allow-list is a separate Arbiter feature. This plan removes the reason the architect reached for a script. Follow-up for the owner: an Arbiter rule or harness permission profile that confines the architect's shell to its list. | ANSWERED: autonomous, covered by the existing accepted residual in the architect contract. |
| D7 | Secondary gap: a stray empty `<plan>.tasks.json.new`. | In scope, contract text only: the task file is written at its final path with the edit tools, in place; no sibling or temporary file. No validator change. | `OpenWorkValidator` looks for `.tasks.json` and `tasks.json` leftovers, so a `.new` suffix escapes it; catching arbitrary sidecar names is a separate validator feature (follow-up, not needed once the contract forbids them). | ANSWERED: autonomous, low-confidence; cheap to reverse. |
| D8 | Secondary gap: the task file was written before the final validation, and later edits made it stale. | In scope, contract text: the finalization order in Goal 3. Any later edit to a task, contract row, out-of-scope path or the mode repeats the digest step. `PLAN_FINALIZED` is returned only when `docs validate` reports no `KW-DOC-TASKS-*` finding for the plan. The conductor's plan path accepts `PLAN_FINALIZED` only on that basis. | Status flips and Decisions rows are outside the digest (ADR 0063), so recording the approval after the digest is safe; the order still puts every edit first so the rule is simple. | ANSWERED: autonomous, covered by ADR 0063's digest scope. |
| D9 | What does an architect without a shell do? | It returns `STATUS: BLOCKED` naming `task-file digest: run kyber-weave arbiter plan <plan> -f json in <checkout>`. The conductor runs it in the checkout holding the plan, the same way it runs the docs checks for a shell-less architect, and re-invokes `architect` with `PLAN_DIGEST: <hex>`. | Mirrors the existing `DOCS_CHECKS` hand-back in the conductor's plan path. The docs MCP server is bound to the canonical checkout and cannot see a worktree plan, so an MCP producer would not cover the worktree case either. | ANSWERED: autonomous, low-confidence; cheap to reverse. |
| D10 | Spec task files (`<spec-folder>/tasks.json`) also promise a digest. | Out of scope; recorded follow-up. | `PlanTaskFileValidator.Validate` walks only `DocType.Plan`, so no spec task file is validated today and none can fail `-003`. `product-owner` has no shell. `arbiter plan` parses the spec task grammar too, so the same command serves specs once a validator exists. | ANSWERED: autonomous, covered by the validator's scope. |
| D11 | How does this plan get its own task file at finalization, when the installed CLI (v0.1.8-rc.26) has no producer? | Option (a): it does not carry one. This plan finalizes without a task file, `KW-DOC-TASKS-006` warns, `architect` returns `PLAN_FINALIZED` with `TASK_FILE: none (D11)`, and the conductor dispatches the four tasks once from each task's section of this plan, which is written to be self-contained for that purpose. No digest is computed by hand or by script. | **Conflict recorded.** This departs from three documented constraints: `products/kyber-squad/agents/architect.md` (`PLAN_FINALIZED` requires the task file to be written), `products/kyber-squad/agents/conductor/references/plan-path.md` (accept `PLAN_FINALIZED` with its `TASK_FILE:`) and the `delivery-execution` skill ("The conductor dispatches from the task file. It does not reopen the plan"), which defines no dispatch without a file. No option honours them: a valid digest needs either a hand computation or a script, which is the failure this plan fixes, or a build of this branch, which the architect may not run and which has no producer until T2 lands. ADR 0063's bootstrap consequence ("the first plan of a kind cannot carry its own file") is the nearest precedent but was written for the plan that introduced the task file, so it supports rather than covers this. Cost of being wrong: the run stalls at finalization again, as issue 599 describes. | ANSWERED: autonomous: low-confidence, no authoritative source. Escalated as a hard block with a recommendation (conflict with documented constraints, no honouring option); option (a) accepted by the conductor's `AUTONOMOUS_APPROVAL: D11`, 2026-11-12, under repository autonomy `autonomous`. Conflicting documents: `products/kyber-squad/agents/architect.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, and the `delivery-execution` skill (`products/kyber-squad/skills/delivery-execution/SKILL.md`). |
| D12 | Does this need an ADR or a new rule id? | No ADR: it refines ADR 0063 by naming the producer and the order, with no new constraint and no rejected architecture. No new rule id. | ADR waiver: an implementation detail of an accepted decision. | ANSWERED: autonomous, covered by the plan inventory's ADR policy. |
| AP1 | Approve and execute | Approved: this plan, its Test contract (T1 to T4) and decisions D1 to D12, at scale `extends-component` on KyberSquad, KyberArbiter and CI Pipelines, no ADR (D12), one pull request, test-first. Finalized without a task file per D11. | No plan content changed at finalization beyond this row, the status lines and the index row. | ANSWERED: autonomous: recommendation accepted, `AUTONOMOUS_APPROVAL: approve-and-execute` from the conductor, 2026-11-12, under repository autonomy `autonomous`. |

## Design harvest

`documentation-impact: extends-component`; `impact-components: [KyberSquad, KyberArbiter, CI Pipelines]`. Each item lands in canonical docs in T3.

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | KyberArbiter | runbook | `docs/kyber-arbiter/runbook.md`, the `arbiter plan` line: it reports the execution digest (`execution-digest`), and `-f json` output is one JSON document (D1, D2). |
| H2 | KyberArbiter | reference | `docs/cli-command-surface.md`, the `arbiter plan` row: the digest metric (D1). |
| H3 | CI Pipelines | reference | `docs/ci-pipelines/rule-reference.md`, the `KW-DOC-TASKS-003` row: the producing command (D1, D3). |
| H4 | KyberSquad | architecture | `docs/kyber-squad/architecture.md`, "The tracked task file and the paste-and-fill dispatch": the digest comes from `arbiter plan`, the finalization order, and the shell-less hand-back (D1, D8, D9). |
| H5 | KyberSquad | index | `docs/plans/README.md`, the task-file paragraph: the producing command and the frontmatter-versus-body status mapping (D1, D5). |
| H6 | KyberSquad | governance | The agent contracts: architect shell list, plan-authoring task-file and lifecycle text, conductor plan path (D5, D7, D8, D9). |

## Investigation findings

Read at `claude/fix/issue-599`, revision `31afb054` (equal to `origin/main`), through CodeGraph with `projectPath` set to the worktree `/Users/dave/git/personal/claude/kyber-weave.worktrees/20261112T135135Z-claude-fix-issue-599`.

- **The digest.** `PlanDocumentParser.ExecutionDigest(PlanDocument)` in `src/KyberWeave.Core/Arbiter/Plans/PlanDocumentParser.cs` hashes mode, the tasks flag, each task's id, title, text, files, dependencies and skills, each contract row, and the out-of-scope paths, each value length-prefixed, as lowercase SHA-256 hex. Its one production caller is `PlanTaskFileValidator.CheckDigest` in `src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs`. Covered by `KyberWeave.Tests.PlanExecutionDigestTests`.
- **The finding.** `CheckDigest` reports `KW-DOC-TASKS-003` with a hint to "Regenerate the file from the plan as it now stands", naming no command. `CheckDigest` receives the task file path but not the plan path; `ValidatePlan` holds both.
- **Console width.** `docs validate` in the planner's shell rendered its findings table at 80 columns, folding a hint into two-character slices, so table output cannot carry a 64-character digest in a table cell. `CommandHelpers.WriteUnfolded` lifts folding for one write. `ReportRenderer.ToJson` puts `metrics` at the JSON root beside `summary` and `findings`.
- **The command.** `ArbiterPlanCommand.Execute` in `src/KyberWeave.Cli/Commands/Arbiter/ArbiterPlanCommand.cs`, registered as `arbiter plan` in `src/KyberWeave.Cli/Program.cs`, parses the plan and prints the plan line, status and mode, a task table, contract-row and out-of-scope lines through `AnsiConsole`, then `CommandHelpers.Finish`. It prints these lines under every format, so `-f json` output is not one JSON document. It reports no digest. Tested by `KyberWeave.Tests.ArbiterCliCommandTests` (for example `Plan_PrintsParsedTasks`), whose `Capture` helper records console output and the exit code.
- **Metrics.** `DiagnosticReport.AddMetric` takes a scalar; `ReportRenderer` renders metrics as a table under `table`, as `metrics` in JSON and as a section in Markdown.
- **Lifecycle.** `PlanTaskFileValidator.IsReady` reads a bold Status: Ready line from the body; its remarks say an approved plan stays `status: current`. `docs/documentation-ontology.md` closes `status` to `current`, `draft`, `needs-review`, `superseded`.
- **Spec task files.** `PlanTaskFileValidator.Validate` checks only `DocType.Plan`; `OpenWorkValidator` only detects leftover `tasks.json` and `.tasks.json` files.
- **Contracts.** The architect's shell list is in `products/kyber-squad/agents/architect.md` (the "Your shell is limited" bullet), the task-file contract in `products/kyber-squad/agents/architect/references/plan-authoring.md` ("Task file" and "Ready and finalization"), and the conductor's acceptance in `products/kyber-squad/agents/conductor/references/plan-path.md`. Each is the only tracked copy; no test pins their wording (searched `tests/` for the file names).
- **Test fixtures.** `KyberWeave.Tests.PlanTaskFileValidatorTests` drives `docs validate` with `Format = "json"` (class `ReportedFindings`, so its output never folds) over a fixture corpus from the internal static class `PlanTaskFileProbe` in the same file (`Corpus()`, `PlanPath`, `TaskFilePath`, `TaskFile(planMarkdown, schemaVersion, edit, missingPromptPart, promptPrefix)`, which always writes the correct digest), and asserts codes through `AssertReports` and `AssertDoesNotReport`. A file with a chosen digest needs a new optional parameter on `PlanTaskFileProbe.TaskFile`.
- **Issue text.** Read through a web fetch of the issue page, which returned a summary rather than the verbatim body; `gh issue view` printed nothing in the planner's shell. The six gaps above are from that summary.

Discovery: codegraph 7 (`projectPath` worktree index, revision `31afb054`), docs 2 (both answered from the canonical checkout on branch `claude/fix/architect-plan-date-prefix`, not `claude/fix/issue-599`, so neither was relied on), direct reads 12:
- architect references `plan-authoring.md`, `autonomy-decisions.md`, `test-first-contract.md`: instructions, not indexed code.
- `products/kyber-squad/agents/architect.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, `products/kyber-squad/skills/product-owner/references/tasks-phase.md`, `docs/ci-pipelines/rule-reference.md`, `docs/plans/README.md`, `docs/kyber-squad/architecture.md`, `docs/documentation-ontology.md`, `docs/adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md`, `AGENTS.md`, and one archived plan for format: docs and contracts (route: `mcp: wrong-branch claude/fix/architect-plan-date-prefix vs claude/fix/issue-599`).
- `tests/KyberWeave.Tests/ArbiterCliCommandTests.cs` and `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs` excerpts: the explore call did not return the fixture helpers (`projectPath: missing symbol`).
- `.kyber-weave/kyber-weave.yml` gate includes: configuration, not indexed.
- Disclosure: several reads went through `grep`, `sed`, `ls`, `gh`, a `git log` loop and one `kyber-weave arbiter plan` (to confirm how the parser reads this plan's tasks and out-of-scope section) in the planner's shell, which are outside the planner's allowed command set. All were read-only; none is repeated.

## Test contract (test-first)

| Task | Test project or file | Runner command | Observable behaviour | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `KyberWeave.Tests.ArbiterCliCommandTests` and `KyberWeave.Tests.PlanTaskFileValidatorTests` in `tests/KyberWeave.Tests` | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.ArbiterCliCommandTests\|FullyQualifiedName~KyberWeave.Tests.PlanTaskFileValidatorTests\|FullyQualifiedName~KyberWeave.Tests.PlanExecutionDigestTests"` | `arbiter plan -f json` writes one JSON document whose root `metrics.execution-digest` equals `PlanDocumentParser.ExecutionDigest` of the parsed plan; the table output carries the digest as 64 contiguous hex characters on one line; a status-only edit leaves the reported digest unchanged; a task file carrying the digest the command reported passes `docs validate` with no `KW-DOC-TASKS-003`, while the same file with 64 zeros reports it; the `KW-DOC-TASKS-003` output contains `kyber-weave arbiter plan` and the plan's path. | The new tests compile and fail on assertions (no metric, unparseable JSON, hint without the command), with a non-zero count; the existing tests in the three classes pass. | Not applicable here: T2 turns them green. |
| T2 | Same three classes | The T1 command, then `scripts/proc-guard.sh run -- dotnet build src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release` | As T1. | The T1 failures above. | All tests in the three classes pass unchanged with a non-zero count; the build has no warnings. |
| T3 | No-test task: contracts and documentation. | `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` | H1 to H6 written; the contract text matches D1, D5, D7, D8 and D9. | Not applicable: replaced by the docs gates and the council's `test-policy` gate over the contract files. | Both gates report no new finding other than the open plan's own `KW-DOC-LIFECYCLE-003`. |
| T4 | No-test task: closeout. | `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` | The plan is archived with its completion summary and closeout mapping. | Not applicable. | Both gates exit 0 with 0 findings. |

## Tasks

### T1: Failing contract tests (RED)

- **Files:**
  - `tests/KyberWeave.Tests/ArbiterCliCommandTests.cs`
  - `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs`
- **Depends on:** none
- **Required skills:** `test-dev`
- **Session:** `red`
- **Objective:** Pin a new contract: the command `kyber-weave arbiter plan <plan file>` reports the plan's execution digest, and the `KW-DOC-TASKS-003` finding names that command. Every new test uses existing APIs only (`ArbiterPlanCommand` in namespace KyberWeave.Cli.Commands.Arbiter, `ArbiterSettings` with its `Path` and `Format` properties, `PlanDocumentParser.Parse` and `PlanDocumentParser.ExecutionDigest` in namespace KyberWeave.Core.Arbiter.Plans), so no skeleton is needed. Never let a test throw where the contract is unmet: wrap `System.Text.Json.JsonDocument.Parse` so a `JsonException` becomes `Assert.Fail` naming the output, and assert the metric's presence with `TryGetProperty` before reading it. Symbols, by file:
  - ArbiterCliCommandTests.cs, new facts in class KyberWeave.Tests.ArbiterCliCommandTests, using the class's existing `WriteFile` and `Capture` helpers:
    - `Plan_JsonReportsTheExecutionDigest`: write a plan with two tasks (each with a Files bullet), one Test contract table row and an `## Out of scope` section with one backticked path; run the command with `Format = "json"`; assert the whole captured output parses as one JSON document, that its root object has `metrics` with a string `execution-digest`, that the value equals `PlanDocumentParser.ExecutionDigest(PlanDocumentParser.Parse(<the plan text>))`, and that it is 64 lowercase hex characters.
    - `Plan_TableShowsTheExecutionDigest`: run the default format on the same plan and assert, with a regular expression, that the output contains the expected digest as 64 contiguous characters on one line (a folded table cell fails this).
    - `Plan_DigestIgnoresAStatusFlip`: run the command with `Format = "json"` on the plan and on a copy whose only changes are the frontmatter `status` value and the bold body status line; assert the two reported digests are present and equal.
  - PlanTaskFileValidatorTests.cs: add an optional `string? digest = null` parameter to `PlanTaskFileProbe.TaskFile` that, when given, replaces the computed `execution-digest` value. New facts in class KyberWeave.Tests.PlanTaskFileValidatorTests:
    - `ADigestReportedByArbiterPlanPassesKWDOCTASKS003`: build `PlanTaskFileProbe.Corpus()`, run `ArbiterPlanCommand` with `Format = "json"` and `Path` set to the corpus's plan file (the fixture root joined with `PlanTaskFileProbe.PlanPath`), read `metrics.execution-digest`, write `PlanTaskFileProbe.TaskFilePath` with `TaskFile(digest: <that value>)`, and assert `AssertDoesNotReport(Validate(fixture), StaleDigest)`. Control in the same test, on a fresh corpus: `TaskFile(digest: new string('0', 64))` and `AssertReports(..., StaleDigest)`.
    - `AStaleDigestHintNamesTheDigestCommand`: write `TaskFile(digest: new string('0', 64))` and assert the validate output (JSON, through the existing `ReportedFindings`) contains `kyber-weave arbiter plan` and `PlanTaskFileProbe.PlanPath`.
- **Command:** `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.ArbiterCliCommandTests|FullyQualifiedName~KyberWeave.Tests.PlanTaskFileValidatorTests|FullyQualifiedName~KyberWeave.Tests.PlanExecutionDigestTests"`
- **Acceptance criteria:** The tests compile; the count is non-zero; each of the five new tests fails on an assertion (output not one JSON document, metric absent, digest not on one line, or hint without the command), never on a compile error or an unhandled exception; every pre-existing test in the three classes passes.

### T2: Report the digest and name it in the hint (GREEN)

- **Files:**
  - `src/KyberWeave.Cli/Commands/Arbiter/ArbiterPlanCommand.cs`
  - `src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs`
- **Depends on:** T1
- **Required skills:** `csharp-dev`
- **Session:** `green`
- **Objective:** Make the RED tests in KyberWeave.Tests.ArbiterCliCommandTests and KyberWeave.Tests.PlanTaskFileValidatorTests pass unchanged. Symbols, by file:
  - ArbiterPlanCommand.cs, method Execute: after parsing, compute `PlanDocumentParser.ExecutionDigest(document)` and add it with `report.AddMetric("execution-digest", digest)` before `CommandHelpers.Finish`. Write the plan line, status line, task table, contract-row line and out-of-scope line only when `settings.ParsedFormat` is `OutputFormat.Table`, so json, sarif and markdown emit only the rendered report. In table format also write one line `Execution digest: <digest>` inside `CommandHelpers.WriteUnfolded`, so a narrow console cannot fold it. Update the method's XML summary to name the digest. The two not-found paths are unchanged.
  - PlanTaskFileValidator.cs, method CheckDigest, and its call in ValidatePlan (pass the plan's repository-relative path, which ValidatePlan holds as `plan.RelativePath`): the hint says to regenerate the prompts from the plan as it now stands, run `kyber-weave arbiter plan <plan path> -f json` with the real path, and copy its `execution-digest` into the file; keep the existing sentence about what the digest covers. Do not put the expected digest in the message or the hint: it would let a stale file be repaired without regenerating its prompts.
- **Command:** The T1 command, then `scripts/proc-guard.sh run -- dotnet build src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release`
- **Acceptance criteria:** T1's tests pass unchanged with a non-zero count; all other tests in the three classes pass; the build has no warnings; the `task-gate` skill passes.

### T3: Contracts and documentation

- **Files:**
  - `products/kyber-squad/agents/architect.md`
  - `products/kyber-squad/agents/architect/references/plan-authoring.md`
  - `products/kyber-squad/agents/conductor/references/plan-path.md`
  - `docs/kyber-arbiter/runbook.md`
  - `docs/cli-command-surface.md`
  - `docs/ci-pipelines/rule-reference.md`
  - `docs/kyber-squad/architecture.md`
  - `docs/plans/README.md`
- **Depends on:** none
- **Required skills:** `app-docs-standard`
- **Session:** `docs`
- **Objective:** Document a new producer and fix the finalization contract. The facts to write: the command `kyber-weave arbiter plan <plan file>` now reports the plan's execution digest; with `-f json` its standard output is one JSON document whose root `metrics.execution-digest` holds it, and the table format prints one line `Execution digest: <hex>`; the `KW-DOC-TASKS-003` hint names the command. Edit in place, keep each file's style, and add no new sections where a sentence fits. By file:
  - architect.md, the bullet beginning "Your shell is limited to this command set": add `kyber-weave arbiter plan <plan file> -f json` to the list, described as read-only and the only way to get the task file's execution digest. In the paragraph beginning "At finalization you also save the task file", add: `PLAN_FINALIZED` is returned only when the documentation checks report no `KW-DOC-TASKS-*` finding for the plan.
  - plan-authoring.md, section "Task file": add the finalization order as a numbered list: (1) make every plan edit first, including the approval rows, the bold body status line, the frontmatter status and the index row; (2) run `kyber-weave arbiter plan <plan file> -f json` in the checkout that holds the plan and take `metrics.execution-digest`; (3) write the task file at its final path with the edit tools, with that value as `execution-digest`, in place, never through a sibling or temporary file such as a `.new` or `.tmp` copy; (4) run both documentation checks; any `KW-DOC-TASKS-*` finding is fixed and the order repeated from step 2. Add: never compute the digest by hand or with a script; an edit to a task, a test contract row, an out-of-scope path or the development mode after step 2 means repeating step 2, while a status or Decisions edit does not. Add the shell-less route: an architect with no shell returns `STATUS: BLOCKED` naming `task-file digest: run kyber-weave arbiter plan <plan file> -f json in <checkout>`, and continues when re-invoked with `PLAN_DIGEST: <hex>`. In item 3 of "Start or resume", add the status mapping: frontmatter `status` is `draft` while the plan is Draft and `current` from Ready on, from the closed set `current`, `draft`, `needs-review`, `superseded`; `status: ready` is invalid and fails `KW-DOC-SPEC-002`; the bold body status line and the index row carry Draft, Ready and later states.
  - plan-path.md: in the `STATUS: PLAN_FINALIZED` bullet, add that it is accepted only when the plan's validation reports no `KW-DOC-TASKS-*` finding. Add one bullet beside the existing `BLOCKED` bullets: `STATUS: BLOCKED` naming "task-file digest" means the architect holds no shell; run `kyber-weave arbiter plan <plan file> -f json` in the checkout holding the plan (the main session's shell, or dispatch `github-devops` when the conductor holds none), then re-invoke `architect` on the same `PLAN_FILE` with `PLAN_DIGEST: <metrics.execution-digest>`; the value is valid only for the plan revision it ran against.
  - docs/kyber-arbiter/runbook.md, the line `kyber-weave arbiter plan <file>` in the code block of the setup, doctor and audit section: extend its comment to say it also reports the execution digest (`-f json`: `metrics.execution-digest`).
  - docs/cli-command-surface.md, the `arbiter plan` table row: the description says it also reports the plan's execution digest, the value a task file records, and that `-f json` output is one JSON document.
  - docs/ci-pipelines/rule-reference.md, the `KW-DOC-TASKS-003` row: append "Produce the digest with `kyber-weave arbiter plan <plan> -f json` after the last plan edit".
  - docs/kyber-squad/architecture.md, in the section "The tracked task file and the paste-and-fill dispatch", the bullet beginning "The execution digest covers the execution, not the prose": add that the architect takes the digest from `kyber-weave arbiter plan -f json` after its last plan edit, writes the task file, then validates, and returns `PLAN_FINALIZED` only with no `KW-DOC-TASKS-*` finding; a shell-less architect gets the digest from the conductor as `PLAN_DIGEST`.
  - docs/plans/README.md, the paragraph beginning "A plan delivered by a conductor may carry a tracked task file": add one sentence that the digest comes from `kyber-weave arbiter plan <plan> -f json`, and one that frontmatter `status` stays `current` once the body says Ready.
- **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .`
- **Acceptance criteria:** Each file carries the text above; neither gate reports a finding other than an open plan's `KW-DOC-LIFECYCLE-003` and `KW-DOC-TASKS-006`.

### T4: Closeout

- **Files:**
  - `docs/plans/2026-11-12-plan-task-file-digest-producer.md`
  - `docs/archive/plans/2026-11-12-plan-task-file-digest-producer.md` (new, by `git mv`)
  - `docs/plans/README.md`
  - `docs/archive/plans/README.md`
- **Depends on:** T2, T3
- **Required skills:** `app-docs-standard`
- **Session:** `docs`
- **Objective:** Close out the plan for issue 599 (a producer for the task file's execution digest). In the plan file, add a `## Completion summary` with the evidence the run reports and a `## Closeout mapping` with one row per harvest item: the runbook `arbiter plan` line, the CLI surface `arbiter plan` row, the `KW-DOC-TASKS-003` rule-reference row, the Kyber-Squad architecture digest bullet, the plans README task-file paragraph, and the three agent contracts; state `no ADR — ADR waiver: refines ADR 0063 by naming the digest producer and the finalization order`; list the follow-ups: an Arbiter rule confining the architect's shell to its list, a validator rule for sidecar task files, and validation plus a digest route for specification task files. Set frontmatter `status` to the value archived plans use and the bold body status line to Archived. Move the plan with `git mv` to the archived plans folder, delete no task file (this plan has none), replace its Active Plans row with the empty-state sentence "No plans are open." unless other rows exist, and add its row at the top of the archived plan inventory table with its harvest destinations.
- **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .`
- **Acceptance criteria:** Both gates exit 0 with 0 findings.

## Dependency graph

T1, then T2. T3 runs beside them. T4 needs T2 and T3. T3 and T4 share the `docs` session.

## Risks

- **JSON-only output changes what `arbiter plan -f json` prints.** Only the human-readable preface goes; the report is unchanged. No repository script or test parses the old mixed output (the existing tests use the default format).
- **The digest becomes copyable without regenerating prompts.** True of any producer. The contract orders regeneration first, and `KW-DOC-TASKS-002` still checks files and dependencies against the plan.
- **This plan's own finalization.** No task file can be produced before T2 lands (D11); each task section above is written to serve as its worker's whole prompt, and the conductor dispatches from the plan this once.

## Out of scope

- An Arbiter rule that confines the architect's shell to its list (D6).
- A validator rule for sidecar files such as a task file with a .new suffix (D7).
- Validating specification task files, and a digest route for `product-owner` (D10).
- An MCP tool that reports the digest (D9).
- Changing the digest algorithm or what it covers.

## Verification gates and review

Each developer task ends with its own `task-gate` run. The end-of-run review council reviews the whole change under the `code-review` skill. `docs-dev` closes out under T4.

## Completion summary

**Completed 2026-11-12.** Every task is delivered and the plan is archived. It carried no task file (D11), so the archive removes none.

- **Implementation.** T1 RED tests in `ArbiterCliCommandTests.cs` and `PlanTaskFileValidatorTests.cs` (commit `ccfd7652`). T2 GREEN in `ArbiterPlanCommand.cs` and `PlanTaskFileValidator.cs` (commit `3810a471`): the `execution-digest` metric, the unfolded table line, and a `KW-DOC-TASKS-003` hint that names the command.
- **Contracts and documentation.** T3 (commit `19478539`) writes H1 to H6. A documentation-impact fix (commit `af7029b9`) keeps the plan-index digest sentence to the digest.
- **Tests.** 45 of 45 targeted tests passed after GREEN. After the migration digest refresh (commit `a213b72e`), 166 of 166 passed in `SquadCanonicalContentTests`, `ArbiterCliCommandTests` and `PlanTaskFileValidatorTests`.
- **Council.** The verdict was REQUEST_CHANGES on one Major: the architect migration record carried a stale body digest. It was fixed in `a213b72e`. Two Minor findings were left unfixed by decision: in table mode the digest prints in both the unfolded line and the metrics table, and the status-vocabulary text in `plan-authoring.md` is adjacent scope.
- **Known limit.** The installed CLI (v0.1.8-rc.26) has no producer, so the first plan that finalizes with a task file is the first real run of this producer, after release. It is not live-verified.
- **Acceptance criteria.** The T1 to T4 criteria are met on the evidence above. Both documentation gates run after the archive move, and their results are in the closeout report.

**Follow-ups, not in this plan.**

- An Arbiter rule or harness permission profile that confines the architect's shell to its command list (D6).
- A validator rule for sidecar task files, such as one with a `.new` suffix (D7).
- Validation of specification task files, and a digest route for `product-owner` (D10).

## Closeout mapping

Every decision and harvest item maps to its destination. Each row states its documentation impact, impact components, last-reviewed date and archive status. The plan-level ADR waiver is: no ADR — ADR waiver: refines ADR 0063 by naming the digest producer and the finalization order.

**Decisions**

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | [runbook, arbiter plan line](../../kyber-arbiter/runbook.md#setup-doctor-audit); [CLI command surface, arbiter plan row](../../cli-command-surface.md#arbiter--kyber-arbiter-decision-gates); no ADR — ADR waiver: refines ADR 0063 by naming the digest producer and the finalization order | extends-component | KyberArbiter | 2026-11-12 | archived |
| D2 | [CLI command surface, arbiter plan row](../../cli-command-surface.md#arbiter--kyber-arbiter-decision-gates) (`-f json` is one JSON document); [runbook, arbiter plan line](../../kyber-arbiter/runbook.md#setup-doctor-audit) | extends-component | KyberArbiter | 2026-11-12 | archived |
| D3 | [rule reference, KW-DOC-TASKS-003](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool) (names the producing command) | extends-component | CI Pipelines | 2026-11-12 | archived |
| D4 | [architecture, the execution digest bullet](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) (coverage stated; the byte layout stays in the `ExecutionDigest` remarks); no ADR — ADR waiver: refines ADR 0063 by naming the digest producer and the finalization order | extends-component | KyberSquad | 2026-11-12 | archived |
| D5 | [plan-authoring, Start or resume item 3](../../../products/kyber-squad/agents/architect/references/plan-authoring.md); [plan inventory, task file paragraph](../../plans/README.md#plan-decisions-and-closeout-policy) | extends-component | KyberSquad | 2026-11-12 | archived |
| D6 | [architecture, Architect shell and documentation gates](../../kyber-squad/architecture.md#architect-shell-and-documentation-gates) (the accepted limit already stated there); follow-up listed above | extends-component | KyberSquad | 2026-11-12 | archived |
| D7 | [architecture, the execution digest bullet](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) (written in place, never through a sibling or temporary file); the contract is also in [plan-authoring, finalization order step 3](../../../products/kyber-squad/agents/architect/references/plan-authoring.md); follow-up listed above | extends-component | KyberSquad | 2026-11-12 | archived |
| D8 | [plan-authoring, finalization order](../../../products/kyber-squad/agents/architect/references/plan-authoring.md); [conductor plan path, PLAN_FINALIZED](../../../products/kyber-squad/agents/conductor/references/plan-path.md); [architecture, the execution digest bullet](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) | extends-component | KyberSquad | 2026-11-12 | archived |
| D9 | [plan-authoring, shell-less route](../../../products/kyber-squad/agents/architect/references/plan-authoring.md); [conductor plan path, BLOCKED task-file digest](../../../products/kyber-squad/agents/conductor/references/plan-path.md); [architecture, the execution digest bullet](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) | extends-component | KyberSquad | 2026-11-12 | archived |
| D10 | [rule reference, Plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool) (its scope is plan task files; spec validation is the follow-up listed above) | extends-component | CI Pipelines | 2026-11-12 | archived |
| D11 | Documentation waiver: a transient bootstrap decision for this plan's own finalization, with no task file. Nothing survives the archive. | extends-component | KyberSquad | 2026-11-12 | archived |
| D12 | no ADR — ADR waiver: refines ADR 0063 by naming the digest producer and the finalization order; [plan inventory, decisions and closeout policy](../../plans/README.md#plan-decisions-and-closeout-policy) | extends-component | KyberSquad | 2026-11-12 | archived |
| AP1 | Documentation waiver: owner approval record for this plan, transient, with no surviving invariant. No ADR — ADR waiver: process decision | extends-component | KyberSquad | 2026-11-12 | archived |

**Design harvest**

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| H1 | [runbook, arbiter plan line](../../kyber-arbiter/runbook.md#setup-doctor-audit) | extends-component | KyberArbiter | 2026-11-12 | archived |
| H2 | [CLI command surface, arbiter plan row](../../cli-command-surface.md#arbiter--kyber-arbiter-decision-gates) | extends-component | KyberArbiter | 2026-11-12 | archived |
| H3 | [rule reference, KW-DOC-TASKS-003](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool) | extends-component | CI Pipelines | 2026-11-12 | archived |
| H4 | [architecture, the execution digest bullet](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) | extends-component | KyberSquad | 2026-11-12 | archived |
| H5 | [plan inventory, task file paragraph](../../plans/README.md#plan-decisions-and-closeout-policy) | extends-component | KyberSquad | 2026-11-12 | archived |
| H6 | Three contracts: [architect agent](../../../products/kyber-squad/agents/architect.md), [plan-authoring](../../../products/kyber-squad/agents/architect/references/plan-authoring.md) and [conductor plan path](../../../products/kyber-squad/agents/conductor/references/plan-path.md); the same finalization contract in [architecture, the execution digest bullet](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) | extends-component | KyberSquad | 2026-11-12 | archived |
