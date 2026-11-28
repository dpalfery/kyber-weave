using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the CLI contract for home-level Squad state: a <c>--global</c> run finds its one receipt whatever
/// directory it is launched from, offers the registered projects through the picker, honours
/// <c>--projects</c> without prompting, and keeps project, registry and global state consistent when one of
/// them fails.
/// </summary>
/// <remarks>
/// Every test runs over the in-memory file system: a fake home, a fake application-data directory and fake
/// global roots, all resolved through the seams the commands take. No test touches the disk, a real lease, a
/// real child process or the real home directory. The one resource a seam cannot replace — the operating
/// system's mutex — is proven by <c>MutexLeaseContractTests</c> in the integration project, and a real
/// permission denial on the home guidance file is proven by <c>SquadDeployTimeGuidanceWiringTests</c> there.
/// </remarks>
public sealed class SquadGlobalHomeCliTests : IDisposable
{
    private const string InstalledVersion = "1.2.3";
    private const string UpdatedVersion = "1.2.4";

    private readonly InMemorySquadState _state = new();
    private readonly FakeSquadReleaseSource _release;
    private readonly FakeSquadRenderer _renderer = FakeSquadRenderer.ScriptedAgents("architect");
    private readonly ISquadGlobalRootResolver _roots;
    private readonly IKyberWeaveConfigRepository _configs;
    private readonly string _work;

    public SquadGlobalHomeCliTests()
    {
        _work = Path.Combine(_state.Files.Root, "work");
        _state.Files.CreateDirectory(_work);
        _release = new FakeSquadReleaseSource { Files = _state.Files };
        _roots = new FakeSquadGlobalRootResolver(_state.UserPaths.HomeDirectory);
        _configs = new FileKyberWeaveConfigRepository(
            new FileRepositoryTreeProbe(_state.Files, _state.Paths, _state.Environment),
            _state.Files,
            _state.Paths,
            _state.Environment);
    }

    public void Dispose() => _release.Dispose();

