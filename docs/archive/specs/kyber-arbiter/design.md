---
id: archive/specs/kyber-arbiter/design
title: Kyber Arbiter design
doc-type: spec
status: archived
owner: dpalfery
last-reviewed: 2026-10-10
component: KyberSquad
---

# Kyber Arbiter design

**Phase status:** Approved

Owner approved 2026-10-03 ('approve design'), with D30–D33, relayed by the main session. Delivery wording in §13 was corrected 2026-10-04 for D32 and D34, at the owner's direction.

The approved [requirements](requirements.md) are the authority: decisions D1–D34 and Requirements 1–25. This document cites them as `Dn` and `Req n.m` and does not restate them. Under D33 it covers only what is needed now, and sub-agents keep their own implementation decisions.

- **Harness facts** come from vendor documentation. Each is cited by a key that §21 resolves to a URL and a read date. Under D7, an undocumented capability is treated as supported until a defect says otherwise.
- **Code facts** were read at `rev=13dcb73` (§18).
- **Section numbers** are used for cross-references (`§n`).

## 1. Architecture

### 1.1 Components

| Component | Location and key types | Responsibility | Req |
|---|---|---|---|
| Rule engine | `src/KyberWeave.Core/Arbiter/Rules/`: `ArbiterRule`, `RulePredicate`, `RuleEngine`, `RuleValidator`, embedded `default-rules.yml` | Step-0 predicates, first-match `decide` clauses, effects, combination (§1.4) | 1, 14 |
| Evaluator | `src/KyberWeave.Core/Arbiter/ArbiterEvaluator.cs`, `ArbiterEvaluationResult`, `ArbiterEscalationEnvelope` | The only entry point for `hook`, `serve` and `eval`. Builds facts, runs step 0, makes at most one step-1 call, combines the answers and writes the decision | 1, 3 |
| Facts | `src/KyberWeave.Core/Arbiter/Facts/`: `ArbiterEvent`, `HeaderBlock`, `CallerResolver`, `TriggerFactBuilder`, `ArbiterTriggerCatalog`, `GitFacts`, `GateReportFacts` | Header parsing, caller resolution, trigger classification, fact readers, `derived` and `asserted` labels (§1.3, §4, §5) | 9, 21 |
| Plan parser | `src/KyberWeave.Core/Arbiter/Plans/`: `PlanDocument`, `PlanDocumentParser` | §6 | 9 |
| Provider | `src/KyberWeave.Core/Arbiter/Providers/`: `IArbiterProvider`, `NoneProvider`, `SystemOneClient`, `SystemOneContracts`, `StateBudget` | Step 1 (§1.5) | 17, 19 |
| Credentials | `src/KyberWeave.Core/Arbiter/Credentials/`: `ArbiterKeyResolver`, `ICredentialStore` with three stores | Key resolution bound to the endpoint origin (§1.5) | 18 |
| Ledger and decision log | `src/KyberWeave.Core/Arbiter/Facts/`: `InFlightLedger`, `DecisionLog` | Locked appends and lock-free reads (§1.6) | 5.3, 21.3 |
| Squad catalog | `src/KyberWeave.Core/Arbiter/Squad/ArbiterSquadCatalog.cs`, embedding `products/kyber-squad/agents/*.md` and `skills/code-review/references/lenses/*.md` | Each agent's `delegates-to`, description and profile, and each lens's Applicability section. Inside a host the canonical tree is absent; the embedded copy is version-locked with Squad by KS-005 | 11, 14 |
| Configuration | `src/KyberWeave.Core/Configuration/ArbiterYamlSection.cs`, `Arbiter/ArbiterConfig.cs`, `ArbiterConfigLoader.cs`, `ArbiterUserSettings.cs` | The `arbiter:` section, the user override and validation (§7) | 14, 22 |
| Hook host | `src/KyberWeave.Arbiter/` (assembly `kyber-weave-arbiter`): `Hooks/HookCommand`, `IHarnessHookAdapter`, one adapter per harness, `PluginEnvelope` | `kyber-weave-arbiter hook` (§1.7) | 2, 3, 5 |
| MCP fallback | `src/KyberWeave.Arbiter/Mcp/`: `ArbiterTools`, `ArbiterProvenance`, `ArbiterRootResolver` | `kyber-weave-arbiter serve` (§1.9) | 4 |
| CLI | `src/KyberWeave.Cli/Commands/Arbiter/`, composed in `ArbiterCommandComposition` | Eight commands (§1.8) | 2.2, 22.3 |
| Gate applicability | `Core/Configuration/ReviewYamlSection.cs`, `Core/Review/GateRunner.cs`, `ReviewModel.cs`, `VerdictEngine.cs`, CLI `review gates` | §11 | 12 |
| Squad wiring | `Core/Squad/Rendering/ArbiterHookWiring.cs`, the renderers, `Core/Squad/Deployment/` | Hooks, owned blocks and degradations (§10) | 6, 8, 22 |
| Distribution | Release workflows, scripts, Homebrew, npm and the self-updater | §1.11 | 22.1 |

**Dependencies.**

- **Core adds no package.** It keeps only Markdig and YamlDotNet, plus the BCL `HttpClient` and P/Invoke.
- **`src/KyberWeave.Arbiter` uses the packages `KyberWeave.Mcp` already pins:** `ModelContextProtocol` 2.2.0 and `Microsoft.Extensions.Hosting` 10.0.12. It takes no Spectre.Console, because its stdout is a protocol (Req 2).
- **Core constructs none of its collaborators.** The composition roots, the CLI and the Arbiter binary, inject the `HttpMessageHandler`, the credential store, the user-home path, the process runner and the clock. The precedent is `OpenAiCompatibleEmbeddingGenerator`.

### 1.2 Evaluation flow

Steps 0 and 1 run inside the `hook` process, `serve` or `eval`. Step 2 never runs there (Req 1.3).

1. **Classify the event (§4).** Two kinds of event are allowed at once, before any configuration is loaded (R14):
   - an event that is not a dispatch;
   - on a project-wide-hook harness, an unmarked dispatch whose caller is unidentified. The ledger records it with `caller: unidentified` (Req 6.4).
2. **Append the ledger event (§1.6).** This comes first, so a hook killed during evaluation still leaves an event with no decision, which `audit` reports (`KW-ARB-AUDIT-001`).
3. **Build the trigger's facts (§1.3).**
4. **Step 0.** Evaluate every enabled step-0 rule bound to the trigger. The first matching clause answers, and no match is `undecidable` (Req 1.1, 1.5).
5. **Step 1: one batched `systemone` call (Req 1.2).** It carries every enabled step-1 rule of the trigger whose `ask.when` holds.
   - The call is not made when step 0 has already produced the trigger's strongest effect (§1.4, §3).
   - Provider `none` marks the step-1 rules *not evaluated* (Req 19.2). That is not `undecidable`.
   - An over-budget state, a provider error, a timeout, or an answer below threshold is `undecidable` (Req 19.4).
6. **Combine.** Any escalating rule escalates the trigger, and signals are not averaged (Req 1.4). `undecidable` maps to a fixed effect per trigger family (§3).
7. **Record and answer.**
   - Append the decision.
   - On a non-allow outcome, render the envelope or review note (§1.10).
   - Write the harness's decision shape (§9). Where §12 says so, the dispatch input is rewritten to strip the routing headers.
8. **Step 2 happens after the block, outside the Arbiter.** On conductor triggers, the conductor dispatches `architect` with the envelope (D15). On review triggers, `code-reviewer` runs the lens or the refutation pass (D11).

### 1.3 Facts

| Fact family | Label | Source |
|---|---|---|
| `plan.*` | derived | The plan parser (§6), run over `PLAN_FILE` at evaluation time. Nothing is cached or written (Req 9.2) |
| `git.*`, `review.changed-*` | derived | `git` run through `ProcessRunner`, which takes argv only and refuses a shell |
| `gates.report` | derived | `ReviewJson.ReadGates` over `artifacts/gates.json` |
| `ledger.*` | derived | The ledger (§1.6), which only the Arbiter writes |
| `roster.*`, `lens.applicability`, `delegation.target-class`, `delegation.target-profile` | derived | The embedded Squad catalog |
| `file.*` | derived | A read of the file at the finding's path |
| `config.planning-dirs` | derived | The directories of the paths declared as `<plan-index>`, `<specification-index>` and `<todo-index>` in `.kyber-weave/kyber-weave.yml` |
| `rules.<id>.answer` | derived | A step-0 answer, readable by step-1 `ask.when` clauses |
| `caller` | derived from the harness payload or the rendered `--caller`; asserted when inferred from headers | §4.2 |
| `delegation.*` (prompt, paths, plan-file, task, markers), `lens.name`, `finding.*` | asserted | The dispatch input or the lens output |

The facts each trigger supplies are listed below. `ArbiterTriggerCatalog` declares this table, and the validator checks every rule against it (`KW-ARB-CONFIG-002`).

| Trigger | Facts |
|---|---|
| `delegate` | `caller`, `delegation.target`, `delegation.target-class`, `delegation.target-profile`, `delegation.prompt`, `delegation.paths`, `delegation.plan-file`, `delegation.task`, `plan.exists`, `plan.status`, `plan.development-mode`, `plan.tasks.count`, `plan.task`, `plan.task.text`, `plan.task.files`, `plan.task.depends-on`, `plan.task.skills`, `plan.out-of-scope`, `plan.test-contract.row`, `ledger.in-flight.paths`, `ledger.completed-tasks`, `ledger.red-evidence`, `roster.caller.delegates-to`, `roster.descriptions`, `rules.<id>.answer` |
| `delegate.returned` | The `delegate` facts, plus `git.changed-paths.since-dispatch` and `ledger.concurrent.paths` |
| `delegate.planner` | `caller`, `delegation.target`, `delegation.prompt`, `delegation.markers`, `delegation.plan-file`, `config.planning-dirs` |
| `delegate.planner.returned` | The `delegate.planner` facts, plus `git.changed-paths.since-dispatch` and `ledger.concurrent.paths` |
| `investigate` | `caller`, `delegation.target`, `delegation.target-class`, `roster.caller.delegates-to` |
| `investigate.returned` | The `investigate` facts, plus `git.changed-paths.since-dispatch` and `ledger.concurrent.paths` |
| `lens.spawn` | `lens.name`, `lens.applicability`, `review.changed-paths`, `review.diff-summary` |
| `lens.returned` | `finding.id`, `finding.file`, `finding.line`, `finding.excerpt`, `file.text-at-line`, `review.changed-hunks` |
| `refute.spawn` | `finding.id`, `finding.claim`, `finding.excerpt`, `finding.file`, `finding.line`, `file.surroundings`, `gates.report` |
| `gate.select` | `gate.id`, `gate.applies-when.paths`, `review.changed-paths` |

Fact definitions that are not self-evident:

- **`delegation.prompt`** is the dispatch prompt with the header block removed (§5).
- **`delegation.paths`** are the backticked path-like spans in that prompt, extracted by the path rule in §6.
- **`git.changed-paths.since-dispatch`.** At pre-dispatch, the ledger stores a snapshot: `HEAD`, and the blob id of every path that `git status --porcelain=v1 -z --untracked-files=all` reports. At post-dispatch, the changed set is:
  - every path whose status or blob id now differs;
  - plus `git diff --name-only <pre-HEAD> HEAD` when `HEAD` has moved;
  - minus `artifacts/**`.
- **`ledger.in-flight.paths`** are the task files of every unpaired `delegate` pre-dispatch event in the current session, other than this one.
- **`ledger.concurrent.paths`** are the task files of every other `delegate` dispatch that was in flight at any moment between this dispatch and its return. It includes dispatches that returned during that window, so their changes are not attributed to this one (R6).
- **`plan.task.files`** are the files the task lists (§6). The list may be empty. When it is, the file-scope checks are skipped and the skip is logged (D30); an empty list is not `undecidable`.
- **`ledger.completed-tasks`** are the tasks whose returned `task-reviewer` dispatch carries `RESULT: PASS`, plus tasks attested complete (D31). Both are keyed by `PLAN_FILE` and `TASK`.
- **`ledger.red-evidence`** is a returned `test-dev` dispatch carrying a `RED_EVIDENCE:` value other than `none`, or a task attested `red` (D31). It counts for the task itself or for any task it depends on.
- **`review.changed-paths`** are computed against the `base` recorded in `artifacts/gates.json` (§11). The fact is absent when no report or no base exists.
- **`review.diff-summary`** is the changed paths plus the hunk headers, not the full diff, so that it fits the state budget.
- **`file.surroundings`** are ±20 lines around `finding.line`.

### 1.4 Rule model

Shipped rules live in the embedded `default-rules.yml`. Host rules use the same shape (§7).

```yaml
- id: KW-ARB-SCOPE-001
  trigger: delegate
  question: Does TASK name a plan task, and do the paths the delegation names stay inside its files?
  answers: [in-task-files, beyond-files, no-task-files, not-in-plan]
  decide:                            # step 0: first match wins; no match is undecidable
    - when: { fact: plan.task, exists: false }
      answer: not-in-plan
    - when: { fact: plan.task.files, count: 0 }
      answer: no-task-files          # D30: file check skipped, and the skip is logged
    - when: { fact: delegation.paths, subset-of: plan.task.files }
      answer: in-task-files
    - when: { fact: delegation.paths, exists: true }
      answer: beyond-files
  effects: { in-task-files: allow, beyond-files: allow, no-task-files: allow, not-in-plan: escalate }

- id: KW-ARB-SCOPE-002
  trigger: delegate
  question: Does the delegation ask for work the plan task does not describe?
  answers: [within-task, adds-work, unrelated]
  ask:                               # step 1: one question in the trigger's batched call
    type: choice
    state: { task: plan.task.text, delegation: delegation.prompt }
    criteria:
      within-task: Every piece of work the delegation asks for is described by the task.
      adds-work: The delegation asks for work the task does not describe.
      unrelated: The delegation does not concern this task.
    confidence-at-least: 0.8         # below it, the answer is undecidable
    tuned-for: [jev-1.13.0]
  effects: { within-task: allow, adds-work: escalate, unrelated: escalate }
```

**Rule fields.**

- **`id`, `trigger`, `question`, `answers`, `effects`.**
- **`enabled`**, which defaults to `true`.
- **`decide`** (step 0) and/or **`ask`** (step 1). A rule that has both asks only when `decide` is `undecidable`. `KW-ARB-LENS-001` is the only shipped rule of this kind.

**Step-0 predicates.** The set is closed (Req 1.1): `all`, `any`, `not`, `exists`, `equals`, `in`, `matches`, `subset-of`, `intersects`, `count`.

- Each `when` names one `fact` and one operator. The operator's operand is either a literal or another fact's name.
- On path lists, `matches`, `subset-of` and `intersects` use `PathGlob`.
- There is no expression language and no new dependency.

**Step-1 `ask` fields.**

- **`type`** is `choice`, `score` or `noul`, the provider's own question types.
- **`state`** maps named fields to facts, and to facts only. A rule therefore declares exactly what leaves the machine (R9).
- **`criteria`** (for `choice`), **`levels`** (for `score`) or **`question`** (for `noul`).
- **`instructions-from`** is `lens:<name>`, resolving to the embedded lens text, or a repository-relative path.
- **`confidence-at-least`** for `choice` and `score`, or **`probability-below`** for `noul`.
- **`when`** gates the question on step-0 answers (`rules.<id>.answer`).
- **`tuned-for`** lists the models the threshold was set for (§1.5).

