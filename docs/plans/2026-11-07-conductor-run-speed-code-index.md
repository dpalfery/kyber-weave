---
id: plans/2026-11-07-conductor-run-speed-code-index
title: Code index where workers run (conductor run-speed, slice 2)
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
created: 2026-11-07
last-reviewed: 2026-11-07
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - DocGraph
  - KyberSquad
---

# Code index where workers run (conductor run-speed, slice 2)

This plan is **Ready and approved for execution under AP1**, with resolved autonomy `autonomous`. It is the child plan of the parent design for the conductor run-speed work, which lives on its own branch (`claude/docs/conductor-bug-crusher-run-speed`) and is deliberately not in this tree. Per the parent's D12, each slice gets one child plan on the slice's branch, carved from the parent's section and complete on its own: this plan restates only slice 2's decision rows, design, harvest rows, ADR disposition and tasks. The parent design is archived by the last slice's pull request with a mapping to the children; this plan is archived in this slice's own pull request.

## Problem and goal

The parent design's measured run showed two defects. **Workers start with no working code search**: subagents on Command Code get only the tools their rendered frontmatter names, and no renderer granted them the `codegraph` server, so every code question degraded to grep. **The docs MCP server follows the main checkout**: in the measured run it served another branch, so a worker in a worktree read another branch's code — `codegraph explore "StoredDeploymentIdentity"` found the worktree's new method from the worktree in 0.37 s and did not find it from the main checkout, whose shared index returned the wrong code. On top of both, **a failed tool passed as a warning**: the run-start doctor check warns and continues, so a run whose code search is unusable proceeds as if it were healthy.

