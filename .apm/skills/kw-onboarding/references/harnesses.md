# Harnesses

The list of supported harnesses comes from the product, not from this file: the `--target` line
of `kyber-weave squad install --help`, or the "Renderers available" line of
`kyber-weave squad doctor --global`. This table only adds what each harness needs beyond the
install. A harness the CLI lists but this table does not: install it and say no extra steps are
known.

## Install commands

| Goal | Command |
|---|---|
| What is deployed | `kyber-weave squad status --global`, `kyber-weave squad status .` |
| Deploy for every repository (recommended) | `kyber-weave squad install --global --target <token>` |
| Deploy into this repository only | `kyber-weave squad install . --target <token>` |
| Already deployed: refresh | `kyber-weave squad update [--global] --target <tokens>` (list every target to keep: an explicit list replaces the deployed set) |
| What gates each harness | `kyber-weave arbiter status` |
| Arbiter hooks, every repository | `kyber-weave arbiter install --target <token>` (one target per run) |
| Arbiter hooks, this repository only | `kyber-weave arbiter install --project --target <token>` |

`squad install` and `arbiter install` print the root they will write and ask to confirm; add
`--dry-run` first when the user wants a preview. Adding a harness to an existing global install
keeps the others. The Arbiter is the decision gate that checks each agent dispatch against the
plan; `arbiter install` turns it on and installs the harness hook that calls it.

## Per harness

"Trust step" is what the user must do once in the harness, or the hooks never run. "Needs" is a
setting the squad cannot write for them; offer to help, and change a user or harness settings
file only with a yes.

| Harness (token) | Trust step | Needs |
|---|---|---|
| Claude Code (`claude`) | Accept the workspace trust dialog; approve project MCP servers in `/mcp` on first run. Headless `claude -p` runs no project hooks. | Nothing else; nested sub-agents work by default. |
| Copilot CLI (`copilot`) | None documented. | `COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES=32000` in the shell Copilot CLI starts from (PowerShell: `$env:COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES = "32000"`), then a new session. Without it, large code-index answers are saved to files and agents stop using the index. Show the line; add it to the user's shell profile only if they ask you to. |
| VS Code Copilot (`copilot`) | Open the folder as a trusted workspace and keep `chat.useHooks` on. | User setting `chat.subagents.allowInvocationsFromSubagents: true`, so the reviewer can start its own sub-reviewers. Without it the reviewer applies every review lens itself. |
| Cursor (`cursor`) | None documented. | Sub-agents inherit the session's MCP servers; wiring `.cursor/mcp.json` is enough. |
| OpenCode (`opencode`) | None documented. | `subagent_depth` of 2 or more in the OpenCode config (default 1), so the reviewer can start sub-reviewers. A global install also offers the KyberDash capture plugin (`--no-capture-plugin` skips it). |
| Kilo (`kilo`) | None documented. | `subagent_depth` of 2 or more in the Kilo config (default 1). MCP servers go in `kilo.jsonc` under `mcp`. |
| Pi (`pi`) | Trust the project so `.pi/extensions/` loads. | The `@tintinweb/pi-subagents` extension (0.19.0 or later) for native agents, and `pi-mcp-adapter` 6.0.0 or later (7.0.0 supported) for MCP tools. |
| Codex (`codex`) | Trust the project's `.codex/` layer, then review and trust the hook in `/hooks`. Repeat after any `squad update` that changes the hook. | MCP servers in `~/.codex/config.toml`; custom agents inherit them. |
| Factory (`factory`) | None documented. | A global install also offers the KyberDash capture plugin. Reviews run through the review-council skill, since droids cannot start droids. |
| ZCode (`zcode`) | Set `hooks.enabled: true` in `~/.zcode/cli/config.json` (the squad never flips it). Project scope uses the Arbiter MCP fallback. | `kw-docs` wired, or every agent fails `squad doctor`. |
| Antigravity (`antigravity`) | Allow the MCP tools through Antigravity's `mcp(server/*)` policy patterns; they default to Ask. | Must be named explicitly with `--target`; it is never auto-detected. |
| Warp (`warp`) | No hooks. Register the Arbiter server, `kyber-weave-arbiter serve --repo-root <root>`, in Warp's MCP settings. | Agents arrive as role skills. |
| Devin (`devin`) | None documented. | `squad doctor` warns about duplicate imports in `.devin/`. |
| Command Code (`commandcode`) | Register `kyber-weave-arbiter serve --repo-root <root>` through its MCP configuration. | None known. |

After any install, update, hook, or MCP change, the user restarts the harness so it reloads its
agents, hooks, and servers.
