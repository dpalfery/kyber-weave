using KyberWeave.Core.FileSystem;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Processes;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace KyberWeave.Core.Docs.Analysis.Persistence;

/// <summary>
/// Stores local analysis evidence in a process-local SQLite database.
/// </summary>
/// <remarks>
/// Every caller-controlled value is bound as a named SQL parameter, so prose cannot
/// become SQL even when it contains quotes, newlines, or SQL-looking text.
/// </remarks>
public sealed partial class SqliteAnalysisPersistence : IAnalysisPersistence
{
    public const int SchemaVersion = 1;

    private const double NormalizedVectorTolerance = 0.0001;
    private const int BusyAttempts = 3;
    private const int BusyAttemptTimeoutSeconds = 1;
    private const int SqliteBusyResultCode = 5;
    private const int SqliteLockedResultCode = 6;

    // Generated so a hostile sqlite message cannot hang the retry classifier.
    [GeneratedRegex(@"\blocked\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LockedWordRegex();

    [GeneratedRegex(@"\bbusy\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BusyWordRegex();
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);
    private static readonly Lock SqliteProviderGate = new();
    private static volatile bool SqliteProviderInitialized;
    private readonly string _repositoryRoot;
    private readonly IWritableFileSystem _files;

    /// <summary>Opens the cache under <paramref name="repositoryRoot"/>, creating it when the repository's state is safe to cache in.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="files">Creates the cache folder and inspects the path components for links.</param>
    /// <param name="paths">Resolves the repository root to a full path.</param>
    /// <param name="environment">Supplies the directory a relative root resolves against.</param>
    /// <param name="processes">Retained so existing composition roots and seam tests keep compiling; the cache no longer starts a process.</param>
    /// <param name="safety">Decides whether the cache is protected from accidental commits.</param>
    public SqliteAnalysisPersistence(
        string repositoryRoot,
        IWritableFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IBoundedProcessExecutor processes,
        AnalysisCacheSafety safety)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(safety);
        _files = files;

        string fullRoot = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        _repositoryRoot = fullRoot;
        DatabasePath = Path.Combine(
            fullRoot,
            UserStatePaths.DirectoryName,
            "cache",
            "docs-analysis.sqlite3");

        EnsureSafePersistencePath(fullRoot, DatabasePath);
        if (!safety.IsSafe(fullRoot) || !CanStartSqlite()) return;

        _files.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
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

        return ReadPayloadRows<PersistedClaim>("analysis_claims", "id", claimIds);
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

        return ReadPayloadRows<PersistedCandidateFingerprint>("analysis_candidates", "candidate_id", candidateIds);
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

        return ReadPayloadRows<AnalysisVerdict>("analysis_verdicts", "candidate_id", candidateIds);
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
        string[] contextualHashes = keys.Select(k => k.ContextualHash).Distinct(StringComparer.Ordinal).ToArray();
        Dictionary<EmbeddingCacheKey, StoredEmbedding> loaded = new Dictionary<EmbeddingCacheKey, StoredEmbedding>();

        ExecuteSqlite(connection => ReadEmbeddingRows(connection, contextualHashes, requested, loaded));
        return loaded;
    }

    private static void ReadEmbeddingRows(
        SqliteConnection connection,
        IReadOnlyList<string> contextualHashes,
        IReadOnlySet<EmbeddingCacheKey> requested,
        IDictionary<EmbeddingCacheKey, StoredEmbedding> loaded)
    {
        {
            // One statement for every requested hash: the hashes travel as one JSON array parameter, so the
            // statement binds one variable however many there are. Keys are stored as UTF-8 BLOBs.
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT contextual_hash, provider_fingerprint, model, dimensions, encoding, vector " +
                "FROM analysis_embeddings WHERE contextual_hash IN " +
                "(SELECT CAST(value AS BLOB) FROM json_each($contextual_hashes));";
            command.Parameters.AddWithValue("$contextual_hashes", JsonSerializer.Serialize(contextualHashes));

            using SqliteDataReader reader = command.ExecuteReader();
            int contextualHashOrdinal = reader.GetOrdinal("contextual_hash");
            int providerFingerprintOrdinal = reader.GetOrdinal("provider_fingerprint");
            int modelOrdinal = reader.GetOrdinal("model");
            int dimensionsOrdinal = reader.GetOrdinal("dimensions");
            int encodingOrdinal = reader.GetOrdinal("encoding");
            int vectorOrdinal = reader.GetOrdinal("vector");
            while (reader.Read())
            {
                try
                {
                    int storedDimensions = reader.GetInt32(dimensionsOrdinal);
                    EmbeddingCacheKey key = new EmbeddingCacheKey(
                        Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(contextualHashOrdinal)),
                        Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(providerFingerprintOrdinal)),
                        Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(modelOrdinal)),
                        storedDimensions < 0 ? null : storedDimensions,
                        Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(encodingOrdinal)));
                    if (!requested.Contains(key)) continue;

                    IReadOnlyList<float> vector = DecodeVector(reader.GetFieldValue<byte[]>(vectorOrdinal));
                    Validate(new StoredEmbedding(key, vector));
                    loaded[key] = new StoredEmbedding(key, vector);
                }
                catch (Exception exception) when (
                    exception is FormatException or ArgumentException or InvalidDataException or InvalidCastException)
                {
                    throw CorruptCache("An embedding row is invalid.", exception);
                }
            }
        }
    }

    public void SaveEmbeddings(IReadOnlyCollection<StoredEmbedding> embeddings)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        EnsureAvailable();
        foreach (StoredEmbedding embedding in embeddings) Validate(embedding);

        ExecuteWriteTransaction(embeddings.Select(EmbeddingUpsert));
    }

    private static Action<SqliteConnection, SqliteTransaction> EmbeddingUpsert(StoredEmbedding embedding) =>
        (connection, transaction) =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO analysis_embeddings(" +
                "contextual_hash, provider_fingerprint, model, dimensions, encoding, vector) VALUES (" +
                "$contextual_hash, $provider_fingerprint, $model, $dimensions, $encoding, $vector) " +
                "ON CONFLICT(contextual_hash, provider_fingerprint, model, dimensions, encoding) " +
                "DO UPDATE SET vector = excluded.vector;";
            command.Parameters.AddWithValue(
                "$contextual_hash",
                Encoding.UTF8.GetBytes(embedding.Key.ContextualHash));
            command.Parameters.AddWithValue(
                "$provider_fingerprint",
                Encoding.UTF8.GetBytes(embedding.Key.ProviderFingerprint));
            command.Parameters.AddWithValue("$model", Encoding.UTF8.GetBytes(embedding.Key.Model));
            command.Parameters.AddWithValue("$dimensions", embedding.Key.Dimensions ?? -1);
            command.Parameters.AddWithValue("$encoding", Encoding.UTF8.GetBytes(embedding.Key.Encoding));
            command.Parameters.AddWithValue("$vector", EncodeVector(embedding.Vector));
            command.ExecuteNonQuery();
        };

    private void InitializeSchema()
    {
        int version = ExecuteSqlite(ReadSchemaVersion);
        if (version > SchemaVersion)
            throw CorruptCache(
                $"The analysis cache uses schema version {version}, newer than supported version {SchemaVersion}.");

        if (version == 0)
        {
            ExecuteSqlite(connection =>
            {
                using (SqliteCommand foreignKeys = connection.CreateCommand())
                {
                    foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                    foreignKeys.ExecuteNonQuery();
                }

                using SqliteTransaction transaction =
                    connection.BeginTransaction(System.Data.IsolationLevel.Serializable);

                using (SqliteCommand claims = connection.CreateCommand())
                {
                    claims.Transaction = transaction;
                    claims.CommandText =
                        "CREATE TABLE IF NOT EXISTS analysis_claims (" +
                        "id BLOB PRIMARY KEY NOT NULL, payload BLOB NOT NULL);";
                    claims.ExecuteNonQuery();
                }

                using (SqliteCommand candidates = connection.CreateCommand())
                {
                    candidates.Transaction = transaction;
                    candidates.CommandText =
                        "CREATE TABLE IF NOT EXISTS analysis_candidates (" +
                        "candidate_id BLOB PRIMARY KEY NOT NULL, payload BLOB NOT NULL);";
                    candidates.ExecuteNonQuery();
                }

                using (SqliteCommand verdicts = connection.CreateCommand())
                {
                    verdicts.Transaction = transaction;
                    verdicts.CommandText =
                        "CREATE TABLE IF NOT EXISTS analysis_verdicts (" +
                        "candidate_id BLOB PRIMARY KEY NOT NULL, payload BLOB NOT NULL, " +
                        "FOREIGN KEY(candidate_id) REFERENCES analysis_candidates(candidate_id) ON DELETE CASCADE);";
                    verdicts.ExecuteNonQuery();
                }

                using (SqliteCommand embeddings = connection.CreateCommand())
                {
                    embeddings.Transaction = transaction;
                    embeddings.CommandText =
                        "CREATE TABLE IF NOT EXISTS analysis_embeddings (" +
                        "contextual_hash BLOB NOT NULL, provider_fingerprint BLOB NOT NULL, " +
                        "model BLOB NOT NULL, dimensions INTEGER NOT NULL, encoding BLOB NOT NULL, " +
                        "vector BLOB NOT NULL, PRIMARY KEY(" +
                        "contextual_hash, provider_fingerprint, model, dimensions, encoding));";
                    embeddings.ExecuteNonQuery();
                }

                ValidateSchema(connection, transaction);
                using (SqliteCommand schemaVersion = connection.CreateCommand())
                {
                    schemaVersion.Transaction = transaction;
                    // The stamp repeats SchemaVersion: a pragma value cannot be a bound parameter, and the command text
                    // stays a literal. Change both together; AnalysisPersistenceTests fails when they differ.
                    schemaVersion.CommandText = "PRAGMA user_version = 1;";
                    schemaVersion.ExecuteNonQuery();
                }

                transaction.Commit();
            });
            return;
        }

        ExecuteSqlite(connection =>
        {
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            ValidateSchema(connection, transaction: null);
        });
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? value = command.ExecuteScalar();
        if (value is not long version || version < int.MinValue || version > int.MaxValue)
            throw CorruptCache("The analysis cache schema version is not an integer.");
        return (int)version;
    }

    private static void ValidateSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using (SqliteCommand claims = connection.CreateCommand())
        {
            claims.Transaction = transaction;
            claims.CommandText = "SELECT id, payload FROM analysis_claims LIMIT 0;";
            claims.ExecuteNonQuery();
        }

        using (SqliteCommand candidates = connection.CreateCommand())
        {
            candidates.Transaction = transaction;
            candidates.CommandText = "SELECT candidate_id, payload FROM analysis_candidates LIMIT 0;";
            candidates.ExecuteNonQuery();
        }

        using (SqliteCommand verdicts = connection.CreateCommand())
        {
            verdicts.Transaction = transaction;
            verdicts.CommandText = "SELECT candidate_id, payload FROM analysis_verdicts LIMIT 0;";
            verdicts.ExecuteNonQuery();
        }

        using (SqliteCommand embeddings = connection.CreateCommand())
        {
            embeddings.Transaction = transaction;
            embeddings.CommandText =
                "SELECT contextual_hash, provider_fingerprint, model, dimensions, encoding, vector " +
                "FROM analysis_embeddings LIMIT 0;";
            embeddings.ExecuteNonQuery();
        }
    }

    private IReadOnlyDictionary<string, T> ReadPayloadRows<T>(
        string table,
        string keyColumn,
        IReadOnlyCollection<string> requestedKeys)
    {
        if (requestedKeys.Count == 0)
        {
            return new Dictionary<string, T>(StringComparer.Ordinal);
        }

        Dictionary<string, T> loaded = new Dictionary<string, T>(StringComparer.Ordinal);
        string[] distinctKeys = requestedKeys.Distinct(StringComparer.Ordinal).ToArray();
        ExecuteSqlite(connection => ReadPayloadRowsBatch(connection, table, keyColumn, distinctKeys, loaded));
        return loaded;
    }

    private static void ReadPayloadRowsBatch<T>(
        SqliteConnection connection,
        string table,
        string keyColumn,
        IReadOnlyList<string> requestedKeys,
        IDictionary<string, T> loaded)
    {
        {
            // One statement for every requested key, passed as one JSON array parameter (see ReadEmbeddingRows).
            using SqliteCommand command = connection.CreateCommand();
            if (table == "analysis_claims")
            {
                command.CommandText =
                    "SELECT id, payload FROM analysis_claims WHERE id IN " +
                    "(SELECT CAST(value AS BLOB) FROM json_each($keys));";
            }
            else if (table == "analysis_candidates")
            {
                command.CommandText =
                    "SELECT candidate_id, payload FROM analysis_candidates WHERE candidate_id IN " +
                    "(SELECT CAST(value AS BLOB) FROM json_each($keys));";
            }
            else
            {
                command.CommandText =
                    "SELECT candidate_id, payload FROM analysis_verdicts WHERE candidate_id IN " +
                    "(SELECT CAST(value AS BLOB) FROM json_each($keys));";
            }

            command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(requestedKeys));

            using SqliteDataReader reader = command.ExecuteReader();
            int keyOrdinal = reader.GetOrdinal(keyColumn);
            int payloadOrdinal = reader.GetOrdinal("payload");
            while (reader.Read())
            {
                try
                {
                    string key = Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(keyOrdinal));
                    T value = JsonSerializer.Deserialize<T>(
                        reader.GetFieldValue<byte[]>(payloadOrdinal),
                        SerializerOptions)
                        ?? throw CorruptCache($"Table '{table}' contains an empty payload.");
                    ValidateLoadedPayload(table, key, value);
                    loaded[key] = value;
                }
                catch (Exception exception) when (
                    exception is FormatException or JsonException or NotSupportedException or ArgumentException
                        or InvalidCastException)
                {
                    throw CorruptCache($"Table '{table}' contains an invalid payload.", exception);
                }
            }
        }
    }

    private void ExecuteWriteTransaction(IEnumerable<Action<SqliteConnection, SqliteTransaction>> statements)
    {
        ExecuteSqlite(connection =>
        {
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            using SqliteTransaction transaction =
                connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            foreach (Action<SqliteConnection, SqliteTransaction> statement in statements)
                statement(connection, transaction);
            transaction.Commit();
        });
    }

    private T ExecuteSqlite<T>(Func<SqliteConnection, T> operation)
    {
        for (int attempt = 1; attempt <= BusyAttempts; attempt++)
        {
            EnsureSafePersistencePath(_repositoryRoot, DatabasePath);
            try
            {
                using SqliteConnection connection = OpenConnection();
                return operation(connection);
            }
            catch (SqliteException exception) when (IsBusy(exception))
            {
                if (attempt < BusyAttempts) continue;
                throw new InvalidOperationException(
                    $"The documentation analysis cache remained locked after {BusyAttempts} bounded attempts. " +
                    FailureDetail(exception),
                    exception);
            }
            catch (SqliteException exception) when (IsOperationalFailure(exception))
            {
                throw new InvalidOperationException(
                    "The documentation analysis cache could not be accessed. " + FailureDetail(exception),
                    exception);
            }
            catch (SqliteException exception)
            {
                throw CorruptCache(FailureDetail(exception), exception);
            }
        }

        throw new InvalidOperationException("The documentation analysis cache operation did not complete.");
    }

    private void ExecuteSqlite(Action<SqliteConnection> operation) =>
        ExecuteSqlite(connection =>
        {
            operation(connection);
            return true;
        });

    private SqliteConnection OpenConnection()
    {
        SqliteConnectionStringBuilder builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Microsoft.Data.Sqlite retries a busy database for the command timeout, overriding any
            // busy_timeout pragma, so this is the per-attempt bound: three attempts wait about 3 s in all.
            DefaultTimeout = BusyAttemptTimeoutSeconds
        };
        SqliteConnection connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            return connection;
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or DllNotFoundException
                or EntryPointNotFoundException
                or TypeInitializationException
                or BadImageFormatException)
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool CanStartSqlite()
    {
        if (!EnsureSqliteProviderRegistered()) return false;
        try
        {
            using SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            return true;
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or DllNotFoundException
                or EntryPointNotFoundException
                or TypeInitializationException
                or BadImageFormatException)
        {
            return false;
        }
    }

    private static bool EnsureSqliteProviderRegistered()
    {
        if (SqliteProviderInitialized) return true;
        lock (SqliteProviderGate)
        {
            if (SqliteProviderInitialized) return true;
            try
            {
                raw.SetProvider(new SQLite3Provider_sqlite3());
                SqliteProviderInitialized = true;
                return true;
            }
            catch (Exception exception) when (
                exception is DllNotFoundException
                    or EntryPointNotFoundException
                    or TypeInitializationException
                    or InvalidOperationException
                    or BadImageFormatException)
            {
                return false;
            }
        }
    }

    private static Action<SqliteConnection, SqliteTransaction> ClaimUpsert(PersistedClaim claim) =>
        (connection, transaction) =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO analysis_claims(id, payload) VALUES ($id, $payload) " +
                "ON CONFLICT(id) DO UPDATE SET payload = excluded.payload;";
            command.Parameters.AddWithValue("$id", Encoding.UTF8.GetBytes(claim.Id));
            command.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(claim, SerializerOptions));
            command.ExecuteNonQuery();
        };

    private static Action<SqliteConnection, SqliteTransaction> CandidateUpsert(PersistedCandidateFingerprint candidate) =>
        (connection, transaction) =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO analysis_candidates(candidate_id, payload) VALUES ($candidate_id, $payload) " +
                "ON CONFLICT(candidate_id) DO UPDATE SET payload = excluded.payload;";
            command.Parameters.AddWithValue("$candidate_id", Encoding.UTF8.GetBytes(candidate.CandidateId));
            command.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(candidate, SerializerOptions));
            command.ExecuteNonQuery();
        };

    private static Action<SqliteConnection, SqliteTransaction> VerdictUpsert(AnalysisVerdict verdict) =>
        (connection, transaction) =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO analysis_verdicts(candidate_id, payload) VALUES ($candidate_id, $payload) " +
                "ON CONFLICT(candidate_id) DO UPDATE SET payload = excluded.payload;";
            command.Parameters.AddWithValue("$candidate_id", Encoding.UTF8.GetBytes(verdict.CandidateId));
            command.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(verdict, SerializerOptions));
            command.ExecuteNonQuery();
        };

    private static byte[] EncodeVector(IReadOnlyList<float> vector)
    {
        byte[] bytes = new byte[checked(vector.Count * sizeof(float))];
        for (int index = 0; index < vector.Count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), vector[index]);
        return bytes;
    }

    private static IReadOnlyList<float> DecodeVector(byte[] bytes)
    {
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
            || (embedding.Key.Dimensions is not null && embedding.Key.Dimensions.Value != embedding.Vector.Count))
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

    private void EnsureSafePersistencePath(string repositoryRoot, string databasePath)
    {
        EnsureContained(repositoryRoot, databasePath);
        string stateDirectory = Path.Combine(repositoryRoot, UserStatePaths.DirectoryName);
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

    private void RejectLinkOrReparsePoint(string path)
    {
        try
        {
            if (_files.GetLinkTarget(path) is not null
                || _files.PathExists(path) && _files.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
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

    private static bool IsBusy(SqliteException exception)
    {
        // SQLite reports lock contention as a result code and free text whose wording
        // shifts between builds, so classify on both. A busy result must not read as
        // corruption: there is no corrupt payload to point at, only a lock that
        // outlasted the bounded retries.
        string output = exception.Message;
        return exception.SqliteErrorCode == SqliteBusyResultCode
            || exception.SqliteErrorCode == SqliteLockedResultCode
            || output.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
            || output.Contains("database is busy", StringComparison.OrdinalIgnoreCase)
            || LockedWordRegex().IsMatch(output)
            || BusyWordRegex().IsMatch(output);
    }

    private static bool IsOperationalFailure(SqliteException exception)
    {
        if (exception.SqliteErrorCode is 3 or 8 or 9 or 10 or 13 or 14)
            return true;

        string reason = exception.Message;
        return reason.Contains("readonly", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("read-only", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("disk i/o", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("unable to open database", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("database or disk is full", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("interrupted", StringComparison.OrdinalIgnoreCase);
    }

    private static string FailureDetail(SqliteException exception)
    {
        string reason = exception.Message.Trim();
        return reason.Length == 0
            ? $"SQLite error {exception.SqliteErrorCode} on the cache file; check that it and its folder are readable and writable."
            : $"SQLite error {exception.SqliteErrorCode} on the cache file: {reason}";
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "Analysis persistence is disabled until .kyber-weave/.gitignore contains the exact 'cache/' entry and the SQLite library can load.");
        }
    }

    private static InvalidDataException CorruptCache(string message, Exception? innerException = null) =>
        new($"The documentation analysis cache is invalid. {message}", innerException);
}
