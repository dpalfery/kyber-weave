using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Which worktree a per-call <c>projectPath</c> names, decided from the path, file, process-environment and git seams
/// alone, and how an unreadable host configuration is reported on the console seam.
/// </summary>
public sealed class DocsRootRegistryTests
{
    private static readonly string Server = Absolute("kw", "server");
    private static readonly string Work = Absolute("kw", "work");
    private static readonly string Common = Absolute("kw", "server", ".git");

    private readonly InMemoryFileSystem _files = new();
    private readonly List<string> _built = [];

    [Fact]
    public void RelativeProjectPathIsResolvedAgainstTheProcessWorkingDirectory()
    {
        string worktree = Path.Combine(Work, "wt");
        _files.CreateDirectory(worktree);
        DocsRootRegistry registry = Registry(new ScriptedGit().Add(Server, Server, Common).Add(worktree, worktree, Common));

        bool resolved = registry.TryResolve("wt", out DocsTools? _, out string? refusal);

        Assert.True(resolved, refusal);
        Assert.Equal([worktree], _built);
    }

    [Fact]
    public void ProjectPathThatIsNotADirectoryIsRefusedWithItsFullPath()
    {
        DocsRootRegistry registry = Registry(new ScriptedGit());

        bool resolved = registry.TryResolve("missing", out DocsTools? tools, out string? refusal);

        Assert.False(resolved);
        Assert.Null(tools);
        Assert.Contains($"projectPath '{Path.Combine(Work, "missing")}' is not a directory", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectPathOfAnotherRepositoryIsRefused()
    {
        string other = Absolute("kw", "other");
        _files.CreateDirectory(other);
        DocsRootRegistry registry = Registry(new ScriptedGit().Add(Server, Server, Common).Add(other, other, Absolute("kw", "other", ".git")));

        bool resolved = registry.TryResolve(other, out _, out string? refusal);

        Assert.False(resolved);
        Assert.Contains("belongs to a different repository", refusal, StringComparison.Ordinal);
        Assert.Empty(_built);
    }

    [Fact]
    public void ServerRootThatIsNotARepositoryRefusesEveryProjectPath()
    {
        string worktree = Path.Combine(Work, "wt");
        _files.CreateDirectory(worktree);
        DocsRootRegistry registry = Registry(new ScriptedGit().Add(worktree, worktree, Common));

        bool resolved = registry.TryResolve(worktree, out _, out string? refusal);

        Assert.False(resolved);
        Assert.Contains("is not a git repository", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperatingSystemKind.Linux, 1)]
    [InlineData(OperatingSystemKind.MacOS, 0)]
    [InlineData(OperatingSystemKind.Windows, 0)]
    public void TopLevelsThatDifferOnlyInCaseAreTheSameRootOnlyWhereTheHostFoldsCase(OperatingSystemKind platform, int toolsBuilt)
    {
        string shouting = Absolute("kw", "SERVER");
        _files.CreateDirectory(shouting);
        DocsRootRegistry registry = Registry(
            new ScriptedGit().Add(Server, Server, Common).Add(shouting, shouting, Common),
            platform);

        bool resolved = registry.TryResolve(shouting, out _, out string? refusal);

        Assert.True(resolved, refusal);
        Assert.Equal(toolsBuilt, _built.Count);
    }

    [Fact]
    public void UnreadableConfigurationIsReportedOnTheConsoleSeamAndTheDefaultOntologyServes()
    {
        FakeKyberWeaveConfigRepository config = new();
        config.WriteRepositoryConfig(Server, "stop-on-tool-failure: true\n");
        using IDisposable failing = config.FailReads(FakeKyberWeaveConfigRepository.RepositoryConfigPath(Server));
        FakeConsole console = new();
        ServiceCollection services = new();
        services.AddSingleton<IKyberWeaveConfigRepository>(config);
        services.AddSingleton<IConsole>(console);
        using ServiceProvider provider = services.BuildServiceProvider();

        OntologyConfig ontology = DocsRootRegistry.ResolveOntology(Server, provider);

        Assert.Equal(OntologyConfig.ProductDefaults, ontology);
        Assert.Contains("Failed to load", console.ErrorOutput, StringComparison.Ordinal);
        Assert.Contains("Serving the default ontology", console.ErrorOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, console.Output);
    }

    private DocsRootRegistry Registry(ScriptedGit git, OperatingSystemKind platform = OperatingSystemKind.Linux) =>
        new(
            Server,
            root =>
            {
                _built.Add(root);
                return null!;
            },
            git,
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(Work, operatingSystem: platform));

    private static string Absolute(params string[] segments) =>
        Path.GetFullPath(Path.Combine([Path.DirectorySeparatorChar.ToString(), .. segments]));

    private sealed class ScriptedGit : IGitWorktreeIdentity
    {
        private readonly Dictionary<string, (string Top, string Common)> _known = new(StringComparer.Ordinal);

        public ScriptedGit Add(string directory, string top, string common)
        {
            _known[directory] = (top, common);
            return this;
        }

        public bool TryRoots(string directory, out string? topLevel, out string? commonDirectory, out string? failure)
        {
            if (_known.TryGetValue(directory, out (string Top, string Common) found))
            {
                (topLevel, commonDirectory, failure) = (found.Top, found.Common, null);
                return true;
            }

            (topLevel, commonDirectory, failure) = (null, null, "not a git repository");
            return false;
        }
    }
}
