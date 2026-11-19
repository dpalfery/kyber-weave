# Antigravity hook fixtures

Payloads for the Antigravity hook-host contract tests
(`AntigravityHookAdapterTests`, `AntigravityRealPayloadTests`).

## Provenance

| Files | Source |
|---|---|
| `real-pre-default-agent.json`, `real-pre-conductor-marked.json` | Captured from agy (the Antigravity CLI), 2026-11-18, from the installed hook. Only the project path (`/repo`) and the home path (`/home/user`) were replaced; the rest is verbatim. |
| `pre-invoke-subagent.json`, `pre-invoke-subagent-multi.json`, `pre-non-dispatch.json`, `post-invoke-subagent.json` | Built from the task packet's Antigravity notes, not captured. They use the older `hook_event_name` and the `Subagents` list; the adapter still reads both. |

What the real capture shows that the packet did not: the event name is `hookEventName`; `toolCall.args` is one
flat `{TypeName, Prompt, Role}` object; the payload adds `cwd`, `artifactDirectoryPath`, `lastUserInput`,
`transcriptPath`, `modelName` and `toolCall.id`; and nothing names the calling agent.

## Files

| File | Covers |
|---|---|
| `real-pre-default-agent.json` | The standard Antigravity agent dispatching `research-agent`, no `KYBER-ARBITER` header |
| `real-pre-conductor-marked.json` | A dispatch carrying `KYBER-ARBITER: true` |
| `pre-invoke-subagent.json` | Single-element `Subagents` dispatch |
| `pre-invoke-subagent-multi.json` | Two-element `Subagents` dispatch, both marked |
| `pre-non-dispatch.json` | Non-dispatch tool (`view_file`) |
| `post-invoke-subagent.json` | `invoke_subagent` return event |
