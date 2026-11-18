# Codex hook fixtures

Payloads for the task 12.3 hook-host contract tests
(`CodexHookAdapterTests`).

## Provenance

Built from **[F7] of the task packet** (`.arbiter-packet.md` in the repository
root): the field names (`session_id`, `transcript_path`, `cwd`,
`hook_event_name`, `model`, `turn_id`, `tool_name`, `tool_use_id`,
`tool_input`, plus `tool_response` on `PostToolUse`), the `spawn_agent`
arguments (`message`, `agent_type`, plus `task_name` under MultiAgentV2), the
dispatch matcher `^(Agent|(.*[._:/])?spawn_agent)$` (so `spawn_agent` also
matches `Agent`), and the output shapes (`hookSpecificOutput` with
`permissionDecision` deny/reason, `allow` with `updatedInput` as the complete
arguments, post-dispatch `hookSpecificOutput.additionalContext` instead of
`decision: block`).

## Files

| File | Covers |
|---|---|
| `pre-spawn-dispatch.json` | `spawn_agent` dispatch (strip path) |
| `pre-agent-alias.json` | `Agent` alias dispatch (same handling) |
| `pre-non-dispatch.json` | Non-dispatch tool (pass-through) |
| `pre-spawn-no-target.json` | Dispatch tool with no `agent_type` (pass-through) |
| `post-spawn-completed.json` | `spawn_agent` return event (`additionalContext`) |
