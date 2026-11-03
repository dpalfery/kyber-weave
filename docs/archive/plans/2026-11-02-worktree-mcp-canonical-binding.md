---
id: plans/2026-11-02-worktree-mcp-canonical-binding
title: "Worktree MCP Canonical Binding and Subagent Exposure Protocol"
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-02
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [DocGraph, KyberSquad]
---

# Worktree MCP Canonical Binding and Subagent Exposure Protocol

**Status: Archived — complete 2026-11-02.**
This plan establishes the execution protocol and configuration discipline for running agent workers within isolated Git worktrees without losing access to CodeGraph or DocGraph MCP servers. **Development mode:** `test-first`. **Autonomy:** `autonomous`. **Scale:** `extends-component`, components `DocGraph` and `KyberSquad`.

**Recommendation.** Update the conductor worktree provisioning protocol to bind both `kyber-weave` and `codegraph` MCP servers to the canonical repository checkout by absolute path (`--repo-root /abs/canonical`, `--path /abs/canonical`). This shares the canonical 200 MB CodeGraph SQLite index and the authoritative documentation corpus with zero setup latency and zero disk overhead. Strictly prohibit symlinking `.codegraph/` into worktrees to prevent background indexing daemons and file watchers from corrupting the canonical database with unintegrated branch state. Authorize `codegraph init .` inside a worktree strictly on demand when a task's verification explicitly requires newly added branch symbols. Align agent runtimes and squad renderers so that subagents are exposed to declared project MCP servers, and include `kyber-weave doctor --check <canonical-root>` in the worktree setup protocol to fail fast if the index or server is unhealthy.

> Discovery: codegraph 0, docs 9, direct reads 24 (self-gathered). CodeGraph MCP tool was unavailable in the harness session; discovery proceeded via Kyber-Weave DocGraph MCP tools (`docs_status`, `docs_explore`) and focused direct reads.
> 
> - Direct reads of indexed/configuration files:
>   - `.mcp.json` (root configuration; inspected stdio launch args for kyber-weave and codegraph)
>   - `docs/docgraph/mcp-runbook.md` (lines 1-800, 800-1400; verified `--repo-root` resolution, harness wiring, doctor check ids and exit codes)
>   - `products/kyber-squad/mcp.json` (squad canonical MCP server declaration)
>   - `docs/adr/README.md` (ADR inventory; reviewed ADRs 0044, 0050, 0051, 0052)
>   - `docs/adr/0050-universal-kyber-weave-mcp-grant.md` (verified universal grant rules and pure-orchestrator carve-out)
>   - `docs/adr/0044-squad-lock-v2-and-instance-deployment-scope.md` (reviewed cross-platform linking and instance scope)
>   - `.agents/skills/spec-conductor/SKILL.md` (reviewed conductor delegation and execution rules)
>   - `products/kyber-squad/toolchain.yml` (inspected `required-mcp-tools` roster)
>   - `products/kyber-squad/agents/conductor.md` and `references/execution-and-review.md` (reviewed run-start doctor and worktree isolation invariants)
>   - `~/.gemini/antigravity/mcp_config.json` (inspected live harness-level MCP config)
>   - `src/KyberWeave.Cli/Commands/Doctor/DoctorCommand.cs` and `src/KyberWeave.Core/Doctor/*` (`RepositoryDoctor.cs`, `CodeGraphProvisioner.cs`, `McpLiveProbe.cs`; inspected git root resolution and check implementations)
>   - `src/KyberWeave.Core/Squad/Rendering/*` (`AntigravityRenderer.cs`, `ClaudeRenderer.cs`; inspected frontmatter MCP emission)
>   - `docs/plans/README.md`, `docs/documentation-ontology.md`, `docs/catalog.md`, and archived reference plans.

---

## 1. Problem and Goal

### 1.1 The Issue

When the conductor isolates parallel tasks or zero-stash RED test proofs using separate Git worktrees, workers operate in a filesystem directory distinct from the canonical repository checkout. In this topology, both MCP servers fail or become unavailable due to three coupled factors:

