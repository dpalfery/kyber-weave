---
id: kyber-arbiter/architecture
title: Kyber Arbiter architecture
doc-type: architecture
component: KyberArbiter
source-root: src/KyberWeave.Core/Arbiter
owner: dpalfery
last-reviewed: 2026-11-10
status: current
decided-by:
  - adr/0053-kyber-arbiter-three-step-decision-gates
  - adr/0054-squad-owned-blocks-in-shared-hook-files
code-refs:
  - ArbiterEvaluator
  - RuleEngine
  - ArbiterRule
  - ArbiterConfig
  - ArbiterConfigLoader
  - PlanDocumentParser
  - TriggerFactBuilder
  - HeaderBlock
  - CallerResolver
  - InFlightLedger
  - DecisionLog
  - SystemOneClient
  - ArbiterKeyResolver
  - ArbiterEscalationEnvelope
  - CodexHookAdapter
  - CursorHookAdapter
  - PluginHookAdapter
  - FactoryHooksShadowing
  - ArbiterTools
  - ArbiterPluginShim
---

# Kyber Arbiter architecture

The Arbiter is a rule engine that gates Squad delegations and review fan-out. It evaluates
each decision in up to three steps inside the hook process, then gets out of the way: step 2
— the reasoning agent — always runs after the block, outside the Arbiter. Decided by
[ADR 0053](../adr/0053-kyber-arbiter-three-step-decision-gates.md).

## Terminology

This repository has no managed glossary, so the two overloaded names are fixed here:

- **Arbiter** — the feature: the rule engine, the `kyber-weave-arbiter` hook binary, and the
  `kyber-weave arbiter` CLI surface. Commands are `kyber-weave arbiter …`, the binary is
  `kyber-weave-arbiter`, rule ids are `KW-ARB-*`, and the configuration section is `arbiter:`.
  The **MCP server key** is a fourth name and the only one that changed: `kw-arbiter`, taken from
  `McpServerNames.Arbiter` in `src/KyberWeave.Core/Configuration/` rather than restated here. A
  key, an executable, and a CLI verb are three contracts, so they do not share a spelling.
- **JEV** — one decision-model provider behind the Arbiter (TypeSafe's cloud `jev-*`
  models), alongside local Ollama decision models (`nimble`, `tev1`). JEV never names the
  feature.

## Reference material

TypeSafe's skill is linked as reference and nothing more: it is not installed, vendored, or
shipped. Whoever implements the provider client or writes model rules installs it at that
time:

- [TypeSafe's skill](https://github.com/typesafe-ai/skills/blob/main/skills/typesafe-ai/SKILL.md)

## The engine

`ArbiterEvaluator` is the single entry point for the hook host and the offline `eval`
command. For each event it appends a ledger event first — so a hook killed mid-evaluation
still leaves an entry for `audit` — then builds the trigger's facts, runs step 0 over every
enabled rule bound to the trigger, makes at most one batched step-1 provider call, combines
the answers, and appends one decision record.

- **Step 0** (`RuleEngine` over `ArbiterRule` `decide` clauses) uses a closed predicate set:
  `all`, `any`, `not`, `exists`, `equals`, `in`, `matches`, `subset-of`, `intersects`,
  `count`. First match wins; no match is `undecidable`. Path predicates match through the
  review engine's `PathGlob`.
- **Step 1** (`SystemOneClient`) carries every enabled step-1 rule of the trigger whose
  `ask.when` holds in one `POST <endpoint>/systemone` call keyed by rule id. It is skipped
  once step 0 has produced the trigger family's strongest effect (`escalate`, `skip`, or
  `verify`): step 1 can add a red flag but never overturns plain code. Over budget, on
  provider error, or on timeout, answers are `undecidable` — and with provider `none`,
  step-1 rules report not evaluated, which is not `undecidable`.
- **Combination.** Any escalating rule escalates the trigger; signals are never averaged.
  `undecidable` escalates on conductor and investigate triggers and allows on review
  triggers. Non-allow conductor outcomes render an `ArbiterEscalationEnvelope` in the style
  of the Squad's status handoffs; review outcomes render skip/verify/annotation notes for
  code-reviewer.

Facts come from `TriggerFactBuilder`: the plan parser over `PLAN_FILE` at evaluation time
(nothing cached or written), git snapshots and diffs, the gate report, the ledger, and the
embedded Squad catalog. `HeaderBlock` parses the closed routing-header set
(`KYBER-ARBITER:`, `PLAN_FILE:`, `TASK:`, `LENS:`, `REFUTE:`); `CallerResolver` ranks the
harness payload above the rendered `--caller`, then header inference. Plan and task identity
come from the headers only — never inferred from the plan index.

`PLAN_FILE:` is a repository-relative path. A value that starts with `/`, contains a
backslash, a space or a tab, has an empty or `..` segment, or appears twice is rejected.
A rejected name goes to `HeaderBlock.RejectedNames` with its value discarded, so a header
that was sent and rejected is distinct from one that was never sent: the planner rule
reads it as `delegation.plan-file-rejected`. A second sighting of a name leaves it absent
whichever sighting was valid. On a planner dispatch, a rejected
`PLAN_FILE:` answers `plan-file-invalid` before any body marker is read, so a marker in
the body cannot rescue it.

The hook route fills the facts step 1 reads. All of it runs in `HookGitFacts`, except the
finding, which `TriggerFactBuilder` reads from the prompt:

