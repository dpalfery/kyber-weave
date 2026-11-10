---
id: plans/copilot-system-prompt-override
title: Opt-in replacement of GitHub Copilot's top-level system prompt in VS Code
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
created: 2026-11-10
last-reviewed: 2026-11-10
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberSquad]
---

# Opt-in replacement of GitHub Copilot's top-level system prompt in VS Code

**Status: Archived, 2026-11-10.** Approved by the owner 2026-11-10 (decision AP1). Development mode `test-first`; autonomy `autonomous`; scale `extends-component` on `KyberSquad`.

**Bottom line.** VS Code does not let this setting be set per project. Its source declares it application scope, its workspace and folder settings parser drops application-scope keys, and one background process serves every window. A live test confirmed it: a project-only value forwards as `{}`. The owner chose one machine-wide user setting, and one prompt whose text tells the model to use Kyber-Weave routing only when the open repository has `.kyber-weave/kyber-weave.yml`.

## Problem and goal

**Problem.** Copilot's default top-level prompt, about 22,000 characters and larger with host additions, pushes the top-level agent to do the work itself rather than route it. Kyber-Weave's guard works around that: PR 576's agent-scoped deny hook, which blocks the conductor from doing work it should route, with the conductor kept as an agent the person selects. VS Code has an internal setting that replaces the prompt outright.

**Goal.**

1. One opt-in Kyber-Weave command, off by default, replaces the top-level system prompt in VS Code stable and Insiders, and one command undoes it.
2. The prompt ships in the package, is short, is safe without Copilot's own safety text, asks the person to select the conductor agent for non-trivial work in a Kyber-Weave repository, and stays useful in a plain chat.
3. Other entries and comments in `settings.json` survive.
4. `kyber-weave doctor` reports the silent failure, where a missing file or bad YAML falls back to Copilot's default prompt.
5. PR 576's agent-scoped deny hook stays as it is, beside the prompt. A prompt is instruction-only and does not change what a custom agent may call.

## Decisions