**Effects.** The effects a rule may use, the fixed mapping of `undecidable`, and how answers combine all depend on the trigger family (§3).

**Short-circuit (D1, Req 1.2).** Step 1 is not called once step 0 has produced the family's strongest effect: `escalate`, `skip` or `verify`. One confident red flag is enough. It also follows that step 1 can add a red flag but can never overturn a plain-code result, which is the R11 mitigation. When a decision rests on several judgements, the least certain one governs: a step-1 answer counts only at or above its threshold [TS-cascade].

### 1.5 Provider and credentials

**`systemone` client.**

- **The call.** `POST <endpoint>/systemone` with `state`, `model` and `questions`. `questions` is a map keyed by rule id [TS-api].
- **Authorization.** `Authorization: Bearer <key>` is sent only when a key resolves for the endpoint's origin. A loopback endpoint therefore sends none.
- **Errors.** 401 and 422 are `undecidable`. 429 and 529 are retried with backoff inside `timeout-ms`.
- **Recording.** The client records the answering `model` and the `usage` the response returns.
- **Implementation.** It is hand-written on the BCL `HttpClient`, because TypeSafe ships no .NET SDK.

| | TypeSafe JEV | Ollama `nimble` | Ollama `tev1` |
|---|---|---|---|
| Endpoint | `https://api.typesafe.ai/v1` | `http://localhost:11434/v1` (Ollama 0.35 or later) | same |
| Key | Required | None. Ollama's guide sets `TYPESAFE_API_KEY=ollama` only so that TypeSafe's SDKs run | None |
| Question types | `choice` (option, probabilities, confidence), `score` (2–10 levels, probabilities, confidence), `noul` (probability of yes, no separate confidence) | same request and response shape | same |
| Usable input | 32k tokens for the state plus the longest question | about 8k tokens per question | about 2,000 tokens |
| Request body | ≤ 64 KiB, applied as a common conservative ceiling (documented for Ollama) | ≤ 64 KiB | ≤ 64 KiB |
| Accuracy on Ollama's 13-dataset, 3,880-decision comparison | 76.0% (JEV 1.13.0) | 74.8% (9B, Apache 2.0, from Qwen3.5-9B) | 73.3% (4B), 63.5% (0.8B) |
| Latency and price | about 70–500 ms; $0.042 per million input tokens; output free | local | local |
| Data | No training on customer data; zero data retention is enterprise-only | local | local |
| Documented caveats | Weak at counting, dates and negation; "can still emit a completely wrong valid value" | "a probability of 0.9 doesn't mean the answer is right 90% of the time on your data" | Not fully tested for prompt injection or calibration |
| Sources | TS-api, TS-legal | OL-blog, OL-nimble, OL-0.35 | OL-tev1 |

**State budget.**

- **Measurement.** Before calling, the client estimates tokens as ⌈UTF-8 bytes ÷ 3⌉ over the shared state plus the longest question. It also checks the request body against 64 KiB. Over-estimating only produces `undecidable`, which is the safe direction.
- **Budgets.** `jev-*` 32,000, `nimble` 8,000, `tev1` 2,000. An unknown model gets the smallest known budget, and `doctor` warns (`KW-ARB-CONFIG-007`).
- **Over budget, no call is made.** Every step-1 rule on the trigger answers `undecidable`. Truncating would let the model judge evidence it never saw.

**Thresholds belong to a rule and model pair.**

- The shipped thresholds are tuned for `jev-1.13.0` and start conservative [TS-conf]:
  - `KW-ARB-SCOPE-002` 0.8;
  - `KW-ARB-OWNER-002` 0.8;
  - `KW-ARB-LENS-001` skips only below P(applies) 0.1;
  - `KW-ARB-CLAIM-001` verifies only at confidence 0.9 or above.
- `doctor` raises `KW-ARB-CONFIG-007` when the configured model is not in an enabled step-1 rule's `tuned-for`.
- The decision log records the model that answered (Req 17.4), and that record is the input for later tuning (§17).

**Key resolution (Req 18).** It runs per process, and the hook and `serve` resolve the key the same way.

1. **`TYPESAFE_API_KEY`.** It is used only for the TypeSafe origin, or for an origin named in the user override.
2. **The credential-store entry for the endpoint's origin.**
3. **Otherwise no key.** A remote provider then raises `KW-ARB-KEY-001` in `doctor` and `status`.

Origin binding exists so that a repository whose configuration points `provider.endpoint` elsewhere cannot obtain the user's key (R21). The key is never:

- read from, or written to, any configuration file, MCP file or hook file;
- placed in argv;
- included in a log, an exception or `ToString`.

| OS | Store | Write (the key travels on stdin) | Read |
|---|---|---|---|
| macOS | Keychain: service `kyber-weave-arbiter`, account = endpoint origin | `security -i`, with the command `add-generic-password -U -s kyber-weave-arbiter -a <origin> -w <key>` fed on stdin | `security find-generic-password -s kyber-weave-arbiter -a <origin> -w` (stdout) |
| Linux | Secret Service | `secret-tool store --label='Kyber Arbiter' service kyber-weave-arbiter account <origin>`, with the secret on stdin | `secret-tool lookup service kyber-weave-arbiter account <origin>` |
| Windows | Credential Manager: target `kyber-weave-arbiter:<origin>` | `CredWriteW` by P/Invoke to `advapi32`, with no package | `CredReadW` and `CredFree` |

- The macOS and Linux stores run through the injected process runner, so tests assert the exact argv and stdin.
- The Windows store compiles on every OS and is gated by `OperatingSystem.IsWindows()`.
- A Linux host without Secret Service fails `setup` with a hint to use `TYPESAFE_API_KEY`.

### 1.6 Decision log and ledger

Both files sit in `artifacts/arbiter/` under the repository root, which is resolved by `git rev-parse --show-toplevel` from the payload's `cwd`.

**Writers.** Only `kyber-weave-arbiter hook` and `kyber-weave-arbiter serve` append to either file (D3).

- The conductor cannot write them: its `orchestrator` profile denies `filesystem.write`.
- Neither `task-reviewer` nor any other agent writes them.
- The hook writes what it observes:
  - pre- and post-dispatch events, paired by tool-call id where the harness provides one;
  - `task-reviewer` PASS or FAIL, read from post-dispatch output;
  - `test-dev` RED evidence, read from post-dispatch output;
  - `architect` attestations (D31), read from post-dispatch output.
- The CLI writes neither file. `eval` is a dry run.

**Decision record** (`decisions.jsonl`, schema `kyber-arbiter.decision/v1`):

| Field | Content |
|---|---|
| `schema`, `id`, `at` | Schema id, a sortable unique id, and a UTC timestamp |
| `source`, `harness`, `session` | `hook` or `serve`; the harness token; the payload's session id when it has one |
| `ledger-id` | The ledger event this decides |
| `trigger`, `caller`, `caller-source` | `caller-source` is `harness`, `rendered`, `header`, `asserted` (for `serve`) or `none` |
| `target`, `plan-file`, `plan-digest`, `task` | From the headers and the parse |
| `rule-set` | SHA-256 of the effective rule set |
| `rules[]` | Per rule: `id`, `step`, `answer`, `probabilities`, `confidence`, `threshold`, `effect`, `facts` (name and label), `evidence` (one line) |
| `provider` | `kind`, `endpoint-origin`, `model` as answered, `usage`, `latency-ms`, and `status`: `answered`, `not-evaluated`, `over-budget`, `error` or `short-circuited` |
| `outcome`, `repeat`, `duration-ms` | The trigger's outcome, the `REPEAT` count (§1.10), and the evaluation time |

Fact values are not logged, except paths, ids and the one-line evidence. Prompts and code appear only as digests.

**Ledger event** (`ledger.jsonl`, schema `kyber-arbiter.ledger/v1`):

| Field | Content |
|---|---|
| `schema`, `id`, `at`, `source`, `harness`, `session` | As in the decision record |
| `phase` | `pre`, `post` or `unmarked` |
| `call-id` | The harness's tool-call id, where the payload carries one (§9.2) |
| `pair-digest` | SHA-256 of the dispatch tool's input as finally executed, after any header strip |
| `pre-id` | On a post event, the pre event it was matched to |
| `caller`, `caller-source`, `target`, `trigger`, `marker` | As classified (§4) |
| `headers` | `plan-file`, `task`, `lens`, `refute` |
| `task-files` | On a `delegate` pre event, the resolved task files, used for the in-flight facts |
| `snapshot` | On a pre event, `HEAD` and the blob ids of dirty and untracked paths |
| `changed-paths` | On a post event, the since-dispatch set |
| `returns` | On a post event, markers read from the dispatch output (see below) |
| `body-names-planning-path` | On a pre event to an implementation target, whether the packet body names a path under `config.planning-dirs` (`KW-ARB-AUDIT-004`) |
| `decision-id` | The decision record for this event |

The `returns` markers are:

- **`task-review`:** `PASS` or `FAIL`, read from `^RESULT:\s+(PASS|FAIL)\b`;
- **`red-evidence`:** the `RED_EVIDENCE:` value;
- **`planner-status`:** the `STATUS:` value;
- **`attested`:** the attestations (D31).

**Pairing.** A post event is matched to its pre event by `call-id` where the harness provides one. Otherwise it is matched by `pair-digest`, oldest unpaired pre event first.

**Concurrency.**

- **Appends.** Each append writes one complete line under an exclusive lock on `artifacts/arbiter/.lock`, opened with `FileShare.None` and retried with backoff for up to 500 ms inside the hook budget. A lock that cannot be taken is an internal error (fail closed, `KW-ARB-HOOK-001`).
- **Reads.** Readers take no lock, and ignore a final line that has no newline.
- **Retention.** Both files are append-only, with no rotation (§17). `doctor` warns when `artifacts/arbiter/` is not ignored by git (`KW-ARB-LOG-001`).

**`kyber-weave arbiter audit [--plan <file>] [--session <id>] [--since <ISO-8601>]`:**

| Id | Check |
|---|---|
| `KW-ARB-AUDIT-001` | A ledger event with no decision: the hook was killed, timed out or crashed after recording the event (Req 5.3, 21.3) |
| `KW-ARB-AUDIT-002` | An unmarked dispatch, with an unidentified caller, whose target is a Squad agent (D24) |
| `KW-ARB-AUDIT-003` | An unpaired event: a `pre` with no `post` although its session has later events, so post-dispatch rules did not run; or a `post` with no `pre`, so the pre-dispatch gate did not run |
| `KW-ARB-AUDIT-004` | A dispatch to an implementation specialist whose packet body names a planning path. This is the advisory half of Req 25 (§12) |

Attestations are listed for information and are not findings.

- **Limitation.** A dispatch that no hook observed leaves no record, so `audit` cannot report it. That covers hooks not yet trusted, hooks not installed, and a harness that failed open before the hook started. Trust state is surfaced at install time instead (§10.7).
- **Use at review.** `code-reviewer` runs `audit --plan <PLAN_FILE>` at the end of a run and cites the result (D21).
- **Diagnosis only.** Inferring a plan or task from the plan index is used for diagnosis only, never to decide (Req 21.2). `audit` may print an inferred candidate; no rule reads one.

### 1.7 Hook host

**Command.** `kyber-weave-arbiter hook --harness <token> [--caller <agent>]`.

- **Tokens:** `claude`, `copilot-vscode`, `copilot-cli`, `opencode`, `pi`, `codex`, `cursor`, `kilo`, `antigravity`, `factory`, `devin`.
- **One command, two kinds of hook.** The host chooses between them by tool and by caller class:
  - dispatch gating, on the harness's dispatch tool;
  - the planning-path Read guard on implementation specialists (§12).

**Input.**

- **Command hooks** receive the harness event on stdin.
- **Plugin harnesses** (OpenCode, Kilo, Pi) get a rendered TypeScript shim instead.
  - The shim spawns the binary by argv, with no shell, and writes a `kyber-arbiter.plugin-event/v1` envelope: `{schema, harness, phase, tool, call-id, session, cwd, args, result}`.
  - It reads back `{decision: allow|block, reason, args}`.
  - It blocks by throwing (OpenCode, Kilo) or by returning `{block: true, reason}` (Pi), and applies a strip by assigning `args`.
  - A shim that cannot spawn the binary blocks.

**Output and exit.** The host writes the harness's decision document on stdout, and nothing else. Logging goes to stderr, as in `KyberWeave.Mcp`'s `Program.cs`. The exit code is 0.

**Pass-through.** Some events pass through untouched:

- an event with no sub-agent target, for example an `Agent` or `Task` input with no `subagent_type`;
- on the guarded agents, a read that names no protected path.

For these the host writes no decision and no input change. It emits an empty stdout where the harness documents that as "proceed", and the harness's plain allow shape otherwise.

**Fail closed (Req 5).**

- One top-level catch turns any exception into that harness's block, carrying `KW-ARB-HOOK-001`.
- On conductor triggers, the block's reason is an envelope with `ANSWER: error` (Req 5.2).
- §8.4 lists what still escapes.

**Latency budget (Req 5.3).**

- Where the harness's hook schema has a timeout field, the per-hook timeout is `provider.timeout-ms` plus 2 seconds.
- Cursor entries set `failClosed: true`.

**Configuration.** It is loaded only for classified events.

- At runtime, `arbiter.enabled: false` allows and logs. Hooks rendered before the change stay harmless until `squad update` removes them.
- A missing or malformed `.kyber-weave/kyber-weave.yml` on a classified event is an internal error.

**Version.** `--version` prints `kyber-weave-arbiter <semver>`. `squad doctor` and `arbiter doctor` probe it (`KW-ARB-BIN-001`).

### 1.8 CLI

| Command | Does | Writes |
|---|---|---|
| `kyber-weave arbiter validate [path]` | Validates `arbiter:`, the user override and the shipped rules (§7) | Nothing |
| `kyber-weave arbiter rules [--trigger <t>]` | Lists each rule's id, trigger, step, question, answers, effects and facts | Nothing |
| `kyber-weave arbiter plan <file>` | Prints what the parser understood (§6), plus `KW-ARB-PLAN-001` and `KW-ARB-PARSE-00x` | Nothing |
| `kyber-weave arbiter eval --trigger <t> --event <file> [--provider none]` | Evaluates a recorded event offline, and prints the outcome or envelope | Nothing. It reads the ledger and never appends |
| `kyber-weave arbiter audit` | §1.6 | Nothing |
| `kyber-weave arbiter setup` | Chooses the provider (`none`, TypeSafe cloud or local Ollama) and writes it to the user override. Reads a TypeSafe key from `--key-stdin` or a masked prompt and stores it for the endpoint origin, never echoing it. Detects Ollama 0.35 or later through `GET <host>/api/version`; it then suggests `nimble` and warns on `tev1` (Req 23) | The user override and the credential store |
| `kyber-weave arbiter status` | Shows provider, model, endpoint origin, and whether the key was found. Never shows the value | Nothing |
| `kyber-weave arbiter doctor` | Raises `KW-ARB-CONFIG-006`, `-007`, `-009`, `-010`, `KW-ARB-KEY-001`, `KW-ARB-LOG-001`, `KW-ARB-BIN-001` and `KW-ARB-GUARD-001` | Nothing |

Output and exit codes follow `src/KyberWeave.Cli/AGENTS.md` and `CommandHelpers.Finish`. Hints come from the edit-distance helper that `DocSpecValidator.Nearest` uses.

### 1.9 MCP fallback

`kyber-weave-arbiter serve` is a stdio MCP server for the D4 fallback. It ships in Phase 3 (§13) and has three tools:

