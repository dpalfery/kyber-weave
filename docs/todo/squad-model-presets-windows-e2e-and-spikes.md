---
id: todo/squad-model-presets-windows-e2e-and-spikes
title: Windows E2E Verification and Harness Spikes for Squad Model Presets
doc-type: todo
component: KyberSquad
status: draft
owner: dpalfery
last-reviewed: 2026-11-26
---

# Windows E2E Verification and Harness Spikes for Squad Model Presets

## The finding

During delivery and closeout of the `squad-model-presets` specification, implementation slices 1
and 2 (Tasks 1–17) and the macOS end-to-end verification (E-mac, Task 18) were delivered and verified
clean. However, full Windows live end-to-end execution (`E-win`) and harness spikes `S1–S8` were
deferred by the owner (2026-10-25) pending execution on hosts with live harness installations and
signed-in provider credentials.

Until these spikes and platform checks run, several catalog entries in `SquadInstanceCatalog` remain
marked as `SpikePending` (which prevents `squad run` dispatch by failing closed with exit code 2),
and the Windows non-admin link guarantees have been verified by unit and contract tests rather than
a live harness CLI invocation.

## Deferred verification items

### 1. Windows End-to-End Scenario (`E-win`)

Task 18 specifies the live verification scenario on a Windows machine logged in as a standard user
(with Developer Mode disabled):

1. **Test suite**: Run `SquadLinkFileSystemTests` (including the 3 Windows-specific hard link and
   junction cases skipped on Unix), `SquadInstanceLinkSyncTests`, and `SquadInstanceServiceTests`.
2. **Concurrent presets**:
   - Two separate git workspaces select different presets for `claude` (`squad preset use <preset>`).
   - Run `kyber-weave squad run claude` concurrently in both workspaces.
   - Confirm each session reports its expected model token.
   - Confirm `git status` remains completely clean in both repositories.
3. **Shared settings and links**:
   - Modify a shared setting in one session; verify it reflects across the link in the other.
   - Verify non-admin creation of hard links for files and junctions for directories.
4. **npm `.cmd` shims**:
   - Launch an npm-installed harness CLI (`pi` or `copilot`) located via `PATHEXT` running under
     `cmd.exe` to verify argument escaping and console inheritance.
5. **Clean removal**:
   - Run `kyber-weave squad instance remove --target claude --preset <preset> --yes`.
   - Confirm file IDs on the primary home directory (`fsutil file queryfileid`) remain unchanged.

### 2. Platform Harness Spikes (`S1–S8`)

Each spike tests whether a candidate environment variable safely redirects agent configuration
without breaking sign-in credentials or altering unrelated system tools.

- **S1. OpenCode override (`OPENCODE_CONFIG_DIR`)**:
  - Deploy a probe agent into the global configuration directory and a different probe into the
    instance layer folder.
  - Run OpenCode with `OPENCODE_CONFIG_DIR` pointing to the layer.
  - If the layered agent overrides the global definition, mark row as `Ready`. If not, test whether
    the `XDG_CONFIG_HOME` fallback works or mark as `Unsupported`.

#### OpenCode instance support: audit evidence (2026-11-26)

Recorded against `opencode v2.4.1` (`opencode --version`) after `kyber-weave doctor --ensure .` passed in the task worktree. No file under `src/` or `tests/` was changed.

##### A1 — plugin API

Verdict: correct as designed

`ArbiterPluginShim.Render` for the `opencode` token emits `OpenCodeDefaultExport`, which is a default export of `{ id, setup }`:

```text
export default {
  id: "kyber-arbiter",
  setup: KyberArbiter,
};
```

`OpenCodePluginBody` assumes `setup` registers both hooks through `ctx.tool.hook` (`execute.before` and `execute.after`), that a block decision is a thrown `Error`, and that `event.input` is writable (`event.input = decision.args`). `RunArbiterHook` assumes a spawn or a non-zero hook exit is also a thrown `Error`. `tests/KyberWeave.Application.Tests/Squad/ArbiterOpenCodeRenderingTests.cs` pins that shape (`Render_Opencode_EmitsDefaultExportObjectWithSetup`, `RenderAsync_Shim_RegistersBothHooksThroughThePluginContext`, `RenderAsync_Shim_AssignsDecisionArgsToTheWritableEventInput`).

