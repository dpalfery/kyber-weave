using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Docs.Analysis.Review;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the content-addressed agent review exchange. Import validation must remain ahead
/// of persistence so one malformed verdict cannot partially poison the reusable cache.
/// </summary>
public sealed class DocumentationReviewExchangeTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public void Export_PendingCandidates_UsesVersionedSchemaAndCompleteBoundedEvidence()
    {
        var exact = Candidate("exact", AnalysisRuleKind.Duplicate, isExact: true);
        var pending = Candidate(
            "pending",
            AnalysisRuleKind.Conflict,
            sources: [CandidateSourceKind.Graph, CandidateSourceKind.Lexical],
            score: new CandidateScore(0.71, 0.84, 1));

        var result = Exchange().Export([exact, pending]);

        Assert.Equal("kyber-weave.docs-review.candidates/v1", result.Bundle.Schema);
        Assert.Equal(DocumentationAnalyzer.AnalyzerVersion, result.Bundle.AnalyzerVersion);
        Assert.Equal(DocumentationAnalyzer.RubricVersion, result.Bundle.RubricVersion);
        Assert.Matches("^[a-f0-9]{64}$", result.Bundle.CandidateSetHash);
        Assert.Equal(5, result.Bundle.Rubric.Labels.Count);
        var item = Assert.Single(result.Bundle.Candidates);
        Assert.Equal(pending.Id, item.CandidateId);
        Assert.Equal(pending.Kind, item.Kind);
        Assert.Equal(pending.Score, item.Score);
        Assert.Equal(pending.Sources, item.Sources);
        Assert.Equal(pending.Claims.Select(claim => claim.ContentHash), item.ClaimContentHashes);
        Assert.All(item.Evidence, evidence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(evidence.Id));
            Assert.False(string.IsNullOrWhiteSpace(evidence.Excerpt));
            Assert.False(string.IsNullOrWhiteSpace(evidence.ContentHash));
            Assert.False(string.IsNullOrWhiteSpace(evidence.ContextualHash));
            Assert.True(evidence.StartLine > 0);
            Assert.True(evidence.EndLine >= evidence.StartLine);
        });
        using var json = JsonDocument.Parse(result.Json);
        Assert.Equal(
            "kyber-weave.docs-review.candidates/v1",
            json.RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public void Export_LongEvidence_EnforcesPerExcerptAndAggregateCharacterBudgets()
    {
        var candidate = Candidate(
            "bounded",
            AnalysisRuleKind.Conflict,
            claimText: new string('x', 2_000));
        var options = new ReviewExportOptions(MaxExcerptCharacters: 40, CharacterBudget: 65);

        var result = Exchange().Export([candidate], options);

        var excerpts = Assert.Single(result.Bundle.Candidates).Evidence.Select(item => item.Excerpt).ToArray();
        Assert.All(excerpts, excerpt => Assert.True(excerpt.Length <= options.MaxExcerptCharacters));
        Assert.True(excerpts.Sum(excerpt => excerpt.Length) <= options.CharacterBudget);
        Assert.Equal(excerpts.Sum(excerpt => excerpt.Length), result.ExportedExcerptCharacters);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Export_CandidateSetHashIsStableAcrossInputOrderAndChangesWithCurrentContent()
    {
        var first = Candidate("first", AnalysisRuleKind.Conflict);
        var second = Candidate("second", AnalysisRuleKind.Terminology, term: "loop");
        var exchange = Exchange();

        var ordered = exchange.Export([first, second]).Bundle.CandidateSetHash;
        var reversed = exchange.Export([second, first]).Bundle.CandidateSetHash;
        var changed = exchange.Export([
            first,
            second with
            {
                Claims = second.Claims
                    .Select(claim => claim with { ContentHash = claim.ContentHash + "-changed" })
                    .ToArray()
            }
        ]).Bundle.CandidateSetHash;

        Assert.Equal(ordered, reversed);
        Assert.NotEqual(ordered, changed);
    }

    [Fact]
    public void Export_UsesCachedVerdictsToReExportLowConfidenceAndUncertainButSuppressConfirmedBenign()
    {
        var low = Candidate("low", AnalysisRuleKind.Duplicate);
        var uncertain = Candidate("uncertain", AnalysisRuleKind.Conflict);
        var benign = Candidate("benign", AnalysisRuleKind.Terminology, term: "loop");
        var persistence = new RecordingPersistence(
            Verdict(low, AnalysisVerdictLabel.Duplicate, 0.79),
            Verdict(uncertain, AnalysisVerdictLabel.Uncertain, 0.99),
            Verdict(benign, AnalysisVerdictLabel.Benign, 0.95));

        var result = Exchange(persistence, confidence: 0.80).Export([low, uncertain, benign]);

        Assert.Equal(
            [low.Id, uncertain.Id],
            result.Bundle.Candidates.Select(item => item.CandidateId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Export_ExcludesEveryHighConfidenceResolvedVerdictButKeepsUncertainPending()
    {
        var duplicate = Candidate("duplicate", AnalysisRuleKind.Duplicate);
        var conflict = Candidate("conflict", AnalysisRuleKind.Conflict);
        var senses = Candidate("senses", AnalysisRuleKind.Terminology, term: "loop");
        var benign = Candidate("benign", AnalysisRuleKind.Conflict);
        var uncertain = Candidate("uncertain", AnalysisRuleKind.Conflict);
        var persistence = new RecordingPersistence(
            Verdict(duplicate, AnalysisVerdictLabel.Duplicate, 0.90),
            Verdict(conflict, AnalysisVerdictLabel.Conflict, 0.90),
            Verdict(senses, AnalysisVerdictLabel.DistinctSenses, 0.90),
            Verdict(benign, AnalysisVerdictLabel.Benign, 0.90),
            Verdict(uncertain, AnalysisVerdictLabel.Uncertain, 0.99));

        var result = Exchange(persistence).Export([duplicate, conflict, senses, benign, uncertain]);

        Assert.Equal([uncertain.Id], result.Bundle.Candidates.Select(item => item.CandidateId));
        Assert.All(
            new[] { duplicate, conflict, senses, benign },
            candidate => Assert.DoesNotContain(result.Bundle.Candidates, item => item.CandidateId == candidate.Id));
    }

    [Fact]
    public void Export_EvidenceIdsRemainStableAcrossSourceMovesAndDisambiguateRepeatedContentHashes()
    {
        var original = Candidate("move-stable", AnalysisRuleKind.Conflict);
        original = original with
        {
            Claims = original.Claims
                .Select(claim => claim with { ContentHash = "repeated-content" })
                .ToArray()
        };
        var moved = original with
        {
            Claims = original.Claims
                .Reverse()
                .Select((claim, index) => claim with
                {
                    ContextualHash = "moved-context-" + index,
                    DocumentIdentity = "moved/document-" + index,
                    FilePath = $"/repo/moved/{index}.md",
                    StartLine = 100 + index,
                    EndLine = 100 + index
                })
                .ToArray()
        };
        var exchange = Exchange();

        var first = Assert.Single(exchange.Export([original]).Bundle.Candidates);
        var second = Assert.Single(exchange.Export([moved]).Bundle.Candidates);

        Assert.Equal(first.Evidence.Select(item => item.Id), second.Evidence.Select(item => item.Id));
        Assert.Equal(2, first.Evidence.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Export_WhenBudgetCannotCoverNextCandidateStopsWithoutEmptyEvidenceAndHashesEmittedSet()
    {
        var first = Candidate("a-first", AnalysisRuleKind.Conflict, claimText: "12345");
        var second = Candidate("b-second", AnalysisRuleKind.Conflict, claimText: "12345");
        var third = Candidate("c-third", AnalysisRuleKind.Conflict, claimText: "12345");
        var exchange = Exchange();
        var options = new ReviewExportOptions(MaxExcerptCharacters: 20, CharacterBudget: 30);

        var result = exchange.Export([first, second, third], options);
        var firstOnly = exchange.Export([first]).Bundle;

        var emitted = Assert.Single(result.Bundle.Candidates);
        Assert.Equal(first.Id, emitted.CandidateId);
        Assert.All(emitted.Evidence, evidence => Assert.NotEmpty(evidence.Excerpt));
        Assert.Equal(firstOnly.CandidateSetHash, result.Bundle.CandidateSetHash);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Import_ValidBundlePersistsAllVerdictsInOneAtomicCall()
    {
        var candidates = new[]
        {
            Candidate("duplicate", AnalysisRuleKind.Duplicate),
            Candidate("terminology", AnalysisRuleKind.Terminology, term: "loop")
        };
        var persistence = new RecordingPersistence();
        var exchange = Exchange(persistence);
        var export = exchange.Export(candidates).Bundle;
        var verdicts = VerdictBundle(
            export,
            VerdictItem(export.Candidates[0], AnalysisVerdictLabel.Duplicate),
            VerdictItem(
                export.Candidates[1],
                AnalysisVerdictLabel.DistinctSenses,
                senses:
                [
                    new ProposedGlossarySense(
                        "loop",
                        "The autonomous agent execution cycle.",
                        ["component:Automation"],
                        ["Codex loop"])
                ]));

        var result = exchange.Import(Serialize(verdicts), candidates);

        Assert.True(result.Success, Join(result.Diagnostics));
        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(1, persistence.SaveVerdictCallCount);
        Assert.Equal(2, persistence.Verdicts.Count);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("analyzer-version")]
    [InlineData("rubric-version")]
    [InlineData("candidate-set-hash")]
    [InlineData("candidate-id")]
    [InlineData("claim-hash")]
    [InlineData("label")]
    [InlineData("confidence")]
    [InlineData("evidence-reference")]
    [InlineData("glossary-sense")]
    [InlineData("glossary-blank-scope")]
    public void Import_WhenAnyBundleContractIsInvalidRejectsEverythingWithReview001(string scenario)
    {
        var candidate = Candidate(
            "review",
            scenario is "glossary-sense" or "glossary-blank-scope"
                ? AnalysisRuleKind.Terminology
                : AnalysisRuleKind.Duplicate,
            term: scenario is "glossary-sense" or "glossary-blank-scope" ? "loop" : null);
        var persistence = new RecordingPersistence();
        var exchange = Exchange(persistence);
        var export = exchange.Export([candidate]).Bundle;
        var item = VerdictItem(
            export.Candidates[0],
            scenario is "glossary-sense" or "glossary-blank-scope"
                ? AnalysisVerdictLabel.DistinctSenses
                : AnalysisVerdictLabel.Duplicate,
            senses: scenario switch
            {
                "glossary-sense" =>
                    [new ProposedGlossarySense("loop", "", ["invalid-scope"], [])],
                "glossary-blank-scope" =>
                    [new ProposedGlossarySense("loop", "Agent cycle.", ["component:   "], [])],
                _ => null
            });
        var bundle = VerdictBundle(export, item);
        bundle = scenario switch
        {
            "schema" => bundle with { Schema = "kyber-weave.docs-review.verdicts/v2" },
            "analyzer-version" => bundle with { AnalyzerVersion = "analyzer/v0" },
            "rubric-version" => bundle with { RubricVersion = "rubric/v0" },
            "candidate-set-hash" => bundle with { CandidateSetHash = "stale-set" },
            "candidate-id" => bundle with
            {
                Verdicts = [item with { CandidateId = "unknown-candidate" }]
            },
            "claim-hash" => bundle with
            {
                Verdicts = [item with { ClaimContentHashes = ["stale-content"] }]
            },
            "label" => bundle with
            {
                Verdicts = [item with { Label = AnalysisVerdictLabel.Conflict }]
            },
            "confidence" => bundle with
            {
                Verdicts = [item with { Confidence = 1.01 }]
            },
            "evidence-reference" => bundle with
            {
                Verdicts = [item with { EvidenceIds = ["unknown-evidence"] }]
            },
            "glossary-sense" => bundle,
            "glossary-blank-scope" => bundle,
            _ => throw new InvalidOperationException(scenario)
        };

        var result = exchange.Import(Serialize(bundle), [candidate]);

        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(0, persistence.SaveVerdictCallCount);
        var finding = Assert.Single(result.Diagnostics.Items);
        Assert.Equal("KW-DOC-REVIEW-001", finding.Code);
        Assert.Equal(Severity.Error, finding.Severity);
    }

    [Fact]
    public void Import_MalformedJsonRejectsWithoutWriting()
    {
        var candidate = Candidate("malformed", AnalysisRuleKind.Conflict);
        var persistence = new RecordingPersistence();

        var result = Exchange(persistence).Import("{\"schema\":", [candidate]);

        Assert.False(result.Success);
        Assert.Empty(persistence.Verdicts);
        Assert.Equal(0, persistence.SaveVerdictCallCount);
        Assert.Contains(result.Diagnostics.Items, item => item.Code == "KW-DOC-REVIEW-001");
    }

    [Fact]
    public void Import_OneInvalidVerdictRejectsOtherwiseValidVerdictsAtomically()
    {
        var first = Candidate("first", AnalysisRuleKind.Duplicate);
        var second = Candidate("second", AnalysisRuleKind.Conflict);
        var candidates = new[] { first, second };
        var persistence = new RecordingPersistence();
        var exchange = Exchange(persistence);
        var export = exchange.Export(candidates).Bundle;
        var valid = VerdictItem(export.Candidates[0], AnalysisVerdictLabel.Duplicate);
        var invalid = VerdictItem(export.Candidates[1], AnalysisVerdictLabel.Conflict) with
        {
            EvidenceIds = ["not-in-export"]
        };

        var result = exchange.Import(Serialize(VerdictBundle(export, valid, invalid)), candidates);

        Assert.False(result.Success);
        Assert.Equal(0, persistence.SaveVerdictCallCount);
        Assert.Empty(persistence.Verdicts);
    }

    [Fact]
    public void Import_HighConfidenceBenignThenExportUnchangedCandidateSuppressesIt()
    {
        var candidate = Candidate("benign-round-trip", AnalysisRuleKind.Conflict);
        var persistence = new RecordingPersistence();
        var exchange = Exchange(persistence, confidence: 0.80);
        var exported = exchange.Export([candidate]).Bundle;
        var bundle = VerdictBundle(
            exported,
            VerdictItem(exported.Candidates[0], AnalysisVerdictLabel.Benign, confidence: 0.90));

        var imported = exchange.Import(Serialize(bundle), [candidate]);
        var reExported = exchange.Export([candidate]);

        Assert.True(imported.Success, Join(imported.Diagnostics));
        Assert.Empty(reExported.Bundle.Candidates);
    }

    [Fact]
    public void Import_RealSqliteAfterCurrentExport_PersistsClaimsFingerprintsAndVerdictAtomically()
    {
        RequireSqlite();
        using var repository = SafeRepository();
        IAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);
        var candidate = Candidate("sqlite-round-trip", AnalysisRuleKind.Conflict);
        var exchange = new DocumentationReviewExchange(persistence);
        var exported = exchange.Export([candidate]).Bundle;
        var bundle = VerdictBundle(
            exported,
            VerdictItem(exported.Candidates[0], AnalysisVerdictLabel.Benign));

        var imported = exchange.Import(Serialize(bundle), [candidate]);

        Assert.True(imported.Success, Join(imported.Diagnostics));
        Assert.Equal(2, persistence.LoadClaims(CurrentClaimIds(persistence)).Count);
        Assert.Equal(
            candidate.Id,
            Assert.Single(persistence.LoadCandidateFingerprints([candidate.Id])).Value.CandidateId);
        Assert.Equal(
            AnalysisVerdictLabel.Benign,
            Assert.Single(persistence.LoadVerdicts([candidate.Id])).Value.Label);
    }

    [Fact]
    public void Import_RealSqliteWhenVerdictWriteFails_RollsBackClaimsFingerprintsAndVerdicts()
    {
        RequireSqlite();
        using var repository = SafeRepository();
        var persistence = new SqliteAnalysisPersistence(repository.Path);
        var candidate = Candidate("sqlite-rollback", AnalysisRuleKind.Conflict);
        var exchange = new DocumentationReviewExchange(persistence);
        var exported = exchange.Export([candidate]).Bundle;
        RunSqlite(
            persistence.DatabasePath,
            "CREATE TRIGGER reject_review BEFORE INSERT ON analysis_verdicts " +
            "BEGIN SELECT RAISE(ABORT, 'forced review failure'); END;");

        var imported = exchange.Import(
            Serialize(VerdictBundle(
                exported,
                VerdictItem(exported.Candidates[0], AnalysisVerdictLabel.Benign))),
            [candidate]);

        Assert.False(imported.Success);
        Assert.Equal("0", QuerySqlite(persistence.DatabasePath, "SELECT COUNT(*) FROM analysis_claims;").Trim());
        Assert.Empty(persistence.LoadCandidateFingerprints([candidate.Id]));
        Assert.Empty(persistence.LoadVerdicts([candidate.Id]));
    }

    private static DocumentationReviewExchange Exchange(
        RecordingPersistence? persistence = null,
        double confidence = 0.80) =>
        new(persistence ?? new RecordingPersistence(), confidence);

    private static AnalysisCandidate Candidate(
        string id,
        AnalysisRuleKind kind,
        bool isExact = false,
        string? term = null,
        string claimText = "The runner emits the configured documentation review evidence.",
        IReadOnlyList<CandidateSourceKind>? sources = null,
        CandidateScore? score = null)
    {
        var claims = new[]
        {
            Claim(id + "-left", "hash-" + id + "-left", claimText, 10),
            Claim(id + "-right", "hash-" + id + "-right", claimText + " Related context.", 20)
        };
        return new AnalysisCandidate(
            id,
            kind,
            claims,
            score ?? new CandidateScore(0.72, null, 1),
            isExact,
            term,
            sources ?? [CandidateSourceKind.Graph]);
    }

    private static Claim Claim(string id, string contentHash, string text, int line) =>
        new(
            ClaimKind.Paragraph,
            text,
            "Behavior\n" + text,
            contentHash,
            "context-" + id,
            "docs/" + id,
            "Runtime",
            "Behavior",
            "/repo/docs/" + id + ".md",
            line,
            line + 1,
            IgnoreRule.None);

    private static AnalysisVerdict Verdict(
        AnalysisCandidate candidate,
        AnalysisVerdictLabel label,
        double confidence) =>
        new(candidate.Id, label, confidence, "Reviewer disposition.");

    private static ReviewVerdictBundle VerdictBundle(
        ReviewCandidateBundle export,
        params ReviewVerdictItem[] verdicts) =>
        new(
            "kyber-weave.docs-review.verdicts/v1",
            export.AnalyzerVersion,
            export.RubricVersion,
            export.CandidateSetHash,
            verdicts);

    private static ReviewVerdictItem VerdictItem(
        ReviewCandidateItem candidate,
        AnalysisVerdictLabel label,
        double confidence = 0.90,
        IReadOnlyList<ProposedGlossarySense>? senses = null) =>
        new(
            candidate.CandidateId,
            label,
            confidence,
            "Reviewer evaluated every supplied evidence location.",
            candidate.ClaimContentHashes,
            candidate.Evidence.Select(evidence => evidence.Id).ToArray(),
            null,
            senses);

    private static string Serialize(ReviewVerdictBundle bundle) =>
        JsonSerializer.Serialize(bundle, JsonOptions);

    private static string Join(DiagnosticReport diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Items);

    private static IReadOnlyCollection<string> CurrentClaimIds(IAnalysisPersistence persistence)
    {
        if (persistence is not SqliteAnalysisPersistence sqlite)
            throw new InvalidOperationException("A SQLite persistence adapter is required.");
        return QuerySqlite(sqlite.DatabasePath, "SELECT CAST(id AS TEXT) FROM analysis_claims;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static TempDirectory SafeRepository() => SqliteTestFixture.SafeRepository();

    private static void RequireSqlite() =>
        SqliteTestFixture.RequireSqlite("sqlite3 is unavailable; SQLite review import parity was not run.");

    private static string QuerySqlite(string databasePath, string sql) =>
        SqliteTestFixture.QuerySqlite(databasePath, sql);

    private static ProcessResult RunSqlite(string databasePath, string sql) =>
        SqliteTestFixture.RunSqlite(databasePath, sql);

    private static ProcessStartInfo SqliteStartInfo() =>
        SqliteTestFixture.SqliteStartInfo();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class RecordingPersistence(params AnalysisVerdict[] verdicts) : IAnalysisPersistence
    {
        public Dictionary<string, AnalysisVerdict> Verdicts { get; } =
            verdicts.ToDictionary(verdict => verdict.CandidateId, StringComparer.Ordinal);

        public int SaveVerdictCallCount { get; private set; }
        public bool IsAvailable => true;

        public IReadOnlyDictionary<string, AnalysisVerdict> LoadVerdicts(
            IReadOnlyCollection<string> candidateIds) =>
            Verdicts
                .Where(item => candidateIds.Contains(item.Key, StringComparer.Ordinal))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

        public void SaveVerdicts(IReadOnlyCollection<AnalysisVerdict> imported)
        {
            SaveVerdictCallCount++;
            foreach (var verdict in imported) Verdicts[verdict.CandidateId] = verdict;
        }

        public IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> LoadEmbeddings(
            IReadOnlyCollection<EmbeddingCacheKey> keys) =>
            new Dictionary<EmbeddingCacheKey, StoredEmbedding>();

        public void SaveEmbeddings(IReadOnlyCollection<StoredEmbedding> embeddings)
        {
        }
    }
}
