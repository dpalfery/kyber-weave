---
id: plans/2026-09-30-squad-global-user-state
title: Make Squad global state user-scoped instead of caller-root-scoped
doc-type: plan
status: ready
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-01
development-mode: test-first
---

# Make Squad global state user-scoped instead of caller-root-scoped

**Lifecycle:** Ready. The user approved both migration-policy decisions through the conductor on
2026-10-01; the Test contract and dispatch graph are approved for execution.

## Problem and goal

`SquadStateStore` currently stores Global lock, receipt, and journal files beneath
`KyberWeave/squad/roots/<root-key>/`, where `<root-key>` is the physical identity of the
repository or directory passed to the command. `SquadLifecycleService` then supplies every
other root-bound receipt to `SquadDeploymentPlan` as a sibling owner. The result is a global
deployment whose target files are user-global but whose state, ownership, mutex, and recovery
identity depend on the caller's current repository.

This produces two observable defects:

- a Global install from repository B can reject files installed from repository A as owned by
  "another Squad deployment" even though both commands address the same user-global harness;
- `squad status --global` from repository B cannot find repository A's receipt.

The goal is one canonical user-level Global deployment state, independent of the caller root,
while preserving project-scope behavior, receipt layout recovery, transactional rollback, and
actionable handling of every state shape written by older releases.

## Intake and development mode

- **Route:** PLAN. This is a bounded defect in the established KyberSquad lifecycle and state
  model; the affected requirements, transaction engine, and canonical documents already exist.
- **Development mode:** `test-first`, the repository default. No opt-out was supplied.
- **Discovery:** the Kyber-Weave documentation MCP was bound to this checkout at revision
  `9128e63` and identified `docs/kyber-squad/architecture.md` as the formal owner of
  `SquadStateStore`. Its CodeGraph joins reported no readable index, so code findings below are
  narrow self-gathered reads of the named state, lifecycle, plan, transaction, command, and test
  files. No broad raw-code search was used to infer architecture.

## Approved decisions

The user explicitly approved both recommended options through the conductor on 2026-10-01.

| Id | Approved choice | Execution constraint |
|---|---|---|
| Q1 | **Option (a):** read one healthy legacy `per-target-roots` partition compatibly for read-only commands, then atomically promote it before the first mutating Global command (`install`, `update`, or `uninstall`). | `status` remains read-only. Promotion is one no-overwrite, same-parent directory rename under the canonical Global lease, after validation and before mutation planning. |
| Q2 | **Option (a):** fail closed without changing disk when canonical and legacy state coexist or when multiple legacy partitions exist. | The diagnostic lists every conflicting binding id and gives actionable recovery guidance; the implementation never merges, selects, quarantines, or deletes a partition automatically. |

## Investigation findings

1. `SquadStateStore.ResolveStateFile` appends
   `roots/{GlobalRootBinding(targetRoot)}` for Global scope. `GlobalRootBinding` is the SHA-256
   physical-root key of the caller's target root. `ResolveStateDirectory` misleadingly returns
   only the unbound parent directory.
2. `SquadStateStore.ListOtherGlobalReceipts` enumerates every other binding under `roots/`.
   `SquadLifecycleService` passes those receipts into install, update, and uninstall planning.
   `SquadDeploymentPlan` rejects or suppresses mutations through
   `IsOwnedBySiblingReceipt`; this is the source of the reported ownership error.
3. `SquadTransaction` also acquires `kyber-weave-squad-<caller-root-key>`, writes the Global
   journal under the caller binding, and places its work directory under the caller repository.
   Changing only lock/receipt paths would therefore leave cross-repository writes able to race
   and recovery dependent on the cwd.
4. Transaction intent files persist each target file's resolved physical harness root, so
   rollback of target files does not need the originating repository. The intent's `rootKey`,
   lease key, journal location, state-file paths, and created-directory authority still use the
   caller-root identity and must move together.
5. Global receipt schema v2 and `layout` solve a different compatibility problem. A modern
   `per-target-roots` receipt can move independently of cwd. A legacy `single-root` receipt
   cannot: it stores `targetRoot: "."`, and the partition name retains only a hash of the
   originating physical path, not the path itself. Its existing original-root-only status and
   uninstall recovery must remain available and it must never be promoted into cwd-independent
   state.
