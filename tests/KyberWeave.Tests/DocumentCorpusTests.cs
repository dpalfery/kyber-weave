using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Text;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The scoring engine, isolated from the corpus on disk. These cover the properties that
/// ranking depends on; <see cref="RetrievalRegressionTests"/> covers whether real
/// questions find real answers.
/// </summary>
public sealed class DocumentCorpusTests : IDisposable
{
    private readonly DocFixture _fixture = new();

    /// <summary>
    /// Six documents, all about one system. Five mention "session" — the shared
    /// vocabulary that used to decide every query — and only one mentions "kerberos".
    /// </summary>
    private DocumentCorpus Build()
    {
        _fixture.WithCatalog().WithSourceRoot("1-Presentation/Api");

        Write("alpha", "The session service manages session state for the session store.");
        Write("beta", "Session handling and session cookies across the session lifetime.");
        Write("gamma", "A session begins when the session token is issued.");
        Write("delta", "Session expiry and session renewal are configured per session.");
        Write("epsilon", "Session diagnostics for the session subsystem.");
        Write("zeta", "Kerberos ticket renewal for the session gateway.");

        return DocumentCorpus.Build(_fixture.Load());
    }

    private void Write(string slug, string body) => _fixture.Write($"6-Docs/{slug}.md", $"""
        ---
        id: api/{slug}
        title: {slug}
        doc-type: reference
        status: current
        component: MotorcycleRAG API
        owner: API maintainers
        last-reviewed: 2026-07-21
        ---
        # {slug}

        ## Detail

        {body}
        """);

    /// <summary>
    /// A term in nearly every document says nothing about which one is relevant, however
    /// often it occurs. Plain term frequency treated it as full evidence, which is how
    /// documents dense in shared vocabulary won every query.
    /// </summary>
    [Fact]
    public void ARareTermOutweighsAUbiquitousOne()
    {
        DocumentCorpus corpus = Build();

        Assert.True(corpus.InverseDocumentFrequency("kerberos") > corpus.InverseDocumentFrequency("session"));
    }

    [Fact]
    public void ATermInMostDocumentsIsTreatedAsCarryingNoInformation()
    {
        DocumentCorpus corpus = Build();

        Assert.False(corpus.IsInformative("session"));
        Assert.True(corpus.IsInformative("kerberos"));
    }

    /// <summary>
    /// Formal documentation rarely writes "why" or "getting", so rarity alone rates them
    /// as highly discriminating. They are scaffolding, and treating them as evidence
    /// rejected the very question this work started from.
    /// </summary>
    [Theory]
    [InlineData("why")]
    [InlineData("getting")]
    [InlineData("keep")]
    [InlineData("tell")]
    public void QuestionScaffoldingIsNeverInformative(string word)
    {
        Assert.False(Build().IsInformative(word));
    }

    /// <summary>
    /// One incidental word must not carry a document. This is what let "best hiking trails
    /// in patagonia" score 0.42 and return three confident results.
    /// </summary>
    [Fact]
    public void AQueryMostlyAbsentFromADocumentScoresFarBelowOneFullyPresent()
    {
        DocumentCorpus corpus = Build();
        DocumentModel zeta = corpus.Documents.Single(d => d.Frontmatter.Id == "api/zeta");

        double onTopic = Score(corpus, zeta, "kerberos ticket renewal");
        double offTopic = Score(corpus, zeta, "kerberos snorkelling parade marmalade");

        Assert.True(onTopic > offTopic * 2,
            $"on-topic {onTopic:0.000} should dominate mostly-absent {offTopic:0.000}");
    }

    [Fact]
    public void AQueryWithNoInformativeTermsScoresZero()
    {
        DocumentCorpus corpus = Build();
        DocumentModel zeta = corpus.Documents.Single(d => d.Frontmatter.Id == "api/zeta");

        Assert.Equal(0, Score(corpus, zeta, "why do we keep getting session"));
    }

