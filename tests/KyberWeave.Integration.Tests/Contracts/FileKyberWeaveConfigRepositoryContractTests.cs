using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Configuration;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the config-repository contract against the repository over the real disk.</summary>
public sealed class FileKyberWeaveConfigRepositoryContractTests : KyberWeaveConfigRepositoryContractTests
{
    private readonly List<string> _created = [];

    /// <inheritdoc />
    protected override IKyberWeaveConfigRepository CreateRepository() =>
        RealFileKyberWeaveConfigRepository.Create();

    /// <inheritdoc />
    protected override string NewRoot() => NewDirectory("kw-config-contract-");

    /// <inheritdoc />
    protected override string NewHome() => NewDirectory("kw-config-home-");

    /// <inheritdoc />
    protected override void WriteRepositoryConfig(string root, string yaml) =>
        WriteFile(RepositoryConfigPath(root), yaml);

    /// <inheritdoc />
    protected override void WriteLegacyConfig(string root, string yaml) => WriteFile(LegacyConfigPath(root), yaml);

    /// <inheritdoc />
    protected override void WriteUserConfig(string home, string yaml) => WriteFile(UserConfigPath(home), yaml);

    /// <inheritdoc />
    protected override void WriteConfigFile(string root, string fileName, string yaml) =>
        WriteFile(Path.Combine(Path.GetFullPath(root), fileName), yaml);

    /// <inheritdoc />
    protected override IDisposable MakeUnreadable(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

    /// <inheritdoc />
    protected override void Cleanup()
    {
        foreach (string directory in _created)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover scratch directory is not a contract failure.
            }
        }
    }

    private string NewDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _created.Add(path);
        return path;
    }

    private static void WriteFile(string path, string yaml)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
    }
}
