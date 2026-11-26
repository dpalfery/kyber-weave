---
id: plans/2026-11-26-opencode-instance-audit-and-spike
title: OpenCode harness support — audit the Arbiter and model-profile paths, run the OPENCODE_CONFIG_DIR spike, promote the instance row only on evidence
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-26
development-mode: test-first
documentation-impact: extends-component
impact-components:
  - KyberSquad
---

# OpenCode harness support — audit, spike, conditional promotion

**Status: Ready, 2026-11-26.**

Autonomy: autonomous. Development mode: test-first. Base: `main` at `c603a303b`, branch
`commandcode/feat/opencode-harness-support`, worktree
`.agent_worktrees/20261126T022035Z-commandcode-feat-opencode-harness-support`.

## Problem and goal

**Problem.** `kyber-weave squad run opencode` fails closed with exit `2` because
`SquadInstanceCatalog` marks OpenCode `SpikePending`. The spike that would justify promotion (S1 in
the deferred-spikes todo) never ran, so the row is stuck behind live evidence nobody recorded.
Two adjacent surfaces — the Arbiter's OpenCode plugin and the bundled model-profile tokens — look
complete in source but were never audited against a specific installed OpenCode version, so nobody
can say whether they are correct as designed or quietly wrong.

**Goal.** Three deliverables, in this order:

1. A written, version-pinned verdict on the Arbiter plugin path and the model-profile path, each
   backed by file and command evidence, recorded durably.
2. A recorded result for spike S1 — OpenCode version, host OS, date, and the observed layering,
   precedence and credential behaviour of `OPENCODE_CONFIG_DIR` — or a recorded BLOCKED with the
   exact error.
3. The catalog row and every documentation surface that states OpenCode's readiness, changed only in
   the direction S1's evidence supports, with contract tests updated in the same change.

The plan treats "no signed-in OpenCode install" as a planned-for outcome with its own task and
closeout, not as a planning failure.

## Request

The user's request, verbatim as the dispatch carried it:

> Task: Complete Kyber-Squad's OpenCode harness support (Arbiter, model profiles, `squad run`)
>
> ## Bottom line
>
> OpenCode already has an Arbiter renderer path and full model-profile support. The only
> missing capability is `kyber-weave squad run opencode`, which fails closed because
> `SquadInstanceCatalog` marks OpenCode `SpikePending`. Your job is to (A) audit the two
> existing paths for defects, (B) run the deferred `OPENCODE_CONFIG_DIR` spike with a live
> OpenCode install, and (C) promote the catalog row only if the evidence supports it, with
> tests and docs updated in the same change. If you cannot run the live spike, report the
> blocker and leave the row `SpikePending` — never flip a status on inference.
>
> Work in `/Users/dave/git/personal/copilot/kyber-weave`. Read `AGENTS.md` first and follow
> its inner-loop, process-watchdog, and governed-docs rules throughout.
>
> ## What already exists (verify from source, do not rebuild)
>
> **Arbiter**
> - `src/KyberWeave.Core.Squad/Rendering/ArbiterHookWiring.cs` — `HookedSquadTargets`
>   already includes `SquadTarget.OpenCode`; `TrustSteps` has no OpenCode entry (documented
>   as "no trust gate today").
> - `src/KyberWeave.Core.Squad/Rendering/OpenCodeRenderer.cs` — `RenderArbiter` emits
>   `.opencode/plugins/kyber-arbiter.ts` from `ArbiterPluginShim`, **project scope only**.
>   Global and instance scope deliberately render nothing.
> - `src/KyberWeave.Core.Squad/Rendering/ArbiterPluginShim.cs` — the shim body.
>
> **Model profiles**
> - `products/kyber-squad/profiles/models.yml` — every profile already has an `opencode:`
>   key (`zai-coding-plan/glm-5.4`, `opencode-go/...`).
> - `OpenCodeRenderer` emits `model` through `SquadModelPolicy.InheritOrBlankOmitted`.
> - Presets merge key-by-key per harness; `docs/kyber-squad/model-profiles.md` documents the
>   `opencode` token shape (`<provider>/<model-id>`, validated with `opencode models`).
>
> **`squad run`**
> - `src/KyberWeave.Core.Squad/Instances/SquadInstanceCatalog.cs` — OpenCode row is
>   `ConfigLayer` / `OPENCODE_CONFIG_DIR` / `SpikePending`.
> - `src/KyberWeave.Core.Squad/Deployment/SquadGlobalRoots.cs` — `ResolveOpenCodeRoot()`
>   already honours `OPENCODE_CONFIG_DIR`, then `XDG_CONFIG_HOME/opencode`, then
>   `~/.config/opencode`.
> - `SquadHarnessLauncher.BuildStartInfo` rejects any row that is not `Ready`, which is why
>   `squad run opencode` exits 2 today.
> - The deferred spike is S1 in
>   `docs/todo/squad-model-presets-windows-e2e-and-spikes.md`.
>
> ## Work item A — audit Arbiter and model-profile support
>
> Read the files above and report one of three verdicts per area: correct as designed, defect
> with a fix, or gap with a decision needed. Specifically establish, with file evidence:
>
> 1. Does `.opencode/plugins/kyber-arbiter.ts` match the plugin API of the installed
>    OpenCode version (`src/index` export, hook names, return shape)? Cite the version.
> 2. Is project-scope-only rendering the intended contract? Confirm against
>    `docs/adr/` and `docs/kyber-squad/` rather than guessing.
> 3. Does an instance-scope render (what `squad run` provisions) intentionally omit the
>    Arbiter shim? If yes, say so explicitly and state the consequence for
>    `squad run opencode`; if the omission looks accidental, do not silently change it —
>    raise it as a finding with the ADR reference that would need amending.
> 4. For model profiles: confirm every profile's `opencode` token exists in
>    `opencode models` on this machine (paste the command output), and confirm a preset
>    for `opencode` applies end to end (`squad preset use <name> --target opencode
>    --dry-run`, then a real apply against a scratch project).
>
> Do not change product code for A unless you can demonstrate a defect with a failing test.
>
> ## Work item B — run the `OPENCODE_CONFIG_DIR` spike (S1)
>
> Prerequisites: `opencode --version` succeeds and a provider is authenticated
> (`opencode auth list`, or `opencode models` returns ids). Record both in the report. If
> either fails, stop and report BLOCKED with the exact error; leave the catalog row alone.
>
> Probe design (use a scratch directory, never the repo or your real global config):
>
> 1. Global layer: place agent `probe-global` in OpenCode's global config root
>    (`$XDG_CONFIG_HOME/opencode/agents/` or `~/.config/opencode/agents/`).
> 2. Instance layer: place agent `probe-layer` in a temporary directory, and run OpenCode
>    with `OPENCODE_CONFIG_DIR=<tmp>`.
> 3. Merge test: confirm whether `probe-global` and `probe-layer` are both visible. This
>    decides whether `OPENCODE_CONFIG_DIR` layers or replaces.
> 4. Precedence test: define `probe-conflict` in both layers with different descriptions and
>    determine which one OpenCode loads.
> 5. Credentials test: confirm sign-in still works with the variable set, and record where
>    auth state lives (it is expected under the data directory, not the config directory).
> 6. Fallback test: repeat step 2 with `XDG_CONFIG_HOME` pointing at a temp tree and
>    `OPENCODE_CONFIG_DIR` unset, and report whether the layered agent still loads.
>
> Interpretation, per the existing todo and ADR 0044:
> - Layered merge with layer precedence, credentials intact → promote the row to `Ready`,
>   keep `ConfigLayer` and `OPENCODE_CONFIG_DIR`, `Reason = null`.
> - Layered merge with the wrong precedence → report and propose the smallest fix; do not
>   promote.
> - Replacement or broken credentials → follow the todo: if the `XDG_CONFIG_HOME` fallback
>   is sound, propose `HomeReplace`; otherwise mark `Unsupported` with a reason and say why
>   `squad run` cannot isolate OpenCode.
>
> ## Work item C — implement the outcome
>
> If promotion is justified:
>
> - `src/KyberWeave.Core.Squad/Instances/SquadInstanceCatalog.cs`: set the OpenCode row to
>   `Ready`, null the reason.
> - Update the contract tests that encode the current split:
>   `tests/KyberWeave.Tests/SquadInstanceCatalogTests.cs` (`SpikePendingTargetsMatchDesignSpecification`
>   currently expects OpenCode, Kilo, Cursor, ZCode), plus any `SquadRunCommandTests`,
>   `SquadInstanceServiceTests`, `SquadInstanceLinkSyncTests`, and `SquadGlobalRootTests`
>   expectations that name OpenCode's status. Add a test that a `Ready` OpenCode row produces
>   `OPENCODE_CONFIG_DIR=<instanceRoot>` in the launched process environment.
> - Docs, all in the same change:
>   - `docs/kyber-squad/onboarding.md` — the instance support matrix and the
>     `Ready`/`SpikePending`/`Unsupported` explanations.
>   - `docs/kyber-squad/model-profiles.md` — the "only claude, codex, copilot, pi are
>     Ready" sentence.
>   - `docs/todo/squad-model-presets-windows-e2e-and-spikes.md` — record the spike result
>     with the OpenCode version, date, and host OS; mark S1 done or restate why it stays
>     open.
>   - `docs/cli-command-surface.md` if it enumerates runnable targets.
>   - An ADR note only if a decision changed (for example `ConfigLayer` → `HomeReplace`);
>     otherwise leave ADRs alone.
> - Keep the Reason text and any `ExtraLinks`/`NeverLinked` you add grounded in observed
>   behaviour, not in expectation.
>
> If the spike fails, implement only the documentation half: record the evidence in the todo
> doc and leave the catalog untouched.
>
> ## Verification
>
> Run targeted checks through the process watchdog:
>
>     scripts/fast-loop.sh --class SquadInstance*
>     scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
>     scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadInstance"
>     dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready
>     dotnet run --project src/KyberWeave.Cli -- docs drift .
>
> Confirm the test run reports a non-zero total. Do not cite `fast-loop.sh` as gate evidence.
>
> ## Definition of done
>
> - A written verdict on Arbiter and model profiles, each backed by file and command evidence.
> - A recorded spike result naming the OpenCode version, OS, and the observed layering and
>   precedence behaviour.
> - Either a justified `Ready` promotion with tests and all four doc surfaces updated, or a
>   justified `Unsupported`/unchanged outcome with the same documentation discipline.
> - Targeted tests and `docs validate`/`docs drift` clean; no unrelated target rows changed;
>   no edits to the generated Config Reg block in `AGENTS.md`.
>
> ## Do not
>
> - Flip a status, or edit the matrix, without live evidence from this machine.
> - Change Kilo, Cursor, ZCode, or any other catalog row.
> - Touch `docs/archive/`.
> - Widen a schema or ontology to make a validation failure disappear.
>
> ---
> Two notes on scope: the prompt asks for an audit rather than an assumption that something is broken, because OpenCode's Arbiter and model support already look complete in source. And it deliberately makes "BLOCKED" an acceptable outcome, since the spike needs a signed-in OpenCode install.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| Work item A: audit the two existing paths and give one of three verdicts per area | T1; `## Investigation findings` | T1 writes per-area verdicts (correct as designed / defect with a fix / gap with a decision needed) into the evidence section it adds to the spikes todo |
| A.1 — does the shim match the installed OpenCode plugin API, citing the version | T1 (step A1) | `opencode --version` output recorded (v2.4.1 confirmed at plan time), plus the `{ id, setup }` / `ctx.tool.hook` shape pinned at `tests/KyberWeave.Application.Tests/Squad/ArbiterOpenCodeRenderingTests.cs` |
| A.2 — is project-scope-only rendering the contract? Confirm against `docs/adr/` and `docs/kyber-squad/` | T1 (step A2) | Verdict cites `docs/adr/0026`, `docs/adr/0054` and `docs/kyber-squad/onboarding.md` "Arbiter hooks"; the finding records that global scope *does* render the plugin file (ADR 0026 amendment), so "project scope only" is wrong as stated |
| A.3 — does an instance-scope render omit the Arbiter shim, and what is the consequence? Raise a finding, do not silently change | T1 (step A3) | Finding: an instance install request carries no `SquadArbiterWiring`, so `AddArbiterPluginShim` returns early; consequence for `squad run opencode` recorded; filed in `docs/todo/arbiter-global-hook-dispatch-coverage.md` with ADR 0044 named as the record to amend |
| A.4 — every `opencode` token exists in `opencode models`; paste the output; preset applies end to end | T1 (step A4); D20; D22 | When a provider is signed in, `opencode models` output is quoted for the three bundled tokens and `squad preset use architect` runs after `squad install` in a scratch project. When it is not, A4 records `BLOCKED (auth)` and skips the install and the apply |
| A: no product-code change without a demonstrated defect and a failing test | T1 acceptance; `## Out-of-scope boundaries` | T1's files contain no file under `src/` or `tests/`; any defect it finds lands as a finding plus a PR note |
| Work item B prerequisites: `opencode --version` and an authenticated provider; BLOCKED with the exact error otherwise | T2 (step B0) | Verdict block opens with the version line and the auth result; an unauthenticated install records BLOCKED verbatim and stops before any probe |
| B.1 global layer probe agent | T2 (step B1) | Path created, recorded, and removed; pre-state recorded |
| B.2 instance layer probe under `OPENCODE_CONFIG_DIR=<tmp>` | T2 (step B2) | Scratch directory path recorded (outside the repo and outside the real global root) |
| B.3 merge test — are both probes visible | T2 (step B3) | Observed listing recorded verbatim; decides layer-vs-replace |
| B.4 precedence test — which `probe-conflict` loads | T2 (step B4) | The loaded description recorded verbatim |
| B.5 credentials test — sign-in survives, auth state location | T2 (step B5) | `opencode models` returns ids with the variable set; the auth path is recorded (data dir, not config dir) |
| B.6 fallback test — `XDG_CONFIG_HOME` temp tree, variable unset | T2 (step B6) | Whether the layered agent loads is recorded |
| B interpretation rules (layered+precedence+credentials → Ready / wrong precedence → report only / replace or broken creds → HomeReplace or Unsupported) | T2 step B8; D3; T3 | The verdict line is `PROMOTE: Ready`, `HOLD: precedence`, `PROPOSE: HomeReplace`, `UNSUPPORTED`, or `BLOCKED`. T3 applies only Ready and Unsupported. T4's matrix matches the catalog afterwards |
| B: use a scratch directory, never the repo or the real global config | T2 files and acceptance; D10; `## Risks` | Acceptance requires the scratch path to be outside the repository, and the one file written under the real global root removed in the same run |
| B: never flip a status on inference | D3; T3 acceptance; T4 acceptance | T3 changes the row only when the outcome line is `PROMOTE: Ready` or `UNSUPPORTED`, and quotes that line; T4 states the recorded outcome, never an expectation |
| Work item C — promotion: catalog row `Ready`, reason null | T3 | `SquadInstanceCatalog.CatalogRows` OpenCode entry changed; `KyberWeave.Tests.SquadInstanceCatalogTests.ReadyTargetsMatchDesignSpecification` lists OpenCode |
| C — update the contract tests that encode the split | T3 | `SpikePendingTargetsMatchDesignSpecification` no longer lists OpenCode; each additional test file named in T3 is inspected and either keeps its assertions or narrows a locator, named in the report |
| C — add a test that a `Ready` OpenCode row produces `OPENCODE_CONFIG_DIR=<instanceRoot>` | T3; Test contract row T3 | New case in `KyberWeave.Tests.SquadRunCommandTests`, asserting `startInfo.Environment["OPENCODE_CONFIG_DIR"]` equals the ensured instance root |
| C — `docs/kyber-squad/onboarding.md` matrix and status explanations | T4 | Matrix row and the `Ready`/`SpikePending`/`Unsupported` wording state OpenCode's recorded status |
| C — `docs/kyber-squad/model-profiles.md` "only claude, codex, copilot, pi are Ready" sentence | T4 | That sentence matches the catalog |
| C — spikes todo records the result with version, date and OS | T1 gathers the inputs, T2 writes the result, T4 closes S1 or restates why it stays open | The todo's S1 section carries the verdict block; the todo index row description matches |
| C — `docs/cli-command-surface.md` if it enumerates runnable targets | D12; `## Out-of-scope boundaries` | Verified: that page carries no `squad run` row and no instance matrix, so no edit is required; the missing row is a PR note |
| C — an ADR note only if a decision changed | D15; T4 | ADR 0044 is amended only for `UNSUPPORTED`. Ready, BLOCKED and a HomeReplace proposal leave it untouched |
| C — Reason / `ExtraLinks` / `NeverLinked` grounded in observed behaviour | T3 acceptance | Any non-empty value must quote the observation that produced it; otherwise it stays empty and null |
| C — if the spike fails, documentation only, catalog untouched | T3 guard; T4 | A `BLOCKED` line, and a HomeReplace proposal, make T3 report `NOT_APPLICABLE` and change nothing. `UNSUPPORTED` is a catalog change, not this branch. T4 still lands the documentation surfaces |
| Verification: run targeted checks, confirm a non-zero test total, never cite `fast-loop.sh` as evidence | `## Verification gates`; D5, D6 | Gates list the task-gate and targeted-test commands and state that `fast-loop.sh` is an iteration aid only |
| Definition of done: verdicts, recorded spike, one justified outcome, clean gates | T1–T4; `## Verification gates`; `## docs-dev closeout` | Every item maps to a task acceptance or a gate |
| Do not: flip a status without live evidence | D3 | Same as the inference row above |
| Do not: change Kilo, Cursor, ZCode or any other catalog row | T3 acceptance; D16 | T3 may edit only the OpenCode entry; the diff is checked against the file |
| Do not: touch `docs/archive/` | `## Out-of-scope boundaries` | No task names a path under `docs/archive/` |
| Do not: widen a schema or ontology to make a validation failure disappear | `## Out-of-scope boundaries` | No task names `docs/documentation-ontology.md` or `.kyber-weave/kyber-weave.yml` |
| The request's work path (`/Users/dave/git/personal/copilot/kyber-weave`) is a stale checkout | D8; this plan's header | This run works in the worktree naming `main` at `c603a303b`; the other checkout is on an unrelated stale branch |

## Scale

