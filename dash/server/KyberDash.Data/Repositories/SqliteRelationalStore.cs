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
    /// Generation 7: records read back carry their stored skill listing, and hydration exposes the
    /// listing and the stored skill invocations to the inactive-skill-reference detector, so every
    /// Claude Code session's findings are rebuilt.
    /// Generation 8: Claude Code v2 transcript hydration adds streaming <c>message.id</c> coalescence,
    /// <c>tool_use</c> content parts, tail-only overflow measurement, <c>claude:compact_boundary</c>
    /// hydration, and <c>#agent-</c> subagent isolation, so projections built under generation 7 and
    /// earlier rebuild on upgrade.
    /// </remarks>
    public const int DetectorVersion = 8;

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
    public void WriteBatch(CollectedBatch batch) => WriteBatchCore(batch, replaceSource: false);

    /// <inheritdoc />
    public void ReplaceSource(CollectedBatch batch) => WriteBatchCore(batch, replaceSource: true);

    private void WriteBatchCore(CollectedBatch batch, bool replaceSource)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Source);
        ArgumentNullException.ThrowIfNull(batch.Sessions);
        ArgumentNullException.ThrowIfNull(batch.Records);
        ArgumentNullException.ThrowIfNull(batch.Problems);

        SqlBatch sqlBatch = new();

        HashSet<string> harnesses = new(StringComparer.Ordinal);
        AppendHarnessInsert(sqlBatch, harnesses, batch.Source.HarnessId);
        foreach (CollectedSession session in batch.Sessions)
        {
            ArgumentNullException.ThrowIfNull(session);
            AppendHarnessInsert(sqlBatch, harnesses, session.HarnessId);
        }

        foreach (CollectedRecord record in batch.Records)
        {
            ArgumentNullException.ThrowIfNull(record);
            AppendHarnessInsert(sqlBatch, harnesses, record.HarnessId);
        }

        if (replaceSource)
        {
            AppendSourceDelete(sqlBatch, SourceIdOf(batch.Source));
        }

        string sourceId = AppendSourceInsert(sqlBatch, batch.Source);

        HashSet<string> seenSessions = new(StringComparer.Ordinal);
        foreach (CollectedSession session in batch.Sessions)
        {
            if (!seenSessions.Add(session.SessionKey))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate session_id '{session.SessionKey}' in CollectedBatch.Sessions.");
            }

            AppendSessionUpsert(sqlBatch, session);
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

            AppendRecordInsert(sqlBatch, sourceId, record);
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

            AppendProblemUpsert(sqlBatch, problem);
        }

        if (replaceSource)
        {
            // As DeleteSource: the cascade removes records of sessions this batch may not name.
            AppendWriteMarkerAdvance(sqlBatch, dirtyKeys: null);
        }
        else if (batch.Sessions.Count > 0 || batch.Records.Count > 0)
        {
            AppendWriteMarkerAdvance(sqlBatch, DirtySessionKeysOf(batch));
        }

        ExecuteTransaction(sqlBatch.ToStatements());
    }

    /// <inheritdoc />
    public void DeleteSource(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        string sourceIdValue = GuardedText("source", "source_id", sourceId);
        SqlBatch batch = new();
        AppendSourceDelete(batch, sourceIdValue);

        // The cascade removes records of sessions this call does not know, so a source delete
        // always marks the range wildcard.
        AppendWriteMarkerAdvance(batch, dirtyKeys: null);
        ExecuteTransaction(batch.ToStatements());
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

        string harness = GuardedText("source", "harness_id", harnessId);
        string locator = GuardedText("source", "locator", sourceLocator);
        SqlBatch batch = new();
        switch (estimated)
        {
            case EstimatedRecordTokens.AllRecordTokens:
                AppendSourceSpanTokenDelete(batch, "DELETE FROM record_token_count", harness, locator, excludeReportedTotal: false);
                AppendSourceSpanTokenDelete(batch, "DELETE FROM record_reported_total", harness, locator, excludeReportedTotal: false);
                break;
            case EstimatedRecordTokens.RecordsWithoutReportedTotal:
                AppendSourceSpanTokenDelete(batch, "DELETE FROM record_token_count", harness, locator, excludeReportedTotal: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(estimated), estimated, "Unsupported estimated token scope.");
        }

        ExecuteTransaction(batch.ToStatements());
    }

    private static void AppendSourceSpanTokenDelete(
        SqlBatch batch,
        string deleteClause,
        string harnessId,
        string sourceLocator,
        bool excludeReportedTotal)
    {
        string harnessParam = batch.Add(harnessId);
        string kindParam = batch.Add("file");
        string locatorParam = batch.Add(sourceLocator);
        string sourceSpans =
            "SELECT r.span_id FROM record r JOIN source s ON s.source_id = r.source_id " +
            $"WHERE s.harness_id = {harnessParam} AND s.source_kind = {kindParam} AND s.locator = {locatorParam}";
        string filter = excludeReportedTotal
            ? $" AND span_id NOT IN (SELECT span_id FROM record_reported_total)"
            : string.Empty;
        batch.Append($"{deleteClause} WHERE span_id IN ({sourceSpans}){filter};\n");
    }

    /// <inheritdoc />
    public void WriteProblem(CollectedProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        SqlBatch batch = new();
        AppendProblemUpsert(batch, problem);
        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public void DeleteProblem(string problemKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problemKey);
        string problemKeyParam = GuardedText("ingest_problem", "problem_key", problemKey);
        SqlBatch batch = new();
        string problemKeyValue = batch.Add(problemKeyParam);
        batch.Append($"DELETE FROM ingest_problem WHERE problem_key = {problemKeyValue};\n");
        ExecuteTransaction(batch.ToStatements());
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

        SqlBatch batch = new();
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

            AppendSessionProjectionReplace(batch, projection);
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public void DeleteSessionProjection(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string sessionIdValue = GuardedText("session_projection", "session_id", sessionId);
        SqlBatch batch = new();
        string sessionIdParam = batch.Add(sessionIdValue);
        batch.Append($"DELETE FROM session_projection WHERE session_id = {sessionIdParam};\n");
        ExecuteTransaction(batch.ToStatements());
    }

    public void ReplaceSessionTurns(string sessionId, IEnumerable<StoredSessionTurn> turns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(turns);
        string sessionIdValue = GuardedText("session_turn", "session_id", sessionId);

        SqlBatch batch = new();
        string deleteSessionIdParam = batch.Add(sessionIdValue);
        batch.Append($"DELETE FROM session_turn WHERE session_id = {deleteSessionIdParam};\n");

        foreach (StoredSessionTurn turn in turns)
        {
            ArgumentNullException.ThrowIfNull(turn);
            if (!string.Equals(turn.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"StoredSessionTurn.SessionId '{turn.SessionId}' does not match '{sessionId}'.");
            }

            string spanIdValue = GuardedText("session_turn", "span_id", turn.SpanId);
            string insertSessionIdParam = batch.Add(sessionIdValue);
            string insertSpanIdParam = batch.Add(spanIdValue);
            string positionParam = batch.Add(turn.Position);
            batch.Append(
                $"INSERT INTO session_turn (session_id, position, span_id) VALUES ({insertSessionIdParam}, {positionParam}, {insertSpanIdParam});\n");
        }

        ExecuteTransaction(batch.ToStatements());
    }

    public void ReplaceRuns(IEnumerable<StoredRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        SqlBatch batch = new();
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

            AppendRunUpsert(batch, run);
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public void DeleteRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string runIdValue = GuardedText("run", "run_id", runId);
        SqlBatch batch = new();
        string runIdParam = batch.Add(runIdValue);
        batch.Append($"DELETE FROM run WHERE run_id = {runIdParam};\n");
        ExecuteTransaction(batch.ToStatements());
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

        SqlBatch batch = new();

        foreach (StoredExecution execution in items)
        {
            string executionId = GuardedText("execution", "execution_id", execution.ExecutionId);
            string runId = GuardedText("execution", "run_id", execution.RunId);
            string harnessId = GuardedText("execution", "harness_id", execution.HarnessId);

            string deleteExecutionIdParam = batch.Add(executionId);
            batch.Append($"DELETE FROM execution WHERE execution_id = {deleteExecutionIdParam};\n");

            string insertExecutionIdParam = batch.Add(executionId);
            string insertRunIdParam = batch.Add(runId);
            string insertHarnessIdParam = batch.Add(harnessId);
            string insertIsRootParam = batch.Add(execution.IsRoot ? 1 : 0);
            batch.Append(
                $"INSERT INTO execution (execution_id, run_id, harness_id, is_root) VALUES ({insertExecutionIdParam}, {insertRunIdParam}, {insertHarnessIdParam}, {insertIsRootParam});\n");
        }

        foreach (StoredExecution execution in items)
        {
            string executionId = GuardedText("execution", "execution_id", execution.ExecutionId);
            AppendOptionalTextChild(batch, "execution_session", "execution_id", executionId, "session_id", execution.SessionId);
            AppendOptionalTextChild(batch, "execution_parent", "execution_id", executionId, "parent_execution_id", execution.ParentExecutionId);
            AppendOptionalTextChild(batch, "execution_agent", "execution_id", executionId, "agent_name", execution.AgentName);

            RequireMatchedPair(
                execution.StartedAtUtc is not null,
                execution.EndedAtUtc is not null,
                "execution_bounds",
                "started_at_utc",
                "ended_at_utc");

            if (execution.StartedAtUtc is not null && execution.EndedAtUtc is not null)
            {
                string started = GuardedText("execution_bounds", "started_at_utc", execution.StartedAtUtc);
                string ended = GuardedText("execution_bounds", "ended_at_utc", execution.EndedAtUtc);
                string boundsExecutionIdParam = batch.Add(executionId);
                string startedParam = batch.Add(started);
                string endedParam = batch.Add(ended);
                batch.Append(
                    $"INSERT INTO execution_bounds (execution_id, started_at_utc, ended_at_utc) VALUES ({boundsExecutionIdParam}, {startedParam}, {endedParam});\n");
            }
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public void DeleteExecutionsForRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string runIdValue = GuardedText("execution", "run_id", runId);
        SqlBatch batch = new();
        string runIdParam = batch.Add(runIdValue);
        batch.Append($"DELETE FROM execution WHERE run_id = {runIdParam};\n");
        ExecuteTransaction(batch.ToStatements());
    }

    public void ReplaceHarnessRollups(IEnumerable<StoredHarnessRollup> rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        SqlBatch batch = new();
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

            AppendHarnessRollupReplace(batch, rollup);
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public void DeleteHarnessRollup(string harnessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        string harnessIdValue = GuardedText("harness_rollup", "harness_id", harnessId);
        SqlBatch batch = new();
        string harnessIdParam = batch.Add(harnessIdValue);
        batch.Append($"DELETE FROM harness_rollup WHERE harness_id = {harnessIdParam};\n");
        ExecuteTransaction(batch.ToStatements());
    }

    public void ReplaceFindings(IEnumerable<StoredFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        SqlBatch batch = new();
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

            AppendFindingReplace(batch, finding);
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public void DeleteFindingsForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string sessionIdValue = GuardedText("finding_session", "session_id", sessionId);
        SqlBatch batch = new();
        string sessionIdParam = batch.Add(sessionIdValue);
        batch.Append(
            $"DELETE FROM finding WHERE finding_id IN (SELECT finding_id FROM finding_session WHERE session_id = {sessionIdParam});\n");
        ExecuteTransaction(batch.ToStatements());
    }

    public void ReplacePrediction(StoredPrediction prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);

        SqlBatch batch = new();
        AppendPredictionReplace(batch, prediction);
        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public void DeletePrediction(string predictionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predictionId);
        string predictionIdValue = GuardedText("prediction", "prediction_id", predictionId);
        SqlBatch batch = new();
        string predictionIdParam = batch.Add(predictionIdValue);
        batch.Append($"DELETE FROM prediction WHERE prediction_id = {predictionIdParam};\n");
        ExecuteTransaction(batch.ToStatements());
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
    private static void AppendWriteMarkerAdvance(SqlBatch batch, SortedSet<string>? dirtyKeys)
    {
        const string PrevExpr =
            "(SELECT COALESCE((SELECT records_write_seq FROM store_write_marker WHERE singleton_id = 1), 0))";
        const string ContinuingExpr =
            "EXISTS (SELECT 1 FROM projection_dirty_range WHERE singleton_id = 1 AND to_seq = " + PrevExpr + ")";

        if (dirtyKeys is null)
        {
            batch.Append("DELETE FROM projection_dirty_session;\n");
        }
        else
        {
            batch.Append("DELETE FROM projection_dirty_session WHERE NOT " + ContinuingExpr + ";\n");
            foreach (string key in dirtyKeys)
            {
                string keyValue = GuardedNonEmptyText("projection_dirty_session", "session_id", key);
                string keyParam = batch.Add(keyValue);
                batch.Append(
                    $"INSERT OR IGNORE INTO projection_dirty_session (session_id) SELECT {keyParam} " +
                    $"WHERE NOT {ContinuingExpr} OR EXISTS (SELECT 1 FROM projection_dirty_session);\n");
            }
        }

        batch.Append(
            "INSERT OR REPLACE INTO projection_dirty_range (singleton_id, from_seq, to_seq) SELECT 1, " +
            "CASE WHEN " + ContinuingExpr + " THEN (SELECT from_seq FROM projection_dirty_range WHERE singleton_id = 1) " +
            "ELSE " + PrevExpr + " END, " + PrevExpr + " + 1;\n");
        batch.Append(
            "INSERT OR REPLACE INTO store_write_marker (singleton_id, records_write_seq) SELECT 1, " + PrevExpr + " + 1;\n");
    }

    private void ExecuteTransaction(IReadOnlyList<SqlStatement> statements)
    {
        Action? rebuildCallback = null;
        lock (writeGate)
        {
            if (!initialized)
            {
                rebuildCallback = Initialize();
                initialized = true;
            }

            if (GetType() == typeof(SqliteRelationalStore))
            {
                ExecuteStatements(statements);
            }
            else
            {
                // A subclass overrides ExecuteSqliteCore to script or inspect SQL, so honour that
                // seam by rendering the parameterized statements into one script for it.
                StringBuilder script = new();
                script.Append("PRAGMA foreign_keys = ON;\n");
                script.Append("BEGIN IMMEDIATE;\n");
                foreach (SqlStatement statement in statements)
                {
                    script.Append(RenderStatement(statement));
                }

                script.Append("COMMIT;\n");
                CircuitBreaker.Execute(() => ExecuteSqliteCore(script.ToString()));
            }
        }

        rebuildCallback?.Invoke();
    }

    private static string RenderStatement(SqlStatement statement)
    {
        string sql = statement.Sql;
        string[] names = [.. statement.Parameters.Keys];
        Array.Sort(names, static (left, right) => right.Length.CompareTo(left.Length));
        foreach (string name in names)
        {
            sql = sql.Replace(name, RenderParameterLiteral(statement.Parameters[name]), StringComparison.Ordinal);
        }

        return sql;
    }

    private static string RenderParameterLiteral(object? value) => value switch
    {
        null => "NULL",
        string text => $"X'{Convert.ToHexString(Encoding.UTF8.GetBytes(text))}' || ''",
        byte[] bytes => $"X'{Convert.ToHexString(bytes)}'",
        double real => real.ToString("G17", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "NULL",
    };

    private void ExecuteTransaction(string bodySql)
    {
        ArgumentNullException.ThrowIfNull(bodySql);
        Action? rebuildCallback = null;
        lock (writeGate)
        {
            if (!initialized)
            {
                rebuildCallback = Initialize();
                initialized = true;
            }

            if (GetType() == typeof(SqliteRelationalStore))
            {
                ExecuteStatements([SqlStatement.Of(bodySql)]);
            }
            else
            {
                StringBuilder script = new(bodySql.Length + 64);
                script.Append("PRAGMA foreign_keys = ON;\n");
                script.Append("BEGIN IMMEDIATE;\n");
                script.Append(bodySql);
                script.Append("COMMIT;\n");
                CircuitBreaker.Execute(() => ExecuteSqliteCore(script.ToString()));
            }
        }

        rebuildCallback?.Invoke();
    }

    /// <summary>Accumulates parameterized statements, each binding its values by generated <c>$name</c> marker.</summary>
    private sealed class SqlBatch
    {
        private readonly List<SqlStatement> statements = [];
        private readonly Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        private readonly StringBuilder sql = new();

        /// <summary>Binds one value to the current statement and returns its <c>$name</c> marker.</summary>
        public string Add(object? value)
        {
            string name = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(name, value);
            return name;
        }

        /// <summary>Appends SQL text, completing the current statement when the text terminates it.</summary>
        public void Append(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            sql.Append(text);
            if (text.EndsWith(";\n", StringComparison.Ordinal))
            {
                EndStatement();
            }
        }

        /// <summary>Returns the completed statements, ending any trailing unterminated statement.</summary>
        public IReadOnlyList<SqlStatement> ToStatements()
        {
            EndStatement();
            return statements;
        }

        private void EndStatement()
        {
            if (sql.Length == 0)
            {
                return;
            }

            statements.Add(new SqlStatement(
                sql.ToString(),
                new Dictionary<string, object?>(parameters, StringComparer.Ordinal)));
            sql.Clear();
            parameters.Clear();
        }
    }

    private static string GuardedText(string tableName, string columnName, string? value)
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

        return value;
    }

    private static string GuardedNonEmptyText(string tableName, string columnName, string? value)
    {
        string text = GuardedText(tableName, columnName, value);
        if (text.Length == 0)
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                $"Non-empty TEXT column '{tableName}.{columnName}' cannot be empty.");
        }

        return text;
    }

    /// <summary>Keeps the legacy guarded-text validation on the write path while returning the value to bind.</summary>
    private static readonly Func<string, string, string?, string> GuardedTextExpr =
        static (tableName, columnName, value) => GuardedText(tableName, columnName, value);

    private static string IntSql(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string LongSql(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string BoolSql(bool value) => value ? "1" : "0";

    private static string RealSql(string tableName, string columnName, double value) =>
        RequireFiniteReal(tableName, columnName, value).ToString("G17", CultureInfo.InvariantCulture);

    private static double RequireFiniteReal(string tableName, string columnName, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                $"REAL column '{tableName}.{columnName}' must be finite.");
        }

        return value;
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
        SqlBatch batch,
        string tableName,
        string keyColumn,
        string keyValue,
        string valueColumn,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        string valueText = GuardedText(tableName, valueColumn, value);
        string keyParam = batch.Add(keyValue);
        string valueParam = batch.Add(valueText);
        batch.Append($"INSERT INTO {tableName} ({keyColumn}, {valueColumn}) VALUES ({keyParam}, {valueParam});\n");
    }

    private static void AppendOptionalLongChild(
        SqlBatch batch,
        string tableName,
        string keyColumn,
        string keyValue,
        string valueColumn,
        long? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        string keyParam = batch.Add(keyValue);
        string valueParam = batch.Add(value.Value);
        batch.Append($"INSERT INTO {tableName} ({keyColumn}, {valueColumn}) VALUES ({keyParam}, {valueParam});\n");
    }

    private static void AppendHarnessInsert(SqlBatch batch, HashSet<string> seenHarnesses, string harnessId)
    {
        string harnessValue = GuardedText("harness", "harness_id", harnessId);
        if (seenHarnesses.Add(harnessId))
        {
            string harnessParam = batch.Add(harnessValue);
            batch.Append($"INSERT OR IGNORE INTO harness (harness_id) VALUES ({harnessParam});\n");
        }
    }

    /// <summary>
    /// Deletes one source and the records it owns, after handing each record another source still claims
    /// (<c>record_source_claim</c>) to that source, so a span shared with another transcript survives.
    /// </summary>
    private static void AppendSourceDelete(SqlBatch batch, string sourceId)
    {
        string updateSourceIdParam = batch.Add(sourceId);
        string otherClaim =
            $"FROM record_source_claim c WHERE c.span_id = record.span_id AND c.source_id <> {updateSourceIdParam}";
        batch.Append(
            $"UPDATE record SET source_id = (SELECT c.source_id {otherClaim} ORDER BY c.source_id LIMIT 1) " +
            $"WHERE source_id = {updateSourceIdParam} AND EXISTS (SELECT 1 {otherClaim});\n");
        string deleteSourceIdParam = batch.Add(sourceId);
        batch.Append($"DELETE FROM source WHERE source_id = {deleteSourceIdParam};\n");
    }

    private static string SourceIdOf(CollectedSource source)
    {
        string kindText = FormatSourceKind(source.Kind);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{source.HarnessId}\0{kindText}\0{source.Locator}"));
        return $"src-{Convert.ToHexStringLower(digest)[..16]}";
    }

    private static string AppendSourceInsert(SqlBatch batch, CollectedSource source)
    {
        string kindText = FormatSourceKind(source.Kind);
        string sourceId = SourceIdOf(source);
        string harnessValue = GuardedText("source", "harness_id", source.HarnessId);
        string kindValue = GuardedText("source", "source_kind", kindText);
        string locatorValue = GuardedText("source", "locator", source.Locator);

        string sourceIdParam = batch.Add(sourceId);
        string harnessParam = batch.Add(harnessValue);
        string kindParam = batch.Add(kindValue);
        string locatorParam = batch.Add(locatorValue);

        batch.Append(
            $"INSERT OR IGNORE INTO source (source_id, harness_id, source_kind, locator) VALUES ({sourceIdParam}, {harnessParam}, {kindParam}, {locatorParam});\n");
        return sourceId;
    }

    private static void AppendSessionUpsert(SqlBatch batch, CollectedSession session)
    {
        string sessionId = GuardedNonEmptyText("session", "session_id", session.SessionKey);
        string harnessId = GuardedText("session", "harness_id", session.HarnessId);
        string nativeSessionId = GuardedText("session", "native_session_id", session.NativeSessionId);

        string insertSessionIdParam = batch.Add(sessionId);
        string insertHarnessIdParam = batch.Add(harnessId);
        string insertNativeSessionIdParam = batch.Add(nativeSessionId);
        batch.Append(
            $"INSERT INTO session (session_id, harness_id, native_session_id) VALUES ({insertSessionIdParam}, {insertHarnessIdParam}, {insertNativeSessionIdParam}) ");
        batch.Append("ON CONFLICT(session_id) DO UPDATE SET harness_id = excluded.harness_id, native_session_id = excluded.native_session_id;\n");

        AppendSessionIdDelete(batch, "session_workspace", sessionId);
        AppendSessionIdDelete(batch, "session_repository", sessionId);
        AppendSessionIdDelete(batch, "session_branch", sessionId);
        AppendSessionIdDelete(batch, "session_title", sessionId);
        AppendSessionIdDelete(batch, "session_model_usage", sessionId);

        if (session.Workspace is not null)
        {
            AppendOptionalTextChild(batch, "session_workspace", "session_id", sessionId, "working_directory", session.Workspace.WorkingDirectory);
            AppendOptionalTextChild(batch, "session_repository", "session_id", sessionId, "repo_name", session.Workspace.Repository);
            AppendOptionalTextChild(batch, "session_branch", "session_id", sessionId, "branch_name", session.Workspace.Branch);
        }

        AppendOptionalTextChild(batch, "session_title", "session_id", sessionId, "title", session.Title);

        if (session.ReportedUsage is not null)
        {
            foreach (CollectedModelUsage usage in session.ReportedUsage)
            {
                ArgumentNullException.ThrowIfNull(usage);
                string modelValue = GuardedText("session_model_usage", "model_id", usage.Model);
                string tokenClass = FormatTokenClass(usage.TokenClass);
                string usageSessionIdParam = batch.Add(sessionId);
                string snapshotPositionParam = batch.Add(usage.SnapshotPosition);
                string modelParam = batch.Add(modelValue);
                string tokenClassParam = batch.Add(tokenClass);
                string countParam = batch.Add(usage.Count);
                batch.Append(
                    $"INSERT INTO session_model_usage (session_id, snapshot_position, model_id, token_class, token_count) VALUES ({usageSessionIdParam}, {snapshotPositionParam}, {modelParam}, {tokenClassParam}, {countParam});\n");
            }
        }
    }

    private static void AppendSessionIdDelete(SqlBatch batch, string tableName, string sessionId)
    {
        string sessionIdParam = batch.Add(sessionId);
        batch.Append($"DELETE FROM {tableName} WHERE session_id = {sessionIdParam};\n");
    }

    private static void AppendRecordInsert(SqlBatch batch, string sourceId, CollectedRecord record)
    {
        if (record.Signals is null)
        {
            throw new DashValidationException(ConstraintViolatedCode, "CollectedRecord.Signals cannot be null.");
        }

        string spanId = GuardedText("record", "span_id", record.RecordId);
        string harnessId = GuardedText("record", "harness_id", record.HarnessId);
        string op = FormatRecordOp(record.Op);
        string kind = FormatRecordKind(record.Kind);
        string name = GuardedText("record", "name", record.Name);
        string started = FormatTimestamp(record.StartedAt);
        string status = FormatRecordStatus(record.Status);

        // One span can arrive from more than one source: a forked or resumed Claude Code transcript
        // copies its parent's lines, parent session id and message ids included, so both files map
        // to the same span id. The latest write owns the span; the delete cascades its child rows.
        // Each writer's claim is kept, so deleting the owner hands the span on (AppendSourceDelete).
        string deleteSpanParam = batch.Add(spanId);
        batch.Append($"DELETE FROM record WHERE span_id = {deleteSpanParam};\n");

        string claimSpanParam = batch.Add(spanId);
        string claimSourceParam = batch.Add(sourceId);
        batch.Append(
            $"INSERT OR IGNORE INTO record_source_claim (span_id, source_id) VALUES ({claimSpanParam}, {claimSourceParam});\n");

        string insertSpanParam = batch.Add(spanId);
        string insertSourceParam = batch.Add(sourceId);
        string insertHarnessParam = batch.Add(harnessId);
        string insertOpParam = batch.Add(op);
        string insertKindParam = batch.Add(kind);
        string insertNameParam = batch.Add(name);
        string insertStartedParam = batch.Add(started);
        string insertStatusParam = batch.Add(status);
        batch.Append(
            $"INSERT INTO record (span_id, source_id, harness_id, op, kind, name, started_at_utc, status) VALUES ({insertSpanParam}, {insertSourceParam}, {insertHarnessParam}, {insertOpParam}, {insertKindParam}, {insertNameParam}, {insertStartedParam}, {insertStatusParam});\n");

        AppendOptionalNonEmptyTextChild(batch, "record_session_link", "span_id", spanId, "session_key", record.SessionKey);
        AppendOptionalTextChild(batch, "record_parent", "span_id", spanId, "parent_span_id", record.ParentRecordId);
        AppendOptionalTextChild(batch, "record_trace", "span_id", spanId, "trace_id", record.TraceId);
        AppendOptionalTextChild(batch, "record_model", "span_id", spanId, "model_id", record.Model);
        AppendOptionalTextChild(batch, "record_response_id", "span_id", spanId, "response_id", record.ResponseId);
        AppendOptionalTextChild(batch, "record_request_id", "span_id", spanId, "request_id", record.RequestId);

        if (record.EndedAt.HasValue)
        {
            string ended = FormatTimestamp(record.EndedAt.Value);
            string endedSpanParam = batch.Add(spanId);
            string endedParam = batch.Add(ended);
            batch.Append($"INSERT INTO record_end (span_id, ended_at_utc) VALUES ({endedSpanParam}, {endedParam});\n");
        }

        AppendOptionalLongChild(batch, "record_reported_duration", "span_id", spanId, "duration_ms", record.ReportedDurationMs);

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

                string tokenClass = FormatTokenClass(tokenCount.TokenClass);
                string tokenSpanParam = batch.Add(spanId);
                string tokenClassParam = batch.Add(tokenClass);
                string countParam = batch.Add(tokenCount.Count);
                batch.Append(
                    $"INSERT INTO record_token_count (span_id, token_class, token_count) VALUES ({tokenSpanParam}, {tokenClassParam}, {countParam});\n");
            }
        }

        if (record.ReportedInputTokens.HasValue)
        {
            string inputSpanParam = batch.Add(spanId);
            string directionParam = batch.Add("input");
            string countParam = batch.Add(record.ReportedInputTokens.Value);
            batch.Append(
                $"INSERT INTO record_reported_total (span_id, direction, token_count) VALUES ({inputSpanParam}, {directionParam}, {countParam});\n");
        }

        if (record.ReportedOutputTokens.HasValue)
        {
            string outputSpanParam = batch.Add(spanId);
            string directionParam = batch.Add("output");
            string countParam = batch.Add(record.ReportedOutputTokens.Value);
            batch.Append(
                $"INSERT INTO record_reported_total (span_id, direction, token_count) VALUES ({outputSpanParam}, {directionParam}, {countParam});\n");
        }

        if (record.ReportedCost is not null)
        {
            double amount = RequireFiniteReal("record_reported_cost", "amount", record.ReportedCost.Amount);
            string currencyValue = GuardedText("record_reported_cost", "currency", record.ReportedCost.Currency);
            string costSpanParam = batch.Add(spanId);
            string amountParam = batch.Add(amount);
            string currencyParam = batch.Add(currencyValue);
            batch.Append(
                $"INSERT INTO record_reported_cost (span_id, amount, currency) VALUES ({costSpanParam}, {amountParam}, {currencyParam});\n");
        }

        if (record.SkillsInvoked is not null)
        {
            foreach (string skillName in record.SkillsInvoked)
            {
                string skillValue = GuardedText("record_skill_invocation", "skill_name", skillName);
                string skillSpanParam = batch.Add(spanId);
                string skillParam = batch.Add(skillValue);
                batch.Append($"INSERT INTO record_skill_invocation (span_id, skill_name) VALUES ({skillSpanParam}, {skillParam});\n");
            }
        }

        if (record.SkillsListed is not null)
        {
            for (int position = 0; position < record.SkillsListed.Count; position++)
            {
                string skillValue = GuardedText("record_skill_listing", "skill_name", record.SkillsListed[position]);
                string listingSpanParam = batch.Add(spanId);
                string positionParam = batch.Add(position);
                string skillParam = batch.Add(skillValue);
                batch.Append($"INSERT INTO record_skill_listing (span_id, position, skill_name) VALUES ({listingSpanParam}, {positionParam}, {skillParam});\n");
            }
        }

        AppendOptionalTextChild(batch, "record_agent_instance", "span_id", spanId, "agent_instance_id", record.AgentInstanceId);
        AppendOptionalTextChild(batch, "record_error", "span_id", spanId, "message", record.Signals.ErrorMessage);
        AppendOptionalLongChild(batch, "record_edit_failure", "span_id", spanId, "failure_count", record.Signals.EditFailures);

        if (record.Signals.CorrectionRule is not null)
        {
            string ruleValue = GuardedText("record_correction_rule", "correction_rule", record.Signals.CorrectionRule);
            string correctionSpanParam = batch.Add(spanId);
            batch.Append($"INSERT INTO record_correction (span_id) VALUES ({correctionSpanParam});\n");
            string ruleSpanParam = batch.Add(spanId);
            string ruleParam = batch.Add(ruleValue);
            batch.Append($"INSERT INTO record_correction_rule (span_id, correction_rule) VALUES ({ruleSpanParam}, {ruleParam});\n");
        }

        AppendOptionalLongChild(
            batch,
            "record_declared_context_window",
            "span_id",
            spanId,
            "declared_context_window",
            record.Signals.DeclaredContextWindow);

        AppendRecordParts(batch, spanId, record);
        AppendRecordToolDefinitions(batch, spanId, record);
        AppendRecordToolCall(batch, spanId, record.Tool);
        AppendRecordAgentInvocation(batch, spanId, record.Agent);
    }

    private static void AppendRecordParts(SqlBatch batch, string spanId, CollectedRecord record)
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

            string bucket = FormatContentBucket(part.Bucket);
            string partSpanParam = batch.Add(spanId);
            string partPositionParam = batch.Add(part.Position);
            string bucketParam = batch.Add(bucket);
            batch.Append($"INSERT INTO content_part (span_id, position, bucket) VALUES ({partSpanParam}, {partPositionParam}, {bucketParam});\n");

            if (part.PlainText is not null)
            {
                string partText = GuardedText("content_part_text", "part_text", part.PlainText);
                string textSpanParam = batch.Add(spanId);
                string textPositionParam = batch.Add(part.Position);
                string partTextParam = batch.Add(partText);
                batch.Append($"INSERT INTO content_part_text (span_id, position, part_text) VALUES ({textSpanParam}, {textPositionParam}, {partTextParam});\n");
            }
            else if (part.Withheld is not null)
            {
                string withheldSpanParam = batch.Add(spanId);
                string withheldPositionParam = batch.Add(part.Position);
                string utf8Param = batch.Add(part.Withheld.Utf8Bytes);
                string charactersParam = batch.Add(part.Withheld.Characters);
                string measuredTokensParam = batch.Add(part.Withheld.MeasuredTokens);
                batch.Append(
                    $"INSERT INTO content_part_withheld (span_id, position, utf8_bytes, characters, measured_tokens) VALUES ({withheldSpanParam}, {withheldPositionParam}, {utf8Param}, {charactersParam}, {measuredTokensParam});\n");
                if (part.Withheld.Tokenizer is string tokenizer)
                {
                    string tokenizerValue = GuardedText("content_part_withheld_tokenizer", "tokenizer", tokenizer);
                    string tokenizerSpanParam = batch.Add(spanId);
                    string tokenizerPositionParam = batch.Add(part.Position);
                    string tokenizerParam = batch.Add(tokenizerValue);
                    string isModelOwnParam = batch.Add(part.Withheld.TokenizerIsModelOwn ? 1 : 0);
                    batch.Append(
                        $"INSERT INTO content_part_withheld_tokenizer (span_id, position, tokenizer, is_model_own) VALUES ({tokenizerSpanParam}, {tokenizerPositionParam}, {tokenizerParam}, {isModelOwnParam});\n");
                }
            }

            if (part.ReportedTokens.HasValue)
            {
                string reportedSpanParam = batch.Add(spanId);
                string reportedPositionParam = batch.Add(part.Position);
                string reportedTokensParam = batch.Add(part.ReportedTokens.Value);
                batch.Append(
                    $"INSERT INTO content_part_reported_tokens (span_id, position, token_count) VALUES ({reportedSpanParam}, {reportedPositionParam}, {reportedTokensParam});\n");
            }

            if (part.Server is not null)
            {
                string serverValue = GuardedText("content_part_server", "server_name", part.Server);
                string serverSpanParam = batch.Add(spanId);
                string serverPositionParam = batch.Add(part.Position);
                string serverParam = batch.Add(serverValue);
                batch.Append($"INSERT INTO content_part_server (span_id, position, server_name) VALUES ({serverSpanParam}, {serverPositionParam}, {serverParam});\n");
            }

            if (part.Role is not null)
            {
                string roleValue = GuardedText("content_part_role", "role_name", part.Role);
                string roleSpanParam = batch.Add(spanId);
                string rolePositionParam = batch.Add(part.Position);
                string roleParam = batch.Add(roleValue);
                batch.Append($"INSERT INTO content_part_role (span_id, position, role_name) VALUES ({roleSpanParam}, {rolePositionParam}, {roleParam});\n");
            }

            if (part.Resident.HasValue)
            {
                string residentSpanParam = batch.Add(spanId);
                string residentPositionParam = batch.Add(part.Position);
                string residentParam = batch.Add(part.Resident.Value ? 1 : 0);
                batch.Append(
                    $"INSERT INTO content_part_residency (span_id, position, is_resident) VALUES ({residentSpanParam}, {residentPositionParam}, {residentParam});\n");
            }

            if (part.OriginalUtf8BytesWhenTruncated.HasValue)
            {
                string truncationSpanParam = batch.Add(spanId);
                string truncationPositionParam = batch.Add(part.Position);
                string truncationBytesParam = batch.Add(part.OriginalUtf8BytesWhenTruncated.Value);
                batch.Append(
                    $"INSERT INTO content_part_truncation (span_id, position, original_utf8_bytes) VALUES ({truncationSpanParam}, {truncationPositionParam}, {truncationBytesParam});\n");
            }
        }
    }

    private static void AppendRecordToolDefinitions(SqlBatch batch, string spanId, CollectedRecord record)
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

            string toolNameValue = GuardedText("tool_definition", "tool_name", definition.ToolName);
            string definitionSpanParam = batch.Add(spanId);
            string positionParam = batch.Add(definition.Position);
            string toolNameParam = batch.Add(toolNameValue);
            string schemaTokensParam = batch.Add(definition.SchemaTokens);
            batch.Append(
                $"INSERT INTO tool_definition (span_id, position, tool_name, schema_tokens) VALUES ({definitionSpanParam}, {positionParam}, {toolNameParam}, {schemaTokensParam});\n");

            if (definition.Tokenizer is string tokenizer)
            {
                string tokenizerValue = GuardedText("tool_definition_tokenizer", "tokenizer", tokenizer);
                string tokenizerSpanParam = batch.Add(spanId);
                string tokenizerPositionParam = batch.Add(definition.Position);
                string tokenizerParam = batch.Add(tokenizerValue);
                string isModelOwnParam = batch.Add(definition.TokenizerIsModelOwn ? 1 : 0);
                batch.Append(
                    $"INSERT INTO tool_definition_tokenizer (span_id, position, tokenizer, is_model_own) VALUES ({tokenizerSpanParam}, {tokenizerPositionParam}, {tokenizerParam}, {isModelOwnParam});\n");
            }

            if (definition.Server is not null)
            {
                string serverValue = GuardedText("tool_definition_server", "server_name", definition.Server);
                string serverSpanParam = batch.Add(spanId);
                string serverPositionParam = batch.Add(definition.Position);
                string serverParam = batch.Add(serverValue);
                batch.Append(
                    $"INSERT INTO tool_definition_server (span_id, position, server_name) VALUES ({serverSpanParam}, {serverPositionParam}, {serverParam});\n");
            }
        }
    }

    private static void AppendRecordToolCall(SqlBatch batch, string spanId, CollectedToolCall? tool)
    {
        if (tool is null)
        {
            return;
        }

        string callIdValue = GuardedText("tool_call", "call_id", tool.CallId);
        string toolNameValue = GuardedText("tool_call", "tool_name", tool.ToolName);
        string callSpanParam = batch.Add(spanId);
        string callIdParam = batch.Add(callIdValue);
        string callToolNameParam = batch.Add(toolNameValue);
        batch.Append($"INSERT INTO tool_call (span_id, call_id, tool_name) VALUES ({callSpanParam}, {callIdParam}, {callToolNameParam});\n");

        RequireMatchedPair(
            tool.McpServerName is not null,
            tool.McpToolName is not null,
            "tool_call_mcp",
            "server_name",
            "server_tool_name");

        if (tool.McpServerName is not null && tool.McpToolName is not null)
        {
            string serverValue = GuardedText("tool_call_mcp", "server_name", tool.McpServerName);
            string serverToolValue = GuardedText("tool_call_mcp", "server_tool_name", tool.McpToolName);
            string mcpSpanParam = batch.Add(spanId);
            string serverParam = batch.Add(serverValue);
            string serverToolParam = batch.Add(serverToolValue);
            batch.Append(
                $"INSERT INTO tool_call_mcp (span_id, server_name, server_tool_name) VALUES ({mcpSpanParam}, {serverParam}, {serverToolParam});\n");
        }

        AppendToolContent(batch, spanId, tool.Input, "tool_call_input");
        AppendToolContent(batch, spanId, tool.Result, "tool_call_result");

        if (tool.Succeeded.HasValue)
        {
            string outcomeSpanParam = batch.Add(spanId);
            string succeededParam = batch.Add(tool.Succeeded.Value ? 1 : 0);
            batch.Append($"INSERT INTO tool_call_outcome (span_id, succeeded) VALUES ({outcomeSpanParam}, {succeededParam});\n");
        }

        if (tool.Error is not null)
        {
            string messageValue = GuardedText("tool_call_error", "message", tool.Error.Message);
            string errorSpanParam = batch.Add(spanId);
            string messageParam = batch.Add(messageValue);
            batch.Append($"INSERT INTO tool_call_error (span_id, message) VALUES ({errorSpanParam}, {messageParam});\n");
            AppendOptionalTextChild(batch, "tool_call_error_code", "span_id", spanId, "code", tool.Error.Code);
        }

        AppendOptionalLongChild(batch, "tool_call_exit_code", "span_id", spanId, "exit_code", tool.ExitCode);
    }

    private static void AppendToolContent(
        SqlBatch batch,
        string spanId,
        CollectedToolContent? content,
        string tablePrefix)
    {
        if (content is null)
        {
            return;
        }

        string sizeSpanParam = batch.Add(spanId);
        string utf8Param = batch.Add(content.Utf8Bytes);
        string charactersParam = batch.Add(content.Characters);
        batch.Append(
            $"INSERT INTO {tablePrefix}_size (span_id, utf8_bytes, characters) VALUES ({sizeSpanParam}, {utf8Param}, {charactersParam});\n");

        if (string.Equals(tablePrefix, "tool_call_input", StringComparison.Ordinal))
        {
            AppendOptionalTextChild(batch, "tool_call_input_fingerprint", "span_id", spanId, "sha256", content.Sha256Fingerprint);
        }

        AppendOptionalTextChild(batch, $"{tablePrefix}_text", "span_id", spanId, "text", content.PlainText);
        AppendOptionalLongChild(batch, $"{tablePrefix}_truncation", "span_id", spanId, "original_utf8_bytes", content.OriginalUtf8BytesWhenTruncated);
    }

    private static void AppendRecordAgentInvocation(SqlBatch batch, string spanId, CollectedAgentInvocation? agent)
    {
        if (agent is null)
        {
            return;
        }

        string agentNameValue = GuardedText("agent_invocation", "agent_name", agent.AgentName);
        string agentSpanParam = batch.Add(spanId);
        string agentNameParam = batch.Add(agentNameValue);
        batch.Append($"INSERT INTO agent_invocation (span_id, agent_name) VALUES ({agentSpanParam}, {agentNameParam});\n");

        AppendOptionalTextChild(batch, "agent_invocation_spawn", "span_id", spanId, "spawning_record_id", agent.SpawningCallId);

        if (agent.Succeeded.HasValue)
        {
            string outcomeSpanParam = batch.Add(spanId);
            string succeededParam = batch.Add(agent.Succeeded.Value ? 1 : 0);
            batch.Append(
                $"INSERT INTO agent_invocation_outcome (span_id, succeeded) VALUES ({outcomeSpanParam}, {succeededParam});\n");
        }

        AppendOptionalTextChild(batch, "agent_invocation_error", "span_id", spanId, "message", agent.Error);
    }

    private static void AppendOptionalNonEmptyTextChild(
        SqlBatch batch,
        string tableName,
        string keyColumn,
        string keyValue,
        string valueColumn,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        string text = GuardedNonEmptyText(tableName, valueColumn, value);
        string keyParam = batch.Add(keyValue);
        string valueParam = batch.Add(text);
        batch.Append($"INSERT INTO {tableName} ({keyColumn}, {valueColumn}) VALUES ({keyParam}, {valueParam});\n");
    }

    private static void AppendProblemUpsert(SqlBatch batch, CollectedProblem problem)
    {
        string problemKeyValue = GuardedText("ingest_problem", "problem_key", problem.ProblemKey);
        string severityValue = GuardedText("ingest_problem", "severity", problem.Severity);
        string codeValue = GuardedText("ingest_problem", "code", problem.Code);
        string messageValue = GuardedText("ingest_problem", "message", problem.Message);

        string upsertProblemKeyParam = batch.Add(problemKeyValue);
        string severityParam = batch.Add(severityValue);
        string codeParam = batch.Add(codeValue);
        string messageParam = batch.Add(messageValue);
        batch.Append(
            $"INSERT INTO ingest_problem (problem_key, severity, code, message) VALUES ({upsertProblemKeyParam}, {severityParam}, {codeParam}, {messageParam}) ");
        batch.Append("ON CONFLICT(problem_key) DO UPDATE SET severity = excluded.severity, code = excluded.code, message = excluded.message;\n");

        foreach (string childTable in ProblemChildTables)
        {
            string deleteProblemKeyParam = batch.Add(problemKeyValue);
            batch.Append(
                $"DELETE FROM {childTable} WHERE problem_id = (SELECT problem_id FROM ingest_problem WHERE problem_key = {deleteProblemKeyParam});\n");
        }

        AppendProblemOptionalChild(batch, problemKeyValue, "ingest_problem_span", "span_id", problem.SpanId);
        AppendProblemOptionalChild(batch, problemKeyValue, "ingest_problem_location", "location", problem.Location);
        AppendProblemOptionalChild(batch, problemKeyValue, "ingest_problem_session", "session_id", problem.SessionKey);
        AppendProblemOptionalChild(batch, problemKeyValue, "ingest_problem_harness", "harness_id", problem.HarnessId);
        AppendProblemOptionalChild(batch, problemKeyValue, "ingest_problem_timestamp", "timestamp_utc", problem.TimestampUtc);
        AppendProblemOptionalChild(batch, problemKeyValue, "ingest_problem_stack", "stack", problem.Stack);
    }

    private static void AppendProblemOptionalChild(
        SqlBatch batch,
        string problemKeyValue,
        string tableName,
        string columnName,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        string valueText = GuardedText(tableName, columnName, value);
        string valueParam = batch.Add(valueText);
        string problemKeyParam = batch.Add(problemKeyValue);
        batch.Append(
            $"INSERT INTO {tableName} (problem_id, {columnName}) SELECT problem_id, {valueParam} FROM ingest_problem WHERE problem_key = {problemKeyParam};\n");
    }

    private static void AppendSessionProjectionReplace(SqlBatch batch, StoredSessionProjection projection)
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

        string sessionId = GuardedText("session_projection", "session_id", projection.SessionId);
        string label = GuardedText("session_projection", "label", projection.Label);
        string started = GuardedText("session_projection", "started_at_utc", projection.StartedAtUtc);
        string ended = GuardedText("session_projection", "ended_at_utc", projection.EndedAtUtc);

        string deleteSessionIdParam = batch.Add(sessionId);
        batch.Append($"DELETE FROM session_projection WHERE session_id = {deleteSessionIdParam};\n");

        string insertSessionIdParam = batch.Add(sessionId);
        string labelParam = batch.Add(label);
        string isSubagentParam = batch.Add(projection.IsSubagent ? 1 : 0);
        string spanCountParam = batch.Add(projection.SpanCount);
        string startedParam = batch.Add(started);
        string endedParam = batch.Add(ended);
        batch.Append(
            $"INSERT INTO session_projection (session_id, label, is_subagent, span_count, started_at_utc, ended_at_utc) VALUES ({insertSessionIdParam}, {labelParam}, {isSubagentParam}, {spanCountParam}, {startedParam}, {endedParam});\n");

        AppendOptionalTextChild(batch, "session_projection_parent", "session_id", sessionId, "parent_session_id", projection.ParentSessionId);
        AppendOptionalTextChild(batch, "session_projection_agent", "session_id", sessionId, "agent_name", projection.AgentName);

        if (projection.IncludedInParent)
        {
            string includedSessionIdParam = batch.Add(sessionId);
            batch.Append($"INSERT INTO session_projection_included_in_parent (session_id) VALUES ({includedSessionIdParam});\n");
        }

        if (projection.RequestSpanIds is not null)
        {
            for (int i = 0; i < projection.RequestSpanIds.Count; i++)
            {
                string requestSpanValue = GuardedText("session_request", "request_span_id", projection.RequestSpanIds[i]);
                string requestSessionIdParam = batch.Add(sessionId);
                string requestPositionParam = batch.Add(i);
                string requestSpanParam = batch.Add(requestSpanValue);
                batch.Append(
                    $"INSERT INTO session_request (session_id, position, request_span_id) VALUES ({requestSessionIdParam}, {requestPositionParam}, {requestSpanParam});\n");
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

                string turnSpanId = GuardedText("session_turn", "span_id", turn.SpanId);
                string turnSessionIdParam = batch.Add(sessionId);
                string turnPositionParam = batch.Add(turn.Position);
                string turnSpanIdParam = batch.Add(turnSpanId);
                batch.Append(
                    $"INSERT INTO session_turn (session_id, position, span_id) VALUES ({turnSessionIdParam}, {turnPositionParam}, {turnSpanIdParam});\n");
            }
        }

        if (projection.Context is not null)
        {
            AppendSessionContext(batch, sessionId, projection.Context);
        }
        else
        {
            AppendOptionalTextChild(batch, "session_context_unmeasurable", "session_id", sessionId, "reason", projection.ContextUnmeasurableReason);
        }

        if (projection.Schema is not null)
        {
            AppendSessionSchema(batch, sessionId, projection.Schema);
        }
        else
        {
            AppendOptionalTextChild(batch, "session_schema_unmeasurable", "session_id", sessionId, "reason", projection.SchemaUnmeasurableReason);
        }

        if (projection.TimelineSubagentSpanIds is not null)
        {
            foreach (string spanId in projection.TimelineSubagentSpanIds)
            {
                string subagentSpanValue = GuardedText("session_timeline_subagent", "span_id", spanId);
                string subagentSessionIdParam = batch.Add(sessionId);
                string subagentSpanParam = batch.Add(subagentSpanValue);
                batch.Append($"INSERT INTO session_timeline_subagent (session_id, span_id) VALUES ({subagentSessionIdParam}, {subagentSpanParam});\n");
            }
        }

        if (projection.TimelineAuxiliarySpanIds is not null)
        {
            foreach (string spanId in projection.TimelineAuxiliarySpanIds)
            {
                string auxiliarySpanValue = GuardedText("session_timeline_auxiliary", "span_id", spanId);
                string auxiliarySessionIdParam = batch.Add(sessionId);
                string auxiliarySpanParam = batch.Add(auxiliarySpanValue);
                batch.Append($"INSERT INTO session_timeline_auxiliary (session_id, span_id) VALUES ({auxiliarySessionIdParam}, {auxiliarySpanParam});\n");
            }
        }
    }

    private static void AppendSessionContext(SqlBatch batch, string sessionId, StoredSessionContext context)
    {
        string sourceValue = GuardedText("session_context", "context_limit_source", context.ContextLimitSource);
        double freshJumpFactor = RequireFiniteReal("session_context", "fresh_jump_factor", context.FreshJumpFactor);
        double accumulationRate = RequireFiniteReal("session_context", "accumulation_rate", context.AccumulationRate);

        string contextSessionIdParam = batch.Add(sessionId);
        string contextLimitParam = batch.Add(context.ContextLimit);
        string sourceParam = batch.Add(sourceValue);
        string residualTotalParam = batch.Add(context.ResidualTotal);
        string freshJumpParam = batch.Add(freshJumpFactor);
        string accumulationParam = batch.Add(accumulationRate);
        batch.Append(
            $"INSERT INTO session_context (session_id, context_limit, context_limit_source, residual_total, fresh_jump_factor, accumulation_rate) VALUES ({contextSessionIdParam}, {contextLimitParam}, {sourceParam}, {residualTotalParam}, {freshJumpParam}, {accumulationParam});\n");

        if (context.ObservedBuckets is not null)
        {
            foreach (string bucket in context.ObservedBuckets)
            {
                string bucketValue = GuardedText("session_context_observed_part", "bucket", bucket);
                string observedSessionIdParam = batch.Add(sessionId);
                string bucketParam = batch.Add(bucketValue);
                batch.Append($"INSERT INTO session_context_observed_part (session_id, bucket) VALUES ({observedSessionIdParam}, {bucketParam});\n");
            }
        }

        if (context.DerivedCounts is not null)
        {
            foreach (StoredSessionContextDerivedCount counter in context.DerivedCounts)
            {
                ArgumentNullException.ThrowIfNull(counter);
                string counterValue = GuardedText("session_context_derived_count", "counter", counter.Counter);
                string countSessionIdParam = batch.Add(sessionId);
                string counterParam = batch.Add(counterValue);
                string countParam = batch.Add(counter.Count);
                batch.Append(
                    $"INSERT INTO session_context_derived_count (session_id, counter, count) VALUES ({countSessionIdParam}, {counterParam}, {countParam});\n");
            }
        }

        if (context.FlaggedTurnIndexes is not null)
        {
            foreach (int turnIndex in context.FlaggedTurnIndexes)
            {
                string flaggedSessionIdParam = batch.Add(sessionId);
                string turnIndexParam = batch.Add(turnIndex);
                batch.Append($"INSERT INTO session_context_flagged_turn (session_id, turn_index) VALUES ({flaggedSessionIdParam}, {turnIndexParam});\n");
            }
        }

        if (context.Turns is not null)
        {
            foreach (StoredSessionContextTurn turn in context.Turns)
            {
                ArgumentNullException.ThrowIfNull(turn);
                AppendSessionContextTurn(batch, sessionId, turn);
            }
        }
    }

    private static void AppendSessionContextTurn(SqlBatch batch, string sessionId, StoredSessionContextTurn turn)
    {
        string attributionValue = GuardedText("session_context_turn", "residual_attribution", turn.ResidualAttribution);

        string turnSessionIdParam = batch.Add(sessionId);
        string turnPositionParam = batch.Add(turn.Position);
        string inputTokensParam = batch.Add(turn.InputTokens);
        string bucketedTokensParam = batch.Add(turn.BucketedTokens);
        string residualTokensParam = batch.Add(turn.ResidualTokens);
        string attributionParam = batch.Add(attributionValue);
        string builtinTokensParam = batch.Add(turn.BuiltinToolDefinitionTokens);
        string freshInputParam = batch.Add(turn.FreshInput);
        batch.Append(
            $"INSERT INTO session_context_turn (session_id, position, input_tokens, bucketed_tokens, residual_tokens, residual_attribution, builtin_tool_definition_tokens, fresh_input) " +
            $"VALUES ({turnSessionIdParam}, {turnPositionParam}, {inputTokensParam}, {bucketedTokensParam}, {residualTokensParam}, {attributionParam}, {builtinTokensParam}, {freshInputParam});\n");

        RequireMatchedPair(turn.Headroom.HasValue, turn.Pressure.HasValue, "session_context_turn_headroom", "headroom", "pressure");
        if (turn.Headroom.HasValue && turn.Pressure.HasValue)
        {
            double pressure = RequireFiniteReal("session_context_turn_headroom", "pressure", turn.Pressure.Value);
            string headroomSessionIdParam = batch.Add(sessionId);
            string headroomPositionParam = batch.Add(turn.Position);
            string headroomParam = batch.Add(turn.Headroom.Value);
            string pressureParam = batch.Add(pressure);
            batch.Append(
                $"INSERT INTO session_context_turn_headroom (session_id, position, headroom, pressure) VALUES ({headroomSessionIdParam}, {headroomPositionParam}, {headroomParam}, {pressureParam});\n");
        }

        if (turn.AccumulationRate.HasValue)
        {
            double rate = RequireFiniteReal("session_context_turn_accumulation", "rate", turn.AccumulationRate.Value);
            string rateSessionIdParam = batch.Add(sessionId);
            string ratePositionParam = batch.Add(turn.Position);
            string rateParam = batch.Add(rate);
            batch.Append(
                $"INSERT INTO session_context_turn_accumulation (session_id, position, rate) VALUES ({rateSessionIdParam}, {ratePositionParam}, {rateParam});\n");
        }

        if (turn.Buckets is not null)
        {
            foreach (StoredTurnBucketTokens bucket in turn.Buckets)
            {
                ArgumentNullException.ThrowIfNull(bucket);
                string bucketValue = GuardedText("session_context_turn_bucket", "bucket", bucket.Bucket);
                string bucketSessionIdParam = batch.Add(sessionId);
                string bucketPositionParam = batch.Add(turn.Position);
                string bucketParam = batch.Add(bucketValue);
                string bucketTokensParam = batch.Add(bucket.Tokens);
                batch.Append(
                    $"INSERT INTO session_context_turn_bucket (session_id, position, bucket, tokens) VALUES ({bucketSessionIdParam}, {bucketPositionParam}, {bucketParam}, {bucketTokensParam});\n");
            }
        }

        if (turn.ServerDefinitions is not null)
        {
            foreach (StoredTurnServerDefinitionTokens server in turn.ServerDefinitions)
            {
                ArgumentNullException.ThrowIfNull(server);
                string serverValue = GuardedText("session_context_turn_server_definitions", "server", server.Server);
                string serverSessionIdParam = batch.Add(sessionId);
                string serverPositionParam = batch.Add(turn.Position);
                string serverParam = batch.Add(serverValue);
                string serverTokensParam = batch.Add(server.Tokens);
                batch.Append(
                    $"INSERT INTO session_context_turn_server_definitions (session_id, position, server, tokens) VALUES ({serverSessionIdParam}, {serverPositionParam}, {serverParam}, {serverTokensParam});\n");
            }
        }

        foreach (TokenizerLabel label in turn.Tokenizers)
        {
            ArgumentNullException.ThrowIfNull(label);
            string tokenizerValue = GuardedText("session_context_turn_tokenizer", "tokenizer", label.Tokenizer);
            string tokenizerSessionIdParam = batch.Add(sessionId);
            string tokenizerPositionParam = batch.Add(turn.Position);
            string tokenizerParam = batch.Add(tokenizerValue);
            string isModelOwnParam = batch.Add(label.IsModelOwn ? 1 : 0);
            batch.Append(
                $"INSERT INTO session_context_turn_tokenizer (session_id, position, tokenizer, is_model_own) VALUES ({tokenizerSessionIdParam}, {tokenizerPositionParam}, {tokenizerParam}, {isModelOwnParam});\n");
        }

        RequireMatchedPair(
            turn.StrippedBlockCount.HasValue,
            turn.StrippedTokens.HasValue,
            "session_context_turn_stripped_instructions",
            "block_count",
            "tokens");
        if (turn.StrippedBlockCount.HasValue && turn.StrippedTokens.HasValue)
        {
            string strippedSessionIdParam = batch.Add(sessionId);
            string strippedPositionParam = batch.Add(turn.Position);
            string blockCountParam = batch.Add(turn.StrippedBlockCount.Value);
            string strippedTokensParam = batch.Add(turn.StrippedTokens.Value);
            batch.Append(
                $"INSERT INTO session_context_turn_stripped_instructions (session_id, position, block_count, tokens) VALUES ({strippedSessionIdParam}, {strippedPositionParam}, {blockCountParam}, {strippedTokensParam});\n");
        }

        RequireMatchedPair(
            turn.PreviousFreshInput.HasValue,
            turn.FreshJumpFactor.HasValue,
            "session_context_turn_fresh_jump",
            "previous_fresh_input",
            "factor");
        if (turn.PreviousFreshInput.HasValue && turn.FreshJumpFactor.HasValue)
        {
            double factor = RequireFiniteReal("session_context_turn_fresh_jump", "factor", turn.FreshJumpFactor.Value);
            string jumpSessionIdParam = batch.Add(sessionId);
            string jumpPositionParam = batch.Add(turn.Position);
            string previousFreshInputParam = batch.Add(turn.PreviousFreshInput.Value);
            string factorParam = batch.Add(factor);
            batch.Append(
                $"INSERT INTO session_context_turn_fresh_jump (session_id, position, previous_fresh_input, factor) VALUES ({jumpSessionIdParam}, {jumpPositionParam}, {previousFreshInputParam}, {factorParam});\n");
        }

        if (turn.NonResidentTokens.HasValue)
        {
            string nonResidentSessionIdParam = batch.Add(sessionId);
            string nonResidentPositionParam = batch.Add(turn.Position);
            string nonResidentTokensParam = batch.Add(turn.NonResidentTokens.Value);
            batch.Append(
                $"INSERT INTO session_context_turn_non_resident (session_id, position, tokens) VALUES ({nonResidentSessionIdParam}, {nonResidentPositionParam}, {nonResidentTokensParam});\n");
        }
    }

    private static void AppendSessionSchema(SqlBatch batch, string sessionId, StoredSessionSchema schema)
    {
        string schemaSessionIdParam = batch.Add(sessionId);
        string invocationCountParam = batch.Add(schema.InvocationCount);
        string turnCountParam = batch.Add(schema.TurnCount);
        batch.Append(
            $"INSERT INTO session_schema (session_id, invocation_count, turn_count) VALUES ({schemaSessionIdParam}, {invocationCountParam}, {turnCountParam});\n");

        if (schema.RankedTools is not null)
        {
            foreach (StoredSchemaRankedTool tool in schema.RankedTools)
            {
                ArgumentNullException.ThrowIfNull(tool);
                string toolNameValue = GuardedText("session_schema_ranked_tool", "tool_name", tool.ToolName);
                string rankedSessionIdParam = batch.Add(sessionId);
                string rankedPositionParam = batch.Add(tool.Position);
                string rankedToolNameParam = batch.Add(toolNameValue);
                string costTokensParam = batch.Add(tool.CostTokens);
                string invokedParam = batch.Add(tool.Invoked ? 1 : 0);
                batch.Append(
                    $"INSERT INTO session_schema_ranked_tool (session_id, position, tool_name, cost_tokens, invoked) VALUES ({rankedSessionIdParam}, {rankedPositionParam}, {rankedToolNameParam}, {costTokensParam}, {invokedParam});\n");

                if (tool.ServerName is not null)
                {
                    string serverValue = GuardedText("session_schema_ranked_tool_server", "server_name", tool.ServerName);
                    string serverSessionIdParam = batch.Add(sessionId);
                    string serverPositionParam = batch.Add(tool.Position);
                    string serverParam = batch.Add(serverValue);
                    batch.Append(
                        $"INSERT INTO session_schema_ranked_tool_server (session_id, position, server_name) VALUES ({serverSessionIdParam}, {serverPositionParam}, {serverParam});\n");
                }
            }
        }

        if (schema.Servers is not null)
        {
            foreach (StoredSchemaServerTokens server in schema.Servers)
            {
                ArgumentNullException.ThrowIfNull(server);
                string serverValue = GuardedText("session_schema_server", "server_name", server.ServerName);
                string schemaServerSessionIdParam = batch.Add(sessionId);
                string serverNameParam = batch.Add(serverValue);
                string serverTokensParam = batch.Add(server.Tokens);
                batch.Append(
                    $"INSERT INTO session_schema_server (session_id, server_name, tokens) VALUES ({schemaServerSessionIdParam}, {serverNameParam}, {serverTokensParam});\n");
            }
        }

        AppendOptionalLongChild(batch, "session_schema_unused", "session_id", sessionId, "token_residencies", schema.UnusedTokenResidencies);
    }

    private static void AppendRunUpsert(SqlBatch batch, StoredRun run)
    {
        RequireMatchedPair(run.StartedAtUtc is not null, run.EndedAtUtc is not null, "run_bounds", "started_at_utc", "ended_at_utc");

        string runId = GuardedText("run", "run_id", run.RunId);
        string harnessId = GuardedText("run", "harness_id", run.HarnessId);
        string basis = GuardedText("run", "grouping_basis", run.GroupingBasis);
        string rule = GuardedText("run", "grouping_rule", run.GroupingRule);

        string insertRunIdParam = batch.Add(runId);
        string insertHarnessIdParam = batch.Add(harnessId);
        string basisParam = batch.Add(basis);
        string ruleParam = batch.Add(rule);
        string executionCountParam = batch.Add(run.ExecutionCount);
        batch.Append(
            $"INSERT INTO run (run_id, harness_id, grouping_basis, grouping_rule, execution_count) VALUES ({insertRunIdParam}, {insertHarnessIdParam}, {basisParam}, {ruleParam}, {executionCountParam}) ");
        batch.Append("ON CONFLICT(run_id) DO UPDATE SET harness_id = excluded.harness_id, grouping_basis = excluded.grouping_basis, grouping_rule = excluded.grouping_rule, execution_count = excluded.execution_count;\n");

        string deleteLabelRunIdParam = batch.Add(runId);
        batch.Append($"DELETE FROM run_label WHERE run_id = {deleteLabelRunIdParam};\n");
        string deleteWorkingDirectoryRunIdParam = batch.Add(runId);
        batch.Append($"DELETE FROM run_working_directory WHERE run_id = {deleteWorkingDirectoryRunIdParam};\n");
        string deleteBoundsRunIdParam = batch.Add(runId);
        batch.Append($"DELETE FROM run_bounds WHERE run_id = {deleteBoundsRunIdParam};\n");

        AppendOptionalTextChild(batch, "run_label", "run_id", runId, "label", run.Label);
        AppendOptionalTextChild(batch, "run_working_directory", "run_id", runId, "working_directory", run.WorkingDirectory);

        if (run.StartedAtUtc is not null && run.EndedAtUtc is not null)
        {
            string started = GuardedText("run_bounds", "started_at_utc", run.StartedAtUtc);
            string ended = GuardedText("run_bounds", "ended_at_utc", run.EndedAtUtc);
            string boundsRunIdParam = batch.Add(runId);
            string startedParam = batch.Add(started);
            string endedParam = batch.Add(ended);
            batch.Append($"INSERT INTO run_bounds (run_id, started_at_utc, ended_at_utc) VALUES ({boundsRunIdParam}, {startedParam}, {endedParam});\n");
        }
    }

    private static void AppendHarnessRollupReplace(SqlBatch batch, StoredHarnessRollup rollup)
    {
        string harnessId = GuardedText("harness_rollup", "harness_id", rollup.HarnessId);

        string deleteHarnessIdParam = batch.Add(harnessId);
        batch.Append($"DELETE FROM harness_rollup WHERE harness_id = {deleteHarnessIdParam};\n");

        string insertHarnessIdParam = batch.Add(harnessId);
        string sessionCountParam = batch.Add(rollup.SessionCount);
        string runCountParam = batch.Add(rollup.RunCount);
        string executionCountParam = batch.Add(rollup.ExecutionCount);
        string unknownWindowSessionsParam = batch.Add(rollup.UnknownWindowSessions);
        string windowSessionsTotalParam = batch.Add(rollup.WindowSessionsTotal);
        batch.Append(
            $"INSERT INTO harness_rollup (harness_id, session_count, run_count, execution_count, unknown_window_sessions, window_sessions_total) " +
            $"VALUES ({insertHarnessIdParam}, {sessionCountParam}, {runCountParam}, {executionCountParam}, {unknownWindowSessionsParam}, {windowSessionsTotalParam});\n");

        if (rollup.Metrics is not null)
        {
            foreach (StoredHarnessRollupMetric metric in rollup.Metrics)
            {
                ArgumentNullException.ThrowIfNull(metric);
                string metricValue = GuardedText("harness_rollup_metric", "metric", metric.Metric);
                double value = RequireFiniteReal("harness_rollup_metric", "value", metric.Value);
                string basisValue = GuardedText("harness_rollup_metric", "basis", metric.Basis);
                string metricHarnessIdParam = batch.Add(harnessId);
                string metricParam = batch.Add(metricValue);
                string valueParam = batch.Add(value);
                string basisParam = batch.Add(basisValue);
                batch.Append(
                    $"INSERT INTO harness_rollup_metric (harness_id, metric, value, basis) VALUES ({metricHarnessIdParam}, {metricParam}, {valueParam}, {basisParam});\n");
            }
        }
    }

    private static void AppendFindingReplace(SqlBatch batch, StoredFinding finding)
    {
        string findingId = GuardedText("finding", "finding_id", finding.FindingId);
        string detectorId = GuardedText("finding", "detector_id", finding.DetectorId);
        string title = GuardedText("finding", "title", finding.Title);
        string mechanism = GuardedText("finding", "mechanism", finding.Mechanism);
        string confidence = GuardedText("finding", "confidence", finding.Confidence);
        string recommendation = GuardedText("finding", "recommendation", finding.Recommendation);
        string caveat = GuardedText("finding", "outcome_risk_caveat", finding.OutcomeRiskCaveat);
        double rankScore = RequireFiniteReal("finding", "rank_score", finding.RankScore);

        string deleteFindingIdParam = batch.Add(findingId);
        batch.Append($"DELETE FROM finding WHERE finding_id = {deleteFindingIdParam};\n");

        string insertFindingIdParam = batch.Add(findingId);
        string detectorIdParam = batch.Add(detectorId);
        string titleParam = batch.Add(title);
        string mechanismParam = batch.Add(mechanism);
        string confidenceParam = batch.Add(confidence);
        string recommendationParam = batch.Add(recommendation);
        string caveatParam = batch.Add(caveat);
        string rankScoreParam = batch.Add(rankScore);
        batch.Append(
            $"INSERT INTO finding (finding_id, detector_id, title, mechanism, confidence, recommendation, outcome_risk_caveat, rank_score) " +
            $"VALUES ({insertFindingIdParam}, {detectorIdParam}, {titleParam}, {mechanismParam}, {confidenceParam}, {recommendationParam}, {caveatParam}, {rankScoreParam});\n");

        AppendOptionalLongChild(batch, "finding_estimated_waste", "finding_id", findingId, "tokens", finding.EstimatedWasteTokens);

        if (finding.ErrorBar is not null)
        {
            double lower = RequireFiniteReal("finding_error_bar", "lower", finding.ErrorBar.Lower);
            double upper = RequireFiniteReal("finding_error_bar", "upper", finding.ErrorBar.Upper);
            string errorBarFindingIdParam = batch.Add(findingId);
            string lowerParam = batch.Add(lower);
            string upperParam = batch.Add(upper);
            batch.Append($"INSERT INTO finding_error_bar (finding_id, lower, upper) VALUES ({errorBarFindingIdParam}, {lowerParam}, {upperParam});\n");
        }

        if (finding.EvidenceLinks is not null)
        {
            for (int i = 0; i < finding.EvidenceLinks.Count; i++)
            {
                FindingEvidenceLink link = finding.EvidenceLinks[i];
                ArgumentNullException.ThrowIfNull(link);
                string spanIdValue = GuardedText("finding_evidence_link", "span_id", link.SpanId);
                string descriptionValue = GuardedText("finding_evidence_link", "description", link.Description);
                string linkFindingIdParam = batch.Add(findingId);
                string positionParam = batch.Add(i);
                string spanIdParam = batch.Add(spanIdValue);
                string turnIndexParam = batch.Add(link.TurnIndex);
                string descriptionParam = batch.Add(descriptionValue);
                batch.Append(
                    $"INSERT INTO finding_evidence_link (finding_id, position, span_id, turn_index, description) VALUES ({linkFindingIdParam}, {positionParam}, {spanIdParam}, {turnIndexParam}, {descriptionParam});\n");
            }
        }

        AppendOptionalTextChild(batch, "finding_run", "finding_id", findingId, "run_id", finding.RunId);
        AppendOptionalTextChild(batch, "finding_session", "finding_id", findingId, "session_id", finding.SessionId);
        AppendOptionalTextChild(batch, "finding_measurement_class", "finding_id", findingId, "measurement_class", finding.MeasurementClass);
        AppendOptionalTextChild(batch, "finding_confidence_basis", "finding_id", findingId, "confidence_basis", finding.ConfidenceBasis);
        AppendOptionalTextChild(batch, "finding_what_would_raise_it", "finding_id", findingId, "text", finding.WhatWouldRaiseIt);

        if (finding.CompactionHazard is not null)
        {
            string sourceValue = GuardedText("finding_compaction_hazard", "context_limit_source", finding.CompactionHazard.ContextLimitSource);
            string hazardFindingIdParam = batch.Add(findingId);
            string contextLimitParam = batch.Add(finding.CompactionHazard.ContextLimit);
            string sourceParam = batch.Add(sourceValue);
            batch.Append(
                $"INSERT INTO finding_compaction_hazard (finding_id, context_limit, context_limit_source) VALUES ({hazardFindingIdParam}, {contextLimitParam}, {sourceParam});\n");
        }
    }

    private static void AppendPredictionReplace(SqlBatch batch, StoredPrediction prediction)
    {
        string predictionId = GuardedText("prediction", "prediction_id", prediction.PredictionId);
        string findingId = GuardedText("prediction", "finding_id", prediction.FindingId);
        string runId = GuardedText("prediction", "run_id", prediction.RunId);
        double confidence = RequireFiniteReal("prediction", "confidence", prediction.Confidence);
        string tier = GuardedText("prediction", "confidence_tier", prediction.ConfidenceTier);
        string created = GuardedText("prediction", "created_at_utc", prediction.CreatedAtUtc);

        string deletePredictionIdParam = batch.Add(predictionId);
        batch.Append($"DELETE FROM prediction WHERE prediction_id = {deletePredictionIdParam};\n");

        string insertPredictionIdParam = batch.Add(predictionId);
        string findingIdParam = batch.Add(findingId);
        string runIdParam = batch.Add(runId);
        string predictedWasteTokensParam = batch.Add(prediction.PredictedWasteTokens);
        string confidenceParam = batch.Add(confidence);
        string tierParam = batch.Add(tier);
        string createdParam = batch.Add(created);
        batch.Append(
            $"INSERT INTO prediction (prediction_id, finding_id, run_id, predicted_waste_tokens, confidence, confidence_tier, created_at_utc) " +
            $"VALUES ({insertPredictionIdParam}, {findingIdParam}, {runIdParam}, {predictedWasteTokensParam}, {confidenceParam}, {tierParam}, {createdParam});\n");

        AppendOptionalTextChild(batch, "prediction_comparison", "prediction_id", predictionId, "comparison_run_id", prediction.ComparisonRunId);
        AppendOptionalLongChild(batch, "prediction_observed_delta", "prediction_id", predictionId, "tokens", prediction.ObservedDeltaTokens);

        if (prediction.CalibrationScore.HasValue)
        {
            double score = RequireFiniteReal("prediction_calibration", "score", prediction.CalibrationScore.Value);
            string calibrationPredictionIdParam = batch.Add(predictionId);
            string scoreParam = batch.Add(score);
            batch.Append($"INSERT INTO prediction_calibration (prediction_id, score) VALUES ({calibrationPredictionIdParam}, {scoreParam});\n");
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