Permanent state, preserved across the plan's whole lifecycle.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| Q1 | The owner expected the override to be settable per project. It cannot be. Which route does the feature take? | **One machine-wide user setting, with one prompt that switches to Kyber-Weave routing only in repositories that have `.kyber-weave/kyber-weave.yml`.** | Evidence is under "Scope question" and "Scope test result" in this plan. Rejected: a separate VS Code `--user-data-dir` per project, which is real isolation but needs its own sign-in, extensions and launch command; and dropping the feature. Accepted cost: every Copilot chat on the machine loses Copilot's own prompt, including chats in repositories without Kyber-Weave, until `uninstall` runs. | ANSWERED: owner decision 2026-11-10, relayed by the conductor, verbatim "Yes, machine-wide". |
| D2 | Which command turns it on? | A new branch, `kyber-weave squad copilot-prompt install`, `uninstall` and `status`. Not a flag on `squad install`. | There is no top-level `kyber-weave install`, so the report's `install --copilot-system-prompt` cannot exist as written (`src/KyberWeave.Cli/Program.cs`, the `squad` branch). `squad install` defaults to project scope and writes through a receipt; this change is per-user and edits a file Kyber-Weave does not own, so it should never be a side effect. `KyberUtilities` is ruled out: its catalog entry says it never edits a shared harness settings file. | ANSWERED: autonomous, covered by existing command structure. |
| D3 | Where does the opt-in state live? | No receipt. The state is the prompt file at `~/.kyber-weave/copilot/system-prompt.yaml` plus a `*` entry whose `promptOverrideFile` equals that path. | Ownership can be read from the two facts, and nothing can drift from them. `~/.kyber-weave` is the existing home-level state root (`ISquadUserPaths.HomeDirectory`, `.kyber-weave/squad`). | ANSWERED: autonomous, low-confidence, no authoritative source; cheap to reverse. |
| D4 | Where does the prompt live and when is it regenerated? | Source `products/kyber-squad/copilot/system-prompt.yaml`, embedded in `KyberWeave.Core`. Written on `install`, and rewritten by `kyber-weave update` only if the file already exists. | Embedding ties prompt to binary, so only a self-update can change it, and it needs no network. `KyberWeave.Core.csproj` already embeds `products/kyber-squad` files. The report proved a rewritten file is used by the next session with no restart. `run start` is too late: the prompt freezes at session launch. | ANSWERED: autonomous, covered by the report's measurement and existing pattern. |
| D5 | How is `settings.json` edited? | Reuse `JsonServerSplice`: key path `["chat.agentHost.copilot.modelCapabilityOverrides"]`, entry name `*`, body `{"promptOverrideFile": "<absolute path>"}`; removal through the plan that returns `JsonSpliceOutcome.Removed`. | It is a byte splice that keeps comments, key order, trailing commas and byte-order mark, and refuses rather than guessing. Writes go through `AtomicFile`. Rejected: a new JSONC library, against the core dependency policy; re-serialising, which drops comments. | ANSWERED: autonomous, covered by `JsonServerSplice` and its tests. |
| D6 | What if a `*` entry already exists? | Same `promptOverrideFile`: no change, exit 0. Anything else: refuse that file, change nothing, exit 1, and name the entry. | Silent overwrite would destroy the owner's own override. Other model-id entries are left alone; VS Code gives a specific model entry priority field by field, and `status` reports it. | ANSWERED: autonomous, low-confidence, no authoritative source; cheap to reverse. |
| D7 | Which VS Code flavours and which settings file? | `Code` and `Code - Insiders`, default profile `User/settings.json`, for each flavour whose `User` directory exists. `--flavour stable\|insiders` narrows it. | Application settings are read from `userDataProfilesService.defaultProfile.settingsResource`, so another profile's file has no effect. Paths per OS match `vscodeSettingsPath` in `dash/src/capture/harnesses/copilot.ts`. | ANSWERED: autonomous, covered by VS Code source. |
| D8 | How strict is the doctor? | Three checks per configured flavour: setting present, file parses and holds only `systemPrompt` and `toolDescriptions`, newest log shows `mode=replace`. They run only when the prompt file exists. Under `--ensure` a failure is a warning. | An opted-out machine must see nothing. `--ensure` starts code-editing runs; a machine-wide prompt fault should not stop work in an unrelated repository. No session in the logs yet is a warning, not a failure. | ANSWERED: autonomous, low-confidence, no authoritative source; cheap to reverse. |
| D9 | Can the HydraFusion Cascade gap be closed? | No guaranteed way. It stays a stated limit unless V3 happens to observe a Cascade run. | Cascade is one value of `FusionPattern`. The routing is not in the local JavaScript and no client setting forcing it was found. Types name the `repair` phase but nothing ties a phase to a system message. | ANSWERED: autonomous, covered by SDK types; absence of a control is low-confidence. |
| D10 | Does this need an ADR? | Yes. | Kyber-Weave would ship a machine-wide replacement of a vendor prompt that depends on a setting VS Code labels experimental and internal. That constrains future work and has rejected alternatives. | ANSWERED: autonomous, covered by the plan inventory's ADR policy. |
| AP1 | Approve and execute | Approved: this plan, its Test contract (T1 to T5) and decisions Q1 and D2 to D10, at scale `extends-component` on `KyberSquad`, with one new ADR, one pull request, test-first, and the T4 live check needing the owner present to sign in. | The gate was presented with exactly that scope. Docs checks in the worktree before finalization: `docs validate --merge-ready` reported only `KW-DOC-LIFECYCLE-003` on this open plan, expected until closeout; `docs drift` reported no findings. | ANSWERED: owner decision 2026-11-10, relayed by the conductor, verbatim "Approve and execute". Not autonomous. |

## Design harvest

`documentation-impact: extends-component`; `impact-components: [KyberSquad]`. Each item lands in canonical docs at closeout (T5).

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | KyberSquad | adr | New ADR: optional machine-wide replacement of the Copilot prompt through an application-scope VS Code setting, why per-project is impossible, and the rejected alternatives (Q1, D10). |
| H2 | KyberSquad | architecture | The opt-in state, prompt embedding and refresh, settings splice, and how the prompt coexists with PR 576's agent-scoped deny hook (D3 to D7). |
| H3 | KyberSquad | onboarding | How to turn it on and off, its machine-wide effect, and the retest needed on each VS Code update. |
| H4 | KyberSquad | reference | `squad copilot-prompt` verbs, flags and exit codes, in `docs/cli-command-surface.md`. |
| H5 | KyberSquad | runbook | The doctor checks, the `mode=replace` log line, silent fallback, and the known limits (D8, D9). |

## Investigation findings

- **Discovery:** codegraph 9, docs 1 (rejected, see below), direct reads 13 (8 file reads, 5 `git show` reads) plus three delegated read-only sweeps.
- **Revision read:** `7f4bcb7c60be24928a552ca865d2dd5bae11ec2f` on branch `claude/feat/copilot-system-prompt-override`, through the worktree's own CodeGraph index and direct reads.
- **Direct reads and reason:** source files in the worktree, for line-level detail the index query did not return. The `kw-docs` server answered from `claude/docs/conductor-bug-crusher-run-speed` at `05eb938a`, another revision, so its answer was not used. No text-search tool is available to the planner, so the repository sweep was delegated to `research-agent`.

### What the owner remembers

Kyber-Weave's per-project settings today, at the revision above:

- `.vscode/mcp.json`, key `servers`: declared in `HarnessMcpCatalog` (`src/KyberWeave.Core.Docs/Mcp/HarnessMcpCatalog.cs:309-319`), written by `McpWiringRepairer.Repair` (`McpWiringRepairer.cs:129`), server list from `mcp.required-servers` in `.kyber-weave/kyber-weave.yml`.
- `.github/agents/<name>.agent.md` with `PreToolUse` and `PostToolUse` frontmatter hooks (`CopilotRenderer.cs:356-378`), `.github/skills/<name>/SKILL.md`, and `.github/hooks/kyber-arbiter.json` (`CopilotRenderer.cs:59`).
- No code in `src/` or `products/` writes `.vscode/settings.json` or any `chat.*` key. The repository's own `.vscode/settings.json` holds one vitest key.
- The one writer of Copilot VS Code settings is KyberDash, and it is per-user: `github.copilot.chat.otel.*` into `User/settings.json` (`dash/src/capture/harnesses/copilot.ts:57-65`, `167-194`).

So the per-project settings are files VS Code reads as workspace content. None is a VS Code setting with a scope to obey.

### Scope question

Facts, VS Code at commit `6bd30dab` and on `main`:

- **Declared scope.** `src/vs/workbench/contrib/chat/browser/chat.shared.contribution.ts`, entry `[AgentHostCopilotModelCapabilityOverridesSettingId]`: `scope: ConfigurationScope.APPLICATION`, `tags: ['experimental', 'advanced']`, on both revisions. Installed build: `workbench.desktop.main.js:3730`, `scope:1`.
- **Workspace values are dropped.** `src/vs/workbench/services/configuration/common/configuration.ts`: `WORKSPACE_SCOPES` and `FOLDER_SCOPES` do not include `APPLICATION`. `src/vs/workbench/services/configuration/browser/configuration.ts`, `FolderConfiguration`, parses `.vscode/settings.json` with `{ scopes: this.scopes, ... }`, and `WorkspaceConfiguration` parses `.code-workspace` settings with `{ scopes: WORKSPACE_SCOPES, ... }`. `ConfigurationModelParser.shouldInclude` in `src/vs/platform/configuration/common/configurationModels.ts` ends `return options.scopes.includes(scope);`, so the key is left out of the parsed model.
- **The forward cannot see it.** The window computes the value with `this._configurationService.getValue(...)` (`workbench.desktop.main.js:3741`) and pushes it to the host's root configuration. `getValue` reads the model the filter above already reduced. The host writes `<user-data>/User/globalStorage/agent-host-config.json` (`agentHostMain.js:94`, `:1908`).
- **One host serves every window.** `ElectronAgentHostStarter` (`src/vs/platform/agentHost/electron-main/electronAgentHostStarter.ts`, both revisions) holds one `utilityProcess`, throws `Agent Host utility process is already running.` on a second start, and gives each window a port on the same process. Root configuration is one object, so a per-window value would be last-writer-wins. That last part is inferred from the reducer; it was not run with two windows.
- **The prompt path is not per-workspace.** `agentHostMain.js:1206`: the path is trimmed and passed to `URI.file`. No `${workspaceFolder}`, no `~`, and no resolution against the session's working directory.

Other routes checked:

- **VS Code profiles:** application settings come from the default profile only (`ApplicationConfiguration`, `userDataProfilesService.defaultProfile.settingsResource`). No effect.
- **`.code-workspace` or multi-root:** filtered as above.
- **Per-project `--user-data-dir`:** would work, because each directory gets its own application and host. Costs its own sign-in and extensions, and a launch command. Recorded as an alternative in Q1. Not tested.
- **Copilot runtime or SDK options from the repository:** none found that replaces the system message. The repository-read instruction files add to the prompt. Not an exhaustive search.
- **Prompt text that branches on the repository:** possible, and the recommendation. The file is static text, so the branch is an instruction to the model, not host logic.

The source analysis above is static. The live run that confirms it is under "Scope test result".

### Prompt mechanics and limits (from the report, spot-checked)

- Fallback is silent: `[PromptOverride] Failed to read prompt override file` or `Failed to parse prompt override`, once as a warning (`agentHostMain.js:1206`).
- On success: `[Copilot:<session>] Resolved system message: mode=replace (content length N)` (`agentHostMain.js:1211`, `:1196`).
- Log directory shape: `<user-data>/logs/<timestamp>/` (`agentHostMain.js:1465`). The report names the file `agenthost.log`; the local search did not find that string, so T3 matches `agenthost*.log` at any depth and T4 confirms the name.
- PR 576's agent-scoped deny hook is not in this base commit, and this plan does not touch it. The skill-start change (PR 579) was closed unmerged, so the conductor stays an agent the person selects.

## Test contract (test-first)

