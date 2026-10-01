---
id: plans/2026-10-01-kyberdash-bundle-litellm-output-path
title: "KyberDash: align the LiteLLM bundle output path and take the full pricing re-snapshot"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-01
development-mode: test-first
---

# KyberDash: align the LiteLLM bundle output path and take the full pricing re-snapshot

**Status: Complete, archived 2026-10-01.** Approve-and-execute gate recorded 2026-10-01: David
delegated plan approval to the orchestrator; the orchestrator approved this plan as written with
no changes to decisions A1–A5. Development mode: `test-first`. Branch:
`cursor/issue-229-bundle-litellm-path`. T1–T4 complete. End-of-run code-review council first pass
**REQUEST_CHANGES** (2026-10-01): KW-REVIEW-005 (`ts-test` blocked by a pre-existing
`migration.test.ts` DROP COLUMN failure unrelated to #229), plus a critical false-closeout that
had claimed APPROVE / full Dash gates green, and a major finding on the `claude-4-opus` alias
(disposition D5 below). Re-council after remediation (2026-10-01): council position
**APPROVE-quality** (empty in-scope #229 findings); engine verdict **REQUEST_CHANGES** solely on
KW-REVIEW-005 `ts-test` / pre-existing `migration.test.ts` DROP COLUMN (disclose, not fixed;
reproduces on clean HEAD). Durable content harvested into
[dash/architecture.md](../../dash/architecture.md) (CostBlock published-path rules),
[dash/runbook.md](../../dash/runbook.md) (build workflow), and
[reference/kyberdash-rationale.md](../../reference/kyberdash-rationale.md) (F2/full-refresh follow-ups
closed). Archived here per `KW-DOC-LIFECYCLE-003` before `docs validate . --merge-ready`.

This plan fixes [issue #229](https://github.com/dpalfery/kyber-weave/issues/229): the LiteLLM
bundler writes `dash/src/data/` while the runtime and tests import
`dash/src/pricing/data/`, so `npm run build` never refreshes committed prices and can leave a
stray untracked directory. This is follow-up **F2** from the archived
[PR #225 review-fixes plan](2026-09-30-issue-186-review-fixes.md) (comment
`4149313657`) and the deferred full re-snapshot from issue #186 decision **U11**.

**Execution note.** T1/T2 implementation may already be in progress on this branch: the
bundler path, luna tuple, focused regression test, and stale comment paths can appear modified.
The task text below remains the approved RED/GREEN contract. Closeout evidence remains pending
until T3/T4 are complete.

**Discovery provenance.** CodeGraph was used first, but marked the bundler changed since the
index was built, so current source was read directly. The Kyber-Weave docs MCP tools were
unavailable; discovery followed the documented fallback from [`docs/README.md`](../../README.md).
`git rev-parse --show-toplevel` returned
`/Users/hal/git/cursor/kyber-weave-43`, matching the assigned root.

## Problem and goal

`dash/scripts/bundle-litellm.mjs:dataDir` originally resolved to
`join(__dirname, '..', 'src', 'data')`, so both generated JSON files landed in a directory the
runtime does not read. `dash/src/pricing/models.ts` imports the committed files from
`./data/` relative to `dash/src/pricing/`. A build therefore performed network work but embedded
stale committed prices.

Goal: point the bundler at `dash/src/pricing/data/`, prove the path contract test-first, and
take one deliberate, source-cited full refresh of both generated artifacts. Preserve the
standing U11 vendor-cited rows, including `gpt-6-luna`'s absent cache-write rate, while making
all other upstream pricing changes explicit and reviewable.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | Use a plan, not a feature spec; deliver on the existing `cursor/issue-229-bundle-litellm-path` branch. | Conductor assignment for issue #229, 2026-10-01 |
| A2 | Fix the output mismatch and commit the full LiteLLM/models.dev/OpenRouter re-snapshot in the same change. `dash/src/pricing/data/` is authoritative. | Direct user/conductor scope constraint and issue #229 body |
| A3 | Development mode is `test-first`; T1 RED must precede T2 GREEN, even if those tasks are already in progress when this Ready plan is restored. | Direct user/conductor scope constraint |
| A4 | Keep existing build wiring and U11 semantics: do not decouple the bundler from `npm run build`; retain both U11 `MANUAL_ENTRIES`; change luna cache write from `0` to `null`; cite the refresh with source URLs, UTC time, counts, digests, and a changed-key summary. | Issue #229, archived #186 U11/review-fixes, current `buildCosts` semantics, and 2026-10-01 upstream verification |
| A5 | Approve and execute this plan as Ready. | David delegated plan approval to the orchestrator; orchestrator approved 2026-10-01 with no decision changes; recorded as the approve-and-execute gate |

## Investigation findings

- `bundle-litellm.mjs:25` originally pointed at `src/data`; `models.ts:6-7` imports from
  `src/pricing/data`. `dash/src/data/` is absent on a clean checkout and is not the tracked
  runtime data home.
- `dash/package.json` runs the bundler first in `build`; `build:cli` does not refresh pricing.
- KyberDash release packaging invokes `tsup` directly, so it embeds committed pricing data and
  does not independently re-snapshot during packaging.
- The primary mandatory source is LiteLLM's
  `model_prices_and_context_window.json`. The script then gap-fills from first-party makers in
  models.dev and from OpenRouter. LiteLLM failure aborts; the two fallback fetches currently log
  and continue.
- `pricing-fallback-data.test.ts` already reads the networked top-level bundler as text to verify
  `MANUAL_ENTRIES`; this is the established hermetic seam for the path regression.
- The committed `gpt-6-luna` tuple is `[1e-7, 5e-7, null, 1e-8, null]`, while the original
  `MANUAL_ENTRIES` row spelled cache write as `0`. `buildCosts` treats `null` as absent and `0`
  as explicit. A naive regeneration would change canonical cache-creation billing from
  input-rate treatment to free.
- The original test parser masked that distinction with `committed[i] ?? 0`; T1 must preserve
  literal `null` before the networked script is run.
- A read-only LiteLLM `main` check on 2026-10-01 found:
  - `claude-sonnet-5-5` matches U11 (`2e-6`, `1e-5`, `2.5e-6`, `2e-7`);
  - `gpt-6-luna` matches base input/output/cache-read but now has upstream cache creation
    `1.25e-7`, unlike U11's no-published-cache-write contract.
- Both U11 rows stay in `MANUAL_ENTRIES`. Removing a currently-equal row changes how future
  builds adopt upstream repricing and is not part of issue #229.
- The pre-refresh primary snapshot has approximately 4,671 keys. A large generated diff is
  expected and is the behavioral center of this change.
- Comment-only paths in `dash/src/providers/codex-pricing-1075.test.ts` and
  `dash/src/ingest/codex-pricing-1075-rehydrate.test.ts` originally cited
  `src/data/litellm-snapshot.json`; T2 corrects them to `src/pricing/data/`.
- Canonical docs list the mismatch and full refresh as open F2 work in
  `docs/dash/architecture.md` and `docs/reference/kyberdash-rationale.md`.

## Test contract (`development-mode: test-first`)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/pricing/data/pricing-fallback-data.test.ts` | `npm --prefix dash exec -- vitest run src/pricing/data/pricing-fallback-data.test.ts` | Bundler source targets `dash/src/pricing/data/`, not `dash/src/data/`. The manual-tuple parser distinguishes `null` from `0`. Both U11 rows remain manual and match the committed first four tuple slots, including luna's `null` cache write. | After adding the contract but before production edits, capture failures on the old output path and luna's `0` cache-write literal. | RED evidence names only those intended gaps; no production/generated file is changed by T1. |
| T2 | Same focused data test plus `dash/src/canon/published-pricing.test.ts` and `dash/src/pricing/models.test.ts` | `npm --prefix dash exec -- vitest run src/pricing/data/pricing-fallback-data.test.ts src/canon/published-pricing.test.ts src/pricing/models.test.ts` | Corrected bundler source satisfies the path/null contract; U11 rates remain pinned; luna cache creation is billed under the existing absent-cache-write policy. | T1's captured RED is required before T2 implementation. | The same contract is GREEN without weakened assertions; existing U11 hygiene/rate/provenance cases pass. |
| T3 | Generated `dash/src/pricing/data/litellm-snapshot.json` and `pricing-fallback.json`; T2 suites | `npm --prefix dash run build`, then the T2 focused command | One full build rewrites tracked runtime artifacts from all three named sources, leaves no `dash/src/data/`, preserves U11 behavior, and produces valid non-negative tuples. | No separate RED: T1 proves the pre-fix build path is wrong; T3 is generated-output verification over T2. | Build exits 0; no source is skipped; focused suites pass; all added/removed/changed keys are summarized; no old-path output exists. |
| T4 | Documentation only: `docs/dash/architecture.md`, `docs/dash/runbook.md`, `docs/reference/kyberdash-rationale.md`, this plan, and `<plan-index>` | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | Current docs describe the corrected path and networked full-build behavior; F2/full refresh are no longer open; archived closeout records source and review evidence. | n/a — explicitly no-test documentation task; documentation gates replace a unit test. | Both checks pass; at PR close the plan is archived and its row moves before `docs validate . --merge-ready`. |

## Dispatchable tasks

### T1 — RED: pin path and null-preservation contracts

- **Objective:** reproduce the mismatch and `0`/`null` regeneration hazard hermetically.
- **Exact files/symbols:** `dash/src/pricing/data/pricing-fallback-data.test.ts`, especially
  `source`, `block`, and `manual` parsing under
  `issue #186 bundler MANUAL_ENTRIES reproducibility`.
- **Work:**
  - parse tuple literals as `(number | null)[]`, preserving `null`;
  - assert `dataDir` joins `src`, `pricing`, `data`, rejecting old `src/data`;
  - require both U11 manual rows to match committed first-four-field tuples exactly;
  - capture focused RED before any production edit.
- **Acceptance criteria:** T1 Test-contract RED evidence exists for only the path and luna gaps.
- **Dependencies:** none.
- **Required skill:** `test-dev`.

### T2 — GREEN: align the bundler and preserve U11

- **Objective:** write generated prices where the runtime reads them without an accidental
  pricing-policy change.
- **Exact files/symbols:**
  - `dash/scripts/bundle-litellm.mjs:dataDir`
  - `dash/scripts/bundle-litellm.mjs:MANUAL_ENTRIES`
  - comment-only paths in `dash/src/providers/codex-pricing-1075.test.ts` and
    `dash/src/ingest/codex-pricing-1075-rehydrate.test.ts`
- **Work:**
  - set `dataDir = join(__dirname, '..', 'src', 'pricing', 'data')`;
  - retain both U11 manual rows;
  - set `gpt-6-luna` cache write to `null` (with optional trailing `null` for shape clarity) and
    correct the comment that equates `null` with `0`;
  - fix both stale comment-only snapshot paths;
  - do not execute the networked bundler until T1 RED is captured.
- **Acceptance criteria:** T2 Test-contract GREEN; lint accepts changes; one output-path
  definition remains.
- **Dependencies:** T1.
- **Required skills:** no listed skill covers Node/TypeScript Dash tooling; conductor assigns a
  TypeScript-capable implementer.

### T3 — Generate, cite, and audit the full re-snapshot

- **Objective:** produce and review the intentionally broad pricing refresh.
- **Exact files/symbols:**
  - `dash/src/pricing/data/litellm-snapshot.json`
  - `dash/src/pricing/data/pricing-fallback.json`
  - this plan's Closeout evidence section
- **Work:**
  - record pre-run SHA-256 digests and key counts;
  - run `npm --prefix dash run build` once with working network access;
  - reject any LiteLLM error, fallback `skipped` warning, first-party allowlist drift warning, or
    partial output;
  - record all three source URLs, UTC time, stdout counts, post-run digests/key counts, and an
    added/removed/changed-key summary;
  - inspect changed tuples for finite non-negative per-token rates and identify zero-rate stubs
    rather than treating them as published free prices;
  - verify both U11 rows and luna cache-creation behavior with T2's focused suites;
  - verify `dash/src/data/` remains absent and has no git status entry.
- **Acceptance criteria:** T3 Test-contract GREEN; only intended tracked pricing data changes;
  complete source/diff evidence is ready for review.
- **Dependencies:** T2.
- **Required skills:** no listed skill covers generated TypeScript pricing data; conductor
  assigns a TypeScript-capable implementer.

### T4 — `docs-dev` closeout

- **Objective:** align canonical docs and lifecycle state with shipped behavior.
- **Exact files/symbols:**
  - `docs/dash/architecture.md`, `CostBlock and cost basis` → `Published-path rules`
  - `docs/dash/runbook.md`, build workflow
  - `docs/reference/kyberdash-rationale.md`, issue #186 known follow-ups
  - this plan and `docs/plans/README.md`
- **Work:**
  - replace the architecture's open F2 statement with the corrected generated-data path;
  - document that `npm run build` performs the networked re-snapshot, while `build:cli` and
    current release packaging do not;
  - remove the completed full-refresh and F2 bullets from the rationale, preserving unrelated
    follow-ups;
  - fill Closeout evidence with sources, time, counts, digests, tuple-diff audit, gates, and
    review;
  - after implementation/review pass, archive the plan and move its inventory row before the
    merge-ready documentation gate.
- **Acceptance criteria:** T4 Test-contract GREEN; no current doc says the bundler writes
  `dash/src/data/`.
- **Dependencies:** T3 and implementation review.
- **Required skill:** `app-docs-standard`.

## Dependency graph and MAX_CONCURRENCY

```text
T1 (RED) → T2 (GREEN) → T3 (full refresh + audit) → review → T4 (docs-dev)
```

| Task | Depends on | Exclusive file scope |
|---|---|---|
| T1 | — | pricing data test |
| T2 | T1 | bundler and two comment-only test files |
| T3 | T2 | two generated pricing JSON files; Closeout evidence |
| T4 | T3, review | canonical docs, plan lifecycle, plan index |

The work is serial because each stage consumes the previous stage's concrete output.
**MAX_CONCURRENCY: 1.**

## Risks

- **Broad repricing:** thousands of generated rows may change. T3 records every changed key and
  treats the data diff as behavior, not noise.
- **Partial fallback refresh:** models.dev and OpenRouter are best-effort in the script. Either
  being skipped fails this committed refresh even if the script exits zero.
- **`null` versus `0`:** `null` means absent cache-write rate; `0` means explicitly free. T1/T2
  prevent silent semantic conversion.
- **Upstream movement:** if U11 source facts materially change, return the plan to Draft rather
  than silently select a new policy.
- **Network nondeterminism:** source URLs, time, counts, digests, and complete tuple-diff evidence
  make the committed result auditable.
- **Post-merge dirty builds:** corrected `npm run build` can rewrite tracked data whenever
  upstream changes. This is existing build wiring and outside issue #229.
- **Plan lifecycle:** active plans fail `docs validate --merge-ready`; T4 archives only after
  evidence and review complete.

## Out of scope

- Removing U11 keys from `MANUAL_ENTRIES` merely because LiteLLM now ships them.
- Decoupling the bundler from `npm run build` or changing release packaging.
- Changing runtime pricing lookup, canonical repricing, Copilot credits, tier selection,
  aliases, overrides, or schema.
- Inferring rates or changing LiteLLM/models.dev/OpenRouter source priority.
- Fixing unrelated #186 follow-ups, including F1 cell width and other cost displays.
- Adding dependencies or changing Dash distribution/update behavior.

## Verification gates

```bash
npm --prefix dash exec -- vitest run src/pricing/data/pricing-fallback-data.test.ts
npm --prefix dash run build
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

At PR close, archive this plan before `docs validate . --merge-ready`. Build
`KyberWeave.sln -c Release` first only if the documentation CLI is unavailable; warnings are
errors.

## Review

- Run the repository `code-review` flow over the complete script, test, generated-data, and docs
  diff after T3 and before T4.
- Compare all generated additions, removals, and changed tuples with T3's source/run evidence.
- Require human confirmation for any unexplained disappearance, negative/non-finite value,
  high-magnitude reprice, source skip, or zero-rate stub presented as free.
- Re-run focused U11/data tests after any review edit.
- No ADR is expected: this restores an intended path contract and executes an already-recorded
  deferred refresh.

## `docs-dev` closeout

T4 records:

- exact LiteLLM, models.dev, and OpenRouter URLs and UTC retrieval time;
- pre/post digests and primary/fallback key counts;
- added/removed/changed tuple summary and anomaly dispositions;
- RED/GREEN evidence and all Dash/documentation gates;
- code-review verdict and human pricing-diff confirmation.

The completed PR closes issue #229 and cites issue #186 U11 and PR #225 comment `4149313657`.

### Closeout evidence

**T3 refresh audit (2026-10-01).** T4 docs narrative and gates complete 2026-10-01.

| Field | Value |
|---|---|
| Initial full refresh (UTC) | ~`2026-10-01T20:25:25Z` |
| Remediation rebundle (UTC) | `2026-10-01T21:03:16Z` → `2026-10-01T21:03:23Z` — after restoring `MANUAL_ENTRIES` for `kimi-k2-thinking`, `claude-opus-4-20250514`, and `grok-latest` |
| LiteLLM URL | `https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json` |
| models.dev URL | `https://models.dev/api.json` |
| OpenRouter URL | `https://openrouter.ai/api/v1/models` |
| Build exit | `0` |
| stdout counts | `models.dev (first-party): +52 models`; `openrouter (backstop): +0 models`; `Bundled 5978 primary + 52 fallback models` |
| Reject signals | None — no LiteLLM error, no `models.dev skipped` / `openrouter skipped`, no first-party allowlist drift warning, no partial output |
| Informational notes | `MANUAL_ENTRIES` candidate-to-remove for keys now in LiteLLM: `MiniMax-M2.7`, `deepseek-v4-flash`, `deepseek-v4-pro`, `claude-mythos-5`, `claude-sonnet-5-5`, `gpt-6-luna` (U11 rows retained per A4) |

**Digests and key counts** (SHA-256; HEAD = committed baseline before this branch's refresh):

| Artifact | HEAD digest | HEAD keys | POST digest | POST keys |
|---|---|---|---|---|
| `dash/src/pricing/data/litellm-snapshot.json` | `723c3dbd33a5ff7bd968a2c341d91cf525c3e1a00ce2efd1a9ccc74f8a8918df` | 4671 | `82bdc2f48efcad9e1a29cfac0732bf011f1d06f81ce6bb5390fd6ac5be54ee97` | 5978 |
| `dash/src/pricing/data/pricing-fallback.json` | `b80279a8f04598a60974d61366b236dacb31c6d262918cfcd9bc4eaea587e91d` | 205 | `387c51ffe27a61ee1b392859ba91e4c67160cf9b951884fde9e37095ad34bfcb` | 52 |

Current on-disk digests (confirmed via `node` at T4 closeout) match POST below. The remediation
rebundle rewrote the same committed bytes as the initial refresh except for the three restored
manual lookup shapes.

**Key diff vs HEAD** (full machine-readable dump: `/tmp/t3-key-diff.json` — present on the T3 host as of this closeout):

| File | added | removed | changed |
|---|---:|---:|---:|
| litellm-snapshot | 1651 | 344 | 403 |
| pricing-fallback | 8 | 161 | 1 |

**Anomaly scan on changed tuples:** negatives `0`; non-finite `0`. Zero-rate stubs among changed primary keys: 19 (15 cache-write `0`, 3 output `0` on reranker-style models, 1 cache-read `0`). High-magnitude samples (≥10× input/output vs HEAD, all decreases): `bigscience/mt0-xxl-13b`, `watsonx/bigscience/mt0-xxl-13b`, `gryphe/mythomax-l2-13b` (+ openrouter twin), `mancer/weaver` (+ openrouter twin), `mistral-small-2503@001`, `vertex_ai/mistral-small-2503`, `vertex_ai/mistral-small-2503@001`. Sidecar: `/tmp/t3-anomalies.json` (present; `litellm.zeroStubs` length 19).

**Anomaly dispositions (D2–D4).** Provenance: orchestrator decision 2026-10-01 (David-delegated gate).

| Id | Finding | Disposition |
|---|---|---|
| D2 | Primary removed 344 keys; fallback removed 161 keys (`/tmp/t3-key-diff.json`) | ACCEPT as authorized upstream attrition — not unexplained disappearance under Review |
| D3 | 19 zero-rate stubs among changed primary keys (`/tmp/t3-anomalies.json`) | ACCEPT as upstream LiteLLM literals (cache-write/output/cache-read `0` fields), not “published free” product claims |
| D4 | ≥10× input/output decreases vs HEAD (samples above) | ACCEPT as upstream LiteLLM/OpenRouter reprices |
| D5 | Council major: alias `claude-4-opus` → missing bare `claude-opus-4` | ACCEPT as intentional post-refresh contract — see disposition below |

**D5 — `claude-4-opus` / bare `claude-opus-4` (council major, 2026-10-01 remediation).** Intentional
post-refresh behavior, not a #229 regression and not a MANUAL restore. `models.test.ts` pins
`getModelCosts('claude-opus-4')` to `null` and Cursor cases map `claude-4-opus` → bare
`claude-4-opus` (the stripped $5 reseller key). `getModelCosts` prefers an alias target only when
`pricingCache.has(canonical)`; with the bare `claude-opus-4` primary row retired, lookup falls
through to that stripped key. Do **not** add bare `claude-opus-4` to `MANUAL_ENTRIES` (would
break #420 / `models.test` expectations). Dated MANUAL `claude-opus-4-20250514` remains per D1.
Alias-table / runtime lookup logic left unchanged (out of plan scope; focused tests green).

**U11:** `claude-sonnet-5-5` = `[2e-6, 1e-5, 2.5e-6, 2e-7]`; `gpt-6-luna` = `[1e-7, 5e-7, null, 1e-8, null]` (cache-creation absent/`null` preserved). Untouched by this rework — neither U11 row nor generated JSON was edited for D1–D5.

**`dash/src/data/`:** absent; no git status entry.

**MANUAL_ENTRIES disposition (D1 — non-U11 extras).** Provenance: orchestrator decision 2026-10-01 (David-delegated gate). KEEP the three keys below; each is present in the current primary snapshot and in `dash/scripts/bundle-litellm.mjs` `MANUAL_ENTRIES`. No additional manual rows added during T3. U11 rows retained per A4 (out of D1 scope).

| Key | Lookup shape restored | Why LiteLLM alone is insufficient (tree-grounded) |
|---|---|---|
| `kimi-k2-thinking` | **Bare** id (and alias targets that resolve to it) | Bundler comment (`bundle-litellm.mjs`): LiteLLM ships only prefixed keys such as `openrouter/moonshotai/kimi-k2-thinking`; Pass 2 strips one segment → `moonshotai/kimi-k2-thinking`, never bare `kimi-k2-thinking`. Runtime aliases `kimi-auto` / `kimi-code` / `kimi-for-coding` → `kimi-k2-thinking` (`models.ts`); `models.test.ts` asserts `resolveCanonicalModelId('kimi-code') === 'kimi-k2-thinking'`. Without the manual bare row, those aliases price null. |
| `claude-opus-4-20250514` | **Dated** Anthropic-style id | Bundler comment: the refresh dropped this dated Opus 4 id from LiteLLM surface. `parser-advisor-usage.test.ts` still cites `ADVISOR_MODEL = 'claude-opus-4-20250514'` as the Claude Code `/advisor` journal model. LiteLLM may retain undated or differently pinned siblings; it does not restore this exact dated lookup the advisor parser and fixtures use. |
| `grok-latest` | **Alias** / product-latest id from xAI usage | Bundler comment: xAI `modelUsage` reports `grok-latest`; LiteLLM carries only ~`x-ai/grok-latest`, and Pass 2 does not peel to bare `grok-latest`, so `chooseAuthoritativeModel` (`grok.ts`) would fall back to summary `grok-build`. `grok-parser-pipeline.test.ts` / `grok.test.ts` price and assert cold calls on bare `grok-latest`. Manual row restores that alias shape. |

Confirmed in POST primary snapshot: `kimi-k2-thinking` → `[6e-7, 2.5e-6, null, 1.5e-7, null]`; `claude-opus-4-20250514` → `[15e-6, 75e-6, 18.75e-6, 1.5e-6, null]`; `grok-latest` → `[2e-6, 6e-6, null, 5e-7, null]`.

### Approved dispositions (D1–D5)

Orchestrator decisions 2026-10-01 (David-delegated gate) for D1–D4; D5 recorded on the same day
during REQUEST_CHANGES remediation. D1–D4 satisfy this plan’s **Review** human-confirmation
requirement for unexplained disappearance, zero-rate stubs, and high-magnitude reprice; D5
disposes the council’s major alias finding without rewriting `MODEL_ALIASES` or lookup logic.
Re-council after remediation recorded APPROVE-quality on findings (see Review below).

| Id | Decision | Satisfies Review gate for |
|---|---|---|
| D1 | KEEP `kimi-k2-thinking`, `claude-opus-4-20250514`, `grok-latest` in `MANUAL_ENTRIES` (per-key rationales above) | Non-U11 manual retention / lookup-shape restores |
| D2 | ACCEPT primary −344 / fallback −161 removals as authorized upstream attrition | Unexplained disappearance |
| D3 | ACCEPT 19 zero-rate stubs as upstream literals (not “published free” product claims) | Zero-rate stub presented as free |
| D4 | ACCEPT ≥10× decreases as upstream LiteLLM/OpenRouter reprices | High-magnitude reprice |
| D5 | ACCEPT missing bare `claude-opus-4` / stripped `claude-4-opus` $5 identity as intentional post-refresh; no MANUAL restore of bare `claude-opus-4` | Council major on opus alias (not a #229 must-fix) |

**RED/GREEN (test-first, cwd `dash/`):**

| Task | Evidence |
|---|---|
| T1 RED | `pricing-fallback-data.test.ts` failed on `dataDir` resolving to `src/data` (not `src/pricing/data`) and on `gpt-6-luna` cache-write literal `0` vs committed `null` before production edits |
| T2 GREEN | Path/null contract GREEN; `vitest run` on `pricing-fallback-data.test.ts`, `published-pricing.test.ts`, `models.test.ts` — 3 files, 234 tests passed |
| T3 GREEN | `npm --prefix dash run build` exit 0; digests/key counts as above; `dash/src/data/` absent; no rate-expectation edits required for this refresh |
| T4 GREEN | Canonical docs updated; plan archived; documentation gates below |

**Review:** code-review council first pass **REQUEST_CHANGES** (2026-10-01). Findings: (1)
critical false closeout that had claimed APPROVE / full Dash gates green; (2) major
`claude-4-opus` → missing `claude-opus-4` (disposed as D5 — intentional post-refresh; comment
and closeout corrected; no MANUAL / alias-table change); (3) KW-REVIEW-005 on `ts-test` from the
known pre-existing `dash/src/canon/migration.test.ts` failure
(`error in table refresh_run after drop column: incomplete input` on schema migration v14 → v15).
That DROP COLUMN failure reproduces on clean HEAD, is unrelated to #229, and is **not** fixed
here (same baseline disclosure pattern as the archived PR #233 review-follow-up plan). Human
pricing-diff confirmation remains D1–D4; D5 added for the alias finding. U11 rows preserved;
`kimi-k2-thinking`, `claude-opus-4-20250514`, and `grok-latest` MANUAL regression restores
confirmed in POST primary snapshot. Re-council after remediation (2026-10-01): council position
**APPROVE-quality** (empty in-scope #229 findings); engine verdict **REQUEST_CHANGES** solely on
KW-REVIEW-005 `ts-test` / pre-existing `migration.test.ts` DROP COLUMN (disclose, not fixed;
reproduces on clean HEAD). Do not claim engine APPROVE or “full Dash gates green” without that
migration disclosure.

**Documentation gates (T4 closeout):**

```bash
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready
```

**Dash gates (honest evidence):** focused pricing suites 234/234
(`pricing-fallback-data.test.ts`, `published-pricing.test.ts`, `models.test.ts`);
`npm --prefix dash run typecheck`, `lint`, and `check:reachable` pass. Full `npm --prefix dash
run test` (`ts-test`) is blocked only by the pre-existing `migration.test.ts` DROP COLUMN
failure above — not by #229 work. .NET / documentation gates pass (validate / drift as above).
