---
id: plans/2026-10-22-opencode-span-turn-hooks-kyberdash
title: "opencode span-turn hooks to KyberDash (Desktop + CLI v2, one codebase)"
doc-type: plan
status: draft
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-22
development-mode: test-first
---

# opencode span-turn hooks to KyberDash (Desktop + CLI v2, one codebase)

**Status: Ready — decision-complete, executable (revision R2 approved human 2026-10-22 `Approve R2 (Recommended)`, gate id R2 approve-and-execute; T8 e2e restated to version-pass + attribution + quarantined==6 non-model with zero fabricated counters + session anchoring, not accepted; R1 prior authority retained as history, AP1 superseded history; frontmatter `status: draft` unchanged per closed ontology — body Ready is authoritative; docs validate/drift outstanding G3, no exec tool in harness — conductor runs both checks before execution).**
**Development mode:** `test-first` (the default; no opt-out supplied).
**Autonomy:** `autonomous` (resolved from repository root `AGENTS.md` Config Reg `<autonomy>`).

## 1. Problem and goal

### 1.1 Request

Author a Draft plan for an **opencode plugin (Desktop + CLI v2)** that uses **hooks** to collect
**span and turn data** for **Kyber-Weave Dash (KyberDash)**, from **one codebase** for both
surfaces (packet CHOICE: scope = both surfaces, one codebase; explicit plan mode, intake
Promotion, no assessment).

### 1.2 Goal

Ship a single in-repo plugin source that, installed into opencode Desktop and opencode CLI v2,
emits per-span (start/end, tokens, tools, errors) and per-turn records with stable session
identity, delivered into the existing KyberDash ingest path (shared engine → `canon.db` →
`/api/kyber/*` → both display surfaces), with privacy/redaction defaults, offline tolerance,
contract versioning, and telemetry-inventory coverage — without violating any repository
non-negotiable (§8).

### 1.3 What this plan does not do

- It does **not** execute. This task was plan-only; implementation was gated on the
  approve-and-execute gate (§9), re-approved human 2026-10-22 (R1
  approve-and-execute; AP1 superseded as execution authority, retained as history)
  — the plan was Ready and executable under R1; revision R2 (2026-10-22, T8 e2e
  quarantine restatement) returned it to Draft (re-approval pending; NOT executable).
- It does **not** change `dash/web/**` or `dash/tray/**` behavior: both are display layers
  (see §3.1). All new logic lives in the shared application / plugin source the engine owns.
- It does **not** widen the documentation ontology, renumber any `KW-*` rule id, or weaken
  `TreatWarningsAsErrors` / `AnalysisMode=all`.

## 2. Investigation findings

Discovery: `codegraph 5, docs 3, direct reads 7`. Direct-read reasons below; every other
cited source comes from the index tools and is treated as already read.

Revision R1 (2026-10-22): `codegraph 1, docs 1, direct reads 5`. Codegraph confirmed the
`plugin.ts` assembly surface (`createOpencodePlugin` / `registerOpencodePlugin` entry,
import-free installed entry reached via the `opencode.ts` declare-only re-export; mapper /
turn / outbox / sender internal-edges-only). Docs confirmed the `check:reachable`
sanctioned-remedy posture via this plan's §11 + the plan index. Direct reads (5): this plan
file, `docs/plans/README.md`, and the three authoring references (`plan-authoring.md`,
`autonomy-decisions.md`, `test-first-contract.md`) to execute the in-place contract
revision. No other plans/specs opened per packet; packet mediation finding taken as
authoritative.

Revision R2 (2026-10-22): `codegraph 1, docs 1, direct reads 2`. Codegraph confirmed the
quarantine taxonomy supporting the restated e2e (`opencode-envelope.ts` version guard +
marker-prefix envelope, marker-less app span quarantining as non-model; `mapper.ts`
`mapOpencodePayloadToOtlpSpan` carries session identity verbatim, never synthesized).
Docs confirmed root (`docs_status` `root=/Users/dave/git/personal/claude/kyber-weave
rev=5ae3be88`). Direct reads (2): this plan file (in-place R2 contract revision target)
and `docs/plans/README.md` (index row sync). No other plans/specs opened per packet;
human decision 2026-10-22 taken as authoritative.

- **Codegraph 1 — opencode hook/span surface:** `dash/src/canon/adapters/opencode.ts`
  (`isModelSpan`, OpenInference `llm.*`/`tool.*` shapes, `opencode.llm → llm.invoke`,
  `Tool.execute → tool.invoke`, non-model quarantine); `dash/src/providers/opencode.ts` +
  `opencode-file-parser.ts` + `sqlite-session-parser.ts` (merged legacy-JSON + SQLite
  session sources, `OPENCODE_DATA_DIR` / `OPENCODE_DB_PREFIX` seams);
  `products/kyber-utilities/statusline/commandcode/statusbar.ts` (in-repo hook precedent:
  `cmd.hooks({ onSessionStart, onTurnEnd })`, best-effort never-fail-host pattern).
- **Codegraph 2 — store/API boundary:** `dash/src/canon/paths.ts` (`resolveCanonDbPath`:
  explicit > `KYBER_CANON_DB` > `~/.kyberdash/canon.db`, the single answer for which file);
  `dash/src/canon/ingest.ts` (`ingestBatch` / `ingestBatchWithAdapters`, `IngestOutcome`,
  quarantine vs rejection accounting, `MAX_QUARANTINE_RAW_PAYLOAD_CHARS`); `dash/web/src/lib/kyberApi.ts`
  (`/api/kyber/*`, `/api/kyber/capture` dry-run/refusal contract).
- **Codegraph 3 — capture ownership:** `dash/src/capture/harnesses/registry.ts` (six-harness
  registry, managed vs pending writers, per-file declaration seam); `…/capture/index.ts`
  (`DEFAULT_CAPTURE_ENDPOINT = http://127.0.0.1:4318`, receipted enable/disable);
  `dash/src/capture/harnesses/opencode.ts` (managed writer: `opencode.jsonc`
  `experimental.openTelemetry=true`, endpoint via `OTEL_EXPORTER_OTLP_ENDPOINT` process env,
  never written to config).
- **Codegraphs 4–5 — opencode sides:** capture `opencodeHarness` detail; file/SQLite
  provider detail (dedup keys `${provider}:${sessionId}:${messageId}`); `claude-code.ts`
  content-consent keys (content is sender opt-in).
- **Docs 1 — status:** `docs_status` answered
  `root=/Users/dave/git/personal/claude/kyber-weave rev=5ae3be88`, matching this checkout.
