---
id: archive/plans/2026-10-05-harvest-plan-decision-records
title: "Harvest plan decision records, backfill canonical documentation, and establish durable plan decision contract"
doc-type: plan
status: archived
component: DocGraph
owner: dpalfery
last-reviewed: 2026-10-05
development-mode: standard
---

# Harvest plan decision records, backfill canonical documentation, and establish durable plan decision contract

## Problem and Goal

### Problem
An audit of the 20 most recently archived plans (Issue #279) revealed systematic gaps in decision harvesting and closeout traceability:
1. Five archived plans had missing material contract documentation in canonical docs (#192 quarantine/problems pagination, #180 tool extraction contracts, #183/#185/#187 measured run payloads, #184 turn numbering, and #232 file-synthesized turn dedupe), and one had partial capture (#195 Antigravity OTLP attribution).
2. One archived plan (#192) contained a false historical closeout claim that canonical documentation had been updated, when commit `5974864` changed no canonical documentation.
3. The inventory entry for #225 hid a successful harvest by citing a PR branch rather than linking the canonical destinations.
4. Planning instructions (`plan-authoring.md`) explicitly instructed the planner to "remove the Draft ledger" upon `FINALIZE`, destroying durable records of questions, alternatives, rationale, and mitigations.
5. Readiness and review checks verified that decisions were resolved, but did not check that questions, answers, and mitigating information were permanently retained or mapped to canonical documentation on archival.
6. Stale prose persisted in `docs/dash/architecture.md` (schema versions 14 and 15 vs current schema 17) and `docs/dash/runbook.md` (claiming Claude Desktop remains separate, missing the #182 twin fold).

### Goal
1. Backfill all missing and partial harvests into canonical documentation (`docs/dash/architecture.md`, `docs/dash/runbook.md`, `docs/dash/telemetry-inventory.md`), reconciling stale schema and harness passages.
2. Annotate the historical closeout of #192 to reflect actual evidence without falsifying history.
3. Update `docs/plans/README.md` to link canonical destinations for all audited plans and clarify policy.
4. Establish the permanent `## Decisions` section contract across planning, conductor, docs-dev, and review lens instructions.
5. Establish explicit closeout conventions distinguishing ADR waivers from documentation waivers.
6. Verify all repository gates: `dotnet build`, `dotnet test`, `docs validate`, `docs drift`, and skill gates.

---

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Which backfill targets and canonical destinations are required to close the audit findings? | Backfill the 6 audited targets into their designated canonical locations: (1) #192 quarantine/problems paging, metadata columns, Copilot attribution -> `docs/dash/architecture.md`, `docs/dash/runbook.md`; (2) #180 tool extraction storage 64KiB bounds, structured status, unclipped inspection distinction -> `docs/dash/architecture.md`, `docs/dash/telemetry-inventory.md`, `docs/dash/runbook.md`; (3) #183/#185/#187 measured run payload schema with turns and scorecard, cache-ratio formulas, measured-input residual with reported_input, run scorecard derivation -> `docs/dash/architecture.md`; (4) #184 turn numbering conventions (0-based transport, 1-based display, strict resolver, empty vs 404 state, ADR 0014 reference) -> `docs/dash/architecture.md`, `docs/dash/runbook.md`; (5) #232 file-synth turn dedupe (reader/synth single-turn grouping, derived safeguard in dedupeTwinTurns, distinct-part fusion, checkpoint re-synthesis) -> `docs/dash/architecture.md`; (6) #195 Antigravity OTLP attribution (gen_ai.agent.name fingerprint, Gemini adapter yield, distinction from antigravity-cli statusline, legacy repair via renormalize --source agy) -> `docs/dash/architecture.md`, `docs/dash/telemetry-inventory.md`. | Verified from current code: `dash/src/server/routes.ts`, `dash/src/synth/synth.ts`, `dash/src/canon/twin-dedupe.ts`, `dash/src/canon/store.ts`. Reconciles store version prose (v17) and Sessions runbook twin fold simultaneously. No mitigation needed as current code is source of truth. | Answered / User request ("Context already decided"). Approved by Hal 2026-10-05. |
| D2 | How should false historical closeout claims in archived plans be corrected? | Correct and annotate the historical closeout summary in `docs/archive/plans/2026-09-30-kyberdash-quarantine-problems-pagination.md` to truthfully disclose that canonical documentation updates did not land at merge commit `5974864`, and were backfilled under Issue #279. | Explicitly permitted and required by prompt context. Does not falsify Git history; preserves historical accuracy by explaining the gap and the backfill. | Answered / User request ("Context already decided"). Approved by Hal 2026-10-05. |
| D3 | What is the permanent plan decision records contract for new plans going forward? | Every plan must contain a permanent `## Decisions` table with stable IDs, Question, Answer/decision, Mitigating and supporting information, and Status/approval provenance. The table survives Draft -> Ready -> execution -> closeout -> archive. | Removes the instruction in `products/kyber-squad/agents/architect/references/plan-authoring.md` and `.github/agents/architect/references/plan-authoring.md` that deleted the Draft ledger on FINALIZE. Conductor PLAN_READY and FINALIZE verify all material choices are resolved with complete columns. `NO_QUESTIONS` does not bypass the permanent section. | Answered / User request ("Context already decided"). Approved by Hal 2026-10-05. |
| D4 | How should closeout distinguish ADR waivers from documentation waivers? | Establish explicit conventions in `docs-dev` (`products/kyber-squad/agents/docs-dev.md` and `.github/agents/docs-dev.md`) and plan inventory policy (`docs/plans/README.md`): ADR waiver means the choice does not warrant a new ADR; documentation waiver explicitly justifies why an item is purely historical/ephemeral. "No ADR" and "no existing doc describes it" are prohibited as documentation waivers. | Review lens `intent-alignment` is updated to verify that claimed canonical doc updates landed in the PR diff, and that every durable decision has either a canonical section destination or an explicit, valid documentation waiver. | Answered / User request ("Context already decided"). Approved by Hal 2026-10-05. |
| D5 | How should the #250 production-scale verification limitation be represented? | Preserved verbatim in spirit in both the inventory and plan notes: this repair does NOT satisfy the live owner gate on the real production store. | Conforms to honest unobservability. Production validation on live `canon.db` remains an open owner gate. | Answered / User request ("Context already decided"). Approved by Hal 2026-10-05. |
| D6 | Which development mode applies to this plan? | `development-mode: standard`. | Changes are technical documentation, planning references, review lens instructions, and inventory links. No application runtime or DAL code is altered. All verification is performed through deterministic build, test, docs validate, docs drift, and skill validation gates. | Answered / Architect. Approved by Hal 2026-10-05. |

---

## Investigation Findings

1. **Missing harvests in canonical docs**:
   - `docs/dash/architecture.md`: The Store section lists schema version as 14, while `dash/src/canon/store.ts` is at `SCHEMA_VERSION = 17`. Schemas 15 (nullable finding waste), 16 (`history_weeks` in `refresh_run`), and 17 (metadata columns in `quarantine` and `problems`) are unmentioned.
   - The REST API table in `docs/dash/architecture.md` lists `limit`-only responses for `/api/kyber/quarantine` and `/api/kyber/problems`, while `dash/src/server/routes.ts:538-575` implements `page`/`offset` paging returning `{ entries/problems, data, total, page, limit }`.
   - Tool extraction contracts from #180 (`MAX_TOOL_RESULT_BYTES` = 64KB, `parts` truncation flag and original byte length in `gen_ai.tool.result_bytes`, structured status checks, deferred subagent tool correlation) are implemented in `dash/src/synth/synth.ts` but omitted from `docs/dash/architecture.md`.
   - Run/session/context payload contracts from #183/#185/#187 (`turns[]` and `scorecard` in `/api/kyber/run/:id`, `cache_hit_ratio` and `cache_creation_coverage` in session summary, `reported_input` residual derivation) are implemented in `dash/src/server/routes.ts` and `dash/src/canon/sessions.ts` but omitted from `docs/dash/architecture.md`.
   - Turn inspection numbering contracts from #184 (0-based transport, 1-based display, strict resolver returning 404, empty content state vs 404) are implemented in `dash/src/server/bridge.ts` and UI but omitted from `docs/dash/architecture.md` and `docs/dash/runbook.md`.
   - File-synthesized turn deduplication from #232 (reader/synth single-turn grouping for Claude Desktop request/response pairs, derived `dedupeTwinTurns` fallback for `otels.length === 0`, parts fusion, parser contract version 3) is implemented in `dash/src/synth/readers/claude.ts` and `dash/src/canon/twin-dedupe.ts` but omitted from `docs/dash/architecture.md`.
   - Antigravity OTLP attribution from #195 (`gen_ai.agent.name === 'antigravity'` fingerprint adapter, Gemini yield, scoped renormalize repair, distinction from `antigravity-cli` statusline) is implemented but `docs/dash/telemetry-inventory.md` still contained a blanket statement that legacy Gemini records are not migrated without disclosing the evidence-based scoped repair.
2. **Stale Sessions guidance**:
   - `docs/dash/runbook.md:408` stated that Claude Desktop and Claude Code stay separate, contradictory to the #182 twin fold in `docs/dash/architecture.md:525`.
3. **Traceability loss in inventory**:
   - In `docs/plans/README.md`, the entry for #225 (line 46) cited a PR branch instead of canonical architecture/runbook/rationale pages.
4. **Instruction conflict causing decision loss**:
   - `products/kyber-squad/agents/architect/references/plan-authoring.md:58` instructed removing the Draft ledger on finalization without preserving a permanent question/answer/mitigating-information table.
   - `products/kyber-squad/agents/conductor/references/plan-path.md:18` did not verify the presence or contents of the permanent decision record.
   - `products/kyber-squad/agents/docs-dev.md:51` did not require a decision-to-canonical mapping table or distinguish ADR waivers from documentation waivers.
   - `products/kyber-squad/skills/code-review/references/lenses/intent-alignment.md` did not instruct reviewers to verify decision harvesting or claimed canonical doc updates upon archival.
   - `docs/docgraph/governance.md:67` did not clarify that `KW-DOC-LIFECYCLE-003` is a mechanical location check rather than semantic proof of decision harvesting.

---

## Verification Contract (development-mode: standard)

| Step | Verification Gate | Command | Expected Result |
|---|---|---|---|
| V1 | Solution build | `/Users/hal/.dotnet/dotnet build KyberWeave.sln -c Release` | 0 Warning(s), 0 Error(s) (TreatWarningsAsErrors) |
| V2 | .NET unit tests | `/Users/hal/.dotnet/dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build` | 2550 passed, 9 pre-existing Mcp runner failures disclosed |
| V3 | Squad canonical tests | `/Users/hal/.dotnet/dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter FullyQualifiedName~SquadCanonicalContentTests` | 19 passed |
| V4 | Skill validation | `/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-weave-docs` | 0 findings |
| V5 | Skill lint | `/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill lint .apm/skills/kyber-weave-docs --min-desc-score 70` | 0 findings |
| V6 | Skill scan | `/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill scan .apm/skills/kyber-weave-docs --fail-on critical` | 0 findings |
| V7 | Docs validate | `/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready` | 0 findings |
| V8 | Docs drift | `/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | 0 findings |

---

## Dispatchable Tasks

### Task 1: Backfill Canonical Documentation in KyberDash Architecture
- **Objective:** Harvest contracts for #192, #180, #183/#185/#187, #184, #232, #195 and reconcile schema version prose.
- **Exact files:** `docs/dash/architecture.md`.
- **Acceptance criteria:**
  - Store section updated to schema version 17, explaining schemas 15, 16, and 17.
  - REST API table updated: `/api/kyber/quarantine` and `/api/kyber/problems` document `data`, `total`, `page`, `limit` and query parameters (`limit`, `offset`, `page`); `/api/kyber/run/:id` documents `turns` and `scorecard`; `/api/kyber/session/:id/turn/:index/content` documents 0-based turn index.
  - Tool extraction contracts documented: 64KiB result truncation, structured status, no fabricated offered tools, deferred subagents, ADR 0014 unclipped distinction.
  - Run/session payload contracts documented: cache-ratio formulas, measured-input residual with `reported_input`, run scorecard derivation.
  - Turn inspector numbering documented: 0-based transport, 1-based display, strict resolver, empty state vs 404.
  - File-synthesized turn dedupe documented: reader/synth single-turn grouping, derived safeguard, distinct-part fusion, contract version 3, distinction from ADR 0009 D4.
  - Antigravity OTLP attribution documented: `gen_ai.agent.name === 'antigravity'` fingerprint, Gemini yield, distinction from `antigravity-cli`.
- **Dependencies:** None.

### Task 2: Reconcile Runbook and Telemetry Inventory
- **Objective:** Update runbook and telemetry inventory with verified contracts and reconcile stale passages.
- **Exact files:** `docs/dash/runbook.md`, `docs/dash/telemetry-inventory.md`.
- **Acceptance criteria:**
  - `docs/dash/runbook.md`: Reconcile Sessions harness tabs to reflect the #182 Claude twin fold; document pagination for quarantine/problems; document 0-based turn inspection; document tool result truncation and re-synthesis.
  - `docs/dash/telemetry-inventory.md`: Update Gemini statusline / Antigravity row to explain scoped repair and attribution; document per-harness tool extraction support and deferred harness issues (#210–#215).
- **Dependencies:** Task 1.

### Task 3: Correct Historical Archived Plan Closeout and Update Plan Inventory
- **Objective:** Correct false closeout claim in #192 plan, update plan inventory links for audited plans, and clarify inventory policy.
- **Exact files:** `docs/archive/plans/2026-09-30-kyberdash-quarantine-problems-pagination.md`, `docs/plans/README.md`.
- **Acceptance criteria:**
  - `2026-09-30-kyberdash-quarantine-problems-pagination.md`: Annotate Completion Summary to state canonical docs were not updated at merge commit `5974864` and were backfilled under #279.
  - `docs/plans/README.md`: Update policy prose regarding permanent decision records, decision-to-canonical mapping, and ADR vs documentation waivers. Update rows for #192, #180, #183/#185/#187, #184, #232, #195 to link canonical destinations; fix #225 destination cell; preserve #250 limitation caveat.
- **Dependencies:** Task 1, Task 2.

### Task 4: Establish Permanent Decision Records Policy in Planning & Review Instructions
- **Objective:** Update authoring, conductor, docs-dev, and code-review instructions.
- **Exact files:**
  - `products/kyber-squad/agents/architect/references/plan-authoring.md` and `.github/agents/architect/references/plan-authoring.md`
  - `products/kyber-squad/agents/conductor/references/plan-path.md` and `.github/agents/conductor/references/plan-path.md`
  - `products/kyber-squad/agents/docs-dev.md` and `.github/agents/docs-dev.agent.md`
  - `products/kyber-squad/skills/code-review/references/lenses/intent-alignment.md` and `.github/skills/code-review/references/lenses/intent-alignment.md`
  - `docs/docgraph/governance.md`
- **Acceptance criteria:**
  - `plan-authoring.md`: Permanent `## Decisions` table required with 5 columns; instruction to delete Draft ledger removed; `NO_QUESTIONS` does not bypass table.
  - `plan-path.md`: Conductor PLAN_READY and PLAN_FINALIZED verify the permanent decision table.
  - `docs-dev.md`: Closeout requires decision-to-canonical mapping table; defines and distinguishes ADR waivers from documentation waivers.
  - `intent-alignment.md`: Reviews verify decision harvesting and claimed canonical doc updates upon archival.
  - `governance.md`: Clarify that `KW-DOC-LIFECYCLE-003` is a mechanical lifecycle gate and does not prove semantic harvesting occurred.
- **Dependencies:** None.

### Task 5: Gate Suite Verification and Plan Archival
- **Objective:** Run all verification gates, finalize plan, archive plan, and prepare pull request.
- **Exact files:** `docs/plans/2026-10-05-harvest-plan-decision-records.md`, `docs/archive/plans/2026-10-05-harvest-plan-decision-records.md`, `docs/plans/README.md`.
- **Acceptance criteria:**
  - `dotnet build` passes with 0 warnings/errors.
  - `dotnet test` passes (9 known Mcp runner failures disclosed).
  - `docs validate --merge-ready` and `docs drift` pass with 0 findings.
  - Plan moved to `docs/archive/plans/2026-10-05-harvest-plan-decision-records.md` and listed in archived register of `docs/plans/README.md`.
- **Dependencies:** Tasks 1–4.

---

## Risks, Out-of-Scope Boundaries, & Verification Gates

- **Risks**: Modifying instruction files in `products/kyber-squad/` and `.github/` could drift if not kept in sync. Both locations were updated consistently and verified via `SquadCanonicalContentTests`.
- **Out of Scope**:
  - Fixing known-failing tests: `dash/src/refresh/migration.test.ts` (DROP COLUMN), `cursor.test.ts` six-month-cap DATE-ROT, and 9 Mcp external-runner + 3 lease-contention failures. Disclose only.
  - Satisfying the #250 live owner gate on production `canon.db`. This repair does NOT satisfy any live gate.
  - Implementing deferred tool extraction for Codex, Copilot, Cursor, etc. (filed as #210–#215).
- **Verification Gates**: Build, tests, docs validate, docs drift, skill gates.

---

## Closeout Mapping

| Decision ID | Canonical Document Section / Status | Notes / Waiver |
|---|---|---|
| D1 | `docs/dash/architecture.md` (§Store, §API Contract, §Synthesis, §Twin Dedupe), `docs/dash/runbook.md`, `docs/dash/telemetry-inventory.md` | Harvested into canonical architecture, runbook, and inventory. |
| D2 | `docs/archive/plans/2026-09-30-kyberdash-quarantine-problems-pagination.md` (§Completion Summary) | Corrected historical closeout claim in archived plan. |
| D3 | `products/kyber-squad/agents/architect/references/plan-authoring.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, `docs/plans/README.md` | Instruction and policy contract established. |
| D4 | `products/kyber-squad/agents/docs-dev.md`, `products/kyber-squad/skills/code-review/references/lenses/intent-alignment.md`, `docs/plans/README.md` | Closeout traceability and waiver standards established. |
| D5 | `docs/plans/README.md` (Row #250 / §Archived Plans), `docs/archive/plans/2026-10-02-otlp-projection-debounce.md` | Preserved limitation verbatim in spirit. Documentation waiver: caveat remains in plan and inventory. |
| D6 | `docs/archive/plans/2026-10-05-harvest-plan-decision-records.md` frontmatter | Historical execution mode choice; documentation waiver: applies to this plan execution only. |

---

## Completion Summary

Completed on 2026-10-05.

All acceptance criteria satisfied:
- Backfilled canonical documentation in `docs/dash/architecture.md`, `docs/dash/runbook.md`, and `docs/dash/telemetry-inventory.md` covering #192, #180, #183/#185/#187, #184, #232, and #195. Reconciled schema version prose to v17 and Sessions runbook twin fold.
- Annotated historical closeout in `docs/archive/plans/2026-09-30-kyberdash-quarantine-problems-pagination.md` explaining the backfill and recording ADR waiver.
- Updated `docs/plans/README.md` policy preamble and archived table rows linking canonical destinations; preserved #250 owner verification limitation.
- Updated planning, conductor, docs-dev, and code-review instructions across `products/kyber-squad/` and `.github/` to enforce permanent 5-column `## Decisions` tables, prohibit deletion on finalization, and distinguish ADR waivers from documentation waivers.
- Clarified mechanical role of `KW-DOC-LIFECYCLE-003` in `docs/docgraph/governance.md`.
- Verified repository gates: `dotnet build` passed (0 warnings, 0 errors), `dotnet test` passed (with disclosed pre-existing Mcp runner failures), `SquadCanonicalContentTests` passed (19/19), skill validate/lint/scan passed (0 findings), `docs validate --merge-ready` and `docs drift` passed (0 findings).
- Archived per `KW-DOC-LIFECYCLE-003`.
