# Claude Desktop OTLP fixtures

Sanitized Claude Code Desktop (`service.name = claude-code-desktop`) export shapes for
`docs/plans/2026-10-19-claude-desktop-request-body-correlation.md` (D7, T0).

- `traces.otlp.json` — two interactions. The first (trace `5a1e…0001`) holds the Desktop
  structure seen in a live store: the interaction span, three `claude_code.llm_request` children
  of it, a `claude_code.tool` → `claude_code.tool.execution` subtree whose child calls (sub-agent
  requests) start after that execution span has ended, and a second tool subtree (Bash) under
  the interaction. The second interaction (trace `5a1e…0002`) starts about 3 s after the first.
- `logs.otlp.json` — six `claude_code.api_request_body` logs:
  1. stamped with the interaction span, 2 ms after its first model call starts;
  2. stamped with the interaction span, 2 ms after the second model call starts;
  3. stamped with the `tool.execution` span, 1 ms after a sub-agent call starts; system-role
     message carries the skill-listing block (`The following skills are available …`);
  4. stamped with the `tool.execution` span, 1 ms after the next sub-agent call starts;
  5. context-free: no `traceId` or `spanId`, only `session.id`, 4 ms after the last model call;
  6. truncated: `body_truncated = "true"` and the text is cut mid-listing, so it does not parse.

Send `logs.otlp.json` before `traces.otlp.json` to reproduce early arrival: the sender emits the
body log at request start and exports the model-call span only when the call ends.

## Provenance and sanitization

Timing (relative offsets and durations) and message/block structure come from a read-only
snapshot of a live store. Every string that could carry user or project content was replaced:
prompts, system prompts, tool inputs and outputs, tool names other than `Bash`/`Task`,
skill names and descriptions, and host details. Identifiers (trace, span, session, prompt,
request body, tool use) are fresh values. Model name and token counts are kept as plain numbers.
No file paths, user names, or secrets appear. Tool definitions appear only on the sub-agent body,
as in the sender's output: main-thread bodies carry no `tools` array.
