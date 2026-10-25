---
id: plans/2026-10-25-docs-dev-harvest-closeout-gaps
title: "Make docs-dev closeout a design harvest: routing, impact declaration, spec parity, merge-ready closeout gate, three backfills"
doc-type: plan
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-25
development-mode: test-first
documentation-impact: extends-component
impact-components: [DocGraph, KyberSquad]
---

# Make docs-dev closeout a design harvest

**Status: Draft — decision-complete (D1–D25, Q1–Q3 all ANSWERED), awaiting the owner's
approve-and-execute, where the owner also confirms the documentation-impact scales (D23,
D24). Scope was revised 2026-10-25 at the owner's direction: closeout harvests the whole
design (architecture, feature set, interfaces, operations, decisions), not just decisions.
Q1–Q3 are orchestrator technical calls the owner may override at that gate. The conductor
re-runs `docs validate .` / `docs drift .` after this revision.**

**Development mode:** `test-first` (packet). **Autonomy:** `supervised` (packet; overrides the
Config Reg `<autonomy>` default for this run).

**Documentation impact (proposed, owner confirms at approve-and-execute):**
`extends-component` for `DocGraph` (the new gate) and `KyberSquad` (the agent lifecycle
contract). See `## Design harvest`.

**Working copy:** worktree `.claude/worktrees/harvest-gaps`, branch `docs/harvest-closeout-gaps`,
cut from `origin/main` `a0862e7c`. Paths below are relative to that worktree.

## Problem and goal

### Problem

The owner's intent for `docs-dev`: harvest **all** design and architecture from a plan or
spec into canonical docs under `docs/`, not just its decisions. When a plan or spec builds a
feature, the component's architecture docs must describe the new design and feature set. A
major new feature can warrant a whole new docs folder (architecture, onboarding), following
the ontology.

Today the contract is narrower than that, and even the narrow version slips:

1. **Closeouts are skipped** (D3). Three plans archived 2026-10-22..24 never got a real
   closeout and still carry `status: draft` with a body status of `Ready`:
   - `docs/archive/plans/2026-10-22-opencode-span-turn-hooks-kyberdash.md` (PR #423). Its §11
     recorded the architecture harvest as `RESIDUAL` with no todo, and it was archived anyway.
   - `docs/archive/plans/2026-10-23-opencode-context-capture-kyberdash.md` (PR #425). Moved
     into the archive unchanged (commit `8beb97ae`, git R100) to satisfy `KW-DOC-LIFECYCLE-003`.
   - `docs/archive/plans/2026-10-23-pi-renderer-network-read-mcp.md` (PR #426). Added straight
     into the archive (commit `6dd4ce3f`) while its body said Ready.
2. **Nothing mechanical catches it.** `KW-DOC-LIFECYCLE-003` only checks that no plan or spec
   is left in the active folder, so a bare move into the archive passes CI.
3. **The contract is about decisions, not design.** Plan closeout maps decision IDs. Nothing
   asks what scale of documentation change the work implies, which design sections must land
   where, or whether the component's architecture doc was updated. The spec path does not
   even have the decision mapping.

### Goal

- **A — Routing and framing.** `docs-dev`'s description and Scope lead with plan/spec
  closeout as a **design harvest**: design, architecture, feature set, interfaces and config,
  operations, and decisions. It still routes READMEs, ADRs and runbooks, and keeps the
  spec-authoring exclusion.
- **B — Declare the impact up front; spec parity.** At authoring time, the architect
  (plans) and product-owner (spec design phase) declare:
  - a documentation impact: `new-component`, `extends-component` or `no-design-change`;
  - the impact components;
  - a `## Design harvest` list naming which design sections go to which canonical docs.

  The owner confirms the scale at approve-and-execute. Specs get the permanent Decisions
  table, and both plans and specs get the same closeout rules: scale obligations, a mapping
  of every design item and decision ID, ADR waiver vs documentation waiver, and "do not
  archive" when anything is unmapped. The intent-alignment lens and the conductor check
  specs as well as plans.
- **C — Gate.** A new rule, `KW-DOC-LIFECYCLE-004`, enforces mechanically under
  `--merge-ready`:
  - archive status and no live body status;
  - impact declared;
  - every decision and design-harvest item mapped to an existing destination;
  - the component's architecture doc mapped (and fresh, per Q3);
  - for `new-component`, the catalog row and the component's architecture and onboarding docs;
  - untracked `RESIDUAL` rejected.

  The judgement parts stay with the intent-alignment lens (D22). Test-first.
- **D — Backfill.** The three plans get their design and decisions mapped, with any missing
  design written into canonical docs. Their history is annotated, not rewritten.

## Decisions

Permanent table; preserved intact across all lifecycle transitions (Draft → Ready → execution
→ closeout → archive). D5 and D10 were revised in place on 2026-10-25 for the owner's scope
change; D19–D25 and Q3 were added then.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Development mode? | `test-first`. | Packet sets it; no opt-out supplied. | ANSWERED — per packet 2026-10-25 |
| D2 | Autonomy for this run? | `supervised`. | Packet sets it; the Config Reg default (`autonomous`) does not apply. | ANSWERED — per packet 2026-10-25 |
| D3 | What is the root cause? | Closeouts were **skipped**, not done badly. There are three real failure shapes. (1) An unchanged move into the archive: #425, `8beb97ae`, R100, done "to satisfy KW-DOC-LIFECYCLE-003". (2) Added straight into the archive while Ready: #426, `6dd4ce3f`. (3) The closeout ran, recorded `RESIDUAL`, and the plan was archived anyway: #423. Item C is the primary fix. | Git evidence relayed by the conductor. The archived files corroborate every shape: all three carry `status: draft` and a body `**Status: Ready`; #423 §11 has two `RESIDUAL` rows; #425 §8 and #426 §11 are forecasts. | ANSWERED — conductor-relayed evidence 2026-10-25; file state verified in worktree |
| D4 | New rule id, or extend 003? | New id **`KW-DOC-LIFECYCLE-004`**, new class `ArchiveCloseoutValidator`. 003 is unchanged. | Rule ids are permanent: suppressions and SARIF baselines key on them, and 003 means "still in the active folder". `OpenWorkValidatorTests.ArchivedPlansAndSpecificationsAreClosed` stays true. `004` is unused anywhere. | ANSWERED — architect technical call |
| D5 | What counts as closeout evidence? (revised 2026-10-25) | Conditions C1–C11 in "Gate contract". C1–C6 cover archive status, no live body status, a Decisions table, a Closeout mapping table, mapping, and residuals. C7–C11 cover the declared impact, the Design harvest list, the component architecture link, `new-component` completeness, and freshness (Q3). One finding per entry names every failed condition and gives a hint. | Built from the three real shapes (D3) plus the owner's design-harvest scope. A loose "any heading containing Closeout" match was rejected, because #425's forecast §8 would satisfy it. `ADR waiver` alone maps nothing; this enforces the existing policy line "No ADR is NOT a documentation waiver". | ANSWERED — architect technical call; scope per owner direction relayed 2026-10-25 |
| D6 | When does 004 run? | Only under `docs validate --merge-ready`, beside 003. | Matches 003. The `docs-validate` gate in `review gates` stays without `--merge-ready`, because an active plan is legitimate on a branch. The post-closeout run (T14) and Q2 cover the after-review window. | ANSWERED — architect technical call (low risk) |
| D7 | Frontmatter status for archived documents | Accept `archived`, `complete` or `superseded` (case-insensitive). New closeouts write `status: archived` and a first body line `**Status: Archived <date> — …**`. The ontology's closed status vocabulary is **not** widened. | The loader excludes the `archive` segment (`OntologyConfig.cs:43-44`; `DocumentLoader.cs:65`), so archived documents are never schema-validated. That is why `archived`, `complete`, `completed` and `Archived` all occur today. | ANSWERED — architect technical call |
| D8 | Where do a spec's impact, design harvest and mapping live? | In the spec's `design.md`. Its frontmatter carries `documentation-impact` and `impact-components`; its body has `## Decisions`, `## Design harvest` and, at closeout, `## Closeout mapping`. Every document in the archived spec folder carries the archive status. | `design.md` exists in both spec layouts (this repo's folder-with-README layout and the host three-file layout). The harvest list and the mapping sit beside the design they cover. | ANSWERED — architect technical call |
| D9 | How does the validator read the archive? | Through the existing `DocumentLoader` parsing and the `DocsRootPath.EnumerateContainedFiles` symlink-safe walk, over `<docs-root>/archive/plans/` and `<docs-root>/archive/specs/` only. It also receives the live `DocumentSet` for the catalog and component checks. `EntryBelow` moves from `OpenWorkValidator` into `InventoryReachability` so both share it. | There is no second frontmatter parser. The default loader cannot be reused unchanged because it excludes the archive. `DocumentSet.Components` is already the parsed catalog vocabulary. | ANSWERED — architect technical call |
| D10 | `docs-dev` description wording (revised 2026-10-25) | Closeout and design harvest lead. Working text: "Closes out finished plans and specs: harvests their design, architecture, features and decisions into canonical docs (a new docs folder for a new component), then archives them. Also writes READMEs, API docs, ADRs, runbooks, and inline docs in Markdown/Mermaid. Use when a plan or spec needs its docs-dev closeout, or when the deliverable is a .md file. Do not use when the deliverable is a formal requirements/design/tasks spec to author (product-owner) rather than reference documentation or a closeout." The worker may refine it with the `writing-trigger-descriptions` skill but must keep the T4 pins. | The `DescriptionScorer` rubric gives it at least 95/100: trigger 35, negative boundary 20, opening 15, keywords 15, length 10–15 (about 500 chars). It passes `KW-AGENT-LINT-002` and is above the `KW-AGENT-LINT-001` floor of 50. The exclusion names spec **authoring**, so spec **closeout** routes to `docs-dev`. | ANSWERED — architect technical call |
| D11 | Move `docs-dev` off `model-profile: fast`? | **Not pursued.** It stays `fast`. | The evidence (D3) shows skipped closeouts, not weak ones; a bigger model does not fix skipping. The owner agrees. | ANSWERED — out of scope; owner position relayed by conductor 2026-10-25 |
| D12 | `docs/dash/architecture.md:359` links `../todo/kyberdash-opencode-plugin-live-verification.md`, which does not exist | Remove the dangling link. State the owner live gate as open and `[NOT LIVE-VERIFIED]`. Do not recreate the todo. | The #423 index row records that the owner-directed opencode todos were removed. Root `AGENTS.md` allows a todo only when the user accepts one. | ANSWERED — architect technical call |
| D13 | How do the backfills change archived history? | Annotate; do not rewrite. **Keep:** each Decisions table and every original section; the original status paragraph, unchanged, under the label "Pre-closeout status (preserved as history)". **Add:** the impact frontmatter keys; a new first body line `**Status: Archived <date> — closeout backfilled by plan 2026-10-25-docs-dev-harvest-closeout-gaps**`; a retroactive `## Design harvest` marked "(backfilled <date>)"; `## Completion summary`; `## Closeout mapping`. **Correct:** false index claims are marked corrected, as the #192 correction did. | Precedent: the #192 false-claim correction in `docs/archive/plans/2026-10-05-harvest-plan-decision-records.md`. 004 reads only the **first** `**Status:` line, so the history stays readable below it. | ANSWERED — architect technical call |
| D14 | Who edits agent/skill markdown and the migration digest? | `docs-dev` edits `products/kyber-squad/**/*.md` text. `test-dev` re-pins `products/kyber-squad/migration/docs-dev.md` `final-body-sha256`. | `LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest` asserts `final-body-sha256 == agent.BodyDigest`, the SHA-256 of the LF-normalized body. Any `docs-dev.md` body edit turns it RED. `docs-dev` has no process tool; `test-dev` reads the actual value from the failing assertion. | ANSWERED — architect technical call |
| D15 | Older partial mappings (`2026-10-09-issue-312`, `2026-10-10-kyberdash-claude-code-turn-content`, `2026-10-09-issue-319`) | Out of scope; listed in the baseline (Q1). A follow-up todo only if the owner accepts one. | Packet scope. | ANSWERED — per packet 2026-10-25 |
| D16 | The in-flight `squad-model-presets` spec | Out of scope. At its own closeout it must meet 004: impact keys and `## Design harvest` in `design.md`, then `## Closeout mapping`. Its `design.md` already has `## Decisions`. | Packet scope. Because the rule is new, its author should add the impact declaration before that spec's approve-and-execute. | ANSWERED — per packet 2026-10-25 |
| D17 | Which harness copies must change with canonical agent/skill text? | None are committed. At `a0862e7c`, `.github/` holds only `CODEOWNERS`, the PR template and `dependabot.yml`. Renderers project `products/kyber-squad` at `squad install/update`. The only propagated artifact is the migration record (D14). | The `.github/agents` tree that the 2026-10-05 plan edited has since been retired (Hotshot golden retirement, 2026-10-21). | ANSWERED — finding, verified in worktree |
| D18 | The `product-owner` **skill** description claims "…tasks, or closeout…", although `closeout-phase.md` says `docs-dev` performs closeout | Out of scope; follow-up. | The overlap is skill vs agent, and `closeout-phase.md` redirects to `docs-dev`. | ANSWERED — architect technical call; follow-up only |
| D19 | Scale vocabulary and where it is declared | Frontmatter on the plan (on a spec, its `design.md`). Required: `documentation-impact`, one of `new-component`, `extends-component` or `no-design-change`. Also `impact-components`, a list of catalog component names; it is required and non-empty for the first two scales. The existing required `component` key is unchanged and independent. For `new-component`, `impact-components` names a component the closeout adds to the catalog, so it is not checked against the catalog while the plan is a draft. | The owner asked for names "as you see fit". Frontmatter is machine-readable, survives archive, and is already tolerated: the shared deserializer uses `IgnoreUnmatchedProperties` (`MarkdownFrontmatterReader.cs:42-46`), which is how `development-mode` already lives there. `DocumentFrontmatter` gains two properties so 004 can read them. A plan's `component` must already be in the catalog (`KW-DOC-SPEC-004`), so it cannot name a component that does not exist yet; hence a separate key. | ANSWERED — architect technical call |
| D20 | Format of the `## Design harvest` list | A table, `\| ID \| Design item (section) \| Planned canonical destination \|`, with ids `H1…Hn`. **Plans:** one row per design, contract, interface, behaviour or operations section that has a durable effect. **Specs:** one row per `design.md` section and per requirement group. `no-design-change` replaces the table with one line containing `Documentation waiver: <reason>`. At closeout every `H` id is mapped exactly like a decision ID. | Giving each design item a stable id makes "the design was harvested" checkable in the same way as decisions, without the gate having to understand prose. Whether the list is complete stays a review judgement (D22). | ANSWERED — architect technical call |
| D21 | Who checks the declaration, and when? | **Author:** the architect (`plan-authoring.md`) or product-owner (`design-phase.md`) writes the scale, the impact components and the Design harvest list. **Conductor:** `PLAN_READY` / `SPEC_READY` checks require all three, and the conductor states the scale in the approve-and-execute question, so the owner confirms it there. **Closeout:** `docs-dev` closes out per the scale. **Gate:** 004 enforces it at archive time. There is no draft-time mechanical check. | The owner proposes and confirms; the planner only proposes. A draft-time validator rule would add a second check on active documents in every host for little gain, because 004 and the conductor checklist already cover it. Cheap to add later. | ANSWERED — architect technical call |
| D22 | Which parts stay review judgement (intent-alignment lens) rather than gate checks? | **Lens judgement:** • whether a mapped section truly describes the shipped design and feature set; • whether the Design harvest list covers every durable design section; • whether the chosen scale is right; • for `extends-component`, whether onboarding, runbook or reference needed updating (behaviour, setup or operations changed) and whether the catalog row needed updating (surface changed); • for `new-component`, whether `requirements.md` is required (application or service); • whether each waiver is valid. **Gate (004):** everything structural and existence-based in C1–C11. | The gate can verify that destinations exist, link the right component's architecture doc, and (per Q3) were reviewed at closeout. It cannot verify meaning. The lens text names each judgement item explicitly (T5). | ANSWERED — architect technical call |
| D23 | Scale for the three backfilled plans | All three `extends-component`: #423 and #425 → `[KyberDash]`; #426 → `[KyberSquad]`. Known missing design: `docs/kyber-squad/architecture.md` does not describe #426's canonical per-target overlay (`pi-extensions` / `pi-tools` keys and their Pi renderer lowering). It appears only in ADR 0019, `requirements.md` and `onboarding.md`, so T11 writes it. | Proposed here; the owner confirms at this plan's approve-and-execute (D21). #423/#425 design is already in `docs/dash/architecture.md` (the "OpenCode plugin span/turn envelope" section, lines 286-360), subject to per-item verification. | ANSWERED — architect proposal; owner confirms scale at approve-and-execute |
| D24 | This plan's own scale | `extends-component`, `[DocGraph, KyberSquad]`. **DocGraph:** `docs/docgraph/architecture.md` gains the archive read path, and `governance.md` the rule. **KyberSquad:** `docs/kyber-squad/architecture.md` gains a "Plan and specification lifecycle" section (impact, design harvest, closeout, digest), and `requirements.md` KS-008 is updated. | Precedent: `kyber-squad/architecture.md` §9 already documents conductor workflow contracts (the circuit-breaker). | ANSWERED — architect proposal; owner confirms scale at approve-and-execute |
| D25 | Components with no architecture or onboarding doc today | Out of scope to create them now. **No architecture doc:** `KyberUtilities` and `Distribution`. **No onboarding doc:** `ContextHygiene`, `ReviewCouncil` and `CI Pipelines`. Under C9, the first `extends-component` closeout for KyberUtilities or Distribution must create its architecture doc. | Verified from frontmatter (`doc-type`/`component`) across `docs/`. This is the owner's intent ("architecture docs must be updated"), applied when a component is next touched rather than as a sweep. | ANSWERED — architect technical call |
| Q1 | How does 004 avoid failing the ~115 legacy archived entries, and how do hosts adopt it? | **(a) Frozen baseline.** `ontology.archive-closeout.baseline: .kyber-weave/archive-closeout-baseline.txt` lists every archived plan entry and spec folder at `a0862e7c` except the three backfill targets. Every other archived entry, including every future archive, is checked. The rule is silent until a host adopts the key. The baseline file joins `policy.always-human`, and a stale baseline entry is a 004 error. | **(a) Frozen baseline — RECOMMENDED and chosen.** Pros: exact, fail-closed, works the same locally and in CI, covers specs, needs no git. Cons: a one-time ~113-line list; hosts must opt in. **(b) Date cutoff.** Heuristic: plans authored before the cutoff but archived later escape, and spec folders have no date. **(c) Git-scoped.** Adds git to `docs validate`. The `skill-docs-gate` job checks out at default depth (`ci.yml:506-515`), and any later edit to a legacy archived plan pulls it into scope. | ANSWERED — orchestrator technical call under the owner's standing rule (make low-risk technical calls and report them); the owner may override at approve-and-execute, 2026-10-25 |
| Q2 | Should the conductor and `docs-dev` also refuse an archive move that has no closeout digest? (Text only; defense in depth beside 004) | **(a) Yes.** `docs-dev` returns `STATUS: CLOSEOUT_COMPLETE` (`ARTIFACT`, `IMPACT`, `MAPPED: <n>/<n> ids`, `WAIVERS`, `VALIDATION`) or `STATUS: CLOSEOUT_BLOCKED` (`GAPS`). Only the `docs-dev` closeout task moves a plan or spec into the archive; any other worker that meets 003 routes the closeout to `docs-dev`. The conductor delivers only with `CLOSEOUT_COMPLETE` plus a post-closeout `docs validate . --merge-ready` run. | **(a) Yes — RECOMMENDED and chosen.** It makes "only a successful closeout completes the objective" enforceable, and it names the #425 shape (a review fix moved the plan). 004 stays the primary fix. **(b) No:** rely on 004 alone. | ANSWERED — orchestrator technical call under the owner's standing rule (make low-risk technical calls and report them); the owner may override at approve-and-execute, 2026-10-25 |
| Q3 | Freshness proxy: must mapped destinations show `last-reviewed` on or after the closeout date? | **(a) Component architecture docs only.** For `new-component` and `extends-component`, every architecture document linked under C9 for each impact component must have `last-reviewed` ≥ the plan document's `last-reviewed` (the closeout date). Other mapped destinations are not date-checked. **Known limit:** a date bump with no real content change passes the gate. The intent-alignment lens must judge whether the architecture content was actually updated with the new design and feature set (D22). | **(a) Component architecture docs only — RECOMMENDED and chosen.** For `new-component` and `extends-component`, every linked architecture doc of each impact component must have `last-reviewed` ≥ the archived plan's (or `design.md`'s) `last-reviewed`, which the closeout sets. Pros: targets the owner's core demand (architecture updated with the new design) with one date bump per component per closeout; `last-reviewed` already means "last verified". Cons: a date bump can be made without real content (the lens backs it up); it forces a re-verify-and-bump even when the architecture text was already right. **(b) Every mapped governed destination.** Pros: strongest signal. Cons: date churn on every doc a closeout touches, frequent frontmatter merge conflicts between parallel branches, and pressure to bump docs that were only cited, not changed. **(c) No freshness check.** Pros: no churn. Cons: an old, untouched architecture section can be "mapped" without anyone re-reading it. | ANSWERED — orchestrator technical call under the owner's standing rule (make low-risk technical calls and report them); the owner may override at approve-and-execute, 2026-10-25 |

## Design harvest

Planned canonical destinations for this plan's design. At closeout (T13), every `H` id and
every decision ID is mapped in `## Closeout mapping`.

| ID | Design item (section of this plan) | Planned canonical destination |
|---|---|---|
| H1 | The 004 rule: scope, conditions C1–C11, message and hint ("Gate contract") | `docs/docgraph/governance.md`; `docs/ci-pipelines/rule-reference.md` |
| H2 | The archive read path: only 004 reads the archive the loader excludes; shared entry grouping (D9) | `docs/docgraph/architecture.md` |
| H3 | Adoption config and the frozen baseline (Q1) | `docs/configuration.md`; `docs/docgraph/governance.md` |
| H4 | Documentation-impact declaration, the Design harvest list, and owner confirmation at approve-and-execute (D19–D21) | `docs/kyber-squad/architecture.md` (new "Plan and specification lifecycle" section); `docs/plans/README.md` and `docs/specs/README.md` policy |
| H5 | Closeout obligations by scale, mapping rules, waivers, archive status convention (D5, D7, D8) | `docs/kyber-squad/architecture.md`; `docs/plans/README.md`; `docs/specs/README.md`; `docs/docgraph/governance.md` |
| H6 | The closeout digest and the conductor delivery rule (Q2) | `docs/kyber-squad/architecture.md`; `docs/kyber-squad/requirements.md` (KS-008) |
| H7 | The split between gate checks and review-lens judgement (D22) | `docs/kyber-squad/architecture.md`; `docs/docgraph/governance.md` |

## Investigation findings

**Discovery:** `Discovery: codegraph 9, docs 2, direct reads 41` (plus about 61 grep/glob calls).
CodeGraph calls 1–8 answered from the main checkout (`9efb0288`); call 9 answered from this
worktree's own index, which the conductor built. Every cited line was confirmed in the
worktree. Direct reads of indexed code, with reasons:

- `DocsValidateCommand.cs` and `DocsValidateSettings.cs`: codegraph did not return the bodies.
- Test files, for exact patterns and worktree confirmation: `OpenWorkValidatorTests`, `SquadCanonicalContentTests`, `SquadSpecGateCollapseTests`, `SquadRustGoSpecialistTests`, `AgentGovernanceTests`.
- The `DocFixture` grep: to confirm that fixtures never load the archive.
- Confirmation greps in `OntologyConfig.cs`, `DocumentLoader.cs` and `OntologyYamlSection.cs`.

Everything else read directly is not in either index: markdown under `products/`, the
catalog's columns, config, CI, and archived plans.

1. **Why the archive holds non-vocabulary statuses.** `OntologyConfig.DefaultStatuses` is
   `current, draft, needs-review, superseded` (`OntologyConfig.cs:30-31`). The default
   excluded segments start with `archive` (`:41-44`), and `DocumentLoader.Load` skips excluded
   paths (`DocumentLoader.cs:65`), so no archived document is ever validated. Statuses
   observed in the archive: `archived`, `complete`, `completed`, `Archived`, `superseded`, and
   `draft` (the three targets only).
2. **How merge-ready works today.** `DocsValidateCommand.Execute` (`:27-33`) loads one
   `DocumentSet` (archive excluded). It runs validators 001 and 002 always, and 003 only with
   `MergeReady`. CI runs `docs validate . --merge-ready --format table` (`ci.yml:580-585`, job
   `skill-docs-gate`, default-depth checkout). The `docs-validate` gate in `review gates` has
   no `--merge-ready` (`kyber-weave.yml:90-92`).
3. **Test fixtures never see the archive.** `DocFixture.Load()` uses the default loader. The
   new validator reads the archive itself, and its tests assert on files the fixture writes
   there.
4. **Frontmatter keys.** `MarkdownFrontmatterReader.Deserializer` ignores unmatched properties
   (`:42-46`). `DocumentFrontmatter` (`DocumentModel.cs:48`) needs two new properties for the
   impact keys, and the hyphenated naming convention binds `documentation-impact` and
   `impact-components`.
5. **Catalog and component docs.** Only the catalog's Component (index 1) and Owner (index 6)
   columns are parsed (`catalog.md` "How the columns are read"), and `DocumentSet.Components`
   carries that vocabulary.

   | Component | `architecture` doc | `onboarding` doc |
   |---|---|---|
   | KyberDash | yes | yes |
   | DocGraph | yes | yes |
   | KyberSquad | yes | yes |
   | ContextHygiene | yes (`agents.md`, `skills.md`) | no |
   | ReviewCouncil | yes | no |
   | CI Pipelines | yes | no |
   | KyberUtilities | no | yes |
   | Distribution | no (`reference` + `runbook` at docs root) | no |

   This is D25. The ontology requires `component` on architecture, onboarding, requirements
   and runbook docs, plus `source-root` on onboarding docs.
6. **app-docs-standard.** A source-root README overview; `onboarding.md`, `architecture.md`
   and `requirements.md` for applications and runnable services; a catalog update for a new
   or materially changed component. It is the procedure `docs-dev` follows for
   `new-component`.
7. **No committed harness copies** (D17). The only propagated artifact is the migration
   digest (D14).
8. **Existing text pins.** `LoadDocsDevDefinesCloseoutMappingTableAndWaiverConventions`
   (`SquadCanonicalContentTests.cs:543-553`) pins "Plan closeout", "mapping table",
   "ADR waiver" and "Documentation waiver"; keep those phrases.
   `LoadArchitectAndConductorDefinePermanentDecisionTableContract` (`:555-569`) pins the
   five Decisions columns and `NO_QUESTIONS`. Precedents for new pins:
   `SquadRustGoSpecialistTests` (descriptions) and `SquadSpecGateCollapseTests` (references).
9. **Description gates for agents.** `KW-AGENT-LINT-002` fires when the trigger regex does not
   match. `KW-AGENT-LINT-001` fires when the `DescriptionScorer` total is below 50.
   `skill lint --min-desc-score 70` gates only `.apm/skills/*`, so the new test pins ≥ 70
   itself.
10. **Spec and conductor gaps.**
    - `design-phase.md`: no Decisions, impact or harvest requirement.
    - `closeout-phase.md` and `docs-dev.md` §Specification closeout: no mapping and no
      waivers.
    - `spec-path.md:18` (`SPEC_READY`) and `plan-path.md:18` (`PLAN_READY`) check no impact
      declaration.
    - `intent-alignment.md:49-54`: plans only, decisions only.
    - `execution-and-review.md:88-95`: no mapping for spec-backed closeout.
    - `docs/specs/README.md`: no policy section.
    - `kyber-squad/requirements.md:33` KS-008 frames the closeout as maintaining docs, not as
      harvesting design.
11. **Backfill target facts** (verified in the worktree).
    - **#423 (KyberDash).** IDs: D1–D7, Q1–Q3, AP1, R1, R2. Design sections: §3.1 placement,
      §3.2 data flow, §3.3 privacy, §3.4 failure modes. They are expected in
      `docs/dash/architecture.md` lines 286-360, whose last paragraph links a missing todo
      (D12). The index row is stale ("RESIDUAL … no section landed").
    - **#425 (KyberDash).** IDs: D1–D7, AP1. Design sections: §3.1–3.3 (envelope v2 shape).
      §8 is a forecast. The index row's claim "mapped in the plan's §8 Closeout" is false.
      Likely gaps: D6 (the `opencode.context.skills` listing source is absent from the
      "Skills view contract" at `architecture.md:747`) and D1 (the standalone 8080 plugin was
      rejected).
    - **#426 (KyberSquad).** IDs: D1–D11. Design: the canonical per-target overlay keys and
      their Pi lowering (§1.2, D7–D10). These appear in ADR 0019, `requirements.md` and
      `onboarding.md`, but **not** in `docs/kyber-squad/architecture.md` (D23). The index row
      is malformed (no Archive Date or canonical-docs cell). Archive date **2026-10-24**: PR
      #426 merged 2026-10-24T13:10:37Z as merge commit `0351a45c`, a squash of `6dd4ce3f`
      (conductor-relayed from git, 2026-10-25).
12. **Archive inventory at `a0862e7c`.** `docs/archive/plans/` holds 115 entries: plan files,
    one `.json` evidence file, and the folder `2026-09-06-kyberdash-spine-tasks/`.
    `docs/archive/specs/` holds two spec folders, `kyberdash/` and
    `kyberdash-context-surfaces/`. (`archive/specs/kyberdash/` is missing from
    `docs/specs/README.md`; that is an out-of-scope observation.)
13. **Pre-revision checks** (run by the conductor in this worktree, 2026-10-25):
    `docs validate .` and `docs drift .` both returned no findings; the `skill-docs-gate`
    checkout is default depth; the archive exclusion is at `OntologyConfig.cs:43-44`. This
    revision adds frontmatter keys and sections to this plan, so both checks must be re-run.

## Gate contract (`KW-DOC-LIFECYCLE-004`)

**Rule.** `KW-DOC-LIFECYCLE-004` — Error — an archived plan or specification lacks closeout
evidence. It is raised only by `docs validate --merge-ready` (D6), and only once the host
adopts `ontology.archive-closeout` with its baseline file (Q1).

**Scope.**

- **Entries:** everything directly below `<docs-root>/archive/plans/` and
  `<docs-root>/archive/specs/`, where `<docs-root>` is `OntologyConfig.DocsRoot`.
- **Plan entries:** a `*.md` file, or the `README.md` of a folder entry.
- **Spec entries:** a folder; its *design document* is `design.md`.
- **Plan document:** the plan file, or `design.md` for a spec. The conditions below read
  this document.
- **Skipped:** non-markdown files, and entries listed in the baseline.
- **Stale baseline:** a baseline line naming a missing path is itself a 004 error.
- **Live corpus:** the catalog and component checks read the live (non-archived)
  `DocumentSet`.

**Conditions** (each failure is named in the entry's single finding):

1. **C1 Archive status.** The frontmatter `status` of every document in the entry is
   `archived`, `complete` or `superseded` (D7).
2. **C2 No live body status (plans).** The first body line that starts `**Status:` does not
   continue with `Draft`, `Ready`, `In progress`, `Blocked` or `Review required`.
3. **C3 Decisions table.** A `##` heading `Decisions` (an `N.` prefix is optional) whose section
   holds a table with at least one body row. The first-cell ids are taken with `**` and
   backticks stripped and must match `^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*$`.
4. **C4 Closeout mapping table.** A `##` heading `Closeout mapping` (an `N.` prefix is
   optional; case-insensitive) whose section holds a table.
5. **C5 Every id mapped.** Every decision id (C3) and every design-harvest id (C8) is mapped.
   A mapping row maps the ids listed in its first cell; comma-separated ids are allowed,
   ranges such as `D1–D7` are **not** expanded. The rest of the row must contain either:
   - (i) a relative link that resolves (`DocGraphProjection.ResolveLink`) to an existing file
     outside `<docs-root>/archive/`; or
   - (ii) the phrase `Documentation waiver`.

   A row containing `RESIDUAL` maps nothing. `ADR waiver` alone maps nothing.
6. **C6 Residuals tracked.** Every line in the Closeout mapping section that contains
   `RESIDUAL` also links an existing document inside the `todo-index` folder.
7. **C7 Impact declared** (D19). `documentation-impact` is `new-component`,
   `extends-component` or `no-design-change`. For the first two, `impact-components` is
   non-empty.
8. **C8 Design harvest.** A `##` heading `Design harvest` exists.
   - For `new-component` / `extends-component`, it holds a table with at least one id row.
     Those ids join C5.
   - For `no-design-change`, it contains `Documentation waiver`.
9. **C9 Architecture mapped.** For `new-component` / `extends-component`, the Closeout
   mapping links, for **each** impact component X, at least one live document with
   `doc-type: architecture` and `component: X`.
10. **C10 New component complete.** For `new-component`, each impact component X:
    - has a catalog row (X is in `DocumentSet.Components`);
    - has live documents with `doc-type: architecture` and with `doc-type: onboarding`,
      both with `component: X`;
    - has a `README.md` in the architecture document's folder.
11. **C11 Freshness (Q3 = a).** Every architecture document linked under C9 has
    `last-reviewed` on or after the plan document's `last-reviewed`, which the closeout sets.
    Other mapped destinations are not date-checked. Known limit: a date bump with no content
    change passes, so the intent-alignment lens judges the content.

**The three real shapes (D3)** fail C1, C2, C4, C5, C7 and C8; #423 also fails C6. Each test
below isolates its distinguishing condition with every other condition satisfied.

**Message and hint.** `Archived plan '<entry>' lacks closeout evidence: <conditions>.` The
hint names the fix, for example:

- "Add `## Closeout mapping` rows for D3, H2"
- "Link docs/<x>/architecture.md for component X"
- "Add a catalog row for X"
- "Set `status: archived`"

**Stays review judgement** (intent-alignment lens; D22):

- whether the mapped content is true and complete;
- whether the Design harvest list is complete;
- whether the scale is right;
- for `extends-component`, onboarding, runbook, reference and catalog-row updates;
- for `new-component`, `requirements.md`;
- whether each waiver is valid.

## Test contract

Runner prefix for every .NET row: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj
-c Release --filter <filter>`, after `dotnet build KyberWeave.sln -c Release`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | No-test task (API skeleton) | Release build + full `dotnet test` | `ArchiveCloseoutValidator(string repoRoot, KyberWeaveConfig config)` with `MissingCloseoutEvidence = "KW-DOC-LIFECYCLE-004"`; `Validate(DocumentSet live)` returns an empty report. `DocumentFrontmatter.DocumentationImpact` / `ImpactComponents` exist. The config key parses but has no effect. Nothing is wired. | n/a. The skeleton lets T2 fail on behavior, not as a compile error that would block T4/T7 in the same project. | 0 warnings; existing suite unchanged and green. |
| T2 | New `tests/KyberWeave.Tests/ArchiveCloseoutValidatorTests.cs`; additions to `OntologyConfigTests.cs` and `DocGovernanceTests.cs` (frontmatter binding) | `--filter "FullyQualifiedName~ArchiveCloseoutValidatorTests"`, `~OntologyConfigTests`, `~DocGovernanceTests` | **Real shapes:** (1) `AnUnchangedMoveIntoTheArchiveIsKWDOCLIFECYCLE004` (#425); (2) `APlanArchivedWhileItsBodySaysReadyFails` (C2, #426); (3) `AnArchivedPlanWithAnUntrackedResidualFails` (C6, #423).<br>**C1–C6:** (4) `ADecisionRowMarkedResidualIsNotMapped`; (5) `ADraftFrontmatterStatusFails`; (6) `AMissingDecisionsSectionFails`; (7) `AMissingCloseoutMappingSectionFails` (a forecast `## Review and closeout` does **not** satisfy C4); (8) `AnUnmappedDecisionIdIsNamedInTheHint`; (9) `AnAdrWaiverAloneDoesNotMapAnId`; (10) `ADocumentationWaiverMapsAnId`; (11) `AMappingLinkMustResolveToAnExistingNonArchivedFile`; (12) `AResidualLinkedToAnExistingTodoPasses`; (13) `ACommaSeparatedIdCellMapsEachIdButARangeDoesNot`.<br>**C7–C11:** (14) `AMissingDocumentationImpactFails`; (15) `AnUnknownImpactValueFails`; (16) `AnImpactWithoutComponentsFails`; (17) `AMissingDesignHarvestSectionFails`; (18) `AnUnmappedDesignHarvestIdIsNamedInTheHint`; (19) `NoDesignChangeNeedsAWaiverLineAndNoArchitectureLink`; (20) `ExtendsComponentWithoutTheComponentsArchitectureLinkFails`; (21) `AnArchitectureDocOfAnotherComponentDoesNotCount`; (22) `NewComponentWithoutACatalogRowFails`; (23) `NewComponentWithoutAnOnboardingDocFails`; (24) `NewComponentWithoutAFolderReadmeFails`; (25) `AStaleArchitectureDocFails` (C11).<br>**Pass controls:** (26) `ACompleteExtendsComponentCloseoutPasses` (`archived` and `complete` both accepted); (27) `ACompleteNewComponentCloseoutPasses`; (28) `ACompleteNoDesignChangeCloseoutPasses`.<br>**Specs:** (29) `AnArchivedSpecificationFolderIsOneFindingReadFromDesign`; (30) `ACompleteArchivedSpecificationPasses`.<br>**Scope:** (31) `ABaselinedEntryIsSkipped`; (32) `AStaleBaselineEntryIsAnError`; (33) `AHostThatHasNotAdoptedTheRuleSeesNothing`; (34) `DocsValidateReportsMissingCloseoutEvidenceOnlyWithMergeReady` (a theory mirroring `DocsValidateReportsOpenWorkOnlyWithMergeReady`).<br>**Other:** config tests (key parses; a missing baseline file is a config error; an entry outside the archive is rejected); a frontmatter test (`documentation-impact` and `impact-components` bind). | Every new test fails against the T1 skeleton, with its failing assertion recorded. The pass controls (26)–(28), (30), (31) and (33) must be shown failing first, so that they discriminate. | Same tests pass unweakened; `OpenWorkValidatorTests` green and unedited. Test (25) date-checks architecture docs only (Q3 = a), and a control asserts that a stale non-architecture destination does **not** fail. |
| T3 | Same as T2 | Same as T2, then the full suite | Behavior per the Gate contract. | T2 RED. | All T2 tests green; full suite green; 0 warnings. |
| T4 | New `tests/KyberWeave.Tests/SquadCloseoutHarvestContractTests.cs` | `--filter "FullyQualifiedName~SquadCloseoutHarvestContractTests"` | **Description:** pins the phrases listed under T5/T6. `DescriptionScorer` total ≥ 70. `AgentSyncLinter.LintSet` over an `AgentModel` raises no `KW-AGENT-LINT-001`/`-002`. "closeout" appears before "README", and "design" and "architecture" both appear.<br>**Body:** the first `You own:` bullet contains `closeout`. | Against the unedited tree, every closeout-first, design-harvest, impact, spec-parity and digest assertion fails. The score assertion passes today (it is a regression guard); say so in the evidence. | Same tests pass after T5/T6 with no test edit. |
| T5, T6 | Text only | — (verified by T7) | — | T4 RED. | T7 green. |
| T7 | T4's file + `SquadCanonicalContentTests` | `--filter "FullyQualifiedName~SquadCloseoutHarvestContractTests"`; `~SquadCanonicalContentTests` | Pins green. The digest test is green after the re-pin; the existing closeout-mapping and Decisions pins stay green. | The digest test fails after T5 (expected the old `6b0c5703…`); record the actual value. | Both classes green; `final-body-sha256 == BodyDigest`; full suite green. |
| T8, T9 | No-test tasks (canonical docs) | `docs validate .` + `docs drift .` (in T12) | DocGraph and KyberSquad canonical docs describe the shipped design truthfully. | n/a | Zero findings (T12); `governance.md` `code-refs` resolves `ArchiveCloseoutValidator`. |
| T10 | Real corpus | `dotnet run --project src/KyberWeave.Cli -c Release --no-build -- docs validate . --merge-ready --format json` | Adoption on the real archive. | **Real-corpus RED:** exactly three `KW-DOC-LIFECYCLE-004` findings (the backfill targets) plus one `KW-DOC-LIFECYCLE-003` for this active plan. | Same command after T11 (T12). |
| T11 | No-test task (backfill) | — (T12) | Three closeouts meet C1–C11. | T10 RED. | T12. |
| T12 | Real corpus | `docs validate . --merge-ready` + `docs drift .` | Gate green on the real archive. | T10. | Zero 004 findings; only 003 for this active plan; zero drift findings. |
| T13 | No-test task (closeout, dogfood) | — (T14) | This plan meets C1–C11 and is not baselined. | n/a | T14. |
| T14 | Real corpus + gates | `docs validate . --merge-ready` + `docs drift .` | Corpus clean after this plan is archived. | n/a | Zero findings of any rule. |

Changing an approved Test-contract row, including weakening an assertion to reach green, is a
scope change: the plan returns to Draft and the conductor relays reapproval.

## Tasks

Required skills are named, not agents. Every developer task ends with the worker's own
`task-gate` run; a failing gate goes straight back to the same worker.

### T1 — Gate API skeleton (no behavior)

- **Objective:** Let T2's tests compile so their RED is behavioral.
- **Files:**
  - New `src/KyberWeave.Core/Docs/Validation/ArchiveCloseoutValidator.cs` (signature per the T1 row, with `<remarks>` giving the why: D3, D4).
  - `src/KyberWeave.Core/Docs/Model/DocumentModel.cs`: `DocumentFrontmatter.DocumentationImpact` (string?) and `ImpactComponents` (list).
  - Config plumbing per Q1: `OntologyYamlSection.ArchiveCloseout` (`baseline`), `OntologyConfigLoader.Merge`, and `OntologyConfig.ArchiveCloseout` (null = not adopted).
- **Acceptance:** Per the T1 row.
- **Dependencies:** none.
- **Required skills:** `csharp-dev`; **<csharp-coding-standard>**.

### T2 — RED: gate tests

- **Objective:** Author the T2 tests.
- **Files:**
  - New `tests/KyberWeave.Tests/ArchiveCloseoutValidatorTests.cs`. Use `DocFixture` in the builder style of `OpenWorkValidatorTests`. Adopting fixtures write `.kyber-weave/kyber-weave.yml` with `ontology: { docs-root: 6-Docs, archive-closeout: { baseline: … } }`. Catalog rows come from `WithCatalog()` plus fixture rows for the new-component cases.
  - Additions to `OntologyConfigTests.cs` and `DocGovernanceTests.cs`.
- **Acceptance:** Per the T2 row; the project compiles; `OpenWorkValidatorTests` is untouched.
- **Dependencies:** T1.
- **Required skills:** `test-dev`; **<test-coding-standard>**.

### T3 — GREEN: gate implementation and wiring

- **Objective:** Implement C1–C11.
- **Files:**
  - `ArchiveCloseoutValidator.cs`: the archive walk (D9); the conditions with hints; C9–C11 against the live `DocumentSet`.
  - `InventoryReachability.cs`: receives `EntryBelow`; `OpenWorkValidator.cs` now calls it there.
  - `DocumentLoader.cs`: only a narrowly scoped internal entry point if parsing the archive needs one; default exclusions unchanged.
  - `DocsValidateCommand.cs`: under `MergeReady`, `new ArchiveCloseoutValidator(settings.Path, config.WithOntology(ontology)).Validate(set)`.
  - `DocsValidateSettings.cs`: the `--merge-ready` description names 003 and 004.
  - `<remarks>`: why the archive is read here; why `ADR waiver` alone maps nothing; why a freshness proxy.
- **Acceptance:** T2 tests green; full suite green; 0 warnings; no new dependency.
- **Dependencies:** T2.
- **Required skills:** `csharp-dev`; **<csharp-coding-standard>**.

### T4 — RED: authoring, closeout and routing text pins

- **Objective:** Pin the A/B/Q2 text contract before it is written.
- **Files:** New `tests/KyberWeave.Tests/SquadCloseoutHarvestContractTests.cs`.
  - Follow the `SquadSpecGateCollapseTests` `Read(...)` / `AssertAll` pattern.
  - Load the `docs-dev` description and body with `SquadSourceLoader.Load`.
  - Section-scoped pins take the text between a heading and the next `## `.
  - Pins are `Ordinal` unless the T4 row says otherwise.
  - The phrases are listed in T5 and T6.
- **Acceptance:** RED per the T4 row.
- **Dependencies:** none (separate file; compiles independently of T2).
- **Required skills:** `test-dev`; **<test-coding-standard>**.

### T5 — GREEN: closeout-side text (docs-dev, closeout phase, lens, conductor closeout)

- **Objective:** Rewrite closeout as a design harvest with scale obligations.
- **Files and pinned phrases:**
  - `products/kyber-squad/agents/docs-dev.md`:
    - **(A) Description** per D10. Pins: `Use when`, `Do not use when`, `design`, `architecture`, `decision`, `README`, `ADR`, `runbook`, `product-owner`, and "closeout" before "README". `model-profile` unchanged (D11).
    - **(A) Scope:** closeout bullets come first. The first `You own:` bullet contains `closeout` and `design harvest`.
    - **Plan closeout:** keep `Plan closeout`, `mapping table`, `ADR waiver` and `Documentation waiver`. Name `## Closeout mapping` (one row per decision ID and per `## Design harvest` id; a relative link or `Documentation waiver`). Obligations by scale, naming `new-component`, `extends-component`, `no-design-change`, `catalog row`, `app-docs-standard`, `architecture.md` and `onboarding.md`. State the `status: archived` rule and the first body line `**Status: Archived`. Keep "do not archive".
    - **(B) Specification closeout:** the same, read from `design.md`, plus `requirement group`.
    - **(Q2) Completion digest:** `STATUS: CLOSEOUT_COMPLETE` (with an `IMPACT:` line) and `STATUS: CLOSEOUT_BLOCKED`.
  - `products/kyber-squad/skills/product-owner/references/closeout-phase.md`: obligations by scale; `## Closeout mapping` covering `design.md` sections, requirement groups and decision IDs; `ADR waiver`; `Documentation waiver`; "do not archive". The digest gains `MAPPED:` and `IMPACT:`.
  - `products/kyber-squad/skills/product-owner/references/tasks-phase.md` (:40): the closeout task names the scale obligations and the design-harvest mapping.
  - `products/kyber-squad/skills/code-review/references/lenses/intent-alignment.md` (:49-54):
    - "archived plan or specification";
    - list the D22 judgement items;
    - a `critical` finding for: a missing component architecture update; a scale that contradicts the delivered change; a draft/Ready status, a missing `## Closeout mapping` or an untracked `RESIDUAL` on an archived artifact.
  - `products/kyber-squad/agents/conductor/references/execution-and-review.md` (:88-95): closeout performs the design harvest per the declared scale. Q2 rules: only the `docs-dev` closeout archives; a worker meeting 003 routes to closeout; `CLOSEOUT_COMPLETE` plus a post-closeout `docs validate . --merge-ready`.
  - `products/kyber-squad/agents/conductor/references/autonomy.md` (:44): delivery requires `CLOSEOUT_COMPLETE`.
  - The `conductor.md` body is not edited (its digest is unaffected).
- **Acceptance:** T7 green (with T6). No other files.
- **Dependencies:** T4.
- **Required skills:** `kyber-weave-docs`, `writing-trigger-descriptions`.

### T6 — GREEN: authoring-side text and policy READMEs

- **Objective:** Declare the impact and the design harvest when plans and specs are written; spec Decisions parity.
- **Files and pinned phrases:**
  - `products/kyber-squad/agents/architect/references/plan-authoring.md`, "Required plan contract":
    - frontmatter `documentation-impact` (the three values) and `impact-components`;
    - a `## Design harvest` table (`H` ids, design item, planned canonical destination) with the `no-design-change` waiver line;
    - "the owner confirms the scale at approve-and-execute";
    - keep the existing Decisions pins.
  - `products/kyber-squad/skills/product-owner/references/design-phase.md`:
    - a permanent `## Decisions` table with the five columns (`ID`, `Question`, `Answer / decision`, `Mitigating and supporting information`, `Status / approval provenance`) and "preserved intact";
    - the same impact frontmatter on `design.md`;
    - a `## Design harvest` covering `design.md` sections and requirement groups.
  - `products/kyber-squad/agents/conductor/references/plan-path.md` (:18) and `spec-path.md` (:18, :21): `PLAN_READY` / `SPEC_READY` verify `documentation-impact`, `impact-components` and `## Design harvest` (spec: plus the Decisions table with `Mitigating and supporting information`). The approve-and-execute question states the scale and components for the owner to confirm.
  - `docs/plans/README.md`, policy section only: rename it "Plan Decisions, Design Harvest and Closeout Policy"; add the scale obligations, `## Design harvest`, `## Closeout mapping`, `KW-DOC-LIFECYCLE-004`, and the D7 status convention. Rows untouched.
  - `docs/specs/README.md`: a "Specification Decisions, Design Harvest and Closeout Policy" section mirroring it.
- **Acceptance:** T7 green. Both READMEs pass `docs validate` (T12).
- **Dependencies:** T4.
- **Required skills:** `kyber-weave-docs`.

### T7 — GREEN verification and migration digest re-pin

- **Objective:** Prove T5 and T6; re-pin the digest.
- **Files:** `products/kyber-squad/migration/docs-dev.md` (`final-body-sha256` only).
- **Acceptance:** Per the T7 row. A failing pin goes back to the T5 or T6 worker; the test is not edited.
- **Dependencies:** T5, T6.
- **Required skills:** `test-dev`.

### T8 — DocGraph canonical docs (H1–H3, H5, H7)

- **Objective:** Document 004 where the gates and DocGraph architecture live.
- **Files:**
  - `docs/docgraph/governance.md`:
    - a rule row;
    - rewrite the 003 "not semantic proof" sentence to point at 004;
    - C1–C11, the D7 convention, adoption and the baseline, and the gate-vs-lens split;
    - `code-refs` gains `ArchiveCloseoutValidator` and `OpenWorkValidator`;
    - bump `last-reviewed`.
  - `docs/docgraph/architecture.md`: the archive read path (H2).
  - `docs/ci-pipelines/rule-reference.md`: a 004 row.
  - `docs/configuration.md`: the `ontology.archive-closeout` key.
- **Acceptance:** Truthful to T3's behavior; zero findings in T12.
- **Dependencies:** T3.
- **Required skills:** `kyber-weave-docs`.

### T9 — KyberSquad canonical docs (H4–H7)

- **Objective:** Canonical home for the new lifecycle design (D24).
- **Files:**
  - `docs/kyber-squad/architecture.md`: a new section, "Plan and specification lifecycle: documentation impact, design harvest, closeout", covering the impact keys, Design harvest, scale obligations, mapping and waivers, the closeout digest and delivery rule, and the gate-vs-lens split.
  - `docs/kyber-squad/requirements.md` (:33): KS-008 restated as the design harvest.
- **Acceptance:** Matches the T5/T6 text; zero findings in T12.
- **Dependencies:** T5, T6.
- **Required skills:** `kyber-weave-docs`.

### T10 — Adopt the gate; record real-corpus RED

- **Objective:** Turn 004 on and prove it catches the three real failures.
- **Files:**
  - `.kyber-weave/kyber-weave.yml`: `ontology.archive-closeout.baseline`, and the baseline path added to `policy.always-human` with a comment giving the reason.
  - New `.kyber-weave/archive-closeout-baseline.txt`: every entry under `docs/archive/plans/` and `docs/archive/specs/` at `a0862e7c` except the three backfill targets, with a header comment citing this plan and Q1.
  - `.github/workflows/ci.yml:580-581`: the comment names 003 and 004 (comment only).
- **Acceptance:** The T10 real-corpus RED, recorded verbatim.
- **Dependencies:** T3.
- **Required skills:** `github-devops`.

### T11 — Backfill the three closeouts as design harvests (D)

- **Objective:** Real closeouts per D13 and D23.
- **Files:**
  - The three archived plans.
  - `docs/dash/architecture.md`, `docs/dash/telemetry-inventory.md`, `docs/dash/runbook.md`.
  - `docs/kyber-squad/architecture.md` (the Pi per-target overlay section), `docs/kyber-squad/requirements.md`, `docs/kyber-squad/onboarding.md`.
  - `docs/adr/0019-pi-native-subagents-and-primary-lowering.md`, as needed.
  - `docs/plans/README.md`: the three rows only.
- **Work:**
  1. **Frontmatter:** add `documentation-impact: extends-component` and `impact-components` (D23); set `status: archived` and `last-reviewed`.
  2. **Status line:** insert the new first status line and label the history (D13).
  3. **Design harvest:** add a retroactive `## Design harvest` listing each plan's design sections (#423 §3.1–3.4; #425 §3.1–3.3; #426 §1.2 and the D7–D10 mechanism).
  4. **Mapping:** map every `H` id and decision ID (13 + 8 + 11 decisions) to the exact canonical section, after reading it in the worktree. Where content is missing, write it rather than waive it: the Pi overlay in `kyber-squad/architecture.md` (D23); #425 D6 in the Skills view contract; #425 D1. Process and approval rows (mode, autonomy, intake route, AP/R approvals, #426 D4 VOID, #426 D11 test placement) may take a reasoned `Documentation waiver`.
  5. **Freshness:** re-verify and bump `last-reviewed` on `docs/dash/architecture.md` and `docs/kyber-squad/architecture.md` (C11).
  6. **Link fix:** fix `docs/dash/architecture.md:359` per D12.
  7. **Completion summary:** add `## Completion summary` (from each plan's own evidence) and `## Closeout mapping`.
  8. **Index rows:**
     - #423: replace "RESIDUAL … no section landed" with the landed section, annotated "corrected".
     - #425: annotate the false "mapped in §8" claim as corrected.
     - #426: rebuild the row with the archived-table columns, with Archive Date `2026-10-24` (PR #426 merge `0351a45c`, 2026-10-24T13:10:37Z).
  9. **Open items:** #426's deployment gap and the owner live gates stay open owner items (`[NOT LIVE-VERIFIED]`), not `RESIDUAL`, with no todo unless the owner accepts one.
- **Acceptance:** Each plan meets C1–C11, proven in T12; no Decisions table or original section altered beyond D13.
- **Dependencies:** T6 (shared `docs/plans/README.md`; final conventions), T9 (shared `docs/kyber-squad/architecture.md` and `requirements.md`), T10 (real-corpus RED first).
- **Required skills:** `kyber-weave-docs`, `app-docs-standard`.

### T12 — Real-corpus GREEN

- **Acceptance:** Per the T12 row, plus the full .NET suite green.
- **Dependencies:** T7, T8, T11.
- **Required skills:** `test-dev`.

### Review

`code-reviewer` runs once over the whole change after T12. The change touches the
always-human paths `products/kyber-squad/agents/**` and `.kyber-weave/kyber-weave.yml`, so the
verdict will be `NEEDS_HUMAN` under `KW-REVIEW-008`. In supervised mode that is terminal until
the owner reviews.

### T13 — `docs-dev` closeout of this plan (dogfood)

- **Objective:** Close this plan under the rules it creates.
- **Files:**
  - This plan.
  - `docs/plans/README.md` (this row).
  - Canonical docs as the mapping requires (H1–H7 destinations).
- **Work:**
  1. Map every `H` id and every decision ID, including the AP1 approval row added at finalization.
  2. Re-verify and bump `last-reviewed` on `docs/docgraph/architecture.md` and `docs/kyber-squad/architecture.md` (C11).
  3. Set `status: archived` and the first body line.
  4. Move the plan to `docs/archive/plans/`, **not** baselined.
  5. Return `STATUS: CLOSEOUT_COMPLETE`.
- **Dependencies:** review resolved (APPROVE, or the owner's resolution of `NEEDS_HUMAN`).
- **Required skills:** `kyber-weave-docs`.

### T14 — Post-closeout gate run

- **Acceptance:** Per the T14 row.
- **Dependencies:** T13.
- **Required skills:** `test-dev`.

## Dependency graph

```text
T1 ─► T2 ─► T3 ─┬─► T8 ───────────────────────────┐
                └─► T10 ─────────────┐            │
T4 ─┬─► T5 ─┬─► T7 ──────────────────┼────────────┼─► T12 ─► Review ─► T13 ─► T14
    └─► T6 ─┴─► T9 ─► T11 ◄──────────┘            │
                      └───────────────────────────┘
```

- **T7** waits on T5 and T6.
- **T9** waits on T5 and T6.
- **T11** waits on T6, T9 and T10.
- **T12** waits on T7, T8 and T11.
- **Waves:**
  1. T1, T4
  2. T2, T5, T6
  3. T3, T7, T9
  4. T8, T10
  5. T11

  At most three workers are ready at once (≤ `review.max-concurrent-workers: 4`).
- **File scopes** never overlap between concurrently ready tasks:
  - `docs/plans/README.md`: T6 → T11 → T13.
  - `docs/kyber-squad/*`: T9 → T11 → T13.

## Risks

| Risk | Mitigation |
|---|---|
| Always-human paths force `NEEDS_HUMAN` (`KW-REVIEW-008`). | Expected. The owner reviews before T13. Reserved-path escalation bypasses the remediation loop (`execution-and-review.md:86`). |
| The gate rejects legitimate closeouts over heading or table variants. | Each section has one canonical heading, pinned by tests (7), (17) and (26)–(28). Hints name the exact fix, and the `docs-dev` text names the headings. |
| Freshness is gamed (date bumped without content). | Q3 limits the check to architecture docs. The intent-alignment lens judges content (D22). |
| Components with no architecture doc (KyberUtilities, Distribution) block their first `extends-component` closeout. | Intended (D25): that closeout creates the doc. Hints say so. |
| The baseline is used to silence the gate. | The baseline file is always-human; stale entries are errors; new archives are never baselined (T13 dogfoods this). |
| Host repositories break on upgrade. | The rule is silent until adopted (test 33). |
| Plans already in flight lack the impact keys (for example `squad-model-presets`). | 004 checks only at archive time, so the author adds the keys before closeout (D16). |
| Closeout happens after review. | T14 runs `--merge-ready` after closeout; Q2 makes that part of delivery; CI runs `--merge-ready` on the PR. |
| The full suite is RED between T5 and T7 (migration digest). | Sequenced; T7 follows directly. |
| `docs drift` needs `ArchiveCloseoutValidator` indexed. | Refresh the worktree's CodeGraph index after T3; CI defers drift (`ci.yml:587-589`). |
| The backfill uncovers more missing design. | Bounded to three plans; write it, never waive "not documented". |
| Commits `8beb97ae` / `6dd4ce3f` are relayed, not verified by this author. | The file states corroborate them (D3). The conductor supplied #426's merge from git (`0351a45c`, 2026-10-24T13:10:37Z); T11 uses 2026-10-24. |
| The archive walk follows a symlink out of the tree. | Reuse `DocsRootPath.EnumerateContainedFiles` (D9). |

## Out of scope

- Changing `docs-dev`'s `model-profile` (D11, not pursued).
- Partial mappings in `2026-10-09-issue-312`, `2026-10-10-kyberdash-claude-code-turn-content`
  and `2026-10-09-issue-319` (D15; baselined; possible follow-up).
- The `squad-model-presets` spec (D16).
- Creating the missing architecture or onboarding docs for KyberUtilities, Distribution,
  ContextHygiene, ReviewCouncil and CI Pipelines (D25; done when each is next extended).
- A draft-time mechanical check of the impact keys (D21).
- The overlap with the `product-owner` skill description (D18).
- #426's `squad install/update --global` deployment gap (open owner item).
- `docs/archive/specs/kyberdash/` missing from `docs/specs/README.md`, and that README's
  outdated `6-Docs/specs/` note.
- Review-loop skills outside this repository that may move plans; 004 catches their output.
- Regenerating `scripts/fast-loop-classes.txt`.
- Any `dash/` change. `./scripts/update-loop.sh` is not required: the release path, packaging
  and self-updater are untouched, and only canonical text already shipped in the pack
  changes.

## Verification gates

Run before the review (T12) and again after closeout (T14). From the root `AGENTS.md`:

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-weave-docs
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill lint .apm/skills/kyber-weave-docs --min-desc-score 70
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill scan .apm/skills/kyber-weave-docs --fail-on critical
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json
```

- `review gates` adds the `kyber-weave-cli` skill trio, InspectCode, duplicates and the TS
  gates.
- Before T13, `docs validate --merge-ready` is expected to report exactly one finding:
  `KW-DOC-LIFECYCLE-003` for this active plan. After T13 it reports zero.
- `scripts/fast-loop.sh` is not evidence.

## Review and closeout

- **Review:** `code-reviewer`, once, after T12. `NEEDS_HUMAN` is expected.
- **Closeout:** T13 (`docs-dev`, a dogfood design harvest of H1–H7 and every decision), then
  T14.
- The permanent `## Decisions` table above is preserved intact through archive.