OpenCode 2.4.1 matches it. The Homebrew binary is `/opt/homebrew/Cellar/opencode/2.4.1/bin/opencode`. Its plugin loader accepts a default export of `{ id, setup }` or `{ id, effect }` and refuses anything else with `Plugin must export a default definition with an id and an effect or setup function.` `setup` is called with a context whose `tool.hook` wraps `execute.before` and `execute.after`. The before wrapper copies the callback's `input` back (`n.input=t.input`). A thrown `Error` is wrapped as `Tool.Error` (`a instanceof Error ? a.message : String(a)`), which is the rejection the tool runner treats as a failed call. The same hook names are in the v2.4.1 changelog (`execute.before` and `execute.after`; reject with `Tool.Error`). The installed user plugin `~/.config/opencode/plugins/kyber-arbiter.ts` (2026-11-22) ends with `id: "kyber-arbiter"` and `setup: KyberArbiter`.

##### A2 — scope contract

Verdict: correct as designed

Project scope and global scope both render the owned plugin file. `OpenCodeRenderer.RenderArbiter` always calls `AddArbiterPluginShim(".opencode/plugins/kyber-arbiter.ts", "plugins/kyber-arbiter.ts")`. `AddArbiterPluginShim` writes the project path at project scope and the global path when `Scope` is global, whenever `EnabledArbiter` is set or `DispatchHooks` is true. `DispatchHooks` is true only at project scope when the request carries an Arbiter wiring, including a disabled one (`Enabled: false`), so a disabled wiring still renders the dispatch-hook shim at project scope. `ArbiterEnforced` is the MCP grant, and it is true only for an enabled wiring at project scope; it does not decide whether the file is written.

[ADR 0026](../adr/0026-kyber-utilities-owned-files-not-settings.md) (amendment 2026-11-05) allows Squad to write its owned file in an auto-loaded plugin directory, including OpenCode `~/.config/opencode/plugins/`, when the user runs the install. [ADR 0054](../adr/0054-squad-owned-blocks-in-shared-hook-files.md) decision 4 writes a v3 receipt at project scope and at global scope. The Arbiter hooks passage in [onboarding](../kyber-squad/onboarding.md) hooks `opencode` in user/global scope and says the hook on `pi`, `kilo`, and `opencode` is an owned extension or plugin file. `RenderAsync_GlobalScope_OpenCode_EmitsOwnedAutoLoadedPluginFileUnderGlobalRoot` in `tests/KyberWeave.Application.Tests/Squad/ArbiterGlobalPluginDirRenderingTests.cs` pins the global file at `plugins/kyber-arbiter.ts` under `~/.config/opencode`. The phrase "project scope only" describes neither scope.

##### A3 — instance render

Verdict: gap with a decision needed

`SquadLifecycleService.InstallAfterRecoveryAsync` maps instance scope onto a global render against the instance root: when `request.Scope` is `Instance`, `renderScope` is `Global` and the global-root resolver is a `SquadInstanceRootResolver` bound to that root. `SquadInstanceService.EnsureAsync` builds `SquadInstallRequest` with the instance root, `SquadDeploymentScope.Instance`, the one target, and the preset name. `Arbiter` is left at its default, `null` (`SquadInstallRequest` in `src/KyberWeave.Domain/Squad/Deployment/SquadDeploymentModels.cs`). With no wiring, `EnabledArbiter` is null and `DispatchHooks` is false, so `AddArbiterPluginShim` writes no shim.

Consequence: `squad run opencode` would launch a session with no Arbiter plugin in the instance, while a global install on this machine has already written `~/.config/opencode/plugins/kyber-arbiter.ts`. Whether that user-global plugin is discovered when the config root is the instance is the next spike; this record is only the omission. The bullet is in [the Arbiter hook todo](arbiter-global-hook-dispatch-coverage.md). Changing it would amend decision 4 of [ADR 0044](../adr/0044-squad-lock-v2-and-instance-deployment-scope.md) and [ADR 0054](../adr/0054-squad-owned-blocks-in-shared-hook-files.md). The renderer, the install request, and both ADRs were left as they are.

##### A4 — model profiles

Verdict: correct as designed

`opencode auth list` shows providers OpenCode Go, GitHub Copilot, Google, LM Studio, OpenCode, Wafer, and Z.AI Coding Plan. `opencode models` returned ids, including the three tokens copied from `products/kyber-squad/profiles/models.yml`:

- `zai-coding-plan/glm-5.3` — Verified present (`architect`, `deep-planning`, and `dev-hard`)
- `opencode-go/muse-spark-1.3-contributor` — Verified present (`fast` and `general`)
- `opencode-go/kimi-k2.7-code` — Verified present (`reviewer`)

