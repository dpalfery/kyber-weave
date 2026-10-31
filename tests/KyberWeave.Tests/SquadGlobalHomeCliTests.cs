using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Harness = KyberWeave.Tests.Fakes.SquadHomeHarness;
using HomeUserPaths = KyberWeave.Tests.Fakes.SquadHomeHarness.HomeUserPaths;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins the CLI contract for home-level Squad state: a <c>--global</c> run finds its one receipt
///     whatever directory it is launched from, offers the registered projects through the picker,
///     honours <c>--projects</c> without prompting, and keeps project, registry and global state
///     consistent when one of them fails.
/// </summary>
/// <remarks>
///     Every test runs against a temp home, a temp application data directory, and temp global roots
///     supplied through the internal command seams. No test reads or writes the real home, the real
///     application data directory, or a harness directory. Project state is seeded through the same
///     lifecycle the CLI uses, so the registry is whatever the install path registers; a hand-written
///     registry file would pin a format this contract does not yet fix.
/// </remarks>
public sealed class SquadGlobalHomeCliTests : IDisposable
{
    private const string InstalledVersion = "1.2.3";
    private const string UpdatedVersion = "1.2.4";

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void UpdateGlobalFromTwoUnrelatedDirectoriesFindsTheSameGlobalReceipt()
    {
        using Harness h = NewHarness("unrelated-directories");
        ScriptedAgentRenderer renderer = new("architect");
        string first = h.MakeDirectory("unrelated-first");
        string second = h.MakeDirectory("unrelated-second");
        InstallGlobal(h, first, renderer, SquadTarget.Codex);
        SquadUpdateCommand command = UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false);

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
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false)
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
            fleet.Command(picker, true, prompts.Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

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
            fleet.Command(picker, true, new PromptRecorder().Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

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
            fleet.Command(picker, true, new PromptRecorder().Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 0);
        Assert.Single(picker.Calls);
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    /// <summary>
    ///     <c>None</c> means nothing, so it cannot sit beside another choice. The interactive picker
    ///     re-prompts on that combination; the flag form has no prompt to re-ask, so it must be refused
    ///     as client input before any write. The re-prompt itself has no seam yet (see the report).
    /// </summary>
    [Fact]
    public void UpdateGlobalNoneCombinedWithAnotherProjectChoiceIsRejectedWithoutWrites()
    {
        using Fleet fleet = ArrangeFleet("none-combined");
        byte[]? globalLockBefore = Snapshot(GlobalLockPath(fleet.Harness));
        byte[]? alphaLockBefore = Snapshot(fleet.Alpha.LockPath);
        RecordingProjectPicker picker = new(AllNonStale);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, false, new PromptRecorder().Answer).Execute(
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
            fleet.Command(picker, false, new PromptRecorder().Answer)
                .Execute(null!, GlobalUpdate(fleet.Anchor, yes: false)));

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
            fleet.Command(picker, true, prompts.Answer).Execute(null!, GlobalUpdate(fleet.Anchor, ["all"])));

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
            fleet.Command(picker, true, prompts.Answer).Execute(null!, GlobalUpdate(fleet.Anchor, ["none"])));

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
            fleet.Command(picker, true, prompts.Answer).Execute(null!, GlobalUpdate(fleet.Anchor, [fleet.Alpha.Root])));

        AssertExit(execution, 0);
        Assert.Empty(picker.Calls);
        Assert.Empty(prompts.Questions);
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    /// <summary>
    ///     A named path that is stale is warned about and skipped, not updated and not treated as a
    ///     failure: the operator named it, so silence would hide that nothing happened to it.
    /// </summary>
    [Fact]
    public void UpdateGlobalProjectsNamingAStaleProjectWarnsAndSkipsIt()
    {
        using Fleet fleet = ArrangeFleet("projects-stale-named");
        byte[]? staleLockBefore = Snapshot(fleet.Stale.LockPath);

        CommandExecution execution = Capture(() =>
            fleet.Command(new RecordingProjectPicker(NoProjects), false, new PromptRecorder().Answer)
                .Execute(null!, GlobalUpdate(fleet.Anchor, [fleet.Stale.Root])));

        AssertExit(execution, 0);
        Assert.Contains("skipping stale project", execution.Output, StringComparison.Ordinal);
        AssertUnchanged(staleLockBefore, fleet.Stale.LockPath);
    }

    /// <summary>
    ///     A dry run plans and never writes. A global dry run that names an unregistered project with
    ///     in-repo state must not import it: the home project state and the registry stay as they were, the
    ///     in-repo files stay byte-identical, and the project is reported as skipped, so the exit stays 0.
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
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false)
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
    ///     An unknown path is refused before any global work starts, and the hint names the nearest
    ///     registered project so a typo is one copy-paste away from being fixed.
    /// </summary>
    [Fact]
    public void UpdateGlobalUnknownProjectsPathExitsTwoWithNearestHintAndNoWrites()
    {
        using Fleet fleet = ArrangeFleet("unknown-path");
        byte[]? globalLockBefore = Snapshot(GlobalLockPath(fleet.Harness));
        byte[]? alphaLockBefore = Snapshot(fleet.Alpha.LockPath);
        string typo = Path.Combine(fleet.Harness.Root, "alpah");

        CommandExecution execution = Capture(() =>
            fleet.Command(new RecordingProjectPicker(NoProjects), false, new PromptRecorder().Answer)
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
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false)
                .Execute(null!,
                    new SquadUpdateSettings
                    { Path = alpha.Root, Global = false, Version = UpdatedVersion, Projects = ["all"] }));

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
            fleet.Command(picker, true, new PromptRecorder().Answer, failing)
                .Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 1);
        Assert.Empty(picker.Calls);
        Assert.Equal(InstalledVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    /// <summary>
    ///     One project failing must not stop the rest, and the operator must be told which one failed:
    ///     the run ends with a summary and exit 1 rather than a silent partial success.
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
            fleet.Command(picker, true, new PromptRecorder().Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 1);
        Assert.Contains(fleet.Alpha.Physical, execution.Output, StringComparison.Ordinal);
        Assert.Contains(fleet.Beta.Physical, execution.Output, StringComparison.Ordinal);
        Assert.Contains("failed", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    /// <summary>
    ///     The global form runs from an unrelated directory, so the receipt is found by the home anchor
    ///     rather than by the directory the operator happened to be in.
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
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false)
                .Execute(null!,
                    GlobalUpdate(h.MakeDirectory("unrelated-second-run"), targets: ["all"], replaceManaged: true)));

        AssertExit(execution, 0);
        Assert.NotEqual(edited, File.ReadAllBytes(architect));
        AssertUnchanged(canonical, architect);
    }

    /// <summary>
    ///     The same run that would overwrite the owned file must not overwrite anything once it finds a
    ///     new path held by an unowned hand-authored file: the collision is planned before any write, so
    ///     the owned file keeps the operator's edit, the receipt and lock keep their bytes, and the
    ///     unowned file is untouched.
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
            UpdateCommand(h, updateRenderer, new RecordingProjectPicker(NoProjects), false)
                .Execute(null!,
                    GlobalUpdate(h.MakeDirectory("unrelated-second-run"), targets: ["all"], replaceManaged: true)));

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
        SquadStatusCommand status = new(fleet.Harness.UserPaths, fleet.Harness.Store, fleet.Harness.GlobalRoots);

        CommandExecution execution = Capture(() =>
            status.Execute(null!, new SquadStatusSettings { Path = fleet.Anchor, Global = true }));

        string alphaLine = LineContaining(execution.Output, fleet.Alpha.Physical);
        string staleLine = LineContaining(execution.Output, fleet.Stale.Physical);
        Assert.DoesNotContain("stale", alphaLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(stale:", alphaLine, StringComparison.Ordinal);
        Assert.Contains("(stale:", staleLine, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A registry the status command cannot parse is named and fails the run, the way doctor reports it, rather than
    ///     crashing or listing nothing and succeeding. The status read is not a repair, so the corrupt file is left as found.
    /// </summary>
    [Fact]
    public void StatusGlobalNamesAnUnreadableRegistryAndFailsWithoutRewritingIt()
    {
        using Harness h = NewHarness("status-global-corrupt-registry");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("global-anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        const string corrupt = "{ this registry is corrupt \n";
        File.WriteAllText(RegistryPath(h), corrupt, new UTF8Encoding(false));

        CommandExecution status = Status(h, anchor, true);

        AssertExit(status, 1);
        Assert.Contains("registry is unreadable", status.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(corrupt, File.ReadAllText(RegistryPath(h), Encoding.UTF8));
    }

    /// <summary>
    ///     Doctor is the one place an operator sees the state the migration and registry left behind,
    ///     so each category must be named: a stale registry entry, a legacy global root the migration
    ///     could not move, and in-repo legacy state that was imported but kept.
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
                    new FakeProcessExecutor(),
                    fleet.Harness.UserPaths,
                    fleet.Anchor,
                    fleet.Harness.GlobalRoots,
                    renderer,
                    fleet.Harness.Store)
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
    ///     Global uninstall removes machine-wide files and nothing else. A project that still depends on
    ///     the registry, or a registry the operator can see, must keep the same bytes.
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
            fleet.Harness.UserPaths,
            fleet.Harness.Store,
            fleet.Harness.Lifecycle(fleet.Renderer),
            false,
            null,
            fleet.Harness.GlobalRoots);

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
    ///     A migrated global deployment that was uninstalled must stay uninstalled. The migration keeps the legacy copy
    ///     for an older CLI, so the command that runs after the uninstall must not copy it back into the home.
    /// </summary>
    [Fact]
    public void UninstallOfMigratedGlobalStateIsNotUndoneByTheNextCommand()
    {
        using Harness h = NewHarness("uninstall-migrated-global");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("global-anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        MoveGlobalStateToLegacyRoot(h, anchor);

        AssertExit(Uninstall(h, renderer, anchor, true), 0);
        Assert.False(File.Exists(GlobalReceiptPath(h)), "Global uninstall must remove the global receipt.");

        CommandExecution status = Status(h, anchor, true);

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
    ///     Project state imported from the repository must not come back after the project is uninstalled. The in-repo
    ///     copy is kept on purpose, so the next touch would import it again unless the import is recorded as done.
    /// </summary>
    [Fact]
    public void UninstallOfImportedProjectStateIsNotUndoneByTheNextTouch()
    {
        using Harness h = NewHarness("uninstall-imported-project");
        ScriptedAgentRenderer renderer = new("architect");
        Project alpha = InstallProject(h, "alpha", renderer);
        MoveProjectStateInRepo(h, alpha);
        new SquadStateMigration(h.Store, h.UserPaths).ImportProject(alpha.Root);
        Assert.True(File.Exists(alpha.ReceiptPath),
            "The import must restore the home project receipt before the uninstall.");

        AssertExit(Uninstall(h, renderer, alpha.Root, false), 0);
        Assert.False(File.Exists(alpha.ReceiptPath), "Project uninstall must remove the home project receipt.");
        Assert.True(
            File.Exists(Path.Combine(alpha.Root, ".kyber-weave", "squad.receipt.json")),
            "Project uninstall keeps the in-repo state, so the next touch has something to re-import.");

        CommandExecution touch = Status(h, alpha.Root, false);

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
    ///     After a migrated global uninstall, an explicit fresh install is not blocked by the migration record. It writes
    ///     new home state, and the status that follows reads that state back.
    /// </summary>
    [Fact]
    public void FreshGlobalInstallAfterUninstallOfMigratedStateWritesNewHomeState()
    {
        using Harness h = NewHarness("fresh-install-after-uninstall");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("global-anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        MoveGlobalStateToLegacyRoot(h, anchor);
        AssertExit(Uninstall(h, renderer, anchor, true), 0);

        CommandExecution install = Capture(() => new SquadInstallCommand(
                h.UserPaths,
                h.Store,
                h.ReleaseSource,
                renderer,
                false,
                null,
                h.GlobalRoots)
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
        CommandExecution status = Status(h, anchor, true);
        AssertExit(status, 0);
        Assert.Contains("All deployed files match", status.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Guards the isolation rule itself: every fake home and global root in this class must be
    ///     beneath the temp directory, and the fake home refuses any other location outright, so a test
    ///     that forgets its fixture cannot quietly reach the real profile.
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

    #region Response guidance

    /// <summary>
    ///     The response guidance is the one file a <c>--global</c> run deploys into the Kyber-Weave home
    ///     rather than into a harness directory, so an operator changes the shape of every Squad response
    ///     from one place that no harness update can overwrite.
    /// </summary>
    [Fact]
    public void UpdateGlobalDeploysTheDefaultResponseGuidanceIntoTheKyberWeaveHome()
    {
        using Harness h = NewHarness("guidance-deploy");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        ClearResponseGuidanceToAbsentAndUnowned(h);

        // Pinned to this harness's own home rather than to the temp directory it happens to sit under:
        // every fake home in this suite is beneath the temp directory, so a prefix check would pass for
        // a guidance file deployed beside the wrong home. The path is built here instead of through
        // GuidancePath, so the test fails if that helper and the deployer ever agree on the wrong
        // location. The file must be absent first, or its presence afterwards would say nothing about
        // where the update wrote. Install now creates the file, so the precondition is set explicitly.
        string expected = Path.GetFullPath(Path.Combine(h.Home, ".kyber-weave", "response-guidance.md"));
        Assert.False(
            File.Exists(expected),
            $"A fresh global install must not deploy the response guidance to '{expected}'; only an update does.");

        CommandExecution update = Capture(() =>
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false)
                .Execute(null!, GlobalUpdate(anchor)));

        AssertExit(update, 0);
        Assert.True(
            File.Exists(expected),
            $"squad update --global must deploy the default response guidance to exactly '{expected}'.");
        Assert.False(
            string.IsNullOrWhiteSpace(File.ReadAllText(expected)),
            $"'{expected}' must carry the default response guidance, not an empty file.");
    }

    /// <summary>
    ///     A deployed file Squad does not own is a file the next run may overwrite by accident. Recording
    ///     the guidance in the global receipt with its digest is what makes "the user edited this" a
    ///     question Squad can answer before it writes.
    /// </summary>
    [Fact]
    public void UpdateGlobalRecordsTheDeployedResponseGuidanceInTheGlobalReceipt()
    {
        using Harness h = NewHarness("guidance-receipt");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        AssertExit(
            Capture(() => UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false)
                .Execute(null!, GlobalUpdate(anchor))),
            0);
        string guidancePath = GuidancePath(h);
        Assert.True(
            File.Exists(guidancePath),
            $"squad update --global must deploy the default response guidance to '{guidancePath}' " +
            "before the run can record that it owns it.");
        string digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(guidancePath)));
        string receipt = File.ReadAllText(RequireFile(GlobalReceiptPath(h)));

        Assert.True(
            receipt.Contains("response-guidance.md", StringComparison.Ordinal),
            "The global receipt must record the deployed response guidance, so a later run knows it owns that file.");
        Assert.True(
            receipt.Contains(digest, StringComparison.OrdinalIgnoreCase),
            "The global receipt must record the SHA-256 of the deployed response guidance, so a user edit is " +
            $"detected instead of overwritten. Expected digest: {digest}.");
    }

    /// <summary>
    ///     An unmodified, digest-matching file is Squad's own copy, so a run that ships newer default
    ///     guidance replaces it. Preserving it would leave the operator on wording the release replaced.
    /// </summary>
    [Fact]
    public void UpdateGlobalReplacesDigestMatchingResponseGuidanceWithTheCurrentDefault()
    {
        using Harness h = NewHarness("guidance-refresh");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        SquadUpdateCommand command = UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false);
        AssertExit(Capture(() => command.Execute(null!, GlobalUpdate(anchor))), 0);
        string path = GuidancePath(h);
        Assert.True(
            File.Exists(path),
            $"squad update --global must deploy the default response guidance to '{path}'.");
        byte[] current = File.ReadAllBytes(path);
        byte[] superseded = Encoding.UTF8.GetBytes(
            "Response guidance from an earlier Kyber-Weave release, which the current release supersedes.\n");

        // The state records the superseded bytes as what it deployed, and nobody has edited them since:
        // exactly the file an update is allowed to replace.
        RewriteRecordedDigest(h, current, superseded);
        File.WriteAllBytes(path, superseded);

        CommandExecution second = Capture(() => command.Execute(null!, GlobalUpdate(anchor)));

        AssertExit(second, 0);
        Assert.True(
            current.AsSpan().SequenceEqual(File.ReadAllBytes(path)),
            "squad update --global must replace an unmodified, digest-matching " +
            $"'response-guidance.md' at '{path}' with the current default.");
    }

    /// <summary>
    ///     The operator's own wording wins. A run that silently reinstated the shipped default would
    ///     discard the one customization the file exists to hold, so the edit survives and the run says so.
    /// </summary>
    [Fact]
    public void UpdateGlobalPreservesAndReportsAUserModifiedResponseGuidance()
    {
        using Harness h = NewHarness("guidance-user-edit");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        SquadUpdateCommand command = UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false);
        AssertExit(Capture(() => command.Execute(null!, GlobalUpdate(anchor))), 0);
        string path = GuidancePath(h);
        Assert.True(
            File.Exists(path),
            $"squad update --global must deploy the default response guidance to '{path}'.");
        string userWording = "Keep every answer to three sentences and never open with a heading.\n";
        File.WriteAllText(path, userWording, Encoding.UTF8);

        CommandExecution second = Capture(() => command.Execute(null!, GlobalUpdate(anchor)));

        AssertExit(second, 0);
        Assert.Equal(userWording, File.ReadAllText(path, Encoding.UTF8));
        Assert.Contains("response-guidance.md", second.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The mirror image of the ordering guarantee above: a run that wrote the default and then failed
    ///     leaves the shipped bytes on disk under a receipt that describes a file it never delivered. The
    ///     next run must adopt those bytes — record, no write, no warning — rather than read its own
    ///     default back as an operator edit and preserve it forever under a digest nobody can correct by
    ///     hand.
    /// </summary>
    [Fact]
    public void UpdateGlobalAdoptsShippedGuidanceWhoseRecordedDigestNoLongerMatches()
    {
        using Harness h = NewHarness("guidance-adopt");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        SquadUpdateCommand command = UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false);
        AssertExit(Capture(() => command.Execute(null!, GlobalUpdate(anchor))), 0);
        string path = GuidancePath(h);
        Assert.True(
            File.Exists(path),
            $"squad update --global must deploy the default response guidance to '{path}'.");
        byte[] shipped = File.ReadAllBytes(path);
        string shippedDigest = Convert.ToHexStringLower(SHA256.HashData(shipped));

        // The receipt is rewritten to describe a different file; the deployed bytes are never touched, so
        // the disk holds the shipped default while the record denies it. This is the state a run leaves
        // behind when it deploys the guidance and then fails before writing the receipt.
        RewriteRecordedDigest(h, shipped, Encoding.UTF8.GetBytes("An earlier Kyber-Weave release's default.\n"));
        Assert.False(
            File.ReadAllText(RequireFile(GlobalReceiptPath(h)))
                .Contains(shippedDigest, StringComparison.OrdinalIgnoreCase),
            "The scenario requires a receipt that no longer records the digest of the bytes on disk.");

        // Rewriting identical bytes would still satisfy a byte comparison, so the last-write time is
        // pinned to a moment no write can share: adopting has to leave the file exactly there.
        DateTime pinned = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, pinned);
        Assert.True(
            File.GetLastWriteTimeUtc(path) == pinned,
            "The scenario requires the file system to keep the pinned last-write time.");

        CommandExecution second = Capture(() => command.Execute(null!, GlobalUpdate(anchor)));

        AssertExit(second, 0);
        Assert.True(
            shipped.AsSpan().SequenceEqual(File.ReadAllBytes(path)),
            $"'{path}' already holds the shipped default, so the run must adopt those bytes rather than " +
            "rewrite them.");
        Assert.True(
            File.GetLastWriteTimeUtc(path) == pinned,
            $"Adopting '{path}' must not write it: identical bytes written again still move the " +
            "last-write time, and that is the only trace a rewrite leaves.");
        Assert.DoesNotContain("Kept the edited", second.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Kept the response guidance", second.Output, StringComparison.Ordinal);
        string receipt = File.ReadAllText(RequireFile(GlobalReceiptPath(h)));
        Assert.True(
            receipt.Contains(shippedDigest, StringComparison.OrdinalIgnoreCase),
            "A run that finds the shipped default already on disk must record its digest, repairing a " +
            "receipt that describes a file the earlier run never delivered. Expected digest: " +
            $"{shippedDigest}.");

        // Negative control, so the assertions above cannot pass vacuously: change only the file's bytes,
        // leaving the receipt alone, and the very same run must now refuse and say so. Without this, a
        // deployer that preserved every file — or that never re-examined the receipt — would satisfy the
        // adopt case and this one equally.
        File.WriteAllText(path, "Edited by the operator after the adopt.\n", Encoding.UTF8);

        CommandExecution third = Capture(() => command.Execute(null!, GlobalUpdate(anchor)));

        AssertExit(third, 0);
        Assert.Equal("Edited by the operator after the adopt.\n", File.ReadAllText(path, Encoding.UTF8));
        Assert.Contains("Kept the edited", third.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A receipt may only record bytes that are on disk. A write that fails must therefore leave no
    ///     ownership record behind: a digest recorded ahead of its write describes a file the run never
    ///     wrote, and the next run reads the operator's untouched file as an edit — preserved forever, and
    ///     reported as an edit they never made.
    /// </summary>
    [Fact]
    public void UpdateGlobalThatCannotWriteTheResponseGuidanceRecordsNoOwnershipAndRetriesCleanly()
    {
        using Harness h = NewHarness("guidance-write-failure");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        ClearResponseGuidanceToAbsentAndUnowned(h);
        string path = GuidancePath(h);

        // A directory at the destination makes the write fail the way a permission error or a full
        // disk does — through the same File.Move the deployer uses — without a seam in production code.
        Directory.CreateDirectory(path);

        CommandExecution failed = Capture(() =>
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false)
                .Execute(null!, GlobalUpdate(anchor)));

        AssertExit(failed, 0);
        Assert.Contains("response-guidance.md", failed.Output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path), $"The failed write must not remove '{path}'.");
        string failedReceipt = File.ReadAllText(RequireFile(GlobalReceiptPath(h)));
        Assert.False(
            failedReceipt.Contains("response-guidance.md", StringComparison.Ordinal),
            "A run that could not write the guidance must record no ownership of it: a digest recorded " +
            "ahead of its write makes the next run read the operator's untouched file as an edit.");

        Directory.Delete(path, true);

        CommandExecution recovered = Capture(() =>
            UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false)
                .Execute(null!, GlobalUpdate(anchor)));

        AssertExit(recovered, 0);
        Assert.True(
            File.Exists(path),
            $"squad update --global must deploy the default response guidance to '{path}' on retry.");
        string digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        // The retry wrote the default, so nothing was preserved and nothing may claim an operator edit.
        Assert.DoesNotContain("Kept the edited", recovered.Output, StringComparison.Ordinal);
        Assert.True(
            File.ReadAllText(RequireFile(GlobalReceiptPath(h))).Contains(digest, StringComparison.OrdinalIgnoreCase),
            "A successful retry must record the digest it deployed, so the next run can tell its own " +
            $"file from an operator edit. Expected digest: {digest}.");
    }

    /// <summary>
    ///     Preflight is what decides whether a run is allowed to write, so it must decide before the home
    ///     guidance is touched. A run that fails on a conflicting path and has already written the guidance
    ///     would leave behind a change nothing accounted for.
    /// </summary>
    [Fact]
    public void UpdateGlobalWhosePlanFailsPreflightLeavesAnAbsentResponseGuidanceAbsent()
    {
        using Harness h = NewHarness("guidance-blocked-preflight-absent");
        ScriptedAgentRenderer installRenderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, installRenderer, SquadTarget.Codex);
        ClearResponseGuidanceToAbsentAndUnowned(h);

        // An unowned hand-authored file at a path the update would claim, so plan construction throws
        // before any write — the same portable mechanism the collision tests use.
        string architect = FindInstalled(h, SquadTarget.Codex, "architect.md");
        File.WriteAllBytes(
            Path.Combine(Path.GetDirectoryName(architect)!, "collider.md"),
            Encoding.UTF8.GetBytes("hand-authored collider\n"));
        string guidance = GuidancePath(h);
        Assert.False(File.Exists(guidance), $"The scenario requires no guidance at '{guidance}' beforehand.");

        CommandExecution execution = Capture(() =>
            UpdateCommand(h, new ScriptedAgentRenderer("architect", "collider"), new RecordingProjectPicker(NoProjects),
                    false)
                .Execute(null!,
                    GlobalUpdate(h.MakeDirectory("unrelated-second-run"), targets: ["all"], replaceManaged: true)));

        AssertExit(execution, 1);
        Assert.Contains("Unmanaged path", execution.Output, StringComparison.Ordinal);
        Assert.False(
            File.Exists(guidance),
            "A run whose plan fails preflight must not have written the response guidance: preflight " +
            $"decides whether the run may write, and '{guidance}' did not exist beforehand.");
        string receipt = File.ReadAllText(RequireFile(GlobalReceiptPath(h)));
        Assert.False(
            receipt.Contains("response-guidance.md", StringComparison.Ordinal),
            "A run that never deployed the guidance must not record ownership of it.");
    }

    /// <summary>
    ///     The same ordering seen from the other side: a refreshable default already on disk keeps both its
    ///     bytes and its record when the plan is refused, so a failed run cannot silently upgrade the file
    ///     it was never allowed to touch.
    /// </summary>
    [Fact]
    public void UpdateGlobalWhosePlanFailsPreflightLeavesADigestMatchingResponseGuidanceUntouched()
    {
        using Harness h = NewHarness("guidance-blocked-preflight-deployed");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        SquadUpdateCommand command = UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false);
        AssertExit(Capture(() => command.Execute(null!, GlobalUpdate(anchor))), 0);

        // An earlier release's default, on disk and recorded: exactly the file a later run would replace.
        string guidance = GuidancePath(h);
        byte[] current = File.ReadAllBytes(guidance);
        byte[] superseded = Encoding.UTF8.GetBytes(
            "Response guidance from an earlier Kyber-Weave release, which the current release supersedes.\n");
        RewriteRecordedDigest(h, current, superseded);
        File.WriteAllBytes(guidance, superseded);
        string receiptBefore = File.ReadAllText(RequireFile(GlobalReceiptPath(h)));

        string architect = FindInstalled(h, SquadTarget.Codex, "architect.md");
        File.WriteAllBytes(
            Path.Combine(Path.GetDirectoryName(architect)!, "collider.md"),
            Encoding.UTF8.GetBytes("hand-authored collider\n"));

        CommandExecution execution = Capture(() =>
            UpdateCommand(h, new ScriptedAgentRenderer("architect", "collider"), new RecordingProjectPicker(NoProjects),
                    false)
                .Execute(null!,
                    GlobalUpdate(h.MakeDirectory("unrelated-second-run"), targets: ["all"], replaceManaged: true)));

        AssertExit(execution, 1);
        Assert.Contains("Unmanaged path", execution.Output, StringComparison.Ordinal);
        Assert.Equal(superseded, File.ReadAllBytes(guidance));
        Assert.Equal(receiptBefore, File.ReadAllText(RequireFile(GlobalReceiptPath(h))));
    }

    /// <summary>
    ///     A same-target reinstall over a receipt that already owns the guidance must keep the recorded
    ///     digest. Without it the deploy would claim no deployment record exists and leave the file alone,
    ///     so the receipt would lose the ownership it needs for the next run.
    /// </summary>
    [Fact]
    public void ReinstallGlobalKeepsTheRecordedGuidanceDigestWhenTheReceiptOwnsIt()
    {
        using Harness h = NewHarness("reinstall-keeps-digest");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        string shippedDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SquadResponseGuidanceSource.DefaultText)));

        SquadLifecycleResult reinstall = RunInstall(h, renderer, new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: InstalledVersion));

        Assert.True(reinstall.Success, DescribeFailure(reinstall));
        Assert.DoesNotContain(
            "no deployment record",
            string.Join(Environment.NewLine, reinstall.Warnings ?? []),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SquadResponseGuidanceSource.DefaultText, File.ReadAllText(GuidancePath(h), Encoding.UTF8));
        Assert.Contains(
            shippedDigest,
            File.ReadAllText(RequireFile(GlobalReceiptPath(h))),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A reinstall over an unedited default an older release wrote must refresh it to the current
    ///     default. The receipt records the older digest, so the file still matches what Squad owns.
    /// </summary>
    [Fact]
    public void ReinstallGlobalRefreshesAnUneditedOlderDefaultTheReceiptRecords()
    {
        using Harness h = NewHarness("reinstall-refreshes-older");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        string path = GuidancePath(h);
        byte[] shipped = File.ReadAllBytes(path);
        byte[] older = Encoding.UTF8.GetBytes("An earlier Kyber-Weave release's default.\n");
        string shippedDigest = Convert.ToHexStringLower(SHA256.HashData(shipped));

        RewriteRecordedDigest(h, shipped, older);
        File.WriteAllBytes(path, older);

        SquadLifecycleResult reinstall = RunInstall(h, renderer, new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: InstalledVersion));

        Assert.True(reinstall.Success, DescribeFailure(reinstall));
        Assert.Equal(SquadResponseGuidanceSource.DefaultText, File.ReadAllText(path, Encoding.UTF8));
        Assert.DoesNotContain(
            "Kept the edited response guidance",
            string.Join(Environment.NewLine, reinstall.Warnings ?? []),
            StringComparison.Ordinal);
        Assert.Contains(
            shippedDigest,
            File.ReadAllText(RequireFile(GlobalReceiptPath(h))),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Doctor reads the home guidance to compare what would be deployed. When the process cannot read
    ///     that file, doctor must say so rather than report a clean state.
    /// </summary>
    [Fact]
    public void DoctorGlobalSurfacesAGuidanceReadWarningWhenTheHomeFileIsUnreadable()
    {
        using Harness h = NewHarness("doctor-unreadable-guidance");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        WriteCanonicalSource(anchor);
        string path = GuidancePath(h);

        CommandExecution execution;
        using (SquadHomeHarness.DenyReadOrSkip(path))
        {
            execution = Capture(() =>
                new SquadDoctorCommand(
                        new FakeProcessExecutor(),
                        h.UserPaths,
                        anchor,
                        h.GlobalRoots,
                        renderer,
                        h.Store)
                    .Execute(null!, new SquadDoctorSettings { Path = anchor, Global = true }));
        }

        Assert.Contains("Failed to read response guidance", execution.Output, StringComparison.Ordinal);
        Assert.Contains("response-guidance", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A corpus that predates the response-preferences block must not crash global doctor, and doctor must
    ///     report the same no-block warning install and update report. Without the doctor's mirror of that
    ///     check, the three commands disagree about the same tree and doctor stays silent about nothing being
    ///     injected.
    /// </summary>
    [Fact]
    public void DoctorGlobalReportsTheNoBlockWarningForAPreBlockCorpusWithoutCrashing()
    {
        using Harness h = NewHarness("doctor-pre-block");
        string anchor = h.MakeDirectory("anchor");
        WriteCanonicalSourceWithoutResponseBlocks(anchor);

        CommandExecution execution = Capture(() =>
            new SquadDoctorCommand(
                    new FakeProcessExecutor(),
                    h.UserPaths,
                    anchor,
                    h.GlobalRoots,
                    SquadCommandComposition.ResolveRenderer(),
                    h.Store)
                .Execute(null!, new SquadDoctorSettings { Path = anchor, Global = true }));

        Assert.Contains(SquadResponsePreferencesInjector.NoBlockWarning, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Global unmanaged-collision scan failed", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A render that reports failure must stop global doctor the way install and update stop, naming the
    ///     collision-scan failure. Without the check, an empty failed render reads as a pre-block corpus and
    ///     doctor prints the no-block warning, which tells the operator the wrong thing about the cause.
    /// </summary>
    [Fact]
    public void DoctorGlobalReportsARenderFailureAsTheCollisionScanFailureNotAsNoBlock()
    {
        using Harness h = NewHarness("doctor-failed-render");
        string anchor = h.MakeDirectory("anchor");
        WriteCanonicalSourceWithoutResponseBlocks(anchor);

        CommandExecution execution = Capture(() =>
            new SquadDoctorCommand(
                    new FakeProcessExecutor(),
                    h.UserPaths,
                    anchor,
                    h.GlobalRoots,
                    new FailedRenderRenderer(),
                    h.Store)
                .Execute(null!, new SquadDoctorSettings { Path = anchor, Global = true }));

        Assert.Contains("Global unmanaged-collision scan failed", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(SquadResponsePreferencesInjector.NoBlockWarning, execution.Output, StringComparison.Ordinal);
    }

    private sealed class FailedRenderRenderer : ISquadRenderer
    {
        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = SquadTargetCatalog.All;

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SquadRenderResult(
                Success: false,
                Files: [],
                Degradations: [],
                Warnings: [],
                Errors: ["The render failed for the test."]));
        }
    }

    /// <summary>
    ///     Copies the canonical corpus beside a solution marker with every response-preferences block removed,
    ///     so the render carries no block anywhere. The precondition assertion keeps the test from passing
    ///     vacuously if the canonical blocks ever move.
    /// </summary>
    private static void WriteCanonicalSourceWithoutResponseBlocks(string anchor)
    {
        File.WriteAllText(Path.Combine(anchor, "KyberWeave.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00");
        string source = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
        string destination = Path.Combine(anchor, "products", "kyber-squad");
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        Regex block = ResponsePreferencesBlock();
        int blocksBefore = 0;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, target, overwrite: true);
                continue;
            }

            string text = File.ReadAllText(file, Encoding.UTF8);
            blocksBefore += block.Count(text);
            File.WriteAllText(target, block.Replace(text, string.Empty), Encoding.UTF8);
        }

        Assert.True(blocksBefore > 0, "The canonical corpus must carry response-preferences blocks for this test to mean anything.");
        foreach (string file in Directory.EnumerateFiles(destination, "*.md", SearchOption.AllDirectories))
            Assert.Equal(0, block.Count(File.ReadAllText(file, Encoding.UTF8)));
    }

    private static Regex ResponsePreferencesBlock()
        => new("<response-preferences>.*?</response-preferences>", RegexOptions.Singleline);

    /// <summary>
    ///     Doctor's global collision scan only runs against a canonical source beside the working
    ///     directory, so the arranged anchor carries the solution marker and a valid copy of the synthetic
    ///     corpus at <c>products/kyber-squad</c>.
    /// </summary>
    private static void WriteCanonicalSource(string anchor)
    {
        File.WriteAllText(Path.Combine(anchor, "KyberWeave.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00");
        string source = SyntheticValidSquadReleaseSource.SyntheticCorpusRoot;
        string destination = Path.Combine(anchor, "products", "kyber-squad");
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    /// <summary>
    ///     Rewrites the recorded SHA-256 of the deployed guidance across the home state, so the superseded
    ///     bytes look like a default this release replaced rather than a user edit. The receipt is the whole
    ///     ownership record, so replacing the digest there is the same state an older release leaves behind.
    /// </summary>
    private static void RewriteRecordedDigest(Harness h, byte[] deployed, byte[] superseded)
    {
        string before = Convert.ToHexStringLower(SHA256.HashData(deployed));
        string after = Convert.ToHexStringLower(SHA256.HashData(superseded));
        string stateRoot = Path.Combine(h.Home, ".kyber-weave");
        bool rewritten = false;
        foreach (string file in Directory.EnumerateFiles(stateRoot, "*", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (!text.Contains(before, StringComparison.OrdinalIgnoreCase))
                continue;

            File.WriteAllText(file, text.Replace(before, after, StringComparison.OrdinalIgnoreCase), Encoding.UTF8);
            rewritten = true;
        }

        Assert.True(
            rewritten,
            $"Expected the home state to record the SHA-256 of the deployed response guidance ('{before}').");
    }

    /// <summary>
    ///     A global receipt is rewritten by more than the deployment verbs — the preset commands rewrite it
    ///     through the same plan while deploying no guidance at all. If that rewrite dropped the guidance's
    ///     ownership record, the operator's untouched default would look unowned to the next update, which
    ///     would then preserve it forever and report an edit they never made.
    /// </summary>
    [Fact]
    public void GuidanceOwnershipSurvivesAReceiptRewriteThatDeploysNoGuidance()
    {
        using Harness h = NewHarness("guidance-preset-rewrite");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        SquadUpdateCommand command = UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false);
        AssertExit(Capture(() => command.Execute(null!, GlobalUpdate(anchor))), 0);

        // An earlier release's default on disk, recorded as what Squad deployed: the file a later run is
        // allowed to replace, and exactly the file a dropped record would strand.
        string path = GuidancePath(h);
        byte[] current = File.ReadAllBytes(path);
        byte[] superseded = Encoding.UTF8.GetBytes(
            "Response guidance from an earlier Kyber-Weave release, which the current release supersedes.\n");
        RewriteRecordedDigest(h, current, superseded);
        File.WriteAllBytes(path, superseded);

        // The preset verbs' own path: rewrite the global receipt through CreateUpdate over the files
        // already deployed, without deploying guidance.
        RewriteGlobalReceiptWithoutGuidance(h);

        Assert.True(
            File.ReadAllText(RequireFile(GlobalReceiptPath(h)))
                .Contains("response-guidance.md", StringComparison.Ordinal),
            "A receipt rewrite that deploys no guidance must carry the guidance ownership record " +
            "forward, so the next update still knows it owns that file.");

        CommandExecution refresh = Capture(() => command.Execute(null!, GlobalUpdate(anchor)));

        AssertExit(refresh, 0);
        Assert.DoesNotContain("Kept the response guidance", refresh.Output, StringComparison.Ordinal);
        Assert.True(
            current.AsSpan().SequenceEqual(File.ReadAllBytes(path)),
            "After a receipt rewrite that deployed no guidance, squad update --global must still " +
            $"recognize '{path}' as its own unmodified default and refresh it.");
    }

    /// <summary>
    ///     Uninstall keeps a receipt only when something survives it. A retained receipt must carry the
    ///     guidance record with it: the file outlives the uninstall either way, so a record dropped here
    ///     would strand it as unowned and it would never refresh again.
    /// </summary>
    [Fact]
    public void UninstallGlobalThatRetainsAnEditedFileKeepsTheGuidanceOwnershipRecord()
    {
        using Harness h = NewHarness("guidance-uninstall-retained");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        SquadUpdateCommand command = UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false);
        AssertExit(Capture(() => command.Execute(null!, GlobalUpdate(anchor))), 0);
        string guidance = GuidancePath(h);
        byte[] guidanceBefore = File.ReadAllBytes(guidance);

        // An edited owned file survives an uninstall, which is what makes the receipt retained rather
        // than deleted — the only uninstall outcome that writes a receipt to carry anything forward.
        string architect = FindInstalled(h, SquadTarget.Codex, "architect.md");
        File.WriteAllText(architect, "operator edit that uninstall must keep\n", Encoding.UTF8);

        CommandExecution uninstall = Uninstall(h, renderer, anchor, true);

        AssertExit(uninstall, 0);
        Assert.True(
            File.Exists(architect),
            "The scenario requires the edited file to survive, so the receipt is retained.");
        Assert.True(
            File.ReadAllText(RequireFile(GlobalReceiptPath(h)))
                .Contains("response-guidance.md", StringComparison.Ordinal),
            "A retained receipt must keep the guidance ownership record, or the file it describes is " +
            "left on disk unowned.");
        Assert.Equal(guidanceBefore, File.ReadAllBytes(guidance));
    }

    /// <summary>
    ///     Two updates can overlap, and the deployment lease is what keeps them apart. An update that cannot
    ///     take it has to have written nothing: guidance written ahead of the lease is replaced by whichever
    ///     run loses the race, under a receipt the winner commits for different bytes, and a later release
    ///     then reads the shipped text as an operator edit.
    /// </summary>
    [Fact]
    public void UpdateGlobalThatCannotTakeTheHomeLeaseLeavesTheResponseGuidanceUntouched()
    {
        using Harness h = NewHarness("guidance-lease-held");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        SquadUpdateCommand command = UpdateCommand(h, renderer, new RecordingProjectPicker(NoProjects), false);
        AssertExit(Capture(() => command.Execute(null!, GlobalUpdate(anchor))), 0);

        // An earlier release's default, recorded as what Squad deployed: the file a run that reached it
        // would replace. Its last-write time is pinned to a moment no write can share, so a rewrite of
        // any bytes shows.
        string path = GuidancePath(h);
        byte[] current = File.ReadAllBytes(path);
        byte[] superseded = Encoding.UTF8.GetBytes(
            "Response guidance from an earlier Kyber-Weave release, which the current release supersedes.\n");
        RewriteRecordedDigest(h, current, superseded);
        File.WriteAllBytes(path, superseded);
        DateTime pinned = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, pinned);
        Assert.True(
            File.GetLastWriteTimeUtc(path) == pinned,
            "The scenario requires the file system to keep the pinned last-write time.");

        string leaseKey = h.Store.ResolveLeaseIdentity(h.Home, SquadDeploymentScope.Global);
        CommandExecution refused;
        using (SquadTransaction.AcquireLease(leaseKey))
        {
            refused = Capture(() => command.Execute(null!, GlobalUpdate(anchor)));
        }

        AssertExit(refused, 1);
        Assert.Contains("transaction is active", refused.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            superseded.AsSpan().SequenceEqual(File.ReadAllBytes(path)),
            $"An update that could not take the lease must not have replaced '{path}'.");
        Assert.True(
            File.GetLastWriteTimeUtc(path) == pinned,
            $"An update that could not take the lease must not have written '{path}' at all.");

        // The refusal costs nothing: with the lease free the same run succeeds and refreshes the file.
        CommandExecution retried = Capture(() => command.Execute(null!, GlobalUpdate(anchor)));

        AssertExit(retried, 0);
        Assert.True(
            current.AsSpan().SequenceEqual(File.ReadAllBytes(path)),
            "Once the lease is free the update must refresh the superseded default.");
    }

    /// <summary>
    ///     The lease has to cover the guidance work itself, not only the commit after it. The callback a
    ///     transaction runs for home-owned files therefore executes with the lease held, so a second
    ///     deployment of the same root is refused while it runs, and what it attaches is what is committed.
    /// </summary>
    [Fact]
    public void HomeFilesAreDeployedWhileTheTransactionHoldsTheHomeLeaseAndTheirRecordIsCommitted()
    {
        using Harness h = NewHarness("guidance-under-lease");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        SquadDeploymentPlan plan = PlanGlobalReceiptRewrite(h);
        SquadHomeFile record = new(SquadResponseGuidanceDeployer.FileName, new string('a', 64));
        string leaseKey = h.Store.ResolveLeaseIdentity(h.Home, SquadDeploymentScope.Global);
        bool ran = false;

        new SquadTransaction(h.Store).Execute(plan, leasedPlan =>
        {
            ran = true;
            InvalidOperationException refused =
                Assert.Throws<InvalidOperationException>(() => SquadTransaction.AcquireLease(leaseKey).Dispose());
            Assert.Contains("transaction is active", refused.Message, StringComparison.OrdinalIgnoreCase);
            leasedPlan.AttachHomeFiles([record]);
        });

        Assert.True(ran, "The transaction must run the home-file callback.");
        SquadReceipt committed = h.Store.ReadReceipt(h.Home, SquadDeploymentScope.Global)
                                 ?? throw new InvalidOperationException("The transaction must leave a global receipt.");
        Assert.Contains(
            committed.HomeFiles,
            file => file.RelativePath == record.RelativePath && file.Sha256 == record.Sha256);
        using (SquadTransaction.AcquireLease(leaseKey))
        {
        }
    }

    /// <summary>
    ///     A callback that fails sits inside the transaction's cleanup, so it must leave the receipt as it
    ///     was and no half-open transaction behind: a stale journal would refuse every later run.
    /// </summary>
    [Fact]
    public void AHomeFileCallbackThatFailsLeavesTheReceiptAsItWasAndNoTransactionBehind()
    {
        using Harness h = NewHarness("guidance-callback-fails");
        ScriptedAgentRenderer renderer = new("architect");
        string anchor = h.MakeDirectory("anchor");
        InstallGlobal(h, anchor, renderer, SquadTarget.Codex);
        byte[] receiptBefore = File.ReadAllBytes(RequireFile(GlobalReceiptPath(h)));
        SquadDeploymentPlan plan = PlanGlobalReceiptRewrite(h);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            new SquadTransaction(h.Store).Execute(
                plan,
                _ => throw new InvalidOperationException("the callback broke its contract")));

        Assert.Equal("the callback broke its contract", failure.Message);
        Assert.True(
            receiptBefore.AsSpan().SequenceEqual(File.ReadAllBytes(GlobalReceiptPath(h))),
            "A callback that failed before the commit must leave the receipt exactly as it was.");

        // The next run neither finds an unfinished transaction nor a lease still held.
        new SquadTransaction(h.Store).Execute(PlanGlobalReceiptRewrite(h));
    }

    /// <summary>
    ///     Rewrites the global receipt the way the preset verbs do: re-plan the files already deployed
    ///     through <c>CreateUpdate</c> and commit the plan, with no guidance deploy anywhere in the path.
    /// </summary>
    private static void RewriteGlobalReceiptWithoutGuidance(Harness h)
    {
        new SquadTransaction(h.Store).Execute(PlanGlobalReceiptRewrite(h));
    }

    /// <summary>A plan that re-plans the files already deployed over the global receipt, ready to commit.</summary>
    private static SquadDeploymentPlan PlanGlobalReceiptRewrite(Harness h)
    {
        SquadReceipt active = h.Store.ReadReceipt(h.Home, SquadDeploymentScope.Global)
                              ?? throw new InvalidOperationException("The scenario requires a global receipt.");
        List<SquadDeploymentFile> deployed = active.Files
            .Select(file => new SquadDeploymentFile(
                file.RelativePath,
                File.ReadAllBytes(SquadDeploymentPlan.ResolveOwnedFilePath(active, h.Home, h.GlobalRoots, file)),
                file.Target))
            .ToList();

        return SquadDeploymentPlan.CreateUpdate(
            h.Home,
            SquadDeploymentScope.Global,
            LockFor(UpdatedVersion),
            deployed,
            active,
            [],
            true,
            TimeProvider.System,
            h.GlobalRoots);
    }

    /// <summary>The user-home guidance file, resolved from the fake home the harness owns.</summary>
    private static string GuidancePath(Harness h)
    {
        return Path.Combine(h.Home, ".kyber-weave", "response-guidance.md");
    }

    /// <summary>
    ///     Puts the home back in the state a fresh install used to leave: no response guidance file and no
    ///     ownership record for it. Global install now creates the file, so a scenario that needs the
    ///     guidance absent must remove both the file and its receipt record itself.
    /// </summary>
    private static void ClearResponseGuidanceToAbsentAndUnowned(Harness h)
    {
        string path = GuidancePath(h);
        if (File.Exists(path))
            File.Delete(path);

        string receiptPath = RequireFile(GlobalReceiptPath(h));
        JsonNode root = JsonNode.Parse(File.ReadAllText(receiptPath))
                        ?? throw new InvalidDataException("The global receipt must parse as JSON.");
        if (root["homeFiles"] is JsonArray homeFiles)
        {
            for (int index = homeFiles.Count - 1; index >= 0; index--)
            {
                if (homeFiles[index]?["relativePath"]?.GetValue<string>() == "response-guidance.md")
                    homeFiles.RemoveAt(index);
            }
        }

        File.WriteAllText(receiptPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Assert.False(File.Exists(path), $"The scenario requires no guidance at '{path}' beforehand.");
    }

    #endregion

    #region Fixtures

    private Harness NewHarness(string name)
    {
        return new Harness(Path.Combine(_temp.Path, name));
    }

    /// <summary>
    ///     Builds the registry the global CLI tests run against: a global install, two live projects
    ///     and one project whose root is then removed, so its registry entry is stale.
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
        Directory.Delete(stale.Root, true);
        return new Fleet(h, anchor, renderer, alpha, beta, stale);
    }

    private static void InstallGlobal(
        Harness h,
        string anchor,
        ISquadRenderer renderer,
        params SquadTarget[] targets)
    {
        SquadLifecycleResult result = RunInstall(h, renderer, new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            targets,
            Version: InstalledVersion));
        Assert.True(result.Success, DescribeFailure(result));
    }

    private static Project InstallProject(Harness h, string name, ISquadRenderer renderer)
    {
        string root = h.MakeDirectory(name);
        SquadLifecycleResult result = RunInstall(h, renderer, new SquadInstallRequest(
            root,
            SquadDeploymentScope.Project,
            [SquadTarget.Codex],
            Version: InstalledVersion));
        Assert.True(result.Success, DescribeFailure(result));
        string physical = SquadPhysicalRootIdentity.Resolve(root).PhysicalPath;
        string key = SquadPhysicalRootIdentity.Resolve(root).Key;
        string stateDirectory = Path.Combine(h.Home, ".kyber-weave", "squad", "projects", key);
        return new Project(root, physical, stateDirectory);
    }

    private static SquadLifecycleResult RunInstall(Harness h, ISquadRenderer renderer, SquadInstallRequest request)
    {
        return h.Lifecycle(renderer).InstallAsync(request).GetAwaiter().GetResult();
    }

    /// <summary>Builds the update command over the injected seams, so no run reaches a real harness directory.</summary>
    private static SquadUpdateCommand UpdateCommand(
        Harness h,
        ISquadRenderer renderer,
        ISquadProjectPicker picker,
        bool isInteractive,
        Func<string, bool>? readAnswer = null,
        ISquadReleaseSource? releaseSource = null)
    {
        return new SquadUpdateCommand(
            h.UserPaths,
            h.Store,
            releaseSource ?? h.ReleaseSource,
            renderer,
            isInteractive,
            readAnswer ?? (_ => true),
            h.GlobalRoots,
            picker);
    }

    private static SquadUpdateSettings GlobalUpdate(
        string path,
        string[]? projects = null,
        string[]? targets = null,
        bool replaceManaged = false,
        bool yes = true)
    {
        return new SquadUpdateSettings
        {
            Path = path,
            Global = true,
            Version = UpdatedVersion,
            Yes = yes,
            Projects = projects,
            Targets = targets ?? [],
            ReplaceManaged = replaceManaged
        };
    }

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
        File.WriteAllText(Path.Combine(directory, "squad.lock.yml"), h.Store.SerializeLock(LockFor(InstalledVersion)),
            Encoding.UTF8);
        string legacyFile = Path.Combine(directory, relativeFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
        File.WriteAllBytes(legacyFile, bytes);
    }

    /// <summary>
    ///     The pre-home layout: a receipt and lock under <c>.kyber-weave/</c> inside the project. The
    ///     migration imports it into the registry and leaves it byte-identical, so doctor must say it is kept.
    /// </summary>
    private static void WriteInRepoLegacyState(Harness h, string root)
    {
        string stateDirectory = Path.Combine(root, ".kyber-weave");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(
            Path.Combine(stateDirectory, "squad.receipt.json"),
            h.Store.SerializeReceipt(new SquadReceipt(
                "kyber-squad.receipt/v1",
                SquadDeploymentScope.Project,
                ".",
                DateTimeOffset.UtcNow,
                [],
                [])),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(stateDirectory, "squad.lock.yml"),
            h.Store.SerializeLock(LockFor(InstalledVersion)),
            Encoding.UTF8);
    }

    /// <summary>
    ///     Moves the home global receipt and lock into the legacy per-user root, the layout a pre-home install left
    ///     behind, so the next migration has a legacy copy to copy. The installed files stay where they were.
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
    ///     Moves a project's home state into the repository, the layout a pre-home install left behind, so the next
    ///     touch imports it. The project's installed files stay where they were.
    /// </summary>
    private static void MoveProjectStateInRepo(Harness h, Project project)
    {
        string inRepo = Path.Combine(project.Root, ".kyber-weave");
        Directory.CreateDirectory(inRepo);
        File.Move(project.ReceiptPath, Path.Combine(inRepo, "squad.receipt.json"));
        File.Move(project.LockPath, Path.Combine(inRepo, "squad.lock.yml"));
        Directory.Delete(project.StateDirectory);
    }

    private static CommandExecution Uninstall(Harness h, ISquadRenderer renderer, string path, bool global)
    {
        return Capture(() => new SquadUninstallCommand(
                h.UserPaths,
                h.Store,
                h.Lifecycle(renderer),
                false,
                null,
                h.GlobalRoots)
            .Execute(null!, new SquadUninstallSettings { Path = path, Global = global, Yes = true }));
    }

    private static CommandExecution Status(Harness h, string path, bool global)
    {
        return Capture(() => new SquadStatusCommand(
                h.UserPaths,
                h.Store,
                h.GlobalRoots)
            .Execute(null!, new SquadStatusSettings { Path = path, Global = global }));
    }

    private static SquadLock LockFor(string version)
    {
        return new SquadLock(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["codex"],
            [],
            "best-effort",
            "a".PadRight(64, '0'),
            "b".PadRight(64, '0'),
            new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')));
    }

    private static string GlobalStateDirectory(Harness h)
    {
        return Path.Combine(h.Home, ".kyber-weave", "squad", "global");
    }

    private static string GlobalLockPath(Harness h)
    {
        return Path.Combine(GlobalStateDirectory(h), "squad.lock.yml");
    }

    private static string GlobalReceiptPath(Harness h)
    {
        return Path.Combine(GlobalStateDirectory(h), "squad.receipt.json");
    }

    private static string RegistryPath(Harness h)
    {
        return Path.Combine(h.Home, ".kyber-weave", "squad", "registry.json");
    }

    private static string FindInstalled(Harness h, SquadTarget target, string fileName)
    {
        string root = h.GlobalRoot(target);
        string[] matches = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).ToArray();
        Assert.True(matches.Length == 1,
            $"Expected one installed '{fileName}' beneath {root}; found {matches.Length}.");
        return matches[0];
    }

    /// <summary>Reads the <c>squad-version</c> scalar from a lock file, without assuming how the store is wired.</summary>
    private static string VersionOf(string lockPath)
    {
        string prefix = "squad-version: ";
        string text = File.ReadAllText(RequireFile(lockPath), Encoding.UTF8);
        string? line = text.Split('\n')
            .FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        Assert.NotNull(line);
        return line[prefix.Length..].Trim().Trim('"');
    }

    /// <summary>Reads state before a run, or null when it is absent, so the run's own assertions go first.</summary>
    private static byte[]? Snapshot(string path)
    {
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Asserts a file keeps the bytes it had before the run, and that it still exists.</summary>
    private static void AssertUnchanged(byte[]? before, string path)
    {
        Assert.True(before is not null,
            $"Expected state at '{path}' before the run. Squad state must be written beneath the home store.");
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
        Assert.True(lines.Length == 1,
            $"Expected exactly one line naming '{text}', found {lines.Length}:{Environment.NewLine}{output}");
        return lines[0];
    }

    private static string DescribeFailure(SquadLifecycleResult result)
    {
        return result.Errors is null ? "The lifecycle reported failure." : string.Join("; ", result.Errors);
    }

    private static void AssertExit(CommandExecution execution, int expected)
    {
        Assert.True(
            execution.ExitCode == expected,
            $"Expected exit {expected} but got {execution.ExitCode}:{Environment.NewLine}{execution.Output}");
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private static IReadOnlyList<string> NoProjects(IReadOnlyList<SquadProjectRegistryEntry> projects)
    {
        return [];
    }

    private static IReadOnlyList<string> AllNonStale(IReadOnlyList<SquadProjectRegistryEntry> projects)
    {
        return projects.Where(entry => !entry.Stale).Select(entry => entry.Path).ToList();
    }

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
        public void Dispose()
        {
            Harness.Dispose();
        }

        public SquadUpdateCommand Command(
            ISquadProjectPicker picker,
            bool isInteractive,
            Func<string, bool> readAnswer,
            ISquadReleaseSource? releaseSource = null)
        {
            return UpdateCommand(Harness, Renderer, picker, isInteractive, readAnswer, releaseSource);
        }
    }

    /// <summary>
    ///     Renders one agent file per requested target at a path the test controls, so a receipt and a
    ///     collision can be planned against exact bytes instead of the fake renderer's harness layout.
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
                foreach (string agent in agentNames)
                    files.Add(new SquadDeploymentFile(
                        $"agents/{agent}.md",
                        Encoding.UTF8.GetBytes($"canonical {agent}\n"),
                        SquadTargetCatalog.GetToken(target)));

            return Task.FromResult(new SquadRenderResult(
                true,
                files,
                [],
                [],
                []));
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
            CancellationToken cancellationToken)
        {
            return Task.FromException<SquadReleaseResult>(
                new InvalidOperationException("The Squad release is unavailable."));
        }

        public void Dispose()
        {
        }
    }

    #endregion
}
