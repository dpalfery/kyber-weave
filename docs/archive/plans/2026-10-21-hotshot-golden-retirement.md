---
id: plans/2026-10-21-hotshot-golden-retirement
title: "Retire the Hotshot Golden Contract Tests and Fixture"
doc-type: plan
status: complete
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-21
development-mode: test-first
---

# Retire the Hotshot Golden Contract Tests and Fixture

**Status: Ready 2026-10-21 — approved and execution authorized.** Authored 2026-10-21 under
`autonomy: autonomous`. Approval recorded as `autonomous: recommendation accepted` (2026-10-21,
`AUTONOMOUS_APPROVAL` for the `PLAN_READY` gate). No open decisions —
the request was already decision-complete: the authoritative tests (`SquadCanonicalContentTests`,
`CopilotRendererTests`, `SquadPackAndReleaseTests`, `SquadResourceClosureTests`) determine the
keep/drop and migration outcomes, and the remaining choices are test-local or doc-local and cheap
to reverse. The rationale for each decided row is in §4. Frontmatter `status` uses the
ontology's closed vocabulary (`current`); the Ready lifecycle state is carried here and in the
plan index.

**Development mode:** `test-first` (the default; no opt-out supplied).
**Autonomy:** `autonomous`.

---

## 1. Problem and goal

### 1.1 Request

Retire the Hotshot golden contract tests and their checked-in fixture. The golden contract pinned
Kyber-Squad's canonical source to the historical Hotshot Logistics Copilot tree (SHA-pinned
`tests/KyberWeave.Tests/Fixtures/kyber-squad-hotshot-golden.json`). Kyber-Squad is now fully
canonical: the roster, identities, delegation, migration provenance, tool ordering, render shape,
and package retention are each pinned by their own owning test class, so the blanket golden net is
redundant maintenance — every operator-approved evolution since has had to thread an allow-list
(`EvolvedAgentIdentities`, `ModelEvolvedAgentIdentities`, `EvolvedSkillIdentities`) through it.

### 1.2 Goal

Delete the golden contract test class and fixture, rehome the one assertion with no other owner,
drop the fixture dependency from the one lifecycle test that reads it, keep the migration
provenance (it is pinned by `SquadCanonicalContentTests`, not by the golden contract), and reword
every Hotshot/golden mention in current documentation — without losing any coverage that another
owning class does not already provide.

### 1.3 Scope (authorized)

- Read `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs` and decide keep vs drop per assertion.
- Delete `HotshotGoldenContractTests.cs` and `tests/KyberWeave.Tests/Fixtures/kyber-squad-hotshot-golden.json`,
  plus any other test/fixture/helper/csproj entry or docs reference existing only to serve them.
- Handle `products/kyber-squad/migration/*.md` Hotshot `source-commit`/source hashes: keep
  `final-body-sha256`; remove provenance fields/prose only if no test/loader depends on them,
  otherwise stop and report rather than change the loader/test.
- Reword every mention of golden contract or Hotshot sync in `docs/` (non-archive), `AGENTS.md`,
  `REVIEW.md`, and `products/kyber-squad/README.md`. Do not edit `docs/archive/`.
- Keep squad content, renderer contract, and pack tests green.

---

## 2. Investigation findings

Discovery: `codegraph 0, docs 0, direct reads 16`. No `.codegraph/` index exists in this checkout
and no CodeGraph MCP tool is exposed in this harness; `codegraph init .` is a mutating step that
would also rewrite the generated CodeGraph block in `AGENTS.md`, so it was declined during
read-only planning. The Kyber-Weave docs MCP tools (`docs_explore`, `docs_for_symbol`,
`docs_glossary`, `docs_status`) are not exposed and no `docs_status` CLI is on `PATH`; the
documentation-index fallback was used as AGENTS.md permits. Direct reads were the narrow fallback
needed to enumerate the deletion/reword surface; no `src/` symbol that a code index would cover was
read directly.

- **The fixture is shared, not golden-only.** `kyber-squad-hotshot-golden.json` is read by two test
  classes: `HotshotGoldenContractTests.ReadManifest()` (full manifest) and
  `SquadHotshotLifecycleTests.ReadGoldenPaths()` (the `agents[].path` + `skills[].path` union, used
  as a 48-entry render list to exercise roster-shrink mechanics). Deleting the fixture therefore
  requires reworking `SquadHotshotLifecycleTests` first; it is a lifecycle test, not golden
  machinery, and must stay green.
