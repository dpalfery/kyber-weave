using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// T07 — the analysis cache is optional for ordinary analysis, but once enabled its
/// content-addressed vectors and review decisions must be durable and safe to reuse.
/// </summary>
public sealed class AnalysisPersistenceTests
{
    private static readonly float[] ExpectedNormalizedVector = [0.6f, 0.8f];

    [Theory]
    [InlineData("cache/\n", true)]
    [InlineData("# Kyber-Weave local state\ncache/\n", true)]
    [InlineData("cache\n", false)]
    [InlineData("/cache/\n", false)]
    [InlineData(".kyber-weave/cache/\n", false)]
    [InlineData("cache/*\n", false)]
    public async Task IsSafeRequiresExactCacheDirectoryIgnoreEntry(string ignoreContents, bool expected)
    {
        using TempDirectory repository = new TempDirectory();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        Directory.CreateDirectory(stateDirectory);
        await File.WriteAllTextAsync(Path.Combine(stateDirectory, ".gitignore"), ignoreContents);

        bool actual = await DocsPorts.CacheSafety.IsSafeAsync(repository.Path);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("cache/\n!cache/\n")]
    [InlineData("cache/\n!cache/docs-analysis.sqlite3\n")]
    [InlineData("cache/\n!**/cache/**\n")]
    public async Task IsSafeWhenLaterRuleNegatesCacheProtectionReturnsFalse(string ignoreContents)
    {
        using TempDirectory repository = new TempDirectory();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        Directory.CreateDirectory(stateDirectory);
        await File.WriteAllTextAsync(Path.Combine(stateDirectory, ".gitignore"), ignoreContents);

        Assert.False(await DocsPorts.CacheSafety.IsSafeAsync(repository.Path));
    }

    [Fact]
    public async Task IsSafeWhenDatabaseIsAlreadyTrackedReturnsFalse()
    {
        await RequireGit();
        using TempDirectory repository = SafeRepository(createCache: true);
        string databasePath = DatabasePath(repository.Path);
        await File.WriteAllTextAsync(databasePath, "tracked cache placeholder");
        await RunGit(repository.Path, "init");
        await RunGit(repository.Path, "add", "-f", ".kyber-weave/cache/docs-analysis.sqlite3");

        Assert.False(await DocsPorts.CacheSafety.IsSafeAsync(repository.Path));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("cache")]
    [InlineData("database")]
    public async Task ConstructorWhenPersistencePathContainsSymbolicLinkRejectsWithoutChangingExternalTarget(
        string linkedSegment)
    {
        await RequireSqlite();
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Symbolic-link creation requires platform-specific privileges on Windows.");

        using TempDirectory repository = new TempDirectory();
        using TempDirectory external = new TempDirectory();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        string cacheDirectory = Path.Combine(stateDirectory, "cache");
        string databasePath = Path.Combine(cacheDirectory, "docs-analysis.sqlite3");
        string sentinelPath = Path.Combine(external.Path, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "external state must remain unchanged");

        switch (linkedSegment)
        {
            case "state":
                Directory.CreateSymbolicLink(stateDirectory, external.Path);
                await File.WriteAllTextAsync(Path.Combine(external.Path, ".gitignore"), "cache/\n");
                break;
            case "cache":
                Directory.CreateDirectory(stateDirectory);
                await File.WriteAllTextAsync(Path.Combine(stateDirectory, ".gitignore"), "cache/\n");
                Directory.CreateSymbolicLink(cacheDirectory, external.Path);
                break;
            case "database":
                Directory.CreateDirectory(cacheDirectory);
                await File.WriteAllTextAsync(Path.Combine(stateDirectory, ".gitignore"), "cache/\n");
                File.CreateSymbolicLink(databasePath, sentinelPath);
                break;
        }

        Assert.Throws<InvalidOperationException>(() => DocsPorts.Sqlite(repository.Path));
        Assert.Equal("external state must remain unchanged", await File.ReadAllTextAsync(sentinelPath));
        Assert.False(File.Exists(Path.Combine(external.Path, "docs-analysis.sqlite3")));
    }

    [Fact]
    public void ConstructorWhenCacheIsNotSafelyIgnoredDisablesReadsAndWritesWithoutCreatingState()
    {
        using TempDirectory repository = new TempDirectory();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        string cacheDirectory = Path.Combine(stateDirectory, "cache");

        IAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);