`zai-coding-plan/glm-5.4` is in the live list and is not a bundled token. `models.yml` was not edited.

`kyber-weave` 0.1.8-rc.31 installed OpenCode Squad into a scratch directory outside the repository (`squad install <scratch> --target opencode --yes`, exit 0). That install rendered the bundled assignment itself. One agent file, `.opencode/agents/architect.md`, contained `model: zai-coding-plan/glm-5.3`.

`squad preset show architect --target opencode`, the dry-run `squad preset use architect <scratch> --target opencode --dry-run`, and the same `use` without `--dry-run` each exited 2:

```text
kyber-weave squad: error: Unknown preset 'architect'. Available presets: bench-arch-deepseek-v4-pro, bench-arch-glm-5-3, bench-arch-gpt-6-1-sol-copilot, bench-arch-gpt-6-1-sol-zen, bench-arch-kimi-k3-1-pro, bench-arch-muse-spark-1-3, kilo-opencode-go, kilo-override, kyber-weave, kyber-weave-factory.
```

There were no dry-run agent lines and no changed model line. `architect` is a profile category in `models.yml`. `squad preset show` and `squad preset use` load a user preset file under `~/.kyber-weave/squad/presets/`, and no file there is named `architect`. `squad preset clear <scratch> --target opencode` exited 0. The scratch directory was then deleted. Missing tokens are not a finding.

- **S2. Kilo override (`KILO_CONFIG_DIR`)**:
  - Repeat the S1 layered override test using `KILO_CONFIG_DIR` and `~/.config/kilo/agents/`.
- **S3. Cursor override (`CURSOR_CONFIG_DIR`)**:
  - Test the Cursor CLI with `CURSOR_CONFIG_DIR` pointing to an instance folder holding `agents/probe.md`.
  - Record the executable name. If agents and settings both move, row is `HomeReplace` and `Ready`.
  - If only agents move and sign-in stays global, row is `ConfigLayer` and `Ready`.
  - If no custom agent loads, mark `Unsupported`.
- **S4. ZCode storage variable (`ZCODE_STORAGE_DIR` vs `ZCODE_DATA_BASE_DIR`)**:
  - Apply the S3 check to `ZCODE_STORAGE_DIR`, and then to `ZCODE_DATA_BASE_DIR`.
  - **Discrepancy note**: `SquadGlobalRoots` currently resolves ZCode's global root using
    `ZCODE_STORAGE_DIR`, but ZCode's upstream documentation references `ZCODE_DATA_BASE_DIR`.
  - Determine which variable is respected by current releases. If `ZCODE_STORAGE_DIR` is ignored by
    the CLI, update `SquadGlobalRoots` and `SquadInstanceCatalog` accordingly.
- **S5. Copilot (`COPILOT_HOME`)**:
  - Verify whether setting `COPILOT_HOME` requires a fresh login or reads the system keychain (`copilot-cli`).
  - Run the rename check (`stat -f %i` or `fsutil file queryfileid`) across authentication to confirm
    credentials are not rewritten by atomic rename (which would split hard links). Any rewritten file
    must be added to the catalog row's `NeverLinked` roster.
- **S6. Claude (`CLAUDE_CONFIG_DIR`)**:
  - Verify whether `<temp>/.claude.json` is created when running under `CLAUDE_CONFIG_DIR`. If created,
    add an explicit link to `~/.claude.json`.
  - On Windows, run the rename check on `.credentials.json` across `/login` and token refresh.
- **S7. Codex (`CODEX_HOME`)**:
  - In file mode, perform the rename check on `auth.json` across token refreshes.
  - In keyring mode, confirm that independent `CODEX_HOME` paths share or partition credentials as documented.
- **S8. Pi on Windows (`PI_CODING_AGENT_DIR`)**:
  - Hard-link `auth.json` into a temporary `PI_CODING_AGENT_DIR` on Windows.
  - Run concurrent `pi` sessions across token refreshes to verify whether file locks on `auth.json`
    interfere with hard-linked access. If contention occurs, apply the R7.4a exception (never link
    `auth.json`, require one sign-in per instance).

## Next steps and resolution criteria

1. Run spikes S1, S2, S3, S4 on macOS and Windows to promote their `SquadInstanceCatalog` rows from
   `SpikePending` to `Ready` or `Unsupported`.
2. Settle the S4 variable discrepancy (`ZCODE_STORAGE_DIR` vs `ZCODE_DATA_BASE_DIR`) and align
   `SquadGlobalRoots` if necessary.
3. Complete E-win on a physical Windows test machine and document findings in the catalog.
