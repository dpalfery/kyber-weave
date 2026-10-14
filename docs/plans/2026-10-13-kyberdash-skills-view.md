---
id: plans/2026-10-13-kyberdash-skills-view
title: "KyberDash skills view: the skills a turn or session had access to"
doc-type: plan
status: draft
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-13
development-mode: test-first
---

# KyberDash skills view: the skills a turn or session had access to

**Status: Draft 2026-10-13. Every decision is answered.** The user answered Q1–Q10 on 2026-10-13 through the conductor relay, and approved D1–D3 in the same relay. D4 is the default development mode. D5 is the fixtures path, which the architect chose because the user's Q7 answer asked for it.

The plan still waits on two things:

- **Documentation checks.** The conductor runs `docs validate` and `docs drift`, because the architect harness has no shell.
- **Approve and execute.** The user's approve-and-execute gate.

Execution cannot start until the owner capture gate G0 is met ([section 8](#8-tasks)).

**Development mode:** `test-first`. The user did not opt out, so the default is persisted (D4). Every implementation task has a Test-contract row in [section 7](#7-test-contract-test-first).

## 1. Problem and goal

The user asked: "one big feature kyber-weave dash is missing is a listing of the skills that the turn or session had access too, exactly as we do for agent tools we should have a view for skills. what questions do you have?"

KyberDash already shows a session's agent tools:

- the Tool & Schema Cost Ranking, which compares offered tools with invoked ones, gives each tool's resident cost, rolls tools up by MCP server, and lists tools never invoked;
- the Tools Offered and Tool Calls tiles;
- the tool drawer;
- the `Tools` block in the turn inspector.

Skills have nothing comparable.

**Goal.** Add a skills view in the session dashboard, with a turn-level listing, for six harnesses: Claude Code (OTLP), Copilot Chat, Antigravity, Pi, Copilot CLI and OpenCode. Other file-ingested harnesses get coverage of invoked skills only. Each skill shows:

- whether it was listed as available;
- whether it was invoked, and how often;
- the turns it was listed on;
- an estimated token count, labelled derived.

Where a harness exports an aggregate skill-token total, the view shows it, with no money figure. Where a harness exports no skill data, the view says so and gives the reason. The engine derives every fact, and the web dashboard only renders what `/api/kyber/*` serves.

## 2. Investigation findings

Facts were read from the tree at `plan/dash-skills-view` HEAD `1f1769f`. Discovery used the Kyber-Weave docs MCP (the provenance root is this checkout) and CodeGraph, plus targeted Grep and Read. Finding 13 is self-gathered.

1. **How tools are captured and stored.** Adapters write tool schemas as `tool_definitions` content parts, for example `parseClaudeApiRequestBody` (`dash/src/canon/adapters/claude-code.ts:60–124`) and `canonicalParts` (`dash/src/canon/adapters/copilot.ts:401–455`).
   - **Tool calls.** Calls become child `tool.invoke` records. For file-synth harnesses, `synthesizeToolCall` (`dash/src/synth/synth.ts:641–784`) keeps bounded `arguments` on `raw`. For Copilot OTLP, a span with `gen_ai.tool.name` maps to `tool.invoke` (`copilot.ts:113`).
   - **Session projection.** `buildSessionRow` (`dash/src/canon/sessions.ts:487–918`) builds the session from those records. `toolDefinitionsOf` (188), `extractToolDefinitionsFromText` (233) and `rankSchemas` (`dash/src/analysis/schema.ts:184`) produce the tool ranking. `invocationsOf` (275) collects the invocations.
   - **Payload.** The payload (`AsadSessionPayload`, `sessions.ts:330–377`) carries `tools[]`, `servers[]`, `schema` and `summary.tools_offered/tools_invoked/tool_calls`.
   - **Storage.** The payload is a JSON blob in the `session` table (`dash/src/canon/store.ts:252`), rebuilt from `records` on every projection. A new payload key therefore needs no schema bump; `SCHEMA_VERSION` stays 19 (`store.ts:164`).
2. **How tools are served and displayed.**
   - **Session route.** `GET /api/kyber/session/:id` serves the payload through `KyberBridge`. The legacy `/api/kyber/schema?id=` route returns `payload.schema` (`dash/src/server/routes.ts:1523–1544`).
   - **Turn content.** `/api/kyber/session/:id/turn/:index/content` assembles five blocks: `system_prompt`, `tool_definitions` ("Tools"), `instruction_context`, `conversation_history` and `tool_result_content` (`dash/src/server/bridge.ts:2000–2006`).
   - **Web components.** `AgentSessionDashboard.tsx` renders the tiles (1065–1100), section "3 · Tool & Schema Cost Ranking" (1119–1189) through `SchemaCostRanking.tsx`, and the tool drawer `openDrawerForTool` (579–616). Section 4 is the execution timeline.
   - **No per-turn list.** No tool list exists at the turn level.
3. **Skill data in the canonical pipeline today.**
   - **Copilot Chat and Antigravity OTLP.** `gen_ai.skills` text and the reported `gen_ai.usage.skill_tokens` map to an `instruction_context` part (`copilot.ts:167, 187, 428–437`). Rules (`gen_ai.rules`) go to the same bucket (417–426). The Antigravity fixture also carries `github.copilot.tool.parameters.skill_name: "Active Skills: a, b, c, d"` (`dash/src/canon/adapters/__fixtures__/antigravity-span.json:29`). That key is unread and survives only on `raw`.
   - **Pi collector.** `pi.skills` is structured JSON of the form `[{"name","path"}]`, mapped to `instruction_context` (`dash/src/canon/adapters/pi.ts:46`; fixture `dash/src/canon/fixtures/pi-collector/collector-request.json:8`).
   - **Claude Code OTLP** (needs the owner opt-in `OTEL_LOG_RAW_API_BODIES=1`). The request body is parsed into `tool_definitions` (this includes the `Skill` tool and its description) and `conversation_history` (`claude-code.ts:49–59`). Any skill listing sits inside that text, and nothing extracts it.
   - **Claude Code and Claude Desktop file-synth.** No system prompt and no tool definitions are captured ([telemetry inventory](../dash/telemetry-inventory.md#claude-desktop-and-cli-file-synth-parts-issue-216)). Invocations are present: a `Skill` call becomes a `tool.invoke` record with `raw.arguments.skill`.
4. **Copilot CLI (newly in scope).** Two sources exist.
   - **Context rows.** `~/.copilot/data.db` rows are read by `loadCopilotCliCalls` (`dash/src/synth/readers/copilot.ts:1–75`). They carry only `context_system_tokens`, `context_conversation_tokens`, `context_tool_definitions_tokens`, `context_mcp_tools_tokens`, `context_buffer_tokens` and `context_tier`. There is no skill column and no skill text.
   - **Session-state events.** `~/.copilot/session-state/<sid>/events.jsonl` is parsed by `dash/src/providers/copilot.ts`. It extracts invoked skills from `skill` tool requests (`arguments.skill`, lines 1051–1056) into `ParsedProviderCall.skills`.
   - **Result.** Today, Copilot CLI has a source for **invoked** skills only (reached through Q8). No source in the repository carries its **listed** skills. A Copilot CLI OTLP export may carry `gen_ai.skills` if one reaches `copilotAdapter`, but no fixture or doc confirms that. Its owner capture (G0) settles which listing source exists. If none does, the listing renders not measurable, with the reason (Q6).
5. **OpenCode (newly in scope).** Two sources exist.
   - **OTLP adapter.** `dash/src/canon/adapters/opencode.ts` maps the legacy AI SDK keys `ai.prompt.messages`, `ai.prompt.tools` and `ai.toolCall.result` (55–57, 140–162), and `gen_ai.*` parts, into content buckets. A listing carried in a `skill` tool description would arrive inside `tool_definitions` text, as it does for Claude Code. Nothing extracts it.
   - **File and SQLite path.** `dash/src/providers/session-message.ts:137–141` extracts invoked skill names from `skill` tool parts (`state.input.name`) into `ParsedProviderCall.skills`, and `dash/src/providers/opencode.ts:20` maps `skill` to `Skill`.
   - **Collection status.** OpenCode OTLP collection is not owner-verified ([telemetry inventory](../dash/telemetry-inventory.md#verified-harness-outcomes)): the endpoint must be set in the environment.
   - **Result.** Invoked skills are reachable through Q8, and the listing through OTLP once a capture confirms its shape.
6. **Invoked-skill names are parsed, then dropped.** Many provider parsers already fill `ParsedProviderCall.skills` (`dash/src/providers/types.ts:61–63`):
   - Claude: the `Skill` tool (`dash/src/ingest/parser.ts:1117`);
   - Codex: `SKILL.md` shell reads (`dash/src/providers/codex.ts:80–95`);
   - Pi: `SKILL.md` and `skill://` reads (`dash/src/providers/pi.ts:39–62`);
   - Copilot (finding 4) and OpenCode/Kilo (finding 5);
   - Cline, CodeWhale, OpenClaude and DSH.

   `synth.ts` never reads `skills`. The legacy `SessionSummary.skillBreakdown` (`parser.ts:1709`) has no `/api/kyber/*` consumer.
7. **The existing skill analysis is mis-wired.** `skillUtilisation` (`dash/src/analysis/signals.ts:893–974`) and `inactive-skill-reference` (`dash/src/analysis/findings.ts:1539–1640`) run under ADR 0013 D16: low confidence, ranked last. They have three faults:
   - **Wrong pattern.** "Referenced" skills come from the regexes `<skill name="…">` and `skill: name`, applied to `system_prompt` only (`signals.ts:901–902, 1155`). No adapter writes that shape.
   - **Wrong name.** "Executed" skills come from tool record names (`signals.ts:1156`), so a `Skill` call credits a skill called `Skill`.
   - **Hard-coded unmeasurable.** The harness rollup always reports `skillUtilisation` as unmeasurable (`dash/src/analysis/scorecard.ts:79, 161`).
8. **Retention.** `purgeExpiredContent` empties `content_json` and `parts_json` after 14 days and never touches `raw` (`dash/src/canon/retention.ts`, [ADR 0018](../adr/0018-kyberdash-content-retention-purge.md)).
   - **Survives.** Skill names on `raw` survive the purge: `gen_ai.skills`, `skill_name`, `pi.skills`, `raw.arguments`, and the Q8 attribute.
   - **Lost.** Listings derived from parts (Claude Code and OpenCode OTLP) are lost on the next projection after the purge. That matches the tools view (Q10).
9. **The display-layer rule** (root `AGENTS.md`, [rule](../rules/kyberdash-display-layer.md), [ADR 0033](../adr/0033-kyberdash-surfaces-are-display-layers.md), and the guard `dash/src/architecture/display-layer.test.ts`). Extraction, the join and token attribution live in `dash/src` and are served over `/api/kyber/*`. `dash/web` renders only.
10. **Honesty and wording rules.**
    - [Honest unobservability](../rules/honest-unobservability.md) R1 and R2 forbid zero coercion and empty-list coercion.
    - [ADR 0013](../adr/0013-telemetry-grounded-finding-contracts-and-waste-ranking.md) D8, D15 and D16, with [relocation over deletion](../rules/relocation-over-deletion.md), require evidence-of-use labels. A skill is never called "waste", never recommended for deletion, and skill signals rank last.
    - **Consequence for copy.** The tools copy "a never-invoked tool is pure waste" (`SchemaCostRanking.tsx:337`) is not reused.
11. **Governing documentation.**
    - [dash/architecture.md](../dash/architecture.md): the Surface Layer, the Web Dashboard and the Backend REST API Contract;
    - [dash/telemetry-inventory.md](../dash/telemetry-inventory.md);
    - [dash/runbook.md](../dash/runbook.md);
    - [ADR 0011](../adr/0011-asad-only-context-view-and-payload-contract.md): the payload comes from `canon.db`, and harnesses report reasons, not zeros.
12. **Ingestion and schema impact.**
    - **No new ingestion.** Copilot Chat, Antigravity and Pi need none. Claude Code OTLP and OpenCode OTLP need new extraction only.
    - **Ingestion change.** Invoked skills from file-ingested harnesses need `synth.ts` to carry `ParsedProviderCall.skills`, plus parser-contract bumps (Q8).
    - **No `canon.db` schema bump.** `DETECTOR_VERSION` (`store.ts:184`) is bumped by Q9.
13. **Unverified wire formats (self-gathered).** No real capture in the repository pins any of these: the listing text for Claude Code (an `<available_skills>` block in the `Skill` tool description, or a system-reminder message, depending on version), the shape of Copilot `gen_ai.skills`, the Antigravity `skill_name` prefix, the OpenCode `skill` tool description, or any Copilot CLI listing. The repository fixtures hold synthetic placeholders. The user will supply sanitized captures (Q7, D5, gate G0).

## 3. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| Q1 | Scope: per session, per turn, or both? | **(c) Both.** A session-level skills section, plus per-turn listing: each skill row carries the turns it was listed on, each payload turn row carries `skills_listed`, and the turn inspector content gains a skills listing. | Alternatives: session only (mirrors tools exactly); turn only. Skill lists can change mid-session (Pi selects per request; Copilot sends context on the first request). | ANSWERED — user decision 2026-10-13 (conductor relay) |
| Q2 | Which harnesses are in scope? | **Six harnesses with listing and invocation extraction:** Claude Code OTLP, Copilot Chat, Antigravity, Pi, **Copilot CLI**, **OpenCode**. **Plus invoked-only coverage** for the other file-ingested harnesses whose parsers already fill `ParsedProviderCall.skills` (Claude file-synth, Codex, Pi files, Kilo, Cline, …) through Q8. | The user's words: "those 4 plus copilot cli, open code". No conflict was found with keeping the invoked-only option (b): it is delivered by Q8 at no extra task. For Copilot CLI, the repository has no listing source today (finding 4). For OpenCode, the listing depends on OTLP collection (finding 5). Where G0 captures show no listing, the listing side renders not measurable (Q6), not empty. | ANSWERED — user decision 2026-10-13 (conductor relay) |
| Q3 | What does "had access to" mean? | **(c) Listed and invoked, joined per skill**, like offered vs invoked tools. A side the harness does not expose is `not_measurable` with a reason, never `false` or `0`. | Alternatives: listed only; invoked only. | ANSWERED — user decision 2026-10-13 (conductor relay) |
| Q4 | Tokens or cost per skill? | **(b) Tokens, no money.** A per-skill token estimate from the skill's listing (description) text, using the o200k counter and labelled *derived*. The harness-reported aggregate `skill_tokens` is shown where exported (Copilot Chat, Antigravity), never divided across skills. **No money figure** and no unused-cost range. | Rejected: names only; a money "unused cost" range like the tools table, which conflicts with ADR 0013 D15/D16 (D3). | ANSWERED — user decision 2026-10-13 (conductor relay) |
| Q5 | Where does it live in the UI? | **(a) A new section "4 · Skills"** in the session dashboard (`AgentSessionDashboard.tsx`) between Tool & Schema and the execution timeline, which becomes "5 ·". Tiles "Skills Listed" and "Skills Invoked". A skill drawer drill-in that mirrors the tool drawer. | Rejected: a sub-tab in section 3; a cross-session top-nav tab; the inspector drawer only. | ANSWERED — user decision 2026-10-13 (conductor relay) |
| Q6 | Harnesses with no measurable skill data? | **(a) An honest "Not measurable" card with the reason** for each harness and side (for example, "Claude Code session files do not carry the skill listing; enable `OTEL_LOG_RAW_API_BODIES=1`"). | Required by the [honest-unobservability rule](../rules/honest-unobservability.md) R1 and ADR 0011. | ANSWERED — user decision 2026-10-13 (conductor relay) |
| Q7 | Real captures? | **(a) The user supplies sanitized real captures for each harness**, in the repository folder named by D5. The first implementation task (T1) depends on them existing (gate G0). | Synthetic-only fixtures were rejected because a parser that misses the real format ships a view that is always empty. | ANSWERED — user decision 2026-10-13 (conductor relay) |
| Q8 | Carry the skill names the parsers already extract into canonical records? | **(a) Yes.** `synth.ts` copies `ParsedProviderCall.skills` onto the turn record's `raw` as `kyber.skills.invoked` (a string array). The affected parser-contract versions are bumped so that units inside the 14-day window re-synthesize. | Rejected: reading `tool.invoke` arguments only, which misses Codex and Pi `SKILL.md` reads. `raw` survives the content purge. | ANSWERED — user decision 2026-10-13 (conductor relay) |
| Q9 | Rewire the existing skill signal and detector? | **(a) Rewire** `skillUtilisation` and `inactive-skill-reference` to the new listed and invoked sets, and bump `DETECTOR_VERSION`. Keep D16 low confidence and ranked last. | Rejected: leaving them as they are, which lets the scorecard and the view disagree; deferring to a todo. | ANSWERED — user decision 2026-10-13 (conductor relay) |
| Q10 | Should skill names survive the 14-day content purge? | **(a) Same as tools.** Names are derived from parts and `raw`. Part-only listings (Claude Code and OpenCode OTLP) are lost after the purge. No change to ADR 0018. | Rejected: writing names to `raw` at ingest, which would amend ADR 0018. | ANSWERED — user decision 2026-10-13 (conductor relay) |
| D1 | Where is the logic, and how is it served? | All extraction, joining and token attribution live in a new engine module, `dash/src/canon/skills.ts`, called from `buildSessionRow`. Results ride in the existing session payload (`skills`, `summary.skills_listed/skills_invoked`, `turns[].skills_listed`, `measurability.skills`). No new route is added. The web dashboard renders only. | Required by ADR 0033 and the display-layer rule. It mirrors how `tools` and `schema` ship. | APPROVED — user 2026-10-13 (conductor relay) |
| D2 | Should skills keep sharing the `instruction_context` bucket? | `CANONICAL_CONTENT_KEYS` is unchanged. Skill identity is read from `raw` attributes and from part text by the skills module. Composition charts are not re-bucketed. | Splitting the bucket would change ADR 0011 payload buckets and every chart. | APPROVED — user 2026-10-13 (conductor relay) |
| D3 | How are never-invoked skills worded? | Evidence-of-use labels only: "listed, no invocation observed" and "invoked N times". No "waste", "unused cost" or deletion language. Recommendations prefer progressive disclosure. | ADR 0013 D8/D15/D16 and [relocation over deletion](../rules/relocation-over-deletion.md). The `findings.ts:249` guard rejects deletion phrasing. | APPROVED — user 2026-10-13 (conductor relay) |
| D4 | Development mode | `test-first` | The user did not opt out. | ANSWERED — default per architect contract |
| D5 | Where do the user's captures go, and what is each harness's capture? | The root is **`dash/src/canon/fixtures/skills/`**, with one subdirectory per harness. The user drops sanitized originals as `capture.*` in each subdirectory, and the T1 worker derives trimmed test fixtures beside them. The contents per harness are in [section 8, gate G0](#8-tasks). | It sits beside the existing `dash/src/canon/fixtures/pi-collector/`, under the dash test tree, so `npm --prefix dash run test` picks it up. One directory per harness keeps the captures for each harness separate and reviewable. | ANSWERED — chosen by the architect at the user's request in Q7, 2026-10-13 |

Open decisions: **none**.

## 4. Design

- **Engine module `dash/src/canon/skills.ts`** (pure, no I/O).
  - `extractListedSkills(record)` returns `{name, description?, sourceKey}[]`, or `NotMeasurable` with a reason. There is one extractor per harness, each pinned to the G0 captures:
    - **Pi:** `pi.skills` JSON.
    - **Copilot Chat and Antigravity:** `gen_ai.skills` text and `github.copilot.tool.parameters.skill_name`.
    - **Claude Code:** the `Skill` tool definition text, and/or the skills-listing message text.
    - **OpenCode:** the `skill` tool definition text in `tool_definitions`.
    - **Copilot CLI:** whatever G0 shows. If G0 shows no listing, the extractor returns `NotMeasurable`, naming the absent source.

    An unrecognised shape returns `NotMeasurable`, never `[]`.
  - `extractInvokedSkills(records)` reads two sources:
    - `raw['kyber.skills.invoked']` on turn records (Q8);
    - `tool.invoke` records named `Skill` or `skill`, using `raw.arguments.skill` or `raw.arguments.name`. These are de-duplicated per call id against the Q8 attribute, so nothing is counted twice.
  - `buildSkillsView(records, turnRecords, measurability, countTokens)` returns:
    - `{measurable: true, rows: [...], reportedSkillTokens?}`, where each row is `{name, listed, invocations, turnsListed, derivedTokens?}`; or
    - `{availability: 'not_measurable', reason}`.
- **Projection (`sessions.ts`).** `buildSessionRow` adds:
  - `payload.skills` (section 5);
  - `summary.skills_listed` and `summary.skills_invoked`, present only when that side is measured;
  - `turns[i].skills_listed`, present only when that turn's listing is measured;
  - a merged `measurability.skills` reason.
- **Turn content (`bridge.ts`).** The turn-content response gains a `skills` entry: that turn's listed names, or a reason. It is a list, not a content block, so existing block bytes are unchanged.
- **Synth (`synth.ts`, Q8).** The turn record built from a `ParsedProviderCall` carrying `skills` gains `raw['kyber.skills.invoked']`. The affected harness-source `parserContractVersion` values are bumped.
- **Analysis (Q9).** `signals.ts` and `findings.ts` take their listed and invoked sets from `skills.ts`. `DETECTOR_VERSION` is bumped. Confidence stays low and the findings stay ranked last.
- **Web.**
  - `dash/web/src/lib/kyberApi.ts`: the payload types.
  - `SkillsPanel.tsx` follows the layout of `SchemaCostRanking`:
    - a headline: "X of Y listed skills had no observed invocation";
    - rows with the name, listed and invoked badges, derived tokens (or "not measurable"), and the turns listed;
    - the reported aggregate, where one exists;
    - a Not measurable card with the reason.
  - In `AgentSessionDashboard.tsx`: section "4 · Skills" (the timeline becomes "5 ·"), the two tiles, and `openDrawerForSkill`. The drawer shows the listing text through the existing turn-content route.

## 5. API contract (additive, inside `GET /api/kyber/session/:id`)

```jsonc
{
  "summary": { "skills_listed": ["docs"], "skills_invoked": ["docs"] },  // each key absent when that side is not measured
  "skills": {
    "measurable": true,
    "reported_skill_tokens": 1040,            // absent unless the harness reported it
    "rows": [
      { "name": "docs", "listed": true, "invocations": 2,
        "turns_listed": [0, 1, 2], "derived_tokens": 38 }
      // "listed" / "invocations" may instead be {"availability":"not_measurable","reason":"…"}
    ]
  },
  // or: "skills": { "availability": "not_measurable", "reason": "…" }
  "turns": [{ "index": 0, "skills_listed": ["docs"] }]
}
```

- **Rows stored before this change** have no `skills` key. The UI reads a missing key as "not yet projected", never as "no skills".
- **Turn indices** in `turns_listed` follow the 0-based transport convention (issue #184).

## 6. Constraints that bind every task

- **Display layer.** The display-layer rule applies: no domain computation in `dash/web`, and the guard stays green.
- **No zero coercion.** No zero or empty-list coercion (honest unobservability).
- **Wording.** ADR 0013 D15/D16 wording, per D3.
- **Existing contract.** Existing payload keys and test ids are unchanged. The only exception is the timeline heading number ("4 ·" becomes "5 ·").
- **Captures.** Captures under `dash/src/canon/fixtures/skills/` stay sanitized. The T1 worker must not add real prompt text, user names, paths that identify the user, or tokens beyond what the user committed.

## 7. Test contract (test-first)

Every dash row runs its file first, then `npm --prefix dash run typecheck lint test check:reachable`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/canon/skills.test.ts` (new), over trimmed fixtures in `dash/src/canon/fixtures/skills/<harness>/` derived from the G0 captures | `cd dash && npx vitest run src/canon/skills.test.ts` | **Listed:** for each of the six harnesses, listed names are extracted exactly from its capture; a harness whose capture has no listing gives `not_measurable` with a reason naming the absent source; rule text in `instruction_context` is never reported as a skill. **Invoked:** names come from `Skill`/`skill` arguments and `kyber.skills.invoked`, never from tool names, with no double count. **Other cases:** an unrecognised shape gives `not_measurable`, never `[]`; derived tokens are labelled derived; the reported `skill_tokens` is carried and never split. | Module absent, or assertions fail | All pass |
| T2 | `dash/src/canon/sessions.skills.test.ts` (new), `dash/src/server/kyber-content-route.test.ts` (additions) | `cd dash && npx vitest run src/canon/sessions.skills.test.ts src/canon/sessions.test.ts src/server/kyber-content-route.test.ts` | **Payload:** `skills`, `summary.skills_*` and `turns[].skills_listed` follow section 5. **Unmeasured:** a harness with no source gives `not_measurable` and no `summary.skills_*` keys. **Turn content:** it lists that turn's skills or a reason, and existing block bytes are unchanged. **Regression:** existing suites pass unmodified. | New keys or entry absent | All pass; existing suites unchanged |
| T3 | `dash/src/synth/synth.skills.test.ts` (new) | `cd dash && npx vitest run src/synth/synth.skills.test.ts src/synth/synth.test.ts` | A `ParsedProviderCall` with `skills` gives a turn record with `raw['kyber.skills.invoked']` equal to those names. Without `skills`, the key is absent. Every bumped `parserContractVersion` is greater than its previous value. | Attribute absent | All pass |
| T4 | `dash/src/analysis/signals.test.ts`, `dash/src/analysis/findings.test.ts` (additions) | `cd dash && npx vitest run src/analysis/signals.test.ts src/analysis/findings.test.ts` | A `Skill` call loading `docs` credits `docs`, not `Skill`. A listed-not-invoked skill drives `inactive-skill-reference` at low confidence, ranked last, with no deletion phrasing. `DETECTOR_VERSION` is incremented. | The new assertions fail on the current tool-name crediting and regex input | All pass, the existing D16 assertions included |
| T5 | `dash/web/src/components/SkillsPanel.test.tsx` (new), `dash/web/src/components/AgentSessionDashboard.test.tsx` (additions); payload fixture `dash/web/src/components/fixtures/skills-payload.json` (new) | `cd dash && npx vitest run web/src/components/SkillsPanel.test.tsx web/src/components/AgentSessionDashboard.test.tsx src/architecture/display-layer.test.ts` | Rows render from the fixture, with listed and invoked badges and turns listed. Tokens are labelled derived. The reported aggregate shows where present, with no currency anywhere. The Not measurable card shows its reason. A missing `skills` key renders "not yet projected". No "waste" wording. The tiles render, and a row click opens the skill drawer. The section headings read "4 · Skills" and "5 · Execution Timeline". The display-layer guard passes. | Component absent | All pass |
| T6 | No-test task: documentation | `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `docs drift .` | Architecture, telemetry inventory and runbook describe the final behavior | N/A | Zero findings |
| T7 | No-test task: whole-change review | `code-review` skill, then `review gates` | Council verdict | N/A | Approve, or findings fixed and re-checked |
| T8 | No-test task: closeout | Docs commands with `--merge-ready` after archiving | Plan archived, index updated, closeout mapping written | N/A | Zero findings |

## 8. Tasks

**G0 — Owner capture gate (blocks T1).** The user commits sanitized real captures under `dash/src/canon/fixtures/skills/`. Each harness directory holds:

| Directory | Listing capture (what the harness had available) | Invocation capture (what was used) |
|---|---|---|
| `claude-code/` | One `claude_code.api_request_body` OTLP log record (sender has `OTEL_LOGS_EXPORTER=otlp` and `OTEL_LOG_RAW_API_BODIES=1`) from a session with skills installed, as exported JSON, `capture-listing.json` | One JSONL transcript excerpt containing a `Skill` `tool_use` block, `capture-invoke.jsonl` |
| `copilot-chat/` | One VS Code Copilot Chat OTLP span with `gen_ai.skills` (and `gen_ai.usage.skill_tokens` if present), `capture-listing.json` | One `execute_tool` span for the `skill` tool, `capture-invoke.json` |
| `antigravity/` | One Antigravity OTLP span with `gen_ai.skills` and `github.copilot.tool.parameters.skill_name`, `capture-listing.json` | Any span or log showing a skill being activated, if one exists, `capture-invoke.json` (otherwise a `README.md` line saying none exists) |
| `pi/` | One Pi collector `pi.llm.request` span with `pi.skills`, `capture-listing.json` | One Pi session-file excerpt with a `SKILL.md` or `skill://` read, `capture-invoke.jsonl` |
| `copilot-cli/` | One Copilot CLI OTLP span if the user collects Copilot CLI OTel, `capture-listing.json`; otherwise a `README.md` line saying no listing source exists | One `~/.copilot/session-state/<sid>/events.jsonl` excerpt with a `skill` tool request, `capture-invoke.jsonl` |
| `opencode/` | One OpenCode OTLP span with `ai.prompt.tools` or `gen_ai.*` tool definitions that include the `skill` tool, `capture-listing.json` | One OpenCode message or part row export with a `skill` tool part (`state.input.name`), `capture-invoke.json` |

Sanitize prompts and paths before committing. Keep skill names and descriptions, which are what the tests assert on.

**T1 — Fixtures and skills extraction module.** Skills: TypeScript engine work in `dash/src` (no dedicated role; the conductor assigns the closest implementer); `test-dev` writes RED.
- **Objective:** trim the G0 captures into test fixtures and implement `skills.ts` per section 4.
- **Files:** `dash/src/canon/fixtures/skills/<harness>/*` (derived fixtures beside the captures), `dash/src/canon/skills.ts` (new), `dash/src/canon/skills.test.ts` (new).
- **Shared constant:** the attribute name `kyber.skills.invoked` is fixed here, so T1 and T3 do not wait for each other.
- **Depends on:** G0.

**T2 — Projection, payload and turn content.** Skills: TypeScript engine work; `test-dev`.
- **Files:** `dash/src/canon/sessions.ts` (`buildSessionRow`, `AsadSessionPayload`, summary, turn rows), `dash/src/canon/sessions.skills.test.ts` (new), `dash/src/server/bridge.ts` (turn-content assembly only), `dash/src/server/kyber-content-route.test.ts` (additions).
- **Depends on:** T1.

**T3 — Carry parsed skills through synth.** Skills: TypeScript engine work; `test-dev`.
- **Files:** `dash/src/synth/synth.ts`; the `parserContractVersion` entries in `dash/src/refresh/registry.ts` for providers that fill `skills` (Claude family, Codex, Pi, Copilot, OpenCode/Kilo, Cline, CodeWhale, OpenClaude, DSH); `dash/src/synth/synth.skills.test.ts` (new).
- **Depends on:** none.

**T4 — Rewire the skill signal and detector.** Skills: TypeScript engine work; `test-dev`.
- **Files:** `dash/src/analysis/signals.ts`, `dash/src/analysis/findings.ts`, `dash/src/canon/store.ts` (`DETECTOR_VERSION` only), and their tests.
- **Depends on:** T1.

**T5 — Web skills section.** Skills: `react-dev`, `test-dev`.
- **Files:** `dash/web/src/lib/kyberApi.ts` (types, append only), `dash/web/src/components/SkillsPanel.tsx` (new), `dash/web/src/components/AgentSessionDashboard.tsx` (section, tiles, drawer handler, timeline renumber), `dash/web/src/components/fixtures/skills-payload.json` (new, authored from section 5), and the tests.
- **Depends on:** none. The fixture pins section 5, so GREEN does not need T2.

**T6 — Documentation.** Skill: `docs-dev`.
- **Files:** `docs/dash/architecture.md`: the session payload `skills` contract, the turn-content skills entry, the web dashboard section list, and the analysis-layer skill inputs. `docs/dash/telemetry-inventory.md`: a new "Skill availability by harness" table with listed and invoked status for each of the six harnesses plus the invoked-only ones, the enabling settings, and the G0 verification status. `docs/dash/runbook.md`: the Sessions skills section and the capture fixtures location.
- **Depends on:** T2, T3, T4, T5.

**T7 — Whole-change review.** Skill: `code-review`.
- **Depends on:** T6.

**T8 — `docs-dev` closeout (mandatory).** Skill: `docs-dev`.
- **Steps:** archive this plan to `docs/archive/plans/`, move its row to Archived in `docs/plans/README.md`, and write the closeout mapping.
- **ADR:** none expected. ADR waiver: this refines the existing session payload and analysis architecture under ADRs 0011, 0013 and 0033, and ADR 0018 is unchanged (Q10).
- **Checks:** run `docs validate . --merge-ready` and `docs drift .`.
- **Depends on:** T7.

## 9. Dependency graph

```text
G0 (owner captures) ──► T1 ──┬──► T2 ──┐
                             └──► T4 ──┤
T3 ────────────────────────────────────┼──► T6 ──► T7 ──► T8
T5 ────────────────────────────────────┘
```

- **Ready at start:** T3 and T5, which need no captures; plus G0, the owner's capture step.
- **After G0:** T1.
- **After T1:** T2 and T4 run in parallel.
- **File scopes are disjoint.**
  - T1: `canon/skills.ts` and its fixtures.
  - T2: `canon/sessions.ts` and `server/bridge.ts`.
  - T3: `synth/synth.ts` and `refresh/registry.ts`.
  - T4: `analysis/*` and one constant in `canon/store.ts`.
  - T5: `dash/web/**`.
- **No concurrency cap** is set.
- **Sizing.** Fixture curation is folded into T1, and the turn-content change into T2, to avoid splitting work that is too small for its own worker. Each task is sized for one worker.

## 10. Verification gates

- **Dash:** `npm --prefix dash run typecheck`, `lint`, `test` (includes the display-layer guard), and `check:reachable`.
- **Docs:** `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready` and `docs drift .`.
- **This Draft:** the architect harness had no shell. The conductor runs `docs validate` and `docs drift` on this plan and its index row before presenting the approval gate.
- **Owner gates:**
  - G0 captures exist before T1.
  - After merge, one live session per in-scope harness shows the section populated, or shows the stated not-measurable reason.

## 11. Risks, scope, review and closeout

**Risks.**

- **Format drift.** Listing text is prose inside other content for Claude Code and OpenCode. A harness update can move it, and the view then degrades to not measurable, never to a false empty list. G0 pins today's formats.
- **No Copilot CLI listing.** Copilot CLI may expose no listing at all (finding 4). It then ships invoked-only, which is honest but less than "exactly as tools".
- **Uncollected OpenCode OTLP.** OpenCode OTLP collection is not owner-verified (finding 5). Without it, OpenCode is invoked-only.
- **Shared bucket.** Rules and skills share `instruction_context` (D2). Extraction keys on `raw` attributes, not on bucket membership.
- **Content purge.** Part-only listings are lost after the 14-day purge (Q10).
- **Re-synthesis window.** Parser-contract bumps (Q8) re-synthesize only units inside the 14-day window. Older invoked skills appear only where `tool.invoke` arguments exist.
- **Re-materialization cost.** Rewiring the detector (Q9) changes existing findings when they are re-materialized under the new `DETECTOR_VERSION`.

**Out of scope.**

- A cross-session skills inventory or top-nav tab.
- Splitting the `instruction_context` bucket.
- New OTLP capture settings or writers.
- Tool extraction for deferred harnesses (#210–#215).
- The tray.
- Any change to ADR 0018.

**Review.** T7 runs a code-review council over the whole change.

**Closeout.** T8, by `docs-dev`. The harvest destinations are listed in T6.
