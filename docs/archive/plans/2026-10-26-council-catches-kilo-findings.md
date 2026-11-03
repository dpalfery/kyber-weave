---
id: plans/2026-10-26-council-catches-kilo-findings
title: "Council catches what Kilo catches: REVIEW.md refresh from PRs #407-#439, TypeScript/Rust/Tauri/Go review checklists, and guidance the council provably applies"
doc-type: plan
status: archived
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-10-27
development-mode: test-first
documentation-impact: extends-component
impact-components: [ReviewCouncil, KyberSquad]
---

# Council catches what Kilo catches

**Status: Archived — complete 2026-10-27.** Closeout mapping and the documentation-impact review are in the Closeout section at the end. The approve-and-execute gate is recorded as D11 (`autonomous: recommendation accepted`, 2026-10-26). Work landed on a feature branch, never `main` (D13).

**Development mode:** `test-first` (the packet default; no user opt-out). Prompt and documentation edits are proved by one RED-first contract test (T2). REVIEW.md rule content has no automated test; T1's evidence ledger verifies it (see §6). **Autonomy:** `autonomous` (repository Config Reg `<autonomy>`).

**Scale:** `extends-component`, components `ReviewCouncil` and `KyberSquad`.

> Discovery (authoring pass 1): codegraph 3, docs 2 (plus 1 `agent_coding_standards`), direct reads 22, greps 14.
> - CodeGraph returned `SquadCanonicalContentTests` (migration digest pin, discovery-contract pin, code-review self-review pin), `SquadAgent`/`BodyDigest` neighbours, `ReviewVerdictCommand`, and `SkillParser`.
> - Direct reads were for things the indexes do not return verbatim, or for one detail each:
>   - prompt and Markdown sources: `AGENTS.md`, `REVIEW.md`, `.kyber-weave/kyber-weave.yml`, `code-review/SKILL.md`, `references/react.md`, `lenses/correctness.md`, the agents `code-reviewer.md`, `review-lens.md` and `review-triage.md`, `migration/code-reviewer.md`, the architect references, `docs/plans/README.md`, and one archived plan (frontmatter shape);
>   - documentation lines the docs tool truncated: `docs/kyber-squad/requirements.md` lines 110-144 and the head of `docs/kyber-squad/skill-resource-dispositions.md`;
>   - single-detail confirmations: `src/KyberWeave.Core/Review/ReviewJson.cs` lines 55-104 (whether the findings reader tolerates unknown members) and `tests/KyberWeave.Tests/SkillResourceDispositionAuditTests.cs` lines 520-590 (the disposition derivation);
>   - evidence: `.agent_scratchpad/kilo-review-mining/summary.md`.
> - `prs.json` was only sampled (the commented-file path list). Verifying it in full is T1.
>
> Validation note: the planner harness has no process tool, so `docs validate .` and `docs drift .` were not run by the planner on this Draft or on the Ready transition. The conductor runs both (plain, not `--merge-ready`; D12).
>
> Discovery (finalization pass): codegraph 0, docs 1, direct reads 4 (this plan, the plan index, `plan-authoring.md`, and `DocumentLoader.cs` lines 318-329), greps 6.
>
> Correction (2026-10-26): reading `DocumentLoader.cs` did not show that a `ready` frontmatter status is accepted; that file is not the check. `docs validate` checks frontmatter `status` against the closed vocabulary in `OntologyConfig.DefaultStatuses`: `current`, `draft`, `needs-review` and `superseded`. This repository does not override it. A `ready` status fails `KW-DOC-SPEC-002`. The approved plan, still being worked, now uses frontmatter `status: current`, as live plans do (see archived #419). Its lifecycle state stays in the body line (`**Status:** Ready`), which is what `ArchiveCloseoutValidator` reads (`LiveBodyStatuses`).

---

## 1. Problem and goal

### 1.1 Problem

Across the last 30 merged PRs (#407-#439), Kilo left 125 inline comments on 16 PRs: 4.17 per PR on average, and 22 on #432. All of them were resolved, which makes them real defects. 18 of the 30 PR bodies cite a council verdict, so the council reviewed those changes first and missed what Kilo then found. By file type the comments were `.ts` 48, `.cs` 39, `.md` 19, `.sh` 8, `.py` 5, `.yml` 4 and `.mjs` 2. Keyword clusters (a comment can match more than one):

| Cluster | Count |
|---|---|
| null or malformed input crashes | 29 |
| boundary, empty, off-by-one (including CRLF) | 28 |
| weak test coverage | 24 |
| non-finite or wrongly typed numbers | about 21 |
| swallowed errors | 16 |
| doc, spec or drift mismatch | 11 |
| security or untrusted input, including YAML injection | 8 |
| config, YAML or schema validity | 8 |
| races or idempotency | 7 |
| unhandled JSON or file I/O | 4 |
| unbounded reads | 3 |
| split-brain logic across modules | 2 |
| cross-platform CRLF and paths | 1 (high impact) |

Investigation found five structural gaps that explain part of this:

1. **The guidance is never loaded on most paths.** Only `code-reviewer.md` (line 62) says to read **<review-guidance>** and paste sections into lens prompts. The following never mention it: the `code-review` skill (which is also the harness-neutral pre-push self-review gate), the refutation pass (step 3), the in-process council fallback for harnesses without delegation (Devin), and the pre-push defect-class checklist. So a self-review, or a council run without delegation, reviews with no guidance at all.
2. **The REVIEW.md lens map does not match the lens catalogue.** Its rows name `react / UI`, `csharp / renderers`, `ci / scripts` and `docs / agent-instruction`, but no lens has those names. Real lenses (`supportability`, `prior-art`, `di-composition`, `model-placement`, `blast-radius-revertibility`, `infra-workflow`, and the three triage lenses) fall into an "every other lens" catch-all. Sections 7, 8 and 9 are keyed by technology, yet nothing routes them by the technology present in the diff.
3. **Nothing shows whether guidance was applied.** The lens output schema has no field for the guidance it applied, `NO FINDINGS` does not name the guidance it checked, and adjudication cannot tell a guided seat from an unguided one.
4. **Some technologies have no checklist.** TypeScript for Node (48 of 125 comments), Rust, Tauri and Go have no review checklist, even though `typescript-dev`, `rust-dev`, `tauri-dev` and `go-dev` exist. Only C#, Python, React, SQL, Pulumi, Azure and GitHub Actions do.
5. **REVIEW.md predates the newest defect classes.** These are not yet covered: wrong-typed fields in untrusted payloads (a numeric `text`, `'28' > 0` compared as strings), one sanitizer bypassed by a sibling path, CRLF frontmatter slicing, raw values written into generated YAML, unchecked GraphQL and JSON response shapes in scripts, and capped serialization of host payloads.

### 1.2 Goal

Most of the defect classes Kilo raised in #407-#439 are caught by the council before the PR exists:

- REVIEW.md keeps the distilled findings from about 100 earlier PRs, de-duplicated, and adds the verified #407-#439 rules, each backed by distinct PR examples.
- Every seat (judgement, triage, refuter, and in-process) receives the guidance its lens and the diff's technologies call for.
- Every `review-lens` seat states which guidance sections it applied.
- TypeScript (Node), Rust, Tauri and Go checklists exist alongside the seven existing ones.
- A contract test keeps the lens map, the checklist roster and the guidance wiring in step.

### 1.3 Out of scope

- The repository-root `.github/agents/` and `.github/skills/`, which are a deliberately stale self-deployment (requirements.md §"Render and knowledge-retention requirement").
- Declaring Rust, Tauri or Go standards for this repository, meaning edits to `.kyber-weave/kyber-weave.yml` `technologies` or new `docs/standards/rust|tauri|go/` (D2).
- Changes to the verdict engine or the findings JSON schema in C# (D4).
- Changes to `dash/`, the tray, or any application code.
- Kilo's own configuration.
- The `dp-code-reviewer` re-review loop.

---

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Where do the Kilo-derived rules live? | **REVIEW.md** (host guidance) holds the repository-specific, evidence-backed rules. Portable, host-neutral technique that the lenses lack goes into `lenses/correctness.md` and `lenses/test-adequacy.md`: non-finite numbers, wrong-typed fields from untrusted input, line-ending-aware text slicing, one sanitizer used by every sibling path, and a malformed-input test for each new guard. Technology checklists stay pointer-only. | `code-review/SKILL.md` says each checklist "points at the host's declared `<technology>-coding-standard` and states no rule of its own". The skill-resource dispositions audit (#126) pins checklists as `pointer-already-#126`, so a rule written into a checklist cannot be reversed by a host. Alternative rejected: putting rules in the checklists breaks #126 and the audit test. | autonomous: recommendation accepted (covered by `code-review/SKILL.md` and `docs/kyber-squad/skill-resource-dispositions.md`) |
| D2 | Rust, Tauri and Go have no `docs/standards/*` in this repository. What do the new checklists point at? | They point at `<rust-coding-standard>`, `<tauri-coding-standard>` and `<go-coding-standard>`. Templates for all three exist in `products/kyber-squad/standards/{rust,tauri,go}/README.md`. Where a token is undeclared, as for Rust, Tauri and Go in this repository, the lens says "no Rust/Tauri/Go standard declared", following the React checklist pattern. This plan does not declare them. | Declaring them means editing `.kyber-weave/kyber-weave.yml` (an `always-human` path) and authoring three standards, which is scope creep. REVIEW.md still carries repository-specific Rust and Tauri rules for `dash/tray`. Declaring the standards is a candidate follow-up todo, reported and not created (AGENTS.md: a todo needs owner acceptance). | autonomous: recommendation accepted (covered by the pointer pattern in `references/react.md` and `references/csharp.md`) |
| D3 | Is Tauri its own checklist or part of Rust? | Its own `references/tauri.md`, pointing at `<tauri-coding-standard>`. It says to also load `rust.md` for `.rs` files and `react.md` or `typescript.md` for the WebView UI. | It mirrors the `tauri-dev` / `rust-dev` agent split and the separate standards templates. Cheap to reverse. | autonomous: low-confidence, no authoritative source (cheap to reverse) |
| D4 | How does a lens prove it applied REVIEW.md? | 1. Add an optional `guidance:` field to the `review-lens` finding schema (the REVIEW.md section ids the finding derives from, for example `[2, 12]`). 2. Require the `NO FINDINGS:` sentence to name the guidance sections checked. 3. `code-reviewer` adjudication records a seat that returned no guidance attestation, when guidance was supplied, as a council-coverage gap in the report; it does not drop that seat's findings. There is no C# change. | `ReviewJson.ReadFindings` uses `System.Text.Json` defaults, which ignore unknown members (`ReviewJson.cs:59-94`), so the extra field cannot break `review verdict`. REVIEW.md's own finding format already has a `CLASS` line. Alternative rejected: an engine-enforced field, which changes a public schema, needs a new `KW-REVIEW-*` id, and is expensive to reverse. `review-triage` seats get the Ground rules and *What not to flag* only, with no attestation field, because they attribute tool output and do not judge. | autonomous: recommendation accepted |
| D5 | What shape should the lens map take? | Two axes. **By lens:** one row per real lens in the `code-review` catalogue (all 15). **By technology present:** one row per checklist technology (csharp, typescript, react, python, sql, pulumi, azure, github-actions, rust, tauri, go, plus shell/scripts and agent-prompt Markdown). A seat gets its lens row plus the technology rows for the diff. The refuter gets the same sections as the seat it refutes. | This fixes gap 2. T2 tests it so the map cannot drift from the catalogue again. | autonomous: recommendation accepted |
| D6 | Byte-match risk when editing `code-reviewer.md`, `review-lens.md` and `review-triage.md` | `code-reviewer` is a migrated agent. `SquadCanonicalContentTests.LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest` asserts `agent.BodyDigest == report.FinalBodySha256` (`migration/code-reviewer.md` line 8, the SHA-256 of the UTF-8, LF-normalized body). T4 therefore recomputes `final-body-sha256` and adds a one-paragraph evolution note to that report. `review-lens` and `review-triage` are `ExpectedNewAgents` with no migration report, so they have no byte pin. Their `## Discovery order` sections are pinned (`AssertDiscoveryContract`: "Discovery reporting is exempt", "machine-consumed") and must stay word-for-word. The `code-review` skill is one of the five evolved skills, so its bytes are not pinned, but `CodeReviewSkillRetainsSelfReviewTriggerAndGate` pins "self-review gate before pushing", "## Pre-push self-review gate" and "major-or-worse". | Requirements: "every non-evolved agent body matches the canonical bytes" (`docs/kyber-squad/requirements.md` line 128). | autonomous: recommendation accepted (covered by the tests named) |
| D7 | Development-mode classification of prompt and documentation tasks | The plan stays `test-first`. T3 and T4 are GREEN for T2's RED contract test. REVIEW.md rule content (T5) is marked no-test, verified by T1's evidence ledger and T6's checks. No `standard` task is declared. | Following the packet: C# stays test-first. The contract test covers every structural promise, so no opt-out is needed. | autonomous: recommendation accepted |
| D8 | Expected council verdict | `NEEDS_HUMAN`. The change edits `products/kyber-squad/agents/**`, which `review.policy.always-human` reserves (`KW-REVIEW-008`). The owner reviews before merge. | `.kyber-weave/kyber-weave.yml` lines 310-315. This is expected and is not a failure. | autonomous: recommendation accepted |
| D9 | The evidence sits in `.agent_scratchpad/`, which is gitignored | T1 copies the verified evidence (PR number, path, short quote, cluster) into this plan's Appendix A, so it survives archival. | `.gitignore` line 14. | autonomous: recommendation accepted |
| D10 | Rule admission threshold for REVIEW.md | A new rule needs at least 3 distinct Kilo comments across at least 2 PRs, or it extends an existing rule. Clusters below that threshold (split-brain at 2, CRLF at 1, unbounded reads at 3 within 2 PRs) are merged into the nearest existing section as an example bullet, not given a new section. Every pre-existing rule stays unless T1 finds it duplicated. | REVIEW.md "Maintaining this file": "Add a rule here when the same class of finding appears in three or more reviews". | autonomous: recommendation accepted |
| D11 | Approve and execute this plan? | **Approved.** The plan moves from Draft to Ready, with `development-mode: test-first` and scale `extends-component` (ReviewCouncil, KyberSquad). | The conductor supplied explicit `AUTONOMOUS_APPROVAL: approve-and-execute` under resolved autonomy `autonomous`, 2026-10-26. | autonomous: recommendation accepted (conductor `AUTONOMOUS_APPROVAL`, 2026-10-26) |
| D12 | When does `docs validate . --merge-ready` run? | **Only after T8 archives this plan.** Interim gates (T6, and any check before archival) run plain `docs validate .` plus `docs drift .`. T8 archives the plan and updates the index row first, then runs `docs validate . --merge-ready` and `docs drift .` as the final gate. | While this plan sits in `docs/plans/`, `--merge-ready` fails by design with `KW-DOC-LIFECYCLE-003` (`docs/plans/README.md`). A failure there before archival is expected and is not a defect. | conductor ruling, 2026-10-26; autonomous: recommendation accepted |
| D13 | Where does the work land? | **A feature branch, never `main`.** Every task commits to that branch; the change reaches `main` only through a pull request. | Conductor ruling. D8's `NEEDS_HUMAN` verdict also requires owner review before merge. | conductor ruling, 2026-10-26; autonomous: recommendation accepted |

---

## 3. Investigation findings

- **Guidance wiring.** `products/kyber-squad/agents/code-reviewer.md:62` is the only canonical mention of **<review-guidance>**. Config Reg declares `<review-guidance>: REVIEW.md` through `config-reg:` in `.kyber-weave/kyber-weave.yml`. `code-review/SKILL.md` steps 2 to 4 and the pre-push gate have no guidance step. `code-reviewer.md` steps 3 (refute) and "When you cannot invoke another agent" do not mention it.
- **Lens schema.** `review-lens.md` §3 fixes the YAML finding shape, and §5 makes `NO FINDINGS:` one sentence. Neither carries guidance. The findings JSON reader ignores unknown members.
- **Checklists.** `skills/code-review/references/` holds 7 pointer checklists. Each one: "Review X changes against **<x-coding-standard>** … states no rule of its own … if undeclared or draft, say so". Standards templates exist for typescript, rust, tauri and go under `products/kyber-squad/standards/`. This repository declares only `csharp`, `test` and `typescript`.
- **Pinned counts.** `SkillResourceDispositionAuditTests` asserts exactly `82` resource rows (line 112) and derives `pointer-already-#126` for non-lens `code-review/references/*`. Four new checklists make it 86, and each needs a dispositions row with Delivery `rendered`, which requires a Markdown link from `SKILL.md`. `docs/code-review/architecture.md` says "7 reference files", and `docs/kyber-squad/requirements.md` says a fresh Copilot render emits 123 files. Both become stale.
- **Byte pins.** See D6.
- **Verdict policy.** `products/kyber-squad/agents/**` is `always-human` (D8).
- **Evidence shape.** Kilo comments cover `dash/src/**` (TS engine, capture plugins, MCP), `src/KyberWeave.*` (Squad presets, instances, docs validators), `scripts/*.sh` (SBOM), `products/kyber-squad/**` (agent and skill prompts, `post_replies.py`), `products/kyber-utilities/statusline/pi/statusbar.ts`, `.github/workflows/ci.yml`, and plans and docs. The sample has no `.rs` or `.go` comments. The Rust, Tauri and Go checklists are a coverage requirement from the owner, not an evidence-driven one.

---

## Design harvest

| ID | Target component | Target doc type | Target doc | Content |
|---|---|---|---|---|
| H1 | ReviewCouncil | ARCHITECTURE | `docs/code-review/architecture.md` | A new "Review guidance" section covering: host **<review-guidance>** loaded by the reviewer; the two-axis lens map; seats receive sections; refuter parity; the in-process path; the `guidance:` attestation and the coverage-gap rule (D4, D5). Update the parts table to 11 technology checklists. |
| H2 | ReviewCouncil | ARCHITECTURE | `docs/code-review/architecture.md` | The checklist roster adds TypeScript (Node), Rust, Tauri and Go, with the D2/D3 pointer semantics. |
| H3 | KyberSquad | REFERENCE | `docs/kyber-squad/skill-resource-dispositions.md` | Four new `review-pointer` / `pointer-already-#126` / `rendered` rows. Resource count 82 becomes 86. |
| H4 | KyberSquad | REQUIREMENTS | `docs/kyber-squad/requirements.md` | Copilot render file count (123 becomes the measured value). |
| H5 | ReviewCouncil | (host guidance) | `REVIEW.md` | The refreshed rules and the two-axis lens map. This is canonical in itself and has no further harvest. |

ADR expectation: no new ADR. Waiver reason: this refines the existing three-layer council (ADR 0002) and the pointer-checklist pattern (#126) with no new boundary, dependency, schema or rule id.

---

## 5. Tasks

Every developer task ends with the worker's own `task-gate` run.

### T1: Verify the clusters against `prs.json` and design the REVIEW.md rules

- **Objective:**
  - Read every Kilo inline comment in `.agent_scratchpad/kilo-review-mining/prs.json` (#407-#439).
  - Confirm or correct each cluster in §1.1 by reading the comments themselves, not keywords. Reclassify any comment the keyword pass mis-bucketed; the summary's examples show the same comment under several clusters.
  - For each rule REVIEW.md will gain or extend, cite at least 3 distinct comments as PR number, path and a quote of 25 words or fewer, under the D10 threshold.
  - Map each rule to: an existing REVIEW.md section (extend) or a new section; the owning lens(es); and the technology row(s) (D5).
  - Mark each previously distilled rule (about 100 PRs) as keep, merge or duplicate.
  - Record the "what the council should have caught, and why it did not" reason per cluster: guidance not loaded, no rule, wrong lens, or no checklist.
- **Files:** this plan only. Add `## Appendix A: Evidence ledger` and `## Appendix B: REVIEW.md rule design`.
- **Acceptance:**
  - Every cluster has a verified count and at least 3 cited examples, or is explicitly merged under D10.
  - Every proposed rule names its section, lens and technology rows.
  - No example appears as primary evidence for two rules.
  - Candidate mappings to verify (not binding):
    - §2 gains wrong-typed fields and `> 0` on untrusted values (#439, #414, #432), GraphQL and JSON response shapes in scripts (#414 `post_replies.py`), and one-sanitizer-everywhere (#439 `partText`).
    - §3 gains `except OSError: hashes = []` (#417 SBOM).
    - §4 gains capped serialization of host payloads (#435) and streaming JSON (#433).
    - §5 gains the same field derived two ways (#439 `partsOf` vs `context.ts`).
    - §6 gains install and receipt idempotency (#435, #429).
    - §7 becomes "Cross-platform shell, paths, and line endings" (#431 `\r\n---`, #417 `.sh`).
    - §9 is generalised to "values written into generated YAML/Markdown/JSON" (#431 preset name).
    - §10 gains a malformed-input test per guard.
    - §11 gains prompt and plan drift (#425, #424 agent and skill `.md`).
    - A new TypeScript Node section covers `dash/src`, the capture plugins and the statusline.
- **Depends on:** none.
- **Skills:** `kyber-docs-authoring`. This is evidence work; the conductor may route it back to `architect` because it edits the plan.

### T2: RED contract test for guidance wiring and checklist roster

- **Objective:** add `tests/KyberWeave.Tests/ReviewGuidanceCouncilContractTests.cs`, which asserts all of the following:
  - (a) `skills/code-review/SKILL.md` links `references/typescript.md`, `rust.md`, `tauri.md` and `go.md`. Each file exists, names its token (`<typescript-coding-standard>`, `<rust-coding-standard>`, `<tauri-coding-standard>`, `<go-coding-standard>`), and carries the "not declared / draft → say so" clause.
  - (b) The REVIEW.md lens-map "by lens" rows equal, as a set, the lens names in the SKILL.md catalogue table. The "by technology" rows equal the checklist file stems plus `shell`/`scripts` and `agent-prompts`.
  - (c) Every `### N.` section in REVIEW.md is referenced by at least one lens-map row, and every referenced number exists.
  - (d) The `review-lens.md` finding schema contains a `guidance:` line, and its `NO FINDINGS` instruction requires naming the guidance sections.
  - (e) `code-reviewer.md` names **<review-guidance>** in the council step, the refutation step and the in-process fallback, and states the coverage-gap rule. `code-review/SKILL.md` names **<review-guidance>** in step 2, step 3 and the pre-push gate.
  - (f) In `SkillResourceDispositionAuditTests`, change the pinned `82` to `86` (a deliberate count change, not a weakening).
- **Files:** `tests/KyberWeave.Tests/ReviewGuidanceCouncilContractTests.cs` (new) and `tests/KyberWeave.Tests/SkillResourceDispositionAuditTests.cs` (line 112 and the doc comments at lines 15 and 75).
- **Acceptance:** the new tests fail for the missing wiring and checklists, the 86 pin fails, and every other test is unchanged.
- **Depends on:** none.
- **Skills:** `test-dev`.

### T3: TypeScript (Node), Rust, Tauri and Go review checklists

- **Objective:** create `products/kyber-squad/skills/code-review/references/{typescript,rust,tauri,go}.md` in the exact pointer shape of `react.md` and `csharp.md`:
  - `typescript.md` points at **<typescript-coding-standard>**, which resolves to `docs/standards/typescript/README.md` here. It covers Node `.ts`/`.mts`/`.cts` outside a React UI. TSX defers to `react.md`.
  - `rust.md` covers `.rs`, `Cargo.toml` and `build.rs` outside Tauri.
  - `tauri.md` covers `src-tauri`, IPC commands and capabilities, and also loads `rust.md` and the UI checklist (D3).
  - `go.md` covers `.go`, `go.mod` and `go.sum`.
  - Each file states no rule of its own (D1), and each may have a "Review procedure" convention line only where `react.md` has one.
  - Add the four rows to `docs/kyber-squad/skill-resource-dispositions.md`: `review-pointer | pointer-already-#126 | rendered | the declared <x-coding-standard>`. Update the "82" prose count to 86.
- **Files:** the four new files, and `docs/kyber-squad/skill-resource-dispositions.md` (rows, count and `last-reviewed`).
- **Acceptance:** T2 (a) for file content, and the dispositions-row tests, pass once T4 adds the SKILL.md links that make Delivery `rendered` true.
- **Depends on:** T2.
- **Skills:** `code-review`, `typescript-dev`, `rust-dev`, `go-dev` (read for scope wording only).

### T4: Wire guidance through the skill, the agents and the lenses

- **Objective:**
  - **`code-review/SKILL.md`:**
    - In the Technology checklists paragraph, link the four new checklists.
    - Step 2 (Council): the reviewer reads **<review-guidance>** and pastes each seat's lens row plus its technology rows plus the Ground rules. Triage seats get the Ground rules and *What not to flag* only (D4/D5). A missing guidance file is reported.
    - Step 3: the refuter receives the same sections.
    - Step 4: discard against Ground rules and *What not to flag*, and record the attestation gap.
    - Pre-push gate step 3: walk the **<review-guidance>** recurring defect classes for the technologies present, de-duplicating the inline a–f list into pointers where REVIEW.md already states the class. Keep a–f wording where it is portable and REVIEW.md may be absent in a host.
    - Report format "Council coverage": each seat's guidance sections.
    - Keep the three pinned phrases (D6).
  - **`agents/code-reviewer.md`:** the council paragraph (line 62) is extended for lens and technology rows, triage scope and refuter parity. Add the guidance clause to step 3 and to "When you cannot invoke another agent". Add the adjudication bullet for the coverage gap (D4).
  - **`migration/code-reviewer.md`:** recompute `final-body-sha256` (SHA-256 of the UTF-8, LF-normalized body after frontmatter) and add a sentence to the evolution paragraph naming this change.
  - **`agents/review-lens.md`:** add `guidance: [<REVIEW.md section ids>]` (optional when no guidance was supplied) to the §3 schema. In §5, `NO FINDINGS:` must name the guidance sections checked. §2 says to apply the supplied guidance sections alongside the lens file. Do not alter the `## Discovery order` text.
  - **`agents/review-triage.md`:** one sentence saying it applies the supplied Ground rules and *What not to flag*. Discovery text unchanged.
  - **`lenses/correctness.md`:** add portable technique bullets (D1): non-finite numbers and wrong-typed fields from untrusted input (`typeof` accepts `NaN`, a string compared with `>`), line-ending-aware slicing and offsets, and one sanitizer or normalizer reused by every sibling path.
  - **`lenses/test-adequacy.md`:** each new guard or parser has a malformed-input test (wrong type, non-finite, CRLF, empty), and a fix's test fails with the fix reverted.
- **Files:** `products/kyber-squad/skills/code-review/SKILL.md`, `products/kyber-squad/agents/code-reviewer.md`, `products/kyber-squad/migration/code-reviewer.md`, `products/kyber-squad/agents/review-lens.md`, `products/kyber-squad/agents/review-triage.md`, `products/kyber-squad/skills/code-review/references/lenses/correctness.md` and `products/kyber-squad/skills/code-review/references/lenses/test-adequacy.md`.
- **Acceptance:** T2 (a), (d), (e) and (f) are GREEN with T3. `SquadCanonicalContentTests` passes in full (migration digest, discovery contract, self-review pin, no `task-reviewer` text). Nothing under repository-root `.github/` is touched.
- **Depends on:** T2 and T3 (`SKILL.md` links T3's files).
- **Skills:** `code-review`, `writing-trigger-descriptions` (only if a description is touched; none is planned).

### T5: Rewrite REVIEW.md (keep and de-duplicate the old rules, add the #407-#439 rules, two-axis lens map)

- **Objective:**
  - Apply Appendix B. Update the intro provenance: about 100 PRs from 30 Sep to 14 Oct 2026, plus Kilo #407-#439 from mid to late October 2026.
  - Keep every retained earlier rule.
  - Merge the duplicates T1 marked.
  - Add new rules with a short `(e.g. #431)` provenance tag each.
  - Replace the lens map with the D5 two-axis map using exact catalogue lens names and checklist technology keys.
  - Add the attestation expectation (D4) to "How reviewers use this file" and to "Finding format" (`GUIDANCE` line).
  - Keep the Ground rules, *What not to flag*, and the maintenance rule.
  - REVIEW.md stays plain Markdown outside `docs/`. Its links must still resolve.
- **Files:** `REVIEW.md`.
- **Acceptance:** T2 (b) and (c) are GREEN. Every Appendix B rule is present. No retained rule is lost; T1's keep/merge list is the check.
- **Depends on:** T1 and T2.
- **Skills:** `code-review`, `kyber-docs-authoring`.

### T6: Gates and render verification

- **Objective:** run the declared suite and the extra checks the owner asked for, and record the output:
  - `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json` (the full declared suite: format, build, test, the four `.apm` skill validate/lint/scan triples, `docs validate`, `docs drift`, inspectcode, duplicates, the `dash` TypeScript gates);
  - `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `-- docs drift .` (plain, not `--merge-ready`; D12: `--merge-ready` runs only in T8, after archival);
  - `skill validate`, `skill lint --min-desc-score 70` and `skill scan --fail-on critical` over `products/kyber-squad/skills/code-review` (owner-requested, not a declared gate);
  - a fresh Copilot render or `squad pack`, to measure the rendered file count for H4 and confirm the four checklists render with no dangling link.
- **Files:** none (evidence only, under `artifacts/`).
- **Acceptance:** every blocking gate passes. Skill checks on `code-review` show no errors, and any lint score under 70 is reported. The render count is measured, not inferred.
- **Depends on:** T3, T4 and T5.
- **Skills:** `task-gate`, `code-review`.

### T7: Council review (dogfood)

- **Objective:** run `code-reviewer` over the whole change. The run itself must show seats receiving REVIEW.md sections and returning `guidance:` or naming their sections in `NO FINDINGS`.
- **Acceptance:** the verdict is reported as the engine returned it. `NEEDS_HUMAN` under `KW-REVIEW-008` is expected (D8). Actionable major or critical findings get one remediation pass.
- **Depends on:** T6.
- **Skills:** `code-review`.

### T8: docs-dev closeout

- **Objective:**
  - Harvest H1 to H4. H5 is self-canonical.
  - Update `docs/code-review/architecture.md`: the Review guidance section, the parts table count, and `last-reviewed`.
  - Update `docs/kyber-squad/requirements.md` with the render count from T6.
  - Map D1 to D10 and record "no ADR, ADR waiver" with its reason.
  - Report the D2 follow-up todo candidate: declare the Rust, Tauri and Go standards. Do not create it without owner acceptance.
  - Archive this plan to `docs/archive/plans/` and move its index row to Archived Plans. Only after the archive step, run `docs validate . --merge-ready` and `docs drift .` as the final gate (D12).
- **Files:** `docs/code-review/architecture.md`, `docs/kyber-squad/requirements.md`, this plan (moved to `docs/archive/plans/`), and `docs/plans/README.md`.
- **Depends on:** T7.
- **Skills:** `kyber-docs-authoring`, `app-docs-standard`.

### Dependency graph

```text
T1 ─────────────┐
T2 ─┬─> T3 ─> T4 ┼─> T6 ─> T7 ─> T8
    └───────────>T5 (also needs T1)
```

T1 and T2 start in parallel. T3 waits for T2. T4 waits for T3 (shared `SKILL.md` link targets). T5 waits for T1 and T2 and runs in parallel with T3 and T4 (no file overlap). T6 joins T4 and T5.

---

## 6. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | none (no-test) | — | Evidence ledger and rule design in Appendices A and B | — | Read-only verification: every cluster count and example re-checked against `prs.json`. The T5 reviewer (council `intent-alignment`) checks REVIEW.md against Appendix B |
| T2 | `tests/KyberWeave.Tests/ReviewGuidanceCouncilContractTests.cs`, `SkillResourceDispositionAuditTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class "*ReviewGuidanceCouncilContractTests"`, then the same with `"*SkillResourceDispositionAuditTests"` (class filter per `tests/KyberWeave.Tests/AGENTS.md` if the syntax differs) | (a) to (f) in T2 | Captured failing output naming the missing checklists, the lens-map and catalogue mismatch, the missing `guidance:` field and wiring, and `82 != 86` | Same command, all passing, assertions unchanged |
| T3 | Same two classes | Same | (a) and the disposition rows | T2's RED | (a) passes; the disposition tests pass after T4 |
| T4 | Same, plus `SquadCanonicalContentTests` | Same, plus `--filter-class "*SquadCanonicalContentTests"` | (d), (e), migration digest, discovery contract, self-review pin | T2's RED for (d) and (e). The digest test goes RED on the first body edit, which is expected | All three classes pass with no assertion edits beyond T2's |
| T5 | `ReviewGuidanceCouncilContractTests` | Same | (b) and (c) | T2's RED for (b) and (c) | (b) and (c) pass. Rule content: no-test, verified against Appendix B |
| T6 | Full suite | `review gates . --out artifacts/gates.json` plus the T6 extras | All declared gates | — | Every blocking gate passes |
| T7, T8 | none (no-test) | — | Review and closeout | — | Engine verdict reported; `CLOSEOUT_COMPLETE` with `docs validate --merge-ready` (run after archival, D12) and `docs drift` clean |

---

## 7. Risks

- **Prompt bloat.** Pasting more REVIEW.md into each seat costs tokens. The two-axis map limits each seat to its rows, and REVIEW.md stays a checklist of bullets.
- **Over-flagging.** New rules can raise false positives. Ground rule 1 (a concrete failure), confidence 7 or above, and the refutation pass are unchanged.
- **Attestation as theatre.** A seat can list sections it did not apply. The coverage gap is recorded but is not a verdict input (D4). T7 inspects real outputs.
- **Digest churn.** Any later `code-reviewer.md` edit must recompute the digest. The existing test catches it.
- **The council is never measured against Kilo again.** Follow-up candidate, reported and not created: rerun the mining after about 10 PRs and compare the per-PR Kilo count against the 4.17 baseline.

---

## 8. Verification gates and review

These are the gates from `AGENTS.md` "Before opening a PR": `dotnet format` whitespace and style, build, test, skill validate/lint/scan on the four `.apm` skills, `docs validate .` and `docs drift .`. They run through `review gates`. `docs validate . --merge-ready` runs once, in T8, after the plan is archived (D12). All work lands on a feature branch, never `main` (D13). The owner-requested extras are listed in T6. No `dash/` change is planned, so the dash gates run only because they are declared. The council verdict is expected to be `NEEDS_HUMAN` (D8).

---

## Appendix A: Evidence ledger

Written by T1 on 2026-10-26 from `.agent_scratchpad/kilo-review-mining/prs.json` (gitignored, so the evidence lives here, D9).

> Discovery (T1 evidence pass): codegraph 0, docs 0, direct reads 8, greps 5, globs 3.
> - CodeGraph and the docs tools were not used. The evidence is a gitignored JSON file and REVIEW.md, which is root-level host guidance outside `docs/`. Neither is in an index, and no code symbol was needed.
> - Direct reads:
>   - this plan (task target);
>   - `prs.json` lines 1-200 (the full text of the first 8 comments);
>   - `summary.md` (the keyword pass being verified);
>   - `REVIEW.md` in full (its rules are what is being redesigned);
>   - four reads of the grep extraction below.
> - Greps:
>   - a count of `number`/`path`/`body` keys (459);
>   - one extraction of every PR number, every comment `path`, and the first 420 characters of every `body` (Kilo comments, author replies and Kilo summaries) over all 4 000-plus lines;
>   - two paging attempts;
>   - the `code-review/SKILL.md` lens catalogue (lines 82-96) for the exact lens names and seat kinds.
> - Coverage: all 125 inline comments and all their replies were read. Each was read to at least 420 characters: the whole issue statement, and for most comments the fix too. Where the 420-character cut fell before Kilo's fix, the author's reply supplied it. Nothing was sampled. Kilo summary bodies were read only to that length; they are not evidence for any rule.

### A.1 Corrections to §1.1 and to the T1 candidate mappings

- **"All resolved, so all real defects" is not quite true.**
  - All 125 threads are resolved, and 124 led to a code or doc change.
  - #439 `kilo-review-loop/SKILL.md` (scope creep) was declined, because the owner had chosen that branch.
  - #435 `opencode.ts` (`--config-dir` dropped) got a "No code change" reply first and was then fixed in `cb8f7e5`.
- **Council exposure.** 12 of the 16 PRs with Kilo comments cite a council verdict in their bodies: #435, #434, #433, #432, #431, #425, #422, #420, #419, #415, #410 and #408. Those 12 PRs carry 98 of the 125 comments. The 4 PRs without council evidence are #439, #429, #424 and #414.
- **`.sh` 8** is a single file, `scripts/generate-artifact-sboms.sh` (#432). Four of the 8 target Python embedded in that script (`except OSError`, `isinstance`), not shell.
- **Keyword counts against verified primary counts** (each comment counted once, §A.2):
  - null or malformed input: 29 → 15 (11 untrusted-shape, 4 C# `!` null-forgiving).
  - boundary, empty, off-by-one: 28 → not a coherent cluster. Its members split into absent-versus-zero (4), CRLF (1) and numeric range checks (counted under V1). Dropped as a cluster.
  - weak tests: 24 → 15.
  - non-finite or wrongly typed numbers: about 21 → 9.
  - swallowed errors: 16 → 14, plus a new 5-comment cluster: catch filters too narrow for a "never throws" contract.
  - doc, spec or drift mismatch: 11 → 16 (the keyword pass missed the `.md` prompt comments in #424).
  - security: 8 → 14 (5 injection or allowlist, 9 filesystem containment, symlink or atomic write).
  - config, YAML or schema: 8 → not coherent. One YAML injection (under V7) and 4 CI workflow comments (V16).
  - races or idempotency: 7 → 7 shared-state and lifecycle comments, a different set. There is no idempotency evidence: #429's two comments are a plan typo and an undefined config key.
  - unbounded reads: 3 → 2 primary, plus 1 secondary (#439 GraphQL `first:100`).
  - split-brain: 2 → 6 (sibling-path divergence).
  - CRLF: 1 → 1, confirmed.
- **Candidate mappings corrected:**
  - "§6 gains install and receipt idempotency (#435, #429)" is not supported. It becomes **path containment and symlink-safe writes** (V6).
  - "§11 gains prompt and plan drift (#425, #424)": #425's `.md` comment is scope discipline. The prompt evidence is #424, #434 and #429.
  - "§9 generalised" becomes a new language-neutral §12, because §9 is keyed to C#.

### A.2 Verified clusters (each comment counted once)

Comment ids are `PR-n`, where n is the comment's order within that PR in `prs.json`. Paraphrases are mine; backticked fragments are verbatim from the data. The "Why missed" codes are:

- **G**: guidance not loaded on the review path (gap 1).
- **R**: no rule existed.
- **L**: the lens map routes the section to a lens name that does not exist (gap 2).
- **C**: no technology checklist (gap 4).

| Id | Cluster | Count / PRs | Existing REVIEW.md coverage | Why missed |
|---|---|---|---|---|
| V1 | Untrusted numbers: non-finite, negative, non-integer, string-compared | 9 / #439 #435 #433 #422 #414 | §2 covers `typeof` and `NaN` only | G, C, R (negative, integer, `>`) |
| V2 | Untrusted shapes: nested null, wrong-typed fields, script inputs and API responses, lax parsers, lax decoding, key passthrough | 11 / #439 #433 #425 | §2 "cast is not validation", partly | G, C, R (scripts, parsers) |
| V3 | C# `!` null-forgiving on values that can be absent | 4 / #431 | §2 .NET bullet, partly | G, R |
| V4 | Swallowed or blanket catch; failure reported as success or as empty | 14 / #434 #433 #432 #431 #414 | §3, fully | G |
| V5 | "Never throws" or "fails closed" contract with too-narrow filters, or a probe outside the `try` | 5 / #435 #434 #422 #414 | none | R |
| V6 | Path containment, symlink-following, predictable temp files, permission only at creation | 9 / #435 #434 #432 #431 #414 | §7 PID-reuse bullet only | R, L (security takes "6 (paths)", and §6 has no containment rule) |
| V7 | Injection and allowlists: shell, GraphQL, YAML; denylist or partial loopback matching | 5 / #439 #435 #431 | §9 escaping (C# renderers only) | R, L |
| V8 | Substring or keyword matching where identity is required | 2 / #434 | §5, fully | G (low evidence, D10) |
| V9 | Sibling-path divergence: a sanitizer, validator or derivation skipped on one path | 6 / #439 #435 #434 #431 | §5 last bullet, partly | G, R |
| V10 | CRLF line-ending offsets | 1 / #431 | none | R (low evidence, D10) |
| V11 | Unbounded token or serialization size | 2 / #435 #433 | §4 UI `JSON.stringify` only | R (low evidence, D10) |
| V12 | Tests that cannot fail for what they claim | 15 / #432 #422 #415 #408 | §10, in principle | G, R (structural assertions) |
| V13 | Docs, plans and agent prompts: undefined outcomes, keys or rubrics; sibling contracts out of step; stale counts, status or claims | 16 / #434 #432 #429 #424 #419 #415 #410 | §11, partly | G, R |
| V14 | Scope discipline | 3 / #439 #425 #408 | Scope discipline, fully | G |
| V15 | TypeScript Node state and lifecycle: shared module state, mutating shared objects, child-process awaits and errors, double reads | 7 / #433 #422 #414 | §8 (React only) | C, L, R |
| V16 | CI workflow: event-dependent contexts, duplicated lists, failure paths, silent retries | 4 / #434 #420 | none (only the github-actions checklist) | R |
| V17 | Shell exit paths: temp files, stale outputs, trap quoting, honest counts | 4 / #432 | §7 "clean up temp files on every exit path", partly | G |
| V18 | TypeScript type escape hatches (`never[]`, `any[]`) | 2 / #422 | none | R (low evidence, D10) |
| V19 | Absent is not zero | 4 / #433 #425 #422 | §1, fully | G, C |
| V20 | Copy-pasted implementation | 1 / #408 | §5 "exists once" | G (low evidence) |
| V21 | Digest compared against the wrong root's receipt | 1 / #435 | §9 "compare like with like" (C# only) | L (low evidence) |

The total is 125. Per PR: #439 12, #435 11, #434 8, #433 15, #432 22, #431 15, #429 2, #425 5, #424 7, #422 10, #420 3, #419 1, #415 4, #414 6, #410 1 and #408 3.

### A.3 Examples per cluster

Each example gives the PR, the path, the comment, and → the fix.

**V1 (9):**
- 439-2 `dash/src/analysis/context.ts`: `part.tokens ?? countTokens(text)` lets `NaN` or `Infinity` poison buckets. → All counts go through `measuredCount` (finite or `undefined`).
- 439-3 `dash/src/canon/runs.ts`: `'28' > 0` is true by string comparison, and `Infinity` passes. → The same finite guard is used for both counters.
- 439-10 `dash/src/canon/measured.ts`: `measuredCount(-50)` passes `Number.isFinite` and subtracts tokens. → Requires `value >= 0`.
- 435-1 `dash/src/canon/harnesses.ts`: `typeof NaN === 'number'` poisons the cache-read denominator. → Adds only when `Number.isFinite`.
- 433-1 `dash/src/canon/streaming-json.ts`: `Number("1e999")` is accepted as a scalar. → Non-finite rejected at token close.
- 433-5 `dash/src/canon/streaming-json.ts`: the EOF `finish()` path still emits `Infinity`. → The same check in `finish()`.
- 433-6 `dash/src/analysis/query/inspect.ts`: the legacy path forwards a negative, fractional or `NaN` `turnIndex`. → Validated at the seam as `invalid_request`.
- 422-2 `products/kyber-utilities/statusline/pi/statusbar.ts`: `usage.input ?? 0` renders `NaN` or the string `"42"`. → A finite check on every accumulator.
- 414-1 `dash/src/capture/kilo-plugin/install.ts`: `{"ts":1e999}` crashes `toISOString`. → Only timestamps within the Date range are accepted.

**V2 (11):**
- 439-4 `dash/src/canon/runs.ts`: `Object.keys(r.content)` throws when `content` is null (critical). → A null or non-object content map reads as empty, with a test.
- 439-6 `.../kilo-review-loop/scripts/post_replies.py`: `json.load(open(args[0]))` has no encoding and no shape check. → UTF-8 read inside `with`, validated, exits 2.
- 439-7 `post_replies.py`: a trailing `--repo` raises `IndexError`. → `argparse`.
- 439-11 `post_replies.py`: `out["data"]["repository"]...` assumes a perfect GraphQL payload. → Walked defensively; WARN and stop.
- 433-2 `dash/src/canon/streaming-json.ts`: `{"a":1,}` is accepted. → A frame marker rejects a trailing comma.
- 433-3 `streaming-json.ts`: `[1,]` is accepted. → The same marker.
- 433-9 `dash/src/mcp/client.ts`: `{ ...raw }` forwards stray keys to the engine. → The engine allowlists fields; unknown keys get `invalid_request`.
- 433-10 `dash/src/mcp/client.ts`: `TextDecoder` defaults to `fatal:false`, so corrupt bytes are laundered. → `fatal: true`.
- 425-1 `dash/src/capture/harnesses/opencode-plugin/entry.ts`: the code casts to `OpencodeHookPayload` and invents `sessionId: ''`. → No synthesis; legacy `sessionID` is promoted.
- 425-2 `.../opencode-plugin/transport/otlp-sender.ts`: `systemPrompt: 42` reaches the wire. → A `wireString` guard.
- 425-5 `.../opencode-plugin/entry.ts`: an identity-less payload still emits a `trace:undefined` span. → Dropped at the entry boundary.

**V3 (4, one PR):**
- 431-7 `src/KyberWeave.Core/Squad/Presets/SquadModelPreset.cs`: `mergedDefault!` is null when both defaults are null. → A null guard.
- 431-10 `src/KyberWeave.Core/Squad/Rendering/SquadRenderSource.cs`: `TryGetValue` returns true with a null preset, giving an NRE. → `preset is not null`.
- 431-11 `src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs`: `defaultModel!` is null when the category omits a default. → Rejected and the `!` removed.
- 431-13 `src/KyberWeave.Cli/Commands/Squad/SquadPresetUseCommand.cs`: `globalReceipt!` gives an NRE on a deleted receipt. → A guarded "no deployment".

**V4 (14). Representative:**
- 432-3 `scripts/generate-artifact-sboms.sh`: `except OSError: hashes = []` publishes a hash-less component. → Fails closed.
- 432-22 the same file: `except OSError: pass` around unlink. → Narrowed to `FileNotFoundError`.
- 432-4 the same file: a non-list `components` is coerced to `[]`. → `fail()`.
- 434-5 `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`: a corrupt receipt is swallowed and the exit is 0. → A warn diagnostic.
- 431-5 `.../Squad/Instances/SquadInstanceLinkSync.cs`: an empty `catch (IOException or UnauthorizedAccessException)`. → Warns and keeps the record.
- 431-6 the same file: a corrupt `links.json` returns `[]`. → `InvalidDataException`.
- 431-12 `.../Squad/Deployment/SquadLifecycleService.cs`: a bare catch skips preset validation. → Propagated.
- 431-15 `.../Commands/Squad/SquadStatusCommand.cs`: `catch (Exception)` reads as "no presets". → Reported, exit 1.
- 431-2 `.../Squad/Instances/SquadHarnessLauncher.cs`: launches with no home isolation for unsupported rows. → Requires `Status == Ready`.
- 433-8 `dash/src/mcp/client.ts`: an invalid `windowDays` is silently dropped. → `invalid_request`.
- 433-11 `dash/src/server/analytics-routes.ts`: an unknown error code maps to 200. → 500.
- 433-12 the same file: `.catch(() => …)` discards `err`. → Logs it and guards `headersSent`.
- 414-5 `dash/src/capture/kilo-plugin/install.ts`: an EISDIR `index.jsonl` reads as "none yet". → "unreadable".
- 432-19 `tests/KyberWeave.Tests/SbomTests.cs`: a bare `catch {}` on `Directory.Delete`. → Narrowed, with a warning.

**V5 (5):**
- 435-9 `.../Squad/Infrastructure/KyberDashCapturePluginStep.cs`: "Never throws" with a three-type filter, so `UnauthorizedAccessException` escapes. → Added to the filters, in `SelfUpdater` too.
- 435-10 `.../opencode-plugin/install.ts`: `lstatSync` sits outside the `try` and throws `EACCES`/`ENOTDIR`. → Goes through `readSlot`.
- 434-6 `src/KyberWeave.Cli/Commands/Docs/DocsValidateCommand.cs`: catches only `InvalidOperationException` despite a fail-closed promise. → Adds IO, UnauthorizedAccess, Win32 and Timeout.
- 414-2 `dash/src/capture/kilo-plugin/install.ts`: `existsSync` then `readFileSync` throws EISDIR, uncaught. → A non-throwing read helper.
- 422-3 `products/kyber-utilities/statusline/pi/statusbar.ts`: the per-frame `render()` has no try/catch. → Falls back to the last good summary.

**V6 (9):**
- 435-2 `.../opencode-plugin/install.ts`: a receipt path is followed anywhere the basename matches. → Constrained to `plugins/kyberdash-opencode.js`, a regular file, with the marker.
- 435-3 the same file: check-then-write follows a symlink. → `lstat` refusal and an atomic write.
- 435-4 `.../opencode-plugin/consent-write.ts`: the predictable `path.<pid>.tmp`. → A `randomBytes` suffix, `wx`, and `fsync` before rename.
- 434-1 `src/KyberWeave.Core/Docs/Parsing/DocumentLoader.cs`: `Path.Combine` with no containment check. → `GetFullPath` plus a root prefix and `IsContainedFile`.
- 432-5 `scripts/generate-artifact-sboms.sh`: `tar`/`unzip` trust `../` and symlink members. → Pre-listed and rejected.
- 431-3 `.../Squad/Instances/SquadInstanceLinkSync.cs`: a `RelativePath` from `links.json` escapes the root. → Rooted paths and `..` rejected, with canonical containment.
- 431-4 `.../Squad/Instances/SquadInstanceService.cs`: `Directory.Delete(…, true)` follows junctions. → `DeleteTreeNoFollow`.
- 414-3 `dash/src/capture/kilo-plugin/source.ts`: `0600` applies only at creation. → Tightens existing paths.
- 414-4 the same file: `blobs/` is not tightened on the dedup path. → Tightened on every call.

**V7 (5):**
- 431-1 `.../Squad/Instances/SquadHarnessLauncher.cs`: the `cmd.exe` denylist misses `&`, `|`, `<` and `>`. → An expanded set (the comment had asked for an allowlist).
- 431-8 `.../Squad/Presets/SquadModelPresetSelectionStore.cs`: `builder.Append(entry.Value)` writes a raw preset name into YAML. → A name regex plus YAML quoting.
- 439-8 `.../kilo-review-loop/scripts/post_replies.py`: `owner:"%s"` interpolates into GraphQL, and `first:100` has no paging. → GraphQL variables plus `hasNextPage` paging.
- 435-8 `dash/tests/setup/network-guard.ts`: the loopback allowlist misses `127.0.0.2` and `[::1]`. → 127/8, IPv6 and v4-mapped.
- 435-11 the same file: `2130706433` is `127.0.0.1`. → inet_aton-style parsing.

**V8 (2, low evidence):**
- 434-2 `.../Docs/Validation/ArchiveCloseoutValidator.cs`: `destination.Contains("documentation waiver")`. → The marker must lead the destination.
- 434-4 `.../Squad/Deployment/SquadRetiredSkills.cs`: `.Contains(retired)` matches `my-second-brainstorm`. → Per-path-segment match.

**V9 (6):**
- 439-1 `dash/src/analysis/context.ts`: `partText()` exists, but `observed` reads the raw `part.text`. → Reads the sanitized text.
- 439-5 `dash/src/canon/sessions.ts`: `partsOf` drops a part that `context.ts` honours, giving 123 against 0. → Keeps the measured count.
- 439-12 `post_replies.py`: `owner`/`name` are stripped but the raw `repo` reaches REST. → Returns the washed slug.
- 435-5 `dash/src/capture/harnesses/opencode.ts`: `--config-dir` reaches the installer but is dropped at `enable`. → Threaded through `capture enable/disable/status` (`cb8f7e5`).
- 434-3 `.../Docs/Validation/ArchiveCloseoutValidator.cs`: the table parser lacks the fence tracking `SplitSections` has. → The same fence rule.
- 431-9 `.../Squad/Presets/SquadModelPresetStore.cs`: the sibling store validates `HomeDirectory` and this one does not. → `ThrowIfNullOrWhiteSpace`.

**V10 (1):** 431-14 `.../Infrastructure/SquadFrontmatterHelper.cs`: `secondDashes + 4` assumes `\n---`, while `\r\n---` is 5 characters. → The delimiter length is computed.

**V11 (2):**
- 435-6 `.../opencode-plugin/entry.ts`: `JSON.stringify` of whatever the host hands over. → Capped at 262144 characters with a marker.
- 433-4 `dash/src/canon/streaming-json.ts`: `literal()` is unbounded. → Capped like `number()`.
- Secondary: 439-8 `first:100` (primary in V7).

**V12 (15):**
- `tests/KyberWeave.Tests/ReleaseTests.cs` (#432, 6): 432-8 a mention, not a definition; 432-9 a slice to EOF, not to the job; 432-10 `IndexOf` order instead of the `needs:` DAG; 432-11 the docstring is untested; 432-12 the name claims attest steps that are never checked; 432-13 an exact-string `needs:` instead of a set.
- `tests/KyberWeave.Tests/SbomTests.cs` (#432, 5): 432-14 a `Contains` after `release:`; 432-15 a comment mention passes; 432-16 `run: echo sbom-path` passes; 432-17 `EnumerateArray` with no `ValueKind` guard; 432-18 absent and malformed metadata look the same.
- 422-5 `tests/KyberWeave.Tests/UtilitiesStatusLineArtifactTests.cs`: stdout is not captured.
- 415-1 `tests/KyberWeave.Tests/KyberStandardsTemplatesTests.cs`: the name says Eleven and the test asserts 14.
- 415-4 `tests/KyberWeave.Tests/SquadTypeScriptSpecialistTests.cs`: pins 2 of 4 references.
- 408-3 `tests/KyberWeave.Tests/CodexRendererContractTests.cs`: a literal `6` instead of a count derived from the source.

**V13 (16):**
- #424 `products/kyber-squad/agents/conductor/references/autonomy.md`, `.../architect/references/autonomy-decisions.md` and `.../skills/bug-crusher/SKILL.md` (7):
  - a draft PR that is never marked ready is never reviewed;
  - a parked PR notifies nobody;
  - a low-confidence accepted decision becomes load-bearing;
  - a retry uses the same evidence;
  - "after `code-reviewer` returns" does not say which verdict;
  - bug-crusher is out of step with the new `autonomy.md` delivery set (two comments).
- 434-8 `.apm/skills/kyber-docs-onboarding-and-assessment/SKILL.md`: contradictory `--base` advice and an undefined C0-C9 rubric.
- 429-2 `docs/plans/2026-10-24-issue-386-reduce-autonomous-gates.md`: an unnamed config key for the scratchpad path.
- 429-1 the same plan: a word-merge typo (minor).
- 432-20 `docs/archive/plans/2026-10-25-issue-417-…md`: `../adr/` links broken after archive.
- 432-21 the same plan: claims a measured wall time that §12 lacks.
- 419-1 `docs/archive/plans/2026-10-21-vite-launch-editor-advisories.md`: the body says `status: current` and the frontmatter says archived.
- 410-1 `docs/kyber-squad/README.md`: a stale count of 25 SKILL.md files.
- 415-2 `.../typescript-dev/references/data-access.md`: the example says to return early and then runs `IN ()`.
- 415-3 `.../typescript-dev/references/errors-and-async.md`: the example uses `.catch(() => undefined)`, which the guidance two pages earlier outlaws.

**V14 (3):**
- 425-4 `docs/plans/README.md`: two Ready plans in one PR. → Split.
- 408-1 `docs/plans/README.md`: an unrelated draft row. → Removed.
- 439-9 `kilo-review-loop/SKILL.md`: a feature riding on a hotfix. → Declined (owner decision).

**V15 (7):**
- 422-4 `statusbar.ts`: a global `activeTui` is shared across sessions. → Per-session WeakMap.
- 422-8 the same file: `(tui ?? activeTui)` repaints one session. → Broadcast to a live set.
- 422-10 the same file: `liveTuis` is not cleared on a context-less shutdown. → Cleared.
- 433-7 `dash/src/analysis/query/inspect.ts`: `delete safeDetail.turns` mutates a possibly cached object. → Shallow copy first.
- 433-13 `dash/scripts/sea-mcp-smoke.mjs`: awaits a future `close` on an already-exited child. → Tracks close state.
- 433-14 the same file: no `stdin.on('error')`, so EPIPE escapes. → Error handler plus a write guard.
- 414-6 `dash/src/capture/kilo-plugin/install.ts`: a second `readSlot` opens a TOCTOU window. → Derived from one read.

**V16 (4):**
- 434-7 `templates/github-actions/kyber-weave-docs-gate.yml`: `github.base_ref` is empty on push and dispatch. → Branches on the event; values passed through `env`.
- 420-1 `.github/workflows/ci.yml`: the package list is copied in two places. → Single `TAURI_APT_PACKAGES`.
- 420-2 the same file: a retry sleeps with no log line. → Logs each failed attempt.
- 420-3 the same file: the failure path leaves the apt archive root-owned. → `chown` on every exit.

**V17 (4, one PR):** all in `scripts/generate-artifact-sboms.sh`.
- 432-1: the raw temp file is outside the EXIT trap. → Moved under `WORK_PARENT`.
- 432-2: a stale SBOM survives a failed run. → Unlinked before exit.
- 432-6: `trap` expands `TMPDIR` early. → Single-quoted, deferred.
- 432-7: the count includes skipped archives. → Counts attempted archives.

**V18 (2):** 422-1 and 422-6, both `statusbar.ts`. `never[]` and then `any[]` handler types. → A precise `(event, ctx)` signature.

**V19 (4):**
- 422-7 `statusbar.ts`: a failed render shows `⚡ 0`. → `--`.
- 422-9 the same file: a manufactured `cache:0`. → `cache:--`.
- 425-3 `.../opencode-plugin/mapper.ts`: `[]` and `'[]'` are measured differently. → Both dropped.
- 433-15 `dash/src/analysis/query/compare.ts`: empty `runIds` compares the first two runs. → Requires exactly two.

**V20 (1):** 408-2 `src/KyberWeave.Core/Squad/Rendering/CodexRenderer.cs`: `RenderPrimaryAgentEntryPointSkill` duplicates `RenderSkill`. → A shared helper.

**V21 (1):** 435-7 `.../opencode-plugin/install.ts`: the drift check compares against the receipt for another root. → Uses the receipt only when `receipt.path === pluginFile`.

### A.4 Previously distilled rules: keep, merge or duplicate

Nothing is deleted.

- **§1**: keep (V19 confirms it).
- **§2**: keep and extend (R2, R3, R4).
- **§3**: keep and extend (R5, R6). §3 bullet 6 (crash-recovery ordering across sibling paths) and §11 bullet 3 (an ordering comment needs its siblings) are **duplicates**. Merge them into §3 bullet 6, and keep a one-line pointer in §11.
- **§4**: keep and extend (R7, low evidence).
- **§5**: keep. The last bullet ("A normalisation function exists once") **merges** into R8.
- **§6**: keep and extend (R10).
- **§7**: keep, rename, and extend (R11).
- **§8**: keep. The "Several instances … must not share global registrations" bullet stays; R12 generalises it for Node and cross-references it.
- **§9**: keep. The first bullet (renderer escaping) **moves** to the new §12 (R13), with a pointer left in §9.
- **§10**: keep and extend (R14, R15).
- **§11**: keep and extend (R16).
- **Scope discipline**: keep (V14 confirms it).

## Appendix B: REVIEW.md rule design

Lens names are the 15 catalogue names in `code-review/SKILL.md` lines 82-96. `duplicate-implementation`, `static-analysis-triage` and `dependency-supply-chain` are `review-triage` seats and get the Ground rules and *What not to flag* only (D4). Technology keys are the checklist stems plus `shell` and `agent-prompts` (D5).

Two design choices are made here, autonomous and cheap to reverse:

- new sections are numbered §12 (injection) and §13 (TypeScript for Node), so the existing §1-§11 numbers stay stable for `guidance:` attestations;
- §7 is renamed but keeps its number.

### B.1 Rules

Each rule gives its section and action, its imperative, its evidence, its lenses and its technology keys.

- **R2** (§2, extend): Read every number from untrusted input through one guard: `Number.isFinite`, `>= 0` for counts, `Number.isSafeInteger` for indexes. Never compare an unvalidated value with `>`.
  - Evidence: V1 (439-2, 439-3, 439-10, 435-1, 433-1, 433-5, 433-6, 422-2, 414-1).
  - Lenses: correctness, security.
  - Technology: typescript, react, python, csharp.
- **R3** (§2, extend): Validate every level of an untrusted payload: nested objects, field types, script inputs and API responses (`errors` with no `data`). Parsers reject invalid input. Decode text strictly (`fatal: true`). Forward only allowlisted keys. Never invent identity.
  - Evidence: V2 (439-4, 439-6, 439-7, 439-11, 433-2, 433-3, 433-9, 433-10, 425-1, 425-2, 425-5).
  - Lenses: correctness, security.
  - Technology: typescript, python, csharp.
- **R4** (§2 .NET bullet, extend): Don't use `!` on a value that can be null at runtime (merged config, deserialised data, receipts, `TryGetValue` results). Guard it, or reject it with a validation error.
  - Evidence: V3 (431-7, 431-10, 431-11, 431-13). 4 comments in **one PR**; admitted as an extension of an existing rule (D10).
  - Lens: correctness.
  - Technology: csharp.
- **R5** (§3, keep and add examples): Add a Python example (`except OSError:` that publishes or skips) and "coercing a wrong shape to empty (`components = []`) hides the failure". No new rule.
  - Evidence: V4 (432-3, 432-22, 432-4, 434-5, 431-5, 431-6, 431-12, 431-15, 431-2, 433-8, 433-11, 433-12, 414-5, 432-19).
  - Lenses: correctness, supportability.
  - Technology: csharp, typescript, python, shell.
- **R6** (§3, new bullet): Check a "never throws" or "fails closed" contract against every call inside it. Its catch filters cover the whole documented failure set (IO, UnauthorizedAccess, Win32, Timeout), and every probe (`lstat`, `existsSync` then read) runs inside the `try`.
  - Evidence: V5 (435-9, 435-10, 434-6, 414-2, 422-3).
  - Lenses: correctness, supportability.
  - Technology: csharp, typescript.
- **R7** (§4, extend; **low evidence**): Cap the serialized size of host-supplied payloads and every parser token. Paginate an external list until `hasNextPage` is false, or fail loudly at the page limit.
  - Evidence: V11 (435-6, 433-4); 439-8 secondary.
  - Lens: performance.
  - Technology: typescript, python.
- **R8** (§5, extend; merges the old last bullet): Apply one sanitizer, validator or normaliser per value on every sibling entry path (legacy and new, EOF and delimiter, sibling stores, install and enable). Derive the same field the same way in every module.
  - Evidence: V9 (439-1, 439-5, 439-12, 435-5, 434-3, 431-9).
  - Lenses: correctness, prior-art.
  - Technology: all code keys.
- **R9** (§5, keep): substring identity. Add 434-2 (keyword waiver) as an example.
  - Evidence: V8 (434-2, 434-4). **Low evidence**; no new rule.
  - Lenses: correctness, security.
  - Technology: csharp.
- **R10** (§6, new bullets): Contain every path that comes from a receipt, manifest, archive member or caller: canonical full path, root prefix, no rooted path or `..`. Refuse symlinks before writing or deleting (lstat, no-follow delete). Write atomically through an unpredictable temp file (`wx`, fsync, rename). Tighten the permissions of existing files, not only of new ones.
  - Evidence: V6 (435-2, 435-3, 435-4, 434-1, 432-5, 431-3, 431-4, 414-3, 414-4).
  - Lenses: security, authz-tenancy, correctness.
  - Technology: csharp, typescript, shell, rust, tauri, go.
- **R11** (§7, renamed "Cross-platform shell, scripts, CI, and line endings"):
  - (a) workflow values that depend on the event (`base_ref` is empty on push) branch on the event and reach the shell through `env`;
  - (b) a list used twice (cache key and install) lives once;
  - (c) failure exits restore state (ownership, temp files, stale outputs) and log each retry;
  - (d) **low evidence**: slicing frontmatter or lines computes the delimiter length from the match (`\r\n`).
  - Evidence: V16 (434-7, 420-1, 420-2, 420-3) for (a)-(c); V17 (432-1, 432-2, 432-6, 432-7) extends the existing temp-cleanup bullet; V10 (431-14) for (d).
  - Lenses: infra-workflow, correctness.
  - Technology: github-actions, shell, csharp (line endings).
- **R12** (§13, new section "TypeScript for Node (dash engine, capture plugins, MCP, statusline)"):
  - module state is per session or instance, and every shutdown shape clears it;
  - copy an object returned by another component before mutating it;
  - child processes: await only on live state, and attach `stdin` error listeners;
  - derive a decision from one filesystem read;
  - **low evidence** (V18: 422-1, 422-6): no `never[]` or `any[]` escape hatches in host handler types.
  - Evidence: V15 (422-4, 422-8, 422-10, 433-7, 433-13, 433-14, 414-6).
  - Lenses: correctness, performance (leaks).
  - Technology: typescript.
- **R13** (§12, new section "Untrusted values reaching an interpreter or a generated document"; absorbs §9 bullet 1): Pass values as parameters (GraphQL variables, argv arrays, quoted YAML scalars), never by interpolation. Use an allowlist, not a denylist, for shell metacharacters. Match hosts and addresses by parsing them, not by spelling.
  - Evidence: V7 (431-1, 431-8, 439-8, 435-8, 435-11).
  - Lenses: security, infra-workflow.
  - Technology: csharp, python, typescript, shell, github-actions.
- **R14** (§10, extend): A structural test asserts structure: scope workflow and YAML checks to the job or `needs:` block, compare sets rather than strings or `IndexOf` order, and assert a definition rather than a mention. Derive expected counts from the source. Test helpers guard the JSON `ValueKind` and `Assert.Fail` with the path. A test's name, docstring and assertions agree.
  - Evidence: V12 (432-8 to 432-18, 422-5, 415-1, 415-4, 408-3).
  - Lens: test-adequacy.
  - Technology: csharp, typescript.
- **R15** (§10, new bullet; **low evidence from comments**): Each new guard or parser ships a malformed-input test (wrong type, non-finite, negative, CRLF, empty), and that test fails with the fix reverted.
  - Evidence: derived from the fix replies (439-4 "Pinned by a test", 414-1 "Test added", 435-10 "Tests added for a file-as-parent"). Those are secondary, because each is primary for another rule.
  - Lens: test-adequacy.
  - Technology: all code keys.
- **R16** (§11, extend):
  - (a) an agent or skill prompt defines every outcome, verdict, key or rubric it refers to, and its sibling contracts change together;
  - (b) counts, status lines and claimed measurements are updated everywhere they appear;
  - (c) **low evidence** (415-2, 415-3): code examples in skill references obey the guidance next to them.
  - Evidence:
    - (a) 424-1 to 424-7, 434-8, 429-2;
    - (b) 410-1, 419-1, 432-20, 432-21;
    - 429-1 is a typo: minor, never blocking.
  - Lenses: intent-alignment, blast-radius-revertibility.
  - Technology: agent-prompts.
- **Unchanged** (confirmed by the data, no new text): §1 (V19), §8, Scope discipline (V14). V20 (408-2) and V21 (435-7) are low evidence: §5 "exists once" covers V20; V21 is generalised into §5 as a one-line "compare like with like" pointer to §9.

### B.2 Two-axis lens map (replaces the current table)

Each seat gets its lens row, plus the rows for the technologies present in the diff, plus the Ground rules. A refuter gets exactly the sections of the seat it refutes.

**By lens** (all 15 catalogue names):

- intent-alignment: Scope discipline, 11
- correctness: 1, 2, 3, 5, 6, 7
- security: 2, 6, 12
- authz-tenancy: 6, 12
- di-composition: Ground rules only (no Kilo evidence)
- model-placement: Ground rules only (no Kilo evidence)
- prior-art: 5
- duplicate-implementation (triage): Ground rules and *What not to flag* only
- test-adequacy: 10
- static-analysis-triage (triage): Ground rules and *What not to flag* only
- performance: 4, 13
- blast-radius-revertibility: 3, 6, 11
- supportability: 3
- dependency-supply-chain (triage): Ground rules and *What not to flag* only
- infra-workflow: 7, 12

**By technology present** (key: the files that trigger it → sections):

- csharp: `*.cs`, `*.csproj` → 2, 3, 9
- typescript: Node `*.ts`, `*.mts`, `*.cts`, `*.mjs` outside a React UI → 1, 2, 3, 13
- react: `*.tsx`, `*.jsx`, and the `.ts` modules of a React UI → 1, 2, 8
- python: `*.py`, and Python embedded in scripts → 2, 3
- sql: `*.sql` and migrations → 4, 6
- pulumi: Pulumi stack code → 12
- azure: Azure resource definitions → 12
- github-actions: `.github/workflows/*.yml`, `templates/github-actions/*.yml` → 7, 12
- rust: `*.rs`, `Cargo.toml`, `build.rs` outside Tauri → 3, 6 (no Kilo evidence in #407-#439; this is a coverage requirement)
- tauri: `src-tauri/**`, IPC commands and capabilities → 6, 12 (no Kilo evidence; also load the rust and react rows)
- go: `*.go`, `go.mod` → 3, 6 (no Kilo evidence)
- shell: `*.sh`, `*.bash`, `scripts/**` → 3, 7, 12
- agent-prompts: `products/**/agents/**/*.md`, `**/SKILL.md`, skill `references/*.md`, `docs/plans/**`, `docs/archive/**` → 11, Scope discipline

Every section from §1 to §13, plus Scope discipline, is referenced by at least one row. T2 (c) checks this.

**T2 note:** the technology row keys are exactly the 11 checklist stems plus `shell` and `agent-prompts`.

## Closeout

Closeout by `docs-dev`, 2026-10-27. Scale `extends-component` (ReviewCouncil, KyberSquad). The
permanent `## Decisions` table above is preserved unedited. Every harvested item below has
`documentation-impact: extends-component`, its impact component, a `last-reviewed` of 2026-10-27
on the destination, and status archived.

## Closeout mapping

| ID | Destination | Impact component | Last-reviewed | Status |
|---|---|---|---|---|
| D1 | [REVIEW.md](../../../REVIEW.md) holds the Kilo-derived rules (R2 to R16, §2 to §13). Portable technique is in the `correctness` and `test-adequacy` lens files (product canonical, not a doc). The rule routing is in [Review guidance](../../code-review/architecture.md#review-guidance) | ReviewCouncil | 2026-10-27 | archived |
| D2 | [Review guidance](../../code-review/architecture.md#review-guidance): pointer checklists, no rules of their own; Rust, Tauri and Go say so while undeclared. Four `review-pointer` rows in [skill-resource dispositions](../../kyber-squad/skill-resource-dispositions.md). Declaring the standards is a follow-up candidate, not part of this closeout | ReviewCouncil, KyberSquad | 2026-10-27 | archived |
| D3 | [Review guidance](../../code-review/architecture.md#review-guidance): Tauri has its own checklist and also loads the Rust checklist and the React or TypeScript checklist | ReviewCouncil | 2026-10-27 | archived |
| D4 | [Review guidance](../../code-review/architecture.md#review-guidance): `guidance:` attestation, `NO FINDINGS` naming sections, and the coverage-gap rule (not a verdict input). [REVIEW.md](../../../REVIEW.md) "How reviewers use this file" | ReviewCouncil | 2026-10-27 | archived |
| D5 | [REVIEW.md](../../../REVIEW.md) "Lens map" (by lens) and "Technology map" (by technology present). [Review guidance](../../code-review/architecture.md#review-guidance) states the two axes | ReviewCouncil | 2026-10-27 | archived |
| D6 | Byte-pin mechanics are a test-fixture concern (`SquadCanonicalContentTests`). The surviving invariant, that non-evolved agent bodies match their canonical bytes, is already stated in [Kyber-Squad requirements](../../kyber-squad/requirements.md) (Render and knowledge-retention requirement). Documentation waiver: test-fixture scoped; the invariant is already canonical | KyberSquad | 2026-10-27 | archived |
| D7 | Documentation waiver: development-mode classification (`test-first`) is transient process metadata for this plan. It stays in the archived Decisions and Test contract tables | ReviewCouncil | 2026-10-27 | archived |
| D8 | Documentation waiver: the expected `NEEDS_HUMAN` verdict under `KW-REVIEW-008` is a transient review outcome, recorded in the Closeout evidence below. The standing rule, that `products/kyber-squad/agents/**` is always-human, is in [Review council configuration](../../code-review/architecture.md#configuration) | ReviewCouncil | 2026-10-27 | archived |
| D9 | Documentation waiver: the evidence was copied from a gitignored scratchpad into Appendix A, so the archive is the record. No surviving invariant | ReviewCouncil | 2026-10-27 | archived |
| D10 | [REVIEW.md](../../../REVIEW.md) "Maintaining this file" (the admission threshold: three or more reviews, at least two PRs, or an extension; otherwise an example bullet). Also [Review guidance](../../code-review/architecture.md#review-guidance) | ReviewCouncil | 2026-10-27 | archived |
| D11 | Documentation waiver: approval provenance for this plan. The provenance is the Decisions table, which is preserved | ReviewCouncil | 2026-10-27 | archived |
| D12 | Documentation waiver: transient sequencing ruling for this plan. The standing merge gate is already canonical in [docs governance](../../docgraph/governance.md) (`KW-DOC-LIFECYCLE-003`, `KW-DOC-LIFECYCLE-004`) | ReviewCouncil | 2026-10-27 | archived |
| D13 | Documentation waiver: transient branching ruling for this change. It is not a standing repository rule added by this plan | ReviewCouncil | 2026-10-27 | archived |
| H1 | [Review guidance](../../code-review/architecture.md#review-guidance): host guidance flow, two-axis lens map, seat and refuter selection, in-process path, attestation, and coverage gap | ReviewCouncil | 2026-10-27 | archived |
| H2 | [Review council architecture](../../code-review/architecture.md) parts table (11 technology checklists, plus the REVIEW.md row) and the Technology checklists bullet | ReviewCouncil | 2026-10-27 | archived |
| H3 | [Skill-resource dispositions](../../kyber-squad/skill-resource-dispositions.md): 86 resources, four new `review-pointer` / `pointer-already-#126` / `rendered` rows | KyberSquad | 2026-10-27 | archived |
| H4 | [Kyber-Squad requirements](../../kyber-squad/requirements.md) Render paragraph (Copilot render 147). The same count is corrected in [Kyber-Squad onboarding](../../kyber-squad/onboarding.md), [Kyber-Squad architecture](../../kyber-squad/architecture.md) and [Kyber-Squad README](../../kyber-squad/README.md) | KyberSquad | 2026-10-27 | archived |
| H5 | The rules, lens map and technology map are canonical in [REVIEW.md](../../../REVIEW.md), the host guidance file outside the governed corpus. The governed description of how they are used is [Review guidance](../../code-review/architecture.md#review-guidance) | ReviewCouncil | 2026-10-27 | archived |

**ADR disposition:** no ADR — ADR waiver: this refines the existing three-layer council
([ADR 0002](../../adr/0002-three-layer-review-council-verdict-engine.md)) and the pointer-checklist
pattern (#126) with no new boundary, dependency, schema or rule id. The verdict engine and the
findings JSON schema did not change.

## Documentation-impact review (DOCS_REVIEW)

Run record: the change was reviewed by its changed-file list, the destination documents, and the
code and tests they describe. The docs-dev command allowlist has no `git diff`, so the review is
by file and by reading the affected sections, not by diff hunks.

| Change | DOCS_REVIEW | RECOMMENDATIONS |
|---|---|---|
| `REVIEW.md` (rewrite; Appendix B rules; lens and technology maps) | update | Done: D10 admission rule added to "Maintaining this file" |
| `products/kyber-squad/skills/code-review/SKILL.md`, `agents/code-reviewer.md`, `agents/review-lens.md`, `agents/review-triage.md`, lens files `correctness.md` and `test-adequacy.md` | update | Done: [Review guidance](../../code-review/architecture.md#review-guidance). No other canonical document describes the step order |
| `skills/code-review/references/{typescript,rust,tauri,go}.md` (new checklists) | update | Done: parts table and checklist bullet (H2); dispositions rows (H3) |
| `migration/code-reviewer.md` (digest and evolution paragraph) | update | Provenance record only. Its digest is pinned by `SquadCanonicalContentTests` (passes) |
| `docs/kyber-squad/skill-resource-dispositions.md` | update | Done (86 resources; `last-reviewed` 2026-10-27) |
| `docs/kyber-squad/requirements.md`, `onboarding.md`, `architecture.md`, `README.md` (render count 143 to 147; resource counts 82 to 86 and 110 to 114) | update | Done. These resource counts changed with the four new checklists and were stale |
| `docs/todo/squad-pack-single-target-render.md` (historical D29 `Total=123`) | no doc change with justification | Left as recorded. It documents a measurement at its time, which the packet asked to keep |
| Root `README.md` (lines 72 and 154, named in the packet) | no doc change with justification | Checked: neither line holds a render count. The 143 claim is in `docs/kyber-squad/README.md`, which is updated |
| `tests/KyberWeave.Tests/ReviewGuidanceCouncilContractTests.cs` (new), `SkillResourceDispositionAuditTests.cs` (82 to 86), `OpenCodeRendererContractTests.cs` (literal removed; now derived) | no doc change with justification | Test code (test-dev scope). The count documented in dispositions and onboarding matches the pinned 86 |
| `docs/plans/README.md` | update | Done: plan moved from Active to Archived |
| `AGENTS.md`, `CLAUDE.md`, `.kyber-weave/kyber-weave.yml` | not in this change | No doc change. The `.kyber-weave/kyber-weave.yml` declaration of Rust, Tauri and Go standards is a follow-up candidate, not part of this change |

## Closeout evidence

- **Targeted tests (run 2026-10-27, docs-dev):** `ReviewGuidanceCouncilContractTests`,
  `SkillResourceDispositionAuditTests`, `OpenCodeRendererContractTests` and
  `SquadCanonicalContentTests`: 110 of 110 passed, 0 failed.
- **Council (conductor run record):** the final council returned `NEEDS_HUMAN` under
  `KW-REVIEW-008` (`products/kyber-squad/agents/**` is always-human). That is the expected
  verdict under D8, and it still requires owner review before merge. Three major findings and
  the minor findings were fixed in one bounded pass. The council ran in-process on four lenses,
  and the refutation pass was not run.
- **Render count (conductor run record):** the fresh Copilot render is 147 files, measured (143
  on `main`). Docs-dev did not re-measure it.
- **Declared gate suite (T6, `review gates`):** run by the conductor, not by docs-dev, because
  it is outside the docs-dev command allowlist. Not re-run in this closeout.
- **Residuals, not blocking:**
  - The PR-number citations in REVIEW.md were checked against the local evidence file
    (Appendix A, from `prs.json`), not against GitHub. The council did not verify them against
    GitHub either.
  - The refutation pass was not run (see the council line above).
- **Follow-up candidates (reported, not created):**
  - Declare the Rust, Tauri and Go coding standards in `.kyber-weave/kyber-weave.yml`, so the
    three new checklists can point at real standards. This needs owner acceptance.
  - Re-run the Kilo comment mining after about 10 more PRs, and compare the per-PR count with the
    4.17 baseline (§7).
