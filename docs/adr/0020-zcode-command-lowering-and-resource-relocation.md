---
id: adr/0020-zcode-command-lowering-and-resource-relocation
title: ZCode Lowers the Primary Agent to a Slash Command and Relocates Agent Resources
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-21
---

# ADR 0020: ZCode Lowers the Primary Agent to a Slash Command and Relocates Agent Resources

## Status

Accepted, 2026-09-21. Records the two ZCode-specific rendering decisions delivered with the
ZCode harness target. Does not change how any other target lowers agents or projects
resources.

## Context

ZCode is Z.ai's coding agent harness — a desktop application with an embedded headless CLI.
Facts below were verified against [`zai-org/ZCode`](https://github.com/zai-org/ZCode)
**3.14.0** (Apache-2.0) on 2026-09-21, not against the published documentation, which still
states that project-level subagents do not exist while `bootstrap/src/subagents.ts` loads them
and `services/src/subagents/subagentStorage.ts` exposes a workspace root the desktop settings
service reads and writes.

Two properties of ZCode have no analogue on the existing roster.

**It has three primitives, not two.** Subagents at `.zcode/agents/`, skills at
`.zcode/skills/`, and slash commands at `.zcode/commands/` — each available at both user and
project scope. No other target has a command primitive Squad can deploy into.

**Two of those three roots are scanned recursively.** `listMarkdownFiles` walks
`.zcode/agents/` to any depth, and `scanMarkdownFiles` walks `.zcode/commands/` to depth 12,
deriving a command's name from its relative path with separators mapped to `:`. The skill
scanner, by contrast, is one level deep and admits only a directory containing a `SKILL.md`.

Like Pi, ZCode has no primary-agent primitive: `ZCODE_AGENT_MODE_OPTIONS` is a closed set of
permission modes (`build`, `edit`, `plan`, `yolo`), not selectable agents. So the canonical
`conductor` cannot render as an agent. Unlike Pi, ZCode offers a third option for where it
should go instead.

Three canonical agents — `architect`, `conductor`, `task-reviewer` — carry Markdown resource
closures and link to them relatively, as `<agent-name>/references/<file>.md`.

## Decision

1. **The primary-invocation agent lowers to a slash command, not to a skill.** A
   `SquadInvocation.Primary` agent whose fallback profile declares `no-primary-agent: skill`
   renders at `.zcode/commands/<name>.md` and is invoked as `/<name>`. `omit` still emits
   nothing. The fallback profile's declared *outcome* — do not render this as an agent — is
   honoured; ZCode simply has a better primitive than a skill to honour it with, and one an
   operator invokes deliberately rather than relying on implicit skill matching. The lowering
   still records `role-skill-fallback` and `permission-not-expressible`, because a command's
   `allowed-tools` is not the canonical capability lattice and its `delegates-to` roster is
   instruction-only.

   Command frontmatter is kebab-case (`allowed-tools`, `argument-hint`, `description`,
   `disable-noninteractive`, `model`, `skills`), unlike an agent's camelCase, and a command's
   name comes from its path rather than its frontmatter. Only keys inside that set are
   emitted; anything else raises a `custom_command_unknown_frontmatter` warning. A canonical
   name that cannot satisfy `^[a-z0-9][a-z0-9_:-]{0,63}$` fails the render closed rather than
   deploying a command ZCode would reject at load.

2. **An agent's resource closure is projected under the skills tree, with its links
   rewritten.** Because `.zcode/agents/` and `.zcode/commands/` are scanned recursively, a
   closure beside its principal is not merely noisy but wrong:
   `.zcode/commands/conductor/references/plan-path.md` would register as a real command named
   `conductor:references:plan-path`, since a command with no frontmatter falls back to its
   first body line for a description. So for agents and the lowered command only, resources
   land at `.zcode/skills/<owner>/…` and the owner's authored links become `../skills/…` — a
   relative form identical under project and global scope, because all three directories sit
   one level below the same root. `.zcode/skills/<owner>/` without a `SKILL.md` is not a
   skill, so nothing phantom appears there either.

   The rewrite is driven by the declared resource closure, never by a pattern over prose: only
   a Markdown link target that exactly equals a declared resource's relative path is changed.
   A canonical skill already owning that directory is a fail-closed error, not a merge. Skill
   closures keep the ordinary beside-the-principal treatment.

3. **The deviation is recorded, not implied.** Each rewritten owner produces a
   `resource-links-rewritten` degradation, so the one place Squad modifies an instruction body
   is visible in the receipt rather than discoverable only by reading the renderer.

## Consequences

- ZCode is the only target that rewrites an instruction body, and the only one whose agent
  resources do not sit beside their principal. The architecture's
  "authored relative links resolve verbatim in the deployed tree" rule now carries this single
  named exception.
- `SquadRendererRegistry.AgentOutputPath` gains a third output shape for one target: an agent
  may claim a command path. The native/fallback validation rules are otherwise unchanged.
- A future ZCode release that stops scanning `.zcode/agents/` recursively would let the
  ordinary projection return. The renderer's remarks name the functions to re-check.
- Choosing the command primitive means the conductor is not implicitly matched the way a
  skill would be. That is deliberate: an orchestrator that seizes a turn by description match
  is worse than one an operator starts on purpose.

## Related

- [ADR 0019](0019-pi-native-subagents-and-primary-lowering.md) — the primary-agent lowering
  precedent this record follows in outcome and departs from in mechanism
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — §8 rendering
- [The ZCode harness plan](../plans/2026-09-21-zcode-harness-target.md) — the verified
  source-of-truth table behind these decisions
