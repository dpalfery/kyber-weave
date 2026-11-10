---
id: adr/0066-copilot-system-prompt-override-is-machine-wide
title: The Copilot system-prompt override is an opt-in, machine-wide user setting
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-10
component: KyberSquad
decided-by: []
supersedes: []
code-refs:
  - CopilotPromptInstallCommand
  - CopilotOverrideSettings
  - VsCodeUserSettingsPaths
  - CopilotPromptFile
---

# ADR 0066: The Copilot system-prompt override is an opt-in, machine-wide user setting

## Status

Accepted, 2026-11-10. The owner approved the decision with the words "Yes, machine-wide".

The live check (a signed-in VS Code chat, and HydraFusion Cascade coverage) is not yet run.
This record states the decision. It does not claim the live result.

## Context

GitHub Copilot's top-level chat agent in VS Code starts from a default system prompt of about
22,000 characters, larger with host additions. That prompt pushes the top-level agent to do the
work itself rather than route it to Kyber-Weave's conductor. Kyber-Weave already has two guards
for this: a `preToolUse` hook that blocks a rogue top-level agent, and starting work from a skill.
Neither changes the prompt.

VS Code has an internal user setting, `chat.agentHost.copilot.modelCapabilityOverrides`. A `*`
entry in it with a `promptOverrideFile` replaces the top-level system prompt. The facts that
bear on where that setting can live, from VS Code's source at commit `6bd30dab` and from a
live test on VS Code Insiders 1.146.0:

- The setting is declared with application scope and the tags `experimental` and `advanced`.
- VS Code drops application-scope keys from `.vscode/settings.json` and `.code-workspace`
  files. Folder and workspace settings never reach the model the window reads.
- One Agent Host utility process serves every VS Code window. Its root configuration is a single
  object, so a value cannot differ between windows.
- A live test showed a value set only in a project's `.vscode/settings.json` reaches the host as
  `{}`. The same value in user settings reached the host.
- VS Code takes the prompt path as an absolute file path. It does not resolve `${workspaceFolder}`,
  `~`, or the session's working directory.

So the setting can only be set machine-wide, in the default profile's user `settings.json`.

## Decision

Kyber-Weave offers an opt-in command family, `kyber-weave squad copilot-prompt install`,
`uninstall` and `status`. Install writes Kyber-Weave's prompt to
`~/.kyber-weave/copilot/system-prompt.yaml` and adds one `*` entry, `{"promptOverrideFile": <that path>}`,
to `chat.agentHost.copilot.modelCapabilityOverrides` in the default-profile `User/settings.json` of
each detected VS Code build (stable and Insiders). The override is machine-wide, so it applies to
every Copilot chat on the machine.

The prompt tells the model to follow Kyber-Weave's conductor routing only when the open workspace
holds `.kyber-weave/kyber-weave.yml`. In any other workspace it acts as a general coding assistant.

The opt-in state is the prompt file together with Kyber-Weave's own `*` entry. No receipt is
written. A `*` entry that names another file is refused and never overwritten. Doctor adds its
`copilot.prompt.*` checks only while that state is present.

## Alternatives considered

- **Per-project activation through a separate VS Code `--user-data-dir` for each project.** This is
  real isolation, and it works, because each user-data directory gets its own application
  configuration and its own Agent Host. Rejected: each one needs its own sign-in, its own
  extensions and a launch command that every person must remember. Not tested in this change.
- **Per-project activation through `.vscode/settings.json` or a `.code-workspace` file.** Not
  possible. VS Code drops the key from those files, and the live test confirmed the value reaches
  the host as `{}`.
- **Dropping the feature.** Rejected by the owner. The accepted costs are listed under Consequences.
- **A flag on `squad install`.** Rejected. `squad install` is project-scoped and writes a receipt.
  This change edits a per-user file that Kyber-Weave does not own, so it must never be a side
  effect of a project install. There is also no top-level `kyber-weave install` to hang it on.

## Consequences

- Every Copilot chat on the machine loses Copilot's own top-level prompt. That includes chats in
  repositories that have nothing to do with Kyber-Weave, and it lasts until `uninstall` runs.
- Kyber-Weave's prompt carries its own safety rules, because Copilot's safety text does not reach
  the top-level agent. Copilot's per-model tuning does not reach it either: one prompt runs on every
  model, including under Auto. Worker agents keep Copilot's prompt.
- The setting is labelled experimental, and its predecessor was removed in VS Code 1.144. VS Code
  may change or remove it. Doctor reports the silent fallback, and each VS Code update needs a
  retest. That retest is documented in the Kyber-Squad onboarding guide.
- Routing is an instruction to the model, and a model can ignore it. The `preToolUse` hook and the
  skill start therefore stay as they are.
- HydraFusion Cascade coverage is not established. Whether Cascade's repair phase carries the custom
  prompt is recorded only by the live check, which is pending.
- The state is two files with no receipt. Deleting the prompt file by hand without removing the
  `*` entry leaves VS Code pointing at a missing file, which falls back silently to Copilot's default
  prompt. Doctor is the only detector.
- Settings edits are byte splices that keep comments, key order and trailing commas. A foreign entry
  is refused, so a person's own override is never destroyed.
- Any future change to how Kyber-Weave routes top-level Copilot chats must be made in the prompt
  file and tested in a live window, because the unit tests cannot reach VS Code.