- **Two other "Hotshot"-named test classes are not fixture-dependent.**
  `SquadHotshotLifecycleCopilotGlobalLinksTests` and `SquadHotshotLifecycleCopilotGlobalUpdateLinksTests`
  test global-scope resource-link contracts through the real renderer and read no fixture and no
  golden semantics; only their class names carry the word. Left unchanged (§4 D7).
- **No csproj/CI/script reference.** The fixture is loaded from the source tree via
  `KyberWeaveTestPaths.ToolRoot`; no `<Content>`/`CopyToOutputDirectory` entry and no workflow or
  script names the file.
- **Migration provenance is pinned by `SquadCanonicalContentTests`, not by the golden contract.**
  `LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest` asserts `schema`, `agent`,
  `source-commit` (`677c3a876…`), `selected-baseline`, `sources` (against the inline
  `ExpectedSourceHashManifest`), and `final-body-sha256` (`== agent.BodyDigest`) for all 19 migrated
  agents; `LoadNetNewAgentsCarryNoFabricatedMigrationRecord` and
  `LoadUnifiedOrchestrationStackContainsNoVersionedIdentityOrAlias` check migration-file
  presence/absence. No production loader parses the migration frontmatter (only `SquadPacker`
  excludes `migration/` from packaging). Per the scope's "stop and report" clause, these fields
  stay and no test/loader changes (§4 D1).
- **Only one assertion has no other owner.** `CodeReviewSkillRetainsSelfReviewTriggerAndGate` is the
  sole golden-test assertion not already pinned elsewhere (§3). Everything else is duplicated by
  `SquadCanonicalContentTests` (roster, identities, collisions, delegation, retired-identity
  absence, migration digests), `CopilotRendererTests` (deterministic tool ordering
  `CopilotToolCatalog.OrderedTools`, membership, render shape), and
  `SquadPackAndReleaseTests`/`SquadResourceClosureTests` (package retention, resource closure).
- **Docs surface.** Hotshot/golden wording in current (non-archive) docs is confined to nine files:
  `docs/adr/0017-copilot-deterministic-tool-order.md`, `docs/adr/0025-devin-native-agents-and-skill-lowering.md`,
  `docs/kyber-squad/requirements.md`, `docs/kyber-squad/architecture.md`, `docs/kyber-squad/README.md`,
  `docs/kyber-squad/onboarding.md`, `docs/distribution.md`, `docs/context-hygiene/skills.md`, and
  `products/kyber-squad/README.md`. `AGENTS.md` and `REVIEW.md` contain no such wording.
  `docs/plans/README.md` carries two historical mentions in the **Archived Plans** table (§4 D5).

---

## 3. Assertions audit — keep vs drop

All six test methods of `HotshotGoldenContractTests` and their disposition:

| Test method | Disposition | Rationale / rehome |
|---|---|---|
| `CheckedInManifestPinsExactHotshotGoldenInventory` | **Drop** | Pins the manifest's schema (`kyber-squad.hotshot-golden/v1`), `source_commit`, manifest SHA-256, approved tool order, collision identities, counts (24/24/64/88/48), resource-path validity, task-reviewer model, conductor delegation, and per-entry SHA-256 format. This is the golden pin itself — retired with the fixture. |
| `CodeReviewSkillRetainsSelfReviewTriggerAndGate` | **Keep — move** | Pins the `code-review` skill's pre-push self-review trigger ("self-review gate before pushing") and gate (`## Pre-push self-review gate`, `major-or-worse`). Not covered anywhere else. **Move verbatim to `SquadCanonicalContentTests` as a new `[Fact]`, rewritten without any golden/snapshot reference.** |
| `CanonicalSourcePreservesGoldenContractOutsideReviewedEvolution` | **Drop** | Golden-vs-canonical byte/SHA/field comparison is retired. The roster, bundle, collision, retired-identity-absence, and delegation invariants it also asserts are already pinned by `SquadCanonicalContentTests` (`LoadFullProductTreeContainsExactlyApprovedAgentIdentities`, `LoadAgentSkillIntersectionHasPinnedSharedAndPrefixedFallbackIdentities`, `LoadUnifiedOrchestrationStackContainsNoVersionedIdentityOrAlias`, `LoadUnifiedOrchestrationStackDeclaresCanonicalDelegationAndModelProfiles`). |
| `RecursivePackagesRetainEveryCanonicalSkillResourceAndResolveLocalReferences` | **Drop** | APM/plugin resource retention and local-reference resolution are already pinned by `SquadPackAndReleaseTests` and `SquadResourceClosureTests` against the canonical `skills/` directory; the golden `CanonicalResources` (64) driver is retired. |
| `CopilotRenderMatchesCheckedInHotshotGoldenContract` | **Drop** | Golden-byte render comparison is retired. Render shape, membership, and deterministic ordering are already pinned by `CopilotRendererTests` (`CopilotToolCatalogHasExactApprovedOrder`, `RenderAsync_EmitsDeterministicCanonicalToolOrdering`, membership checks). |
| `ExternalHotshotGoldenTreeMatchesManifestAndCopilotRender` | **Drop** | The opt-in external parity harness (`KYBER_SQUAD_HOTSHOT_GOLDEN_ROOT`) is retired with the fixture. |

