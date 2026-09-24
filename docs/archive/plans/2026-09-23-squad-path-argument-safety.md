---
id: archive/plans/2026-09-23-squad-path-argument-safety
title: Make squad target-root selection explicit and confirmed
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-23
development-mode: test-first
---

# Make squad target-root selection explicit and confirmed

**Status:** Archived  
**Archive Date:** 2026-09-24  
**Completion:** Complete — council position APPROVE-quality; the engine token is REQUEST_CHANGES solely on the environmental docs-drift gate (CI verifies drift at PR time); 14/15 gates PASS, full suite deterministic; delivered via PR #113 on 2026-09-24. See §11 Review.  
**Date:** 2026-09-23
**Development mode:** test-first (user accepted the default via the conductor, 2026-09-23)
**Goal:** Make it impossible for a squad mutating command to write to a silently-guessed
directory: an explicit `--path` option joins the positional argument, unrecognized options
become hard parse errors CLI-wide, and mutating squad commands echo and interactively
confirm the resolved absolute target root before any write.

**Approval:** 2026-09-23 via conductor — user approved and directed execution (verbatim: "i approve the plan, execute once docs dev is done").

---

## 1. Problem / Motivation

`kyber-weave squad install --target copilot --dry-run --path <scratch-dir>` was run
expecting `--path` to redirect the deployment root. It did not: `--path` is not a
registered option, Spectre.Console.Cli's default relaxed parsing silently ignored it, the
positional `Path` stayed at its `"."` default, and the deployment root resolved to the
current working directory — this repository's own root. A subsequent non-dry-run call
wrote 43 Copilot files plus `.kyber-weave/squad.lock.yml` and `squad.receipt.json` into
the repository before being caught via `git status`. Full account in
[the todo](../todo/squad-path-argument-safety.md).

Three independent failures compound: the plausible flag guess is silently accepted
(parser), nothing shows the operator where the write is headed before it happens (echo),
and there is no chance to abort once it is visible (confirmation).

## 2. Investigation findings

All facts verified against source on `feature/squad-path-argument-safety`, 2026-09-23.

- **Positional-only path.** `Path` on `SquadInstallSettings`, `SquadUpdateSettings`,
  `SquadUninstallSettings`, `SquadStatusSettings`, `SquadDoctorSettings`
  (`src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs`) is
  `[CommandArgument(0, "[path]")]` with default `"."`. No `--path` option exists anywhere
  in the file. `SquadPackSettings` has no path argument at all (it uses `--out`).
- **Relaxed parsing is in effect.** `src/KyberWeave.Cli/Program.cs` constructs
  `new CommandApp()` and configures five branches with no `UseStrictParsing()` call.
  Spectre.Console.Cli's default parsing mode is relaxed: unrecognized options do not
  produce a parse error. This matches the observed clean-success behavior exactly. Task
  T1 pins the empirical behavior on this branch before the fix lands.
- **Single resolution seam.** `SquadCommandComposition.ResolveTargetRoot(string?)`
  (`src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs:126`) is the one place
  every squad command turns `settings.Path` into an absolute root, defaulting
  null/whitespace to `"."` — i.e. the current working directory.
- **Package pins.** `src/KyberWeave.Cli/KyberWeave.Cli.csproj` pins
  `Spectre.Console.Cli` 0.55.0 and `Spectre.Console` 0.57.2 (intake gap 2a, resolved).
- **Test coverage.** `tests/KyberWeave.Tests/SquadCliCommandTests.cs` exercises squad
  commands by direct `Execute` invocation with hand-constructed settings — there are no
  parser-level (`CommandApp`) tests anywhere in the suite, so strict parsing cannot break
  existing tests, and `--path` binding has no existing coverage. One test asserts
  Program.cs textually (`ProgramRegistersSquadBranchWithAllVerbsDescriptionsAndExamples`),
  which T3 extends additively with a `UseStrictParsing` assertion (intake gap 2b,
  resolved).