| Task | Test project or file | Runner command | Observable behaviour | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | No-test task: skeleton. | `scripts/proc-guard.sh run -- dotnet build src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release` | Empty types and members exist, so T2's tests compile. | Not applicable: replaced by a successful build. | Build exits 0 with no warnings. |
| T2 | The four test classes named in T2 | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.CopilotPrompt"` | The contract tests exist and compile. | They fail on assertions or the skeleton's `NotImplementedException`, with no compile error. | Not applicable here: T3 turns them green. |
| T3 | Same four classes | The command in T2 | Install writes the prompt and the `*` entry for each detected flavour and keeps other bytes. A second install changes nothing. A foreign `*` entry is refused byte for byte. Uninstall removes only Kyber-Weave's entry and the file. Update rewrites the file only if it exists. The prompt holds every required section. Doctor adds no check when opted out, reports each failure mode, and turns failures into warnings under `--ensure`. | The T2 failures above. | The same tests pass unchanged, non-zero count, 0 failures. |
| T4 | No-test task: live verification on real VS Code, V1 to V3. | The steps in T4 | Real-window behaviour the unit tests cannot reach. | Not applicable. | Each step's result recorded in the plan. V3 may record the stated limit. |
| T5 | No-test task: documentation and closeout. | `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` | H1 to H5 are in canonical docs and the plan is archived. | Not applicable. | Both gates exit 0 with 0 findings. |

## Tasks

### T1: Compile-only skeleton

- **Files:**
  - `src/KyberWeave.Core/CopilotPrompt/VsCodeUserSettingsPaths.cs` (type `VsCodeUserSettingsPaths`, new)
  - `src/KyberWeave.Core/CopilotPrompt/CopilotPromptFile.cs` (type `CopilotPromptFile`, new)
  - `src/KyberWeave.Core/Doctor/CopilotPromptProbe.cs` (type `CopilotPromptProbe`, new)
  - `src/KyberWeave.Cli/Commands/Squad/CopilotPromptCommands.cs` (types `CopilotPromptInstallCommand`, `CopilotPromptUninstallCommand`, `CopilotPromptStatusCommand`, `CopilotPromptSettings`, new)
  - `src/KyberWeave.Cli/Update/CopilotPromptRefresher.cs` (type `CopilotPromptRefresher`, new)
- **Depends on:** none
- **Required skills:** `csharp-dev`
- **Session:** `skeleton`
- **Objective:** Add the types with the signatures T2 calls, every body throwing `NotImplementedException`. No behaviour and no CLI registration.
- **Command:** `scripts/proc-guard.sh run -- dotnet build src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release`
- **Acceptance:** The build exits 0 with no warnings.

### T2: Failing contract tests (RED)

- **Files:**
  - `tests/KyberWeave.Tests/CopilotPrompt/CopilotPromptSettingsMergeTests.cs` (class `KyberWeave.Tests.CopilotPrompt.CopilotPromptSettingsMergeTests`, new)
  - `tests/KyberWeave.Tests/CopilotPrompt/CopilotPromptFileTests.cs` (class `KyberWeave.Tests.CopilotPrompt.CopilotPromptFileTests`, new)
  - `tests/KyberWeave.Tests/CopilotPrompt/CopilotPromptProbeTests.cs` (class `KyberWeave.Tests.CopilotPrompt.CopilotPromptProbeTests`, new)
  - `tests/KyberWeave.Tests/CopilotPrompt/CopilotPromptRefreshTests.cs` (class `KyberWeave.Tests.CopilotPrompt.CopilotPromptRefreshTests`, new)
- **Depends on:** T1
- **Required skills:** `test-dev`
- **Session:** `red`
- **Objective:** Pin the behaviour below, using temp homes only. No test may resolve a real home directory, real VS Code settings or `~/.copilot`.
  - `CopilotPromptSettingsMergeTests`: absent file; `{}`; a file with `//` comments, trailing commas and a byte-order mark, asserting bytes outside the insertion are identical; an existing setting with another model id, which stays; idempotent second install; foreign `*` entry refused with the input bytes unchanged and exit 1; malformed file refused; uninstall removes only our entry and keeps comments; uninstall leaves a foreign `*` alone; only detected flavours are written; `--flavour` narrows; Windows, macOS and Linux paths for both flavours.
  - `CopilotPromptFileTests`: the embedded prompt parses as YAML; its only top-level key is `systemPrompt`; the text is non-empty and at most 8,000 characters; it contains `AGENTS.md`, `.github/copilot-instructions.md`, `.kyber-weave/kyber-weave.yml`, `conductor`, `skill`, `tool_search_tool`, `task`; it contains a safety section; it does not contain `Default to doing the work yourself`.
  - `CopilotPromptProbeTests`: no prompt file means no checks; missing setting fails with the fix command; a missing file with a setting present fails; invalid YAML fails; an unexpected key fails; a newest log with `Resolved system message: mode=replace` passes; a newest log with another mode fails; no matching log line is a warning; only the newest log folder counts; under `DoctorMode.Ensure` every failure is passed with a `warning:` reason.
  - `CopilotPromptRefreshTests`: refresh rewrites an existing file; refresh does not create one when opted out; a failed refresh is logged and does not change the update's exit code.