Net keep: exactly one block → `CodeReviewSkillRetainsSelfReviewTriggerAndGate` to
`SquadCanonicalContentTests`. All helper members (`ReadManifest`, `CompareFrontmatter`,
`CollapseGranularCopilotTool`, `Sha256`, the golden record types, and the allow-lists) retire with
the class.

---

## 4. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Are the migration `source-commit`/source-hash fields still needed? | **Keep all migration frontmatter fields and prose; make no change to `products/kyber-squad/migration/*.md` and no change to `SquadCanonicalContentTests` or any loader.** | `SquadCanonicalContentTests.LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest` asserts `schema`, `agent`, `source-commit` (`677c3a876…`), `selected-baseline`, `sources` (vs `ExpectedSourceHashManifest`), and `final-body-sha256` (`== agent.BodyDigest`); `LoadNetNewAgentsCarryNoFabricatedMigrationRecord` and `LoadUnifiedOrchestrationStackContainsNoVersionedIdentityOrAlias` check migration-file presence/absence. No production loader reads these fields. Scope instructs: "if loader/tests depend, stop and report instead of changing loader." | ANSWERED — determined by the test (authoritative) + scope stop-and-report clause. Approved — autonomous: recommendation accepted (2026-10-21). |
| D2 | How does `SquadHotshotLifecycleTests` survive the fixture deletion? | **Rework it to inline a small deterministic path list and drop the golden/`48` wording and the `ReadGoldenPaths()` fixture read.** | The test pins roster-shrink mechanics (remove only an unchanged owned obsolete file, keep an edited file, keep an unmanaged neighbor, write an exact receipt), not golden parity. A 5-entry inline list fully exercises the mechanics; the golden-specific `48` count and "48-file golden render" wording retire with the fixture. Cheap to reverse, local to one test. | ANSWERED — autonomous; test-local rework, no authoritative source required. Approved — autonomous: recommendation accepted (2026-10-21). |
| D3 | Which assertions survive, and where? | **Move only `CodeReviewSkillRetainsSelfReviewTriggerAndGate` into `SquadCanonicalContentTests`; drop the other five methods as already-covered (§3).** | The audit shows every other assertion is duplicated by `SquadCanonicalContentTests`, `CopilotRendererTests`, or `SquadPackAndReleaseTests`/`SquadResourceClosureTests`. Moving the one orphaned assertion before deletion prevents a silent coverage hole (the `code-review` pre-push self-review gate). | ANSWERED — audit of the live test file and its owning classes (§3). Approved — autonomous: recommendation accepted (2026-10-21). |
| D4 | Reword the "Hotshot golden baseline" prose in migration records and the "Hotshot" comments in `SquadCanonicalContentTests`? | **Leave them intact.** | The migration records are per-agent provenance of a real import event, and the frontmatter they describe is still pinned (D1). The `SquadCanonicalContentTests` comments accurately describe that the migration records name a Hotshot source commit. These are not "golden contract test" machinery. | ANSWERED — provenance records; accurate as written. Approved — autonomous: recommendation accepted (2026-10-21). |
| D5 | Reword the historical Hotshot/golden mentions in `docs/plans/README.md`'s Archived Plans table (rows for issue-249 and the hotshot-golden-sync plan)? | **Leave them intact.** | They are historical goal summaries of already-archived plans, equivalent in kind to `docs/archive/` content the request says not to edit. Rewriting them would falsify the archive index. Cheap to reverse if the user wants them reworded too. | ANSWERED — autonomous; historical archive index, "do not edit archive" spirit. Approved — autonomous: recommendation accepted (2026-10-21). |
| D6 | How deep is the ADR 0017/0025 rewording? | **Preserve the accepted decisions; remove only the Hotshot-golden provenance framing and the deleted-test code reference.** | ADR 0017's decision (deterministic global tool order via `CopilotToolCatalog.OrderedTools`) and ADR 0025's decision (Devin native agents) remain valid; only the "Hotshot golden" framing, the `PostGoldenToolAdditions in the golden contract tests` code reference (deleted), and the golden-membership consequence wording change. | ANSWERED — reword provenance, keep decisions. Approved — autonomous: recommendation accepted (2026-10-21). |
| D7 | Rename `SquadHotshotLifecycle*` classes that only carry "Hotshot" in the name? | **Leave names unchanged.** | The two `*CopilotGlobal*LinksTests` classes read no fixture and test global resource-link contracts; renaming would churn unrelated references in archived plans for no coverage gain. Out of scope. | ANSWERED — out of scope; cosmetic. Approved — autonomous: recommendation accepted (2026-10-21). |
| D8 | Baseline mode and autonomy. | **`development-mode: test-first` (default, no opt-out); `autonomy: autonomous`.** | Recorded per plan-authoring contract; no mode opt-out was supplied. | ANSWERED — packet fields. Approved — autonomous: recommendation accepted (2026-10-21). |