`documentation-impact: extends-component`. The change extends KyberSquad: it re-classifies one
existing catalog row inside an existing mechanism, adds no component, no folder, no dependency and no
schema. `impact-components` names `KyberSquad`. Four documentation surfaces carry the outcome, one of
which (`docs/todo/`) also carries the evidence a future spike copies.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Plan or specification for this work? | Plan. The architecture is already decided (ADR 0044 defines instance scope and the catalog), and the work is a short, ordered sequence: audit, live probe, conditional promotion. | A specification would restate ADR 0044 and add nothing; the request itself asks for tasks with acceptance criteria and a conditional outcome. | autonomous: recommended option, ADR 0044 and the request's own task framing |
| D2 | Where does the audit verdict and the spike evidence live so both survive the run? | A new section in `docs/todo/squad-model-presets-windows-e2e-and-spikes.md`, which the request already names as S1's record. The archived plan keeps the same text in its investigation findings. | Alternatives rejected: the plan file alone (a dispatched worker cannot edit a plan), a new reference page under `docs/kyber-squad/` (a new governed document with no reader who needs it as canonical guidance, and the corpus gate would have to absorb an unreferenced page), a comment in the catalog source (invisible to the operator who runs `squad run`). A todo is durable and is the documented home for deferred or recorded evidence. | autonomous: recommended option, `docs/todo/README.md` "When to use a todo" |
| D3 | How is work item C's conditional modelled in a dispatchable task file? | Two guarded tasks. T3 changes the OpenCode row for `PROMOTE: Ready` (status `Ready`, reason null, kind `ConfigLayer`, variable `OPENCODE_CONFIG_DIR`) and for `UNSUPPORTED` (status `Unsupported`, kind `None`, variable null, reason quoted from the observation, executable still `opencode`). For `HOLD: precedence`, `PROPOSE: HomeReplace` and every `BLOCKED` line, T3 changes nothing and reports `NOT_APPLICABLE` quoting the line. T4 always runs; the matrix matches the catalog T3 left, including Kind and Environment Variable. | `AcceptanceCriterion7_7_HarnessWithNoUsableVariableIsMarkedUnsupportedWithExplanatoryReason` requires every Unsupported row to have kind None and a null variable. Keeping ConfigLayer fails that test and the onboarding matrix. HomeReplace stays a proposal. | autonomous: recommended option, `AcceptanceCriterion7_7` and plan review correctness/unsupported-row-must-be-none |
| D4 | Do documentation-only tasks carry a `Technology:` line? | No. T1, T2 and T4 carry `Capabilities:` naming documentation and omit `Technology:`. | `PlanTaskFileValidator.NeedsTechnology` exempts a task whose capabilities contain "documentation", and every archived plan's documentation tasks omit the line. T3 is the only task that names a technology (`csharp`). | autonomous: recommended option, `src/KyberWeave.Application/Docs/Validation/PlanTaskFileValidator.cs` `NeedsTechnology` |
| D5 | The request's verification wraps dotnet in `scripts/proc-guard.sh run --`. That script does not exist in this tree. | Run the commands directly, unwrapped: `dotnet build` and `dotnet test` on the named project. No `proc-guard` prefix anywhere in this plan. | `scripts/` holds 16 entries and no `proc-guard.sh`; the retired todo row for `kyberdash-spawn-guard-residuals` records `scripts/proc-guard.sh` as retired, and `AGENTS.md` states every test, gate and e2e command runs directly with no process wrapper. | autonomous: recommended option, `AGENTS.md` "The inner loop" and `docs/todo/README.md` (closed rows) |
| D6 | The request's verification runs `scripts/fast-loop.sh --class SquadInstance*`. | It is kept as an iteration aid only and is never cited as gate evidence; the evidence commands are the task gates and the targeted `dotnet test` filters. | `AGENTS.md` states plainly that `fast-loop.sh` "is **not** evidence and must never be cited as such", and the request repeats the constraint. | autonomous: recommended option, `AGENTS.md` and the request's own sentence |
| D7 | The request's verification runs `docs validate . --merge-ready`. That mode always fails while this plan is open in the worktree. | Run `docs validate .` and `docs drift .` without `--merge-ready` in the run worktree. The `--merge-ready` gate belongs to CI and to closeout, after the plan is archived. | `KW-DOC-LIFECYCLE-003` fails on any plan still in the active plans folder; running it mid-run can only produce a finding no one may fix yet. | autonomous: recommended option, plan-authoring "Documentation gates after plan writes" |
| D8 | Which OpenCode build is the audit's pinned version, and which checkout holds the work? | `opencode v2.4.1`, resolved at `/opt/homebrew/bin/opencode` on macOS (darwin), verified 2026-11-26. The work happens in this run's worktree on `commandcode/feat/opencode-harness-support` at `c603a303b`, not in the stale `/Users/dave/git/personal/copilot/kyber-weave` checkout. | The stale checkout is on `arbiter-global-install-plan` with unrelated uncommitted work; both paths share origin `dpalfery/kyber-weave`, so the worktree is the same repository on a fresh branch. Provider authentication is unverified and is exactly what T2's prerequisite step settles. | autonomous: recommended option, run routing facts and `opencode --version` |
| D9 | How does the spike observe which agents OpenCode loaded, without inference? | Only a non-interactive OpenCode verb. Run `opencode --help`, then any agent-listing verb it names that does not start a provider session. If none can list loaded agents, T2 records `BLOCKED (method)` with the commands tried and leaves the row `SpikePending`. Do not call a provider model, and do not start a session to obtain the listing. | Reading files and assuming the loader read them is inference. A provider-model fallback is the same kind of inference, and it would send harness configuration to a third-party endpoint. The request's B3 already names `BLOCKED (method)` for this gap. | autonomous: recommended option, the request's B3 and plan review intent-alignment/d9-provider-model-fallback |
| D10 | The global-layer probe writes an agent into the user's real OpenCode global config root. How is that made safe? | T2 records the path before writing, writes only `agents/probe-global.md` (and `agents/probe-conflict.md`), edits no existing file, and removes every file it created before it finishes; the verdict block lists each path created and each removal. A run that cannot restore reports `BLOCKED (restore)` rather than a clean result. | The request instructs the global layer explicitly and specifies the restore. Creating only new files makes rollback a deletion, so no user content is ever rewritten. | autonomous: recommended option, the request's probe design plus `AGENTS.md` "Protecting hand-authored instruction files" |
| D11 | What happens if a bundled `opencode` token is absent from `opencode models`? | Report it as a finding with the command output, and carry it as a PR note. Do not edit `products/kyber-squad/profiles/models.yml`. | The request forbids product-code changes in work item A without a failing test, and the existence of a provider model id cannot be asserted by a unit test — the catalog is live state on one machine. | autonomous: recommended option, the request's work-item-A constraint |
| D12 | `docs/cli-command-surface.md` — does it enumerate runnable targets? | No. It carries no `squad run` row and no instance support matrix, so no edit is required. The absent `squad run` row is a PR note, not work in this plan. | `rg` over the page finds no `squad run` row: the `squad` table lists install, update, uninstall, status, doctor, brief, pack, the copilot-prompt verbs and the preset verbs only. | autonomous: recommended option, `docs/cli-command-surface.md` § `squad` |
| D13 | Difficulty ratings for the two non-trivial tasks. | T2 (live spike) is `medium`: it changes a real machine's global harness config and restores it, so the failure mode is a leftover file nobody's test will see. T3 (promotion) is `medium`: it changes a status other code and four documents read, and a wrong flip launches an unverified isolation path no unit test can detect. T1 and T4 are `easy`. | The rubric's contrast pairs put "runs on a real external system that can be left broken" and "changes a value other readers branch on" one tier above standard, and neither reaches advanced: no data is corrupted, no access is leaked, nothing is charged. | autonomous: recommended option, plan-authoring difficulty rubric |
| D14 | Which documentation surfaces change in the always-run docs task? | `docs/kyber-squad/onboarding.md` (matrix row plus the status explanations), `docs/kyber-squad/model-profiles.md` (the Ready sentence), `docs/todo/squad-model-presets-windows-e2e-and-spikes.md` (S1 marked done, or why it stays open) and the one `docs/todo/README.md` row for that spikes todo. The arbiter-hook todo's index row stays: its description is still true after T1 adds a bullet. | The request names the first three. The index row is updated once, in T4, after the catalog status is known. `KW-DOC-LIFECYCLE-002` is reachability; a row whose description no longer matches its todo misleads the next reader. | autonomous: recommended option, the request and `docs/todo/README.md` |
| D15 | When does ADR 0044 change? | Amend it in place only for `UNSUPPORTED`: a dated amendment that OpenCode cannot be isolated, with the observed reason. Leave it untouched for `PROMOTE: Ready` (ConfigLayer stands), for `BLOCKED`, and for `PROPOSE: HomeReplace`. HomeReplace is a proposal in the spikes todo and a PR note, because an accepted ADR states the decision the catalog implements and this run does not change the kind. | The ADR policy edits an accepted ADR to state only the decision in force (`docs/todo/adr-in-place-cleanup.md`). A proposal that leaves `SquadInstanceKind.ConfigLayer` in place is not that decision. | autonomous: recommended option, `docs/todo/adr-in-place-cleanup.md` and the request's "propose HomeReplace" |
| D16 | Are any other catalog rows, or `docs/archive/`, in scope? | No. T3 may edit only the `SquadTarget.OpenCode` entry in `SquadInstanceCatalog.CatalogRows`; Kilo, Cursor, ZCode and the Unsupported rows keep their status and reason. No task names a path under `docs/archive/`. | The request states both prohibitions; S2–S4 (Kilo, Cursor, ZCode) remain open in the same todo and are separate spikes on their own hosts. | autonomous: recommended option, the request's "Do not" section |
| D17 | What is out of scope when the audit finds the instance render carries no Arbiter wiring? | Out of scope for this plan: raise it as a finding in `docs/todo/arbiter-global-hook-dispatch-coverage.md` plus a PR note, naming ADR 0044 as the record to amend. Do not change the renderer or the instance install request. | The request says explicitly not to silently change an apparent omission and to raise it with the ADR that would need amending. Changing what the instance install passes would also change every instance target's rendering, far beyond OpenCode. | autonomous: recommended option, the request's A.3 |
| D18 | Does the promotion task change a contract other code reads, and how is that shown to the reviewer? | Yes: `SquadInstanceStatus` for OpenCode is read by `SquadHarnessLauncher.PrepareLaunch` (which refuses a non-Ready row), `SquadRunCommand.Execute` (exit 2 versus launch), `SquadInstanceLinkSync`, and four documents. Ready makes `squad run opencode` provision an instance; Unsupported keeps the refusal and adds a reason. T3's design note states both, and its report names every reader it re-checked. | Either flip is justified only by T2's live evidence. Ready keeps `ConfigLayer` and `OPENCODE_CONFIG_DIR`. Unsupported sets kind `None` and variable null, which `AcceptanceCriterion7_7` already requires of every Unsupported row, and which `PrepareLaunch` refuses. | autonomous: recommended option, `AcceptanceCriterion7_7` and plan review correctness/unsupported-row-must-be-none |
| D19 | Which backtick paths belong in a task's Files list? | Only the repository files that task may write. Read-only source, ADRs consulted as evidence, and probe files outside the repository are named in the objective, not under Files. | `HarvestBulletRun` treats every slash-bearing backtick in the Files bullets as file scope, and `PlanFileScope` keeps any relative path, including `.config/opencode` and `agents/probe-global.md`. A worker is then told those paths are its change scope. | autonomous: recommended option, `PlanDocumentParser.HarvestBulletRun` and `ArbiterPathRule.ExtractFilesPaths` |
| D20 | How does the model-profile apply actually rewrite an agent file? | Use the bundled profile `architect` (`opencode: zai-coding-plan/glm-5.3`). In a scratch directory outside the repository: `kyber-weave squad install <scratch> --target opencode --yes`, then `kyber-weave squad preset show architect --target opencode`, `kyber-weave squad preset use architect <scratch> --target opencode --dry-run`, the same `use` without `--dry-run`, then `kyber-weave squad preset clear <scratch> --target opencode`. Delete the scratch directory after the model line is recorded. | A `preset use` with no deployment records the selection and writes no file (F12). The request's `<name>` is this profile. `models.yml` is not edited. | autonomous: recommended option, `products/kyber-squad/profiles/models.yml` and `SquadPresetCliTests.UseWhenTargetNotDeployedInProjectRecordsSelectionAndExitsZeroWithGuidance` |
| D21 | What body makes a spike probe an agent OpenCode will load? | Each probe file is Markdown with YAML frontmatter whose required keys are `name`, `description`, and `mode: subagent`, matching `OpenCodeRenderer`'s recorded contract. The four bodies are written in T2 steps B1 and B2. Descriptions differ so the precedence probe can quote which one loaded. | A file that is only a title can be skipped by the loader, and the merge test would then report a false miss. `mode: subagent` is the documented non-conductor value. | autonomous: recommended option, `OpenCodeRenderer` remarks and plan review correctness/probe-files-need-valid-frontmatter |
| D22 | What does T1 do when `opencode models` cannot list ids because no provider is signed in? | A4 records `BLOCKED (auth)`, quotes the error, and does not run the scratch install or the preset apply. Missing tokens are not reported as a defect. A1–A3 still receive verdicts. T2 then records `BLOCKED (prerequisite)` from the same failure. | The request treats an unauthenticated install as an acceptable stop. A4's three verdicts have no place for that stop, so a failed `opencode models` would otherwise be written up as absent tokens. | autonomous: recommended option, the request's spike prerequisites and plan review intent-alignment/t1-a4-needs-auth-gate |
| D23 | When do the corpus gates run? | T1, T2 and T4 run `kyber-weave doctor --ensure .` before `kyber-weave docs validate .` and `kyber-weave docs drift .`. | `docs drift` resolves documented symbols against the CodeGraph index. A fresh worker worktree does not have one until doctor ensures it. | autonomous: recommended option, `AGENTS.md` exploration order and plan review test-infrastructure/missing-index-ensure |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute, 2026-11-26 |

