using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>Pins roster-removal behavior through the normal Squad update lifecycle.</summary>
/// <remarks>
/// The prior deployment the update shrinks comes from <see cref="PreparedSquadInstalls"/>, built once for the
/// run and copied into this test's own project and home. The update itself runs for real, because the
/// subject is that sequence: an edited owned file and an unmanaged neighbor already on disk when the
/// roster shrinks.
/// </remarks>
public sealed class SquadHotshotLifecycleTests : IDisposable
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

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A roster shrink must remove only an unchanged file owned by the prior receipt. A locally
    /// edited owned file remains an operator conflict, and an unmanaged neighbor remains outside
    /// Squad ownership, while the replacement receipt names the exact shrunken roster.
    /// </summary>
    [Fact]
    public async Task UpdateToShrunkenRosterRemovesOnlyUnchangedObsoleteOwnedFileAndWritesExactReceipt()
    {
        string targetRoot = Path.Combine(_temp.Path, "hotshot-update");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        PreparedHotshotInstall prior = PriorInstall();
        prior.CopyInto(userPaths, targetRoot);
        Assert.Equal(0, prior.Record.ExitCode);

        string obsoleteFile = ResolveTargetPath(targetRoot, ObsoletePath);
        Assert.True(File.Exists(obsoleteFile));
        SquadStateStore stateStore = new(userPaths);
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

        using FakeSquadReleaseSource releaseSource = new();
        SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(
            releaseSource,
            new RosterShrinkingRenderer(RosterPaths, includeObsolete: false),
            stateStore);

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
        Assert.Equal(RosterPaths, persistedPaths);
        Assert.Equal(5, persistedPaths.Length);
        Assert.Equal(persistedPaths.Length, persistedPaths.Distinct(StringComparer.Ordinal).Count());
    }

    private static string ResolveTargetPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string RecipeKey => SquadInstallRecipe.Key(
        "squad-hotshot-lifecycle",
        SquadDeploymentScope.Project,
        [SquadTarget.Copilot],
        ("version", "1.0.0"),
        ("roster", string.Join(",", RosterPaths)),
        ("obsolete", ObsoletePath));

    private static PreparedHotshotInstall PriorInstall() =>
        PreparedSquadInstalls.Get(RecipeKey, () => BuildPriorInstall(RecipeKey));

    /// <summary>
    /// The one real prior install the class starts from: the roster that still holds the obsolete file,
    /// made with the class's renderer once for the run.
    /// </summary>
    private static PreparedHotshotInstall BuildPriorInstall(string key)
    {
        TempDirectory? directory = new();
        try
        {
            string projectRoot = Path.Combine(directory.Path, "project");
            Directory.CreateDirectory(projectRoot);
            FakeSquadUserPaths userPaths = new(Path.Combine(directory.Path, "user-data"));
            SquadStateStore stateStore = new(userPaths);
            using FakeSquadReleaseSource releaseSource = new();
            SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(
                releaseSource,
                new RosterShrinkingRenderer(RosterPaths, includeObsolete: true),
                stateStore);

            SquadLifecycleResult result = service.InstallAsync(new SquadInstallRequest(
                    TargetRoot: projectRoot,
                    Scope: SquadDeploymentScope.Project,
                    Targets: [SquadTarget.Copilot],
                    Version: "1.0.0"))
                .GetAwaiter().GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"The prepared prior install failed: {string.Join("; ", result.Errors ?? [])}");
            }

            PreparedHotshotInstall prepared = new(
                key,
                directory,
                projectRoot,
                userPaths.HomeDirectory,
                SquadInstallRecord.FromLifecycle(result, exitCode: 0));
            directory = null;
            return prepared;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    /// <summary>
    /// The shared prior install, copied into a test's own project and fake home as if the test had
    /// installed it there: the project tree, then the project's home state directory keyed to the copy's
    /// own physical root, with the receipt's recorded root rewritten.
    /// </summary>
    private sealed class PreparedHotshotInstall(
        string key,
        TempDirectory directory,
        string projectRoot,
        string homeDirectory,
        SquadInstallRecord record) : PreparedSquadInstall(key, directory, record)
    {
        public void CopyInto(FakeSquadUserPaths userPaths, string targetRoot)
        {
            ArgumentNullException.ThrowIfNull(userPaths);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

            SquadInstallCopy.Tree(projectRoot, targetRoot);

            string templateRoot = SquadInstallCopy.Canonical(projectRoot);
            string copyRoot = SquadInstallCopy.Canonical(targetRoot);
            string sourceState = ProjectStateDirectory(homeDirectory, templateRoot);
            string copyState = ProjectStateDirectory(userPaths.HomeDirectory, copyRoot);
            SquadInstallCopy.Tree(sourceState, copyState);
            SquadInstallCopy.ReRoot(copyState, templateRoot, copyRoot);
        }

        private static string ProjectStateDirectory(string home, string canonicalRoot) =>
            Path.Combine(
                home,
                ".kyber-weave",
                "squad",
                "projects",
                SquadPhysicalRootIdentity.KeyOf(canonicalRoot));
    }

    private sealed class RosterShrinkingRenderer(IReadOnlyList<string> rosterPaths, bool includeObsolete)
        : ISquadRenderer
    {
        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Copilot];

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            List<string> paths = [.. rosterPaths];
            if (includeObsolete)
            {
                paths.Add(ObsoletePath);
            }

            SquadDeploymentFile[] files = paths
                .Order(StringComparer.Ordinal)
                .Select(path => new SquadDeploymentFile(
                    path,
                    Encoding.UTF8.GetBytes($"render: {path}\n"),
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