The goal of this slice: make CodeGraph work in the tree the worker is actually in, and make the failure of a required tool stop the run instead of passing as a warning.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What happens when CodeGraph or `kw-docs` cannot be repaired? | A project setting `stop-on-tool-failure` in `.kyber-weave/kyber-weave.yml` defaults to stop. A code-editing run stops before dispatching workers. Autonomous runs stop too. `false` warns and continues. | The name is confirmed by D33. Read-only runs do not stop, and a repository with no governed docs skips the check. | user: explicit decision 2026-11-07 |
| D8 | Where does a run start from? | Every run fetches origin first and branches directly from the remote base branch, without checking out `main`. Resuming a plan or pull request keeps its branch. | Command in D21. | user: explicit decision 2026-11-07 |
| D10 | When is the CodeGraph index refreshed? | At the start of every architect, developer and review task. | Who does it for each role is in the worktree table under fix 1. | user: explicit decision 2026-11-07 |
| D11 | Who refreshes CodeGraph for roles with no shell? | The `kw-docs` MCP server refreshes CodeGraph itself, and its docs tools fail when CodeGraph fails. Form: use `sync`, run on tool calls rather than at startup, fail the tools rather than the process, and apply only where CodeGraph is declared. | Details in D23. | user: explicit decision 2026-11-07 |
| D12 | Can one plan span several pull requests? | **No. Several plans, one per slice.** This file stays the single approved design and never merges while open. On approval it stays on its own branch. Each slice gets a child plan on the slice's branch, carved from its section here, and that plan is archived in the slice's pull request. The last slice's pull request brings this file in and archives it with a mapping to the children. | `OpenWorkValidator` raises `KW-DOC-LIFECYCLE-003`, and `ci.yml:733-757` runs `docs validate --merge-ready` on every pull request. A plan left open in `docs/plans/` fails CI, and the rule exists because a specification merged in stages never got its closeout. Its message says "keep it on its branch". Changing the rule was rejected for that reason. Child plans restate only their slice's decision rows, so each is complete on its own. Approval here carries to each child through `AUTONOMOUS_APPROVAL`. | user: confirmed 2026-11-07 — the owner confirmed the autonomous recommendation |
| D13 | Does the requested build order hold against real dependencies? | **Five changes.** (a) Fix 5's one-line test repair moves to the very front, because a failed blocking gate forces `REQUEST_CHANGES`, so no slice's council can approve on the owner's machine until it lands. (b) The `stop-on-tool-failure` setting moves to the fix 8 slice, because fix 8's fetch failure reads it. (c) The `branch=` and `repo=` provenance fields move from fix 4 to fix 1, because the worktree rule needs them. (d) Fix 5's remaining work goes after fix 2, because it edits the shared execution skill fix 2 creates. (e) The `github-actions` standard is declared in the first slice, because `github-devops` refuses to write a workflow without it. Fixes 6 and 10 land with fix 3: fix 6 adds a field to the task file, and fix 10 shares fix 3's detector. Otherwise 8, then 1 to 5, then 11, then 7 stands. | Fix 7 depends on fix 11 (the `Completion` section) and on fix 3 (commands in the prompt). Fix 11 depends on fix 4's topic repair. | autonomous: recommendation accepted |
| D21 | What is the one run-start command? | `kyber-weave run start --harness <token> --type <type> --slug <slug> [--resume <branch-or-pr>] [--failing-version <version>] [--json]`. Exit 0 ready, 1 tool failure under stop, 2 refused input, 3 CodeGraph binary missing. Where the conductor has no shell it dispatches `github-devops` with the exact command and a `RUN-START: true` marker. | `KW-ARB-PLAN-001` escalates a `delegate` with no `PLAN_FILE`, so that dispatch would be blocked on every hook harness. `TriggerClassifier` classifies the marker as `investigate`, which keeps the roster check and `KW-ARB-READONLY-001`. | autonomous: recommendation accepted |
| D22 | What is the command a worker runs first? | `kyber-weave doctor --ensure [path]`: `sync` or `init`, restore any tracked edit `init` made, one `codegraph explore`, one `kw-docs` call, repair once with `codegraph index`, retry once. It reads `stop-on-tool-failure` itself, so no agent judges the result. | `RepositoryDoctor` already takes a path and resolves its git toplevel, so it works inside a worktree. `doctor --provision` changes from `index` to `sync` when an index exists (`CodeGraphProvisioner.cs:172`). `index` stays only as the repair step. | autonomous: recommendation accepted |
| D23 | How does the docs server refresh safely? | First tool call, then at most once every 60 seconds (the interval is a suggestion, tunable by a constant). A 30-second bound. Lock busy means another process is refreshing, so answer with a one-line note, not a failure. Binary missing or `sync` failure: the tool returns `STATUS: TOOL_FAILURE` with the reason and fix under stop, or a warning line under `false`. `docs_get_source_repository_and_revision` always answers, so the failure can be diagnosed. `kyber-weave-mcp --no-codegraph-refresh` lets `doctor --check` keep its promise. | `AcquireLock` waits up to `IndexTimeout`, 600 seconds by default, far too long for a tool call, so this path takes a zero-wait lock. `doctor --check` promises it "Never runs init or index" but `McpLiveProbe` makes a real docs call. Child output cannot reach stdout because `ProcessRunner` captures it. `.codegraph/` is gitignored, so the tools stay read-only for documents. | autonomous: low-confidence, no authoritative source |
| D24 | How is a server bound to the main checkout handled when a worker is in a worktree on another branch? | See "The worktree limitation" under fix 1. Shell roles query the worktree's own index with `codegraph explore`. Shell-less roles get the worktree path in their packet and read it directly. Run start branches in place when the checkout is clean, so the server and the work align. Provenance gains `branch=` and `repo=`, and agents compare the repository, not the path. [Issue 221](https://github.com/dpalfery/kyber-weave/issues/221) stays open. | The canonical binding is deliberate and stays. A session started inside a worktree is already covered, because `--repo-root .` resolves there. | autonomous: recommendation accepted |
| D25 | Does a worktree still share the main checkout's index? | No. Every new worktree indexes itself with `doctor --ensure` at about 8 seconds. This replaces "Conditional worktree indexing". The ban on symlinking `.codegraph/` stays. | The shared index returned the wrong code: `codegraph explore "StoredDeploymentIdentity"` found the new method from the worktree in 0.37 s and did not find it from the main checkout. Disk cost is about 223 MB per worktree until cleanup removes it. | autonomous: recommendation accepted |
| D26 | Which server does the renderer grant? | `codegraph` only, to agents the existing `GrantsOrchestratorExcludedMcpServers` predicate entitles, so the orchestrator stays excluded. `context7` is not widened here. | Scope is the measured failure. Amends [ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md) in place. | autonomous: recommendation accepted |
| AP1 | Is the Test contract approved, and may this plan execute? | Approved as written, as recommended. Execution follows the parent design's D12. | Not a planning decision. Resolved by the conductor carrying `AUTONOMOUS_APPROVAL: AP1` under resolved autonomy `autonomous`. | autonomous: recommendation accepted |
| D20 | How does this slice read `stop-on-tool-failure` while slice 1's pull request is still open? | This slice carries the setting itself, with the same name, the same type and the same default as slice 1's: `stop-on-tool-failure` is a boolean on the repository configuration, absent means stop, and the resolved value is read as `StopsOnToolFailure`. Slice 1's pull request #538 adds the same setting; when the two pull requests are merged the second one drops its copy and keeps the first, because they are one setting with one meaning. | The setting does not exist on this branch: `rg stop-on-tool-failure src/ .kyber-weave/` returns nothing here and returns the property on slice 1's branch. D22's `doctor --ensure` reads it, and `McpToolFailureTests` writes `stop-on-tool-failure: false` into a scaffolded repository and expects the docs server to honour it, so the setting cannot be deferred to the merge. | autonomous: recommendation accepted |

In D12, "this file" names the parent design on its own branch, not this child plan.

## Investigation findings

Verified against this tree (branch `factory/feat/conductor-run-speed-code-index`, off `origin/main` at `2759e5f5`), not assumed from the packet. Code facts were checked through the CodeGraph index first, then narrowed direct reads only where the index did not cover the file or an exact line citation had to be confirmed.