- **Interactive precedent.** `SquadCommandComposition.IsInteractiveConsole()` exists and
  install/update already feed it into `SquadTargetResolutionRequest.IsInteractive`
  (interactive target selection prompts; non-interactive exits 2 with a recovery
  command). The confirmation follows the same seam and the same exit-code convention.
- **Automation consumers.** `scripts/update-loop.sh:255,312` invokes
  `kyber-weave update <version>` and `kyber-weave squad install --target copilot` — only
  declared options, and non-interactive. Strict parsing does not break them, and the
  confirmation's non-interactive path (N1 below) must not block the loop.
- **Constructor pattern.** The three mutating commands each expose an internal
  constructor with nullable optional collaborators — the established extension point for
  the `isInteractive`/`readAnswer` seams the confirmation tests need.

### Addendum — T1 empirical pin (2026-09-23, post-finalize evidence)

Empirical confirmation of the relaxed-parsing behavior on this branch, via `squad install --path /tmp/squad-path-safety-smoke --target copilot --dry-run` and controls (exit 1 both, stdout byte-identical):

- The unrecognized `--path <dir>` pair is silently discarded entire — flag and value; the positional `[path]` stays at its `"."` default and the deployment root resolves to the current working directory.
- Neither install run printed a root: the release fetch 404s (`releases/tags/v<assembly version>`, `GitHubSquadReleaseSource`) after root resolution but before the success-path root echo (`SquadInstallCommand.cs:119`).
- Triangulation via `squad status` (same settings shape, names its root): `--path <dir>` → repo CWD, identical to bare `squad status`; explicit positional `<dir>` → "No Kyber-Squad deployment found at <dir>" (probe detects positional binding, so the value was dropped, not re-bound); `--totally-unknown-flag` → dropped silently, no parse error.
- Test-design consequence: a real `squad install` on this branch never reaches the root-printing line (404 first), so root assertions must stub the release source or use a root-echoing surface. The T3 contract suite was authored against this constraint.

## 3. Approved decisions

Provenance: conductor-relayed user decisions, 2026-09-23, recorded verbatim. Not reopened.

- **D1 (fix-locus = BOTH).** "(a) add an explicit `--path` option bound to the existing
  Path property on the squad settings classes in src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs,
  and (b) enable strict parsing CLI-wide (`UseStrictParsing()` on the CommandApp
  configuration in src/KyberWeave.Cli/Program.cs) so unrecognized options become hard
  parse errors on every command."
- **D2 (echo-scope = INTERACTIVE CONFIRMATION).** "Mutating squad commands (install,
  update, uninstall without --dry-run) confirm the resolved absolute target root before
  any write when the console is interactive; the plan must explicitly define the
  non-interactive-console behavior and any scripting/automation escape hatch — that
  definition is your technical design to settle and record."

### Architect-settled definitions (under D2's delegation and the technical-design mandate)

- **N1 (non-interactive = echo and proceed).** When the console is non-interactive, the
  command prints the resolved absolute root and scope, then proceeds without prompting
  and without requiring a flag. A scripted caller is deterministic by construction — the
  hazard D2 addresses is a human guessing a flag interactively — and `update-loop.sh`
  must keep working unattended. The echo still lands in CI logs, which is the
  after-the-fact audit trail that was missing in the incident.
- **N2 (`--yes` escape hatch).** A long-only `--yes` option on `squad install`, `update`,
  and `uninstall` skips the interactive prompt. This is for automation running under a
  pty (expect scripts, CI with a tty), which would otherwise hang on the prompt. It has
  no effect on the non-interactive path, which never prompts.
- **N3 (decline is exit 2).** Declining the confirmation prints
  `Declined. No changes were made.` and exits 2 — the commands' existing exit-2
  convention for aborted-before-side-effects client input (unknown target tokens,
  no-target-in-non-interactive).
- **N4 (echo contract).** Every non-dry-run mutating run prints the resolved absolute
  target root and scope (project/global) before any write, in every mode. Dry-run output
  already names the root and gains nothing from a prompt.
