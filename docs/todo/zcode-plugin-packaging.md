---
id: todo/zcode-plugin-packaging
title: Package Kyber-Squad as a ZCode plugin
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-21
status: draft
---

# Package Kyber-Squad as a ZCode plugin

`ZCodeRenderer` deploys every ZCode primitive Squad has canonical source for — subagents,
skills, and the lowered conductor command — at both project and global scope. ZCode also
supports a *plugin* distribution channel, which this page is the context for. It is not
remaining work from the ZCode target; it is a **different deployment model**, and adopting it
is a decision about Kyber-Squad, not about ZCode.

Facts verified against [`zai-org/ZCode`](https://github.com/zai-org/ZCode) **3.14.0**
(Apache-2.0) on 2026-09-21.

## What a plugin is

A manifest looked up in priority order — `.zcode-plugin/plugin.json`, then
`.claude-plugin/plugin.json`, then `.codex-plugin/plugin.json`
(`adapters/src/skills/index.ts`, `PLUGIN_MANIFEST_RELATIVE_PATHS`) — over a root holding
`commands/`, `skills/<name>/SKILL.md`, `agents/*.md`, `hooks/hooks.json`, and `.mcp.json`. The
manifest `name` must match `^[a-z0-9][a-z0-9._-]{0,127}$`.

## Why it is a decision, not a task

**It replaces per-file ownership with per-unit ownership.** Squad's receipt records and
verifies each deployed file, which is what makes `squad status`, drift detection, and
transactional rollback work. A plugin is enabled or disabled as one unit.

**It renames every agent.** `loadPluginAgentProfiles` namespaces a plugin's agents as
`<plugin>:<agent>`, exposing the bare name only when it is unambiguous and unreserved. Canonical
identities would no longer be what an operator types.

**It changes the trust model.** Plugin-scope skill scanning refuses to follow symbolic links at
every granularity — root, subdirectory, and `SKILL.md` itself — while user-scope roots follow
them. A packaging target must not rely on links.

The existing `SquadPacker` "plugins" package format synthesizes a generic
`agent-plugins.org` manifest over `skills/` and `mcp.json` only. A ZCode plugin target would
be a second, richer packaging format rather than an extension of that one.

## Not on this list

Two things that looked like gaps when the ZCode target landed turned out not to be:

- **Hooks.** `products/kyber-squad/` declares no hook artifact and `SquadSource` models none,
  so there is nothing to render. Project-scope hooks are also gated behind a workspace trust
  prompt (`bootstrap/src/workspace-hook-trust-cli.ts`), which would make deploying them a
  security decision rather than a rendering one.
- **MCP servers.** Not deferred — decided per [ADR 0021](../../adr/0021-zcode-command-lowering-and-resource-relocation.md)
  (Decision 5 & 7). `ZCodeRenderer` emits fully qualified `mcp__<server>__<tool>` names from
  canonical `required-mcp-tools` on each entitled agent rather than omitting MCP, and
  `squad doctor` checks that required servers are declared in ZCode's configuration. Only the
  pure orchestrator has MCP withheld (recording `permission-not-expressible`). `mcpServers`
  is deliberately omitted from agent frontmatter because the explicit tool allowlist already
  governs model visibility and naming a server there introduces a redundant failure mode.