    [Fact]
    public void UpdateGlobalFromTwoUnrelatedDirectoriesFindsTheSameGlobalReceipt()
    {
        string anchor = CreateDirectory("global-anchor");
        string first = CreateDirectory("unrelated-first");
        string second = CreateDirectory("unrelated-second");
        AssertExit(InstallGlobal(anchor), 0);

        CommandExecution fromFirst = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false).Execute(null!, GlobalUpdate(first)));
        CommandExecution fromSecond = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false).Execute(null!, GlobalUpdate(second)));

        AssertExit(fromFirst, 0);
        AssertExit(fromSecond, 0);
        Assert.DoesNotContain("No Kyber-Squad deployment found", fromSecond.Output, StringComparison.Ordinal);
        Assert.Equal(UpdatedVersion, VersionOf(GlobalLockPath()));
    }

    [Fact]
    public void UpdateGlobalReadsTheHomeConfigAsUserLevelSoItsArbiterGlobalBlockIsAccepted()
    {
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        WriteHomeConfig("""
            arbiter:
              enabled: true
              global:
                targets: [codex]
            """);

        CommandExecution execution = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false).Execute(null!, GlobalUpdate(anchor)));

        AssertExit(execution, 0);
        Assert.DoesNotContain("KW-ARB-CONFIG-011", execution.Output, StringComparison.Ordinal);
        Assert.Equal(UpdatedVersion, VersionOf(GlobalLockPath()));
    }

    [Fact]
    public void UpdateGlobalFailsOnAMalformedHomeConfigProvingTheHomeFileIsActuallyRead()
    {
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        WriteHomeConfig("arbiter: [unclosed");

        CommandExecution execution = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false).Execute(null!, GlobalUpdate(anchor)));

        AssertExit(execution, 1);
        Assert.Contains("kyber-weave squad: error:", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallGlobalReadsTheHomeConfigAsUserLevelSoItsArbiterGlobalBlockIsAccepted()
    {
        string anchor = CreateDirectory("anchor");
        WriteHomeConfig("""
            arbiter:
              enabled: true
              global:
                targets: [codex]
            """);

        CommandExecution install = InstallGlobal(anchor);

        AssertExit(install, 0);
        Assert.DoesNotContain("KW-ARB-CONFIG-011", install.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateGlobalWithAPathWarnsThatThePathIsIgnoredAndStillUpdates()
    {
        string anchor = CreateDirectory("anchor");
        string named = CreateDirectory("elsewhere");
        AssertExit(InstallGlobal(anchor), 0);

        CommandExecution execution = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false).Execute(null!, GlobalUpdate(named)));

        AssertExit(execution, 0);
        Assert.Contains("is ignored under --global", execution.Output, StringComparison.Ordinal);
        Assert.Contains(named, execution.Output, StringComparison.Ordinal);
        Assert.Equal(UpdatedVersion, VersionOf(GlobalLockPath()));
    }

    [Fact]
    public void UpdateGlobalPickerAllUpdatesEveryNonStaleProjectAndLeavesStaleOneAlone()
    {
        Fleet fleet = ArrangeFleet();
        RecordingProjectPicker picker = new(AllNonStale);
        PromptRecorder prompts = new();
        byte[]? staleLockBefore = Snapshot(fleet.Stale.LockPath);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, true, prompts.Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 0);
        Assert.Single(picker.Calls);
        Assert.Contains(picker.Calls[0], entry => entry.Stale && entry.Path == fleet.Stale.Root);
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(UpdatedVersion, VersionOf(fleet.Beta.LockPath));
        AssertUnchanged(staleLockBefore, fleet.Stale.LockPath);
    }

    [Fact]
    public void UpdateGlobalPickerNoneUpdatesNoProject()
    {
        Fleet fleet = ArrangeFleet();
        RecordingProjectPicker picker = new(NoProjects);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, true, new PromptRecorder().Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 0);
        Assert.Single(picker.Calls);
        Assert.Equal(UpdatedVersion, VersionOf(GlobalLockPath()));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    [Fact]
    public void UpdateGlobalPickerOneProjectUpdatesOnlyThatProject()
    {
        Fleet fleet = ArrangeFleet();
        RecordingProjectPicker picker = new(_ => [fleet.Alpha.Root]);

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
    ///     as client input before any write.
    /// </summary>
    [Fact]
    public void UpdateGlobalNoneCombinedWithAnotherProjectChoiceIsRejectedWithoutWrites()
    {
        Fleet fleet = ArrangeFleet();
        byte[]? globalLockBefore = Snapshot(GlobalLockPath());
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
                    Projects = ["none", fleet.Alpha.Root]
                }));

        AssertExit(execution, 2);
        Assert.Contains("none", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(picker.Calls);
        AssertUnchanged(globalLockBefore, GlobalLockPath());
        AssertUnchanged(alphaLockBefore, fleet.Alpha.LockPath);
    }

    [Fact]
    public void UpdateGlobalNonInteractiveWithoutProjectsNeverInvokesThePicker()
    {
        Fleet fleet = ArrangeFleet();
        RecordingProjectPicker picker = new(AllNonStale);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, false, new PromptRecorder().Answer)
                .Execute(null!, GlobalUpdate(fleet.Anchor, yes: false)));

        AssertExit(execution, 0);
        Assert.Empty(picker.Calls);
        Assert.Equal(UpdatedVersion, VersionOf(GlobalLockPath()));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Alpha.LockPath));
        Assert.Equal(InstalledVersion, VersionOf(fleet.Beta.LockPath));
    }

    [Fact]
    public void UpdateGlobalProjectsAllUpdatesEveryNonStaleProjectWithoutPrompting()
    {
        Fleet fleet = ArrangeFleet();
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
        Fleet fleet = ArrangeFleet();
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
        Fleet fleet = ArrangeFleet();
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
        Fleet fleet = ArrangeFleet();
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
        string anchor = CreateDirectory("global-anchor");
        AssertExit(InstallGlobal(anchor), 0);
        string importable = CreateDirectory("importable");
        WriteInRepoLegacyState(importable);
        string inRepoReceipt = Path.Combine(importable, ".kyber-weave", "squad.receipt.json");
        string inRepoLock = Path.Combine(importable, ".kyber-weave", "squad.lock.yml");
        string homeProjectState = _state.Layout.HomeProjectDirectory(importable);
        byte[]? receiptBefore = Snapshot(inRepoReceipt);
        byte[]? lockBefore = Snapshot(inRepoLock);
        byte[]? globalLockBefore = Snapshot(GlobalLockPath());
        byte[]? registryBefore = Snapshot(RegistryPath());
        SquadUpdateSettings settings = GlobalUpdate(anchor, [importable]);
        settings.DryRun = true;

        CommandExecution execution = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false).Execute(null!, settings));

        AssertExit(execution, 0);
        Assert.Contains("on the next non-dry-run invocation", execution.Output, StringComparison.Ordinal);
        Assert.Contains("0 updated, 0 failed, 1 skipped", execution.Output, StringComparison.Ordinal);
        Assert.False(
            _state.Files.DirectoryExists(homeProjectState),
            "A dry run must not create the home project state.");
        Assert.Equal(registryBefore, Snapshot(RegistryPath()));
        AssertUnchanged(receiptBefore, inRepoReceipt);
        AssertUnchanged(lockBefore, inRepoLock);
        AssertUnchanged(globalLockBefore, GlobalLockPath());
    }

    /// <summary>
    ///     An unknown path is refused before any global work starts, and the hint names the nearest
    ///     registered project so a typo is one copy-paste away from being fixed.
    /// </summary>
    [Fact]
    public void UpdateGlobalUnknownProjectsPathExitsTwoWithNearestHintAndNoWrites()
    {
        Fleet fleet = ArrangeFleet();
        byte[]? globalLockBefore = Snapshot(GlobalLockPath());
        byte[]? alphaLockBefore = Snapshot(fleet.Alpha.LockPath);
        string typo = Path.Combine(_work, "alpah");

        CommandExecution execution = Capture(() =>
            fleet.Command(new RecordingProjectPicker(NoProjects), false, new PromptRecorder().Answer)
                .Execute(null!, GlobalUpdate(fleet.Anchor, [typo])));

        AssertExit(execution, 2);
        Assert.Contains(fleet.Alpha.Root, execution.Output, StringComparison.Ordinal);
        AssertUnchanged(globalLockBefore, GlobalLockPath());
        AssertUnchanged(alphaLockBefore, fleet.Alpha.LockPath);
    }

    [Fact]
    public void UpdateProjectsWithoutGlobalExitsTwoNamingTheGlobalFlag()
    {
        Project alpha = InstallProject("alpha");
        byte[]? alphaLockBefore = Snapshot(alpha.LockPath);

        CommandExecution execution = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false)
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
        Fleet fleet = ArrangeFleet();
        RecordingProjectPicker picker = new(AllNonStale);
        using FakeSquadReleaseSource failing =
            FakeSquadReleaseSource.Failing(new InvalidOperationException("The Squad release is unavailable."));

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
        Fleet fleet = ArrangeFleet();
        string betaConfigDirectory = Path.Combine(fleet.Beta.Root, ".kyber-weave");
        _state.Files.CreateDirectory(betaConfigDirectory);
        _state.Files.WriteAllText(
            Path.Combine(betaConfigDirectory, "kyber-weave.yml"),
            "key: [unterminated\n");
        RecordingProjectPicker picker = new(AllNonStale);

        CommandExecution execution = Capture(() =>
            fleet.Command(picker, true, new PromptRecorder().Answer).Execute(null!, GlobalUpdate(fleet.Anchor)));

        AssertExit(execution, 1);
        Assert.Contains(fleet.Alpha.Root, execution.Output, StringComparison.Ordinal);
        Assert.Contains(fleet.Beta.Root, execution.Output, StringComparison.Ordinal);
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        string architect = InstalledFile(SquadTarget.Codex, "architect.md");
        byte[] canonical = _state.Files.ReadAllBytes(architect);
        _state.Files.WriteAllText(architect, "locally edited architect\n");
        byte[] edited = _state.Files.ReadAllBytes(architect);

        CommandExecution execution = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false)
                .Execute(null!,
                    GlobalUpdate(CreateDirectory("unrelated-second-run"), targets: ["all"], replaceManaged: true)));

        AssertExit(execution, 0);
        Assert.NotEqual(edited, _state.Files.ReadAllBytes(architect));
        Assert.Equal(canonical, _state.Files.ReadAllBytes(architect));
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        string architect = InstalledFile(SquadTarget.Codex, "architect.md");
        _state.Files.WriteAllText(architect, "locally edited architect\n");
        byte[] edited = _state.Files.ReadAllBytes(architect);
        string collider = Path.Combine(Path.GetDirectoryName(architect)!, "collider.md");
        byte[] unmanaged = Encoding.UTF8.GetBytes("hand-authored collider\n");
        _state.Files.WriteAllBytes(collider, unmanaged);
        byte[]? receiptBefore = Snapshot(GlobalReceiptPath());
        byte[]? lockBefore = Snapshot(GlobalLockPath());
        FakeSquadRenderer updateRenderer = FakeSquadRenderer.ScriptedAgents("architect", "collider");

        CommandExecution execution = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false, updateRenderer)
                .Execute(null!,
                    GlobalUpdate(CreateDirectory("unrelated-second-run"), targets: ["all"], replaceManaged: true)));

        AssertExit(execution, 1);
        Assert.Contains("Unmanaged path", execution.Output, StringComparison.Ordinal);
        Assert.True(
            execution.Output.Contains("--adopt", StringComparison.Ordinal) ||
            execution.Output.Contains("Move the file aside", StringComparison.Ordinal),
            $"The collision must name the way out.{Environment.NewLine}{execution.Output}");
        AssertUnchanged(unmanaged, collider);
        AssertUnchanged(edited, architect);
        AssertUnchanged(receiptBefore, GlobalReceiptPath());
        AssertUnchanged(lockBefore, GlobalLockPath());
        Assert.False(
            _state.Files.DirectoryExists(_roots.ResolveGlobalRoot(SquadTarget.Claude)),
            "A run that fails on one path must not write into another target's global root.");
    }

    [Fact]
    public void StatusGlobalListsRegisteredProjectsWithStaleFlags()
    {
        Fleet fleet = ArrangeFleet();

        CommandExecution execution = Status(fleet.Anchor, global: true);

        string alphaLine = LineContaining(execution.Output, fleet.Alpha.Root);
        string staleLine = LineContaining(execution.Output, fleet.Stale.Root);
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
        string anchor = CreateDirectory("global-anchor");
        AssertExit(InstallGlobal(anchor), 0);
        const string corrupt = "{ this registry is corrupt \n";
        _state.Files.WriteAllText(RegistryPath(), corrupt);

        CommandExecution status = Status(anchor, global: true);

        AssertExit(status, 1);
        Assert.Contains("registry is unreadable", status.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(corrupt, _state.Files.ReadAllText(RegistryPath()));
    }

    /// <summary>
    ///     Doctor is the one place an operator sees the state the migration and registry left behind,
    ///     so each category must be named: a stale registry entry, a legacy global root the migration
    ///     could not move, and in-repo legacy state that was imported but kept.
    /// </summary>
    [Fact]
    public void DoctorGlobalReportsStaleEntriesUnmigratedLegacyRootsAndRetainedInRepoState()
    {
        Fleet fleet = ArrangeFleet();
        WriteLegacyGlobalRoot("legacy-alpha", "agents/alpha.toml", "alpha\n");
        WriteLegacyGlobalRoot("legacy-beta", "agents/beta.toml", "beta\n");
        Project gamma = InstallProject("gamma");
        WriteInRepoLegacyState(gamma.Root);

        CommandExecution execution = Doctor(fleet.Anchor, fleet.Renderer);

        Assert.Contains("is stale (", execution.Output, StringComparison.Ordinal);
        Assert.Contains("until its path is restored", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("uninstall it", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fleet.Stale.Root, execution.Output, StringComparison.Ordinal);
        Assert.Contains("legacy-alpha", execution.Output, StringComparison.Ordinal);
        Assert.Contains("legacy-beta", execution.Output, StringComparison.Ordinal);
        Assert.Contains("retain", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(gamma.Root, ".kyber-weave"), execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Global uninstall removes machine-wide files and nothing else. A project that still depends on
    ///     the registry, or a registry the operator can see, must keep the same bytes.
    /// </summary>
    /// <remarks>
    ///     <c>squad uninstall</c> takes no project picker, so there is no seam through which a
    ///     <c>--global</c> uninstall could consult one and nothing to fake for it. What the case is named
    ///     for is stronger and is asserted directly: each project's whole tree — receipt, lock and every
    ///     deployed file — and the registry are the same bytes after the run as before it.
    /// </remarks>
    [Fact]
    public void UninstallGlobalLeavesProjectStateAndRegistryUntouched()
    {
        Fleet fleet = ArrangeFleet();
        string registry = RegistryPath();
        byte[]? registryBefore = Snapshot(registry);
        string alphaTreeBefore = CaptureTree(fleet.Alpha.Root);
        string betaTreeBefore = CaptureTree(fleet.Beta.Root);
        byte[]? alphaLockBefore = Snapshot(fleet.Alpha.LockPath);
        byte[]? alphaReceiptBefore = Snapshot(fleet.Alpha.ReceiptPath);

        CommandExecution execution = Uninstall(fleet.Anchor, global: true);

        AssertExit(execution, 0);
        Assert.False(
            _state.Files.FileExists(GlobalReceiptPath()),
            "Global uninstall must remove the global receipt.");
        AssertUnchanged(alphaLockBefore, fleet.Alpha.LockPath);
        AssertUnchanged(alphaReceiptBefore, fleet.Alpha.ReceiptPath);
        AssertUnchanged(registryBefore, registry);
        Assert.False(string.IsNullOrEmpty(alphaTreeBefore), "The scenario requires project state to compare.");
        Assert.Equal(alphaTreeBefore, CaptureTree(fleet.Alpha.Root));
        Assert.Equal(betaTreeBefore, CaptureTree(fleet.Beta.Root));
    }

    /// <summary>
    ///     A migrated global deployment that was uninstalled must stay uninstalled. The migration keeps the legacy copy
    ///     for an older CLI, so the command that runs after the uninstall must not copy it back into the home.
    /// </summary>
    [Fact]
    public void UninstallOfMigratedGlobalStateIsNotUndoneByTheNextCommand()
    {
        string anchor = CreateDirectory("global-anchor");
        AssertExit(InstallGlobal(anchor), 0);
        MoveGlobalStateToLegacyRoot(anchor);

        AssertExit(Uninstall(anchor, global: true), 0);
        Assert.False(
            _state.Files.FileExists(GlobalReceiptPath()),
            "Global uninstall must remove the global receipt.");

        CommandExecution status = Status(anchor, global: true);

        AssertExit(status, 1);
        Assert.Contains("No Kyber-Squad deployment found", status.Output, StringComparison.Ordinal);
        Assert.False(
            _state.Files.FileExists(GlobalReceiptPath()),
            "The command after an uninstall must not re-create the global receipt from the legacy copy.");
        Assert.False(
            _state.Files.FileExists(GlobalLockPath()),
            "The command after an uninstall must not re-create the global lock from the legacy copy.");
    }

    /// <summary>
    ///     Project state imported from the repository must not come back after the project is uninstalled. The in-repo
    ///     copy is kept on purpose, so the next touch would import it again unless the import is recorded as done.
    /// </summary>
    [Fact]
    public void UninstallOfImportedProjectStateIsNotUndoneByTheNextTouch()
    {
        Project alpha = InstallProject("alpha");
        MoveProjectStateInRepo(alpha);
        _ = _state.Migration.ImportProject(alpha.Root);
        Assert.True(
            _state.Files.FileExists(alpha.ReceiptPath),
            "The import must restore the home project receipt before the uninstall.");

        AssertExit(Uninstall(alpha.Root, global: false), 0);
        Assert.False(
            _state.Files.FileExists(alpha.ReceiptPath),
            "Project uninstall must remove the home project receipt.");
        Assert.True(
            _state.Files.FileExists(Path.Combine(alpha.Root, ".kyber-weave", "squad.receipt.json")),
            "Project uninstall keeps the in-repo state, so the next touch has something to re-import.");

        CommandExecution touch = Status(alpha.Root, global: false);

        AssertExit(touch, 1);
        Assert.Contains("No Kyber-Squad deployment found", touch.Output, StringComparison.Ordinal);
        Assert.False(
            _state.Files.FileExists(alpha.ReceiptPath),
            "The next touch must not re-import the in-repo state after the project was uninstalled.");
        Assert.DoesNotContain(
            _state.Store.ListProjects(),
            entry => string.Equals(entry.Path, alpha.Root, StringComparison.Ordinal));
    }

    /// <summary>
    ///     After a migrated global uninstall, an explicit fresh install is not blocked by the migration record. It writes
    ///     new home state, and the status that follows reads that state back.
    /// </summary>
    [Fact]
    public void FreshGlobalInstallAfterUninstallOfMigratedStateWritesNewHomeState()
    {
        string anchor = CreateDirectory("global-anchor");
        AssertExit(InstallGlobal(anchor), 0);
        MoveGlobalStateToLegacyRoot(anchor);
        AssertExit(Uninstall(anchor, global: true), 0);

        CommandExecution install = InstallGlobal(anchor);

        AssertExit(install, 0);
        Assert.True(
            _state.Files.FileExists(GlobalReceiptPath()),
            "A fresh global install must write new home global state.");
        CommandExecution status = Status(anchor, global: true);
        AssertExit(status, 0);
        Assert.Contains("All deployed files match", status.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Guards the isolation rule itself: every fake home and global root in this class lives inside the
    ///     in-memory file system, so a test that forgets its fixture cannot quietly reach the real profile. The
    ///     user-paths fake's own refusal of a location outside the temp directory is proven by
    ///     <c>FakeSquadUserPathsTests</c>.
    /// </summary>
    [Fact]
    public void EveryFakeHomeAndGlobalRootLivesInsideTheInMemoryFileSystem()
    {
        string home = _state.UserPaths.HomeDirectory;

        Assert.True(_state.Files.DirectoryExists(home), $"The fake home '{home}' must exist in memory.");
        Assert.StartsWith(_state.Files.Root, Path.GetFullPath(home), StringComparison.Ordinal);
        Assert.StartsWith(
            _state.Files.Root,
            Path.GetFullPath(_state.UserPaths.ApplicationDataDirectory),
            StringComparison.Ordinal);
        Assert.StartsWith(_state.Files.Root, Path.GetFullPath(_work), StringComparison.Ordinal);
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            Assert.StartsWith(
                _state.Files.Root,
                Path.GetFullPath(_roots.ResolveGlobalRoot(target)),
                StringComparison.Ordinal);
        }
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        ClearResponseGuidanceToAbsentAndUnowned();

        // Pinned to this fixture's own home rather than to the in-memory root it happens to sit under:
        // a prefix check would pass for a guidance file deployed beside the wrong home.
        string expected = Path.GetFullPath(GuidancePath());
        Assert.False(
            _state.Files.FileExists(expected),
            $"The scenario requires no guidance at '{expected}' beforehand.");

        CommandExecution update = DeployGuidance(anchor);

        AssertExit(update, 0);
        Assert.True(
            _state.Files.FileExists(expected),
            $"squad update --global must deploy the default response guidance to exactly '{expected}'.");
        Assert.False(
            string.IsNullOrWhiteSpace(_state.Files.ReadAllText(expected)),
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        AssertExit(DeployGuidance(anchor), 0);
        string guidancePath = GuidancePath();
        Assert.True(
            _state.Files.FileExists(guidancePath),
            $"squad update --global must deploy the default response guidance to '{guidancePath}' " +
            "before the run can record that it owns it.");
        string digest = Convert.ToHexStringLower(SHA256.HashData(_state.Files.ReadAllBytes(guidancePath)));
        string receipt = GlobalReceiptText();

        Assert.True(
            receipt.Contains(SquadResponseGuidanceDeployer.FileName, StringComparison.Ordinal),
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        AssertExit(DeployGuidance(anchor), 0);
        string path = GuidancePath();
        Assert.True(
            _state.Files.FileExists(path),
            $"squad update --global must deploy the default response guidance to '{path}'.");
        byte[] current = _state.Files.ReadAllBytes(path);
        byte[] superseded = Encoding.UTF8.GetBytes(
            "Response guidance from an earlier Kyber-Weave release, which the current release supersedes.\n");

        // The state records the superseded bytes as what it deployed, and nobody has edited them since:
        // exactly the file an update is allowed to replace.
        RewriteRecordedDigest(current, superseded);
        _state.Files.WriteAllBytes(path, superseded);

        CommandExecution second = DeployGuidance(anchor);

        AssertExit(second, 0);
        Assert.True(
            current.AsSpan().SequenceEqual(_state.Files.ReadAllBytes(path)),
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        AssertExit(DeployGuidance(anchor), 0);
        string path = GuidancePath();
        Assert.True(
            _state.Files.FileExists(path),
            $"squad update --global must deploy the default response guidance to '{path}'.");
        const string userWording = "Keep every answer to three sentences and never open with a heading.\n";
        _state.Files.WriteAllText(path, userWording);

        CommandExecution second = DeployGuidance(anchor);

        AssertExit(second, 0);
        Assert.Equal(userWording, _state.Files.ReadAllText(path));
        Assert.Contains(SquadResponseGuidanceDeployer.FileName, second.Output, StringComparison.Ordinal);
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        AssertExit(DeployGuidance(anchor), 0);
        string path = GuidancePath();
        Assert.True(
            _state.Files.FileExists(path),
            $"squad update --global must deploy the default response guidance to '{path}'.");
        byte[] shipped = _state.Files.ReadAllBytes(path);
        string shippedDigest = Convert.ToHexStringLower(SHA256.HashData(shipped));

        // The receipt is rewritten to describe a different file; the deployed bytes are never touched, so
        // the disk holds the shipped default while the record denies it. This is the state a run leaves
        // behind when it deploys the guidance and then fails before writing the receipt.
        RewriteRecordedDigest(shipped, Encoding.UTF8.GetBytes("An earlier Kyber-Weave release's default.\n"));
        Assert.False(
            GlobalReceiptText().Contains(shippedDigest, StringComparison.OrdinalIgnoreCase),
            "The scenario requires a receipt that no longer records the digest of the bytes on disk.");

        // Rewriting identical bytes would still satisfy a byte comparison, so the clock is advanced past
        // the last write: adopting has to leave the file's last-write time where it was.
        DateTime stamped = _state.Files.GetLastWriteTimeUtc(path);
        _state.Time.Advance(TimeSpan.FromHours(1));

        CommandExecution second = DeployGuidance(anchor);

        AssertExit(second, 0);
        Assert.True(
            shipped.AsSpan().SequenceEqual(_state.Files.ReadAllBytes(path)),
            $"'{path}' already holds the shipped default, so the run must adopt those bytes rather than " +
            "rewrite them.");
        Assert.Equal(stamped, _state.Files.GetLastWriteTimeUtc(path));
        Assert.DoesNotContain("Kept the edited", second.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Kept the response guidance", second.Output, StringComparison.Ordinal);
        Assert.True(
            GlobalReceiptText().Contains(shippedDigest, StringComparison.OrdinalIgnoreCase),
            "A run that finds the shipped default already on disk must record its digest, repairing a " +
            "receipt that describes a file the earlier run never delivered. Expected digest: " +
            $"{shippedDigest}.");

        // Negative control, so the assertions above cannot pass vacuously: change only the file's bytes,
        // leaving the receipt alone, and the very same run must now refuse and say so.
        _state.Files.WriteAllText(path, "Edited by the operator after the adopt.\n");

        CommandExecution third = DeployGuidance(anchor);

        AssertExit(third, 0);
        Assert.Equal("Edited by the operator after the adopt.\n", _state.Files.ReadAllText(path));
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        ClearResponseGuidanceToAbsentAndUnowned();
        string path = GuidancePath();

        // A directory at the destination makes the write fail the way a permission error or a full
        // disk does — through the same atomic move the deployer uses — without a real disk.
        _state.Files.CreateDirectory(path);

        CommandExecution failed = DeployGuidance(anchor);

        AssertExit(failed, 0);
        Assert.Contains(SquadResponseGuidanceDeployer.FileName, failed.Output, StringComparison.Ordinal);
        Assert.True(_state.Files.DirectoryExists(path), $"The failed write must not remove '{path}'.");
        Assert.False(
            GlobalReceiptText().Contains(SquadResponseGuidanceDeployer.FileName, StringComparison.Ordinal),
            "A run that could not write the guidance must record no ownership of it: a digest recorded " +
            "ahead of its write makes the next run read the operator's untouched file as an edit.");

        _state.Files.DeleteTreeNoFollow(path);

        CommandExecution recovered = DeployGuidance(anchor);

        AssertExit(recovered, 0);
        Assert.True(
            _state.Files.FileExists(path),
            $"squad update --global must deploy the default response guidance to '{path}' on retry.");
        string digest = Convert.ToHexStringLower(SHA256.HashData(_state.Files.ReadAllBytes(path)));

        // The retry wrote the default, so nothing was preserved and nothing may claim an operator edit.
        Assert.DoesNotContain("Kept the edited", recovered.Output, StringComparison.Ordinal);
        Assert.True(
            GlobalReceiptText().Contains(digest, StringComparison.OrdinalIgnoreCase),
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        ClearResponseGuidanceToAbsentAndUnowned();

        // An unowned hand-authored file at a path the update would claim, so plan construction throws
        // before any write.
        string architect = InstalledFile(SquadTarget.Codex, "architect.md");
        _state.Files.WriteAllBytes(
            Path.Combine(Path.GetDirectoryName(architect)!, "collider.md"),
            Encoding.UTF8.GetBytes("hand-authored collider\n"));
        string guidance = GuidancePath();
        Assert.False(
            _state.Files.FileExists(guidance),
            $"The scenario requires no guidance at '{guidance}' beforehand.");

        CommandExecution execution = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false, FakeSquadRenderer.ScriptedAgents("architect", "collider"))
                .Execute(null!,
                    GlobalUpdate(CreateDirectory("unrelated-second-run"), targets: ["all"], replaceManaged: true)));

        AssertExit(execution, 1);
        Assert.Contains("Unmanaged path", execution.Output, StringComparison.Ordinal);
        Assert.False(
            _state.Files.FileExists(guidance),
            "A run whose plan fails preflight must not have written the response guidance: preflight " +
            $"decides whether the run may write, and '{guidance}' did not exist beforehand.");
        Assert.False(
            GlobalReceiptText().Contains(SquadResponseGuidanceDeployer.FileName, StringComparison.Ordinal),
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        AssertExit(DeployGuidance(anchor), 0);

        // An earlier release's default, on disk and recorded: exactly the file a later run would replace.
        string guidance = GuidancePath();
        byte[] current = _state.Files.ReadAllBytes(guidance);
        byte[] superseded = Encoding.UTF8.GetBytes(
            "Response guidance from an earlier Kyber-Weave release, which the current release supersedes.\n");
        RewriteRecordedDigest(current, superseded);
        _state.Files.WriteAllBytes(guidance, superseded);
        string receiptBefore = GlobalReceiptText();

        string architect = InstalledFile(SquadTarget.Codex, "architect.md");
        _state.Files.WriteAllBytes(
            Path.Combine(Path.GetDirectoryName(architect)!, "collider.md"),
            Encoding.UTF8.GetBytes("hand-authored collider\n"));

        CommandExecution execution = Capture(() =>
            UpdateCommand(new RecordingProjectPicker(NoProjects), false, FakeSquadRenderer.ScriptedAgents("architect", "collider"))
                .Execute(null!,
                    GlobalUpdate(CreateDirectory("unrelated-second-run"), targets: ["all"], replaceManaged: true)));

        AssertExit(execution, 1);
        Assert.Contains("Unmanaged path", execution.Output, StringComparison.Ordinal);
        Assert.Equal(superseded, _state.Files.ReadAllBytes(guidance));
        Assert.Equal(receiptBefore, GlobalReceiptText());
    }

    /// <summary>
    ///     A same-target reinstall over a receipt that already owns the guidance must keep the recorded
    ///     digest. Without it the deploy would claim no deployment record exists and leave the file alone,
    ///     so the receipt would lose the ownership it needs for the next run.
    /// </summary>
    [Fact]
    public void ReinstallGlobalKeepsTheRecordedGuidanceDigestWhenTheReceiptOwnsIt()
    {
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        string shippedDigest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(SquadResponseGuidanceSource.DefaultText)));

        SquadLifecycleResult reinstall = RunInstall(anchor);

        Assert.True(reinstall.Success, DescribeFailure(reinstall));
        Assert.DoesNotContain(
            "no deployment record",
            string.Join(Environment.NewLine, reinstall.Warnings ?? []),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SquadResponseGuidanceSource.DefaultText, _state.Files.ReadAllText(GuidancePath()));
        Assert.Contains(shippedDigest, GlobalReceiptText(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A reinstall over an unedited default an older release wrote must refresh it to the current
    ///     default. The receipt records the older digest, so the file still matches what Squad owns.
    /// </summary>
    [Fact]
    public void ReinstallGlobalRefreshesAnUneditedOlderDefaultTheReceiptRecords()
    {
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        string path = GuidancePath();
        byte[] shipped = _state.Files.ReadAllBytes(path);
        byte[] older = Encoding.UTF8.GetBytes("An earlier Kyber-Weave release's default.\n");
        string shippedDigest = Convert.ToHexStringLower(SHA256.HashData(shipped));

        RewriteRecordedDigest(shipped, older);
        _state.Files.WriteAllBytes(path, older);

        SquadLifecycleResult reinstall = RunInstall(anchor);

        Assert.True(reinstall.Success, DescribeFailure(reinstall));
        Assert.Equal(SquadResponseGuidanceSource.DefaultText, _state.Files.ReadAllText(path));
        Assert.DoesNotContain(
            "Kept the edited response guidance",
            string.Join(Environment.NewLine, reinstall.Warnings ?? []),
            StringComparison.Ordinal);
        Assert.Contains(shippedDigest, GlobalReceiptText(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Doctor reads the home guidance to compare what would be deployed. When the process cannot read
    ///     that file, doctor must say so rather than report a clean state. The real permission denial this
    ///     reader also survives is proven by <c>SquadDeployTimeGuidanceWiringTests</c>; here the file system
    ///     seam fails the read.
    /// </summary>
    [Fact]
    public void DoctorGlobalSurfacesAGuidanceReadWarningWhenTheHomeFileCannotBeRead()
    {
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        PrepareCanonicalSource();
        string path = GuidancePath();
        _state.Files.FailWhen(
            operation => operation.Path is { } candidate
                         && string.Equals(candidate, path, StringComparison.Ordinal)
                         && operation.Name.StartsWith("Read", StringComparison.Ordinal),
            new UnauthorizedAccessException("Access to the path is denied."));

        CommandExecution execution;
        try
        {
            execution = Doctor(_work, FakeSquadRenderer.ScriptedAgents("architect"));
        }
        finally
        {
            _state.Files.ClearFaults();
        }

        Assert.Contains("Failed to read response guidance", execution.Output, StringComparison.Ordinal);
        Assert.Contains(SquadResponseGuidanceDeployer.FileName, execution.Output, StringComparison.OrdinalIgnoreCase);
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
        string anchor = CreateDirectory("anchor");
        WriteCanonicalSourceWithoutResponseBlocks();

        CommandExecution execution = Doctor(_work, renderer: null);

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
        string anchor = CreateDirectory("anchor");
        PrepareCanonicalSource();

        CommandExecution execution = Doctor(_work, FakeSquadRenderer.Failing("The render failed for the test."));

        Assert.Contains("Global unmanaged-collision scan failed", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(SquadResponsePreferencesInjector.NoBlockWarning, execution.Output, StringComparison.Ordinal);
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        AssertExit(DeployGuidance(anchor), 0);

        // An earlier release's default on disk, recorded as what Squad deployed: the file a later run is
        // allowed to replace, and exactly the file a dropped record would strand.
        string path = GuidancePath();
        byte[] current = _state.Files.ReadAllBytes(path);
        byte[] superseded = Encoding.UTF8.GetBytes(
            "Response guidance from an earlier Kyber-Weave release, which the current release supersedes.\n");
        RewriteRecordedDigest(current, superseded);
        _state.Files.WriteAllBytes(path, superseded);

        // The preset verbs' own path: rewrite the global receipt through CreateUpdate over the files
        // already deployed, without deploying guidance.
        RewriteGlobalReceiptWithoutGuidance();

        Assert.True(
            GlobalReceiptText().Contains(SquadResponseGuidanceDeployer.FileName, StringComparison.Ordinal),
            "A receipt rewrite that deploys no guidance must carry the guidance ownership record " +
            "forward, so the next update still knows it owns that file.");

        CommandExecution refresh = DeployGuidance(anchor);

        AssertExit(refresh, 0);
        Assert.DoesNotContain("Kept the response guidance", refresh.Output, StringComparison.Ordinal);
        Assert.True(
            current.AsSpan().SequenceEqual(_state.Files.ReadAllBytes(path)),
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        AssertExit(DeployGuidance(anchor), 0);
        string guidance = GuidancePath();
        byte[] guidanceBefore = _state.Files.ReadAllBytes(guidance);

        // An edited owned file survives an uninstall, which is what makes the receipt retained rather
        // than deleted — the only uninstall outcome that writes a receipt to carry anything forward.
        string architect = InstalledFile(SquadTarget.Codex, "architect.md");
        _state.Files.WriteAllText(architect, "operator edit that uninstall must keep\n");

        CommandExecution uninstall = Uninstall(anchor, global: true);

        AssertExit(uninstall, 0);
        Assert.True(
            _state.Files.FileExists(architect),
            "The scenario requires the edited file to survive, so the receipt is retained.");
        Assert.True(
            GlobalReceiptText().Contains(SquadResponseGuidanceDeployer.FileName, StringComparison.Ordinal),
            "A retained receipt must keep the guidance ownership record, or the file it describes is " +
            "left on disk unowned.");
        Assert.Equal(guidanceBefore, _state.Files.ReadAllBytes(guidance));
    }

    #endregion


    #region The home lease

    /// <summary>
    ///     Two updates can overlap, and the deployment lease is what keeps them apart. An update that cannot
    ///     take it has to have written nothing: guidance written ahead of the lease is replaced by whichever
    ///     run loses the race, under a receipt the winner commits for different bytes, and a later release
    ///     then reads the shipped text as an operator edit. The cross-process adapter of the lease is proven by
    ///     <c>MutexLeaseContractTests</c>; here the in-process fake holds it.
    /// </summary>
    [Fact]
    public void UpdateGlobalThatCannotTakeTheHomeLeaseLeavesTheResponseGuidanceUntouched()
    {
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        AssertExit(DeployGuidance(anchor), 0);

        // An earlier release's default, recorded as what Squad deployed: the file a run that reached it
        // would replace. The clock is advanced past its last write, so a rewrite of any bytes shows.
        string path = GuidancePath();
        byte[] current = _state.Files.ReadAllBytes(path);
        byte[] superseded = Encoding.UTF8.GetBytes(
            "Response guidance from an earlier Kyber-Weave release, which the current release supersedes.\n");
        RewriteRecordedDigest(current, superseded);
        _state.Files.WriteAllBytes(path, superseded);
        DateTime stamped = _state.Files.GetLastWriteTimeUtc(path);
        _state.Time.Advance(TimeSpan.FromHours(1));

        CommandExecution refused;
        using (HoldHomeLease())
        {
            refused = DeployGuidance(anchor);
        }

        AssertExit(refused, 1);
        Assert.Contains("transaction is active", refused.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            superseded.AsSpan().SequenceEqual(_state.Files.ReadAllBytes(path)),
            $"An update that could not take the lease must not have replaced '{path}'.");
        Assert.Equal(stamped, _state.Files.GetLastWriteTimeUtc(path));

        // The refusal costs nothing: with the lease free the same run succeeds and refreshes the file.
        CommandExecution retried = DeployGuidance(anchor);

        AssertExit(retried, 0);
        Assert.True(
            current.AsSpan().SequenceEqual(_state.Files.ReadAllBytes(path)),
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        SquadDeploymentPlan plan = PlanGlobalReceiptRewrite();
        SquadHomeFile record = new(SquadResponseGuidanceDeployer.FileName, new string('a', 64));
        bool ran = false;

        _state.Transaction().Execute(plan, leasedPlan =>
        {
            ran = true;
            _ = Assert.Throws<LeaseTimeoutException>(() => HoldHomeLease().Dispose());
            leasedPlan.AttachHomeFiles([record]);
        });

        Assert.True(ran, "The transaction must run the home-file callback.");
        SquadReceipt committed = _state.Store.ReadReceipt(_state.UserPaths.HomeDirectory, SquadDeploymentScope.Global)
                                 ?? throw new InvalidOperationException("The transaction must leave a global receipt.");
        Assert.Contains(
            committed.HomeFiles,
            file => file.RelativePath == record.RelativePath && file.Sha256 == record.Sha256);
        using (HoldHomeLease())
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
        string anchor = CreateDirectory("anchor");
        AssertExit(InstallGlobal(anchor), 0);
        byte[] receiptBefore = _state.Files.ReadAllBytes(RequireFile(GlobalReceiptPath()));
        SquadDeploymentPlan plan = PlanGlobalReceiptRewrite();

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            _state.Transaction().Execute(
                plan,
                _ => throw new InvalidOperationException("the callback broke its contract")));

        Assert.Equal("the callback broke its contract", failure.Message);
        Assert.True(
            receiptBefore.AsSpan().SequenceEqual(_state.Files.ReadAllBytes(GlobalReceiptPath())),
            "A callback that failed before the commit must leave the receipt exactly as it was.");

        // The next run neither finds an unfinished transaction nor a lease still held.
        _state.Transaction().Execute(PlanGlobalReceiptRewrite());
    }

    #endregion

    #region Fixtures

    private CommandExecution DeployGuidance(string anchor) =>
        Capture(() => UpdateCommand(new RecordingProjectPicker(NoProjects), false).Execute(null!, GlobalUpdate(anchor)));

    private SquadLifecycleResult RunInstall(string anchor)
    {
        SquadLifecycleResult result = SquadTestCommands
            .InMemoryLifecycle(_state, _release, _renderer, _roots)
            .InstallAsync(new SquadInstallRequest(
                anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Codex],
                Version: InstalledVersion))
            .GetAwaiter()
            .GetResult();
        return result;
    }

    /// <summary>The user-home guidance file, resolved from the fake home the fixture owns.</summary>
    private string GuidancePath() =>
        Path.Combine(_state.UserPaths.HomeDirectory, ".kyber-weave", SquadResponseGuidanceDeployer.FileName);

    private string GlobalReceiptText() => _state.Files.ReadAllText(RequireFile(GlobalReceiptPath()));

    /// <summary>
    ///     Rewrites the recorded SHA-256 of the deployed guidance across the home state, so the superseded
    ///     bytes look like a default this release replaced rather than a user edit. The receipt is the whole
    ///     ownership record, so replacing the digest there is the same state an older release leaves behind.
    /// </summary>
    private void RewriteRecordedDigest(byte[] deployed, byte[] superseded)
    {
        string before = Convert.ToHexStringLower(SHA256.HashData(deployed));
        string after = Convert.ToHexStringLower(SHA256.HashData(superseded));
        string stateRoot = Path.Combine(_state.UserPaths.HomeDirectory, ".kyber-weave");
        bool rewritten = false;
        foreach (string file in _state.Files.EnumerateFiles(stateRoot, "*", recursive: true).ToList())
        {
            string text = _state.Files.ReadAllText(file);
            if (!text.Contains(before, StringComparison.OrdinalIgnoreCase))
                continue;

            _state.Files.WriteAllText(file, text.Replace(before, after, StringComparison.OrdinalIgnoreCase));
            rewritten = true;
        }

        Assert.True(
            rewritten,
            $"Expected the home state to record the SHA-256 of the deployed response guidance ('{before}').");
    }

    /// <summary>
    ///     Puts the home back in the state a fresh install used to leave: no response guidance file and no
    ///     ownership record for it. Global install creates the file, so a scenario that needs the guidance
    ///     absent removes both the file and its receipt record itself.
    /// </summary>
    private void ClearResponseGuidanceToAbsentAndUnowned()
    {
        string path = GuidancePath();
        if (_state.Files.FileExists(path))
            _state.Files.DeleteFile(path);

        string receiptPath = RequireFile(GlobalReceiptPath());
        JsonNode root = JsonNode.Parse(_state.Files.ReadAllText(receiptPath))
                        ?? throw new InvalidDataException("The global receipt must parse as JSON.");
        if (root["homeFiles"] is JsonArray homeFiles)
        {
            for (int index = homeFiles.Count - 1; index >= 0; index--)
            {
                if (homeFiles[index]?["relativePath"]?.GetValue<string>() == SquadResponseGuidanceDeployer.FileName)
                    homeFiles.RemoveAt(index);
            }
        }

        _state.Files.WriteAllText(
            receiptPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Assert.False(
            _state.Files.FileExists(path),
            $"The scenario requires no guidance at '{path}' beforehand.");
    }

    private void RewriteGlobalReceiptWithoutGuidance()
    {
        _state.Transaction().Execute(PlanGlobalReceiptRewrite());
    }

    /// <summary>A plan that re-plans the files already deployed over the global receipt, ready to commit.</summary>
    private SquadDeploymentPlan PlanGlobalReceiptRewrite()
    {
        string home = _state.UserPaths.HomeDirectory;
        SquadReceipt active = _state.Store.ReadReceipt(home, SquadDeploymentScope.Global)
                              ?? throw new InvalidOperationException("The scenario requires a global receipt.");
        List<SquadDeploymentFile> deployed = [.. active.Files.Select(file => new SquadDeploymentFile(
            file.RelativePath,
            _state.Files.ReadAllBytes(SquadDeploymentPlanRules.ResolveOwnedFilePath(
                active,
                home,
                _roots,
                file,
                _state.Semantics)),
            file.Target))];

        return _state.Planner.CreateUpdate(
            home,
            SquadDeploymentScope.Global,
            LockFor(UpdatedVersion),
            deployed,
            active,
            [],
            true,
            _state.Time,
            _roots);
    }

    /// <summary>
    ///     Copies the shipped Squad source beside a solution marker in the working directory, so doctor's global
    ///     collision scan finds and loads a canonical source.
    /// </summary>
    private string PrepareCanonicalSource()
    {
        _state.Files.WriteAllText(
            Path.Combine(_work, "KyberWeave.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00");
        return CanonicalSquadTree.CopyInto(_state.Files, Path.Combine(_work, "products", "kyber-squad"));
    }

    /// <summary>
    ///     Copies the canonical corpus beside a solution marker with every response-preferences block removed,
    ///     so the render carries no block anywhere. The precondition assertion keeps the test from passing
    ///     vacuously if the canonical blocks ever move.
    /// </summary>
    private void WriteCanonicalSourceWithoutResponseBlocks()
    {
        string destination = PrepareCanonicalSource();
        Regex block = ResponsePreferencesBlock();
        int blocksBefore = 0;
        foreach (string file in _state.Files.EnumerateFiles(destination, "*.md", recursive: true).ToList())
        {
            string text = _state.Files.ReadAllText(file);
            blocksBefore += block.Count(text);
            _state.Files.WriteAllText(file, block.Replace(text, string.Empty));
        }

        Assert.True(
            blocksBefore > 0,
            "The canonical corpus must carry response-preferences blocks for this test to mean anything.");
        foreach (string file in _state.Files.EnumerateFiles(destination, "*.md", recursive: true))
            Assert.Equal(0, block.Count(_state.Files.ReadAllText(file)));
    }

    private static Regex ResponsePreferencesBlock() =>
        new("<response-preferences>.*?</response-preferences>", RegexOptions.Singleline);

    private static string DescribeFailure(SquadLifecycleResult result) =>
        result.Errors is null ? "The lifecycle reported failure." : string.Join("; ", result.Errors);


    /// <summary>Holds the home deployment lease through the in-process lease host, so a run contends with it.</summary>
    private IDisposable HoldHomeLease() =>
        new InProcessLease(_state.LeaseHost).Acquire(
            SquadLeaseNames.ForRoot(_state.Store.ResolveLeaseIdentity(
                _state.UserPaths.HomeDirectory,
                SquadDeploymentScope.Global)),
            TimeSpan.Zero);

    private SquadUpdateCommand UpdateCommand(
        ISquadProjectPicker picker,
        bool isInteractive,
        FakeSquadRenderer? renderer = null,
        Func<string, bool>? readAnswer = null,
        ISquadReleaseSource? releaseSource = null)
    {
        return SquadTestCommands.InMemoryUpdate(
            _state,
            releaseSource ?? _release,
            renderer ?? _renderer,
            globalRoots: _roots,
            isInteractive: isInteractive,
            readAnswer: readAnswer ?? (_ => true),
            configs: _configs,
            picker: picker);
    }

    private CommandExecution InstallGlobal(string anchor) =>
        Capture(() => SquadTestCommands.InMemoryInstall(
                _state,
                _release,
                _renderer,
                globalRoots: _roots,
                configs: _configs)
            .Execute(null!, new SquadInstallSettings
            {
                Path = anchor,
                Global = true,
                Targets = ["codex"],
                Version = InstalledVersion,
                Yes = true
            }));

    private Project InstallProject(string name)
    {
        string root = Path.Combine(_work, "projects", name);
        _state.Files.CreateDirectory(root);
        string physical = _state.Layout.PhysicalOf(root);
        AssertExit(
            Capture(() => SquadTestCommands.InMemoryInstall(
                    _state,
                    _release,
                    _renderer,
                    globalRoots: _roots,
                    configs: _configs)
                .Execute(null!, new SquadInstallSettings
                {
                    Path = physical,
                    Global = false,
                    Targets = ["codex"],
                    Version = InstalledVersion,
                    Yes = true
                })),
            0);
        return new Project(
            physical,
            _state.Store.ResolveLockPath(physical, SquadDeploymentScope.Project),
            _state.Store.ResolveReceiptPath(physical, SquadDeploymentScope.Project));
    }

    /// <summary>
    ///     Builds the registry the global CLI tests run against: a global install, two live projects
    ///     and one project whose root is then removed, so its registry entry is stale.
    /// </summary>
    private Fleet ArrangeFleet()
    {
        string anchor = CreateDirectory("global-anchor");
        AssertExit(InstallGlobal(anchor), 0);
        Project alpha = InstallProject("alpha");
        Project beta = InstallProject("beta");
        Project stale = InstallProject("stale");
        _state.Files.DeleteTreeNoFollow(stale.Root);
        return new Fleet(
            anchor,
            _renderer,
            alpha,
            beta,
            stale,
            (picker, isInteractive, readAnswer, releaseSource) =>
                UpdateCommand(picker, isInteractive, null, readAnswer, releaseSource));
    }

    private CommandExecution Uninstall(string path, bool global) =>
        Capture(() => SquadTestCommands.InMemoryUninstall(_state, _release, _renderer, _roots)
            .Execute(null!, new SquadUninstallSettings { PathOption = path, Global = global, Yes = true }));

    private CommandExecution Status(string path, bool global) =>
        Capture(() => SquadTestCommands.InMemoryStatus(_state, _roots)
            .Execute(null!, new SquadStatusSettings { Path = path, Global = global }));

    private CommandExecution Doctor(string path, ISquadRenderer? renderer) =>
        Capture(() => SquadTestCommands.InMemoryDoctor(
                _state,
                new FakeProcessExecutor(),
                workingDirectory: path,
                globalRoots: _roots,
                renderer: renderer,
                configs: _configs)
            .Execute(null!, new SquadDoctorSettings { Path = path, Global = true }));

    private string CreateDirectory(string name)
    {
        string path = Path.Combine(_work, name);
        _state.Files.CreateDirectory(path);
        return path;
    }

    private void WriteHomeConfig(string yaml)
    {
        string path = UserHostConfigPath.Of(_state.UserPaths.HomeDirectory);
        _state.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _state.Files.WriteAllText(path, yaml);
    }

    /// <summary>
    ///     Writes a legacy per-user global root: the receipt, lock and one owned file a pre-home install left
    ///     beneath the application-data directory, keyed by a name no registered project answers to, so the
    ///     migration cannot move it.
    /// </summary>
    private void WriteLegacyGlobalRoot(string name, string relativeFile, string content)
    {
        string directory = Path.Combine(
            _state.UserPaths.ApplicationDataDirectory,
            "KyberWeave",
            "squad",
            "roots",
            name);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        string receipt = _state.Store.SerializeReceipt(new SquadReceipt(
            SquadStateSchemas.ReceiptSchemaV1,
            SquadDeploymentScope.Global,
            ".",
            SquadStateTestLayout.InstalledAt,
            [],
            [new SquadOwnedFile(relativeFile, Digest(bytes), "codex", Adopted: false)]));
        _state.Files.CreateDirectory(directory);
        _state.Files.WriteAllText(Path.Combine(directory, "squad.receipt.json"), receipt);
        _state.Files.WriteAllText(
            Path.Combine(directory, "squad.lock.yml"),
            _state.Store.SerializeLock(LockFor(InstalledVersion)));
        string legacyFile = Path.Combine(directory, relativeFile.Replace('/', Path.DirectorySeparatorChar));
        _state.Files.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
        _state.Files.WriteAllBytes(legacyFile, bytes);
    }

    /// <summary>
    ///     The pre-home layout: a receipt and lock under <c>.kyber-weave/</c> inside the project. The
    ///     migration imports it into the registry and leaves it byte-identical, so doctor must say it is kept.
    /// </summary>
    private void WriteInRepoLegacyState(string root)
    {
        string stateDirectory = Path.Combine(root, ".kyber-weave");
        _state.Files.CreateDirectory(stateDirectory);
        _state.Files.WriteAllText(
            Path.Combine(stateDirectory, "squad.receipt.json"),
            _state.Store.SerializeReceipt(SquadStateTestLayout.ProjectReceipt()));
        _state.Files.WriteAllText(
            Path.Combine(stateDirectory, "squad.lock.yml"),
            _state.Store.SerializeLock(LockFor(InstalledVersion)));
    }

    /// <summary>
    ///     Moves the home global receipt and lock into the legacy per-user root, the layout a pre-home install left
    ///     behind, so the next migration has a legacy copy to copy. The installed files stay where they were.
    /// </summary>
    private void MoveGlobalStateToLegacyRoot(string anchor)
    {
        string legacyDirectory = _state.Layout.LegacyGlobalDirectory(anchor);
        _state.Files.CreateDirectory(legacyDirectory);
        _state.Files.MoveFile(GlobalReceiptPath(), Path.Combine(legacyDirectory, "squad.receipt.json"));
        _state.Files.MoveFile(GlobalLockPath(), Path.Combine(legacyDirectory, "squad.lock.yml"));
        _state.Files.DeleteTreeNoFollow(GlobalStateDirectory());
    }

    /// <summary>
    ///     Moves a project's home state into the repository, the layout a pre-home install left behind, so the next
    ///     touch imports it. The project's installed files stay where they were.
    /// </summary>
    private void MoveProjectStateInRepo(Project project)
    {
        string inRepo = Path.Combine(project.Root, ".kyber-weave");
        _state.Files.CreateDirectory(inRepo);
        _state.Files.MoveFile(project.ReceiptPath, Path.Combine(inRepo, "squad.receipt.json"));
        _state.Files.MoveFile(project.LockPath, Path.Combine(inRepo, "squad.lock.yml"));
        _state.Files.DeleteTreeNoFollow(Path.GetDirectoryName(project.ReceiptPath)!);
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

    private string GlobalStateDirectory() =>
        Path.Combine(_state.UserPaths.HomeDirectory, ".kyber-weave", "squad", "global");

    private string GlobalLockPath() =>
        Path.Combine(GlobalStateDirectory(), "squad.lock.yml");

    private string GlobalReceiptPath() =>
        Path.Combine(GlobalStateDirectory(), "squad.receipt.json");

    private string RegistryPath() =>
        Path.Combine(_state.UserPaths.HomeDirectory, ".kyber-weave", "squad", "registry.json");

    private string InstalledFile(SquadTarget target, string fileName)
    {
        string root = _roots.ResolveGlobalRoot(target);
        string[] matches = [.. _state.Files.EnumerateFiles(root, fileName, recursive: true)];
        Assert.True(
            matches.Length == 1,
            $"Expected one installed '{fileName}' beneath {root}; found {matches.Length}.");
        return matches[0];
    }

    /// <summary>Reads the <c>squad-version</c> scalar from a lock file, without assuming how the store is wired.</summary>
    private string VersionOf(string lockPath)
    {
        const string prefix = "squad-version: ";
        string text = _state.Files.ReadAllText(RequireFile(lockPath));
        string? line = text.Split('\n')
            .FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        Assert.NotNull(line);
        return line[prefix.Length..].Trim().Trim('"');
    }

    /// <summary>Reads state before a run, or null when it is absent, so the run's own assertions go first.</summary>
    private byte[]? Snapshot(string path) =>
        _state.Files.FileExists(path) ? _state.Files.ReadAllBytes(path) : null;

    /// <summary>
    ///     Every file beneath a root as one <c>relative-path sha256</c> line each, in ordinal order, so two
    ///     captures of the same tree compare as text and a changed or vanished file cannot pass unnoticed.
    /// </summary>
    private string CaptureTree(string root)
    {
        IEnumerable<string> lines = _state.Files
            .EnumerateFiles(root, "*", recursive: true)
            .Select(path =>
                $"{Path.GetRelativePath(root, path)} " +
                Convert.ToHexStringLower(SHA256.HashData(_state.Files.ReadAllBytes(path))))
            .Order(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    /// <summary>Asserts a file keeps the bytes it had before the run, and that it still exists.</summary>
    private void AssertUnchanged(byte[]? before, string path)
    {
        Assert.True(
            before is not null,
            $"Expected state at '{path}' before the run. Squad state must be written beneath the home store.");
        Assert.True(_state.Files.FileExists(path), $"State at '{path}' disappeared during the run.");
        Assert.Equal(before, _state.Files.ReadAllBytes(path));
    }

    private string RequireFile(string path)
    {
        Assert.True(
            _state.Files.FileExists(path),
            $"Expected state at '{path}'. Squad state for this scenario must be written beneath the home store.");
        return path;
    }

    private static string LineContaining(string output, string text)
    {
        string[] lines = output
            .Split('\n')
            .Where(line => line.Contains(text, StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            lines.Length == 1,
            $"Expected exactly one line naming '{text}', found {lines.Length}:{Environment.NewLine}{output}");
        return lines[0];
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

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

    private static IReadOnlyList<string> NoProjects(IReadOnlyList<SquadProjectRegistryEntry> projects) => [];

    private static IReadOnlyList<string> AllNonStale(IReadOnlyList<SquadProjectRegistryEntry> projects) =>
        [.. projects.Where(entry => !entry.Stale).Select(entry => entry.Path)];

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed record Project(string Root, string LockPath, string ReceiptPath);

    private sealed class Fleet(
        string anchor,
        FakeSquadRenderer renderer,
        Project alpha,
        Project beta,
        Project stale,
        Func<ISquadProjectPicker, bool, Func<string, bool>, ISquadReleaseSource?, SquadUpdateCommand> command)
    {
        public string Anchor { get; } = anchor;

        public FakeSquadRenderer Renderer { get; } = renderer;

        public Project Alpha { get; } = alpha;

        public Project Beta { get; } = beta;

        public Project Stale { get; } = stale;

        public SquadUpdateCommand Command(
            ISquadProjectPicker picker,
            bool isInteractive,
            Func<string, bool> readAnswer,
            ISquadReleaseSource? releaseSource = null) =>
            command(picker, isInteractive, readAnswer, releaseSource);
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

    #endregion
}
