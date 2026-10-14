---
id: plans/2026-10-14-squad-home-state
title: "Relocate Kyber-Squad lock and receipt state to ~/.kyber-weave/squad"
doc-type: plan
status: complete
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-14
development-mode: test-first
---

# Relocate Kyber-Squad lock and receipt state to ~/.kyber-weave/squad

**Status: Complete 2026-10-14.** Executed through T10 on branch `feat/squad-home-state`; see
[section 10](#10-review-and-closeout) for the closeout. Originally **Ready 2026-10-14**: the user
approved execution in advance, and the conductor relayed that approval on 2026-10-14. Branch:
`feat/squad-home-state`, cut from `main`.

**Development mode:** `test-first`. Each behaviour scope has a RED task that writes
failing contract tests. The GREEN task that follows makes those tests pass without
weakening them.

---

## 1. Problem and goal

### 1.1 Problem

`kyber-weave squad update --global` depends on the current directory. Global receipts are
keyed by `sha256(physical cwd)` under
`<ApplicationData>/KyberWeave/squad/roots/<key>/`
(`SquadStateStore.ResolveStateFile`, `SquadStateStore.GlobalRootBinding`,
`SquadPhysicalRootIdentity`). Running the command from `~` prints "No Kyber-Squad
deployment found". Project installs keep `.kyber-weave/squad.lock.yml` and
`.kyber-weave/squad.receipt.json` inside each repository, so no single place lists every
project deployment on the machine.

### 1.2 Goal

- All Squad lock and receipt state moves to `~/.kyber-weave/squad/`:
  - `global/{squad.lock.yml,squad.receipt.json}`
  - `projects/<root-key>/{squad.lock.yml,squad.receipt.json}`
  - `registry.json`, mapping each root key to its physical path, last update time, and targets.
- Global scope ignores the current directory entirely.
- After a successful interactive global update, a picker offers the registered projects,
  with explicit **All** and **None** options. Non-interactive runs use
  `--projects <all|none|path...>`.
- `--global -t all --replace-managed` overwrites receipt-owned files even when they were
  edited locally. It never touches unowned hand-authored files.
- Migration runs automatically. It is non-destructive and idempotent, and covers:
  - legacy application-data global receipts;
  - in-repo project state, imported the next time a command touches that project.
- The contention mutex and the transaction work directory stay per physical root. Only
  the receipt and lock move.
- Tests never touch the real home directory or real harness directories.

---

## 2. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Where does state live, and how do tests override it? | `ISquadUserPaths` gains `HomeDirectory`, which is `Environment.SpecialFolder.UserProfile` in production. The state root is `<HomeDirectory>/.kyber-weave/squad/`. Global state goes in `global/`, project state in `projects/<SquadPhysicalRootIdentity.Key>/`, and the index in `registry.json`. `ApplicationDataDirectory` is kept for two uses only: as the legacy migration source, and as the existing render `UserScopeDirectory` (`SquadLifecycleService` lines ~146 and ~296), which does not change. | Spec: "override via ISquadUserPaths". The root key reuses the existing physical-identity hash, so lexical and symlink aliases still converge. | ANSWERED. Approved spec, user pre-approval relayed 2026-10-14 |
| D2 | Global scope used the cwd as its anchor for the mutex and work directory. What replaces it? | The global anchor root is `HomeDirectory` and nothing else. Commands ignore the path and cwd under `--global`. Mutex key: `hash(physical home)`. Global work directory and journal: `<home>/.kyber-weave/.squad-transaction`. Project scope is unchanged: mutex, journal, and work directory stay under `<root>/.kyber-weave/.squad-transaction`. `ResolveTransactionDirectory` is decoupled from `ResolveStateFile`. | Spec: "Contention mutex and transaction work dir stay per physical root; only receipt/lock move". Without a fixed anchor, two global runs from different directories would take different mutexes but write the same global receipt. A project install at `~` shares the global mutex, which is consistent with the existing scope-independent contention rule. | ANSWERED. Derived from the approved spec, 2026-10-14 |
| D3 | How does a receipt record its physical root? | Receipts written by this version store the canonical absolute physical root in the existing `targetRoot` field. Global receipts store the home anchor, or the resolved binding root for a migrated legacy single-root receipt. Reads still accept the legacy `"."`. Schema versions do not change: v1 for project, v2 for global. Legacy single-root handling uses `receipt.TargetRoot` instead of the cwd. | Spec: "Receipt records physical root (today targetRoot '.')". `SquadDeploymentPlan.NewReceipt` hard-codes `"."` today. This amends ADR 0024's consequence that project receipts are byte-identical forever. ADR 0037 records the amendment. | ANSWERED. Approved spec, 2026-10-14 |
| D4 | Registry contract | `registry.json` uses schema `kyber-squad.registry/v1` and is keyed by root key. Each entry holds `path`, `lastUpdatedUtc`, and `targets`. A successful project install, update, or import upserts the entry. A successful project uninstall removes it. Writes are atomic (temp file, then rename). `ListProjects()` returns each entry with a `Stale` flag and a reason: path missing, physical key mismatch, or receipt missing. It also rebuilds entries that are missing from the registry but have a receipt under `projects/<key>/`. If the registry write fails after a committed transaction, the command warns and names the fix (rerun `squad status`) but does not fail the transaction. Global state is not registered. | Spec: written on install, removed on uninstall, stale entries flagged. Rebuilding entries makes the registry recoverable, because it is an index and not the authority. | ANSWERED. Approved spec, 2026-10-14 |
| D5 | What happens to sibling global receipts (`ListOtherGlobalReceipts`)? | Retired. There is one global receipt. Sibling-ownership checks in `CreateInstall`, `CreateUpdate`, and `CollectUnmanagedCollisions` receive an empty list, and the method is removed. Legacy roots that were not migrated are reported by `doctor` instead. | A per-cwd binding no longer exists, so a sibling global deployment cannot exist either. | ANSWERED. Consequence of D2, 2026-10-14 |
| D6 | Legacy global migration rules | Ignore empty `roots/*` directories. If exactly one non-empty legacy receipt exists and no home global receipt exists, copy its receipt and lock to `global/`, set `targetRoot` per D3, and leave the legacy files untouched. A `per-target-roots` receipt needs no root. A `single-root` receipt needs its binding root, resolved from (1) an absolute `targetRoot`, (2) the registry, or (3) a key match against `HomeDirectory` or the invoking cwd. If none resolves, it is not migrated and is reported. If more than one non-empty legacy receipt exists, none is migrated automatically. The run reports each one with guidance, fail-closed and without guessing. If a home global receipt exists, migration is a no-op (idempotent). | The live receipt on the dev machine is a single `per-target-roots` v2 receipt (`roots/3f72382c…/squad.receipt.json`, bound to `/Users/dave/git/personal/kyber-weave`), so it migrates without needing a root. The previous sibling-collision rule guaranteed that multiple legacy receipts own disjoint files, but merging their locks would be a guess. | ANSWERED. Derived from the approved spec, non-destructive and idempotent, 2026-10-14 |
| D7 | Project import rules | When a project-scope verb or a picker-driven update touches root R, `projects/<key(R)>/` has no receipt, and `R/.kyber-weave/squad.receipt.json` exists: import the receipt and lock, set `targetRoot`, and register R. The in-repo files stay untouched. From then on the home state wins and the in-repo state is ignored. `doctor` warns that legacy in-repo state was retained and should be removed from version control. | Spec: "import in-repo .kyber-weave/squad.* for a project when next touched". | ANSWERED. Approved spec, 2026-10-14 |
| D8 | When does migration run? | One shared helper, `SquadCommandComposition.EnsureStateMigrated`, backed by Core `SquadStateMigration`. Every squad verb (install, update, status, uninstall, doctor) calls it before reading state. Under `--dry-run` it writes nothing: when only legacy state exists, the dry run reports "legacy state at <path> will be migrated on the next non-dry-run invocation" and reads no deployment. | Keeps the dry-run promise of no writes. Status and doctor migrate because the copy is non-destructive and idempotent ("auto-migrate on first run"). | ANSWERED. Derived from the approved spec, 2026-10-14 |
| D9 | Picker semantics | The picker runs only after a successful global update, only on an interactive console, only when `--projects` is absent, and only when at least one project is registered. It is a Spectre `MultiSelectionPrompt` listing `All`, `None`, and then each project as `path`, with `(stale: <reason>)` when stale. `All` selects every non-stale project. `None` or an empty selection selects nothing. `None` combined with any other choice fails validation and re-prompts. Selecting a stale project skips it with a warning. The picker sits behind an internal `ISquadProjectPicker` seam (production: `SpectreSquadProjectPicker`) so tests can drive it. If the global update fails, no picker is shown and the exit code is 1. | Spec: explicit All and None, individual projects, stale entries flagged. The seam lets tests run without a TTY. | ANSWERED. Approved spec, 2026-10-14 |
| D10 | `--projects` contract | Applies to `squad update` only, and only with `--global`. Without `--global` it is an exit-2 client-input error. Accepts `all`, `none`, or one or more paths (repeatable). When given, it never prompts. A non-interactive run without it behaves as `none` and does not prompt. A path that is neither registered nor holds importable in-repo state is rejected with exit 2 before any global work, and the message offers the nearest registered path. A stale named path is warned about and skipped. | Spec: "Non-interactive: no prompt; add --projects <all\|none\|path...>". The house style requires diagnostics to carry a hint. | ANSWERED. Approved spec, 2026-10-14 |
| D11 | How are the selected projects updated? | Through the existing project update path, extracted from `SquadUpdateCommand.Execute` into one internal method. Each project uses its own config and receipt targets and exclusions. The global run passes on `--version` and `--dry-run`, but not `-t`, `-x`, or `--replace-managed`. The per-project confirmation is suppressed because the selection is the consent. Projects are updated one after another. A failure does not stop the rest. A summary is printed at the end, and the exit code is 1 if any project failed. | Spec: "reuse existing per-project update path". Conservative: global target and overwrite flags must not silently widen or force project deployments. To force-overwrite a project, run `update --replace-managed` in that project directly. | ANSWERED. Derived from the approved spec, 2026-10-14 |
| D12 | `--global -t all --replace-managed` | No planner change is needed. `CreateUpdate` already overwrites edited owned files when `replaceManaged` is true. A new path that collides with an unowned file still fails the whole run with `UnmanagedCollision` and makes no writes. The hint names `install --adopt` or moving the file aside. Tests pin both halves at global scope from an arbitrary cwd. | Spec: "never touches unowned hand-authored files (install --adopt remains)". | ANSWERED. Approved spec, 2026-10-14 |
| D13 | `--global` together with an explicit path or `--path` | Print a warning that the path is ignored under `--global`, then continue. | Keeps existing scripts working, which an exit-2 refusal would break. | ANSWERED. Derived from D2, 2026-10-14 |
| D14 | Changes to status, doctor, and uninstall | `status --global` reads the single global receipt and also lists registered projects with stale flags. `doctor` (both scopes) reports stale registry entries, unmigrated legacy global roots, and retained in-repo legacy state. `uninstall --global` touches the global deployment only: no picker, and no project changes. | Spec lists these commands. | ANSWERED. Approved spec, 2026-10-14 |
| D15 | Isolation from the real home directory | Every `ISquadUserPaths` fake returns a temporary `HomeDirectory`. Existing fakes derive it as `<appData>/home`. The update, install, status, uninstall, and doctor commands gain internal injection for `ISquadGlobalRootResolver` and `ISquadProjectPicker`, and the confirmation-root display uses the injected resolver. A guard test asserts that every fake home resolves under `Path.GetTempPath()`. No environment-variable override is added. | Spec: "Tests must NEVER touch the real home or real harness dirs". Today `SquadUpdateCommand` builds `ResolveGlobalRoots()` from the real environment and `UserProfile`. | ANSWERED. Approved spec, 2026-10-14 |
| D16 | Version control and the repository's own tracked state | The docs state that the lock and receipt are no longer committed. This repository's tracked `.kyber-weave/squad.lock.yml` and `.kyber-weave/squad.receipt.json` self-deployment stay untouched, and `requirements.md` describes them as legacy in-repo state. | Non-destructive. Deleting tracked files is a separate decision. | ANSWERED. Approved spec, 2026-10-14 |
| D17 | ADR | New ADR 0037 records the relocation, the registry, the global anchor, the migration rules, and the amendment to ADR 0024's project-receipt consequence. ADR 0024 gets an in-place "amended by 0037" note. | Hard to reverse: on-disk layout and version-control policy. Spec: "an ADR for the relocation". | ANSWERED. Approved spec, 2026-10-14 |
| D18 | Development mode | `test-first`. | Default, and the spec requires red tests first. | ANSWERED. 2026-10-14 |
| AP1 | Approve and execute | Approved. | The user approved execution in advance, and the conductor relayed it. | APPROVED. User pre-approval relayed by conductor, 2026-10-14 |

---

## 3. Investigation findings

- `SquadStateStore.ResolveStateFile` (~line 402): project state resolves to
  `<physical root>/.kyber-weave/<file>`; global state resolves to
  `<AppData>/KyberWeave/squad/roots/<GlobalRootBinding(targetRoot)>/<file>`.
  `ResolveTransactionDirectory` reuses `ResolveStateFile`, so the global journal currently
  sits under app data while the global work directory sits under
  `<cwd>/.kyber-weave/.squad-transaction` (`ResolveTransactionWorkDirectory`).
  `ResolveStateAuthorityRoot` (global) is the parent of app data.
- `SquadTransaction.AcquireLease(plan.PhysicalRootKey)` names the mutex
  `kyber-weave-squad-<key>`. `PhysicalRootKey` comes from the plan's `targetRoot`, which
  is the cwd for global scope today.
- `SquadDeploymentPlan.NewReceipt` always writes `targetRoot: "."`.
  `IsLegacySingleRootReceipt` drives single-root handling against the caller's
  `targetRoot`. `CreateUpdate` with `replaceManaged: true` already overwrites locally
  edited owned files, and still throws `UnmanagedCollision` for unowned existing files.
- `ListOtherGlobalReceipts` has two callers: `SquadLifecycleService.SiblingGlobalReceipts`
  (~426) and `SquadDoctorCommand.ReportGlobalCollisions` (~307).
- CLI composition: `SquadCommandComposition.ResolveStateStore`, `ResolveGlobalRoots` (real
  environment and `UserProfile`), `ResolveGlobalTargetRoots`,
  `ResolveUninstallGlobalTargetRoots`, `CreateLifecycleService`. `SquadUpdateCommand`
  reads the receipt at `ResolveTargetRoot(path)` and prints "No Kyber-Squad deployment
  found" when it is missing.
- `ISquadUserPaths` (`SquadDeploymentModels.cs:108`) has one member,
  `ApplicationDataDirectory`. It has six implementations:
  - production `SquadUserPaths`;
  - test fakes in `Fakes/FakeSquadUserPaths.cs`, `SquadDeploymentStateTests.cs:6152`,
    `SquadCliCommandTests.cs:1856`, `SquadHotshotLifecycleCopilotGlobalLinksTests.cs:150`,
    and `SquadHotshotLifecycleCopilotGlobalUpdateLinksTests.cs:118`.
- Live machine state, read-only: exactly one legacy root,
  `~/Library/Application Support/KyberWeave/squad/roots/3f72382c…e0e9/`. It holds a v2
  global `per-target-roots` receipt with `targetRoot "."`, installed
  2026-10-14T00:48:13Z, plus its lock.
- Docs that describe the old locations: `docs/kyber-squad/onboarding.md` (Deployment
  Scopes, where project lock and receipt "should be committed"),
  `docs/kyber-squad/architecture.md` §4, `docs/kyber-squad/README.md` (Transactional
  Engine row), `docs/kyber-squad/requirements.md` (self-deployment note), and ADR 0024.
- Test runner: xUnit on Microsoft.Testing.Platform. Run a single class with
  `scripts/fast-loop.sh --class <FQN>`. Squad classes are outside the fast tier, so the
  fast-tier class list must not be edited.
- Discovery basis: CodeGraph and `docs_explore` ran against the main checkout
  (rev `6155591`, not `main`'s `803cc3e`). Line numbers are approximate. Re-resolve them
  with CodeGraph on the branch.

---

## 4. Tasks

Required skills are named for each task. Owner mapping: `csharp-dev` → csharp-dev,
`test-dev` → test-dev, `app-docs-standard` and `architecture-decision-record` → docs-dev.

### T1: API seam (no behaviour)

- **Skill:** `csharp-dev`
- **Objective:** Add the compile-time surface so the RED tests compile and fail on
  behaviour, not on missing symbols. The production paths do not change behaviour yet.
- **Files:**
  - `src/KyberWeave.Core/Squad/Deployment/SquadDeploymentModels.cs`: add
    `ISquadUserPaths.HomeDirectory`; add records `SquadProjectRegistryEntry(RootKey, Path,
    LastUpdatedUtc, Targets, Stale, StaleReason)` and `SquadMigrationReport`.
  - `src/KyberWeave.Core/Squad/Deployment/SquadStateStore.cs`: add `ListProjects()`,
    `RegisterProject(...)`, `UnregisterProject(...)`, and `ResolveHomeStateDirectory()`.
    Each throws `NotImplementedException`.
  - New `src/KyberWeave.Core/Squad/Deployment/SquadStateMigration.cs`: stub with
    `MigrateGlobal(string? invokingCwd)` and `ImportProject(string root)`.
  - `src/KyberWeave.Cli/Commands/Squad/SquadUserPaths.cs`: `HomeDirectory` returns
    `UserProfile`.
  - `src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs`: `SquadUpdateSettings.Projects`
    (`string[]`, `--projects`).
  - New `src/KyberWeave.Cli/Commands/Squad/Infrastructure/ISquadProjectPicker.cs`.
  - Internal constructor parameters (`ISquadGlobalRootResolver? globalRoots`,
    `ISquadProjectPicker? picker`) on `SquadUpdateCommand`, `SquadInstallCommand`,
    `SquadStatusCommand`, `SquadUninstallCommand`, and `SquadDoctorCommand`. They are
    stored but not yet used.
  - The five test fakes listed in §3: one-line `HomeDirectory => Path.Combine(appData,
    "home")`.
- **Acceptance:** `dotnet build KyberWeave.sln -c Release` passes, and the full existing
  suite still passes unchanged.
- **Depends on:** none.

### T2: RED, home state store, registry, and transaction anchors

- **Skill:** `test-dev`
- **Files:**
  - New `tests/KyberWeave.Tests/SquadHomeStateStoreTests.cs`.
  - New `tests/KyberWeave.Tests/SquadProjectRegistryTests.cs`.
  - Update old-location assertions in `tests/KyberWeave.Tests/SquadDeploymentStateTests.cs`,
    for example `ResolveStateGlobalScopeUsesInjectedUserPathAndClock`, the
    `ResolveGlobalState*Binding` tests, the sibling-receipt tests, and the
    `InsertDuplicateReceiptFile` helper. Supersede them to the D1–D5 contract; do not
    delete coverage.
- **Acceptance:** Contract rows TC-T2 in §5 fail RED against T1.
- **Depends on:** T1.

### T3: RED, migration

- **Skill:** `test-dev`
- **Files:** New `tests/KyberWeave.Tests/SquadStateMigrationTests.cs`.
- **Acceptance:** Rows TC-T3 fail RED.
- **Depends on:** T1.

### T4: RED, CLI behaviour

- **Skill:** `test-dev`
- **Objective:** Cover global commands run from an arbitrary cwd, the picker, `--projects`,
  `--replace-managed`, status, doctor, and uninstall.
- **Files:**
  - New `tests/KyberWeave.Tests/SquadGlobalHomeCliTests.cs`.
  - Update old-location assertions in `tests/KyberWeave.Tests/SquadCliCommandTests.cs`,
    for example tests that read `.kyber-weave/squad.receipt.json` inside the project or
    `roots/<key>`.
- **Acceptance:** Rows TC-T4 fail RED.
- **Depends on:** T1.

### T5: GREEN, Core state relocation, registry, and lifecycle wiring

- **Skill:** `csharp-dev`
- **Files:**
  - `SquadStateStore.cs`: implement D1–D5. This covers `ResolveStateFile`,
    `ResolveTransactionDirectory`/`WorkDirectory`, `ResolveStateAuthorityRoot` (state
    directories under home are captured for rollback), the registry, and removal of
    `ListOtherGlobalReceipts` and `GlobalRootBinding`.
  - `SquadDeploymentPlan.cs`: `NewReceipt` records the physical root; single-root
    handling uses `receipt.TargetRoot`.
  - `SquadLifecycleService.cs`: register on project install/update success, unregister on
    uninstall success; `SiblingGlobalReceipts` is removed.
  - `SquadTransaction.cs`: only if needed to capture and roll back the home state
    directories.
  - `SquadDeploymentModels.cs`.
  - Keep `SquadDoctorCommand.ReportGlobalCollisions` compiling, passing no siblings.
- **Acceptance:** TC-T2 is GREEN. The full suite is green apart from TC-T3 and TC-T4.
- **Depends on:** T2.

### T6: GREEN, migration

- **Skill:** `csharp-dev`
- **Files:** `SquadStateMigration.cs` (implements D6–D8). Uses `SquadStateStore` from T5.
- **Acceptance:** TC-T3 is GREEN.
- **Depends on:** T3 and T5.

### T7: GREEN, CLI

- **Skill:** `csharp-dev`
- **Files:**
  - `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`:
    `EnsureStateMigrated`, `ResolveGlobalAnchorRoot`, `ParseProjectSelection`,
    `RunProjectPicker`, and an injectable `ResolveGlobalTargetRoots` resolver.
  - New `Infrastructure/SpectreSquadProjectPicker.cs`.
  - `SquadUpdateCommand.cs`: extract the per-project update path; post-run picker and
    `--projects`; D11 summary and exit code.
  - `SquadInstallCommand.cs`, `SquadStatusCommand.cs`, `SquadUninstallCommand.cs`,
    `SquadDoctorCommand.cs`: implement D2, D8, D13, and D14.
  - `SquadSettings.cs`: `--projects` description.
- **Acceptance:** TC-T4 is GREEN, and so is the whole suite.
- **Depends on:** T4 and T6.

### T8: Docs and ADR

- **Skills:** `app-docs-standard`, `architecture-decision-record`
- **Files:**
  - `docs/kyber-squad/onboarding.md`: Deployment Scopes, the new state location, the lock
    no longer being committed, `--projects`, the picker, migration, and `doctor`
    findings.
  - `docs/kyber-squad/architecture.md` §4: the state layout, the global anchor, the
    per-root mutex and work directory, the registry, and migration.
  - `docs/kyber-squad/README.md`: Transactional Engine row.
  - `docs/kyber-squad/requirements.md`: self-deployment note per D16.
  - New `docs/adr/0037-squad-state-in-user-home.md`.
  - `docs/adr/README.md`: index row.
  - `docs/adr/0024-squad-global-receipt-layout-marker.md`: "amended by 0037" note.
  - Add a `code-refs` entry for `SquadStateMigration` where the governing doc claims it.
- **Acceptance:**
  - `docs validate . --merge-ready` and `docs drift .` both report zero findings.
  - The docs match D1–D17 and the shipped flag names.
  - No mention of committing the lock remains.
- **Depends on:** none. It may start at once from the decisions. T10 reconciles it
  against the shipped code.

### T9: Verification gates, including Linux

- **Skill:** `csharp-dev`
- **Objective:** Run the full AGENTS.md gate list (§7) on macOS, then the Linux
  verification (§7).
- **Acceptance:** Every gate passes. Linux build and tests are green. The Linux smoke
  check shows the same `status --global` result from two different cwds under a temporary
  `HOME`.
- **Depends on:** T7 and T8.

### T10: Closeout

- **Skill:** `app-docs-standard`
- **Objective:**
  - Reconcile the docs against the shipped code.
  - Map each decision to its canonical destination (D1–D17 → ADR 0037 and the
    kyber-squad docs).
  - Archive this plan to `docs/archive/plans/` and move its index row to Archived.
  - Rerun `docs validate --merge-ready` and `docs drift`.
- **Depends on:** T9 and the final code review.

---

## 5. Test contract

Run each class with:
`dotnet build KyberWeave.sln -c Release && scripts/fast-loop.sh --class KyberWeave.Tests.<Class>`

If the test namespace differs, use the class's fully qualified name. Every test uses
`TempDirectory` and fake `ISquadUserPaths`, `ISquadGlobalRootResolver`, and
`ISquadProjectPicker` instances. No test reads or writes the real `HOME`, real app data,
or real harness directories.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | none (seam only) | `dotnet build KyberWeave.sln -c Release` then the full `dotnet test` | No behaviour change. **No-test task.** Verified by a green build and an unchanged green suite. | n/a | Build and existing suite green |
| TC-T2 / T5 | `SquadHomeStateStoreTests.cs` | `--class KyberWeave.Tests.SquadHomeStateStoreTests` | (a) Global lock and receipt paths are `<home>/.kyber-weave/squad/global/…` for any `targetRoot`, including two unrelated temporary directories. (b) Project paths are `<home>/.kyber-weave/squad/projects/<key>/…`, and lexical or symlink aliases share one key. (c) The project journal and work directory stay at `<root>/.kyber-weave/.squad-transaction`; global uses `<home>/.kyber-weave/.squad-transaction`. (d) Mutex contention is keyed by physical root, and global contention is keyed by home. (e) A written receipt's `targetRoot` is the absolute physical root; a legacy `"."` still reads. (f) A failed transaction leaves no new directories under the home store. | Tests fail on old paths or on `NotImplementedException` | Same tests pass unmodified |
| TC-T2 / T5 | `SquadProjectRegistryTests.cs` | `--class KyberWeave.Tests.SquadProjectRegistryTests` | (a) Project install registers path, `lastUpdatedUtc`, and targets. (b) Update refreshes `lastUpdatedUtc`. (c) Uninstall removes the entry. (d) A missing path, a key mismatch, or a missing receipt is flagged stale with a reason. (e) A registry with a missing entry is rebuilt from `projects/<key>`. (f) Writes are atomic: no partial file after a simulated failure. (g) Global install does not register. | `NotImplementedException` or wrong state | Pass |
| TC-T2 / T5 | `SquadDeploymentStateTests.cs` (updated cases) | `--class KyberWeave.Tests.SquadDeploymentStateTests` | Superseded old-location and sibling assertions now pin the D1–D5 contract. | Updated cases fail against T1 | Pass |
| TC-T3 / T6 | `SquadStateMigrationTests.cs` | `--class KyberWeave.Tests.SquadStateMigrationTests` | (a) One legacy `per-target-roots` receipt and lock under `<appData>/KyberWeave/squad/roots/<key>/` are copied to `global/` with the D3 `targetRoot`, and the legacy files are byte-identical afterwards. (b) A second run is a no-op (idempotent). (c) Empty `roots/*` directories are ignored. (d) Two or more non-empty legacy receipts: none is migrated, and the report lists each. (e) A legacy `single-root` receipt migrates when its key matches the home or the cwd, and otherwise stays unmigrated and reported. (f) An existing home global receipt is never overwritten. (g) In-repo `.kyber-weave/squad.*` is imported into `projects/<key>`, registered, and left byte-identical. (h) A home project receipt wins over in-repo state. (i) Unrelated files under `.kyber-weave/` and app data are untouched. (j) `--dry-run` writes nothing. | `NotImplementedException` | Pass |
| TC-T4 / T7 | `SquadGlobalHomeCliTests.cs` | `--class KyberWeave.Tests.SquadGlobalHomeCliTests` | (a) `update --global` run with the path set to two unrelated temporary directories finds the same global receipt and exits 0; it does not print "No Kyber-Squad deployment found". (b) A path under `--global` prints the ignored-path warning. (c) Picker **All** updates every non-stale registered project through the project path and leaves stale ones untouched. (d) **None** updates no project. (e) Selecting one specific project updates only that project. (f) **None** combined with another choice is rejected and re-prompted. (g) A non-interactive run without `--projects` does not invoke the picker and updates no project. (h) `--projects all`, `--projects none`, and `--projects <path>` behave as D10, with no prompt even when interactive. (i) An unknown `--projects` path exits 2 with a nearest-match hint and makes no writes. (j) `--projects` without `--global` exits 2. (k) A failed global update shows no picker and exits 1. (l) A failed project update leaves the others updated, prints a summary, and exits 1. (m) `--global -t all --replace-managed` overwrites a locally edited receipt-owned file. (n) The same run fails with `UnmanagedCollision` and makes no writes when a new path holds an unowned hand-authored file, which stays byte-identical. (o) `status --global` lists registered projects with stale flags. (p) `doctor` reports stale entries, unmigrated legacy roots, and retained in-repo state. (q) `uninstall --global` leaves project state and the registry untouched. (r) Guard: every fake home is under `Path.GetTempPath()`. | Tests fail: receipt not found from another cwd, picker never invoked, or flag unknown | Pass |
| TC-T4 / T7 | `SquadCliCommandTests.cs` (updated cases) | `--class KyberWeave.Tests.SquadCliCommandTests` | Old-location assertions now pin the home store. | Updated cases fail against T1 | Pass |
| T8, T10 | docs | `docs validate . --merge-ready` and `docs drift .` | **No-test task.** Verified by zero findings on both checks and by review. | n/a | Zero findings |
| T9 | full suite | §7 | **No-test task.** Gate execution only. | n/a | All gates pass |

A test-dev task's RED evidence is the failing runner output, captured before any GREEN
edit. GREEN evidence is the same command passing. Weakening any assertion is a scope
change, and the plan returns to Draft.

---

## 6. Dependency graph

```text
T1 ─┬─> T2 ──> T5 ──┬─> T6 ──> T7 ──┐
    ├─> T3 ─────────┘         ↑     ├─> T9 ──> T10
    └─> T4 ───────────────────┘     │
T8 (no deps) ───────────────────────┘
```

T2, T3, T4, and T8 run in parallel. T6 depends on both T3 and T5. T7 depends on both T4
and T6.

---

## 7. Verification gates

Run from the repository root on the branch (AGENTS.md):

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-weave-docs
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill lint .apm/skills/kyber-weave-docs --min-desc-score 70
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill scan .apm/skills/kyber-weave-docs --fail-on critical
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json
```

This change touches the Squad state path, so `./scripts/update-loop.sh` also runs. It
belongs to the Squad release path ("Touching … the Squad release path").

**Linux verification:** run the following, or the equivalent on the owner's Linux host.

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet build KyberWeave.sln -c Release &&
  dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build &&
  export HOME=$(mktemp -d) &&
  CLI="dotnet src/KyberWeave.Cli/bin/Release/net10.0/kyber-weave.dll" &&
  (cd /tmp && $CLI squad status --global) > /tmp/a.txt;
  (cd "$HOME" && $CLI squad status --global) > /tmp/b.txt;
  diff /tmp/a.txt /tmp/b.txt'
```

The two outputs must be identical, and the run must not write `$HOME/.config/KyberWeave`.
Confirm the CLI dll name and path at execution time.

**Live dev-machine check (owner-run, after merge-ready):** from `~`, run
`kyber-weave squad update --global --dry-run`. It should report that legacy state will
be migrated. Then run `kyber-weave squad status --global`. It should migrate the
`roots/3f72382c…` receipt to `~/.kyber-weave/squad/global/` and report the deployment.
The legacy directory must stay intact.

---

## 8. Risks

- **R1:** Moving state-directory creation out of the physical root can break rollback
  (`CaptureMissingDirectories` and the authority root). Mitigation: TC-T2(f).
- **R2:** Every existing test that asserts the old locations must be superseded, not
  deleted. Mitigation: the T2 and T4 scopes include the updated cases. The task reviewer
  diffs test counts.
- **R3:** Global commands previously used the real global root resolver. A test that
  misses the injection could write to real harness directories. Mitigation: D15
  injection plus guard TC-T4(r). Reviewers grep the new tests for `ResolveGlobalRoots()`
  and `UserProfile`.
- **R4:** Older CLIs keep reading legacy state (app data or in-repo) after migration.
  Accepted. The docs say to upgrade every machine, and legacy files are kept for
  rollback.
- **R5:** `SquadDeploymentStateTests` contains a known flaky lease test with a 5-second
  wait (`docs/todo/squad-lease-contention-cross-process.md`). Do not count a flake from it
  as RED or GREEN evidence for this plan.
- **R6:** Spectre `MultiSelectionPrompt` cannot be driven in unit tests. Mitigation: the
  `ISquadProjectPicker` seam. The Spectre implementation stays thin, with validation in a
  pure function that is tested directly.

## 9. Out of scope

- Deleting legacy app-data roots or in-repo `.kyber-weave/squad.*` files, including this
  repository's tracked self-deployment.
- Merging multiple legacy global receipts.
- A project picker for uninstall, status, or doctor.
- An environment-variable override for the state root.
- Any change to rendering, the release source, or `kyber-weave.yml` config location.
- KyberDash.

## 10. Review and closeout

- Each task gets a `task-reviewer` audit.
- The whole change gets a `code-reviewer` council before the PR.
- The `docs-dev` closeout (T10) maps D1–D17 into ADR 0037 and `docs/kyber-squad/*`, then
  archives this plan per KW-DOC-LIFECYCLE-003.

### Closeout

**Outcome.** T1–T9 were delivered in development mode `test-first`, and T10 archived this plan. The
home-state store, registry, global anchor, migration with its ledger, the `--projects` contract, and the
picker all exist on the branch. No decision was left open. ADR 0037 is Accepted.

**Durable decisions and where they now live.**

| Decision | Destination |
|---|---|
| D1, D2, D3 (home state root, global anchor, physical root in `targetRoot`) | [ADR 0037](../../adr/0037-squad-state-in-user-home.md) decisions 1–3; [kyber-squad/architecture.md](../../kyber-squad/architecture.md) "State Location and Layout"; [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md) "Deployment Scopes" |
| D4 (registry contract, stale flags, reconciliation) | ADR 0037 decision 4; [kyber-squad/architecture.md](../../kyber-squad/architecture.md) "Registry"; [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md) "Registered projects" |
| D5 (sibling global receipts retired), D13 (path under `--global` is named and ignored) | ADR 0037 decisions 2 and 5; [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md) "2. Global Scope (`--global`)" |
| D6, D7, D8 (migration rules, import, timing under `--dry-run`) | ADR 0037 decisions 6–7; [kyber-squad/architecture.md](../../kyber-squad/architecture.md) "Migration from earlier layouts"; [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md) "Migration from earlier layouts" |
| Migration ledger (one-shot per source; not in the D-list, added during review) | ADR 0037 decision 9; [kyber-squad/architecture.md](../../kyber-squad/architecture.md) "Migration ledger" |
| D9, D10, D11, D12 (picker, `--projects`, per-project update, `--replace-managed` at global scope) | [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md) "Updating global and registered projects" and "Replacing managed files in global scope" |
| D14 (status, doctor, and uninstall at global scope) | [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md) "Doctor findings" |
| D15 (tests never touch the real home) | Documentation waiver: test-fixture scoped. The invariant is pinned by the guard test that asserts each fake home resolves under the temporary directory. |
| D16 (lock and receipt no longer committed; tracked legacy files untouched) | [kyber-squad/requirements.md](../../kyber-squad/requirements.md) (legacy in-repo state) and ADR 0037 decision 7 |
| D17 (ADR 0037, and ADR 0024 amended in place) | [ADR 0037](../../adr/0037-squad-state-in-user-home.md); [ADR 0024](../../adr/0024-squad-global-receipt-layout-marker.md) amendment note |
| D18, AP1 (development mode, execution approval) | Documentation waiver: process record with no surviving architectural invariant. |

**ADR waiver.** Not claimed. ADR 0037 is the decision record for this change.

**Known limit, stated in the docs.** A stale registry entry whose directory is gone stays flagged until the
path returns. No command clears it: uninstall requires an existing project directory, and the doctor hint says
so. The registry write is not locked across roots, so a concurrent update can lose one registry row; the
receipt under `projects/<root-key>/` is authoritative and `squad status --global` rebuilds the row.
