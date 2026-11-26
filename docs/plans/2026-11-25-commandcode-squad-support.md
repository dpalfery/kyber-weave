---
id: plans/2026-11-25-commandcode-squad-support
title: Command Code support in Kyber-Squad — model profiles, Arbiter enforcement, and the squad run verdict
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-26
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - KyberSquad
  - KyberArbiter
---

# Command Code support in Kyber-Squad — model profiles, Arbiter enforcement, and the squad run verdict

**Status: Ready, 2026-11-26.**

## Problem and goal

Command Code (Squad target token `commandcode`, CLI `cmd`) is a registered Kyber-Squad harness with a renderer, but three capabilities are missing:

1. **Model profiles** — no profile in the bundled matrix `products/kyber-squad/profiles/models.yml` pins a `commandcode` model, so every rendered `.commandcode/agents/<name>.md` inherits the session model.
2. **Arbiter enforcement** — the Squad Arbiter wiring classes Command Code `dispatch-unconfirmed` at both project and global scope and renders no gating artifact, so `agent` dispatches on Command Code are ungated.
3. **`squad run`** — the instance catalog row is `Unsupported` with a reason that does not name its evidence.

Goal: ship a verified `commandcode` model column with renderer and preset evidence; render a Command Code mod that gates the `agent` dispatch tool through the Arbiter hook host with live-verified block behaviour honestly recorded; and leave `squad run commandcode` with an evidence-backed verdict — `Ready` only if a safe, live-proven mechanism exists, otherwise a documented `Unsupported`.

## Request

The dispatch carried this request verbatim:

