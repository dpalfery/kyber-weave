---
id: adr/0061-code-editing-runs-stop-on-unusable-tools
title: Code-editing runs stop on unusable tools
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-07
component: KyberSquad
decided-by: []
supersedes: []
code-refs:
  - CodeGraphProvisioner
  - DoctorMode
  - KyberWeaveConfig
  - RepositoryDoctor
---

# ADR 0061: Code-editing runs stop on unusable tools

## Status

Accepted, 2026-11-07, by the owner's explicit decision. **This reverses a documented rule**: the
conductor and `bug-crusher` both said "warn and continue; never block a run on it", and the
Kyber-Squad architecture recorded a read-only run-start check whose failure was one line in the
run record. Those statements are replaced rather than qualified. It does not change what "required"
means for a tool, the CodeGraph install decision, or any renderer's grant.

## Context

A run-start `doctor --check` detected the CodeGraph binary, the index and the docs MCP server, and
reported the result. Whatever it reported, the run continued: the recorded rule was that a failed
check is one line and never a stop. The measured conductor run showed what that costs. Subagents on
Command Code had no code search at all, because no renderer granted the `codegraph` server and
Command Code gives a subagent only the tools its file names, so every code question degraded to
grep. The docs server answered from the main checkout, which in that run held another branch, so a
worker in a worktree read the wrong code: `codegraph explore "StoredDeploymentIdentity"` found a
method added on the worktree's branch in 0.37 s from the worktree and did not find it from the
canonical checkout. Neither defect failed anything. A run whose code search is unusable produced
findings that read exactly like findings from a run whose code search worked, and the only record of
the degradation was one advisory line nobody halted on.

Two things follow. A tool the work depends on has to be usable before work starts, and the
decision to proceed without it has to belong to the repository rather than to an agent's judgement
of a diagnostic line — an agent cannot tell a degraded answer from a complete one, and there is no
reason to expect it to try.

## Decision

1. **A required tool's failure stops a code-editing run.** The run stops before any worker is
   dispatched, with `STATUS: TOOL_FAILURE` and the doctor's `fix:` line, and nothing is
   dispatched. The existing escalation route goes to the architect and is the wrong shape here:
   there is nothing to mediate, only a tool to repair.
2. **`stop-on-tool-failure` is a top-level boolean in `.kyber-weave/kyber-weave.yml`, and absent
   means stop.** `false` reports the failure and continues. A value that is neither `true` nor
   `false` is a configuration error naming the setting and its two values, the treatment
   `autonomy:` already gets, because a binding failure names a type an operator cannot see. The
   setting is deliberately **not** published in the generated `AGENTS.md` Config Reg block: the
   readers are the CLI and the MCP server, not a portable instruction file.
3. **`kyber-weave doctor --ensure` is the one command a worker runs first**, in the tree it will
   work in. It runs `codegraph sync` when an index exists and `codegraph init .` when it does not,
   restores any tracked file that preparation rewrote, proves the index with one
   `codegraph explore` call and the docs server with one call, and repairs once with
   `codegraph index .` followed by one retry when a step fails.
   - **The restore never touches a file that was already dirty.** The set of dirty tracked files
     is recorded before the index work starts; reverting the operator's own edits would be
     unrecoverable data loss disguised as housekeeping.
   - **`index` is the repair step, never the routine one.** `--provision` changes from always
     indexing to `sync` when an index exists, because a full rebuild on every provision pays for
     new information it does not produce.
4. **The doctor reads the setting itself, so no agent judges the result.** Under the default a
   failed `--ensure` exits `1`; under `false` the same checks are reported as warnings and the exit
   is `0`. A missing binary stays exit `3` under either value: installing CodeGraph is the user's
   decision, in every mode, on every path.
5. **The stop rule's boundaries are part of the decision.**
   - **A read-only run does not stop.** Reviewers, lenses and the architect keep working without
     an index; a lens that cannot read its packet reports `SKIPPED` with the reason.
   - **A repository with no governed docs skips the check entirely.**
   - **An autonomous run stops too.** It does not downgrade the failure to a pending-human item: a
     run with no code search produces a report nobody can trust, and parking it defers the same
     decision to a human who now has less information.
   - **`doctor --check` never applies the setting.** A caller that asked for a verdict gets one.
6. **Every worktree indexes itself.** A worktree does not share the canonical checkout's index; it
   runs `doctor --ensure` as its first command, at about 8 seconds and about 223 MB until cleanup
   removes it. This replaces the earlier conditional rule, which indexed a worktree only when a
   task's acceptance criteria asked for it — the shared index had been measured answering for
   another branch, so the conditional rule's default was the wrong default. The ban on symlinking
   `.codegraph/` into a worktree stands.
