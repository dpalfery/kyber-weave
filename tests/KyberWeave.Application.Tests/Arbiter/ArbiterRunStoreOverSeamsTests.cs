using System.Security.Cryptography;
using System.Text;
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
    private const string RelativeArbiterDirectory = "artifacts/arbiter";

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
    /// A relative Arbiter directory still sweeps: the identity check answers for the spellings the store was given
    /// instead of refusing them, because a refused check would abort the whole sweep and let the stores grow without
    /// bound.
    /// </summary>
    [Fact]
    public async Task AnAppendSweepsWhenTheArbiterDirectoryIsRelative()
    {
        string expired = RelativeStores + "/review-" + DigestOf("RUN-OLD");
        _files.CreateDirectory(RelativeStores);
        _files.CreateDirectory(expired);
        _files.WriteAllText(expired + "/findings.jsonl", "{}");
        _clock.Advance(TimeSpan.FromDays(15));

        ArbiterRunStore store = new(RelativeArbiterDirectory, _log, _files, _files, _paths, clock: _clock);
        await store.AppendFindingAsync("RUN-NEW", "written today");

        Assert.False(_files.DirectoryExists(expired), $"'{expired}' is past the window and was kept.");
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

    private const string RelativeStores = RelativeArbiterDirectory + "/stores";

    private static string DigestOf(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
