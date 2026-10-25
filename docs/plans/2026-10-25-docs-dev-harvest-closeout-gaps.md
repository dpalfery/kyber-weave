---
id: plans/2026-10-25-docs-dev-harvest-closeout-gaps
title: "docs-dev design harvest: authoring scale, closeout contract, every-change docs review, change-scoped merge gate, Kyber-Docs skill renames, three backfills"
doc-type: plan
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-25
development-mode: test-first
documentation-impact: extends-component
impact-components: [DocGraph, KyberSquad, CI Pipelines, ContextHygiene, Distribution]
---

# docs-dev design harvest

**Status: Ready — approved at the approve-and-execute gate 2026-10-25 (AP1): the owner chose
"approve and execute this plan" and confirmed the documentation-impact scales (D23, D24
`extends-component`). Rewritten 2026-10-25 from the owner's alignment record (G1–G19); Q4 was
answered by the owner 2026-10-25. `docs validate` and `docs drift` are outstanding for this
edit: the authoring harness has no process tool and nested agents are disabled, so the
conductor runs both before execution (as on the 2026-10-23 pi-renderer plan).**

**Development mode:** `test-first`. **Autonomy:** `supervised` (packet).

**Working copy:** worktree `.claude/worktrees/harvest-gaps`, branch `docs/harvest-closeout-gaps`
(last commit `f0368f0d`, cut from `origin/main` `a0862e7c`). Paths are relative to the worktree.
Literal paths in this plan describe **this** repository. The portable agent and skill text this
plan changes must not contain them (G4).

## Problem and goal

### Problem

Kyber-Weave is a meta-harness: this repository's ontology, configuration and catalog are one
host's setup, and every host has its own. `docs-dev` exists to harvest a plan's or spec's
**design** — architecture, behaviour and feature set, interfaces, operations, decisions — into
the host's canonical documentation, routed by the host's own document identities.

Today it falls short in four ways:

1. **Closeouts are skipped** (D3). Three plans were archived 2026-10-22..24 without a real
   closeout, and each still carries `status: draft` with a body status of `Ready`:
   - #423, `docs/archive/plans/2026-10-22-opencode-span-turn-hooks-kyberdash.md`: its closeout
     admitted a `RESIDUAL` and archived anyway.
   - #425, `…/2026-10-23-opencode-context-capture-kyberdash.md`: moved as an unchanged
     rename (`8beb97ae`, R100) "to satisfy KW-DOC-LIFECYCLE-003".
   - #426, `…/2026-10-23-pi-renderer-network-read-mcp.md`: added straight into the archive
     while its body said Ready (`6dd4ce3f`, merged as `0351a45c` on 2026-10-24).
2. **Nothing mechanical catches it.** `KW-DOC-LIFECYCLE-003` only checks that no plan or spec
   is still open.
3. **The contract is decisions-only and plans-only.** Nothing declares the documentation scale
   of a change. Specs have no decision mapping. Changes made without a plan (bug fixes, todos,
   chat requests) get no documentation review at all.
4. **Kyber-Docs skill naming and placement are inconsistent.** The authoring skill is called
   `kyber-weave-docs`. The documentation-architecture skill (`second-brain`) ships with
   Kyber-Squad, not Kyber-Docs, and it has no way to assess legacy plans for unharvested
   design.

### Goal

| Item | Goal | Owner decisions |
|---|---|---|
| A — Routing | `docs-dev` leads with closeout as a design harvest, plus the every-change documentation review. It routes by host document identity (doc type + catalog component), never by folder. | G1–G7 |
| B — Authoring | Plans and specs declare a scale and impact components, plus a Design harvest list that targets component + doc type. The owner confirms at approve-and-execute. Spec parity with plans. | G8, G9 |
| C — Every-change review | The conductor and bug-crusher send every file-changing run to `docs-dev`. With no plan, the conductor writes a run record. The outcome goes in the PR description. | G10–G13 |
| D — Gate | `KW-DOC-LIFECYCLE-004`, always on under `--merge-ready`. It checks only the plans and specs each change archives (git diff against the base). It is host-agnostic: no folder or doc-type rules. | G14–G16 |
| E — Kyber-Docs skills | Rename `kyber-weave-docs` and `doc-corpus-integrity-review`. Move and rename `second-brain` into Kyber-Docs, with an owner-started assessment-and-remediation mode. Warn about stale installed copies. | G17–G19 |
| F — Backfill | The three skipped plans get real design-harvest closeouts. | Kept |

Q2 is unchanged: delivery requires the `docs-dev` closeout digest.

## Decisions

Permanent table, preserved intact across every lifecycle transition.

- **G1–G19:** owner decisions from the 2026-10-25 alignment session.
- **D-rows:** architect decisions. Rows overtaken by the alignment are kept and marked
  SUPERSEDED, not deleted.
- **Old Q1 and Q3:** superseded. Q2 stands. Q4 was answered by the owner.
- **Numbering:** the "(Qn)" in a G-row's provenance is the alignment session's own question
  number, not one of this plan's Q-ids.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| G1 | How is design routed to canonical docs? | By document identity: the host's doc type plus its catalog component (frontmatter), never by folder path. | This repository's layout is one host's configuration. | ANSWERED — owner, alignment session 2026-10-25 (Q1) |