6. Current tests intentionally pin private caller bindings and sibling ownership:
   `GlobalStateTwoTargetRootsSharingOneStateDirectoryRemainPrivatelyBoundToTheirOwnReceipts`,
   the Global alias-binding tests in `SquadDeploymentStateTests`, and sibling receipt tests in
   `SquadGlobalRootTests`. They must be replaced with the new public behavior, not merely deleted.
7. Canonical documentation currently advertises `KyberWeave/squad/roots/<root-key>/` and says
   sibling-global ownership is unchanged. `docs/kyber-squad/architecture.md`,
   `docs/kyber-squad/onboarding.md`, requirement KS-004, and ADR 0024's narrow consequence need
   aligned closeout.

## Recommended design

### Canonical Global state

Use one fixed state root:

```text
<ApplicationDataDirectory>/KyberWeave/squad/global/
  squad.lock.yml
  squad.receipt.json
  .squad-transaction/
```

`SquadStateStore.ResolveStateDirectory(_, Global)` returns this exact directory. Global lock,
receipt, journal, transaction work, and state-authority resolution no longer include a caller
root hash. Project scope stays byte-for-byte and path-for-path unchanged.

Derive a stable Global operation key from the canonical physical application-data path plus the
fixed `KyberWeave/squad/global` suffix. `SquadTransaction.Execute` and `Recover` use that key for
the in-process lease, OS mutex, and intent `rootKey`; they continue to validate the plan's caller
root physical identity separately because project files and legacy `single-root` files can still
depend on it. All modern Global invocations for one user therefore contend on one lease even when
their cwd values differ.

Do not add caller provenance to the receipt schema in this change. No current lifecycle behavior
consumes it, and adding a last-caller field would create schema churn without restoring ownership
authority to that caller.

### Legacy state inventory and promotion

Centralize Global state selection in `SquadStateStore`; callers must not enumerate `roots/`
themselves. Inventory the canonical directory and each immediate legacy
`roots/<64-lowercase-hex>/` child without following links. Reject malformed binding names,
unreadable/corrupt state, or unexpected transaction artifacts with a diagnostic that names the
state path and next action.

| Disk state | Read-only Global command | Mutating Global command |
|---|---|---|
| no canonical state, no legacy partitions | report no deployment | create canonical state |
| canonical state only | read canonical state | mutate canonical state |
| exactly one healthy `per-target-roots` legacy partition | read that partition without using cwd | atomically rename the partition directory to `global/` under the Global lease, re-read it, then plan the mutation, per approved Q1 |
| exactly one legacy `single-root` partition and caller binding matches | preserve existing status/uninstall compatibility at that legacy path; install/update remain refused by ADR 0024 | allow only the existing safe uninstall flow; never promote |
| exactly one legacy `single-root` partition and caller binding differs | fail with guidance to rerun status/uninstall from the original root; never guess the unhashed path | same |
| a legacy partition has an unfinished journal/work tree | fail without moving it; name the binding and original-root recovery constraint | same |
| canonical plus any legacy partition, or multiple legacy partitions | fail closed without changing disk; list every conflicting binding id and recovery guidance, per approved Q2 | same |

Promotion is an atomic, no-overwrite directory rename between siblings beneath
`KyberWeave/squad/`; it runs under the same Global lease later used by transactions. It is allowed
only after both state files parse consistently as a modern Global deployment and no transaction
journal exists. On success, remove only an empty `roots/` parent. On rename collision or any
post-move validation failure, preserve the evidence and return an actionable conflict rather than
deleting either side.

### Ownership and lifecycle semantics

Delete `ListOtherGlobalReceipts`, `SiblingGlobalReceipts`, the
`siblingGlobalReceipts` parameters, `IsOwnedBySiblingReceipt`, and
`SiblingGlobalOwnership`. There is one Global receipt, so its owned `(target, relativePath)` set is
the only managed ownership set. Existing unmanaged-file collision, adoption, local-edit
preservation, target reconciliation, and legacy receipt-layout rules remain unchanged.

Before install, update, or uninstall reads Global state, `SquadLifecycleService` asks the state
store to prepare the effective state for mutation. Status and confirmation reads use the
read-only resolver. A same-target install from any repository therefore finds the one receipt and
follows the existing update path; a different requested target roster receives the existing
"use update to modify targets" conflict instead of a sibling-owner conflict.

