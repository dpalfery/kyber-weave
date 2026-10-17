---
id: plans/2026-10-15-docs-two-audience-completeness
title: Documentation corpus completeness for two audiences - KyberDash, the CLI surface, and install, update, and configure
doc-type: plan
status: complete
component: DocGraph
owner: dpalfery
last-reviewed: 2026-10-15
development-mode: standard
keywords:
  - documentation corpus
  - KyberDash
  - CLI command surface
  - install
  - update
  - configure
  - two audiences
---

# Documentation corpus completeness for two audiences

## Status

Approved 2026-10-15; executing. The user gave the **approve and execute** gate
verbatim ("approve and execute", 2026-10-15; recorded as [D10](#decisions)): the
plan is approved as authored, with no conditions, no scope changes, and no
rejected findings. Decision-complete (D1–D9, all user-supplied or forced by user
constraints; see the [decision record](#decisions)). The assessment phase T1–T4
completed and produced `artifacts/docs-corpus-assessment-2026-10-15.md`.

The **human review gate has passed** (2026-10-15): the user reviewed the
assessment report and returned the gate verbatim ("accept the assessment and the
default recommendations. party on Garth"). The assessment is accepted, and all
ten gate decisions GD-1…GD-10 of the report's §8 register are dispositioned as
[D11–D20](#decisions) — the conductor read "the default recommendations" as
option (a) in each gate decision, with the two sub-decisions resolved against
the repository's stated non-negotiables (GD-1 frontmatter; GD-4 rule-id
permanence), as each row records. The remediation tasks R1–R5 and closeout C1
are now dispatchable, their scopes carrying the gate's amendments (F-T2-8 and
F-T2-9 → R1; F-T3-10 → R2; R3 drops F-T1-2 — reclassified as plan-lifecycle
housekeeping, resolved at the gate — and gains the GD-5/GD-6/GD-7 work). At that
save, no remediation had been executed; it only recorded the gate and its scope
amendments.

**Remediation executed 2026-10-15.** All five clusters (R1–R5) completed; every
task audit returned PASS. During execution, seven items surfaced that the
approved assessment did not contain. The user dispositioned them by accepting
the conductor's recommendation verbatim ("do the recommended", 2026-10-15),
recorded as [D21–D27](#decisions): items 1, 2, 3 and 5 → the new cluster
**R6**; item 4 → investigation-first inside R6 (fix the wrong side only on
unambiguous source truth); item 6 → the todo
`docs/todo/squad-pack-single-target-render.md` ([D26](#decisions)); item 7 → no
action ([D27](#decisions)). The execution divergence and provenance facts that
surfaced during R1–R5 are recorded as [D28–D32](#decisions) — records, not new
work. **R6 is dispatchable now; C1 is not**: C1 depends on R6, per the
[dependency graph](#dependency-graph).

**Closeout executed 2026-10-15.** R6 completed and was audited PASS; C1 then
closed the plan. The durable decisions D1–D34 are mapped to canonical
documentation in the [Closeout](#closeout) section; the plan is archived to
`docs/archive/plans/2026-10-15-docs-two-audience-completeness.md` and its index
row moved to Archived in `docs/plans/README.md` ([D34](#decisions)). The
whole-change review council returned `NEEDS_HUMAN` under `KW-REVIEW-008` — the
designed `.kyber-weave/kyber-weave.yml` `always-human` escalation of
[D32](#decisions); the human owner reviewed the change and directed push and
pull-request submission ([D33](#decisions)), satisfying the required human
review. The council raised one minor finding and the TypeScript gates were
skipped for the environment; both are disclosed, not fixed, in the
[Closeout](#closeout) section. `docs validate . --merge-ready` and
`docs drift .` report zero findings with the plan archived.

## Problem and goal

The documentation corpus is governed and claims zero findings, but currency and
completeness have not been verified around three areas, and the corpus does not yet
flow for two distinct audiences:

1. **KyberDash as a feature.** `docs/dash/` (README, architecture, runbook,
   telemetry-inventory), the `KyberDash` catalog row, and the `docs/README.md` and
   root `README.md` listings exist and are marked current, but the set has not been
   verified complete and internally consistent against the repository's
   documentation ontology, the app-docs standard, and the current `dash/` code.
2. **The real CLI command surface.** Known staleness exists: documented verbs that
   are not registered, counts that disagree between the two READMEs, and a
   documented command form (`kyber-weave dash refresh`) that the `kyber-weave`
   binary does not register. A full flag-level reconciliation of every `Settings`
   class against every feature page has never been completed.
3. **Install, update, and configuration capabilities.** The user-facing
   `docs/install.md` documents the `install.sh` and self-updater paths but not the
   `npm` path for `kyberdash` or the `dotnet tool` path, both of which appear only
   in maintainer-facing pages. Whether `kyberdash` is actually published to the
   public npm registry is unverified.

The **audiences**: a new human installing Kyber-Weave or KyberDash, and an agent
asked to install, update, or configure either. Today neither audience has a single
entry point that carries the complete command surface and the canonical approaches;
for the agent audience there is no entry point at all (no unified page, no
entry-point skill).

**Goal.** An assessment first (mechanical checks plus a truth reconciliation plus
the two audience-journey walkthroughs, producing one named report), a human review
gate on that assessment, then remediation that closes every approved finding —
including a unified CLI command-surface page for humans and agents, made
agent-discoverable through an entry-point skill — so that both audience flows are
complete, with `docs validate` and `docs drift` at zero findings throughout.

## Development mode

`standard`. The user explicitly chose it (D6): no RED/GREEN test-first requirement.
Standard mode still requires an approved verification contract per task (the
repository's documentation gates — `docs validate`, `docs drift`, the skill gates
for the entry-point skill — plus evidence checks), task audits, and a final review
council pass over the whole change before closeout. This plan's work products are
documents and one SKILL.md; the "automated test surface" for each task is the
repository's own gates, named per task in the
[verification contract](#verification-contract-standard-mode).

## Decisions

The permanent decision record. D1–D6 are the user's authoritative decisions,
relayed by the conductor in the authoring packet. D7–D9 are baseline decisions
recorded per plan policy (a user constraint forces D7; D8 is the architect's
location choice for the assessment artifact; D9 is the conductor's remediation
bound, relayed in the authoring packet). D10 records the plan's approve-and-execute
gate decision. D11–D20 record the human review gate dispositions (GD-1…GD-10 of
the approved assessment report's §8 register): the user accepted the assessment
and "the default recommendations" ("accept the assessment and the default
recommendations. party on Garth", 2026-10-15). The assessment deliberately
offered options without recommending one, so the conductor read "the default
recommendations" as option (a) in each gate decision, with two sub-decisions
resolved against the repository's own stated non-negotiables (GD-1: the plan
frontmatter stays `status: current`, anchored on the closed status vocabulary;
GD-4: the `KW-SKILL-REVIEW-001` row stays, anchored on "rule ids are
permanent"). Each D11–D20 row records that reading in its own fields, so the
provenance is honest rather than implying the user chose each option by name.

D21–D27 record the dispositions of the seven-item findings collection that
surfaced during remediation execution (R1–R5) and that the approved assessment
report did not contain. The user dispositioned the collection by accepting the
conductor's recommendation verbatim ("do the recommended", 2026-10-15): fix
items 1, 2, 3 and 5 (cluster R6); investigate item 4 and fix it only if the
source is unambiguous (R6, investigation-first); capture item 6 as a todo; no
action on item 7. Each row states its item, its disposition, and its evidence
class (verified / audit-confirmed / unverified pending investigation). D28–D32
are execution divergence and provenance records — facts about what happened
during R1–R5, recorded here because the plan's decision table is the only place
provenance survives closeout; they are records, not new work, and no decision
in them is open.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Artifact for this work: PLAN or spec? | **PLAN.** A bounded implementation plan under `docs/plans/`, registered in the plan index. | The corpus work is verification plus bounded remediation, not a multi-phase feature spec. A spec's phase structure adds no value for document rework. | ANSWERED — user decision, relayed 2026-10-15. |
| D2 | What does "KyberDash documentation is complete and consistent" mean in scope? | **Verification plus rework as needed** — assess the existing set against the ontology and app-docs standard, then fix what the approved assessment finds. Not net-new-only, not consistency-only. | The set exists (`docs/dash/` four documents, catalog row, both README listings), so completeness is about verifying the set and closing gaps, including any required document the standard names that the set lacks (surfaced as findings at the gate, never fixed silently). | ANSWERED — user decision, relayed 2026-10-15. |
| D3 | Sequencing of assessment vs remediation? | **Full review FIRST, producing a named assessment report; then an explicit human review gate; then remediate.** Remediation tasks are not ready until the user approves the assessment. See [Human review gate](#human-review-gate). | Guarantees the user sees and shapes the finding list before any document is changed; prevents remediation scope from being guessed. All remediation tasks (including the user-decided unified page and skill, which are objective-derived but consume the assessment's reconciled inventory) wait for the gate. | ANSWERED — user decision, relayed 2026-10-15. |
| D4 | CLI surface for the two audiences? | **Add a unified CLI command-surface page for humans and agents, possibly backed by a skill.** Agent discoverability is a testable acceptance criterion: an agent asked to install, update, or configure Kyber-Weave or KyberDash must find the page, and the page must carry all the commands and the canonical CLI approaches. | Discoverability mechanisms for this repository: a listing in the docs index (`docs/README.md`), a pointer in the root `README.md`, a pointer in root `AGENTS.md` ("Where to go next"), and an entry-point skill under `.apm/skills/` whose routing description fires on install/update/configure prompts — proven mechanically with `skill validate`, `skill lint`, `skill scan`, and `skill route` probes (task R5). A catalog row is not a mechanism here: the page is a document, not a component, and inventing a component would fail `KW-DOC-SPEC-004`. | ANSWERED — user decision, relayed 2026-10-15. |
| D5 | Which audience flow first? | **No preference — both flows must be complete for the objective to be done.** | The assessment walks both journeys in parallel (T3); remediation closes both. Neither journey may be left open at closeout. | ANSWERED — user decision, relayed 2026-10-15. |
| D6 | Development mode? | **`standard`.** Each implementation task carries an approved verification contract built on the repository's documentation gates, not RED/GREEN test evidence. | The work products are Markdown documents and one SKILL.md; the repo's own gates (`docs validate`, `docs drift`, the skill gates) are the appropriate evidence surface. | ANSWERED — user decision, relayed 2026-10-15. |
| D7 | Tension: a unified CLI command-surface page vs the stated design in `src/KyberWeave.Cli/AGENTS.md` that "the command surface is described in `docs/`, by feature"? | **The unified page is an aggregation index inside `docs/`, not a replacement for the by-feature design.** It carries the complete verb/flag/exit-code inventory with a link from every entry to the owning feature page, which remains the canonical detail surface. `src/KyberWeave.Cli/AGENTS.md`'s rule is preserved and extended: adding or changing a verb, a flag, or an exit-code rule means updating the matching feature page **and** the unified page in the same change (that extension is migrated at closeout, C1). | The user's D4 constraint forbids overruling the by-feature design without surfacing the tension — this row surfaces it. Aggregation-with-links overrules nothing: the page is itself in `docs/`, and per-feature pages keep their authority. The alternative (make the unified page the single canonical surface and demote the feature pages) was rejected because it overrules the stated design. | ANSWERED — forced by the user's D4 constraint (do not overrule without surfacing); recorded at plan authoring 2026-10-15; re-confirmable at the human review gate. |
| D8 | Where does the assessment report live? | **`artifacts/docs-corpus-assessment-2026-10-15.md`** — the repository's scratch space for generated evidence (`artifacts/gates.json`, `artifacts/inspectcode.xml` already live there), outside the governed corpus. | An assessment report is transient review evidence, not a durable document: it fits none of the closed doc-types, and placing it under `docs/` would either widen the ontology (forbidden — AGENTS.md and D-constraint) or pollute the corpus with a transient status. Durable facts found by the assessment are harvested into canonical docs at remediation/closeout, not kept in the report. | ANSWERED — architect baseline decision, plan authoring 2026-10-15; consistent with the D2/D3 user decisions. |
| D9 | Remediation rework bound? | **At most 2 rework dispatches per failure cluster.** A task-reviewer finding that survives its second rework dispatch escalates to the conductor with the cluster's evidence; it does not get a third dispatch inside this plan. | Conductor rule relayed in the authoring packet. Keeps the remediation loop bounded even if a finding cluster turns out larger than assessed. | ANSWERED — conductor rule, relayed 2026-10-15. |
| D10 | Plan approval: was the **approve and execute** gate given? | **Yes — the plan is approved as authored.** The user returned the gate verbatim: "approve and execute", 2026-10-15. No conditions, no scope changes, no rejected findings. | The conductor presented the decision-complete Draft (D1–D9 answered; `development-mode: standard` with its verification contract) after both documentation gates (`docs validate .` and `docs drift .`) reported zero findings against the working tree. Approval covers the plan as authored, including the D3 sequencing: the assessment phase T1–T4 is dispatchable now; R1–R5 and C1 remain **not ready** until the user approves the assessment report at the [human review gate](#human-review-gate). | APPROVED — user gate decision, relayed 2026-10-15 ("approve and execute"). |
| D11 | Human review gate GD-1: disposition of the plan-status mutation and the residual "Ready"/"current" inconsistency (F-T1-1, F-T1-2)? | **(a) Harmonize the residual "Ready" wording** — the plan `## Status` prose and the Active Plans row in `docs/plans/README.md` are reworded to a non-status phrase ("Approved 2026-10-15; executing" / "Approved — executing") so nothing collides with the closed document-status vocabulary; the plan frontmatter stays `status: current`. | The frontmatter sub-decision is anchored on the repository's closed status vocabulary (`current\|draft\|needs-review\|superseded`; the mid-assessment `KW-DOC-SPEC-002` on `status: Ready`). F-T1-2 is reclassified as plan-lifecycle housekeeping — resolved at the gate by this recording save, not by a remediation cluster; R3 drops it and edits neither the plan nor the plan index. F-T1-1's underlying event was the plan's own lifecycle transition, disclosed in the report's header and §9. **Provenance reading:** the assessment offered options without a recommendation; the conductor read the user's "the default recommendations" as option (a) in each gate decision, with the two sub-decisions resolved against the repository's stated non-negotiables (this row's frontmatter sub-decision; D14's rule-id-permanence sub-decision) — the user did not choose each option by name. | ANSWERED — user gate decision, relayed 2026-10-15 ("accept the assessment and the default recommendations"). |
| D12 | Human review gate GD-2: npm install-path scope (F-T3-1, F-T3-9)? | **(a) `docs/install.md` states explicitly that npm and homebrew are unsupported for KyberDash; only `install.sh` is the documented user path.** | Verified at assessment: no public npm channel for kyberdash exists (`npm view kyberdash --json` → registry 404; `docs/distribution.md:68-70`). Option (c) (publish a package) is a code/release change, out of this plan's scope. R2 owns; recorded in R2's scope amendments. Provenance reading as D11 records. | ANSWERED — user gate decision, relayed 2026-10-15 ("accept the assessment and the default recommendations"). |
| D13 | Human review gate GD-3: `dotnet tool` install-path scope (F-T2-11, F-T3-2)? | **(a) `dotnet tool` is documented as unsupported / out of scope for end users.** | The channel is maintainer-facing and auth-gated (GitHub Packages; `docs/distribution.md:9`; option (c)'s end-user reachability could not be verified). R2 owns; recorded in R2's scope amendments. Provenance reading as D11 records. | ANSWERED — user gate decision, relayed 2026-10-15 ("accept the assessment and the default recommendations"). |
| D14 | Human review gate GD-4: phantom `skill review` verb disposition, including the `KW-SKILL-REVIEW-001` row (F-T2-1, F-T2-2, F-T2-10)? | **(a) Remove the phantom `skill review` verb rows** (`docs/context-hygiene/skills.md:38-39`) **and re-point the `:110` reference to `docs review`. Sub-decision: KEEP the `KW-SKILL-REVIEW-001` row** in `docs/ci-pipelines/rule-reference.md` and correct only its trigger description. | The KEEP sub-decision is anchored on the repository non-negotiable "rule ids are permanent": `KW-SKILL-REVIEW-001` is a real, emitted, unit-tested rule id (`SkillReviewExchange.cs:17`, `SkillReviewTests.cs`), so erasing the row would un-suppress a live rule id in every host repository; only the trigger description (which implies a phantom CLI verb) is corrected. **Provenance reading:** as D11 records — the assessment offered options without a recommendation; the conductor read "the default recommendations" as option (a), and this KEEP sub-decision was resolved against the stated non-negotiable rather than chosen by name by the user. R1 owns; recorded in R1's scope amendments. | ANSWERED — user gate decision, relayed 2026-10-15 ("accept the assessment and the default recommendations"). |
| D15 | Human review gate GD-5: ADR 0016 disposition (F-T2-7, F-T2-6)? | **(a) Amend ADR 0016 in place** to correct Decision 1 and its status line to the shipped `kyberdash dash refresh`, **and fix `docs/dash/runbook.md:576`.** | In-place amendment follows the repository's established ADR-amendment pattern (ADR 0031, ADR 0035); supersession (option b) would spend a new ADR on a naming error. Scope amendment: the amendment moves from C1's conditional ("only if the gate chose it") into R3's scope; C1 keeps only the durable-fact harvest. Provenance reading as D11 records. | ANSWERED — user gate decision, relayed 2026-10-15 ("accept the assessment and the default recommendations"). |
| D16 | Human review gate GD-6: add `docs/dash/onboarding.md` and `docs/dash/requirements.md` (F-T1-3, F-T1-4)? | **(a) Add both** (R3 creates them per the app-docs standard's required shape). | The external app-docs standard requires `onboarding.md` and `requirements.md` for applications and runnable services; peers exist (`docs/kyber-squad/onboarding.md`, `docs/kyber-squad/requirements.md`); both are existing ontology doc-types — no ontology widening. R3 owns; recorded in R3's scope amendments. Provenance reading as D11 records. | ANSWERED — user gate decision, relayed 2026-10-15 ("accept the assessment and the default recommendations"). |
| D17 | Human review gate GD-7: `docs/dash/telemetry-inventory.md` `status: draft` (F-T1-5)? | **(a) Promote to `status: current`** and refresh the `KyberDash` catalog row's `Last reviewed`. | T1's conformance matrix found no other defect in the document; leaving `draft` would keep the KyberDash set mixed-status against the corpus's currency claims. R3 owns; recorded in R3's scope amendments. Provenance reading as D11 records. | ANSWERED — user gate decision, relayed 2026-10-15 ("accept the assessment and the default recommendations"). |
| D18 | Human review gate GD-8: canonical agent count "21" and the "from 20 to 35" roster claim (F-T2-8, F-T2-9)? | **(a) Update the documentation to 22 canonical agents** — the roster count in `products/kyber-squad/bundles/full.yml:3-25` — **and reconcile `docs/code-review/architecture.md:144`'s "from 20 to 35".** No code change. | `full.yml` is the source of truth, not an edit target (option (b) is a code change, out of scope); the "24 skills" half of the claims is correct and stays. Scope amendment: R1 gains F-T2-8 and F-T2-9 (`docs/README.md:119`, `README.md:155`/`:253`, `docs/kyber-squad/README.md:28,42`, `docs/kyber-squad/onboarding.md:25`, `docs/code-review/architecture.md:144`). Provenance reading as D11 records. | ANSWERED — user gate decision, relayed 2026-10-15 ("accept the assessment and the default recommendations"). |
| D19 | Human review gate GD-9: product-choice / install-path framing page (F-T3-10)? | **(a) Fold the "which product / which install path" decision block into `docs/install.md`.** | `docs/install.md:22-27` already carries the binary table the choice is made from; a dedicated page (option b) would add a document the corpus does not otherwise need. Scope amendment: R2 gains F-T3-10. Provenance reading as D11 records. | ANSWERED — user gate decision, relayed 2026-10-15 ("accept the assessment and the default recommendations"). |
| D20 | Human review gate GD-10: report every deviation, or only material ones? | **(a) Report every deviation observed by the assessment.** No change to the plan. | Policy: the approved report's §8 register and §9 limitations carry all observed deviations (cluster reclassifications, `Program.cs` line offsets, partial readings); nothing was silently dropped or silently clustered. Affects all findings. Provenance reading as D11 records. | ANSWERED — user gate decision, relayed 2026-10-15 ("accept the assessment and the default recommendations"). |
| D21 | Execution finding 1: `products/kyber-squad/README.md`'s agent directory tree lists 21 agent entries and omits `bug-crusher.md`, while the file's own header now says 22 — fix or defer? | **Fix.** R6 adds the missing `bug-crusher.md` entry to the hand-maintained directory tree. | Evidence class: **verified** — `products/kyber-squad/agents/bug-crusher.md` exists on disk, the tree lists 21 entries, the header says 22, and the tree is hand-maintained (not generated), so the omission is proven the only one. The tree is corrected, not regenerated. | ANSWERED — user decision, relayed 2026-10-15 ("do the recommended"). |
| D22 | Execution finding 2: `docs/context-hygiene/agents.md:58` says "the 23-skill namespace" where the canonical skill count is 24, and "seven names" where the agent∩skill intersection is now 8 — fix or defer? | **Fix.** R6 corrects both counts in that sentence. | Evidence class: **verified** — `git log` proves the line stale: a retired skill took the canonical skill count 24→23, and a later addition returned it to 24 without restoring this line; the agent∩skill intersection is now 8. | ANSWERED — user decision, relayed 2026-10-15 ("do the recommended"). |
| D23 | Execution finding 3: `docs/dash/telemetry-inventory.md:17` states store "schema 15" while `dash/src/canon/migration-v20.test.ts:113` pins `SCHEMA_VERSION` to 20 and ADR 0038 says Schema v20 — fix or defer? | **Fix.** R6 corrects the schema claim to v20. | Evidence class: **audit-confirmed** — an audit confirmed this as a genuine defect against the test pin and ADR 0038. | ANSWERED — user decision, relayed 2026-10-15 ("do the recommended"). |
| D24 | Execution finding 4: `docs/kyber-squad/architecture.md:520` classifies `antigravity` as Native while root `README.md`'s harness-class table files Antigravity under Role-Skill Lowering — which is right is unverified. Fix, defer, or investigate? | **Investigate first; fix only on unambiguous source truth.** R6 investigates the classification from source; if the source demonstrates one side wrong, R6 fixes the wrong side; if both classifications are genuinely defensible, R6 changes nothing and reports — that outcome escalates to the conductor, never a worker guess. | Evidence class: **unverified pending investigation** — neither side is proven wrong today. Root `README.md` enters R6's file set only if the investigation shows the harness-class table is the wrong side. | ANSWERED — user decision, relayed 2026-10-15 ("do the recommended"). |
| D25 | Execution finding 5: `docs/adr/README.md`'s ADR-0016 summary row records only the older `Amended 2026-10-10` note while the ADR body now carries R3's `Amended 2026-10-16` correction — fix or defer? | **Fix.** R6 adds the `Amended 2026-10-16` note to the ADR-0016 summary row so index and body agree. | Evidence class: **audit-confirmed** — an audit confirmed the index/body inconsistency and assigned its resolution to the task that owns the ADR index; R3's amendment ([D15](#decisions)) changed the body, and R6 (the follow-on cluster) owns the index row. | ANSWERED — user decision, relayed 2026-10-15 ("do the recommended"). |
| D26 | Execution finding 6: `kyber-weave squad pack` cannot render a single target to a directory, so the Copilot render total could only be verified by invoking `CopilotRenderer` directly from a throwaway program — fix the tooling now, capture as a todo, or no action? | **Capture as a todo.** A tooling/feature gap, not a documentation gap, and fixing it is a code change this plan's Out-of-scope forbids. Registered as `docs/todo/squad-pack-single-target-render.md`. | Evidence class: **verified** — the direct-invocation workaround is recorded evidence the gap is real. Per the repository rule "capture authorized deferred work as todos", the user's acceptance authorizes the todo; no further investigation in this plan. | ANSWERED — user decision, relayed 2026-10-15 ("do the recommended"). |
| D27 | Execution finding 7: five workers reported inconsistent `src/KyberWeave.Cli/Program.cs` line numbers (`:24`, `:25`, `:30`) for the same branches while agreeing on the branch set — record a correction, or no action? | **No action.** A meta-observation about evidence gathering, not a corpus defect: the branch set was consistent and no document claims a wrong line. This is why the new published page (`docs/cli-command-surface.md`) carries no line citations. | Evidence class: **verified (as an observation)** — the line numbers disagreed, the branch set did not; nothing in the corpus asserts the disputed line numbers. | ANSWERED — user decision, relayed 2026-10-15 ("do the recommended"). |
| D28 | R1's execution diverged from D14 as literally worded: D14 said re-point the `:110` reference to `docs review`, but the worker refused the re-point and fixed the finding's intent instead — how is the divergence resolved? | **The refusal is upheld; D14's implementation is corrected, not its intent.** `docs review` is wired to `DocumentationReviewExchange` (rule id `KW-DOC-REVIEW-001`), so re-pointing the `KW-SKILL-REVIEW-001` reference to `docs review` would have created a new false attribution; the worker removed the phantom-verb attribution instead, which is what D14's intent (no false verb attribution) required. | The task audit independently confirmed the refusal was correct. D14's intent — remove the phantom `skill review` verb and keep the rule-id row honest — is unchanged; only the literal "re-point the `:110` reference to `docs review`" wording was not executable as written without creating a new false attribution. D14's row is preserved verbatim; this row records the implementation resolution. | RECORDED — execution divergence, task-audit confirmed PASS, 2026-10-15. |
| D29 | The Copilot render total changed from 122 to 123 in five documents — on what evidence, and what dependent claims changed? | **123, on empirical evidence; the five documents now say 123.** The real `CopilotRenderer` invoked against the checked-in corpus returned `Total=123` (`AgentPrincipals=22`, `AgentRefs=10`, `SkillPrincipals=24`, `SkillResources=67`); the dependent "45 principal files" count became 46. | Evidence class: **verified (empirical)** — the total was corrected on a measured render of the checked-in corpus, not on arithmetic from the roster. Recorded here because the plan's decision table is the only place this provenance survives closeout. | RECORDED — execution provenance, empirical render evidence, 2026-10-15. |
| D30 | F-T2-8's occurrence list was under-enumerated by the assessment — how was the finding completed? | **The same false "21 canonical agents" count was found and fixed in the additional occurrences too; the finding is closed on the full occurrence set.** Beyond the files the assessment listed, occurrences were found in `products/kyber-squad/README.md`, `docs/kyber-squad/requirements.md`, `docs/kyber-squad/architecture.md`, `docs/context-hygiene/agents.md`, `docs/distribution.md`, and further lines in `docs/kyber-squad/README.md` and `docs/kyber-squad/onboarding.md`, and were completed. | Evidence class: **verified** — each occurrence was fixed against D18's source of truth (`products/kyber-squad/bundles/full.yml`). The remediation closed the finding itself, not only the rows the assessment had listed. | RECORDED — execution provenance, verified occurrences, 2026-10-15. |
| D31 | Root `README.md`'s architecture-tree line summarising the `kyber-weave` surface omitted `utilities` and `review` — under which finding was it corrected? | **Corrected as a continuation of F-T3-8 (the understated-surface finding); the line now names the full surface.** | Evidence class: **verified** — the omission is the same defect class as F-T3-8 (a summary understating the real `kyber-weave` surface), so the correction continued that finding rather than minting a new id. | RECORDED — execution provenance, verified, 2026-10-15. |
| D32 | R5's gate-coverage edits place `.kyber-weave/kyber-weave.yml` within `review.policy.always-human` — is the resulting review escalation intended? | **Yes — acknowledged consequence of the design.** An edit to `.kyber-weave/kyber-weave.yml` now escalates a review to a human by design. | Evidence class: **verified** — the placement follows from R5's approved gate-coverage scope; the consequence is recorded so closeout does not mistake the escalation for a regression. | RECORDED — execution provenance, acknowledged consequence, 2026-10-15. |
| D33 | Whole-change review council returned `NEEDS_HUMAN` under `KW-REVIEW-008` because `.kyber-weave/kyber-weave.yml` matches `review.policy.always-human` (the consequence acknowledged in [D32](#decisions)) — has the required human review been satisfied? | **Yes.** The human owner reviewed the whole change and directed push and pull-request submission, satisfying the required human review. | Evidence class: **verified** — the council's `NEEDS_HUMAN` verdict is the published record and the owner's direction is the resolution; the escalation was the designed consequence of D32, not a regression. | ANSWERED — user decision, relayed 2026-10-15 ("push and submit a pr"). |
| D34 | Plan lifecycle at closeout — how is the completed plan retired? | **Archived.** Plan Archived on 2026-10-15 by the pull-request closeout, with the index row in `docs/plans/README.md` and the closeout mapping in the archived plan's [Closeout](#closeout) section. | Evidence class: **verified** — `docs validate . --merge-ready` reports zero findings only after the plan leaves `docs/plans/` (`KW-DOC-LIFECYCLE-003`); the permanent `## Decisions` section (D1–D34) is preserved intact in `docs/archive/plans/`. | RECORDED — closeout, 2026-10-15. |

## Investigation findings

Discovery report: **codegraph 0, docs-MCP 0, direct reads 14.** This headless harness
exposes no `codegraph_explore` and no `docs_explore`/`docs_for_symbol`/`docs_glossary`
tools, so the AGENTS.md exploration order could not be followed; per its own fallback
rule, narrow self-gathered lookups were made instead and are labeled here. The
`.codegraph/` index exists in this checkout, so `docs drift` can run. Direct reads and
their reasons (each was verifying a claim a task depends on, or matching the
repository's plan and decision-record conventions):

1. Root `AGENTS.md` — gate suite, docs rules (re-run `docs validate` and `docs drift`
   after any `docs/` edit; never widen the ontology), plan placement, exploration
   order, hand-authored-file rules.
2. `docs/plans/README.md` — plan-index conventions, the permanent five-column
   `## Decisions` contract, closeout and waiver policy, KW-DOC-LIFECYCLE rules.
3. `products/kyber-squad/agents/architect/references/plan-authoring.md` — the plan
   contract this plan follows.
4. `products/kyber-squad/agents/architect/references/standard-verification.md` — the
   standard-mode verification-contract shape.
5. `docs/archive/plans/2026-09-29-release-checksums-unsigned-windows.md` — the shape
   of a recent `standard`-mode plan (frontmatter, decision record, findings, scope,
   design, task fields, verification-contract rows).
6. `.apm/skills/kyber-weave-docs/SKILL.md` — the skill workers must follow for
   frontmatter conformance; also confirms `.apm/skills/` is a validated skills root
   (today: `doc-corpus-integrity-review/`, `kyber-weave-docs/`, `spec-conductor/`; no
   name collision with the planned entry-point skill).
7. `src/KyberWeave.Cli/Program.cs` — the registered command surface: branches `skill`
   (validate, lint, scan, route, catalog, pack, new), `agent` (validate, scan,
   sync-check, catalog), `docs` (init, validate, drift, export-graph, catalog,
   integrity-check, `review` sub-branch with export/import, glossary), `squad`
   (install, update, uninstall, status, doctor, pack), `utilities statusline`
   (deploy, status, doctor, remove), top-level `review` (gates, duplicates, verdict),
   and top-level `update`. **No `dash` branch exists.**
8. `docs/context-hygiene/skills.md` — lines 38–39 and 110 document `skill review
   export` / `skill review import`, verbs the `skill` branch does not register. The
   equivalent functionality exists as `docs review export` / `docs review import`
   (Program.cs lines 119–127).
9. `docs/README.md` — line 136 says "the two commands" where three review verbs are
   registered (`review gates`, `review duplicates`, `review verdict`); the KyberDash
   Feature 5 section and the "4 Core Pillars" listing exist; `docs/code-review/README.md`
   documents all three review verbs (verified in the prior intake; re-verified here
   via the `docs/README.md` cross-reference).
10. Root `README.md` — line 37 says "10 IDE/CLI harnesses" while `docs/README.md` and
    the same root README's Feature 3 prose declare twelve harnesses.
11. `docs/catalog.md` — the `KyberDash` Feature row exists and is `current`; the
    `DocGraph` Feature row exists (this plan's `component`); `Distribution` covers
    `install.md`/`distribution.md`.
12. `docs/install.md` — the flag table (`--version`, `--prerelease`, `--install-dir`,
    `--no-mcp`, `--no-kyberdash`, `--with-menubar`, `KYBER_WEAVE_RELEASE_ORIGIN`)
    matches `scripts/install.sh` per the prior intake; `docs/install.md` documents
    neither the `npm` path for `kyberdash` nor the `dotnet tool` path; line 257 notes
    `kyber-weave update` refuses `dotnet run` and `dotnet tool` installs.
13. `docs/dash/runbook.md` — the released-binary surface (`kyberdash report`,
    `kyberdash web`, `kyberdash kyber otel`, `kyberdash menubar`, `dash refresh`,
    `dash clean`, `dash import-history`, `dash settings`), the maintainer-facing
    `npm publish` build workflow, and line 576, which instructs
    **`kyber-weave dash refresh`** — a command form `Program.cs` does not register.
    `dash refresh` is a verb on the `kyberdash` CLI (runbook lines 212–214, 36, 258).
    ADR 0016 names `kyber-weave dash refresh` as the public command — a tension
    between ADR 0016 and the current surface that must be surfaced at the human
    review gate, not silently edited.
14. `docs/documentation-ontology.md` — the closed doc-type and status vocabularies,
    the required-key matrix (a `reference` document takes base keys only; a `plan`
    requires `component`), the pairing invariant, and the rule that ids are permanent.

Unverified gaps carried forward as assessment questions (the prior intake flagged
them; T2/T3 must settle them): the full flag-level comparison of every `Settings`
class against every feature page; whether `kyberdash` is actually published to the
public npm registry; whether the phantom `skill review` verbs were ever implemented
and removed, or documented ahead of implementation (the archived plan
`docs/archive/plans/2026-08-14-skill-agent-description-trigger-verification.md`
proposed them; nothing in `Program.cs` registers them today).

## Scope

In:

- The assessment phase: mechanical verification of the corpus, full CLI command-surface
  truth reconciliation (`kyber-weave` and `kyberdash`), and the two audience-journey
  walkthroughs, producing `artifacts/docs-corpus-assessment-2026-10-15.md`.
- The human review gate on that report.
- Remediation of every approved finding: stale-claim fixes, the user-facing
  install/update/configure flow, KyberDash completeness/consistency rework.
- The unified CLI command-surface page (`docs/cli-command-surface.md`) and its
  discoverability mechanisms, including the entry-point skill under `.apm/skills/`.
- Closeout: durable-fact migration, plan-index synchronization, archive.

Out:

- Any change to CLI, engine, `dash/`, tray, or MCP **code**. This plan never
  implements the phantom `skill review` verbs; it fixes what the documents claim.
  (If the user wants them implemented, that is new work — a todo or a new plan,
  decided at the gate.)
- Any change to `scripts/install.sh` or the self-updater's behavior.
- Widening the documentation ontology (no new doc-types, no new statuses) — a failing
  document is fixed, never the vocabulary.
- Editing archived plans or ADR history other than the amendment-in-place mechanism
  ADR 0016 may require, decided at the gate.
- CI, pipelines, or release workflow changes.

## Design

### What "complete and consistent" means (the assessment rubric)

Against `docs/documentation-ontology.md` and the app-docs standard:

1. **Mechanical**: `docs validate .` zero findings (run without `--merge-ready` while
   this plan is active — `KW-DOC-LIFECYCLE-003` fails any plan still under
   `docs/plans/` by design); `docs drift .` zero findings (or explicitly recorded
   "skipped" with the reason, if no usable CodeGraph index exists on the runner);
   every KyberDash document's frontmatter conforms to the required-key matrix
   (`doc-type` and `status` from the closed sets; `owner` a catalog row; `last-reviewed`
   ISO); the `KyberDash` catalog row is current and its links resolve; `docs/README.md`
   and root `README.md` listings match the actual document set.
2. **Complete** (per the app-docs standard's required shape): the KyberDash set has an
   overview, architecture, runbook, and telemetry inventory. Any further document the
   standard's shape requires (for example `onboarding.md` or `requirements.md`) is a
   **finding for the human gate** — the assessment names the gap and the candidate
   shape; it never silently creates or widens anything.
3. **Consistent / true**: every documented claim about a CLI verb, flag, exit code,
   harness count, install path, or update path matches the current source
   (`src/KyberWeave.Cli/Program.cs`, the `*Settings` classes, `scripts/install.sh`,
   the `kyberdash` CLI surface under `dash/src/`, `dash/package.json`).
4. **Audience-complete**: both journeys (below) close without a gap.

### The two audience journeys (assessed in T3)

- **Human, fresh machine**: choose a product → pick an install path (`install.sh`
  curl; `npm` for `kyberdash`?; `dotnet tool`?) → install → verify the download
  (SHA256SUMS) → update (`kyber-weave update`, `kyberdash menubar --update` for the
  tray) → configure (`docs/configuration.md`, `kyberdash settings`). Every step must
  resolve from a user-facing page, or it is a gap.
- **Agent, asked to install/update/configure either product**: starting from the
  repository's entry surfaces (root `AGENTS.md`, root `README.md`, `docs/README.md`,
  `docs/catalog.md`, `.apm/skills/`), the agent must reach the command surface and the
  canonical approaches without being told the path. The baseline today is a gap (no
  unified page, no entry-point skill); the after-criteria are R4/R5's acceptance
  criteria.

### The assessment artifact

`artifacts/docs-corpus-assessment-2026-10-15.md` (D8). It carries, exactly:

- Mechanical evidence: verbatim `docs validate .` and `docs drift .` output (or
  recorded skip with reason), and a per-document frontmatter conformance matrix for
  the KyberDash set.
- A **findings table**: one row per finding — id, severity (blocker / should-fix /
  nice-to-have), `file:line`, the documented claim, the source-of-truth evidence
  (`file:line` or symbol), and the proposed remediation cluster (R1–R5, or
  "unclustered — gate decision").
- The **reconciled command-surface inventory**: every registered `kyber-weave`
  branch/verb/flag and every `kyberdash` verb/flag, each with its source line — the
  frozen input R4's page is built from.
- The **journey gap lists** for both audiences, with the after-criteria for R4/R5.
- Answers to the open questions: npm publish status of `kyberdash` (with the query
  used), `dotnet tool` path reachability, the disposition question for the phantom
  `skill review` verbs, and the ADR 0016 `kyber-weave dash refresh` tension.
- A **file-ownership map**: each finding assigned to exactly one cluster, with
  disjoint file sets per cluster except where a dependency is declared; hub files
  (`docs/README.md`, root `README.md`, root `AGENTS.md`, `docs/catalog.md`) list
  their single owning task per edit.

### The human review gate

A plan-level stop, not a worker task. See [Human review gate](#human-review-gate).

### The unified CLI command-surface page (R4)

`docs/cli-command-surface.md`, `doc-type: reference` (base keys only per the
required-key matrix), `status: current`, `id: cli-command-surface`. One page, two
audiences: a human-facing half (what you can do, and the canonical install, update,
and configure flows for both products) and an agent-facing half (the same flows,
stated as stable, link-free-of-prose command sequences with the page's anchors named
so an agent can cite them). The body is the complete verb/flag inventory from the
assessment's frozen reconciliation, every entry linking to the owning feature page
(D7: aggregation index, by-feature design preserved), plus the exit-code rules table
from `src/KyberWeave.Cli/AGENTS.md`. No catalog row is added (the page is a document,
not a component; D4).

### The entry-point skill (R5)

`.apm/skills/kyber-weave-cli/SKILL.md` (name free today). Its routing description
fires on install, update, and configure requests for Kyber-Weave or KyberDash; its
body carries the canonical commands and points at the unified page. R5 also checks
whether the skill needs any registration beyond the directory (for example an
`apm.yml` target) and adds it if the repository requires one.

## Human review gate

**Stop.** After T4 lands, the conductor presents the assessment report
(`artifacts/docs-corpus-assessment-2026-10-15.md`) to the user, together with this
plan's proposed remediation mapping (clusters R1–R5) and the open dispositions the
report names (npm path scope, `dotnet tool` path scope, phantom-verb disposition,
ADR 0016 tension, any unclustered finding).

**What the user is shown**: the report's findings table, severity-ranked; the
journey gap lists; the proposed cluster mapping; and any finding the report marks
"gate decision".

**What approval authorizes**: dispatch of R1–R5 with each packet embedding its
cluster's findings verbatim from the approved report (workers do not read the plan
body; the report is the input artifact). Scope adjustments the user makes at the
gate are recorded as new rows in [Decisions](#decisions) or amended task scope in
the same save, and the report is corrected to match before dispatch.

**What approval does not authorize**: anything in Out-of-scope, new code, ontology
widening, or a third rework dispatch beyond D9's bound.

**If the user rejects or edits the assessment**: T4-style revision of the report
(one dispatch), then the gate again. No remediation task is ready before approval
(D3). Unclustered findings the user accepts become either an amended cluster or an
explicitly declined item recorded in the report and in the closeout mapping.

## Verification contract (standard mode)

Approved with the plan (D6). Fresh evidence is required after implementation and
again after any review-driven rework. `docs validate` during execution runs **without**
`--merge-ready` (this plan's presence under `docs/plans/` fails that flag by design —
`KW-DOC-LIFECYCLE-003`); `--merge-ready` is the C1 post-archive gate. `docs drift`
requires a usable `.codegraph/` index; if absent it is recorded as skipped with the
reason, never as passed. **Editing anything under `docs/` means re-running
`docs validate .` and `docs drift .`** — every R task and C1 does this as its final
step.

| Task | Automated test project or file | Runner command | Acceptance behavior | Additional current evidence |
|---|---|---|---|---|
| T1 | None — read-only verification. The mechanical gates themselves are the surface. | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `-- docs drift .` | Zero findings recorded verbatim, or drift skip with reason; conformance matrix complete for the KyberDash set | Gate output and matrix delivered into the T4 report |
| T2 | None — read-only reconciliation. | The reconciliation is grep/read over `Program.cs`, `Commands/**/*Settings.cs`, `dash/src/` CLI surface, and every docs claim | Every docs claim about a verb/flag/exit code is classified true or false with evidence | The inventory + findings rows delivered into the T4 report; `npm view kyberdash` result recorded with the query used |
| T3 | None — read-only walkthroughs. | The journeys are walked against the current corpus only | Both journey gap lists complete, each gap naming the missing page/section | Baseline agent-entry-point check (root AGENTS.md, README, docs index, catalog, `.apm/skills/`) recorded |
| T4 | None — authoring a non-governed artifact outside `docs/`. | Report schema audit | Report carries every field named in [Design](#design); file-ownership map has disjoint cluster file sets | task-reviewer audits the report against the schema |
| R1 | n/a (documents) | `docs validate .`; `docs drift .` | Zero findings after the edit | Grep proof: no cluster file mentions an unregistered verb; each closed finding's before/after line quoted in the task result |
| R2 | n/a (documents) | `docs validate .`; `docs drift .` | Zero findings after the edit | Flag-table parity re-checked against `scripts/install.md`/`install.sh` and T2's inventory; npm/dotnet-tool scope matches the gate decision |
| R3 | n/a (documents) | `docs validate .`; `docs drift .` | Zero findings after the edit | KyberDash doc set + catalog row + both README listings consistent per the approved findings; ADR 0016 disposition matches the gate decision |
| R4 | n/a (documents) | `docs validate .`; `docs drift .` | Zero findings after the edit | Verb-by-verb diff of the page against the frozen inventory: every registered verb present, no unregistered verb present; all internal links resolve; the three pointers (docs index, root README, root AGENTS.md) exist |
| R5 | `.apm/skills/kyber-weave-cli/` | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-weave-cli`; `skill lint .apm/skills/kyber-weave-cli --min-desc-score 70`; `skill scan .apm/skills/kyber-weave-cli --fail-on critical`; `skill route` probes ("install kyber-weave", "update kyberdash", "configure kyber-weave", "install kyberdash") | All gates exit 0; the route probes resolve to the entry-point skill above `--min-accuracy` | Registration check (apm target if required) recorded |
| R6 | n/a (documents) | `docs validate . -f json`; `docs drift . -f json` | Zero findings after the edit | Items 1, 2, 3 and 5 closed with before/after quoted and per-claim source verification; item 4 either fixed from unambiguous source truth or reported unresolved with the investigation's evidence; `last-reviewed` bumped on every governed document touched |
| C1 | n/a (documents + archive) | `docs validate . --merge-ready`; `docs drift .` | Zero findings with the plan archived | Closeout mapping links every durable decision to its destination; index row updated; archive date recorded |

Changing this contract returns the plan to Draft and requires the conductor to relay
reapproval before implementation resumes.

## Tasks

### Assessment phase (dispatchable on plan approval)

#### T1: Mechanical corpus verification (read-only)

**Required skill:** `docs-dev`, following `app-docs-standard`.

**Files (read-only):** `docs/` corpus; specifically `docs/dash/README.md`,
`docs/dash/architecture.md`, `docs/dash/runbook.md`, `docs/dash/telemetry-inventory.md`,
`docs/catalog.md`, `docs/README.md`, `README.md`, `docs/documentation-ontology.md`.

**Objective:** Run the mechanical gates; build the per-document frontmatter
conformance matrix for the KyberDash set against the required-key matrix; verify the
catalog row's currency and link resolution; verify the two README listings match the
actual document set; name any document the app-docs standard's required shape calls
for that the KyberDash set lacks, as a finding (never a silent fix).

**Acceptance:** The gate outputs are captured verbatim (or drift recorded skipped
with reason); the matrix covers every KyberDash document; listing mismatches and
shape gaps are findings with `file:line`. No file is written.

**Depends on:** none.

#### T2: CLI command-surface truth reconciliation (read-only)

**Required skill:** `docs-dev`, following `app-docs-standard`.

**Files (read-only):** `src/KyberWeave.Cli/Program.cs`, `src/KyberWeave.Cli/Commands/**`
(every `*Settings` class), `src/KyberWeave.Cli/AGENTS.md`, the `kyberdash` CLI surface
under `dash/src/` (verb registration and flags), `dash/package.json`,
`scripts/install.sh`; and every corpus page that states a verb, flag, exit code,
harness count, or install/update path: `docs/README.md`, `README.md`,
`docs/context-hygiene/skills.md`, `docs/context-hygiene/agents.md`,
`docs/ci-pipelines/rule-reference.md`, `docs/code-review/README.md`,
`docs/code-review/architecture.md`, `docs/install.md`, `docs/distribution.md`,
`docs/configuration.md`, `docs/dash/runbook.md`, `docs/dash/README.md`,
`docs/kyber-squad/`, `docs/kyber-utilities/`, root `AGENTS.md`.

**Objective:** Build the complete reconciled inventory of every registered
`kyber-weave` and `kyberdash` verb and flag with source lines; then classify every
documented claim as true or false with evidence. Seed findings already verified
(intake re-verified in this plan's investigation): the phantom `skill review
export`/`import` (`docs/context-hygiene/skills.md:38-39`, `:110`;
`docs/ci-pipelines/rule-reference.md:90`) against the real `docs review
export`/`import`; "the two commands" (`docs/README.md:136`) against the three
registered review verbs; "10 IDE/CLI harnesses" (`README.md:37`) against twelve;
`kyber-weave dash refresh` (`docs/dash/runbook.md:576`) against the absent `dash`
branch, including the ADR 0016 tension. Complete the flag-level `Settings` sweep the
prior intake left unverified. Settle the npm question (`npm view kyberdash` or the
registry query, recorded with the command used) and the `dotnet tool` path's
user-reachability.

**Acceptance:** The inventory covers every branch, verb, and flag with source lines;
every seed finding is confirmed or refuted with evidence; every claim classification
carries both sides (`file:line` claim vs `file:line`/symbol truth); the npm and
dotnet-tool questions are answered with the query recorded. No file is written.

**Depends on:** none.

#### T3: Audience-journey walkthroughs (read-only)

**Required skill:** `docs-dev`, following `app-docs-standard`.

**Files (read-only):** the corpus pages a fresh human or an agent would traverse:
`README.md`, `AGENTS.md`, `docs/README.md`, `docs/install.md`, `docs/distribution.md`,
`docs/configuration.md`, `docs/dash/README.md`, `docs/dash/runbook.md`,
`docs/catalog.md`, `.apm/skills/`.

**Objective:** Walk both journeys exactly as defined in [Design](#design) and record
every gap: the human journey step by step (choose → install → verify → update →
configure, for both products and every install path the corpus names), and the agent
journey from the repository's entry surfaces to the command surface and canonical
approaches. Record the agent-journey baseline explicitly (no unified page, no
entry-point skill today) and state the after-criteria R4/R5 will be held to.

**Acceptance:** Both gap lists are complete, each gap naming the missing or stale
page and section; the agent-journey baseline names every entry surface checked. No
file is written.

**Depends on:** none.

#### T4: Assessment report authorship

**Required skill:** `docs-dev`, following `app-docs-standard`.

**Files (write):** `artifacts/docs-corpus-assessment-2026-10-15.md` only. It is
outside `docs/`, so no doc gate re-run is required — state that in the task result.

**Objective:** Consolidate T1–T3 into the assessment report carrying exactly the
fields named in [Design](#design): mechanical evidence, findings table (id,
severity, `file:line`, claim, evidence, proposed cluster or "gate decision"),
the frozen command-surface inventory, both journey gap lists, answers to the open
questions (npm, dotnet tool, phantom-verb disposition, ADR 0016 tension), and the
file-ownership map with disjoint cluster file sets (hub files listing their single
owning task per edit).

**Acceptance:** Every schema field is present; every finding has evidence on both
sides; the ownership map's clusters are file-disjoint except where this plan
declares a dependency (R4 on R1/R3); no finding is silently dropped or silently
clustered — ambiguous ones are marked "gate decision".

**Depends on:** T1, T2, T3 (consumes their outputs).

### Human review gate (plan-level stop)

Not a dispatchable task. After T4, the conductor stops and runs the
[human review gate](#human-review-gate). The gate **passed 2026-10-15**: its
outcomes are recorded in [Decisions](#decisions) (D11–D20) and the approved
report's §7/§8 were amended to match before any remediation dispatch. R1–R5 were dispatched per
the dependency graph and are complete (audited PASS); R6 — added 2026-10-15
for the user-dispositioned execution findings ([D21–D27](#decisions)) — is
dispatchable; C1 additionally waits on R6.

### Remediation phase (dispatchable only after gate approval)

Every remediation packet embeds its cluster's findings verbatim from the approved
report; workers do not read this plan's body. Each task's file scope is the seed
scope here plus its cluster's confirmed findings per the report's file-ownership
map. Bump `last-reviewed` on every document touched. Every task re-runs
`docs validate .` and `docs drift .` as its final step. If a cluster is empty after
gate edits, its task is closed as a no-op with the gate decision cited — never
guessed work.

#### R1: CLI truth fixes across the corpus

**Required skill:** `docs-dev`, following `app-docs-standard` and `kyber-weave-docs`.

**Files (seed scope, confirmed/extended by the approved cluster):**
`docs/context-hygiene/skills.md`, `docs/ci-pipelines/rule-reference.md`,
`docs/README.md`, `README.md`, plus every additional file the approved report's
R1 cluster names.

**Objective:** Fix every confirmed stale CLI claim to match the reconciled surface:
the phantom `skill review export`/`import` rows and `KW-SKILL-REVIEW-001` mention
(removed or re-pointed to `docs review export`/`import`, per the gate's disposition
decision); "the two commands" → the three review verbs; "10 IDE/CLI harnesses" →
twelve; and every additional R1-cluster finding, each fixed against its evidence.

**Acceptance:** Every R1-cluster finding is closed with its before/after quoted;
no R1 file mentions an unregistered verb or a wrong count; `docs validate .` and
`docs drift .` zero findings.

**Scope amendments (human review gate, 2026-10-15):** R1 additionally owns
**F-T2-8 and F-T2-9** ([D18](#decisions), GD-8a): update the canonical agent
count to **22** — the roster in `products/kyber-squad/bundles/full.yml:3-25` —
in `docs/README.md:119`, `README.md:155`/`:253`, `docs/kyber-squad/README.md:28,42`,
and `docs/kyber-squad/onboarding.md:25`, and reconcile
`docs/code-review/architecture.md:144`'s "from 20 to 35" to the 22-agent roster;
`full.yml` is the source of truth, not an edit target (no code change). R1's
file set gains `docs/kyber-squad/README.md`, `docs/kyber-squad/onboarding.md`,
and `docs/code-review/architecture.md`. The phantom-verb disposition is decided
([D14](#decisions), GD-4a): remove the phantom `skill review` rows and re-point
the `:110` reference to `docs review`; **keep** the `KW-SKILL-REVIEW-001` row in
`docs/ci-pipelines/rule-reference.md` and correct only its trigger description.

**Depends on:** human review gate approval (D3). Consumes the approved report.

#### R2: User-facing install, update, and configure flow (humans)

**Required skill:** `docs-dev`, following `app-docs-standard` and `kyber-weave-docs`.

**Files (seed scope, confirmed/extended by the approved cluster):** `docs/install.md`,
`docs/distribution.md` (cross-links only), `README.md` install paragraphs if the
cluster names them (per the file-ownership map), plus the cluster's additional files.

**Objective:** Close the human-journey gaps from the approved report: bring the
`npm` path for `kyberdash` and the `dotnet tool` path into user-facing docs **or**
explicitly scope them maintainer-only with the gate decision cited (the gate decides
which, informed by T2's npm answer); keep the flag tables at flag-level parity with
`scripts/install.sh` and the self-updater; make the verify/update/configure steps of
the human journey resolve end to end.

**Acceptance:** Every human-journey gap in the approved report is closed or
explicitly declined with the gate decision cited; flag parity re-verified against
T2's inventory; `docs validate .` and `docs drift .` zero findings.

**Scope amendments (human review gate, 2026-10-15):** R2 additionally owns
**F-T3-10** ([D19](#decisions), GD-9a): fold the "which product / which install
path" decision block into `docs/install.md`. The two scope questions the
objective left to the gate are decided: npm and homebrew are stated explicitly
as unsupported for KyberDash, `install.sh` being the only documented user path
([D12](#decisions), GD-2a); and `dotnet tool` is documented as unsupported / out
of scope for end users ([D13](#decisions), GD-3a).

**Depends on:** human review gate approval (D3). Consumes the approved report.

#### R3: KyberDash completeness and consistency rework

**Required skill:** `docs-dev`, following `app-docs-standard` and `kyber-weave-docs`.

**Files (seed scope, confirmed/extended by the approved cluster):**
`docs/dash/README.md`, `docs/dash/architecture.md`, `docs/dash/runbook.md`,
`docs/dash/telemetry-inventory.md`, `docs/catalog.md` (the KyberDash row), the
KyberDash listings in `docs/README.md` and `README.md` (per the file-ownership map),
plus the cluster's additional files.

**Objective:** Close every approved KyberDash finding: frontmatter and shape gaps
from T1, stale claims from T2 (including `docs/dash/runbook.md:576`'s
`kyber-weave dash refresh` and the ADR 0016 disposition the gate chose), and
listing/consistency fixes — including any document the gate decided the standard's
required shape needs (created only on that explicit decision).

**Acceptance:** Every R3-cluster finding closed with before/after quoted; the
KyberDash set, catalog row, and both listings are mutually consistent; the ADR
disposition matches the gate decision; `docs validate .` and `docs drift .` zero
findings.

**Scope amendments (human review gate, 2026-10-15):** R3 **drops F-T1-2**
([D11](#decisions), GD-1a) — reclassified as plan-lifecycle housekeeping,
resolved at the gate by the recording save itself (the plan `## Status` wording
and the Active Plans row in `docs/plans/README.md` were reworded); R3 neither
owns F-T1-2 nor edits the plan or the plan index. R3 gains: **F-T1-3 and
F-T1-4** ([D16](#decisions), GD-6a) — create `docs/dash/onboarding.md` and
`docs/dash/requirements.md`; **GD-5a** ([D15](#decisions)) — amend ADR 0016 in
place to correct Decision 1 and its status line (moved here from C1's
conditional) and fix `docs/dash/runbook.md:576` (F-T2-6, F-T2-7); and **GD-7a**
([D17](#decisions)) — promote `docs/dash/telemetry-inventory.md` to
`status: current` and refresh the `KyberDash` catalog row's `Last reviewed`
(F-T1-5).

**Depends on:** human review gate approval (D3). Consumes the approved report.

#### R4: The unified CLI command-surface page

**Required skill:** `docs-dev`, following `app-docs-standard` and `kyber-weave-docs`.

**Files (write):** `docs/cli-command-surface.md` (new, `doc-type: reference`,
`status: current`, `id: cli-command-surface`, owner per catalog), plus the
discoverability pointers: a listing in `docs/README.md`, a pointer in `README.md`,
and a row in root `AGENTS.md`'s "Where to go next" table. Root `AGENTS.md` is
hand-authored — edit only that table row, nothing generated.

**Objective:** Build the page per [Design](#design): the two-audience structure;
the complete verb/flag inventory from the report's frozen reconciliation, every
entry linking to its owning feature page (D7 — the by-feature design stays
canonical); the canonical install, update, and configure approaches for both
products; the exit-code rules table. Add the three pointers. Extend nothing else.

**Acceptance:** Verb-by-verb diff against the frozen inventory passes (every
registered verb present, no unregistered verb present); all internal links resolve;
the three pointers exist; the human- and agent-facing halves each carry the
canonical flows; `docs validate .` and `docs drift .` zero findings.

**Depends on:** human review gate approval (D3); R1 and R3 (certain file overlap on
the hub files `docs/README.md` and `README.md`). Consumes the approved report's
frozen inventory.

#### R5: The agent entry-point skill

**Required skill:** `docs-dev`, following `kyber-weave-docs` (the skill governs
SKILL.md authorship constraints; `.apm/skills/` is the validated root).

**Files (write):** `.apm/skills/kyber-weave-cli/SKILL.md` (new; name is free today),
plus any registration the repository requires for a new skill in that root (check
`apm.yml` targets; add only if required).

**Objective:** Author the entry-point skill: a routing description that fires on
install, update, and configure requests for Kyber-Weave or KyberDash; a body
carrying the canonical commands and pointing at `docs/cli-command-surface.md`'s
anchors. This is the mechanism that makes D4's testable criterion true: an agent
asked to do this work finds the page.

**Acceptance:** `skill validate`, `skill lint --min-desc-score 70`, and
`skill scan --fail-on critical` all pass on the new skill; `skill route` probes
("install kyber-weave", "update kyberdash", "configure kyber-weave", "install
kyberdash") resolve to it above `--min-accuracy`; registration recorded.

**Depends on:** R4 (consumes the page's path and anchors). After gate approval by
transitivity.

#### R6: Post-remediation truth fixes from the execution findings collection

**Required skill:** `docs-dev`, following `app-docs-standard` and `kyber-weave-docs`.

**Files (write):** `products/kyber-squad/README.md`, `docs/context-hygiene/agents.md`,
`docs/dash/telemetry-inventory.md`, `docs/adr/README.md`,
`docs/kyber-squad/architecture.md`; plus root `README.md` **only if** the
item-4 investigation shows its harness-class table is the wrong side
([D24](#decisions)). No other file.

**Objective:** Close the collection items the user dispositioned for fixing
(items 1, 2, 3, 5 — and item 4 conditionally), each against its recorded
evidence class. Item 1 ([D21](#decisions), verified): add the missing
`bug-crusher.md` entry to `products/kyber-squad/README.md`'s hand-maintained
agent directory tree (the agent exists on disk; the file's own header already
says 22). Item 2 ([D22](#decisions), verified): correct
`docs/context-hygiene/agents.md:58` — "the 23-skill namespace" → 24 and
"seven names" → the current agent∩skill intersection (8). Item 3
([D23](#decisions), audit-confirmed): correct
`docs/dash/telemetry-inventory.md:17`'s "schema 15" → schema 20
(`SCHEMA_VERSION` pinned by `dash/src/canon/migration-v20.test.ts:113`;
ADR 0038). Item 5 ([D25](#decisions), audit-confirmed): add the
`Amended 2026-10-16` correction note to `docs/adr/README.md`'s ADR-0016 summary
row (the ADR body carries R3's amendment; this cluster owns the ADR index
edit). Item 4 ([D24](#decisions), unverified pending investigation) is
**investigation-first**: determine from source truth whether
`docs/kyber-squad/architecture.md:520`'s Native classification of `antigravity`
or root `README.md`'s harness-class table (Role-Skill Lowering) is right; fix
the demonstrably wrong side only if the source is unambiguous; if both
classifications are genuinely defensible, change nothing and report — that
outcome escalates to the conductor, never a worker guess.

**Packet note:** the approved report does not contain these items (they
surfaced during execution); the R6 dispatch packet embeds items 1–5 verbatim
from [D21–D25](#decisions), which are this cluster's input of record. Whether
the frozen report gains a supplement recording the collection is the
conductor's call, not this cluster's.

**Acceptance:** Items 1, 2, 3 and 5 closed with before/after quoted and
per-claim source verification cited; item 4 either fixed from unambiguous
source truth or reported unresolved with the investigation's evidence;
`last-reviewed` bumped on every governed document touched;
`docs validate . -f json` and `docs drift . -f json` zero findings.

**Depends on:** R1–R5 (this cluster re-touches files the completed remediation
edited, so it runs after all remediation). Consumes [D21–D25](#decisions).
C1 closeout depends on R6.

#### C1: Closeout — harvest, index sync, archive

**Required skills:** `docs-dev`, following `app-docs-standard`, `kyber-weave-docs`,
and `architecture-decision-record` if the gate decisions warrant an ADR.

**Files (write):** `docs/plans/README.md` (index row to Archived, archive date,
closeout mapping), `docs/archive/plans/2026-10-15-docs-two-audience-completeness.md`
(the moved plan), `src/KyberWeave.Cli/AGENTS.md` ("Documented behaviour": the D7
extension — a verb/flag/exit-code change updates the matching feature page and the
unified page in the same change), and the canonical destinations of any durable
facts the gate decisions named (the ADR 0016 amendment-in-place the gate chose
under GD-5 — recorded as [D15](#decisions) — is performed by R3's amended scope;
C1 harvests its durable outcome and does not re-edit the ADR).

**Objective:** Migrate durable facts into canonical documentation; provide the
closeout mapping linking each durable decision (D1–D9 plus gate decisions) to its
destination; state `no ADR — ADR waiver: <reason>` or the created ADR per the plan
index's waiver policy; move the plan to `docs/archive/plans/`; update the plan index.

**Acceptance:** Closeout mapping complete; index row updated and reachable;
`docs validate . --merge-ready` and `docs drift .` zero findings with the plan
archived; the permanent `## Decisions` section preserved intact in the archived
copy.

**Depends on:** R1, R2, R3, R4, R5, R6 — C1 closeout waits on R6 (the R6 fixes
are part of the remediated corpus C1 harvests and archives).

## Dependency graph

```text
T1 ─┬─> T4 ──> HUMAN REVIEW GATE ──┬─> R1 ──┐
T2 ─┤                             │         │
T3 ─┘                             ├─> R2 ──┤──> R6 ──> C1
                                  ├─> R3 ──┤    ▲
                                  └─> R4 (needs R1, R3) ──> R5 ──┘
                                     (gate approval)
```

- T1, T2, T3: parallel, read-only, no file overlap (all evidence flows into T4's report).
- T4: after T1–T3 (consumes their outputs).
- **Human review gate**: a hard barrier. R1–R5 and C1 were not dispatchable before
  approval (D3); the gate passed 2026-10-15 (D11–D20), so they were.
- R1, R2, R3: parallel after the gate (disjoint file sets per the report's
  file-ownership map; hub-file edits assigned one owner per edit).
- R4: after the gate and after R1 and R3 (certain overlap on `docs/README.md` and
  `README.md`).
- R5: after R4 (consumes the page's anchors).
- R6: after all remediation (R1–R5) — it re-touches files the completed clusters
  edited and closes the user-dispositioned execution findings
  ([D21–D25](#decisions)); added 2026-10-15 and dispatchable now that R1–R5 are
  complete and audited PASS.
- C1: after all remediation, including R6.

Rework bound (D9): at most 2 rework dispatches per failure cluster; a third failure
escalates to the conductor with the cluster's evidence.

## Risks

- **The assessment may surface more than the seed findings** — including the ADR
  0016 tension and any standard-shape gap. All such items are gate decisions, never
  worker guesses; the gate exists precisely to bound this risk.
- **The npm question may be unanswerable live** (registry unreachable). T2 records
  the query and the failure; R2 then scopes the npm path per the gate decision with
  the uncertainty stated honestly in the document.
- **Cluster file sets may not partition cleanly.** Mitigated by the report's
  file-ownership map requirement (disjoint sets, hub files with a single owner per
  edit) and the R4 dependencies.
- **Hub-file merge conflicts** if parallel remediation tasks edit
  `docs/README.md`/`README.md`. Mitigated by the ownership map and the R4→R1/R3
  serialization.
- **`docs drift` depends on the CodeGraph index**; on a runner without one it is
  recorded as skipped with the reason, never as passed.
- **Scope creep into code** (implementing the phantom verbs). Explicitly
  out-of-scope; the gate routes any such wish to a todo or a new plan.

## Review

- Every task is audited by its `task-reviewer` against its row in the
  [verification contract](#verification-contract-standard-mode), immediately when
  the worker finishes; rework follows D9's bound (2 dispatches per failure cluster,
  then escalation).
- Read-only assessment tasks are audited on their delivered evidence, not on gate
  output alone.
- After C1's edits and before archive, a final review-council pass over the whole
  change (the repository's standard four-lens council), citing the full gate suite
  plus the doc gates, per repo convention.

## docs-dev closeout

C1 is the closeout task (see above): durable-fact migration with a closeout mapping,
plan-index synchronization, archive to `docs/archive/plans/`, `--merge-ready` and
drift re-run, and the permanent `## Decisions` section preserved intact.

## Closeout

Completed 2026-10-15. The plan delivered 26 assessed findings across R1–R6 plus
the seven later items D21–D27, then C1 harvested the durable facts and archived
the plan. R1 corrected stale CLI claims corpus-wide, removed the phantom
`skill review` verbs while keeping the real `KW-SKILL-REVIEW-001` rule row, and
reconciled the canonical counts (22 agents, 24 skills, 12 harnesses, the 123-file
Copilot render, the eight-name agent∩skill intersection with two shared
identities). R2 completed `docs/install.md` with the product/install-path
decision block, an explicit unsupported-channels statement, and the tray
install/update path. R3 corrected the `antigravity` harness classification,
added `docs/dash/onboarding.md` and `docs/dash/requirements.md`, promoted
`docs/dash/telemetry-inventory.md` to `current` (schema 20), and amended
[ADR 0016](../../adr/0016-kyberdash-harness-source-refresh.md) in place. R4
created `docs/cli-command-surface.md` with three discoverability pointers; R5
wired the entry-point skill `.apm/skills/kyber-weave-cli/` into the declared gate
suite; R6 closed the user-dispositioned execution findings and registered
`docs/todo/squad-pack-single-target-render.md`.

### Decision-to-canonical mapping

Harvested means the decision's content is in the named canonical document. The
plan index's two waiver categories are used for the rest: an **ADR waiver** where
the decision refines existing architecture without a new lasting record, and a
**documentation waiver** for items that are purely transient or historical with
no surviving architectural, operational, or behavioural invariant.

| ID | Decision (short form) | Destination in canonical docs | Status |
|---|---|---|---|
| D1 | This work is a bounded plan under `docs/plans/` | Plan lifecycle; the archived plan itself is the durable record | Documentation waiver — plan-lifecycle metadata, transient, no surviving invariant |
| D2 | KyberDash completeness means verification plus rework as needed | The completed and corrected KyberDash set: [dash/README.md](../../dash/README.md), [dash/architecture.md](../../dash/architecture.md), [dash/runbook.md](../../dash/runbook.md), [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md), [dash/onboarding.md](../../dash/onboarding.md), [dash/requirements.md](../../dash/requirements.md), and the KyberDash row in [catalog.md](../../catalog.md) | Harvested |
| D3 | Assessment first, then an explicit human review gate, then remediation | Assessment report `artifacts/docs-corpus-assessment-2026-10-15.md` (transient evidence outside the governed corpus); the outcomes are D2/D4–D20 | Documentation waiver — process sequencing of this change, transient, no surviving invariant beyond the report and this table |
| D4 | Add a unified CLI command-surface page and make it agent-discoverable | [cli-command-surface.md](../../cli-command-surface.md); entry-point skill [.apm/skills/kyber-weave-cli/SKILL.md](../../../.apm/skills/kyber-weave-cli/SKILL.md); pointers in [docs/README.md](../../README.md), [`README.md`](../../../README.md), and root [`AGENTS.md`](../../../AGENTS.md) | Harvested |
| D5 | Both audience flows must be complete | Human flow: [install.md](../../install.md) and [dash/onboarding.md](../../dash/onboarding.md). Agent flow: [cli-command-surface.md](../../cli-command-surface.md) and [.apm/skills/kyber-weave-cli/SKILL.md](../../../.apm/skills/kyber-weave-cli/SKILL.md) | Harvested |
| D6 | Development mode `standard` | Plan frontmatter and the [verification contract](#verification-contract-standard-mode) | Documentation waiver — process metadata of this change, transient, no surviving invariant |
| D7 | The unified page is an aggregation index that preserves the by-feature design; a verb/flag/exit-code change updates both pages in the same change | [cli-command-surface.md](../../cli-command-surface.md) (the aggregation-index statement) and [src/KyberWeave.Cli/AGENTS.md](../../../src/KyberWeave.Cli/AGENTS.md) "Documented behaviour" | Harvested |
| D8 | The assessment report lives in `artifacts/`, outside the governed corpus | `artifacts/docs-corpus-assessment-2026-10-15.md`; durable facts harvested here into `docs/` per this mapping | Documentation waiver — transient review evidence outside the governed corpus, with no surviving invariant once harvested |
| D9 | At most two rework dispatches per failure cluster | Execution policy recorded in the plan's [Review](#review) section | Documentation waiver — execution policy of this change, transient, no surviving product invariant |
| D10 | Plan approved as authored ("approve and execute") | Plan Status line and the [Decisions](#decisions) table | Documentation waiver — historical approval provenance, transient, no surviving invariant |
| D11 | Harmonize the residual "Ready"/"current" wording (GD-1); frontmatter stays `status: current` | [docs/plans/README.md](../../plans/README.md) index wording; this plan's frontmatter (`status: current` → `complete` at archive) | Harvested |
| D12 | npm and homebrew are unsupported for KyberDash; `install.sh` is the only documented user path (GD-2) | [install.md](../../install.md) "Install channels that are not supported"; root [`README.md`](../../../README.md) install paragraph | Harvested |
| D13 | `dotnet tool` is unsupported / out of scope for end users (GD-3) | [install.md](../../install.md) "Install channels that are not supported"; [distribution.md](../../distribution.md) "Other published artifacts" | Harvested |
| D14 | Remove the phantom `skill review` verbs; keep the real `KW-SKILL-REVIEW-001` row and correct only its trigger description (GD-4) | [context-hygiene/skills.md](../../context-hygiene/skills.md); [ci-pipelines/rule-reference.md](../../ci-pipelines/rule-reference.md) | Harvested |
| D15 | Amend ADR 0016 in place and fix `runbook.md:576` (GD-5) | [ADR 0016](../../adr/0016-kyberdash-harness-source-refresh.md) (Decision 1 and the status line); [dash/runbook.md](../../dash/runbook.md); [adr/README.md](../../adr/README.md) summary row | Harvested |
| D16 | Add `docs/dash/onboarding.md` and `docs/dash/requirements.md` (GD-6) | [dash/onboarding.md](../../dash/onboarding.md); [dash/requirements.md](../../dash/requirements.md); linked from [catalog.md](../../catalog.md), root [`README.md`](../../../README.md), and [dash/README.md](../../dash/README.md) | Harvested |
| D17 | Promote `dash/telemetry-inventory.md` to `current` and refresh the catalog row (GD-7) | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) frontmatter; [catalog.md](../../catalog.md) KyberDash row | Harvested |
| D18 | Update the canonical agent count to 22 and reconcile "from 20 to 35" (GD-8) | [docs/README.md](../../README.md), root [`README.md`](../../../README.md), [kyber-squad/README.md](../../kyber-squad/README.md), [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md), [code-review/architecture.md](../../code-review/architecture.md) | Harvested |
| D19 | Fold the product-choice / install-path decision block into `docs/install.md` (GD-9) | [install.md](../../install.md) "Which product, and which install path" | Harvested |
| D20 | Report every deviation observed by the assessment (GD-10) | Assessment report §8/§9 and this plan's [Decisions](#decisions) table | Documentation waiver — assessment-report policy, transient, with no surviving invariant beyond the recorded deviations |
| D21 | Add the missing `bug-crusher.md` entry to the agent directory tree | [products/kyber-squad/README.md](../../../products/kyber-squad/README.md) tree | Harvested |
| D22 | Correct `agents.md:58`: 24-skill namespace and the eight-name agent∩skill intersection | [context-hygiene/agents.md](../../context-hygiene/agents.md) | Harvested |
| D23 | Correct the store schema claim to 20 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) | Harvested |
| D24 | Investigate the `antigravity` classification and fix the demonstrably wrong side | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) (Native) reconciled with the root [`README.md`](../../../README.md) harness-class table (Antigravity under Native Agents) | Harvested |
| D25 | Add the `Amended 2026-10-16` note to the ADR-0016 summary row | [adr/README.md](../../adr/README.md) ADR-0016 row | Harvested |
| D26 | Capture the single-target-render tooling gap as a todo | [todo/squad-pack-single-target-render.md](../../todo/squad-pack-single-target-render.md), registered in [todo/README.md](../../todo/README.md) | Harvested |
| D27 | No action on the `Program.cs` line-number meta-observation | [cli-command-surface.md](../../cli-command-surface.md) carries no line citations (the durable consequence) | Documentation waiver — a meta-observation about evidence gathering that asserts no corpus claim; nothing survives to document |
| D28 | R1's divergence from D14's literal wording: the refusal is upheld, D14's intent corrected | [context-hygiene/skills.md](../../context-hygiene/skills.md) and [ci-pipelines/rule-reference.md](../../ci-pipelines/rule-reference.md) (the corrected result) | Harvested |
| D29 | The Copilot render total is 123, on empirical evidence; "45 principal files" became 46 | [kyber-squad/README.md](../../kyber-squad/README.md), [kyber-squad/architecture.md](../../kyber-squad/architecture.md), [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md), [kyber-squad/requirements.md](../../kyber-squad/requirements.md), [products/kyber-squad/README.md](../../../products/kyber-squad/README.md) | Harvested |
| D30 | The "21 canonical agents" occurrence set was completed beyond the assessed lines | The D18 destinations plus [products/kyber-squad/README.md](../../../products/kyber-squad/README.md), [kyber-squad/requirements.md](../../kyber-squad/requirements.md), [kyber-squad/architecture.md](../../kyber-squad/architecture.md), [context-hygiene/agents.md](../../context-hygiene/agents.md), and [distribution.md](../../distribution.md) | Harvested |
| D31 | Root `README.md`'s architecture-tree line now names the full `kyber-weave` surface | Root [`README.md`](../../../README.md) architecture tree (`skill \| agent \| squad \| docs \| utilities \| review \| update`) | Harvested |
| D32 | An edit to `.kyber-weave/kyber-weave.yml` escalates a review to a human by design | [`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml) `review.policy.always-human` (canonical repository configuration, not a governed `docs/` document) | Harvested |
| D33 | The council's `NEEDS_HUMAN` (`KW-REVIEW-008`) was resolved by the owner's review and push/PR direction | This plan's Status prose and the [Open items](#open-items-disclosed-not-fixed) council record | Documentation waiver — historical human-review acceptance provenance, transient, no surviving invariant |
| D34 | The plan is archived by the finishing pull request, with the index row and closeout mapping | [docs/plans/README.md](../../plans/README.md) Archived row; `docs/archive/plans/2026-10-15-docs-two-audience-completeness.md` | Harvested |

**ADR disposition: no ADR — ADR waiver.** The plan corrects existing documentation to
match shipped behaviour and adds an aggregation index plus an entry-point skill. It
refines the existing CLI, KyberDash, and Kyber-Squad surfaces under established records
([ADR 0016](../../adr/0016-kyberdash-harness-source-refresh.md), amended in place per
[D15](#decisions), plus [ADR 0021](../../adr/0021-zcode-command-lowering-and-resource-relocation.md),
[ADR 0022](../../adr/0022-antigravity-native-agents.md),
[ADR 0028](../../adr/0028-devin-target-scoped-authoring-capability-profiles.md),
[ADR 0033](../../adr/0033-kyberdash-surfaces-are-display-layers.md), and
[ADR 0038](../../adr/0038-post-run-transcript-sync-subagent-hierarchy.md)). It adds no
new architectural invariant, dependency, boundary, or threshold, so no new ADR is
warranted.

**Documentation waivers.** The items marked above are the only documentation waivers:
plan-lifecycle and process metadata (D1, D3, D6, D9, D10, D20, D33), the transient
assessment evidence (D8), and the evidence-gathering meta-observation (D27). Each is
historical or transient with no surviving architectural, operational, or behavioural
invariant. No decision was waived on the ground that "no existing doc describes it".

### Open items (disclosed, not fixed)

The owner has not dispositioned these two findings, so they are recorded here rather
than fixed. Neither file was edited by C1.

1. **`docs/dash/runbook.md:253` schema label (council finding, minor).** The page
   labels `refresh_run.history_weeks` as "schema 15", while
   [dash/architecture.md:600](../../dash/architecture.md) records that schema 16 added
   the column and the store schema is 20
   ([dash/telemetry-inventory.md:17](../../dash/telemetry-inventory.md) agrees on 20).
   The label is stale; the correction is open.
2. **`README.md:166` shared-identity wording.** The line reads "shared conductor
   identities", but `conductor` is the unshared, unoccupied-skill identity that lowers
   to a same-name role skill; the shared identities are `product-owner` and
   `bug-crusher` ([context-hygiene/agents.md](../../context-hygiene/agents.md)). The
   wording is imprecise; the correction is open.

### Review and gate evidence

- **Whole-change council.** Verdict `NEEDS_HUMAN` under `KW-REVIEW-008`, because
  `.kyber-weave/kyber-weave.yml` matches `review.policy.always-human` — the designed
  escalation recorded as [D32](#decisions). The human owner reviewed the change and
  directed push and pull-request submission ([D33](#decisions)), satisfying the
  required human review. The council raised one minor finding: the
  `docs/dash/runbook.md:253` schema label disclosed as open item 1.
- **TypeScript gates skipped (environment).** The `dash/` TypeScript gates
  (`typecheck`, `lint`, `test`, `check:reachable`) were not run in this closeout
  environment; this plan changed no `dash/` source, so they are not claimed as
  passed here.
- **Documentation gates.** `docs validate . --merge-ready`, `docs validate .`, and
  `docs drift .` were run at closeout on 2026-10-15 with the plan archived; all three
  reported `{"summary":{"critical":0,"error":0,"warning":0,"info":0},"findings":[]}`.
- **Archive.** Moved to `docs/archive/plans/2026-10-15-docs-two-audience-completeness.md`;
  the permanent `## Decisions` section (D1–D34) is preserved intact.
