# Claude hook fixtures

Payloads for the task 4.1 hook-host contract tests
(`ArbiterHookHostTests`, `ClaudeHookAdapterTests`, `ArbiterReadGuardTests`).

## Provenance

Built from **[F1] of the task packet** (`.arbiter-packet.md` in the repository
root): the field names (`hook_event_name`, `tool_name`, `tool_input`,
`tool_use_id`, `agent_type`, `tool_response` with `status`/`agentId`/`content`),
the `Agent` input fields (`prompt`, `description`, `subagent_type`), the read
tool inputs (`Read.file_path`, `Bash.command`), and the `PreToolUse` /
`PostToolUse` output shapes (`hookSpecificOutput` deny/allow/`updatedInput`,
top-level `decision: block`, `additionalContext`).

## Files

| File | Covers |
|---|---|
| `pre-agent-dispatch.json` | `PreToolUse` `Agent` dispatch to an implementation specialist (strip path) |
| `pre-agent-no-subagent.json` | `Agent` input without `subagent_type` (pass-through) |
| `pre-read-denied.json` | `Read` of a planning path by a guarded caller (deny) |
| `pre-read-allowed.json` | `Read` outside planning paths (allow, empty stdout) |
| `pre-bash-denied.json` | `Bash` naming a planning path (deny, substring match) |
| `post-completed.json` | `PostToolUse` `Agent` with `status: completed` (return event) |
| `post-async-launched.json` | `PostToolUse` `Agent` with `status: async_launched` (launch link) |
| `pre-task-dispatch.json` | `Task` alias dispatch (same handling as `Agent`) |
| `pre-edit-passthrough.json` | Non-dispatch, non-guarded tool (pass-through) |