## Test contract

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T2 | `tests/KyberWeave.Tests/SquadDeploymentStateTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~SquadDeploymentStateTests` | Two different existing caller roots resolve one canonical Global lock/receipt/journal and one operation key; project paths remain root-bound. A unique healthy modern legacy partition is readable from another root and promotes before the first mutation, never during a read-only command. Single-root, pending-journal, malformed, canonical-plus-legacy, and multi-partition states follow the approved matrix without data loss. Global recovery uses the canonical journal from another cwd. | T1 adds the state-path, lease-contention, recovery, and migration cases and records their focused failure against hash-partitioned paths before T2 starts | The same cases pass without weakening assertions; crash/failure injection proves promotion never deletes evidence and existing project transaction cases remain green |
| T4 | `tests/KyberWeave.Tests/SquadGlobalRootTests.cs`; `tests/KyberWeave.Tests/SquadCliCommandTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SquadGlobalRootTests|FullyQualifiedName~SquadCliCommandTests"` | Install Global from root A, then status/update/uninstall or same-target install from root B, uses the same receipt and managed files. No "another Squad deployment" path remains. Ambiguous legacy state exits non-zero, lists every conflicting binding id with recovery guidance, and leaves disk unchanged. Legacy single-root uninstall from its matching original root still works. | T3 adds the cross-root lifecycle and command cases, records status missing the deployment and same-file install reaching the sibling-owner conflict, and retains that focused output before T2 or T4 starts | The same cases pass without weakening assertions; project-scope command cases are unchanged, ambiguous-state snapshots prove no disk mutation, and old single-root recovery remains green |
| T5 | Documentation and ADR only | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | Canonical docs state the single user-global root, lease, migration matrix, and diagnostics; a new ADR explicitly replaces only ADR 0024's sibling-global consequence | n/a — no executable RED for prose; review the current contradictory path and sibling statement before editing | Both documentation checks report zero findings and `docs_for_symbol SquadStateStore` still resolves to the updated architecture |

Changing or weakening an approved row after execution begins returns this plan to Draft and
reopens approval of the Test contract.

## Dispatchable tasks

### T1 — RED: state, migration, lease, and recovery contract

- **Objective:** replace caller-binding assertions with failing tests for the canonical Global
  state and every legacy-state matrix row.
- **Files/symbols:** `tests/KyberWeave.Tests/SquadDeploymentStateTests.cs`;
  `SquadStateStore.ResolveStateDirectory`, `ResolveLockPath`, `ResolveReceiptPath`,
  `ResolveTransactionDirectory`; `SquadTransaction.Execute` and `Recover`.
- **Acceptance:** capture RED evidence described by Test-contract row T2. Existing project-scope,
  receipt-layout, rollback, and recovery tests are not weakened.
- **Dependencies:** none.
- **Required skills:** `test-dev`.

### T2 — GREEN: canonical state identity and guarded legacy promotion

- **Objective:** implement the one-root resolver, shared Global operation lease, migration guard,
  and cwd-independent transaction/recovery identity.
- **Files/symbols:**
  `src/KyberWeave.Core/Squad/Deployment/SquadStateStore.cs`;
  `SquadPhysicalRootIdentity.cs`; `SquadTransaction.cs`; one narrowly scoped internal lease/state
  selection type under `src/KyberWeave.Core/Squad/Deployment/` if needed to share lease ownership
  between promotion and transaction execution.
- **Acceptance:** Test-contract row T2 is GREEN with the same assertions; new Global state uses
  only `KyberWeave/squad/global`; a unique modern legacy directory moves atomically under the
  Global lease; unsafe legacy states do not change; project paths and old single-root matching-root
  uninstall paths remain unchanged.
- **Dependencies:** T1 and T3. Both RED contracts must be recorded before implementation starts.
- **Required skills:** `csharp-dev`.

### T3 — RED: cross-repository lifecycle and CLI contract

- **Objective:** replace sibling-deployment tests with failing end-to-end lifecycle and command
  cases proving Global state is independent of the path supplied to each command.
- **Files/symbols:** `tests/KyberWeave.Tests/SquadGlobalRootTests.cs`;
  `tests/KyberWeave.Tests/SquadCliCommandTests.cs`; existing fake user paths and global-root
  resolvers.
- **Acceptance:** capture RED evidence described by Test-contract row T4. Keep explicit guards for
  project scope and ADR 0024 single-root recovery.
- **Dependencies:** none.
- **Required skills:** `test-dev`.

### T4 — GREEN: one receipt through install, update, status, and uninstall

- **Objective:** remove sibling ownership as a lifecycle concept and route all Global commands
  through the state store's read-only or mutation-preparation API.
