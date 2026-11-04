---
id: plans/2026-11-03-arbiter-one-command-install
title: Arbiter one-command install — `kyber-weave arbiter install` and update-time hook refresh
doc-type: plan
status: current
component: KyberArbiter
owner: dpalfery
created: 2026-11-03
last-reviewed: 2026-11-03
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberArbiter, KyberSquad, Distribution]
---

# Arbiter one-command install — `kyber-weave arbiter install` and update-time hook refresh

**Status: Ready — approved for execution 2026-11-03 (AP1, `AUTONOMOUS_APPROVAL: approve-and-execute`,
provenance `autonomous: recommendation accepted`).** No decisions remain open; the permanent
`## Decisions` table below records the baseline choices, the autonomous recommendations accepted
during planning, the gate-off bootstrap this dispatch runs under (D10), and the approve-and-execute
gate (AP1). The first task (T1) fixes the `KW-ARB-PLANNER-001` gate that currently rejects every
real `PLAN_FILE: docs/plans/<file>.md` header as `malformed`, so a planner dispatch naming a real
plan file no longer needs the exception. Frontmatter `status` stays `current` because the
frontmatter vocabulary has no `ready` value; the lifecycle Ready is this body status line and the
plan index. **Development mode:** `test-first` (packet default; no user opt-out). **Autonomy:**
`autonomous` (packet value and repository Config Reg `<autonomy>`). **Scale:** `extends-component`,
components `KyberArbiter`, `KyberSquad`, `Distribution`.

## Problem and goal

**Problem.** Enabling the Kyber Arbiter today is five separate, order-sensitive steps: `docs init`,
hand-edit `arbiter.enabled: true`, `squad install --target <t>`, grant the per-harness trust gate,
then `arbiter setup` with a key. Each step is a place to get the order wrong or to leave a half-on
installation that fails closed (`KW-ARB-HOOK-001`) the moment a hook fires. `kyber-weave update`
replaces the `kyber-weave-arbiter` binary but never refreshes the rendered hook files, so a shim or
owned-block fix shipped in the binary can leave an already-deployed project running the old hook
source indefinitely.

**Goal.** One invocation does everything, and the update path keeps the hooks in step with the
binary:

```bash
kyber-weave arbiter install --target kilo --provider "" --arbiter-key "<key>"
```

- `--target` is required and validated through `SquadTargetCatalog`; `--provider ""` means `none`;
  `--arbiter-key` stores the key in the OS credential store.
- One run ensures `.kyber-weave/kyber-weave.yml` exists (reusing the `docs init` scaffold), sets
  `arbiter.enabled: true` non-destructively, renders the target through the existing Squad
  install/update pipeline (hooks, agents, MCP grants, receipt), applies the provider config, then
  verifies (`kyber-weave-arbiter` binary probe, config load, key resolves) and prints the target's
  trust step.
- Idempotent: a second run is a no-op with a status line. `--dry-run` and `--yes` are added;
  `--global` is rejected (no project config means no hooks).
- `arbiter uninstall --target <t>` removes only Squad-owned files/blocks; key deletion is opt-in.
- The key is never printed and never written to config, logs, or git. `--arbiter-key` is warned as
  argv/shell-history visible; `--key-stdin`/env stay the safer path; the exception is recorded in an
  ADR (amended 0053).
- The Kilo plugin shim binds and verifies the resolved repo root, so a host whose cwd is not the repo
  cannot trigger `KW-ARB-HOOK-001` from a wrong working directory.

## Decisions

