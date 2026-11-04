---
id: archive/todo/factory-prompt-not-projected-to-session-payload
title: The Factory prompt text never reaches the canonical session payload
doc-type: todo
component: KyberDash
status: archived
owner: dpalfery
last-reviewed: 2026-11-04
documentation-impact: extends-component
impact-components: [KyberDash]
---

# The Factory prompt text never reaches the canonical session payload

Delivered on 2026-11-04: the `droid` canon adapter files the consented prompt as the record's
`conversation_history` part, so the canonical session payload carries it and the session view
shows what was asked. The **prompt turn** whose prompt the consent withheld declares the prompt
`not_measurable` with a reason that names the consent; the plugin's **completion turn** for the
same turn carries no prompt by construction, so it says nothing about one. The decision is recorded in
[ADR 0055](../../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md).

The Factory capture plugin puts the user's prompt on the wire: the `UserPromptSubmit` turn span
carries `factory.turn.prompt.text`, capped at 64 KiB
(`dash/src/capture/harnesses/factory-plugin/mapper.ts`), and it travels only under the content
consent (`dash/src/capture/harnesses/factory-plugin/consent.ts`).

The `droid` canon adapter did not carry that attribute into the record's content
(`dash/src/canon/adapters/droid.ts`). Its `applyPluginEnvelope` bridged the session id, the model
and the tool name onto the keys the projection reads and copied the rest into `record.raw`; it
appended no content part. The OpenCode adapter has an `applyContent` that files the prompt as a
`conversation_history` part (`dash/src/canon/adapters/opencode.ts`); the `droid` adapter had no
equivalent.

So a reader of the dashboard's session view saw the turn structure, the tool names and sizes,
the timings and the model, and not the prompt. The prompt was captured, consented, transmitted and
kept in the raw payload, and the canonical session payload did not carry it.

That difference between the two plugin pipelines was not stated in the governed documents: the
[architecture](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory)
section described the Factory wire contract and the consent switch, and the
[telemetry inventory](../../dash/telemetry-inventory.md#factory-droid-capture-the-plugin-path-and-the-file-path)
recorded the prompt text as travelling under consent, but neither said that the session payload
did not show it.

**The options, as they stood.** Whether the prompt reached the canonical session payload at all,
and under what consent. The options were not symmetric:

- Project the prompt into a content part, as the OpenCode adapter does. The two plugin pipelines
  then behave alike, and the Factory prompt becomes subject to the 14-day content purge
  (`dash/src/canon/retention.ts`) that the raw-only copy is not.
- Leave the prompt in the raw payload only, and say so plainly in the governed documents. The raw
  payload is retained under ADR 0008's rule and is reachable through the raw-packet surface
  (`dash/src/server/bridge.ts`), so the text is not lost — but it is not part of the session's own
  content either.
- File the prompt but declare the session's prompt bucket `not_measurable` with a reason, so the
  absence is stated rather than shown as a blank.

Whichever was chosen, the reason had to be recorded: "the plugin sends it and the projection
drops it" is exactly the gap the honest-unobservability rule
([`docs/rules/honest-unobservability.md`](../../rules/honest-unobservability.md)) exists to
prevent.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| FPROMPT-1 | Should the Factory prompt text reach the canonical session payload, and under what consent? | Yes. The `droid` adapter files the consented prompt as the record's `conversation_history` part, so the session payload carries it and the dashboard's session view shows what was asked. The consent still decides whether the text travels at all: the plugin gates it at emission, the store's gate strips it from a claimed envelope while the consent is off, and a turn whose prompt never arrived declares the prompt `not_measurable` with a reason that names the consent. | The two plugin pipelines now behave alike (the OpenCode adapter files its prompt the same way). The projected prompt is a content part like every other, so the 14-day content purge applies to it; the raw copy the plugin's span carries is not purged. The absence is stated rather than shown as a blank, which is what the honest-unobservability rule requires. | Owner decision, 2026-11-04: the separate consent step was removed in the same week — the Settings switch is the consent — and the owner asked for the live Factory data to be complete. Recorded in [ADR 0055](../../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md). |

## Closeout mapping

Links are written from this archive entry's location (`docs/archive/todo/`).

| ID | Destination |
|---|---|
| FPROMPT-1 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) states that the prompt reaches the session payload and what happens to a withheld one; [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#factory-droid-capture-the-plugin-path-and-the-file-path) records the same for the prompt and the content buckets; [ADR 0055](../../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md) records the decision and its 2026-11-04 amendment. |
| H1 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) (the prompt reaches the session payload); [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#factory-droid-capture-the-plugin-path-and-the-file-path) (the prompt and the content buckets). |
| H2 | [ADR 0055](../../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md) (the 2026-11-04 amendment). |
| H3 | [kyberdash-harness-collection.md](../../rules/kyberdash-harness-collection.md) (the capture rule's Factory row). |

## Design harvest

| ID | Durable item | Target component | Target doc type / location |
|---|---|---|---|
| H1 | The consented Factory prompt is filed as the session's `conversation_history` part, and a withheld prompt is declared `not_measurable` with a reason naming the consent | KyberDash | ARCHITECTURE `docs/dash/architecture.md` (Factory capture plugin pipeline); REFERENCE `docs/dash/telemetry-inventory.md` (Factory capture: the plugin path and the file path) |
| H2 | The decision to project the prompt, and the consent that decides whether it travels | KyberDash | ADR `docs/adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md` (2026-11-04 amendment) |
| H3 | The capture rule states that the Factory prompt reaches the session payload | KyberDash | RULE `docs/rules/kyberdash-harness-collection.md` |
