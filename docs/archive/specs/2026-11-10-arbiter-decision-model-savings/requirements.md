---
id: specs/2026-11-10-arbiter-decision-model-savings/requirements
title: Arbiter decision-model savings requirements
doc-type: requirements
status: archived
owner: dpalfery
last-reviewed: 2026-11-11
component: KyberArbiter
documentation-impact: extends-component
impact-components: [KyberArbiter, KyberSquad, ReviewCouncil]
keywords:
  - arbiter
  - decision model
  - model targeting
  - retarget
  - shadow mode
  - nitpick filter
  - duplicate findings
  - spawn savings
---

# Requirements Document

**Status:** Archived 2026-11-11. Closeout mapping: [README](README.md#closeout-mapping).

**Phase status:** Approved

Authored 2026-11-10 under autonomy `autonomous` and development mode `test-first`. Intake chose a
specification (architect recommendation, accepted autonomously). Approved 2026-11-10 by the
conductor's `AUTONOMOUS_APPROVAL: requirements-approval` (D13).

Amended 2026-11-11 by owner decision D55 ([tasks](tasks.md#decisions)): Requirement 20 now covers
bot-authored PR-level summary comments as well as inline comments (criteria 20.1, 20.4, 20.5
reworded; 20.8–20.13 added). The amended text is the owner's own correction; the design and tasks
that implement it (D56–D61) await approval.

## Introduction

The Kyber Arbiter is the rule engine that gates Squad dispatches and review fan-out from harness
hooks ([architecture](../../kyber-arbiter/architecture.md),
[ADR 0053](../../adr/0053-kyber-arbiter-three-step-decision-gates.md)). Step 0 answers exact
facts in plain code; step 1 asks a decision model (TypeSafe JEV or local Ollama) about meaning;
step 2, a reasoning agent, runs after a block, outside the Arbiter. Four step-1 rules ship today
(`KW-ARB-SCOPE-002`, `KW-ARB-OWNER-002`, `KW-ARB-LENS-001`, `KW-ARB-CLAIM-001`), and the effect set
is closed: `allow`, `escalate`, `skip`, `verify`, `annotate` (plus `applies` and `not-applicable`
for gate selection).

A Squad run still spawns agents whose answer a decision model could give for a fraction of the
cost, and runs every implementation task on the model its agent was rendered with. This
specification adds:

- a **foundation** of shared triggers, facts and effects, the missing `review.changed-hunks`
  filler, hunk-level lens skipping, and a threshold-tuning report;
- **model targeting**: a judgement of how hard each implementation task is, mapped to a semantic
  model profile and written into the dispatch where the harness allows it, shadow-only by default;
- **eight spawn-removing judgements**: nitpick findings, duplicate findings, trimmed worker reads,
  bug triage, plan-or-spec intake, documentation impact, repeated failures, and bot review
  comments.

Terms used throughout:

- **Semantic model profile** — one of `architect`, `deep-planning`, `fast`, `general`,
  `orchestration`, `reviewer` ([model profiles](../../kyber-squad/model-profiles.md)). A
  profile resolves to a per-harness **model token**; a user **preset** overrides the bundled token.
- **Fail safe** — when a judgement is undecidable, below its threshold, over budget, on a provider
  error, or the provider is `none`, the run behaves exactly as it does today.
- **Shadow mode** — the rule is evaluated and its answer logged, but nothing about the dispatch or
  review changes.
- **Hooked harness** — a harness whose dispatch hook the Arbiter's harness facts confirm. The MCP
  fallback (`kyber-weave-arbiter serve`) is advisory, as today.

## Requirements

### Requirement 1: Shared effects, triggers and rule identity

**User Story:** As an Arbiter maintainer, I want the new judgements built on a small, documented
set of new effects and triggers, so that the closed rule model stays closed and auditable.

#### Acceptance Criteria

1.1. WHEN a judgement in this specification needs an outcome the existing effect set cannot
express THEN the Arbiter SHALL add a named effect to the closed set, bound to the trigger families
that may use it, with its combination rule against the family's existing effects defined.

1.2. WHEN a judgement needs a decision point no existing trigger covers THEN the Arbiter SHALL add
a named trigger mapped to exactly one family, with that family's fixed `undecidable` mapping.

1.3. WHEN any rule on the same trigger firing answers `escalate` THEN no new effect on that firing
SHALL change the dispatch: escalation still wins.

1.4. WHEN a new effect or trigger is added THEN ADR 0053 SHALL be amended in place to state it
(the repository's ADR convention: one record per subject, edited to say what is true now).

1.5. WHEN a new rule ships THEN it SHALL carry a new permanent `KW-ARB-*` id never used before,
and the [rule reference](../../ci-pipelines/rule-reference.md) SHALL list it with its trigger,
step, question, answers and effects.

1.6. WHEN a new diagnostic is needed THEN it SHALL take a new permanent id; no existing id is
reused or renumbered.

1.7. WHEN `kyber-weave arbiter rules` lists the catalogue THEN every new rule SHALL appear with
its trigger, step, answers, effects and the facts it reads.

### Requirement 2: Changed-hunks fact

**User Story:** As a rule author, I want the declared `review.changed-hunks` fact filled, so that
rules can judge what changed inside each file rather than only which files changed.

#### Acceptance Criteria

2.1. WHEN a review trigger fires on the hook route THEN the Arbiter SHALL fill
`review.changed-hunks` with each changed path's hunks measured from the same base as
`review.changed-paths` (committed plus dirty).

2.2. IF the base cannot be read or nothing changed THEN `review.changed-hunks` SHALL be absent,
and a rule reading it SHALL answer as for a missing fact, never as "no changes".

2.3. WHEN `review.changed-hunks` exceeds the step-1 request budget THEN the fact SHALL NOT be
shortened; the request goes over budget and its rules answer `undecidable`.

2.4. WHEN `arbiter eval` runs offline THEN the fact SHALL stay absent, as other git facts do.

### Requirement 3: Hunk-level lens skipping

**User Story:** As a code-reviewer, I want lenses skipped when a change is only comments or only
file renames, so that the council does not spawn lens seats that cannot find anything.

#### Acceptance Criteria

3.1. WHEN every changed hunk in a code file changes only comment lines or whitespace, judged by a
per-language comment syntax the Arbiter knows, THEN step 0 SHALL be able to answer that the file
is comment-only.

3.2. WHEN a changed path is a git-detected rename with identical content THEN step 0 SHALL be able
to answer that the path is rename-only.

3.3. WHEN every changed path is comment-only, rename-only or Markdown THEN the lenses the shipped
rule names SHALL be skipped with a review note naming the rule and the reason.

3.4. IF a file's language has no known comment syntax, the hunks are absent, or any hunk changes a
non-comment line THEN the file SHALL NOT count as comment-only, and the lens SHALL run.

3.5. WHEN this skip is decided THEN no decision model SHALL be called for it (step 0 only).

### Requirement 4: Threshold tuning from the decision log

**User Story:** As a repository owner, I want a report that shows how step-1 scores fell against
their thresholds, so that I can tune a threshold such as `KW-ARB-LENS-001`'s from real runs.

#### Acceptance Criteria

4.1. WHEN the owner runs the tuning report THEN the CLI SHALL read `decisions.jsonl` and, per
step-1 rule, report the count of scored answers, their score distribution, and how many answers
would change at candidate thresholds.

4.2. WHEN a decision has a known outcome joined from the ledger (for example a task's later
`RESULT:` and `TASK_GATE` line) THEN the report SHALL show it beside the score.

4.3. THEN the report SHALL only read: it never writes configuration. Applying a threshold stays the
existing shipped-rule override (`confidence-at-least` / `probability-below`), which already
accepts `KW-ARB-LENS-001`.

4.4. WHEN the decision log is missing or holds no scored answers THEN the report SHALL say so and
exit successfully.

### Requirement 5: Shared safety contract for every new judgement

**User Story:** As a repository owner, I want every new judgement to fail safe and to be visible,
so that a wrong or missing model answer never removes work that was needed.

#### Acceptance Criteria

5.1. IF a new step-1 judgement is undecidable, below its threshold, over budget, fails at the
provider, or the provider is `none` THEN the run SHALL do what it does today: spawn the agent, keep
the finding, carry the full documents, or keep the model.

5.2. WHEN a harness has no confirmed dispatch hook and no MCP fallback call is made THEN no new rule
SHALL run, and no decision record SHALL claim it ran.

5.3. WHEN a new rule runs through the MCP fallback THEN its outcome SHALL carry the existing
advisory label, and a rule that needs an input rewrite SHALL only record what it would have done.

5.4. THEN every new step-1 rule SHALL support shadow mode, settable per rule through the
`arbiter:` configuration; in shadow mode it is evaluated and logged and changes nothing.

5.5. WHEN a new step-1 rule ships THEN it SHALL default to shadow mode until the owner enables it
(see decision D7).

5.6. WHEN a new rule answers THEN its decision entry SHALL use the existing entry shape
(`probabilities`, `confidence`, `threshold`, `effect`, `facts`, `evidence`), and SHALL record
whether it ran in shadow mode and what it would have changed.

5.7. THEN prompts, code and finding text SHALL never be written in full to `decisions.jsonl` or
`ledger.jsonl`; logs keep digests, as today.

5.8. WHEN a judgement needs text from an earlier event (an earlier finding, a previous failure)
THEN that text SHALL be held only under the Arbiter's ignored log directory (the one
`KW-ARB-LOG-001` checks), scoped to the run or review it belongs to.

5.9. WHEN a new rule's configuration is invalid THEN `arbiter validate` and `doctor` SHALL report a
permanent `KW-ARB-CONFIG-*` diagnostic with a hint, and the hook SHALL treat the rule as not
evaluated rather than guess.

5.10. THEN every new rule SHALL be testable end to end with a fake provider; no test needs a real
provider, network access or a paid call.

5.11. THEN this repository's `.kyber-weave/kyber-weave.yml` `arbiter.provider.kind: none` SHALL NOT
change.

### Requirement 6: One model-profile resolution path

**User Story:** As a Squad user with a model preset, I want the Arbiter to resolve a profile to the
same model token my rendered agents use, so that a retargeted dispatch respects my preset.

#### Acceptance Criteria

6.1. WHEN the Arbiter resolves a profile to a model token THEN it SHALL use the existing model-profile
resolution (`SquadModelResolution.ResolveToken` over the bundled matrix merged with the preset
selected for that harness and project), not a second copy of its logic.

6.2. WHEN the resolution returns no token (the profile resolves to `inherit`, or is undefined for
the harness) THEN the dispatch SHALL NOT be changed, and the decision SHALL record why.

6.3. THEN the project layering SHALL be decided in the design phase, with a recorded reason, so
that `KyberWeave.Core`'s Arbiter code does not take a dependency the repository's layering forbids.

6.4. WHEN the preset store cannot be read THEN resolution SHALL fail safe (no change) and the
decision SHALL record the reason.

### Requirement 7: Per-harness model-override capability

**User Story:** As an Arbiter maintainer, I want each hook adapter to declare whether its harness
accepts a per-dispatch model, so that no decision claims a model change a harness cannot make.

#### Acceptance Criteria

7.1. THEN each hook adapter SHALL declare whether its harness accepts a model on a single dispatch,
in the same way adapters declare whether returns are observable, and SHALL name the dispatch field
that carries it.

7.2. WHEN an adapter's harness is declared to accept a per-dispatch model THEN that declaration
SHALL rest on vendor documentation verified during the design phase and cited in the harness facts.

7.3. IF a harness's support is unverified THEN its adapter SHALL declare it unsupported.

7.4. WHEN a harness does not support the override THEN model targeting SHALL record the profile and
token it would have chosen and change nothing.

### Requirement 8: Task difficulty judgement

**User Story:** As a Squad user, I want each implementation task judged easy, standard or hard
before it is dispatched, so that easy work runs on a cheaper model and hard work on a stronger one.

#### Acceptance Criteria

8.1. WHEN a `delegate` trigger fires for an implementation-specialist dispatch THEN a new step-1
`choice` rule SHALL ask how hard the task is, with answers `easy`, `standard` and `hard`.

8.2. THEN the rule SHALL read the plan task text, the task's file list, and the file kinds of those
files.

8.3. WHEN the chosen answer's confidence is at or above the rule's threshold THEN the answer SHALL
map to a semantic profile through a mapping in the `arbiter:` configuration.

8.4. THEN the configuration and the rule SHALL name only semantic profiles, never a concrete model
id; a concrete id in the mapping SHALL fail validation.

8.5. IF the answer is below threshold or undecidable THEN no profile SHALL be chosen (Requirement 5.1).

### Requirement 9: Retarget effect and delivery

**User Story:** As a Squad user, I want the chosen profile written into the dispatch itself, so that
the worker actually runs on that model.

#### Acceptance Criteria

9.1. WHEN a profile is chosen, the rule is not in shadow mode, the harness supports the override,
and no rule on the firing escalates THEN the Arbiter SHALL return a new effect (working name
`retarget`) carrying the profile name.

9.2. WHEN the effect is delivered THEN the hook host SHALL resolve the profile to the harness token
(Requirement 6) and the adapter SHALL write it into the dispatch's model field.

9.3. WHEN an adapter rewrites the dispatch input THEN it SHALL copy every input field and change only
the model field and, where the routing headers are stripped today, the prompt; the model change and
the header strip SHALL be delivered in one rewrite.

9.4. IF the dispatch input already names a model THEN the Arbiter SHALL leave it unchanged and record
that it did (decision D9).

9.5. WHEN the resolved token equals the model the agent was rendered with THEN the dispatch SHALL be
left unchanged and the decision SHALL record no-op.

### Requirement 10: Retry ladder

**User Story:** As a Squad user, I want a task that came back failing to retry on a stronger model,
so that a model that was too weak is not retried at the same strength.

#### Acceptance Criteria

10.1. WHEN a `delegate` fires for a task whose earlier dispatch in this run returned anything other
than `RESULT: PASS` THEN a step-0 rule SHALL choose the next profile up from the one that earlier
dispatch ran on, with no model call.

10.2. THEN the ladder order SHALL be a configured ordered list of semantic profiles in the `arbiter:`
section.

10.3. WHEN the earlier profile is already the top of the ladder, or the earlier profile is not
recorded THEN the ladder SHALL choose nothing.

10.4. WHEN the ladder chooses a profile THEN it SHALL take precedence over the difficulty judgement
for that dispatch.

10.5. THEN the ledger SHALL record, per dispatch, the profile and token the dispatch ran on, so that
the ladder can read it.

10.6. WHEN returns are unobservable on the harness THEN the ladder SHALL answer
`returns-unobservable` and choose nothing.

### Requirement 11: Who may be retargeted

**User Story:** As a repository owner, I want model targeting limited to implementation work in this
version, so that planning, review and orchestration quality is never traded for cost.

#### Acceptance Criteria

11.1. WHEN the dispatch target is a planner (`architect`, `product-owner`), a reviewer
(`code-reviewer`, `review-lens`, `review-triage`), or the orchestrator (`conductor`) THEN neither the
difficulty judgement nor the ladder SHALL change its model.

11.2. THEN only dispatches to the roster's implementation specialists SHALL be eligible, with that
set defined once (design phase) rather than per rule.

11.3. WHEN the target cannot be identified THEN the dispatch SHALL be ineligible.

### Requirement 12: Shadow mode and outcome join for model targeting

**User Story:** As a repository owner, I want model targeting to run in shadow first and show its
choices beside the tasks' outcomes, so that I can see whether it would have chosen well before
enabling it.

#### Acceptance Criteria

12.1. THEN model targeting SHALL have one configuration switch whose default is shadow.

12.2. WHEN model targeting runs in shadow THEN the decision SHALL record the chosen profile, the
resolved token, the probabilities, the confidence and the threshold, and the dispatch SHALL be
unchanged.

12.3. WHEN the task later returns THEN the tuning report (Requirement 4) SHALL show the choice beside
the task's `RESULT:` and `TASK_GATE` outcome.

12.4. WHEN model targeting is enabled THEN the same fields SHALL be recorded, plus whether the
override was delivered.

### Requirement 13: Nitpick filter

**User Story:** As a code-reviewer, I want nitpick lens findings dropped before any refuter or fix
is dispatched for them, so that the run does not spend agents on style preferences.

#### Acceptance Criteria

13.1. WHEN a lens returns findings THEN a new step-1 rule SHALL classify each finding as `nitpick`
or `substantive`.

13.2. WHEN a finding is confidently `nitpick` and the rule is enforced THEN no refuter and no fix
dispatch SHALL be spawned for it.

13.3. WHEN a finding is dropped THEN it SHALL be listed as dropped, with the rule id and decision id,
in the review output, and logged.

13.4. IF a failed gate corroborates the finding, or the finding carries the highest severity the lens
contract defines THEN it SHALL NOT be dropped.

13.5. IF unsure THEN the finding SHALL be kept as substantive.

### Requirement 14: Duplicate findings

**User Story:** As a code-reviewer, I want two lens findings about the same defect refuted once, so
that one defect does not cost two refuter spawns.

#### Acceptance Criteria

14.1. WHEN a refutation is about to be spawned THEN a new rule SHALL decide whether its finding is the
same defect as a finding already refuted or being refuted in the same review.

14.2. THEN earlier findings of the same review SHALL be available to that rule from a store whose
home the design phase decides (the in-flight ledger or a per-review store), within Requirement 5.8.

14.3. WHEN a finding is confidently a duplicate and the rule is enforced THEN its refuter SHALL NOT
be spawned, the finding SHALL be marked duplicate of the earlier one, and it SHALL take the earlier
finding's refutation outcome.

14.4. THEN findings SHALL be compared only within one review run.

14.5. IF unsure THEN both findings SHALL be refuted.

### Requirement 15: Trim what workers read

**User Story:** As a Squad user, I want a worker's packet to carry only the coding-standard and
documentation sections its task needs, so that workers read less without the plan being capped.

#### Acceptance Criteria

15.1. THEN the Arbiter SHALL have a section-level index of the governed coding standards and
documents a packet may point a worker to.

15.2. WHEN a dispatch packet points a worker to whole standards or documents THEN a new rule SHALL
judge which sections are relevant to the task.

15.3. WHEN the rule is confident and enforced THEN the packet SHALL carry references to only the
relevant sections.

15.4. THEN the selection SHALL be obtainable by the packet author before dispatch, through a surface
available on harnesses that cannot rewrite dispatch input, so trimming does not depend on an input
rewrite.

15.5. THEN the task text, acceptance criteria, file list, test command and routing headers SHALL
never be trimmed, and plan size SHALL never be capped (owner decision).

15.6. IF unsure about any section THEN that section SHALL be kept; IF unsure overall THEN the packet
SHALL be unchanged.

### Requirement 16: Bug triage shortcut

**User Story:** As a bug-crusher user, I want a clearly trivial defect fixed without first spawning
an investigator, so that one-line fixes cost one agent, not two.

#### Acceptance Criteria

16.1. WHEN bug-crusher is about to dispatch `bug-crusher-investigator` THEN a new rule SHALL classify
the reported defect as `TRIVIAL` or `NEEDS_ARCHITECT`.

16.2. WHEN the defect is confidently `TRIVIAL` and the rule is enforced THEN the investigator SHALL
NOT be spawned, and bug-crusher SHALL receive an outcome it accepts in place of the investigator's
verdict, naming the decision id.

16.3. THEN bug-crusher's tripwires and verification SHALL still apply after the shortcut, so a fix
that proves larger escalates to `architect` as today.

16.4. IF the answer is `NEEDS_ARCHITECT`, or anything less than a confident `TRIVIAL` THEN the
investigator SHALL be spawned.

### Requirement 17: Plan-or-spec intake shortcut

**User Story:** As a conductor user, I want a request that is clearly plan-sized or clearly
spec-sized routed without an architect intake spawn, so that obvious intake costs no agent.

#### Acceptance Criteria

17.1. WHEN the conductor is about to dispatch the architect intake assessment (the `INTAKE:` marker)
THEN a new rule SHALL classify the request as `PLAN` or `SPEC`.

17.2. WHEN the answer is confident and the rule is enforced THEN the intake dispatch SHALL NOT be
spawned, and the conductor SHALL receive a digest in the `STATUS: INTAKE_RECOMMENDATION` shape it
already accepts, with a rationale naming the rule and decision id.

17.3. THEN the conductor's handling of that digest SHALL be unchanged: supervised runs still ask the
user to choose; autonomous runs record the choice.

17.4. IF unsure THEN the architect intake SHALL be spawned.

17.5. THEN the shortcut SHALL never replace the planning dispatch that follows intake.

### Requirement 18: Documentation-impact shortcut

**User Story:** As a conductor user, I want the docs-dev documentation-impact review skipped when a
change confidently touches no documented behaviour, so that small internal changes cost one less agent.

#### Acceptance Criteria

18.1. WHEN the conductor or bug-crusher is about to dispatch the docs-dev documentation-impact review
THEN a new rule SHALL decide whether the change touches documented behaviour.

18.2. WHEN the answer is a confident no and the rule is enforced THEN the review SHALL NOT be spawned,
and the caller SHALL receive an outcome it can carry into the pull request's documentation-impact
section, naming the decision id.

18.3. THEN a plan or specification closeout dispatch to docs-dev SHALL never be skipped.

18.4. IF the change edits a governed document, or the answer is anything but a confident no THEN the
review SHALL be spawned.

### Requirement 19: Same-failure detection for the circuit breaker

**User Story:** As a conductor user, I want a repeated failure recognised as the same failure, so that
the iteration circuit breaker trips before more rework is spent on it.

#### Acceptance Criteria

19.1. WHEN a task returns a non-PASS result after an earlier non-PASS attempt THEN a new rule SHALL
decide whether this failure is the same as the previous attempt's.

19.2. WHEN the answer is a confident yes and the rule is enforced THEN the conductor SHALL receive it
as input to the existing failure-cluster keying, so the attempt joins the previous cluster and its
dispatch tally.

19.3. THEN the breaker's limits, tally persistence and oscillation detection SHALL be unchanged; the
rule only supplies the same-failure answer.

19.4. IF unsure THEN cluster keying SHALL follow today's rules.

### Requirement 20: Bot review comment triage

**User Story:** As a pull request author, I want each bot review finding classified before an agent
is spawned for it, whether the bot posted it as an inline comment or as a row of a PR-level summary
comment, so that nitpicks and already-fixed findings cost no agent.

**Amended 2026-11-11 by owner decision D55** ([tasks](tasks.md#decisions)). The approved text limited
triage to bot-authored inline review comments. The owner's correction, verbatim: "this is wrong, most
if not all of the comments that cuase this are summary comments". Evidence: a Kilo review of PR 589
posted 15 findings in one PR-level summary comment with a findings table
(`Severity | File | Line | Issue`) and zero inline threads. Criteria 20.1, 20.4 and 20.5 are reworded
and 20.8–20.13 added; 20.2, 20.3, 20.6 and 20.7 are unchanged and apply to each summary row.

#### Acceptance Criteria

20.1. WHEN a review loop (for example the Kilo review loop or the code-review loop) is about to act on
a bot-authored inline review comment, or on a bot-authored PR-level summary comment, THEN a new rule
SHALL classify the inline comment, or each finding row of the summary comment, as `real`, `nitpick`
or `already-fixed`.

20.2. THEN the home of this judgement (a hook trigger, or an `arbiter` MCP tool the review loops
call) SHALL be decided in the design phase, with a recorded reason.

20.3. THEN the rule SHALL read the comment, its `file:line`, and the current text at that line from
the `file.text-at-line` fact, which this specification fills.

20.4. WHEN an inline comment is confidently `nitpick` THEN the loop SHALL reply "nitpick" and resolve
it with no agent spawned (owner policy for nitpicks).

20.5. WHEN an inline comment or a summary row is confidently `already-fixed` and the fixing commit can
be named THEN the loop SHALL report that commit with no agent spawned (an inline comment is replied
to and resolved; a summary row is listed per 20.11); IF no commit can be named THEN the comment or
row SHALL be treated as `real`.

20.6. WHEN a comment is `real`, or the answer is unsure THEN the loop SHALL spawn as it does today.

20.7. THEN comments from human reviewers SHALL never be classified, and the Arbiter itself SHALL
never post to the pull request; the loop agent does, as today.

20.8. WHEN a bot-authored PR-level summary comment carries a findings table THEN each table row SHALL
be one item, keyed by its file and line, and classified independently of the other rows.

20.9. IF the classification of a summary row is unsure, below the rule's confidence floor, or not
obtained (provider failure), OR the row's file cannot be read, OR the row is malformed (no file, or
no positive line number) THEN that row SHALL be `real`.

20.10. IF a summary comment's author is given and is not a bot (a login ending `[bot]` or listed in
`arbiter.comment-triage.bot-logins`) THEN every row SHALL be `real` and no provider SHALL be asked.

20.11. WHEN a loop has acted on a summary comment's rows THEN, because a summary comment has no
thread to resolve, the loop SHALL post ONE pull request comment listing the fixed rows (each with
its fix commit link), the already-fixed rows (each with the fixing commit), the nitpick rows and the
rejected rows (each with its reason); inline threads keep reply-and-resolve (20.4, 20.5).

20.12. IF a summary comment has no recognisable findings table THEN the comment SHALL be treated as
`real` as a whole, never as "no findings".

20.13. THEN summary triage SHALL be delivered through the existing tool
`arbiter_classify_bot_review_comment` and CLI `kyber-weave arbiter comment`, in a summary mode; no
new tool name SHALL be added.

### Requirement 21: Delivery in three phases

**User Story:** As the owner, I want this delivered as three draft pull requests in a fixed order,
each shippable alone, so that each can be reviewed and merged without the others.

#### Acceptance Criteria

21.1. THEN delivery phase 1 (Foundation) SHALL cover Requirements 1–7.

21.2. THEN delivery phase 2 (Model targeting) SHALL cover Requirements 8–12 and ship shadow by default.

21.3. THEN delivery phase 3 SHALL cover Requirements 13–20.

21.4. WHEN a phase merges THEN the repository SHALL build, its targeted tests and the docs gates SHALL
pass, and the canonical Arbiter documentation SHALL describe what that phase shipped.

21.5. THEN the tasks artifact SHALL group tasks by these three phases, each ending with its own
documentation, verification and review, and the whole ending with one docs-dev closeout.

### Requirement 22: Documentation and verification

**User Story:** As a maintainer, I want each phase documented and verified the way the repository
requires, so that the corpus stays at zero findings and the change is reviewable.

#### Acceptance Criteria

22.1. WHEN a phase edits `docs/` THEN `docs validate` and `docs drift` SHALL report zero findings.

22.2. THEN each phase SHALL be delivered test-first: a failing test precedes each behaviour change.

22.3. THEN dev agents SHALL run targeted tests only; the full suite runs in the review council and CI
(ADR 0059).

22.4. THEN the Arbiter architecture, runbook, configuration reference and rule reference SHALL be
updated for every new effect, trigger, fact, rule, configuration key and harness declaration.

22.5. THEN the Squad role instructions that receive a new outcome (conductor, bug-crusher,
code-reviewer, docs-dev, the review loops) SHALL be updated to accept it, and their portable copies
re-rendered as the repository's build requires.

## Design harvest

Requirement-level harvest items. The full harvest is in [design](design.md#design-harvest).

| ID | Design item | Target component | Target doc type |
|---|---|---|---|
| H17 | Requirements 1–7 (foundation) | KyberArbiter | architecture |
| H18 | Requirements 8–12 (model targeting) | KyberArbiter | architecture, runbook |
| H19 | Requirements 13–20 (spawn-removing judgements) | KyberArbiter, ReviewCouncil, KyberSquad | architecture |
| H20 | Requirements 21–22 (delivery and verification) | KyberArbiter | none (process; waiver) |

## Closeout mapping

Destinations are the same as in the [specification closeout mapping](README.md#closeout-mapping).

| ID | Destination | Documentation impact | Status |
|---|---|---|---|
| D1 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: process | waiver (process) | archived |
| D2 | [ADR 0053 status](../../../adr/0053-kyber-arbiter-three-step-decision-gates.md#status) | extends-component | archived |
| D3 | [Tuning thresholds](../../../kyber-arbiter/runbook.md#tuning-thresholds-arbiter-tune) | extends-component | archived |
| D4 | [Phase 1 facts](../../../kyber-arbiter/architecture.md#phase-1-facts) | extends-component | archived |
| D5 | [Model targeting (phase 2)](../../../kyber-arbiter/architecture.md#model-targeting-phase-2) | extends-component | archived |
| D6 | [Per-dispatch model](../../../kyber-arbiter/architecture.md#per-dispatch-model) | extends-component | archived |
| D7 | [Phase 3 rules](../../../kyber-arbiter/runbook.md#phase-3-rules-reading-and-enforcing) | extends-component | archived |
| D8 | [Gate applicability and Arbiter review gates](../../../code-review/architecture.md#gate-applicability-and-arbiter-review-gates), [The rules](../../../kyber-arbiter/architecture.md#the-rules) | extends-component | archived |
| D9 | [Per-dispatch model](../../../kyber-arbiter/architecture.md#per-dispatch-model) | extends-component | archived |
| D10 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: process | waiver (process) | archived |
| D11 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D12 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D13 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: approval provenance | waiver (process) | archived |
| H17 | [Phase 1 facts](../../../kyber-arbiter/architecture.md#phase-1-facts), [Secondary triggers](../../../kyber-arbiter/architecture.md#secondary-triggers-families-and-effects) | extends-component | archived |
| H18 | [Model targeting (phase 2)](../../../kyber-arbiter/architecture.md#model-targeting-phase-2), [Switching to enforce](../../../kyber-arbiter/runbook.md#switching-model-targeting-to-enforce) | extends-component | archived |
| H19 | [Arbiter savings in the roles](../../../kyber-squad/architecture.md#arbiter-savings-in-the-roles), [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments), [Gate applicability](../../../code-review/architecture.md#gate-applicability-and-arbiter-review-gates) | extends-component | archived |
| H20 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: process | waiver (process) | archived |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Specification or plan? | Specification, one bundle for all three delivery phases | Architect intake recommendation; owner pre-approved the three-PR sequence | autonomous: recommendation accepted |
| D2 | Amend or supersede ADR 0053 for new effects and triggers? | Amend in place | [ADR index](../../adr/README.md): an ADR is edited in place when its decision changes; a new ADR only for a different subject | autonomous: recommendation accepted |
| D3 | Is `KW-ARB-LENS-001`'s threshold already tunable? | Yes: `probability-below` is an accepted shipped-rule override (`ArbiterConfigLoader.FindOverrideViolation`, `ApplyOverride`). The remaining work is a read-only tuning report | Code read 2026-11-10 at `e418fe93` | autonomous: recommendation accepted |
| D4 | What counts as rename-only for lens skipping? | A git-detected rename with identical content. Symbol renames inside code are out of scope (they need judgement, not step 0) | Cheap to widen later | autonomous: low-confidence, no authoritative source |
| D5 | Does "provider none means no change" stop the retry ladder? | No. The owner's list applies to the step-1 difficulty answer; the ladder is step 0 with no model call by the owner's own description. The ladder still obeys the model-targeting shadow switch | Conflict noted between two owner statements; resolved toward the more specific one. Cheap to reverse (one condition) | autonomous: low-confidence, no authoritative source |
| D6 | Which harnesses accept a per-dispatch model? | Not decided here. Each adapter declares support only from vendor documentation verified in the design phase; unverified means unsupported | Fail-safe default; the design phase dispatches research | autonomous: recommendation accepted |
| D7 | Do the new spawn-removing step-1 rules (Requirements 13–20) enforce by default? | No: shadow by default, like model targeting. Their thresholds are unmeasured, and the shipped thresholds were set only after a measured calibration | Cost: no savings until the owner enables each rule (one configuration line). Owner's call, recorded in Open questions | autonomous: low-confidence, no authoritative source |
| D8 | Does hunk-level lens skipping enforce by default? | Yes: it is step 0, deterministic, and fails safe on any unknown language or missing hunk, like the existing Markdown-only skip | Cheap to reverse through the rule's `enabled` override | autonomous: low-confidence, no authoritative source |
| D9 | What if a dispatch already names a model? | Leave it and record it; an explicit choice by the caller wins | Cheap to reverse | autonomous: low-confidence, no authoritative source |
| D10 | How do phase PRs merge while this specification is open? (`docs validate --merge-ready` fails on an open spec, `KW-DOC-LIFECYCLE-003`) | Follow the archived Kyber Arbiter precedent (its Q16): archive the complete specification once all three phases are specified, so trunk carries it and each phase PR updates the canonical docs | [Archived Kyber Arbiter spec](../../archive/specs/kyber-arbiter/README.md), Q16. The design and tasks phases confirm the mechanics | autonomous: recommendation accepted |
| D11 | Which bot comments are classified? | Bot-authored comments only; human comments are never classified. Amended by D55: bot-authored PR-level summary comments are classified too, row by row | Matches the Kilo review loop's scope, which excludes human reviewers | autonomous: recommendation accepted; scope widened by owner decision D55 (2026-11-11) |
| D12 | Can the Arbiter post replies on a pull request? | No. The review loop agent posts, as today; the Arbiter only classifies | Posting is outward-facing; keeping it with the existing loop changes nothing outward | autonomous: recommendation accepted |
| D13 | Are the requirements approved? | Yes: Requirements 1–22 and D1–D12 approved as written, 2026-11-10 | Conductor input `AUTONOMOUS_APPROVAL: requirements-approval` under autonomy `autonomous`; Open questions Q1–Q3 keep their safe defaults. Design decisions continue at D14 in [design](design.md#decisions) | autonomous: recommendation accepted |

Owner decision D55 (2026-11-11), recorded in the [tasks Decisions table](tasks.md#decisions), amends
Requirement 20 to cover bot-authored PR-level summary comments; D56–D61 there record how.

## Open questions

Owner's calls. A safe default is already taken for each; none blocks the design phase.

- **Q1. When to enforce the new judgements.** Default taken: every new step-1 rule ships in shadow
  mode (D7). Enabling one is a per-rule configuration line.
- **Q2. Measuring the new thresholds.** Measuring them the way the shipped thresholds were measured
  makes paid provider calls. Default taken: no paid calls are made by this work; the tuning report
  (Requirement 4) lets the owner measure from real shadow runs instead.
- **Q3. Provider choice.** Default taken: this repository's `arbiter.provider.kind` stays `none`; all
  behaviour is verified with a fake provider.

## Design-phase obligations

Carried from intake so the design phase answers them, each with a recorded reason:

- The new effects and triggers, by name, and how each combines with its family (Requirement 1).
- Where the conductor family's combination grows beyond escalate-wins for `retarget` and the skip
  outcomes of Requirements 16–18.
- The pairwise fact for duplicate findings, which the single-dispatch fact model cannot express
  (Requirement 14).
- The store for earlier findings and previous failures (Requirements 5.8, 14.2, 19).
- The project layering for profile resolution (Requirement 6.3).
- The per-harness model-override facts from vendor documentation, through `research-agent`
  (Requirement 7).
- The section-level index and the pre-dispatch surface for trimmed reads (Requirement 15).
- The home of bot comment triage (Requirement 20.2).
