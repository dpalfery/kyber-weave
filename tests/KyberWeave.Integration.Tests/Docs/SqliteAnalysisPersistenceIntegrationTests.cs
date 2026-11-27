using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The analysis cache over the real disk and the in-process SQLite adapter: it creates its state,
/// round-trips every persisted shape unchanged, and refuses a cache written by a newer version.
/// </summary>
public sealed class SqliteAnalysisPersistenceIntegrationTests
{
    private static readonly float[] ExpectedNormalizedVector = [0.6f, 0.8f];

    [Fact]
    public void OpeningASafeCheckoutCreatesTheCacheFolderAndFile()
    {
        using TempDirectory repository = SafeRepository();

        SqliteAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);

        Assert.True(persistence.IsAvailable);
        Assert.Equal(DatabasePath(repository.Path), persistence.DatabasePath);
        Assert.True(Directory.Exists(Path.Combine(repository.Path, ".kyber-weave", "cache")));
        Assert.True(File.Exists(persistence.DatabasePath));
    }

    [Fact]
    public void ClaimsCandidatesVerdictsAndEmbeddingsRoundTripUnchanged()
    {
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        const string hostile = "'close\n); DROP TABLE analysis_claims; --\nnext";
        PersistedClaim claim = Claim(
            hostile,
            filePath: "docs/it's-a-path.md",
            text: "Operator's first line\nsecond line; SELECT * FROM analysis_verdicts; --\n\"quoted\"");
        PersistedCandidateFingerprint candidate = Candidate(hostile);
        AnalysisVerdict verdict = new(
            candidate.CandidateId,
            AnalysisVerdictLabel.DistinctSenses,
            0.91,
            "It isn't a conflict.\nKeep both; -- literally.",
            ["claim-left", "claim-right"],
            "docs/glossary.md#loop",
            [new ProposedGlossarySense("loop", "An autonomous cycle; 'not' a literal loop.", ["component:Agents"], ["churn loop"])]);
        EmbeddingCacheKey key = new(
            hostile,
            "http://127.0.0.1:1234/v1/embeddings",
            "local-model",
            2,
            "float");
        StoredEmbedding embedding = new(key, ExpectedNormalizedVector);

        persistence.SaveClaims([claim]);
        persistence.SaveCandidateFingerprints([candidate]);
        persistence.SaveVerdicts([verdict]);
        persistence.SaveEmbeddings([embedding]);

        Assert.Equal(claim, persistence.LoadClaims([hostile])[hostile]);
        AssertCandidateEqual(candidate, persistence.LoadCandidateFingerprints([hostile])[hostile]);
        AnalysisVerdict loadedVerdict = persistence.LoadVerdicts([hostile])[hostile];
        Assert.Equal(verdict.CandidateId, loadedVerdict.CandidateId);
        Assert.Equal(verdict.Label, loadedVerdict.Label);
        Assert.Equal(verdict.Confidence, loadedVerdict.Confidence);
        Assert.Equal(verdict.Rationale, loadedVerdict.Rationale);
        Assert.Equal(verdict.EvidenceIds, loadedVerdict.EvidenceIds);
        Assert.Equal(verdict.RecommendedCanonicalLocation, loadedVerdict.RecommendedCanonicalLocation);
        ProposedGlossarySense expectedSense = Assert.Single(verdict.ProposedGlossarySenses!);
        ProposedGlossarySense actualSense = Assert.Single(loadedVerdict.ProposedGlossarySenses!);
        Assert.Equal(expectedSense.Term, actualSense.Term);
        Assert.Equal(expectedSense.Definition, actualSense.Definition);
        Assert.Equal(expectedSense.Scopes, actualSense.Scopes);
        Assert.Equal(expectedSense.Aliases, actualSense.Aliases);
        StoredEmbedding loadedEmbedding = Assert.Single(persistence.LoadEmbeddings([key])).Value;
        Assert.Equal(key, loadedEmbedding.Key);
        Assert.Equal(ExpectedNormalizedVector, loadedEmbedding.Vector);
    }

    [Fact]
    public void ACacheStampedWithANewerSchemaVersionIsRefused()
    {
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence first = DocsPorts.Sqlite(repository.Path);
        using (SqliteConnection connection = new($"Data Source={first.DatabasePath}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            // CA2100 requires a constant command text; SchemaVersion is 1, so 2 is newer.
            command.CommandText = "PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => DocsPorts.Sqlite(repository.Path));

        Assert.Contains("newer than supported version", failure.Message, StringComparison.Ordinal);
    }

    private static TempDirectory SafeRepository()
    {
        TempDirectory repository = new TempDirectory();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(stateDirectory, ".gitignore"), "cache/\n");
        return repository;
    }

    private static string DatabasePath(string repositoryRoot) =>
        Path.Combine(repositoryRoot, ".kyber-weave", "cache", "docs-analysis.sqlite3");

    private static PersistedClaim Claim(
        string id,
        string filePath = "docs/reference.md",
        int startLine = 7,
        string text = "The processor retains approved reviewer verdicts.") =>
        new(
            id,
            "shared-content-hash",
            "context-" + id,
            "reference/runtime",
            filePath,
            startLine,
            startLine + 1,
            text);

    private static PersistedCandidateFingerprint Candidate(string id) =>
        new(
            id,
            AnalysisRuleKind.Conflict,
            "loop",
            "candidate-set-v1",
            DocumentationAnalyzer.AnalyzerVersion,
            DocumentationAnalyzer.RubricVersion,
            ["shared-content-hash", "other-content-hash"]);

    private static void AssertCandidateEqual(
        PersistedCandidateFingerprint expected,
        PersistedCandidateFingerprint actual)
    {
        Assert.Equal(expected.CandidateId, actual.CandidateId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.NormalizedTerm, actual.NormalizedTerm);
        Assert.Equal(expected.CandidateSetHash, actual.CandidateSetHash);
        Assert.Equal(expected.AnalyzerVersion, actual.AnalyzerVersion);
        Assert.Equal(expected.RubricVersion, actual.RubricVersion);
        Assert.Equal(expected.ClaimContentHashes, actual.ClaimContentHashes);
    }
}