1. **Workspace-Relative MCP Binding:** The repository `.mcp.json` (and harness equivalents) specifies relative paths:
   ```json
   {
     "mcpServers": {
       "kyber-weave": {
         "type": "stdio",
         "command": "kyber-weave-mcp",
         "args": ["--repo-root", "."]
       },
       "codegraph": {
         "type": "stdio",
         "command": "codegraph",
         "args": ["serve", "--mcp", "--path", "."]
       }
     }
   }
   ```
   When an agent session or tool process executes in a worktree, `.` resolves to the worktree path rather than the canonical repository checkout.
2. **Gitignored CodeGraph Index:** The CodeGraph database (`.codegraph/codegraph.db`, ~200 MB) is ignored by version control. Consequently, a freshly created Git worktree contains neither `.codegraph/` nor an index.
   - `codegraph serve --mcp --path .` finds no database and serves no symbols.
   - `kyber-weave-mcp --repo-root .` fails to locate `.codegraph/codegraph.db`, disabling DocGraph-CodeGraph symbol joins and causing `docs drift` to fail with `KW-DOC-DRIFT-001`.
   - Furthermore, `kyber-weave-mcp` serves only the worktree's local branch state of `docs/`, which may be incomplete, divergent, or unmerged.
3. **Subagent MCP Propagation Gap:** In agent runtimes (such as Antigravity and Factory), worker subagents dispatched to execute tasks (e.g. `csharp-dev`, `test-dev`, `architect`) are not automatically exposed to the project `.mcp.json` servers unless explicitly configured in agent frontmatter or workspace configuration. In Antigravity, `AntigravityRenderer` only injected `kyber-weave` and completely omitted `codegraph` because `products/kyber-squad/mcp.json` only declared `kyber-weave`. In Factory, `FactoryRenderer` whitelists only `kyber-weave` in droids for the same reason.

### 1.2 Evaluation of the Proposed 3-Sentence Fix

The user proposed the following three-sentence fix:
> 1. *Make the worktree protocol bind both servers to the canonical checkout by absolute path (`--repo-root /abs/canonical`, `--path /abs/canonical`), so the 200 MB index and the full docs/ corpus are shared for free — and never symlink `.codegraph/`, because the worktree's watcher would then rewrite the canonical db from the worktree's content.*
> 2. *Run `codegraph init <worktree>` only when a task's acceptance depends on code the branch adds, since a shared index does not know the branch's new files.*
> 3. *Ensure the agent runtime exposes the servers declared in the project's `.mcp.json` to subagents (the repo side then only needs the absolute `--repo-root`), and keep `kyber-weave doctor --check` in the worktree protocol so a missing or stale index is reported rather than silently absent.*

#### Evaluation of Sentence 1: Absolute Canonical Binding & Ban on Symlinking
- **Absolute Path Binding:** Binding both servers to `/abs/canonical` allows worktree workers to instantly query the entire canonical index and documentation without building a new 200 MB database or duplicating memory/disk resources.
- **The Symlink Hazard:** Prohibiting symlinking `.codegraph/` into the worktree is essential. If `.codegraph` were symlinked (`ln -s /abs/canonical/.codegraph <worktree>/.codegraph`), any active CodeGraph indexing process or daemon attached to the worktree would detect worktree file events, re-index based on uncommitted branch changes, and overwrite `/abs/canonical/.codegraph/codegraph.db`. This would corrupt the canonical repository database with transient branch state.
- **Implementation Mechanism:** The committed `.mcp.json` in version control must retain relative tokens (`.`) so that fresh checkouts remain portable across different developer machines and CI environments. The worktree provisioning step (managed by conductor or worker preparation scripts) generates or updates the worktree's local `.mcp.json` (or harness-specific config) with the canonical checkout's absolute path before launching worker sessions.

#### Evaluation of Sentence 2: Conditional Worktree Indexing
- **Default Behavior:** By default, worker tasks consume the shared canonical index. Most developer tasks (implementing backend logic, unit tests, bug fixes) require code exploration of existing caller/callee trees and architectural types already present in the canonical trunk.
- **When Worktree Indexing Is Required:** A shared canonical index does not index untracked or unmerged files created in the branch. If a task's specific acceptance verification or exit gate explicitly depends on CodeGraph indexing newly introduced symbols (for example, a task validating cross-project CodeGraph joins or verifying `docs drift` over newly documented symbols), the worker must run `codegraph init .` inside the worktree.
- **Isolation:** When run inside the worktree, `codegraph init .` writes a worktree-local `.codegraph/` directory. The worktree's CodeGraph MCP server is then repointed to `--path <worktree>`. When the worktree is cleaned up via `git worktree remove --force`, the throwaway index is cleanly pruned.