- **N5 (placement).** The confirmation runs after token validation and target
  resolution (so it can name the scope, and after any existing target-selection prompt)
  and immediately before the lifecycle mutation call — `InstallAsync`, `UpdateAsync`,
  `UninstallAsync` are the only side-effecting steps in these commands; config load and
  receipt reads that precede it are read-only.
- **N6 (`--path` shape and conflict rule).** Long-only `--path <PATH>` (matching
  `--dry-run`/`--adopt` style; no new short flag). Spectre.Console.Cli does not reliably
  support one property carrying both `[CommandArgument]` and `[CommandOption]`, so the
  option binds a separate nullable `PathOption` property coalesced with the positional
  through a new `SquadCommandComposition.CoalesceTargetPath(positional, option)`. Supplying
  both a non-default positional and `--path` is a conflict: error with a hint naming both
  forms, exit 2 (the token-validation catch pattern). `--path` with the positional at its
  `"."` default is the normal case; `--path` wins. `status` and `doctor` (read-only) get
  `--path` for consistency but no confirmation and no `--yes`.
- **N7 (prompt is injectable).** The confirmation lives in one helper,
  `SquadCommandComposition`-adjacent under `Commands/Squad/Infrastructure/`, taking the
  resolved root, scope, verb, interactivity flag, `--yes`, and a `readAnswer` delegate.
  The mutating commands' internal constructors gain optional `isInteractive` and
  `readAnswer` parameters defaulting to `IsInteractiveConsole()` and real console input —
  the existing collaborator-injection pattern — so both branches are deterministically
  testable.

## 5. Scope

**In:** the five squad settings classes (`--path`; `--yes` on the three mutating ones);
`SquadCommandComposition.CoalesceTargetRoot` coalescing; `UseStrictParsing()` in
Program.cs plus squad-branch example updates; the confirmation helper and its wiring in
`SquadInstallCommand`, `SquadUpdateCommand`, `SquadUninstallCommand`; the new
`SquadPathSafetyTests` contract suite and the additive Program.cs assertion; scripts and
governed-docs alignment; todo supersession at closeout.

**Out:** `squad pack --out` write safety (different hazard, different surface);
confirmation on read-only commands (`status`, `doctor`); changes to any non-squad
settings classes (strict parsing changes their failure mode for unknown options, not
their declared surface); MCP server binary; renegotiating the positional argument itself
(it stays, and remains the documented form).

## 6. Test contract (development-mode: test-first)

Runner for all rows: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | Manual smoke, no test task | `dotnet run --project src/KyberWeave.Cli -c Release -- squad install --path <tmp> --target copilot --dry-run` (read-only, `--dry-run`) | Pre-fix relaxed-mode behavior of `--path <dir>` recorded (exit code, output, effective root) | n/a — observation, recorded into §2 addendum | n/a |
| T2 | Read-only audit, no test task | grep/read over `scripts/`, `install.sh`, `.github/workflows/`, `docs/` | Every `kyber-weave`/`squad` invocation in-repo uses only declared options; offenders listed for T6 | n/a — audit note in task report | n/a |
| T3 | `tests/KyberWeave.Tests/SquadPathSafetyTests.cs` (new) + additive rows in `SquadCliCommandTests.cs` | dotnet test (contract suite) | (a) stub command over real `SquadInstallSettings` through a strict-configured `CommandApp`: `--path <dir>` binds `PathOption`, unknown option exits non-zero naming the offender; (b) `CoalesceTargetPath`: option-only, default-positional+option, conflict exits 2 with hint; (c) confirmation matrix: echo always, interactive accept/decline, `--yes`, non-interactive proceed; (d) command wiring: install interactive decline → exit 2, zero filesystem writes, `Declined. No changes were made.`; non-interactive run → exit 0 with absolute-root echo in output; (e) `Assert.Contains("UseStrictParsing", Program.cs text)` | Suite run with tests present and failing: compile failures for absent seams (PathOption, helper, ctor params) documented per row, and the Program.cs assertion failing on current source | Same suite passing with no weakened assertions |
| T4 | same files as T3 | dotnet test (full suite) | Settings/options/strict surface implemented | T3 RED captured before any source edit | T3 rows (a), (b), (e) green; full existing suite green (commands are tested via direct `Execute`, unaffected by parser change) |
| T5 | same files as T3 | dotnet test (full suite) | Confirmation implemented and wired | T3 RED captured before any source edit | T3 rows (c), (d) green; pre-existing install/update/uninstall Execute tests remain green on the non-interactive path (xunit input is redirected) |
| T6 | No new test task | `docs validate .` + `docs drift .` re-run in T7 | Docs/scripts aligned with new surface | Marked no-test: read-only alignment plus governed-docs edits verified by the T7 gates | n/a |
| T7 | No new test task | Repo gate suite (below) | All declared gates green; docs checks zero findings | Marked no-test: gates replace it | Gate evidence attached to closeout |