- **`arbiter_evaluate(trigger, facts)`.** Not read-only, because it appends to the ledger and the decision log. It returns the same allow, envelope or review note that the hook would return for the same event.
- **`arbiter_rules(trigger?)`.** Read-only and closed-world.
- **`arbiter_status()`.** Read-only and closed-world. It reports the root, the rule-set hash, the rule count and the provider state, never the key.

Every response leads with the provenance line, as the docs tools' responses do. The root is resolved in this order: `--repo-root`, then `KYBER_WEAVE_REPO_ROOT`, then the working directory.

**Callers.**

- **The conductor** calls it before each dispatch and after each return.
- **`code-reviewer`** calls it once before the lens fan-out (Req 4.2), and once before the refutation fan-out.

**Facts.**

- The server accepts these asserted facts: `target`, `plan-file`, `task`, `prompt`, `lens` (a list, for the batched fan-out), `finding` (YAML), `phase`, `call-id`, and `output` (on return). It derives everything else itself.
- The agent being gated asserts the routing facts, so this path is advisory (R7). The log records `caller-source: asserted`.
- On fallback targets, the routing facts are passed here, and no routing header is written into a worker's prompt (§5, §12).

### 1.10 Escalation envelope and review notes

A non-allow outcome on a conductor or investigate trigger becomes the block's reason. It uses the marker style of the Squad's existing status handoffs (Req 15.5):

```text
STATUS: ARBITER_ESCALATION
TRIGGER: delegate
CALLER: conductor (harness)
TARGET: csharp-dev
PLAN_FILE: docs/plans/<plan>.md
TASK: T3
RULES: KW-ARB-SCOPE-002
QUESTION: Does the delegation ask for work the plan task does not describe?
ANSWER: adds-work (p=0.86, step 1, jev-1.13.0)
EVIDENCE: delegation asks to "also refactor GateRunner"; T3 describes only the plan parser
DECISION_ID: <decision id>
REPEAT: 1
NEXT: dispatch architect with this envelope; do not retry this delegation unchanged.
```

- **Field order is fixed.** When several rules fire, `RULES` lists them in id order, and `QUESTION`, `ANSWER` and `EVIDENCE` repeat once per rule in the same order.
- **`ANSWER`** takes one of four forms: `<answer> (step 0)`, `<answer> (p=<p>, step 1, <model>)`, `undecidable (<reason>)`, or `error (KW-ARB-HOOK-001: <message>)`.
- **`REPEAT`** is 1 plus the number of earlier escalation decisions with the same plan file, plan digest and task that share a rule id. A Draft amendment changes the digest and so resets the count (R8). The hook computes it from the decision log; the conductor keeps no count.
- **`NEXT`** depends on the situation:

| Situation | `NEXT` |
|---|---|
| Conductor trigger, `REPEAT: 1` | `dispatch architect with this envelope; do not retry this delegation unchanged.` |
| Conductor trigger, `REPEAT` ≥ 2 | `stop: record this as a run finding; do not dispatch architect again for this task.` |
| `KW-ARB-PLANNER-001` answered `malformed` | `re-issue this planner dispatch with a recognised marker; do not send it to architect.` |
| Investigate trigger, caller `architect`, `product-owner` or `code-reviewer` | `report this in your own result; do not retry this dispatch unchanged.` |
| Investigate trigger, caller unidentified | `the conductor dispatches architect with this envelope; any other caller reports it in its own result.` |

Review outcomes use notes in the same style. They are not escalations.

```text
STATUS: ARBITER_SKIP            # lens.spawn denied: the lens does not apply
LENS: infra-workflow
RULES: KW-ARB-LENS-001
ANSWER: not-applicable (step 0)
DECISION_ID: <decision id>
```

- **`STATUS: ARBITER_VERIFIED`** is the deny reason on a `refute.spawn`. It carries `REFUTE: <finding id>` and an `ANSWER` such as `supports (c=0.93, step 1, jev-1.13.0)` or `corroborated (step 0)`.
- **`STATUS: ARBITER_ANNOTATION`** is returned on `lens.returned` with `FINDING: <finding id>`. It is delivered as post-dispatch additional context where the harness documents that field, and otherwise as the post-dispatch block reason.

`code-reviewer` handles each note this way:

- **SKIP:** the lens is recorded as `SKIPPED`, with the reason (D11).
- **VERIFIED:** the finding is kept, and its refutation is recorded as skipped.
- **ANNOTATION:** the note feeds `code-reviewer`'s existing quote check, which drops fabricated quotes as it does today.

The Arbiter never drops a finding (Req 11.6).

### 1.11 Distribution

The third binary ships like `kyber-weave-mcp` (Req 22.1). It goes in Phase 1, because Phase 1 hooks call it.

| Surface | Change |
|---|---|
| `.github/workflows/release.yml`, `ci.yml`, `scripts/release-local.sh` | Five `kyber-weave-arbiter-<rid>` archives |
| `scripts/verify-release-checksums.sh` | The asset list grows from 20 to 25. ADR 0027's decision is unchanged; its count is superseded in `docs/distribution.md` |
| `scripts/install.sh` | `--no-arbiter`, following the `NO_MCP` pattern, and an `ARBITER_MIN_VERSION` floor |
| `scripts/update-loop.sh` | Asserts `kyber-weave-arbiter --version` |
| `homebrew/kyber-weave.rb` | An `arbiter` resource per platform |
| `npm/` | `package.json`, `bin/kyber-weave-arbiter.js`, `lib/platform.js`, `lib/download.js`, `scripts/postinstall.js`, `README.md` |
| CLI self-update | `SelfUpdater` gains `ArbiterBaseName` and `ArbiterMinVersion`, following the `KyberDashMinVersion` pattern. `SelfUpdateOptions.NoArbiter`, `UpdateSettings` and `UpdateCommand` change with it. `update` replaces an installed arbiter and reports an absent one rather than creating it |
| Probes | `ProcessProbes` and `SquadDoctorCommand` probe `kyber-weave-arbiter --version` |

## 2. Rule catalogue

All 18 rules ship in Phase 1. Later phases add harnesses, not rules. Ids are permanent (Req 14.4).

| Rule | Trigger | Step | Question | Answers and their effects | Default threshold | Facts read | Phase |
|---|---|---|---|---|---|---|---|
| `KW-ARB-PLAN-001` | `delegate` | 0 | Does `PLAN_FILE` name an existing plan or spec task artifact with parseable tasks? | `has-tasks` → allow; `no-tasks` → escalate; `missing` → escalate; `invalid-path` (absent, outside the repository, or outside `config.planning-dirs`) → escalate | — | `delegation.plan-file`, `plan.exists`, `plan.tasks.count` | 1 |
| `KW-ARB-SCOPE-001` | `delegate` | 0 | Does `TASK` name a plan task, and do the paths the delegation names stay inside its files? | `in-task-files` → allow; `beyond-files` → allow and logged (`SCOPE-002` judges intent, `DIFF-001` judges writes, R22); `no-task-files` → allow, with the file check skipped and the skip logged (D30); `not-in-plan` → escalate | — | `delegation.task`, `delegation.paths`, `plan.task`, `plan.task.files` | 1 |
| `KW-ARB-SCOPE-002` | `delegate` | 1, `choice` | Does the delegation ask for work the plan task does not describe? | `within-task` → allow; `adds-work` → escalate; `unrelated` → escalate | confidence ≥ 0.8 | `plan.task.text`, `delegation.prompt` | 1 |
| `KW-ARB-READY-001` | `delegate` | 0 | Are the task's dependencies complete, and are its files clear of work in flight? | `ready` → allow; `waiting` → escalate; `overlaps-in-flight` → escalate; `unknown-dependency` → escalate | — | `plan.task.depends-on`, `plan.task.files`, `ledger.completed-tasks`, `ledger.in-flight.paths` | 1 |
| `KW-ARB-OWNER-001` | `delegate` | 0 | Is the target the specialist the task names, or the owner its file kinds map to? | `owner` → allow; `not-owner` → escalate; `no-mapping` → allow, and `OWNER-002` is asked | — | `delegation.target`, `plan.task.skills`, `plan.task.files`, and the file-kind map, which is data in `default-rules.yml` (`.tsx` → `react-dev`, `src-tauri/**` → `tauri-dev`, …) | 1 |
| `KW-ARB-OWNER-002` | `delegate` | 1, `choice`, asked only when `OWNER-001` answered `no-mapping` | Which specialist in the caller's implementation roster owns this task? Each option's text is that agent's description | `target` (the choice equals `delegation.target`) → allow; `other` → escalate | confidence ≥ 0.8 | `plan.task.text`, `roster.descriptions`, `delegation.target`, `rules.KW-ARB-OWNER-001.answer` | 1 |
| `KW-ARB-MODE-001` | `delegate` | 0 | In test-first mode, does RED evidence exist before an implementation dispatch? | `not-required` → allow. That covers mode `standard`, target `test-dev`, a target in the `documentation` profile, and a task whose contract row says "Not applicable" for RED. `present` → allow; `missing` → escalate. A task with no parseable contract row still needs RED evidence; the missing row alone does not escalate (D33) | — | `plan.development-mode`, `delegation.target`, `delegation.target-profile`, `plan.test-contract.row`, `ledger.red-evidence` | 1 |
| `KW-ARB-ROSTER-001` | `delegate`, `investigate` | 0 | Is the target in the caller's `delegates-to`? It runs only for a caller identified by the harness payload or the rendered `--caller` | `listed` → allow; `not-listed` → escalate | — | `caller`, `delegation.target`, `roster.caller.delegates-to` | 1 |
| `KW-ARB-DIFF-001` | `delegate.returned` | 0 | Did the delegation change only paths inside its task's files? | `out-of-scope` (intersects `plan.out-of-scope`) → escalate; `no-task-files` → allow, with the task-file comparison skipped and the skip logged (D30); `inside-task` → allow; `outside-task` → escalate | — | `git.changed-paths.since-dispatch`, `ledger.concurrent.paths`, `plan.task.files`, `plan.out-of-scope` | 1 |
| `KW-ARB-PLANNER-001` | `delegate.planner` | 0 | Is this a recognised planner invocation (§4.4)? | `recognised` → allow; `malformed` → escalate, routed back to the conductor (§8) | — | `delegation.target`, `delegation.markers`, `delegation.plan-file`, `config.planning-dirs` | 1 |
| `KW-ARB-PLANNER-DIFF-001` | `delegate.planner.returned` | 0 | Did the planner write only inside the plan, spec and todo directories? | `inside` → allow; `outside` → escalate | — | `git.changed-paths.since-dispatch`, `ledger.concurrent.paths`, `config.planning-dirs` | 1 |
| `KW-ARB-READONLY-001` | `investigate.returned` | 0 | Did the read-only dispatch leave the tree unchanged? | `unchanged` → allow; `changed` → escalate | — | `git.changed-paths.since-dispatch`, `ledger.concurrent.paths` | 1 |
| `KW-ARB-LENS-001` | `lens.spawn` | 0, then 1 (`noul`, using `instructions-from: lens:<name>`) | Does the lens apply to this change? | Step 0, from the per-lens path table in `default-rules.yml`: `applies` → allow, `not-applicable` → skip, no match → step 1. Step 1: P(applies) < 0.1 → skip; otherwise allow | probability-below 0.1 | `lens.name`, `review.changed-paths`; at step 1, `lens.applicability` and `review.diff-summary` | 1 |
| `KW-ARB-QUOTE-001` | `lens.returned` | 0 | Is each finding's excerpt present at its `file:line`? | `present` (the whitespace-normalised excerpt starts within ±3 lines of `line`) → allow; `elsewhere` → annotate; `absent` → annotate (fabricated [TS-cite]) | — | `finding.file`, `finding.line`, `finding.excerpt`, `file.text-at-line` | 1 |
| `KW-ARB-PREEX-001` | `lens.returned` | 0 | Is the excerpt inside a changed hunk? | `in-hunk` → allow; `outside-hunk` → annotate (pre-existing code) | — | `finding.file`, `finding.line`, `review.changed-hunks` | 1 |
| `KW-ARB-CLAIM-001` | `refute.spawn` | 1, `choice` | Do the quoted code and its surroundings support the finding's claim? | `supports` → verify; `contradicts` → allow; `says_nothing` → allow | confidence ≥ 0.9 | `finding.claim`, `finding.excerpt`, `file.surroundings` | 1 |
| `KW-ARB-GATE-CORROBORATED-001` | `refute.spawn` | 0 | Does a failed gate already corroborate the finding? It does when the gate failed and its captured output names `finding.file` with `finding.line` | `corroborated` → verify; `not-corroborated` → allow | — | `gates.report`, `finding.file`, `finding.line` | 1 |
| `KW-ARB-GATE-001` | `gate.select` | 0 | Does any changed path match the gate's `applies-when.paths`? | `applies` → applies; `not-applicable` → not-applicable (`KW-REVIEW-026`); no changed paths → `undecidable` → applies | — | `gate.applies-when.paths`, `review.changed-paths` | 1 |

Notes:

- **No rule short-circuits the council on reserved paths (D13).** Delegation rules still apply to dispatches that touch reserved paths (Req 13.2).
- **`ROSTER-001` closes the `permission-not-expressible` roster degradations** that Claude and Antigravity record today, on every harness where the caller is trusted.
- **`OWNER-001` prefers the task's own `Skills` label.** It falls back to the file-kind map only when the task names no Squad agent.
- **`MODE-001` accepts RED evidence recorded for a task the target task depends on.** This covers plans that split RED and GREEN into separate tasks (`T1-RED-…` → `T4-GREEN-…`).
- **A task that lists no files is still checked by everything else (D30).** It contributes no in-flight paths and overlaps nothing in `READY-001`. Its dependency, mode, roster and intent checks still run.

## 3. Trigger families

Every trigger belongs to a family. The family fixes three things: the effects its rules may use, what `undecidable` maps to, and how the rules' answers combine. A host cannot remap `undecidable` (`KW-ARB-CONFIG-008`).

| Family | Triggers | Effects | `undecidable` maps to | Combination |
|---|---|---|---|---|
| Conductor | `delegate`, `delegate.returned`, `delegate.planner`, `delegate.planner.returned` | `allow`, `escalate` | `escalate` (D10, Req 10.1) | `escalate` wins |
| Investigate | `investigate`, `investigate.returned` | `allow`, `escalate` | `escalate` | `escalate` wins |
| Review spawn | `lens.spawn` | `allow`, `skip` | `allow` (Req 10.3) | `skip` wins |
| Review spawn | `refute.spawn` | `allow`, `verify` | `allow` (Req 10.3) | `verify` wins |
| Review return | `lens.returned` | `allow`, `annotate` | `allow` | every `annotate` is delivered |
| Gate | `gate.select` | `applies`, `not-applicable` | `applies`: the gate runs | — |

- **Where non-allow outcomes go.** On the conductor and investigate families, an escalation goes wherever the caller requires (§4.3, §1.10).
- **Review effects are not escalations.** On the review families, `skip`, `verify` and `annotate` are review notes for `code-reviewer` (Req 15.6).

## 4. Trigger classification

### 4.1 Target classes

The class is derived from the target's capability profile in the embedded catalog.

| Class | Profiles | Agents |
|---|---|---|
| implementation | `worker`, `publishing-worker`, `documentation` | csharp-dev, dal-dev, maui-dev, pulumi-dev, python-dev, react-dev, sql-database-architect, tauri-dev, test-dev, github-devops, docs-dev |
| planner | `architect`, `product-planning` | architect, product-owner |
| read-only | `investigator`, `read-only`, `reviewer` | task-reviewer, bug-crusher-investigator, research-agent, azure-reader, review-lens, review-triage, code-reviewer |
| not Squad | — | any other target |