No open decisions; no impactful escalation (§8).

---

## 5. Test contract (test-first)

Behavior-changing tasks are test-local refactors that preserve existing invariants (guard
preservation), so RED-first does not apply in the usual "missing behavior" sense; each row below
names the exact surviving contract test and its green acceptance, or marks a genuinely no-test task
with its read-only verification.

| Task | Test project / file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | — (read-only) | — | The keep/drop table in §3 matches the live `HotshotGoldenContractTests.cs`. | n/a — read-only audit. | Audit recorded with no delta (or a decision amendment if source differs). |
| T2 | `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `tests/KyberWeave.Tests/SquadHotshotLifecycleTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SquadCanonicalContentTests\|FullyQualifiedName~SquadHotshotLifecycle"` | `CodeReviewSkillRetainsSelfReviewTriggerAndGate` lives in `SquadCanonicalContentTests` and asserts the three conditions; `SquadHotshotLifecycleTests` passes with an inline path list and no fixture read. | n/a — guard preservation (behavior already exists); the moved test must pass before deletion removes the source. | Both classes pass; the moved assertion is byte-for-byte equivalent to the removed one. |
| T3 | — (read-only) | grep for `HotshotGoldenContractTests` and `kyber-squad-hotshot-golden` | Deletion leaves no code reference to the removed class or fixture. | n/a — deletion. | No code/CI/csproj reference remains; full suite passes. |
| T4 | `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` | `dotnet test … --filter "FullyQualifiedName~LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest"` | Migration provenance still asserted (D1). | n/a — read-only verification. | Test passes and still asserts all six frontmatter fields; report records keep-all. |
| T5 | — (read-only) | grep for `Hotshot\|golden` in the nine files (§2) | Wording is reworded to neutral/canonical phrasing; no golden-contract claim survives. | n/a — documentation. | `docs validate .` and `docs drift .` clean; only intended neutral wording remains. |
| T6 | full project | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --artifacts-path .agent_scratchpad/<task>/artifacts` | Squad content, renderer, and pack tests all green with counts. | n/a — gate. | Full suite passes with no count regression; format + both docs checks clean. |

---

## 6. Tasks

Skills are named (not owners); the conductor maps them to the live inventory. Each implementation
task that edits C# tests must first call the `agent_coding_standards` MCP tool (C#) in the
implementation phase.

- **T1 — Assertions audit (read-only).** `test-dev`. Confirm §3's keep/drop mapping against the live
  `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs`; record any delta as a decision amendment.
  Files: none (read-only). Acceptance: audit confirmed. Depends: none.

- **T2 — Rehome the orphaned assertion and drop the fixture dependency.** `test-dev`.
  (1) Move `CodeReviewSkillRetainsSelfReviewTriggerAndGate` verbatim into
  `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` as a new `[Fact]`, with no golden/snapshot
  reference. (2) Rework `tests/KyberWeave.Tests/SquadHotshotLifecycleTests.cs` to inline a small
  deterministic path list, remove `ReadGoldenPaths()` and the fixture path, and reword the
  golden/`48` doc comments. Acceptance: both classes pass (Test contract T2). Depends: T1.

- **T3 — Delete the golden contract test and fixture.** `test-dev`. Delete
  `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs` and
  `tests/KyberWeave.Tests/Fixtures/kyber-squad-hotshot-golden.json`. Verify no remaining reference
  (grep for `HotshotGoldenContractTests` and `kyber-squad-hotshot-golden`). Acceptance: no code/CI
  reference; full suite green (Test contract T3). Depends: T2.

- **T4 — Migration frontmatter decision (read-only, stop-and-report).** `test-dev`. Verify
  `SquadCanonicalContentTests.LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest`
  pins `schema`, `agent`, `source-commit`, `selected-baseline`, `sources`, `final-body-sha256`; run
  the focused filter; record D1's keep-all outcome. No file edits. Acceptance: verification
  recorded (Test contract T4). Depends: none.

- **T5 — Reword Hotshot/golden references in current docs.** `app-docs-standard` (+ `kyber-weave-docs`
  for any frontmatter touched; none expected). Reword in: `docs/adr/0017-copilot-deterministic-tool-order.md`,
  `docs/adr/0025-devin-native-agents-and-skill-lowering.md`, `docs/kyber-squad/requirements.md`
  ("Golden-render and knowledge-retention requirement" → neutral), `docs/kyber-squad/architecture.md`
  (golden-render requirement, "Hotshot-era 48-file", "Skill golden bytes", and the
  `HotshotGoldenContractTests.cs` regression-pin line → `SquadCanonicalContentTests.cs`/
  `CopilotRendererTests.cs`), `docs/kyber-squad/README.md`, `docs/kyber-squad/onboarding.md`,
  `docs/distribution.md`, `docs/context-hygiene/skills.md`, and `products/kyber-squad/README.md`.
  Preserve current facts (121-file Copilot render, 69 resources/94 files, 25 skills, 21 agents,
  evolved-skills list, 18 migration reports) while removing the Hotshot-golden framing. Do not edit
  `docs/archive/`. Acceptance: reworded, both docs checks clean (Test contract T5). Depends: none
  (parallel; the `HotshotGoldenContractTests.cs`-name removal is deterministic).

- **T6 — Verification.** `test-dev` (+ `github-devops` if a release gate is implicated — it is not).
  Run `dotnet format KyberWeave.sln whitespace --verify-no-changes --include <changed .cs files>`;
  build and `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --artifacts-path
  .agent_scratchpad/<task>/artifacts` with counts; `docs validate .` and `docs drift .` clean.
  Acceptance: all gates pass with counts; docs checks clean (Test contract T6). Depends: T2, T3, T5.

---

## 7. Dependency graph

```text
T1 ──▶ T2 ──▶ T3 ──▶ T6
        ▲             ▲
        │             │