    [Fact]
    public void OutOfVocabularyTermReturnsCalibratedCorpusShareBaseline()
    {
        DocumentCorpus corpus = Build();

        // Fixture corpus has N documents (6 api documents + 1 catalog = 7 documents).
        // Calibrated baseline with n_effective = max(1, floor(0.2 * N)):
        // IDF_oov = ln(1 + (N - n_eff + 0.5) / (n_eff + 0.5))
        int n = corpus.Documents.Count;
        double nEffective = Math.Max(1.0, Math.Floor(0.2 * n));
        double expected = Math.Log(1.0 + ((n - nEffective + 0.5) / (nEffective + 0.5)));
        double actual = corpus.InverseDocumentFrequency("novelunseenterm");

        Assert.Equal(expected, actual, precision: 6);
        Assert.True(actual < Math.Log((2 * n) + 2), "Calibrated IDF must be lower than uncalibrated ln(2N + 2).");
    }

    [Fact]
    public void QueryWithThreeMatchingTermsAndOneOovTermRetainsCoverageAboveRelevanceFloor()
    {
        DocumentCorpus corpus = Build();
        DocumentModel zeta = corpus.Documents.Single(d => d.Frontmatter.Id == "api/zeta");

        // "kerberos", "ticket", "renewal" match zeta; "novelunseenterm" is OOV.
        double score = Score(corpus, zeta, "kerberos ticket renewal novelunseenterm");

        Assert.True(score >= 0.25, $"Expected score {score:0.000} to be >= 0.25 relevance floor.");
    }

    [Fact]
    public void UnanswerableQueryWithThreeOovTermsCollapsesFarBelowFloor()
    {
        DocumentCorpus corpus = Build();
        DocumentModel zeta = corpus.Documents.Single(d => d.Frontmatter.Id == "api/zeta");

        // "kerberos" matches incidentally, but 3 terms ("snorkelling", "parade", "marmalade") are OOV.
        double score = Score(corpus, zeta, "kerberos snorkelling parade marmalade");

        Assert.True(score < 0.25, $"Expected score {score:0.000} to collapse far below 0.25 floor.");
        Assert.True(score < 0.10, $"Expected score {score:0.000} to be strictly under 0.10.");
    }

    private static double Score(DocumentCorpus corpus, DocumentModel doc, string query) =>
        corpus.ScoreBody(
            doc,
            TextVectorizer.VectorizeFused(query),
            TextVectorizer.Vectorize(query).Keys.ToList());

    public void Dispose() => _fixture.Dispose();
}

/// <summary>
/// Documents and code joins go stale at wildly different rates, so they are tracked
/// separately: the CodeGraph daemon rewrites its database continuously while
/// documentation is edited by hand.
/// </summary>
public sealed class DocumentIndexHostTests : IDisposable
{
    private readonly DocFixture _fixture = new();

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
        return new DocumentIndexHost(
            _fixture.Root,
            () => CodeGraphResolverAdapter.ForRepository(_fixture.Root),
            () => new Core.Docs.Parsing.DocumentLoader(_fixture.Root).Load());
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
        Directory.CreateDirectory(db);
        File.WriteAllText(Path.Combine(db, "codegraph.db"), "not a real database");

        host.Current();

        Assert.Equal(1, host.CorpusBuilds);
        Assert.Equal(2, host.JoinBuilds);
    }

    [Fact]
    public void ADocumentationChangeRebuildsTheCorpus()
    {
        DocumentIndexHost host = NewHost();
        host.Current();

        string doc = Path.Combine(_fixture.Root, "6-Docs", "thing.md");
        File.WriteAllText(doc, File.ReadAllText(doc) + "\n\n## More\n\nExtra prose.\n");
        File.SetLastWriteTimeUtc(doc, DateTime.UtcNow.AddMinutes(1));

        host.Current();

        Assert.Equal(2, host.CorpusBuilds);
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

        DocumentIndexHost Host(params string[] roots) => new(
            _fixture.Root,
            () => CodeGraphResolverAdapter.ForRepository(_fixture.Root),
            () => new Core.Docs.Parsing.DocumentLoader(_fixture.Root).Load(),
            roots,
            "6-Docs/catalog.md");

        long single = Host("6-Docs").ComputeDocsStamp();
        long overlapping = Host("6-Docs", "6-Docs/nested").ComputeDocsStamp();

        Assert.Equal(single, overlapping);
        Assert.NotEqual(0L, single);
    }

    public void Dispose() => _fixture.Dispose();
}