#### Evaluation of Sentence 3: Subagent MCP Propagation & Doctor Check
- **Subagent MCP Propagation:** Harness runtimes must expose the declared MCP servers to subagents. In Kyber-Squad:
  - `products/kyber-squad/mcp.json` currently declares only `kyber-weave`. Under ADR 0050, `codegraph` has an orchestrator carve-out (not granted to `conductor`, but granted to specialist subagents such as `csharp-dev`, `test-dev`, and `architect`).
  - Renderers that emit per-agent MCP definitions (such as `AntigravityRenderer` emitting frontmatter `mcpServers` and `FactoryRenderer` emitting droid whitelists) must include `codegraph` for non-orchestrator specialists when declared in squad configuration or project wiring.
  - Harness-level configs (e.g. `.agents/mcp_config.json`, `.factory/mcp.json`) must be accessible to child worker processes.
- **Worktree Doctor Protocol:**
  - `RepositoryDoctor.Run` executes `git rev-parse --show-toplevel`. When run inside a worktree without arguments, it evaluates `<worktree>` as the repository root and checks `<worktree>/.codegraph/`. In a worktree sharing the canonical index, `codegraph.index` would report `FAIL no CodeGraph index at .codegraph/`.
  - Therefore, the worktree execution protocol must invoke `kyber-weave doctor --check <canonical-root>` during worktree setup. This verifies that the canonical index and MCP server are responsive before worker tasks begin, preventing silent tool failures.

### 1.3 Acceptance Criteria

1. **Canonical Binding in Worktree Protocol:** Conductor worktree isolation protocol specifies that when creating a Git worktree for parallel workers or zero-stash RED proofs, the worktree's MCP server configuration binds `kyber-weave` and `codegraph` to the canonical checkout root via absolute arguments (`--repo-root /abs/canonical`, `--path /abs/canonical`).
2. **Symlink Prohibition:** Conductor and worktree instructions explicitly forbid creating filesystem symlinks or junctions to `.codegraph/`.
3. **Conditional Worktree Indexing Rule:** Conductor instructions specify that `codegraph init .` is executed inside a worktree only when a task's acceptance criteria or contract tests explicitly require indexing newly added branch symbols.
4. **Subagent MCP Exposure:** Kyber-Squad configuration and renderers expose `codegraph` to specialist subagents (excluding `conductor` per ADR 0050) on harnesses supporting per-agent MCP definitions (Antigravity, Factory, Claude, ZCode, Command Code, Pi, Kilo).
5. **Worktree Doctor Verification:** Conductor worktree setup protocol requires running `kyber-weave doctor --check <canonical-root>` to verify index presence and server health ahead of worker task dispatches.
6. **Documentation & Tests:** All contracts, runbooks, and conductor instructions are updated with zero drift, and test-first regression tests pin the worktree protocol and renderer behaviors.

---

## 2. Technical Investigation Findings

### 2.1 Git Worktrees and Root Resolution

A Git worktree checkout created via `git worktree add <path> <branch>` creates an isolated working tree linked back to the main repository's `.git/worktrees/<name>` administrative directory.
- `git rev-parse --show-toplevel` inside the worktree returns the worktree directory path (e.g., `<canonical-root>/.agent_scratchpad/task-1/worktree`).
- `git rev-parse --git-common-dir` returns the canonical repository `.git` directory (e.g., `<canonical-root>/.git`).
- The canonical repository root is deterministically resolved from inside a worktree using:
  ```bash
  git -C <worktree> rev-parse --path-format=absolute --git-common-dir | sed 's#/\.git$##'
  ```
  Or passed directly by the conductor, which already knows the canonical repository root from its own session (`git rev-parse --show-toplevel`).