- **`review.diff-summary`** is each changed path followed by its hunk headers, measured from
  the same base as `review.changed-paths`. It is never shortened: a summary missing the one
  file that makes a lens apply could skip that lens, so an oversized one goes over budget and
  answers `undecidable` instead. It is absent when nothing changed or the base cannot be read.
- **`finding.*`** on a refutation come from the finding block whose `id` equals the `REFUTE:`
  header. A prompt with no such block leaves them absent.
- **`file.surroundings`** is the 20 lines either side of `finding.line`, from the working tree.
  It is absent when the path falls outside the repository, the file is over 16 MiB, or the
  line is past the end of the file.
- **Lens instructions.** `instructions-from: lens:` sends the whole embedded lens file, with
  the lens named by `lens.name` when the rule names none. An unknown lens sends the rule's
  own question.

Limits on those facts:

- A repository-relative `instructions-from` path has no resolver, so such a rule sends its own
  question.
- `arbiter eval` is an offline dry run with no repository state, so these git and file facts
  stay absent there.
- `review.changed-hunks`, `file.text-at-line` and `gates.report` have no filler yet.

`InFlightLedger` and `DecisionLog` persist `artifacts/arbiter/ledger.jsonl`
(`kyber-arbiter.ledger/v1`) and `artifacts/arbiter/decisions.jsonl`
(`kyber-arbiter.decision/v1`) as append-only lines under one lock file. Only the hook
appends; the CLI only reads. Prompts and code are logged as digests, never in full.
Each step-1 rule entry records what the model scored, so thresholds can be tuned from the log:
`probabilities` (in the rule's answer order; for `noul`, one value for the first answer),
`confidence`, and `threshold`, the floor or ceiling the score was compared against. A score
below its floor is still recorded, beside the `undecidable` it produced. Entries are null when
the answer could not be read, and `probabilities` is null unless every option has a value.

A worker's return reaches the rules through the ledger. The post event records the report's
`RESULT:`, `RED_EVIDENCE:` and `STATUS:` lines, and only a planner return may record
`ATTESTED:`. It pairs with its dispatch by call id, or by a digest of the target and the
prompt with its headers removed, and a specialist's return takes its `TASK` from that dispatch
because its headers were stripped before it ran. On each `delegate`, `ledger.completed-tasks`
is the tasks of this plan whose report read `RESULT: PASS`, `ledger.red-evidence` is present
when this task or one it depends on reported RED evidence, and `ledger.in-flight.paths` is the
files of this session's dispatches that have not returned. A refused dispatch holds no files.
A task counts as complete on its worker's own report: no separate reviewer confirms it.

## The rules

Twenty rules ship with permanent `KW-ARB-*` ids, tuned by id through `enabled`,
thresholds, and per-answer `effects`. Host rules use the same shape under non-`KW-ARB-` ids.

| Rule | Trigger | Step | Question |
|---|---|---|---|
| `KW-ARB-PLAN-001` | `delegate` | 0 | Does `PLAN_FILE` name an artifact with parseable tasks? |
| `KW-ARB-SCOPE-001` | `delegate` | 0 | Does `TASK` name a plan task, and do named paths stay inside its files? |
| `KW-ARB-SCOPE-002` | `delegate` | 1 (`choice`, ≥ 0.8) | Does the delegation ask for work the task does not describe? |
| `KW-ARB-READY-001` | `delegate` | 0 | Are dependencies complete and files clear of work in flight? |
| `KW-ARB-OWNER-001` | `delegate` | 0 | Is the target the named specialist or the file-kind-map owner? |
| `KW-ARB-OWNER-002` | `delegate` | 1 (`choice`, ≥ 0.8) | Which roster specialist owns this task? |
| `KW-ARB-MODE-001` | `delegate` | 0 | In test-first mode, does RED evidence exist first? |
| `KW-ARB-ROSTER-001` | `delegate`, `investigate` | 0 | Is the target in the caller's `delegates-to`? |
| `KW-ARB-PLANREAD-001` | `delegate` | 0 | Does the dispatch send the worker into a plan, specification or todo path? |
| `KW-ARB-DIFF-001` | `delegate.returned` | 0 | Did the delegation change only its task's files? |
| `KW-ARB-GATE-REPORT-001` | `delegate.returned` | 0 | Does the returned digest's `TASK_GATE` line agree with the gate report it names? |
| `KW-ARB-PLANNER-001` | `delegate.planner` | 0 | Is this a recognised planner invocation? |
| `KW-ARB-PLANNER-DIFF-001` | `delegate.planner.returned` | 0 | Did the planner write only plan, spec, and todo directories? |
| `KW-ARB-READONLY-001` | `investigate.returned` | 0 | Did the read-only dispatch leave the tree unchanged? |
| `KW-ARB-LENS-001` | `lens.spawn` | 0, then 1 (`noul`, skip below P 0.1) | Does the lens apply to this change? |
| `KW-ARB-QUOTE-001` | `lens.returned` | 0 | Is each finding's excerpt present at its `file:line`? |
| `KW-ARB-PREEX-001` | `lens.returned` | 0 | Is the excerpt inside a changed hunk? |
| `KW-ARB-CLAIM-001` | `refute.spawn` | 1 (`choice`, ≥ 0.9) | Do the quoted code and surroundings support the claim? |
| `KW-ARB-GATE-CORROBORATED-001` | `refute.spawn` | 0 | Does a failed gate already corroborate the finding? |
| `KW-ARB-GATE-001` | `gate.select` | 0 | Does any changed path match the gate's `applies-when.paths`? |

**The plan-read ban is enforced at dispatch (`KW-ARB-PLANREAD-001`).** Workers treat their dispatch
packet as their whole context and do not open plan or specification files, and until this rule the
only enforcement was a Read guard — advisory wherever a harness's hooks are. On a `delegate` trigger
the rule removes the routing header block, then asks whether what remains names a path under
`config.planning-dirs`; if it does, the dispatch escalates. The header is removed first because that
block names the plan by design, so a rule reading the raw prompt would fire on every dispatch. A
documentation-profile target answers `closeout` and is allowed, because a `docs-dev` dispatch that
names the plan while archiving it is the correct behaviour. The rule shares one detector with the
authoring-time prompt check (`KW-DOC-TASKS-005`), so the two cannot drift apart, and it may not fire
where dispatch hooks are unconfirmed: a harness with no hook runs no rule, and a decision claiming
otherwise would report a check that never executed ([ADR 0053](../adr/0053-kyber-arbiter-three-step-decision-gates.md),
decision 23).

**A claimed gate result is checked against the report it names (`KW-ARB-GATE-REPORT-001`).** A worker
ends its task with a `TASK_GATE:` line naming the report its gate run wrote, and until this rule
nothing read that path at task end: the fact existed and only the review council consumed it. On a
`delegate.returned` trigger the rule reads the named report — an absolute path from a worktree
worker, or one resolved against the event's working directory — and answers three ways. `verified`
means the claim was `PASS` and the report corroborates it: at least one gate passed and none failed.
`disagrees` means the report contradicts the claim, and it includes a claim no report can back — any
status other than `PASS`, and a `PASS` over a run whose every gate was not applicable, because a
skipped gate is evidence neither way (`KW-REVIEW-026`). `missing` means the claim names no readable
report, or the return carries no claim at all. Only `verified` allows; the other two escalate, because
a claim whose evidence cannot be found is exactly the case the rule exists for. The comparison spans
two facts — the claim and the report — which the closed step-0 predicate set cannot express, so
`GateReportFacts` makes it and the rule reads a single boolean. Where a harness cannot observe returns
the post-dispatch hook never fires and the rule cannot run; the end-of-run gates catch that case
instead, and no decision claims otherwise.

**Changed paths are committed plus dirty, and an empty set is not Markdown-only.** `review.changed-paths`
is the review base against the working tree — the committed diff plus the dirty tree, computed by
`GitFacts.GetChangedPathsSinceBase` — not the dirty list alone. Per-task commits are the normal case
in a Squad run, and a dirty-only basis made a committed change invisible, so `KW-ARB-LENS-001` saw an
empty set, treated it as "no code to judge", and skipped `correctness` and `test-adequacy` exactly
when the run started committing each task. The rule now skips those two lenses only when the set is
non-empty **and** positively Markdown-only, so a tree the Arbiter cannot read is judged rather than
skipped (ADR 0053, decision 9). `KW-ARB-PLANNER-DIFF-001` evaluates `git.changed-paths.since-dispatch` on `delegate.planner.returned` — computed by `HookGitFacts` against the pre-dispatch snapshot via `GitFacts.GetChangedPathsSinceSnapshot` and left absent when git cannot observe the repository — so an empty planner diff allows while out-of-scope writes or unobservable git state escalate.

Two answers are return-dependent: on a harness whose adapter declares returns unobservable
(today only Antigravity), `READY-001`'s completion check and `MODE-001`'s RED check answer
`returns-unobservable`, which allows and is logged. The in-flight overlap check finds nothing
there, because a dispatch that never reports back would otherwise hold its files for the
whole session.

Diagnostics are permanent too: `KW-ARB-CONFIG-001`…`-011` and `KW-ARB-INSTALL-001`
for configuration and scope validation, `KW-ARB-KEY-001` for a missing key,
`KW-ARB-LOG-001` for an un-ignored log directory,
`KW-ARB-BIN-001` for a missing binary, `KW-ARB-GUARD-001` for undeclared planning indexes,
`KW-ARB-PARSE-001`/`-002` for plan parsing, `KW-ARB-AUDIT-001`…`-004` for the ledger audit,
and `KW-ARB-HOOK-001` for a fail-closed hook error. See the
[rule reference](../ci-pipelines/rule-reference.md).

## Configuration

The `arbiter:` section is merged by `ArbiterConfigLoader` into the host configuration; with
no section the Arbiter is disabled, the provider is `none`, and all shipped rules are
enabled. A host may tune a shipped rule's `enabled`, confidence/probability threshold, and
`effects` only — anything else is a hint to add a host rule. The per-user
`~/.config/kyber-weave/arbiter.yml` override may hold `provider:` and nothing else. The key
never appears in any configuration file: `ArbiterKeyResolver` reads `TYPESAFE_API_KEY`,
then the OS credential-store entry for the endpoint origin. Full shape and merge rules are
in [configuration](../configuration.md).

The install surface defaults to global scope. `kyber-weave arbiter install --target <t>`
stores the target list in `~/.kyber-weave/kyber-weave.yml` under `arbiter.global.targets`,
while `--project` opts into repository scope and `--path` is valid only with that flag. The
project config still wins when it has `arbiter.enabled: true`: a global runtime hook emits
`deferred-to-project`, leaves the ledger untouched, and the project decision remains the one
that actually gates the dispatch. The user-level check remains the fallback when no project
config is present, and the per-root ledger stays under `~/.kyber-weave/arbiter/projects/<root-key>/`.
Declaring `arbiter.global` inside a repository configuration file is rejected as `KW-ARB-CONFIG-011`.
Passing mutually exclusive scope flags (`--global` and `--project`) or `--path` without
`--project` fails with client-input exit code `2` and `KW-ARB-INSTALL-001`.

## Provider and models

`SystemOneClient` speaks the same `/v1/systemone` API to TypeSafe's cloud, to OpenRouter, and
to a local Ollama 0.35 or later. Provider `none` is the default, and step 1 is then not evaluated.

| Model | Endpoint | Key | Usable input | Accuracy on Ollama's 3,880-decision comparison |
|---|---|---|---|---|
| `jev-1.14.0` (TypeSafe, default) | `https://api.typesafe.ai/v1` | Required (`TYPESAFE_API_KEY` or the credential store) | 32,000 tokens for the state plus the longest question | Not published |
| `jev-1.13.0` (TypeSafe) | `https://api.typesafe.ai/v1` | Required (`TYPESAFE_API_KEY` or the credential store) | 32,000 tokens for the state plus the longest question | 76.0% |
| `typesafe/jev-1.14`, `typesafe/jev-1.13` (OpenRouter) | `https://openrouter.ai/api/v1` | Required (the credential store) | The same budget as the TypeSafe ids | Not published |
| `nimble` (Ollama, 9B) | `http://localhost:11434/v1` | None | About 8,000 tokens per question | 74.8% |
| `tev1` (Ollama, 4B) | `http://localhost:11434/v1` | None | About 2,000 tokens | 73.3% |

`setup` suggests `nimble` when Ollama is detected and warns against `tev1`, whose input is
too small for most shipped rules. The figures are from Ollama's own benchmark, as cited in
[Ollama's Jev-style decision models post](https://ollama.com/blog/ollama-now-supports-jev-style-decision-models).
Each request is budgeted before it is sent. An over-budget state makes no call, and every
step-1 rule on the trigger answers `undecidable`.

### Step-1 threshold measurement

The shipped step-1 thresholds were measured on 2026-11-09 for Jev 1.13 and Jev 1.14. TypeSafe
publishes no accuracy or calibration comparison between the two, so the figures below are
this project's own.

**Method.** 120 invented cases, 30 for each of the four step-1 rules, in
`tests/KyberWeave.Tests/Fixtures/arbiter-calibration/cases.jsonl`. Two Claude models labelled
them independently and agreed on all 120; no person reviewed them. Each case went through the
production request builder, over OpenRouter, as `typesafe/jev-1.13` and `typesafe/jev-1.14`,
in four full runs. A direct TypeSafe run was not made, so OpenRouter and TypeSafe were not
compared.

**Result.** No threshold changed.

- `KW-ARB-SCOPE-002`, `KW-ARB-OWNER-002` and `KW-ARB-CLAIM-001`: no wrong answer cleared its
  floor on either model in any run, so the floors stay at 0.8, 0.8 and 0.9. Accuracy was 29 or
  30 of 30 per rule. Of 30 answers, Jev 1.14 cleared the floor on 24 (`SCOPE`), 26 to 28
  (`OWNER`) and 22 or 23 (`CLAIM`). Jev 1.13 cleared it on 22 or 23, 19 to 21 and 23 to 25.
- `KW-ARB-LENS-001`: the ceiling stays at 0.1. No lens that applies was skipped at 0.1. The
  rule written before the measurement proposed raising it to 0.6 or 0.63, and that was not
  adopted. A repeat run scored an applying lens at 0.55 on Jev 1.14. Without the diff summary,
  every lens scored between 0.12 and 0.45 on both models, so a ceiling of 0.6 would skip all
  of them.

Confidence on one case can differ noticeably between the models (0.89 and 0.67 on the same
case), so a floor set for one model is not assumed to hold for another.

**Model ids.** `tuned-for` is an exact match and holds TypeSafe's `jev-1.14.0` and
`jev-1.13.0`, and OpenRouter's `typesafe/jev-1.14` and `typesafe/jev-1.13`, the ids the
measurement used. Any other `typesafe/` id, including a dated snapshot, warns in `doctor`.
The state budget for a `typesafe/jev-1.*` id is TypeSafe's published limit; OpenRouter's own
limit was not checked, and the measured cases were small. `jev-1.13.0` stays listed, and a configuration pinned to it keeps working with no
`doctor` warning. [TypeSafe says](https://typesafe.ai/blog/jev-1-14) it intends to give
60 days' warning before formally deprecating `jev-1.13.0`, and has set no date. Only a
configuration that names no model moves to the new default.

**Limits.**

- 30 cases per rule is small: one error is about 3%.
- The measurement sent no lens text, and production now sends the whole lens file, so
  `KW-ARB-LENS-001`'s ceiling of 0.1 has not been measured against what it now reads. The
  harness now sends that text too, so a repeat run measures it.
- `jev-1.13-free` does not appear in TypeSafe's models page, changelog or Jev 1.14 post.

To repeat the measurement, set `KW_ARBITER_CALIBRATE_OUT` to a new output file and run
`ArbiterCalibrationTests`. It makes paid calls, about one cent per run. By default it calls
TypeSafe directly with `jev-1.13.0` and `jev-1.14.0`. To repeat the runs reported here, also
set `KW_ARBITER_CALIBRATE_ENDPOINT` to `https://openrouter.ai/api/v1` and
`KW_ARBITER_CALIBRATE_MODELS` to `typesafe/jev-1.13,typesafe/jev-1.14`.

## Harness facts

One table covers every harness at project scope. Phase 1 hooks Claude, Copilot in VS Code, Copilot CLI and
OpenCode; Phase 2 adds Pi, Codex and Cursor; Phase 3 adds Kilo, Antigravity, Factory and
Devin, and leaves Warp and ZCode fallback-only at project scope, served by [`serve`](#the-mcp-fallback-serve).
The degradation records these outcomes produce are listed once, in the
[degradation taxonomy](../kyber-squad/requirements.md#degradation-taxonomy).

| Harness | Phase | What Squad renders | Dispatch tool | Caller identity | Target and prompt |
|---|---|---|---|---|---|
| Claude | 1 | `hooks` in each dispatching agent's `.claude/agents/<agent>.md` frontmatter and the `/conductor` entry-point skill | `Agent`, matched exactly so task-list tools do not match | `agent_type` inside sub-agents, rendered `--caller` otherwise | `subagent_type`, `prompt` |
| Copilot in VS Code | 1 | `hooks` in each `.github/agents/<agent>.agent.md` (Preview, Local harness) | `runSubagent` on agent-file hooks; the 2026-11-10 runtime reproduction sent `task` in the owned file's CLI shape | Rendered `--caller` on agent-file hooks; the payload carries no caller field | `agentName` (absent means the calling agent), `prompt`; the CLI shape carries `agent_type`, `prompt` |
| Copilot in VS Code, skill entry | 1 | Nothing of its own: the `/conductor` skill has no `hooks` key, so the owned `.github/hooks/kyber-arbiter.json` (or the user-scope `~/.copilot/hooks/kyber-arbiter.json`) gates it | `task` (CLI shape, observed 2026-11-10) | None. The skill carries no caller identity, so only the marker and header inference apply (`skill-entry-no-caller`) | `agent_type`, `prompt` |
| Copilot CLI | 1 | `.github/hooks/kyber-arbiter.json`, its own file | `task` | Header inference from the marker | `agent_type`, `prompt` |
| OpenCode | 1 | `.opencode/plugins/kyber-arbiter.ts` shim speaking the plugin envelope | `task` | Header inference from the marker | `args.subagent_type`, `args.prompt` |
| Pi | 2 | Owned extension file `.pi/extensions/kyber-arbiter.ts` speaking the plugin envelope (`before`/`after`, tool `Agent`) | `Agent` | Header inference from the marker | `args.subagent_type`, `args.prompt` |
| Codex | 2 | Owned block in the shared `.codex/hooks.json` (`PreToolUse`/`PostToolUse` matcher groups) | `spawn_agent` (also matched as `Agent`, under any namespace prefix) | Header inference from the marker | `tool_input.agent_type`, `tool_input.message`; returns pair by `tool_use_id` |
| Cursor | 2 | Owned entries in the shared `.cursor/hooks.json` (`preToolUse`/`postToolUse`, `failClosed: true`) | `Task` | Header inference from the marker | `tool_input.prompt`; `tool_input.subagent_type` is undocumented, so when absent the rules answer `undecidable`. Returns pair by `tool_use_id` |
| Kilo | 3 | The `.kilo/plugin/kyber-arbiter.ts` shim speaking the plugin envelope | `task` | Header inference from the marker | `args.subagent_type`, `args.prompt`; returns pair by `callID` |
| Antigravity | 3 | An owned top-level group `kyber-arbiter` in `.agents/hooks.json`, matching `^invoke_subagent$` | `invoke_subagent`; each element of `Subagents` is one dispatch | Header inference from the marker | `TypeName`, `Prompt`. Deny writes `{decision: "deny", reason}`; pass-through writes `{}`, since an explicit allow would bypass the user's approval prompts |
| Factory | 3 | An owned block in `.factory/hooks.json`, matcher groups at the top level with no `hooks` wrapper, plus a `SubagentStop` group with no matcher (Factory runs `Task` in the background, so the worker report arrives at stop, not at `PostToolUse`), unless shadowed (below) | `Task` | Header inference from the marker | `tool_input.subagent_type`, `tool_input.prompt`; returns pair by `pair-digest` |
| Devin | 3 | An owned block in `.devin/hooks.v1.json` (the Claude Code format), matching `^run_subagent$` | `run_subagent` | Header inference from the marker | `tool_input.profile`, `tool_input.prompt`; returns pair by `pair-digest` |
| Warp, ZCode | 3 | No hooks: the MCP fallback | — | Asserted by the caller in `arbiter_evaluate_agent_dispatch_or_return` | — |

Notes on the table:

On Claude, and on an agent-file dispatch in Copilot in VS Code, the caller is trusted, so gating does not depend on the
marker — though Squad agents still write `KYBER-ARBITER: true` on every hooked dispatch,
and `audit` reports a missing one. Hooks render at user scope by default (recording installed
targets in `arbiter.global.targets`), or at project scope when `--project` is passed and
`arbiter.enabled: true`. Project scope overrides global scope at runtime: when a project's
`.kyber-weave/kyber-weave.yml` has `arbiter.enabled: true`, the global hook logs
`deferred-to-project` and skips evaluation. Trust gates (Claude's workspace dialog, VS Code's
trusted workspace plus `chat.useHooks`) are surfaced at install time — see the
[runbook](runbook.md) and [Squad onboarding](../kyber-squad/onboarding.md).

On the Copilot CLI target post-dispatch outcomes are advisory only: `Deny`,
`PostBlock` and `PostAnnotation` all render as `additionalContext`, so a post-dispatch
finding is surfaced as context rather than enforced. Pre-dispatch denies still block.

### Delivery and caller notes

A conductor that VS Code starts as the `/conductor` skill
([ADR 0066](../adr/0066-copilot-parent-roles-start-as-skills.md)) has no caller identity, and it
has no hooks of its own. Its dispatches are gated only by the owned Copilot hook file, and the
rendered parent records `arbiter-not-enforced` with the reason `skill-entry-no-caller`. The
2026-11-10 reproduction ran through the Copilot runtime bundled with VS Code, not the chat window,
and it saw the dispatch as `task` in the CLI payload shape, which the owned file's adapter already
handles. Whether the chat window delivers the event is not yet verified. The gate needs
`chat.useHooks` on and a trusted workspace.

Phase 2 and Phase 3 targets are project-wide hooks with no caller identity. They gate only
dispatches carrying the `KYBER-ARBITER: true` marker, infer the caller from the headers, and
pass anything else through, logged with `caller: unidentified`.

Caller inference from a planner target is one of those gates, and it applies only to a
marked dispatch or on a trusted harness (`claude`, `copilot-vscode`). So an unmarked
dispatch to a planner passes on `factory` and `antigravity`, and `KW-ARB-AUDIT-002`
reports it. An unmarked dispatch that carries `PLAN_FILE:` or `TASK:`, or any header that
was rejected, still infers the conductor and stays gated. The cost is that a conductor which drops its marker goes
ungated on those harnesses. That is the accepted risk the Arbiter design records, and the
audit rule is how it is seen.

Delivery differs per harness. On Pi a `block` before the call returns
`{block: true, reason}`, stripped arguments are assigned onto the event input, and a
spawn error or non-zero status blocks; after the call a `block` appends the reason as
text content. On Codex a deny writes the `hookSpecificOutput` deny shape with the
envelope as the reason, and a strip writes `allow` with `updatedInput` carrying the
complete arguments with the routing headers removed from `message`. On Cursor every
invocation answers JSON — deny as `{permission: "deny", agent_message, user_message}`,
pass as `{permission: "allow"}`, strip as `{permission: "allow", updated_input}`, and
post-dispatch as `{additional_context}` (or `{}`) — because `failClosed` blocks when
there is no output.

On Codex, Copilot CLI and Cursor, post-dispatch denials are advisory only
(`additionalContext` on the first two, `additional_context` on Cursor): on Codex a `decision: block` would replace the sub-agent's
result, so every post-dispatch outcome — deny, block, or annotation — is delivered
as `hookSpecificOutput.additionalContext`. Pre-dispatch denies still block on all
three targets, and `arbiter doctor` prints an informational line for each.

Trust steps for the new targets are surfaced at install time: Codex needs a trusted
`.codex/` layer plus per-hook review through `/hooks` (every `squad update` that
changes the hook needs trust again, or the changed hook is skipped); Pi needs
project trust so that `.pi/extensions/` loads. Cursor's hooks carry no trust gate
today. See the [runbook](runbook.md) and
[Squad onboarding](../kyber-squad/onboarding.md).

Claude observes background dispatches through a hand-back hook. Every agent a governed
dispatcher can target gets a frontmatter `PreToolUse` hook on `^SubagentHandback$`: it
records the return under the payload's `agent_id`, joined to the dispatch through the
`agentId` the dispatcher's `PostToolUse(Agent)` reports at the background launch, and then
runs the post-dispatch rules. A non-allow outcome never denies the hand-back — it returns
`allow` with `updatedInput`, the complete input with the envelope or note appended to
`message`, so the dispatcher receives the outcome inside the hand-back. Foreground calls
keep the `PostToolUse` path, and a return observed by neither is reported by `audit` as an
unpaired event (`KW-ARB-AUDIT-003`).

### Phase 3 notes

Per-harness notes:

- **Kilo** is a plugin harness like OpenCode: the rendered shim spawns the binary by argv,
  writes a `kyber-arbiter.plugin-event/v1` envelope, blocks by throwing, and strips the
  routing headers by assigning `output.args`. The target comes from `args.subagent_type`
  and the prompt from `args.prompt`; returns pair by `callID`. No trust gate is documented.
  Both harnesses' loaders read only the renderer's default export: OpenCode v2 accepts
  `{ id, setup }` while Kilo's fork accepts `{ id, server }`, so the shared generator
  parameterizes the default export by harness token (`ArbiterPluginShim.Render`) and
  keeps the named `KyberArbiter` export alongside it. A named-only render fails OpenCode
  v2's loader with `SchemaError` and the plugin silently never loads.
- **Antigravity** can dispatch several subagents in one call (`toolCall.args.Subagents`,
  each element with its own target and prompt); the call is denied when any element
  escalates. It documents no input-rewrite field for `hooks.json`, so routing headers are
  not stripped — only documented fields are written. Because `PostToolUse` output is `{}`,
  the adapter declares returns unobservable (`harness.observes-returns` is false):
  `READY-001`'s completion check and `MODE-001`'s RED check answer `returns-unobservable`,
  which allows and is logged, and the in-flight overlap check finds nothing because no
  dispatch ever closes — the other pre-dispatch gating (plan, scope, intent, ownership) is
  unaffected, and completion order is advisory there. The render records `arbiter-not-enforced` with
  `no-post-dispatch-feedback` for every rendered agent: post-dispatch outcomes are logged
  and reported by `audit`, never delivered back to the harness.
- **Factory** reads the `hooks` key of `.factory/settings.json` only while
  `.factory/hooks.json` is absent, so creating `hooks.json` would silently disable any
  hooks the user keeps in `settings.json`. When that shape is found the renderer drops its
  block and records `arbiter-not-enforced` with `settings-hooks-shadowed`, naming the fix:
  move the user's hooks into `.factory/hooks.json`, then run `squad update`. A strip writes
  `allow` with the complete `updatedInput` (every `tool_input` field copied, only `prompt`
  rewritten); no call id is documented, so returns pair by `pair-digest`. `Task` runs in the
  background by default, so `PostToolUse` fires at launch with no report and records nothing.
  The return is recorded on `SubagentStop`, which carries the report but names the task only
  by its description, so the adapter reads the target and prompt from the parent session's
  transcript. `SubagentStop` output reaches no conductor, so a non-allow outcome there is
  logged and reported by `audit`.
- **Devin** takes the target from `tool_input.profile` and the prompt from
  `tool_input.prompt`; when either is absent the dispatch is treated as unmarked and
  logged, so `audit` reports it. A strip writes `hookSpecificOutput.updatedInput:
  {prompt: …}`, which Devin merges into the arguments. Post-dispatch outcomes are logged
  and reported by `audit`, never delivered, so the render records `arbiter-not-enforced`
  with `no-post-dispatch-feedback` for every dispatcher agent.

On Factory, as on Codex, Copilot CLI and Cursor, post-dispatch denials are advisory only:
every post-dispatch outcome renders as `hookSpecificOutput.additionalContext`, so a
post-dispatch finding is surfaced as context rather than enforced. Pre-dispatch denies
still block. On Antigravity and Devin, post-dispatch outcomes are not delivered at all —
they are recorded and reported by `audit`. No trust gate is documented for any Phase 3
harness.

### Verified global-hook matrix

Research verified against vendor documentation governs the user-level global hook installation for each of the thirteen harnesses. Tiers distinguish enforced global hooks, project-only hooks, partial coverage, and MCP fallback (`kyber-weave-arbiter serve`).

| Harness | Global hook location (verified) | Owned artifact | Layers merge or override? | Block-on-dispatch / trust notes | Tier and group | Evidence |
|---|---|---|---|---|---|---|
| Claude Code | `~/.claude/settings.json` (user-owned settings) | owned marked block inside the user's settings file | project `.claude/settings.json` also runs, but waits on workspace trust | `preToolUse` deny: yes | enforced-global | [Claude settings](https://code.claude.com/docs/en/settings.md) · [Claude hooks](https://code.claude.com/docs/en/hooks.md) |
| Copilot CLI and Copilot in VS Code | `~/.copilot/hooks/kyber-arbiter.json` (or `$COPILOT_HOME/hooks`); JSON `version: 1` | owned file `~/.copilot/hooks/kyber-arbiter.json` | sources policy > user > project > plugins all run | `preToolUse` deny: yes; VS Code requires `chat.useHooks` and a trusted workspace (Preview). VS Code agent-file dispatch (`runSubagent`) unconfirmed by name → degraded `dispatch-unconfirmed`; the 2026-11-10 runtime reproduction sent a skill-started conductor's dispatch to the owned file as `task` in the CLI shape, with no caller identity | enforced-global (CLI); enforced-global, degraded (VS Code) | [Copilot hooks reference](https://docs.github.com/en/copilot/reference/hooks-reference) · [VS Code agent hooks](https://code.visualstudio.com/docs/agent-customization/hooks) |
| Codex | `~/.codex/hooks.json` or `~/.codex/config.toml` | owned block in `~/.codex/hooks.json` | all sources load, none replaces | one-time user trust review required; `[features] hooks=false` disables; blocking applies to tool hooks | enforced-global | [Codex hooks](https://developers.openai.com/codex/hooks) |
| Cursor | `~/.cursor/hooks.json` | owned entries in `~/.cursor/hooks.json` | auto-reloads; global-vs-project precedence unstated | exit code 2 blocks; `preToolUse` supported | enforced-global | [Cursor hooks](https://cursor.com/docs/hooks) |
| Antigravity | `~/.gemini/config/hooks.json`; project `.agents/hooks.json` | owned group in `~/.gemini/config/hooks.json` | no merge policy documented | `PreToolUse` decision `deny` | enforced-global | [Antigravity hooks](https://antigravity.google/docs/hooks.md) |
| Factory | `~/.factory/hooks.json`; project `.factory/hooks.json` | owned block in `~/.factory/hooks.json` | `settings.json` `hooks` key read only if `hooks.json` is absent; existing renderer shadow handling preserved | per existing Factory hook renderer | enforced-global | [Factory hooks guide](https://docs.factory.ai/cli/configuration/hooks-guide) |
| Devin | `~/.config/devin/config.json` (also reads Claude-compatible config) | owned block in the Devin global config | all sources load | block via decision / exit 2 | enforced-global | [Devin hooks](https://docs.devin.ai/cli/extensibility/hooks/overview) |
| ZCode | global-only `~/.zcode/cli/config.json` with `hooks.enabled: true`; project hooks ignored | hooks are not rendered at project or global scope without manual harness flag enablement; falls back to `kyber-weave-arbiter serve` | n/a (global only) | `PreToolUse` deny: yes; hooks require manual `hooks.enabled: true` in harness config | fallback-only (degraded with fallback) | [ZCode hooks](https://zcode.z.ai/en/docs/hooks) |
| CommandCode | `cmd.hooks({ beforeToolCall })` mods vs `~/.commandcode/settings.json` | owned file or owned block only | n/a | design check evaluated mods vs settings; block-on-dispatch unproven, so degraded with recorded reason `dispatch-unconfirmed` at both scopes; next step register `kyber-weave-arbiter serve` in CommandCode's MCP configuration | degraded (`dispatch-unconfirmed`) | [CommandCode settings](https://commandcode.ai/docs/settings) · [CommandCode mods](https://commandcode.ai/docs/mods) |
| OpenCode | `~/.config/opencode/plugins/` (auto-loaded) | owned plugin file `~/.config/opencode/plugins/kyber-arbiter.ts` (ADR 0026 amendment) | arrays merge, none replace | before-hook blocks by throwing; `task` dispatch unconfirmed by name → degraded `dispatch-unconfirmed` | enforced-global, degraded | [OpenCode plugins](https://opencode.ai/v2/docs/plugins) |
| Kilo | `~/.config/kilo/plugin/` (auto-registered) | owned plugin file `~/.config/kilo/plugin/kyber-arbiter.ts` (ADR 0026 amendment) | order: builtins > global config > global plugin dir > project config > project plugin dir | block by throwing; `KILO_PURE=1` skips external plugins; `task` dispatch unconfirmed → degraded `dispatch-unconfirmed` | enforced-global, degraded | [Kilo plugins](https://kilo.ai/docs/automate/extending/plugins) |
| Pi | `~/.pi/agent/extensions/` | owned extension file `~/.pi/agent/extensions/kyber-arbiter.ts` under `SquadGlobalRoots` (`~/.pi/agent`) rendered only from the Pi body (`spawnSync` + `pi.on`), never `ArbiterPluginShim` | n/a | extension loader (`loader.js:486`) requires bare-function default export; tier partial until block-on-dispatch is proven, MCP fallback otherwise | partial | [Pi coding agent](https://github.com/gotgenes/pi-coding-agent) |
| Warp | none — no hooks | nothing written; user registers `kyber-weave-arbiter serve` in `~/.warp/.mcp.json` | n/a | MCP fallback only | fallback-only | [Warp documentation](https://docs.warp.dev) |

### Install output table

`kyber-weave arbiter install` prints a structured table for every target:
- **Tier**: `enforced-global`, `enforced-project`, `partial`, or `fallback-only`.
- **Written path & artifact**: the exact filesystem path and artifact type (own file, owned block, plugin file, or nothing).
- **Reason & next step**: for unenforced or degraded targets, the reason code (`fallback-only`, `project-only`, `dispatch-unconfirmed`, `hooks-disabled-by-harness`) and actionable next step (e.g. register `kyber-weave-arbiter serve` in MCP config).
- **Off-switches & trust steps**: harness-side trust requirements (e.g. Codex `/hooks` trust review, `[features] hooks=false`; Kilo `KILO_PURE=1`; VS Code `chat.useHooks` and trusted workspace; ZCode `hooks.enabled: true`; Claude workspace trust dialog).

`--dry-run` prints the identical table with "would write" and makes zero changes on disk.

## The MCP fallback (`serve`)

`kyber-weave-arbiter serve --repo-root <root>` is a stdio MCP server carrying the fallback
for harnesses without hooks or with degraded dispatch (such as Warp, CommandCode, or ZCode). Its server key is **`kw-arbiter`**
(`McpServerNames.Arbiter`), which is the name a client configuration declares and the name a
grant is built from; the executable and its argv are unchanged. Three tools:

- **`arbiter_evaluate_agent_dispatch_or_return(trigger, facts)`** — evaluates one event exactly as
  the hook would: the same facts, rules, ledger append and decision record, and the same allow,
  envelope or review note. The caller asserts the routing facts (`target`, `plan-file`, `task`,
  `prompt`, `lens`, `finding`, `phase`, `call-id`, `output`); the server derives the rest
  and records `caller-source: asserted`. The asserted `plan-file` is rendered as a
  `PLAN_FILE:` header, so the repository-relative rule and the `plan-file-invalid` answer
  apply to it as they do to a hook dispatch. Because the gated agent makes the call, this path
  is advisory.
- **`arbiter_list_effective_rules_by_trigger(trigger?)`** — lists the rule catalogue; read-only
  and closed-world.
- **`arbiter_check_configuration_and_key_readiness()`** — reports the root, the rule-set hash,
  the rule count and the provider state, never the key; read-only and closed-world.

Every response leads with a provenance line naming the root it answered from; the root is
resolved from `--repo-root`, then `KYBER_WEAVE_REPO_ROOT`, then the working directory. The
conductor calls `arbiter_evaluate_agent_dispatch_or_return` before each dispatch and after each
return, and
code-reviewer calls it once before the lens fan-out and once before the refutation
fan-out. Holding `arbiter_evaluate_agent_dispatch_or_return` is the agent's signal to use the
fallback: the
`decision.query` capability is allowed only for the `orchestrator` and `reviewer` profiles
and denied for the other ten, and on Warp and ZCode an `allow` lowers to the
`kw-arbiter` server alone. The setup step — Squad writes no MCP configuration for
either target — is in the [runbook](runbook.md#the-mcp-fallback-warp-commandcode-and-project-scope-zcode).

## Related

- [Runbook](runbook.md) — operating the Arbiter day to day
- [ADR 0053](../adr/0053-kyber-arbiter-three-step-decision-gates.md) — the decisions
- [ADR 0054](../adr/0054-squad-owned-blocks-in-shared-hook-files.md) — owned entries in shared hook files
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — hook wiring per harness
- [Review council architecture](../code-review/architecture.md) — review triggers
- [Configuration](../configuration.md) — `arbiter:` and `applies-when`
