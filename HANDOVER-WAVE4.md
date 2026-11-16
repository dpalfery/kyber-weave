# Handover: finishing the layered refactor (wave 4 and the final pass)

Written for the next agent (opencode). Read `REFACTOR-LAYERS.md` (plan, layering rules, how to run a package) and `AGENTS.md` first. **The goal is the complete refactor: every entry-app folder thin, no legacy projects left. Do not report a partial slice as done.**

## Where things stand

- PR [#619](https://github.com/dpalfery/kyber-weave/pull/619), branch `claude/refactor/layers-wave-1`, holds waves 1-3 merged: layers `Kernel, Domain, Contracts, Data, Application` plus the `Hooks` entry app. The full product suite passed on it (9,666 pass; 2 fail only when run as root: `ProcessConsoleCaptureTests.UncapturedExecutionRoutesToOriginalFallbackConsole` and `RepositoryDoctorTests.Doctor_CodegraphDirUnwritable_Exits1WithRemedy_DoesNotDelete`). `main` already has the squashed #616.
- Wave 4 branches below are **work in progress, unmerged, and may not build**. Each is based on #619's head `591de870`. Start by checking out the branch, running `dotnet build KyberWeave.sln -c Release`, and finishing or discarding what is half-edited.
- Wave-3 numbers (lines still holding logic): Core forwarders ~850, Core.Docs ~1.65k, Cli/Commands/Arbiter ~4.0k, Cli/Commands/Squad ~7.5k, Cli/Commands/Review ~3.8k, Cli/Commands/Docs ~3.3k, Mcp ~1.6k (host code that must stay is ~0.5k of it).

## Rules that apply to every task

- Layers: Kernel (no refs) -> Domain (pure) -> Contracts (interfaces; `Repositories/` I/O ports, `Services/` for entry apps) -> Data (adapters) -> Application (use cases, `AddWeaveApplication` wires Data). Entry apps never reference Data. Layers never reference legacy `KyberWeave.Core*`. `tests/KyberWeave.Architecture.Tests` pins this; do not loosen its baselines to make a move pass.
- Thin command = parse, call a registered service, format output. No `File.`/`Directory.`/`Environment.`/`Process`/`DateTime.UtcNow`/static composition in entry-app code.
- Behavior stays byte-identical (CLI output, MCP tool names/schemas/instructions, Arbiter hook output, pinned by tests). Never weaken or skip a test; only repoint/compile-fix them. Windows/macOS-only code moves without logic edits (CI is the only place it runs).
- Move technique: `git mv`, namespaces unchanged (renamed in the final pass), ports in Contracts registered with `TryAdd`, test fakes registered before `AddWeaveApplication`, one public constructor per command. Delete a shim only when its last caller (incl. tests) is converted.
- Spawn sites: new `Process.Start` needs an entry in `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json`; moved ones need their path updated.
- Gates before pushing: `dotnet build KyberWeave.sln -c Release` (0 warnings), `dotnet format whitespace` and `dotnet format style --verify-no-changes`, then `dotnet test` for `tests/KyberWeave.{Architecture,Domain,Application,Hooks,PromptContent,RepoPolicy}.Tests` and the touched classes in `tests/KyberWeave.Tests` (xUnit v3, Microsoft.Testing.Platform: `--filter-class`). Run the full `tests/KyberWeave.Tests` once at the end. Docs: `codegraph sync`, then `kyber-weave docs validate . --merge-ready` and `docs drift .` (ADR `code-refs` must name live symbols). Push only `claude/*` branches. CI: only "CI Summary" is required; re-running an old run cancels the newest, so do not.
- Merge tip: registration files (`ServiceCollectionExtensions.cs` in Data/Application) conflict often; union both sides but keep every line (do not dedupe `{`/`}`).

## Tasks and branches

Each branch: `origin/claude/refactor/wave4-<ID>`.

### RG: Review gates, seats, catalogue, cycle (`wave4-RG`)
- **Scope:** `src/KyberWeave.Cli/Commands/Review/` except the verdict command: `ReviewGatesCommand` (~1,150 lines), `ReviewSeatsCommand` (~300), `ReviewCatalogueCommand`, suppressions, duplicates, cycle. Move `PlanTaskTarget` and `PacketTestCommand` from `Core.Docs` to Application.
- **Done in the WIP commit:** `ReviewCommandModels`, `ReviewCycleView`, `ReviewGateReporting` (411 lines), `ReviewOutcomeCodes`, `TaskGateNextStep` moved under `Review/Commands/`; the old CLI `ReviewCycleView` removed. Callers not yet repointed, so it does not build.
- **Left:** repoint callers, extract gate planning/validation/report writing into Domain+Application services (`ITaskGateSelectionService` and the config repository are already registered), finish seats and catalogue, move PlanTaskTarget/PacketTestCommand.
- **Pinned by:** `ReviewGates*`, `ReviewSeats*`, `ReviewCycle*`, `ReviewCatalogue*`, `TaskGateSelectionTests`, `PacketTestCommandTests`.

### VD: Review verdict, Run, Rendering (`wave4-VD`)
- **Scope:** `ReviewVerdictCommand` (~1,280), `ReviewDecisionModelFactory` and the review-side Arbiter key/provider calls (replace `ArbiterCommandComposition.TryResolveKey/CreateProvider` with the registered key service/provider factory), `Commands/Run/**` (`RunDeliverCommand` -> `IRunDeliveryRunner`; `RunStartCommand` brief/report -> `ISquadBriefService`), `Cli/Rendering/**` (remove the `Environment.CurrentDirectory` read in `ReportRenderer`).
- **Done in the WIP commit:** `IConfigLoadService`/`ConfigLoadService`, `ISquadBriefBuilder` with `SquadBriefBuilder` moved to Application (`Squad/Briefs`), `SquadBriefRequest` in Domain, `RunStartPlanner` additions, `RunStartCommand` shrunk, `RunDeliverCommand` and `SquadBriefCommand` edited.
- **Left:** compile-fix, the verdict command itself (validation, cycle, report writing, orchestration to Application), rendering.
- **Pinned by:** `ReviewVerdict*`, `RunStart*`, `RunDeliver*`, `SquadBrief*`.

### AR: Arbiter CLI leftovers (`wave4-AR`)
- **Scope:** `ArbiterAuditCommand` (444), `ArbiterTuneCommand` (406), `ArbiterInstallCommand` (717: config merge, key flow), `ArbiterDoctorCommand` (489), `ArbiterPlanCommand` (158), `SectionSelector` (static `BoundedFileReader`), `ArbiterHookRefresher` (487; make it a container service, `SelfUpdater` already takes `IArbiterHookRefresherFactory`), delete `UserHostConfig` (use `IUserHostConfigStore`; callers include `CliArbiterRefreshTargets`, `SelfUpdater`, tests) and `ArbiterCommandComposition` (3-member shim), and `SquadHostDefaults` use inside Arbiter code.
- **Done in the WIP commit:** `IArbiterConfigResolver` contract and `ArbiterConfigResolver` rewrite (now meant to use `IKyberWeaveConfigRepository`), registration lines.
- **Left:** everything else in scope.
- **Pinned by:** the `Arbiter*RenderingTests`, `ArbiterInstallCommandTests`, `ArbiterUninstallCommandTests`: hook-file output must stay byte-identical for Claude, Codex, Copilot, Cursor, Kilo, Factory, Devin, Pi, Antigravity.

### SQ: Squad CLI leftovers (`wave4-SQ`)
- **Branch has no changes** (equals #619's head). Start from scratch.
- **Scope:** `Commands/Squad/**` still holding logic: `SquadDoctorCommand` (1,010), `SquadStatusCommand`, `CopilotPromptCommands` (517; a streaming console callback is fine), `SquadInstallCommand` leftovers, `SquadTargetRootConfirmation`, delete `SquadCommandComposition` statics and `SquadHostDefaults` (callers include Arbiter code and tests), `SquadBriefBuilder` (VD moved it), the physical-root port (`SquadPhysicalRootIdentity`, `SquadFileSystemPathSemantics`: decided: add a Contracts port and inject it into the Domain APIs that use it, `SquadPathPolicy`, `SquadDeploymentPlan.ResolveOwnedFilePath`, `StatusLineReceiptFormat`, updating ~65 test sites), `SquadWorktreeMcp` (no production caller; move it to its layer, or delete it with its contract test only if nothing references it), `TryLoadScopeConfig` (-> `IKyberWeaveConfigRepository`), `SelfUpdater` direct `File/Directory/Environment` use (behind `ISelfUpdateSystem`), the leftover Data statics (`SquadStateStore`, etc.).
- **Pinned by:** `Squad*` tests, `UpdateCommandTests`, `SelfUpdater*`; CI also runs the update loop on ubuntu/macOS and the filesystem contract on macOS/Windows: keep platform bodies unedited.

### DC: Docs CLI and MCP leftovers (`wave4-DC`)
- **Scope:** `Core.Docs`: `DocsScaffolder` (1,100), `ReviewBlockWriter` (259), `McpDeclarationReader`, `PlanPathResolver`, `LegacyPorts` (RG owns `PlanTaskTarget`/`PacketTestCommand`). `Commands/Docs`: `DocsInitCommand`, `DocsOnboardingCommand`, `DocsMcpWireCommand`, `DocsMcpMigrateCommand`, `DocsCommandComposition` (builds analysis runtime/SQLite/embeddings with concrete classes), `DocsPacketCommand` (`new GitFacts()`), shared `PlanArguments`/`CommandHelpers.TryLoadConfig`. `Mcp`: `CodeGraphFreshness` (TimeProvider + provisioner port), `HostConfiguration` and `DocsRootRegistry.ResolveOntology` (-> config repository), `RepositoryRootResolver`. Host code that must stay: `Program`, `DrainingStreamServerTransport`, `DocsServerInstructions`, the `DocsTools*` adapters. MCP tool names/descriptions/schemas/instructions stay byte-identical.
- **Also:** verify `PlanFinalize.IsLink` (now `IWritableFileSystem.GetLinkTarget`) treats a dangling symlink exactly like the old `FileInfo.LinkTarget` check, with a dedicated test.
- **Done in the WIP commit:** `DocsScaffoldTemplates` and `ScaffoldModels` extracted (templates and models split out of the scaffolder), a `PlanFinalizeTests` case. Scaffolder itself not yet converted.
- **Pinned by:** `DocsScaffolderTests`, `DocsInit*`, `Mcp*` tests (`McpPackagingTests`, `McpStdioTransportTests`, `McpToolNameContractTests`), `DocsPacketCommandTests`.

### DM: Domain/Kernel purity and the bug fixes (`wave4-DM`)
- **Scope A:** Domain and Kernel must be pure. Grep them for `File.`, `Directory.`, `Environment.`, `Process`, `DateTime.UtcNow`, `Console.` and move each use behind a Contracts port with a Data adapter (or inject it). Known: the skills/agents governance library (`SkillLoader`, `SkillParser`, `SpecValidator`, `RoutingEvalFile.Load`, `AgentLoader`, the agent parsers, `SkillScanner`, `AgentSyncLinter`) calls `File.*`/`Directory.*` directly. Not started.
- **Scope B, bug fixes (done in the WIP commit, verify):** (1) `FailOnPolicy` (`skill scan` / `agent scan` `--fail-on`) upper-cased the value and compared with lower-case names, so only `critical` ever applied; it now lowercases. This changes CI exit codes for `--fail-on warning|error` to match `docs/ci-pipelines/architecture.md`; new `FailOnPolicyTests`; docs lines updated. (2) `AnalysisPersistenceTests.IsStandardInputOpen_WhenPipeBroken_ReturnsFalse` was flaky under parallel process forks; it now retries the write+flush over a bounded 5 s window until `IOException`.
- **Pinned by:** `FailOnPolicyTests`, skill/agent command tests, `AnalysisPersistenceTests`.

### Dash CI fix (not started)
`main` and #619 both fail the CI job "Dash harness-collection e2e": `dash/e2e/harness-collection.spec.ts:416` ("the Factory (Droid) replay"), line 474 expects the span inspector to contain `FACTORY_REPLAY_MODEL` but shows the `factory.turn` span with no attributes (wrong span opened or attributes missing). Find the real cause (product code or a race in `openPromptTurnInspector`), fix without skipping or blind retries, and ship it as its own PR from `origin/main` (the user owns the Dash CI issue). Then merge `main` into #619.

## Final pass (after wave 4 is merged, in this order)

1. Merge each wave-4 branch into `claude/refactor/layers-wave-1` (or a fresh integration branch), run all gates, push, wait for CI green.
2. Delete leftover shims (the ~850-line Core forwarders and any CLI static composition) once nothing calls them.
3. Mechanical namespace rename `KyberWeave.Core.*` -> layer namespaces (update `InternalsVisibleTo`, ADR/docs `code-refs`, source-root docs, the spawn allowlist paths).
4. Split `tests/KyberWeave.Tests` (5,985 test methods) into layer-shaped projects (Domain/Application/Data/entry-app), and register them in CI, `scripts/fast-loop.sh` and the review gates. Goal: fast unit layers, few slow real-I/O tests.
5. Delete the legacy projects (`Core`, `Core.Docs`, `Core.Squad` shell), rename `Kernel` -> `Core`, remove the `CS1591` suppression, update `LayerReferenceTests`/`CoreAssemblySplitTests`.
6. Write the superseding ADR for ADR 0057 (assembly split); wire the `kyber-weave-hooks` tool into CI/release/SBOM publish; update `REFACTOR-LAYERS.md`.

## Open decisions (ask the owner, recommendation first)

- Resolve the open Kilo review threads on the hook (PathContainment critical, symlink, raw-string, FileExists, YAML fail-open, filter-class): recommended yes.
- `.kyber-weave/hooks.yml` trust model: recommend repo config may only tune built-in rules; arbitrary commands only from the user-level file.
- Jev judge for the edit hook is not implemented (documented as a planned seam).
- Speed branch `claude/perf/unit-test-runtime` (unmerged): decide whether to open it as its own PR after the test split, since the split changes what it optimizes.
- Interim branches `claude/refactor/squad`, `claude/refactor/runstart-cli-seams` and the empty `claude/refactor/wave4-SQ` can be deleted.