- **Docs 2 — architecture/runbook/inventory/requirements:** `dash/architecture.md` (surface
  layer, jobs/shared settings, receiver child, `kyberdash web` owns `JobHost`/`ReceiverHost`,
  single `ContextReport`); `rules/kyberdash-display-layer.md` + `adr/0033`; `dash/runbook.md`
  (§1 receiver `127.0.0.1:4318` `POST /v1/traces` + `POST /v1/logs`, `kyber build`/`dash refresh`,
  retention); `dash/telemetry-inventory.md` (collector contract, capture ownership per ADR 0031,
  OpenCode OTLP log-correlation gap); `dash/requirements.md` (KD-011 display-layer guard).
- **Docs 3 — hooks precedent:** `adr/0009` notes Cursor's usable signal comes from agent hooks,
  not its exporter; `todo/kyberdash-opencode-log-correlation.md` (OpenCode `/v1/logs` rows
  still quarantine — the exact gap hooks can close); `todo/pi-emitter-tool-spans.md`
  (external-emitter boundary is deliberate; bringing an emitter in-repo wants an ADR).
- **Direct reads (7), each for what the indexes do not cover:**
  1. `docs/plans/README.md` — Active Plans table shape for the required index row (§10).
  2. `products/kyber-squad/agents/architect/references/plan-authoring.md` — required plan
     contract (mode contract, Decisions table, Ready/Finalized markers).
  3. `…/architect/references/autonomy-decisions.md` — three-checks + impactful-escalation
     block format for `autonomy: autonomous`.
  4. `…/architect/references/test-first-contract.md` — Test-contract row shape and
     RED-before-GREEN sequencing.
  5. `docs/documentation-ontology.md` — `plan` required-key matrix (base keys + `component`).
  6. `docs/catalog.md` — `KyberDash` component / `dpalfery` owner vocabulary.
  7. `docs/archive/plans/2026-10-21-commandcode-collector-kyberdash.md` (≤120 lines) —
     roven plan frontmatter/body precedent (mode + autonomy persistence, Decisions usage).

### 2.1 What the checkout already gives the plugin

1. **A receiver to aim at.** OTLP/HTTP on `127.0.0.1:4318`, `POST /v1/traces` and
   `POST /v1/logs`, owned by the engine (`ReceiverHost` child of `kyberdash web`);
   `IngestWriter` → `ingestBatch` → `CanonStore.upsertMany` in one transaction per batch.
2. **An adapter seam.** `HarnessAdapter` (`dash/src/canon/adapters/base.ts`) + `AdapterRegistry`;
   the `opencode` adapter already claims OpenInference/`ai.*`/`gen_ai.*` spans and maps
   model vs tool ops; unclaimed or `op == unspecified` rows quarantine with evidence.
3. **A store with one address.** `resolveCanonDbPath` is the only resolver; the plugin must
   never name a second path.
4. **A capture command that owns keys.** `kyberdash kyber capture` (+ `POST /api/kyber/capture`
   with dry-run preview and 409 refusals) writes only declared keys with receipts; the
   opencode writer writes one boolean and reads the endpoint from process env.
5. **A hooks precedent.** The CommandCode statusline mod shows the in-repo hook shape
   (`onSessionStart` / `onTurnEnd`, capability-tolerant, swallow-and-continue so a hook
   never fails the host).
6. **A contract-versioning precedent.** Parser/repair contract bumps invalidate checkpoints
   (`PARSER_CONTRACT_VERSION` pattern; opencode log-correlation todo names the same need).

### 2.2 GAPS (what the checkout does not contain)

- **G1 — opencode Desktop + CLI v2 hook/plugin API.** No in-repo spec for the external
  surface this plugin implements against (hook names, event payloads for span start/end,
  token/tool/error/session fields, Desktop-vs-CLIv2 packaging and install path). The plan
  assumes the owner (or a `research-agent` web pass at execution time) supplies the API
  reference; T0 records it before any emission code.
- **G2 — live opencode Desktop/CLI v2 fixtures.** No captured hook payloads to pin the
  mapper; T1–T3 carry owner live-capture gates.
- **G3 — validation toolchain in this harness.** No shell/process tool was exposed, so
  `docs validate` / `docs drift` could not be run during authoring (see §9). They must run
  before any `PLAN_READY` claim.

## 3. Architecture

### 3.1 Non-negotiable placement

- **Display-layer rule.** The tray and web dashboard stay display-only. The plugin is a
  **sender** (like any harness exporter), not a surface: it emits to the shared
  application's receiver; it schedules nothing, cleans nothing, imports nothing, pauses
  nothing. Enforcement stays green: `dash/src/architecture/display-layer.test.ts`.
- **One store, one API.** The plugin writes no database directly and serves no API. Rows
  reach `canon.db` only through the receiver → `ingestBatch` path addressed by
  `resolveCanonDbPath`, and are read only through `/api/kyber/*`.
- **First-party dash, dash-CLI-installed.** The plugin source lives in this repository under
  dash capture/harness ownership and installs/configures via the dash CLI (`kyberdash kyber
  capture` managed-writer / registry pattern, like the other harnesses) — not a standalone
  package home with its own dual-target manifests (Q1 custom direction, human 2026-10-22).
  No upstream merge, no new third-party runtime dependency without justification (Core takes
  only Markdig + YAMLDotNet; the dash side avoids new native deps — the `sqlite3` CLI
  precedent stands).
- **No ontology widening.** New docs use existing `doc-type`/`component` vocabulary;
  `KW-*` ids are never renumbered or reused.

### 3.2 Data flow

```text
opencode Desktop / CLI v2 ──hooks──▶ plugin hooks/config installed via dash CLI
        │ (capture registry + managed writer, same as other harnesses; one codebase)
        │ span start/end, tokens, tools, errors, session identity
        │ OTLP/HTTP → http://127.0.0.1:4318 (/v1/traces + /v1/logs)   [Q2 confirms]
        ▼
ReceiverHost child (OtlpReceiver) → IngestWriter → ingestBatch(+Adapters)
        ├─ claimed model/tool records → CanonStore → projection → /api/kyber/*
        └─ unclaimed / non-model / unmatched logs → quarantine (counted, with raw_payload)
```

Checkpoint/contract versioning rides the existing pattern: a plugin contract version the
ingest side checks, so a payload-shape change invalidates stale checkpoints instead of
silently misreading them (Q3 decided span-anchored 2026-10-22; version key lands in T1).

### 3.3 Privacy / redaction (decided, D5)

Content (prompts, tool arguments/results, message history) is **off by default**; counters
(model, tokens, tool names, error codes, timing, session identity) always flow. Content
flows only under explicit sender opt-in, following the Claude-Code precedent
(`OTEL_LOG_USER_PROMPTS`-style consent owned by the sender, never auto-enabled by
`kyber capture`). Assembled text honors the 14-day content purge; raw packets stay scoped
per harness wipe rules (ADR 0035 pattern).

