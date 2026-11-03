# Antigravity hook fixtures

Payloads for the task 16.3 hook-host contract tests
(`AntigravityHookAdapterTests`).

## Provenance

Built from **[F10] of the task packet** (`.arbiter-packet.md` in the repository
root): the `invoke_subagent` dispatch tool with `toolCall.args.Subagents` holding
`TypeName` and `Prompt` per dispatch, the `toolCall`, `stepIdx`,
`conversationId` and `workspacePaths` fields, and the `{decision, reason}` and
`{}` output shapes. The event name is carried as `hook_event_name`, which F10
does not document; the fixtures follow the sibling dialects.

## Files

| File | Covers |
|---|---|
| `pre-invoke-subagent.json` | Single-subagent `invoke_subagent` dispatch |
| `pre-invoke-subagent-multi.json` | Two-subagent `invoke_subagent` dispatch |
| `pre-non-dispatch.json` | Non-dispatch tool (`view_file`) |
| `post-invoke-subagent.json` | `invoke_subagent` return event |