## Design harvest

| ID | Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|---|
| H1 | KyberSquad | onboarding | `docs/kyber-squad/onboarding.md` § 3, "Harness Instance Support Matrix", and the `Ready` / `SpikePending` / `Unsupported` explanations below it | The OpenCode row's recorded status, kind and variable, and the isolation behaviour the spike observed — stated as observed, with the version and OS that observed it |
| H2 | KyberSquad | onboarding | `docs/kyber-squad/model-profiles.md` § "Multiple profiles at once" | Which targets are `Ready` for isolated instances, matching the catalog after this change |
| H3 | KyberSquad | todo | `docs/todo/squad-model-presets-windows-e2e-and-spikes.md` § S1 | The method (both probe layers, the precedence probe, the credential probe, the fallback probe), the OpenCode version and host OS, the observed behaviour, and the resulting catalog decision, so S2–S4 copy a validated method |
| H4 | KyberSquad | adr | `docs/adr/0044-squad-lock-v2-and-instance-deployment-scope.md` | Only for `UNSUPPORTED`: a dated amendment that OpenCode cannot be isolated, with the observed reason. No amendment when the catalog stays `ConfigLayer`, including a HomeReplace proposal |
| H5 | KyberArbiter | todo | `docs/todo/arbiter-global-hook-dispatch-coverage.md` | The instance-render finding: what an instance install passes, which hook file it therefore omits, the consequence for `squad run` on every instance target, and ADR 0044 as the record to amend |

## Investigation findings

Read at revision `c603a303b` on branch `commandcode/feat/opencode-harness-support`, in this run's
worktree. Discovery: codegraph 10, docs 0 (the session docs server may answer for a different git
directory, so it was not used), direct reads 24. The review pass read
`AcceptanceCriterion7_7_HarnessWithNoUsableVariableIsMarkedUnsupportedWithExplanatoryReason` and the
`OpenCodeRenderer` remarks, because the review named those lines. The extra codegraph calls and the direct reads of
`PlanDocumentParser`, `PlanFileScope`, `PlanPacketInliner`, `SquadInstanceCatalogTests`,
`SquadRunCommandTests`, `SquadSettings` and the spikes todo are the resume pass: the first codegraph
answers truncated before the file-scope rule and the S1 wording.

**F1 — the catalog is in the domain project, not under `src/KyberWeave.Core.Squad`.** Two files share
the name and both matter: `src/KyberWeave.Domain/Squad/Instances/SquadInstanceCatalog.cs` holds
`SquadInstanceKind`, `SquadInstanceStatus` and `SquadInstanceRow`;
`src/KyberWeave.Application/Squad/Instances/SquadInstanceCatalog.cs` holds `CatalogRows`. The
request's path (`src/KyberWeave.Core.Squad/Instances/…`) is the namespace, not the folder. T3 names
both real paths.

**F2 — the block that refuses a launch is `PrepareLaunch`, not `BuildStartInfo`.** Both overloads of
`SquadHarnessLauncher.PrepareLaunch` (and `ProcessSquadHarnessLauncher`) throw
`ArgumentException("Harness '<target>' is not ready for instance execution.")` when
`row.Status != Ready || string.IsNullOrWhiteSpace(row.Variable)`. `SquadRunCommand.Execute` refuses
earlier, with exit `2` and the row's reason, before `EnsureAsync` runs.

**F3 — "project scope only" is not the contract.** `SquadHarnessRenderer.AddArbiterPluginShim`
selects the project path unless the scope is `Global`, in which case it writes the optional global
path — and `OpenCodeRenderer.RenderArbiter` supplies `"plugins/kyber-arbiter.ts"` for exactly that.
`ArbiterPluginShim.Render` picks its import block and command line from `scope == Global`, and
`ArbiterGlobalPluginDirRenderingTests.RenderAsync_GlobalScope_OpenCode_EmitsOwnedAutoLoadedPluginFileUnderGlobalRoot`
pins the global file under `~/.config/opencode/plugins/` with `dispatch-unconfirmed` recorded as the
degradation. `docs/kyber-squad/onboarding.md` ("Arbiter hooks") agrees: on `pi`, `kilo` and `opencode`
the hook is an owned extension/plugin file, ADR 0026 amendment. So global scope renders the plugin and
project scope renders it; the audit must correct the request's premise rather than repeat it.

**F4 — an instance render is a Global-scope render that carries no Arbiter wiring.** `SquadLifecycleService`
maps `SquadDeploymentScope.Instance` to `renderScope = Global` and to a `SquadInstanceRootResolver`
root, so the renderer sees `Global` against the instance root. `DispatchHooks` is
`Scope == Project && Request.Arbiter is not null`, and `SquadInstanceService.EnsureAsync` builds
`SquadInstallRequest(instanceRoot, Instance, [target], ModelPresets: …)` with no `Arbiter`. Both
guards in `AddArbiterPluginShim` therefore fail and no shim is written. The consequence for
`squad run opencode` is that the launched session loads no Arbiter plugin, while
`squad install --global` on the same machine installed one under the user's global root — and
whether that global plugin is even discovered by a session whose config root is the instance is
exactly what T2's probe design settles. This is the A.3 finding: not a scope rule, an unwired
install request, and the record that would have to change is ADR 0044 decision 4 (instance scope)
with ADR 0054's "instance scope never owns a block" as the neighbouring rule.