### 4.2 Caller resolution per harness

Sources are taken in order. The harness payload outranks the rendered `--caller`; the two agree whenever a frontmatter hook fires, and R5 covers the one exception. Neither comes from the prompt.

| Harness | 1. Harness payload | 2. Rendered `--caller` | 3. Headers (asserted, D24) |
|---|---|---|---|
| Claude | `agent_type` on tool events inside sub-agents and under `--agent` [CC-hooks] | Each dispatching agent's frontmatter hook, and the `/conductor` entry-point skill's hook | Not needed: whenever a Squad hook fires, the caller is already known |
| Copilot in VS Code | No caller field documented | Each `.agent.md` hook, which runs while that agent is active, including as a sub-agent [VS-agents] | Used by the `.github/hooks/kyber-arbiter.json` hook, which VS Code also loads [VS-hooks] |
| Copilot CLI, OpenCode, Pi, Codex, Cursor, Kilo, Antigravity, Factory, Devin | None: hooks are project-wide [GH-agents, CU-sub, AG-sub, FA-droids, OC-types] | None | Inference rules below |
| Warp, ZCode | — | — | Asserted in `arbiter_evaluate` (R7) |

**Header inference** applies to marked dispatches on project-wide-hook harnesses:

- `PLAN_FILE` or `TASK` present → the conductor. Only the conductor writes them.
- `LENS` or `REFUTE` present → `code-reviewer`.
- A planner target → the conductor. Only the conductor's roster holds planners.
- Only the marker present → unidentified.

**Copilot in VS Code runs both hooks.** It applies the most restrictive decision, deny over ask over allow [VS-hooks-ref].

- The project-wide evaluation reuses a trusted-caller decision already logged for the same `tool_use_id`. Otherwise it evaluates, and the ledger keeps the trusted-caller event.
- On the same facts, its rules are a subset of the trusted evaluation's: everything except `ROSTER-001`. It can therefore deny what the trusted evaluation allows only through step-1 variance between two calls.

### 4.3 Classification

| Caller | Target class | Trigger | Headers required | Non-allow goes to |
|---|---|---|---|---|
| conductor | implementation | `delegate` / `delegate.returned` | `KYBER-ARBITER`, `PLAN_FILE`, `TASK` | `architect`, with the envelope (D15) |
| conductor | planner | `delegate.planner` / `delegate.planner.returned` | `KYBER-ARBITER`, a planner marker (§4.4), and `PLAN_FILE` when the marker is that header; never `TASK` | For `PLANNER-001`, the conductor; for `PLANNER-DIFF-001`, `architect` |
| conductor | read-only (task-reviewer, code-reviewer, investigators) | `investigate` / `investigate.returned` | `KYBER-ARBITER` and `PLAN_FILE`, plus `TASK` when the dispatch serves a task (always for task-reviewer) | `architect`, with the envelope |
| code-reviewer | review-lens or review-triage, with `LENS` | `lens.spawn` / `lens.returned` | `KYBER-ARBITER`, `LENS` | Recorded by `code-reviewer`: `SKIPPED`, or an annotation |
| code-reviewer | review-lens, with `REFUTE` | `refute.spawn` | `KYBER-ARBITER`, `REFUTE` | Recorded by `code-reviewer` as verified |
| architect, product-owner, code-reviewer | any other target | `investigate` / `investigate.returned` | `KYBER-ARBITER` | Back to that caller |
| unidentified, marked | read-only, with neither `PLAN_FILE` nor `TASK` | `investigate` / `investigate.returned` (`ROSTER-001` does not run) | `KYBER-ARBITER` | Whoever dispatched it, with a `NEXT` that covers both cases (§1.10) |
| unidentified, unmarked (project-wide harnesses only) | any | none: the dispatch passes, and the ledger records `caller: unidentified` | — | — |
| `kyber-weave review gates --base` | a gate | `gate.select` | — | Reported as not applicable (`KW-REVIEW-026`) |

- **An identified caller dispatching outside its roster** classifies as `investigate`, or as `delegate` when the caller is the conductor and the target is an implementation specialist. `ROSTER-001` then escalates it. Examples: an `architect` dispatching `csharp-dev`, or the conductor dispatching `review-lens`.
- **On Claude and Copilot in VS Code**, gating does not depend on the marker (D24). A trusted conductor's unmarked delegation is still gated, and `KW-ARB-AUDIT-002` reports the missing marker.

### 4.4 Planner markers

`KW-ARB-PLANNER-001` recognises a planner dispatch by its marker. Except for `PLAN_FILE`, which is itself a header, a marker is the first non-blank line after the header block.

| Planner | Invocation | Marker |
|---|---|---|
| architect | Intake assessment | `INTAKE: <todo path \| open request>` |
| architect | Authoring a new Draft, or resuming one | A `PLAN_FILE:` header under the plans directory. For a new plan, the conductor assigns the path, which `architect` accepts because it is beneath the plans directory |
| architect | Finalization | A `PLAN_FILE:` header, then the line `FINALIZE` |
| architect | Findings drain | `FINDINGS:` |
| architect | Arbiter escalation | `STATUS: ARBITER_ESCALATION`, the envelope itself |
| product-owner | A spec phase | `FEATURE:` and `PHASE: requirements \| design \| tasks \| phase-approval \| finalization`, as the first two non-blank lines in either order |

A planner dispatch carries no `TASK`. If one appears, it is not read.

## 5. Header grammar

**The header block.** It is the run of lines at the very start of the dispatch prompt in which every line matches `^(KYBER-ARBITER|PLAN_FILE|TASK|LENS|REFUTE):[ \t]*(\S.*?)[ \t]*$`.

- The block ends at the first line that does not match: a blank line, content, or an envelope's `STATUS:` line.
- Names are case-sensitive, and each appears at most once.
- A duplicate name or a malformed value leaves that fact absent. The rules then decide on the absence, for example `PLAN-001` answers `invalid-path`.

| Header | Value | Written by | On |
|---|---|---|---|
| `KYBER-ARBITER` | `true` | conductor, architect, product-owner and code-reviewer, on every dispatch (D24) | Every target that renders hooks |
| `PLAN_FILE` | A repository-relative path, `/`-separated, with no `..` after normalisation, under `config.planning-dirs` | conductor (D21) | `delegate`; `investigate` from the conductor; planner authoring, resume and finalization |
| `TASK` | A task id from `PLAN_FILE`: `T<n>[a-z][-suffix]` in the plan grammar, or `<n>[.<m>]` in the spec grammar (§6, D30) | conductor (D21) | `delegate`; task-reviewer dispatches; never planners |
| `LENS` | One of the 15 lens names below | code-reviewer (D27) | `lens.spawn` |
| `REFUTE` | `<lens>/<slug>`: the lens is one of the 15, and the slug matches `[a-z0-9-]+`. Examples: `security/key-in-argv`, `correctness/null-plan-task` | code-reviewer (D27) | `refute.spawn`. The finding YAML follows the header block intact |

The header set is closed. There is no `FILES:` header, and the conductor does not list the files a worker may edit (D33).

The lens names are `authz-tenancy`, `blast-radius-revertibility`, `correctness`, `dependency-supply-chain`, `di-composition`, `duplicate-implementation`, `infra-workflow`, `intent-alignment`, `model-placement`, `performance`, `prior-art`, `security`, `static-analysis-triage`, `supportability`, and `test-adequacy`. Finding ids take the form `<lens-name>/<short-slug>`, as `review-lens` reports them.

**Metadata, not content (Req 25).**

- **Implementation targets.** Where §12 documents input rewriting, the hook returns the prompt without the header block and the one blank line that follows it.
- **Other targets** receive the prompt unchanged. Their roles keep plan access (Req 25.3), and a planner's `PLAN_FILE` is its invocation.

**The marker on every hooked harness.** Squad agents write the routing headers, `KYBER-ARBITER: true` included, on every target that renders hooks. They write none on the fallback targets, Warp and ZCode, where the routing facts go to `arbiter_evaluate` instead. The canonical instruction keys on whether the agent holds `arbiter_evaluate`, not on the target's name. The reasons:

1. **The Copilot target's rendered `.agent.md` files serve both Copilot in VS Code and Copilot CLI.** Per-harness text cannot omit the marker for VS Code without also omitting it for the CLI, which needs it.
2. **Canonical agent bodies stay target-neutral.** That is an existing invariant, pinned by `HotshotGoldenContractTests`.
3. **`KW-ARB-AUDIT-002` checks for the marker the same way everywhere.**
4. **Where the caller is trusted, gating ignores the marker (D24),** so writing it there costs one line and changes no decision.

## 6. Plan parser contract

`PlanDocumentParser` is a pure function of the file text (Req 9). It uses Markdig, with CommonMark, pipe tables and YAML front matter, for structure, and regex for labels. It writes nothing.

| Element | Contract |
|---|---|
| Mode and status | Read from the frontmatter `status` and `development-mode`. A spec task artifact's body line `**Development mode:** <mode>` is the fallback. When both are absent, the mode is `test-first` |
| Tasks section | The H2 `Tasks` or `Implementation Tasks`, case-insensitive. Without one, `HasTasks` is false and `PLAN-001` answers `no-tasks` (Req 10.2) |
| Plan task grammar (Req 9.3) | After `**` emphasis is removed, an H3 inside the Tasks section matching `^T(?<n>\d+)(?<sub>[a-z])?(?<suffix>(?:-[A-Za-z0-9]+)*)(?:\s*:\s*\|\s+[—–-]\s+)?(?<title>.*)$`. The task id is `T<n><sub><suffix>`: `T4a`, or `T1-RED-provenance-headers`. Both title separators occur in the archive: `### T1: RED, Claude renderer contract` and `### T1 — Establish the failing regression contracts`. A task's section runs to the next H3 or H2 |
| Spec task grammar (D30) | A list item in the Tasks section matching `^- \[( \|x\|X)\] (?<id>\d+(?:\.\d+)?)\.?\s+(?<title>.+)$`, such as `- [ ] 2.1 Title`. Its nested bullets are the task body. A ticked checkbox is recorded as `checked`, for information only; it is not completion. A spec task's files are the Files label's value when a label is present. Otherwise they are the backticked paths in its body, taken by the path rule. The product-owner tasks template does not change |
| Files label family | `Files`, `Files / symbols`, `Files/symbols owned`, `Files owned`, `scope` and `Scope`, case-insensitive. Bold is optional, the colon may sit inside or outside the bold, and a leading `- ` is optional. The value is the backticked paths on the label line, plus the bullet items that follow, up to the next label or the end of the list |
| Depends-on label family | `Depends on`, `Depends-on` and `depends-on`, in the same forms. The value is the task-id tokens up to the next bold label or the end of the paragraph. `none` means no dependencies |
| Skills label family | `Skills`, `Required skills` and `Required skill`, giving backticked names. A heading suffix `(agent)` also counts, as in `### T4-GREEN-provenance-core: … (csharp-dev)` |
| Path rule | A backticked span is a path when it contains `/` or ends in a file extension, and contains no spaces. A bare file name after a path in the same bullet inherits that path's directory. A trailing `/` becomes `dir/**`, and a `<placeholder>` segment becomes `*`. Other spans, such as type names and flags, are ignored |
| Dependency tokens | `T\d+[a-z]?(?:-[A-Za-z0-9]+)*`, or, in the spec grammar and only after the label, `\d+(?:\.\d+)?`. A token that names no task raises `KW-ARB-PARSE-001`, and `READY-001` answers `unknown-dependency` |
| Test or verification contract | The first pipe table under an H2 containing `Test contract` or `Verification contract`. Rows are keyed by a first cell holding a task id. Cells are kept by header name: `Task`, `Test project or file`, `Runner filter` or `Runner command`, `Observable behaviour` or `behavior`, `RED evidence required`, `GREEN acceptance`. `MODE-001` reads `RED evidence required` |
| Out of scope | The H2 `Out of scope`, case-insensitive. Its backticked paths, taken by the path rule, become `plan.out-of-scope` |
| Output | `PlanDocument(Status, DevelopmentMode, HasTasks, Tasks, OutOfScope, ContractRows, Diagnostics)` and `PlanTask(Id, Title, Text, Files, DependsOn, Skills, Checked)`. A task that lists no files has an empty `Files` and raises `KW-ARB-PARSE-002`; its file-scope checks are skipped and the skip is logged (D30) |

- **Why the families match on their leading word.** The labels Req 9.3 names are matched as families, so the older `Files / symbols` and `Files owned` variants in `docs/archive/plans/` also parse.
- **Two archived plans have no Tasks section:** `2026-09-29-pr-158-defects` and `2026-09-29-glib-variant-str-iter-backport`. Each answers `no-tasks`.

## 7. Configuration schema

```yaml
arbiter:
  enabled: false                       # squad install/update render hooks only when true (Req 22.2)
  provider:                            # non-secret; the key never appears here (Req 18.4)
    kind: none                         # none (default, Req 19.1) | systemone
    endpoint: https://api.typesafe.ai/v1   # or http://localhost:11434/v1 for Ollama 0.35+
    model: jev-1.13.0                  # pinned (Req 17.4); jev-latest drifts (R10); locally nimble or tev1
    timeout-ms: 3000                   # inside the hook latency budget (Req 5.3)
  rules:
    - id: KW-ARB-SCOPE-002             # shipped rule, tuned by id
      confidence-at-least: 0.85
    - id: KW-ARB-OWNER-002
      enabled: false
    - id: HOST-MIGRATIONS-001          # host rule: its own id, without the KW-ARB- prefix
      trigger: delegate
      question: Does the task touch database migrations?
      answers: [touches, clear]
      decide:
        - when: { fact: plan.task.files, intersects: ["src/**/Migrations/**"] }
          answer: touches
        - when: { fact: plan.task, exists: true }
          answer: clear
      effects: { touches: escalate, clear: allow }
```

**Defaults.** With no `arbiter:` section, the Arbiter is disabled, the provider is `none`, and all 18 shipped rules are enabled.

**Host overrides by id (Req 14.2).** A host may set only these fields on a shipped rule:

- `enabled`;
- `confidence-at-least` or `probability-below`;
- `effects`, which merge per answer.

Any other field raises `KW-ARB-CONFIG-005`, with a hint to add a host rule. Host rules use the full shape from §1.4, under ids that do not start with `KW-ARB-` (Req 14.3).

**User override.** `~/.config/kyber-weave/arbiter.yml` may hold `provider:` and nothing else (Req 22.3). Its fields replace the repository's `arbiter.provider` field by field; any other key raises `KW-ARB-CONFIG-009`.

**Loading.** The section is a new `ArbiterYamlSection`, merged by `ArbiterConfigLoader.Merge` into `KyberWeaveConfig.Arbiter`. `KyberWeaveConfig.Clone` learns the new section. Lists replace rather than append, which is the Core convention.

**Diagnostics.** Every id is permanent.