| G2 | Who may propose a new component? | The architect or `docs-dev` may recommend one, only with high confidence. A human always approves. When in doubt, use an existing component. | Typical hosts have 5–10 components; large ones about 100. | ANSWERED — owner, alignment session 2026-10-25 (Q2) |
| G3 | May `docs-dev` add ontology vocabulary? | Never. It recommends new doc types and similar vocabulary to the human, even in autonomous mode. | Root `AGENTS.md`: "Do not widen the ontology to make a failure disappear." | ANSWERED — owner, alignment session 2026-10-25 (Q3) |
| G4 | What may portable Kyber-Squad and Kyber-Docs text name? | Only host ontology terms and Config Reg placeholders, never this repository's layout. | Pinned by tests on every portable file this plan edits (D34). | ANSWERED — owner, alignment session 2026-10-25 (Q4) |
| G5 | What happens while a recommendation waits? | Harvest into the best existing home now, list the recommendation in the PR, and complete the closeout. | Closeout never blocks on a pending new-component or new-doc-type recommendation. | ANSWERED — owner, alignment session 2026-10-25 (Q5) |
| G6 | Where does a new doc file go? | Beside the component's existing docs. For a brand-new component, the location is part of the approved recommendation. | | ANSWERED — owner, alignment session 2026-10-25 (Q6) |
| G7 | How is the doc type chosen? | `docs-dev` picks it by judgement from the host's doc-type list and documentation standard. No routing configuration for now. | | ANSWERED — owner, alignment session 2026-10-25 (Q7) |
| G8 | What do plans and specs declare? | A scale: `new-component`, `extends-component` (one or more components), or `no-design-change` with a reason. The owner confirms it at approve-and-execute. | Mechanics in D19 and D21. | ANSWERED — owner, alignment session 2026-10-25 (Q9) |
| G9 | How does the Design harvest list name targets? | As component + doc type, not file paths. `docs-dev` resolves the files at closeout. | Format in D20. | ANSWERED — owner, alignment session 2026-10-25 (Q12) |
| G10 | Which changes get a docs review? | Every change, by `docs-dev`, whether it came from a plan, spec, todo, issue or one-off chat. The conductor and bug-crusher cover every file-changing entry point. A change may end with "no doc change", but only after the review. | Mechanics in D32 and D33. | ANSWERED — owner, alignment session 2026-10-25 (Q13, Q16) |
| G11 | What does `docs-dev` get when there is no plan? | A short run record from the conductor: what was asked, what was decided and why, and which files changed. No transcripts. | It extends the existing execution artifact (D32). | ANSWERED — owner, alignment session 2026-10-25 (Q14) |
| G12 | Where does the review outcome go? | No new register or file. `docs-dev` reviews what it is sent. The outcome goes in the PR description: "docs updated: …" or "no doc change: reason". The PR is critical. | `create-pull-request` already has a "Documentation impact" section (D32). | ANSWERED — owner, alignment session 2026-10-25 (Q15, Q20) |
| G13 | Where do recommendations appear? | In the plan's closeout section, in the `docs-dev` digest, and in the PR. A todo only if the owner accepts one. | | ANSWERED — owner, alignment session 2026-10-25 (Q10) |
| G14 | Is the gate opt-in? | No. It is always on, because it is core to the value proposition. | Replaces old Q1. Runs under `--merge-ready` with no config key (D6). | ANSWERED — owner, alignment session 2026-10-25 (Q11) |
| G15 | What does the gate check? | Only what each change archives, from a git diff against the base. History is never re-checked; there is no baseline list. CI checkout needs enough history. | Scope rule in D26; base and CI in D27 and D35. | ANSWERED — owner, alignment session 2026-10-25 (Q17) |
| G16 | Which checks does the gate run? | Archived status; a declared scale and impact components; every decision and design item maps to an existing governed doc or a written waiver; each impact component is a catalog row, and at least one mapped doc carries it with a fresh `last-reviewed`; a RESIDUAL links a todo. No folder or doc-type rules. Changes without a plan are covered by the review and the PR section, not by CI. | Conditions C0–C9 in "Gate contract". Replaces the earlier C9/C10/C11 and Q3. | ANSWERED — owner, alignment session 2026-10-25 (Q8, Q20) |
| G17 | What happens to `second-brain`? | Rename it `kyber-docs-onboarding-and-assessment` and move it to Kyber-Docs (`.apm/skills`, deployed by `docs init`). Add an owner-started assessment-and-remediation mode: a per-plan/spec report (harvested, partial, or not harvested), the owner picks, and `docs-dev` fixes them in batches as PRs. | Today its Audit mode never inspects archived plans and remediates nothing; `doc-corpus-integrity-review` excludes the archive. Phase 2. | ANSWERED — owner, alignment session 2026-10-25 (Q17, Q18, Q19, Q21) |
| G18 | Which skills are renamed? | Now, in this plan: `kyber-weave-docs` → `kyber-docs-authoring`, and `doc-corpus-integrity-review` → `kyber-docs-integrity-review`. `kyber-weave-cli` and `spec-conductor` keep their names. | Phase 2 (D36). | ANSWERED — owner, alignment session 2026-10-25 (Q22–Q24) |
| G19 | What about old installed copies of renamed skills? | Verify whether APM and `squad update` prune removed skills. If they do not, `docs init` and `squad doctor` warn, and the release notes give the cleanup command. No stubs. **Release-notes channel (Q4):** the rename PR's title carries a short cleanup hint, which the auto-generated Release notes list; the PR body and the install guide carry the full commands; the warnings are the main channel. | Facts in D37, confirmed by observation in T13a. Q4 answered by the owner 2026-10-25. | ANSWERED — owner, alignment session 2026-10-25 (Q25) |
| D1 | Development mode? | `test-first`. | Packet. | ANSWERED — per packet 2026-10-25 |
| D2 | Autonomy for this run? | `supervised`. | Packet; overrides the Config Reg default for this run. | ANSWERED — per packet 2026-10-25 |
| D3 | Root cause? | Closeouts were **skipped**, not done badly. Three real failure shapes: an unchanged move (#425); added while Ready (#426); archived with an admitted RESIDUAL (#423). | Git evidence was relayed by the conductor: #426 merged 2026-10-24T13:10:37Z as `0351a45c`, a squash of `6dd4ce3f`. All three files carry `status: draft` and `**Status: Ready`, which corroborates it. | ANSWERED — conductor-relayed evidence 2026-10-25; file state verified in worktree |
| D4 | New rule id, or extend 003? | New id **`KW-DOC-LIFECYCLE-004`**, class `ArchiveCloseoutValidator`. 003 is unchanged. | Rule ids are permanent. 003 means "still open". No `LIFECYCLE-00[4-9]` exists anywhere. | ANSWERED — architect technical call |
| D5 | Closeout evidence conditions | SUPERSEDED by G16; see C0–C9 and D30/D31. | The earlier C9 (architecture doc), C10 (new-component folder) and C11 (architecture freshness) shapes were dropped by the owner. | SUPERSEDED — owner, alignment session 2026-10-25 (G16) |
| D6 | When does 004 run? | Always under `docs validate --merge-ready`, beside 003, with no opt-in key (G14). Plain `docs validate` does not run it: without a base there is no change to scope (G15). | `--merge-ready` is the merge gate CI already passes. The fast loop and `review gates` keep running plain `docs validate`. | ANSWERED — architect technical call under G14/G15 |
| D7 | Archive status convention | Frontmatter `archived`, `complete` or `superseded` passes. New closeouts write `status: archived` and a first body line `**Status: Archived <date> — …**`. The ontology vocabulary is not widened (G3). | The loader excludes the `archive` segment (`OntologyConfig.cs:43-44`), so archived documents were never validated; that is why the archive mixes `archived`, `complete`, `completed` and `Archived`. | ANSWERED — architect technical call |
| D8 | Where does a spec declare its harvest? | SUPERSEDED by D29. | | SUPERSEDED — architect, 2026-10-25 |
| D9 | How is the archive read? | SUPERSEDED by D26 and D28. | | SUPERSEDED — architect, 2026-10-25 |
| D10 | `docs-dev` description | Closeout as design harvest leads, then the every-change review, then general docs. Working text: "Closes out finished plans and specs by harvesting their design, architecture, features and decisions into the host's canonical docs, and reviews every change for documentation impact. Also writes READMEs, API docs, ADRs, runbooks, and inline docs in Markdown/Mermaid. Use when a plan or spec needs its docs-dev closeout, when a change needs its documentation-impact review, or when the deliverable is a .md file. Do not use when the deliverable is a formal requirements/design/tasks spec to author (product-owner) rather than reference documentation or a closeout." The worker may refine it with the `writing-trigger-descriptions` skill but must keep the T4 pins. | `DescriptionScorer` scores it at least 95 (length about 560 characters, so 10/15 on length). It passes `KW-AGENT-LINT-002`. The exclusion names spec **authoring**. | ANSWERED — architect technical call |
| D11 | Move `docs-dev` off `model-profile: fast`? | **Not pursued.** | Evidence shows skipped closeouts, not weak ones. The owner agrees. | ANSWERED — out of scope; owner position relayed by conductor 2026-10-25 |
| D12 | `docs/dash/architecture.md:359` links a missing todo | Remove the link and state the owner live gate as `[NOT LIVE-VERIFIED]`. Do not recreate the todo. | The owner removed the opencode todos. Todos exist only when the owner accepts them. | ANSWERED — architect technical call |
| D13 | How do the backfills change archived history? | Annotate; never rewrite. **Keep** the Decisions tables and every original section; the original status paragraph stays, labelled "Pre-closeout status (preserved as history)". **Add** the scale frontmatter, a new first status line, a retroactive `## Design harvest`, `## Completion summary` and `## Closeout mapping`. **Correct** false index claims and mark them as corrected. | Precedent: the #192 correction in `docs/archive/plans/2026-10-05-harvest-plan-decision-records.md`. | ANSWERED — architect technical call |
| D14 | Who edits agent and skill markdown, and who re-pins the digests? | `docs-dev` edits the text. `test-dev` re-pins `final-body-sha256` in `products/kyber-squad/migration/{docs-dev,conductor}.md`. | `LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest` requires `final-body-sha256 == BodyDigest` (body only). `docs-dev` has no process tool. `bug-crusher` has no migration record (net-new agent). | ANSWERED — architect technical call |
| D15 | Older partial mappings (`2026-10-09-issue-312`, `2026-10-10-kyberdash-claude-code-turn-content`, `2026-10-09-issue-319`) | Not touched here. The G17 assessment-and-remediation mode is their route. | G15 never re-checks history. | ANSWERED — architect, per packet and G17 |
| D16 | The in-flight `squad-model-presets` spec | Out of scope. At its closeout it must declare its scale and a Design harvest, and pass 004. | | ANSWERED — per packet 2026-10-25 |
| D17 | Committed harness copies? | None. `.github/` holds only `CODEOWNERS`, the PR template and `dependabot.yml`. Renderers project `products/kyber-squad` at install time. The migration records are the only propagated artifacts. | | ANSWERED — finding, verified in worktree |
| D18 | The `product-owner` skill description also claims "closeout" | Out of scope; follow-up. | `closeout-phase.md` redirects to `docs-dev`. | ANSWERED — architect technical call |
| D19 | Scale keys | Frontmatter on the plan, or on the spec's design document: `documentation-impact` (`new-component`, `extends-component` or `no-design-change`) and `impact-components` (catalog component names; required and non-empty for the first two). For `new-component`, the names are checked against the catalog only at archive time, because the closeout adds the approved row. The existing `component` key is unchanged. | The shared deserializer ignores unknown keys (`MarkdownFrontmatterReader.cs:42-46`); `DocumentFrontmatter` gains two properties. A plan's `component` must already be in the catalog (`KW-DOC-SPEC-004`), so it cannot name a new component. | ANSWERED — architect technical call under G8 |
| D20 | Design harvest format | A table: `\| ID \| Design item (section) \| Target component \| Target doc type \|`. Ids are `H1…Hn`. Targets are component + doc type from the host's catalog and ontology (G9). **Plans:** one row per durable design, contract, interface, behaviour or operations section. **Specs:** one row per design section and per requirement group. **`no-design-change`:** a single line, `Documentation waiver: <reason>`. | Every H id is then mapped at closeout exactly like a decision ID. | ANSWERED — architect technical call under G9 |
| D21 | Who checks the declaration, and when? | **Author:** the architect (`plan-authoring.md`) or product-owner (`design-phase.md`) writes it. **Conductor:** the `PLAN_READY` / `SPEC_READY` checks require it, and the approve-and-execute question states the scale for the owner to confirm. **Gate:** 004 enforces it at archive time. There is no draft-time mechanical check. | G8. A draft-time rule would add little beyond 004 plus the checklist. | ANSWERED — architect technical call under G8 |
| D22 | Gate vs review-lens split | **Lens (intent-alignment) judges:** • whether mapped content truly describes the shipped design; • whether the Design harvest list is complete; • whether the scale and component choice are right (G2); • whether the doc type and location are right (G6, G7); • whether waivers are valid; • whether a `no doc change` outcome is justified; • whether the PR documentation section is present and true. **Gate checks:** C0–C9 only. | G16: the gate has no folder or doc-type rules. | ANSWERED — architect technical call under G16 |
| D23 | Backfill scales | All three plans are `extends-component`: #423 and #425 → `[KyberDash]`, #426 → `[KyberSquad]`. Known missing design: KyberSquad's architecture doc does not describe #426's `pi-extensions` / `pi-tools` overlay, so the backfill writes it. | The owner confirms at approve-and-execute. KyberDash design is expected in its architecture doc, "OpenCode plugin span/turn envelope" (lines 286-360), subject to per-item verification. | ANSWERED — architect proposal; owner confirmed at the approve-and-execute gate 2026-10-25 (AP1) |
| D24 | This plan's own scale | `extends-component`, `[DocGraph, KyberSquad, CI Pipelines, ContextHygiene, Distribution]`. See `## Design harvest`. | Distribution has root-level docs only (`install.md`, `distribution.md`), so this closeout exercises the host-agnostic rule. | ANSWERED — architect proposal; owner confirmed at the approve-and-execute gate 2026-10-25 (AP1) |
| D25 | Components with no architecture or onboarding doc | SUPERSEDED by G16: no doc-type rule. A component passes with any mapped fresh doc that carries it. | | SUPERSEDED — owner, alignment session 2026-10-25 (G16) |
| D26 | Exactly which archived entries does a change put in scope? (G15) | Take the paths changed since the **merge base** of `HEAD` and `--base`, plus uncommitted and untracked files. Statuses A/C/M/R/T; deletions are ignored. An archived plan or spec entry is in scope when the change **adds, copies or renames** any of its documents into the archive, **or modifies** it while its harvest document declares `documentation-impact`. Editing a legacy archived plan that has no `documentation-impact` (a typo or link fix) is never checked. | This honours "history is never re-checked". It still gate-verifies the backfills and later G17 remediation PRs, because both add the key. A merge-base diff, rather than a plain diff against the base, keeps changes that landed on main after the branch point out of scope. | ANSWERED — architect technical call under G15 |
| D27 | How is the base resolved? | `docs validate --merge-ready [--base <ref>]`. The default is `origin/HEAD` (the remote's default branch), so no branch name is assumed (G4). If git is missing, the directory is not a repository, the base is unknown, or the merge base cannot be found (shallow clone), 004 fails closed with one error: "cannot determine what this change archives". Its hint is "fetch full history (for example `actions/checkout` `fetch-depth: 0`) or pass `--base <ref>`". | G14: always on, fail-closed. `ChangedFiles` (`src/KyberWeave.Core/Review/ChangedFiles.cs`) already runs git by argv with `ValidateBase`; it gains a name-status, merge-base variant. | ANSWERED — architect technical call under G14/G15 |
| D28 | Where is the archive? | For each inventory, `<docs root containing the index>/archive/<leaf name of the index folder>/`, for the `plan-index` and `specification-index` registry properties. Entries group by the first path segment below it. The existing `EntryBelow` moves to `InventoryReachability` and is shared with 003. | This matches the existing 003 hint and the `docs-dev` archive instruction (a product convention, not host layout). It works with any docs-root name and any number of roots. | ANSWERED — architect technical call |
| D29 | Which document carries a spec's harvest? | Identity, not filename: the document in the archived spec entry that declares `documentation-impact`. The product-owner writes it on the spec's design document. A plan's harvest document is the plan document. | G1. No reliance on `design.md`. | ANSWERED — architect technical call under G1 |
| D30 | What counts as an "existing governed doc"? | A relative link from the mapping that resolves to a document in the live `DocumentSet`: under a configured docs root, with frontmatter, and not archived. Code files, missing files and archived documents do not count. | G16 wording. ADRs and indexes are governed documents. | ANSWERED — architect technical call under G16 |
| D31 | Component and freshness check | For each impact component X: X is a catalog row (`DocumentSet.Components`, the parsed Component column only), **and** at least one document linked from the Closeout mapping has `component: X` and `last-reviewed` ≥ the harvest document's `last-reviewed` (the closeout date). Known limit: a date bump without real content passes, so the lens judges content (D22). | G16. The free-text catalog columns are never parsed. It does not matter which doc type or folder the doc has. | ANSWERED — architect technical call under G16 |
| D32 | Mechanics of the every-change review (G10–G13) | **When:** before delivery of every file-changing run, the conductor and bug-crusher dispatch `docs-dev`. **Packet:** the plan or spec, or a run record (asked, decided and why, files changed) written on the existing execution artifact, plus the changed-file list. **Digest:** a `DOCS_REVIEW:` line, either `docs updated: <docs>` or `no doc change: <reason>`, plus `RECOMMENDATIONS:`. For plan- or spec-backed work the closeout performs the review, and its `CLOSEOUT_COMPLETE` digest carries the same lines. **PR:** the "Documentation impact" section carries the outcome verbatim, with recommendations; it is appended even when the host's PR template has no such section. | G12 bars a new register, so the run record is not committed; it lives on the execution artifact and in the packet. | ANSWERED — architect technical call under G10–G13 |
| D33 | Can bug-crusher reach `docs-dev`? | Add `docs-dev` to `bug-crusher`'s `delegates-to`. Update the Copilot render pin (`CopilotRendererTests.cs:634`). | Today it delegates only to `bug-crusher-investigator`, and harnesses that enforce `delegates-to` would block the review. Its `root-fixer` profile already allows `delegate`. | ANSWERED — architect technical call under G10 |
| D34 | How is G4 enforced? | Text pins assert that every portable file this plan edits contains no literal `docs/` path (regex `(?<![\w<.-])docs/`). Paths use Config Reg placeholders (`<docs-root>`, `<component-catalog>`, `<documentation-ontology>`, `<plan-index>`, `<specification-index>`, `<adr-index>`, `<todo-index>`). | `docs-dev.md` currently says "(`docs/`) and/or ADRs (`docs/adr/`)". | ANSWERED — architect technical call under G4 |
| D35 | CI and the host template | **`ci.yml` `skill-docs-gate`:** `fetch-depth: 0`; pass `--base origin/${{ github.base_ref }}` on `pull_request` and `--base ${{ github.event.before }}` on `push`. **Host template** `templates/github-actions/kyber-weave-docs-gate.yml`: the `docs-validate` job runs `--merge-ready --base origin/${{ github.base_ref }}` with `fetch-depth: 0` on `pull_request`. | G14 (core value, so hosts get it), G15 (history). Today's checkout is default depth (`ci.yml:506-515`; template `:39-40`). | ANSWERED — architect technical call under G14/G15 |
| D36 | One plan or two? | **One plan, two phases.** Phase 1: G1–G16, Q2 and the backfills. Phase 2: G17–G19, after phase 1's text and gate land. One review, one closeout. | **Split rejected:** a second Draft plan in the active folder on this branch would fail Phase 1's PR under 003 until it was archived, or force a branch hand-off. G18 says "in this plan". The always-human review happens once either way. The task graph stays dispatchable (22 tasks, at most 4 ready at once). | ANSWERED — architect technical call; not overridden at the approve-and-execute gate 2026-10-25 (AP1) |
| D37 | G19 facts: do the installers prune removed skills? | **APM: pruning is lockfile-gated, on both versions tested.** Observed v1→rename→v2 on the shared `.agents/skills/` root (`agent-skills` target): with `apm.lock.yaml` present, plain `apm install` pruned the stale `kyber-weave-docs/` directory on **both** APM 0.28.0 (the fixtures' pin; "Cleaned 2 stale files") and 0.35.0 (identical clean, plus a "Historical dependency claims retained" notice). Without a lockfile, **both versions leave the stale directory standing**, and `apm update --yes` is a no-op on local-path dependencies. The earlier "shared-root rename cleanup only from APM 0.34.0" gate is **dropped**: 0.28.0 prunes identically given a lockfile, so neither a 0.34.0 version gate nor "0.28.0 never prunes" is encoded. Untested by T13a (`file://` scratch packages rejected): the git-URL ref-pinned rename path and the edited-deployed-file path; the warnings must not assume pruning there. **`squad update`: yes for unedited files** (unchanged, from source): receipt-owned files it no longer renders are deleted when their digest matches; edited ones are retained (`SquadDeploymentPlan.cs:475-509`). Per G19 the warnings follow the observation, never version claims: `docs init` warns when a retired skill directory is still present under the target's skills root after the deploy step — the standing case T13a actually observed is the no-lockfile install — and `squad doctor` warns about retained or unmanaged retired skills; both print the cleanup command. | Verified T13a, 2026-10-25: observation-only (TASK_GATE `NONE_APPLICABLE`), session `ses_e8bbcd111ffePxP3NY9Bfhmz2D`, isolated installs per version across four scratch consumers; record on the execution artifact, analysis in the scratch observation file §5 (`/tmp/t13a-scratch/T13a-observation.md`, outside the worktree). The observation supersedes the `research-agent` summary (microsoft/apm v0.35.0 source, CHANGELOG [0.12.4], [0.21.0], [0.34.0], [0.35.0]) wherever they disagree: the 0.34.0 gate is contradicted on the tested path. `squad update` behaviour remains from source (high confidence), not re-observed. T14/T15 warning conditions and wording encode this row, not the summarized research (G19). | ANSWERED — APM part verified by T13a observation 2026-10-25 (0.28.0 and 0.35.0); `squad update` part verified from source 2026-10-25; row amended in place at Ready per the T13a protocol |
| D38 | Which skills does CI gate after the rename? | Validate, lint and scan run for `kyber-docs-authoring`, `kyber-docs-integrity-review`, `kyber-docs-onboarding-and-assessment` and `kyber-weave-cli`, in `ci.yml` and in the `review gates` suite. The renamed authoring skill keeps SARIF category `kyber-weave-skills` (per the `ci.yml:554-559` comment); the new skills take path-derived categories. `spec-conductor` gating is out of scope. | The `ci.yml:535-537` comment requires a gate and an upload step for every new skill directory. | ANSWERED — architect technical call under G18 |
| Q1 | (old) Legacy scoping and opt-in for 004 | SUPERSEDED: G14 (always on) and G15 (change-scoped, no baseline) replace the frozen baseline and opt-in key. | Earlier answer (a), frozen baseline (orchestrator technical call 2026-10-25), is withdrawn. | SUPERSEDED — owner, alignment session 2026-10-25 (G14, G15) |
| Q2 | Should the conductor and `docs-dev` refuse an archive move without a closeout digest? | **(a) Yes.** `docs-dev` returns `STATUS: CLOSEOUT_COMPLETE` (`ARTIFACT`, `IMPACT`, `MAPPED: <n>/<n> ids`, `WAIVERS`, `DOCS_REVIEW`, `RECOMMENDATIONS`, `VALIDATION`) or `STATUS: CLOSEOUT_BLOCKED` (`GAPS`). Only the `docs-dev` closeout task archives. Any other worker that meets 003 routes the work to closeout. Delivery needs `CLOSEOUT_COMPLETE` plus a post-closeout `docs validate . --merge-ready`. | It makes "only a successful closeout completes the objective" enforceable, and names the #425 shape. The owner's alignment record keeps this ("conductor requires the docs-dev closeout digest before delivery"). | ANSWERED — orchestrator technical call under the owner's standing rule (make low-risk technical calls and report them); kept by owner alignment session 2026-10-25; not overridden at the approve-and-execute gate 2026-10-25 (AP1) |
| Q3 | (old) Freshness proxy scope | SUPERSEDED by G16 and D31: freshness applies to at least one mapped doc that carries each impact component, of any doc type. | | SUPERSEDED — owner, alignment session 2026-10-25 (G16) |
| Q4 | G19 says the release notes give the retired-skill cleanup command, but Release notes are GitHub's generated list of merged PR titles (`docs/distribution.md` "Release notes"), so no channel exists for it. How should it get there? | **A variant of (a), with no manual paste.** Release pages list merged PR titles automatically, so: (1) the rename PR's **title** carries a short cleanup hint; (2) the PR **body** and the install guide carry the full cleanup commands; (3) the `docs init` and `squad doctor` warnings are the main channel. No release-workflow change, and nothing pasted by the release approver. | **(a) Manual addition at release, with the PR as the source — RECOMMENDED.** The PR title flags the rename; the PR body and the install guide carry the exact cleanup commands; the release approver pastes the upgrade note into that Release's body at the approval step the release job already has. The `docs init` and `squad doctor` warnings are the primary channel either way. Pros: no release-workflow change and no new file. Cons: a human step that can be forgotten. **(b) Build an "upgrade notes" injection into the release workflow.** Pros: automatic. Cons: touches the release path (adds `./scripts/update-loop.sh` to the gates); a per-version notes file needs versioning rules, which adds scope. **(c) Drop the release-notes channel:** warnings plus the install guide only. Pros: smallest. Cons: deviates from G19. The owner's variant keeps (a)'s zero-workflow cost and removes its forgettable manual step: the title is in the generated list by construction. | ANSWERED — owner 2026-10-25 |
| AP1 | Approve and execute this plan? | **Approved.** The owner chose "approve and execute 2026-10-25-docs-dev-harvest-closeout-gaps.md" on 2026-10-25 and confirmed the documentation-impact scales: D23 (all three backfills `extends-component`) and D24 (this plan `extends-component`, five impact components). The plan is set to Ready; execution may start at wave 1 (T1, T4, T13a). | Recorded at the approve-and-execute gate per the plan-path protocol. `docs validate` and `docs drift` are outstanding for this edit: the authoring harness has no process tool and nested agents are disabled — the conductor runs both before execution. | APPROVED — user, via conductor, 2026-10-25 |

## Design harvest

Targets are component + doc type (G9). The closeout (T20) maps every H id and every decision
ID to the resolved docs.

| ID | Design item (section of this plan) | Target component | Target doc type |
|---|---|---|---|
| H1 | The 004 rule: conditions C0–C9, message and hint ("Gate contract") | DocGraph; CI Pipelines | governance; reference |
| H2 | Change-scoped reading: merge-base diff, scope rule, archive location, harvest-document identity (D26, D28, D29) | DocGraph | architecture |
| H3 | `--base`, its default, fail-closed behaviour, CI history, the host template (D27, D35) | DocGraph; CI Pipelines | governance; runbook (and the CLI command-surface reference) |
| H4 | Authoring declaration: scale, impact components, Design harvest by component + doc type, owner confirmation (G8, G9, D19–D21) | KyberSquad | architecture (plus plan and spec index policy) |
| H5 | Routing and recommendations at closeout (G1–G7, G13) | KyberSquad | architecture |
| H6 | Every-change docs review, run record, PR outcome (G10–G12, D32, D33) | KyberSquad | architecture; requirements |
| H7 | Closeout digest and delivery rule (Q2) | KyberSquad | architecture |
| H8 | Gate vs review-lens split (D22) | KyberSquad; DocGraph | architecture; governance |
| H9 | Kyber-Docs skill set: renames, the onboarding-and-assessment skill and its assessment-and-remediation mode (G17, G18, D38) | ContextHygiene; DocGraph; KyberSquad | architecture; onboarding; architecture |
| H10 | Retired-skill cleanup: the observed prune behaviour (T13a, D37); the `docs init` and `squad doctor` warnings as the main channel; full cleanup commands in the install guide; the rename PR title as the release-notes channel (G19, Q4) | Distribution; DocGraph; KyberSquad | runbook; onboarding; onboarding |

## Investigation findings

**Discovery:** `Discovery: codegraph 12, docs 2, direct reads 48` (plus about 75 grep/glob
calls; 1 delegated `research-agent` query on APM).

- CodeGraph calls 1–8 answered from the main checkout; calls 9–12 from this worktree's index.
- Direct reads of indexed code, with reasons:
  - `DocsValidateCommand.cs`, `DocsValidateSettings.cs` and `DocsInitCommand.cs:90-174`: bodies not returned by codegraph.
  - `SquadDeploymentPlan.cs:360-518`: to confirm `CreateUpdate`'s removal loop.
  - Test files, for exact patterns.
  - Confirmation greps.
- Everything else read directly is outside the indexes: markdown under `products/` and `.apm/`, `apm.yml`, the catalog, CI and templates, and archived plans.

1. **Archive and statuses.** The loader excludes `archive` (`OntologyConfig.cs:41-44`;
   `DocumentLoader.cs:65`), so archived documents were never validated. The archive holds
   mixed statuses. Active plans keep frontmatter `draft` through Ready.
2. **Merge-ready today.** `DocsValidateCommand.Execute` (`:27-33`) runs 003 only with
   `MergeReady`. CI runs `docs validate . --merge-ready --format table` (`ci.yml:580-585`, job
   `skill-docs-gate`, default-depth checkout `:514-515`). The host template runs plain
   `docs validate` with default depth (`templates/github-actions/kyber-weave-docs-gate.yml:39-55`).
3. **Git plumbing exists.** `ChangedFiles.Resolve` runs `git diff -z --name-only --diff-filter=ACMRT <base>`
   plus untracked files, by argv with no shell, and `ValidateBase` refuses option-like refs. It
   compares with the base itself, not the merge base, and returns names without statuses. D26
   needs both.
4. **Fixtures.** `DocFixture` defaults to the root `6-Docs` and the default loader, so the
   validator must be handed the changed paths. This keeps condition tests git-free; the scope
   tests use temporary git repositories.
5. **Frontmatter.** Unknown keys are ignored (`MarkdownFrontmatterReader.cs:42-46`).
   `DocumentFrontmatter` lives at `DocumentModel.cs:48`.
6. **Catalog.** Only the Component (index 1) and Owner (index 6) columns are parsed; the other
   columns are free text (`catalog.md` "How the columns are read"). `DocumentSet.Components`
   holds the vocabulary. In this host, Distribution is `Supporting` with root-level docs only,
   and ContextHygiene's docs are `skills.md` and `agents.md` (doc type `architecture`).
7. **Agents.**
   - `bug-crusher` has `delegates-to: [bug-crusher-investigator]` (`agents/bug-crusher.md:12`),
     pinned by `CopilotRendererTests.cs:634`. It is a net-new agent with no migration record.
   - `conductor.md` lines 59 and 71 tie completion to the plan or spec closeout only; editing
     the body re-pins `migration/conductor.md`.
   - `execution-and-review.md:13,63-65,88-95` defines the execution artifact and §Closeout.
   - `create-pull-request` already has "Documentation impact" (`SKILL.md:137`) and fills the host
     template when one exists (`:127-129`). This host's PR template has no docs section, only a
     checkbox.
8. **Kyber-Docs skills.**
   - `apm.yml` (`includes: auto`) ships four skills from `.apm/skills/`; its description names
     `kyber-weave-docs`.
   - `docs init` runs `apm install dpalfery/kyber-weave --target <t>` (`DocsInitCommand.cs:108-153`;
     default target `agent-skills`), so all four deploy together, and a fifth placed there
     deploys too.
   - `kyber-weave-docs` is named in `DocsInitCommand.cs:61,140,167`, `DocsInitSettings.cs:27`,
     `DocsScaffolder.cs:885`, `AGENTS.md:36-38,184`, `.kyber-weave/kyber-weave.yml:67,70,75`,
     `ci.yml:538,561,565`, `scripts/codex-cloud-setup.sh:99-100`, `README.md:97,283`,
     `.apm/skills/kyber-weave-cli/SKILL.md:3,23,137`, `products/kyber-squad/README.md:157-158`,
     `tests/KyberWeave.Tests/{KyberWeaveDocsSkillVocabularyTests.cs:14,SquadCanonicalContentTests.cs:217-218}`,
     and in the docs (`install.md`, `docgraph/onboarding.md`, `context-hygiene/skills.md`,
     `distribution.md`).
   - `ApmManifestGuardTests` names `dpalfery/kyber-weave-docs` as a package-name variant, not
     the skill, so it is unchanged.
   - `second-brain` is named in `products/kyber-squad/{bundles/full.yml:51,README.md:85,130}`,
     `SquadCanonicalContentTests.cs:131`, `SquadPackAndReleaseTests.cs:80`,
     `Fakes/FakeSquadRenderer.cs:60`, and in `docs/kyber-squad/*` and `context-hygiene/skills.md:152`.
     The `docs/plans/README.md` and `docs/todo/README.md` mentions are historical rows and stay.
   - CI gates only `kyber-weave-docs` and `kyber-weave-cli` today (`ci.yml:538`).
   - `second-brain`'s Audit mode never reads archived plans, and its text hard-codes layout
     (`<docs-root>/catalog.md`, `system/architecture.md`), which G4 forbids.
9. **Release notes** are GitHub's generated list of merged PR titles (`docs/distribution.md`
   "Release notes"). So a merged PR's title reaches the notes automatically; this is the
   basis of the Q4 answer.
10. **Backfill targets.**
    - **#423:** decision IDs D1–D7, Q1–Q3, AP1, R1, R2. Design in §3.1–3.4. Index row stale.
    - **#425:** decision IDs D1–D7, AP1. Design in §3.1–3.3. Its index claim "mapped in §8" is
      false. Likely missing: D6 (no Skills-view source for `opencode.context.skills`) and D1.
    - **#426:** decision IDs D1–D11. Design in §1.2 and D7–D10. KyberSquad's architecture doc
      lacks the overlay design. Index row malformed. Archive date 2026-10-24.
11. **Conductor checks (2026-10-25, before this rewrite):** `docs validate .` and
    `docs drift .` returned no findings. Both must be re-run.

## Gate contract (`KW-DOC-LIFECYCLE-004`)

**Rule.** Error. Raised by `docs validate --merge-ready` in every host, with no opt-in (G14).

**Scope (G15, D26–D29).**

1. **Changes.** Resolve the merge base of `HEAD` and `--base` (default `origin/HEAD`). Take the
   paths changed since then (A/C/M/R/T, plus uncommitted and untracked files).
2. **Archive containers.** For the `plan-index` and `specification-index` properties, the
   container is `<docs root of the index>/archive/<leaf of the index folder>/`. An entry is the
   first path segment below a container.
3. **Selection.** An entry is in scope when any of its documents was added, copied or renamed
   by the change, or was modified while its harvest document declares `documentation-impact`.
4. **Harvest document.** For a plan, the plan document. For a spec entry, the document that
   declares `documentation-impact`.
5. **Ignored:** untouched archives and deleted files.

**Conditions.** One finding per entry, naming every failed condition, with a hint.

0. **C0 Scope resolvable.** If git is missing, the directory is not a repository, the base is
   unknown or the merge base is missing, emit one error: "cannot determine what this change
   archives". Hint: "fetch full history (e.g. `actions/checkout` `fetch-depth: 0`) or pass
   `--base <ref>`".
1. **C1 Archive status.** Every document in the entry has frontmatter `status` of `archived`,
   `complete` or `superseded`.
2. **C2 No live body status (plans).** The first `**Status:` line does not continue with
   `Draft`, `Ready`, `In progress`, `Blocked` or `Review required`.
3. **C3 Decisions table.** A `##` heading `Decisions` (optionally numbered) whose section holds
   a table with id rows. Ids are first cells with `**` and backticks stripped, matching
   `^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*$`.
4. **C4 Closeout mapping table.** A `##` heading `Closeout mapping` (optionally numbered) whose
   section holds a table.
5. **C5 Scale declared.** `documentation-impact` is `new-component`, `extends-component` or
   `no-design-change`. For the first two, `impact-components` is non-empty. For
   `no-design-change`, the `## Design harvest` section contains `Documentation waiver:` followed
   by a reason.
6. **C6 Design harvest table.** For `new-component` and `extends-component`: a `##` heading
   `Design harvest` with a table of id rows.
7. **C7 Every id mapped.** Every C3 id and every C6 id is mapped by a row whose first cell
   lists it. Comma-separated ids are allowed; ranges are not expanded. The row must contain
   either a relative link that resolves to a live governed document (D30), or
   `Documentation waiver`. A row containing `RESIDUAL` maps nothing. `ADR waiver` alone maps
   nothing.
8. **C8 Impact components covered.** For each impact component X:
   - X is a catalog row; **and**
   - at least one document linked from the Closeout mapping has `component: X` and
     `last-reviewed` on or after the harvest document's `last-reviewed`.

   There are no doc-type or folder rules (D31).
9. **C9 Residuals tracked.** Every Closeout mapping line containing `RESIDUAL` links an existing
   document inside the `todo-index` folder.

**The real shapes.** #425 (an R100 rename into the archive) and #426 (added while Ready) fail
C1, C2, C4, C5, C6 and C7. #423 also fails C9. Each shape has a RED test, in a temporary git
repository, that isolates its distinguishing condition.

**Stays judgement:** D22.

## Test contract

.NET rows run `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter
<filter>` after a Release build. Scope tests create temporary git repositories; git is a test
prerequisite, as in the existing review-gate tests.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | No-test task (API skeleton) | Release build + full suite | `ArchiveCloseoutValidator(string repoRoot, KyberWeaveConfig config).Validate(DocumentSet live, IReadOnlyList<ChangedPath> changes)` returns an empty report, with `MissingCloseoutEvidence = "KW-DOC-LIFECYCLE-004"`. `ChangedPath(string Path, ChangeKind Kind)`; `ChangedFiles.ResolveSinceMergeBase(...)` is a stub; `DocsValidateSettings.Base` exists; `DocumentFrontmatter.DocumentationImpact` and `.ImpactComponents` exist. Nothing is wired. | n/a. It lets T2 fail on behavior, not compile, so T4, T8 and T14 can share the project. | 0 warnings; existing suite unchanged and green. |
| T2 | New `ArchiveCloseoutValidatorTests.cs`, `ArchiveCloseoutScopeTests.cs` (temporary git repos); additions to `DocGovernanceTests.cs` (frontmatter binding) | `--filter "FullyQualifiedName~ArchiveCloseout"`; `~DocGovernanceTests` | **Real shapes (git):** (1) `AnUnchangedRenameIntoTheArchiveIsKWDOCLIFECYCLE004` (#425); (2) `APlanAddedToTheArchiveWhileReadyFails` (C2, #426); (3) `AnArchivedPlanWithAnUntrackedResidualFails` (C9, #423).<br>**Scope (git):** (4) `AnUntouchedLegacyArchivedPlanIsNeverChecked`; (5) `EditingALegacyArchivedPlanWithoutTheImpactKeyIsNotChecked`; (6) `EditingAnArchivedPlanThatDeclaresImpactIsChecked`; (7) `ADeletedArchivedPlanIsIgnored`; (8) `UncommittedAndUntrackedArchiveMovesAreInScope`; (9) `ChangesThatLandedOnTheBaseAfterTheBranchPointAreOutOfScope` (merge base); (10) `TheBaseDefaultsToTheRemoteDefaultBranch`; (11) `AShallowCloneWithoutTheMergeBaseFailsClosedWithAFetchHint`; (12) `ANonGitDirectoryFailsClosedUnderMergeReady`; (13) `DocsValidateReportsMissingCloseoutEvidenceOnlyWithMergeReady` (theory).<br>**Conditions (injected changes):** (14) draft frontmatter; (15) missing Decisions; (16) missing Closeout mapping (a forecast `## Review and closeout` does not count); (17) missing scale; (18) unknown scale; (19) scale without components; (20) `no-design-change` without a reason; (21) missing Design harvest; (22) unmapped decision id named in the hint; (23) unmapped H id named; (24) `ADR waiver` alone does not map; (25) `Documentation waiver` maps; (26) a link to a code file, a missing file or an archived doc does not map; (27) a comma list maps each id, a range does not; (28) a RESIDUAL row maps nothing; (29) a RESIDUAL linked to an existing todo passes; (30) an impact component not in the catalog fails; (31) no mapped doc carries the component; (32) all mapped docs for the component are stale; (33) one fresh doc among stale ones passes.<br>**Portability:** (34) a docs root named other than `docs` (for example `handbook`); (35) multiple docs roots, with the plan index in the second root; (36) a component whose only doc is a `runbook` or `reference` passes; (37) a Supporting component with root-level docs and no folder passes; (38) `new-component` needs only a catalog row plus one mapped fresh doc carrying it, with no doc type or folder required.<br>**Pass controls:** (39) complete `extends-component` (with `archived` and with `complete`); (40) complete `no-design-change`; (41) a complete spec entry whose harvest document is the one declaring impact.<br>**Specs:** (42) a spec entry is one finding; (43) a spec entry where no document declares impact fails C5.<br>**Binding:** `documentation-impact` and `impact-components` bind. | Every new test fails against T1. The controls (36)–(41) are shown failing first, so they discriminate. Record each test's failing assertion. | Same tests pass unweakened. `OpenWorkValidatorTests` stays green and unedited. |
| T3 | Same as T2 | Same, then the full suite | Per the Gate contract. | T2. | All T2 tests green; full suite green; 0 warnings. |
| T4 | New `SquadDocsHarvestContractTests.cs`; edit `Squad/CopilotRendererTests.cs:634` | `--filter "FullyQualifiedName~SquadDocsHarvestContractTests"`; `~CopilotRendererTests` | Phrase pins (listed in T5–T7), plus: <br>• `docs-dev` description: `DescriptionScorer` ≥ 70; no `KW-AGENT-LINT-001`/`-002`; "closeout" before "README". <br>• `bug-crusher` `delegates-to` contains `docs-dev`; its Copilot render is `agents: ['bug-crusher-investigator', 'docs-dev']` (renderer order). <br>• **G4:** no literal `docs/` in any portable file edited in T5–T7. | Every assertion fails on the unedited tree, except the description score, which passes today as a regression guard (state this). | Same tests pass after T5–T7 with no test edit. |
| T5–T7 | Text only | (T8) | — | T4. | T8 green. |
| T8 | T4's tests + `SquadCanonicalContentTests` | `--filter "FullyQualifiedName~SquadDocsHarvestContractTests"`; `~SquadCanonicalContentTests`; `~CopilotRendererTests` | Pins green. Migration digests re-pinned for `docs-dev` and `conductor`. | The digest test fails after T5/T6; record the actual values. | All green; full suite green. |
| T9, T11 | No-test (canonical docs) | (T19) | Docs describe the shipped design. | n/a | Zero findings in T19. |
| T10 | Real history (no new test) | `dotnet run --project src/KyberWeave.Cli -c Release --no-build -- docs validate . --merge-ready --base 8beb97ae~1 --format json` | The gate catches the real failures. | **Historical RED:** 004 findings include the #425 and #426 plans. Record any other entries reported. | n/a (evidence). |
| T12 | No-test (backfill) | (T19) | The three plans meet C1–C9 (in scope under D26 because they now declare impact). | T10. | T19. |
| T13 | No-test (CI and template) | (T19 + PR CI) | CI passes `--base` with full history. | n/a | The PR's `skill-docs-gate` runs 004. |
| T13a | No-test: observation experiment in a scratch repository (outside the worktree) | Per the T13a task. An isolated APM install per version; no global install. | **Observed** behaviour: whether a renamed skill's old directory remains after re-installing the package, for APM 0.28.0 (the fixtures' pin) and for APM ≥ 0.34.0. Recorded for both `apm install` and `apm update`, with and without `apm.lock.yaml`. | n/a (it is the evidence T14's RED encodes) | The observation record exists with versions, exact commands and outputs. If it contradicts D37, D37 and the warning text are corrected before T14 starts. |
| T14 | `SquadCanonicalContentTests`, `SquadPackAndReleaseTests`, `Fakes/FakeSquadRenderer.cs`, `KyberWeaveDocsSkillVocabularyTests.cs`, new `KyberDocsSkillSetTests.cs`, `DocsInitCommand` and `SquadDoctorCommand` tests | `--filter "FullyQualifiedName~KyberDocsSkillSetTests"` and the named classes | **Renames:** `.apm/skills/{kyber-docs-authoring,kyber-docs-integrity-review,kyber-docs-onboarding-and-assessment}/SKILL.md` exist with matching `name`; the old directories and `products/kyber-squad/skills/second-brain` are gone; the Squad bundle and rosters lack `second-brain`; no Kyber-Docs skill lives in Squad. <br>**Assessment-mode pins:** `owner-started`, `harvested`, `partial`, `not harvested`, `batch`, `pull request`, `docs-dev`. <br>**G4:** no literal `docs/` in the three skills. <br>**`docs init`:** names `kyber-docs-authoring`; warns about a leftover retired skill directory under the target's skills root and prints the cleanup command. The warning's conditions and wording encode **T13a's observed APM behaviour**, not the summarized research. <br>**`squad doctor`:** warns about a retained or unmanaged retired skill, prints its paths and the cleanup steps, and names the new Kyber-Docs home. | All fail on the phase-1 tree. | Same tests pass after T15–T17. |
| T15–T17 | Code, moves, text | (T18) | — | T14. | T18. |
| T18 | Phase-2 tests | as T14, then the full suite | Green. | T14. | Green; `scripts/fast-loop-classes.txt` updated if a test class was renamed. |
| T19 | Real corpus | `docs validate . --merge-ready --base origin/main` + `docs drift .` | Gate green for this change. | — | Zero 004 findings. The only finding is 003 for this active plan. Drift zero. Full suite green. |
| T20 | No-test (closeout, dogfood) | (T21) | This plan meets C1–C9 once archived (an added entry). | n/a | T21. |
| T21 | Real corpus + gates | `docs validate . --merge-ready --base origin/main` + `docs drift .` | Clean after closeout. | n/a | Zero findings of any rule. |

Changing an approved Test-contract row is a scope change: back to Draft and reapproval.

## Tasks

Required skills are named, not agents. Every developer task ends with the worker's
`task-gate`. Skill names in this section are the names current when each task starts;
phase 2 renames the authoring skill to `kyber-docs-authoring`.

### Phase 1 — contract, review, gate, backfill

**T1 — Gate API skeleton.**
- **Files:**
  - New `src/KyberWeave.Core/Docs/Validation/ArchiveCloseoutValidator.cs`; `<remarks>` give the why (D3, D4).
  - `src/KyberWeave.Core/Review/ChangedFiles.cs`: a `ResolveSinceMergeBase` stub plus the `ChangedPath`/`ChangeKind` types.
  - `src/KyberWeave.Core/Docs/Model/DocumentModel.cs`: two frontmatter properties.
  - `src/KyberWeave.Cli/Commands/Docs/DocsValidateSettings.cs`: a `--base <REF>` option.
- **Acceptance:** per the T1 row.
- **Deps:** none.
- **Skills:** `csharp-dev`.

**T2 — RED: gate, scope and portability tests.**
- **Files:** per the T2 row. Condition tests use `DocFixture` with injected changes. Scope tests build temporary repositories with a bare "origin", set `origin/HEAD`, and create a shallow clone for (11).
- **Deps:** T1.
- **Skills:** `test-dev`.

**T3 — GREEN: gate.**
- **Files:**
  - `ArchiveCloseoutValidator.cs`: C1–C9.
  - `ChangedFiles.cs`: the merge-base name-status diff plus untracked files; argv only; `ValidateBase`.
  - `InventoryReachability.cs`: receives `EntryBelow`; `OpenWorkValidator.cs` now calls it there.
  - `DocsValidateCommand.cs`: under `MergeReady`, resolve the base (`--base` or `origin/HEAD`), compute the changes (or C0), and run the validator over the live `set`.
  - `DocsValidateSettings.cs`: the description names 003 and 004.
  - `DocumentLoader.cs`: only a narrowly scoped internal entry point, and only if needed to parse archived files.
- **Acceptance:** per the T3 row; no new dependency.
- **Deps:** T2.
- **Skills:** `csharp-dev`.

**T4 — RED: text pins.**
- **Files:** per the T4 row, following the `SquadSpecGateCollapseTests` `Read`/`AssertAll` pattern.
- **Deps:** none.
- **Skills:** `test-dev`.

**T5 — GREEN: the `docs-dev` side.**
- `products/kyber-squad/agents/docs-dev.md`:
  - **Description** per D10. Pins: `Use when`, `Do not use when`, `design`, `decision`, `documentation-impact review`, `README`, `ADR`, `runbook`, `product-owner`.
  - **Scope:** the first `You own:` bullet contains `closeout` and `design harvest`.
  - **New "Documentation-impact review" section:**
    - `DOCS_REVIEW: docs updated:`, `DOCS_REVIEW: no doc change:`, `RECOMMENDATIONS:`;
    - "reviews what it is sent", "run record";
    - routing by `component` and `doc type` from `<component-catalog>` and `<documentation-ontology>` (G1, G7);
    - "beside the component's existing docs" (G6);
    - "high confidence" and "human approves" for new components (G2);
    - "never adds ontology vocabulary" (G3);
    - "best existing home" (G5);
    - recommendations in the closeout section, the digest and the PR, and `a todo only if the owner accepts` (G13).
  - **Plan and Specification closeout:**
    - keep `Plan closeout`, `mapping table`, `ADR waiver`, `Documentation waiver`, `do not archive`;
    - add `## Design harvest`, `## Closeout mapping`, `documentation-impact`, `impact-components`, `last-reviewed`, `status: archived` and `**Status: Archived`;
    - specs read from the document that declares the scale.
  - **Digests (Q2):** `STATUS: CLOSEOUT_COMPLETE`, `STATUS: CLOSEOUT_BLOCKED`.
  - Replace the literal `docs/` paths with placeholders (D34).
- `products/kyber-squad/skills/product-owner/references/closeout-phase.md`: the same closeout contract; the digest gains `MAPPED:`, `IMPACT:` and `DOCS_REVIEW:`.
- `products/kyber-squad/skills/product-owner/references/tasks-phase.md` (:40): the closeout task names the design harvest and the scale.
- `products/kyber-squad/skills/code-review/references/lenses/intent-alignment.md` (:49-54):
  - "archived plan or specification" plus the D22 judgement items;
  - a `critical` finding for a missing or false PR documentation section, or an unjustified `no doc change`.
- **Deps:** T4.
- **Skills:** `kyber-weave-docs`, `writing-trigger-descriptions`.

**T6 — GREEN: the orchestration side.**
- `products/kyber-squad/agents/conductor.md` (:59, :71): every completed change receives a `docs-dev` documentation-impact review; plan- and spec-backed changes get the closeout.
- `products/kyber-squad/agents/conductor/references/execution-and-review.md`:
  - a "Documentation-impact review" step: `run record` (asked, decided and why, files changed; `no transcripts`) on the execution artifact; dispatch `docs-dev`; require `DOCS_REVIEW:`;
  - §Closeout gains the Q2 rules and `CLOSEOUT_COMPLETE`;
  - "the pull request description" carries the outcome.
- `products/kyber-squad/agents/conductor/references/autonomy.md` (:44): delivery requires `DOCS_REVIEW:` (or `CLOSEOUT_COMPLETE`), and the PR carries it.
- `products/kyber-squad/skills/bug-crusher/SKILL.md` (§3 step 4, §5 step 5, §6): after verification and before review or delivery, a run record plus a `docs-dev` documentation-impact review; the outcome goes in the report and the PR.
- `products/kyber-squad/agents/bug-crusher.md`: `delegates-to: [bug-crusher-investigator, docs-dev]` (D33).
- `products/kyber-squad/skills/create-pull-request/SKILL.md` (:125-141, :178-185):
  - "Documentation impact" = the `docs-dev` outcome verbatim (`docs updated:` / `no doc change:`) plus recommendations;
  - add it under its own heading when the host template lacks one;
  - "After merge" now says the closeout comes **before** delivery.
- **Deps:** T4.
- **Skills:** `kyber-weave-docs`.

**T7 — GREEN: the authoring side and policy.**
- `products/kyber-squad/agents/architect/references/plan-authoring.md`:
  - frontmatter `documentation-impact` (the three values) and `impact-components`;
  - a `## Design harvest` table with `Target component` and `Target doc type`;
  - "the owner confirms the scale at approve-and-execute"; "high confidence" for proposing a new component;
  - keep the existing Decisions pins.
- `products/kyber-squad/skills/product-owner/references/design-phase.md`: a permanent `## Decisions` table (the five columns, "preserved intact"), the scale frontmatter, and a `## Design harvest` that includes a `requirement group` row type.
- `products/kyber-squad/agents/conductor/references/plan-path.md` (:18) and `spec-path.md` (:18, :21): `PLAN_READY` / `SPEC_READY` verify the scale and the Design harvest; the approve-and-execute question states the scale and the components.
- `docs/plans/README.md` and `docs/specs/README.md`: policy sections only ("Decisions, Design Harvest and Closeout Policy").
- `.github/PULL_REQUEST_TEMPLATE.md`: a `## Documentation impact` section (this host).
- **Deps:** T4.
- **Skills:** `kyber-weave-docs`.

**T8 — GREEN verification; digests.**
- **Files:** `products/kyber-squad/migration/{docs-dev,conductor}.md` (`final-body-sha256` only).
- **Deps:** T5, T6, T7.
- **Skills:** `test-dev`.

**T9 — DocGraph, CI Pipelines and CLI-reference docs (H1–H3, H8).**
- `docs/docgraph/governance.md`:
  - the 004 row, C0–C9, scope, fail-closed behaviour, and the gate-vs-lens split;
  - the 003 "not semantic proof" sentence now points at 004;
  - `code-refs` gains `ArchiveCloseoutValidator`, `OpenWorkValidator` and `ChangedFiles`;
  - bump `last-reviewed`.
- `docs/docgraph/architecture.md`: change-scoped reading.
- `docs/ci-pipelines/rule-reference.md`: the 004 row.
- `docs/ci-pipelines/workflows-runbook.md`: the template's history and `--base` requirement.
- `docs/cli-command-surface.md`: `--base`.
- **Deps:** T3, T13.
- **Skills:** `kyber-weave-docs`.

**T10 — Historical RED on the real corpus.**
- Run the T10 command and record the output verbatim.
- **Deps:** T3.
- **Skills:** `test-dev`.

**T11 — KyberSquad docs (H4–H7).**
- `docs/kyber-squad/architecture.md`: a new section, "Plan and specification lifecycle: documentation impact, design harvest, every-change review, closeout".
- `docs/kyber-squad/requirements.md:33`: KS-008 restated.
- **Deps:** T5, T6, T7.
- **Skills:** `kyber-weave-docs`.

**T12 — Backfill the three plans (D13, D23).**
- **Steps:**
  1. Add the scale frontmatter, `status: archived` and `last-reviewed` to each plan.
  2. Insert the new first status line and label the history.
  3. Add a retroactive `## Design harvest` (component + doc type).
  4. Map every H id and decision ID (13 + 8 + 11 decisions) to the resolved canonical doc. Write missing design rather than waiving it: the KyberSquad overlay, #425 D6 and #425 D1. Process and approval rows may take a reasoned `Documentation waiver`.
  5. Bump `last-reviewed` on at least one mapped KyberDash doc and one KyberSquad doc after re-verifying them.
  6. Fix the link at `docs/dash/architecture.md:359` (D12).
  7. Add `## Completion summary` and `## Closeout mapping` to each plan.
  8. Reconcile the three index rows (#426 Archive Date `2026-10-24`, merge `0351a45c`).
  9. Leave #426's deployment gap and the owner live gates as open owner items, not `RESIDUAL`.
- **Files:**
  - the three archived plans;
  - `docs/dash/{architecture,telemetry-inventory,runbook}.md`;
  - `docs/kyber-squad/{architecture,requirements,onboarding}.md`;
  - `docs/adr/0019-pi-native-subagents-and-primary-lowering.md`;
  - `docs/plans/README.md` (three rows).
- **Deps:** T7 (README policy), T11 (shared `docs/kyber-squad/*`), T10 (historical RED first).
- **Skills:** `kyber-weave-docs`, `app-docs-standard`.

**T13 — CI and host template (D35).**
- `.github/workflows/ci.yml` (`skill-docs-gate`, `:514-515`, `:580-585`): `fetch-depth: 0`; `--base` per event; the comment names 003 and 004.
- `templates/github-actions/kyber-weave-docs-gate.yml`: on pull requests, `--merge-ready --base origin/${{ github.base_ref }}` with `fetch-depth: 0`.
- `templates/github-actions/README.md`: the matching row.
- **Deps:** T3.
- **Skills:** `github-devops`.

### Phase 2 — Kyber-Docs skills (G17–G19)

**T13a — Observe APM's pruning of renamed skills (D37 verification).**
- **Objective:** Replace D37's medium-confidence APM facts with an observation, before any warning test is written.
- **Scratch packages:** a git-backed package pair outside the worktree, in the session scratchpad:
  - **v1** ships `.apm/skills/kyber-weave-docs/SKILL.md`;
  - **v2** ships the same skill as `.apm/skills/kyber-docs-authoring/` instead;
  - both use `apm.yml` with `targets: [agent-skills]` and `includes: auto`.
  - Alternative: the real package at an old ref versus a pushed ref of this branch after T16.
  - Record which source was used.
- **Consumer:** a scratch consumer repository.
- **Runs, for each APM version:**
  1. Install v1 (`apm install <pkg> --target agent-skills`).
  2. Move to v2: plain `apm install` and `apm update`, with `apm.lock.yaml` present.
  3. Repeat without a lockfile.
  4. List `.agents/skills/` after each step.
- **Versions:**
  - APM **0.28.0**, the version `tests/KyberWeave.Tests/SquadCliCommandTests.cs:361` pins;
  - APM **0.34.0 or later** (latest is 0.35.0).
- **Isolation:** run each version in an isolated tool environment (for example `uv tool run` or a throwaway virtualenv). No global or system-wide install. Network access is needed.
- **Record:** on the execution artifact, write the versions, exact commands, exit codes, and directory listings, plus whether the old `kyber-weave-docs/` directory remained.
- **If the observation contradicts D37:** before T14 starts, the conductor has the architect amend D37 (provenance "verified T13a") and the warning conditions and text that T14/T15 specify.
- **Deps:** none (it can start in wave 1).
- **Skills:** `github-devops` (toolchain and package-manager diagnostics, with a process tool).

**T14 — RED: skill set, assessment mode, warnings.**
- **Files:** per the T14 row. The `docs init` leftover-warning tests encode T13a's observed behaviour.
- **Deps:** T8 (phase-1 text pins green first, so the shared test project is stable), T13a.
- **Skills:** `test-dev`.

**T15 — GREEN: CLI strings and warnings.**
- `DocsInitCommand.cs` (:61, :140, :167), `DocsInitSettings.cs:27` and `DocsScaffolder.cs:885`: name `kyber-docs-authoring`.
- `DocsInitCommand.cs`: after the deploy step, check the target's skills root (from the APM targets matrix; `agent-skills` → `.agents/skills/`, `claude` → `.claude/skills/`) for the retired `kyber-weave-docs` and `doc-corpus-integrity-review` directories, then warn with the exact cleanup command. The conditions and wording follow T13a's observation.
- New `SquadRetiredSkills` (`second-brain`): `SquadDoctorCommand` warns about retained receipt entries or unmanaged directories, gives the delete-then-`squad update` steps, and points to `docs init` for the new home.
- **Deps:** T14.
- **Skills:** `csharp-dev`.

**T16 — Moves and wiring.**
- `git mv`:
  - `.apm/skills/kyber-weave-docs` → `.apm/skills/kyber-docs-authoring`;
  - `.apm/skills/doc-corpus-integrity-review` → `.apm/skills/kyber-docs-integrity-review`;
  - `products/kyber-squad/skills/second-brain` → `.apm/skills/kyber-docs-onboarding-and-assessment` (`SKILL.md` + `references/templates.md`).
- `products/kyber-squad/bundles/full.yml`: remove `second-brain`.
- `ci.yml` `skill-docs-gate`:
  - `skills_dirs` lists all four gated skills (D38);
  - the authoring upload keeps category `kyber-weave-skills` with the new `sarif_file`;
  - new upload steps for the two other Kyber-Docs skills.
- `.kyber-weave/kyber-weave.yml`: the gate paths, plus validate/lint/scan gates for the two new directories.
- `AGENTS.md`: lines 36-38 and 184.
- `scripts/codex-cloud-setup.sh:99-100`.
- `apm.yml`: description.
- **Deps:** T14, T13 (shared `ci.yml`).
- **Skills:** `github-devops`.

**T17 — GREEN: skill content.**
- `name` fields in the three moved skills.
- `kyber-docs-onboarding-and-assessment/SKILL.md`:
  - description (routing: bootstrap, audit, and the assessment and remediation of archived plans and specs);
  - G4 placeholders throughout;
  - the new **Assessment and remediation** mode (owner-started, per G17):
    1. read archived plans and specs;
    2. classify each as `harvested`, `partial` or `not harvested`, using the 004 conditions as the rubric — the mechanical half may come from `kyber-weave docs validate . --merge-ready --base <first commit>`;
    3. report, and the owner picks;
    4. `docs-dev` fixes the picks in batches, each as its own `pull request`.
- `kyber-docs-integrity-review/SKILL.md`: name only.
- `.apm/skills/kyber-weave-cli/SKILL.md` (:3, :23, :137): cross-references.
- `products/kyber-squad/README.md` (:85, :130, :157-158).
- **Deps:** T16.
- **Skills:** `kyber-weave-docs`, `writing-trigger-descriptions`.

**T18 — GREEN verification, phase 2.**
- **Deps:** T15, T17.
- **Skills:** `test-dev`.

**T19 — Phase-2 canonical docs, then real-corpus GREEN for the whole change.**
- **Docs (`docs-dev` part):**
  - `README.md` (:97, :283);
  - `docs/install.md` (:417, :434, plus an "Upgrading: retired skills" note with the full cleanup commands, matching the behaviour observed in T13a);
  - `docs/docgraph/onboarding.md` (:61, :109);
  - `docs/context-hygiene/skills.md` (:133, :152);
  - `docs/distribution.md:483`;
  - `docs/kyber-squad/{README.md:54, architecture.md:704, requirements.md:127, skill-resource-dispositions.md:23,45,127}`.
- **Verification (`test-dev` part):** per the T19 row.
- Split this task in two at dispatch: docs first, verification second.
- **Deps:** T9, T12 (shared `docs/kyber-squad/*`), T16, T17, T18.
- **Skills:** `kyber-weave-docs` (then `test-dev`).

### Review and closeout

**Review.** One `code-reviewer` run over the whole change. It is `NEEDS_HUMAN` by design
(`KW-REVIEW-008`), because the change touches `products/kyber-squad/agents/**` and
`.kyber-weave/kyber-weave.yml`. That verdict is terminal in supervised mode until the owner
reviews.

**T20 — `docs-dev` closeout of this plan (dogfood).**
- Map H1–H10 and every decision ID.
- Re-verify and bump `last-reviewed` on one mapped doc per impact component: DocGraph, KyberSquad, CI Pipelines, ContextHygiene, Distribution.
- Set the archive status and the first body line.
- Archive the plan, write its row, and return `CLOSEOUT_COMPLETE`.
- Apply the Q4 answer in the closeout digest's `RECOMMENDATIONS` for delivery:
  - the PR **title** carries a short cleanup hint, for example "Rename Kyber-Docs skills — delete old `kyber-weave-docs` skill dirs";
  - the PR **body** carries the full cleanup commands from the install guide.

  No release-workflow change, and nothing pasted at release.
- **Deps:** the review is resolved.
- **Skills:** `kyber-docs-authoring`.

**T21 — Post-closeout gates.**
- **Deps:** T20.
- **Skills:** `test-dev`.

## Dependency graph

```text
Phase 1
  T1 ─► T2 ─► T3 ─┬─► T10 ─────────────────┐
                  └─► T13 ─► T9            │
  T4 ─┬─► T5 ─┐                            │
      ├─► T6 ─┼─► T8                       │
      └─► T7 ─┴─► T11 ─► T12 ◄─────────────┘
Phase 2
  T13a ─┐
  T8 ───┴─► T14 ─┬─► T15 ───────────┐
  T13 ───────────┴─► T16 ─► T17 ─► T18 ─► T19 ◄── T9, T12
                                            └─► Review ─► T20 ─► T21
```

- **T13a** has no dependencies and runs in wave 1. T14 cannot start until T13a has recorded its
  observation.
- **Waves:**
  1. T1, T4, T13a
  2. T2, T5, T6, T7
  3. T3, T8, T11
  4. T10, T13, T14
  5. T9, T12, T15, T16
  6. T17
  7. T18
  8. T19

  At most four tasks are ready at once (`review.max-concurrent-workers: 4`).
- **Shared files are serialized:**
  - `docs/plans/README.md`: T7 → T12 → T20.
  - `docs/kyber-squad/*`: T11 → T12 → T19.
  - `ci.yml`: T13 → T16.

## Risks

| Risk | Mitigation |
|---|---|
| Hosts that run `--merge-ready` with a shallow checkout fail C0 after upgrading. | This is intended (G14, G15). The hint names the fix; the host template and install docs show `fetch-depth: 0`; the PR body states it. |
| Always-human paths force `NEEDS_HUMAN`. | Expected; the owner reviews before T20. |
| The rename breaks consumers' references to the old skill names. | The `docs init` and `squad doctor` warnings are the main channel. The install guide has the full commands. The rename PR's title carries a short cleanup hint, so it appears in the auto-generated Release notes (Q4). There are no stubs (G19). |
| The PR title loses the cleanup hint (squash-merge retitling, truncation). | Keep the hint short and at the end of the title. `create-pull-request` reads the PR back after creating it and confirms the title (`SKILL.md:147-148`). The warnings remain the main channel either way. |
| D37's APM facts are wrong for the pinned 0.28.0, or for current APM. | T13a observes both versions before any warning test is written. T14 RED encodes the observation, and D37 is corrected if it disagrees. |
| The rename orphans SARIF alerts. | The authoring skill keeps category `kyber-weave-skills` (D38). |
| Moving `second-brain` out of Squad leaves Squad-only hosts without it. | `squad doctor` names the new home (`docs init`). |
| Freshness can be gamed with a date bump. | The lens judges content (D22, D31). |
| Gate false positives from heading variants. | One canonical heading each, pinned by tests. Hints name the fix. |
| Recommendations stall a closeout. | G5: harvest into the best existing home and recommend in the PR. |
| Full suite RED between T5/T6 and T8 (migration digests). | Sequenced. |
| `docs drift` needs new symbols indexed. | Refresh the worktree's CodeGraph index after T3 and T15. |
| Backfill uncovers more missing design. | Bounded to three plans; write it, never waive it. |
| A large single change. | Two phases, serialized shared files, one review. Well under `max-reviewable-lines`. |

## Out of scope

- `docs-dev` `model-profile` (D11).
- Legacy partial mappings, which go through the G17 mode later (D15).
- The `squad-model-presets` spec (D16).
- The `product-owner` skill description overlap (D18).
- CI gating for `spec-conductor`.
- #426's deployment gap.
- A draft-time mechanical check of the scale keys (D21).
- Renaming the `kyber-weave-docs-gate.yml` template file (G18 covers skills only).
- `dash/` fixtures that contain the string `kyber-weave-docs`.
- Any release-workflow change (Q4). `./scripts/update-loop.sh` is not required: the release path and self-updater are untouched.

## Verification gates

Run before review (T19) and after closeout (T21), per the root `AGENTS.md`, with the
authoring skill path renamed after T16:

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-docs-authoring
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill lint .apm/skills/kyber-docs-authoring --min-desc-score 70
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill scan .apm/skills/kyber-docs-authoring --fail-on critical
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready --base origin/main
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json
```

- `review gates` covers the four gated skills (D38), InspectCode, duplicates and the TS gates.
- Before T20, the expected merge-ready result is one finding: 003 for this plan. After T20,
  zero.

## Review and closeout

1. One `code-reviewer` run (expected `NEEDS_HUMAN`).
2. T20: the dogfood closeout of H1–H10 and every decision, under the rules this plan creates.
3. T21: the post-closeout gate run.
4. Delivery: the PR title carries the short cleanup hint, and the PR body carries the
   documentation-impact outcome and the full cleanup commands (Q4).

The permanent `## Decisions` table is preserved through archive.
