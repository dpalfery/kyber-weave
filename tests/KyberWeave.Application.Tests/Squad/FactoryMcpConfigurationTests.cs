using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>Which Factory config files the warning reads, over a fake file set, so no ancestor directory is listed on a disk.</summary>
public sealed class FactoryMcpConfigurationTests
{
    private const string Home = "/kw/home";

    private readonly FileSet _files = new();

    private IReadOnlyList<string> Warnings(SquadDeploymentScope scope, string targetRoot) =>
        FactoryMcpConfiguration.Warnings(_files, [SquadTarget.Factory], scope, TestSquadCorpus.CanonicalSource, targetRoot, Home);

    [Fact]
    public void AConfigInAnAncestorOfTheProjectDefinesTheServers()
    {
        _files.Add("/kw/a/.factory/mcp.json", TestSquadCorpus.CanonicalSource.McpConfiguration.GetRawText());

        Assert.Empty(Warnings(SquadDeploymentScope.Project, "/kw/a/b/c/"));
    }

    [Fact]
    public void AConfigInTheHomeDefinesTheServersForAProject()
    {
        _files.Add("/kw/home/.factory/mcp.json", TestSquadCorpus.CanonicalSource.McpConfiguration.GetRawText());

        Assert.Empty(Warnings(SquadDeploymentScope.Project, "/kw/a/b"));
    }

    [Fact]
    public void NoConfigAnywhereWarnsOnce()
    {
        Assert.Single(Warnings(SquadDeploymentScope.Project, "/kw/a/b"));
    }

    [Fact]
    public void AConfigBesideTheProjectIsNotReadForAGlobalDeployment()
    {
        _files.Add("/kw/a/b/.factory/mcp.json", TestSquadCorpus.CanonicalSource.McpConfiguration.GetRawText());

        Assert.Single(Warnings(SquadDeploymentScope.Global, "/kw/a/b"));
    }

    private sealed class FileSet : ISquadFileAccess
    {
        private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);

        public void Add(string path, string text) => _text[path] = text;

        public bool FileExists(string path) => _text.ContainsKey(path);

        public bool DirectoryExists(string path) => throw new NotSupportedException();

        public string ReadAllText(string path) => _text[path];

        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();

        public byte[] ReadBytesUpTo(string path, int maxBytes) => throw new NotSupportedException();

        public long GetLength(string path) => throw new NotSupportedException();

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public void DeleteDirectory(string path) => throw new NotSupportedException();

        public string NewScratchDirectoryPath(string prefix) => throw new NotSupportedException();

        public IEnumerable<SquadDirectoryEntry> EnumerateRealDirectories(string directory) => throw new NotSupportedException();

        public string? FindCheckoutSquadSourceRoot() => throw new NotSupportedException();

        public void WriteAllBytesAtomic(string path, byte[] contents) => throw new NotSupportedException();
    }
}
