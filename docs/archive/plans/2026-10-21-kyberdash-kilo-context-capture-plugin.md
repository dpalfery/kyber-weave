---
id: plans/2026-10-21-kyberdash-kilo-context-capture-plugin
title: "KyberDash Kilo context capture through a Kilo plugin: system prompt, tool definitions and skills"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-21
development-mode: test-first
autonomy: supervised
---

# KyberDash Kilo context capture through a Kilo plugin

**Status: Complete 2026-10-21.** Built test-first and verified live against Kilo 7.4.16. OpenCode is out of scope by the owner's instruction and was not changed.

## 1. Problem and goal

The prior plan ([Kilo collection](2026-10-21-kyberdash-kilo-otel-collection.md), D7) marked Kilo's system prompt, tool definitions and instruction context unmeasurable, because Kilo's OTLP stream carries no model spans and `kilo.db` stores messages but not the prompt or tool schemas sent to the model. That was true of OTLP and `kilo.db`, and false of Kilo: Kilo is an open-source OpenCode fork whose plugin hooks run exactly where the prompt and tool list are assembled.

**Goal:** capture, for Kilo (CLI, VS Code extension, desktop app), the system prompt as sent (including the skills catalog and instruction files), each tool definition, and the agent, model and provider of every call, and make the Kilo reader report them.

## 2. Investigation findings

1. Kilo plugin hooks (`packages/plugin/src/index.ts`, `Kilo-Org/kilocode`, read 2026-10-21): `experimental.chat.system.transform` (assembled system prompt, `session/llm/request.ts`), `tool.definition` (per visible tool, `tool/registry.ts`), `chat.params` (agent, model, provider, user message).
2. Load path: any `.ts` or `.js` file in `<config dir>/plugin/` loads automatically; the module must default-export `{ id, server }`.
3. The `<available_skills>` block is part of the assembled system prompt, so one hook sees the skills catalog.
4. Kilo sets `experimental_telemetry: { isEnabled: false }`, so AI SDK spans never carry prompts; the hooks sit upstream of that layer.
5. Live result (Kilo 7.4.16, real CLI run in a project with an `AGENTS.md`): the 27 KB system prompt contained the `AGENTS.md` sentinel text and the skills catalog; 18 tool definitions were captured; the store was `0600`/`0700`. The model call itself failed on a subscription limit, and capture was unaffected because the hooks run first. The same prompt fires once per retry, so the store dedupes by content hash.
6. Each assistant message in `kilo.db` carries `parentID`, the user message it answers, and one user message can have several assistant messages (an agent loop). The capture's `chat.params` event carries that user message id.
7. A skill's body is a `skill` tool result, already in `kilo.db`.

## 3. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Where do the missing buckets come from? | A plugin installed into Kilo, using its documented hooks. Not OTLP, not `kilo.db`. | Findings 1 to 5. | Approved: owner said yes to building the Kilo plugin. |
| D2 | Files or network? | Files. The plugin appends to `~/.kyberdash/capture/kilo` (`blobs/<sha256>` plus `index.jsonl`); it has no network code. | Works when KyberDash is not running; a receiver fault cannot affect Kilo. | Approved |
| D3 | Failure isolation | Every hook is wrapped in try/catch, writes are small synchronous appends, and the first write error turns capture off for that process. | A plugin fault must never break the owner's Kilo. | Approved |
| D4 | Ownership under ADR 0031 | The owned unit may be a whole file. `enable` installs one plugin file with its own digest receipt; `disable` removes it only while the digest matches; a file KyberDash did not write is never overwritten; `kilo.jsonc` is never touched. Settings can switch it (Enable and Disable on the Kilo row, through the same preview and confirm flow), and it never turns the OTLP receiver on. | ADR 0031 amended in place. | Approved |
| D5 | How is a turn matched to a captured call? | By `sessionID` and the assistant message's `parentID`, choosing the call nearest in time. | Finding 6; retries and loop steps share a parent. | Approved |
| D6 | Measurability | A Kilo turn whose reader supplied `system_prompt` or `tool_definitions` is measured for that bucket; otherwise it stays `not_measurable`. `instruction_context` stays unmeasurable because it is inside the system prompt and not separable. The rule is scoped to Kilo's two buckets because Cursor deliberately declares a supplied bucket incomplete. | Absent is never zero. | Approved |
| D7 | Scope | Kilo only. No change to OpenCode code, config or OTLP. | Owner instruction. | Approved |
| D8 | Old OTel config | Removed for Kilo: the OTLP-framed status text, tests and docs are replaced. `OTEL_EXPORTER_OTLP_ENDPOINT` was removed from the owner's shell profiles and `launchctl` at the owner's request (backups beside each file). OpenCode's `experimental.openTelemetry` is untouched. | Owner chose to remove the endpoint variable after evidence that OpenCode OTLP produced almost no usable records. | Approved |
| D9 | Parser contract bump | Not bumped. Active sessions re-import on their own, and old units have no captured data to gain. | Avoids a one-time re-synthesis of every Kilo unit for no benefit. | Approved (autonomous: low-confidence, no authoritative source) |

