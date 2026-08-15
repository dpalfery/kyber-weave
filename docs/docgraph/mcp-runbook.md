---
id: docgraph/mcp-runbook
title: Running the DocGraph MCP server
doc-type: runbook
status: current
component: DocGraph
source-root: src/KyberWeave.Mcp
owner: dpalfery
last-reviewed: 2026-08-15
code-refs:
  - DocsTools
---

# Running the DocGraph MCP server

`kyber-weave-mcp` serves [the documentation graph](architecture.md) to an agent over stdio
MCP. It is the delivery surface for the whole feature: everything else produces the corpus,
this hands it to a model.

## Why it is a separate binary

JSON-RPC owns stdout, and the CLI is built on Spectre.Console, which also writes there. A
separate executable makes stream corruption **structurally impossible** rather than a
matter of discipline. All logging is pinned to stderr for the same reason — one stray line
on stdout breaks the transport.

## Wiring it up

Every client launches the same process the same way — `kyber-weave-mcp` over stdio, with
`--repo-root` pointing at the repository. Only the file it goes in and the names of the
keys differ, and they differ more than you would expect:

| Client | File | Server key | Notes |
|---|---|---|---|
| [Claude Code](#claude-code) | `.mcp.json` (repo root) | `mcpServers` | Committed servers need approval on first run |
| [Cursor](#cursor) | `.cursor/mcp.json` | `mcpServers` | Same shape as Claude Code |
| [VS Code / Copilot in the editor](#vs-code-and-github-copilot-in-the-editor) | `.vscode/mcp.json` | `servers` | Not `.mcp.json`, and not `mcpServers` |
| [Copilot coding agent](#github-copilot-coding-agent) | Repository settings, not a file | `mcpServers` | `tools` is required; the binary must be installed first |
| [Codex CLI](#codex-cli) | `~/.codex/config.toml` | `[mcp_servers.<name>]` | TOML, and the table name is snake_case |
| [opencode](#opencode) | `opencode.json` | `mcp` | Command and arguments are one array |

Use an **absolute path** to the binary in any client launched from a desktop icon rather
than a shell — GUI processes inherit a minimal PATH that usually excludes `~/.local/bin`,
and the failure looks like a server that will not start rather than a missing binary.

### Claude Code

`.mcp.json` in the repository root, so the whole team gets the server from a checkout:

```json
{
  "mcpServers": {
    "kyber-weave": {
      "type": "stdio",
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."]
    }
  }
}
```

Or write it from the CLI, which is equivalent:

```bash
claude mcp add --scope project kyber-weave -- kyber-weave-mcp --repo-root .
```

Servers from a committed `.mcp.json` are **not trusted automatically** — a checkout cannot
approve its own servers. Each user approves once on first run, and the server sits at
`⏸ Pending approval` in `/mcp` until then.

### Cursor

`.cursor/mcp.json` in the repository, or `~/.cursor/mcp.json` to get it in every project.
The shape matches Claude Code:

```json
{
  "mcpServers": {
    "kyber-weave": {
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."]
    }
  }
}
```

### VS Code and GitHub Copilot in the editor

Copilot's agent mode reads `.vscode/mcp.json`. Two things differ from every other JSON
client here, and both fail silently as "no tools appeared": the file is **not** `.mcp.json`
at the root, and the key is **`servers`**, not `mcpServers`.

```json
{
  "servers": {
    "kyber-weave": {
      "type": "stdio",
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "${workspaceFolder}"],
      "env": {
        "KYBER_WEAVE_REPO_ROOT": "${workspaceFolder}"
      }
    }
  }
}
```

`${workspaceFolder}` is worth using here rather than `.`: VS Code does not guarantee the
server's working directory, and in a multi-root workspace `.` is genuinely ambiguous. The
`env` entry is belt and braces — either mechanism alone resolves the root.

### GitHub Copilot coding agent

Configured in the repository's settings under **Copilot → coding agent → MCP
configuration**, not in a file in the tree. `type` and `tools` are both required, and
`tools` must name the tools to expose or use `["*"]`:

```json
{
  "mcpServers": {
    "kyber-weave": {
      "type": "local",
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."],
      "tools": ["*"]
    }
  }
}
```

The coding agent runs in an **ephemeral container that has never heard of Kyber-Weave**, so
this configuration alone starts nothing. Install the binary in
`.github/workflows/copilot-setup-steps.yml`, which runs before the agent starts:

```yaml
name: Copilot setup steps
on: workflow_dispatch
jobs:
  copilot-setup-steps:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5
      - name: Install Kyber-Weave
        run: |
          curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
          echo "$HOME/.local/bin" >> "$GITHUB_PATH"
```

`--repo-root .` is safe in this one case because the agent's working directory is the
checkout. Everywhere else, prefer an explicit path.

### Codex CLI

TOML, not JSON, in `~/.codex/config.toml` — or `.codex/config.toml` in a trusted project.
Note the underscore: the table is `mcp_servers`, not `mcpServers`.

```toml
[mcp_servers.kyber-weave]
command = "kyber-weave-mcp"
args = ["--repo-root", "/path/to/your/repo"]
startup_timeout_sec = 10
```

Or from the CLI:

```bash
codex mcp add kyber-weave -- kyber-weave-mcp --repo-root /path/to/your/repo
```

Codex has no workspace-variable substitution, so an absolute path is the only reliable
option in the user-level config. Verify with `codex mcp list`.

### opencode

`opencode.json` in the repository, or `~/.config/opencode/opencode.json` for all projects.
The distinguishing quirk is that **`command` is a single array** holding the executable and
its arguments together, rather than the `command` / `args` split every other client uses —
and the environment key is `environment`, not `env`:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "kyber-weave": {
      "type": "local",
      "command": ["kyber-weave-mcp", "--repo-root", "."],
      "enabled": true,
      "environment": {
        "KYBER_WEAVE_REPO_ROOT": "."
      }
    }
  }
}
```

### From source

Any of the above works against a source build by swapping the command:

```bash
dotnet run --project src/KyberWeave.Mcp -- --repo-root .
```

In a client config that means `"command": "dotnet"` with the rest as arguments. Build once
with `-c Release` first — a client that times out during startup is usually waiting on a
first-run compile.

### Resolving the repository root

Clients launch servers with an unpredictable working directory, so the root is resolved in
this order:

1. `--repo-root <path>`
2. the `KYBER_WEAVE_REPO_ROOT` environment variable
3. the nearest ancestor of the working directory containing a `.git` entry
4. the working directory itself

Guessing wrong yields an **empty corpus rather than an error**, which is the failure mode
to suspect first when every query returns a miss. Pass `--repo-root` explicitly in a client
config.

### The ontology comes from the host config

Once the root is resolved, the server reads `.kyber-weave/kyber-weave.yml` from it and
serves the same corpus [`docs validate`](governance.md) does — every `docs-root`, and the
catalog wherever [config](../configuration.md) puts it. A repository with no config gets
product defaults.

A config that cannot be read is reported on **stderr** as `KW-CONFIG-001` and the server
keeps running on defaults. That combination — a corpus that looks empty and a line on
stderr the client may not surface — is worth checking before blaming the repo root.
This fallback applies to the retrieval corpus initialized at startup. The analysis and
glossary tools reload the current config for each call and return an unavailable response
until the invalid config is fixed; they do not analyze on defaults.

## The tools

All four are declared `ReadOnly` and `OpenWorld = false`, which the SDK maps to the
protocol's `readOnlyHint` and `openWorldHint`. A client can act on an annotation; it cannot
act on a sentence claiming the same thing, which is why that claim is no longer prose.

### How the descriptions are written

Every description is **routing metadata plus a calling contract**, not documentation. A
capability clause states what comes back, the trigger clause — the longest part — names the
conditions that should fire it, and the response semantics say what an empty result means.

Two rules are worth stating because breaking them is subtle and the damage shows up far
away:

**A description claims no authority over other tools.** `Call this instead of Grep` is a
global directive arriving through a routing hint, competing with the harness's own
tool-selection rules. It is replaced by the condition that wins the same decision on
merit — superseded documents are outside the corpus, so a match is current guidance.

**Exclusions are properties, never noun lists.** `Do not use for source code, tests, CI
logs` puts those nouns in permanently resident text held back only by a negation, and
negation is the first thing to degrade when a model scans many tools at once. Read out of
context it becomes a prohibition on reading tests. Only `docs_explore` carries an
exclusion, because it is the broadest of the four; the narrower three state their territory
positively so no request falls between them.

Where a constraint can live on a parameter it goes there instead — how `maxDocs` and
`charBudget` interact sits on those parameters, because a property description binds at the
point of use.

### `docs_explore(query, maxDocs = 5, charBudget = 12000)`

Ranked documents for a question, symbol, route, component, or document id. Each hit
carries its frontmatter identity, its most relevant `##` sections within budget, and its
resolved code joins as `symbol -> file:line`.

The response leads with the relevance range so a caller can judge confidence, and warns
explicitly when no CodeGraph index was readable and joins are therefore unresolved.
Individual sections are capped at 6000 characters and joins at 20 per document, so one
enormous document cannot crowd out every other result.

A miss says so plainly, reports how many documents were considered, and suggests naming a
component, doc-id, or symbol instead. Treat that as permission to fall back to grep.

### `docs_for_symbol(symbol)`

The documents whose `code-refs` **formally claim** a symbol — not those that mention it in
prose, which is exactly the distinction grep cannot make. Run it before renaming, moving,
or deleting anything to find the documentation that must change with it.

An empty result distinguishes the two cases the caller cares about: the symbol is
undocumented, or it is discussed in prose that never claimed ownership of it.

### `docs_analysis_candidates(kind?, cursor?, limit = 20, charBudget = 12000)`

Runs the repository's current configured documentation analysis and returns candidates in
stable kind, term, and candidate-id order. `kind` is optional and accepts `duplicate`,
`conflict`, or `terminology`. Pass the returned candidate id as `cursor` to continue after
that item.

The tool is conversational rather than a bulk export: the hard candidate limit is 20,
the hard response budget is 12,000 characters, and evidence per candidate is capped. The
response keeps local cost/cache metrics and line-addressable evidence inside the same
budget. Use [`docs review export`](analysis.md#cli-and-exit-behavior) when a reviewing
agent needs the versioned rubric and hashes for reusable verdicts.

### `docs_glossary(term)`

Looks up one managed glossary term case-insensitively and returns its proposed, approved,
and rejected senses, definitions, scopes, and aliases. An unknown term is an ordinary
empty result, not an error. Output is capped at 20 senses and 12,000 characters.

Neither analysis tool accepts a write parameter, so neither can import a
verdict or change the glossary. Reusable decisions enter through `docs review import`, and
glossary proposals enter through `docs glossary --write` at the CLI. The MCP reader reloads
the current config and corpus for each call; embeddings remain loopback-only and are not
called unless the local cache is safely ignored.

## Reading a code join

```
code joins:
  DocumentIndex -> src/KyberWeave.Core/Docs/Search/DocumentIndex.cs:23 [class]
  Authority -> src/KyberWeave.Core/Docs/Search/DocumentIndex.cs:357 [method] (+2 other same-named)
```

Two annotations mean the resolver had to guess and you should verify:

- `(+N other same-named)` — several symbols share the name; a declaration was preferred
- `(outside this document's source-root)` — the match came from elsewhere in the repository

## Staleness

The index reloads on demand. Document mtimes and file count drive a corpus rebuild;
`.codegraph/codegraph.db` mtime and length drive a joins-only rebuild. No restart is needed
after editing documentation or after the CodeGraph daemon rewrites its index.

## Troubleshooting

| Symptom | Cause |
|---|---|
| The server never starts | `kyber-weave-mcp` is not on the PATH the client inherits — use an absolute path |
| The client lists no tools at all | Wrong file or wrong key for that client — check the table in [Wiring it up](#wiring-it-up) |
| Every query is a miss | Wrong repo root, or `docs-root` in [config](../configuration.md) names a tree the documents are not in — check stderr for `KW-CONFIG-001` |
| "no CodeGraph index was readable" | No `.codegraph/codegraph.db`, or `sqlite3` missing from PATH |
| Joins show `(unresolved)` | The symbol is in `code-refs` but not in the index — run [`docs drift`](governance.md) |
| Analysis warns that the cache is unsafe | Run `docs init` to merge `.kyber-weave/.gitignore` with `cache/`; until then deterministic/lexical analysis continues and no document text is sent for embeddings |
| `docs_glossary` returns no senses | The configured glossary is absent, the term is not present, or its spelling differs; use `docs glossary .` to preview proposals |
| Client reports a protocol error | Something wrote to stdout; check that you launched `kyber-weave-mcp`, not `kyber-weave` |

## Related

- [Retrieval and ranking](retrieval.md) — how results are chosen and budgeted
- [Documentation analysis and review](analysis.md) — findings, review exchange, cache, and glossary
- [DocGraph architecture](architecture.md) — the index behind these tools
- [Installing Kyber-Weave](../install.md) — getting the binary on PATH
