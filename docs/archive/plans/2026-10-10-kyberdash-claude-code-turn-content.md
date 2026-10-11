---
id: plans/2026-10-10-kyberdash-claude-code-turn-content
title: KyberDash — Claude Code turn content ingest and raw-packet inspection
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-10
development-mode: test-first
keywords:
  - claude code
  - turn content
  - otlp
  - api_request_body
  - raw packet
  - quarantine evidence
---

# KyberDash — Claude Code turn content ingest and raw-packet inspection

## Status

**Complete 2026-10-10 — `[NOT LIVE-VERIFIED]`.** Q1–Q4 were answered by the user on
2026-10-10, as relayed by the conductor, and approve-and-execute (A1) was granted the same
day. The premises behind Q1 and Q3 were re-checked against the latest `origin/main`
([F0](#f0-what-latest-originmain-is-and-where-the-codex-branch-lives)). Executed on branch
`fix/kyberdash-claude-turn-content` off `2794cc8`: TP plus T1–T9, T11–T19 all delivered
test-first (T10 correctly not needed — the e2e regression passed after the GREEN chain);
every task audited with T3, T13, T15, and T6 each reworked once inside the three-pass
budget. Gates: typecheck, lint, full test suite (346 files / 5226 tests), check:reachable,
`docs validate`, and `docs drift` all clean. T0 owner live-capture was not supplied, so the
real-world `api_request_body` shape remains documented-but-unverified; everything else is
verified against synthetic OTLP shaped from Anthropic's documented event format. Decisions
Q1–Q4 and B1–B10 are recorded below and harvested into
[ADR 0035](../adr/0035-kyberdash-quarantine-raw-packet-evidence.md) plus the dash docs.
Archived per KW-DOC-LIFECYCLE-003.

- **Date:** 2026-10-10
- **Development mode:** test-first (default; the user has not opted out)
- **Provenance:** an escalated bug-crusher defect. The investigator reproduced it against the
  live store: session `06890f81-337b-4649-8072-0dc304ef8c44`, turn 1 returns `parts: []`,
  `assembledText: ""`, and every block is `notMeasurable`. It was escalated because the fix
  spans several layers and files, touches a public API contract, has privacy consequences,
  and changes test assertions.
- **Where the work happens (Q1):** in `/Users/dave/git/personal/Devin/kyber-weave`, on a new
  branch cut from the latest `origin/main` (TP). This file and its index row were written on
  that checkout's current branch, `kyber-utilities/statusline-slice`. TP carries them onto the
  new branch.

## Problem and goal

**Problem.** The KyberDash turn view for Claude Code OTLP sessions shows no context or prompt
text on any turn, although token spend renders. There are two causes.

1. **Sender side (outside the repo, not changed here).** The user's Claude Code exports traces
   but runs with `OTEL_LOGS_EXPORTER=none` and without `OTEL_LOG_USER_PROMPTS` or
   `OTEL_LOG_RAW_API_BODIES`. The only text-bearing field that arrives is
   `user_prompt: "<REDACTED>"` on the `claude_code.interaction` span.
2. **Receiver side (in the repo — this is the defect).** Even after the sender opts in,
   KyberDash drops the text at five points:
   - the interaction span is quarantined, and its prompt is discarded with it;
   - the request-body log is matched on the wrong record name;
   - the request-body log is read from the wrong attribute key;
   - request-body `messages` are never parsed;
   - the turn view reports session-wide "not measurable" even when the turn has parts.

   Test fixtures use the same wrong names and keys, which hides the mismatches. Quarantined
   packets are not kept on `origin/main`, so the user cannot even see the packet that carried
   the redacted prompt.

**Goal.**

1. When Claude Code sends text-bearing telemetry, KyberDash stores it as canonical content
   parts and renders it in the turn view. That telemetry is the interaction span's
   `user_prompt` and the `claude_code.api_request_body` log event. What is still missing stays
   declared not measurable.
2. Answer the user's "I should at least be able to see the packet". From the turn view, the
   user can open the stored packet behind a turn: the `llm_request` span, and its parent
   interaction span, which today is quarantined with its packet thrown away.
3. Test fixtures use the record names and event shapes Claude Code actually emits. An
   end-to-end regression runs synthetic OTLP through the store to the rendered turn-content API.
4. Document the sender-side settings and what they expose (Q2). Make the per-harness wipe
   delete every place this plan puts Claude prompt text (Q3).

**Precondition this plan codes against (not a task):** text-bearing Claude Code telemetry
reaches the receiver. Nothing in this plan edits `~/.claude/settings.json` or any other user
environment file.

## Discovery method

- **Docs:** the Kyber-Weave docs MCP (`docs_explore`, `docs_for_symbol`) was not available to
  this planner. As a fallback, governed docs were read directly from `origin/main` with
  `git show` (*self-gathered*):
  - `docs/dash/telemetry-inventory.md`
  - `docs/dash/architecture.md` (the clean section)
  - `docs/dash/runbook.md`
  - `docs/rules/honest-unobservability.md`
  - ADR 0018 and ADR 0032
- **Code:** the CodeGraph shell fallback (`codegraph explore`) on the planning checkout, then
  `git show origin/main:<path>` for current source.
- **Live state:** read-only `sqlite3 -readonly ~/.kyberdash/canon.db`. This is local; no Azure
  is involved.
- **External:** no research agent was available, so the Claude Code monitoring docs
  (code.claude.com `monitoring-usage`, `agent-sdk/observability`, anthropics/claude-code#55962)
  were found by direct web search (*self-gathered*). Log-event field shapes are known only from
  those docs (F6).

## Investigation findings

### F0. What "latest origin/main" is, and where the `codex/` branch lives

Re-verified on 2026-10-10 after `git fetch origin`. The latest `origin/main` is still
**`2794cc8`** (#325, "Pin Factory models per squad profile"). Every source finding in this plan
was read from that commit with `git show origin/main:<path>`.

What was out of date in the first pass was the **working checkout**, not the analysis. The
`Devin/kyber-weave` checkout is on `kyber-utilities/statusline-slice`, 63 commits behind main.

The branch named in the first draft, `codex/dash-troubleshooting`, **is not part of this
checkout.** `git worktree list` and `git branch -a` in `Devin/kyber-weave` show no `codex/`
branch. It lives in a **separate clone** at `/Users/dave/git/personal/codex/kyber-weave`, which
has its own linked worktrees under `~/.codex/worktrees/` (for example
`quarantine-detail-payload` and `quarantine-api`) and under
`~/git/personal/codex/kyber-weave-squad/`. It is local-only: `git ls-remote origin` shows no
such head.

It came up only because the **KyberDash server running on this machine** (`server.json` pid
89815, port 4747) was built from that clone. As a result the live `canon.db` is stamped
`schema_version = 18` and already has a `quarantine.raw_payload` column. Per the Q1 answer,
that branch is **not a dependency**: whatever this plan needs is built on `origin/main` (F2,
D-Q1).

### F1. Receiver code locations on `origin/main`

- `ingestBatch`, the `op === 'unspecified'` quarantine branch: `dash/src/canon/ingest.ts` (main
  passes `source`/`name`/`timestamp` metadata to `quarantineAndDelete`).
- `enrichRecord` and `target`: `dash/src/canon/log-ingest.ts:26-49`.
- `parseClaudeApiRequestBody` and `unexportedMetrics`: `dash/src/canon/adapters/claude-code.ts`.
- `KyberBridge.assembleTurnContent`: `dash/src/server/bridge.ts`, around lines 1591–1830.
- `TurnContentResult`: `bridge.ts:566`.
- `CanonStore.wipeHarnesses` / `wipeAll` / `wipeHarnessRows`: `dash/src/canon/store.ts`.
  `SCHEMA_VERSION = 17`.

### F2. What exists on `origin/main` now for raw packets and quarantine (Q1 re-check)

| Piece | On `origin/main`? | Evidence |
|---|---|---|
| Stored-record packet route `GET /api/kyber/span/:spanId/attributes` | **Yes** | `routes.ts` `parseSpanAttributesPath` → `KyberBridge.getSpanAttributes` → `CanonStore.spanAttributes` (inflates `records.raw`). Used by web `TimelineView` and `SessionInspectorDrawer` via `fetchSpanAttributes` (`dash/web/src/lib/kyberApi.ts:457`). |
| Quarantine list `GET /api/kyber/quarantine` | **Yes** — paginated `{entries, data, total, page, limit}` | `routes.ts:908`. Columns `span_id, namespaces, reason, source, name, timestamp`. |
| `quarantine.raw_payload` column | **No** | `git grep raw_payload origin/main -- dash/src dash/web/src` finds nothing. The quarantine schema on main has no payload column. |
| `GET /api/kyber/quarantine/:spanId` detail route | **No** | No `quarantine/` route and no `getQuarantineEntry` on main. |
| Quarantine detail inspector (web) | **No** | `dash/web/src/components/analysis/QuarantineView.tsx` lists rows only. |
| Turn-view link to a turn's packet | **No** | `TurnDetail.tsx` / `ContextInspector.tsx` never call `fetchSpanAttributes`. `TurnContentResult` has `spanId` but no parent span id. |

**Gaps this plan fills:**

- keep the packet of a quarantined span (`quarantine.raw_payload`);
- serve it (`GET /api/kyber/quarantine/:spanId`);
- link both packets from the turn view.

The quarantine-page detail inspector is **not** needed for this defect and is out of scope.

### F3. The documented Claude content path is file-synth, and it is now opt-in

`docs/dash/telemetry-inventory.md` ("Claude Desktop and CLI file-synth parts (issue #216)")
documents that Claude conversation and tool-result text arrives from JSONL transcripts through
refresh or import-history. ADR 0009 D4/D10 (`twin-dedupe.ts`) then merges each bucket onto the
OTel row. Since #319/#324 (ADR 0033), folder import is off by default.

The live store has **zero** `synth:` Claude records, although the session's transcript
`~/.claude/projects/-Users-dave-git-personal-claude-kyber-weave/06890f81-….jsonl` exists. A
one-off owner run of `kyberdash dash import-history` would fill `conversation_history` and
`tool_result_content` for that session. It would not fill `system_prompt` or `tool_definitions`:
transcripts never carry them.

This plan neither runs nor changes that path (B9). The OTLP receiver defects are real
regardless, and they are the only route to live, import-free content and to those two buckets.

### F4. Receiver defects (verified on `origin/main`)

| # | Location | Defect |
|---|---|---|
| R1 | `ingest.ts` `ingestBatch`, `record.op === 'unspecified'` | `claude_code.interaction` has no counters, so `canonicalOp` returns `unspecified` and the span is quarantined as `non-model span`. Its `user_prompt` is never read. Live: 16 interaction spans and 634 tool/tool-child spans are quarantined for the session. Quarantining it as a *record* is correct; dropping its prompt is the defect. |
| R2 | `log-ingest.ts:31-34` | Checks `record.name === 'llm_request'`, but Claude Code emits `claude_code.llm_request` (212 live rows). `setContent` is unreachable. Anthropic documents span names as beta. |
| R3 | `log-ingest.ts:26,29` | Reads the body from `log.attributes['claude_code.api_request_body']`. Per Claude Code docs, that string is the *event name* (`event.name: "api_request_body"`). The JSON is in attribute `body` (inline mode); file mode instead gives a path in `body_ref`. |
| R4 | `claude-code.ts` `parseClaudeApiRequestBody` | Reads only `system` and `tools`; `messages` is ignored. |
| R5 | `claude-code.ts` `unexportedMetrics` + `copilot.ts` `baseRecord` | Every bucket is stamped `not_measurable` on every record at normalize time. Nothing clears it when parts arrive later. |
| R6 | `bridge.ts` `assembleTurnContent` block loop | `block.notMeasurable` comes from *session-level* `payload.measurability` / `context.first.buckets`, whatever the turn's own parts are. |
| R7 | `log-ingest.ts` `target` → `store.findBySessionTime` | The session/time fallback picks the nearest record of *any* op within 5 s. |
| R8 | inline body truncation | Inline bodies are cut at 60 KB by default (`body_truncated: "true"`). A real Claude Code request is routinely larger, so `JSON.parse` fails and `[]` is returned silently. |

### F6. No live Claude log evidence exists

The log tables hold no Claude Code log records: `quarantined_logs` 5,883, `pending_logs` 13,
`enriched_logs` 19, all from other sources. That is because the sender's log exporter is
`none`. Two things are known only from documentation: the OTLP `LogRecord.body` value, and
whether Claude log events carry trace/span ids. T0 is the owner capture that settles both.

### F7. Test masking (verified on main)

- `log-ingest.test.ts:25,65` and `otel/service.test.ts:88` build Claude records named
  `'llm_request'`.
- `log-ingest.test.ts:168,206` and `service.test.ts:119-121` put the body under
  `attributes['claude_code.api_request_body']`.

Generic `'llm_request'` names elsewhere — `canon/fixtures/records.ts`, `twin-dedupe*.test.ts`,
`sessions.test.ts`, and others — exercise no name-dependent behaviour.

### F8. Retention and wipe facts (Q3 re-check on `origin/main`)

- **Complete wipe — `CanonStore.wipeAll`.** Behind `kyberdash dash clean --all --yes`,
  `POST /api/kyber/clean`, and the web/tray Clean. In one transaction it deletes `records`
  (including `raw`), `record_provenance`, `source_checkpoint`, `session`, `run`, `execution`,
  `harness_rollup`, `finding`, `prediction`, `problems`, **`quarantine`, `pending_logs`,
  `quarantined_logs`, and `enriched_logs`**. It keeps `ingest_log`, `refresh_run`, `metadata`,
  `token_cache`, and the model-window catalog. **Verdict: it deletes every place prompt text
  can land.**
- **Per-harness wipe — `CanonStore.wipeHarnesses` → `wipeHarnessRows`.** It deletes `records`
  (including `raw`) for the canonical harness and its folded raw names (`claude-desktop` under
  `claude-code`), plus provenance, checkpoints, `session`, `harness_rollup`, `problems`, `run`,
  `execution`, and `prediction`. It does **not** delete `quarantine`, `pending_logs`, or
  `quarantined_logs`. `docs/dash/architecture.md:951` and `docs/dash/runbook.md:276-277` both
  state this: "a per-harness wipe cannot scope quarantine rows (no harness column)".
- **Automatic retention (ADR 0018).** `purgeExpiredContent` empties `content_json` /
  `parts_json` past 14 days. It runs in both `refreshHarnessSources` and `runMaintenancePass`,
  so it runs with folder import off. `records.raw` is deliberately kept. ADR 0018's
  Consequences say plainly: "Findings, tokens, timestamps, and raw payloads survive past
  14 days; assembled inspector text does not."
- **Where this plan would put Claude prompt text:**
  - `parts_json` / `content_json` — purged at 14 days and deleted by both wipes;
  - `quarantine.raw_payload` for the interaction span — new; deleted only by the complete wipe;
  - `pending_logs` / `quarantined_logs` for uncorrelated body logs or pending interaction
    prompts — deleted only by the complete wipe.

  `records.raw` for `claude_code.llm_request` carries no prompt text: those spans carry
  counters and metadata only.
- **Conclusion.** The repo never promised that raw packets expire in 14 days; ADR 0018 says
  they survive. A time-based scrub would therefore change an accepted ADR to deliver something
  the corpus never claimed. The real gap is narrower: **"wipe the claude-code harness" would
  leave Claude prompt text behind in quarantine and log rows.** D-Q3 closes exactly that gap.
- The server binds `127.0.0.1` by default (`cli/register.ts`, `cli/web.ts`), and `/api/kyber/*`
  has no further guard.

### F9. Schema-version collision with the live store

The live `canon.db` is stamped `schema_version = 18` by the local `codex/` build. That build's
v17→v18 migration adds `quarantine.raw_payload` and `problems.stack`. A build from main
(`SCHEMA_VERSION = 17`) refuses to open a store stamped with a newer version.

Any schema change in this plan must:

- use a version number above 18, so the user's live store still migrates;
- guard every `ADD COLUMN` with a presence check, so both a v17 main store and the v18 live
  store reach the same shape.

This is a technical constraint, not a fork: see B10.

### F10. Test-gate noise

The investigator's two failing Vitest files are under `.kilo/worktrees/cool-airship/`. That is
a linked worktree of the `Devin/kyber-weave` checkout (`git worktree list`, detached at
`1768d28`). `dash/vitest.config.ts` excludes `**/.claude/worktrees/**` but not `.kilo`. The
failures are unrelated and out of scope: do not widen the excludes to hide them. Running the
gates on the TP branch, which has no `.kilo` worktree of its own, is the clean measurement.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| Q1 | How does this plan relate to the unmerged quarantine raw-payload work in the local `codex/` clone? | **Self-contained on `origin/main`.** Work in `/Users/dave/git/personal/Devin/kyber-weave` on a new branch from the latest `origin/main` (TP). The `codex/` branches are **not** a dependency. Build only the gaps F2 lists: `quarantine.raw_payload` persistence, `GET /api/kyber/quarantine/:spanId`, and the turn-view links. Reuse the span-attributes route main already has. | User's answer (verbatim): "you should be working in the devin/kyber-weave folder and have your own branch based on origin main not sure where u are getting a codex branch from?" F0 now says where that branch lives and why it came up. Closest to the first draft's option (b), narrowed to the verified gaps. The quarantine-page detail inspector is out of scope. F9 covers schema compatibility with the live store. | ANSWERED — user decision 2026-10-10 (Other), relayed by conductor; re-verified against `origin/main` `2794cc8` |
| Q2 | What is the privacy posture for storing and serving Claude prompt and request-body text, and does the sender-side enablement belong in repo docs? | **The sender's opt-in is the consent.** Store prompt and body text as content parts under the existing purge rules (ADR 0018), and serve it on the existing loopback-only API. Render `<REDACTED>` as not measurable, with a hint naming `OTEL_LOG_USER_PROMPTS=1`. Document `OTEL_LOGS_EXPORTER=otlp`, `OTEL_LOG_USER_PROMPTS=1`, `OTEL_LOG_RAW_API_BODIES=1`, and `CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH`, with their data-exposure warning, in `telemetry-inventory.md` and `runbook.md`. Documented, never applied automatically. | Matches the shipped Copilot content-capture precedent and ADR 0014/0018. No KyberDash-side consent switch. | ANSWERED — user selected recommended option (a) 2026-10-10, relayed by conductor |
| Q3 | How long is prompt text kept inside raw packets? | **No time-based scrub, no ADR 0018 amendment.** Wiping is the deletion mechanism for raw packets, consistent with ADR 0018's stated Consequences (F8). Close the one real gap: **a per-harness wipe deletes the quarantine and log rows that harness owns.** Quarantine rows and Claude log rows (pending and quarantined) gain a nullable `harness`, written when attribution or B3 recognition names one. `wipeHarnessRows` deletes rows whose `harness` is one of the wiped raw names. Unclaimed quarantine rows (no harness) are still cleared only by the complete wipe. Claude spans are always claimed by vendor namespace, so Claude text never lands there. Docs must state honestly: "Assembled turn text is purged after 14 days. The raw packet keeps exactly what the sender sent until you wipe the `claude-code` harness or the whole database." Docs must never claim that all prompt text expires in about 14 days. **A new ADR is needed** to amend ADR 0032's documented consequence ("per-harness wipe cannot scope quarantine rows") and to record quarantine raw-payload evidence. ADR 0018 is unchanged. | User's answer (verbatim): "are you on the latest origin main yet? we have a database whipe per harness and complete database that wipe should clean all data....". F8 verified that the complete wipe deletes every relevant table, and that the per-harness wipe misses quarantine and log rows. This is the lighter of the two options the conductor offered: it documents the wipe as the mechanism, and the only added work makes the per-harness wipe actually clean. The rejected 14-day scrub would rewrite compressed `raw` blobs for something the corpus never promised. | ANSWERED — user decision 2026-10-10 (Other), reconciled by the planner against `origin/main` per the conductor's instruction; approved with A1 |
| Q4 | Which `llm.invoke` records receive the interaction span's `user_prompt`? | **Every `llm.invoke` child of the interaction**, meaning records whose `parentSpanId` is the interaction span's id. A request-body `messages` parse for the same record replaces the prompt-derived part, so text never comes from two sources. | Every API call in the turn re-sends the prompt. The rest of the context shows up as the unattributed residual (honest-unobservability R4). | ANSWERED — user selected recommended option (a) 2026-10-10, relayed by conductor |
| B1 | What contract decides which records get Claude content? | Require `harness === 'claude-code' && op === 'llm.invoke'`. **No dependency on the span name.** | Span names are beta. `op` comes from counters (`canonicalOp`). This removes R2 by design. | Baseline — approved with A1 |
| B2 | How is an interaction span recognised? | Attribute `span.type === 'interaction'` **or** name `claude_code.interaction`, on a span attributed to `claude-code`. | Both appear on the live packet. Either one alone survives a single rename. | Baseline — approved with A1 |
| B3 | How is a request-body log event recognised, and where is its body? | Recognised by `attributes['event.name'] === 'api_request_body'` **or** `OtlpLog.body === 'claude_code.api_request_body'`. A Claude log in general is recognised by those, or by scope name `com.anthropic.claude_code.events`; that recognition also stamps `harness: 'claude-code'` for wipe scoping. The body is read from `attributes.body`. `body_ref` is not read: it produces a not-measurable reason. `body_truncated === "true"`, or JSON that will not parse, produces a reason naming `CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH`. Drop the fictional `attributes['claude_code.api_request_body']` key. | From the docs (self-gathered, F6), to be confirmed by T0. Scope and event names are instrumentation evidence, not `service.name` (R6.2). Reading arbitrary local paths named in telemetry is a file-read surface this plan does not open. | Baseline — approved with A1 |
| B4 | Which test fixtures change? | Only the Claude-content fixtures in `log-ingest.test.ts` (25, 65, 168, 206) and `otel/service.test.ts` (88, 119–121). | B1 removes the name gate, so generic fixtures mask nothing. These corrections replace fictional shapes with emitted ones; they are an approved test-contract change, not a weakened assertion. | Baseline — approved with A1 |
| B5 | How do request-body `messages` map onto buckets? | User/assistant text → `conversation_history`. `tool_result` → `tool_result_content`. `tool_use` → `conversation_history` as JSON. Image or document blocks → the placeholder `[<type> block]` (never base64). Thinking blocks are skipped (the sender redacts them). Message order is kept, and there is no heuristic `instruction_context` split. | Mirrors `copilot.ts` `messageParts`. | Baseline — approved with A1 |
| B6 | How does per-record measurability change once parts arrive? | Clear a bucket's not-measurable entry on a record when that bucket gains parts. Buckets still missing keep a reason naming the env var that would supply them. Session-level availability is derived from records. The turn view sets `block.notMeasurable` only when that turn's block has no parts. | Fixes R5/R6 without inventing zeros. | Baseline — approved with A1 |
| B7 | What do the turn-content and quarantine-detail APIs return? (public contract, additive only) | `TurnContentResult` gains `parentSpanId?: string` and `rawPacket: { spanAttributesPath: string; interactionPath?: string }`. `interactionPath` is `/api/kyber/quarantine/<parentSpanId>`, present only when a quarantine row with a stored payload exists for the parent. New `GET /api/kyber/quarantine/:spanId` returns `{ spanId, source, name, reason, timestamp, namespaces, harness, rawPayload: string \| null, evidenceAvailable: boolean }`: 404 for an unknown id, 405 for a non-GET method, 400 for an empty id. The quarantine list response is unchanged; it stays light, without payloads. | Follows existing route conventions (`sendKyberJson`, a `parse*Path` helper). Older rows answer `rawPayload: null, evidenceAvailable: false`, which is explicit and never invented. The server decides which links exist; the web only renders them (ADR 0033). | Baseline — approved with A1 |
| B8 | What happens when the interaction prompt arrives before its children? | It waits as a pending entry in `pending_logs`, keyed by trace id and interaction span id, tagged `harness: 'claude-code'`, under the existing TTL. On expiry, the prompt survives only in the quarantine packet. | Both arrival orders are tested. | Baseline — approved with A1 |
| B9 | Are `claude_code.user_prompt` logs and transcript import in scope? | Out of scope. The interaction span is the prompt source. The F3 file-synth path is unchanged. | A prompt log with no spans has no record to attach to. | Baseline — approved with A1 |
| B10 | How is the schema versioned? | `SCHEMA_VERSION` becomes **19**. Migration 17→18 adds `quarantine.raw_payload TEXT`, guarded so it is a no-op on the live store already stamped 18. Migration 18→19 adds nullable `harness TEXT` to `quarantine`, `pending_logs`, and `quarantined_logs`, each guarded. `SCHEMA_SQL` carries the final shape. `problems.stack` from the `codex/` build is neither required nor removed. | F9: a main v17 store and the live v18 store both reach v19. Skipping 18 would leave a v18 store without `raw_payload` if it was not built by the `codex/` line. | Baseline — approved with A1 |
| A1 | Approve and execute. | — | All of Q1–Q4 are answered. | APPROVED — user decision 2026-10-10, relayed by conductor |

## Test contract (approved with A1)

Every row runs as `npm --prefix dash run test -- <file>` from the TP branch root.

- **RED** is the named file failing on the stated assertion while the production change is
  absent.
- **GREEN** is the same file, with the assertions unchanged, passing.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1/T2 | `dash/src/canon/adapters/claude-code.test.ts` | `npm --prefix dash run test -- src/canon/adapters/claude-code.test.ts` | Given a synthetic body with `system`, `tools`, and `messages` (user string, user text blocks, assistant text plus `tool_use`, user `tool_result`, an image block), `parseClaudeApiRequestBody` returns the B5 parts in order. Truncated or malformed JSON returns `[]` plus a failure reason. | The new `messages` assertions fail: only system and tool parts come back. | Pass. The existing system/tools assertions are untouched. |
| T3/T4 | `dash/src/canon/log-ingest.test.ts`, with fixtures corrected per B4 | `npm --prefix dash run test -- src/canon/log-ingest.test.ts` | A B3 event correlated to a `claude_code.llm_request` record whose op is `llm.invoke` gets parts through `setContent`; tokens are unchanged. `body_truncated` or `body_ref` gives no parts and adds a B3 measurability reason. The session/time fallback never lands on `tool.invoke` (R7). Non-Claude records are not overwritten. An uncorrelated Claude log is stored pending or quarantined with `harness: 'claude-code'`. | The corrected fixtures fail: parts stay empty. | Pass with no weakened assertion. |
| T5/T6 | `dash/src/canon/ingest.test.ts` | `npm --prefix dash run test -- src/canon/ingest.test.ts` | An interaction span (`span.type: interaction`, `user_prompt: "Fix the bug"`) plus two `claude_code.llm_request` children: both children get a `conversation_history` part `"Fix the bug"`. The interaction span stays quarantined (`non-model span`) with `harness: 'claude-code'` and a `raw_payload` equal to the decoded span JSON. Two batch orders (children first; interaction first, via B8) give the same result. `user_prompt: "<REDACTED>"` gives no part and a reason naming `OTEL_LOG_USER_PROMPTS=1`. Tool spans are unchanged apart from their quarantine evidence. | The children have `parts: []`. | All scenarios pass. |
| T7/T8 | `dash/src/server/kyber-content-route.test.ts` | `npm --prefix dash run test -- src/server/kyber-content-route.test.ts` | For a Claude turn with a `conversation_history` part, the turn-content route returns that block with text and **without** `notMeasurable`. `system_prompt` keeps `notMeasurable` with a reason. The result carries `spanId`, `parentSpanId`, `rawPacket.spanAttributesPath === '/api/kyber/span/<id>/attributes'`, and `rawPacket.interactionPath === '/api/kyber/quarantine/<parentId>'` only when that row has a payload. | The block carries `notMeasurable` despite text; `rawPacket` is absent. | Pass. |
| T9/T10 | `dash/src/otel/claude-content.e2e.test.ts` (new) | `npm --prefix dash run test -- src/otel/claude-content.e2e.test.ts` | Synthetic OTLP JSON (traces: interaction plus `claude_code.llm_request` for session `s-e2e`; logs: inline `api_request_body`) goes through the real receiver/service into a temp store. `GET /api/kyber/session/s-e2e/turn/0/content` then has `assembledText` containing both the prompt and the system prompt. `GET rawPacket.spanAttributesPath` and `GET rawPacket.interactionPath` both return 200, and the interaction payload contains `user_prompt`. | `assembledText === ""`. | Pass. T10 exists only if wiring is missing. |
| T11/T12 | `dash/web/src/pages/TurnDetail.test.tsx` and/or the `ContextInspector` test | `npm --prefix dash run test -- web/src/pages/TurnDetail.test.tsx` | A "Raw packet" disclosure appears when `rawPacket` is present. Opening it fetches exactly `spanAttributesPath`, plus `interactionPath` when present, through `kyberApi`, and shows the JSON unclipped. With no `rawPacket`, there is no disclosure. | The disclosure is not found. | Pass. `src/architecture/display-layer.test.ts` passes. |
| T13/T14 | `dash/src/canon/migration.test.ts`, `dash/src/canon/store.test.ts`, `dash/src/server/kyber-quarantine-detail-route.test.ts` (new) | `npm --prefix dash run test -- src/canon/migration.test.ts src/canon/store.test.ts src/server/kyber-quarantine-detail-route.test.ts` | A v17 store and a v18 store that already has `raw_payload` both open at v19 with `quarantine.raw_payload`/`harness`, `pending_logs.harness`, and `quarantined_logs.harness`; existing rows survive. `quarantineAndDelete` with `rawPayload`/`harness` metadata persists them. The detail route returns the B7 shape (200, 404, 405, 400), and a pre-v18 row reads `rawPayload: null, evidenceAvailable: false`. The list route's shape is unchanged. | The columns and route are missing. | Pass. |
| T15/T16 | `dash/src/canon/store.test.ts` (`wipeHarnesses` cases), and `dash/src/clean/clean-scope.test.ts` if the scope report is asserted there | `npm --prefix dash run test -- src/canon/store.test.ts src/clean/clean-scope.test.ts` | `wipeHarnesses(['claude-code'])` deletes `quarantine`, `pending_logs`, and `quarantined_logs` rows whose `harness` is `claude-code` or `claude-desktop`. It keeps rows with another harness or a null harness. `wipeAll` behaviour is unchanged. | Claude quarantine and log rows survive a per-harness wipe. | Pass. Existing clean tests pass. |
| TP, T0, T17, T18, T19 | No test — see each task | — | Branch setup, owner capture, docs, gates, closeout | — | As each task's acceptance states |

## Tasks

### TP: Execution branch (setup; no test)

- **Objective:** in `/Users/dave/git/personal/Devin/kyber-weave`, run `git fetch origin` and
  create a branch `fix/kyberdash-claude-turn-content` from the latest `origin/main`. Use a
  linked worktree if `kyber-utilities/statusline-slice` has uncommitted work that must not be
  disturbed. Then bring over this plan file and its `docs/plans/README.md` row, reconciling
  the index against main's copy.
- **Acceptance:** `git merge-base --is-ancestor origin/main HEAD` succeeds, and the plan and its
  index row are present.
- **Dependencies:** A1. **Skills:** git.

### T0: Owner capture of real Claude Code content telemetry (human; non-blocking)

- **Objective:** the owner runs one Claude Code session with
  `OTEL_LOGS_EXPORTER=otlp OTEL_LOG_USER_PROMPTS=1 OTEL_LOG_RAW_API_BODIES=1` set **for that
  process only** (no settings-file edit), pointed at a temporary receiver (`--db` temp path).
  The owner reports, for one `api_request_body` event: its `LogRecord.body`, its attribute
  keys, its scope name, whether it carries trace/span ids, and whether `body_truncated` fired.
- **Acceptance:** the findings are recorded here. If they contradict B3, the plan returns to
  Draft. If T0 is skipped, closeout is labelled `[NOT LIVE-VERIFIED]`.
- **Dependencies:** none. **Skills:** none (owner action relayed by the conductor).

### T1: RED — request-body `messages` parsing

- **Files:** `dash/src/canon/adapters/claude-code.test.ts`.
- **Dependencies:** TP. **Skills:** `typescript`, `vitest`, test-first.

### T2: GREEN — `parseClaudeApiRequestBody` reads `messages`

- **Files:** `dash/src/canon/adapters/claude-code.ts`: `parseClaudeApiRequestBody`, a
  body-failure reason helper, and the `unexportedMetrics` comment.
- **Acceptance:** T1 passes; B5 holds; no base64 is stored.
- **Dependencies:** T1. **Skills:** `typescript`.

### T3: RED — log enrichment on the real event shape (fixture correction)

- **Files:** `dash/src/canon/log-ingest.test.ts`; `dash/src/otel/service.test.ts` (fixture
  lines 88 and 119–121 only).
- **Dependencies:** TP. **Skills:** `typescript`, `vitest`.

### T4: GREEN — `enrichRecord` fixes (R2, R3, R7, R8) and Claude log harness tagging

- **Files:**
  - `dash/src/canon/log-ingest.ts` (`enrichRecord`, `target`, pending/quarantine writes);
  - `dash/src/canon/store.ts` (an op-filtered `findBySessionTime`, `addPendingLog` /
    `quarantineLog` accepting `harness`, per-record measurability update).
- **Acceptance:** T3 passes, and `service.test.ts` passes.
- **Dependencies:** T2, T3, T14 (log `harness` columns; shared `store.ts`). **Skills:** `typescript`.

### T5: RED — keep the interaction-span prompt

- **Files:** `dash/src/canon/ingest.test.ts`.
- **Dependencies:** TP. **Skills:** `typescript`, `vitest`.

### T6: GREEN — attach the interaction prompt before quarantine

- **Files:**
  - `dash/src/canon/ingest.ts` (`ingestBatch`: before `quarantineAndDelete` of a B2 span,
    attach a non-redacted `user_prompt` per Q4/B8);
  - `dash/src/canon/store.ts` (child lookup by `trace_id` + `parent_span_id`);
  - `dash/src/canon/adapters/claude-code.ts` (redaction-aware reason).
- **Acceptance:** T5 passes; `IngestOutcome` counts are unchanged.
- **Dependencies:** T5, T4 (shared `store.ts`), T14 (raw-payload/harness metadata on
  `quarantineAndDelete`). **Skills:** `typescript`.

### T7: RED — per-turn measurability and raw-packet pointers

- **Files:** `dash/src/server/kyber-content-route.test.ts`.
- **Dependencies:** TP. **Skills:** `typescript`, `vitest`.

### T8: GREEN — bridge and session measurability (R5, R6, B6, B7 turn fields)

- **Files:**
  - `dash/src/server/bridge.ts` (`TurnContentResult`, `assembleTurnContent`);
  - the session measurability derivation in `dash/src/canon/sessions.ts`, or wherever
    `payload.measurability` is built on main;
  - `dash/web/src/lib/kyberApi.ts` (`KyberTurnContentResult` type only).
- **Acceptance:** T7 passes; the existing bridge and session suites pass unchanged.
- **Dependencies:** T7, T6, T14 (shared `bridge.ts`; uses `getQuarantineEntry`). **Skills:** `typescript`.

### T9: RED — end-to-end OTLP → store → API regression

- **Files:** `dash/src/otel/claude-content.e2e.test.ts` (new). Synthetic payloads only: no real
  prompt text and no data from `~/.claude`.
- **Dependencies:** TP. **Skills:** `typescript`, `vitest`.

### T10: GREEN — residual wiring (only if T9 still fails after T2/T4/T6/T8/T14)

- **Files:** whatever T9 shows, scoped to `dash/src/otel/`, `dash/src/canon/`, and
  `dash/src/server/`.
- **Dependencies:** T9, T2, T4, T6, T8, T14. **Skills:** `typescript`.

### T11: RED — turn-view raw-packet disclosure

- **Files:** `dash/web/src/pages/TurnDetail.test.tsx` and/or the `ContextInspector` test.
- **Dependencies:** TP. **Skills:** `react`, `vitest`, display-layer rule.

### T12: GREEN — render the raw-packet disclosure

- **Files:** `dash/web/src/pages/TurnDetail.tsx` and/or
  `dash/web/src/components/ContextInspector.tsx`; `dash/web/src/lib/kyberApi.ts` (a
  `fetchQuarantineEntry` fetcher next to `fetchSpanAttributes`).
- **Acceptance:** T11 passes; `display-layer.test.ts` passes; the web adds no logic beyond
  rendering what the API returns.
- **Dependencies:** T11, T8. **Skills:** `react`, `typescript`.

### T13: RED — quarantine evidence schema, store, and detail route

- **Files:** `dash/src/canon/migration.test.ts`, `dash/src/canon/store.test.ts`,
  `dash/src/server/kyber-quarantine-detail-route.test.ts` (new).
- **Dependencies:** TP. **Skills:** `typescript`, `vitest`.

### T14: GREEN — quarantine raw payload, harness columns, and `GET /api/kyber/quarantine/:spanId`

- **Files:**
  - `dash/src/canon/store.ts`: `SCHEMA_VERSION` 19, `SCHEMA_SQL`, `MIGRATIONS` 17 and 18 per
    B10, `quarantineAndDelete` metadata `rawPayload`/`harness`, and `getQuarantineEntry`;
  - `dash/src/canon/ingest.ts`: pass `rawPayload` (the decoded span JSON) and, when
    attribution named one, `harness` on both quarantine calls;
  - `dash/src/server/bridge.ts`: `getQuarantineEntry`;
  - `dash/src/server/routes.ts`: the detail route placed **after** the exact
    `/api/kyber/quarantine` list match.
- **Acceptance:** T13 passes; the existing quarantine list tests pass unchanged.
- **Dependencies:** T13. **Skills:** `typescript`, `sqlite`.

### T15: RED — per-harness wipe scopes quarantine and log rows

- **Files:** `dash/src/canon/store.test.ts`; `dash/src/clean/clean-scope.test.ts` if the scope
  report is asserted there.
- **Dependencies:** TP. **Skills:** `typescript`, `vitest`.

### T16: GREEN — extend `wipeHarnessRows`

- **Files:** `dash/src/canon/store.ts` (`wipeHarnessRows`: delete `quarantine`,
  `pending_logs`, and `quarantined_logs` rows whose `harness` is one of the wiped raw names).
- **Acceptance:** T15 passes. The existing clean and wipe tests, including
  `jobs/wipe-acceptance.integration.test.ts`, pass.
- **Dependencies:** T15, T6 (the last `store.ts` writer before it). **Skills:** `typescript`, `sqlite`.

### T17: Documentation

- `docs/dash/telemetry-inventory.md`:
  - the Claude Code row, plus a new "Claude Code OTLP content (live)" section covering the
    B1–B5 contract;
  - the Q2 env vars and their exposure warning; inline truncation and
    `CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH`; `body_ref` is not read; the F3 import-history
    alternative;
  - the honest retention sentence from Q3;
  - an update to remaining owner gate 1.
- `docs/dash/runbook.md`:
  - enabling content;
  - seeing a turn's raw packet;
  - replacing lines 276–277: the per-harness wipe now scopes attributed quarantine and Claude
    log rows, and unclaimed rows still need the complete wipe;
  - the honest retention sentence.
- `docs/dash/architecture.md`:
  - the additive `TurnContentResult` fields;
  - `GET /api/kyber/quarantine/:spanId`;
  - schema v19;
  - the clean paragraph at line 951.
- **A new ADR**, plus its row in the ADR index. It amends ADR 0032's per-harness quarantine
  consequence and records quarantine raw-payload evidence, with raw retention lasting until a
  wipe (consistent with ADR 0018, which is unchanged).
- **Dependencies:** T2, T4, T6, T8, T12, T14, T16. **Skills:** `kyber-weave-docs`.
- **Acceptance:** `docs validate .` and `docs drift .` report zero findings.

### T18: Gates and review

- **Gates:**
  - `npm --prefix dash run typecheck`, `lint`, `test`, and `check:reachable`;
  - `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready`;
  - `docs drift .`.
- Then review the diff. Report F10-style stray-worktree failures as pre-existing and unrelated,
  without editing excludes.
- **Dependencies:** all implementation tasks and T17. **Skills:** code review.

### T19: `docs-dev` closeout

- Harvest Q1–Q4 and B1–B10 into the T17 docs and the ADR. Archive this plan to
  `docs/archive/plans/` with its Decisions section intact, and move its index row. Record the
  T0 evidence, or label the closeout `[NOT LIVE-VERIFIED]`.
- **Dependencies:** T18. **Skills:** `kyber-weave-docs`.

## Dependency graph and concurrency

```text
A1 → TP
RED wave (parallel after TP; disjoint test files except store.test.ts, see note): T1 T3 T5 T7 T9 T11 T13 T15
store.ts writer chain:   T13 → T14 → T4 (also ← T2 ← T1, ← T3) → T6 (also ← T5) → T16 (also ← T15)
bridge.ts writer chain:  T14 → T8 (also ← T7, ← T6) → T12 (also ← T11)
T9 + T2 + T4 + T6 + T8 + T14 → T10 (conditional)
T2 T4 T6 T8 T12 T14 T16 → T17 → T18 → T19
T0 runs any time; it gates only the "live-verified" label at T19.
```

T13 and T15 both edit `dash/src/canon/store.test.ts`, so T15 runs after T13. `kyberApi.ts`
(T8 types, then T12) and `ingest.ts` (T14, then T6) are serialized by the chains above.

**MAX_CONCURRENCY: 4.** The RED wave has seven files that can be written independently, but
four parallel writers keep review tractable. GREEN work is mostly serial because of
`store.ts` and `bridge.ts`.

## Risks

- **Event shape known only from docs (F6).** If T0 contradicts B3 or R7, return to Draft. B3
  accepts all the documented signals.
- **Inline truncation (R8).** Request bodies at the default 60 KB will often be truncated.
  `user_prompt` (T6) still delivers prompt text. The docs must say so plainly.
- **Q4 inflates `conversation_history`** across the calls in one interaction. This is accurate,
  but review should check findings that sum buckets across turns.
- **Schema collision (F9).** If the `codex/` line later merges with its own v18 meaning, it
  must rebase onto v19. B10's guarded migrations make the live store safe today.
- **Renormalize/backfill** (`tools/backfill.ts`) does not replay cross-span prompt attachment,
  and does not pass `rawPayload`/`harness` for rows it quarantines (those read
  `evidenceAvailable: false`). This is a known limit and is out of scope.
- **Raw retention lasts until a wipe (Q3).** This is accepted by ADR 0018 and must be stated in
  the docs, not implied away.
- **The B6 measurability change** could shift session availability for other harnesses. The
  existing session suites must pass unchanged.

## Out of scope

- Editing the user's Claude Code settings or environment, or running `import-history` for them.
- Anything on the `codex/` branches: no merging, depending on, or porting.
- A quarantine-page detail inspector in `QuarantineView.tsx`.
- Reading `body_ref`; ingesting `api_response_body` or `claude_code.user_prompt` logs.
- A time-based scrub of raw packets (Q3).
- Storing the full OTLP envelope in `records.raw`.
- The `.kilo/worktrees` test pickup (F10).
- Renaming generic `'llm_request'` fixtures (B4).

## Verification gates

- `npm --prefix dash run typecheck`
- `npm --prefix dash run lint`
- `npm --prefix dash run test`
- `npm --prefix dash run check:reachable`
- `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready`
- `dotnet run --project src/KyberWeave.Cli -- docs drift .`

## Planning verification

On the planning checkout, on 2026-10-10, `docs validate .` and `docs drift .` both reported
zero findings. TP re-runs both on the execution branch, because main's documentation corpus
differs from this checkout's.

## Review and closeout

Review checks:

- display-layer compliance in T12;
- that no assertion was weakened (B4 is the only approved fixture change);
- honest-unobservability R1–R4 on every new not-measurable path;
- the Q2/Q3 privacy posture, including the honest retention sentence;
- that both B10 migration paths hold.

T19 archives the plan in the PR that finishes it.
