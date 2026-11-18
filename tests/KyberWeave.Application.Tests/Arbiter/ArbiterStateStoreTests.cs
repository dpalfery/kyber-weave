using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The ledger, the decision log and the run store over the injected log seam and an in-memory file system: every
/// answer here is the class's own, with no disk, no lock file and no clock outside the test.
/// </summary>
public sealed class ArbiterStateStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeArbiterLogStore _log = new();
    private readonly InMemoryFileSystem _files = new();

    [Fact]
    public async Task AStoredPostDispatchCarriesThePreEventItPairedWith()
    {
        InFlightLedger ledger = new("/repo/artifacts/arbiter", _log);
        ArbiterLedgerEvent pre = Event("pre-1", ArbiterLedgerPhases.Pre, callId: "call-1");
        ArbiterLedgerEvent post = Event("post-1", ArbiterLedgerPhases.Post, callId: "call-1");

        await ledger.AppendAsync(pre);
        await ledger.AppendAsync(post);

        IReadOnlyList<ArbiterLedgerEvent> stored = ledger.ReadAll();
        Assert.Equal(["pre-1", "post-1"], stored.Select(record => record.Id));
        Assert.Equal("pre-1", stored[1].PreId);
        Assert.Equal(2, _log.AppendCount);
    }

    [Fact]
    public async Task APostEventResolvesItsPreEventFromWhatTheLogStoreAlreadyHolds()
    {
        InFlightLedger ledger = new("/repo/artifacts/arbiter", _log);
        await ledger.AppendAsync(Event("pre-1", ArbiterLedgerPhases.Pre, callId: "call-1"));

        string? paired = ledger.MatchPre(Event("post-1", ArbiterLedgerPhases.Post, callId: "call-1"));

        Assert.Equal("pre-1", paired);
    }

    [Fact]
    public void ADecisionLogItsLogStoreCannotReadReportsTheProblemInsteadOfAPartialLog()
    {
        _log.UnreadablePaths.Add(Path.Combine("/repo/artifacts/arbiter", DecisionLog.FileName));
        DecisionLog decisions = new("/repo/artifacts/arbiter", _log);

        ArbiterLogRead<ArbiterDecisionRecord> read = decisions.ReadAllWithDiagnostics();

        Assert.Empty(read.Records);
        Assert.NotNull(read.Unreadable);
    }

    [Fact]
    public async Task ALogThatRefusesTheAppendReportsTheStoreUnavailableInsteadOfThrowing()
    {
        _log.AppendFailure = new IOException("the lock was never taken");
        ArbiterRunStore store = new("/repo/artifacts/arbiter", _log, _files, _files);

        ArbiterStoreStatus status = await store.AppendFindingAsync("REVIEW-1", "a finding");

        Assert.Equal(ArbiterStoreStatus.Unavailable, status);
        Assert.Empty(store.ReadFindings("REVIEW-1"));
    }

    [Fact]
    public async Task TheRunStoreKeepsAFindingRetrievableByItsReviewRun()
    {
        ArbiterRunStore store = new("/repo/artifacts/arbiter", _log, _files, _files);

        Assert.Equal(ArbiterStoreStatus.Stored, await store.AppendFindingAsync("REVIEW-1", "line one\nline two"));

        Assert.Equal(["line one\nline two"], store.ReadFindings("REVIEW-1"));
    }

    [Fact]
    public async Task AnItemOverTheConfiguredSizeIsRefusedWithoutAnAppend()
    {
        ArbiterRunStore store = new(
            "/repo/artifacts/arbiter",
            _log,
            _files,
            _files,
            new ArbiterStoreOptions { MaxItemBytes = 4 });

        Assert.Equal(ArbiterStoreStatus.TooLarge, await store.AppendFindingAsync("REVIEW-1", "five!"));
        Assert.Equal(0, _log.AppendCount);
    }

    [Fact]
    public async Task AnExpiredSiblingStoreIsSweptThroughTheFileSystemSeamAndTheActiveOneIsKept()
    {
        string active = "/repo/artifacts/arbiter/stores/" + ReviewDirectoryName("REVIEW-1");
        _files.CreateDirectory("/repo/artifacts/arbiter/stores/review-expired");
        _files.CreateDirectory(active);
        _files.WriteAllText("/repo/artifacts/arbiter/stores/review-expired/findings.jsonl", "{}\n");
        ArbiterRunStore store = new(
            "/repo/artifacts/arbiter",
            _log,
            _files,
            _files,
            new ArbiterStoreOptions { RetainDays = 1 },
            () => Now,
            _ => Now.AddDays(-30).UtcDateTime);

        await store.AppendFindingAsync("REVIEW-1", "a finding");

        Assert.False(_files.DirectoryExists("/repo/artifacts/arbiter/stores/review-expired"));
        Assert.True(_files.DirectoryExists(active));
    }

    private static string ReviewDirectoryName(string reviewRun)
    {
        byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reviewRun));
        return "review-" + Convert.ToHexStringLower(digest);
    }

    private static ArbiterLedgerEvent Event(
        string id,
        string phase,
        string? callId = null,
        string? task = null) =>
        new(id, Now, ArbiterSources.Hook, "test-harness", "session-1", phase)
        {
            CallId = callId,
            TaskFiles = task is null ? null : [task],
        };
}
