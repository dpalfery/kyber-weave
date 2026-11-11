---
id: archive/specs/2026-11-10-arbiter-decision-model-savings/index
title: Arbiter decision-model savings specification
doc-type: index
status: archived
owner: dpalfery
last-reviewed: 2026-11-11
documentation-impact: extends-component
impact-components: [KyberArbiter, KyberSquad, ReviewCouncil]
---

# Arbiter decision-model savings specification

**Status:** Archived
**Archive Date:** 2026-11-11

Closed out 2026-11-11. Durable content is harvested into the canonical documents in the
[closeout mapping](#closeout-mapping). Delivery is recorded with its deviations and open owner
decisions below; those are the truth, and the requirements, design and tasks are the intent as
approved.

The Kyber Arbiter's step-1 decision models (TypeSafe's cloud JEV models, or a local Ollama
model) already answer four judgements. This specification extends them so a Kyber-Squad run
spawns fewer agents and sends more of the work it does spawn to a cheaper model. Every new
judgement fails safe: when unsure, the run does the expensive thing it does today.

| Document | Covers | Closeout state |
|---|---|---|
| [Requirements](requirements.md) | Requirements 1–22: the foundation, model targeting per task, and eight spawn-removing judgements | Archived; Req 20 amended by owner (D55) |
| [Design](design.md) | Secondary triggers and new families, render-time model-profile resolution, per-harness model capability, run stores, tool surfaces, delivery file lists | Archived; §7.8.1 summary-mode amendment delivered, approval not recorded |
| [Tasks](tasks.md) | 58 tasks in three phases plus closeout, test-first, with the Test contract | Archived; task checkboxes were never ticked (see open decisions) |

## Delivery

Delivered in three stacked draft pull requests, in order:

1. **Foundation** — the ADR amendment, the new effects, triggers and facts, the
   `review.changed-hunks` filler, the model-profile resolution layering, hunk-level lens
   skipping, and the threshold-tuning report.
2. **Model targeting** — choosing a model profile per implementation task, shadow mode by default.
3. **Spawn-removing judgements** — nitpick filter, duplicate findings, trimmed worker reads,
   bug-triage shortcut, intake shortcut, docs-impact shortcut, same-failure detection, and
   bot review comment triage, including the summary-comment mode added by D55.

## Decisions