### 3.4 Failure modes (designed, not deferred)

- **Receiver down / offline:** plugin spools boundedly to a local outbox (size + age caps),
  retries with backoff and jitter, drops oldest-first with a counted `dropped_spans` metric
  surfaced in plugin status; never blocks the host session (the CommandCode swallow pattern).
- **Backpressure (429 / slow drain):** same outbox; batch size halves on 429, recovers
  additively; no unbounded memory growth.
- **Clock skew / late spans:** ingest correlation windows unchanged; late logs park in
  `pending_logs` under the existing TTL rather than inventing a new table.
- **Version drift:** unknown plugin contract version quarantines with a named reason, never
  misattributes.

## 4. Task graph

File scopes are optimistic per the plan-authoring reference; a task overruns its scope only
by escalating. Every task ends with the worker's own `task-gate` run; a failing gate goes
straight back to the same worker. `Q1` decided 2026-10-22 (human): no standalone package
home — hooks/config live under dash capture/harness ownership and install via the dash CLI
managed-writer / registry pattern like the other harnesses; `T2–T5` path prefix follows
that home as a whole; task boundaries do not move.

| Task | Objective | Files / symbols (exact scope) | Depends on | Skills |
|---|---|---|---|---|
| T0 | Pin the external contract: opencode Desktop + CLI v2 hook names, event payloads (span/turn/session/tokens/tools/errors), install/packaging targets, Desktop-vs-CLIv2 deltas; record as plan addendum + fixture skeletons | New fixtures under chosen plugin home (`fixtures/*.json`); `docs/dash/telemetry-inventory.md` (survey note only) | — | docs-dev |
| T1 | Ingest contract + version key: plugin span/turn envelope (RED: unknown-version quarantine + checkpoint invalidation), adapter claim rules for the new envelope | `dash/src/canon/adapters/opencode*.ts`, `dash/src/canon/adapters/base.ts` (registration only), `dash/src/canon/ingest.ts` (version check only), tests `dash/src/canon/adapters/opencode*.test.ts` | T0, Q1–Q3 | test-dev |
| T2 | Plugin skeleton + hook lifecycle: session start/end, turn boundaries, span start/end emission, never-fail-host guarantee, one codebase installed via dash CLI | Dash-CLI-managed home per Q1 (hook source + opencode config keys + managed-writer install path; no standalone package home); tests colocated (`*.test.ts`) | T0, Q1 | test-dev |
| T3 | Span mapping: model/tool spans with tokens, timing, errors, session identity; non-model filtering per adapter rules | Plugin mapper module; adapter unit tests extended; fixtures from T0 | T1, T2 | test-dev |
| T4 | Turn mapping + log correlation: turn records join spans; tool outcomes ride `/v1/logs` correlated by `(trace_id, span_id)` then session+bounds (ADR 0009 rule), idempotent replay | Plugin turn module; `dash/src/canon/ingest.ts` log path (only if the existing correlator needs the new envelope — otherwise plugin-side only); tests for correlation + idempotency | T1, T3 | test-dev |
| T5 | Transport + offline/backpressure: OTLP/HTTP sender, bounded outbox, retry/backoff/jitter, drop-oldest accounting, status surface | Plugin transport module + outbox; no engine changes (receiver unchanged) | T2, Q2 | test-dev |
| T6 | Config + capture ownership: managed `kyber capture` keys for the plugin (declare-only keys, receipted writes, dry-run/refusal parity), `POST /api/kyber/capture` + CLI parity, telemetry-inventory row, runbook notes | `dash/src/capture/harnesses/opencode*.ts`, `dash/src/capture/**` tests, `docs/dash/telemetry-inventory.md`, `docs/dash/runbook.md` (notes only) | T0, T5, Q2 | test-dev, docs-dev |
| T8 | Pipeline assembly: wire hook→mapper→turn→outbox→sender in the installed entry (`plugin.ts`) or sanctioned build step so all four F1 modules are legitimately loaded; preserve Q1 / Q2a / Q3a + display-layer / one-store / one-API, no web/tray change | Dash-CLI-managed home per Q1: `dash/src/capture/harnesses/opencode-plugin/plugin.ts` (installed-entry assembly; legitimately imports mapper / turn / outbox / sender), sanctioned build step if any (no new package home, no exemption); tests colocated (`*.test.ts`) | T3, T4, T5 | test-dev |
| T7 | Validation + closeout: full gates, display-layer guard, review, harvest mapping (§11) | No source; `docs/plans/2026-10-22-opencode-span-turn-hooks-kyberdash.md` (§11), `docs/plans/README.md` row | T1–T8 | docs-dev |

Dependency graph: `T0 → T1 → T3 → T4`; `T0 → T2 → T3`; `T2 → T5 → T6`; `T3 + T4 + T5 → T8 → T7`.
`T1`/`T2` may run concurrently once `T0`+`Q1` land; `T3`/`T5` may overlap after `T2`;
`T4` needs `T1`+`T3`; `T6` needs `T0`+`T5`; `T8` needs `T3`+`T4`+`T5`; `T7` is terminal after `T1–T8`.

## 5. Test contract (test-first)