Permanent state, preserved across the plan's whole lifecycle. All rows are answered; no question is
open.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Reuse the Squad render and `arbiter setup` code, or reimplement? | Reuse. `arbiter install` composes `SquadCommandComposition`/`SquadLifecycleService.InstallAsync` for the render, and reuses `ArbiterCommandComposition` (credential store, key resolution) and `ArbiterSetupCommand`'s provider/override rendering. | Constraint in the request; also honours ADR 0053 D2 (engine in Core, one render pipeline) and the "one shared render pipeline" refactor (#511). Reimplementation would drift from the 13 renderers. | ANSWERED — request constraint; ADR 0053 D2. |
| D2 | Accept `--arbiter-key` on argv even though argv/shell history exposes it? | Accept the flag (required behaviour), print a one-line warning that it is visible in argv and shell history, and retain `--key-stdin` and `TYPESAFE_API_KEY` env as the safer paths. The key is stored in the OS credential store and appears in no file, log, or git. | ADR 0053 D18 said keys travel on stdin, never argv. This is an explicit, owner-directed exception: the one-liner requires the key on the command line. The warning names the safer alternatives so the operator can choose them. | ANSWERED — request requirement; exception recorded in ADR 0053 D18 (T7). |
| D3 | What does `--provider ""` mean? | `none`. Empty or whitespace maps to `ArbiterProviderKind.None`, matching `ArbiterSetupCommand.TryParseProvider`, which already treats null/whitespace as `None`. No new provider value. | ADR 0053 D19: provider `none` switches model rules off; step 0 and step 2 still run. The existing parse already answers this; only the flag wiring is new. | ANSWERED — existing `TryParseProvider` behaviour; ADR 0053 D19. |
| D4 | How to set `arbiter.enabled: true` non-destructively? | Add `HostConfigYaml.WithArbiterEnabled(string yaml)`, the same line-based editor as `WithDocsRoot`/`WithTechnologies`: flips or appends the `arbiter:`/`enabled:` keys, preserves comments, key order and line endings, and is idempotent. When no config exists, reuse the `docs init` host-config creation path. | A deserialize/re-emit round-trip would drop comments and unknown keys (operator state). `HostConfigYaml` already proves the text-edit pattern for the same file. | ANSWERED — autonomous: recommendation accepted (HostConfigYaml precedent, covered by `DocsScaffolder`/`HostConfigYamlTests`). |
| D5 | How does the Kilo shim stop the cwd-dependence that triggers `KW-ARB-HOOK-001`? | The shim resolves the repo root by walking up from its own plugin directory to the nearest `.kyber-weave/kyber-weave.yml` (fallback `.git`), binds that root into both the spawn `cwd` and the envelope `cwd`, and throws a clear block when no root resolves — never spawning with a possibly-wrong cwd. | The hook host (`HookCommand.RepoRootOf`) already prefers the payload `cwd` over `Directory.GetCurrentDirectory()`; the gap is that the shim passed the harness-reported `directory` unverified. Binding + verifying in the shim fixes it at the source for Kilo, OpenCode and Pi (same `ArbiterPluginShim`). | ANSWERED — autonomous: recommendation accepted (root cause named by the request objective). |
| D6 | Should `arbiter uninstall` delete the stored key? | No, unless asked: add `--remove-key` (opt-in). Key deletion never happens automatically. | Aligns with ADR 0053 D18 (the key lives in the OS store, outside Squad's ownership); silently deleting a credential on uninstall would be hard to reverse. | ANSWERED — autonomous: recommendation accepted (key never auto-deleted). |
| D7 | How does `kyber-weave update` refresh arbiter hooks/scripts? | In-process reuse: after replacing binaries and only when an arbiter is installed, re-render arbiter hooks for each registered project with `arbiter.enabled` by reusing `SquadLifecycleService.UpdateAsync` + `SquadStateStore.ListProjects()`, through a new testable seam on `SelfUpdater`. | The shim/hook source is embedded in the CLI/Core (`ArbiterPluginShim.Render`), so a binary update must re-render to sync deployed files. Reusing the squad update path reuses the receipt/conflict/owned-block logic instead of a bespoke copier. | ANSWERED — autonomous: recommendation accepted (reuse squad update; mirrors the in-run capture-plugin steps). |
| D8 | Amend ADR 0053 or add a new ADR? | Amend ADR 0053 in place: D18 (argv key exception) and D22 (one-command install replacing the three-part install). | The repository's precedent is in-place amendment (ADR 0031, 0034). D18/D22 are reversals of a single accepted decision, not a new boundary. | ANSWERED — autonomous: recommendation accepted (in-place amend precedent). |
| D9 | Exit codes and distinct failure codes for the new verbs? | `arbiter install`/`uninstall` follow the squad convention: `2` for client-input errors (invalid target, `--global` on install, missing key, invalid provider), `1` for operational failures, `0` for success or an idempotent no-op. Missing key surfaces the existing `KW-ARB-KEY-001`; an invalid target names the target and the roster (reusing `SquadTargetCatalog`'s message). | Mirrors `squad install` (`2` client input, `1` other) and `ArbiterStatusCommand.KeyMissing = "KW-ARB-KEY-001"`. | ANSWERED — squad exit-code convention; existing `KW-ARB-KEY-001`. |
| D10 | Fix `KW-ARB-PLANNER-001` first (test-first, with a regression test) and record the gate-off bootstrap this plan runs under? | Yes to both. This plan is authored under the documented bootstrap exception (`arbiter.enabled: false`), because `KW-ARB-PLANNER-001`'s `matches: config.planning-dirs` decide clause compares the raw `delegation.plan-file` against literal directory strings (`docs/plans`, `docs/specs`, `docs/todo`) through `PathGlob`, so a file beneath a planning directory never matches and falls through to `malformed`, while the bare `docs/plans` directory matches. T1 (the first task) fixes it test-first: emit `config.planning-dirs` as directory-prefix globs (`docs/plans/**`, `docs/specs/**`, `docs/todo/**`) from `TriggerFactBuilder.PlanningDirs`, so a `PLAN_FILE` naming a file inside a planning directory answers `recognised`. The id stays permanent (no renumber, no new `KW-ARB-*` id). | The one-liner's own install/update work depends on planner dispatches that name `docs/plans/<file>.md`; keeping the gate broken means every such dispatch needs the exception. The `**` idiom is the one the `OWNER-001` map and `LENS-001` already use. The same change also repairs `KW-ARB-PLANNER-DIFF-001`'s `subset-of: config.planning-dirs`, which `IsCovered` evaluates through `PathGlob` and so currently fails to count a file write beneath a planning directory as `inside`. | ANSWERED — autonomous: recommendation accepted; gate-off bootstrap recorded here (auditable run decision/finding). |
| D11 | May T1 also fix `KW-ARB-READY-001`'s empty-dependency handling? | Yes. T1 adds a `count: 0` → `ready` clause before the subset check; the first T1 dispatch was blocked because an empty `plan.task.depends-on` list was compared with the absent `ledger.completed-tasks` fact and answered `waiting`. | This is the same execution-blocking gate class as D10. It is fixed test-first in T1 with the bootstrap gate-off exception, then the gate is re-enabled. | ANSWERED — autonomous: recommendation accepted; bootstrap continuation. |
| AP1 | Approve and execute | Approve this plan and its Test contract (T1–T6) for execution at the declared scale — `extends-component` on `KyberArbiter`, `KyberSquad`, `Distribution`. | Conductor explicitly returned `AUTONOMOUS_APPROVAL: approve-and-execute` on 2026-11-03 under resolved autonomy `autonomous` (Config Reg `<autonomy>`). Recorded in the permanent `## Decisions` table per the autonomous-decisions reference and preserved intact across the lifecycle. | ANSWERED — autonomous: recommendation accepted; explicit conductor gate `approve-and-execute`. |

## Scale and Design harvest

`documentation-impact: extends-component`; `impact-components: [KyberArbiter, KyberSquad, Distribution]`.
Each durable design item lands in canonical docs at closeout (T8):

| Target component | Target doc type | Durable design item |
|---|---|---|
| KyberArbiter | runbook | The one-command `arbiter install` flow and its idempotency/verification contract; `arbiter uninstall --target` and `--remove-key`; `--arbiter-key` argv warning and the retained `--key-stdin`/env path. |
| KyberArbiter | architecture | `arbiter.enabled` non-destructive merge (`HostConfigYaml.WithArbiterEnabled`) and the provider `none` default in the one-command path. |
| KyberSquad | onboarding | The Arbiter hooks section updated for the one-command install; the Kilo shim repo-root binding and the trust-step printout. |
| Distribution | install.md | `kyber-weave update` now also refreshes arbiter hooks/scripts; `arbiter install` replaces the multi-step "initialize your repository" sequence. |
| Distribution | cli-command-surface.md | New `arbiter install` / `arbiter uninstall` verbs (the `arbiter` branch is currently absent from this page — a gap closed here), exit codes, and the updated `update` verb. |
| KyberArbiter | adr | ADR 0053 amended in place: D18 argv-key exception, D22 one-command install. |

## Investigation findings

Discovery on this resume run used the Kyber-Weave docs MCP (`docs_explore`, `docs_status` —
provenance `root=/Users/dave/git/personal/kilo/kyber-weave`, rev `50691a5b`) and the
`codegraph_explore` tool for symbol location and blast radius. Three governed-config/test files
were read directly because codegraph does not index the embedded YAML resource or the exact test
pins, and to confirm line-numbered detail the explorer truncated: `src/KyberWeave.Core/Arbiter/Rules/default-rules.yml`,
`tests/KyberWeave.Tests/Arbiter/ArbiterDefaultRulesTests.cs`,
`tests/KyberWeave.Tests/Arbiter/ArbiterFactBuilderTests.cs`,
`tests/KyberWeave.Tests/Arbiter/ArbiterExistsFalseRuleTests.cs`, and
`src/KyberWeave.Core/Arbiter/Rules/RulePredicate.cs` (the `matches`/`subset-of`/`IsCovered` bodies).

Key seams located:

- **`KW-ARB-PLANNER-001` gate bug (T1)** — `src/KyberWeave.Core/Arbiter/Rules/default-rules.yml`
  (rule `KW-ARB-PLANNER-001`, trigger `delegate.planner`) decides `recognised` only when
  `delegation.plan-file matches config.planning-dirs`; the `config.planning-dirs` fact is built by
  `TriggerFactBuilder.PlanningDirs` (`src/KyberWeave.Core/Arbiter/Facts/TriggerFactBuilder.cs`) as
  the literal directories `["docs/plans","docs/specs","docs/todo"]` via `DirectoryOf`. `RulePredicate.EvaluateMatches`
  runs `PathGlob.IsMatch(pattern, path)`, so `docs/plans/2026-11-03-…md` matches no literal directory
  and the fourth clause (`delegation.plan-file exists: true`) answers `malformed`, while the bare
  `docs/plans` directory matches. Fix (D10): emit `docs/plans/**` prefix globs so a file beneath
  matches; `RulePredicate.IsCovered` (used by `subset-of`) also runs `PathGlob`, so the same change
  repairs `KW-ARB-PLANNER-DIFF-001`'s file-write `inside` check. No fact-catalog change is needed:
  `delegate.planner` already declares `delegation.plan-file`, `delegation.markers` and
  `config.planning-dirs`. The shipped-rules catalogue (`ArbiterDefaultRulesTests`) and the
  `config.planning-dirs` pin (`ArbiterFactBuilderTests.FactBuilder_PlanningDirsAreDirectoriesOfIndexes`)
  are the only existing tests touching this.
- **Target validation** — `SquadTargetCatalog` (`src/KyberWeave.Core/Squad/Deployment/SquadTarget.cs`):
  `TryResolve(token, out target)` (single exact token) and `Parse(values)` (comma/repeated). `SquadTarget.Kilo`
  is a member; `ApprovedTargets`/`GetToken` name the roster. This is the `--target` validation seam.
- **Render pipeline** — `SquadInstallCommand`/`SquadUpdateCommand` (`src/KyberWeave.Cli/Commands/Squad/`)
  already render hooks when `SquadCommandComposition.ResolveArbiterWiring(config.Arbiter)` yields
  `Enabled`; the 13 renderers + `ArbiterHookWiring` + `ArbiterPluginShim` (`src/KyberWeave.Core/Squad/Rendering/`)
  produce the hook files/shim and `ArbiterHookWiring.TrustSteps` carries the trust text. `WriteArbiterTrustSteps`
  (`SquadInstallCommand`) already prints it. Nothing here needs reimplementation.
- **Provider/key** — `ArbiterSetupCommand` (`src/KyberWeave.Cli/Commands/Arbiter/ArbiterSetupCommand.cs`)
  renders the user override and stores the key via `ArbiterCommandComposition.CredentialStore().Write(origin, key)`;
  `ArbiterKeyResolver` (`src/KyberWeave.Core/Arbiter/Credentials/ArbiterKeyResolver.cs`) resolves by origin
  (`TYPESAFE_API_KEY` then store); `ArbiterStatusCommand.KeyMissing == "KW-ARB-KEY-001"`. No code accepts an
  argv key today — that is the new surface.
- **Config** — `ArbiterConfig.Enabled` (`src/KyberWeave.Core/Arbiter/ArbiterConfig.cs`) and
  `ArbiterYamlSection.Enabled` (`src/KyberWeave.Core/Configuration/ArbiterYamlSection.cs`) define the key.
  `HostConfigYaml` (`src/KyberWeave.Core/Docs/Scaffolding/HostConfigYaml.cs`) is the line-based non-destructive
  editor (`WithDocsRoot`, `WithTechnologies`) to extend with `WithArbiterEnabled`. `DocsScaffolder.WriteHostConfig`
  (`src/KyberWeave.Core/Docs/Scaffolding/DocsScaffolder.cs`) creates the config when absent — the reuse point.
- **Kilo shim cwd bug** — `ArbiterPluginShim.Render` (`src/KyberWeave.Core/Squad/Rendering/ArbiterPluginShim.cs`)
  spawns `kyber-weave-arbiter hook` with `cwd: directory` and writes `cwd: directory` into the envelope; the
  hook host `HookCommand.RepoRootOf` (`src/KyberWeave.Arbiter/Hooks/HookCommand.cs`) then loads
  `Composition.LoadHostConfig(repoRoot)`, which throws `KW-ARB-HOOK-001` when `.kyber-weave/kyber-weave.yml` is
  absent under that root. A host whose cwd is not the repo passes a wrong `directory` → fail closed. Fix belongs
  in the shim (bind + verify).
- **Update** — `UpdateCommand` (`src/KyberWeave.Cli/Commands/Update/UpdateCommand.cs`) → `SelfUpdater`
  (`src/KyberWeave.Cli/Update/SelfUpdater.cs`): replaces CLI/MCP/arbiter/kyberdash binaries; `ShouldUpdateArbiter`
  gates the arbiter binary. No step refreshes rendered hooks today — the gap to close.
- **Uninstall/owned** — `SquadUninstallCommand` + `SquadLifecycleService.UninstallAsync` reverse receipt-owned
  files and `SquadReceipt.Blocks` (`src/KyberWeave.Core/Squad/Deployment/SquadDeploymentModels.cs`) owned blocks.
  The `arbiter uninstall` seam reuses this.
- **Docs gaps** — `docs/cli-command-surface.md` has no `arbiter` section at all (the branch's verbs are
  undocumented there); `docs/install.md` (`## Then initialize your repository`, `## Updating`),
  `docs/kyber-arbiter/runbook.md` (`## Setup, doctor, audit`), and `docs/kyber-squad/onboarding.md`
  (`## Arbiter hooks`) describe the multi-step path to be replaced.

## Test contract (test-first)

Every implementation task's contract, authored before implementation. RED by `test-dev`; GREEN by the
matching `csharp-dev`; implementers never author or weaken tests.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/Arbiter/ArbiterPlannerRuleTests.cs` (new) + `tests/KyberWeave.Tests/Arbiter/ArbiterFactBuilderTests.cs` (pin update) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterPlannerRuleTests|FullyQualifiedName~ArbiterFactBuilderTests"` | `KW-ARB-PLANNER-001` answers `recognised` for a `delegation.plan-file` naming a file beneath `docs/plans`, `docs/specs`, or `docs/todo`; answers `malformed` for a path outside the planning dirs and for a missing plan-file. `config.planning-dirs` resolves to the prefix globs `docs/plans/**`, `docs/specs/**`, `docs/todo/**`. `KW-ARB-READY-001` answers `ready` for a task whose dependency list is empty even when `ledger.completed-tasks` is absent, and `waiting` for a non-empty unsatisfied list. | New RED cases fail because the current `matches: config.planning-dirs` clause makes a file path answer `malformed`, and because the empty `plan.task.depends-on` list is compared with an absent `ledger.completed-tasks` fact and answers `waiting`. | Same cases pass; the shipped-rules catalogue (`ArbiterDefaultRulesTests`) stays green; no rule id is renumbered or added. |
| T2 | `tests/KyberWeave.Tests/HostConfigYamlTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~HostConfigYamlTests"` | `WithArbiterEnabled` flips an existing `arbiter.enabled: false` to `true`; appends an `arbiter:` block when absent; is idempotent; preserves comments, key order, and line endings; preserves `arbiter.provider`/`rules` when present. | New RED cases fail because `WithArbiterEnabled` does not exist (compile) or returns the input unchanged. | Same cases pass; no assertion weakened; every other byte of the input is byte-identical. |
| T3 | `tests/KyberWeave.Tests/ArbiterShimBehaviourTests.cs` + `tests/KyberWeave.Tests/ArbiterKiloRenderingTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterShimBehaviourTests|FullyQualifiedName~ArbiterKiloRenderingTests"` | `ArbiterPluginShim.Render("kilo")` output contains a root-resolution step walking up for `.kyber-weave/kyber-weave.yml` (fallback `.git`); the spawn `cwd` and the envelope `cwd` are the resolved root; an unresolvable root throws a block instead of spawning. | New RED cases fail because the rendered shim still passes the raw harness `directory`. | Same cases pass; the rendered shim no longer uses an unverified cwd. |
| T4 | `tests/KyberWeave.Tests/ArbiterInstallCommandTests.cs` (new) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterInstallCommandTests"` | Invalid `--target` exits 2 naming the roster; `--global` exits 2; `--provider ""` → `none`; `--provider systemone --arbiter-key <k>` writes `<k>` to the injected fake store for the origin and never into config/receipt/log; systemone with no key/`--key-stdin`/env surfaces `KW-ARB-KEY-001`; a second run exits 0 as a no-op with no duplicate writes; the merge preserves pre-existing config content. | New RED cases fail because `arbiter install` is unregistered or does not yet write/merge. | Same cases pass; key value asserted only via the injected store, never from any file or stdout. |
| T5 | `tests/KyberWeave.Tests/ArbiterUninstallCommandTests.cs` (new) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterUninstallCommandTests"` | `--target kilo` removes only the receipt-owned `.kilo/plugin/kyber-arbiter.ts`/owned blocks and leaves other files; `--remove-key` deletes the fake-store entry, and without it the key is untouched; invalid target exits 2; no deployment exits 0 "nothing to uninstall". | New RED cases fail because `arbiter uninstall` is unregistered or removes nothing owned. | Same cases pass; non-owned files and (without `--remove-key`) the key are untouched. |
| T6 | `tests/KyberWeave.Tests/UpdateCommandTests.cs` (+ new `SelfUpdaterArbiterRefreshTests.cs` if needed) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~UpdateCommandTests|FullyQualifiedName~SelfUpdaterArbiterRefreshTests"` | When an arbiter is installed, `update` invokes the arbiter-refresh seam for registered projects with `arbiter.enabled`; when no arbiter is installed, the seam is not invoked. | New RED cases fail because `SelfUpdater` never calls the refresh seam. | Same cases pass; the seam is a constructor/factory injection and never shells out. |

## Tasks

Mode-aware, dependency-ordered, file-scope disjoint where possible. RED first, then GREEN, per task.
Each developer task ends with the worker's own `task-gate`; a failing gate returns to that worker.

### T1 — Fix the planner and leaf-readiness gate rules (Core rules)

- **Objective.** The planner gate answers `malformed` for every real plan-file header: the
  `matches: config.planning-dirs` decide clause compares the raw `delegation.plan-file`
  (`docs/plans/<file>.md`) against literal directory strings (`docs/plans`, `docs/specs`,
  `docs/todo`) through `PathGlob`, so a file beneath a planning directory never matches and the
  `delegation.plan-file exists: true` clause answers `malformed`, while the bare `docs/plans`
  directory matches. Fix it so a file inside a planning directory answers `recognised`; the
  `KW-ARB-*` id stays permanent (no renumber, no new id).
  The same task fixes `KW-ARB-READY-001`: an empty `plan.task.depends-on` list is compared with the
  absent `ledger.completed-tasks` fact on a fresh run, so every leaf task answers `waiting` and no
  execution can start. Add a `count: 0` → `ready` clause before the subset check.
- **Files.** `src/KyberWeave.Core/Arbiter/Facts/TriggerFactBuilder.cs` (`PlanningDirs`/`DirectoryOf`
  emit `docs/plans/**` prefix globs); `src/KyberWeave.Core/Arbiter/Rules/default-rules.yml` (one
  `count: 0` clause on `KW-ARB-READY-001`, no id change); tests
  `tests/KyberWeave.Tests/Arbiter/ArbiterPlannerRuleTests.cs` (new regression) and
  `tests/KyberWeave.Tests/Arbiter/ArbiterFactBuilderTests.cs` (update the `config.planning-dirs` pin
  to the glob values). The planner rule's ids and other clauses are unchanged; its
  `matches`/`subset-of` clauses now match files beneath the planning dirs because the fact carries
  prefix globs.
- **Skills.** RED `test-dev`; GREEN `csharp-dev`.
- **Acceptance.** Contract row T1 GREEN; `RuleEngine.Decide` on `KW-ARB-PLANNER-001` with
  `delegation.plan-file = docs/plans/2026-11-03-arbiter-one-command-install.md` (and `docs/specs/`,
  `docs/todo/` equivalents) answers `recognised`; a path outside the planning dirs and a missing
  plan-file answer `malformed`; `KW-ARB-READY-001` answers `ready` for an empty dependency list with
  no completed tasks and `waiting` for a non-empty unsatisfied list; the shipped-rules catalogue
  stays green.

### T2 — `HostConfigYaml.WithArbiterEnabled` (Core, non-destructive merge)

- **Objective.** Add the line-based `arbiter.enabled: true` editor; no behaviour beyond the merge.
- **Files.** `src/KyberWeave.Core/Docs/Scaffolding/HostConfigYaml.cs`; tests `tests/KyberWeave.Tests/HostConfigYamlTests.cs`.
- **Skills.** RED `test-dev`; GREEN `csharp-dev`.
- **Acceptance.** Contract row T2 GREEN; `dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release` 0 warnings.

### T3 — Kilo plugin shim binds and verifies the repo root (Core)

- **Objective.** Fix the cwd-dependence at the source: `ArbiterPluginShim.Render` resolves and binds the repo root before spawning `kyber-weave-arbiter hook`, and blocks clearly when no root resolves.
- **Files.** `src/KyberWeave.Core/Squad/Rendering/ArbiterPluginShim.cs`; tests `tests/KyberWeave.Tests/ArbiterShimBehaviourTests.cs`, `tests/KyberWeave.Tests/ArbiterKiloRenderingTests.cs`. Note: the same shim serves OpenCode and Pi; the generated source changes once and all three keep parity.
- **Skills.** RED `test-dev`; GREEN `csharp-dev`.
- **Acceptance.** Contract row T3 GREEN; build 0 warnings; a non-repo-cwd Kilo session is gated with an explicit reason, never `KW-ARB-HOOK-001` from a wrong root.

### T4 — `kyber-weave arbiter install` (CLI)

- **Objective.** The one-command install: validate target, ensure config, non-destructive enable, render via the Squad pipeline, apply provider + key, verify, print trust step; idempotent; `--dry-run`, `--yes`; reject `--global`.
- **Files.** New `src/KyberWeave.Cli/Commands/Arbiter/ArbiterInstallCommand.cs`, `ArbiterInstallSettings.cs`; register `install` (and `uninstall`, T5) in `src/KyberWeave.Cli/Program.cs`. Reuse: `SquadCommandComposition`/`SquadLifecycleService.InstallAsync`, `SquadTargetCatalog`, `HostConfigYaml.WithArbiterEnabled` (T2), `DocsScaffolder` host-config creation, `ArbiterCommandComposition` (credential store, key resolve, `ArbiterProbeExecutor`), `ArbiterSetupCommand` provider/override rendering, `ArbiterStatusCommand.KeyMissing`, `SquadInstallCommand.WriteArbiterTrustSteps`.
- **Skills.** RED `test-dev`; GREEN `csharp-dev`.
- **Acceptance.** Contract row T4 GREEN; one-liner succeeds end to end on a fresh temp repo; second run is a no-op; invalid target and missing key fail with distinct codes (`2`/`KW-ARB-KEY-001`); the key appears in no log/config/commit; `--global` is refused.
- **Depends on.** T2 (merge), T3 (shim correctness is a prerequisite for the end-to-end acceptance, but file scopes are disjoint — declared dependency so T4's live acceptance can rely on a correct shim).

### T5 — `kyber-weave arbiter uninstall --target <t>` (CLI)

- **Objective.** Remove only Squad-owned files/blocks for the target; `--remove-key` opt-in key deletion.
- **Files.** New `src/KyberWeave.Cli/Commands/Arbiter/ArbiterUninstallCommand.cs`, `ArbiterUninstallSettings.cs`; registration in `src/KyberWeave.Cli/Program.cs`. Reuse `SquadLifecycleService.UninstallAsync`, `SquadReceipt`.
- **Skills.** RED `test-dev`; GREEN `csharp-dev`.
- **Acceptance.** Contract row T5 GREEN; uninstall reverses only owned artifacts; key untouched without `--remove-key`.

### T6 — `kyber-weave update` refreshes arbiter hooks/scripts (CLI Update)

- **Objective.** After replacing binaries and only when an arbiter is installed, re-render arbiter hooks/scripts for registered projects by reusing the squad update path, through a testable seam.
- **Files.** `src/KyberWeave.Cli/Update/SelfUpdater.cs`; add the refresh seam (injectable `Func`/interface) wired in `UpdateCommand.cs` as needed. Reuse `SquadLifecycleService.UpdateAsync` + `SquadStateStore.ListProjects()`.
- **Skills.** RED `test-dev`; GREEN `csharp-dev`.
- **Acceptance.** Contract row T6 GREEN; `--no-arbiter` and an absent arbiter skip the refresh.

### T7 — Amend ADR 0053 (docs-dev)

- **Objective.** Record in ADR 0053 in place: D18 argv-key exception (`--arbiter-key`, warned, safer paths retained) and D22 one-command install (replacing the three-part install). Bump `last-reviewed`; no new ADR row.
- **Files.** `docs/adr/0053-kyber-arbiter-three-step-decision-gates.md` (and its index row only if the title/date changes).
- **Skills.** `docs-dev`.
- **Acceptance.** ADR 0053 states the amended D18 and D22; `docs validate` clean.

### T8 — Docs closeout: harvest + docs validation (docs-dev)

- **Objective.** Update the four named docs and run the repository's two documentation checks.
- **Files.** `docs/cli-command-surface.md` (add the `arbiter` section: `install`, `uninstall`, and the existing verbs; update `update`), `docs/install.md` (`## Updating` + `## Then initialize your repository`), `docs/kyber-arbiter/runbook.md` (`## Setup, doctor, audit` and `## Hooks`), `docs/kyber-squad/onboarding.md` (`## Arbiter hooks`).
- **Skills.** `docs-dev`.
- **Acceptance.** `docs validate .` and `docs drift .` return 0 findings (Draft: `--merge-ready` is not run while this plan is in the active folder — `KW-DOC-LIFECYCLE-003`).
- **Depends on.** T7 (so validation sees the amended ADR); otherwise parallel with T2–T6.

## Dependency graph

```
T1  (leaf — gate fix)
T2 ──► T4 ──► (acceptance)
T3 ──► T4 (end-to-end acceptance only; file scopes disjoint)
T5  (leaf)
T6  (leaf)
T7 ──► T8 (validation sees the ADR)
T8  (closeout; last)
```

`review.max-concurrent-workers` (default 4) bounds ready-task concurrency: T1, T2, T3, T5, T6, T7 can
all proceed concurrently; T4 waits on T2 (and T3 for its end-to-end acceptance); T8 runs after T7.

## Risks

- **Secret-in-argv.** `--arbiter-key` is inherently visible in argv and shell history. Mitigated by the
  printed warning, the retained `--key-stdin`/env path, the never-persist invariant, and the ADR 0053 D18
  amendment. Accepted (D2).
- **Non-destructive YAML edge cases.** The `HostConfigYaml` text editor must tolerate CRLF/lone-CR,
  a BOM, and a pre-existing `arbiter:` block. The existing `HostConfigLines`/`DetectNewline` machinery
  already covers these; T2's tests pin them.
- **Shim root resolution on every call.** Walking up the tree adds a stat per hook event. Bounded by the
  depth of the tree and by the existing hook latency budget; the resolved root is cached for the plugin's
  lifetime (same `directory`).
- **Update refresh re-renders owned files.** Reusing `SquadLifecycleService.UpdateAsync` means an edited
  owned file follows the existing `ReplaceManaged`/drift rules, so a user edit is not silently overwritten.

## Out of scope

- Reimplementing the renderers, the receipt model, the credential store, or the provider client — all reused.
- Live-probing harness hook behaviour (vendor documentation remains the support claim, ADR 0053 D7).
- Changing any `KW-ARB-*` rule id or renumbering; no new rule id is added by this work.
- Phase-by-phase harness rollout; all hooked targets already render, and `arbiter install` reuses that.

## Verification gates

1. **Build.** `dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release` (0 warnings; `TreatWarningsAsErrors` on).
2. **Targeted tests.** Per-task filtered commands in the Test contract, each run through `scripts/proc-guard.sh run --`.
3. **Full suite.** CI (`test` gate) runs the whole suite; not a local pre-closeout requirement.
4. **Docs.** `docs validate .` and `docs drift .` (0 findings). `--merge-ready` is for closeout after archive.
5. **Review.** `code-review` council on the draft PR (`feat/arbiter-one-command-install`, base `origin/main` 50691a5b).
6. **Live acceptance.** Fresh temp repo one-liner end to end; second run no-op; invalid target/missing key
   distinct codes; key absent from log/config/commit; uninstall reverses only owned artifacts; Kilo
   non-repo-cwd dispatch gated instead of failing closed.

## Targeted test commands (pre-computed)

```bash
# Build once (fast-loop builds first; direct runner otherwise)
scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release

# T1
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterPlannerRuleTests|FullyQualifiedName~ArbiterFactBuilderTests"
# T2
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~HostConfigYamlTests"
# T3
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterShimBehaviourTests|FullyQualifiedName~ArbiterKiloRenderingTests"
# T4
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterInstallCommandTests"
# T5
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterUninstallCommandTests"
# T6
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~UpdateCommandTests|FullyQualifiedName~SelfUpdaterArbiterRefreshTests"
```

## Docs validation commands (pre-computed)

```bash
# Draft: the active plan folder forbids --merge-ready (KW-DOC-LIFECYCLE-003), so validate plain.
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate .
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .
```

## Closeout

`docs-dev` (T8) runs the harvest and the two documentation checks, then the conductor archives the plan
(`git mv` to `docs/archive/plans/`), moves the index row to **Archived Plans**, and re-runs
`docs validate . --merge-ready` + `docs drift .` — at which point this plan's `## Decisions` table is
preserved intact and the `Design harvest` rows above become the mapping to canonical docs. ADR work is T7.