> # Task: Full Command Code support in Kyber-Squad — model profiles, Arbiter enforcement, `squad run`
>
> ## Bottom line
>
> Command Code is a registered Squad harness with a renderer, but three capabilities are missing. Deliver them in this order, because each depends on the previous:
>
> 1. **Model profiles** — add a `commandcode:` column to `products/kyber-squad/profiles/models.yml` and prove the renderer emits it. Small and self-contained.
> 2. **Arbiter enforcement** — render a project/global Command Code *mod* that gates the `agent` dispatch tool, and register the harness in the Arbiter hook host. The repo currently classes Command Code `dispatch-unconfirmed` at both scopes.
> 3. **`squad run`** — investigate config isolation, implement it only if a safe mechanism exists, and otherwise record an evidence-backed `Unsupported` verdict.
>
> Work in the checkout at `/Users/dave/git/personal/copilot/kyber-weave`. Read `AGENTS.md` first. This is a multi-component change: author or extend a plan under `docs/plans/` using the repository's plan format before writing code, and keep the plan's decisions current. Do not rewrite the approved historical plan `docs/plans/2026-11-04-arbiter-global-install.md`; record superseding decisions in a new ADR or architecture-doc entry and reference it.
>
> ## Ground rules
>
> - `git status` first. The tree may hold unrelated work; never revert or reformat it.
> - Run every test, build, and gate through `scripts/proc-guard.sh run --`.
> - Use `scripts/fast-loop.sh` for the inner loop; it is not gate evidence.
> - Any edit under `docs/` requires `docs validate . --merge-ready` and `docs drift .`.
> - Do not edit the generated Config Reg block in `AGENTS.md`.
> - Do not widen a schema or ontology to make a validation failure disappear.
> - Add new `KW-*` rule ids; never reuse or renumber existing ones.
>
> ## Verified starting state (2026-11-26, command-code 1.88.2)
>
> **Model profiles**
> - `products/kyber-squad/schemas/model-profiles.schema.json` already allows a `commandcode` key.
> - `products/kyber-squad/profiles/models.yml` has no `commandcode:` key in any profile, so every profile's `default: inherit` applies.
> - `CommandCodeRenderer` uses `SquadModelPolicy.InheritOrBlankOmitted` and splits an optional `[reasoningEffort=off|low|medium|high|xhigh|max]` suffix onto the `reasoningEffort` frontmatter key.
> - Result today: rendered `.commandcode/agents/*.md` files carry no `model:` key.
>
> **Arbiter**
> - `ArbiterHookWiring` lists hooked targets and special-cases `commandcode` to `dispatch-unconfirmed` at both project and global scope.
> - `CommandCodeRenderer` implements no `RenderArbiter` override, so nothing is written.
> - `docs/plans/2026-11-04-arbiter-global-install.md` decision R-D1 records that the design check between Command Code's two hook seams was never completed, and that the outcome is owed to `docs/kyber-arbiter/architecture.md`.
> - Command Code has two seams: **settings.json hooks** (matchers cover only `SHELL`, `READ`, `WRITE`, `EDIT`; they cannot see or block the `agent` dispatch tool) and **mods** (`cmd.hooks({beforeToolCall, afterToolCall})` fires for every tool call, including `agent`, with the tool name and input, and can `block`; the only seam that can gate a dispatch).
> - The `agent` tool input is `{description, prompt, subagent_type, model?, run_in_background?}`; `subagent_type` is the agent file name.
> - The Arbiter host already understands a generic plugin envelope `kyber-arbiter.plugin-event/v1` (`PluginEnvelope`), and `PluginHookAdapters` maps plugin harnesses to `(token, dispatchTool)` reading `args.subagent_type` and `args.prompt`. Today that list is `opencode|task`, `kilo|task`, `pi|Agent`.
> - Known limit: the hook host receives no caller identity from a Command Code mod. A callerless hook is evaluated as caller `unidentified` by `PluginHookAdapter.Handle`. Whether that is acceptable must be established, not assumed.
>
> **`squad run`**
> - `SquadInstanceCatalog` has Command Code as `None / Unsupported` with the reason "does not support configuration or home directory redirection via environment variable".
> - `SquadGlobalRoots` resolves the Command Code root as `~/.commandcode` via the OS home, with no override variable.
> - `SquadHarnessLauncher.BuildStartInfo` refuses any row that is not `Ready`, which is why `squad run commandcode` exits 2 today.
> - Verified in command-code 1.88.2: there is no config-directory environment variable and no XDG layer; the CLI resolves `~/.commandcode` from the OS home. `cmd --config key=value` sets settings only. `--add-dir`, `--skill`, and `--mod` do not relocate agents.
>
> ## Work item 1 — model profiles
>
> 1. Add a `commandcode:` key to all profiles in `models.yml`, keeping parity with an existing column; verify against `cmd --list-models` and the per-model effort set before committing.
> 2. Extend the Command Code renderer contract tests to assert that a profile with a `commandcode` value renders `model:` and `reasoningEffort:` into `.commandcode/agents/<name>.md`, and that `inherit` omits both.
> 3. Update `docs/kyber-squad/model-profiles.md`: add a `commandcode` row to the per-harness token-shape table and any target list, and describe the reasoning-effort suffix.
> 4. Update `docs/kyber-squad/onboarding.md`, "Command Code notes → Models": remove "No profile pins one yet" and state the shipped values.
> 5. Run `kyber-weave squad preset show <name> --target commandcode` and a real apply in a scratch project; confirm the rendered agent files carry the pinned model.
>
> ## Work item 2 — Arbiter enforcement
>
> 1. **Implement the mod renderer.** Add a Command Code-specific mod body (do not reuse `ArbiterPluginShim`, which is Bun/OpenCode-specific). It must: default-export a factory taking the ModApi and registering `beforeToolCall` and, if expressible, `afterToolCall`; act only on `toolName === 'agent'`; build the `kyber-arbiter.plugin-event/v1` envelope; spawn `kyber-weave-arbiter hook --harness commandcode` (add `--scope global` for a global install) with the envelope on stdin using `node:child_process` with no shell; translate `{decision: "block", reason}` into `{block: true, additionalContext: reason}` and a stripped `args` into `{input: args}`; fail closed when the spawn or parse fails. Render it at project scope as `.commandcode/mods/kyber-arbiter.ts` and at global scope as `mods/kyber-arbiter.ts` relative to the Command Code global root, following the `AddArbiterPluginShim(projectPath, globalRelativePath)` precedent.
> 2. **Register the harness in the hook host.** Add `("commandcode", "agent")` to the `Entries` table in `PluginHookAdapters`.
> 3. **Change the Squad classification.** Add `SquadTarget.CommandCode` to `HookedSquadTargets` in `ArbiterHookWiring.cs`, delete the `commandcode` `dispatch-unconfirmed` special cases, and add a `TrustSteps` entry explaining that project mods are trust-gated while user-scope mods load without a prompt. Update every test that pins the old classification.
> 4. **Prove the behaviour, then record it.** Evidence required before claiming parity: show `beforeToolCall` fires for the `agent` tool and that `input.subagent_type` is the canonical agent name (run `cmd --mod .commandcode/mods/kyber-arbiter.ts`, use `--debug` and observe `tool_hook_blocked`); show a guarded worker dispatch is blocked and the model receives the Arbiter reason; determine the caller-identity question empirically — if the mod cannot identify the caller, do not claim per-caller parity: gate by target worker only and record a degradation, or state the limitation in the architecture doc if the engine's rules still enforce correctly with `unidentified`. Record the design-check outcome (mods versus settings.json, and why settings cannot gate `agent`) in `docs/kyber-arbiter/architecture.md`, including the new matrix row and harness capability table, referencing the superseded R-D1 decision. Any change to the owned-artifact list or hook semantics needs the matching ADR amendment (ADR 0026 for Arbiter artifacts, ADR 0053 for degradation codes).
> 5. Add tests: a contract test for the emitted mod body and its project/global paths, a test that the registry serves `commandcode`, and a classification test proving Command Code is no longer degraded (or is degraded for the documented reason).
>
> ## Work item 3 — `squad run`
>
> Investigate in this order and stop at the first option that is safe and provable: (1) documented config redirection — confirm there is no supported variable or flag in 1.88.2 (`cmd --help`, `cmd info`, the bundled docs, the settings schema); if one exists, use `ConfigLayer`, set the row `Ready`, and add a launcher test. (2) Home replacement — set `HOME` to the instance root and follow the `HomeReplace` shape; this is a design change, not a config tweak (`SquadGlobalRoots.ResolveGlobalRoot(CommandCode)` returns `~/.commandcode`, not the home, so the existing link-sync source is wrong for this row; add an explicit mechanism instead of overloading the existing one; the instance home must link every top-level entry of the real home except `.commandcode`, and give the instance `.commandcode` its own rendered agents and settings plus a link or copy of `auth.json`; prove `git`, `ssh`, `npm`, and the shell tool still behave; requires an ADR 0044 amendment; do not do it as an unrecorded hack). (3) No safe mechanism — keep the row `Unsupported` with a reason that names the evidence, and document the investigation; that is an acceptable outcome, a fabricated `Ready` is not.
>
> If the row becomes `Ready`, add a live `squad run commandcode` verification that the harness starts, loads the preset's agents, and propagates the harness exit code, updating `SquadInstanceCatalogTests`, `SquadRunCommandTests`, `SquadHarnessLauncherTests`, `docs/kyber-squad/onboarding.md` (support matrix), `docs/kyber-squad/model-profiles.md` (the `Ready` list), `docs/todo/` if a deferred item is closed, and `docs/cli-command-surface.md` if it enumerates runnable targets.
>
> ## Verification
>
> Run the Command Code, SquadInstance, Arbiter and docs test sets with non-zero totals; do not cite `fast-loop.sh` as gate evidence.
>
> ## Definition of done
>
> Six profiles carry a verified `commandcode` model, with renderer and preset evidence; a rendered Command Code mod gates `agent` dispatches, the hook host serves `--harness commandcode`, and the observed block behaviour is recorded with command output, with any caller-identity limitation recorded honestly; `docs/kyber-arbiter/architecture.md` contains the design-check record and matrix row, and affected ADRs and the onboarding/model-profile docs are updated; `squad run commandcode` either works end to end with tests and docs, or remains `Unsupported` with a documented, reproducible reason; targeted tests, `docs validate`, and `docs drift` are clean.
>
> ## Do not
>
> Claim Arbiter parity without showing the dispatch block in a live session; write a `Ready` instance row without a successful live launch; reuse the OpenCode/Bun shim as the Command Code mod body; render the mod at instance scope or claim it applies there unless the instance render actually contains it; rewrite approved historical plans, or edit generated or archived docs.
>
> Two things to watch: the model mapping is a proposal grounded in column parity, so the agent may justify different ids — that's fine as long as it verifies them with `cmd --list-models`; and the Arbiter caller-identity limit is the genuine unknown, which is why a recorded degradation is an acceptable outcome instead of forcing a parity claim.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| Add a `commandcode:` key to all profiles in `models.yml`, parity with an existing column | T1 | Six profile keys land in `products/kyber-squad/profiles/models.yml`; D2 records the values and parity |
| Verify ids and effort levels against `cmd --list-models` before committing | T1 acceptance | `cmd --list-models` output recorded in the task evidence; D2 authorizes verified substitution of a wrong id |
| Extend the renderer contract tests: pinned value renders `model:` + `reasoningEffort:`, `inherit` omits both | T1 Test contract | New cases in `CommandCodeRendererContractTests` |
| Update `model-profiles.md` token-shape row and target lists, describe the effort suffix | T1 files | Row at `docs/kyber-squad/model-profiles.md` (commandcode token row) rewritten with the shipped shape |
| Update `onboarding.md` "Command Code notes → Models" (remove "No profile pins one yet") | T1 files | Paragraph at the Command Code notes states the shipped values |
| Run `squad preset show --target commandcode` and a real apply in a scratch project | T1 acceptance | Command output and rendered agent files recorded in the task evidence |
| Implement the Command Code mod renderer (envelope, spawn, block translation, fail closed, project + global paths; not `ArbiterPluginShim`) | T2 | `CommandCodeRenderer.RenderArbiter` + mod body, contract tests for body and paths |
| Register `("commandcode", "agent")` in `PluginHookAdapters.Entries` | T2 files | Registry test proves `commandcode` is served with the plugin deny dialect |
| Add `SquadTarget.CommandCode` to `HookedSquadTargets`; delete the `dispatch-unconfirmed` special cases; add a `TrustSteps` entry | T2 files (per-scope, evidence-gated per D5) | Classification tests; TrustSteps entry printed by `WriteArbiterTrustSteps` |
| Update every test pinning the old classification (grep `dispatch-unconfirmed` under `tests/`) | T2 files | All five pinned test classes updated |
| Prove live: `beforeToolCall` fires for `agent`, `subagent_type` is canonical, block observed via `--debug` / `tool_hook_blocked` | T2 acceptance | Live command output in the task evidence |
| Determine the caller-identity question empirically; record degradation or limitation honestly | D4 + T2 acceptance + T4 | Recorded outcome in architecture.md and the completion summary |
| Record the design-check outcome (mods vs settings.json) and matrix row in `kyber-arbiter/architecture.md`, referencing superseded R-D1 | T4 files | Design-check record and rewritten matrix row |
| ADR amendments for owned artifacts and degradation codes (0026, 0053) | T4 files | Amendment notes in both ADRs |
| Tests: mod body/path contract, registry serves `commandcode`, classification no longer degraded (or degraded for documented reason) | T2 Test contract | Three named test surfaces green |
| `squad run` investigation in order; implement only if safe and provable; otherwise evidence-backed `Unsupported` | D6 + T3 | Catalog row, tests, onboarding row name the evidence; no `Ready` claim without live launch |
| Update `SquadInstanceCatalogTests`, `SquadRunCommandTests`, `SquadHarnessLauncherTests`, onboarding support matrix, model-profiles `Ready` list, `docs/todo/`, `cli-command-surface.md` — if the row becomes `Ready` | T3 (conditional branch, taken only on a documented, isolated config redirection) | The branch carries the `ConfigLayer` row, the launcher test, the live `squad run` launch and every listed update; the expected path stays `Unsupported` per D6, where only the reason-pinning surfaces change — `cli-command-surface.md` does not enumerate `squad run` targets (verified), so it needs no change in either outcome |
| Verification: targeted classes with non-zero totals; `fast-loop.sh` not cited as evidence | Test contract + Verification gates | Filters named per class; gates are task-gate runs |
| Do-not list (no parity claim without live block; no `Ready` without live launch; no shim reuse; no instance-scope mod claim; no rewriting historical plans/generated/archived docs) | Out-of-scope boundaries + D8 | Boundaries section |