**F5 — model profiling is complete in source.** `products/kyber-squad/profiles/models.yml` carries an
`opencode:` key on every profile that names one (`zai-coding-plan/glm-5.3` ×3,
`opencode-go/muse-spark-1.3-contributor` ×2, `opencode-go/kimi-k2.7-code`), and
`OpenCodeRenderer.HarnessSpec` resolves tokens through `SquadModelPolicy.InheritOrBlankOmitted`, which
omits the `model` key for `inherit`. `docs/kyber-squad/model-profiles.md` documents the
`<provider>/<model-id>` shape and tells the reader to verify with `opencode models`. Note the
request's example token `zai-coding-plan/glm-5.4` does not appear in the bundled file — the bundled
token is `glm-5.3`, so T1 verifies the three real tokens, not the example.

**F6 — the test that must move is `SpikePendingTargetsMatchDesignSpecification`, and its sibling
matters too.** `KyberWeave.Tests.SquadInstanceCatalogTests` asserts the exact Ready list
(`ReadyTargetsMatchDesignSpecification`) and the exact SpikePending list. Adding OpenCode to Ready
without removing it from SpikePending fails both. `KyberWeave.Tests.SquadRunCommandTests` has
`SpikePendingHarnessExitsWithCodeTwoAndNamesReason`, which selects `SquadTarget.OpenCode` — after
promotion that case must use another still-pending target (Kilo, Cursor or ZCode) or it fails; its
assertion about exit `2` and the reason text stays intact.

**F7 — `SquadGlobalRoots` needs no change for this work.** `ResolveOpenCodeRoot` already honours
`OPENCODE_CONFIG_DIR`, then `XDG_CONFIG_HOME/opencode`, then `~/.config/opencode`, and
`SquadGlobalRootsTests` pins the override beating `XDG_CONFIG_HOME` and the refusal of an override
pointing into the squad instances directory. Only the catalog row and its readers move.

**F8 — the diagnostics build is not the one the request names.** The request's verification names
`tests/KyberWeave.Tests`, which does not exist in this tree: unit tests live one project per product
project (`tests/KyberWeave.Application.Tests`, `tests/KyberWeave.Cli.Tests`, `tests/KyberWeave.Data.Tests`,
…), as `AGENTS.md` states and the gate catalogue confirms. T3 names the projects that actually hold the
two classes.

**F9 — evidence gap this run cannot close.** Whether a real `opencode` session loads a plugin found
only in the instance config root depends on OpenCode's own loader and cannot be settled by reading this
tree; it is what T2 measures and what the `SpikePending` status exists for.

**F10 — a non-Ready outcome has to change the catalog or the matrix cannot tell the truth.** S1 in
`docs/todo/squad-model-presets-windows-e2e-and-spikes.md` says mark Ready when the layered agent
overrides, otherwise try `XDG_CONFIG_HOME` or mark Unsupported. The request is stricter, and
`UnsupportedTargetsMatchDesignSpecification` requires a non-empty Reason.
`AcceptanceCriterion7_7_HarnessWithNoUsableVariableIsMarkedUnsupportedWithExplanatoryReason` also
requires kind `None` and a null variable for every Unsupported row. Leaving the row `SpikePending`
while the matrix says Unsupported fails T4. An Unsupported row that keeps `ConfigLayer` fails 7.7. HomeReplace stays a proposal: the request says propose it, and an ADR states only a
decision the catalog implements.

**F11 — Files bullets were becoming the worker's change scope.** `HarvestBulletRun` keeps every
slash-bearing backtick under a Files bullet, and `PlanFileScope` keeps any relative path. The previous
wording therefore scoped `docs/adr/0026`, `ArbiterPluginShim.Render`, `.config/opencode` and
`agents/probe-global.md` as files to change.

**F12 — a preset apply writes an agent file only after a project install.**
`UseWhenTargetNotDeployedInProjectRecordsSelectionAndExitsZeroWithGuidance` records the selection and
leaves the directory empty. The profile key `architect` in `products/kyber-squad/profiles/models.yml`
carries `opencode: zai-coding-plan/glm-5.3`. The request's example token `glm-5.4` is not in that file.

