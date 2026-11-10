---
id: adr/0066-copilot-parent-roles-start-as-skills
title: Copilot's Parent Roles Start as Skills, and Their Agent Files Are Hidden from the Picker
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-10
component: KyberSquad
code-refs:
  - CopilotRenderer
decided-by: []
supersedes: []
---

# ADR 0066: Copilot's Parent Roles Start as Skills, and Their Agent Files Are Hidden from the Picker

## Status

Accepted, 2026-11-10. Decides how the three Copilot parent roles (`conductor`, `bug-crusher`
and `product-owner`) start in VS Code. Builds on the entry-point skill pattern of
[ADR 0034](0034-factory-droid-entry-point-skill.md) and
[ADR 0036](0036-primary-agent-canonical-skill-entry-point.md), and changes the operator-channel
and Arbiter consequences recorded in [ADR 0045](0045-conductor-operator-channel-opt-in.md).

## Context

VS Code gives a worker only the built-in tools that the selected custom agent lists. This was
introduced by a `microsoft/vscode` change merged on 2026-10-18. The Kyber-Squad `conductor` agent
lists no shell or edit tool, by design, so a worker it starts in an ordinary chat can only read
files. The run then stops with `STATUS: TOOL_FAILURE no shell`.

VS Code, the Copilot CLI and the GitHub Copilot app all read the same user agent folder,
`~/.copilot/agents`. A change made only for VS Code therefore reaches the other two as well.

Before the change, the parents were selectable in the VS Code agent dropdown, and a worker started
from a parent could not reach a shell. A worker started from a skill in an unselected chat kept its
shell and edit tools, which is what the fix relies on.

## Decision

1. **The three parent roles also start as skills.** Each parent renders an entry-point skill at
   `.github/skills/<name>/SKILL.md`, so a person types `/conductor`, `/bug-crusher` or
   `/product-owner` in normal chat. The skill is the agent body verbatim, with its resources beside
   it, through the same entry-point projection Claude, Factory and Command Code use. `bug-crusher`
   and `product-owner` reuse their canonical skills, so no second copy is written.
2. **The agent files stay and are hidden from the picker.** Every parent agent carries
   `user-invocable: false`, so it does not appear in the VS Code agent dropdown. A parent that no
   agent names in its delegation roster also carries `disable-model-invocation: true`, so the model
   cannot start it on its own. In this decision that is `conductor` and `bug-crusher`.
   `product-owner` is named in the conductor's roster, so it stays a dispatchable hidden agent.
3. **Workers do not change.** Every worker agent stays an agent with `user-invocable: false` and
   no `disable-model-invocation` line.
4. **The operator channel overlay stays on the agent file only.** `copilot --agent conductor` keeps
   it. The conductor skill does not carry it, because a skill has no tool list.
5. **The skill's limits are recorded on the skill.** Each parent records `role-skill-fallback`,
   which says why VS Code forces the skill. It records `permission-not-expressible`, which says the
   session's tools, roster and model apply to the skill rather than to the canonical profile. When
   the Arbiter is on, it records `arbiter-not-enforced` with the reason `skill-entry-no-caller`.
6. **The branch that shipped is A.** A spike chose it. `copilot --agent` runs an agent marked
   `user-invocable: false`, and a same-name agent and skill coexist in the VS Code runtime. Both
   checks passed, so all three agent files stay hidden and the skills are added. The
   `disable-model-invocation` key was kept, because it did not break `--agent`.

The records and the rendered output are pinned by `CopilotRendererTests`, and the Arbiter's
handling of the dispatches is pinned by `CopilotHookAdapterTests`.

## Alternatives considered

- **A VS Code-only agent location, through the `chat.agentFilesLocations` setting.** Rejected.
  The setting lives in each user's editor, so the product would be relying on a per-user
  configuration it does not own. It would also split VS Code from the CLI and the app, which share
  the same folder today.
- **Adding shell and edit tools to the conductor's agent file.** Rejected. It would give the
  orchestrator a capability its profile denies (`process.execute: deny`), so the fix would widen the
  conductor's role on every harness to repair one. The conductor's own text already says to run the
  shell itself only where the harness gives it one.
- **Removing the parent agent files from the Copilot render altogether.** Not chosen for the
  shipped branch, because the spike showed hiding is enough. It stays the fallback if a future
  VS Code change stops honouring `user-invocable: false`. It would also end the enforced
  `copilot --agent conductor` route for CLI users, which the owner had not agreed to lose.

## Consequences

- **The no-shell limit is instruction-only in VS Code.** A conductor started as a skill runs in the
  chat session with that session's shell. Its own text is the only limit there. This is an accepted
  loss of the hard limit on that route, recorded against
  [ADR 0045](0045-conductor-operator-channel-opt-in.md) and in the
  [Kyber-Squad requirements](../kyber-squad/requirements.md#non-broadening-guarantee).
- **The model pin is lost for `bug-crusher` and `product-owner` when they start as skills.** The
  session's model applies. The conductor has no pinned model to lose.
- **The Arbiter gates a skill-run conductor only through the owned hook file.** A skill carries no
  caller identity, so the dispatch gate reads only the file-level hook. It needs `chat.useHooks` on
  in a trusted workspace. This is recorded per parent and is weaker than the agent path.
- **VS Code may load a parent's skill from its description.** This is accepted, as Claude and
  Factory accept the same behaviour. `disable-model-invocation` on the skill is the one-line
  follow-up if it proves noisy.
- **Workers keep their tools and their agent files.** Nothing about a worker's rendered output
  changes.

## Related

- [ADR 0034: Factory Keeps the Conductor Droid and Adds an Entry-Point Skill](0034-factory-droid-entry-point-skill.md)
- [ADR 0036: A Primary Agent Sharing a Canonical Skill Identity Reuses the Skill as Its Entry Point](0036-primary-agent-canonical-skill-entry-point.md)
- [ADR 0045: Conductor Operator Channel as an Opt-In Render-Time Overlay](0045-conductor-operator-channel-opt-in.md)
- [Kyber-Squad architecture: role-skill lowering](../kyber-squad/architecture.md#3-role-skill-lowering-and-namespace-resolution)
- [Kyber Arbiter architecture: harness facts](../kyber-arbiter/architecture.md#harness-facts)
