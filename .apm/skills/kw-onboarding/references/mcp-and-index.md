# Code index and docs tools

## What to tell the user

- **CodeGraph index** (`.codegraph/` at the repository root): a local database of every symbol
  and call in the code. Agents use it to answer "where is this used" in one call instead of
  dozens of text searches, and the documentation drift check needs it. Building it reads the
  repository and writes only `.codegraph/`; nothing leaves the machine. Indexing is the user's
  decision.
- **`kw-docs`**: the Kyber-Weave docs server (`kyber-weave-mcp`). It answers questions from the
  repository's documentation and hands agents the coding standard for a technology.
- **`codegraph`**: the CodeGraph MCP server (`codegraph serve --mcp`). It gives agents the
  `codegraph_explore` tool over the index.

An **MCP server** is a small local program a harness starts so its agents can call extra tools;
these two give agents code search and documentation answers.

## The questions, with their context

Ask each only where first-look found a gap.

**Build the code index?**
- *Why*: without it agents search code by text, which takes many more steps and misses callers;
  the docs drift check cannot run.
- *Options*: build it now (`kyber-weave doctor --provision .`), or skip (agents still work, slower).
- *Cost and undo*: one to a few minutes, longer on a large repository; writes only `.codegraph/`
  in the repository, and doctor puts back any tracked file indexing rewrites; undo by deleting
  `.codegraph/`. Nothing leaves the machine.
- *Recommend*: build it. A "no" stands for the rest of the run.

**Declare the `codegraph` server for this repository?**
- *What*: adds `codegraph` to the list of servers this repository's agents rely on, in
  `.kyber-weave/kyber-weave.yml`.
- *Why and options*: see "Declaring the servers" below for what changes with and without it.
- *Cost and undo*: one repository file; undo by deleting the entry.
- *Recommend*: declare it when the user said yes to the index; otherwise leave it out.

**Wire the servers into each harness?**
- *What*: each harness reads its own list of MCP servers; declaring a server does not add it there.
- *Why*: an unwired harness's agents have neither tool, so they search by text and guess at docs.
- *Options*: per harness, wire it (the table below says how), or leave it.
- *Cost and undo*: `mcp-wire` writes repository files only (preview without `--target`, or with
  `--dry-run`); a manual entry may be a file in the home directory (ask). Undo by removing the entry.
- *Found*: which chosen harnesses already list each server.
- *Recommend*: wire every harness the user has.

**Let CodeGraph register itself (`codegraph install`)?** Only when a harness has no slug and the
user prefers it to a manual entry. It is a third-party tool writing harness settings in the home
directory: show `codegraph install --help` and say exactly which files it names before asking.

## Index commands

| Command | Does | Ask first? |
|---|---|---|
| `kyber-weave doctor --check .` | Reports only. Never indexes. | No |
| `kyber-weave doctor --provision .` | Builds the index when absent, refreshes it otherwise. | Yes |
| `kyber-weave doctor --ensure .` | Refreshes the index, proves it and the docs server answer, repairs once, and installs the worktree-index git hook. | Yes |

Never run `codegraph init`, `sync`, or `index` directly from this skill; doctor wraps them with a
lock, a timeout, and a restore of any tracked file the index rewrites.

## Declaring the servers

The repository declares the servers its agents rely on under `mcp.required-servers` in
`.kyber-weave/kyber-weave.yml`. `docs init` writes the `kw-docs` entry; `codegraph` is left for
the user to turn on:

```yaml
mcp:
  required-servers:
    kw-docs:
      command: [kyber-weave-mcp, --repo-root, .]
      provenance: first-party
      level: required
    codegraph:
      command: [codegraph, serve, --mcp]
      provenance: third-party
      level: recommended
```

**Declaring `codegraph` changes behaviour, so explain it before adding it.** With it declared,
the docs server refreshes the index as it answers, and when the index cannot be read its tools
return a tool-failure status that stops code-editing runs (or a warning, when the
`stop-on-tool-failure` setting is `false`). Without it, the docs server never touches the index.
Recommend declaring it when the user said yes to indexing.

## Wiring a harness

A declaration is not wiring: each harness reads its own MCP configuration file. Where a slug is
listed, `kyber-weave docs mcp-wire . --target <slug>` writes every declared server into that
file (no `--target` previews; `--dry-run` shows the change). It writes repository files only.
Everything else is a manual entry the user places; show them the entry and the file.

| Harness | File, key | `mcp-wire` slug |
|---|---|---|
| Claude Code | `.mcp.json`, `mcpServers` | `claude-code` |
| Command Code | `.mcp.json`, `mcpServers` | `claude-code` (same file) |
| Factory | `.factory/mcp.json`, `mcpServers` | `factory` |
| Cursor | `.cursor/mcp.json`, `mcpServers` | `cursor` |
| VS Code Copilot | `.vscode/mcp.json`, `servers` | `vscode` |
| OpenCode | `opencode.json`, `mcp` (command and args in one array) | `opencode` |
| Kilo | `kilo.jsonc`, `mcp` | manual |
| Codex | `~/.codex/config.toml`, `[mcp_servers.<name>]` | manual (user file: ask) |
| ZCode | `zcode.json` or `.zcode/config.json`, `mcp.servers` | manual; ZCode fails every agent until `kw-docs` is wired |
| Antigravity | `.agents/mcp_config.json`, `mcpServers` | manual |
| Pi | `.mcp.json` in the `pi-mcp-adapter` schema, or `~/.config/mcp/mcp.json` | manual |
| Copilot CLI | `$COPILOT_HOME/mcp-config.json` (default `~/.copilot/`), `mcpServers` | manual (user file: ask) |
| Warp | Warp's own MCP settings | manual |

Clients started from a desktop icon may not see `~/.local/bin` on `PATH`; if a server will not
start there, use the absolute path of the binary in the entry.

## Proving they answer

Tool names can carry a harness prefix (for example `mcp__kw-docs__…`). In the current session:

- **kw-docs**: call `docs_get_source_repository_and_revision`. A good answer starts with a
  `provenance:` line whose `root=` is this repository (or another worktree with the same
  `repo=`).
- **codegraph**: call `codegraph_explore` with a symbol or file name from this repository. A good
  answer returns source with line numbers.

If a tool is not offered in this session, the server is not wired for this harness or the harness
needs a restart or an approval (Claude Code asks once per project-level server). For any other
harness the user chose, tell them what to run there to check, since this session cannot reach it.
