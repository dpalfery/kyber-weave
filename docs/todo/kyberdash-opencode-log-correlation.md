---
id: todo/kyberdash-opencode-log-correlation
title: Correlate OpenCode's OTLP logs, which still quarantine
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-16
---

# Correlate OpenCode's OTLP logs, which still quarantine

The span side of OpenCode is done. `adapters/opencode.ts` recognises the OpenInference
vocabulary the deployed OpenCode (2.0.0) actually emits — `openinference.span.kind`,
`llm.*`, `input.value` — alongside the older Vercel AI SDK spellings, and maps
`opencode.llm` to `llm.invoke` and `Tool.execute` to `tool.invoke`.

The log side is not. OpenCode also posts to `/v1/logs`, and those rows still quarantine:

```
41  identified log remained unmatched past pending expiry
 5  could not correlate log to a canonical span
```

They carry a tool's outcome as a structured body — `body: "tool_result"` with `tool_name`,
`agent`, `success`, `duration_ms`, `tool_result_size_bytes` and `session.id`, under scope
`com.opencode`. Correlating them would let a tool span carry its own result and duration
instead of leaving them in `quarantined_logs`; bridging a late log to its span is what
`pending_logs` exists for.

Reproduce by running an OpenCode session against the receiver and reading
`quarantined_logs`; the reasons above come from exactly that, and the counts grow with every
session.