## Scale

`documentation-impact: extends-component`; `impact-components: KyberSquad, KyberArbiter`. The change extends two existing components: KyberSquad (bundled model matrix, renderer, Arbiter wiring, instance catalog) and KyberArbiter (plugin hook adapter registry, architecture record, ADR amendments).

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Which of the request's "verified starting state" facts hold in this worktree? | All behavioural claims hold; the request's paths and profile names do not. Verified in this tree: Squad code lives under `src/KyberWeave.Application/Squad/...` (namespaces `KyberWeave.Core.Squad.*`), not `src/KyberWeave.Core.Squad/...`; renderer contract tests live in `tests/KyberWeave.Application.Tests/CommandCodeRendererContractTests.cs`, not `tests/KyberWeave.Tests/...`; `models.yml` has six profiles (`architect`, `deep-planning`, `dev-hard`, `fast`, `general`, `reviewer`) — the request's `orchestration` profile does not exist here (removed before this branch), and its row is covered by `dev-hard`, which takes the architect-family pin. | Re-verified per the routing note against revision `c603a303` in this worktree; every task names this tree's paths. | ANSWERED — code evidence (`SquadTarget.cs`, `models.yml`, `ls src tests`) |
| D2 | Which `commandcode` value does each profile pin? | Bundled column: `architect`, `deep-planning`, `dev-hard` → `gpt-6.2-sol[reasoningEffort=high]`; `fast` → `gpt-6-luna[reasoningEffort=low]`; `general` → `gpt-6.1-terra[reasoningEffort=medium]`; `reviewer` → `google/gemini-3.8-flash[reasoningEffort=high]`. The worker verifies every id and its per-model effort set against `cmd --list-models` before committing; an id or level the catalog does not list is substituted with the nearest valid id for the same tier and the substitution recorded in the task report. | Effort levels follow the repository's own tier→effort mapping (`AntigravityRenderer.ResolveReasoningEffort`: architect/deep-planning/reviewer/dev-hard → high, general → medium, fast → low), which the request's proposal matched except on `general` (it proposed `high`; the repo's documented mapping says `medium`). Model families follow the request's column-parity proposal (codex/copilot columns). Token shape: the renderer writes everything before the suffix as the `model` frontmatter key, so a provider-qualified id passes through; the doc row states the verified form. | ANSWERED — autonomous: low-confidence, no authoritative source; cost of a wrong id is one-line-per-profile, caught the first time Command Code resolves the model |
| D3 | How is the mod body rendered, given `ArbiterPluginShim` is OpenCode/Bun-specific? | The renderer owns the body: a `RenderArbiterMod` static on `CommandCodeRenderer` (the `PiRenderer.RenderArbiterExtension` precedent — Pi's extension is also a renderer-owned TypeScript body, never `ArbiterPluginShim`), emitted through a new context method beside `AddArbiterPluginShim` (`SquadHarnessRenderer.cs` `AddArbiterPluginShim`, line ~691) that keeps the same gating (`EnabledArbiter`/`DispatchHooks`) and scope path selection. Project path `.commandcode/mods/kyber-arbiter.ts`; global path `mods/kyber-arbiter.ts` under the Command Code global root. | Reusing `ArbiterPluginShim` would import `@opencode-ai/plugin` under Bun — wrong loader contract for `cmd.hooks` mods; the request forbids it. | ANSWERED — code evidence (`PiRenderer`, `SquadHarnessRenderer.AddArbiterPluginShim`) |
| D4 | Is a callerless mod acceptable — the hook host sees caller `unidentified`? | Accept with precedent, verify empirically. OpenCode, Kilo and Pi shims already spawn callerless hooks evaluated as `unidentified`; the engine's target-based rules (the ones that gate a guarded worker dispatch) still enforce. T2's live evidence must show a guarded worker dispatch blocked with the Arbiter reason reaching the model. If the live run shows the engine cannot enforce correctly with `unidentified`, T2 keeps a documented degradation instead of the classification change. | A per-caller claim is never made: project-wide hooks on plugin harnesses gate marked dispatches and log `caller: unidentified` (ADR 0053 decision 4); the limitation is recorded in `architecture.md` either way. | ANSWERED — autonomous, ADR 0053 decision 4 precedent; verified live in T2 |
| D5 | May both `dispatch-unconfirmed` special cases be deleted up front? | Per scope, evidence-gated. The project-scope special case (`DegradationReason`, line ~467) is deleted only after the live project-scope block is demonstrated (`cmd --mod .commandcode/mods/kyber-arbiter.ts`, `--debug`, `tool_hook_blocked` observed); the global-scope special case (line ~449) only after the user-scope mod blocks in a live global install. A scope that cannot be verified keeps its special case with the reason updated to name the evidence. `HookedSquadTargets` gains `SquadTarget.CommandCode` regardless (it drives hook production, and the mod is the rendered artifact). | The request says to delete both, but its own Definition of Done forbids claiming parity without the live block; deleting first and failing later would ship an unverified `enforced-global` claim. OpenCode/Kilo keep `dispatch-unconfirmed` at global scope for unconfirmed dispatch naming, so a retained special case has precedent. | ANSWERED — autonomous, honours the request's evidence rule |
| D6 | What is the `squad run` verdict? | `Unsupported`, with an evidence-backed reason — the default and expected path. No config-directory environment variable and no XDG layer exist in command-code 1.88.2 (re-confirmed by T3 against the installed CLI); the only isolation mechanism is redirecting `HOME` for the whole harness process, which moves authentication and system-tool state (git, ssh, npm), and `SquadGlobalRoots.ResolveGlobalRoot(CommandCode)` returns `~/.commandcode`, not the home, so the existing `HomeReplace` link-sync source is wrong for this row. The catalog reason becomes the evidence-naming text; the onboarding row follows. Conditional branch (restored on plan review, finding `intent-alignment/t3-ready-branch-deferred`): if T3's investigation finds a documented, isolated config redirection, the request's option 1 runs in this slice — the row becomes `ConfigLayer` with the variable set, a launcher test lands, a live `squad run commandcode` launch is recorded, and the listed test/doc updates follow; a `Ready` row never lands without that live launch. The home-replacement design stays its own slice either way. | The request accepts `Unsupported` as the outcome and forbids a fabricated `Ready`, and its option 1 orders the `Ready` branch when a documented redirection exists — the deferral the first Draft carried contradicted that order. ADR 0044 decision 7 already catalogs `commandcode` as `Unsupported`. The stale catalog reason ("does not support … redirection via environment variable") is inaccurate — the onboarding matrix already says the opposite ("only supports global `HOME` redirection") — so the row is corrected to name the real mechanism and why it is not used. | ANSWERED — autonomous; amended on plan review (intent-alignment/t3-ready-branch-deferred); the home-replacement slice is a PR note for the owner |
| D7 | The request says to run every test through `scripts/proc-guard.sh run --`; this tree's rules say the opposite. | Follow the tree: `scripts/proc-guard.sh` does not exist on this branch (verified), and this worktree's `AGENTS.md` requires every test, build and gate to run directly with no process wrapper. Tasks run the equivalent commands directly (`dotnet build`/`dotnet test … --filter`, task gates via `kyber-weave review gates`). | The instruction's mechanism is inexecutable here; its intent (safe, reproducible test runs) is preserved by the tree's own command set and the task gates. | ANSWERED — conflict recorded; documented rule wins over an instruction whose mechanism is absent |
| D8 | How is the historical plan handled? | `docs/archive/plans/2026-11-04-arbiter-global-install.md` (archived; the request's path `docs/plans/...` is where it lived when approved) is referenced, never rewritten. Its R-D1 decision — Command Code `dispatch-unconfirmed` at both scopes because the design check was never completed — is superseded by this plan's live verification, recorded in `docs/kyber-arbiter/architecture.md` and an ADR 0053 amendment note that cite R-D1. | Archived plans are not implementation authority and must not be edited; the supersession is recorded where the request says the outcome was owed. | ANSWERED — plan-index policy |
| D9 | Development mode and autonomy | `development-mode: test-first` (the dispatch's default, recorded). `autonomy: autonomous` (the dispatch's packet): every open item above is decided and recorded here; no approval is requested. | — | ANSWERED — dispatch packet |
| D10 | Plan-review round 1: seven findings (intent-alignment, correctness ×3, prior-art, blast-radius-revertibility, test-infrastructure) | All seven fixed, none rejected. The T3 conditional `Ready` branch is restored and D6 amended (intent-alignment/t3-ready-branch-deferred); the mod body takes `RenderArbiterMod(int timeoutSeconds, SquadDeploymentScope scope)` with the wiring's clamped `HookTimeoutSeconds` enforced on the spawn and timeout expiry failing closed (mod-timeout-omitted); the fail-closed contract now names the exit/empty cases explicitly — a non-zero exit blocks naming the status, empty stdout is an allow only as the host's deliberate allow — both pinned in the contract tests (exit-empty-handling-unspecified); the returned-args application skips `__proto__`/`constructor`/`prototype` with a pin (prototype-pollution-guard-missing); the context helper is one shared `AddArbiterPluginFile` with `AddArbiterPluginShim` delegating to it (context-method-duplication); the global probe backs up and restores `~/.commandcode/mods/kyber-arbiter.ts` with hashes recorded, and the throwaway preset is removed after use (home-mutation-without-restore); a trust-blocked probe is defined as the unverified-scope outcome rather than a stall (trust-gate-blocks-autonomous-probe). | Evidence anchored in `PiRenderer.RenderArbiterExtension` (timeout clamp, status/empty handling, `POLLUTING_ARG_KEYS`), `ArbiterPluginShim.RunArbiterHook` (non-zero exit and deliberate-allow), and `SquadHarnessRenderer.AddArbiterPluginShim` (the gating to keep in one place). | ANSWERED — plan review round 1; all findings fixed |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute (autonomous: recommendation accepted — PLAN_READY after one plan-review round, 7 findings fixed, none rejected), 2026-11-26 |

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| KyberSquad | onboarding (`docs/kyber-squad/onboarding.md`) | Command Code notes → Models | The six shipped `commandcode` values, the `[reasoningEffort=<level>]` suffix split, and the id form verified from `cmd --list-models` |
| KyberSquad | onboarding (`docs/kyber-squad/onboarding.md`) | Harness instance support matrix | `commandcode` row reason names the verified evidence: no config/XDG variable in the installed command-code, only whole-`HOME` redirection, and why it is not used |
| KyberArbiter | architecture (`docs/kyber-arbiter/architecture.md`) | Verified global-hook matrix | `commandcode` row rewritten: owned mod file `.commandcode/mods/kyber-arbiter.ts` (project) / `mods/kyber-arbiter.ts` (global under `~/.commandcode`), trust notes (project mods trust-gated on first load; user-scope mods load without a prompt), tier per the live verification, evidence links |
| KyberArbiter | architecture (`docs/kyber-arbiter/architecture.md`) | Command Code design-check record | The R-D1 design check, now completed: `settings.json` hooks match only `SHELL`, `READ`, `WRITE`, `EDIT` so they cannot see or block `agent`; mods' `beforeToolCall`/`afterToolCall` fire for every tool call including `agent` and can `block` — the only seam that can gate a dispatch; the caller-identity limitation and its outcome; supersedes R-D1 |
| KyberArbiter | adr (`docs/adr/0026-…`) | Amendment | Command Code mods directories (project `.commandcode/mods/`, user `~/.commandcode/mods/`) join the owned-file exception; project mods are trust-gated, user-scope auto-load |
| KyberArbiter | adr (`docs/adr/0053-…`) | Amendment | `commandcode` leaves `dispatch-unconfirmed` at scopes whose live block is demonstrated (D5); the reason-code set itself is unchanged |
| KyberSquad | onboarding (`docs/kyber-squad/onboarding.md`) | Arbiter hooks | Eleven hooked targets; `commandcode` row in the "What is rendered" table names the mod and its paths |

## Investigation findings

Discovery: codegraph 6, docs MCP 0, direct reads 14. Direct reads were used because the plan needed verbatim rows, frontmatter and line-level doc content (passage answers do not carry whole tables) and the worktree corpus is what the tasks edit; codegraph (worktree index, `doctor --ensure` run) covered every code symbol below, with three direct re-reads of sections its output trimmed (gap markers).

Facts verified in this worktree at `c603a303`:

- **Model profiles.** `products/kyber-squad/profiles/models.yml` holds six profiles (`architect`, `deep-planning`, `dev-hard`, `fast`, `general`, `reviewer`), none with a `commandcode` key; every profile's `default: inherit` applies. `products/kyber-squad/schemas/model-profiles.schema.json` line 29 already allows a `commandcode` string. `CommandCodeRenderer` (`src/KyberWeave.Application/Squad/Rendering/CommandCodeRenderer.cs:112`) declares `SquadModelPolicy.InheritOrBlankOmitted` and splits an optional `[reasoningEffort=off|low|medium|high|xhigh|max]` suffix onto the `reasoningEffort` frontmatter key (lines 159-186); agents render at `.commandcode/agents/<name>.md`. `tests/KyberWeave.Content.Tests/DevHardModelUpgradeTests.cs` lines 32 and 92 pin that `models.yml` names no `commandcode` column — T1 must update those assertions, not weaken them.
- **Arbiter wiring.** `ArbiterHookWiring.HookedSquadTargets` (`src/KyberWeave.Application/Squad/Rendering/ArbiterHookWiring.cs:69-71`) lists ten targets, not `CommandCode`. `DegradationReason` special-cases `commandcode` to `dispatch-unconfirmed` at global scope (line ~449, grouped with `opencode`/`kilo`) and at project scope (line ~467). `TrustSteps` (same file, elided range ~90-155) feeds `SquadInstallCommand.WriteArbiterTrustSteps`, which prints each entry verbatim.
- **Hook host.** `PluginEnvelope` (`src/KyberWeave.Application/Arbiter/Hooks/PluginEnvelope.cs:28`) defines `kyber-arbiter.plugin-event/v1` with `schema`, `harness`, `phase` (`before`/`after`), `tool`, `call-id`, `session`, `cwd`, `args`, `result`. `PluginHookAdapters.Entries` (`src/KyberWeave.Application/Arbiter/Hooks/PluginHookAdapters.cs:15-20`) lists `opencode` (`task`/`subagent` → `subagent_type`/`agent`), `kilo` (`task` → `subagent_type`), `pi` (`Agent` → `subagent_type`), all with prompt argument `prompt`; one added row gives `commandcode` the plugin deny dialect with no new adapter class. `PluginHookAdapter` reads `args.subagent_type` as target and `args.prompt`, returns `{decision:"block",reason}` or `{decision:"allow",args:{…}}` (prompt insertion), and a callerless event evaluates as caller `unidentified`.
- **Renderer precedent.** `CommandCodeRenderer` has no `RenderArbiter` override (the base default at `SquadHarnessRenderer.cs:334` emits nothing). `SquadHarnessRenderContext.AddArbiterPluginShim(projectRelativePath, globalRelativePath)` (`SquadHarnessRenderer.cs:691`) gates on `EnabledArbiter`/`DispatchHooks` and selects the path by scope, but renders `ArbiterPluginShim.Render`, which accepts only `opencode` and `kilo` loader shapes. `PiRenderer` owns its TypeScript body (`RenderArbiterExtension`, `.pi/extensions/kyber-arbiter.ts`) — the precedent a Command Code mod follows.
- **Instance catalog and launcher.** `SquadInstanceCatalog` (`src/KyberWeave.Application/Squad/Instances/SquadInstanceCatalog.cs`) rows: `commandcode` is `SquadInstanceKind.None`, `Variable` null, `SquadInstanceStatus.Unsupported`, reason "CommandCode does not support configuration or home directory redirection via environment variable." (note: the onboarding matrix says the opposite — "CommandCode only supports global `HOME` redirection" — the two disagree today). `SquadHarnessLauncher.PrepareLaunch` (`src/KyberWeave.Data/Squad/Instances/SquadHarnessLauncher.cs:54-55`) refuses any row that is not `Ready` or has a blank `Variable` — why `squad run commandcode` exits 2. `SquadGlobalRoots.ResolveGlobalRoot(CommandCode)` is `ResolveWithOverride(null, ".commandcode")` → `~/.commandcode` (line 119), so a `HomeReplace` row's link-sync source would be the wrong directory (D6).
- **Docs surfaces to edit.** `docs/kyber-squad/model-profiles.md`: `commandcode` token-shape row (line ~115, "No bundled column; a preset supplies the first value"); the `Ready` list sentence already classes `commandcode` as `Unsupported` (line ~202) and needs no change. `docs/kyber-squad/onboarding.md`: Command Code notes Models paragraph (lines ~374-376, "No profile pins one yet"); instance support matrix row (line ~1265); Arbiter hooks section — "Ten targets are hooked … `commandcode` operates under degraded trust (`dispatch-unconfirmed`)" (lines ~1297-1310) including the `commandcode` row of the "What is rendered" table. `docs/kyber-arbiter/architecture.md`: verified global-hook matrix `CommandCode` row (line ~1773) and the `[CM-MOD]` mods citation (line ~1587). `docs/cli-command-surface.md` does not enumerate `squad run` targets (no occurrence) — no change needed.
- **Tests pinning the old classification.** `dispatch-unconfirmed` appears in `src/…/ArbiterHookWiring.cs` and in `tests/KyberWeave.Application.Tests/Squad/ArbiterGlobalPluginDirRenderingTests.cs`, `Squad/ArbiterGlobalWiringTests.cs`, `Squad/ArbiterHookWiringTests.cs`, and `tests/KyberWeave.Cli.Tests/Arbiter/ArbiterDoctorCommandTests.cs`, `Arbiter/ArbiterStatusCommandTests.cs`. The archived plan's R4 also names the specific `ArbiterHookWiringTests` cases: `TargetDegradations_GlobalScope_CommandCodeIsDispatchUnconfirmed_NotEnforcedGlobal`, `TargetDegradations_ProjectScope_ClassifiesEveryApprovedTarget`, and the table test pinning that no renderer without a global Arbiter artifact is classed `enforced-global`.
- **Historical plan.** `docs/archive/plans/2026-11-04-arbiter-global-install.md` decision R-D1 (line ~266) records Command Code `dispatch-unconfirmed` at both scopes because the design check was never completed, with the outcome owed to `architecture.md` — this plan's T2/T4 deliver it. The plan is archived; it is referenced, never edited.
- **Environment.** `scripts/proc-guard.sh` does not exist on this branch; `scripts/fast-loop.sh` supports `--class 'KyberWeave.Tests.<Area>*'` (namespace in the test files is `KyberWeave.Tests`). Declared technologies: `csharp`, `test`, `typescript`, `react`, `rust`, `tauri` (+ `github-actions` in gate use).

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Application.Tests/CommandCodeRendererContractTests.cs` (extend; beside `ReasoningEffortSuffixSplitsOntoCommandCodeLevels`) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~CommandCodeRendererContract"` | A profile with a `commandcode` value renders `model:` and `reasoningEffort:` into `.commandcode/agents/<name>.md`; `inherit` (and the policy's blank omission) renders neither key | New case fails on the current tree: the rendered frontmatter carries no `model` key because the bundled matrix has no column | Same tests pass with a non-zero total; no existing assertion weakened (existing tests keep their meaning; a locator narrowed by the new column is named in the worker's report) |
| T1 | `tests/KyberWeave.Content.Tests/DevHardModelUpgradeTests.cs` (update pins) | `dotnet test tests/KyberWeave.Content.Tests/KyberWeave.Content.Tests.csproj -c Release --filter "FullyQualifiedName~DevHardModelUpgrade"` | The content assertions state the new column while keeping the test's meaning: what the `dev-hard` step-up changes on `commandcode` | Fails after the column lands if run before the pin update (order: renderer test RED first, then the column, then this update) | Non-zero total; the step-up claim stays honest for the new column |
| T1 | `tests/KyberWeave.Application.Tests/SquadModelPresetRenderTests.cs` (may narrow) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~SquadModelPresetRender"` | Command Code renders of the real corpus stay valid with the column present | Any expectation the new column makes ambiguous fails before the narrowing | Non-zero total |
| T2 | `tests/KyberWeave.Application.Tests/CommandCodeRendererContractTests.cs` (extend: mod body + paths) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~CommandCodeRendererContract"` | With Arbiter enabled or dispatch hooks on, the render emits the mod at `.commandcode/mods/kyber-arbiter.ts` (project) and `mods/kyber-arbiter.ts` (global); with neither, no mod file. The body filters on `toolName === 'agent'`, builds the `kyber-arbiter.plugin-event/v1` envelope, spawns `kyber-weave-arbiter hook --harness commandcode` (with `--scope global` at global scope) through `node:child_process` with no shell and bounded by the wiring's clamped `HookTimeoutSeconds`, maps block to `{block:true, additionalContext:reason}` and allow-with-args to `{input:args}` skipping the polluting keys `__proto__`/`constructor`/`prototype`, and fails closed exactly as the precedents — a non-zero exit blocks naming the status and empty stdout is an allow only as the host's deliberate allow, each pinned | New case fails: no such file in the render output | Non-zero total; byte-level assertions mirror the `ArbiterPluginShim`/Pi extension contract tests, including the timeout, exit/empty and polluting-key pins |
| T2 | `tests/KyberWeave.Application.Tests/Arbiter/PluginHookAdapterTests.cs` (extend; pattern of `OpencodeAdapter_IsRegisteredWithDispatchShape`) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~PluginHookAdapter"` | `PluginHookAdapters.All` serves `commandcode`: dispatch tool `agent`, target argument `subagent_type`, prompt argument `prompt`, plugin deny dialect on block | New case fails: `commandcode` absent from `All()` | Non-zero total |
| T2 | `tests/KyberWeave.Application.Tests/Squad/ArbiterHookWiringTests.cs`, `Squad/ArbiterGlobalWiringTests.cs`, `Squad/ArbiterGlobalPluginDirRenderingTests.cs`, `Squad/ArbiterHookRenderScopeTests.cs`, `tests/KyberWeave.Cli.Tests/Arbiter/ArbiterDoctorCommandTests.cs`, `tests/KyberWeave.Cli.Tests/Arbiter/ArbiterStatusCommandTests.cs` (update pins) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~Arbiter"` and `dotnet test tests/KyberWeave.Cli.Tests/KyberWeave.Cli.Tests.csproj -c Release --filter "FullyQualifiedName~Arbiter"` | `HookedSquadTargets` includes Command Code; `DegradationReason("commandcode", …)` returns the decided answer per scope — null at project scope and `enforced-global` at global scope for a live-verified scope, `dispatch-unconfirmed` retained for an unverified one; trust steps include the Command Code entry; install/doctor/status output reflects it | Updated expectations fail while the special cases remain (and while `HookedSquadTargets` lacks the target) | Both filters green with non-zero totals; the legacy-assertion changes are recorded in the commit message as the 2026-11-04 classification's supersession, not as weakening |
| T3 | `tests/KyberWeave.Application.Tests/Squad/Instances/SquadInstanceCatalogTests.cs` (update pin) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~SquadInstance"` | The `commandcode` row stays `None` / null variable / `Unsupported`, with the reason naming the verified evidence; `SquadRunCommandTests` and `SquadHarnessLauncherTests` need no change (the exit-2 path is unchanged) — verified by the same filter run | Updated reason assertion fails against the current stale reason | Non-zero total |

## Tasks

### T1: Pin the `commandcode` model column and prove the renderer emits it

- **Objective:** Ship a verified `commandcode` model for all six bundled profiles and prove rendered agents carry it.
- **Capabilities:** C# renderer contract and content tests over the bundled YAML matrix; Markdown doc rows; verifying model ids against the Command Code CLI's own catalog.
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `commandcode-models`
- **Depends on:** none
- **Files:**
  - `products/kyber-squad/profiles/models.yml`: `profiles.<name>.commandcode` for every profile — `architect`, `deep-planning`, `dev-hard` → `gpt-6.2-sol[reasoningEffort=high]`; `fast` → `gpt-6-luna[reasoningEffort=low]`; `general` → `gpt-6.1-terra[reasoningEffort=medium]`; `reviewer` → `google/gemini-3.8-flash[reasoningEffort=high]` — after verifying each id and its effort levels against `cmd --list-models`; when the catalog disagrees, substitute the nearest valid id for the same tier and record the substitution in the task report.
  - `tests/KyberWeave.Application.Tests/CommandCodeRendererContractTests.cs`: new cases beside `ReasoningEffortSuffixSplitsOntoCommandCodeLevels` — a pinned `commandcode` value renders `model:` and `reasoningEffort:`; `inherit` omits both.
  - `tests/KyberWeave.Content.Tests/DevHardModelUpgradeTests.cs`: update the "no commandcode column" pins (lines ~32, ~92) to the shipped column, keeping the test's meaning.
  - `tests/KyberWeave.Application.Tests/SquadModelPresetRenderTests.cs`: narrow any expectation the new column makes ambiguous (existing assertions keep their meaning).
  - `docs/kyber-squad/model-profiles.md`: the `commandcode` token-shape row — replace "No bundled column; a preset supplies the first value" with the bundled values and the `[reasoningEffort=<level>]` suffix split; state the id form verified from `cmd --list-models`.
  - `docs/kyber-squad/onboarding.md`: "Command Code notes → Models" — remove "No profile pins one yet" and state the six shipped values.
- **Commands:**
  - `cmd --list-models` (verify every id and its per-model effort set before writing the column)
  - Task-gate run (test-first): the failing run with `--red`, then the passing run — `kyber-weave review gates . --agent <assigned-agent> --task T1`
  - Preset evidence: `dotnet run --project src/KyberWeave.Cli -- squad preset show <preset> --target commandcode` (a throwaway preset under `~/.kyber-weave/squad/presets/` if needed to exercise the surface — remove it after and record the cleanup in the task evidence) and a real apply in a scratch project — `cd <scratch> && dotnet run --project <repo>/src/KyberWeave.Cli -- squad install --target commandcode` — then read `.commandcode/agents/*.md` for the pinned `model:` and `reasoningEffort:`.
- **Acceptance:**
  - All Test-contract rows for T1 green with non-zero totals; `cmd --list-models` output and the scratch-project agent files recorded in the task evidence.
  - No verification claim beyond what the commands showed; every id substitution recorded.

### T2: Render the Command Code Arbiter mod, register the harness, and change the classification on live evidence

- **Objective:** Gate `agent` dispatches on Command Code through a rendered mod and the Arbiter hook host, with the observed block behaviour recorded.
- **Capabilities:** C# renderer and wiring code with xUnit unit tests; TypeScript shipped inside a C# renderer; live verification against the installed Command Code CLI.
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `commandcode-arbiter`
- **Depends on:** none — when dispatched in parallel with T1, run T1 first: both extend `tests/KyberWeave.Application.Tests/CommandCodeRendererContractTests.cs`
- **Design (the failure this task must not ship):** a mod that never fires, fires on the wrong tool, or fails open silently removes dispatch enforcement while every unit test still passes, because unit tests assert rendered bytes, not live behaviour. Mitigations, already decided:
  - The body fails closed exactly as the precedents do: spawn failure, timeout expiry, a non-zero exit (naming the status) or unparseable output blocks the dispatch with a reason naming the failure; empty stdout is an allow only as the host's deliberate allow, otherwise it blocks.
  - The body acts only on `toolName === 'agent'` and passes every other tool through untouched.
  - The envelope is the shared `kyber-arbiter.plugin-event/v1` shape `PluginEnvelope` parses, and the registry row reuses `PluginHookAdapter` — no new adapter class, no new dialect.
  - The classification special cases are deleted per scope only after that scope's live block is demonstrated.
- **Files:**
  - `src/KyberWeave.Application/Squad/Rendering/CommandCodeRenderer.cs`: `RenderArbiter` override plus a `public static string RenderArbiterMod(int timeoutSeconds, SquadDeploymentScope scope)` body — the `PiRenderer.RenderArbiterExtension` precedent, with the timeout clamped the same way and taken from the render's Arbiter wiring (`HookTimeoutSeconds`). Project scope `.commandcode/mods/kyber-arbiter.ts`; global scope `mods/kyber-arbiter.ts` under the Command Code global root. The mod: default-exports a factory taking the ModApi registering `beforeToolCall` and, if the post-dispatch annotation is expressible, `afterToolCall`; filters `toolName === 'agent'`; builds the envelope with `phase`, `tool`, `call-id`, `session`, `cwd`, `args` (and `result` for the post phase); spawns `kyber-weave-arbiter hook --harness commandcode` (plus `--scope global` at global scope) with the envelope on stdin through `node:child_process` with no shell, bounded by the clamped timeout; translates `{decision:"block",reason}` to `{block:true, additionalContext:reason}` and a returned stripped `args` to `{input:args}`, skipping the polluting keys `__proto__`, `constructor` and `prototype` when applying them onto the tool input (the `PiRenderer` guard against inherited-setter rebinding); and fails closed exactly as the precedents do — spawn failure, timeout expiry, non-zero exit (naming the status) or unparseable output blocks with a reason naming the failure, and empty stdout is an allow only as the host's deliberate allow.
  - `src/KyberWeave.Application/Squad/Rendering/SquadHarnessRenderer.cs`: one shared helper, `AddArbiterPluginFile(string projectRelativePath, string? globalRelativePath, string body)`, holding the `EnabledArbiter`/`DispatchHooks` gating and the scope path selection once; `AddArbiterPluginShim` (line ~691) is refactored to delegate to it with `ArbiterPluginShim.Render(Token, Scope)` as the body, and the Command Code renderer calls it with its mod body — no duplicated gating rule to drift.
  - `src/KyberWeave.Application/Arbiter/Hooks/PluginHookAdapters.cs`: `Entries` row `("commandcode", ["agent"], ["subagent_type"])` and the class doc comment's harness list.
  - `src/KyberWeave.Application/Squad/Rendering/ArbiterHookWiring.cs`: add `SquadTarget.CommandCode` to `HookedSquadTargets` (lines 69-71); delete the `commandcode` `dispatch-unconfirmed` special cases (lines ~449 and ~467), each scope's deletion landing only after that scope's live block is demonstrated; add a `TrustSteps` entry stating that project mods are trust-gated on first load while user-scope mods load without a prompt.
  - Tests per the Test contract: `CommandCodeRendererContractTests.cs` (mod body + project/global paths), `Arbiter/PluginHookAdapterTests.cs` (registry row), `Squad/ArbiterHookWiringTests.cs`, `Squad/ArbiterGlobalWiringTests.cs`, `Squad/ArbiterGlobalPluginDirRenderingTests.cs`, `Squad/ArbiterHookRenderScopeTests.cs`, `tests/KyberWeave.Cli.Tests/Arbiter/ArbiterDoctorCommandTests.cs`, `tests/KyberWeave.Cli.Tests/Arbiter/ArbiterStatusCommandTests.cs` — plus a grep of `dispatch-unconfirmed` under `tests/` so no pin is missed.
- **Commands:**
  - Task-gate run (test-first): the failing run with `--red`, then the passing run — `kyber-weave review gates . --agent <assigned-agent> --task T2`
  - Live verification (project scope): in a scratch project with `arbiter.enabled: true`, `dotnet run --project src/KyberWeave.Cli -- squad install --target commandcode`, then `cmd --mod .commandcode/mods/kyber-arbiter.ts --debug` — grant the scratch project's mod trust through the installed command-code's documented non-interactive mechanism if one exists; a probe that stays trust-blocked is the unverified-scope outcome for that scope (its `dispatch-unconfirmed` special case is retained with a reason naming the trust block), never a claimed defect of the mod — and observe `tool_hook_blocked` for a guarded worker dispatch; confirm `input.subagent_type` is the canonical agent name and that the model receives the Arbiter reason.
  - Live verification (global scope): record the pre-probe SHA-256 of `~/.commandcode/mods/kyber-arbiter.ts` if one exists (or its absence) in the task evidence; a global render (`--global`); the same probe against the user-scope mod; then restore the operator's prior file — or remove the rendered one when none existed — and record the hashes and the restore in the task evidence.
  - Host-side check: `kyber-weave-arbiter hook --harness commandcode` with a sample envelope on stdin, asserting the deny dialect and the `subagent_type`/`prompt` reads.
- **Acceptance:**
  - Test-contract rows green with non-zero totals.
  - Live command output recorded in the task evidence: `beforeToolCall` fires for `agent`, the block is observed with the reason delivered, and the caller-identity outcome is stated — whether the mod can identify the caller, and if it cannot, that enforcement ran with caller `unidentified` and still blocked the guarded dispatch; a degradation is recorded, not a parity claim, wherever the evidence does not reach.
  - Each scope's classification special case is deleted only after that scope's live block is demonstrated; a scope without live evidence keeps `dispatch-unconfirmed` with a reason naming what is missing.
  - The mod is not rendered at instance scope, and no claim is made that it applies there.

### T3: `squad run` — investigate, then record the evidence-backed verdict

- **Objective:** Leave the `commandcode` instance row with a verdict that names its evidence.
- **Capabilities:** C# catalog row and tests; reading the installed Command Code CLI's own help and bundled documentation; Markdown doc row.
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `commandcode-run`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Application/Squad/Instances/SquadInstanceCatalog.cs`: the `commandcode` row's `Reason` — the evidence-naming text: no config-directory variable and no XDG layer in the installed command-code; only whole-`HOME` redirection, which moves authentication and system-tool state, and the global root is `~/.commandcode`, not the home, so the existing link-sync source is wrong. Kind, variable and status stay `None` / null / `Unsupported`.
  - `tests/KyberWeave.Application.Tests/Squad/Instances/SquadInstanceCatalogTests.cs`: update the pinned reason; the row still exits 2. `SquadRunCommandTests` and `SquadHarnessLauncherTests` need no change (verify via the filter run).
  - `docs/kyber-squad/onboarding.md`: the instance support matrix `commandcode` row's reason (~1265) aligned with the catalog row.
  - Conditional, only if the investigation finds a documented, isolated config redirection (the request's option 1): the row becomes `ConfigLayer` with the variable set; `tests/KyberWeave.Data.Tests/Squad/SquadHarnessLauncherTests.cs` gains the row's launch start-info test; the `SquadRunCommandTests` class (locate its project with `grep -rl "class SquadRunCommandTests" tests`) gains the run expectations; `docs/kyber-squad/onboarding.md`'s matrix row and `docs/kyber-squad/model-profiles.md`'s `Ready`-list sentence move `commandcode` to `Ready`; `docs/todo/` only if the branch closes a deferred item; `docs/cli-command-surface.md` only if it enumerates runnable targets (verified: it does not).
- **Commands:**
  - Investigation, read-only, in order per the request: `cmd --help`, `cmd info`, the installed command-code's bundled docs and settings schema — looking for any config-directory variable, XDG layer, or redirection flag.
  - Task-gate run (test-first): the failing run with `--red`, then the passing run — `kyber-weave review gates . --agent <assigned-agent> --task T3`
  - Conditional, on the Ready branch only: a live `squad run commandcode` in the scratch project — `dotnet run --project src/KyberWeave.Cli -- squad run commandcode` — proving the harness starts, loads the preset's agents, and propagates the harness exit code; recorded in the task evidence.
- **Acceptance:**
  - The investigation's findings are recorded in the task report with the commands that produced them. Default path: the row stays `Unsupported` with the evidence-naming reason. Ready branch, taken only when the investigation finds a documented, isolated config redirection: the row becomes `ConfigLayer` with the variable set, the launcher test and run-command expectations land, the live `squad run commandcode` launch is recorded in the task evidence, and the listed doc updates land with it.
  - No `Ready` row without a live launch; no home-replacement hack.

### T4: Record the design check, matrix row, and ADR amendments

- **Objective:** Harvest T2's and T3's evidence into the Arbiter architecture record, the onboarding Arbiter sections, and the two ADR amendments.
- **Capabilities:** Documentation in Markdown under the governed-docs rules, including ADR amendment notes.
- **Difficulty:** easy
- **Session:** `commandcode-arbiter`
- **Depends on:** T2 (the verified mod, its observed behaviour, and the classification outcome), T3 (the matrix verdict and its evidence)
- **Files:**
  - `docs/kyber-arbiter/architecture.md`: the verified global-hook matrix `CommandCode` row (~1773) — owned mod file at both scopes, trust notes, tier per the live-verified scopes, evidence links; and the Command Code design-check record: `settings.json` hooks match only `SHELL`, `READ`, `WRITE`, `EDIT` so they cannot see or block `agent`; mods' `cmd.hooks` fire for every tool call including `agent` and can `block` — the only seam that can gate a dispatch; the caller-identity limitation and its outcome. The record states that it supersedes the earlier `dispatch-unconfirmed` classification, which was chosen because block-on-dispatch was unproven and the design check was owed and never completed.
  - `docs/kyber-squad/onboarding.md`: the Arbiter hooks section (~1297-1310) — "Ten targets are hooked" becomes eleven; the `commandcode` row of the "What is rendered" table names the mod and its project/global paths.
  - `docs/adr/0026-kyber-utilities-owned-files-not-settings.md`: an amendment note adding Command Code mods directories (project `.commandcode/mods/`, user `~/.commandcode/mods/`) to the Squad owned-file exception, with the trust-gating note.
  - `docs/adr/0053-kyber-arbiter-three-step-decision-gates.md`: an amendment note recording `commandcode` leaving `dispatch-unconfirmed` at the live-verified scopes (or the retained reason where a scope stayed unverified), with the evidence reference.
  - `docs/kyber-squad/README.md`, `docs/kyber-squad/architecture.md`, `docs/kyber-squad/requirements.md`: grep `commandcode` and update any tier or degradation statement that T2's outcome supersedes.
- **Commands:**
  - `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` after the edits (plain, not `--merge-ready`: this plan is active).
- **Acceptance:**
  - Every statement matches what T2/T3 actually demonstrated — a degradation is recorded wherever the evidence did not reach; nothing claims parity the live run did not show.
  - Both docs checks pass with zero findings outside the standards warning period.

## Dependency graph

- T1 — none (run first when parallel with T2: both extend `CommandCodeRendererContractTests.cs`).
- T2 — none (same session as T4).
- T3 — none.
- T4 — T2, T3 (consumes their evidence and verdicts).

## Risks

- The live block may not be demonstrable (a mod API mismatch in the installed command-code, or a trust gate that blocks the project mod — a trust-blocked probe is itself the unverified-scope outcome) → the fallback is pre-authorized: the unverified scope keeps `dispatch-unconfirmed` with a reason naming what is missing; nothing else in T2 depends on the classification flip.
- A proposed model id or effort level may not exist in the installed catalog → D2 authorizes verified substitution with the substitution recorded; the risk is a wrong pin caught at first model resolution, one line to fix.
- Caller-unidentified evaluation may change engine outcomes on Command Code → D4 requires the live guarded-dispatch block before any enforcement claim; the fallback is a recorded degradation.
- The mod body is TypeScript shipped from C#; escaping bugs are invisible to the live CLI → the contract tests pin the emitted bytes (envelope, spawn argv, block and args mappings), mirroring the `ArbiterPluginShim` and Pi extension tests.
- New tests could slow the unit suite → all new tests are in-memory renders and pure-function calls, per the test standard's speed rules; no real-program test is added to the unit projects (the live verification is command evidence, not a test).

## Out-of-scope boundaries

- Home-replacement instance isolation for Command Code: an explicit instance mechanism (the global root is `~/.commandcode`, not the home), the home-link strategy, `auth.json` handling, an ADR 0044 amendment, and a live launch — a design slice of its own (D6). PR note: whole-`HOME` isolation for Command Code is possible but needs an owner-sponsored design; the current verdict and its evidence are recorded instead.
- Registering the `kw-arbiter` MCP server in Command Code's MCP configuration: the recorded fallback for any scope that stays degraded; not auto-written by Squad.
- Classification changes for `opencode`, `kilo` or any other target; their global-scope `dispatch-unconfirmed` stays.
- A `Ready` instance row, launcher change, `SquadRunCommandTests`/`SquadHarnessLauncherTests` behaviour change, `docs/todo/` entry or `cli-command-surface.md` change reached outside T3's conditional branch (a documented, isolated config redirection found) — and a `Ready` row never lands without the live launch that branch records. PR note: `cli-command-surface.md` does not enumerate `squad run` targets (verified), so no change is needed there in either outcome.
- Rewriting the archived 2026-11-04 plan, or editing generated or archived docs (referenced only).
- Rendering the mod at instance scope.

## Verification gates

- Per task, test-first: the failing task-gate run with `--red`, then the passing run — `kyber-weave review gates . --agent <assigned-agent> --task <id>`; each run's report states a non-zero test count.
- Targeted classes (run directly, no wrapper — D7): `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~CommandCode"`, `--filter "FullyQualifiedName~Arbiter"`, `--filter "FullyQualifiedName~SquadInstance"`, `--filter "FullyQualifiedName~SquadModelPresetRender"`; `dotnet test tests/KyberWeave.Cli.Tests/KyberWeave.Cli.Tests.csproj -c Release --filter "FullyQualifiedName~Arbiter"`; `dotnet test tests/KyberWeave.Content.Tests/KyberWeave.Content.Tests.csproj -c Release --filter "FullyQualifiedName~DevHardModelUpgrade"`; `dotnet build` of the touched test projects first.
- Docs, after any `docs/` edit and at run end: `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` — plain, not `--merge-ready`, while this plan is active; the merge-ready gate runs after closeout archives it.
- `scripts/fast-loop.sh --class 'KyberWeave.Tests.<Area>*'` for iteration only; never cited as evidence.

## Review

The end-of-run council derives its review from the `code-review` skill, measured against the change with `--base` and admitted to the full-suite gates with `--council`; this plan does not carry the command. The change spans C# and governed Markdown and is expected to exceed 300 changed lines (the mod body, its tests, and the doc records), so it takes the full pre-push council.

## docs-dev closeout

Complete the Closeout mapping for every Design harvest row above (`kyber-weave docs closeout <this plan>` names what is missing), then archive through the conductor's archive tool or `kyber-weave docs closeout <this plan> --archive`: the plan moves to `docs/archive/plans/`, the task file is removed in the same commit, the Active Plans row is removed, and `docs validate --merge-ready` then passes. The Decisions table is preserved intact through the archive.