| Id | Severity | Meaning | Raised by |
|---|---|---|---|
| `KW-ARB-CONFIG-001` | Error | The `arbiter:` section or the user override is malformed. The file is named | `validate`, `hook`, `doctor` |
| `KW-ARB-CONFIG-002` | Error | A rule reads a fact its trigger does not supply. Hints at the nearest fact | `validate` |
| `KW-ARB-CONFIG-003` | Error | An unknown lens in `instructions-from`. Hints at the nearest lens | `validate` |
| `KW-ARB-CONFIG-004` | Error | An answer with no effect | `validate` |
| `KW-ARB-CONFIG-005` | Error | A duplicate, retired or reserved-prefix id, or a non-overridable field on a shipped rule | `validate` |
| `KW-ARB-CONFIG-006` | Warning | Model rules are switched off because the provider is `none` (Req 19.3) | `validate`, `doctor` |
| `KW-ARB-CONFIG-007` | Warning | The configured model is not in an enabled step-1 rule's `tuned-for`, or has no known budget | `doctor` |
| `KW-ARB-CONFIG-008` | Error | An effect not allowed for the rule's trigger family, or a remapped `undecidable` | `validate` |
| `KW-ARB-CONFIG-009` | Error | The user override holds a key other than `provider` | `validate`, `doctor` |
| `KW-ARB-CONFIG-010` | Error | A non-loopback endpoint uses `http`. The key is never sent over plain HTTP | `validate`, `doctor`, `hook` |
| `KW-ARB-KEY-001` | Warning | A remote provider is configured, and no key resolves for its origin | `doctor`, `status` |
| `KW-ARB-LOG-001` | Warning | `artifacts/arbiter/` is not ignored by git | `doctor` |
| `KW-ARB-BIN-001` | Warning | `kyber-weave-arbiter` is missing from `PATH`, or its version differs from the CLI's | `doctor`, `squad doctor` |
| `KW-ARB-GUARD-001` | Warning | No plan, spec or todo index is declared, so the Read guard protects nothing (§12) | `doctor` |
| `KW-ARB-PARSE-001` | Warning | A dependency names no task in the artifact | `plan` |
| `KW-ARB-PARSE-002` | Info | A task lists no files, so its file-scope checks are skipped (D30) | `plan` |
| `KW-ARB-AUDIT-001` … `-004` | Warning | §1.6 | `audit` |
| `KW-ARB-HOOK-001` | Error | An internal hook error produced a fail-closed block (D5) | `hook`, `serve` |
| `KW-REVIEW-026` | Info | A gate does not apply to this change (D12) | `review gates` |

The 18 rule ids are listed in §2. The degradation code `arbiter-not-enforced` is defined in §10.5.

## 8. Escalation flow

### 8.1 Conductor triggers, end to end

1. The harness fires the pre-dispatch hook, and the evaluation runs steps 0 and 1 (§1.2).
2. On a non-allow outcome, the harness blocks the dispatch. The conductor receives the envelope as the block's reason.
3. The conductor follows `NEXT`. On `REPEAT: 1`, it dispatches `architect`: a `delegate.planner` dispatch whose marker is the envelope. `PLANNER-001` recognises it. The conductor never retries the blocked dispatch unchanged (D15).
4. `architect` follows its new `architect/references/arbiter-escalation.md` and returns one of three outcomes:
   - **`STATUS: ESCALATION_RESOLVED`**, with corrected dispatch guidance inside the approved plan: a narrower scope, the right specialist, or a dependency to wait for. The user is not asked.
   - **`STATUS: NEEDS_DECISION`**, in its existing format, which the conductor relays to the user.
   - **A Draft amendment to the plan.** Execution of the affected task then waits for the normal approval gate.

   `architect` makes that choice. Neither the Arbiter nor the conductor does (Req 15.3).
5. The conductor re-dispatches with the corrected guidance.
6. If the same rule escalates again on the same plan digest and task, the envelope carries `REPEAT: 2` and `NEXT` says stop. The conductor records a run finding, and the findings drain takes it (R8).

   An `ESCALATION_RESOLVED` cannot authorise a dispatch the rules block. It can only change the dispatch. Changing the policy is a plan amendment or a host configuration change.
7. **Post-dispatch outcomes** (`DIFF-001`, `PLANNER-DIFF-001`, and `READONLY-001` on conductor dispatches) follow the same path. The work is already in the tree, so `architect` decides whether a rework task or an amendment follows.
8. **On the fallback targets**, `arbiter_evaluate` returns the identical envelope to the conductor's own call.

### 8.2 Exceptions

| Case | Handling |
|---|---|
| `PLANNER-001` answers `malformed` | The envelope goes back to the conductor, with a `NEXT` to re-issue the dispatch with a recognised marker. Dispatching `architect` with it would repeat the same malformed call. A repeat stops, as in R8 |
| A planner is the caller: `architect` or `product-owner` dispatching an investigator, where `ROSTER-001` or `READONLY-001` is non-allow | The dispatch is blocked, and the reason goes back to that caller. The caller reports it in its own result (`GAPS` or `STATUS: BLOCKED`) and does not retry unchanged. No `architect` dispatch follows, because `architect` would be escalating to itself |
| `code-reviewer` is the caller, on an investigate trigger | The reason goes back to `code-reviewer`, which cites it in the review |
| Review triggers | A skip, a verify and an annotation are review notes (§1.10), not escalations |
| The hook fails on the escalation dispatch to `architect` itself | It is a conductor trigger, so its envelope is `KW-ARB-HOOK-001` with `ANSWER: error`. A second failure is `REPEAT: 2`, and the run stops (R8) |

### 8.3 Ledger loss and attestation (D31)

`READY-001` and `MODE-001` read completion and RED evidence from the ledger, and `artifacts/` is not committed. A run resumed in a fresh clone, or after the folder was cleaned, therefore escalates every task with a recorded dependency. Recovery runs through the escalation path:

1. The escalation reaches `architect` as usual (§8.1).
2. `architect` returns `STATUS: NEEDS_DECISION`, asking the user to confirm which tasks are complete and which have RED evidence.
3. Once the user confirms, `architect` returns `ESCALATION_RESOLVED` carrying the line `ATTESTED: <task>=complete|red, …`.
4. The hook reads that line from the `delegate.planner.returned` output and records it under `returns.attested`. `READY-001` and `MODE-001` then count it.

No agent writes the ledger (D3). `audit` lists every attestation.

### 8.4 Error handling

| Condition | Conductor and investigate triggers | Review triggers | `gate.select` |
|---|---|---|---|
| Malformed stdin or an unknown payload | Block, `KW-ARB-HOOK-001`, envelope with `ANSWER: error` | Block with the reason (Req 5.1). `code-reviewer` reports the lens as not run, never as `SKIPPED` | Not applicable |
| Configuration missing or malformed, on a classified event | Block, `KW-ARB-HOOK-001` | Block | The gate runs, and `review gates` reports `KW-ARB-CONFIG-001` |
| `arbiter.enabled: false` at runtime | Allow, and log | Allow | `applies-when` still applies, because it is a review setting |
| Provider error, timeout, 401 or 422 | `undecidable` → escalate | `undecidable` → allow | — |
| State over budget | `undecidable` → escalate, with no call made | allow | — |
| Provider `none` | Step 1 not evaluated, which is not `undecidable` | Not evaluated | — |
| Ledger lock not acquired within 500 ms | Block, `KW-ARB-HOOK-001` | Block | — |
| Exception in the evaluator | An error result; never allow | Block | The gate runs |
| Process killed by the harness's timeout | Depends on the harness: Copilot fails open, Claude treats a crash without exit code 2 as non-blocking, Cursor's `failClosed` blocks. The ledger event recorded first leaves an undecided entry for `KW-ARB-AUDIT-001` (R1) | same | — |

## 9. Harness facts

### 9.1 Per-harness hook facts

| Target | Phase | What Squad writes | Dispatch tool | Deny output | Caller identity | Input rewriting | Trust gate | Sources |
|---|---|---|---|---|---|---|---|---|
| Claude | 1 | `hooks` in `.claude/agents/<agent>.md` frontmatter, and in the `/conductor` entry-point skill's | `Agent`, matched by exact name with `^(Agent\|Task)$`, so task-list tools whose names start with `Task` do not match (R19). An event with no sub-agent target passes through untouched (§1.7) | Pre: `hookSpecificOutput.permissionDecision: deny` with `permissionDecisionReason`. Post: `decision: block` with `reason` | `agent_type` inside sub-agents; `--caller` otherwise | `hookSpecificOutput.updatedInput` | The workspace trust dialog. `claude -p` sessions do not count | CC-hooks, CC-sub, CC-tools |
| Copilot in VS Code | 1 | `hooks` in `.github/agents/<agent>.agent.md` (Preview, Local harness). VS Code also loads `.github/hooks/*.json` | `runSubagent`. Matchers are ignored, so the adapter filters on `tool_name` | `hookSpecificOutput.permissionDecision: deny` with the reason. The most restrictive decision wins | Rendered `--caller`; the payload has no caller field | `hookSpecificOutput.updatedInput` | Workspace trust, and the `chat.useHooks` setting | VS-hooks-ref, VS-agents, VS-hooks, VS-sub |
| Copilot CLI | 1 | `.github/hooks/kyber-arbiter.json`, its own file. Every file there runs, in alphabetical order | `task` | `permissionDecision: deny` with `permissionDecisionReason` | None | `modifiedArgs` | None documented | GH-hooks-cfg, GH-hooks-ref, GH-3013 |
| OpenCode | 1 | `.opencode/plugins/kyber-arbiter.ts`, its own file | `task` (inferred) | An error thrown in `tool.execute.before` | None: the `tool.execute.before` input has no agent | Mutating `output.args` | None documented | OC-plugins, OC-types |
| Pi | 2 | `.pi/extensions/kyber-arbiter.ts`, its own file | `Agent` from `@tintinweb/pi-subagents`. Pi has no built-in dispatch tool | `{block: true, reason}` returned from `tool_call` | None | Mutating `event.input` | Project trust for `.pi/extensions/` | PI-ext, PI-cfg, PI-sec, PI-sub |
| Codex | 2 | An owned block in `.codex/hooks.json` | `spawn_agent`, also matched as `Agent` | `permissionDecision: deny` with the reason | None | `updatedInput`, returned with `permissionDecision: allow` | A trusted `.codex/` layer. Each new or changed hook is skipped until the user trusts it through `/hooks` | CX-hooks |
| Cursor | 2 | An owned block in `.cursor/hooks.json` | `Task` | `{permission: deny, agent_message}`. Hooks fail open by default, so entries set `failClosed: true` | None | `updated_input`, on `preToolUse` only | None documented | CU-hooks, CU-sub |
| Kilo | 3 | `.kilo/plugin/kyber-arbiter.ts`, its own file | `task` (inferred) | A thrown error | None | Mutating `output.args` | None documented | KI-plugins, KI-sub |
| Antigravity | 3 | An owned top-level group `kyber-arbiter` in `.agents/hooks.json` | `invoke_subagent` | `decision: deny` with `reason` | None | Not documented for `hooks.json`, whose `PreToolUse` output lists only `decision`, `reason` and `permissionOverrides`. The SDK documents `modified_args`, which does not apply to `hooks.json` | None documented | AG-hooks, AG-sub, AG-sdk |
| Factory | 3 | An owned block in `.factory/hooks.json`, unless that would shadow the user's hooks (§10.8) | `Task` | `permissionDecision: deny` with the reason | None | `hookSpecificOutput.updatedInput` | None documented | FA-hooks, FA-guide, FA-droids |
| Devin | 3 | An owned block in `.devin/hooks.v1.json`, which uses the Claude Code format | `run_subagent`. That `PreToolUse` fires for it is inferred | `decision: block` with `reason` | None | `hookSpecificOutput.updatedInput`, merged into the arguments | None documented | DV-hooks, DV-life, DV-sub, DV-changelog |
| Warp | 3 | No hook: the MCP fallback | — | — | Asserted | — | — | WA-7834 |
| ZCode | 3 | No hook: project-level hooks are not executed in the current version. The MCP fallback | — | — | Asserted | — | — | ZC-hooks, ZC-32 |

### 9.2 Pairing and post-dispatch

| Target | Tool-call id | Hooks inside sub-agents | Post-dispatch feedback | Fail-open hazard |
|---|---|---|---|---|
| Claude | `tool_use_id` | Documented, with `agent_id` and `agent_type` | `PostToolUse`: `decision: block`, `additionalContext` | A crash without exit code 2 is non-blocking |
| Copilot in VS Code | `tool_use_id` | Documented for `.agent.md` hooks | `additionalContext` | — |
| Copilot CLI | Not documented, so pairing uses `pair-digest` | Yes: closing comment of 2026-08-05 on copilot-cli#3013, by the GitHub staff engineer assigned to the issue. The reference page is silent | `postToolUse`: `additionalContext`, `modifiedResult` | Fails open on a hook timeout |
| OpenCode | `callID` (plugin source) | Not documented, so supported under D7 | `tool.execute.after` | — |
| Pi | `toolCallId` | Not documented | Documented | Extension failures block |
| Codex | Not verified, so `pair-digest` | Not documented | Documented | — |
| Cursor | `tool_use_id` | Not documented | `postToolUse` | Fails open unless `failClosed: true` |
| Kilo | `callID` | Not documented | `tool.execute.after` | — |
| Antigravity | Not documented, so `pair-digest` | Not documented | None: `PostToolUse` output is `{}`. Results are recorded only if the input carries them, which is not documented and so supported under D7 | — |
| Factory | Not verified, so `pair-digest` | Not documented | Documented | — |
| Devin | Not verified, so `pair-digest` | Not documented | Documented | — |
| Warp, ZCode | — | No hooks | — | — |

## 10. Squad rendering per phase

Hooks are rendered only when the project's `arbiter.enabled` is `true` (Req 22.2).

- **The render request** gains an optional trailing `Arbiter` member (`SquadArbiterWiring?`: enabled, and scope), and the render result gains an optional trailing `Blocks` member. Both are trailing optionals, so the 37 callers of the positional records keep compiling.
- **A request without `Arbiter`** renders exactly as today.
- **Global installs** render no hooks (Req 22.4).

### 10.1 Which agents get hooks

| Agents | Hook | Targets | Why |
|---|---|---|---|
| conductor, architect, product-owner, code-reviewer; on Claude, also the `/conductor` entry-point skill | Pre- and post-dispatch gating on the dispatch tool, with `--caller <agent>` | Claude, Copilot in VS Code | These are exactly the agents with a non-empty `delegates-to`, so they are the only dispatchers. Per-agent hooks make the caller trusted (D22) without a new agent field, which `agent.schema.json` (`additionalProperties: false`) would refuse |
| The implementation specialists in the `worker` and `publishing-worker` profiles: csharp-dev, dal-dev, github-devops, maui-dev, pulumi-dev, python-dev, react-dev, sql-database-architect, tauri-dev, test-dev | The planning-path Read guard (§12), with `--caller <agent>` | Claude, Copilot in VS Code | Req 25.2 can be enforced only where the reading agent is known |
| docs-dev, task-reviewer, review-lens, review-triage, research-agent, azure-reader, bug-crusher-investigator | None | — | Req 25.3 keeps docs-dev's access. The others are read-only roles that dispatch nothing |
| None per agent | One project hook | Copilot CLI, OpenCode, Pi, Codex, Cursor, Kilo, Antigravity, Factory, Devin | These harnesses have no per-agent hooks. D24 gates by the marker |
| None | The MCP server, granted to the `decision.query: allow` agents | Warp, ZCode | No hooks (D4) |

### 10.2 Per-harness declaration

