---
id: todo/squad-model-presets-windows-e2e-and-spikes
title: Windows E2E Verification and Harness Spikes for Squad Model Presets
doc-type: todo
component: KyberSquad
status: draft
owner: dpalfery
last-reviewed: 2026-10-25
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
