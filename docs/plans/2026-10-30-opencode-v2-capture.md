---
id: plans/2026-10-30-opencode-v2-capture
title: "OpenCode V2-only capture — rebuild kyberDash's OpenCode collection as a V2 plugin pipeline"
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-30
development-mode: test-first
documentation-impact: extends-component
impact-components: [KyberDash, KyberSquad]
---

# OpenCode V2-only capture — rebuild kyberDash's OpenCode collection as a V2 plugin pipeline

**Status: In progress — Wave 0 verification (code present; T15 docs open). Approved for
execution 2026-10-30 (AP1).**

**Development mode:** `test-first` (supplied in the packet). **Autonomy:** `autonomous` (supplied in
the packet; matches the repository root `AGENTS.md` Config Reg `<autonomy>`).

Branch: `fix/opencode-v2-capture`, cut from `origin/main` `8612eb4` (= tag `v0.1.8-rc.12`).

## 1. Problem and goal

### 1.1 Owner requirements (verbatim intent, from the packet)

- Collect for every OpenCode session: the system prompt, user prompts, skills (listed and
  invoked), tools offered and tools invoked, and all usage: input, output, reasoning, cache-read
  and cache-write tokens, cost, and duration.
- Support OpenCode V2 only. V1 is deprecated; remove V1 support rather than keep it alongside.
- No claim of "done" is acceptable without a live run on this machine showing the data in
  kyberDash. The owner has been told this was fixed several times when it was not.

### 1.2 Problem

The diagnosis (reproduced live on 2026-10-30 against OpenCode CLI 2.2.1 and Desktop 2.2.2, one
shared background server, kyberDash `0.1.8-rc.12` = `origin/main`) found that OpenCode 2.2.1
hands a V2 plugin every field the owner wants, and kyberDash keeps almost none of it:

| Loss | Where (this checkout) |
|---|---|
| Usage, cost, turn boundaries never captured: the plugin never calls `ctx.event.subscribe()` | `dash/src/capture/harnesses/opencode-plugin/entry.ts:326-387` registers only `session.hook("context")` and `tool.hook("execute.before"/"execute.after")` |
| Tool spans read with V1 field names (`callID`, `success`, `errors`, text output) | `entry.ts:261-319` |
| Tool name, call id, status, duration, size dropped on the wire, so tool spans quarantine as `non-model span` | `transport/otlp-sender.ts:98-160`; `canon/adapters/opencode.ts:486-512` |
| Spans are instants (start = end), so session duration is 0 | `otlp-sender.ts:158`; `canon/sessions.ts:1801` |
| Every model step is a parentless span, so each step is its own "request" with "1 turns" | `canon/sessions.ts:1792`, `:1845-1852`; `web/src/components/AgentSessionDashboard.tsx:939-958` |
| Tools offered parse to `[]` because OpenCode sends an object keyed by name | `canon/sessions.ts:816-851` |
| Content (system prompt, tools, skills listing) dropped: consent file holds `content: false` | `entry.ts` consent read; `~/.kyberdash/opencode-plugin.json` |
| Native OpenCode OTel carries no usage at 2.2.x (0 of 55,261 spans); the V1-table import reads empty `session`/`message`/`part` tables | `canon/adapters/opencode.ts` (OpenInference/AI SDK paths); `providers/sqlite-session-parser.ts:102,168` |
| Settings "OpenCode Enabled" writes `experimental.openTelemetry`, which 2.2.x ignores | `capture/harnesses/opencode.ts:65,92-94` |