The permanent decision record is in [requirements](requirements.md#decisions) (D1–D13),
[design](design.md#decisions) (D14–D42) and [tasks](tasks.md#decisions) (D43–D61, including
the owner's D55). Each is mapped in the [closeout mapping](#closeout-mapping).

| ID | Question | Recorded in |
|---|---|---|
| D1 | Specification or plan? | [requirements](requirements.md#decisions) |
| D2 | Amend or supersede ADR 0053 for new effects and triggers? | [requirements](requirements.md#decisions) |
| D3 | Is `KW-ARB-LENS-001`'s threshold already tunable? | [requirements](requirements.md#decisions) |
| D4 | What counts as rename-only for lens skipping? | [requirements](requirements.md#decisions) |
| D5 | Does "provider none means no change" stop the retry ladder? | [requirements](requirements.md#decisions) |
| D6 | Which harnesses accept a per-dispatch model? | [requirements](requirements.md#decisions) |
| D7 | Do the new spawn-removing step-1 rules enforce by default? | [requirements](requirements.md#decisions) |
| D8 | Does hunk-level lens skipping enforce by default? | [requirements](requirements.md#decisions) |
| D9 | What if a dispatch already names a model? | [requirements](requirements.md#decisions) |
| D10 | How do phase PRs merge while this specification is open? | [requirements](requirements.md#decisions) |
| D11 | Which bot comments are classified? | [requirements](requirements.md#decisions) |
| D12 | Can the Arbiter post replies on a pull request? | [requirements](requirements.md#decisions) |
| D13 | Are the requirements approved? | [requirements](requirements.md#decisions) |
| D14 | How does a new judgement get its own fail-safe `undecidable`? | [design](design.md#decisions) |
| D15 | Does the conductor family's combination grow beyond escalate-wins? | [design](design.md#decisions) |
| D16 | Core.Squad to Arbiter layering for profile to token resolution: move or reference? | [design](design.md#decisions) |
| D17 | Where does the model-targets table live? | [design](design.md#decisions) |
| D18 | Claude takes aliases only; bundled tokens include full ids | [design](design.md#decisions) |
| D19 | Which harnesses support a per-dispatch model? | [design](design.md#decisions) |
| D20 | `review.changed-hunks` value shape versus `KW-ARB-PREEX-001`'s comparison | [design](design.md#decisions) |
| D21 | Which lenses does `KW-ARB-LENS-002` skip? | [design](design.md#decisions) |
| D22 | Is `KW-ARB-NITPICK-002` shadowed separately? | [design](design.md#decisions) |
| D23 | Model-targeting defaults | [design](design.md#decisions) |
| D24 | Home of earlier findings and failures | [design](design.md#decisions) |
| D25 | Pairwise fact for duplicates | [design](design.md#decisions) |
| D26 | How is a documentation-impact review dispatch recognised? | [design](design.md#decisions) |
| D27 | Home of the trim judgement and its rewrite rule | [design](design.md#decisions) |
| D28 | A document whose every section is judged not needed | [design](design.md#decisions) |
| D29 | Home of bot comment triage | [design](design.md#decisions) |
| D30 | Intake shortcut digest shape | [design](design.md#decisions) |
| D31 | Additive decision fields or a schema bump? | [design](design.md#decisions) |
| D32 | Triage shortcut digest | [design](design.md#decisions) |
| D33 | Shortcut combination | [design](design.md#decisions) |
| D34 | `drop` versus `annotate` on one finding | [design](design.md#decisions) |
| D35 | Is a second step-1 call allowed for secondary triggers? | [design](design.md#decisions) |
| D36 | Retargeting bug-crusher's dispatches | [design](design.md#decisions) |
| D37 | Review identity for the finding store | [design](design.md#decisions) |
| D38 | Store retention | [design](design.md#decisions) |
| D39 | Conflict between ADR 0053's hook-capability wording and Req 7.3 | [design](design.md#decisions) |
| D40 | Is `delivered: true` proof the model ran? | [design](design.md#decisions) |
| D41 | `KW-ARB-NITPICK-001` floor and others | [design](design.md#decisions) |
| D42 | Is the design approved? | [design](design.md#decisions) |
| D43 | Where is `file.text-at-line` filled today? | [tasks](tasks.md#decisions) |
| D44 | Which file is the delivery-execution contract? | [tasks](tasks.md#decisions) |
| D45 | Where does an agent's rendered profile come from? | [tasks](tasks.md#decisions) |
| D46 | How is secondary-trigger evaluation testable in phase 1? | [tasks](tasks.md#decisions) |
| D47 | Where does the semantic-profile parity test live? | [tasks](tasks.md#decisions) |
| D48 | How do ledger pre events learn a retargeted model? | [tasks](tasks.md#decisions) |
| D49 | How do phase pull requests merge while this specification is open? | [tasks](tasks.md#decisions) |
| D50 | Which delegate owns non-`.cs` files? | [tasks](tasks.md#decisions) |
| D51 | Where does `ModelTargetResolver` run? | [tasks](tasks.md#decisions) |
| D52 | What does "re-render portable copies" require? | [tasks](tasks.md#decisions) |
| D53 | Are the tasks approved? | [tasks](tasks.md#decisions) |
| D54 | Is execution authorized? | [tasks](tasks.md#decisions) |
| D55 | Does bot comment triage cover only inline review comments? (owner: summary comments too) | [tasks](tasks.md#decisions) |
| D56 | Where does summary mode start from? | [tasks](tasks.md#decisions) |
| D57 | How is a findings table recognised and a row parsed? | [tasks](tasks.md#decisions) |
| D58 | Who counts as a bot, and what if `author` is absent? | [tasks](tasks.md#decisions) |
| D59 | Provider cost per summary (row cap) | [tasks](tasks.md#decisions) |
| D60 | Does the commit rule of Req 20.5 apply per row? | [tasks](tasks.md#decisions) |
| D61 | How do the loops find and answer summary comments? | [tasks](tasks.md#decisions) |

## Design harvest

Scale: `documentation-impact: extends-component`, `impact-components: [KyberArbiter, KyberSquad, ReviewCouncil]`.
The design's own harvest table is in [design](design.md#design-harvest); it is reproduced here so
each item maps to a destination below.

| ID | Design item (section) | Target component | Target doc type |
|---|---|---|---|
| H1 | Secondary triggers, families, effects, combination (§2) | KyberArbiter | adr (amend 0053), architecture |
| H2 | New facts and fillers (§3) | KyberArbiter | architecture |
| H3 | New rules, `for-each` expansion (§4) | KyberArbiter | architecture, reference (rule reference) |
| H4 | Shadow mode, model-targeting switch, fail-safe table (§5) | KyberArbiter | architecture, runbook |
| H5 | Model-targets table and render-time resolution (§6.1) | KyberArbiter, KyberSquad | architecture, adr (amend 0053) |
| H6 | Per-harness model-override capability and sources (§6.3, §17) | KyberArbiter | architecture (harness facts) |
| H7 | Profile choice, eligibility, delivery, ladder (§6.4–§6.8) | KyberArbiter | architecture, runbook |
| H8 | Nitpick, duplicate, hunk skip in the review council (§7.1, §7.2, §7.9) | ReviewCouncil | architecture |
| H9 | Shortcut digests and role changes (§7.4–§7.7) | KyberSquad | architecture |
| H10 | Section index and trim tool (§7.3) | KyberArbiter | architecture, reference (CLI) |
| H11 | Bot comment triage tool, inline and summary mode, and the loops' one-comment summary report (§7.8, §7.8.1) | KyberArbiter, KyberSquad | architecture, runbook |
| H12 | Run stores (§8) | KyberArbiter | architecture |
| H13 | Configuration keys (§9) | KyberArbiter | reference (configuration) |
| H14 | Decision log and ledger fields (§10) | KyberArbiter | architecture |
| H15 | Diagnostics (§11) | KyberArbiter | reference (rule reference) |
| H16 | `arbiter tune` and the new tools (§12) | KyberArbiter | runbook, reference (CLI command surface) |
| H17 | Requirements 1–7 (foundation) | KyberArbiter | architecture |
| H18 | Requirements 8–12 (model targeting) | KyberArbiter | architecture, runbook |
| H19 | Requirements 13–20 (spawn-removing judgements) | KyberArbiter, ReviewCouncil, KyberSquad | architecture |
| H20 | Requirements 21–22 (delivery and verification) | KyberArbiter | none (process; closes with the spec) |

## Closeout mapping

Every decision D1–D61 and harvest item H1–H20 maps to its destination. A waiver is stated in the
destination cell: process and approval records have no surviving architectural invariant, and
the policy page is their home. No canonical document links this specification.

| ID | Destination | Documentation impact | Status |
|---|---|---|---|
| D1 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: the three-file bundle shape is process | waiver (process) | archived |
| D2 | [ADR 0053 status](../../../adr/0053-kyber-arbiter-three-step-decision-gates.md#status) | extends-component | archived |
| D3 | [Tuning thresholds](../../../kyber-arbiter/runbook.md#tuning-thresholds-arbiter-tune) | extends-component | archived |
| D4 | [Phase 1 facts](../../../kyber-arbiter/architecture.md#phase-1-facts) | extends-component | archived |
| D5 | [Model targeting (phase 2)](../../../kyber-arbiter/architecture.md#model-targeting-phase-2) | extends-component | archived |
| D6 | [Per-dispatch model](../../../kyber-arbiter/architecture.md#per-dispatch-model) | extends-component | archived |
| D7 | [Phase 3 rules: reading and enforcing](../../../kyber-arbiter/runbook.md#phase-3-rules-reading-and-enforcing) | extends-component | archived |
| D8 | [The rules](../../../kyber-arbiter/architecture.md#the-rules) | extends-component | archived |
| D9 | [Per-dispatch model](../../../kyber-arbiter/architecture.md#per-dispatch-model) | extends-component | archived |
| D10 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: merge sequencing is process | waiver (process) | archived |
| D11 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D12 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D13 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: approval provenance | waiver (process) | archived |
| D14 | [Secondary triggers](../../../kyber-arbiter/architecture.md#secondary-triggers-families-and-effects) | extends-component | archived |
| D15 | [Secondary triggers](../../../kyber-arbiter/architecture.md#secondary-triggers-families-and-effects) | extends-component | archived |
| D16 | [Model targets](../../../kyber-arbiter/architecture.md#model-targets) | extends-component | archived |
| D17 | [Model targets](../../../kyber-arbiter/architecture.md#model-targets) | extends-component | archived |
| D18 | [Model targets](../../../kyber-arbiter/architecture.md#model-targets) | extends-component | archived |
| D19 | [Per-dispatch model](../../../kyber-arbiter/architecture.md#per-dispatch-model) | extends-component | archived |
| D20 | [Phase 1 facts](../../../kyber-arbiter/architecture.md#phase-1-facts) | extends-component | archived |
| D21 | [Gate applicability and Arbiter review gates](../../../code-review/architecture.md#gate-applicability-and-arbiter-review-gates) | extends-component | archived |
| D22 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D23 | [Arbiter configuration](../../../configuration.md#arbiter-configuration) | extends-component | archived |
| D24 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D25 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D26 | [Arbiter savings in the roles](../../../kyber-squad/architecture.md#arbiter-savings-in-the-roles) | extends-component | archived |
| D27 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D28 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D29 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D30 | [Arbiter savings in the roles](../../../kyber-squad/architecture.md#arbiter-savings-in-the-roles) | extends-component | archived |
| D31 | [Decision record fields](../../../kyber-arbiter/architecture.md#decision-record-fields-in-phase-1) | extends-component | archived |
| D32 | [Arbiter savings in the roles](../../../kyber-squad/architecture.md#arbiter-savings-in-the-roles) | extends-component | archived |
| D33 | [Secondary triggers](../../../kyber-arbiter/architecture.md#secondary-triggers-families-and-effects) | extends-component | archived |
| D34 | [Secondary triggers](../../../kyber-arbiter/architecture.md#secondary-triggers-families-and-effects) | extends-component | archived |
| D35 | [Secondary triggers](../../../kyber-arbiter/architecture.md#secondary-triggers-families-and-effects) | extends-component | archived |
| D36 | [Arbiter savings in the roles](../../../kyber-squad/architecture.md#arbiter-savings-in-the-roles) | extends-component | archived |
| D37 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D38 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D39 | [ADR 0053 status](../../../adr/0053-kyber-arbiter-three-step-decision-gates.md#status) | extends-component | archived |
| D40 | [Per-dispatch model](../../../kyber-arbiter/architecture.md#per-dispatch-model) | extends-component | archived |
| D41 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D42 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: approval provenance | waiver (process) | archived |
| D43 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D44 | [Arbiter savings in the roles](../../../kyber-squad/architecture.md#arbiter-savings-in-the-roles) | extends-component | archived |
| D45 | [Model targets](../../../kyber-arbiter/architecture.md#model-targets) | extends-component | archived |
| D46 | [Secondary triggers](../../../kyber-arbiter/architecture.md#secondary-triggers-families-and-effects) | extends-component | archived |
| D47 | [Model targets](../../../kyber-arbiter/architecture.md#model-targets). Waiver: test-fixture location, no surviving invariant | waiver (test-fixture) | archived |
| D48 | [Decision record fields](../../../kyber-arbiter/architecture.md#decision-record-fields-in-phase-1) | extends-component | archived |
| D49 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: merge sequencing is process | waiver (process) | archived |
| D50 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: transient delegation choice | waiver (transient) | archived |
| D51 | [Model targets](../../../kyber-arbiter/architecture.md#model-targets) | extends-component | archived |
| D52 | [Rendering](../../../kyber-squad/architecture.md#8-rendering). Waiver: nothing committed, the check is the renderer contract tests | waiver (test-fixture) | archived |
| D53 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: approval provenance | waiver (process) | archived |
| D54 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: approval provenance | waiver (process) | archived |
| D55 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D56 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D57 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D58 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D59 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| D60 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments). Not delivered, see deviations | extends-component | archived |
| D61 | [Arbiter savings in the roles](../../../kyber-squad/architecture.md#arbiter-savings-in-the-roles) | extends-component | archived |
| H1 | [ADR 0053 status](../../../adr/0053-kyber-arbiter-three-step-decision-gates.md#status), [Secondary triggers](../../../kyber-arbiter/architecture.md#secondary-triggers-families-and-effects) | extends-component | archived |
| H2 | [Phase 1 facts](../../../kyber-arbiter/architecture.md#phase-1-facts), [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| H3 | [Rule reference, Arbiter](../../../ci-pipelines/rule-reference.md#arbiter--kyber-arbiter), [The rules](../../../kyber-arbiter/architecture.md#the-rules) | extends-component | archived |
| H4 | [Shadow and the switch](../../../kyber-arbiter/architecture.md#shadow-and-the-model-targeting-switch), [Switching to enforce](../../../kyber-arbiter/runbook.md#switching-model-targeting-to-enforce) | extends-component | archived |
| H5 | [Model targets](../../../kyber-arbiter/architecture.md#model-targets), [ADR 0053 status](../../../adr/0053-kyber-arbiter-three-step-decision-gates.md#status) | extends-component | archived |
| H6 | [Harness facts](../../../kyber-arbiter/architecture.md#harness-facts), [Arbiter configuration](../../../configuration.md#arbiter-configuration) | extends-component | archived |
| H7 | [Model targeting (phase 2)](../../../kyber-arbiter/architecture.md#model-targeting-phase-2), [Switching to enforce](../../../kyber-arbiter/runbook.md#switching-model-targeting-to-enforce) | extends-component | archived |
| H8 | [Gate applicability and Arbiter review gates](../../../code-review/architecture.md#gate-applicability-and-arbiter-review-gates) | extends-component | archived |
| H9 | [Arbiter savings in the roles](../../../kyber-squad/architecture.md#arbiter-savings-in-the-roles) | extends-component | archived |
| H10 | [CLI command surface](../../../cli-command-surface.md#kyber-weave-reference), [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| H11 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments), [Arbiter savings in the roles](../../../kyber-squad/architecture.md#arbiter-savings-in-the-roles), [Phase 3 rules](../../../kyber-arbiter/runbook.md#phase-3-rules-reading-and-enforcing) | extends-component | archived |
| H12 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| H13 | [Savings keys](../../../configuration.md#savings-keys) | extends-component | archived |
| H14 | [Decision record fields](../../../kyber-arbiter/architecture.md#decision-record-fields-in-phase-1) | extends-component | archived |
| H15 | [Rule reference, Arbiter](../../../ci-pipelines/rule-reference.md#arbiter--kyber-arbiter), [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments) | extends-component | archived |
| H16 | [Tuning thresholds](../../../kyber-arbiter/runbook.md#tuning-thresholds-arbiter-tune), [CLI command surface](../../../cli-command-surface.md#kyber-weave-reference) | extends-component | archived |
| H17 | [Phase 1 facts](../../../kyber-arbiter/architecture.md#phase-1-facts), [Secondary triggers](../../../kyber-arbiter/architecture.md#secondary-triggers-families-and-effects) | extends-component | archived |
| H18 | [Model targeting (phase 2)](../../../kyber-arbiter/architecture.md#model-targeting-phase-2), [Switching to enforce](../../../kyber-arbiter/runbook.md#switching-model-targeting-to-enforce) | extends-component | archived |
| H19 | [Phase 3 facts](../../../kyber-arbiter/architecture.md#phase-3-facts-review-shortcuts-stores-and-comments), [Arbiter savings in the roles](../../../kyber-squad/architecture.md#arbiter-savings-in-the-roles), [Gate applicability](../../../code-review/architecture.md#gate-applicability-and-arbiter-review-gates) | extends-component | archived |
| H20 | [Specification policy](../../../specs/README.md#specification-decisions-design-harvest-and-closeout-policy). Waiver: process, closes with the specification | waiver (process) | archived |

## Deviations as built

Checked against the code on branch `claude/feat/arbiter-spawn-removing-judgements` at closeout.

| Area | Specified | As built |
|---|---|---|
| Summary row cap (D59) | 25 rows per summary | 50 rows (`CommentTriageService.MaxSummaryRows`); later rows are `real`, unasked |
| Bot author in summary mode (D58) | An absent author is classified as before | An absent author is never a bot: every row is `real`, no provider call. A login ending `[bot]` counts only when `bot-logins` is non-empty |
| Summary with no bot logins | Not stated | No summary row is classified |
| Fixing-commit check (D60, Req 20.5) | `already-fixed` kept only when a fixing commit is named, per row | Not delivered. Summary rows reach the rule with no commit, so `already-fixed` rests on the model alone |
| Shadow for tool and CLI services (Req 5) | Shadow honoured by every new judgement | The evaluator honours shadow. The comment tool and CLI source has no shadow reference, so their answer is returned whatever the rule's shadow state |
| Section selector without a provider | The selector asks the provider | `arbiter sections` (and the tool) list every section with no provider call, marked `failedSafe` only when the provider is unsure or fails |
| `gates.report` fact (Req 2 filler set) | Filled | Catalogued; no filler sets it, so any rule reading it answers `undecidable` |
| Task checkboxes (closeout acceptance: every task ticked) | Ticked as delivered | All 58 remain unticked; delivery is evidenced by commits, and the ticks were not made without running each task's tests |
| Design and task approval of the summary amendment (D56–D61) | Approved before dispatch | Delivered on the branch; approval not recorded. The design and tasks headers still say "pending" |
| ADR 0053 model-override rule (D39) | Req 7.3 governs model override only | Amended in ADR 0053 status at closeout |

Shipped rule state: nine phase 3 rules ship with `shadow: true` (`KW-ARB-NITPICK-001`,
`KW-ARB-NITPICK-002`, `KW-ARB-DUP-001`, `KW-ARB-TRIAGE-001`, `KW-ARB-INTAKE-001`,
`KW-ARB-DOCIMPACT-001`, `KW-ARB-FAILURE-001`, `KW-ARB-TRIM-001`, `KW-ARB-COMMENT-001`). Nothing is
enforced until an owner sets `shadow: false` after calibration with `arbiter tune`.

Not closed by this specification: `KW-ARB-AUDIT-005`, `KW-ARB-PLAN-002` and `KW-ARB-PROJECT-001`
are named in neither the shipped rules file nor the rule reference. This predates the
specification and is not changed here.

CleanupCode was not run locally (it stalled), so the formatting pass is not evidenced by this
closeout. Continuous integration remains the gate for it.

## Open owner decisions

- Approve or reject the summary-comment design (D56–D61), retroactively.
- Accept the summary row cap of 50 in place of 25 (D59).
- Accept that an absent author is never classified in summary mode (D58).
- Decide whether to build the per-row fixing-commit check (D60, Req 20.5).
- Decide whether to build shadow honouring for the comment tool and CLI.
- Decide whether to build the `gates.report` filler or drop the rules that read it.
- Decide whether `arbiter sections` should use the provider, or stay provider-free by design.
- Decide whether the 58 task checkboxes are ticked retrospectively or the archive stands as is.
- Decide when to enforce the nine shadow rules, after calibration with `arbiter tune`.

## Verification at closeout

- `kyber-weave docs validate . --merge-ready` passes with no findings, with both `--base`
  origin/claude/feat/arbiter-model-targeting and origin/main. `kyber-weave docs drift .` reports no findings.
- Tests run: `ArchiveCloseoutScopeTests`, `ArchiveCloseoutValidatorTests`, `McpDocsGateToolsTests`
  and `OpenWorkValidatorTests`, 75 passed. The Arbiter behaviour tests were not re-run at closeout.
- Every decision and harvest item in the [closeout mapping](#closeout-mapping) links to a live
  governed document.
- The canonical documents were checked against source for the as-built behaviour above.