Sequencing note: T3 is the single RED wave (one test file, one authoring task) preceding
both GREEN scopes, because the contract suite references seams from both T4 and T5 and a
shared test file cannot be safely co-edited concurrently.

## 7. Tasks

- **T1 — Smoke the relaxed-mode behavior (early verification, intake gap 1).**
  Objective: pin empirically what Spectre.Console.Cli 0.55.0 relaxed parsing does with
  `squad install --path <dir> --target copilot --dry-run` on the unmodified branch.
  Files: none (read-only; `--dry-run` writes nothing). Acceptance: exit code, captured
  output, and the effective root recorded as a §2 addendum in this plan. Dependencies:
  none. Skills: none (read-only verification).
- **T2 — Audit in-repo CLI consumers (early verification, intake gap 2b).**
  Objective: enumerate every `kyber-weave` invocation in `scripts/`, `install.sh`,
  `.github/workflows/`, and docs examples; verify each option used is declared (post-T4
  surface included). Files: read-only. Acceptance: audit list delivered; any offender
  becomes a T6 fix item. Dependencies: none (parallel with T1). Skills: none.
- **T3 — Author the RED contract suite.** Objective: create
  `tests/KyberWeave.Tests/SquadPathSafetyTests.cs` per §6 row T3, and extend
  `ProgramRegistersSquadBranchWithAllVerbsDescriptionsAndExamples` with the
  `UseStrictParsing` assertion. Files: the two test files only. Acceptance: RED run
  evidence captured (failing assertions and documented compile failures for absent
  seams). Dependencies: T1 (evidence ordering). Skills: `test-dev`.
- **T4 — GREEN scope A: options, coalescing, strict parsing.** Objective: add
  `PathOption` (`--path <PATH>`) to the five settings classes and `Yes` (`--yes`) to
  install/update/uninstall in `src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs`;
  add `CoalesceTargetPath` to `SquadCommandComposition` with the N6 conflict rule; add
  `config.UseStrictParsing();` in `src/KyberWeave.Cli/Program.cs` and additive
  `--path` examples on the squad install/update/uninstall branch registrations.
  Acceptance: §6 rows (a), (b), (e) green; full suite green. Dependencies: T3.
  Skills: `csharp-dev`.
- **T5 — GREEN scope B: confirmation.** Objective: add
  `src/KyberWeave.Cli/Commands/Squad/Infrastructure/SquadTargetRootConfirmation.cs`
  (N1–N5, N7 contract); add optional `isInteractive`/`readAnswer` constructor parameters
  to the three mutating commands and call the helper immediately before
  `InstallAsync`/`UpdateAsync`/`UninstallAsync`. Acceptance: §6 rows (c), (d) green;
  full suite green. Dependencies: T4 (settings surface). Skills: `csharp-dev`.
