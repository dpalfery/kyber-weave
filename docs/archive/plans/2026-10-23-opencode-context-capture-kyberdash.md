---
id: plans/2026-10-23-opencode-context-capture-kyberdash
title: "opencode context capture: system prompt, tool schemas, skills (envelope v2)"
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-25
documentation-impact: extends-component
impact-components: [KyberDash]
development-mode: test-first
---

# opencode context capture: system prompt, tool schemas, skills (envelope v2)

**Status: Archived — T12 backfill closeout 2026-10-25: scale declared (`extends-component`, [KyberDash]), design harvested into the KyberDash canonical docs (see `## Design harvest` and `## Closeout mapping` below). The Decisions table and every original section are preserved intact; the original status paragraph follows, labelled as history.**

**Pre-closeout status (preserved as history):** Ready — decision-complete and executable. All decisions D1–D7 decided under
`autonomy: autonomous` (provenance recorded per row); the approve-and-execute gate (AP1)
is recorded as accepted per the bug-crusher escalation packet's autonomous-mode instruction,
and the conductor still presents it before execution routing. Frontmatter `status: draft`
per closed ontology — body Ready is authoritative. `docs validate` / `docs drift` could not
be run in this harness (no process tool; G3) — the conductor runs both before execution.**

**Development mode:** `test-first` (default; no opt-out supplied).
**Autonomy:** `autonomous` (resolved from repository root `AGENTS.md` Config Reg `<autonomy>`; supplied by the bug-crusher escalation packet).

## 1. Problem and goal

### 1.1 Defect (bug-crusher escalation, investigator verdict NEEDS_ARCHITECT)

opencode sessions in KyberDash show no system prompt, tool schemas, or skills data. The
user believed a session context hook was already built and proposed a standalone
`.opencode/plugins/kyberdash/index.ts` plugin using `ctx.session.hook("context", ...)`
POSTing raw `{systemPrompt, userPrompt, tools, sessionId}` JSON to `http://127.0.0.1:8080/v1/traces`.

The user's proposal fails on its own terms and is **rejected** (D1):

- **Wrong port.** The live loopback OTLP/HTTP receiver is `http://127.0.0.1:4318`
  (`transport/otlp-sender.ts` `DEFAULT_OTLP_ENDPOINT`); nothing listens on 8080.
- **Wrong body.** The receiver consumes OTLP `ExportTraceServiceRequest` bodies
  (`buildOtlpTracesBody`), not raw JSON keyed `systemPrompt`/`userPrompt`.
- **Wrong route.** A second standalone plugin bypasses the version guard, the bounded
  outbox, the never-fail-host contract, and the contract-versioned envelope the shipped
  plugin already owns — it would quarantine or double-claim.

The gap is structural and was never built (investigator findings, verified during this
planning pass):

- `OPENCODE_PLUGIN_HOOK_KINDS` (`plugin.ts`) covers only lifecycle boundaries
  (session/turn/span start/end); its payload type `OpencodeHookPayload` carries only
  `{sessionId, turnIndex?, spanId?}` — no event content.
- The v2 `session.context` hook — cataloged at
  `fixtures/hook-catalog.json` (`v2_namespaced_hooks.session`) with its request payload
  keys (`system`, `messages`, `options`, `tools`, …) surveyed in
  `docs/dash/telemetry-inventory.md` "OpenCode Desktop + CLI v2 hook/plugin API addendum"
  — is registered nowhere.
- The T0 skeleton surveys `prompt_skills` (`span-payload-skeleton.json`) but no code reads it.
- `mapper.ts` has no system/skills fields; `opencode.prompt.text` maps only to
  `opencode.prompt.text`.
- The canon adapter (`canon/adapters/opencode.ts`) buckets envelope prompt text only as
  `conversation_history` (`applyEnvelope`); `system_prompt`/`tool_definitions` bucketing
  exists only on the legacy AI-SDK path (`legacyAiParts`).
- The archived plan `docs/archive/plans/2026-10-22-opencode-span-turn-hooks-kyberdash.md`
  confirms T2 scope was lifecycle emission only.

**Additional structural finding from this planning pass (carried forward, tripwire 4):**
the plugin source directory ships **no opencode-loadable module entry** — `plugin.ts` has
no default export and no `Plugin.define`/`ctx`-shaped export, and no dash-CLI installer
copies it (only the Kilo plugin has `install.ts`). Whatever sessions the user sees in
KyberDash today arrive through opencode's native OpenTelemetry export
(`experimental.openTelemetry` → OpenInference spans), which carries counters but no
assembled request content. The fix therefore needs a registration bridge, not just a new
handler.

### 1.2 Goal

opencode sessions in KyberDash show the system prompt, tool definitions, and skills
listing on the Context and Skills views, captured through the in-repo
`kyberdash-opencode` plugin's context hook, mapped onto a versioned envelope
(contract `'2'`), bucketed by the canon adapter, and surfaced through the skills module —
with colocated tests, governed docs updated, the display-layer rule untouched, and no new
dependency.

### 1.3 Non-goals / out of scope