| Target | Phase | Declaration | Ownership |
|---|---|---|---|
| Claude | 1 | Frontmatter `hooks`. On the dispatchers: `PreToolUse` and `PostToolUse` with matcher `^(Agent\|Task)$`. On the implementation specialists: `PreToolUse` with matcher `^(Read\|Grep\|Glob\|Bash)$`. The `/conductor` skill is rendered by `RenderPrimaryAgentEntryPointSkill` | Existing owned agent and skill files |
| Copilot | 1 | `.agent.md` frontmatter `hooks` (`PreToolUse`, `PostToolUse`) on the same agents, with `--harness copilot-vscode`. `.github/hooks/kyber-arbiter.json` (`preToolUse`, `postToolUse`) with `--harness copilot-cli`. Its adapter accepts `task` from the CLI and `runSubagent` when VS Code loads the same file | Existing owned agent files, plus one new owned file |
| OpenCode | 1 | The `.opencode/plugins/kyber-arbiter.ts` shim (§1.7) | New owned file |
| Pi | 2 | The `.pi/extensions/kyber-arbiter.ts` shim, matching the `@tintinweb/pi-subagents` `Agent` tool | New owned file |
| Codex | 2 | `PreToolUse` and `PostToolUse` entries matching `spawn_agent` | Owned block |
| Cursor | 2 | `preToolUse` and `postToolUse` entries matching `Task`, with `failClosed: true` and a timeout | Owned block |
| Kilo | 3 | The `.kilo/plugin/kyber-arbiter.ts` shim | New owned file |
| Antigravity | 3 | The top-level hook group `kyber-arbiter`, matching `invoke_subagent` | Owned group |
| Factory | 3 | Entries matching `Task`, rendered only when there is no shadowing (§10.8) | Owned block |
| Devin | 3 | Entries matching `run_subagent` | Owned block |
| Warp, ZCode | 3 | No hook. `kyber-weave-arbiter serve --repo-root .` is granted through the existing MCP configuration path | MCP configuration |

### 10.3 Marker

§5 states the decision, and its reasons, to write `KYBER-ARBITER: true` on every hooked target and on none of the fallback targets.

### 10.4 Receipt and ownership

- **Phase 1 changes no receipt schema.** Its outputs are whole owned files (`SquadOwnedFile`): agent and skill files that are already owned gain frontmatter, and `.github/hooks/kyber-arbiter.json` and the OpenCode shim are new owned files.
- **Phase 2 adds owned blocks (Req 8, D8, D25).**
  - **Rendering.** A renderer emits a block fragment: the target, path, format and entries. `SquadDeploymentPlan` splices it into the current file at plan time, with an `Exact` precondition on that file's digest. A file that does not exist is created. `SquadTransaction`'s claim-and-publish protocol is unchanged, because a block is a `Write` with an `Exact` precondition.
  - **Identification.** Squad's entries are identified by their command signature, `kyber-weave-arbiter hook --harness <h>`. D25's `--caller <agent>` is part of the signature only where a hook is per agent. Every shared-file target has project-wide hooks, so these signatures carry no `--caller`.
  - **Antigravity.** Squad owns the top-level group `kyber-arbiter`. Top-level keys there are group names, so any unknown key would be read as another group.
  - **Fields.** Only documented fields are written, and no sentinel key. JSON hook files carry no comments, so markers in the style of the Config Reg block cannot be used.
- **Receipt v3.**
  - `kyber-squad.receipt/v3` adds `blocks`: for each owned entry, the file, its JSON pointer and its canonical digest.
  - It is written only when a receipt carries a block. Receipts without blocks stay byte-identical v1 or v2, following the precedent of [ADR 0024](../../adr/0024-squad-global-receipt-layout-marker.md).
  - Older CLIs refuse a v3 receipt with exit 1.
- **Update, uninstall and drift.**
  - Update rewrites the block.
  - An owned entry edited by hand is drift. `squad status` and `squad doctor` report it (Req 8.4), and update preserves it unless `--replace-managed` is given.
  - Uninstall removes only the owned entries. It deletes the file only if Squad created it and no hook remains.
- **ADR 0029** records the exception to the rule that Squad does not own settings files ([ADR 0026](../../adr/0026-kyber-utilities-owned-files-not-settings.md)) (Req 8.3).

### 10.5 Degradations

`arbiter-not-enforced` is recorded at render time, per target and agent. Its reason goes in `SquadDegradationRecord.Details`; the receipt keeps the code. It is never a runtime condition. A missing marker at runtime is an audit finding (`KW-ARB-AUDIT-002`), and an untrusted hook is covered by §10.7.

| Kind | `Details` | Targets |
|---|---|---|
| Fallback-only target | `fallback-only`: no hooks, so the D4 fallback is advisory (R7). Planner investigator dispatches are ungated there, because only the conductor and `code-reviewer` call `arbiter_evaluate` (Req 4.1) | Warp, ZCode |
| Global install | `global-scope`: there is no project configuration to read (Req 22.4) | Every target |
| No hook support at a decision point | `no-post-dispatch-feedback`: post-dispatch outcomes are logged and reported by `audit`, but not delivered | Antigravity |
| No hook support at a decision point | `settings-hooks-shadowed` (§10.8) | Factory |

### 10.6 `decision.query` capability (Phase 3)

- **Rationale: the D4 fallback.** The conductor and `code-reviewer` must reach `arbiter_evaluate`, and nothing else may gain a grant. Today `ClaudeRenderer.ResolveTools` withholds every MCP server from the orchestrator.
- **Vocabulary.** The capability is added to the vocabulary in `products/kyber-squad/profiles/capabilities.yml`. `SquadSourceLoader.ParseCapabilityProfiles` rejects a profile that omits any vocabulary capability, so all ten profiles declare it explicitly:
  - `allow` for `orchestrator` and `reviewer`;
  - `deny` for `architect`, `architect-copilot`, `documentation`, `investigator`, `product-planning`, `publishing-worker`, `read-only` and `worker`.
- **Lowering.**
  - On Warp and ZCode, `allow` lowers to the `kyber-weave-arbiter` server alone. On hooked targets it lowers to nothing.
  - A separate server name is the only portable grant that excludes the docs tools, because MCP grants are whole-server wildcards (`mcp__kyber-weave__*`, `kyber-weave/*`).
- **Order.** The renderer filters land before `mcp.json` and `toolchain.yml` gain the server, so no hooked target is widened. The filters are Claude's `StandardMcpTools`, ZCode's and Devin's `QualifiedMcpToolNames`, and Factory's `mcpServers: []`.
  - `mcp.json` gains `kyber-weave-arbiter serve --repo-root .`.
  - `toolchain.yml`'s `required-mcp-tools.kyber-weave-arbiter` lists the three tools.
- **Signal.** Holding `arbiter_evaluate` is the agent's signal to use the fallback, so the canonical bodies need no knowledge of targets.
- **Reserved paths.** `capabilities.yml` is an `always-human` path (D13). `mcp.json` and `toolchain.yml` are not.

### 10.7 Trust gates

| Harness | Gate | Effect until trust is granted |
|---|---|---|
| Claude | The workspace trust dialog [CC-sub]. `claude -p` sessions never count | No project sub-agent frontmatter hook runs, so headless runs are unenforced |
| Codex | A trusted `.codex/` layer, plus per-hook trust by hash through `/hooks` [CX-hooks] | Each new or changed hook is skipped. Every `squad update` that changes the hook needs trust again |
| Pi | Project trust for `.pi/extensions/` [PI-sec] | The extension does not load |
| Copilot in VS Code | Workspace trust and `chat.useHooks` [VS-agents] | Hooks are off |

Mitigation:

- `squad install` and `squad update` print the trust step for these targets.
- The onboarding document states it.
- `audit` cannot see dispatches that no hook observed (§1.6), which is why trust is surfaced at install time.

### 10.8 Factory shadowing

- **The hazard.** Factory reads the `hooks` key of `.factory/settings.json` only when `.factory/hooks.json` is absent [FA-hooks]. Creating `hooks.json` would therefore silently disable any hooks the user keeps in `settings.json`.
- **The renderer's response.** When `settings.json` has a `hooks` key and `hooks.json` is absent, the Factory renderer renders no block. It records `arbiter-not-enforced` with `settings-hooks-shadowed`, naming the fix: move the user's hooks into `.factory/hooks.json`, then run `squad update` (R18).

### 10.9 Agent and skill contract changes

| File | Change | Basis | Phase |
|---|---|---|---|
| `agents/conductor.md`, `conductor/references/execution-and-review.md` | Write the header block on every dispatch (§4.3). Tell implementation specialists that the packet is their whole context and that they do not open plan, spec or todo files. On any `ARBITER_ESCALATION`, follow `NEXT`. Never retry unchanged | D21, D24, D15, Req 25.2, R8 | 1 |
| `conductor/references/plan-path.md`, `spec-path.md`, `intake-path.md` | The planner markers (§4.4) | `PLANNER-001` | 1 |
| `agents/architect.md`, plus the new `architect/references/arbiter-escalation.md` | Route `STATUS: ARBITER_ESCALATION`; the three outcomes; `ATTESTED:` after user confirmation; the marker on investigator dispatches | D15, D24, D31 | 1 |
| `agents/product-owner.md` | The marker on investigator dispatches | D24 | 1 |
| `agents/code-reviewer.md`, `skills/code-review/SKILL.md` | The `LENS:` and `REFUTE:` headers, with the marker. Record the review notes (§1.10). Pass `--base` to `review gates`. Write findings to `artifacts/findings.json`, not the repository root. Run `kyber-weave arbiter audit` and cite it | D11, D21, D27 | 1 |
| `agents/review-lens.md` | Refutation framing, keyed on `REFUTE:` | D27 | 1 |
| `agents/test-dev.md` | A `RED_EVIDENCE: <runner filter> — <failing tests> — <reason>` line in the completion digest, or `RED_EVIDENCE: none` | `MODE-001` | 1 |
| `HotshotGoldenContractTests` | `EvolvedAgentIdentities` gains review-lens and test-dev. `EvolvedSkillIdentities` gains code-review, and every other pinned skill file edited above. Pinned lists are extended, never loosened | — | 1 |
| `docs/kyber-squad/requirements.md` | KS-001 counts 11 progressive-disclosure references, adding the escalation reference | — | 1 |
| `agents/conductor.md`, `agents/code-reviewer.md` | When the agent holds `arbiter_evaluate`: call it with the routing facts, and write no headers | D4 | 3 |

The Req 25.2 instruction travels in the conductor's packet rather than in ten implementation bodies, which `HotshotGoldenContractTests` pins as imported Hotshot contracts.

Which of these paths are reserved:

- **`always-human` (D13):** the `products/kyber-squad/agents/**` paths, along with `capabilities.yml`, `.kyber-weave/kyber-weave.yml` and the `*credential*` files elsewhere in this design.
- **Not reserved:** the skill files, the tests and the docs.

## 11. Gate `applies-when`

```yaml
review:
  gates:
    - id: ts-typecheck
      run: [npm, run, --prefix, dash, typecheck]   # argv, never a command string
      blocking: true
      applies-when:
        paths: ["dash/**"]
```

| Aspect | Contract |
|---|---|
| Schema | `applies-when.paths` is a list of `PathGlob` patterns. A gate applies when any changed path matches any pattern (`KW-ARB-GATE-001`). `run` stays argv, as `.kyber-weave/kyber-weave.yml` requires, because `ProcessRunner` refuses a shell |
| Changed paths | `review gates --base <ref>` computes `git diff --name-only <ref>...HEAD`, together with the staged, unstaged and untracked paths. The report records `base`, which lens-spawn facts reuse (§1.3) |
| Without `--base` | Every gate runs, and the report says that `applies-when` was not evaluated |
| Not applicable | The gate is not executed. `GateResult` carries its not-applicable reason, and the report lists it with outcome `KW-REVIEW-026` (Info) rather than omitting it (D12) |
| Verdict engine | `VerdictEngine.EvaluateGates` and `GradeRisk` count a not-applicable gate as neither passed nor failed. A verdict with not-applicable gates equals the verdict without them (Req 12.3) |
| Compatibility | `review-gates/v1` gains only optional fields, so older reports without `applies-when` read unchanged (Req 12.4) |
| Code | `ReviewGateYaml.AppliesWhen`; `ReviewGate` gains an optional trailing `AppliesWhen`; `ReviewConfigLoader.ParseGates`; `GateRunner.Run` gains an optional `changedPaths`; `ReviewGatesSettings.Base`; `ReviewGateOutcome.NotApplicable` |
| This repository | `ts-typecheck`, `ts-test`, `ts-lint` and `ts-reachable` gain `applies-when: { paths: ["dash/**"] }`. The .NET gates stay unconditional |
| Agent contract | `code-reviewer` passes `--base` (§10.9) |

## 12. Req 25 and issue #278 enforcement

The packet is a worker's whole context (Req 25.1). There are two mechanisms (Req 25.4), and both apply only to implementation-class targets:

- a **Read guard** where the reading agent is known;
- **header stripping** where the harness documents input rewriting.

**Read guard.**

- **What it protects.** The directories of the paths declared as `<plan-index>`, `<specification-index>` and `<todo-index>` in the host's configuration; here, `docs/plans`, `docs/specs` and `docs/todo`. With none declared, it protects nothing, and `doctor` raises `KW-ARB-GUARD-001`.
- **What it denies.** A tool call by a guarded agent whose input holds a string that resolves, after normalisation and relative to the repository root, inside a protected directory. The reason names Req 25.
  - Path-valued inputs are matched exactly: Claude's `Read.file_path`, `Grep.path`, and `Glob.path` and `pattern`.
  - Shell commands (`Bash.command`) are matched by substring. That is best effort, because indirection escapes it (R24).
- **VS Code.** VS Code hooks see every tool, because matchers are ignored, and VS Code does not document its tool names [VS-hooks-ref]. The check therefore keys on the input's content, not on the tool's name.
- **Scope.** The guard applies whenever a guarded agent runs, inside or outside a conductor run. docs-dev is not guarded, because the closeout keeps plan access (Req 25.3).

| Harness | Read guard (Req 25.2) | Header stripping (Req 25.1) | Result |
|---|---|---|---|
| Claude | Enforced: caller from `agent_type` or `--caller`, with frontmatter hooks on the implementation specialists | `updatedInput` | Enforced. Shell reads are best effort |
| Copilot in VS Code | Enforced: the `.agent.md` hook runs while the agent is a sub-agent; caller from the rendered `--caller`, not a payload field | `updatedInput` | Enforced, while the feature is in Preview, and given trust and `chat.useHooks` |
| Copilot CLI | Advisory | `modifiedArgs` | Headers stripped; plan reads advisory |
| OpenCode | Advisory | `output.args` | Headers stripped; plan reads advisory |
| Pi | Advisory | `event.input` | Headers stripped; plan reads advisory |
| Codex | Advisory | `updatedInput`, with `allow` | Headers stripped; plan reads advisory |
| Cursor | Advisory | `updated_input` | Headers stripped; plan reads advisory |
| Kilo | Advisory | `output.args`. The mutation snippet was read from the Context7 index of the vendor page | Headers stripped; plan reads advisory |
| Antigravity | Advisory | Not documented for `hooks.json` [AG-hooks]. Only documented fields are written (D25), so the headers are not stripped | Headers visible; plan reads advisory |
| Factory | Advisory | `updatedInput` | Headers stripped; plan reads advisory |
| Devin | Advisory | `updatedInput` | Headers stripped; plan reads advisory |
| Warp, ZCode | Advisory | Not applicable: no headers are written, because the conductor passes the routing facts to `arbiter_evaluate` | Headers never reach the prompt; plan reads advisory |

