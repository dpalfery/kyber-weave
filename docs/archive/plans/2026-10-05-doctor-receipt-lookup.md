---
id: plans/2026-10-05-doctor-receipt-lookup
title: "Fix squad doctor receipt lookup for identity-colliding files (#283)"
doc-type: plan
status: complete
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-05
development-mode: test-first
---

# Fix squad doctor receipt lookup for identity-colliding files (#283)

**Status: Complete, archived 2026-10-05.** Approved via the autonomous "Hal approves" approve-and-execute gate on 2026-10-05. Tasks T1–T5 complete. Decisions D1–D4 recorded below; no new ADR. Archived per KW-DOC-LIFECYCLE-003.

This plan addresses GitHub issue [#283](https://github.com/dpalfery/kyber-weave/issues/283): `squad doctor --global` flags receipt-managed files as "unmanaged" right after a clean install.

**Development mode:** `test-first`. Every implementation task defines failing automated tests before modifying production code.

---

## 1. Problem and Scope

### 1.1 Context and Evidence

Per issue [#283](https://github.com/dpalfery/kyber-weave/issues/283):
- Following a clean install `kyber-weave squad install --global --target cursor` (0.1.7-rc.15, Linux, `~/.cursor` wiped first), `kyber-weave squad status --global` reports:
  `All deployed files match the recorded receipt (119 files).`
- However, running `kyber-weave squad doctor --global` prints 6 warnings for the deployed files:
  ```
  warn Unmanaged global file 'agents/conductor.md' collides with canonical identity 'conductor'.
  warn Unmanaged global file 'agents/conductor/references/execution-and-review.md' collides with canonical identity 'execution-and-review'.
  warn Unmanaged global file 'agents/csharp-dev.md' collides with canonical identity 'csharp-dev'.
  warn Unmanaged global file 'agents/github-devops.md' collides with canonical identity 'github-devops'.
  warn Unmanaged global file 'agents/test-dev.md' collides with canonical identity 'test-dev'.
  warn Unmanaged global file 'skills/code-review/SKILL.md' collides with canonical identity 'code-review'.
  ```
- All six files are recorded in the global receipt (`~/.config/KyberWeave/squad/roots/<hash>/squad.receipt.json`).
- In `SquadDoctorCommand.cs`, `ReportGlobalCollisions` currently calls `SquadDeploymentPlan.CollectUnmanagedCollisions` without passing or checking any receipt.
- `CollectUnmanagedCollisions` iterates through rendered files, checks `File.Exists(rendered.FullPath)`, and compares `File.ReadAllBytes(rendered.FullPath)` against the rendered content of the current working directory.
- Because `CollectUnmanagedCollisions` has no knowledge of the receipt, it treats any existing file whose bytes do not match the current render as an "unmanaged collision" — even if the file was placed there by a legitimate Squad install and is recorded in the receipt.
- Furthermore, doctor discarded its injected `ISquadUserPaths` parameter (`_ = userPaths;`), preventing it from reading the state store and receipts.

### 1.2 Expected Behavior

- `squad doctor --global` uses the same receipt lookup mechanism as `squad status --global` (`stateStore.ReadReceipt(workingDirectory, scope)` plus `stateStore.ListOtherGlobalReceipts(workingDirectory)`).
- When a file existing on disk is recorded in the deployment receipt (or any sibling global receipt) for that target and relative path (`DeployedFileIdentity(target, relativePath)`), it is a managed file and is not flagged as an unmanaged collision.
- A clean global install reports `Global unmanaged collisions: none` when diagnosed with `squad doctor --global`.

---

## 2. Approved Decisions and Decision Ledger

### Approved Decisions

| Id | Decision | Provenance |
|---|---|---|
| **D1** | Wire `ISquadUserPaths` and optional `SquadStateStore` into `SquadDoctorCommand` and resolve `SquadReceipt` for the diagnosed root in `ReportGlobalCollisions` using the same receipt lookup as `SquadStatusCommand`. | Issue #283 intake: doctor's unmanaged-file check must use the same receipt lookup as status. Approved under autonomous "Hal approves" gate 2026-10-05. |
| **D2** | Extend `SquadDeploymentPlan.CollectUnmanagedCollisions` to accept optional `SquadReceipt? receipt = null` and `IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null`. Filter out any file whose `(Target, RelativePath)` is managed in `receipt.Files` or `siblingGlobalReceipts`. | Issue #283 intake: files recorded in the receipt are managed by Squad and cannot collide as unmanaged files. Approved under autonomous "Hal approves" gate 2026-10-05. |
| **D3** | In `SquadDoctorCommand.ReportGlobalCollisions`, pass the resolved global receipt and sibling receipts to `CollectUnmanagedCollisions`. | Direct consequence of D1 and D2. Approved under autonomous "Hal approves" gate 2026-10-05. |
| **D4** | Author automated tests in `tests/KyberWeave.Tests/SquadCliCommandTests.cs` verifying that `squad doctor --global` after a clean global install reports no unmanaged collisions, while genuine unmanaged colliding files continue to trigger warnings. | Issue #283 intake requirement. Approved under autonomous "Hal approves" gate 2026-10-05. |

---

## 3. Implementation Tasks (Test-First Mode)

- [x] **T1: Failing Test — Doctor Global Scope Clean Install Reports No Unmanaged Collisions**
  Add a new test in `tests/KyberWeave.Tests/SquadCliCommandTests.cs`: `Doctor_GlobalScope_CleanInstall_ReportsNoUnmanagedCollisions`.
  The test performs a clean global install (or sets up an owned receipt and matching files on disk in a fake global root), executes `SquadDoctorCommand` with `Global = true`, and asserts that:
  - Exit code is 0.
  - Output contains `Global unmanaged collisions: none`.
  - Output contains no `warn Unmanaged global file` lines.
  Run test to verify RED (fails because doctor currently ignores the receipt).

- [x] **T2: Extend `SquadDeploymentPlan.CollectUnmanagedCollisions` with Receipt Filtering**
  In `src/KyberWeave.Core/Squad/Deployment/SquadDeploymentPlan.cs`:
  Update `CollectUnmanagedCollisions` signature:
  ```csharp
  public static IReadOnlyList<SquadUnmanagedPathCollision> CollectUnmanagedCollisions(
      string targetRoot,
      SquadDeploymentScope scope,
      IReadOnlyList<SquadDeploymentFile> renderedFiles,
      ISquadGlobalRootResolver? globalRoots,
      SquadReceipt? receipt = null,
      IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null)
  ```
  Build an index of managed file identities:
  ```csharp
  HashSet<string> managedIdentities = new(StringComparer.Ordinal);
  if (receipt is not null)
  {
      foreach (SquadOwnedFile file in receipt.Files)
      {
          managedIdentities.Add(DeployedFileIdentity(file.Target, file.RelativePath));
      }
  }
  if (siblingGlobalReceipts is not null)
  {
      foreach (SquadReceipt sibling in siblingGlobalReceipts)
      {
          foreach (SquadOwnedFile file in sibling.Files)
          {
              managedIdentities.Add(DeployedFileIdentity(file.Target, file.RelativePath));
          }
      }
  }
  ```
  In the loop over `normalizedFiles`:
  If `managedIdentities.Contains(DeployedFileIdentity(rendered.File.Target, rendered.File.RelativePath))`, skip checking for collision (it is a managed file).

- [x] **T3: Wire StateStore and Receipt Lookup into `SquadDoctorCommand`**
  In `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`:
  - Retain `_userPaths = userPaths;` and add `_stateStore = stateStore;` in constructor.
  - In `ReportGlobalCollisions`, resolve:
    ```csharp
    SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);
    SquadReceipt? receipt = stateStore.ReadReceipt(workingDirectory, SquadDeploymentScope.Global);
    IReadOnlyList<SquadReceipt> siblingReceipts = stateStore.ListOtherGlobalReceipts(workingDirectory);
    ```
  - Pass `receipt` and `siblingReceipts` to `SquadDeploymentPlan.CollectUnmanagedCollisions`.

- [x] **T4: Verify GREEN, Run Test Suites, and Validate Docs**
  - Verify T1 test passes (GREEN).
  - Run `SquadCliCommandTests` and verify all tests pass.
  - Run full build and test suites.
  - Run `docs validate` and `docs drift`.

- [x] **T5: Archival and PR Preparation**
  - Move plan to `docs/archive/plans/2026-10-05-doctor-receipt-lookup.md` per KW-DOC-LIFECYCLE-003.
  - Update `docs/plans/README.md`.
  - Open draft PR with title `fix(Squad):[Hal:Hermes:AGY][Issue#283] Fix squad doctor receipt lookup for identity-colliding files`.
