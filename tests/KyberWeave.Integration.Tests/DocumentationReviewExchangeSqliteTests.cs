using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Docs.Analysis.Review;
using Xunit;
using static KyberWeave.Tests.ReviewExchangeFixtures;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The documentation review exchange against the real SQLite analysis cache: an import persists claims, fingerprints
/// and the verdict in one transaction, and a failed verdict write rolls all three back. The exchange's validation and
/// export rules run in memory in <c>DocumentationReviewExchangeTests</c>.
/// </summary>
public sealed class DocumentationReviewExchangeSqliteTests
{
    [Fact]
    public async Task ImportRealSqliteAfterCurrentExportPersistsClaimsFingerprintsAndVerdictAtomically()
    {
        await RequireSqliteAsync();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        AnalysisCandidate candidate = Candidate("sqlite-round-trip", AnalysisRuleKind.Conflict);
        DocumentationReviewExchange exchange = new DocumentationReviewExchange(persistence);
        ReviewCandidateBundle exported = exchange.Export([candidate]).Bundle;
        ReviewVerdictBundle bundle = VerdictBundle(
            exported,
            VerdictItem(exported.Candidates[0], AnalysisVerdictLabel.Benign));

        ReviewImportResult imported = exchange.Import(Serialize(bundle), [candidate]);

        Assert.True(imported.Success, Join(imported.Diagnostics));
        Assert.Equal(2, persistence.LoadClaims(await CurrentClaimIds(persistence)).Count);
        Assert.Equal(
            candidate.Id,
            Assert.Single(persistence.LoadCandidateFingerprints([candidate.Id])).Value.CandidateId);
        Assert.Equal(
            AnalysisVerdictLabel.Benign,
            Assert.Single(persistence.LoadVerdicts([candidate.Id])).Value.Label);
    }


    [Fact]
    public async Task ImportRealSqliteWhenVerdictWriteFailsRollsBackClaimsFingerprintsAndVerdicts()
    {
        await RequireSqliteAsync();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        AnalysisCandidate candidate = Candidate("sqlite-rollback", AnalysisRuleKind.Conflict);
        DocumentationReviewExchange exchange = new DocumentationReviewExchange(persistence);
        ReviewCandidateBundle exported = exchange.Export([candidate]).Bundle;
        await RunSqliteAsync(
            persistence.DatabasePath,
            "CREATE TRIGGER reject_review BEFORE INSERT ON analysis_verdicts " +
            "BEGIN SELECT RAISE(ABORT, 'forced review failure'); END;");

        ReviewImportResult imported = exchange.Import(
            Serialize(VerdictBundle(
                exported,
                VerdictItem(exported.Candidates[0], AnalysisVerdictLabel.Benign))),
            [candidate]);

        Assert.False(imported.Success);
        Assert.Equal("0", (await QuerySqliteAsync(persistence.DatabasePath, "SELECT COUNT(*) FROM analysis_claims;")).Trim());
        Assert.Empty(persistence.LoadCandidateFingerprints([candidate.Id]));
        Assert.Empty(persistence.LoadVerdicts([candidate.Id]));
    }

    private static async Task<IReadOnlyCollection<string>> CurrentClaimIds(IAnalysisPersistence persistence)
    {
        if (persistence is not SqliteAnalysisPersistence sqlite)
            throw new InvalidOperationException("A SQLite persistence adapter is required.");
        return (await QuerySqliteAsync(sqlite.DatabasePath, "SELECT CAST(id AS TEXT) FROM analysis_claims;"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static TempDirectory SafeRepository() => SqliteTestFixture.SafeRepository();

    private static Task RequireSqliteAsync() =>
        SqliteTestFixture.RequireSqliteAsync("sqlite3 is unavailable; SQLite review import parity was not run.");

    private static Task<string> QuerySqliteAsync(string databasePath, string sql) =>
        SqliteTestFixture.QuerySqliteAsync(databasePath, sql);

    private static Task RunSqliteAsync(string databasePath, string sql) =>
        SqliteTestFixture.RunSqliteAsync(databasePath, sql);
}
