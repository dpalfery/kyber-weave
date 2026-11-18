using System.Collections.Frozen;
using KyberWeave.Contracts.Host;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Proves <see cref="SquadInstanceLinkSync"/> through the in-memory file system, path resolver and lease alone: links,
/// the recorded links file, the person's own files and the lease are all in memory.
/// </summary>
public sealed class SquadInstanceLinkSyncUnitTests
{
    private const string Main = "/kw/main";
    private const string Instance = "/kw/instance";
    private const string State = "/kw/state";

    private static readonly SquadInstanceRow Row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly InProcessLeaseHost _leases = new();
    private readonly FileSystemSquadLinkFileSystem _links;
    private readonly SquadInstanceLinkSync _sync;

    public SquadInstanceLinkSyncUnitTests()
    {
        _links = new FileSystemSquadLinkFileSystem(_files);
        _sync = new SquadInstanceLinkSync(
            _files,
            new FakePathResolver(_files),
            new InProcessLease(_leases),
            new FakeProcessEnvironment(currentDirectory: "/kw/work"));
        _files.CreateDirectory(Instance);
        _files.CreateDirectory(State);
        _files.CreateDirectory(Main + "/logs");
        _files.WriteAllText(Main + "/settings.json", "{}");
        _files.WriteAllText(Main + "/logs/session.log", "log");
    }

    private SquadInstanceLinkSyncResult Sync(IReadOnlySet<string>? rendered = null, SquadInstanceRow? row = null) =>
        _sync.Sync(Instance, Main, row ?? Row, rendered ?? FrozenSet<string>.Empty, _links, State);

    [Fact]
    public void ConfigLayerInstanceIsLeftAloneAndRecordsNothing()
    {
        SquadInstanceRow configLayer = Row with { Kind = SquadInstanceKind.ConfigLayer };

        SquadInstanceLinkSyncResult result = Sync(row: configLayer);

        Assert.Empty(result.Links);
        Assert.False(_files.FileExists(State + "/links.json"));
        Assert.Empty(_files.EnumerateEntries(Instance));
    }

