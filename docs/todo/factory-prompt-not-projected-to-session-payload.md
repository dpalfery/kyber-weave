---
id: todo/factory-prompt-not-projected-to-session-payload
title: The Factory prompt text never reaches the canonical session payload
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# The Factory prompt text never reaches the canonical session payload

The Factory capture plugin puts the user's prompt on the wire: the `UserPromptSubmit` turn span
carries `factory.turn.prompt.text`, capped at 64 KiB
(`dash/src/capture/harnesses/factory-plugin/mapper.ts`), and it travels only under the content
consent (`dash/src/capture/harnesses/factory-plugin/consent.ts`).

The `droid` canon adapter does not carry that attribute into the record's content
(`dash/src/canon/adapters/droid.ts`). Its `applyPluginEnvelope` bridges the session id, the model
and the tool name onto the keys the projection reads and copies the rest into `record.raw`; it
appends no content part. The OpenCode adapter has an `applyContent` that files the prompt as a
`conversation_history` part (`dash/src/canon/adapters/opencode.ts`); the `droid` adapter has no
equivalent.

So a reader of the dashboard's session view sees the turn structure, the tool names and sizes,
the timings and the model, and not the prompt. The prompt is captured, consented, transmitted and
kept in the raw payload, and the canonical session payload does not carry it.

That difference between the two plugin pipelines is not stated in the governed documents: the
[architecture](../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory)
section describes the Factory wire contract and the consent switch, and the
[telemetry inventory](../dash/telemetry-inventory.md#factory-droid-capture-the-plugin-path-and-the-file-path)
records the prompt text as travelling under consent, but neither says that the session payload
does not show it.

**What a decision must settle.** Whether the prompt reaches the canonical session payload at all,
and under what consent. The options are not symmetric:

- Project the prompt into a content part, as the OpenCode adapter does. The two plugin pipelines
  then behave alike, and the Factory prompt becomes subject to the 14-day content purge
  (`dash/src/canon/retention.ts`) that the raw-only copy is not.
- Leave the prompt in the raw payload only, and say so plainly in the governed documents. The raw
  payload is retained under ADR 0008's rule and is reachable through the raw-packet surface
  (`dash/src/server/bridge.ts`), so the text is not lost — but it is not part of the session's own
  content either.
- File the prompt but declare the session's prompt bucket `not_measurable` with a reason, so the
  absence is stated rather than shown as a blank.

Whichever is chosen, the reason must be recorded: "the plugin sends it and the projection drops
it" is exactly the gap the honest-unobservability rule
([`docs/rules/honest-unobservability.md`](../rules/honest-unobservability.md)) exists to prevent.

**Deliberately not done here.** This delivery changed neither the adapter, the wire contract nor
the projection. The contract's version and the consent gate are settled, and changing the content
projection is a different piece of work from the capture pipeline. The gap is recorded so that a
reader of the session view is not left to infer it.
