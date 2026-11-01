---
id: adr/0049-dotnet-cli-spawns-stdio-children-through-one-bounded-session
title: "The .NET CLI Spawns Long-Lived Stdio Children Only Through One Bounded Session"
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-31
component: DocGraph
code-refs:
  - StdioSession
  - ProcessRunner
  - McpLiveProbe
  - CodeGraphProvisioner
---

# ADR 0049: The .NET CLI Spawns Long-Lived Stdio Children Only Through One Bounded Session

## Status

Accepted, 2026-10-31. Records the decision behind `doctor --check`, the live MCP proof in
`KyberWeave.Core/Doctor`, and the session type in `KyberWeave.Core/Processes/StdioSession.cs`.
It is the .NET counterpart of [ADR 0047](0047-kyberdash-self-spawns-through-one-guarded-chokepoint.md),
which covers KyberDash's TypeScript self-spawns and does not reach the .NET CLI.

## Context

`ProcessRunner.Run` is the one bounded spawn path in the .NET CLI. It starts a child, writes all of
its standard input, closes that input, and then waits for the child to finish and captures what it
wrote. That fits a one-shot command such as `--version` or `git rev-parse`. It does not fit a
JSON-RPC server over stdio, which needs a request, then a response, then the next request, over one
process that stays alive between them. The live docs MCP check needs exactly that conversation.

Before this decision the .NET CLI had no spawn site that held a child open for two-way traffic. The
check needed one, and the question was how to add it without a second, weaker spawn path.

The existing one-shot spawn path, `ProcessRunner.Run`, already has these safety properties: the start information is built
with no shell, the process tree is killed on timeout, and no environment is echoed into a failure.
A two-way child can hang on a read, and a read that is never answered is the common failure, so a
session that only closes stdin on exit would leave a sleeping server running behind the CLI.

## Decision

A .NET process that starts a child which is read and written line by line goes through
`StdioSession` in `KyberWeave.Core/Processes`, and through no other type. Concretely:

- `StdioSession` builds its start information through `ProcessRunner.CreateSafeStartInfo` and kills
  the child's process tree through `ProcessRunner.TryKillTree`. It does not build a second start path.
- **Kill on every exit path.** `Dispose` kills the tree whether or not the child has already exited,
  waits up to five seconds for the kill to be reaped, and then releases the streams. Callers hold the
  session in a `using` block, so success, a timeout, a closed stream and an exception all reach
  `Dispose`. A stuck server returns no line, so a dispose that only closed stdin would not end it.
- **No orphan.** A run that leaves a child behind is a failed run. The contract is that no process
  outlives the CLI, not that the CLI usually cleans up.
- **One total budget per session.** Every read waits only for what is left of the budget, so a server
  that answers slowly cannot extend the run past it. After a timed-out read the session refuses
  further reads and must be disposed.
- **The budgets.** The MCP session has a 20-second total budget (`--mcp-timeout`). A CodeGraph
  `init` or `index` child is bounded by 600 seconds (`--index-timeout`), and the executor that runs
  it kills the tree when that bound is reached. A second doctor waits for the same 600 seconds for the
  index lock before it reports `codegraph.index busy`.
- **Stderr is drained for the child's whole life**, so a chatty child cannot fill its pipe and block.
  Only its first non-empty line is kept, truncated, and shown in a failure reason.

`ProcessRunner.Run` stays the path for one-shot capture. `StdioSession` is the path for a child that
must be answered between writes.

## Alternatives considered

1. **Raw `System.Diagnostics.Process` at the call site.** Rejected. Each caller would rebuild the
   start information and the kill-tree rule. The first caller that forgot to kill on a timeout would
   leave an MCP server running, and nothing in the type system would show it. Keeping the rule in one
   type makes the no-orphan property reviewable in one file.
2. **Reuse `ProcessRunner.Run` for the conversation.** Rejected. `Run` writes all input and closes it
   before it waits. A JSON-RPC server that reads one request, answers it, and then reads the next
   would see end of input after the first request and never receive the second. Changing `Run` to
   keep stdin open would change every existing one-shot caller, which all rely on the close, to
   support one new caller.
3. **Parse the harness configuration and skip the live session.** Rejected by the plan (D1). It proves
   what a configuration file says, not whether the server answers for this checkout.

## Consequences

- The CLI has two spawn paths in `KyberWeave.Core/Processes`, and both share the start-information
  rule and the kill-tree rule. A new two-way child goes through `StdioSession`, and a reviewer can
  reject a second `Process` that holds stdin open.
- `StdioSession` is the first .NET spawn site that is held open across requests. Its test
  (`StdioSessionTests`) pins that a dispose on every path leaves no child running.
- `ProcessRunner.TryKillTree` is `internal`, so only code in `KyberWeave.Core` can call it. The kill
  rule is not enforced by the compiler across assemblies; review is what keeps a second spawn site honest.
- The budgets are flags, not environment variables, which matches the watchdog's rule that caps change
  by flag only. A caller that needs a larger budget passes it explicitly.
- ADR 0047 and this ADR describe the same invariant on two runtimes. Neither replaces the other.

## Related

- [DocGraph MCP runbook](../docgraph/mcp-runbook.md#the-live-check-doctor---check) — the check that uses the session
- [CLI command surface](../cli-command-surface.md#doctor--repository-documentation-tooling) — the `doctor` verb and its exit codes
