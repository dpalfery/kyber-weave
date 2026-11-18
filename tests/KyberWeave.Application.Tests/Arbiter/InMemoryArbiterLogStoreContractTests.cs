using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Arbiter;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>Runs the append-only log contract against the in-memory fake, so a unit test can trust its window and its problems.</summary>
public sealed class InMemoryArbiterLogStoreContractTests : ArbiterLogStoreContractTests
{
    private readonly FakeArbiterLogStore _store = new();
    private readonly List<string> _directories = [];

    /// <inheritdoc />
    protected override IArbiterLogStore Store => _store;

    /// <inheritdoc />
    protected override string NewDirectory()
    {
        string directory = Path.Combine("/contract", Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        return directory;
    }

    /// <inheritdoc />
    protected override string UnreadablePath(string directory)
    {
        string path = Path.Combine(directory, FileName);
        _store.UnreadablePaths.Add(path);
        return path;
    }

    /// <inheritdoc />
    protected override void DeleteDirectory(string directory)
    {
    }
}
