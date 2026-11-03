---
id: plans/2026-10-28-kyberdash-harness-collection-standard
title: "KyberDash per-harness collection and dashboard-completeness standard"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-28
development-mode: test-first
documentation-impact: extends-component
impact-components: [KyberDash, CI Pipelines]
---

# KyberDash per-harness collection and dashboard-completeness standard

**Status: Complete 2026-10-28 — archived by its finishing change.**

**Development mode:** `test-first` (default; no opt-out supplied). **Autonomy:** `autonomous`
(resolved from repository root `AGENTS.md` Config Reg `<autonomy>`; supplied in this run's
packet).

## 1. Problem and goal

### 1.1 Request (paraphrased)

Author a durable per-harness collection-and-display standard for KyberDash so a worker cannot
claim "complete" on missing or empty dashboard data, add enforcement, and update the governing
docs so a repeat false-complete claim is impossible.

### 1.2 Problem

KyberDash already collects, projects, and renders honestly at the engine level — the
per-harness end-to-end truth is recorded in `docs/dash/telemetry-inventory.md` §"Per-harness OTel
collection matrix" (ingested → projected → visible, absent dimensions serialized as
`not_measurable`, never zero). What is missing is a **normative standard** a worker must satisfy:
for each harness, what the dashboard must show when the source supplies data, and what
honest-empty looks like (a visible session whose absent dimensions carry `not_measurable` with a
reason) versus missing data (a silent skip, a fabricated zero, or an absent session for a
recognized model call). Without that, "complete" is a claim with no named, checkable criterion,
and the prior defect (`docs/archive/plans/2026-10-27-kyberdash-otel-harness-coverage.md`) — a
counter-less, content-less model-call session being silently dropped at projection — is the exact
failure mode a standard must make un-claimable.

### 1.3 Goal

1. Formalize the archived plan's §3.2 matrix into a durable per-harness standard (doc-type
   `rule`) naming, per harness, what must be collected and what the dashboard must show,
   including what honest-empty looks like versus missing.
2. State the collection-mechanism hierarchy: OTel / plugin hooks / direct database reads are
   PRIMARY; file ingest (`dash refresh` synth, `dash import-history`) is backup/fallback and must
   never be billed as primary.
3. Enforce mechanically: a deterministic, self-contained Playwright conformance spec (the T8
   fixture pattern already in `dash/e2e/refresh-filters.spec.ts`) that pins the completeness
   predicate per harness, wired into CI on ubuntu.
4. Update only the docs the standard owns (`telemetry-inventory.md`, `architecture.md`, the new
   rule, and the CI runbook for the new job) so "complete" cannot be claimed without the named
   dashboard evidence; `docs validate` / `docs drift` stay at zero findings; no new `KW-*` rule
   ids; no ontology widening.

## Decisions