### 2.2 Symlink Hazard Analysis

CodeGraph maintains an internal SQLite database (`.codegraph/codegraph.db`) and an active watcher daemon (`codegraph serve` / `codegraph index`).
If a symlink `.codegraph -> /abs/canonical/.codegraph` is introduced into a worktree:
1. `codegraph serve` or any file watcher operating in the worktree will follow the link and open `/abs/canonical/.codegraph/codegraph.db`.
2. As the worker edits files, creates scratch files, or switches branches in the worktree, the watcher processes file modification events and updates the database.
3. This rewrites the canonical repository's index with uncommitted, partial, or conflicting branch code, corrupting the shared index for all other concurrent workers and for the conductor.
4. Furthermore, Kyber-Weave strictly prohibits symbolic links for repository-scope configuration and documentation state (`McpConfigurationLink`, ADR 0052). Therefore, symlinks to `.codegraph` are completely prohibited.

### 2.3 Doctor Check in Worktrees

`RepositoryDoctor.Run` in `src/KyberWeave.Core/Doctor/RepositoryDoctor.cs` resolves the repository root via:
```csharp
(string? repositoryRoot, string? notRepository) = ResolveRepositoryRoot(root, executor);
```
Where `root` is the directory path passed to `kyber-weave doctor [path]`.
- If `path` is omitted, `root` defaults to the current working directory (`.`).
- Inside a worktree, `.` resolves to the worktree path. `CodeGraphProvisioner.Run` looks for `<worktree>/.codegraph/`.
- In a worktree sharing the canonical index, `<worktree>/.codegraph/` does not exist. `doctor --check .` would fail with:
  ```text
  FAIL codegraph.index no CodeGraph index at .codegraph/
    fix: kyber-weave doctor --provision
  ```
- If an agent erroneously followed the fix line and ran `doctor --provision`, it would initiate a 600-second full re-index of the worktree, defeating the purpose of sharing the canonical index.
- **Solution:** The worktree setup protocol must invoke:
  ```bash
  kyber-weave doctor --check <canonical-root>
  ```
  This asserts that the canonical index that the worktree binds to is present, writable, and responsive.

### 2.4 Subagent MCP Exposure Across Harnesses

| Harness | Subagent MCP Grant Mechanism | How It Is Configured | Current Status / Change Required |
|---|---|---|---|
| **Antigravity** | Agent frontmatter `mcpServers: [{name, command, args}]` | Rendered by `AntigravityRenderer.cs` from `products/kyber-squad/mcp.json` | Currently only emits `kyber-weave`. Needs `codegraph` declared for non-orchestrator specialists. Workspace `.agents/mcp_config.json` must mirror project servers. |
| **Claude Code** | Agent frontmatter `tools: [mcp__<server>__*]` | Rendered by `ClaudeRenderer.cs`; servers defined in `.mcp.json` | `mcp__codegraph__*` is already in `OrchestratorExcludedMcpTools` and granted to specialists. Worktree `.mcp.json` must bind to canonical root. |
| **Factory** | Droid frontmatter `mcpServers: [<server>]` whitelist | Rendered by `FactoryRenderer.cs` from `mcp.json`; servers launched via `.factory/mcp.json` | Droid whitelist requires `codegraph` added for non-orchestrators. Worktree `.factory/mcp.json` must bind to canonical root. |
| **Cursor** | Inherited structurally from session | Defined in `.cursor/mcp.json` or `.mcp.json` | Worktree config must bind to canonical root; subagents inherit automatically. |
| **Codex** | Inherited structurally from session | Defined in `config.toml` | Worktree config binds to canonical root; subagents inherit automatically. |
| **ZCode** | Frontmatter `tools: [mcp__<server>__<tool>]` | Rendered by `ZCodeRenderer.cs` from `toolchain.yml` | `codegraph_explore` already granted to non-orchestrator specialists. Worktree `zcode.json` must bind to canonical root. |
| **Command Code**| Frontmatter `tools: [mcp__<server>__<tool>]` | Rendered by `CommandCodeRenderer.cs` from `toolchain.yml` | `codegraph_explore` already granted to non-orchestrator specialists. Worktree `.mcp.json` must bind to canonical root. |
| **Kilo** | Frontmatter `permission: {<server>_*: allow}` | Rendered by `KiloRenderer.cs`; servers in `kilo.jsonc` | Granted to specialists. Worktree `kilo.jsonc` must bind to canonical root. |
| **Pi** | Frontmatter `extensions` and `tools` overlay | Rendered by `PiRenderer.cs` | Specialists receive derived overlay. Worktree `.mcp.json` binds to canonical root. |

