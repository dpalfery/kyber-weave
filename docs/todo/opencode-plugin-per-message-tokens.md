---
id: todo/opencode-plugin-per-message-tokens
title: Per-message OpenCode tokens are not sent by the capture plugin
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-26
---

# Per-message OpenCode tokens are not sent by the capture plugin

The KyberDash OpenCode capture plugin sends the request context only: identity, system prompt,
tool definitions, skills listing, and the context window. It sends no per-message token counts.
Token usage and cost reach KyberDash through OpenCode's own OpenTelemetry export (and the
third-party `@devtheops/opencode-plugin-otel` plugin where the owner runs it), so tokens are
counted once and the context record carries no token counters.

A future change could emit per-message token counts from the plugin through OpenCode's `event`
hook. That hook is not pinned for OpenCode 2.1.4 in the plugin's fixture, so the exact event
name and payload fields must be read from the installed runtime before any code is written.
Until then, tokens stay on the OpenTelemetry path.

Open questions for that change: which event carries per-message usage in 2.1.4; whether it
duplicates the OpenTelemetry counters; and how a double count is prevented once both paths run.

The shipped boundary is described in [dash/architecture.md](../dash/architecture.md), section
"OpenCode plugin span/turn envelope". The decision that set the boundary is in the archived plan
[2026-10-25-opencode-capture-deploy-kyberdash](../archive/plans/2026-10-25-opencode-capture-deploy-kyberdash.md)
(non-goal "No per-message token capture through the plugin").
