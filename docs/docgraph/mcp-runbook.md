---
id: docgraph/mcp-runbook
title: Running the DocGraph MCP server
doc-type: runbook
status: current
component: DocGraph
source-root: src/KyberWeave.Mcp
owner: dpalfery
last-reviewed: 2026-10-28
code-refs:
  - DocsTools
  - DrainingStreamServerTransport
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

## Closing stdin

A client may write its requests and close stdin without waiting, as a scripted health check
or `kyber-weave-mcp < requests.jsonl` does. Every request the server read before stdin
closed is still answered on stdout, and the process exits once the last answer is written.
Only replies are sent after stdin closes. Notifications, and any request the server would
start itself, are dropped, because nothing can answer them any more.

The SDK's own stdio transport drops every message once stdin reaches end of stream, even
replies to requests it has already read. `DrainingStreamServerTransport` replaces it for
that reason; its remarks have the detail. Releases up to and including `v0.1.7-rc.13` ship
the SDK transport, and there piped requests produce zero bytes on stdout
([#95](https://github.com/dpalfery/kyber-weave/issues/95)).

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

The project-local configuration emitted by the Squad package uses `--repo-root .`. That
binding is resolved against the client's working directory, which must be the directory
initialized by `kyber-weave docs init` (the command defaults to the current directory). For
global or headless clients, use an explicit absolute path or the generic
`KYBER_WEAVE_REPO_ROOT` setting. The root is resolved in this order:

1. `--repo-root <path>`
2. the `KYBER_WEAVE_REPO_ROOT` environment variable
3. the working directory itself, only when it contains a Kyber-Weave host configuration

The server never walks up to a parent Git repository and never serves an unbound default
corpus. If the selected directory was not initialized, startup fails on stderr with an
actionable `kyber-weave docs init "<path>"` command. This prevents two repositories under a
shared parent directory from accidentally reading one another's documentation.

### Asserting the root with `--expect-root`

Resolution decides which root the server binds to; `--expect-root` asserts which root the
client believes that is. After the ordinary resolution above, the server compares the two
and refuses to serve when they differ. It is a client-asserted safety rail against the
reported failure mode — a same-named server pinned to another checkout by a user-scope or
headless harness config — not a replacement for correct project-local wiring.

```bash
kyber-weave-mcp --repo-root . --expect-root /Users/you/git/your/repo
```

The value may also come from the `KYBER_WEAVE_EXPECT_ROOT` environment variable, honoured
only when the flag is absent: **the CLI flag wins over the environment**, so a shell export
cannot override a project pin. A relative value, and a value from the environment, resolve
against the same working directory `--repo-root .` uses.

A mismatch from either source is a refuse-to-serve: the server writes `KW-MCP-ROOT-002: …`
to **stderr** and exits 1. That is distinct from the `KW-MCP-ROOT-001` an unbound or
uninitialized root produces. Nothing is served either way, so no answer can come from the
wrong corpus.

### The ontology comes from the host config

Once the root is resolved, the server reads `.kyber-weave/kyber-weave.yml` from it and
serves the same corpus [`docs validate`](governance.md) does — every `docs-root`, and the
catalog wherever [config](../configuration.md) puts it. Run `kyber-weave docs init .` first
when adopting a repository without a host configuration.

A config that cannot be read is reported on **stderr** as `KW-CONFIG-001` and the server
keeps running on defaults. That combination — a corpus that looks empty and a line on
stderr the client may not surface — is worth checking before blaming the repo root.
This fallback applies to the retrieval corpus initialized at startup. The analysis and
glossary tools reload the current config for each call and return an unavailable response
until the invalid config is fixed; they do not analyze on defaults.

## Provenance on every answer

Every docs tool response — hits, misses, and empty or error prose alike — leads with
exactly one provenance line:

```text
provenance: root=/absolute/repo/root rev=1a2b3c4 dirty=no documents=72
```

- `root` — the absolute repository root the server is bound to, normalised with
  `Path.GetFullPath`.
- `rev` — the short HEAD SHA, or the literal `unavailable` when Git cannot be read.
- `dirty` — `yes` or `no` when `git status` succeeds, otherwise `unknown`. Untracked files
  are ignored.
- `documents` — the live document count for the corpus; `0` is allowed.

Git identity is read lazily and cached until the corpus is rebuilt, so the header does not
shell out on every call. A missing Git, a directory that is not a repository, or a failed
command are all success paths: the line still renders with `rev=unavailable` and
`dirty=unknown` rather than inventing a SHA or omitting the root.

The line is emitted whole and any character budget is taken from the body, so the one tell
that a same-named server answered from another checkout is never the part truncated away.
Compare `root=` to `git rev-parse --show-toplevel` before trusting an answer: a mismatch
means the server is bound to a different repository, not this checkout.

## The tools

All eight are declared `ReadOnly` and `OpenWorld = false`, which the SDK maps to the
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
exclusion, because it is the broadest of the eight; the narrower seven state their territory
positively so no request falls between them.

Where a constraint can live on a parameter it goes there instead — how `maxDocs` and
`charBudget` interact sits on those parameters, because a property description binds at the
point of use.

### `docs_explore(query, maxDocs = 5, charBudget = 12000)`

Ranked documents for a question, symbol, route, component, or document id. Each hit
carries its frontmatter identity, its most relevant `##` sections within budget, and its
resolved code joins as `symbol -> file:line`. The `doc-type:` in that identity is the
ontology's spelling rather than the enum member's name, so a coding standard comes back as
`doc-type: CODING-STANDARD`.

The response leads with the relevance range so a caller can judge confidence, and warns
explicitly when no CodeGraph index was readable and joins are therefore unresolved.
Individual sections are capped at 6000 characters and joins at 20 per document, so one
enormous document cannot crowd out every other result.

A miss says so plainly, reports how many documents were considered, and suggests naming a
component, doc-id, or symbol instead. Treat that as permission to fall back to grep.

### `agent_coding_standards(technology, topic = null, charBudget = 12000)`

The local answer for one stack: which standard this repository writes that technology by,
and what it says. A portable agent arrives holding the standard its own stack ships; this
returns *this* repository's, which is why it is a lookup by declared key rather than a
ranked guess at whichever standard is worded most like the question.

`technology` is required and accepts either the slug (`csharp`, `test`) or the
configuration registry's property name for it (`csharp-coding-standard`,
`<csharp-coding-standard>`), matched case-insensitively. Resolution trims the value, strips
one surrounding `<>` pair and the literal suffix `-coding-standard`, then matches corpus
documents whose `doc-type` is `coding-standard` by their declared `technology`. **The
corpus decides, not the registry**: declaring a technology in `ontology.technologies`
without a standard scaffolded at `docs/standards/<technology>/README.md` is still a miss.
Only a *pair* of angle brackets is the registry's prose form — a half-typed `"<csharp"` is
not quietly repaired into a hit.

`topic` is what the caller is about to do (`naming`, `error handling`, `pull request
review`). Omitted, the whole standard comes back in document order under budget. Provided,
the sections that answer it are chosen by the same rarity-weighted section ranking
[`docs_explore`](#docs_explorequery-maxdocs--5-charbudget--12000) uses. A topic no section
clears returns no prose — it does not fall back to the whole standard.

`charBudget` is total characters of prose across the standards returned, clamped to
1000–120000 and defaulting to 12000. One technology can be answered by more than one
standard — a README and an appendix beside it — and they share that one budget, ordered by
path so the same request answers the same way twice.

The provenance line is emitted whole and the budget applies to the body, so what did not fit
is named instead of silently dropped: `[omitted for space, ask again with a larger
charBudget: …]` when the budget was what bit, and `[also in this document, not matching this
topic: …]` when the section was dropped for irrelevance and no larger budget will bring it
back.

The response reads, in order:

1. the provenance line
2. a count line naming the technology asked for and the status that answered — `1 standard
   for technology 'csharp' (status CURRENT).`
3. a `###` heading with the standard's title, then the identity block — `path:`, `id:`,
   `doc-type:`, `status:`, `component:` — and a `technology:` line echoing the corpus value
   verbatim, not the caller's spelling. The declared value is what a later lookup is defined
   against, so echoing the request would hide a standard that answers under a different
   name. A standard declares no `component` by ontology design, so that field reads
   `component: (none)`. The `doc-type:` is the ontology's own spelling rather than the enum
   member's name, so a coding standard reads `doc-type: CODING-STANDARD` — the value a
   reader can look up, not `CODINGSTANDARD`, which no document or rule id uses.
4. the rule-id index
5. the chosen sections, with the `[complete document]` marker when nothing was omitted
6. nothing else — a coding standard declares no `code-refs` by ontology design, so the
   response never carries code joins.

**The rule-id index is emitted ahead of the prose.** It lists the `KW-*` ids the standard
states, deduped: the ids its section headings and bodies cite, in document order — the order
the standard itself reads — then any id its frontmatter title states that no section repeats.
An id stated in both appears once, at the position its section gave it. The title's ids come
last rather than first, though the title precedes the body in the file, because the 50-id
window below is a window over the body: a title id placed at the front of it would let
frontmatter displace a section id from what the caller actually reads. The ids are what a
finding cites, a suppression names, and the repository's own documentation indexes, and
clipping the prose is recoverable by asking again with a larger budget while losing the ids
is not, so the index is placed where a budget can never reach it. A standard that states no
ids gets an honest empty line — `rule ids: (this standard states no rule ids)` — rather than
an absent one, which would read as ids the tool failed to find. Both standards in this
repository state no ids today.

The index is capped at 50 ids. A standard that states more names the first 50 and then
reports how many more it states; an id past the cap is replaced with `[rule id withheld]`
everywhere the response echoes the document's own text — the headings and bodies of the
returned sections, the title echoed in the identity block, and the headings the omission
markers name, whichever of the two markers carries them. The same replacement, never a
deletion, so a reader sees that an id was cited rather than that a sentence lost a word. The
identity block's `path:`, `id:` and `technology:` lines print as they stand, because each is
a key the caller reuses in a later call and none can hold a rule id. So the cap the response
states is a cap it keeps, and an omitted id cannot leak further down and contradict the count.

**A standard whose status is not `current` still answers**, carrying a warning line above
its identity block that names the status. A caller about to follow a draft has to know it is
not the settled answer, and a status field two lines down is the part of a response a reader
skims past.

Four replies are not a standard at all:

- A **miss** — a technology no standard answers for — says so, names the technologies this
  repository does declare, and reports how many documents were considered. That is a real
  miss: the answer for that technology is not written down here, not a lookup that failed
  quietly.
- An **empty or whitespace `technology`** is a caller error rather than a lookup, and returns
  usage prose naming both accepted key forms.
- A standard whose body carries **no `##` headings** is reported as such in place of the
  prose, below the identity block that names its path, rather than handed back as an identity
  block followed by nothing: `no sections: this standard's body carries no ## headings, so
  there is nothing to excerpt from it.`
- A standard that **has** headings but none of them matching the caller's `topic` is the other
  case, and must not be read as the one above. It reports
  `no section matches the topic '<topic>'` and then names the headings the standard does carry,
  with the relevance marker a partially returned excerpt uses —
  `[also in this document, not matching this topic: <headings>]`. The body does carry sections,
  so this reply never claims otherwise, and no prose comes back: the topic selected nothing, and
  a larger `charBudget` cannot fit a section the topic did not select.

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

### `docs_validate(mergeReady = false, base = "origin/HEAD", charBudget = 12000)`

Runs the same schema conformance gate as [`kyber-weave docs validate`](governance.md) against
the corpus the server already bound at startup. There is **no repository-path argument**: the
root is `DocumentIndexHost.RepositoryRoot` for this process, disclosed by the provenance line
on every response. Compare that `root=` to `git rev-parse --show-toplevel` before trusting a
pass or fail.

`mergeReady` maps to the CLI's `--merge-ready`. Default `false` is the **planning gate** —
the check an architect runs while a plan still sits in its active folder. With
`mergeReady: false`, open-work rules such as `KW-DOC-LIFECYCLE-003` do not run; with
`mergeReady: true`, they do, so a branch that still carries an active plan fails until the
plan is archived. Pull-request CI passes `mergeReady: true`; in-progress plan work on a branch
must leave it `false`.

`base` is consulted only when `mergeReady` is `true`. It matches the CLI default
(`origin/HEAD`) and selects archive-closeout scope the same way [`docs validate
--merge-ready`](governance.md) does.

`charBudget` caps **finding text** in the body (default 12000, clamped 1000–120000). The
provenance line and verdict line are always emitted whole and never count toward the budget.

The response reads, in order:

1. the provenance line
2. `result: pass` or `result: fail` with error and warning counts
3. findings — errors and criticals before warnings, each with rule id, severity, path, line,
   message, and hint when the gate supplies one

The verdict line is never truncated. When the budget omits later findings, the body ends with
an omitted count so the caller knows how many more exist.

The document walk reads the tree on disk, not `git ls-files`, so an untracked plan file under
the active plans folder is visible to this gate the same way it is to the CLI.

### `docs_drift(charBudget = 12000)`

Runs the same entity-drift gate as [`kyber-weave docs drift`](governance.md) against the
server's resolved repository root — again with no path argument and the same provenance
contract as [`docs_validate`](#docs_validatemergeready--false-base--originhead-charbudget--12000).

`charBudget` uses the same default, clamp, and truncation rules as `docs_validate`. Response
order matches: provenance, `result: pass` or `result: fail` with counts, then findings with
errors and criticals before warnings.

Warnings such as `KW-DOC-DRIFT-003` do **not** fail the gate; they still appear in the
findings when present. A missing or unreadable CodeGraph index is reported as critical
`KW-DOC-DRIFT-001` with the existing "Drift cannot be verified" message — not as `result:
pass`.

### `docs_status()`

Reports the provenance of the corpus this server is answering from, with no query and no
arguments. This is the cheap session-start check: call it once before trusting any other
docs tool, and compare its `root=` to `git rev-parse --show-toplevel`. It returns exactly
the provenance line above — nothing else — so an empty corpus is not a failure: the line
still reports the root, a count of `0`, and unavailable revision markers when Git cannot be
read.

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
| `agent_coding_standards` reports no standard for a technology | The key names no technology this corpus declares a standard for — check the Config Reg block of the repository root [`AGENTS.md`](../../AGENTS.md) and `ontology.technologies` in [`.kyber-weave/kyber-weave.yml`](../../.kyber-weave/kyber-weave.yml). Spelling alone cannot be it: the match is case-insensitive, so a miss means the technology is undeclared, or is declared with no standard scaffolded at `docs/standards/<technology>/README.md` |
| Client reports a protocol error | Something wrote to stdout; check that you launched `kyber-weave-mcp`, not `kyber-weave` |
| Answers look right but named files do not exist, or the document count is wrong | A same-named server is bound to a different checkout — compare the `provenance: root=` line to `git rev-parse --show-toplevel`, call `docs_status`, and pin the server with `--expect-root` |
| Piped requests exit 0 with nothing on stdout | A release up to `v0.1.7-rc.13` dropped replies once stdin closed — upgrade, or keep stdin open until the replies arrive. See [Closing stdin](#closing-stdin) |

## Related

- [Retrieval and ranking](retrieval.md) — how results are chosen and budgeted
- [Documentation analysis and review](analysis.md) — findings, review exchange, cache, and glossary
- [DocGraph architecture](architecture.md) — the index behind these tools
- [Installing Kyber-Weave](../install.md) — getting the binary on PATH