- **What "advisory" means.** On project-wide-hook harnesses, a Read event carries no caller identity. A guard there could not tell a worker from a planner, and would block `architect` too. What remains is the conductor's packet instruction, plus `KW-ARB-AUDIT-004` for packets whose body names a planning path.
- **Where a rewrite rides on an `allow` decision,** as Codex documents, Squad already grants the dispatch tool to the four dispatching agents. `allow` therefore adds no authority beyond their rendered permissions.
- **The claim, stated exactly:**
  - Req 25 is enforced on Claude and on Copilot in VS Code.
  - Header stripping is enforced on Copilot CLI, OpenCode, Pi, Codex, Cursor, Kilo, Factory and Devin, where plan reads remain advisory.
  - Antigravity is advisory on both counts.
  - On Warp and ZCode no headers exist to strip, and plan reads are advisory.

## 13. Phase mapping (D28)

| Deliverable | Phase | Reason |
|---|---|---|
| Engine, evaluator, facts, the plan parser with both grammars, ledger and decision log, configuration and validation, all 18 rules, embedded Squad catalog | 1 | All harness-neutral. The Phase 1 harnesses exercise every trigger family |
| `systemone` client, budgets, key resolution with origin binding, credential stores, user override | 1 | Step 1 is harness-neutral (Req 1.2, 17–19) |
| The CLI `arbiter` commands | 1 | Req 2.2. `audit` is needed from the first hooked run (Req 21.3) |
| Gate `applies-when`, `KW-REVIEW-026`, `--base` | 1 | CLI only (Req 12), with no harness dependency |
| `kyber-weave-arbiter hook` with the Claude, Copilot in VS Code, Copilot CLI and OpenCode adapters, the plugin envelope, the Read guard and header stripping | 1 | D28's Phase 1 harnesses, and Req 25 on them |
| Distribution of the third binary (§1.11) | 1 | Phase 1 hooks call it |
| Squad wiring for Claude, Copilot and OpenCode; `squad` CLI plumbing; the `global-scope` degradation | 1 | These targets use frontmatter or their own files only, so the receipt schema does not change |
| Agent and skill contracts (§10.9, Phase 1 rows) | 1 | Every hooked harness depends on the headers, markers and envelope handling |
| ADR 0028 and the Arbiter documentation (`docs/kyber-arbiter/`, the rule reference, configuration) | 1 | Record D1–D34 and the identifiers as shipped. Later phases extend these documents with what they deliver |
| Owned blocks, receipt v3, block drift, ADR 0029 | 2 | Codex and Cursor are the first targets whose hook file is shared (D28) |
| Cursor and Codex adapters and renderers; the Pi renderer, reusing the plugin envelope | 2 | D28 |
| Kilo, Antigravity, Factory and Devin adapters and renderers; the Factory shadowing check | 3 | D28 |
| `serve`, `decision.query` in all ten profiles, MCP grants and renderer filters, the fallback contract text | 3 | Only Warp and ZCode need the fallback, and they are Phase 3. No Phase 1 harness needs it (see below) |

No Phase 1 harness needs the fallback:

- **Claude** documents hooks inside sub-agents [CC-hooks].
- **Copilot in VS Code** documents `.agent.md` hooks that run while the agent is a sub-agent [VS-agents].
- **Copilot CLI's** sub-agent tool calls are hooked, per the closing comment of 2026-08-05 on copilot-cli#3013 [GH-3013].
- **OpenCode** is undocumented on the point, which D7 treats as supported.

A defect that disproves one of these before Phase 3 ships is recorded as `arbiter-not-enforced` for the affected trigger on that harness, until the fallback exists (R2).

**Delivery (D32, D34).**

- Each phase ships as its own PR, with its own verification and its own review.
- The task list covers all three phases, and each phase updates the canonical documentation for what it delivers.
- One closeout after Phase 3 archives the specification.
- Where each phase's PR merges is open (Q16, in the task list).

## 14. Size per area per phase (Req 24)

Estimated changed lines, given as information (Req 24 as amended, D29). Tests are counted against the area they test. Documentation has no tests.

| Area | P1 code | P1 tests | P2 code | P2 tests | P3 code | P3 tests | Total |
|---|---|---|---|---|---|---|---|
| Core engine and providers | 4,600 | 2,950 | 0 | 0 | 150 | 100 | 7,800 |
| Binary | 1,400 | 1,000 | 300 | 350 | 900 | 650 | 4,600 |
| CLI | 1,100 | 800 | 0 | 0 | 0 | 0 | 1,900 |
| Squad integration | 1,150 | 1,000 | 1,650 | 1,300 | 1,000 | 900 | 7,000 |
| Review | 350 | 400 | 0 | 0 | 0 | 0 | 750 |
| Agent text | 500 | 250 | 0 | 0 | 60 | 60 | 870 |
| Distribution | 650 | 350 | 0 | 0 | 0 | 0 | 1,000 |
| Documentation | 1,400 | — | 450 | — | 350 | — | 2,200 |
| **Phase totals** | **11,150** | **6,750** | **2,400** | **1,650** | **2,460** | **1,710** | **26,120** |
| **Phase total, code and tests** | **17,900** | | **4,050** | | **4,170** | | **26,120** |

The Tests area of Req 24 is the sum of the test columns: 6,750, 1,650 and 1,710, which is 10,110.

| Area | Why this size |
|---|---|
| Core engine and providers | Each piece is a separate fact source or step the rules need (Req 1, 9, 17, 18): 18 rules as data (about 450 lines of YAML); ten predicates; two task grammars with three label families; a snapshot-based git reader; a locked ledger and log; three credential stores with origin binding; a budgeted HTTP client. Phase 3 adds the normalisation of `arbiter_evaluate`'s asserted facts |
| Binary | One adapter per harness, because deny shapes, rewrite fields and id fields all differ (§9). Phase 1 also carries the host, the plugin envelope, the Read guard and the strip. Phase 3 adds `serve` |
| CLI | Eight commands (Req 2.2), three of which manage the provider and key (Req 22.3) |
| Squad integration | Phase 1: wiring for three renderers and the `squad` CLI plumbing. Phase 2: owned blocks reach the receipt, the deployment plan, the lifecycle and drift reporting (Req 8). Phase 3: four renderers, plus MCP grants and filters for the two fallback targets |
| Review | Four existing types gain a not-applicable state (§11) |
| Agent text | Contract lines in eleven canonical files, plus one new `architect` reference (§10.9) |
| Distribution | A third binary in every release list: five archives, and 20 → 25 checksummed assets. Also the installer, the self-updater, Homebrew and npm (§1.11) |
| Documentation | Phase 1: ADR 0028, the Arbiter README, architecture and runbook, the rule reference and the configuration page. Phase 2: ADR 0029 and the Squad architecture amendment. Phase 3: the fallback runbook and the remaining harness rows |
| Tests | Every row of the test-first contract. Archived plans are read in place from `docs/archive/plans/` rather than copied as fixtures (4,065 lines for the eight named in §15). The reason is correctness: Req 9.3 names the archive itself as the conformance set, so a later edit that breaks parsing should fail the test |

- **Phase 1 exceeds the review ceiling.** At about 17,900 lines it is over `review.policy.max-reviewable-lines: 10000`, so `review verdict` returns `NEEDS_HUMAN` on size (`KW-REVIEW-009`) as well as on reserved paths (`KW-REVIEW-008`, D13).
- **That escalation is accepted.** D26 accepts it, and D29 rules out cuts made only to meet the size.

## 15. Testing strategy

Test-first applies throughout. The Test contract itself belongs to the tasks phase.

| Area | What the tests establish |
|---|---|
| Engine | Every predicate. First match, and no match as `undecidable`. The effect sets per family (§3). Combination. The step-1 short-circuit. The envelope's bytes for a given input |
| Configuration | Defaults. The limits on overrides. Every `KW-ARB-CONFIG-*` id with its hint. The shipped rules validate with zero errors, and their id set equals §2. The catalog equals the canonical agents' `delegates-to` and descriptions, and every lens's Applicability text |
| Plan parser | Eight archived plans, read in place: `2026-09-29-release-checksums-unsigned-windows`, `2026-09-29-glib-variant-str-iter-backport`, `2026-09-29-mcp-docs-corpus-provenance`, `2026-09-29-pr-158-defects`, `2026-09-28-kyber-utilities-status-line-slice`, `2026-09-28-kyberdash-sea-release-integrity`, `2026-09-28-skill-resource-dispositions`, `2026-09-28-provider-aware-create-pull-request`. Every other archived plan as a no-throw smoke test. Synthetic fixtures for each label variant, each title separator and the spec grammar. A spec task with no files gets `no-task-files` from `SCOPE-001` and `DIFF-001`, and never `undecidable` |
| Readers | In temporary git repositories: committed, staged, unstaged, untracked and renamed paths; the snapshot diff; hunk ranges. Without git, facts are absent and nothing throws |
| Ledger and log | Concurrent appending processes never interleave a line. A trailing partial line is ignored. Pairing works by id and by digest. A sentinel secret appears in no record |
| Provider | Against a stub `HttpMessageHandler`: the request shape per question type; 401 and 422; 429 and 529 retried inside `timeout-ms`; an over-budget state sends nothing; a loopback endpoint sends no `Authorization`; origin binding |
| Credentials | An injected process seam asserts argv and stdin. The Windows store compiles on every OS |
| Evaluator | One provider call per trigger. None after a step-0 escalation. `none` reports not evaluated. An error is `undecidable`. An exception never allows |
| Hook host and adapters | Per-harness payload fixtures built from the §21 vendor pages, each test citing its URL. Stdout holds only the decision document. Malformed input blocks. The fast path loads no configuration |
| CLI | Exit codes and output. `eval` writes nothing |
| Review | Without `--base`, every gate runs. A not-applicable gate is never counted. Old reports read. The verdict is unchanged by not-applicable gates |
| Squad rendering and lifecycle | Per target. `arbiter.enabled: false` renders byte-identically to today. Owned blocks keep the user's entries byte-stable. Receipts without blocks stay v1 or v2 |
| Agent text | `SquadCanonicalContentTests` assertions. The pinned lists are extended |
| Distribution | `ReleaseTests`, `UpdateCommandTests`, the new `DistributionManifestTests`, and `./scripts/update-loop.sh` |
| Not tested | Live harness behaviour. Under D7 a defect is fixed as it is found, and recorded with its harness, version and the documented cell it contradicts (Req 7.3) |

**Run-level verification:**

- `kyber-weave arbiter validate .` on this repository.
- `kyber-weave arbiter plan` over every archived plan.
- `doctor`, run twice: with provider `none`, and against a loopback stub of `/v1/systemone`.
- `squad install --dry-run` in a scratch host with `arbiter.enabled: true`.
- At closeout, `docs validate --merge-ready` and `docs drift`.

## 16. Risks

| Id | Risk | Mitigation |
|---|---|---|
| R1 | Fail-open paths. Copilot fails open on a hook timeout. A Claude hook that crashes without exit code 2 is non-blocking. Cursor fails open unless `failClosed` is set | The top-level catch (D5); `timeout-ms` inside the budget; Cursor's `failClosed`; recording the ledger event first, so `KW-ARB-AUDIT-001` sees it |
| R2 | Support is claimed from documentation alone (D7) | A defect is recorded per Req 7.3. The affected trigger records `arbiter-not-enforced`, and moves to the fallback once Phase 3 ships it |
| R3 | A model answer can be confidently wrong [TS-api] | Step 1 only escalates, skips or verifies, and only at conservative thresholds. It never drops a finding (Req 11.6) |
| R4 | Users edit owned blocks | The receipt digest, and drift reported by `squad status` and `squad doctor` |
| R5 | The `/conductor` skill's hook stays registered for the rest of the session, so later dispatches are classed as the conductor's | Accepted. Squad architecture §3 rejected skill-scoped hooks for the conductor's profile for the same reason |
| R6 | Parallel workers: `DIFF-001` attributes a change to the union of concurrent scopes, not to one worker | `ledger.concurrent.paths`. The envelope's evidence says the attribution is to the union |
| R7 | The Warp and ZCode fallbacks are advisory, because the gated agent makes the call | `arbiter-not-enforced` (`fallback-only`), and `caller-source: asserted` in the log |
| R8 | Escalation churn | The hook-computed `REPEAT`, which stops the task at 2 |
| R9 | Egress: a remote provider receives each step-1 rule's declared `state`, which includes prompts, task text and quoted code. Zero data retention is enterprise-only [TS-legal] | Provider `none` by default, per-rule `state`, and a local provider as the alternative |
| R10 | Model drift | The model is pinned, and the answering model is recorded |
| R11 | Prompt injection through state: the delegation prompt and quoted code are in it. `tev1` is not fully tested for injection [OL-tev1] | Step 1 runs only after step 0 and cannot overturn it (§1.4). State fields are named and kept separate. Thresholds are conservative |
| R12 | `tev1`'s input of about 2,000 tokens puts most delegation and claim checks over budget, so they escalate | The budget check, and `setup` recommending `nimble` (Req 23) |
| R13 | Enforcement by header is weaker: on a project-wide harness, an unmarked conductor dispatch passes ungated | `KW-ARB-AUDIT-002`, which `code-reviewer` cites |
| R14 | Project-wide hooks run in every session, and each dispatch-tool call costs a process start | The fast path returns before any configuration load, and a test asserts it |
| R15 | `code-review` run on the main thread has no per-agent hook on Claude, so its lens spawns are ungated there | Accepted. The lenses still self-skip, as today |
| R16 | Review size: Phase 1 is about 17,900 lines (§14) | D26 accepts `NEEDS_HUMAN` on size |
| R17 | Trust gates leave hooks inert, and headless `claude -p` runs no project sub-agent frontmatter hooks | §10.7 |
| R18 | Factory hook shadowing | §10.8 |
| R19 | The Claude tools reference names the dispatch tool `Agent` [CC-tools]. A community report says matcher `Task` fires and `Agent` does not [CC-95769] | Match exactly with `^(Agent\|Task)$`, so `Task`-prefixed task-list tools do not match. The adapter accepts both names, and passes through untouched any event with no sub-agent target. The matcher is a documented regex field, and nothing is probed (D7) |
| R20 | The Copilot target's `.agent.md` files are read by Copilot CLI too, and the CLI does not document a `hooks` frontmatter key [GH-agents] | Treated as supported under D7, and a rejection is fixed as a defect. The CLI's own gating comes from `.github/hooks` |
| R21 | A repository's configuration could point `provider.endpoint` at another origin to obtain the user's key | Keys are bound to an origin. `TYPESAFE_API_KEY` is used only for TypeSafe or for a user-override origin. A non-loopback `http` endpoint is refused (`KW-ARB-CONFIG-010`) |
| R22 | Extracting paths from prose in plain code cannot tell "edit X" from "read X" or "do not touch X" | Pre-dispatch path checks allow `beyond-files`. Scope is judged on actual writes after the dispatch (`DIFF-001`) |
| R23 | Losing the ledger (a fresh clone, or a cleaned `artifacts/`) leaves completed tasks unrecorded | §8.3 (D31) |
| R24 | The Read guard matches path-valued inputs exactly, but shell commands only by substring, so indirection escapes it | Labelled best effort (§12) |

## 17. Out of scope

- **Per-edit enforcement inside worker sub-agents.** Harnesses do not carry the parent dispatch id into the sub-agent; Claude's `SubagentStart` has no parent `tool_use_id` [CC-hooks].
- **Live probes of the harness matrix** (D7).
- **Automated threshold calibration.** The decision log is its input.
- **Installing or vendoring TypeSafe's skill** (D20). The Arbiter documentation links it [TS-skill] (Req 20).
- **KyberDash ingestion of Arbiter decisions.**
- **Hooks for `--global` installs** (Req 22.4).
- **A managed glossary.** None exists in this repository. *Arbiter* and *JEV* are defined in the Arbiter architecture document instead.
- **Gating `code-review` or `bug-crusher` run on the main thread** (R15).
- **Ledger and log rotation.**
- **Workers running in a separate worktree.** The conductor runs workers in one checkout.

