---
id: docgraph/architecture
title: DocGraph architecture
doc-type: architecture
status: current
component: DocGraph
source-root: src/KyberWeave.Application/Docs
owner: dpalfery
last-reviewed: 2026-11-25
decided-by:
  - adr/0010-keywords-prefix-coverage-and-oov-idf
  - adr/0057-squad-and-docs-are-their-own-assemblies
  - adr/0062-the-docs-server-refreshes-codegraph-and-fails-its-tools
code-refs:
  - CodeGraphFreshness
  - CodeGraphIndexProvisioner
  - CorpusProvenance
  - DocumentLoader
  - DocumentCorpus
  - DocumentIndex
  - DocumentIndexHost
  - ConfigRegValidator
  - ConfigRegConfig
  - ConfigRegEntry
  - ConfigRegEntryKind
  - ConfigRegMarkdown
lead: retrieval-has-no-database
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
                             the code graph, index docs by symbol and
                             by frontmatter keywords                    → DocumentIndex
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

The optional `nodes.jsonl` / `edges.jsonl` export from `docs export-graph` is written for external
consumers and never read back. Documentation analysis has a separate local cache at
`.kyber-weave/cache/docs-analysis.sqlite3` for reusable vectors and agent verdicts. It is
not a retrieval database or a source of documentation, and deterministic analysis runs
without it. The cache exists only when the narrow path is safely ignored; see
[analysis privacy and persistence](analysis.md#local-cache-and-embedding-privacy).

The one database in play is **CodeGraph's**, at `.codegraph/codegraph.db`. Kyber-Weave
opens it read-only and issues nothing but `SELECT`. It does not create it, write to it, or
place documentation nodes in it — see [the code graph join](#the-code-graph-join).

## Change-scoped archive reading

The live corpus walk excludes the archive subtree (`archive` is an excluded path segment
by default), so archived plans and specifications are not retrievable documents. The
archive-closeout gate (`KW-DOC-LIFECYCLE-004`) still reads the entries the change touches,
but through a narrow entry point — `DocumentLoader.LoadArchivedDocument` reads one named
file, with no directory walk and no exclusion bypass beyond that file. Retrieval,
ranking, and the two clocks above never see the archive; only the gate does, and only for
the entries the diff selects. See [governance](governance.md) for the scope and the
`C0`–`C9` conditions.

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
`docs export-graph` hard-require it.

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

## Mode properties are not paths

The configuration registry publishes repository paths under stable names, so a portable skill
resolves `<docs-root>` instead of embedding a relative link out of its own directory.
`ConfigRegConfig` resolves the entries, `ConfigRegMarkdown` renders them into the generated
block of the repository root `AGENTS.md`, and `ConfigRegValidator` checks that block against
the configuration that produced it.

Every entry used to be a path, and the registry's promise was that every value resolves on
disk. That promise cannot cover a setting: `enabled` is not a file, and a validator that went
looking for it would fail a repository for adopting the feature it names. So a registry entry
carries its kind — `ConfigRegEntry` with `ConfigRegEntryKind.Path` or `ConfigRegEntryKind.Mode`
— and the **kind**, not the property name, decides how an entry is checked.

- **`ConfigRegEntryKind.Path`**: the value is a repository-relative path. `KW-CONFIG-REG-001`
  fires when it resolves to nothing on disk.
- **`ConfigRegEntryKind.Mode`**: the value is a mode word describing configuration state. The
  path-existence check is skipped for it, because there is nothing on disk to find. No other
  check is relaxed with it: path entries are still checked, without exception.
- **Both kinds are covered by `KW-CONFIG-REG-002`**: the properties the rendered block publishes
  must match what the configuration produces, whichever kind an entry carries. A mode property the
  configuration no longer publishes, or one published with the wrong value, is as stale as a moved
  path. The generated prose around the properties is not compared: it is template text whose
  wording changes between builds and it publishes nothing an agent resolves.
- **The standards warning period also covers the block.** A block whose only differences are
  standard properties still naming the folder's `README.md` is stale because the host has not run
  the migration, so `KW-CONFIG-REG-002` is a warning naming `kyber-weave docs standards migrate`
  rather than a planning-check failure. Any other difference stays an error.

`<intake-assessment>` is the first instance. A repository that sets `ontology: intake: true`
in `.kyber-weave/kyber-weave.yml` gets the property published with the value `enabled`;
absent or false, nothing is published, and the property's absence is the off state. What the
property means is the consumer's, not the engine's — the conductor reads it to decide whether
to run an intake assessment. See the
[Kyber-Squad requirements](../kyber-squad/requirements.md#intake-assessment) and the
[adoption guide](../kyber-squad/onboarding.md#intake-assessment-opt-in-per-repository).

Because the kind travels on the entry, a mode property is also reserved: a host may not
declare one under `config-reg:`, because that would replace a setting with a path and break
the promise the registry makes.

**The run settings are the next two.** `stop-on-tool-failure` at the top level, and
`run.base-branch`, publish `<stop-on-tool-failure>` and
`<run-base-branch>` as mode properties. A key the operator never
wrote publishes nothing at all: an agent already applies the documented default, only a value
somebody chose can differ from it, and a published default would claim a choice the repository
never made. `run.base-branch` carries a branch name rather than a mode word, and the kind still
applies — the value names nothing on disk, so `KW-CONFIG-REG-001` must not look for it there
while `KW-CONFIG-REG-002` still checks the line. See
[Run controls](../configuration.md#run-controls).

## Standards folders and the KW skill pool

A technology or role folder under `<docs-root>/standards/<scope>/` holds four files: `<scope>.standards.md`
(technologies only), `<scope>.rules.md`, `<scope>.skills.yml` and `<scope>.arbiter-rules.yml`. The skills file is a
**list of KW skill names**, not prose: a YAML sequence of strings and nothing else, where `[]` is a valid empty list
and a missing file lists nothing. `KW-DOC-SPEC-012` expects `<scope>.skills.yml`, and `KW-DOC-SPEC-015` no longer
checks a skills file.

The skills the names point at live in a **pool**, `<docs-root>/standards/skills/<name>/SKILL.md`, beside a shipped
pool embedded in the binary. The pool's `SKILL.md` files carry harness frontmatter (`name`, `description`) so a skill
copies between a harness and the pool unchanged, which no corpus document does. So the corpus does not contain them:
`DocumentLoader` skips every file beneath a docs root's `standards/skills/`, and the standards-folder walk skips the
`skills` folder (`skills` is therefore a reserved scope name). Without that, every pool file would fail
`KW-DOC-SPEC-001` and the pool folder `KW-DOC-SPEC-012`. The pool is checked by its own rule family,
`KW-DOC-SKILL-001` to `-007`, which a repository's `.skills.yml` files and pool are held to by `docs validate`:
a list that is not names, a listed name with no folder, a pool skill without `name` or `description`, a `name` that
differs from its folder, a legacy `<scope>.skills.md`, a scope named `skills`, and a pool folder with no `SKILL.md` or one behind a link
([the rows](../ci-pipelines/rule-reference.md#kw-skills--docs-validate)).

How the lists resolve to a prompt, and how `load_skill` reads a skill, are in
[the Squad architecture](../kyber-squad/architecture.md#13-kw-skills-the-on-demand-pool) and
[the MCP runbook](mcp-runbook.md#load_skillname-projectpath--null).

## Provisioning the index

The graph lives in `.codegraph/` at the git root, and `docs drift` is the gate that needs it.
`kyber-weave doctor` owns creating and refreshing it, so the rules live in one place rather
than in each caller's shell script:

- **Repository-local only.** The root is the git root. Doctor refuses (exit `2`) when the path
  is not a repository or is the user's home, and it never provisions another checkout.
- **Init once, sync thereafter.** `codegraph init .` runs only when `.codegraph/` is absent;
  every later provisioning run is `codegraph sync .`, which brings an existing index up to
  date. `codegraph index .` rebuilds the whole index and is the **repair** step, never the
  routine one — running it on a healthy index pays a full rebuild for no new information.
  Each child runs with the root as the working directory, under a 600-second bound that kills
  the process tree on expiry.
- **The worker's first command is `doctor --ensure`.** It is `sync` or `init`, then a restore
  of any tracked file the preparation rewrote, then one `codegraph explore` and one docs-server
  call, then a single `codegraph index` repair and a single retry. The restore is the reason it
  is not a shell alias: initialization rewrites tracked files, and reverting the operator's own
  edits would be unrecoverable. The set of dirty tracked files is recorded before the index
  work starts, so only what the index itself wrote is reverted.
- **A held CodeGraph lock is never repaired.** A `sync` that fails only because CodeGraph's own
  lock (`.codegraph/codegraph.lock`) is held is retried twice, after 0.3 and 0.7 seconds. If
  the lock is still held, `codegraph.index` passes with a `warning:` message, the exit code is
  not changed by it, and the `explore` and docs-server proofs still run against the index on
  disk. No repair runs while another live process holds that lock, whatever failed:
  `codegraph index` against a held lock exits 1 and leaves the database empty (measured on
  CodeGraph 1.6.2), so the repair would break a healthy index.
- **Never delete, never install.** A corrupt or unwritable `.codegraph/` is reported with a
  remedy a human applies; callers of the CLI are told to install CodeGraph rather than it being
  installed for them. The binary's absence is exit `3`, not a failure to route around.
- **One writer at a time.** A lock file in the OS temp directory, keyed by a hash of the root,
  serialises concurrent runs; the second waits up to the index timeout and then reports
  `codegraph.index busy`. It is deliberately never removed, so a third run cannot slip past a
  holder that still holds it.
- **A running daemon is left alone.** Sync and index coexist with it. Doctor never stops or
  restarts it, and never touches its files.

Read-only callers use `doctor --check`: it detects the binary and the index and runs the live
MCP proof, and never writes.

## The server keeps the index fresh, and fails its tools when it cannot

The docs server used to answer from whatever index happened to be on disk, which for a role
with no shell was the only index it would ever see. It now refreshes the index itself
([ADR 0061](../adr/0062-the-docs-server-refreshes-codegraph-and-fails-its-tools.md)):

- **The refresh.** On the first tool call of the process, then at most once every 60 seconds,
  the server runs the same `sync` (or `init`) the doctor runs, through
  `CodeGraphProvisioner`, under a 30-second bound. It takes the same cross-process lock, with
  a **zero wait**: a tool call cannot block for the 600-second index timeout.
- **Contention is a note, not a failure.** A held lock means another process is refreshing the
  index — a worker's `doctor --ensure`, the CodeGraph daemon — so the answer proceeds with one
  `note:` line above the body.
- **No sync before a look for CodeGraph's lock file.** CodeGraph keeps a second lock, the file
  `.codegraph/codegraph.lock`, which its server, its file watcher and every `sync` create while
  they write. The refresh looks for that file before it starts a `sync`. If the file exists it
  waits 1 second and looks again, then 5 seconds, then 30. As soon as a look finds no file the
  `sync` runs. If the fourth look still finds it, after 36 seconds, no `sync` runs and the
  tools fail as below: the reason names the file, the PID it records and whether that process
  is running, and the fix is `codegraph unlock <root>` for a process that is not running and
  "wait for it or stop it" otherwise. Existence is the test, not the PID. This failure is not
  kept for the 60-second interval, so the next call looks again.
- **Losing the race after the look is contention.** A `sync` can still find the lock taken
  after the look found no file. It is retried twice, after 0.3 and 0.7 seconds, each after its
  own look; if it is still held the answer proceeds with the same kind of `note:` line.
  CodeGraph exits 1 for every `sync` failure, so this case is recognised by the message its
  `sync` prints, "Sync could not acquire the file lock". Its "rebuild in progress" messages
  stay failures, because the database file is being replaced.
- **Failure is a token, not a sentence.** A missing binary or a failed `sync` returns
  `STATUS: TOOL_FAILURE` with `tool:`, `reason:` and `fix:` lines under the stop rule, or the
  same detail as a `warning:` line under `stop-on-tool-failure: false`. The token is a whole
  line so a passing mention cannot satisfy an orchestrator that routes on it. Seven of the
  eight read-only tools return the block; `docs_get_source_repository_and_revision` always answers so a
  failure can be diagnosed, and
  `docs_detect_drift_between_documents_and_code` answers **with** the failure because its
  verdict is the failure report.
- **Read-only for documents, a writer for a cache.** The tools write `.codegraph/`, which is
  gitignored, and never write a document. The server keeps its contract with a client that
  reads it as a read-only corpus service.
- **Gated on the declaration.** The refresh and the failure apply only where the repository
  lists `codegraph` under `mcp.required-servers`. A repository that never declared it takes
  no index lock, reports nothing, and answers exactly as it did before. Settings are read at
  each refresh, so an edit takes effect without a restart, and
  `kyber-weave-mcp --no-codegraph-refresh` disables the refresh entirely — which is how
  `doctor --check` keeps its promise never to run `init` or `index`.

**Provenance is what makes a wrong answer visible.** Every response leads with
`root=`, `branch=`, `repo=`, `rev=`, `dirty=` and `documents=`. `repo=` is the git directory
every worktree of the repository shares, so a `root=` from another worktree with a matching
`repo=` is the same repository on another branch, and only a different `repo=` is a foreign
checkout. A worktree does not share the canonical checkout's index: it indexes itself with
`doctor --ensure`, and a worker with a shell asks its own index through the CLI.

## Plan parsing reads a task's paths from its Files bullets

`PlanDocumentParser` derives each task's file list for `docs tasks`, the packet and the Arbiter's plan facts. In a
`Files` bullet only the lead names files: the backticked spans before the bullet's first colon are paths (a bare file
name after a directory in that lead inherits the directory), and a span in the description after the colon counts only
when it contains a slash and never inherits a directory. A description that names `process.env.VERSION` or
`matrix.target` is therefore prose, not a file, while one that names `src/a/B.cs` still is. The delegation-prompt
check uses its own shared rule, unchanged. The task file's digest covers the parsed files, so a change to this rule
makes existing task files stale until `docs tasks --write` regenerates them
([Kyber Squad architecture](../kyber-squad/architecture.md)).

## Related

- [Retrieval and ranking](retrieval.md) — how a query becomes an answer
- [Documentation analysis and review](analysis.md) — graph-first duplicates, conflicts, and terminology
- [Documentation governance](governance.md) — the conformance and drift gates
- [MCP server runbook](mcp-runbook.md) — serving this graph to an agent
- [The documentation ontology](../documentation-ontology.md) — the schema all of this assumes
