using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Search;

/// <summary>
/// Documents and code joins go stale at wildly different rates, so they are tracked
/// separately: the CodeGraph daemon rewrites its database continuously while
/// documentation is edited by hand.
/// </summary>
public sealed class DocumentIndexHostTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.Zero));

    private readonly DocFixture _fixture;

    public DocumentIndexHostTests() => _fixture = DocFixture.CreateInMemory(_clock);

    private DocumentIndexHost Host(Func<DocumentSet> load, IReadOnlyList<string>? roots = null, string? catalog = null) =>
        roots is null
            ? new DocumentIndexHost(
                _fixture.Root,
                () => FakeCodeGraphResolver.WithSymbols(),
                load,
                _fixture.Store,
                _fixture.Paths,
                _fixture.Environment)
            : new DocumentIndexHost(
                _fixture.Root,
                () => FakeCodeGraphResolver.WithSymbols(),
                load,
                _fixture.Store,
                _fixture.Paths,
                _fixture.Environment,
                roots,
                catalog);

    private void AppendToThing()
    {
        string doc = Path.Combine(_fixture.Root, "6-Docs", "thing.md");
        _clock.Advance(TimeSpan.FromMinutes(1));
        _fixture.Memory!.WriteAllText(doc, _fixture.Memory.ReadAllText(doc) + "\n\n## More\n\nExtra prose.\n");
    }

    private DocumentIndexHost NewHost()
    {
        _fixture.WithCatalog().Write("6-Docs/thing.md", """
            ---
            id: api/thing
            title: Thing
            doc-type: reference
            status: current
            component: MotorcycleRAG API
            owner: API maintainers
            last-reviewed: 2026-07-21
            ---
            # Thing

            ## Detail

            Kerberos ticket renewal.
            """);
        return Host(_fixture.Load);
    }

    [Fact]
    public void AnUnchangedRepositoryRebuildsNothing()
    {
        DocumentIndexHost host = NewHost();

        host.Current();
        host.Current();
        host.Current();

        Assert.Equal(1, host.CorpusBuilds);
        Assert.Equal(1, host.JoinBuilds);
    }

    /// <summary>
    /// A code-graph write must not force a re-read and re-vectorisation of every
    /// document. That is the expensive half, and it is the half that did not change.
    /// </summary>
    [Fact]
    public void ACodeGraphChangeRebuildsOnlyTheJoins()
    {
        DocumentIndexHost host = NewHost();
        host.Current();

        string db = Path.Combine(_fixture.Root, ".codegraph");
        _fixture.Memory!.CreateDirectory(db);
        _fixture.Memory.WriteAllText(Path.Combine(db, "codegraph.db"), "not a real database");

        host.Current();

        Assert.Equal(1, host.CorpusBuilds);
        Assert.Equal(2, host.JoinBuilds);
    }

    [Fact]
    public void ADocumentationChangeRebuildsTheCorpus()
    {
        DocumentIndexHost host = NewHost();
        host.Current();

        AppendToThing();

        host.Current();

        Assert.Equal(2, host.CorpusBuilds);
    }

    [Fact]
    public void DocumentCount_WhenCorpusStale_InvalidatesIndexSoCurrentRebuildsJoins()
    {
        DocumentIndexHost host = NewHost();
        DocumentIndex index1 = host.Current();
        Assert.Equal(1, host.JoinBuilds);

        AppendToThing();

        int? count = host.DocumentCount;
        Assert.Equal(2, count);

        DocumentIndex index2 = host.Current();
        Assert.NotSame(index1, index2);
        Assert.Equal(2, host.JoinBuilds);
    }

    [Fact]
    public void DocumentCount_WhenDocumentLoaderThrows_ReturnsNullWithoutThrowing()
    {
        DocumentIndexHost host = Host(() => throw new IOException("disk error / unreadable docs"));

        int? count = host.DocumentCount;
        Assert.Null(count);
    }

    [Fact]
    public void DocumentCount_WhenNonIoExceptionThrown_Propagates()
    {
        DocumentIndexHost host = Host(() => throw new InvalidOperationException("unexpected non-IO failure"));

        Assert.Throws<InvalidOperationException>(() => host.DocumentCount);
    }

    [Fact]
    public void DocumentCount_WhenCorpusAlreadyBuilt_DoesNotCallLoaderAgain()
    {
        int loadCalls = 0;
        NewHost(); // sets up fixture
        DocumentIndexHost host = Host(() =>
        {
            loadCalls++;
            return _fixture.Load();
        });

        _ = host.Current();
        Assert.Equal(1, loadCalls);

        // Subsequent DocumentCount calls when corpus exists must not invoke the loader
        int? count = host.DocumentCount;
        Assert.True(count > 0);
        Assert.Equal(1, loadCalls);
    }

    /// <summary>
    /// The stamp fingerprints the same set Load() produces. An overlapping root and a
    /// catalog that already lives inside a root must not inflate the count.
    /// </summary>
    [Fact]
    public void TheDocsStampCountsEachDocumentOnce()
    {
        _fixture.WithCatalog().Write("6-Docs/nested/guide.md", """
            ---
            id: nested/guide
            title: Guide
            doc-type: reference
            status: current
            component: MotorcycleRAG API
            owner: API maintainers
            last-reviewed: 2026-07-21
            ---
            # Guide
            """);

        DocumentIndexHost WithRoots(params string[] roots) => Host(_fixture.Load, roots, "6-Docs/catalog.md");

        long single = WithRoots("6-Docs").ComputeDocsStamp();
        long overlapping = WithRoots("6-Docs", "6-Docs/nested").ComputeDocsStamp();

        Assert.Equal(single, overlapping);
        Assert.NotEqual(0L, single);
    }

    public void Dispose() => _fixture.Dispose();
}
