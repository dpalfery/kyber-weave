---
id: adr/0061-the-docs-server-refreshes-codegraph-and-fails-its-tools
title: The docs server refreshes CodeGraph and fails its tools
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-07
component: DocGraph
decided-by: []
supersedes: []
code-refs:
  - CodeGraphFreshness
  - CodeGraphProvisioner
  - CorpusProvenance
  - DocsTools
  - McpConfig
---

# ADR 0061: The docs server refreshes CodeGraph and fails its tools

## Status

Accepted, 2026-11-07. Applies where a repository declares `codegraph` under
`mcp.required-servers`; other repositories keep the behaviour they had. Amends
[ADR 0046](0046-docs-explore-section-answer.md)'s response contract only by adding the provenance
fields and the failure and note blocks; the section-answer shape is untouched.

## Context

The docs tools answer with code joins, and those joins come from `.codegraph/`. The server never
wrote that index, so it answered from whatever index happened to be on disk at whatever revision it
happened to describe. For an agent with a shell that is survivable: it can refresh the index itself.
For an agent with **no** shell — `architect`, `review-lens`, `review-triage`, the analyst roles — the
server was the only way to reach the code graph, and a stale index was invisible in the answer. The
joins simply omitted what the index did not know, and an answer with joins missing reads exactly
like a complete one.

Two constraints shaped the answer. The server is the one component every role reaches, so it is the
right place to keep the index fresh. It is also a read-only corpus service in every client's
configuration, and `kyber-weave doctor --check` promises it never runs `init` or `index` — so
whatever the server does has to remain honest about both.

## Decision

1. **The server refreshes the index itself, on tool calls.** On the first tool call of the process,
   then at most once every 60 seconds — an interval held in a constant, not a setting — it runs
   `codegraph sync` when an index exists and `init` when it does not, reusing
   `CodeGraphProvisioner` and its cross-process lock rather than adding a second way to start a
   process. The refresh is bounded at 30 seconds.
2. **The lock is taken with zero wait.** A tool call cannot block for the provisioner's 600-second
   index timeout, so the server tries once.
3. **A busy lock is a note, not a failure.** Another process holding the refresh lock means someone
   is already refreshing the index — a worker's `doctor --ensure`, the CodeGraph daemon — so the
   answer proceeds with one `note:` line above the body, saying the index is being updated and the
   answer uses it as it stands. A refresh that fails while the lock is held is treated the same
   way, because the lock is the one honest witness that the failure is contention and not
   breakage. Contention must never be reported as a broken index, or the note becomes noise a
   caller learns to ignore.
4. **A real failure fails the tools.** A missing binary or a failed `sync` returns
   `STATUS: TOOL_FAILURE` with `tool:`, `reason:` and `fix:` lines, and nothing else, under the
   default stop rule. The token is a whole line so a passing mention cannot satisfy an orchestrator
   that routes on it. Under `stop-on-tool-failure: false` the same detail arrives as a `warning:`
   line above an answer that comes from the documents on disk, with the consequence named: code
   joins may be missing.
5. **Two deliberate exceptions.** `docs_get_source_repository_and_revision` **always** answers,
   because it is the tool a caller reaches for once something else has failed.
   `docs_detect_drift_between_documents_and_code` answers **with** the failure, because its verdict
   is the CodeGraph-failure report the caller asked for; blocking it would replace the findings with
   a generic block. The other six tools return the failure block.
6. **The tools write a cache and stay read-only for documents.** The only bytes they write are
   `.codegraph/`, which is gitignored. The attribute every tool carries is `ReadOnly` and
   `OpenWorld = false`, and this decision does not make it false: a client that treats the server as
   a read-only corpus service is still correct about documents.
7. **Nothing reaches stdout.** The refresh runs through the repository's process runner, which
   captures both streams. Stdout is the JSON-RPC transport, and this feature is the one that could
   have corrupted it from inside a tool call.
8. **Applied only where the repository declares the server.** The refresh and the failure apply only
   when `codegraph` is among the `mcp.required-servers` in the bound repository's configuration. A
   repository that never declared it takes no index lock, reports no failure and no warning, and
   answers exactly as it did before this existed — it has claimed no dependency on an index.