**Doctor today.** `DoctorCommand` has exactly two modes, `--check` and `--provision` (a bare interactive run provisions; non-interactive acts as `--check`); there is no `--ensure`. `DoctorSettings` describes `--check` as "Never runs init or index" and `--provision` as "init when .codegraph/ is absent, index otherwise"; `--index-timeout` defaults to 600 seconds and is also the lock wait. `CodeGraphProvisioner.cs:172` reads `string verb = Directory.Exists(indexDirectory) ? "index" : "init";` — so D22's change (sync when an index exists) is confirmed as a change, and D23's `AcquireLock` claim (waits up to `IndexTimeout`, verified at `CodeGraphProvisioner.cs:228-259` with a 100 ms poll) is confirmed as the reason the server path needs a zero-wait lock. `RepositoryDoctor` takes a directory and asks git for its `--show-toplevel` (`RepositoryDoctor.cs:90`), so D22's worktree claim holds. `McpLiveProbe` makes a real `docs_answer_question_from_repo_documentation` call (`McpLiveProbe.cs:144-145`), and `TrailingMetadataRegex` (`McpLiveProbe.cs:25`) strips only `rev=`/`dirty=`/`documents=` after `root=` — so D23's flags ("the probe would violate the `--check` promise without `--no-codegraph-refresh`"; "the regex must handle the new fields") and the `mcp.root` check (`:181-207`, compares the served root to the resolved root, so it still catches another checkout) are all confirmed against source.

**Docs server today.** `CorpusProvenance.Line` emits exactly `provenance: root=… rev=… dirty=… documents=…` and nothing else — no `branch=`, no `repo=` — confirmed in source and in every live tool response this session. The `kyber-weave-mcp` project has no CodeGraph integration today (the only mention of `codegraph` in `src/KyberWeave.Mcp` is a doc comment at `DocsTools.cs:19`), nothing reaches stdout (logging is pinned to stderr in `Program.cs`; git runs through `ProcessRunner`, which rebuilds start info and captures both streams), so D11/D23's refresh is entirely new and the stdout rule it must keep is structural. The server's 8 docs tools are the 8 `kw-docs` tools live in this session.

**Renderers today.** `CommandCodeRenderer` always emits an explicit `tools` list ("Omitting it already grants nothing", `CommandCodeRenderer.cs:169-177`) with no MCP names in its tool map — the measured "subagents get only the tools they name" cause is confirmed. `OpenCodeRenderer` and `KiloRenderer` grant only the `kw-docs` permission (`KyberWeaveMcpPermission`, `kw-docs_*`); `PiRenderer` derives its overlay through `GrantsUniversalMcpServers`. `GrantsOrchestratorExcludedMcpServers` exists at `CapabilityDegradations.cs:130` and excludes only the pure orchestrator profile, delegating to the `filesystem.read` entitlement — so D26's grant rides an existing predicate, and the orchestrator stays excluded.

**Repository settings today.** `.kyber-weave/kyber-weave.yml` `mcp.required-servers` declares only `kw-docs`; `codegraph` is present as a commented-out third-party template. There is no `stop-on-tool-failure` key on this branch — per D13(b) the setting is delivered by the fix 8 slice (slice 1), whose pull request is still open.

**Instruction files today.** `conductor.md:62` says "**Warn and continue**; never block a run on it" and `:73` carries "Conditional worktree indexing" — the exact rules this slice replaces (D1, D25). `bug-crusher/SKILL.md:43` says "**Warn and continue.** A failure is a line in the record, never a stop". `autonomy.md:54` is "## Hard stops", where the autonomous stop joins. `AGENTS.md:105-111` is the `codegraph init` authorization and `:117-119` is the `provenance:` `root=` comparison rule that D24 turns into the `repo=` rule. All verified at those exact lines.

**Test pins today.** `SquadCanonicalContentTests` pins the warn-and-continue rule in the Theory `RunStartSurfacesCarryTheReadOnlyDoctorStepWhenDocsAreEnabled` (`:1791-:1802`, asserting "warn and continue" over both `agents/conductor.md` and `skills/bug-crusher/SKILL.md`) plus the section-order pin at `:1808-:1820`, and asserts the migration reports' `final-body-sha256` at `:1700`. The parent cites `SquadCanonicalContentTests:1680` and `:1696` for the warn-and-continue pins; in this tree those two line numbers hold the migration-report assertions, so the parent's citations were made against its own branch's tree — T10's row is copied verbatim, and the worker rewrites the warn-and-continue pins wherever they sit at execution time.

**Files that exist versus are new.** Existing: the 7 of T10's 12 named test classes that are on this branch (`RepositoryDoctorTests`, `DoctorCommandTests`, `McpLiveProbeTests`, `CorpusProvenance`-less `CommandCodeRendererContractTests`, `SquadUniversalMcpGrantContractTests`, `SquadCanonicalContentTests`, `SquadWorktreeMcpContractTests`) and every named source, agent, skill and docs file in T11–T16 except two. New (verified absent, to be created): `tests/KyberWeave.Tests/DoctorEnsureTests.cs`, `McpCodeGraphRefreshTests.cs`, `McpToolFailureTests.cs`, `CorpusProvenanceTests.cs`, `SquadCodeGraphGrantContractTests.cs`, and `src/KyberWeave.Mcp/CodeGraphFreshness.cs`. Also absent from this branch: `src/KyberWeave.Core/RunStart/RunStartRunner.cs` (T11 edits it; the run-start surface arrives with slice 1's open pull request, see Risks). None of T10's 12 class names exists in `tests/KyberWeave.RepoPolicy.Tests` today; the policy project keeps the `KyberWeave.Tests` namespace (verified in its files and its AGENTS.md), so a fully-qualified name is unambiguous either way.

