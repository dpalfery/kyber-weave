---
id: archive/todo/opencode-plugin-per-message-tokens
title: Per-message OpenCode tokens are not sent by the capture plugin
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-31
status: superseded
---

# Per-message OpenCode tokens are not sent by the capture plugin

Superseded: the V2 capture plugin sends per-step usage. Each model step carries OpenCode's own
input, output, reasoning and cache counters, read from the `session.step.ended` bus event, so this
question no longer applies ([ADR 0046](../../adr/0046-opencode-capture-is-a-v2-only-plugin-pipeline.md)).
The shipped behaviour is in
[the architecture](../../dash/architecture.md#opencode-v2-capture-plugin-pipeline-kyberdash-opencode).

The KyberDash OpenCode capture plugin sent the request context only: identity, system prompt,
tool definitions, skills listing, and the context window. It sent no per-message token counts.
Token usage and cost reached KyberDash through OpenCode's own OpenTelemetry export (and the
third-party `@devtheops/opencode-plugin-otel` plugin where the owner ran it).

A future change could emit per-message token counts from the plugin through OpenCode's `event`
hook. That hook is not pinned for OpenCode 2.1.4 in the plugin's fixture, so the exact event
name and payload fields must be read from the installed runtime before any code is written.