Every prior plan (archived 2026-10-22, 10-23, 10-25, 10-27, 10-28; PR #457) was proven against
fixtures or a hand-seeded session row (`dash/e2e/harness-collection-world.ts` direct
`store.upsertSession`) and closed with its live check marked "not live-verified" or "procedure
only".

### 1.3 Goal

Replace the OpenCode collection path with one V2-only plugin pipeline — OpenCode V2 hooks and bus
events → KyberDash plugin → OTLP contract version `3` → OpenCode adapter → canonical store →
shared projection → `/api/kyber/*` → web dashboard — that records every owner field for every
OpenCode session, remove every V1 path, and prove it with an agent-run live OpenCode V2 session on
this Mac whose kyberDash numbers equal OpenCode's own totals, plus the full test suite on Linux.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D0 | Development mode | `test-first`. | Supplied in the packet; repository default. | ANSWERED — packet |
| D1 | Which OpenCode runtime is supported? | **OpenCode 2.x only, verified on CLI 2.2.1 and Desktop 2.2.2.** V1 (1.x) support is removed, not kept beside V2. The plugin degrades without throwing when a V2 surface is absent (no `ctx.event` → hooks only). | Owner requirement (packet). Live evidence: 1.19.4 failed to load the V2 `{id, setup}` export (`opencode.log` 2026-10-27T01:06:48Z); 2.2.1 loads it. Versions below 2.2.1 are UNVERIFIED and documented as such. | ANSWERED — owner requirement (packet) |
| D2 | How are requests and turns grouped? | **Request = one OpenCode execution** (`session.execution.started` → `succeeded`/`failed`/`interrupted`); **turn = one model step** (`session.step.started` → `session.step.ended`); **tool call = child of the step** whose `assistantMessageID` equals the tool's `messageID`. Wire: a parentless `opencode.request` span per execution, child `opencode.step` spans, grandchild `opencode.tool` spans, one trace per OpenCode session. | Live payloads (runA probe-log lines 5, 12, 23, 26, 62): execution events carry only `sessionID`; step events carry `assistantMessageID`; tool hooks carry `messageID` and `id`. The projection already counts parentless records as requests (`sessions.ts:1792`) and `llm.invoke` records as turns (`turnsOf`, `:427-433`); the request root uses op `agent.invoke`, which `isRollup` (`:415-418`) already excludes from turns. Same shape as the Cursor hook turn assembler (`dash/src/otel/cursor-hook.ts:121-172`). | ANSWERED — autonomous: decided from live payloads and existing projection rules |
| D3 | Where does duration come from, and how does the session total avoid double counting nested spans? | **Every span uses OpenCode's own `created` timestamps** (execution, step, `session.tool.called` → `session.tool.success`/`failed`). **Session `duration_ms` = sum of request-root durations when a session's records nest; unchanged (sum of all records) for flat sessions.** Per-step and per-tool durations stay on their records for turn and tool timing. | Today `duration_ms` sums every record (`sessions.ts:1801`); with nesting that would count each second up to three times. The rule is generic (any nested harness) and is regression-pinned for flat harnesses. runA ground truth: execution 1793337354060 → 1793337367427 = 13,367 ms. | ANSWERED — autonomous: low-confidence, no authoritative source (shared projection change; cost of wrong: other harnesses' displayed duration; mitigated by a flat-session regression test and a scope-change rule in T7) |
| D4 | Cost: OpenCode-reported cost or kyberDash published rates? | **OpenCode-reported cost, carried verbatim** as `{ basis: 'harness', status: 'priced', value, currency: 'USD' }` per step, a reported `0` included. A step with no `cost` field is `{ basis: 'unknown', status: 'no_rate' }`. No published-rate fallback for OpenCode. | (1) It is OpenCode's own accounting for the provider actually used, including subscription plans (`opencode-go` reports 0) and Zen pricing; (2) kyberDash's published table does not price OpenCode Zen/Go models (runA cost block `no_rate`), and OpenCode is not in the LiteLLM applicability set (`canon/published-pricing.ts:8-18`); (3) it equals `session_v2.cost`, so the live gate can compare 1:1; (4) the canonical rule already lets a harness-reported figure win outright (`canon/cost.ts:224-242`). Currency: OpenCode's `cost` carries no currency field; models.dev publishes USD (UNVERIFIED in OpenCode source, recorded as an assumption). | ANSWERED — autonomous: decided from `canon/cost.ts` R5.2 rule and live `session_v2` evidence |
| D5 | Consent: default and how capture is enabled for this owner | **Default stays off at install** (`squad install`/`kyber-weave update`/`capture plugin install` never turn content on). **The owner enables content with the Settings "OpenCode Enabled" toggle, which is `kyberdash kyber capture enable --harness opencode`: install the plugin and set `content: true`.** The live gate runs that exact command, so the owner ends the gate with content on. Content = system prompt, tool definitions, skills listing, user prompt text, session title. Metadata (tokens, cost, timings, request/turn structure, model, agent, tool-call names/status/sizes, invoked skill ids) always flows. `KYBERDASH_OPENCODE_CONTEXT` env override stays. | Privacy-by-default matches the accepted 2026-10-28 D1 ("consent-on is part of Enabled") and ADR 0031 ownership; the owner's explicit wish is honoured by one documented action that the gate itself performs. Session titles are model summaries of the prompt, so they are content. Invoked skill ids are identifiers like tool names, so they are metadata. | ANSWERED — autonomous: decided from the accepted 2026-10-28 D1 and the packet's stated owner wish |
| D6 | What happens to OpenCode native-OTel ingestion? | **Removed.** The OpenCode adapter claims only plugin envelope contract `3`. The AI SDK, OpenInference and legacy `ai.*` mappings, their fixtures and tests are deleted. Native OpenCode spans (most carry `attributes: {}`) fall to the existing unclaimed quarantine. KyberDash does not ask the owner to unset `OTEL_EXPORTER_OTLP_ENDPOINT`, because other harnesses share that variable. | 0 of 55,261 native spans carried usage at 2.2.x; the config key is ignored ("omitted unsupported legacy setting"). | ANSWERED — autonomous: decided from live evidence (diagnosis §3) |
| D7 | What happens to the V1-table import? | **Removed:** `providers/opencode.ts`, `providers/opencode-file-parser.ts`, `synth/readers/opencode.ts`, the OpenCode import job and provider registration. **No V2 database importer is added.** `opencode` stays a known harness id for scope resolution (`dash clean --harness opencode`) with no folder source. Kilo's use of `providers/sqlite-session-parser.ts` and `providers/session-message.ts` is unchanged. | The plugin is the single source; a second V2 source would double count. `session_v2` retention is not durable (diagnosis §3: sessions vanish; cause UNVERIFIED). `dash clean` resolves scope through the refresh registry (`clean/clean.ts:133-159`), so the id must stay known. | ANSWERED — autonomous: decided from owner V2-only requirement and diagnosis evidence |
| D8 | What does the Settings toggle write? | **No `opencode.jsonc` key.** OpenCode becomes a plugin-only capture writer: enable = strict plugin install + consent on; disable = consent off + plugin removal; status = plugin state, consent, receiver liveness. **One-time migration:** enable and disable restore the `experimental.openTelemetry` key KyberDash previously wrote, from the capture receipt, and drop that receipt entry; a drifted value is left in place with a warning. | 2.2.1 ignores the key and warns on every start (diagnosis §3). The receipt already records prior values for restore (`capture/index.ts` enable/disable path). | ANSWERED — autonomous: decided from live evidence and the existing receipt-restore contract |
| D9 | Wire contract version | **Bump `opencode.contract_version` from `2` to `3`.** Contract `2` spans quarantine under the existing unknown-version reason. | The attribute set, span kinds and parentage all change; the envelope module already defines a bump as the invalidation mechanism (`opencode-envelope.ts:25-36`). | ANSWERED — autonomous: decided from the envelope contract |
| D10 | Duplicate emission: one global plugin loads once per directory instance and again on hot reload, and the event bus delivered another session's events to a project probe (runA lines 63-93) | **Three guards.** (1) Ownership: an instance emits a session only after one of its own hooks fired for that session, or when the event's `location.directory` equals `ctx.location`'s directory; events for unowned sessions are held in a small bounded buffer, then dropped. (2) Deterministic ids: trace id and span ids are SHA-256 derivations of session id plus anchor id (execution-started event id, `assistantMessageID`, tool call id), so the store's `INSERT OR REPLACE` on `span_id` (`canon/store.ts:326`, `:2766`) makes re-delivery idempotent. (3) Lifecycle: `setup` returns the V2 `Cleanup` that stops the subscription and flushes the outbox; a process-global generation registry keyed by instance directory stops a superseded generation. | `Plugin.setup` may return `Cleanup` (`@opencode/plugin@2.2.1` `promise/plugin.d.ts:58-62`); `Context.location` exists (`:27`). Without ownership, an instance that never saw the context hook would overwrite a content-bearing step with a content-free one. | ANSWERED — autonomous: decided from the 2.2.1 type package and live probe evidence |
| D11 | Free text in `record.raw` breaks the timeline: subagent and auxiliary markers (`subagent`, `title`) are matched against every raw attribute key and string value (`analysis/timeline.ts:86-122`), and OpenCode's tool catalogue contains a `subagent` tool | **The adapter keeps only metadata keys in `record.raw`; content goes to parts only. Timeline evidence matching ignores keys and values under the `kyber.*` namespace.** Real evidence still works: a tool span named `subagent` is subagent evidence; a step whose request kind is `title` is auxiliary. | Found during this investigation; contract-2 records with consent on carried the whole system prompt and tool catalogue in raw. | ANSWERED — autonomous: low-confidence, no authoritative source (shared timeline change; cost of wrong: subagent/auxiliary grouping for other harnesses; mitigated by keeping the exclusion to the KyberDash-owned `kyber.*` namespace) |
| D12 | Session label | **OpenCode's session title (from `session.renamed`) under the generic key `kyber.session.label`, consent permitting; otherwise the existing `formatSessionTitle` fallback ("OpenCode session"). Never the span name.** | Today every session is labelled `opencode.session.context` (`sessions.ts:1779`). The key sits in `kyber.*` so D11's exclusion keeps a title like "fix subagent title" from re-grouping the timeline. | ANSWERED — autonomous: low-confidence, no authoritative source (cheap to reverse) |
| D13 | Token convention | **OpenCode V2 classes are disjoint:** `input` is fresh input, `cache.read`/`cache.write` are separate, `reasoning` is separate from `output`. Canonical mapping: `freshInput = input`, `cacheRead = cache.read`, `cacheCreation = cache.write`, `output = output + reasoning`, `reasoning = reasoning`. | Canonical `reasoning` is a subset of `output` (`canon/types.ts:30-50`). runA step 1 reports `output 69`, `reasoning 957` — reasoning larger than output is only possible if they are disjoint. Sum of per-step `input` (10935 + 1259 + 439) = `session_v2.tokens_input` 12633 and per-step `cache.read` sums to 27475 = `tokens_cache_read`, so `input` excludes cache. | ANSWERED — autonomous: decided from live payload arithmetic |
| D14 | Tools offered and tool content | **The plugin serializes `context.tools` (an object keyed by name) as a JSON array `[{ name, description, input }]`** under content consent, so the existing splitter (`sessions.ts:816-851`) names each tool without a projection change. **Tool inputs and results are not captured as content in this plan**; only name, call id, status, error type, input/result byte sizes, and the `skill` tool's skill id. | Smallest change that fixes `tools_offered: []`; tool I/O content is not an owner field and grows storage per read. | ANSWERED — autonomous: decided (cheap to reverse) |
| D15 | Skills invoked sources | **Three sources, de-duplicated per turn:** the `skill` tool's `input.id`, `prompt.skills[].id` from the `prompt` hook (applied to the first step of that execution), and `session.skill.activated` if it fires. The plugin stamps the ids on the step as `kyber.skills.invoked`; the projection's existing per-turn max rule (`canon/skills.ts:181-219`) prevents double counting. | runA shows the tool path; runC shows the attached-prompt path; `session.skill.activated` (schema `{sessionID, id, name, text}`) never fired in either (UNVERIFIED which flow emits it). | ANSWERED — autonomous: decided from live payloads and the 2.2.1 schema |
| D16 | Where do user prompts surface? | **The `prompt` hook's `prompt.text` rides on the request root as `opencode.prompt.text` (content).** The adapter appends it as a `conversation_history` part on the root. **The projection adds `requests[i].prompt`**, a preview of at most 200 characters, `null` without consent. The web user-requests list shows the preview and the request's turn count instead of the span id. | Today the list prints span ids (`AgentSessionDashboard.tsx:949-957`). Additive API field; the browser renders only. | ANSWERED — autonomous: decided (additive, cheap to reverse) |
| D17 | Existing OpenCode rows in the owner's `canon.db` (built from counter-less contract-2 spans) | **No automatic wipe.** The runbook documents `kyberdash dash clean --harness opencode --no-reingest --yes`, and the conductor offers it to the owner in the final report. The live gate queries its own session by id, so old rows cannot mask a failure. | Deleting owner data is hard to undo and was not requested. | ANSWERED — autonomous: decided (outward-facing action left to the owner) |
| D18 | Local deploy path and version for the live gate | **The documented local release path:** `scripts/release-local.sh --version 0.1.8-rc.12.local.<n>` → `scripts/local-release-server.py` on loopback → `KYBER_WEAVE_RELEASE_ORIGIN=http://127.0.0.1:<port> kyber-weave update 0.1.8-rc.12.local.<n> --no-menubar`. `<n>` starts at 1 and increments per gate attempt. | `docs/distribution.md` §"Verifying a release locally" and `docs/install.md` (`KYBER_WEAVE_RELEASE_ORIGIN`). `rc.12.local.n` sorts above the installed `rc.12` and below the next real `rc.13`, so normal updates resume after merge. `--no-menubar` because the local release carries no tray installer and the tray step would otherwise fail by name (`distribution.md` `tray-delegated` case). The update also upgrades the installed plugin (`--preserve --if-installed`) and replaces the running engine. Whether the CLI's version comparison accepts the `.local.<n>` suffix is UNVERIFIED; a refusal blocks the gate and is reported, never worked around. | ANSWERED — autonomous: decided from `docs/distribution.md` and `docs/install.md` |
| D19 | Evidence location | **`artifacts/evidence/opencode-v2-live-gate/<UTC-stamp>/`** (the repository's gate-output root, gitignored like `artifacts/gates.json`), plus a committed summary table in this plan's §8.4 at closeout. Secrets are stripped before writing (no `Authorization` or key headers, no provider tokens). | `AGENTS.md` writes gate output under `artifacts/`; `.gitignore:17` ignores it, so the committed plan carries the durable summary. | ANSWERED — autonomous: decided from `AGENTS.md` conventions |
| D20 | What happens to the seeded e2e scenario? | **Replaced and relabelled.** The seeded "measured opencode round trip" (`store.upsertSession` of a hand-written payload) is replaced by a replay scenario: real 2.2.1 fixture events → production plugin pipeline → OTLP body → receiver decode → OpenCode adapter → `projectCanonicalStore` → dashboard. The honest-empty seed stays, relabelled "rendering-only seed — not capture proof". Neither counts as live proof; docs stop calling the e2e "proof of capture". | Packet requirement; diagnosis §5. | ANSWERED — owner requirement (packet) |
| D21 | Linux gate | **Two parts, both required when available:** (a) the full suite in a local Linux container that mirrors CI (Ubuntu, .NET SDK from `global.json`, Node from `dash/.nvmrc`, the `ci.yml` `build-test`, `ts-gates` and `dash-e2e` commands plus `npm run test:locks`); (b) the pull request's GitHub Actions Ubuntu jobs green on the final head. If no Docker-compatible runtime is installed on this Mac (UNVERIFIED), (b) alone satisfies the gate and the evidence says so. | Owner requirement (packet; memory "test on Linux"). | ANSWERED — owner requirement (packet); runtime availability autonomous: low-confidence |
| D22 | Squad installer and `kyber-weave update` guidance | **The C# steps relay the dash verb's own `harness opencode: …` guidance lines instead of a hard-coded "Restart OpenCode" line.** KyberDash owns the wording (ADR 0031). The new wording, owned by `install.ts`, is: "OpenCode 2.x picks up the capture plugin for new sessions; if KyberDash shows no new OpenCode sessions, restart OpenCode." | Hot reload of edited plugin files is proven for a project plugin and UNVERIFIED for the global file (diagnosis §3); the sentence is true either way, and the live gate records which case held. Hard-coded strings live at `KyberDashCapturePluginStep.cs:120` and `SelfUpdater.cs:450`. | ANSWERED — autonomous: decided from ADR 0031 ownership |
| D23 | Model rule for every OpenCode run | **Pass the model explicitly:** `opencode-go/muse-spark-1.3-contributor`, fallback `opencode/big-pickle`. Never an Anthropic model, never the default. `opencode-go/space-bunny` currently answers `provider.no-route`. No unit, integration or e2e test calls a model; only the live gate (§8.3) runs OpenCode. No session may be left running. | Owner requirement (packet; memory rule). | ANSWERED — owner requirement (packet) |
| D24 | ADR | **New ADR 0046 "OpenCode capture is a V2-only plugin pipeline"; ADR 0031 amended in place** (OpenCode capture owns a plugin file and consent, no harness config key). | The choice retires two collection paths with real alternatives (native OTel, DB import) and constrains future OpenCode work; it is expensive to revisit. | ANSWERED — autonomous: decided from the ADR skill criteria |
| D25 | Is `experimental.idle` a terminal signal for flushing an open execution? (Raised during T2: §3.2 said "flush as `unknown` at the idle hook"; the T2 author pinned "only when no step is in flight".) | **No. Drop the idle flush (option b).** A request span is emitted only at `session.execution.succeeded`, `failed` or `interrupted`. The plugin does not register `experimental.idle`. The only fallbacks, each emitting the request once with outcome `unknown` and ending at the last event observed for that execution, are 30 minutes without events for the session and the plugin's `Cleanup` (reload or shutdown, when the in-memory state would otherwise be lost). **Test changes for T2:** replace "experimental.idle flushes an open execution as outcome unknown once no step is in flight" in `v2-replay.test.ts` with three tests: (1) "experimental.idle never closes a request" — replay runA as recorded and also with the idle line moved after `step.ended`; both emit exactly one request span, status ok, end 1793337367427 (= `execution.succeeded.created`); (2) "an execution without a terminal event is emitted once as unknown after 30 minutes without events" (fake clock; end = last observed `created`); (3) "cleanup emits each open execution once as unknown". Also assert the registered-surface list has no `experimental.idle`. The golden bodies do not change. | The 2.2.1 type doc says idle "runs … just before it goes idle", "does not run after interruption, failure", and input it admits "runs in the same execution" (`@opencode/plugin@2.2.1` `promise/session.d.ts:151-166`). So idle precedes the terminal event by design, can be followed by more steps in the same execution, and never fires on the failure paths where a fallback would matter. Verified ordering in `fixtures/v2/runA.events.jsonl`: idle at line 47 sits inside step 3 (started line 42, ended line 49), and `execution.succeeded` is at line 51. Option (a) still closes a request early whenever idle lands after the last `step.ended`, or when idle admits input, and a later `succeeded` would find no open execution. Option (c) doubles emissions, changes the golden bodies, and lets a projection that runs between the two emissions show `unknown`. Option (b) gives the live gate what it needs: two requests, each closed by its own `execution.succeeded`, outcome ok, with no false `unknown`. This clarifies §3.2 within the AP1 scope. No assertion is weakened: the change only adds assertions and removes a hook registration. | ANSWERED — autonomous: decided from the 2.2.1 type documentation and the committed fixture ordering; clarification within AP1 scope (conductor request 2026-10-30) |
| D26 | Scope-change ruling (§3.3 rule 2): the Antigravity corpus pin in `dash/src/canon/parts-discipline.test.ts` moves (`bad2d184…` → `c56b557e…`). Re-pin, or is rule 2 mis-scoped? | **Re-pin approved.** The structural diff against `HEAD` shows two leaves only: `summary.duration_ms` 88 → 84 and the additive `requests[i].prompt`. The corpus parents `parts-session-tool-0` (4 ms) to `parts-session-turn-0`, so the session nests; 84 = the seven parentless turns (6 × 12 + 12). 88 counted the child tool's 4 ms a second time, inside its parent turn. This is D3 applied as written, generic for any nested harness, and not a mis-scope. Flat sessions are still pinned unchanged by `sessions.nested-requests.test.ts` ("flat session: counts that must not move"). The pin comment must name both changes. Known edge: in a nested session, a record whose parent is absent from the session is neither a root nor counted in `duration_ms`. This matches `request_count`, which already ignores it. | `sessions.ts` `hasNesting` and the `duration_ms` reduction; corpus `parts-discipline.test.ts:127-188`. OTLP parentage means a child's time lies inside its parent's. | ANSWERED — autonomous: decided (scope-change rule ruling, conductor request 2026-10-30) |
| D27 | Prompt preview of a JSON-quoted prompt (`"\"Load the skill…\""` in runA) | **No unwrap in any layer. The preview is the literal `conversation_history` text (§3.3 rule 4).** Remove `unwrapJsonString` from `promptPreviewOf` in `dash/src/canon/sessions.ts`. Correct `fixtures/v2/runA.kyberdash-api-session.v3-expected.json` `requests[0].prompt` to the quoted text. The plugin, adapter and golden OTLP bodies do not change. The live gate's `run.mjs` passes each prompt as one argv element, with no embedded quotes. | The quotes are OpenCode's own data, not plugin double-encoding. The same quoted text appears in the `prompt` hook (runA line 3), OpenCode's `user` bus item (line 4) and the `model.request` message content sent to the model (lines 8, 23, 39). runC's `prompt` hook text has no quotes. So the runA probe typed the quotes. An unwrap would rewrite a genuine user prompt such as `"fix it"` for every harness. | ANSWERED — autonomous: decided from the committed runA/runC fixtures |
| D28 | Shape of `summary.tools_offered` and `requests[i].prompt` when content is withheld | **The shared not-measurable object `{ availability: 'not_measurable', reason }`** when the source declares `tool_definitions` (or the user-prompt metric) unmeasurable and recorded none. Absent or `null` when undeclared, as before; never `[]` or zero. This clarifies D16's "`null` without consent" to match the §5 T10 contract ("`not_measurable` with a reason naming content capture, never zero"). | Same shape as `schema`, `timeline` and `skills` in the payload, and `KyberUnmeasurable` in `web/src/lib/kyberApi.ts`. Every reader guards with `Array.isArray` (`canon/harnesses.ts:135,271`, `AgentSessionDashboard.tsx:652,709`, `compare.mjs:384-389`), so the object reads as "not offered" and is never counted. Follow-up (types only): widen `AgentSessionDashboard.tsx:71` to `string[] \| number \| KyberUnmeasurable \| null`. | ANSWERED — autonomous: decided from the §5 T10 contract and the existing API convention |
| AP1 | Approve-and-execute gate for this Draft plan | **Approved: the plan moves to Ready and is executable.** Scale `extends-component` with `impact-components: [KyberDash, KyberSquad]` and mode `test-first` are confirmed. Every autonomous row D1–D24 is accepted as recorded. | The conductor supplied explicit `AUTONOMOUS_APPROVAL: approve-and-execute` under resolved autonomy `autonomous` (repository Config Reg) on 2026-10-30. The conductor reports that the owner gave a general go-ahead in chat ("approved continue") before reviewing the plan. No task runs until the conductor dispatches it. G3 (live gate) and G2 (Linux) remain mandatory exit gates. | ANSWERED — autonomous: recommendation accepted (owner go-ahead "approved continue" given in chat before review, as relayed by the conductor) |

No hard-block escalation: every decision has a recommendation this plan stands behind. The two
shared-projection changes (D3, D11) carry regression pins and a scope-change rule.

## Design harvest

`documentation-impact: extends-component`; `impact-components: [KyberDash, KyberSquad]`.

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | KyberDash | adr | New ADR 0046: OpenCode capture is a V2-only plugin pipeline; native OTel and the V1-table import retired; contract `3`; the D1, D6, D7 rationale. |
| H2 | KyberDash | adr | ADR 0031 amended: OpenCode capture writes no harness config key; it owns a plugin file, its receipt and the consent file; the legacy key migration (D8). |
| H3 | KyberDash | architecture | `docs/dash/architecture.md`: the contract-3 envelope (§3.2 of this plan), request/step/tool grouping (D2), duration rule (D3), harness-reported cost (D4), consent split (D5), ownership and dedupe (D10), raw-metadata rule and `kyber.*` evidence exclusion (D11), label (D12), token mapping (D13), `requests[].prompt` (D16). |
| H4 | KyberDash | reference | `docs/dash/telemetry-inventory.md`: OpenCode rows rewritten to the V2 pipeline; V1, native-OTel and import rows removed; the T0 addendum of V1 hooks removed. |
| H5 | KyberDash | runbook | `docs/dash/runbook.md`: enable and verify OpenCode capture, the cleanup command (D17), the live gate procedure (§8.3). |
| H6 | KyberDash | onboarding | `docs/dash/onboarding.md`: OpenCode capture plugin section (V2 only, consent default, toggle). |
| H7 | KyberDash | reference | `docs/cli-command-surface.md`: `kyber capture enable/disable/status` and `kyber capture plugin install` for OpenCode; `update` plugin guidance line. |
| H8 | KyberDash | rule | `docs/rules/kyberdash-harness-collection.md`: OpenCode rows and the e2e relabel (D20). |
| H9 | KyberSquad | architecture | `docs/kyber-squad/architecture.md` and `docs/kyber-squad/onboarding.md`: the global OpenCode plugin step relays KyberDash guidance (D22). |
| H10 | KyberDash | runbook | `docs/install.md` §Updating: the plugin step guidance line (D22). |

## Execution state

As of 2026-10-30. Scope, design, decisions D0–D25 and the §5 Test contract are unchanged by this
section. D26–D28 are execution-time rulings within the AP1 scope.

**Assessment result (architect, read-only, 2026-10-30).**

- T1, T1b, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12a, T12b, T13a, T13b, T14a and T14b: code is
  on disk on `fix/opencode-v2-capture` and matches the plan's file scopes and constraints.
- T15 (documentation, H1–H10, todo archiving): not started.
- Gates G1, G2 and G3: not run. Wave 0 is the verification of the code already present.

**Recorded deviation — RED evidence (Q1).** RED evidence required by §5 was not captured for
T1–T14: the code was written in earlier session work before the failing runs were saved. Accepted
by the conductor as `autonomous: recommendation accepted` (Q1): GREEN is verified at the final
commit instead, no assertion is weakened, and the code reviewer is told of this deviation
explicitly. This does not relax any GREEN acceptance in §5 or any gate in §8.

**Dispatch tally (per failure cluster).** 0 rework dispatches so far.

| Failure-cluster key | Owning task | Dispatches | Last result |
|---|---|---|---|
| — | — | 0 | — |

**Worker sessions.**

| Task id | Specialist | Worker-session-id | Note |
|---|---|---|---|
| T1–T14b | as named in §6 | none recorded | Earlier session work; handles unavailable. |

**Findings.**

- **Rulings D26–D28 (architect, 2026-10-30)**, from a `typescript-dev` report (evidence:
  `artifacts/evidence/opencode-v2-state/ts-fix-*.log`). D26 approves the re-pin of the Antigravity
  digest. D27 removes the projection-level JSON unwrap and corrects the v3-expected fixture. D28
  confirms the not-measurable object shape. Scope and the §5 contract are unchanged; D27 and D28
  only align the code with §3.3 rule 4 and the T10 row.

**Unplanned files (pending verification).** Accepted as scope extensions of the named tasks;
each is verified with that task's GREEN acceptance and the G1 gates.

- `testing.ts` (plugin test-support module) — extension of T2/T10. **Q3 resolved:** keep it under
  `src/` if `npm --prefix dash run check:reachable` and the bundle test pass; otherwise move it to
  a test-only path under T2.
- `KyberDashGuidance.cs` — extension of T13b (the shared relay of KyberDash guidance lines, D22).
- `runA.kyberdash-api-session.v3-expected.json` (fixture) — extension of T1/T10.
- `dash/src/canon/sessions.test.ts` edits — extension of T7; still subject to the §3.3
  scope-change rule (a changed non-OpenCode pin returns to the architect).
- `dash/web/src/lib/kyberApi.ts` prompt type — deviation within T12b: the `prompt` field was added
  in `AgentSessionDashboard.tsx` rather than in `kyberApi.ts`; accepted if the T12 row and
  `display-layer.test.ts` pass.

## 2. Investigation findings

All facts below were read in this checkout at `8612eb4` or in the diagnosis evidence; numbers are
from runA unless stated.

- **F1 — Registered surfaces.** `createSetup` (`entry.ts:326-387`) registers `context`,
  `execute.before`, `execute.after` and returns `void`; V2 allows a `Cleanup` return
  (`@opencode/plugin@2.2.1` `promise/plugin.d.ts:58-62`) and exposes `ctx.location` and
  `ctx.event.subscribe(): AsyncIterable` (`promise/event.d.ts:4-6`).
- **F2 — Tool payload.** V2 `execute.after` is `{tool, sessionID, agent, messageID, id, input}` plus
  `{status: 'completed', result}` or `{status: 'error', error}` (`promise/tool.d.ts:29-52`). The
  current reader looks for `callID`/`callId`, `success`, `errors` and text output
  (`entry.ts:271`, `:304-317`), so it records no duration and always `success: true`.
- **F3 — Event shapes (live).** `session.created` carries `location.directory`, `model`, `version`;
  `session.renamed` carries `title`; `session.execution.started/succeeded` carry only `sessionID`
  plus event `id` and `created`; `session.step.started` carries `assistantMessageID`, `agent`,
  `model`; `session.step.ended` carries `assistantMessageID`, `finish`, `cost`,
  `tokens{input, output, reasoning, cache{read, write}}`; `session.usage.updated` is the running
  total. Execution `failed` adds `error`; `interrupted` adds `reason` (`user | shutdown |
  superseded | inactivity`) (`@opencode/schema@2.2.1` `session-event.js:207-215`).
- **F4 — Ordering (live).** `prompt` hook → `execution.started` → per step: `context` hook →
  `model.request` hook → `step.started` → `tool.called` → `execute.before/after` →
  `tool.success` → `step.ended` → `usage.updated`; then `execution.succeeded`. Tool hooks fire
  before their step ends. In the committed fixture `fixtures/v2/runA.events.jsonl` (lines 1-51 are
  `ses_e74c398c…`, lines 52-73 the foreign session): `execution.started` line 5; steps 10→22,
  26→38, 42→49; the `experimental.idle` hook at line 47 arrives inside step 3 (after
  `text.ended` line 46, before `step.streamed` 48 and `step.ended` 49); `execution.succeeded`
  line 51 (`created` 1793337367427, 8 ms after step 3 ended at 1793337367419).
- **F5 — Cross-session delivery.** The runA project probe received events for another session
  (`ses_e74c858a…`, probe-log lines 63-93) for which no hook fired in that instance. Event
  subscription is not instance-scoped; ownership must be filtered (D10).
- **F6 — Store idempotence.** `records.span_id` is the primary key (`canon/store.ts:326`) and
  records are written with `INSERT OR REPLACE` (`:2766`).
- **F7 — Projection.** `request_count` = parentless records (`sessions.ts:1792`);
  `requests[].turns` is 1 only when the root itself is a turn (`:1845-1852`); `duration_ms` sums
  every record (`:1801`); `total_input` = fresh + cache read + cache creation (`:1645-1653`,
  `cost.ts:100-102`); context composition charts only turns with measured input (`:1416-1441`);
  the label is the first record's span name (`:1779`); tools offered come from definition parts
  (`:1686-1701`) split by `extractToolDefinitionsFromText` (`:816-851`), which handles arrays and
  `{name}` objects but not an object keyed by name.
- **F8 — Timeline evidence.** Subagent/auxiliary markers match every raw key and string value
  (`analysis/timeline.ts:86-122`); the OpenCode tool catalogue contains a `subagent` tool, and the
  adapter keeps all span attributes in `record.raw` today.
- **F9 — Cost.** `createCostBlock` lets a harness-reported figure win (`canon/cost.ts:238-249`);
  OpenCode is outside the LiteLLM applicability set (`canon/published-pricing.ts:8-22`).
- **F10 — Cache counters.** `harnessExportsCacheCounter` reads the harness cache survey
  (`canon/measurability.ts:981-995`), where OpenCode is catalogued `not_measurable`, so
  `cache_hit_ratio` is suppressed; `READER_UNMEASURABLE` lists `opencode` (`:98-108`).
- **F11 — Skills.** Invocations come from a `Skill`/`skill` tool span or the `kyber.skills.invoked`
  key, with a per-turn max rule (`canon/skills.ts:44-46`, `:159-220`).
- **F12 — Capture writer.** OpenCode is a `managed` writer whose only key is
  `experimental.openTelemetry` (`capture/harnesses/opencode.ts:64-101`); enable installs the
  plugin and sets consent on (`capture/index.ts:851-876`); disable sets consent off first, then
  uninstalls (`:879-903`).
- **F13 — V1 import.** `providers/opencode.ts` uses the shared `sqlite-session-parser.ts` (also
  used by `providers/kilo-code.ts:5`) and `opencode-file-parser.ts`; it is registered in
  `providers/index.ts:107-137,201-253,329`, `synth/provider.ts:41,113`, `synth/synth.ts:273`,
  `refresh/registry.ts:59,176,362-369,623`.
- **F14 — Guidance strings.** "Restart OpenCode" is hard-coded in `install.ts:330`,
  `KyberDashCapturePluginStep.cs:120` and `SelfUpdater.cs:450`, and pinned by
  `install.test.ts:104`, `SquadCapturePluginStepTests.cs:95,110`, `UpdateCommandTests.cs:1087`.
- **F15 — Projection cadence.** Automatic projection has a 10-minute floor; `kyber build` is the
  manual path that always projects (`docs/dash/architecture.md` §"The shared canonical
  projection"). The live gate uses it.
- **F16 — Local deploy.** `scripts/release-local.sh --version <v> --out <dir>` builds the CLI, MCP,
  Squad archives and the `kyberdash` single executable for this machine; the loopback server and
  `KYBER_WEAVE_RELEASE_ORIGIN` drive a real `kyber-weave update` (`docs/distribution.md`
  §"Verifying a release locally").

**Discovery:** `codegraph 6, docs 10, direct reads 34` (plus 3 architect references and 10 reads
of the diagnosis evidence outside the repository). Direct reads of indexed code, with reasons:
`canon/sessions.ts` ×3 (CodeGraph returned `buildSessionRow`'s signature only; body cut by the
explore budget); `canon/adapters/opencode.ts` ×2 (explore output stopped before the usage and
counter-key code); `capture/harnesses/opencode.ts` (explore returned neighbouring capture files,
not this writer); `AgentSessionDashboard.tsx` (the request-list JSX was not in any explore
result); `canon/adapters/copilot.ts`, `canon/types.ts` ×2, `canon/cost.ts`, `otel/service.ts`
(confirming the token and cost invariants the design relies on); `canon/measurability.ts` ×3,
`canon/skills.ts`, `canon/naming.ts`, `analysis/timeline.ts`, `canon/store.ts`, `clean/clean.ts`,
`refresh/registry.ts` ×3, `synth/synth.ts`, `e2e/harness-collection-world.ts` (single details
named by grep that no explore call had covered). Non-code direct reads: `AGENTS.md`,
`dash/AGENTS.md`, `docs/plans/README.md`, one archived plan ×2 (structure; archive is outside the
docs index), `docs/distribution.md` (section not returned by `docs_explore`),
`scripts/release-local.sh`, `.github/workflows/ci.yml`, `dash/vitest.config.ts`. No discovery
call failed; no fallback was needed. No sub-agent was used.

## 3. Target design

### 3.1 Field map — owner field to screen

| Owner field | V2 source (2.2.1, live) | Plugin (contract 3) | Adapter → canonical record | Projection → API (`/api/kyber/session/:id`) | UI |
|---|---|---|---|---|---|
| Input tokens | `session.step.ended` `data.tokens.input` | step span `opencode.usage.input` | `tokens.freshInput` (D13) | `summary.total_input` = Σ(fresh + cache read + cache write); `turns[].fresh` | Overview tiles, per-turn spend chart |
| Output tokens | `data.tokens.output` | `opencode.usage.output` | `tokens.output` = output + reasoning (D13) | `summary.total_output`; `turns[].output` | Overview, spend chart |
| Reasoning tokens | `data.tokens.reasoning` | `opencode.usage.reasoning` | `tokens.reasoning` | `turns[].reasoning` | Spend chart |
| Cache read | `data.tokens.cache.read` | `opencode.usage.cache_read` | `tokens.cacheRead` | `summary.total_cache_read`, `cache_hit_ratio` (survey flipped, F10) | Tiles |
| Cache write | `data.tokens.cache.write` | `opencode.usage.cache_write` | `tokens.cacheCreation` | `summary.total_cache_creation`, `cache_creation_coverage` | Tiles |
| Cost | `data.cost` | `opencode.cost` | `cost = {harness, priced, value, USD}` or `no_rate` when absent (D4) | `summary.cost` (sum) | Cost tile |
| Duration | `created` of execution, step, tool events | real span start/end | `durationMs` per record | `summary.duration_ms` = Σ request roots (D3) | Duration tile, timeline |
| Requests and turns | execution boundaries; steps | request root, step children (D2) | `agent.invoke` root; `llm.invoke` steps | `summary.request_count`, `summary.turn_count`, `requests[].turns` (subtree count) | User-requests list, tiles |
| System prompt | `context` hook `system: SystemPart[]` (4 parts, once per step) | step `opencode.context.system` (content) | `system_prompt` part on the step | context composition (`context.measurable: true`) | Composition chart, inspector |
| User prompt | `prompt` hook `prompt.text` | request root `opencode.prompt.text` (content) | `conversation_history` part on the root | `requests[].prompt` preview (D16) | User-requests list |
| Tools offered | `context.tools` (object keyed by name) | step `opencode.context.tools` as JSON array (content, D14) | `tool_definitions` part | `summary.tools_offered`, `tools[]` schema table | Tool table |
| Tools invoked | `session.tool.called/success/failed`; `execute.before/after` | tool span: name, call id, status, error type, sizes (metadata) | `tool.invoke` record named after the tool | `summary.tool_calls`, `summary.tools_invoked`, `tools[].invocations` | Tool table, timeline |
| Skills listed | `<available_skills>` block in `context.system` | step `opencode.context.skills` (JSON array of ids, parsed by `skills-block.ts`; content) | raw listing key | `skills` view (listed) | Skills section |
| Skills invoked | `skill` tool `input.id`; `prompt.skills[].id`; `session.skill.activated` | step `kyber.skills.invoked` (metadata, D15) | raw key | `skills` view (invoked) | Skills section |
| Model | `step.started` `data.model`; `context.model` | step `opencode.model.id`, `opencode.model.provider` | bridged `gen_ai.request.model` (existing bridge) | `summary.models` | Header |
| Session title | `session.renamed` `data.title` | request root `kyber.session.label` (content, D12) | raw key, excluded from timeline evidence (D11) | `label` | Session list and header |

### 3.2 Wire contract version 3

One OTLP trace per OpenCode session, posted as OTLP JSON to `http://127.0.0.1:4318/v1/traces`
through the existing bounded outbox. Every span carries `opencode.contract_version = "3"`,
`opencode.session.id`, and `opencode.span.kind` (`request` | `step` | `tool`).

- **Ids.** `traceId` = first 32 hex of SHA-256(`opencode|trace|<sessionID>`). `spanId` = first 16
  hex of SHA-256(`opencode|<kind>|<sessionID>|<anchor>`), anchor = the `session.execution.started`
  event id (request), `assistantMessageID` (step), tool call `id` (tool). A tool's `parentSpanId`
  is the step id derived from its `messageID`; a step's is its execution's request id.
- **`opencode.request`** (parentless): start `execution.started.created`, end
  `execution.{succeeded|failed|interrupted}.created`; status ok, or error with
  `opencode.execution.outcome` and, for interrupted, `opencode.execution.reason`. Metadata:
  `opencode.agent`, `opencode.version` (from `session.created`), `opencode.prompt.count`;
  outcome `unknown` only for the D25 fallbacks.
  Content: `opencode.prompt.text` (every prompt delivered in the execution, in order, joined by a
  blank line, bounded at 64 KiB), `kyber.session.label`.
- **`opencode.step`** (child of its request): start `step.started.created`, end
  `step.ended.created`. Metadata: `opencode.message.id`, `opencode.agent`, `opencode.model.id`,
  `opencode.model.provider`, `opencode.request.kind` (`primary` default; from the `model.request`
  hook), `opencode.step.finish`, `opencode.usage.{input,output,reasoning,cache_read,cache_write}`
  (raw OpenCode integers), `opencode.cost` (only when OpenCode reported it), the existing
  context-limit key, `kyber.skills.invoked` (string array). Content: `opencode.context.system`
  (system parts joined by a blank line), `opencode.context.tools` (JSON array, D14),
  `opencode.context.skills` (JSON array of listed ids).
- **`opencode.tool`** (child of its step): start `session.tool.called.created`, end
  `session.tool.success|failed.created`; when a bus event is missing, the hook observation time
  stands in and the span records `opencode.tool.timing = "hook"`. Metadata: `opencode.tool.name`,
  `opencode.tool.call_id`, `opencode.message.id`, `opencode.tool.status`
  (`completed` | `error`), `opencode.tool.error_type`, `opencode.tool.input_bytes`,
  `opencode.tool.result_bytes`, `opencode.skill.id` for the `skill` tool.
- **Emission.** A tool span is emitted at `execute.after` (or `tool.success/failed`), a step at
  `step.ended`, a request only at its terminal execution event (`session.execution.succeeded`,
  `failed` or `interrupted`). `experimental.idle` is not a terminal signal and is not registered
  (D25). Per-session state is bounded by oldest-session eviction past a cap. An execution with no
  terminal event is emitted once with outcome `unknown`, ending at the last event observed for it,
  in two cases only: 30 minutes without events for that session, or the plugin's `Cleanup`
  (reload or shutdown). Every hook and event handler swallows its own errors; delivery never
  blocks or fails OpenCode.
- **Constants.** Keys, span names, kinds, the version and the id derivation live in one leaf module,
  `dash/src/capture/harnesses/opencode-plugin/contract.ts`, imported by the plugin and the adapter.
  The generic label key `kyber.session.label` lives beside `SKILLS_INVOKED_KEY` in
  `dash/src/canon/types.ts`.

### 3.3 Projection changes (shared, generic)

1. `requests[i].turns` = number of turn records whose root ancestor is request `i` (flat sessions
   unchanged: a root that is a turn counts 1, any other root 0 unless it has turn descendants).
2. `summary.duration_ms` = Σ durations of request roots when any record in the session has a
   parent in the session; otherwise the current Σ of all records (D3).
3. `label` = latest `kyber.session.label` on the session's records, else the current rule passed
   through `formatSessionTitle`'s machine-label fallback (D12).
4. `requests[i].prompt` = first 200 characters of request `i`'s `conversation_history` part text,
   else `null` (D16).
5. Timeline evidence matching skips `kyber.*` keys and their values (D11).
6. OpenCode cache survey: cache read and cache creation exported (`supported`), the
   `OPENCODE_NO_CAPTURE_REASON` text names the plugin and consent, and `READER_UNMEASURABLE` drops
   `opencode` (no reader remains).

Any existing non-OpenCode test whose pinned value changes under rules 1, 2 or 5 is a scope change:
the task stops and returns to the architect rather than editing that pin.

### 3.4 Consent, toggle and status

- Consent file and env override are unchanged in format. The plugin re-reads consent per event.
- Settings "OpenCode Enabled" = `kyber capture enable --harness opencode`: strict plugin install +
  consent on + legacy key migration (D8). Disabled = consent off + plugin removal + migration.
- `kyber capture status --harness opencode` reports: plugin installed and current or outdated
  (by receipt sha), consent on/off, receiver liveness, and no OTLP env snippet.

### 3.5 Native OTel, V1 import, settings toggle — summary

Native OpenCode OTel mapping removed (D6); V1-table and file import removed (D7); the toggle writes
no OpenCode config key and migrates the old one away (D8).

## 4. V1 removal inventory

Removed (deleted) or replaced, with the owning task:

| Path | Fate | Task |
|---|---|---|
| `dash/src/capture/harnesses/opencode-plugin/turn.ts`, `turn.test.ts` | Deleted (V1 `tool_result` log that was never sent) | T3, T2 |
| `…/opencode-plugin/plugin.ts` V1 lifecycle kinds (`session.start/turn.start/span.start/span.end/turn.end/session.end`), `registerOpencodePlugin` host maps, `outcomeLogs` | Removed; plugin rebuilt on V2 surfaces | T3 |
| `…/opencode-plugin/entry.ts`, `mapper.ts`, `transport/otlp-sender.ts` | Replaced (contract 3) | T3 |
| `…/opencode-plugin/generated/plugin-source.ts` | Regenerated | T3 |
| `…/opencode-plugin/fixtures/hook-catalog.json`, `span-payload-skeleton.json`, `context-payload.json` (2.1.4 pin), `plugin-module-1x.json` (1.x loader), `install-targets.json` (V1 targets) | Deleted; replaced by `fixtures/v2/` | T2 (delete), T1 (new) |
| `…/opencode-plugin/tool-lifecycle.test.ts` | Deleted; replaced by `v2-replay.test.ts` | T2 |
| `…/opencode-plugin/{entry,plugin,plugin.pipeline,mapper,diagnostics-bounds,sliced-string-backing,install,bundle}.test.ts`, `transport/*.test.ts` | Rewritten to V2 or kept where still valid | T2 |
| `dash/src/capture/opencode-plugin-wiring.test.ts`, `dash/src/architecture/opencode-leaves.test.ts` | Updated (contract leaf, no `turn.ts`) | T1, T2 |
| `dash/src/canon/adapters/opencode.ts` AI SDK, OpenInference and legacy `ai.*` mapping | Removed; contract-3 mapping only | T5 |
| `dash/src/canon/adapters/opencode-ai-sdk.test.ts`, `opencode-tool-lifecycle.test.ts` | Deleted | T4 |
| `dash/src/canon/adapters/opencode.test.ts`, `opencode-envelope.test.ts` | Rewritten for contract 3 | T4 |
| `dash/src/canon/fixtures/opencode-ai-sdk/*.json`, `dash/src/canon/fixtures/opencode-openinference/*.json` | Deleted | T4 |
| `dash/src/providers/opencode.ts`, `opencode-file-parser.ts`, `opencode.test.ts`, `opencode-file.test.ts` | Deleted | T9, T8 |
| `dash/src/synth/readers/opencode.ts`, `opencode.test.ts` | Deleted | T9, T8 |
| `dash/src/providers/index.ts`, `synth/provider.ts`, `synth/synth.ts`, `refresh/registry.ts` OpenCode import registration | Removed; `opencode` stays a scope id | T9 |
| `dash/src/providers/sqlite-session-parser.ts`, `session-message.ts` | **Kept for Kilo**; comment-only edits naming Kilo as the remaining user | T9 |
| `dash/src/capture/harnesses/opencode.ts` (`experimental.openTelemetry` writer, OTLP env snippet) | Replaced by a plugin-only writer + migration | T9 |
| `dash/src/capture/status.ts` OpenCode OTLP-endpoint manual step | Removed | T9 |
| `dash/src/server/fixtures/capture-api/{status-mixed,status-unknown,post-disable-last}.json`, `dash/src/capture/actions.test.ts`, `dash/src/capture/harnesses/opencode.test.ts`, `dash/web/src/lib/kyberApi.capture.test.ts`, `dash/src/cli/doctor.test.ts` | Updated to the plugin-only writer / no OpenCode provider | T8 |
| `dash/e2e/harness-collection-world.ts`, `harness-collection.spec.ts` seeded measured-opencode scenario and its "live gate procedure" comment | Replaced by the replay scenario; honest-empty seed relabelled | T10 |
| `docs/todo/opencode-1x-runtime-support.md` | Archived as superseded (V1 removed) | T15 |
| `docs/todo/opencode-plugin-per-message-tokens.md` | Archived as superseded (delivered by step events) | T15 |
| `docs/todo/opencode-plugin-token-usage-capture.md` | Usage and content items delivered; narrowed to the per-source "last emitted" signal only, or archived if that item is already tracked elsewhere | T15 |
| `docs/todo/kyberdash-skills-view-follow-ups.md` item (c) | OpenCode part resolved; Pi, Copilot CLI and Claude Desktop parts remain | T15 |
| `docs/dash/telemetry-inventory.md` (OpenCode rows, T0 addendum), `docs/dash/architecture.md`, `runbook.md`, `onboarding.md`, `docs/rules/kyberdash-harness-collection.md`, `docs/cli-command-surface.md`, `docs/install.md`, `docs/ci-pipelines/workflows-runbook.md` (`dash-e2e`), `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`, ADR 0031 | Updated; ADR 0046 added | T15 |

**Kilo guard.** `providers/kilo-code.ts`, its tests and `ingest/sqlite-readonly-parent.test.ts`
are not edited and must stay green; `sqlite-session-parser.ts` and `session-message.ts` change in
comments only.

## 5. Test-first contract

Scoped Vitest runs use `cd dash && npx vitest run <path filters>`: the `test` script already
passes `src tests scripts web/src` as filters, so extra filters after `npm run test --` would not
narrow the run. RED evidence is the failing run's output saved in the task handoff; GREEN is the
same test passing with no weakened assertion.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1/T1b | `dash/src/capture/harnesses/opencode-plugin/contract.test.ts`, `fixtures/v2/fixtures.sanitized.test.ts`, `dash/src/architecture/opencode-leaves.test.ts` | `cd dash && npx vitest run src/capture/harnesses/opencode-plugin/contract.test.ts src/capture/harnesses/opencode-plugin/fixtures src/architecture/opencode-leaves.test.ts` | Version is `"3"`; key strings match §3.2; trace id is 32 hex and span ids 16 hex, stable for equal input and distinct per kind and anchor; `contract.ts` imports nothing but `node:` built-ins; fixtures contain no secret or personal path pattern | `contract.test.ts` fails because `contract.ts` does not exist; the sanitizer test passes on the new fixtures (it is a guard, not a RED) | All pass; leaves test lists `contract.ts` |
| T2/T3 | `opencode-plugin/v2-replay.test.ts`, `entry.test.ts`, `plugin.test.ts`, `plugin.pipeline.test.ts`, `mapper.test.ts`, `transport/otlp-sender.test.ts`, `install.test.ts`, `bundle.test.ts` | `cd dash && npx vitest run src/capture/harnesses/opencode-plugin src/capture/opencode-plugin-wiring.test.ts` | Replaying `fixtures/v2/runA.events.jsonl` through `createSetup` with a fake V2 context and an injected fetch emits exactly the golden body `otlp-v3-runA.consent-on.json` (consent on) and `…consent-off.json` (consent off); no V1 lifecycle kind is emitted; events for the foreign session are not emitted; a second setup in the same instance stops the first generation; `setup` returns a cleanup; requests close only on a terminal execution event — an `experimental.idle` hook, before or after the last `step.ended`, never emits or closes a request, and no `experimental.idle` hook is registered; an execution with no terminal event is emitted exactly once with outcome `unknown` after 30 minutes without events (fake clock) or at cleanup, ending at its last observed event (D25); a throwing fetch never rejects a hook; tool status `error` maps to `opencode.tool.status = error`; install guidance line starts `harness opencode: ` and contains no bare "restart OpenCode so it loads"; the committed bundle equals a fresh bundle | Each test fails against today's plugin (no subscription, V1 fields, instants, wrong guidance) | Same tests pass; golden bodies byte-equal after canonical JSON ordering |
| T4/T5 | `dash/src/canon/adapters/opencode.test.ts`, `opencode-envelope.test.ts` | `cd dash && npx vitest run src/canon/adapters/opencode` | Golden contract-3 spans normalize to: root `agent.invoke` parentless, `durationMs` 13367; step 1 `llm.invoke` with freshInput 10935, cacheRead 1521, cacheCreation 0, output 1026, reasoning 957, reportedInput 12456, all four classes `measured`; cost `{harness, priced, 0, USD}`; a step without `opencode.cost` is `{unknown, no_rate}`; tool span `tool.invoke` named `read`/`skill` with real duration; `record.raw` holds no `opencode.context.*`, `opencode.prompt.text` key; contract `2` spans quarantine with the unknown-version reason; an OpenInference model span is no longer attributed to `opencode` | Fails today: no contract 3, OpenInference still claimed, raw keeps content | Same tests pass |
| T6/T7 | `dash/src/canon/sessions.nested-requests.test.ts` (new), `dash/src/canon/sessions.opencode-v2.test.ts` (new), `dash/src/analysis/timeline.test.ts`, `dash/src/canon/measurability.test.ts` | `cd dash && npx vitest run src/canon/sessions src/canon/measurability src/analysis/timeline` | Nested session of 2 requests (3 + 1 steps): `request_count` 2, `turn_count` 4, `requests[].turns` [3, 1], `duration_ms` = Σ root durations; a flat fixture session's `duration_ms` and `requests[].turns` are unchanged; `label` = `kyber.session.label`; `requests[].prompt` previews ≤ 200 chars, `null` without a prompt part; a step whose parts mention "subagent" and "title" and whose raw holds `kyber.session.label = "fix subagent title"` is neither subagent nor auxiliary; OpenCode `cache_hit_ratio` is present when input > 0; a harness `0` cost renders as priced $0.00, not `no_rate` | New tests fail on today's projection | Same tests pass; every pre-existing test in `canon/` and `analysis/` passes unchanged |
| T8/T9 | `dash/src/capture/harnesses/opencode.test.ts`, `dash/src/capture/actions.test.ts`, `dash/src/server/fixtures/capture-api/*.json`, `dash/web/src/lib/kyberApi.capture.test.ts`, `dash/src/cli/doctor.test.ts`, `dash/src/refresh/registry.test.ts`, `dash/src/clean/clean-scope.test.ts` | `cd dash && npx vitest run src/capture/harnesses/opencode.test.ts src/capture/actions.test.ts src/server src/refresh/registry.test.ts src/clean src/cli/doctor.test.ts src/providers src/ingest/sqlite-readonly-parent.test.ts web/src/lib/kyberApi.capture.test.ts` | Enable writes no `opencode.jsonc` key, installs the plugin, sets consent on, and restores a previously receipted `experimental.openTelemetry` key; a drifted key is kept with a warning; status has no OTLP env snippet; `resolveHarnessScope(['opencode'])` is known and the OpenCode refresh imports nothing; doctor no longer reports `session-model-v1` for OpenCode; Kilo provider and read-only parent tests unchanged | Updated tests fail against the managed key writer and the registered provider | Same tests pass; Kilo tests pass untouched |
| T10/T11 | `dash/src/capture/opencode-v2-roundtrip.test.ts` (new), `dash/e2e/harness-collection-world.ts`, `dash/e2e/harness-collection.spec.ts` | `cd dash && npx vitest run src/capture/opencode-v2-roundtrip.test.ts`, then from the root `npx --prefix dash playwright test dash/e2e/harness-collection.spec.ts` | With consent on (injected `KYBERDASH_OPENCODE_CONTEXT=1`), runA replay through plugin → OTLP JSON → receiver decode → ingest → `projectCanonicalStore` on a temp store yields: `request_count` 1, `turn_count` 3, `requests[0].turns` 3, `total_input` 40108, `total_cache_read` 27475, `total_cache_creation` 0, `total_output` 1529, Σ reasoning 1376, cost priced 0 (harness), `duration_ms` 13367, `tool_calls` 2, `tools_invoked` ⊇ {skill, read}, `tools_offered` length 12, system prompt bucket > 0, `requests[0].prompt` starts with the runA prompt, skills listed 37 incl. `kw-probe-skill`, skills invoked ⊇ {`kw-probe-skill`}; with consent off the usage, cost, duration, request/turn and tool-call figures are identical while system prompt, user prompt, tools offered and skills listed are `not_measurable` with a reason naming content capture, never zero; e2e replay scenario renders the consent-on values; the honest-empty seed is labelled rendering-only | Fails until T3, T5, T7, T9 land | Unit test and the e2e spec pass; the three pre-existing e2e scenarios unchanged except the replaced one |
| T12a/T12b | `dash/web/src/components/AgentSessionDashboard.test.tsx` | `cd dash && npx vitest run web/src/components/AgentSessionDashboard.test.tsx` | The user-requests list shows each request's prompt preview and `(N turns)`; without a prompt it shows `Request N`; it never shows a raw span id as the label | Fails on today's span-id rendering | Same test passes |
| T13a/T13b | `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs`, `tests/KyberWeave.Tests/UpdateCommandTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SquadCapturePluginStep\|FullyQualifiedName~UpdateCommand"` | Every stdout line of the verb that starts with `harness opencode: ` is relayed; no hard-coded "Restart OpenCode" line is printed by either step; Squad and update exit codes are unchanged | Fails against the hard-coded lines | Same tests pass |
| T14a/T14b | `dash/scripts/opencode-live-gate/compare.test.ts`, `dash/e2e/opencode-live-gate.spec.ts` | `cd dash && npx vitest run scripts/opencode-live-gate`; the spec runs only in the live gate | The comparator passes on the runA fixture pair and fails, naming the field, for each of: missing field, token class mismatch, cost mismatch, `duration_ms` 0, any `requests[].turns` of 1 while steps > requests, `tools_offered` empty, missing tool name, missing system prompt, missing user prompt, skills listed or invoked missing; the live spec skips unless `KW_OPENCODE_LIVE_SESSION_ID` is set | Fails: comparator absent | Same tests pass |
| T15 | `docs validate` / `docs drift` | `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `… docs drift .` | Docs describe the shipped contract; zero findings | No-test task (docs); verified by both checks | Both exit 0 |
| T1b, T11 | — | — | T1b is constants plus one id function, covered by T1's RED test. T11 has no own test: its acceptance is T10's tests turning GREEN | Explicit no-test tasks; T1 and T10 carry the RED | As stated |

## 6. Tasks

Required skills are named; the conductor maps them to workers. Every developer task ends with its
own `task-gate` run. No task runs OpenCode or calls a model (D23).

### T1 — V2 fixtures, contract RED, leaves guard
- **Skill:** `test-dev`. **Depends on:** none.
- **Files (new):** `dash/src/capture/harnesses/opencode-plugin/fixtures/v2/PROVENANCE.md`,
  `runA.events.jsonl` (hooks and bus events for `ses_e74c398c…` in order, plus the foreign-session
  events from probe-log lines 63-93; excluding `http.request`, `http.response`, permission hooks,
  reasoning/text deltas), `runA.context.json` (the 4 system parts and the 12-tool object for each
  of the 3 steps), `runC.prompt-skills.jsonl`, `runA.opencode-db.json` (session_v2 totals and
  per-step tokens: 12633/153/1376/27475/0, cost 0, steps {10935,69,957,1521,0},
  {1259,65,223,12401,0}, {439,19,196,13553,0}), `runA.kyberdash-api-session.v0.json` (today's API
  shape, from `runA/runA-api-session.json`), `otlp-v3-runA.consent-on.json` and
  `otlp-v3-runA.consent-off.json` (golden bodies authored from §3.2), `fixtures.sanitized.test.ts`;
  `opencode-plugin/contract.test.ts`. **Edit:** `dash/src/architecture/opencode-leaves.test.ts`
  (add `contract.ts` as a leaf).
- **Sanitizing:** replace `/Users/dave`, `/private/tmp/claude-501/…` and the scratch project path
  with `/HOME`-style placeholders; drop any header map; the sanitizer test fails on `Authorization`,
  `Bearer`, `sk-`, `api_key`, `x-api-key`, `/Users/`, `@gmail.com`.
- **Source:** `/private/tmp/claude-501/-Users-dave-git-personal-claude-kyber-weave/aabca51a-ec96-4463-857c-14830088f183/scratchpad/{runA,runB,runC}/`.
  If that scratchpad is gone at execution time, the task returns BLOCKED; payloads are never
  invented.
- **Acceptance:** §5 T1 row.

### T1b — Contract module
- **Skill:** `typescript-dev`. **Depends on:** T1.
- **Files:** new `dash/src/capture/harnesses/opencode-plugin/contract.ts`; `dash/src/canon/types.ts`
  (add `SESSION_LABEL_KEY = 'kyber.session.label'` beside `SKILLS_INVOKED_KEY`).
- **Acceptance:** §5 T1 row GREEN.

### T2 — Plugin RED
- **Skill:** `test-dev`. **Depends on:** T1b.
- **Files:** the plugin test files and fixture deletions listed in §4 for T2;
  `dash/src/capture/opencode-plugin-wiring.test.ts`.
- **Acceptance:** §5 T2/T3 row RED evidence.

### T3 — Plugin GREEN (V2 surfaces, contract 3)
- **Skill:** `typescript-dev`. **Depends on:** T2.
- **Files:** `opencode-plugin/entry.ts`, `plugin.ts`, `mapper.ts`, `transport/otlp-sender.ts`,
  `transport/outbox.ts` (only if the batch shape needs it), delete `turn.ts`, `install.ts`
  (guidance line D22, V2 target notes), `install-command.ts`, `consent.ts` and `skills-block.ts`
  (unchanged unless a test requires), `dash/scripts/bundle-opencode-plugin.mjs` (only if the
  entry changes shape), `generated/plugin-source.ts` (regenerated, committed).
- **Constraints:** register `ctx.event.subscribe()`, `session.hook('prompt')`,
  `session.hook('context')`, `session.hook('model.request')` (not `experimental.idle`, D25),
  `tool.hook('execute.before'/'execute.after')`; implement §3.2 and D10; return `Cleanup`; keep
  `OPENCODE_PLUGIN_ID` and `OPENCODE_PLUGIN_CONFIG_KEYS` exported and keep the function
  signatures `capture/harnesses/opencode.ts` imports from `install.ts`; the bundle stays
  dependency-free.
- **Acceptance:** §5 T2/T3 row GREEN.

### T4 — Adapter RED
- **Skill:** `test-dev`. **Depends on:** T1b.
- **Files:** §4 rows for T4.
- **Acceptance:** §5 T4/T5 row RED evidence.

### T5 — Adapter GREEN
- **Skill:** `typescript-dev`. **Depends on:** T4.
- **Files:** `dash/src/canon/adapters/opencode.ts`, `opencode-envelope.ts`,
  `dash/src/canon/context-window.ts` and `dash/src/canon/ingest.ts` only if an import or the
  unknown-version path needs it.
- **Constraints:** D9, D11 (raw keeps metadata only), D13 mapping through `exclusiveConvention`,
  D4 cost block; keep `isOpencodeEnvelopeSpan` exported (cited in `docs/dash/architecture.md`
  code-refs) or update those code-refs in T15.
- **Acceptance:** §5 T4/T5 row GREEN.

### T6 — Projection RED
- **Skill:** `test-dev`. **Depends on:** T1b.
- **Files:** new `dash/src/canon/sessions.nested-requests.test.ts`,
  `dash/src/canon/sessions.opencode-v2.test.ts`; `dash/src/analysis/timeline.test.ts`;
  `dash/src/canon/measurability.test.ts`. Records are hand-built `CanonicalRecord`s mirroring §3.2
  so this scope does not wait for T5.
- **Acceptance:** §5 T6/T7 row RED evidence.

### T7 — Projection GREEN
- **Skill:** `typescript-dev`. **Depends on:** T6.
- **Files:** `dash/src/canon/sessions.ts`, `dash/src/canon/measurability.ts`,
  `dash/src/analysis/timeline.ts`; `dash/src/canon/skills.ts` and `dash/src/canon/naming.ts` only
  if a T6 test requires.
- **Constraints:** §3.3, including its scope-change rule.
- **Acceptance:** §5 T6/T7 row GREEN; `cd dash && npx vitest run src/canon src/analysis` fully green.

### T8 — V1 import and capture-writer RED
- **Skill:** `test-dev`. **Depends on:** none.
- **Files:** §4 rows for T8; deletions of `providers/opencode.test.ts`,
  `providers/opencode-file.test.ts`, `synth/readers/opencode.test.ts`;
  `dash/src/refresh/registry.test.ts`; `dash/src/clean/clean-scope.test.ts`.
- **Acceptance:** §5 T8/T9 row RED evidence.

### T9 — V1 import and capture-writer GREEN
- **Skill:** `typescript-dev`. **Depends on:** T8.
- **Files:** §4 rows for T9; `dash/src/capture/index.ts` (migration); `dash/src/capture/detect.ts`
  and `dash/src/cli/doctor.ts` only if a T8 test requires.
- **Constraints:** D7, D8; Kilo guard (§4); `npm --prefix dash run check:reachable` green.
- **Acceptance:** §5 T8/T9 row GREEN.

### T10 — Replay round trip and e2e RED
- **Skill:** `test-dev`. **Depends on:** T1b.
- **Files:** new `dash/src/capture/opencode-v2-roundtrip.test.ts`;
  `dash/e2e/harness-collection-world.ts`; `dash/e2e/harness-collection.spec.ts`.
- **Constraints:** production modules only in the round trip (no direct `upsertSession` for the
  OpenCode replay); injected fetch, no network; temp `canon.db`, never `~/.kyberdash/canon.db`.
- **Acceptance:** §5 T10/T11 row RED evidence.

### T11 — Integration GREEN
- **Skill:** `typescript-dev`. **Depends on:** T3, T5, T7, T9, T10.
- **Files:** non-test `dash/src` seams the round trip exposes that no earlier task owns (for
  example `dash/src/otel/receiver.ts` decoding a string-array attribute). A gap inside an earlier
  task's file scope goes back to that task's skill instead.
- **Acceptance:** §5 T10/T11 row GREEN.

### T12a — UI RED
- **Skill:** `test-dev`. **Depends on:** none.
- **Files:** `dash/web/src/components/AgentSessionDashboard.test.tsx`.
- **Acceptance:** §5 T12 row RED evidence.

### T12b — UI GREEN
- **Skill:** `react-dev`. **Depends on:** T12a.
- **Files:** `dash/web/src/components/AgentSessionDashboard.tsx`; `dash/web/src/lib/kyberApi.ts`
  (add `prompt?: string | null` to the request item type).
- **Constraints:** display only (`docs/rules/kyberdash-display-layer.md`); the preview text comes
  from the API.
- **Acceptance:** §5 T12 row GREEN; `dash/src/architecture/display-layer.test.ts` green.

### T13a — Squad and update RED
- **Skill:** `test-dev`. **Depends on:** none.
- **Files:** `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs`,
  `tests/KyberWeave.Tests/UpdateCommandTests.cs`.
- **Acceptance:** §5 T13 row RED evidence.

### T13b — Squad and update GREEN
- **Skill:** `csharp-dev`. **Depends on:** T13a.
- **Files:** `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`,
  `src/KyberWeave.Cli/Update/SelfUpdater.cs`.
- **Constraints:** D22; `TreatWarningsAsErrors`; never changes Squad's or update's exit code.
- **Acceptance:** §5 T13 row GREEN; `dotnet build KyberWeave.sln -c Release` clean.

### T14a — Live-gate comparator and live UI spec RED
- **Skill:** `test-dev`. **Depends on:** T1.
- **Files:** new `dash/scripts/opencode-live-gate/compare.test.ts`,
  `dash/e2e/opencode-live-gate.spec.ts`.
- **Acceptance:** §5 T14 row RED evidence.

### T14b — Live-gate runner GREEN
- **Skill:** `typescript-dev`. **Depends on:** T14a.
- **Files:** new `dash/scripts/opencode-live-gate/compare.mjs`,
  `dash/scripts/opencode-live-gate/run.mjs`; `dash/package.json` (script
  `gate:opencode-live`).
- **Behaviour of `run.mjs`:** steps 5-13 of §8.3 — scratch project, two prompts with an explicit
  model, idle wait, manual projection, API and `opencode.db` reads (read-only, schema checked with
  `PRAGMA table_info` before any query), comparison, evidence files, cleanup. Exit non-zero on any
  failed assertion, timeout or busy session left behind.
- **Acceptance:** §5 T14 row GREEN; `npm --prefix dash run check:reachable` green.

### T15 — Documentation
- **Skill:** `docs-dev` (with `app-docs-standard` and `architecture-decision-record`).
  **Depends on:** T3, T5, T7, T9, T11, T13b, T14b.
- **Files:** H1-H10 targets; ADR index row for 0046; `docs/todo/README.md` rows and `git mv` of
  the superseded todos (§4).
- **Acceptance:** `docs validate .` and `docs drift .` both zero findings; no doc still claims the
  e2e is proof of capture or that native OpenCode OTel carries usage.

## 7. Dependency graph

```
T1 ──► T1b ──┬─► T2 ──► T3 ──┐
             ├─► T4 ──► T5 ──┤
             ├─► T6 ──► T7 ──┼─► T11 ──┐
             └─► T10 ────────┘         │
T8 ──► T9 ─────────────────────► T11   ├─► T15 ──► G1/G2/G3 ──► review ──► closeout
T1 ──► T14a ──► T14b ──────────────────┤
T12a ──► T12b ─────────────────────────┤
T13a ──► T13b ─────────────────────────┘
```

- First wave (no dependencies, up to the default cap of 4 workers): T1, T8, T12a, T13a.
- T2, T4, T6, T10 are disjoint test scopes and run in parallel after T1b; T3, T5, T7 are disjoint
  source scopes and run in parallel after their REDs.
- T11 waits for every source scope because the round trip crosses all of them.
- T12b and T13b are independent of the TypeScript engine work.
- Gates G1, G2 and G3 (§8) run after all code tasks and T15; G2 and G3 can run in parallel.

## 8. Verification gates

### 8.1 G1 — macOS local gates (every task's `task-gate`, then once on the whole change)

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run test:locks
npm --prefix dash run check:reachable
npx --prefix dash playwright test dash/e2e/harness-collection.spec.ts
dotnet run --project src/KyberWeave.Cli -- review gates . --all --out artifacts/gates.json
dotnet run --project src/KyberWeave.Cli -- docs validate .
dotnet run --project src/KyberWeave.Cli -- docs drift .
```

### 8.2 G2 — Linux (mandatory, owner requirement)

- **Local container (when a Docker-compatible runtime exists on this Mac):** an Ubuntu container
  with the .NET SDK from `global.json` and Node from `dash/.nvmrc`, repository mounted read-write
  into a fresh clone of the branch head, running the `ci.yml` commands: `dotnet build` and
  `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj` (build-test), `npm ci` in `dash`,
  `dash/web`, `dash/tray/ui`, then `npm run typecheck`, `npm test`, `npm run test:locks`,
  `npm run lint`, `npm run check:reachable`, tray UI `typecheck` and `test` (ts-gates), and
  `npx playwright install --with-deps` + `npx --prefix dash playwright test
  dash/e2e/harness-collection.spec.ts` (dash-e2e). Output saved under
  `artifacts/evidence/opencode-v2-linux/<UTC-stamp>/`.
- **CI:** the pull request's Ubuntu jobs (`build-test`, `ts-gates`, `dash-e2e`, and every other job
  `ci-summary` requires) green on the final head commit.
- A Linux-only failure goes back to the owning task. The gate passes only when both parts pass, or,
  with no local runtime, when CI passes and the evidence states that no runtime was available.

### 8.3 G3 — Live acceptance on this Mac (mandatory; run by an agent, never "procedure only")

Executed by an agent with the `test-dev` skill, after G1 is green. Every command and its output is
appended to `artifacts/evidence/opencode-v2-live-gate/<UTC-stamp>/commands.log`.

1. **Preflight.** Record `git rev-parse HEAD`, `opencode --version` (must be 2.2.x),
   `ps -axo pid,lstart,command | grep -E 'opencode|kyberdash'`, `kyberdash --version`,
   `curl -s http://127.0.0.1:4747/api/kyber/version`, and the sha256 of
   `~/.config/opencode/plugins/kyberdash-opencode.js`. Confirm no OpenCode session is busy:
   `sqlite3 -readonly ~/.local/share/opencode/opencode.db` — the busy-time column of `session_v2`
   is null for every row (column name confirmed with `PRAGMA table_info(session_v2)` first).
2. **Build and install through the documented local path (D18).**
   `scripts/release-local.sh --version 0.1.8-rc.12.local.<n> --out .local-release`;
   start `scripts/local-release-server.py` on loopback and record its port;
   `KYBER_WEAVE_RELEASE_ORIGIN=http://127.0.0.1:<port> kyber-weave update 0.1.8-rc.12.local.<n> --no-menubar`.
   Assert: `kyberdash --version` and `/api/kyber/version` both print the local version; the
   installed plugin's sha256 equals the sha256 of the bundle in `generated/plugin-source.ts`; the
   receipt names the same sha. Stop the loopback server.
3. **Enable capture the owner's way (D5).** `kyberdash kyber capture enable --harness opencode`.
   Assert `kyberdash kyber capture status --harness opencode` shows plugin current, consent on,
   receiver up, and that `opencode.jsonc` no longer holds `experimental.openTelemetry` (record a
   path-sanitized diff).
4. **Plugin load.** Wait up to 60 s for a `loading plugin … kyberdash-opencode.js` line in
   `~/.local/share/opencode/log/opencode.log` timestamped after step 2. If none appears, record
   "global plugin hot reload NOT observed"; then, only if step 1's busy check still shows no busy
   session, stop the shared `opencode serve --service` process (SIGTERM) so the next run starts a
   fresh service, and record that. Otherwise record "hot reload observed".
5. **Scratch project.** A new temp directory outside the repository with
   `.opencode/skills/kw-gate-skill/SKILL.md` (instructs: reply with `KW-GATE-SKILL-LOADED` and the
   number of lines in `notes.txt`) and a three-line `notes.txt`.
6. **Prompt 1 (tool + skill) through the shared background service.**
   `opencode run -m opencode-go/muse-spark-1.3-contributor --format json --title "kw-live-gate <stamp>" --auto "Load the skill named kw-gate-skill using the skill tool, then read notes.txt with the read tool, then follow the skill's instruction. Do not do anything else."`
   Capture the session id from the JSON output. If the model answers `provider.no-route`, rerun
   with `-m opencode/big-pickle` and record it. Never omit `-m`; never an Anthropic model.
7. **Prompt 2 (same session, attached skill).** Discover and record the continuation route
   (`opencode run --help`; else `opencode api --help`, the route runC used) and send to the same
   session, with the explicit model: "Follow the attached kw-gate-skill skill. Do not call any
   tools; reply with its token and the number 3." with `kw-gate-skill` attached.
8. **Idle wait.** Poll `session_v2` for that id until not busy (max 5 minutes). On timeout,
   interrupt the session through the same API route, record it, and fail the gate.
9. **Ingest and project.** Poll `~/.kyberdash/canon.db` read-only until the session's step
   records equal the number of OpenCode assistant messages (max 2 minutes), then run
   `kyberdash kyber build` (manual projection, F15).
10. **Read both sides.** `curl -s http://127.0.0.1:4747/api/kyber/session/<id>` and
    `curl -s 'http://127.0.0.1:4747/api/kyber/sessions?harness=opencode'`;
    `curl -s http://127.0.0.1:4747/api/kyber/capture` (OpenCode shown enabled);
    from `opencode.db` (read-only): the `session_v2` row, the count of assistant and user
    `session_message` rows, the per-message token sums, and the tool parts (name, status) in
    assistant content.
11. **Compare (`compare.mjs`) — every line must pass:**
    - `total_input` = `tokens_input + tokens_cache_read + tokens_cache_write`, and > 0;
    - `total_output` = `tokens_output + tokens_reasoning`, and > 0; Σ `turns[].reasoning` =
      `tokens_reasoning`;
    - `total_cache_read` = `tokens_cache_read`; `total_cache_creation` = `tokens_cache_write`
      (cache values as reported, zero allowed when OpenCode reports zero);
    - `cost` is a priced harness block and its value equals `session_v2.cost` within 1e-9;
    - `duration_ms` > 0 and ≤ the gate's measured wall-clock for both prompts plus 1 s;
    - `request_count` = 2 = user messages sent; `turn_count` = assistant messages;
      `requests[].turns` sums to `turn_count` and no request shows 1 turn unless OpenCode recorded
      exactly one assistant message for it;
    - `tools_offered` non-empty and contains `skill` and `read`;
    - `tool_calls` = OpenCode tool-part count; `tools_invoked` ⊇ {`skill`, `read`};
    - the system prompt is present (`context.measurable` true and the `system_prompt` bucket > 0);
    - `requests[0].prompt` and `requests[1].prompt` start with the two prompt texts;
    - skills listed include `kw-gate-skill`; skills invoked include `kw-gate-skill` from both the
      tool call (prompt 1) and the attachment (prompt 2);
    - `models` contains the model actually used; `label` = the `--title` given.
12. **UI.** `KW_OPENCODE_LIVE_SESSION_ID=<id> npx --prefix dash playwright test dash/e2e/opencode-live-gate.spec.ts`
    against `http://127.0.0.1:4747/session/<id>`: the tiles, user-requests list, tool table,
    composition chart and skills section show the values from step 11; save a full-page
    screenshot to the evidence directory.
13. **Cleanup and leftover check.** No `opencode run` process started by the gate remains; the gate
    session is not busy; the loopback release server is stopped; the scratch project is removed
    (its `SKILL.md` copied into the evidence first). The shared service and Desktop are left as
    found unless step 4 stopped the service.
14. **Evidence.** `commands.log`, both OpenCode JSON outputs, the API JSON responses,
    `opencode-session_v2.json`, `opencode-session_message-summary.json`, `comparison.json`
    (field, kyberDash value, OpenCode value, pass/fail), the plugin-load log excerpt, the
    screenshot, and `summary.md`. Secrets stripped (D19).

**Fail rule.** Any missing field, mismatch, timeout or leftover session fails G3. A failure goes
back to the owning task through the conductor; nobody reports the work done, and the plan stays
open. A pass is reported with the evidence path and the §8.4 table — never as "fixed" without it.

### 8.4 Live evidence summary (filled at closeout from G3)

To be completed by the G3 executor: the comparison table, the model used, the hot-reload outcome
and the evidence directory path.

### 8.5 Review and closeout

- Whole-change `code-review` after the draft pull request is pushed and G1-G3 pass.
- `docs-dev` closeout: Closeout mapping for D0-D25, AP1 and H1-H10, archive this plan, move its index
  row, `docs validate --merge-ready` and `docs drift` at zero.

## 9. Risks and out of scope

**Risks.**
- **R1 — V2 surface churn.** OpenCode 2.x moves fast. Mitigation: tolerant readers, fixtures from
  real 2.2.1 payloads, `opencode.version` stamped on every request root, and the live gate.
- **R2 — Duplicate or poorer re-delivery.** Mitigated by D10's three guards, each unit-pinned.
- **R3 — Shared projection changes (D3, D11).** Mitigated by flat-session regression pins and the
  §3.3 scope-change rule.
- **R4 — Global plugin hot reload unverified.** G3 step 4 observes it and restarts the shared
  service only when no session is busy.
- **R5 — Cost semantics.** A reported 0 (Go plan) shows $0.00; currency assumed USD (D4).
- **R6 — Content volume.** About 52 KB of system prompt and tool catalogue per step with consent
  on, the same as contract 2 today; the outbox and receiver caps are unchanged.
- **R7 — Local version string.** `kyber-weave update` may refuse `0.1.8-rc.12.local.<n>`
  (UNVERIFIED); G3 then blocks and reports.
- **R8 — Old OpenCode rows** stay visible until the owner runs the D17 command.
- **R9 — Linux runtime availability** (D21).
- **R10 — Model availability.** `space-bunny` currently answers `provider.no-route`; the fallback
  is `opencode/big-pickle`.
- **R11 — Diagnosis scratchpad is temporary.** T1 must run while it exists; otherwise T1 is
  BLOCKED rather than invented.

**Out of scope.**
- A V2 database importer or historical backfill (D7).
- Tool input and result content capture (D14).
- Linking OpenCode subagent child sessions to their parent session.
- Kilo capture changes (the shared parser is only protected).
- Quarantine volume management for native OpenCode spans (D6).
- A per-source "last emitted" status signal (stays in the narrowed todo, T15).

## 10. References

- Diagnosis report and payloads:
  `/private/tmp/claude-501/-Users-dave-git-personal-claude-kyber-weave/aabca51a-ec96-4463-857c-14830088f183/scratchpad/opencode-diagnosis.md`
  (runA, runB, runC, `npm-plugin-2.2.1`, `npm-schema-2.2.1` beside it).
- `docs/dash/architecture.md` §"The shared canonical projection" and the OpenCode envelope section.
- `docs/distribution.md` §"Verifying a release locally"; `docs/install.md` §Updating.
- `docs/rules/kyberdash-display-layer.md`; `docs/rules/kyberdash-harness-collection.md`.
- ADR 0031 (capture command owns harness telemetry keys).
- Archived plans: `2026-10-22-opencode-span-turn-hooks-kyberdash.md`,
  `2026-10-23-opencode-context-capture-kyberdash.md`,
  `2026-10-25-opencode-capture-deploy-kyberdash.md`,
  `2026-10-27-kyberdash-otel-harness-coverage.md`,
  `2026-10-28-kyberdash-harness-collection-standard.md`,
  `2026-10-28-opencode-telemetry-fill.md` (PR #457).