- No `dash/web/**` or `dash/tray/**` change (display-layer rule; both surfaces read
  `/api/kyber/*` only — the new content arrives as ordinary record parts).
- No rewrite of the existing lifecycle hook kinds: their behavior is unchanged; T0 records
  what the real API offers but lifecycle re-registration is not required for GREEN.
- No cross-instrumentation correlation between the plugin's context record and opencode's
  native OpenInference spans (D4; deferred — a todo if turn pairing proves disjoint).
- No dash-CLI installer automation for the plugin file copy (install stays by-file per the
  runbook; an installer wants its own todo — D7).
- No new npm/runtime dependency (the plugin imports only node built-ins and in-repo
  modules; the `@opencode/plugin` types are pinned as fixtures, not installed).
- No ontology widening, no `KW-*` renumbering, no `TreatWarningsAsErrors`/`AnalysisMode=all` change.

## 2. Investigation findings

Discovery: `codegraph 0, docs 0, direct reads 24, research-agent 2`. The CodeGraph and
kyber-weave docs MCP tools are not exposed in this harness (tool inventory: read / grep /
find / ls / edit / write / nested agents only), so the protocol's narrow fallback applied:
every source below was read directly and is labeled. Two `research-agent` passes for the
external opencode v2 API both failed — the child harnesses expose **no network tools**
(read/grep/find/ls only) — an environmental, deterministic failure, so the external
contract rests on the in-repo T0 survey (which was sourced live at the time, citing
opencode.ai v2 docs and anomalyco/opencode PRs) and is re-pinned by T0 at execution time.

Direct reads (24), each for what no index covers in this harness:

1. Root `AGENTS.md` — Config Reg (plan-index `docs/plans/README.md`), non-negotiables, dash gates.
2. `docs/plans/README.md` — Active Plans table shape and closeout policy.
3. `dash/src/capture/harnesses/opencode-plugin/plugin.ts` — hook kinds, payload type, host abstraction, installed pipeline, no loadable entry.
4. `…/opencode-plugin/mapper.ts` — envelope attribute mapping, no system/skills fields.
5. `…/opencode-plugin/fixtures/hook-catalog.json` — v1 legacy hooks, v1 events, v2 namespaced hooks (`session.context` present, registered nowhere).
6. `…/opencode-plugin/fixtures/span-payload-skeleton.json` — `prompt_skills` surveyed, unread.
7. `…/opencode-plugin/fixtures/install-targets.json` — v2 package forms (relative/absolute/`file:` paths legal), load order.
8. `…/opencode-plugin/transport/otlp-sender.ts` — `DEFAULT_OTLP_ENDPOINT` `http://127.0.0.1:4318/v1/traces`, `buildOtlpTracesBody`, contract-version stamping.
9. `dash/src/capture/harnesses/opencode.ts` — managed writer: `enable` writes only `experimental.openTelemetry`; plugin keys declare-only.
10. `dash/src/canon/adapters/opencode.ts` — adapter: `applyEnvelope` buckets prompt text as `conversation_history` only; `legacyAiParts`/`openInferenceParts` show the `system_prompt`/`tool_definitions` bucketing precedents.
11. `dash/src/canon/adapters/opencode-envelope.ts` — contract `'1'`, version key, marker prefixes, accessors.
12. `dash/src/canon/ingest.ts` (grep) — version guard: unsupported envelope quarantines under `UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON` before the adapter vote.
13. `dash/src/canon/adapters/opencode-envelope.test.ts` (grep) — existing suite pins the v1 envelope, version quarantine, and checkpoint invalidation on bump (the T1 test pattern to extend).
14. `dash/src/canon/types.ts` — `CANONICAL_CONTENT_KEYS` (`system_prompt`, `tool_definitions`, …), `ContentPart`, `contentFromParts`, `SKILLS_LISTED_KEY`.
15. `dash/src/canon/skills.ts` (grep) — listing-source pattern: `readListingOf` reads raw keys per harness (`github.copilot.context.skills`, `gen_ai.skills`) with named `notMeasurable` reasons; `readSystemPromptListing` fallback.
16. `dash/src/synth/synth.ts` (partial) — where `kyber.skills.listed` is written on the synth path (context for how the Skills view consumes listings).
17. `dash/src/canon/adapters/copilot.ts` (`baseRecord`) — `raw: raw.attributes`: every adapter record carries span attributes as `raw`, so a raw skills key is readable by `skills.ts` without a store change.
18. `dash/src/capture/kilo-plugin/source.ts` — the Kilo precedent: an opencode-fork plugin hooking `experimental.chat.system.transform` + `tool.definition` + `chat.params` to capture exactly system prompt, tool definitions, and call identity; `input` carries `sessionID`, `message`, `agent`, `model`, `provider`.
19. `dash/src/capture/kilo-plugin/install.ts` (grep) — the only plugin installer in the tree (opencode has none).
20. `docs/dash/telemetry-inventory.md` — the T0 survey addendum: v1/v2 hook names, payload keys ("Request keys: `system`, `messages`, `options` …, `tools` …"), install targets, Desktop-vs-CLIv2 deltas; the OpenCode row and its sources.
21. `docs/dash/runbook.md` (grep) — the OpenCode plugin paragraph (install by file, restart, "a green enable is not capture").
22. `docs/dash/architecture.md` (grep) — the landed envelope-contract section (version key, version guard, claim rule, mapping, assembly) — the section this plan extends.
23. `docs/archive/plans/2026-10-22-opencode-span-turn-hooks-kyberdash.md` (2 reads) — prior decisions D5 (content off by default, sender opt-in), Q2a/Q3a, G1 (external API pin at execution time), task/test precedent.
24. Role references (`plan-authoring.md`, `autonomy-decisions.md`, `test-first-contract.md`) and `docs/documentation-ontology.md` (plan required-key matrix) — plan contract compliance.

