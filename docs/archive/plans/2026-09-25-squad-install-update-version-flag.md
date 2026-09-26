---
id: archive/plans/2026-09-25-squad-install-update-version-flag
title: Add --version pinning to squad install and squad update
doc-type: plan
status: archived
development-mode: test-first
component: KyberSquad
owner: dpalfery
created: 2026-09-25
last-reviewed: 2026-09-25
---

# Add --version pinning to squad install and squad update

**Status:** Archived
**Archive Date:** 2026-09-25
**Date:** 2026-09-25
**Development mode:** test-first (user accepted the default at the intake approval gate, 2026-09-25)
**Delivery:** GitHub issue [dpalfery/kyber-weave#127](https://github.com/dpalfery/kyber-weave/issues/127) — "Add a --version flag to squad install and squad update". The delivering PR must close or reference #127. The issue is already labeled `in-progress` and commented by the conductor; no issue-side action is in scope.
**Goal:** Let `kyber-weave squad install` and `squad update` pin an explicit Kyber-Squad release version via `-v|--version <VERSION>` — normalized and validated client-side before any network call — with a targeted "no Squad release exists at version X" diagnostic when the pinned version has no matching GitHub release, and default behavior preserved byte-for-byte when the flag is omitted. Retires the [squad-install-version-flag todo](../archive/todo/squad-install-version-flag.md) in the same PR.
**Approval:** 2026-09-25 via conductor. The approve-and-execute gate (including the D4 veto question) was presented and drew no user response; the harness directed continuation rather than rejection. The conductor therefore records approval on the strength of the user's standing delivery directive for #127 ("fix this issue in a new feature pr") and the four answered intake decisions (§3). D4: no veto received — the archive-move reading stands.

---

## 1. Problem / Motivation

`squad install` and `squad update` can only deploy whatever Squad release matches the
*running CLI binary's own* assembly version — there is no way to pin a version from the
command line. A dev build (`0.1.0+<commit-sha>`) corresponds to no real GitHub release tag,
so the only way to prove a renderer end-to-end from a dev binary was to publish a throwaway
binary stamped with a real released version ([the todo](../archive/todo/squad-install-version-flag.md),
found while verifying the Copilot renderer).

Two aggravations, both decided at intake:

- The failure for a non-existent version is the raw GitHub 404 verbatim — correct, but not
  actionable for a user unfamiliar with the release flow (decision D3: targeted diagnostic).
- A nonexistent-vs-mistyped version must not be conflated: input that is not a release tag
  at all is client error and must be rejected before any network call with the squad
  commands' exit-2 client-input convention.

## 2. Investigation findings

All facts verified against source on `main` at `9a2eea7`, 2026-09-25.

- **The domain model is ready.** `SquadInstallRequest.Version`
  ([src/KyberWeave.Core/Squad/Deployment/SquadLifecycleService.cs:16](../../src/KyberWeave.Core/Squad/Deployment/SquadLifecycleService.cs))
  and `SquadUpdateRequest.Version` (line 29) are both `string? Version = null`; both
  `InstallAsync` (line 124) and `UpdateAsync` (line 263) already resolve
  `request.Version ?? ResolveDefaultVersion()`. Both requests default
  `Repository = "dpalfery/kyber-weave"` (lines 21, 34). Nothing downstream of the requests
  changes.
- **The commands simply never wire it.** `SquadInstallCommand.Execute` builds its request
  without `Version` at
  [src/KyberWeave.Cli/Commands/Squad/SquadInstallCommand.cs:130](../../src/KyberWeave.Cli/Commands/Squad/SquadInstallCommand.cs);
  `SquadUpdateCommand.Execute` likewise at
  [src/KyberWeave.Cli/Commands/Squad/SquadUpdateCommand.cs:139](../../src/KyberWeave.Cli/Commands/Squad/SquadUpdateCommand.cs).
- **The settings classes have no Version option.** `SquadInstallSettings` and
  `SquadUpdateSettings` ([src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs:8-105](../../src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs))
  carry `-t`, `-x`, `-g`, `--path`, `--dry-run`, `--yes` (install also `--adopt`; update also
  `--replace-managed`). `SquadPackSettings` in the same file already carries the exact
  template: `[CommandOption("-v|--version <VERSION>")] public string? Version` (lines 188-191).
  No `-v` short flag exists on install/update settings, so no conflict.
- **The normalization helper exists and is already reused inside the Squad family.**
  `ReleaseVersion.Normalize`
  ([src/KyberWeave.Cli/Update/ReleaseVersion.cs:8](../../src/KyberWeave.Cli/Update/ReleaseVersion.cs))
  strips a leading `v`/`V` (lines 13-14) and `+build` metadata (lines 16-19), rejects
  empty-after-`v` (lines 20-21) and path-fragment-shaped input (lines 23-29), throwing
  `SelfUpdateException` ("version is empty…", "refusing version 'X': it is not a Release
  tag."). `SquadPackCommand` already routes its version through it
  ([src/KyberWeave.Cli/Commands/Squad/SquadPackCommand.cs:61-63](../../src/KyberWeave.Cli/Commands/Squad/SquadPackCommand.cs)).
  One correction to the issue's precedent claim: `kyber-weave update` takes its version as a
  positional `[version]` argument (`UpdateSettings`), and normalization happens inside
  `SelfUpdater`, not `UpdateCommand.Execute` — the precedent is real, but `squad pack` is the
  closer template.
- **The release source has a stricter, earlier gate.**
  `GitHubSquadReleaseSource.ValidateRequest`
  ([src/KyberWeave.Cli/Commands/Squad/Infrastructure/GitHubSquadReleaseSource.cs:150-170](../../src/KyberWeave.Cli/Commands/Squad/Infrastructure/GitHubSquadReleaseSource.cs))
  rejects any version failing the strict SemVer regex (lines 172-175: `X.Y.Z` or
  `X.Y.Z-prerelease`, no leading zeros, **no build metadata, no short forms**) with
  `ArgumentException` before any network call — but *after* the command layer has built the
  request, so today it would surface through the commands' generic catch as exit 1.
  `Normalize` is looser (it accepts `1.0`), so the two rules differ; the command layer must
  apply both, in order, to keep every invalid pinned version on one exit code (§4 V2).
- **The 404 path today.** `DownloadAndExtractAsync` (line 74) builds
  `releases/tags/v{version}` (`BuildReleaseUri`, line 180) and fetches via `ReadReleaseAsync`
  (line 190) → `SendHttpsAsync`, whose `EnsureSuccessStatusCode` (line 231) turns a 404 into a
  raw `HttpRequestException`. The commands' generic `catch (Exception)` prints `ex.Message`
  and returns 1. Precedent for a targeted diagnostic in this exact class: the asset-missing
  path throws `InvalidDataException` with a message that says what the state means and what
  to do ("Release '…' ships no Squad bundle. Squad is versioned with the CLI, so move to a
  release that carries one with 'kyber-weave update'.", lines 94-106).
- **Exit-code conventions.** Both commands already have a client-input try/catch that maps
  `ArgumentException` → `SquadCommandComposition.WriteClientInputError` → exit 2
  (`SquadInstallCommand.cs:61-76`, `SquadUpdateCommand.cs:62-77`), and a trailing generic
  catch → exit 1 (`SquadInstallCommand.cs:187-191`, `SquadUpdateCommand.cs:204-208`).
  `Install_WhenToolchainUnreleased_FailsClosedWithExitOne`
  ([tests/KyberWeave.Tests/SquadCliCommandTests.cs:748](../../tests/KyberWeave.Tests/SquadCliCommandTests.cs))
  pins the fail-closed exit-1 convention for release-side failures.
- **Strict parsing is on.** `Program.cs:23` calls `UseStrictParsing()`, so today
  `squad install --version 1.2.3` is a hard parse error ("unknown option") — the new option
  is a purely additive surface; no silent behavior changes for existing callers.
- **Test seams cover the surface.** The commands' internal constructors accept injectable
  collaborators; `FakeSquadReleaseSource`
  ([tests/KyberWeave.Tests/Fakes/FakeSquadReleaseSource.cs:10-13](../../tests/KyberWeave.Tests/Fakes/FakeSquadReleaseSource.cs))
  records every `SquadReleaseRequest`, so the pinned version reaching the release source is
  directly assertable. `SquadReleaseClientTests` has a `RoutingHandler`/`ReleaseHandler`
  stub-HTTP harness and the existing invalid-version-rejected-before-HTTP theory (lines
  97-122). `UpdateCommandTests` pins `ReleaseVersionNormalize*` behavior (lines 92-101).
- **Docs surfaces.** [docs/kyber-squad/onboarding.md:32,35](../kyber-squad/onboarding.md)
  carry explicit option synopses for `squad install` and `squad update` that must gain the
  flag. `Program.cs` registers `WithExample` rows for both verbs (lines 140-154).
- **D2 verified.** There is no forward/backward guard anywhere in `UpdateAsync` —
  `request.Version ?? ResolveDefaultVersion()` is the only version logic. Nothing to
  remove; the unguarded behavior is accepted and documented (decision D2).
- **D4 verified.** [docs/todo/squad-install-version-flag.md](../archive/todo/squad-install-version-flag.md)
  still exists and is Open row 4 in [docs/todo/README.md:52](../todo/README.md), despite the
  issue text claiming migration. It is retired by this plan's delivering PR.

## 3. Approved decisions

Provenance: conductor-relayed user decisions recorded at the intake approval gate,
2026-09-25. All four are user-resolved and are not reopened by this plan.

- **D1 (artifact = implementation plan).** This document; plan path selected by the user.
- **D2 (no forward/backward guard on `squad update --version`).** NO guard, prompt, or
  block. Pinning forward or backward relative to the receipt's recorded version is accepted
  behavior. It is recorded as a documented, accepted behavior in the aligned docs (T4), not
  as a check in code.
- **D3 (clearer diagnostic for a version with no matching release).** A targeted
  "no Squad release exists at version X" style message replaces the raw GitHub 404 verbatim,
  with test coverage. Because `GitHubSquadReleaseSource.ValidateRequest` already rejects
  non-SemVer before any network call, the plan must sequence `ReleaseVersion.Normalize`
  relative to that gate and specify which failure surfaces which message and exit code —
  settled in §4 V2 and the exit-code matrix.
- **D4 (retire the source todo in the delivering PR).** Delete the todo from the active
  inventory and its index row per the todo index's own mechanics (T4): the todo moves to
  `docs/archive/todo/` with `status: superseded`, the Open row is removed, a Closed row
  records provenance, and `docs validate` + `docs drift` are re-run. The todo index's own
  retirement mechanics are the archive-move plus Closed row — every retirement in
  [docs/todo/README.md](../todo/README.md) follows it, and it is what keeps provenance
  reachable; a literal filesystem deletion with no Closed row would diverge from that
  convention. This reading of "delete … per the todo index's own mechanics" is recorded
  here so the approval gate can veto it if literal deletion was meant.
- **development-mode: test-first.** User did not opt out; this plan carries the Test
  contract in §7.

## 4. Architect-settled definitions

Settled under D3's mandate and the plan author's technical-design mandate. These are
implementation details with no remaining user visibility; they follow existing conventions
cited in §2.

- **V1 (option shape).** `[CommandOption("-v|--version <VERSION>")] public string? Version`
  on `SquadInstallSettings` and `SquadUpdateSettings`, mirroring `SquadPackSettings`
  (same file, lines 188-191). Description: pinning semantics plus the default, e.g.
  "Pin the Kyber-Squad release version to deploy (X.Y.Z or X.Y.Z-prerelease; a leading 'v'
  and '+build' metadata are accepted and stripped). Defaults to the running CLI's own
  version." Help text comes from the attribute; no other registration is needed.
- **V2 (normalize-and-validate at the command boundary, exit 2).** New helper
  `SquadCommandComposition.NormalizePinnedVersion(string? version)` → `string?`:
  returns `null` for null/whitespace (default preserved); otherwise runs
  `ReleaseVersion.Normalize` (strips `v`/`+build`, rejects tag-shaped garbage with
  `SelfUpdateException`), then the strict SemVer check by reusing
  `GitHubSquadReleaseSource.IsValidReleaseVersion` (visibility `private` → `internal`; the
  one rule, reused, no regex duplication), throwing
  `ArgumentException("--version must be a semantic version (X.Y.Z or X.Y.Z-prerelease); a
  leading 'v' and '+build' metadata are accepted and stripped. Got '<value>'.")` for forms
  like `1.0` or `01.2.3`. Both commands call it inside their existing client-input try
  block (before root resolution, target parsing order preserved), and that block's catch is
  widened from `catch (ArgumentException)` to
  `catch (Exception ex) when (ex is ArgumentException or SelfUpdateException)` →
  `WriteClientInputError` → exit 2. Sequencing relative to the D3 gap:
  `Normalize` first (it is looser and canonicalizes), strict SemVer second, and
  `GitHubSquadReleaseSource.ValidateRequest` stays as defense-in-depth, unchanged, still
  rejecting before any HTTP call — its existing client tests are untouched. Because
  `Normalize` strips `+build` metadata, `v1.2.3+sha` reaches the release source as valid
  `1.2.3` rather than being rejected by its stricter rule.
- **V3 (request wiring).** `Version: pinnedVersion` is added to the named-argument request
  constructions (`SquadInstallCommand.cs:130-136`, `SquadUpdateCommand.cs:139-145`).
  `null` flows through `request.Version ?? ResolveDefaultVersion()` unchanged — the
  non-breaking default for every existing caller and test.
- **V4 (D3 diagnostic, exit 1).** In `GitHubSquadReleaseSource.DownloadAndExtractAsync`,
  the `ReadReleaseAsync` call is wrapped: `catch (HttpRequestException ex) when
  (ex.StatusCode == HttpStatusCode.NotFound)` → throw `InvalidDataException` naming the
  requested version and repository and pointing at the releases page, e.g.
  "No Kyber-Squad release exists at version '9.9.9' in 'dpalfery/kyber-weave'. Squad is
  released with the CLI under 'v<version>' tags; see
  https://github.com/dpalfery/kyber-weave/releases for available versions." (exact wording
  is implementation's, pinned by test to the shape: version + repository + releases-page
  hint). `net10.0` guarantees `HttpRequestException.StatusCode`. Non-404 failures keep the
  raw `HttpRequestException` (guard test). The commands' existing generic catch prints the
  message and returns 1, matching the asset-missing precedent (§2).
- **V5 (D2 documented acceptance).** No guard. `squad update --version` may pin forward or
  backward relative to the receipt's recorded version; `squad install` over an existing
  deployment already guards target-set equality, not version. The onboarding doc's update
  section gains one sentence stating the accepted behavior.
- **V6 (examples).** Additive `WithExample` rows on the two verb registrations in
  `Program.cs` (install: `--version` with a target; update: `--version`), following the
  additive-example convention used for `--path`.

### Exit-code matrix (the D3 sequencing answer)

| `--version` input | Rejected where | Exception | User surface | Exit |
|---|---|---|---|---|
| `../evil`, `0.2.0/evil`, `v` (not a release tag) | Command client-input block: `ReleaseVersion.Normalize` | `SelfUpdateException` (existing message) | `WriteClientInputError` red error line | 2 |
| `1.0`, `01.2.3`, `1.2.3-01` (tag-shaped, not strict SemVer) | Command client-input block: strict SemVer check (V2) | `ArgumentException` (new flag-targeted message) | `WriteClientInputError` red error line | 2 |
| Valid SemVer with no matching GitHub release | `GitHubSquadReleaseSource`, on 404 from the release-tag GET (V4) | `InvalidDataException` (targeted message) | generic catch red error line | 1 |
| Omitted | — | — | default version resolution, unchanged | unchanged |

All exit-2 rejections happen before root resolution and before any network call; the
release request is never issued (asserted by an empty `FakeSquadReleaseSource.Requests`).

## 6. Scope

**In:** `SquadInstallSettings`/`SquadUpdateSettings` (`-v|--version`); the
`SquadCommandComposition.NormalizePinnedVersion` helper; `IsValidReleaseVersion` visibility;
the widened client-input catch and `Version:` wiring in both commands; the 404 diagnostic in
`GitHubSquadReleaseSource`; additive `WithExample` rows in `Program.cs`; the new
`SquadVersionOptionTests` contract suite and new rows in `SquadReleaseClientTests`;
onboarding-doc alignment including the D2 sentence; retirement of the
`squad-install-version-flag` todo; docs and gate verification.

**Out:** `squad pack` (already has the flag); the self-updater and `kyber-weave update`
positional version; any lifecycle-service changes (`InstallAsync`/`UpdateAsync` already
support `Version`); any new guard, prompt, or block on update pinning (D2); the MCP server;
KyberDash and `dash/`; skill artifacts under `.apm/skills/` (skill gates unaffected);
renegotiating the default version source.

## 7. Test contract (development-mode: test-first)

Runner for all rows: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1a wiring | `tests/KyberWeave.Tests/SquadVersionOptionTests.cs` (new) | `dotnet test … --filter "FullyQualifiedName~SquadVersionOption"` | Install and update with `Version = "v1.2.3+dev"` pin `FakeSquadReleaseSource.Requests.Single().Version == "1.2.3"`, exit 0; omitting the flag yields the assembly informational version with metadata stripped (computed in-test from the attribute) | Compile failure: `SquadInstallSettings`/`SquadUpdateSettings` carry no `Version` — captured as the RED record | Same tests pass, no weakened assertions |
| T1b invalid input | same file | same filter | Theory over `["../evil", "0.2.0/evil", "v", "1.0", "01.2.3", "1.2.3-01"]` for both verbs: exit 2, red error line names the flag or offending value, `FakeSquadReleaseSource.Requests` empty (no network call), zero filesystem writes | Same compile failure (settings property absent) | Same theory green for both verbs; existing suite green |
| T1c missing release | `SquadVersionOptionTests.cs` + rows in `tests/KyberWeave.Tests/SquadReleaseClientTests.cs` | same filter, plus `--filter "FullyQualifiedName~SquadReleaseClient"` | Client: stub 404 on `releases/tags/v9.9.9` → `InvalidDataException` whose message contains `9.9.9`, the repository, and `releases`; destination tree unchanged; exactly one HTTP request. Non-404 (500) still throws raw `HttpRequestException` (guard). Command: install with injected 404-stubbed source and `Version = "9.9.9"` → exit 1, output carries the targeted message | Compile failure for the settings property; client rows fail with `HttpRequestException`/assertion mismatch instead of `InvalidDataException` — captured | Both surfaces green; guard row green without swallowing non-404 failures |
| T2–T3 GREEN | same files | full `dotnet test` | §4 implemented | T1 RED captured before any source edit | Full suite green, including `SquadCliCommandTests`, `SquadPathSafetyTests`, `SquadLifecycleTests`, `SquadReleaseClientTests` pre-existing rows |
| T4 docs/todo | No test task | `docs validate .` + `docs drift .` in T5 gates | Onboarding synopses carry the flag; D2 sentence present; todo retired per index mechanics | Marked no-test: governed-docs edits verified by the T5 gates | Zero findings both checks |
| T5 gates | No test task | gate suite in §11 | All declared gates green | Marked no-test: gates replace it | Gate evidence attached to closeout |

Sequencing: T1 is the single RED wave (both test surfaces, one authoring task) preceding
both GREEN scopes, because the contract suite references seams from T2 and T3 and shared
test files cannot be safely co-edited concurrently.

## 8. Tasks

- **T1 — Author the RED contract suite.**
  **id**: T1-red-squad-version-option
  **specialist**: `test-dev`
  **scope**: `tests/KyberWeave.Tests/SquadVersionOptionTests.cs` (new);
  `tests/KyberWeave.Tests/SquadReleaseClientTests.cs` (additive rows only)
  **depends-on**: none
  **concurrency**: first task; blocks T2, T3
  **objective**: create the §7 T1a/T1b/T1c tests. Command tests use the internal
  constructors with `FakeSquadReleaseSource`/`FakeSquadRenderer`/`FakeUserPaths`
  (the `Install_WhenCollaboratorsInjected_SucceedsWithExitZero` pattern), an update-path
  test seeds a deployment first; the 404 command test injects a `GitHubSquadReleaseSource`
  over its own stub `HttpMessageHandler` returning 404 (loopback-safe `api.github.test`
  root, as `SquadReleaseClientTests` does).
  **acceptance**: RED run evidence captured per §7 (compile failure text for the absent
  settings property; the client-row failure mode). No assertion weakened later.
- **T2 — GREEN scope A: option, normalization, wiring, examples.**
  **id**: T2-green-settings-and-wiring
  **specialist**: `csharp-dev`
  **scope**: `src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs`;
  `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`;
  `src/KyberWeave.Cli/Commands/Squad/SquadInstallCommand.cs`;
  `src/KyberWeave.Cli/Commands/Squad/SquadUpdateCommand.cs`;
  `src/KyberWeave.Cli/Commands/Squad/Infrastructure/GitHubSquadReleaseSource.cs`
  (visibility of `IsValidReleaseVersion` only); `src/KyberWeave.Cli/Program.cs` (examples)
  **depends-on**: T1
  **concurrency**: after T1; shares `SquadSettings.cs` and both command files with no other
  task, but must not run concurrently with T3 (shared solution build/test tree)
  **objective**: implement V1, V2, V3, V6 exactly.
  **acceptance**: §7 T1a/T1b green; full suite green; no new analyzer warnings
  (`TreatWarningsAsErrors`); `dotnet format` clean.
- **T3 — GREEN scope B: the D3 diagnostic.**
  **id**: T3-green-release-not-found-diagnostic
  **specialist**: `csharp-dev`
  **scope**: `src/KyberWeave.Cli/Commands/Squad/Infrastructure/GitHubSquadReleaseSource.cs`
  **depends-on**: T1 (RED evidence); after T2 if serialized
  **concurrency**: disjoint source files from T2, but shared build tree — run sequentially
  **objective**: implement V4 (404 → targeted `InvalidDataException`; non-404 untouched).
  **acceptance**: §7 T1c green including the non-404 guard; full suite green.
- **T4 — Docs alignment and todo retirement (D4).**
  **id**: T4-docs-and-todo-retirement
  **specialist**: `kyber-weave-docs`
  **scope**: `docs/kyber-squad/onboarding.md`; `docs/todo/squad-install-version-flag.md` →
  `docs/archive/todo/squad-install-version-flag.md`; `docs/todo/README.md`
  **depends-on**: T2 (final flag shape and wording), T3 (final diagnostic wording)
  **concurrency**: after T3; blocks T5
  **objective**: (a) add `[--version <VERSION>]` to the install/update synopses
  (onboarding.md:32,35) and a short pinning paragraph — including the V5 sentence that
  update may pin forward or backward with no guard (D2) and that an unknown version fails
  with exit 1; (b) retire the todo per the index's mechanics: move the file to
  `docs/archive/todo/`, set frontmatter `id: archive/todo/squad-install-version-flag` and
  `status: superseded` (last-reviewed 2026-09-25), remove Open row 4, add a Closed row
  linking this plan and issue #127.
  **acceptance**: todo reachable only from the Closed table; `docs validate .` zero
  findings; `docs drift .` zero findings.
- **T5 — Gates, review, closeout.**
  **id**: T5-gates-review-closeout
  **specialist**: `code-review`, `kyber-weave-docs`
  **scope**: repository root (gate runs); this plan and index at closeout
  **depends-on**: T4
  **concurrency**: final task
  **objective**: run the §11 verification contract; conduct the repo review path over the
  diff (`review gates .` plus the `code-review` skill, InspectCode triaged by
  `static-analysis-triage`); on approval, close out per §13.
  **acceptance**: all gates green; review verdict recorded; closeout executed.

## 9. Dependency graph and concurrency audit

T1 → T2 → T3 → T4 → T5 (a strict chain).

MAX_CONCURRENCY: **1**. T2 and T3 have disjoint source-file scopes, but every task runs
`dotnet test` against the same working tree and build outputs, and T1's contract suite is
shared evidence — no two tasks here can safely run in parallel. File-scope overlaps to
respect if the chain is ever re-ordered: `SquadSettings.cs` and both command files are
T2-only; `GitHubSquadReleaseSource.cs` is touched by T2 (visibility) and T3 (diagnostic) —
one more reason they serialize.

## 10. Risks

- **Exit-code split by failure locus.** Malformed values exit 2 (client input) while
  well-formed-but-unknown versions exit 1 (release state). This is deliberate — it matches
  the commands' existing conventions — and is pinned by the §7 theories so it cannot drift.
- **`SelfUpdateException` crossing subsystems.** The squad commands will catch an exception
  type named for the self-updater. It is the type `ReleaseVersion.Normalize` throws, the
  helper is already used by `SquadPackCommand`, and the alternative (re-deriving validation)
  duplicates a rule that must not diverge. If review prefers a squad-named wrapper, that is
  a mechanical rename inside T2 — not a contract change.
- **Message wording churn.** Tests pin message *shape* (contains version, repository,
  releases-page hint), not full sentences, so wording can be polished without breaking the
  contract.
- **404 semantics.** GitHub returns 404 for a missing tag, a private repo, and rate-limit
  edge cases; the targeted message says "no release exists at version X", which could be
  wrong for an auth/rate failure. Accepted: the repository is public, the loop-loop and CI
  contexts never hit the private case, and the message still names the exact version and
  releases page. The non-404 guard keeps genuine server errors on the raw surface.
- **Docs drift gate is environmental.** `docs drift` needs the host CodeGraph index; if the
  local run reports `KW-DOC-DRIFT-001` for that reason only, CI re-verifies at PR time (the
  same allowance the squad-path-safety review took).

## 11. Verification gates

Per repository `AGENTS.md`, on the delivering branch, after T3 (T4/T5 add the docs
re-runs):

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
./scripts/update-loop.sh --no-kyberdash
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- review gates . --out artifacts/gates.json
```

The release loop is required because this change touches the Squad release path's client
half — `distribution.md:132` names client-side `squad install`/`update` as part of it, and
`distribution.md:225` mandates the loop for changes there; `--no-kyberdash` scopes it to
the publish → self-update → `squad install` cases this change can affect. The three `skill`
gates are covered by `review gates .`; no skill artifact is edited, so no direct skill-gate
work is scoped.

Final PR-time check (the plan must be archived by the PR that finishes it — §13):

```bash
dotnet run --project src/KyberWeave.Cli -c Release -- docs validate . --merge-ready
```

## 12. Review

Review via the repo's review path after T4: `review gates .` green plus the `code-review`
skill over the diff, with `static-analysis-triage` attributing InspectCode findings to the
change. The delivering PR closes or references #127.

## 13. Docs-dev closeout

- This plan is registered in [docs/plans/README.md](README.md) (Active Plans) with
  development mode `test-first`, status Draft.
- On finalize (conductor-relayed approval): body Status Draft → Ready, frontmatter
  `status: draft` → `current`, index Status → Ready, §5 ledger removed, both docs checks
  re-run.
- On completion (the delivering PR): archive this plan to
  `docs/archive/plans/2026-09-25-squad-install-update-version-flag.md`, move the index row
  Active → Archived with harvest notes (expected: the onboarding CLI section; no ADR — the
  decisions are user choices recorded here and in the aligned doc), confirm the todo's
  Closed row names the PR, and pass `docs validate --merge-ready`.
- Durable fact worth harvesting into [docs/kyber-squad/onboarding.md](../kyber-squad/onboarding.md):
  version pinning semantics, the exit-code split (§4 matrix), and the D2 accepted
  unguarded update pinning.

## Related

- [Todo: squad install version flag](../archive/todo/squad-install-version-flag.md) (retired by this plan)
- [Issue #127](https://github.com/dpalfery/kyber-weave/issues/127) — delivery target
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md) · [architecture](../kyber-squad/architecture.md)
- [Plan: squad path argument safety](../archive/plans/2026-09-23-squad-path-argument-safety.md) — the option/catch-pattern precedent reused in V2
- [Plan: agent-spec broken reference rule](../archive/plans/2026-09-24-agent-spec-broken-reference-rule.md) — plan-convention reference
