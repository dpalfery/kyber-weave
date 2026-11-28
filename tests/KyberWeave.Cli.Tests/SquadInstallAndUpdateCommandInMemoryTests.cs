using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What <c>squad install</c> and <c>squad update</c> do over an in-memory Squad state: fail closed when the
/// release cannot be obtained, install through injected collaborators, plan a dry run, refuse an unknown
/// target or exclusion, refuse a targetless non-interactive run, and reconcile an explicit target set into
/// the receipt, the lock and the render request. No test here touches the disk or the network.
/// </summary>
public sealed class SquadInstallAndUpdateCommandInMemoryTests : IDisposable
{
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly InMemorySquadState _state = new();
    private readonly SquadConsole _console;
    private readonly FakeSquadRenderer _renderer = new();

    public SquadInstallAndUpdateCommandInMemoryTests()
    {
        _ansi.Profile.Width = 200;
        _console = new SquadConsole(_ansi, new FakeConsole());
    }

    public void Dispose() => _ansi.Dispose();

    [Fact]
    public async Task Install_WhenToolchainUnreleased_FailsClosedWithExitOne()
    {
        string targetDir = CreateRoot("install-unreleased");
        FakeSquadReleaseSource release = FakeSquadReleaseSource.Failing(
            new InvalidOperationException("No validated release is available for this toolchain."));

        int exitCode = await SquadTestCommands.InMemoryInstall(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false
            },
            CancellationToken.None);

        string output = Output();
        Assert.Equal(1, exitCode);
        Assert.Contains("error", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(_state.Files.DirectoryExists(Path.Combine(targetDir, ".kyber-weave")));
    }

    [Fact]
    public async Task Install_WhenCollaboratorsInjected_SucceedsWithExitZero()
    {
        string targetDir = CreateRoot("install-success");
        FakeSquadReleaseSource release = new() { Files = _state.Files };

        int exitCode = await SquadTestCommands.InMemoryInstall(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false
            },
            CancellationToken.None);