**PR note (divergence, not fixed here):** `docs/cli-command-surface.md` documents every other `squad`
verb but has no `squad run` row, although `squad run` is documented in
`docs/kyber-squad/onboarding.md`. Out of scope; the request makes that page conditional and its
condition is false.

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | none — verification task | `kyber-weave doctor --ensure .`; `opencode --version`; `opencode auth list`; `opencode models`; `kyber-weave squad install <scratch> --target opencode --yes`; `kyber-weave squad preset show architect --target opencode`; `kyber-weave squad preset use architect <scratch> --target opencode --dry-run`; `kyber-weave squad preset use architect <scratch> --target opencode`; `kyber-weave squad preset clear <scratch> --target opencode`; `kyber-weave docs validate .`; `kyber-weave docs drift .` | No behaviour is added. The replacement evidence is the recorded command output behind each verdict. A4 may instead be `BLOCKED (auth)` when no provider is signed in, in which case the install and preset commands are not run. | Not applicable: no code, no test. | A1–A3 each carry one of the three verdicts with evidence. A4 either does the same, including the dry run and the apply, or records `BLOCKED (auth)` and skips the install and apply. The corpus gates pass. |
| T2 | none — live probe task | `kyber-weave doctor --ensure .`; `opencode --version`; `opencode auth list`; `opencode models`; `opencode --help`; then the six probe runs with `OPENCODE_CONFIG_DIR` and `XDG_CONFIG_HOME` set per step; `kyber-weave docs validate .`; `kyber-weave docs drift .` | No behaviour is added. The replacement evidence is a verdict block naming exactly one outcome (`PROMOTE: Ready`, `HOLD: precedence`, `PROPOSE: HomeReplace`, `UNSUPPORTED`, or `BLOCKED (<stage>)`) with the version, OS, date, and the six observed results. | Not applicable: no code, no test. | The verdict block is complete: version, OS, date, auth result, each of the six probes with its observed result, every file created under the real global root listed as removed, and the scratch path named and confirmed outside the repository. A `BLOCKED` verdict quotes the failing command's exact stderr. |
| T3 | `tests/KyberWeave.Application.Tests/Squad/Instances/SquadInstanceCatalogTests.cs` (extend `KyberWeave.Tests.SquadInstanceCatalogTests`); `tests/KyberWeave.Cli.Tests/SquadRunCommandTests.cs` (extend `KyberWeave.Tests.SquadRunCommandTests`, copying `ConcurrentRunsExecuteWithIsolatedInstanceRoots` for the env-var assertion) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.SquadInstanceCatalogTests"` and `dotnet test tests/KyberWeave.Cli.Tests/KyberWeave.Cli.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.SquadRunCommandTests"` | Write only the branch T2's outcome line selects. Ready branch: (a) `GetRow(OpenCode)` is `Ready`, `Kind == ConfigLayer`, `Variable == "OPENCODE_CONFIG_DIR"`, `Reason` null, and the Ready roster contains OpenCode. (b) SpikePending contains Kilo, Cursor and ZCode and no OpenCode. (c) Kilo still exits `2`; an existing Unsupported target (Warp) still exits `2` naming its reason. (d) `ExtraLinks` and `NeverLinked` stay empty. (e) `startInfo.Environment["OPENCODE_CONFIG_DIR"]` equals the instance root `EnsureAsync` returned, and `FileName` resolves `opencode` from PATH. Unsupported branch: (a) OpenCode is `Unsupported`, `Kind == None`, `Variable` null, and Reason non-empty. `AcceptanceCriterion7_7_HarnessWithNoUsableVariableIsMarkedUnsupportedWithExplanatoryReason` then passes without a new assertion. (b) SpikePending drops OpenCode and keeps Kilo, Cursor and ZCode. (c) the Ready roster is unchanged. (d) link collections stay empty. (e) no launch env-var case, because the row must not launch. (f) both branches leave out the Windows `.cmd`/`PATHEXT` shape, because `SquadHarnessLauncherTests` already covers every row and this task changes no launcher. NOT_APPLICABLE writes no test. | Ready branch, tests written first while the row is still `SpikePending`: `ReadyTargetsMatchDesignSpecification` fails on the roster, `SpikePendingTargetsMatchDesignSpecification` fails on the roster, and the new env-var case fails because `Execute` returns `2` and captures no `ProcessStartInfo`. Unsupported branch: the SpikePending roster assertion fails and `UnsupportedTargetsMatchDesignSpecification` fails because OpenCode is still pending; the Ready roster assertion is left unchanged so it stays green. The failing run reports a non-zero total. | The same two filters pass with a non-zero total and no assertion loosened. On Ready, the env-var case asserts equality with the ensured root. On Unsupported, that case is absent, OpenCode is on the Unsupported roster, Kind is None, Variable is null, and Reason is non-empty. On NOT_APPLICABLE, no test file differs from `c603a303b`. |
| T4 | none — documentation task | `kyber-weave doctor --ensure .`; `kyber-weave docs validate .`; `kyber-weave docs drift .` | No behaviour is added. The replacement evidence is both corpus gates at zero findings outside the standards warning period, plus each edited statement matching the catalog's recorded status. | Not applicable: no code, no test. | No document states a status the catalog contradicts; the matrix row, the model-profiles sentence and the spikes todo's index row agree with each other and with `SquadInstanceCatalog`; both gates pass. |

## Tasks

### T1: Audit the OpenCode Arbiter path and the bundled model profiles, and record the verdicts

- **Objective:** Establish, with file and command evidence against `opencode v2.4.1`, whether the Arbiter plugin path and the model-profile path are correct as designed, a defect with a fix, or a gap needing a decision, and record the verdicts durably. Each verdict opens with exactly one of `Verdict: correct as designed`, `Verdict: defect with a fix`, or `Verdict: gap with a decision needed`, then the evidence. Change no file under `src/` or `tests/`.
  - A1 — plugin API. In `src/KyberWeave.Application/Squad/Rendering/ArbiterPluginShim.cs`, quote what `ArbiterPluginShim.Render`, `OpenCodePluginBody`, `OpenCodeDefaultExport` and `RunArbiterHook` assume: a default export of `{ id, setup }`, `setup` registering through `ctx.tool.hook` for `execute.before` and `execute.after`, a thrown `Error` to block, and writable `event.input`. Record the `opencode --version` line. State whether v2.4.1 matches that shape, and name `tests/KyberWeave.Application.Tests/Squad/ArbiterOpenCodeRenderingTests.cs` as the test that pins the assumption.
  - A2 — scope contract. Confirm `AddArbiterPluginShim` and `DispatchHooks` in `src/KyberWeave.Application/Squad/Rendering/SquadHarnessRenderer.cs`, and `RenderArbiter` plus `ArbiterEnforced` in `src/KyberWeave.Application/Squad/Rendering/OpenCodeRenderer.cs`. The contract to confirm: project scope and global scope both render the owned plugin file, and a disabled wiring still renders the dispatch-hook shim at project scope. Cite `docs/adr/0026-kyber-utilities-owned-files-not-settings.md`, `docs/adr/0054-squad-owned-blocks-in-shared-hook-files.md`, the "Arbiter hooks" passage in `docs/kyber-squad/onboarding.md`, and `tests/KyberWeave.Application.Tests/Squad/ArbiterGlobalPluginDirRenderingTests.cs` (`RenderAsync_GlobalScope_OpenCode_EmitsOwnedAutoLoadedPluginFileUnderGlobalRoot`). The request's "project scope only" describes neither scope.
  - A3 — instance render. Confirm this mechanism and record it. `SquadLifecycleService` maps instance scope to a Global render against the instance root. `SquadInstanceService.EnsureAsync` builds `SquadInstallRequest` with no Arbiter wiring, so `AddArbiterPluginShim` writes no shim. Consequence: `squad run opencode` would launch a session with no Arbiter plugin, while a global install on the same machine wrote one under the user global root. Whether that global plugin is discovered when the config root is the instance is the spike in T2; this task only records the omission. Append one bullet to the arbiter-hook todo named in Files. The bullet names decision 4 of `docs/adr/0044-squad-lock-v2-and-instance-deployment-scope.md` and `docs/adr/0054-squad-owned-blocks-in-shared-hook-files.md` as the records that would have to change. Do not edit the renderer, the install request, or any ADR.
  - A4 — model profiles. Quote `opencode --version`, `opencode auth list` (provider names only), and `opencode models`. If auth shows no provider, or `opencode models` fails because none is signed in, record `BLOCKED (auth)` for A4, quote the error, and stop before the scratch install and the preset apply. Do not call the tokens a defect. Decision D22. Otherwise mark each bundled token `Verified present` or `ABSENT`. The tokens, copied from `products/kyber-squad/profiles/models.yml`, are `zai-coding-plan/glm-5.3`, `opencode-go/muse-spark-1.3-contributor` and `opencode-go/kimi-k2.7-code`. The request's example `zai-coding-plan/glm-5.4` is not in the file. Decision D20 is the apply: a scratch directory outside the repository, then `kyber-weave squad install` of target `opencode` with `--yes`, `kyber-weave squad preset show architect --target opencode`, the dry-run `use`, the real `use`, and `kyber-weave squad preset clear` of that scratch. Record the dry-run agent lines and the changed model line in one rendered agent file, then delete the scratch directory. An absent token, when models did return ids, is a finding and a PR note. Decision D11 forbids editing the profiles file.
- **Capabilities:** source reading across the renderer, shim and lifecycle layers; live CLI probing with `opencode` and `kyber-weave`; Markdown documentation under the governed-docs rules.
- **Difficulty:** easy
- **Session:** `opencode-spike`
- **Depends on:** none
- **Files:**
  - `docs/todo/squad-model-presets-windows-e2e-and-spikes.md`: new section "OpenCode instance support: audit evidence (2026-11-26)" under S1, holding the four verdicts from the objective.
  - `docs/todo/arbiter-global-hook-dispatch-coverage.md`: the A3 bullet from the objective, beside the existing dispatch-coverage items.
- **Commands:**
  - `kyber-weave doctor --ensure .`
  - `opencode --version`
  - `opencode auth list`
  - `opencode models`
  - `kyber-weave squad install "$SCRATCH" --target opencode --yes`
  - `kyber-weave squad preset show architect --target opencode`
  - `kyber-weave squad preset use architect "$SCRATCH" --target opencode --dry-run`
  - `kyber-weave squad preset use architect "$SCRATCH" --target opencode`
  - `kyber-weave squad preset clear "$SCRATCH" --target opencode`
  - `kyber-weave docs validate .`
  - `kyber-weave docs drift .`
- **Acceptance:**
  - A1–A3 each carry one of the three verdicts and the evidence supporting it. No verdict rests on expectation.
  - A2 quotes at least one ADR and one `docs/kyber-squad/` passage, and corrects the "project scope only" premise with the test that pins the global file.
  - A3's finding is in `docs/todo/arbiter-global-hook-dispatch-coverage.md` and names ADR 0044 decision 4 as the record to amend; no file under `src/` or `tests/` is modified.
  - A4 either quotes real `opencode models` output, marks each bundled token present or absent, and records the dry run and the real apply, or records `BLOCKED (auth)` and skips the install and the apply.
  - Both corpus gates report no finding for either edited file. Doctor ensure ran first.

### T2: Run the `OPENCODE_CONFIG_DIR` spike (S1) and record the result

- **Objective:** Determine, on this machine, whether `OPENCODE_CONFIG_DIR` layers OpenCode configuration with layer precedence and leaves credentials working, or replaces it, and record the result with the version, OS and date, or record BLOCKED with the exact error. Append "Spike S1 result" to the section T1 created. It opens with one line, exactly one of `PROMOTE: Ready`, `HOLD: precedence`, `PROPOSE: HomeReplace`, `UNSUPPORTED`, or `BLOCKED (<stage>)`. Do not edit catalog source. Decision D10 is the restore rule. Decision D9 is how a listing is observed.
  - B0. Record `opencode --version`, the host OS and the date, and the auth result from `opencode auth list` (provider names only) or from `opencode models` returning ids. Never paste a token, key or secret. If either prerequisite fails, write `BLOCKED (prerequisite)` with the exact error text and stop. Record the later steps as not run.
  - B1. Resolve the global root: the XDG config home's opencode directory, or the `.config/opencode` directory under the user's home when XDG is unset. Create only new files there. Record the pre-state of any path that already existed. Never edit or overwrite an existing file. Decision D21. `agents/probe-global.md` is exactly:

    ```markdown
    ---
    name: probe-global
    description: global-layer probe
    mode: subagent
    ---
    Global layer probe.
    ```

    For B4, `agents/probe-conflict.md` in that same root is the same shape with `name: probe-conflict` and `description: global-layer conflict probe`, and the body line `Global layer conflict probe.`
  - B2. Use a scratch directory outside the repository and outside the real global root. Record the exact `OPENCODE_CONFIG_DIR` value. `agents/probe-layer.md` there is the same shape with `name: probe-layer`, `description: instance-layer probe`, and the body line `Instance layer probe.` The scratch copy of `agents/probe-conflict.md` uses `description: instance-layer conflict probe` and the body line `Instance layer conflict probe.`
  - B3. Record whether `probe-global` and `probe-layer` are both visible, quoting the listing. Name the command that produced it. Use only a non-interactive verb from `opencode --help` that does not start a provider session. If no such verb lists loaded agents, write `BLOCKED (method)` with the commands tried. Do not call a model. Decision D9.
  - B4. Record which `probe-conflict` description OpenCode loads, quoted verbatim.
  - B5. Record whether `opencode models` still returns ids with `OPENCODE_CONFIG_DIR` set, and where auth state lives, from the paths the harness reports. Expect the data directory, and write what was observed.
  - B6. Repeat the instance probe with `XDG_CONFIG_HOME` pointing at a temporary tree and `OPENCODE_CONFIG_DIR` unset. Record whether the layered agent still loads.
  - B7. Remove every file created under the real global root, list each as removed, and check that each is gone. A run that cannot remove one writes `BLOCKED (restore)` and names the path.
  - B8. Map B3–B6 in one sentence. Layered merge, layer precedence and intact credentials select `PROMOTE: Ready`. Layered merge with the wrong precedence selects `HOLD: precedence` and states the smallest fix; do not promote. Replacement or broken credentials select `PROPOSE: HomeReplace` when the XDG fallback is sound, and `UNSUPPORTED` when it is not.
- **Capabilities:** careful live CLI probing of a third-party harness in a scratch directory, restoring every change to the user's real global configuration, and Markdown documentation under the governed-docs rules.
- **Difficulty:** medium
- **Session:** `opencode-spike`
- **Depends on:** T1 (the same file, and the Arbiter verdict the consequence sentence cites)
- **Files:**
  - `docs/todo/squad-model-presets-windows-e2e-and-spikes.md`: the "Spike S1 result" section specified in the objective.
- **Commands:**
  - `kyber-weave doctor --ensure .`
  - `opencode --version`
  - `opencode auth list`
  - `opencode models`
  - `opencode --help`
  - `kyber-weave docs validate .`
  - `kyber-weave docs drift .`
- **Acceptance:**
  - The section opens with exactly one outcome line. This task does not edit catalog source.
  - Version, OS and date are stated. A BLOCKED outcome quotes the failing command's exact stderr.
  - The scratch directory is named and is outside the repository and outside the real global root.
  - Every file created under the real global root is listed as removed and the final check confirms it. `BLOCKED (restore)` is used if not.
  - No secret, key or token appears in the recorded output.
  - Both corpus gates report no finding for the edited file.

### T3: Apply the OpenCode catalog outcome S1 recorded

- **Objective:** Change the OpenCode catalog row only when T2's outcome line is `PROMOTE: Ready` or `UNSUPPORTED`, with the contract tests updated in the same change. Ready makes `kyber-weave squad run opencode` provision an isolated instance. Unsupported keeps the launch refusal and records the observed reason. Any other outcome line changes nothing. Do not edit `tests/KyberWeave.Application.Tests/Squad/Deployment/SquadGlobalRootsTests.cs`: `ResolveOpenCodeRoot` already honours the variable chain and that test already pins it.
- **Capabilities:** C# domain and CLI behaviour with xUnit unit tests over the shared fakes.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `opencode-promote`
- **Depends on:** T2 (the outcome line that gates the change)
- **Files:**
  - `src/KyberWeave.Application/Squad/Instances/SquadInstanceCatalog.cs`: the `SquadTarget.OpenCode` entry in `CatalogRows`. Ready branch: status `Ready`, reason null, kind `ConfigLayer`, variable `OPENCODE_CONFIG_DIR`. Unsupported branch: status `Unsupported`, kind `None`, variable null, reason a sentence quoted from T2's observation, with no secret in it. Both branches keep executable `opencode` and the empty link collections. Change no other entry.
  - `tests/KyberWeave.Application.Tests/Squad/Instances/SquadInstanceCatalogTests.cs`: Ready branch adds OpenCode in `ReadyTargetsMatchDesignSpecification` and drops it from `SpikePendingTargetsMatchDesignSpecification`, which keeps Kilo, Cursor and ZCode. Unsupported branch adds OpenCode in `UnsupportedTargetsMatchDesignSpecification`, drops it from the SpikePending roster, and leaves the Ready roster unchanged. Do not edit `AcceptanceCriterion7_7_HarnessWithNoUsableVariableIsMarkedUnsupportedWithExplanatoryReason`; the catalog shape makes it pass. Keep every assertion the branch does not have to move.
  - `tests/KyberWeave.Cli.Tests/SquadRunCommandTests.cs`: on either branch, retarget `SpikePendingHarnessExitsWithCodeTwoAndNamesReason` from OpenCode to Kilo, leaving its exit-code assertion. Ready branch only: add `OpenCodeReadyRowSetsTheInstanceConfigDirEnvironmentVariable`, copying `ConcurrentRunsExecuteWithIsolatedInstanceRoots`, asserting the captured environment value `OPENCODE_CONFIG_DIR` equals the instance root the ensure override returned, and that the exit code is passed through. Unsupported branch adds no launch test.
  - `tests/KyberWeave.Application.Tests/Squad/Instances/SquadInstanceServiceTests.cs`: edit only an assertion the branch makes wrong, and name it in the report.
  - `tests/KyberWeave.Data.Tests/Squad/SquadInstanceLinkSyncTests.cs`: edit only an assertion the branch makes wrong, and name it in the report.
- **Design (the contract this changes):** `SquadInstanceStatus` is read by `SquadRunCommand.Execute` (exit 2 versus launch), `SquadHarnessLauncher.PrepareLaunch` and `ProcessSquadHarnessLauncher` (a refusal for a non-Ready row), `SquadInstanceLinkSync`, and four documents. Ready starts provisioning. Unsupported keeps the refusal, nulls the variable, sets kind `None`, and supplies the reason `UnsupportedHarnessExitsWithCodeTwoAndNamesReason` already prints for Warp. `AcceptanceCriterion7_7_HarnessWithNoUsableVariableIsMarkedUnsupportedWithExplanatoryReason` is the contract that forces that shape. The tests prove the wiring. Only T2's live evidence proves the isolation. If the ensure override is not how `ConcurrentRunsExecuteWithIsolatedInstanceRoots` returns an instance root, copy that test rather than inventing a new double. Decision D3 is the branch guard. Decision D18 names the readers.
- **Commands:**
  - `kyber-weave review gates . --agent <your agent name> --task T3 --red`
  - `kyber-weave review gates . --agent <your agent name> --task T3`
- **Acceptance:**
  - The first step quotes T2's outcome line. For `HOLD: precedence`, `PROPOSE: HomeReplace` or any `BLOCKED` line, change nothing, commit nothing, and report `NOT_APPLICABLE` quoting that line and naming decision D3.
  - For `PROMOTE: Ready` or `UNSUPPORTED`, the Test-contract row's two filters pass with a non-zero total, and the RED run is recorded first with its failing assertions.
  - Ready: `GetRow(OpenCode)` reports Ready, ConfigLayer, `OPENCODE_CONFIG_DIR`, and a null reason. Unsupported: kind None, variable null, status Unsupported, and a non-empty reason that quotes the observation. No other row's status, kind, variable or reason differs from `c603a303b`.
  - No `ExtraLinks` or `NeverLinked` entry is added unless the spike observed it. A non-empty entry quotes the observation in the report.
  - The report names every existing test it edited and why, and lists the reader classes it re-checked.

### T4: State OpenCode's recorded outcome on every documentation surface that carries it

- **Objective:** Make the instance support matrix, the model-profiles readiness sentence, the spikes todo and the todo index all state the outcome T2 recorded — whether that is a promotion, a `HomeReplace` proposal, `Unsupported`, or an unchanged `SpikePending` — with no claim the evidence does not support.
- **Capabilities:** documentation in Markdown under the governed-docs rules.
- **Difficulty:** easy
- **Session:** `opencode-docs`
- **Depends on:** T2 (the recorded outcome), T3 (the catalog status the matrix must match)
- **Files:**
  - `docs/kyber-squad/onboarding.md`: § 3 "Harness Instance Support Matrix". The Executable cell stays `opencode`. When the catalog kind or variable changed, update Kind, Environment Variable, Status, and Behavior and Isolation together. An `UNSUPPORTED` outcome uses Kind `None`, Environment Variable `*None*`, Status `Unsupported`, and a Behavior cell that quotes the observed reason, matching the other Unsupported rows. A Ready outcome keeps Kind `ConfigLayer` and Environment Variable `OPENCODE_CONFIG_DIR`, sets Status `Ready`, and quotes the observed behaviour, with the OpenCode version in that cell or an adjacent sentence. Reword the status definitions only if the outcome needs a distinction they do not already draw. Leave the `kilo`, `cursor` and `zcode` rows exactly as they are.
  - `docs/kyber-squad/model-profiles.md`: § "Multiple profiles at once" — the sentence naming which targets are `Ready` for isolated instances, so it matches the catalog; and § "Per-harness token shapes" only if T1's A4 verdict found a token that does not resolve.
  - `docs/todo/squad-model-presets-windows-e2e-and-spikes.md`: in the S1 item, either mark S1 done — writing the OpenCode version, the host OS, the date and the verdict line T2 recorded — or restate in one sentence why it remains unresolved and what observation would settle it. Then correct "Next steps and resolution criteria" only where S1's own item is now settled.
  - `docs/todo/README.md`: the `squad-model-presets-windows-e2e-and-spikes.md` row's description, matching what S1 now records.
  - `docs/adr/0044-squad-lock-v2-and-instance-deployment-scope.md`: only when the outcome is `UNSUPPORTED`, a dated amendment that OpenCode cannot be isolated, with the observed reason. Decision D15. Leave this file unmodified for Ready, BLOCKED and a HomeReplace proposal. Touch no other ADR.
- **Commands:**
  - `kyber-weave doctor --ensure .`
  - `kyber-weave docs validate .`
  - `kyber-weave docs drift .`
- **Acceptance:**
  - Kind, Environment Variable, Status, and Behavior and Isolation all match `SquadInstanceCatalog` after T3. The spikes todo states T2's outcome line, including a HomeReplace proposal, a precedence hold, or a BLOCKED line, without claiming a catalog change T3 did not make.
  - On `HOLD: precedence`, `PROPOSE: HomeReplace` or BLOCKED, Kind stays `ConfigLayer`, Environment Variable stays `OPENCODE_CONFIG_DIR`, and Status stays `SpikePending`. The behavior cell quotes the observation. On HomeReplace it also states the proposal. On `HOLD: precedence` it states the smallest fix.
  - The matrix's other rows and the status definitions are byte-identical to `c603a303b` except where the outcome required a rewording, which the report names.
  - ADR 0044 is unmodified unless the outcome is `UNSUPPORTED`.
  - Both corpus gates report no finding for the edited files.

## Dependency graph

```
T1 (audit, docs)  ──►  T2 (live spike, docs)  ──►  T3 (promotion, csharp)  ──►  T4 (docs surfaces)
```

Each edge is a real dependency: T2 appends to the section T1 created; T3 is gated on T2's outcome
line; T4 states the status T3 leaves in the catalog. The chain is serial by construction, so the run
uses one worker at a time and `review.max-concurrent-workers` (4) is never reached.

## Risks

- **The spike leaves a probe agent in the user's real OpenCode global config.** Mitigation: D10 plus T2's B7 step — only new files are created, every one is listed as removed, and a failed removal reports `BLOCKED (restore)`.
- **A status is flipped on a plausible reading rather than an observation.** Mitigation: T2's section opens with one outcome line, and T3 changes the row only for `PROMOTE: Ready` or `UNSUPPORTED`, quoting that line. Decision D3 records why.
- **OpenCode exposes no non-interactive agent listing, so the merge test cannot be observed.** Mitigation: D9 — record `BLOCKED (method)` and leave the row alone; the method gap is a useful result for the next run.
- **The promotion changes behaviour for a target whose isolation was never exercised by a test.** Mitigation: the live evidence is the justification and D18 states the readers; the new env-var test pins the wiring a future regression would break.
- **A bundled `opencode` token does not resolve on this host.** Mitigation: D11 — report it, do not edit the shipped matrix.
- **T3 is a no-op and the plan looks incomplete.** It is not: the request makes "unchanged, documented" a successful outcome, and T4 is the deliverable for that branch.

## Out-of-scope boundaries

- Any change to the Kilo, Cursor, ZCode, Antigravity, Factory, CommandCode, Devin or Warp catalog rows, and any spike S2–S8. Those remain open in `docs/todo/squad-model-presets-windows-e2e-and-spikes.md`.
- Any change to the Arbiter renderer, the shim, the instance install request, or ADR 0044/0054 to give `squad run` an Arbiter hook. Recorded as a finding instead (D17).
- `docs/archive/` in any form, `docs/documentation-ontology.md`, and `.kyber-weave/kyber-weave.yml` — including any ontology or schema widening to clear a validation finding.
- The generated Config Reg block in `AGENTS.md`.
- `products/kyber-squad/profiles/models.yml`, even when a token does not resolve (D11).
- PR note: `scripts/proc-guard.sh` no longer exists, so the request's wrapped verification forms are recorded as stale in the pull request rather than reproduced (D5).
- PR note: `docs/cli-command-surface.md` has no `squad run` row although `squad run` is documented in `docs/kyber-squad/onboarding.md` (D12).
- PR note: `docs/todo/arbiter-global-hook-dispatch-coverage.md` now carries the instance-render finding, which needs a decision on whether an instance install should pass an Arbiter wiring (D17).
- PR note: a `PROPOSE: HomeReplace` verdict is a proposal in the spikes todo. This change does not switch the catalog kind or amend ADR 0044 for it, because the request says propose that mechanism and the ADR states only a decision the catalog implements (D15).

## Verification gates

The host's gates, run as declared, with no process wrapper:

- Per task, the developer gates for the task's technology and difficulty tier:
  `kyber-weave review gates . --agent <agent name> --task <id>` and, in test-first mode,
  `... --task <id> --red` first. The gate runs the Test contract's runner command, so no task lists a
  separate `dotnet test` step as an extra command.
- T1, T2 and T4 are documentation and probe tasks with no code gate. Each runs `kyber-weave doctor --ensure .` and then the corpus gates, `kyber-weave docs validate .` and `kyber-weave docs drift .`, in the run worktree (D23).
- Targeted verification beyond the per-task gates, for whoever assembles the final report:
  `dotnet build KyberWeave.sln -c Release`, then
  `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.SquadInstanceCatalogTests"`
  and the same for `SquadRunCommandTests` in `tests/KyberWeave.Cli.Tests`. Every run must report a
  non-zero test total.
