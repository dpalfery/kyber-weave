---
id: kyber-squad/copilot-prompt-runbook
title: Copilot system-prompt override runbook
doc-type: runbook
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-10
status: current
decided-by:
  - adr/0067-copilot-system-prompt-override-is-machine-wide
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
machine-wide is in [ADR 0067](../adr/0067-copilot-system-prompt-override-is-machine-wide.md).

## Live verification status

Verified on 2026-11-10 with VS Code Insiders 1.146.0-insider, a throwaway user-data directory and a
scratch home, using a branch build of the CLI. The owner was signed in to GitHub. The macOS keychain
prompt for the throwaway profile was denied, and sign-in still worked.

- **Install.** `squad copilot-prompt install --flavour insiders --yes` wrote the prompt file and the
  `*` entry. The window forwarded the value to `agent-host-config.json`.
- **Full chat, in a Kyber-Weave repository (Auto).** The model identified itself as the
  Kyber-Weave-configured assistant. It loaded the `task-gate` skill, ran a shell command, found
  deferred GitHub MCP tools through `tool_search_tool`, and gave a file link. Across five sessions,
  the host log showed `Resolved system message: mode=replace (content length 1648)` every time.
- **Log file name.** The host log is `agenthost.log`, the name the doctor's `agenthost*.log` pattern
  matches.
- **Doctor.** `kyber-weave doctor --check` passed `copilot.prompt.insiders.setting`,
  `copilot.prompt.insiders.file` and `copilot.prompt.insiders.log`.
- **Routing, in a Kyber-Weave repository.** The model read `.kyber-weave/kyber-weave.yml` and
  started with the conductor: it dispatched the conductor agent with the `task` tool, then architect.
- **Routing, in a plain repository.** The model looked for `AGENTS.md`,
  `.github/copilot-instructions.md` and `.kyber-weave/kyber-weave.yml`, found none, and acted as a
  general assistant with no conductor routing.
- **Hook events.** `hook.start` and `hook.end` events fired in every session.
- **Uninstall.** It removed the `*` entry and deleted the prompt file, and exited `0`. It leaves an
  empty `"chat.agentHost.copilot.modelCapabilityOverrides": {}` object in `settings.json`. That
  object is harmless.
- **HydraFusion Cascade: not observed.** The Auto session stayed on Auto's default `balance` tier and
  never engaged the Fusion tier, so no `session.fusion_resolved` event appeared. Cascade coverage
  is still an open limit, and no statement is made about the `repair` phase.

**Known interaction.** The conductor is a custom agent the person selects in the agent picker, not a
skill. In the live check, without the conductor selected, the model offered to dispatch the
conductor agent itself. The prompt now asks the person to switch to the conductor agent for
non-trivial work in a Kyber-Weave repository, instead of starting it. The agent-scoped deny hook
from PR 576, recorded as "The Copilot conductor holds tools a hook denies it", guards the conductor
beside the prompt.

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
  possible; see [ADR 0067](../adr/0067-copilot-system-prompt-override-is-machine-wide.md).
- **Copilot's safety text is gone for the top-level agent.** Kyber-Weave's prompt carries its own
  safety rules. Its wording is the one that is tested, not Copilot's.
- **Copilot's per-model tuning is gone for the top-level agent.** One prompt runs on every model,
  including under Auto.
- **Worker agents keep Copilot's prompt.** Only the top-level chat agent is replaced.
- **Routing is an instruction.** The model can ignore the conductor branch of the prompt. The
  agent-scoped deny hook from PR 576 stays in place beside the prompt to catch that.
- **HydraFusion Cascade coverage is not established.** The live check did not observe Cascade, so
  whether its `repair` phase carries the custom prompt is unknown. See the live verification status above.
- **The conductor must be selected.** The conductor is an agent the person selects in the agent
  picker. Without it selected, the prompt asks the person to switch rather than starting it.
- **Enterprise policy may block the setting.** This was not checked.
- **Out of scope.** Copilot CLI, remote and SSH VS Code sessions, the Copilot Chat extension's own
  agent mode, and `toolDescriptions` overrides.

## Retest after each VS Code update

After any VS Code update, restart VS Code, send one chat message, and run `kyber-weave doctor`.
The `.log` check must show `mode=replace`. If VS Code has dropped the setting, the override does
nothing, and doctor reports the failure. Run `kyber-weave squad copilot-prompt uninstall --yes` to
remove the stale entry and the prompt file.
