---
id: plans/2026-11-04-arbiter-global-install
title: Arbiter global (user-scope) install — `--global` by default, `--project` opt-in
doc-type: plan
status: current
component: KyberArbiter
owner: dpalfery
created: 2026-11-04
last-reviewed: 2026-11-06
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberArbiter, KyberSquad, Distribution]
---

# Arbiter global (user-scope) install — `--global` by default, `--project` opt-in

**Status: Ready, revision 2 — 2026-11-05 (AP1 approved by the user).** All decisions in the `## Decisions`
table are answered (user decisions U1–U9 are requirements; D-rows are autonomous decisions with sources).
Revision 2 applies the user's 2026-11-05 decisions U8–U11: the global-hook matrix is now **verified**
(research done in planning, so task T0 is removed), the position is "global where supported, project
where only project exists, MCP fallback otherwise", and ADR 0026 and ADR 0052 are **amended**, not
worked around. Because the contract changed, AP1 (the prior autonomous approval of revision 1) no longer
applied, so the gate was presented again and the user approved it on 2026-11-05 (AP1). **Development mode:** `test-first`
(packet; no user opt-out). **Autonomy:** `autonomous` (packet and Config Reg `<autonomy>`).
**Scale:** `extends-component`, components `KyberArbiter`, `KyberSquad`, `Distribution`.

## Problem and goal

