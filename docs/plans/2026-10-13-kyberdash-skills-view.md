---
id: plans/2026-10-13-kyberdash-skills-view
title: "KyberDash skills view: the skills a turn or session had access to"
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-14
development-mode: test-first
---

# KyberDash skills view: the skills a turn or session had access to

**Status: Ready 2026-10-13.** The user chose "approve" (approve and execute) on 2026-10-13 through the conductor relay, with development mode `test-first`. Before that approval:

- The user answered Q1–Q10 on 2026-10-13 and approved D1–D3 in the same relay.
- D4 is the default development mode.
- D5 is the fixtures path, which the architect chose because the user's Q7 answer asked for it.

The approval also covers D4, D5 and the Test contract in [section 7](#7-test-contract-test-first). No decision is open.

The conductor reruns `docs validate` and `docs drift`, because the architect harness has no shell.

**Progress.** T3 and T5 are done and passed task review (`fd54843`).

**Amendment AP2 (2026-10-13).** The user approved folding the T5 reviewer's findings F1–F4 into the remaining tasks, so the plan stays Ready:

- T2 is now the engine side only, including F1's served headline counts.
- T9 is new and carries the web follow-on: F1–F4.
- T9 can start now.

**Amendment AP3 (2026-10-13).** G0 is done (`53c08a1`). The user chose to have the conductor extract the captures read-only from the user's local KyberDash store and transcripts. The real data contradicts parts of the original design, so this amendment changes the plan:

- **Claude Code:** the skill listing comes from transcript `skill_listing` attachments, not OTLP. Reading them is new ingestion work, done by a new task, T10.
- **Copilot Chat:** the listing key is `github.copilot.context.skills`, plus an `<available_skills>` block in the system prompt.
- **Pi, OpenCode and Copilot CLI:** their listings ship as not measurable.

T1 and T10 can start now. T2 and T4 follow T1.

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
13. **Unverified wire formats (self-gathered).** No real capture in the repository pins any of these: the listing text for Claude Code (an `<available_skills>` block in the `Skill` tool description, or a system-reminder message, depending on version), the shape of Copilot `gen_ai.skills`, the Antigravity `skill_name` prefix, the OpenCode `skill` tool description, or any Copilot CLI listing. The repository fixtures hold synthetic placeholders. The user will supply sanitized captures (Q7, D5, gate G0). *Superseded by findings 14–17 (AP3).*
14. **Claude Code, from the G0 captures** (`dash/src/canon/fixtures/skills/claude-code/`).
    - **Listing.** It is a transcript `attachment` entry: `{"type":"attachment","attachment":{"type":"skill_listing","content":"- name: description\n…","skillCount":N,"isInitial":true,"names":[…]}}`. It is in the session JSONL that the existing Claude provider ingests, not in OTLP.
    - **Invocation.** An assistant `tool_use` named `Skill` with `input.skill`. `extractSkillNames` already parses this (`dash/src/ingest/parser.ts:1117`), and T3 carries it.
    - **Not yet ingested.** In the local store, the Claude Code records carry no skill attributes on `raw` and no listing in parts.
    - **Precedent.** The parser already reads one attachment type, `deferred_tools_delta`, through `extractLargeAddedNames` and `extractMcpInventory` (`parser.ts:483–490, 612–614, 984–993, 1670–1686`). That is the pattern for `skill_listing`.
    - **Contract version.** `CLAUDE_PARSER_CONTRACT_VERSION` is `'6'` (`dash/src/refresh/registry.ts:29`). `SKILLS_INVOKED_KEY` is at `dash/src/canon/types.ts:202`.
    - **Unverified.** Only `isInitial: true` was captured, so whether a later listing (`isInitial: false`) is a full list or a delta is not known.
15. **Copilot Chat, from the G0 captures** (`copilot-chat/`).
    - **Listing names.** The real key is `github.copilot.context.skills`, a JSON-array string of names, on `invoke_agent` spans (source `github-copilot`, harness `copilot`).
    - **Descriptions.** The system-prompt part also holds an `<available_skills><skill><name/><description/><location/></skill>…</available_skills>` block.
    - **What the adapter does today.** The adapter reads `gen_ai.skills` only (`copilot.ts:428`), which this capture does not carry. It does nothing with `github.copilot.context.skills`. That key survives only because `baseRecord` keeps every attribute on `raw` (`copilot.ts:688`).
    - **Rollup, not turn.** `invoke_agent` is a rollup record, not a turn, when its child calls exist (`sessions.ts:62–80`). The listing must therefore be attributed to the turns of that agent invocation.
    - **Invocations.** The store has no Copilot skill invocation span; only `view`, `task`, `bash` and `edit` tool spans exist.
16. **Antigravity** matches the plan.
    - `gen_ai.skills` is one string, `"Active Skills: a, b, c, d"`, and `github.copilot.tool.parameters.skill_name` duplicates it.
    - `gen_ai.usage.skill_tokens` is the harness-reported total.
    - No activation record exists.
17. **Pi, OpenCode and Copilot CLI.** The user's store holds no Pi or OpenCode data. Copilot CLI cannot be told apart from Copilot Chat in the store: both arrive as source `github-copilot`. No captures exist. See the READMEs under each fixtures directory.

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
| D4 | Development mode | `test-first` | The user did not opt out. | ANSWERED — default per architect contract; confirmed by the user's approval 2026-10-13 ("approve", test-first) |
| D5 | Where do the user's captures go, and what is each harness's capture? | The root is **`dash/src/canon/fixtures/skills/`**, with one subdirectory per harness. The user drops sanitized originals as `capture.*` in each subdirectory, and the T1 worker derives trimmed test fixtures beside them. The contents per harness are in [section 8, gate G0](#8-tasks). | It sits beside the existing `dash/src/canon/fixtures/pi-collector/`, under the dash test tree, so `npm --prefix dash run test` picks it up. One directory per harness keeps the captures for each harness separate and reviewable. | ANSWERED — chosen by the architect at the user's request in Q7, 2026-10-13; approved by the user's approval 2026-10-13 |
| AP1 | Approve and execute? | **Approved.** The plan is Ready for execution in `test-first` mode. | The approval covers Q1–Q10, D1–D5, the section 7 Test contract and the section 8 task graph. Execution of T1 and every task after it is still gated on the owner captures (G0). | APPROVED — user "approve" 2026-10-13 (conductor relay) |
| AP2 | Should the T5 task-review findings F1–F4 be folded into the remaining work? | **Yes.** Folded as follows, with no existing task renumbered.<br>**F1, served headline counts.** The engine adds `skills.listed_count` and `skills.listed_uninvoked_count` (section 5). The web renders those values instead of counting rows.<br>**F2, skill pane.** A skill-specific pane in `SessionInspectorDrawer.tsx` for `{skill: row}` drawer content.<br>**F3, unused export.** Remove the `KyberSessionTurnSkills` export unless the new code uses it.<br>**F4, empty headline.** Hide the headline when `rows` is empty, keeping the line "No skill was recorded for this session."<br>T2 keeps the engine side (F1 projection). A new task, T9, takes the web side (F1 render, F2, F3, F4). | T2 originally gained web scope as well, which no longer fit one worker. Its engine part (projection, headline counts, turn-content entry in `sessions.ts` and `bridge.ts`) and its web part (`SkillsPanel`, a new drawer pane, fixture and types) are each large enough for a separate worker. Their file scopes do not overlap. They run in parallel because section 5 now pins both new fields: the headline counts and the turn-content `skills` entry. T9 extends the T5 fixture to that shape. | APPROVED — user "fold the findings in" 2026-10-13 (conductor relay) |
| AP3 | Captures exist (G0). How does the design change to match the real formats? | **G0 route:** the conductor extracted sanitized captures read-only from the user's local store and transcripts into `dash/src/canon/fixtures/skills/` (`53c08a1`). Design changes (findings 14–17):<br>**(1) Claude Code listing.** It comes from the transcript `attachment` entry `{type:'skill_listing', content, skillCount, isInitial, names}`, not from OTLP. New task T10 makes the Claude parser read it, carries it onto the turn record as `raw['kyber.skills.listed']` (constant `SKILLS_LISTED_KEY` in `dash/src/canon/types.ts`), and bumps `CLAUDE_PARSER_CONTRACT_VERSION`. T1's Claude extractor reads that attribute. Invocations are `Skill` `tool_use` blocks with `input.skill`, already carried by T3.<br>**(2) Copilot Chat listing.** Names come from `raw['github.copilot.context.skills']` (a JSON-array string on `invoke_agent` rollup spans). Descriptions come from the `<available_skills>` XML block in the `system_prompt` part, when present. Not from `gen_ai.skills`. Invoked is not measurable unless a `skill` tool record is present.<br>**(3) Antigravity.** As planned: the `gen_ai.skills` string with the "Active Skills:" prefix, and the reported total `gen_ai.usage.skill_tokens`. Invoked is not measurable (no activation record exists).<br>**(4) Pi, OpenCode, Copilot CLI.** No captures were possible. Each listing side ships `not_measurable`, naming the absent source. Invoked comes only from what the parsers already provide (T3). No wire format is invented, and the `pi.skills` extractor is out of scope. | Real data overrides the unverified formats of finding 13. A Claude listing from transcripts covers file-ingested sessions without the `OTEL_LOG_RAW_API_BODIES` opt-in, and it lives on `raw`, so it survives the 14-day purge. The example reason in Q6 ("Claude Code session files do not carry the skill listing…") is superseded and must not be used as copy. The user confirmed that the conductor may relay these format findings. | APPROVED — user approved the G0 route 2026-10-13 (conductor relay) |
| AP4 | Where does the turn-content `skills` entry get its data, given that the narrow turn-content seam must not walk session records? | **Option D: serve the entry from the stored session payload. No record walk and no schema change.**<br>**Projection.** `buildSessionRow` adds two additive payload fields:<br>• `skills.rows[].description`: the skill's listing text, once per skill per session. It is the first description observed in the session, and it is absent when no listing carried one.<br>• `turns[i].skills_invoked`: names invoked in that turn, present only when the invoked side is measured.<br>**Route.** `assembleTurnContent` builds the entry from the payload it already reads (`getSessionPayload`):<br>• `listed` = `turns[i].skills_listed` mapped to `{name, text: rows[name].description}`.<br>• `invoked` = `turns[i].skills_invoked`.<br>• Each side is `not_measurable` (with the payload's reason) when its key is absent. The whole entry is `not_measurable` ("session not yet projected with skills") when `payload.skills` is absent.<br>The section 5 shape of the entry is unchanged. | **Option A rejected.** The narrow seam is a performance invariant, pinned by three tests in `dash/src/server/bridge-fallback-cost.test.ts` (lines 125–148, 388–402, 411–482). Its stated reason (`bridge.ts:2473–2475`) is that a 7,000-turn session must not be decompressed to return one turn. Relaxing it would cost that on every drawer open.<br>**Option B rejected** (per-turn text in the payload): it repeats the listing text on every turn.<br>**Option C ruled out:** it needs a schema change.<br>**Why D:** it is B with the text deduplicated by skill name. It keeps ADR 0033, because the engine computes and the route only reads, and it fits D1 ("results ride in the existing session payload"). It adds about one description per skill per session, the same text already counted for `derived_tokens`, so the size cost is bounded by the listing itself.<br>**Purge:** Claude descriptions live on `raw` and survive. Copilot descriptions come from parts, so after the purge `text` is absent and the name remains.<br>**T9:** the already-reviewed pane reads `listed[].text` for the first turn in `turns_listed`, and with first-observed descriptions that is the text of that turn's listing. T9 is unaffected.<br>**Known limit:** if a later listing in the same session changes a skill's description, earlier turns still show the first text. This is accepted and documented. | ANSWERED — settled by the architect 2026-10-14. Additive to the approved section 5 and D1; it changes no documented invariant and no approved decision. |

Open decisions: **none**.

## 4. Design

- **Engine module `dash/src/canon/skills.ts`** (pure, no I/O).
  - `extractListedSkills(record)` returns `{name, description?, sourceKey}[]`, or `NotMeasurable` with a reason. There is one extractor per harness, each pinned to the G0 captures:
    *Amended by AP3. The capture-pinned extractors are:*
    - **Claude Code:** reads `raw['kyber.skills.listed']`, written by T10. It does not read OTLP text.
    - **Copilot Chat:** names come from `raw['github.copilot.context.skills']` on `invoke_agent` records. A string that does not parse as a JSON array gives `NotMeasurable`. Descriptions come from the `<available_skills>` block in the same trace's `system_prompt` part, when present; after the purge, names remain and descriptions are absent.
    - **Antigravity:** reads `gen_ai.skills` with the `Active Skills:` prefix removed, split on commas. `github.copilot.tool.parameters.skill_name` is the fallback when `gen_ai.skills` is absent.

    *Not measurable, with the absent source named:* the listing for Pi, OpenCode and Copilot CLI (finding 17), and the listing for any other harness. An unrecognised shape also returns `NotMeasurable`, never `[]`.

    *Listing carry-forward, which T1 applies across a session's turns:*
    - **Claude.** An `isInitial: true` listing replaces the effective list. A non-initial listing is unioned into it. That is an assumption, pinned by a test and listed as a risk (finding 14).
    - **Copilot.** An `invoke_agent` listing applies to every turn record under that invocation (same trace, at or after the rollup).
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
- **Synth (`synth.ts`, Q8).** The turn record built from a `ParsedProviderCall` carrying `skills` gains `raw['kyber.skills.invoked']`. The affected harness-source `parserContractVersion` values are bumped. This is done in T3.
- **Claude listing ingestion (T10, AP3).**
  - **Parser.** The Claude parser reads `attachment.type === 'skill_listing'` entries, both in the small-file path and the large-file path in `parser.ts`, following the `deferred_tools_delta` precedent. It parses `content` lines of the form `- name: description` into descriptions keyed by `names[]`.
  - **Attaching to calls.** The parser puts the listing on the first `ParsedProviderCall` after each attachment, as a new optional field `skillsListed`.
  - **Writing to `raw`.** `synth.ts` writes `raw[SKILLS_LISTED_KEY]`, where `SKILLS_LISTED_KEY = 'kyber.skills.listed'`, as `{ isInitial: boolean, skills: [{ name, description? }] }`. A name with no matching content line has no description.
  - **Contract version.** `CLAUDE_PARSER_CONTRACT_VERSION` goes from `'6'` to `'7'`.
- **Invoked side by harness (AP3).**
  - **Measured:** Claude, from `Skill` calls and `kyber.skills.invoked`, and the file-ingested harnesses that carry `kyber.skills.invoked`.
  - **Copilot Chat and Copilot CLI:** measured only when a `skill` `tool.invoke` record or `kyber.skills.invoked` is present. Otherwise `not_measurable` with the reason "no skill invocation record observed for this harness".
  - **Antigravity:** always `not_measurable`.
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
    "listed_count": 3,                        // AP2/F1: rows with listed === true; absent when the listing side is not measured
    "listed_uninvoked_count": 1,              // AP2/F1: rows with listed === true AND invocations === 0 (a number);
                                              // absent when either side is not measured; an unmeasured row never counts
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
- **Headline counts (AP2/F1).** The web renders the headline "X of Y listed skills had no observed invocation" only from `listed_uninvoked_count` (X) and `listed_count` (Y), and only when both are present. If either is absent, the headline states that it is not measurable. The browser never counts rows. If `rows` is empty, the headline is hidden and the line "No skill was recorded for this session." is shown (F4).

**Turn-content `skills` entry (AP2/F2)** in `GET /api/kyber/session/:id/turn/:index/content` is additive. Existing blocks are unchanged:

```jsonc
{
  "skills": {
    "listed": [ { "name": "docs", "text": "docs: Answer questions from the repo docs …" } ],  // "text" absent when the listing carried no description
    "invoked": ["docs"]                                       // names invoked in this turn; [] is a measured "none"
    // either side may instead be {"availability":"not_measurable","reason":"…"}
  }
  // or: "skills": { "availability": "not_measurable", "reason": "…" }
}
```

The skill drawer pane (T9) reads the listing text for a skill from the first turn in that row's `turns_listed`, through this entry.

**AP4: what feeds the entry.** The route builds this entry only from the stored session payload, never by walking session records. The payload gains two additive fields:

- `skills.rows[].description` (optional): the first description observed for that skill in the session.
- `turns[i].skills_invoked` (optional): the names invoked in that turn. It is absent when the invoked side is not measured, and `[]` is a measured "none".

The route maps them like this:

- `listed[]` is `turns[i].skills_listed` joined to `rows[].description`, which becomes `text`.
- `invoked` is `turns[i].skills_invoked`.
- A side whose payload key is absent is `not_measurable`.
- If `payload.skills` is absent, the whole entry is `not_measurable` with the reason "session not yet projected with skills".

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
| T1 (amended AP3) | `dash/src/canon/skills.test.ts` (new), over canonical-record fixtures built from the G0 captures in `dash/src/canon/fixtures/skills/<harness>/` | `cd dash && npx vitest run src/canon/skills.test.ts` | **Claude:** a record whose `raw['kyber.skills.listed']` holds the `claude-code/capture-listing.jsonl` listing yields exactly `app-docs-standard`, `architecture-decision-record` and `azure-cli`, with their descriptions. A non-initial listing unions into the effective list and an initial one replaces it. **Copilot:** `raw['github.copilot.context.skills']` from `copilot-chat/capture-listing.json` yields its six names. Descriptions come from `capture-listing-system-prompt.xml` when a `system_prompt` part holds it; without the part, the names remain and the descriptions are absent. A malformed JSON string gives `not_measurable`. A rollup's listing applies to the turns under it. **Antigravity:** `antigravity/capture-listing.json` yields the names after the `Active Skills:` prefix, and `reported_skill_tokens` equals `gen_ai.usage.skill_tokens`, never split. **Not measurable:** the listing for Pi, OpenCode, Copilot CLI and any unknown harness is `not_measurable` with a reason naming the absent source, never `[]`. **Invoked:** names come from `Skill`/`skill` arguments and `kyber.skills.invoked`, never from tool names, with no double count. Copilot without a skill record is `not_measurable`. Antigravity is `not_measurable`. **Other:** rule text in `instruction_context` is never a skill, and derived tokens are labelled derived. | Module absent, or assertions fail | All pass |
| T10 (AP3) | `dash/src/ingest/parser.skill-listing.test.ts` (new), `dash/src/synth/synth.skills.test.ts` (additions) | `cd dash && npx vitest run src/ingest/parser.skill-listing.test.ts src/ingest/parser.test.ts src/synth/synth.skills.test.ts src/synth/synth.test.ts src/refresh/registry.test.ts` | **Parsing:** `claude-code/capture-listing.jsonl` followed by an assistant turn yields a `ParsedProviderCall.skillsListed` with `isInitial: true` and three `{name, description}` entries, the descriptions matching the content lines exactly. Both the small-file and the large-file parse paths give the same result. **No listing:** a transcript with no listing leaves the field absent. **Synth:** the field becomes `raw['kyber.skills.listed']`. **Contract:** `CLAUDE_PARSER_CONTRACT_VERSION` is greater than `'6'`. **Regression:** existing parser and synth suites pass unmodified. | Field and attribute absent | All pass |
| T2 | `dash/src/canon/sessions.skills.test.ts` (new), `dash/src/server/kyber-content-route.test.ts` (additions) | `cd dash && npx vitest run src/canon/sessions.skills.test.ts src/canon/sessions.test.ts src/server/kyber-content-route.test.ts` | **Payload:** `skills`, `summary.skills_*` and `turns[].skills_listed` follow section 5. **Unmeasured:** a harness with no source gives `not_measurable` and no `summary.skills_*` keys. **Headline counts (AP2/F1):** `listed_count` and `listed_uninvoked_count` are correct. Both are absent when the listing side is not measured. `listed_uninvoked_count` is absent when invocations are not measured. A row with an unmeasured side never counts as uninvoked. **Turn content:** the `skills` entry follows section 5, carrying listed names with text and invoked names, or a reason. Existing block bytes are unchanged. **Regression:** existing suites pass unmodified.<br>**AP4 test changes (`test-dev` first):**<br>• `sessions.skills.test.ts` (additions): `rows[].description` is the first observed description and is absent when none was listed. `turns[i].skills_invoked` holds that turn's invoked names, is `[]` when measured with none, and is absent when the invoked side is not measured.<br>• `kyber-content-route.test.ts`: the 6 existing T2 route tests change their hand-built `upsertSession` payloads to carry `skills.rows` (with `description`) and `turns[i].skills_listed`/`skills_invoked`. Their assertions on the entry are unchanged. Three tests are added: a payload without `skills` gives an entry `not_measurable` with "session not yet projected with skills"; a turn missing `skills_invoked` gives `invoked` as `not_measurable`; and spies on `store.recordsForSession` (never called) and `store.get` (at most the target span) prove that building the skills entry does no session walk.<br>• `bridge-fallback-cost.test.ts`: **no change.** Its three narrow-seam tests must still pass. | New keys or entry absent; the new route tests fail on the current blocked state | All pass; `bridge-fallback-cost.test.ts` unmodified and green |
| T3 | `dash/src/synth/synth.skills.test.ts` (new) | `cd dash && npx vitest run src/synth/synth.skills.test.ts src/synth/synth.test.ts` | A `ParsedProviderCall` with `skills` gives a turn record with `raw['kyber.skills.invoked']` equal to those names. Without `skills`, the key is absent. Every bumped `parserContractVersion` is greater than its previous value. | Attribute absent | All pass |
| T4 | `dash/src/analysis/signals.test.ts`, `dash/src/analysis/findings.test.ts` (additions) | `cd dash && npx vitest run src/analysis/signals.test.ts src/analysis/findings.test.ts` | A `Skill` call loading `docs` credits `docs`, not `Skill`. A listed-not-invoked skill drives `inactive-skill-reference` at low confidence, ranked last, with no deletion phrasing. `DETECTOR_VERSION` is incremented. | The new assertions fail on the current tool-name crediting and regex input | All pass, the existing D16 assertions included |
| T5 | `dash/web/src/components/SkillsPanel.test.tsx` (new), `dash/web/src/components/AgentSessionDashboard.test.tsx` (additions); payload fixture `dash/web/src/components/fixtures/skills-payload.json` (new) | `cd dash && npx vitest run web/src/components/SkillsPanel.test.tsx web/src/components/AgentSessionDashboard.test.tsx src/architecture/display-layer.test.ts` | Rows render from the fixture, with listed and invoked badges and turns listed. Tokens are labelled derived. The reported aggregate shows where present, with no currency anywhere. The Not measurable card shows its reason. A missing `skills` key renders "not yet projected". No "waste" wording. The tiles render, and a row click opens the skill drawer. The section headings read "4 · Skills" and "5 · Execution Timeline". The display-layer guard passes. | Component absent | All pass |
| T9 (AP2) | `dash/web/src/components/SkillsPanel.test.tsx`, `dash/web/src/components/AgentSessionDashboard.test.tsx` (additions as needed), `dash/web/src/components/SessionInspectorDrawer.test.tsx` (additions) | `cd dash && npx vitest run web/src/components/SkillsPanel.test.tsx web/src/components/AgentSessionDashboard.test.tsx web/src/components/SessionInspectorDrawer.test.tsx src/architecture/display-layer.test.ts` | **F1:** the headline renders the served `listed_uninvoked_count` and `listed_count`. A fixture whose counts deliberately differ from a row count proves no counting in the browser. With either count absent, the headline says "not measurable". **F4:** empty `rows` hides the headline and keeps "No skill was recorded for this session." **F2:** a `{skill: row}` drawer shows listed and invoked state, turns listed (1-based display), and the listing text from the turn-content `skills` entry. A not-measurable listing shows its reason, not an empty pane. **F3:** `KyberSessionTurnSkills` is either removed or used. **Regression:** the display-layer guard passes. | The new assertions fail on the T5 code (headline counted from rows; no skill pane) | All pass, with the T5 assertions still green |
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
- **AP3 scope:** the extractors are as amended in section 4. Only the Claude, Copilot Chat and Antigravity extractors are pinned by captures. `dash/src/canon/types.ts` gets one pinned line beside `SKILLS_INVOKED_KEY`: `export const SKILLS_LISTED_KEY = 'kyber.skills.listed' as const`. T10 adds the same line. Whichever task lands first adds it, and the other's merge is a no-op, so T1 and T10 stay parallel.
- **Depends on:** G0 (done, `53c08a1`). It does not depend on T10, because tests build records carrying the attribute directly.

**T10 — Claude Code skill-listing ingestion (AP3).** Skills: TypeScript engine work in `dash/src`; `test-dev` writes RED.
- **Objective:** section 4, "Claude listing ingestion".
- **Files:**
  - `dash/src/ingest/parser.ts`: the `skill_listing` attachment in both parse paths, beside `deferred_tools_delta`, and placing it on the following call;
  - `dash/src/providers/types.ts`: optional `skillsListed` on `ParsedProviderCall`;
  - `dash/src/providers/claude.ts`: only if the call hand-off needs it;
  - `dash/src/synth/synth.ts`: write `raw[SKILLS_LISTED_KEY]` beside `SKILLS_INVOKED_KEY`;
  - `dash/src/canon/types.ts`: the pinned constant line;
  - `dash/src/refresh/registry.ts`: `CLAUDE_PARSER_CONTRACT_VERSION` only;
  - tests: `dash/src/ingest/parser.skill-listing.test.ts` (new) and `dash/src/synth/synth.skills.test.ts` (additions).
- **Depends on:** G0 (done). It runs in parallel with T1, T9, and T2 and T4 once they start. T3, which touched `synth.ts` and `registry.ts`, is already done.

**T2 — Projection, payload and turn content.** Skills: TypeScript engine work; `test-dev`.
- **Files:** `dash/src/canon/sessions.ts` (`buildSessionRow`, `AsadSessionPayload`, summary, turn rows), `dash/src/canon/sessions.skills.test.ts` (new), `dash/src/server/bridge.ts` (turn-content assembly only), `dash/src/server/kyber-content-route.test.ts` (additions).
- **Depends on:** T1.

*AP4 scope note for T2:*

- **Engine:** add `rows[].description` and `turns[].skills_invoked` in `sessions.ts`.
- **Route:** build the turn-content `skills` entry in `assembleTurnContent` (`bridge.ts`) from `payload.skills` and `payload.turns[i]` only. Do not call `recordsForSession` and do not issue extra `get` calls.
- **Unchanged:** `bridge-fallback-cost.test.ts` stays as it is.
- **Order:** the test changes listed in the T2 Test-contract row go to `test-dev` first (RED), then GREEN.

*AP2 scope note for T2:* T2 also projects `skills.listed_count` and `skills.listed_uninvoked_count` (F1), and its turn-content entry follows the pinned section 5 shape (F2). T2 touches no `dash/web` file.

**T9 — Web follow-on from T5 review (AP2).** Skills: `react-dev`, `test-dev` (RED first).
- **Objective:** F1 render the served headline counts, F2 skill drawer pane, F3 remove the unused export, F4 empty-state headline.
- **Files:**
  - `dash/web/src/components/SkillsPanel.tsx`, `SkillsPanel.test.tsx`;
  - `dash/web/src/components/AgentSessionDashboard.tsx`, its test (only where it computes or passes the headline);
  - `dash/web/src/components/SessionInspectorDrawer.tsx`, `SessionInspectorDrawer.test.tsx`;
  - `dash/web/src/components/fixtures/skills-payload.json`: add the two counts and a turn-content `skills` sample;
  - `dash/web/src/lib/kyberApi.ts`: types for the counts and the turn-content `skills` entry; remove `KyberSessionTurnSkills` unless used.
- **Depends on:** T5 (done). It runs in parallel with T1, T2 and T4, because section 5 pins the shapes.

**T3 — Carry parsed skills through synth.** **Done:** passed task review, `fd54843`. Skills: TypeScript engine work; `test-dev`.
- **Files:** `dash/src/synth/synth.ts`; the `parserContractVersion` entries in `dash/src/refresh/registry.ts` for providers that fill `skills` (Claude family, Codex, Pi, Copilot, OpenCode/Kilo, Cline, CodeWhale, OpenClaude, DSH); `dash/src/synth/synth.skills.test.ts` (new).
- **Depends on:** none.

**T4 — Rewire the skill signal and detector.** Skills: TypeScript engine work; `test-dev`.
- **Files:** `dash/src/analysis/signals.ts`, `dash/src/analysis/findings.ts`, `dash/src/canon/store.ts` (`DETECTOR_VERSION` only), and their tests.
- **Depends on:** T1.

**T5 — Web skills section.** **Done:** passed task review, `fd54843`. The reviewer's findings F1–F4 moved to T2 and T9 under AP2. Skills: `react-dev`, `test-dev`.
- **Files:** `dash/web/src/lib/kyberApi.ts` (types, append only), `dash/web/src/components/SkillsPanel.tsx` (new), `dash/web/src/components/AgentSessionDashboard.tsx` (section, tiles, drawer handler, timeline renumber), `dash/web/src/components/fixtures/skills-payload.json` (new, authored from section 5), and the tests.
- **Depends on:** none. The fixture pins section 5, so GREEN does not need T2.

**T6 — Documentation.** Skill: `docs-dev`.
- **Files:** `docs/dash/architecture.md`: the session payload `skills` contract, the turn-content skills entry, the web dashboard section list, and the analysis-layer skill inputs. `docs/dash/telemetry-inventory.md`: a new "Skill availability by harness" table with listed and invoked status for each of the six harnesses plus the invoked-only ones, the enabling settings, and the G0 verification status. `docs/dash/runbook.md`: the Sessions skills section and the capture fixtures location.
- **AP3 additions:**
  - `telemetry-inventory.md`: the Claude listing comes from transcript `skill_listing` attachments, with no OTLP opt-in. Copilot Chat uses `github.copilot.context.skills` and `<available_skills>`. Antigravity uses `gen_ai.skills` and `skill_tokens`. Pi, OpenCode and Copilot CLI are not measurable and unverified.
  - The Claude file-synth row: session files now carry the skill listing, and still do not carry `system_prompt` or `tool_definitions`.
- **Depends on:** T2, T3, T4, T5, T9, T10.

**T7 — Whole-change review.** Skill: `code-review`.
- **Depends on:** T6.

**T8 — `docs-dev` closeout (mandatory).** Skill: `docs-dev`.
- **Steps:** archive this plan to `docs/archive/plans/`, move its row to Archived in `docs/plans/README.md`, and write the closeout mapping.
- **ADR:** none expected. ADR waiver: this refines the existing session payload and analysis architecture under ADRs 0011, 0013 and 0033, and ADR 0018 is unchanged (Q10).
- **Checks:** run `docs validate . --merge-ready` and `docs drift .`.
- **Depends on:** T7.

## 9. Dependency graph

```text
G0 (done 53c08a1) ──► T1 ──┬──► T2 ──┐
                   │        └──► T4 ──┤
                   └──► T10 ──────────┤
T3 (done fd54843) ────────────────────┤
T5 (done fd54843) ──► T9 ─────────────┼──► T6 ──► T7 ──► T8
```

- **Done:** T3 and T5 (`fd54843`, task review PASS) and G0 (`53c08a1`).
- **Ready now:** T1, T10 and T9, in parallel.
- **After T1:** T2 and T4 run in parallel, alongside T10 and T9.
- **File scopes are disjoint.** The only exception is one identical pinned line in `canon/types.ts`, shared by T1 and T10 (see T1).
  - T1: `canon/skills.ts` and its fixtures.
  - T10: `ingest/parser.ts`, `providers/types.ts`, `providers/claude.ts`, `synth/synth.ts`, and the Claude contract constant in `refresh/registry.ts`.
  - T2: `canon/sessions.ts` and `server/bridge.ts`, plus their tests. No web files.
  - T4: `analysis/*` and one constant in `canon/store.ts`.
  - T9: the `dash/web` files listed for T9.
  - T3 and T5 are done; their files now belong to T9 where it lists them.
- **No concurrency cap** is set.
- **Sizing.** Fixture curation is folded into T1, to avoid a task too small for its own worker. Under AP2, the turn-content change stays in T2. The web follow-on is its own task, T9: the engine and web parts are each a full worker's load and do not overlap, and section 5 pins the shapes they share, so they run in parallel. Under AP3, the Claude ingestion work is its own task, T10. It spans the parser's two paths, the call hand-off, synth and the contract bump, which is a full worker's load, and its files do not overlap T1's extraction module. T1 stays one worker: three capture-pinned extractors, the not-measurable cases, and the view builder.

## 10. Verification gates

- **Dash:** `npm --prefix dash run typecheck`, `lint`, `test` (includes the display-layer guard), and `check:reachable`.
- **Docs:** `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready` and `docs drift .`.
- **This plan:** the architect harness has no shell. The conductor runs `docs validate` and `docs drift` on this plan and its index row after each plan edit.
- **Owner gates:**
  - G0 captures exist before T1.
  - After merge, one live session per in-scope harness shows the section populated, or shows the stated not-measurable reason.

## 11. Risks, scope, review and closeout

**Risks.**

- **Format drift.** A Claude Code or Copilot update can rename `skill_listing` or `github.copilot.context.skills`. The view then degrades to not measurable, never to a false empty list. G0 pins today's formats (AP3).
- **Claude non-initial listings.** Only `isInitial: true` was captured. Treating a later listing as a union is an assumption (finding 14). If later listings turn out to be full replacements, a removed skill would still show as listed. That would show up in a live check, and the fix would be a one-line change in T1.
- **Copilot descriptions are purged.** Copilot descriptions come from the system-prompt part and are lost after the 14-day purge. The names survive on `raw`.
- **Unverified harnesses (AP3).** Pi, OpenCode and Copilot CLI have no captures (finding 17). Their listings ship as not measurable, and their invoked sides come only from existing parsers. No wire format was invented for them. They need a capture before any listing support is added (follow-up, not in this plan). Copilot CLI cannot be told apart from Copilot Chat in the store.
- **Claude re-synthesis window.** The T10 contract bump re-synthesizes Claude units only inside the 14-day window. Older Claude sessions have no listing until their files are re-imported.
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
