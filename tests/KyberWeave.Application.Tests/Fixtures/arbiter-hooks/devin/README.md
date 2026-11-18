# Devin hook fixtures

Payloads for the task 16.7 hook-host contract tests
(`DevinHookAdapterTests`).

## Provenance

Built from **[F12] of the task packet** (`.arbiter-packet.md` in the
repository root), with the dispatch facts from design §9.1: the field names
(`session_id`, `prompt_id`, `tool_name`, `tool_input`, plus `tool_response`
with `success`/`output`/`error` on `PostToolUse`, in the Claude Code format
that `.devin/hooks.v1.json` uses), the `run_subagent` dispatch tool with the
target at `tool_input.profile` (the vendor documents only that the tool
"takes a profile") and the prompt at `tool_input.prompt`, **no call id**
(the vendor documents none, so return pairing uses `pair-digest` at the
ledger layer), and the output shapes from [F12]: top-level
`{"decision":"block","reason":…}` to deny, `hookSpecificOutput.updatedInput`
merged into the arguments to strip, and no documented `PostToolUse` output
field.

## Files

| File | Covers |
|---|---|
| `pre-run-subagent-dispatch.json` | `run_subagent` dispatch (deny and merge-strip paths) |
| `pre-run-subagent-no-profile.json` | `run_subagent` dispatch with no `profile` (unmarked, still classified) |
| `pre-run-subagent-no-prompt.json` | `run_subagent` dispatch with no `prompt` (unmarked, still classified) |
| `pre-non-dispatch.json` | Non-dispatch tool (pass-through; plan reads stay advisory) |
| `post-run-subagent-completed.json` | `run_subagent` return event (reads `tool_response.output`, writes nothing) |