**Other facts.** `ci.yml:733-757` runs `docs validate . --merge-ready` on every pull request, so `--merge-ready` is wrong while this plan is open (KW-DOC-LIFECYCLE-003 fails an open plan by design; the plan archives in this slice's pull request). No `pr-naming` job exists anywhere under `.github` on this branch. `.codegraph/` is gitignored (`.gitignore:25`). `docs/kyber-squad/architecture.md:1712` holds "### Run-start documentation check" — the section the stop rule rewrites — and `docs/docgraph/mcp-runbook.md:1531` holds "## Git worktrees and MCP binding" — the worktree section H2 extends. `products/kyber-squad/migration/conductor.md:9` records `final-body-sha256`, so any edit to an agent body changes a digest both slices' pull requests must reconcile (see Risks).

**Runner commands.** The parent states the PRODUCT-RUN and POLICY-RUN definitions verbatim in slice 6's child plan, which is delivered on its own unmerged branch and not reachable from this tree. The Test contract below restates them from the two standards that define the same commands: `docs/standards/test/README.md` ("Commands") and `tests/KyberWeave.RepoPolicy.Tests/AGENTS.md`.

## Design

This slice's design, in the parent's voice. Fix 1 verbatim:

### Fix 1: make CodeGraph work where workers run, and check it first

**Causes.** No tool (subagents on Command Code get only the tools they name) and wrong checkout (the server follows the main checkout, which in the measured run was another branch without the base of the fix).

- **Grant.** Add `codegraph` to the renderers that omit it, through the shared predicate (D26).
- **Provision.** `doctor --provision` runs `sync` when an index exists and `init` when absent (D22). The cross-process lock stays.
- **Worker's first command.** `kyber-weave doctor --ensure .` (D22) for every shell role, including when a session continues with a new task.
- **Stop rule.** `conductor.md:62` and `bug-crusher/SKILL.md:43` change from "warn and continue" to stop by default. A new `STATUS: TOOL_FAILURE` makes the orchestrator stop and report, because the existing escalation route goes to the architect. Read-only runs do not stop. A repository with governed docs but no CodeGraph stops until it installs it or sets `false`. Autonomous mode stops too, and joins the hard stops in `autonomy.md:54`.
- **Which tools count.** `mcp.required-servers`. `kw-docs` is required; `codegraph` is turned on for this repository.
- **Discovery order.** Every agent gains: in a worktree, or when the tool is not listed, run `codegraph explore "…"`. Read files directly only if that fails.
- **Server refresh.** D11 and D23. It runs only where `codegraph` is in `mcp.required-servers`, so other repositories keep working. It does not cover a worktree created after the server starts, and does not grant `codegraph_explore` to subagents.

**Who refreshes the index.**

| Role | Has a shell? | Who refreshes |
|---|---|---|
| Any role working in the server's checkout | Either | The docs server, on its first tool call |
| Developers, `docs-dev`, investigator in a worktree | Yes | The worker, `doctor --ensure .` as its first command |
| `code-reviewer` in a worktree | Yes | The reviewer, before starting any lens |
| `review-lens`, `review-triage` | No | Nothing extra: they rely on the server or the reviewer's refresh |
| `architect` | No, by design | The docs server for its own root; `run start` for a worktree |

The worktree limitation, verbatim:

### The worktree limitation

The MCP servers stay bound to the canonical checkout on purpose. So a worker in a worktree on another branch can get answers about the wrong code. This is the rule for each role.

| Role | Code questions | Docs and standards | If the check fails |
|---|---|---|---|
| Developers, `test-dev`, `docs-dev`, investigator (shell) | `codegraph explore` from the worktree's shell, against its own index. Never the MCP `codegraph_explore` when `branch=` differs. | `kw-docs` for standards, which rarely differ by branch. Direct read of the worktree's copy for any document being edited. | `doctor --ensure` follows `stop-on-tool-failure` |
| `code-reviewer` (shell) | Same, before any lens | CLI `docs validate` and `docs drift` in the worktree | Same |
| `review-lens`, `review-triage` (no shell) | The packet carries `.agent_scratchpad/<task-id>/review-diff.patch` and the absolute worktree path. Direct reads of that path. MCP code tools only when provenance `branch=` matches the packet. | `kw-docs` for standards | A lens that cannot read reports `SKIPPED` with the reason |
| `architect` (no shell) | The packet carries `RUN_BASE`, `RUN_BRANCH` and, when present, `WORKTREE: <absolute path>`, all from `run start`. If provenance `rev=` and `branch=` match, normal discovery. If not, direct `Read` and `Grep` at the absolute worktree path, each counted as a direct read with reason "server bound to another revision", and the plan's findings state which revision was read. | The server cannot see a plan written into a worktree. The architect writes to `<WORKTREE>/docs/plans/…`, returns `STATUS: BLOCKED` naming "server root differs: run the docs checks in the worktree", and the conductor has the two CLI gates run there (main session's shell or `github-devops`), then re-invokes the architect with `DOCS_CHECKS: <results>`. `PLAN_READY` then reports `DOCS_VALIDATE: pass (cli, <path>)`. | `BLOCKED` when no worktree path was given |

