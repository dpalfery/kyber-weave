# Copilot hook fixtures (CLI schema)

Payloads for the task 4.2 hook-host contract tests
(`CopilotHookAdapterTests`, CLI-schema rows).

## Provenance

Built from **[F3] of the task packet** (`.arbiter-packet.md` in the repository
root): the field names (`toolName`, `toolArgs` as a JSON string or an object,
`toolResult` with `resultType`/`textResultForLlm` on `postToolUse`), the `task`
arguments (`agent_type`, `prompt`, `description`), and the output shapes
(`permissionDecision` with `permissionDecisionReason`, `allow` with
`modifiedArgs` as the complete arguments, `additionalContext` post-dispatch).
The payload carries no tool-call id, so post events pair by `pair-digest`.

## Files

| File | Covers |
|---|---|
| `pre-task-object.json` | `task` dispatch with `toolArgs` as an object (strip path) |
| `pre-task-string.json` | `task` dispatch with `toolArgs` as a JSON string (same handling) |
| `pre-task-marked-no-target.json` | Marked dispatch with no `agent_type` (target fact absent, rules answer `undecidable`) |
| `pre-task-unmarked-no-target.json` | Unmarked dispatch with no `agent_type` (pass-through) |
| `post-task.json` | `task` return event: `toolResult`, no call id (`additionalContext`) |