9. **Settings are read at each refresh**, so a change to `stop-on-tool-failure` or to the
   declaration takes effect without restarting the server.
10. **`--no-codegraph-refresh` disables it.** `kyber-weave doctor --check` starts the server with
    that flag, which is how its promise that it never runs `init` or `index` keeps holding through a
    probe that makes a real docs call.
11. **Every response's provenance names the branch and the repository.** The header is
    `provenance: root=… branch=… repo=… rev=… dirty=… documents=N`, where `repo=` is the git
    directory every worktree of the repository shares. The pair exists for one distinction
    `root=` cannot make: a second worktree of this repository has a different root and the same
    `repo=`, while a foreign repository differs in both. Comparing `repo=` therefore answers "the
    right repository?" and `branch=` answers "the right revision?".

## Alternatives Considered

- **Refresh once at startup.** Rejected. A long session's index goes stale exactly when it matters,
  and a server started before the branch was created describes neither the branch nor its files.
- **Refresh on every tool call.** Rejected. A `sync` per call pays a child process and the index
  lock for every question, which is the cost this exists to avoid paying repeatedly; the interval
  bounds it without making freshness arbitrary.
- **Exit the process when the index cannot be refreshed.** Rejected. The client then reports a
  server that will not start, with no reason and no remedy; a failing tool at least says what is
  broken and how to fix it.
- **Run `codegraph index` at every server start.** Rejected. A full rebuild per start is the
  expensive verb applied to the routine case, and it would make the read-only promise impossible to
  keep in any form.
- **Keep the server read-only and let a shell role refresh for everyone.** Rejected. It is precisely
  the shell-less roles that have no other route, and the measured run's wrong answers came from an
  index nobody refreshed.
- **Fail every tool, including `docs_get_source_repository_and_revision`.** Rejected. A caller then
  has no way to see which corpus or branch answered, so the failure cannot be diagnosed from the
  client at all.
- **Make the refresh apply everywhere, unconditionally.** Rejected. A repository with no interest in
  CodeGraph would start taking index locks and reporting failures for a tool it never asked for.
- **Report contention as a failure.** Rejected. A worker's own `--ensure` would then make the docs
  server refuse to answer, which turns correct operation into an outage.

## Consequences

- A shell-less role now gets answers from an index the server keeps fresh, up to a minute old, and
  it learns when the index cannot be read instead of silently getting joins-less answers.
- A tool call can take up to 30 seconds longer once every 60 seconds while a refresh runs, and
  longer never.
- The server writes `.codegraph/`. An operator who must keep the tree untouched starts it with
  `--no-codegraph-refresh`.
- Six of the eight tools can now answer with a failure block instead of an answer. A client that
  routes on `STATUS:` sees a stop; a client that reads prose sees the reason and the fix.
- The provenance header is wider than it was, so a client that compares the whole line to a stored
  value must compare fields. `McpLiveProbe` reads the new fields so `mcp.root` still catches a
  server answering from another checkout.
- A worktree created after the server starts is not covered: the refresh belongs to the root the
  server is bound to. [ADR 0060](0060-code-editing-runs-stop-on-unusable-tools.md) carries that
  consequence into the per-role rules.

## Related

- [ADR 0060: code-editing runs stop on unusable tools](0060-code-editing-runs-stop-on-unusable-tools.md) — the run-start half of the same failure, including `doctor --ensure`
- [ADR 0051: the .NET CLI spawns stdio children through one bounded session](0051-dotnet-cli-spawns-stdio-children-through-one-bounded-session.md) — the bound discipline the refresh follows
- [ADR 0046: docs_answer_question_from_repo_documentation returns one section answer](0046-docs-explore-section-answer.md) — the response shape the note and failure blocks sit beside
- [DocGraph architecture, the server keeps the index fresh](../docgraph/architecture.md#the-server-keeps-the-index-fresh-and-fails-its-tools-when-it-cannot)
- [MCP runbook, the server refreshes CodeGraph](../docgraph/mcp-runbook.md#the-server-refreshes-codegraph-and-its-tools-fail-when-it-cannot)
- [MCP runbook, provenance on every answer](../docgraph/mcp-runbook.md#provenance-on-every-answer)
- [Configuration, stop on tool failure](../configuration.md#stop-on-tool-failure)