        string homeState = _state.Layout.HomeProjectDirectory(targetDir);
        Assert.Equal(0, exitCode);
        Assert.Contains("Successfully installed", Output(), StringComparison.OrdinalIgnoreCase);
        Assert.True(_state.Files.DirectoryExists(homeState));
        Assert.True(_state.Files.FileExists(Path.Combine(homeState, "squad.lock.yml")));
        Assert.True(_state.Files.FileExists(Path.Combine(homeState, "squad.receipt.json")));
    }

    [Fact]
    public async Task Install_DryRun_ReportsPlannedFilesWithoutModifyingFilesystem()
    {
        string targetDir = CreateRoot("install-dry-run");
        FakeSquadReleaseSource release = new() { Files = _state.Files };

        int exitCode = await SquadTestCommands.InMemoryInstall(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = true,
                Adopt = false
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Dry-run", Output(), StringComparison.OrdinalIgnoreCase);
        Assert.False(_state.Files.DirectoryExists(Path.Combine(targetDir, ".kyber-weave")));
    }

    [Theory]
    [InlineData("invalid-target")]
    [InlineData("codex,unknown-target")]
    public async Task Install_InvalidTargetToken_ExitsTwo(string target)
    {
        string targetDir = CreateRoot("install-invalid-target");
        FakeSquadReleaseSource release = new() { Files = _state.Files };

        int exitCode = await SquadTestCommands.InMemoryInstall(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = [target]
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown Squad target", Output(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("invalid-exclude")]
    [InlineData("cursor,bad-exclude")]
    public async Task Install_InvalidExcludeToken_ExitsTwo(string exclusion)
    {
        string targetDir = CreateRoot("install-invalid-exclude");
        FakeSquadReleaseSource release = new() { Files = _state.Files };

        int exitCode = await SquadTestCommands.InMemoryInstall(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["all"],
                Exclusions = [exclusion]
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown Squad target", Output(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_NoTargetInNonInteractiveTerminal_ExitsTwoWithRecoveryCommand()
    {
        string targetDir = CreateRoot("install-no-target");
        FakeSquadReleaseSource release = new() { Files = _state.Files };

        int exitCode = await SquadTestCommands.InMemoryInstall(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = []
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("kyber-weave squad install --target <target>", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WhenToolchainUnreleased_FailsClosedWithExitOne()
    {
        string targetDir = CreateRoot("update-target");
        SeedProjectDeployment(targetDir, ("agents/architect.md", "You are architect.\nPlan first.\n"));
        FakeSquadReleaseSource release = FakeSquadReleaseSource.Failing(
            new InvalidOperationException("No validated release is available for this toolchain."));

        int exitCode = await SquadTestCommands.InMemoryUpdate(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Global = false,
                DryRun = false,
                ReplaceManaged = false
            },
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("error", Output(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_WhenCollaboratorsInjected_SucceedsWithExitZero()
    {
        string targetDir = CreateRoot("update-success-target");
        SeedProjectDeployment(targetDir, (".codex/agents/architect.toml", "name = \"architect\"\n"));
        FakeSquadReleaseSource release = new() { Files = _state.Files };

        int exitCode = await SquadTestCommands.InMemoryUpdate(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Successfully updated", Output(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Issue 99: updating a codex receipt with explicit <c>codex,claude</c> must render
    /// both harnesses and persist that same ordered set. Leaving the receipt roster in
    /// charge reports success while claude never reaches the lock or the receipt.
    /// </summary>
    [Fact]
    public async Task UpdateExplicitTargetsReconcileRenderedReceiptAndLockTargets()
    {
        string targetDir = CreateRoot("update-explicit-targets");
        SeedProjectDeployment(targetDir, (".codex/agents/architect.toml", "name = \"architect\"\n"));
        FakeSquadReleaseSource release = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();

        // Confirmation is a separate gate from target selection. Pin it closed so a
        // pty cannot turn this regression into a hung prompt.
        int exitCode = await SquadTestCommands.InMemoryUpdate(
            _state,
            release,
            renderer,
            _console,
            isInteractive: false).ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex,claude"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        SquadRenderRequest renderRequest = Assert.Single(renderer.RenderRequests);
        SquadLock? persistedLock = _state.Store.ReadLock(targetDir, SquadDeploymentScope.Project);
        SquadReceipt? persistedReceipt = _state.Store.ReadReceipt(targetDir, SquadDeploymentScope.Project);
        Assert.NotNull(persistedLock);
        Assert.NotNull(persistedReceipt);
        string[] receiptTargets = persistedReceipt.Files
            .Select(file => file.Target)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Multiple(
            () => Assert.Equal([SquadTarget.Codex, SquadTarget.Claude], renderRequest.Targets),
            () => Assert.Equal(["codex", "claude"], persistedLock.Targets),
            () => Assert.Equal(["codex", "claude"], receiptTargets));
    }

    /// <summary>
    /// Issue 99: a dry-run of the same explicit pair must report the two-target deployed
    /// file count. Dry-run planning still has to leave the existing lock and receipt
    /// byte-identical, because the operator has not accepted a write.
    /// </summary>
    [Fact]
    public async Task UpdateExplicitTargetsDryRunReportsDesiredSetWithoutWritingState()
    {
        string targetDir = CreateRoot("update-explicit-targets-dry-run");
        SeedProjectDeployment(targetDir, (".codex/agents/architect.toml", "name = \"architect\"\n"));
        FakeSquadReleaseSource release = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();

        string lockPath = _state.Store.ResolveLockPath(targetDir, SquadDeploymentScope.Project);
        string receiptPath = _state.Store.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project);
        byte[] lockBefore = _state.Files.ReadAllBytes(lockPath);
        byte[] receiptBefore = _state.Files.ReadAllBytes(receiptPath);
        int desiredFileCount = await RenderedDeploymentFileCountAsync(SquadTarget.Codex, SquadTarget.Claude);

        int exitCode = await SquadTestCommands.InMemoryUpdate(
            _state,
            release,
            renderer,
            _console,
            isInteractive: false).ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex,claude"],
                Global = false,
                DryRun = true,
                ReplaceManaged = true
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        string output = Output();
        Assert.Multiple(
            () => Assert.Equal(desiredFileCount, PlannedDeployedFileCount(output)),
            () => Assert.Equal(lockBefore, _state.Files.ReadAllBytes(lockPath)),
            () => Assert.Equal(receiptBefore, _state.Files.ReadAllBytes(receiptPath)));
    }

    [Theory]
    [InlineData("invalid-target")]
    [InlineData("claude,unknown-target")]
    public async Task Update_InvalidTargetToken_ExitsTwo(string target)
    {
        string targetDir = CreateRoot("update-invalid-target");
        FakeSquadReleaseSource release = new() { Files = _state.Files };

        int exitCode = await SquadTestCommands.InMemoryUpdate(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = [target]
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown Squad target", Output(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_EmptyReceiptTargetsInNonInteractiveTerminal_ExitsTwoWithRecoveryCommand()
    {
        string targetDir = CreateRoot("update-empty-receipt");
        FakeSquadReleaseSource release = new() { Files = _state.Files };

        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project),
            _state.Store.SerializeReceipt(new SquadReceipt(
                Schema: "kyber-squad.receipt/v1",
                Scope: SquadDeploymentScope.Project,
                TargetRoot: ".",
                InstalledAtUtc: SquadStateTestLayout.InstalledAt,
                Degradations: [],
                Files: [])));

        int exitCode = await SquadTestCommands.InMemoryUpdate(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("kyber-weave squad update --target <target>", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_MissingReceipt_ExitsOneAndDirectsToInstall()
    {
        string targetDir = CreateRoot("update-no-receipt");
        FakeSquadReleaseSource release = new() { Files = _state.Files };

        int exitCode = await SquadTestCommands.InMemoryUpdate(_state, release, _renderer, _console).ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Global = false
            },
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("squad install", Output(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The dry-run oracle is the file count a renderer emits for the desired pair. A
    /// one-target plan cannot report that count.
    /// </summary>
    private async Task<int> RenderedDeploymentFileCountAsync(params SquadTarget[] targets)
    {
        FakeSquadRenderer renderer = new();
        SquadRenderResult result = await renderer.RenderAsync(
            new SquadRenderRequest(
                SourceDirectory: ".",
                Targets: targets,
                Scope: SquadDeploymentScope.Project).Prepared(_state.Files));
        return result.Files.Count;
    }

    private static int PlannedDeployedFileCount(string output)
    {
        const string marker = "planned ";
        const string trailer = " deployed files";
        int markerIndex = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, output);
        int countStart = markerIndex + marker.Length;
        int trailerIndex = output.IndexOf(trailer, countStart, StringComparison.Ordinal);
        Assert.True(trailerIndex > countStart, output);
        string digits = output[countStart..trailerIndex];
        Assert.True(
            int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int count),
            digits);
        return count;
    }

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string CreateRoot(string name)
    {
        string root = Path.Combine(_state.Files.Root, "projects", name);
        _state.Files.CreateDirectory(root);
        return _state.Layout.PhysicalOf(root);
    }

    private void SeedProjectDeployment(
        string targetRoot,
        params (string RelativePath, string Content)[] files)
    {
        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relativePath, string content) in files)
        {
            string fullPath = Path.Combine(targetRoot, relativePath);
            _state.Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            _state.Files.WriteAllText(fullPath, content);
            ownedFiles.Add(new SquadOwnedFile(relativePath, Digest(content), "codex", Adopted: false));
        }

        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Project),
            _state.Store.SerializeReceipt(new SquadReceipt(
                Schema: "kyber-squad.receipt/v1",
                Scope: SquadDeploymentScope.Project,
                TargetRoot: ".",
                InstalledAtUtc: SquadStateTestLayout.InstalledAt,
                Degradations: [],
                Files: ownedFiles)));
        _state.Layout.WriteText(
            _state.Store.ResolveLockPath(targetRoot, SquadDeploymentScope.Project),
            _state.Store.SerializeLock(SquadStateTestLayout.Lock()));
    }

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
