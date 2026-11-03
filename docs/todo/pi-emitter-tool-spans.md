---
id: todo/pi-emitter-tool-spans
title: Pi's collector emits no tool spans, and caps the conversation at 262144 characters
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-16
---

# Pi's collector emits no tool spans, and caps the conversation at 262144 characters

KyberDash now derives `tool.invoke` records for Pi from the tool calls carried inline in the
turn span, so Pi tool usage is visible under `harness=pi` instead of being a harness with
`llm.invoke` records and nothing else. Two gaps remain, both on the emitter side, and
neither is closable from this repository:

- **No tool spans.** Pi's OTLP exporter is the external package
  `@dpalfery/pi-statusline`, which emits exactly one span per turn (`pi.llm.request`). A
  search of that package for tool-span emission finds nothing, so per-call timing and a
  call's own success are inferred rather than reported. One span per call — the shape
  `Tool.execute` gives OpenCode — would let the derived records retire.
- **The conversation is capped at 262144 characters.** Past that, `gen_ai.input.messages` is
  truncated and the calls beyond the cut are unrecoverable, so the derived count is stamped
  a lower bound and never a total. Raising the cap only moves the boundary; the durable fix
  is the per-call span above.

The in-repo surface is `capture/harnesses/pi.ts`, which is status-only by design: it reports
which extension is loaded and writes nothing. `products/kyber-utilities/statusline/pi/statusbar.ts`
is a standalone statusline whose manifest records "zero imports, no OTLP", so it is not the
place either. Decide whether the emitter should be brought in-repo; if it should, that
decision wants an ADR, because the boundary is currently deliberate and documented.
