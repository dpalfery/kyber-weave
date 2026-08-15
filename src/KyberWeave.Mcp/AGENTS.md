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
nearest ancestor containing `.git`, then the working directory. Guessing wrong yields an
**empty corpus rather than an error**, which is the failure to suspect first when every
query returns a miss.

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

## Staleness is handled for you

`DocumentIndexHost.Current()` rebuilds whichever half has gone stale on each call. Do not
add caching in the tools layer.

## Documented behaviour

The tool contract, client wiring, and troubleshooting live in
[`docs/docgraph/mcp-runbook.md`](../../docs/docgraph/mcp-runbook.md). Changing a tool name,
parameter, default, or cap means updating that page in the same change.