| Task | Test project / file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/canon/adapters/opencode*.test.ts` (+ ingest version test) | `npm --prefix dash run test -- <file>` | Unknown plugin contract version quarantines with named reason; contract bump invalidates stale checkpoints; claimed envelope maps to `llm.invoke` / `tool.invoke`, non-model quarantines | New version-guard test fails on current tree (no version key) before implementation | Same test passes unweakened; `IngestOutcome{accepted,quarantined,rejected}` accounts every row |
| T2 | Plugin lifecycle tests colocated with plugin home | Plugin runner (named in T0; e.g. `npm --prefix <home> run test`) + `npm --prefix dash run test` for registry wiring if any | Session/turn/span hooks fire in order; hook throw/timeout never fails host; Desktop and CLIv2 targets build from one source | Lifecycle test fails with no skeleton (hooks unregistered) | Hooks registered on both targets; failure-injection test proves host continues |
| T3 | Mapper tests (plugin) + `dash/src/canon/adapters/opencode*.test.ts` | As T1/T2 | Span fields (tokens, timing, tools, errors, session id) map exactly; byte fixtures from T0 round-trip | Mapper test on real-shaped fixture fails pre-mapping | Fixture round-trips; token decomposition passes `validateTokens`-class checks where applicable |
| T4 | Correlation tests (`ingest` log path or plugin turn tests per T4 scope) | `npm --prefix dash run test -- <file>` | Log enriches exactly one span-shaped record; unmatched log quarantines (not drops); replay is idempotent | Unmatched/outcome-log test quarantines pre-fix | Correlated turn carries outcome+duration; replay test shows no duplicates |
| T5 | Transport/outbox tests (plugin) | Plugin runner | Receiver-down spools boundedly, retries with backoff, drops oldest-first with counted metric; 429 halves batch; no unbounded growth | Kill-receiver test loses data or blocks pre-fix | Bounded outbox proof + drop counter + resume-drain test green |
| T6 | `dash/src/capture/**/*.test.ts` (harness-scoped) + route test | `npm --prefix dash run test -- <file>` | Declared keys only; dry-run previews exact changes; 409 drift/conflict refuses without writes; receipted disable restores; CLI↔`POST /api/kyber/capture` parity | New key test fails (key unwritten or endpoint written to config) | Enable/disable/status round-trip green on temp HOME incl. win32-seam paths |
| T8 | Plugin assembly tests colocated with plugin home + `npm --prefix dash run check:reachable` | Plugin runner + `npm --prefix dash run check:reachable` | Installed entry legitimately loads hook→mapper→turn→outbox→sender; `check:reachable` clean; e2e asserts version-guard pass + opencode-harness attribution with `quarantined==6` (reason non-model span) + zero fabricated counters + session anchoring (not accepted); lifecycle still green (session/turn/span order, never-fail-host) | RED: F1 reproduced pre-assembly — `check:reachable` fails on the four plugin modules (mapper / turn / outbox / sender unreachable from any entry) | GREEN: `check:reachable` clean unweakened (no exemption, no deletion, no sham import) + version-guard pass + harness attribution + e2e `quarantined==6` with reason non-model span + zero fabricated counters + session anchoring (not `accepted==6` / `op!=unspecified`); no-fabrication rule preserved (mapper.ts / mapper envelope never synthesized); `opencode-envelope.test.ts` quarantine pins cited as supporting taxonomy; lifecycle / failure-injection tests still green |
| T7 | Whole-change gates (§7) + review | §7 commands | All gates green; display-layer guard green; closeout harvest complete | Gate failure blocks closeout (no silent waiver) | `PLAN_READY`-grade evidence bundle; review verdict recorded |

No-test tasks: none. Every task above names its test surface; silence is invalid per the
test-first contract. Changing any approved Test contract row (including weakening an
assertion to reach green) is a scope change: return this plan to Draft and require
conductor-relayed reapproval before implementation resumes. `development-mode` change
likewise reopens approval; `autonomy` change does not.

## 6. Evidence rules

- RED before GREEN per task scope; RED evidence names the failing contract test and the
  missing behavior, captured before implementation.
- GREEN uses the same contract test unweakened; any weakening is a scope change (§5).
- Prohibited evidence: host-selected subsets, hand-picked file lists, or
  `scripts/fast-loop.sh` alone (per `AGENTS.md` it is not evidence; the declared suite is).
- Every developer task ends with its own `task-gate`; failing gates return to the same worker.
- T8 RED evidence is the `check:reachable` F1 gate output pre-assembly (four modules
  unreachable from any entry) plus the plugin lifecycle contract test; GREEN requires the
  same gate clean with no exemption, no deletion of T3–T5 deliverables, and no sham import —
  only legitimate loading in the installed entry (`plugin.ts`) or a sanctioned build step.
- T8 e2e restated per R2 (human 2026-10-22): RED stays pre-assembly unreachable; GREEN
  asserts version-guard pass + opencode-harness attribution with `quarantined==6` (reason
  non-model span) + zero fabricated counters + session anchoring — not `accepted==6` /
  `op!=unspecified`. Non-model quarantine is the correct taxonomy for lifecycle
  boundaries. No-fabrication rule preserved (mapper.ts / mapper envelope never
  synthesized); `opencode-envelope.test.ts` quarantine pins cited as supporting.
- Whole-change review (council) adjudicates once over the full diff; per-task audits do not
  substitute for it.

## 7. Verification gates

Run the local equivalents of the required CI gates (per root `AGENTS.md`; exact commands
are authoritative there, summarized here):

- Dash (applies to T1–T6, T8; T8 owns `check:reachable`): `npm --prefix dash run typecheck`, `npm --prefix dash run lint`,
  `npm --prefix dash run test`, `npm --prefix dash run check:reachable` (T8 GREEN requires clean with no exemption / deletion / sham import + version-pass + harness attribution + e2e `quarantined==6` non-model with zero fabricated counters + session anchoring, not accepted); plus the
  display-layer guard is inside `run test` (`dash/src/architecture/display-layer.test.ts`).
- Docs (applies to T0, T6, T7, T8 closeout and this plan itself): `kyber-weave docs validate .` and
  `kyber-weave docs drift .` must be zero-findings; the corpus claim in the README must
  remain true.
- .NET gates: only if a task touches `src/` or `tests/` (none planned); if touched, the
  declared `review gates` suite is the evidence a review cites.
- Tray/native gates (`cargo fmt/clippy/test`, web `typecheck/lint/test`): only if a surface
  is touched (none planned — display-layer rule). If touched, the plan returns to Draft.

## 8. Standing constraints (non-negotiables, acknowledged)

- `TreatWarningsAsErrors` + `AnalysisMode=all`: warnings fail the build; fix causes, never
  extend `NoWarn` without a stated reason.
- `KW-*` ids permanent: add new, never reuse or renumber.
- `docs validate` + `docs drift` clean on any `docs/` edit.
- Display-layer rule (§3.1); first-party `dash/`; shared settings/pause/receiver ownership
  unchanged.
- No new dependencies without justification; no ontology widening to clear a finding.

## 9. Approval gate (Ready — decision-complete; executable; R2 approved human 2026-10-22)

This plan was Ready (AP1 explicit human `Approve and execute` 2026-10-22), was
returned to Draft by contract revision (adding T8 changes the approved Test contract
(§5) and verification scope (§§6–7), which per the test-first contract reopens
approval), and is now Ready again by explicit human re-approval. Revision scope was
authorized human-approved-revision 2026-10-22 (R1: resolve F1 `check:reachable` via
pipeline assembly in the installed entry or sanctioned build step — no exemption, no
deletion, no sham import; preserves Q1 dash-CLI-managed home, Q2a live OTLP, Q3a
span-anchored, display-layer / one-store / one-API, no web/tray change). The revised
contract is APPROVED: explicit human `Approve revised plan (Recommended)` 2026-10-22
for R1 (provenance human; gate id R1 approve-and-execute; scope: revised plan with T8
pipeline-assembly + updated Test contract, preserving Q1/Q2a/Q3a + constraints) —
implementation (T0–T8, then T7) may proceed under the conductor. Frontmatter
`status: draft` is unchanged per the closed plan lifecycle ontology (same as the prior
finalization: only `draft` is writable in frontmatter; body Ready is authoritative per
plan-authoring). Validation remains outstanding in this harness: `docs validate` /
`docs drift` were not run here (G3 — no exec tool), so neither check is claimed;
the conductor runs both checks before execution. No `AUTONOMOUS_APPROVAL` is inferred
or needed.

Revision R2 (2026-10-22) returned this plan to Draft: the human-accepted quarantine
restatement changes the approved Test contract (§5 T8 row) and verification scope
(§§6–7 e2e expectation), which per the test-first contract reopens approval. R2 scope
is human-authorized (packet authoritative 2026-10-22): T8-claimable redefined as
version-guard pass + opencode-harness attribution (achieved); non-model quarantine is
the correct taxonomy for lifecycle boundaries; e2e restated to assert `quarantined==6`
with reason non-model span + harness attribution + zero fabricated counters + session
anchoring (not `accepted==6` / `op!=unspecified`); no-fabrication rule preserved;
`opencode-envelope.test.ts` quarantine pins cited as supporting; `development-mode:
test-first`, `autonomy: autonomous`, Q1/Q2a/Q3a + display-layer / one-store / one-API /
no-web-tray preserved. Status is Draft decision-incomplete (NOT executable) pending
explicit human re-approval against R2 (gate id R2 approve-and-execute). R1 remains the
last approved authority (AP1 superseded history). Frontmatter `status: draft` unchanged
(closed ontology; body Draft is authoritative). `docs validate` / `docs drift` were not
run here (G3 — no exec tool in harness); no pass claimed — the conductor runs both
checks before execution.

Finalization R2 (2026-10-22) to Ready: explicit human `Approve R2 (Recommended)`
(provenance human; gate id R2 approve-and-execute; scope: R2 restated T8 e2e —
quarantined==6 non-model + version-pass/attribution, no fabrication — + updated Test
contract). R2 row in §10 updated in place to APPROVED with complete fields (D1–D7,
Q1–Q3, AP1, R1 preserved intact; R1 remains prior authority as history, AP1 remains
superseded history). Body status to Ready decision-complete (executable) with index row
reconciled in the same save; frontmatter `status: draft` unchanged (closed ontology —
body Ready is authoritative per plan-authoring). `development-mode: test-first` and
`autonomy: autonomous` unchanged. `docs validate` / `docs drift` still not run (G3 —
no exec tool in harness); no pass claimed — conductor runs both checks before
execution. No `AUTONOMOUS_APPROVAL` inferred or needed. Implementation (T0–T8, then
T7) may proceed under the conductor.

## 10. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Development mode? | `test-first` (default; no opt-out supplied) | Packet carries no opt-out; omission means `test-first` per plan-authoring; §5 Test contract is the matching contract. | Decided per packet 2026-10-22 (no approval needed; change reopens approval) |
| D2 | Autonomy? | `autonomous` (from Config Reg `<autonomy>`) | Packet + root `AGENTS.md` agree; autonomous-decisions reference governs; change does not reopen approval. | Decided per packet 2026-10-22 |
| D3 | Intake route? | PLAN (explicit plan mode; Promotion; no assessment) | Packet CHOICE is explicit; intake assessment skipped by instruction. | Decided per packet 2026-10-22 |
| D4 | Scope? | Both surfaces (Desktop + CLI v2) from one codebase | Packet CHOICE; §4 dual-target single-source tasks; per-target divergence isolated to manifest/install deltas. | Decided per packet 2026-10-22 |
| D5 | Content privacy default? | Counters always; content opt-in only, sender-owned (never auto-enabled) | Covered by existing pattern: Claude-Code consent keys + ADR 0031 capture-owns-keys + 14-day purge/ADR 0035 scoping; alternatives (default-on content, receiver-side redaction list) rejected as consent-violating / unownable. | `autonomous: decided, covered by ADR 0031 + Claude-Code precedent` |
| D6 | Transport if Q2 confirms OTLP? | Direct to `127.0.0.1:4318` `/v1/traces`+`/v1/logs`; no new route, no direct DB writes | Covered by runbook §1 + ADR 0009 multi-signal rule; receiver unchanged keeps one ingest code path for live and stored batches. | `autonomous: decided, covered by docs/dash/runbook.md §1 + ADR 0009` |
| D7 | Display-layer placement? | Plugin is a sender; zero `dash/web` + `dash/tray` logic | Enforced by `display-layer.test.ts` + KD-011; any surface edit returns plan to Draft. | `autonomous: decided, covered by rules/kyberdash-display-layer.md` |
| Q1 | Plugin home: new top-level package vs `dash/` subtree? | CUSTOM (human 2026-10-22): no standalone package home — hooks/config live under dash capture/harness ownership and install/configure via the dash CLI (`kyberdash kyber capture` managed-writer / registry pattern, like the other harnesses). Rejects (a)/(b)/(c) as framed per owner verbatim 2026-10-22. | Cost-of-wrong preserved: (a) `products/opencode-plugin-kyberdash/` (or `dash/opencode-plugin/`) with dual-target manifests was RECOMMENDED to isolate external API churn, match first-party-`dash` + utilities precedents, keep engine diffs to adapter+capture; (b) `dash/src/opencode-plugin/` subtree couples plugin releases to engine layout; (c) `products/kyber-utilities/` extension overloads the utilities receipt model. Custom wins on owner direction + existing pattern fit: six-harness registry with managed vs pending writers, declare-only keys, receipted enable/disable, dry-run/`POST /api/kyber/capture` parity and 409 refusals — a single install path the owner already operates; avoids a second package/home to version and distribute. Packaging impact: §§3–4 follow the dash-CLI-managed home; no dual-manifest package. | Decided human 2026-10-22 (owner verbatim: "the plugin gets installed the same way the configuration on the other harnesses does via the dash cli") |
| Q2 | Ingest transport: OTLP/HTTP to the hosted receiver vs file-drop JSONL for folder-import? | (a) Live OTLP/HTTP to `127.0.0.1:4318` (`/v1/traces` + `/v1/logs`) — CHOSEN; (b) JSONL outbox files read by `dash refresh` / `import-history` and (c) both (OTLP live + JSONL catch-up) rejected | (a) reuses one ingest path, live projection, existing quarantine/pending-log machinery; receiver unchanged so live and stored batches share one code path (runbook §1 + ADR 0009). (b) rejected: simpler offline story but adds a second parser + checkpoint seam and delayed visibility. (c) rejected: most robust but most surface (two paths, two failure stories, most code/testing). Plain-language consequences preserved in §10.1; SOURCES_SEARCHED + COST_OF_WRONG + RECOMMENDED kept there. | Decided human 2026-10-22 (owner chose (a); provenance human) |
| Q3 | Event identity + contract version: span-anchored ids with plugin contract version vs turn-aggregated records? | (a) span-anchored (`trace_id`/`span_id` + `session.id`, plugin contract version checked at ingest) — RECOMMENDED and CHOSEN | (a) preserves per-call timing, matches OpenCode `Tool.execute` shape + ADR 0009 correlation, invalidates cleanly; (b) turn-aggregated plugin records: smaller payloads but loses call-level timing and complicates log correlation (rejected). | Decided human-approved 2026-10-22 (chose RECOMMENDED (a) span-anchored) |
| AP1 | Approve-and-execute gate (gate id `approve-and-execute`)? | APPROVED — explicit human `Approve and execute` 2026-10-22; plan transitions Draft → Ready and is executable by the conductor | Scope of approval: full plan as saved (decision-complete Q1–Q3, test-first mode with §5 Test contract, §§4/7 dispatchable tasks and gates, §§3/8 standing constraints, §11 closeout mapping). Outstanding: `docs validate` / `docs drift` not run in this harness (G3, no exec tool) — conductor runs both checks before execution; any failure returns the plan to Draft. No autonomous approval inferred or needed. SUPERSEDED as execution authority by R1 revision 2026-10-22 (contract changed; re-approval pending; preserved here as history) | Approved human 2026-10-22 (provenance human; gate id approve-and-execute) — superseded by R1 revision, not current authority |
| R1 | Revision round + re-approval: add pipeline-assembly (T8) to resolve F1 `check:reachable` and re-approve the revised Test / verification contract? | APPROVED — explicit human `Approve revised plan (Recommended)` 2026-10-22: revised plan with T8 pipeline-assembly + updated Test contract is executable; new T8 wires hook→mapper→turn→outbox→sender in the installed entry (`plugin.ts`) or sanctioned build step so all four modules are legitimately loaded; preserves Q1 dash-CLI-managed home, Q2a live OTLP, Q3a span-anchored, display-layer / one-store / one-API, no web/tray change; no exemption, no deletion, no sham import; T8 after T3+T4+T5, before T7; Test contract gains T8 row (RED: unreachable pre-assembly; GREEN: `check:reachable` clean + lifecycle still green); §§6/7/9/11 updated; plan Ready and executable | Mediation finding (authoritative 2026-10-22): genuine integration gap — `plugin.ts` import-free, reached via `opencode.ts` re-export only; mapper / turn / outbox / sender internal-edges-only, invisible to `unreachable.mjs` ENTRIES walk; external host loads by file copy; sanctioned remedies delete-or-add-legit-entry only; deletion destroys T3–T5; sham import games the gate; T7 `No source` scope cannot satisfy the §7 `check:reachable` gate without contract revision. Per the test-first contract, changing the approved Test contract reopens approval | Approved human 2026-10-22 (provenance human; gate id R1 approve-and-execute) — prior execution authority (superseded as authority by R2 revision 2026-10-22, retained as history) |
| R2 | Revision round + re-approval: accept quarantine and restate the T8 e2e to `quarantined==6` non-model + version-pass/attribution, and re-approve the revised Test / verification contract? | APPROVED — explicit human `Approve R2 (Recommended)` 2026-10-22: T8-claimable redefined as version-guard pass + opencode-harness attribution (achieved); non-model quarantine is the correct taxonomy for lifecycle boundaries; e2e restated to assert `quarantined==6` with reason non-model span + harness attribution + zero fabricated counters + session anchoring (not `accepted==6` / `op!=unspecified`); no-fabrication rule preserved (mapper.ts / mapper envelope never synthesized); `opencode-envelope.test.ts` quarantine pins cited as supporting; preserves `development-mode: test-first`, `autonomy: autonomous`, Q1/Q2a/Q3a + display-layer / one-store / one-API / no-web-tray; RED stays pre-assembly unreachable; GREEN is version-pass + attribution + non-model quarantine; implementation (T0–T8, then T7) may proceed under the conductor | Per the test-first contract, changing the approved Test contract (§5 T8 row) and verification scope (§§6–7) reopened approval; D1–D7, Q1–Q3, AP1, R1 preserved intact | Approved human 2026-10-22 (provenance human; gate id R2 approve-and-execute) — current execution authority (R1 prior authority retained as history) |

### 10.1 Decided — Q2 (repaired 2026-10-22; decided human 2026-10-22)

Q1 (dash-CLI-managed home), Q2 ((a) live OTLP/HTTP), and Q3 (span-anchored) are all
decided human 2026-10-22. The plain-language context from the repaired escalation is
preserved below; the decision record lives in the Q2 row above. No choice inferred —
the owner chose (a); (b)/(c) are recorded as rejected.

**In plain language:** after opencode fires a hook (span start/end, turn end), how does
that record get into KyberDash?

- **(a) Live send (OTLP/HTTP).** The plugin phones home immediately to the KyberDash
  receiver already running on your machine (`http://127.0.0.1:4318`, `POST /v1/traces`
  + `POST /v1/logs`). *Liveness:* dashboards update while you work. *Setup:* the dash
  CLI turns capture on (receipted, dry-run preview, 409 refusal on drift) and the
  endpoint comes from process env — nothing else to configure. *Offline:* if the
  receiver is down, the plugin holds a bounded local outbox (size + age caps), retries
  with backoff/jitter, and drops oldest-first with a counted `dropped_spans` metric —
  it never blocks your opencode session. *Failure modes:* receiver down, slow drain /
  429 backpressure (batch halves, recovers additively), late spans park in `pending_logs`.
