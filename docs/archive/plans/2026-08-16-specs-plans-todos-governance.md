---
id: archive/plans/2026-08-16-specs-plans-todos-governance
title: Specs, Plans, and Todos Governance
doc-type: plan
status: archived
component: DocGraph
owner: dpalfery
last-reviewed: 2026-08-16
---

# Specs, Plans, and Todos Governance

**Status:** Archived
**Archive Date:** 2026-08-16
**Date:** 2026-08-16
**Goal:** Give Kyber-Weave's own `docs/` a governed third document category — `todo`, a real closed-vocabulary doc-type — alongside a new `docs/specs/` folder, and document the spec/plan/todo distinction for both adopters (`docs/README.md`) and contributors (`AGENTS.md`).

---

## 1. Problem / Motivation

The Kyber-Squad renderer work (this session, same branch) produced 11 forward-looking
documents under a new `docs/todo/` folder, written ad hoc: `doc-type: requirements` stood in
for a concept the ontology doesn't actually have, and `docs/todo/README.md` conflated two
things — the general process for the todo folder, and Kyber-Squad-specific renderer-coverage
status. Neither `todo` nor a `docs/specs/` folder exists in the governed corpus today, and
nothing documents when a contributor should reach for a spec, a plan, or a todo, or how the
three relate.

## 2. Approved decisions

- **D1:** `todo` becomes a real closed-vocabulary doc-type, not a folder convention layered
  on `requirements`. The closed vocabulary is enforced in code (`DocSpecValidator`,
  `OntologyConfig`, `DocumentLoader.ParseDocType`) as well as described in
  `documentation-ontology.md` — both move together.
- **D2:** `docs/specs/` becomes a real third folder, alongside `docs/plans/` and
  `docs/todo/`, for upfront spec-driven-development work (Kiro spec mode / GitHub Spec Kit
  lineage) — used at a greenfield or large-feature start, when architecture and structure
  still need defining. This is Kyber-Weave's own governance convention for its own `docs/` —
  a different, unrelated system from the Kyber-Squad product's downstream `6-Docs/specs/`
  workflow that consumer repositories get (defined in `products/kyber-squad/agents/product-owner.md`).
  One sentence in `docs/README.md` says so explicitly.
- **D3:** A todo is a reminder of work not done now — a finding, a deferred fix, a declined
  suggestion. An agent that finds such work, or declines a suggestion rather than acting on
  it, adds a todo rather than letting it evaporate. A todo is usually the seed that later
  becomes a spec (greenfield, upfront design needed) or a plan (mature codebase, lightweight,
  single-file, typically architect-authored) once someone picks it up.
- **D4:** `todo` requires `component` in the required-key matrix, matching
  `plan`/`spec`/`requirements`/`runbook`/`architecture` — a todo is scoped to a system area.
- **D5:** `docs/todo/README.md` is rewritten as the generic process page (no `component` —
  matches `docs/plans/README.md`); the renderer-coverage content it currently carries moves
  to its own file, `docs/todo/kyber-squad-renderer-coverage.md`.
- **D6:** The concept (why three categories, when each applies) is documented once, in
  `docs/README.md`, for adopters. `AGENTS.md` states the contributor workflow rule (add a
  todo when you find later work) and points at `docs/todo/README.md` for the mechanics,
  rather than duplicating the concept explanation.

## 2a. Open questions (decision ledger)

None open. All decisions above were resolved directly with the user in conversation before
this plan was written — D1–D2 via an explicit two-question decision point, D3 stated
directly by the user, D4–D6 proposed by the planning agent and approved with the plan.

## 3. Investigation findings