### 2.1 What the checkout already gives the fix

1. **A receiver and transport that are already right.** The plugin's OTLP/HTTP sender
   posts `ExportTraceServiceRequest` bodies to `127.0.0.1:4318/v1/traces` through the
   bounded outbox — the user's 8080/raw-JSON route was the only wrong part.
2. **A versioned envelope with a guard.** `opencode.contract_version` +
   `isSupportedOpencodeEnvelope` + ingest quarantine + checkpoint invalidation exist and
   are pinned by tests — schema growth is exactly what the contract bump is for.
3. **Bucketing precedents.** `legacyAiParts`/`openInferenceParts` already bucket
   system-role text to `system_prompt` and a tools payload to `tool_definitions` on the
   legacy paths; `applyEnvelope` extends, not invents.
4. **A skills listing-source pattern.** `skills.ts` `readListingOf` reads per-harness raw
   keys with named `notMeasurable` reasons; `baseRecord` already stores span attributes
   as `raw`, so a new `opencode.context.skills` key needs no store change.
5. **A hook precedent for exactly this data.** The Kilo capture plugin (an opencode fork
   with the same v1 `Hooks` surface) captures the assembled system prompt
   (`experimental.chat.system.transform`), tool definitions (`tool.definition`), and call
   identity (`chat.params` carrying session/message/agent/model/provider).
6. **A pinned survey of the v2 surface.** `session.context` and its request keys
   (`system`, `messages`, `options`, `tools`) are cataloged in-repo from the opencode v2
   docs and the anomalyco plugin source.

### 2.2 GAPS

- **G1 — verbatim live registration shape.** The exact v2 registration surface
  (`Plugin.define({ id, setup })` → `ctx.session.hook("context", …)` per the survey, or
  the exact `setup` return shape) and the exact `session.context` payload field names
  could not be quoted verbatim: no network tool exists in this harness, and two
  `research-agent` passes confirmed the same. Mitigation: T0 is a hard pin gate — no
  emission task starts before the verbatim types land as fixtures; the fallback ladder
  (v1 `chat.params` + `experimental.chat.system.transform` + `tool.definition`, pinned
  from the plugin source and live-proven by the Kilo precedent) survives within the same
  architecture. Precedent: the 2026-10-22 plugin plan shipped with the same G1 shape.
- **G2 — no live fixtures.** No captured `session.context` payload exists; the owner
  live-capture acceptance gate remains (`[NOT LIVE-VERIFIED]` convention).
- **G3 — validation toolchain.** No process tool in this harness; `docs validate` /
  `docs drift` must be run by the conductor before execution (the 2026-10-22 plan's G3
  precedent). The dash gates (`npm --prefix dash run typecheck/lint/test/check:reachable`)
  likewise run at execution time.

## 3. Architecture

### 3.1 Non-negotiable placement (unchanged)

- **Display-layer rule.** No `dash/web` or `dash/tray` change. The context data lands as
  ordinary `ContentPart`s (`system_prompt`, `tool_definitions`) and a raw skills key on
  canon records; both surfaces already render those through `/api/kyber/*`.
  `dash/src/architecture/display-layer.test.ts` stays green.
- **One store, one API.** The plugin remains a sender to the loopback receiver;
  `ingestBatch` is the only write path; `resolveCanonDbPath` the only store address.
- **First-party, dash-owned.** The plugin stays under
  `dash/src/capture/harnesses/opencode-plugin/`, installed by file per the runbook;
  capture keeps owning its keys declare-only (`enable` still writes only
  `experimental.openTelemetry`); the consent env var (D5) is sender-owned and never
  written by capture or named in a receipt (the `OTEL_EXPORTER_OTLP_ENDPOINT` pattern).
- **No new dependency.** The `@opencode/plugin` types are pinned as fixture text; the
  entry module imports only node built-ins and in-repo modules.

### 3.2 Data flow (extended)