- `scripts/fast-loop.sh --class 'SquadInstance*'` may be used while iterating and is never cited as
  evidence (D6). `scripts/proc-guard.sh` does not exist (D5).
- `docs validate . --merge-ready` is not run during this plan's life; it fails by design on an open
  plan (D7). It runs in CI and at closeout, after the archive.

## Review

The end-of-run review council reviews the change, governed by the `code-review` skill: the council
derives its own command, measured against `--base` and admitted to the full-suite gates with
`--council`. No command is copied into this plan. The reviewer should judge D18 first — whether the
evidence justifies flipping a status that four code paths and four documents read — then the
documentation surfaces for claims that outrun the observed behaviour.

## docs-dev closeout

`docs-dev` harvests the plan at closeout: the Design harvest table names each destination, the
`## Decisions` table is preserved intact into the archive, the two `docs/todo/README.md` rows and the
S1 section keep the evidence a future spike reads, and the completion summary maps every decision to a
destination or a recorded waiver. The task file `<plan>.tasks.yml` is removed in the same commit that
archives the plan. `kyber-weave docs closeout <plan> --archive` performs the move once the evidence is
complete; `docs validate . --merge-ready` and `docs drift .` are re-run afterwards, and the standards
warning period (`KW-DOC-SPEC-012`, `KW-DOC-SPEC-013`, `KW-CONFIG-REG-001`, `KW-CONFIG-REG-002`) is
cleared there with `kyber-weave docs standards migrate`, committed with the closeout.