- **(b) File-drop (JSONL).** The plugin writes span/turn records as JSONL files to a local
  outbox directory; KyberDash picks them up on `dash refresh` / `import-history`.
  *Liveness:* no live updates — you only see data after a refresh/import (visibility
  delay). *Setup:* outbox directory + file checkpoint bookkeeping instead of a network
  endpoint. *Offline:* fully tolerant by construction (files wait until imported).
  *Failure modes:* stale checkpoints after a contract change, a second parser/
  checkpoint seam to own, disk growth if imports lag. No receiver needed.
- **(c) Both.** Live-send when the receiver is up, JSONL catch-up when it is not.
  *Most robust, most surface:* two paths, two failure stories, most code and testing.

```text
STATUS: DECIDED human 2026-10-22 (provenance human)
PLAN_FILE: /Users/dave/git/personal/claude/kyber-weave/docs/plans/2026-10-22-opencode-span-turn-hooks-kyberdash.md
QUESTION: [Q2] Ingest transport — live OTLP/HTTP to the hosted receiver, file-drop JSONL for folder-import, or both?
ANSWER: (a) Live OTLP/HTTP to 127.0.0.1:4318 (/v1/traces + /v1/logs); (b)/(c) rejected
SOURCES_SEARCHED: codegraph store/API boundary (dash/src/canon/paths.ts resolveCanonDbPath; dash/src/canon/ingest.ts ingestBatch/ingestBatchWithAdapters + quarantine accounting; dash/web/src/lib/kyberApi.ts /api/kyber/capture dry-run/refusal); capture ownership (dash/src/capture/harnesses/registry.ts six-harness registry + managed writers; dash/src/capture/index.ts DEFAULT_CAPTURE_ENDPOINT http://127.0.0.1:4318; dash/src/capture/harnesses/opencode.ts opencode.jsonc + OTEL_EXPORTER_OTLP_ENDPOINT env seam); docs dash/architecture.md (ReceiverHost child of kyberdash web), dash/runbook.md §1 (receiver POST /v1/traces + /v1/logs), dash/telemetry-inventory.md (collector contract, OpenCode /v1/logs quarantine gap), dash/requirements.md KD-011, rules/kyberdash-display-layer.md + ADRs 0009/0031/0033, todos kyberdash-opencode-log-correlation + pi-emitter-tool-spans
COST_OF_WRONG: expensive to reverse — transport picks the ingest seam (one live OTLP path vs a second JSONL parser + file-checkpoint seam vs both), the T1 contract-version/quarantine shape, the T4 log-correlation path, the T5 outbox-vs-file lifecycle with retry/backpressure vs import-lag story, and the T6 capture keys; choosing wrong wastes the run reworking ingest + transport + capture wiring across T1/T4–T6
RECOMMENDED was (a) OTLP/HTTP live — reuses the single existing ingest path with live projection and the proven quarantine/pending-log machinery; receiver stays unchanged so live and stored batches share one code path
```

