using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using KyberDash.Domain.Schema;

namespace KyberDash.Data.Repositories;

/// <summary>
/// SQLite-backed implementation of <see cref="IRelationalStoreWriter"/> and the write half of
/// <see cref="IProjectionStore"/>, executing DDL initialization and transactional hex-encoded
/// SQL batches through <see cref="BaseSqliteRepository.ExecuteSqliteCore"/>.
/// </summary>
public partial class SqliteRelationalStore : BaseSqliteRepository, IRelationalStoreWriter, IProjectionStore
{
    /// <summary>Current normalized relational schema version stamped in <c>store_state</c>.</summary>
    /// <remarks>
    /// Version 30 adds <c>record_skill_listing</c>, the skills a harness listed to the agent on a
    /// record. The rebuild-and-re-ingest pattern applies, so every source is read again and its
    /// listings, invoked skills and tool schemas are stored by the current mappers.
    /// </remarks>
    public const int SchemaVersion = 30;

    /// <summary>Current detector generation stamped in <c>store_state</c>.</summary>
    /// <remarks>
    /// Generation 6: records read back carry their source name, so the projector's source rules
    /// (the Droid plugin/file split and the file-source twin collapses) apply again, and every
    /// projection built under generation 5's source-less hydration is rebuilt.
    /// </remarks>
    public const int DetectorVersion = 6;

    /// <summary>Maximum retained WAL journal size in bytes after checkpoint (32 MiB).</summary>
    public const int WalSizeLimitBytes = 32 * 1024 * 1024;

    /// <summary>Error code raised when a relational or domain constraint invariant is violated.</summary>
    public const string ConstraintViolatedCode = "STORE_CONSTRAINT_VIOLATED";

    /// <summary>Error code raised when standalone JSON object or array text is passed for a plain-text column.</summary>
    public const string JsonTextRefusedCode = "STORE_JSON_TEXT_REFUSED";

    private const int BusyTimeoutMilliseconds = 5000;
    private const int BusyAttempts = 3;

    private static readonly string[] ProblemChildTables =
    [
        "ingest_problem_span",
        "ingest_problem_location",
        "ingest_problem_session",
        "ingest_problem_harness",
        "ingest_problem_timestamp",
        "ingest_problem_stack",
    ];

    private readonly Lock writeGate = new();
    private readonly IRelationalStoreFileSystem fileSystem;
    private readonly Action? onRebuiltFromOlderSchema;
    private readonly Action<string> log;
    private bool initialized;

    /// <summary>
    /// Initializes or opens the relational SQLite store at <paramref name="databasePath"/> and
    /// ensures the schema is initialized at <see cref="SchemaVersion"/> before returning.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file, created when absent.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    public SqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
        : this(databasePath, circuitBreaker, initialize: true, fileSystem: null, onRebuiltFromOlderSchema: null, log: null)
    {
    }

    /// <summary>
    /// Initializes or opens the relational SQLite store at <paramref name="databasePath"/> with
    /// explicit filesystem, rebuild callback, and diagnostic logging seams.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file, created when absent.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    /// <param name="fileSystem">Filesystem seam; defaults to <see cref="PhysicalRelationalStoreFileSystem.Instance"/> when <see langword="null"/>.</param>
    /// <param name="onRebuiltFromOlderSchema">Optional callback invoked after an older schema is rebuilt.</param>
    /// <param name="log">Optional diagnostic logger; defaults to <c>Console.Error.WriteLine</c> when <see langword="null"/>.</param>
    public SqliteRelationalStore(
        string databasePath,
        ICircuitBreaker circuitBreaker,
        IRelationalStoreFileSystem? fileSystem,
        Action? onRebuiltFromOlderSchema = null,
        Action<string>? log = null)
        : this(databasePath, circuitBreaker, initialize: true, fileSystem, onRebuiltFromOlderSchema, log)
    {
    }

    /// <summary>Opens or initializes the relational store resolved from <paramref name="options"/>.</summary>
    /// <param name="options">Composition options supplying an optional explicit database path.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    public SqliteRelationalStore(DashOptions options, ICircuitBreaker circuitBreaker)
        : this(ResolveDatabasePath(options), circuitBreaker, initialize: true, fileSystem: null, onRebuiltFromOlderSchema: null, log: null)
    {
    }