## Closeout mapping

| ID | Destination | Documentation-impact |
|---|---|---|
| H1, D3, D12, D16, D18 | [Kyber-Squad adoption and usage guide](../kyber-squad/onboarding.md#3-harness-instance-support-matrix) | extends-component; impact-components: KyberSquad; last-reviewed 2026-11-26; status archived. The OpenCode row records the 2.4.1 observation on Darwin 27.3.0, `PROPOSE: HomeReplace`, kind `ConfigLayer`, variable `OPENCODE_CONFIG_DIR`, and status `SpikePending`. `squad run` for that status still exits 2. Kilo, Cursor, ZCode, and the Unsupported rows are unchanged. `docs/cli-command-surface.md` has no `squad run` row; the runnable-target contract stays on this page. |
| H2 | [Model profiles and model presets](../kyber-squad/model-profiles.md#multiple-profiles-at-once) | extends-component; impact-components: KyberSquad; last-reviewed 2026-11-26; status archived. Ready isolated instances are `claude`, `codex`, `copilot`, and `pi`. OpenCode stays `SpikePending` for the same observed replacement. |
| H3, H4, D2, D8, D9, D10, D11, D14, D20, D21, D22 | [Windows E2E and harness spikes](../todo/squad-model-presets-windows-e2e-and-spikes.md) and [Todos](../todo/README.md) | extends-component; impact-components: KyberSquad; last-reviewed 2026-11-26; status archived. S1 records the probe method, OpenCode 2.4.1, Darwin 27.3.0, 2026-11-25, `PROPOSE: HomeReplace`, and that the catalog row was not edited. The index row says the same. No ADR amendment. |
| D15 | [ADR 0044: Squad Lock v2 and Instance Deployment Scope](../adr/0044-squad-lock-v2-and-instance-deployment-scope.md) | extends-component; impact-components: KyberSquad; the ADR's own last-reviewed stays 2026-10-25 because this outcome does not amend it. ConfigLayer remains the decision the catalog implements. |
| H5, D17 | [Confirm that sub-agent dispatch fires the global Arbiter before-hook](../todo/arbiter-global-hook-dispatch-coverage.md) | Component KyberArbiter; last-reviewed 2026-11-26; status archived. An instance install passes no Arbiter wiring, so it writes no `plugins/kyber-arbiter.ts`. A later change would amend ADR 0044 decision 4 and ADR 0054. |
| D1, D4, D5, D6, D7, D13, D19, D23, AP1 | Documentation waiver — these decide how this run was planned and checked: plan versus specification, no Technology line on documentation tasks, commands run with no process wrapper, fast-loop never cited as evidence, `docs validate` without `--merge-ready` while the plan is open, difficulty ratings, which backtick paths are file scope, `doctor --ensure` before the corpus gates, and the approval to execute. None of them adds a contract an operator or a later change consults. | extends-component; impact-components: KyberSquad; last-reviewed 2026-11-26; status archived |

no ADR — ADR waiver: `PROPOSE: HomeReplace` leaves `SquadInstanceKind.ConfigLayer` in the catalog, so ADR 0044 stays the decision in force and is not amended (D15, H4).

## Completion summary

- T1 (`7caada9d`): Arbiter path verdicts are correct as designed for the plugin shape and for project-and-global rendering, and a gap with a decision needed for the unwired instance install. Model profiles are correct as designed; the three bundled OpenCode tokens were present in `opencode models`. `architect` is a profile category, not a preset file, so the preset apply recorded that and edited no profile.
- T2 (`31505149`): `PROPOSE: HomeReplace` on OpenCode 2.4.1, Darwin 27.3.0, 2026-11-25. `OPENCODE_CONFIG_DIR` replaces the config root. Credentials stayed in the data directory. The XDG fallback is the same replacement and did load an agent. Probe files under the real global root were removed.
- T3: `NOT_APPLICABLE`. The outcome line is `PROPOSE: HomeReplace` (D3). `SquadInstanceCatalog` still has OpenCode `SpikePending`, `ConfigLayer`, `OPENCODE_CONFIG_DIR`, reason null. No file under `src/` or `tests/` differs from `c603a303b`.
- T4 (`075eeea8`, plus this closeout): the instance matrix, the model-profiles Ready sentence, the spikes todo, and the todo index state that outcome. ADR 0044 is unmodified.
- NOT VERIFIED: a HomeReplace implementation for OpenCode — the spike recorded the proposal and did not change the catalog kind.
