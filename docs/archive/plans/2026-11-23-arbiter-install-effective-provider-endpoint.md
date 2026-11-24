---
id: plans/2026-11-23-arbiter-install-effective-provider-endpoint
title: Arbiter install and uninstall resolve the effective provider endpoint
doc-type: plan
status: archived
component: KyberArbiter
owner: dpalfery
last-reviewed: 2026-11-23
development-mode: test-first
documentation-impact: extends-component
impact-components:
  - KyberArbiter
---

# Arbiter install and uninstall resolve the effective provider endpoint

**Status: Archived, 2026-11-23.**

## Problem and goal

`kyber-weave arbiter install --provider systemone` stops with `KW-ARB-KEY-001`
naming the TypeSafe origin even when the per-user override
(`~/.config/kyber-weave/arbiter.yml`) sets `kind: systemone` and an endpoint such
as `https://openrouter.ai/api/v1` and the key for that origin is already stored:
issue [#664](https://github.com/dpalfery/kyber-weave/issues/664). The provider
line reports the effective kind because `ArbiterInstallService.ReadExistingProviderKind`
applies the override, but `ArbiterInstallService.ResolveEndpoint`
(src/KyberWeave.Application/Arbiter/ArbiterInstallService.cs:469-478) reads the host
configuration alone and falls back to the pinned TypeSafe endpoint, so the origin the
key is looked up under is wrong whenever the override names an endpoint. The same
host-config-only read sits in `ArbiterInstallService.Verify` (:615), in install's
environment-key check (`ResolveStoredKey`, :492-502, which never passes the override
endpoint to `ArbiterKeyResolver.Resolve`), and in
`ArbiterUninstallCommand.RemoveKey` (src/KyberWeave.Cli/Commands/Arbiter/ArbiterUninstallCommand.cs:210-223),
where `--remove-key` deletes the credential for the wrong origin — or none — and the
user believes the key is gone.

Goal: install and uninstall derive the endpoint, its origin and every key lookup from
the effective provider (host configuration plus the per-user override), the same way
`ArbiterStatusCommand` and `ArbiterKeyService` already do, while the environment key
stays bound to the TypeSafe origin or the user-override origin
(ArbiterKeyResolver.cs:7-11) so a repository whose configuration points elsewhere can
never obtain the user's key.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| Fix issue 664: `arbiter install --provider systemone` ignores the per-user override endpoint when resolving the key | Problem and goal; T1 | Diagnosis reproduced on this branch (exit 2, `KW-ARB-KEY-001` for `https://api.typesafe.ai`); Test contract rows 1 and 6 prove the fix at the service and command seams |
| Expected: `ResolveEndpoint` resolves the effective provider (host config + per-user override) the way `ReadExistingProviderKind` does, before deriving origin and looking up the key | D2; T1 files (`ReadExistingProviderKind` generalization, `Prepare` endpoint derivation, `ResolveEndpoint` removal) | One effective-provider read inside `Prepare`'s existing `YamlException` guard; `Prepare_SystemoneWithOverrideEndpoint_ResolvesTheKeyFromTheOverrideOrigin` asserts the plan's endpoint and origin |
| The fix must leave no sibling reading host config only, or state exactly why one is intentionally left | D1 (install's `ResolveStoredKey` and `Verify`, and uninstall's `RemoveKey`, are fixed; `ArbiterSetupCommand` intentionally left, with the reason); T1 file list names each | Investigation findings enumerate every `ArbiterKeyResolver` caller and classify it override-aware, fixed, or intentionally unchanged |
| A new regression test is required, test-first, covering the reported scenario (host `kind: none`, override `systemone` + `https://openrouter.ai/api/v1`, key stored only under the override origin, `--provider systemone`, dry run) | Test contract rows 1 and 6 | `Prepare_SystemoneWithOverrideEndpoint_ResolvesTheKeyFromTheOverrideOrigin` and `Install_SystemoneWithOverrideEndpointAndStoredKey_CompletesTheDryRun`, both RED today |
| Plan the malformed-override path the addendum names | D2; Test contract rows 4 and 8 | `Prepare_MalformedOverride_StopsWithOneError` and `Execute_RemoveKey_MalformedOverride_DeletesNothing` pin the fail-closed behavior through the restructure |
| Secrets reasoning explicit: the env key is deliberately origin-bound, so widening which origin install accepts must not let a repository obtain the user's key | D3; Test contract row 3 | Key resolution delegates to `ArbiterKeyService.TryResolveKey`, whose `UserOverrideEndpoint` computes the binding from the override alone; `Prepare_RepositoryEndpoint_NeverServesTheEnvironmentKey` guards it |
| Targeted test classes only; never the full suite; never start the dashboard | Test contract runner command; T1 Commands | Filters name three classes in two unit projects; no full-suite or dashboard command appears anywhere in the plan |
| Work only in the named worktree | This plan and its task file are written in the worktree; Verification gates run there | Worktree path recorded in Investigation findings |
| Derive the task file with `kyber-weave docs tasks --write`, validate, run `docs validate .` and `docs drift .` | Verification gates | Both checks run in the worktree after the last plan edit |
| Commit the plan and its task file on the run branch | D6 | The conductor's finalize tool commits plan and task file together; the architect's command set excludes git writes |

## Scale

`documentation-impact: extends-component` on `KyberArbiter`: the change extends the
existing per-user-override precedence and key-origin binding to the install and
uninstall surfaces, and the runbook/architecture prose that describes those surfaces
gains the corrected behavior at closeout.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Fix `ResolveEndpoint` alone, or also align the host-config-only siblings (`ResolveStoredKey`, `Verify`, install's env-key remedy, `ArbiterUninstallCommand.RemoveKey`)? | Fix the whole key-derivation path: install's endpoint read, its two key-resolution call sites, and uninstall's `RemoveKey`. `ArbiterSetupCommand` is intentionally left: it *writes* the override and takes its endpoint from argv (`--endpoint`, default `TypeSafeEndpoint`, ArbiterSetupCommand.cs:31-38), so it reads no host configuration and is not a sibling of this defect. | Leaving `Verify` host-config-only would fail an install whose key resolved for the override origin; leaving `RemoveKey` host-config-only would delete the TypeSafe credential while the user's override-origin key silently survives `--remove-key`. Doctor, status, serve and the provider factory are already override-aware and are mirrored, not modified. | ANSWERED — autonomous (diagnosis blast radius; siblings' source on this branch) |
| D2 | How is the override applied in install, and what happens on a malformed override file? | Generalize `ReadExistingProviderKind` into one read that returns the merged `ArbiterProviderConfig` (configured kind plus effective provider), called once inside `Prepare`'s existing `catch (YamlDotNet.Core.YamlException)` block (:59-67); the endpoint is the effective provider's endpoint when non-blank, else `ArbiterProviderConfig.DefaultNone.Endpoint`. Uninstall applies `userSettings.ApplyTo` after `configs.Load` inside its own catch that prints the escaped message and deletes nothing. `ReadExistingProviderKind` stays the model; the addendum's gap (the catch covering only the kind read) closes because there is only one read left. | `FileArbiterUserSettings.ApplyTo` throws `YamlException` on a malformed override (IArbiterUserSettings.cs:14); install already fails closed on it today through the kind read, and the single-read restructure keeps that true for the endpoint. `ArbiterConfigResolver.Load` (:94-97) is deliberately not reused as-is because it applies no override; `TryResolve` (:69) was rejected because it changes the load semantics (repository path only) that install and uninstall rely on to distinguish project and user scope. | ANSWERED — autonomous (issue's Expected; investigator addendum) |
| D3 | How does install decide which origin the environment key may serve? | Delegate both call sites (`ResolveStoredKey`, `Verify`) to `IArbiterKeyService.TryResolveKey` (new constructor dependency on `ArbiterInstallService`), whose `UserOverrideEndpoint` (ArbiterKeyService.cs:50-68) computes the binding from the per-user override alone and fails safe to null. The env key therefore serves only the TypeSafe origin or the override's origin — never a repository-configured endpoint. | `ArbiterKeyResolver.IsEnvOrigin` (ArbiterKeyResolver.cs:101-114) is the boundary; the repository's trust level is the reason the binding exists (ArbiterKeyResolver.cs:7-11). Re-implementing the binding inside install was rejected: a third copy of `UserOverrideEndpoint` is one transcription away from handing a repository the env key. `TryResolveKey`'s catch set (FileFailure, InvalidOperationException, Win32Exception) matches what both call sites catch today. Amended per plan review: both call sites pass the un-merged repository configuration, because `UserOverrideEndpoint` compares the merged endpoint against the repository's — a merged config would return null and silently drop the env key for the override origin. | ANSWERED — autonomous (existing override-aware sibling pattern; secrets constraint in the dispatch; repository-config requirement made explicit at plan review) |
| D4 | Does the override's kind change whether install demands a key? | No. `needsKey` stays keyed on the CLI-or-existing provider kind (Prepare :92-93). Only endpoint, origin and key resolution become override-aware. | The issue reports the omitted-`--provider` flow as working (`none (kept...)` plus the precedence note) and asks only for the endpoint/origin fix; keying `needsKey` on the override kind would change that flow's behavior, which the request did not ask for. Guard test `Prepare_OmittedProvider_KeepsNoneAndSkipsTheKey` pins it. | ANSWERED — autonomous (issue body) |
| D5 | One task or separate install/uninstall tasks? | One task. Both commands share the root cause, the pattern to copy, and the `tests/KyberWeave.Testing` credential-fake widening; a split would serialize on a shared fixture file for no parallel gain. | Rate `medium`: the change decides which credential-store origin and which env-key origin the commands use (trust-boundary decisions with a known defensive pattern to copy), and `--remove-key` is destructive — but every failure mode is revealed by the planned tests, so not `hard`. | ANSWERED — autonomous (plan-authoring granularity rule) |
| D6 | Who commits the plan and its task file on the run branch? | The conductor's finalize step commits both together, as the plan inventory policy records; the architect's command set has no git write. | `docs_finalize_approved_plan_and_write_task_file` writes the task file's digest and commits plan plus task file; the dispatch's commit instruction is satisfied by that step, not by the architect. | ANSWERED — autonomous (plan inventory, docs/plans/README.md task-file paragraph) |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute, 2026-11-23 |

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| KyberArbiter | Runbook (docs/kyber-arbiter/runbook.md) | Install and uninstall key origin | install and uninstall derive the endpoint origin from the effective provider (host config plus per-user override); `--remove-key` deletes the override origin's key; a malformed override fails closed on both commands |
| KyberArbiter | Architecture (docs/kyber-arbiter/architecture.md) | Environment-key origin binding | `TYPESAFE_API_KEY` serves only the TypeSafe origin or the per-user override's origin on every key-resolving surface, install and uninstall included; a repository-configured endpoint never widens it |

## Investigation findings

All code reads are from the worktree's own CodeGraph index at revision `f122a2c6`,
branch `opencode/fix/arbiter-install-ignores-override-endpoint` (provenance verified:
`rev=` and `branch=` match the run base). The bug-crusher diagnosis was re-confirmed
against this branch before planning.

- Defect: `ArbiterInstallService.ResolveEndpoint` (src/KyberWeave.Application/Arbiter/ArbiterInstallService.cs:469-478) loads host configuration via `IArbiterConfigResolver.Load` — which applies no override (ArbiterConfigResolver.cs:94-97; only `TryResolve` :66-81 does) — and falls back to `ArbiterProviderConfig.DefaultNone.Endpoint` (`https://api.typesafe.ai/v1`). `Prepare` then derives origin (:91), `needsKey` (:92-93), the stored-key check (:101), the prompt and the `KW-ARB-KEY-001` stop (:107-119), the stored-key comparison (:124) and the plan's endpoint/origin (:251-253) from it.
- Contrast: `ReadExistingProviderKind` (:409-419) applies `userSettings.ApplyTo` and feeds the provider line; `ArbiterStatusCommand.Inspect` (ArbiterStatusCommand.cs:49-58) is override-aware end to end via `configs.TryResolve` + `keys.TryResolveKey`.
- Env-key boundary: `ArbiterKeyResolver.Resolve` (ArbiterKeyResolver.cs:37-65) honors the environment key only when `IsEnvOrigin(origin, userOverrideEndpoint)` (:101-114) — TypeSafe always, any other origin only when the user override names it. `ArbiterKeyService.UserOverrideEndpoint` (ArbiterKeyService.cs:50-68) computes that argument from the override alone and returns null on `YamlException`/file failure. Install's two static calls (:496, :615-618) pass no override endpoint; uninstall never resolves a key.
- Destructive sibling: `ArbiterUninstallCommand.RemoveKey` (ArbiterUninstallCommand.cs:204-248) derives the origin from `configs.Load` alone (falling back to `ArbiterSetupCommand.TypeSafeEndpoint`) and calls `credentials.Delete(origin)`; `IArbiterUserSettings` is not among its constructor dependencies today.
- Intentionally unchanged: `ArbiterSetupCommand` writes the override through `IArbiterUserOverrideWriter` and stores the key for the argv-supplied endpoint (ArbiterSetupCommand.cs:23-38, StoreKey :160), so it reads no host configuration. Override-aware siblings mirrored, not modified: `ArbiterDoctorService.CheckKey` (:357-413), `ArbiterStatusCommand`, `ArbiterServeService.Status` (:256-309), `ConfiguredArbiterProviderFactory`.
- Existing coverage: none fails on this today. Nearest homes to extend: `KyberWeave.Application.Tests.Arbiter.ArbiterInstallServiceSeamsTests` (Prepare seams; its `NoOverride` stub and null `ProviderOption` are why the defect is invisible there), `KyberWeave.Tests.ArbiterInstallCommandSeamTests` (`Install_SystemoneWithoutKey_SurfacesKey001` — the no-override counterpart of the repro), `KyberWeave.Tests.ArbiterUninstallCommandSeamTests` in `tests/KyberWeave.Cli.Tests/ArbiterUninstallCommandSeamTests.cs` — at the test project's root, not in its `Arbiter/` folder — (`Execute_RemoveKey_DeletesTheStoredKeyForTheEndpointOrigin`, verified at line 95), `KyberWeave.Application.Tests.Arbiter.ArbiterKeyResolutionTests` (`EnvKeyHonoredOnlyForUserOverrideOrigin` pins the resolver-level binding).
- Shared fake: `FakeCredentialStore` (`tests/KyberWeave.Testing/Fakes/FakeCredentialStore.cs`) already seeds a key per origin (`Seed`) and records `Reads`, `Writes` and `Deletes`; `KyberWeave.Testing` grants every test project `InternalsVisibleTo`, and both the Cli uninstall tests and Application credential tests already use it. `ArbiterInstallServiceSeamsTests` carries a local `NoCredentials` stub to swap for it. No new fake is needed.
- Symbol locations (corrected per plan review): `ArbiterProviderConfig.DefaultNone` and its pinned endpoint are declared in `src/KyberWeave.Domain/Arbiter/ArbiterConfig.cs:28`, not under `src/KyberWeave.Application/Arbiter/`; the static key resolver (`Resolve`, `IsEnvOrigin`) is in `src/KyberWeave.Application/Arbiter/Credentials/ArbiterKeyResolver.cs:37`.
- Standards: the `csharp` and `test` bundles were fetched through `docs_get_coding_standard_for_technology` with worktree provenance (branch and revision match). On this branch the standard files are `docs/standards/csharp/csharp.standards.md` and `docs/standards/test/test.standards.md`.
- Direct reads (not covered by the indexes used): `docs/plans/README.md`, `docs/catalog.md`, `docs/archive/plans/README.md` and the plans index's git history — needed verbatim for the index-row format and the catalog component names; the kw-docs corpus questions returned confidence 0.00/-0.47, below the answer threshold.

Discovery: codegraph 7, docs 4, direct reads 10 (the plan index, catalog, archived index and plans-index git history for verbatim formats; at plan review, the uninstall seam-test file, `FakeCredentialStore`, the task file, namespace and visibility greps — verifying each review finding against this branch).

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Application.Tests/Arbiter/ArbiterInstallServiceSeamsTests.cs` (extend), `tests/KyberWeave.Cli.Tests/Arbiter/ArbiterInstallCommandSeamTests.cs` (extend), `tests/KyberWeave.Cli.Tests/ArbiterUninstallCommandSeamTests.cs` (extend; at the test project's root, not inside its Arbiter subfolder); fixture: the shared `FakeCredentialStore` (`tests/KyberWeave.Testing/Fakes/FakeCredentialStore.cs` — no new fake) | `dotnet build KyberWeave.sln -c Release && dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ArbiterInstallServiceSeamsTests' && dotnet test tests/KyberWeave.Cli.Tests/KyberWeave.Cli.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ArbiterInstallCommandSeamTests' && dotnet test tests/KyberWeave.Cli.Tests/KyberWeave.Cli.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ArbiterUninstallCommandSeamTests'` (three filtered runs; each must report a non-zero test count — exit 8 means the filter matched nothing) | 1. `Prepare_SystemoneWithOverrideEndpoint_ResolvesTheKeyFromTheOverrideOrigin`: host config `kind: none`, `FakeArbiterUserSettings` with `systemone` + `https://openrouter.ai/api/v1`, credential fake seeded only under `https://openrouter.ai`, `ProviderOption="systemone"`, `DryRun=true`, no key input — exit 0, plan endpoint and origin are the override's, no `KW-ARB-KEY-001`. 2. `Prepare_OverrideEndpoint_ServesTheEnvironmentKeyForTheOverrideOriginOnly`: same override, empty store, env key set — succeeds with the override endpoint/origin (the env remedy line's promise; also pins that `TryResolveKey` receives the un-merged repository config, the input its env-key binding is computed from). 3. `Prepare_RepositoryEndpoint_NeverServesTheEnvironmentKey`: no override, host config names `https://repo.example.net/v1`, env key set, empty store, `--provider systemone` — `KW-ARB-KEY-001` naming that origin (guard: the env key never serves a repository origin). 4. `Prepare_MalformedOverride_StopsWithOneError`: `ApplyTo` throws `YamlException` — exit 1, one `ArbiterInstallError`, null plan (guard: fail closed through the restructure). 5. `Prepare_OmittedProvider_KeepsNoneAndSkipsTheKey`: override present, `ProviderOption` null — provider line `none (kept...)` with the precedence note, no key demanded (guard: D4). 6. `Install_SystemoneWithOverrideEndpointAndStoredKey_CompletesTheDryRun`: the issue's exact repro at the command seam — `--target factory --provider systemone --dry-run`, key stored only under the override origin — exit 0, no `KW-ARB-KEY-001`. 7. `Execute_RemoveKey_DeletesTheKeyForTheOverrideEndpointOrigin`: override endpoint set, keys seeded under both origins — the override origin's key is deleted, the TypeSafe key is untouched, output names the override origin. 8. `Execute_RemoveKey_MalformedOverride_DeletesNothing`: malformed override — error line, no deletion (guard). Left out, with reasons: a loopback override endpoint (the `needsKey`-false path is unchanged logic); a project-scope install variant (the override applies identically on both load paths; one scope keeps the suite inside its budget); a post-`Apply` `Verify` case (its key resolution is the same `TryResolveKey` delegation, whose binding `ArbiterKeyResolutionTests.EnvKeyHonoredOnlyForUserOverrideOrigin` already pins). | Tests 1, 2 (on the endpoint/origin assertions), 6 and 7 fail on the current code: 1 and 6 with exit 2 and `KW-ARB-KEY-001` for `https://api.typesafe.ai`; 7 deletes the TypeSafe key instead. Tests 3, 4, 5 and 8 are guards that are green today and must stay green — 3 and 4 exist so the fix cannot regress the boundary or the fail-closed path. | All eight pass with no weakened assertion, a non-zero test count from each of the three filtered runs, and the guards unchanged. |

## Tasks

### T1: Effective-provider endpoint and key origin in install and uninstall

- **Objective:** Make `arbiter install` and `arbiter uninstall --remove-key` derive the endpoint, its origin and every key lookup from the effective provider (host configuration plus the per-user override), keeping the environment key bound to the TypeSafe origin or the override origin.
- **Capabilities:** C# development in the Application and Cli layers with xUnit tests over the shared in-memory fakes; comfortable with constructor-injected seams and the repository's fake/contract-suite conventions.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `arbiter-effective-endpoint`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Application/Arbiter/ArbiterInstallService.cs`: `ArbiterInstallService` (primary constructor) — add `IArbiterKeyService keys` (already registered; `ArbiterStatusCommand` and `ArbiterDoctorService` consume it). `ReadExistingProviderKind` — generalize to one read returning the loaded repository `KyberWeaveConfig`, the configured kind, and the merged effective provider (for example `ReadExistingProvider`), still called inside `Prepare`'s existing `catch (YamlDotNet.Core.YamlException)` block. `Prepare` — derive `endpoint` from the effective provider (trimmed when non-blank, else the pinned default endpoint declared in `src/KyberWeave.Domain/Arbiter/ArbiterConfig.cs`); delete the second host-config load. `ResolveEndpoint` — remove, superseded by the single effective-provider read. `ResolveStoredKey` — delegate to `keys.TryResolveKey`, passing the endpoint and the un-merged repository configuration the generalized read returned: `ArbiterKeyService.UserOverrideEndpoint` derives the env-key binding by comparing the merged endpoint against the repository's, so passing a merged config would return null and disable the env key for the override origin (tests 2 and 3 pin this). `Verify` — replace the static resolver call, declared in `src/KyberWeave.Application/Arbiter/Credentials/ArbiterKeyResolver.cs`, with `keys.TryResolveKey` over the plan endpoint and the host configuration this method already loads, that configuration being the un-merged repository one.
  - `src/KyberWeave.Cli/Commands/Arbiter/ArbiterUninstallCommand.cs`: `ArbiterUninstallCommand` (primary constructor) — add `IArbiterUserSettings userSettings`. `RemoveKey` — after the existing resolver load of the host configuration, apply the per-user override to the loaded repository provider inside a `catch (YamlDotNet.Core.YamlException)` that prints the escaped message and returns false without deleting; derive the endpoint from the merged provider (trimmed when non-blank, else `ArbiterSetupCommand.TypeSafeEndpoint` as today), then the existing origin derivation, `AnotherTargetIsDeployed` warning and `credentials.Delete(origin)` stay.
  - `tests/KyberWeave.Application.Tests/Arbiter/ArbiterInstallServiceSeamsTests.cs`: extend the `Service` fixture to take an `IArbiterUserSettings` (default the existing `NoOverride`) and `IArbiterKeyService`; swap the local `NoCredentials` stub for the shared `FakeCredentialStore` (`tests/KyberWeave.Testing/Fakes/FakeCredentialStore.cs`, visible to this project — `Seed` per origin, `Reads`/`Writes`/`Deletes` recording; create no new fake); add tests 1–5 from the Test contract; update direct constructions of the service for the new parameter.
  - `tests/KyberWeave.Cli.Tests/Arbiter/ArbiterInstallCommandSeamTests.cs`: add test 6 (the issue's dry-run repro at the command seam).
  - `tests/KyberWeave.Cli.Tests/ArbiterUninstallCommandSeamTests.cs` (class `KyberWeave.Tests.ArbiterUninstallCommandSeamTests`; the file sits at the test project's root, not inside its Arbiter subfolder): add tests 7 and 8 beside the existing `Execute_RemoveKey_DeletesTheStoredKeyForTheEndpointOrigin`; the `Run` fixture constructs the command directly, so it passes the new `IArbiterUserSettings` (a `FakeArbiterUserSettings` or a no-override stub) and seeds `_credentials`, the file's existing `FakeCredentialStore` field.
  - `tests/KyberWeave.Cli.Tests/Arbiter/ArbiterUninstallCommandInMemoryTests.cs` and any other fixture the compiler names: update `ArbiterUninstallCommand`/`ArbiterInstallService` constructions for the new dependencies; existing tests keep their assertions.
- **Commands:**
  - `kyber-weave review gates . --agent <agent> --task T1 -- --red` (after the tests are written, before the implementation)
  - `kyber-weave review gates . --agent <agent> --task T1`
- **Acceptance:**
  - The Test contract's three filtered runs each report a non-zero test count and all eight tests pass with no weakened assertion.
  - The reported scenario — host config `kind: none`, per-user override `systemone` + `https://openrouter.ai/api/v1`, key stored only under `https://openrouter.ai`, `--provider systemone`, dry run — completes with exit 0 and no `KW-ARB-KEY-001` (test 6).
  - A repository-configured endpoint never receives the environment key (test 3 green).
  - A malformed per-user override stops install with one `ArbiterInstallError` (exit 1) and stops `--remove-key` with an error line, deleting nothing (tests 4 and 8 green).
  - The omitted-`--provider` flow's output and key behavior are unchanged (test 5 green).
  - No sibling in the install/uninstall key path reads host configuration alone.
  - Standards to fetch before writing code: the `csharp` and `test` bundles via `docs_get_coding_standard_for_technology`.

## Dependency graph

T1 has no dependencies; it is the plan's only task.

## Risks

| Risk | Mitigation |
|---|---|
| The restructure moves the override application outside `Prepare`'s `YamlException` guard, so a malformed override crashes instead of failing closed | One effective-provider read inside the existing try (D2); guard test 4 fails the moment the guard is lost |
| A transcription of the env-key binding into install passes a repository-derived endpoint where the override endpoint belongs, letting a repository harvest the env key | Delegation to `ArbiterKeyService.TryResolveKey` (D3), no local copy of `UserOverrideEndpoint`, with the un-merged repository config as its input; guard tests 2 and 3 |
| New constructor dependencies ripple into fixtures that construct the two types directly | The compiler enumerates every site; the known fixtures are listed in T1's files |
| The credential fake is duplicated as a local stub instead of the shared fake, breaching the one-fake-per-interface rule | T1 names the shared `FakeCredentialStore` (`Seed`, `Reads`, `Writes`, `Deletes`, `Entries`) and the local `NoCredentials` stub it replaces; no new fake exists to write |

## Out-of-scope boundaries

- `PR note: src/KyberWeave.Cli/Commands/Arbiter/ArbiterSetupCommand.cs` — writes the override itself and takes its endpoint from argv, so it is not a host-config-only reader; intentionally unchanged (D1).
- `PR note: src/KyberWeave.Application/Arbiter/ArbiterDoctorService.cs`, `src/KyberWeave.Cli/Commands/Arbiter/ArbiterStatusCommand.cs`, `src/KyberWeave.Application/Arbiter/Mcp/ArbiterServeService.cs`, `src/KyberWeave.Application/Arbiter/ConfiguredArbiterProviderFactory.cs` — already override-aware; mirrored, not modified.
- `PR note: docs/kyber-arbiter/runbook.md` and `docs/kyber-arbiter/architecture.md` — the operator-visible key-origin behavior lands through this plan's Design harvest at closeout, not in the code task.
- The omitted-`--provider` flow's key prompting (override kind driving `needsKey`) is unchanged by design (D4); changing it is a behavior change the request did not ask for.

## Verification gates

Run in the worktree, after the last plan edit:

- `kyber-weave docs tasks docs/plans/2026-11-23-arbiter-install-effective-provider-endpoint.md --write` then `--check` — the task file is derived, never hand-written.
- `dotnet run --project src/KyberWeave.Cli -- docs validate .` — no `--merge-ready` while the plan is an open Draft.
- `dotnet run --project src/KyberWeave.Cli -- docs drift .`

The code change is proven by T1's task gate running the Test contract's filtered
runner (three classes, two unit projects); CI and the council run the full suite.

## Review

The end-of-run review council governs the change through the `code-review` skill;
the conductor dispatches `code-reviewer`, which derives the command and admittance
from the skill and the change size (quick review at or under 300 changed lines,
full pre-push council above). This plan carries no review command, by the
plan-authoring contract.

## docs-dev closeout

- Harvest the Design harvest table's two rows into `docs/kyber-arbiter/runbook.md` and `docs/kyber-arbiter/architecture.md`.
- Expected ADR waiver: `no ADR — ADR waiver: applies the established per-user-override precedence and the ArbiterKeyResolver origin binding to two more surfaces; no new boundary, dependency or contract`. Record the final call in the closeout mapping.
- Run `kyber-weave docs standards migrate` if the standards warning period (`KW-DOC-SPEC-012`, `KW-DOC-SPEC-013`, `KW-CONFIG-REG-001`, `KW-CONFIG-REG-002`) is flagged, committed with the closeout.
- Archive per the plan inventory policy: closeout mapping complete, then the archive step moves this file to `docs/archive/plans/`, removes the task file and the Active Plans row, and adds the archived row.

## Closeout mapping

| ID | Destination |
|---|---|
| D1 | [Install and uninstall key origin](../../kyber-arbiter/runbook.md#setup-doctor-audit) |
| D2 | [Install and uninstall key origin](../../kyber-arbiter/runbook.md#setup-doctor-audit) |
| D3 | [Environment-key origin binding](../../kyber-arbiter/architecture.md#configuration) |
| D4 | [Install and uninstall key origin](../../kyber-arbiter/runbook.md#setup-doctor-audit) |
| D5 | Documentation waiver — one-task organization was transient execution sequencing with no surviving architectural, operational or behavioral invariant |
| D6 | Documentation waiver — commit mechanics were transient process with no surviving invariant |
| AP1 | Documentation waiver — the approval is recorded in the Decisions table above, which the archive preserves; no separate lasting record is needed |
| KyberArbiter | [Kyber Arbiter runbook](../../kyber-arbiter/runbook.md#setup-doctor-audit) and [Kyber Arbiter architecture](../../kyber-arbiter/architecture.md#configuration) — the two Design harvest rows land in the sections those links name |

no ADR — ADR waiver: applies the established per-user-override precedence and the ArbiterKeyResolver origin binding to two more surfaces; no new boundary, dependency or contract.

## Completion summary (closeout evidence — archive pending)

This plan stays Active/Ready in the inventory until the conductor's archive step moves
it to `docs/archive/plans/`; what follows is the evidence that closeout verified, not a
claim that archival has happened.

T1's change is on this branch as commits `02b59e36` and `abaa9fa`: install derives the
endpoint, its origin and every key lookup from one effective-provider read
(`ReadExistingProvider`), uninstall applies the per-user override in `RemoveKey` and
deletes the credential-store entry for the effective origin directly, and install's key
lookups delegate to `IArbiterKeyService.TryResolveKey` with the un-merged repository
configuration so the environment key serves only the TypeSafe origin or the override's
origin. `Verify` additionally validates the override first, so a file that broke between
planning and verification fails closed with an error naming the override file instead of
surfacing as a missing key. The three filtered Test contract runs report 10, 19 and 9
passing tests with zero failures and non-zero counts; no sibling in the
install/uninstall key path reads host configuration alone (`ArbiterSetupCommand` is
intentionally unchanged: it writes the override and takes its endpoint from argv).
NOT VERIFIED: none — every acceptance criterion is pinned by an automated test; no live
environment, browser or person check applies. The Closeout mapping above is complete and
no further plan edit is expected; the conductor archives next.