## 11. Closeout harvest mapping (T7 closeout 2026-10-22; re-run PASS after T8; re-run PASS under R2 + restated T8)

| Durable item | Destination | Waiver (if none) |
|---|---|---|
| Plugin contract (envelope, version key, hook→span/turn mapping; R2 restated e2e) | RESIDUAL — no `docs/dash/architecture.md` ingest-layer section landed (0 matches for `kyberdash-opencode`/`opencode-envelope`/`OPENCODE_CONTRACT` 2026-10-22; T0–T8 diff touches no `architecture.md`). Requires a `docs/todo/` follow-up plus the architecture update. Documentation waiver refused: durable contract, and "no existing doc describes it" is invalid per the inventory policy. Canonical code: `dash/src/canon/adapters/opencode-envelope.ts`, `dash/src/canon/ingest.ts` (T1 version guard), `dash/src/capture/harnesses/opencode-plugin/{plugin,mapper,turn}.ts` + `transport/` (T8-assembled pipeline). R2 restatement landed in code: version-guard pass + opencode-harness attribution + `quarantined==6` non-model + zero fabricated counters + session anchoring (`plugin.pipeline.test.ts`, 6 tests); one shared lenient JSON read (`parseStructured` exported from `copilot.ts`, no duplication). Partial doc coverage only: `docs/dash/telemetry-inventory.md` T0 survey addendum (explicitly not collected). | — |
| Per-harness outcome row (spans/turns observed, content availability, residual gaps) | `docs/dash/telemetry-inventory.md` — LANDED T6 ("OpenCode plugin (`kyberdash-opencode`)" row, `last-reviewed` → 2026-10-22). Residual: "Not live-verified on this checkout" — owner enablement/install/live-turn gate open. | — |
| Enable/verify/wipe operator steps for the plugin | `docs/dash/runbook.md` — LANDED T6 ("OpenCode plugin (`kyberdash-opencode`, T6)" paragraph: plugin dirs, bounded outbox, declare-only keys, restart matrix; green-enable≠capture). Residual: live turn still required. | — |
| Capture key ownership for the plugin's config surface | `docs/dash/telemetry-inventory.md` (plugin row) + `dash/src/capture/harnesses/opencode.ts` (T6 declare-only re-export; `desiredKeys` stays the single OTel flag; endpoint process-env-only). LANDED. | — |
| Residual owner/live gates (G1/G2 fixtures, live Desktop + CLI v2 round trips) | RESIDUAL — no new `docs/todo/` files landed (only pre-existing `todo/kyberdash-opencode-log-correlation.md`). Requires one follow-up per open gate. | — |
| ADR | `no ADR — ADR waiver (reaffirmed T7, extended under R2): refines ADRs 0009/0016/0031/0033 with no new boundary, dependency, schema, or lasting invariant. T1 version guard follows the existing parser-contract invalidation precedent; T6 declare-only capture follows ADR 0031; T8 pipeline assembly (static imports in the installed entry) adds no new boundary or dependency; R2 restatement (quarantined==6 non-model taxonomy, shared parseStructured) refines the existing quarantine/adapter seam. The full D/Q/AP/R mapping in §10 is the record. Q1 dash-CLI-managed home preserved (no dual-manifest package, no new runtime dep).` | — |