- The closed doc-type vocabulary is enforced in **five** places, not described once — found
  by reading the validator, not the prose:
  - [`src/KyberWeave.Core/Docs/Model/DocumentModel.cs:9`](../../../src/KyberWeave.Core/Docs/Model/DocumentModel.cs) —
    the `DocType` enum itself.
  - [`src/KyberWeave.Core/Docs/Parsing/DocumentLoader.cs:239`](../../../src/KyberWeave.Core/Docs/Parsing/DocumentLoader.cs) —
    `ParseDocType`, the mapping `docs validate`/`docs drift` actually use on real documents.
  - [`src/KyberWeave.Core/Configuration/OntologyConfigLoader.cs`](../../../src/KyberWeave.Core/Configuration/OntologyConfigLoader.cs) —
    `TryParseDocType`, used only for `kyber-weave.yml` per-host overrides, plus a hardcoded
    error-message string listing known types.
  - [`src/KyberWeave.Core/Configuration/OntologyConfig.cs`](../../../src/KyberWeave.Core/Configuration/OntologyConfig.cs) —
    `DefaultDocTypes` (the closed set `DocSpecValidator.IsKnownDocType` checks) and
    `CreateDefaultRequiredKeysByType()` (the required-key matrix; today:
    `Architecture/Onboarding/Requirements/Runbook/Plan/Spec → component`, `Onboarding` adds
    `source-root`).
  - [`docs/documentation-ontology.md`](../../documentation-ontology.md) — the human-readable
    description of both the closed set (line ~64) and the required-key matrix (line ~85).
  - All five must move together, or `docs validate` rejects `doc-type: todo` as unknown
    (`KW-DOC-SPEC-002`) the moment it's used.
- `docs/specs/` does not exist; no `doc-type: spec` document exists anywhere in the corpus
  today, though `spec` has been in the closed vocabulary and required-key matrix
  (`component`) since before this plan.
- `docs/plans/README.md` (`doc-type: index`) carries no `component` field — the pattern this
  plan follows for `docs/todo/README.md` and the new `docs/specs/README.md`.
- No test hardcodes the doc-type list, its count, or the closed-vocabulary error message
  (checked via `grep` across `tests/KyberWeave.Tests/*.cs`) — adding `todo` needs no test
  retrofit beyond what the `docs/todo/*.md` doc-type changes (D1/D5) already touch.
- `DocsCatalogCommand` reads `d.DocType.ToString().ToLowerInvariant()` generically — no
  hardcoded switch, so it picks up `Todo` automatically once the enum has it.
- This plan document itself follows the layout `products/kyber-squad/agents/architect.md`
  defines for plan files, per repository convention — governed frontmatter (`doc-type:
  plan`, `status: draft` per the ontology's own closed status set — distinct from this
  template's own `**Status:**` line, which is the plan's *implementation* lifecycle, not
  ontology-governed) wrapping the architect's own body template. `docs/archive/plans/*.md`
  files carry a `status:` value (`archived`) outside the ontology's closed set because
  `archive` is one of `OntologyConfig.DefaultExcludedSegments` — archived plans are excluded
  from `docs validate` entirely, which is also why this plan will need its frontmatter
  `status` value changed (to a closed-set value, or the file moved to `docs/archive/plans/`)
  once implementation completes and it is archived under this repo's own convention.

## 4. Task list