---

## Decisions

| ID | Question | Answer / Decision | Mitigating and Supporting Information | Status / Approval Provenance |
|---|---|---|---|---|
| D1 | How should MCP servers in Git worktrees bind to repository roots? | **By absolute path to the canonical checkout.** When a worktree is created, its local MCP configuration (`.mcp.json` or harness equivalent) sets `--repo-root <canonical-root>` for `kyber-weave` and `--path <canonical-root>` for `codegraph`. The committed `.mcp.json` in Git retains portable relative paths (`.`). | Avoids 200 MB index duplication and re-indexing latency. Provides instant access to full codebase graph and complete doc corpus. Uncommitted local worktree configs remain isolated to the transient worktree directory. | approved by user, 2026-11-02 |
| D2 | Should `.codegraph/` ever be symlinked into a Git worktree? | **Strictly prohibited.** Worktree setup protocols, conductor instructions, and runbooks must never create symbolic links or directory junctions for `.codegraph/`. | Symlinking exposes the canonical index to active file watchers and indexing daemons in the worktree. Branch edits and scratch files would trigger watcher updates that overwrite `/abs/canonical/.codegraph/codegraph.db`, corrupting the canonical database. | approved by user, 2026-11-02 |
| D3 | When should `codegraph init .` be run inside a Git worktree? | **Conditionally, only when required by task acceptance.** By default, tasks read the shared canonical index. `codegraph init .` (or `kyber-weave doctor --provision`) is run in the worktree only when a task explicitly requires CodeGraph indexing of newly introduced branch symbols for verification or drift checking. | Most implementation and test tasks explore existing call graphs. Indexing takes up to 600 seconds and consumes significant memory/CPU. Performing it on demand preserves fast iteration loops. | approved by user, 2026-11-02 |
| D4 | How should subagent MCP access to `codegraph` be configured across harnesses? | **Declare `codegraph` in `products/kyber-squad/mcp.json` and harness templates for non-orchestrator specialists.** `AntigravityRenderer` and `FactoryRenderer` emit `codegraph` for agents whose profile allows `filesystem.read` and is not `orchestrator` (preserving ADR 0050's carve-out). Agent runtimes expose project `.mcp.json` servers to dispatched subagents. | Resolves the harness-side gap where worker sessions lacked MCP tool exposure. Keeps `conductor` purely orchestration-focused while giving implementers (`csharp-dev`, `test-dev`, `architect`) symbol exploration capabilities. | approved by user, 2026-11-02 |
| D5 | How should `kyber-weave doctor --check` be executed in the worktree protocol? | **Explicitly targeted at the canonical root: `kyber-weave doctor --check <canonical-root>`.** | Bare `doctor --check` inside a worktree checks `<worktree>/.codegraph/`, which will fail when sharing the canonical index. Target-path invocation proves that the canonical index and MCP server to which the worktree binds are healthy. | approved by user, 2026-11-02 |
| D6 | What documentation and instruction files govern this worktree protocol? | **Conductor instructions (`conductor.md`, `execution-and-review.md`), MCP runbook (`mcp-runbook.md`), Kyber-Squad architecture (`architecture.md`), and onboarding (`onboarding.md`).** | Ensures single-source consistency across all developer and agent guidance without drift. | approved by user, 2026-11-02 |

---

## Design harvest

| ID | Target Document | Target Section | Doc Type | Description |
|---|---|---|---|---|
| H1 | `docs/docgraph/mcp-runbook.md` | `## Git worktrees and MCP binding` | runbook | Documents the worktree protocol: absolute canonical path binding (`--repo-root`, `--path`), strict prohibition of `.codegraph` symlinks, conditional worktree indexing, and doctor check targeting canonical root. |
| H2 | `docs/kyber-squad/architecture.md` | `## Concurrency and scheduling model (worktree and artifact isolation)` | architecture | Updates worktree isolation rules to include MCP server binding, subagent MCP propagation, and symlink guardrails. |
| H3 | `docs/kyber-squad/onboarding.md` | `## Task execution runbook` | onboarding | Adds operational guidelines for worktree creation, MCP configuration, and index health verification. |
| H4 | `products/kyber-squad/agents/conductor.md` | `## Shared lifecycle invariants` | agent instruction | Adds worktree MCP binding and canonical doctor check invariants to the conductor agent. |
| H5 | `products/kyber-squad/agents/conductor/references/execution-and-review.md` | `## Ready queue` | reference | Updates worktree isolation procedure with canonical MCP path splicing and symlink prohibition. |
| H6 | `products/kyber-squad/mcp.json` | Root | configuration | Declares `codegraph` beside `kyber-weave` so renderers can emit both servers for entitled specialists. |

**ADR Waiver:** No new ADR is required. This plan refines existing worktree concurrency isolation patterns (ADR 0044, PR #476/PR #486) and MCP server grant rules (ADRs 0050, 0051, 0052) within existing architectural boundaries without adding new capabilities, schemas, or external dependencies.

---

## 5. Phased Implementation Sequence (Test-First)

### Phase 1: Contract & Unit Tests (RED)

- [x] **T1. Worktree Protocol & Splicing Contract Tests (RED)**
  - **Goal:** Author contract tests asserting that worktree MCP setup scripts/helpers splice `--repo-root` and `--path` to absolute canonical paths and fail closed if a symlink to `.codegraph` is detected.
  - **Entry file:** `tests/KyberWeave.Tests/SquadWorktreeMcpContractTests.cs`
  - **Pre-computed command:**
    ```bash
    scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj --filter "FullyQualifiedName~SquadWorktreeMcpContractTests"
    ```
- [x] **T2. Antigravity & Factory Specialist Codegraph MCP Grant Tests (RED)**
  - **Goal:** Author contract tests in `SquadRendererMcpTests` asserting that when `codegraph` is declared in `products/kyber-squad/mcp.json`, `AntigravityRenderer` and `FactoryRenderer` emit `codegraph` in `mcpServers` for non-orchestrator specialists (`csharp-dev`, `test-dev`, `architect`) while withholding it from `conductor` (per ADR 0050).
  - **Entry file:** `tests/KyberWeave.Tests/SquadUniversalMcpGrantContractTests.cs`
  - **Pre-computed command:**
    ```bash
    scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj --filter "FullyQualifiedName~SquadUniversalMcpGrantContractTests"
    ```
- [x] **T3. Doctor Worktree Path Targeted Check Tests (RED)**
  - **Goal:** Assert that `RepositoryDoctor.Run` correctly inspects a specified canonical repository path when invoked with an explicit path argument from within an arbitrary directory (simulating a worktree checking its canonical parent).
  - **Entry file:** `tests/KyberWeave.Tests/DoctorCommandTests.cs`
  - **Pre-computed command:**
    ```bash
    scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj --filter "FullyQualifiedName~DoctorCommandTests"
    ```

### Phase 2: Implementation & Protocol Updates (GREEN)

- [x] **T4. Squad MCP Declaration & Renderer Alignment (GREEN)**
  - **Goal:** Update `products/kyber-squad/mcp.json` to include `codegraph` launched with `codegraph serve --mcp --path .`. Update `AntigravityRenderer.cs` and `FactoryRenderer.cs` to emit `codegraph` for read-entitled non-orchestrator agents according to `CapabilityDegradations.GrantsOrchestratorExcludedMcpServers`.
  - **Entry files:**
    - `products/kyber-squad/mcp.json`
    - `src/KyberWeave.Core/Squad/Rendering/AntigravityRenderer.cs`
    - `src/KyberWeave.Core/Squad/Rendering/FactoryRenderer.cs`
  - **Verification:** Run T2 test suite until GREEN.
- [x] **T5. Conductor Worktree Protocol & Instruction Updates (GREEN)**
  - **Goal:** Update `products/kyber-squad/agents/conductor.md` and `execution-and-review.md` to specify:
    1. Worktree creation binds MCP servers to canonical root by absolute path.
    2. Strict prohibition of `.codegraph` symlinks.
    3. Conditional worktree indexing (`codegraph init .` only when branch symbols are required for verification).
    4. Execution of `kyber-weave doctor --check <canonical-root>` before dispatching worker tasks.
  - **Entry files:**
    - `products/kyber-squad/agents/conductor.md`
    - `products/kyber-squad/agents/conductor/references/execution-and-review.md`
  - **Verification:** Run T1 test suite until GREEN.
- [x] **T6. Canonical Documentation Harvest (GREEN)**
  - **Goal:** Update `docs/docgraph/mcp-runbook.md`, `docs/kyber-squad/architecture.md`, and `docs/kyber-squad/onboarding.md` per the Design Harvest table.
  - **Entry files:**
    - `docs/docgraph/mcp-runbook.md`
    - `docs/kyber-squad/architecture.md`
    - `docs/kyber-squad/onboarding.md`

### Phase 3: Verification & Governance

- [x] **T7. Full Test Suite & Inner Loop Verification**
  - **Command:** `scripts/fast-loop.sh --scoped`
  - **Target:** 0 warnings, all unit and contract tests passing.
- [x] **T8. Documentation Validation & Drift Gates**
  - **Command:**
    ```bash
    dotnet run --project src/KyberWeave.Cli -- docs validate .
    dotnet run --project src/KyberWeave.Cli -- docs drift .
    ```
  - **Target:** 0 findings, verified against current checkout provenance root.

---

## 6. Verification and Test Contract

### 6.1 Test Surfaces

- **`SquadUniversalMcpGrantContractTests`**: Tests that `AntigravityRenderer` and `FactoryRenderer` correctly evaluate `GrantsOrchestratorExcludedMcpServers` to grant `codegraph` to specialists and withhold it from `conductor`.
- **`DoctorCommandTests`**: Tests that `kyber-weave doctor --check <path>` correctly resolves the provided repository root and performs live checks against it.
- **`SquadWorktreeMcpContractTests`**: Tests that worktree configuration helpers generate absolute paths for `--repo-root` and `--path`, and reject symlink creation for `.codegraph/`.

### 6.2 Pre-Computed Commands

```bash
# Run targeted renderer and contract tests
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj --filter "FullyQualifiedName~SquadUniversalMcpGrantContractTests"

# Run doctor tests
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj --filter "FullyQualifiedName~DoctorCommandTests"

# Run fast loop check
scripts/fast-loop.sh --scoped

# Run governance checks
dotnet run --project src/KyberWeave.Cli -- docs validate .
dotnet run --project src/KyberWeave.Cli -- docs drift .
```

---

## Closeout mapping

**Completed 2026-11-02.** Archived under `docs/archive/plans/`. Every decision and design harvest item is mapped to canonical documentation and verified by tests.

### Verification evidence

| Check | Result |
|---|---|
| Contract & Unit Tests | 95 passed, 0 failed, 0 skipped in `AntigravityRendererContractTests`, `FactoryRendererContractTests`, `SquadWorktreeMcpContractTests`, `DoctorCommandTests`, and `SquadUniversalMcpGrantContractTests`. |
| `docs validate . --merge-ready` | 0 critical, 0 error, 0 warning, 0 info. |
| `docs drift .` | 0 critical, 0 error, 0 warning, 0 info against current CodeGraph index. |

### ADR Waiver

No new ADR is required. This plan refines existing worktree concurrency isolation patterns (ADR 0044) and MCP server grant rules (ADRs 0050, 0051, 0052) within existing architectural boundaries without adding new capabilities, schemas, or external dependencies.

### Mapping

Documentation-impact is `extends-component` for every row; impact components are `KyberSquad` and `DocGraph`; `last-reviewed` is 2026-11-02.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | [`docs/docgraph/mcp-runbook.md`](../../docgraph/mcp-runbook.md#git-worktrees-and-mcp-binding), [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md#concurrency-and-scheduling-model-worktree-and-artifact-isolation), [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md#task-execution-runbook-inlined-packets-fast-testing-and-completion-digests), [`products/kyber-squad/agents/conductor.md`](../../../products/kyber-squad/agents/conductor.md), [`products/kyber-squad/agents/conductor/references/execution-and-review.md`](../../../products/kyber-squad/agents/conductor/references/execution-and-review.md) | extends-component | DocGraph, KyberSquad | 2026-11-02 | archived |
| D2 | [`docs/docgraph/mcp-runbook.md`](../../docgraph/mcp-runbook.md#git-worktrees-and-mcp-binding), [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md#concurrency-and-scheduling-model-worktree-and-artifact-isolation), [`products/kyber-squad/agents/conductor.md`](../../../products/kyber-squad/agents/conductor.md), [`products/kyber-squad/agents/conductor/references/execution-and-review.md`](../../../products/kyber-squad/agents/conductor/references/execution-and-review.md) | extends-component | DocGraph, KyberSquad | 2026-11-02 | archived |
| D3 | [`docs/docgraph/mcp-runbook.md`](../../docgraph/mcp-runbook.md#git-worktrees-and-mcp-binding), [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md#task-execution-runbook-inlined-packets-fast-testing-and-completion-digests) | extends-component | DocGraph, KyberSquad | 2026-11-02 | archived |
| D4 | [`products/kyber-squad/mcp.json`](../../../products/kyber-squad/mcp.json), [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md#concurrency-and-scheduling-model-worktree-and-artifact-isolation), [`AntigravityRenderer.cs`](../../../src/KyberWeave.Core/Squad/Rendering/AntigravityRenderer.cs), [`FactoryRenderer.cs`](../../../src/KyberWeave.Core/Squad/Rendering/FactoryRenderer.cs) | extends-component | KyberSquad | 2026-11-02 | archived |
| D5 | [`docs/docgraph/mcp-runbook.md`](../../docgraph/mcp-runbook.md#git-worktrees-and-mcp-binding), [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md#task-execution-runbook-inlined-packets-fast-testing-and-completion-digests), [`products/kyber-squad/agents/conductor.md`](../../../products/kyber-squad/agents/conductor.md) | extends-component | DocGraph, KyberSquad | 2026-11-02 | archived |
| D6 | [`docs/docgraph/mcp-runbook.md`](../../docgraph/mcp-runbook.md), [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md), [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md), [`products/kyber-squad/agents/conductor.md`](../../../products/kyber-squad/agents/conductor.md), [`products/kyber-squad/agents/conductor/references/execution-and-review.md`](../../../products/kyber-squad/agents/conductor/references/execution-and-review.md) | extends-component | DocGraph, KyberSquad | 2026-11-02 | archived |
| H1 | [`docs/docgraph/mcp-runbook.md`](../../docgraph/mcp-runbook.md#git-worktrees-and-mcp-binding) | extends-component | DocGraph | 2026-11-02 | archived |
| H2 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md#concurrency-and-scheduling-model-worktree-and-artifact-isolation) | extends-component | KyberSquad | 2026-11-02 | archived |
| H3 | [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md#task-execution-runbook-inlined-packets-fast-testing-and-completion-digests) | extends-component | KyberSquad | 2026-11-02 | archived |
| H4 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md#concurrency-and-scheduling-model-worktree-and-artifact-isolation) and [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md#task-execution-runbook-inlined-packets-fast-testing-and-completion-digests) (conductor instructions updated in canonical source `products/kyber-squad/agents/conductor.md`) | extends-component | KyberSquad | 2026-11-02 | archived |
| H5 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md#concurrency-and-scheduling-model-worktree-and-artifact-isolation) and [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md#task-execution-runbook-inlined-packets-fast-testing-and-completion-digests) (worktree isolation reference updated in canonical source `products/kyber-squad/agents/conductor/references/execution-and-review.md`) | extends-component | KyberSquad | 2026-11-02 | archived |
| H6 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md#concurrency-and-scheduling-model-worktree-and-artifact-isolation) (squad MCP server declaration in canonical configuration `products/kyber-squad/mcp.json`) | extends-component | KyberSquad | 2026-11-02 | archived |
