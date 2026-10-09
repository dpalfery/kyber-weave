# Copilot hook fixtures (VS Code Local schema)

Payloads for the task 4.2 hook-host contract tests
(`CopilotHookAdapterTests`, Local-schema rows).

## Provenance

Built from **[F2] of the task packet** (`.arbiter-packet.md` in the repository
root): the field names (`hook_event_name`, `tool_name`, `tool_input`,
`tool_use_id`), the `runSubagent` input fields (`prompt`, `description`,
`agentName?` with an absent `agentName` meaning the calling agent), and the
`PreToolUse` / `PostToolUse` output shapes (`hookSpecificOutput` deny/allow
with `updatedInput` as the complete input, top-level `decision: block`).

## Files

| File | Covers |
|---|---|
| `pre-runsubagent-dispatch.json` | `PreToolUse` `runSubagent` dispatch to an implementation specialist (strip path) |
| `pre-runsubagent-no-agentname.json` | `runSubagent` input without `agentName` (target is the calling agent) |
| `pre-tool-content-denied.json` | Non-dispatch tool naming a planning path (deny; the guard keys on content, not the tool name) |
| `pre-tool-content-allowed.json` | Non-dispatch tool outside planning paths (allow, empty stdout) |
| `post-runsubagent-completed.json` | `PostToolUse` `runSubagent` (return event, top-level block) |
