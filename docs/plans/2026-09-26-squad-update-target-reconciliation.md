---
id: plans/squad-update-target-reconciliation
title: Squad update target reconciliation
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-26
development-mode: test-first
---

# Squad update target reconciliation

## Status

Ready. The conductor approved execution as `approve-issue-99` on 2026-09-26, sourced from
the user's research-and-fix order for issue 99. This plan is now implementation authority.

## Problem and goal

[Issue 99](https://github.com/dpalfery/kyber-weave/issues/99) reports that an existing global
Kyber-Squad deployment cannot gain targets without uninstalling first. `squad install`
correctly refuses a mismatched installed target set and directs the operator to `squad
update`, but `squad update --target <full-set>` reports success while planning and deploying
only the lock file's old targets. The goal is for an explicit update target set to reconcile
the deployment and its persisted state so newly requested targets are rendered, receipted,
and visible to later lifecycle commands, including in dry-run output.

## Intake assessment

Recommendation: **PLAN**. This is a bounded defect in the established Squad deployment,
transaction, lock, and receipt architecture. The issue's clean uninstall/reinstall workaround
shows that target parsing and renderers already support the requested targets; the missing
behavior is isolated to how update chooses the desired target set.

## Development mode

`test-first` — the user did not opt out, so the repository default applies.

## Discovery method

The CodeGraph and Kyber-Weave MCP namespaces were unavailable after two attempts. Per
repository guidance, investigation used the local `codegraph explore` fallback for code and
began documentation lookup at `docs/README.md`; findings gathered that way are labeled as
narrow self-gathered lookups. No broad delegated research was possible in this harness.

## Approved decisions

| ID | Decision | Approval provenance |
|---|---|---|
| A1 | Use `development-mode: test-first`. | The assignment states that the user did not opt out, so the repository default applies. |
| A2 | Keep the plan bounded to issue 99 and change no implementation during planning. | Explicit assignment constraint. |
| D1 | Treat an explicit update target list as the complete desired target set. With no explicit list, reuse the receipt roster and never re-detect filesystem markers. | Resolved from issue 99, KS-003, and the existing lifecycle contract; included in conductor approval `approve-issue-99` on 2026-09-26. No product decision was inferred. |
| D2 | Keep install's mismatch refusal and fix the update path it already recommends. | Resolved from issue 99's accepted update alternative; included in conductor approval `approve-issue-99` on 2026-09-26. No product decision was inferred. |
| A3 | Execute this decision-complete plan without reopening D1 or D2. | Conductor approval `approve-issue-99` on 2026-09-26, sourced from the user's `/conductor` order to research and fix issue 99. |

## Investigation findings

- Issue 99 reproduces the defect from a global `claude`-only deployment: explicit update with
  six targets still plans 113 files, leaves `squad.lock.yml` at `targets: [claude]`, and
  leaves the receipt unchanged.
- A clean six-target install deploys 678 files and passes `status` and `doctor`, excluding the
  renderers from the likely defect.
- Narrow self-gathered CodeGraph fallback: `SquadUpdateCommand.Execute` parses
  `settings.Targets`, places them in `SquadTargetResolutionRequest.ExplicitTargets`, and passes
  `decision.Targets` to `SquadUpdateRequest`.
- Narrow self-gathered CodeGraph fallback: `SquadLifecycleService.UpdateAsync` already treats a
  non-empty `SquadUpdateRequest.Targets` list as the render and `BuildLock` target set; only an
  empty list falls back to distinct targets recovered from the previous receipt.
- Narrow self-gathered documentation fallback: KS-003 permits explicit CLI flags and existing
  receipts as deterministic target sources. The onboarding guide says update/uninstall consume
  the recorded receipt roster and never re-detect, but its update command reference currently
  omits the implemented `--target` and `--exclude` options. This wording needs clarification,
  not an additive-install redesign.
- Narrow self-gathered CodeGraph fallback confirmed the root cause in
  `SquadTargetResolver.SelectTargets`: update and uninstall return receipt targets before
  considering `ExplicitTargets`. That makes `SquadUpdateCommand`'s parsed target list dead input,
  despite passing the resolver decision into the lifecycle.
- `SquadLifecycleService.UpdateAsync` is already prepared for the fix: it renders the provided
  target set, builds the lock from that set, and creates the update plan against the previous
  receipt. The source change should therefore stay in target resolution rather than duplicating
  merge logic in the CLI or lifecycle service.
- Existing `LifecycleOperationsUseReceiptAndNeverRedetect` coverage proves receipt fallback and
  no marker detection but does not supply explicit targets. Existing CLI tests validate malformed
  update targets and missing/empty receipts, but no command-level regression asserts that valid
  explicit targets reach rendering and persisted state.
- `FakeSquadRenderer.RenderRequests`, `SquadStateStore.ReadLock`, and
  `SquadStateStore.ReadReceipt` provide existing seams for one command-level regression to assert
  the selected target set, persistent state, and dry-run file count without touching a real home
  directory.
- `SquadDeploymentPlan.CreateUpdate` already treats rendered output as the desired deployment:
  it writes newly rendered target files, removes no-longer-rendered clean managed files, and
  preserves locally edited receipt-owned files. No transaction-engine change is required.

## Scope

In scope:

- explicit target selection for an existing `squad update` deployment;
- dry-run planning for the explicit desired target set;
- lock and receipt persistence after adding targets;
- regression coverage for update and CLI-visible output;
- canonical Kyber-Squad usage documentation if its current update contract needs
  clarification.

Out of scope:

- changing `squad install` into an additive operation;
- renderer changes or new target support;
- changing update exclusion behavior; issue 99 concerns explicit `--target`;
- transaction, managed-edit preservation, or rollback redesign;
- unrelated install, uninstall, status, or doctor behavior.

## Test contract

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/SquadTargetResolutionTests.cs`; `tests/KyberWeave.Tests/SquadCliCommandTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SquadTargetResolutionTests.UpdateExplicitTargetsOverrideReceiptTargets\|FullyQualifiedName~SquadCliCommandTests.UpdateExplicitTargetsReconcileRenderedReceiptAndLockTargets\|FullyQualifiedName~SquadCliCommandTests.UpdateExplicitTargetsDryRunReportsDesiredSetWithoutWritingState"` | An update from a `codex` receipt with explicit `codex,claude` resolves from `Explicit`, sends both targets to rendering, persists both in the lock and receipt on a real update, reports the two-target receipt count on dry-run, and leaves pre-existing state byte-identical on dry-run. Receipt fallback without explicit targets and uninstall receipt behavior remain covered by the existing suite. | Add and run the three named tests before changing production code. The intended RED is that `SquadTargetResolver` reports `Receipt`, rendering receives only `Codex`, persistent state lacks `claude`, and dry-run reports the one-target count. Compilation errors or unrelated fixture failures are not acceptable RED evidence. | Run the same command after T2 without weakening assertions; all three tests pass. |
| T2 | `tests/KyberWeave.Tests/SquadTargetResolutionTests.cs`; `tests/KyberWeave.Tests/SquadCliCommandTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SquadTargetResolutionTests.UpdateExplicitTargetsOverrideReceiptTargets\|FullyQualifiedName~SquadCliCommandTests.UpdateExplicitTargetsReconcileRenderedReceiptAndLockTargets\|FullyQualifiedName~SquadCliCommandTests.UpdateExplicitTargetsDryRunReportsDesiredSetWithoutWritingState"` | `SquadTargetResolver.SelectTargets` gives update's explicit target list precedence over receipt fallback, while no-target update and uninstall retain receipt-only/no-redetection behavior; CLI help names `--target` as the complete desired update set. | Consume T1's saved failing run. If the tests are already green before the source edit, stop and re-investigate rather than claiming RED. | The unchanged T1 tests pass after the smallest resolver/help-text change; the full `SquadTargetResolutionTests` and `SquadCliCommandTests` classes also pass. |
| T3 | `docs/kyber-squad/onboarding.md`; `docs/kyber-squad/requirements.md` | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . && dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | Canonical docs show `squad update --target`, define an explicit list as the complete desired set, define receipt fallback when omitted, retain no-marker-redetection behavior, and align KS-003 with that precedence. | Genuinely no executable behavior test: preserve the pre-edit passages showing the RED documentation contradiction (the command reference omits `--target`, while the guide says update always consumes receipt targets). | Read back the edited sections against D1, then both documentation commands pass with zero findings. |
| T4 | Read-only verification of the whole change | `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json` | Deterministic repository gates and council review evaluate the complete implementation, tests, and docs with the focused regressions still green. | No new test code: this is a read-only verification task. Any gate or review finding is failure evidence and returns work to the owning task. | Declared gates pass and review returns approval-quality with no unresolved finding attributable to issue 99. |
| T5 | `docs/plans/2026-09-26-squad-update-target-reconciliation.md`; `docs/plans/README.md`; `docs/archive/plans/2026-09-26-squad-update-target-reconciliation.md` | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready && dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | Verified behavior is harvested into canonical docs, this plan is marked complete and archived, and the inventory moves it from Active to Archived with review evidence. | Genuinely no application test: before closeout, `--merge-ready` must fail only with `KW-DOC-LIFECYCLE-003` because this active plan remains under `docs/plans/`. | After review evidence is recorded and the plan is archived, `docs validate . --merge-ready` and `docs drift .` both pass. |

## Tasks

### T1 — Establish the failing regression contracts

- **Objective:** Add a pure resolver contract and command-level real/dry-run regressions that
  reproduce issue 99 before production code changes.
- **Files and symbols:**
  - `tests/KyberWeave.Tests/SquadTargetResolutionTests.cs` —
    `UpdateExplicitTargetsOverrideReceiptTargets`.
  - `tests/KyberWeave.Tests/SquadCliCommandTests.cs` —
    `UpdateExplicitTargetsReconcileRenderedReceiptAndLockTargets`,
    `UpdateExplicitTargetsDryRunReportsDesiredSetWithoutWritingState`, and existing
    `SeedDeployment`.
- **Acceptance criteria:** The tests arrange an existing one-target receipt, request that target
  plus a second target, assert resolver source and ordered targets, assert both render/persisted
  target sets for the real update, assert the two-target deployed-file count for dry-run, and
  assert dry-run leaves lock and receipt bytes unchanged. The initial run fails only for the
  receipt-precedence defect described in the Test contract.
- **Dependencies:** none.
- **Required skills:** `test-dev`.

### T2 — Give explicit update targets precedence

- **Objective:** Correct update target resolution at the existing pure decision seam and align
  CLI help with complete desired-set semantics.
- **Files and symbols:**
  - `src/KyberWeave.Core/Squad/Deployment/SquadTargetResolver.cs` —
    `SquadTargetResolver.SelectTargets`.
  - `src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs` —
    `SquadUpdateSettings.Targets` summary and option description.
- **Acceptance criteria:** For `Update`, a non-empty explicit list parses to an ordered set with
  source `Explicit`; otherwise the receipt remains the fallback. `Uninstall` remains receipt-only.
  Configuration and marker detection do not become update fallbacks. No union is computed: the
  explicit list is the complete desired set passed to the existing lifecycle. Reach GREEN on the
  unchanged T1 contracts, then run both affected test classes after any refactor.
- **Dependencies:** T1.
- **Required skills:** `csharp-dev`.

### T3 — Document the update target contract

- **Objective:** Make the governed command reference and deterministic-resolution requirement
  match the fixed behavior.
- **Files and symbols:**
  - `docs/kyber-squad/onboarding.md` — Command Reference, Detection Rules, and Updating
    Deployments.
  - `docs/kyber-squad/requirements.md` — KS-003.
- **Acceptance criteria:** The update syntax includes `--target`; prose says an explicit list is
  the full desired deployment target set, omission reuses the receipt, and update does not
  auto-detect markers. Include a copy-ready add-target example that names the existing and new
  targets. Do not document additive install or change exclusions.
- **Dependencies:** none; D1 fixes the contract, and this file scope is disjoint from T1/T2.
- **Required skills:** `app-docs-standard`, `kyber-weave-docs`.

### T4 — Run completion gates and review

- **Objective:** Verify the focused fix against repository-wide quality and release-path gates,
  then obtain the declared review verdict.
- **Files and symbols:** read-only; `KyberWeave.sln`, `.apm/skills/kyber-weave-docs`,
  `scripts/update-loop.sh`, `artifacts/gates.json`, and review inputs.
- **Acceptance criteria:** Run the focused contracts, formatting, Release build, full test suite,
  Squad skill validate/lint/scan, canonical docs validate/drift, `./scripts/update-loop.sh`, and
  `review gates`. Triage every failure to T1, T2, or T3; do not weaken a contract or suppress a
  warning. Review has no unresolved issue-99 finding.
- **Dependencies:** T2 and T3.
- **Required skills:** `resharper-clt`, `code-review`.

### T5 — Perform docs closeout and archive the plan

- **Objective:** Record verified results, harvest the shipped contract into canonical docs, and
  remove the active-plan merge blocker.
- **Files and symbols:**
  - `docs/plans/2026-09-26-squad-update-target-reconciliation.md`.
  - `docs/archive/plans/2026-09-26-squad-update-target-reconciliation.md`.
  - `docs/plans/README.md` — Active Plans and Archived Plans inventories.
- **Acceptance criteria:** Record implementation and review evidence; keep D1/D2 provenance;
  mark complete; move the plan to the archive; replace the Active row with an Archived row; run
  merge-ready validation and drift to zero findings. Create an ADR only if implementation reveals
  a genuinely new, costly-to-reverse architectural decision.
- **Dependencies:** T4.
- **Required skills:** `app-docs-standard`, `architecture-decision-record` only if the ADR
  threshold is met.

## Dependency graph and concurrency

```text
T1 (RED tests) ──> T2 (GREEN implementation) ──┐
                                               ├──> T4 (gates/review) ──> T5 (closeout)
T3 (canonical docs) ───────────────────────────┘
```

`MAX_CONCURRENCY: 2`. T1 and T3 have disjoint files and consume only the settled contract.
T2 waits for T1's concrete RED evidence. T4 consumes T2's GREEN implementation and T3's
canonical docs. T5 consumes T4's review evidence. No tasks sharing a file are concurrent.

## Risks

- Choosing union semantics instead of desired-set reconciliation could make explicit target
  removal impossible or silently preserve targets an operator intended to remove.
- Rendering the new set without persisting matching lock and receipt state would make later
  update, status, doctor, and uninstall operations inconsistent.
- A unit-only test could miss the CLI settings-to-core handoff or dry-run output regression.
- Desired-set semantics can remove clean managed files for omitted targets. Help and onboarding
  must say “complete desired set” so operators include existing targets when adding another.
- An edited file for an omitted target is retained by existing managed-edit preservation even
  though the lock's desired roster no longer contains that target. This plan does not redesign
  that established safety behavior.
- The original report is macOS global scope, while deterministic tests use isolated project
  roots. The defect is in scope-independent pure resolution; the command regression must assert
  the same request path and T4 must run the repository's release loop.

## Verification gates

- `dotnet restore KyberWeave.sln`
- `dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal`
- `dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal`
- The T1/T2 focused test command, followed by:
  `dotnet build KyberWeave.sln -c Release --no-restore`
- `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-weave-docs`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill lint .apm/skills/kyber-weave-docs --min-desc-score 70`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill scan .apm/skills/kyber-weave-docs --fail-on critical`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`
- `./scripts/update-loop.sh`
- `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`

## Planning verification

Run from `/workspace` on 2026-09-26 after authoring:

- `dotnet restore KyberWeave.sln` — pass.
- `dotnet build KyberWeave.sln -c Release --no-restore` — pass, 0 warnings and 0 errors.
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` — pass,
  0 findings.
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` — pass,
  0 findings.
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
  --merge-ready` — expected non-zero result with exactly one finding,
  `KW-DOC-LIFECYCLE-003`, naming this active Draft. No other finding was present. This is not
  a closeout failure; T5 removes it after implementation and review.

## Review and closeout

T4 runs the repository review workflow after GREEN and canonical docs. T5 is the explicit
`docs-dev` closeout: record evidence, retain the established contract in canonical docs, archive
this plan, update the plan index, and rerun merge-ready validation and drift checks. Do not leave
the active plan in `docs/plans/` when merge-ready.

## Human judgement reserved

The only reserved human judgement is approval of this complete Draft for execution. No open
product or architecture question remains, and no live global-home mutation is required to approve
the plan.