## 4. Design

- `dash/src/capture/kilo-plugin/source.ts`: the plugin as installable text (node built-ins only, ownership marker on line one).
- `dash/src/capture/kilo-plugin/install.ts`: `installKiloPlugin`, `uninstallKiloPlugin`, `describeKiloPluginStatus`, with the plugin receipt `~/.kyberdash/kilo-plugin-receipt.json`.
- `dash/src/capture/harnesses/kilo.ts` and `registry.ts`: a `filePlugin` on the pending writer; `index.ts` calls it from enable, disable and status.
- `dash/src/synth/readers/kilo-capture.ts` and `kilo.ts`: the reader joins the store; `synth.ts` lifts the two declarations for supplied buckets.

## 5. Constraints

No feature logic in the web or tray display layers; one datastore and one API; no new dependencies; absent is never zero; a plugin fault never propagates; existing assertions weakened only where this plan names them.

## 6. Test contract (test-first)

- Plugin source: shape Kilo requires, records prompt, tools and call identity, dedupes, separates sessions, never throws, turns off after a write error, private permissions, both provider shapes.
- Installer: install, dry run, no-op, upgrade of its own file, refusal of a foreign file, `kilo.jsonc` untouched, uninstall (keeps captured data), refusal on an owner edit, status without echoing owner text.
- Capture reader: nearest call, session and message isolation, missing blob is absent, non-sha256 names rejected, corrupt lines skipped.
- Kilo reader: captured buckets present with a store, absent without; the developer's real store is never read in tests.
- Synthesizer: measurability lifted only for supplied Kilo buckets.
- Updated pins: the Kilo status text, manual steps (now two) and the old "enable writes nothing" tests.

## 7. Tasks

All done: plugin source, installer, capture command wiring, capture reader, Kilo reader join, measurability, test updates, docs and ADR amendment, live check.

## 8. Verification

- `dash`: 398 test files, 6,232 tests pass; `tsc --noEmit` clean.
- Live: real Kilo 7.4.16 run with the plugin installed through `kyberdash kyber capture enable --harness kilo` into a temporary HOME.
- `docs validate` clean.

## 9. Risks and follow-ups

- The hooks are `experimental.*` upstream and may change; `status` reports captured-call count and last-call time so a silent stop is visible.
- A later plugin can alter the prompt after ours; the capture can drift from what is sent.
- Tool definitions are captured per registry build, not per agent, so a per-agent tool filter can attach the latest definitions rather than that agent's exact set.
- Prompts can contain text from instruction files; the store is local and owner-only, and the runbook says how to delete it.
- Not yet exercised live: a run that loads a skill, and the desktop app's plugin directory.
- OpenCode v2 uses a different plugin API (`{ id, effect | setup }` with `session.hook`); a separate plugin would be needed. Out of scope here.

## 10. Completion summary and closeout

### Closeout mapping

| Durable decision | Destination |
|---|---|
| The plugin as the source of Kilo's prompt, tools and skills; how it loads and what it stores | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) (Kilo Code capture plugin and why not OTLP) |
| The owned unit may be a file; digest receipt; Settings route stays read-only | [ADR 0031](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) (amended 2026-10-21) |
| Reader join, measurability lift, enable/disable behaviour | [dash/architecture.md](../../dash/architecture.md) |
| How to install, check and remove it; where the data lives | [dash/runbook.md](../../dash/runbook.md) (section 10) |
