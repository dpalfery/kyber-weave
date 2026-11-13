---
id: adr/0066-the-copilot-conductor-holds-tools-a-hook-denies-it
title: The Copilot conductor holds tools a hook denies it
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-10
component: KyberArbiter
decided-by: []
supersedes: []
code-refs:
  - ConductorGuard
---

# ADR 0066: The Copilot conductor holds tools a hook denies it

## Status

Accepted, 2026-11-10. Changes the Copilot case of [ADR 0045](0045-conductor-operator-channel-opt-in.md), which records that the conductor has no shell.

## Context

In VS Code, a worker keeps a built-in tool only if the selected agent also lists it. A conductor with no shell or edit tools therefore leaves its workers unable to work.

Listing those tools let the conductor use them itself. In one VS Code run, Copilot's HydraFusion review rejected the conductor's approval question as unfinished work and told a second model to complete the task. That model had edit and shell tools and built the feature. A run with the same handoff and no such tools stayed in the workflow.

VS Code's documentation says instructions do not guarantee compliance. Copilot's system prompt also tells agents to finish work themselves. The only way to replace that prompt is an application-wide evaluation setting, which would change every Copilot chat.

## Decision

When the Arbiter is enabled, the Copilot conductor is rendered with `search`, `execute`, `edit` and `web` for its workers, plus an agent-scoped hook running `kyber-weave-arbiter guard`.

- `preToolUse` denies everything except delegation, bookkeeping (its own todo list included), the docs tools, reading contract files (its references and skills, `AGENTS.md`, and any file under the planning directories, such as a plan's task file), and exact `kyber-weave run start` and `kyber-weave doctor --ensure` commands.
- `userPromptTransformed` appends an explanation of the workflow to every model-facing message, including HydraFusion handoffs. It says why general single-agent defaults do not apply.

Without the Arbiter, the conductor keeps its narrow tool list, because nothing would enforce the boundary.

## Alternatives considered

- **Start the conductor as a skill.** Rejected: a skill-started conductor is not a selected agent, so an agent-scoped hook cannot apply and every plain chat looks the same to a global hook.
- **Instructions only.** Rejected: that is what failed.
- **Replace the system prompt with `chat.agentHost.copilot.modelCapabilityOverrides`.** Rejected: it applies to every chat on the machine, removes Copilot's safety text, and must be rebuilt per model.

## Consequences

- Copilot runs a hook on each conductor tool call. A timeout, empty output or invalid JSON lets the tool run, so the guard answers every denial explicitly and denies events it cannot read.
- A tool Copilot adds later is denied until it is added to the guard's list, and the denial names it.
- HydraFusion still rejects approval questions, so conductor runs should use a named model or the Balance setting for Auto.