T7 status: PASS (re-run under R2 + restated T8). F1 stays RESOLVED: T8 assembled the pipeline in the
installed entry — `opencode-plugin/plugin.ts` statically imports `mapper`, `turn`,
`transport/outbox`, and `transport/otlp-sender`, reached via the `opencode.ts`
declare-only re-export (no entry-registration gaming, no deletions, no behavior change to the
stage modules). R2 restatement verified in tree: T8 e2e GREEN is version-guard pass +
opencode-harness attribution + `quarantined==6` non-model + zero fabricated counters +
session anchoring (`plugin.pipeline.test.ts`, 6 tests PASS); shared `parseStructured`
exported from `copilot.ts`. R4 gates: dash typecheck, lint, `check:reachable` clean (every source +
test-support file reached), full suite 6921/6921 over 459 files (display-layer guard 28
tests; new-plugin tests 68 incl. pipeline e2e 6), `docs validate` + `docs drift` zero findings. Residuals carried explicitly per
Complete-with-residuals precedent: architecture harvest + `docs/todo/` follow-ups +
owner live gates (above). GAPS: T0–T8 `TASK_GATE:` lines not found in tree (packet
authority only). Evidence: `.agent_scratchpad/T7-opencode-span-turn-hooks-closeout/`
(worker-session-id `T7-20261022-1A8A34A5`, r4 logs + `gates-table-r4.md`).

Revision R1 2026-10-22 (provenance, preserved): T8 added for the pipeline assembly under Q1/Q2a/Q3a (no exemption, no deletion, no sham import); R1 explicitly approved human (gate id R1 approve-and-execute), making the revised contract (`T1–T8 → T7`) executable — the authority this re-run executes under. T8's interim table row and the prior BLOCKED status are superseded by the re-run table and PASS status above.

