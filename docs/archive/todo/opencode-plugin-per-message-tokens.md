---
id: archive/todo/opencode-plugin-per-message-tokens
title: Per-message OpenCode tokens are not sent by the capture plugin
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-31
status: superseded
documentation-impact: no-design-change
---

# Per-message OpenCode tokens are not sent by the capture plugin

Superseded: the V2 capture plugin sends per-step usage. Each model step carries OpenCode's own
input, output, reasoning and cache counters, read from the `session.step.ended` bus event, so this
question no longer applies ([ADR 0048](../../adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline.md)).
The shipped behaviour is in
[the architecture](../../dash/architecture.md#opencode-v2-capture-plugin-pipeline-kyberdash-opencode).

The KyberDash OpenCode capture plugin sent the request context only: identity, system prompt,
tool definitions, skills listing, and the context window. It sent no per-message token counts.
Token usage and cost reached KyberDash through OpenCode's own OpenTelemetry export (and the
third-party `@devtheops/opencode-plugin-otel` plugin where the owner ran it).

A future change could emit per-message token counts from the plugin through OpenCode's `event`
hook. That hook is not pinned for OpenCode 2.1.4 in the plugin's fixture, so the exact event
name and payload fields must be read from the installed runtime before any code is written.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| OCTOK-1 | Should the capture plugin send per-message token counts? | Yes, and the V2 plugin now does: every model step carries OpenCode's own input, output, reasoning and cache counters, read from the `session.step.ended` bus event, plus reported cost. | A turn is one model step (`session.step.started` to `session.step.ended`), so per-step usage is the per-message usage this todo asked for. The plugin no longer depends on OpenCode's native OpenTelemetry export for usage. | Plan [2026-10-30-opencode-v2-capture](../plans/2026-10-30-opencode-v2-capture.md) decisions D2 and D4, approved in AP1 on 2026-10-30. |

## Closeout mapping

Links are written from this archive entry's location (`docs/archive/todo/`).

| ID | Destination |
|---|---|
| OCTOK-1 | [dash/architecture.md](../../dash/architecture.md#opencode-v2-capture-plugin-pipeline-kyberdash-opencode) describes the per-step usage the plugin sends; [ADR 0048](../../adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline.md) records the decision. |

## Design harvest

Documentation waiver: no new design survives from this todo. Per-step usage is delivered by the V2
pipeline and documented in the architecture section above, so no governed document gains a
statement beyond what that change already added.