This table is permanent state, preserved intact across all lifecycle transitions.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D-INTAKE | Plan or spec for this open request? | **Plan.** Bounded change to an existing product: the per-harness semantics already exist as `docs/dash/telemetry-inventory.md` §"Per-harness OTel collection matrix"; the work is to formalize that matrix into an enforceable rule, add a collection-mechanism hierarchy, and extend existing verification (Playwright E2E at `dash/e2e/*.spec.ts` + `dash/playwright.config.ts`). | A spec would author three artifacts for work that is a standard-plus-enforcement refinement of existing docs and tests. Plan matches the route. | `autonomous: recommendation accepted` |
| D-A | Verification scope: Playwright-only, CI macOS job, or both? | **Playwright-only commitment.** Extend `dash/e2e/` with a self-contained per-harness conformance spec (fixture-driven completeness predicate, T8 pattern) and run it in CI on `ubuntu-latest` (new `dash-e2e` job — no macOS, no funded model, no new secret). The CI macOS + funded live-model turn per harness is **deferred as a follow-up todo**. | A live funded call proves a *harness emits* (capture-side), which no check in this repo can make deterministic and which overlaps the deferred "last emitted" diagnostic (`docs/todo/opencode-plugin-token-usage-capture.md`). Deterministic Playwright over seeded fixtures asserts the completeness rule itself, which is the claim being gated. Rejected: "CI macOS job now" (funding-gated, non-deterministic, needs a provider secret the repo has no funder for); "both" (same objection). | `autonomous: recommendation accepted` |
| D-B | "Complete" semantics: real measured number required, or `not_measurable` acceptable? | **Synthesis — measured where the primary mechanism supplies data; `not_measurable` is the only acceptable absence.** Complete IFF every standard-named dimension is either (a) a real measured value from a PRIMARY mechanism, or (b) rendered `not_measurable`/dash with a stated reason because the source genuinely cannot supply it; AND a recognized model call always surfaces as a visible session; AND a zero-record harness renders honest-empty ("No runs recorded") which is never "complete". Never allowed: fabricated zero, silent skip, absent session for a recognized call, or "complete" claimed for a missing/empty dimension. | The two prompt options are not mutually exclusive; honest-unobservability R1/R2 plus the projection contract (`architecture.md` §"shared canonical projection") already require measured-or-`not_measurable`, never zero-coercion. "Honest-empty" (visible + `not_measurable`) is complete; "missing" (dropped/absent) is a false-incomplete the standard forbids. | `autonomous: recommendation accepted` |
| D-C | CI funding: who funds model calls, cheapest/free opencode-compatible provider+model? | **No funded model call in this plan.** No funding source exists today (no model-funded job in `.github/workflows/ci.yml` or `release.yml`; no provider secret beyond Apple signing keys and `github.token`). The enforcement stays deterministic (Playwright fixtures). If a live-verification follow-up is later funded, the cheapest deterministic path is a locally-hosted opencode-compatible model over `baseURL` (no API funder), but that requires a macOS runner + model cache and remains deferred. No provider or model is named now. | Naming a paid provider/model without an owner-approved funder would add a new secret + macOS cost with no sign-off; committing to one indexes a dependency the repo does not carry. Cost of being wrong is high (funding/spend), so the safe recommendation is to defer funding and keep enforcement deterministic. | `autonomous: recommendation accepted` |
| D-D | Token-capture scope: keep deferred vs pull into this plan? | **Keep deferred.** `docs/todo/opencode-plugin-token-usage-capture.md` (real opencode plugin token-usage capture; per-source last-emitted status; counter-less content projection) stays out of scope. | Capture-side enhancement requiring a live hook verification (hook name, payload, and whether it exposes counters are unknown from this checkout). Orthogonal to formalizing the standard. The standard only needs to state "tokens are `not_measurable` until the plugin captures usage," which the deferred todo already models; pulling it in would expand scope to live-gated capture with no deterministic test. | `autonomous: recommendation accepted` |
| D-E | GAP: is per-session "no published rate" expected, or a rendering gap? | **Expected — honest-empty cost state, not a gap.** `costBlockFor` (`dash/src/synth/synth.ts:356-378`) and `pricePublishedTurn` (`dash/src/canon/published-pricing.ts:24-57`) make "no published rate" a first-class `CostBlock` status (`status: 'no_rate'`, plus `'not_billed'`/`'out_of_scope'`), enforced by R5.4/R2.8 ("renders a zero figure as no published rate, never a priced $0.00") and `secondary-cost-display` R3 ("unpriced"/"flat-rate", never `$0.00`). Rate publication is a pricing input — secondary and derived — distinct from measured token/cost metrics. | The standard therefore treats "no published rate" as honest-empty and MUST NOT require a per-session rate as a "complete" criterion (doing so would force a fabricated cost and violate honest-unobservability R2 and secondary-cost-display R1/R3). The dashboard must render the basis+status (`unpriced`/`no published rate`), not a dollar figure. | `autonomous: recommendation accepted` |
| AP1 | Approve-and-execute gate for this Draft plan | **Approved — the plan moves to Ready and is executable.** | Conductor supplied explicit `AUTONOMOUS_APPROVAL: approve-and-execute` under resolved autonomy `autonomous` on 2026-10-28; scale (`extends-component`, `impact-components: [KyberDash, CI Pipelines]`) and `test-first` mode confirmed. No task executes until the conductor dispatches it. | `autonomous: recommendation accepted` |
| CB1 | Circuit-breaker on T2 (`BLAST_RADIUS_EXCEEDED`): the T1 spec's boot and navigation assumptions are stale against the current product | **CONTRACT UNCHANGED — bounded spec-only rework, one final dispatch for this cluster (1 of 2 used).** The §5 Test contract's observable behavior is intact and still renderable: `HarnessDetail` (route `/harness/:harnessId`) mounts `Scorecard` (`dimension-value` / `dimension-unmeasurable` / `dimension-reason-*`), `harness-runs-table`, `drill-run-*`, and "No runs recorded". The break is stale spec mechanism only, no product/UI restore and no `dash/package.json` change; the plan stays Ready. | Verified against source: `dash/package.json` (no `build:dash`; `build:web`/`build` present), `dash/web/vite.config.ts` (`outDir: '../dist/dash'`), `dash/src/cli/web.ts:89-158` (`resolveStaticSource` auto-finds `dash/dist/dash`; `CODEBURN_DASH_DIR` is read by nothing — the override is `KYBERDASH_DASH_DIR`), `dash/web/src/pages/ContextDoctor.tsx:530-536` ("the scorecard matrix was removed from this page outright"), `dash/web/src/lib/router.ts:106` + `App.tsx:640` (`/harness/:harnessId` → HarnessDetail), `HarnessDetail.tsx:259/272/288/308/338`, `Scorecard.tsx:279/291`. | `autonomous: recommendation accepted` |
| CB2 | Second circuit-breaker on T2 rework (`BLAST_RADIUS_EXCEEDED`): direct deep-link `page.goto('/harness/:id')` against the **built** server renders blank — `runWebDashboard`'s static fallback serves `index.html` (text/html) for `/harness/assets/*.js`, so the module script is refused. Root `/` loads fine. | **CONTRACT UNCHANGED — spec-side navigation workaround, final dispatch for this cluster (2 of 2).** Navigate from root via client-side routing using the app's own `pushView`/`popstate` mechanism instead of a direct deep-link document load; keep blast radius inside `dash/e2e/`; no product/server change. §5 observable behavior unchanged; the plan stays Ready. The deep-link blank page is a genuine pre-existing product defect (built-server asset resolution under `base: './'`), recorded as a recommended follow-up todo — out of this plan's scope and remaining budget. | Verified: `App.tsx:114-119` (`pageFor` maps `harness`→`context-doctor`, so `showSpine` renders `HarnessDetail` at `App.tsx:639-647`), `App.tsx:419-443` (popstate handler consumes `history.pushState({stack},…)` via `restoreFromPopState`), `App.tsx:449-456` (`openSpine`/`pushView` push `{stack}`), `router.ts:263-299` (`pushView`/`restoreFromPopState`/`isStoredStack`), `vite.config.ts:11` (`base: './'` — relative assets, so built deep links mis-resolve), `web.ts:1207-1208` (unknown path → `index.html`, so `/harness/assets/*.js` served as text/html). | `autonomous: recommendation accepted` |