## 18. Code facts this design builds on

All read at `rev=13dcb73`.

| Fact | Consequence |
|---|---|
| `ReviewGateYaml` declares only `Id`, `Run`, `Blocking` and `TimeoutSeconds`. `GateRunner.Run(config, workingDirectory, stopOnBlockingFailure)` takes no changed paths. `GateResult.Passed` is `ExitCode == 0`. `VerdictEngine.EvaluateGates` and `GradeRisk` treat a blocking gate that did not pass as failed | §11 needs a not-applicable state, `--base`, a new outcome id, and tolerant reads |
| The `KW-REVIEW` ids in use are `-001`…`-012`, `-020`…`-025` and `-030`…`-032` | The next free gate id is `KW-REVIEW-026` |
| `ProcessRunner` is argv-only and refuses a shell. The only git call is `git rev-parse` in `CorpusProvenance`, so nothing computes a diff today | Git facts are new code over `ProcessRunner`. The credential-store CLIs use it too |
| `KyberWeaveConfigLoader.FromDocument` merges each section through a `*ConfigLoader.Merge`, and `KyberWeaveConfig.Clone` must learn each section | §7 loading |
| `SquadSourceLoader.ParseCapabilityProfiles` throws when a profile omits a vocabulary capability. There are ten profiles | §10.6 |
| The agents with a non-empty `delegates-to` are conductor, architect, product-owner and code-reviewer. `agent.schema.json` has `additionalProperties: false` | Hooks derive from `delegates-to` and the profile, with no schema change |
| `ClaudeRenderer.ResolveTools` withholds MCP from the orchestrator. MCP grants are whole-server wildcards. Claude hard-codes `StandardMcpTools`; ZCode and Devin enumerate `required-mcp-tools` in `QualifiedMcpToolNames`; Factory emits `mcpServers: []` | A separate server name, and renderer filters that land before the canonical data |
| Receipts own whole files as `SquadOwnedFile(RelativePath, Sha256, Target, Adopted)`. Plans throw `UnmanagedCollision` on an unowned file. `SquadStateStore` uses `UnmappedMemberHandling.Disallow`. Project receipts are v1 and global receipts v2 (ADR 0024) | Receipt v3 is written only when a block exists |
| `SquadRenderRequest` (37 callers) and `SquadRenderResult` are positional records | New members are optional and trailing |
| `SquadDegradationRecord` has `Details`. The receipt's `SquadDegradation(Target, Subject, Code)` does not | The reason lives in `Details` |
| `HotshotGoldenContractTests` pins every agent body except architect, code-reviewer, conductor, product-owner and task-reviewer, and every skill outside its evolved list | §10.9 |
| Release lists its assets explicitly, and the checksum verifier enumerates every one ([ADR 0027](../../adr/0027-release-integrity-checksums-signing-deferred.md)) | §1.11 |
| `OpenAiCompatibleEmbeddingGenerator` takes an injected `HttpMessageHandler` | The provider precedent |
| `artifacts/` is ignored by git here, but not necessarily in a host | `KW-ARB-LOG-001` |
| The `code-review` skill writes `findings.json` at the repository root | §10.9 moves it under `artifacts/` |

## 19. Requirement traceability

| Req | Sections |
|---|---|
| 1 | §1.2, §1.4, §2 |
| 2 | §1.1, §1.7, §1.8 |
| 3 | §1.2, §1.6 |
| 4 | §1.9, §10.6, §13 |
| 5 | §1.7, §8.4 |
| 6 | §4.2, §9, §10, §13 |
| 7 | §9, §15, R2 |
| 8 | §10.4 |
| 9 | §6 |
| 10 | §2, §3 |
| 11 | §1.10, §2 |
| 12 | §11 |
| 13 | §2 notes |
| 14 | §1.4, §2, §7 |
| 15 | §1.10, §3, §8 |
| 16 | Naming throughout |
| 17 | §1.5 |
| 18 | §1.5, R21 |
| 19 | §1.2, §1.5 |
| 20 | §17, §21 (TS-skill) |
| 21 | §1.6, §5 |
| 22 | §1.8, §1.11, §10 |
| 23 | §1.5, §1.8 |
| 24 | §14 |
| 25 | §5, §12 |

## 20. Open questions

None from the design phase. The tasks phase raised Q15 (Claude return observation), Q16 (phase PRs and the open-specification rule) and Q17 (Antigravity return observation). The [task list](tasks.md#pending-decisions) records all three. Q15 and Q17, once answered, amend §9.2, §10.2 and §10.5.

The owner answered every design-phase question on 2026-10-03:

| Question | Answered by | Applied in |
|---|---|---|
| Q12: one PR per phase, or one for all three | D32 | §13 |
| Q13: how spec task artifacts are parsed | D30 | §2, §6 |
| Q14: recovery after ledger loss | D31 | §8.3 |

## 21. Sources

Vendor pages were read on the dates shown. Under D7 they are the support claim, and a page that is silent leaves the capability as supported until a defect says otherwise. Third-party items are labelled, and nothing in this design depends on them alone.

| Key | Source | Read | Supports |
|---|---|---|---|
| TS-api | [TypeSafe API reference](https://docs.typesafe.ai/api.md) | 2026-10-01 | `POST /v1/systemone`, the question types, errors, the 32k window, latency, price, weaknesses |
| TS-conf | [TypeSafe confidence](https://docs.typesafe.ai/confidence.md) | 2026-10-01 | Thresholds scale with the cost of a wrong action and start conservative |
| TS-cascade | [TypeSafe escalation cookbook](https://docs.typesafe.ai/cookbooks/sde_cascade.md) | 2026-10-01 | The three steps, one red flag, the least-certain judgement |
| TS-cite | [TypeSafe citation check](https://docs.typesafe.ai/cookbooks/citation_check.md) | 2026-10-01 | Fabricated, contradicted, unsupported, verified |
| TS-fn | [TypeSafe function calling](https://docs.typesafe.ai/cookbooks/function_calling.md) | 2026-10-01 | Exact lookups stay in code |
| TS-legal | [TypeSafe legal](https://docs.typesafe.ai/legal.md) | 2026-10-01 | No training on customer data; zero data retention is enterprise-only |
| TS-skill | [TypeSafe skill](https://github.com/typesafe-ai/skills/blob/main/skills/typesafe-ai/SKILL.md) | 2026-10-01 | Reference only (Req 20) |
| InfoQ | [InfoQ: TypeSafe JEV released](https://www.infoq.com/news/2026/10/typesafe-ai-jev-released/) | 2026-10-01 | Release context |
| OL-blog | [Ollama: Jev-style decision models](https://ollama.com/blog/ollama-now-supports-jev-style-decision-models) | 2026-10-02 | `/v1/systemone` on Ollama; no key; the 3,880-decision comparison; the calibration caveat |
| OL-0.35 | [Ollama v0.35.0](https://github.com/ollama/ollama/releases/tag/v0.35.0) | 2026-10-02 | Released 2026-09-28 |
| OL-nimble | [nimble](https://ollama.com/library/nimble) | 2026-10-02 | 9B; about 8k tokens per question; 64 KiB body |
| OL-tev1 | [tev1](https://ollama.com/library/tev1) | 2026-10-02 | A context of about 2,000 tokens; not fully tested for injection or calibration |
| CC-hooks | [Claude Code hooks](https://code.claude.com/docs/en/hooks) | 2026-10-03 | Deny shapes; `updatedInput`; `tool_use_id`; `agent_id` and `agent_type` on tool events inside sub-agents; `SubagentStart` has no parent `tool_use_id` |
| CC-sub | [Claude Code sub-agents](https://code.claude.com/docs/en/sub-agents) | 2026-10-03 | Frontmatter hooks run only while that sub-agent is active; the workspace trust dialog; `-p` does not count |
| CC-tools | [Claude Code tools reference](https://code.claude.com/docs/en/tools-reference) | 2026-10-03 | The dispatch tool is named `Agent` |
| CC-95769 | [anthropics/claude-code#95769](https://github.com/anthropics/claude-code/issues/95769) (community) | 2026-10-03 | Matcher `Task` fires and `Agent` does not. Conflicts with CC-tools (R19) |
| GH-hooks-cfg | [Copilot hooks configuration](https://docs.github.com/en/copilot/reference/hooks-configuration) | 2026-10-03 | `.github/hooks/*.json`, all run in alphabetical order; payload fields |
| GH-hooks-ref | [Copilot hooks reference](https://docs.github.com/en/copilot/reference/hooks-reference) | 2026-10-03 | `permissionDecision`; `modifiedArgs`; tool names `task`, `view`, `grep`, `glob`; no tool-call id |
| GH-agents | [Copilot custom agents configuration](https://docs.github.com/en/copilot/reference/custom-agents-configuration) | 2026-10-02 | No per-agent hooks and no caller field |
| GH-2392 | [copilot-cli#2392](https://github.com/github/copilot-cli/issues/2392) | 2026-10-01 | A Copilot CLI hooks issue cited by the 2026-10-01 matrix. Not load-bearing here |
| GH-3013 | [copilot-cli#3013, closing comment](https://github.com/github/copilot-cli/issues/3013#issuecomment-5193501911) | 2026-10-03 | 2026-08-05, by `szabta89`, the GitHub staff engineer assigned to the issue, who also closed it: "preToolUse and postToolUse hooks now apply to sub-agent tool calls". GitHub labels the author association `NONE`, and the comment gives no release number |
| VS-hooks-ref | [VS Code hooks reference](https://code.visualstudio.com/docs/agents/reference/hooks-reference) | 2026-10-03 | `permissionDecision` allow, deny or ask; `updatedInput`; `tool_use_id`; the most restrictive decision wins; tool names undocumented |
| VS-agents | [VS Code custom agents](https://code.visualstudio.com/docs/agent-customization/custom-agents) | 2026-10-03 | `.agent.md` `hooks` (Preview) run when the agent is active, "either invoked by the user or as a subagent"; `chat.useHooks`; trust |
| VS-hooks | [VS Code agent hooks](https://code.visualstudio.com/docs/agent-customization/hooks) | 2026-10-01 | The Local harness ignores matchers and discovers `.github/hooks/*.json` |
| VS-sub | [VS Code subagents](https://code.visualstudio.com/docs/agents/subagents) | 2026-10-02 | `runSubagent` |
| VS-tools | [VS Code tools reference](https://code.visualstudio.com/docs/agents/reference/tools-reference) | 2026-10-03 | Reference names for the read and search tools, which are not hook `tool_name` values |
| CU-hooks | [Cursor hooks](https://cursor.com/docs/agent/hooks) | 2026-10-03 | `.cursor/hooks.json`; `{permission, agent_message}`; `updated_input`; `tool_use_id`; `failClosed` |
| CU-sub | [Cursor subagents](https://cursor.com/docs/agent/subagents) | 2026-10-02 | No per-agent hooks |
| CX-hooks | [Codex hooks](https://learn.chatgpt.com/docs/hooks), redirected from developers.openai.com/codex/hooks | 2026-10-03 | Matcher `spawn_agent` / `Agent`; `permissionDecision`; `updatedInput` with `allow`; `.codex/hooks.json`; the trusted layer and per-hook trust |
| OC-plugins | [OpenCode plugins](https://opencode.ai/docs/plugins/) | 2026-10-03 | `tool.execute.before` mutating `output.args`; a thrown error blocks |
| OC-types | [OpenCode plugin types](https://raw.githubusercontent.com/anomalyco/opencode/dev/packages/plugin/src/index.ts) (source, not docs) | 2026-10-02 | The `tool.execute.before` input has no agent |
| KI-plugins | [Kilo plugins](https://kilo.ai/docs/automate/extending/plugins) | 2026-10-03 | "Plugins can intercept tool calls to mutate arguments". The mutation snippet came from the Context7 index of this page |
| KI-sub | [Kilo custom subagents](https://kilo.ai/docs/customize/custom-subagents) | 2026-10-02 | The Task tool; `permission.task` |
| PI-ext | [Pi extensions](https://pi.dev/docs/latest/extensions), and the [GitHub copy](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/docs/extensions.md) | 2026-10-03 | "`tool_call` can mutate input or block execution"; `{block, reason}`; `toolCallId` |
| PI-cfg | [Pi configuration](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/docs/configuration.md) | 2026-10-02 | `.pi/extensions/` |
| PI-sec | [Pi security](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/docs/security.md) | 2026-10-02 | Project trust |
| PI-sub | [pi-subagents](https://github.com/tintinweb/pi-subagents) | 2026-10-02 | The `Agent` tool |
| AG-hooks | [Antigravity hooks](https://antigravity.google/docs/hooks/) | 2026-10-03 | `.agents/hooks.json` with named groups; `PreToolUse` output limited to `decision`, `reason` and `permissionOverrides`; no rewrite field |
| AG-sub | [Antigravity subagents](https://antigravity.google/docs/subagents/) | 2026-10-02 | `invoke_subagent`; no per-agent hooks |
| AG-sdk | [Antigravity SDK hooks README](https://github.com/google-antigravity/antigravity-sdk-python/blob/main/google/antigravity/hooks/README.md) | 2026-10-03 | `modified_args`, for the SDK only |
| AG-changelog | [Antigravity changelog](https://antigravity.google/docs/changelog/) | 2026-10-03 | An SDK 0.1.13 entry on pre-tool argument modification is listed, but its body could not be read. The wording and date (August 18 or 20, 2026) are unverified. Third-party reports of a CLI `overwrite` field ([antigravity-cli#1053](https://github.com/google-antigravity/antigravity-cli/issues/1053)) are not vendor documentation and are not relied on |
| FA-hooks | [Factory hooks reference](https://docs.factory.com/reference/hooks-reference) | 2026-10-03 | `.factory/hooks.json`; the `settings.json` `hooks` key is read only when that file is absent; `updatedInput` |
| FA-guide | [Factory hooks guide](https://docs.factory.com/cli/configuration/hooks-guide) | 2026-10-02 | `permissionDecision` |
| FA-droids | [Factory custom droids](https://docs.factory.com/cli/configuration/custom-droids) | 2026-10-02 | No per-droid hooks |
| DV-hooks | [Devin hooks overview](https://docs.devin.ai/cli/extensibility/hooks/overview) | 2026-10-03 | "block or rewrite tool calls"; `updatedInput` merged into the arguments |
| DV-life | [Devin lifecycle hooks](https://docs.devin.ai/cli/extensibility/hooks/lifecycle-hooks) | 2026-10-02 | Standalone `.devin/hooks.v1.json`; the `run_subagent` matcher |
| DV-sub | [Devin subagents](https://docs.devin.ai/cli/subagents) | 2026-10-02 | `run_subagent` |
| DV-changelog | [Devin changelog](https://docs.devin.ai/cli/changelog/stable) | 2026-10-02 | `hooks.v1.json` uses the Claude Code format |
| WA-7834 | [Warp #7834](https://github.com/warpdotdev/Warp/issues/7834) | 2026-10-01 | No hooks: a feature request |
| ZC-hooks | [ZCode hooks](https://zcode.z.ai/en/docs/hooks) | 2026-10-01 | Project-level hooks are not executed in the current version |
| ZC-32 | [zai-org/feedback#32](https://github.com/zai-org/feedback/issues/32) | 2026-10-01 | The same |
