---
id: docgraph/architecture
title: DocGraph architecture
doc-type: architecture
status: current
component: DocGraph
source-root: src/KyberWeave.Core/Docs
owner: dpalfery
last-reviewed: 2026-08-12
code-refs:
  - DocumentLoader
  - DocumentCorpus
  - DocumentIndex
  - DocumentIndexHost
---

# DocGraph architecture

DocGraph turns a governed documentation corpus into a **queryable in-memory graph**, joined
at query time to a code index. It is the feature the rest of Kyber-Weave exists to support.

## The pipeline

```
DocumentLoader.Load()        walk every <docs-root>/**/*.md, parse frontmatter + body,
                             split on '##', read catalog vocabularies   → DocumentSet
        │
DocumentCorpus.Build()       BM25 term statistics, fused term vectors   → DocumentCorpus
        │
DocumentIndex.Build()        resolve code-refs / api-endpoints against
                             the code graph, index docs by symbol       → DocumentIndex
        │
DocumentIndexHost.Current()  cache, and rebuild whichever half is stale
```

Each stage is a pure transform of the one before it. Nothing writes to disk.

Documentation analysis branches from the same `DocumentSet` and an immutable projection
of the export relationships:

```
ClaimExtractor              paragraphs, list items, table rows, code fences  → claims
        │
DocGraphProjection.Build()  document relationships + one-hop CodeGraph       → neighbors
        │
DocumentationAnalyzer       exact clusters + bounded graph/lexical/semantic  → findings
```

That shared projection keeps analysis graph-first rather than rebuilding relationships
or scanning every claim pair. See [documentation analysis](analysis.md) for the blocking,
classification, and review contracts.

## Retrieval has no database

The graph lives in process memory and is rebuilt from the Markdown on demand. The corpus
is roughly 600 KB of prose for a repository of this size, and parsing plus vectorising it
costs milliseconds — cheap enough that a persistence tier would buy latency at the cost of
a cache-invalidation problem.

The optional `nodes.jsonl` / `edges.jsonl` export from `docs graph` is written for external
consumers and never read back. Documentation analysis has a separate local cache at
`.kyber-weave/cache/docs-analysis.sqlite3` for reusable vectors and agent verdicts. It is
not a retrieval database or a source of documentation, and deterministic analysis runs
without it. The cache exists only when the narrow path is safely ignored; see
[analysis privacy and persistence](analysis.md#local-cache-and-embedding-privacy).

The one database in play is **CodeGraph's**, at `.codegraph/codegraph.db`. Kyber-Weave
opens it read-only and issues nothing but `SELECT`. It does not create it, write to it, or
place documentation nodes in it — see [the code graph join](#the-code-graph-join).

## Two inputs, two clocks

`DocumentIndexHost` tracks its two inputs separately, because they change at wildly
different rates:

| Input | Fingerprint | Changes |
|---|---|---|
| Documentation | newest mtime + file count across the docs roots and the catalog | when a human edits a file |
| Code graph | mtime + byte length of `.codegraph/codegraph.db` | continuously, while a coding session runs |

Folding both into one fingerprint meant every background write by the CodeGraph daemon
forced a full re-read and re-vectorisation of every document — the expensive half — to
refresh joins that are the cheap half. So a documentation change rebuilds the corpus, and
a code-graph change rebuilds only the joins.

The consequence worth knowing: the docs fingerprint is corpus-wide, so editing **one**
document rebuilds **all** of them. There is no incremental path today. That is
comfortable at hundreds of documents and is the first thing to revisit at thousands.

## The code graph join

A document's `code-refs` entries are resolved through `ICodeGraphResolver`, a port with
one production adapter that reads CodeGraph's SQLite index. Resolution is a **join, not a
merge**: documentation stays in memory, code stays in the index, and the two meet only
when retrieval, export, drift, or analysis needs them together.

Both the reference as authored and its last dotted segment are indexed, so a `code-refs`
entry of `KyberWeave.Core.Docs.Search.DocumentIndex` is findable by the bare `DocumentIndex`
a caller would actually type.

When several symbols share a name, the document's own `source-root` disambiguates: symbols
beneath it win, and within that pool a declaration outranks an incidentally same-named
member. A class named `X` is far likelier to be what documentation calls "X" than a
property that happens to be called `X`.

Analysis also asks an optional one-hop neighborhood port for `contains`, `calls`,
`references`, `instantiates`, `extends`, and `implements` edges. `imports` and
high-degree code nodes are excluded because they connect too much of the repository to be
useful evidence.

**The index is optional.** Without it, `IsAvailable` is false, joins come back empty,
retrieval still works completely, and analysis continues with document relationships and
bounded lexical search after one warning. Only [`docs drift`](governance.md) and
`docs graph` hard-require it.

## Why sqlite3 and not a library

The adapter shells out to the `sqlite3` CLI rather than referencing `Microsoft.Data.Sqlite`,
whose native dependency `SQLitePCLRaw.lib.e_sqlite3` carries an unresolved advisory at
every published version. Arguments are passed as a list and never through a shell, so a
database path cannot be reinterpreted. The whole node table is read in one batched
invocation rather than per-symbol, which is why an adapter that could have been thousands
of process launches is one.

The connection is deliberately **not** opened `-readonly`: the CodeGraph daemon leaves the
index in WAL mode, and a WAL database needs a shared-memory file that `-readonly` forbids
creating.

## Related

- [Retrieval and ranking](retrieval.md) — how a query becomes an answer
- [Documentation analysis and review](analysis.md) — graph-first duplicates, conflicts, and terminology
- [Documentation governance](governance.md) — the conformance and drift gates
- [MCP server runbook](mcp-runbook.md) — serving this graph to an agent
- [The documentation ontology](../documentation-ontology.md) — the schema all of this assumes
