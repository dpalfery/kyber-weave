using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Docs.Analysis.Persistence;

/// <summary>
/// Stores local analysis evidence through the system <c>sqlite3</c> executable.
/// </summary>
/// <remarks>
/// The CLI boundary avoids adding a native SQLite package with its transitive advisory.
/// Every caller-controlled value is encoded as a SQLite BLOB literal, so prose cannot
/// become SQL even when it contains quotes, newlines, or SQL-looking text.
/// </remarks>
public sealed class SqliteAnalysisPersistence : IAnalysisPersistence
{
    public const int SchemaVersion = 1;

    private const double NormalizedVectorTolerance = 0.0001;
    private const int BusyTimeoutMilliseconds = 250;
    private const int BusyAttempts = 3;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);
    private readonly string _repositoryRoot;

    public SqliteAnalysisPersistence(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        string fullRoot = Path.GetFullPath(repositoryRoot);
        _repositoryRoot = fullRoot;
        DatabasePath = Path.Combine(
            fullRoot,
            ".kyber-weave",
            "cache",
            "docs-analysis.sqlite3");

        EnsureSafePersistencePath(fullRoot, DatabasePath);
        if (!AnalysisCacheSafety.IsSafe(fullRoot) || !CanStartSqlite()) return;

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        EnsureSafePersistencePath(fullRoot, DatabasePath);
        InitializeSchema();
        IsAvailable = true;
    }

    public bool IsAvailable { get; }

    public string DatabasePath { get; }

    public IReadOnlyDictionary<string, PersistedClaim> LoadClaims(
        IReadOnlyCollection<string> claimIds)
    {
        ArgumentNullException.ThrowIfNull(claimIds);
        if (!IsAvailable || claimIds.Count == 0)
            return new Dictionary<string, PersistedClaim>(StringComparer.Ordinal);

        HashSet<string> requested = new HashSet<string>(claimIds, StringComparer.Ordinal);
        return ReadPayloadRows<PersistedClaim>("analysis_claims", "id")
            .Where(pair => requested.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public void SaveClaims(IReadOnlyCollection<PersistedClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        EnsureAvailable();
        foreach (PersistedClaim claim in claims) Validate(claim);

        ExecuteWriteTransaction(claims.Select(ClaimUpsert));
    }

    public IReadOnlyDictionary<string, PersistedCandidateFingerprint> LoadCandidateFingerprints(
        IReadOnlyCollection<string> candidateIds)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (!IsAvailable || candidateIds.Count == 0)
            return new Dictionary<string, PersistedCandidateFingerprint>(StringComparer.Ordinal);

        HashSet<string> requested = new HashSet<string>(candidateIds, StringComparer.Ordinal);
        return ReadPayloadRows<PersistedCandidateFingerprint>("analysis_candidates", "candidate_id")
            .Where(pair => requested.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public void SaveCandidateFingerprints(
        IReadOnlyCollection<PersistedCandidateFingerprint> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        EnsureAvailable();
        foreach (PersistedCandidateFingerprint candidate in candidates) Validate(candidate);

        ExecuteWriteTransaction(candidates.Select(CandidateUpsert));
    }

    public IReadOnlyDictionary<string, AnalysisVerdict> LoadVerdicts(
        IReadOnlyCollection<string> candidateIds)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (!IsAvailable || candidateIds.Count == 0)
            return new Dictionary<string, AnalysisVerdict>(StringComparer.Ordinal);

        HashSet<string> requested = new HashSet<string>(candidateIds, StringComparer.Ordinal);
        return ReadPayloadRows<AnalysisVerdict>("analysis_verdicts", "candidate_id")
            .Where(pair => requested.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public void SaveVerdicts(IReadOnlyCollection<AnalysisVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        EnsureAvailable();
        foreach (AnalysisVerdict verdict in verdicts) Validate(verdict);

        ExecuteWriteTransaction(verdicts.Select(VerdictUpsert));
    }

    /// <inheritdoc />
    public void SaveReviewImport(
        IReadOnlyCollection<PersistedClaim> claims,
        IReadOnlyCollection<PersistedCandidateFingerprint> candidates,
        IReadOnlyCollection<AnalysisVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(verdicts);
        EnsureAvailable();
        foreach (PersistedClaim claim in claims) Validate(claim);
        foreach (PersistedCandidateFingerprint candidate in candidates) Validate(candidate);
        foreach (AnalysisVerdict verdict in verdicts) Validate(verdict);

        HashSet<string> candidateIds = candidates
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.Ordinal);
        if (verdicts.Any(verdict => !candidateIds.Contains(verdict.CandidateId)))
            throw new ArgumentException(
                "Every imported verdict must have a current candidate fingerprint.",
                nameof(verdicts));

        ExecuteWriteTransaction(
            claims.Select(ClaimUpsert)
                .Concat(candidates.Select(CandidateUpsert))
                .Concat(verdicts.Select(VerdictUpsert)));
    }

    public IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> LoadEmbeddings(
        IReadOnlyCollection<EmbeddingCacheKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!IsAvailable || keys.Count == 0)
            return new Dictionary<EmbeddingCacheKey, StoredEmbedding>();

        HashSet<EmbeddingCacheKey> requested = new HashSet<EmbeddingCacheKey>(keys);
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT hex(contextual_hash), hex(provider_fingerprint), hex(model), " +
            "dimensions, hex(encoding), hex(vector) FROM analysis_embeddings;");
        Dictionary<EmbeddingCacheKey, StoredEmbedding> loaded = new Dictionary<EmbeddingCacheKey, StoredEmbedding>();
        foreach (string line in Lines(output))
        {
            try
            {
                string[] fields = line.Split('\t');
                if (fields.Length != 6
                    || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int storedDimensions))
                {
                    throw new InvalidDataException("An embedding row has an invalid shape.");
                }

                EmbeddingCacheKey key = new EmbeddingCacheKey(
                    Text(fields[0]),
                    Text(fields[1]),
                    Text(fields[2]),
                    storedDimensions < 0 ? null : storedDimensions,
                    Text(fields[4]));
                if (!requested.Contains(key)) continue;

                IReadOnlyList<float> vector = DecodeVector(fields[5]);
                Validate(new StoredEmbedding(key, vector));
                loaded[key] = new StoredEmbedding(key, vector);
            }
            catch (Exception exception) when (
                exception is FormatException or ArgumentException or InvalidDataException)
            {
                throw CorruptCache("An embedding row is invalid.", exception);
            }
        }

        return loaded;
    }

    public void SaveEmbeddings(IReadOnlyCollection<StoredEmbedding> embeddings)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        EnsureAvailable();
        foreach (StoredEmbedding embedding in embeddings) Validate(embedding);

        ExecuteWriteTransaction(embeddings.Select(embedding =>
        {
            int dimensions = embedding.Key.Dimensions ?? -1;
            return "INSERT INTO analysis_embeddings(" +
                   "contextual_hash, provider_fingerprint, model, dimensions, encoding, vector) VALUES (" +
                   $"{Blob(embedding.Key.ContextualHash)}, " +
                   $"{Blob(embedding.Key.ProviderFingerprint)}, " +
                   $"{Blob(embedding.Key.Model)}, " +
                   $"{dimensions.ToString(CultureInfo.InvariantCulture)}, " +
                   $"{Blob(embedding.Key.Encoding)}, " +
                   $"{Blob(EncodeVector(embedding.Vector))}) " +
                   "ON CONFLICT(contextual_hash, provider_fingerprint, model, dimensions, encoding) " +
                   "DO UPDATE SET vector = excluded.vector;";
        }));
    }

    private void InitializeSchema()
    {
        string versionText = ExecuteSqlite("PRAGMA user_version;").Trim();
        if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out int version))
            throw CorruptCache("The analysis cache schema version is not an integer.");
        if (version > SchemaVersion)
            throw CorruptCache(
                $"The analysis cache uses schema version {version}, newer than supported version {SchemaVersion}.");

        if (version == 0)
        {
            ExecuteSqlite(
                "PRAGMA foreign_keys = ON;\n" +
                "BEGIN IMMEDIATE;\n" +
                "CREATE TABLE IF NOT EXISTS analysis_claims (" +
                "id BLOB PRIMARY KEY NOT NULL, payload BLOB NOT NULL);\n" +
                "CREATE TABLE IF NOT EXISTS analysis_candidates (" +
                "candidate_id BLOB PRIMARY KEY NOT NULL, payload BLOB NOT NULL);\n" +
                "CREATE TABLE IF NOT EXISTS analysis_verdicts (" +
                "candidate_id BLOB PRIMARY KEY NOT NULL, payload BLOB NOT NULL, " +
                "FOREIGN KEY(candidate_id) REFERENCES analysis_candidates(candidate_id) ON DELETE CASCADE);\n" +
                "CREATE TABLE IF NOT EXISTS analysis_embeddings (" +
                "contextual_hash BLOB NOT NULL, provider_fingerprint BLOB NOT NULL, " +
                "model BLOB NOT NULL, dimensions INTEGER NOT NULL, encoding BLOB NOT NULL, " +
                "vector BLOB NOT NULL, PRIMARY KEY(" +
                "contextual_hash, provider_fingerprint, model, dimensions, encoding));\n" +
                SchemaValidationSql() +
                $"PRAGMA user_version = {SchemaVersion.ToString(CultureInfo.InvariantCulture)};\n" +
                "COMMIT;");
            return;
        }

        ExecuteSqlite("PRAGMA foreign_keys = ON;\n" + SchemaValidationSql());
    }

    private static string SchemaValidationSql() =>
        "SELECT id, payload FROM analysis_claims LIMIT 0;\n" +
        "SELECT candidate_id, payload FROM analysis_candidates LIMIT 0;\n" +
        "SELECT candidate_id, payload FROM analysis_verdicts LIMIT 0;\n" +
        "SELECT contextual_hash, provider_fingerprint, model, dimensions, encoding, vector " +
        "FROM analysis_embeddings LIMIT 0;\n";

    private IReadOnlyDictionary<string, T> ReadPayloadRows<T>(string table, string keyColumn)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            $"SELECT hex({keyColumn}), hex(payload) FROM {table};");
        Dictionary<string, T> loaded = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (string line in Lines(output))
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 2) throw CorruptCache($"Table '{table}' contains an invalid row.");

            try
            {
                string key = Text(fields[0]);
                T value = JsonSerializer.Deserialize<T>(Convert.FromHexString(fields[1]), SerializerOptions)
                    ?? throw CorruptCache($"Table '{table}' contains an empty payload.");
                ValidateLoadedPayload(table, key, value);
                loaded[key] = value;
            }
            catch (Exception exception) when (
                exception is FormatException or JsonException or NotSupportedException or ArgumentException)
            {
                throw CorruptCache($"Table '{table}' contains an invalid payload.", exception);
            }
        }

        return loaded;
    }

    private void ExecuteWriteTransaction(IEnumerable<string> statements)
    {
        StringBuilder script = new StringBuilder("PRAGMA foreign_keys = ON;\nBEGIN IMMEDIATE;\n");
        foreach (string statement in statements) script.AppendLine(statement);
        script.AppendLine("COMMIT;");
        ExecuteSqlite(script.ToString());
    }

    private string ExecuteSqlite(string input)
    {
        EnsureSafePersistencePath(_repositoryRoot, DatabasePath);
        string boundedInput = $".timeout {BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}\n{input}";
        for (int attempt = 1; attempt <= BusyAttempts; attempt++)
        {
            ProcessStartInfo startInfo = CreateStartInfo();
            startInfo.ArgumentList.Add("-batch");
            startInfo.ArgumentList.Add("-bail");
            startInfo.ArgumentList.Add(DatabasePath);

            ProcessResult result;
            try
            {
                result = ProcessRunner.Run(startInfo, boundedInput);
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException("The 'sqlite3' executable is unavailable.", exception);
            }

            if (result.ExitCode == 0) return result.StandardOutput;

            string reason = result.StandardError.Trim();
            if (IsBusy(reason))
            {
                if (attempt < BusyAttempts) continue;
                throw new InvalidOperationException(
                    $"The documentation analysis cache remained locked after {BusyAttempts} bounded attempts. " +
                    FailureDetail(result, reason));
            }

            if (IsOperationalFailure(reason))
            {
                throw new InvalidOperationException(
                    "The documentation analysis cache could not be accessed. " + FailureDetail(result, reason));
            }

            throw CorruptCache(FailureDetail(result, reason));
        }

        throw new InvalidOperationException("The documentation analysis cache operation did not complete.");
    }

    private static bool CanStartSqlite()
    {
        ProcessStartInfo startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("--version");
        try
        {
            return ProcessRunner.Run(startInfo, string.Empty).ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo() =>
        new("sqlite3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string JsonBlob<T>(T value) => Blob(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions));

    private static string ClaimUpsert(PersistedClaim claim) =>
        $"INSERT INTO analysis_claims(id, payload) VALUES ({Blob(claim.Id)}, {JsonBlob(claim)}) " +
        "ON CONFLICT(id) DO UPDATE SET payload = excluded.payload;";

    private static string CandidateUpsert(PersistedCandidateFingerprint candidate) =>
        "INSERT INTO analysis_candidates(candidate_id, payload) VALUES " +
        $"({Blob(candidate.CandidateId)}, {JsonBlob(candidate)}) " +
        "ON CONFLICT(candidate_id) DO UPDATE SET payload = excluded.payload;";

    private static string VerdictUpsert(AnalysisVerdict verdict) =>
        "INSERT INTO analysis_verdicts(candidate_id, payload) VALUES " +
        $"({Blob(verdict.CandidateId)}, {JsonBlob(verdict)}) " +
        "ON CONFLICT(candidate_id) DO UPDATE SET payload = excluded.payload;";

    private static string Blob(string value) => Blob(Encoding.UTF8.GetBytes(value));

    private static string Blob(byte[] value) => $"X'{Convert.ToHexString(value)}'";

    private static string Text(string hex) => Encoding.UTF8.GetString(Convert.FromHexString(hex));

    private static byte[] EncodeVector(IReadOnlyList<float> vector)
    {
        byte[] bytes = new byte[checked(vector.Count * sizeof(float))];
        for (int index = 0; index < vector.Count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), vector[index]);
        return bytes;
    }

    private static IReadOnlyList<float> DecodeVector(string hex)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException exception)
        {
            throw CorruptCache("An embedding vector is not valid hexadecimal.", exception);
        }

        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
            throw CorruptCache("An embedding vector has an invalid byte length.");

        float[] vector = new float[bytes.Length / sizeof(float)];
        for (int index = 0; index < vector.Length; index++)
            vector[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * sizeof(float)));
        return vector;
    }

    private static void Validate(PersistedClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        Required(claim.Id, nameof(claim.Id));
        Required(claim.ContentHash, nameof(claim.ContentHash));
        Required(claim.ContextualHash, nameof(claim.ContextualHash));
        Required(claim.DocumentIdentity, nameof(claim.DocumentIdentity));
        Required(claim.FilePath, nameof(claim.FilePath));
        ArgumentOutOfRangeException.ThrowIfLessThan(claim.StartLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(claim.EndLine, claim.StartLine);
        Required(claim.Text, nameof(claim.Text));
    }

    private static void Validate(PersistedCandidateFingerprint candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Required(candidate.CandidateId, nameof(candidate.CandidateId));
        if (!Enum.IsDefined(candidate.Kind))
            throw new ArgumentException("The candidate kind is invalid.", nameof(candidate));
        Required(candidate.CandidateSetHash, nameof(candidate.CandidateSetHash));
        Required(candidate.AnalyzerVersion, nameof(candidate.AnalyzerVersion));
        Required(candidate.RubricVersion, nameof(candidate.RubricVersion));
        ArgumentNullException.ThrowIfNull(candidate.ClaimContentHashes);
        if (candidate.ClaimContentHashes.Count == 0
            || candidate.ClaimContentHashes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "At least one non-empty claim content hash is required.",
                nameof(candidate));
        }
    }

    private static void Validate(AnalysisVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        Required(verdict.CandidateId, nameof(verdict.CandidateId));
        if (!Enum.IsDefined(verdict.Label))
            throw new ArgumentException("The verdict label is invalid.", nameof(verdict));
        if (!double.IsFinite(verdict.Confidence) || verdict.Confidence is < 0 or > 1)
            throw new ArgumentException("Verdict confidence must be between zero and one.", nameof(verdict));
        Required(verdict.Rationale, nameof(verdict.Rationale));
        if (verdict.EvidenceIds?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("Evidence ids cannot be empty.", nameof(verdict));
        if (verdict.RecommendedCanonicalLocation is not null
            && string.IsNullOrWhiteSpace(verdict.RecommendedCanonicalLocation))
        {
            throw new ArgumentException(
                "The recommended canonical location cannot be empty.",
                nameof(verdict));
        }
        if (verdict.ProposedGlossarySenses is not null)
        {
            foreach (ProposedGlossarySense sense in verdict.ProposedGlossarySenses)
                Validate(sense);
        }
    }

    private static void Validate(ProposedGlossarySense sense)
    {
        ArgumentNullException.ThrowIfNull(sense);
        Required(sense.Term, nameof(sense.Term));
        Required(sense.Definition, nameof(sense.Definition));
        ArgumentNullException.ThrowIfNull(sense.Scopes);
        if (sense.Scopes.Count == 0 || sense.Scopes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Glossary sense scopes cannot be empty.", nameof(sense));
        ArgumentNullException.ThrowIfNull(sense.Aliases);
        if (sense.Aliases.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Glossary sense aliases cannot be empty.", nameof(sense));
    }

    private static void Validate(StoredEmbedding embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(embedding.Key);
        Required(embedding.Key.ContextualHash, nameof(embedding.Key.ContextualHash));
        Required(embedding.Key.ProviderFingerprint, nameof(embedding.Key.ProviderFingerprint));
        Required(embedding.Key.Model, nameof(embedding.Key.Model));
        Required(embedding.Key.Encoding, nameof(embedding.Key.Encoding));
        ArgumentNullException.ThrowIfNull(embedding.Vector);
        if (embedding.Vector.Count == 0
            || embedding.Vector.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException("Embedding vectors must contain only finite values.", nameof(embedding));
        }
        if (embedding.Key.Dimensions is <= 0
            || embedding.Key.Dimensions is int dimensions && dimensions != embedding.Vector.Count)
        {
            throw new ArgumentException("Embedding dimensions must match the vector length.", nameof(embedding));
        }

        double squaredNorm = embedding.Vector.Sum(value => (double)value * value);
        if (Math.Abs(Math.Sqrt(squaredNorm) - 1) > NormalizedVectorTolerance)
            throw new ArgumentException("Embedding vectors must be normalized.", nameof(embedding));
    }

    private static void Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value cannot be empty.", parameterName);
    }

    private static void ValidateLoadedPayload<T>(string table, string rowKey, T value)
    {
        switch (value)
        {
            case PersistedClaim claim:
                Validate(claim);
                RequireMatchingIdentity(table, rowKey, claim.Id);
                break;
            case PersistedCandidateFingerprint candidate:
                Validate(candidate);
                RequireMatchingIdentity(table, rowKey, candidate.CandidateId);
                break;
            case AnalysisVerdict verdict:
                Validate(verdict);
                RequireMatchingIdentity(table, rowKey, verdict.CandidateId);
                break;
            default:
                throw new InvalidOperationException(
                    $"Table '{table}' uses an unsupported persisted payload type '{typeof(T).Name}'.");
        }
    }

    private static void RequireMatchingIdentity(string table, string rowKey, string payloadId)
    {
        if (!StringComparer.Ordinal.Equals(rowKey, payloadId))
        {
            throw new ArgumentException(
                $"Table '{table}' row identity does not match its payload identity.",
                nameof(payloadId));
        }
    }

    private static void EnsureSafePersistencePath(string repositoryRoot, string databasePath)
    {
        EnsureContained(repositoryRoot, databasePath);
        string stateDirectory = Path.Combine(repositoryRoot, ".kyber-weave");
        string cacheDirectory = Path.GetDirectoryName(databasePath)!;
        RejectLinkOrReparsePoint(stateDirectory);
        RejectLinkOrReparsePoint(cacheDirectory);
        RejectLinkOrReparsePoint(databasePath);
    }

    private static void EnsureContained(string repositoryRoot, string candidatePath)
    {
        string relative = Path.GetRelativePath(repositoryRoot, candidatePath);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The documentation analysis cache must resolve inside the repository root.");
        }
    }

    private static void RejectLinkOrReparsePoint(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        try
        {
            if (info.LinkTarget is not null
                || info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    $"The documentation analysis cache path cannot contain a symbolic link or reparse point: '{path}'.");
            }
        }
        catch (FileNotFoundException)
        {
            // A not-yet-created cache component is validated again immediately after creation.
        }
    }

    private static bool IsBusy(string reason) =>
        reason.Contains("locked", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("busy", StringComparison.OrdinalIgnoreCase);

    private static bool IsOperationalFailure(string reason) =>
        reason.Contains("readonly", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("read-only", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("disk i/o", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("unable to open database", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("database or disk is full", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("interrupted", StringComparison.OrdinalIgnoreCase);

    private static string FailureDetail(ProcessResult result, string reason) =>
        reason.Length == 0
            ? $"sqlite3 exited with code {result.ExitCode}."
            : $"sqlite3 exited with code {result.ExitCode}: {reason}";

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "Analysis persistence is disabled until .kyber-weave/.gitignore contains the exact 'cache/' entry and sqlite3 is available.");
        }
    }

    private static InvalidDataException CorruptCache(string message, Exception? innerException = null) =>
        new($"The documentation analysis cache is invalid. {message}", innerException);
}