        Assert.False(persistence.IsAvailable);
        Assert.Empty(persistence.LoadClaims(["claim-1"]));
        Assert.Empty(persistence.LoadCandidateFingerprints(["candidate-1"]));
        Assert.Empty(persistence.LoadVerdicts(["candidate-1"]));
        Assert.Empty(persistence.LoadEmbeddings([Key("context-1")]));
        Assert.False(Directory.Exists(stateDirectory));
        Assert.False(Directory.Exists(cacheDirectory));
        Assert.Throws<InvalidOperationException>(() => persistence.SaveClaims([Claim("claim-1")]));
        Assert.Throws<InvalidOperationException>(() => persistence.SaveCandidateFingerprints([Candidate("candidate-1")]));
        Assert.Throws<InvalidOperationException>(() => persistence.SaveVerdicts([Verdict("candidate-1")]));
        Assert.Throws<InvalidOperationException>(() => persistence.SaveEmbeddings([Embedding(Key("context-1"))]));
        Assert.False(Directory.Exists(stateDirectory));
        Assert.False(Directory.Exists(cacheDirectory));
    }

    [Fact]
    public async Task ConstructorWhenCacheIsSafeInitializesVersionedSchemaIdempotently()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence first = DocsPorts.Sqlite(repository.Path);
        PersistedClaim claim = Claim("claim-idempotent");
        first.SaveClaims([claim]);

        SqliteAnalysisPersistence second = DocsPorts.Sqlite(repository.Path);

        Assert.True(first.IsAvailable);
        Assert.True(second.IsAvailable);
        Assert.Equal(
            SqliteAnalysisPersistence.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (await QuerySqlite(second.DatabasePath, "PRAGMA user_version;")).Trim());
        Assert.Equal(claim, Assert.Single(second.LoadClaims([claim.Id])).Value);
    }

    [Fact]
    public async Task SaveAndLoadClaimsPreservesDuplicateOccurrencesAndLineAddressableText()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        PersistedClaim first = Claim("claim-left", filePath: "docs/left.md", startLine: 11);
        PersistedClaim second = Claim("claim-right", filePath: "docs/right.md", startLine: 29);

        persistence.SaveClaims([first, second]);
        IReadOnlyDictionary<string, PersistedClaim> loaded = persistence.LoadClaims([first.Id, second.Id]);

        Assert.Equal(first, loaded[first.Id]);
        Assert.Equal(second, loaded[second.Id]);
        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public async Task SaveAndLoadEmbeddingsUsesEveryContentProviderModelAndShapeKeyField()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        EmbeddingCacheKey storedKey = Key("context", provider: "local-a", model: "embed-a", dimensions: 2);
        persistence.SaveEmbeddings([Embedding(storedKey)]);
        EmbeddingCacheKey[] requested =
        [
            storedKey,
            Key("other-context", provider: "local-a", model: "embed-a", dimensions: 2),
            Key("context", provider: "local-b", model: "embed-a", dimensions: 2),
            Key("context", provider: "local-a", model: "embed-b", dimensions: 2),
            Key("context", provider: "local-a", model: "embed-a", dimensions: 3),
            Key("context", provider: "local-a", model: "embed-a", dimensions: 2, encoding: "base64")
        ];

        IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> loaded = persistence.LoadEmbeddings(requested);

        StoredEmbedding embedding = Assert.Single(loaded).Value;
        Assert.Equal(storedKey, embedding.Key);
        Assert.Equal(ExpectedNormalizedVector, embedding.Vector);
    }

    [Fact]
    public async Task SaveAndLoadCandidateVerdictPreservesReviewFingerprintAndUnicodeText()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        PersistedCandidateFingerprint candidate = Candidate("candidate-review");
        AnalysisVerdict verdict = new AnalysisVerdict(
            candidate.CandidateId,
            AnalysisVerdictLabel.DistinctSenses,
            0.91,
            "Gameplay loop and Codex loop are distinct — reviewer’s rationale.",
            ["claim-left", "claim-right"],
            "docs/glossary.md#loop",
            [new ProposedGlossarySense("loop", "An autonomous Codex cycle.", ["component:Agents"], ["churn loop"])]);

        persistence.SaveCandidateFingerprints([candidate]);
        persistence.SaveVerdicts([verdict]);
        PersistedCandidateFingerprint loadedCandidate = Assert.Single(
            persistence.LoadCandidateFingerprints([candidate.CandidateId])).Value;
        AnalysisVerdict loadedVerdict = Assert.Single(persistence.LoadVerdicts([candidate.CandidateId])).Value;

        AssertCandidateEqual(candidate, loadedCandidate);
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
    }

    [Fact]
    public async Task SaveBatchesWithApostrophesNewlinesAndSqlTokensRoundTripWithoutExecutingInput()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        const string hostile = "'close\n); DROP TABLE analysis_claims; --\nnext";
        PersistedClaim claim = Claim(hostile, text: "Operator's first line\nsecond line; SELECT * FROM verdicts;");
        PersistedCandidateFingerprint candidate = Candidate(hostile);
        AnalysisVerdict verdict = Verdict(hostile, rationale: "It isn't a conflict.\nKeep both; -- literally.");

        persistence.SaveClaims([claim]);
        persistence.SaveCandidateFingerprints([candidate]);
        persistence.SaveVerdicts([verdict]);

        Assert.Equal(claim, persistence.LoadClaims([hostile])[hostile]);
        AssertCandidateEqual(candidate, persistence.LoadCandidateFingerprints([hostile])[hostile]);
        Assert.Equal(verdict.Rationale, persistence.LoadVerdicts([hostile])[hostile].Rationale);
        persistence.SaveClaims([Claim("schema-still-present")]);
        Assert.Single(persistence.LoadClaims(["schema-still-present"]));
    }

    [Fact]
    public async Task SaveEmbeddingsWhenAnyVectorIsNotFiniteAndNormalizedRollsBackWholeBatch()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        StoredEmbedding valid = Embedding(Key("valid"));
        StoredEmbedding invalid = new StoredEmbedding(Key("invalid"), [float.NaN, 1f]);

        Assert.Throws<ArgumentException>(() => persistence.SaveEmbeddings([valid, invalid]));

        Assert.Empty(persistence.LoadEmbeddings([valid.Key, invalid.Key]));
    }

    [Fact]
    public async Task SaveVerdictsWhenAnyVerdictIsInvalidRollsBackWholeBatch()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        PersistedCandidateFingerprint first = Candidate("candidate-valid");
        PersistedCandidateFingerprint second = Candidate("candidate-invalid");
        persistence.SaveCandidateFingerprints([first, second]);

        Assert.Throws<ArgumentException>(() => persistence.SaveVerdicts(
        [
            Verdict(first.CandidateId),
            Verdict(second.CandidateId) with { Confidence = double.NaN }
        ]));

        Assert.Empty(persistence.LoadVerdicts([first.CandidateId, second.CandidateId]));
    }

    [Fact]
    public async Task ConstructorWhenMigrationFailsRollsBackSchemaAndVersion()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository(createCache: true);
        string databasePath = DatabasePath(repository.Path);
        await RunSqlite(
            databasePath,
            "CREATE TABLE analysis_claims (broken TEXT);\nPRAGMA user_version = 0;");
        string originalSchema = await QuerySqlite(databasePath, ".schema analysis_claims");

        Assert.Throws<InvalidDataException>(() => DocsPorts.Sqlite(repository.Path));

        Assert.Equal("0", (await QuerySqlite(databasePath, "PRAGMA user_version;")).Trim());
        Assert.Equal(originalSchema, await QuerySqlite(databasePath, ".schema analysis_claims"));
    }

    [Fact]
    public async Task ConstructorWhenDatabaseIsInvalidDoesNotOverwriteIt()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository(createCache: true);
        string databasePath = DatabasePath(repository.Path);
        string original = "not a sqlite database\nwith operator-owned evidence";
        await File.WriteAllTextAsync(databasePath, original);

        Assert.Throws<InvalidDataException>(() => DocsPorts.Sqlite(repository.Path));

        Assert.Equal(original, await File.ReadAllTextAsync(databasePath));
    }

    [Theory]
    [InlineData("analysis_claims", "stored-id", "payload-id")]
    [InlineData("analysis_candidates", "stored-id", "payload-id")]
    [InlineData("analysis_verdicts", "stored-id", "payload-id")]
    public async Task LoadWhenRowKeyDoesNotMatchPayloadIdentityReportsCorruptCache(
        string table,
        string rowId,
        string payloadId)
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        byte[] payload = table switch
        {
            "analysis_claims" => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Claim(payloadId)),
            "analysis_candidates" => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Candidate(payloadId)),
            "analysis_verdicts" => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Verdict(payloadId)),
            _ => throw new InvalidOperationException()
        };
        string keyColumn = table == "analysis_claims" ? "id" : "candidate_id";
        await RunSqlite(
            persistence.DatabasePath,
            $"PRAGMA foreign_keys=OFF; INSERT INTO {table}({keyColumn}, payload) VALUES " +
            $"({Blob(rowId)}, {Blob(payload)});");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => table switch
        {
            "analysis_claims" => persistence.LoadClaims([rowId]),
            "analysis_candidates" => persistence.LoadCandidateFingerprints([rowId]),
            "analysis_verdicts" => persistence.LoadVerdicts([rowId]),
            _ => throw new InvalidOperationException()
        });

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("claim-empty-id")]
    [InlineData("candidate-empty-hashes")]
    [InlineData("verdict-invalid-label")]
    [InlineData("verdict-invalid-confidence")]
    [InlineData("verdict-empty-rationale")]
    public async Task LoadWhenPersistedPayloadViolatesDomainContractReportsCorruptCache(string scenario)
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        (string table, string keyColumn, string rowId, byte[] payload) = scenario switch
        {
            "claim-empty-id" => (
                "analysis_claims",
                "id",
                "claim-row",
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Claim(string.Empty))),
            "candidate-empty-hashes" => (
                "analysis_candidates",
                "candidate_id",
                "candidate-row",
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    Candidate("candidate-row") with { ClaimContentHashes = [] })),
            "verdict-invalid-label" => (
                "analysis_verdicts",
                "candidate_id",
                "verdict-row",
                System.Text.Encoding.UTF8.GetBytes(
                    "{\"CandidateId\":\"verdict-row\",\"Label\":999," +
                    "\"Confidence\":0.9,\"Rationale\":\"reviewed\"}")),
            "verdict-invalid-confidence" => (
                "analysis_verdicts",
                "candidate_id",
                "verdict-row",
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    Verdict("verdict-row") with { Confidence = 1.1 })),
            "verdict-empty-rationale" => (
                "analysis_verdicts",
                "candidate_id",
                "verdict-row",
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    Verdict("verdict-row") with { Rationale = string.Empty })),
            _ => throw new InvalidOperationException()
        };
        await RunSqlite(
            persistence.DatabasePath,
            $"PRAGMA foreign_keys=OFF; INSERT INTO {table}({keyColumn}, payload) VALUES " +
            $"({Blob(rowId)}, {Blob(payload)});");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => table switch
        {
            "analysis_claims" => persistence.LoadClaims([rowId]),
            "analysis_candidates" => persistence.LoadCandidateFingerprints([rowId]),
            "analysis_verdicts" => persistence.LoadVerdicts([rowId]),
            _ => throw new InvalidOperationException()
        });

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveClaimsWhenDatabaseHasConcurrentImmediateTransactionDoesNotMisreportCorruption()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        using Process lockProcess = await StartSqliteLock(persistence.DatabasePath);
        try
        {
            Assert.False(lockProcess.HasExited, "sqlite lock fixture exited before SaveClaims ran.");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => persistence.SaveClaims([Claim("contended-claim")]));

            Assert.False(lockProcess.HasExited, "sqlite lock fixture exited during SaveClaims.");
            Assert.True(
                exception.Message.Contains("lock", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("busy", StringComparison.OrdinalIgnoreCase),
                $"Expected a lock/busy diagnostic, but got: {exception.Message}");
        }
        finally
        {
            await lockProcess.StandardInput.WriteLineAsync("ROLLBACK;");
            lockProcess.StandardInput.Close();
            Assert.True(await WaitForExitWithinAsync(lockProcess, TimeSpan.FromSeconds(5)), "sqlite lock fixture did not exit.");
        }
    }

    [Fact]
    public async Task StartSqliteLockWhenContendedFailsFastWithBailAndReportsError()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);
        using Process primaryLock = await StartSqliteLock(persistence.DatabasePath);
        try
        {
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => StartSqliteLock(persistence.DatabasePath, sqliteTimeoutMilliseconds: 50));

            Assert.True(
                exception.Message.Contains("lock", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("busy", StringComparison.OrdinalIgnoreCase),
                $"Expected a lock/busy diagnostic, but got: {exception.Message}");
        }
        finally
        {
            if (!primaryLock.HasExited)
            {
                try
                {
                    await primaryLock.StandardInput.WriteLineAsync("ROLLBACK;");
                    primaryLock.StandardInput.Close();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    // Child process terminated before or during rollback write.
                }
            }

            if (!primaryLock.HasExited)
            {
                Assert.True(await WaitForExitWithinAsync(primaryLock, TimeSpan.FromSeconds(5)), "sqlite primary lock fixture did not exit.");
            }
        }
    }

    [Fact]
    public async Task StartSqliteLockWhenOutputIsNotAcquiredKillsChildAndFailsFastWithoutHanging()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartSqliteLock(persistence.DatabasePath, acquireAckQuery: "SELECT 'UNEXPECTED';"));

        Assert.Contains("Output: 'UNEXPECTED'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartSqliteLockWhenRollbackCleanupFailsPreservesCleanupDiagnosticInAggregateException()
    {
        await RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence persistence = DocsPorts.Sqlite(repository.Path);

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(
            () => StartSqliteLock(
                persistence.DatabasePath,
                probeTimeoutSeconds: 0,
                beforeProbe: process =>
                {
                    // Closing the child's standard input is what makes sqlite exit, so waiting for
                    // it here pins the state the fixture's cleanup block observes: input closed and
                    // child gone. Without the wait, whether the child had exited by the time cleanup
                    // ran was a race — this test failed on one machine and passed on another.
                    process.StandardInput.Dispose();
                    Assert.True(
                        process.WaitForExit(30_000),
                        "sqlite lock fixture child did not exit after its standard input was closed.");
                    Assert.True(
                        process.HasExited,
                        "sqlite lock fixture child must have exited before the fixture's cleanup block runs.");
                }));

        Assert.IsType<TimeoutException>(exception.InnerExceptions[0]);
        Assert.Contains(exception.InnerExceptions.Skip(1), inner => inner is IOException or InvalidOperationException);
    }

    [Fact]
    public void PublicPersistenceContractDoesNotAcceptCredentialsOrRequestHeaders()
    {
        string[] publicNames = typeof(SqliteAnalysisPersistence)
            .GetMembers()
            .SelectMany(member => member switch
            {
                System.Reflection.MethodBase method =>
                    method.GetParameters().Select(parameter => parameter.Name ?? string.Empty),
                System.Reflection.PropertyInfo property => [property.Name],
                _ => []
            })
            .ToArray();

        Assert.DoesNotContain(publicNames, name =>
            name.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Contains("apiKey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("header", StringComparison.OrdinalIgnoreCase));
    }

    private static TempDirectory SafeRepository(bool createCache = false)
    {
        TempDirectory repository = new TempDirectory();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(stateDirectory, ".gitignore"), "cache/\n");
        if (createCache) Directory.CreateDirectory(Path.Combine(stateDirectory, "cache"));
        return repository;
    }

    private static string DatabasePath(string repositoryRoot) =>
        Path.Combine(repositoryRoot, ".kyber-weave", "cache", "docs-analysis.sqlite3");

    private static string Blob(string value) => Blob(System.Text.Encoding.UTF8.GetBytes(value));

    private static string Blob(byte[] value) => $"X'{Convert.ToHexString(value)}'";

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

    private static AnalysisVerdict Verdict(
        string candidateId,
        string rationale = "The scopes are intentionally compatible.") =>
        new(candidateId, AnalysisVerdictLabel.Benign, 0.95, rationale, ["claim-left", "claim-right"]);

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

    private static EmbeddingCacheKey Key(
        string context,
        string provider = "http://127.0.0.1:1234/v1/embeddings",
        string model = "local-model",
        int? dimensions = 2,
        string encoding = "float") =>
        new(context, provider, model, dimensions, encoding);

    private static StoredEmbedding Embedding(EmbeddingCacheKey key) => new(key, [0.6f, 0.8f]);

    private static Task RequireSqlite() =>
        SqliteTestFixture.RequireSqliteAsync("sqlite3 is unavailable; SQLite adapter parity was not run.");

    private static async Task RequireGit()
    {
        ProcessStartInfo startInfo = ProcessStartInfo("git");
        startInfo.ArgumentList.Add("--version");
        try
        {
            if ((await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(60))).ExitCode != 0)
                throw SkipException.ForSkip("git is unavailable; tracked-file safety was not run.");
        }
        catch (Win32Exception)
        {
            throw SkipException.ForSkip("git is unavailable; tracked-file safety was not run.");
        }
    }

    private static async Task RunGit(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = ProcessStartInfo("git");
        startInfo.WorkingDirectory = workingDirectory;
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        ProcessResult result = await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
    }

    private static async Task<Process> StartSqliteLock(
        string databasePath,
        int acquireTimeoutSeconds = 60,
        int probeTimeoutSeconds = 60,
        int sqliteTimeoutMilliseconds = 60000,
        string acquireAckQuery = "SELECT 'ACQUIRED';",
        Action<Process>? beforeProbe = null)
    {
        ProcessStartInfo startInfo = SqliteStartInfo();
        startInfo.ArgumentList.Add("-batch");
        startInfo.ArgumentList.Add("-bail");
        startInfo.ArgumentList.Add(databasePath);
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start sqlite lock fixture.");

        bool acquired = false;
        try
        {
            await process.StandardInput.WriteLineAsync(
                $".timeout {sqliteTimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            await process.StandardInput.WriteLineAsync("BEGIN IMMEDIATE;");
            await process.StandardInput.WriteLineAsync(acquireAckQuery);
            await process.StandardInput.FlushAsync();

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(acquireTimeoutSeconds));
            string? line;
            try
            {
                line = process.StandardOutput.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException exception)
            {
                throw new TimeoutException("sqlite lock fixture did not acquire an immediate transaction within deadline.", exception);
            }

            if (!string.Equals(line?.Trim(), "ACQUIRED", StringComparison.Ordinal))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await WaitForExitWithinAsync(process, TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or NotSupportedException or InvalidOperationException)
                {
                    // Best effort termination before draining stderr.
                }

                string stderr;
                using (CancellationTokenSource stderrCts = new(TimeSpan.FromSeconds(5)))
                {
                    try
                    {
                        stderr = process.StandardError.ReadToEndAsync(stderrCts.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        stderr = string.Empty;
                    }
                }

                throw new InvalidOperationException(
                    $"sqlite lock fixture did not acquire an immediate transaction. Output: '{line}', Error: '{stderr}'.");
            }

            acquired = true;

            beforeProbe?.Invoke(process);

            DateTime deadline = DateTime.UtcNow.AddSeconds(probeTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                ProcessResult result = await RunSqliteAllowFailure(databasePath, ".timeout 50\nBEGIN IMMEDIATE;");
                if (result.ExitCode != 0
                    && (result.StandardError.Contains("locked", StringComparison.OrdinalIgnoreCase)
                        || result.StandardError.Contains("busy", StringComparison.OrdinalIgnoreCase)))
                {
                    return process;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("sqlite lock fixture verified acquire, but concurrent transaction probe did not detect lock.");
        }
        catch (Exception primaryException)
        {
            List<Exception> cleanupFailures = [];
            if (acquired)
            {
                try
                {
                    // The fixture owes the child a ROLLBACK, and it can issue that write only while
                    // the child's standard input is open. Closed input leaves the rollback unissued
                    // and the immediate transaction in an unknown state, so that is a cleanup failure
                    // the caller must see.
                    //
                    // Carve-out: a child that exited with its input still open (or whose broken pipe
                    // was undetected because Flush on an empty buffer sends no bytes) took the
                    // transaction with it: process termination releases all SQLite file locks, so
                    // there is nothing to roll back, no lock leak, and nothing to report.
                    if (!IsStandardInputOpen(process))
                    {
                        cleanupFailures.Add(new IOException(
                            "sqlite lock fixture could not issue ROLLBACK; the child's standard input was already closed, "
                            + "so whether the immediate transaction was released is unknown."));
                    }
                    else if (!process.HasExited)
                    {
                        await process.StandardInput.WriteLineAsync("ROLLBACK;");
                        process.StandardInput.Close();
                        if (!process.HasExited)
                        {
                            await WaitForExitWithinAsync(process, TimeSpan.FromSeconds(5));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    cleanupFailures.Add(ex);
                }
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await WaitForExitWithinAsync(process, TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException or InvalidOperationException)
            {
                if (!process.HasExited)
                {
                    cleanupFailures.Add(ex);
                }
            }

            if (!process.HasExited)
            {
                cleanupFailures.Add(new InvalidOperationException("sqlite lock fixture could not terminate child process."));
            }

            process.Dispose();

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "sqlite lock fixture encountered a failure, and cleanup also failed.",
                    [primaryException, .. cleanupFailures]);
            }

            throw;
        }
    }

    /// <summary>
    ///     Whether the child's standard input is still open, which decides whether the fixture can
    ///     still issue the <c>ROLLBACK;</c> it owes the child.
    /// </summary>
    /// <remarks>
    ///     <see cref="StreamWriter"/> exposes no disposal state, and the pipe behind
    ///     <see cref="StreamWriter.BaseStream"/> keeps reporting <c>CanWrite</c> after the writer is
    ///     disposed, so neither answers the question. A flush is what does: it returns when the
    ///     writer is open and throws <see cref="ObjectDisposedException"/> once a caller has disposed
    ///     it. The buffer is empty here because the acquire path flushed every write, so the probe
    ///     sends the child nothing.
    ///     <para>
    ///         Because the buffer is empty, this probe does not detect a broken pipe when the child
    ///         exits without explicit input disposal. That exited-child carve-out is handled by the
    ///         subsequent <see cref="Process.HasExited"/> check (the exited child took its locks with it,
    ///         leaving nothing to roll back).
    ///     </para>
    /// </remarks>
    private static bool IsStandardInputOpen(Process process)
    {
        try
        {
            process.StandardInput.Flush();
            return true;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            return false;
        }
    }

    [Fact]
    public void IsStandardInputOpen_WhenDisposed_ReturnsFalse()
    {
        ProcessStartInfo startInfo = new("git", ["version"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Dispose();

        Assert.False(IsStandardInputOpen(process));
        Assert.True(process.WaitForExit(5000), "Process did not exit in time.");
    }

    [Fact]
    public void IsStandardInputOpen_WhenPipeBroken_ReturnsFalse()
    {
        ProcessStartInfo startInfo = new("git", ["version"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using Process process = Process.Start(startInfo)!;
        _ = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(5000), "Process did not exit in time.");

        // A single buffered write can succeed before the OS reports the child's closed read end (it
        // lands in the pipe buffer), so keep writing and flushing within a bounded window until the
        // broken pipe surfaces as an IOException.
        process.StandardInput.AutoFlush = false;
        Stopwatch window = Stopwatch.StartNew();
        bool brokenPipeSeen = false;
        while (!brokenPipeSeen && window.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                process.StandardInput.Write(' ');
                process.StandardInput.Flush();
            }
            catch (IOException)
            {
                brokenPipeSeen = true;
            }

            if (!brokenPipeSeen)
            {
                Thread.Sleep(10);
            }
        }

        Assert.True(brokenPipeSeen, "Writing to the exited child's standard input never raised IOException within 5 seconds.");

        // The failed flush drained the writer's buffer, and flushing an empty buffer sends nothing, so
        // buffer one more character for the helper's own flush to push across the broken pipe.
        process.StandardInput.Write(' ');
        Assert.False(IsStandardInputOpen(process));
    }

    private static async Task<string> QuerySqlite(string databasePath, string input)
    {
        ProcessResult result = await RunSqlite(databasePath, input);
        return result.StandardOutput;
    }

    private static async Task<ProcessResult> RunSqlite(string databasePath, string input)
    {
        ProcessResult result = await RunSqliteAllowFailure(databasePath, input);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.StandardError);
        return result;
    }

    private static async Task<ProcessResult> RunSqliteAllowFailure(string databasePath, string input)
    {
        ProcessStartInfo startInfo = SqliteStartInfo();
        startInfo.ArgumentList.Add("-batch");
        startInfo.ArgumentList.Add("-bail");
        startInfo.ArgumentList.Add(databasePath);
        return await ProcessRunner.RunAsync(startInfo, input, TimeSpan.FromSeconds(60));
    }

    private static async Task<bool> WaitForExitWithinAsync(Process process, TimeSpan timeout)
    {
        Task wait = process.WaitForExitAsync();
        Task completed = await Task.WhenAny(wait, Task.Delay(timeout));
        if (completed != wait)
            return false;

        await wait;
        return true;
    }

    private static ProcessStartInfo SqliteStartInfo() => ProcessStartInfo("sqlite3");

    private static ProcessStartInfo ProcessStartInfo(string fileName) =>
        new(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
}
