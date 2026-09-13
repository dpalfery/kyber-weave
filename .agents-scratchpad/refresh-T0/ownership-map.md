# T0 ownership map — KyberDash harness-source refresh

Inspected 2026-09-12. No production/test/docs edits. docs MCP miss on this plan; T0 row read from `docs/plans/2026-09-06-kyberdash-refresh-pipeline.md`.

## Tree facts

- Live `SCHEMA_VERSION` in `dash/kyber/canon/store.ts:64` is **10**. T2 bumps from 10, not 9. `source-state.ts` does not exist.
- `dash/kyber/refresh/` **does not exist** (0 files). T1+ create the Kyber-owned adapter here.
- Inspected CLI/synth files are **clean vs HEAD** (committed on `main` in `3ffad26a`). They are not uncommitted dirty. Plan wording “uncommitted refresh edits” is stale; salvage from this committed partial `dash refresh`.
- Working tree is otherwise dirty (frontend, ADRs, spine plan, etc.). Those paths are user-owned.

## 1. Per inspected file

### `dash/kyber/cli/register.ts`

**Salvage**
- `dash` group + `dash refresh` command placement (`registerKyberCommands`, not `kyber refresh`, not top-level `refresh`).
- `resolveDbPath` → `~/.kyberdash/canon.db`; `--db` option; `CanonStore` open/close in `try/finally`.
- Other `kyber` commands (otel, backfill, renormalize, build, cursor-hook) — out of T1–T10 scope; do not rewrite.

**Rework**
- Public `--provider <name>` (lines 137–145). Plan: all harness jobs run; inject source selection in tests only.
- Aggregate-only stdout (`Providers/Sources/Synthesized/...`). Replace with per-harness table + derived summary + exit 0/1/2.
- Missing `--history-weeks` (default 2; validate before opening DB).
- Description still says “local provider sessions”, not harness-source jobs.

### `dash/kyber/cli/refresh.ts`

**Salvage (concepts, not the scheduler shape)**
- `RefreshDependencies` / `getAllProviders` injection for tests.
- Per-source persist (`ingestSource` → validate → `upsertMany`) instead of holding the whole corpus.
- Discovery failure continues (`continue` after `recordProblem`); do not abort sibling jobs.
- `expandSourceFiles` Claude directory→transcript expansion (until `dash/kyber/refresh/**` owns Claude’s special parse).
- OTLP write-volume idea: do not re-upsert untouched OTLP per source. **Do not keep** the current full-corpus `store.streamOtlpSourced()` index on every pass.

**Rework (replace the body)**
- Sequential `getAllProviders()` loop = provider objects, not one job per harness source type.
- No `--history-weeks` / `DateRange`; parses whole sources; Claude `createSessionParser` empty → no Claude calls.
- Aggregate `LocalProviderRefreshReport`; `PROVIDER_REFRESH_ERROR` not per-harness status/exit.
- Corpus-wide OTLP stream + `buildSessions` over entire store after sequential loop.
- No bounded parallel jobs, checkpoints, provenance, or writer queue.

Green `refresh.test.ts` is **not** this plan’s gate.

### `dash/kyber/cli/refresh.test.ts`

**Salvage**
- Temp `CanonStore` fixture pattern; `RefreshDependencies` injection.
- Write-volume idea (OTLP row not rewritten per source) — retarget after T2/T6, do not keep gemini/listAll coupling as the contract.
- Dual-path collapse (file + OTLP → one run) is still a product invariant; rewrite against split harness ids.

**Rework (replace expectations)**
- Happy path expects merged `antigravity` + **`gemini`** harness/source identities (`codeburn/gemini`). Plan: Gemini is not a coding harness.
- Sequential provider loop + continue-on-parse-failure only. Missing: window, checkpoint, split identities (`antigravity-cli`/`-ide`), concurrency, exit 1/2, per-row table.
- Do not treat current green as acceptance.

### `dash/kyber/cli/register.test.ts`

**Salvage**
- Asserts `dash refresh` exists and is **not** under `kyber`. Keep.
- `cursor-hook` test is unrelated; do not drop.

**Rework**
- Does not pin `--history-weeks`, absence of public `--provider`, usage-error exit 2, or summary shape. T1/T8 extend; do not delete the placement test.

### `dash/kyber/synth/provider.ts`