```text
opencode Desktop (v1 hooks) / CLI v2 (Plugin.define, ctx.session.hook("context"))
   └─▶ entry module (new): consent gate (KYBERDASH_OPENCODE_CONTEXT) → createInstalledOpencodePlugin
         │ session.context (v2) | chat.params + system.transform + tool.definition (v1 fallback ladder)
         │ payload: sessionID, model/provider/agent, system, tools, skills (T0-pinned field names)
         ▼
   hook event {kind:'session.context', sessionId, systemPrompt?, toolsText?, skills?, model?}
         │ mapper: envelope v2 attributes — opencode.context.system / .tools / .skills
         │          + opencode.model.* markers when the payload names them
         ▼
   outbox → OTLP sender → POST /v1/traces (127.0.0.1:4318, ExportTraceServiceRequest)
         ▼
   ingestBatch version guard (contract '2'; '1' spans now quarantine) → opencode adapter
         ├─ applyEnvelope: system_prompt + tool_definitions parts (+ conversation_history from prompt text)
         ├─ record.raw carries opencode.context.skills → skills.ts readListingOf (new listing source)
         └─ llm.invoke record (context markers are model-call evidence) → canon store → /api/kyber/* → both surfaces
```

Privacy (D5, inheriting the 2026-10-22 plan's decided D5): counters and identity always
flow; the system prompt, tool definitions, and skills listing are **content** (the
assembled system prompt embeds instruction files and environment) and ride only under
the sender-owned env consent, off by default. With consent absent the context span still
emits identity/markers; the content fields are dropped before emit.

### 3.3 Envelope v2 shape (contract bump `'1'` → `'2'`)

- New marker prefix `opencode.context.` (joining `opencode.turn./model./tool./prompt./
  call./message./hook`): a span carrying any `opencode.context.*` key is the envelope.
- New keys: `opencode.context.system` (string), `opencode.context.tools` (JSON-serialized
  tool definitions array), `opencode.context.skills` (JSON array of skill names).
- `OPENCODE_CONTRACT_VERSION` → `'2'`; the existing version guard, unknown-version
  quarantine reason, and checkpoint-invalidation semantics apply unchanged. Spans
  emitted by a pre-upgrade plugin (stamped `'1'`) quarantine after the bump — intended
  (stale checkpoints invalidate and re-emit under `'2'`); the runbook documents the
  restart/upgrade note.
- Mapping: model markers map `llm.invoke` as today; a context span that carries model
  markers maps `llm.invoke`; a context span with context markers but no model identity
  still maps `llm.invoke` (a context event is pre-LLM-call request assembly — the same
  claim the Claude request-body enrichment makes), never fabricates counters (tokens stay
  unset/zero with honest measurability), and lifecycle spans without markers keep the
  non-model quarantine taxonomy.

## Decisions

Permanent table; preserved intact across all lifecycle transitions.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Follow the user's standalone `.opencode/plugins/kyberdash/index.ts` plugin (raw JSON to 8080), or extend the in-repo `kyberdash-opencode` OTLP envelope plugin? | **Extend the in-repo plugin.** The user's route is rejected: wrong port (receiver is 4318), wrong body (receiver consumes OTLP `ExportTraceServiceRequest`), and it bypasses the version guard, bounded outbox, and never-fail-host contract. | `transport/otlp-sender.ts` `DEFAULT_OTLP_ENDPOINT`; `buildOtlpTracesBody`; `ingest.ts` version guard; the 2026-10-22 plugin plan's T1–T8 pipeline. Alternative (standalone plugin) would quarantine or double-claim and forks the capture ownership (ADR 0031 pattern). | ANSWERED — `autonomous: recommendation accepted` (bug-crusher escalation packet, autonomy: autonomous, 2026-10-23) |
| D2 | Which hook carries the context, and what is the registration surface? | **Primary: v2 `session.context`** (cataloged in `fixtures/hook-catalog.json`; request payload keys `system`, `messages`, `options`, `tools` per the telemetry-inventory T0 survey), registered through the entry module's v2 shape; **fallback ladder: v1 dotted hooks `chat.params` + `experimental.chat.system.transform` + `tool.definition`** (pinned from the plugin `Hooks` source and live-proven by the Kilo capture plugin on the same fork surface). The exact verbatim registration shape is pinned by **T0 before any emission code** (hard gate). | In-repo survey covers hook names + payload keys; verbatim types unverifiable in this harness (G1, two no-network research passes). Fallbacks live inside the same architecture, so a T0 surprise adjusts the entry, not the envelope/adapter design. The user's `ctx.session.hook("context", …)` snippet is treated as a hint, not authority. | ANSWERED — `autonomous: recommendation accepted` (2026-10-23); verbatim shape pending T0 pin (low-confidence on exact field names, mitigated by the T0 gate) |
| D3 | Envelope semantics for the new content: new namespace or contract bump? | **Contract bump `'1'` → `'2'` with an `opencode.context.*` marker prefix** (`opencode.context.system/.tools/.skills`). Version guard, unknown-version quarantine, and checkpoint invalidation semantics unchanged. | `OPENCODE_CONTRACT_VERSION` exists precisely to invalidate stale checkpoints on schema change (T1 test pins the bump pattern); pre-upgrade `'1'` spans quarantining after the bump is the designed behavior, documented in the runbook. | ANSWERED — `autonomous: recommendation accepted` (2026-10-23) |
| D4 | Record shape: correlate the context with opencode's native OpenInference model spans, or a self-contained record? | **Self-contained `llm.invoke` context record** on the plugin's envelope (model markers when the payload names them; context markers are model-call evidence on their own). Cross-instrumentation correlation with native OTel spans is deferred. | Claude's request-body correlation was needed because the body arrived as an identity-less log; here the plugin controls the span shape and the payload carries session + model identity (`chat.params` input precedent: sessionID/message/agent/model/provider), and session grouping already shares `session.id`. Cheap to reverse: correlation can be added later without touching the envelope. If turn pairing proves disjoint in live use, a follow-up todo takes it. | ANSWERED — `autonomous: recommendation accepted` (2026-10-23); low-confidence, cheap to reverse, recorded |
| D5 | Privacy gating for system prompt / tool definitions / skills listing. | **Content, off by default, behind the sender-owned env consent `KYBERDASH_OPENCODE_CONTEXT=1`** (exact name finalized in T3; the semantic is the decision). Identity/markers/counters always flow; with consent absent the context span emits without content fields. Capture never writes or receipts the env key; the harness writer reports it `statusEnvSnippet`-style (declare-only, the `OTEL_EXPORTER_OTLP_ENDPOINT` pattern). | Inherits the 2026-10-22 plugin plan's decided D5 (content off by default, sender opt-in, Claude-Code `OTEL_LOG_*` precedent). Alternative (always-on, like the Kilo file-store capture) rejected: the assembled system prompt embeds instruction files and environment; the opencode plugin's decided privacy contract governs it. | ANSWERED — `autonomous: recommendation accepted` (2026-10-23) |
| D6 | How do skills surface on the Skills view? | **New listing source in `canon/skills.ts` reading raw `opencode.context.skills`** (JSON array of skill names, the Copilot `github.copilot.context.skills` pattern; `isInitial: true`; malformed value → named `notMeasurable` reason). The `readSystemPromptListing` fallback still applies for skills embedded in prompt text. | `baseRecord` already stores span attributes as `raw`, so no store/synth change; `readListingOf`'s per-harness key pattern with honest not-measurable reasons; per-turn listings dedup through `uniqueByName`. `kyber.skills.listed` on the synth path is untouched. | ANSWERED — `autonomous: recommendation accepted` (2026-10-23) |
| D7 | The plugin has no opencode-loadable entry — build one, and who installs it? | **New entry module in the plugin directory** exporting the opencode-loadable shape(s) T0 pins (v1 default-export hooks map and/or v2 `Plugin.define({ id, setup })`), bridging to `createInstalledOpencodePlugin` + `registerOpencodePlugin` with the consent gate. **No dash-CLI installer change in this plan** — install stays by-file per the runbook; installer automation becomes a todo candidate. | Grep-verified: no default export / `Plugin.define` / `ctx` anywhere in the plugin dir; only Kilo has an installer. The v2 package forms allow relative/absolute/`file:` paths (`install-targets.json`), so a file-installed directory is a legal v2 plugin. Installer automation is separable follow-up work, not needed for the defect. | ANSWERED — `autonomous: recommendation accepted` (2026-10-23) |
| AP1 | Approve-and-execute gate. | Approved for execution routing under autonomous mode. | Recorded per the bug-crusher escalation packet's autonomous-mode instruction; the conductor still presents the gate and routes execution to specialists. | `autonomous: recommendation accepted` (bug-crusher escalation packet, 2026-10-23) |

## 5. Task graph

File scopes optimistic per the plan-authoring reference; a task overruns its scope only
by escalating. Every task ends with the worker's own `task-gate` run; a failing gate goes
straight back to the same worker. Required skills are named, not agents.

| Task | Objective | Files / symbols (exact scope) | Depends on | Skills |
|---|---|---|---|---|
| T0 | Pin the live registration + context payload contract **verbatim** (web pass via research-agent with network, or owner-supplied `@opencode/plugin` types / opencode.ai v2 docs / anomalyco plugin source): v2 `Plugin.define`/`setup`/`ctx.session.hook("context")` shape and payload fields (system, tools, skills, session/model identity); v1 `chat.params` / `experimental.chat.system.transform` / `tool.definition` signatures; file-installed-dir loadability for both targets. Record as fixtures + telemetry-inventory addendum update. **Hard gate: no T2–T4 emission code before this lands; T0 may adjust the entry's concrete shape inside the D2 ladder without reopening the plan.** | `dash/src/capture/harnesses/opencode-plugin/fixtures/hook-catalog.json` (+ new `context-payload.json` skeleton), `docs/dash/telemetry-inventory.md` (addendum) | — | test-dev, docs-dev |
| T1 | Envelope v2: bump `OPENCODE_CONTRACT_VERSION` to `'2'`, add the `opencode.context.` marker prefix and exported accessors (`opencodeEnvelopeContextSystem/Tools/Skills`), extend the claim/mapping rules for context spans (D3, D4). | `dash/src/canon/adapters/opencode-envelope.ts`, tests `opencode-envelope.test.ts` | T0 | test-dev |
| T2 | Plugin payload + registration: add `'session.context'` to `OpencodeHookKind`; extend `OpencodeHookPayload` with the T0-pinned context fields (`systemPrompt?`, `toolsText?`, `skills?`, `model?`, `agent?`, message/turn identity as pinned); register the context handler on the host surface (v2 namespaced + v1 dotted per the T0 pin) through `registerOpencodePlugin`; lifecycle kinds unchanged. | `dash/src/capture/harnesses/opencode-plugin/plugin.ts`, tests `plugin.test.ts` | T0 | test-dev |
| T3 | Loadable entry + consent gate: new entry module exporting the opencode-loadable shape(s) T0 pins, wiring `createInstalledOpencodePlugin` + `registerOpencodePlugin`; the D5 consent env gate drops content fields pre-emit when unset (identity/markers still flow); never-fail-host preserved; `check:reachable` stays clean. | New `dash/src/capture/harnesses/opencode-plugin/entry.ts` (name per T0 pin), tests colocated; `dash/src/capture/harnesses/opencode.ts` (status env snippet, declare-only) | T0, T2 | test-dev |
| T4 | Mapper + sender: carry the context fields onto envelope v2 attributes (`opencode.context.system/.tools/.skills`, model markers when named); `eventToSpan` includes them in `buildOtlpTracesBody`. | `…/opencode-plugin/mapper.ts`, `…/opencode-plugin/transport/otlp-sender.ts`, tests `mapper.test.ts` + transport tests | T2 | test-dev |
| T5 | Adapter bucketing + skills listing: `applyEnvelope` buckets context system → `system_prompt` and tools JSON → `tool_definitions` (the `legacyAiParts` pattern; ordering after existing parts); context-marker spans map `llm.invoke` per D4 with zero fabricated counters; `canon/skills.ts` gains the `opencode.context.skills` listing source (D6). | `dash/src/canon/adapters/opencode.ts`, `dash/src/canon/skills.ts`, tests `opencode-envelope.test.ts` / `opencode.test.ts` / `skills.test.ts` | T1 | test-dev |
| T6 | Governed docs: telemetry-inventory OpenCode row (context capture, consent key, measurability declaration — skills/tool definitions measurable only under consent), architecture envelope-contract section (contract `'2'`, context keys, entry, consent, claim rules), runbook OpenCode plugin paragraph (consent env var, restart-to-upgrade note: `'1'` spans quarantine after the bump, live check steps). | `docs/dash/telemetry-inventory.md`, `docs/dash/architecture.md`, `docs/dash/runbook.md` | T0, T4, T5 | docs-dev |
| T7 | Validation + closeout: dash gates (`typecheck`, `lint`, `test`, `check:reachable`), display-layer guard, `docs validate . --merge-ready` + `docs drift .` (conductor-run where the harness lacks the tool), whole-change review, closeout harvest mapping (§8), owner live-capture gate recorded. | No source; this plan file (§8), `docs/plans/README.md` row | T1–T6 | docs-dev |

Dependency graph: `T0 → {T1, T2}`; `T2 → {T3, T4}`; `T1 → T5`; `{T4, T5} → T6 → T7`;
T3 runs alongside T4 after T2. T5 may start once T1 lands (its tests build envelope
attributes directly, the existing `opencode-envelope.test.ts` pattern); T4 and T5 file
scopes do not overlap.

## 6. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | Fixtures + addendum (no executable test) | `npm --prefix dash run test -- opencode-plugin` (existing suites stay green) | The pinned verbatim types/fixtures exist and the addendum cites them; T2–T4 cite the fixture in their tests | Gate check: the fixture file is absent before T0 (trivially RED); T0 fails closed if the web pass cannot quote the types — escalate, do not guess | Fixture + addendum landed with source URLs/quotes; subsequent tasks reference it |
| T1 | `dash/src/canon/adapters/opencode-envelope.test.ts` | `npm --prefix dash run test -- opencode-envelope` | A `'2'`-stamped context span is the envelope and is supported; a `'1'`-stamped span now quarantines under `unknown opencode contract version`; checkpoint written under `'1'` is not reusable under `'2'` | New tests fail against the current tree (constant `'1'`; context keys unrecognized) before the bump | Same tests pass unweakened; mixed-batch outcome accounts every row; quarantine reason exact-matched |
| T2 | `dash/src/capture/harnesses/opencode-plugin/plugin.test.ts` | `npm --prefix dash run test -- opencode-plugin/plugin` | `session.context` fires through the plugin host with the pinned payload shape; a throwing/timeout handler never fails the host; existing lifecycle kinds unchanged | New kind test fails (kind absent from `OPENCODE_PLUGIN_HOOK_KINDS`, payload type rejects content fields) | Context event delivered with content fields intact; failure-injection test proves host continues; lifecycle order tests green |
| T3 | Entry tests colocated + `npm --prefix dash run check:reachable` | `npm --prefix dash run test -- opencode-plugin/entry` + `npm --prefix dash run check:reachable` | The entry exports the T0-pinned loadable shape(s) and bridges to the installed pipeline; consent unset → emitted events carry no content fields but still carry identity/markers; consent set → content rides; registration rejection tolerated | Entry module absent pre-task (test fails to import); consent-gate test fails with content flowing un-gated | Loadable shape asserted against the pinned fixture; both consent states observable at the emitted event; `check:reachable` clean unweakened; never-fail-host preserved |
| T4 | `…/opencode-plugin/mapper.test.ts` + transport sender tests | `npm --prefix dash run test -- opencode-plugin` | Context payload → attributes round trip (`opencode.context.system/.tools/.skills`, model markers when named); `buildOtlpTracesBody` includes them on the wire span | Mapper test on the T0 fixture fails pre-mapping (fields dropped); wire-body assertion fails (attributes absent) | Fixture round-trips byte-exact; wire body carries context attributes; no synthesis of absent fields (empty/absent stays absent) |
| T5 | `opencode-envelope.test.ts` / `opencode.test.ts` / `skills.test.ts` | `npm --prefix dash run test -- opencode-envelope opencode skills` | A `'2'` context span with model markers (or context markers alone, D4) becomes an `llm.invoke` record with `system_prompt` + `tool_definitions` parts; `opencode.context.skills` JSON array reads as a measurable listing; malformed → named `notMeasurable`; zero fabricated counters | Adapter test fails (context keys bucket nowhere — parts empty); skills test fails (no listing source for the key) | Record parts and content carry both buckets in order; skills listing measurable with dedup; malformed value yields the named reason; tokens stay un-fabricated; existing envelope/legacy-path tests green |
| T6 | Docs gates (read-only-verified edits) | `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` + `docs drift .` (conductor-run if the harness lacks the tool) | The three governed docs describe contract `'2'`, the consent key, the entry, and the restart/upgrade behavior truthfully | Pre-edit: the docs describe contract `'1'` with no context capture (drift against the landed code) | `docs validate` zero findings; `docs drift` zero findings; telemetry-inventory measurability declaration matches shipped behavior |
| T7 | Whole-change gates + review | §7 commands + `review gates` | All dash gates green; display-layer guard green; closeout harvest complete; owner live-capture gate recorded `[NOT LIVE-VERIFIED]` unless performed | Gate failure blocks closeout (no silent waiver) | Evidence bundle recorded in §8; review verdict recorded; plan archived per `KW-DOC-LIFECYCLE-003` by the finishing PR |

No-test tasks: none. Changing an approved Test-contract row (including weakening an
assertion to reach green) is a scope change: return this plan to Draft and require the
conductor to relay reapproval before implementation resumes.

## 7. Risks, boundaries, verification gates

**Risks.**

1. **Live API differs from the survey (G1).** T0 is a hard gate; the D2 fallback ladder
   keeps the architecture valid for v1 hooks proven live by Kilo. If T0 fails closed
   (neither web pass nor owner types available), T2–T4 do not start and the plan returns
   to the conductor as BLOCKED — no guessed emission contract.
2. **Pre-upgrade spans quarantine after the bump.** Intended checkpoint-invalidation
   behavior; runbook documents the restart/upgrade path (T6).
3. **Content volume.** System prompts and tool schemas are large; the outbox is bounded
   (drop-oldest, batch halving) and the store compresses `raw` (R12.4) — no new bound is
   invented here; T4 asserts absent fields stay absent rather than empty-string padding.
4. **Desktop v1 loader quirks** (cache-path regression history, issue #12222): the
   runbook's Desktop disable-plugins/clear-cache/restart guidance is extended, not
   replaced.
5. **Consent naming drift.** `KYBERDASH_OPENCODE_CONTEXT` is finalized in T3 and pinned by
   test + docs together; the always-off-by-default semantic (D5) is the invariant.

**Out-of-scope boundaries.** §1.3. In particular: no lifecycle-hook rewrite, no
installer automation, no native-span correlation, no web/tray change, no new dependency.

**Verification gates (T7).**

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli -- docs drift .
```

Plus the display-layer guard (`dash/src/architecture/display-layer.test.ts`, inside the
dash suite) and the whole-change review (`review gates .` / council) before closeout.
Owner live-capture gate (G2): after install + consent + restart, emit one opencode turn
and confirm the session's Context view shows system prompt and tool definitions and the
Skills view shows the listing — recorded `[NOT LIVE-VERIFIED]` if the owner does not
perform it.

## 8. Review and closeout (docs-dev)

Whole-change review after T1–T6 (the bug-crusher routing presents the **approve and
execute** gate first; this plan's AP1 is recorded in §4). Closeout:

- **Harvest mapping:** `docs/dash/telemetry-inventory.md` (OpenCode row: context capture,
  consent key, measurability — T6), `docs/dash/architecture.md` (envelope-contract
  section: contract `'2'`, context keys, entry, consent — T6), `docs/dash/runbook.md`
  (OpenCode plugin paragraph — T6).
- **ADR disposition:** default **ADR waiver** — the work refines the existing
  contract-versioned envelope (2026-10-22 plugin plan), the display-layer rule (ADR
  0033), capture key ownership (ADR 0031), and the enrichment pattern (ADR 0009) with no
  new boundary, dependency, or external surface. If T0's pin reveals a new lasting
  invariant (e.g., a loadable-entry contract other repos must copy), the closeout
  escalates that single item to an ADR instead.
- **Deferred work → todos (only if the user accepts):** dash-CLI installer automation for
  the opencode plugin file copy; native-span correlation if turn pairing proves disjoint
  in live use.
- Archive per `KW-DOC-LIFECYCLE-003` by the finishing PR; the plan-index row moves to the
  archived table with the closeout mapping.

## Design harvest

Targets are component + doc type. The closeout mapping below maps every H id and every
decision ID to the resolved docs.

| ID | Design item (section of this plan) | Target component | Target doc type |
|---|---|---|---|
| H1 | Envelope v2: contract bump `'1'`→`'2'`, `opencode.context.*` keys (D3, T1) | KyberDash | architecture |
| H2 | `session.context` hook + loadable entry + v1 fallback ladder (D2, T2–T3) | KyberDash | architecture |
| H3 | Adapter bucketing + self-contained `llm.invoke` record, zero fabricated counters (D4, T5) | KyberDash | architecture |
| H4 | Consent gate `KYBERDASH_OPENCODE_CONTEXT=1`, off by default (D5, T3) | KyberDash | architecture; runbook; reference |
| H5 | Skills listing source from raw `opencode.context.skills` (D6, T5) | KyberDash | architecture; reference |
| H6 | In-repo extension; standalone raw-JSON route rejected (D1) | KyberDash | architecture |
| H7 | Telemetry-inventory OpenCode row: context capture, consent-gated measurability (T6) | KyberDash | reference |
| H8 | Runbook plugin paragraph: consent, restart-to-upgrade, live check (T6) | KyberDash | runbook |

## Completion summary (T12 2026-10-25)

Delivered T0–T6 test-first with whole-change review (per the archived index row:
review with re-gate 18/18, dash suite 7063/7063) and archived 2026-10-24 per
`KW-DOC-LIFECYCLE-003`. This T12 backfill adds the scale frontmatter, the archived
status line, this Design harvest, and the Closeout mapping below — the Decisions table
and every original section are untouched. The T12 pass also wrote the two missing
design items into the architecture doc: the standalone-route rejection (H6/D1, Entry
bullet) and the skills-listing source (H5/D6, Mapping bullet + skills-view contract).

Open owner items (not residuals): G2 live-capture gate (consent set, restart, Context
and Skills views) — recorded `[NOT LIVE-VERIFIED]` in the inventory row, the runbook
paragraph, and the architecture envelope section.

## Closeout mapping

| ID | Destination |
|---|---|
| H1 | [dash/architecture.md](../../dash/architecture.md), "OpenCode plugin span/turn envelope" — Version key (`'2'`) + Context keys bullets |
| H2 | [dash/architecture.md](../../dash/architecture.md), same section — Context keys payload pin + Entry bullet (v2 + v1 surfaces, `session.context` registration) |
| H3 | [dash/architecture.md](../../dash/architecture.md), same section — Claim rule + Mapping bullets (bucketing, self-contained `llm.invoke`, zero fabricated counters) |
| H4 | [dash/architecture.md](../../dash/architecture.md), same section — Consent bullet; [dash/runbook.md](../../dash/runbook.md), OpenCode plugin paragraph; [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md), OpenCode plugin row |
| H5 | [dash/architecture.md](../../dash/architecture.md), same section — Mapping bullet (skills-listing source sentence, added T12) + skills-view contract (OpenCode measurable under consent, corrected T12); [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md), OpenCode plugin row |
| H6 | [dash/architecture.md](../../dash/architecture.md), same section — Entry bullet (standalone-route rejection sentence, added T12) |
| H7 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md), OpenCode plugin row |
| H8 | [dash/runbook.md](../../dash/runbook.md), OpenCode plugin paragraph |
| D1 | [dash/architecture.md](../../dash/architecture.md) — Entry bullet (in-repo extension; standalone raw-JSON route rejected) |
| D2 | [dash/architecture.md](../../dash/architecture.md) — Context keys + Entry bullets |
| D3 | [dash/architecture.md](../../dash/architecture.md) — Version key + Context keys bullets |
| D4 | [dash/architecture.md](../../dash/architecture.md) — Claim rule + Mapping bullets |
| D5 | [dash/architecture.md](../../dash/architecture.md) — Consent bullet; [dash/runbook.md](../../dash/runbook.md), OpenCode plugin paragraph |
| D6 | [dash/architecture.md](../../dash/architecture.md) — Mapping bullet (skills-listing source); [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md), OpenCode plugin row |
| D7 | [dash/architecture.md](../../dash/architecture.md) — Entry bullet; the installer-automation candidate takes **Documentation waiver — deferred candidate:** no owner acceptance (todos exist only when accepted), no surviving invariant. |
| AP1 | **Documentation waiver — transient approval record:** the approve-and-execute provenance is preserved in plan §4. |
