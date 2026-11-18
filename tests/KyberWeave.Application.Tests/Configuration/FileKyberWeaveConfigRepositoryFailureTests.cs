using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Data.Configuration;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Configuration;

/// <summary>
/// A seam failure on the user path is reported as a failed result, as it is on the repository path: a caller of
/// <see cref="IKyberWeaveConfigRepository.TryLoadUserConfig"/> gets a diagnostic to print, never an exception.
/// </summary>
public sealed class FileKyberWeaveConfigRepositoryFailureTests
{
    private const string Home = "/kw/config-failure-home";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/kw/config-failure");

    [Fact]
    public void AUserConfigExistenceProbeThatFailsReturnsAFailureResult()
    {
        string path = UserHostConfigPath.Of(Home);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, "task-prompt-budget: 41011\n");
        FileKyberWeaveConfigRepository repository = new(
            new FakeRepositoryTreeProbe(),
            _files,
            new FakePathResolver(_files),
            _environment);

        using IDisposable scope = FaultExistenceProbes();

        KyberWeaveConfigLoadResult result = repository.TryLoadUserConfig(Home);

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.NotNull(result.Error);
        Assert.Equal(path, result.ConfigPath);
    }

    /// <summary>Makes every existence probe throw, as a file system whose parent directory denies listing may.</summary>
    private IDisposable FaultExistenceProbes()
    {
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.FileExists));
        return new Scope(_files);
    }

    private sealed class Scope(InMemoryFileSystem files) : IDisposable
    {
        public void Dispose() => files.ClearFaults();
    }
}