7. **The MCP servers stay bound to the canonical checkout, and the per-role rules carry the
   consequence.** A worker with a shell asks its own index with `codegraph explore` in that shell;
   a worker with no shell is given the worktree's absolute path in its packet and reads it
   directly; the architect returns `BLOCKED` naming "server root differs" so the two CLI docs gates
   run in the worktree before it plans. Every docs response gains `branch=` and `repo=` in its
   provenance line, and the comparison rule becomes **compare `repo=`, not `root=`**: a different
   `root=` with the same `repo=` is another worktree of this repository, and only a different
   `repo=` is the wrong repository.
8. **Prevention comes first.** A run branches in place when the checkout is clean and not in use,
   which keeps the docs server, the architect and the workers on one revision; a worktree is for a
   dirty or busy checkout and for parallel workers. The run-start command that performs this is
   delivered by the run-start slice and is not in this repository yet, so the rule above stands and
   the automation follows it. A session started inside a worktree is already
   correct, because `--repo-root .` resolves there.

## Alternatives Considered

- **Warn and continue, and make the warning louder.** Rejected. This was the rule in force, and
  the measured run is the evidence: the warning appeared and nothing changed. A line an agent may
  read and proceed past is a line a degraded run can pass through.
- **Stop only in supervised mode, and park the item for human review under `autonomy: autonomous`.**
  Rejected. Parking defers the same decision to a human, with less information than the run had at
  start, and the run in the meantime produces findings from a code index nobody trusts.
- **Stop on every failed doctor check, including read-only runs.** Rejected. A reviewer with no
  index still reviews, and stopping a read-only run turns a missing index into a total outage
  rather than a degraded review that says what it could not read.
- **Let the agent decide from the doctor's output.** Rejected. An agent cannot distinguish a
  degraded answer from a complete one — that is the whole failure — so the decision moves into the
  command, where it is one boolean read once.
- **Keep `codegraph index .` as the routine provisioning verb.** Rejected. It rebuilds the whole
  index on every run for no new information; `sync` brings an existing index up to date, and
  `index` remains available as the repair step with a bounded budget of one.
- **Keep the conditional worktree indexing rule.** Rejected. Its default was to share the canonical
  index, which is exactly the measurement that produced wrong answers.
- **Widen `context7` at the same time, since a specialist could use both.** Rejected as out of
  scope: the measured failure was the code graph, and widening a third-party documentation server
  is a separate decision for a separate reason.

## Consequences

- A repository with governed docs whose CodeGraph cannot be repaired **stops code-editing runs**
  until it installs CodeGraph or sets `stop-on-tool-failure: false`. That is the intended cost, and
  it is the only setting that changes it.
- Every worker start pays the `--ensure` cost: a `sync` is about 8 seconds. Every new worktree also
  pays about 223 MB of disk until cleanup removes it, and parallel workers multiply that.
- The rule does not leak: read-only runs never stop, and a repository with no governed docs never
  runs the check.
- `codegraph` is no longer a routine full rebuild. A change that needs one runs it explicitly, and
  `--ensure` runs it at most once, only after a failure.
- The conductor and `bug-crusher` instructions carry the rule as prose, and
  `SquadCanonicalContentTests` pins the stop wording rather than the "warn and continue" wording it
  used to pin. The reversal is the owner's decision, dated 2026-11-07, and a future reader finding
  the old sentence is reading text that no longer ships.
- Work still open: [issue 221](https://github.com/dpalfery/kyber-weave/issues/221), a read-only
  fact-finder for a shell-less architect, stands. A worktree created after the docs server starts
  is outside that server's reach.

## Related

- [ADR 0062: the docs server refreshes CodeGraph and fails its tools](0062-the-docs-server-refreshes-codegraph-and-fails-its-tools.md) — the server-side half of the same failure
- [ADR 0050: the kw-docs MCP server reaches every entitled agent](0050-universal-kyber-weave-mcp-grant.md) — amended in place: the code graph is granted through the shared predicate, and the orchestrator stays excluded
- [ADR 0051: the .NET CLI spawns stdio children through one bounded session](0051-dotnet-cli-spawns-stdio-children-through-one-bounded-session.md) — the bound on the doctor's MCP proof
- [Kyber-Squad architecture, run-start documentation check](../kyber-squad/architecture.md#run-start-documentation-check)
- [MCP runbook, the worker's first command](../docgraph/mcp-runbook.md#the-workers-first-command-doctor---ensure)
- [Configuration, stop on tool failure](../configuration.md#stop-on-tool-failure)