    /// <summary>
    /// Opens or initializes the relational store resolved from <paramref name="options"/> with
    /// explicit filesystem, rebuild callback, and diagnostic logging seams.
    /// </summary>
    /// <param name="options">Composition options supplying an optional explicit database path.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    /// <param name="fileSystem">Filesystem seam; defaults to <see cref="PhysicalRelationalStoreFileSystem.Instance"/> when <see langword="null"/>.</param>
    /// <param name="onRebuiltFromOlderSchema">Optional callback invoked after an older schema is rebuilt.</param>
    /// <param name="log">Optional diagnostic logger; defaults to <c>Console.Error.WriteLine</c> when <see langword="null"/>.</param>
    public SqliteRelationalStore(
        DashOptions options,
        ICircuitBreaker circuitBreaker,
        IRelationalStoreFileSystem? fileSystem,
        Action? onRebuiltFromOlderSchema = null,
        Action<string>? log = null)
        : this(ResolveDatabasePath(options), circuitBreaker, initialize: true, fileSystem, onRebuiltFromOlderSchema, log)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="SqliteRelationalStore"/> instance, optionally deferring
    /// <see cref="EnsureInitialized"/> for subclasses that configure scripted responses first.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    /// <param name="initialize">Whether to initialize schema and <c>store_state</c> immediately.</param>
    protected SqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker, bool initialize)
        : this(databasePath, circuitBreaker, initialize, fileSystem: null, onRebuiltFromOlderSchema: null, log: null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="SqliteRelationalStore"/> instance with explicit filesystem,
    /// rebuild callback, and diagnostic logging seams, optionally deferring <see cref="EnsureInitialized"/>.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    /// <param name="initialize">Whether to initialize schema and <c>store_state</c> immediately.</param>
    /// <param name="fileSystem">Filesystem seam; defaults to <see cref="PhysicalRelationalStoreFileSystem.Instance"/> when <see langword="null"/>.</param>
    /// <param name="onRebuiltFromOlderSchema">Optional callback invoked after an older schema is rebuilt.</param>
    /// <param name="log">Optional diagnostic logger; defaults to <c>Console.Error.WriteLine</c> when <see langword="null"/>.</param>
    protected SqliteRelationalStore(
        string databasePath,
        ICircuitBreaker circuitBreaker,
        bool initialize,
        IRelationalStoreFileSystem? fileSystem,
        Action? onRebuiltFromOlderSchema = null,
        Action<string>? log = null)
        : base(
            ValidateDatabasePath(databasePath),
            circuitBreaker,
            batchTimeout: TimeSpan.FromSeconds(120),
            busyTimeoutMilliseconds: BusyTimeoutMilliseconds,
            busyAttempts: BusyAttempts)
    {
        this.fileSystem = fileSystem ?? PhysicalRelationalStoreFileSystem.Instance;
        this.onRebuiltFromOlderSchema = onRebuiltFromOlderSchema;

        // D15: the diagnostic log defaults to Console.Error so a destructive rebuild is never
        // silent, even when a host or test wires the store without a logger.
        this.log = log ?? Console.Error.WriteLine;

        if (initialize)
        {
            EnsureInitialized();
        }
    }

    /// <summary>
    /// Ensures WAL pragmas, schema DDL from <see cref="SchemaDdlGenerator.GenerateSchemaDdl"/>,
    /// and the <c>store_state</c> version stamp are applied idempotently.
    /// </summary>
    public void EnsureInitialized()
    {
        Action? rebuildCallback;
        lock (writeGate)
        {
            if (initialized)
            {
                return;
            }

            rebuildCallback = Initialize();
            initialized = true;
        }

        rebuildCallback?.Invoke();
    }

    /// <inheritdoc />
    public void WriteBatch(CollectedBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Source);
        ArgumentNullException.ThrowIfNull(batch.Sessions);
        ArgumentNullException.ThrowIfNull(batch.Records);
        ArgumentNullException.ThrowIfNull(batch.Problems);

        StringBuilder body = new();

        HashSet<string> harnesses = new(StringComparer.Ordinal);
        AppendHarnessInsert(body, harnesses, batch.Source.HarnessId);
        foreach (CollectedSession session in batch.Sessions)
        {
            ArgumentNullException.ThrowIfNull(session);
            AppendHarnessInsert(body, harnesses, session.HarnessId);
        }

        foreach (CollectedRecord record in batch.Records)
        {
            ArgumentNullException.ThrowIfNull(record);
            AppendHarnessInsert(body, harnesses, record.HarnessId);
        }

        string sourceIdExpr = AppendSourceInsert(body, batch.Source);

        HashSet<string> seenSessions = new(StringComparer.Ordinal);
        foreach (CollectedSession session in batch.Sessions)
        {
            if (!seenSessions.Add(session.SessionKey))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate session_id '{session.SessionKey}' in CollectedBatch.Sessions.");
            }

            AppendSessionUpsert(body, session);
        }

        HashSet<string> seenRecords = new(StringComparer.Ordinal);
        foreach (CollectedRecord record in batch.Records)
        {
            if (!seenRecords.Add(record.RecordId))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate span_id '{record.RecordId}' in CollectedBatch.Records.");
            }

            AppendRecordInsert(body, sourceIdExpr, record);
        }

        HashSet<string> seenProblems = new(StringComparer.Ordinal);
        foreach (CollectedProblem problem in batch.Problems)
        {
            ArgumentNullException.ThrowIfNull(problem);
            if (!seenProblems.Add(problem.ProblemKey))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate problem_key '{problem.ProblemKey}' in CollectedBatch.Problems.");
            }

            AppendProblemUpsert(body, problem);
        }

        if (batch.Sessions.Count > 0 || batch.Records.Count > 0)
        {
            AppendWriteMarkerAdvance(body, DirtySessionKeysOf(batch));
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void DeleteSource(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        string sourceIdExpr = GuardedTextExpr("source", "source_id", sourceId);
        StringBuilder body = new();
        body.Append(CultureInfo.InvariantCulture, $"DELETE FROM source WHERE source_id = {sourceIdExpr};\n");

        // The cascade removes records of sessions this call does not know, so a source delete
        // always marks the range wildcard.
        AppendWriteMarkerAdvance(body, dirtyKeys: null);
        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void ClearEstimatedRecordTokens(string harnessId, string sourceLocator, EstimatedRecordTokens estimated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLocator);
        if (estimated == EstimatedRecordTokens.None)
        {
            return;
        }

        string harnessExpr = GuardedTextExpr("source", "harness_id", harnessId);
        string locatorExpr = GuardedTextExpr("source", "locator", sourceLocator);
        string sourceSpans =
            "SELECT r.span_id FROM record r JOIN source s ON s.source_id = r.source_id " +
            $"WHERE s.harness_id = {harnessExpr} AND s.source_kind = {TextExpr("file")} AND s.locator = {locatorExpr}";

        StringBuilder body = new();
        switch (estimated)
        {
            case EstimatedRecordTokens.AllRecordTokens:
                body.Append(CultureInfo.InvariantCulture, $"DELETE FROM record_token_count WHERE span_id IN ({sourceSpans});\n");
                body.Append(CultureInfo.InvariantCulture, $"DELETE FROM record_reported_total WHERE span_id IN ({sourceSpans});\n");
                break;
            case EstimatedRecordTokens.RecordsWithoutReportedTotal:
                body.Append(
                    CultureInfo.InvariantCulture,
                    $"DELETE FROM record_token_count WHERE span_id IN ({sourceSpans}) AND span_id NOT IN (SELECT span_id FROM record_reported_total);\n");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(estimated), estimated, "Unsupported estimated token scope.");
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void WriteProblem(CollectedProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        StringBuilder body = new();
        AppendProblemUpsert(body, problem);
        ExecuteTransaction(body.ToString());
    }

    public void ReplaceSessionProjection(StoredSessionProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ReplaceSessionProjections([projection]);
    }

    /// <inheritdoc />
    public void ReplaceSessionProjections(IEnumerable<StoredSessionProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(projections);

        StringBuilder body = new();
        HashSet<string> seenSessions = new(StringComparer.Ordinal);
        foreach (StoredSessionProjection projection in projections)
        {
            ArgumentNullException.ThrowIfNull(projection);
            if (!seenSessions.Add(projection.SessionId))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate session_id '{projection.SessionId}' in ReplaceSessionProjections.");
            }

            AppendSessionProjectionReplace(body, projection);
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void DeleteSessionProjection(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string sessionIdExpr = GuardedTextExpr("session_projection", "session_id", sessionId);
        ExecuteTransaction($"DELETE FROM session_projection WHERE session_id = {sessionIdExpr};\n");
    }

    public void ReplaceSessionTurns(string sessionId, IEnumerable<StoredSessionTurn> turns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(turns);

        string sessionIdExpr = GuardedTextExpr("session_turn", "session_id", sessionId);
        StringBuilder body = new();
        body.Append(CultureInfo.InvariantCulture, $"DELETE FROM session_turn WHERE session_id = {sessionIdExpr};\n");

        foreach (StoredSessionTurn turn in turns)
        {
            ArgumentNullException.ThrowIfNull(turn);
            if (!string.Equals(turn.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"StoredSessionTurn.SessionId '{turn.SessionId}' does not match '{sessionId}'.");
            }

            string spanIdExpr = GuardedTextExpr("session_turn", "span_id", turn.SpanId);
            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO session_turn (session_id, position, span_id) VALUES ({sessionIdExpr}, {IntSql(turn.Position)}, {spanIdExpr});\n");
        }

        ExecuteTransaction(body.ToString());
    }

    public void ReplaceRuns(IEnumerable<StoredRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        StringBuilder body = new();
        HashSet<string> seenRuns = new(StringComparer.Ordinal);
        foreach (StoredRun run in runs)
        {
            ArgumentNullException.ThrowIfNull(run);
            if (!seenRuns.Add(run.RunId))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate run_id '{run.RunId}' in ReplaceRuns.");
            }

            AppendRunUpsert(body, run);
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void DeleteRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string runIdExpr = GuardedTextExpr("run", "run_id", runId);
        ExecuteTransaction($"DELETE FROM run WHERE run_id = {runIdExpr};\n");
    }

    public void ReplaceExecutions(IEnumerable<StoredExecution> executions)
    {
        ArgumentNullException.ThrowIfNull(executions);

        List<StoredExecution> items = [];
        HashSet<string> seenExecutions = new(StringComparer.Ordinal);
        foreach (StoredExecution execution in executions)
        {
            ArgumentNullException.ThrowIfNull(execution);
            if (!seenExecutions.Add(execution.ExecutionId))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate execution_id '{execution.ExecutionId}' in ReplaceExecutions.");
            }

            items.Add(execution);
        }

        StringBuilder body = new();
        List<string> executionIdExprs = new(items.Count);

        foreach (StoredExecution execution in items)
        {
            string executionIdExpr = GuardedTextExpr("execution", "execution_id", execution.ExecutionId);
            string runIdExpr = GuardedTextExpr("execution", "run_id", execution.RunId);
            string harnessIdExpr = GuardedTextExpr("execution", "harness_id", execution.HarnessId);
            executionIdExprs.Add(executionIdExpr);

            body.Append(CultureInfo.InvariantCulture, $"DELETE FROM execution WHERE execution_id = {executionIdExpr};\n");
            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO execution (execution_id, run_id, harness_id, is_root) VALUES ({executionIdExpr}, {runIdExpr}, {harnessIdExpr}, {BoolSql(execution.IsRoot)});\n");
        }

        for (int i = 0; i < items.Count; i++)
        {
            StoredExecution execution = items[i];
            string executionIdExpr = executionIdExprs[i];

            AppendOptionalTextChild(body, "execution_session", "execution_id", executionIdExpr, "session_id", execution.SessionId);
            AppendOptionalTextChild(body, "execution_parent", "execution_id", executionIdExpr, "parent_execution_id", execution.ParentExecutionId);
            AppendOptionalTextChild(body, "execution_agent", "execution_id", executionIdExpr, "agent_name", execution.AgentName);

            RequireMatchedPair(
                execution.StartedAtUtc is not null,
                execution.EndedAtUtc is not null,
                "execution_bounds",
                "started_at_utc",
                "ended_at_utc");

            if (execution.StartedAtUtc is not null && execution.EndedAtUtc is not null)
            {
                string startedExpr = GuardedTextExpr("execution_bounds", "started_at_utc", execution.StartedAtUtc);
                string endedExpr = GuardedTextExpr("execution_bounds", "ended_at_utc", execution.EndedAtUtc);
                body.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO execution_bounds (execution_id, started_at_utc, ended_at_utc) VALUES ({executionIdExpr}, {startedExpr}, {endedExpr});\n");
            }
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void DeleteExecutionsForRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string runIdExpr = GuardedTextExpr("execution", "run_id", runId);
        ExecuteTransaction($"DELETE FROM execution WHERE run_id = {runIdExpr};\n");
    }

    public void ReplaceHarnessRollups(IEnumerable<StoredHarnessRollup> rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        StringBuilder body = new();
        HashSet<string> seenHarnesses = new(StringComparer.Ordinal);
        foreach (StoredHarnessRollup rollup in rollups)
        {
            ArgumentNullException.ThrowIfNull(rollup);
            if (!seenHarnesses.Add(rollup.HarnessId))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate harness_id '{rollup.HarnessId}' in ReplaceHarnessRollups.");
            }

            AppendHarnessRollupReplace(body, rollup);
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void DeleteHarnessRollup(string harnessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        string harnessIdExpr = GuardedTextExpr("harness_rollup", "harness_id", harnessId);
        ExecuteTransaction($"DELETE FROM harness_rollup WHERE harness_id = {harnessIdExpr};\n");
    }

    public void ReplaceFindings(IEnumerable<StoredFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        StringBuilder body = new();
        HashSet<string> seenFindings = new(StringComparer.Ordinal);
        foreach (StoredFinding finding in findings)
        {
            ArgumentNullException.ThrowIfNull(finding);
            if (!seenFindings.Add(finding.FindingId))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate finding_id '{finding.FindingId}' in ReplaceFindings.");
            }

            AppendFindingReplace(body, finding);
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void DeleteFindingsForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string sessionIdExpr = GuardedTextExpr("finding_session", "session_id", sessionId);
        ExecuteTransaction(
            $"DELETE FROM finding WHERE finding_id IN (SELECT finding_id FROM finding_session WHERE session_id = {sessionIdExpr});\n");
    }

    public void ReplacePrediction(StoredPrediction prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);

        StringBuilder body = new();
        AppendPredictionReplace(body, prediction);
        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void DeletePrediction(string predictionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predictionId);
        string predictionIdExpr = GuardedTextExpr("prediction", "prediction_id", predictionId);
        ExecuteTransaction($"DELETE FROM prediction WHERE prediction_id = {predictionIdExpr};\n");
    }

    /// <summary>Filesystem seam used for existence checks, directory creation, and rebuild cleanup.</summary>
    protected IRelationalStoreFileSystem FileSystem => fileSystem;

    /// <summary>Ensures the parent directory of <see cref="BaseSqliteRepository.DatabasePath"/> exists via the filesystem seam.</summary>
    protected void EnsureDatabaseDirectory()
    {
        string? directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            fileSystem.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Ensures the parent directory exists before delegating to <see cref="BaseSqliteRepository.ExecuteSqliteCore"/>,
    /// so test subclasses overriding <see cref="ExecuteSqliteCore"/> touch zero filesystem entries.
    /// </summary>
    /// <param name="sql">Complete SQL script passed to <c>sqlite3</c>.</param>
    /// <returns>Standard output captured from <c>sqlite3</c>.</returns>
    protected override string ExecuteSqliteCore(string sql)
    {
        if (!fileSystem.FileExists(DatabasePath))
        {
            EnsureDatabaseDirectory();
        }

        return base.ExecuteSqliteCore(sql);
    }

    private static string ResolveDatabasePath(DashOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CanonDbPath.Resolve(options.DatabasePath, null, null);
    }

    private static string ValidateDatabasePath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return databasePath;
    }

    /// <summary>
    /// Returns the session keys a batch marks dirty: every session key and every record session
    /// key, or <see langword="null"/> (wildcard) when any record carries no session key.
    /// </summary>
    private static SortedSet<string>? DirtySessionKeysOf(CollectedBatch batch)
    {
        SortedSet<string> keys = new(StringComparer.Ordinal);
        foreach (CollectedSession session in batch.Sessions)
        {
            keys.Add(session.SessionKey);
        }

        foreach (CollectedRecord record in batch.Records)
        {
            if (record.SessionKey is null)
            {
                return null;
            }

            keys.Add(record.SessionKey);
        }

        return keys;
    }

    /// <summary>
    /// Appends the statements that advance <c>store_write_marker</c> by one and merge this write
    /// into the dirty projection range, so they commit or roll back with the write itself.
    /// </summary>
    /// <param name="body">Transaction body the statements are appended to, after the write's own statements.</param>
    /// <param name="dirtyKeys">Session keys the write changed; <see langword="null"/> marks the range wildcard.</param>
    /// <remarks>
    /// <para>
    /// Every value is computed in SQL from the rows the transaction sees, never from a read made
    /// in C# before it, so two writers cannot interleave a stale marker or range. With
    /// <c>prev</c> the stored marker (0 when absent), the write is <em>continuing</em> when a range
    /// row exists whose <c>to_seq</c> equals <c>prev</c>:
    /// </para>
    /// <list type="bullet">
    /// <item>Continuing: <c>from_seq</c> is kept and <c>to_seq</c> becomes <c>prev + 1</c>; a stored
    /// wildcard stays wildcard, otherwise the keys are merged.</item>
    /// <item>Not continuing (no range, or a stale one): the range restarts at
    /// (<c>prev</c>, <c>prev + 1</c>) with only this write's keys.</item>
    /// <item>A wildcard write leaves no session rows under the range in either case.</item>
    /// </list>
    /// <para>
    /// A wildcard is a range row with no <c>projection_dirty_session</c> rows, so the statements
    /// read the old range and marker before replacing either, and the marker moves last.
    /// </para>
    /// </remarks>
    private static void AppendWriteMarkerAdvance(StringBuilder body, SortedSet<string>? dirtyKeys)
    {
        const string PrevExpr =
            "(SELECT COALESCE((SELECT records_write_seq FROM store_write_marker WHERE singleton_id = 1), 0))";
        const string ContinuingExpr =
            "EXISTS (SELECT 1 FROM projection_dirty_range WHERE singleton_id = 1 AND to_seq = " + PrevExpr + ")";

        if (dirtyKeys is null)
        {
            body.Append("DELETE FROM projection_dirty_session;\n");
        }
        else
        {
            body.Append("DELETE FROM projection_dirty_session WHERE NOT " + ContinuingExpr + ";\n");
            foreach (string key in dirtyKeys)
            {
                string keyExpr = GuardedNonEmptyTextExpr("projection_dirty_session", "session_id", key);
                body.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT OR IGNORE INTO projection_dirty_session (session_id) SELECT {keyExpr} " +
                    $"WHERE NOT {ContinuingExpr} OR EXISTS (SELECT 1 FROM projection_dirty_session);\n");
            }
        }

        body.Append(
            "INSERT OR REPLACE INTO projection_dirty_range (singleton_id, from_seq, to_seq) SELECT 1, " +
            "CASE WHEN " + ContinuingExpr + " THEN (SELECT from_seq FROM projection_dirty_range WHERE singleton_id = 1) " +
            "ELSE " + PrevExpr + " END, " + PrevExpr + " + 1;\n");
        body.Append(
            "INSERT OR REPLACE INTO store_write_marker (singleton_id, records_write_seq) SELECT 1, " + PrevExpr + " + 1;\n");
    }

    private void ExecuteTransaction(string bodySql)
    {
        Action? rebuildCallback = null;
        lock (writeGate)
        {
            if (!initialized)
            {
                rebuildCallback = Initialize();
                initialized = true;
            }

            StringBuilder script = new(bodySql.Length + 64);
            script.Append("PRAGMA foreign_keys = ON;\n");
            script.Append("BEGIN IMMEDIATE;\n");
            script.Append(bodySql);
            script.Append("COMMIT;\n");
            ExecuteSqlite(script.ToString());
        }

        rebuildCallback?.Invoke();
    }

    private static string GuardedTextExpr(string tableName, string columnName, string? value)
    {
        if (value is null)
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                $"Required TEXT column '{tableName}.{columnName}' cannot be null.");
        }

        if (JsonDocumentText.IsDocument(value))
        {
            throw new DashValidationException(
                JsonTextRefusedCode,
                $"Refused JSON object or array text in '{tableName}.{columnName}'.");
        }

        return TextExpr(value);
    }

    private static string IntSql(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string LongSql(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string BoolSql(bool value) => value ? "1" : "0";

    private static string RealSql(string tableName, string columnName, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                $"REAL column '{tableName}.{columnName}' must be finite.");
        }

        return value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static void RequireMatchedPair(
        bool hasFirst,
        bool hasSecond,
        string tableName,
        string firstColumn,
        string secondColumn)
    {
        if (hasFirst != hasSecond)
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                $"Table '{tableName}' requires both '{firstColumn}' and '{secondColumn}' when either is present.");
        }
    }

    private static void AppendOptionalTextChild(
        StringBuilder sql,
        string tableName,
        string keyColumn,
        string keyExpr,
        string valueColumn,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        string valueExpr = GuardedTextExpr(tableName, valueColumn, value);
        sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO {tableName} ({keyColumn}, {valueColumn}) VALUES ({keyExpr}, {valueExpr});\n");
    }

    private static void AppendOptionalLongChild(
        StringBuilder sql,
        string tableName,
        string keyColumn,
        string keyExpr,
        string valueColumn,
        long? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO {tableName} ({keyColumn}, {valueColumn}) VALUES ({keyExpr}, {LongSql(value.Value)});\n");
    }

    private static void AppendHarnessInsert(StringBuilder sql, HashSet<string> seenHarnesses, string harnessId)
    {
        string harnessExpr = GuardedTextExpr("harness", "harness_id", harnessId);
        if (seenHarnesses.Add(harnessId))
        {
            sql.Append(CultureInfo.InvariantCulture, $"INSERT OR IGNORE INTO harness (harness_id) VALUES ({harnessExpr});\n");
        }
    }

    private static string AppendSourceInsert(StringBuilder sql, CollectedSource source)
    {
        string kindText = FormatSourceKind(source.Kind);
        string harnessExpr = GuardedTextExpr("source", "harness_id", source.HarnessId);
        string kindExpr = GuardedTextExpr("source", "source_kind", kindText);
        string locatorExpr = GuardedTextExpr("source", "locator", source.Locator);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{source.HarnessId}\0{kindText}\0{source.Locator}"));
        string sourceId = $"src-{Convert.ToHexStringLower(digest)[..16]}";
        string sourceIdExpr = TextExpr(sourceId);

        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT OR IGNORE INTO source (source_id, harness_id, source_kind, locator) VALUES ({sourceIdExpr}, {harnessExpr}, {kindExpr}, {locatorExpr});\n");
        return sourceIdExpr;
    }

    private static void AppendSessionUpsert(StringBuilder sql, CollectedSession session)
    {
        string sessionIdExpr = GuardedNonEmptyTextExpr("session", "session_id", session.SessionKey);
        string harnessIdExpr = GuardedTextExpr("session", "harness_id", session.HarnessId);
        string nativeSessionIdExpr = GuardedTextExpr("session", "native_session_id", session.NativeSessionId);

        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO session (session_id, harness_id, native_session_id) VALUES ({sessionIdExpr}, {harnessIdExpr}, {nativeSessionIdExpr}) ");
        sql.Append("ON CONFLICT(session_id) DO UPDATE SET harness_id = excluded.harness_id, native_session_id = excluded.native_session_id;\n");

        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM session_workspace WHERE session_id = {sessionIdExpr};\n");
        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM session_repository WHERE session_id = {sessionIdExpr};\n");
        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM session_branch WHERE session_id = {sessionIdExpr};\n");
        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM session_title WHERE session_id = {sessionIdExpr};\n");
        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM session_model_usage WHERE session_id = {sessionIdExpr};\n");

        if (session.Workspace is not null)
        {
            AppendOptionalTextChild(sql, "session_workspace", "session_id", sessionIdExpr, "working_directory", session.Workspace.WorkingDirectory);
            AppendOptionalTextChild(sql, "session_repository", "session_id", sessionIdExpr, "repo_name", session.Workspace.Repository);
            AppendOptionalTextChild(sql, "session_branch", "session_id", sessionIdExpr, "branch_name", session.Workspace.Branch);
        }

        AppendOptionalTextChild(sql, "session_title", "session_id", sessionIdExpr, "title", session.Title);

        if (session.ReportedUsage is not null)
        {
            foreach (CollectedModelUsage usage in session.ReportedUsage)
            {
                ArgumentNullException.ThrowIfNull(usage);
                string modelExpr = GuardedTextExpr("session_model_usage", "model_id", usage.Model);
                string tokenClassExpr = TextExpr(FormatTokenClass(usage.TokenClass));
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO session_model_usage (session_id, snapshot_position, model_id, token_class, token_count) VALUES ({sessionIdExpr}, {IntSql(usage.SnapshotPosition)}, {modelExpr}, {tokenClassExpr}, {LongSql(usage.Count)});\n");
            }
        }
    }

    private static void AppendRecordInsert(StringBuilder sql, string sourceIdExpr, CollectedRecord record)
    {
        if (record.Signals is null)
        {
            throw new DashValidationException(ConstraintViolatedCode, "CollectedRecord.Signals cannot be null.");
        }

        string spanIdExpr = GuardedTextExpr("record", "span_id", record.RecordId);
        string harnessIdExpr = GuardedTextExpr("record", "harness_id", record.HarnessId);
        string opExpr = TextExpr(FormatRecordOp(record.Op));
        string kindExpr = TextExpr(FormatRecordKind(record.Kind));
        string nameExpr = GuardedTextExpr("record", "name", record.Name);
        string startedExpr = TextExpr(FormatTimestamp(record.StartedAt));
        string statusExpr = TextExpr(FormatRecordStatus(record.Status));

        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO record (span_id, source_id, harness_id, op, kind, name, started_at_utc, status) VALUES ({spanIdExpr}, {sourceIdExpr}, {harnessIdExpr}, {opExpr}, {kindExpr}, {nameExpr}, {startedExpr}, {statusExpr});\n");

        AppendOptionalNonEmptyTextChild(sql, "record_session_link", "span_id", spanIdExpr, "session_key", record.SessionKey);
        AppendOptionalTextChild(sql, "record_parent", "span_id", spanIdExpr, "parent_span_id", record.ParentRecordId);
        AppendOptionalTextChild(sql, "record_trace", "span_id", spanIdExpr, "trace_id", record.TraceId);
        AppendOptionalTextChild(sql, "record_model", "span_id", spanIdExpr, "model_id", record.Model);
        AppendOptionalTextChild(sql, "record_response_id", "span_id", spanIdExpr, "response_id", record.ResponseId);
        AppendOptionalTextChild(sql, "record_request_id", "span_id", spanIdExpr, "request_id", record.RequestId);

        if (record.EndedAt.HasValue)
        {
            string endedExpr = TextExpr(FormatTimestamp(record.EndedAt.Value));
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO record_end (span_id, ended_at_utc) VALUES ({spanIdExpr}, {endedExpr});\n");
        }

        AppendOptionalLongChild(sql, "record_reported_duration", "span_id", spanIdExpr, "duration_ms", record.ReportedDurationMs);

        if (record.Tokens is not null)
        {
            HashSet<TokenClass> seenTokenClasses = [];
            foreach (CollectedTokenCount tokenCount in record.Tokens)
            {
                ArgumentNullException.ThrowIfNull(tokenCount);
                if (!seenTokenClasses.Add(tokenCount.TokenClass))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Duplicate token_class '{tokenCount.TokenClass}' on record '{record.RecordId}'.");
                }

                string tokenClassExpr = TextExpr(FormatTokenClass(tokenCount.TokenClass));
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO record_token_count (span_id, token_class, token_count) VALUES ({spanIdExpr}, {tokenClassExpr}, {LongSql(tokenCount.Count)});\n");
            }
        }

        if (record.ReportedInputTokens.HasValue)
        {
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO record_reported_total (span_id, direction, token_count) VALUES ({spanIdExpr}, {TextExpr("input")}, {LongSql(record.ReportedInputTokens.Value)});\n");
        }

        if (record.ReportedOutputTokens.HasValue)
        {
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO record_reported_total (span_id, direction, token_count) VALUES ({spanIdExpr}, {TextExpr("output")}, {LongSql(record.ReportedOutputTokens.Value)});\n");
        }

        if (record.ReportedCost is not null)
        {
            string amountSql = RealSql("record_reported_cost", "amount", record.ReportedCost.Amount);
            string currencyExpr = GuardedTextExpr("record_reported_cost", "currency", record.ReportedCost.Currency);
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO record_reported_cost (span_id, amount, currency) VALUES ({spanIdExpr}, {amountSql}, {currencyExpr});\n");
        }

        if (record.SkillsInvoked is not null)
        {
            foreach (string skillName in record.SkillsInvoked)
            {
                string skillExpr = GuardedTextExpr("record_skill_invocation", "skill_name", skillName);
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO record_skill_invocation (span_id, skill_name) VALUES ({spanIdExpr}, {skillExpr});\n");
            }
        }

        if (record.SkillsListed is not null)
        {
            for (int position = 0; position < record.SkillsListed.Count; position++)
            {
                string skillExpr = GuardedTextExpr("record_skill_listing", "skill_name", record.SkillsListed[position]);
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO record_skill_listing (span_id, position, skill_name) VALUES ({spanIdExpr}, {IntSql(position)}, {skillExpr});\n");
            }
        }

        AppendOptionalTextChild(sql, "record_agent_instance", "span_id", spanIdExpr, "agent_instance_id", record.AgentInstanceId);
        AppendOptionalTextChild(sql, "record_error", "span_id", spanIdExpr, "message", record.Signals.ErrorMessage);
        AppendOptionalLongChild(sql, "record_edit_failure", "span_id", spanIdExpr, "failure_count", record.Signals.EditFailures);

        if (record.Signals.CorrectionRule is not null)
        {
            string ruleExpr = GuardedTextExpr("record_correction_rule", "correction_rule", record.Signals.CorrectionRule);
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO record_correction (span_id) VALUES ({spanIdExpr});\n");
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO record_correction_rule (span_id, correction_rule) VALUES ({spanIdExpr}, {ruleExpr});\n");
        }

        AppendOptionalLongChild(
            sql,
            "record_declared_context_window",
            "span_id",
            spanIdExpr,
            "declared_context_window",
            record.Signals.DeclaredContextWindow);

        AppendRecordParts(sql, spanIdExpr, record);
        AppendRecordToolDefinitions(sql, spanIdExpr, record);
        AppendRecordToolCall(sql, spanIdExpr, record.Tool);
        AppendRecordAgentInvocation(sql, spanIdExpr, record.Agent);
    }

    private static void AppendRecordParts(StringBuilder sql, string spanIdExpr, CollectedRecord record)
    {
        if (record.Parts is null)
        {
            return;
        }

        HashSet<int> seenPositions = [];
        foreach (CollectedContentPart part in record.Parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            if (!seenPositions.Add(part.Position))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate content_part position {IntSql(part.Position)} on record '{record.RecordId}'.");
            }

            if ((part.PlainText is not null) == (part.Withheld is not null))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"content_part at position {IntSql(part.Position)} must specify exactly one of PlainText or Withheld.");
            }

            string posSql = IntSql(part.Position);
            string bucketExpr = TextExpr(FormatContentBucket(part.Bucket));
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO content_part (span_id, position, bucket) VALUES ({spanIdExpr}, {posSql}, {bucketExpr});\n");

            if (part.PlainText is not null)
            {
                string partTextExpr = GuardedTextExpr("content_part_text", "part_text", part.PlainText);
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO content_part_text (span_id, position, part_text) VALUES ({spanIdExpr}, {posSql}, {partTextExpr});\n");
            }
            else if (part.Withheld is not null)
            {
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO content_part_withheld (span_id, position, utf8_bytes, characters, measured_tokens) VALUES ({spanIdExpr}, {posSql}, {LongSql(part.Withheld.Utf8Bytes)}, {LongSql(part.Withheld.Characters)}, {LongSql(part.Withheld.MeasuredTokens)});\n");
                if (part.Withheld.Tokenizer is string tokenizer)
                {
                    string tokenizerExpr = GuardedTextExpr("content_part_withheld_tokenizer", "tokenizer", tokenizer);
                    sql.Append(
                        CultureInfo.InvariantCulture,
                        $"INSERT INTO content_part_withheld_tokenizer (span_id, position, tokenizer, is_model_own) VALUES ({spanIdExpr}, {posSql}, {tokenizerExpr}, {(part.Withheld.TokenizerIsModelOwn ? "1" : "0")});\n");
                }
            }

            if (part.ReportedTokens.HasValue)
            {
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO content_part_reported_tokens (span_id, position, token_count) VALUES ({spanIdExpr}, {posSql}, {LongSql(part.ReportedTokens.Value)});\n");
            }

            if (part.Server is not null)
            {
                string serverExpr = GuardedTextExpr("content_part_server", "server_name", part.Server);
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO content_part_server (span_id, position, server_name) VALUES ({spanIdExpr}, {posSql}, {serverExpr});\n");
            }

            if (part.Role is not null)
            {
                string roleExpr = GuardedTextExpr("content_part_role", "role_name", part.Role);
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO content_part_role (span_id, position, role_name) VALUES ({spanIdExpr}, {posSql}, {roleExpr});\n");
            }

            if (part.Resident.HasValue)
            {
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO content_part_residency (span_id, position, is_resident) VALUES ({spanIdExpr}, {posSql}, {BoolSql(part.Resident.Value)});\n");
            }

            if (part.OriginalUtf8BytesWhenTruncated.HasValue)
            {
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO content_part_truncation (span_id, position, original_utf8_bytes) VALUES ({spanIdExpr}, {posSql}, {LongSql(part.OriginalUtf8BytesWhenTruncated.Value)});\n");
            }
        }
    }

    private static void AppendRecordToolDefinitions(StringBuilder sql, string spanIdExpr, CollectedRecord record)
    {
        if (record.ToolDefinitions is null)
        {
            return;
        }

        HashSet<int> seenPositions = [];
        foreach (CollectedToolDefinition definition in record.ToolDefinitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!seenPositions.Add(definition.Position))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate tool_definition position {IntSql(definition.Position)} on record '{record.RecordId}'.");
            }

            string posSql = IntSql(definition.Position);
            string toolNameExpr = GuardedTextExpr("tool_definition", "tool_name", definition.ToolName);
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO tool_definition (span_id, position, tool_name, schema_tokens) VALUES ({spanIdExpr}, {posSql}, {toolNameExpr}, {LongSql(definition.SchemaTokens)});\n");

            if (definition.Tokenizer is string tokenizer)
            {
                string tokenizerExpr = GuardedTextExpr("tool_definition_tokenizer", "tokenizer", tokenizer);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO tool_definition_tokenizer (span_id, position, tokenizer, is_model_own) VALUES ({spanIdExpr}, {posSql}, {tokenizerExpr}, {BoolSql(definition.TokenizerIsModelOwn)});\n");
            }

            if (definition.Server is not null)
            {
                string serverExpr = GuardedTextExpr("tool_definition_server", "server_name", definition.Server);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO tool_definition_server (span_id, position, server_name) VALUES ({spanIdExpr}, {posSql}, {serverExpr});\n");
            }
        }
    }

    private static void AppendRecordToolCall(StringBuilder sql, string spanIdExpr, CollectedToolCall? tool)
    {
        if (tool is null)
        {
            return;
        }

        string callIdExpr = GuardedTextExpr("tool_call", "call_id", tool.CallId);
        string toolNameExpr = GuardedTextExpr("tool_call", "tool_name", tool.ToolName);
        sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO tool_call (span_id, call_id, tool_name) VALUES ({spanIdExpr}, {callIdExpr}, {toolNameExpr});\n");

        RequireMatchedPair(
            tool.McpServerName is not null,
            tool.McpToolName is not null,
            "tool_call_mcp",
            "server_name",
            "server_tool_name");

        if (tool.McpServerName is not null && tool.McpToolName is not null)
        {
            string serverExpr = GuardedTextExpr("tool_call_mcp", "server_name", tool.McpServerName);
            string serverToolExpr = GuardedTextExpr("tool_call_mcp", "server_tool_name", tool.McpToolName);
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO tool_call_mcp (span_id, server_name, server_tool_name) VALUES ({spanIdExpr}, {serverExpr}, {serverToolExpr});\n");
        }

        AppendToolContent(sql, spanIdExpr, tool.Input, "tool_call_input");
        AppendToolContent(sql, spanIdExpr, tool.Result, "tool_call_result");

        if (tool.Succeeded.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO tool_call_outcome (span_id, succeeded) VALUES ({spanIdExpr}, {BoolSql(tool.Succeeded.Value)});\n");
        }

        if (tool.Error is not null)
        {
            string msgExpr = GuardedTextExpr("tool_call_error", "message", tool.Error.Message);
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO tool_call_error (span_id, message) VALUES ({spanIdExpr}, {msgExpr});\n");
            AppendOptionalTextChild(sql, "tool_call_error_code", "span_id", spanIdExpr, "code", tool.Error.Code);
        }

        AppendOptionalLongChild(sql, "tool_call_exit_code", "span_id", spanIdExpr, "exit_code", tool.ExitCode);
    }

    private static void AppendToolContent(
        StringBuilder sql,
        string spanIdExpr,
        CollectedToolContent? content,
        string tablePrefix)
    {
        if (content is null)
        {
            return;
        }

        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO {tablePrefix}_size (span_id, utf8_bytes, characters) VALUES ({spanIdExpr}, {LongSql(content.Utf8Bytes)}, {LongSql(content.Characters)});\n");

        if (string.Equals(tablePrefix, "tool_call_input", StringComparison.Ordinal))
        {
            AppendOptionalTextChild(sql, "tool_call_input_fingerprint", "span_id", spanIdExpr, "sha256", content.Sha256Fingerprint);
        }

        AppendOptionalTextChild(sql, $"{tablePrefix}_text", "span_id", spanIdExpr, "text", content.PlainText);
        AppendOptionalLongChild(sql, $"{tablePrefix}_truncation", "span_id", spanIdExpr, "original_utf8_bytes", content.OriginalUtf8BytesWhenTruncated);
    }

    private static void AppendRecordAgentInvocation(StringBuilder sql, string spanIdExpr, CollectedAgentInvocation? agent)
    {
        if (agent is null)
        {
            return;
        }

        string agentNameExpr = GuardedTextExpr("agent_invocation", "agent_name", agent.AgentName);
        sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO agent_invocation (span_id, agent_name) VALUES ({spanIdExpr}, {agentNameExpr});\n");

        AppendOptionalTextChild(sql, "agent_invocation_spawn", "span_id", spanIdExpr, "spawning_record_id", agent.SpawningCallId);

        if (agent.Succeeded.HasValue)
        {
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO agent_invocation_outcome (span_id, succeeded) VALUES ({spanIdExpr}, {BoolSql(agent.Succeeded.Value)});\n");
        }

        AppendOptionalTextChild(sql, "agent_invocation_error", "span_id", spanIdExpr, "message", agent.Error);
    }

    private static void AppendProblemUpsert(StringBuilder sql, CollectedProblem problem)
    {
        string problemKeyExpr = GuardedTextExpr("ingest_problem", "problem_key", problem.ProblemKey);
        string severityExpr = GuardedTextExpr("ingest_problem", "severity", problem.Severity);
        string codeExpr = GuardedTextExpr("ingest_problem", "code", problem.Code);
        string messageExpr = GuardedTextExpr("ingest_problem", "message", problem.Message);

        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO ingest_problem (problem_key, severity, code, message) VALUES ({problemKeyExpr}, {severityExpr}, {codeExpr}, {messageExpr}) ");
        sql.Append("ON CONFLICT(problem_key) DO UPDATE SET severity = excluded.severity, code = excluded.code, message = excluded.message;\n");

        foreach (string childTable in ProblemChildTables)
        {
            sql.Append(
                CultureInfo.InvariantCulture,
                $"DELETE FROM {childTable} WHERE problem_id = (SELECT problem_id FROM ingest_problem WHERE problem_key = {problemKeyExpr});\n");
        }

        AppendProblemOptionalChild(sql, problemKeyExpr, "ingest_problem_span", "span_id", problem.SpanId);
        AppendProblemOptionalChild(sql, problemKeyExpr, "ingest_problem_location", "location", problem.Location);
        AppendProblemOptionalChild(sql, problemKeyExpr, "ingest_problem_session", "session_id", problem.SessionKey);
        AppendProblemOptionalChild(sql, problemKeyExpr, "ingest_problem_harness", "harness_id", problem.HarnessId);
        AppendProblemOptionalChild(sql, problemKeyExpr, "ingest_problem_timestamp", "timestamp_utc", problem.TimestampUtc);
        AppendProblemOptionalChild(sql, problemKeyExpr, "ingest_problem_stack", "stack", problem.Stack);
    }

    private static void AppendProblemOptionalChild(
        StringBuilder sql,
        string problemKeyExpr,
        string tableName,
        string columnName,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        string valueExpr = GuardedTextExpr(tableName, columnName, value);
        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO {tableName} (problem_id, {columnName}) SELECT problem_id, {valueExpr} FROM ingest_problem WHERE problem_key = {problemKeyExpr};\n");
    }

    private static void AppendSessionProjectionReplace(StringBuilder sql, StoredSessionProjection projection)
    {
        if (projection.Context is not null && projection.ContextUnmeasurableReason is not null)
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                "StoredSessionProjection cannot supply both Context and ContextUnmeasurableReason.");
        }

        if (projection.Schema is not null && projection.SchemaUnmeasurableReason is not null)
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                "StoredSessionProjection cannot supply both Schema and SchemaUnmeasurableReason.");
        }

        string sessionIdExpr = GuardedTextExpr("session_projection", "session_id", projection.SessionId);
        string labelExpr = GuardedTextExpr("session_projection", "label", projection.Label);
        string startedExpr = GuardedTextExpr("session_projection", "started_at_utc", projection.StartedAtUtc);
        string endedExpr = GuardedTextExpr("session_projection", "ended_at_utc", projection.EndedAtUtc);

        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM session_projection WHERE session_id = {sessionIdExpr};\n");
        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO session_projection (session_id, label, is_subagent, span_count, started_at_utc, ended_at_utc) VALUES ({sessionIdExpr}, {labelExpr}, {BoolSql(projection.IsSubagent)}, {IntSql(projection.SpanCount)}, {startedExpr}, {endedExpr});\n");

        AppendOptionalTextChild(sql, "session_projection_parent", "session_id", sessionIdExpr, "parent_session_id", projection.ParentSessionId);
        AppendOptionalTextChild(sql, "session_projection_agent", "session_id", sessionIdExpr, "agent_name", projection.AgentName);

        if (projection.IncludedInParent)
        {
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO session_projection_included_in_parent (session_id) VALUES ({sessionIdExpr});\n");
        }

        if (projection.RequestSpanIds is not null)
        {
            for (int i = 0; i < projection.RequestSpanIds.Count; i++)
            {
                string requestSpanExpr = GuardedTextExpr("session_request", "request_span_id", projection.RequestSpanIds[i]);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO session_request (session_id, position, request_span_id) VALUES ({sessionIdExpr}, {IntSql(i)}, {requestSpanExpr});\n");
            }
        }

        if (projection.Turns is not null)
        {
            foreach (StoredSessionTurn turn in projection.Turns)
            {
                ArgumentNullException.ThrowIfNull(turn);
                if (!string.Equals(turn.SessionId, projection.SessionId, StringComparison.Ordinal))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"StoredSessionTurn.SessionId '{turn.SessionId}' does not match '{projection.SessionId}'.");
                }

                string spanIdExpr = GuardedTextExpr("session_turn", "span_id", turn.SpanId);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO session_turn (session_id, position, span_id) VALUES ({sessionIdExpr}, {IntSql(turn.Position)}, {spanIdExpr});\n");
            }
        }

        if (projection.Context is not null)
        {
            AppendSessionContext(sql, sessionIdExpr, projection.Context);
        }
        else
        {
            AppendOptionalTextChild(sql, "session_context_unmeasurable", "session_id", sessionIdExpr, "reason", projection.ContextUnmeasurableReason);
        }

        if (projection.Schema is not null)
        {
            AppendSessionSchema(sql, sessionIdExpr, projection.Schema);
        }
        else
        {
            AppendOptionalTextChild(sql, "session_schema_unmeasurable", "session_id", sessionIdExpr, "reason", projection.SchemaUnmeasurableReason);
        }

        if (projection.TimelineSubagentSpanIds is not null)
        {
            foreach (string spanId in projection.TimelineSubagentSpanIds)
            {
                string spanIdExpr = GuardedTextExpr("session_timeline_subagent", "span_id", spanId);
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO session_timeline_subagent (session_id, span_id) VALUES ({sessionIdExpr}, {spanIdExpr});\n");
            }
        }

        if (projection.TimelineAuxiliarySpanIds is not null)
        {
            foreach (string spanId in projection.TimelineAuxiliarySpanIds)
            {
                string spanIdExpr = GuardedTextExpr("session_timeline_auxiliary", "span_id", spanId);
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO session_timeline_auxiliary (session_id, span_id) VALUES ({sessionIdExpr}, {spanIdExpr});\n");
            }
        }
    }

    private static void AppendSessionContext(StringBuilder sql, string sessionIdExpr, StoredSessionContext context)
    {
        string sourceExpr = GuardedTextExpr("session_context", "context_limit_source", context.ContextLimitSource);
        string freshJumpSql = RealSql("session_context", "fresh_jump_factor", context.FreshJumpFactor);
        string accumSql = RealSql("session_context", "accumulation_rate", context.AccumulationRate);

        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO session_context (session_id, context_limit, context_limit_source, residual_total, fresh_jump_factor, accumulation_rate) VALUES ({sessionIdExpr}, {LongSql(context.ContextLimit)}, {sourceExpr}, {LongSql(context.ResidualTotal)}, {freshJumpSql}, {accumSql});\n");

        if (context.ObservedBuckets is not null)
        {
            foreach (string bucket in context.ObservedBuckets)
            {
                string bucketExpr = GuardedTextExpr("session_context_observed_part", "bucket", bucket);
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO session_context_observed_part (session_id, bucket) VALUES ({sessionIdExpr}, {bucketExpr});\n");
            }
        }

        if (context.DerivedCounts is not null)
        {
            foreach (StoredSessionContextDerivedCount counter in context.DerivedCounts)
            {
                ArgumentNullException.ThrowIfNull(counter);
                string counterExpr = GuardedTextExpr("session_context_derived_count", "counter", counter.Counter);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO session_context_derived_count (session_id, counter, count) VALUES ({sessionIdExpr}, {counterExpr}, {LongSql(counter.Count)});\n");
            }
        }

        if (context.FlaggedTurnIndexes is not null)
        {
            foreach (int turnIndex in context.FlaggedTurnIndexes)
            {
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO session_context_flagged_turn (session_id, turn_index) VALUES ({sessionIdExpr}, {IntSql(turnIndex)});\n");
            }
        }

        if (context.Turns is not null)
        {
            foreach (StoredSessionContextTurn turn in context.Turns)
            {
                ArgumentNullException.ThrowIfNull(turn);
                AppendSessionContextTurn(sql, sessionIdExpr, turn);
            }
        }
    }

    private static void AppendSessionContextTurn(StringBuilder sql, string sessionIdExpr, StoredSessionContextTurn turn)
    {
        string posSql = IntSql(turn.Position);
        string attributionExpr = GuardedTextExpr("session_context_turn", "residual_attribution", turn.ResidualAttribution);

        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO session_context_turn (session_id, position, input_tokens, bucketed_tokens, residual_tokens, residual_attribution, builtin_tool_definition_tokens, fresh_input) " +
            $"VALUES ({sessionIdExpr}, {posSql}, {LongSql(turn.InputTokens)}, {LongSql(turn.BucketedTokens)}, {LongSql(turn.ResidualTokens)}, {attributionExpr}, {LongSql(turn.BuiltinToolDefinitionTokens)}, {LongSql(turn.FreshInput)});\n");

        RequireMatchedPair(turn.Headroom.HasValue, turn.Pressure.HasValue, "session_context_turn_headroom", "headroom", "pressure");
        if (turn.Headroom.HasValue && turn.Pressure.HasValue)
        {
            string pressureSql = RealSql("session_context_turn_headroom", "pressure", turn.Pressure.Value);
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO session_context_turn_headroom (session_id, position, headroom, pressure) VALUES ({sessionIdExpr}, {posSql}, {LongSql(turn.Headroom.Value)}, {pressureSql});\n");
        }

        if (turn.AccumulationRate.HasValue)
        {
            string rateSql = RealSql("session_context_turn_accumulation", "rate", turn.AccumulationRate.Value);
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO session_context_turn_accumulation (session_id, position, rate) VALUES ({sessionIdExpr}, {posSql}, {rateSql});\n");
        }

        if (turn.Buckets is not null)
        {
            foreach (StoredTurnBucketTokens bucket in turn.Buckets)
            {
                ArgumentNullException.ThrowIfNull(bucket);
                string bucketExpr = GuardedTextExpr("session_context_turn_bucket", "bucket", bucket.Bucket);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO session_context_turn_bucket (session_id, position, bucket, tokens) VALUES ({sessionIdExpr}, {posSql}, {bucketExpr}, {LongSql(bucket.Tokens)});\n");
            }
        }

        if (turn.ServerDefinitions is not null)
        {
            foreach (StoredTurnServerDefinitionTokens server in turn.ServerDefinitions)
            {
                ArgumentNullException.ThrowIfNull(server);
                string serverExpr = GuardedTextExpr("session_context_turn_server_definitions", "server", server.Server);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO session_context_turn_server_definitions (session_id, position, server, tokens) VALUES ({sessionIdExpr}, {posSql}, {serverExpr}, {LongSql(server.Tokens)});\n");
            }
        }

        foreach (TokenizerLabel label in turn.Tokenizers)
        {
            ArgumentNullException.ThrowIfNull(label);
            string tokenizerExpr = GuardedTextExpr("session_context_turn_tokenizer", "tokenizer", label.Tokenizer);
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO session_context_turn_tokenizer (session_id, position, tokenizer, is_model_own) VALUES ({sessionIdExpr}, {posSql}, {tokenizerExpr}, {BoolSql(label.IsModelOwn)});\n");
        }

        RequireMatchedPair(
            turn.StrippedBlockCount.HasValue,
            turn.StrippedTokens.HasValue,
            "session_context_turn_stripped_instructions",
            "block_count",
            "tokens");
        if (turn.StrippedBlockCount.HasValue && turn.StrippedTokens.HasValue)
        {
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO session_context_turn_stripped_instructions (session_id, position, block_count, tokens) VALUES ({sessionIdExpr}, {posSql}, {LongSql(turn.StrippedBlockCount.Value)}, {LongSql(turn.StrippedTokens.Value)});\n");
        }

        RequireMatchedPair(
            turn.PreviousFreshInput.HasValue,
            turn.FreshJumpFactor.HasValue,
            "session_context_turn_fresh_jump",
            "previous_fresh_input",
            "factor");
        if (turn.PreviousFreshInput.HasValue && turn.FreshJumpFactor.HasValue)
        {
            string factorSql = RealSql("session_context_turn_fresh_jump", "factor", turn.FreshJumpFactor.Value);
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO session_context_turn_fresh_jump (session_id, position, previous_fresh_input, factor) VALUES ({sessionIdExpr}, {posSql}, {LongSql(turn.PreviousFreshInput.Value)}, {factorSql});\n");
        }

        if (turn.NonResidentTokens.HasValue)
        {
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO session_context_turn_non_resident (session_id, position, tokens) VALUES ({sessionIdExpr}, {posSql}, {LongSql(turn.NonResidentTokens.Value)});\n");
        }
    }

    private static void AppendSessionSchema(StringBuilder sql, string sessionIdExpr, StoredSessionSchema schema)
    {
        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO session_schema (session_id, invocation_count, turn_count) VALUES ({sessionIdExpr}, {IntSql(schema.InvocationCount)}, {IntSql(schema.TurnCount)});\n");

        if (schema.RankedTools is not null)
        {
            foreach (StoredSchemaRankedTool tool in schema.RankedTools)
            {
                ArgumentNullException.ThrowIfNull(tool);
                string posSql = IntSql(tool.Position);
                string toolNameExpr = GuardedTextExpr("session_schema_ranked_tool", "tool_name", tool.ToolName);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO session_schema_ranked_tool (session_id, position, tool_name, cost_tokens, invoked) VALUES ({sessionIdExpr}, {posSql}, {toolNameExpr}, {LongSql(tool.CostTokens)}, {BoolSql(tool.Invoked)});\n");

                if (tool.ServerName is not null)
                {
                    string serverExpr = GuardedTextExpr("session_schema_ranked_tool_server", "server_name", tool.ServerName);
                    sql.Append(
                        CultureInfo.InvariantCulture,
                        $"INSERT INTO session_schema_ranked_tool_server (session_id, position, server_name) VALUES ({sessionIdExpr}, {posSql}, {serverExpr});\n");
                }
            }
        }

        if (schema.Servers is not null)
        {
            foreach (StoredSchemaServerTokens server in schema.Servers)
            {
                ArgumentNullException.ThrowIfNull(server);
                string serverExpr = GuardedTextExpr("session_schema_server", "server_name", server.ServerName);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO session_schema_server (session_id, server_name, tokens) VALUES ({sessionIdExpr}, {serverExpr}, {LongSql(server.Tokens)});\n");
            }
        }

        AppendOptionalLongChild(sql, "session_schema_unused", "session_id", sessionIdExpr, "token_residencies", schema.UnusedTokenResidencies);
    }

    private static void AppendRunUpsert(StringBuilder sql, StoredRun run)
    {
        RequireMatchedPair(run.StartedAtUtc is not null, run.EndedAtUtc is not null, "run_bounds", "started_at_utc", "ended_at_utc");

        string runIdExpr = GuardedTextExpr("run", "run_id", run.RunId);
        string harnessIdExpr = GuardedTextExpr("run", "harness_id", run.HarnessId);
        string basisExpr = GuardedTextExpr("run", "grouping_basis", run.GroupingBasis);
        string ruleExpr = GuardedTextExpr("run", "grouping_rule", run.GroupingRule);

        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO run (run_id, harness_id, grouping_basis, grouping_rule, execution_count) VALUES ({runIdExpr}, {harnessIdExpr}, {basisExpr}, {ruleExpr}, {IntSql(run.ExecutionCount)}) ");
        sql.Append("ON CONFLICT(run_id) DO UPDATE SET harness_id = excluded.harness_id, grouping_basis = excluded.grouping_basis, grouping_rule = excluded.grouping_rule, execution_count = excluded.execution_count;\n");

        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM run_label WHERE run_id = {runIdExpr};\n");
        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM run_working_directory WHERE run_id = {runIdExpr};\n");
        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM run_bounds WHERE run_id = {runIdExpr};\n");

        AppendOptionalTextChild(sql, "run_label", "run_id", runIdExpr, "label", run.Label);
        AppendOptionalTextChild(sql, "run_working_directory", "run_id", runIdExpr, "working_directory", run.WorkingDirectory);

        if (run.StartedAtUtc is not null && run.EndedAtUtc is not null)
        {
            string startedExpr = GuardedTextExpr("run_bounds", "started_at_utc", run.StartedAtUtc);
            string endedExpr = GuardedTextExpr("run_bounds", "ended_at_utc", run.EndedAtUtc);
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO run_bounds (run_id, started_at_utc, ended_at_utc) VALUES ({runIdExpr}, {startedExpr}, {endedExpr});\n");
        }
    }

    private static void AppendHarnessRollupReplace(StringBuilder sql, StoredHarnessRollup rollup)
    {
        string harnessIdExpr = GuardedTextExpr("harness_rollup", "harness_id", rollup.HarnessId);

        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM harness_rollup WHERE harness_id = {harnessIdExpr};\n");
        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO harness_rollup (harness_id, session_count, run_count, execution_count, unknown_window_sessions, window_sessions_total) " +
            $"VALUES ({harnessIdExpr}, {IntSql(rollup.SessionCount)}, {IntSql(rollup.RunCount)}, {IntSql(rollup.ExecutionCount)}, {IntSql(rollup.UnknownWindowSessions)}, {IntSql(rollup.WindowSessionsTotal)});\n");

        if (rollup.Metrics is not null)
        {
            foreach (StoredHarnessRollupMetric metric in rollup.Metrics)
            {
                ArgumentNullException.ThrowIfNull(metric);
                string metricExpr = GuardedTextExpr("harness_rollup_metric", "metric", metric.Metric);
                string valueSql = RealSql("harness_rollup_metric", "value", metric.Value);
                string basisExpr = GuardedTextExpr("harness_rollup_metric", "basis", metric.Basis);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO harness_rollup_metric (harness_id, metric, value, basis) VALUES ({harnessIdExpr}, {metricExpr}, {valueSql}, {basisExpr});\n");
            }
        }
    }

    private static void AppendFindingReplace(StringBuilder sql, StoredFinding finding)
    {
        string findingIdExpr = GuardedTextExpr("finding", "finding_id", finding.FindingId);
        string detectorIdExpr = GuardedTextExpr("finding", "detector_id", finding.DetectorId);
        string titleExpr = GuardedTextExpr("finding", "title", finding.Title);
        string mechanismExpr = GuardedTextExpr("finding", "mechanism", finding.Mechanism);
        string confidenceExpr = GuardedTextExpr("finding", "confidence", finding.Confidence);
        string recommendationExpr = GuardedTextExpr("finding", "recommendation", finding.Recommendation);
        string caveatExpr = GuardedTextExpr("finding", "outcome_risk_caveat", finding.OutcomeRiskCaveat);
        string rankScoreSql = RealSql("finding", "rank_score", finding.RankScore);

        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM finding WHERE finding_id = {findingIdExpr};\n");
        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO finding (finding_id, detector_id, title, mechanism, confidence, recommendation, outcome_risk_caveat, rank_score) " +
            $"VALUES ({findingIdExpr}, {detectorIdExpr}, {titleExpr}, {mechanismExpr}, {confidenceExpr}, {recommendationExpr}, {caveatExpr}, {rankScoreSql});\n");

        AppendOptionalLongChild(sql, "finding_estimated_waste", "finding_id", findingIdExpr, "tokens", finding.EstimatedWasteTokens);

        if (finding.ErrorBar is not null)
        {
            string lowerSql = RealSql("finding_error_bar", "lower", finding.ErrorBar.Lower);
            string upperSql = RealSql("finding_error_bar", "upper", finding.ErrorBar.Upper);
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO finding_error_bar (finding_id, lower, upper) VALUES ({findingIdExpr}, {lowerSql}, {upperSql});\n");
        }

        if (finding.EvidenceLinks is not null)
        {
            for (int i = 0; i < finding.EvidenceLinks.Count; i++)
            {
                FindingEvidenceLink link = finding.EvidenceLinks[i];
                ArgumentNullException.ThrowIfNull(link);
                string spanIdExpr = GuardedTextExpr("finding_evidence_link", "span_id", link.SpanId);
                string descriptionExpr = GuardedTextExpr("finding_evidence_link", "description", link.Description);
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO finding_evidence_link (finding_id, position, span_id, turn_index, description) VALUES ({findingIdExpr}, {IntSql(i)}, {spanIdExpr}, {IntSql(link.TurnIndex)}, {descriptionExpr});\n");
            }
        }

        AppendOptionalTextChild(sql, "finding_run", "finding_id", findingIdExpr, "run_id", finding.RunId);
        AppendOptionalTextChild(sql, "finding_session", "finding_id", findingIdExpr, "session_id", finding.SessionId);
        AppendOptionalTextChild(sql, "finding_measurement_class", "finding_id", findingIdExpr, "measurement_class", finding.MeasurementClass);
        AppendOptionalTextChild(sql, "finding_confidence_basis", "finding_id", findingIdExpr, "confidence_basis", finding.ConfidenceBasis);
        AppendOptionalTextChild(sql, "finding_what_would_raise_it", "finding_id", findingIdExpr, "text", finding.WhatWouldRaiseIt);

        if (finding.CompactionHazard is not null)
        {
            string sourceExpr = GuardedTextExpr("finding_compaction_hazard", "context_limit_source", finding.CompactionHazard.ContextLimitSource);
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO finding_compaction_hazard (finding_id, context_limit, context_limit_source) VALUES ({findingIdExpr}, {LongSql(finding.CompactionHazard.ContextLimit)}, {sourceExpr});\n");
        }
    }

    private static void AppendPredictionReplace(StringBuilder sql, StoredPrediction prediction)
    {
        string predictionIdExpr = GuardedTextExpr("prediction", "prediction_id", prediction.PredictionId);
        string findingIdExpr = GuardedTextExpr("prediction", "finding_id", prediction.FindingId);
        string runIdExpr = GuardedTextExpr("prediction", "run_id", prediction.RunId);
        string confidenceSql = RealSql("prediction", "confidence", prediction.Confidence);
        string tierExpr = GuardedTextExpr("prediction", "confidence_tier", prediction.ConfidenceTier);
        string createdExpr = GuardedTextExpr("prediction", "created_at_utc", prediction.CreatedAtUtc);

        sql.Append(CultureInfo.InvariantCulture, $"DELETE FROM prediction WHERE prediction_id = {predictionIdExpr};\n");
        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO prediction (prediction_id, finding_id, run_id, predicted_waste_tokens, confidence, confidence_tier, created_at_utc) " +
            $"VALUES ({predictionIdExpr}, {findingIdExpr}, {runIdExpr}, {LongSql(prediction.PredictedWasteTokens)}, {confidenceSql}, {tierExpr}, {createdExpr});\n");

        AppendOptionalTextChild(sql, "prediction_comparison", "prediction_id", predictionIdExpr, "comparison_run_id", prediction.ComparisonRunId);
        AppendOptionalLongChild(sql, "prediction_observed_delta", "prediction_id", predictionIdExpr, "tokens", prediction.ObservedDeltaTokens);

        if (prediction.CalibrationScore.HasValue)
        {
            string scoreSql = RealSql("prediction_calibration", "score", prediction.CalibrationScore.Value);
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO prediction_calibration (prediction_id, score) VALUES ({predictionIdExpr}, {scoreSql});\n");
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatSourceKind(SourceKind kind) => kind switch
    {
        SourceKind.File => "file",
        SourceKind.Otlp => "otlp",
        _ => throw new DashValidationException(ConstraintViolatedCode, $"Unsupported SourceKind '{kind}'."),
    };

    private static string FormatRecordOp(RecordOp op) => op switch
    {
        RecordOp.LlmInvoke => "llm.invoke",
        RecordOp.ToolInvoke => "tool.invoke",
        RecordOp.AgentInvoke => "agent.invoke",
        RecordOp.Unspecified => "unspecified",
        _ => throw new DashValidationException(ConstraintViolatedCode, $"Unsupported RecordOp '{op}'."),
    };

    private static string FormatRecordKind(RecordKind kind) => kind switch
    {
        RecordKind.Unspecified => "unspecified",
        RecordKind.Internal => "internal",
        RecordKind.Server => "server",
        RecordKind.Client => "client",
        RecordKind.Producer => "producer",
        RecordKind.Consumer => "consumer",
        _ => throw new DashValidationException(ConstraintViolatedCode, $"Unsupported RecordKind '{kind}'."),
    };

    private static string FormatRecordStatus(RecordStatus status) => status switch
    {
        RecordStatus.Unset => "unset",
        RecordStatus.Ok => "ok",
        RecordStatus.Error => "error",
        RecordStatus.Unspecified => "unspecified",
        _ => throw new DashValidationException(ConstraintViolatedCode, $"Unsupported RecordStatus '{status}'."),
    };

    private static string FormatTokenClass(TokenClass tokenClass) => tokenClass switch
    {
        TokenClass.Input => "input",
        TokenClass.CacheRead => "cache_read",
        TokenClass.CacheWrite => "cache_write",
        TokenClass.Output => "output",
        TokenClass.Reasoning => "reasoning",
        TokenClass.Total => "total",
        _ => throw new DashValidationException(ConstraintViolatedCode, $"Unsupported TokenClass '{tokenClass}'."),
    };

    private static string FormatContentBucket(ContentBucket bucket) => bucket switch
    {
        ContentBucket.SystemPrompt => "system_prompt",
        ContentBucket.ToolDefinitions => "tool_definitions",
        ContentBucket.ToolDefinition => "tool_definition",
        ContentBucket.InstructionContext => "instruction_context",
        ContentBucket.ConversationHistory => "conversation_history",
        ContentBucket.ToolResultContent => "tool_result_content",
        ContentBucket.UserPrompt => "user_prompt",
        ContentBucket.UserMessage => "user_message",
        ContentBucket.Reasoning => "reasoning",
        _ => throw new DashValidationException(ConstraintViolatedCode, $"Unsupported ContentBucket '{bucket}'."),
    };
}
