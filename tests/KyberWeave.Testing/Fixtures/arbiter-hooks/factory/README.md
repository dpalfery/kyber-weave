# Factory hook fixtures

Payloads for the task 16.5 hook-host contract tests
(`FactoryHookAdapterTests`).

## Provenance

Built from **[F11] of the task packet** (`.arbiter-packet.md` in the
repository root), with the dispatch facts from design §9.1: the field names
(`session_id`, `transcript_path`, `cwd`, `permission_mode`,
`hook_event_name`, `tool_name`, `tool_input`, plus `tool_response` on
`PostToolUse`), the `Task` dispatch tool with the prompt at
`tool_input.prompt` and the target at `tool_input.subagent_type`, **no call
id** (Factory documents none, so return pairing uses `pair-digest` at the
ledger layer), and the Claude-format output shapes
(`hookSpecificOutput.permissionDecision` deny with
`permissionDecisionReason`, `allow` with the complete `updatedInput`,
post-dispatch `hookSpecificOutput.additionalContext`).

## Files

| File | Covers |
|---|---|
| `pre-task-dispatch.json` | `Task` dispatch (deny and strip paths) |
| `pre-task-no-target.json` | `Task` dispatch with no `subagent_type` (pass-through, design §1.7) |
| `pre-non-dispatch.json` | Non-dispatch tool (pass-through; plan reads stay advisory) |
| `post-task-completed.json` | `Task` return event (`additionalContext`) |