- **Prevention comes first.** `run start` branches in place when the checkout is clean and not in use, which keeps server, architect and workers on one revision. A worktree is only for a dirty or busy checkout and for parallel workers.
- **A session started inside a worktree** is already correct, because `--repo-root .` resolves there. In supervised mode `run start` says so, so the owner can restart there.
- **Provenance.** Every docs response gains `branch=` and `repo=<common-git-dir>`. `AGENTS.md:117` changes to compare `repo=`, and treats a `root=` mismatch with a matching `repo=` as "another worktree of this repository", not "wrong repository".
- **Not closed.** [Issue 221](https://github.com/dpalfery/kyber-weave/issues/221) (a read-only fact-finder for the architect) stays open. The architect still cannot query a worktree's index. `run start`'s output supplies some of the facts issues 219 and 221 ask for.

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | `doctor --ensure`, `sync`, server refresh, `TOOL_FAILURE`, `--no-codegraph-refresh`, `branch=` and `repo=` | DocGraph | runbook | `docs/docgraph/mcp-runbook.md` and `docs/docgraph/architecture.md` |
| H2 | Worktree limitation, per role | KyberSquad | operations | `docs/kyber-squad/onboarding.md`, and the runbook's worktree section |
| H3 | `codegraph` grant | KyberSquad | requirements | `docs/kyber-squad/requirements.md` matrix |
| H4 | Stop rule replacing "warn and continue" | KyberSquad | architecture | `docs/kyber-squad/architecture.md` |

## ADR disposition

From the parent's ADRs table (D35). Each ADR is written by this slice's closeout, at the next free number; the parent reserves the numbers from 0060.

| Slice | ADR | Records |
|---|---|---|
| 2 | New: code-editing runs stop on unusable tools | D1, D10, D22, D25. **The reversal of "never block a run"**, recorded in `docs/kyber-squad/architecture.md` and two agent files. Rejected: warn and continue, a stop only in supervised mode |
| 2 | New: the docs server refreshes CodeGraph and fails its tools | D11, D23. The tools write to a cache while staying read-only for documents. Rejected: refresh at startup, exiting the process, `index` per server start |
| 2 | Amend ADR 0050 in place | D26 |

## Test contract

Development mode is `test-first`: every task's RED proof is written and shown failing first, then the implementation turns it GREEN. No row runs the full suite — every command below is narrowed by class, and the process guard refuses an unfiltered run from a dev agent (exit 89). A filter that matches nothing exits 8 ("Zero tests ran"), which is how a wrong class name surfaces.

The two runner definitions, as the parent's Test contract states them. The parent's verbatim PRODUCT-RUN and POLICY-RUN definitions live in slice 6's child plan, which sits on that slice's unmerged branch and is not reachable from this tree; they are restated here from the sources that define the same commands — `docs/standards/test/README.md` ("Commands") and `tests/KyberWeave.RepoPolicy.Tests/AGENTS.md`:

- **PRODUCT-RUN** — build once, then run the named classes:
  `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<ClassList>"`
- **POLICY-RUN** — build once, then run the named classes:
  `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`
  `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"`

The policy project's classes keep the `KyberWeave.Tests` namespace, so a fully-qualified name (`-class KyberWeave.Tests.<Name>` under the direct-assembly runner) names the same test wherever it lives. None of this contract's classes lives in the policy project today; the two POLICY-RUN rows name the classes whose subject is the repository file being edited.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T10 | `tests/KyberWeave.Tests/` — `RepositoryDoctorTests`, `DoctorCommandTests`, `DoctorEnsureTests` (new), `McpLiveProbeTests`, `McpCodeGraphRefreshTests` (new), `McpToolFailureTests` (new), `CorpusProvenanceTests` (new), `CommandCodeRendererContractTests`, `SquadUniversalMcpGrantContractTests`, `SquadCodeGraphGrantContractTests` (new), `SquadCanonicalContentTests`, `SquadWorktreeMcpContractTests` | PRODUCT-RUN over the twelve classes | Rows T11 to T14 are written and fail for the reason they name; the warn-and-continue pins are rewritten to the stop rule | Each new class fails for the reason it names against the current tree (`--ensure` absent, provision verb `index`, no server refresh, no `branch=`/`repo=`, no `codegraph` grant); the rewritten `SquadCanonicalContentTests` stop-rule assertions fail against the current "Warn and continue" text (pins at `:1798`–`:1801` in this tree) | T10's acceptance: rows T11 to T14 are written and fail for the reason they name. `SquadCanonicalContentTests:1680` and `:1696` are rewritten to the stop rule, and the report names that as the owner's reversal, not a weakening |
| T11 | `tests/KyberWeave.Tests/` — `RepositoryDoctorTests`, `DoctorCommandTests`, `DoctorEnsureTests`, `McpLiveProbeTests` | PRODUCT-RUN over the four classes | `doctor --ensure` exists with the D22 contract; `--provision` runs `sync` when an index exists; the probe handles the new provenance fields | `DoctorEnsureTests` fails (no `--ensure` mode); the sync expectation fails (the verb is `index` today, `CodeGraphProvisioner.cs:172`) | T11's acceptance: D22 holds — `Provision` takes a lock-wait argument, `TrailingMetadataRegex` handles the new fields, `mcp.root` still catches another checkout, the restore never touches an already-dirty file, `run start` gains its tool test step, and the `--provision` description and the codegraph skill say `sync` |
| T12 | `tests/KyberWeave.Tests/` — `McpCodeGraphRefreshTests`, `McpToolFailureTests`, `CorpusProvenanceTests` | PRODUCT-RUN over the three classes | The docs server refreshes CodeGraph on tool calls, fails its tools under stop with `STATUS: TOOL_FAILURE`, and emits `branch=` and `repo=` | Refresh tests fail (the MCP project has no CodeGraph integration today); provenance tests fail (`CorpusProvenance` emits no `branch=`/`repo=` today) | T12's acceptance: D11 and D23 hold — `CodeGraphProvisioner` and its lock reused with no new process start, one gate at the top of each of the 8 tools, settings read at each refresh, nothing reaches stdout; the measurement reported (no "auto-sync is DISABLED" banner with a CodeGraph server running, or what happens if one appears) |
| T13 | `tests/KyberWeave.Tests/` — `CommandCodeRendererContractTests`, `SquadCodeGraphGrantContractTests`, `SquadUniversalMcpGrantContractTests` | PRODUCT-RUN over the three classes | The renderers grant `codegraph` through the shared predicate; the orchestrator stays excluded | Grant tests fail (Command Code's tool map carries no MCP names; OpenCode and Kilo grant only `kw-docs` today) | T13's acceptance: D26 holds — names come from the toolchain roster, the `permission-not-expressible` records stay truthful, the Command Code class remark is rewritten, whether Pi needs a change is settled and reported |
| T14 | `tests/KyberWeave.Tests/` — `SquadCanonicalContentTests`, `SquadWorktreeMcpContractTests` | PRODUCT-RUN over the two classes | The agent files state the stop rule, the first command and the worktree rule; `STATUS: TOOL_FAILURE` is routed; the architect's `root=` rule becomes the `repo=` rule with the `DOCS_CHECKS` return path | Canonical pins fail against the current text (`conductor.md:62` and `bug-crusher/SKILL.md:43` warn and continue; `conductor.md:73` conditional worktree indexing) | T14's acceptance: D1, D24, D25 hold — `conductor.md:62`, `:73` and `bug-crusher/SKILL.md:43` are replaced, the role tables are stated in each role's own file |
| T15 | `tests/KyberWeave.RepoPolicy.Tests/` — `McpRenameRepoGuardTests` (guards `mcp.required-servers`), `RootAgentsOperationalPointerTests` (pins the root `AGENTS.md` operational facts) | POLICY-RUN over the two classes | `.kyber-weave/kyber-weave.yml` turns `codegraph` on under `mcp.required-servers`; `AGENTS.md:105-111` and `:117-119` follow D24 | The covering RED comes from T10 and T14; this task is the settings and root-rules edit, and the two policy classes must stay green through it | T15's acceptance: `codegraph` is on under `mcp.required-servers`, the `AGENTS.md` provenance rule compares `repo=`, and both policy classes pass with a non-zero count |
| T16 | No test class — documentation and closeout | `docs validate .` and `docs drift .` after the docs edits (CLI gates, through `scripts/proc-guard.sh run --`) | Harvest, ADRs, archive and zero findings | None — documentation task | T16's acceptance: harvest rows landed, two new ADRs including the reversal, ADR 0050 amended, this child plan archived, zero findings |

## Tasks

- **T10: RED for the code index**
  - **Files:** `tests/KyberWeave.Tests/RepositoryDoctorTests.cs`, `tests/KyberWeave.Tests/DoctorCommandTests.cs`, `tests/KyberWeave.Tests/DoctorEnsureTests.cs`, `tests/KyberWeave.Tests/McpLiveProbeTests.cs`, `tests/KyberWeave.Tests/McpCodeGraphRefreshTests.cs`, `tests/KyberWeave.Tests/McpToolFailureTests.cs`, `tests/KyberWeave.Tests/CorpusProvenanceTests.cs`, `tests/KyberWeave.Tests/CommandCodeRendererContractTests.cs`, `tests/KyberWeave.Tests/SquadUniversalMcpGrantContractTests.cs`, `tests/KyberWeave.Tests/SquadCodeGraphGrantContractTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `tests/KyberWeave.Tests/SquadWorktreeMcpContractTests.cs`
  - **Required skills:** `test-dev`
  - **Depends on:** none
  - **Acceptance:** rows T11 to T14 are written and fail for the reason they name. `SquadCanonicalContentTests:1680` and `:1696` are rewritten to the stop rule, and the report names that as the owner's reversal, not a weakening.
- **T11: `doctor --ensure` and `sync`**
  - **Files:** `src/KyberWeave.Core/Doctor/CodeGraphProvisioner.cs`, `src/KyberWeave.Core/Doctor/RepositoryDoctor.cs`, `src/KyberWeave.Core/Doctor/DoctorMode.cs`, `src/KyberWeave.Core/Doctor/McpLiveProbe.cs`, `src/KyberWeave.Cli/Commands/Doctor/DoctorCommand.cs`, `src/KyberWeave.Cli/Commands/Doctor/DoctorSettings.cs`, `src/KyberWeave.Core/RunStart/RunStartRunner.cs`, `src/KyberWeave.Core/Configuration/KyberWeaveConfig.cs`, `src/KyberWeave.Core/Configuration/KyberWeaveConfigLoader.cs`, `src/KyberWeave.Core/Configuration/KyberWeaveYamlDocument.cs`, `.apm/skills/codegraph/SKILL.md`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T10
  - **Acceptance:** D22, D20. `Provision` takes a lock-wait argument. `McpLiveProbe.TrailingMetadataRegex` handles the new provenance fields, and `mcp.root` still catches another checkout. The `--ensure` restore never touches a file that was already dirty. `run start` gains its tool test step. The `--provision` description and the codegraph skill say `sync`. The `stop-on-tool-failure` setting is carried by this task as D20 records, with the same name, type and default as slice 1's. T11's row holds.
- **T12: Docs server refresh and failure**
  - **Files:** `src/KyberWeave.Mcp/CodeGraphFreshness.cs` (new), `src/KyberWeave.Mcp/DocsTools.cs`, `src/KyberWeave.Mcp/Program.cs`, `src/KyberWeave.Mcp/RepositoryRootResolver.cs`, `src/KyberWeave.Mcp/CorpusProvenance.cs`, `src/KyberWeave.Mcp/DocsServerInstructions.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T10, T11
  - **Acceptance:** D11 and D23. It reuses `CodeGraphProvisioner` and its cross-process lock, with no new way to start a process. One gate at the top of each of the 8 tools. Settings are read at each refresh, so an edit takes effect without a restart. Nothing reaches stdout. T12's row holds. Measure: with a CodeGraph server running, a worktree `--ensure` and repeated docs calls produce no "auto-sync is DISABLED" banner; report what happens if they do.
- **T13: Grant `codegraph` in the renderers**
  - **Files:** `src/KyberWeave.Core.Squad/Rendering/CommandCodeRenderer.cs`, `src/KyberWeave.Core.Squad/Rendering/OpenCodeRenderer.cs`, `src/KyberWeave.Core.Squad/Rendering/KiloRenderer.cs`, `src/KyberWeave.Core.Squad/Rendering/PiRenderer.cs`, `src/KyberWeave.Core.Squad/Rendering/CapabilityDegradations.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T10
  - **Acceptance:** D26. Names come from the toolchain roster, not a local list. The `permission-not-expressible` records stay truthful. The Command Code class remark is rewritten. Whether Pi needs a change is settled and reported. T13's row holds.
- **T14: Agent instructions: stop rule, first command, worktree rule**
  - **Files:** `products/kyber-squad/agents/*.md`, `products/kyber-squad/agents/architect/references/plan-authoring.md`, `products/kyber-squad/agents/conductor/references/execution-and-review.md`, `products/kyber-squad/agents/conductor/references/autonomy.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`, `products/kyber-squad/skills/code-review/SKILL.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T11, T12
  - **Acceptance:** D1, D24, D25. The role tables above are stated in each role's own file. `conductor.md:62`, `:73` and `bug-crusher/SKILL.md:43` are replaced. `STATUS: TOOL_FAILURE` is routed. The architect's `root=` rule becomes the `repo=` rule, with the `DOCS_CHECKS` return path. T14's row holds.
- **T15: This repository's settings and root rules**
  - **Files:** `.kyber-weave/kyber-weave.yml`, `AGENTS.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T11, T12
  - **Acceptance:** `codegraph` is turned on under `mcp.required-servers`. `AGENTS.md:105-111` and `:117-119` follow D24. T15's row holds.
- **T16: Docs and closeout for slice 2**
  - **Files:** `docs/docgraph/mcp-runbook.md`, `docs/docgraph/architecture.md`, `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`, `docs/kyber-squad/requirements.md`, `docs/configuration.md`, `docs/cli-command-surface.md`, `docs/ci-pipelines/rule-reference.md`, `src/KyberWeave.Mcp/AGENTS.md`, `docs/adr/0050-universal-kyber-weave-mcp-grant.md`, `docs/adr/README.md`, `docs/plans/README.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T11, T12, T13, T14, T15
  - **Acceptance:** harvest, two new ADRs including the reversal, ADR 0050 amended, child plan archived, zero findings.

## Dependency graph

- T10 stands alone.
- T11 depends on T10.
- T12 depends on T10 and T11.
- T13 depends on T10.
- T14 depends on T11 and T12.
- T15 depends on T11 and T12.
- T16 depends on T11, T12, T13, T14 and T15.

## Risks

- **The reversal of "never block a run" is the largest cost of this slice.** It reverses a documented rule — `conductor.md:62`, `bug-crusher/SKILL.md:43`, the `Run-start documentation check` section at `docs/kyber-squad/architecture.md:1712`, and the `SquadCanonicalContentTests` pins — recorded as the owner's explicit decision (D1, 2026-11-07). The cost: a repository with governed docs whose CodeGraph cannot be repaired stops code-editing runs until it installs CodeGraph or sets `stop-on-tool-failure: false`, and every worker start pays the `--ensure` cost (a `sync` is about 8 seconds). The stop rule must not leak to read-only runs, which never stop.
- **Worktree index cost.** D25: every new worktree indexes itself at about 8 seconds and about 223 MB on disk until cleanup removes it. Parallel workers multiply that cost; the guarantee it buys is that the index answers for the tree the worker is in.
- **A refresh that cannot take the lock must not loop or fail the run.** D23: the docs server takes a zero-wait lock; contention means another process is refreshing, answered with a one-line note rather than a failure. The failure mode to watch is a refresh that cannot distinguish contention from a dead lock holder and warns forever — the 60-second interval (a tunable constant) and the 30-second bound are the mitigation.
- **Slice 6 is delivered but NOT merged.** Its pull request #541 edits the same agent files this slice's T14 edits: `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/bug-crusher.md`, `products/kyber-squad/agents/conductor/references/execution-and-review.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md` and `products/kyber-squad/skills/code-review/SKILL.md`. This branch is off `origin/main`, so those files do not carry slice 6's text here. The two pull requests will need a rebase when the second merges, and the migration reports under `products/kyber-squad/migration/` record a `final-body-sha256` for the agent bodies (asserted at `SquadCanonicalContentTests:1700`), so both slices' text has to end up in the migration reports that name them. Do not merge slice 6 into this branch.
- **Slice 1 is still open, and this slice builds on it.** This branch is off `origin/main`, so the run-start surface T11 edits (`src/KyberWeave.Core/RunStart/RunStartRunner.cs`, verified absent here) and the `stop-on-tool-failure` setting D22's `doctor --ensure` reads (verified absent from `.kyber-weave/kyber-weave.yml` here) do not exist on this branch; they arrive with slice 1's open pull request. D20 records how this slice carries the setting itself so it is not blocked on that merge: the same name, the same type and the same default, with the second pull request to merge dropping its copy. The run-start surface is not carried the same way, because no test in this slice reaches it and creating the file would collide with slice 1's own version; T11 reports it as the one acceptance line it cannot satisfy. Execution sequences after slice 1 lands, or rebases onto it, and T10's RED rows are written against that integrated tree.

## Out of scope

- The parent's other seven slices — each has its own child plan on its own branch and its own pull request.
- [Issue 221](https://github.com/dpalfery/kyber-weave/issues/221), a read-only fact-finder for the architect: it stays open, and the architect still cannot query a worktree's index.
- The server refresh does not cover a worktree created after the server starts, and does not grant `codegraph_explore` to subagents (D11, D23, D24).
- `context7` is not widened (D26); [ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md) is amended in place, not replaced.
- `doctor` never installs the `codegraph` binary; exit 3 remains a human decision in every mode (D1, D22).
- The canonical MCP binding stays: the servers stay bound to the canonical checkout on purpose; only the per-role rules around that fact change.

## Verification gates

- Every task's row in the Test contract runs a named class set with a **non-zero test count**; a filter that matches nothing exits 8 and is treated as a wrong class name, not a pass. **No task or gate runs the full suite** — `scripts/proc-guard.sh run` refuses an unfiltered run from a dev agent with exit 89, and the full suite belongs to the review council's `test` gate and CI.
- `docs validate .` and `docs drift .` run after every plan save and after every docs task (T14, T15, T16), each at zero findings. `docs validate . --merge-ready` is **not** the check while this plan is open: KW-DOC-LIFECYCLE-003 fails an open plan by design, and `ci.yml:733-757` runs it on every pull request, which is what archives this plan in this slice's pull request at closeout.
- **Branch and title.** The `pr-naming` CI job does not exist on this branch or on `main` — it was delivered on slice 1's branch, which is still open. This slice's branch is already created as `factory/feat/conductor-run-speed-code-index` (off `origin/main` at `2759e5f5`), and its pull request title follows the shape slice 6 used: `<type>(<scope>): <summary> [factory]`.

## Review

This slice's pull request goes through **one `code-reviewer` council over its whole change**, run after the draft is pushed, with nothing else editing that tree while the council runs. No agent reviews a single task; each developer task ends with the worker's own `task-gate` run, and the council is the single end-of-run review.

## Closeout

Placeholder — filled at closeout, not now; this plan is Ready, not closed. At closeout this section records: the harvest rows H1–H4 landed at their targets, the two new ADRs written at the next free number (including the reversal of "never block a run") and ADR 0050 amended in place, this child plan `git mv`'d to `docs/archive/plans/` with its index row moved to **Archived Plans**, `docs validate` and `docs drift` at zero findings, and the rebase against slice 6 recorded.

## Closeout mapping

Placeholder — filled at closeout: one row per decision (D1, D8, D10–D13, D21–D26, AP1) and per harvest row (H1–H4), each naming its destination document or ADR and its evidence, in the shape the archived plans' Closeout mapping sections use.

STATUS: PLAN_READY