    [Fact]
    public void HomeReplaceInstanceLinksTheMainHomeAndRecordsTheLinks()
    {
        SquadInstanceLinkSyncResult result = Sync();

        Assert.Equal(Main + "/settings.json", _files.GetLinkTarget(Instance + "/settings.json"));
        Assert.Equal(Main + "/logs", _files.GetLinkTarget(Instance + "/logs"));
        Assert.Contains(result.Links, link => link.RelativePath == "settings.json" && link.Kind == SquadLinkKind.File);
        Assert.Contains("\"settings.json\"", _files.ReadAllText(State + "/links.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void SyncingAgainChangesNothingAndWarnsOfNothing()
    {
        _ = Sync();
        string recorded = _files.ReadAllText(State + "/links.json");

        SquadInstanceLinkSyncResult again = Sync();

        Assert.Empty(again.Warnings);
        Assert.Equal(recorded, _files.ReadAllText(State + "/links.json"));
    }

    [Fact]
    public void FileTheSquadRendersItselfIsNeverLinked()
    {
        _files.CreateDirectory(Main + "/agents");
        _files.WriteAllText(Main + "/agents/worker.md", "main copy");

        SquadInstanceLinkSyncResult result = Sync(new HashSet<string> { "agents/worker.md" });

        Assert.DoesNotContain(result.Links, link => link.RelativePath.StartsWith("agents/worker.md", StringComparison.Ordinal));
        Assert.Null(_files.GetLinkTarget(Instance + "/agents/worker.md"));
    }

    [Fact]
    public void PersonsOwnFileWhereALinkWouldGoIsKeptWithAWarning()
    {
        _files.WriteAllText(Instance + "/settings.json", "mine");

        SquadInstanceLinkSyncResult result = Sync();

        Assert.Equal("mine", _files.ReadAllText(Instance + "/settings.json"));
        Assert.Contains(result.Warnings, warning => warning.Contains("settings.json", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Links, link => link.RelativePath == "settings.json");
        Assert.Equal(Main + "/logs", _files.GetLinkTarget(Instance + "/logs"));
    }

    [Fact]
    public void LinkWhoseMainHomeFileIsGoneIsRemovedAndLeavesTheMainHomeAlone()
    {
        _files.WriteAllText(Main + "/history.sqlite", "h");
        _ = Sync();
        _files.DeleteFile(Main + "/history.sqlite");

        SquadInstanceLinkSyncResult result = Sync();

        Assert.False(_files.PathExists(Instance + "/history.sqlite"));
        Assert.DoesNotContain(result.Links, link => link.RelativePath == "history.sqlite");
        Assert.True(_files.FileExists(Main + "/settings.json"));
    }

    [Fact]
    public void LeaseHeldByAPeerRefusesTheSyncBeforeAnythingChanges()
    {
        using IDisposable peer = new InProcessLease(_leases).Acquire(_sync.GetMutexName(Instance), TimeSpan.Zero);

        _ = Assert.Throws<LeaseTimeoutException>(() => Sync());

        Assert.Empty(_files.EnumerateEntries(Instance));
        Assert.False(_files.FileExists(State + "/links.json"));
    }

    [Fact]
    public void LeaseIsReleasedWhenTheSyncEnds()
    {
        _ = Sync();

        using IDisposable next = new InProcessLease(_leases).Acquire(_sync.GetMutexName(Instance), TimeSpan.Zero);
    }

    [Fact]
    public void CorruptRecordedLinksFileIsRefusedNotOverwritten()
    {
        _files.WriteAllText(State + "/links.json", "not json");

        _ = Assert.Throws<InvalidDataException>(() => Sync());

        Assert.Equal("not json", _files.ReadAllText(State + "/links.json"));
    }

    [Fact]
    public void RecordedLinkThatEscapesTheInstanceRootIsRefused()
    {
        _files.WriteAllText(
            State + "/links.json",
            "[{\"relativePath\":\"../../etc/passwd\",\"kind\":\"file\",\"targetPath\":\"/kw/main/settings.json\"}]");

        _ = Assert.Throws<InvalidDataException>(() => Sync());
    }

    private const string PlannedSettings =
        "[{\"relativePath\":\"settings.json\",\"kind\":\"file\",\"targetPath\":\"/kw/main/settings.json\"}]";

    [Fact]
    public void LinkTheStoppedSyncPlannedAndNeverRecordedIsAdoptedAsItsOwn()
    {
        _files.CreateFileLink(Instance + "/settings.json", Main + "/settings.json");
        _files.WriteAllText(State + "/links.pending.json", PlannedSettings);

        SquadInstanceLinkSyncResult result = Sync();

        Assert.Empty(result.Warnings);
        Assert.Contains(result.Links, link => link.RelativePath == "settings.json");
        Assert.Contains("\"settings.json\"", _files.ReadAllText(State + "/links.json"), StringComparison.Ordinal);
        Assert.False(_files.FileExists(State + "/links.pending.json"));
    }

    [Fact]
    public void PersonsOwnCopyAtAPlannedPathIsStillKeptWithAWarning()
    {
        _files.WriteAllText(Instance + "/settings.json", "mine");
        _files.WriteAllText(State + "/links.pending.json", PlannedSettings);

        SquadInstanceLinkSyncResult result = Sync();

        Assert.Equal("mine", _files.ReadAllText(Instance + "/settings.json"));
        Assert.Null(_files.GetLinkTarget(Instance + "/settings.json"));
        Assert.Contains(result.Warnings, warning => warning.Contains("settings.json", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Links, link => link.RelativePath == "settings.json");
    }

    [Fact]
    public void PersonsLinkToAnotherFileAtAPlannedPathIsStillKeptWithAWarning()
    {
        _files.WriteAllText("/kw/elsewhere.json", "x");
        _files.CreateFileLink(Instance + "/settings.json", "/kw/elsewhere.json");
        _files.WriteAllText(State + "/links.pending.json", PlannedSettings);

        SquadInstanceLinkSyncResult result = Sync();

        Assert.Equal("/kw/elsewhere.json", _files.GetLinkTarget(Instance + "/settings.json"));
        Assert.Contains(result.Warnings, warning => warning.Contains("settings.json", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Links, link => link.RelativePath == "settings.json");
    }

    [Fact]
    public void FolderTheInstanceCannotListIsSkippedAndTheSyncStillCompletes()
    {
        _files.CreateDirectory(Instance + "/private");
        _files.WriteAllText(Instance + "/private/mine.txt", "mine");
        _files.DenyListing(Instance + "/private");

        SquadInstanceLinkSyncResult result = Sync();

        Assert.Contains(result.Links, link => link.RelativePath == "settings.json");
        Assert.Equal("mine", _files.ReadAllText(Instance + "/private/mine.txt"));
    }

    [Fact]
    public void FolderTheMainHomeCannotListIsSkippedAndTheSyncStillCompletes()
    {
        _files.CreateDirectory(Main + "/sealed");
        _files.DenyListing(Main + "/sealed");

        SquadInstanceLinkSyncResult result = Sync(new HashSet<string> { "sealed/rendered.md" });

        Assert.Contains(result.Links, link => link.RelativePath == "settings.json");
    }

    [Fact]
    public void PlannedLinksFileWithAnEscapingPathIsTreatedAsAbsent()
    {
        _files.WriteAllText(
            State + "/links.pending.json",
            "[{\"relativePath\":\"../x\",\"kind\":\"file\",\"targetPath\":\"/kw/main/settings.json\"}]");

        SquadInstanceLinkSyncResult result = Sync();

        Assert.Contains(result.Links, link => link.RelativePath == "settings.json");
        Assert.False(_files.FileExists(State + "/links.pending.json"));
    }

    [Fact]
    public void PlannedLinksFileWithACorruptBodyIsTreatedAsAbsent()
    {
        _files.WriteAllText(State + "/links.pending.json", "not json");

        SquadInstanceLinkSyncResult result = Sync();

        Assert.Contains(result.Links, link => link.RelativePath == "settings.json");
    }

    [Fact]
    public void PlannedLinksFileWithANullEntryIsTreatedAsAbsent()
    {
        _files.WriteAllText(State + "/links.pending.json", "[null]");

        SquadInstanceLinkSyncResult result = Sync();

        Assert.Contains(result.Links, link => link.RelativePath == "settings.json");
    }

    [Fact]
    public void RecordedFolderLinkReplacedByARealFolderIsKeptAndDroppedFromTheRecord()
    {
        _ = Sync();
        _links.DeleteTreeNoFollow(Instance + "/logs");
        _files.CreateDirectory(Instance + "/logs");
        _files.WriteAllText(Instance + "/logs/mine.txt", "mine");

        SquadInstanceLinkSyncResult result = Sync();

        Assert.Equal("mine", _files.ReadAllText(Instance + "/logs/mine.txt"));
        Assert.DoesNotContain(result.Links, link => link.RelativePath == "logs");
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void RewrittenLinkedFileWhoseMainHomeSourceIsGoneIsKeptAndDroppedFromTheRecord()
    {
        _ = Sync();
        _files.DeleteFile(Instance + "/settings.json");
        _files.WriteAllText(Instance + "/settings.json", "mine");
        _files.DeleteFile(Main + "/settings.json");

        SquadInstanceLinkSyncResult result = Sync();

        Assert.Equal("mine", _files.ReadAllText(Instance + "/settings.json"));
        Assert.DoesNotContain(result.Links, link => link.RelativePath == "settings.json");
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task TwoSyncObjectsOnOneRootInOneProcessQueueInsteadOfFailing()
    {
        SquadInstanceLinkSync other = new(
            _files,
            new FakePathResolver(_files),
            new InProcessLease(_leases),
            new FakeProcessEnvironment(currentDirectory: "/kw/work"));

        for (int round = 0; round < 20; round++)
        {
            using ManualResetEventSlim start = new();
            Task[] runs =
            [
                Task.Run(() => { start.Wait(); _ = Sync(); }),
                Task.Run(() => { start.Wait(); _ = other.Sync(Instance, Main, Row, FrozenSet<string>.Empty, _links, State); })
            ];
            start.Set();
            await Task.WhenAll(runs);
        }

        Assert.Equal(Main + "/settings.json", _files.GetLinkTarget(Instance + "/settings.json"));
    }

    [Fact]
    public void SyncRefusedByAHeldLeaseThrowsAndTheNextSyncStillRuns()
    {
        InProcessLease holder = new(_leases);
        using (holder.Hold(_sync.GetMutexName(Instance)))
        {
            _ = Assert.Throws<KyberWeave.Contracts.Host.LeaseTimeoutException>(() => Sync());
        }

        Assert.Empty(_files.EnumerateEntries(Instance));
        SquadInstanceLinkSyncResult result = Sync();
        Assert.Contains(result.Links, link => link.RelativePath == "settings.json");
    }

    [Fact]
    public void LinkNameIsTheSameForARootWhateverItsSpelling()
    {
        Assert.Equal(_sync.GetMutexName(Instance), _sync.GetMutexName(Instance + "/"));
        Assert.NotEqual(_sync.GetMutexName(Instance), _sync.GetMutexName("/kw/other"));
    }
}