`kyber-weave arbiter install --target <harness>` (PR #514, rc.18) is always project-scoped and
rejects `--global` with exit `2`; Squad renders no Arbiter hooks at global scope and records
`arbiter-not-enforced` / `global-scope` ([ADR 0053](../adr/0053-kyber-arbiter-three-step-decision-gates.md)
decisions 15/19 in its Decision list, spec ids D18/D22). Repository policy is that `--global` (user scope) is
the default and every feature supports it.

**Goal.** (1) `arbiter install` / `uninstall` default to the user (global) scope and a project install
is an explicit `--project` opt-in; (2) a global install renders each harness's *user-level* Arbiter
hooks, records itself in `~/.kyber-weave/kyber-weave.yml`, and shares the one OS-credential-store key
with project installs; (3) `kyber-weave update` refreshes global hooks as well as registered
`arbiter.enabled` projects; (4) when both scopes exist the project wins; (5) the rule "every feature
supports `--global`" becomes a documented repo-wide rule by amending existing ADRs in place, and
ADR 0053 is amended so no document still says the install is project-only.

## Decisions

Status vocabulary: `ANSWERED` (decided, source recorded), `APPROVED` (explicit approval).
Row provenance for U-rows is `User decision 2026-11-04 (conductor packet)`.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| U1 | How are ADRs handled? | **Amend in place; never supersede.** Amend ADR 0053 (reverse the "project-scoped / `--global` rejected" and "global install renders no hooks" statements) and state the repo-wide rule by amending the best-fitting existing ADR(s) (see D1). The corpus holds no outdated text. | `docs/adr/README.md`: "A changed decision edits its record in place… superseded wording is removed". | ANSWERED — User decision 2026-11-04 |
| U2 | Per-harness global hook locations? | Research all 13 harnesses; any without a global hook mechanism is an **explicit, recorded gap**, never silently skipped. See Investigation findings §Global-hook matrix (verified 2026-11-05, U9). | Verification completed in planning (U9); the former task T0 is removed. | ANSWERED — User decision 2026-11-04 |
| U3 | Where is a global install recorded; what reads it? | In the user-level config `~/.kyber-weave/kyber-weave.yml`; `kyber-weave update` reads it to refresh global hooks **and** hooks of registered projects with `arbiter.enabled`. | Schema in D3. | ANSWERED — User decision 2026-11-04 |
| U4 | Source of `arbiter.enabled` for a global install? | Read from the user-level config; `arbiter install` creates the file when missing (non-destructive merge via `HostConfigYaml`). | Mirrors the project path (`DocsScaffolder` + `WithArbiterEnabled`) without the docs scaffold. | ANSWERED — User decision 2026-11-04 |
| U5 | Key handling? | Key stays in the OS credential store only; project and global installs share ONE key per endpoint origin. | Already per-origin (`ArbiterKeyResolver.GetOrigin`); no new store entry. Uninstall must not strand the other scope (D8). | ANSWERED — User decision 2026-11-04 |
| U6 | Precedence when both scopes exist? | **Project overrides global.** | Runtime mechanics in D4. | ANSWERED — User decision 2026-11-04 |
| U7 | Default scope? | `arbiter install` / `uninstall` with no flag = **global**; project is explicit opt-in (`--project`). Behaviour change: CLI surface, exit codes, help, `docs/cli-command-surface.md`, back-compat in D6/D7. | — | ANSWERED — User decision 2026-11-04 |
| D1 | Which existing ADR(s) carry the repo-wide "every feature supports `--global`" rule? | Amend **[ADR 0037](../adr/0037-squad-state-in-user-home.md)** (home-level state `~/.kyber-weave/`, global anchor, registry — the substrate every user-scope feature depends on) with a "User scope is the default scope" section, and add a rule document `docs/rules/global-scope-by-default.md` (`decided-by: adr/0037…`) mirroring the ADR 0033 → `rules/kyberdash-display-layer` pattern; list it in `docs/rules/README.md`. No existing ADR governs install scope or the CLI command surface, so this is a best fit, not an exact one. ADR 0053 gets the Arbiter-specific reversal. **Never a superseding ADR.** | Autonomy check 1: searched ADR index (0001–0056), `docs/rules`, `cli-command-surface.md`; only 0024/0037 define global-scope infrastructure. 0037 is Squad-titled, so its title/summary are updated in place to say it governs user-home state for all features. Cheap to reverse (docs only). | ANSWERED — autonomous: low-confidence, no authoritative source (alternative: amend ADR 0024; rejected as narrower) |
| D2 | Trust the first external research pass (13 harnesses) for the global-hook matrix? | **No — superseded by U9.** The first pass was wrong for Codex, Cursor, Antigravity, ZCode and CommandCode and was discarded. The matrix below was re-researched against vendor documentation on 2026-11-05 (URLs recorded per row) and is the contract. Points the documentation leaves open are carried as explicit, non-blocking items (D16) with an observed fallback, not as execution-time research. | The provisional UNVERIFIED matrix and task T0 are removed (U9). | ANSWERED — revised 2026-11-05 per User decision U9 |
| D3 | Global record schema in `~/.kyber-weave/kyber-weave.yml`? | `arbiter:` section gains `global: { targets: [<token>, …] }` plus the existing `enabled`/`provider`. `arbiter.global` is valid **only** in the user-level file; in a project file it fails as new rule `KW-ARB-CONFIG-011` (ids 001–010 exist, never reused). `update` refreshes exactly the listed targets. Provider kind written by `install` is stored here for global scope. | Reuses `HostConfigYaml.WithArbiterEnabled`/`WithArbiterProviderKind` patterns; the existing `~/.config/kyber-weave/arbiter.yml` override (provider only) is untouched and keeps replacing `provider` field by field. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D4 | How does "project overrides global" hold when harnesses MERGE hook layers (both fire)? | Global hooks render with `--scope global` (`kyber-weave-arbiter hook --harness <t> --scope global`). At run time a global-scope invocation resolves the project root from the payload; when that project's `.kyber-weave/kyber-weave.yml` exists with `arbiter.enabled: true` it **defers** (allow pass-through, decision logged `deferred-to-project`, no ledger write) so exactly one scope evaluates; otherwise it evaluates using the user-level config. Per-agent-frontmatter harnesses (Claude, Copilot VS Code) get natural override: a project agent of the same name shadows the user agent. | A project that sets `arbiter.enabled: false` explicitly disables gating there (project wins), documented. A project with `enabled: true` but no project hooks installed is ungated by design; `arbiter doctor` warns (T9). Wrong call is caught at PR review; reversible in code. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D5 | A global hook fires in projects with no `.kyber-weave/`; today a missing config fails closed (`KW-ARB-HOOK-001`). | Global-scope runs never fail closed on a missing project config: they use the user-level config (D3). The project-scope fail-closed contract is unchanged. A missing/malformed **user** config at global scope fails closed with `KW-ARB-HOOK-001` naming `~/.kyber-weave/kyber-weave.yml`. Ledger and decision log for a global-scope evaluation go to `~/.kyber-weave/arbiter/projects/<root-key>/` (same root-key function as `SquadStateStore`), never into an arbitrary working tree. | Avoids writing `artifacts/arbiter/` into every repo the user opens. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D6 | CLI surface for scope. | `--global` / `-g` (explicit; the default), `--project` (new, opt-in), mutually exclusive → exit `2` with new id `KW-ARB-INSTALL-001`. `--path` is only valid with `--project` (exit `2` otherwise). Applies to `install` and `uninstall`. `--target` stays required. Help text states the default. | Mirrors `squad` verbs, which already carry `-g\|--global`; the new default differs from squad's (project) by owner directive U7 — `docs/cli-command-surface.md` states this explicitly. | ANSWERED — autonomous: recommendation accepted (derived from U7) |
| D7 | Migration / back-compat for existing project installs. | No file migration: project receipts, hooks and `.kyber-weave/kyber-weave.yml` are untouched and keep working; `update` still refreshes them. A bare `arbiter install` inside a repo that has a project install now installs globally and prints one notice line: `Installed at user scope; use --project to manage this repository's install.` Scripts that passed nothing (project) must add `--project`; scripts passing `--global` get exit `0` instead of `2`. `arbiter uninstall` bare removes the **global** install only and prints the same hint when a project install is present. Release notes carry the change. | rc.18 is a release candidate; blast radius is small. Exit-code table keeps `2` for client-input errors. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D8 | Shared key vs. uninstall in either scope. | `--remove-key` deletes the origin entry only after warning when the *other* scope still records an install that uses the same origin (reuses `AnotherTargetIsDeployed`, extended to both receipts and the user config). Key is never written to any file at either scope. | U5. | ANSWERED — autonomous: recommendation accepted |
| D9 | Dry-run and verification at both scopes. | `--dry-run` plans and prints the global file set (resolved physical paths from `SquadGlobalRoots`), writes nothing, including no user-config creation. Verify (non-dry-run): config enables Arbiter at the chosen scope, key resolves when needed, `kyber-weave-arbiter` is on PATH, and every rendered global artifact exists; unverified/gap targets are listed, not failed. | Extends the existing `Verify` method. | ANSWERED — autonomous: recommendation accepted |
| D10 | Conflict: the new rule vs. [ADR 0052](../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md) ("docs mcp-write: repository scope, **never user scope**") and [ADR 0026](../adr/0026-kyber-utilities-owned-files-not-settings.md) (Utilities never edits shared harness settings, no writes to auto-load directories, manual activation). | **Revised 2026-11-05 (U10): no "named exceptions".** The rule is worded: "every feature that installs or configures per-harness artifacts supports `--global`; where a harness has a global hook location, install there; where only a project location exists, install at project level; where hooks are unsupported, use the Arbiter MCP `serve` fallback." ADR 0026 and ADR 0052 are **amended in place** (T9) so they agree with the rule: Squad may add or remove **only its own marked/owned hook block or owned file** in a harness's global hook location (including auto-loaded plugin directories for OpenCode and Kilo), **only** when the user explicitly runs the install command, never touching anything else in the file, and the install output lists exactly what was written. ADR 0052's docs-branch `mcp-write` rule itself is **unchanged** — it concerns docs MCP configuration, a different artifact — and the amendment says so. The todo `docs-mcp-write-user-scope-decision` is resolved by this decision and removed. | User decision 2026-11-05 (U10). ADR 0054 already covers Squad-owned blocks in shared hook files at project scope; the amendments extend the same ownership model to user scope. | ANSWERED — User decision 2026-11-05 (U10), supersedes the autonomous revision-1 answer |
| U8 | Position on global vs project vs fallback? | **Where a harness supports global hooks, install there; where only project-level hooks exist, install at project level; where hooks are unsupported, fall back to the Arbiter MCP `serve` fallback.** "Don't let perfection be the enemy of good." | Drives D12 and the matrix Status column. | ANSWERED — User decision 2026-11-05 |
| U9 | Is verification of the matrix execution work? | **No.** It was research and is DONE in planning. Task T0 is removed; the provisional UNVERIFIED matrix is replaced by the verified matrix below. Rule ids and decision ids stay permanent: T0's number is **not reused**; T1–T10 keep their numbers. | Remaining open points are D16, non-blocking. | ANSWERED — User decision 2026-11-05 |
| U11 | Relationship between the Arbiter and the docs MCP? | The Arbiter MCP is its own server (`kyber-weave arbiter serve`), separate from the docs MCP. **No Arbiter feature is added to the docs MCP.** | The MCP fallback in D12 targets only `arbiter serve`. | ANSWERED — User decision 2026-11-05 |
| D12 | Fallback tiers and how they are reported. | Each harness is exactly one of: **enforced-global** (hook installed at the global location), **enforced-project** (only a project hook exists or global is ignored; install at project scope only), **partial** (enforced for some dispatch paths; reason recorded), **fallback-only** (no hook; user registers `kyber-weave-arbiter serve` through the harness's MCP configuration). A harness whose block-on-dispatch cannot be shown at implementation time is **degraded with a recorded reason**, not blocked, and not withheld. Reason codes replace `global-hooks-unverified`: `fallback-only`, `no-hook-support` (retired for CommandCode, see matrix), `project-only`, `dispatch-unconfirmed`, `hooks-disabled-by-harness`. | U8. Autonomy check 2 is satisfied: every written path is vendor-verified. | ANSWERED — User decision 2026-11-05 (U8) |
| D13 | Install output. | `arbiter install` prints a table of every target: tier (D12), the **exact path written** and what was written (own file, owned block, plugin file, or nothing), and for each unenforced or degraded target the reason and the next step (for example register `arbiter serve`). It also lists harness-side off-switches and trust steps that stop enforcement: Codex one-time trust review and `[features] hooks=false`; Kilo `KILO_PURE=1`; VS Code `chat.useHooks` and a trusted workspace; ZCode `hooks.enabled: true`; Claude project settings wait on workspace trust. `--dry-run` prints the same table with "would write" and writes nothing. | U8 and U10 ("install output lists exactly what was written and what is enforced vs fallback-only"). | ANSWERED — User decision 2026-11-05 (U10) |
| D14 | Ownership boundary for every global write. | Only an **owned file** (Squad-created, receipt-listed) or an **owned marked block** (ADR 0054 mechanism) is ever added or removed; foreign entries and unrelated keys are byte-preserved, and a user-owned file (for example `~/.claude/settings.json`) is edited only inside the owned block. Nothing is written unless the user explicitly runs `arbiter install` (or `squad install --global` with Arbiter wiring); `update` refreshes only what the receipt owns. Flags the harness requires but the block does not own (ZCode `hooks.enabled`, Codex `[features] hooks`) are **never flipped**; the output reports them. | U10. | ANSWERED — User decision 2026-11-05 (U10) |
| D15 | ADR work. | Amend **ADR 0026** (Utilities/Squad ownership: owned block or owned file in a global hook location, including auto-loaded plugin directories, on explicit install only) and **ADR 0052** (state that its never-user-scope rule governs docs MCP configuration only and does not restrict Squad-owned hook blocks) **in place**; no superseding ADR; ADR 0054 cross-referenced. Rule `docs/rules/global-scope-by-default.md` is updated in the same task. | U10. | ANSWERED — User decision 2026-11-05 (U10) |
| D16 | Remaining unconfirmed points. | Carried in [todo/arbiter-global-hook-dispatch-coverage](../todo/arbiter-global-hook-dispatch-coverage.md), **non-blocking**: whether `task`/`runSubagent` fires the before-hook in OpenCode, Kilo and VS Code; Cursor global-vs-project precedence; harness release versions. Observed fallback: report the harness `degraded` (`dispatch-unconfirmed`) and keep the MCP `serve` path. Unverified Factory "`settings.json` shadows `hooks.json`" caveat: keep the existing renderer's shadow handling, treat as unverified. | U9. | ANSWERED — User decision 2026-11-05 (U9) |
| D11 | Global install and the full Squad deployment. | Like the project path, a global install renders the target through the Squad install pipeline at `SquadDeploymentScope.Global` with `Arbiter` wiring (the global Squad deployment is part of the pipeline today). Squad's global receipt (`~/.kyber-weave/squad/global/`) owns the files; hook blocks in shared user hook files use the ADR 0054 marked-entry mechanism. | `SquadHarnessRenderContext.EnabledArbiter` currently returns null for global scope — the single gate T2 changes. | ANSWERED — autonomous: low-confidence, no authoritative source |
| AP1 | Approve and execute this plan? | **APPROVED 2026-11-05.** Revision 1 was approved autonomously; revision 2 changed the contract (U8–U11, D12–D16, T0 removed, ADR 0026/0052 amendments), so the gate was presented again and the user chose "approve and execute" ("yes and yes"). | Declared scale `extends-component`; components `KyberArbiter`, `KyberSquad`, `Distribution`. | APPROVED — user, explicit, 2026-11-05 (conductor-relayed "approve and execute" gate) |

## Design harvest

`documentation-impact: extends-component`; `impact-components: [KyberArbiter, KyberSquad, Distribution]`.

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | KyberArbiter | adr | ADR 0053 amended in place: install is user-scope by default, project opt-in; global install renders user-level hooks; no "rejected" or "renders no hooks" wording survives. |
| H2 | KyberSquad | adr | ADR 0037 amended in place: user scope is the default scope for every feature; `~/.kyber-weave/` hosts the user-level config. |
| H10 | KyberSquad | adr | ADR 0026 amended in place (owned block or owned file in a harness's global hook location, including auto-loaded plugin directories, on explicit install only) and ADR 0052 amended in place (its never-user-scope rule governs docs MCP configuration only); ADR 0054 cross-referenced. |
| H3 | KyberArbiter | runbook | `arbiter install`/`uninstall` scope flags, defaults, dry-run, verification, shared key, project-overrides-global, migration notice. |
| H4 | KyberArbiter | architecture | Verified global-hook matrix (per-harness user-level location, format, trust step, tier, URLs), `--scope global` hook runtime (defer-to-project, user-config fallback, user-home ledger), `arbiter.global` schema, `KW-ARB-CONFIG-011`, `KW-ARB-INSTALL-001`, install-output table (D13). |
| H5 | Distribution | cli-command-surface.md | New flags/defaults for `arbiter install`/`uninstall`, exit codes, updated `update` verb (global refresh). |
| H6 | Distribution | install.md | One-command install is user-scope by default; `update` refreshes global hooks. |
| H7 | KyberSquad | onboarding | Arbiter hooks section: global trust steps and global deployment paths per harness. |
| H8 | KyberArbiter | configuration.md | User-level `~/.kyber-weave/kyber-weave.yml`, `arbiter.global.targets`. |
| H9 | KyberArbiter | rule | `docs/rules/global-scope-by-default.md` + `docs/rules/README.md` entry. |

## Investigation findings

Discovery: codegraph 1 (not indexed — repository has no `.codegraph/`; not retried), docs 0
(`docs_explore` not used; kyber-weave docs MCP tools are available only for the validate/drift gates in this session —
direct reads substituted), direct reads ~14 (CodeGraph unavailable: install/uninstall/refresher/renderer sources, ADR
0053/0037/0052 headers, cli-command-surface, configuration, install, archived arbiter plans), delegated 1
(`research-agent`, result judged unreliable — D2). Self-gathered lookups are labelled.

**Code state (self-gathered, verified by reading).**

- `ArbiterInstallCommand.Execute` returns `2` when `settings.Global`; everything after is project-only:
  `DocsScaffolder.Scaffold(root)`, project config path, `ReadReceipt(root, Project)`,
  `SquadInstallRequest(root, Project, …, Arbiter: wiring)`, `Verify(root, …)`.
- `ArbiterUninstaller` hard-codes `SquadDeploymentScope.Project` and project-relative `OwnedFiles`/`OwnedBlocks`;
  `ArbiterUninstallCommand` has no scope option.
- `ArbiterHookRefresher.Refresh(IReadOnlyList<string> projectRoots)` only builds `SquadUpdateRequest(root, Project, Arbiter)`
  for roots with `arbiter.enabled`; `SelfUpdater`/`SelfUpdateHost`/`UpdateCommand` supply the registered roots.
- `SquadHarnessRenderContext.EnabledArbiter` is `Request.Arbiter is { Enabled: true } && Scope == Project`;
  `ArbiterHookWiring.DegradationReason` returns `global-scope` for every target at global scope.
- Hook runtime (`HookCommand.RepoRootOf`, `Composition.LoadHostConfig`) takes the payload `cwd`/workspace as the repo root and
  fails closed (`KW-ARB-HOOK-001`) when `.kyber-weave/kyber-weave.yml` is absent; ledger/log live under
  `artifacts/arbiter/` of that root. There is no user-level `kyber-weave.yml` loader today
  (`KyberWeaveConfigLoader` is per-repo; only `~/.config/kyber-weave/arbiter.yml` provider override exists).
- `SquadGlobalRoots.ResolveGlobalRoot` already resolves a physical global root for all 13 targets
  (`~/.claude` (`CLAUDE_CONFIG_DIR`), `~/.codex` (`CODEX_HOME`), `~/.cursor` (`CURSOR_CONFIG_DIR`), `~/.copilot` (`COPILOT_HOME`),
  `~/.gemini/config`, `~/.pi/agent` (`PI_CODING_AGENT_DIR`), OpenCode, `$XDG_CONFIG_HOME/kilo`, `~/.factory`, `~/.warp`, ZCode, Devin, `~/.commandcode`).
- 13 targets: Codex, Cursor, Claude, Copilot, OpenCode, Kilo, Antigravity, Warp, Factory, Pi, ZCode, Devin, CommandCode. Project
  hooked today: all except Warp, ZCode (MCP fallback, `fallback-only`) and CommandCode (`no-hook-support`).

**Global-hook matrix — VERIFIED against vendor documentation, 2026-11-05 (user decision U9).**
Research was done in planning; it is not an execution task. Tiers are defined in D12; the renderer groups are T3a–T3d.
Where a harness supports a global hook location the install goes there (U8); the existing project renderer is unchanged.

| Harness | Global hook location (verified) | Owned artifact (D14) | Layers merge or override? | Block-on-dispatch / trust notes | Tier and group | Evidence |
|---|---|---|---|---|---|---|
| Claude Code | `~/.claude/settings.json` (user-owned settings) | owned marked block inside the user's settings file | project `.claude/settings.json` also runs, but waits on workspace trust | `preToolUse` deny: yes | enforced-global — T3a | https://code.claude.com/docs/en/settings.md , https://code.claude.com/docs/en/hooks.md |
| Copilot CLI and Copilot in VS Code | `~/.copilot/hooks/*.json` (or `$COPILOT_HOME/hooks`); one shared harness and format, JSON `version: 1`; VS Code agent-frontmatter hooks also exist at `~/.copilot/agents/*.agent.md` | owned file `~/.copilot/hooks/kyber-arbiter.json` | sources policy > user > project > plugins **all run** (no shadowing) | `preToolUse` deny: yes; VS Code needs `chat.useHooks` and a trusted workspace (Preview). **UNCONFIRMED:** whether VS Code `runSubagent` fires `preToolUse` (docs name only `subagentStart`/`subagentStop`) → degraded `dispatch-unconfirmed` for VS Code until shown (D16) | enforced-global (CLI); enforced-global, degraded (VS Code) — T3a | https://docs.github.com/en/copilot/reference/hooks-reference , https://code.visualstudio.com/docs/agent-customization/hooks |
| Codex | `~/.codex/hooks.json` or `~/.codex/config.toml` | owned block in `~/.codex/hooks.json` | all sources load, none replaces | one-time user trust review required; `[features] hooks=false` disables; docs explicit on blocking only for MCP-tool hooks. (Earlier "no global hooks" claim was wrong.) | enforced-global — T3b | https://developers.openai.com/codex/hooks |
| Cursor | `~/.cursor/hooks.json` | owned entries in `~/.cursor/hooks.json` | global-vs-project precedence **undocumented** (D16) | exit code 2 blocks; `preToolUse` supported; auto-reloads. (Earlier claim wrong.) | enforced-global — T3b | https://cursor.com/docs/hooks |
| Antigravity | `~/.gemini/config/hooks.json`; project `.agents/hooks.json` | owned group in `~/.gemini/config/hooks.json` | no merge policy documented | `PreToolUse` decision `deny`. (Earlier "likely gap" wrong.) | enforced-global — T3b | https://antigravity.google/docs/hooks.md |
| Factory | `~/.factory/hooks.json`; project `.factory/hooks.json` | owned block in `~/.factory/hooks.json` | `settings.json` `hooks` key is read only if `hooks.json` is absent; the "settings.json shadows hooks.json" caveat is **not confirmed** — unverified, keep the existing renderer's shadow handling | per existing Factory hook renderer | enforced-global — T3b | https://docs.factory.ai/cli/configuration/hooks-guide |
| Devin | `~/.config/devin/config.json` (also reads Claude-compatible config); project `.devin/hooks.v1.json` | owned block in the Devin global config | — | block via decision / exit 2 | enforced-global — T3b | https://docs.devin.ai/cli/extensibility/hooks/overview |
| ZCode | **global-only** `~/.zcode/cli/config.json` with `hooks.enabled: true`; project hooks explicitly **ignored** | owned hook block; `hooks.enabled` is **not owned and never flipped** (D14) — reported when absent/false (`hooks-disabled-by-harness`) | n/a | `PreToolUse` deny: yes. (Earlier "fallback-only/no hook" claim wrong.) | enforced-global (global-only; no project install) — T3b | https://zcode.z.ai/en/docs/hooks |
| CommandCode | hooks exist via mods (`cmd.hooks({ … beforeToolCall })`) and settings `~/.commandcode/settings.json`; user-scope mods load without a prompt, project mods are trust-gated | decided in T3b by a **design check of mods vs settings** (record the choice in the architecture matrix); owned file or owned block only | n/a | if block-on-dispatch cannot be shown → degraded with a recorded reason, never withheld (D12). (Earlier `no-hook-support` claim wrong.) | enforced-global or degraded — T3b | https://commandcode.ai/docs/settings , https://commandcode.ai/docs/mods |
| OpenCode | `~/.config/opencode/plugins/` (auto-loaded); project `.opencode/plugins/` | owned plugin file `~/.config/opencode/plugins/kyber-arbiter.ts` (needs the ADR 0026 amendment, D15) | arrays merge, none replace | before-hook blocks by throwing. **UNCONFIRMED by name:** that `task` fires `tool.execute.before` (generic docs only) → degraded `dispatch-unconfirmed` until shown (D16) | enforced-global, degraded — T3c | https://opencode.ai/v2/docs/plugins |
| Kilo | `~/.config/kilo/plugin/` (auto-registered); project `.kilo/plugin/` | owned plugin file `~/.config/kilo/plugin/kyber-arbiter.ts` (ADR 0026 amendment, D15) | order builtins > global config > global plugin dir > project config > project plugin dir; all run | block by throwing; `KILO_PURE=1` skips external plugins (surface in install output, D13); `task` dispatch unconfirmed (D16) | enforced-global, degraded — T3c | https://kilo.ai/docs/automate/extending/plugins |
| Pi | global `~/.pi/agent/hook/hooks.yaml` is a **third-party** package (pi-yaml-hooks), not core; the existing project extension `.pi/extensions/kyber-arbiter.ts` stays | **Pi-global (2026-11-06):** owned extension file `~/.pi/agent/extensions/kyber-arbiter.ts` under `SquadGlobalRoots` (`~/.pi/agent`) rendered **only** from the Pi body (`PiRenderer.RenderArbiterExtension` shape: `node:child_process` `spawnSync`, `export default function (pi) { pi.on("tool_call", …); pi.on("tool_result", …) }`), never from `ArbiterPluginShim` — the shared shim is a Bun/Plugin body (`Bun.spawn`, `tool.execute.before/after`) that the Pi loader cannot call; `Render("pi")` throws per PR 525. Tier stays **partial** until block-on-dispatch is shown (D16), with MCP fallback otherwise | n/a | — | **partial** — T3c | (Pi extension loader: `loader.js:486` requires a bare-function default export; verified against `pi-coding-agent` 1.4.0) |
| Warp | none — no hooks | nothing written; user registers `kyber-weave-arbiter serve` in `~/.warp/.mcp.json` (project `.warp/.mcp.json`); printed in the install output | n/a | MCP fallback only | **fallback-only** — T3d | (no hooks documented) |

Observed fallback for every unconfirmed point (D16): report the harness `degraded` with the reason and keep the MCP `serve` path.
Release versions are mostly unstated by the vendors and are recorded in the matrix only when the documentation states them.
The Arbiter MCP is its own server (`kyber-weave arbiter serve`), separate from the docs MCP (U11); nothing here adds Arbiter features to the docs MCP.
Every unenforced or degraded harness is emitted by the renderer as `arbiter-not-enforced` with a D12 reason code (T2), printed at install (D13), and shown by `arbiter doctor`.
The first external research pass (D2) was discarded; this table replaces it.

## Test contract (test-first)

RED authored by `test-dev` before implementation; GREEN by the matching developer. Implementers never author or weaken tests.
All `dotnet test` commands run through `scripts/proc-guard.sh run --` and check a non-zero total.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/UserHostConfigTests.cs` (new), `HostConfigYamlTests.cs`, `ArbiterConfigLoaderTests.cs` | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~UserHostConfigTests\|FullyQualifiedName~HostConfigYamlTests\|FullyQualifiedName~ArbiterConfigLoaderTests"` | User config path is `<home>/.kyber-weave/kyber-weave.yml` (home injectable); created when missing with `arbiter.enabled: true`, idempotent, comment/order preserving; `arbiter.global.targets` round-trips; `arbiter.global` in a project file fails `KW-ARB-CONFIG-011`. | Cases fail: resolver/creator and `global` key do not exist. | Same cases pass. |
| T2 | `tests/KyberWeave.Tests/ArbiterGlobalWiringTests.cs` (new), `ArbiterHookWiringTests.cs` | `… --filter "FullyQualifiedName~ArbiterGlobalWiringTests\|FullyQualifiedName~ArbiterHookWiringTests"` | At `Global` scope with enabled Arbiter, `EnabledArbiter` is non-null; hook command lines carry `--scope global`; project scope lines are byte-identical to today; degradation `global-scope` is replaced by the D12 tier and reason codes (`fallback-only`, `project-only`, `dispatch-unconfirmed`, `hooks-disabled-by-harness`); disabled Arbiter at global renders byte-identically to today. | Fail: global scope returns null/`global-scope` for every target. | Pass; existing project-scope golden tests untouched and green. |
| T3a | `ArbiterGlobalClaudeCopilotRenderingTests.cs` (new) | `… --filter "FullyQualifiedName~ArbiterGlobalClaudeCopilot"` | Claude: an owned marked block is added to and removed from the injected `~/.claude/settings.json`; every foreign key, hook and byte outside the block is preserved; a file without the block is never created by uninstall. Copilot CLI/VS Code: owned file `<COPILOT_HOME>/hooks/kyber-arbiter.json` (JSON `version: 1`, `--scope global`), `$COPILOT_HOME` honoured; VS Code `dispatch-unconfirmed` reason recorded. | Fail: nothing rendered at global scope. | Pass, no assertion weakened. |
| T3b | `ArbiterGlobalHookFileRenderingTests.cs` (new) | `… --filter "FullyQualifiedName~ArbiterGlobalHookFile"` | Shared-file owned-block groups under the injected global roots: Codex `hooks.json`, Cursor `hooks.json`, Antigravity `~/.gemini/config/hooks.json`, Factory `hooks.json` (existing shadow handling kept), Devin global config, ZCode `config.json` (owned hook block only; `hooks.enabled` never written and reported when not `true`), CommandCode (mods-vs-settings choice recorded; degraded reason if block cannot be shown). Blocks are receipt-owned, ADR 0054 marked, foreign entries byte-preserved, content carries `--scope global`. | Fail: nothing rendered at global scope. | Pass. |
| T3c | `ArbiterGlobalPluginRenderingTests.cs` (new), `ArbiterGlobalPluginDirRenderingTests.cs` (existing pins) | `… --filter "FullyQualifiedName~ArbiterGlobalPlugin"` | Auto-load plugin directory groups: owned files `~/.config/opencode/plugins/kyber-arbiter.ts`, `~/.config/kilo/plugin/kyber-arbiter.ts`, and the Pi owned global extension `~/.pi/agent/extensions/kyber-arbiter.ts` rendered **from the Pi body** (`spawnSync` + `pi.on`, never `ArbiterPluginShim.Render("pi")` — `Render("pi")` throws, PR 525); OpenCode/Kilo shims resolve the project root by walking up and pass it, else defer; never spawn with an unverified cwd; written only on explicit install; uninstall removes only the owned file. **RED includes the loader-path behaviour pins:** the Pi global file imported as `ext({ on })` must register `tool_call`/`tool_result` and block/allow via the default-export function (Bun, fake `kyber-weave-arbiter` binary, as `ArbiterShimBehaviourTests.PiExtension_*` does for project scope); OpenCode/Kilo global shims imported via `setup`/`server` must block on block and allow on allow. | Fail: nothing rendered at global scope. | Pass. |
| T3d | `ArbiterGlobalFallbackRenderingTests.cs` (new) | `… --filter "FullyQualifiedName~ArbiterGlobalFallback"` | Fallback group (Warp, and any target degraded to fallback): nothing is written; the install output names `kyber-weave-arbiter serve` and the harness's MCP config path (`~/.warp/.mcp.json`); the target is classed `fallback-only`. | Fail: no fallback classification at global scope. | Pass. |
| T4 | `tests/KyberWeave.Tests/ArbiterGlobalHookRuntimeTests.cs` (new), existing `ArbiterHookCommandTests.cs` | `… --filter "FullyQualifiedName~ArbiterGlobalHookRuntimeTests\|FullyQualifiedName~ArbiterHookCommand"` | `--scope global`: project config with `arbiter.enabled: true` → pass-through logged `deferred-to-project`, no ledger write; project absent/`enabled: false`/no config → evaluates with the user config, log and ledger under `<home>/.kyber-weave/arbiter/projects/<root-key>/`; user config missing → fail closed `KW-ARB-HOOK-001`; project scope behaviour unchanged. | Fail: `--scope` unknown; missing project config fails closed. | Pass. |
| T5 | `tests/KyberWeave.Tests/ArbiterInstallCommandTests.cs` (extend) | `… --filter "FullyQualifiedName~ArbiterInstallCommandTests"` | No flag = global: user config created/merged, global receipt written, `arbiter.global.targets` recorded; `--project` = today's behaviour; `--global --project` exit `2` (`KW-ARB-INSTALL-001`); `--global` exit `0` (no longer `2`); `--path` without `--project` exit `2`; `--dry-run` writes nothing at either scope and lists the planned global files; second run is an idempotent no-op; key written only to the injected store and shared across scopes; verification failures exit `1`; bare install in a repo with a project install prints the migration notice; the install output (also under `--dry-run`, as "would write") lists for every target its D12 tier, the exact path and artifact written (or "nothing"), the reason and next step for each fallback-only/degraded target, and the harness off-switches and trust steps (D13). | Fail: `--global` still exits 2; no `--project` option. | Pass; key asserted only via injected store. |
| T6 | `ArbiterUninstallCommandTests.cs` (extend) | `… --filter "FullyQualifiedName~ArbiterUninstallCommandTests"` | Bare/`--global` uninstall removes only global-receipt-owned files/blocks and the target from `arbiter.global.targets`; `--project` as today; foreign files untouched; no install exits `0`; `--remove-key` warns when the other scope still uses the origin. | Fail: no scope handling. | Pass. |
| T7 | `SelfUpdaterArbiterRefreshTests.cs`, `UpdateCommandTests.cs`, `ArbiterHookRefresherTests.cs` (new) | `… --filter "FullyQualifiedName~SelfUpdaterArbiterRefreshTests\|FullyQualifiedName~UpdateCommandTests\|FullyQualifiedName~ArbiterHookRefresherTests"` | `update` reads `~/.kyber-weave/kyber-weave.yml`; refreshes each `arbiter.global.targets` target at global scope and each registered `arbiter.enabled` project; a missing/unreadable user config or failed global refresh warns and keeps the update's exit code; `--no-arbiter` skips both. | Fail: refresher has no global path. | Pass. |
| T8 | `ArbiterDoctorCommandTests.cs`, `ArbiterStatusCommandTests.cs` (extend) | `… --filter "FullyQualifiedName~ArbiterDoctorCommandTests\|FullyQualifiedName~ArbiterStatusCommandTests"` | `status`/`doctor` report global and project scope separately, state "project overrides global", list per-harness gaps, warn when a project is `enabled: true` with no project hooks. | Fail: output has no scope section. | Pass. |
| T9–T10 | none — docs | n/a | `docs validate . --merge-ready` and `docs drift .` at zero findings; ADR 0026 and ADR 0052 are amended in place and agree with `docs/rules/global-scope-by-default.md`; no rule or ADR says "named exceptions"; no document says the install is project-only or that `--global` is rejected (grep gate in T10 acceptance). | n/a | Gates pass. |

## Tasks

### T0 — removed (2026-11-05, U9)
Verification of the global-hook matrix was research, completed in planning; the verified matrix is in Investigation findings. The id `T0` is retired and not reused.

### T1 — User-level host config (Core/Configuration)
Required skills: csharp. Files: new `src/KyberWeave.Core/Configuration/UserHostConfig.cs`; `HostConfigYaml` (`WithArbiterGlobalTargets`, create-if-missing); `ArbiterConfig` + `ArbiterConfigLoader` (`Global.Targets`, `KW-ARB-CONFIG-011`). Dependencies: RED for T1.

### T2 — Global-scope Arbiter wiring (Core/Squad)
Required skills: csharp. Files: `SquadRenderModels.cs`/`SquadHarnessRenderContext.EnabledArbiter`, `ArbiterHookWiring.cs` (`DegradationReason`, `HookCommandLine(…, scope)`), `SquadHarnessRenderer.cs` helpers, `SquadLifecycleService` global update path. Dependencies: RED for T2.

### T3a — Settings-owned block and own-file hook (Claude Code; Copilot CLI and VS Code)
Required skills: csharp. Files: `ClaudeRenderer.cs` (owned block in `~/.claude/settings.json`, ADR 0054 marker, byte-preserving splice and removal), `CopilotRenderer.cs` (owned file `~/.copilot/hooks/kyber-arbiter.json`, `$COPILOT_HOME`), shared helpers from T2. Dependencies: T2.

### T3b — Shared-file owned-block groups (Codex, Cursor, Antigravity, Factory, Devin, ZCode, CommandCode)
Required skills: csharp. Files: the matching `*Renderer.cs`. Each writes only an ADR 0054 owned block in the harness's global hook file; Factory keeps the existing shadow handling; ZCode never writes `hooks.enabled` and the install output reports it (D14); CommandCode starts with a short design check of mods vs `~/.commandcode/settings.json`, recorded in the architecture matrix, and degrades with a recorded reason if block-on-dispatch cannot be shown. Dependencies: T2. One worker per renderer file (disjoint files).

### T3c — Auto-load plugin directory groups (OpenCode, Kilo; Pi partial)
Required skills: csharp. Files: `OpenCodeRenderer.cs`, `KiloRenderer.cs`, `PiRenderer.cs`, `ArbiterPluginShim.cs` (global shim walks up for the project root and passes `--scope global`; never spawns with an unverified cwd). Owned files only, written on explicit install only (ADR 0026 amendment, T9); Pi-global renders **only** the Pi-extension body (`RenderArbiterExtension` shape) to `~/.pi/agent/extensions/kyber-arbiter.ts` under `SquadGlobalRoots` — it must not call `ArbiterPluginShim.Render("pi")` (`Render("pi")` throws, PR 525, because the shared Bun/Plugin body cannot register Pi hooks); tier stays `partial` until block-on-dispatch is shown (D16), with MCP fallback otherwise. Dependencies: T2; T9 text fixed (D10/D15).

### T3d — MCP-fallback group (Warp; any degraded-to-fallback target)
Required skills: csharp. Files: `ArbiterHookWiring.cs` (tier/reason table, D12), `WarpRenderer.cs` (no write; reports `~/.warp/.mcp.json` registration of `kyber-weave-arbiter serve`). The Arbiter MCP is its own server; nothing is added to the docs MCP (U11). Dependencies: T2.

### T4 — Hook runtime `--scope global` (KyberWeave.Arbiter)
Required skills: csharp. Files: `src/KyberWeave.Arbiter/Hooks/HookCommand.cs`, `Composition.cs` (`LoadHostConfig` scope-aware), ledger/decision-log path provider, `Hooks/*Adapter` untouched. Dependencies: T1 (user-config loader).

### T5 — `arbiter install` scope surface (CLI)
Required skills: csharp. Files: `ArbiterInstallSettings.cs` (`--global` real, `--project`, help text), `ArbiterInstallCommand.cs` (scope-parameterised root/config/receipt/confirmation/verify/dry-run/notice, and the D13 install-output table: tier, exact path and artifact written, reasons, harness off-switches and trust steps; `--dry-run` as "would write"), `SquadTargetRootConfirmation` reuse. Dependencies: T1, T2, T3a–T3d (tier data).

### T6 — `arbiter uninstall` scope surface (CLI)
Required skills: csharp. Files: `ArbiterUninstallSettings.cs`, `ArbiterUninstallCommand.cs`, `ArbiterUninstaller.cs` (scope, global-root-relative owned files/blocks via `SquadGlobalRoots`). Dependencies: T1, T2.

### T7 — Update-time global refresh (CLI Update)
Required skills: csharp. Files: `IArbiterHookRefresher.cs`, `ArbiterHookRefresher.cs` (global refresh from the user config), `SelfUpdater.cs`/`SelfUpdateHost.cs`/`UpdateCommand.cs` wiring and output line. Dependencies: T1, T2.

### T8 — `arbiter status` / `doctor` scope awareness (CLI)
Required skills: csharp. Files: `ArbiterStatusCommand.cs`, `ArbiterDoctorCommand.cs`. Dependencies: T1.

### T9 — ADR amendments and rule (docs-dev)
Required skills: kyber-docs-authoring. Files: `docs/adr/0053-…md` (reverse install-scope statements in the Decision list, line ~112, line ~149 and `arbiter-not-enforced` global wording; add amended note), `docs/adr/0037-…md` (title/summary and the user-scope-default section), **`docs/adr/0026-…md` amended in place** (Squad may add or remove only its own marked/owned hook block or owned file in a harness's global hook location, including auto-loaded plugin directories for OpenCode and Kilo, only when the user explicitly runs the install command, never touching anything else in the file; install output lists what was written and what is enforced vs fallback-only; the "never edits shared settings / no writes to auto-load directories / manual activation" wording is rewritten to state the amended decision, not left beside an exception; cross-reference ADR 0054), **`docs/adr/0052-…md` amended in place** (the docs-branch `mcp-write` rule is unchanged; state that it concerns docs MCP configuration and does not restrict Squad-owned hook blocks or the Arbiter install), `docs/adr/README.md` row text, new `docs/rules/global-scope-by-default.md` (rule worded per D10, no "named exceptions"), `docs/rules/README.md`. The todo `docs-mcp-write-user-scope-decision` is already removed (resolved by U10). Dependencies: none (decision text is fixed in D10/D15).

### T10 — Docs harvest (docs-dev)
Required skills: kyber-docs-authoring. Files: `docs/cli-command-surface.md` (install/uninstall rows, `update` row, exit-code row), `docs/kyber-arbiter/runbook.md` (~line 140 "`--global` is rejected"), `docs/kyber-arbiter/architecture.md` (~line 191 and the verified matrix with its vendor URLs), `docs/configuration.md`, `docs/install.md`, `docs/kyber-squad/onboarding.md`, `docs/catalog.md` (affected only if a component row's summary mentions scope — check), `docs/plans/README.md`. Acceptance: zero hits for "project-scoped", "`--global` (rejected)", "renders no hooks" in non-archive docs; `docs validate . --merge-ready` and `docs drift .` zero findings. Dependencies: T5–T8, T9 (documents real behaviour).

## Dependency graph

```
T1 ──► T4, T5, T6, T7, T8
T2 ──► T3a, T3b, T3c, T3d, T5, T6, T7
T3a–T3d ──► T5
T9 (leaf; fixes the ADR text T3c relies on)
T10 ◄── T5, T6, T7, T8, T9   (last)
```
RED precedes GREEN per task. With `review.max-concurrent-workers` = 4: T1, T2, T9 start together; T3a–d, T4–T8 fan out after T1/T2.

## Risks

- **Wrong global path written into a user's home** (expensive): every written path is vendor-verified (matrix); points the vendors leave open degrade the harness with a recorded reason (D12/D16) rather than block.
- **Double gating** when harnesses merge hook layers: D4 defer-to-project; covered by T4 tests.
- **Default flip surprises scripts** (D7): notice line, exit-code stability, release note.
- **Writing into a user's shared harness config or an auto-load directory** (ADR 0054 marked entries; amended ADR 0026/0052): only owned blocks/files, explicit install only, foreign entries byte-preserved (T3a/T3b tests), exact written paths listed (D13).
- **Ungated project** when `enabled: true` but no project hooks: `doctor` warning (T8).
- Wide change surface (Core, Squad, Arbiter, CLI): one worker per scope above; the full suite is CI's job.

## Out of scope

Reversing ADR 0052's docs-branch `mcp-write` rule (unchanged, D10); adding Arbiter features to the docs MCP (U11); new harness support; changing the provider/key model; Warp MCP auto-registration (Req 6.5); KyberDash/tray changes; creating any superseding ADR.

## Verification gates

`dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`; `docs validate . --merge-ready`; `docs drift .` (zero findings);
targeted tests per the Test contract; each developer task ends with its own `task-gate`.

## Review

One council review over the whole change (conductor-invoked `code-reviewer`) after the draft PR is pushed.

## Review findings remediation (final council, REQUEST_CHANGES, branch head 65142477)

Mediated 2026-11-05. The approved scope, Decisions table and Test contract are **unchanged**: every accepted finding
restores behaviour the approved plan already requires (U4, D11, D12, D14, T3b, T3d, T6, plan "Out of scope" Warp line).
Rework follows the plan's test-first rule: the named RED test is authored by `test-dev` first (and must fail on the current head),
then the owner implements; implementers never edit the RED test. Each worker ends with its own `task-gate`.
Not findings (recorded, no action): the two `ArchiveCloseoutScopeTests` fail on `main` too (machine git config);
`ProcessConsoleCaptureTests` and `RepositoryDoctorTests` are flaky under full-suite concurrency and pass in isolation.

| Finding | Verdict | Plan authority | Task |
|---|---|---|---|
| F1 CRITICAL global install scaffolds docs into `$HOME` | Valid, in scope | U4: "mirrors the project path … **without the docs scaffold**" | R1 |
| F2 MAJOR global Warp `.mcp.json` write | Valid, in scope (the write is out of scope; removing it is the fix) | Matrix row Warp "nothing written"; T3d "no write"; T3d test "nothing is written"; Out of scope "Warp MCP auto-registration (Req 6.5)"; D14 (only receipt-owned files/blocks). The conductor packet for T3d that asked workers to write the entry contradicted the approved plan. | R2 |
| F3 MAJOR global uninstall (a) receipt scope, (b) Claude shape, (c) user config | Valid, in scope; larger than reported (see R3) | T6 "global-root-relative owned files/blocks via `SquadGlobalRoots`", "removes only global-receipt-owned files/blocks and the target from `arbiter.global.targets`"; D14; D11 | R3 |
| F4 MAJOR CommandCode reason code | Valid, in scope | D12 (`no-hook-support` retired for CommandCode; degraded with recorded reason; reason must describe what is rendered); T3b design-check record | R4 |
| F5 MINOR project shim output gains a whitespace line | Valid, in scope | T2/T3c acceptance "project scope lines are byte-identical to today" | R5 |
| F6 MINOR non-atomic user-config write | Valid, in scope (hardening) | Repo convention: `KyberWeave.Core.FileSystem.AtomicFile.WriteAllText` exists and is used by `SquadStateStore`, `SquadTransaction` | R6 |

### Decision R-D1 — CommandCode reason code (autonomous, low-confidence; reversible in code)

`CommandCodeRenderer` renders no Arbiter hook at either scope (T3b design check not recorded; block-on-dispatch not shown).
D12 forbids both `no-hook-support` and withholding the target, and `project-only` / `enforced-global` are false for it.
**Decision:** CommandCode is classed `dispatch-unconfirmed` (degraded) at **both** scopes, install output next step
"register `kyber-weave-arbiter serve` through CommandCode's MCP configuration", and the T3b design-check outcome
(mods `beforeToolCall` vs `~/.commandcode/settings.json`; not rendered because block-on-dispatch is unproven) is recorded in
`docs/kyber-arbiter/architecture.md` by the T10 harvest. Alternative: class it `fallback-only` (adds it to `FallbackTargets`;
changes the "3 fallback records" legacy assertions); rejected as a wider change for the same user-visible outcome.
Sources searched: plan D12/D16, matrix CommandCode row, `ArbiterHookWiring.DegradationReason`, `CommandCodeRenderer` (no hook rendering).
`project-only` stays a known code for the roster-parameterised overload (hypothetical rosters) but no declared target reaches it today.

### OPEN_QUESTION (non-blocking; does not gate R1–R6) — Warp MCP registration

The user wanted "no hooks ⇒ fall back to the Arbiter MCP `serve` server". The approved plan satisfies this by **reporting**
the registration step (path `~/.warp/.mcp.json`, command `kyber-weave-arbiter serve`) in the install output, and explicitly puts
auto-registration out of scope (Req 6.5; D14 ownership boundary). If the user instead wants Squad to **write** the entry, that is a scope and
decision change (new owned-block mechanism for a JSON `mcpServers` entry, receipt ownership, uninstall removal, new ADR 0026 wording, a new Test contract row) and needs a
plan revision and renewed AP approval. **Recommendation:** keep the approved behaviour (report only); revisit as a separate plan if desired.
R2 proceeds on the approved behaviour regardless.

### Fix tasks

All tasks: RED first by `test-dev` (fails on head 65142477), then the owner agent. Test commands use
`scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "<filter>"` with a non-zero total check.

**R1 — No docs scaffold at global install (F1). Owner: `csharp-dev`. Files:** `src/KyberWeave.Cli/Commands/Arbiter/ArbiterInstallCommand.cs`
(guard the `DocsScaffolder.Scaffold(root)` block, ~lines 167–181, with `isProjectScope`; global creates only the user config via `UserHostConfig`).
**RED (test-dev):** in `ArbiterInstallCommandTests`, `Install_DefaultGlobalScope_WithoutUserConfig_CreatesUserConfigAndNoDocsScaffold`:
injected temp home with no `~/.kyber-weave/kyber-weave.yml`; run bare install for a hooked target; assert `<home>/.kyber-weave/kyber-weave.yml` exists and
`<home>/AGENTS.md`, `<home>/docs`, and any other `DocsScaffolder` output do not. Also assert `--project --path <repo>` still scaffolds (unchanged).
**Acceptance:** test green; existing project-scope tests green and unchanged; `--dry-run` writes nothing at either scope.

**R2 — Remove the unowned Warp `.mcp.json` write (F2). Owner: `csharp-dev`. Files:** `src/KyberWeave.Cli/Commands/Squad/SquadInstallCommand.cs`
(delete the Warp block ~lines 228–236, `WriteGlobalWarpArbiterMcpEntry`, `IsOwnedWarpArbiterServer` and any now-unused usings/`_globalRoots` plumbing added only for it);
confirm `ArbiterHookWiring.TrustSteps[Warp]` / the D13 install table name `~/.warp/.mcp.json` and `kyber-weave-arbiter serve` with a correct, copy-pasteable
`--repo-root` placeholder (never `~/.warp`). **RED (test-dev):** in `ArbiterGlobalFallbackRenderingTests` (T3d file) add
`GlobalWarpInstall_WritesNothingAndReportsMcpRegistration`: after a global Squad install with Arbiter wiring and Warp selected,
`<warp-root>/.mcp.json` does not exist and the output contains `kyber-weave-arbiter serve` and `.mcp.json`; and a second case with a pre-existing foreign `.mcp.json` asserts it is byte-identical afterwards.
**Acceptance:** green; no file written by Squad outside the receipt; uninstall needs no Warp handling. If any existing test asserted the write, test-dev rewrites it to the above (the write contradicted the approved T3d row).

**R3 — Global uninstall driven by the global receipt (F3a/b/c). Owner: `csharp-dev`. Files:** `src/KyberWeave.Cli/Commands/Arbiter/ArbiterUninstaller.cs`,
`ArbiterUninstallCommand.cs`, `IArbiterUninstaller.cs` (scope parameter), `src/KyberWeave.Core/Configuration/UserHostConfig.cs` (+ `HostConfigYaml` if needed: `RemoveGlobalTarget`, preserving other targets/comments).
Required behaviour: (a) construct the uninstaller with the scope; at global scope read `ReadReceipt(<home>, SquadDeploymentScope.Global)` and remove only receipt-listed files/blocks for the target, resolving paths through `SquadGlobalRoots` (the current code joins the project-relative `.kilo/plugin/...`, `.github/hooks/...`, `hooks.json` onto `$HOME`, which is wrong for every global harness, not only Cursor/Codex);
(b) remove Claude's global block by the shape actually rendered: the ADR 0054 owned block in `<claude-root>/settings.json` written by `ClaudeRenderer` (`SquadRenderedBlock` with `SquadHookBlockFormat.Cursor`, `PreToolUse`/`PostToolUse` entries) using `SquadHookJsonBlock.RemoveContent`, byte-preserving every foreign key and byte; delete the invented root-key `"kyber-arbiter"` path and its whole-file re-serialization (which also breaks D14's byte preservation); `HasDeployment` must use the same detection; (c) after a successful global removal remove the target token from `arbiter.global.targets` in the user config (atomic write, R6), leaving other targets and comments intact; absent user config is not an error. `--remove-key` multi-scope warning (D8) stays.
**RED (test-dev), in `ArbiterUninstallCommandTests`:** (1) **Replace** `Uninstall_DefaultScope_GlobalRemovesOnlyOwnedClaudeBlockAndPreservesForeignBytes`: its fixture hard-codes a `"kyber-arbiter"` root key that no renderer produces, so it passes vacuously. New fixture: render the real global Claude block with the Squad pipeline (or copy the exact `ClaudeRenderer` global output) into a `settings.json` surrounded by foreign keys and hooks; assert after uninstall the owned entries are gone and the remainder is byte-identical to the pre-install file. (2) `Uninstall_DefaultScope_GlobalRemovesSharedFileBlock_Cursor` (and one of Codex/Antigravity): global install then bare uninstall leaves the foreign entries byte-identical and the owned block gone. (3) `Uninstall_DefaultScope_GlobalRemovesOwnedPluginFile_OpenCode` at the global root, foreign sibling file untouched. (4) `Uninstall_Global_RemovesTargetFromArbiterGlobalTargets`: two targets recorded, one uninstalled, the other and all comments remain, and `update`'s refresher input no longer lists the removed target. (5) project-scope uninstall tests unchanged and green.
**Acceptance:** all green; no whole-file re-serialization of user files; nothing outside the global receipt is touched.

**R4 — CommandCode and tier classification match what is rendered (F4). Owner: `csharp-dev`. Files:** `src/KyberWeave.Core/Squad/Rendering/ArbiterHookWiring.cs` (`DegradationReason`: CommandCode → `dispatch-unconfirmed` at both scopes per R-D1; `project-only` retained only for the set-parameterised overload; update the `TargetDegradations` remarks), D13 next-step text for CommandCode; `docs/kyber-arbiter/architecture.md` is T10's (docs-dev adds the design-check record then).
**RED (test-dev), in `ArbiterHookWiringTests` (owner of the T2 legacy test):** (1) `TargetDegradations_GlobalScope_CommandCodeIsDispatchUnconfirmed_NotEnforcedGlobal`; (2) rewrite `TargetDegradations_ProjectScope_ClassifiesEveryApprovedTarget` so `commandcode` expects `dispatch-unconfirmed` (it currently pins `project-only`, which T2 introduced by editing the old `no-hook-support` expectation; the old expectation was itself the pre-plan behaviour), keeping the three fallback assertions; (3) a table test pinning that every target whose renderer emits no Arbiter hook at global scope is never classed `enforced-global` (CommandCode, and Pi only as `partial`). The test-dev records in the commit message that the legacy assertion changed by D12/R-D1, not by weakening.
**Acceptance:** green; `ArbiterGlobalHookFileRenderingTests` CommandCode case consistent; install table shows CommandCode with reason and next step.

**R5 — Byte-identical project shim output (F5). Owner: `csharp-dev`. Files:** `src/KyberWeave.Core/Squad/Rendering/ArbiterPluginShim.cs` (~line 192), `PiRenderer.cs` (~line 442): emit the `globalCommandLineStatement` line (and its newline) only when non-empty so project scope has no added whitespace line.
**RED (test-dev):** `ArbiterPluginShimTests.ProjectScope_Output_IsByteIdenticalToMainGolden` for OpenCode, Kilo and Pi project outputs, comparing against the golden bytes from `main` (`git show main:` fixture or existing golden files; test-dev captures them from `main` and checks them in as a fixture); global-scope output remains as asserted by T3c.
**Acceptance:** green; existing project golden tests unchanged and green.

**R6 — Atomic user-config writes (F6). Owner: `csharp-dev`. Files:** `src/KyberWeave.Core/Configuration/UserHostConfig.cs` (lines ~28 and ~44: use `KyberWeave.Core.FileSystem.AtomicFile.WriteAllText`, the helper `SquadStateStore` and `SquadTransaction` use; keep the existing create-directory and idempotent no-op behaviour; keep UTF-8 without BOM as today).
**RED (test-dev), in `UserHostConfigTests`:** `EnsureUserConfig_And_WriteGlobalTargets_LeaveNoTempFilesAndPreserveContentOnFailure`: after a write no `*.tmp`/sibling temp file remains in `<home>/.kyber-weave/`; an injected failure path (read-only target directory or locked file, as `AtomicFile` tests do) leaves the previous bytes intact. If `AtomicFile` offers no seam for the failure case, assert only the no-temp-file and exact-bytes behaviour.
**Acceptance:** green. Dependency: none; R3 consumes the same file, so R3 starts after R6 (shared `UserHostConfig.cs`).

Dependency graph: `R6 → R3`; R1, R2, R4, R5 independent of each other and of R3/R6 (disjoint files; R1 and R3 both touch the CLI Arbiter folder but different files). R4's doc record is picked up by the existing T10 harvest (add "CommandCode design-check outcome" to the architecture matrix acceptance). Run order with `review.max-concurrent-workers` = 4: RED for all six together, then R1, R2, R4, R5, R6 in parallel, then R3.
After all R-tasks: rerun the targeted filters for T1–T8, `review gates`, `docs validate`, `docs drift`, then the council re-review (conductor-invoked), scoped to the changed files.

## Closeout (`docs-dev`)

On completion, `docs-dev` harvests H1–H10, archives this plan to `docs/archive/plans/`, moves the index row to **Archived Plans**, and runs `docs validate . --merge-ready` and `docs drift .`. ADR handling: amendments only — no ADR waiver is claimed for H1/H2.
