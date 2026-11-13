# KyberWeave.Mcp

The `kyber-weave-mcp` binary. A stdio MCP server exposing the documentation graph to
agents.

Read [`/AGENTS.md`](../../AGENTS.md) first for repository-wide rules.

## Never write to stdout

**stdout is the JSON-RPC transport.** One stray line corrupts it and the client reports a
protocol error with no useful detail.

- No `Console.WriteLine`, no `AnsiConsole`, no `Console.Out` — anywhere in this project or
  in a code path it reaches.
- Logging is pinned to stderr in `Program.cs` via `LogToStandardErrorThreshold =
  LogLevel.Trace`. Do not relax it.
- This is why the server is a **separate executable** from the CLI rather than a
  `kyber-weave mcp` subcommand: the CLI is built on Spectre.Console, which writes to
  stdout. Separate entry points make the corruption structurally impossible instead of a
  matter of discipline. Do not merge them.

## Composition root

`Program.cs` constructs the single `DocumentIndexHost` and hands it the factories Core
needs. Tools receive it by injection. Core never builds these itself — see
[`../KyberWeave.Core/AGENTS.md`](../KyberWeave.Core/AGENTS.md).

Repository root resolution order is `--repo-root`, then `KYBER_WEAVE_REPO_ROOT`, then the
working directory. The working directory is accepted only when it contains a Kyber-Weave
host configuration created by `kyber-weave docs init`; the server never walks up to a parent
Git repository and never serves an unbound default corpus. A missing binding is reported on
stderr with an actionable `kyber-weave docs init` command.

After resolution, an optional client assertion is checked: `--expect-root <path>`, or
`KYBER_WEAVE_EXPECT_ROOT` when the flag is absent, must name the resolved root or the server
refuses to serve — `KW-MCP-ROOT-002` on stderr, exit 1. The flag wins over the environment.
This is the rail against a same-named server pinned to another checkout by a global or
headless harness config; it does not replace correct project-local wiring.

## Tools return text for a model, not data for a program

`DocsTools` formats results as prose an agent reads. Three properties matter when editing:

**Cap everything.** Sections at `SectionCharCap`, joins at `JoinCap`, documents by the
caller's budget. A retrieval tool that returns a whole corpus is a slower grep.

**Say when the answer is weak.** A miss reports that it is a real miss and how many
documents were considered, so the caller knows it may now fall back to grep. An
unresolvable code join prints `(unresolved)`. An ambiguous one is annotated
`(+N other same-named)` so the reader verifies instead of trusting.

**Name what was omitted.** Excerpts list the headings that did not fit, and distinguish
dropped-for-space from dropped-for-irrelevance — only the former makes asking again with a
larger budget worthwhile.

Adding a tool means adding an `[McpServerTool]` method with a `[Description]` written as
routing metadata: a capability clause, then the conditions that should fire it. Only the
broadest tool in the set carries an exclusion; the rest state their territory positively.
Write descriptions in the requester's words ("runbook", "how to run"), not the project's
("governed corpus", "code joins"). The documentation-first rule also lives in
`DocsServerInstructions`, which clients load before the model weighs any tool; keep the two
consistent.
Every tool response must also lead with the shared provenance header — the absolute root, the
branch, the git directory the repository's worktrees share, the short HEAD revision with dirty
marker, and the live document count — so a caller can tell which corpus answered, and about which
revision. `CorpusProvenance` formats it, `docs_get_source_repository_and_revision` returns it alone, and the
header is emitted whole while any budget is taken from the body.

**Compare `repo=`, not `root=`.** A second worktree of this repository has a different `root=` and
the same `repo=`, which is a different checkout of the same repository and a legitimate answer about
another branch; only a different `repo=` is the wrong repository. `branch=` says which revision the
answer describes. Adding a field to the header means teaching `McpLiveProbe`'s trailing-metadata
regex the same field, or `mcp.root` starts failing on a healthy server.

## One root at startup, any worktree per call

Every tool takes an optional `projectPath`. `DocsRootRegistry` resolves it with git to a worktree
of the same repository as the server root (equal common git directory) and refuses anything else;
the per-root objects (document host, analysis reader, CodeGraph schedule) live in a `DocsTools`
built per root, kept in a four-entry least-recently-used set. Omitted means the server root.
A new tool must take `projectPath` and start with `Redirect(projectPath, ...)`.

## Staleness is handled for you

`DocumentIndexHost.Current()` rebuilds whichever half has gone stale on each call. Do not
add caching in the tools layer.

## The server keeps the code graph fresh, and fails its tools

The joins these tools report come from `.codegraph/`, and the server is the only route to them
for a role with no shell. `CodeGraphFreshness` refreshes that index on tool calls: first call,
then at most once every 60 seconds (`RefreshInterval`), under a 30-second bound
(`RefreshBound`), reusing `CodeGraphProvisioner` and its cross-process lock with a **zero** wait
so a tool call never blocks on the provisioner's 600-second index timeout.

- **A busy lock is a note, not a failure.** Another process refreshing means a worker's
  `doctor --ensure` or the CodeGraph daemon got there first: one `note:` line above the body, and
  the answer proceeds.
- **A real failure fails the tool.** `CodeGraphGate.Blocking` returns `STATUS: TOOL_FAILURE` with
  `tool:`, `reason:` and `fix:` lines, and nothing else, under the default stop rule; under
  `stop-on-tool-failure: false` the same detail arrives as the `warning:` advisory instead. The
  token is a whole line so a passing mention cannot satisfy a caller that routes on it.
- **Two tools answer anyway, deliberately.** `docs_get_source_repository_and_revision` always
  answers so a failure can be diagnosed, and `docs_detect_drift_between_documents_and_code` answers
  with the failure because its verdict is the failure report. The other six go through
  `CheckCodeGraph` and return the block.
- **Where it applies.** Only where the bound repository lists `codegraph` under
  `mcp.required-servers` (`McpConfig.DeclaresCodeGraph`). A host that never declared it takes no
  lock and reports nothing. Settings are read at each refresh, so an edit needs no restart.
- **`--no-codegraph-refresh` disables it.** `doctor --check` starts the server with that flag,
  because `--check` promises it never runs `init` or `index` and its probe makes a real docs call.
- **The schedule is opt-in at the tools level.** A `DocsTools` constructed without one answers
  from the documents on disk and consults no CodeGraph. The schedule is a gate as much as a
  refresh, and a CI runner has no index to satisfy one, so a test that builds the tools directly
  must not inherit it. The composition roots pass it explicitly.
- **Nothing reaches stdout.** The refresh runs through `ProcessRunner`, which captures both
  streams. A log or a child's output on stdout corrupts the transport.

The rule and its alternatives are [ADR 0062](../../docs/adr/0062-the-docs-server-refreshes-codegraph-and-fails-its-tools.md).

## Documented behaviour

The tool contract, client wiring, and troubleshooting live in
[`docs/docgraph/mcp-runbook.md`](../../docs/docgraph/mcp-runbook.md). Changing a tool name,
parameter, default, or cap means updating that page in the same change.