- **T6 — Scripts and governed docs alignment.** Objective: apply T2 findings; check
  `docs/kyber-squad/onboarding.md`, `docs/kyber-squad/architecture.md`,
  `docs/install.md`, `docs/distribution.md` for squad CLI usage whose meaning changed
  (strict parsing) or that should show `--path`/`--yes`; make minimal alignment edits.
  Acceptance: every in-repo invocation valid under strict parsing; docs examples current.
  Dependencies: T2, T4. Skills: `kyber-weave-docs`.
- **T7 — Gates, review, closeout.** Objective: run the full verification gate list (§9);
  conduct review; supersede `docs/todo/squad-path-argument-safety.md` (frontmatter
  `status: superseded`, body link to this plan, todo-index row updated per the
  `kilo.md` convention). Acceptance: all gates green, docs checks zero findings, todo
  superseded. Dependencies: T5, T6. Skills: `code-review`, `kyber-weave-docs`.

## 8. Dependency graph and concurrency audit

T1 ∥ T2 → T3 → T4 → T5 → T6 → T7.

MAX_CONCURRENCY: **2** (T1 and T2 are independent read-only tasks). T3–T7 are a strict
chain: T3–T5 share the test file, T4–T5 share command/settings source files, and T6–T7
consume their outputs. No two chain tasks may run concurrently.

## 9. Verification gates

Per repository `AGENTS.md`, on this branch:

- `dotnet restore KyberWeave.sln`
- `dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal`
- `dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal`
- `dotnet build KyberWeave.sln -c Release --no-restore`
- `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`
  (mandatory: `docs/` changes — this plan, the index row, and the todo supersession)
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- review gates . --out artifacts/gates.json`

## 10. Risks

- **Strict parsing is a CLI-wide behavior change.** Any invocation relying on silently
  ignored unknown options now hard-errors. In-repo consumers are audited (T2/T6);
  out-of-repo consumer scripts cannot be audited — accepted, since silent acceptance is
  precisely the defect. Strict mode also rejects surplus positionals (e.g.
  `squad install a b`); that tighter behavior is intended and pinned by test.
- **Interactive prompt vs pty automation.** Mitigated by N2 (`--yes`) and N1
  (non-interactive never prompts; `update-loop.sh` keeps working).
- **Echo changes captured output.** Existing tests assert `Contains`, not exact output;
  external parsers of squad output could see a new line — low risk, noted.
- **Spectre API uncertainty.** The design assumes only `UseStrictParsing`,
  `CommandOption`, and `CommandApp.Run` (all already exercised in-repo or asserted by
  text). If 0.55.0 lacks an assumed surface, T1/T3 discover it at RED time — the contract
  tests, not the implementation, are the authority.

## 11. Review

Review via the repo's review path after T5: `review gates .` green plus the
`code-review` skill over the diff, with `static-analysis-triage` attributing
InspectCode findings.

Council verdict 2026-09-24: engine token REQUEST_CHANGES solely on the environmental docs-drift gate (KW-DOC-DRIFT-001, no local CodeGraph index — host-owned tool; CI verifies at PR time); council position: APPROVE-quality, merge conditional on CI's drift run. Gate evidence: 14/15 gates PASS (artifacts/gates.json, post-remediation refresh), full suite 2078/0 deterministic across repeated runs, InspectCode findings triaged — none attributable defects on this branch.

## 12. Docs-dev closeout

- This plan is registered in `docs/plans/README.md` (Active Plans) with development mode
  `test-first`.
- On finalize (conductor-relayed approval): body Status Draft → Ready, frontmatter
  `status: draft` → `current`, index Status → Ready, ledger removed, both docs checks
  re-run.
- On completion: supersede the todo and its index row (T7); harvest durable notes into
  `docs/kyber-squad/` CLI sections if review deems it warranted; no ADR expected — the
  strict-parsing choice is recorded here and in the aligned docs.

## Related

- [Todo: squad path argument safety](../todo/squad-path-argument-safety.md)
- [Kyber-Squad architecture](../../kyber-squad/architecture.md) · [onboarding](../../kyber-squad/onboarding.md)
- [Plan: ZCode harness target](../../plans/2026-09-21-zcode-harness-target.md) — plan-convention reference
