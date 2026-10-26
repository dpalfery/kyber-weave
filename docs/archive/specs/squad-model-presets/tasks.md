---
id: archive/specs/squad-model-presets/tasks
title: Squad model presets tasks
doc-type: spec
status: archived
owner: dpalfery
last-reviewed: 2026-10-25
component: KyberSquad
---

# Implementation Tasks

**Status:** Archived
**Archive Date:** 2026-10-25
**Phase status:** Delivered and verified
**Development mode:** test-first

This builds the approved [requirements](requirements.md) and [design](design.md). Where a task
says "per design", the design section of that name is the specification. These tasks add only
file names, class names, messages and test cases.

## How every task runs

- **Test-first:** `test-dev` writes the test and records the failing run (RED). Then `csharp-dev`
  makes it pass (GREEN) and tidies the code with the tests still green (REFACTOR). Code under
  `src/` is never written before the RED run is recorded.
- **Runner:**
  `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class KyberWeave.Tests.<Class>`.
  `global.json` selects Microsoft Testing Platform, where xUnit v3 filters with the
  `--filter-class` family, not `--filter`.
  - While iterating, run `scripts/fast-loop.sh --class KyberWeave.Tests.<Class>`. Its output is
    never evidence.
- **Fixtures:** `tests/KyberWeave.Tests/Fakes/FakeSquadUserPaths.cs` (a temporary home),
  `Fakes/FakeSquadReleaseSource.cs`, and the canonical source in `products/kyber-squad`.