T4 ─────┘             │
T5 ───────────────────┘
```

- T1 → T2 (audit mapping consumed).
- T2 → T3 (delete only after the surviving coverage is green).
- T4 independent (read-only, parallel).
- T5 independent (parallel; disjoint file scope from the test edits).
- T6 depends on T2, T3, T5 (T1 and T4 produce no file changes).

---

## 8. Risks and out of scope

**Risks**

- **Silent coverage loss** if a golden assertion is dropped that no owning class actually covers.
  Mitigated by the §3 audit and by T2 moving the one orphan before T3 deletes.
- **Docs reword falsifies a fact** (e.g., dropping the 121-file/69-resource counts). Mitigated by
  T5's explicit "preserve current facts" acceptance.
- **`docs drift` skips** when no CodeGraph index exists in this checkout; recorded as skipped, not
  passed, per AGENTS.md.

**Out of scope**

- `docs/archive/` (including the archived hotshot-golden-sync plan and its index rows).
- Renaming `SquadHotshotLifecycle*` classes (D7).
- Any change to `products/kyber-squad/migration/*.md`, `SquadCanonicalContentTests`, or a loader (D1).
- Production `src/` behavior — this is a test-retirement and documentation change only.

**Escalation.** None. Every open decision resolved to an answer with an authoritative source (D1,
D3) or is cheap to reverse and local to one test/document (D2, D4–D7). Nothing touches a data model,
public interface, dependency choice, or cross-cutting architecture.

---

## 9. Verification gates, review, and closeout

**Gates (T6):** scoped `dotnet format … whitespace --verify-no-changes`; build; full test project
with counts; `docs validate .` and `docs drift .` clean. `--artifacts-path .agent_scratchpad/<task>/artifacts`
on build/test.

**Review:** normal per-task `task-reviewer`; a full `code-review` council is not required for a
deletion + documentation change, but the PR that archives this plan runs
`docs validate . --merge-ready` (KW-DOC-LIFECYCLE-003).

**Closeout (`docs-dev`):** no durable architectural decision is introduced — the deterministic
tool-order contract (ADR 0017), the canonical roster (SquadCanonicalContentTests), and the migration
provenance all predate this plan and remain. Closeout records the deletion and doc rewording;
`no ADR — ADR waiver: retirement of a redundant test net, no new invariant, boundary, dependency,
or schema`. No documentation waiver is needed: the canonical facts (counts, roster, migration
provenance) are already documented and remain.

---

## 10. Closeout (complete)

The change shipped and this plan is archived by its finishing change per KW-DOC-LIFECYCLE-003.
The permanent `## Decisions` table above (§4, D1–D8) is preserved intact.

**ADR waiver:** `no ADR — ADR waiver: retirement of redundant test net, no new invariant, boundary, dependency, or schema`.
No durable decision introduced: the deterministic tool-order contract (ADR 0017), the canonical
roster (`SquadCanonicalContentTests`), and the migration provenance all predate this plan and
remain. D6 rewords ADR 0017/0025 provenance only; D1/D4/D5/D7 explicitly keep existing behavior
and names.

**Documentation waiver:** none needed. The canonical facts (121-file Copilot render, 69
resources/94 files, 25 skills, 21 agents, 18 migration reports, deterministic tool order,
migration provenance) are already documented in `docs/kyber-squad/`, `docs/distribution.md`,
`docs/context-hygiene/skills.md`, and `products/kyber-squad/README.md`; T5 reworded them to
neutral/canonical phrasing without changing facts.

Closeout mapping (durable content → canonical home; nothing new harvested):

| Decision(s) | Disposition | Canonical destination |
|---|---|---|
| D1 (keep all migration frontmatter/prose) | No migration — provenance already pinned by `SquadCanonicalContentTests.LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest` | Already documented; no doc change (intentional per scope stop-and-report clause) |
| D2 (inline path list in `SquadHotshotLifecycleTests`) | Test-local rework, cheap to reverse | No canonical doc target — code change only; deletion recorded here |
| D3 (move `CodeReviewSkillRetainsSelfReviewTriggerAndGate` to `SquadCanonicalContentTests`) | Coverage preserved, one block moved verbatim | No canonical doc target — code change only; deletion recorded here |
| D4 (leave migration-record prose + test comments intact) | Accurate as written | No change |
| D5 (leave archived-plan index rows intact) | Historical index, `docs/archive/` spirit | No change |
| D6 (reword ADR 0017/0025 provenance, keep decisions) | Decisions unchanged | `docs/adr/0017-copilot-deterministic-tool-order.md`, `docs/adr/0025-devin-native-agents-and-skill-lowering.md` (reworded in place by T5) |
| D7 (leave `SquadHotshotLifecycle*` names) | Out of scope, cosmetic | No change |
| D8 (test-first, autonomous) | Process record | This plan only |
| Deletion + rewording as a whole | Retired test net | Recorded here; `docs/plans/README.md` archive row carries the harvest note |

Completion summary: T1 (audit confirmed §3 mapping, no delta), T2 (orphaned assertion moved
verbatim to `SquadCanonicalContentTests`; `SquadHotshotLifecycleTests` inlined, fixture read
removed), T3 (deleted `HotshotGoldenContractTests.cs` + `kyber-squad-hotshot-golden.json`; grep
confirms no code/CI reference), T4 (migration provenance verified pinned, keep-all recorded),
T5 (nine files reworded; `docs/archive/` untouched), T6 (full suite 2921/2921 passed,
`dotnet format` whitespace+style clean, `docs validate .` clean, `docs drift .` PASS with index;
`docs validate . --merge-ready` clean after archive — the pre-archive error was the expected
KW-DOC-LIFECYCLE-003 open-plan error). Council APPROVE, LOW, 18/18 gates (`artifacts/gates.json`),
zero findings (`artifacts/findings.json`); all task audits PASS. No commit/push per user rule.
