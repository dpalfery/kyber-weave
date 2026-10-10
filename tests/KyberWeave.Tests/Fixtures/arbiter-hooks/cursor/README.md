# Cursor hook fixtures

Payloads for the task 12.5 hook-host contract tests
(`CursorHookAdapterTests`).

## Provenance

Built from **[F6] of the task packet** (`.arbiter-packet.md` in the repository
root): the field names (`tool_name`, `tool_input`, `tool_use_id`, `cwd`, plus
`conversation_id` and `hook_event_name`), the `Task` dispatch tool with the
prompt at `tool_input.prompt` and the undocumented target at
`tool_input.subagent_type`, pairing by `tool_use_id`, and the output shapes
(`permission` deny with `agent_message`/`user_message`, `allow` with the
complete `updated_input`, post-dispatch `additional_context`).

## Files

| File | Covers |
|---|---|
| `pre-task-dispatch.json` | `Task` dispatch (strip path) |
| `pre-task-no-target.json` | `Task` dispatch with no `subagent_type` (target fact absent) |
| `pre-non-dispatch.json` | Non-dispatch tool (pass-through) |
| `post-task-completed.json` | `Task` return event (`additional_context`) |