Revision R2 2026-10-22 (provenance human; APPROVED explicit human `Approve R2 (Recommended)` gate id R2 approve-and-execute): T8 e2e restated in place — RED stays pre-assembly unreachable; GREEN is now version-guard pass + opencode-harness attribution + `quarantined==6` (reason non-model span) + zero fabricated counters + session anchoring (not `accepted==6` / `op!=unspecified`); no-fabrication rule preserved (mapper.ts / mapper envelope never synthesized); `opencode-envelope.test.ts` quarantine pins cited as supporting taxonomy; §§5/6/7 updated; §9 to Ready decision-complete; R2 row APPROVED in §10; index row synced; `development-mode: test-first`, `autonomy: autonomous`, Q1/Q2a/Q3a + display-layer / one-store / one-API / no-web-tray preserved. R2 is current execution authority (R1 prior authority retained as history). T7 PASS above was under R1 authority and is re-authorized under R2 for the restated contract — T7 remains terminal after T1–T8.

## 12. Investigation ledger (recovery)

- Session: single architect pass on packet + checkout at `5ae3be88` (clean). No harness
  continuation handle was supplied; rework resumes by re-reading this file and §2/GAPS.
- Plan path: Draft authored here; index row added in the same save (§10 of plan-authoring:
  body status, frontmatter, index reconciled together).
- Validation outstanding: `docs validate`, `docs drift` (G3 — no exec tool in harness).
- Reconciliation 2026-10-22 (owner answers): Q1 CUSTOM decided human (dash-CLI-managed
  install/config via capture registry + managed writer; rejects (a)/(b)/(c) as framed;
  §§3/4/10/11 updated; cost-of-wrong preserved in Q1 row); Q3 (a) span-anchored decided
  human-approved; Q2 NOT ANSWERED — repaired plain-language escalation persisted in
  §10.1, same ids, no choice inferred. Status stays Draft; development-mode=test-first
  and autonomy=autonomous unchanged; approve-and-execute gate stays pending on Q2 +
  docs checks.
- Reconciliation 2026-10-22 (revision R1 to Draft): owner-approved revision round to
  resolve F1 `check:reachable` (provenance human-approved-revision); added T8
  pipeline-assembly scope (installed entry `plugin.ts` or sanctioned build step; T3+T4+T5
  → T8 → T7; T7 now `T1–T8`), T8 Test-contract row, §§6/7 evidence and gate updates, §11
  T8 mapping + T7 revision note, R1 OPEN row in §10 (D1–D7, Q1–Q3, AP1 preserved intact;
  AP1 marked superseded as execution authority, retained as history); §9 gate to Draft
  decision-incomplete (NOT executable) pending explicit approve-and-execute re-approval
  against R1; body status to Draft decision-incomplete with index row reconciled in the
  same save; frontmatter `status: draft` unchanged (closed ontology); `development-mode:
  test-first` and `autonomy: autonomous` unchanged; `docs validate` / `docs drift` still
  not run (G3 — no exec tool in harness); no pass claimed.
- Finalization 2026-10-22 (R1 to Ready): explicit human `Approve revised plan
  (Recommended)` for R1 (provenance human; gate id R1 approve-and-execute; scope:
  revised plan with T8 pipeline-assembly + updated Test contract, preserving Q1/Q2a/Q3a
  + constraints); R1 row in §10 updated in place to APPROVED with complete fields
  (D1–D7, Q1–Q3, AP1 preserved intact; AP1 remains superseded history); §9 gate to
  Ready decision-complete (executable) with frontmatter note; body status to Ready
  with index row reconciled in the same save; frontmatter `status: draft` unchanged
  (closed ontology — same as prior finalization; body Ready is authoritative per
  plan-authoring); `development-mode: test-first` and `autonomy: autonomous`
  unchanged; `docs validate` / `docs drift` still not run (G3 — no exec tool in
  harness); no pass claimed — conductor runs both checks before execution.
- Reconciliation 2026-10-22 (revision R2 to Draft): human-accepted quarantine
  restatement (provenance human; packet authoritative); T8 Test-contract row restated
  in place (RED pre-assembly unreachable; GREEN version-pass + attribution +
  `quarantined==6` non-model + zero fabricated counters + session anchoring, not
  accepted; no-fabrication rule preserved; `opencode-envelope.test.ts` quarantine
  pins cited as supporting), §§6/7 evidence and gate expectations updated, §11 R2
  revision note added, R2 OPEN row in §10 (D1–D7, Q1–Q3, AP1, R1 preserved intact;
  R1 marked prior authority, retained as history); §9 gate to Draft
  decision-incomplete (NOT executable) pending explicit R2 approve-and-execute
  re-approval; body status to Draft decision-incomplete with index row reconciled in
  the same save; frontmatter `status: draft` unchanged (closed ontology);
  `development-mode: test-first` and `autonomy: autonomous` unchanged; Q1/Q2a/Q3a +
  display-layer / one-store / one-API / no-web-tray preserved; `docs validate` /
  `docs drift` still not run (G3 — no exec tool in harness); no pass claimed.
- Finalization 2026-10-22 (R2 to Ready): explicit human `Approve R2 (Recommended)`
  for R2 (provenance human; gate id R2 approve-and-execute; scope: R2 restated T8 e2e —
  quarantined==6 non-model + version-pass/attribution, no fabrication — + updated Test
  contract, preserving `development-mode: test-first`, `autonomy: autonomous`,
  Q1/Q2a/Q3a + display-layer / one-store / one-API / no-web-tray); R2 row in §10
  updated in place to APPROVED with complete fields (D1–D7, Q1–Q3, AP1, R1 preserved
  intact; R1 prior authority retained as history, AP1 superseded history); §9 gate to
  Ready decision-complete (executable); body status to Ready with index row reconciled
  in the same save; frontmatter `status: draft` unchanged (closed ontology — body Ready
  is authoritative per plan-authoring); `docs validate` / `docs drift` still not run
  (G3 — no exec tool in harness); no pass claimed — conductor runs both checks before
   execution.
- Closeout 2026-10-22 (T7 re-run under R2 + restated T8): all gates re-run fresh (r4:
  typecheck/lint/reachable clean, 6921/6921 tests over 459 files incl. display-layer
  guard 28 + pipeline e2e 6, validate+drift zero findings); F1 stays resolved by real
  assembly; §11 R2 mapping complete (4 LANDED, 2 explicit RESIDUALs, ADR waiver
  extended); index row to Complete pending archive (no archive action — CI/PR act);
  G3 cleared (validate/drift run in this harness); T0–T8 TASK_GATE lines absent from
  tree (packet authority only). Evidence under
  `.agent_scratchpad/T7-opencode-span-turn-hooks-closeout/` (r4 logs).
