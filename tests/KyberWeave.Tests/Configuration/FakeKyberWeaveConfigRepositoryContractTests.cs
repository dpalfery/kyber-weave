using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Configuration;

/// <summary>Runs the config-repository contract against the in-memory fake every unit test uses.</summary>
public sealed class FakeKyberWeaveConfigRepositoryContractTests : KyberWeaveConfigRepositoryContractTests
{
    private readonly FakeKyberWeaveConfigRepository _repository = new();

    /// <inheritdoc />
    protected override IKyberWeaveConfigRepository CreateRepository() => _repository;

    /// <inheritdoc />
    protected override string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "kw-fake-config", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override string NewHome() =>
        Path.Combine(Path.GetTempPath(), "kw-fake-home", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override void WriteRepositoryConfig(string root, string yaml) =>
        _repository.WriteRepositoryConfig(root, yaml);

    /// <inheritdoc />
    protected override void WriteLegacyConfig(string root, string yaml) =>
        _repository.WriteLegacyConfig(root, yaml);

    /// <inheritdoc />
    protected override void WriteUserConfig(string home, string yaml) =>
        _repository.WriteUserConfig(home, yaml);

    /// <inheritdoc />
    protected override void WriteConfigFile(string root, string fileName, string yaml) =>
        _repository.WriteFile(Path.Combine(Path.GetFullPath(root), fileName), yaml);

    /// <inheritdoc />
    protected override IDisposable MakeUnreadable(string path) => _repository.FailReads(path);
}
