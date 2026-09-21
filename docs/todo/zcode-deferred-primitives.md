---
id: todo/zcode-deferred-primitives
title: ZCode primitives Kyber-Squad does not deploy yet
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-21
status: draft
---

# ZCode primitives Kyber-Squad does not deploy yet

`ZCodeRenderer` covers ZCode's subagents, skills, and — for the lowered conductor — slash
commands, at both project and global scope. Three further ZCode primitives were deliberately
left out of the target's first release, so this page is the context for picking one up, not a
plan for doing so.

Facts verified against [`zai-org/ZCode`](https://github.com/zai-org/ZCode) **3.14.0**
(Apache-2.0) on 2026-09-21. Re-verify before acting: the published docs already lag the source
on project-scope subagents.

## Plugin packaging

ZCode loads a plugin from a manifest looked up in priority order —
`.zcode-plugin/plugin.json`, then `.claude-plugin/plugin.json`, then
`.codex-plugin/plugin.json` (`adapters/src/skills/index.ts`,
`PLUGIN_MANIFEST_RELATIVE_PATHS`). A plugin bundles `commands/`, `skills/<name>/SKILL.md`,
`agents/*.md`, `hooks/hooks.json`, and `.mcp.json` under one root, and its `name` must match
`^[a-z0-9][a-z0-9._-]{0,127}$`.

A packaged Squad would be enabled or disabled as one unit and its agents namespaced
`<plugin>:<agent>` (`loadPluginAgentProfiles`), which is a different deployment model from the
per-file receipt Squad owns today — the trade is worth stating before anyone builds it.

Note that plugin-scope skill scanning refuses to follow symbolic links at every granularity
(root, subdirectory, and `SKILL.md` itself), while user-scope roots follow them. A packaging
target must not rely on links.

## Hooks

`hooks/hooks.json` exists at plugin scope. Project-scope hooks are treated as untrusted
repository content and gated behind a workspace trust prompt
(`bootstrap/src/workspace-hook-trust-cli.ts`), so deploying them is a security decision, not a
rendering one.

## MCP servers

An agent's `mcpServers` frontmatter key names *parent* servers to expose to the child, and the
parser fails closed to `null` — rejecting the whole key — on a bare value or a nested mapping
(`parseMcpServerNames`). Squad's `mcp.json` would first need a mapping onto whatever the
parent session has registered; naming a server the parent does not have is not expressible.

## Also deferred

| Gap | Page |
|---|---|
| `storage.dir` set in `~/.zcode/cli/config.json` is invisible to the global-root resolver | [zcode-storage-dir-config-override.md](zcode-storage-dir-config-override.md) |
| A skill description containing `"` round-trips with the backslash visible | [zcode-skill-description-block-scalar.md](zcode-skill-description-block-scalar.md) |