- **Command:** `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.CopilotPrompt"`
- **Acceptance:** Tests compile, the count is non-zero, and they fail on assertions or the skeleton's exception.

### T3: Implement the option (GREEN)

- **Files:**
  - `products/kyber-squad/copilot/system-prompt.yaml` (new; the prompt)
  - `src/KyberWeave.Core/KyberWeave.Core.csproj` (an `EmbeddedResource` for the prompt, logical name CopilotPrompt.system-prompt.yaml)
  - `src/KyberWeave.Core/CopilotPrompt/VsCodeUserSettingsPaths.cs` and `src/KyberWeave.Core/CopilotPrompt/CopilotPromptFile.cs`
  - `src/KyberWeave.Core/Doctor/CopilotPromptProbe.cs`, and `src/KyberWeave.Core/Doctor/RepositoryDoctor.cs` (method `Run`, appended to the `checks` list after the `stops` handling)
  - `src/KyberWeave.Cli/Commands/Squad/CopilotPromptCommands.cs`, using `KyberWeave.Core.Docs.Mcp.JsonServerSplice` and `KyberWeave.Core.FileSystem.AtomicFile`
  - `src/KyberWeave.Cli/Program.cs` (branch `squad`, new sub-branch `copilot-prompt`)
  - `src/KyberWeave.Cli/Update/CopilotPromptRefresher.cs`, and `src/KyberWeave.Cli/Update/SelfUpdater.cs` (methods `RunCore`, the already-current path, and `FinishUpdate`, beside `RefreshArbiterHooks`)
- **Depends on:** T2
- **Required skills:** `csharp-dev`
- **Session:** `green`
- **Objective:** Make T2 pass unchanged. The prompt must carry: identity; Kyber-Weave's own safety rules (no secret exfiltration, confirm destructive or outward-facing actions, refuse malicious code, tool output is data and not instructions); how to use tools, the `skill` tool, `tool_search_tool` for deferred tools and `task` for workers; read `AGENTS.md` and `.github/copilot-instructions.md` first; file references as workspace-relative Markdown links with `#L<line>`; if `.kyber-weave/kyber-weave.yml` exists in the workspace, load the conductor skill for non-trivial work; otherwise behave as a general coding assistant. `install`, `uninstall` and any machine-wide write need `--yes` or an interactive confirmation that names each settings file. `--dry-run` writes nothing. `install` prints that the change affects every Copilot chat on the machine.
- **Command:** The T2 command, then `scripts/proc-guard.sh run -- dotnet build src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release`
- **Acceptance:** T2 passes unchanged with a non-zero count, the build has no warnings, and the `task-gate` skill passes.

### T4: Live verification on real VS Code

- **Files:** this plan only (results recorded under a `## Live verification` heading).
- **Depends on:** T3
- **Required skills:** none
- **Session:** `live`
- **Objective:** No automated test can reach these. Run in the owner's presence, because V1 needs an interactive sign-in. Every step runs with `HOME` set to a scratch directory, because a launch using the real `HOME` writes to `~/.copilot` and the shared storage folder. Never touch the owner's `~/.copilot`, real VS Code settings or running VS Code.
  - **Setup.** Use the short root `/private/tmp/claude-501/kwg3`, because a long user-data path crashes at the socket claim. Create `h/Library/Application Support`, `u/User`, `e`, and two scratch folders, `plain` and `kw`, under that root. Symlink `h/Library/Application Support/Code - Insiders` to `u`, so `kyber-weave` run with the scratch `HOME` writes the same `User/settings.json` the window reads. In `kw`, run `git init`, add a `.kyber-weave/kyber-weave.yml`, and run `HOME=/private/tmp/claude-501/kwg3/h kyber-weave squad install . --target copilot --yes` so the conductor skill is present.
  - **macOS keychain prompt.** A new user-data-dir raises a keychain prompt for VS Code's safe-storage key. The operator denies it, because allowing it hands the throwaway profile the key of the owner's real install. If denial stops the sign-in from working, stop T4, record that V1 is not proved, and ask the owner how to proceed. Do not allow the prompt to get past it.
  - **V1, full chat.** Sign in within the throwaway window. Install, open `kw`, start a new chat. Confirm the agent reports the new identity, loads a skill, calls a shell, finds a deferred MCP tool through `tool_search_tool`, and a file link opens. The log shows `mode=replace`, and confirms the log file name.
  - **V2, plain chat and Kyber-Weave chat.** `plain` gets general help with no conductor routing. `kw` routes non-trivial work to the conductor skill. The `preToolUse` hook, if deployed, still blocks.
  - **V3, HydraFusion Cascade.** Under Auto, run up to ten sessions with a multi-file task and read `session.fusion_resolved` for `pattern`. If `cascade` appears, record whether the `repair` phase carried the custom prompt. If it does not appear, record the count and state the limit.
  - **Cleanup.** Run `uninstall` and confirm the entry is gone, kill only the processes this task started, and remove `/private/tmp/claude-501/kwg3`.