No hard-block escalations: every open decision had a recommendable option (autonomy checks 1–3 all resolved; every recommendable option stands on a documented source — the matrix, the R5.x/R2.8 cost rules, the existing e2e fixture pattern, the funded-job absence in CI).

## 3. Investigation findings

### 3.1 The matrix this standard formalizes

`docs/dash/telemetry-inventory.md` §"Per-harness OTel collection matrix" already decomposes
"collected" into three stages — **Ingested** (a canonical `records` row exists), **Projected**
(the record feeds a `session` row the ASAD payload reads), **Visible** (the session appears in
the Sessions rail with `not_measurable` markers for absent dimensions). Per harness (OTLP
adapter): `claude-code` (reference — counters + content, opt-in env), `opencode` native Effect
(by-design quarantine, no model operation), `opencode` plugin (counter-less, content only with
consent → projects as a `not_measurable` session), `pi` / `copilot (chat)` / `antigravity`
(capture-side stopped exporters), `codex OTLP` (owner-gated), `gemini` (attribution-only survey
family, never a stored harness). The standard builds on this, adding an explicit per-harness
"must show" column and the honest-empty-vs-missing predicate.

### 3.2 Collection-mechanism hierarchy (existing behaviour, to be named in the standard)

- **PRIMARY mechanisms** produce measured canonical records: (1) live OTLP from the harness
  (`dash/src/otel/*` receiver + `dash/src/canon/adapters/registry.ts` fingerprint vote), (2)
  harness plugin hooks (`dash/src/capture/harnesses/opencode-plugin/`, the Kilo capture plugin),
  (3) direct harness-datastore reads (`kilo.db` via `createSqliteSessionParser`/`kiloReader`,
  Cursor SQLite, transcript JSONL, Command Code JSONL).
