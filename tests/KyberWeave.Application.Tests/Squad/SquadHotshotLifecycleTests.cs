using KyberWeave.Testing.Squad;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>Pins roster-removal behavior through the normal Squad update lifecycle.</summary>
/// <remarks>
/// The prior deployment the update shrinks is a real install of the roster that still holds the obsolete
/// file, run over the in-memory state; the update then runs for real on top of it. The subject is that
/// sequence: an edited owned file and an unmanaged neighbor already present when the roster shrinks.
/// </remarks>
public sealed class SquadHotshotLifecycleTests
{
    private const string ObsoletePath = ".github/agents/obsolete.agent.md";
    private const string EditedPath = ".github/agents/architect.agent.md";
    private const string UnmanagedPath = ".github/agents/local-only.agent.md";

    private static readonly string[] RosterPaths =
    [
        ".github/agents/architect.agent.md",
        ".github/agents/conductor.agent.md",
        ".github/agents/task-reviewer.agent.md",
        ".github/skills/code-review/SKILL.md",
        ".github/skills/product-owner/SKILL.md"
    ];

    private readonly InMemorySquadState _state = new();

    /// <summary>
    /// A roster shrink must remove only an unchanged file owned by the prior receipt. A locally
    /// edited owned file remains an operator conflict, and an unmanaged neighbor remains outside
    /// Squad ownership, while the replacement receipt names the exact shrunken roster.
    /// </summary>
    [Fact]
    public async Task UpdateToShrunkenRosterRemovesOnlyUnchangedObsoleteOwnedFileAndWritesExactReceipt()
    {
        string targetRoot = Path.Combine(_state.Files.Root, "work", "hotshot-update");
        _state.Files.CreateDirectory(targetRoot);
        await InstallPriorAsync(targetRoot);

        string obsoleteFile = ResolveTargetPath(targetRoot, ObsoletePath);
        Assert.True(_state.Files.FileExists(obsoleteFile));
        SquadStateStore stateStore = _state.Store;
        SquadReceipt priorReceipt = Assert.IsType<SquadReceipt>(
            stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));
        Assert.Contains(priorReceipt.Files, file =>
            string.Equals(file.RelativePath, ObsoletePath, StringComparison.Ordinal));
        Assert.Contains(priorReceipt.Files, file =>
            string.Equals(file.RelativePath, EditedPath, StringComparison.Ordinal));

        string editedFile = ResolveTargetPath(targetRoot, EditedPath);
        string editedBytes = _state.Files.ReadAllText(editedFile) + "operator edit\n";
        _state.Files.WriteAllText(editedFile, editedBytes);
        string unmanagedFile = ResolveTargetPath(targetRoot, UnmanagedPath);
        _state.Files.CreateDirectory(Path.GetDirectoryName(unmanagedFile)!);
        _state.Files.WriteAllText(unmanagedFile, "unmanaged neighbor\n");

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(
            _state,
            releaseSource,
            RosterRenderer(includeObsolete: false));

        SquadLifecycleResult update = await service.UpdateAsync(new SquadUpdateRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Version: "1.1.0",
            ReplaceManaged: false));

        Assert.True(update.Success);
        Assert.False(_state.Files.FileExists(obsoleteFile));
        Assert.Equal(editedBytes, _state.Files.ReadAllText(editedFile));
        Assert.Equal("unmanaged neighbor\n", _state.Files.ReadAllText(unmanagedFile));
        SquadReceipt persistedReceipt = Assert.IsType<SquadReceipt>(
            stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));
        string[] persistedPaths = persistedReceipt.Files
            .Select(file => file.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(RosterPaths, persistedPaths);
        Assert.Equal(5, persistedPaths.Length);
        Assert.Equal(persistedPaths.Length, persistedPaths.Distinct(StringComparer.Ordinal).Count());
    }

    private string ResolveTargetPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The prior install the test starts from: the roster that still holds the obsolete file, installed
    /// with the class's renderer.
    /// </summary>
    private async Task InstallPriorAsync(string targetRoot)
    {
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(
            _state,
            releaseSource,
            RosterRenderer(includeObsolete: true));

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Copilot],
            Version: "1.0.0"));
        Assert.True(result.Success, $"The prior install failed: {string.Join("; ", result.Errors ?? [])}");
    }

    /// <summary>
    /// Renders the roster paths with content the receipt can plan against, adding the obsolete path an
    /// older release wrote so an update can remove it.
    /// </summary>
    private static FakeSquadRenderer RosterRenderer(bool includeObsolete) =>
        FakeSquadRenderer.Responding(
            _ => new SquadRenderResult(
                true,
                [.. RosterPaths
                    .Concat(includeObsolete ? [ObsoletePath] : [])
                    .Order(StringComparer.Ordinal)
                    .Select(path => new SquadDeploymentFile(
                        path,
                        Encoding.UTF8.GetBytes($"render: {path}\n"),
                        "copilot"))],
                [],
                [],
                []),
            SquadTarget.Copilot);
}