**Salvage**
- `ingestProviders` as the common record-level ingest seam.
- R1.2/R1.3: ENOENT silent; parse errors → `PROVIDER_PARSE_ERROR` + continue.
- `ProviderLoad` `{ calls, filePath }`; `parseProblem`/`fileOf`; Copilot/Claude empty-parser fallbacks in `callsAndTurns`.
- `PROVIDER_READERS` map.

**Rework**
- Loader is still **provider-name** keyed, not source-unit / harness-job keyed. Make errors source-unit aware (native path + harness id).
- Reader lookup by upstream `provider` string (`claude`, `copilot`) will collide with split ids (`claude-cli`, `copilot-vscode`, …).

### `dash/kyber/synth/synth.ts`

**Salvage**
- Keep `ParsedProviderCall` import/re-export (`../../src/providers/types.js`) if synthesis still takes upstream calls.
- `synthesizeCall` sessionId carry (dual-path grouping).
- `Synthesizer` serial/parallel paths; harness currently `call.provider` — T3/T6 must classify before/at this seam, not delete synthesis.

**Rework**
- Not a scheduler file. Only change if harness identity must stop being raw `call.provider`. No T0 rewrite.

## 2. Unrelated dirty paths — T1–T10 MUST NOT revert

Do not `git checkout`, restage-to-undo, reformat, or bundle into refresh commits.

**Frontend (user / spine Q5Q6; T8 must not take `App.tsx`)**
- `dash/dash/src/App.tsx`, `App.test.tsx`
- `dash/dash/src/components/ContextInspector.tsx` + `.test.tsx`
- `dash/dash/src/components/SessionInspectorDrawer.tsx`
- `dash/dash/src/components/kyber/*` (deleted CompareView/ContextView/SchemaView; Scorecard/FindingList/TurnAlignedDiff/DerivedCaveat/ContextReviewPanel/index/kyber-views tests; **untracked** `ScorecardMatrix.tsx`)
- `dash/dash/src/lib/kyberApi.ts`
- `dash/dash/src/pages/{Attention,CompareRuns,FindingDetail,HarnessDetail,RunDetail,TurnDetail}.tsx` + FindingDetail tests
- `dash/eslint.config.js`

**Kyber merge-zone but not this pipeline’s T0–T7 CLI/store work**
- `dash/kyber/server/bridge.ts`, `dash/kyber/server/routes.ts` (T8 API later; do not revert current dirty)
- `dash/kyber/web/components/DerivedCaveat.tsx`

**Docs / ADRs / plans / todos (user-owned; T9 is pipeline docs closeout only, not spine harvest)**
- `docs/adr/0012`–`0015` (modified), `docs/adr/README.md`
- **untracked** `docs/adr/0016-kyberdash-harness-source-refresh.md`, `docs/adr/0017-copilot-deterministic-tool-order.md`
- `docs/dash/architecture.md`, `docs/dash/runbook.md`
- `docs/kyber-squad/README.md`, `docs/kyber-squad/architecture.md`
- `docs/plans/2026-09-06-kyberdash-refresh-pipeline.md` (this plan — do not revert)
- `docs/plans/2026-09-06-kyberdash-spine.md`, `docs/plans/README.md`
- archive moves: `docs/archive/plans/2026-09-04-...`, `2026-09-05-...` (renamed), `2026-08-29-...` (deleted from plans, untracked in archive)
- `docs/todo/README.md`, `docs/todo/kyber-squad-renderer-coverage.md`, **untracked** `docs/todo/pi.md`

**Scratch (not product)**
- `.agents-scratchpad/` (untracked) — do not commit unless asked.

Not currently dirty: Playwright, `docs/dash/telemetry-inventory.md`. If they appear later, still user-owned unless T9/T10 owns them.

## 3. `dash/kyber/refresh/` exists?

**No.**

## 4. Live `SCHEMA_VERSION`

**10** (`dash/kyber/canon/store.ts:64`).

## Call graph (partial, confirmed)

`registerKyberCommands` → `refreshLocalProviders` → `ingestSource` → `ingestProviders` → `Synthesizer`.

Missing vs plan: `--history-weeks`, harness-source registry/jobs, `dash/kyber/refresh/**` adapter, checkpoints/provenance, bounded scheduler, per-harness report/exit.

## OPEN_QUESTIONS

None blocking T1. Note only: plan “uncommitted” vs tree “committed on main” — treat files as salvage sources, not as a dirty patch to restore.