- **BACKUP/FALLBACK** is file-synth ingest: `dash refresh` synthesizing `synth:` records from
  transcripts where the primary path is absent/unpaired, and `dash import-history`. Precedence is
  already per-bucket and never summed: "counters come from OTLP; file-derived parts are used only
  when the OTLP record has no parts" (`telemetry-inventory.md`; ADR 0029 leaves the file reader as
  the per-bucket baseline). A bucket sourced only from backup stays honestly labelled (e.g. Claude
  file-synth `system_prompt` remains `not_measurable`).

### 3.3 D-E finding — the rate/publish path (verified, cited)

"no published rate" is an expected, deliberate cost-block state, not a rendering gap. Verified:
`dash/src/synth/synth.ts:356-378` (`costBlockFor`: finite non-zero `costUSD` → `priced`; a zero
figure with `costIsEstimated: true` → `{ basis: 'published', status: 'no_rate' }`; otherwise
`{ basis: 'unknown', status: 'no_rate' }`); `dash/src/canon/published-pricing.ts:24-57`
(`pricePublishedTurn`: published-table harnesses Claude/Codex priced from the LiteLLM snapshot;
no rate → `no_rate`, flat-rate → `not_billed`, out-of-scope → `out_of_scope`); pinned by
`dash/src/synth/synth.test.ts` ("renders a zero figure as no published rate, never a priced
$0.00 (R5.4)") and `dash/src/pricing/unpriced-model-no-rate.test.ts` ("costs `no_rate`, never
$0.00 (R2.8)").

### 3.4 Enforcement surface (verified)

- `dash/e2e/spine.spec.ts` runs against the live 4747 host and real `canon.db`; it already
  asserts "no unreported counter renders as zero" (G3) and "empty datasets do not render as
  charts" (G6). It is not CI-deterministic (depends on real data).
- `dash/e2e/refresh-filters.spec.ts` (T8) is the CI-able pattern: it boots its own temp
  dashboard + temp refreshed DB from a fixture, seeds derived-harness sessions, and asserts
  `dimension-unmeasurable` / `dimension-value` presence with a lying-zero guard.
- `dash/playwright.config.ts` `reuseExistingServer: !process.env.CI` anticipates CI (start own
  server in CI).
- CI today: `ts-gates` (ubuntu) runs `npm test` (vitest, does NOT run Playwright); no workflow
  file mentions `playwright` or `e2e`; macOS runners exist (`squad-filesystem-contract`,
  `update-loop`, `tray-rust`, release build) but there is **no model-funded job** (API-key
  matches are Apple signing keys + `github.token` only). Confirmed the intake's claim.

## Design harvest

`documentation-impact: extends-component`, `impact-components: [KyberDash, CI Pipelines]`.

| ID | Target component | Target doc type | Target doc | Durable design item |
|---|---|---|---|---|
| H1 | KyberDash | rule | [`docs/rules/kyberdash-harness-collection.md`](../../rules/kyberdash-harness-collection.md) (new) and [`docs/rules/README.md`](../../rules/README.md) row | Per-harness must-collect/must-show table, the completeness predicate (D-B), honest-empty vs missing, and the PRIMARY vs backup collection-mechanism hierarchy. |
| H2 | KyberDash | reference | [`docs/dash/telemetry-inventory.md`](../../dash/telemetry-inventory.md) | The rule cross-reference as the binding standard, the collection-mechanism hierarchy, and the per-harness OTel collection matrix. |
| H3 | KyberDash | architecture | [`docs/dash/architecture.md`](../../dash/architecture.md) | One short subsection naming the collection-mechanism hierarchy and the completeness contract, referencing the rule. |
| H4 | CI Pipelines | runbook | [`docs/ci-pipelines/workflows-runbook.md`](../../ci-pipelines/workflows-runbook.md) | The new `dash-e2e` ubuntu job (deterministic per-harness conformance spec) and the pinned local command. |

No ADR — ADR waiver: no new boundary, schema, dependency, or surface; this formalizes the
existing matrix (harvested from the archived 2026-10-27 plan), the existing OTel-first precedence
(ADR 0029), and the existing honest-unobservability/secondary-cost-display rules. No
documentation waiver (canonical docs are updated in the closeout task).

## 5. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 (docs standard) | No product test — rule doc | `dotnet run --project src/KyberWeave.Cli -- docs validate .` then `... -- docs drift .` | New rule is reachable from `docs/rules/README.md`; correct `doc-type: rule` frontmatter; no new `KW-*` id; no ontology widening. | `docs validate .` reports `KW-DOC-LIFECYCLE-001` (new rule unreachable) before the index row is added. | Both commands exit 0, zero findings. |
| T1 (RED conformance spec) | `dash/e2e/harness-collection.spec.ts` (new) | `npx playwright test dash/e2e/harness-collection.spec.ts` | Completeness predicate encoded per harness: measured group renders `dimension-value` and no lying zero; honest-empty group renders a visible session with `dimension-unmeasurable` + reason; zero-record group renders honest-empty ("No runs recorded"), never claimable as complete. | Spec committed first, run → fails: it imports/seeds a fixture world that does not exist yet (0 passing / build-or-seed failure). | Same spec GREEN after T2's world is seeded. |
| T2 (fixture world) | `dash/e2e/harness-collection-world.ts` (new) + the T1 spec | same command | Seeded temp `canon.db` + booted dashboard (T8 pattern; never the live 4747 host) with one representative session per matrix group: measured (`claude-code` measured counters), honest-empty (`opencode` plugin counter-less → `not_measurable`), zero-record (a never-configured harness). | RED already captured at T1; T2 is the green half of the same spec. | `npx playwright test dash/e2e/harness-collection.spec.ts` GREEN. |
| T3 (CI job) | `.github/workflows/ci.yml` (new `dash-e2e` job), `docs/ci-pipelines/workflows-runbook.md` | job runs `npx playwright test dash/e2e/harness-collection.spec.ts` after `npx playwright install --with-deps` | Deterministic ubuntu job enforces the completeness predicate continuously; no macOS, no funded model, no new secret. | No product RED (CI wiring) — verification: `docs validate .`/`docs drift .` zero findings on the runbook edit; local `npx playwright test dash/e2e/harness-collection.spec.ts` green. | Job defined and reachable; both docs commands exit 0; local spec green. |
| T4 (docs closeout) | No product test — canonical docs | `dotnet run --project src/KyberWeave.Cli -- docs validate .` then `... -- docs drift .` | Canonical docs carry the standard and the hierarchy; completeness cannot be claimed without the named evidence. | `docs validate .`/`docs drift .` would flag an unreachable/incorrect edit until the harvest settles. | Both commands exit 0, zero findings; closeout mapping complete. |

No assertion is weakened; RED for the code-bearing scope (T1→T2) is a genuine failing contract
test written before its fixture world, matching the archived coverage plan's T0→T1 shape.

## 6. Dispatchable tasks

### T0 — Author the per-harness collection standard (docs-dev)
- **Objective:** Author the durable rule at `docs/rules/kyberdash-harness-collection.md` and add
  its entry to `docs/rules/README.md`. The rule states, per harness (the §3.2 matrix rows): what
  must be collected, what the dashboard must show (measured vs honest-empty with the reason),
  the completeness predicate (D-B), and the collection-mechanism hierarchy (D-A context:
  OTel/hooks/DB reads PRIMARY; file ingest backup, never billed primary). No new `KW-*` rule id;
  no ontology/doc-type/component widening.
- **Files:** `docs/rules/kyberdash-harness-collection.md` (new), `docs/rules/README.md` (add row).
- **Acceptance:** `docs validate .` and `docs drift .` exit 0 with zero findings; the rule reads
  from the rules index; base keys + `doc-type: rule`.
- **Dependencies:** none.
- **Skills:** docs-dev, kyber-weave-docs.

### T1 — RED: per-harness completeness conformance spec (test-dev)
- **Objective:** Write `dash/e2e/harness-collection.spec.ts` encoding D-B's completeness
  predicate against a fixture-world contract (`harness-collection-world.ts`, created in T2,
  following the `refresh-filters.spec.ts` / `refresh-filters-world.ts` T8 pattern). Assert the
  measured / honest-empty / zero-record groups per §5. Run it and capture RED.
- **Files:** `dash/e2e/harness-collection.spec.ts` (new).
- **Acceptance:** `npx playwright test dash/e2e/harness-collection.spec.ts` fails for the
  intended missing world (RED evidence captured); no pre-existing spec is modified.
- **Dependencies:** none (encodes D-B straight from this plan).
- **Skills:** test-dev.

### T2 — GREEN: fixture world for the matrix (test-dev / typescript-dev)
- **Objective:** Seed a self-contained temp `canon.db` and boot the dashboard (T8 pattern, never
  the live 4747 host) with one representative session per matrix group, then make the T1 spec
  pass.
- **Files:** `dash/e2e/harness-collection-world.ts` (new); reuse `dash/e2e/refresh-filters-world.ts`
  helpers where possible.
- **Acceptance:** `npx playwright test dash/e2e/harness-collection.spec.ts` GREEN.
- **Dependencies:** T1.
- **Skills:** test-dev, typescript-dev.

### T3 — Wire the conformance spec into CI (github-devops / docs-dev)
- **Objective:** Add a `dash-e2e` job to `.github/workflows/ci.yml` (ubuntu-latest, `needs:
  ts-gates`): install dash deps, `npx playwright install --with-deps`, run the conformance spec.
  Document the job in `docs/ci-pipelines/workflows-runbook.md`. No macOS, no funded model, no new
  secret (D-A/D-C).
- **Files:** `.github/workflows/ci.yml`, `docs/ci-pipelines/workflows-runbook.md`.
- **Acceptance:** Job defined and reachable in the workflow; `docs validate .`/`docs drift .`
  zero findings; local `npx playwright test dash/e2e/harness-collection.spec.ts` green.
- **Dependencies:** T2.
- **Skills:** github-devops, docs-dev.

### T4 — docs-dev closeout (docs-dev)
- **Objective:** Harvest the standard's durable claims: add a collection-mechanism-hierarchy
  subsection and completeness-contract reference to `docs/dash/architecture.md`; add the rule
  cross-reference and PRIMARY/BACKUP hierarchy statement to
  `docs/dash/telemetry-inventory.md`. Keep `docs/todo/opencode-plugin-token-usage-capture.md`
  deferred (D-D). Record the D-A/D-C deferred "CI macOS funded live-model turn" as a follow-up
  todo. Publish the closeout mapping (§9).
- **Files:** `docs/dash/architecture.md`, `docs/dash/telemetry-inventory.md`,
  `docs/todo/opencode-plugin-token-usage-capture.md` (may append a deferred-item note; no change
  to its behaviour today), `docs/todo/README.md` (index row for any new todo).
- **Acceptance:** `docs validate .` and `docs drift .` exit 0 with zero findings; closeout mapping
  complete; no ontology/doc-type/component/rule-id widening.
- **Dependencies:** T0, T3.
- **Skills:** docs-dev, kyber-weave-docs.

## 7. Dependency graph

```
T0 ─────────────┐
                ├──> T4
T1 ──> T2 ──> T3 ┘
```

- T2 consumes T1 (the RED spec defines the target world). T3 consumes T2 (the green spec the CI
  job invokes). T4 consumes T0 (the standard to harvest) and T3 (the CI job to document).
- T0 and T1 run in parallel: they share no files (`docs/rules/**` vs `dash/e2e/**`) and T1 does
  not consume T0's text (it encodes D-B from this plan). Max observed concurrency 2, within
  `review.max-concurrent-workers`.

## 8. Risks and out-of-scope boundaries

**Risks.**
- **Over-broad "complete" bar** could mark a legitimate capture-side stop (pi/Copilot/Antigravity
  exporters stopped) as a product defect. Mitigation: the standard ties completeness to the
  projection/display contract, not to whether an external exporter is running; capture-side stops
  stay documented environment facts (the matrix already says so), and the "last emitted" signal
  stays deferred.
- **Fixture drift** — the conformance spec seeds representative sessions; a future harness change
  could make a fixture stale. Mitigation: the fixture world is versioned alongside the standard
  (same plan), and the spec asserts the matrix, which is the source the standard owns.
- **CI flake** — Playwright in CI adds browser install time and a long-running server. Mitigation:
  mirror the existing T8 self-contained boot (no live host), `workers: 1`, and reuse
  `reuseExistingServer: !CI` semantics.

**Out of scope (kept deferred).**
- Real opencode plugin token-usage capture and per-source "last emitted" status
  (`docs/todo/opencode-plugin-token-usage-capture.md`) — D-D.
- The CI macOS + funded live-model turn per harness — D-A/D-C (new follow-up todo, no provider or
  model named).
- Supervised "complete" wording in any dashboard copy — the standard governs what must be shown,
  not marketing copy; product copy stays free of decision ids (spine G5 unchanged).

## Closeout mapping

Scale: `extends-component`; impact components `KyberDash` and `CI Pipelines`. No ADR — ADR
waiver: this formalizes the existing matrix (harvested from the archived 2026-10-27 plan), the
existing OTel-first precedence (ADR 0029), and the existing honest-unobservability /
secondary-cost-display rules with no new boundary, schema, dependency, or surface.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D-INTAKE | [rule, harness collection standard](../../rules/kyberdash-harness-collection.md); [telemetry inventory](../../dash/telemetry-inventory.md) | extends-component | KyberDash | 2026-10-28 | archived |
| D-A | [architecture, the shared canonical projection](../../dash/architecture.md); [runbook, dash-e2e](../../ci-pipelines/workflows-runbook.md) | extends-component | KyberDash, CI Pipelines | 2026-10-28 | archived |
| D-B | [rule, completeness predicate](../../rules/kyberdash-harness-collection.md) | extends-component | KyberDash | 2026-10-28 | archived |
| D-C | [todo, macOS funded live-model turn](../../todo/kyberdash-macos-funded-live-model-turn.md) (the deferred verification, no provider or model named) | extends-component | KyberDash | 2026-10-28 | archived |
| D-D | [todo, OpenCode plugin token usage capture](../../todo/opencode-plugin-token-usage-capture.md) (capture-side enhancement stays deferred) | extends-component | KyberDash | 2026-10-28 | archived |
| D-E | [rule, cost and rate exclusion](../../rules/kyberdash-harness-collection.md) (no published rate is honest-empty) | extends-component | KyberDash | 2026-10-28 | archived |
| H1 | [rule, harness collection standard](../../rules/kyberdash-harness-collection.md); [rules index](../../rules/README.md) | extends-component | KyberDash | 2026-10-28 | archived |
| H2 | [telemetry inventory, per-harness OTel collection matrix](../../dash/telemetry-inventory.md) | extends-component | KyberDash | 2026-10-28 | archived |
| H3 | [architecture, the shared canonical projection](../../dash/architecture.md) | extends-component | KyberDash | 2026-10-28 | archived |
| H4 | [runbook, dash-e2e](../../ci-pipelines/workflows-runbook.md) | extends-component | CI Pipelines | 2026-10-28 | archived |
| AP1 | **Documentation waiver:** approval provenance for this run is transient and is recorded in the Decisions table. | extends-component | KyberDash | 2026-10-28 | archived |
| CB1 | **Documentation waiver:** circuit-breaker provenance (stale spec mechanism, contract unchanged) is transient and is recorded in the Decisions table. | extends-component | KyberDash | 2026-10-28 | archived |
| CB2 | **Documentation waiver:** circuit-breaker provenance (built-server deep-link defect parked as an owner-accepted follow-up proposal) is transient and is recorded in the Decisions table. | extends-component | KyberDash | 2026-10-28 | archived |

## Discovery report

- CodeGraph MCP (`codegraph_explore`): **unavailable** — no such tool exposed in this harness.
  `.codegraph/` is present and the `codegraph` CLI is available, but the code questions were
  answered with narrow direct reads (fallback), self-gathered.
- Docs MCP (`docs_explore` / `docs_glossary` / `docs_for_symbol`): **unavailable** — no
  Kyber-Weave MCP tools exposed; fell back to the documentation index and direct reads.
- `Discovery: codegraph 0, docs 0, direct reads 21`. Direct reads of indexed code/source and the
  reason for each: `dash/playwright.config.ts` (confirm e2e webServer + CI anticipation),
  `dash/e2e/spine.spec.ts` (existing G3/G4a/G6 dashboard checks), `dash/e2e/refresh-filters.spec.ts`
  (T8 self-contained fixture pattern to reuse), `dash/src/synth/synth.ts:356-378` (costBlockFor —
  D-E rate finding), `dash/src/canon/published-pricing.ts` (pricePublishedTurn — D-E rate finding),
  `.github/workflows/ci.yml` (4 sections — macOS runners, ts-gates scope, no Playwright job, no
  model-funded job), `.github/workflows/release.yml` (grep — no model-funded job, macOS-14
  builds), `dash/AGENTS.md` (dash gates). Docs/canonical reads (indexed by the docs corpus, not
  code): `docs/dash/telemetry-inventory.md`, the archived 2026-10-27 plan §3.2,
  `docs/todo/opencode-plugin-token-usage-capture.md`, `docs/rules/kyberdash-display-layer.md`,
  `docs/rules/honest-unobservability.md`, `docs/rules/secondary-cost-display.md`,
  `docs/catalog.md`, `docs/rules/README.md`, `docs/plans/README.md`, the plan-authoring /
  autonomy-decisions / test-first references, and the `kyber-weave-docs` skill + ontology grep
  (frontmatter keys).