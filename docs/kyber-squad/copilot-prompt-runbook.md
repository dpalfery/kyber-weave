---
id: kyber-squad/copilot-prompt-runbook
title: Copilot system-prompt override runbook
doc-type: runbook
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-10
status: current
decided-by:
  - adr/0066-copilot-system-prompt-override-is-machine-wide
code-refs:
  - CopilotPromptProbe
  - CopilotPromptFile
  - CopilotOverrideSettings
---

# Copilot system-prompt override runbook

This runbook covers the opt-in Copilot system-prompt override in VS Code: what the doctor checks
mean, how to tell the override is live, what happens when it silently falls back, and what it
does not cover. How to turn it on and off is in the
[Kyber-Squad adoption guide](onboarding.md#copilot-system-prompt-override-opt-in). Why it is
machine-wide is in [ADR 0066](../adr/0066-copilot-system-prompt-override-is-machine-wide.md).

## Live verification status

**Not yet verified.** A real signed-in VS Code chat has not been run with the override, so the
following are unproven:

- That a new chat reports the Kyber-Weave identity and loads a skill, runs a shell, finds a
  deferred tool through `tool_search_tool`, and opens a file link.
- That a plain chat gets general help, and that a chat in a repository holding
  `.kyber-weave/kyber-weave.yml` routes non-trivial work to the conductor.
- Whether HydraFusion Cascade carries the custom prompt. Cascade is one fusion pattern under
  Auto. Until a live run records it, Cascade coverage is unknown.
- The exact name of the AgentHost log file. The name used below is a search pattern that matches
  it. The live check confirms the name.

Until the live check passes, treat the override as unproven on a real machine.

## Doctor checks

`kyber-weave doctor` adds these checks only when the prompt file is installed. Without the file it
adds nothing, so a machine that never opted in sees no new output.

| Check | Passes when | Fails when | Fix |
|---|---|---|---|
| `copilot.prompt.setting` | Some detected VS Code settings file points its `*` entry at the installed prompt file. | No detected settings file points at it. Reported once, without a build label. | `kyber-weave squad copilot-prompt install --yes` |
| `copilot.prompt.<stable\|insiders>.setting` | That build's `*` entry names the installed prompt file. | Not reported for a build that does not point at it. | As above |
| `copilot.prompt.<stable\|insiders>.file` | The prompt file is readable YAML with exactly one document, a non-empty `systemPrompt`, and no key other than `systemPrompt` and `toolDescriptions`. | The file is unreadable, not YAML, has another key, or has no usable `systemPrompt`. The reason names the problem. | As above |
| `copilot.prompt.<stable\|insiders>.log` | The newest AgentHost log's last `Resolved system message` line shows `mode=replace`. | The last line shows another mode. The reason says to restart VS Code and run doctor again. | `kyber-weave doctor` |

The `.log` check is inconclusive, not failed, when there is no log folder yet, or the newest log has
no `Resolved system message` line. It then passes with a `warning:` reason. Restart VS Code, send a
chat message, and run doctor again.

Under `kyber-weave doctor --ensure`, every failure is reported as a warning. The check passes with
a `warning:` reason, so a fault in the machine-wide prompt does not stop an unrelated run.

Without `--ensure`, a failed check makes doctor exit `1`.

## The live-mode log line

VS Code logs the mode it actually applied for each session:

```text
[Copilot:<session>] Resolved system message: mode=replace (content length N)
```

`mode=replace` is the only proof that the override is live. The settings file can name the right
prompt file while VS Code ignores it. The doctor reads the last such line in the newest log
folder: `<user-data>/logs/<newest timestamp folder>/`. It searches that folder, at any depth, for
files named `agenthost*.log`, and skips links.

A `mode` other than `replace` means the default prompt is in use for that session, even though the
settings are right.

## Silent fallback

VS Code does not report a bad prompt file to the chat. If the file is missing, cannot be read, or
does not parse, VS Code uses Copilot's default prompt for every session. The only trace is a
warning in the AgentHost log, once, with one of these messages:

- `[PromptOverride] Failed to read prompt override file`
- `[PromptOverride] Failed to parse prompt override`

So a broken install looks like a working one in the chat. Use doctor to find it. The file check and
the log check together catch both kinds of fault. The file check parses with YamlDotNet, which can
accept a file VS Code rejects. The log check is the authority on real machines.

## Known limits

- **Experimental setting.** `chat.agentHost.copilot.modelCapabilityOverrides` is labelled
  `experimental` and `advanced`. VS Code may change or remove it in any release.
- **Machine-wide.** Every Copilot chat on the machine uses the prompt, including chats in
  repositories that do not use Kyber-Weave, until `uninstall` runs. Per-project activation is not
  possible; see [ADR 0066](../adr/0066-copilot-system-prompt-override-is-machine-wide.md).
- **Copilot's safety text is gone for the top-level agent.** Kyber-Weave's prompt carries its own
  safety rules. Its wording is the one that is tested, not Copilot's.
- **Copilot's per-model tuning is gone for the top-level agent.** One prompt runs on every model,
  including under Auto.
- **Worker agents keep Copilot's prompt.** Only the top-level chat agent is replaced.
- **Routing is an instruction.** The model can ignore the conductor branch of the prompt. The
  `preToolUse` hook and the skill start stay in place to catch that.
- **HydraFusion Cascade coverage is unverified.** See the live verification status above.
- **Enterprise policy may block the setting.** This was not checked.
- **Out of scope.** Copilot CLI, remote and SSH VS Code sessions, the Copilot Chat extension's own
  agent mode, and `toolDescriptions` overrides.

## Retest after each VS Code update

After any VS Code update, restart VS Code, send one chat message, and run `kyber-weave doctor`.
The `.log` check must show `mode=replace`. If VS Code has dropped the setting, the override does
nothing, and doctor reports the failure. Run `kyber-weave squad copilot-prompt uninstall --yes` to
remove the stale entry and the prompt file.
