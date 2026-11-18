using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Data.Arbiter;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The state factory over the shared file-system, path and log fakes: the run store it opens reads through the path
/// seam the factory was given, so the retention sweep needs no disk and no process working directory.
/// </summary>
public sealed class ArbiterRunStoreOverSeamsTests
{
    private const string ArbiterDirectory = "/repo/artifacts/arbiter";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFileSystem _files;
    private readonly FakePathResolver _paths;
    private readonly FakeArbiterLogStore _log = new();

    public ArbiterRunStoreOverSeamsTests()
    {
        _files = new InMemoryFileSystem(_clock);
        _files.CreateDirectory(ArbiterDirectory);
        _paths = new FakePathResolver(_files);
    }

    /// <summary>
    /// The factory hands the path seam to the store it opens, so a finding written through the factory is read back
    /// through it with no disk involved.
    /// </summary>
    [Fact]
    public async Task TheStateFactoryOpensARunStoreOverThePathSeam()
    {
        IArbiterRunStore store = new FileArbiterStateFactory(_log, _files, _files, _paths)
            .OpenRunStore(ArbiterDirectory, clock: _clock);

        Assert.Equal(ArbiterStoreStatus.Stored, await store.AppendFindingAsync("RUN-FACTORY", "finding one"));
        Assert.Equal(["finding one"], store.ReadFindings("RUN-FACTORY"));
    }
}
