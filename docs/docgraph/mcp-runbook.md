---
id: docgraph/mcp-runbook
title: Running the DocGraph MCP server
doc-type: runbook
status: current
component: DocGraph
source-root: src/KyberWeave.Mcp
owner: dpalfery
last-reviewed: 2026-08-01
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

Point any MCP client at the binary on your PATH:

```json
{
  "mcpServers": {
    "kyber-weave": {
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "/path/to/your/repo"]
    }
  }
}
```

From source:

```bash
dotnet run --project src/KyberWeave.Mcp -- --repo-root .
```

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

## The tools

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
prose, which is exactly the distinction grep cannot make. Run it before renaming anything
to find the documentation that must change with it.

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
| Every query is a miss | Wrong repo root, or the docs root does not match `docs-root` in [config](../configuration.md) |
| "no CodeGraph index was readable" | No `.codegraph/codegraph.db`, or `sqlite3` missing from PATH |
| Joins show `(unresolved)` | The symbol is in `code-refs` but not in the index — run [`docs drift`](governance.md) |
| Client reports a protocol error | Something wrote to stdout; check that you launched `kyber-weave-mcp`, not `kyber-weave` |

## Related

- [Retrieval and ranking](retrieval.md) — how results are chosen and budgeted
- [DocGraph architecture](architecture.md) — the index behind these tools
- [Installing Kyber-Weave](../install.md) — getting the binary on PATH