- **Files/symbols:**
  `src/KyberWeave.Core/Squad/Deployment/SquadLifecycleService.cs`;
  `SquadDeploymentPlan.cs`; `SquadStateStore.ListOtherGlobalReceipts` (remove);
  `src/KyberWeave.Cli/Commands/Squad/SquadStatusCommand.cs`;
  `SquadCommandComposition.ResolveUninstallGlobalTargetRoots`; command error handling where needed.
- **Acceptance:** Test-contract row T4 is GREEN; no sibling receipt parameter/helper/message
  remains; same-target install from a different cwd uses the existing update path; ambiguous state
  is actionable and non-destructive; receipt v1/v2 and layout behavior is unchanged.
- **Dependencies:** T2 and T3.
- **Required skills:** `csharp-dev`.

### T5 — Canonical documentation and architectural record

- **Objective:** make the governed corpus describe the shipped state identity and preserve the
  durable migration decision.
- **Files/symbols:** add one ADR under the path declared by `<adr-index>`; update
  `docs/kyber-squad/architecture.md` (`SquadStateStore`, physical identity, lease, state files),
  `docs/kyber-squad/onboarding.md` (Global path and migration/operator guidance), and
  `docs/kyber-squad/requirements.md` (KS-004). Add the new ADR id to architecture `decided-by`.
- **Acceptance:** Test-contract row T5 is GREEN. The ADR says exactly which ADR 0024 consequence
  it replaces while retaining its receipt schema/layout and single-root recovery decisions.
- **Dependencies:** T4.
- **Required skills:** `architecture-decision-record`, `app-docs-standard`.

### T6 — Verification, review, and docs-dev closeout

- **Objective:** run the focused contract, repository gates, review, and lifecycle closeout.
- **Files/symbols:** no implementation scope; after approval and delivery, archive this plan and
  update `<plan-index>` in the implementation PR. Harvest the final Q1/Q2 decisions into the ADR
  and canonical docs before archival.
- **Acceptance:** all gates below pass; code review has no unresolved findings; the plan is removed
  from Active Plans and linked from Archived Plans before `docs validate . --merge-ready`.
- **Dependencies:** T5.
- **Required skills:** `code-review`, `app-docs-standard`.

## Dependency graph and concurrency audit

```text
T1 ─┐
  ├─> T2 -> T4 -> T5 -> T6
T3 ─┘        ^
       └── T3 supplies T4's recorded RED contract
```

T1 and T3 use different test files and run concurrently. T2 waits for both RED tasks so no
implementation can invalidate their evidence. T2 owns state/transaction files; T4 owns
lifecycle/plan/CLI files and waits for T2's concrete state API while consuming T3's recorded
contract. Documentation waits for the behavior to be green. **MAX_CONCURRENCY: 2.**

## Risks and out of scope

- **Legacy path is irrecoverable from its hash:** a `single-root` receipt cannot be made
  cwd-independent without guessing. Preserve the safe original-binding uninstall route and fail
  elsewhere.
- **Interrupted old transaction:** its journal binding and repository work directory were split.
  Never move it automatically; preserve all artifacts and identify the binding in the diagnostic.
- **Cross-process race:** inventory, promotion, execute, and recover must use the same canonical
  Global lease. A separate migration mutex would leave a check/move race.
- **Symlink and case aliases:** canonical application-data aliases must converge on one operation
  key. Legacy binding names remain exact lowercase hashes and enumeration must not follow links.
- **State/target distinction:** the user-global state root does not replace per-target harness
  roots (`~/.codex`, `~/.claude`, and so on). Receipts remain target-relative and transaction
  intents retain captured physical target roots.
- **Out of scope:** a new receipt schema or caller-repository provenance; automatic merge of
  independently versioned legacy deployments; changes to target discovery,
  rendering, adoption, update target reconciliation, or rc.9/rc.10 file-layout semantics.

## Verification gates

Focused RED/GREEN commands are in the Test contract. Before review, run:

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json
```

`docs drift` currently reports no readable CodeGraph index through the documentation MCP. It must
be run after implementation; if the execution environment still has no usable index, report it as
skipped rather than passed, per repository policy.

## Review and closeout

Review must concentrate on no-loss migration, one mutex across cwd values, preserved transaction
recovery authority, symlink containment, and unchanged project/receipt-layout contracts. This is a
durable change to persisted state identity, so closeout creates the ADR in T5 rather than leaving
Q1/Q2 only in this plan. The implementation PR archives this plan, updates the inventory, and runs
the merge-ready documentation gate only after code, canonical docs, and ADR agree.