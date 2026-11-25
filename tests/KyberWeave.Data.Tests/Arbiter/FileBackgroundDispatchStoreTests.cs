using KyberWeave.Arbiter.Hooks;
using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The background dispatch records through the in-memory file system and a controlled clock.</summary>
public sealed class FileBackgroundDispatchStoreTests
{
    private const string Directory = "/arbiter";

    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryFileSystem _files;
    private readonly FileBackgroundDispatchStore _store;

    public FileBackgroundDispatchStoreTests()
    {
        _files = new InMemoryFileSystem(_time);
        _store = new FileBackgroundDispatchStore(_files, _time, new FakeIdGenerator());
    }

    [Fact]
    public void ASavedRecordIsFoundByItsSessionAndAgent()
    {
        BackgroundDispatchRecord record = RecordOf("session-1", "agent-1", "pre-1");

        _store.Save(Directory, record);

        Assert.Equal(record, _store.Find(Directory, "session-1", "agent-1"));
    }

    [Fact]
    public void AnUnknownAgentIsNotFound()
    {
        _store.Save(Directory, RecordOf("session-1", "agent-1", "pre-1"));

        Assert.Null(_store.Find(Directory, "session-1", "agent-2"));
        Assert.Null(_store.Find(Directory, "session-2", "agent-1"));
    }

    [Fact]
    public void SavingAgainReplacesTheRecord()
    {
        _store.Save(Directory, RecordOf("session-1", "agent-1", "pre-1"));
        _store.Save(Directory, RecordOf("session-1", "agent-1", "pre-2"));

        Assert.Equal("pre-2", _store.Find(Directory, "session-1", "agent-1")!.PreId);
    }

    [Fact]
    public void ASessionListsOnlyItsOwnRecords()
    {
        _store.Save(Directory, RecordOf("session-1", "agent-1", "pre-1"));
        _store.Save(Directory, RecordOf("session-1", "agent-2", "pre-2"));
        _store.Save(Directory, RecordOf("session-2", "agent-3", "pre-3"));

        IReadOnlyList<BackgroundDispatchRecord> records = _store.ForSession(Directory, "session-1");

        Assert.Equal(["pre-1", "pre-2"], records.Select(record => record.PreId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASessionWithNothingSavedListsNothing() =>
        Assert.Empty(_store.ForSession(Directory, "session-1"));

    [Fact]
    public void ARecordOlderThanAWeekIsPrunedByTheNextSave()
    {
        _store.Save(Directory, RecordOf("session-1", "old", "pre-old"));

        _time.Advance(TimeSpan.FromDays(8));
        _store.Save(Directory, RecordOf("session-1", "new", "pre-new"));

        Assert.Null(_store.Find(Directory, "session-1", "old"));
        Assert.NotNull(_store.Find(Directory, "session-1", "new"));
    }

    [Fact]
    public void PruningRunsAtMostOnceAnHour()
    {
        _store.Save(Directory, RecordOf("session-1", "old", "pre-old"));
        _time.Advance(TimeSpan.FromDays(8));
        _store.Save(Directory, RecordOf("session-1", "trigger", "pre-trigger"));

        _store.Save(Directory, RecordOf("session-1", "older", "pre-older"));
        _time.Advance(TimeSpan.FromMinutes(30));
        _time.Advance(TimeSpan.FromDays(8));
        _store.Save(Directory, RecordOf("session-1", "later", "pre-later"));

        Assert.Null(_store.Find(Directory, "session-1", "older"));
    }

    [Fact]
    public void ARecordThatCannotBeParsedIsNotFollowed()
    {
        _store.Save(Directory, RecordOf("session-1", "agent-1", "pre-1"));
        foreach (string file in _files.EnumerateFiles(Directory + "/background", "*.json", recursive: false))
        {
            _files.WriteAllText(file, "{ not json");
        }

        Assert.Null(_store.Find(Directory, "session-1", "agent-1"));
        Assert.Empty(_store.ForSession(Directory, "session-1"));
    }

    [Fact]
    public void AKeptReportIsTakenOnce()
    {
        _store.Keep(Directory, "session-1", "agent-1", "the report");

        Assert.Equal("the report", _store.Take(Directory, "session-1", "agent-1"));
        Assert.Null(_store.Take(Directory, "session-1", "agent-1"));
    }

    [Fact]
    public void AKeptReportIsKeyedBySessionAndAgent()
    {
        _store.Keep(Directory, "session-1", "agent-1", "the report");

        Assert.Null(_store.Take(Directory, "session-1", "agent-2"));
        Assert.Null(_store.Take(Directory, "session-2", "agent-1"));
        Assert.Equal("the report", _store.Take(Directory, "session-1", "agent-1"));
    }

    [Fact]
    public void KeepingAgainReplacesTheKeptReport()
    {
        _store.Keep(Directory, "session-1", "agent-1", "first");
        _store.Keep(Directory, "session-1", "agent-1", "second");

        Assert.Equal("second", _store.Take(Directory, "session-1", "agent-1"));
    }

    [Fact]
    public void AKeptReportIsNotAFollowedDispatch()
    {
        _store.Keep(Directory, "session-1", "agent-1", "the report");

        Assert.Null(_store.Find(Directory, "session-1", "agent-1"));
        Assert.Empty(_store.ForSession(Directory, "session-1"));
    }

    [Fact]
    public void AKeptReportOlderThanAWeekIsPrunedByTheNextKeep()
    {
        _store.Keep(Directory, "session-1", "old", "stale report");

        _time.Advance(TimeSpan.FromDays(8));
        _store.Keep(Directory, "session-1", "new", "fresh report");

        Assert.Null(_store.Take(Directory, "session-1", "old"));
        Assert.Equal("fresh report", _store.Take(Directory, "session-1", "new"));
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("io")]
    public void AnUnreadableKeptReportReturnsNullAndIsRemoved(string failureKind)
    {
        _store.Keep(Directory, "session-1", "agent-1", "the report");
        Exception failure = failureKind == "unauthorized"
            ? new UnauthorizedAccessException("permission denied")
            : new IOException("disk read error");
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText), failure);

        Assert.Null(_store.Take(Directory, "session-1", "agent-1"));

        _files.ClearFaults();
        Assert.Null(_store.Take(Directory, "session-1", "agent-1"));
    }

    [Fact]
    public void AnUnreadableKeptReportWhoseRemovalAlsoFailsStillReturnsNull()
    {
        _store.Keep(Directory, "session-1", "agent-1", "the report");
        _files.FailWhen(
            operation => operation.Name is nameof(InMemoryFileSystem.ReadAllText) or nameof(InMemoryFileSystem.DeleteFile),
            new UnauthorizedAccessException("permission denied"));

        Assert.Null(_store.Take(Directory, "session-1", "agent-1"));
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("io")]
    public void AnUnreadableFollowedRecordIsNotFollowed(string failureKind)
    {
        _store.Save(Directory, RecordOf("session-1", "agent-1", "pre-1"));
        Exception failure = failureKind == "unauthorized"
            ? new UnauthorizedAccessException("permission denied")
            : new IOException("disk read error");
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText), failure);

        Assert.Null(_store.Find(Directory, "session-1", "agent-1"));
        Assert.Empty(_store.ForSession(Directory, "session-1"));
    }

    private static BackgroundDispatchRecord RecordOf(string sessionId, string agentId, string preId) =>
        new(new BackgroundDispatch("claude", sessionId, agentId, "conductor", "worker", "do the work"), preId);
}
