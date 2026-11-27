# Layered refactor: status, plan and how to run a package

This is the working plan for moving the Kyber-Weave engine onto the shared layers (Kernel, Domain, Contracts, Data, Application), the same shape as `dash/server/`. It exists so any machine or any agent (Claude Code, OpenCode, another) can pick up a package without this session's history.

## Where it stands

Measured on PR #616's head, lines of C#:

| | Lines | Share |
|---|---|---|
| Already in the layers (Kernel 2.2k, Domain 5.5k, Contracts, Data, Application, Hooks) | about 8,500 | about 6% |
| Legacy engine still to move: Core 40.5k, Core.Docs 26.5k, Core.Squad 30.5k | about 97,500 | |
| Entry apps still holding logic: Cli 29.6k, Arbiter 10.9k, Mcp 3.3k | about 43,800 | |

Done: layer projects and the architecture test, the edit hook built on them, leaf folders into Kernel, Agents, Skills and Security into Domain, 21 Docs and 6 Squad pure files.

## Packages (run in parallel, one branch each)

Every package branches from the integration base and is merged back by pull request. The base is the head of PR #616 until it merges, then `main`. Package names match the ownership table in the brief below.

| Package | Scope | Order and notes |
|---|---|---|
| S Squad | `Core.Squad/**`, `Core/Squad/**`, plus `Configuration/McpServerNames.cs` and `McpConfig.cs` | Hub types first (`SquadTarget`, deployment scope and file, renderer contract), then Rendering, then Deployment stores and services |
| D Docs | `Core.Docs/**`, `Core/Docs/**` | Pure models to Domain; analysis, search and validation to Application; loaders, SQLite persistence, embeddings and `gh` delivery to Data; analyzer and scaffolder to Application |
| C Config, Review, Arbiter | `Core/Configuration/**` (minus S's two files), `Core/Review/**`, `Core/Arbiter/**` | These three reference each other; move pure records first, then I/O, then services. Largest and hardest package |
| C2 Doctor and the rest of Core | `Core/{Doctor,CodeGraph,CopilotPrompt,Utilities}/**` and root files | Depends on C for configuration types |
| E RunStart and CLI seams | `Core/RunStart/**`, RunStart and Utilities CLI commands, CLI composition | Builds the shared environment, file system and process contracts and the Spectre type registrar the rest of the CLI will reuse |
| T Prompt-content tests | `tests/**` | New `KyberWeave.PromptContent.Tests` project for tests that assert skill, agent and instruction prose |

After these, wave 2: thin the entry apps onto the layers (Docs MCP, then Arbiter, then CLI by command folder), split the product test project into layer projects, write the superseding ADR for the assembly split, delete the three legacy projects, rename Kernel to Core, and remove the temporary `CS1591` suppression.

## Run a package on your own machine (Claude Code or OpenCode)

```bash
git fetch origin
git worktree add ../kw-<pkg> -b w1-<pkg> origin/claude/refactor/shared-layers-and-edit-hook
cd ../kw-<pkg>
```

Then start your agent in that directory with this prompt, replacing `<PKG>`:

> You are agent `<PKG>`. Read `REFACTOR-LAYERS.md` completely, in particular the brief at the bottom, and follow it exactly for the package named `<PKG>` in the Packages table. Finish your whole scope; do not stop at the first stall. Commit in clusters, never push to `main`, and send me the final report in the format the brief asks for.

When it reports, push the branch under a name that fits the repo's pull request naming rule (`claude/refactor/<slug>` or another `<harness>/<type>/<slug>`), and open a draft pull request against the integration base. Conflicts are expected only in lock files, csproj files, `LayerReferenceTests`, the spawn-sites allowlist and path citations; resolve them by merging the base, never by rebasing.

## Merge protocol

1. Each package opens its own pull request against the integration base and runs the normal gates.
2. Merge packages in any order; each rebuilds green on its own.
3. Types owned by another package that block a cluster are recorded in the report as `BLOCKED-BY`; they are resolved in the next wave once the owner has moved them.

---

# Layered refactor: brief for every agent (read fully, then follow)

You are one of several agents working IN PARALLEL on the same refactor, each in its own git worktree and its own area. Your worktree is `/home/user/kw-wt/w1-<X>` (branch `w1-<X>`, cut from PR #616's head). Use absolute paths there. Never touch another worktree. Never push. Never switch branches.

## What the refactor is
Move the legacy engine (`src/KyberWeave.Core`, `src/KyberWeave.Core.Docs`, `src/KyberWeave.Core.Squad`) and the logic inside the entry apps (`src/KyberWeave.Cli`, `src/KyberWeave.Mcp`, `src/KyberWeave.Arbiter`) onto five shared layer projects, in the same layering as `dash/server/` (KyberDash): interface first, domain driven, IoC.

| Layer | Holds | May reference |
|---|---|---|
| `KyberWeave.Kernel` | primitives: processes, file helpers, text, parsing | nothing |
| `KyberWeave.Domain` | data only: records, DTOs, enums, options, constants (`AGENTS.md`, ADR 0089). Logic goes in Application | Kernel |
| `KyberWeave.Contracts` | interfaces only. `Repositories/` = I/O ports, `Services/` = what entry apps call | Domain, Kernel |
| `KyberWeave.Data` | adapters that implement Contracts: file system, processes, sqlite, HTTP, credentials, git | Contracts, Domain, Kernel |
| `KyberWeave.Application` | use cases and pipelines, `AddWeaveApplication` wiring (it wires Data) | Contracts, Data, Domain, Kernel |

Layers NEVER reference the legacy projects. Legacy projects reference the layers (add the ProjectReference to a legacy csproj when it needs a moved type). Entry apps never reference Data directly. Reference rules are pinned by `tests/KyberWeave.Architecture.Tests/LayerReferenceTests.cs`; update its allowed sets only when a legacy project legitimately gains a layer reference.

Read first: `AGENTS.md`; the worked example of the target style in `src/KyberWeave.{Domain,Contracts,Data,Application}/Hooks` (interface in Contracts, adapter in Data, use case in Application, `TryAdd` registration in `AddWeaveData*` / `AddWeaveApplication`); and `git log --stat -- src/KyberWeave.Domain src/KyberWeave.Kernel` for the mechanical move technique already used.

## Your job is to FINISH YOUR AREA, not to take one pass
Do not stop at the first stall and report. Keep going until everything in your scope is moved, or only true blockers remain.

1. **Inventory** your scope: files, lines, and per file whether it is pure, I/O, or orchestration.
2. **Move bottom-up by dependency order** with `git mv`. Keep namespaces unchanged in this pass (a later mechanical commit renames them). No behavior change; output must stay byte-identical.
   - A file that mixes a pure type with I/O code: split it into two files; the pure part moves.
   - A hub type blocks a cluster: move the hub and its pure dependency closure first. Do not skip the cluster.
   - A type that will live in Domain/Application calls static I/O (`File.`, `Directory.`, `Process`, `Environment`, `HttpClient`, `Console`, git): extract an interface into Contracts named by capability (reuse existing ones in `Contracts/Repositories`; clocks use `TimeProvider`), implement it in Data, inject by constructor, register with `TryAdd`.
   - Orchestration and use-case classes go to Application behind a `Contracts/Services` interface; entry apps keep calling them (minimal legacy wiring so everything compiles).
3. **True blockers only**: a type owned by another agent's area (ownership table below), or something that needs a product decision. Record each as `BLOCKED-BY <area>: <type> in <file>` and continue with everything else.
4. **Resources**: an `EmbeddedResource` owned by moved files moves with the same `LogicalName` (each layer csproj imports `Areas\*.props`; add `Areas/<Area>.props`). Resource readers use `typeof(Anchor).Assembly`, so reader and resource must land in the same assembly. Tests that scan assemblies for resource names (`SquadCliCommandTests`, `SquadPackAndReleaseTests`) must still pass; extend them for new assemblies if needed. Missing NuGet packages go into the same props file at the version Core uses.
5. **Citations**: update old-path citations (docs/ except archive, products/, .github/, scripts/, `.kyber-weave/kyber-weave.yml`, and the path keys in `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json`) with scripted replaces, in their own commit. If a moved file contains a process spawn site, update only the path in the existing allowlist entry.
6. **Tests**: do not weaken, skip or delete any test; no `NoWarn` or `#pragma`; touch tests only to compile or to point at new paths. Do NOT optimize or speed up tests (that is out of scope and a known waste: those tests are being restructured later).
7. **Verify after every cluster**, then commit it:
   - `dotnet build KyberWeave.sln -c Release` (0 warnings, 0 errors)
   - `dotnet format KyberWeave.sln whitespace --verify-no-changes -v minimal` and `... style --verify-no-changes --severity warn -v minimal`
   - `dotnet test --project tests/KyberWeave.Architecture.Tests -c Release`
   - `dotnet test --project tests/KyberWeave.RepoPolicy.Tests -c Release --filter-class "KyberWeave.Tests.ProcessSpawnSitesTests" --filter-class "KyberWeave.Tests.CoreAssemblySplitTests"`
   - the product test classes that cover what you moved: `dotnet test --project tests/KyberWeave.<Project>.Tests -c Release --filter-class "<namespace>.<Class>"` (find them by name and grep; never the whole suite)
   Commit one cluster per commit, message `refactor(<area>): <what>`; trailers after a blank line: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_019dANC4GH6AXF82GAgUdBAt` (Haiku agents use `Claude Haiku 5.5`). Never leave a red build in a commit.
8. The machine has 4 cores shared by about six agents, so builds are slow. Be patient, do not kill other processes, do not run the whole product suite.

## Ownership (do not edit files in another agent's scope; if blocked by one, record BLOCKED-BY and move on)
- **S (Squad):** `src/KyberWeave.Core.Squad/**`, `src/KyberWeave.Core/Squad/**`, and `src/KyberWeave.Core/Configuration/McpServerNames.cs` + `McpConfig.cs`.
- **D (Docs):** `src/KyberWeave.Core.Docs/**`, `src/KyberWeave.Core/Docs/**`.
- **C (Config, Review, Arbiter):** `src/KyberWeave.Core/Configuration/**` (except the two files S owns), `src/KyberWeave.Core/Review/**`, `src/KyberWeave.Core/Arbiter/**`.
- **C2 (Doctor and the rest of Core):** `src/KyberWeave.Core/{Doctor,CodeGraph,CopilotPrompt,Utilities}/**` and any other Core folder or root file not named here (Skills, Agents, Security, Kernel folders are already moved).
- **E (RunStart, CLI seams):** `src/KyberWeave.Core/RunStart/**`, the CLI commands for RunStart and Utilities under `src/KyberWeave.Cli/Commands/**`, the CLI composition (type registrar), and their tests.
- **T (tests):** `tests/**` only, the new prose-test project.

## Stuck rule
If the same error survives two attempts, use the advisor if one is configured or try another approach; if still stuck, record BLOCKED and continue elsewhere. Never weaken a test or add a suppression to get green.

## Final report (required format)
1. Scope at start: files and lines.
2. Moved: files and lines per layer (Kernel/Domain/Contracts/Data/Application).
3. Remaining in legacy: files and lines, with the reason per cluster: `BLOCKED-BY <area>: <type>`, `needs decision: <what>`, or other.
4. Fraction of your scope done (lines moved / lines at start).
5. Commits (hash + one line), test results (class names and counts), anything you could not verify.