| # | Phase | Component | Description | Skills |
|---|-------|-----------|-------------|--------|
| 1 | 1 | DocGraph | Add `Todo` to the `DocType` enum (`DocumentModel.cs`), `ParseDocType` (`DocumentLoader.cs`), `TryParseDocType` + its error message (`OntologyConfigLoader.cs`), `DefaultDocTypes` + `CreateDefaultRequiredKeysByType()` (`OntologyConfig.cs`). | dotnet-dev |
| 2 | 1 | DocGraph | Update `docs/documentation-ontology.md`: add `todo` to the closed-set list and a `| todo \| component \|` row to the required-key matrix. | docs-dev |
| 3 | 2 | KyberSquad | Extract the renderer-coverage content from `docs/todo/README.md` into `docs/todo/kyber-squad-renderer-coverage.md` (`doc-type: todo`, `component: KyberSquad`). | docs-dev |
| 4 | 2 | DocGraph | Rewrite `docs/todo/README.md` as the generic todo-folder process page: what a todo is, when to add one (D3), the required shape, an index of current todos. Drop `component`. | docs-dev |
| 5 | 2 | KyberSquad | Retrofit `doc-type: requirements` → `doc-type: todo` across all 13 files under `docs/todo/` (the 9 renderer-target docs, the two gap docs, and the newly extracted renderer-coverage doc). | docs-dev |
| 6 | 2 | DocGraph | Create `docs/specs/README.md` (`doc-type: index`, no `component`) — empty inventory, short description of what belongs here and when. | docs-dev |
| 7 | 3 | DocGraph | Add the specs/plans/todos concept section to `docs/README.md`, after "Start here". | docs-dev |
| 8 | 3 | DocGraph | Add the todo workflow rule (short) to `AGENTS.md`, after "Non-negotiables", pointing at `docs/todo/README.md` and `docs/README.md`. | docs-dev |
| 9 | 4 | DocGraph | Verify: full build, full test suite, `docs validate .`, `docs drift .`, plus a scratch-document check that `doc-type: todo` without `component` fails `KW-DOC-SPEC-003` and with `component` passes. | dotnet-dev |

## 5. Sequencing / dependency graph

Task 1 (code) must land before task 5 (retrofit) — `docs validate` will reject
`doc-type: todo` until the enum/config/validator changes are in. Task 2 (ontology doc) has no
hard code dependency but should land alongside task 1 so the prose and the enforcement never
drift apart, even briefly. Tasks 3–4 (todo README split) are independent of task 1/2 and can
happen in either order relative to them, but task 5 (retrofit) depends on task 1. Task 6
(specs folder) is independent of everything else. Tasks 7–8 (docs/README.md, AGENTS.md) are
independent of each other and of 3–6, but read most naturally after the folders they describe
exist. Task 9 (verification) is last, after everything.

Practical order: 1 → 2 → 5 (retrofit, now safe) → 3 → 4 → 6 → 7 → 8 → 9.

## 6. Residual decisions / risks

- **Archiving this plan on completion**: per the repository's own convention (all three
  existing entries in `docs/plans/README.md` are `Archived`), this plan should move to
  `docs/archive/plans/` once implementation and verification are done, with its frontmatter
  `status` and `id` updated to match that convention. Owner: whoever runs the closeout step
  (`docs-dev` per `conductor`'s "Plan closeout," or the implementing agent directly in this
  single-session context).
- **Risk**: retrofitting 13 files' `doc-type` in one step (task 5) before task 1's code lands
  would zero every one of them into `KW-DOC-SPEC-002` findings. Mitigated by the sequencing
  above — task 1 first, `docs validate` run before task 5 begins.

## 7. Out of scope

- Writing an actual spec document — `docs/specs/` is created empty; no greenfield feature
  currently warrants one.
- Migrating or referencing the Kyber-Squad product's own downstream `6-Docs/specs/`
  three-file workflow (`requirements.md`/`design.md`/`tasks.md`) — that belongs to
  `product-owner.md` and consumer repositories, not this repository's own `docs/`.
- Renumbering or otherwise altering the three existing archived plans.

## 8. Required skills

- `dotnet-dev` — the five-file ontology code change (task 1), build/test verification
  (task 9).
- `docs-dev` — every governed documentation change (tasks 2–8).

## 9. Verification harness

```bash
dotnet build KyberWeave.sln -c Release
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
dotnet run --project src/KyberWeave.Cli -- docs validate .
dotnet run --project src/KyberWeave.Cli -- docs drift .
```

Plus the specific proof that the required-key row is actually wired (not just declared in
prose): a scratch document with `doc-type: todo` and no `component` must fail
`docs validate` with `KW-DOC-SPEC-003`; the same document with `component` set must pass.
Every file under `docs/todo/` and the new `docs/specs/README.md` must validate clean after
the retrofit and the new folder land.
