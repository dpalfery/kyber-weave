using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

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
    public void IsSafeRequiresExactCacheDirectoryIgnoreEntry(string ignoreContents, bool expected)
    {
        using TempDirectory repository = new TempDirectory();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(stateDirectory, ".gitignore"), ignoreContents);

        bool actual = AnalysisCacheSafety.IsSafe(repository.Path);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("cache/\n!cache/\n")]
    [InlineData("cache/\n!cache/docs-analysis.sqlite3\n")]
    [InlineData("cache/\n!**/cache/**\n")]
    public void IsSafeWhenLaterRuleNegatesCacheProtectionReturnsFalse(string ignoreContents)
    {
        using TempDirectory repository = new TempDirectory();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(stateDirectory, ".gitignore"), ignoreContents);

        Assert.False(AnalysisCacheSafety.IsSafe(repository.Path));
    }

    [Fact]
    public void IsSafeWhenDatabaseIsAlreadyTrackedReturnsFalse()
    {
        RequireGit();
        using TempDirectory repository = SafeRepository(createCache: true);
        string databasePath = DatabasePath(repository.Path);
        File.WriteAllText(databasePath, "tracked cache placeholder");
        RunGit(repository.Path, "init");
        RunGit(repository.Path, "add", "-f", ".kyber-weave/cache/docs-analysis.sqlite3");

        Assert.False(AnalysisCacheSafety.IsSafe(repository.Path));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("cache")]
    [InlineData("database")]
    public void ConstructorWhenPersistencePathContainsSymbolicLinkRejectsWithoutChangingExternalTarget(
        string linkedSegment)
    {
        RequireSqlite();
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Symbolic-link creation requires platform-specific privileges on Windows.");

        using TempDirectory repository = new TempDirectory();
        using TempDirectory external = new TempDirectory();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        string cacheDirectory = Path.Combine(stateDirectory, "cache");
        string databasePath = Path.Combine(cacheDirectory, "docs-analysis.sqlite3");
        string sentinelPath = Path.Combine(external.Path, "sentinel.txt");
        File.WriteAllText(sentinelPath, "external state must remain unchanged");

        switch (linkedSegment)
        {
            case "state":
                Directory.CreateSymbolicLink(stateDirectory, external.Path);
                File.WriteAllText(Path.Combine(external.Path, ".gitignore"), "cache/\n");
                break;
            case "cache":
                Directory.CreateDirectory(stateDirectory);
                File.WriteAllText(Path.Combine(stateDirectory, ".gitignore"), "cache/\n");
                Directory.CreateSymbolicLink(cacheDirectory, external.Path);
                break;
            case "database":
                Directory.CreateDirectory(cacheDirectory);
                File.WriteAllText(Path.Combine(stateDirectory, ".gitignore"), "cache/\n");
                File.CreateSymbolicLink(databasePath, sentinelPath);
                break;
        }

        Assert.Throws<InvalidOperationException>(() => new SqliteAnalysisPersistence(repository.Path));
        Assert.Equal("external state must remain unchanged", File.ReadAllText(sentinelPath));
        Assert.False(File.Exists(Path.Combine(external.Path, "docs-analysis.sqlite3")));
    }

    [Fact]
    public void ConstructorWhenCacheIsNotSafelyIgnoredDisablesReadsAndWritesWithoutCreatingState()
    {
        using TempDirectory repository = new TempDirectory();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        string cacheDirectory = Path.Combine(stateDirectory, "cache");

        IAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);

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
    public void ConstructorWhenCacheIsSafeInitializesVersionedSchemaIdempotently()
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence first = new SqliteAnalysisPersistence(repository.Path);
        PersistedClaim claim = Claim("claim-idempotent");
        first.SaveClaims([claim]);

        SqliteAnalysisPersistence second = new SqliteAnalysisPersistence(repository.Path);

        Assert.True(first.IsAvailable);
        Assert.True(second.IsAvailable);
        Assert.Equal(
            SqliteAnalysisPersistence.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            QuerySqlite(second.DatabasePath, "PRAGMA user_version;").Trim());
        Assert.Equal(claim, Assert.Single(second.LoadClaims([claim.Id])).Value);
    }

    [Fact]
    public void SaveAndLoadClaimsPreservesDuplicateOccurrencesAndLineAddressableText()
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);
        PersistedClaim first = Claim("claim-left", filePath: "docs/left.md", startLine: 11);
        PersistedClaim second = Claim("claim-right", filePath: "docs/right.md", startLine: 29);

        persistence.SaveClaims([first, second]);
        IReadOnlyDictionary<string, PersistedClaim> loaded = persistence.LoadClaims([first.Id, second.Id]);

        Assert.Equal(first, loaded[first.Id]);
        Assert.Equal(second, loaded[second.Id]);
        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void SaveAndLoadEmbeddingsUsesEveryContentProviderModelAndShapeKeyField()
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);
        EmbeddingCacheKey storedKey = Key("context", provider: "local-a", model: "embed-a", dimensions: 2);
        persistence.SaveEmbeddings([Embedding(storedKey)]);
        EmbeddingCacheKey[] requested = new[]
        {
            storedKey,
            Key("other-context", provider: "local-a", model: "embed-a", dimensions: 2),
            Key("context", provider: "local-b", model: "embed-a", dimensions: 2),
            Key("context", provider: "local-a", model: "embed-b", dimensions: 2),
            Key("context", provider: "local-a", model: "embed-a", dimensions: 3),
            Key("context", provider: "local-a", model: "embed-a", dimensions: 2, encoding: "base64")
        };

        IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> loaded = persistence.LoadEmbeddings(requested);

        StoredEmbedding embedding = Assert.Single(loaded).Value;
        Assert.Equal(storedKey, embedding.Key);
        Assert.Equal(ExpectedNormalizedVector, embedding.Vector);
    }

    [Fact]
    public void SaveAndLoadCandidateVerdictPreservesReviewFingerprintAndUnicodeText()
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);
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
    public void SaveBatchesWithApostrophesNewlinesAndSqlTokensRoundTripWithoutExecutingInput()
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);
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
    public void SaveEmbeddingsWhenAnyVectorIsNotFiniteAndNormalizedRollsBackWholeBatch()
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);
        StoredEmbedding valid = Embedding(Key("valid"));
        StoredEmbedding invalid = new StoredEmbedding(Key("invalid"), [float.NaN, 1f]);

        Assert.Throws<ArgumentException>(() => persistence.SaveEmbeddings([valid, invalid]));

        Assert.Empty(persistence.LoadEmbeddings([valid.Key, invalid.Key]));
    }

    [Fact]
    public void SaveVerdictsWhenAnyVerdictIsInvalidRollsBackWholeBatch()
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository();
        IAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);
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
    public void ConstructorWhenMigrationFailsRollsBackSchemaAndVersion()
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository(createCache: true);
        string databasePath = DatabasePath(repository.Path);
        RunSqlite(
            databasePath,
            "CREATE TABLE analysis_claims (broken TEXT);\nPRAGMA user_version = 0;");
        string originalSchema = QuerySqlite(databasePath, ".schema analysis_claims");

        Assert.Throws<InvalidDataException>(() => new SqliteAnalysisPersistence(repository.Path));

        Assert.Equal("0", QuerySqlite(databasePath, "PRAGMA user_version;").Trim());
        Assert.Equal(originalSchema, QuerySqlite(databasePath, ".schema analysis_claims"));
    }

    [Fact]
    public void ConstructorWhenDatabaseIsInvalidDoesNotOverwriteIt()
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository(createCache: true);
        string databasePath = DatabasePath(repository.Path);
        string original = "not a sqlite database\nwith operator-owned evidence";
        File.WriteAllText(databasePath, original);

        Assert.Throws<InvalidDataException>(() => new SqliteAnalysisPersistence(repository.Path));

        Assert.Equal(original, File.ReadAllText(databasePath));
    }

    [Theory]
    [InlineData("analysis_claims", "stored-id", "payload-id")]
    [InlineData("analysis_candidates", "stored-id", "payload-id")]
    [InlineData("analysis_verdicts", "stored-id", "payload-id")]
    public void LoadWhenRowKeyDoesNotMatchPayloadIdentityReportsCorruptCache(
        string table,
        string rowId,
        string payloadId)
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);
        byte[] payload = table switch
        {
            "analysis_claims" => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Claim(payloadId)),
            "analysis_candidates" => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Candidate(payloadId)),
            "analysis_verdicts" => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Verdict(payloadId)),
            _ => throw new InvalidOperationException()
        };
        string keyColumn = table == "analysis_claims" ? "id" : "candidate_id";
        RunSqlite(
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
    public void LoadWhenPersistedPayloadViolatesDomainContractReportsCorruptCache(string scenario)
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);
        (string? table, string? keyColumn, string? rowId, byte[]? payload) = scenario switch
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
        RunSqlite(
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
    public void SaveClaimsWhenDatabaseHasConcurrentImmediateTransactionDoesNotMisreportCorruption()
    {
        RequireSqlite();
        using TempDirectory repository = SafeRepository();
        SqliteAnalysisPersistence persistence = new SqliteAnalysisPersistence(repository.Path);
        using Process lockProcess = StartSqliteLock(persistence.DatabasePath);
        try
        {
            Exception? exception = Record.Exception(() => persistence.SaveClaims([Claim("contended-claim")]));

            if (exception is null)
            {
                Assert.Single(persistence.LoadClaims(["contended-claim"]));
            }
            else
            {
                Assert.IsNotType<InvalidDataException>(exception);
                Assert.Contains("lock", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            lockProcess.StandardInput.WriteLine("ROLLBACK;");
            lockProcess.StandardInput.Close();
            Assert.True(lockProcess.WaitForExit(5_000), "sqlite lock fixture did not exit.");
        }
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

    private static void RequireSqlite()
    {
        ProcessStartInfo startInfo = SqliteStartInfo();
        startInfo.ArgumentList.Add("--version");
        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);
            if (result.ExitCode != 0)
                throw SkipException.ForSkip("sqlite3 is unavailable; SQLite adapter parity was not run.");
        }
        catch (Win32Exception)
        {
            throw SkipException.ForSkip("sqlite3 is unavailable; SQLite adapter parity was not run.");
        }
    }

    private static void RequireGit()
    {
        ProcessStartInfo startInfo = ProcessStartInfo("git");
        startInfo.ArgumentList.Add("--version");
        try
        {
            if (ProcessRunner.Run(startInfo, string.Empty).ExitCode != 0)
                throw SkipException.ForSkip("git is unavailable; tracked-file safety was not run.");
        }
        catch (Win32Exception)
        {
            throw SkipException.ForSkip("git is unavailable; tracked-file safety was not run.");
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = ProcessStartInfo("git");
        startInfo.WorkingDirectory = workingDirectory;
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
    }

    private static Process StartSqliteLock(string databasePath)
    {
        ProcessStartInfo startInfo = SqliteStartInfo();
        startInfo.ArgumentList.Add(databasePath);
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start sqlite lock fixture.");
        process.StandardInput.WriteLine("BEGIN IMMEDIATE;");
        process.StandardInput.Flush();

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            ProcessResult result = RunSqliteAllowFailure(databasePath, ".timeout 1\nBEGIN IMMEDIATE;");
            if (result.ExitCode != 0
                && result.StandardError.Contains("locked", StringComparison.OrdinalIgnoreCase))
            {
                return process;
            }
        }

        process.StandardInput.WriteLine("ROLLBACK;");
        process.StandardInput.Close();
        process.WaitForExit();
        throw new TimeoutException("sqlite lock fixture did not acquire an immediate transaction.");
    }

    private static string QuerySqlite(string databasePath, string input)
    {
        ProcessResult result = RunSqlite(databasePath, input);
        return result.StandardOutput;
    }

    private static ProcessResult RunSqlite(string databasePath, string input)
    {
        ProcessResult result = RunSqliteAllowFailure(databasePath, input);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.StandardError);
        return result;
    }

    private static ProcessResult RunSqliteAllowFailure(string databasePath, string input)
    {
        ProcessStartInfo startInfo = SqliteStartInfo();
        startInfo.ArgumentList.Add("-batch");
        startInfo.ArgumentList.Add("-bail");
        startInfo.ArgumentList.Add(databasePath);
        return ProcessRunner.Run(startInfo, input);
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
