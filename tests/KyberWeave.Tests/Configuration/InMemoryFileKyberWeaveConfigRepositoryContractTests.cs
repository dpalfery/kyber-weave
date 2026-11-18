using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Configuration;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Configuration;

/// <summary>
/// Runs the config-repository contract against the shipped repository over the in-memory file system, the way a unit
/// test reaches it: no disk, no process working directory and no real tree probe.
/// </summary>
public sealed class InMemoryFileKyberWeaveConfigRepositoryContractTests : KyberWeaveConfigRepositoryContractTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeRepositoryTreeProbe _tree = new();
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/kw/config-repository");

    /// <inheritdoc />
    protected override IKyberWeaveConfigRepository CreateRepository() =>
        new FileKyberWeaveConfigRepository(_tree, _files, new FakePathResolver(_files), _environment);

    /// <inheritdoc />
    protected override string NewRoot() => NewDirectory("kw-in-memory-config");

    /// <inheritdoc />
    protected override string NewHome() => NewDirectory("kw-in-memory-home");

    /// <inheritdoc />
    protected override void WriteRepositoryConfig(string root, string yaml) =>
        Write(RepositoryConfigPath(root), yaml);

    /// <inheritdoc />
    protected override void WriteLegacyConfig(string root, string yaml) =>
        Write(LegacyConfigPath(root), yaml);

    /// <inheritdoc />
    protected override void WriteUserConfig(string home, string yaml) =>
        Write(UserConfigPath(home), yaml);

    /// <inheritdoc />
    protected override void WriteConfigFile(string root, string fileName, string yaml) =>
        Write(Path.Combine(Path.GetFullPath(root), fileName), yaml);

    private string NewDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        _files.CreateDirectory(path);
        return path;
    }

    private void Write(string path, string yaml)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        _files.CreateDirectory(directory);
        _files.WriteAllText(fullPath, yaml);
    }

    /// <inheritdoc />
    protected override IDisposable MakeUnreadable(string path) =>
        new ReadFailure(_files, Path.GetFullPath(path));

    /// <summary>Fails every read of one path, then removes the rule.</summary>
    private sealed class ReadFailure : IDisposable
    {
        private readonly InMemoryFileSystem _files;

        public ReadFailure(InMemoryFileSystem files, string path)
        {
            _files = files;
            files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText)
                && string.Equals(operation.Path, path, StringComparison.Ordinal));
        }

        public void Dispose() => _files.ClearFaults();
    }
}
