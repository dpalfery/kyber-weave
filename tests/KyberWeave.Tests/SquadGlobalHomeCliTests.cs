using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the CLI contract for home-level Squad state: a <c>--global</c> run finds its one receipt
/// whatever directory it is launched from, offers the registered projects through the picker,
/// honours <c>--projects</c> without prompting, and keeps project, registry and global state
/// consistent when one of them fails.
/// </summary>
/// <remarks>
/// Every test runs against a temp home, a temp application data directory, and temp global roots
/// supplied through the internal command seams. No test reads or writes the real home, the real
/// application data directory, or a harness directory. Project state is seeded through the same
/// lifecycle the CLI uses, so the registry is whatever the install path registers; a hand-written
/// registry file would pin a format this contract does not yet fix.
/// </remarks>
public sealed class SquadGlobalHomeCliTests : IDisposable
{
    private const string InstalledVersion = "1.2.3";
    private const string UpdatedVersion = "1.2.4";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void UpdateGlobalFromTwoUnrelatedDirectoriesFindsTheSameGlobalReceipt()
    {
        using Harness h = NewHarness("unrelated-directories");
        ScriptedAgentRenderer renderer = new("architect");
        string first = h.MakeDirectory("unrelated-first");
        string second = h.MakeDirectory("unrelated-second");
        InstallGlobal(h, first, renderer, SquadTarget.Codex);
        SquadUpdateCommand command = UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), isInteractive: false);

        CommandExecution fromFirst = Capture(() => command.Execute(null!, GlobalUpdate(first)));
        CommandExecution fromSecond = Capture(() => command.Execute(null!, GlobalUpdate(second)));

        AssertExit(fromFirst, 0);
        AssertExit(fromSecond, 0);
        Assert.DoesNotContain("No Kyber-Squad deployment found", fromSecond.Output, StringComparison.Ordinal);
        Assert.Equal(UpdatedVersion, VersionOf(GlobalLockPath(h)));
    }

    [Fact]
    public void UpdateGlobalWithAPathWarnsThatThePathIsIgnoredAndStillUpdates()
    {
        using Harness h = NewHarness("global-with-path");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        string named = h.MakeDirectory("elsewhere");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);

        CommandExecution execution = Capture(() =>
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), isInteractive: false)
                .Execute(null!, GlobalUpdate(named)));

        AssertExit(execution, 0);
        Assert.Contains("is ignored under --global", execution.Output, StringComparison.Ordinal);
        Assert.Contains(named, execution.Output, StringComparison.Ordinal);
        Assert.Equal(UpdatedVersion, VersionOf(GlobalLockPath(h)));
    }

    [Fact]
    public void UpdateGlobalPickerAllUpdatesEveryNonStaleProjectAndLeavesStaleOneAlone()
    {
        using Fleet fleet = ArrangeFleet("picker-all");
        RecordingProjectPicker picker = new(AllNonStale);
        PromptRecorder prompts = new();
        byte[]? staleLockBefore = Snapshot(fleet.Stale.LockPath);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, isInteractive: true, prompts.Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 0);
        Assert.Single(picker.Calls);
        Assert.Contains(picker.Calls[0], entry => entry.Stale && entry.Path == fleet.Stale.Physical);
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Beta.LockPath));
        AssertUnchanged(staleLockBefore, fleet.Stale.LockPath);
    }

    [Fact]
    public void UpdateGlobalPickerNoneUpdatesNoProject()
    {
        using Fleet fleet = ArrangeFleet("picker-none");
        RecordingProjectPicker picker = new(NoProjects);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, isInteractive: true, new PromptRecorder().Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 0);
        Assert.Single(picker.Calls);
        Assert.Equal(UpdatedVersion, VersionOf(GlobalLockPath(fleet.Harness)));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    [Fact]
    public void UpdateGlobalPickerOneProjectUpdatesOnlyThatProject()
    {
        using Fleet fleet = ArrangeFleet("picker-one");
        RecordingProjectPicker picker = new(_ => [fleet.Alpha.Physical]);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, isInteractive: true, new PromptRecorder().Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 0);
        Assert.Single(picker.Calls);
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    /// <summary>
    /// <c>None</c> means nothing, so it cannot sit beside another choice. The interactive picker
    /// re-prompts on that combination; the flag form has no prompt to re-ask, so it must be refused
    /// as client input before any write. The re-prompt itself has no seam yet (see the report).
    /// </summary>
    [Fact]
    public void UpdateGlobalNoneCombinedWithAnotherProjectChoiceIsRejectedWithoutWrites()
    {
        using Fleet fleet = ArrangeFleet("none-combined");
        byte[]? globalLockBefore = Snapshot(GlobalLockPath(fleet.Harness));
        byte[]? alphaLockBefore = Snapshot(fleet.Alpha.LockPath);
        RecordingProjectPicker picker = new(AllNonStale);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, isInteractive: false, new PromptRecorder().Answer).Execute(
                null!,
                new SquadUpdateSettings
                {
                    Path = fleet.Anchor,
                    Global = true,
                    Version = UpdatedVersion,
                    Yes = true,
                    Projects = ["none", fleet.Alpha.Physical]
                }));

        AssertExit(execution, 2);
        Assert.Contains("none", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(picker.Calls);
        AssertUnchanged(globalLockBefore, GlobalLockPath(fleet.Harness));
        AssertUnchanged(alphaLockBefore, fleet.Alpha.LockPath);
    }

    [Fact]
    public void UpdateGlobalNonInteractiveWithoutProjectsNeverInvokesThePicker()
    {
        using Fleet fleet = ArrangeFleet("non-interactive");
        RecordingProjectPicker picker = new(AllNonStale);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, isInteractive: false, new PromptRecorder().Answer).Execute(null!, GlobalUpdate(fleet.Anchor, yes: false)));

        AssertExit(execution, 0);
        Assert.Empty(picker.Calls);
        Assert.Equal(UpdatedVersion, VersionOf(GlobalLockPath(fleet.Harness)));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    [Fact]
    public void UpdateGlobalProjectsAllUpdatesEveryNonStaleProjectWithoutPrompting()
    {
        using Fleet fleet = ArrangeFleet("projects-all");
        RecordingProjectPicker picker = new(AllNonStale);
        PromptRecorder prompts = new();

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, isInteractive: true, prompts.Answer).Execute(null!, GlobalUpdate(fleet.Anchor, ["all"])));

        AssertExit(execution, 0);
        Assert.Empty(picker.Calls);
        Assert.Empty(prompts.Questions);
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Beta.LockPath));
    }

    [Fact]
    public void UpdateGlobalProjectsNoneUpdatesNoProjectWithoutPrompting()
    {
        using Fleet fleet = ArrangeFleet("projects-none");
        RecordingProjectPicker picker = new(AllNonStale);
        PromptRecorder prompts = new();

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, isInteractive: true, prompts.Answer).Execute(null!, GlobalUpdate(fleet.Anchor, ["none"])));

        AssertExit(execution, 0);
        Assert.Empty(picker.Calls);
        Assert.Empty(prompts.Questions);
        Assert.Equal(InstalledVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    [Fact]
    public void UpdateGlobalProjectsPathUpdatesOnlyThatProjectWithoutPrompting()
    {
        using Fleet fleet = ArrangeFleet("projects-path");
        RecordingProjectPicker picker = new(AllNonStale);
        PromptRecorder prompts = new();

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, isInteractive: true, prompts.Answer).Execute(null!, GlobalUpdate(fleet.Anchor, [fleet.Alpha.Root])));

        AssertExit(execution, 0);
        Assert.Empty(picker.Calls);
        Assert.Empty(prompts.Questions);
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    /// <summary>
    /// A named path that is stale is warned about and skipped, not updated and not treated as a
    /// failure: the operator named it, so silence would hide that nothing happened to it.
    /// </summary>
    [Fact]
    public void UpdateGlobalProjectsNamingAStaleProjectWarnsAndSkipsIt()
    {
        using Fleet fleet = ArrangeFleet("projects-stale-named");
        byte[]? staleLockBefore = Snapshot(fleet.Stale.LockPath);

        CommandExecution execution = Capture(() =>
            fleet.Command(new RecordingProjectPicker(NoProjects), isInteractive: false, new PromptRecorder().Answer)
                .Execute(null!, GlobalUpdate(fleet.Anchor, [fleet.Stale.Root])));

        AssertExit(execution, 0);
        Assert.Contains("skipping stale project", execution.Output, StringComparison.Ordinal);
        AssertUnchanged(staleLockBefore, fleet.Stale.LockPath);
    }

    /// <summary>
    /// A dry run plans and never writes. A global dry run that names an unregistered project with
    /// in-repo state must not import it: the home project state and the registry stay as they were, the
    /// in-repo files stay byte-identical, and the project is reported as skipped, so the exit stays 0.
    /// </summary>
    [Fact]
    public void GlobalDryRunNamingAnImportableProjectWritesNothingAndSkipsIt()
    {
        using Harness h = NewHarness("dry-run-import");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("global-anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        string importable = h.MakeDirectory("importable");
        WriteInRepoLegacyState(h, importable);
        string inRepoReceipt = Path.Combine(importable, ".kyber-weave", "squad.receipt.json");
        string inRepoLock = Path.Combine(importable, ".kyber-weave", "squad.lock.yml");
        string homeProjectState = Path.Combine(
            h.Home,
            ".kyber-weave",
            "squad",
            "projects",
            SquadPhysicalRootIdentity.Resolve(importable).Key);
        byte[]? receiptBefore = Snapshot(inRepoReceipt);
        byte[]? lockBefore = Snapshot(inRepoLock);
        byte[]? globalLockBefore = Snapshot(GlobalLockPath(h));
        byte[]? registryBefore = Snapshot(RegistryPath(h));
        SquadUpdateSettings settings = GlobalUpdate(anchor, [importable]);
        settings.DryRun = true;

        CommandExecution execution = Capture(() =>
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), isInteractive: false)
                .Execute(null!, settings));

        AssertExit(execution, 0);
        Assert.Contains("on the next non-dry-run invocation", execution.Output, StringComparison.Ordinal);
        Assert.Contains("0 updated, 0 failed, 1 skipped", execution.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(homeProjectState), "A dry run must not create the home project state.");
        Assert.Equal(registryBefore, Snapshot(RegistryPath(h)));
        AssertUnchanged(receiptBefore, inRepoReceipt);
        AssertUnchanged(lockBefore, inRepoLock);
        AssertUnchanged(globalLockBefore, GlobalLockPath(h));
    }

    /// <summary>
    /// An unknown path is refused before any global work starts, and the hint names the nearest
    /// registered project so a typo is one copy-paste away from being fixed.
    /// </summary>
    [Fact]
    public void UpdateGlobalUnknownProjectsPathExitsTwoWithNearestHintAndNoWrites()
    {
        using Fleet fleet = ArrangeFleet("unknown-path");
        byte[]? globalLockBefore = Snapshot(GlobalLockPath(fleet.Harness));
        byte[]? alphaLockBefore = Snapshot(fleet.Alpha.LockPath);
        string typo = Path.Combine(fleet.Harness.Root, "alpah");

        CommandExecution execution = Capture(() =>
            fleet.Command(new RecordingProjectPicker(NoProjects), isInteractive: false, new PromptRecorder().Answer)
                .Execute(null!, GlobalUpdate(fleet.Anchor, [typo])));

        AssertExit(execution, 2);
        Assert.Contains(fleet.Alpha.Physical, execution.Output, StringComparison.Ordinal);
        AssertUnchanged(globalLockBefore, GlobalLockPath(fleet.Harness));
        AssertUnchanged(alphaLockBefore, fleet.Alpha.LockPath);
    }

    [Fact]
    public void UpdateProjectsWithoutGlobalExitsTwoNamingTheGlobalFlag()
    {
        using Harness h = NewHarness("projects-without-global");
        ScriptedAgentRenderer renderer = new("architect");
        Project alpha = InstallProject(h, "alpha", renderer);
        byte[]? alphaLockBefore = Snapshot(alpha.LockPath);

        CommandExecution execution = Capture(() =>
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), isInteractive: false)
                .Execute(null!, new SquadUpdateSettings { Path = alpha.Root, Global = false, Version = UpdatedVersion, Projects = ["all"] }));

        AssertExit(execution, 2);
        Assert.Contains("--global", execution.Output, StringComparison.Ordinal);
        AssertUnchanged(alphaLockBefore, alpha.LockPath);
    }

    [Fact]
    public void UpdateGlobalWhenTheGlobalUpdateFailsNeverOffersThePicker()
    {
        using Fleet fleet = ArrangeFleet("global-fails");
        RecordingProjectPicker picker = new(AllNonStale);
        using FailingReleaseSource failing = new();

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, isInteractive: true, new PromptRecorder().Answer, failing)
                .Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 1);
        Assert.Empty(picker.Calls);
        Assert.Equal(InstalledVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    /// <summary>
    /// One project failing must not stop the rest, and the operator must be told which one failed:
    /// the run ends with a summary and exit 1 rather than a silent partial success.
    /// </summary>
    [Fact]
    public void UpdateGlobalWhenOneProjectFailsUpdatesTheRestPrintsSummaryAndExitsOne()
    {
        using Fleet fleet = ArrangeFleet("one-project-fails");
        string betaConfigDirectory = Path.Combine(fleet.Beta.Root, ".kyber-weave");
        Directory.CreateDirectory(betaConfigDirectory);
        File.WriteAllText(Path.Combine(betaConfigDirectory, "kyber-weave.yml"), "key: [unterminated\n", Encoding.UTF8);
        RecordingProjectPicker picker = new(AllNonStale);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, isInteractive: true, new PromptRecorder().Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 1);
        Assert.Contains(fleet.Alpha.Physical, execution.Output, StringComparison.Ordinal);
        Assert.Contains(fleet.Beta.Physical, execution.Output, StringComparison.Ordinal);
        Assert.Contains("failed", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    /// <summary>
    /// The global form runs from an unrelated directory, so the receipt is found by the home anchor
    /// rather than by the directory the operator happened to be in.
    /// </summary>
    [Fact]
    public void UpdateGlobalTargetAllReplaceManagedOverwritesEditedOwnedFile()
    {
        using Harness h = NewHarness("replace-managed");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        string architect = FindInstalled(h, SquadTarget.Codex, "architect.md");
        byte[] canonical = File.ReadAllBytes(architect);
        File.WriteAllText(architect, "locally edited architect\n", Encoding.UTF8);
        byte[] edited = File.ReadAllBytes(architect);

        CommandExecution execution = Capture(() =>
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), isInteractive: false)
                .Execute(null!, GlobalUpdate(h.MakeDirectory("unrelated-second-run"), targets: ["all"], replaceManaged: true)));

        AssertExit(execution, 0);
        Assert.NotEqual(edited, File.ReadAllBytes(architect));
        AssertUnchanged(canonical, architect);
    }

    /// <summary>
    /// The same run that would overwrite the owned file must not overwrite anything once it finds a
    /// new path held by an unowned hand-authored file: the collision is planned before any write, so
    /// the owned file keeps the operator's edit, the receipt and lock keep their bytes, and the
    /// unowned file is untouched.
    /// </summary>
    [Fact]
    public void UpdateGlobalTargetAllReplaceManagedWithUnmanagedNewPathFailsWholeRunWithoutWrites()
    {
        using Harness h = NewHarness("unmanaged-collision");
        ScriptedAgentRenderer installRenderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, installRenderer, SquadTarget.Codex);
        string architect = FindInstalled(h, SquadTarget.Codex, "architect.md");
        File.WriteAllText(architect, "locally edited architect\n", Encoding.UTF8);
        byte[] edited = File.ReadAllBytes(architect);
        string collider = Path.Combine(Path.GetDirectoryName(architect)!, "collider.md");
        byte[] unmanaged = Encoding.UTF8.GetBytes("hand-authored collider\n");
        File.WriteAllBytes(collider, unmanaged);
        byte[]? receiptBefore = Snapshot(GlobalReceiptPath(h));
        byte[]? lockBefore = Snapshot(GlobalLockPath(h));
        ScriptedAgentRenderer updateRenderer = new("architect", "collider");

        CommandExecution execution = Capture(() =>
            UpdateCommand(h, updateRenderer, new RecordingProjectPicker(NoProjects), isInteractive: false)
                .Execute(null!, GlobalUpdate(h.MakeDirectory("unrelated-second-run"), targets: ["all"], replaceManaged: true)));

        AssertExit(execution, 1);
        Assert.Contains("Unmanaged path", execution.Output, StringComparison.Ordinal);
        Assert.True(
            execution.Output.Contains("--adopt", StringComparison.Ordinal) ||
            execution.Output.Contains("Move the file aside", StringComparison.Ordinal),
            $"The collision must name the way out.{Environment.NewLine}{execution.Output}");
        AssertUnchanged(unmanaged, collider);
        AssertUnchanged(edited, architect);
        AssertUnchanged(receiptBefore, GlobalReceiptPath(h));
        AssertUnchanged(lockBefore, GlobalLockPath(h));
        Assert.False(
            Directory.Exists(h.GlobalRoot(SquadTarget.Claude)),
            "A run that fails on one path must not write into another target's global root.");
    }

    [Fact]
    public void StatusGlobalListsRegisteredProjectsWithStaleFlags()
    {
        using Fleet fleet = ArrangeFleet("status-global");
        SquadStatusCommand status = new(userPaths: fleet.Harness.UserPaths, stateStore: fleet.Harness.Store, globalRoots: fleet.Harness.GlobalRoots);

        CommandExecution execution = Capture(() =>
            status.Execute(null!, new SquadStatusSettings { Path = fleet.Anchor, Global = true }));

        string alphaLine = LineContaining(execution.Output, fleet.Alpha.Physical);
        string staleLine = LineContaining(execution.Output, fleet.Stale.Physical);
        Assert.DoesNotContain("stale", alphaLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(stale:", alphaLine, StringComparison.Ordinal);
        Assert.Contains("(stale:", staleLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Doctor is the one place an operator sees the state the migration and registry left behind,
    /// so each category must be named: a stale registry entry, a legacy global root the migration
    /// could not move, and in-repo legacy state that was imported but kept.
    /// </summary>
    [Fact]
    public void DoctorGlobalReportsStaleEntriesUnmigratedLegacyRootsAndRetainedInRepoState()
    {
        using Fleet fleet = ArrangeFleet("doctor-global");
        WriteLegacyGlobalRoot(fleet.Harness, "legacy-alpha", "agents/alpha.toml", "alpha\n");
        WriteLegacyGlobalRoot(fleet.Harness, "legacy-beta", "agents/beta.toml", "beta\n");
        Project gamma = InstallProject(fleet.Harness, "gamma", fleet.Renderer);
        WriteInRepoLegacyState(fleet.Harness, gamma.Root);
        ScriptedAgentRenderer renderer = fleet.Renderer;

        CommandExecution execution = Capture(() =>
            new SquadDoctorCommand(
                executor: new FakeProcessExecutor(),
                userPaths: fleet.Harness.UserPaths,
                workingDirectory: fleet.Anchor,
                globalRoots: fleet.Harness.GlobalRoots,
                renderer: renderer,
                stateStore: fleet.Harness.Store)
            .Execute(null!, new SquadDoctorSettings { Path = fleet.Anchor, Global = true }));

        Assert.Contains("is stale (", execution.Output, StringComparison.Ordinal);
        Assert.Contains("until its path is restored", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("uninstall it", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fleet.Stale.Physical, execution.Output, StringComparison.Ordinal);
        Assert.Contains("legacy-alpha", execution.Output, StringComparison.Ordinal);
        Assert.Contains("legacy-beta", execution.Output, StringComparison.Ordinal);
        Assert.Contains("retain", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(gamma.Physical, ".kyber-weave"), execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Global uninstall removes machine-wide files and nothing else. A project that still depends on
    /// the registry, or a registry the operator can see, must keep the same bytes.
    /// </summary>
    [Fact]
    public void UninstallGlobalLeavesProjectStateAndRegistryUntouched()
    {
        using Fleet fleet = ArrangeFleet("uninstall-global");
        string registry = RegistryPath(fleet.Harness);
        byte[]? registryBefore = Snapshot(registry);
        byte[]? alphaLockBefore = Snapshot(fleet.Alpha.LockPath);
        byte[]? alphaReceiptBefore = Snapshot(fleet.Alpha.ReceiptPath);
        RecordingProjectPicker picker = new(AllNonStale);
        SquadUninstallCommand command = new(
            userPaths: fleet.Harness.UserPaths,
            stateStore: fleet.Harness.Store,
            lifecycleService: fleet.Harness.Lifecycle(fleet.Renderer),
            isInteractive: false,
            readAnswer: null,
            globalRoots: fleet.Harness.GlobalRoots);

        CommandExecution execution = Capture(() =>
            command.Execute(null!, new SquadUninstallSettings { Path = fleet.Anchor, Global = true, Yes = true }));

        AssertExit(execution, 0);
        Assert.Empty(picker.Calls);
        Assert.False(File.Exists(GlobalReceiptPath(fleet.Harness)), "Global uninstall must remove the global receipt.");
        AssertUnchanged(alphaLockBefore, fleet.Alpha.LockPath);
        AssertUnchanged(alphaReceiptBefore, fleet.Alpha.ReceiptPath);
        AssertUnchanged(registryBefore, registry);
    }

    /// <summary>
    /// A migrated global deployment that was uninstalled must stay uninstalled. The migration keeps the legacy copy
    /// for an older CLI, so the command that runs after the uninstall must not copy it back into the home.
    /// </summary>
    [Fact]
    public void UninstallOfMigratedGlobalStateIsNotUndoneByTheNextCommand()
    {
        using Harness h = NewHarness("uninstall-migrated-global");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("global-anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        MoveGlobalStateToLegacyRoot(h, anchor);

        AssertExit(Uninstall(h, renderer, anchor, global: true), 0);
        Assert.False(File.Exists(GlobalReceiptPath(h)), "Global uninstall must remove the global receipt.");

        CommandExecution status = Status(h, anchor, global: true);

        AssertExit(status, 1);
        Assert.Contains("No Kyber-Squad deployment found", status.Output, StringComparison.Ordinal);
        Assert.False(
            File.Exists(GlobalReceiptPath(h)),
            "The command after an uninstall must not re-create the global receipt from the legacy copy.");
        Assert.False(
            File.Exists(GlobalLockPath(h)),
            "The command after an uninstall must not re-create the global lock from the legacy copy.");
    }

    /// <summary>
    /// Project state imported from the repository must not come back after the project is uninstalled. The in-repo
    /// copy is kept on purpose, so the next touch would import it again unless the import is recorded as done.
    /// </summary>
    [Fact]
    public void UninstallOfImportedProjectStateIsNotUndoneByTheNextTouch()
    {
        using Harness h = NewHarness("uninstall-imported-project");
        ScriptedAgentRenderer renderer = new("architect");
        Project alpha = InstallProject(h, "alpha", renderer);
        MoveProjectStateInRepo(h, alpha);
        new SquadStateMigration(h.Store, h.UserPaths).ImportProject(alpha.Root);
        Assert.True(File.Exists(alpha.ReceiptPath), "The import must restore the home project receipt before the uninstall.");

        AssertExit(Uninstall(h, renderer, alpha.Root, global: false), 0);
        Assert.False(File.Exists(alpha.ReceiptPath), "Project uninstall must remove the home project receipt.");
        Assert.True(
            File.Exists(Path.Combine(alpha.Root, ".kyber-weave", "squad.receipt.json")),
            "Project uninstall keeps the in-repo state, so the next touch has something to re-import.");

        CommandExecution touch = Status(h, alpha.Root, global: false);

        AssertExit(touch, 1);
        Assert.Contains("No Kyber-Squad deployment found", touch.Output, StringComparison.Ordinal);
        Assert.False(
            File.Exists(alpha.ReceiptPath),
            "The next touch must not re-import the in-repo state after the project was uninstalled.");
        Assert.DoesNotContain(
            h.Store.ListProjects(),
            entry => string.Equals(entry.Path, alpha.Physical, StringComparison.Ordinal));
    }

    /// <summary>
    /// After a migrated global uninstall, an explicit fresh install is not blocked by the migration record. It writes
    /// new home state, and the status that follows reads that state back.
    /// </summary>
    [Fact]
    public void FreshGlobalInstallAfterUninstallOfMigratedStateWritesNewHomeState()
    {
        using Harness h = NewHarness("fresh-install-after-uninstall");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("global-anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        MoveGlobalStateToLegacyRoot(h, anchor);
        AssertExit(Uninstall(h, renderer, anchor, global: true), 0);

        CommandExecution install = Capture(() => new SquadInstallCommand(
            userPaths: h.UserPaths,
            stateStore: h.Store,
            releaseSource: h.ReleaseSource,
            renderer: renderer,
            isInteractive: false,
            readAnswer: null,
            globalRoots: h.GlobalRoots)
            .Execute(null!, new SquadInstallSettings
            {
                Path = anchor,
                Global = true,
                Targets = ["codex"],
                Version = InstalledVersion,
                Yes = true
            }));

        AssertExit(install, 0);
        Assert.True(File.Exists(GlobalReceiptPath(h)), "A fresh global install must write new home global state.");
        CommandExecution status = Status(h, anchor, global: true);
        AssertExit(status, 0);
        Assert.Contains("All deployed files match", status.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards the isolation rule itself: every fake home and global root in this class must be
    /// beneath the temp directory, and the fake home refuses any other location outright, so a test
    /// that forgets its fixture cannot quietly reach the real profile.
    /// </summary>
    [Fact]
    public void EveryFakeHomeAndGlobalRootIsBeneathTheTempDirectory()
    {
        using Harness h = NewHarness("guard");
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

        Assert.StartsWith(temp, Path.GetFullPath(h.Home), StringComparison.Ordinal);
        Assert.StartsWith(temp, Path.GetFullPath(h.ApplicationData), StringComparison.Ordinal);
        Assert.StartsWith(temp, Path.GetFullPath(h.GlobalRootsDirectory), StringComparison.Ordinal);
        Assert.StartsWith(temp, Path.GetFullPath(h.GlobalRoot(SquadTarget.Codex)), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new HomeUserPaths(h.ApplicationData, "/Users/not-a-temp-home"));
    }

    #region Fixtures

    private Harness NewHarness(string name) => new(Path.Combine(_temp.Path, name));

    /// <summary>
    /// Builds the registry the global CLI tests run against: a global install, two live projects
    /// and one project whose root is then removed, so its registry entry is stale.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the harness transfers to the returned Fleet, which disposes it.")]
    private Fleet ArrangeFleet(string name)
    {
        Harness h = NewHarness(name);
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("global-anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        Project alpha = InstallProject(h, "alpha", renderer);
        Project beta = InstallProject(h, "beta", renderer);
        Project stale = InstallProject(h, "stale", renderer);
        Directory.Delete(stale.Root, recursive: true);
        return new Fleet(h, anchor, renderer, alpha, beta, stale);
    }

    private static void InstallGlobal(
        Harness h,
        string anchor,
        ISquadRenderer renderer,
        params SquadTarget[] targets)
    {
        SquadLifecycleResult result = RunInstall(h, renderer, new SquadInstallRequest(
            TargetRoot: anchor,
            Scope: SquadDeploymentScope.Global,
            Targets: targets,
            Version: InstalledVersion));
        Assert.True(result.Success, DescribeFailure(result));
    }

    private static Project InstallProject(Harness h, string name, ISquadRenderer renderer)
    {
        string root = h.MakeDirectory(name);
        SquadLifecycleResult result = RunInstall(h, renderer, new SquadInstallRequest(
            TargetRoot: root,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: InstalledVersion));
        Assert.True(result.Success, DescribeFailure(result));
        string physical = SquadPhysicalRootIdentity.Resolve(root).PhysicalPath;
        string key = SquadPhysicalRootIdentity.Resolve(root).Key;
        string stateDirectory = Path.Combine(h.Home, ".kyber-weave", "squad", "projects", key);
        return new Project(root, physical, stateDirectory);
    }

    private static SquadLifecycleResult RunInstall(Harness h, ISquadRenderer renderer, SquadInstallRequest request) =>
        h.Lifecycle(renderer).InstallAsync(request).GetAwaiter().GetResult();

    /// <summary>Builds the update command over the injected seams, so no run reaches a real harness directory.</summary>
    private static SquadUpdateCommand UpdateCommand(
        Harness h,
        ISquadRenderer renderer,
        ISquadProjectPicker picker,
        bool isInteractive,
        Func<string, bool>? readAnswer = null,
        ISquadReleaseSource? releaseSource = null) =>
        new(
            userPaths: h.UserPaths,
            stateStore: h.Store,
            releaseSource: releaseSource ?? h.ReleaseSource,
            renderer: renderer,
            isInteractive: isInteractive,
            readAnswer: readAnswer ?? (_ => true),
            globalRoots: h.GlobalRoots,
            picker: picker);

    private static SquadUpdateSettings GlobalUpdate(
        string path,
        string[]? projects = null,
        string[]? targets = null,
        bool replaceManaged = false,
        bool yes = true) =>
        new()
        {
            Path = path,
            Global = true,
            Version = UpdatedVersion,
            Yes = yes,
            Projects = projects,
            Targets = targets ?? [],
            ReplaceManaged = replaceManaged
        };

    private static void WriteLegacyGlobalRoot(Harness h, string name, string relativeFile, string content)
    {
        string directory = Path.Combine(h.ApplicationData, "KyberWeave", "squad", "roots", name);
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        JsonObject receipt = new()
        {
            ["schema"] = "kyber-squad.receipt/v1",
            ["scope"] = "global",
            ["targetRoot"] = ".",
            ["installedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["degradations"] = new JsonArray(),
            ["files"] = new JsonArray(new JsonObject
            {
                ["relativePath"] = relativeFile,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                ["target"] = "codex",
                ["adopted"] = false
            })
        };
        File.WriteAllText(Path.Combine(directory, "squad.receipt.json"), receipt.ToJsonString() + "\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "squad.lock.yml"), h.Store.SerializeLock(LockFor(InstalledVersion)), Encoding.UTF8);
        string legacyFile = Path.Combine(directory, relativeFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
        File.WriteAllBytes(legacyFile, bytes);
    }

    /// <summary>
    /// The pre-home layout: a receipt and lock under <c>.kyber-weave/</c> inside the project. The
    /// migration imports it into the registry and leaves it byte-identical, so doctor must say it is kept.
    /// </summary>
    private static void WriteInRepoLegacyState(Harness h, string root)
    {
        string stateDirectory = Path.Combine(root, ".kyber-weave");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(
            Path.Combine(stateDirectory, "squad.receipt.json"),
            h.Store.SerializeReceipt(new SquadReceipt(
                Schema: "kyber-squad.receipt/v1",
                Scope: SquadDeploymentScope.Project,
                TargetRoot: ".",
                InstalledAtUtc: DateTimeOffset.UtcNow,
                Degradations: [],
                Files: [])),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(stateDirectory, "squad.lock.yml"),
            h.Store.SerializeLock(LockFor(InstalledVersion)),
            Encoding.UTF8);
    }

    /// <summary>
    /// Moves the home global receipt and lock into the legacy per-user root, the layout a pre-home install left
    /// behind, so the next migration has a legacy copy to copy. The installed files stay where they were.
    /// </summary>
    private static void MoveGlobalStateToLegacyRoot(Harness h, string anchor)
    {
        string legacyDirectory = Path.Combine(
            h.ApplicationData,
            "KyberWeave",
            "squad",
            "roots",
            SquadPhysicalRootIdentity.Resolve(anchor).Key);
        Directory.CreateDirectory(legacyDirectory);
        File.Move(GlobalReceiptPath(h), Path.Combine(legacyDirectory, "squad.receipt.json"));
        File.Move(GlobalLockPath(h), Path.Combine(legacyDirectory, "squad.lock.yml"));
        Directory.Delete(GlobalStateDirectory(h));
    }

    /// <summary>
    /// Moves a project's home state into the repository, the layout a pre-home install left behind, so the next
    /// touch imports it. The project's installed files stay where they were.
    /// </summary>
    private static void MoveProjectStateInRepo(Harness h, Project project)
    {
        string inRepo = Path.Combine(project.Root, ".kyber-weave");
        Directory.CreateDirectory(inRepo);
        File.Move(project.ReceiptPath, Path.Combine(inRepo, "squad.receipt.json"));
        File.Move(project.LockPath, Path.Combine(inRepo, "squad.lock.yml"));
        Directory.Delete(project.StateDirectory);
    }

    private static CommandExecution Uninstall(Harness h, ISquadRenderer renderer, string path, bool global) =>
        Capture(() => new SquadUninstallCommand(
            userPaths: h.UserPaths,
            stateStore: h.Store,
            lifecycleService: h.Lifecycle(renderer),
            isInteractive: false,
            readAnswer: null,
            globalRoots: h.GlobalRoots)
            .Execute(null!, new SquadUninstallSettings { Path = path, Global = global, Yes = true }));

    private static CommandExecution Status(Harness h, string path, bool global) =>
        Capture(() => new SquadStatusCommand(
            userPaths: h.UserPaths,
            stateStore: h.Store,
            globalRoots: h.GlobalRoots)
            .Execute(null!, new SquadStatusSettings { Path = path, Global = global }));

    private static SquadLock LockFor(string version) => new(
        Schema: "kyber-squad.lock/v1",
        SquadVersion: version,
        CliVersion: version,
        McpVersion: version,
        Bundle: "full",
        Targets: ["codex"],
        Exclusions: [],
        Translation: "best-effort",
        BundleDigest: "a".PadRight(64, '0'),
        AssetDigest: "b".PadRight(64, '0'),
        Apm: new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')));

    private static string GlobalStateDirectory(Harness h) =>
        Path.Combine(h.Home, ".kyber-weave", "squad", "global");

    private static string GlobalLockPath(Harness h) =>
        Path.Combine(GlobalStateDirectory(h), "squad.lock.yml");

    private static string GlobalReceiptPath(Harness h) =>
        Path.Combine(GlobalStateDirectory(h), "squad.receipt.json");

    private static string RegistryPath(Harness h) =>
        Path.Combine(h.Home, ".kyber-weave", "squad", "registry.json");

    private static string FindInstalled(Harness h, SquadTarget target, string fileName)
    {
        string root = h.GlobalRoot(target);
        string[] matches = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).ToArray();
        Assert.True(matches.Length == 1, $"Expected one installed '{fileName}' beneath {root}; found {matches.Length}.");
        return matches[0];
    }

    /// <summary>Reads the <c>squad-version</c> scalar from a lock file, without assuming how the store is wired.</summary>
    private static string VersionOf(string lockPath)
    {
        string prefix = "squad-version: ";
        string text = File.ReadAllText(RequireFile(lockPath), Encoding.UTF8);
        string? line = text.Split('\n').FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        Assert.NotNull(line);
        return line[prefix.Length..].Trim().Trim('"');
    }

    /// <summary>Reads state before a run, or null when it is absent, so the run's own assertions go first.</summary>
    private static byte[]? Snapshot(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    /// <summary>Asserts a file keeps the bytes it had before the run, and that it still exists.</summary>
    private static void AssertUnchanged(byte[]? before, string path)
    {
        Assert.True(before is not null, $"Expected state at '{path}' before the run. Squad state must be written beneath the home store.");
        Assert.True(File.Exists(path), $"State at '{path}' disappeared during the run.");
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private static string RequireFile(string path)
    {
        Assert.True(
            File.Exists(path),
            $"Expected state at '{path}'. Squad state for this scenario must be written beneath the home store.");
        return path;
    }

    private static string LineContaining(string output, string text)
    {
        string[] lines = output
            .Split('\n')
            .Where(line => line.Contains(text, StringComparison.Ordinal))
            .ToArray();
        Assert.True(lines.Length == 1, $"Expected exactly one line naming '{text}', found {lines.Length}:{Environment.NewLine}{output}");
        return lines[0];
    }

    private static string DescribeFailure(SquadLifecycleResult result) =>
        result.Errors is null ? "The lifecycle reported failure." : string.Join("; ", result.Errors);

    private static void AssertExit(CommandExecution execution, int expected) =>
        Assert.True(
            execution.ExitCode == expected,
            $"Expected exit {expected} but got {execution.ExitCode}:{Environment.NewLine}{execution.Output}");

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private static IReadOnlyList<string> NoProjects(IReadOnlyList<SquadProjectRegistryEntry> projects) => [];

    private static IReadOnlyList<string> AllNonStale(IReadOnlyList<SquadProjectRegistryEntry> projects) =>
        projects.Where(entry => !entry.Stale).Select(entry => entry.Path).ToList();

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed record Project(string Root, string Physical, string StateDirectory)
    {
        public string LockPath => Path.Combine(StateDirectory, "squad.lock.yml");

        public string ReceiptPath => Path.Combine(StateDirectory, "squad.receipt.json");
    }

    private sealed record Fleet(
        Harness Harness,
        string Anchor,
        ScriptedAgentRenderer Renderer,
        Project Alpha,
        Project Beta,
        Project Stale) : IDisposable
    {
        public SquadUpdateCommand Command(
            ISquadProjectPicker picker,
            bool isInteractive,
            Func<string, bool> readAnswer,
            ISquadReleaseSource? releaseSource = null) =>
            UpdateCommand(Harness, Renderer, picker, isInteractive, readAnswer, releaseSource);

        public void Dispose() => Harness.Dispose();
    }

    private sealed class Harness : IDisposable
    {
        public Harness(string root)
        {
            Root = root;
            ApplicationData = Path.Combine(root, "app-data");
            Home = Path.Combine(root, "home");
            GlobalRootsDirectory = Path.Combine(root, "global-roots");
            Directory.CreateDirectory(ApplicationData);
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(GlobalRootsDirectory);
            UserPaths = new HomeUserPaths(ApplicationData, Home);
            Store = new SquadStateStore(UserPaths);
            ReleaseSource = new FakeSquadReleaseSource();
            GlobalRoots = new TempGlobalRoots(GlobalRootsDirectory);
        }

        public string Root { get; }

        public string Home { get; }

        public string ApplicationData { get; }

        public string GlobalRootsDirectory { get; }

        public HomeUserPaths UserPaths { get; }

        public SquadStateStore Store { get; }

        public FakeSquadReleaseSource ReleaseSource { get; }

        public TempGlobalRoots GlobalRoots { get; }

        public string MakeDirectory(string name)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string GlobalRoot(SquadTarget target) => GlobalRoots.ResolveGlobalRoot(target);

        public SquadLifecycleService Lifecycle(ISquadRenderer renderer) =>
            new(ReleaseSource, renderer, Store, globalRoots: GlobalRoots);

        /// <summary>Releases the fake release source; the temp root itself is removed by the test class.</summary>
        public void Dispose() => ReleaseSource.Dispose();
    }

    private sealed class HomeUserPaths : ISquadUserPaths
    {
        public HomeUserPaths(string applicationDataDirectory, string homeDirectory)
        {
            RequireUnderTemp(applicationDataDirectory);
            RequireUnderTemp(homeDirectory);
            ApplicationDataDirectory = applicationDataDirectory;
            HomeDirectory = homeDirectory;
        }

        public string ApplicationDataDirectory { get; }

        public string HomeDirectory { get; }
    }

    private sealed class TempGlobalRoots(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) =>
            Path.Combine(root, SquadTargetCatalog.GetToken(target));
    }

    /// <summary>
    /// Renders one agent file per requested target at a path the test controls, so a receipt and a
    /// collision can be planned against exact bytes instead of the fake renderer's harness layout.
    /// </summary>
    private sealed class ScriptedAgentRenderer(params string[] agentNames) : ISquadRenderer
    {
        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = SquadTargetCatalog.All;

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            List<SquadDeploymentFile> files = [];
            foreach (SquadTarget target in request.Targets)
            {
                foreach (string agent in agentNames)
                {
                    files.Add(new SquadDeploymentFile(
                        $"agents/{agent}.md",
                        Encoding.UTF8.GetBytes($"canonical {agent}\n"),
                        SquadTargetCatalog.GetToken(target)));
                }
            }

            return Task.FromResult(new SquadRenderResult(
                Success: true,
                Files: files,
                Degradations: [],
                Warnings: [],
                Errors: []));
        }
    }

    private sealed class RecordingProjectPicker(
        Func<IReadOnlyList<SquadProjectRegistryEntry>, IReadOnlyList<string>> choose) : ISquadProjectPicker
    {
        private readonly List<IReadOnlyList<SquadProjectRegistryEntry>> _calls = [];

        public IReadOnlyList<IReadOnlyList<SquadProjectRegistryEntry>> Calls => _calls;

        public IReadOnlyList<string> Pick(IReadOnlyList<SquadProjectRegistryEntry> projects)
        {
            _calls.Add(projects);
            return choose(projects);
        }
    }

    private sealed class PromptRecorder
    {
        public List<string> Questions { get; } = [];

        public bool Answer(string question)
        {
            Questions.Add(question);
            return true;
        }
    }

    private sealed class FailingReleaseSource : ISquadReleaseSource
    {
        public Task<SquadReleaseResult> DownloadAndExtractAsync(
            SquadReleaseRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<SquadReleaseResult>(new InvalidOperationException("The Squad release is unavailable."));

        public void Dispose()
        {
        }
    }

    private static void RequireUnderTemp(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Squad CLI tests must keep every fake home and state root beneath the temp directory; got '{full}'.");
        }
    }

    #endregion
}