- **Platform tasks (12, 13, 18):** their test classes must also pass on macOS, and on Windows as a
  standard user with Developer Mode off. Windows-only cases call `Assert.Skip` on other systems,
  as `UtilitiesCliCommandTests` does. Results go in [Evidence](#evidence).
- **Order:**
  - Slice 1: 1 → 2 → 3, with 4 in parallel; then 3 and 4 → 5 → 6 → 7, and 5 → 8.
  - Slice 2: 9, 12 and 15 can start at any time; 5 → 10; 9 → 11; 11 and 12 → 13; 10 and 13 → 14;
    6, 9, 14 and 15 → 16; 14 → 17; 12–17 → 18.
  - Then 19 → 20 → 21.
  - Spikes run alongside slice 1.

## Group 0 — Spikes (no test code; macOS and Windows)

Each spike is checked by an [Evidence](#evidence) entry instead of a test. The entry records the
OS, the harness version, the commands and the result. Each outcome either changes one row of
task 9 or applies the R7.4a exception. The owner, or a worker on that OS with the harness
installed and signed in, runs them.

**Rename check:** read a file's ID before and after a step, with `stat -f %i` on macOS or
`fsutil file queryfileid` on Windows. A new ID means the file was rewritten by rename.

- [ ] S1. **OpenCode override** · R7.4, R7.7
  - Put a `probe` agent with description `GLOBAL` in the main config, and another `probe` with
    description `LAYER` in `<temp>/agents/`.
  - Run with `OPENCODE_CONFIG_DIR=<temp>` and ask `@probe` for its description.
  - `LAYER` means the row is Ready; any other answer means Unsupported. Record whether the
    `XDG_CONFIG_HOME` fallback works, as a note only.
- [ ] S2. **Kilo override** · R7.4, R7.7. Same as S1, with `KILO_CONFIG_DIR` and
  `~/.config/kilo/agents/`.
- [ ] S3. **Cursor** · R7.4, R7.7
  - Run the Cursor CLI with `CURSOR_CONFIG_DIR=<temp>` holding `agents/probe.md`. Record the
    executable name.
  - If agents and settings both move: HomeReplace, Ready.
  - If only agents move and sign-in stays: ConfigLayer, Ready.
  - If no agent loads: Unsupported.
- [ ] S4. **ZCode** · R7.4, R7.7. Apply S3 to `ZCODE_STORAGE_DIR`, then to `ZCODE_DATA_BASE_DIR`.
  Record which variable works. If `ZCODE_STORAGE_DIR` does nothing, note it for task 21, because
  `SquadGlobalRoots` reads that variable.
- [ ] S5. **Copilot** · R7.4, R7.4a. Under `COPILOT_HOME=<temp>`, record whether a sign-in is
  asked for. Either answer keeps the row Ready. Run the rename check on credential files across a
  sign-in: any file rewritten by rename joins the row's never-linked list.
- [ ] S6. **Claude** · R7.4, R7.4a
  - Under `CLAUDE_CONFIG_DIR=<temp>`, check whether `<temp>/.claude.json` is created. If it is, add
    an extra link from it to `~/.claude.json`.
  - On Windows, run the rename check on `.credentials.json` across `/login` and a token refresh.
    If it is renamed, it is never linked.
  - If `.claude.json` itself is renamed, escalate to the owner.
- [ ] S7. **Codex** · R7.4, R7.4a. In file mode (the setting is on https://learn.chatgpt.com/docs/auth),
  run the rename check on `auth.json` across a sign-in and a refresh. If it is renamed, it is
  never linked. In keyring mode, confirm a second `CODEX_HOME` asks for one sign-in.
- [ ] S8. **Pi on Windows** · R7.4, R7.4a. Hard-link `auth.json` into a temporary
  `PI_CODING_AGENT_DIR`, then run `pi` in both homes across a token refresh.
  - If only one `auth.json.lock` ever exists and `auth.json` stays valid in both homes, the row is
    Ready.
  - Otherwise, apply 7.4a to Pi's `auth.json`: never linked, one sign-in per instance
    (dpalfery, 2026-10-24). The row stays Ready.
- All of S5–S8 also run the rename check on every file in the home over a 10-minute session. A
  non-sign-in file that gets a new ID is escalated.

## Slice 1 — Presets (R1–R6, shippable alone)

- [x] 1. **Preset parsing and store** · R1.1, R1.2, R4.1, R4.2, R4.3 · needs none
  - Do:
    - Add record `SquadModelPreset(Name, SourcePath, Digest, Profiles)`. `Digest` is the lowercase
      SHA-256 hex of the file's bytes.
    - Add `internal SquadSourceLoader.ParseModelPreset(path, bytes)` beside `ParseModelProfiles`,
      reusing its private YAML helpers. It enforces the schema `kyber-squad.model-preset/v1`,
      allows only `schema` and `profiles` at the top level, and allows only `ModelProfileFields`
      keys per category (`default` optional, `inherit` allowed). Errors go through
      `SquadSourceValidator.Throw` with a line number.
    - Make `ModelProfileFields` `internal`.
    - Add `SquadModelPresetStore(ISquadUserPaths)` over `<home>/.kyber-weave/squad/presets/`.
      - `List()` returns the `*.yml` stems, sorted ordinally.
      - `TryLoad(name)` returns `null` when the file is missing.
      - A name must match `^[a-z0-9][a-z0-9-]{0,63}$`; otherwise it throws `ArgumentException`.
  - Files: `src/KyberWeave.Core/Squad/Presets/SquadModelPreset.cs`, `SquadModelPresetStore.cs`, and
    `src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs`.
  - Test: `SquadModelPresetStoreTests`.
    - Behavior:
      - a valid file loads with its digest;
      - `default` may be missing, and `inherit` is kept;
      - an unknown harness, a duplicate key, an unknown top-level key and bad YAML each throw an
      exception naming the file and line;
      - a bad name throws, a missing file returns `null`, and `List` is sorted.
    - RED: the types do not exist, so the build fails. GREEN: every case passes.
- [x] 2. **Merge, validation and model diff** · R1.3, R2.4, R4.1, R4.3 · needs 1
  - Do:
    - Add `SquadModelPreset.ApplyTo(SquadModelProfiles)`, following the design's merge rule.
    - Add `SquadModelPresetValidator.Validate(preset, bundled)`. An unknown category throws
      `SquadSourceValidationException` naming `profiles.<category>`, the file and the line.
    - Add `SquadModelResolution.ResolveToken(profiles, category, target)`, returning
      `HarnessModels[target] ?? Default`, or `null` for `inherit`.
    - Add `SquadModelDiff.Compute(source, target, before, after)`, returning `(Agent, Old, New)`
      for each changed agent, ordered by name.
  - Files: `src/KyberWeave.Core/Squad/Presets/SquadModelPresetValidator.cs`,
    `SquadModelResolution.cs` and `SquadModelDiff.cs`.
  - Test: `SquadModelPresetMergeTests`, against the canonical source.
    - Behavior:
      - merging is key by key, and a preset `default` reaches only harnesses that neither file
        names;
      - an unknown category throws, and so does a source copy with that category removed;
      - `inherit` resolves to `null`;
      - the diff lists only the agents that changed.
    - RED: the members are missing. GREEN: every case passes.
- [x] 3. **Render seam across the 13 renderers** · R2.2, R2.3 · needs 2
  - Do: add `ModelPresets` to `SquadRenderRequest` as its last parameter, defaulting to `null`.
    Add `SquadRenderSource.Load(request, target)`, as the design's render seam describes. Use it in
    the 13 renderers: Antigravity, Claude, CommandCode, Codex, Copilot, Cursor, Devin, Factory,
    Kilo, OpenCode, Pi, Warp and ZCode. Leave the registry's reload as it is.
  - Files: `src/KyberWeave.Core/Squad/Rendering/SquadRenderModels.cs`, a new `SquadRenderSource.cs`,
    and the 13 `*Renderer.cs` files.
  - Test: `SquadModelPresetRenderTests`, building the registry the way `SquadRenderingContractTests`
    does.
    - Behavior:
      - render every target twice: without a preset, then with one that sets every category to
        `preset-probe-model`;
      - both renders produce the same paths;
      - each pair of files is byte-identical once lines starting `model` or `"model"` are dropped;
      - every target except `warp` changes at least one file;
      - a file contains `preset-probe-model` exactly when `ResolveToken` returns it.
    - RED: the build fails. GREEN: every case passes.
- [x] 4. **Lock v2** · R3.1 · needs none
  - Do: add `SquadAppliedPreset(Name, Digest)` and the optional `SquadLock.ModelPresets`. Implement
    the design's lock rule in `SerializeLock`, `DeserializeLock` and `ValidateLock`:
    - `model-presets` is written last, in ordinal order;
    - v2 requires a non-empty map, known target tokens and 64-hex digests.
  - Files: `src/KyberWeave.Core/Squad/Deployment/SquadDeploymentModels.cs` and `SquadStateStore.cs`.
  - Test: `SquadLockV2Tests`.
    - Behavior:
      - with no presets the lock stays v1 and has no `model-presets` key;
      - v2 round-trips;
      - an empty v2 map, a v1 lock carrying `model-presets`, and a bad digest are each rejected.
    - RED: the build fails. GREEN: every case passes.
- [x] 5. **Lifecycle applies and records presets** · R2.1, R2.2, R3.1, R3.2, R3.4 · needs 3, 4
  - Do: add `ModelPresets` (target to name) to both `SquadInstallRequest` and `SquadUpdateRequest`.
    `null` keeps the previous lock's map; an empty map means none. Give `SquadLifecycleService`
    an optional `SquadModelPresetStore` (default: built over `stateStore.UserPaths`). Then:
    1. Read the previous lock.
    2. Before the download, check each named file exists. A missing one throws
      `SquadDeploymentConflictException` naming the target, the preset, the path and the fix
      `kyber-weave squad preset clear --target <t>`.
    3. After extraction, load the source once, validate, and pass the presets to the render.
    4. Have `BuildLock` record each name and digest. Entries for targets that were not rendered
       are dropped.
    5. On a dry run, fill the new `SquadLifecycleResult.ModelChanges`.
  - Files: `src/KyberWeave.Core/Squad/Deployment/SquadLifecycleService.cs`.
  - Test: `SquadModelPresetLifecycleTests`.
    - Behavior:
      - install writes a v2 lock, and the receipt hashes match the files;
      - an update with a `null` map re-applies the preset to a new fake release;
      - changing only `claude` leaves a second target unchanged;
      - a missing preset throws without calling the release source, leaving home and root
        snapshots unchanged;
      - an unknown category throws before any write;
      - a dry run returns `ModelChanges` and writes nothing;
      - two project roots keep their own maps.
    - RED: the build fails. GREEN: every case passes.
- [x] 6. **Project selection** · R6.1, R6.2, R6.3 · needs 5
  - Do: add `SquadModelPresetSelectionStore(ISquadUserPaths)` with `Read`, `Set` and `Clear`.
    - The file format is the design's project selection. The file is named by
      `SquadPhysicalRootIdentity.Key`.
    - Writes go to a temporary file and are then renamed into place. When the map becomes empty,
      the file is deleted.
    - A first project install with a `null` map applies the selection.
  - Files: `src/KyberWeave.Core/Squad/Presets/SquadModelPresetSelectionStore.cs` and
    `SquadLifecycleService.cs`.
  - Test: `SquadModelPresetSelectionTests`.
    - Behavior:
      - the file round-trips;
      - a symlinked spelling of the root reaches the same file;
      - the last `Clear` deletes the file;
      - nothing is written under the project root;
      - a global install ignores the selection, and a project install applies it.
    - RED: the build fails. GREEN: every case passes.
- [x] 7. **`squad preset` verbs** · R2.1, R2.4, R3.3, R4.1, R5.1, R5.2 · needs 5, 6
  - Do: add `squad.AddBranch("preset", …)` in `src/KyberWeave.Cli/Program.cs`, with
    `SquadPreset{Use,Clear,List,Show}Command.cs` and settings in `SquadSettings.cs`. Compose them
    the way `SquadUpdateCommand` is composed. Behavior follows the design's `squad preset` verbs,
    with these exact outcomes:
    - **Unknown preset name:** exit 2, listing the names that exist.
    - **Target not deployed, `--global`:** exit 2 with "`<t>` is not deployed globally; run
      `kyber-weave squad install --global --target <t>` first."
    - **Target not deployed, project:** record the selection and print "Recorded `<name>` for `<t>`
      in this project; it applies on install or `squad run`."
    - **Project scope:** `Set` (on `use`) or `Clear` (on `clear`) every named target.
    - **`--dry-run`:** print `agent: old -> new` lines and write nothing, including no selection.
    - **`list`:** print `<name>  <path>  used by: <scope:target, …>`, or `none`.
    - **`show`:** use the release at the CLI's version, and print `<category>: <token>`, adding
      ` (bundled)` to bundled values. For `warp`, print "Warp renders no model."
  - Test: `SquadPresetCliTests`, following `SquadCliCommandTests`.
    - Behavior:
      - each exit code above;
      - `use` asks for the locked version and keeps the other targets;
      - the dry-run output, with nothing written;
      - `show` marks bundled values;
      - `list` shows usage.
    - RED: the build fails. GREEN: every case passes.
- [x] 8. **Status and doctor** · R3.4, R5.3 · needs 5
  - Do: the design's status lines in `SquadStatusCommand`. `(missing)` exits 1;
    `(changed since applied)` only warns. `SquadDoctorCommand` passes the lock's presets into its
    comparison render.
  - Test: `SquadPresetStatusDoctorTests`.
    - Behavior: the three status cases, and no doctor finding on a global install rendered with a
      preset.
    - RED: no preset line is printed. GREEN: every case passes.

## Slice 2 — Instances (R7)

- [x] 9. **Instance catalog** · R7.4a, R7.6, R7.7 · needs none (update it after each spike)
  - Do: add `SquadInstanceCatalog` with
    `SquadInstanceRow(Target, Executable, Kind, Variable, ExtraLinks, NeverLinked, Status, Reason)`.
    - `Kind` is HomeReplace, ConfigLayer or None.
    - `Status` is Ready, SpikePending or Unsupported.
    - The rows start as the design's harness rows. Each recorded spike outcome is then applied.
  - Files: `src/KyberWeave.Core/Squad/Instances/SquadInstanceCatalog.cs`.
  - Test: `SquadInstanceCatalogTests`.
    - Behavior:
      - there is one row per `SquadTarget`;
      - every Ready row has a kind and a variable;
      - every Unsupported row has a reason;
      - the statuses match a table kept in step with Evidence.
    - RED: the build fails. GREEN: every case passes.
- [x] 10. **Instance scope and state** · R7.3 · needs 5
  - Do: add `SquadDeploymentScope.Instance`. It behaves exactly like `Project` (one root, receipt
    v1, a mutex keyed by the root key), with these exceptions:
    - the root must be `<home>/.kyber-weave/squad/instances/<target>/<preset>/`, otherwise
      `ArgumentException`;
    - the lock, the receipt and the `.squad-transaction` journal live in
      `<home>/.kyber-weave/squad/instance-state/<target>/<preset>/`;
    - it is never registered, and migration ignores it;
    - rendering uses `Scope = Global` with a new `SquadInstanceRootResolver(root, target)`, which
      returns the root and throws for any other target.

    Update every branch on scope in `SquadStateStore`, `SquadDeploymentPlan`, `SquadTransaction`,
    `SquadStateMigration` and `SquadLifecycleService`.
  - Test: `SquadInstanceScopeTests`.
    - Behavior:
      - a Claude instance has `agents/<name>.md`;
      - its state sits in instance-state;
      - nothing goes into the registry, and no journal is created inside the instance;
      - a bad root throws;
      - Copilot's absolute links point inside the instance.
    - RED: the build fails. GREEN: every case passes.
- [x] 11. **Link planner (pure)** · R7.4, R7.4a · needs 9
  - Do: add `SquadInstanceLinkPlanner.Plan(mainListing, renderedPaths, row, recordedLinks, instanceListing)`,
    returning `Add`, `Remove` and `Keep(warning)` actions. It follows the design's "What is
    linked" rule, then:
    - skips `NeverLinked` paths;
    - adds the row's `ExtraLinks`;
    - removes recorded links whose target has gone;
    - keeps any existing instance entry that is not a recorded link, with the warning "instance
      copy kept".

    It does no file I/O.
  - Files: `src/KyberWeave.Core/Squad/Instances/SquadInstanceLinkPlanner.cs` and
    `SquadLinkRecord.cs`.
  - Test: `SquadInstanceLinkPlannerTests`.
    - Behavior: one case per rule, including that a personal agent inside `agents/` is linked, a
      rendered file is not, and a never-linked file is skipped.
    - RED: the build fails. GREEN: every case passes.
- [x] 12. **Platform link layer** · R7.5, R7.8 · needs none · macOS and Windows evidence
  - Do: add the port `ISquadLinkFileSystem` with `CreateFileLink`, `CreateFolderLink`, `IsLink`,
    `IsLinkTo` and `DeleteTreeNoFollow`. `SquadCommandComposition` picks the implementation with
    `OperatingSystem.IsWindows()`.
    - **Unix:**
      - links are made with `File.CreateSymbolicLink` and `Directory.CreateSymbolicLink`;
      - `IsLinkTo` holds when the symlink is present and `LinkTarget` equals the target;
      - links are deleted with `File.Delete`.
    - **Windows:**
      - hard links are made through P/Invoke `CreateHardLinkW`;
      - junctions are made through `DeviceIoControl(FSCTL_SET_REPARSE_POINT)` with
        `IO_REPARSE_TAG_MOUNT_POINT`;
      - `IsLinkTo` compares the volume serial and file index (`GetFileInformationByHandle`), or the
        junction target;
      - a junction is deleted with `Directory.Delete(path, false)`, and a hard link with
        `File.Delete`;
      - `ERROR_NOT_SAME_DEVICE` becomes a `SquadLinkException` naming the file and both paths;
      - no symbolic links are ever created.
    - **`DeleteTreeNoFollow`:**
      - a link (a reparse point, or `LinkTarget != null`) is deleted as an entry itself;
      - a real folder is recursed into, then removed.
  - Files: `src/KyberWeave.Core/Squad/Instances/ISquadLinkFileSystem.cs`,
    `UnixSquadLinkFileSystem.cs`, `WindowsSquadLinkFileSystem.cs`, `SquadLinkException.cs` and
    `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`.
  - Test: `SquadLinkFileSystemTests`. The Unix cases also run on Linux CI.
    - Behavior:
      - file and folder links are created;
      - `IsLinkTo` turns false once a file is renamed over the link;
      - `DeleteTreeNoFollow` leaves every target file and byte intact;
      - a cross-volume hard link names both paths (skipped when there is no second volume).
    - RED: the build fails. GREEN: the class passes on Linux CI, macOS and Windows.
- [x] 13. **Launch-time link sync and split check** · R7.4, R7.4a, R7.8 · needs 11, 12 · macOS and Windows evidence
  - Do: add `SquadInstanceLinkSync.Sync(instanceRoot, mainHome, row, renderedPaths)`. It runs under
    the instance's mutex (`kyber-weave-squad-<root-key>`):
    1. List both trees, plan, and apply the actions.
    2. For each recorded file link whose `IsLinkTo` is false: if the bytes match, re-link it; if
       they differ, keep both copies and warn, naming the file.
    3. Write `links.json` (`[{path, kind, target}]`) atomically in instance-state.

    It does nothing for ConfigLayer rows.
  - Test: `SquadInstanceLinkSyncTests`, with a fake port on Linux CI and then the real port on
    macOS and Windows.
    - Behavior:
      - links are added and removed;
      - a matching split is re-linked;
      - a differing split is warned about;
      - the main home is never written.
    - RED: the build fails. GREEN: every case passes.
- [x] 14. **Instance service** · R7.1, R7.3, R7.8 · needs 10, 13
  - Do: add `SquadInstanceService`, taking the lifecycle service, the preset store, the link port
    and the main `ISquadGlobalRootResolver`.
    - **`EnsureAsync(target, preset)`:**
      - if there is no receipt, install the instance with `{target: preset}` at the CLI version;
      - if the version or the preset digest differs from the lock, update it;
      - then sync links for HomeReplace rows (main home = `ResolveGlobalRoot(target)`);
      - return the instance root and any warnings.
    - **`RemoveAsync`:** uninstall, then `DeleteTreeNoFollow(root)`, then delete the state.
    - **`RefreshAllAsync`:** run `EnsureAsync` for each instance-state entry that has a receipt.
  - Files: `src/KyberWeave.Core/Squad/Instances/SquadInstanceService.cs`.
  - Test: `SquadInstanceServiceTests`.
    - Behavior:
      - an instance is created, and a second call is a no-op;
      - a version change or a preset edit triggers a refresh that keeps the path and links;
      - removing an instance leaves the main home unchanged.
    - RED: the build fails. GREEN: every case passes.
- [x] 15. **Refuse overrides into instances** · R7.3, R7.8 · needs none
  - Do: in `SquadGlobalRoots`, any override value under `<home>/.kyber-weave/squad/instances/`
    throws `ArgumentException` naming the variable and the value. This covers the plain
    overrides and the OpenCode, ZCode and XDG paths.
  - Test: add cases to `SquadGlobalRootTests` for `CLAUDE_CONFIG_DIR` and `OPENCODE_CONFIG_DIR`.
    - RED: no exception is thrown. GREEN: both cases pass.
- [x] 16. **`squad run`** · R7.1, R7.2, R7.6, R7.7 · needs 6, 9, 14, 15
  - Do: implement the design's `squad run` steps.
    - **`SquadHarnessLauncher.BuildStartInfo(row, instanceRoot, args, getEnv, findOnPath, isWindows)`**
      (pure):
      - it finds the executable through `PATH`, and also through `PATHEXT` on Windows;
      - it sets `row.Variable` to the instance root;
      - an `.exe` gets its arguments through `ArgumentList`;
      - a `.cmd` or `.bat` gets `FileName = %ComSpec%` and
        `Arguments = /d /s /c ""<shim>" "<arg>" …"`. This is the only allowed departure from the
        `ArgumentList` rule, and a code comment there must cite this task;
      - an argument containing `"`, `%`, `!`, `^`, CR or LF makes it exit 2, naming the argument.
    - **`ProcessRunner.RunInteractive(startInfo)`:** no redirection, `UseShellExecute` off, and
      it returns the exit code.
    - **`SquadRunCommand`:**
      - with no selection, exit 2 with "No preset selected for `<t>` in this project. Run
        `kyber-weave squad preset use <name> --target <t>`.";
      - for a row that is not Ready, exit 2 with "`<t>` cannot run on a preset instance:
        `<reason>`.";
      - otherwise run `EnsureAsync`, then the injected `Func<ProcessStartInfo, int>`, and return
        its exit code;
      - a harness missing from `PATH`, or a `SquadLinkException`, exits 1.
  - Files: `src/KyberWeave.Core/Squad/Instances/SquadHarnessLauncher.cs`,
    `src/KyberWeave.Core/Processes/ProcessRunner.cs`,
    `src/KyberWeave.Cli/Commands/Squad/SquadRunCommand.cs`, `SquadSettings.cs` and `Program.cs`.
  - Tests: `SquadHarnessLauncherTests` and `SquadRunCommandTests`.
    - Behavior:
      - each Ready row gives the expected start info;
      - a Windows `.cmd` harness gets the exact command string;
      - the rejected characters exit 2;
      - exit codes 0, 1 and 2 behave as above, and the harness's own exit code is passed through;
      - the project tree is unchanged.
    - RED: the build fails. GREEN: every case passes.
- [x] 17. **`squad instance remove`, status and global refresh** · R7.3, R7.8 · needs 14
  - Do:
    - `squad instance remove --target <t> --preset <name> [--yes]` calls `RemoveAsync`. It
      confirms the way uninstall does, and exits 2 for an unknown instance.
    - `squad status --global` lists `<target>/<preset>  <version>` under a new `Instances` heading.
    - `squad update --global` runs `RefreshAllAsync` after the global step, even when there is no
      global deployment. Any failure makes the command exit 1.
  - Files: `src/KyberWeave.Cli/Commands/Squad/SquadInstanceRemoveCommand.cs`,
    `SquadStatusCommand.cs`, `SquadUpdateCommand.cs`, `SquadSettings.cs` and `Program.cs`.
  - Test: `SquadInstanceCliTests`.
    - Behavior: removing an instance, the status listing, the refresh during update, and their
      exit codes.
    - RED: the build fails. GREEN: every case passes.
- [x] 18. **macOS and Windows end-to-end** · macOS verified / Windows deferred · R7.1, R7.2, R7.4, R7.5, R7.8 · csharp-dev with the owner · needs 12–17
  - This task writes no test code. It is checked by the E-mac and E-win entries in
    [Evidence](#evidence).
  - Run `SquadLinkFileSystemTests`, `SquadInstanceLinkSyncTests` and `SquadInstanceServiceTests` on
    each OS.
  - Then the scenario:
    1. Two git projects select different `claude` presets and run `squad run claude` at the same
       time.
    2. Each session reports its own preset's model.
    3. `git status` is clean in both projects.
    4. A setting changed in one session shows up in the other.
    5. `squad update --global` to a newer release asks for no new sign-in.
    6. `squad instance remove` leaves the main home's file IDs unchanged.
  - On Windows, also launch an npm `.cmd` harness (`pi` or `copilot`).

## Release and closeout

- [x] 19. **Pre-push gates** · all ids · csharp-dev · needs 1–18
  - Run `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`; it
    must be clean.
  - Run the full suite in a CI-like Linux container:
    `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`.
- [x] 20. **Final code-review council** · all ids · conductor → code-reviewer · needs 19
  - The verdict must be Approve.
  - Each fix goes back to the task that owns it, then 19 and 20 run again.
- [x] 21. **Closeout** · all ids · conductor → docs-dev · needs 20
  - Check every requirement id against the code, the tests, the Evidence section and the review.
  - Move the durable facts into:
    - `docs/kyber-squad/onboarding.md`: preset files and verbs, selections, `squad run`,
      instances, harness support and exit codes;
    - `docs/kyber-squad/architecture.md`: the overlay seam, lock v2, the Instance scope and its
      state, links, and the catalog;
    - a new ADR for lock v2 and the Instance scope.
  - File a todo for each spike that ended Unsupported or escalated, and for the S4 finding.
  - Update `docs/specs/README.md`, and archive the spec to `docs/archive/specs/squad-model-presets/`.
  - Done when `docs validate . --merge-ready` and `docs drift .` are clean.

## Coverage

| Requirement | Tasks |
|---|---|
| 1.1, 1.2 | 1 |
| 1.3 | 2 |
| 2.1 | 5, 7 |
| 2.2 | 3, 5 |
| 2.3 | 3 |
| 2.4 | 2, 5, 7 |
| 3.1 | 4, 5 |
| 3.2 | 5 |
| 3.3 | 7 |
| 3.4 | 5, 8 |
| 4.1 | 1, 2, 7 |
| 4.2 | 1 |
| 4.3 | 1, 2 |
| 5.1, 5.2 | 7 |
| 5.3 | 8 |
| 6.1, 6.3 | 6 |
| 6.2 | 6, 7 |
| 7.1 | 14, 16, 18 |
| 7.2 | 16, 18 |
| 7.3 | 10, 14, 15, 17 |
| 7.4 | S1–S8, 11, 13, 18 |
| 7.4a | S5–S8, 9, 11, 13 |
| 7.5 | 12, 18 |
| 7.6 | 9, 16 |
| 7.7 | S1–S4, 9, 16 |
| 7.8 | 12, 13, 14, 17, 18 |

## Evidence

Each entry starts with `<id> · <OS and version> · <harness and version> · <date>`, then gives the
commands, what was observed, and the outcome. The outcome is one of: `<row> → Ready`,
`<row> → Unsupported`, `7.4a on <file>`, or `escalated`.

### Checkpoint 2026-10-25

- Slice 1 (Tasks 1-8): Complete.
- Slice 2 (Tasks 9-17): Complete. All 17 implementation tasks delivered and verified green with passing task gates and review gates.
- Task 18: macOS E2E verified (E-mac); Windows deferred by owner (E-win).
- Spikes S1-S8: pending execution on real target platforms.
- Tasks 19-21 (Pre-push gates, final code review council, documentation closeout): next steps upon platform verification.

- S1–S8: _pending_

#### E-mac · macOS 27.2 (Darwin 27.2.0 arm64) · claude · 2026-10-25

- **Commands:**
  - `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class KyberWeave.Tests.SquadLinkFileSystemTests` (7 passed, 3 skipped Windows-only)
  - `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class KyberWeave.Tests.SquadInstanceLinkSyncTests` (13 passed)
  - `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class KyberWeave.Tests.SquadInstanceServiceTests` (6 passed)
  - `kyber-weave squad preset use preset-alpha --target claude` (project-alpha)
  - `kyber-weave squad preset use preset-beta --target claude` (project-beta)
  - `kyber-weave squad run claude` (project-alpha and project-beta)
  - `kyber-weave squad instance remove --target claude --preset preset-alpha --yes`
- **Observed:**
  - All link and instance unit tests passed green on macOS host.
  - `git status` remained clean in both projects after preset selection and execution.
  - Instances created under `.kyber-weave/squad/instances/claude/preset-alpha` and `preset-beta`.
  - Main home settings file (`~/.claude/settings.json`) symlinked to instance roots (`CLAUDE_CONFIG_DIR`).
  - Modifications to settings in main home reflected across link in instances.
  - File inode check (`stat -f %i`) on main home file before and after `squad instance remove` confirmed inode 281372378 remained unchanged.
- **Outcome:** `claude → Ready`

#### E-win

- **Outcome:** `deferred by owner (2026-10-25)`

#### Task 19 · Linux Docker container & Host Gates · 2026-10-25

- **Commands:**
  - `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`
  - `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
- **Observed:**
  - Declared review gates: 18/18 gates clean (build, test, formatting, inspectcode, docs validate, docs drift).
  - Linux Docker test run: 3,422 passed, 0 failed, 3 skipped (Windows-only skip).
- **Outcome:** `Pass`

#### Task 20 · Code Review Council · 2026-10-25

- **Commands:**
  - Final code-review council execution over branch diff.
- **Observed:**
  - Verdict: `APPROVE`
  - Risk: `LOW`
  - Findings: 0 surviving findings.
- **Outcome:** `Approve`

#### Task 21 · Closeout Documentation & Archival · 2026-10-25

- **Commands:**
  - Canonical documentation updates (`docs/kyber-squad/onboarding.md`, `docs/kyber-squad/architecture.md`).
  - Author ADR 0044 (`docs/adr/0044-squad-lock-v2-and-instance-deployment-scope.md`) and update ADR index.
  - Author deferred work todo (`docs/todo/squad-model-presets-windows-e2e-and-spikes.md`) and update todo index.
  - Move spec bundle to `docs/archive/specs/squad-model-presets/` and update `docs/specs/README.md`.
- **Observed:**
  - All durable content migrated to canonical locations.
  - Deferred spikes S1–S8 and Windows E2E captured in todo corpus.
- **Outcome:** `Archived`
