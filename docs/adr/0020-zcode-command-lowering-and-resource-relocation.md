---
id: adr/0020-zcode-command-lowering-and-resource-relocation
title: ZCode Command Lowering, Resource Relocation, and the Inverted Empty Tool List
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-21
---

# ADR 0020: ZCode Command Lowering, Resource Relocation, and the Inverted Empty Tool List

## Status

Accepted, 2026-09-21. Records the ZCode-specific rendering decisions delivered with the
ZCode harness target. Does not change how any other target lowers agents, projects resources,
or emits tool lists.

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

**And its empty tool list means the opposite of every other target's.** `resolveAllowedTools`
reduces both a missing `tools` key and an explicit `tools: []` to an empty
`request.allowedTools`; `resolveSubagentToolAllowlist` then reads that as
`inheritsAvailableTools` and hands the child every tool the parent has, while
`shouldBorrowParentMcp` additionally lends it the parent's MCP servers. On Claude, Factory and
Pi the empty or absent list is the shape a renderer must avoid *because omitting it* widens;
on ZCode the explicit empty list widens just as far.

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

4. **A resolved grant of nothing fails the render closed.** Because `tools: []` is ZCode's
   inherit-everything signal, it is the one value this renderer must never emit. The list
   always carries at least the ungoverned base — `TodoWrite` and `Skill`, matching
   `ClaudeRenderer` — and an empty resolution raises
   `SquadRenderValidationException` rather than producing the literal that would widen.
   `Skill` is not a convenience: it is what makes the 24 skills this renderer deploys
   reachable, and withholding it would ship a skill tree no agent could open.

5. **No MCP is emitted, and the gap is recorded per principal.** `registerMcpTools` admits a
   tool only on an exact name match against the allow-list, so `ClaudeRenderer`'s
   `mcp__<server>__*` selector form registers nothing here and would only flip
   `shouldBorrowParentMcp` to true while granting no tool. A concrete `mcp__<server>__<tool>`
   name would instead become a hard requirement that fails the agent closed wherever that
   server is not connected, and the `mcpServers` key throws
   `Required MCP server is not connected` the same way. Every principal therefore records
   `permission-not-expressible` naming the canonical servers from `mcp.json`. Because an
   MCP-free non-empty tool list leaves `shouldBorrowParentMcp` false, nothing is inherited
   either — the omission narrows rather than leaks.

6. **Skill descriptions use a folded block scalar; agent and command descriptions cannot.**
   The skill adapter supports block scalars and the desktop skill service parses skill
   frontmatter with a real YAML parser, so a skill description needs no quoting and carries an
   embedded `"` intact. The agent reader cannot: a source comment in ZCode's own skill adapter
   records that the agent side reads only the top-level `description: >` and skips the
   indented continuation. Two emission styles in one renderer is the price of removing the
   escape artifact from the only place it actually occurs.

## Consequences

- ZCode is the only target that rewrites an instruction body, and the only one whose agent
  resources do not sit beside their principal. The architecture's
  "authored relative links resolve verbatim in the deployed tree" rule now carries this single
  named exception.
- `SquadGlobalRoots` gains an optional file-reading port, because ZCode is the only target
  whose global root can be set in a config file rather than an environment variable. The port
  is supplied by the composition root, and a null reader leaves every existing two-argument
  construction unchanged.
- Squad agents on ZCode reach no MCP server. That is a real capability difference from the
  Claude rendering, and it is recorded on every principal rather than left to be discovered.
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