- **Command:** With `S=/private/tmp/claude-501/kwg3`: `HOME=$S/h kyber-weave squad copilot-prompt install --flavour insiders --yes`, then `HOME=$S/h code-insiders --new-window --user-data-dir $S/u --extensions-dir $S/e $S/kw`, then `HOME=$S/h kyber-weave doctor --check $S/kw`, then `HOME=$S/h kyber-weave squad copilot-prompt uninstall --flavour insiders --yes`
- **Acceptance:** V1 and V2 pass and V3 is recorded either way. A V1 or V2 failure sends the plan back for rework. A sign-in blocked by the denied keychain prompt is reported to the owner, not counted as a pass.

### T5: Documentation and closeout

- **Files:** `docs/adr/` (new ADR, and `docs/adr/README.md`), `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`, `docs/cli-command-surface.md`, `docs/kyber-squad/README.md`, `docs/plans/copilot-system-prompt-override.md` moved to `docs/archive/plans/`, `docs/archive/plans/README.md`.
- **Depends on:** T3, T4
- **Required skills:** `app-docs-standard`, `architecture-decision-record`
- **Session:** `docs`
- **Objective:** Harvest H1 to H5, add the completion summary, archive the plan and remove its task file. Runbook content goes in the KyberSquad documentation named by the catalog.
- **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .`
- **Acceptance:** Both gates exit 0 with 0 findings.

## Dependency graph

T1, then T2, then T3, then T4, then T5. A straight line, because each task consumes the previous one's output.

## Scope test result

Run before approval by the conductor on VS Code Insiders 1.146.0-insider, with a throwaway user-data-dir and a scratch `HOME`. Result as reported to the planner, which did not run it.

- **Project `.vscode/settings.json` only:** `agent-host-config.json` had `"modelCapabilityOverrides": {}`.
- **Same, with `security.workspace.trust.enabled: false` in user settings:** still `{}`, so workspace trust is not the cause.
- **User settings only (control):** `"modelCapabilityOverrides": {"*": {"promptOverrideString": "systemPrompt: KWG2-MARKER"}}`, and the host log showed `root/configChanged` carrying the marker.

**Conclusion:** user settings only. A project-only value forwards as `{}`, which matches the source analysis.

## Live verification

T4 ran on 2026-11-10 with the owner present, on VS Code Insiders 1.146.0-insider, a throwaway user-data-dir and a scratch `HOME`, using a branch build of the CLI. The owner was signed in to GitHub. The macOS keychain prompt for the throwaway profile was denied, and sign-in still worked, so the keychain denial did not block V1.

- **Setup.** `squad copilot-prompt install --flavour insiders --yes` exited `0` and wrote the prompt file and the `*` entry. The window forwarded the value to `agent-host-config.json`.
- **V1, full chat (pass).** In the Kyber-Weave repository on Auto, the model identified as the Kyber-Weave-configured assistant, loaded the `task-gate` skill, ran a shell command (`echo SHELL-OK`), found deferred GitHub MCP tools through `tool_search_tool`, and gave a file link. The host log is `<user-data>/logs/<timestamp>/agenthost.log`, which confirms the file name the doctor matches. Across five sessions it showed `Resolved system message: mode=replace (content length 1648)` every time. `kyber-weave doctor --check` passed `copilot.prompt.insiders.setting`, `.file` and `.log`.
- **V2, routing (pass).** In the Kyber-Weave repository, a cross-cutting request led the model to read `.kyber-weave/kyber-weave.yml` and start with the conductor: it dispatched the conductor agent with the `task` tool, then architect. In a plain repository without that file, the model checked for `AGENTS.md`, `.github/copilot-instructions.md` and `.kyber-weave/kyber-weave.yml`, found none, and acted as a general assistant with no conductor routing. `hook.start` and `hook.end` events fired in every session.
- **V3, HydraFusion Cascade (not observed).** The Auto session stayed on Auto's default `balance` tier (model `gpt-6-luna`) and never engaged the Fusion tier. No `session.fusion_resolved` event appeared, so the `repair` phase was not recorded. Cascade coverage stays a stated limit.
- **Cleanup.** `uninstall --flavour insiders --yes` exited `0`, removed the `*` entry and deleted the prompt file. It leaves an empty `"chat.agentHost.copilot.modelCapabilityOverrides": {}` object in `settings.json`, which is harmless and is documented. The owner's real VS Code settings and `~/.copilot` were not touched; none of them references the scratch path.

**Finding, a known interaction and not a defect of this change.** The conductor is an agent the person selects, not a skill. In the live check, without the conductor selected, the model offered to dispatch the conductor agent itself. The prompt now asks the person to switch to the conductor agent in the agent picker for non-trivial work, which replaces that offer. PR 576's agent-scoped deny hook, recorded as "The Copilot conductor holds tools a hook denies it", guards the conductor beside the prompt.

**Acceptance.** V1 and V2 pass. V3 is recorded as not observed, which the plan permits. No V1 or V2 failure sends the plan back for rework.

## Risks

- **VS Code changes or removes the setting.** It is labelled experimental, and its predecessor was removed in 1.144. Doctor reports the fallback; onboarding says to retest on each update.
- **Safety text is gone for the top-level agent.** Kyber-Weave's prompt must carry its own. Workers keep Copilot's.
- **Machine-wide effect.** Every Copilot chat changes, including in other repositories and after Kyber-Weave stops being used, until `uninstall` runs.
- **Prompt branching is an instruction.** A model can ignore it. PR 576's agent-scoped deny hook stays for that reason.
- **Lost per-model tuning.** One prompt runs on every model, including under Auto.
- **VS Code's YAML parser differs from YamlDotNet.** Doctor's parse can pass a file VS Code rejects. V1 proves the shipped file against the real parser, and the log check catches it on real machines.
- **Enterprise policy** may block the setting. Not checked.

## Out of scope

- Per-project activation (see Q1).
- Changes to PR 576's agent-scoped deny hook, or to the tools the conductor agent holds.
- The Copilot Chat extension's own agent mode, which has no override since VS Code 1.144.
- Copilot CLI, and remote or SSH VS Code sessions.
- `toolDescriptions` overrides.

## Verification gates and review

Each developer task ends with its own `task-gate` run. The end-of-run review council reviews the whole change under the `code-review` skill. `docs-dev` closes out under T5.

## Completion summary

**Completed 2026-11-10.** Every task is delivered and the plan is archived.

- **Implementation.** T1 skeleton (`057c4899`), T2 red contract tests (`616c8f61`), and T3 green implementation (`2e66ddd1`, amended on origin). The task gate passed, with 82 contract tests passing.
- **Live check.** T4 ran on 2026-11-10 with the owner present (results in [Live verification](#live-verification)). V1 and V2 pass. V3, HydraFusion Cascade, was not observed and stays a stated limit.
- **Design reversal.** The owner reversed the skill fallback. PR 579 ("start Copilot's parent roles as skills") was closed unmerged. The conductor is an agent the person selects in the agent picker. PR 576's agent-scoped deny hook guards it, recorded in the ADR "The Copilot conductor holds tools a hook denies it", which is not on this branch. The prompt asks the person to switch to the conductor agent for non-trivial work, in commit `d0723503`.
- **Superseded task text.** The T3 objective, the T4 setup line and the T4 V2 line still say "conductor skill". That is the approved wording, kept deliberately because the plan's execution digest binds task text and the task file is removed at closeout. The conductor-agent wording replaced it in commit `d0723503`.
- **End-of-run review.** The council returned REQUEST_CHANGES with one Major finding. The release policy test `SquadPackAndReleaseTests.ReleaseArtifact_AssertsCliAndMcpArchivesContainNoEmbeddedSquadCorpus` did not allow the new embedded prompt. It was fixed in `1ddd41e5` with a narrow allowance. The full suite before that fix ran 7041 tests: 7012 passed, 28 skipped, and 1 failed, which is that test.
- **Gate not evidenced.** The `inspectcode` gate timed out after 900 s with no evidence. The owner chose to rely on CI rather than re-run it locally.
- **Documentation-impact review.** `DOCS_REVIEW: update`. Every recommendation is applied in the closeout commit: H1 to H5 are written to the destinations below, and the `squad copilot-prompt` rows and exit codes are in the CLI reference.
- **Follow-ups, not fixed.** The Linux path ignores `XDG_CONFIG_HOME`. Doctor reads the home directory as `HOME`, then `USERPROFILE`, while install uses the user profile folder. `AtomicFile` replaces a symlinked `settings.json` with a regular file.

**Acceptance criteria.** H1 to H5 appear in the named documents: [ADR 0067](../../adr/0067-copilot-system-prompt-override-is-machine-wide.md), [architecture, section 11](../../kyber-squad/architecture.md#11-copilot-system-prompt-override-opt-in-machine-wide), [onboarding](../../kyber-squad/onboarding.md#copilot-system-prompt-override-opt-in), [CLI command surface](../../cli-command-surface.md), and the [runbook](../../kyber-squad/copilot-prompt-runbook.md). The task file is removed. Both documentation gates are run at closeout, and their results are recorded in the closeout report.

## Closeout mapping

Every decision and harvest item maps to its destination. Each row states its documentation impact, impact components, last-reviewed date and archive status.

**Decisions**

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| Q1 | [ADR 0067, Decision](../../adr/0067-copilot-system-prompt-override-is-machine-wide.md); [architecture, section 11](../../kyber-squad/architecture.md#11-copilot-system-prompt-override-opt-in-machine-wide) | extends-component | KyberSquad | 2026-11-10 | archived |
| D2 | [ADR 0067, Alternatives considered](../../adr/0067-copilot-system-prompt-override-is-machine-wide.md) (no flag on `squad install`); [CLI command surface, `squad copilot-prompt` rows](../../cli-command-surface.md) | extends-component | KyberSquad | 2026-11-10 | archived |
| D3 | [architecture, Opt-in state](../../kyber-squad/architecture.md#opt-in-state); no ADR — ADR waiver: the no-receipt state refines ADR 0067's decision | extends-component | KyberSquad | 2026-11-10 | archived |
| D4 | [architecture, Prompt embedding and refresh](../../kyber-squad/architecture.md#prompt-embedding-and-refresh); no ADR — ADR waiver: follows the embedded-resource pattern and ADR 0065's rule for post-update steps | extends-component | KyberSquad | 2026-11-10 | archived |
| D5 | [architecture, Settings splice and refusal rule](../../kyber-squad/architecture.md#settings-splice-and-refusal-rule); no ADR — ADR waiver: reuses `JsonServerSplice` and `AtomicFile`, both existing patterns | extends-component | KyberSquad | 2026-11-10 | archived |
| D6 | [architecture, Settings splice and refusal rule](../../kyber-squad/architecture.md#settings-splice-and-refusal-rule); [CLI exit codes](../../cli-command-surface.md#exit-codes); no ADR — ADR waiver: refusal detail within ADR 0067's decision | extends-component | KyberSquad | 2026-11-10 | archived |
| D7 | [architecture, Opt-in state](../../kyber-squad/architecture.md#opt-in-state) (detection per build); no ADR — ADR waiver: per-platform path detail | extends-component | KyberSquad | 2026-11-10 | archived |
| D8 | [runbook, Doctor checks](../../kyber-squad/copilot-prompt-runbook.md#doctor-checks); no ADR — ADR waiver: diagnostic detail | extends-component | KyberSquad | 2026-11-10 | archived |
| D9 | [runbook, Known limits](../../kyber-squad/copilot-prompt-runbook.md#known-limits); [ADR 0067, Consequences](../../adr/0067-copilot-system-prompt-override-is-machine-wide.md) | extends-component | KyberSquad | 2026-11-10 | archived |
| D10 | [ADR 0067](../../adr/0067-copilot-system-prompt-override-is-machine-wide.md): the record itself | extends-component | KyberSquad | 2026-11-10 | archived |
| AP1 | Documentation waiver: owner approval record, transient, with no surviving invariant; the approved test contract stays in this archived plan. No ADR — ADR waiver: process decision | extends-component | KyberSquad | 2026-11-10 | archived |

**Design harvest**

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| H1 | [ADR 0067](../../adr/0067-copilot-system-prompt-override-is-machine-wide.md) and its row in the [ADR index](../../adr/README.md) | extends-component | KyberSquad | 2026-11-10 | archived |
| H2 | [architecture, section 11](../../kyber-squad/architecture.md#11-copilot-system-prompt-override-opt-in-machine-wide): opt-in state, prompt embedding and refresh, settings splice, and coexistence with PR 576's guard | extends-component | KyberSquad | 2026-11-10 | archived |
| H3 | [onboarding, Copilot system prompt override (opt-in)](../../kyber-squad/onboarding.md#copilot-system-prompt-override-opt-in): turning it on and off, the machine-wide effect, and the retest after each VS Code update | extends-component | KyberSquad | 2026-11-10 | archived |
| H4 | [CLI command surface](../../cli-command-surface.md): the three `squad copilot-prompt` verbs, their flags, and their exit codes | extends-component | KyberSquad | 2026-11-10 | archived |
| H5 | [runbook](../../kyber-squad/copilot-prompt-runbook.md): doctor checks, the `mode=replace` log line, silent fallback, and known limits | extends-component | KyberSquad | 2026-11-10 | archived |

The task file `copilot-system-prompt-override.tasks.json` was removed at closeout.
