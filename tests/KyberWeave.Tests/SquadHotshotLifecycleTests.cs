using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>Pins roster-removal behavior through the normal Squad update lifecycle.</summary>
public sealed class SquadHotshotLifecycleTests : IDisposable
{
    private const string ObsoletePath = ".github/agents/obsolete.agent.md";
    private const string EditedPath = ".github/agents/architect.agent.md";
    private const string UnmanagedPath = ".github/agents/local-only.agent.md";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A roster shrink must remove only an unchanged file owned by the prior receipt. A locally
    /// edited owned file remains an operator conflict, and an unmanaged neighbor remains outside
    /// Squad ownership, while the replacement receipt names the exact 48-file golden render.
    /// </summary>
    [Fact]
    public async Task UpdateToGoldenRosterRemovesOnlyUnchangedObsoleteOwnedFileAndWritesExactReceipt()
    {
        string[] goldenPaths = ReadGoldenPaths();
        string targetRoot = Path.Combine(_temp.Path, "hotshot-update");
        Directory.CreateDirectory(targetRoot);
        SquadStateStore stateStore = new(new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-data")));
        using FakeSquadReleaseSource releaseSource = new();
        RosterShrinkingRenderer renderer = new(goldenPaths);
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadLifecycleResult install = await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Copilot],
            Version: "1.0.0"));
        Assert.True(install.Success);

        string obsoleteFile = ResolveTargetPath(targetRoot, ObsoletePath);
        Assert.True(File.Exists(obsoleteFile));
        SquadReceipt priorReceipt = Assert.IsType<SquadReceipt>(
            stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));
        Assert.Contains(priorReceipt.Files, file =>
            string.Equals(file.RelativePath, ObsoletePath, StringComparison.Ordinal));
        Assert.Contains(priorReceipt.Files, file =>
            string.Equals(file.RelativePath, EditedPath, StringComparison.Ordinal));

        string editedFile = ResolveTargetPath(targetRoot, EditedPath);
        string editedBytes = await File.ReadAllTextAsync(editedFile) + "operator edit\n";
        await File.WriteAllTextAsync(editedFile, editedBytes);
        string unmanagedFile = ResolveTargetPath(targetRoot, UnmanagedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(unmanagedFile)!);
        await File.WriteAllTextAsync(unmanagedFile, "unmanaged neighbor\n");

        SquadLifecycleResult update = await service.UpdateAsync(new SquadUpdateRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Version: "1.1.0",
            ReplaceManaged: false));

        Assert.True(update.Success);
        Assert.False(File.Exists(obsoleteFile));
        Assert.Equal(editedBytes, await File.ReadAllTextAsync(editedFile));
        Assert.Equal("unmanaged neighbor\n", await File.ReadAllTextAsync(unmanagedFile));
        SquadReceipt persistedReceipt = Assert.IsType<SquadReceipt>(
            stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));
        string[] persistedPaths = persistedReceipt.Files
            .Select(file => file.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(goldenPaths, persistedPaths);
        Assert.Equal(48, persistedPaths.Length);
        Assert.Equal(persistedPaths.Length, persistedPaths.Distinct(StringComparer.Ordinal).Count());
    }

    private static string[] ReadGoldenPaths()
    {
        string path = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "tests",
            "KyberWeave.Tests",
            "Fixtures",
            "kyber-squad-hotshot-golden.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        return root.GetProperty("agents").EnumerateArray()
            .Concat(root.GetProperty("skills").EnumerateArray())
            .Select(entry => entry.GetProperty("path").GetString() ?? throw new InvalidDataException("Golden path is not a string."))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveTargetPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private sealed class RosterShrinkingRenderer(IReadOnlyList<string> goldenPaths) : ISquadRenderer
    {
        private int _renderCount;

        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Copilot];

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            _renderCount++;
            List<string> paths = [.. goldenPaths];
            if (_renderCount == 1)
            {
                paths.Add(ObsoletePath);
            }

            SquadDeploymentFile[] files = paths
                .Order(StringComparer.Ordinal)
                .Select(path => new SquadDeploymentFile(
                    path,
                    Encoding.UTF8.GetBytes($"render {_renderCount}: {path}\n"),
                    "copilot"))
                .ToArray();
            return Task.FromResult(new SquadRenderResult(
                Success: true,
                Files: files,
                Degradations: [],
                Warnings: [],
                Errors: []));
        }
    }
}
